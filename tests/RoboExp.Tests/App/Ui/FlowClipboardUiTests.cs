using Avalonia.Input;
using Avalonia.Input.Platform;
using RoboExp.App.Flow;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;
using RoboExp.Core.Rcu;

namespace RoboExp.Tests.App.Ui;

public class FlowClipboardUiTests
{
    [UiFact]
    public Task Original_format_pastes_blocks_and_preserves_undo_without_internal_clipboard() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var original = doc.Objects.First(o => o.DataObject is FuncData);
        var sourceCode = original.DataObject!.Code;
        var cloned = FlowClipboard.Clone([original], null, out _);
        var collection = new GraphicObjectCollection();
        collection.List!.Add(cloned.Single());
        using var stream = new MemoryStream();
        stream.Write(new Guid("FD9EA796-3B13-4370-A679-56106BB288FB").ToByteArray());
        RcuWriter.WriteGraphicObjects(stream, collection);
        var clipboard = ui.Window.Clipboard!;
        await clipboard.SetValueAsync(FlowClipboard.LegacyFormat, stream.ToArray());
        var before = doc.Objects.ToArray();
        Assert.True(await FlowClipboard.HasDataAsync(clipboard));

        await doc.PasteAsync();

        var added = Assert.Single(doc.Objects.Except(before));
        Assert.Equal(sourceCode, added.DataObject!.Code);
        Assert.NotSame(original.DataObject, added.DataObject);
        Assert.All(added.DrawObject!.Ports!, p => Assert.Null(p.ConnectedPort));
        Assert.Equal([added], doc.Selection);
        doc.DoUndo();
        Assert.Equal(before.Length, doc.Objects.Count);
        doc.DoRedo();
        Assert.Contains(doc.Objects, o => o.DataObject?.Code == sourceCode && !before.Contains(o));
        await clipboard.ClearAsync();
    });

    [UiFact]
    public Task Copy_publishes_original_format_and_text_replacement_does_not_paste_stale_selection() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var selected = doc.Objects.First(o => o.DataObject is FuncData);
        doc.SetSelection([selected]);
        var oldConnections = selected.DrawObject!.Ports!.Select(p => p.ConnectedPort).ToArray();
        var clipboard = ui.Window.Clipboard!;

        await doc.CopyAsync();

        var bytes = await clipboard.TryGetValueAsync(FlowClipboard.LegacyFormat);
        Assert.NotNull(bytes);
        Assert.Equal(new Guid("FD9EA796-3B13-4370-A679-56106BB288FB").ToByteArray(), bytes[..16]);
        var decoded = Assert.Single(FlowClipboard.DecodeLegacy(bytes));
        Assert.Equal(selected.DataObject!.Code, decoded.DataObject!.Code);
        Assert.All(decoded.DrawObject!.Ports!, p => Assert.Null(p.ConnectedPort));
        Assert.Equal(oldConnections, selected.DrawObject.Ports!.Select(p => p.ConnectedPort));
        var before = doc.Objects.ToArray();
        await clipboard.SetTextAsync("unrelated clipboard text");
        Assert.False(await FlowClipboard.HasDataAsync(clipboard));
        await doc.PasteAsync();
        Assert.Equal(before, doc.Objects);
        await clipboard.ClearAsync();
    });

    [UiFact]
    public Task Corrupt_original_payload_and_unavailable_clipboard_never_modify_document() => UiHarness.RunAsync(async ui =>
    {
        var doc = await ui.OpenExampleCopyAsync("E6RCU", "Other/MusicNoteTest");
        var before = doc.Objects.ToArray();
        await ui.Window.Clipboard!.SetValueAsync(FlowClipboard.LegacyFormat, new byte[] { 0, 1, 2 });
        await doc.PasteAsync();
        Assert.Equal(before, doc.Objects);
        doc.SetSelection([before.First()]);
        doc.ClipboardProvider = () => null;
        await doc.CutAsync();
        Assert.Equal(before, doc.Objects);
        await ui.Window.Clipboard!.ClearAsync();
    });
}
