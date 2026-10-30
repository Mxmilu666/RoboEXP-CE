using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace RoboExp.Core.Install.Inno;

/// <summary>One output file: the innoextract-style relative path ('/'-separated, e.g. "app/Robots/…") and its data location.</summary>
public sealed record InnoPlannedFile(string Path, InnoFileEntry Entry);

/// <summary>What an extraction produces: every directory (including empty <c>[Dirs]</c> entries) and file.</summary>
public sealed record InnoExtractionPlan(IReadOnlyList<string> Directories, IReadOnlyList<InnoPlannedFile> Files);

public sealed record InnoExtractProgress(long BytesDone, long BytesTotal, int FilesDone, int FilesTotal);

/// <summary>
/// Extracts an Inno Setup installer read by <see cref="InnoSetupInfo"/>, reproducing the output layout of
/// <c>innoextract -d &lt;dir&gt; setup.exe</c> (1.9, default options): constants such as <c>{app}</c> become
/// top-level directories, unsafe characters become '$', all languages and temporary files are included,
/// colliding destinations are resolved with innoextract's overwrite rules and directory-name case follows the
/// first occurrence. File data is verified against the stored SHA-1 checksums; mtimes are set from the stored
/// timestamps (UTC, like innoextract's default <c>--timestamps UTC</c>).
/// </summary>
public static class InnoExtractor
{
    public static InnoExtractionPlan Plan(InnoSetupInfo info)
    {
        var dirs = new SortedDictionary<string, DirInfo>(StringComparer.Ordinal);
        foreach (var directory in info.Directories)
        {
            var path = ConvertPath(directory.Name);
            if (path.Length == 0) continue;
            var key = AsciiLower(path);
            InsertDirs(dirs, key, ref path);
            dirs.TryAdd(key, new DirInfo(path));
        }

        var files = new Dictionary<string, (InnoFileEntry Entry, string Path)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var file in info.Files)
        {
            if (file.Location >= info.DataEntries.Count) continue; // external file (copy command)
            var path = ConvertPath(file.Destination);
            if (path.Length == 0) continue;
            var key = AsciiLower(path);
            InsertDirs(dirs, key, ref path);
            if (files.TryGetValue(key, out var existing))
            {
                var keepOld = CollisionKeepsOld(existing.Entry, info.DataEntries[(int)existing.Entry.Location],
                    file, info.DataEntries[(int)file.Location]);
                if (!keepOld)
                    files[key] = (file, file.Type != InnoFileEntry.UninstExe ? path : existing.Path);
            }
            else
            {
                files[key] = (file, path);
                order.Add(key);
            }
        }

