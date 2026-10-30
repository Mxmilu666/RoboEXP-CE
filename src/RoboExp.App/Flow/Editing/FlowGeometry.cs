using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;

namespace RoboExp.App.Flow.Editing;

/// <summary>Original <c>PortSizeType</c>: ports grow to <see cref="Large"/> while connecting.</summary>
public enum PortSize { Small, Large }

/// <summary>
/// <c>System.Drawing.Rectangle</c> semantics (inclusive left/top, exclusive right/bottom) used by the original's
/// hit tests, so empty and degenerate rectangles behave exactly as in GDI+.
/// </summary>
public readonly record struct Rect32(int X, int Y, int Width, int Height)
{
    public static readonly Rect32 Empty = default;

    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => X == 0 && Y == 0 && Width == 0 && Height == 0;

    public bool Contains(Point pt) => X <= pt.X && pt.X < X + Width && Y <= pt.Y && pt.Y < Y + Height;

    public bool Contains(Rect32 r) => X <= r.X && r.X + r.Width <= X + Width && Y <= r.Y && r.Y + r.Height <= Y + Height;

    public bool IntersectsWith(Rect32 r) => r.X < X + Width && X < r.X + r.Width && r.Y < Y + Height && Y < r.Y + r.Height;

    public Rect32 Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary><c>DrawObject.GetRealRectangle</c>: normalizes negative width/height.</summary>
    public static Rect32 Real(Rect32 r) => new(
        r.Width < 0 ? r.X + r.Width : r.X,
        r.Height < 0 ? r.Y + r.Height : r.Y,
        Math.Abs(r.Width),
        Math.Abs(r.Height));
}

/// <summary>
/// Geometry of the flowchart objects: <c>DefaultInfo</c> constants, <c>DrawObject/LineDraw/Port</c> bounds, connect
/// points and hit tests (RoboExp.GraphicObjects).
/// </summary>
public static class FlowGeometry
{
    public const int IconWidth = 32;
    public const int IconHeight = 32;
    public const int PortWidthSmall = 6, PortHeightSmall = 12;
    public const int PortWidthLarge = 15, PortHeightLarge = 15;

    private static readonly Point[] PortPosSmall = [new(0, 2), new(25, 2), new(0, 17), new(25, 17)];
    private static readonly Point[] PortPosLarge = [new(0, 0), new(16, 0), new(0, 16), new(16, 16)];
    private static readonly Point[] ConnectOffset = [new(-2, 7), new(34, 7), new(-2, 25), new(34, 25)];

    // ------------------------------------------------------------------ DrawObject

    public static int Width(DrawObject d) => d is LineDraw l ? l.EndPosition.X - l.Position.X : d.Size.Width;
    public static int Height(DrawObject d) => d is LineDraw l ? l.EndPosition.Y - l.Position.Y : d.Size.Height;

    /// <summary><c>DrawObject.CenterPosition</c>.</summary>
    public static Point CenterPosition(DrawObject d) => new(d.Position.X + Width(d) / 2, d.Position.Y + Height(d) / 2);

    /// <summary><c>GetBounds</c>: icons/text use position+size; lines are widened to 6px when axis-aligned.</summary>
    public static Rect32 GetBounds(DrawObject d)
    {
        if (d is not LineDraw l) return new Rect32(d.Position.X, d.Position.Y, d.Size.Width, d.Size.Height);
        var r = Rect32.Real(new Rect32(l.Position.X, l.Position.Y, l.EndPosition.X - l.Position.X, l.EndPosition.Y - l.Position.Y));
        var width = r.Width;
        if (r.Width == 0 && r.Height != 0) r = r with { Width = 6, X = r.X - 2 };
        if (r.Height == 0 && width != 0) r = r with { Height = 6, Y = r.Y - 2 };
        return r;
    }

    /// <summary><c>GetSelectedBounds</c>: icons inflate by 1 (top/left); lines use a 4px band.</summary>
    public static Rect32 GetSelectedBounds(DrawObject d)
    {
        if (d is LineDraw l)
        {
            var r = Rect32.Real(new Rect32(l.Position.X, l.Position.Y, l.EndPosition.X - l.Position.X, l.EndPosition.Y - l.Position.Y));
            var width = r.Width;
            if (r.Width == 0 && r.Height != 0) r = r with { Width = 4, X = r.X - 2 };
            if (r.Height == 0 && width != 0) r = r with { Height = 4, Y = r.Y - 2 };
            return r;
        }
        var b = Rect32.Real(GetBounds(d));
        return new Rect32(b.X - 1, b.Y - 1, b.Width + 1, b.Height + 1);
    }

