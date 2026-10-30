using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using RoboExp.App.Localization;
using RoboExp.App.Views;
using RoboExp.Core.Hardware;
using RoboExp.Core.Model;

namespace RoboExp.App.Hardware;

/// <summary>
/// Port of HardwareTypeFrm: define a new hardware type (name, nick name, control pin count 1-4, pin descriptions,
/// default pin order, description) or edit an existing one (name and pin count fixed). "Setting" shows the controller
/// picture where clicking a pin toggles it in the type's disabled Ctrl/GND/Vcc pin lists.
/// </summary>
internal sealed class HardwareTypeWindow : Window
{
    private readonly HardwareTable _table;
    private readonly HardwareType? _editing;
    private readonly RcuBoard? _board;
    private readonly TextBox _name;
    private readonly TextBox _nickName;
    private readonly NumericUpDown _pinCount;
    private readonly CheckBox _isDefault;
    private readonly TextBox _description;
    private readonly StackPanel _pinRows = new() { Spacing = 4 };
    private readonly List<TextBox> _pinTexts = [];
    private readonly TextBlock _disabledText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly RcuPictureControl _picture = new();
    private readonly Control _pictureGroup;
    private readonly List<int>[] _limits = [[], [], []];
    private (PinKind Kind, int Index)? _hover;

    /// <param name="table">Combined table, for the unique-name check of a new type.</param>
    /// <param name="editing">Type to edit (left untouched; the result is a modified copy), or null to add one.</param>
    public HardwareTypeWindow(HardwareTable table, HardwareType? editing, RcuBoard? board, Bitmap? picture)
    {
        _table = table;
        _editing = editing;
        _board = board;
        var loc = Localizer.Instance;
        Title = loc.T("HardwareTypeFrm:$this.Text");
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _name = new TextBox { Width = 160, IsReadOnly = editing != null };
        _nickName = new TextBox { Width = 160 };
        _pinCount = new NumericUpDown { Minimum = 1, Maximum = 4, Increment = 1, Value = 1, FormatString = "0", Width = 120, IsEnabled = editing == null };
        _isDefault = new CheckBox { Content = loc.T("HardwareTypeFrm:ChkDefault.Text"), IsChecked = true };
        _description = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 60 };

        if (editing != null)
        {
            _name.Text = (editing.Name ?? "").Replace("_", "");
            _nickName.Text = editing.NickName;
            _description.Text = editing.Description;
            _isDefault.IsChecked = editing.IsDefault;
            _pinCount.Value = editing.PinCount;
            // HardwareTypeFrm(HardwareType, RCUInfo): keep only limits that exist on the board.
            int Max(PinKind kind) => board?.Pins(kind).Count ?? int.MaxValue;
            _limits[(int)PinKind.Ctrl].AddRange((editing.CtrlLimitList ?? []).Where(n => n < Max(PinKind.Ctrl)));
            _limits[(int)PinKind.Gnd].AddRange((editing.GNDLimitList ?? []).Where(n => n < Max(PinKind.Gnd)));
            _limits[(int)PinKind.Vcc].AddRange((editing.VccLimitList ?? []).Where(n => n < Max(PinKind.Vcc)));
            foreach (var d in editing.PinDescriptionList ?? []) AddPinRow(d ?? "");
        }
        else
        {
            RebuildPinRows(1);
        }
        _pinCount.ValueChanged += (_, _) => RebuildPinRows((int)(_pinCount.Value ?? 1));

