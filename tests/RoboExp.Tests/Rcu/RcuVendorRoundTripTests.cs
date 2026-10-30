using RoboExp.Core.Install;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;
using RoboExp.Core.Rcu;

namespace RoboExp.Tests.Rcu;

/// <summary>Read -> write against the original's own files (vendor/app, all RoboExp versions 1.0 .. 6.0.8).</summary>
public class RcuVendorRoundTripTests
{
    private const int SamplesPerType = 4;

    /// <summary>Up to <see cref="SamplesPerType"/> evenly spaced projects of every supported controller.</summary>
    public static IEnumerable<object[]> SampleProjects()
    {
        if (RcuVendor.Install is not { } install) yield break;
        foreach (var type in RobotType.Supported)
        {
            var dir = install.RobotDirectory(type);
            if (!Directory.Exists(dir)) continue;
            var files = RcuVendor.ProjectFiles(dir).ToList();
            if (files.Count == 0) continue;
            var step = Math.Max(1, files.Count / SamplesPerType);
            for (var i = 0; i < files.Count && i / step < SamplesPerType; i += step)
                yield return [type.Name, Path.GetRelativePath(install.Root, files[i])];
        }
    }

    [RcuVendorTheory]
    [MemberData(nameof(SampleProjects))]
    public void Sample_project_round_trips_byte_identically(string robotType, string relativePath)
    {
        Assert.StartsWith(Path.Combine("Robots", robotType), relativePath);
        var original = File.ReadAllBytes(RcuVendor.FullPath(relativePath));
        var file = RcuReader.Read(new MemoryStream(original));
        var written = new MemoryStream();
        RcuWriter.Write(written, file);
        Assert.Null(NrbfGraphComparer.FindFirstDifference(original, written.ToArray()));
        Assert.Equal(original, written.ToArray());
    }

    [RcuVendorFact]
    public void Every_vendor_project_reads_and_round_trips_graph_equivalently()
    {
        var failures = new List<string>();
        var files = RcuVendor.ProjectFiles(RcuVendor.Install!.Root).ToList();
        var identical = 0;
        Parallel.ForEach(files, path =>
        {
            try
            {
                var original = File.ReadAllBytes(path);
                var written = new MemoryStream();
                RcuWriter.Write(written, RcuReader.Read(new MemoryStream(original)));
                var bytes = written.ToArray();
                if (bytes.AsSpan().SequenceEqual(original))
                {
                    Interlocked.Increment(ref identical);
                    return;
                }
                if (NrbfGraphComparer.FindFirstDifference(original, bytes) is { } diff)
                    lock (failures) failures.Add($"{path}: {diff}");
            }
            catch (Exception ex)
            {
                lock (failures) failures.Add($"{path}: {ex.GetType().Name}: {ex.Message}");
            }
        });
        Assert.True(files.Count > 0, "no vendor .rcu files found");
        Assert.Empty(failures);
        // Two .bak files hold a second distinct "" inside a string[] (not observable through NrbfDecoder): equivalent, not identical.
        Assert.True(identical >= files.Count - 2, $"only {identical}/{files.Count} byte-identical");
    }

    [RcuVendorFact]
    public void Serialized_event_handlers_are_kept_and_bound_to_the_project_data()
    {
        var file = RcuReader.Read(RcuVendor.FullPath(Path.Combine("External Tools", "other", "URM37_Test", "URM37_Test.rcu")));
        var data = file.ProjectData!;
        var changed = data.VarList!.Changed;
        Assert.NotNull(changed);
        Assert.All(changed.MethodNames, name => Assert.Equal("VarListChangedHandle", name));
        Assert.Equal(changed.Members.Count(m => m.Key.StartsWith("target", StringComparison.Ordinal)), changed.MethodNames.Count());
        foreach (var (name, value) in changed.Members.Where(m => m.Key.StartsWith("target", StringComparison.Ordinal)))
            Assert.Same(data, value);
        var method = Assert.IsType<SerializedMemberInfo>(changed.Members.First(m => m.Key == "method0").Value);
        Assert.Equal("RoboExp.ProjectData", method.ClassName);
        Assert.Contains("UsedProjectInfoListChangedHandle", data.UsedProjectInfoList!.Changed!.MethodNames);
    }

    [RcuVendorFact]
    public void Legacy_parameter_layout_is_read_with_defaults_and_kept()
    {
        var path = RcuVendor.FullPath(Path.Combine("Robots", "X4RCU", "Examples", "Other", "Acceleration", "Acceleration.rcu"));
        var file = RcuReader.Read(path);
        var legacy = file.ProjectData!.UsedProjectInfoList!
            .SelectMany(p => p.ParamList ?? [])
            .Where(p => p.SerializedMemberOrder != null)
            .ToList();
        Assert.NotEmpty(legacy);
        Assert.All(legacy, p =>
        {
            Assert.Equal(100, p.Version);
            Assert.DoesNotContain("m_NameCHS", p.SerializedMemberOrder!);
            Assert.Equal("", p.NameCHS);
        });
        var written = new MemoryStream();
        RcuWriter.Write(written, file);
        Assert.Equal(File.ReadAllBytes(path), written.ToArray());
    }

    [RcuVendorFact]
    public void Flowchart_model_is_navigable()
    {
        var file = RcuReader.Read(RcuVendor.FullPath(Path.Combine("External Tools", "other", "URM37_Test", "URM37_Test.rcu")));
        Assert.Equal(ProjectType.FlowApp, file.ProjectType);
        Assert.StartsWith("RoboExp, Version=5.2.", file.Assemblies.RoboExp);
        var objects = file.FlowData!.DesignSurfaceData!.DrawingObjects!.List!;
        var start = Assert.Single(objects, o => o.DrawObject is IconDraw { IconType: IconType.Start });
        Assert.Same(start, start.DrawObject!.Parent);
        Assert.All(objects.Where(o => o.DrawObject is LineDraw), o => Assert.IsType<LineCode>(o.CodeObject));
        var calls = objects.Select(o => o.DataObject).OfType<FuncData>().Select(f => f.ProjectName).ToList();
        Assert.Contains("GetURM37", calls);
        Assert.Contains("int main(void)", file.ProjectData!.CodeFrameData!.AllCode);
    }
}
