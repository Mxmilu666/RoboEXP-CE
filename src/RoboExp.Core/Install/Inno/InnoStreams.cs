using System.Text;

namespace RoboExp.Core.Install.Inno;

/// <summary>CRC-32 (IEEE 802.3, reflected 0xEDB88320) as used by Inno Setup block and loader checksums.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Update(0, data);

    /// <summary>Continues a CRC: pass the previous result (0 to start).</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        crc = ~crc;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}

/// <summary>
/// Read-only window of <paramref name="length"/> bytes starting at the base stream's current position.
/// Does not own the base stream.
/// </summary>
internal sealed class SubStream(Stream baseStream, long length) : ReadOnlyStreamBase
{
    private long _left = length;

    public override int Read(Span<byte> buffer)
    {
        if (_left <= 0) return 0;
        if (buffer.Length > _left) buffer = buffer[..(int)_left];
        var n = baseStream.Read(buffer);
        if (n <= 0) throw new EndOfStreamException("Installer is truncated");
        _left -= n;
        return n;
    }
}

/// <summary>
/// Inno Setup block payload: 4096-byte chunks each preceded by their CRC32 (the last chunk may be shorter).
/// Throws <see cref="InvalidDataException"/> on a checksum mismatch before returning any data of that chunk.
/// </summary>
internal sealed class InnoBlockChunkStream(Stream source) : ReadOnlyStreamBase
{
    private readonly byte[] _buffer = new byte[4096];
    private int _pos, _len;

    public override int Read(Span<byte> buffer)
    {
        var n = 0;
        while (n < buffer.Length)
        {
            if (_pos == _len && !NextChunk()) break;
            var c = Math.Min(buffer.Length - n, _len - _pos);
            _buffer.AsSpan(_pos, c).CopyTo(buffer[n..]);
            _pos += c;
            n += c;
        }
        return n;
    }

    private bool NextChunk()
    {
        Span<byte> crcBytes = stackalloc byte[4];
        var got = source.ReadAtLeast(crcBytes, 4, throwOnEndOfStream: false);
        if (got == 0) return false;
        if (got != 4) throw new InvalidDataException("Unexpected end of setup block");
        _len = source.ReadAtLeast(_buffer, _buffer.Length, throwOnEndOfStream: false);
        if (_len == 0) throw new InvalidDataException("Unexpected end of setup block");
        if (Crc32.Compute(_buffer.AsSpan(0, _len)) != BitConverter.ToUInt32(crcBytes))
            throw new InvalidDataException("Setup block CRC32 mismatch");
        _pos = 0;
        return true;
    }
}

/// <summary>Little-endian primitive reader for Inno Setup's stored records.</summary>
internal sealed class InnoReader(Stream stream)
{
    private readonly byte[] _scratch = new byte[8];

    public byte U8()
    {
        stream.ReadExactly(_scratch, 0, 1);
        return _scratch[0];
    }

    public bool Bool() => U8() != 0;
    public short I16() { stream.ReadExactly(_scratch, 0, 2); return BitConverter.ToInt16(_scratch, 0); }
    public uint U32() { stream.ReadExactly(_scratch, 0, 4); return BitConverter.ToUInt32(_scratch, 0); }
    public int I32() => (int)U32();
    public ulong U64() { stream.ReadExactly(_scratch, 0, 8); return BitConverter.ToUInt64(_scratch, 0); }
    public long I64() => (long)U64();

    public byte[] Bytes(int count)
    {
        var data = new byte[count];
        stream.ReadExactly(data);
        return data;
    }

    public void Skip(long count)
    {
        Span<byte> sink = stackalloc byte[4096];
        while (count > 0)
        {
            var c = (int)Math.Min(count, sink.Length);
            stream.ReadExactly(sink[..c]);
            count -= c;
        }
    }

    /// <summary>Length-prefixed byte string (<c>util::binary_string</c>).</summary>
    public byte[] BinaryString()
    {
        var len = U32();
        if (len > 64 << 20) throw new InvalidDataException("Implausible string length in setup header");
        return Bytes((int)len);
    }

    public void SkipString() => Skip(U32());

    /// <summary>UTF-16LE string (unicode installers store all "encoded" strings this way).</summary>
    public string WideString() => Encoding.Unicode.GetString(BinaryString());

    /// <summary>Windows version range: two × (build u16, minor u8, major u8, nt build u16, minor, major, sp minor, sp major).</summary>
    public void SkipWindowsVersionRange() => Skip(20);

    /// <summary>
    /// Reads <paramref name="count"/> packed flag bits (<c>stored_flag_reader</c>/<c>stored_bitfield</c>):
    /// ceil(count/8) bytes, plus one padding byte when that is exactly 3 (32-bit set alignment).
    /// </summary>
    public ulong Flags(int count)
    {
        var bytes = (count + 7) / 8;
        ulong value = 0;
        for (var i = 0; i < bytes; i++) value |= (ulong)U8() << (8 * i);
        if (bytes == 3) U8();
        return value;
    }
}
