using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;
using static RoboExp.App.Flow.Editing.FlowGeometry;

namespace RoboExp.App.Flow.Editing;

/// <summary>
/// The model mutations of the original <c>DesignSurface</c> + <c>UndoManager</c> (RoboExp.DesignSurface): add/remove,
/// move, connect/disconnect, <c>MoveLineTo</c>, wire re-routing after moves (<c>UpdateConnectLinesPos</c>,
/// <c>UpdateTwoSidePos</c>), wire simplification (<c>CombineLines</c>: DeleteBalanceLines + DeleteCrossLines),
/// dangling wire removal, alignment and clipboard cloning. Undo is snapshot based (<see cref="FlowUndoManager"/>), so
/// these operations apply directly; their effects (including the original's quirks) are otherwise identical.
/// </summary>
public sealed class FlowEditor
{
    private readonly HashSet<LineDraw> _hasCheckCombine = new(ReferenceEqualityComparer.Instance);

    public FlowEditor(IList<GraphicObject> objects, IList<GraphicObject> selection)
    {
        Objects = objects;
        Selection = selection;
    }

    /// <summary>DrawingObjects (stored order = z-order = codegen order).</summary>
    public IList<GraphicObject> Objects { get; }

    /// <summary>SelectedObjects; removals also drop the object from it (UndoManager.DoRemoveStep).</summary>
    public IList<GraphicObject> Selection { get; }

    /// <summary>Raised when <see cref="AddGraphicObject"/> refuses a 10th Start icon (original message box "最多8个").</summary>
    public event EventHandler? StartLimitReached;

    // ------------------------------------------------------------------ primitive steps

    /// <summary>
    /// UndoManager.AddGraphicObject. Quirk kept: it refuses a Start icon only when more than 8 already exist.
    /// </summary>
    public bool AddGraphicObject(GraphicObject go)
    {
        var starts = Objects.Count(o => o.DrawObject is IconDraw { IconType: IconType.Start });
        if (starts > 8 && go.DrawObject is IconDraw { IconType: IconType.Start })
        {
            StartLimitReached?.Invoke(this, EventArgs.Empty);
            return false;
        }
        Objects.Add(go);
        return true;
    }

    /// <summary>UndoManager.DoRemoveStep: neighbours forget this object's ports; the object keeps its own links.</summary>
    public void RemoveGraphicObject(GraphicObject go)
    {
        if (go.DrawObject is not { } d) return;
        foreach (var p in d.Ports ?? [])
            if (p.ConnectedPort != null) p.ConnectedPort.ConnectedPort = null;
        Objects.Remove(go);
        Selection.Remove(go);
    }

    /// <summary>DrawObject.MoveOffset (no-op for 0,0 on icons; lines move both ends).</summary>
    public static void MoveOffset(DrawObject d, int dx, int dy)
    {
        if (d is LineDraw l)
        {
            l.Position = new Point(l.Position.X + dx, l.Position.Y + dy);
            l.EndPosition = new Point(l.EndPosition.X + dx, l.EndPosition.Y + dy);
            return;
        }
        if (dx != 0 || dy != 0) d.Position = new Point(d.Position.X + dx, d.Position.Y + dy);
    }

    public static void ConnectTwoPorts(Port? a, Port? b)
    {
        if (a != null) a.ConnectedPort = b;
        if (b != null) b.ConnectedPort = a;
    }

    public static void DisconnectTwoPorts(Port? a, Port? b)
    {
        if (a != null) a.ConnectedPort = null;
        if (b != null) b.ConnectedPort = null;
    }

    /// <summary>
    /// UndoManager.MoveLineTo: the undo step stores deltas and treats <see cref="Point.Empty"/> as "unchanged", so a
    /// target of exactly (0,0) leaves that end where it is (original quirk).
    /// </summary>
    public static void MoveLineTo(LineDraw line, Point startPos, Point endPos)
    {
        if (startPos != Point.Empty) line.Position = startPos;
        if (endPos != Point.Empty) line.EndPosition = endPos;
    }

