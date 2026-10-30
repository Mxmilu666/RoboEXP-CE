using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using RoboExp.App.Flow.Editing;
using RoboExp.App.Palette;
using RoboExp.Core.Flow;
using RoboExp.Core.Model.Graphics;
using AvPoint = Avalonia.Point;
using Point = RoboExp.Core.Model.Point;

namespace RoboExp.App.Flow;

/// <summary>Original <c>WorkingType</c>.</summary>
public enum FlowWorking { DragObject, DragSelection, Connect, NormalMove, Scroll }

/// <summary>
/// The design surface (RoboExp.DesignSurface.DesignSurface): paints the original flowchart geometry and artwork
/// with workspace-only theme colors (lines first, then icons/text; captions; grey unreachable icons; hover ports;
/// selection bounds; marquee; wire preview; drag outlines) and implements its mouse/keyboard state machine on top of
/// <see cref="FlowDocumentViewModel"/>. World coordinates are integer pixels; the view is the world translated by
/// the scroll offset (the original's g_Scale is fixed at 1, so there is no zoom).
/// </summary>
public sealed class FlowCanvas : Control
{
    private const int ScrollBarScale = 30;

    private FlowDocumentViewModel? _doc;
    private FlowWorking _working = FlowWorking.NormalMove;
    private Point _dragStart, _dragPos;
    private Rect32 _selectionRect;
    private AvPoint _scrollStart;
    private GraphicObject? _lastFoundObj;
    private Port? _lastFoundPort;
    private GraphicObject? _hoverNormal;
    private GraphicObject? _selectedObjectNeedToRemove;
    private int _lastTick = Environment.TickCount;
    private bool _ctrlDown;
    private double _wheelRemainder;

