using System.Formats.Nrbf;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;
using RoboExp.Core.Rcu;

namespace RoboExp.Tests.Rcu;

/// <summary>On-disk shape of files written by <see cref="RcuWriter"/> (what the original BinaryFormatter must read).</summary>
public class RcuEncodingTests
{
    private static byte[] Write(RcuFile file)
    {
        var ms = new MemoryStream();
        RcuWriter.Write(ms, file);
        return ms.ToArray();
    }

    private static ClassRecord Decode(byte[] bytes) => (ClassRecord)NrbfDecoder.Decode(new MemoryStream(bytes));

    private static ClassRecord ProjectDataRecord(ClassRecord root) => root.GetClassRecord("m_FlowData")!.GetClassRecord("m_ProjectData")!;

    private static ClassRecord FirstIconDraw(ClassRecord root)
    {
        var list = root.GetClassRecord("m_FlowData")!.GetClassRecord("m_DesignSurfaceData")!
            .GetClassRecord("m_DrawingObjects")!.GetClassRecord("List")!;
        var items = ((SZArrayRecord<SerializationRecord>)list.GetArrayRecord("_items")!).GetArray();
        return ((ClassRecord)items[0]!).GetClassRecord("m_DrawObject")!;
    }

    private static ClassRecord LineDrawRecord(ClassRecord root)
    {
        var list = root.GetClassRecord("m_FlowData")!.GetClassRecord("m_DesignSurfaceData")!
            .GetClassRecord("m_DrawingObjects")!.GetClassRecord("List")!;
        var items = ((SZArrayRecord<SerializationRecord>)list.GetArrayRecord("_items")!).GetArray();
        return ((ClassRecord)items[1]!).GetClassRecord("m_DrawObject")!;
    }

    [Fact]
    public void Stream_starts_with_BinaryFormatter_header_and_RoboExp_library()
    {
        var bytes = Write(SampleProject.Create());
        // SerializedStreamHeader: RecordType 0, RootId 1, HeaderId -1, version 1.0
        Assert.Equal(new byte[] { 0, 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 1, 0, 0, 0, 0, 0, 0, 0 }, bytes[..17]);
        // BinaryLibrary id 2 = RoboExp assembly, then the root class record (id 1) of RoboExp.RCUFile
        Assert.Equal(12, bytes[17]);
        Assert.Equal(2, BitConverter.ToInt32(bytes, 18));
        Assert.Equal(11, bytes[^1]); // MessageEnd

        var root = Decode(bytes);
        Assert.Equal("RoboExp.RCUFile", root.TypeName.FullName);
        Assert.Equal(RcuAssemblyNames.Default.RoboExp, root.TypeName.AssemblyName!.FullName);
        Assert.Equal(["m_Version", "m_ProjectType", "m_FlowData", "m_CodeData"], root.MemberNames);
    }

    [Fact]
    public void Point_and_Size_are_inline_System_Drawing_structs_with_int32_fields()
    {
        var draw = FirstIconDraw(Decode(Write(SampleProject.Create())));
        var position = draw.GetClassRecord("m_Position")!;
        Assert.Equal("System.Drawing.Point", position.TypeName.FullName);
        Assert.Equal(RcuAssemblyNames.Default.SystemDrawing, position.TypeName.AssemblyName!.FullName);
        Assert.Equal(["x", "y"], position.MemberNames);
        Assert.Equal(84, position.GetRawValue("x"));
        Assert.Equal(32, position.GetRawValue("y"));

        var size = draw.GetClassRecord("m_Size")!;
        Assert.Equal("System.Drawing.Size", size.TypeName.FullName);
        Assert.Equal(["width", "height"], size.MemberNames);
        Assert.Equal(32, size.GetRawValue("width"));
    }