    /// <summary>MoveLineTo(line, port, pos): moves the end of <paramref name="line"/> that is <paramref name="port"/>.</summary>
    public static void MoveLineTo(LineDraw line, Port? port, Point pos)
    {
        if (port == null || !ReferenceEquals(port.Parent, line)) return;
        if (ReferenceEquals(port, StartPort(line))) MoveLineTo(line, pos, Point.Empty);
        else if (ReferenceEquals(port, EndPort(line))) MoveLineTo(line, Point.Empty, pos);
    }

    // ------------------------------------------------------------------ wire chains

    /// <summary>UndoManager.GetLinesStartPort: walks from <paramref name="port"/> to the far end of the chain.</summary>
    public static Port? GetLinesStartPort(LineDraw line, Port? port)
    {
        if (port == null) return null;
        var next = GetNextLineDraw(line, port);
        var p = port;
        if (next != null) p = GetNextPortByConnectedPort(next, p);
        while (next != null && !ReferenceEquals(next, line))
        {
            next = GetNextLineDraw(next, p);
            if (next != null) p = GetNextPortByConnectedPort(next, p);
        }
        return p;
    }

    /// <summary>DesignSurface.GetConnectLines: every segment of the wire containing <paramref name="line"/>.</summary>
    public static List<GraphicObject> GetConnectLines(LineDraw? line)
    {
        var result = new List<GraphicObject>();
        if (line == null) return result;
        var startPort = GetLinesStartPort(line, StartPort(line));
        if (startPort?.Parent is not LineDraw first) return result;
        var nextPort = GetNextPort(first, startPort);
        var next = GetNextLineDraw(first, nextPort);
        Port? p = null;
        if (next != null) p = GetNextPortByConnectedPort(next, nextPort);
        result.Add(first.Parent!);
        while (next != null && !ReferenceEquals(next, first))
        {
            result.Add(next.Parent!);
            next = GetNextLineDraw(next, p);
            if (next != null) p = GetNextPortByConnectedPort(next, p);
        }
        return result;
    }

    /// <summary>DesignSurface.RemoveInvalidateLines: drops whole wires that are not attached at both ends.</summary>
    public void RemoveInvalidateLines()
    {
        var doomed = new List<GraphicObject>();
        foreach (var go in Objects)
        {
            if (go.DrawObject is not LineDraw l) continue;
            if ((StartPort(l).ConnectedPort != null && EndPort(l).ConnectedPort != null) || doomed.Contains(go)) continue;
            doomed.AddRange(GetConnectLines(l));
        }
        foreach (var go in doomed) RemoveGraphicObject(go);
    }

    private void SetLinesHasCombine(LineDraw line, Port port, bool flag)
    {
        Mark(line, flag);
        var next = GetNextLineDraw(line, port);
        var p = GetNextPortByConnectedPort(line, port);
        while (next != null && !ReferenceEquals(next, line))
        {
            Mark(next, flag);
            next = GetNextLineDraw(next, p);
            if (next != null) p = GetNextPortByConnectedPort(next, p);
        }
    }

    private void Mark(LineDraw line, bool flag)
    {
        if (flag) _hasCheckCombine.Add(line);
        else _hasCheckCombine.Remove(line);
    }

    private void SetLinesHasCombine(LineDraw line, bool flag)
    {
        SetLinesHasCombine(line, StartPort(line), flag);
        SetLinesHasCombine(line, EndPort(line), flag);
    }

    /// <summary>UndoManager.CombineOneLines(line): merge collinear neighbours on both sides, then cut loops.</summary>
    public void CombineOneLines(LineDraw line)
    {
        DeleteBalanceLines(line, StartPort(line));
        DeleteBalanceLines(line, EndPort(line));
        var start = GetLinesStartPort(line, StartPort(line));
        if (start?.Parent is not LineDraw first) return;
        DeleteCrossLines(first, GetNextPort(first, start));
    }