    public static bool HitTest(DrawObject d, Point pt) => GetBounds(d).Contains(pt);

    public static bool HitTest(DrawObject d, Rect32 rect) => rect.IntersectsWith(GetBounds(d));

    // ------------------------------------------------------------------ LineDraw

    public static bool IsHorizontal(LineDraw l) => l.Position.Y == l.EndPosition.Y;
    public static bool IsVertical(LineDraw l) => l.Position.X == l.EndPosition.X;

    public static Port StartPort(LineDraw l) => l.Ports![0];
    public static Port EndPort(LineDraw l) => l.Ports![1];

    /// <summary><c>LineDraw.GetNextPort</c>: the other end of the segment.</summary>
    public static Port? GetNextPort(LineDraw l, Port port) =>
        ReferenceEquals(StartPort(l), port) ? EndPort(l) : ReferenceEquals(EndPort(l), port) ? StartPort(l) : null;

    /// <summary><c>LineDraw.GetNextPortByConnectedPort</c>: the end opposite the one linked to <paramref name="port"/>.</summary>
    public static Port? GetNextPortByConnectedPort(LineDraw l, Port? port) =>
        ReferenceEquals(StartPort(l).ConnectedPort, port) ? EndPort(l) : ReferenceEquals(EndPort(l).ConnectedPort, port) ? StartPort(l) : null;

    /// <summary><c>LineDraw.GetNextLineDraw</c>: the segment linked at <paramref name="port"/> (one of this line's ends).</summary>
    public static LineDraw? GetNextLineDraw(LineDraw l, Port? port) =>
        port != null && (ReferenceEquals(StartPort(l), port) || ReferenceEquals(EndPort(l), port)) && port.ConnectedPort != null
            ? port.ConnectedPort.Parent as LineDraw
            : null;

    /// <summary>
    /// <c>LineDraw.IntersectsWith</c> for orthogonal segments (exact port, including its quirks for collinear overlap).
    /// </summary>
    public static bool IntersectsWith(LineDraw a, LineDraw? b, out Point point)
    {
        point = Point.Empty;
        if (b == null) return false;
        int x1 = a.Position.X, y1 = a.Position.Y, x2 = a.EndPosition.X, y2 = a.EndPosition.Y;
        int x3 = b.Position.X, y3 = b.Position.Y, x4 = b.EndPosition.X, y4 = b.EndPosition.Y;
        if (IsHorizontal(a)) { x1 = Math.Min(a.Position.X, a.EndPosition.X); x2 = Math.Max(a.Position.X, a.EndPosition.X); }
        else { y1 = Math.Min(a.Position.Y, a.EndPosition.Y); y2 = Math.Max(a.Position.Y, a.EndPosition.Y); }
        if (IsHorizontal(b)) { x3 = Math.Min(b.Position.X, b.EndPosition.X); x4 = Math.Max(b.Position.X, b.EndPosition.X); }
        else { y3 = Math.Min(b.Position.Y, b.EndPosition.Y); y4 = Math.Max(b.Position.Y, b.EndPosition.Y); }

        if (IsHorizontal(a))
        {
            if (IsVertical(b))
            {
                if (x1 <= x3 && x3 <= x2 && y3 <= y1 && y1 <= y4) { point = new Point(x3, y1); return true; }
                return false;
            }
            if (y1 != y3) return false;
            if (x1 <= x3 && x3 <= x2) { point = new Point(x3, y1); return true; }
            if (x1 <= x4 && x4 <= x2) { point = new Point(x4, y1); return true; }
            if (x3 <= x1 && x1 <= x4) { point = new Point(x1, y1); return true; }
            if (x3 <= x2 && x2 <= x4) { point = new Point(x2, y1); return true; }
            return false;
        }
        if (IsHorizontal(b))
        {
            if (x3 <= x1 && x1 <= x4 && y1 <= y3 && y3 <= y2) { point = new Point(x1, y3); return true; }
            return false;
        }
        if (x1 != x3) return false;
        if (y1 <= y3 && y3 <= y2) { point = new Point(x1, y3); return true; }
        if (y1 <= y4 && y4 <= y2) { point = new Point(x1, y4); return true; }
        if (y3 <= y1 && y1 <= y4) { point = new Point(x1, y1); return true; }
        if (y3 <= y2 && y2 <= y4) { point = new Point(x1, y2); return true; }
        return false;
    }

