using CommunityToolkit.Mvvm.ComponentModel;
using RoboExp.Core.Library;

namespace RoboExp.App.ViewModels;

/// <summary>
/// A code template (original SysFuncData.NodeData): <see cref="Code"/> lines are inserted at the caret, continuation
/// lines indented to the caret column; <see cref="IncludeLine"/> is added once near the top for library functions.
/// </summary>
public sealed record CodeTemplate(string Caption, string ToolTip, IReadOnlyList<string> Code, string? IncludeLine);

public sealed record CodeTemplateGroup(string Name, IReadOnlyList<CodeTemplate> Items);

/// <summary>Template Code window (original DockSysFunc filled by GetCodeTempleNodes): control statements, then library bands.</summary>
public sealed partial class TemplateCodeViewModel : ObservableObject
{
    [ObservableProperty]
    private IReadOnlyList<CodeTemplateGroup> _groups = [];

    [ObservableProperty]
    private CodeTemplateGroup? _selectedGroup;

    [ObservableProperty]
    private CodeTemplate? _selectedItem;

    /// <summary>Raised by Insert/double-click; the main window inserts into the active code document.</summary>
    public event EventHandler<CodeTemplate>? InsertRequested;

    public void RequestInsert(CodeTemplate? item)
    {
        if (item != null) InsertRequested?.Invoke(this, item);
    }

    public void Refresh(SystemLibrary? library, UiLanguage language)
    {
        var selected = SelectedGroup?.Name;
        var groups = new List<CodeTemplateGroup> { new("Control Icons", ControlTemplates(language)) };
        if (library != null)
        {
            foreach (var band in library.Bands)
            {
                var items = band.FunctionNames.Select(library.Find).OfType<LibFunction>()
                    .Select(f => FunctionTemplate(f, language, library.IsSystemFunction(f.Name))).ToList();
                groups.Add(new CodeTemplateGroup(band.Caption.Get(language), items));
            }
        }
        Groups = groups;
        SelectedGroup = groups.FirstOrDefault(g => g.Name == selected) ?? groups[0];
    }

    /// <summary>GetCodeTempleNodes.GetCodesTemple: <c>Name( type:name ,type:name )</c>.</summary>
    public static string CallTemplate(LibFunction f) =>
        f.Name + "( " + string.Join(" ,", f.Params.Select(p => p.Type + ":" + p.Name)) + " )";

    /// <summary>GetCodeTempleNodes.GetHintTemple: description, call template, return line, one line per parameter.</summary>
    public static IReadOnlyList<string> HintLines(LibFunction f, UiLanguage language)
    {
        var lines = new List<string>();
        var description = f.Description.Get(language);
        if (description.Trim().Length > 0) lines.Add(description);
        lines.Add(CallTemplate(f));
        var returnDescription = f.ReturnDescription.Get(language);
        if (!string.Equals(f.ReturnType.Trim(), "void", StringComparison.OrdinalIgnoreCase) && returnDescription.Trim().Length > 0)
            lines.Add("return: " + f.ReturnType + " ; " + returnDescription);
        lines.AddRange(f.Params.Select(p => p.Name + ": " + p.Type + "; " + p.Description.Get(language)));
        return lines;
    }

    private static CodeTemplate FunctionTemplate(LibFunction f, UiLanguage language, bool isSystem)
    {
        var caption = f.Caption.Get(language);
        return new CodeTemplate(caption.Length > 0 ? caption : f.Name, string.Join("\n", HintLines(f, language)), [CallTemplate(f)],
            isSystem ? "#include <" + f.Name + ".h>" : "#include \"" + f.Name + ".c\"");
    }

    /// <summary>C templates of GetCodeTempleNodes.BuildCtrlData, per culture.</summary>
    private static IReadOnlyList<CodeTemplate> ControlTemplates(UiLanguage language)
    {
        string Pick(string en, string chs, string cht) => language switch { UiLanguage.CHS => chs, UiLanguage.CHT => cht, _ => en };
        CodeTemplate T(string name, string code)
        {
            var lines = code.Split("\r\n");
            return new CodeTemplate(name, string.Join("\n", lines), lines, null);
        }
        return
        [
            T("if", Pick("if (condition)\r\n{\r\n\tstatements;\r\n}\r\nelse\r\n{\r\n\r\n}",
                "if (输入成立的条件)\r\n{\r\n\r\n}\r\nelse    // 不成立时\r\n{\r\n\r\n}",
                "if (輸入成立的條件)\r\n{\r\n\r\n}\r\nelse    // 不成立時\r\n{\r\n\r\n}")),
            T("while", Pick("while (condition)\r\n{\r\n\r\n}", "while (循环的条件)\r\n{\r\n\r\n}", "while (循環的條件)\r\n{\r\n\r\n}")),
            T("for", Pick("for (initialization; condition; update)\r\n{\r\n\r\n}",
                "for (循环开始时初始化语句; 循环的条件; 每次循环改变量的语句)\r\n{\r\n\r\n}",
                "for (循環開始時初始化語句; 循環的條件; 每次循環改變量的語句)\r\n{\r\n\r\n}")),
            T("continue", Pick("continue;", "continue;  // 转入下一次循环", "continue;  // 轉入下一次循環")),
            T("break", Pick("break;", "break;\t// 跳出循环", "break;\t// 跳出循環")),
            T("return", Pick("return;", "return 返回值;", "return 返回值;")),
        ];
    }
}
