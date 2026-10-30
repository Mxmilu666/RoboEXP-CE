using System.Formats.Nrbf;
using System.Runtime.Serialization;
using System.Text;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;

namespace RoboExp.Core.Rcu;

/// <summary>
/// Reads a .rcu project (MS-NRBF written by the original's BinaryFormatter, any version from RoboExp 1.0 to 6.0.8)
/// into the <see cref="RcuFile"/> model. Decoding uses <see cref="NrbfDecoder"/>, so no type is ever instantiated
/// from the file. Older member layouts and unknown members are tolerated and kept for round-tripping
/// (<see cref="RcuObject.SerializedMemberOrder"/>, <see cref="RcuObject.UnknownMembers"/>); serialized event handlers
/// are kept opaque (<see cref="SerializedDelegate"/>).
/// </summary>
public static class RcuReader
{
    public const string RootTypeName = "RoboExp.RCUFile";

    public static RcuFile Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    /// <summary>
    /// Original <c>PublicMethod.DeSerializeObjectFromFile</c>: <see cref="Read(string)"/>, then the project's
    /// <see cref="ProjectInfo.ProjectPath"/> is set to the full path it was opened from.
    /// </summary>
    public static RcuFile Open(string path)
    {
        var file = Read(path);
        if (file.ProjectData?.ProjectInfo is { } info) info.ProjectPath = Path.GetFullPath(path);
        return file;
    }

    /// <exception cref="InvalidDataException">Not an NRBF stream, not an RCUFile, or a member of unexpected type.</exception>
    public static RcuFile Read(Stream s)
    {
        var (root, records) = DecodeRoot(s, RootTypeName, "project file");
        var file = (RcuFile)new GraphConverter().ConvertGraph(root, typeof(RcuFile))!;
        file.Assemblies = AssembliesOf(records.Values);
        return file;
    }

    /// <summary>
    /// Reads the bare NRBF collection used by the original RoboEXP flowchart clipboard, without a project wrapper
    /// or a platform clipboard envelope. Types are decoded as data, never instantiated from the stream.
    /// The stream remains open.
    /// </summary>
    /// <exception cref="InvalidDataException">Not an NRBF collection, or a member has an unexpected type.</exception>
    public static GraphicObjectCollection ReadGraphicObjects(Stream s)
    {
        var (root, _) = DecodeRoot(s, "RoboExp.GraphicObjects.GraphicObjectCollection", "graphic object collection");
        return (GraphicObjectCollection)new GraphConverter().ConvertGraph(root, typeof(GraphicObjectCollection))!;
    }

    private static (ClassRecord Root, IReadOnlyDictionary<SerializationRecordId, SerializationRecord> Records)
        DecodeRoot(Stream s, string expectedType, string description)
    {
        SerializationRecord rootRecord;
        IReadOnlyDictionary<SerializationRecordId, SerializationRecord> records;
        try
        {
            rootRecord = NrbfDecoder.Decode(s, out records, leaveOpen: true);
        }
        catch (Exception ex) when (ex is SerializationException or NotSupportedException or DecoderFallbackException or EndOfStreamException)
        {
            throw new InvalidDataException($"Not a RoboEXP {description}: {ex.Message}", ex);
        }
        if (rootRecord is not ClassRecord root || root.TypeName.FullName != expectedType)
            throw new InvalidDataException($"Not a RoboEXP {description}: root object is {DescribeRecord(rootRecord)}, expected {expectedType}");
        return (root, records);
    }

    private static RcuAssemblyNames AssembliesOf(IEnumerable<SerializationRecord> records)
    {
        string? roboExp = null, softType = null, drawing = null;
        foreach (var r in records)
        {
            if (r is not ClassRecord c || c.TypeName.AssemblyName is not { } asm) continue;
            switch (asm.Name)
            {
                case "RoboExp": roboExp ??= asm.FullName; break;
                case "SoftType": softType ??= asm.FullName; break;
                case "System.Drawing": drawing ??= asm.FullName; break;
            }
        }
        var d = RcuAssemblyNames.Default;
        return new RcuAssemblyNames(roboExp ?? d.RoboExp, softType ?? d.SoftType, drawing ?? d.SystemDrawing);
    }

