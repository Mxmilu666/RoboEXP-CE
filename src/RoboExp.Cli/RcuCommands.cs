using System.Collections.Concurrent;
using System.Formats.Nrbf;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using RoboExp.Core.Model;
using RoboExp.Core.Rcu;

namespace RoboExp.Cli;

/// <summary>
/// <c>roboexp rcu dump|code|roundtrip|gen|gencheck|compile</c>: inspect .rcu project files, verify the reader/writer,
/// regenerate and compile projects.
/// </summary>
internal static partial class RcuCommands
{
    private const string Usage = """
        usage: roboexp rcu dump <file.rcu>                    JSON of the project model (images summarised)
               roboexp rcu code <file.rcu> [--global|--func|--code]
                                                              stored C source (default: AllCode)
               roboexp rcu roundtrip <dir> [--max-diffs N]    read -> write -> decode every .rcu/.bak under <dir>
               roboexp rcu gen <file.rcu> [--type T]          regenerated C (<name>.c content)
               roboexp rcu gencheck <dir> [--max-diffs N] [--verbose]
                                                              regenerated vs stored AllCode for every flow .rcu (--verbose lists matches)
               roboexp rcu compile <file.rcu|dir> [--type T] [--verbose]
                                                              save a temp copy (regenerated .c + HardwareInfo.c) and build
        """;