    /// <summary>UndoManager.CombineLines: CombineOneLines for every wire touching the moved objects (once per wire).</summary>
    public void CombineLines(IEnumerable<GraphicObject> moved)
    {
        var lines = new List<LineDraw>();
        foreach (var go in moved.ToList())
        {
            if (go.DrawObject is LineDraw l)
            {
                Mark(l, false);
                lines.Add(l);
                continue;
            }
            foreach (var p in go.DrawObject?.Ports ?? [])
            {
                if (p.ConnectedPort?.Parent is LineDraw pl)
                {
                    Mark(pl, false);
                    lines.Add(pl);
                }
            }
        }
        foreach (var l in lines)
        {
            if (!_hasCheckCombine.Contains(l) && l.Parent != null && Objects.Contains(l.Parent))
            {
                SetLinesHasCombine(l, true);
                CombineOneLines(l);
            }
        }
    }

    /// <summary>UndoManager.DeleteBalanceLines: removes segments collinear with their predecessor, extending it.</summary>
    private void DeleteBalanceLines(LineDraw line, Port port)
    {
        if (!ReferenceEquals(StartPort(line), port) && !ReferenceEquals(EndPort(line), port)) return;
        Port? p = null;
        var cur = line;
        var next = GetNextLineDraw(line, port);
        if (next != null) p = GetNextPortByConnectedPort(next, port);
        while (next != null && !ReferenceEquals(next, line))
        {
            if (IsHorizontal(cur) == IsHorizontal(next))
            {
                var point = ReferenceEquals(StartPort(next), p) ? next.Position : next.EndPosition;
                var farConnected = GetNextPort(next, p!)!.ConnectedPort;
                var atStart = ReferenceEquals(farConnected, StartPort(cur));
                var outer = p!.ConnectedPort;
                var removed = next;
                next = GetNextLineDraw(next, p);
                if (next != null) p = GetNextPortByConnectedPort(next, p);
                RemoveGraphicObject(removed.Parent!);
                if (atStart) MoveLineTo(cur, point, Point.Empty);
                else MoveLineTo(cur, Point.Empty, point);
                if (outer != null) ConnectTwoPorts(farConnected, outer);
            }
            else
            {
                cur = next;
                next = GetNextLineDraw(next, p);
                if (next != null) p = GetNextPortByConnectedPort(next, p);
            }
        }
    }

    /// <summary>UndoManager.DeleteCrossLines: when the wire crosses itself, the loop between the crossing segments is cut out.</summary>
    private void DeleteCrossLines(LineDraw line, Port? port)
    {
        if (port == null) return;
        var a = line;
        var pa = port;
        while (a != null)
        {
            var b = GetNextLineDraw(a, pa);
            Port? pb = null;
            if (b != null) pb = GetNextPortByConnectedPort(b, pa);
            if (b != null) b = GetNextLineDraw(b, pb);
            if (b != null) pb = GetNextPortByConnectedPort(b, pb);
            var stop = false;
            while (b != null)
            {
                if (ReferenceEquals(b, line))
                {
                    stop = true;
                    break;
                }
                if (IntersectsWith(a, b, out var point))
                {
                    var portA = pa;
                    var portB = GetNextPort(b, pb!);
                    var aAtStart = ReferenceEquals(StartPort(a), portA);
                    var bAtStart = ReferenceEquals(StartPort(b), portB);
                    var mid = GetNextLineDraw(a, pa);
                    var pm = GetNextPortByConnectedPort(mid!, pa);
                    while (mid != null && !ReferenceEquals(mid, b))
                    {
                        var removed = mid;
                        mid = GetNextLineDraw(mid, pm);
                        if (mid != null) pm = GetNextPortByConnectedPort(mid, pm);
                        RemoveGraphicObject(removed.Parent!);
                    }
                    if (aAtStart) MoveLineTo(a, point, Point.Empty);
                    else MoveLineTo(a, Point.Empty, point);
                    if (bAtStart) MoveLineTo(b, point, Point.Empty);
                    else MoveLineTo(b, Point.Empty, point);
                    ConnectTwoPorts(portA, portB);
                }
                b = GetNextLineDraw(b, pb);
                if (b != null) pb = GetNextPortByConnectedPort(b, pb);
            }
            if (stop) break;
            a = GetNextLineDraw(a, pa)!;
            if (a != null) pa = GetNextPortByConnectedPort(a, pa)!;
            if (ReferenceEquals(a, line)) break;
        }
        DeleteBalanceLines(line, port);
    }

