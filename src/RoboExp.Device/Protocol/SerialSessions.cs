using System.Text;
using RoboExp.Core.Model;
using RoboExp.Device.Transport;

namespace RoboExp.Device.Protocol;

/// <summary>
/// Classic Bluetooth SPP protocol of DownloadBluetooth (MRCU, E2RCU, E3RCU, X3RCU, IQ2Robot).
/// Port settings: 115200,n,8,1, DTR on, no handshake. 13-byte frames 56 AB cmd d0..d7 sum([2..10]) CF.
/// </summary>
internal sealed class SppProtocol
{
    public const int PacketSize = 128;
    public const int MaxFileSize = 65535;
    public const int FrameLength = 13;

    public BinFile File { get; }
    public int PacketNum { get; }
    private readonly byte[] _date, _time, _name;

    public SppProtocol(BinFile file)
    {
        File = file;
        PacketNum = file.PacketCount(PacketSize);
        _date = BinFile.FatDate(file.LastWriteTime);
        _time = BinFile.FatTime(file.LastWriteTime);
        _name = file.FileName8();
    }

    public static byte[] Frame(byte cmd, ReadOnlySpan<byte> data = default)
    {
        var f = new byte[FrameLength];
        f[0] = 0x56; f[1] = 0xAB; f[2] = cmd;
        data.CopyTo(f.AsSpan(3));
        f[11] = BinFile.ByteSum(f.AsSpan(2, 9));
        f[12] = 0xCF;
        return f;
    }

    public byte[] DataBackRequestCmd() => Frame(1);
    public byte[] RcuTypeRequestCmd() => Frame(2);
    public byte[] PageNumberCmd() => Frame(6, [(byte)(PacketNum % 256), (byte)(PacketNum / 256)]);
    public byte[] FileNameCmd() => Frame(7, _name);
    public byte[] FileDateTimeCmd() => Frame(8, [_time[0], _time[1], _date[0], _date[1]]);
    public byte[] DownloadCmd() => Frame(4);
    /// <summary>128-byte packet padded with 0x20 (spaces), as in the original.</summary>
    public byte[] DataPacket(int i) => File.Packet(i, PacketSize, 0x20);

    /// <summary>checkRcuStateResponse: state byte [3] of a valid 05 frame, or 2 (error) when malformed.</summary>
    public static byte StateOf(byte[] r)
    {
        if (r.Length != FrameLength || r[0] != 0x56 || r[1] != 0xAB || r[2] != 5 || r[12] != 0xCF) return 2;
        return BinFile.ByteSum(r.AsSpan(2, 9)) == r[11] ? r[3] : (byte)2;
    }

    public static async Task<DownloadOutcome> RunAsync(ISerialChannel port, SppProtocol p, TimeSpan connectTimeout, IProgress<DownloadProgress>? progress, DelayFunc delay, CancellationToken ct)
    {
        var second = TimeSpan.FromMilliseconds(1000);
        // Connect: the original re-opened the port and repeated the echo request until the user cancelled.
        var deadline = DateTime.UtcNow + connectTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var back = p.DataBackRequestCmd();
            port.Write(back);
            var echo = port.Read(FrameLength, second);
            if (echo.AsSpan().SequenceEqual(back)) break;
            if (DateTime.UtcNow >= deadline)
                return new DownloadOutcome(DownloadStatus.TimeOut, "Controller did not answer on the Bluetooth port");
            port.DiscardInput();
            await delay(TimeSpan.FromMilliseconds(500), ct);
        }
        progress?.Report(new DownloadProgress(0, "Connected"));

        port.Write(p.RcuTypeRequestCmd());
        var type = port.Read(FrameLength, second);
        if (type.Length != FrameLength || type[0] != 0x56 || type[1] != 0xAB || type[2] != 3 || type[12] != 0xCF)
            return Fail(type, "RCU type");

