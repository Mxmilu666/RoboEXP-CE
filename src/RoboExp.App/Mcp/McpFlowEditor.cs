using System.Globalization;
using System.Text.Json;
using RoboExp.App.Flow;
using RoboExp.App.Flow.Editing;
using RoboExp.App.Properties;
using RoboExp.App.Variables;
using RoboExp.Core.Build;
using RoboExp.Core.CodeGen;
using RoboExp.Core.Library;
using RoboExp.Core.Model;
using RoboExp.Core.Model.Graphics;
using RoboExp.Core.Project;
using RoboExp.Core.Rcu;

namespace RoboExp.App.Mcp;

public sealed record McpFlowEditResult(object Snapshot, object Created);

/// <summary>
/// Explicit, revision-scoped flow operations. The caller must dispatch onto the UI thread and check its document
/// revision. A batch is applied to an NRBF round-trip clone, generated and serialized before one undoable adoption.
/// No operation reads a caller-supplied path or edits the generated C in place.
/// </summary>
public sealed class McpFlowEditor
{
    private static readonly IconType[] Controls =
    [
        IconType.Start, IconType.BeginIf, IconType.EndIf, IconType.BeginWhile, IconType.EndWhile,
        IconType.BeginFor, IconType.EndFor, IconType.Calculate, IconType.Continue, IconType.Break,
        IconType.Return, IconType.CodeEditor,
    ];

    public static JsonElement OperationsSchema { get; } = CreateSchema();

    public object Read(FlowDocumentViewModel doc) => Snapshot(doc.Session, doc.Code);

