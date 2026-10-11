using System.Buffers.Binary;

namespace Hotshot.Core.Agents;

public static class AgentFrames
{
    public static async Task WriteAsync(Stream stream, byte[] message, int limit, CancellationToken token)
    {
        if (message.Length < 1 || message.Length > limit) throw new InvalidDataException("Agent message exceeds the transport limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, message.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(message, token);
        await stream.FlushAsync(token);
    }

    public static async Task<byte[]> ReadAsync(Stream stream, int limit, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 || length > limit) throw new InvalidDataException("Agent message exceeds the transport limit.");
        var message = new byte[length];
        await stream.ReadExactlyAsync(message, token);
        return message;
    }
}
