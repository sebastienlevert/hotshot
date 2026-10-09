using System.Runtime.InteropServices;
using Hotshot.Gif.Internal;
using Vortice.MediaFoundation;

namespace Hotshot.Gif.Tests.Support;

/// <summary>Generates H.264 MP4 test clips with the Media Foundation sink writer (software encoder).</summary>
internal static unsafe class TestVideo
{
    public delegate void DrawFrame(int index, Span<uint> pixels, int width, int height);

    public static void CreateMp4(string path, int width, int height, int fps, int frameCount, DrawFrame draw, int bitrate = 4_000_000)
    {
        File.Delete(path);
        using ComApartmentScope com = ComApartmentScope.EnterMta();
        using MediaFoundationRuntime.Scope mf = MediaFoundationRuntime.Enter();
        using IMFAttributes attributes = MediaFactory.MFCreateAttributes(2);
        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 0u).CheckError();
        using IMFSinkWriter writer = MediaFactory.MFCreateSinkWriterFromURL(path, null!, attributes);

        int stream;
        using (IMFMediaType output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
            output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate).CheckError();
            output.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
            MediaFactory.MFSetAttributeSize(output, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height).CheckError();
            MediaFactory.MFSetAttributeRatio(output, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1).CheckError();
            MediaFactory.MFSetAttributeRatio(output, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
            stream = writer.AddStream(output);
        }

        using (IMFMediaType input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32).CheckError();
            input.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
            input.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(width * 4)).CheckError();
            MediaFactory.MFSetAttributeSize(input, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height).CheckError();
            MediaFactory.MFSetAttributeRatio(input, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1).CheckError();
            MediaFactory.MFSetAttributeRatio(input, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
            writer.SetInputMediaType(stream, input, null!);
        }

        writer.BeginWriting();
        var pixels = new uint[width * height];
        int size = pixels.Length * 4;
        for (int i = 0; i < frameCount; i++)
        {
            draw(i, pixels, width, height);
            using IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(size);
            buffer.Lock(out nint data, out _, out _);
            try
            {
                MemoryMarshal.AsBytes(pixels.AsSpan()).CopyTo(new Span<byte>((void*)data, size));
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.CurrentLength = size;
            using IMFSample sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            long start = (long)Math.Round(i * 10_000_000.0 / fps);
            long end = (long)Math.Round((i + 1) * 10_000_000.0 / fps);
            sample.SampleTime = start;
            sample.SampleDuration = end - start;
            writer.WriteSample(stream, sample);
        }

        writer.Finalize();
    }

    public static void Fill(Span<uint> pixels, int width, int x0, int y0, int w, int h, uint color)
    {
        int height = pixels.Length / width;
        int x1 = Math.Min(width, x0 + w), y1 = Math.Min(height, y0 + h);
        for (int y = Math.Max(0, y0); y < y1; y++)
        {
            pixels.Slice((y * width) + Math.Max(0, x0), Math.Max(0, x1 - Math.Max(0, x0))).Fill(color);
        }
    }
}
