using RoboExp.App.Flow;
using RoboExp.App.Flow.Editing;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;
using RoboExp.Core.Project;
using RoboExp.Core.Rcu;
using RoboExp.Tests.Rcu;

namespace RoboExp.Tests.App.Flow;

internal static class FlowFixture
{
    public static GraphicObject Icon(IconType type, int x, int y) =>
        FlowFactory.NewIcon(type, new Point(x, y), null, FlowFactory.NewControlData(type));

    public static Port PortOf(GraphicObject go, PortType type) => go.DrawObject!.Ports!.First(p => p.Type == type);

    public static LineDraw Line(GraphicObject go) => (LineDraw)go.DrawObject!;

    public static (List<GraphicObject> Objects, List<GraphicObject> Selection, FlowEditor Editor) Surface(params GraphicObject[] objects)
    {
        var list = objects.ToList();
        var selection = new List<GraphicObject>();
        return (list, selection, new FlowEditor(list, selection));
    }

    /// <summary>Follows a wire from an icon port to the icon port at its other end.</summary>
    public static Port? FarEnd(Port port)
    {
        var p = port.ConnectedPort;
        while (p?.Parent is LineDraw l)
        {
            var other = ReferenceEquals(l.Ports![0], p) ? l.Ports[1] : l.Ports[0];
            p = other.ConnectedPort;
        }
        return p;
    }
}

public class FlowGeometryTests
{
    [Fact]
    public void Rect32_follows_System_Drawing_semantics()
    {
        var r = new Rect32(10, 10, 5, 5);
        Assert.True(r.Contains(new Point(10, 10)));
        Assert.False(r.Contains(new Point(15, 10)));
        Assert.False(new Rect32(3, 3, 0, 0).Contains(new Point(3, 3)));
        Assert.True(r.IntersectsWith(new Rect32(14, 14, 1, 1)));
        Assert.False(r.IntersectsWith(new Rect32(15, 10, 3, 3)));
        Assert.Equal(new Rect32(2, 3, 4, 5), Rect32.Real(new Rect32(6, 8, -4, -5)));
    }

    [Fact]
    public void Icon_ports_use_DefaultInfo_rectangles_and_connect_points()
    {
        var ifIcon = FlowFixture.Icon(IconType.BeginIf, 100, 200);
        var inPort = FlowFixture.PortOf(ifIcon, PortType.In);
        var elsePort = FlowFixture.PortOf(ifIcon, PortType.BeginElse);
        Assert.Equal(new Rect32(100, 202, 6, 12), FlowGeometry.PortRect(inPort, PortSize.Small));
        Assert.Equal(new Rect32(125, 217, 6, 12), FlowGeometry.PortRect(elsePort, PortSize.Small));
        Assert.Equal(new Rect32(116, 216, 15, 15), FlowGeometry.PortRect(elsePort, PortSize.Large));
        Assert.Equal(new Point(98, 207), FlowGeometry.ConnectPoint(inPort));
        Assert.Equal(new Point(134, 225), FlowGeometry.ConnectPoint(elsePort));
        Assert.Same(elsePort, FlowGeometry.FindPort(ifIcon, new Point(126, 220), PortSize.Small));
        Assert.Null(FlowGeometry.FindPort(ifIcon, new Point(110, 220), PortSize.Small));
    }

