using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;
using RoboExp.Core.Project;

namespace RoboExp.Tests.CodeGen;

/// <summary>Builds flowcharts the way the editor stores them: icons plus LineDraw wires (Ports[0] In, Ports[1] Out) in order.</summary>
internal sealed class FlowBuilder
{
    public GraphicObjectCollection Objects { get; } = new();

    public GraphicObject Icon(IconType type, DataObject? data = null, params string[] description)
    {
        data ??= type switch
        {
            IconType.BeginIf => new BeginIfData(),
            IconType.BeginWhile => new BeginWhileData(),
            IconType.BeginFor => new BeginForData(),
            IconType.Calculate => new CalculateData(),
            IconType.Return => new ReturnData(),
            IconType.CodeEditor => new CodeEditorData(),
            _ => new DataObject(),
        };
        if (description.Length > 0) data.Description = description;
        var go = ProjectSession.NewIcon(type, new Point(0, 0), null, data);
        Objects.List!.Add(go);
        return go;
    }

    public GraphicObject Calc(string expression, params string[] description) =>
        Icon(IconType.Calculate, new CalculateData { Expression = expression }, description);

    /// <summary>Wires <paramref name="fromPort"/> of <paramref name="from"/> to <paramref name="toPort"/> of <paramref name="to"/> with <paramref name="segments"/> line segments.</summary>
    public void Wire(GraphicObject from, PortType fromPort, GraphicObject to, PortType toPort, int segments = 1)
    {
        var src = Port(from, fromPort);
        for (var i = 0; i < segments; i++)
        {
            var line = new GraphicObject { CodeObject = new LineCode() };
            var draw = new LineDraw { Parent = line };
            var a = new Port { Parent = draw, Type = PortType.In };
            var b = new Port { Parent = draw, Type = PortType.Out };
            draw.Ports!.Add(a);
            draw.Ports.Add(b);
            line.DrawObject = draw;
            Objects.List!.Add(line);
            src.ConnectedPort = a;
            a.ConnectedPort = src;
            src = b;
        }
        var dst = Port(to, toPort);
        src.ConnectedPort = dst;
        dst.ConnectedPort = src;
    }

    /// <summary>Wires the natural exit of <paramref name="from"/> (Start/Out) into the natural entry of <paramref name="to"/> (In).</summary>
    public void Chain(params GraphicObject[] icons)
    {
        for (var i = 0; i + 1 < icons.Length; i++)
        {
            var fromType = ((IconDraw)icons[i].DrawObject!).IconType == IconType.Start ? PortType.Start : PortType.Out;
            Wire(icons[i], fromType, icons[i + 1], PortType.In);
        }
    }

    public static Port Port(GraphicObject go, PortType type) => go.DrawObject!.Ports!.First(p => p.Type == type);

    public static ProjectData Project(ProjectType type, string name = "Prog", SoftwareType sw = SoftwareType.MRCU)
    {
        var info = new ProjectInfo { ProjectName = name, ProjectType = type, SoftwareType = sw };
        info.ReturnType!.Type = type is ProjectType.FlowApp or ProjectType.CodeApp ? "long" : "void";
        return new ProjectData { ProjectInfo = info };
    }
}
