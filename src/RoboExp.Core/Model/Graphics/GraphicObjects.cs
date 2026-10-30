using System.Text;

namespace RoboExp.Core.Model.Graphics;

/// <summary>
/// <c>RoboExp.GraphicObjects.GraphicObject</c>: one flowchart element = drawing (<see cref="DrawObject"/>) +
/// payload (<see cref="DataObject"/>) + code-generation state (<see cref="CodeObject"/>).
/// </summary>
public sealed class GraphicObject : RcuObject
{
    public int GraphicObjectVersion { get; set; } = 100;
    public DrawObject? DrawObject { get; set; }
    public DataObject? DataObject { get; set; } = new();
    public CodeObject? CodeObject { get; set; } = new();
}

/// <summary>
/// <c>RoboExp.GraphicObjects.GraphicObjectCollection</c> (a CollectionBase): members <c>m_Version</c> and
/// <c>List</c> (the inner ArrayList).
/// </summary>
public sealed class GraphicObjectCollection : RcuObject
{
    public int Version { get; set; } = 100;
    public ArrayList<GraphicObject>? List { get; set; } = new();
}

/// <summary>
/// <c>RoboExp.GraphicObjects.DrawObject</c> (abstract): members
/// <c>m_DrawObjectVersion, m_Parent, m_Position, m_Size, m_AutoSize, m_Ports</c>.
/// </summary>
public abstract class DrawObject : RcuObject
{
    public int DrawObjectVersion { get; set; } = 100;

    /// <summary>Owning graphic object (back reference).</summary>
    public GraphicObject? Parent { get; set; }

    /// <summary>Top-left (icons) or start point (lines).</summary>
    public Point Position { get; set; }

    public Size Size { get; set; }
    public bool AutoSize { get; set; }
    public ArrayList<Port>? Ports { get; set; } = new(4);
}

/// <summary><c>RoboExp.GraphicObjects.IconDraw</c>: a flowchart icon (adds <c>m_IconDrawVersion, m_Image, m_IconType</c>).</summary>
public sealed class IconDraw : DrawObject
{
    public int IconDrawVersion { get; set; } = 100;

    /// <summary>Serialized as <c>System.Drawing.Image</c> (a Bitmap).</summary>
    public EncodedImage? Image { get; set; }

    public IconType IconType { get; set; }
}

/// <summary>
/// <c>RoboExp.GraphicObjects.LineDraw</c>: a connector (adds
/// <c>m_LineDrawVersion, m_LineWidth, m_LineColor, m_FillColor, m_Fill, m_EndPosition</c>).
/// Ports[0] is the start port, Ports[1] the end port.
/// </summary>
public sealed class LineDraw : DrawObject
{
    public int LineDrawVersion { get; set; } = 100;
    public float LineWidth { get; set; } = 3f;
    public Color LineColor { get; set; } = Color.Red;
    public Color FillColor { get; set; } = Color.White;
    public bool Fill { get; set; }
    public Point EndPosition { get; set; }
}

/// <summary><c>RoboExp.GraphicObjects.TextBaseDraw</c> (adds <c>m_TextBaseDrawVersion, m_Text</c>).</summary>
public class TextBaseDraw : DrawObject
{
    public int TextBaseDrawVersion { get; set; } = 100;
    public string? Text { get; set; } = "";
}

/// <summary><c>RoboExp.GraphicObjects.TextDraw</c>: a text note (adds <c>m_TextDrawVersion</c>).</summary>
public sealed class TextDraw : TextBaseDraw
{
    public int TextDrawVersion { get; set; } = 100;
}

/// <summary><c>RoboExp.GraphicObjects.PortType</c>.</summary>
public enum PortType
{
    Default = 0,
    In = 1,
    Out = 2,
    Start = 3,
    BeginIf = 4,
    EndIf = 5,
    BeginFor = 6,
    EndFor = 7,
    BeginWhile = 8,
    EndWhile = 9,
    BeginElse = 10,
    EndElse = 11,
}

/// <summary><c>RoboExp.GraphicObjects.PortDirection</c>.</summary>
public enum PortDirection
{
    TopLeft = 0,
    TopRight = 1,
    BottomLeft = 2,
    BottomRight = 3,
}

/// <summary>
/// <c>RoboExp.GraphicObjects.Port</c>: connection point of an icon or line end
/// (members <c>m_PortVersion, m_Parent, m_Type, m_ConnectedPort, m_PortDirection</c>).
/// </summary>
public sealed class Port : RcuObject
{
    public int PortVersion { get; set; } = 100;
    public DrawObject? Parent { get; set; }
    public PortType Type { get; set; } = PortType.In;
    public Port? ConnectedPort { get; set; }
    public PortDirection PortDirection { get; set; }
}

/// <summary>
/// <c>RoboExp.GraphicObjects.DataObject</c>: payload of plain icons (start, end markers, continue/break...);
/// base of the specific payloads. Members <c>m_DataObjectVersion, m_Description, m_ProjectName, m_Code</c>.
/// </summary>
public class DataObject : RcuObject
{
    public int DataObjectVersion { get; set; } = 100;

