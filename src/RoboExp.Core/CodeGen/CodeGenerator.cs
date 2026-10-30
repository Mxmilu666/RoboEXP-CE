using System.Text;
using RoboExp.Core.Flow;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;

namespace RoboExp.Core.CodeGen;

/// <summary>
/// Generation settings the original reads from global state: <c>PublicInfo.SoftwareInfo.SoftwareType/OuterType</c>,
/// <c>PublicInfo.CodeInfo</c> (Code.xml; no target defines it, so EntryStr "int main(void)", ReturnType "long", no globals)
/// and <c>LibManager.CheckIsSystemFuncName</c> (Include.xml).
/// </summary>
public sealed record CodeGenOptions(SoftwareType Type, OuterType OuterType = OuterType.Entity, Func<string, bool>? IsSystemFunction = null)
{
    public string EntryStr { get; init; } = "int main(void)";
    public string ReturnType { get; init; } = "long";
    public IReadOnlyList<string>? GlobalList { get; init; }
}

/// <summary>Result of one generation: AllCode entries (last is ""), the text (each entry + CRLF) and the Function icons reached.</summary>
public sealed record CodeGenResult(IReadOnlyList<string> Lines, string Text, IReadOnlyList<GraphicObject> UsedFunctionIcons, IReadOnlyList<string> Includes);

/// <summary>Port of RoboExp.GraphicObjects.BuildCode (IconTransformCode / MY_IconTransformCode, BuildAllCode).</summary>
public static class CodeGenerator
{
    private static readonly SoftwareType[] JmLibTypes =
    [
        SoftwareType.E3RCU, SoftwareType.E6RCU, SoftwareType.E7RCU, SoftwareType.M7RCU, SoftwareType.M6RCU, SoftwareType.C6RCU,
        SoftwareType.C1RCU, SoftwareType.EV5, SoftwareType.MRCU, SoftwareType.M2RCU, SoftwareType.X1RCU, SoftwareType.X2RCU,
        SoftwareType.X3RCU, SoftwareType.X4RCU, SoftwareType.E2RCU, SoftwareType.IQrobot, SoftwareType.IQ2Robot,
    ];

    /// <summary>Types whose FlowApps use MY_IconTransformCode (every Start icon generates, later ones first).</summary>
    public static bool UsesMultiStart(SoftwareType type, ProjectType projectType) =>
        projectType == ProjectType.FlowApp && type is SoftwareType.E6RCU or SoftwareType.E7RCU or SoftwareType.M7RCU or SoftwareType.X4RCU or SoftwareType.E3RCU;

    /// <summary>FlowView.GetCodeText: generates and stores <c>CodeFrameData.AllCode</c>.</summary>
    public static CodeGenResult RegenerateAllCode(ProjectData data, FlowGraph graph, CodeGenOptions options)
    {
        var result = Generate(data, graph, options);
        (data.CodeFrameData ??= new()).AllCode = result.Text;
        return result;
    }

    /// <summary>
    /// IconTransformCode. Mutates what the original mutates: the computed <c>DataObject.Code</c> of control icons and the
    /// graph's runtime state (pairing, BeginLine/EndLine, IsInCode). Icon codes are refreshed before traversal, i.e. the
    /// output equals the original's steady state (it regenerates on every repaint).
    /// </summary>
    public static CodeGenResult Generate(ProjectData data, FlowGraph graph, CodeGenOptions options) =>
        new Run(data, graph, options).Execute();

    private sealed class Run(ProjectData data, FlowGraph graph, CodeGenOptions o)
    {
        private readonly List<string> _code = new();
        private readonly List<string> _include = new();
        private readonly List<GraphicObject> _used = new();
        private int _pointer;
        private readonly ProjectInfo _info = data.ProjectInfo ?? new ProjectInfo();

