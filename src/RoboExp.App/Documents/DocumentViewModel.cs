using CommunityToolkit.Mvvm.ComponentModel;
using RoboExp.Core.Build;

namespace RoboExp.App.Documents;

/// <summary>What the Compile/Download commands build for a document (see <see cref="DocumentViewModel.PrepareBuildAsync"/>).</summary>
/// <param name="SourcePath">The C file handed to <see cref="Builder.BuildAsync"/>.</param>
/// <param name="Group">JMCompiler.xml group: applications build <see cref="BuildGroup.Main"/>, subroutines <see cref="BuildGroup.Func"/>.</param>
/// <param name="BinaryPath">Where the original expects the image: <c>&lt;project dir&gt;/&lt;project name&gt;.bin</c>.</param>
public sealed record BuildInput(string SourcePath, BuildGroup Group, string BinaryPath)
{
    /// <summary>Pre-compile warning (original CheckReturnHasSet line), shown before "CompilationSuccessful".</summary>
    public string? Warning { get; init; }
}

/// <summary>
/// Thrown by <see cref="DocumentViewModel.PrepareBuildAsync"/> when a pre-compile check stops the build (original
/// MainForm.Compile error lines); the message is shown in the Output window.
/// </summary>
public sealed class BuildRejectedException(string output) : Exception(output);

/// <summary>Undo/clipboard target of the active document; implemented by the document's view once it is attached.</summary>
public interface IEditCommands
{
    void Undo();
    void Redo();
    Task CutAsync();
    Task CopyAsync();
    Task PasteAsync();
    void Delete();
    void SelectAll();
    /// <summary>Opens find (or find-and-replace) for this document; no-op for documents without text search.</summary>
    void Find(bool replace);
}

/// <summary>
/// A tab in the document host (original <c>BaseView</c>: FlowView / FrmCodeApp / FrmCodeFunc).
/// Register new kinds with <see cref="DocumentKinds.Register"/> and their views with <see cref="ViewLocator.Register{T}"/>.
/// </summary>
public abstract partial class DocumentViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(ToolTip))]
    private string? _filePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private bool _isDirty;

    /// <summary>Set by the view while it is attached.</summary>
    public IEditCommands? EditTarget { get; set; }

    public abstract DocumentKind Kind { get; }

    /// <summary>Tab caption: file name (original <c>Path.GetFileName(ProjectPath)</c>), "*" when modified.</summary>
    public string Title => (FilePath == null ? UntitledName : Path.GetFileName(FilePath)) + (IsDirty ? " *" : "");

    public string ToolTip => FilePath ?? UntitledName;

    protected virtual string UntitledName => "Untitled" + Kind.Extensions[0];

    /// <summary>Writes the document to <paramref name="path"/> and adopts it as <see cref="FilePath"/>.</summary>
    public abstract Task SaveAsync(string path);

    /// <summary>
    /// Saves/generates whatever the compiler needs and returns it, or null when this document cannot be built
    /// (the caller reports CWRM "NoInputFile"). Code documents build themselves; flow documents generate C first.
    /// </summary>
    public virtual Task<BuildInput?> PrepareBuildAsync() => Task.FromResult<BuildInput?>(null);

    /// <summary>
    /// Moves the caret/selection to a compiler diagnostic if <paramref name="file"/> belongs to this document
    /// (its source or generated C file). Returns false when the diagnostic is not for this document.
    /// </summary>
    public virtual bool TryNavigateTo(string file, int line, int? column) => false;

    public bool IsFile(string path) =>
        FilePath != null && string.Equals(Path.GetFullPath(FilePath), Path.GetFullPath(path), PathComparison);

    /// <summary>Windows-origin paths: compare case-insensitively like the original.</summary>
    public static StringComparison PathComparison => StringComparison.OrdinalIgnoreCase;
}
