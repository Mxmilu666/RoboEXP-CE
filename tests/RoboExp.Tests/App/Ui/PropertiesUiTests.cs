using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using RoboExp.App.Flow;
using RoboExp.App.Palette;
using RoboExp.App.Properties;
using RoboExp.App.ViewModels;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;
using AvPoint = Avalonia.Point;
using Point = RoboExp.Core.Model.Point;

namespace RoboExp.Tests.App.Ui;

/// <summary>Real-input tests of the block property editors (popup PropertyFrmNew panels, docked PropertyFrm grid).</summary>
public class PropertiesUiTests
{
    internal const string ShotDir = "/tmp/roboexp-properties-shots/special";

    /// <summary>The newest window owned (transitively) by the main window.</summary>
    internal static Window TopWindow(UiHarness ui)
    {
        Window w = ui.Window;
        while (w.OwnedWindows.LastOrDefault() is { } owned) w = owned;
        return w;
    }

    internal static T Named<T>(Avalonia.Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"no {typeof(T).Name} '{name}' in {root}");

    internal static AvPoint Center(Window w, Control c) =>
        c.TranslatePoint(new AvPoint(c.Bounds.Width / 2, c.Bounds.Height / 2), w) ?? throw new InvalidOperationException("not in window");

    internal static void Click(UiHarness ui, Window w, Control c)
    {
        var p = Center(w, c);
        w.MouseMove(p, RawInputModifiers.None);
        w.MouseDown(p, MouseButton.Left, RawInputModifiers.None);
        w.MouseUp(p, MouseButton.Left, RawInputModifiers.None);
        ui.Pump();
    }

    internal static void Key(UiHarness ui, Window w, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        w.KeyPress(key, mods, PhysicalKey.None, null);
        ui.Pump();
        w.KeyRelease(key, mods, PhysicalKey.None, null);
        ui.Pump();
    }

    internal static void Type(UiHarness ui, Window w, string text)
    {
        foreach (var ch in text)
        {
            w.KeyTextInput(ch.ToString());
            ui.Pump();
        }
    }

    /// <summary>Selects all text of the focused text box and types <paramref name="text"/> over it.</summary>
    internal static void Retype(UiHarness ui, Window w, string text)
    {
        Key(ui, w, Avalonia.Input.Key.A, UiHarness.Cmd);
        Key(ui, w, Avalonia.Input.Key.Back);
        Type(ui, w, text);
    }

    internal static GraphicObject Drop(UiHarness ui, FlowDocumentViewModel doc, PaletteItemKind kind, string name, Point world)
    {
        ui.Reveal(world);
        ui.DropAt(ui.WindowPoint(world), PaletteDragData.Create(new PaletteDragItem(kind, name)));
        return doc.Objects.Last(o => kind == PaletteItemKind.Function ? o.DataObject is FuncData f && f.ProjectName == name : o.DrawObject is IconDraw i && i.IconType.ToString() == name);
    }

    /// <summary>Opens the block editor the way the canvas does (double-click on the icon) and returns its window.</summary>
    internal static async Task<Window> OpenEditor(UiHarness ui, GraphicObject go)
    {
        var center = new Point(go.DrawObject!.Position.X + 16, go.DrawObject.Position.Y + 16);
        ui.Reveal(center);
        await Task.Delay(600); // headless shares one mouse device across windows: keep earlier clicks out of the double-click
        ui.DoubleClick(ui.WindowPoint(center));
        await ui.SettleAsync();
        var w = TopWindow(ui);
        Assert.True(!ReferenceEquals(ui.Window, w), $"no editor opened for {go.DataObject?.ProjectName} (selection {(ui.Vm.ActiveDocument is FlowDocumentViewModel d ? d.Selection.Count : -1)})");
        return w;
    }

    internal static async Task CloseEditor(UiHarness ui, Window w)
    {
        Click(ui, w, Named<Button>(w, "BtnOk"));
        await ui.SettleAsync();
    }

    internal static void Shot(Window w, string name)
    {
        Directory.CreateDirectory(ShotDir);
        w.CaptureRenderedFrame()?.Save(Path.Combine(ShotDir, name + ".png"));
    }

    internal static string CallOf(FlowDocumentViewModel doc, string fn) =>
        doc.Objects.Select(o => o.DataObject).OfType<FuncData>().Last(f => f.ProjectName == fn).Code ?? "";

    // ------------------------------------------------------------------ motor panel

