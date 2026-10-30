using RoboExp.Core.Install;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;

namespace RoboExp.Tests.Rcu;

/// <summary>Skipped when no RoboEXP vendor install can be located (vendor/app is gitignored).</summary>
public sealed class RcuVendorFactAttribute : FactAttribute
{
    public RcuVendorFactAttribute()
    {
        if (RcuVendor.Install == null) Skip = RcuVendor.MissingMessage;
    }
}

/// <summary>Theory variant of <see cref="RcuVendorFactAttribute"/>.</summary>
public sealed class RcuVendorTheoryAttribute : TheoryAttribute
{
    public RcuVendorTheoryAttribute()
    {
        if (RcuVendor.Install == null) Skip = RcuVendor.MissingMessage;
    }
}

public static class RcuVendor
{
    public const string MissingMessage = "RoboEXP vendor install not found (vendor/app or ROBOEXP_HOME); vendor .rcu round-trip tests skipped";

    private static readonly Lazy<VendorInstall?> Lazy = new(() =>
    {
        try { return VendorInstall.Locate(); }
        catch (DirectoryNotFoundException) { return null; }
    });

    public static VendorInstall? Install => Lazy.Value;

    /// <summary>All files under the install that are .rcu, or .bak in .rcu format (sorted, relative to the root).</summary>
    public static IEnumerable<string> ProjectFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".rcu", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            .Where(f => System.Formats.Nrbf.NrbfDecoder.StartsWithPayloadHeader(File.ReadAllBytes(f)))
            .Order(StringComparer.Ordinal);

    public static string FullPath(string relative) => Path.Combine(Install!.Root, relative);
}

/// <summary>Builds a small flowchart project the way the original's "new project" + editor would.</summary>
public static class SampleProject
{
    /// <summary>A real 1x1 PNG.</summary>
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");

    public static RcuFile Create()
    {
        var icon = new EncodedImage(Png);
        var data = new ProjectData
        {
            ProjectInfo = new ProjectInfo
            {
                UserName = "tester",
                ProjectName = "Demo",
                ProjectCaption = "演示",
                ProjectPath = @"C:\RoboExp\Demo\Demo.rcu",
                ProjectIcon = icon,
                SoftwareType = SoftwareType.E6RCU,
                ProjectType = ProjectType.FlowApp,
            },
        };
        data.HardwareTable!.SoftwareType = SoftwareType.E6RCU;
        data.VarList!.Add(new VarStruct { Type = "int", Name = "var1", DefaultValue = "0" });
        data.CodeFrameData!.AllCode = "int main(void)\r\n{\r\n    E6RCU_Init();\r\n    // 中文注释\r\n}\r\n";

        var start = new GraphicObject();
        var startDraw = new IconDraw { Parent = start, Position = new Point(84, 32), Size = new Size(32, 32), Image = icon, IconType = IconType.Start };
        start.DrawObject = startDraw;
        var startOut = new Port { Parent = startDraw, Type = PortType.Start, PortDirection = PortDirection.BottomLeft };
        startDraw.Ports!.Add(startOut);

        var line = new GraphicObject { CodeObject = new LineCode() };
        var lineDraw = new LineDraw { Parent = line, Position = new Point(100, 64), EndPosition = new Point(100, 120) };
        line.DrawObject = lineDraw;
        var lineStart = new Port { Parent = lineDraw, Type = PortType.Out, ConnectedPort = startOut };
        var lineEnd = new Port { Parent = lineDraw, Type = PortType.In };
        startOut.ConnectedPort = lineStart;
        lineDraw.Ports!.Add(lineStart);
        lineDraw.Ports.Add(lineEnd);

        var call = new GraphicObject { DataObject = new FuncData { ProjectName = "SetLCD5Char", ValueList = new ArrayList<string?> { "0", "0", "var1" } } };
        var callDraw = new IconDraw { Parent = call, Position = new Point(84, 120), Size = new Size(32, 32), Image = icon, IconType = IconType.Function };
        call.DrawObject = callDraw;
        var callIn = new Port { Parent = callDraw, Type = PortType.In, ConnectedPort = lineEnd };
        lineEnd.ConnectedPort = callIn;
        callDraw.Ports!.Add(callIn);

        var surface = new DesignSurfaceData { ScrollOffset = new Point(0, -131), DrawingObjects = new GraphicObjectCollection() };
        surface.DrawingObjects.List!.Add(start);
        surface.DrawingObjects.List.Add(line);
        surface.DrawingObjects.List.Add(call);

        return new RcuFile
        {
            ProjectType = ProjectType.FlowApp,
            FlowData = new FlowData { ProjectData = data, DesignSurfaceData = surface },
        };
    }
}
