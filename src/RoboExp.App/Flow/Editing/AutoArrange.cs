using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;

namespace RoboExp.App.Flow.Editing;

/// <summary>
/// Port of <c>RoboExp.AutoArrayGraphicObjects</c> (toolbar "AutoAlign"): every "begin" object starts a row at x=32,
/// following icons are placed 52px apart in flow order (else branches one row of 60px lower), leftovers get their own
/// row, then every wire leaving an Out/Start/Begin* port is deleted and re-created with default routing.
/// Quirks kept: text notes count as row starts (their GraphicType is Default, not Text); dangling wires count as begin
/// objects and are moved too. Scratch state is local (the original reused CodeObject fields).
/// </summary>
public sealed class AutoArrange
{
    private const int InitX = 32, InitY = 32, AddX = 52, AddY = 60, AddSectHeight = 100;

    private sealed class Scope
    {
        public required GraphicObject Owner;
        public Scope? Parent;
        public int Type;
    }

    private sealed class State
    {
        public bool Used;
        public GraphicObject? EndIf, EndElse, EndWhile, EndIfBeginIf, EndIfBeginElse;
        public bool Invalid;
    }

    private readonly FlowEditor _editor;
    private readonly ConnectingLines _wire;
    private readonly Dictionary<GraphicObject, State> _state = new(ReferenceEqualityComparer.Instance);
    private readonly List<(Port Start, Port End)> _linePorts = [];
    private int _maxY;
    private Point _cur;

    public AutoArrange(FlowEditor editor, ConnectingLines wire)
    {
        _editor = editor;
        _wire = wire;
    }

    private State S(GraphicObject go)
    {
        if (!_state.TryGetValue(go, out var s)) _state[go] = s = new State();
        return s;
    }

    private static bool IsLine(GraphicObject go) => go.DrawObject is LineDraw;
    private static bool IsIcon(GraphicObject go) => go.DrawObject is IconDraw;
    private static GraphicObject Owner(Port p) => p.Parent!.Parent!;

    /// <summary>AutoArrayGraphicObjects.Refresh. Returns the first Start icon (the original then scrolls it into view).</summary>
    public GraphicObject? Refresh()
    {
        var objects = _editor.Objects;
        foreach (var go in objects) S(go);
        foreach (var go in objects.ToList())
        {
            if (!IsBegin(go)) continue;
            _cur = _maxY > 0 ? new Point(InitX, _maxY + AddSectHeight) : new Point(InitX, InitY);
            SetLocation(go, _cur);
            RefreshAll(go, null, null);
        }
        var n = 0;
        foreach (var go in objects.ToList())
        {
            if (S(go).Used) continue;
            if (n == 0)
            {
                n++;
                _cur = _maxY > 0 ? new Point(InitX, _maxY + AddSectHeight) : new Point(InitX, InitY);
            }
            else if (go.DrawObject is IconDraw { IconType: IconType.Start or IconType.BeginIf or IconType.BeginWhile
                         or IconType.EndWhile or IconType.BeginFor or IconType.EndFor })
            {
                _cur = new Point(_cur.X + AddX, _cur.Y);
            }
            SetLocation(go, _cur);
            RefreshAll(go, null, null);
        }

        _linePorts.Clear();
        foreach (var icon in objects.Where(IsIcon).ToList())
        {
            foreach (var port in icon.DrawObject!.Ports!.ToList())
            {
                if (port.Type is not (PortType.Out or PortType.Start or PortType.BeginIf or PortType.BeginFor or PortType.BeginWhile or PortType.BeginElse))
                    continue;
                if (port.ConnectedPort != null && IsLine(Owner(port.ConnectedPort))) ReSetLine(Owner(port.ConnectedPort), port.ConnectedPort);
            }
        }
        foreach (var (start, end) in _linePorts) _editor.AddLine(_wire, start, end);
        return objects.FirstOrDefault(o => o.DrawObject is IconDraw { IconType: IconType.Start });
    }

    private void SetLocation(GraphicObject go, Point location)
    {
        var d = go.DrawObject!;
        FlowEditor.MoveOffset(d, location.X - d.Position.X, location.Y - d.Position.Y);
        if (location.Y > _maxY) _maxY = location.Y;
        S(go).Used = true;
    }

