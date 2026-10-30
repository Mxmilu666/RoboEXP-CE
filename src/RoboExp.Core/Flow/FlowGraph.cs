using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;

namespace RoboExp.Core.Flow;

/// <summary><c>GraphicObject.GraphicType</c>: Null without a DrawObject, Line/Icon by class, Default otherwise (text notes).</summary>
public enum GraphicKind { Null, Default, Icon, Line }

/// <summary><c>LineCode.LineState</c>: Invalid wires are drawn grey and never reached by code generation.</summary>
public enum LineState { Normal, Invalid }

/// <summary>
/// Runtime-only fields of the original <c>CodeObject</c>/<c>LineCode</c>/<c>IconDraw.IsInCode</c>: recomputed by
/// <see cref="FlowGraph.RefreshLineValidity"/> and code generation, never persisted.
/// </summary>
public sealed class FlowNodeState
{
    public bool Used { get; internal set; }

    /// <summary>Icon reached by code generation (drawn normally; others greyed).</summary>
    public bool IsInCode { get; internal set; }

    /// <summary>1-based line in &lt;project&gt;.c after generation; -1 when not emitted. Start maps to the declaration line.</summary>
    public int BeginLine { get; internal set; } = -1;

    public int EndLine { get; internal set; } = -1;
    public LineState LineState { get; internal set; }

    /// <summary>BeginIf: matching EndIf reached through the if branch.</summary>
    public GraphicObject? EndIf { get; internal set; }

    /// <summary>BeginIf: matching EndIf reached through the else branch.</summary>
    public GraphicObject? EndElse { get; internal set; }

    /// <summary>BeginWhile/BeginFor: matching EndWhile/EndFor.</summary>
    public GraphicObject? EndWhile { get; internal set; }

    /// <summary>EndIf: BeginIf of its if branch.</summary>
    public GraphicObject? EndIfBeginIf { get; internal set; }

    /// <summary>EndIf: BeginIf of its else branch.</summary>
    public GraphicObject? EndIfBeginElse { get; internal set; }

    /// <summary>BeginIf: the else branch was emitted.</summary>
    public bool UseElse { get; internal set; }
}

/// <summary>
/// Graph semantics of a flowchart (<c>DesignSurfaceData.DrawingObjects</c>) in stored order: kinds, port traversal
/// (BuildCode.GetGraphicObject), the GraphicsValidity Begin/End pairing pre-pass, and per-object runtime state.
/// The collection is not copied: edits to it are seen by the next refresh/generation.
/// </summary>
public sealed class FlowGraph
{
    private readonly Dictionary<GraphicObject, FlowNodeState> _state = new(ReferenceEqualityComparer.Instance);

    public FlowGraph(GraphicObjectCollection? drawingObjects)
    {
        DrawingObjects = drawingObjects ?? new GraphicObjectCollection();
        DrawingObjects.List ??= new();
    }

    public GraphicObjectCollection DrawingObjects { get; }

    /// <summary>Objects in stored order (the order every original loop uses).</summary>
    public IReadOnlyList<GraphicObject> Objects => DrawingObjects.List!;

    public FlowNodeState State(GraphicObject go)
    {
        if (!_state.TryGetValue(go, out var s)) _state[go] = s = new FlowNodeState();
        return s;
    }

    public static GraphicKind KindOf(GraphicObject go) => go.DrawObject switch
    {
        null => GraphicKind.Null,
        LineDraw => GraphicKind.Line,
        IconDraw => GraphicKind.Icon,
        _ => GraphicKind.Default,
    };

    public static IconType? IconTypeOf(GraphicObject go) => (go.DrawObject as IconDraw)?.IconType;

    /// <summary>The graphic object owning <paramref name="port"/> (<c>port.Parent.Parent</c>).</summary>
    public static GraphicObject? OwnerOf(Port? port) => port?.Parent?.Parent;

    public static IEnumerable<Port> PortsOf(GraphicObject go) => (IEnumerable<Port>?)go.DrawObject?.Ports ?? [];

    public IEnumerable<GraphicObject> StartIcons => Objects.Where(o => IconTypeOf(o) == IconType.Start);

    /// <summary>
    /// FlowView.SelectLine: the object whose BeginLine is <paramref name="line"/>; the last match wins unless a
    /// BeginIf/BeginWhile/BeginFor matches first.
    /// </summary>
    public GraphicObject? FindObjectAtLine(int line)
    {
        GraphicObject? result = null;
        foreach (var go in Objects)
        {
            if (State(go).BeginLine != line) continue;
            result = go;
            if (go.DataObject is BeginIfData or BeginWhileData or BeginForData) break;
        }
        return result;
    }

