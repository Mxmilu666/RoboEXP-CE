using System.Net;
using RoboExp.Core.Model;
using RoboExp.Device.Transport;

namespace RoboExp.Device.Protocol;

/// <summary>
/// UDP WiFi protocol of DownloadIQRobot (used by WifiDownloadForm and DownloadX1RCU). Local port 36803, controller port 28000.
/// Envelope: 78 CD lenHi lenLo k0 k1 pair0 pair1 pair2 payload.. BE where len = payload+10 and
/// (k0,k1) = (EE,FF), or (AA,03) for MRCU. Inner commands are 13-byte 56 AB cmd .. sum([2..10]) CF frames.
/// </summary>
internal sealed class WifiProtocol
{
    public const int LocalPort = 36803;
    public const int ControllerPort = 28000;
    public const int PacketSize = 1024;
    public const int MaxFileSize = 65535;

    public BinFile File { get; }
    public SoftwareType Type { get; }
    public byte[] PairCode { get; }
    public int PacketNum { get; }
    private bool IsMrcu => Type == SoftwareType.MRCU;
    private readonly byte[] _date, _time, _name;

    public WifiProtocol(BinFile file, SoftwareType type, string pairCode)
    {
        File = file;
        Type = type;
        PairCode = ParsePairCode(pairCode);
        PacketNum = file.PacketCount(PacketSize);
        _date = BinFile.FatDate(file.LastWriteTime);
        _time = BinFile.FatTime(file.LastWriteTime);
        _name = file.FileName8();
    }

    /// <summary>Decimal pairing code (as shown on the controller) → 3 big-endian bytes.</summary>
    public static byte[] ParsePairCode(string code)
    {
        if (!int.TryParse(code.Trim(), out var v) || v < 0)
            throw new ArgumentException($"Invalid WiFi pairing code '{code}' (digits expected)");
        return [(byte)(v >> 16), (byte)(v >> 8), (byte)v];
    }

    /// <summary>sendip: 78 CD 00 0E AA 01 pair[3] hostIp[4] BE.</summary>
    public byte[] PairRequest(IPAddress local)
    {
        var ip = local.GetAddressBytes();
        return [0x78, 0xCD, 0x00, 0x0E, 0xAA, 0x01, PairCode[0], PairCode[1], PairCode[2], ip[0], ip[1], ip[2], ip[3], 0xBE];
    }

    public byte[] Wrap(byte[] payload)
    {
        var n = payload.Length + 10;
        var a = new byte[n];
        a[0] = 0x78; a[1] = 0xCD; a[2] = (byte)(n / 256); a[3] = (byte)(n % 256);
        (a[4], a[5]) = IsMrcu ? ((byte)0xAA, (byte)0x03) : ((byte)0xEE, (byte)0xFF);
        a[6] = PairCode[0]; a[7] = PairCode[1]; a[8] = PairCode[2];
        payload.CopyTo(a, 9);
        a[^1] = 0xBE;
        return a;
    }

    public static byte[] Cmd(byte code, ReadOnlySpan<byte> data = default) => SppProtocol.Frame(code, data);

    public byte[] RcuInfoCmd() => Cmd(2);
    public byte[] PageNumCmd() => Cmd(6, [(byte)(PacketNum % 256), (byte)(PacketNum / 256)]);
    public byte[] FileNameCmd() => Cmd(7, _name);
    public byte[] DateCmd() => Cmd(8, [_time[0], _time[1], _date[0], _date[1]]);
    public byte[] DownloadCmd() => Cmd(4);

    /// <summary>Data frame 56 AB seq[2] data[1024] (sum(data)+seq0+seq1) CF. Sequence is big endian, little endian for MRCU.</summary>
    public byte[] DataFrame(int index, out byte dataSum)
    {
        var data = File.Packet(index, PacketSize, 0);
        dataSum = BinFile.ByteSum(data);
        var f = new byte[PacketSize + 6];
        f[0] = 0x56; f[1] = 0xAB;
        (f[2], f[3]) = IsMrcu ? ((byte)(index % 256), (byte)(index / 256)) : ((byte)(index / 256), (byte)(index % 256));
        data.CopyTo(f, 4);
        f[^2] = unchecked((byte)(dataSum + f[2] + f[3]));
        f[^1] = 0xCF;
        return f;
    }

    /// <summary>RcuInfo reply [12] model codes accepted per type (E3/E6/E7/M6/M7/C6/C1 accept any code, faithful to the original).</summary>
    public static bool AcceptsModel(SoftwareType type, byte code) => type switch
    {
        SoftwareType.IQrobot => code == 3,
        SoftwareType.IQ2Robot => code == 10,
        SoftwareType.E2RCU => code == 1,
        SoftwareType.X2RCU => code == 2,
        SoftwareType.X3RCU or SoftwareType.X4RCU => code == 4,
        SoftwareType.E3RCU or SoftwareType.E6RCU or SoftwareType.E7RCU or SoftwareType.M7RCU or SoftwareType.M6RCU
            or SoftwareType.C6RCU or SoftwareType.C1RCU => true,
        SoftwareType.EV5 => code == 7,
        SoftwareType.MRCU => code == 9,
        _ => false,
    };

