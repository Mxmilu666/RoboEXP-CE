using System.Text;
using System.Xml.Linq;
using AvaloniaEdit.Document;
using RoboExp.App.Services;
using RoboExp.Core.Build;
using RoboExp.Core.Install;
using RoboExp.App.ViewModels;
using RoboExp.Core.Library;

namespace RoboExp.App.Documents;

/// <summary>A C source/header edited as text (original FrmCodeApp/Scintilla editor), kept in its on-disk encoding.</summary>
public sealed class CodeDocumentViewModel : DocumentViewModel
{
    public static DocumentKind KindInfo { get; } = new("code", "C File", [".c", ".h"], (path, _) => Load(path), CreateNew);

    public CodeDocumentViewModel(string text, Encoding encoding, string? filePath)
    {
        Document = new TextDocument(text);
        Document.UndoStack.MarkAsOriginalFile();
        Encoding = encoding;
        FilePath = filePath;
        Document.UndoStack.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Document.UndoStack.IsOriginalFile)) IsDirty = !Document.UndoStack.IsOriginalFile;
        };
    }

    public override DocumentKind Kind => KindInfo;

    public TextDocument Document { get; }

    /// <summary>On-disk encoding; GBK unless the file had a BOM.</summary>
    public Encoding Encoding { get; private set; }

    /// <summary>Navigation requested before the view attached; the view consumes it on attach.</summary>
    public (int Line, int? Column)? PendingNavigation { get; set; }

    /// <summary>Raised to move the caret; the view scrolls/selects the line (original <c>BaseView.SelectLine</c>).</summary>
    public event EventHandler? NavigationRequested;

    /// <summary>Line to put the caret on when the view first attaches (original <c>ScCode.GotoLine(4)</c> for new files).</summary>
    public int? InitialCaretLine { get; private set; }

    public static CodeDocumentViewModel Load(string path)
    {
        var (text, encoding) = TextFileCodec.Read(path);
        return new CodeDocumentViewModel(text, encoding, Path.GetFullPath(path));
    }

    /// <summary>
    /// Original FrmCodeApp.InitCode: localized template comment, Code.xml GlobalStr lines for the robot type,
    /// then the entry point (Code.xml EntryStr, default <c>int main(void)</c>).
    /// </summary>
    public static CodeDocumentViewModel CreateNew(AppEnvironment env)
    {
        var comment = env.Language switch
        {
            UiLanguage.CHS => "//应用程序模板 ",
            UiLanguage.CHT => "//應用程序模板 ",
            _ => "//Application temple ",
        };
        var (globals, entry) = ReadCodeTemplate(env.Install, env.RobotType);
        var sb = new StringBuilder(comment).Append("\r\n");
        foreach (var g in globals) sb.Append(g).Append("\r\n");
        sb.Append("\r\n").Append(entry).Append("\r\n{\r\n\r\n}\r\n");
        return new CodeDocumentViewModel(sb.ToString(), TextFileCodec.Gbk, null) { InitialCaretLine = 4 };
    }

    internal static (IReadOnlyList<string> Globals, string EntryStr) ReadCodeTemplate(VendorInstall? install, RobotType type)
    {
        const string defaultEntry = "int main(void)";
        if (install == null) return ([], defaultEntry);
        var path = Path.Combine(install.ResourcesDirectory, "Code.xml");
        if (!File.Exists(path)) return ([], defaultEntry);
        var node = XDocument.Load(path).Root?.Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, type.Name, StringComparison.OrdinalIgnoreCase));
        if (node == null) return ([], defaultEntry);
        var globals = node.Elements().Where(e => string.Equals(e.Name.LocalName, "GlobalStr", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Value).ToList();
        return (globals, node.Element("EntryStr")?.Value ?? defaultEntry);
    }

    public override async Task SaveAsync(string path)
    {
        var version = Document.Version;
        var text = Document.Text;
        var encoding = Encoding;
        await Task.Run(() => TextFileCodec.Write(path, text, encoding));
        FilePath = Path.GetFullPath(path);
        // The editor stays usable while writing; newer edits must not be marked as saved.
        if (version.CompareAge(Document.Version) == 0)
        {
            Document.UndoStack.MarkAsOriginalFile();
            IsDirty = false;
        }
        else Document.UndoStack.DiscardOriginalFileMarker();
    }

    public override async Task<BuildInput?> PrepareBuildAsync()
    {
        if (FilePath == null || !FilePath.EndsWith(".c", StringComparison.OrdinalIgnoreCase)) return null;
        if (IsDirty) await SaveAsync(FilePath);
        var bin = Path.Combine(Path.GetDirectoryName(FilePath)!, Path.GetFileNameWithoutExtension(FilePath) + ".bin");
        return new BuildInput(FilePath, BuildGroup.Main, bin);
    }

    public override bool TryNavigateTo(string file, int line, int? column)
    {
        if (!IsFile(file)) return false;
        PendingNavigation = (line, column);
        NavigationRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Inserts text at the caret (template code window); handled by the view.</summary>
    public event EventHandler<CodeTemplate>? InsertRequested;

    public void InsertTemplate(CodeTemplate template) => InsertRequested?.Invoke(this, template);
}