        public CodeGenResult Execute()
        {
            var type = o.Type;
            var projectType = _info.ProjectType;
            var isApp = projectType is ProjectType.FlowApp or ProjectType.CodeApp;
            graph.RefreshLineValidity();
            graph.InitLine();
            RefreshIconDataCode();
            if (o.OuterType != OuterType.Simulation) _include.Add("HardwareInfo");
            if (JmLibTypes.Contains(type) && isApp) _include.Add("JMLib");

            var num = 0;
            _code.Insert(num++, "{");
            if (isApp && InitLine(type) is { } init) _code.Insert(num++, init);
            _code.Insert(num, "}");
            var used = _info.CurUsedGlobalVarList;
            if (used is { Count: > 0 } && projectType == ProjectType.FlowFunc)
            {
                _code.Insert(num++, "    // extern global var");
                foreach (var v in used)
                {
                    if (v.IsParam) continue;
                    var text = "    extern " + v.Type + " " + v.Name + ";";
                    if (!string.IsNullOrEmpty(v.Description)) text += "   //" + v.Description;
                    _code.Insert(num++, text);
                }
                _code.Insert(num++, "");
            }
            foreach (var v in (IEnumerable<VarStruct>?)data.VarList ?? [])
            {
                if (v.IsParam) continue;
                var text = string.IsNullOrEmpty(v.DefaultValue)
                    ? "    " + v.Type + " " + v.Name + ";"
                    : "    " + v.Type + " " + v.Name + " = " + v.DefaultValue + ";";
                if (!string.IsNullOrEmpty(v.Description)) text += "   //" + v.Description;
                _code.Insert(num++, text);
            }

            var starts = graph.StartIcons.ToList();
            if (UsesMultiStart(type, projectType))
            {
                foreach (var start in starts)
                {
                    if (!EmitStart(start, num)) break;
                }
            }
            else if (starts.Count > 0)
            {
                EmitStart(starts[0], num);
            }
            var lines = BuildAllCode();
            var sb = new StringBuilder();
            foreach (var l in lines) sb.Append(l).Append("\r\n");
            return new CodeGenResult(lines, sb.ToString(), _used, _include);
        }

        /// <summary>Returns false when the Start port leads nowhere (the MY_ loop stops there).</summary>
        private bool EmitStart(GraphicObject start, int num)
        {
            var s = graph.State(start);
            s.IsInCode = true;
            s.BeginLine = 0;
            GraphicObject? next = null;
            foreach (var p in FlowGraph.PortsOf(start))
                if (p.Type == PortType.Start) next = graph.Follow(start, p);
            if (next == null) return false;
            _pointer = num;
            IconCode(next, 1);
            return true;
        }

        private static string? InitLine(SoftwareType t) => t switch
        {
            SoftwareType.X1RCU or SoftwareType.X2RCU or SoftwareType.X3RCU or SoftwareType.X4RCU or SoftwareType.E2RCU
                or SoftwareType.EV5 or SoftwareType.E3RCU or SoftwareType.E6RCU or SoftwareType.E7RCU or SoftwareType.M7RCU
                or SoftwareType.M6RCU or SoftwareType.C6RCU or SoftwareType.MRCU or SoftwareType.M2RCU => "    " + t + "_Init();",
            SoftwareType.C1RCU => "   C1RCU_Init();",
            SoftwareType.IQrobot or SoftwareType.IQ2Robot => "    IQRobot_Init();",
            _ => null,
        };

        private void RefreshIconDataCode()
        {
            var n = 0;
            foreach (var go in graph.Objects)
            {
                if (go.DrawObject is not IconDraw icon) continue;
                switch (icon.IconType)
                {
                    case IconType.BeginFor when go.DataObject is BeginForData f:
                    {
                        var v = "_FOR_" + n + "_";
                        f.Code = "for (int " + v + " = 0; " + v + " < " + f.LoopCountStr + "; " + v + "++ )";
                        n++;
                        break;
                    }
                    case IconType.BeginFor:
                        n++; // original would throw on a foreign payload; keep numbering stable
                        break;
                    case IconType.BeginIf when go.DataObject is BeginIfData d:
                        d.Code = d.Expression != null && d.Expression.Trim() != "" ? "if ( " + d.Expression + " )" : "if (1)";
                        break;
                    case IconType.BeginWhile when go.DataObject is BeginWhileData d:
                        d.Code = d.Expression != null && d.Expression.Trim() != "" ? "while ( " + d.Expression + " )" : "while (1)";
                        break;
                    case IconType.Break when go.DataObject != null:
                        go.DataObject.Code = "break;";
                        break;
                    case IconType.Continue when go.DataObject != null:
                        go.DataObject.Code = "continue;";
                        break;
                    case IconType.Calculate when go.DataObject is CalculateData d:
                        d.Code = d.Expression != null && d.Expression.Trim().Length != 0 ? d.Expression + ";" : "";
                        break;
                    case IconType.Return when go.DataObject is ReturnData d:
                        d.Code = d.Expression != null ? "return " + d.Expression + ";" : "return;";
                        break;
                }
            }
        }