    [Fact]
    public void Lines_hit_in_a_6px_band_and_icons_win_over_lines()
    {
        var line = FlowFactory.NewLine(new Point(0, 50), new Point(100, 50));
        Assert.Equal(new Rect32(0, 48, 100, 6), FlowGeometry.GetBounds(line.DrawObject!));
        Assert.Equal(new Rect32(0, 48, 100, 4), FlowGeometry.GetSelectedBounds(line.DrawObject!));
        var vertical = FlowFactory.NewLine(new Point(20, 80), new Point(20, 10));
        Assert.Equal(new Rect32(18, 10, 6, 70), FlowGeometry.GetBounds(vertical.DrawObject!));

        var icon = FlowFixture.Icon(IconType.Calculate, 40, 40);
        Assert.Equal(new Rect32(39, 39, 33, 33), FlowGeometry.GetSelectedBounds(icon.DrawObject!));
        IReadOnlyList<GraphicObject> objects = [icon, line];
        // The line is later (on top) but non-lines are hit first (FindObjectAtPointB).
        Assert.Same(icon, FlowGeometry.FindObjectAtPointB(objects, new Point(50, 51)));
        Assert.Same(line, FlowGeometry.FindObjectAtPoint(objects, new Point(50, 51)));
        Assert.Same(line, FlowGeometry.FindObjectAtPointB(objects, new Point(5, 52)));
        Assert.Null(FlowGeometry.FindObjectAtPointB(objects, new Point(5, 55)));
    }

    [Fact]
    public void Orthogonal_intersection_matches_original()
    {
        var h = (LineDraw)FlowFactory.NewLine(new Point(0, 10), new Point(50, 10)).DrawObject!;
        var v = (LineDraw)FlowFactory.NewLine(new Point(20, 0), new Point(20, 40)).DrawObject!;
        Assert.True(FlowGeometry.IntersectsWith(h, v, out var p));
        Assert.Equal(new Point(20, 10), p);
        var far = (LineDraw)FlowFactory.NewLine(new Point(60, 0), new Point(60, 40)).DrawObject!;
        Assert.False(FlowGeometry.IntersectsWith(h, far, out _));
    }
}

public class ConnectingLinesTests
{
    [Fact]
    public void Clicks_add_horizontal_first_elbows_and_collinear_clicks_extend_or_are_dropped()
    {
        var a = FlowFixture.Icon(IconType.Calculate, 0, 0);
        var wire = new ConnectingLines { StartPort = FlowFixture.PortOf(a, PortType.Out) }; // (34,7)
        wire.AddPoint(new Point(80, 60));
        Assert.Equal([new Point(34, 7), new Point(34, 60), new Point(80, 60)], wire.Points);
        wire.AddPoint(new Point(120, 60)); // extends the horizontal segment
        Assert.Equal(new Point(120, 60), wire.Points[^1]);
        Assert.Equal(3, wire.Points.Count);
        wire.AddPoint(new Point(120, 90)); // vertical after horizontal: original drops it
        Assert.Equal(3, wire.Points.Count);

        wire.IsHorizontal = false;
        wire.AddPoint(new Point(150, 100));
        Assert.Equal(new Point(150, 60), wire.Points[^2]);
        wire.Reset();
        Assert.False(wire.IsHorizontal); // Reset keeps the toggle
    }

    [Fact]
    public void Copy_creates_one_chained_segment_per_leg()
    {
        var a = FlowFixture.Icon(IconType.Calculate, 0, 0);
        var b = FlowFixture.Icon(IconType.Calculate, 200, 100);
        var wire = new ConnectingLines { StartPort = FlowFixture.PortOf(a, PortType.Out) };
        wire.AddPoint(new Point(100, 50));
        wire.SetEndPort(FlowFixture.PortOf(b, PortType.In)); // (198,107)
        var lines = wire.CopyLinesToCollection(out var first, out var last);
        Assert.True(lines.Count >= 3);
        Assert.Same(lines[0].DrawObject!.Ports![0], first);
        Assert.Same(lines[^1].DrawObject!.Ports![1], last);
        for (var i = 1; i < lines.Count; i++)
        {
            var prevEnd = lines[i - 1].DrawObject!.Ports![1];
            Assert.Same(lines[i].DrawObject!.Ports![0], prevEnd.ConnectedPort);
            Assert.All(lines, l => Assert.IsType<LineCode>(l.CodeObject));
        }
    }

