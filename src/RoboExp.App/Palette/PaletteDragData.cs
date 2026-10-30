using Avalonia.Input;

namespace RoboExp.App.Palette;

/// <summary>What a palette icon represents.</summary>
public enum PaletteItemKind
{
    /// <summary>Flow-control icon; <see cref="PaletteDragItem.Name"/> is the original <c>IconType</c> name (BeginIf, BeginWhile, BeginFor, Calculate, Continue, Break, Return, CodeEditor, Start).</summary>
    Control,
    /// <summary>System library function; <see cref="PaletteDragItem.Name"/> is the JMlib.xml <c>&lt;Func&gt;&lt;Name&gt;</c> (resolve with <c>SystemLibrary.Find</c>).</summary>
    Function,
}

public sealed record PaletteDragItem(PaletteItemKind Kind, string Name);

/// <summary>
/// Drag-and-drop contract between the Icons palette (source) and the flowchart canvas (target).
/// <para>Format: application string format <c>roboexp-palette-item</c>; payload <c>"ctrl:&lt;IconType&gt;"</c> or
/// <c>"func:&lt;FunctionName&gt;"</c>, e.g. <c>"ctrl:BeginIf"</c>, <c>"func:SetMotor"</c>. Effect: Copy.</para>
/// Drop targets: <c>DragDrop.SetAllowDrop(canvas, true)</c>, handle <c>DragDrop.DropEvent</c> and call
/// <see cref="TryRead"/> on <c>e.DataTransfer</c>.
/// </summary>
public static class PaletteDragData
{
    public static DataFormat<string> Format { get; } = DataFormat.CreateStringApplicationFormat("roboexp-palette-item");

    private const string ControlPrefix = "ctrl:";
    private const string FunctionPrefix = "func:";

    public static string Encode(PaletteDragItem item) =>
        (item.Kind == PaletteItemKind.Control ? ControlPrefix : FunctionPrefix) + item.Name;

    public static PaletteDragItem? Decode(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return null;
        if (payload.StartsWith(ControlPrefix, StringComparison.Ordinal) && payload.Length > ControlPrefix.Length)
            return new PaletteDragItem(PaletteItemKind.Control, payload[ControlPrefix.Length..]);
        if (payload.StartsWith(FunctionPrefix, StringComparison.Ordinal) && payload.Length > FunctionPrefix.Length)
            return new PaletteDragItem(PaletteItemKind.Function, payload[FunctionPrefix.Length..]);
        return null;
    }

    public static DataTransfer Create(PaletteDragItem item)
    {
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(Format, Encode(item)));
        return transfer;
    }

    public static PaletteDragItem? TryRead(IDataTransfer? data) => data == null ? null : Decode(data.TryGetValue(Format));
}