        private static string Indent(int level) => level <= 0 ? "" : new string(' ', 4 * level);

        private void Insert(string line) => _code.Insert(_pointer, line);

        private void Continue(GraphicObject go, Port? port, int level)
        {
            if (port != null && graph.Follow(go, port) is { } next) IconCode(next, level);
        }

        private void IconCode(GraphicObject go, int level)
        {
            var indent = Indent(level);
            var s = graph.State(go);
            var type = FlowGraph.IconTypeOf(go);
            s.IsInCode = true;
            var data = go.DataObject;
            var desc = data?.Description;
            if (type is not (IconType.EndFor or IconType.EndIf or IconType.EndWhile) && desc is { Length: > 0 })
            {
                foreach (var d in desc)
                {
                    Insert(indent + "//" + d);
                    _pointer++;
                }
            }
            var code = data?.Code;
            switch (type)
            {
                case IconType.BeginIf:
                {
                    if (s.EndIf == null && s.EndElse == null || s.EndIf != null && s.EndElse != null && !ReferenceEquals(s.EndIf, s.EndElse))
                    {
                        s.IsInCode = false;
                        return;
                    }
                    s.BeginLine = _pointer + 1;
                    if (s.EndIf != null) graph.State(s.EndIf).BeginLine = _pointer + 1;
                    else if (s.EndElse != null) graph.State(s.EndElse).BeginLine = _pointer + 1;
                    Insert(indent + code);
                    _pointer++;
                    Insert(indent + "{");
                    _pointer++;
                    Insert(indent + "}");
                    if (s.EndIf != null) Continue(go, FlowGraph.FindPort(go, PortType.BeginIf), level + 1);
                    if (s.EndElse == null) return;
                    _pointer++;
                    Insert(indent + "else");
                    _pointer++;
                    Insert(indent + "{");
                    _pointer++;
                    Insert(indent + "}");
                    var elsePort = FlowGraph.FindPort(go, PortType.BeginElse);
                    if (elsePort != null && graph.Follow(go, elsePort) is { } next)
                    {
                        s.UseElse = true;
                        IconCode(next, level + 1);
                    }
                    return;
                }
                case IconType.BeginWhile or IconType.BeginFor:
                    if (s.EndElse != null && s.EndIf != null && !ReferenceEquals(s.EndIf, s.EndElse) || s.EndWhile == null)
                    {
                        s.IsInCode = false;
                        return;
                    }
                    s.BeginLine = _pointer + 1;
                    graph.State(s.EndWhile).BeginLine = _pointer + 1;
                    Insert(indent + code);
                    _pointer++;
                    Insert(indent + "{");
                    _pointer++;
                    Insert(indent + "}");
                    Continue(go, FlowGraph.GetOutPort(go), level + 1);
                    return;
                case IconType.CodeEditor:
                    s.BeginLine = _pointer + 1;
                    foreach (var line in (IEnumerable<string?>?)(data as CodeEditorData)?.Codes ?? [])
                    {
                        Insert(RemoveEndrn(indent + line));
                        _pointer++;
                    }
                    if (_pointer > s.BeginLine + 1) s.EndLine = _pointer;
                    Continue(go, FlowGraph.GetOutPort(go), level);
                    return;
                case IconType.EndIf:
                {
                    if (s.EndIfBeginElse != null && !graph.State(s.EndIfBeginElse).UseElse) return;
                    _pointer++;
                    EmitEndComments(desc, level);
                    s.EndLine = _pointer;
                    if (s.EndIfBeginIf != null) graph.State(s.EndIfBeginIf).EndLine = _pointer;
                    else if (s.EndIfBeginElse != null) graph.State(s.EndIfBeginElse).EndLine = _pointer;
                    Continue(go, FlowGraph.GetOutPort(go), level - 1);
                    return;
                }
                case IconType.EndWhile or IconType.EndFor:
                {
                    _pointer++;
                    EmitEndComments(desc, level);
                    s.EndLine = _pointer;
                    var begin = graph.Objects.FirstOrDefault(o => ReferenceEquals(graph.State(o).EndWhile, go));
                    if (begin == null) return;
                    graph.State(begin).EndLine = _pointer;
                    Continue(go, FlowGraph.GetOutPort(go), level - 1);
                    return;
                }
                case IconType.Function:
                {
                    _used.Add(go);
                    var name = data?.ProjectName;
                    if (!_include.Any(i => string.Equals(i, name, StringComparison.Ordinal))) _include.Add(name!);
                    break;
                }
            }
            s.BeginLine = _pointer + 1;
            Insert(indent + code);
            _pointer++;
            Continue(go, FlowGraph.GetOutPort(go), level);
        }

