namespace RoboExp.Device.Protocol;

/// <summary>Firmware image plus the metadata every original LoadFile() variant derives from it.</summary>
internal sealed record BinFile(string Path, byte[] Bytes, DateTime LastWriteTime)
{
    public string NameWithoutExtension => System.IO.Path.GetFileNameWithoutExtension(Path);

    public static BinFile Load(string path) =>
        new(path, File.ReadAllBytes(path), File.GetLastWriteTime(path));

    /// <summary>Number of <paramref name="packetSize"/> packets (last one partial).</summary>
    public int PacketCount(int packetSize) => (Bytes.Length + packetSize - 1) / packetSize;

    /// <summary>File name as 8 bytes, ASCII truncated, space padded (original (byte)char cast).</summary>
    public byte[] FileName8()
    {
        var name = NameWithoutExtension;
        var result = new byte[8];
        for (var i = 0; i < 8; i++)
            result[i] = i < name.Length ? unchecked((byte)name[i]) : (byte)' ';
        return result;
    }

    /// <summary>Packet <paramref name="index"/> of <paramref name="packetSize"/> bytes, tail padded with <paramref name="pad"/>.</summary>
    public byte[] Packet(int index, int packetSize, byte pad)
    {
        var packet = new byte[packetSize];
        var offset = index * packetSize;
        var count = Math.Clamp(Bytes.Length - offset, 0, packetSize);
        Array.Copy(Bytes, offset, packet, 0, count);
        if (pad != 0)
            packet.AsSpan(count).Fill(pad);
        return packet;
    }

    /// <summary>FAT date (little endian): ((year-1980)&lt;&lt;9)|(month&lt;&lt;5)|day.</summary>
    public static byte[] FatDate(DateTime t)
    {
        var v = ((t.Year - 1980) << 9) | (t.Month << 5) | t.Day;
        return [(byte)v, (byte)(v >> 8)];
    }

    /// <summary>FAT time (little endian): (hour&lt;&lt;11)|(minute&lt;&lt;5)|(second/2).</summary>
    public static byte[] FatTime(DateTime t)
    {
        var v = (t.Hour << 11) | (t.Minute << 5) | (t.Second / 2);
        return [(byte)v, (byte)(v >> 8)];
    }

    public static byte ByteSum(ReadOnlySpan<byte> data)
    {
        byte b = 0;
        foreach (var x in data) unchecked { b += x; }
        return b;
    }

    public static int IntSum(ReadOnlySpan<byte> data)
    {
        var n = 0;
        foreach (var x in data) n += x;
        return n;
    }
}