    private static string DescribeRecord(SerializationRecord? r) => r switch
    {
        null => "null",
        ClassRecord c => c.TypeName.FullName,
        _ => r.RecordType.ToString(),
    };

    /// <summary>
    /// Record graph -> model. Reference objects are created and memoised on first sight and filled from a work queue,
    /// so arbitrarily long flowchart chains (Port -> ConnectedPort -> Parent -> ...) never recurse deeply.
    /// Error paths name the owning type and member (full paths would grow with the chain length).
    /// </summary>
    private sealed class GraphConverter
    {
        private readonly Dictionary<SerializationRecord, object?> _memo = new(ReferenceEqualityComparer.Instance);
        private readonly Queue<Action> _pending = new();
        private readonly Dictionary<SerializationRecord, object> _emptyItems = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<SerializationRecord, string> _emptyStrings = new(ReferenceEqualityComparer.Instance);

        public object? ConvertGraph(ClassRecord root, Type target)
        {
            var result = Convert(root, target, root.TypeName.FullName);
            while (_pending.TryDequeue(out var fill)) fill();
            return result;
        }

        /// <summary>Converts a raw member/array value to <paramref name="target"/> (typeof(object) = generic).</summary>
        private object? Convert(object? raw, Type target, string path)
        {
            if (raw is PrimitiveTypeRecord<string> { Value.Length: 0 } emptyString) raw = EmptyString(emptyString);
            else if (raw is PrimitiveTypeRecord primitive) raw = primitive.Value;
            if (raw is null)
            {
                if (target.IsValueType && Nullable.GetUnderlyingType(target) == null)
                    throw new InvalidDataException($"{path}: null where a {target.Name} is required");
                return null;
            }
            if (raw is not SerializationRecord record)
            {
                if (target == typeof(object) || target.IsInstanceOfType(raw)) return raw;
                if (target.IsEnum && raw is int i) return Enum.ToObject(target, i);
                throw Mismatch(path, raw.GetType().Name, target);
            }

            if (target.IsEnum) return ConvertEnum(record, target, path);
            if (target == typeof(Point) || target == typeof(Size) || target == typeof(Color))
                return ConvertDrawingStruct(record, target, path);

            if (_memo.TryGetValue(record, out var done))
            {
                if (done == null || target.IsInstanceOfType(done)) return done;
                throw Mismatch(path, done.GetType().Name, target);
            }

            object? result = record switch
            {
                ClassRecord c => ConvertClass(c, target, path),
                SZArrayRecord<string> strings => Memo(record, strings.GetArray()),
                SZArrayRecord<byte> bytes => Memo(record, bytes.GetArray()),
                SZArrayRecord<SerializationRecord> objects => ConvertObjectArray(record, objects, path),
                SZArrayRecord<int> ints => Memo(record, ints.GetArray()),
                SZArrayRecord<long> longs => Memo(record, longs.GetArray()),
                SZArrayRecord<short> shorts => Memo(record, shorts.GetArray()),
                SZArrayRecord<bool> bools => Memo(record, bools.GetArray()),
                SZArrayRecord<float> floats => Memo(record, floats.GetArray()),
                SZArrayRecord<double> doubles => Memo(record, doubles.GetArray()),
                _ => throw new InvalidDataException($"{path}: unsupported record {record.RecordType}"),
            };
            if (result != null && !target.IsInstanceOfType(result)) throw Mismatch(path, result.GetType().Name, target);
            return result;
        }

        private object? Memo(SerializationRecord record, object? value)
        {
            _memo[record] = value;
            return value;
        }

        private object?[] ConvertObjectArray(SerializationRecord record, SZArrayRecord<SerializationRecord> array, string path)
        {
            var source = array.GetArray();
            var result = new object?[source.Length];
            _memo[record] = result;
            _pending.Enqueue(() =>
            {
                for (var i = 0; i < source.Length; i++) result[i] = Convert(source[i], typeof(object), path + "[]");
            });
            return result;
        }

        private static InvalidDataException Mismatch(string path, string actual, Type expected) =>
            new($"{path}: found {actual}, expected {expected.Name}");

