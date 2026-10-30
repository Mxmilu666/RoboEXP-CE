using RoboExp.App.ViewModels;

namespace RoboExp.App.Services;

public enum MessageKind { Info, Warning, Error }

public enum ConfirmResult { Yes, No, Cancel }

/// <summary>A file-dialog filter: caption plus extensions without dot (e.g. "c", "h").</summary>
public sealed record FileFilter(string Name, IReadOnlyList<string> Extensions);

/// <summary>Modal UI used by view models; implemented by the main window.</summary>
public interface IDialogService
{
    Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FileFilter> filters, string? startDirectory = null);
    Task<string?> PickSaveFileAsync(string title, IReadOnlyList<FileFilter> filters, string suggestedName, string? startDirectory = null);
    Task<string?> PickFolderAsync(string title, string? startDirectory = null);
    Task ShowMessageAsync(string text, string title, MessageKind kind = MessageKind.Info);
    Task<ConfirmResult> ConfirmAsync(string text, string title, bool allowCancel);

    /// <summary>Modal progress (original CompileProgressDlg): runs <paramref name="work"/>; the Cancel button cancels its token.</summary>
    Task RunWithProgressAsync(string title, string tip, Func<CancellationToken, Task> work);

    Task ShowOptionsAsync(OptionsViewModel options);
    Task ShowAboutAsync(AboutViewModel about);
    Task ShowDownloadAsync(DownloadViewModel download);
}