        return new InnoExtractionPlan(
            dirs.Values.Select(d => d.Path).ToList(),
            order.Select(k => new InnoPlannedFile(files[k].Path, files[k].Entry)).ToList());
    }

    /// <summary>Writes the plan's directories and files under <paramref name="outputDir"/>.</summary>
    /// <exception cref="UnsupportedInstallerException">Encrypted chunks or bzip2 compression.</exception>
    /// <exception cref="InvalidDataException">Corrupt data or checksum mismatch.</exception>
    public static void Extract(Stream exe, InnoSetupInfo info, InnoExtractionPlan plan, string outputDir,
        IProgress<InnoExtractProgress>? progress = null, CancellationToken ct = default)
    {
        var outputsByLocation = plan.Files.GroupBy(f => (int)f.Entry.Location)
            .ToDictionary(g => g.Key, g => g.Select(f => f.Path).ToList());
        var locations = outputsByLocation.Keys.Select(i => (Index: i, Data: info.DataEntries[i])).ToList();
        foreach (var (_, d) in locations)
        {
            if ((d.Flags & InnoDataEntry.ChunkEncrypted) != 0)
                throw new UnsupportedInstallerException("Installer uses encrypted (password-protected) files");
            if (d.FirstSlice != 0)
                throw new UnsupportedInstallerException("Installer spans multiple disk slices");
        }
        var compression = info.Compression;
        if (compression == InnoCompression.BZip2 && locations.Any(l => (l.Data.Flags & InnoDataEntry.ChunkCompressed) != 0))
            throw new UnsupportedInstallerException("bzip2-compressed installers are not supported by the built-in extractor");

        var root = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(root);
        foreach (var dir in plan.Directories) Directory.CreateDirectory(OsPath(root, dir));

        long total = locations.Sum(l => (long)l.Data.FileSize), done = 0;
        int filesDone = 0, filesTotal = plan.Files.Count;
        var buffer = new byte[1 << 16];
        var filtered = new byte[buffer.Length + 4];
        Span<byte> magic = stackalloc byte[4];

        var chunks = locations
            .GroupBy(l => (l.Data.ChunkOffset, l.Data.ChunkSize, Compressed: (l.Data.Flags & InnoDataEntry.ChunkCompressed) != 0))
            .OrderBy(g => g.Key.ChunkOffset);
        foreach (var chunk in chunks)
        {
            exe.Position = info.DataOffset + chunk.Key.ChunkOffset;
            exe.ReadExactly(magic);
            if (!magic.SequenceEqual("zlb\x1a"u8)) throw new InvalidDataException("Bad chunk signature in installer data");
            using var raw = new SubStream(exe, (long)chunk.Key.ChunkSize);
            using Stream source = !chunk.Key.Compressed ? raw : compression switch
            {
                InnoCompression.Stored => raw,
                InnoCompression.Zlib => new ZLibStream(raw, CompressionMode.Decompress),
                InnoCompression.Lzma1 => new InnoLzma1Stream(raw),
                InnoCompression.Lzma2 => new InnoLzma2Stream(raw),
                _ => throw new UnsupportedInstallerException($"Unsupported chunk compression {compression}"),
            };

            ulong position = 0;
            foreach (var (index, data) in chunk.OrderBy(l => l.Data.FileOffset))
            {
                ct.ThrowIfCancellationRequested();
                if (data.FileOffset < position) throw new InvalidDataException("Overlapping files in installer chunk");
                Skip(source, data.FileOffset - position, buffer);
                var paths = outputsByLocation[index];
                var first = OsPath(root, paths[0]);
                var filter = (data.Flags & InnoDataEntry.CallInstructionOptimized) != 0
                    ? new CallInstructionFilter(info.Version.AtLeast(5, 3, 9)) : null;
                using (var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
                using (var output = new FileStream(first, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    var left = data.FileSize;
                    while (left > 0)
                    {
                        var want = (int)Math.Min((ulong)buffer.Length, left);
                        source.ReadExactly(buffer, 0, want);
                        left -= (ulong)want;
                        ReadOnlySpan<byte> chunkOut = buffer.AsSpan(0, want);
                        if (filter != null)
                        {
                            var n = filter.Process(chunkOut, filtered);
                            if (left == 0) n += filter.Flush(filtered.AsSpan(n));
                            chunkOut = filtered.AsSpan(0, n);
                        }
                        sha1.AppendData(chunkOut);
                        output.Write(chunkOut);
                        done += want;
                        progress?.Report(new InnoExtractProgress(done, total, filesDone, filesTotal));
                    }
                    if (!sha1.GetHashAndReset().AsSpan().SequenceEqual(data.Sha1))
                        throw new InvalidDataException($"Checksum mismatch for {paths[0]}");
                }
                position = data.FileOffset + data.FileSize;
                File.SetLastWriteTimeUtc(first, data.TimestampUtc);
                foreach (var other in paths.Skip(1))
                {
                    var dest = OsPath(root, other);
                    File.Copy(first, dest, overwrite: true);
                    File.SetLastWriteTimeUtc(dest, data.TimestampUtc);
                }
                filesDone += paths.Count;
                progress?.Report(new InnoExtractProgress(done, total, filesDone, filesTotal));
            }
        }
    }

    private static void Skip(Stream s, ulong count, byte[] buffer)
    {
        while (count > 0)
        {
            var c = (int)Math.Min((ulong)buffer.Length, count);
            s.ReadExactly(buffer, 0, c);
            count -= (ulong)c;
        }
    }

    private static string OsPath(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    // ---- innoextract-compatible path handling (setup/filename.cpp, cli/extract.cpp) --------------------------------

    private sealed class DirInfo(string path)
    {
        public string Path = path;
    }

    /// <summary>Registers all parent directories of <paramref name="key"/>; rewrites <paramref name="path"/>'s directory
    /// part to the case of an already registered directory (first occurrence wins).</summary>
    private static bool InsertDirs(SortedDictionary<string, DirInfo> dirs, string key, ref string path)
    {
        var dir = Parent(path);
        var keyDir = Parent(key);
        if (keyDir.Length == 0) return false;
        if (dirs.TryGetValue(keyDir, out var existing))
        {
            if (existing.Path == dir) return false;
            path = existing.Path + path[dir.Length..];
            return true;
        }
        var inserted = new DirInfo(dir);
        dirs.Add(keyDir, inserted);
        var oldLength = dir.Length;
        if (InsertDirs(dirs, keyDir, ref dir))
        {
            path = dir + path[oldLength..];
            inserted.Path = dir;
            return true;
        }
        return false;
    }

    private static string Parent(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

    /// <summary>Lower-cases ASCII only (innoextract uses the C locale on UTF-8 bytes).</summary>
    private static string AsciiLower(string s) => string.Create(s.Length, s, (span, src) =>
    {
        for (var i = 0; i < src.Length; i++) span[i] = src[i] is >= 'A' and <= 'Z' ? (char)(src[i] + 32) : src[i];
    });

    /// <summary>Expands <c>{constants}</c> to their names, replaces unsafe characters and normalizes separators/dots.</summary>
    internal static string ConvertPath(string destination)
    {
        var i = 0;
        return ShortenPath(ExpandVariables(destination, ref i, close: false));
    }

    private static bool IsUnsafe(char c) => c < 32 || c is '<' or '>' or ':' or '"' or '|' or '?' or '*';

    private static string ReplaceUnsafe(string s)
    {
        var chars = s.ToCharArray();
        for (var k = 0; k < chars.Length; k++) if (IsUnsafe(chars[k])) chars[k] = '$';
        return new string(chars);
    }

    private static string ExpandVariables(string s, ref int i, bool close)
    {
        var sb = new StringBuilder(s.Length - i);
        while (i < s.Length)
        {
            var pos = i;
            while (pos < s.Length && s[pos] != '{' && s[pos] != '}') pos++;
            sb.Append(ReplaceUnsafe(s[i..pos]));
            i = pos;
            if (pos == s.Length) break;
            i++;
            if (close && s[pos] == '}') break;
            if (!close && s[pos] == '}') { sb.Append('}'); continue; }
            if (i < s.Length && s[i] == '{') { sb.Append('{'); i++; continue; }
            sb.Append(ReplaceUnsafe(ExpandVariables(s, ref i, close: true)));
        }
        return sb.ToString();
    }

    private static string ShortenPath(string path)
    {
        var result = new StringBuilder(path.Length);
        foreach (var segment in path.Split('\\', '/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                var current = result.ToString();
                var last = current.LastIndexOf('/');
                result.Length = last < 0 ? 0 : last;
                continue;
            }
            if (result.Length > 0) result.Append('/');
            result.Append(segment);
        }
        return result.ToString();
    }

    /// <summary>innoextract's <c>handle_collision</c> with the user always agreeing to overwrite: true keeps the old file.</summary>
    private static bool CollisionKeepsOld(InnoFileEntry oldFile, InnoDataEntry oldData, InnoFileEntry newFile, InnoDataEntry newData)
    {
        var allowTimestamp = true;
        if ((newFile.Options & InnoFileEntry.IgnoreVersion) == 0)
        {
            var newValid = (newData.Flags & InnoDataEntry.VersionInfoValid) != 0;
            if ((oldData.Flags & InnoDataEntry.VersionInfoValid) != 0)
            {
                allowTimestamp = false;
                if (!newValid || oldData.FileVersion > newData.FileVersion)
                {
                    if ((newFile.Options & InnoFileEntry.PromptIfOlder) == 0) return true; // old version
                }
                else if (newData.FileVersion == oldData.FileVersion && (newFile.Options & InnoFileEntry.OverwriteSameVersion) == 0)
                {
                    if ((newFile.Options & InnoFileEntry.ReplaceSameVersionIfContentsDiffer) != 0 && oldData.Sha1.AsSpan().SequenceEqual(newData.Sha1))
                        return true; // duplicate (checksum)
                    if ((newFile.Options & InnoFileEntry.CompareTimeStamp) == 0) return true; // duplicate (version)
                    allowTimestamp = true;
                }
            }
            else if (newValid)
            {
                allowTimestamp = false;
            }
        }
        if (allowTimestamp && (newFile.Options & InnoFileEntry.CompareTimeStamp) != 0)
        {
            if (newData.FileTime == oldData.FileTime) return true; // duplicate (modification time)
            if (newData.FileTime < oldData.FileTime && (newFile.Options & InnoFileEntry.PromptIfOlder) == 0) return true;
        }
        // ConfirmOverwrite / read-only prompts: innoextract always answers "overwrite".
        return false;
    }
}

/// <summary>
/// Inverse of Inno Setup's CALL/JMP address transform (5.2.0+; <paramref name="flipHighByte"/> for 5.3.9+): relative
/// E8/E9 targets were made absolute per 64 KiB block. Streaming: holds back at most 4 bytes between calls.
/// </summary>
internal sealed class CallInstructionFilter(bool flipHighByte)
{
    private uint _offset;
    private int _need; // address bytes still to collect (0 = scanning)
    private readonly byte[] _addr = new byte[4];

    /// <summary>Transforms <paramref name="input"/>; <paramref name="output"/> must have room for input.Length + 4 bytes.</summary>
    public int Process(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var n = 0;
        foreach (var b in input)
        {
            _offset++;
            if (_need > 0)
            {
                _addr[4 - _need] = b;
                if (--_need == 0)
                {
                    if (_addr[3] is 0x00 or 0xFF)
                    {
                        var addr = _offset & 0xFFFFFF;
                        var rel = (uint)(_addr[0] | (_addr[1] << 8) | (_addr[2] << 16)) - addr;
                        _addr[0] = (byte)rel;
                        _addr[1] = (byte)(rel >> 8);
                        _addr[2] = (byte)(rel >> 16);
                        if (flipHighByte && (rel & 0x800000) != 0) _addr[3] = (byte)~_addr[3];
                    }
                    _addr.CopyTo(output[n..]);
                    n += 4;
                }
                continue;
            }
            output[n++] = b;
            if (b is 0xE8 or 0xE9 && 0x10000 - ((_offset - 1) % 0x10000) >= 5) _need = 4;
        }
        return n;
    }

    /// <summary>At end of input: emits address bytes collected so far, untransformed.</summary>
    public int Flush(Span<byte> output)
    {
        if (_need == 0) return 0;
        var have = 4 - _need;
        _addr.AsSpan(0, have).CopyTo(output);
        _need = 0;
        return have;
    }
}
