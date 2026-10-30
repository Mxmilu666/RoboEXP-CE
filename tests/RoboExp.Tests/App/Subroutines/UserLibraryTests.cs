using RoboExp.Core.Install;
using RoboExp.Core.Library;
using RoboExp.Core.Model;
using RoboExp.Core.Rcu;

namespace RoboExp.Tests.App.Subroutines;

internal static class SubTestFiles
{
    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "roboexp-subs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static ProjectInfo Info(string name, string version = "1.0", params (string Type, string Name)[] ps)
    {
        var info = new ProjectInfo { ProjectName = name, ProjectCaption = name + " caption", UserVersion = version, ProjectType = ProjectType.CodeFunc };
        foreach (var (t, n) in ps) info.ParamList!.Add(new ParameterClass { Type = t, Name = n, DefaultValue = "0" });
        return info;
    }

    /// <summary>Writes &lt;dir&gt;/&lt;name&gt;.rcu (+ .c) holding a CodeFunc (or <paramref name="type"/>) project.</summary>
    public static string WriteFunc(string dir, ProjectInfo info, ProjectType type = ProjectType.CodeFunc, ProjectData? data = null)
    {
        data ??= new ProjectData();
        data.ProjectInfo = info;
        info.ProjectType = type;
        var file = new RcuFile { ProjectType = type, CodeData = new CodeData { ProjectData = data } };
        var path = Path.Combine(dir, info.ProjectName + ".rcu");
        RcuWriter.Write(path, file);
        File.WriteAllText(Path.Combine(dir, info.ProjectName + ".c"), "// " + info.ProjectName);
        return path;
    }
}

public class UserLibraryTests
{
    private static readonly RobotType E6 = RobotType.Parse("E6RCU");

    private static readonly PaletteBand[] SystemBands =
    [
        new(new LocalizedText("Performer", "执行模块", "執行模塊"), ["SetMotor", "SetLed"]),
        new(new LocalizedText("Sensor", "传感器", "傳感器"), ["GetLight"]),
    ];

    private static LibFunction? Find(string name) =>
        name is "SetMotor" or "SetLed" or "GetLight"
            ? new LibFunction(name, "1", "", LocalizedText.Empty, LocalizedText.Empty, "void", LocalizedText.Empty, [], null)
            : null;

    private static UserLibrary Create(string? xml, UiLanguage lang = UiLanguage.CHS) =>
        UserLibrary.Create(E6, SystemBands, Find, lang, xml);

    private static string[] Paths(UserBand band) => band.Functions.Select(f => f.Path).ToArray();

    [Fact]
    public void Without_user_file_the_tree_is_the_system_tree()
    {
        var lib = Create(null);
        Assert.Equal(["执行模块", "传感器"], lib.Bands.Select(lib.CaptionOf));
        Assert.Equal(["SetMotor", "SetLed"], Paths(lib.Bands[0]));
        Assert.All(lib.Bands.SelectMany(b => b.Functions), f => Assert.True(f.IsSystem && f.IsLoaded));
    }

    [Fact]
    public void User_file_order_and_flags_win_and_missing_system_functions_are_appended()
    {
        const string xml = """
            <!--RoboExp Include File Information-->
            <root>
              <Band Default="Mine" CHS="我的" CHT="我的" IsShow="true">
                <FuncName IsSystem="false" IsShow="true">/nowhere/Foo.rcu</FuncName>
                <FuncName IsSystem="true" IsShow="false">C:\RoboExp\Robots\E6RCU\Include\SetLed.rcu</FuncName>
              </Band>
              <Band Default="Performer" CHS="执行模块" CHT="執行模塊" IsShow="false">
                <FuncName IsSystem="true" IsShow="true">SetMotor</FuncName>
              </Band>
            </root>
            """;
        var lib = Create(xml);

        Assert.Equal(["我的", "执行模块", "传感器"], lib.Bands.Select(lib.CaptionOf));
        Assert.Equal(["/nowhere/Foo.rcu", "SetLed"], Paths(lib.Bands[0]));
        var setLed = lib.Bands[0].Functions[1];
        Assert.True(setLed.IsSystem);
        Assert.False(setLed.IsShown);
        Assert.False(lib.Bands[0].Functions[0].IsSystem);
        Assert.False(lib.Bands[0].Functions[0].IsLoaded);
        Assert.False(lib.Bands[1].IsShown);
        Assert.Equal(["SetMotor"], Paths(lib.Bands[1])); // SetLed already present elsewhere: not appended again
        Assert.Equal(["GetLight"], Paths(lib.Bands[2])); // missing band appended with its function
    }

