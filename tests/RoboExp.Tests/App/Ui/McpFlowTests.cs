using System.Text.Json;
using Avalonia.Input;
using RoboExp.App.Flow;
using RoboExp.App.Mcp;
using RoboExp.App.Services;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;
using RoboExp.Core.Project;

namespace RoboExp.Tests.App.Ui;

[Collection(nameof(LocalizerStateCollection))]
public class McpFlowTests
{
    [UiFact]
    public Task Existing_music_argument_and_expression_edits_generate_undo_and_roundtrip() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var editor = new McpFlowEditor();
        var original = Capture(doc);
        var originalCode = doc.Code;
        var originalDirty = doc.IsDirty;
        var read = Json(editor.Read(doc));
        Assert.Equal(original, Capture(doc)); // Read is not an edit and must not change the revision.
        Assert.Equal(originalDirty, doc.IsDirty);
        Assert.True(read.GetProperty("nodes").GetArrayLength() > 0);
        Assert.True(read.TryGetProperty("hardware", out _));
        var call = doc.Objects.First(o => o.DataObject is FuncData f && doc.Session.Data.GetUsedProjectInfo(f.ProjectName ?? "")
            ?.ParamList?.Any(p => !p.IsHardwareType && !p.IsEnumType) == true);
        var data = (FuncData)call.DataObject!;
        var signature = doc.Session.Data.GetUsedProjectInfo(data.ProjectName!)!;
        var parameter = signature.ParamList!.First(p => !p.IsHardwareType && !p.IsEnumType);
        var condition = doc.Objects.First(o => o.DataObject is BeginWhileData or BeginIfData or CalculateData);
        editor.Apply(doc, Ops(
            new { op = "set_arguments", node = Id(doc, call), arguments = new Dictionary<string, string> { [parameter.Name!] = "37 + 1" } },
            new { op = "set_expression", node = Id(doc, condition), expression = "1 == 1" }));
        Assert.Contains("37 + 1", doc.Code);
        Assert.Contains("1 == 1", doc.Code);
        Assert.True(doc.IsDirty);
        Assert.True(doc.Undo.CanUndo);
        var updated = doc.Code;
        // MCP edits do not steal keyboard focus from the user's current panel. Exercise the canvas shortcut explicitly.
        Assert.True(ui.Canvas.Focus());
        ui.Pump();
        ui.Key(Key.Z, UiHarness.Cmd);
        Assert.Equal(originalCode, doc.Code);
        Assert.Equal(original, Capture(doc));
        ui.Key(Key.Y, UiHarness.Cmd);
        Assert.Equal(updated, doc.Code);
        await ui.Vm.SaveDocumentAsync(doc, saveAs: false);
        Assert.Equal(updated, TextFileCodec.Read(doc.Session.CPath).Text);
        var reopened = ProjectSession.Open(doc.FilePath!, ui.Install, doc.Session.Type);
        Assert.Equal(updated, reopened.Regenerate());
        Assert.Contains(reopened.DrawingObjects!.List!, o => o.DataObject is FuncData f && f.ValueList!.Contains("37 + 1"));
    });

    [UiFact]
    public Task Bad_batch_preserves_live_objects_dirty_revision_and_both_undo_stacks() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var editor = new McpFlowEditor();
        editor.Apply(doc, Ops(new { op = "add_text", id = "first", x = 500, y = 500, text = "first" }));
        editor.Apply(doc, Ops(new { op = "add_text", id = "second", x = 500, y = 550, text = "second" }));
        doc.DoUndo(); // Deliberately retain both an undo and a redo entry.
        var before = Capture(doc);
        var liveObjects = doc.Objects.ToArray();
        var code = doc.Code;
        var dirty = doc.IsDirty;
        var undo = doc.Undo.UndoDescription;
        var redo = doc.Undo.RedoDescription;
        Assert.True(doc.Undo.CanUndo);
        Assert.True(doc.Undo.CanRedo);
        Assert.Throws<ArgumentException>(() => editor.Apply(doc, Ops(
            new { op = "add_control", id = "block", kind = "CodeEditor", x = 600, y = 500 },
            new { op = "set_code", node = "block", code = "int should_not_exist = 1;" },
            new { op = "set_expression", node = "block", expression = "wrong payload" })));
        Assert.Equal(before, Capture(doc));
        Assert.Equal(liveObjects, doc.Objects);
        Assert.Equal(code, doc.Code);
        Assert.Equal(dirty, doc.IsDirty);
        Assert.Equal(undo, doc.Undo.UndoDescription);
        Assert.Equal(redo, doc.Undo.RedoDescription);
        Assert.True(doc.Undo.CanUndo);
        Assert.True(doc.Undo.CanRedo);
        doc.DoRedo();
        Assert.Contains(doc.Objects, o => o.DrawObject is TextDraw { Text: "second" });
    });

    [UiFact]
    public Task Insert_connect_move_disconnect_delete_code_block_is_one_undoable_batch() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var editor = new McpFlowEditor();
        var last = FlowCanvasUiTests.LastBlock(doc);
        var fromPort = last.DrawObject!.Ports!.IndexOf(FlowCanvasUiTests.PortOf(last, PortType.Out));
        var lastIndex = doc.Objects.ToList().IndexOf(last);
        var before = Capture(doc);
        var inserted = editor.Apply(doc, Ops(
            new { op = "add_control", id = "block", kind = "CodeEditor", x = last.DrawObject.Position.X + 150, y = last.DrawObject.Position.Y },
            new { op = "set_code", node = "block", code = "int mcp_inserted = 42;\r\nmcp_inserted++;" },
            new { op = "set_comment", node = "block", comment = "Inserted by MCP" },
            new { op = "connect", from = Id(doc, last), fromPort, to = "block", toPort = 0 }));
        var id = Json(inserted.Created).GetProperty("block").GetString()!;
        var block = doc.Objects[int.Parse(id.AsSpan(1))];
        Assert.IsType<CodeEditorData>(block.DataObject);
        Assert.Same(FlowCanvasUiTests.PortOf(block, PortType.In), FlowCanvasUiTests.FarEnd(FlowCanvasUiTests.PortOf(doc.Objects[lastIndex], PortType.Out)));
        Assert.Contains("int mcp_inserted = 42;", doc.Code);
        Assert.Contains("mcp_inserted++;", doc.Code);
        Assert.Contains("//Inserted by MCP", doc.Code);
        doc.DoUndo();
        Assert.Equal(before, Capture(doc));
        doc.DoRedo();
        block = doc.Objects.Single(o => o.DataObject is CodeEditorData c && c.Codes!.Contains("int mcp_inserted = 42;"));
        var blockId = Id(doc, block);
        editor.Apply(doc, Ops(new { op = "move", node = blockId, x = 700, y = 300 }));
        Assert.Contains("int mcp_inserted = 42;", doc.Code);
        block = doc.Objects.Single(o => o.DataObject is CodeEditorData c && c.Codes!.Contains("int mcp_inserted = 42;"));
        Assert.Equal(new Point(700, 300), block.DrawObject!.Position);
        await ui.Vm.SaveDocumentAsync(doc, saveAs: false);
        var reopened = ProjectSession.Open(doc.FilePath!, ui.Install, doc.Session.Type);
        Assert.Contains("mcp_inserted++;", reopened.Regenerate());
        blockId = Id(doc, block);
        editor.Apply(doc, Ops(new { op = "disconnect", node = blockId, port = 0 }, new { op = "remove", node = blockId }));
        Assert.DoesNotContain("mcp_inserted", doc.Code);
        Assert.DoesNotContain(doc.Objects, o => o.DataObject is CodeEditorData c && c.Codes!.Contains("int mcp_inserted = 42;"));
        doc.DoUndo();
        Assert.Contains("mcp_inserted++;", doc.Code);
    });

    [UiFact]
    public Task Paired_controls_variables_hardware_validation_and_function_discovery_are_explicit() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var editor = new McpFlowEditor();
        var functions = Json(editor.ListFunctions(doc, "SetMotor"));
        Assert.Contains(functions.GetProperty("functions").EnumerateArray(), f => f.GetProperty("name").GetString() == "SetMotor");
        var result = editor.Apply(doc, Ops(
            new { op = "set_variable", scope = "local", name = "mcp_repeat", type = "long", defaultValue = "3" },
            new { op = "add_control", id = "repeat", kind = "BeginFor", x = 800, y = 300 },
            new { op = "set_for", node = "repeat", count = "mcp_repeat" },
            new { op = "add_function", id = "motor", function = "SetMotor", x = 900, y = 400 }));
        var created = Json(result.Created);
        Assert.True(created.TryGetProperty("repeat:end", out _));
        Assert.Contains(doc.Objects, o => o.DrawObject is IconDraw { IconType: IconType.EndFor });
        Assert.Contains(doc.Session.Data.VarList!, v => v.Name == "mcp_repeat");
        var motor = doc.Objects.Single(o => o.DataObject is FuncData { ProjectName: "SetMotor" });
        var signature = doc.Session.Data.GetUsedProjectInfo("SetMotor")!;
        var hardware = signature.ParamList!.First(p => p.IsHardwareType);
        var before = Capture(doc);
        Assert.Throws<ArgumentException>(() => editor.Apply(doc, Ops(new
        {
            op = "set_arguments", node = Id(doc, motor), arguments = new Dictionary<string, string> { [hardware.Name!] = "_UNKNOWN_" },
        })));
        Assert.Equal(before, Capture(doc));
        editor.Apply(doc, Ops(new { op = "remove_variable", scope = "local", name = "mcp_repeat" }));
        Assert.DoesNotContain(doc.Session.Data.VarList!, v => v.Name == "mcp_repeat");
    });

    private static JsonElement Ops(params object[] operations) => JsonSerializer.SerializeToElement(operations);
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static string Id(FlowDocumentViewModel doc, GraphicObject go) => "n" + doc.Objects.ToList().IndexOf(go);
    private static byte[] Capture(FlowDocumentViewModel doc)
    {
        using var stream = new MemoryStream();
        doc.Session.WriteRcu(stream);
        return stream.ToArray();
    }
}