    private void RefreshAll(GraphicObject go, Scope? scope, Port? connectPort)
    {
        if (IsLine(go))
        {
            var s = S(go);
            if (s.Used)
            {
                SetLineInvalid(go);
                return;
            }
            s.Used = true;
            foreach (var p in go.DrawObject!.Ports!)
            {
                if (ReferenceEquals(p, connectPort)) continue;
                if (p.ConnectedPort != null) RefreshAll(Owner(p.ConnectedPort), scope, p.ConnectedPort);
                break;
            }
            return;
        }
        if (go.DrawObject is not IconDraw icon) return;
        switch (icon.IconType)
        {
            case IconType.Calculate or IconType.Continue or IconType.Break or IconType.Return or IconType.CodeEditor or IconType.Function:
                _cur = new Point(_cur.X + AddX, _cur.Y);
                SetLocation(go, _cur);
                foreach (var p in icon.Ports!)
                    if (p.Type == PortType.Out && p.ConnectedPort != null) RefreshAll(Owner(p.ConnectedPort), scope, p.ConnectedPort);
                break;
            case IconType.BeginWhile:
                RefreshLoop(go, scope, PortType.BeginWhile, 1);
                break;
            case IconType.BeginFor:
                RefreshLoop(go, scope, PortType.BeginFor, 4);
                break;
            case IconType.BeginIf:
                RefreshBeginIfElse(go, scope);
                break;
            case IconType.Start:
                RefreshBegin(go, scope);
                break;
            case IconType.EndIf:
                RefreshEndIfElse(go, scope, connectPort);
                break;
            case IconType.EndWhile:
                RefreshEndLoop(go, scope, PortType.EndWhile, 1);
                break;
            case IconType.EndFor:
                RefreshEndLoop(go, scope, PortType.EndFor, 4);
                break;
        }
    }

    private void RefreshBegin(GraphicObject go, Scope? scope)
    {
        var frame = new Scope { Owner = go, Parent = scope, Type = 0 };
        foreach (var p in go.DrawObject!.Ports!)
        {
            if (p.Type != PortType.Start || p.ConnectedPort == null) continue;
            _cur = new Point(_cur.X + AddX, _cur.Y);
            SetLocation(go, _cur);
            RefreshAll(Owner(p.ConnectedPort), frame, p.ConnectedPort);
        }
    }

    private void RefreshLoop(GraphicObject go, Scope? scope, PortType bodyPort, int type)
    {
        if (!S(go).Used)
        {
            _cur = new Point(_cur.X + AddX, _cur.Y);
            SetLocation(go, _cur);
        }
        var frame = new Scope { Owner = go, Parent = scope, Type = type };
        foreach (var p in go.DrawObject!.Ports!)
            if (p.Type == bodyPort && p.ConnectedPort != null) RefreshAll(Owner(p.ConnectedPort), frame, p.ConnectedPort);
    }

    private void RefreshEndLoop(GraphicObject go, Scope? scope, PortType endPort, int type)
    {
        if (!S(go).Used)
        {
            _cur = new Point(_cur.X + AddX, _cur.Y);
            SetLocation(go, _cur);
        }
        foreach (var p in go.DrawObject!.Ports!)
        {
            if (p.Type == endPort)
            {
                if (scope == null) continue;
                S(scope.Owner).EndWhile = go;
                if (scope.Type != type && p.ConnectedPort != null) SetLineInvalid(Owner(p.ConnectedPort));
            }
            else if (p.Type == PortType.Out && p.ConnectedPort != null)
            {
                RefreshAll(Owner(p.ConnectedPort), scope?.Parent, p.ConnectedPort);
            }
        }
    }

    private void RefreshBeginIfElse(GraphicObject go, Scope? scope)
    {
        if (!S(go).Used)
        {
            _cur = new Point(_cur.X + AddX, _cur.Y);
            SetLocation(go, _cur);
        }
        var frame = new Scope { Owner = go, Parent = scope };
        foreach (var p in go.DrawObject!.Ports!)
        {
            switch (p.Type)
            {
                case PortType.BeginIf:
                    frame.Type = 2;
                    if (p.ConnectedPort != null) RefreshAll(Owner(p.ConnectedPort), frame, p.ConnectedPort);
                    break;
                case PortType.BeginElse:
                    frame.Type = 3;
                    if (p.ConnectedPort != null)
                    {
                        _cur = new Point(go.DrawObject.Position.X, go.DrawObject.Position.Y + AddY);
                        RefreshAll(Owner(p.ConnectedPort), frame, p.ConnectedPort);
                    }
                    break;
            }
        }
    }

