using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;

namespace RoboExp.Core.Rcu;

/// <summary>Assembly a serialized type lives in; resolved to a display name through <see cref="RcuAssemblyNames"/>.</summary>
internal enum Asm : byte { Mscorlib, RoboExp, SoftType, Drawing }

/// <summary>
/// How the original declared a member, which decides its NRBF member type info
/// (BinaryFormatter derives it from the <c>SerializationInfo.AddValue</c> overload / the field type).
/// </summary>
internal enum MemberKind : byte
{
    Int32,
    Boolean,
    Single,
    Int64,
    Int16,

    /// <summary><c>AddValue(name, string)</c> (object overload): String, or Object when null.</summary>
    StringValue,

    /// <summary>Field of type string: always String.</summary>
    StringField,

    /// <summary>Declared <c>string[]</c>.</summary>
    StringArray,

    /// <summary>Field of type object.</summary>
    ObjectField,

    /// <summary><c>AddValue(name, object)</c>: declared as the runtime type, Object when null.</summary>
    ObjectValue,

    /// <summary><c>AddValue(name, v, typeof(T))</c> / field of reference type T: Class(runtime type ?? T).</summary>
    Typed,

    /// <summary>Struct or enum of declared type T: written inline as a nested class record.</summary>
    ValueTyped,

    /// <summary>Declared <c>object[]</c>.</summary>
    ObjectArray,

    /// <summary>Declared <c>byte[]</c>.</summary>
    ByteArray,
}

internal sealed class MemberSchema(string name, MemberKind kind, Type modelType, string? declType, Asm declAsm,
    Func<object, object?> get, Action<object, object?> set)
{
    public string Name { get; } = name;
    public MemberKind Kind { get; } = kind;

    /// <summary>CLR type of the model property (target type for the reader).</summary>
    public Type ModelType { get; } = modelType;

    /// <summary>Declared wire type for <see cref="MemberKind.Typed"/>/<see cref="MemberKind.ValueTyped"/>.</summary>
    public string? DeclType { get; } = declType;

    public Asm DeclAsm { get; } = declAsm;
    public Func<object, object?> Get { get; } = get;
    public Action<object, object?> Set { get; } = set;
}

internal sealed class TypeSchema(string typeName, Asm asm, Type modelType, Func<RcuObject> create, MemberSchema[] members)
{
    public string TypeName { get; } = typeName;
    public Asm Asm { get; } = asm;
    public Type ModelType { get; } = modelType;
    public Func<RcuObject> Create { get; } = create;

    /// <summary>Members in the order of the 6.0.8 <c>GetObjectData</c>.</summary>
    public MemberSchema[] Members { get; } = members;

    private readonly Dictionary<string, MemberSchema> _byName = members.ToDictionary(m => m.Name, StringComparer.Ordinal);

    public MemberSchema? Find(string name) => _byName.GetValueOrDefault(name);
}

internal sealed class SchemaBuilder<T>(string typeName, Asm asm, Func<T> create) where T : RcuObject
{
    private readonly List<MemberSchema> _members = [];

    private SchemaBuilder<T> Add<TV>(string name, MemberKind kind, string? declType, Asm declAsm, Func<T, TV> get, Action<T, TV> set)
    {
        _members.Add(new MemberSchema(name, kind, typeof(TV), declType, declAsm,
            o => get((T)o),
            (o, v) => set((T)o, v is null ? default! : (TV)v)));
        return this;
    }

