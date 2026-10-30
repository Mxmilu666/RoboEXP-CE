using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;

namespace RoboExp.Core.Rcu;

/// <summary>
/// Writes an <see cref="RcuFile"/> as the MS-NRBF stream the original RoboEXP (<c>BinaryFormatter</c> on .NET
/// Framework) reads: original type names, assembly names (<see cref="RcuFile.Assemblies"/>), member names/order and
/// member type info, emitted with BinaryFormatter's id/metadata/library ordering so a file read with
/// <see cref="RcuReader"/> is written back byte-identical.
/// </summary>
/// <remarks>
/// Serialized delegates (<see cref="SerializedDelegate"/> on <see cref="EventArrayList{T}.Changed"/>) are written back
/// as read; objects created in memory carry none (null event field), which the original accepts.
/// </remarks>
public static class RcuWriter
{
    public static void Write(string path, RcuFile file)
    {
        using var stream = File.Create(path);
        Write(stream, file);
    }

    public static void Write(Stream s, RcuFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var root = new WireBuilder(file.Assemblies).Build(file);
        NrbfGraphWriter.Write(s, root);
    }

    /// <summary>
    /// Writes the bare NRBF collection expected by the original RoboEXP flowchart clipboard, without a project
    /// wrapper or a platform clipboard envelope. Uses the original assembly identities unless overridden.
    /// The stream remains open.
    /// </summary>
    public static void WriteGraphicObjects(Stream s, GraphicObjectCollection objects, RcuAssemblyNames? assemblies = null)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var root = new WireBuilder(assemblies ?? RcuAssemblyNames.Default).Build(objects);
        NrbfGraphWriter.Write(s, root);
    }

    /// <summary>
    /// Model -> wire graph. Reference objects get their wire shell on first sight (memoised by identity, so sharing is
    /// preserved) and are filled from a work queue: long flowchart chains never recurse deeply.
    /// </summary>
    private sealed class WireBuilder(RcuAssemblyNames names)
    {
        private readonly Dictionary<object, object> _memo = new(ReferenceEqualityComparer.Instance);
        private readonly Queue<Action> _pending = new();
        private readonly Dictionary<object, WArray> _emptyItems = new(ReferenceEqualityComparer.Instance);

        public WClass Build(RcuObject root)
        {
            var wire = (WClass)ToWire(root)!;
            while (_pending.TryDequeue(out var fill)) fill();
            return wire;
        }

        private string? Lib(Asm asm) => RcuSchema.AssemblyName(asm, names);

        /// <summary>Converts a model value to a wire value (null, primitive, string, WClass, WArray).</summary>
        private object? ToWire(object? value)
        {
            switch (value)
            {
                case null:
                case string:
                case bool or byte or char or double or short or int or long or sbyte or float or ushort or uint or ulong:
                    return value;
                case Enum e:
                    return EnumToWire(e);
                case Point p:
                    return new WClass(RcuSchema.PointType, Lib(Asm.Drawing), true)
                        .Add("x", WDecl.Prim(PrimitiveCode.Int32), p.X)
                        .Add("y", WDecl.Prim(PrimitiveCode.Int32), p.Y);
                case Size sz:
                    return new WClass(RcuSchema.SizeType, Lib(Asm.Drawing), true)
                        .Add("width", WDecl.Prim(PrimitiveCode.Int32), sz.Width)
                        .Add("height", WDecl.Prim(PrimitiveCode.Int32), sz.Height);
                case Color c:
                    return ColorToWire(c);
            }

            if (_memo.TryGetValue(value, out var done)) return done;
            switch (value)
            {
                case string?[] strings:
                    return Memo(value, new WArray(WArrayKind.String, strings));
                case byte[] bytes:
                    return Memo(value, new WArray(WArrayKind.Primitive, bytes, PrimitiveCode.Byte));
                case int[] or long[] or short[] or bool[] or float[] or double[]:
                    var array = (Array)value;
                    return Memo(value, new WArray(WArrayKind.Primitive, array, NrbfGraphWriter.CodeOf(Activator.CreateInstance(array.GetType().GetElementType()!)!)));
                case object?[] objects:
                {
                    var items = new object?[objects.Length];
                    _pending.Enqueue(() =>
                    {
                        for (var i = 0; i < items.Length; i++) items[i] = ToWire(objects[i]);
                    });
                    return Memo(value, new WArray(WArrayKind.Object, items));
                }
                case RcuObject o:
                    return ObjectToWire(o);
                case ISerializedArrayList list:
                    return ListToWire(list);
                case EncodedImage image:
                    return Memo(image, new WClass(RcuSchema.BitmapType, Lib(Asm.Drawing), false))
                        .Add("Data", WDecl.PrimArray(PrimitiveCode.Byte), ToWire(image.Data));
                case SerializedDelegate d:
                    return DelegateToWire(d);
                case SerializedDelegateEntry e:
                    return EntryToWire(e);
                case UnknownObject u:
                    return UnknownToWire(u);
                case Array a:
                    throw new NotSupportedException($"Cannot serialize array of {a.GetType().GetElementType()?.Name}");
                default:
                    throw new NotSupportedException($"Cannot serialize {value.GetType().FullName} into a .rcu file");
            }
        }

        private T Memo<T>(object model, T wire) where T : class
        {
            _memo[model] = wire;
            return wire;
        }

        private WClass EnumToWire(Enum e)
        {
            if (!RcuSchema.Enums.TryGetValue(e.GetType(), out var t))
                throw new NotSupportedException($"Enum {e.GetType().Name} has no .rcu type");
            return new WClass(t.TypeName, Lib(t.Asm), true).Add("value__", WDecl.Prim(PrimitiveCode.Int32), Convert.ToInt32(e));
        }

        private WClass ColorToWire(Color c)
        {
            var w = new WClass(RcuSchema.ColorType, Lib(Asm.Drawing), true);
            if (!names.LegacyColorLayout) w.Add("name", WDecl.String, c.Name);
            w.Add("value", WDecl.Prim(PrimitiveCode.Int64), c.Value)
                .Add("knownColor", WDecl.Prim(PrimitiveCode.Int16), c.KnownColor)
                .Add("state", WDecl.Prim(PrimitiveCode.Int16), c.State);
            if (names.LegacyColorLayout) w.Add("name", WDecl.String, c.Name);
            return w;
        }

        private WClass ObjectToWire(RcuObject o)
        {
            if (!RcuSchema.ByModelType.TryGetValue(o.GetType(), out var schema))
                throw new NotSupportedException($"{o.GetType().FullName} has no .rcu type");
            var w = Memo(o, new WClass(schema.TypeName, Lib(schema.Asm), false));
            _pending.Enqueue(() =>
            {
                if (o.SerializedMemberOrder is { } order)
                {
                    foreach (var name in order)
                    {
                        if (schema.Find(name) is { } m) AddMember(w, m, m.Get(o));
                        else if (o.UnknownMembers != null && o.UnknownMembers.TryGetValue(name, out var extra)) AddDynamic(w, name, extra);
                        else throw new InvalidOperationException($"{schema.TypeName}: member '{name}' in SerializedMemberOrder has no value");
                    }
                }
                else
                {
                    foreach (var m in schema.Members) AddMember(w, m, m.Get(o));
                    if (o.UnknownMembers != null)
                        foreach (var (name, extra) in o.UnknownMembers) AddDynamic(w, name, extra);
                }
            });
            return w;
        }

        private void AddMember(WClass w, MemberSchema m, object? value)
        {
            switch (m.Kind)
            {
                case MemberKind.Int32: w.Add(m.Name, WDecl.Prim(PrimitiveCode.Int32), value); break;
                case MemberKind.Boolean: w.Add(m.Name, WDecl.Prim(PrimitiveCode.Boolean), value); break;
                case MemberKind.Single: w.Add(m.Name, WDecl.Prim(PrimitiveCode.Single), value); break;
                case MemberKind.Int64: w.Add(m.Name, WDecl.Prim(PrimitiveCode.Int64), value); break;
                case MemberKind.Int16: w.Add(m.Name, WDecl.Prim(PrimitiveCode.Int16), value); break;
                case MemberKind.StringValue: w.Add(m.Name, value == null ? WDecl.Object : WDecl.String, value); break;
                case MemberKind.StringField: w.Add(m.Name, WDecl.String, value); break;
                case MemberKind.StringArray: w.Add(m.Name, WDecl.StringArray, ToWire(value)); break;
                case MemberKind.ObjectField: w.Add(m.Name, WDecl.Object, ToWire(value)); break;
                case MemberKind.ObjectArray: w.Add(m.Name, WDecl.ObjectArray, ToWire(value)); break;
                case MemberKind.ByteArray: w.Add(m.Name, WDecl.PrimArray(PrimitiveCode.Byte), ToWire(value)); break;
                case MemberKind.Typed:
                case MemberKind.ValueTyped:
                    w.Add(m.Name, WDecl.Class(m.DeclType!, Lib(m.DeclAsm)), ToWire(value));
                    break;
                case MemberKind.ObjectValue:
                    AddDynamic(w, m.Name, value);
                    break;
                default:
                    throw new InvalidOperationException($"Unhandled member kind {m.Kind}");
            }
        }

        /// <summary><c>SerializationInfo.AddValue(name, object)</c>: declared as the value's runtime type.</summary>
        private void AddDynamic(WClass w, string name, object? value)
        {
            var wire = ToWire(value);
            var decl = wire switch
            {
                null => WDecl.Object,
                string => WDecl.String,
                WClass c => WDecl.Class(c.TypeName, c.Library),
                WArray { Kind: WArrayKind.String } => WDecl.StringArray,
                WArray { Kind: WArrayKind.Object } => WDecl.ObjectArray,
                WArray a => WDecl.PrimArray(a.Primitive),
                _ => WDecl.Prim(NrbfGraphWriter.CodeOf(wire)),
            };
            w.Add(name, decl, wire);
        }

        private WClass ListToWire(ISerializedArrayList list)
        {
            var source = list.GetItems();
            var items = new object?[source.Length];
            _pending.Enqueue(() =>
            {
                for (var i = 0; i < items.Length; i++) items[i] = ToWire(source[i]);
            });
            var itemsArray = items.Length == 0
                ? _emptyItems.TryGetValue(list.EmptyItemsIdentity, out var shared) ? shared
                    : _emptyItems[list.EmptyItemsIdentity] = new WArray(WArrayKind.Object, items)
                : new WArray(WArrayKind.Object, items);
            if (list is ISerializedEventArrayList eventList)
            {
                var w = Memo(list, new WClass(RcuSchema.EventArrayListType, Lib(Asm.RoboExp), false));
                return w.Add("m_Version", WDecl.Prim(PrimitiveCode.Int32), eventList.Version)
                    .Add("m_OldVersion", WDecl.Prim(PrimitiveCode.Int32), eventList.OldVersion)
                    .Add("Changed", WDecl.Class(RcuSchema.EventHandlerType, null), ToWire(eventList.Changed))
                    .Add("ArrayList+_items", WDecl.ObjectArray, itemsArray)
                    .Add("ArrayList+_size", WDecl.Prim(PrimitiveCode.Int32), list.Count)
                    .Add("ArrayList+_version", WDecl.Prim(PrimitiveCode.Int32), list.InternalVersion);
            }
            return Memo(list, new WClass(RcuSchema.ArrayListType, null, false))
                .Add("_items", WDecl.ObjectArray, itemsArray)
                .Add("_size", WDecl.Prim(PrimitiveCode.Int32), list.Count)
                .Add("_version", WDecl.Prim(PrimitiveCode.Int32), list.InternalVersion);
        }

        private WClass DelegateToWire(SerializedDelegate d)
        {
            var w = Memo(d, new WClass(RcuSchema.DelegateHolderType, null, false));
            _pending.Enqueue(() =>
            {
                foreach (var (name, value) in d.Members) AddDynamic(w, name, value);
            });
            return w;
        }

        private WClass EntryToWire(SerializedDelegateEntry e)
        {
            var w = Memo(e, new WClass(RcuSchema.DelegateEntryType, null, false));
            _pending.Enqueue(() => w.Add("type", WDecl.String, e.Type)
                .Add("assembly", WDecl.String, e.Assembly)
                .Add("target", WDecl.Object, ToWire(e.Target))
                .Add("targetTypeAssembly", WDecl.String, e.TargetTypeAssembly)
                .Add("targetTypeName", WDecl.String, e.TargetTypeName)
                .Add("methodName", WDecl.String, e.MethodName)
                .Add("delegateEntry", WDecl.Class(RcuSchema.DelegateEntryType, null), ToWire(e.DelegateEntry)));
            return w;
        }

        private WClass UnknownToWire(UnknownObject u)
        {
            var w = Memo(u, new WClass(u.TypeName, u.AssemblyName, false));
            _pending.Enqueue(() =>
            {
                foreach (var (name, value) in u.Members) AddDynamic(w, name, value);
            });
            return w;
        }
    }
}