    /// <summary>BuildCode.InitLine.</summary>
    internal void InitLine()
    {
        foreach (var go in Objects)
        {
            var s = State(go);
            s.BeginLine = -1;
            s.EndLine = -1;
            s.Used = false;
            if (go.DrawObject is IconDraw) s.IsInCode = false;
        }
    }

    /// <summary>
    /// BuildCode.GetGraphicObject: the icon reached from <paramref name="port"/> of <paramref name="from"/> following the wire
    /// chain. Null for an unconnected port, a self connection, a dangling wire or a wire already used in this generation.
    /// Marks traversed wires Used.
    /// </summary>
    public GraphicObject? Follow(GraphicObject from, Port port)
    {
        if (port.ConnectedPort == null) return null;
        var parent = OwnerOf(port.ConnectedPort);
        if (parent == null || ReferenceEquals(parent, from)) return null;
        switch (KindOf(parent))
        {
            case GraphicKind.Icon:
                return parent;
            case GraphicKind.Line:
            {
                var s = State(parent);
                if (s.Used) return null;
                s.Used = true;
                GraphicObject? result = null;
                foreach (var p in PortsOf(parent))
                {
                    if (p.ConnectedPort == null) return null;
                    if (!ReferenceEquals(OwnerOf(p.ConnectedPort), from)) result = Follow(parent, p);
                }
                return result;
            }
            default:
                return null;
        }
    }

    /// <summary>BuildCode.GetOutPort: BeginWhile/BeginFor body port, none for Start/BeginIf, else the Out port.</summary>
    public static Port? GetOutPort(GraphicObject go)
    {
        var want = IconTypeOf(go) switch
        {
            IconType.BeginWhile => PortType.BeginWhile,
            IconType.BeginFor => PortType.BeginFor,
            IconType.Start or IconType.BeginIf => (PortType?)null,
            _ => PortType.Out,
        };
        return want == null ? null : FindPort(go, want.Value);
    }

    public static Port? FindPort(GraphicObject go, PortType type) => PortsOf(go).FirstOrDefault(p => p.Type == type);

    /// <summary>
    /// GraphicsValidity.RefreshLineValidity: clears pairing state, walks from every begin object (Start icons and
    /// objects with no connected incoming port), then from everything not yet visited, pairing Begin*/End* icons and
    /// marking wires Invalid on mismatches or revisits.
    /// </summary>
    public void RefreshLineValidity()
    {
        foreach (var go in Objects)
        {
            var s = State(go);
            s.Used = false;
            s.EndIfBeginElse = null;
            s.EndIfBeginIf = null;
            s.EndElse = null;
            s.EndIf = null;
            s.EndWhile = null;
            s.UseElse = false;
            if (KindOf(go) == GraphicKind.Line) s.LineState = LineState.Normal;
        }
        foreach (var go in Objects.ToArray())
            if (IsBegin(go)) RefreshAll(go, null, null);
        foreach (var go in Objects.ToArray())
            if (!State(go).Used) RefreshAll(go, null, null);
    }

    private sealed class Scope(GraphicObject owner, Scope? parent, int type)
    {
        public GraphicObject Owner { get; } = owner;
        public Scope? Parent { get; } = parent;

        /// <summary>0 Start, 1 While, 2 If branch, 3 Else branch, 4 For.</summary>
        public int Type { get; set; } = type;

        public bool EndIfElseOut { get; set; }
    }

    private void Next(Port port, Scope? scope)
    {
        if (port.ConnectedPort != null && OwnerOf(port.ConnectedPort) is { } next) RefreshAll(next, scope, port.ConnectedPort);
    }

    private void RefreshAll(GraphicObject go, Scope? scope, Port? connectPort)
    {
        switch (KindOf(go))
        {
            case GraphicKind.Line:
            {
                var s = State(go);
                if (s.Used)
                {
                    SetLineInvalid(go);
                    break;
                }
                s.Used = true;
                foreach (var p in PortsOf(go))
                {
                    if (ReferenceEquals(p, connectPort)) continue;
                    Next(p, scope);
                    break;
                }
                break;
            }
            case GraphicKind.Icon:
                State(go).Used = true;
                switch (IconTypeOf(go))
                {
                    case IconType.Calculate or IconType.Continue or IconType.Break or IconType.Return or IconType.CodeEditor or IconType.Function:
                        foreach (var p in PortsOf(go))
                            if (p.Type == PortType.Out) Next(p, scope);
                        break;
                    case IconType.Start:
                        BeginScope(go, scope, 0, PortType.Start);
                        break;
                    case IconType.BeginWhile:
                        BeginScope(go, scope, 1, PortType.BeginWhile);
                        break;
                    case IconType.BeginFor:
                        BeginScope(go, scope, 4, PortType.BeginFor);
                        break;
                    case IconType.BeginIf:
                    {
                        var frame = new Scope(go, scope, 0);
                        foreach (var p in PortsOf(go))
                        {
                            if (p.Type == PortType.BeginIf)
                            {
                                frame.Type = 2;
                                Next(p, frame);
                            }
                            else if (p.Type == PortType.BeginElse)
                            {
                                frame.Type = 3;
                                Next(p, frame);
                            }
                        }
                        break;
                    }
                    case IconType.EndWhile:
                        RefreshEndLoop(go, scope, connectPort, PortType.EndWhile, 1);
                        break;
                    case IconType.EndFor:
                        RefreshEndLoop(go, scope, connectPort, PortType.EndFor, 4);
                        break;
                    case IconType.EndIf:
                        RefreshEndIf(go, scope, connectPort);
                        break;
                }
                break;
        }
    }