    /// <summary>Returns up to 40 matching system signatures, plus referenced user signatures.</summary>
    public object ListFunctions(FlowDocumentViewModel doc, string query)
    {
        var session = doc.Session;
        var matches = session.Library.Functions.Where(f => session.Library.IsSystemFunction(f.Name)
            && (f.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || f.Caption.Default.Contains(query, StringComparison.OrdinalIgnoreCase)
                || f.Description.Default.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(f => f.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ThenBy(f => f.Name, StringComparer.Ordinal).ToArray();
        return new
        {
            total = matches.Length, truncated = matches.Length > 40,
            functions = matches.Take(40).Select(f => new
            {
                name = f.Name, caption = f.Caption.Default, description = f.Description.Default, returnType = f.ReturnType,
                parameters = f.Params.Select((p, i) => new
                {
                    index = i, name = p.Name, type = p.Type, description = p.Description.Default,
                    defaultValue = p.Default, min = p.Min, max = p.Max, hardware = p.IsHardwareType,
                    candidates = ParameterCandidates(session, f.Name, new ParameterClass { Type = p.Type, IsHardwareType = p.IsHardwareType }),
                }).ToArray(),
            }).ToArray(),
            referencedUserFunctions = (session.Data.UsedProjectInfoList ?? [])
                .Where(p => !session.Library.IsSystemFunction(p.ProjectName ?? "")
                    && (p.ProjectName ?? "").Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(40).Select(p => new
                {
                    name = p.ProjectName, returnType = p.ReturnType?.Type,
                    parameters = (p.ParamList ?? []).Select((a, i) => new
                    {
                        index = i, name = a.Name, type = a.Type, description = a.Description,
                        defaultValue = a.DefaultValue, hardware = a.IsHardwareType,
                        candidates = ParameterCandidates(session, p.ProjectName, a),
                    }).ToArray(),
                }).ToArray(),
        };
    }

    public McpFlowEditResult Apply(FlowDocumentViewModel doc, JsonElement operations)
    {
        if (operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() is < 1 or > 256)
            throw Error("operations must be an array containing 1 to 256 operations.");

        var original = doc.Session.File;
        using var source = new MemoryStream();
        doc.Session.WriteRcu(source);
        source.Position = 0;
        var staged = ProjectSession.Create(RcuReader.Read(source), doc.Session.RcuPath,
            doc.Session.Install, doc.Session.Type);
        var batch = new Batch(staged, doc.Session, doc.Env.Language);
        var index = 0;
        foreach (var operation in operations.EnumerateArray())
        {
            try { batch.Apply(operation); }
            catch (ArgumentException e) { throw Error($"Operation {index}: {e.Message}"); }
            index++;
        }

        // Exercise generation and serialization before touching the document or undo stacks.
        batch.ValidateTopology();
        var generation = CodeGenerator.RegenerateAllCode(staged.Data, staged.Graph!, doc.Session.CodeGenOptions);
        PreCompileChecks.UpdateUsedHardwareInstance(staged.Data, generation.UsedFunctionIcons);
        using var serialized = new MemoryStream();
        staged.WriteRcu(serialized);
        var result = new McpFlowEditResult(Snapshot(staged, generation.Text), batch.Created());
        doc.Edit("MCP: Edit flow", () =>
        {
            try { doc.Session.Restore(staged.File); }
            catch
            {
                doc.Session.Restore(original);
                throw;
            }
        });
        doc.Wire.Reset();
        return result;
    }

    private static object Snapshot(ProjectSession session, string code)
    {
        var objects = session.DrawingObjects?.List;
        if (objects == null) throw Error("This document has no flow drawing objects.");
        var ids = new Dictionary<GraphicObject, string>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < objects.Count; i++) ids.Add(objects[i], NodeId(i));
        object? Endpoint(Port? port)
        {
            if (port?.Parent?.Parent is not { } owner || !ids.TryGetValue(owner, out var id)) return null;
            return new { node = id, port = port.Parent.Ports?.IndexOf(port) ?? -1 };
        }
        object Signature(ProjectInfo info) => new
        {
            name = info.ProjectName, caption = info.ProjectCaption, description = info.ProjectDescription,
            returnType = info.ReturnType?.Type,
            parameters = (info.ParamList ?? []).Select((p, i) => new
            {
                index = i, name = p.Name, type = p.Type, description = p.Description,
                defaultValue = p.DefaultValue, min = p.Min, max = p.Max,
                hardware = p.IsHardwareType, enumType = p.IsEnumType,
                candidates = ParameterCandidates(session, info.ProjectName, p),
            }).ToArray(),
        };
        var used = session.Data.UsedProjectInfoList ?? [];
        return new
        {
            idScope = "IDs n0, n1, ... belong only to this document revision. Read again after any edit/undo/redo.",
            projectType = session.Info.ProjectType.ToString(), robotType = session.Type.Name,
            generatedCode = code,
            nodes = objects.Select((go, i) => new
            {
                id = NodeId(i), kind = go.DrawObject switch
                {
                    IconDraw icon => icon.IconType.ToString(), LineDraw => "Line", TextBaseDraw => "Text", _ => "Unknown",
                },
                position = Position(go.DrawObject?.Position ?? Point.Empty),
                endPosition = go.DrawObject is LineDraw line ? Position(line.EndPosition) : null,
                size = go.DrawObject is { } draw ? new { width = draw.Size.Width, height = draw.Size.Height } : null,
                ports = (go.DrawObject?.Ports ?? []).Select((p, n) => new
                {
                    index = n, type = p.Type.ToString(), direction = p.PortDirection.ToString(),
                    position = Position(FlowGeometry.ConnectPoint(p)), connectedTo = Endpoint(p.ConnectedPort),
                }).ToArray(),
                data = Payload(go),
            }).ToArray(),
            variables = new
            {
                local = (session.Data.VarList ?? []).Select(Variable).ToArray(),
                global = (session.Info.CurUsedGlobalVarList ?? []).Select(Variable).ToArray(),
                types = VariableRules.Types(session.Type.SoftwareType),
                parameters = (session.Info.ParamList ?? []).Select(p => new { name = p.Name, type = p.Type }).ToArray(),
            },
            hardware = (session.Data.HardwareTable?.HardwareItemList ?? []).Select(item => new
            {
                type = item.HardwareType?.Name, description = item.HardwareType?.Description,
                instances = (item.InstanceList ?? []).Select(h => new
                {
                    name = h.Name, typeName = h.TypeName, typeNum = h.TypeNum,
                    controlPins = (h.PinTeam?.CtrlPinNumList ?? []).ToArray(),
                    groundPins = (h.PinTeam?.GNDPinNumList ?? []).ToArray(),
                    powerPins = (h.PinTeam?.VccPinNumList ?? []).ToArray(),
                }).ToArray(),
            }).ToArray(),
            usedFunctions = used.Select(Signature).ToArray(),
            editingNotes = "Only connected/reachable, structurally paired icons generate C. Begin controls auto-create an unconnected end " +
                "(created alias id:end); connect body/branch ports to its matching input. Disconnect removes the complete wire chain. " +
                "Remove does not reconnect neighbours or automatically delete paired controls. Variables are not textually renamed in C expressions. " +
                "Hardware configuration and project/function signatures are read-only. C/expression syntax is not compiled or executed. " +
                "Use list_functions to discover insertable function signatures and argument defaults; physical wire cycles are rejected (use Begin/End controls).",
        };
    }

    private static object Payload(GraphicObject go)
    {
        var d = go.DataObject;
        return new
        {
            comment = d?.GetDescription(), code = d?.Code, function = (d as FuncData)?.ProjectName,
            arguments = (d as FuncData)?.ValueList?.ToArray(), returnVariable = (d as FuncData)?.ReturnName,
            expression = d switch
            {
                BeginIfData v => v.Expression, BeginWhileData v => v.Expression,
                CalculateData v => v.Expression, ReturnData v => v.Expression, _ => null,
            },
            count = (d as BeginForData)?.LoopCountStr,
            codes = (d as CodeEditorData)?.Codes?.ToArray(), includes = (d as CodeEditorData)?.Includes?.ToArray(),
            text = (go.DrawObject as TextBaseDraw)?.Text,
        };
    }

    private static object Variable(VarStruct v) => new
    {
        name = v.Name, type = v.Type, defaultValue = v.DefaultValue, comment = v.Description, isParameter = v.IsParam,
    };

    private static object Position(Point p) => new { x = p.X, y = p.Y };
    private static string NodeId(int index) => "n" + index.ToString(CultureInfo.InvariantCulture);
    private static ArgumentException Error(string message) => new(message);

    private static IReadOnlyList<string> ParameterCandidates(ProjectSession session, string? function, ParameterClass p)
    {
        if (!p.IsHardwareType && !p.IsEnumType) return [];
        var fixedValues = HardwarePorts.FixedCandidates(session.Type.SoftwareType, function, p, docked: true);
        return fixedValues ?? HardwarePorts.TableInstances(session.Data.HardwareTable, p);
    }

    private sealed class Batch(ProjectSession session, ProjectSession live, UiLanguage language)
    {
        private readonly Dictionary<string, GraphicObject> _nodes = session.DrawingObjects!.List!
            .Select((go, i) => (go, id: NodeId(i))).ToDictionary(x => x.id, x => x.go, StringComparer.Ordinal);
        private readonly Dictionary<string, GraphicObject> _created = new(StringComparer.Ordinal);
        private ArrayList<GraphicObject> Objects => session.DrawingObjects!.List!;
        private FlowEditor Editor => new(Objects, new List<GraphicObject>());

        public object Created() => _created.Where(p => Objects.Contains(p.Value))
            .ToDictionary(p => p.Key, p => NodeId(Objects.IndexOf(p.Value)), StringComparer.Ordinal);

        public void ValidateTopology()
        {
            // Structured loops use Begin/End pairs, not physical wire cycles; core generation walks icons recursively.
            var icons = Objects.Where(o => o.DrawObject is IconDraw).ToArray();
            var edges = icons.ToDictionary(o => o, _ => new List<GraphicObject>());
            var incoming = icons.ToDictionary(o => o, _ => 0);
            foreach (var owner in icons)
            foreach (var port in owner.DrawObject!.Ports ?? [])
            {
                if (port.Type is not (PortType.Out or PortType.Start or PortType.BeginIf or PortType.BeginElse or PortType.BeginFor or PortType.BeginWhile)) continue;
                var next = port.ConnectedPort;
                var visited = new HashSet<LineDraw>(ReferenceEqualityComparer.Instance);
                while (next?.Parent is LineDraw line)
                {
                    if (!visited.Add(line)) throw Error("Physical wire cycles are unsupported; use paired Begin/End controls for loops.");
                    if (line.Ports is not { Count: 2 } ports) throw Error("A wire must have exactly two ports.");
                    next = (ReferenceEquals(ports[0], next) ? ports[1] : ports[0]).ConnectedPort;
                }
                if (next?.Parent?.Parent is not { } target || !incoming.ContainsKey(target)) continue;
                edges[owner].Add(target);
                incoming[target]++;
            }
            var pending = new Queue<GraphicObject>(icons.Where(o => incoming[o] == 0));
            var count = 0;
            while (pending.TryDequeue(out var current))
            {
                count++;
                foreach (var target in edges[current]) if (--incoming[target] == 0) pending.Enqueue(target);
            }
            if (count != icons.Length) throw Error("Physical flow cycles are unsupported; use paired Begin/End controls for loops.");
        }

        public void Apply(JsonElement op)
        {
            if (op.ValueKind != JsonValueKind.Object) throw Error("An operation must be an object.");
            var name = String(op, "op");
            switch (name)
            {
                case "add_control":
                {
                    Members(op, "op", "id", "kind", "x", "y");
                    var id = NewId(op);
                    var kind = String(op, "kind");
                    if (!Controls.Any(t => t.ToString() == kind)) throw Error("Unsupported control kind: " + kind);
                    var type = Enum.Parse<IconType>(kind);
                    if (type == IconType.Start && Objects.Count(o => o.DrawObject is IconDraw { IconType: IconType.Start }) >= 8)
                        throw Error("At most eight Start nodes are supported.");
                    Add(id, Control(type, PointOf(op)));
                    if (FlowFactory.PairEndType(type) is { } end)
                    {
                        var pos = PointOf(op);
                        Add(id + ":end", Control(end, new Point(pos.X + FlowGeometry.IconWidth * 3, pos.Y)));
                    }
                    break;
                }
                case "add_function":
                {
                    Members(op, "op", "id", "function", "x", "y");
                    var id = NewId(op);
                    var function = String(op, "function");
                    var info = session.Data.GetUsedProjectInfo(function);
                    if (info == null)
                    {
                        var libraryFunction = live.Library.Find(function);
                        if (libraryFunction == null || !live.Library.IsSystemFunction(function))
                            throw Error("Function must be a listed system function or an already referenced user function.");
                        info = LibraryProjectInfo.FromLibFunction(libraryFunction, live.Install, live.Type, language);
                        (session.Data.UsedProjectInfoList ??= new()).Add(info);
                    }
                    Add(id, FlowFactory.NewIcon(IconType.Function, PointOf(op), info.ProjectIcon, FuncCallText.CreateFuncData(info)));
                    break;
                }
                case "add_text":
                    Members(op, "op", "id", "x", "y", "text");
                    Add(NewId(op), FlowFactory.NewText(PointOf(op), String(op, "text")));
                    break;
                case "set_code":
                {
                    Members(op, "op", "node", "code", "includes");
                    var data = Node(op).DataObject as CodeEditorData ?? throw Error("set_code requires a CodeEditor node.");
                    data.Codes = Lines(String(op, "code"));
                    if (op.TryGetProperty("includes", out var includes))
                    {
                        if (includes.ValueKind != JsonValueKind.Array) throw Error("includes must be an array of strings.");
                        var lines = new ArrayList<string?>();
                        foreach (var value in includes.EnumerateArray()) lines.Add(Text(value));
                        data.Includes = lines;
                    }
                    break;
                }
                case "set_expression":
                {
                    Members(op, "op", "node", "expression");
                    var value = String(op, "expression");
                    switch (Node(op).DataObject)
                    {
                        case BeginIfData d: d.Expression = value; break;
                        case BeginWhileData d: d.Expression = value; break;
                        case CalculateData d: d.Expression = value; break;
                        case ReturnData d: d.Expression = value; break;
                        default: throw Error("set_expression requires BeginIf, BeginWhile, Calculate or Return.");
                    }
                    break;
                }
                case "set_for":
                {
                    Members(op, "op", "node", "count");
                    var data = Node(op).DataObject as BeginForData ?? throw Error("set_for requires BeginFor.");
                    var count = String(op, "count");
                    if (string.IsNullOrWhiteSpace(count)) throw Error("count must be a nonempty C count expression.");
                    data.LoopCountStr = count;
                    if (int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) data.LoopCount = number;
                    break;
                }
                case "set_arguments":
                    Members(op, "op", "node", "arguments");
                    Arguments(Node(op), Required(op, "arguments"));
                    break;
                case "set_return":
                {
                    Members(op, "op", "node", "variable");
                    var go = Node(op);
                    var data = go.DataObject as FuncData ?? throw Error("set_return requires Function.");
                    var info = Callee(data);
                    var value = String(op, "variable");
                    if (value.Length > 0 && info.ReturnType?.Type is (null or "" or "void"))
                        throw Error("This function has no return value.");
                    if (value.Length > 0 && !KnownVariable(value)) throw Error("Return variable must be a declared variable/parameter or empty.");
                    data.ReturnName = value;
                    FuncCallText.SetCodeStr(data, info);
                    break;
                }
                case "set_comment":
                {
                    Members(op, "op", "node", "comment");
                    var data = Node(op).DataObject ?? throw Error("Node has no comment payload.");
                    var value = String(op, "comment");
                    data.Description = value.Length == 0 ? null : Split(value);
                    break;
                }
                case "set_text":
                    Members(op, "op", "node", "text");
                    var text = Node(op).DrawObject as TextBaseDraw ?? throw Error("set_text requires a Text node.");
                    text.Text = String(op, "text");
                    break;
                case "move":
                {
                    Members(op, "op", "node", "x", "y");
                    var node = Node(op);
                    var draw = node.DrawObject ?? throw Error("Node has no drawing.");
                    var target = PointOf(op);
                    var editor = new FlowEditor(Objects, new List<GraphicObject> { node });
                    editor.MoveSelection(target.X - draw.Position.X, target.Y - draw.Position.Y);
                    break;
                }
                case "remove":
                {
                    Members(op, "op", "node");
                    var go = Node(op);
                    if (go.DrawObject is IconDraw { IconType: IconType.Start } &&
                        Objects.Count(o => o.DrawObject is IconDraw { IconType: IconType.Start }) <= 1)
                        throw Error("The last Start node cannot be removed.");
                    if (go.DrawObject is LineDraw line) RemoveWire(line);
                    else
                    {
                        foreach (var port in (go.DrawObject?.Ports ?? []).ToArray()) Disconnect(port);
                        Editor.RemoveGraphicObject(go);
                    }
                    break;
                }
                case "connect":
                    Members(op, "op", "from", "fromPort", "to", "toPort");
                    Connect(Port(op, "from", "fromPort"), Port(op, "to", "toPort"));
                    break;
                case "disconnect":
                    Members(op, "op", "node", "port");
                    Disconnect(Port(op, "node", "port"));
                    break;
                case "set_variable":
                    Members(op, "op", "scope", "name", "type", "defaultValue", "comment");
                    SetVariable(op);
                    break;
                case "remove_variable":
                {
                    Members(op, "op", "scope", "name");
                    var list = VariableList(String(op, "scope"));
                    var variable = list.FirstOrDefault(v => v.Name == String(op, "name")) ?? throw Error("Variable does not exist.");
                    if (variable.IsParam) throw Error("Function parameters cannot be removed by a variable operation.");
                    list.Remove(variable);
                    break;
                }
                default: throw Error("Unknown operation: " + name);
            }
        }

        private GraphicObject Control(IconType type, Point point) => FlowFactory.NewIcon(type, point,
            FlowImages.ControlIconImage(type, Objects), FlowFactory.NewControlData(type));

        private string NewId(JsonElement op)
        {
            var id = String(op, "id");
            if (!ValueCheck.IsRightVarName(id) || id.StartsWith('n') && id.Length > 1 && id[1..].All(char.IsDigit))
                throw Error("New id must be an identifier other than revision IDs n0, n1, ... (e.g. block).");
            if (_nodes.ContainsKey(id) || _nodes.ContainsKey(id + ":end")) throw Error("Duplicate node alias: " + id);
            return id;
        }

        private void Add(string id, GraphicObject go)
        {
            if (_nodes.ContainsKey(id)) throw Error("Duplicate node alias: " + id);
            Objects.Add(go);
            _nodes.Add(id, go);
            _created.Add(id, go);
        }

        private GraphicObject Find(string id) => _nodes.TryGetValue(id, out var go) && Objects.Contains(go)
            ? go : throw Error("Unknown or removed node ID: " + id);
        private GraphicObject Node(JsonElement op) => Find(String(op, "node"));

        private Port Port(JsonElement op, string nodeProperty, string portProperty)
        {
            var node = Find(String(op, nodeProperty));
            var index = Integer(op, portProperty);
            var ports = node.DrawObject?.Ports;
            if (ports == null || index < 0 || index >= ports.Count) throw Error("Port index is out of range.");
            return ports[index];
        }

        private void Connect(Port from, Port to)
        {
            if (from.Parent is not IconDraw || to.Parent is not IconDraw || ReferenceEquals(from.Parent, to.Parent))
                throw Error("connect requires ports on two different icon nodes, not wire segments or text.");
            if (from.ConnectedPort != null || to.ConnectedPort != null) throw Error("Both ports must be free; disconnect the old wire first.");
            if (from.Type is not (PortType.Out or PortType.Start or PortType.BeginIf or PortType.BeginElse or PortType.BeginFor or PortType.BeginWhile)
                || to.Type is not (PortType.In or PortType.EndIf or PortType.EndElse or PortType.EndFor or PortType.EndWhile))
                throw Error("Connect from an outgoing/body/branch port to an incoming/end port.");
            var a = FlowGeometry.ConnectPoint(from);
            var b = FlowGeometry.ConnectPoint(to);
            var points = new List<Point> { a };
            if (a.X != b.X && a.Y != b.Y)
            {
                var mid = a.X + (b.X - a.X) / 2;
                points.Add(new Point(mid, a.Y));
                points.Add(new Point(mid, b.Y));
            }
            points.Add(b);
            Port previous = from;
            for (var i = 1; i < points.Count; i++)
            {
                var go = FlowFactory.NewLine(points[i - 1], points[i]);
                Objects.Add(go);
                var ports = go.DrawObject!.Ports!;
                FlowEditor.ConnectTwoPorts(previous, ports[0]);
                previous = ports[1];
            }
            FlowEditor.ConnectTwoPorts(previous, to);
        }

        private void Disconnect(Port port)
        {
            if (port.Parent is LineDraw own) RemoveWire(own);
            else if (port.ConnectedPort?.Parent is LineDraw connected) RemoveWire(connected);
            else FlowEditor.DisconnectTwoPorts(port, port.ConnectedPort);
        }

        private void RemoveWire(LineDraw first)
        {
            // Bounded graph walk also handles imported cyclic/dangling wire chains without core traversal loops.
            var visited = new HashSet<LineDraw>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<LineDraw>();
            pending.Push(first);
            while (pending.TryPop(out var line))
            {
                if (!visited.Add(line)) continue;
                foreach (var port in line.Ports ?? [])
                    if (port.ConnectedPort?.Parent is LineDraw next) pending.Push(next);
            }
            foreach (var line in visited)
            {
                foreach (var port in line.Ports ?? []) FlowEditor.DisconnectTwoPorts(port, port.ConnectedPort);
                if (line.Parent is { } go) Editor.RemoveGraphicObject(go);
            }
        }

        private ProjectInfo Callee(FuncData data) => session.Data.GetUsedProjectInfo(data.ProjectName ?? "")
            ?? throw Error("Function signature is missing from this project; its arguments cannot be safely edited.");

        private void Arguments(GraphicObject go, JsonElement arguments)
        {
            var data = go.DataObject as FuncData ?? throw Error("set_arguments requires Function.");
            var info = Callee(data);
            if (arguments.ValueKind != JsonValueKind.Object || !arguments.EnumerateObject().Any())
                throw Error("arguments must be a nonempty map of parameter names to C expression strings.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var arg in arguments.EnumerateObject())
            {
                if (!seen.Add(arg.Name)) throw Error("Duplicate argument: " + arg.Name);
                var parameters = info.ParamList ?? [];
                var index = -1;
                for (var i = 0; i < parameters.Count; i++) if (parameters[i].Name == arg.Name) { index = i; break; }
                if (index < 0) throw Error("Unknown parameter: " + arg.Name);
                var parameter = parameters[index];
                var value = Text(arg.Value);
                if (parameter.IsHardwareType || parameter.IsEnumType)
                {
                    var candidates = ParameterCandidates(session, info.ProjectName, parameter);
                    if (!candidates.Contains(value, StringComparer.Ordinal))
                        throw Error($"Parameter {arg.Name} must be one of its snapshot candidates (hardware values are not arbitrary C expressions).");
                }
                else if (string.IsNullOrWhiteSpace(value)) throw Error("Argument expression cannot be empty.");
                data.ValueList ??= new();
                while (data.ValueList.Count <= index) data.ValueList.Add("");
                data.ValueList[index] = value;
            }
            FuncCallText.SetCodeStr(data, info);
        }

        private bool KnownVariable(string name) => (session.Data.VarList ?? []).Any(v => v.Name == name)
            || (session.Info.CurUsedGlobalVarList ?? []).Any(v => v.Name == name)
            || (session.Info.ParamList ?? []).Any(p => p.Name == name);

        private EventArrayList<VarStruct> VariableList(string scope) => scope switch
        {
            "local" => session.Data.VarList ??= new(),
            "global" => session.Info.CurUsedGlobalVarList ??= new(),
            _ => throw Error("scope must be local or global."),
        };

        private void SetVariable(JsonElement op)
        {
            var scope = String(op, "scope");
            var list = VariableList(scope);
            var name = String(op, "name");
            var other = VariableList(scope == "local" ? "global" : "local");
            var existing = list.FirstOrDefault(v => v.Name == name);
            if (existing?.IsParam == true) throw Error("Function parameters cannot be changed by a variable operation.");
            var error = VariableRules.CheckName(list, existing == null ? -1 : list.IndexOf(existing), other, name,
                scope == "local" ? live.Library.IsSystemFunction : null);
            if (error != VariableRules.NameError.None || (session.Info.ParamList ?? []).Any(p => p.Name == name))
                throw Error("Invalid or already reserved variable name: " + name);
            var type = String(op, "type");
            var value = String(op, "defaultValue");
            if (!VariableRules.Types(session.Type.SoftwareType).Contains(type)) throw Error("Variable type must be listed in snapshot variables.types.");
            if (!VariableRules.IsValidDefault(type, value, session.Type.SoftwareType)) throw Error("Variable default does not match its type.");
            var variable = existing ?? new VarStruct { Name = name };
            variable.Type = type;
            variable.DefaultValue = value;
            if (op.TryGetProperty("comment", out var comment)) variable.Description = Text(comment);
            if (existing == null) list.Add(variable);
        }
    }

    private static JsonElement Required(JsonElement op, string property) => op.TryGetProperty(property, out var value)
        ? value : throw Error("Missing property: " + property);
    private static string String(JsonElement op, string property) => Text(Required(op, property));
    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw Error("Expected a string.");
    private static int Integer(JsonElement op, string property)
    {
        var element = Required(op, property);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value) || value is < -1000000 or > 1000000)
            throw Error(property + " must be an integer between -1000000 and 1000000.");
        return value;
    }
    private static Point PointOf(JsonElement op) => new(Integer(op, "x"), Integer(op, "y"));
    private static string[] Split(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    private static ArrayList<string?> Lines(string value)
    {
        var lines = new ArrayList<string?>();
        foreach (var line in Split(value)) lines.Add(line);
        return lines;
    }
    private static void Members(JsonElement op, params string[] allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in op.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw Error("Unknown or duplicate property: " + property.Name);
    }

    private static JsonElement CreateSchema()
    {
        object Str(string description) => new { type = "string", description };
        object Choice(params string[] choices) => new Dictionary<string, object> { ["type"] = "string", ["enum"] = choices };
        object IntegerSchema(string description) => new { type = "integer", minimum = -1000000, maximum = 1000000, description };
        var id = Str("Batch-local identifier for a newly added node (e.g. block); not n followed by digits. Use it in later operations. Begin controls also create id:end.");
        var node = Str("Opaque n# ID from the expected-revision snapshot, or an alias added earlier in this batch. IDs do not shift within the batch.");
        var x = IntegerSchema("World-coordinate top-left x (line start x for move).");
        var y = IntegerSchema("World-coordinate top-left y (line start y for move).");
        var port = new { type = "integer", minimum = 0, description = "Snapshot port index. New Function/Calculate/Continue/Break/Return/CodeEditor: 0=In,1=Out. Start:0=Start. BeginIf:0=In,1=BeginIf,2=BeginElse. EndIf:0=EndIf,1=EndElse,2=Out. BeginWhile/BeginFor:0=In,1=body. EndWhile/EndFor:0=end,1=Out." };
        var operations = new List<object>();
        void Op(string name, string description, Dictionary<string, object> fields, string[]? optional = null)
        {
            fields.Add("op", new { type = "string", @const = name });
            operations.Add(new
            {
                type = "object", description, additionalProperties = false, properties = fields,
                required = fields.Keys.Where(k => optional == null || !optional.Contains(k)).ToArray(),
            });
        }
        Op("add_control", "Add a control. BeginIf/BeginWhile/BeginFor also add an unconnected matching end at x+96, alias id:end. Connect ports explicitly; Start limit is eight.",
            new() { ["id"] = id, ["kind"] = Choice(Controls.Select(t => t.ToString()).ToArray()), ["x"] = x, ["y"] = y });
        Op("add_function", "Add a listed system function or already referenced user function, with signature defaults. No new file imports.",
            new() { ["id"] = id, ["function"] = Str("Exact name returned by list_functions or snapshot usedFunctions."), ["x"] = x, ["y"] = y });
        Op("add_text", "Add a canvas annotation (not executable code).", new() { ["id"] = id, ["x"] = x, ["y"] = y, ["text"] = Str("Annotation text.") });
        Op("set_code", "Replace inline C in a CodeEditor node, not generated program C. Optional includes preserves the stored include list when omitted.",
            new() { ["node"] = node, ["code"] = Str("Multiline C source, normalized to stored lines."), ["includes"] = new { type = "array", items = Str("Stored include entry.") } }, ["includes"]);
        Op("set_expression", "Replace the condition/calculation/return expression in BeginIf, BeginWhile, Calculate or Return; syntax is not compiled.",
            new() { ["node"] = node, ["expression"] = Str("C expression; Calculate appends a semicolon, Return prefixes return.") });
        Op("set_for", "Set a BeginFor loop count expression.", new() { ["node"] = node, ["count"] = Str("Nonempty C count expression, e.g. 8 or repeatCount.") });
        Op("set_arguments", "Patch function arguments by exact signature parameter names. Unspecified args are preserved. Base parameters accept C expressions; hardware/enum parameters require snapshot candidates.",
            new() { ["node"] = node, ["arguments"] = new { type = "object", minProperties = 1, additionalProperties = Str("C expression or an exact hardware/enum candidate.") } });
        Op("set_return", "Choose a declared result variable for a non-void function, or clear its assignment.", new() { ["node"] = node, ["variable"] = Str("Declared variable/parameter name, or empty.") });
        Op("set_comment", "Replace model comment lines on a node.", new() { ["node"] = node, ["comment"] = Str("Multiline comment without // prefixes; empty clears.") });
        Op("set_text", "Replace canvas annotation text.", new() { ["node"] = node, ["text"] = Str("Annotation text.") });
        Op("move", "Move a node to absolute coordinates, maintaining and rerouting attached wires using the app editor.", new() { ["node"] = node, ["x"] = x, ["y"] = y });
        Op("remove", "Remove a node and its attached wire chains, or remove a whole wire chain when node is a line. Does not reconnect neighbours or remove paired control nodes. Cannot remove the last Start.", new() { ["node"] = node });
        Op("connect", "Connect free outgoing and incoming ports of two different icons using an orthogonal wire. Does not overwrite existing links; disconnect first.",
            new() { ["from"] = node, ["fromPort"] = port, ["to"] = node, ["toPort"] = port });
        Op("disconnect", "Remove the entire wire chain attached to a port (or containing a line port). A free icon port is a no-op.", new() { ["node"] = node, ["port"] = port });
        Op("set_variable", "Create or update a variable by scope and name, preserving other model fields. Does not rename existing code references. Function parameters are protected.",
            new() { ["scope"] = Choice("local", "global"), ["name"] = Str("Valid unique C variable identifier."), ["type"] = Str("One of snapshot variables.types."),
                ["defaultValue"] = Str("Type-compatible literal or empty."), ["comment"] = Str("Variable description; preserved if omitted.") }, ["comment"]);
        Op("remove_variable", "Remove an existing variable. References in expressions/C are not rewritten. Function parameters are protected.",
            new() { ["scope"] = Choice("local", "global"), ["name"] = Str("Existing variable name.") });
        return JsonSerializer.SerializeToElement(new
        {
            type = "array", minItems = 1, maxItems = 256,
            description = "Atomic ordered flow-edit batch, applied to the expected document revision and recorded as one app undo step. Refer to new nodes using their id aliases. Read snapshot again after success.",
            items = new { oneOf = operations },
        });
    }
}
