using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;
using RoboExp.Core.Project;

namespace RoboExp.App.Flow.Editing;

/// <summary>Constructors of the original graphic objects (GraphicObject + LineDraw/IconDraw/TextDraw).</summary>
public static class FlowFactory
{
    /// <summary><c>new LineDraw(start, end)</c> in a GraphicObject with a <see cref="LineCode"/> (ports: In = start, Out = end).</summary>
    public static GraphicObject NewLine(Point start, Point end)
    {
        var go = new GraphicObject { DataObject = new DataObject(), CodeObject = new LineCode() };
        var line = new LineDraw { Parent = go, Position = start, EndPosition = end, AutoSize = false };
        line.Ports!.Add(new Port { Parent = line, Type = PortType.In });
        line.Ports.Add(new Port { Parent = line, Type = PortType.Out });
        go.DrawObject = line;
        return go;
    }

    /// <summary><c>new IconDraw(pos, img, type)</c> (32x32, InitPorts) carrying <paramref name="data"/>.</summary>
    public static GraphicObject NewIcon(IconType type, Point topLeft, EncodedImage? image, DataObject data) =>
        ProjectSession.NewIcon(type, topLeft, image, data);

    /// <summary>DesignSurface Text tool: <c>new TextDraw(point, text)</c> with a plain DataObject.</summary>
    public static GraphicObject NewText(Point position, string text)
    {
        var go = new GraphicObject { DataObject = new DataObject(), CodeObject = new CodeObject() };
        go.DrawObject = new TextDraw { Parent = go, Position = position, Text = text, AutoSize = false };
        return go;
    }

    /// <summary>FlowView.AddCtrlIcon payload: the DataObject subclass for <paramref name="type"/> with ProjectName/Code "".</summary>
    public static DataObject NewControlData(IconType type)
    {
        DataObject data = type switch
        {
            IconType.BeginIf => new BeginIfData(),
            IconType.BeginWhile => new BeginWhileData(),
            IconType.BeginFor => new BeginForData(),
            IconType.Calculate => new CalculateData(),
            IconType.Return => new ReturnData(),
            IconType.CodeEditor => new CodeEditorData(),
            _ => new DataObject(),
        };
        data.ProjectName = "";
        data.Code = "";
        return data;
    }

    /// <summary>FlowView.AddPairEndIcon: the End icon auto-added for a Begin icon, or null.</summary>
    public static IconType? PairEndType(IconType type) => type switch
    {
        IconType.BeginIf => IconType.EndIf,
        IconType.BeginWhile => IconType.EndWhile,
        IconType.BeginFor => IconType.EndFor,
        _ => null,
    };
}
