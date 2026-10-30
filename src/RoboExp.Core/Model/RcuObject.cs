namespace RoboExp.Core.Model;

/// <summary>
/// Base of every persisted class of the .rcu object graph.
/// </summary>
/// <remarks>
/// .rcu files span RoboEXP 1.0 (2012) .. 6.0.8; several classes changed their serialized member set over time
/// (e.g. <c>ParameterClass</c> v100 lacks <c>m_NameCHS/m_NameCHT/m_NameDefault</c>). <see cref="Rcu.RcuReader"/> records
/// the member order actually found in the file when it differs from the current (6.0.8) layout, and keeps members this
/// port does not model, so <see cref="Rcu.RcuWriter"/> re-emits the object exactly as read. Objects created in memory
/// leave both null and are written in the 6.0.8 <c>GetObjectData</c> layout.
/// </remarks>
public abstract class RcuObject
{
    /// <summary>Serialized member names in file order, when not the current layout; null = current layout.</summary>
    public IReadOnlyList<string>? SerializedMemberOrder { get; set; }

    /// <summary>Members present in the file but not modelled by this port (values as produced by the reader).</summary>
    public Dictionary<string, object?>? UnknownMembers { get; set; }
}

/// <summary>
/// A class record whose type this port does not know. Members keep file order; values are primitives, strings,
/// arrays, model objects or other <see cref="UnknownObject"/>s.
/// </summary>
public sealed class UnknownObject
{
    public UnknownObject(string typeName, string? assemblyName)
    {
        TypeName = typeName;
        AssemblyName = assemblyName;
    }

    /// <summary>Full .NET type name as serialized.</summary>
    public string TypeName { get; }

    /// <summary>Assembly display name; null for mscorlib types.</summary>
    public string? AssemblyName { get; }

    public List<KeyValuePair<string, object?>> Members { get; } = [];
}
