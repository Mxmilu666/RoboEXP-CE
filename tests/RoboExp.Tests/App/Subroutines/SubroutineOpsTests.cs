using RoboExp.App.Subroutines;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;
using RoboExp.Core.Rcu;

namespace RoboExp.Tests.App.Subroutines;

public class SubroutineOpsTests
{
    private static bool IsSystem(string name) => name.StartsWith("Sys", StringComparison.Ordinal);

    private static ProjectData DataWith(params string[] names)
    {
        var data = new ProjectData();
        foreach (var n in names) data.UsedProjectInfoList!.Add(SubTestFiles.Info(n));
        return data;
    }

    private static string[] Names(ProjectData data) => data.UsedProjectInfoList!.Select(p => p.ProjectName!).ToArray();

    [Fact]
    public void Private_subs_skip_system_functions_and_keep_list_indexes()
    {
        var subs = SubroutineOps.PrivateSubs(DataWith("SysA", "B", "SysC", "D"), IsSystem);
        Assert.Equal([(1, "B"), (3, "D")], subs.Select(s => (s.ListIndex, s.Info.ProjectName!)));
    }

    [Fact]
    public void Move_passes_the_visible_neighbour_like_SubManagerDlg()
    {
        var data = DataWith("SysA", "B", "SysC", "D", "E");
        var subs = SubroutineOps.PrivateSubs(data, IsSystem);
        Assert.False(SubroutineOps.Move(data, subs, 0, -1));
        Assert.True(SubroutineOps.Move(data, subs, 1, -1)); // D up past B
        Assert.Equal(["SysA", "D", "B", "SysC", "E"], Names(data));

        data = DataWith("SysA", "B", "SysC", "D", "E");
        subs = SubroutineOps.PrivateSubs(data, IsSystem);
        Assert.True(SubroutineOps.Move(data, subs, 0, +1)); // B down past D
        Assert.Equal(["SysA", "SysC", "D", "B", "E"], Names(data));
        subs = SubroutineOps.PrivateSubs(data, IsSystem);
        Assert.False(SubroutineOps.Move(data, subs, 2, +1));
    }

    [Fact]
    public void Remove_drops_first_entry_by_exact_name()
    {
        var data = DataWith("B", "b", "B");
        Assert.True(SubroutineOps.Remove(data, "B"));
        Assert.Equal(["b", "B"], Names(data));
        Assert.False(SubroutineOps.Remove(data, "X"));
    }

    private static RcuFile FlowWithCall(string funcName, string code, params string?[] values)
    {
        var func = new FuncData { ProjectName = funcName, Code = code, ReturnName = "r" };
        foreach (var v in values) func.ValueList!.Add(v);
        var objects = new GraphicObjectCollection();
        objects.List!.Add(new GraphicObject { DataObject = func, DrawObject = new IconDraw() });
        return new RcuFile
        {
            ProjectType = ProjectType.FlowApp,
            FlowData = new FlowData { ProjectData = new ProjectData(), DesignSurfaceData = new DesignSurfaceData { DrawingObjects = objects } },
        };
    }

    [Fact]
    public void Used_check_finds_function_icons()
    {
        var file = FlowWithCall("MySub", "MySub(1)", "1");
        Assert.True(SubroutineOps.IsUsedInFlow(file, "MySub"));
        Assert.False(SubroutineOps.IsUsedInFlow(file, "mysub"));
    }

    [Fact]
    public void Flow_icons_follow_a_changed_signature()
    {
        var file = FlowWithCall("MySub", "MySub( a , b )", "a", "b");
        var oldInfo = SubTestFiles.Info("MySub", "1.0", ("int", "x"), ("int", "y"));
        var newInfo = SubTestFiles.Info("MySub", "1.1", ("int", "x"), ("int", "y"), ("char", "z"));
        newInfo.ReturnType!.Type = "int";
        newInfo.ProjectIcon = new EncodedImage([1, 2, 3]);

        SubroutineOps.UpdateUsedModuleInfoInFlow(file, oldInfo, newInfo);

        var g = SubroutineOps.DrawingObjects(file).Single();
        var f = (FuncData)g.DataObject!;
        Assert.Equal("", f.ReturnName);
        Assert.Equal(new string?[] { "a", "b", "0" }, f.ValueList!.ToArray());
        Assert.Same(newInfo.ProjectIcon, ((IconDraw)g.DrawObject!).Image);
    }

