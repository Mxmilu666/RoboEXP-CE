using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using RoboExp.App.Flow;
using RoboExp.App.Flow.Editing;
using RoboExp.App.Palette;
using RoboExp.App.Properties;
using RoboExp.App.ViewModels;
using RoboExp.Core.Install;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;

namespace RoboExp.Tests.App.Ui;

/// <summary>Flowchart canvas driven through Avalonia's input pipeline (pointer/keys/drag-drop on the real main window).</summary>
public class FlowCanvasUiTests
{
    internal static Port PortOf(GraphicObject go, PortType type) => go.DrawObject!.Ports!.First(p => p.Type == type);

    /// <summary>World centre of a port's (small) hit rectangle.</summary>
    internal static Point PortCenter(Port port)
    {
        var r = FlowGeometry.PortRect(port, PortSize.Small);
        return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
    }

    /// <summary>A point inside the icon but outside its port rectangles.</summary>
    internal static Point IconCenter(GraphicObject go) => new(go.DrawObject!.Position.X + 16, go.DrawObject.Position.Y + 16);

    /// <summary>The last block of a chain: Out port free, an incoming port connected (MusicNoteTest: the EndWhile).</summary>
    internal static GraphicObject LastBlock(FlowDocumentViewModel doc) =>
        doc.Objects.Last(o => o.DrawObject is IconDraw icon
                              && icon.Ports!.Any(p => p.Type == PortType.Out && p.ConnectedPort == null)
                              && icon.Ports!.Any(p => p.Type != PortType.Out && p.ConnectedPort != null));

    internal static Port? FarEnd(Port port)
    {
        var p = port.ConnectedPort;
        while (p?.Parent is LineDraw l)
        {
            var other = ReferenceEquals(l.Ports![0], p) ? l.Ports[1] : l.Ports[0];
            p = other.ConnectedPort;
        }
        return p;
    }

    /// <summary>Drops a palette function right of the chain's last block and wires it with two port clicks.</summary>
    private static (GraphicObject Last, GraphicObject Added) DropAndConnect(UiHarness ui, FlowDocumentViewModel doc, string function = "SetLCDClear")
    {
        var last = LastBlock(doc);
        var dropWorld = new Point(IconCenter(last).X + 100, IconCenter(last).Y);
        ui.Reveal(dropWorld);
        ui.DropAt(ui.WindowPoint(dropWorld), PaletteDragData.Create(new PaletteDragItem(PaletteItemKind.Function, function)));
        var added = doc.Objects.Single(o => o.DataObject is FuncData f && f.ProjectName == function && !ReferenceEquals(o, last));
        ui.Click(ui.WindowPoint(PortCenter(PortOf(last, PortType.Out))));
        ui.Move(ui.WindowPoint(PortCenter(PortOf(added, PortType.In))));
        ui.Click(ui.WindowPoint(PortCenter(PortOf(added, PortType.In))));
        return (last, added);
    }