        Label LabelFor(string key) => new() { Content = loc.T(key), VerticalAlignment = VerticalAlignment.Center };
        var top = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            RowSpacing = 6,
            ColumnSpacing = 4,
        };
        void Put(Control c, int row, int col) { Grid.SetRow(c, row); Grid.SetColumn(c, col); top.Children.Add(c); }
        Put(LabelFor("HardwareTypeFrm:label2.Text"), 0, 0);
        Put(new TextBlock { Text = "_", VerticalAlignment = VerticalAlignment.Center }, 0, 1);
        Put(_name, 0, 2);
        Put(new TextBlock { Text = "_", VerticalAlignment = VerticalAlignment.Center }, 0, 3);
        Put(LabelFor("HardwareTypeFrm:label5.Text"), 1, 0);
        Put(_nickName, 1, 2);
        Put(LabelFor("HardwareTypeFrm:label1.Text"), 2, 0);
        Put(_pinCount, 2, 2);

        var pinsHeader = new TextBlock { Text = loc.T("HardwareTypeFrm:ClhPinDesc.Text"), FontWeight = FontWeight.SemiBold };

        _picture.SetBoard(board, picture);
        _picture.HoverPinChanged += pin =>
        {
            _hover = pin;
            _picture.ShowHandCursor = pin != null;
            UpdateMarks();
        };
        _picture.PinClicked += pin =>
        {
            if (pin is not { } p || _board == null) return;
            PinAssignment.ToggleLimit(_limits[(int)p.Kind], p.Kind, p.Index, _board);
            UpdateMarks();
            UpdateDisabledText();
        };
        _pictureGroup = HardwareInstanceWindow.Group(loc.T("HardwareTypeFrm:GbPins.Text"),
            new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, Child = _picture });
        _pictureGroup.IsVisible = false;

        var setting = new Button { Content = loc["HardwareTypeFrm:BtnSetting.Text"], MinWidth = 80, IsEnabled = board != null };
        setting.Click += (_, _) => _pictureGroup.IsVisible = !_pictureGroup.IsVisible;
        var ok = new Button { Content = loc["HardwareTypeFrm:BtnOk.Text"], IsDefault = true, MinWidth = 80 };
        ok.Click += async (_, _) => await OnOkAsync();
        var cancel = new Button { Content = loc["HardwareTypeFrm:BtnCancel.Text"], IsCancel = true, MinWidth = 80 };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 10,
            MinWidth = 420,
            Children =
            {
                top,
                _isDefault,
                pinsHeader,
                _pinRows,
                HardwareInstanceWindow.Group(loc.T("HardwareTypeFrm:GbDesc.Text"), _description),
                _disabledText,
                _pictureGroup,
                new DockPanel
                {
                    Children =
                    {
                        new StackPanel { [DockPanel.DockProperty] = Dock.Right, Orientation = Orientation.Horizontal, Spacing = 8, Children = { ok, cancel } },
                        new Border { HorizontalAlignment = HorizontalAlignment.Left, Child = setting },
                    },
                },
            },
        };
        UpdateMarks();
        UpdateDisabledText();
    }

    /// <summary>The new type (add) or an edited copy of the type (edit) when closed with OK.</summary>
    public HardwareType? Result { get; private set; }

    /// <summary>NudPinCount_ValueChanged: one "Pin_i" description row per control pin.</summary>
    private void RebuildPinRows(int count)
    {
        _pinRows.Children.Clear();
        _pinTexts.Clear();
        for (var i = 0; i < count; i++) AddPinRow("Pin_" + i);
    }

    private void AddPinRow(string text)
    {
        var box = new TextBox { Text = text };
        _pinTexts.Add(box);
        _pinRows.Children.Add(box);
    }

    private void UpdateMarks()
    {
        var marks = new List<PinMark>();
        foreach (var kind in new[] { PinKind.Ctrl, PinKind.Gnd, PinKind.Vcc })
            foreach (var n in _limits[(int)kind]) marks.Add(new PinMark(kind, n, PinMarkStyle.Limited));
        if (_hover is { } h) marks.Add(new PinMark(h.Kind, h.Index, PinMarkStyle.Hover));
        _picture.Marks = marks;
    }

    /// <summary>SetText: "Disenabled control pins: A1S,A1D  Disenabled GND pins: ..." or "No disenabled pins.".</summary>
    private void UpdateDisabledText()
    {
        var loc = Localizer.Instance;
        var parts = new List<string>();
        void Add(PinKind kind, string key)
        {
            var list = _limits[(int)kind];
            if (list.Count == 0 || _board == null) return;
            var pins = _board.Pins(kind);
            parts.Add(loc.T(key) + string.Join(",", list.Where(n => n >= 0 && n < pins.Count).Select(n => pins[n].Name)));
        }
        Add(PinKind.Ctrl, "Xwb:HardwareTypeFrm_DisCtrlPins");
        Add(PinKind.Gnd, "Xwb:HardwareTypeFrm_DisGNDPins");
        Add(PinKind.Vcc, "Xwb:HardwareTypeFrm_DisVccPins");
        _disabledText.Text = parts.Count == 0 ? loc.T("Xwb:HardwareTypeFrm_NoDisPins") : string.Join("  ", parts);
    }

    private async Task OnOkAsync()
    {
        HardwareType result;
        if (_editing == null)
        {
            var text = _name.Text ?? "";
            if (PinAssignment.ValidateTypeName(text, _table) is { } error)
            {
                await MessageDialog.ShowAsync(this, Localizer.Instance.T("Xwb:" + error), Title ?? "", MessageDialog.Buttons.Ok);
                return;
            }
            result = new HardwareType
            {
                Name = "_" + text + "_",
                NickName = (_nickName.Text ?? "").Trim(),
                PinCount = (int)(_pinCount.Value ?? 1),
            };
        }
        else
        {
            result = HardwareTables.Clone(_editing);
            result.NickName = _nickName.Text ?? "";
        }
        result.Description = _description.Text ?? "";
        result.IsDefault = _isDefault.IsChecked == true;
        result.PinDescriptionList = new ArrayList<string?>(_pinTexts.Select(t => (string?)(t.Text ?? "")));
        result.CtrlLimitList = new ArrayList<int>(_limits[(int)PinKind.Ctrl]);
        result.GNDLimitList = new ArrayList<int>(_limits[(int)PinKind.Gnd]);
        result.VccLimitList = new ArrayList<int>(_limits[(int)PinKind.Vcc]);
        Result = result;
        Close();
    }
}