    // ------------------------------------------------------------------ Port

    /// <summary><c>Port.ConnectPoint</c>: line ends are the line's points; icon ports use the ConnectPoint offsets.</summary>
    public static Point ConnectPoint(Port port)
    {
        switch (port.Parent)
        {
            case LineDraw l:
                return ReferenceEquals(port, StartPort(l)) ? l.Position : l.EndPosition;
            case IconDraw icon:
                var o = ConnectOffset[(int)port.PortDirection & 3];
                return new Point(icon.Position.X + o.X, icon.Position.Y + o.Y);
            default:
                return Point.Empty;
        }
    }

    /// <summary><c>Port.GetRect</c> (zero-size for line ends).</summary>
    public static Rect32 PortRect(Port port, PortSize size)
    {
        switch (port.Parent)
        {
            case LineDraw l:
                var p = ReferenceEquals(port, StartPort(l)) ? l.Position : l.EndPosition;
                return new Rect32(p.X, p.Y, 0, 0);
            case IconDraw icon:
                var o = (size == PortSize.Small ? PortPosSmall : PortPosLarge)[(int)port.PortDirection & 3];
                return size == PortSize.Small
                    ? new Rect32(icon.Position.X + o.X, icon.Position.Y + o.Y, PortWidthSmall, PortHeightSmall)
                    : new Rect32(icon.Position.X + o.X, icon.Position.Y + o.Y, PortWidthLarge, PortHeightLarge);
            case { } other:
                return new Rect32(other.Position.X, other.Position.Y, 0, 0);
            default:
                return Rect32.Empty;
        }
    }

    public static bool HitTest(Port port, Point pt, PortSize size) => PortRect(port, size).Contains(pt);

    /// <summary><c>Port.IsJoin</c>: unconnected, or both ends at the same connect point.</summary>
    public static bool IsJoin(Port port) => port.ConnectedPort == null || ConnectPoint(port.ConnectedPort) == ConnectPoint(port);

    // ------------------------------------------------------------------ Surface hit testing (DesignSurface)

    /// <summary><c>FindObjectAtPoint</c>: topmost object of any kind.</summary>
    public static GraphicObject? FindObjectAtPoint(IReadOnlyList<GraphicObject> objects, Point pt)
    {
        for (var i = objects.Count - 1; i >= 0; i--)
            if (objects[i].DrawObject is { } d && HitTest(d, pt)) return objects[i];
        return null;
    }

    /// <summary><c>FindObjectAtPointB</c>: topmost non-line object, else topmost line.</summary>
    public static GraphicObject? FindObjectAtPointB(IReadOnlyList<GraphicObject> objects, Point pt)
    {
        for (var i = objects.Count - 1; i >= 0; i--)
            if (objects[i].DrawObject is { } d and not LineDraw && HitTest(d, pt)) return objects[i];
        for (var i = objects.Count - 1; i >= 0; i--)
            if (objects[i].DrawObject is LineDraw l && HitTest(l, pt)) return objects[i];
        return null;
    }

    /// <summary><c>FindPort(pt, gObj)</c>: the first port of <paramref name="go"/> whose rectangle contains the point.</summary>
    public static Port? FindPort(GraphicObject? go, Point pt, PortSize size)
    {
        if (go?.DrawObject?.Ports is not { } ports) return null;
        foreach (var p in ports)
            if (HitTest(p, pt, size)) return p;
        return null;
    }

    /// <summary><c>GetDrawingObjectsRect</c>: extents of all objects plus a 50px margin (empty when there are none).</summary>
    public static Rect32 GetDrawingObjectsRect(IReadOnlyList<GraphicObject> objects)
    {
        if (objects.Count == 0) return Rect32.Empty;
        var first = objects[0].DrawObject!;
        int minX = first.Position.X, minY = first.Position.Y, maxX = minX, maxY = minY;
        foreach (var go in objects)
        {
            var d = go.DrawObject!;
            if (d.Position.X < minX) minX = d.Position.X;
            if (d.Position.Y < minY) minY = d.Position.Y;
            var r = d.Position.X + Width(d);
            if (r > maxX) maxX = r;
            var b = d.Position.Y + Height(d);
            if (b > maxY) maxY = b;
        }
        minX -= 50; minY -= 50; maxX += 50; maxY += 50;
        return new Rect32(minX, minY, maxX - minX, maxY - minY);
    }
}
