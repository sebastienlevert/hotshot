using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace Hotshot.Core.Descriptions;

/// <summary>Lossless Unicode PNG metadata; image-data chunks are copied unchanged.</summary>
public static class PngDescriptionMetadata
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static byte[] Embed(byte[] png, CaptureDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        using var output = new MemoryStream(png.Length + 8192);
        output.Write(Signature);
        foreach (var chunk in Chunks(png))
        {
            if (chunk.Type == "IEND")
            {
                WriteText(output, "Title", description.Summary);
                WriteText(output, "Description", description.Description);
            }
            if ((chunk.Type is "iTXt" or "tEXt" or "zTXt") && IsDescriptionKeyword(chunk.Data.Span)) continue;
            output.Write(png.AsSpan(chunk.Offset, chunk.Length));
        }
        return output.ToArray();
    }

    public static CaptureDescription? Read(byte[] png)
    {
        string? summary = null, description = null;
        foreach (var chunk in Chunks(png))
        {
            if (chunk.Type != "iTXt" || !IsDescriptionKeyword(chunk.Data.Span)) continue;
            var data = chunk.Data.Span;
            var keywordEnd = data.IndexOf((byte)0);
            if (data.Length < keywordEnd + 5 || data[keywordEnd + 1] != 0 || data[keywordEnd + 2] != 0)
                throw new InvalidDataException("Screenshot description metadata is compressed or malformed.");
            var textStart = keywordEnd + 3;
            for (var field = 0; field < 2; field++)
            {
                var end = data[textStart..].IndexOf((byte)0);
                if (end < 0) throw new InvalidDataException("PNG text metadata is malformed.");
                textStart += end + 1;
            }
            var text = Utf8.GetString(data[textStart..]);
            if (Encoding.ASCII.GetString(data[..keywordEnd]) == "Title") summary = text;
            else description = text;
        }
        return summary is null || description is null ? null : new CaptureDescription(summary, description);
    }

    private static bool IsDescriptionKeyword(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)0);
        return end >= 0 && (data[..end].SequenceEqual("Title"u8) || data[..end].SequenceEqual("Description"u8));
    }

    private static void WriteText(Stream output, string keyword, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\0')) throw new InvalidDataException("PNG description text is invalid.");
        var content = Utf8.GetBytes(keyword + "\0\0\0\0\0" + text);
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)content.Length);
        "iTXt"u8.CopyTo(header[4..]);
        output.Write(header);
        output.Write(content);
        var crc = new Crc32();
        crc.Append(header[4..]);
        crc.Append(content);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc.GetCurrentHashAsUInt32());
        output.Write(checksum);
    }

    private static List<Chunk> Chunks(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length < 20 || !png.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("Not a PNG image.");
        var chunks = new List<Chunk>();
        var offset = 8;
        while (offset <= png.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            if (length > png.Length - offset - 12) throw new InvalidDataException("PNG chunk is truncated.");
            var size = (int)length;
            var type = Encoding.ASCII.GetString(png.AsSpan(offset + 4, 4));
            var crc = Crc32.HashToUInt32(png.AsSpan(offset + 4, size + 4));
            if (crc != BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + size + 8, 4)))
                throw new InvalidDataException("PNG chunk checksum is invalid.");
            chunks.Add(new Chunk(offset, size + 12, type, png.AsMemory(offset + 8, size)));
            offset += size + 12;
            if (type == "IEND")
            {
                if (size != 0 || offset != png.Length || chunks[0].Type != "IHDR") throw new InvalidDataException("PNG structure is invalid.");
                return chunks;
            }
        }
        throw new InvalidDataException("PNG end marker is missing.");
    }

    private sealed record Chunk(int Offset, int Length, string Type, ReadOnlyMemory<byte> Data);
}
