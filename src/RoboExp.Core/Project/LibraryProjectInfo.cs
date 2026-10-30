using System.Xml;
using RoboExp.Core.Install;
using RoboExp.Core.Library;
using RoboExp.Core.Model;

namespace RoboExp.Core.Project;

/// <summary>Library-side ProjectInfo/HardwareTable sources of the original DataManager/LibManager.</summary>
public static class LibraryProjectInfo
{
    /// <summary>
    /// LibManager.CreateLibProjInfo for one JMlib.xml function: a CodeFunc ProjectInfo whose ProjectPath is
    /// <c>&lt;Include&gt;/&lt;Name&gt;.rcu</c> and icon <c>Include/Icons/&lt;Name&gt;.png</c>.
    /// </summary>
    public static ProjectInfo FromLibFunction(LibFunction f, VendorInstall install, RobotType type, UiLanguage lang = UiLanguage.Default)
    {
        var includeDir = install.IncludeDirectory(type);
        var info = new ProjectInfo
        {
            SoftwareType = type.SoftwareType,
            OuterType = OuterType.Entity,
            ProjectType = ProjectType.CodeFunc,
            ProjectIcon = f.IconPath != null && File.Exists(f.IconPath) ? new EncodedImage(File.ReadAllBytes(f.IconPath)) : null,
            ProjectName = f.Name,
            ProjectPath = Path.Combine(includeDir, f.Name + ".rcu"),
            UserVersion = f.Version,
            UserName = f.Author,
            ProjectCaption = f.Caption.Get(lang),
            ProjectDescription = f.Description.Get(lang),
        };
        info.ReturnType!.Type = f.ReturnType;
        info.ReturnType.Description = f.ReturnDescription.Get(lang);
        foreach (var p in f.Params)
        {
            info.ParamList!.Add(new ParameterClass
            {
                Type = p.Type,
                Name = p.Name,
                IsHardwareType = p.IsHardwareType,
                Min = p.Min,
                Max = p.Max,
                DefaultValue = p.Default,
                Description = p.Description.Get(lang),
            });
        }
        return info;
    }

    public static string SystemHardwareTablePath(VendorInstall install, RobotType type) =>
        Path.Combine(install.RobotDirectory(type), "Resources", "SystemHardwareTable.xml");

    /// <summary>HardwareManager.GetHardwareTableFromXML; null when the type ships no table (all but X3RCU/X4RCU).</summary>
    public static HardwareTable? LoadSystemHardwareTable(VendorInstall install, RobotType type, UiLanguage lang = UiLanguage.Default)
    {
        var path = SystemHardwareTablePath(install, type);
        if (!File.Exists(path)) return null;
        var doc = new XmlDocument();
        doc.Load(path);
        var root = doc.DocumentElement!;
        var table = new HardwareTable();
        var children = root.ChildNodes.Cast<XmlNode>().ToList();
        if (children.FirstOrDefault(n => n.Name.Equals("SoftwareType", StringComparison.OrdinalIgnoreCase)) is { } st
            && Enum.TryParse<SoftwareType>(st.InnerText, out var sw) && Enum.IsDefined(sw))
            table.SoftwareType = sw;
        if (children.FirstOrDefault(n => n.Name.Equals("Description", StringComparison.OrdinalIgnoreCase)) is { } d)
            table.Description = d.InnerText;
        var types = children.FirstOrDefault(n => n.Name.Equals("HardwareType", StringComparison.OrdinalIgnoreCase));
        foreach (XmlNode node in (System.Collections.IEnumerable?)types?.ChildNodes ?? Array.Empty<XmlNode>())
        {
            if (node.NodeType != XmlNodeType.Element) continue;
            table.HardwareItemList!.Add(ItemFromNode(node, LangSuffix(lang)));
        }
        return table;
    }

    private static string LangSuffix(UiLanguage lang) => lang switch { UiLanguage.CHS => "CHS", UiLanguage.CHT => "CHT", _ => "Default" };

    private static HardwareItem ItemFromNode(XmlNode node, string lang)
    {
        string? Child(XmlNode n, string name) => n.SelectSingleNode(name)?.InnerText;
        var item = new HardwareItem();
        var t = item.HardwareType!;
        t.Name = node.Name;
        t.PinCount = int.Parse(Child(node, "PinCount") ?? "0");
        t.IsDefault = (Child(node, "IsDefault") ?? "").Trim() == "true";
        t.CtrlLimitList = IntList(Child(node, "CtrlLimit"));
        t.GNDLimitList = IntList(Child(node, "GNDLimit"));
        t.VccLimitList = IntList(Child(node, "VccLimit"));
        t.NickName = Child(node, "TypeNickName" + lang);
        t.Description = Child(node, "TypeDes" + lang);
        var pins = new ArrayList<string?>();
        for (var i = 0; i < t.PinCount; i++)
            if (Child(node, "PinDes" + lang + i) is { } pd) pins.Add(pd);
        t.PinDescriptionList = pins;
        var instances = new ArrayList<HardwareInstance>();
        var instNode = node.ChildNodes.Cast<XmlNode>().FirstOrDefault(n => n.Name.Trim().Equals("HardwareInstance", StringComparison.OrdinalIgnoreCase));
        foreach (XmlNode x in (System.Collections.IEnumerable?)instNode?.ChildNodes ?? Array.Empty<XmlNode>())
        {
            if (x.NodeType != XmlNodeType.Element) continue;
            instances.Add(new HardwareInstance
            {
                Name = x.Name,
                TypeName = Child(x, "TypeName"),
                TypeNum = int.Parse(Child(x, "TypeNum") ?? "0"),
                PinTeam = new PinTeam
                {
                    CtrlPinNumList = IntList(Child(x, "CtrlPin")),
                    GNDPinNumList = IntList(Child(x, "GNDPin")),
                    VccPinNumList = IntList(Child(x, "VccPin")),
                },
            });
        }
        item.InstanceList = instances;
        return item;
    }

    /// <summary>HardwareManager.ChangeList: comma separated ints.</summary>
    private static ArrayList<int> IntList(string? src)
    {
        var list = new ArrayList<int>();
        if (src == null) return list;
        foreach (var part in src.Split(','))
            if (part.Trim().Length > 0) list.Add(int.Parse(part));
        return list;
    }
}
