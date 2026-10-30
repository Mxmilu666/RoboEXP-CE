using System.IO.Ports;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using HidSharp;
using RoboExp.Core.Install;
using RoboExp.Core.Model;
using RoboExp.Device.Protocol;
using RoboExp.Device.Transport;

namespace RoboExp.Device;

internal static class Runner
{
    /// <summary>Loads the image and maps file/cancellation/IO exceptions to outcomes.</summary>
    public static async Task<DownloadOutcome> RunAsync(string binPath, Func<BinFile, Task<DownloadOutcome>> body, CancellationToken ct)
    {
        BinFile file;
        try { file = BinFile.Load(binPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new DownloadOutcome(DownloadStatus.ReadFileFailed, $"Cannot read '{binPath}': {e.Message}");
        }
        try { return await body(file); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new DownloadOutcome(DownloadStatus.Cancelled, "Cancelled by user");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SocketException or InvalidOperationException or TimeoutException)
        {
            return new DownloadOutcome(DownloadStatus.IoError, e.Message);
        }
    }

    public static DownloadOutcome TooBig(string reason) => new(DownloadStatus.FileTooBig, reason);
}

/// <summary>
/// USB HID. Two devices exist in the original: the "Download" menu path (DownloadX1RCU, VID 0x0483 PID 0x5750,
/// RcuDownloadProtocol / C1RCU variant) and the "HID Download" menu path (DownloadHid, VID 0x0812 PID 0x5354).
/// The protocol is chosen by the VID/PID of the target.
/// </summary>
public sealed class HidDownloader : IDownloader
{
    public const int StVid = 0x0483, StPid = 0x5750;
    public const int LegacyVid = 0x0812, LegacyPid = 0x5354;

    public TransportKind Kind => TransportKind.Hid;

    public Task<IReadOnlyList<DownloadTarget>> DiscoverAsync(RobotType type, CancellationToken ct) => Task.Run<IReadOnlyList<DownloadTarget>>(() =>
        Devices().Select(d => new DownloadTarget(TransportKind.Hid, d.DevicePath, $"{Describe(d)} ({d.VendorID:X4}:{d.ProductID:X4})")).ToList(), ct);

    private static IEnumerable<HidDevice> Devices() => DeviceList.Local.GetHidDevices()
        .Where(d => (d.VendorID == StVid && d.ProductID == StPid) || (d.VendorID == LegacyVid && d.ProductID == LegacyPid));

    private static string Describe(HidDevice d)
    {
        try { return d.GetProductName(); } catch { return d.VendorID == StVid ? "RCU HID" : "RCU HID (legacy)"; }
    }

    public Task<DownloadOutcome> DownloadAsync(string binPath, RobotType type, DownloadTarget target, IProgress<DownloadProgress>? progress, CancellationToken ct) =>
        Runner.RunAsync(binPath, file => Task.Run(async () =>
        {
            if (RcuDownloadProtocol.CheckSize(file.Bytes.Length, type.SoftwareType) is { } tooBig) return Runner.TooBig(tooBig);
            var device = Devices().FirstOrDefault(d => d.DevicePath == target.Id);
            if (device is null) return new DownloadOutcome(DownloadStatus.DeviceNotFound, $"HID device not found: {target.Id}");
            using var hid = HidSharpChannel.TryOpen(device);
            if (hid is null) return new DownloadOutcome(DownloadStatus.DeviceNotFound, "HID open fail!");
            var protocol = new RcuDownloadProtocol(file, type.SoftwareType);
            return device.VendorID == StVid
                ? await HidSessions.RunStAsync(hid, protocol, progress, Delays.Real, ct)
                : await HidSessions.RunLegacyAsync(hid, protocol, progress, Delays.Real, ct);
        }, ct), ct);
}

/// <summary>
/// Bluetooth over an OS-paired serial port (replaces connecter.exe pairing). Protocol per MainForm.BluetoothDownload:
/// DownloadBLE for E6/E7/M6/C6/C1/X4/M2, DownloadBluetooth (SPP) for the rest.
/// </summary>
public sealed class BluetoothDownloader : IDownloader
{
    /// <summary>How long to keep sending the SPP echo request (the original retried until cancelled).</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TransportKind Kind => TransportKind.Bluetooth;

