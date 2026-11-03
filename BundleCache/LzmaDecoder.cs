using System;
using System.IO;

namespace Morgott.Preloader.BundleCache;

/// <summary>
/// LZMA (LZMA1) decoder, written from the public algorithm description in Igor Pavlov's LZMA SDK
/// (LzmaSpec.cpp reference decoder, public domain). Decodes one UnityFS LZMA block: 5 property bytes
/// (lc/lp/pb byte + little-endian dictionary size) followed by the range-coded stream, with the unpacked size
/// known from the block table. Output goes into a circular window (at least the dictionary size, a multiple of
/// <see cref="ChunkSize"/>) and every full <see cref="ChunkSize"/> segment (and the final partial one) is handed
/// to the sink while it is still contiguous in the window, so memory stays bounded by the dictionary size.
/// Any inconsistency (bad properties, a distance before the start, reading past the input, an unexpected end
/// marker) throws <see cref="InvalidDataException"/>.
/// </summary>
internal sealed class LzmaDecoder
{
    public const int ChunkSize = 128 * 1024;

    private const int NumStates = 12;
    private const int NumPosBitsMax = 4;
    private const int NumLenToPosStates = 4;
    private const int NumAlignBits = 4;
    private const int StartPosModelIndex = 4;
    private const int EndPosModelIndex = 14;
    private const int NumFullDistances = 1 << (EndPosModelIndex >> 1);
    private const int MatchMinLen = 2;
    private const uint TopValue = 1u << 24;

    private readonly Stream _input;
    private readonly long _inputEnd;
    private readonly byte[] _inBuf = new byte[64 * 1024];
    private int _inPos;
    private int _inLen;
    private long _inRead;
    private uint _range;
    private uint _code;

    private readonly int _lc;
    private readonly int _lp;
    private readonly int _pb;
    private readonly uint _dictSize;

    private readonly ushort[] _isMatch = new ushort[NumStates << NumPosBitsMax];
    private readonly ushort[] _isRep = new ushort[NumStates];
    private readonly ushort[] _isRepG0 = new ushort[NumStates];
    private readonly ushort[] _isRepG1 = new ushort[NumStates];
    private readonly ushort[] _isRepG2 = new ushort[NumStates];
    private readonly ushort[] _isRep0Long = new ushort[NumStates << NumPosBitsMax];
    private readonly ushort[] _posSlot = new ushort[NumLenToPosStates << 6];
    private readonly ushort[] _posDecoders = new ushort[1 + NumFullDistances - EndPosModelIndex];
    private readonly ushort[] _align = new ushort[1 << NumAlignBits];
    private readonly LenDecoder _lenDecoder = new();
    private readonly LenDecoder _repLenDecoder = new();
    private readonly ushort[] _literals;

    private int _endState;
    private long _endTotal;

    /// <param name="input">Positioned at the property bytes.</param>
    /// <param name="packedSize">Bytes of this block in <paramref name="input"/>, property bytes included.</param>
    public LzmaDecoder(Stream input, long packedSize)
    {
        if (packedSize < 5)
        {
            throw new InvalidDataException("LZMA block shorter than its properties");
        }
        _input = input;
        _inputEnd = packedSize;
        int d = ReadByte();
        if (d >= 9 * 5 * 5)
        {
            throw new InvalidDataException("bad LZMA properties");
        }
        _lc = d % 9;
        d /= 9;
        _lp = d % 5;
        _pb = d / 5;
        for (int i = 0; i < 4; i++)
        {
            _dictSize |= (uint)ReadByte() << (8 * i);
        }
        _literals = new ushort[0x300 << (_lc + _lp)];
    }

