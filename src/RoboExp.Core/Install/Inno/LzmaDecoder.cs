using System.Runtime.CompilerServices;

namespace RoboExp.Core.Install.Inno;

/// <summary>
/// Raw LZMA decoder (the algorithm of the public-domain LZMA SDK) used for Inno Setup headers and file chunks.
/// Pull-based: <see cref="Decode"/> produces at most the requested number of bytes and keeps any unfinished match
/// for the next call, so arbitrarily large solid chunks stream through a fixed dictionary buffer.
/// </summary>
internal sealed class LzmaDecoder
{
    private const int NumStates = 12;
    private const int PosStatesMax = 1 << 4;
    private const int EndPosModelIndex = 14;
    private const int NumFullDistances = 1 << (EndPosModelIndex >> 1);
    private const int NumAlignBits = 4;
    private const int MatchMinLen = 2;

    private readonly InputBuffer _in;
    private readonly byte[] _dict;
    private int _dictPos;
    private long _dictFilled; // bytes written since the last dictionary reset (capped use: distance check)

    private int _lc, _lp, _pb;
    private ushort[] _literal = [];
    private readonly ushort[] _isMatch = new ushort[NumStates << 4];
    private readonly ushort[] _isRep = new ushort[NumStates];
    private readonly ushort[] _isRepG0 = new ushort[NumStates];
    private readonly ushort[] _isRepG1 = new ushort[NumStates];
    private readonly ushort[] _isRepG2 = new ushort[NumStates];
    private readonly ushort[] _isRep0Long = new ushort[NumStates << 4];
    private readonly ushort[] _posSlot = new ushort[4 << 6];
    private readonly ushort[] _posDecoders = new ushort[1 + NumFullDistances - EndPosModelIndex];
    private readonly ushort[] _align = new ushort[1 << NumAlignBits];
    private readonly LenDecoder _len = new();
    private readonly LenDecoder _repLen = new();

    private int _state;
    private uint _rep0, _rep1, _rep2, _rep3;
    private int _pendingLen;
    private long _totalPos;

    private uint _range, _code;

    /// <summary>True after an end-of-stream marker was decoded.</summary>
    public bool Finished { get; private set; }

    public LzmaDecoder(Stream input, uint dictSize)
    {
        _in = new InputBuffer(input);
        _dict = new byte[Math.Max(dictSize, 1u << 12)];
    }

    internal InputBuffer Input => _in;

    /// <summary>Sets lc/lp/pb from the packed properties byte <c>(pb * 5 + lp) * 9 + lc</c>.</summary>
    public void SetProperties(byte props)
    {
        if (props >= 9 * 5 * 5) throw new InvalidDataException("Invalid LZMA properties byte");
        _lc = props % 9;
        _lp = props / 9 % 5;
        _pb = props / 45;
        var size = 0x300 << (_lc + _lp);
        if (_literal.Length != size) _literal = new ushort[size];
    }

    public void ResetState()
    {
        Array.Fill(_literal, (ushort)1024);
        Array.Fill(_isMatch, (ushort)1024);
        Array.Fill(_isRep, (ushort)1024);
        Array.Fill(_isRepG0, (ushort)1024);
        Array.Fill(_isRepG1, (ushort)1024);
        Array.Fill(_isRepG2, (ushort)1024);
        Array.Fill(_isRep0Long, (ushort)1024);
        Array.Fill(_posSlot, (ushort)1024);
        Array.Fill(_posDecoders, (ushort)1024);
        Array.Fill(_align, (ushort)1024);
        _len.Reset();
        _repLen.Reset();
        _state = 0;
        _rep0 = _rep1 = _rep2 = _rep3 = 0;
        _pendingLen = 0;
    }

    public void ResetDictionary()
    {
        _dictPos = 0;
        _dictFilled = 0;
        _totalPos = 0;
    }

    /// <summary>Starts a range-coded segment (5 bytes: a zero byte then the big-endian initial code).</summary>
    public void InitRangeCoder()
    {
        if (_in.ReadByte() != 0) throw new InvalidDataException("Corrupt LZMA stream (range coder init)");
        _code = 0;
        _range = 0xFFFFFFFF;
        for (var i = 0; i < 4; i++) _code = (_code << 8) | _in.ReadByte();
        if (_code == _range) throw new InvalidDataException("Corrupt LZMA stream");
    }