    private void RefreshEndIfElse(GraphicObject go, Scope? scope, Port? connectPort)
    {
        var d = go.DrawObject!;
        var s = S(go);
        if (!s.Used || _cur.X + AddX > d.Position.X)
        {
            _cur = scope != null ? new Point(_cur.X + AddX, scope.Owner.DrawObject!.Position.Y) : new Point(_cur.X + AddX, _cur.Y);
            SetLocation(go, _cur);
        }
        else
        {
            _cur = d.Position;
        }
        foreach (var p in d.Ports!)
        {
            switch (p.Type)
            {
                case PortType.EndIf:
                    if (!ReferenceEquals(p, connectPort) || scope == null) break;
                    if (scope.Type != 2)
                    {
                        if (p.ConnectedPort != null) SetLineInvalid(Owner(p.ConnectedPort));
                        break;
                    }
                    if (s.EndIfBeginIf == null)
                    {
                        s.EndIfBeginIf = scope.Owner;
                        S(scope.Owner).EndIf = go;
                    }
                    if (s.EndIfBeginElse != null && !ReferenceEquals(s.EndIfBeginIf, s.EndIfBeginElse) && p.ConnectedPort != null)
                        SetLineInvalid(Owner(p.ConnectedPort));
                    break;
                case PortType.EndElse:
                    if (!ReferenceEquals(p, connectPort) || scope == null) break;
                    if (scope.Type != 3)
                    {
                        if (p.ConnectedPort != null) SetLineInvalid(Owner(p.ConnectedPort));
                        break;
                    }
                    if (s.EndIfBeginElse == null)
                    {
                        s.EndIfBeginElse = scope.Owner;
                        S(scope.Owner).EndElse = go;
                    }
                    if (s.EndIfBeginIf != null && !ReferenceEquals(s.EndIfBeginElse, s.EndIfBeginIf) && p.ConnectedPort != null)
                        SetLineInvalid(Owner(p.ConnectedPort));
                    break;
                case PortType.Out:
                    if (scope != null)
                    {
                        bool ifDone = false, elseDone = false;
                        var begin = S(scope.Owner);
                        foreach (var bp in scope.Owner.DrawObject!.Ports!)
                        {
                            // The original dereferences the Begin icon's EndIf/EndElse unguarded (a NullReferenceException
                            // aborted the whole layout); a missing pairing is treated as "branch not finished" instead.
                            if (bp.Type == PortType.BeginIf)
                                ifDone |= bp.ConnectedPort == null || (s.EndIfBeginIf != null && ReferenceEquals(begin.EndIf, go));
                            if (bp.Type == PortType.BeginElse)
                                elseDone |= bp.ConnectedPort == null || (s.EndIfBeginElse != null && ReferenceEquals(begin.EndElse, go));
                        }
                        if (ifDone && elseDone && p.ConnectedPort != null) RefreshAll(Owner(p.ConnectedPort), scope.Parent, p.ConnectedPort);
                    }
                    else if (p.ConnectedPort != null)
                    {
                        RefreshAll(Owner(p.ConnectedPort), null, p.ConnectedPort);
                    }
                    break;
            }
        }
    }

    private void ReSetLine(GraphicObject line, Port inPort)
    {
        var start = inPort.ConnectedPort;
        var end = GetEndPortAndDelLine(line, inPort);
        if (start != null && end != null) _linePorts.Add((start, end));
    }

    private Port? GetEndPortAndDelLine(GraphicObject line, Port inPort)
    {
        if (!IsLine(line)) return null;
        Port? result = null;
        var other = line.DrawObject!.Ports!.FirstOrDefault(p => !ReferenceEquals(p, inPort));
        if (other == null)
        {
            _editor.RemoveGraphicObject(line);
            return null;
        }
        if (other.ConnectedPort != null)
        {
            var next = Owner(other.ConnectedPort);
            if (IsLine(next)) result = GetEndPortAndDelLine(next, other.ConnectedPort);
            else if (IsIcon(next)) result = other.ConnectedPort;
        }
        _editor.RemoveGraphicObject(line);
        return result;
    }

    private void SetLineInvalid(GraphicObject go)
    {
        if (IsLine(go)) S(go).Invalid = true;
        foreach (var p in go.DrawObject?.Ports ?? [])
        {
            if (p.ConnectedPort == null) continue;
            var next = Owner(p.ConnectedPort);
            if (IsLine(next) && !S(next).Invalid) SetLineInvalid(next);
        }
    }

    /// <summary>AutoArrayGraphicObjects.GraphicObjectIsBegin.</summary>
    private static bool IsBegin(GraphicObject go)
    {
        bool flag = true, ifConnected = true, elseConnected = true;
        switch (go.DrawObject)
        {
            case IconDraw icon:
                if (icon.IconType == IconType.Start) return true;
                foreach (var p in icon.Ports!)
                {
                    if (!flag) break;
                    switch (p.Type)
                    {
                        case PortType.In or PortType.EndIf or PortType.EndFor or PortType.EndWhile or PortType.EndElse:
                            if (p.ConnectedPort != null) flag = false;
                            break;
                        case PortType.BeginElse:
                            if (p.ConnectedPort == null) elseConnected = false;
                            break;
                        case PortType.Out or PortType.BeginFor or PortType.BeginWhile:
                            if (p.ConnectedPort == null) flag = false;
                            break;
                        case PortType.BeginIf:
                            if (p.ConnectedPort == null) ifConnected = false;
                            break;
                    }
                }
                break;
            case LineDraw line:
                foreach (var p in line.Ports!)
                {
                    if (!flag) break;
                    if (p.Type is PortType.In or PortType.EndIf or PortType.EndWhile or PortType.EndElse && p.ConnectedPort != null) flag = false;
                }
                break;
        }
        return flag && ifConnected && elseConnected;
    }
}
