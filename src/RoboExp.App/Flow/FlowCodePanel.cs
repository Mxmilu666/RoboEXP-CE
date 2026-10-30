using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using RoboExp.App.Documents;

namespace RoboExp.App.Flow;

/// <summary>
/// Content of the Code window (original DockCodeBox): the generated &lt;name&gt;.c of the active flow document, refreshed
/// after every model change (the original regenerated on every repaint), with the selected icon's lines highlighted.
/// </summary>
public sealed class FlowCodePanelViewModel
{
    private FlowDocumentViewModel? _doc;

    public TextDocument Document { get; } = new();

    public FlowDocumentViewModel? ActiveFlow => _doc;

    /// <summary>Lines to select (DockCodeBox.SetSelectLines); handled by the view.</summary>
    public event EventHandler<(int Begin, int End)>? SelectLinesRequested;

    /// <summary>MainWindowViewModel.ActiveDocumentChanged: follow flow documents; other documents leave the text as is.</summary>
    public void SetActiveDocument(DocumentViewModel? doc)
    {
        if (doc is not FlowDocumentViewModel flow || ReferenceEquals(flow, _doc)) return;
        if (_doc != null)
        {
            _doc.ModelChanged -= OnModelChanged;
            _doc.CodeLinesSelected -= OnCodeLinesSelected;
        }
        _doc = flow;
        _doc.ModelChanged += OnModelChanged;
        _doc.CodeLinesSelected += OnCodeLinesSelected;
        Refresh();
    }

    /// <summary>Closing the document clears the window.</summary>
    public void Forget(DocumentViewModel doc)
    {
        if (!ReferenceEquals(doc, _doc)) return;
        _doc!.ModelChanged -= OnModelChanged;
        _doc.CodeLinesSelected -= OnCodeLinesSelected;
        _doc = null;
        Document.Text = "";
    }

    private void OnModelChanged(object? sender, EventArgs e) => Refresh();

    private void OnCodeLinesSelected(object? sender, (int Begin, int End) lines) => SelectLinesRequested?.Invoke(this, lines);

    /// <summary>DockCodeBox.SetCodeText: replaced only when it differs.</summary>
    private void Refresh()
    {
        var text = _doc?.Code ?? "";
        if (Document.Text != text) Document.Text = text;
    }
}

/// <summary>Read-only C view of <see cref="FlowCodePanelViewModel"/>; double-click selects the icon of that line (FlowView.SelectLine).</summary>
public sealed class FlowCodePanelView : UserControl
{
    private readonly TextEditor _editor;
    private FlowCodePanelViewModel? _vm;

    public FlowCodePanelView()
    {
        _editor = new TextEditor
        {
            IsReadOnly = true,
            ShowLineNumbers = true,
            FontFamily = new FontFamily("Menlo, Consolas, DejaVu Sans Mono, PingFang SC, Microsoft YaHei, monospace"),
            FontSize = 12,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        StudioCodeTheme.Apply(_editor);
        _editor.TextArea.AddHandler(PointerPressedEvent, OnPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        Content = _editor;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) _vm.SelectLinesRequested -= OnSelectLines;
        _vm = DataContext as FlowCodePanelViewModel;
        if (_vm == null) return;
        _editor.Document = _vm.Document;
        _vm.SelectLinesRequested += OnSelectLines;
    }

    private void OnSelectLines(object? sender, (int Begin, int End) lines)
    {
        var doc = _editor.Document;
        if (doc.LineCount == 0 || lines.Begin < 1) return;
        var first = doc.GetLineByNumber(Math.Clamp(lines.Begin, 1, doc.LineCount));
        var last = doc.GetLineByNumber(Math.Clamp(lines.End, lines.Begin, doc.LineCount));
        _editor.Select(first.Offset, last.EndOffset - first.Offset);
        _editor.ScrollTo(first.LineNumber, 1);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 2 || _vm?.ActiveFlow is not { } flow) return;
        var pos = _editor.GetPositionFromPoint(e.GetPosition(_editor));
        if (pos != null) Avalonia.Threading.Dispatcher.UIThread.Post(() => flow.SelectLine(pos.Value.Line));
    }
}
