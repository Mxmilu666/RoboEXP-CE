using System.Text;
using Avalonia.Controls;
using Avalonia.VisualTree;
using RoboExp.App.Documents;
using RoboExp.App.Services;
using RoboExp.App.ViewModels;

namespace RoboExp.Tests.App.Ui;

[Collection(nameof(LocalizerStateCollection))]
public class AutoSaveTests
{
    [Fact]
    public Task Switching_tabs_saves_the_document_being_left() => UiHarness.RunAsync(async ui =>
    {
        var first = await OpenCodeAsync(ui, "first.c");
        var second = await OpenCodeAsync(ui, "second.c");
        var tabs = ui.Window.GetVisualDescendants().OfType<TabControl>()
            .Single(t => ReferenceEquals(t.ItemsSource, ui.Vm.Documents));
        tabs.SelectedItem = first;
        first.Document.Replace(0, first.Document.TextLength, "int saved_on_switch = 1;");
        tabs.SelectedItem = second;
        for (var i = 0; i < 100 && first.IsDirty; i++) await ui.SettleAsync(10);
        Assert.Equal("int saved_on_switch = 1;", File.ReadAllText(first.FilePath!));
        Assert.False(first.IsDirty);
        Assert.Same(second, ui.Vm.ActiveDocument);
        first.Document.UndoStack.Undo();
        Assert.True(first.IsDirty);
    });

    [Fact]
    public Task Auto_save_skips_untitled_and_clean_files_and_reports_write_failure() => UiHarness.RunAsync(async ui =>
    {
        var clean = await OpenCodeAsync(ui, "clean.c");
        File.WriteAllText(clean.FilePath!, "external edit");
        var untitled = new CodeDocumentViewModel("", Encoding.UTF8, null);
        ui.Vm.AddDocument(untitled);
        untitled.Document.Insert(0, "unsaved");
        var failed = await OpenCodeAsync(ui, "failed.c");
        failed.Document.Replace(0, failed.Document.TextLength, "keep my edits");
        File.Delete(failed.FilePath!);
        Directory.CreateDirectory(failed.FilePath!);
        await ui.Vm.AutoSaveDocumentsAsync();
        Assert.Equal("external edit", File.ReadAllText(clean.FilePath!));
        Assert.Null(untitled.FilePath);
        Assert.True(untitled.IsDirty);
        Assert.True(failed.IsDirty);
        Assert.Equal("keep my edits", failed.Document.Text);
        Assert.Contains(ui.Vm.Output.Rows, r => r.Severity == OutputSeverity.Error && r.File == failed.FilePath);
        Assert.Contains("failed.c", ui.Vm.StatusText);
        Assert.Empty(ui.Dialogs);
    });

    [Fact]
    public Task Edits_during_background_write_remain_dirty_and_are_saved_on_next_trigger() => UiHarness.RunAsync(async ui =>
    {
        var doc = await OpenCodeAsync(ui, "editing.c");
        doc.Document.Replace(0, doc.Document.TextLength, "first version");
        var saving = ui.Vm.AutoSaveDocumentsAsync();
        doc.Document.Replace(0, doc.Document.TextLength, "newer version");
        await saving;
        Assert.Equal("first version", File.ReadAllText(doc.FilePath!));
        Assert.True(doc.IsDirty);
        await ui.Vm.AutoSaveDocumentsAsync();
        Assert.Equal("newer version", File.ReadAllText(doc.FilePath!));
        Assert.False(doc.IsDirty);
    });

    private static async Task<CodeDocumentViewModel> OpenCodeAsync(UiHarness ui, string name)
    {
        var path = Path.Combine(ui.WorkDir, name);
        File.WriteAllText(path, "int original;");
        return Assert.IsType<CodeDocumentViewModel>(await ui.Vm.OpenPathAsync(path));
    }
}