        private static object ConvertEnum(SerializationRecord record, Type target, string path)
        {
            if (record is ClassRecord c && c.HasMember("value__") && c.GetRawValue("value__") is int value)
                return Enum.ToObject(target, value);
            throw Mismatch(path, DescribeRecord(record), target);
        }

        private static object ConvertDrawingStruct(SerializationRecord record, Type target, string path)
        {
            if (record is not ClassRecord c) throw Mismatch(path, DescribeRecord(record), target);
            try
            {
                if (target == typeof(Point)) return new Point(c.GetInt32("x"), c.GetInt32("y"));
                if (target == typeof(Size)) return new Size(c.GetInt32("width"), c.GetInt32("height"));
                return new Color(c.GetString("name"), c.GetInt64("value"), c.GetInt16("knownColor"), c.GetInt16("state"));
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
            {
                throw new InvalidDataException($"{path}: malformed {c.TypeName.FullName}: {ex.Message}", ex);
            }
        }

        private object? ConvertClass(ClassRecord c, Type target, string path)
        {
            var typeName = c.TypeName.FullName;
            if (RcuSchema.ByName.TryGetValue(typeName, out var schema))
            {
                var o = schema.Create();
                _memo[c] = o;
                _pending.Enqueue(() => FillSchemaObject(c, schema, o));
                return o;
            }
            switch (typeName)
            {
                case RcuSchema.ArrayListType:
                    return ConvertList(c, ListType(target, typeof(ArrayList<>)), "_items", "_size", "_version", path);
                case RcuSchema.EventArrayListType:
                {
                    var list = (ISerializedEventArrayList)ConvertList(c, ListType(target, typeof(EventArrayList<>)),
                        "ArrayList+_items", "ArrayList+_size", "ArrayList+_version", path);
                    list.Version = Int(c, "m_Version", 100);
                    list.OldVersion = Int(c, "m_OldVersion", 100);
                    if (c.HasMember("Changed"))
                        _pending.Enqueue(() => list.Changed = (SerializedDelegate?)Convert(Raw(c, "Changed"), typeof(SerializedDelegate), typeName + ".Changed"));
                    return list;
                }
                case RcuSchema.BitmapType:
                {
                    if (c.GetArrayRecord("Data") is not SZArrayRecord<byte> data)
                        throw new InvalidDataException($"{path}: Bitmap without Data bytes");
                    return Memo(c, new EncodedImage(data.GetArray()));
                }
                case RcuSchema.DelegateHolderType:
                {
                    var holder = new SerializedDelegate();
                    _memo[c] = holder;
                    _pending.Enqueue(() =>
                    {
                        foreach (var name in c.MemberNames)
                            holder.Members.Add(new(name, Convert(Raw(c, name), typeof(object), typeName + "." + name)));
                    });
                    return holder;
                }
                case RcuSchema.DelegateEntryType:
                {
                    var entry = new SerializedDelegateEntry();
                    _memo[c] = entry;
                    _pending.Enqueue(() =>
                    {
                        entry.Type = Str(c, "type");
                        entry.Assembly = Str(c, "assembly");
                        entry.Target = c.HasMember("target") ? Convert(Raw(c, "target"), typeof(object), typeName + ".target") : null;
                        entry.TargetTypeAssembly = Str(c, "targetTypeAssembly");
                        entry.TargetTypeName = Str(c, "targetTypeName");
                        entry.MethodName = Str(c, "methodName");
                        entry.DelegateEntry = c.HasMember("delegateEntry")
                            ? (SerializedDelegateEntry?)Convert(Raw(c, "delegateEntry"), typeof(SerializedDelegateEntry), typeName + ".delegateEntry")
                            : null;
                    });
                    return entry;
                }
            }
            if (RcuSchema.EnumsByName.TryGetValue(typeName, out var enumType)) return ConvertEnum(c, enumType, path);
            switch (typeName)
            {
                case RcuSchema.PointType: return ConvertDrawingStruct(c, typeof(Point), path);
                case RcuSchema.SizeType: return ConvertDrawingStruct(c, typeof(Size), path);
                case RcuSchema.ColorType: return ConvertDrawingStruct(c, typeof(Color), path);
            }