        private void EmitEndComments(string?[]? desc, int level)
        {
            if (desc is not { Length: > 0 }) return;
            var indent = Indent(level - 1);
            foreach (var d in desc)
            {
                Insert(indent + "//" + d);
                _pointer++;
            }
        }

        private static string RemoveEndrn(string str)
        {
            if (str.Length == 0) return "";
            var t = str;
            if (t[^1] is '\n' or '\r') t = t[..^1];
            if (t.Length > 0 && t[^1] is '\n' or '\r') t = t[..^1];
            return t;
        }

        private List<string> BuildAllCode()
        {
            var all = new List<string>();
            var global = data.CodeFrameData?.Global ?? "";
            var globalLines = SplitCrlf(global);
            for (var i = _include.Count - 1; i > -1; i--)
            {
                var file = (_include[i] + ".c").ToLower();
                if (globalLines.Any(l => l.ToLower().IndexOf(file, StringComparison.Ordinal) > 8)) _include.RemoveAt(i);
            }
            var num = 0;
            var name = _info.ProjectName ?? "";
            if (_info.ProjectType == ProjectType.FlowFunc)
            {
                all.Insert(num++, "#ifndef _" + name.ToUpperInvariant() + "_");
                all.Insert(num++, "#define _" + name.ToUpperInvariant() + "_");
                all.Insert(num, "#endif");
            }
            if (o.GlobalList != null)
            {
                foreach (var g in o.GlobalList) all.Insert(num, g);
                num++;
            }
            var chip = o.Type switch
            {
                SoftwareType.E6RCU or SoftwareType.M7RCU or SoftwareType.M6RCU or SoftwareType.C6RCU => "#include <stm32f4xx_hal.h>",
                SoftwareType.E7RCU => "#include <stm32h7xx_hal.h>",
                SoftwareType.X4RCU or SoftwareType.X3RCU => "#include <stm32f4xx.h>",
                SoftwareType.E3RCU => "#include <stm32f10x.h>",
                _ => null,
            };
            if (chip != null) all.Insert(num++, chip);
            foreach (var g in globalLines) all.Insert(num++, g);
            all.Insert(num++, "");
            foreach (var inc in _include)
            {
                if (string.Equals(inc, name, StringComparison.Ordinal)) continue;
                all.Insert(num++, o.IsSystemFunction?.Invoke(inc) == true ? "#include <" + inc + ".h>" : "#include \"" + inc + ".c\"");
            }
            var used = _info.CurUsedGlobalVarList;
            if (used is { Count: > 0 } && _info.ProjectType == ProjectType.FlowApp)
            {
                all.Insert(num++, "// define global var");
                foreach (var v in used)
                {
                    if (v.IsParam) continue;
                    var text = string.IsNullOrEmpty(v.DefaultValue) ? v.Type + " " + v.Name + ";" : v.Type + " " + v.Name + " = " + v.DefaultValue + ";";
                    if (!string.IsNullOrEmpty(v.Description)) text += "   //" + v.Description;
                    all.Insert(num++, text);
                }
                all.Insert(num++, "");
            }
            all.Insert(num++, _info.ProjectType switch
            {
                ProjectType.FlowApp or ProjectType.CodeApp => o.EntryStr,
                ProjectType.FlowFunc or ProjectType.CodeFunc => _info.GetFuncDeclareCodeLine(),
                _ => "",
            });
            foreach (var go in graph.Objects)
            {
                var s = graph.State(go);
                if (s.BeginLine > -1) s.BeginLine += num;
                if (s.EndLine > -1) s.EndLine += num;
            }
            foreach (var c in _code) all.Insert(num++, c);
            if (_info.ProjectType == ProjectType.FlowApp && o.ReturnType != "void") all.Insert(num - 1, "    while(1);");
            all.Add("");
            return all;
        }
    }

    /// <summary>BuildCode.ChangeStringArray: split on CRLF only, dropping a final empty piece.</summary>
    public static List<string> SplitCrlf(string str)
    {
        var list = new List<string>();
        int i;
        while ((i = str.IndexOf("\r\n", StringComparison.Ordinal)) > -1)
        {
            list.Add(str[..i]);
            str = str[(i + 2)..];
        }
        if (str != "") list.Add(str);
        return list;
    }
}