    // ------------------------------------------------------------------ re-routing after moves

    /// <summary>UndoManager.UpdateTwoSidePos.</summary>
    public void UpdateTwoSidePos(LineDraw? line)
    {
        if (line == null) return;
        UpdateOneSidePos(line, StartPort(line));
        UpdateOneSidePos(line, EndPort(line));
    }

    /// <summary>UndoManager.UpdateOneSidePos: keeps a moved segment attached (and orthogonal) at <paramref name="port"/>.</summary>
    public void UpdateOneSidePos(LineDraw line, Port port)
    {
        if (!ReferenceEquals(port.Parent, line) || IsJoin(port)) return;
        var connected = port.ConnectedPort!;
        var next = GetNextLineDraw(line, port);
        Point pt;
        if (next == null)
        {
            pt = IsHorizontal(line)
                ? new Point(ConnectPoint(connected).X, ConnectPoint(port).Y)
                : new Point(ConnectPoint(port).X, ConnectPoint(connected).Y);
            MoveLineTo(line, port, pt);
            var go = FlowFactory.NewLine(Point.Empty, Point.Empty);
            var bridge = (LineDraw)go.DrawObject!;
            AddGraphicObject(go);
            MoveLineTo(bridge, ConnectPoint(port), ConnectPoint(connected));
            DisconnectTwoPorts(port, port.ConnectedPort);
            ConnectTwoPorts(port, StartPort(bridge));
            ConnectTwoPorts(EndPort(bridge), connected);
            return;
        }
        var far = GetNextPortByConnectedPort(next, port)!.ConnectedPort;
        if (far == null)
        {
            MoveLineOffsetByPort(next, port.ConnectedPort);
            return;
        }
        if (far.Parent is not LineDraw farLine)
        {
            pt = IsHorizontal(line)
                ? new Point(ConnectPoint(port.ConnectedPort!).X, ConnectPoint(port).Y)
                : new Point(ConnectPoint(port).X, ConnectPoint(port.ConnectedPort!).Y);
            MoveLineTo(line, port, pt);
            MoveLineTo(next, port.ConnectedPort, pt);
            return;
        }
        pt = IsHorizontal(line)
            ? new Point(ConnectPoint(port).X, ConnectPoint(far).Y)
            : new Point(ConnectPoint(far).X, ConnectPoint(port).Y);
        MoveLineTo(next, port.ConnectedPort, ConnectPoint(port));
        MoveLineTo(next, far.ConnectedPort, pt);
        MoveLineTo(farLine, far, pt);
    }

