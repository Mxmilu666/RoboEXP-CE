using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using RoboExp.App.Localization;
using RoboExp.App.Views;
using RoboExp.Core.Install;
using RoboExp.Core.Library;
using RoboExp.Core.Model;
using RoboExp.Core.Project;

namespace RoboExp.App.Subroutines;

/// <summary>
/// Entry points of the subroutine / library dialogs (SubManagerDlg, UpdateSubDlg + BaseView.UpdatePrivateModuleFromLib,
/// SystemLibDlg, MainForm.MenuRefSub). Each returns after its window closed.
/// </summary>
public static class SubroutineDialogs
{
    /// <summary>
    /// SubManagerDlg: the project's private subroutines. Open (closes the dialog, then <paramref name="openFile"/>),
    /// Delete (refused when <paramref name="isUsedByIcon"/>), Add (<see cref="ReferenceSubAsync"/>), Up/Down, caption rename
    /// (double-click or F2). Returns true when <see cref="ProjectSession.Data"/> changed.
    /// </summary>
    public static async Task<bool> ShowSubManagerAsync(Window owner, ProjectSession session, Func<string, Task> openFile,
        Func<ProjectInfo, bool> isUsedByIcon)
    {
        var window = new SubManagerWindow(session, isUsedByIcon);
        await window.ShowDialog(owner);
        if (window.OpenPath is { } path) await openFile(path);
        return window.Changed;
    }

    /// <summary>The private subroutines whose library copy differs (MainForm enables "Update Subroutine" when non-empty).</summary>
    public static IReadOnlyList<SubUpdate> PendingUpdates(ProjectSession session, UserLibrary? library = null)
    {
        library ??= UserLibrary.Load(session.Install, session.Library, Localizer.Instance.Language);
        return SubroutineOps.NeedUpdate(session.Data, library.GetProjectInfo, session.Library.IsSystemFunction);
    }