    private void BeginScope(GraphicObject go, Scope? scope, int type, PortType bodyPort)
    {
        var frame = new Scope(go, scope, type);
        foreach (var p in PortsOf(go))
            if (p.Type == bodyPort) Next(p, frame);
    }

    private void RefreshEndLoop(GraphicObject go, Scope? scope, Port? endPort, PortType endType, int scopeType)
    {
        foreach (var p in PortsOf(go))
        {
            if (p.Type == endType)
            {
                if (!ReferenceEquals(p, endPort) || scope == null) continue;
                if (scope.Type != scopeType) InvalidateArriving(p);
                else State(scope.Owner).EndWhile = go;
            }
            else if (p.Type == PortType.Out)
            {
                Next(p, scope?.Parent);
            }
        }
    }

    private void RefreshEndIf(GraphicObject go, Scope? scope, Port? connectPort)
    {
        var s = State(go);
        foreach (var p in PortsOf(go))
        {
            switch (p.Type)
            {
                case PortType.EndIf:
                    if (!ReferenceEquals(p, connectPort) || scope == null) break;
                    if (scope.Type != 2)
                    {
                        InvalidateArriving(p);
                        break;
                    }
                    if (s.EndIfBeginIf == null)
                    {
                        s.EndIfBeginIf = scope.Owner;
                        State(scope.Owner).EndIf = go;
                    }
                    if (s.EndIfBeginElse != null && !ReferenceEquals(s.EndIfBeginIf, s.EndIfBeginElse))
                    {
                        InvalidateArriving(p);
                        s.EndIfBeginIf = null;
                        State(scope.Owner).EndIf = null;
                    }
                    break;
                case PortType.EndElse:
                    if (!ReferenceEquals(p, connectPort) || scope == null) break;
                    if (scope.Type != 3)
                    {
                        InvalidateArriving(p);
                        break;
                    }
                    if (s.EndIfBeginElse == null)
                    {
                        s.EndIfBeginElse = scope.Owner;
                        State(scope.Owner).EndElse = go;
                    }
                    if (s.EndIfBeginIf != null && !ReferenceEquals(s.EndIfBeginElse, s.EndIfBeginIf))
                    {
                        InvalidateArriving(p);
                        s.EndIfBeginElse = null;
                        State(scope.Owner).EndElse = null;
                    }
                    break;
                case PortType.Out:
                    if (scope != null)
                    {
                        if (scope.EndIfElseOut) break;
                        scope.EndIfElseOut = true;
                    }
                    Next(p, scope?.Parent);
                    break;
            }
        }
    }

    private void InvalidateArriving(Port p)
    {
        if (OwnerOf(p.ConnectedPort) is { } wire) SetLineInvalid(wire);
    }

    private void SetLineInvalid(GraphicObject go)
    {
        if (KindOf(go) == GraphicKind.Line) State(go).LineState = LineState.Invalid;
        foreach (var p in PortsOf(go))
        {
            if (OwnerOf(p.ConnectedPort) is { } other && KindOf(other) == GraphicKind.Line && State(other).LineState != LineState.Invalid)
                SetLineInvalid(other);
        }
    }

    /// <summary>GraphicsValidity.GraphicObjectIsBegin.</summary>
    private static bool IsBegin(GraphicObject go)
    {
        switch (KindOf(go))
        {
            case GraphicKind.Icon:
                if (IconTypeOf(go) == IconType.Start) return true;
                return !PortsOf(go).Any(p => p.Type is PortType.In or PortType.EndIf or PortType.EndFor or PortType.EndWhile or PortType.EndElse
                                             && p.ConnectedPort != null);
            case GraphicKind.Line:
                return !PortsOf(go).Any(p => p.Type is PortType.In or PortType.EndIf or PortType.EndWhile or PortType.EndElse
                                             && p.ConnectedPort != null);
            default:
                return true;
        }
    }
}