    [UiFact]
    public Task Motor_panel_typing_out_of_range_drops_the_char_and_closing_commits_one_undo_step() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var motor = Drop(ui, doc, PaletteItemKind.Function, "SetMotor", new Point(600, 400));
        var w = await OpenEditor(ui, motor);
        Assert.IsType<BlockEditorWindow>(w);

        var speed = Named<ComboBox>(w, "comboBox_Motorspeed");
        var bar = Named<ScrollBar>(w, "hScrollBar1");
        Click(ui, w, speed);
        Retype(ui, w, "150");
        // "150" > Max 100: the original removes the last typed character and shows the message in place of the scroll bar.
        Assert.Equal("15", speed.Text);
        Assert.Equal(15, bar.Value);
        Assert.Equal("SetMotor(_M1_, 0);", CallOf(doc, "SetMotor")); // nothing written while typing

        // Port list: keyboard selection on the closed list.
        var which = Named<ComboBox>(w, "comboBox_Motorwhich");
        Click(ui, w, which);
        Key(ui, w, Avalonia.Input.Key.Escape);
        which.Focus();
        Key(ui, w, Avalonia.Input.Key.Down);
        Key(ui, w, Avalonia.Input.Key.Down);
        ui.Pump();
        Shot(w, "motor-SetMotor-E6RCU");