    /// <summary>True when an unfinished match still has bytes to emit.</summary>
    public bool HasPending => _pendingLen > 0;

    /// <summary>Copies <paramref name="count"/> raw bytes (LZMA2 uncompressed chunk) through the dictionary.</summary>
    public void CopyUncompressed(Span<byte> output)
    {
        _in.ReadExactly(output);
        foreach (var b in output) PutByte(b);
    }

    /// <summary>Decodes up to <c>output.Length</c> bytes. Returns the count written; 0 only when <see cref="Finished"/>.</summary>
    public int Decode(Span<byte> output)
    {
        var n = 0;
        var dict = _dict;
        while (n < output.Length)
        {
            if (_pendingLen > 0)
            {
                var src = _dictPos - (int)_rep0 - 1;
                if (src < 0) src += dict.Length;
                var count = Math.Min(_pendingLen, output.Length - n);
                _pendingLen -= count;
                while (count-- > 0)
                {
                    var b = dict[src];
                    if (++src == dict.Length) src = 0;
                    output[n++] = b;
                    PutByte(b);
                }
                continue;
            }
            if (Finished) break;

            var posState = (int)_totalPos & ((1 << _pb) - 1);
            if (Bit(ref _isMatch[(_state << 4) + posState]) == 0)
            {
                var prev = _dictFilled > 0 ? dict[(_dictPos == 0 ? dict.Length : _dictPos) - 1] : (byte)0;
                var baseIdx = 0x300 * ((((int)_totalPos & ((1 << _lp) - 1)) << _lc) + (prev >> (8 - _lc)));
                var symbol = 1;
                if (_state >= 7)
                {
                    int matchByte = GetByte(_rep0);
                    while (symbol < 0x100)
                    {
                        var matchBit = (matchByte >> 7) & 1;
                        matchByte <<= 1;
                        var bit = Bit(ref _literal[baseIdx + 0x100 + (matchBit << 8) + symbol]);
                        symbol = (symbol << 1) | bit;
                        if (matchBit != bit) break;
                    }
                }
                while (symbol < 0x100) symbol = (symbol << 1) | Bit(ref _literal[baseIdx + symbol]);
                var lit = (byte)symbol;
                output[n++] = lit;
                PutByte(lit);
                _state = _state < 4 ? 0 : _state < 10 ? _state - 3 : _state - 6;
                continue;
            }

            int len;
            if (Bit(ref _isRep[_state]) == 1)
            {
                if (_dictFilled == 0) throw new InvalidDataException("Corrupt LZMA stream (rep at start)");
                if (Bit(ref _isRepG0[_state]) == 0)
                {
                    if (Bit(ref _isRep0Long[(_state << 4) + posState]) == 0)
                    {
                        _state = _state < 7 ? 9 : 11;
                        var b = GetByte(_rep0);
                        output[n++] = b;
                        PutByte(b);
                        continue;
                    }
                }
                else
                {
                    uint dist;
                    if (Bit(ref _isRepG1[_state]) == 0) dist = _rep1;
                    else
                    {
                        if (Bit(ref _isRepG2[_state]) == 0) dist = _rep2;
                        else { dist = _rep3; _rep3 = _rep2; }
                        _rep2 = _rep1;
                    }
                    _rep1 = _rep0;
                    _rep0 = dist;
                }
                len = _repLen.Decode(this, posState);
                _state = _state < 7 ? 8 : 11;
            }
            else
            {
                _rep3 = _rep2;
                _rep2 = _rep1;
                _rep1 = _rep0;
                len = _len.Decode(this, posState);
                _state = _state < 7 ? 7 : 10;
                _rep0 = DecodeDistance(len);
                if (_rep0 == 0xFFFFFFFF)
                {
                    Finished = true;
                    break;
                }
                if (_rep0 >= _dictFilled || _rep0 >= dict.Length)
                    throw new InvalidDataException("Corrupt LZMA stream (distance beyond dictionary)");
            }
            _pendingLen = len + MatchMinLen;
        }
        return n;
    }