    public SchemaBuilder<T> Int(string name, Func<T, int> get, Action<T, int> set) => Add(name, MemberKind.Int32, null, Asm.Mscorlib, get, set);
    public SchemaBuilder<T> Bool(string name, Func<T, bool> get, Action<T, bool> set) => Add(name, MemberKind.Boolean, null, Asm.Mscorlib, get, set);
    public SchemaBuilder<T> Single(string name, Func<T, float> get, Action<T, float> set) => Add(name, MemberKind.Single, null, Asm.Mscorlib, get, set);
    public SchemaBuilder<T> Str(string name, Func<T, string?> get, Action<T, string?> set) => Add(name, MemberKind.StringValue, null, Asm.Mscorlib, get, set);
    public SchemaBuilder<T> StrField(string name, Func<T, string?> get, Action<T, string?> set) => Add(name, MemberKind.StringField, null, Asm.Mscorlib, get, set);
    public SchemaBuilder<T> StrArray(string name, Func<T, string?[]?> get, Action<T, string?[]?> set) => Add(name, MemberKind.StringArray, null, Asm.Mscorlib, get, set);
    public SchemaBuilder<T> Ref<TV>(string name, string declType, Asm declAsm, Func<T, TV?> get, Action<T, TV?> set) where TV : class => Add(name, MemberKind.Typed, declType, declAsm, get, set);
    public SchemaBuilder<T> Val<TV>(string name, string declType, Asm declAsm, Func<T, TV> get, Action<T, TV> set) where TV : struct => Add(name, MemberKind.ValueTyped, declType, declAsm, get, set);
    public SchemaBuilder<T> Obj<TV>(string name, Func<T, TV?> get, Action<T, TV?> set) where TV : class => Add(name, MemberKind.ObjectValue, null, Asm.Mscorlib, get, set);
    public SchemaBuilder<T> ObjField(string name, Func<T, object?> get, Action<T, object?> set) => Add(name, MemberKind.ObjectField, null, Asm.Mscorlib, get, set);

    public SchemaBuilder<T> List<TE>(string name, Func<T, ArrayList<TE>?> get, Action<T, ArrayList<TE>?> set) =>
        Ref(name, "System.Collections.ArrayList", Asm.Mscorlib, get, set);

    public SchemaBuilder<T> EventList<TE>(string name, Func<T, EventArrayList<TE>?> get, Action<T, EventArrayList<TE>?> set) =>
        Ref(name, RcuSchema.EventArrayListType, Asm.RoboExp, get, set);

    public TypeSchema Build() => new(typeName, asm, typeof(T), () => create(), [.. _members]);
}

/// <summary>
/// Serialized shape of every original type, transcribed from the decompiled <c>GetObjectData</c> implementations
/// (RoboExp 6.0.8.31673). Shared by <see cref="RcuReader"/> and <see cref="RcuWriter"/>.
/// </summary>
internal static class RcuSchema
{
    public const string ArrayListType = "System.Collections.ArrayList";
    public const string EventArrayListType = "RoboExp.GraphicObjects.EventArrayList";
    public const string BitmapType = "System.Drawing.Bitmap";
    public const string PointType = "System.Drawing.Point";
    public const string SizeType = "System.Drawing.Size";
    public const string ColorType = "System.Drawing.Color";
    public const string DelegateHolderType = "System.DelegateSerializationHolder";
    public const string DelegateEntryType = "System.DelegateSerializationHolder+DelegateEntry";
    public const string EventHandlerType = "System.EventHandler";

    private const string G = "RoboExp.GraphicObjects.";

    /// <summary>Enums persisted as nested records with member <c>value__</c>.</summary>
    public static readonly IReadOnlyDictionary<Type, (string TypeName, Asm Asm)> Enums = new Dictionary<Type, (string, Asm)>
    {
        [typeof(ProjectType)] = ("RoboExp.ProjectType", Asm.RoboExp),
        [typeof(IconType)] = ("RoboExp.IconType", Asm.RoboExp),
        [typeof(SoftwareType)] = ("RoboExp.SoftwareType", Asm.SoftType),
        [typeof(OuterType)] = ("RoboExp.OuterType", Asm.SoftType),
        [typeof(PortType)] = (G + "PortType", Asm.RoboExp),
        [typeof(PortDirection)] = (G + "PortDirection", Asm.RoboExp),
    };

    public static readonly IReadOnlyDictionary<string, Type> EnumsByName = Enums.ToDictionary(kv => kv.Value.TypeName, kv => kv.Key);

    public static readonly IReadOnlyDictionary<string, TypeSchema> ByName;
    public static readonly IReadOnlyDictionary<Type, TypeSchema> ByModelType;