    /// <summary>Decodes exactly <paramref name="unpackedSize"/> bytes; <paramref name="sink"/> gets (window, offset, count).</summary>
    public void Decode(long unpackedSize, Action<byte[], int, int> sink)
    {
        long windowSize = Math.Max(_dictSize, 4096u);
        windowSize = (windowSize + ChunkSize - 1) / ChunkSize * ChunkSize;
        long cappedTotal = (unpackedSize + ChunkSize - 1) / ChunkSize * ChunkSize;
        windowSize = Math.Max(ChunkSize, Math.Min(windowSize, cappedTotal));
        if (windowSize > int.MaxValue)
        {
            throw new InvalidDataException("LZMA dictionary too large");
        }
        var window = new byte[windowSize];
        int wsize = (int)windowSize;

        foreach (ushort[] probs in new[] { _isMatch, _isRep, _isRepG0, _isRepG1, _isRepG2, _isRep0Long, _posSlot, _posDecoders, _align, _literals })
        {
            Init(probs);
        }
        _lenDecoder.Init();
        _repLenDecoder.Init();

        if (ReadByte() != 0)
        {
            throw new InvalidDataException("bad LZMA range coder start");
        }
        _range = 0xFFFFFFFF;
        _code = 0;
        for (int i = 0; i < 4; i++)
        {
            _code = (_code << 8) | (uint)ReadByte();
        }
        if (_code == _range)
        {
            throw new InvalidDataException("bad LZMA range coder start");
        }

        long total = 0;   // bytes produced
        int wpos = 0;     // write position in the window
        int chunkStart = 0;
        int pbMask = (1 << _pb) - 1;
        int lpMask = (1 << _lp) - 1;
        uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
        int state = 0;

        while (total < unpackedSize)
        {
            int posState = (int)total & pbMask;
            if (DecodeBit(_isMatch, (state << NumPosBitsMax) + posState) == 0)
            {
                int prev = total == 0 ? 0 : window[wpos == 0 ? wsize - 1 : wpos - 1];
                int litState = (((int)total & lpMask) << _lc) + (prev >> (8 - _lc));
                int baseIndex = 0x300 * litState;
                int symbol = 1;
                if (state >= 7)
                {
                    int matchByte = window[Back(wpos, rep0 + 1, wsize)];
                    do
                    {
                        int matchBit = (matchByte >> 7) & 1;
                        matchByte <<= 1;
                        int bit = DecodeBit(_literals, baseIndex + ((1 + matchBit) << 8) + symbol);
                        symbol = (symbol << 1) | bit;
                        if (matchBit != bit)
                        {
                            break;
                        }
                    }
                    while (symbol < 0x100);
                }
                while (symbol < 0x100)
                {
                    symbol = (symbol << 1) | DecodeBit(_literals, baseIndex + symbol);
                }
                window[wpos++] = (byte)symbol;
                total++;
                state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                if (wpos - chunkStart == ChunkSize)
                {
                    Emit(sink, window, ref wpos, ref chunkStart);
                }
                continue;
            }

            int len;
            if (DecodeBit(_isRep, state) != 0)
            {
                if (total == 0)
                {
                    throw new InvalidDataException("LZMA rep match at the start");
                }
                if (DecodeBit(_isRepG0, state) == 0)
                {
                    if (DecodeBit(_isRep0Long, (state << NumPosBitsMax) + posState) == 0)
                    {
                        state = state < 7 ? 9 : 11;
                        window[wpos] = window[Back(wpos, rep0 + 1, wsize)];
                        wpos++;
                        total++;
                        if (wpos - chunkStart == ChunkSize)
                        {
                            Emit(sink, window, ref wpos, ref chunkStart);
                        }
                        continue;
                    }
                }
                else
                {
                    uint dist;
                    if (DecodeBit(_isRepG1, state) == 0)
                    {
                        dist = rep1;
                    }
                    else
                    {
                        if (DecodeBit(_isRepG2, state) == 0)
                        {
                            dist = rep2;
                        }
                        else
                        {
                            dist = rep3;
                            rep3 = rep2;
                        }
                        rep2 = rep1;
                    }
                    rep1 = rep0;
                    rep0 = dist;
                }
                len = _repLenDecoder.Decode(this, posState);
                state = state < 7 ? 8 : 11;
            }
            else
            {
                rep3 = rep2;
                rep2 = rep1;
                rep1 = rep0;
                len = _lenDecoder.Decode(this, posState);
                state = state < 7 ? 7 : 10;
                rep0 = DecodeDistance(len);
                if (rep0 == 0xFFFFFFFF)
                {
                    throw new InvalidDataException("LZMA end marker before the unpacked size");
                }
                if (rep0 >= total || rep0 >= wsize)
                {
                    throw new InvalidDataException("LZMA distance before the start of the data");
                }
            }
            len += MatchMinLen;
            if (total + len > unpackedSize)
            {
                throw new InvalidDataException("LZMA match past the unpacked size");
            }
            total += len;
            int from = Back(wpos, rep0 + 1, wsize);
            while (len-- > 0)
            {
                window[wpos++] = window[from++];
                if (from == wsize)
                {
                    from = 0;
                }
                if (wpos - chunkStart == ChunkSize)
                {
                    Emit(sink, window, ref wpos, ref chunkStart);
                }
            }
        }
        if (wpos > chunkStart)
        {
            sink(window, chunkStart, wpos - chunkStart);
        }
        _endState = state;
        _endTotal = total;
    }

    /// <summary>A full chunk goes to the sink; writing continues after it, or wraps to the window start.</summary>
    private static void Emit(Action<byte[], int, int> sink, byte[] window, ref int wpos, ref int chunkStart)
    {
        sink(window, chunkStart, ChunkSize);
        chunkStart = wpos == window.Length ? 0 : wpos;
        wpos = chunkStart;
    }