    private uint DecodeDistance(int len)
    {
        var lenState = Math.Min(len, 3);
        var posSlot = BitTree(_posSlot, lenState << 6, 6);
        if (posSlot < 4) return (uint)posSlot;
        var numDirectBits = (posSlot >> 1) - 1;
        var dist = (uint)((2 | (posSlot & 1)) << numDirectBits);
        if (posSlot < EndPosModelIndex)
            return dist + ReverseBitTree(_posDecoders, (int)dist - posSlot, numDirectBits);
        dist += DirectBits(numDirectBits - NumAlignBits) << NumAlignBits;
        return dist + ReverseBitTree(_align, 0, NumAlignBits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte GetByte(uint distance)
    {
        var src = _dictPos - (int)distance - 1;
        if (src < 0) src += _dict.Length;
        return _dict[src];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PutByte(byte b)
    {
        _dict[_dictPos] = b;
        if (++_dictPos == _dict.Length) _dictPos = 0;
        _dictFilled++;
        _totalPos++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Bit(ref ushort prob)
    {
        var bound = (_range >> 11) * prob;
        int bit;
        if (_code < bound)
        {
            _range = bound;
            prob += (ushort)((2048 - prob) >> 5);
            bit = 0;
        }
        else
        {
            _range -= bound;
            _code -= bound;
            prob -= (ushort)(prob >> 5);
            bit = 1;
        }
        if (_range < (1u << 24))
        {
            _range <<= 8;
            _code = (_code << 8) | _in.ReadByte();
        }
        return bit;
    }

    internal int BitTree(ushort[] probs, int offset, int numBits)
    {
        var m = 1;
        for (var i = 0; i < numBits; i++) m = (m << 1) | Bit(ref probs[offset + m]);
        return m - (1 << numBits);
    }

    private uint ReverseBitTree(ushort[] probs, int offset, int numBits)
    {
        var m = 1;
        uint symbol = 0;
        for (var i = 0; i < numBits; i++)
        {
            var bit = Bit(ref probs[offset + m]);
            m = (m << 1) | bit;
            symbol |= (uint)bit << i;
        }
        return symbol;
    }

    private uint DirectBits(int numBits)
    {
        uint result = 0;
        for (var i = 0; i < numBits; i++)
        {
            _range >>= 1;
            uint bit = _code >= _range ? 1u : 0u;
            if (bit != 0) _code -= _range;
            result = (result << 1) | bit;
            if (_range < (1u << 24))
            {
                _range <<= 8;
                _code = (_code << 8) | _in.ReadByte();
            }
        }
        return result;
    }

    private sealed class LenDecoder
    {
        private ushort _choice, _choice2;
        private readonly ushort[] _low = new ushort[PosStatesMax << 3];
        private readonly ushort[] _mid = new ushort[PosStatesMax << 3];
        private readonly ushort[] _high = new ushort[1 << 8];

        public void Reset()
        {
            _choice = _choice2 = 1024;
            Array.Fill(_low, (ushort)1024);
            Array.Fill(_mid, (ushort)1024);
            Array.Fill(_high, (ushort)1024);
        }

        public int Decode(LzmaDecoder d, int posState)
        {
            if (d.Bit(ref _choice) == 0) return d.BitTree(_low, posState << 3, 3);
            if (d.Bit(ref _choice2) == 0) return 8 + d.BitTree(_mid, posState << 3, 3);
            return 16 + d.BitTree(_high, 0, 8);
        }
    }

    /// <summary>Byte-at-a-time reader over a buffered stream; throws on premature end of input.</summary>
    internal sealed class InputBuffer(Stream stream)
    {
        private readonly byte[] _buffer = new byte[1 << 16];
        private int _pos, _len;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadByte()
        {
            if (_pos == _len) Fill();
            return _buffer[_pos++];
        }

        public void ReadExactly(Span<byte> dest)
        {
            var n = 0;
            while (n < dest.Length)
            {
                if (_pos == _len) Fill();
                var c = Math.Min(dest.Length - n, _len - _pos);
                _buffer.AsSpan(_pos, c).CopyTo(dest[n..]);
                _pos += c;
                n += c;
            }
        }

        private void Fill()
        {
            _len = stream.Read(_buffer, 0, _buffer.Length);
            _pos = 0;
            if (_len <= 0) throw new EndOfStreamException("Truncated LZMA stream");
        }
    }
}

/// <summary>Inno Setup LZMA1 stream: 5-byte header (properties byte + little-endian dictionary size), then raw LZMA.</summary>
internal sealed class InnoLzma1Stream : ReadOnlyStreamBase
{
    private readonly LzmaDecoder _decoder;

    public InnoLzma1Stream(Stream input)
    {
        Span<byte> header = stackalloc byte[5];
        input.ReadExactly(header);
        _decoder = new LzmaDecoder(input, BitConverter.ToUInt32(header[1..]));
        _decoder.SetProperties(header[0]);
        _decoder.ResetState();
        _decoder.ResetDictionary();
        _decoder.InitRangeCoder();
    }

    public override int Read(Span<byte> buffer) => _decoder.Decode(buffer);
}

/// <summary>Inno Setup LZMA2 stream: 1 dictionary-size byte, then LZMA2 chunks (no .xz container).</summary>
internal sealed class InnoLzma2Stream : ReadOnlyStreamBase
{
    private readonly LzmaDecoder _decoder;
    private int _chunkLeft; // unpacked bytes left in the current chunk
    private bool _chunkCompressed;
    private bool _needDictReset = true;
    private bool _end;

    public InnoLzma2Stream(Stream input)
    {
        var prop = input.ReadByte();
        if (prop is < 0 or > 40) throw new InvalidDataException("Invalid LZMA2 dictionary size");
        var dict = prop == 40 ? 0xFFFFFFFF : (2u | ((uint)prop & 1)) << (prop / 2 + 11);
        _decoder = new LzmaDecoder(input, (uint)Math.Min(dict, 1u << 30));
    }

    public override int Read(Span<byte> buffer)
    {
        var n = 0;
        while (n < buffer.Length && !_end)
        {
            if (_chunkLeft == 0 && !StartChunk()) break;
            var want = Math.Min(buffer.Length - n, _chunkLeft);
            int got;
            if (_chunkCompressed)
            {
                got = _decoder.Decode(buffer.Slice(n, want));
                if (got == 0) throw new InvalidDataException("Corrupt LZMA2 chunk");
            }
            else
            {
                _decoder.CopyUncompressed(buffer.Slice(n, want));
                got = want;
            }
            n += got;
            _chunkLeft -= got;
            if (_chunkLeft == 0 && _chunkCompressed && _decoder.HasPending)
                throw new InvalidDataException("Corrupt LZMA2 chunk (match crosses chunk end)");
        }
        return n;
    }

    private bool StartChunk()
    {
        var input = _decoder.Input;
        var control = (int)input.ReadByte();
        if (control == 0) { _end = true; return false; }
        if (control is 1 or 2)
        {
            if (control == 1) { _decoder.ResetDictionary(); _needDictReset = false; }
            else if (_needDictReset) throw new InvalidDataException("Corrupt LZMA2 stream (missing dictionary reset)");
            _chunkLeft = (int)((input.ReadByte() << 8) | input.ReadByte()) + 1;
            _chunkCompressed = false;
            return true;
        }
        if (control < 0x80) throw new InvalidDataException("Corrupt LZMA2 control byte");
        _chunkLeft = (int)((((uint)control & 0x1F) << 16) | (input.ReadByte() << 8) | input.ReadByte()) + 1;
        _ = (input.ReadByte() << 8) | input.ReadByte(); // packed size: the range decoder consumes exactly this much
        var reset = (control >> 5) & 3;
        if (reset == 3) { _decoder.ResetDictionary(); _needDictReset = false; }
        else if (_needDictReset) throw new InvalidDataException("Corrupt LZMA2 stream (missing dictionary reset)");
        if (reset >= 2) _decoder.SetProperties((byte)input.ReadByte());
        if (reset >= 1) _decoder.ResetState();
        _decoder.InitRangeCoder();
        _chunkCompressed = true;
        return true;
    }
}

/// <summary>Minimal forward-only read-only stream base.</summary>
internal abstract class ReadOnlyStreamBase : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public abstract override int Read(Span<byte> buffer);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