        foreach (var (cmd, name, timeout) in new[]
                 {
                     (p.PageNumberCmd(), "page number", second), (p.FileNameCmd(), "file name", second),
                     (p.FileDateTimeCmd(), "file date", second), (p.DownloadCmd(), "download", TimeSpan.FromMilliseconds(3000)),
                 })
        {
            port.Write(cmd);
            var r = port.Read(FrameLength, timeout);
            if (StateOf(r) == 2) return Fail(r, name);
        }

        for (var i = 0; i < p.PacketNum; i++)
        {
            ct.ThrowIfCancellationRequested();
            var packet = p.DataPacket(i);
            var sum = BinFile.ByteSum(packet);
            port.Write(packet);
            var r = port.Read(FrameLength, second);
            var state = StateOf(r);
            if (state == 2) return Fail(r, $"packet {i + 1}");
            if (state == 4 && r[4] != sum)
                return new DownloadOutcome(DownloadStatus.ProtocolError, $"File data checksum error (packet {i + 1})");
            progress?.Report(new DownloadProgress(i * 100 / p.PacketNum, $"{i + 1}/{p.PacketNum}"));
        }
        progress?.Report(new DownloadProgress(100, "Download finished"));
        await delay(TimeSpan.FromMilliseconds(500), ct);
        return new DownloadOutcome(DownloadStatus.Ok, "Download finished");
    }

    private static DownloadOutcome Fail(byte[] r, string step) => r.Length == 0
        ? new(DownloadStatus.TimeOut, $"No reply ({step})")
        : new(DownloadStatus.ProtocolError, $"Bad reply ({step}): {Convert.ToHexString(r)}");
}

/// <summary>
/// BLE-module serial protocol of DownloadBLE (E6RCU, E7RCU, M6RCU, C6RCU, C1RCU, X4RCU, M2RCU). 4096-byte pages,
/// port 115200 8N1. No file size limit.
/// </summary>
internal sealed class BleProtocol
{
    public const int PacketSize = 4096;
    public const int FileNameMax = 15;
    public const int MaxRetries = 10;
    private static readonly TimeSpan ReadWait = TimeSpan.FromMilliseconds(660); // 11 polls x 60 ms

    public BinFile File { get; }
    public int PageCount { get; }
    public byte[] FileName { get; }

    static BleProtocol() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public BleProtocol(BinFile file)
    {
        File = file;
        PageCount = file.PacketCount(PacketSize);
        var gbk = Encoding.GetEncoding(936).GetBytes(file.NameWithoutExtension);
        FileName = gbk.Length >= FileNameMax ? gbk[..FileNameMax] : gbk;
    }

    /// <summary>Accepted ASCII model tags at [6],[7] of the type reply. E7RCU and C1RCU have none (original rejects them).</summary>
    public static bool AcceptsModel(SoftwareType type, byte a, byte b) => (type, (char)a, (char)b) switch
    {
        (SoftwareType.E6RCU, 'E', '6') => true,
        (SoftwareType.M2RCU, 'M', '2') => true,
        (SoftwareType.X4RCU, 'X', '4') => true,
        (SoftwareType.M6RCU, 'M', '6') => true,
        (SoftwareType.C6RCU, 'C', '6') or (SoftwareType.C6RCU, 'E', '7') or (SoftwareType.C6RCU, 'M', '7') => true,
        _ => false,
    };

    public static byte[] RcuTypeRequestCmd()
    {
        var f = new byte[20];
        f[0] = 0x86; f[1] = 0xAB; f[2] = 0x14; f[3] = 0xE8; f[4] = 0x80; f[19] = 0xCF;
        return f;
    }

    public static byte[] EndCmd()
    {
        var f = new byte[20];
        f[0] = 0x86; f[1] = 0xAB; f[2] = 0x14; f[3] = 0xE8; f[4] = 0x87; f[19] = 0xCF;
        return f;
    }

