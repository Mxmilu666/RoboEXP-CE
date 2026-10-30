using RoboExp.Core.Model;

namespace RoboExp.Device.Protocol;

/// <summary>
/// Port of RoboExp.RcuDownloadProtocol: 64-byte HID command frames
/// <c>[0]=0x56 [1]=0xAB [2]=cmd ... [62]=sum([2..61]) [63]=0xCF</c>.
/// </summary>
internal sealed class RcuDownloadProtocol
{
    public const int PacketSize = 64;
    public const int CmdLength = 64;
    /// <summary>Original LoadFile limit, applied only to SoftwareType &lt;= ERCU (none of the ARM controllers).</summary>
    public const int LegacyMaxFileSize = 65535;

    public BinFile File { get; }
    public SoftwareType Type { get; }
    public int PacketNum { get; }
    public byte[] FileNameBytes { get; }
    public byte[] LastWriteDateBytes { get; }
    public byte[] LastWriteTimeBytes { get; }
    /// <summary>[0]=low, [1]=high. Packet count is 2 bytes on the wire.</summary>
    public byte[] PacketNumBytes { get; }
    /// <summary>16-bit sum over PacketNum*64 bytes, tail padded with 0xFF except on C1RCU (no padding).</summary>
    public byte[] FileChecksumBytes { get; }

    public RcuDownloadProtocol(BinFile file, SoftwareType type)
    {
        File = file;
        Type = type;
        PacketNum = file.PacketCount(PacketSize);
        FileNameBytes = file.FileName8();
        LastWriteDateBytes = BinFile.FatDate(file.LastWriteTime);
        LastWriteTimeBytes = BinFile.FatTime(file.LastWriteTime);
        PacketNumBytes = [(byte)(PacketNum % 256), unchecked((byte)(PacketNum / 256))];
        var sum = BinFile.IntSum(file.Bytes);
        if (type != SoftwareType.C1RCU)
            sum += (PacketNum * PacketSize - file.Bytes.Length) * 0xFF;
        FileChecksumBytes = [(byte)(sum % 256), unchecked((byte)(sum / 256))];
    }

    /// <summary>Returns null when the file is acceptable, otherwise the FileTooBig reason.</summary>
    public static string? CheckSize(long length, SoftwareType type)
    {
        if (type <= SoftwareType.ERCU && length > LegacyMaxFileSize)
            return "File size over 64Kb";
        if ((length + PacketSize - 1) / PacketSize > 0xFFFF)
            return "File too big: packet count does not fit in 16 bits";
        return null;
    }

    public byte[] RequestBackCmd() => Cmd(1);

    public byte[] RequestRcuInfoCmd() => Cmd(2);

    public byte[] RequestDownloadCmd()
    {
        var cmd = new byte[CmdLength];
        cmd[2] = 4;
        FileNameBytes.CopyTo(cmd, 3);
        cmd[11] = PacketNumBytes[0];
        cmd[12] = PacketNumBytes[1];
        cmd[13] = LastWriteTimeBytes[0];
        cmd[14] = LastWriteTimeBytes[1];
        cmd[15] = LastWriteDateBytes[0];
        cmd[16] = LastWriteDateBytes[1];
        cmd[17] = FileChecksumBytes[0];
        cmd[18] = FileChecksumBytes[1];
        Format(cmd);
        return cmd;
    }

    /// <summary>C1RCU header used by DownloadX1RCU.C2download: 56 AB 40 E1 00 1A pnHi pnLo, rest zero, no checksum.</summary>
    public byte[] C1DownloadHeader()
    {
        var buf = new byte[CmdLength];
        buf[0] = 0x56; buf[1] = 0xAB; buf[2] = 0x40; buf[3] = 0xE1; buf[4] = 0x00; buf[5] = 0x1A;
        buf[6] = PacketNumBytes[1];
        buf[7] = PacketNumBytes[0];
        return buf;
    }

    /// <summary>Data packet i: 64 bytes, tail padded with 0x00 (the checksum padding differs, faithful to the original).</summary>
    public byte[] DataPacket(int index) => File.Packet(index, PacketSize, 0);

    private static byte[] Cmd(byte code)
    {
        var cmd = new byte[CmdLength];
        cmd[2] = code;
        Format(cmd);
        return cmd;
    }

    private static void Format(byte[] cmd)
    {
        cmd[0] = 0x56;
        cmd[1] = 0xAB;
        cmd[CmdLength - 2] = BinFile.ByteSum(cmd.AsSpan(2, CmdLength - 4));
        cmd[CmdLength - 1] = 0xCF;
    }
}
