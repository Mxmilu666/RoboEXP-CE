using System.Reflection;
using System.Xml.Linq;
using RoboExp.App.Localization;
using RoboExp.App.Services;

namespace RoboExp.App.ViewModels;

/// <summary>Help → About (original About form: product = robot type description, info tree with language/versions).</summary>
public sealed class AboutViewModel
{
    public AboutViewModel(AppEnvironment env)
    {
        var loc = Localizer.Instance;
        string? version = null, libVersion = null;
        if (env.Install != null)
        {
            try
            {
                var system = XDocument.Load(Path.Combine(env.Install.Root, "System")).Root;
                version = system?.Element("Version")?.Value;
                libVersion = system?.Element("LibVersion")?.Value;
            }
            catch (Exception e) when (e is IOException or System.Xml.XmlException) { }
        }
        Product = loc.T("Xwb:RobotExpress") + " " + env.RobotType.Name;
        var portVersion = typeof(AboutViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? typeof(AboutViewModel).Assembly.GetName().Version?.ToString() ?? "";
        Info =
        [
            loc.T("Xwb:AboutLanguage"),
            loc.T("Xwb:AboutSoftVerion").Trim() + " " + (version ?? "-"),
            loc.T("Xwb:AboutLibVerion").Trim() + " " + (libVersion ?? "-"),
            loc.T("Port:About.Port") + " " + portVersion,
            loc.T("Port:Vendor") + (env.Install?.Root ?? loc.T("Port:NotFound")),
        ];
        CopyRight = "@Mxmilu 2026";
        Link = "https://github.com/Mxmilu666/RoboEXP-CE";
    }

    public string Product { get; }
    public IReadOnlyList<string> Info { get; }
    public string CopyRight { get; }
    public string Link { get; }
}
