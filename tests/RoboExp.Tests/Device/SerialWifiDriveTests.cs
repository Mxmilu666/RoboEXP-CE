using System.Net;
using RoboExp.Core.Install;
using RoboExp.Core.Model;
using RoboExp.Device;
using RoboExp.Device.Protocol;
using RoboExp.Device.Transport;

namespace RoboExp.Tests.Device;

public class SppProtocolTests
{
    private static byte[] State(byte state, byte extra = 0)
    {
        var f = SppProtocol.Frame(5, [state, extra]);
        return f;
    }

    [Fact]
    public void Frames_ExactBytes()
    {
        var p = new SppProtocol(TestFiles.Make(300, "AB"));
        Assert.Equal(new byte[] { 0x56, 0xAB, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0x01, 0xCF }, p.DataBackRequestCmd());
        // 300 bytes / 128 = 3 pages
        Assert.Equal(new byte[] { 0x56, 0xAB, 0x06, 0x03, 0x00, 0, 0, 0, 0, 0, 0, 0x09, 0xCF }, p.PageNumberCmd());
        Assert.Equal(new byte[] { 0x56, 0xAB, 0x07, (byte)'A', (byte)'B', 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x4A, 0xCF }, p.FileNameCmd());
        Assert.Equal(new byte[] { 0x56, 0xAB, 0x08, 0xB3, 0x6D, 0xB1, 0x58, 0, 0, 0, 0, 0x31, 0xCF }, p.FileDateTimeCmd());
        var last = p.DataPacket(2);
        Assert.Equal(128, last.Length);
        Assert.All(last[44..], b => Assert.Equal(0x20, b)); // space padded
    }

    [Fact]
    public void StateOf_RejectsBadChecksum()
    {
        var ok = State(1);
        Assert.Equal(1, SppProtocol.StateOf(ok));
        ok[11]++;
        Assert.Equal(2, SppProtocol.StateOf(ok));
    }

    [Fact]
    public async Task Run_RetriesEchoThenDownloads()
    {
        var p = new SppProtocol(TestFiles.Make(200));
        var echoes = 0;
        var port = new FakeSerial((w, _) =>
        {
            if (w.Length == 13 && w[2] == 1) return ++echoes == 1 ? [] : w; // first echo lost
            if (w.Length == 13 && w[2] == 2) return SppProtocol.Frame(3);
            if (w.Length == 13) return State(1);
            return State(4, BinFile.ByteSum(w));
        });
        var outcome = await SppProtocol.RunAsync(port, p, TimeSpan.FromMinutes(1), null, Delays.None, default);
        Assert.Equal(DownloadStatus.Ok, outcome.Status);
        Assert.Equal(2, echoes);
        Assert.Equal(2 + 1 + 4 + 2, port.Writes.Count);
    }

    [Fact]
    public async Task Run_PacketChecksumMismatch_IsProtocolError()
    {
        var port = new FakeSerial((w, _) => w.Length == 13
            ? w[2] switch { 1 => w, 2 => SppProtocol.Frame(3), _ => State(1) }
            : State(4, (byte)(BinFile.ByteSum(w) + 1)));
        var outcome = await SppProtocol.RunAsync(port, new SppProtocol(TestFiles.Make(200)), TimeSpan.FromMinutes(1), null, Delays.None, default);
        Assert.Equal(DownloadStatus.ProtocolError, outcome.Status);
    }

    [Fact]
    public async Task Run_NoEcho_TimesOut()
    {
        var port = new FakeSerial((_, _) => []);
        var outcome = await SppProtocol.RunAsync(port, new SppProtocol(TestFiles.Make(10)), TimeSpan.Zero, null, Delays.None, default);
        Assert.Equal(DownloadStatus.TimeOut, outcome.Status);
    }
}

public class BleProtocolTests
{
    private static byte[] TypeReply(string model)
    {
        var r = new byte[20];
        r[0] = 0x86; r[1] = 0xAB; r[2] = 0x14; r[6] = (byte)model[0]; r[7] = (byte)model[1]; r[19] = 0xCF;
        return r;
    }

    private static readonly byte[] Ok10 = [0x56, 0xAB, 0, 0, 0, 0, 0, 0, 1, 0xCF];

