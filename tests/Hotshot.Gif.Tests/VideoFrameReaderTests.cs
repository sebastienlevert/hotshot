using System.Runtime.InteropServices;
using Hotshot.Gif.Internal;
using Vortice.MediaFoundation;

namespace Hotshot.Gif.Tests;

public sealed unsafe class VideoFrameReaderTests
{
    [Fact]
    public void PixelsUseTheFormatOwnedByTheHeldFrame()
    {
        using ComApartmentScope com = ComApartmentScope.EnterMta();
        using MediaFoundationRuntime.Scope mf = MediaFoundationRuntime.Enter();
        using var buffer = MediaFactory.MFCreateMemoryBuffer(4 * 2 * 4);
        buffer.Lock(out nint data, out _, out _);
        try
        {
            new Span<uint>((void*)data, 8).Fill(0xFF112233);
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = 32;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        var heldFormat = new VideoFrameReader.FrameFormat(new VideoFrameReader.Area(1, 1, 2, 1), 2, 16, 1);
        var subsequentFormat = new VideoFrameReader.FrameFormat(new VideoFrameReader.Area(0, 0, 8, 4), 4, 32, 2);

        VideoFrameReader.WithPixels(sample, heldFormat, (pixels, pitch) =>
        {
            Assert.Equal(16, pitch);
            Assert.Equal(0xFF112233u, MemoryMarshal.Read<uint>(new ReadOnlySpan<byte>((void*)pixels, 4)));
        });
        Assert.Throws<InvalidDataException>(() =>
            VideoFrameReader.WithPixels(sample, subsequentFormat, static (_, _) => Assert.Fail("Invalid layout must not reach the scaler.")));

        // A rejected layout must also unlock the buffer before returning.
        buffer.Lock(out _, out _, out _);
        buffer.Unlock();
    }
}
