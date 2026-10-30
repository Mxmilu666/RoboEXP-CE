using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using RoboExp.App.Flow;
using RoboExp.App.Localization;
using RoboExp.App.NewProject;
using RoboExp.App.Palette;
using RoboExp.App.Variables;
using RoboExp.App.ViewModels;
using RoboExp.Core.Build;
using RoboExp.Core.Library;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;
using RoboExp.Core.Project;
using RoboExp.Core.Rcu;
using static RoboExp.Tests.App.Ui.PropertiesUiTests;
using Point = RoboExp.Core.Model.Point;

namespace RoboExp.Tests.App.Ui;

/// <summary>Real-input tests of the variables pane, File → New, user library palette and the subroutine dialogs.</summary>
public class ProjectDialogsUiTests
{
    private static Button ButtonByText(Window w, string key) =>
        w.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == Localizer.Instance[key] || b.Content as string == Localizer.Instance.T(key))
        ?? throw new Xunit.Sdk.XunitException($"no button '{Localizer.Instance.T(key)}' in {w.Title}");

    private static async Task<Window> WaitForDialog(UiHarness ui, Window below)
    {
        for (var i = 0; i < 20; i++)
        {
            await ui.SettleAsync();
            var top = TopWindow(ui);
            if (!ReferenceEquals(top, below)) return top;
        }
        throw new Xunit.Sdk.XunitException("no dialog opened");
    }

    [UiFact]
    public Task Variables_pane_add_rename_and_rejected_name() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        ui.Vm.ShowPanelCommand.Execute(PanelIds.Variables);
        await ui.SettleAsync();
        var view = ui.Window.GetVisualDescendants().OfType<VariablesView>().Single(v => v.IsEffectivelyVisible);
        var before = doc.Session.Data.VarList!.Count;
        Click(ui, ui.Window, Named<Button>(view, "btnAddLocalVar"));
        await ui.SettleAsync();
        Assert.Equal(before + 1, doc.Session.Data.VarList!.Count);
        var added = doc.Session.Data.VarList![^1];
        Assert.StartsWith("var", added.Name);
        Assert.Equal("long", added.Type);

        var nameBox = view.GetVisualDescendants().OfType<TextBox>().Last(t => t.Name == "TxtName" && t.IsEffectivelyVisible);
        Click(ui, ui.Window, nameBox);
        Retype(ui, ui.Window, "speedL");
        Key(ui, ui.Window, Avalonia.Input.Key.Enter);
        await ui.SettleAsync();
        Assert.Equal("speedL", doc.Session.Data.VarList![^1].Name);
        Assert.Contains("long speedL = 0;", doc.Code);

        nameBox = view.GetVisualDescendants().OfType<TextBox>().Last(t => t.Name == "TxtName" && t.IsEffectivelyVisible);
        Click(ui, ui.Window, nameBox);
        Retype(ui, ui.Window, "while");
        Key(ui, ui.Window, Avalonia.Input.Key.Enter);
        await ui.SettleAsync(100);
        // Exactly one error box (Enter commits; the box taking focus must not commit and warn again).
        Assert.Equal([Localizer.Instance.T("Xwb:VariableFrm_ErrIsKeyWord")], ui.Dialogs);
        Assert.Equal("speedL", doc.Session.Data.VarList![^1].Name);
        Assert.Equal("speedL", nameBox.Text); // reverted in the grid
        ui.Screenshot(Path.Combine(ShotDir, "variables-pane-E6RCU.png"));
    });

    [UiFact]
    public Task File_new_dialog_creates_and_opens_a_flow_app() => UiHarness.RunAsync(async ui =>
    {
        ui.Vm.NewCommand.Execute(null);
        var w = await WaitForDialog(ui, ui.Window);
        Assert.IsType<NewProjectWindow>(w);
        var path = Named<TextBox>(w, "txt_Path");
        Click(ui, w, path);
        Retype(ui, w, ui.WorkDir);
        var name = Named<TextBox>(w, "txt_FileName");
        Assert.Equal("JMApp1", name.Text);
        Click(ui, w, name);
        Retype(ui, w, "bad name");
        Click(ui, w, Named<Button>(w, "btn_Ok"));
        var err = await WaitForDialog(ui, w);
        Assert.Contains(err.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.Text == Localizer.Instance.T("Xwb:Existedblank"));
        err.Close();
        await ui.SettleAsync();

        Click(ui, w, name);
        Retype(ui, w, "Robot1");
        Click(ui, w, Named<Button>(w, "btn_Ok"));
        await ui.SettleAsync(200);
        var rcu = Path.Combine(ui.WorkDir, "Robot1", "Robot1.rcu");
        Assert.True(File.Exists(rcu));
        Assert.True(File.Exists(Path.ChangeExtension(rcu, ".c")));
        var doc = Assert.IsType<FlowDocumentViewModel>(ui.Vm.ActiveDocument);
        Assert.True(doc.IsFile(rcu));
        Assert.Single(doc.Objects, o => o.DrawObject is IconDraw { IconType: IconType.Start });
        Assert.Contains("int main(void)", doc.Code);
    });

    /// <summary>A FlowFunc "LibSpin(int power)" calling SetMotor, saved in a library folder of the work dir.</summary>
    private static string CreateLibrarySub(UiHarness ui, string version)
    {
        var path = Path.Combine(ui.WorkDir, "lib", "LibSpin", "LibSpin.rcu");
        var session = File.Exists(path)
            ? ProjectSession.Open(path, ui.Install)
            : ProjectSession.CreateNew(path, ProjectType.FlowFunc, ui.Vm.Env.RobotType, ui.Install, projectIcon: DefaultProjectIcon.Get());
        session.Info.UserVersion = version;
        if (session.Info.ParamList!.Count == 0)
            session.Info.ParamList.Add(new ParameterClass { Name = "power", Type = "int", Min = "-100", Max = "100", DefaultValue = "40" });
        session.Save();
        return path;
    }

    private static void UseLibrary(UiHarness ui, string libRcu)
    {
        var lib = ui.Vm.Env.Library!;
        var xml = $"<root><Band Default=\"MyLib\" CHS=\"MyLib\" CHT=\"MyLib\" IsShow=\"true\"><FuncName IsSystem=\"false\" IsShow=\"true\">{libRcu}</FuncName></Band></root>";
        ui.Vm.SetUserLibrary(UserLibrary.Create(lib.Type, lib.Bands, lib.Find, ui.Vm.Env.Language, xml));
        ui.Pump();
    }

    [UiFact]
    public Task User_library_function_appears_in_the_palette_drops_compiles_and_updates() => UiHarness.RunAsync(async ui =>
    {
        var libRcu = CreateLibrarySub(ui, "1.0");
        UseLibrary(ui, libRcu);
        var band = Assert.Single(ui.Vm.Palette.Bands, b => b.Caption == "MyLib");
        var item = Assert.Single(band.Items);
        Assert.Equal(new PaletteDragItem(PaletteItemKind.Function, "LibSpin"), item.Item);
        Assert.NotNull(item.Icon);

        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var last = FlowCanvasUiTests.LastBlock(doc);
        var at = new Point(FlowCanvasUiTests.IconCenter(last).X + 100, FlowCanvasUiTests.IconCenter(last).Y);
        var call = Drop(ui, doc, PaletteItemKind.Function, "LibSpin", at);
        ui.Click(ui.WindowPoint(FlowCanvasUiTests.PortCenter(FlowCanvasUiTests.PortOf(last, PortType.Out))));
        ui.Move(ui.WindowPoint(FlowCanvasUiTests.PortCenter(FlowCanvasUiTests.PortOf(call, PortType.In))));
        ui.Click(ui.WindowPoint(FlowCanvasUiTests.PortCenter(FlowCanvasUiTests.PortOf(call, PortType.In))));
        Assert.Contains("#include \"LibSpin.c\"", doc.Code);
        Assert.Contains("LibSpin(40);", doc.Code);
        var projDir = doc.Session.ProjectDirectory;
        Assert.True(File.Exists(Path.Combine(projDir, "LibSpin.rcu")));
        Assert.True(File.Exists(Path.Combine(projDir, "LibSpin.c")));

        // The project's own subroutine band lists it too (RefreshOutlookBar private band).
        Assert.Contains(ui.Vm.Palette.Bands, b => b.Caption == doc.Session.Info.ProjectName && b.Items.Any(i => i.Item.Name == "LibSpin"));

        var toolchain = ui.Vm.Env.ResolveToolchain();
        if (toolchain.IsAvailable)
        {
            var outcome = await doc.Session.CompileAsync(new Builder(ui.Install, toolchain));
            Assert.True(outcome.Success, outcome.Build?.Log);
        }

        // Update Subroutine: the library copy changed (new version) → UpdateSubDlg → Yes copies it into the project.
        CreateLibrarySub(ui, "2.0");
        UseLibrary(ui, libRcu);
        ui.Vm.FeatureCommands["UpdateSub"].Execute(null);
        var dlg = await WaitForDialog(ui, ui.Window);
        Assert.Equal(Localizer.Instance.T("UpdateSubDlg:$this.Text"), dlg.Title);
        Click(ui, dlg, ButtonByText(dlg, "UpdateSubDlg:btnYes.Text"));
        await ui.SettleAsync(100);
        Assert.Equal("2.0", RcuReader.Open(Path.Combine(projDir, "LibSpin.rcu")).ProjectData!.ProjectInfo!.UserVersion);
        Assert.Equal("2.0", doc.Session.Data.GetUsedProjectInfo("LibSpin")!.UserVersion);
        Assert.True(doc.IsDirty);

        // Sub manager: the used subroutine is listed and cannot be deleted while an icon calls it.
        ui.Vm.FeatureCommands["ManagerSub"].Execute(null);
        var mgr = await WaitForDialog(ui, ui.Window);
        var list = mgr.GetVisualDescendants().OfType<ListBox>().First();
        Assert.Equal(1, list.ItemCount);
        list.SelectedIndex = 0;
        ui.Pump();
        Click(ui, mgr, ButtonByText(mgr, "SubManagerDlg:btnDelete.Text"));
        var refusal = await WaitForDialog(ui, mgr);
        Assert.Contains(refusal.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.Text == Localizer.Instance.T("CannotDeleteUsingIcon"));
        refusal.Close();
        await ui.SettleAsync();
        Assert.NotNull(doc.Session.Data.GetUsedProjectInfo("LibSpin"));
        mgr.Close();
        await ui.SettleAsync();
    });

    [UiFact]
    public Task System_library_dialog_lists_bands_and_cancel_keeps_the_library() => UiHarness.RunAsync(async ui =>
    {
        var before = ui.Vm.Palette.Bands.Select(b => b.Caption).ToList();
        ui.Vm.FeatureCommands["SystemLibrary"].Execute(null);
        var dlg = await WaitForDialog(ui, ui.Window);
        Assert.Equal(Localizer.Instance.T("SystemLibDlg:$this.Text"), dlg.Title);
        var tree = dlg.GetVisualDescendants().OfType<TreeView>().First();
        Assert.True(tree.ItemCount >= ui.Vm.Env.Library!.Bands.Count(b => b.IsShown));
        tree.SelectedItem = tree.Items[0];
        ui.Pump();
        Click(ui, dlg, ButtonByText(dlg, "SystemLibDlg:btnHide.Text"));
        Click(ui, dlg, ButtonByText(dlg, "SystemLibDlg:btnCancel.Text"));
        await ui.SettleAsync();
        Assert.Same(ui.Window, TopWindow(ui));
        Assert.Equal(before, ui.Vm.Palette.Bands.Select(b => b.Caption).ToList());
    });
}