    [Fact]
    public void Xml_round_trips()
    {
        var lib = Create(null);
        var mine = lib.AddBand("我的");
        lib.MoveUp(mine);
        lib.SetShown(lib.Bands[0].Functions[0], false);
        lib.SetShown(lib.Bands[2], false);

        var xml = lib.ToXml();
        Assert.StartsWith("<!--RoboExp Include File Information-->", xml);
        Assert.Contains("<Band Default=\"Performer\" CHS=\"执行模块\" CHT=\"執行模塊\" IsShow=\"true\">", xml);
        Assert.Contains("<Band Default=\"我的\" CHS=\"我的\" CHT=\"我的\" IsShow=\"true\" />", xml);
        Assert.Contains("<FuncName IsSystem=\"true\" IsShow=\"false\">SetMotor</FuncName>", xml);

        var again = Create(xml);
        Assert.Equal(xml, again.ToXml());
        Assert.Equal(lib.Bands.Select(lib.CaptionOf), again.Bands.Select(again.CaptionOf));
        Assert.Equal(lib.Bands.Select(b => b.IsShown), again.Bands.Select(b => b.IsShown));
    }

    [Fact]
    public void Functions_move_within_and_across_bands_like_LibManager()
    {
        var lib = Create(null);
        var performer = lib.Bands[0];
        var sensor = lib.Bands[1];
        var setLed = performer.Functions[1];
        var getLight = sensor.Functions[0];

        Assert.True(lib.MoveUp(setLed));
        Assert.Equal(["SetLed", "SetMotor"], Paths(performer));
        Assert.True(lib.MoveUp(getLight)); // top of a band → end of the previous band
        Assert.Equal(["SetLed", "SetMotor", "GetLight"], Paths(performer));
        Assert.Empty(sensor.Functions);
        Assert.True(lib.MoveDown(getLight)); // bottom of a band → start of the next band
        Assert.Equal(["GetLight"], Paths(sensor));
        Assert.False(lib.MoveDown(getLight)); // last band, last entry
        Assert.False(lib.MoveUp(performer));
        Assert.True(lib.MoveDown(performer));
        Assert.Equal(["传感器", "执行模块"], lib.Bands.Select(lib.CaptionOf));
    }

    [Fact]
    public void System_entries_and_bands_holding_them_cannot_be_removed()
    {
        var lib = Create(null);
        Assert.False(lib.CanRemoveBand(lib.Bands[0]));
        Assert.False(lib.RemoveFunction(lib.Bands[0].Functions[0]));
        var mine = lib.AddBand("Mine");
        Assert.Same(mine, lib.AddBand("MINE")); // case-insensitive existing band
        Assert.True(lib.RemoveBand(mine));
        Assert.Equal(2, lib.Bands.Count);
    }

    [Fact]
    public void AddFunction_loads_rcu_and_reports_original_errors()
    {
        var dir = SubTestFiles.TempDir();
        try
        {
            var lib = Create(null);
            var band = lib.AddBand("Mine");
            var changes = 0;
            lib.Changed += (_, _) => changes++;

            var sub = SubTestFiles.WriteFunc(dir, SubTestFiles.Info("MySub"));
            var node = lib.AddFunction(band, sub, out var err);
            Assert.Null(err);
            Assert.NotNull(node);
            Assert.Equal("MySub", node.Name);
            Assert.Equal(Path.GetFullPath(sub), node.Path);
            Assert.Same(node.ProjectInfo, lib.GetProjectInfo("MySub"));
            Assert.Equal(1, changes);

            Assert.Same(node, lib.AddFunction(band, sub, out err)); // same path
            Assert.Null(err);

            var other = SubTestFiles.TempDir();
            var dup = SubTestFiles.WriteFunc(other, SubTestFiles.Info("MySub"));
            Assert.Same(node, lib.AddFunction(band, dup, out err));
            Assert.Equal(UserLibrary.ErrorNameExists, err);

            var app = SubTestFiles.WriteFunc(dir, SubTestFiles.Info("App"), ProjectType.CodeApp);
            Assert.Null(lib.AddFunction(band, app, out err));
            Assert.Equal(UserLibrary.ErrorNotSubroutine, err);

            var junk = Path.Combine(dir, "junk.rcu");
            File.WriteAllText(junk, "not nrbf");
            Assert.Null(lib.AddFunction(band, junk, out err));
            Assert.Equal(UserLibrary.ErrorOpenFailed, err);

            Assert.Single(band.Functions);
            Directory.Delete(other, true);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Clone_is_independent_and_reset_restores_system_tree()
    {
        var lib = Create(null);
        var copy = lib.Clone();
        copy.AddBand("X");
        Assert.Equal(2, lib.Bands.Count);
        Assert.Equal(3, copy.Bands.Count);
        copy.ResetSystemDefault();
        Assert.Equal(lib.ToXml(), copy.ToXml());
    }
}
