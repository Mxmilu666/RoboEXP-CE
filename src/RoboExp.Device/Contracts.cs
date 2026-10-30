using RoboExp.Core.Install;

namespace RoboExp.Device;

/// <summary>
/// Download result. Superset of the original <c>RoboExp.DownloadResult</c>:
/// OK→<see cref="Ok"/>, CancelByUser→<see cref="Cancelled"/>, TimeOut→<see cref="TimeOut"/>,
/// ReadFileFailed→<see cref="ReadFileFailed"/>, ReadFileBig→<see cref="FileTooBig"/>, InvalidMCU→<see cref="InvalidMcu"/>,
/// X1RCUCopyFailed→<see cref="IoError"/>, Default (generic failure)→<see cref="ProtocolError"/>.
/// </summary>
public enum DownloadStatus { Ok, Cancelled, TimeOut, DeviceNotFound, ReadFileFailed, FileTooBig, InvalidMcu, ProtocolError, IoError }

public sealed record DownloadProgress(int Percent, string Message);

public sealed record DownloadOutcome(DownloadStatus Status, string Message)
{
    public bool IsSuccess => Status == DownloadStatus.Ok;
}

public enum TransportKind { Hid, Serial, Bluetooth, Wifi, RemovableDrive }

/// <summary>A concrete download destination. <see cref="Id"/> = HID device path / serial port / IP / mount path.</summary>
public sealed record DownloadTarget(TransportKind Kind, string Id, string DisplayName);

public interface IDownloader
{
    TransportKind Kind { get; }
    Task<IReadOnlyList<DownloadTarget>> DiscoverAsync(RobotType type, CancellationToken ct);
    Task<DownloadOutcome> DownloadAsync(string binPath, RobotType type, DownloadTarget target, IProgress<DownloadProgress>? progress, CancellationToken ct);
}