    /// <summary>Comment lines.</summary>
    public string?[]? Description { get; set; }

    public string? ProjectName { get; set; } = "";
    public string? Code { get; set; } = "";

    /// <summary>Original <c>GetDescription</c>: lines joined with CRLF.</summary>
    public string GetDescription() => Description is { Length: > 0 } d ? string.Join("\r\n", d) : "";
}

/// <summary><c>RoboExp.GraphicObjects.FuncData</c>: a function call (adds <c>m_FuncDataVersion, m_ReturnName, m_ValueList</c>).</summary>
public sealed class FuncData : DataObject
{
    public int FuncDataVersion { get; set; } = 100;

    /// <summary>Variable receiving the return value.</summary>
    public string? ReturnName { get; set; } = "";

    /// <summary>Argument expressions; hardware arguments are encoded <c>_Type_Instance_</c>.</summary>
    public ArrayList<string?>? ValueList { get; set; } = new();

    /// <summary>Original <c>GetUsingHardInstName</c>: instance name of the first <c>_Type_Instance_</c> argument.</summary>
    public string GetUsingHardInstName()
    {
        foreach (var value in ValueList ?? [])
        {
            if (value != null && IsHardwareArg(value)) return HardwareInstanceOf(value);
        }
        return "";
    }

    /// <summary>Original <c>GetParamString</c>: first <paramref name="paramCount"/> non-empty args, CRLF separated.</summary>
    public string GetParamString(int paramCount)
    {
        var sb = new StringBuilder();
        var n = 0;
        foreach (var value in ValueList ?? [])
        {
            if (++n > paramCount) break;
            if (string.IsNullOrEmpty(value)) continue;
            if (sb.Length > 0) sb.Append("\r\n");
            sb.Append(IsHardwareArg(value) ? HardwareInstanceOf(value) : value);
        }
        return sb.ToString();
    }

    private static bool IsHardwareArg(string value) =>
        value.Count(c => c == '_') >= 3 && value.StartsWith('_') && value.EndsWith('_');

    private static string HardwareInstanceOf(string value)
    {
        var inner = value[1..^1];
        return inner[(inner.IndexOf('_') + 1)..];
    }
}

/// <summary><c>RoboExp.GraphicObjects.BeginForData</c> (adds <c>m_BeginForDataVersion, m_LoopCount, m_LoopCountStr</c>).</summary>
public sealed class BeginForData : DataObject
{
    /// <summary>101 since <c>m_LoopCountStr</c> exists; the original derives LoopCountStr from LoopCount for 100.</summary>
    public int BeginForDataVersion { get; set; } = 101;

    public int LoopCount { get; set; } = 1;
    public string? LoopCountStr { get; set; } = "1";
}

/// <summary><c>RoboExp.GraphicObjects.BeginIfData</c> (adds <c>m_BeginIfDataVersion, m_Expression</c>).</summary>
public sealed class BeginIfData : DataObject
{
    public int BeginIfDataVersion { get; set; } = 100;
    public string? Expression { get; set; } = "";
}

/// <summary><c>RoboExp.GraphicObjects.BeginWhileData</c> (adds <c>m_BeginWhileDataVersion, m_Expression</c>).</summary>
public sealed class BeginWhileData : DataObject
{
    public int BeginWhileDataVersion { get; set; } = 100;
    public string? Expression { get; set; } = "";
}

/// <summary><c>RoboExp.GraphicObjects.CalculateData</c> (adds <c>m_CalculateDataVersion, m_Expression</c>).</summary>
public sealed class CalculateData : DataObject
{
    public int CalculateDataVersion { get; set; } = 100;
    public string? Expression { get; set; } = "";
}

/// <summary><c>RoboExp.GraphicObjects.ReturnData</c> (adds <c>m_ReturnDataVersion, m_Expression</c>).</summary>
public sealed class ReturnData : DataObject
{
    public int ReturnDataVersion { get; set; } = 100;
    public string? Expression { get; set; } = "";
}

/// <summary>
/// <c>RoboExp.GraphicObjects.CodeEditorData</c>: an inline C code block
/// (adds <c>m_CodeEditorDataVersion, m_Codes, m_Includes</c>).
/// </summary>
public sealed class CodeEditorData : DataObject
{
    public int CodeEditorDataVersion { get; set; } = 100;
    public ArrayList<string?>? Codes { get; set; } = new();
    public ArrayList<string?>? Includes { get; set; } = new();
}

/// <summary>
/// <c>RoboExp.GraphicObjects.CodeObject</c>: only <c>m_CodeObjectVersion</c> is persisted; the remaining original
/// fields are code-generation scratch state rebuilt on every generation.
/// </summary>
public class CodeObject : RcuObject
{
    public int CodeObjectVersion { get; set; } = 100;
}

/// <summary><c>RoboExp.GraphicObjects.LineCode</c> (adds <c>m_LineCodeVersion</c>).</summary>
public sealed class LineCode : CodeObject
{
    public int LineCodeVersion { get; set; } = 100;
}