    [Fact]
    public void DownloadCommand_ExactBytes()
    {
        var p = new BleProtocol(TestFiles.Make(5000, "AB"));
        Assert.Equal(new byte[]
        {
            0x56, 0xAB, 23, 0xE1, 0x00, 0x1A, 0x00, 0x13, 0x88, // length 5000 (24-bit)
            0x07, 0xE8, 5, 17, 13, 45, 19,                       // year hi/lo, month, day, h, m, s/2
            (byte)'A', (byte)'B', (byte)'.', (byte)'b', (byte)'i', (byte)'n', 0xCF,
        }, p.DownloadCmd());
        Assert.Equal(new byte[] { 0x56, 0xAB, 0x0A, 0xE1, 0x00, 0x1B, 0x01, 0x02, 0x33, 0xCF }, p.PageHeader(0x102, 0x33));
    }

    [Fact]
    public void FileName_IsGbkTruncatedTo15Bytes()
    {
        var p = new BleProtocol(TestFiles.Make(1, "机器人程序项目名称很长"));
        Assert.Equal(15, p.FileName.Length);
        Assert.Equal(new byte[] { 0xBB, 0xFA }, p.FileName[..2]); // GBK for 机
    }

    [Fact]
    public async Task Run_RetriesNakedPageThenSucceeds()
    {
        var p = new BleProtocol(TestFiles.Make(5000)); // 2 pages
        var naks = 0;
        var port = new FakeSerial((w, _) =>
        {
            if (w.Length == 20) return TypeReply("E6");
            if (w[2] == 23 + 2) return Ok10; // download cmd (name "PROJ" -> 25 bytes)
            if (w.Length == 10) return naks++ == 0 ? [0x56, 0xAB, 0, 0, 0, 0, 0, 0, 0, 0xCF] : Ok10;
            return Ok10;
        });
        var outcome = await BleProtocol.RunAsync(port, p, SoftwareType.E6RCU, null, Delays.None, default);
        Assert.Equal(DownloadStatus.Ok, outcome.Status);
        // type, download, header(nak), header, page0, header, page1, end
        Assert.Equal(8, port.Writes.Count);
        Assert.Equal(BleProtocol.EndCmd(), port.Writes[^1]);
    }

    [Theory]
    [InlineData(SoftwareType.E7RCU, "E7")] // original has no E7RCU branch
    [InlineData(SoftwareType.M2RCU, "M6")]
    public async Task Run_WrongModel_IsInvalidMcu(SoftwareType type, string model)
    {
        var port = new FakeSerial((_, _) => TypeReply(model));
        var outcome = await BleProtocol.RunAsync(port, new BleProtocol(TestFiles.Make(10)), type, null, Delays.None, default);
        Assert.Equal(DownloadStatus.InvalidMcu, outcome.Status);
    }

    [Fact]
    public async Task Run_NoReply_IsTimeOut()
    {
        var port = new FakeSerial((_, _) => []);
        var outcome = await BleProtocol.RunAsync(port, new BleProtocol(TestFiles.Make(10)), SoftwareType.E6RCU, null, Delays.None, default);
        Assert.Equal(DownloadStatus.TimeOut, outcome.Status);
    }
}

public class WifiProtocolTests
{
    private static readonly IPAddress Local = IPAddress.Parse("192.168.1.10");
    private static readonly IPEndPoint Broadcast = new(IPAddress.Broadcast, 28000);

    [Fact]
    public void PairRequestAndEnvelope_ExactBytes()
    {
        var p = new WifiProtocol(TestFiles.Make(10), SoftwareType.E6RCU, "123456"); // 0x01E240
        Assert.Equal(new byte[] { 0x78, 0xCD, 0x00, 0x0E, 0xAA, 0x01, 0x01, 0xE2, 0x40, 192, 168, 1, 10, 0xBE }, p.PairRequest(Local));
        Assert.Equal(new byte[] { 0x78, 0xCD, 0x00, 0x0C, 0xEE, 0xFF, 0x01, 0xE2, 0x40, 0x11, 0x22, 0xBE }, p.Wrap([0x11, 0x22]));
        var m = new WifiProtocol(TestFiles.Make(10), SoftwareType.MRCU, "0");
        Assert.Equal(new byte[] { 0xAA, 0x03 }, m.Wrap([1])[4..6]);
    }

