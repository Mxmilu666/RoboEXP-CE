using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using RoboExp.App.Localization;
using RoboExp.App.Palette;
using RoboExp.App.Properties;
using RoboExp.App.Services;
using RoboExp.App.ViewModels;
using RoboExp.App.Views;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;
using AvPoint = Avalonia.Point;

namespace RoboExp.App.Flow;

/// <summary>
/// The FlowView tab: the design surface with its two scroll bars (30px per step, ranges from
/// DesignSurface.UpdateScrollBarsValue) and the right-click menu m_ContextMenuE2 (flow control icons, library bands,
/// the project's subroutines, then Undo/Redo/Cut/Copy/Paste/Delete/Select All shown only when applicable).
/// </summary>
public sealed class FlowDocumentView : UserControl
{
    private const int Step = 30;
    private readonly FlowCanvas _canvas = new();
    private readonly ScrollBar _h = new() { Orientation = Orientation.Horizontal, SmallChange = 1, LargeChange = 1, Minimum = 0 };
    private readonly ScrollBar _v = new() { Orientation = Orientation.Vertical, SmallChange = 1, LargeChange = 1, Minimum = 0 };
    private FlowDocumentViewModel? _vm;
    private bool _syncing;
    private bool _revealedStart;

    public FlowDocumentView()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("*,Auto"),
        };
        grid.Children.Add(_canvas);
        Grid.SetColumn(_v, 1);
        grid.Children.Add(_v);
        Grid.SetRow(_h, 1);
        grid.Children.Add(_h);
        Content = grid;

        _h.ValueChanged += (_, e) => OnScrollBar(e, horizontal: true);
        _v.ValueChanged += (_, e) => OnScrollBar(e, horizontal: false);
        _canvas.ViewChanged += (_, _) => UpdateScrollBars();
        _canvas.ContextMenuRequested += async (_, p) => await ShowContextMenuAsync(p);
        _canvas.EditRequested += async (_, go) => await EditBlockAsync(go);
        _canvas.ShowCodeRequested += (_, _) => Host?.ShowPanelCommand.Execute(PanelIds.Code);
        _canvas.TextRequested += async (_, p) => await AddTextAsync(p);
    }

    private MainWindowViewModel? Host => TopLevel.GetTopLevel(this)?.DataContext as MainWindowViewModel;

    public FlowCanvas Canvas => _canvas;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.MessageRequested -= OnMessageRequested;
            _vm.ClipboardProvider = null;
        }
        _vm = DataContext as FlowDocumentViewModel;
        _canvas.Document = _vm;
        if (_vm != null)
        {
            _vm.MessageRequested += OnMessageRequested;
            _vm.ClipboardProvider = () => TopLevel.GetTopLevel(this)?.Clipboard;
        }
        _revealedStart = false;
        UpdateScrollBars();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Host?.NotifyEditTargetChanged();
        _canvas.Focus();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // MainForm.Open: ScrollStartIconIntoView once the view has a size.
        if (_vm == null || _revealedStart || e.NewSize.Width <= 0) return;
        _revealedStart = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_vm?.Objects.FirstOrDefault(o => o.DrawObject is IconDraw { IconType: IconType.Start }) is { } start)
                _vm.Reveal(start);
        });
    }

    private void OnScrollBar(RangeBaseValueChangedEventArgs e, bool horizontal)
    {
        if (_syncing || _vm == null) return;
        var delta = (int)Math.Round(e.NewValue - e.OldValue) * Step;
        if (horizontal) _canvas.ScrollBy(delta, 0);
        else _canvas.ScrollBy(0, delta);
    }

    private void UpdateScrollBars()
    {
        if (_vm == null) return;
        var s = _canvas.ScrollBarState();
        _syncing = true;
        _h.Maximum = s.HMax;
        _h.Value = s.HValue;
        _h.ViewportSize = Math.Max(1, _canvas.Bounds.Width / Step);
        _v.Maximum = s.VMax;
        _v.Value = s.VValue;
        _v.ViewportSize = Math.Max(1, _canvas.Bounds.Height / Step);
        _syncing = false;
    }

    private async void OnMessageRequested(object? sender, string text)
    {
        if (TopLevel.GetTopLevel(this) is Window w) await MessageDialog.ShowAsync(w, text, "", MessageDialog.Buttons.Ok);
    }

    private async Task EditBlockAsync(GraphicObject go)
    {
        if (_vm == null || FlowServices.BlockEditor is not { } editor) return;
        await editor.EditAsync(_vm, go);
        _canvas.Focus();
    }

    /// <summary>Text tool: InputBox(CWRM "TextBox", CWRM "String") → TextDraw.</summary>
    private async Task AddTextAsync(RoboExp.Core.Model.Point p)
    {
        if (_vm == null || TopLevel.GetTopLevel(this) is not Window w) return;
        var loc = Localizer.Instance;
        var text = await FlowInputBox.ShowAsync(w, loc.T("TextBox"), loc.T("String"));
        if (!string.IsNullOrEmpty(text)) _vm.AddText(p, text);
        _canvas.Focus();
    }

    // ------------------------------------------------------------------ context menu (FlowView.InitContentMenuE2)

    private async Task ShowContextMenuAsync(AvPoint at)
    {
        if (_vm == null) return;
        var canPaste = await FlowClipboard.HasDataAsync(TopLevel.GetTopLevel(this)?.Clipboard);
        if (_vm == null) return;
        var loc = Localizer.Instance;
        var lang = _vm.Env.Language;
        var items = new List<object>();

        if (_vm.Selection.Count == 1 && BlockPropertiesViewModel.KindOf(_vm.Selection[0]) != BlockEditorKind.None)
        {
            var target = _vm.Selection[0];
            var properties = new MenuItem { Name = "BlockPropertiesMenuItem", Header = loc.T("PropertyFrm:$this.Text") };
            properties.Click += async (_, _) => await EditBlockAsync(target);
            items.Add(properties);
            items.Add(new Separator());
        }

        var flow = new MenuItem { Header = loc["FlowControl"] };
        foreach (var (type, caption, nameKey) in ControlIcons)
            flow.Items.Add(PlacementItem(caption, VendorImages.CtrlIcon(type), loc.T(nameKey), new PaletteDragItem(PaletteItemKind.Control, type)));
        items.Add(flow);

        var library = _vm.Session.Library;
        foreach (var band in library.Bands)
        {
            var bandItem = new MenuItem { Header = band.Caption.Get(lang) };
            if (band.IsShown)
            {
                foreach (var name in band.FunctionNames)
                {
                    if (library.Find(name) is not { } f) continue;
                    var caption = f.Caption.Get(lang);
                    bandItem.Items.Add(PlacementItem(caption.Length > 0 ? caption : f.Name, VendorImages.FromFile(f.IconPath),
                        PaletteViewModel.FunctionToolTip(f, lang), new PaletteDragItem(PaletteItemKind.Function, f.Name)));
                }
            }
            items.Add(bandItem);
        }

        var info = _vm.Session.Info;
        var sub = new MenuItem { Header = info.ProjectCaption ?? "", Icon = IconImage(FlowImages.Decode(info.ProjectIcon)) };
        foreach (var used in (IEnumerable<ProjectInfo>?)_vm.Session.Data.UsedProjectInfoList ?? [])
        {
            var name = used.ProjectName ?? "";
            if (library.IsSystemFunction(name)) continue;
            var tip = used.GetFuncDeclareCodeLine();
            if (tip.Length != 0 && !string.IsNullOrEmpty(used.ProjectDescription)) tip += "\n";
            tip += used.ProjectDescription;
            sub.Items.Add(PlacementItem(used.ProjectCaption ?? name, FlowImages.Decode(used.ProjectIcon), tip, new PaletteDragItem(PaletteItemKind.Function, name)));
        }
        items.Add(sub);
        items.Add(new Separator());

        var doc = _vm;
        void Cmd(string key, string image, bool visible, Action action)
        {
            if (!visible) return;
            var mi = new MenuItem { Header = loc[key], Icon = IconImage(RoboExp.App.Icons.FlatIcons.Command(image), 16) };
            mi.Click += (_, _) => action();
            items.Add(mi);
        }
        Cmd("Undo", "Undo", doc.Undo.CanUndo, doc.DoUndo);
        Cmd("Redo", "Redo", doc.Undo.CanRedo, doc.DoRedo);
        void ClipboardCmd(string key, bool visible, Func<Task> action)
        {
            if (!visible) return;
            var mi = new MenuItem { Header = loc[key], Icon = IconImage(RoboExp.App.Icons.FlatIcons.Command(key), 16) };
            mi.Click += async (_, _) => await action();
            items.Add(mi);
        }
        ClipboardCmd("Cut", doc.Selection.Count > 0, doc.CutAsync);
        ClipboardCmd("Copy", doc.Selection.Count > 0, doc.CopyAsync);
        ClipboardCmd("Paste", canPaste, doc.PasteAsync);
        Cmd("Delete", "Delete", doc.Selection.Count > 0, doc.Delete);
        items.Add(new Separator());
        Cmd("SelectAll", "SelectAll", doc.Objects.Count > 0, doc.SelectAll);

        var menu = new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer };
        menu.Open(_canvas);
    }

    /// <summary>Original order of PublicMethod.CreateCtrlIconList without the End icons (InitContentMenuE2).</summary>
    private static readonly (string Type, string Caption, string NameKey)[] ControlIcons =
    [
        ("BeginIf", "If", "IfMsg"), ("BeginWhile", "While", "WhileMsg"), ("BeginFor", "For", "ForMsg"),
        ("Calculate", "Calculate", "CalculateMsg"), ("Continue", "Continue", "ContinueMsg"), ("Break", "Break", "BreakMsg"),
        ("Return", "Return", "ReturnMsg"), ("CodeEditor", "CodeEditor", "CodeEditorMsg"), ("Start", "Start", "StartMsg"),
    ];

    private static Image? IconImage(Avalonia.Media.IImage? bmp, double size = 32) => bmp == null ? null : new Image { Source = bmp, Width = size, Height = size };

    /// <summary>FlowView.aaa: the chosen icon follows the mouse (as cursor) and the next left click places it.</summary>
    private MenuItem PlacementItem(string caption, Avalonia.Media.IImage? icon, string tip, PaletteDragItem item)
    {
        var mi = new MenuItem { Header = caption, Icon = IconImage(icon) };
        if (tip.Length > 0) ToolTip.SetTip(mi, tip);
        mi.Click += (_, _) =>
        {
            _canvas.PendingPlacement = item;
            _canvas.Cursor = new Cursor(StandardCursorType.Cross);
            _canvas.Focus();
        };
        return mi;
    }
}
