using System.Text.Json;
using RoboExp.App.Documents;
using RoboExp.App.Flow;
using RoboExp.App.Mcp;
using RoboExp.Core.Project;
using RoboExp.Core.Model;

namespace RoboExp.Tests.App.Ui;

[Collection(nameof(LocalizerStateCollection))]
public class McpWorkspaceTests
{
    [Fact]
    public Task Code_edit_updates_live_editor_undo_and_disk_and_rejects_stale_revision() => UiHarness.RunAsync(async ui =>
    {
        var path = Path.Combine(ui.WorkDir, "program.c");
        File.WriteAllText(path, "int speed = 1;\n");
        var doc = Assert.IsType<CodeDocumentViewModel>(await ui.Vm.OpenPathAsync(path));
        var workspace = new McpWorkspace(ui.Vm);
        var info = (await Call(workspace, "list_documents", new { })).GetProperty("documents")[0];
        var id = info.GetProperty("documentId").GetString()!;
        var revision = info.GetProperty("revision").GetString()!;
        var result = await Call(workspace, "edit_code", new { documentId = id, expectedRevision = revision, section = "code", oldText = "speed = 1", newText = "speed = 2" });
        Assert.Equal("int speed = 2;\n", doc.Document.Text);
        Assert.True(doc.IsDirty);
        Assert.Equal("int speed = 1;\n", File.ReadAllText(path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Call(workspace, "edit_code", new { documentId = id, expectedRevision = revision, section = "code", oldText = "2", newText = "3" }));
        revision = result.GetProperty("document").GetProperty("revision").GetString()!;
        result = await Call(workspace, "undo", new { documentId = id, expectedRevision = revision });
        Assert.Equal("int speed = 1;\n", doc.Document.Text);
        result = await Call(workspace, "redo", new { documentId = id, expectedRevision = Revision(result) });
        Assert.Equal("int speed = 2;\n", doc.Document.Text);
        await Call(workspace, "save_document", new { documentId = id, expectedRevision = Revision(result) });
        Assert.Equal("int speed = 2;\n", File.ReadAllText(path));
        Assert.False(doc.IsDirty);
        doc.Document.Insert(0, "// user edit\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Call(workspace, "edit_code", new { documentId = id, expectedRevision = Revision(result), section = "code", oldText = "2", newText = "9" }));
        Assert.StartsWith("// user edit", doc.Document.Text);
    });

    [Fact]
    public Task Ambiguous_replacements_untitled_save_and_unopened_paths_do_not_mutate_workspace() => UiHarness.RunAsync(async ui =>
    {
        var doc = new CodeDocumentViewModel("same same", System.Text.Encoding.UTF8, null);
        ui.Vm.AddDocument(doc);
        var workspace = new McpWorkspace(ui.Vm);
        var list = await Call(workspace, "list_documents", new { });
        var info = list.GetProperty("documents")[0];
        var id = info.GetProperty("documentId").GetString();
        var revision = info.GetProperty("revision").GetString();
        await Assert.ThrowsAsync<ArgumentException>(() => Call(workspace, "edit_code", new { documentId = id, expectedRevision = revision, section = "code", oldText = "same", newText = "other" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Call(workspace, "save_document", new { documentId = id, expectedRevision = revision }));
        await Assert.ThrowsAsync<ArgumentException>(() => Call(workspace, "read_document", new { documentId = "/etc/passwd" }));
        Assert.Equal("same same", doc.Document.Text);
        Assert.False(doc.Document.UndoStack.CanUndo);
        Assert.Empty(ui.Dialogs);
    });

    [UiFact]
    public Task Code_project_include_edits_preserve_declaration_and_roundtrip_in_rcu() => UiHarness.RunAsync(async ui =>
    {
        var session = ProjectSession.CreateNew(Path.Combine(ui.WorkDir, "Function.rcu"), ProjectType.CodeFunc, ui.Vm.Env.RobotType, ui.Install);
        var doc = new CodeProjectDocumentViewModel(session);
        ui.Vm.AddDocument(doc);
        var declaration = doc.Declaration!.Text;
        var workspace = new McpWorkspace(ui.Vm);
        var info = (await Call(workspace, "list_documents", new { })).GetProperty("documents")[0];
        var id = info.GetProperty("documentId").GetString();
        var result = await Call(workspace, "edit_code", new { documentId = id, expectedRevision = info.GetProperty("revision").GetString(), section = "include", oldText = doc.Include!.Text, newText = "#include <stdint.h>\r\n" });
        Assert.Equal(declaration, doc.Declaration.Text);
        Assert.Equal("#include <stdint.h>\r\n", doc.Include.Text);
        await Call(workspace, "save_document", new { documentId = id, expectedRevision = Revision(result) });
        var saved = ProjectSession.Open(doc.FilePath!, ui.Install, ui.Vm.Env.RobotType);
        Assert.Equal("#include <stdint.h>\r\n", saved.Data.CodeFrameData!.Global);
    });

    private static string Revision(JsonElement result) => result.GetProperty("document").GetProperty("revision").GetString()!;
    private static async Task<JsonElement> Call(McpWorkspace workspace, string name, object args) =>
        JsonSerializer.SerializeToElement(await workspace.CallAsync(name, JsonSerializer.SerializeToElement(args), CancellationToken.None), new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
