using System.Xml.Linq;
using Avalonia.Styling;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace RoboExp.App.Documents;

/// <summary>Workspace-only colors shared by the editable and generated C views.</summary>
internal static class StudioCodeTheme
{
    private sealed class EditorTheme(bool light)
    {
        public readonly IBrush Background = new SolidColorBrush(Color.Parse(light ? "#FAFAFA" : "#202020"));
        public readonly IBrush Foreground = new SolidColorBrush(Color.Parse(light ? "#252525" : "#E3E3E3"));
        public readonly IBrush Muted = new SolidColorBrush(Color.Parse(light ? "#606060" : "#A3A3A3"));
        public readonly IBrush Selection = new SolidColorBrush(Color.Parse(light ? "#D5E5F8" : "#385B85"));
        public readonly IBrush Accent = new SolidColorBrush(Color.Parse(light ? "#2467B2" : "#69AAFF"));
        public readonly IHighlightingDefinition Highlighting = CreateHighlighting(light);
    }

    private static readonly EditorTheme Light = new(true);
    private static readonly EditorTheme Dark = new(false);

    public static void Apply(TextEditor editor)
    {
        // The publisher is the editor itself, so detached documents do not leave global subscriptions.
        editor.ActualThemeVariantChanged -= OnThemeChanged;
        editor.ActualThemeVariantChanged += OnThemeChanged;
        Update(editor);
    }

    private static void OnThemeChanged(object? sender, EventArgs e)
    {
        if (sender is TextEditor editor) Update(editor);
    }

    private static void Update(TextEditor editor)
    {
        var theme = editor.ActualThemeVariant == ThemeVariant.Dark ? Dark : Light;
        editor.Background = theme.Background;
        editor.Foreground = theme.Foreground;
        editor.LineNumbersForeground = theme.Muted;
        editor.SyntaxHighlighting = theme.Highlighting;
        editor.TextArea.SelectionBrush = theme.Selection;
        editor.TextArea.SelectionForeground = theme.Foreground;
        editor.TextArea.CaretBrush = theme.Accent;
    }

    private static IHighlightingDefinition CreateHighlighting(bool light)
    {
        // Reuse AvaloniaEdit's complete C/C++ grammar, not a second language definition or a mutation
        // of HighlightingManager's shared (potentially frozen) colors.
        using var stream = typeof(HighlightingManager).Assembly.GetManifestResourceStream(
            "AvaloniaEdit.Highlighting.Resources.CPP-Mode.xshd")
            ?? throw new InvalidOperationException("AvaloniaEdit C/C++ highlighting resource is missing.");
        var definition = XDocument.Load(stream);
        foreach (var color in definition.Root!.Elements().Where(element => element.Name.LocalName == "Color"))
        {
            var foreground = light ? (string?)color.Attribute("name") switch
            {
                "Comment" => "#47713D",
                "String" or "Character" => "#98501C",
                "Digits" or "BooleanConstants" => "#3A6C30",
                "Preprocessor" => "#775093",
                "MethodName" => "#795B16",
                "TypeKeywords" or "Namespace" => "#166B70",
                "Punctuation" or "Operators" => "#454545",
                _ => "#245F9C",
            } : (string?)color.Attribute("name") switch
            {
                "Comment" => "#97AB8F",
                "String" or "Character" => "#D6B58D",
                "Digits" or "BooleanConstants" => "#AEC9A4",
                "Preprocessor" => "#BBA7CD",
                "MethodName" => "#D2C59D",
                "TypeKeywords" or "Namespace" => "#8DC5C2",
                "Punctuation" or "Operators" => "#C5C5C5",
                _ => "#8EB9EC",
            };
            color.SetAttributeValue("foreground", foreground);
        }
        using var reader = definition.CreateReader();
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
