using System.Formats.Nrbf;
using System.Text;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;
using RoboExp.Core.Rcu;

namespace RoboExp.Tests.Rcu;

public class GraphicObjectClipboardEncodingTests
{
    private const string CollectionType = "RoboExp.GraphicObjects.GraphicObjectCollection";

    private static GraphicObjectCollection Sample() => SampleProject.Create().FlowData!.DesignSurfaceData!.DrawingObjects!;

    private static byte[] Write(GraphicObjectCollection objects, RcuAssemblyNames? assemblies = null)
    {
        using var stream = new MemoryStream();
        RcuWriter.WriteGraphicObjects(stream, objects, assemblies);
        Assert.True(stream.CanWrite);
        return stream.ToArray();
    }

    private static GraphicObjectCollection Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var objects = RcuReader.ReadGraphicObjects(stream);
        Assert.True(stream.CanRead);
        return objects;
    }

    [Fact]
    public void Collection_is_the_bare_root_with_original_member_schema_and_assembly_identities()
    {
        var bytes = Write(Sample());
        Assert.Equal(new byte[] { 0, 1, 0, 0, 0, 255, 255, 255, 255, 1, 0, 0, 0, 0, 0, 0, 0 }, bytes[..17]);
        Assert.Equal(11, bytes[^1]);
        var root = Assert.IsAssignableFrom<ClassRecord>(NrbfDecoder.Decode(new MemoryStream(bytes), out var records));
        Assert.Equal(CollectionType, root.TypeName.FullName);
        Assert.Equal(RcuAssemblyNames.Default.RoboExp, root.TypeName.AssemblyName!.FullName);
        Assert.Equal(["m_Version", "List"], root.MemberNames);
        Assert.Equal(100, root.GetInt32("m_Version"));
        Assert.DoesNotContain(records.Values.OfType<ClassRecord>(), record => record.TypeName.FullName == RcuReader.RootTypeName);

        var list = root.GetClassRecord("List")!;
        Assert.Equal("System.Collections.ArrayList", list.TypeName.FullName);
        Assert.Equal(["_items", "_size", "_version"], list.MemberNames);
        Assert.Equal(3, list.GetInt32("_size"));
        var items = ((SZArrayRecord<SerializationRecord>)list.GetArrayRecord("_items")!).GetArray();
        var graphic = Assert.IsAssignableFrom<ClassRecord>(items[0]);
        Assert.Equal("RoboExp.GraphicObjects.GraphicObject", graphic.TypeName.FullName);
        Assert.Equal(["m_GraphicObjectVersion", "m_DrawObject", "m_DataObject", "m_CodeObject"], graphic.MemberNames);
        var drawing = graphic.GetClassRecord("m_DrawObject")!;
        Assert.Equal(RcuAssemblyNames.Default.SystemDrawing, drawing.GetClassRecord("m_Position")!.TypeName.AssemblyName!.FullName);
        Assert.Same(graphic, drawing.GetClassRecord("m_Parent"));
    }

    [Fact]
    public void Graph_cycles_shared_images_duplicate_objects_and_list_metadata_survive()
    {
        var source = Sample();
        source.List!.Add(source.List[0]);
        source.List.Capacity = 12;
        source.List.InternalVersion = 27;
        var bytes = Write(source);
        var copy = Read(bytes);
        var list = copy.List!;
        Assert.Equal(4, list.Count);
        Assert.Equal(12, list.Capacity);
        Assert.Equal(27, list.InternalVersion);
        Assert.Same(list[0], list[3]);
        var start = Assert.IsType<IconDraw>(list[0].DrawObject);
        var line = Assert.IsType<LineDraw>(list[1].DrawObject);
        var call = Assert.IsType<IconDraw>(list[2].DrawObject);
        Assert.Same(list[0], start.Parent);
        Assert.Same(list[1], line.Parent);
        Assert.Same(list[2], call.Parent);
        Assert.Same(start, start.Ports![0].Parent);
        Assert.Same(line, line.Ports![0].Parent);
        Assert.Same(line.Ports[0], start.Ports[0].ConnectedPort);
        Assert.Same(start.Ports[0], line.Ports[0].ConnectedPort);
        Assert.Same(call.Ports![0], line.Ports[1].ConnectedPort);
        Assert.Same(line.Ports[1], call.Ports[0].ConnectedPort);
        Assert.Same(start.Image, call.Image);
        Assert.Equal(SampleProject.Png, start.Image!.Data);
        Assert.IsType<LineCode>(list[1].CodeObject);
        Assert.Equal(["0", "0", "var1"], Assert.IsType<FuncData>(list[2].DataObject).ValueList!);
        Assert.Equal(bytes, Write(copy));
    }

    [Fact]
    public void Explicit_original_assembly_profile_is_used()
    {
        var names = RcuAssemblyNames.Default with
        {
            RoboExp = "RoboExp, Version=5.2.0.0, Culture=neutral, PublicKeyToken=null",
            SystemDrawing = "System.Drawing, Version=1.0.5000.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
        };
        var root = (ClassRecord)NrbfDecoder.Decode(new MemoryStream(Write(Sample(), names)), out var records);
        Assert.Equal(names.RoboExp, root.TypeName.AssemblyName!.FullName);
        var color = Assert.Single(records.Values.OfType<ClassRecord>(), record =>
            record.TypeName.FullName == "RoboExp.GraphicObjects.LineDraw").GetClassRecord("m_LineColor")!;
        Assert.Equal(names.SystemDrawing, color.TypeName.AssemblyName!.FullName);
        Assert.Equal(["value", "knownColor", "state", "name"], color.MemberNames);
    }

    [Fact]
    public void Empty_collection_is_valid_and_project_and_collection_roots_are_not_interchangeable()
    {
        Assert.Empty(Read(Write(new GraphicObjectCollection())).List!);
        using var project = new MemoryStream();
        RcuWriter.Write(project, SampleProject.Create());
        project.Position = 0;
        Assert.Throws<InvalidDataException>(() => RcuReader.ReadGraphicObjects(project));
        Assert.Throws<InvalidDataException>(() => RcuReader.Read(new MemoryStream(Write(Sample()))));
    }

    [Fact]
    public void Unknown_and_primitive_roots_are_rejected_before_graph_conversion()
    {
        var unknownRoot = Write(new GraphicObjectCollection());
        var name = Encoding.UTF8.GetBytes(CollectionType);
        var index = unknownRoot.AsSpan().IndexOf(name);
        Assert.True(index >= 0);
        unknownRoot[index] = (byte)'X';
        var error = Assert.Throws<InvalidDataException>(() => Read(unknownRoot));
        Assert.Contains("expected " + CollectionType, error.Message);

        // SerializedStreamHeader, BinaryObjectString (id 1), "x", MessageEnd.
        byte[] stringRoot = [0, 1, 0, 0, 0, 255, 255, 255, 255, 1, 0, 0, 0, 0, 0, 0, 0, 6, 1, 0, 0, 0, 1, (byte)'x', 11];
        Assert.Throws<InvalidDataException>(() => Read(stringRoot));
    }

    [Fact]
    public void Truncated_and_non_Nrbf_payloads_are_rejected()
    {
        var bytes = Write(Sample());
        foreach (var length in new[] { 0, 1, 16, bytes.Length / 2, bytes.Length - 1 })
            Assert.Throws<InvalidDataException>(() => Read(bytes[..length]));
        Assert.Throws<InvalidDataException>(() => Read(Encoding.UTF8.GetBytes("not a clipboard collection")));
    }

    [RcuVendorFact]
    public void Reads_original_BinaryFormatter_records_with_collection_as_stream_root()
    {
        // The installed .rcu is an actual legacy BinaryFormatter payload. Change only the header's root ID,
        // retaining its collection and every original record unchanged; no CE writer generates this input.
        var path = RcuVendor.ProjectFiles(RcuVendor.Install!.Root).First(file =>
            file.EndsWith(Path.Combine("DelayTest", "DelayTest.rcu"), StringComparison.Ordinal));
        var bytes = File.ReadAllBytes(path);
        var projectRoot = (ClassRecord)NrbfDecoder.Decode(new MemoryStream(bytes));
        var original = projectRoot.GetClassRecord("m_FlowData")!.GetClassRecord("m_DesignSurfaceData")!
            .GetClassRecord("m_DrawingObjects")!;
        var typeName = Encoding.UTF8.GetBytes(CollectionType);
        var index = bytes.AsSpan().IndexOf(typeName);
        while (index >= 0 && (index < 6 || bytes[index - 6] != 5))
        {
            var next = bytes.AsSpan(index + typeName.Length).IndexOf(typeName);
            index = next < 0 ? -1 : index + typeName.Length + next;
        }
        Assert.True(index >= 6);
        Assert.Equal(typeName.Length, bytes[index - 1]); // single-byte NRBF string length
        Assert.Equal(5, bytes[index - 6]); // ClassWithMembersAndTypes, followed by object ID
        bytes.AsSpan(index - 5, 4).CopyTo(bytes.AsSpan(1, 4));
        var rerooted = (ClassRecord)NrbfDecoder.Decode(new MemoryStream(bytes));
        Assert.Equal(original.Id, rerooted.Id);
        Assert.Equal(CollectionType, rerooted.TypeName.FullName);
        var objects = Read(bytes);
        Assert.NotEmpty(objects.List!);
        Assert.Equal(original.GetClassRecord("List")!.GetInt32("_size"), objects.List!.Count);
        Assert.All(objects.List, graphic =>
        {
            if (graphic.DrawObject is not { } drawing) return;
            Assert.Same(graphic, drawing.Parent);
            Assert.All(drawing.Ports ?? [], port =>
            {
                Assert.Same(drawing, port.Parent);
                if (port.ConnectedPort is { } connected)
                    Assert.Contains(connected, connected.Parent!.Ports!);
            });
        });
        Assert.Equal(objects.List.Count, Read(Write(objects)).List!.Count);
    }
}
