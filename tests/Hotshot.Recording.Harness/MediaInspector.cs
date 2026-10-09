using Vortice.MediaFoundation;

namespace Hotshot.Recording.Harness;

internal sealed record MediaInfo(
    int Width,
    int Height,
    double DurationSeconds,
    bool HasAudio,
    int VideoSamples,
    double VideoEndSeconds,
    double AudioEndSeconds,
    double MeanLuma)
{
    public override string ToString() =>
        $"file {Width}x{Height}, MF_PD_DURATION {DurationSeconds:F2}s, {VideoSamples} video samples (end {VideoEndSeconds:F2}s), " +
        (HasAudio ? $"audio track end {AudioEndSeconds:F2}s" : "no audio track") + $", mid-frame mean luma {MeanLuma:F0}";
}

/// <summary>A decoded frame as top-down BGRA.</summary>
internal sealed class DecodedFrame(int width, int height, byte[] bgra)
{
    public int Width { get; } = width;
    public int Height { get; } = height;

    /// <summary>Average colour of a small patch centred at the relative position (0..1, 0..1).</summary>
    public (int B, int G, int R) Sample(double rx, double ry, int radius = 2)
    {
        int cx = Math.Clamp((int)(rx * Width), radius, Width - 1 - radius);
        int cy = Math.Clamp((int)(ry * Height), radius, Height - 1 - radius);
        long b = 0, g = 0, r = 0, n = 0;
        for (int y = cy - radius; y <= cy + radius; y++)
        {
            for (int x = cx - radius; x <= cx + radius; x++)
            {
                int o = ((y * Width) + x) * 4;
                b += bgra[o];
                g += bgra[o + 1];
                r += bgra[o + 2];
                n++;
            }
        }

        return ((int)(b / n), (int)(g / n), (int)(r / n));
    }

    public double MeanLuma()
    {
        double sum = 0;
        long count = 0;
        for (int o = 0; o < bgra.Length; o += 4 * 7)
        {
            sum += (0.114 * bgra[o]) + (0.587 * bgra[o + 1]) + (0.299 * bgra[o + 2]);
            count++;
        }

        return count == 0 ? 0 : sum / count;
    }
}

/// <summary>Reads an MP4 back through the Media Foundation SourceReader.</summary>
internal static unsafe class MediaInspector
{
    public static MediaInfo Inspect(string path)
    {
        int width;
        int height;
        double duration;
        bool hasAudio;
        using (var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!))
        {
            using (var videoType = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0))
            {
                MediaFactory.MFGetAttributeSize(videoType, MediaTypeAttributeKeys.FrameSize, out uint w, out uint h).CheckError();
                width = (int)w;
                height = (int)h;
            }

            var durationValue = reader.GetPresentationAttribute(SourceReaderIndex.MediaSource, PresentationDescriptionAttributeKeys.Duration);
            duration = Convert.ToInt64(durationValue.Value) / 1e7;

            try
            {
                using var audioType = reader.GetNativeMediaType(SourceReaderIndex.FirstAudioStream, 0);
                hasAudio = true;
            }
            catch
            {
                hasAudio = false;
            }
        }

        var (videoSamples, videoEnd) = ScanStream(path, SourceReaderIndex.FirstVideoStream);
        var (_, audioEnd) = hasAudio ? ScanStream(path, SourceReaderIndex.FirstAudioStream) : (0, 0d);
        double luma = DecodeFrame(path, duration / 2)?.MeanLuma() ?? -1;
        return new MediaInfo(width, height, duration, hasAudio, videoSamples, videoEnd, audioEnd, luma);
    }

    private static (int Samples, double EndSeconds) ScanStream(string path, SourceReaderIndex stream)
    {
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);
        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection(stream, true);
        int count = 0;
        long end = 0;
        while (true)
        {
            using var sample = reader.ReadSample(stream, SourceReaderControlFlag.None, out _, out var flags, out long timestamp);
            if (sample is not null)
            {
                count++;
                end = Math.Max(end, timestamp + sample.SampleDuration);
            }

            if ((flags & (SourceReaderFlag.EndOfStream | SourceReaderFlag.Error)) != 0)
            {
                break;
            }
        }

        return (count, end / 1e7);
    }

    /// <summary>Decodes the first frame whose interval reaches <paramref name="atSeconds"/> to top-down BGRA.</summary>
    public static DecodedFrame? DecodeFrame(string path, double atSeconds)
    {
        using var attributes = MediaFactory.MFCreateAttributes(1);
        attributes.Set(SourceReaderAttributeKeys.EnableVideoProcessing, 1u);
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
        using (var rgb = MediaFactory.MFCreateMediaType())
        {
            rgb.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            rgb.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, rgb);
        }

        uint width;
        uint height;
        using (var current = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
        {
            MediaFactory.MFGetAttributeSize(current, MediaTypeAttributeKeys.FrameSize, out width, out height).CheckError();
        }

        long target = (long)(atSeconds * 1e7);
        while (true)
        {
            using var sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out long timestamp);
            if (sample is not null && timestamp + sample.SampleDuration > target)
            {
                return ToFrame(sample, (int)width, (int)height);
            }

            if ((flags & (SourceReaderFlag.EndOfStream | SourceReaderFlag.Error)) != 0)
            {
                return null;
            }
        }
    }

    private static DecodedFrame ToFrame(IMFSample sample, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        using var buffer = sample.GetBufferByIndex(0);
        using var buffer2D = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
        if (buffer2D is not null)
        {
            // Lock2D yields the top row and a signed pitch, so bottom-up buffers are handled too.
            buffer2D.Lock2D(out nint scanline0, out int pitch);
            try
            {
                CopyRows((byte*)scanline0, pitch, pixels, width, height);
            }
            finally
            {
                buffer2D.Unlock2D();
            }
        }
        else
        {
            buffer.Lock(out nint data, out _, out int length);
            try
            {
                int pitch = length >= ((width + 15) & ~15) * ((height + 15) & ~15) * 4 && length != width * height * 4
                    ? ((width + 15) & ~15) * 4
                    : width * 4;
                CopyRows((byte*)data, pitch, pixels, width, height);
            }
            finally
            {
                buffer.Unlock();
            }
        }

        return new DecodedFrame(width, height, pixels);
    }

    private static void CopyRows(byte* top, int pitch, byte[] destination, int width, int height)
    {
        int rowBytes = width * 4;
        for (int y = 0; y < height; y++)
        {
            new ReadOnlySpan<byte>(top + ((long)y * pitch), rowBytes).CopyTo(destination.AsSpan(y * rowBytes, rowBytes));
        }
    }
}
