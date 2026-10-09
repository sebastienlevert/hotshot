using System.Text;

namespace Hotshot.Gif.Tests.Support;

/// <summary>Independent managed GIF parser + LZW decoder + compositor used to verify the encoder bit-exactly.</summary>
internal sealed class ParsedGif
{
    public int Width { get; private set; }

    public int Height { get; private set; }

    public uint[]? GlobalPalette { get; private set; }

    public string? ApplicationId { get; private set; }

    public int? LoopCount { get; private set; }

    public List<ParsedFrame> Frames { get; } = [];

    public int TotalDelayCentiseconds => Frames.Sum(f => f.DelayCentiseconds);

    public static ParsedGif Parse(string path) => Parse(File.ReadAllBytes(path));

    public static ParsedGif Parse(byte[] data)
    {
        var gif = new ParsedGif();
        string signature = Encoding.ASCII.GetString(data, 0, 6);
        if (signature != "GIF89a")
        {
            throw new InvalidDataException($"Bad signature '{signature}'.");
        }

        int pos = 6;
        gif.Width = ReadU16(data, ref pos);
        gif.Height = ReadU16(data, ref pos);
        byte packed = data[pos++];
        pos += 2; // background index, aspect
        if ((packed & 0x80) != 0)
        {
            gif.GlobalPalette = ReadPalette(data, ref pos, 1 << ((packed & 7) + 1));
        }

        var canvas = new uint[gif.Width * gif.Height];
        int delay = 0, disposal = 0, transparent = -1;
        ParsedFrame? previous = null;
        while (true)
        {
            byte block = data[pos++];
            if (block == 0x3B)
            {
                break;
            }

            if (block == 0x21)
            {
                byte label = data[pos++];
                if (label == 0xF9)
                {
                    int size = data[pos++];
                    if (size != 4)
                    {
                        throw new InvalidDataException("Bad GCE size.");
                    }

                    byte flags = data[pos++];
                    delay = ReadU16(data, ref pos);
                    int ti = data[pos++];
                    disposal = (flags >> 2) & 7;
                    transparent = (flags & 1) != 0 ? ti : -1;
                    if (data[pos++] != 0)
                    {
                        throw new InvalidDataException("Missing GCE terminator.");
                    }
                }
                else if (label == 0xFF)
                {
                    int size = data[pos++];
                    gif.ApplicationId = Encoding.ASCII.GetString(data, pos, size);
                    pos += size;
                    byte[] sub = ReadSubBlocks(data, ref pos);
                    if (gif.ApplicationId == "NETSCAPE2.0" && sub.Length >= 3 && sub[0] == 1)
                    {
                        gif.LoopCount = sub[1] | (sub[2] << 8);
                    }
                }
                else
                {
                    ReadSubBlocks(data, ref pos);
                }

                continue;
            }

            if (block != 0x2C)
            {
                throw new InvalidDataException($"Unexpected block 0x{block:X2} at {pos - 1}.");
            }

            var frame = new ParsedFrame
            {
                Left = ReadU16(data, ref pos),
                Top = ReadU16(data, ref pos),
                Width = ReadU16(data, ref pos),
                Height = ReadU16(data, ref pos),
                DelayCentiseconds = delay,
                Disposal = disposal,
                TransparentIndex = transparent,
            };
            byte imagePacked = data[pos++];
            if ((imagePacked & 0x40) != 0)
            {
                throw new InvalidDataException("Interlaced images are not expected.");
            }

            if ((imagePacked & 0x80) != 0)
            {
                frame.Palette = ReadPalette(data, ref pos, 1 << ((imagePacked & 7) + 1));
                frame.HasLocalPalette = true;
            }
            else
            {
                frame.Palette = gif.GlobalPalette ?? throw new InvalidDataException("No color table.");
            }

            if (frame.Left + frame.Width > gif.Width || frame.Top + frame.Height > gif.Height || frame.Width == 0 || frame.Height == 0)
            {
                throw new InvalidDataException("Frame outside the logical screen.");
            }

            int minCodeSize = data[pos++];
            frame.MinCodeSize = minCodeSize;
            byte[] lzw = ReadSubBlocks(data, ref pos);
            frame.Indices = LzwDecode(lzw, minCodeSize, frame.Width * frame.Height);

            if (previous is { Disposal: 2 })
            {
                for (int y = previous.Top; y < previous.Top + previous.Height; y++)
                {
                    Array.Clear(canvas, (y * gif.Width) + previous.Left, previous.Width);
                }
            }

            for (int y = 0; y < frame.Height; y++)
            {
                for (int x = 0; x < frame.Width; x++)
                {
                    int idx = frame.Indices[(y * frame.Width) + x];
                    if (idx == frame.TransparentIndex)
                    {
                        continue;
                    }

                    if (idx >= frame.Palette.Length)
                    {
                        throw new InvalidDataException("Index outside the color table.");
                    }

                    canvas[((frame.Top + y) * gif.Width) + frame.Left + x] = frame.Palette[idx] | 0xFF000000u;
                }
            }

            frame.Canvas = (uint[])canvas.Clone();
            gif.Frames.Add(frame);
            previous = frame;
            delay = 0;
            disposal = 0;
            transparent = -1;
        }

        return gif;
    }

