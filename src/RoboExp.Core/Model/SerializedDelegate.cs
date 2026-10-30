namespace RoboExp.Core.Model;

/// <summary>
/// Opaque <c>System.DelegateSerializationHolder</c>: a serialized event subscriber list.
/// </summary>
/// <remarks>
/// Every vendor file carries these on its <c>EventArrayList.Changed</c> fields: the original's
/// <c>ProjectData.OnDeserialization</c> subscribes <c>VarListChangedHandle</c> / <c>HardwareItemListChangedHandle</c> /
/// <c>UsedProjectInfoListChangedHandle</c> after each load, and BinaryFormatter persists them on save (so the
/// invocation list grows by one per load/save cycle). They carry no project data. The port keeps them verbatim so
/// files round-trip byte-for-byte; objects created in memory have none (a null event field, which the original
/// accepts and re-subscribes on load).
/// Members: <c>Delegate</c> (<see cref="SerializedDelegateEntry"/> chain) then <c>targetN</c> (subscriber object,
/// a model object such as <see cref="ProjectData"/>) and <c>methodN</c> (<see cref="SerializedMemberInfo"/>),
/// in serialized order. .NET 1.1 files have only <c>Delegate</c>.
/// </remarks>
public sealed class SerializedDelegate
{
    public List<KeyValuePair<string, object?>> Members { get; } = [];

    public SerializedDelegateEntry? Delegate =>
        Members.FirstOrDefault(m => m.Key == "Delegate").Value as SerializedDelegateEntry;

    /// <summary>Handler method names, head of the invocation chain first.</summary>
    public IEnumerable<string?> MethodNames
    {
        get
        {
            for (var e = Delegate; e != null; e = e.DelegateEntry) yield return e.MethodName;
        }
    }
}

/// <summary><c>System.DelegateSerializationHolder+DelegateEntry</c> (serialized field-by-field).</summary>
public sealed class SerializedDelegateEntry
{
    /// <summary><c>type</c>: delegate type name, e.g. <c>System.EventHandler</c>.</summary>
    public string? Type { get; set; }

    /// <summary><c>assembly</c>: delegate type assembly (mscorlib display name).</summary>
    public string? Assembly { get; set; }

    /// <summary><c>target</c>: "targetN" key into the holder (.NET 2+), or the target object itself (.NET 1.1).</summary>
    public object? Target { get; set; }

    public string? TargetTypeAssembly { get; set; }
    public string? TargetTypeName { get; set; }
    public string? MethodName { get; set; }

    /// <summary><c>delegateEntry</c>: next entry of the invocation list.</summary>
    public SerializedDelegateEntry? DelegateEntry { get; set; }
}

/// <summary><c>System.Reflection.MemberInfoSerializationHolder</c> (a serialized <c>MethodInfo</c>).</summary>
public sealed class SerializedMemberInfo : RcuObject
{
    public string? Name { get; set; }
    public string? AssemblyName { get; set; }
    public string? ClassName { get; set; }
    public string? Signature { get; set; }

    /// <summary>Only written by .NET 4.x (<c>Signature2</c>); absent in .NET 2.0 files.</summary>
    public string? Signature2 { get; set; }

    public int MemberType { get; set; }

    /// <summary><c>System.Type[]</c>; always null for the event handlers found in .rcu files.</summary>
    public object? GenericArguments { get; set; }
}
