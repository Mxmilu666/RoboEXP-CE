using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using RoboExp.App.Localization;
using RoboExp.Core.Model;

namespace RoboExp.App.Variables;

/// <summary>
/// Original xuwenbin.SetNewVar: quick "define variable" from the property editor. Local/global, int (long) or double,
/// name prefilled var&lt;n&gt;/g_&lt;n&gt; from 1. "More..." opens the variable pane instead.
/// </summary>
public sealed class NewVariableWindow : Window
{
    private const string Form = "xuwenbin.SetNewVar:";
    private readonly IReadOnlyList<VarStruct> _local, _global;
    private readonly TextBox _name = new() { MinWidth = 160 };
    private readonly RadioButton _int, _double, _localRb, _globalRb;
    private readonly Func<string, bool>? _isSystemFunction;

    public enum Outcome { Cancel, Created, More }

    public Outcome Result { get; private set; } = Outcome.Cancel;
    public VarStruct? Variable { get; private set; }
    public bool IsGlobal => _globalRb.IsChecked == true;

    public NewVariableWindow(IReadOnlyList<VarStruct> local, IReadOnlyList<VarStruct> global, Func<string, bool>? isSystemFunction)
    {
        _local = local;
        _global = global;
        _isSystemFunction = isSystemFunction;
        var l = Localizer.Instance;
        Title = l.T(Form + "$this.Text");
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _int = new RadioButton { Content = l.T(Form + "radioButton1.Text"), GroupName = "t", IsChecked = true };
        _double = new RadioButton { Content = l.T(Form + "radioButton2.Text"), GroupName = "t" };
        _localRb = new RadioButton { Content = l.T(Form + "radioButton3.Text"), GroupName = "s", IsChecked = true };
        _globalRb = new RadioButton { Content = l.T(Form + "radioButton4.Text"), GroupName = "s" };
        _localRb.IsCheckedChanged += (_, _) => { if (_localRb.IsChecked == true) _name.Text = NextName("var"); };
        _globalRb.IsCheckedChanged += (_, _) => { if (_globalRb.IsChecked == true) _name.Text = NextName("g_"); };
        _name.Text = NextName("var");

        var ok = new Button { Content = l.T(Form + "button1.Text"), MinWidth = 70, IsDefault = true };
        var more = new Button { Content = l.T(Form + "button2.Text"), MinWidth = 70 };
        var cancel = new Button { Content = l.T(Form + "button3.Text"), MinWidth = 70, IsCancel = true };
        ok.Click += async (_, _) => await OnOk();
        more.Click += (_, _) => { Result = Outcome.More; Close(); };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 8,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _localRb, _globalRb } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = l.T(Form + "label1.Text"), VerticalAlignment = VerticalAlignment.Center }, _name } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _int, _double } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, more, cancel } },
            },
        };
        Opened += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    /// <summary>SetNewVar.LocalVar/GlobalVar: counter starts at 1 each time.</summary>
    private string NextName(string prefix)
    {
        var n = 1;
        return VariableRules.NextName(prefix, ref n, _local, _global);
    }

    private async Task OnOk()
    {
        var name = (_name.Text ?? "").Trim();
        // Original validated a global with the local rule set (incl. system function names).
        var err = VariableRules.CheckName(_local, -1, _global, name, _isSystemFunction);
        if (err != VariableRules.NameError.None)
        {
            await Views.MessageDialog.ShowAsync(this, Localizer.Instance.T(VariableRules.MessageKey(err)), Localizer.Instance.T("Xwb:RobotExpress"), Views.MessageDialog.Buttons.Ok);
            _name.Focus();
            return;
        }
        Variable = new VarStruct { Name = name, Type = _double.IsChecked == true ? "double" : "long", DefaultValue = "0", Description = "" };
        Result = Outcome.Created;
        Close();
    }
}
