using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace RoboExp.App.Icons;

/// <summary>Shared vector artwork. Never used to replace the bitmaps persisted in an .rcu file.</summary>
public static class FlatIcons
{
    private static readonly Dictionary<string, IImage> Commands = new(StringComparer.Ordinal);

    static FlatIcons()
    {
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged += (_, _) =>
            {
                foreach (var icon in Commands.Values.OfType<VectorIcon>()) icon.RefreshTheme();
            };
    }

    public static IImage Command(string name)
    {
        if (Commands.TryGetValue(name, out var image)) return image;
        var (glyph, color) = name switch
        {
            "New" => ("file-plus", "#334155"), "Open" => ("folder-open", "#334155"),
            "Examples" => ("book-open", "#334155"), "Save" => ("save", "#334155"),
            "SaveAll" => ("save-all", "#334155"), "Print" => ("printer", "#334155"),
            "Exit" => ("log-out", "#334155"), "Undo" => ("undo-2", "#334155"),
            "Redo" => ("redo-2", "#334155"), "Cut" => ("scissors", "#334155"),
            "Copy" => ("copy", "#334155"), "Paste" => ("clipboard", "#334155"),
            "Delete" => ("trash-2", "#DC4455"), "SelectAll" => ("square-dashed-mouse-pointer", "#334155"),
            "Properties" or "Options" or "ProjectProperty" => ("sliders-horizontal", "#334155"), "VarDlg" => ("variable", "#334155"),
            "FuncIconDlg" => ("layout-grid", "#334155"), "CodeDlg" => ("code-xml", "#334155"),
            "TemplateCodeDlg" => ("file-code", "#334155"), "Output" => ("terminal", "#334155"),
            "HardwareInfo" => ("cpu", "#334155"), "Compile" => ("hammer", "#0D9488"),
            "Download" => ("download", "#2563EB"), "About" => ("info", "#2563EB"),
            "AutoTool" => ("mouse-pointer-2", "#334155"), "Scroll" => ("hand", "#334155"),
            "Select" => ("mouse-pointer-2", "#334155"), "Connect" => ("cable", "#334155"),
            "Text" => ("type", "#334155"), "AlignLeft" => ("align-start-vertical", "#334155"),
            "AlignRight" => ("align-end-vertical", "#334155"), "AlignTop" => ("align-start-horizontal", "#334155"),
            "AlignBottom" => ("align-end-horizontal", "#334155"), "AutoAlign" => ("workflow", "#334155"),
            "OpenSub" => ("folder-tree", "#334155"), "UpdateSub" => ("rotate-cw", "#334155"),
            "SystemLib" => ("book-open", "#334155"), "Cascade" => ("rows-3", "#334155"),
            "TileHorizontal" or "TileVertical" => ("grid-2x2", "#334155"),
            "Next" or "Prev" => ("arrow-left-right", "#334155"),
            "CompileSuccessful" => ("circle-check", "#059669"), "CompileWarning" => ("triangle-alert", "#C27C0E"),
            "CompileError" => ("circle-x", "#DC4455"),
            "DownloadUsb" => ("usb", "#2563EB"), "DownloadUsbIdle" => ("usb", "#8793A1"),
            "DownloadPower" => ("power", "#2563EB"), "DownloadPowerIdle" => ("power", "#8793A1"),
            "DownloadButton" => ("download", "#2563EB"), "DownloadButtonIdle" => ("download", "#8793A1"),
            _ => ("shapes", "#334155"),
        };
        return Commands[name] = new VectorIcon(glyph, color);
    }


    private sealed class VectorIcon : DrawingImage
    {
        private readonly Geometry[] _geometry;
        private readonly string _colorKey;
        private static readonly Dictionary<string, Geometry[]> GeometryCache = [];

        public VectorIcon(string glyph, string color)
        {
            if (!GeometryCache.TryGetValue(glyph, out var geometry))
                GeometryCache[glyph] = geometry = LucidePaths.Data[glyph].Select(Geometry.Parse).ToArray();
            _geometry = geometry;
            _colorKey = color;
            Viewbox = new Rect(0, 0, 32, 32);
            RefreshTheme();
        }

        public void RefreshTheme()
        {
            var dark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
            var color = (dark, _colorKey) switch
            {
                (true, "#334155") => "#D1D1D1",
                (true, "#2563EB") => "#69AAFF",
                (true, "#0D9488") => "#79C6A1",
                _ => _colorKey,
            };
            var pen = new Pen(new SolidColorBrush(Color.Parse(color)), 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            var drawing = new DrawingGroup();
            using (var context = drawing.Open())
            using (context.PushTransform(Matrix.CreateScale(1.16, 1.16) * Matrix.CreateTranslation(2.08, 2.08)))
                foreach (var shape in _geometry) context.DrawGeometry(null, pen, shape);
            Drawing = drawing;
        }

    }
}