    public static int Run(string[] args)
    {
        if (args.Length < 2) return Error(Usage);
        try
        {
            return args[0] switch
            {
                "dump" when args.Length == 2 => Dump(args[1]),
                "code" => Code(args[1], args.Length > 2 ? args[2] : null),
                "roundtrip" => RoundTrip(args[1], args.Length == 4 && args[2] == "--max-diffs" ? int.Parse(args[3]) : 10),
                "gen" => Gen(args[1..]),
                "gencheck" => GenCheck(args[1..]),
                "compile" => Compile(args[1..]),
                _ => Error(Usage),
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
        {
            return Error(ex.Message);
        }
    }

    private static int Error(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    /// <summary>
    /// Writes the model as a flat object table: <c>{"objects":[{"$id":1,"$type":"RcuFile",...},...]}</c> in
    /// breadth-first order from the root; references between model objects are <c>{"$ref":n}</c>. Flat because the
    /// flowchart graph is cyclic and arbitrarily deep (Port -> ConnectedPort -> Parent -> ...). Images are summarised.
    /// </summary>
    private static int Dump(string path)
    {
        var file = RcuReader.Read(path);
        using var stdout = Console.OpenStandardOutput();
        using (var json = new Utf8JsonWriter(stdout, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            new ModelDumper(json).Dump(file);
        }
        stdout.WriteByte((byte)'\n');
        return 0;
    }

    private sealed class ModelDumper(Utf8JsonWriter json)
    {
        private readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
        private readonly Queue<object> _queue = new();

        public void Dump(RcuFile file)
        {
            json.WriteStartObject();
            json.WriteStartArray("objects");
            Ref(file);
            while (_queue.TryDequeue(out var o)) WriteObject(o);
            json.WriteEndArray();
            json.WriteEndObject();
        }

        private int Ref(object o)
        {
            if (_ids.TryGetValue(o, out var id)) return id;
            _ids[o] = id = _ids.Count + 1;
            _queue.Enqueue(o);
            return id;
        }

        private void WriteObject(object o)
        {
            json.WriteStartObject();
            json.WriteNumber("$id", _ids[o]);
            json.WriteString("$type", o.GetType().Name);
            switch (o)
            {
                case SerializedDelegate d:
                    WriteMembers(d.Members);
                    break;
                case UnknownObject u:
                    json.WriteString("TypeName", u.TypeName);
                    json.WriteString("AssemblyName", u.AssemblyName);
                    WriteMembers(u.Members);
                    break;
                default:
                    foreach (var p in o.GetType().GetProperties())
                    {
                        if (!p.CanWrite || p.GetIndexParameters().Length != 0) continue;
                        var value = p.GetValue(o);
                        if (value == null) continue;
                        json.WritePropertyName(p.Name);
                        WriteValue(value);
                    }
                    break;
            }
            json.WriteEndObject();
        }

        private void WriteMembers(IEnumerable<KeyValuePair<string, object?>> members)
        {
            json.WriteStartObject("Members");
            foreach (var (name, value) in members)
            {
                json.WritePropertyName(name);
                WriteValue(value);
            }
            json.WriteEndObject();
        }

        private void WriteValue(object? value)
        {
            switch (value)
            {
                case null: json.WriteNullValue(); break;
                case string s: json.WriteStringValue(s); break;
                case bool b: json.WriteBooleanValue(b); break;
                case Enum e: json.WriteStringValue(e.ToString()); break;
                case int or short or long or byte: json.WriteNumberValue(Convert.ToInt64(value)); break;
                case float f: json.WriteNumberValue(f); break;
                case double d: json.WriteNumberValue(d); break;
                case Point p:
                    json.WriteStartObject();
                    json.WriteNumber("X", p.X);
                    json.WriteNumber("Y", p.Y);
                    json.WriteEndObject();
                    break;
                case Size sz:
                    json.WriteStartObject();
                    json.WriteNumber("Width", sz.Width);
                    json.WriteNumber("Height", sz.Height);
                    json.WriteEndObject();
                    break;
                case Color c:
                    json.WriteStartObject();
                    if (c.Name != null) json.WriteString("Name", c.Name);
                    json.WriteNumber("Value", c.Value);
                    json.WriteNumber("KnownColor", c.KnownColor);
                    json.WriteNumber("State", c.State);
                    json.WriteString("Argb", $"#{(uint)c.ToArgb():X8}");
                    json.WriteEndObject();
                    break;
                case EncodedImage image:
                    json.WriteStartObject();
                    json.WriteString("Format", image.Format.ToString());
                    if (image.PixelSize is { } px)
                    {
                        json.WriteNumber("Width", px.Width);
                        json.WriteNumber("Height", px.Height);
                    }
                    json.WriteNumber("Bytes", image.Data.Length);
                    json.WriteEndObject();
                    break;
                case RcuAssemblyNames names:
                    json.WriteStartObject();
                    json.WriteString("RoboExp", names.RoboExp);
                    json.WriteString("SoftType", names.SoftType);
                    json.WriteString("SystemDrawing", names.SystemDrawing);
                    json.WriteEndObject();
                    break;
                case RcuObject or SerializedDelegate or SerializedDelegateEntry or UnknownObject:
                    json.WriteStartObject();
                    json.WriteNumber("$ref", Ref(value));
                    json.WriteEndObject();
                    break;
                case IDictionary<string, object?> dict:
                    WriteMembers(dict);
                    break;
                case System.Collections.IEnumerable items:
                    json.WriteStartArray();
                    foreach (var item in items) WriteValue(item);
                    json.WriteEndArray();
                    break;
                default:
                    json.WriteStringValue(value.ToString());
                    break;
            }
        }
    }

    private static int Code(string path, string? part)
    {
        var frame = RcuReader.Read(path).ProjectData?.CodeFrameData;
        if (frame == null) return Error($"{path}: no CodeFrameData");
        var text = part switch
        {
            null => frame.AllCode,
            "--global" => frame.Global,
            "--func" => frame.Func,
            "--code" => frame.Code,
            _ => null,
        };
        if (part != null && part is not ("--global" or "--func" or "--code")) return Error(Usage);
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        stdout.Write(text ?? "");
        if (text is { Length: > 0 } && !text.EndsWith('\n')) stdout.WriteLine();
        return 0;
    }

    private sealed record FileResult(string Path, bool Rcu, bool Read, bool Equivalent, bool Identical, string? Difference);

    private static int RoundTrip(string dir, int maxDiffs)
    {
        if (!Directory.Exists(dir)) return Error($"not a directory: {dir}");
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".rcu", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
        var results = new ConcurrentBag<FileResult>();
        Parallel.ForEach(files, f => results.Add(RoundTripFile(f)));

        var failures = 0;
        foreach (var group in results.GroupBy(r => Path.GetExtension(r.Path).ToLowerInvariant()).OrderBy(g => g.Key))
        {
            var rcu = group.Where(r => r.Rcu).ToList();
            Console.WriteLine($"{group.Key}: {group.Count()} files, {rcu.Count} rcu-format, read {rcu.Count(r => r.Read)}, " +
                              $"graph-equivalent {rcu.Count(r => r.Equivalent)}, byte-identical {rcu.Count(r => r.Identical)}");
            failures += rcu.Count(r => !r.Equivalent);
        }

        var shown = 0;
        foreach (var r in results.Where(r => r.Rcu && !r.Identical).OrderBy(r => r.Equivalent).ThenBy(r => r.Path, StringComparer.Ordinal))
        {
            if (shown++ == maxDiffs) break;
            Console.WriteLine($"{(r.Equivalent ? "NOT BYTE-IDENTICAL" : r.Read ? "DIFFERENT" : "READ FAILED")} {Path.GetRelativePath(dir, r.Path)}: {r.Difference}");
        }
        return failures == 0 ? 0 : 1;
    }

    private static FileResult RoundTripFile(string path)
    {
        var original = File.ReadAllBytes(path);
        if (original.Length == 0 || !NrbfDecoder.StartsWithPayloadHeader(original))
            return new FileResult(path, false, false, false, false, "not NRBF");
        RcuFile file;
        try
        {
            file = RcuReader.Read(new MemoryStream(original, false));
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            return new FileResult(path, true, false, false, false, ex.Message);
        }
        var written = new MemoryStream();
        try
        {
            RcuWriter.Write(written, file);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            return new FileResult(path, true, true, false, false, "write failed: " + ex.Message);
        }
        var bytes = written.ToArray();
        if (bytes.AsSpan().SequenceEqual(original)) return new FileResult(path, true, true, true, true, null);
        var diff = NrbfGraphComparer.FindFirstDifference(original, bytes);
        if (diff != null) return new FileResult(path, true, true, false, false, diff);
        var offset = original.AsSpan().CommonPrefixLength(bytes);
        return new FileResult(path, true, true, true, false, $"first byte difference at offset {offset} (sizes {original.Length} / {bytes.Length})");
    }
}
