using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Interactivity;
using RoboExp.App.Localization;
using RoboExp.App.Services;
using RoboExp.Core.Model;

namespace RoboExp.App.Properties;

/// <summary>
/// The per-function panels of PropertyFrmNew (SetTypeVisible 4..18), laid out at the original designer coordinates.
/// Controls carry the original names (comboBox_Motorspeed, hScrollBar1, CB_LED, ...) and parameter tags; edits go through
/// <see cref="BlockPropertiesViewModel"/>'s staged setters (original setValueForNameStr / NoCheck).
/// </summary>
public sealed class SpecialPanelView : UserControl
{
    private const string Form = "PropertyFrmNew:";
    private readonly BlockPropertiesViewModel _vm;
    private readonly Canvas _root = new();
    private readonly string _fn;
    private readonly SoftwareType _type;
    private bool _updating;

    public SpecialPanelView(BlockPropertiesViewModel vm)
    {
        _vm = vm;
        _fn = vm.FunctionName;
        _type = vm.SoftwareType;
        FontSize = 12;
        DataContext = vm;
        var layout = new DockPanel();
        var help = new PropertyHelpView { Width = 260 };
        DockPanel.SetDock(help, Dock.Bottom);
        layout.Children.Add(help);
        layout.Children.Add(_root);
        Content = layout;
        Build(vm.SpecialPanel);
        WireHelp(_root);
    }

    public SpecialPanelKind Kind => _vm.SpecialPanel;

    /// <summary>Writes any staged text before the popup closes.</summary>
    public void CommitPending() => _vm.CommitSpecial();

    // ------------------------------------------------------------------ building blocks

    private static string T(string key) => Localizer.Instance.T(Form + key);

    private T Place<T>(T c, double x, double y, double w = double.NaN, double h = double.NaN, Panel? parent = null) where T : Control
    {
        Canvas.SetLeft(c, x);
        Canvas.SetTop(c, y);
        if (!double.IsNaN(w)) c.Width = w;
        if (!double.IsNaN(h)) c.Height = h;
        (parent ?? _root).Children.Add(c);
        return c;
    }

    private TextBlock Label(string name, string text, double x, double y, double w, double h, Panel? parent = null) =>
        Place(new TextBlock { Name = name, Text = text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(0, 3, 0, 0) },
            x, y, w, h, parent);

    private TextBlock ErrorLabel(string name, double x, double y, double w, double h, Panel? parent = null)
    {
        var l = Label(name, "", x, y, w, h, parent);
        l.Foreground = Brushes.Red;
        l.IsVisible = false;
        return l;
    }

    private void Separator(double y, double w = 260) => Place(new Rectangle { Height = 1, Fill = Brushes.Gray }, 0, y + 6, w);

    private ComboBox List(string name, double x, double y, double w, IEnumerable<string> items, string? selected, Action<string> onSelect)
    {
        var cb = Place(new ComboBox { Name = name, ItemsSource = items.ToList(), MinHeight = 22, Padding = new Thickness(4, 1), HorizontalContentAlignment = HorizontalAlignment.Left }, x, y, w);
        if (selected != null && cb.ItemsSource is IList<string> l && l.Contains(selected)) cb.SelectedItem = selected;
        cb.SelectionChanged += (_, _) =>
        {
            if (_updating || cb.SelectedItem is not string s) return;
            onSelect(s);
        };
        return cb;
    }

    private ComboBox Editable(string name, double x, double y, double w, IEnumerable<string> items, string text, Action<ComboBox, string> onText)
    {
        var cb = Place(new ComboBox { Name = name, IsEditable = true, ItemsSource = items.ToList(), MinHeight = 22, Padding = new Thickness(4, 1) }, x, y, w);
        _updating = true;
        cb.Text = text;
        _updating = false;
        cb.PropertyChanged += (_, e) =>
        {
            if (e.Property != ComboBox.TextProperty || _updating) return;
            onText(cb, cb.Text ?? "");
        };
        cb.LostFocus += (_, _) => _vm.CommitSpecial();
        cb.KeyDown += (_, e) => { if (e.Key == Key.Enter) _vm.CommitSpecial(); };
        return cb;
    }

    private void SetText(ComboBox cb, string text)
    {
        _updating = true;
        cb.Text = text;
        _updating = false;
    }