    /// <summary>get78cd03: resynchronise on the next 78 CD marker after index 0.</summary>
    public static byte[]? Resync(byte[] rev)
    {
        if (rev.Length <= 10) return null;
        for (var i = 1; i < rev.Length - 3; i++)
            if (rev[i] == 0x78 && rev[i + 1] == 0xCD)
                return rev[i..];
        return null;
    }

    private static bool IsInfoReply(byte[]? a) => a is { Length: >= 14 } && a[11] == 3 && a[13] == 1;
    private static bool IsStepAck(byte[]? a) => a is { Length: > 12 } && a[11] == 5 && a[12] == 1;

    public static async Task<DownloadOutcome> RunAsync(IUdpChannel udp, WifiProtocol p, IPAddress local, IPEndPoint pairDestination,
        TimeSpan pairTimeout, IProgress<DownloadProgress>? progress, DelayFunc delay, CancellationToken ct)
    {
        var receiveTimeout = TimeSpan.FromMilliseconds(1000);
        progress?.Report(new DownloadProgress(0, $"Pairing via {pairDestination.Address}..."));
        IPEndPoint? host = null;
        var deadline = DateTime.UtcNow + pairTimeout;
        while (host is null)
        {
            ct.ThrowIfCancellationRequested();
            udp.Send(p.PairRequest(local), pairDestination);
            await delay(TimeSpan.FromMilliseconds(10), ct);
            if (udp.Receive(receiveTimeout) is { } reply) { host = reply.From; break; }
            if (DateTime.UtcNow >= deadline)
                return new DownloadOutcome(DownloadStatus.TimeOut, "No controller answered: check the WiFi pairing code and that the computer is on the same network segment");
            await delay(TimeSpan.FromMilliseconds(1000), ct);
        }
        progress?.Report(new DownloadProgress(0, host.Address.ToString()));

        // RcuInfo handshake: 30 tries with resync for MRCU, 3 tries otherwise.
        var ok = false;
        for (var attempt = 0; attempt < (p.IsMrcu ? 30 : 3) && !ok; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            udp.Send(p.Wrap(p.RcuInfoCmd()), host);
            await delay(TimeSpan.FromMilliseconds(100), ct);
            var r = udp.Receive(receiveTimeout);
            if (r is null) continue;
            host = r.Value.From;
            var a = r.Value.Data;
            if (!p.IsMrcu && a.Length == 10)
                return new DownloadOutcome(DownloadStatus.InvalidMcu, "Controller rejected the request (invalid RCU type)");
            if (p.IsMrcu)
                while (a is not null && !IsInfoReply(a)) a = Resync(a);
            if (!IsInfoReply(a)) continue;
            if (!AcceptsModel(p.Type, a![12]))
                return new DownloadOutcome(DownloadStatus.InvalidMcu, $"Controller model code {a[12]} is not a {p.Type}");
            ok = true;
        }
        if (!ok) return new DownloadOutcome(DownloadStatus.TimeOut, "No RCU info reply");
        progress?.Report(new DownloadProgress(0, $"DownLoad to:{host.Address}"));

        var steps = new (byte[] Cmd, string Name, int WaitMs)[]
        {
            (p.PageNumCmd(), "page number", 100), (p.FileNameCmd(), "file name", 100),
            (p.DateCmd(), "date", 100), (p.DownloadCmd(), "download", p.IsMrcu ? 100 : 1200),
        };
        foreach (var (cmd, name, wait) in steps)
        {
            udp.Send(p.Wrap(cmd), host);
            await delay(TimeSpan.FromMilliseconds(wait), ct);
            var r = udp.Receive(receiveTimeout);
            if (r is null) return new DownloadOutcome(DownloadStatus.TimeOut, $"No reply ({name})");
            host = r.Value.From;
            var a = r.Value.Data;
            var accepted = IsStepAck(a) || (!p.IsMrcu && name == "page number" && a.Length > 12 && a[11] == 3 && a[12] == 4);
            if (!accepted) return new DownloadOutcome(DownloadStatus.ProtocolError, $"Bad reply ({name}): {Convert.ToHexString(a)}");
        }

        for (var i = 0; i < p.PacketNum; i++)
        {
            var frame = p.Wrap(p.DataFrame(i, out var sum));
            var acked = false;
            for (var attempt = 0; attempt < 5 && !acked; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                udp.Send(frame, host);
                if (p.IsMrcu) await delay(TimeSpan.FromMilliseconds(10), ct);
                var r = udp.Receive(receiveTimeout);
                if (r is null) continue;
                host = r.Value.From;
                var a = r.Value.Data;
                acked = p.IsMrcu
                    ? a.Length >= 13 && a[11] == 5 && a[12] == 4
                    : a.Length >= 14 && a[11] == 5 && a[12] == 4 && a[13] == sum;
            }
            if (!acked) return new DownloadOutcome(DownloadStatus.TimeOut, $"Packet {i + 1} not acknowledged after 5 tries");
            if (!p.IsMrcu || i % 3 == 0)
                progress?.Report(new DownloadProgress((i + 1) * 100 / p.PacketNum, $"{i + 1}/{p.PacketNum}"));
        }
        progress?.Report(new DownloadProgress(100, "Download finished"));
        return new DownloadOutcome(DownloadStatus.Ok, "Download finished");
    }
}
