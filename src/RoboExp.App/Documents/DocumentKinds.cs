using RoboExp.App.Services;

namespace RoboExp.App.Documents;

/// <summary>
/// A file type the document host can open.
/// </summary>
/// <param name="Id">Stable identifier, e.g. "code", "flow".</param>
/// <param name="FilterName">File-dialog filter caption, e.g. "C File".</param>
/// <param name="Open">Loads a document from disk, on the UI thread (view models may own thread-affine models); I/O exceptions are reported to the user as CWRM "OpenFileFaild".</param>
/// <param name="CreateNew">Creates an untitled document for File → New, or null when the kind has its own new-project flow.</param>
/// <param name="ConfirmOpen">Optional question asked before <paramref name="Open"/> (e.g. MainForm.Open's robot-type mismatch warning); false cancels.</param>
public sealed record DocumentKind(
    string Id,
    string FilterName,
    IReadOnlyList<string> Extensions,
    Func<string, AppEnvironment, DocumentViewModel> Open,
    Func<AppEnvironment, DocumentViewModel>? CreateNew = null,
    Func<string, AppEnvironment, IDialogService, Task<bool>>? ConfirmOpen = null);

/// <summary>Registry of openable document kinds. Registration happens once in <see cref="AppComposition"/>.</summary>
public static class DocumentKinds
{
    private static readonly List<DocumentKind> Registered = [];

    public static IReadOnlyList<DocumentKind> All => Registered;

    public static void Register(DocumentKind kind)
    {
        if (Registered.Any(k => k.Id == kind.Id)) throw new InvalidOperationException($"Document kind '{kind.Id}' already registered");
        Registered.Add(kind);
    }

    public static DocumentKind? ForPath(string path)
    {
        var ext = Path.GetExtension(path);
        return Registered.FirstOrDefault(k => k.Extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)));
    }
}