    /// <summary>Owner-drawn colour list (comboBox2_DrawItem): a swatch of Color.FromName(item) and the name.</summary>
    private ComboBox ColorList(string name, double x, double y, double w, IReadOnlyList<string> items, string tag)
    {
        var cb = List(name, x, y, w, items, _vm.GetValue(tag), v => _vm.SetNow(tag, v));
        cb.ItemTemplate = new FuncDataTemplate<string>((s, _) => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children =
            {
                new Border { Width = 12, Height = 12, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(s is { Length: > 0 } ? 1 : 0), Background = BrushOf(s) },
                new TextBlock { Text = s, VerticalAlignment = VerticalAlignment.Center },
            },
        });
        return cb;
    }

    private static IBrush BrushOf(string? name) => name switch
    {
        "BLACK" => Brushes.Black, "RED" => Brushes.Red, "GREEN" => Brushes.Green, "BLUE" => Brushes.Blue, "YELLOW" => Brushes.Yellow,
        "PURPLE" => Brushes.Purple, "CYAN" => Brushes.Cyan, "WHITE" => Brushes.White, _ => Brushes.Transparent,
    };

    private ScrollBar HScroll(string name, double x, double y, double w, int min, int max, int largeChange, Action<int> onScroll)
    {
        var sb = Place(new ScrollBar
        {
            Name = name, Orientation = Orientation.Horizontal, Minimum = min, Maximum = max, SmallChange = 1, LargeChange = largeChange,
            ViewportSize = Math.Max(1, largeChange), AllowAutoHide = false, Height = 18,
        }, x, y + 3, w);
        sb.Scroll += (_, e) =>
        {
            if (_updating) return;
            onScroll((int)Math.Round(e.NewValue));
            if (e.ScrollEventType != ScrollEventType.ThumbTrack) _vm.CommitSpecial();
        };
        sb.PointerCaptureLost += (_, _) => _vm.CommitSpecial();
        return sb;
    }

    private void SetScroll(ScrollBar sb, double value)
    {
        _updating = true;
        sb.Value = Math.Clamp(value, sb.Minimum, sb.Maximum);
        _updating = false;
    }

    /// <summary>"DefineVar" picked in an editable combo: SetNewVar, then the combo shows the new name (or its old text).</summary>
    private async Task<string?> DefineVariableAsync(ComboBox cb, string oldText)
    {
        var created = await _vm.DefineVariableAsync();
        SetText(cb, created ?? oldText);
        cb.ItemsSource = _vm.VariableItems.ToList();
        return created;
    }

    private static bool IsDefineVar(string s) => string.Equals(s, BlockPropertiesViewModel.DefineVarText, StringComparison.OrdinalIgnoreCase);

    /// <summary>Return-variable list (comboBox1_SelectedValueChanged_1): "DefineVar" opens SetNewVar.</summary>
    private ComboBox ReturnList(string name, double x, double y, double w)
    {
        ComboBox? cb = null;
        cb = List(name, x, y, w, _vm.VariableItems, _vm.ReturnName, async s =>
        {
            if (IsDefineVar(s))
            {
                var created = await _vm.DefineVariableAsync();
                _updating = true;
                cb!.ItemsSource = _vm.VariableItems.ToList();
                cb.SelectedItem = created ?? _vm.ReturnName;
                _updating = false;
                if (created == null) return;
                s = created;
            }
            _vm.StageReturn(s);
            _vm.CommitSpecial();
        });
        ToolTip.SetTip(cb, _vm.Callee?.ReturnType?.Type);
        return cb;
    }

    /// <summary>IQRobotPort: radio buttons per PortStyle; selecting one writes "which" (UserBox_SelectedIndexChanged).</summary>
    private Control PortPicker(string name, IqPortStyle style, double x, double y, double w, double h, Panel? parent = null)
    {
        var panel = new StackPanel { Name = name, Orientation = Orientation.Horizontal, Spacing = 6, Background = Brushes.Transparent };
        if (style != IqPortStyle.PortNone)
            panel.Children.Add(new TextBlock { Text = T("label15.Text"), VerticalAlignment = VerticalAlignment.Center, Width = 40 });
        var current = _vm.GetValue("which");
        foreach (var (caption, value) in SpecialPanels.PortChoices(style))
        {
            var rb = new RadioButton { Content = caption, GroupName = name + GetHashCode(), IsChecked = value == current, Tag = value, MinWidth = 0 };
            rb.IsCheckedChanged += (_, _) => { if (!_updating && rb.IsChecked == true) _vm.SetNow("which", value); };
            panel.Children.Add(rb);
        }
        return Place(panel, x, y, w, h, parent);
    }

    /// <summary>The port row of PnlDeF: comboBox_Def (label19) or, on IQ/MRCU/M2RCU, the iqRobotPort3 picker.</summary>
    private void PortRow()
    {
        if (SpecialPanels.PortStyle(_type, _fn, SpecialPanels.PortPicker.Def) is { } style)
        {
            PortPicker("iqRobotPort3", style, 16, 8, 215, 44);
            return;
        }
        Label("label19", T("label19.Text"), 3, 23, 56, 23);
        var cb = List("comboBox_Def", 59, 24, 104, SpecialPanels.Ports(_type, _fn), _vm.GetValue("which"), v => _vm.SetNow("which", v));
        ToolTip.SetTip(cb, DescriptionOf("which"));
    }

    private string? DescriptionOf(string tag) => _vm.Param(tag) is { } p ? _vm.ParameterDescription(p) : null;

    private void WireHelp(Panel panel)
    {
        foreach (var control in panel.Children)
        {
            if (control is Panel child) WireHelp(child);
            var tag = control.Name switch
            {
                "comboBox_Motorwhich" or "CB_LED" or "comboBox_Def" or "iqRobotPort1" or "iqRobotPort2" or "iqRobotPort3" or "comboBox4" => "which",
                "comboBox_Motorspeed" or "hScrollBar1" => "speed",
                "comboBox_Angle" or "hScrollBar2" => "angle",
                "comboBox_y" or "comboBox2" when Kind == SpecialPanelKind.LcdDot || control.Name == "comboBox_y" => "y",
                "comboBox1" when Kind == SpecialPanelKind.LcdDot => "x",
                "comboBox_data" or "comboBox6" => "data",
                "comboBox_FColor" => "Font Color",
                "comboBox_BGColor" => "background Color",
                "comboBox3" => "Color",
                "comboBox_LCDBack" => "FColor",
                "comboBox_Value" or "hScrollBar3" or "comboBox_addvar" => "value",
                "comboBox_s" or "hScrollBar4" => "s",
                "comboBox_add" => "address",
                "radioButton8" or "radioButton9" => "state",
                _ when panel.Name == "panel_Get3CLed" => "Color",
                _ when panel.Name is "panel_GetRFModuleRemoteBit" or "panel_wifi" => Kind == SpecialPanelKind.Getter ? "num" : "button",
                _ => null,
            };
            var isReturn = control.Name is "comboBox_ValueShow" or "comboBox_DefReturn" or "comboBox5" or "comboBox_addretuen";
            if (tag == null && !isReturn && control.Name != "comboBox_ForData") continue;
            void Show()
            {
                if (tag != null) _vm.ShowParameterHelp(tag);
                else if (isReturn) _vm.ShowHelp(Localizer.Instance.T("Xwb:PropertyFrm_ReturnVariable"), _vm.ReturnDescription);
                else _vm.ShowHelp(Localizer.Instance.T("Xwb:PropertyFrm_Times"), Localizer.Instance.T("Xwb:PropertyFrm_LoopTimes"));
            }
            control.AddHandler(PointerPressedEvent, (_, _) => Show(), RoutingStrategies.Tunnel, handledEventsToo: true);
            control.GotFocus += (_, _) => Show();
            // Original designer labels are siblings of their value controls, not wrapping containers.
            foreach (var label in panel.Children.OfType<TextBlock>().Where(l =>
                Math.Abs(Canvas.GetTop(l) - Canvas.GetTop(control)) <= 5 && Canvas.GetLeft(l) < Canvas.GetLeft(control)))
                label.AddHandler(PointerPressedEvent, (_, _) => Show(), RoutingStrategies.Tunnel, handledEventsToo: true);
        }
    }

    /// <summary>
    /// comboBox_data_SelectedValueChanged: set with check; on error the original shows the message and drops the last
    /// typed character.
    /// </summary>
    private async void CheckedText(ComboBox cb, string tag, string text, Action<string>? after = null)
    {
        if (IsDefineVar(text))
        {
            var old = _vm.GetValue(tag);
            if (await DefineVariableAsync(cb, old) is { } created) { _vm.Stage(tag, created); _vm.CommitSpecial(); after?.Invoke(created); }
            return;
        }
        if (_vm.Validate(tag, text) is { } error)
        {
            SetText(cb, text.Length > 0 ? text[..^1] : text);
            await _vm.ShowWarningAsync(error);
            return;
        }
        _vm.Stage(tag, text);
        after?.Invoke(text);
    }

    /// <summary>comboBox_TextChangedNoCheck: at most 30 characters (label12 warns), quoted for string functions.</summary>
    private void FreeText(ComboBox cb, string tag, string text, TextBlock? warn)
    {
        if (IsDefineVar(text))
        {
            _ = DefineVariableAsync(cb, Unquote(_vm.GetValue(tag))).ContinueWith(t =>
            {
                if (t.Result is { } v) { _vm.Stage(tag, v); _vm.CommitSpecial(); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }
        if (text.Length > SpecialPanels.MaxTextLength)
        {
            SetText(cb, text[..SpecialPanels.MaxTextLength]);
            if (warn != null) warn.IsVisible = true;
            return;
        }
        if (warn != null) warn.IsVisible = false;
        _vm.Stage(tag, SpecialPanels.QuotesText(_fn) ? "\"" + text + "\"" : text);
    }

    private static string Unquote(string s) => s.Replace("\"", "");

    // ------------------------------------------------------------------ panels

    private void Build(SpecialPanelKind kind)
    {
        switch (kind)
        {
            case SpecialPanelKind.Motor: BuildMotor(); break;
            case SpecialPanelKind.Led: BuildLed(); break;
            case SpecialPanelKind.Display: BuildDisplay(); break;
            case SpecialPanelKind.PortGetter: BuildPortGetter(); break;
            case SpecialPanelKind.Getter: BuildGetter(); break;
            case SpecialPanelKind.WaitLight: BuildValue(withPort: true); break;
            case SpecialPanelKind.Threshold: BuildValue(withPort: false); break;
            case SpecialPanelKind.WaitTouch: PortRow(); Place(new Rectangle { Height = 1, Fill = Brushes.Gray }, 0, 54, 260); break;
            case SpecialPanelKind.OnOff: BuildOnOff(); break;
            case SpecialPanelKind.WaitTime: BuildWaitTime(); break;
            case SpecialPanelKind.LcdDot: BuildLcdDot(); break;
            case SpecialPanelKind.ChoiceBox: BuildChoiceBox(); break;
            case SpecialPanelKind.Data: BuildData(); break;
            case SpecialPanelKind.LcdClear: BuildLcdClear(); break;
            case SpecialPanelKind.For: BuildFor(); break;
        }
        var maxBottom = _root.Children.Select(c => Canvas.GetTop(c) + (double.IsNaN(c.Height) ? 26 : c.Height)).DefaultIfEmpty(0).Max();
        _root.Width = 240;
        _root.Height = Math.Max(120, maxBottom + 8);
    }

    /// <summary>Case 4 PnlMotor: port, speed + "modify" scroll bar, optional angle + scroll bar.</summary>
    private void BuildMotor()
    {
        if (SpecialPanels.PortStyle(_type, _fn, SpecialPanels.PortPicker.Motor) is { } style)
            PortPicker("iqRobotPort2", style, 0, 4, 215, 38);
        else
        {
            Label("label15", T("label15.Text"), 0, 8, 48, 23);
            var which = List("comboBox_Motorwhich", 48, 8, 120, SpecialPanels.MotorPorts(_type, _fn), _vm.GetValue("which"), v => _vm.SetNow("which", v));
            ToolTip.SetTip(which, DescriptionOf("which"));
        }
        Separator(43);

        if (SpecialPanels.ShowsSpeed(_type, _fn))
        {
            var (min, max) = SpecialPanels.SpeedRange(_vm.Callee);
            Label("label3", T("label3.Text"), 0, 74, 48, 23);
            Label("label24", T("label24.Text"), 0, 106, 48, 23);
            var err = ErrorLabel("label1", 48, 106, 160, 40);
            ScrollBar? bar = null;
            var speed = Editable("comboBox_Motorspeed", 48, 74, 120, _vm.VariableItems, _vm.GetValue("speed"), (cb, text) =>
                ScrollText(cb, "speed", text, bar!, err, emptyValue: 0));
            ToolTip.SetTip(speed, DescriptionOf("speed"));
            bar = HScroll("hScrollBar1", 48, 106, 120, min, max, 10, v => { SetText(speed, v.ToString()); _vm.Stage("speed", v.ToString()); });
            InitScroll(bar, _vm.GetValue("speed"), err);
        }

        if (SpecialPanels.ShowsAngle(_fn))
        {
            var (min, max) = SpecialPanels.AngleRange(_vm.Callee, _type, _fn);
            Label("label17", T("label17.Text"), 0, 146, 48, 23);
            Label("label32", T("label32.Text"), 0, 178, 48, 23);
            var err = ErrorLabel("label18", 48, 178, 160, 40);
            ScrollBar? bar = null;
            var angle = Editable("comboBox_Angle", 48, 146, 120, _vm.VariableItems, _vm.GetValue("angle"), (cb, text) =>
                ScrollText(cb, "angle", text, bar!, err, emptyValue: (int)bar!.Minimum));
            ToolTip.SetTip(angle, DescriptionOf("angle"));
            bar = HScroll("hScrollBar2", 48, 178, 120, min, max, 10, v => { SetText(angle, v.ToString()); _vm.Stage("angle", v.ToString()); });
            InitScroll(bar, _vm.GetValue("angle"), err);
        }
    }

    private void InitScroll(ScrollBar bar, string value, TextBlock err)
    {
        if (int.TryParse(value, out var v) && v >= bar.Minimum && v <= bar.Maximum) SetScroll(bar, v);
        else if (value.Length > 0) bar.IsVisible = false; // a variable: the original hides the scroll bar
        err.IsVisible = false;
    }

    /// <summary>
    /// comboBox1_SelectedValueChanged / comboBox_Angle_SelectedValueChanged: empty or "-" writes the default, an in-range
    /// number moves the scroll bar, anything else must be a variable (else the last character is removed and the error
    /// shown in place of the scroll bar).
    /// </summary>
    private async void ScrollText(ComboBox cb, string tag, string text, ScrollBar bar, TextBlock err, int emptyValue)
    {
        if (text.Length == 0 || text == "-")
        {
            SetScroll(bar, emptyValue);
            _vm.Stage(tag, emptyValue.ToString());
            bar.IsVisible = true;
            err.IsVisible = false;
            return;
        }
        if (IsDefineVar(text))
        {
            if (await DefineVariableAsync(cb, _vm.GetValue(tag)) is { } created) { _vm.Stage(tag, created); _vm.CommitSpecial(); bar.IsVisible = false; }
            return;
        }
        if (SpecialPanels.IsInRange(text, (int)bar.Minimum, (int)bar.Maximum))
        {
            err.IsVisible = false;
            bar.IsVisible = true;
            if (_vm.Validate(tag, text) is { } e1) { err.Text = e1; err.IsVisible = true; bar.IsVisible = false; return; }
            _vm.Stage(tag, text);
            SetScroll(bar, int.Parse(text));
            return;
        }
        err.IsVisible = false;
        if (_vm.Validate(tag, text) is { } error)
        {
            SetText(cb, text[..^1]);
            err.Text = error;
            err.IsVisible = true;
        }
        else _vm.Stage(tag, text);
        bar.IsVisible = false;
    }

    /// <summary>Case 5 Pnl_Led: port list, colour radios with swatches, function description.</summary>
    private void BuildLed()
    {
        if (SpecialPanels.PortStyle(_type, _fn, SpecialPanels.PortPicker.Led) is { } style)
            PortPicker("iqRobotPort1", style, 4, 3, 215, 37);
        else
        {
            Label("label23", T("label23.Text"), 16, 8, 42, 16);
            var items = SpecialPanels.LedPorts(_type);
            var cb = Place(new ComboBox { Name = "CB_LED", ItemsSource = items, MinHeight = 22, Padding = new Thickness(4, 1) }, 72, 8, 88);
            var idx = SpecialPanels.LedPortIndex(_vm.GetValue("which"));
            if (idx >= 0 && idx < items.Count) cb.SelectedIndex = idx;
            cb.SelectionChanged += (_, _) => { if (!_updating && cb.SelectedItem is string s) _vm.SetNow("which", s); };
            ToolTip.SetTip(cb, DescriptionOf("which"));
        }
        Separator(40);
        var group = new Canvas { Name = "panel_Get3CLed", Width = 200, Height = 202 };
        Place(group, 0, 52);
        var current = SpecialPanels.LedColorOf(_vm.GetValue("Color"));
        string[] radioNames = ["radioButton1", "radioButton2", "radioButton3", "radioButton4", "radioButton5", "radioButton6", "radioButton7", "radioButton10"];
        IBrush[] swatches = [Brushes.Black, Brushes.Red, Brushes.Green, Brushes.Blue, Brushes.Yellow, Brushes.Purple, Brushes.Cyan, Brushes.White];
        for (var i = 0; i < SpecialPanels.LedColors.Count; i++)
        {
            var color = SpecialPanels.LedColors[i];
            var y = 9 + i * 24;
            var rb = Place(new RadioButton { Name = radioNames[i], Content = T(radioNames[i] + ".Text"), GroupName = "led" + GetHashCode(), IsChecked = color == current, Tag = color, MinHeight = 0, Padding = new Thickness(2, 0) },
                15, y - 4, 70, 24, group);
            rb.IsCheckedChanged += (_, _) => { if (!_updating && rb.IsChecked == true) _vm.SetNow("Color", color); };
            var swatch = Place(new Border { Background = swatches[i], BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Cursor = new Cursor(StandardCursorType.Hand) }, 90, y, 80, 16, group);
            swatch.PointerPressed += (_, _) => rb.IsChecked = true;
        }
        if (!string.IsNullOrEmpty(_fn))
            Label("label38", _vm.FunctionDescription, 0, 258, 220, 80);
    }

    /// <summary>Case 6 PnlLCD8: line, data (variable list or free text), colours / return variable.</summary>
    private void BuildDisplay()
    {
        var rows = SpecialPanels.DisplayRows(_type, _fn);
        if (rows.Line)
        {
            Label("label13", T("label13.Text"), 0, 8, 56, 24);
            var y = List("comboBox_y", 64, 8, 104, SpecialPanels.DisplayLines(_type), _vm.GetValue("y"), async v =>
            {
                if (_vm.Validate("y", v) is { } e) { await _vm.ShowWarningAsync(e); return; }
                _vm.SetNow("y", v);
            });
            ToolTip.SetTip(y, DescriptionOf("y"));
        }
        if (rows.DataList || rows.DataText) Label("label14", T("label14.Text"), 0, 48, 56, 24);
        if (rows.DataList)
            List("comboBox_data", 64, 48, 104, _vm.VariableItems, _vm.GetValue("data"), async v =>
            {
                if (IsDefineVar(v))
                {
                    var created = await _vm.DefineVariableAsync();
                    if (created != null) _vm.SetNow("data", created);
                    return;
                }
                _vm.SetNow("data", v);
            });
        TextBlock? warn = null;
        if (rows.DataText)
        {
            // comboBox1_DropDown: SetDisplayString / GetWifiVoice offer no list; SetDisplayFloat lists variables.
            var items = _fn is "SetDisplayString" or "GetWifiVoice" ? [] : _vm.VariableItems;
            Editable("comboBox6", 64, 48, 104, items, Unquote(_vm.GetValue("data")), (cb, text) => FreeText(cb, "data", text, warn));
        }
        if (rows.ReturnVar)
        {
            Label("label44", T("label44.Text"), 1, 88, 56, 24);
            var rv = ReturnList("comboBox_ValueShow", 64, 88, 104);
            ToolTip.SetTip(rv, _vm.ReturnDescription);
        }
        if (rows.Colors)
        {
            Label("label10", T("label10.Text"), 0, 88, 56, 24);
            ColorList("comboBox_FColor", 64, 88, 104, SpecialPanels.DisplayColors, "Font Color");
            Label("label11", T("label11.Text"), 0, 128, 72, 24);
            ColorList("comboBox_BGColor", 64, 128, 104, SpecialPanels.DisplayColors, "background Color");
        }
        warn = ErrorLabel("label12", 72, 168, 96, 64);
        warn.Text = Localizer.Instance.T("Port:Prop.MaxText"); // the original label12 is a red marker without text
    }

    /// <summary>Case 7 PnlDeF: port + return variable (+ GetRFModuleRemoteBit remote picture).</summary>
    private void BuildPortGetter()
    {
        var rf = _fn == "GetRFModuleRemoteBit";
        if (!(rf && _type is SoftwareType.MRCU or SoftwareType.M2RCU)) PortRow();
        Place(new Rectangle { Height = 1, Fill = Brushes.Gray }, 0, 54, 260);
        Label("label20", T("label20.Text"), 0, 77, 56, 23);
        ReturnList("comboBox_DefReturn", 56, 77, 104);
        if (rf) RemotePanel("panel_GetRFModuleRemoteBit", 0, 107, "pictureBox3.Image", 4, 38, 196, 143, SpecialPanels.RfRemoteButtons, 0, "button", "num");
    }

    /// <summary>Case 13: return variable only (+ GetWifiRemote picture).</summary>
    private void BuildGetter()
    {
        Label("label20", T("label20.Text"), 0, 77, 56, 23);
        ReturnList("comboBox_DefReturn", 56, 77, 104);
        if (_fn == "GetWifiRemote") RemotePanel("panel_wifi", 1, 110, "pictureBox4.Image", 3, 0, 200, 100, SpecialPanels.WifiRemoteButtons, 0, "num", "button");
    }

    /// <summary>RadioButtonEx buttons over a remote picture; picking one writes "button" and "num" (radioButtonEx10_CheckedChanged).</summary>
    private void RemotePanel(string name, double x, double y, string image, double ix, double iy, double iw, double ih,
        IReadOnlyList<(string Value, int X, int Y)> buttons, int offset, string primary, string secondary)
    {
        var panel = new Canvas { Name = name, Width = 231, Height = 190 };
        Place(panel, x, y);
        if (FormResourceImages.Get("PropertyFrmNew", image) is { } bmp)
            Place(new Image { Source = bmp, Stretch = Stretch.Uniform }, ix, iy, iw, ih, panel);
        var current = _vm.GetValue(primary);
        var dots = new List<Ellipse>();
        foreach (var (value, bx, by) in buttons)
        {
            var dot = Place(new Ellipse
            {
                Name = name + "_" + value, Stroke = Brushes.DimGray, StrokeThickness = 1, Fill = value == current ? Brushes.Lime : Brushes.White,
                Cursor = new Cursor(StandardCursorType.Hand), Tag = value,
            }, bx, by + offset, 13, 13, panel);
            ToolTip.SetTip(dot, value);
            dots.Add(dot);
            dot.PointerPressed += (_, e) =>
            {
                foreach (var d in dots) d.Fill = d == dot ? Brushes.Lime : Brushes.White;
                _vm.Stage(primary, value);
                _vm.Stage(secondary, value);
                _vm.CommitSpecial();
                e.Handled = true;
            };
        }
    }

    /// <summary>Cases 8/12: (port) + threshold value text + "modify" scroll bar.</summary>
    private void BuildValue(bool withPort)
    {
        if (withPort) PortRow();
        Place(new Rectangle { Height = 1, Fill = Brushes.Gray }, 0, 54, 260);
        Label("label22", SpecialPanels.ThresholdCaption(_fn, Localizer.Instance.Language) ?? T("label22.Text"), 0, 77, 56, 23);
        Label("label33", T("label33.Text"), 0, 117, 56, 23);
        var (min, max) = SpecialPanels.ValueRange(_vm.Callee);
        ScrollBar? bar = null;
        var value = Editable("comboBox_Value", 56, 77, 104, _vm.VariableItems, _vm.GetValue("value"), (cb, text) =>
            CheckedText(cb, "value", text, t => { if (int.TryParse(t, out var v)) SetScroll(bar!, v); }));
        ToolTip.SetTip(value, DescriptionOf("value"));
        bar = HScroll("hScrollBar3", 56, 117, 104, min, max, 100, v => { SetText(value, v.ToString()); _vm.Stage("value", v.ToString()); });
        if (int.TryParse(_vm.GetValue("value"), out var cur)) SetScroll(bar, cur);
    }

    /// <summary>Case 10: OFF / ON radios (state), port only for SetLightSensorLed.</summary>
    private void BuildOnOff()
    {
        if (_fn == "SetLightSensorLed") PortRow();
        var on = _vm.GetValue("state") is not ("OFF" or "0");
        var off = Place(new RadioButton { Name = "radioButton8", Content = T("radioButton8.Text"), GroupName = "state" + GetHashCode(), IsChecked = !on, Tag = "OFF" }, 59, 72, 64, 24);
        var onRb = Place(new RadioButton { Name = "radioButton9", Content = T("radioButton9.Text"), GroupName = "state" + GetHashCode(), IsChecked = on, Tag = "ON" }, 139, 72, 80, 24);
        off.IsCheckedChanged += (_, _) => { if (!_updating && off.IsChecked == true) _vm.SetNow("state", "OFF"); };
        onRb.IsCheckedChanged += (_, _) => { if (!_updating && onRb.IsChecked == true) _vm.SetNow("state", "ON"); };
    }

    /// <summary>Case 11 SetWaitForTime: seconds text + scroll bar in tenths (hScrollBar4 0..100 → "0.0").</summary>
    private void BuildWaitTime()
    {
        Label("label25", T("label25.Text"), 0, 77, 56, 23);
        Label("label33", T("label33.Text"), 0, 117, 56, 23);
        ScrollBar? bar = null;
        var s = Editable("comboBox_s", 56, 77, 104, _vm.VariableItems, _vm.GetValue("s"), (cb, text) =>
            CheckedText(cb, "s", text, t =>
            {
                if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) SetScroll(bar!, d * 10);
            }));
        ToolTip.SetTip(s, DescriptionOf("s"));
        bar = HScroll("hScrollBar4", 56, 117, 104, 0, 100, 5, v =>
        {
            var text = (v / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
            SetText(s, text);
            _vm.Stage("s", text);
        });
        SetScroll(bar, double.TryParse(_vm.GetValue("s"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cur) ? cur * 10 : 0);
    }

    /// <summary>Case 14 SetLCDDot: X, Y (free text, variables listed) and colour.</summary>
    private void BuildLcdDot()
    {
        Label("label28", T("label28.Text"), 0, 24, 40, 23);
        Editable("comboBox1", 40, 24, 121, _vm.VariableItems, _vm.GetValue("x"), (cb, t) => FreeText(cb, "x", t, null));
        Label("label30", T("label30.Text"), 0, 56, 40, 23);
        Editable("comboBox2", 40, 56, 121, _vm.VariableItems, _vm.GetValue("y"), (cb, t) => FreeText(cb, "y", t, null));
        Label("label31", T("label31.Text"), 0, 88, 40, 23);
        ColorList("comboBox3", 40, 88, 121, SpecialPanels.DotColors, "Color");
    }

    /// <summary>Case 15 GetChoiceBox: title text (stored quoted) and return variable with its description.</summary>
    private void BuildChoiceBox()
    {
        Label("label34", T("label34.Text"), 0, 16, 56, 16);
        Editable("comboBox4", 56, 16, 104, [], Unquote(_vm.GetValue("which")), (cb, t) => FreeText(cb, "which", t, null));
        Label("label35", T("label35.Text"), 0, 72, 56, 16);
        ReturnList("comboBox5", 56, 72, 104);
        Label("label36", _vm.ReturnDescription, 56, 112, 104, 104);
    }

    /// <summary>Case 16 SetData/GetData: address and data (SetData) or return variable (GetData).</summary>
    private void BuildData()
    {
        Label("label16", T("label16.Text"), 8, 16, 64, 24);
        Editable("comboBox_add", 64, 16, 104, _vm.VariableItems, _vm.GetValue("address"), (cb, t) => CheckedText(cb, "address", t));
        if (_fn == "SetData")
        {
            Label("label29", T("label29.Text"), 8, 64, 56, 24);
            Editable("comboBox_addvar", 64, 64, 104, _vm.VariableItems, _vm.GetValue("value"), (cb, t) => CheckedText(cb, "value", t));
        }
        else
        {
            Label("label39", T("label39.Text"), 8, 64, 56, 24);
            ReturnList("comboBox_addretuen", 64, 64, 104);
        }
    }

    /// <summary>Case 17 SetLCDClear: background colour.</summary>
    private void BuildLcdClear()
    {
        Label("label40", T("label40.Text"), 8, 16, 56, 24);
        ColorList("comboBox_LCDBack", 64, 16, 104, SpecialPanels.DisplayColors, "FColor");
    }

    /// <summary>Case 18 Pnl_ForData: loop count text (≤ 8 chars, numbers only) or a variable; "DefineVar" opens SetNewVar.</summary>
    private void BuildFor()
    {
        Label("label27", T("label27.Text"), 0, 16, 56, 20);
        Editable("comboBox_ForData", 64, 16, 121, _vm.VariableItems, _vm.LoopCountText, async (cb, text) =>
        {
            if (IsDefineVar(text))
            {
                if (await DefineVariableAsync(cb, _vm.LoopCountText) is { } created) { _vm.StageLoop(created); _vm.CommitSpecial(); }
                return;
            }
            if (text.Length > SpecialPanels.MaxLoopTextLength)
            {
                SetText(cb, text[..SpecialPanels.MaxLoopTextLength]);
                return;
            }
            // A picked variable is stored as is; typed text only when numeric (comboBox_ForData_TextChanged).
            if (cb.SelectedItem is string picked && picked == text && text.Length > 0) { _vm.StageLoop(text); _vm.CommitSpecial(); return; }
            if (SpecialPanels.LoopCountFor(text) is { } loop) _vm.StageLoop(loop);
        });
    }
}
