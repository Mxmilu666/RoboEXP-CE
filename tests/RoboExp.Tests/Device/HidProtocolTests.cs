using RoboExp.Core.Model;
using RoboExp.Device;
using RoboExp.Device.Protocol;
using RoboExp.Device.Transport;

namespace RoboExp.Tests.Device;

public class HidProtocolTests
{
    private static RcuDownloadProtocol Proto(int length, SoftwareType type, Func<int, byte>? content = null) =>
        new(TestFiles.Make(length, content: content), type);

    [Fact]
    public void FatDateAndTime_MatchOriginalBitPacking()
    {
        // 2024-05-17 13:45:38 -> date (44<<9)|(5<<5)|17 = 0x58B1, time (13<<11)|(45<<5)|19 = 0x6DB3, little endian
        Assert.Equal(new byte[] { 0xB1, 0x58 }, BinFile.FatDate(TestFiles.Stamp));
        Assert.Equal(new byte[] { 0xB3, 0x6D }, BinFile.FatTime(TestFiles.Stamp));
    }

    [Fact]
    public void SimpleCommands_HaveHeaderChecksumAndTrailer()
    {
        var p = Proto(10, SoftwareType.E6RCU);
        var back = p.RequestBackCmd();
        var info = p.RequestRcuInfoCmd();
        Assert.Equal(64, back.Length);
        Assert.Equal(new byte[] { 0x56, 0xAB, 0x01 }, back[..3]);
        Assert.Equal(0x01, back[62]);
        Assert.Equal(0xCF, back[63]);
        Assert.Equal(0x02, info[2]);
        Assert.Equal(0x02, info[62]);
        Assert.All(back[3..62], b => Assert.Equal(0, b));
    }

    [Fact]
    public void DownloadCommand_ExactBytes_WithFfPaddedChecksum()
    {
        var p = Proto(100, SoftwareType.E6RCU, _ => 1);
        var expected = new byte[64];
        new byte[]
        {
            0x56, 0xAB, 0x04, (byte)'P', (byte)'R', (byte)'O', (byte)'J', 0x20, 0x20, 0x20, 0x20,
            0x02, 0x00,             // 2 packets
            0xB3, 0x6D, 0xB1, 0x58, // FAT time, date
            0x48, 0x1C,             // 100*1 + 28*0xFF = 7240 = 0x1C48
        }.CopyTo(expected, 0);
        expected[62] = 0x4E;
        expected[63] = 0xCF;
        Assert.Equal(expected, p.RequestDownloadCmd());
    }