            var unknown = new UnknownObject(typeName, c.TypeName.AssemblyName is { Name: not "mscorlib" } a ? a.FullName : null);
            _memo[c] = unknown;
            _pending.Enqueue(() =>
            {
                foreach (var name in c.MemberNames)
                    unknown.Members.Add(new(name, Convert(Raw(c, name), typeof(object), typeName + "." + name)));
            });
            return unknown;
        }

        /// <summary>
        /// Member value as <see cref="ClassRecord.GetRawValue"/>, except that each distinct empty-string record yields
        /// a distinct string instance: the original sometimes held several "" instances, which BinaryFormatter writes
        /// as separate records, and <see cref="RcuWriter"/> reproduces sharing by reference identity.
        /// </summary>
        private object? Raw(ClassRecord c, string member)
        {
            var raw = c.GetRawValue(member);
            return raw is string { Length: 0 } && c.GetSerializationRecord(member) is PrimitiveTypeRecord<string> record
                ? EmptyString(record)
                : raw;
        }

        private string EmptyString(PrimitiveTypeRecord<string> record)
        {
            if (_emptyStrings.TryGetValue(record, out var s)) return s;
#pragma warning disable CS0618 // string.Copy is the only way to obtain a distinct zero-length string instance
            s = _emptyStrings.Count == 0 ? string.Empty : string.Copy(string.Empty);
#pragma warning restore CS0618
            _emptyStrings[record] = s;
            return s;
        }

        private string? Str(ClassRecord c, string member) =>
            !c.HasMember(member) ? null
            : Raw(c, member) switch
            {
                null => null,
                string s => s,
                var other => throw Mismatch($"{c.TypeName.FullName}.{member}", other.GetType().Name, typeof(string)),
            };

        private static int Int(ClassRecord c, string member, int fallback) =>
            c.HasMember(member) && c.GetRawValue(member) is int i ? i : fallback;

        /// <summary>Closed list type for a target property type; generic (object) targets get object elements.</summary>
        private static Type ListType(Type target, Type definition) =>
            target.IsGenericType && target.GetGenericTypeDefinition() == definition ? target : definition.MakeGenericType(typeof(object));

        private ISerializedArrayList ConvertList(ClassRecord c, Type listType, string itemsMember, string sizeMember, string versionMember, string path)
        {
            var list = (ISerializedArrayList)Activator.CreateInstance(listType)!;
            _memo[c] = list;
            if (c.GetArrayRecord(itemsMember) is not SZArrayRecord<SerializationRecord> items)
                throw new InvalidDataException($"{path}: ArrayList without an object[] {itemsMember}");
            var source = items.GetArray();
            if (source.Length == 0)
            {
                if (!_emptyItems.TryGetValue(items, out var identity)) _emptyItems[items] = identity = new object();
                list.EmptyItemsIdentity = identity;
            }
            var size = c.GetInt32(sizeMember);
            var version = c.GetInt32(versionMember);
            _pending.Enqueue(() =>
            {
                var converted = new object?[source.Length];
                for (var i = 0; i < source.Length && i < size; i++)
                    converted[i] = Convert(source[i], list.ElementType, path + "[]");
                try
                {
                    list.Load(converted, size, version);
                }
                catch (InvalidDataException ex)
                {
                    throw new InvalidDataException($"{path}: {ex.Message}", ex);
                }
            });
            return list;
        }

        private void FillSchemaObject(ClassRecord c, TypeSchema schema, RcuObject o)
        {
            var names = c.MemberNames.ToArray();
            foreach (var name in names)
            {
                var raw = Raw(c, name);
                var path = schema.TypeName + "." + name;
                if (schema.Find(name) is { } m)
                {
                    m.Set(o, Convert(raw, m.ModelType, path));
                }
                else
                {
                    o.UnknownMembers ??= new Dictionary<string, object?>(StringComparer.Ordinal);
                    o.UnknownMembers[name] = Convert(raw, typeof(object), path);
                }
            }
            if (!names.SequenceEqual(schema.Members.Select(m => m.Name), StringComparer.Ordinal))
                o.SerializedMemberOrder = names;
        }
    }
}
