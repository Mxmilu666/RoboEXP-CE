using Avalonia.Input;
using Avalonia.Input.Platform;
using RoboExp.App.Flow.Editing;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;
using RoboExp.Core.Rcu;

namespace RoboExp.App.Flow;

/// <summary>System clipboard exchange with the original WinForms flowchart and other RoboExp CE processes.</summary>
public static class FlowClipboard
{
    public const string LegacyFormatName = "RoboExp.GraphicObjects.GraphicObjectCollection";
    public static DataFormat<byte[]> LegacyFormat { get; } = DataFormat.CreateBytesPlatformFormat(LegacyFormatName);
    private static readonly DataFormat<byte[]> ProjectFormat = DataFormat.CreateBytesApplicationFormat("RoboExpCE.FlowSelection");
    // .NET Framework DataObject prefixes BinaryFormatter objects (but not raw streams) with this GUID.
    private static readonly byte[] SerializedObjectId = new Guid("FD9EA796-3B13-4370-A679-56106BB288FB").ToByteArray();

    /// <summary>Clones selected objects and removes all links leaving the selection.</summary>
    public static List<GraphicObject> Clone(IReadOnlyList<GraphicObject> objects, IEnumerable<ProjectInfo>? used, out List<ProjectInfo> usedClone)
    {
        var bytes = Serialize(objects, used);
        return Deserialize(bytes, out usedClone);
    }

    public static async Task<bool> HasDataAsync(IClipboard? clipboard)
    {
        if (clipboard == null) return false;
        var formats = await clipboard.GetDataFormatsAsync();
        return formats.Contains(ProjectFormat) || formats.Contains(LegacyFormat);
    }

    public static async Task SetAsync(IClipboard clipboard, IReadOnlyList<GraphicObject> objects, IEnumerable<ProjectInfo> used)
    {
        // Clone before making independent: never disconnect links in the source document.
        var clones = Clone(objects, used, out var usedClone);
        var item = DataTransferItem.Create(ProjectFormat, Serialize(clones, usedClone));
        item.Set(LegacyFormat, EncodeLegacy(clones));
        var transfer = new DataTransfer();
        transfer.Add(item);
        await clipboard.SetDataAsync(transfer);
        await clipboard.FlushAsync();
    }

    public static async Task<(List<GraphicObject> Objects, List<ProjectInfo> Used)?> GetAsync(IClipboard clipboard)
    {
        using var transfer = await clipboard.TryGetDataAsync();
        if (transfer == null) return null;
        if (await transfer.TryGetValueAsync(ProjectFormat) is { } project)
        {
            var objects = Deserialize(project, out var used);
            return (objects, used);
        }
        if (await transfer.TryGetValueAsync(LegacyFormat) is not { } legacy) return null;
        return (DecodeLegacy(legacy), []);
    }

    internal static byte[] EncodeLegacy(IReadOnlyList<GraphicObject> objects)
    {
        var collection = new GraphicObjectCollection();
        foreach (var go in objects) collection.List!.Add(go);
        using var stream = new MemoryStream();
        stream.Write(SerializedObjectId);
        RcuWriter.WriteGraphicObjects(stream, collection);
        return stream.ToArray();
    }

    internal static List<GraphicObject> DecodeLegacy(byte[] bytes)
    {
        var offset = bytes.AsSpan().StartsWith(SerializedObjectId) ? SerializedObjectId.Length : 0;
        using var stream = new MemoryStream(bytes, offset, bytes.Length - offset, writable: false);
        var list = RcuReader.ReadGraphicObjects(stream).List?.ToList() ?? [];
        FlowEditor.MakeIndependent(list);
        return list;
    }

    private static byte[] Serialize(IReadOnlyList<GraphicObject> objects, IEnumerable<ProjectInfo>? used)
    {
        var collection = new GraphicObjectCollection();
        foreach (var go in objects) collection.List!.Add(go);
        var data = new ProjectData { ProjectInfo = new ProjectInfo() };
        foreach (var info in used ?? []) data.UsedProjectInfoList!.Add(info);
        var file = new RcuFile
        {
            ProjectType = ProjectType.FlowApp,
            FlowData = new FlowData { ProjectData = data, DesignSurfaceData = new DesignSurfaceData { DrawingObjects = collection } },
        };
        using var stream = new MemoryStream();
        RcuWriter.Write(stream, file);
        return stream.ToArray();
    }

    private static List<GraphicObject> Deserialize(byte[] bytes, out List<ProjectInfo> used)
    {
        using var stream = new MemoryStream(bytes);
        var file = RcuReader.Read(stream);
        used = [.. (IEnumerable<ProjectInfo>?)file.ProjectData?.UsedProjectInfoList ?? []];
        var list = file.FlowData?.DesignSurfaceData?.DrawingObjects?.List?.ToList() ?? [];
        FlowEditor.MakeIndependent(list);
        return list;
    }
}
