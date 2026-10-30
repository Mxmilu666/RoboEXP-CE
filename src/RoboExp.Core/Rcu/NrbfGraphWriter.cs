using System.Text;

namespace RoboExp.Core.Rcu;

/// <summary>MS-NRBF BinaryTypeEnumeration.</summary>
internal enum BinaryType : byte
{
    Primitive = 0,
    String = 1,
    Object = 2,
    SystemClass = 3,
    Class = 4,
    ObjectArray = 5,
    StringArray = 6,
    PrimitiveArray = 7,
}

/// <summary>MS-NRBF PrimitiveTypeEnumeration (subset used by .rcu files plus common extras).</summary>
internal enum PrimitiveCode : byte
{
    None = 0,
    Boolean = 1,
    Byte = 2,
    Char = 3,
    Double = 6,
    Int16 = 7,
    Int32 = 8,
    Int64 = 9,
    SByte = 10,
    Single = 11,
    UInt16 = 14,
    UInt32 = 15,
    UInt64 = 16,
}

/// <summary>
/// Declared type of a member as BinaryFormatter saw it. <see cref="CompatKey"/> stands for the declared
/// <c>System.Type</c> that BinaryFormatter compares when deciding whether a class record may reuse earlier metadata.
/// For <see cref="BinaryType.Class"/>, <see cref="TypeName"/>/<see cref="Library"/> are used only when the value is null
/// (otherwise the runtime type of the value is written, as BinaryFormatter does).
/// </summary>
internal readonly record struct WDecl(BinaryType Kind, PrimitiveCode Primitive, string? TypeName, string? Library, string CompatKey)
{
    public static WDecl Prim(PrimitiveCode code) => new(BinaryType.Primitive, code, null, null, code.ToString());
    public static readonly WDecl String = new(BinaryType.String, PrimitiveCode.None, null, null, "System.String");
    public static readonly WDecl Object = new(BinaryType.Object, PrimitiveCode.None, null, null, "System.Object");
    public static readonly WDecl StringArray = new(BinaryType.StringArray, PrimitiveCode.None, null, null, "System.String[]");
    public static readonly WDecl ObjectArray = new(BinaryType.ObjectArray, PrimitiveCode.None, null, null, "System.Object[]");
    public static WDecl PrimArray(PrimitiveCode code) => new(BinaryType.PrimitiveArray, code, null, null, code + "[]");
    public static WDecl Class(string typeName, string? library) => new(BinaryType.Class, PrimitiveCode.None, typeName, library, typeName);
}

internal readonly record struct WMember(string Name, WDecl Decl, object? Value);

/// <summary>A class record to write. Value types are written inline (nested) when declared as their own type.</summary>
internal sealed class WClass(string typeName, string? library, bool isValueType)
{
    public string TypeName { get; } = typeName;

    /// <summary>Assembly display name; null = mscorlib (SystemClass records).</summary>
    public string? Library { get; } = library;

    public bool IsValueType { get; } = isValueType;
    public List<WMember> Members { get; } = [];

    public WClass Add(string name, WDecl decl, object? value)
    {
        Members.Add(new WMember(name, decl, value));
        return this;
    }
}

internal enum WArrayKind : byte { Object, String, Primitive }

/// <summary>A single-dimension zero-based array record. Object array items are wire values (null/primitive/string/WClass/WArray).</summary>
internal sealed class WArray(WArrayKind kind, Array items, PrimitiveCode primitive = PrimitiveCode.None)
{
    public WArrayKind Kind { get; } = kind;
    public Array Items { get; } = items;
    public PrimitiveCode Primitive { get; } = primitive;
}

