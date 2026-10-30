using System.Collections.ObjectModel;
using System.Net;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoboExp.App.Localization;
using RoboExp.Core.Install;
using RoboExp.Device;

namespace RoboExp.App.ViewModels;

public sealed record DownloadTargetItem(DownloadTarget Target, IDownloader Downloader)
{
    public string Label => Localizer.Instance.T("Port:" + Target.Kind) + " — " + Target.DisplayName;
}

/// <summary>
/// Original-style waiting dialog: automatically detects USB/HID controllers; explicit transports remain in Advanced.
/// </summary>
public sealed partial class DownloadViewModel : ObservableObject
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IReadOnlyList<IDownloader> _downloaders;
    private readonly List<DownloadTargetItem> _manualTargets = [];
    private CancellationTokenSource? _downloadCts;

    public DownloadViewModel(string binPath, RobotType type, IReadOnlyList<IDownloader> downloaders)
    {
        BinPath = binPath;
        RobotType = type;
        _downloaders = downloaders;
        Wifi = downloaders.OfType<WifiDownloader>().FirstOrDefault();
        _pairCode = Wifi?.PairCode ?? "";
        var info = new FileInfo(binPath);
        FileInfoText = $"{Path.GetFileName(binPath)}  {info.Length:N0} B  {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
        FileSizeText = Localizer.Instance.T("DownloadX1RCU:statusBarPanel1.Text") + " " + info.Length + " (Bytes)";
        FileTimeText = Localizer.Instance.T("DownloadX1RCU:statusBarPanel2.Text") + " " + info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
    }

    public string BinPath { get; }
    public RobotType RobotType { get; }
    public string FileInfoText { get; }
    public WifiDownloader? Wifi { get; }
    public bool HasWifi => Wifi != null;

    public ObservableCollection<DownloadTargetItem> Targets { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private DownloadTargetItem? _selectedTarget;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isDownloading;

    [ObservableProperty]
    private int _percent;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _pairCode;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddIpCommand))]
    private string _fixedIp = "";

    /// <summary>Result of the last download; null when the dialog was closed without downloading.</summary>
    public DownloadOutcome? Outcome { get; private set; }

    public event EventHandler? CloseRequested;

    partial void OnPairCodeChanged(string value)
    {
        if (Wifi != null) Wifi.PairCode = value;
    }

    /// <summary>Polls for targets until <paramref name="ct"/> is cancelled (dialog closed). Must be called on the UI thread.</summary>
    public async Task PollTargetsAsync(CancellationToken ct)
    {
        Status = Localizer.Instance.T("WaitingDevice");
        while (!ct.IsCancellationRequested)
        {
            if (!IsDownloading)
            {
                var found = new List<DownloadTargetItem>();
                foreach (var d in _downloaders)
                {
                    try
                    {
                        foreach (var t in await d.DiscoverAsync(RobotType, ct)) found.Add(new DownloadTargetItem(t, d));
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        // one broken transport (no permission on a HID node, ...) must not hide the others
                    }
                }
                found.AddRange(_manualTargets);
                ApplyTargets(found);
                if (!IsAdvancedOpen && Outcome == null && Targets.FirstOrDefault(t => t.Target.Kind is TransportKind.Hid or TransportKind.RemovableDrive) is { } usb)
                {
                    SelectedTarget = usb;
                    await StartCommand.ExecuteAsync(null);
                    return;
                }
            }
            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void ApplyTargets(List<DownloadTargetItem> found)
    {
        for (var i = Targets.Count - 1; i >= 0; i--)
            if (!found.Contains(Targets[i])) Targets.RemoveAt(i);
        foreach (var item in found)
            if (!Targets.Contains(item)) Targets.Add(item);
        if (SelectedTarget == null || !Targets.Contains(SelectedTarget))
            SelectedTarget = Targets.FirstOrDefault(t => t.Target.Kind is TransportKind.Hid or TransportKind.RemovableDrive) ?? Targets.FirstOrDefault();
        if (!IsDownloading && Outcome == null)
            Status = Localizer.Instance.T("WaitingDevice");
    }

    private bool CanAddIp() => Wifi != null && IPAddress.TryParse(FixedIp.Trim(), out _);

    [RelayCommand(CanExecute = nameof(CanAddIp))]
    private void AddIp()
    {
        var ip = FixedIp.Trim();
        var item = new DownloadTargetItem(new DownloadTarget(TransportKind.Wifi, ip, ip), Wifi!);
        if (!_manualTargets.Contains(item)) _manualTargets.Add(item);
        if (!Targets.Contains(item)) Targets.Add(item);
        SelectedTarget = item;
    }

    private bool CanStart() => SelectedTarget != null && !IsDownloading &&
        (SelectedTarget.Target.Kind != TransportKind.Wifi || !UseFixedIp || CanAddIp());

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        if (SelectedTarget?.Target.Kind == TransportKind.Wifi && Wifi != null)
        {
            if (UseFixedIp) AddIp();
            else SelectedTarget = new DownloadTargetItem(new DownloadTarget(TransportKind.Wifi, WifiDownloader.BroadcastId, "WIFI"), Wifi);
        }
        var item = SelectedTarget!;
        IsDownloading = true;
        Outcome = null;
        Percent = 0;
        Status = Localizer.Instance.T("Programming");
        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<DownloadProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            if (!IsDownloading) return;
            Percent = Math.Clamp(p.Percent, 0, 100);
            if (p.Message.Length > 0) Status = p.Message;
        }));
        try
        {
            Outcome = await item.Downloader.DownloadAsync(BinPath, RobotType, item.Target, progress, _downloadCts.Token);
        }
        catch (OperationCanceledException)
        {
            Outcome = new DownloadOutcome(DownloadStatus.Cancelled, Localizer.Instance.T("CancelByUser"));
        }
        finally
        {
            _downloadCts.Dispose();
            _downloadCts = null;
            IsDownloading = false;
        }
        Status = DescribeOutcome(Outcome);
        if (Outcome.IsSuccess) Percent = 100;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Cancel button: aborts a running download, else closes (original cmdExit).</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (Outcome == null) Outcome = new DownloadOutcome(DownloadStatus.Cancelled, Localizer.Instance.T("CancelByUser"));
        if (_downloadCts != null) _downloadCts.Cancel();
        else CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>MainForm.DownLoad's messages per original DownloadResult, falling back to the transport's message.</summary>
    public static string DescribeOutcome(DownloadOutcome outcome)
    {
        var loc = Localizer.Instance;
        return outcome.Status switch
        {
            DownloadStatus.Ok => loc.T("DownLoadOK"),
            DownloadStatus.Cancelled => loc.T("CancelByUser"),
            DownloadStatus.TimeOut => loc.T("DownloadTimeOut"),
            DownloadStatus.ReadFileFailed => loc.T("DownloadReadFileFailed"),
            DownloadStatus.FileTooBig => loc.T("DownloadReadFileBig") + (outcome.Message.Length > 0 ? "\n" + outcome.Message : ""),
            DownloadStatus.InvalidMcu => loc.T("InvalidRCUType"),
            _ => outcome.Message,
        };
    }
}