    /// <summary>
    /// UndoManager.UpdateConnectLinesPos: after an icon moved, drags the first segment of each attached wire along, or
    /// inserts two segments to keep a single straight segment orthogonal.
    /// </summary>
    public void UpdateConnectLinesPos(DrawObject? icon)
    {
        if (icon == null || icon is LineDraw) return;
        foreach (var port in (icon.Ports ?? []).ToList())
        {
            if (IsJoin(port) || port.ConnectedPort!.Parent is not LineDraw line) continue;
            var nextPort = GetNextPortByConnectedPort(line, port)!;
            var nextLine = GetNextLineDraw(line, nextPort);
            if (nextPort.ConnectedPort == null)
            {
                MoveLineOffsetByPort(line, port.ConnectedPort);
                continue;
            }
            if (nextLine != null)
            {
                if (!IsJoin(nextPort))
                {
                    MoveLineOffsetByPort(line, port.ConnectedPort);
                    continue;
                }
                var pt = IsHorizontal(line)
                    ? new Point(ConnectPoint(nextPort).X, ConnectPoint(port).Y)
                    : new Point(ConnectPoint(port).X, ConnectPoint(nextPort).Y);
                MoveLineOffsetByPort(line, port.ConnectedPort);
                MoveLineTo(line, nextPort, pt);
                MoveLineTo(nextLine, nextPort.ConnectedPort, pt);
                continue;
            }
            if (!IsJoin(nextPort))
            {
                MoveLineOffsetByPort(line, port.ConnectedPort);
                continue;
            }
            var cp = ConnectPoint(port);
            var cp2 = ConnectPoint(nextPort.ConnectedPort);
            Point e1, e2;
            if (IsHorizontal(line))
            {
                e1 = new Point((cp.X + cp2.X) / 2, cp.Y);
                e2 = new Point(e1.X, cp2.Y);
            }
            else
            {
                e1 = new Point(cp.X, (cp.Y + cp2.Y) / 2);
                e2 = new Point(cp2.X, e1.Y);
            }
            var g1 = FlowFactory.NewLine(Point.Empty, Point.Empty);
            var g2 = FlowFactory.NewLine(Point.Empty, Point.Empty);
            var l1 = (LineDraw)g1.DrawObject!;
            var l2 = (LineDraw)g2.DrawObject!;
            AddGraphicObject(g1);
            AddGraphicObject(g2);
            MoveLineTo(l1, cp, e1);
            MoveLineTo(l2, e1, e2);
            var keep = GetNextPort(line, nextPort)!;
            MoveLineTo(line, keep, e2);
            DisconnectTwoPorts(port, port.ConnectedPort);
            ConnectTwoPorts(port, StartPort(l1));
            ConnectTwoPorts(EndPort(l1), StartPort(l2));
            ConnectTwoPorts(EndPort(l2), keep);
        }
    }

    /// <summary>UndoManager.MoveLineOffsetByPort: translate the segment so its end <paramref name="port"/> meets its partner.</summary>
    public static void MoveLineOffsetByPort(LineDraw line, Port? port)
    {
        if (port == null) return;
        var delta = Point.Empty;
        if (ReferenceEquals(port, StartPort(line)))
        {
            if (!IsJoin(port))
            {
                var c = ConnectPoint(port.ConnectedPort!);
                delta = new Point(c.X - line.Position.X, c.Y - line.Position.Y);
            }
        }
        else if (ReferenceEquals(port, EndPort(line)) && !IsJoin(port))
        {
            var c = ConnectPoint(port.ConnectedPort!);
            delta = new Point(c.X - line.EndPosition.X, c.Y - line.EndPosition.Y);
        }
        if (delta != Point.Empty) MoveOffset(line, delta.X, delta.Y);
    }

    // ------------------------------------------------------------------ DesignSurface operations

    /// <summary>DesignSurface.MouseUp DragObject end: move every selected object, re-route their wires, simplify.</summary>
    public void MoveSelection(int dx, int dy)
    {
        var moved = Selection.ToList();
        foreach (var go in moved) MoveOffset(go.DrawObject!, dx, dy);
        foreach (var go in moved)
        {
            UpdateTwoSidePos(go.DrawObject as LineDraw);
            UpdateConnectLinesPos(go.DrawObject);
        }
        CombineLines(moved);
    }

    /// <summary>
    /// DesignSurface.AddConnectingLines: add the drawn segments, break any existing link on the two icon ports (ports
    /// are 1:1), link both ends, simplify the new wire, then drop dangling wires.
    /// </summary>
    public bool AddConnectingLines(ConnectingLines wire)
    {
        var added = false;
        var lines = wire.CopyLinesToCollection(out var first, out var last);
        if (lines.Count > 0)
        {
            foreach (var go in lines) AddGraphicObject(go);
            var start = wire.StartPort!;
            var end = wire.EndPort!;
            if (start.ConnectedPort != null) DisconnectTwoPorts(start, start.ConnectedPort);
            if (end.ConnectedPort != null) DisconnectTwoPorts(end, end.ConnectedPort);
            ConnectTwoPorts(start, first);
            ConnectTwoPorts(end, last);
            CombineOneLines((LineDraw)lines[0].DrawObject!);
            RemoveInvalidateLines();
            added = true;
        }
        wire.Reset();
        return added;
    }