    [Fact]
    public void C1rcu_ChecksumIsNotPaddedWithFf()
    {
        var p = Proto(100, SoftwareType.C1RCU, _ => 1);
        Assert.Equal(new byte[] { 100, 0 }, p.FileChecksumBytes);
        Assert.Equal(new byte[] { 0x56, 0xAB, 0x40, 0xE1, 0x00, 0x1A, 0x00, 0x02 }, p.C1DownloadHeader()[..8]);
        Assert.All(p.C1DownloadHeader()[8..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void FileName_IsTruncatedToEightBytes()
    {
        var p = new RcuDownloadProtocol(TestFiles.Make(1, "LONGPROJECT"), SoftwareType.E6RCU);
        Assert.Equal("LONGPROJ"u8.ToArray(), p.FileNameBytes);
    }

    [Fact]
    public void DataPackets_LastPacketZeroPadded()
    {
        var p = Proto(70, SoftwareType.E6RCU, _ => 0xAA);
        Assert.Equal(2, p.PacketNum);
        var last = p.DataPacket(1);
        Assert.Equal(64, last.Length);
        Assert.All(last[..6], b => Assert.Equal(0xAA, b));
        Assert.All(last[6..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void SizeLimit_OnlyForLegacyTypes()
    {
        Assert.NotNull(RcuDownloadProtocol.CheckSize(65536, SoftwareType.ERCU));
        Assert.Null(RcuDownloadProtocol.CheckSize(65535, SoftwareType.ERCU));
        Assert.Null(RcuDownloadProtocol.CheckSize(200_000, SoftwareType.E2RCU));
        Assert.NotNull(RcuDownloadProtocol.CheckSize(64L * 65536, SoftwareType.E6RCU));
    }

    /// <summary>Device that accepts the ST protocol: replies [20]=rcuCode to info and correct packet sums.</summary>
    private static FakeHid StDevice(byte rcuCode, int badPacket = -1) => new((w, i) =>
    {
        if (i == 0) { var r = new byte[64]; r[20] = rcuCode; return r; }
        if (i < 3) return new byte[64];
        var sum = BinFile.IntSum(w);
        if (i - 3 == badPacket) sum++;
        var a = new byte[64];
        a[4] = (byte)(sum % 256);
        a[5] = (byte)(sum / 256);
        return a;
    });

    [Fact]
    public async Task St_Success_SendsInfoBackDownloadThenPackets()
    {
        var p = Proto(150, SoftwareType.E6RCU);
        var hid = StDevice(1);
        var reports = new List<DownloadProgress>();
        var outcome = await HidSessions.RunStAsync(hid, p, new SyncProgress(reports), Delays.None, default);
        Assert.Equal(DownloadStatus.Ok, outcome.Status);
        Assert.Equal(3 + 3, hid.Writes.Count);
        Assert.Equal(p.RequestRcuInfoCmd(), hid.Writes[0]);
        Assert.Equal(p.RequestBackCmd(), hid.Writes[1]);
        Assert.Equal(p.RequestDownloadCmd(), hid.Writes[2]);
        Assert.Equal(p.DataPacket(2), hid.Writes[5]);
        Assert.Equal(100, reports[^1].Percent);
    }

    [Theory]
    [InlineData(SoftwareType.E6RCU, 4)]
    [InlineData(SoftwareType.X3RCU, 1)]
    [InlineData(SoftwareType.IQ2Robot, 0)] // IQ2Robot is in no branch of DownloadX1RCU.Download
    public async Task St_WrongRcuCode_IsInvalidMcu(SoftwareType type, byte code)
    {
        var hid = StDevice(code);
        var outcome = await HidSessions.RunStAsync(hid, Proto(10, type), null, Delays.None, default);
        Assert.Equal(DownloadStatus.InvalidMcu, outcome.Status);
        Assert.Single(hid.Writes);
    }

    [Fact]
    public async Task St_ChecksumNak_StopsWithProtocolError()
    {
        var hid = StDevice(4, badPacket: 1);
        var outcome = await HidSessions.RunStAsync(hid, Proto(64 * 4, SoftwareType.X4RCU), null, Delays.None, default);
        Assert.Equal(DownloadStatus.ProtocolError, outcome.Status);
        Assert.Equal(3 + 2, hid.Writes.Count);
    }

    [Fact]
    public async Task St_NoReply_IsTimeOut()
    {
        var hid = new FakeHid((_, _) => null);
        var outcome = await HidSessions.RunStAsync(hid, Proto(10, SoftwareType.E2RCU), null, Delays.None, default);
        Assert.Equal(DownloadStatus.TimeOut, outcome.Status);
    }

    [Fact]
    public async Task St_WriteFailure_IsIoError()
    {
        var hid = new FakeHid((_, _) => new byte[64]) { FailWrites = true };
        var outcome = await HidSessions.RunStAsync(hid, Proto(10, SoftwareType.E2RCU), null, Delays.None, default);
        Assert.Equal(DownloadStatus.IoError, outcome.Status);
    }

    [Fact]
    public async Task St_Cancellation_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HidSessions.RunStAsync(StDevice(1), Proto(10, SoftwareType.E6RCU), null, Delays.None, cts.Token));
    }

    [Fact]
    public async Task C1_SendsHeaderThenPackets_LastPacketAcceptsFileChecksum()
    {
        var p = Proto(130, SoftwareType.C1RCU);
        var hid = new FakeHid((w, i) =>
        {
            var a = new byte[64];
            if (i is 1 or 2) a[5] = 1;
            if (i == 3) { a[7] = p.FileChecksumBytes[0]; a[6] = p.FileChecksumBytes[1]; }
            return a;
        });
        var outcome = await HidSessions.RunStAsync(hid, p, null, Delays.None, default);
        Assert.Equal(DownloadStatus.Ok, outcome.Status);
        Assert.Equal(p.C1DownloadHeader(), hid.Writes[0]);
        Assert.Equal(4, hid.Writes.Count);
    }

    [Fact]
    public async Task C1_MidPacketWithoutAck_IsProtocolError()
    {
        var hid = new FakeHid((_, _) => new byte[64]);
        var outcome = await HidSessions.RunStAsync(hid, Proto(200, SoftwareType.C1RCU), null, Delays.None, default);
        Assert.Equal(DownloadStatus.ProtocolError, outcome.Status);
        Assert.Equal(2, hid.Writes.Count);
    }

    [Fact]
    public async Task Legacy_HeaderBytesAndAckOffsets()
    {
        var p = Proto(300, SoftwareType.E6RCU); // 5 packets
        var hid = new FakeHid((w, i) =>
        {
            if (i == 0) return [0x77, 0x76, 0x06, 0xDD, 0x01, 0x00];
            var sum = BinFile.IntSum(w);
            var a = new byte[64];
            a[6] = (byte)(sum % 256);
            a[7] = (byte)(sum / 256);
            return a;
        });
        var outcome = await HidSessions.RunLegacyAsync(hid, p, null, Delays.None, default);
        Assert.Equal(DownloadStatus.Ok, outcome.Status);
        Assert.Equal(new byte[] { 0x77, 0x76, 0x09, 0xDD, 0x01, 0x00, 0x00, 0x05, 0x75 }, hid.Writes[0][..9]);
        Assert.Equal(6, hid.Writes.Count);
    }

    [Fact]
    public async Task Legacy_UnexpectedHeaderReply_IsProtocolError()
    {
        var hid = new FakeHid((_, _) => new byte[64]);
        var outcome = await HidSessions.RunLegacyAsync(hid, Proto(10, SoftwareType.E6RCU), null, Delays.None, default);
        Assert.Equal(DownloadStatus.ProtocolError, outcome.Status);
        Assert.Single(hid.Writes);
    }
}

/// <summary>Synchronous IProgress (Progress&lt;T&gt; posts asynchronously).</summary>
internal sealed class SyncProgress(List<DownloadProgress> sink) : IProgress<DownloadProgress>
{
    public void Report(DownloadProgress value) => sink.Add(value);
}