    /// <summary>
    /// Regression for the reported "wire to a dropped block not shown / not in the code": drop from the palette, wire it by
    /// port clicks, select it, undo and redo with the keyboard; model, generated code and the rendered frame must agree.
    /// </summary>
    [UiFact]
    public Task Palette_drop_then_port_clicks_wire_the_new_block_into_the_generated_code() => UiHarness.RunAsync(async ui =>
    {
        ui.Vm.ShowPanelCommand.Execute(PanelIds.Code);
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var codePanel = (FlowCodePanelViewModel)ui.Vm.Panel(PanelIds.Code).Content!;
        var last = LastBlock(doc);
        var dropWorld = new Point(IconCenter(last).X + 100, IconCenter(last).Y);
        ui.Reveal(dropWorld);
        ui.Evidence("headless-1-opened");

        ui.DropAt(ui.WindowPoint(dropWorld), PaletteDragData.Create(new PaletteDragItem(PaletteItemKind.Function, "SetLCDClear")));
        var added = doc.Objects.Single(o => o.DataObject is FuncData { ProjectName: "SetLCDClear" });
        Assert.Equal(new Point(dropWorld.X - 16, dropWorld.Y - 16), added.DrawObject!.Position);
        Assert.Equal("SetLCDClear(BLACK);", added.DataObject!.Code);
        Assert.False(doc.Session.Graph!.State(added).IsInCode);
        ui.Evidence("headless-2-dropped");

        ui.Click(ui.WindowPoint(PortCenter(PortOf(last, PortType.Out))));
        Assert.Equal(FlowWorking.Connect, ui.Canvas.Working);
        ui.Move(ui.WindowPoint(PortCenter(PortOf(added, PortType.In))));
        ui.Click(ui.WindowPoint(PortCenter(PortOf(added, PortType.In))));
        Assert.Equal(FlowWorking.NormalMove, ui.Canvas.Working);
        Assert.Same(PortOf(added, PortType.In), FarEnd(PortOf(last, PortType.Out)));
        Assert.Contains("SetLCDClear(BLACK);", doc.Code);
        Assert.Contains("SetLCDClear(BLACK);", codePanel.Document.Text);
        Assert.True(doc.Session.Graph!.State(added).IsInCode);

        // Selecting the block (Properties/Variables panels rebuild) must not lose the wire.
        ui.Click(ui.WindowPoint(IconCenter(added)));
        await ui.SettleAsync();
        Assert.Equal([added], doc.Selection);
        Assert.Contains("SetLCDClear(BLACK);", doc.Code);
        ui.Evidence("headless-3-connected");

        ui.Key(Key.Z, UiHarness.Cmd);
        Assert.DoesNotContain("SetLCDClear(BLACK);", doc.Code);
        Assert.DoesNotContain("SetLCDClear(BLACK);", codePanel.Document.Text);
        Assert.Single(doc.Objects, o => o.DataObject is FuncData { ProjectName: "SetLCDClear" });
        Assert.Null(FarEnd(PortOf(LastBlock(doc), PortType.Out)));
        ui.Evidence("headless-4-undo");

        ui.Key(Key.Y, UiHarness.Cmd);
        var redone = doc.Objects.Single(o => o.DataObject is FuncData { ProjectName: "SetLCDClear" });
        Assert.Contains("SetLCDClear(BLACK);", doc.Code);
        Assert.True(doc.Session.Graph!.State(redone).IsInCode);
        ui.Evidence("headless-5-redo");

        // Saving and reopening keeps it (the .c on disk equals the live code).
        await ui.Vm.SaveDocumentAsync(doc, saveAs: false);
        var saved = RoboExp.App.Services.TextFileCodec.Read(doc.Session.CPath).Text;
        Assert.Equal(doc.Code, saved);
    });

    [UiFact]
    public Task Click_ctrl_click_and_rubber_band_select() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var icons = doc.Objects.Where(o => o.DrawObject is IconDraw).OrderBy(o => o.DrawObject!.Position.Y).ThenBy(o => o.DrawObject!.Position.X).ToList();
        var a = icons[0];
        var b = icons[1];
        ui.Reveal(IconCenter(a));

        ui.Click(ui.WindowPoint(IconCenter(a)));
        Assert.Equal([a], doc.Selection);
        ui.Click(ui.WindowPoint(IconCenter(b)), mods: UiHarness.Cmd);
        Assert.Equal([a, b], doc.Selection);
        ui.Click(ui.WindowPoint(IconCenter(a)), mods: UiHarness.Cmd); // toggles off on release
        Assert.Equal([b], doc.Selection);

        // Marquee from empty space above-left of the first row to below the first two icons of it.
        var top = icons.Where(o => o.DrawObject!.Position.Y == a.DrawObject!.Position.Y).ToList();
        var from = new Point(top.Min(o => o.DrawObject!.Position.X) - 20, a.DrawObject!.Position.Y - 60);
        var to = new Point(top[1].DrawObject!.Position.X + 20, a.DrawObject.Position.Y + 20);
        ui.Drag(ui.WindowPoint(from), ui.WindowPoint(to));
        Assert.Contains(top[0], doc.Selection);
        Assert.Contains(top[1], doc.Selection);
        Assert.All(doc.Selection, s => Assert.True(FlowGeometry.HitTest(s.DrawObject!, Rect32.Real(new Rect32(from.X, from.Y, to.X - from.X, to.Y - from.Y)))));

