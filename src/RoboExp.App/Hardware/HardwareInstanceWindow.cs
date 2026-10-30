using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using RoboExp.App.Localization;
using RoboExp.App.Views;
using RoboExp.Core.Hardware;
using RoboExp.Core.Model;

namespace RoboExp.App.Hardware;

/// <summary>
/// Port of HardwareInstFrm: pick the pins of a new hardware instance (or re-pick those of an existing one) on the
/// controller picture. Default-order types select a whole pin team of the board by hovering any of its pins;
/// other types assign single control pins to the slot selected in the pin list.
/// </summary>
internal sealed class HardwareInstanceWindow : Window
{
    private readonly PinAssignment _pins;
    private readonly HardwareTable _table;
    private readonly IEnumerable<VarStruct>? _variables;
    private readonly bool _isEdit;
    private readonly RcuPictureControl _picture = new();
    private readonly TextBox _nameText;
    private readonly ListBox _slots;
    private PinTeamDef? _candidate;
    private bool _updatingSlots;

    public HardwareInstanceWindow(RcuBoard board, Bitmap? picture, PinAssignment pins, HardwareTable table,
        IEnumerable<VarStruct>? variables, string nameText, bool isEdit)
    {
        _pins = pins;
        _table = table;
        _variables = variables;
        _isEdit = isEdit;
        var loc = Localizer.Instance;
        var type = pins.Type;

        Title = loc.T("HardwareInstFrm:$this.Text");
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _picture.SetBoard(board, picture);
        _picture.HoverPinChanged += OnHoverPin;
        _picture.PinClicked += OnPinClicked;

        _nameText = new TextBox { Text = nameText, IsReadOnly = isEdit, Width = 120, VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = loc.T("HardwareInstFrm:LabName.Text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) },
                new TextBox { Text = type.Name, IsReadOnly = true, Focusable = false, Width = 140, VerticalAlignment = VerticalAlignment.Center },
                _nameText,
                new TextBlock { Text = "_", VerticalAlignment = VerticalAlignment.Center },
            },
        };
        var description = new TextBox
        {
            Text = type.Description ?? "",
            IsReadOnly = true,
            Focusable = false,
            TextWrapping = TextWrapping.Wrap,
            Height = 60,
        };
        var defineGroup = Group(loc.T("HardwareInstFrm:GpbDefine.Text"), new StackPanel
        {
            Spacing = 6,
            Children =
            {
                nameRow,
                new TextBlock { Text = loc.T("HardwareInstFrm:label1.Text") },
                description,
            },
        });

        _slots = new ListBox { Height = 110 };
        _slots.SelectionChanged += (_, _) =>
        {
            if (_updatingSlots) return;
            _pins.SelectedSlot = Math.Max(0, _slots.SelectedIndex);
        };
        var pinsGroup = Group(loc.T("HardwareInstFrm:GpbPins.Text"), new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new CheckBox { Content = loc.T("HardwareInstFrm:ChkDefault.Text"), IsChecked = type.IsDefault, IsHitTestVisible = false, Focusable = false },
                _slots,
            },
        });

        var ok = new Button { Content = loc["HardwareInstFrm:BtnOk.Text"], IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += async (_, _) => await OnOkAsync();
        var cancel = new Button { Content = loc["HardwareInstFrm:BtnCancel.Text"], IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => Close();

        var right = new StackPanel
        {
            Width = 320,
            Spacing = 10,
            Children =
            {
                defineGroup,
                pinsGroup,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { ok, cancel } },
            },
        };

        Content = new DockPanel
        {
            Margin = new Thickness(12),
            Children =
            {
                new Border { [DockPanel.DockProperty] = Dock.Right, Margin = new Thickness(12, 0, 0, 0), Child = right },
                new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top, Child = _picture },
            },
        };

        RefreshSlots();
        _slots.SelectedIndex = 0;
        UpdateMarks();
    }

    /// <summary>The pins chosen when the dialog was closed with OK.</summary>
    public PinTeam? ResultPins { get; private set; }

    /// <summary>Full instance name (TypeName + text + "_") when OK was pressed.</summary>
    public string? ResultName { get; private set; }

    internal static HeaderedContentControl Group(string header, Control content) => new()
    {
        Header = header,
        Content = new Border
        {
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8),
            Child = content,
        },
    };

    private void OnHoverPin((PinKind Kind, int Index)? pin)
    {
        _candidate = pin is { } p ? _pins.CandidateAt(p.Kind, p.Index) : null;
        _picture.ShowHandCursor = _candidate != null;
        UpdateMarks();
    }

    private void OnPinClicked((PinKind Kind, int Index)? pin)
    {
        if (pin is { } p) _candidate = _pins.CandidateAt(p.Kind, p.Index);
        if (_candidate == null) return;
        _pins.Apply(_candidate);
        RefreshSlots();
        // The candidate may no longer be valid against the new selection (e.g. a second slot on the same pin).
        _candidate = pin is { } q ? _pins.CandidateAt(q.Kind, q.Index) : null;
        UpdateMarks();
    }

    private void RefreshSlots()
    {
        _updatingSlots = true;
        var selected = _pins.SelectedSlot;
        _slots.ItemsSource = _pins.SlotCaptions();
        _slots.SelectedIndex = selected;
        _updatingSlots = false;
    }

    /// <summary>PtbHardware_Paint order: used (white), selection (lime), limits (red), candidate (cyan).</summary>
    private void UpdateMarks()
    {
        var marks = new List<PinMark>();
        foreach (var kind in new[] { PinKind.Ctrl, PinKind.Gnd, PinKind.Vcc })
            foreach (var n in _pins.Used(kind)) marks.Add(new PinMark(kind, n, PinMarkStyle.Used));
        foreach (var kind in new[] { PinKind.Ctrl, PinKind.Gnd, PinKind.Vcc })
            foreach (var n in _pins.Selected(kind)) marks.Add(new PinMark(kind, n, PinMarkStyle.Selected));
        foreach (var kind in new[] { PinKind.Ctrl, PinKind.Gnd, PinKind.Vcc })
            foreach (var n in _pins.Limits(kind)) marks.Add(new PinMark(kind, n, PinMarkStyle.Limited));
        if (_candidate != null)
            foreach (var kind in new[] { PinKind.Ctrl, PinKind.Gnd, PinKind.Vcc })
                foreach (var n in _candidate.Get(kind)) marks.Add(new PinMark(kind, n, PinMarkStyle.Candidate));
        _picture.Marks = marks;
    }

    private async Task OnOkAsync()
    {
        var error = _pins.ValidatePins();
        var typeName = _pins.Type.Name ?? "";
        var text = _nameText.Text ?? "";
        if (error == null && !_isEdit) error = PinAssignment.ValidateInstanceName(typeName, text, _table, _variables);
        if (error != null)
        {
            await MessageDialog.ShowAsync(this, Localizer.Instance.T("Xwb:" + error), Title ?? "", MessageDialog.Buttons.Ok);
            return;
        }
        ResultPins = _pins.ToPinTeam();
        ResultName = PinAssignment.FullInstanceName(typeName, text);
        Close();
    }
}
