using RoboExp.Core.Model;
using RoboExp.Device.Transport;

namespace RoboExp.Device.Protocol;

/// <summary>HID download state machines ported from DownloadX1RCU.Download/C2download and DownloadHid.mcdounload.</summary>
internal static class HidSessions
{
    /// <summary>HidDevice.readTimeout (the original ReadFile blocked; 1000 ms is the declared timeout).</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// RCU type codes reported in byte [20] of the RcuInfo reply, as accepted by DownloadX1RCU.Download.
    /// IQ2Robot is in no branch, so the original rejects it as InvalidMCU over this path.
    /// </summary>
    public static bool AcceptsRcuCode(byte code, SoftwareType type) => code switch
    {
        0 or 1 or 7 => type is SoftwareType.E2RCU or SoftwareType.E3RCU or SoftwareType.E6RCU or SoftwareType.E7RCU
            or SoftwareType.M7RCU or SoftwareType.M6RCU or SoftwareType.C6RCU or SoftwareType.C1RCU
            or SoftwareType.EV5 or SoftwareType.MRCU or SoftwareType.M2RCU,
        2 => type == SoftwareType.X2RCU,
        3 => type == SoftwareType.IQrobot,
        4 => type is SoftwareType.X3RCU or SoftwareType.X4RCU,
        _ => false,
    };

    /// <summary>"Download" menu HID path (VID 0x0483 / PID 0x5750).</summary>
    public static async Task<DownloadOutcome> RunStAsync(IHidChannel hid, RcuDownloadProtocol p, IProgress<DownloadProgress>? progress, DelayFunc delay, CancellationToken ct)
    {
        progress?.Report(new DownloadProgress(0, "HID DownLoading..."));
        if (p.Type == SoftwareType.C1RCU)
            return await RunC1Async(hid, p, progress, delay, ct);

        if (!hid.Write(p.RequestRcuInfoCmd())) return WriteFailed();
        await delay(TimeSpan.FromMilliseconds(10), ct);
        var info = hid.Read(ReadTimeout);
        if (info is null) return ReadFailed("RCU info");
        if (!AcceptsRcuCode(info[20], p.Type))
            return new DownloadOutcome(DownloadStatus.InvalidMcu, $"Controller reported RCU code {info[20]}, which is not a {p.Type}");

        if (!hid.Write(p.RequestBackCmd())) return WriteFailed();
        await delay(TimeSpan.FromMilliseconds(10), ct);
        if (hid.Read(ReadTimeout) is null) return ReadFailed("back request");

        if (!hid.Write(p.RequestDownloadCmd())) return WriteFailed();
        await delay(TimeSpan.FromMilliseconds(10), ct);
        if (hid.Read(ReadTimeout) is null) return ReadFailed("download request");

        for (var i = 0; i < p.PacketNum; i++)
        {
            ct.ThrowIfCancellationRequested();
            var packet = p.DataPacket(i);
            var sum = BinFile.IntSum(packet);
            if (!hid.Write(packet)) return WriteFailed();
            await delay(TimeSpan.FromMilliseconds(1), ct);
            var ack = hid.Read(ReadTimeout);
            if (ack is null) return ReadFailed($"packet {i + 1} ack");
            if (ack[4] != sum % 256 || ack[5] != sum / 256)
                return ChecksumError(i);
            progress?.Report(new DownloadProgress((i + 1) * 100 / p.PacketNum, $"{i + 1}/{p.PacketNum}"));
        }
        return Success();
    }

    /// <summary>DownloadX1RCU.C2download (C1RCU).</summary>
    private static async Task<DownloadOutcome> RunC1Async(IHidChannel hid, RcuDownloadProtocol p, IProgress<DownloadProgress>? progress, DelayFunc delay, CancellationToken ct)
    {
        if (!hid.Write(p.C1DownloadHeader())) return WriteFailed();
        await delay(TimeSpan.FromMilliseconds(1000), ct);
        if (hid.Read(ReadTimeout) is null) return ReadFailed("download header");

        for (var i = 0; i < p.PacketNum; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!hid.Write(p.DataPacket(i))) return WriteFailed();
            await delay(TimeSpan.FromMilliseconds(1), ct);
            var ack = hid.Read(ReadTimeout);
            if (ack is null) return ReadFailed($"packet {i + 1} ack");
            if (ack[5] == 1)
            {
                progress?.Report(new DownloadProgress((i + 1) * 100 / p.PacketNum, $"{i + 1}/{p.PacketNum}"));
                continue;
            }
            // Original: on the last packet the device answers with the file checksum instead of [5]=1.
            if (i == p.PacketNum - 1 && ack[7] == p.FileChecksumBytes[0] && ack[6] == p.FileChecksumBytes[1])
                break;
            return ChecksumError(i);
        }
        return Success();
    }

    /// <summary>"HID Download" menu path (DownloadHid, VID 0x0812 / PID 0x5354), thread mcdounload.</summary>
    public static async Task<DownloadOutcome> RunLegacyAsync(IHidChannel hid, RcuDownloadProtocol p, IProgress<DownloadProgress>? progress, DelayFunc delay, CancellationToken ct)
    {
        var header = new byte[64];
        header[0] = 0x77; header[1] = 0x76; header[2] = 0x09; header[3] = 0xDD; header[4] = 0x01; header[5] = 0x00;
        header[6] = (byte)(p.PacketNum >> 8);
        header[7] = (byte)p.PacketNum;
        header[8] = 0x75;
        if (!hid.Write(header)) return WriteFailed();
        await delay(TimeSpan.FromMilliseconds(10), ct);
        var reply = hid.Read(ReadTimeout);
        if (reply is null) return ReadFailed("download header");
        // The original silently reported success without sending data on an unexpected reply; treat it as an error.
        if (reply[0] != 0x77 || reply[1] != 0x76 || reply[2] != 0x06 || reply[3] != 0xDD || reply[4] != 0x01 || reply[5] != 0x00)
            return new DownloadOutcome(DownloadStatus.ProtocolError, "Unexpected reply to download header");
        progress?.Report(new DownloadProgress(0, "Download started..."));

        for (var i = 0; i < p.PacketNum; i++)
        {
            ct.ThrowIfCancellationRequested();
            var packet = p.DataPacket(i);
            var sum = BinFile.IntSum(packet);
            if (!hid.Write(packet)) return WriteFailed();
            await delay(TimeSpan.FromMilliseconds(1), ct);
            var ack = hid.Read(ReadTimeout);
            if (ack is null) return ReadFailed($"packet {i + 1} ack");
            if (ack[6] != sum % 256 || ack[7] != sum / 256)
                return ChecksumError(i);
            progress?.Report(new DownloadProgress((i + 1) * 100 / p.PacketNum, $"{i + 1}/{p.PacketNum}"));
        }
        return Success();
    }

    private static DownloadOutcome Success() => new(DownloadStatus.Ok, "Download successed!");
    private static DownloadOutcome WriteFailed() => new(DownloadStatus.IoError, "HID write failed");
    private static DownloadOutcome ReadFailed(string what) => new(DownloadStatus.TimeOut, $"No HID reply ({what})");
    private static DownloadOutcome ChecksumError(int i) => new(DownloadStatus.ProtocolError, $"Checksum error! (packet {i + 1})");
}