    public static bool UsesBle(SoftwareType t) => t is SoftwareType.E6RCU or SoftwareType.E7RCU or SoftwareType.M7RCU
        or SoftwareType.M6RCU or SoftwareType.C6RCU or SoftwareType.C1RCU or SoftwareType.X4RCU or SoftwareType.A3RCU or SoftwareType.M2RCU;

    public Task<IReadOnlyList<DownloadTarget>> DiscoverAsync(RobotType type, CancellationToken ct) => Task.Run<IReadOnlyList<DownloadTarget>>(() =>
    {
        IEnumerable<string> names = SerialPort.GetPortNames();
        // macOS lists both /dev/tty.* and /dev/cu.*; the call-out (cu) device is the one to open without waiting for DCD.
        if (OperatingSystem.IsMacOS())
            names = names.Where(n => n.StartsWith("/dev/cu.", StringComparison.Ordinal));
        return names.Distinct().Order(StringComparer.Ordinal)
            .Select(n => new DownloadTarget(TransportKind.Bluetooth, n, n)).ToList();
    }, ct);

    public Task<DownloadOutcome> DownloadAsync(string binPath, RobotType type, DownloadTarget target, IProgress<DownloadProgress>? progress, CancellationToken ct) =>
        Runner.RunAsync(binPath, file => Task.Run(async () =>
        {
            var ble = UsesBle(type.SoftwareType);
            if (!ble && file.Bytes.Length > SppProtocol.MaxFileSize) return Runner.TooBig("File size over 64Kb");
            SerialPortChannel port;
            try { port = new SerialPortChannel(target.Id, 115200, dtr: !ble); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                return new DownloadOutcome(DownloadStatus.DeviceNotFound, $"Cannot open {target.Id}: {e.Message}");
            }
            using (port)
            {
                return ble
                    ? await BleProtocol.RunAsync(port, new BleProtocol(file), type.SoftwareType, progress, Delays.Real, ct)
                    : await SppProtocol.RunAsync(port, new SppProtocol(file), ConnectTimeout, progress, Delays.Real, ct);
            }
        }, ct), ct);
}

/// <summary>UDP WiFi (DownloadIQRobot). Target Id "broadcast" pairs via 255.255.255.255, otherwise the fixed controller IP.</summary>
public sealed class WifiDownloader : IDownloader
{
    public const string BroadcastId = "broadcast";

    /// <summary>Pairing code shown on the controller (original default "000000").</summary>
    public string PairCode { get; set; } = "000000";
    public TimeSpan PairTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TransportKind Kind => TransportKind.Wifi;

    public Task<IReadOnlyList<DownloadTarget>> DiscoverAsync(RobotType type, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DownloadTarget>>(LocalAddress() is null
            ? []
            : [new DownloadTarget(TransportKind.Wifi, BroadcastId, "Broadcast pairing (enter the controller's pairing code)")]);

    /// <summary>First operational non-loopback IPv4 address (the original used the first IPv4 address with a mask).</summary>
    internal static IPAddress? LocalAddress() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.Supports(NetworkInterfaceComponent.IPv4))
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
        .Select(a => a.Address)
        .FirstOrDefault();

    public Task<DownloadOutcome> DownloadAsync(string binPath, RobotType type, DownloadTarget target, IProgress<DownloadProgress>? progress, CancellationToken ct) =>
        Runner.RunAsync(binPath, file => Task.Run(async () =>
        {
            if (file.Bytes.Length > WifiProtocol.MaxFileSize) return Runner.TooBig("File size over 64Kb");
            var local = LocalAddress();
            if (local is null) return new DownloadOutcome(DownloadStatus.DeviceNotFound, "No IPv4 network interface");
            IPAddress dest;
            if (target.Id == BroadcastId) dest = IPAddress.Broadcast;
            else if (!IPAddress.TryParse(target.Id, out dest!)) return new DownloadOutcome(DownloadStatus.DeviceNotFound, $"Invalid IP '{target.Id}'");
            var protocol = new WifiProtocol(file, type.SoftwareType, PairCode);
            using var udp = new UdpClientChannel(WifiProtocol.LocalPort);
            return await WifiProtocol.RunAsync(udp, protocol, local, new IPEndPoint(dest, WifiProtocol.ControllerPort), PairTimeout, progress, Delays.Real, ct);
        }, ct), ct);
}

/// <summary>USB mass-storage copy (DownloadX1RCU.CopyToX1RCU): copy &lt;name&gt;.bin to the root of the controller's volume.</summary>
public sealed class RemovableDriveDownloader : IDownloader
{
    public TransportKind Kind => TransportKind.RemovableDrive;