    public FlowCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Cursor = FlowCursors.AutoToolOver;
        ActualThemeVariantChanged += (_, _) =>
        {
            _theme = ActualThemeVariant == ThemeVariant.Dark ? DarkTheme : LightTheme;
            InvalidateVisual();
        };
        _theme = ActualThemeVariant == ThemeVariant.Dark ? DarkTheme : LightTheme;
    }

    public FlowDocumentViewModel? Document
    {
        get => _doc;
        set
        {
            if (_doc != null)
            {
                _doc.ModelChanged -= OnModelChanged;
                _doc.SelectionChanged -= OnModelChanged;
                _doc.PropertyChanged -= OnDocPropertyChanged;
                _doc.ScrollIntoViewRequested -= OnScrollIntoViewRequested;
            }
            _doc = value;
            if (_doc != null)
            {
                _doc.ModelChanged += OnModelChanged;
                _doc.SelectionChanged += OnModelChanged;
                _doc.PropertyChanged += OnDocPropertyChanged;
                _doc.ScrollIntoViewRequested += OnScrollIntoViewRequested;
            }
            _working = FlowWorking.NormalMove;
            _lastFoundObj = null;
            _lastFoundPort = null;
            InvalidateVisual();
        }
    }

    /// <summary>A palette item chosen from the context menu, placed by the next left click (PanelIcon.DraggingBaseNode).</summary>
    public PaletteDragItem? PendingPlacement { get; set; }

    /// <summary>Raised when the scroll offset or drawing extents change (the view updates its scroll bars).</summary>
    public event EventHandler? ViewChanged;

    /// <summary>Raised for a context menu request at a control-local point (the view builds the menu).</summary>
    public event EventHandler<AvPoint>? ContextMenuRequested;

    /// <summary>Raised to open the block editor of one object (property popup / double-click / Enter).</summary>
    public event EventHandler<GraphicObject>? EditRequested;

    /// <summary>Raised on a double-click with nothing selected (original: show the code window).</summary>
    public event EventHandler? ShowCodeRequested;

    /// <summary>Raised by the Text tool click (the view asks for the text, original InputBox).</summary>
    public event EventHandler<Point>? TextRequested;

    public FlowWorking Working
    {
        get => _working;
        private set => _working = value;
    }

    private PortSize CurrentPortSize =>
        _doc?.Tool == FlowTool.Connect || _working == FlowWorking.Connect ? PortSize.Large : PortSize.Small;

    private Point Scroll => _doc?.ScrollOffset ?? Point.Empty;

    private IReadOnlyList<GraphicObject> Objects => _doc?.Objects ?? [];

    /// <summary>DesignSurface.PageToWorld.</summary>
    public Point PageToWorld(AvPoint p) => new(Scroll.X + (int)Math.Floor(p.X), Scroll.Y + (int)Math.Floor(p.Y));

    private void SetScroll(int x, int y)
    {
        if (_doc == null) return;
        _doc.ScrollOffset = new Point(x, y);
        UpdateViewRect();
        ViewChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void ScrollBy(int dx, int dy) => SetScroll(Scroll.X + dx, Scroll.Y + dy);

    private void UpdateViewRect()
    {
        if (_doc != null) _doc.ViewSize = new RoboExp.Core.Model.Size((int)Bounds.Width, (int)Bounds.Height);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateViewRect();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnModelChanged(object? sender, EventArgs e)
    {
        if (_lastFoundObj != null && !Objects.Contains(_lastFoundObj)) _lastFoundObj = null;
        if (_lastFoundPort?.Parent?.Parent is { } owner && !Objects.Contains(owner)) _lastFoundPort = null;
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDocPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FlowDocumentViewModel.Tool) || _doc == null) return;
        _working = FlowWorking.NormalMove;
        Cursor = _doc.Tool switch
        {
            FlowTool.Select => FlowCursors.SelectOver,
            FlowTool.Connect => FlowCursors.Connect,
            FlowTool.Scroll => FlowCursors.Scroll,
            FlowTool.Text => FlowCursors.Text,
            _ => FlowCursors.AutoToolOver,
        };
        InvalidateVisual();
    }

    /// <summary>DesignSurface.ScrollObjectIntoView: when not visible, put it 2 icons from the top-left.</summary>
    private void OnScrollIntoViewRequested(object? sender, GraphicObject go)
    {
        if (go.DrawObject == null) return;
        var view = new Rect32(Scroll.X, Scroll.Y, (int)Bounds.Width, (int)Bounds.Height);
        if (!FlowGeometry.HitTest(go.DrawObject, view))
            SetScroll(go.DrawObject.Position.X - FlowGeometry.IconHeight * 2, go.DrawObject.Position.Y - FlowGeometry.IconWidth * 2);
        InvalidateVisual();
    }

    // ================================================================== painting

    private static readonly Typeface CaptionTypeface = new("Verdana, DejaVu Sans, Arial");
    private const double CaptionSize = 8.666667;
    private static readonly Typeface TextTypeface = new("Microsoft Sans Serif, Arial, PingFang SC, Microsoft YaHei");
    private const double TextSize = 12;

    // Presentation colors deliberately do not modify the project's legacy FlowViewConfig or node artwork.
    private sealed class CanvasTheme(bool light)
    {
        public readonly IBrush CanvasBrush = new SolidColorBrush(Color.Parse(light ? "#FAFAFA" : "#202020"));
        public readonly IBrush TextBrush = new SolidColorBrush(Color.Parse(light ? "#252525" : "#E3E3E3"));
        public readonly IBrush IconTextBackground = new SolidColorBrush(Color.Parse(light ? "#E7E7E7" : "#333333"));
        public readonly IPen TextBoundPen = new Pen(new SolidColorBrush(Color.Parse(light ? "#CBCBCB" : "#555555")), 1);
        public readonly IBrush PortShowBrush = new SolidColorBrush(Color.Parse(light ? "#992467B2" : "#9969AAFF"));
        public readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse(light ? "#2467B2" : "#69AAFF"));
        public readonly IBrush SelectionBrush = new SolidColorBrush(Color.Parse(light ? "#332467B2" : "#3369AAFF"));
        public readonly IPen SelectionPen = new Pen(new SolidColorBrush(Color.Parse(light ? "#2467B2" : "#69AAFF")), 1);
        public readonly IPen SelectedBoundPen = new Pen(new SolidColorBrush(Color.Parse(light ? "#2467B2" : "#69AAFF")), 1, new DashStyle([3, 2], 0));
        public readonly IPen ConnectingPen = new Pen(new SolidColorBrush(Color.Parse(light ? "#2467B2" : "#69AAFF")), 1, new DashStyle([3, 1], 0));
        public readonly IPen NormalLinePen = new Pen(new SolidColorBrush(Color.Parse(light ? "#546575" : "#A0AAB4")), 3);
        public readonly IPen InvalidLinePen = new Pen(new SolidColorBrush(Color.Parse(light ? "#A5A5A5" : "#626262")), 3);
        public readonly IPen InvalidLineInsidePen = new Pen(new SolidColorBrush(Color.Parse(light ? "#686868" : "#A8A8A8")), 1, new DashStyle([3, 1], 0));
        public readonly IPen NormalLineInsidePen = new Pen(new SolidColorBrush(Color.Parse(light ? "#E1E6EB" : "#CCD2D8")), 1, new DashStyle([3, 1], 0));
    }

    private static readonly CanvasTheme LightTheme = new(true);
    private static readonly CanvasTheme DarkTheme = new(false);
    private CanvasTheme _theme;

    public override void Render(DrawingContext ctx)
    {
        var config = _doc?.Config ?? FlowViewConfig.Default;
        ctx.FillRectangle(_theme.CanvasBrush, new Rect(Bounds.Size));
        if (_doc == null) return;
        var scroll = Scroll;
        using (ctx.PushTransform(Matrix.CreateTranslation(-scroll.X, -scroll.Y)))
        {
            var clip = new Rect(scroll.X, scroll.Y, Bounds.Width, Bounds.Height);
            DrawObjects(ctx, clip, config);
            if (_working != FlowWorking.DragObject)
                foreach (var go in _doc.Selection)
                    if (go.DrawObject != null) DrawRect(ctx, _theme.SelectedBoundPen, FlowGeometry.GetSelectedBounds(go.DrawObject));
            if (_working == FlowWorking.DragSelection)
            {
                var r = Rect32.Real(_selectionRect);
                ctx.FillRectangle(_theme.SelectionBrush, new Rect(r.X, r.Y, r.Width, r.Height));
                DrawRect(ctx, _theme.SelectionPen, r);
            }
            if (_working == FlowWorking.Connect && _doc.Wire.StartPort != null)
                foreach (var (a, b) in _doc.Wire.PreviewSegments())
                    ctx.DrawLine(_theme.ConnectingPen, Px(a), Px(b));
            if (_working == FlowWorking.DragObject)
            {
                int dx = _dragPos.X - _dragStart.X, dy = _dragPos.Y - _dragStart.Y;
                foreach (var go in _doc.Selection)
                    if (go.DrawObject != null) DrawRect(ctx, _theme.SelectedBoundPen, FlowGeometry.GetSelectedBounds(go.DrawObject).Offset(dx, dy));
            }
        }
    }

    /// <summary>GDI+ draws pens on pixel centres at integer coordinates; Avalonia's pixel centres are at +0.5.</summary>
    private static AvPoint Px(Point p) => new(p.X + 0.5, p.Y + 0.5);

    private static void DrawRect(DrawingContext ctx, IPen pen, Rect32 r) =>
        ctx.DrawRectangle(null, pen, new Rect(r.X + 0.5, r.Y + 0.5, r.Width, r.Height));

    /// <summary>DesignSurface.DrawObjects: all lines first, then the other objects in stored order.</summary>
    private void DrawObjects(DrawingContext ctx, Rect clip, FlowViewConfig config)
    {
        var graph = _doc!.Session.Graph;
        foreach (var go in Objects)
        {
            if (go.DrawObject is not LineDraw line) continue;
            var invalid = graph?.State(go).LineState == LineState.Invalid;
            var a = Px(line.Position);
            var b = Px(line.EndPosition);
            ctx.DrawLine(invalid ? _theme.InvalidLinePen : _theme.NormalLinePen, a, b);
            ctx.DrawLine(invalid ? _theme.InvalidLineInsidePen : _theme.NormalLineInsidePen, a, b);
        }
        var size = CurrentPortSize;
        foreach (var go in Objects)
        {
            switch (go.DrawObject)
            {
                case LineDraw:
                    continue;
                case IconDraw icon:
                    DrawIcon(ctx, clip, go, icon, graph, config);
                    break;
                case TextBaseDraw text:
                    DrawText(ctx, text);
                    break;
            }
            if (_lastFoundPort != null)
            {
                if (ReferenceEquals(_lastFoundPort.Parent, go.DrawObject))
                    FillRect(ctx, _theme.AccentBrush, FlowGeometry.PortRect(_lastFoundPort, size));
            }
            else if (ReferenceEquals(go, _lastFoundObj))
            {
                foreach (var p in go.DrawObject!.Ports ?? [])
                    FillRect(ctx, _theme.PortShowBrush, FlowGeometry.PortRect(p, size));
            }
        }
    }

    private static void FillRect(DrawingContext ctx, IBrush brush, Rect32 r) => ctx.FillRectangle(brush, new Rect(r.X, r.Y, r.Width, r.Height));

    /// <summary>Original module artwork, greyed when not reached by code and not hovered.</summary>
    private void DrawIcon(DrawingContext ctx, Rect clip, GraphicObject go, IconDraw icon, FlowGraph? graph, FlowViewConfig config)
    {
        var rect = new Rect(icon.Position.X, icon.Position.Y, icon.Size.Width, icon.Size.Height);
        if (clip.Intersects(rect))
        {
            var normal = graph?.State(go).IsInCode == true || ReferenceEquals(_hoverNormal, go);
            var image = normal ? FlowImages.Decode(icon.Image) : FlowImages.DecodeGray(icon.Image, icon.Size.Width, icon.Size.Height);
            if (image != null) ctx.DrawImage(image, new Rect(image.Size), rect);
        }
        var text = CaptionOf(go, config);
        if (string.IsNullOrEmpty(text)) return;
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, CaptionTypeface, CaptionSize, _theme.TextBrush);
        var w = MeasureWidth(ft, CaptionSize);
        var h = ft.Height;
        var textRect = new Rect(icon.Position.X, icon.Position.Y - (h + 4), w, h);
        var box = new Rect(textRect.X - 1, textRect.Y - 1, w + 2, h + 2);
        if (!clip.Intersects(box)) return;
        ctx.FillRectangle(_theme.IconTextBackground, box);
        ctx.DrawText(ft, new AvPoint(textRect.X + CaptionSize / 6, textRect.Y + 1));
        ctx.DrawRectangle(null, _theme.TextBoundPen, box.Translate(new Vector(0.5, 0.5)));
    }

    /// <summary>GDI+ MeasureString pads 1/6 em on each side of the text.</summary>
    private static double MeasureWidth(FormattedText ft, double emSize) => ft.WidthIncludingTrailingWhitespace + emSize / 3;

    /// <summary>IconDraw.Draw caption: Function → first ShowFuncParamCount args; If/While → Code; Calculate → Code w/o ';'; For → LoopCountStr.</summary>
    public static string? CaptionOf(GraphicObject go, FlowViewConfig config)
    {
        var data = go.DataObject;
        if (data is FuncData f) return config.ShowFuncParamCount > 0 ? f.GetParamString(config.ShowFuncParamCount) : null;
        if (!config.ShowCtrlIconCode || data == null || string.IsNullOrEmpty(data.Code)) return null;
        return data switch
        {
            BeginIfData or BeginWhileData => data.Code,
            CalculateData => data.Code!.EndsWith(';') ? data.Code[..^1] : data.Code,
            BeginForData bf => bf.LoopCountStr,
            _ => null,
        };
    }

    /// <summary>TextBaseDraw.Draw with workspace colors; original measured size remains the object's hit area.</summary>
    private void DrawText(DrawingContext ctx, TextBaseDraw text)
    {
        var s = text.Text ?? "";
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, TextTypeface, TextSize, _theme.TextBrush);
        var w = MeasureWidth(ft, TextSize);
        var h = ft.Height;
        var box = new Rect(text.Position.X - 1, text.Position.Y - 1, w + 1, h + 1);
        var size = new RoboExp.Core.Model.Size((int)Math.Round(box.Width), (int)Math.Round(box.Height));
        if (text.Size != size) text.Size = size;
        ctx.FillRectangle(_theme.IconTextBackground, box);
        ctx.DrawText(ft, new AvPoint(text.Position.X + TextSize / 6, text.Position.Y + 1));
        ctx.DrawRectangle(null, _theme.TextBoundPen, box.Translate(new Vector(0.5, 0.5)));
    }

    // ================================================================== scroll bars (DesignSurface.UpdateScrollBarsValue)

    /// <summary>(maximum, value) of the horizontal and vertical scroll bars in 30px steps.</summary>
    public (int HMax, int HValue, int VMax, int VValue) ScrollBarState()
    {
        var drawing = FlowGeometry.GetDrawingObjectsRect(Objects);
        var view = new Rect32(Scroll.X, Scroll.Y, (int)Bounds.Width, (int)Bounds.Height);
        Rect32 all;
        if (drawing.IsEmpty) all = view;
        else
        {
            var x = Math.Min(drawing.X, view.X);
            var y = Math.Min(drawing.Y, view.Y);
            all = new Rect32(x, y, Math.Max(drawing.Right, view.Right) - x, Math.Max(drawing.Bottom, view.Bottom) - y);
        }
        static (int Max, int Value) Axis(int viewStart, int allStart, int viewEnd, int allEnd)
        {
            int max = 0, value = 0;
            var d = viewStart - allStart;
            if (d > 0) max = value = (int)Math.Floor(d / (float)ScrollBarScale);
            d = viewEnd - allEnd;
            if (d < 0) max += (int)Math.Floor(Math.Abs(d) / (float)ScrollBarScale);
            return (max, value);
        }
        var h = Axis(view.X, all.X, view.Right, all.Right);
        var v = Axis(view.Y, all.Y, view.Bottom, all.Bottom);
        return (h.Max, h.Value, v.Max, v.Value);
    }

    // ================================================================== mouse

    private KeyModifiers CommandModifier =>
        Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    /// <summary>Original IsControlKeyDown: exactly the command modifier (Ctrl; Cmd on macOS).</summary>
    private bool IsCtrl(KeyModifiers mods) => mods == CommandModifier;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_doc == null) return;
        Focus();
        var props = e.GetCurrentPoint(this).Properties;
        var point = PageToWorld(e.GetPosition(this));
        var ctrl = IsCtrl(e.KeyModifiers);

        if (props.IsLeftButtonPressed && PendingPlacement is { } placing)
        {
            // FlowView.m_DrawPanel_MouseDown with a pending context-menu icon: place it, nothing else.
            PendingPlacement = null;
            _doc.AddIcon(placing, point);
            Cursor = FlowCursors.AutoToolOver;
            e.Handled = true;
            return;
        }
        if (props.IsRightButtonPressed) PendingPlacement = null;

        _doc.LastMouseDownPos = point;
        var go = FlowGeometry.FindObjectAtPointB(Objects, point);
        var port = FlowGeometry.FindPort(go, point, CurrentPortSize);
        var wire = _doc.Wire;

        if (props.IsRightButtonPressed)
        {
            _working = FlowWorking.NormalMove;
            wire.Reset();
        }
        else if (props.IsLeftButtonPressed)
        {
            switch (_doc.Tool)
            {
                case FlowTool.AutoTool:
                    if (go == null)
                    {
                        if (_working == FlowWorking.NormalMove)
                        {
                            Cursor = FlowCursors.Click;
                            _working = FlowWorking.DragSelection;
                            _selectionRect = new Rect32(point.X, point.Y, 0, 0);
                        }
                        else if (_working == FlowWorking.Connect)
                        {
                            if (wire.StartPort != null)
                            {
                                wire.AddPoint(point);
                                wire.MovingPoint = point;
                            }
                            else
                            {
                                wire.Reset();
                                _working = FlowWorking.NormalMove;
                            }
                        }
                    }
                    else if (_working == FlowWorking.NormalMove)
                    {
                        if (port != null)
                        {
                            wire.Reset();
                            wire.StartPort = port;
                            _working = FlowWorking.Connect;
                            Cursor = FlowCursors.Connect;
                            go = null;
                            _doc.ClearSelection();
                            break;
                        }
                        Cursor = FlowCursors.Select;
                        _working = FlowWorking.DragObject;
                        _dragStart = _dragPos = point;
                    }
                    else if (_working == FlowWorking.Connect)
                    {
                        go = null;
                        if (port != null && wire.StartPort != null && !ReferenceEquals(wire.StartPort, port) && !ReferenceEquals(wire.StartPort.Parent, port.Parent))
                        {
                            Cursor = FlowCursors.AutoToolOver;
                            _working = FlowWorking.NormalMove;
                            wire.SetEndPort(port);
                            _doc.FinishWire();
                        }
                    }
                    break;
                case FlowTool.Select:
                    if (go == null)
                    {
                        Cursor = FlowCursors.Click;
                        _working = FlowWorking.DragSelection;
                        _selectionRect = new Rect32(point.X, point.Y, 0, 0);
                        break;
                    }
                    port = null;
                    Cursor = FlowCursors.Select;
                    _working = FlowWorking.DragObject;
                    _dragStart = _dragPos = point;
                    break;
                case FlowTool.Connect:
                    go = null;
                    if (port != null)
                    {
                        if (_working == FlowWorking.Connect && wire.StartPort != null && !ReferenceEquals(wire.StartPort, port) && !ReferenceEquals(wire.StartPort.Parent, port.Parent))
                        {
                            _working = FlowWorking.NormalMove;
                            wire.SetEndPort(port);
                            _doc.FinishWire();
                        }
                        else
                        {
                            wire.Reset();
                            _working = FlowWorking.Connect;
                            wire.StartPort = port;
                        }
                    }
                    else if (_working == FlowWorking.Connect && wire.StartPort != null)
                    {
                        wire.AddPoint(point);
                        wire.MovingPoint = point;
                    }
                    break;
                case FlowTool.Scroll:
                    go = null;
                    port = null;
                    _working = FlowWorking.Scroll;
                    _scrollStart = e.GetPosition(this);
                    break;
                case FlowTool.Text:
                    TextRequested?.Invoke(this, point);
                    break;
            }
        }
        _lastFoundObj = go;
        _lastFoundPort = port;
        if (props.IsRightButtonPressed && go != null) _doc.SetSelection([go]);
        else UpdateSelectedObjectsAfterMouseDown(go, e.ClickCount > 1, ctrl);
        if (_working is FlowWorking.Connect or FlowWorking.DragSelection or FlowWorking.DragObject) e.Pointer.Capture(this);
        InvalidateVisual();

        if (props.IsLeftButtonPressed && e.ClickCount == 2) Dispatcher.UIThread.Post(OnDoubleClick);
        e.Handled = true;
    }

    /// <summary>DesignSurface.UpdateSelectedObjectsAfterMouseDown.</summary>
    private void UpdateSelectedObjectsAfterMouseDown(GraphicObject? go, bool isDoubleClick, bool ctrl)
    {
        var doc = _doc!;
        if (go == null)
        {
            if (!ctrl) doc.ClearSelection();
            return;
        }
        var selected = doc.Selection.Contains(go);
        if (!isDoubleClick)
        {
            if (!ctrl)
            {
                if (!selected) doc.SetSelection([go]);
            }
            else if (!selected) doc.AddToSelection(go);
            else _selectedObjectNeedToRemove = go;
            return;
        }
        if (go.DrawObject is not LineDraw line) return;
        var chain = FlowEditor.GetConnectLines(line);
        if (ctrl && !selected) doc.SetSelection(doc.Selection.Where(s => !chain.Contains(s)));
        else doc.SetSelection(doc.Selection.Concat(chain.Where(c => !doc.Selection.Contains(c))));
    }

    /// <summary>FlowView.m_DrawPanel_DoubleClick: nothing selected → code window; one non-line → its property editor.</summary>
    private void OnDoubleClick()
    {
        if (_doc == null) return;
        if (_doc.Selection.Count == 0) ShowCodeRequested?.Invoke(this, EventArgs.Empty);
        else if (_doc.Selection.Count == 1 && _doc.Selection[0].DrawObject is not LineDraw and not null)
        {
            _working = FlowWorking.NormalMove;
            EditRequested?.Invoke(this, _doc.Selection[0]);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_doc == null) return;
        var pos = e.GetPosition(this);
        var point = PageToWorld(pos);
        var go = FlowGeometry.FindObjectAtPointB(Objects, point);
        var port = FlowGeometry.FindPort(go, point, CurrentPortSize);
        var wire = _doc.Wire;
        switch (_doc.Tool)
        {
            case FlowTool.AutoTool:
                if (_working == FlowWorking.NormalMove)
                {
                    if (PendingPlacement == null)
                        Cursor = go == null ? FlowCursors.AutoToolOver : port != null ? FlowCursors.Connect : FlowCursors.Select;
                }
                else if (_working == FlowWorking.DragObject)
                {
                    go = null;
                    port = null;
                    _dragPos = point;
                    AutoScrollOffset(point);
                }
                else if (_working == FlowWorking.Connect)
                {
                    wire.MovingPoint = point;
                    AutoScrollOffset(point);
                }
                else if (_working == FlowWorking.DragSelection)
                {
                    go = null;
                    port = null;
                    _selectionRect = _selectionRect with { Width = point.X - _selectionRect.X, Height = point.Y - _selectionRect.Y };
                    AutoScrollOffset(point);
                }
                break;
            case FlowTool.Select:
                if (go != null && port != null) port = null;
                if (_working == FlowWorking.NormalMove) Cursor = go != null ? FlowCursors.Select : FlowCursors.SelectOver;
                else if (_working == FlowWorking.DragObject)
                {
                    _dragPos = point;
                    AutoScrollOffset(point);
                }
                else if (_working == FlowWorking.DragSelection)
                {
                    _selectionRect = _selectionRect with { Width = point.X - _selectionRect.X, Height = point.Y - _selectionRect.Y };
                    AutoScrollOffset(point);
                }
                break;
            case FlowTool.Connect:
                Cursor = FlowCursors.Connect;
                if (_working == FlowWorking.Connect && wire.StartPort != null) wire.MovingPoint = point;
                AutoScrollOffset(point);
                break;
            case FlowTool.Scroll:
                go = null;
                port = null;
                if (_working == FlowWorking.Scroll)
                {
                    var dx = (int)(_scrollStart.X - pos.X);
                    var dy = (int)(_scrollStart.Y - pos.Y);
                    _scrollStart = pos;
                    ScrollBy(dx, dy);
                    return;
                }
                break;
            case FlowTool.Text:
                go = null;
                port = null;
                break;
        }
        // Hovering an icon draws it in colour even when it is not reached by code (IconDraw.IsNeedDrawNormal). The
        // original sets the flag when the pointer enters the icon and only repaints the icon again when it leaves, so
        // the icon stays coloured while hovered; entering straight from another object does not un-grey it.
        if (go?.DrawObject is not IconDraw) _hoverNormal = null;
        else if (_lastFoundObj == null) _hoverNormal = go;
        else if (!ReferenceEquals(_lastFoundObj, go)) _hoverNormal = null;
        var lastObj = _lastFoundObj;
        var lastPort = _lastFoundPort;
        _lastFoundObj = go;
        _lastFoundPort = port;
        InvalidateVisual();
        UpdateToolTip(go, port, lastObj, lastPort);
    }

    /// <summary>DesignSurface.OnMouseMove tooltip (ConfigInfo IsShow*OnTip flags).</summary>
    private void UpdateToolTip(GraphicObject? go, Port? port, GraphicObject? lastObj, Port? lastPort)
    {
        var doc = _doc!;
        if (doc.Tool is not (FlowTool.AutoTool or FlowTool.Select) || _working != FlowWorking.NormalMove || go == null || port != null || go.DrawObject is not IconDraw)
        {
            ToolTip.SetTip(this, null);
            return;
        }
        if (ReferenceEquals(lastObj, go) && ReferenceEquals(lastPort, port)) return;
        var config = doc.Config;
        var sb = new StringBuilder();
        void Append(string? s)
        {
            if (string.IsNullOrEmpty(s)) return;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(s);
        }
        var data = go.DataObject;
        if (data is FuncData)
        {
            if (config.ShowFuncCodeOnTip) Append(data.Code);
            if (config.ShowFuncParamOnTip && doc.Session.Data.GetUsedProjectInfo(data.ProjectName ?? "") is { } info) Append(ReturnParamTip(info));
            if (config.ShowFuncDescriptionOnTip && data.Description != null) Append(data.GetDescription());
        }
        else if (data != null)
        {
            if (config.ShowCtrlIconCodeOnTip) Append(data.Code);
            if (config.ShowCtrlIconDescriptionOnTip && data.Description != null) Append(data.GetDescription());
        }
        var tip = sb.ToString().Replace("\r\n", "\n");
        ToolTip.SetTip(this, tip.Length == 0 ? null : tip);
    }

    /// <summary>ProjectInfo.GetReturnParamTip.</summary>
    private static string ReturnParamTip(RoboExp.Core.Model.ProjectInfo info)
    {
        var sb = new StringBuilder();
        if (info.ReturnType?.Type is { Length: > 0 } rt && rt != "void")
            sb.Append("return: " + rt + "; ").Append(info.ReturnType.Description);
        foreach (var p in (IEnumerable<RoboExp.Core.Model.ParameterClass>?)info.ParamList ?? [])
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(p.Name).Append(": " + p.Type + "; ").Append(p.Description);
        }
        return sb.ToString();
    }

    /// <summary>DesignSurface.AutoScrollOffset (330ms throttle; the horizontal step is derived from the right edge for both sides).</summary>
    private bool AutoScrollOffset(Point mouse)
    {
        var tick = Environment.TickCount;
        if (tick - _lastTick < 330) return false;
        _lastTick = tick;
        var scroll = Scroll;
        int sx = scroll.X, sy = scroll.Y;
        var result = false;
        var num = mouse.X - (scroll.X + (int)Bounds.Width);
        var step = 0;
        if (num != 0) step = num + 2000 / num;
        step = Math.Clamp(step, -200, 200);
        if (num > 0) { sx += step; result = true; }
        num = mouse.X - scroll.X;
        if (num < 0) { sx += step; result = true; }
        var v = mouse.Y - (scroll.Y + (int)Bounds.Height);
        if (v > 0) { sy += v + 100; result = true; }
        v = mouse.Y - scroll.Y;
        if (v < 0) { sy += v - 100; result = true; }
        if (result) SetScroll(sx, sy);
        return result;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_doc == null) return;
        var point = PageToWorld(e.GetPosition(this));
        var go = FlowGeometry.FindObjectAtPointB(Objects, point);
        var ctrl = IsCtrl(e.KeyModifiers);
        var doc = _doc;

        if (e.InitialPressMouseButton == MouseButton.Right)
        {
            var popupTypes = doc.Session.Type.SoftwareType is not (RoboExp.Core.Model.SoftwareType.X3RCU or RoboExp.Core.Model.SoftwareType.X4RCU);
            if (go != null && doc.Selection.Count == 1 && doc.Selection.Contains(go) && go.DrawObject is not LineDraw && popupTypes
                && !_ctrlDown && (e.KeyModifiers & KeyModifiers.Control) == 0)
                EditRequested?.Invoke(this, go);
            else if (_working != FlowWorking.Connect)
                ContextMenuRequested?.Invoke(this, e.GetPosition(this));
            _ctrlDown = false;
        }

        switch (doc.Tool)
        {
            case FlowTool.AutoTool:
            case FlowTool.Select:
                if (_working == FlowWorking.DragSelection)
                {
                    UpdateSelectedObjectsAfterSelectionDrag(Rect32.Real(_selectionRect), ctrl);
                }
                else if (_working == FlowWorking.DragObject)
                {
                    int dx = point.X - _dragStart.X, dy = point.Y - _dragStart.Y;
                    var copy = doc.Tool == FlowTool.AutoTool ? dx != 0 || dy != 0 : dx > 0 || dy > 0;
                    _working = FlowWorking.NormalMove;
                    if (ctrl && copy) doc.CopySelectionWithOffset(dx, dy);
                    else doc.MoveSelection(dx, dy);
                }
                if (_selectedObjectNeedToRemove != null) doc.RemoveFromSelection(_selectedObjectNeedToRemove);
                _selectedObjectNeedToRemove = null;
                if (doc.Tool == FlowTool.AutoTool)
                {
                    if (_working == FlowWorking.Connect) break;
                    var found = FlowGeometry.FindObjectAtPointB(Objects, point);
                    var port = FlowGeometry.FindPort(found, point, CurrentPortSize);
                    Cursor = found == null ? FlowCursors.AutoToolOver : port != null ? FlowCursors.Connect : FlowCursors.Select;
                }
                else
                {
                    Cursor = FlowGeometry.FindObjectAtPoint(Objects, point) != null ? FlowCursors.Select : FlowCursors.SelectOver;
                }
                break;
            case FlowTool.Scroll:
            case FlowTool.Text:
                _working = FlowWorking.NormalMove;
                break;
        }
        if (_working != FlowWorking.Connect) _working = FlowWorking.NormalMove;
        if (_working != FlowWorking.Connect) e.Pointer.Capture(null);
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>DesignSurface.UpdateSelectedObjectsAfterSelectionDrag: objects intersecting the marquee (Ctrl toggles).</summary>
    private void UpdateSelectedObjectsAfterSelectionDrag(Rect32 rect, bool ctrl)
    {
        var doc = _doc!;
        var hits = Objects.Where(go => go.DrawObject != null && FlowGeometry.HitTest(go.DrawObject, rect)).ToList();
        if (!ctrl)
        {
            doc.SetSelection(hits);
            return;
        }
        var selection = doc.Selection.ToList();
        foreach (var go in hits)
        {
            if (!selection.Contains(go)) selection.Add(go);
            else _selectedObjectNeedToRemove = go;
        }
        doc.SetSelection(selection);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _wheelRemainder += e.Delta.Y;
        var notches = (int)_wheelRemainder;
        if (notches == 0) return;
        _wheelRemainder -= notches;
        ScrollBy(0, -notches * ScrollBarScale);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_working is FlowWorking.DragObject or FlowWorking.DragSelection) _working = FlowWorking.NormalMove;
        InvalidateVisual();
    }

    // ================================================================== keys (DesignSurface.ProcessCmdKey / OnKeyDown / OnKeyUp)

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_doc == null) return;
        var doc = _doc;
        var cmd = CommandModifier;
        _ctrlDown = false;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LWin or Key.RWin)
        {
            _ctrlDown = true;
            if (_working == FlowWorking.DragObject) Cursor = FlowCursors.DragCopy;
            return;
        }
        var handled = true;
        switch (e.Key)
        {
            case Key.Up when e.KeyModifiers == cmd: PageUp(); break;
            case Key.Down when e.KeyModifiers == cmd: PageDown(); break;
            case Key.Left when e.KeyModifiers == cmd: PageLeft(); break;
            case Key.Right when e.KeyModifiers == cmd: PageRight(); break;
            case Key.Up: ScrollBy(0, -ScrollBarScale); break;
            case Key.Down: ScrollBy(0, ScrollBarScale); break;
            case Key.Left: ScrollBy(-ScrollBarScale, 0); break;
            case Key.Right: ScrollBy(ScrollBarScale, 0); break;
            case Key.PageUp: PageUp(); break;
            case Key.PageDown: PageDown(); break;
            case Key.Home: Home(); break;
            case Key.End: End(); break;
            case Key.Escape:
                if (_working is FlowWorking.DragObject or FlowWorking.DragSelection) _working = FlowWorking.NormalMove;
                else if (doc.Wire.StartPort != null || _working == FlowWorking.Connect)
                {
                    _working = FlowWorking.NormalMove;
                    doc.Wire.Reset();
                }
                PendingPlacement = null;
                InvalidateVisual();
                break;
            case Key.Space:
                if (_working == FlowWorking.Connect)
                {
                    if (doc.Wire.StartPort != null) doc.Wire.IsHorizontal = !doc.Wire.IsHorizontal;
                    InvalidateVisual();
                }
                else if (doc.Tool == FlowTool.Select) doc.Tool = FlowTool.Connect;
                else if (doc.Tool == FlowTool.Connect) doc.Tool = FlowTool.Select;
                else if (doc.Tool == FlowTool.AutoTool) doc.Tool = FlowTool.Scroll;
                break;
            case Key.F5:
                doc.RemoveDanglingWires();
                break;
            case Key.Delete or Key.Back when e.KeyModifiers == KeyModifiers.None:
                doc.Delete();
                break;
            case Key.Enter when doc.Selection.Count == 1 && doc.Selection[0].DrawObject is not LineDraw and not null:
                EditRequested?.Invoke(this, doc.Selection[0]);
                break;
            case Key.Z when e.KeyModifiers == cmd: doc.DoUndo(); break;
            case Key.Z when e.KeyModifiers == (cmd | KeyModifiers.Shift): doc.DoRedo(); break;
            case Key.Y when e.KeyModifiers == cmd: doc.DoRedo(); break;
            case Key.X when e.KeyModifiers == cmd: e.Handled = true; await doc.CutAsync(); break;
            case Key.C when e.KeyModifiers == cmd: e.Handled = true; await doc.CopyAsync(); break;
            case Key.V when e.KeyModifiers == cmd: e.Handled = true; await doc.PasteAsync(); break;
            case Key.A when e.KeyModifiers == cmd: doc.SelectAll(); break;
            default:
                handled = false;
                break;
        }
        e.Handled = handled;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_doc == null) return;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LWin or Key.RWin)
        {
            if (_working == FlowWorking.DragObject) Cursor = FlowCursors.Select;
        }
        else if (e.Key == Key.Space && _doc.Tool == FlowTool.Scroll)
        {
            _doc.Tool = FlowTool.AutoTool;
        }
        _ctrlDown = false;
    }

    private Rect32 ViewRect => new(Scroll.X, Scroll.Y, (int)Bounds.Width, (int)Bounds.Height);

    private void PageUp()
    {
        var d = FlowGeometry.GetDrawingObjectsRect(Objects);
        if (d.IsEmpty) return;
        var n = Math.Max(Scroll.Y - ViewRect.Height + FlowGeometry.IconHeight, d.Top);
        if (Scroll.Y > n) SetScroll(Scroll.X, n);
    }

    private void PageLeft()
    {
        var d = FlowGeometry.GetDrawingObjectsRect(Objects);
        if (d.IsEmpty) return;
        var n = Math.Max(Scroll.X - ViewRect.Width + FlowGeometry.IconWidth, d.Left);
        if (Scroll.X > n) SetScroll(n, Scroll.Y);
    }

    private void PageDown()
    {
        var d = FlowGeometry.GetDrawingObjectsRect(Objects);
        if (d.IsEmpty) return;
        var n = Math.Min(Scroll.Y + ViewRect.Height - FlowGeometry.IconHeight, d.Bottom - ViewRect.Height);
        if (Scroll.Y < n) SetScroll(Scroll.X, n);
    }

    private void PageRight()
    {
        var d = FlowGeometry.GetDrawingObjectsRect(Objects);
        if (d.IsEmpty) return;
        var n = Math.Min(Scroll.X + ViewRect.Width - FlowGeometry.IconWidth, d.Right - ViewRect.Width);
        if (Scroll.X < n) SetScroll(n, Scroll.Y);
    }

    private void Home()
    {
        var d = FlowGeometry.GetDrawingObjectsRect(Objects);
        if (!d.IsEmpty && (Scroll.X != d.X || Scroll.Y != d.Y)) SetScroll(d.X, d.Y);
    }

    private void End()
    {
        var d = FlowGeometry.GetDrawingObjectsRect(Objects);
        if (d.IsEmpty) return;
        var y = d.Bottom - Math.Min(d.Height, ViewRect.Height);
        if (Scroll.X != d.X || Scroll.Y != y) SetScroll(d.X, y);
    }

    // ================================================================== palette drop (FlowView.DrawPanel_DragDrop)

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = PaletteDragData.TryRead(e.DataTransfer) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_doc == null || PaletteDragData.TryRead(e.DataTransfer) is not { } item) return;
        _doc.AddIcon(item, PageToWorld(e.GetPosition(this)));
        Focus();
        e.Handled = true;
    }
}