    [Theory]
    [InlineData("F(1, x+2 ,3)", new[] { "1", "x+2", "3" })]
    [InlineData("F()", new string[0])]
    [InlineData("(1)", new string[0])]
    [InlineData("F)(", new string[0])]
    public void Call_arguments_are_taken_between_first_parentheses(string code, string[] expected) =>
        Assert.Equal(expected, SubroutineOps.CallArguments(code));

    [Fact]
    public void Need_update_compares_library_signature()
    {
        var data = DataWith("SysA", "B", "C", "D");
        var libB = SubTestFiles.Info("B", "2.0");
        var libC = SubTestFiles.Info("C");
        var updates = SubroutineOps.NeedUpdate(data, n => n switch { "B" => libB, "C" => libC, "SysA" => SubTestFiles.Info("SysA", "9"), _ => null }, IsSystem);
        var u = Assert.Single(updates);
        Assert.Same(libB, u.Library);
        Assert.Same(data.UsedProjectInfoList![1], u.Project);
    }

    [Fact]
    public void Apply_update_copies_files_with_nested_subs_and_replaces_entry()
    {
        var root = SubTestFiles.TempDir();
        try
        {
            var libDir = Path.Combine(root, "lib");
            var projDir = Path.Combine(root, "proj");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(projDir);
            SubTestFiles.WriteFunc(libDir, SubTestFiles.Info("Inner"));
            var libData = new ProjectData();
            libData.UsedProjectInfoList!.Add(SubTestFiles.Info("Inner"));
            libData.UsedProjectInfoList.Add(SubTestFiles.Info("SysX"));
            var libPath = SubTestFiles.WriteFunc(libDir, SubTestFiles.Info("MySub", "2.0", ("int", "x")), data: libData);
            var lib = Core.Library.UserLibrary.LoadFuncProjectInfo(libPath, out _)!;

            var project = FlowWithCall("MySub", "MySub()");
            var old = SubTestFiles.Info("MySub", "1.0");
            project.FlowData!.ProjectData!.UsedProjectInfoList!.Add(old);
            var projectRcu = Path.Combine(projDir, "Main.rcu");

            var missing = SubroutineOps.ApplyUpdate(project, projectRcu, new SubUpdate(lib, old), IsSystem);

            Assert.Null(missing);
            Assert.True(File.Exists(Path.Combine(projDir, "MySub.rcu")));
            Assert.True(File.Exists(Path.Combine(projDir, "MySub.c")));
            Assert.True(File.Exists(Path.Combine(projDir, "Inner.rcu")));
            var entry = project.ProjectData!.UsedProjectInfoList!.Single();
            Assert.Equal("2.0", entry.UserVersion);
            Assert.Equal(Path.GetFullPath(Path.Combine(projDir, "MySub.rcu")), entry.ProjectPath);
            Assert.Equal(new string?[] { "0" }, ((FuncData)SubroutineOps.DrawingObjects(project).Single().DataObject!).ValueList!.ToArray());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Copy_reports_missing_source_file()
    {
        var root = SubTestFiles.TempDir();
        try
        {
            var src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            SubTestFiles.WriteFunc(src, SubTestFiles.Info("A"));
            File.Delete(Path.Combine(src, "A.c"));
            Assert.Equal("A.c", SubroutineOps.CopySubFiles("A", src, Path.Combine(root, "dst"), IsSystem));
            Assert.Equal("B.rcu", SubroutineOps.CopySubFiles("B", src, Path.Combine(root, "dst"), IsSystem));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Rename_sets_caption_in_entry_and_sub_file()
    {
        var dir = SubTestFiles.TempDir();
        try
        {
            SubTestFiles.WriteFunc(dir, SubTestFiles.Info("MySub"));
            var entry = SubTestFiles.Info("MySub");
            entry.ProjectPath = @"C:\Old\Place\MySub.rcu"; // vendor-style Windows path: only the file name counts
            SubroutineOps.RenameCaption(entry, "New caption", Path.Combine(dir, "Main.rcu"));

            Assert.Equal("New caption", entry.ProjectCaption);
            Assert.Equal("New caption", RcuReader.Read(Path.Combine(dir, "MySub.rcu")).ProjectData!.ProjectInfo!.ProjectCaption);
        }
        finally { Directory.Delete(dir, true); }
    }
}