    [Fact]
    public void Known_color_keeps_System_Drawing_state_and_member_order()
    {
        var line = LineDrawRecord(Decode(Write(SampleProject.Create())));
        Assert.Equal("RoboExp.GraphicObjects.LineDraw", line.TypeName.FullName);
        var color = line.GetClassRecord("m_LineColor")!;
        Assert.Equal("System.Drawing.Color", color.TypeName.FullName);
        Assert.Equal(["name", "value", "knownColor", "state"], color.MemberNames);
        Assert.Null(color.GetRawValue("name"));
        Assert.Equal(0L, color.GetRawValue("value"));
        Assert.Equal((short)141, color.GetRawValue("knownColor")); // KnownColor.Red
        Assert.Equal((short)1, color.GetRawValue("state"));        // StateKnownColorValid
        Assert.Equal(3f, line.GetRawValue("m_LineWidth"));
        Assert.Equal(unchecked((int)0xFFFF0000), Color.Red.ToArgb());
    }

    [Fact]
    public void Net11_System_Drawing_profile_uses_legacy_color_member_order()
    {
        var file = SampleProject.Create();
        file.Assemblies = RcuAssemblyNames.Default with { SystemDrawing = "System.Drawing, Version=1.0.5000.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a" };
        var color = LineDrawRecord(Decode(Write(file))).GetClassRecord("m_FillColor")!;
        Assert.Equal(["value", "knownColor", "state", "name"], color.MemberNames);
        Assert.Equal((short)164, color.GetRawValue("knownColor")); // KnownColor.White
    }

    [Fact]
    public void Argb_color_round_trips()
    {
        var file = SampleProject.Create();
        var line = (LineDraw)file.FlowData!.DesignSurfaceData!.DrawingObjects!.List![1].DrawObject!;
        line.LineColor = Color.FromArgb(255, 0, 128, 255);
        var read = RcuReader.Read(new MemoryStream(Write(file)));
        var color = ((LineDraw)read.FlowData!.DesignSurfaceData!.DrawingObjects!.List![1].DrawObject!).LineColor;
        Assert.Equal(Color.StateArgbValueValid, color.State);
        Assert.Equal(0xFF0080FFL, color.Value);
        Assert.Equal((byte)128, color.G);
    }

    [Fact]
    public void Bitmap_stores_encoded_bytes_under_Data_and_shared_images_are_written_once()
    {
        var root = Decode(Write(SampleProject.Create()));
        var image = FirstIconDraw(root).GetClassRecord("m_Image")!;
        Assert.Equal("System.Drawing.Bitmap", image.TypeName.FullName);
        Assert.Equal(["Data"], image.MemberNames);
        Assert.Equal(SampleProject.Png, ((SZArrayRecord<byte>)image.GetArrayRecord("Data")!).GetArray());

        var icon = ProjectDataRecord(root).GetClassRecord("m_ProjectInfo")!.GetClassRecord("m_ProjectIcon")!;
        Assert.Same(icon, image); // one Image instance in the model -> one record, referenced twice

        var model = RcuReader.Read(new MemoryStream(Write(SampleProject.Create())));
        var iconDraw = (IconDraw)model.FlowData!.DesignSurfaceData!.DrawingObjects!.List![0].DrawObject!;
        Assert.Same(model.ProjectData!.ProjectInfo!.ProjectIcon, iconDraw.Image);
        Assert.Equal(ImageFormat.Png, iconDraw.Image!.Format);
        Assert.Equal(new Size(1, 1), iconDraw.Image.PixelSize);
    }

    [Fact]
    public void Enums_are_nested_records_with_int32_value__()
    {
        var root = Decode(Write(SampleProject.Create()));
        var projectType = root.GetClassRecord("m_ProjectType")!;
        Assert.Equal("RoboExp.ProjectType", projectType.TypeName.FullName);
        Assert.Equal(["value__"], projectType.MemberNames);
        Assert.Equal(1, projectType.GetRawValue("value__")); // FlowApp, as Int32

        var software = ProjectDataRecord(root).GetClassRecord("m_HardwareTable")!.GetClassRecord("m_SoftwareType")!;
        Assert.Equal("RoboExp.SoftwareType", software.TypeName.FullName);
        Assert.Equal(RcuAssemblyNames.Default.SoftType, software.TypeName.AssemblyName!.FullName);
        Assert.Equal((int)SoftwareType.E6RCU, software.GetRawValue("value__"));

        var port = ((SZArrayRecord<SerializationRecord>)FirstIconDraw(root).GetClassRecord("m_Ports")!.GetArrayRecord("_items")!).GetArray()[0] as ClassRecord;
        Assert.Equal((int)PortType.Start, port!.GetClassRecord("m_Type")!.GetRawValue("value__"));
        Assert.Equal((int)PortDirection.BottomLeft, port.GetClassRecord("m_PortDirection")!.GetRawValue("value__"));
    }