    [Fact]
    public void DataFrame_SequenceEndiannessAndChecksum()
    {
        var file = TestFiles.Make(1024 * 300, content: _ => 1);
        var p = new WifiProtocol(file, SoftwareType.E6RCU, "0");
        var f = p.DataFrame(258, out var sum);
        Assert.Equal(1030, f.Length);
        Assert.Equal(new byte[] { 0x56, 0xAB, 0x01, 0x02 }, f[..4]); // big endian
        Assert.Equal(0, sum); // 1024 * 1 mod 256
        Assert.Equal(3, f[^2]);
        Assert.Equal(0xCF, f[^1]);
        var m = new WifiProtocol(file, SoftwareType.MRCU, "0").DataFrame(258, out _);
        Assert.Equal(new byte[] { 0x02, 0x01 }, m[2..4]); // little endian on MRCU
    }

    private static byte[] Reply(byte a, byte b, byte c = 0)
    {
        var r = new byte[15];
        r[11] = a; r[12] = b; r[13] = c;
        return r;
    }

    private static FakeUdp Controller(byte model, Func<int, bool>? dropPacket = null) => new((d, to, i) =>
    {
        if (d[4] == 0xAA && d[5] == 0x01) return [0x01]; // pair reply
        var inner = d[9..^1];
        if (inner.Length == 13)
            return inner[2] switch { 2 => Reply(3, model, 1), _ => Reply(5, 1) };
        return dropPacket?.Invoke(i) == true ? null : Reply(5, 4, BinFile.ByteSum(inner.AsSpan(4, 1024)));
    });

    [Fact]
    public async Task Run_Success_PairsThenSendsAllSteps()
    {
        var p = new WifiProtocol(TestFiles.Make(2500), SoftwareType.IQ2Robot, "000000");
        var udp = Controller(10);
        var outcome = await WifiProtocol.RunAsync(udp, p, Local, Broadcast, TimeSpan.FromMinutes(1), null, Delays.None, default);
        Assert.Equal(DownloadStatus.Ok, outcome.Status);
        Assert.Equal(Broadcast, udp.Sent[0].To);
        Assert.All(udp.Sent.Skip(1), s => Assert.Equal(FakeUdp.Controller, s.To));
        Assert.Equal(1 + 1 + 4 + 3, udp.Sent.Count);
        Assert.Equal(p.Wrap(p.PageNumCmd()), udp.Sent[2].Data);
    }

    [Fact]
    public async Task Run_WrongModel_IsInvalidMcu()
    {
        var p = new WifiProtocol(TestFiles.Make(10), SoftwareType.X3RCU, "0");
        var outcome = await WifiProtocol.RunAsync(Controller(1), p, Local, Broadcast, TimeSpan.FromMinutes(1), null, Delays.None, default);
        Assert.Equal(DownloadStatus.InvalidMcu, outcome.Status);
    }

    [Fact]
    public async Task Run_PacketRetriedFiveTimesThenTimeOut()
    {
        var p = new WifiProtocol(TestFiles.Make(2000), SoftwareType.E6RCU, "0");
        var udp = Controller(0, dropPacket: i => i >= 6);
        var outcome = await WifiProtocol.RunAsync(udp, p, Local, Broadcast, TimeSpan.FromMinutes(1), null, Delays.None, default);
        Assert.Equal(DownloadStatus.TimeOut, outcome.Status);
        Assert.Equal(6 + 5, udp.Sent.Count);
    }

    [Fact]
    public async Task Run_NoPairReply_TimesOut()
    {
        var udp = new FakeUdp((_, _, _) => null);
        var outcome = await WifiProtocol.RunAsync(udp, new WifiProtocol(TestFiles.Make(10), SoftwareType.E6RCU, "0"), Local, Broadcast, TimeSpan.Zero, null, Delays.None, default);
        Assert.Equal(DownloadStatus.TimeOut, outcome.Status);
    }

    [Fact]
    public void Resync_FindsNextMarker()
    {
        byte[] noisy = [0x00, 0x00, 0x78, 0xCD, 1, 2, 3, 4, 5, 6, 7, 8];
        Assert.Equal(noisy[2..], WifiProtocol.Resync(noisy));
        Assert.Null(WifiProtocol.Resync([0x78, 0xCD, 1]));
    }

    [Fact]
    public void PairCode_RejectsNonDigits() => Assert.Throws<ArgumentException>(() => WifiProtocol.ParsePairCode("12ab"));
}