    /// <summary>
    /// After <see cref="Decode"/>: the stream ends cleanly at the known size, as LzmaSpec.cpp accepts it: either
    /// the range coder is finished (code 0, no end marker) or exactly one end marker follows (a match with
    /// distance 0xFFFFFFFF) and then the coder is finished. Anything else = corrupt or wrongly decoded stream.
    /// </summary>
    public bool EndsCleanly()
    {
        if (_code == 0)
        {
            return true;
        }
        try
        {
            int posState = (int)_endTotal & ((1 << _pb) - 1);
            if (DecodeBit(_isMatch, (_endState << NumPosBitsMax) + posState) == 0 || DecodeBit(_isRep, _endState) != 0)
            {
                return false;
            }
            return DecodeDistance(_lenDecoder.Decode(this, posState)) == 0xFFFFFFFF && _code == 0;
        }
        catch (Exception e) when (e is InvalidDataException || e is EndOfStreamException)
        {
            return false;
        }
    }

    private static int Back(int wpos, uint distance, int wsize)
    {
        long at = wpos - (long)distance;
        return (int)(at < 0 ? at + wsize : at);
    }

    private uint DecodeDistance(int len)
    {
        int posSlot = BitTree(_posSlot, Math.Min(len, NumLenToPosStates - 1) << 6, 6);
        if (posSlot < StartPosModelIndex)
        {
            return (uint)posSlot;
        }
        int numDirectBits = (posSlot >> 1) - 1;
        uint dist = (uint)((2 | (posSlot & 1)) << numDirectBits);
        if (posSlot < EndPosModelIndex)
        {
            return dist + ReverseBitTree(_posDecoders, (int)dist - posSlot, numDirectBits);
        }
        dist += DirectBits(numDirectBits - NumAlignBits) << NumAlignBits;
        return dist + ReverseBitTree(_align, 0, NumAlignBits);
    }

    private int DecodeBit(ushort[] probs, int index)
    {
        uint v = probs[index];
        uint bound = (_range >> 11) * v;
        int symbol;
        if (_code < bound)
        {
            v += ((1u << 11) - v) >> 5;
            _range = bound;
            symbol = 0;
        }
        else
        {
            v -= v >> 5;
            _code -= bound;
            _range -= bound;
            symbol = 1;
        }
        probs[index] = (ushort)v;
        if (_range < TopValue)
        {
            _range <<= 8;
            _code = (_code << 8) | (uint)ReadByte();
        }
        return symbol;
    }

    private uint DirectBits(int numBits)
    {
        uint res = 0;
        do
        {
            _range >>= 1;
            _code -= _range;
            uint t = 0 - (_code >> 31);
            _code += _range & t;
            if (_code == _range)
            {
                throw new InvalidDataException("corrupt LZMA direct bits");
            }
            if (_range < TopValue)
            {
                _range <<= 8;
                _code = (_code << 8) | (uint)ReadByte();
            }
            res <<= 1;
            res += t + 1;
        }
        while (--numBits > 0);
        return res;
    }

    private int BitTree(ushort[] probs, int offset, int numBits)
    {
        int m = 1;
        for (int i = 0; i < numBits; i++)
        {
            m = (m << 1) + DecodeBit(probs, offset + m);
        }
        return m - (1 << numBits);
    }

    private uint ReverseBitTree(ushort[] probs, int offset, int numBits)
    {
        int m = 1;
        uint symbol = 0;
        for (int i = 0; i < numBits; i++)
        {
            int bit = DecodeBit(probs, offset + m);
            m = (m << 1) + bit;
            symbol |= (uint)bit << i;
        }
        return symbol;
    }

    private int ReadByte()
    {
        if (_inPos == _inLen)
        {
            long left = _inputEnd - _inRead;
            if (left <= 0)
            {
                throw new InvalidDataException("LZMA stream reads past its block");
            }
            _inLen = _input.Read(_inBuf, 0, (int)Math.Min(left, _inBuf.Length));
            if (_inLen <= 0)
            {
                throw new EndOfStreamException();
            }
            _inRead += _inLen;
            _inPos = 0;
        }
        return _inBuf[_inPos++];
    }

    private static void Init(ushort[] probs)
    {
        for (int i = 0; i < probs.Length; i++)
        {
            probs[i] = 1024;
        }
    }

    private sealed class LenDecoder
    {
        private readonly ushort[] _choice = new ushort[2];
        private readonly ushort[] _low = new ushort[(1 << NumPosBitsMax) << 3];
        private readonly ushort[] _mid = new ushort[(1 << NumPosBitsMax) << 3];
        private readonly ushort[] _high = new ushort[1 << 8];

        public void Init()
        {
            LzmaDecoder.Init(_choice);
            LzmaDecoder.Init(_low);
            LzmaDecoder.Init(_mid);
            LzmaDecoder.Init(_high);
        }

        public int Decode(LzmaDecoder rc, int posState) =>
            rc.DecodeBit(_choice, 0) == 0 ? rc.BitTree(_low, posState << 3, 3)
            : rc.DecodeBit(_choice, 1) == 0 ? 8 + rc.BitTree(_mid, posState << 3, 3)
            : 16 + rc.BitTree(_high, 0, 8);
    }
}