    public Task<IReadOnlyList<DownloadTarget>> DiscoverAsync(RobotType type, CancellationToken ct) => Task.Run<IReadOnlyList<DownloadTarget>>(() =>
    {
        var result = new List<DownloadTarget>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                var label = VolumeLabel(drive);
                if (DriveCriteria.Matches(type.SoftwareType, label, drive.TotalSize))
                    result.Add(new DownloadTarget(TransportKind.RemovableDrive, drive.RootDirectory.FullName, $"{label} ({drive.RootDirectory.FullName})"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }, ct);

    /// <summary>Windows: real label. Unix: .NET returns the mount point, and macOS/Linux mount volumes at .../&lt;label&gt;.</summary>
    private static string VolumeLabel(DriveInfo d) => OperatingSystem.IsWindows()
        ? d.VolumeLabel
        : Path.GetFileName(d.RootDirectory.FullName.TrimEnd('/')) ?? "";

    public Task<DownloadOutcome> DownloadAsync(string binPath, RobotType type, DownloadTarget target, IProgress<DownloadProgress>? progress, CancellationToken ct) =>
        CopyAsync(binPath, target.Id, progress, Delays.Real, ct);

    internal static async Task<DownloadOutcome> CopyAsync(string binPath, string root, IProgress<DownloadProgress>? progress, DelayFunc delay, CancellationToken ct)
    {
        if (!File.Exists(binPath)) return new DownloadOutcome(DownloadStatus.ReadFileFailed, $"Cannot read '{binPath}'");
        if (!Directory.Exists(root)) return new DownloadOutcome(DownloadStatus.DeviceNotFound, $"Volume not mounted: {root}");
        var dest = Path.Combine(root, Path.GetFileName(binPath));
        string error = "";
        try
        {
            for (var attempt = 0; attempt < DriveCriteria.CopyAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(dest) && File.GetAttributes(dest).HasFlag(FileAttributes.ReadOnly))
                        File.SetAttributes(dest, FileAttributes.Normal);
                    progress?.Report(new DownloadProgress(0, $"Copying to {dest}"));
                    // A controller's mass-storage file may be writable without read access. File.Copy opens
                    // the destination ReadWrite on Unix; firmware transfer needs Write only, no metadata cloning.
                    await using (var input = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                     bufferSize: 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None,
                                     bufferSize: 65536, FileOptions.Asynchronous))
                    {
                        await input.CopyToAsync(output, ct);
                        await output.FlushAsync(ct);
                    }
                    progress?.Report(new DownloadProgress(100, "Copied"));
                    return new DownloadOutcome(DownloadStatus.Ok, $"Copied to {dest}");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    error = e.Message;
                    progress?.Report(new DownloadProgress(0, e.Message));
                    await delay(TimeSpan.FromSeconds(1), ct);
                }
            }
        }
        catch (OperationCanceledException) { return new DownloadOutcome(DownloadStatus.Cancelled, "Cancelled by user"); }
        return new DownloadOutcome(DownloadStatus.IoError, $"Copy to {dest} failed after {DriveCriteria.CopyAttempts} attempts: {error}");
    }
}

/// <summary>Per-type transports, derived from the MainForm download dispatch.</summary>
public static class DownloadMethods
{
    /// <summary>
    /// Every supported ARM controller is accepted by all four menu entries: DownLoad() → DownloadX1RCU (HID 0483:5750 +
    /// removable drive), HIDDownload() → DownloadHid (HID 0812:5354), BluetoothDownload(), WIFIDownload().
    /// The MSComm serial loader (Download.cs) is only reached by types outside the supported set.
    /// Order is the "Download" menu priority: HID, drive, Bluetooth, WiFi.
    /// </summary>
    public static IReadOnlyList<IDownloader> For(RobotType type) =>
    [
        new HidDownloader(),
        .. type.SoftwareType == SoftwareType.C1RCU ? Array.Empty<IDownloader>() : [new RemovableDriveDownloader()],
        new BluetoothDownloader(),
        new WifiDownloader(),
    ];
}
