using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Search;
using RoboExp.App.Documents;
using RoboExp.App.ViewModels;

namespace RoboExp.App.Flow;

/// <summary>
/// FrmCodeApp (one editor) / FrmCodeFunc (global area, splitter, read-only declaration line, code area) with the same
/// editor setup as the C file editor.
/// </summary>
public sealed class CodeProjectDocumentView : UserControl, IEditCommands
{
    private CodeProjectDocumentViewModel? _vm;
    private readonly List<TextEditor> _editors = [];
    private readonly Dictionary<TextEditor, SearchPanel> _search = [];
    private TextEditor? _lastFocused;

    private TextEditor CreateEditor(TextDocument document, bool readOnly)
    {
        var editor = new TextEditor
        {
            Document = document,
            IsReadOnly = readOnly,
            ShowLineNumbers = !readOnly,
            FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono, PingFang SC, Microsoft YaHei, monospace"),
            FontSize = 13,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = readOnly ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        StudioCodeTheme.Apply(editor);
        editor.Options.ConvertTabsToSpaces = false;
        editor.Options.IndentationSize = 4;
        _search[editor] = SearchPanel.Install(editor);
        return editor;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.NavigationRequested -= OnNavigationRequested;
            if (_vm.EditTarget == this) _vm.EditTarget = null;
        }
        _vm = DataContext as CodeProjectDocumentViewModel;
        _editors.Clear();
        _search.Clear();
        if (_vm == null)
        {
            Content = null;
            return;
        }
        var code = CreateEditor(_vm.Code, false);
        if (_vm.Include != null && _vm.Declaration != null)
        {
            var include = CreateEditor(_vm.Include, false);
            var decl = CreateEditor(_vm.Declaration, true);
            decl.Height = 22;
            var grid = new Grid { RowDefinitions = new RowDefinitions("120,4,Auto,*") };
            grid.Children.Add(include);
            var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows };
            splitter.Bind(BackgroundProperty, new DynamicResourceExtension("StudioBorderBrush"));
            Grid.SetRow(splitter, 1);
            grid.Children.Add(splitter);
            var declBorder = new Border { BorderThickness = new Avalonia.Thickness(1), Child = decl };
            declBorder.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("StudioBorderBrush"));
            Grid.SetRow(declBorder, 2);
            grid.Children.Add(declBorder);
            Grid.SetRow(code, 3);
            grid.Children.Add(code);
            _editors.AddRange([include, decl, code]);
            Content = grid;
        }
        else
        {
            _editors.Add(code);
            Content = code;
        }
        foreach (var ed in _editors) ed.TextArea.GotFocus += (_, _) => _lastFocused = ed;
        _vm.NavigationRequested += OnNavigationRequested;
        _vm.EditTarget = this;
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm != null) _vm.EditTarget = this;
        (TopLevel.GetTopLevel(this)?.DataContext as MainWindowViewModel)?.NotifyEditTargetChanged();
        if (_vm?.PendingNavigation is { } nav) GoTo(nav.Document, nav.Line);
    }

    private void OnNavigationRequested(object? sender, (TextDocument Document, int Line) nav)
    {
        if (VisualRoot != null) GoTo(nav.Document, nav.Line);
    }

    private void GoTo(TextDocument document, int line)
    {
        if (_vm != null) _vm.PendingNavigation = null;
        var editor = _editors.FirstOrDefault(ed => ReferenceEquals(ed.Document, document));
        if (editor == null || document.LineCount == 0) return;
        var l = document.GetLineByNumber(Math.Clamp(line, 1, document.LineCount));
        editor.CaretOffset = l.Offset;
        editor.Select(l.Offset, l.Length);
        editor.ScrollTo(l.LineNumber, 1);
        editor.TextArea.Focus();
    }

    private TextEditor? Active => _lastFocused ?? _editors.LastOrDefault();

    public void Undo() => Active?.Undo();
    public void Redo() => Active?.Redo();
    public Task CutAsync() { Active?.Cut(); return Task.CompletedTask; }
    public Task CopyAsync() { Active?.Copy(); return Task.CompletedTask; }
    public Task PasteAsync() { Active?.Paste(); return Task.CompletedTask; }
    public void Delete() => Active?.Delete();
    public void SelectAll() => Active?.SelectAll();

    public void Find(bool replace)
    {
        if (Active is not { } editor || !_search.TryGetValue(editor, out var panel)) return;
        panel.IsReplaceMode = replace;
        panel.Open();
        panel.Reactivate();
    }
}