    [Fact]
    public void Straight_connection_is_a_single_segment()
    {
        var a = FlowFixture.Icon(IconType.Calculate, 0, 0);
        var b = FlowFixture.Icon(IconType.Calculate, 100, 0);
        var wire = new ConnectingLines { StartPort = FlowFixture.PortOf(a, PortType.Out) };
        wire.SetEndPort(FlowFixture.PortOf(b, PortType.In));
        var lines = wire.CopyLinesToCollection(out _, out _);
        var line = Assert.Single(lines);
        Assert.Equal(new Point(34, 7), FlowFixture.Line(line).Position);
        Assert.Equal(new Point(98, 7), FlowFixture.Line(line).EndPosition);
    }
}

public class WireEditingTests
{
    private static void Connect(FlowEditor editor, Port from, Port to, params Point[] bends)
    {
        var wire = new ConnectingLines { StartPort = from };
        foreach (var b in bends) wire.AddPoint(b);
        wire.SetEndPort(to);
        Assert.True(editor.AddConnectingLines(wire));
    }

    [Fact]
    public void MoveLineTo_treats_origin_as_unchanged()
    {
        var line = FlowFixture.Line(FlowFactory.NewLine(new Point(5, 5), new Point(9, 9)));
        FlowEditor.MoveLineTo(line, Point.Empty, new Point(20, 9));
        Assert.Equal(new Point(5, 5), line.Position);
        Assert.Equal(new Point(20, 9), line.EndPosition);
        FlowEditor.MoveLineTo(line, new Point(1, 1), Point.Empty);
        Assert.Equal(new Point(1, 1), line.Position);
    }

    [Fact]
    public void Connecting_a_used_port_replaces_its_wire_and_drops_the_dangling_one()
    {
        var a = FlowFixture.Icon(IconType.Calculate, 0, 0);
        var b = FlowFixture.Icon(IconType.Calculate, 100, 0);
        var c = FlowFixture.Icon(IconType.Calculate, 200, 0);
        var (objects, _, editor) = FlowFixture.Surface(a, b, c);
        Connect(editor, FlowFixture.PortOf(a, PortType.Out), FlowFixture.PortOf(b, PortType.In));
        Assert.Same(FlowFixture.PortOf(b, PortType.In), FlowFixture.FarEnd(FlowFixture.PortOf(a, PortType.Out)));
        Assert.Equal(4, objects.Count);

        // a.Out now goes to c.In: ports are 1:1, the old wire loses its start and is removed as dangling.
        Connect(editor, FlowFixture.PortOf(a, PortType.Out), FlowFixture.PortOf(c, PortType.In));
        Assert.Same(FlowFixture.PortOf(c, PortType.In), FlowFixture.FarEnd(FlowFixture.PortOf(a, PortType.Out)));
        Assert.Null(FlowFixture.PortOf(b, PortType.In).ConnectedPort);
        Assert.Single(objects, o => o.DrawObject is LineDraw);
        Assert.All(objects.Where(o => o.DrawObject is LineDraw), l =>
            Assert.All(l.DrawObject!.Ports!, p => Assert.NotNull(p.ConnectedPort)));
    }

    [Fact]
    public void Collinear_segments_are_merged()
    {
        var a = FlowFixture.Icon(IconType.Calculate, 0, 0);
        var b = FlowFixture.Icon(IconType.Calculate, 200, 0);
        var (objects, _, editor) = FlowFixture.Surface(a, b);
        // Bend points on the same y as the ports: the legs are collinear and collapse into one segment.
        Connect(editor, FlowFixture.PortOf(a, PortType.Out), FlowFixture.PortOf(b, PortType.In), new Point(100, 7));
        Assert.Single(objects, o => o.DrawObject is LineDraw);
    }