    static RcuSchema()
    {
        TypeSchema[] all =
        [
            new SchemaBuilder<RcuFile>("RoboExp.RCUFile", Asm.RoboExp, () => new RcuFile())
                .Int("m_Version", o => o.Version, (o, v) => o.Version = v)
                .Val("m_ProjectType", "RoboExp.ProjectType", Asm.RoboExp, o => o.ProjectType, (o, v) => o.ProjectType = v)
                .Ref("m_FlowData", "RoboExp.FlowData", Asm.RoboExp, o => o.FlowData, (o, v) => o.FlowData = v)
                .Ref("m_CodeData", "RoboExp.CodeData", Asm.RoboExp, o => o.CodeData, (o, v) => o.CodeData = v)
                .Build(),
            AddBaseData(new SchemaBuilder<FlowData>("RoboExp.FlowData", Asm.RoboExp, () => new FlowData()))
                .Int("m_Version", o => o.Version, (o, v) => o.Version = v)
                .Ref("m_DesignSurfaceData", "RoboExp.DesignSurfaceData", Asm.RoboExp, o => o.DesignSurfaceData, (o, v) => o.DesignSurfaceData = v)
                .Build(),
            AddBaseData(new SchemaBuilder<CodeData>("RoboExp.CodeData", Asm.RoboExp, () => new CodeData()))
                .Int("m_Version", o => o.Version, (o, v) => o.Version = v)
                .Build(),
            AddBaseData(new SchemaBuilder<FunctionData>("RoboExp.FunctionData", Asm.RoboExp, () => new FunctionData()))
                .Int("m_Version", o => o.Version, (o, v) => o.Version = v)
                .Str("m_FuncHead", o => o.FuncHead, (o, v) => o.FuncHead = v)
                .Str("m_FuncDeclare", o => o.FuncDeclare, (o, v) => o.FuncDeclare = v)
                .Str("m_FuncBody", o => o.FuncBody, (o, v) => o.FuncBody = v)
                .Build(),
            new SchemaBuilder<ProjectData>("RoboExp.ProjectData", Asm.RoboExp, () => new ProjectData())
                .Int("m_ProjectDataVersion", o => o.ProjectDataVersion, (o, v) => o.ProjectDataVersion = v)
                .Ref("m_ProjectInfo", "RoboExp.ProjectInfo", Asm.RoboExp, o => o.ProjectInfo, (o, v) => o.ProjectInfo = v)
                .EventList("m_VarList", o => o.VarList, (o, v) => o.VarList = v)
                .Ref("m_HardwareTable", "RoboExp.HardwareTable", Asm.RoboExp, o => o.HardwareTable, (o, v) => o.HardwareTable = v)
                .EventList("m_UsedProjectInfoList", o => o.UsedProjectInfoList, (o, v) => o.UsedProjectInfoList = v)
                .Ref("m_CodeFrameData", "RoboExp.CodeFrameData", Asm.RoboExp, o => o.CodeFrameData, (o, v) => o.CodeFrameData = v)
                .Build(),
            new SchemaBuilder<CodeFrameData>("RoboExp.CodeFrameData", Asm.RoboExp, () => new CodeFrameData())
                .Int("m_OldVersion", o => o.OldVersion, (o, v) => o.OldVersion = v)
                .Str("m_Global", o => o.Global, (o, v) => o.Global = v)
                .Str("m_Func", o => o.Func, (o, v) => o.Func = v)
                .Str("m_Code", o => o.Code, (o, v) => o.Code = v)
                .Str("m_AllCode", o => o.AllCode, (o, v) => o.AllCode = v)
                .Build(),
            new SchemaBuilder<DesignSurfaceData>("RoboExp.DesignSurfaceData", Asm.RoboExp, () => new DesignSurfaceData())
                .Int("m_Version", o => o.Version, (o, v) => o.Version = v)
                .Val("m_ScrollOffset", PointType, Asm.Drawing, o => o.ScrollOffset, (o, v) => o.ScrollOffset = v)
                .Ref("m_DrawingObjects", G + "GraphicObjectCollection", Asm.RoboExp, o => o.DrawingObjects, (o, v) => o.DrawingObjects = v)
                .Build(),
            new SchemaBuilder<ProjectInfo>("RoboExp.ProjectInfo", Asm.RoboExp, () => new ProjectInfo())
                .Int("m_Version", o => o.Version, (o, v) => o.Version = v)
                .Str("m_UserVersion", o => o.UserVersion, (o, v) => o.UserVersion = v)
                .Str("m_UserName", o => o.UserName, (o, v) => o.UserName = v)
                .Str("m_ProjectName", o => o.ProjectName, (o, v) => o.ProjectName = v)
                .Str("m_ProjectCaption", o => o.ProjectCaption, (o, v) => o.ProjectCaption = v)
                .Str("m_ProjectDescription", o => o.ProjectDescription, (o, v) => o.ProjectDescription = v)
                .Str("m_ProjectPath", o => o.ProjectPath, (o, v) => o.ProjectPath = v)
                .Ref("m_ReturnType", "RoboExp.ReturnType", Asm.RoboExp, o => o.ReturnType, (o, v) => o.ReturnType = v)
                .Ref("m_ProjectIcon", "System.Drawing.Image", Asm.Drawing, o => o.ProjectIcon, (o, v) => o.ProjectIcon = v)
                .Val("m_SoftwareType", "RoboExp.SoftwareType", Asm.SoftType, o => o.SoftwareType, (o, v) => o.SoftwareType = v)
                .Val("m_OuterType", "RoboExp.OuterType", Asm.SoftType, o => o.OuterType, (o, v) => o.OuterType = v)
                .Val("m_ProjectType", "RoboExp.ProjectType", Asm.RoboExp, o => o.ProjectType, (o, v) => o.ProjectType = v)
                .List("m_ParamList", o => o.ParamList, (o, v) => o.ParamList = v)
                .List("m_SubFuncUsedHardObjectList", o => o.SubFuncUsedHardObjectList, (o, v) => o.SubFuncUsedHardObjectList = v)
                .List("m_CurUsedHardInstanceList", o => o.CurUsedHardInstanceList, (o, v) => o.CurUsedHardInstanceList = v)
                .EventList("m_SubFuncUsedGlobalVarObjectList", o => o.SubFuncUsedGlobalVarObjectList, (o, v) => o.SubFuncUsedGlobalVarObjectList = v)
                .EventList("m_CurUsedGlobalVarList", o => o.CurUsedGlobalVarList, (o, v) => o.CurUsedGlobalVarList = v)
                .Build(),
            new SchemaBuilder<ReturnType>("RoboExp.ReturnType", Asm.RoboExp, () => new ReturnType())
                .Int("m_Version", o => o.Version, (o, v) => o.Version = v)
                .Str("m_Type", o => o.Type, (o, v) => o.Type = v)
                .Str("m_Description", o => o.Description, (o, v) => o.Description = v)
                .Build(),
            new SchemaBuilder<ParameterClass>("RoboExp.ParameterClass", Asm.RoboExp, () => new ParameterClass())
                .Int("m_Version", o => o.Version, (o, v) => o.Version = v)
                .Str("m_Name", o => o.Name, (o, v) => o.Name = v)
                .Str("m_NameCHS", o => o.NameCHS, (o, v) => o.NameCHS = v)
                .Str("m_NameCHT", o => o.NameCHT, (o, v) => o.NameCHT = v)
                .Str("m_NameDefault", o => o.NameDefault, (o, v) => o.NameDefault = v)
                .Str("m_Type", o => o.Type, (o, v) => o.Type = v)
                .Str("m_DefaultValue", o => o.DefaultValue, (o, v) => o.DefaultValue = v)
                .Str("m_Min", o => o.Min, (o, v) => o.Min = v)
                .Str("m_Max", o => o.Max, (o, v) => o.Max = v)
                .Str("m_Description", o => o.Description, (o, v) => o.Description = v)
                .Bool("m_IsHardwareType", o => o.IsHardwareType, (o, v) => o.IsHardwareType = v)
                .Bool("m_IsEnumType", o => o.IsEnumType, (o, v) => o.IsEnumType = v)
                .Build(),
            new SchemaBuilder<VarStruct>("RoboExp.VarStruct", Asm.RoboExp, () => new VarStruct())
                .Int("m_Version", o => o.Version, (o, v) => o.Version = v)
                .Str("m_Type", o => o.Type, (o, v) => o.Type = v)
                .Str("m_Name", o => o.Name, (o, v) => o.Name = v)
                .Str("m_Description", o => o.Description, (o, v) => o.Description = v)
                .Str("m_DefaultValue", o => o.DefaultValue, (o, v) => o.DefaultValue = v)
                .Int("m_ReturnCount", o => o.ReturnCount, (o, v) => o.ReturnCount = v)
                .Int("m_ParamCount", o => o.ParamCount, (o, v) => o.ParamCount = v)
                .Bool("m_IsParam", o => o.IsParam, (o, v) => o.IsParam = v)
                .Build(),
            new SchemaBuilder<UsedHardObject>("RoboExp.UsedHardObject", Asm.RoboExp, () => new UsedHardObject())
                .Int("m_Version", o => o.Version, (o, v) => o.Version = v)
                .Str("m_ProjectName", o => o.ProjectName, (o, v) => o.ProjectName = v)
                .List("m_HardwareInstanceList", o => o.HardwareInstanceList, (o, v) => o.HardwareInstanceList = v)
                .Build(),
            new SchemaBuilder<UsedGlobalVarObject>("RoboExp.UsedGlobalVarObject", Asm.RoboExp, () => new UsedGlobalVarObject())
                .Int("m_Version", o => o.Version, (o, v) => o.Version = v)
                .Str("m_ProjectName", o => o.ProjectName, (o, v) => o.ProjectName = v)
                .List("m_GlobalVarList", o => o.GlobalVarList, (o, v) => o.GlobalVarList = v)
                .Build(),
            new SchemaBuilder<HardwareTable>("RoboExp.HardwareTable", Asm.RoboExp, () => new HardwareTable())
                .Int("m_Version", o => o.Version, (o, v) => o.Version = v)
                .EventList("m_HardwareItemList", o => o.HardwareItemList, (o, v) => o.HardwareItemList = v)
                .Str("m_Description", o => o.Description, (o, v) => o.Description = v)
                .Val("m_SoftwareType", "RoboExp.SoftwareType", Asm.SoftType, o => o.SoftwareType, (o, v) => o.SoftwareType = v)
                .Build(),
            new SchemaBuilder<HardwareItem>("RoboExp.HardwareItem", Asm.RoboExp, () => new HardwareItem())
                .Int("m_Version", o => o.Version, (o, v) => o.Version = v)
                .Ref("m_HardwareType", "RoboExp.HardwareType", Asm.RoboExp, o => o.HardwareType, (o, v) => o.HardwareType = v)
                .List("m_InstanceList", o => o.InstanceList, (o, v) => o.InstanceList = v)
                .Build(),
            new SchemaBuilder<HardwareType>("RoboExp.HardwareType", Asm.RoboExp, () => new HardwareType())
                .Int("m_Version", o => o.Version, (o, v) => o.Version = v)
                .Str("m_Name", o => o.Name, (o, v) => o.Name = v)
                .Int("m_PinCount", o => o.PinCount, (o, v) => o.PinCount = v)
                .List("m_PinDescription", o => o.PinDescriptionList, (o, v) => o.PinDescriptionList = v)
                .Bool("m_IsDefault", o => o.IsDefault, (o, v) => o.IsDefault = v)
                .Str("m_Description", o => o.Description, (o, v) => o.Description = v)
                .List("m_CtrlLimitList", o => o.CtrlLimitList, (o, v) => o.CtrlLimitList = v)
                .Str("m_NickName", o => o.NickName, (o, v) => o.NickName = v)
                .List("m_GNDLimitList", o => o.GNDLimitList, (o, v) => o.GNDLimitList = v)
                .List("m_VccLimitList", o => o.VccLimitList, (o, v) => o.VccLimitList = v)
                .Build(),
            new SchemaBuilder<HardwareInstance>("RoboExp.HardwareInstance", Asm.RoboExp, () => new HardwareInstance())
                .Int("m_Version", o => o.Version, (o, v) => o.Version = v)
                .Str("m_TypeName", o => o.TypeName, (o, v) => o.TypeName = v)
                .Str("m_Name", o => o.Name, (o, v) => o.Name = v)
                .Ref("m_PinTeam", "RoboExp.RCUInfo+PinTeam", Asm.RoboExp, o => o.PinTeam, (o, v) => o.PinTeam = v)
                .Int("m_TypeNum", o => o.TypeNum, (o, v) => o.TypeNum = v)
                .Build(),
            new SchemaBuilder<PinTeam>("RoboExp.RCUInfo+PinTeam", Asm.RoboExp, () => new PinTeam())
                .List("CtrlPinNumList", o => o.CtrlPinNumList, (o, v) => o.CtrlPinNumList = v)
                .List("GNDPinNumList", o => o.GNDPinNumList, (o, v) => o.GNDPinNumList = v)
                .List("VccPinNumList", o => o.VccPinNumList, (o, v) => o.VccPinNumList = v)
                .Build(),
            new SchemaBuilder<GraphicObject>(G + "GraphicObject", Asm.RoboExp, () => new GraphicObject())
                .Int("m_GraphicObjectVersion", o => o.GraphicObjectVersion, (o, v) => o.GraphicObjectVersion = v)
                .Ref("m_DrawObject", G + "DrawObject", Asm.RoboExp, o => o.DrawObject, (o, v) => o.DrawObject = v)
                .Ref("m_DataObject", G + "DataObject", Asm.RoboExp, o => o.DataObject, (o, v) => o.DataObject = v)
                .Ref("m_CodeObject", G + "CodeObject", Asm.RoboExp, o => o.CodeObject, (o, v) => o.CodeObject = v)
                .Build(),
            new SchemaBuilder<GraphicObjectCollection>(G + "GraphicObjectCollection", Asm.RoboExp, () => new GraphicObjectCollection())
                .Int("m_Version", o => o.Version, (o, v) => o.Version = v)
                .List("List", o => o.List, (o, v) => o.List = v)
                .Build(),
            AddDrawObject(new SchemaBuilder<IconDraw>(G + "IconDraw", Asm.RoboExp, () => new IconDraw()))
                .Int("m_IconDrawVersion", o => o.IconDrawVersion, (o, v) => o.IconDrawVersion = v)
                .Ref("m_Image", "System.Drawing.Image", Asm.Drawing, o => o.Image, (o, v) => o.Image = v)
                .Val("m_IconType", "RoboExp.IconType", Asm.RoboExp, o => o.IconType, (o, v) => o.IconType = v)
                .Build(),
            AddDrawObject(new SchemaBuilder<LineDraw>(G + "LineDraw", Asm.RoboExp, () => new LineDraw()))
                .Int("m_LineDrawVersion", o => o.LineDrawVersion, (o, v) => o.LineDrawVersion = v)
                .Single("m_LineWidth", o => o.LineWidth, (o, v) => o.LineWidth = v)
                .Val("m_LineColor", ColorType, Asm.Drawing, o => o.LineColor, (o, v) => o.LineColor = v)
                .Val("m_FillColor", ColorType, Asm.Drawing, o => o.FillColor, (o, v) => o.FillColor = v)
                .Bool("m_Fill", o => o.Fill, (o, v) => o.Fill = v)
                .Val("m_EndPosition", PointType, Asm.Drawing, o => o.EndPosition, (o, v) => o.EndPosition = v)
                .Build(),
            AddTextBaseDraw(AddDrawObject(new SchemaBuilder<TextBaseDraw>(G + "TextBaseDraw", Asm.RoboExp, () => new TextBaseDraw())))
                .Build(),
            AddTextBaseDraw(AddDrawObject(new SchemaBuilder<TextDraw>(G + "TextDraw", Asm.RoboExp, () => new TextDraw())))
                .Int("m_TextDrawVersion", o => o.TextDrawVersion, (o, v) => o.TextDrawVersion = v)
                .Build(),
            new SchemaBuilder<Port>(G + "Port", Asm.RoboExp, () => new Port())
                .Int("m_PortVersion", o => o.PortVersion, (o, v) => o.PortVersion = v)
                .Ref("m_Parent", G + "DrawObject", Asm.RoboExp, o => o.Parent, (o, v) => o.Parent = v)
                .Val("m_Type", G + "PortType", Asm.RoboExp, o => o.Type, (o, v) => o.Type = v)
                .Ref("m_ConnectedPort", G + "Port", Asm.RoboExp, o => o.ConnectedPort, (o, v) => o.ConnectedPort = v)
                .Val("m_PortDirection", G + "PortDirection", Asm.RoboExp, o => o.PortDirection, (o, v) => o.PortDirection = v)
                .Build(),
            AddDataObject(new SchemaBuilder<DataObject>(G + "DataObject", Asm.RoboExp, () => new DataObject()))
                .Build(),
            AddDataObject(new SchemaBuilder<FuncData>(G + "FuncData", Asm.RoboExp, () => new FuncData()))
                .Int("m_FuncDataVersion", o => o.FuncDataVersion, (o, v) => o.FuncDataVersion = v)
                .Str("m_ReturnName", o => o.ReturnName, (o, v) => o.ReturnName = v)
                .List("m_ValueList", o => o.ValueList, (o, v) => o.ValueList = v)
                .Build(),
            AddDataObject(new SchemaBuilder<BeginForData>(G + "BeginForData", Asm.RoboExp, () => new BeginForData()))
                .Int("m_BeginForDataVersion", o => o.BeginForDataVersion, (o, v) => o.BeginForDataVersion = v)
                .Int("m_LoopCount", o => o.LoopCount, (o, v) => o.LoopCount = v)
                .Str("m_LoopCountStr", o => o.LoopCountStr, (o, v) => o.LoopCountStr = v)
                .Build(),
            AddDataObject(new SchemaBuilder<BeginIfData>(G + "BeginIfData", Asm.RoboExp, () => new BeginIfData()))
                .Int("m_BeginIfDataVersion", o => o.BeginIfDataVersion, (o, v) => o.BeginIfDataVersion = v)
                .Str("m_Expression", o => o.Expression, (o, v) => o.Expression = v)
                .Build(),
            AddDataObject(new SchemaBuilder<BeginWhileData>(G + "BeginWhileData", Asm.RoboExp, () => new BeginWhileData()))
                .Int("m_BeginWhileDataVersion", o => o.BeginWhileDataVersion, (o, v) => o.BeginWhileDataVersion = v)
                .Str("m_Expression", o => o.Expression, (o, v) => o.Expression = v)
                .Build(),
            AddDataObject(new SchemaBuilder<CalculateData>(G + "CalculateData", Asm.RoboExp, () => new CalculateData()))
                .Int("m_CalculateDataVersion", o => o.CalculateDataVersion, (o, v) => o.CalculateDataVersion = v)
                .Str("m_Expression", o => o.Expression, (o, v) => o.Expression = v)
                .Build(),
            AddDataObject(new SchemaBuilder<ReturnData>(G + "ReturnData", Asm.RoboExp, () => new ReturnData()))
                .Int("m_ReturnDataVersion", o => o.ReturnDataVersion, (o, v) => o.ReturnDataVersion = v)
                .Str("m_Expression", o => o.Expression, (o, v) => o.Expression = v)
                .Build(),
            AddDataObject(new SchemaBuilder<CodeEditorData>(G + "CodeEditorData", Asm.RoboExp, () => new CodeEditorData()))
                .Int("m_CodeEditorDataVersion", o => o.CodeEditorDataVersion, (o, v) => o.CodeEditorDataVersion = v)
                // AddValue(name, ArrayList) without a type: declared as the runtime type.
                .Obj("m_Codes", o => o.Codes, (o, v) => o.Codes = v)
                .Obj("m_Includes", o => o.Includes, (o, v) => o.Includes = v)
                .Build(),
            new SchemaBuilder<CodeObject>(G + "CodeObject", Asm.RoboExp, () => new CodeObject())
                .Int("m_CodeObjectVersion", o => o.CodeObjectVersion, (o, v) => o.CodeObjectVersion = v)
                .Build(),
            new SchemaBuilder<LineCode>(G + "LineCode", Asm.RoboExp, () => new LineCode())
                .Int("m_CodeObjectVersion", o => o.CodeObjectVersion, (o, v) => o.CodeObjectVersion = v)
                .Int("m_LineCodeVersion", o => o.LineCodeVersion, (o, v) => o.LineCodeVersion = v)
                .Build(),
            new SchemaBuilder<SerializedMemberInfo>("System.Reflection.MemberInfoSerializationHolder", Asm.Mscorlib, () => new SerializedMemberInfo())
                .StrField("Name", o => o.Name, (o, v) => o.Name = v)
                .StrField("AssemblyName", o => o.AssemblyName, (o, v) => o.AssemblyName = v)
                .StrField("ClassName", o => o.ClassName, (o, v) => o.ClassName = v)
                .StrField("Signature", o => o.Signature, (o, v) => o.Signature = v)
                .StrField("Signature2", o => o.Signature2, (o, v) => o.Signature2 = v)
                .Int("MemberType", o => o.MemberType, (o, v) => o.MemberType = v)
                .Ref("GenericArguments", "System.Type[]", Asm.Mscorlib, o => o.GenericArguments, (o, v) => o.GenericArguments = v)
                .Build(),
        ];
        ByName = all.ToDictionary(s => s.TypeName, StringComparer.Ordinal);
        ByModelType = all.ToDictionary(s => s.ModelType);
    }