    /// <summary>
    /// BaseView.UpdatePrivateModuleFromLib: for each private subroutine whose user-library version differs, UpdateSubDlg
    /// asks Yes / All / No / None; accepted ones are copied into the project directory and replace the used entry
    /// (Function icons adapted). Returns true when anything was updated.
    /// </summary>
    /// <param name="library">The loaded library tree (the main window's); loaded from disk when null.</param>
    public static async Task<bool> ShowUpdateSubAsync(Window owner, ProjectSession session, UserLibrary? library = null)
    {
        var loc = Localizer.Instance;
        var updates = PendingUpdates(session, library?.Type == session.Type ? library : null);
        var changed = false;
        var answer = UpdateAnswer.No;
        foreach (var update in updates)
        {
            if (answer is UpdateAnswer.Yes or UpdateAnswer.No)
            {
                var dialog = new UpdateSubWindow(update.Library, update.Project);
                await dialog.ShowDialog(owner);
                answer = dialog.Answer;
            }
            if (answer is not (UpdateAnswer.Yes or UpdateAnswer.All)) continue;
            string? missing;
            try { missing = SubroutineOps.ApplyUpdate(session.File, session.RcuPath, update, session.Library.IsSystemFunction); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await MessageDialog.ShowAsync(owner, e.Message, loc.T("RoboExp"), MessageDialog.Buttons.Ok);
                continue;
            }
            if (missing != null)
            {
                await MessageDialog.ShowAsync(owner, loc.T("Tzw:SrcNotComplete") + "(" + missing + ")", loc.T("RoboExp"), MessageDialog.Buttons.Ok);
                continue;
            }
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// SystemLibDlg: edits the library tree (Include.xml + UserInclude.xml). OK saves UserInclude.xml when something
    /// changed and returns true; Cancel discards.
    /// </summary>
    public static async Task<bool> ShowSystemLibraryAsync(Window owner, VendorInstall install, RobotType type)
    {
        var loc = Localizer.Instance;
        SystemLibrary system;
        try { system = SystemLibrary.Load(install, type); }
        catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            await MessageDialog.ShowAsync(owner, loc.T("Port:LibraryLoadFailed") + e.Message, loc.T("RoboExp"), MessageDialog.Buttons.Ok);
            return false;
        }
        var library = UserLibrary.Load(install, system, loc.Language);
        var window = new SystemLibraryWindow(library);
        await window.ShowDialog(owner);
        if (!window.Accepted || !window.HasChange) return false;
        try { library.Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await MessageDialog.ShowAsync(owner, loc.T("Port:SaveFailed") + e.Message, loc.T("RoboExp"), MessageDialog.Buttons.Ok);
            return false;
        }
        return true;
    }

    /// <summary>
    /// MainForm.MenuRefSub_Click: pick a function .rcu and reference it in the project (copied next to the project by
    /// <see cref="ProjectSession.RefProjectInfo"/>). Asks before replacing an existing same-name sub file
    /// (ProjectData.RefProjectInfo). Returns true when the project changed.
    /// </summary>
    public static async Task<bool> ReferenceSubAsync(Window owner, ProjectSession session)
    {
        var loc = Localizer.Instance;
        var path = await PickRcuAsync(owner, null);
        if (path == null) return false;
        var info = UserLibrary.LoadFuncProjectInfo(path, out var err);
        if (info == null)
        {
            await MessageDialog.ShowAsync(owner, loc.T(UserLibrary.ErrorKey(err) ?? UserLibrary.ErrorOpenFailed), loc.T("RoboExp"), MessageDialog.Buttons.Ok);
            return false;
        }
        var name = info.ProjectName ?? "";
        if (session.Data.GetUsedProjectInfo(name) != null)
        {
            var existing = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(session.RcuPath))!, name + SubroutineOps.ProjectFileExtension);
            if (!string.Equals(session.Data.ProjectInfo?.ProjectName, name, StringComparison.Ordinal) && File.Exists(existing)
                && await MessageDialog.ShowAsync(owner, existing + " " + loc.T("AlreadyExist") + "\r\n" + loc.T("ReplaceIt"), loc.T("RoboExp"),
                    MessageDialog.Buttons.YesNo) != Services.ConfirmResult.Yes)
                return false;
        }
        try { session.RefProjectInfo(info); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await MessageDialog.ShowAsync(owner, e.Message, loc.T("RoboExp"), MessageDialog.Buttons.Ok);
            return false;
        }
        return true;
    }

    /// <summary>OpenFileDialog "RCU Files (*.RCU)|*.RCU".</summary>
    internal static async Task<string?> PickRcuAsync(Window owner, string? title)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("RCU Files (*.RCU)") { Patterns = ["*.rcu", "*.RCU"] }],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    /// <summary>The subroutine's original serialized artwork.</summary>
    internal static Bitmap? ToBitmap(EncodedImage? image) =>
        RoboExp.App.Flow.FlowImages.Decode(image);

    internal static Button DialogButton(string content, Action onClick, bool isDefault = false, bool isCancel = false)
    {
        var b = new Button { Content = content, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = isDefault, IsCancel = isCancel };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>The original InputBox (title, prompt, default text). Null on Cancel.</summary>
    internal static async Task<string?> InputAsync(Window owner, string title, string prompt, string defaultText)
    {
        var loc = Localizer.Instance;
        string? result = null;
        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            MinWidth = 320,
        };
        var text = new TextBox { Text = defaultText };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children =
            {
                DialogButton(loc["Options:btnOK.Text"], () => { result = text.Text ?? ""; window.Close(); }, isDefault: true),
                DialogButton(loc["Options:btnCancel.Text"], () => window.Close(), isCancel: true),
            },
        };
        window.Content = new StackPanel
        {
            Margin = new Thickness(12),
            Spacing = 8,
            Children = { new TextBlock { Text = prompt }, text, buttons },
        };
        window.Opened += (_, _) => { text.Focus(); text.SelectAll(); };
        await window.ShowDialog(owner);
        return result;
    }
}