/// <summary>
/// Writes a <see cref="WClass"/> graph as MS-NRBF the way .NET Framework's BinaryFormatter (ObjectWriter /
/// __BinaryWriter) does, so output is byte-identical to the original for the same graph:
/// <list type="bullet">
/// <item>one id counter shared by objects, strings, libraries and inline value types (negative ids);</item>
/// <item>breadth-first object queue; strings inline on first occurrence (by reference), then MemberReference;</item>
/// <item>a member's assembly gets its BinaryLibrary record when the owning object is about to be written;</item>
/// <item>class metadata reused (ClassWithId) only against the first record of a type name, when member names and
/// declared types match;</item>
/// <item>null runs inside arrays collapse into ObjectNull / ObjectNullMultiple256 / ObjectNullMultiple.</item>
/// </list>
/// </summary>
internal sealed class NrbfGraphWriter
{
    private readonly BinaryWriter _w;
    private readonly Dictionary<object, int> _ids = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, int> _libraries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Id, WMember[] Members)> _metadata = new(StringComparer.Ordinal);
    private readonly Queue<object> _queue = new();
    private int _counter;
    private int _pendingNulls;
    private object? _previous;
    private int _previousId;

    private NrbfGraphWriter(Stream stream) => _w = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true);

    public static void Write(Stream stream, WClass root)
    {
        var writer = new NrbfGraphWriter(stream);
        writer.WriteGraph(root);
        writer._w.Flush();
    }

    private void WriteGraph(WClass root)
    {
        var rootId = GetId(root, out _);
        _w.Write((byte)0); // SerializedStreamHeader
        _w.Write(rootId);
        _w.Write(-1);
        _w.Write(1);
        _w.Write(0);
        _queue.Enqueue(root);
        while (_queue.TryDequeue(out var next))
        {
            var id = _ids[next];
            switch (next)
            {
                case WClass c:
                    EnsureLibrary(c.Library);
                    WriteClass(c, id);
                    break;
                case WArray a:
                    WriteArray(a, id);
                    break;
            }
        }
        _w.Write((byte)11); // MessageEnd
    }

    /// <summary>
    /// ObjectWriter.InternalGetId: every lookup consumes a counter value (a new object takes it), except a repeated
    /// lookup of the object looked up last.
    /// </summary>
    private int GetId(object o, out bool isNew)
    {
        if (ReferenceEquals(o, _previous))
        {
            isNew = false;
            return _previousId;
        }
        var next = ++_counter;
        isNew = !_ids.TryGetValue(o, out var id);
        if (isNew) _ids[o] = id = next;
        _previous = o;
        _previousId = id;
        return id;
    }

    private void EnsureLibrary(string? library)
    {
        if (library == null || _libraries.ContainsKey(library)) return;
        FlushNulls();
        var id = ++_counter;
        _previous = null; // the original looks up a fresh "___AssemblyString___" key
        _libraries[library] = id;
        _w.Write((byte)12); // BinaryLibrary
        _w.Write(id);
        _w.Write(library);
    }

    private void WriteClass(WClass c, int id)
    {
        var members = c.Members;
        // BinaryFormatter resolves every non-primitive member's type (allocating its assembly id) before the record.
        foreach (var m in members)
        {
            if (m.Decl.Kind is BinaryType.Class or BinaryType.Object)
                EnsureLibrary(m.Value is WClass wc ? wc.Library : m.Value == null && m.Decl.Kind == BinaryType.Class ? m.Decl.Library : null);
        }

        if (_metadata.TryGetValue(c.TypeName, out var meta) && Compatible(meta.Members, members))
        {
            _w.Write((byte)1); // ClassWithId
            _w.Write(id);
            _w.Write(meta.Id);
        }
        else
        {
            WriteClassMetadata(c, id);
            _metadata.TryAdd(c.TypeName, (id, [.. members]));
        }

        foreach (var m in members) WriteMemberValue(m);
    }

    private static bool Compatible(WMember[] first, List<WMember> members)
    {
        if (first.Length != members.Count) return false;
        for (var i = 0; i < first.Length; i++)
        {
            if (!string.Equals(first[i].Name, members[i].Name, StringComparison.Ordinal)
                || !string.Equals(first[i].Decl.CompatKey, members[i].Decl.CompatKey, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private void WriteClassMetadata(WClass c, int id)
    {
        _w.Write((byte)(c.Library == null ? 4 : 5)); // SystemClassWithMembersAndTypes / ClassWithMembersAndTypes
        _w.Write(id);
        _w.Write(c.TypeName);
        _w.Write(c.Members.Count);
        foreach (var m in c.Members) _w.Write(m.Name);

        Span<BinaryType> kinds = c.Members.Count <= 64 ? stackalloc BinaryType[c.Members.Count] : new BinaryType[c.Members.Count];
        for (var i = 0; i < c.Members.Count; i++)
        {
            var m = c.Members[i];
            kinds[i] = m.Decl.Kind == BinaryType.Class ? (TypeOf(m).Library == null ? BinaryType.SystemClass : BinaryType.Class) : m.Decl.Kind;
            _w.Write((byte)kinds[i]);
        }
        for (var i = 0; i < c.Members.Count; i++)
        {
            var m = c.Members[i];
            switch (kinds[i])
            {
                case BinaryType.Primitive:
                case BinaryType.PrimitiveArray:
                    _w.Write((byte)m.Decl.Primitive);
                    break;
                case BinaryType.SystemClass:
                    _w.Write(TypeOf(m).TypeName);
                    break;
                case BinaryType.Class:
                    var (typeName, library) = TypeOf(m);
                    _w.Write(typeName);
                    _w.Write(_libraries[library!]);
                    break;
            }
        }
        if (c.Library != null) _w.Write(_libraries[c.Library]);
    }

    private static (string TypeName, string? Library) TypeOf(WMember m) => m.Value switch
    {
        WClass wc => (wc.TypeName, wc.Library),
        null => (m.Decl.TypeName!, m.Decl.Library),
        _ => throw new InvalidOperationException($"Member {m.Name}: class-typed member holds a {m.Value.GetType().Name}"),
    };

    private void WriteMemberValue(WMember m)
    {
        if (m.Decl.Kind == BinaryType.Primitive)
        {
            WritePrimitive(m.Decl.Primitive, m.Value ?? throw new InvalidOperationException($"Member {m.Name}: primitive is null"));
            return;
        }
        switch (m.Value)
        {
            case null:
                _w.Write((byte)10); // ObjectNull
                break;
            case string s:
                WriteString(s);
                break;
            case WClass { IsValueType: true } nested when m.Decl.Kind == BinaryType.Class:
                WriteClass(nested, -++_counter);
                break;
            case WClass or WArray:
                WriteReference(m.Value);
                break;
            default:
                WriteMemberPrimitiveTyped(m.Value);
                break;
        }
    }

    private void WriteReference(object value)
    {
        var id = GetId(value, out var isNew);
        if (isNew) _queue.Enqueue(value);
        _w.Write((byte)9); // MemberReference
        _w.Write(id);
    }

    private void WriteString(string s)
    {
        var id = GetId(s, out var isNew);
        if (isNew)
        {
            _w.Write((byte)6); // BinaryObjectString
            _w.Write(id);
            _w.Write(s);
        }
        else
        {
            _w.Write((byte)9);
            _w.Write(id);
        }
    }

    private void WriteArray(WArray a, int id)
    {
        switch (a.Kind)
        {
            case WArrayKind.Primitive:
                _w.Write((byte)15); // ArraySinglePrimitive
                _w.Write(id);
                _w.Write(a.Items.Length);
                _w.Write((byte)a.Primitive);
                if (a.Items is byte[] bytes) _w.Write(bytes);
                else foreach (var item in a.Items) WritePrimitive(a.Primitive, item!);
                return;
            case WArrayKind.String:
                _w.Write((byte)17); // ArraySingleString
                break;
            default:
                _w.Write((byte)16); // ArraySingleObject
                break;
        }
        _w.Write(id);
        _w.Write(a.Items.Length);
        foreach (var item in a.Items)
        {
            if (item == null)
            {
                _pendingNulls++;
                continue;
            }
            if (item is WClass wc) EnsureLibrary(wc.Library);
            FlushNulls();
            switch (item)
            {
                case string s:
                    WriteString(s);
                    break;
                case WClass or WArray:
                    WriteReference(item);
                    break;
                default:
                    WriteMemberPrimitiveTyped(item);
                    break;
            }
        }
        FlushNulls();
    }

    private void FlushNulls()
    {
        switch (_pendingNulls)
        {
            case 0:
                return;
            case 1:
                _w.Write((byte)10); // ObjectNull
                break;
            case < 256:
                _w.Write((byte)13); // ObjectNullMultiple256
                _w.Write((byte)_pendingNulls);
                break;
            default:
                _w.Write((byte)14); // ObjectNullMultiple
                _w.Write(_pendingNulls);
                break;
        }
        _pendingNulls = 0;
    }

    private void WriteMemberPrimitiveTyped(object value)
    {
        var code = CodeOf(value);
        _w.Write((byte)8); // MemberPrimitiveTyped
        _w.Write((byte)code);
        WritePrimitive(code, value);
    }

    internal static PrimitiveCode CodeOf(object value) => value switch
    {
        bool => PrimitiveCode.Boolean,
        byte => PrimitiveCode.Byte,
        char => PrimitiveCode.Char,
        double => PrimitiveCode.Double,
        short => PrimitiveCode.Int16,
        int => PrimitiveCode.Int32,
        long => PrimitiveCode.Int64,
        sbyte => PrimitiveCode.SByte,
        float => PrimitiveCode.Single,
        ushort => PrimitiveCode.UInt16,
        uint => PrimitiveCode.UInt32,
        ulong => PrimitiveCode.UInt64,
        _ => throw new NotSupportedException($"Cannot serialize a {value.GetType().Name} as an NRBF primitive"),
    };

    private void WritePrimitive(PrimitiveCode code, object value)
    {
        switch (code)
        {
            case PrimitiveCode.Boolean: _w.Write((bool)value); break;
            case PrimitiveCode.Byte: _w.Write((byte)value); break;
            case PrimitiveCode.Char: _w.Write((char)value); break;
            case PrimitiveCode.Double: _w.Write((double)value); break;
            case PrimitiveCode.Int16: _w.Write((short)value); break;
            case PrimitiveCode.Int32: _w.Write((int)value); break;
            case PrimitiveCode.Int64: _w.Write((long)value); break;
            case PrimitiveCode.SByte: _w.Write((sbyte)value); break;
            case PrimitiveCode.Single: _w.Write((float)value); break;
            case PrimitiveCode.UInt16: _w.Write((ushort)value); break;
            case PrimitiveCode.UInt32: _w.Write((uint)value); break;
            case PrimitiveCode.UInt64: _w.Write((ulong)value); break;
            default: throw new NotSupportedException($"Primitive type {code}");
        }
    }
}
