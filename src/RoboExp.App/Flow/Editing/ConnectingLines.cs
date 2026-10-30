using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;

namespace RoboExp.App.Flow.Editing;

/// <summary>
/// Port of <c>RoboExp.GraphicObjects.ConnectingLines</c>: the wire being drawn. Points start at the start port's connect
/// point; clicks add orthogonal elbows (horizontal-first unless <see cref="IsHorizontal"/> is toggled). Quirks kept:
/// a click collinear with the last point is dropped unless it extends the previous segment, and
/// <see cref="Reset"/> keeps the <see cref="IsHorizontal"/> toggle.
/// </summary>
public sealed class ConnectingLines
{
    private readonly List<Point> _points = new(4);
    private Port? _startPort;

    public bool IsHorizontal { get; set; } = true;
    public Point MovingPoint { get; set; }
    public IReadOnlyList<Point> Points => _points;

    public Port? StartPort
    {
        get => _startPort;
        set
        {
            Reset();
            _startPort = value;
            if (value == null) return;
            var cp = FlowGeometry.ConnectPoint(value);
            MovingPoint = cp;
            AddPoint(cp);
        }
    }

    public Port? EndPort { get; private set; }

    public void SetEndPort(Port port)
    {
        EndPort = port;
        AddPoint(FlowGeometry.ConnectPoint(port));
    }

    public void Reset()
    {
        _startPort = null;
        EndPort = null;
        MovingPoint = Point.Empty;
        _points.Clear();
    }

    /// <summary><c>GetLineStructList</c>: the preview segments (points, an elbow towards the mouse, the mouse point).</summary>
    public IReadOnlyList<(Point A, Point B)> PreviewSegments()
    {
        var result = new List<(Point, Point)>();
        if (_points.Count == 0) return result;
        var pts = new List<Point>(_points);
        var last = _points[^1];
        pts.Add(IsHorizontal ? new Point(last.X, MovingPoint.Y) : new Point(MovingPoint.X, last.Y));
        pts.Add(MovingPoint);
        var prev = pts[0];
        for (var i = 1; i < pts.Count; i++)
        {
            var cur = pts[i];
            if (cur.X == prev.X) result.Add(cur.Y > prev.Y ? (prev, cur) : (cur, prev));
            else if (cur.X > prev.X) result.Add((prev, cur));
            else result.Add((cur, prev));
            prev = cur;
        }
        return result;
    }

    public void AddPoint(Point point)
    {
        if (_points.Count == 0)
        {
            _points.Add(point);
            return;
        }
        var last = _points[^1];
        var beforeLast = _points.Count > 1 ? _points[^2] : Point.Empty;
        if (last == point) return;
        if (last.X == point.X)
        {
            if (_points.Count == 1) _points.Add(point);
            else if (beforeLast.X == last.X)
            {
                _points.RemoveAt(_points.Count - 1);
                _points.Add(point);
            }
        }
        else if (last.Y == point.Y)
        {
            if (_points.Count == 1) _points.Add(point);
            else if (beforeLast.Y == last.Y)
            {
                _points.RemoveAt(_points.Count - 1);
                _points.Add(point);
            }
        }
        else
        {
            _points.Add(IsHorizontal ? new Point(last.X, point.Y) : new Point(point.X, last.Y));
            _points.Add(point);
        }
    }

    /// <summary>
    /// <c>CopyLinesToCollection</c>: one LineDraw per segment, chained port to port. With 1-2 points a single straight
    /// segment joins the two connect points. Empty when either port is missing or both are the same.
    /// </summary>
    public List<GraphicObject> CopyLinesToCollection(out Port? firstPort, out Port? endPort)
    {
        var result = new List<GraphicObject>();
        firstPort = null;
        endPort = null;
        if (_startPort == null || EndPort == null || ReferenceEquals(_startPort, EndPort) || _points.Count == 0) return result;
        if (_points.Count <= 2)
        {
            var go = FlowFactory.NewLine(FlowGeometry.ConnectPoint(_startPort), FlowGeometry.ConnectPoint(EndPort));
            var line = (LineDraw)go.DrawObject!;
            firstPort = FlowGeometry.StartPort(line);
            endPort = FlowGeometry.EndPort(line);
            result.Add(go);
            return result;
        }
        LineDraw? current = null;
        Port? prevEnd = null;
        for (var i = 1; i < _points.Count; i++)
        {
            var go = FlowFactory.NewLine(_points[i - 1], _points[i]);
            current = (LineDraw)go.DrawObject!;
            if (i == 1) firstPort = FlowGeometry.StartPort(current);
            else
            {
                prevEnd!.ConnectedPort = FlowGeometry.StartPort(current);
                FlowGeometry.StartPort(current).ConnectedPort = prevEnd;
            }
            prevEnd = FlowGeometry.EndPort(current);
            result.Add(go);
        }
        endPort = FlowGeometry.EndPort(current!);
        return result;
    }
}