    /// <summary>DesignSurface.AddLine (auto layout): default routing between two ports, no disconnects or cleanup.</summary>
    public void AddLine(ConnectingLines wire, Port startPort, Port endPort)
    {
        wire.StartPort = startPort;
        wire.SetEndPort(endPort);
        var lines = wire.CopyLinesToCollection(out var first, out var last);
        if (lines.Count == 0) return;
        foreach (var go in lines) AddGraphicObject(go);
        ConnectTwoPorts(startPort, first);
        ConnectTwoPorts(endPort, last);
    }

    /// <summary>DesignSurface.Delete: remove the selection, then dangling wires.</summary>
    public void DeleteSelection()
    {
        foreach (var go in Selection.ToList()) RemoveGraphicObject(go);
        RemoveInvalidateLines();
    }

    public enum Align { Top, Left, Right, Bottom }

    /// <summary>DesignSurface.AlignIcon*: align selected icons to the extreme edge, re-route their wires, simplify.</summary>
    public bool AlignIcons(Align align)
    {
        var icons = Selection.Select(g => g.DrawObject).OfType<IconDraw>().ToList();
        if (icons.Count == 0) return false;
        var target = align switch
        {
            Align.Top => icons.Min(i => i.Position.Y),
            Align.Left => icons.Min(i => i.Position.X),
            Align.Right => icons.Max(i => i.Position.X),
            _ => icons.Max(i => i.Position.Y),
        };
        foreach (var icon in icons)
        {
            if (align is Align.Top or Align.Bottom) MoveOffset(icon, 0, target - icon.Position.Y);
            else MoveOffset(icon, target - icon.Position.X, 0);
        }
        foreach (var icon in icons) UpdateConnectLinesPos(icon);
        CombineLines(Selection);
        return true;
    }

    /// <summary>DesignSurface.RemoveInvalidConnectedPort: drop links to objects outside <paramref name="objects"/>.</summary>
    public static void MakeIndependent(IReadOnlyCollection<GraphicObject> objects)
    {
        var set = new HashSet<GraphicObject>(objects, ReferenceEqualityComparer.Instance);
        foreach (var go in objects)
        foreach (var p in go.DrawObject?.Ports ?? [])
        {
            if (p.ConnectedPort != null && (p.ConnectedPort.Parent?.Parent is not { } owner || !set.Contains(owner)))
                p.ConnectedPort = null;
        }
    }

    /// <summary>DesignSurface.GetCenterPoint: centre of the objects' centre points, or Empty.</summary>
    public static Point GetCenterPoint(IEnumerable<GraphicObject> objects)
    {
        Point min = Point.Empty, max = Point.Empty;
        foreach (var go in objects)
        {
            if (go.DrawObject == null) continue;
            var c = CenterPosition(go.DrawObject);
            if (min == Point.Empty || max == Point.Empty)
            {
                min = c;
                max = c;
                continue;
            }
            if (c.X < min.X) min = min with { X = c.X };
            if (c.Y < min.Y) min = min with { Y = c.Y };
            if (c.X > max.X) max = max with { X = c.X };
            if (c.Y > max.Y) max = max with { Y = c.Y };
        }
        return min != Point.Empty && max != Point.Empty ? new Point((min.X + max.X) / 2, (min.Y + max.Y) / 2) : Point.Empty;
    }

    /// <summary>DesignSurface.Paste / CopyAfterDragDrop core: add the clones offset by (dx,dy) and select them.</summary>
    public void AddClones(IReadOnlyList<GraphicObject> clones, int dx, int dy)
    {
        Selection.Clear();
        foreach (var go in clones)
        {
            if (go.DrawObject == null) continue;
            if (!AddGraphicObject(go)) continue;
            MoveOffset(go.DrawObject, dx, dy);
            Selection.Add(go);
        }
    }
}