    public byte[] DownloadCmd()
    {
        var t = File.LastWriteTime;
        var len = File.Bytes.Length;
        var a = new byte[15 + FileName.Length + 6];
        a[0] = 0x56; a[1] = 0xAB; a[2] = (byte)a.Length; a[3] = 0xE1; a[4] = 0x00; a[5] = 0x1A;
        a[6] = (byte)(len >> 16); a[7] = (byte)(len >> 8); a[8] = (byte)len;
        a[9] = (byte)(t.Year >> 8); a[10] = (byte)t.Year; a[11] = (byte)t.Month; a[12] = (byte)t.Day;
        a[13] = (byte)t.Hour; a[14] = (byte)t.Minute; a[15] = (byte)(t.Second / 2);
        FileName.CopyTo(a, 16);
        ".bin"u8.CopyTo(a.AsSpan(a.Length - 5));
        a[^1] = 0xCF;
        return a;
    }

    public byte[] PageHeader(int page, byte sum) =>
        [0x56, 0xAB, 0x0A, 0xE1, 0x00, 0x1B, (byte)(page >> 8), (byte)page, sum, 0xCF];

    public byte[] Page(int page) => File.Packet(page, PacketSize, 0);

    public static bool IsOk(byte[] r) => r.Length >= 2 && r[0] == 0x56 && r[1] == 0xAB && r[^2] == 1 && r[^1] == 0xCF;

    /// <summary>spp_read: waits for count bytes, returns a count-sized buffer zero-filled past what arrived.</summary>
    private static byte[] ReadPadded(ISerialChannel port, int count, out int received)
    {
        var got = port.Read(count, ReadWait);
        received = got.Length;
        var buf = new byte[count];
        got.CopyTo(buf, 0);
        return buf;
    }

    public static async Task<DownloadOutcome> RunAsync(ISerialChannel port, BleProtocol p, SoftwareType type, IProgress<DownloadProgress>? progress, DelayFunc delay, CancellationToken ct)
    {
        port.Write(RcuTypeRequestCmd());
        var t = ReadPadded(port, 20, out var n);
        if (t[0] != 0x86 || t[1] != 0xAB || t[2] != 0x14 || t[19] != 0xCF)
            return new DownloadOutcome(n == 0 ? DownloadStatus.TimeOut : DownloadStatus.ProtocolError, "rcu无响应 (no controller response)");
        if (!AcceptsModel(type, t[6], t[7]))
            return new DownloadOutcome(DownloadStatus.InvalidMcu, $"rcu类型不对 (controller reports '{(char)t[6]}{(char)t[7]}', expected {type})");

        port.Write(p.DownloadCmd());
        var d = ReadPadded(port, 10, out n);
        if (d[0] != 0x56 || d[1] != 0xAB || d[8] != 1 || d[9] != 0xCF)
            return new DownloadOutcome(n == 0 ? DownloadStatus.TimeOut : DownloadStatus.ProtocolError, "Download command rejected");

        var retries = MaxRetries;
        for (var page = 0; page < p.PageCount; page++)
        {
            ct.ThrowIfCancellationRequested();
            var data = p.Page(page);
            port.Write(p.PageHeader(page, BinFile.ByteSum(data)));
            var r = ReadPadded(port, 10, out _);
            if (retries < 0)
                return new DownloadOutcome(DownloadStatus.TimeOut, $"Too many retries (page {page + 1})");
            if (IsOk(r))
            {
                port.Write(data);
                r = ReadPadded(port, 10, out _);
                if ((page == p.PageCount - 1 && r[0] == 0x56) || IsOk(r))
                {
                    progress?.Report(new DownloadProgress((page + 1) * 100 / p.PageCount, $"{page + 1}/{p.PageCount}"));
                    continue;
                }
            }
            retries--;
            page--;
        }
        progress?.Report(new DownloadProgress(100, "Download finished"));
        await delay(TimeSpan.FromMilliseconds(600), ct);
        port.Write(EndCmd());
        port.Read(20, ReadWait);
        return new DownloadOutcome(DownloadStatus.Ok, "Download finished");
    }
}
