using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using RoboExp.Core.Hardware;

namespace RoboExp.App.Hardware;

/// <summary>How a pin is marked on the controller picture (pens of HardwareInstFrm/HardwareTypeFrm/HardInfoDlg Paint).</summary>
public enum PinMarkStyle
{
    /// <summary>Taken by another instance: white outline with a cross.</summary>
    Used,
    /// <summary>Not allowed for the type: red outline with a cross.</summary>
    Limited,
    /// <summary>Selected: lime 2px outline, inset by 1px.</summary>
    Selected,
    /// <summary>Hover candidate of the instance dialog: cyan outline.</summary>
    Candidate,
    /// <summary>Hovered pin of the type dialog: blue outline.</summary>
    Hover,
}

public readonly record struct PinMark(PinKind Kind, int Index, PinMarkStyle Style);

/// <summary>
/// The controller picture (<c>RCUInfo.RCUPicture</c>) at its pixel size with pin marks drawn on top; reports the pin
/// under the pointer. Replaces the PictureBox + Paint handlers of the original hardware dialogs.
/// </summary>
public sealed class RcuPictureControl : Control
{
    private static readonly IPen WhitePen = new Pen(Brushes.White, 1);
    private static readonly IPen RedPen = new Pen(Brushes.Red, 1);
    private static readonly IPen LimePen = new Pen(Brushes.Lime, 2);
    private static readonly IPen CyanPen = new Pen(new SolidColorBrush(Color.FromRgb(0, 255, 255)), 1);
    private static readonly IPen BluePen = new Pen(Brushes.Blue, 1);

    private RcuBoard? _board;
    private Bitmap? _bitmap;
    private IReadOnlyList<PinMark> _marks = [];
    private (PinKind Kind, int Index)? _hoverPin;

    /// <summary>Pointer moved onto another pin (or off all pins: null).</summary>
    public event Action<(PinKind Kind, int Index)?>? HoverPinChanged;

    /// <summary>Left click; argument is the pin under the pointer (null on blank picture).</summary>
    public event Action<(PinKind Kind, int Index)?>? PinClicked;

    public void SetBoard(RcuBoard? board, Bitmap? bitmap)
    {
        _board = board;
        _bitmap = bitmap;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public IReadOnlyList<PinMark> Marks
    {
        get => _marks;
        set
        {
            _marks = value;
            InvalidateVisual();
        }
    }

    /// <summary>Hand cursor over a selectable pin (set by the owner after evaluating the hover).</summary>
    public bool ShowHandCursor
    {
        set => Cursor = value ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        _bitmap == null ? default : new Size(_bitmap.PixelSize.Width, _bitmap.PixelSize.Height);

    public override void Render(DrawingContext context)
    {
        if (_bitmap == null || _board == null) return;
        var size = new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height);
        context.DrawImage(_bitmap, size);
        foreach (var mark in _marks) DrawMark(context, mark);
    }

    private void DrawMark(DrawingContext g, PinMark mark)
    {
        var pins = _board!.Pins(mark.Kind);
        if (mark.Index < 0 || mark.Index >= pins.Count) return;
        var p = pins[mark.Index];
        double w = _board.PinWidth, h = _board.PinHeight;
        // GDI+ DrawRectangle(x, y, w - 1, h - 1) covers pixels x..x+w-1; offset by half a pixel for crisp 1px lines.
        var outer = new Rect(p.X + 0.5, p.Y + 0.5, w - 1, h - 1);
        switch (mark.Style)
        {
            case PinMarkStyle.Used:
            case PinMarkStyle.Limited:
                var pen = mark.Style == PinMarkStyle.Used ? WhitePen : RedPen;
                Outline(g, pen, outer);
                g.DrawLine(pen, outer.TopLeft, outer.BottomRight);
                g.DrawLine(pen, outer.TopRight, outer.BottomLeft);
                break;
            case PinMarkStyle.Selected:
                Outline(g, LimePen, new Rect(p.X + 1, p.Y + 1, w - 2, h - 2));
                break;
            case PinMarkStyle.Candidate:
                Outline(g, CyanPen, outer);
                break;
            case PinMarkStyle.Hover:
                Outline(g, BluePen, outer);
                break;
        }
    }

    private void Outline(DrawingContext g, IPen pen, Rect r)
    {
        if (_board!.IsCircle) g.DrawEllipse(null, pen, r);
        else g.DrawRectangle(null, pen, r);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        var pin = _board?.HitTest(pos.X, pos.Y);
        if (pin == _hoverPin) return;
        _hoverPin = pin;
        HoverPinChanged?.Invoke(pin);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoverPin == null) return;
        _hoverPin = null;
        HoverPinChanged?.Invoke(null);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var pos = e.GetPosition(this);
        PinClicked?.Invoke(_board?.HitTest(pos.X, pos.Y));
    }

    /// <summary>Decodes the board picture; null when absent or undecodable.</summary>
    public static Bitmap? DecodePicture(RcuBoard? board)
    {
        if (board?.Picture is not { Length: > 0 } bytes) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            return null;
        }
    }
}