        ui.Click(ui.WindowPoint(new Point(from.X, from.Y))); // empty space clears
        Assert.Empty(doc.Selection);
    });

    [UiFact]
    public Task Dragging_a_wired_block_moves_it_and_keeps_its_wires_attached_and_orthogonal() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var (last, added) = DropAndConnect(ui, doc);
        var before = added.DrawObject!.Position;
        ui.Drag(ui.WindowPoint(IconCenter(added)), ui.WindowPoint(new Point(IconCenter(added).X + 40, IconCenter(added).Y + 70)));
        Assert.Equal(new Point(before.X + 40, before.Y + 70), added.DrawObject!.Position);
        Assert.Same(PortOf(added, PortType.In), FarEnd(PortOf(last, PortType.Out)));
        Assert.All(doc.Objects.Select(o => o.DrawObject).OfType<LineDraw>(),
            l => Assert.True(FlowGeometry.IsHorizontal(l) || FlowGeometry.IsVertical(l)));
        Assert.Contains("SetLCDClear(BLACK);", doc.Code);

        ui.Key(Key.Z, UiHarness.Cmd); // the move is one undo step
        Assert.Equal(before, doc.Objects.Single(o => o.DataObject is FuncData { ProjectName: "SetLCDClear" }).DrawObject!.Position);
    });

    [UiFact]
    public Task Delete_key_on_a_wire_disconnects_the_block() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var (last, added) = DropAndConnect(ui, doc);
        var wire = (LineDraw)PortOf(last, PortType.Out).ConnectedPort!.Parent!;
        var mid = new Point((wire.Position.X + wire.EndPosition.X) / 2, (wire.Position.Y + wire.EndPosition.Y) / 2);
        ui.Click(ui.WindowPoint(mid));
        Assert.Equal([wire.Parent!], doc.Selection);
        ui.Key(Key.Delete);
        Assert.Null(PortOf(last, PortType.Out).ConnectedPort);
        Assert.Null(PortOf(added, PortType.In).ConnectedPort);
        Assert.DoesNotContain("SetLCDClear(BLACK);", doc.Code);
        Assert.Contains(added, doc.Objects);

        // Deleting the block itself also drops it from UsedProjectInfoList (system function no longer called).
        ui.Click(ui.WindowPoint(IconCenter(added)));
        ui.Key(Key.Delete);
        Assert.DoesNotContain(added, doc.Objects);
        Assert.Null(doc.Session.Data.GetUsedProjectInfo("SetLCDClear"));
    });

    [UiFact]
    public Task Copy_paste_shortcuts_duplicate_the_selection_at_the_last_click() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var (_, added) = DropAndConnect(ui, doc);
        ui.Click(ui.WindowPoint(IconCenter(added)));
        ui.Key(Key.C, UiHarness.Cmd);
        var target = new Point(IconCenter(added).X, IconCenter(added).Y + 120);
        ui.Click(ui.WindowPoint(target)); // paste anchor = last mouse-down (empty space)
        var count = doc.Objects.Count;
        ui.Key(Key.V, UiHarness.Cmd);
        Assert.Equal(count + 1, doc.Objects.Count);
        var pasted = Assert.Single(doc.Selection);
        Assert.NotSame(added, pasted);
        Assert.Equal("SetLCDClear(BLACK);", pasted.DataObject!.Code);
        Assert.Equal(new Point(target.X - 16, target.Y - 16), pasted.DrawObject!.Position);
        Assert.All(pasted.DrawObject.Ports!, p => Assert.Null(p.ConnectedPort)); // links outside the copy are dropped

        ui.Key(Key.X, UiHarness.Cmd); // cut = copy + delete
        Assert.Equal(count, doc.Objects.Count);
        ui.Key(Key.Z, UiHarness.Cmd);
        Assert.Equal(count + 1, doc.Objects.Count);
    });

    [UiFact]
    public Task Double_click_and_right_click_on_a_block_open_its_property_editor() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var (_, added) = DropAndConnect(ui, doc);

        ui.DoubleClick(ui.WindowPoint(IconCenter(added)));
        await ui.SettleAsync(100);
        var editor = Assert.Single(ui.Window.OwnedWindows.OfType<BlockEditorWindow>());
        editor.Close();
        await ui.SettleAsync();

        // E6RCU: right-click on the single selected block opens the floating editor (PropertyFrmNew) instead of the menu.
        ui.Click(ui.WindowPoint(IconCenter(added)), MouseButton.Right);
        await ui.SettleAsync(100);
        editor = Assert.Single(ui.Window.OwnedWindows.OfType<BlockEditorWindow>());
        editor.Close();
        await ui.SettleAsync();

        // Right-click on empty space: the flowchart context menu with the edit commands.
        ui.Click(ui.WindowPoint(new Point(IconCenter(added).X, IconCenter(added).Y + 150)), MouseButton.Right);
        await ui.SettleAsync();
        var menu = ui.Canvas.ContextMenu ?? ui.Window.GetVisualDescendants().OfType<ContextMenu>().FirstOrDefault();
        var items = ui.Window.GetVisualDescendants().OfType<MenuItem>()
            .Concat(TopLevel.GetTopLevel(ui.Canvas)!.GetVisualDescendants().OfType<MenuItem>())
            .Select(m => m.Header as string).Where(h => h != null).ToList();
        Assert.Contains(RoboExp.App.Localization.Localizer.Instance["Undo"], items);
        Assert.Contains(RoboExp.App.Localization.Localizer.Instance["FlowControl"], items);
        ui.Key(Key.Escape);
    });

    [UiFact]
    public Task Double_clicking_a_diagnostic_in_Output_selects_the_block_of_that_line() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var (_, added) = DropAndConnect(ui, doc);
        ui.Click(ui.WindowPoint(new Point(IconCenter(added).X, IconCenter(added).Y + 150)));
        Assert.Empty(doc.Selection);
        var line = doc.Session.Graph!.State(added).BeginLine;
        Assert.True(line > 0);

        ui.Vm.Output.SetOutput($"{doc.Session.CPath}:{line}:5: error: 'x' undeclared (first use in this function)");
        await ui.SettleAsync();
        var row = ui.Window.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is OutputRow { Line: not null });
        ui.DoubleClick(ui.CenterOf(row));
        await ui.SettleAsync();
        Assert.Equal([added], doc.Selection);
    });

    [UiFact]
    public Task Opening_a_project_of_another_robot_type_asks_first() => UiHarness.RunAsync(async ui =>
    {
        var path = Path.Combine(ui.Install.ExamplesDirectory(RobotType.Parse("C1RCU")), "BuiltIn", "DelayTest", "DelayTest.rcu");
        var loc = RoboExp.App.Localization.Localizer.Instance;

        ui.DialogAnswer = _ => loc["Port:No"];
        Assert.Null(await ui.Vm.OpenPathAsync(path));
        Assert.Empty(ui.Vm.Documents);
        Assert.Contains(loc.T("OpenDiffSoftTypeWarning"), ui.Dialogs);

        ui.DialogAnswer = null;
        ui.Dialogs.Clear();
        var doc = Assert.IsType<FlowDocumentViewModel>(await ui.Vm.OpenPathAsync(path));
        Assert.Equal([loc.T("OpenDiffSoftTypeWarning"), loc.T("RedefinePort")], ui.Dialogs);
        Assert.Equal(SoftwareType.E6RCU, doc.Session.Info.SoftwareType);
        Assert.Equal("E6RCU", doc.Session.Type.Name);
        Assert.Contains("E6RCU_Init();", doc.Code);
    });
}
