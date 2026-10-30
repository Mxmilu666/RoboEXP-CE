using AvaloniaEdit.Document;
using RoboExp.App.Documents;
using RoboExp.App.Services;
using RoboExp.Core.Build;
using RoboExp.Core.Model;
using RoboExp.Core.Project;

namespace RoboExp.App.Flow;

/// <summary>
/// A code project .rcu (original FrmCodeApp / FrmCodeFunc). CodeApp: one editor holding the text of &lt;name&gt;.c (the
/// original loads the .c next to the project; when it is missing the stored CodeFrameData.Code is shown); saving stores
/// it as CodeFrameData.Code and writes the .c. CodeFunc: three stacked editors — global area (CodeFrameData.Global),
/// the read-only declaration line, and the code area (CodeFrameData.Code); the .c is Global, blank line, declaration, Code.
/// </summary>
public sealed class CodeProjectDocumentViewModel : DocumentViewModel
{
    public CodeProjectDocumentViewModel(ProjectSession session)
    {
        Session = session;
        FilePath = session.RcuPath;
        var frame = session.Data.CodeFrameData ??= new CodeFrameData();
        if (IsFunc)
        {
            Include = new TextDocument(frame.Global ?? "");
            Declaration = new TextDocument(session.Info.GetFuncDeclareCodeLine());
            Code = new TextDocument(frame.Code ?? "");
        }
        else
        {
            var text = frame.Code ?? "";
            if (File.Exists(session.CPath)) text = TextFileCodec.Read(session.CPath).Text;
            Code = new TextDocument(text);
        }
        foreach (var doc in Documents)
        {
            doc.UndoStack.MarkAsOriginalFile();
            doc.TextChanged += (_, _) => IsDirty = Documents.Any(d => !d.UndoStack.IsOriginalFile);
        }
    }

    public override DocumentKind Kind => FlowDocumentViewModel.KindInfo;

    public ProjectSession Session { get; }
    public bool IsFunc => Session.Info.ProjectType == ProjectType.CodeFunc;

    /// <summary>CodeFunc global area (ScInclude); null for CodeApp.</summary>
    public TextDocument? Include { get; }

    /// <summary>CodeFunc declaration line (ScFunc, read-only); null for CodeApp.</summary>
    public TextDocument? Declaration { get; }

    /// <summary>ScCode.</summary>
    public TextDocument Code { get; }

    public IEnumerable<TextDocument> Documents => new[] { Include, Code }.OfType<TextDocument>();

    /// <summary>Navigation request for the view: (editor, 1-based line in that editor).</summary>
    public event EventHandler<(TextDocument Document, int Line)>? NavigationRequested;

    /// <summary>Navigation requested before the view attached.</summary>
    public (TextDocument Document, int Line)? PendingNavigation { get; set; }

    public override Task SaveAsync(string path)
    {
        var frame = Session.Data.CodeFrameData!;
        frame.Code = Code.Text;
        if (Include != null) frame.Global = Include.Text;
        if (string.Equals(Path.GetFullPath(path), Session.RcuPath, PathComparison)) Session.Save();
        else Session.SaveAs(path);
        if (Include != null) Include.Text = Session.Data.CodeFrameData!.Global ?? "";
        if (Declaration != null) Declaration.Text = Session.Info.GetFuncDeclareCodeLine();
        FilePath = Session.RcuPath;
        foreach (var doc in Documents) doc.UndoStack.MarkAsOriginalFile();
        IsDirty = false;
        return Task.CompletedTask;
    }

    public override async Task<BuildInput?> PrepareBuildAsync()
    {
        await SaveAsync(Session.RcuPath);
        var bin = Path.Combine(Session.ProjectDirectory, Session.Info.ProjectName + ".bin");
        return new BuildInput(Session.CPath, Session.IsApp ? BuildGroup.Main : BuildGroup.Func, bin);
    }

    /// <summary>
    /// BaseView.SelectLine: CodeApp lines map 1:1. CodeFunc maps the .c line back to its pane (global lines, then the
    /// blank separator and declaration, then the code area).
    /// </summary>
    public override bool TryNavigateTo(string file, int line, int? column)
    {
        string full;
        try { full = Path.GetFullPath(file.Replace('\\', Path.DirectorySeparatorChar), Session.ProjectDirectory); }
        catch (ArgumentException) { return false; }
        if (!string.Equals(full, Session.CPath, PathComparison) && !string.Equals(full, Session.RcuPath, PathComparison)) return false;
        var target = (Code, line);
        if (Include != null)
        {
            var includeLines = Include.Text.Split("\r\n").Length;
            if (line <= includeLines) target = (Include, line);
            else if (line <= includeLines + 1) target = (Include, includeLines);
            else if (line == includeLines + 2) target = (Declaration!, 1);
            else target = (Code, line - includeLines - 2);
        }
        PendingNavigation = target;
        NavigationRequested?.Invoke(this, target);
        return true;
    }
}
