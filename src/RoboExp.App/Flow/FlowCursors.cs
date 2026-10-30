using Avalonia.Input;

namespace RoboExp.App.Flow;

/// <summary>Platform cursors stay sharp at the display's scale and use the OS's correct hot spots.</summary>
public static class FlowCursors
{
    public static Cursor AutoToolOver { get; } = new(StandardCursorType.Cross);
    public static Cursor SelectOver { get; } = new(StandardCursorType.Arrow);
    public static Cursor Select { get; } = new(StandardCursorType.Arrow);
    public static Cursor Click { get; } = new(StandardCursorType.Cross);
    public static Cursor Connect { get; } = new(StandardCursorType.Cross);
    public static Cursor Scroll { get; } = new(StandardCursorType.SizeAll);
    public static Cursor Text { get; } = new(StandardCursorType.Ibeam);
    public static Cursor DragCopy { get; } = new(StandardCursorType.DragCopy);
}