    [Fact]
    public void Model_survives_write_read_and_rewrite_is_byte_identical()
    {
        var first = Write(SampleProject.Create());
        var read = RcuReader.Read(new MemoryStream(first));
        Assert.Equal(first, Write(read));

        var info = read.ProjectData!.ProjectInfo!;
        Assert.Equal("演示", info.ProjectCaption);
        Assert.Equal(SoftwareType.E6RCU, info.SoftwareType);
        Assert.Contains("中文注释", read.ProjectData.CodeFrameData!.AllCode);
        var list = read.FlowData!.DesignSurfaceData!.DrawingObjects!.List!;
        Assert.Equal(3, list.Count);
        var start = (IconDraw)list[0].DrawObject!;
        Assert.Same(list[0], start.Parent);
        var line = (LineDraw)list[1].DrawObject!;
        Assert.Same(start.Ports![0], line.Ports![0].ConnectedPort);
        Assert.Same(line.Ports[0], start.Ports[0].ConnectedPort);
        Assert.IsType<LineCode>(list[1].CodeObject);
        Assert.Equal(new Point(100, 120), line.EndPosition);
        Assert.Equal(["0", "0", "var1"], ((FuncData)list[2].DataObject!).ValueList!);
    }

    [Fact]
    public void ArrayList_capacity_and_version_are_persisted()
    {
        var file = SampleProject.Create();
        var vars = file.ProjectData!.VarList!;
        vars.Capacity = 16;
        vars.InternalVersion = 42;
        var record = ProjectDataRecord(Decode(Write(file))).GetClassRecord("m_VarList")!;
        Assert.Equal("RoboExp.GraphicObjects.EventArrayList", record.TypeName.FullName);
        Assert.Equal(["m_Version", "m_OldVersion", "Changed", "ArrayList+_items", "ArrayList+_size", "ArrayList+_version"], record.MemberNames);
        Assert.Equal(16, ((SZArrayRecord<SerializationRecord>)record.GetArrayRecord("ArrayList+_items")!).Length);
        Assert.Equal(1, record.GetRawValue("ArrayList+_size"));
        Assert.Equal(42, record.GetRawValue("ArrayList+_version"));
    }

    [Fact]
    public void ArrayList_grows_like_dotnet_framework()
    {
        var list = new ArrayList<int>();
        Assert.Equal(0, list.Capacity);
        list.Add(1);
        Assert.Equal(4, list.Capacity);
        list.AddRange([2, 3, 4, 5]);
        Assert.Equal(8, list.Capacity);
        list.AddRange(Enumerable.Range(0, 20));
        Assert.Equal(25, list.Capacity); // EnsureCapacity(min) takes min when doubling is not enough
        Assert.Equal(3, list.InternalVersion);
    }

    [Fact]
    public void Missing_delegate_is_written_as_null_EventHandler_field()
    {
        var root = Decode(Write(SampleProject.Create()));
        var varList = ProjectDataRecord(root).GetClassRecord("m_VarList")!;
        Assert.Null(varList.GetRawValue("Changed"));
    }

