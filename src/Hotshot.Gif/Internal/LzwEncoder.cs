namespace Hotshot.Gif.Internal;

/// <summary>Growable byte buffer used to stage encoded frame data.</summary>
internal sealed class GifByteBuffer
{
    private byte[] _buffer;

    public GifByteBuffer(int capacity = 1 << 16) => _buffer = new byte[capacity];

    public int Length { get; private set; }

    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, Length);

    public void Clear() => Length = 0;

    public void WriteByte(byte value)
    {
        if (Length == _buffer.Length)
        {
            Grow(1);
        }

        _buffer[Length++] = value;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (Length + data.Length > _buffer.Length)
        {
            Grow(data.Length);
        }

        data.CopyTo(_buffer.AsSpan(Length));
        Length += data.Length;
    }

    public void WriteUInt16(int value)
    {
        WriteByte((byte)value);
        WriteByte((byte)(value >> 8));
    }

    private void Grow(int extra)
    {
        int size = Math.Max(_buffer.Length * 2, Length + extra);
        Array.Resize(ref _buffer, size);
    }
}

/// <summary>
/// GIF variable-length LZW encoder: codes grow from (minCodeSize + 1) up to 12 bits, a clear code is emitted when the
/// table is full (same policy as giflib: clear at code 4095), and output is packed into ≤ 255-byte sub-blocks.
/// </summary>
internal sealed class LzwEncoder
{
    private const int MaxCode = 4095;
    private const int HashBits = 13;
    private const int HashSize = 1 << HashBits;

    // Entry = (key << 12) | code, key = (prefix << 8) | suffix (20 bits). -1 = empty (key 0xFFFFF is impossible: prefix < 4095).
    private readonly int[] _hash = new int[HashSize];
    private readonly byte[] _block = new byte[255];
    private int _blockLength;
    private ulong _bitBuffer;
    private int _bitCount;
    private GifByteBuffer _output = null!;
    private int _runningBits;
    private int _runningCode;
    private int _maxCode1;

    /// <summary>Encodes a <paramref name="width"/> x <paramref name="height"/> index image (row pitch <paramref name="stride"/>).</summary>
    public void Encode(ReadOnlySpan<byte> indices, int stride, int width, int height, int minCodeSize, GifByteBuffer output)
    {
        if (minCodeSize is < 2 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(minCodeSize));
        }

        _output = output;
        _blockLength = 0;
        _bitBuffer = 0;
        _bitCount = 0;
        output.WriteByte((byte)minCodeSize);

        int clearCode = 1 << minCodeSize;
        int eoiCode = clearCode + 1;
        int limit = clearCode;
        Reset(minCodeSize, eoiCode);
        Output(clearCode);

        int prefix = -1;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = indices.Slice(y * stride, width);
            for (int x = 0; x < row.Length; x++)
            {
                int k = row[x];
                if (k >= limit)
                {
                    throw new ArgumentException($"Index {k} does not fit LZW minimum code size {minCodeSize}.", nameof(indices));
                }

                if (prefix < 0)
                {
                    prefix = k;
                    continue;
                }

                int key = (prefix << 8) | k;
                int slot = (int)(((uint)key * 0x9E3779B1u) >> (32 - HashBits));
                int found = -1;
                while (true)
                {
                    int e = _hash[slot];
                    if (e == -1)
                    {
                        break;
                    }

                    if ((e >>> 12) == key)
                    {
                        found = e & 0xFFF;
                        break;
                    }

                    slot = (slot + 1) & (HashSize - 1);
                }

                if (found >= 0)
                {
                    prefix = found;
                    continue;
                }

                Output(prefix);
                if (_runningCode >= MaxCode)
                {
                    Output(clearCode);
                    Reset(minCodeSize, eoiCode);
                }
                else
                {
                    _hash[slot] = (key << 12) | _runningCode;
                    _runningCode++;
                }

                prefix = k;
            }
        }

        if (prefix >= 0)
        {
            Output(prefix);
        }

        Output(eoiCode);
        if (_bitCount > 0)
        {
            PutByte((byte)_bitBuffer);
            _bitBuffer = 0;
            _bitCount = 0;
        }

        FlushBlock();
        output.WriteByte(0);
        _output = null!;
    }

    private void Reset(int minCodeSize, int eoiCode)
    {
        Array.Fill(_hash, -1);
        _runningBits = minCodeSize + 1;
        _maxCode1 = 1 << _runningBits;
        _runningCode = eoiCode + 1;
    }

    private void Output(int code)
    {
        _bitBuffer |= (ulong)(uint)code << _bitCount;
        _bitCount += _runningBits;
        while (_bitCount >= 8)
        {
            PutByte((byte)_bitBuffer);
            _bitBuffer >>= 8;
            _bitCount -= 8;
        }

        // The decoder adds an entry per code and widens when its next free code reaches 2^bits.
        if (_runningCode >= _maxCode1 && _runningBits < 12)
        {
            _runningBits++;
            _maxCode1 = 1 << _runningBits;
        }
    }

    private void PutByte(byte value)
    {
        _block[_blockLength++] = value;
        if (_blockLength == 255)
        {
            FlushBlock();
        }
    }

    private void FlushBlock()
    {
        if (_blockLength == 0)
        {
            return;
        }

        _output.WriteByte((byte)_blockLength);
        _output.Write(_block.AsSpan(0, _blockLength));
        _blockLength = 0;
    }
}
