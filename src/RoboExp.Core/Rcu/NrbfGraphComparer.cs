using System.Formats.Nrbf;

namespace RoboExp.Core.Rcu;

/// <summary>
/// Structural comparison of two MS-NRBF payloads as decoded by <see cref="NrbfDecoder"/>: same record kinds, type and
/// assembly names, member names in order, primitive/string values, array lengths/elements, and the same object
/// sharing (a record reachable twice in one graph must correspond to one record in the other). Used to verify that
/// <see cref="RcuWriter"/> output is equivalent to the original file.
/// </summary>
public static class NrbfGraphComparer
{
    /// <returns>Null when equivalent, else a description of the first difference (owning type and member).</returns>
    public static string? FindFirstDifference(Stream expected, Stream actual)
    {
        var a = NrbfDecoder.Decode(expected, leaveOpen: true);
        var b = NrbfDecoder.Decode(actual, leaveOpen: true);
        return new Walker().Compare(a, b);
    }

    public static string? FindFirstDifference(byte[] expected, byte[] actual) =>
        FindFirstDifference(new MemoryStream(expected, false), new MemoryStream(actual, false));

    /// <summary>Iterative walk (graphs are deep: flowchart port chains).</summary>
    private sealed class Walker
    {
        private readonly Dictionary<SerializationRecord, SerializationRecord> _pairs = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<SerializationRecord> _matched = new(ReferenceEqualityComparer.Instance);
        private readonly Stack<(object? A, object? B, string Path)> _work = new();

        public string? Compare(SerializationRecord a, SerializationRecord b)
        {
            _work.Push((a, b, "root"));
            while (_work.TryPop(out var item))
            {
                if (Step(item.A, item.B, item.Path) is { } diff) return diff;
            }
            return null;
        }

        private string? Step(object? a, object? b, string path)
        {
            if (a is PrimitiveTypeRecord pa) a = pa.Value;
            if (b is PrimitiveTypeRecord pb) b = pb.Value;
            if (a is null || b is null)
                return a is null && b is null ? null : $"{path}: {Describe(a)} vs {Describe(b)}";
            if (a is not SerializationRecord ra || b is not SerializationRecord rb)
            {
                if (a is SerializationRecord || b is SerializationRecord) return $"{path}: {Describe(a)} vs {Describe(b)}";
                return a.GetType() == b.GetType() && a.Equals(b) ? null : $"{path}: {Describe(a)} vs {Describe(b)}";
            }

            if (_pairs.TryGetValue(ra, out var seen))
                return ReferenceEquals(seen, rb) ? null : $"{path}: shared object maps to different records";
            if (!_matched.Add(rb)) return $"{path}: record shared in actual graph only";
            _pairs[ra] = rb;

            switch (ra)
            {
                case ClassRecord ca:
                {
                    if (rb is not ClassRecord cb) return $"{path}: {Describe(ra)} vs {Describe(rb)}";
                    if (ca.TypeName.FullName != cb.TypeName.FullName)
                        return $"{path}: type {ca.TypeName.FullName} vs {cb.TypeName.FullName}";
                    if (ca.TypeName.AssemblyName?.FullName != cb.TypeName.AssemblyName?.FullName)
                        return $"{path}: assembly {ca.TypeName.AssemblyName?.FullName} vs {cb.TypeName.AssemblyName?.FullName}";
                    var na = ca.MemberNames.ToArray();
                    var nb = cb.MemberNames.ToArray();
                    if (!na.SequenceEqual(nb, StringComparer.Ordinal))
                        return $"{path} ({ca.TypeName.FullName}): members [{string.Join(",", na)}] vs [{string.Join(",", nb)}]";
                    for (var i = na.Length - 1; i >= 0; i--)
                        _work.Push((ca.GetRawValue(na[i]), cb.GetRawValue(na[i]), $"{ca.TypeName.FullName}.{na[i]}"));
                    return null;
                }
                case ArrayRecord aa:
                {
                    if (rb is not ArrayRecord ab) return $"{path}: {Describe(ra)} vs {Describe(rb)}";
                    if (aa.TypeName.FullName != ab.TypeName.FullName) return $"{path}: array {aa.TypeName.FullName} vs {ab.TypeName.FullName}";
                    if (aa is SZArrayRecord<byte> ba && ab is SZArrayRecord<byte> bb)
                        return ba.GetArray().AsSpan().SequenceEqual(bb.GetArray()) ? null : $"{path}: byte[] content differs";
                    var ea = Elements(aa);
                    var eb = Elements(ab);
                    if (ea.Length != eb.Length) return $"{path}: array length {ea.Length} vs {eb.Length}";
                    for (var i = ea.Length - 1; i >= 0; i--) _work.Push((ea.GetValue(i), eb.GetValue(i), $"{path}[{i}]"));
                    return null;
                }
                default:
                    return $"{path}: unsupported record {ra.RecordType}";
            }
        }

        private static Array Elements(ArrayRecord array) => array switch
        {
            SZArrayRecord<SerializationRecord> o => o.GetArray(),
            SZArrayRecord<string> s => s.GetArray(),
            SZArrayRecord<int> i => i.GetArray(),
            SZArrayRecord<long> l => l.GetArray(),
            SZArrayRecord<short> sh => sh.GetArray(),
            SZArrayRecord<bool> bo => bo.GetArray(),
            SZArrayRecord<float> f => f.GetArray(),
            SZArrayRecord<double> d => d.GetArray(),
            _ => throw new NotSupportedException($"Array record {array.TypeName.FullName}"),
        };

        private static string Describe(object? v) => v switch
        {
            null => "null",
            string s => $"\"{(s.Length > 60 ? s[..60] + "…" : s)}\"",
            ClassRecord c => c.TypeName.FullName,
            SerializationRecord r => r.RecordType.ToString(),
            _ => $"{v} ({v.GetType().Name})",
        };
    }
}