    [Fact]
    public void Serialized_delegate_round_trips_with_target_bound_to_project_data()
    {
        var file = SampleProject.Create();
        var data = file.ProjectData!;
        var method = new SerializedMemberInfo
        {
            Name = "VarListChangedHandle",
            AssemblyName = RcuAssemblyNames.Default.RoboExp,
            ClassName = "RoboExp.ProjectData",
            Signature = "Void VarListChangedHandle(System.Object, System.EventArgs)",
            Signature2 = "System.Void VarListChangedHandle(System.Object, System.EventArgs)",
            MemberType = 8,
        };
        var handler = new SerializedDelegate();
        handler.Members.Add(new("Delegate", new SerializedDelegateEntry
        {
            Type = "System.EventHandler",
            Assembly = "mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089",
            Target = "target0",
            TargetTypeAssembly = RcuAssemblyNames.Default.RoboExp,
            TargetTypeName = "RoboExp.ProjectData",
            MethodName = "VarListChangedHandle",
        }));
        handler.Members.Add(new("target0", data));
        handler.Members.Add(new("method0", method));
        data.VarList!.Changed = handler;

        var bytes = Write(file);
        var pd = ProjectDataRecord(Decode(bytes));
        var holder = pd.GetClassRecord("m_VarList")!.GetClassRecord("Changed")!;
        Assert.Equal("System.DelegateSerializationHolder", holder.TypeName.FullName);
        Assert.Equal(["Delegate", "target0", "method0"], holder.MemberNames);
        Assert.Same(pd, holder.GetClassRecord("target0"));
        Assert.Equal("System.Reflection.MemberInfoSerializationHolder", holder.GetClassRecord("method0")!.TypeName.FullName);

        var read = RcuReader.Read(new MemoryStream(bytes));
        var changed = read.ProjectData!.VarList!.Changed!;
        Assert.Equal(["VarListChangedHandle"], changed.MethodNames);
        Assert.Same(read.ProjectData, changed.Members.Single(m => m.Key == "target0").Value);
        Assert.Equal(bytes, Write(read));
    }

    [Fact]
    public void Older_member_layout_and_unknown_members_are_written_back_as_read()
    {
        var file = SampleProject.Create();
        var param = new ParameterClass
        {
            Version = 100,
            Name = "port",
            Type = "int",
            SerializedMemberOrder =
            [
                "m_Version", "m_Name", "m_Type", "m_DefaultValue", "m_Min", "m_Max", "m_Description",
                "m_IsHardwareType", "m_IsEnumType", "m_FutureMember",
            ],
            UnknownMembers = new() { ["m_FutureMember"] = "kept" },
        };
        file.ProjectData!.ProjectInfo!.ParamList!.Add(param);

        var bytes = Write(file);
        var info = ProjectDataRecord(Decode(bytes)).GetClassRecord("m_ProjectInfo")!;
        var items = ((SZArrayRecord<SerializationRecord>)info.GetClassRecord("m_ParamList")!.GetArrayRecord("_items")!).GetArray();
        var record = (ClassRecord)items[0]!;
        Assert.Equal(param.SerializedMemberOrder, record.MemberNames);
        Assert.Equal("kept", record.GetRawValue("m_FutureMember"));

        var read = RcuReader.Read(new MemoryStream(bytes));
        var p = read.ProjectData!.ProjectInfo!.ParamList![0];
        Assert.Equal(100, p.Version);
        Assert.Equal("", p.NameCHS); // absent in v100 files: constructor default, like the original
        Assert.Equal("kept", p.UnknownMembers!["m_FutureMember"]);
        Assert.Equal(bytes, Write(read));
    }

    [Fact]
    public void Non_nrbf_input_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => RcuReader.Read(new MemoryStream("not a project"u8.ToArray())));
    }

    [Fact]
    public void Nrbf_with_other_root_type_is_rejected()
    {
        byte[] bytes =
        [
            0, 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 1, 0, 0, 0, 0, 0, 0, 0, // header
            4, 1, 0, 0, 0, 3, (byte)'F', (byte)'o', (byte)'o', 0, 0, 0, 0, // SystemClassWithMembersAndTypes "Foo", no members
            11,
        ];
        var ex = Assert.Throws<InvalidDataException>(() => RcuReader.Read(new MemoryStream(bytes)));
        Assert.Contains("Foo", ex.Message);
    }

    [Fact]
    public void Wrong_member_type_is_reported_with_its_location()
    {
        var bytes = Write(SampleProject.Create());
        // Rename the ReturnType class record (last occurrence; earlier ones are member type infos) to an unknown type:
        // still valid NRBF, but not a ReturnType where ProjectInfo.m_ReturnType requires one.
        var text = System.Text.Encoding.UTF8.GetBytes("RoboExp.ReturnType");
        var at = bytes.AsSpan().LastIndexOf(text);
        Assert.True(at > 0);
        System.Text.Encoding.UTF8.GetBytes("RoboExp.ReturnTypX").CopyTo(bytes, at);
        var ex = Assert.Throws<InvalidDataException>(() => RcuReader.Read(new MemoryStream(bytes)));
        Assert.Contains("m_ReturnType", ex.Message);
    }
}