    [Fact]
    public void Moving_an_icon_vertically_keeps_its_wire_orthogonal()
    {
        var a = FlowFixture.Icon(IconType.Calculate, 0, 0);
        var b = FlowFixture.Icon(IconType.Calculate, 200, 0);
        var (objects, selection, editor) = FlowFixture.Surface(a, b);
        Connect(editor, FlowFixture.PortOf(a, PortType.Out), FlowFixture.PortOf(b, PortType.In));
        selection.Add(b);
        editor.MoveSelection(0, 60);
        var lines = objects.Where(o => o.DrawObject is LineDraw).Select(FlowFixture.Line).ToList();
        Assert.Equal(3, lines.Count); // straight wire split into horizontal-vertical-horizontal
        Assert.All(lines, l => Assert.True(FlowGeometry.IsHorizontal(l) || FlowGeometry.IsVertical(l)));
        Assert.Same(FlowFixture.PortOf(b, PortType.In), FlowFixture.FarEnd(FlowFixture.PortOf(a, PortType.Out)));
        Assert.Equal(new Point(198, 67), FlowGeometry.ConnectPoint(FlowFixture.PortOf(b, PortType.In)));
        Assert.Contains(lines, l => l.EndPosition == new Point(198, 67) || l.Position == new Point(198, 67));
    }

    [Fact]
    public void Delete_removes_the_selection_and_wires_left_dangling()
    {
        var a = FlowFixture.Icon(IconType.Calculate, 0, 0);
        var b = FlowFixture.Icon(IconType.Calculate, 100, 40);
        var (objects, selection, editor) = FlowFixture.Surface(a, b);
        Connect(editor, FlowFixture.PortOf(a, PortType.Out), FlowFixture.PortOf(b, PortType.In));
        Assert.True(objects.Count > 2);
        selection.Add(b);
        editor.DeleteSelection();
        Assert.Equal([a], objects);
        Assert.Null(FlowFixture.PortOf(a, PortType.Out).ConnectedPort);
    }

    [Fact]
    public void Wire_chain_selection_returns_every_segment()
    {
        var a = FlowFixture.Icon(IconType.Calculate, 0, 0);
        var b = FlowFixture.Icon(IconType.Calculate, 200, 100);
        var (objects, _, editor) = FlowFixture.Surface(a, b);
        Connect(editor, FlowFixture.PortOf(a, PortType.Out), FlowFixture.PortOf(b, PortType.In), new Point(100, 50));
        var lines = objects.Where(o => o.DrawObject is LineDraw).ToList();
        var chain = FlowEditor.GetConnectLines(FlowFixture.Line(lines[1]));
        Assert.Equal(lines.Count, chain.Count);
        Assert.All(lines, l => Assert.Contains(l, chain));
    }

    [Fact]
    public void Align_left_moves_icons_to_the_leftmost_x()
    {
        var a = FlowFixture.Icon(IconType.Calculate, 30, 0);
        var b = FlowFixture.Icon(IconType.Calculate, 90, 80);
        var (_, selection, editor) = FlowFixture.Surface(a, b);
        selection.AddRange([a, b]);
        Assert.True(editor.AlignIcons(FlowEditor.Align.Left));
        Assert.Equal(30, b.DrawObject!.Position.X);
        Assert.Equal(80, b.DrawObject.Position.Y);
    }

    [Fact]
    public void A_tenth_start_icon_is_refused()
    {
        var starts = Enumerable.Range(0, 9).Select(i => FlowFixture.Icon(IconType.Start, i * 40, 0)).ToArray();
        var (objects, _, editor) = FlowFixture.Surface(starts);
        var refused = false;
        editor.StartLimitReached += (_, _) => refused = true;
        Assert.False(editor.AddGraphicObject(FlowFixture.Icon(IconType.Start, 0, 100)));
        Assert.True(refused);
        Assert.Equal(9, objects.Count);
    }
}