    /// <summary>Classic GIF LZW decoder (variable code width up to 12 bits, clear/EOI codes, deferred clear).</summary>
    public static byte[] LzwDecode(ReadOnlySpan<byte> data, int minCodeSize, int pixelCount)
    {
        int clear = 1 << minCodeSize;
        int eoi = clear + 1;
        var prefix = new int[4096];
        var suffix = new byte[4096];
        var length = new int[4096];
        for (int i = 0; i < clear; i++)
        {
            prefix[i] = -1;
            suffix[i] = (byte)i;
            length[i] = 1;
        }

        var output = new byte[pixelCount];
        int outPos = 0;
        int codeSize = minCodeSize + 1;
        int next = eoi + 1;
        int prev = -1;
        long bitPos = 0;
        long totalBits = (long)data.Length * 8;
        bool sawEoi = false;
        while (bitPos + codeSize <= totalBits)
        {
            int code = 0;
            for (int b = 0; b < codeSize; b++, bitPos++)
            {
                code |= ((data[(int)(bitPos >> 3)] >> (int)(bitPos & 7)) & 1) << b;
            }

            if (code == clear)
            {
                codeSize = minCodeSize + 1;
                next = eoi + 1;
                prev = -1;
                continue;
            }

            if (code == eoi)
            {
                sawEoi = true;
                break;
            }

            if (prev == -1)
            {
                if (code >= clear)
                {
                    throw new InvalidDataException("First code after clear must be a literal.");
                }

                Emit(code, output, ref outPos, prefix, suffix, length);
                prev = code;
                continue;
            }

            byte first;
            if (code < next)
            {
                first = FirstOf(code, prefix, suffix);
                Emit(code, output, ref outPos, prefix, suffix, length);
            }
            else if (code == next)
            {
                first = FirstOf(prev, prefix, suffix);
                Emit(prev, output, ref outPos, prefix, suffix, length);
                if (outPos >= output.Length)
                {
                    throw new InvalidDataException("Too much LZW data.");
                }

                output[outPos++] = first;
            }
            else
            {
                throw new InvalidDataException($"Invalid LZW code {code} (next {next}).");
            }

            if (next < 4096)
            {
                prefix[next] = prev;
                suffix[next] = first;
                length[next] = length[prev] + 1;
                next++;
                if (next == (1 << codeSize) && codeSize < 12)
                {
                    codeSize++;
                }
            }

            prev = code;
        }

        if (!sawEoi)
        {
            throw new InvalidDataException("Missing end-of-information code.");
        }

        if (outPos != pixelCount)
        {
            throw new InvalidDataException($"Decoded {outPos} pixels, expected {pixelCount}.");
        }

        return output;
    }

    private static byte FirstOf(int c, int[] prefix, byte[] suffix)
    {
        while (prefix[c] >= 0)
        {
            c = prefix[c];
        }

        return suffix[c];
    }

    private static void Emit(int c, byte[] output, ref int outPos, int[] prefix, byte[] suffix, int[] length)
    {
        int len = length[c];
        if (outPos + len > output.Length)
        {
            throw new InvalidDataException("Too much LZW data.");
        }

        for (int i = len - 1; i >= 0; i--)
        {
            output[outPos + i] = suffix[c];
            c = prefix[c];
        }

        outPos += len;
    }

    private static uint[] ReadPalette(byte[] data, ref int pos, int count)
    {
        var palette = new uint[count];
        for (int i = 0; i < count; i++)
        {
            palette[i] = (uint)((data[pos] << 16) | (data[pos + 1] << 8) | data[pos + 2]);
            pos += 3;
        }

        return palette;
    }

    private static byte[] ReadSubBlocks(byte[] data, ref int pos)
    {
        var result = new List<byte>();
        while (true)
        {
            int size = data[pos++];
            if (size == 0)
            {
                return [.. result];
            }

            result.AddRange(data.AsSpan(pos, size));
            pos += size;
        }
    }

    private static int ReadU16(byte[] data, ref int pos)
    {
        int v = data[pos] | (data[pos + 1] << 8);
        pos += 2;
        return v;
    }
}

internal sealed class ParsedFrame
{
    public int Left { get; init; }

    public int Top { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public int DelayCentiseconds { get; init; }

    public int Disposal { get; init; }

    public int TransparentIndex { get; init; }

    public bool HasLocalPalette { get; set; }

    public int MinCodeSize { get; set; }

    public uint[] Palette { get; set; } = [];

    public byte[] Indices { get; set; } = [];

    /// <summary>Composited logical screen after this frame, as 0xAARRGGBB (BGRA in memory).</summary>
    public uint[] Canvas { get; set; } = [];
}