    private static SchemaBuilder<T> AddBaseData<T>(SchemaBuilder<T> b) where T : BaseData => b
        .Int("m_BaseDataVersion", o => o.BaseDataVersion, (o, v) => o.BaseDataVersion = v)
        .Ref("m_ProjectData", "RoboExp.ProjectData", Asm.RoboExp, o => o.ProjectData, (o, v) => o.ProjectData = v);

    private static SchemaBuilder<T> AddDrawObject<T>(SchemaBuilder<T> b) where T : DrawObject => b
        .Int("m_DrawObjectVersion", o => o.DrawObjectVersion, (o, v) => o.DrawObjectVersion = v)
        .Ref("m_Parent", G + "GraphicObject", Asm.RoboExp, o => o.Parent, (o, v) => o.Parent = v)
        .Val("m_Position", PointType, Asm.Drawing, o => o.Position, (o, v) => o.Position = v)
        .Val("m_Size", SizeType, Asm.Drawing, o => o.Size, (o, v) => o.Size = v)
        .Bool("m_AutoSize", o => o.AutoSize, (o, v) => o.AutoSize = v)
        .List("m_Ports", o => o.Ports, (o, v) => o.Ports = v);

    private static SchemaBuilder<T> AddTextBaseDraw<T>(SchemaBuilder<T> b) where T : TextBaseDraw => b
        .Int("m_TextBaseDrawVersion", o => o.TextBaseDrawVersion, (o, v) => o.TextBaseDrawVersion = v)
        .Str("m_Text", o => o.Text, (o, v) => o.Text = v);

    private static SchemaBuilder<T> AddDataObject<T>(SchemaBuilder<T> b) where T : DataObject => b
        .Int("m_DataObjectVersion", o => o.DataObjectVersion, (o, v) => o.DataObjectVersion = v)
        .StrArray("m_Description", o => o.Description, (o, v) => o.Description = v)
        .Str("m_ProjectName", o => o.ProjectName, (o, v) => o.ProjectName = v)
        .Str("m_Code", o => o.Code, (o, v) => o.Code = v);

    public static string? AssemblyName(Asm asm, RcuAssemblyNames names) => asm switch
    {
        Asm.RoboExp => names.RoboExp,
        Asm.SoftType => names.SoftType,
        Asm.Drawing => names.SystemDrawing,
        _ => null,
    };
}
