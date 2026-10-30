using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Search;
using RoboExp.App.Documents;
using RoboExp.App.ViewModels;

namespace RoboExp.App.Views;

/// <summary>Editor for <see cref="CodeDocumentViewModel"/>: C highlighting, line numbers, find/replace (AvaloniaEdit).</summary>
public sealed class CodeDocumentView : UserControl, IEditCommands
{
    private readonly TextEditor _editor;
    private readonly SearchPanel _search;
    private CodeDocumentViewModel? _vm;

    public CodeDocumentView()
    {
        _editor = new TextEditor
        {
            ShowLineNumbers = true,
            FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono, PingFang SC, Microsoft YaHei, monospace"),
            FontSize = 13,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        StudioCodeTheme.Apply(_editor);
        _editor.Options.ConvertTabsToSpaces = false;
        _editor.Options.IndentationSize = 4;
        _search = SearchPanel.Install(_editor);
        Content = _editor;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.NavigationRequested -= OnNavigationRequested;
            _vm.InsertRequested -= OnInsertRequested;
            if (_vm.EditTarget == this) _vm.EditTarget = null;
        }
        _vm = DataContext as CodeDocumentViewModel;
        if (_vm == null) return;
        _editor.Document = _vm.Document;
        _vm.NavigationRequested += OnNavigationRequested;
        _vm.InsertRequested += OnInsertRequested;
        _vm.EditTarget = this;
        if (_vm.InitialCaretLine is { } line && _vm.PendingNavigation == null) GoTo(line, null, select: false);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm != null) _vm.EditTarget = this;
        (TopLevel.GetTopLevel(this)?.DataContext as MainWindowViewModel)?.NotifyEditTargetChanged();
        ConsumePendingNavigation();
        _editor.TextArea.Focus();
    }

    private void OnNavigationRequested(object? sender, EventArgs e) => ConsumePendingNavigation();

    private void ConsumePendingNavigation()
    {
        if (_vm?.PendingNavigation is not { } nav || VisualRoot == null) return;
        _vm.PendingNavigation = null;
        GoTo(nav.Line, nav.Column, select: true);
    }

    /// <summary>Original BaseView.SelectLine: caret to the line, line selected, scrolled into view.</summary>
    private void GoTo(int line, int? column, bool select)
    {
        var doc = _editor.Document;
        if (doc.LineCount == 0) return;
        var l = doc.GetLineByNumber(Math.Clamp(line, 1, doc.LineCount));
        var offset = l.Offset + Math.Clamp((column ?? 1) - 1, 0, l.Length);
        _editor.CaretOffset = offset;
        if (select) _editor.Select(l.Offset, l.Length);
        _editor.ScrollTo(l.LineNumber, column ?? 1);
        _editor.TextArea.Focus();
    }

    /// <summary>SysFuncData.InsertNodeText: continuation lines indented to the caret column; include added once.</summary>
    private void OnInsertRequested(object? sender, CodeTemplate template)
    {
        var doc = _editor.Document;
        var caret = _editor.CaretOffset;
        var line = doc.GetLineByOffset(caret);
        var indent = new string(' ', caret - line.Offset);
        var text = string.Join("\r\n" + indent, template.Code);
        doc.BeginUpdate();
        try
        {
            doc.Insert(caret, text);
            if (template.IncludeLine != null && !doc.Text.Contains(template.IncludeLine, StringComparison.Ordinal))
            {
                var at = 0;
                foreach (var l in doc.Lines)
                    if (doc.GetText(l).TrimStart().StartsWith("#include", StringComparison.Ordinal)) at = l.EndOffset + l.DelimiterLength;
                doc.Insert(at, template.IncludeLine + "\r\n");
            }
        }
        finally { doc.EndUpdate(); }
        _editor.TextArea.Focus();
    }

    public void Undo() => _editor.Undo();
    public void Redo() => _editor.Redo();
    public Task CutAsync() { _editor.Cut(); return Task.CompletedTask; }
    public Task CopyAsync() { _editor.Copy(); return Task.CompletedTask; }
    public Task PasteAsync() { _editor.Paste(); return Task.CompletedTask; }
    public void Delete() => _editor.Delete();
    public void SelectAll() => _editor.SelectAll();

    public void Find(bool replace)
    {
        _search.IsReplaceMode = replace;
        _search.Open();
        if (_editor.SelectionLength > 0 && !_editor.SelectedText.Contains('\n')) _search.SearchPattern = _editor.SelectedText;
        _search.Reactivate();
    }
}