        // Leaving the speed field committed "15"; the list selection applied at once.
        Assert.Equal("SetMotor(_M3_, 15);", CallOf(doc, "SetMotor"));
        await CloseEditor(ui, w);
        Assert.Equal("SetMotor(_M3_, 15);", CallOf(doc, "SetMotor"));
        // Every committed field / list step is one undo step: _M3_ → _M2_ → _M1_ → speed back to 0.
        ui.Key(Avalonia.Input.Key.Z, UiHarness.Cmd);
        Assert.Equal("SetMotor(_M2_, 15);", CallOf(doc, "SetMotor"));
        ui.Key(Avalonia.Input.Key.Z, UiHarness.Cmd);
        ui.Key(Avalonia.Input.Key.Z, UiHarness.Cmd);
        Assert.Equal("SetMotor(_M1_, 0);", CallOf(doc, "SetMotor"));
    });

    [UiFact]
    public Task Servo_panel_angle_scrollbar_writes_the_angle() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var servo = Drop(ui, doc, PaletteItemKind.Function, "SetMotorServo", new Point(600, 400));
        var w = await OpenEditor(ui, servo);
        var angle = Named<ComboBox>(w, "comboBox_Angle");
        Click(ui, w, angle);
        Retype(ui, w, "720");
        Key(ui, w, Avalonia.Input.Key.Enter);
        Assert.Equal(720, Named<ScrollBar>(w, "hScrollBar2").Value);
        Shot(w, "motor-SetMotorServo-E6RCU");
        await CloseEditor(ui, w);
        Assert.Equal("SetMotorServo(_M1_, 50, 720);", CallOf(doc, "SetMotorServo"));
    });

    // ------------------------------------------------------------------ LED / display / misc panels

    [UiFact]
    public Task Led_panel_colour_swatch_and_port_list() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var led = Drop(ui, doc, PaletteItemKind.Function, "Set3CLed", new Point(600, 400));
        var w = await OpenEditor(ui, led);
        Assert.Equal(true, Named<RadioButton>(w, "radioButton1").IsChecked); // Color "0" → OFF
        Click(ui, w, Named<RadioButton>(w, "radioButton5"));                   // YELLOW
        var port = Named<ComboBox>(w, "CB_LED");
        port.Focus();
        Key(ui, w, Avalonia.Input.Key.Down);
        Shot(w, "led-Set3CLed-E6RCU");
        await CloseEditor(ui, w);
        Assert.Equal("Set3CLed(_P2_, YELLOW);", CallOf(doc, "Set3CLed"));
    });

    [UiFact]
    public Task Display_string_is_quoted_and_capped_at_30_chars() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var disp = Drop(ui, doc, PaletteItemKind.Function, "SetDisplayString", new Point(600, 400));
        var w = await OpenEditor(ui, disp);
        var data = Named<ComboBox>(w, "comboBox6");
        Assert.Equal("Hello!", data.Text);
        Click(ui, w, data);
        Retype(ui, w, new string('x', 35));
        Assert.Equal(30, data.Text!.Length);
        Assert.True(Named<TextBlock>(w, "label12").IsVisible);
        Shot(w, "display-SetDisplayString-E6RCU");
        await CloseEditor(ui, w);
        Assert.Contains("\"" + new string('x', 30) + "\"", CallOf(doc, "SetDisplayString"));
    });

    [UiFact]
    public Task Wait_time_scrollbar_and_text_use_tenths() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var wait = Drop(ui, doc, PaletteItemKind.Function, "SetWaitForTime", new Point(600, 400));
        var w = await OpenEditor(ui, wait);
        var s = Named<ComboBox>(w, "comboBox_s");
        Click(ui, w, s);
        Retype(ui, w, "2.5");
        Assert.Equal(25, Named<ScrollBar>(w, "hScrollBar4").Value);
        Shot(w, "wait-SetWaitForTime-E6RCU");
        await CloseEditor(ui, w);
        Assert.Equal("SetWaitForTime(2.5);", CallOf(doc, "SetWaitForTime"));
    });

    [UiFact]
    public Task Getter_panel_return_variable_and_light_threshold() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        doc.Edit("var", () => doc.Session.Data.VarList!.Add(new VarStruct { Name = "light", Type = "long", DefaultValue = "0" }));
        var get = Drop(ui, doc, PaletteItemKind.Function, "GetLightSensor", new Point(600, 400));
        var w = await OpenEditor(ui, get);
        var ret = Named<ComboBox>(w, "comboBox_DefReturn");
        ret.SelectedItem = null;
        ret.Focus();
        Key(ui, w, Avalonia.Input.Key.Down); // "" → first variable
        Key(ui, w, Avalonia.Input.Key.Down);
        Shot(w, "getter-GetLightSensor-E6RCU");
        await CloseEditor(ui, w);
        Assert.StartsWith("light = GetLightSensor(", CallOf(doc, "GetLightSensor"));

        var dark = Drop(ui, doc, PaletteItemKind.Function, "SetWaitforDark", new Point(700, 400));
        w = await OpenEditor(ui, dark);
        Assert.Equal("Value", Named<TextBlock>(w, "label22").Text);
        var value = Named<ComboBox>(w, "comboBox_Value");
        Click(ui, w, value);
        Retype(ui, w, "l"); // int parameter: a non-number that is no variable → original error box, char removed
        await ui.SettleAsync();
        var box = TopWindow(ui);
        Assert.NotSame(w, box);
        box.Close();
        await ui.SettleAsync();
        Assert.Equal("", value.Text);
        for (var i = 0; i < 20 && value.SelectedItem as string != "light"; i++) Key(ui, w, Avalonia.Input.Key.Down);
        Shot(w, "threshold-SetWaitforDark-E6RCU");
        await CloseEditor(ui, w);
        Assert.Equal("SetWaitforDark(_P1_, light);", CallOf(doc, "SetWaitforDark"));
    });

    [UiFact]
    public Task For_panel_ignores_non_numeric_text_and_caps_at_8_chars() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var loop = Drop(ui, doc, PaletteItemKind.Control, "BeginFor", new Point(600, 400));
        var w = await OpenEditor(ui, loop);
        var box = Named<ComboBox>(w, "comboBox_ForData");
        Click(ui, w, box);
        Retype(ui, w, "123456789");
        Assert.Equal("12345678", box.Text);
        Shot(w, "for-BeginFor-E6RCU");
        await CloseEditor(ui, w);
        Assert.Equal("12345678", ((BeginForData)loop.DataObject!).LoopCountStr);
    });

    [UiFact]
    public Task Lcd_dot_and_clear_colour_lists() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var dot = Drop(ui, doc, PaletteItemKind.Function, "SetLCDDot", new Point(600, 400));
        var w = await OpenEditor(ui, dot);
        Click(ui, w, Named<ComboBox>(w, "comboBox1"));
        Retype(ui, w, "120");
        var color = Named<ComboBox>(w, "comboBox3");
        color.Focus();
        Key(ui, w, Avalonia.Input.Key.Down);
        Shot(w, "lcddot-SetLCDDot-E6RCU");
        await CloseEditor(ui, w);
        Assert.Equal("SetLCDDot(120, 1, PURPLE);", CallOf(doc, "SetLCDDot"));
    });

    [UiFact]
    public Task Right_click_Properties_shows_persistent_parameter_help_without_editing_values() => UiHarness.RunAsync(async ui =>
    {
        var path = Path.Combine(ui.WorkDir, "Help", "Help.rcu");
        RoboExp.Core.Project.ProjectSession.CreateNew(path, ProjectType.FlowApp, ui.Vm.Env.RobotType, ui.Install);
        var doc = Assert.IsType<FlowDocumentViewModel>(await ui.Vm.OpenPathAsync(path));
        await ui.SettleAsync();
        var motor = Drop(ui, doc, PaletteItemKind.Function, "SetMotor", new Point(300, 200));
        doc.ClearSelection();
        ui.Click(ui.WindowPoint(FlowCanvasUiTests.IconCenter(motor)), MouseButton.Right);
        await ui.SettleAsync();
        Assert.Equal([motor], doc.Selection);
        ui.Key(Avalonia.Input.Key.Down);
        ui.Key(Avalonia.Input.Key.Enter);
        await ui.SettleAsync();
        var view = ui.Window.GetVisualDescendants().OfType<BlockPropertiesView>().Single(v => v.IsEffectivelyVisible);
        var vm = Assert.IsType<BlockPropertiesViewModel>(view.DataContext);
        var before = CallOf(doc, "SetMotor");
        var values = ((FuncData)motor.DataObject!).ValueList!.ToArray();
        var boxes = view.GetVisualDescendants().OfType<ComboBox>().Where(b => b.IsEffectivelyVisible && b.DataContext is ParameterRowViewModel).ToList();
        var fixedBox = boxes.First(b => !b.IsEditable);
        var first = Assert.IsType<ParameterRowViewModel>(fixedBox.DataContext);
        var label = view.GetVisualDescendants().OfType<TextBlock>().First(t => ReferenceEquals(t.DataContext, first) && t.Text == first.Label);
        Click(ui, ui.Window, label);
        AssertHelp(view, first);
        Assert.True(fixedBox.Focus());
        ui.Pump();
        AssertHelp(view, first);
        var editable = boxes.Last(b => b.IsEditable);
        Click(ui, ui.Window, editable);
        var other = Assert.IsType<ParameterRowViewModel>(editable.DataContext);
        AssertHelp(view, other);
        Assert.NotEqual(first.Label, vm.HelpName);
        Assert.True(fixedBox.Focus());
        ui.Pump();
        AssertHelp(view, first);
        Assert.Equal(before, CallOf(doc, "SetMotor"));
        Assert.Equal(values, ((FuncData)motor.DataObject!).ValueList);
        ui.Screenshot("/tmp/rx-property-help/X4RCU-parameter-help.png");
    }, robotType: "X4RCU");

    [UiFact]
    public Task E6_popup_parameter_focus_shows_full_original_help_without_mutation() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var motor = Drop(ui, doc, PaletteItemKind.Function, "SetMotor", new Point(300, 200));
        doc.ClearSelection();
        ui.Click(ui.WindowPoint(FlowCanvasUiTests.IconCenter(motor)), MouseButton.Right);
        await ui.SettleAsync();
        var window = Assert.IsType<BlockEditorWindow>(TopWindow(ui));
        var panel = Assert.IsType<SpecialPanelView>(window.SpecialView);
        var vm = Assert.IsType<BlockPropertiesViewModel>(panel.DataContext);
        var before = CallOf(doc, "SetMotor");
        var values = ((FuncData)motor.DataObject!).ValueList!.ToArray();
        Click(ui, window, Named<TextBlock>(window, "label3"));
        AssertHelp(panel, vm.Parameters.Single(p => p.Parameter == vm.Param("speed")));
        var port = Named<ComboBox>(window, "comboBox_Motorwhich");
        Assert.True(port.Focus());
        ui.Pump();
        AssertHelp(panel, vm.Parameters.Single(p => p.Parameter == vm.Param("which")));
        Key(ui, window, Avalonia.Input.Key.Tab);
        AssertHelp(panel, vm.Parameters.Single(p => p.Parameter == vm.Param("speed")));
        Assert.Equal(before, CallOf(doc, "SetMotor"));
        Assert.Equal(values, ((FuncData)motor.DataObject!).ValueList);
        Directory.CreateDirectory("/tmp/rx-property-help");
        window.CaptureRenderedFrame()!.Save("/tmp/rx-property-help/E6RCU-popup-help.png");
        await CloseEditor(ui, window);
    });

    [UiFact]
    public Task E6_generic_popup_uses_library_help_when_project_metadata_is_empty() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var function = doc.Session.Library.Bands.SelectMany(b => b.FunctionNames).Select(doc.Session.Library.Find)
            .First(f => f != null && SpecialPanels.ForFunction(f.Name) == SpecialPanelKind.None
                && !string.IsNullOrWhiteSpace(f.Description.Get(ui.Vm.Env.Language))
                && f.Params.Any(p => !string.IsNullOrWhiteSpace(p.Description.Get(ui.Vm.Env.Language))))!;
        var block = Drop(ui, doc, PaletteItemKind.Function, function.Name, new Point(300, 200));
        var info = doc.Session.Data.GetUsedProjectInfo(function.Name)!;
        foreach (var parameter in info.ParamList!) parameter.Description = "";
        var window = await OpenEditor(ui, block);
        var view = window.GetVisualDescendants().OfType<BlockPropertiesView>().Single();
        var vm = Assert.IsType<BlockPropertiesViewModel>(view.DataContext);
        Assert.Equal(function.Description.Get(ui.Vm.Env.Language), vm.HelpDescription);
        var row = vm.Parameters.First(p => function.Params.Any(lp => lp.Name == p.Parameter.Name
            && !string.IsNullOrWhiteSpace(lp.Description.Get(ui.Vm.Env.Language))));
        var box = view.GetVisualDescendants().OfType<ComboBox>().First(b => b.IsEffectivelyVisible && ReferenceEquals(b.DataContext, row));
        var before = block.DataObject!.Code;
        Assert.True(box.Focus());
        ui.Pump();
        AssertHelp(view, row);
        var original = function.Params.Single(p => p.Name == row.Parameter.Name);
        Assert.Contains(original.Description.Get(ui.Vm.Env.Language), vm.HelpDescription);
        Assert.Equal(before, block.DataObject.Code);
        Directory.CreateDirectory("/tmp/rx-property-help");
        window.CaptureRenderedFrame()!.Save("/tmp/rx-property-help/E6RCU-generic-help.png");
        await CloseEditor(ui, window);
    });

    private static void AssertHelp(Control view, ParameterRowViewModel row)
    {
        Assert.Equal(row.Label, Named<SelectableTextBlock>(view, "PropertyHelpName").Text);
        var description = Named<SelectableTextBlock>(view, "PropertyHelpDescription");
        Assert.Equal(row.Description, description.Text);
        Assert.Contains(row.Parameter.Type!, description.Text!);
        Assert.Equal(Avalonia.Media.TextWrapping.Wrap, description.TextWrapping);
        Assert.True(description.IsEffectivelyVisible);
    }

    // ------------------------------------------------------------------ generic grid (docked, X4RCU)

    [UiFact]
    public Task Docked_grid_typing_commits_on_focus_loss_and_rejects_values_above_max() => UiHarness.RunAsync(async ui =>
    {
        ui.Vm.Env.SetRobotType(RoboExp.Core.Install.RobotType.Parse("X4RCU"));
        var dir = Path.Combine(ui.WorkDir, "X4");
        var path = Path.Combine(dir, "X4", "X4.rcu");
        RoboExp.Core.Project.ProjectSession.CreateNew(path, ProjectType.FlowApp, ui.Vm.Env.RobotType, ui.Install);
        var doc = Assert.IsType<FlowDocumentViewModel>(await ui.Vm.OpenPathAsync(path));
        await ui.SettleAsync();
        var motor = Drop(ui, doc, PaletteItemKind.Function, "SetMotor", new Point(300, 200));
        var center = new Point(motor.DrawObject!.Position.X + 16, motor.DrawObject.Position.Y + 16);
        ui.DoubleClick(ui.WindowPoint(center));
        await ui.SettleAsync();
        Assert.Same(ui.Window, TopWindow(ui)); // X4 edits in the dock
        var panel = (PropertiesPanelViewModel)ui.Vm.Panel(PanelIds.Properties).Content!;
        Assert.Same(motor, panel.Editor!.Target);

        var view = ui.Window.GetVisualDescendants().OfType<BlockPropertiesView>().Single(v => v.IsEffectivelyVisible);
        var boxes = view.GetVisualDescendants().OfType<ComboBox>().Where(b => b.IsEffectivelyVisible && b.IsEditable).ToList();
        var before = CallOf(doc, "SetMotor");
        var speed = boxes[1]; // which (fixed list), state, speed → editable rows are state and speed
        Click(ui, ui.Window, speed);
        Retype(ui, ui.Window, "200");
        Key(ui, ui.Window, Avalonia.Input.Key.Enter);
        await ui.SettleAsync(100);
        Assert.Equal([RoboExp.App.Localization.Localizer.Instance.T("Xwb:PropertyFrm_ErrMoreMax")], ui.Dialogs); // once, not again on focus loss
        Assert.Equal(before, CallOf(doc, "SetMotor"));

        Click(ui, ui.Window, speed);
        Retype(ui, ui.Window, "80");
        Assert.Equal("80", speed.Text);
        var row = Assert.IsType<ParameterRowViewModel>(speed.DataContext);
        Assert.Equal("80", row.Value);
        Click(ui, ui.Window, boxes[0]); // focus moves to another row: commit
        await ui.SettleAsync();
        Assert.Equal("SetMotor(, 1, 80);", CallOf(doc, "SetMotor"));
        ui.Screenshot(Path.Combine(ShotDir, "docked-grid-SetMotor-X4RCU.png"));
    }, robotType: "X4RCU");

    // ------------------------------------------------------------------ MRCU: radio port pickers and the RF remote picture

    [UiFact]
    public Task Mrcu_port_picker_radios_and_rf_remote_buttons() => UiHarness.RunAsync(async ui =>
    {
        var path = Path.Combine(ui.WorkDir, "M", "M.rcu");
        RoboExp.Core.Project.ProjectSession.CreateNew(path, ProjectType.FlowApp, ui.Vm.Env.RobotType, ui.Install);
        var doc = Assert.IsType<FlowDocumentViewModel>(await ui.Vm.OpenPathAsync(path));
        await ui.SettleAsync();

        var motor = Drop(ui, doc, PaletteItemKind.Function, "SetMotor", new Point(300, 200));
        var w = await OpenEditor(ui, motor);
        var picker = Named<StackPanel>(w, "iqRobotPort2");
        var m2 = picker.Children.OfType<RadioButton>().Single(r => r.Content as string == "M2");
        Click(ui, w, m2);
        Shot(w, "motor-SetMotor-MRCU");
        await CloseEditor(ui, w);
        Assert.Equal("SetMotor(_M2_, 0);", CallOf(doc, "SetMotor"));

        var rf = Drop(ui, doc, PaletteItemKind.Function, "GetRFModuleRemoteBit", new Point(400, 200));
        w = await OpenEditor(ui, rf);
        var remote = Named<Canvas>(w, "panel_GetRFModuleRemoteBit");
        Assert.Contains(remote.Children, c => c is Image { Source: not null }); // pictureBox3 from the original resources
        Click(ui, w, Named<Avalonia.Controls.Shapes.Ellipse>(w, "panel_GetRFModuleRemoteBit_7"));
        Shot(w, "remote-GetRFModuleRemoteBit-MRCU");
        await CloseEditor(ui, w);
        Assert.Equal("GetRFModuleRemoteBit(7);", CallOf(doc, "GetRFModuleRemoteBit"));
    }, robotType: "MRCU");

    // ------------------------------------------------------------------ expression editor

    [UiFact]
    public Task Expression_editor_operator_buttons_insert_at_caret_and_check_reports_errors() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var iff = Drop(ui, doc, PaletteItemKind.Control, "BeginIf", new Point(600, 400));
        var w = await OpenEditor(ui, iff);
        var text = Named<TextBox>(w, "TxtExpression");
        Click(ui, w, text);
        Type(ui, w, "a");
        var gt = w.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == ">");
        Click(ui, w, gt);
        Assert.Equal("a>", text.Text);
        Assert.True(text.IsFocused);
        Type(ui, w, "1");
        Key(ui, w, Avalonia.Input.Key.Enter);   // check
        await ui.SettleAsync();
        var ok = TopWindow(ui);
        Assert.NotSame(w, ok);                  // "Expression is true."
        ok.Close();
        await ui.SettleAsync();

        Click(ui, w, text);
        Key(ui, w, Avalonia.Input.Key.End);
        Click(ui, w, w.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "&&"));
        Key(ui, w, Avalonia.Input.Key.Enter);
        await ui.SettleAsync();
        var err = TopWindow(ui);
        Assert.Contains(err.GetVisualDescendants().OfType<SelectableTextBlock>(), t => (t.Text ?? "").Length > 0);
        err.Close();
        await ui.SettleAsync();
        Shot(w, "expression-BeginIf-E6RCU");
        await CloseEditor(ui, w);
        Assert.Equal("a>1&&", ((BeginIfData)iff.DataObject!).Expression); // advisory: stored anyway
    });
}