public class DriveAndMethodsTests
{
    [Theory]
    [InlineData(SoftwareType.E6RCU, "E6-RCU", 1L, true)]
    [InlineData(SoftwareType.E6RCU, "NO NAME", 14393344L, true)]
    [InlineData(SoftwareType.E6RCU, "E7-RCU", 1L, false)]
    [InlineData(SoftwareType.X3RCU, "x", 8359936L, true)]
    [InlineData(SoftwareType.MRCU, "M-RCU", 1L, true)]
    [InlineData(SoftwareType.C1RCU, "C1-RCU", 1L, false)]
    public void DriveCriteria_LabelOrSize(SoftwareType type, string label, long size, bool expected) =>
        Assert.Equal(expected, DriveCriteria.Matches(type, label, size));

    [Fact]
    public async Task DriveCopy_OverwritesReadOnlyFile()
    {
        var root = Directory.CreateTempSubdirectory("roboexp-drive").FullName;
        var src = Path.Combine(Directory.CreateTempSubdirectory("roboexp-src").FullName, "PROJ.bin");
        File.WriteAllBytes(src, [1, 2, 3]);
        var dest = Path.Combine(root, "PROJ.bin");
        File.WriteAllBytes(dest, [9]);
        File.SetAttributes(dest, FileAttributes.ReadOnly);
        var outcome = await RemovableDriveDownloader.CopyAsync(src, root, null, Delays.None, default);
        Assert.Equal(DownloadStatus.Ok, outcome.Status);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(dest));
    }

    [Fact]
    public async Task DriveCopy_OverwritesWriteOnlyUnicodeTargetWithoutRequiringReadPermission()
    {
        if (OperatingSystem.IsWindows()) return;
        var work = Directory.CreateTempSubdirectory("roboexp-write-only").FullName;
        var volume = Directory.CreateDirectory(Path.Combine(work, "E6-RCU")).FullName;
        var src = Path.Combine(work, "麦轮程序.bin");
        var dest = Path.Combine(volume, Path.GetFileName(src));
        byte[] bytes = [1, 2, 3, 4];
        try
        {
            File.WriteAllBytes(src, bytes);
            File.WriteAllBytes(dest, [9, 9, 9, 9, 9, 9]);
            File.SetUnixFileMode(dest, UnixFileMode.UserWrite);
            var outcome = await RemovableDriveDownloader.CopyAsync(src, volume, null, Delays.None, default);
            Assert.Equal(DownloadStatus.Ok, outcome.Status);
            Assert.Equal(UnixFileMode.UserWrite, File.GetUnixFileMode(dest));
            File.SetUnixFileMode(dest, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Equal(bytes, File.ReadAllBytes(dest));
        }
        finally
        {
            if (File.Exists(dest)) File.SetUnixFileMode(dest, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public async Task DriveCopy_MissingVolume_IsDeviceNotFound()
    {
        var src = Path.GetTempFileName();
        var outcome = await RemovableDriveDownloader.CopyAsync(src, "/nonexistent-roboexp-volume", null, Delays.None, default);
        Assert.Equal(DownloadStatus.DeviceNotFound, outcome.Status);
    }

    [Fact]
    public void Methods_AllTypesGetHidBluetoothWifi_DriveExceptC1()
    {
        foreach (var t in RobotType.Supported)
        {
            var kinds = DownloadMethods.For(t).Select(d => d.Kind).ToList();
            Assert.Contains(TransportKind.Hid, kinds);
            Assert.Contains(TransportKind.Bluetooth, kinds);
            Assert.Contains(TransportKind.Wifi, kinds);
            Assert.Equal(t.SoftwareType != SoftwareType.C1RCU, kinds.Contains(TransportKind.RemovableDrive));
        }
    }

    [Fact]
    public async Task Wifi_FileOver64K_IsFileTooBig()
    {
        var src = Path.GetTempFileName();
        File.WriteAllBytes(src, new byte[65536]);
        var outcome = await new WifiDownloader().DownloadAsync(src, new RobotType(SoftwareType.E6RCU),
            new DownloadTarget(TransportKind.Wifi, WifiDownloader.BroadcastId, ""), null, default);
        Assert.Equal(DownloadStatus.FileTooBig, outcome.Status);
    }

    [Fact]
    public async Task MissingFile_IsReadFileFailed()
    {
        var outcome = await new HidDownloader().DownloadAsync("/nonexistent/x.bin", new RobotType(SoftwareType.E6RCU),
            new DownloadTarget(TransportKind.Hid, "x", ""), null, default);
        Assert.Equal(DownloadStatus.ReadFileFailed, outcome.Status);
    }
}