public class AutoArrangeTests
{
    [Fact]
    public void Flow_order_is_laid_out_in_rows_of_52px()
    {
        var start = FlowFixture.Icon(IconType.Start, 300, 300);
        var c1 = FlowFixture.Icon(IconType.Calculate, 10, 400);
        var c2 = FlowFixture.Icon(IconType.Calculate, 500, 10);
        var loose = FlowFixture.Icon(IconType.Calculate, 700, 700);
        var (objects, _, editor) = FlowFixture.Surface(start, c1, c2, loose);
        var wire = new ConnectingLines();
        editor.AddLine(wire, FlowFixture.PortOf(start, PortType.Start), FlowFixture.PortOf(c1, PortType.In));
        editor.AddLine(wire, FlowFixture.PortOf(c1, PortType.Out), FlowFixture.PortOf(c2, PortType.In));
        var first = new AutoArrange(editor, wire).Refresh();
        Assert.Same(start, first);
        Assert.Equal(new Point(84, 32), start.DrawObject!.Position); // row start (32) + 52: the Start icon itself steps first
        Assert.Equal(new Point(136, 32), c1.DrawObject!.Position);
        Assert.Equal(new Point(188, 32), c2.DrawObject!.Position);
        Assert.Equal(132, loose.DrawObject!.Position.Y); // leftovers: a new row 100px lower
        Assert.Same(FlowFixture.PortOf(c2, PortType.In), FlowFixture.FarEnd(FlowFixture.PortOf(c1, PortType.Out)));
        Assert.Same(FlowFixture.PortOf(c1, PortType.In), FlowFixture.FarEnd(FlowFixture.PortOf(start, PortType.Start)));
        Assert.All(objects.Where(o => o.DrawObject is LineDraw).Select(FlowFixture.Line),
            l => Assert.True(FlowGeometry.IsHorizontal(l) || FlowGeometry.IsVertical(l)));
    }
}

public class FlowUndoManagerTests
{
    [Fact]
    public void Records_only_changes_and_bounds_the_stacks()
    {
        var state = new byte[] { 0 };
        var undo = new FlowUndoManager(() => (byte[])state.Clone(), s => state = s) { MaxCount = 2 };
        Assert.False(undo.Record("noop", () => { }));
        for (byte i = 1; i <= 3; i++)
        {
            var v = i;
            Assert.True(undo.Record("set", () => state = [v]));
        }
        Assert.True(undo.Undo());
        Assert.Equal([2], state);
        Assert.True(undo.Undo());
        Assert.Equal([1], state);
        Assert.False(undo.Undo()); // depth 2: the first step fell off
        Assert.True(undo.Redo());
        Assert.Equal([2], state);
        undo.Record("set", () => state = [9]);
        Assert.False(undo.CanRedo); // a new edit clears redo
    }

    [Fact]
    public void Max_count_is_clamped_like_ConfigInfo()
    {
        var undo = new FlowUndoManager(() => [], _ => { }) { MaxCount = 50 };
        Assert.Equal(20, undo.MaxCount);
        undo.MaxCount = -3;
        Assert.Equal(0, undo.MaxCount);
    }

    [RcuVendorFact]
    public void Snapshot_restore_round_trips_a_vendor_project()
    {
        var path = RcuVendor.FullPath("Robots/E6RCU/Examples/Other/MusicNoteTest/MusicNoteTest.rcu");
        var session = ProjectSession.Open(path, RcuVendor.Install!);
        byte[] Capture()
        {
            var ms = new MemoryStream();
            session.WriteRcu(ms);
            return ms.ToArray();
        }
        var original = Capture();
        var undo = new FlowUndoManager(Capture, s => session.Restore(RcuReader.Read(new MemoryStream(s))));
        var selection = new List<GraphicObject>();
        var icon = session.DrawingObjects!.List!.First(o => o.DrawObject is IconDraw { IconType: IconType.Function });
        Assert.True(undo.Record("move", () =>
        {
            selection.Add(icon);
            new FlowEditor(session.DrawingObjects!.List!, selection).MoveSelection(40, 25);
        }));
        Assert.NotEqual(original, Capture());
        Assert.True(undo.Undo());
        Assert.Equal(original, Capture());
        Assert.True(undo.Redo());
        Assert.NotEqual(original, Capture());
    }
}
