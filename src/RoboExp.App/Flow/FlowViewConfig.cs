using System.Xml.Linq;
using RoboExp.Core.Install;

namespace RoboExp.App.Flow;

/// <summary>
/// The flowchart settings of the original <c>ConfigInfo</c> (defaults from ConfigInfo.cs, overridden by
/// <c>Robots/&lt;TYPE&gt;/Resources/Config.xml</c> or <c>Robots/Default/Resources/Config.xml</c> like
/// FilePathInfo.ConfigFilePath). Colors are ARGB ints as stored.
/// </summary>
public sealed record FlowViewConfig
{
    public int BackColor { get; init; } = unchecked((int)0xFF8080C0);
    public int TextBoxColor { get; init; } = unchecked((int)0xFF00FF00);
    public int LineColor { get; init; } = unchecked((int)0xFFFF00FF);
    public bool ShowCtrlIconCode { get; init; } = true;
    public bool ShowCtrlIconCodeOnTip { get; init; }
    public bool ShowCtrlIconDescriptionOnTip { get; init; } = true;
    public int ShowFuncParamCount { get; init; } = 4;
    public bool ShowFuncCodeOnTip { get; init; } = true;
    public bool ShowFuncParamOnTip { get; init; }
    public bool ShowFuncDescriptionOnTip { get; init; } = true;
    public int MaxUndoSteps { get; init; } = 10;

    public static FlowViewConfig Default { get; } = new();

    public static FlowViewConfig Load(VendorInstall? install, RobotType type)
    {
        if (install == null) return Default;
        foreach (var robot in new[] { type.Name, "Default" })
        {
            var dir = VendorInstall.FindChildIgnoreCase(install.RobotsDirectory, robot);
            if (dir == null) continue;
            var resources = VendorInstall.FindChildIgnoreCase(dir, "Resources");
            var file = resources == null ? null : VendorInstall.FindChildIgnoreCase(resources, "Config.xml");
            if (file == null) continue;
            try
            {
                return Parse(XDocument.Load(file).Root);
            }
            catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException)
            {
                return Default;
            }
        }
        return Default;
    }

    internal static FlowViewConfig Parse(XElement? root)
    {
        if (root == null) return Default;
        int Int(string name, int fallback) => int.TryParse(root.Element(name)?.Value, out var v) ? v : fallback;
        bool Bool(string name, bool fallback) => bool.TryParse(root.Element(name)?.Value, out var v) ? v : fallback;
        var d = Default;
        return new FlowViewConfig
        {
            BackColor = Int("FlowViewBackColorValue", d.BackColor),
            TextBoxColor = Int("FlowViewTextBoxColorValue", d.TextBoxColor),
            LineColor = Int("FlowViewLineColorValue", d.LineColor),
            ShowCtrlIconCode = Bool("IsShowCtrlIconCode", d.ShowCtrlIconCode),
            ShowCtrlIconCodeOnTip = Bool("IsShowCtrlIconCodeOnTip", d.ShowCtrlIconCodeOnTip),
            ShowCtrlIconDescriptionOnTip = Bool("IsShowCtrlIconDescriptionOnTip", d.ShowCtrlIconDescriptionOnTip),
            ShowFuncParamCount = Int("ShowFuncParamCount", d.ShowFuncParamCount),
            ShowFuncCodeOnTip = Bool("IsShowFuncCodeOnTip", d.ShowFuncCodeOnTip),
            ShowFuncParamOnTip = Bool("IsShowFuncParamOnTip", d.ShowFuncParamOnTip),
            ShowFuncDescriptionOnTip = Bool("IsShowFuncDescriptionOnTip", d.ShowFuncDescriptionOnTip),
            MaxUndoSteps = Math.Clamp(Int("FlowViewMaxUndoSteps", d.MaxUndoSteps), 0, 20),
        };
    }
}
