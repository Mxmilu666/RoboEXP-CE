using Avalonia.Controls;
using Avalonia.Headless;
using RoboExp.App.Localization;
using RoboExp.App.ViewModels;
using RoboExp.App.Views;
using RoboExp.Core.Install;
using RoboExp.Core.Library;
using RoboExp.Device;

namespace RoboExp.Tests.App.Ui;

public class DownloadUiTests
{
    [UiFact]
    public Task Usb_is_detected_automatically_and_cancel_stops_the_transfer() => UiHarness.RunAsync(async ui =>
    {
        var device = new FakeDownloader(TransportKind.Hid);
        var vm = Create(ui, device);
        var window = new DownloadWindow { DataContext = vm };
        window.Show(ui.Window);
        await device.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await ui.SettleAsync();
        Assert.True(vm.IsDownloading);
        Assert.False(window.FindControl<Expander>("ConnectionOptions")!.IsExpanded);
        device.Progress!.Report(new DownloadProgress(37, "writing page 37"));
        await ui.SettleAsync();
        Assert.Equal(37, vm.Percent);
        Assert.Equal("writing page 37", vm.Status);
        Capture(window, "download-progress");
        PropertiesUiTests.Click(ui, window, window.FindControl<Button>("CancelDownload")!);
        await device.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await ui.SettleAsync();
        Assert.False(window.IsVisible);
        Assert.Equal(DownloadStatus.Cancelled, vm.Outcome!.Status);
    }, language: UiLanguage.CHS);

    [UiFact]
    public Task Idle_page_keeps_original_steps_and_does_not_start_bluetooth_implicitly() => UiHarness.RunAsync(async ui =>
    {
        var device = new FakeDownloader(TransportKind.Bluetooth);
        var vm = Create(ui, device);
        var window = new DownloadWindow { DataContext = vm };
        window.Show(ui.Window);
        await ui.SettleAsync(100);
        Assert.False(device.Started.Task.IsCompleted);
        Assert.Contains("按下RCU", vm.Step3Text);
        Capture(window, "download-waiting");
        var expander = window.FindControl<Expander>("ConnectionOptions")!;
        expander.IsExpanded = true;
        await ui.SettleAsync();
        Assert.Single(vm.Targets);
        PropertiesUiTests.Click(ui, window, window.FindControl<Button>("StartDownload")!);
        await device.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        device.Finish.TrySetResult(new DownloadOutcome(DownloadStatus.Ok, "ok"));
        await ui.SettleAsync();
        Assert.Equal(100, vm.Percent);
        Assert.Equal(DownloadStatus.Ok, vm.Outcome!.Status);
        Assert.False(window.IsVisible);
    }, language: UiLanguage.CHS);

    [UiFact]
    public Task Waiting_page_has_wifi_fields_and_uses_fixed_ip_for_explicit_wifi_download() => UiHarness.RunAsync(async ui =>
    {
        var vm = Create(ui, new WifiDownloader());
        var window = new DownloadWindow { DataContext = vm };
        window.Show(ui.Window);
        await ui.SettleAsync();
        var pairing = window.FindControl<TextBox>("PairCodeBox")!;
        PropertiesUiTests.Click(ui, window, pairing);
        PropertiesUiTests.Retype(ui, window, "123456");
        Assert.Equal("123456", vm.Wifi!.PairCode);
        PropertiesUiTests.Click(ui, window, window.FindControl<CheckBox>("FixedIpCheck")!);
        vm.IpSegment = "192.168.4";
        vm.IpHost = "25";
        Assert.Equal("192.168.4.25", vm.FixedIp);
        Assert.True(vm.AddIpCommand.CanExecute(null));
        vm.AddIpCommand.Execute(null);
        Assert.Equal("192.168.4.25", vm.SelectedTarget!.Target.Id);
        Capture(window, "download-wifi");
        PropertiesUiTests.Click(ui, window, window.FindControl<Button>("CancelDownload")!);
        await ui.SettleAsync();
        Assert.False(window.IsVisible);
    }, robotType: "MRCU", language: UiLanguage.CHS);

    private static DownloadViewModel Create(UiHarness ui, IDownloader downloader)
    {
        var bin = Path.Combine(ui.WorkDir, "sample.bin");
        File.WriteAllBytes(bin, [1, 2, 3, 4]);
        return new DownloadViewModel(bin, ui.Vm.Env.RobotType, [downloader]);
    }

    private static void Capture(Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("ROBOEXP_UI_EVIDENCE");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
#pragma warning disable CS0618
        frame!.Save(Path.Combine(dir, name + ".png"));
#pragma warning restore CS0618
    }

    private sealed class FakeDownloader(TransportKind kind) : IDownloader
    {
        public TransportKind Kind => kind;
        public TaskCompletionSource Started { get; } = new();
        public TaskCompletionSource Cancelled { get; } = new();
        public TaskCompletionSource<DownloadOutcome> Finish { get; } = new();
        public IProgress<DownloadProgress>? Progress { get; private set; }
        public Task<IReadOnlyList<DownloadTarget>> DiscoverAsync(RobotType type, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DownloadTarget>>([new(kind, "device", "Test RCU")]);
        public async Task<DownloadOutcome> DownloadAsync(string binPath, RobotType type, DownloadTarget target, IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            Progress = progress;
            Started.TrySetResult();
            try { return await Finish.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
        }
    }
}
