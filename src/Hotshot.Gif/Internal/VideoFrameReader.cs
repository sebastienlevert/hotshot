using System.Buffers.Binary;
using Vortice.MediaFoundation;

namespace Hotshot.Gif.Internal;

/// <summary>
/// Decodes the first video stream of a media file to RGB32 frames with the Media Foundation source reader
/// (software decode + MF video processing for color conversion). Handles stride sign, coded-size padding (crops to the
/// display aperture) and mid-stream media type changes. Must be used on a thread inside an MF/MTA scope.
/// </summary>
internal sealed unsafe class VideoFrameReader : IDisposable
{
    private readonly IMFSourceReader _reader;
    private readonly Area? _nativeAperture;
    private int _bufferWidth;
    private int _bufferHeight;
    private int _defaultStride;

    private VideoFrameReader(IMFSourceReader reader, Area? nativeAperture, double pixelAspect, long duration)
    {
        _reader = reader;
        _nativeAperture = nativeAperture;
        PixelAspectRatio = pixelAspect;
        Duration = duration;
        UpdateFormat();
    }

    /// <summary>Visible (cropped) frame width in pixels.</summary>
    public int Width => Crop.Width;

    /// <summary>Visible (cropped) frame height in pixels.</summary>
    public int Height => Crop.Height;

    /// <summary>Pixel aspect ratio (width / height of one pixel).</summary>
    public double PixelAspectRatio { get; }

    /// <summary>Media duration in 100 ns units, or 0 when unknown.</summary>
    public long Duration { get; }

    /// <summary>Raised when the decoded frame geometry changes mid-stream.</summary>
    public int FormatVersion { get; private set; }

    internal Area Crop { get; private set; }

    public static VideoFrameReader Open(string path)
    {
        // Advanced processing (video processor MFT) is preferred; the legacy color converter is the fallback.
        try
        {
            return Open(path, SourceReaderAttributeKeys.EnableAdvancedVideoProcessing);
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
            return Open(path, SourceReaderAttributeKeys.EnableVideoProcessing);
        }
    }

    private static VideoFrameReader Open(string path, Guid processingKey)
    {
        IMFSourceReader? reader = null;
        try
        {
            using (IMFAttributes attributes = MediaFactory.MFCreateAttributes(2))
            {
                attributes.Set(processingKey, 1u).CheckError();
                reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
            }

            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);

            Area? nativeAperture;
            double pixelAspect = 1;
            using (IMFMediaType native = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0))
            {
                nativeAperture = TryGetArea(native, MediaTypeAttributeKeys.MinimumDisplayAperture)
                    ?? TryGetArea(native, MediaTypeAttributeKeys.GeometricAperture);
                if (nativeAperture is null && MediaFactory.MFGetAttributeSize(native, MediaTypeAttributeKeys.FrameSize, out uint nw, out uint nh).Success && nw > 0 && nh > 0)
                {
                    nativeAperture = new Area(0, 0, (int)nw, (int)nh);
                }

                if (MediaFactory.MFGetAttributeRatio(native, MediaTypeAttributeKeys.PixelAspectRatio, out uint num, out uint den).Success && num > 0 && den > 0)
                {
                    pixelAspect = (double)num / den;
                }
            }

            using (IMFMediaType output = MediaFactory.MFCreateMediaType())
            {
                output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
                output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32).CheckError();
                reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, output);
            }

            long duration = 0;
            try
            {
                object? value = reader.GetPresentationAttribute(SourceReaderIndex.MediaSource, PresentationDescriptionAttributeKeys.Duration).Value;
                duration = value switch
                {
                    ulong u => (long)u,
                    long l => l,
                    _ => 0,
                };
            }
            catch (SharpGen.Runtime.SharpGenException)
            {
                // Duration is optional (e.g. live/fragmented sources); progress then uses timestamps only.
            }

            var result = new VideoFrameReader(reader, nativeAperture, pixelAspect, Math.Max(0, duration));
            reader = null;
            return result;
        }
        finally
        {
            reader?.Dispose();
        }
    }

    /// <summary>Reads the next decoded frame. Returns false at end of stream.</summary>
    public bool TryReadFrame(out IMFSample sample, out long timestamp, out long sampleDuration)
    {
        while (true)
        {
            IMFSample? s = _reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out SourceReaderFlag flags, out long ts);
            if ((flags & SourceReaderFlag.Error) != 0)
            {
                s?.Dispose();
                throw new InvalidDataException("Media Foundation reported an error while decoding the video stream.");
            }

            if ((flags & SourceReaderFlag.CurrentMediaTypeChanged) != 0)
            {
                UpdateFormat();
            }

            if (s is null)
            {
                if ((flags & SourceReaderFlag.EndOfStream) != 0)
                {
                    sample = null!;
                    timestamp = 0;
                    sampleDuration = 0;
                    return false;
                }

                continue;
            }

            sample = s;
            timestamp = ts;
            try
            {
                sampleDuration = s.SampleDuration;
            }
            catch (SharpGen.Runtime.SharpGenException)
            {
                sampleDuration = 0;
            }

            return true;
        }
    }

    /// <summary>Locks the sample's pixels and passes the top-left visible pixel and the signed row pitch to <paramref name="action"/>.</summary>
    public void WithPixels(IMFSample sample, Action<nint, int> action)
    {
        IMFMediaBuffer buffer = sample.BufferCount == 1 ? sample.GetBufferByIndex(0) : sample.ConvertToContiguousBuffer();
        try
        {
            Area crop = Crop;
            using IMF2DBuffer? buffer2D = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
            if (buffer2D is not null)
            {
                buffer2D.Lock2D(out nint scanline0, out int pitch);
                try
                {
                    action(scanline0 + ((nint)crop.Y * pitch) + (crop.X * 4), pitch);
                }
                finally
                {
                    buffer2D.Unlock2D();
                }

                return;
            }

            buffer.Lock(out nint data, out _, out int length);
            try
            {
                int stride = _defaultStride;
                int absStride = Math.Abs(stride);
                int rows = Math.Min(_bufferHeight, length / absStride);
                if (crop.Y + crop.Height > rows || crop.X + crop.Width > absStride / 4)
                {
                    throw new InvalidDataException("Decoded video buffer is smaller than the reported frame size.");
                }

                nint top = stride >= 0 ? data : data + ((nint)(rows - 1) * absStride);
                action(top + ((nint)crop.Y * stride) + (crop.X * 4), stride);
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            buffer.Dispose();
        }
    }

    public void Dispose() => _reader.Dispose();

    private void UpdateFormat()
    {
        using IMFMediaType current = _reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream);
        MediaFactory.MFGetAttributeSize(current, MediaTypeAttributeKeys.FrameSize, out uint w, out uint h).CheckError();
        _bufferWidth = (int)w;
        _bufferHeight = (int)h;
        _defaultStride = current.GetUInt32(MediaTypeAttributeKeys.DefaultStride, out uint stride).Success && stride != 0
            ? unchecked((int)stride)
            : _bufferWidth * 4;

        Area crop = TryGetArea(current, MediaTypeAttributeKeys.MinimumDisplayAperture)
            ?? TryGetArea(current, MediaTypeAttributeKeys.GeometricAperture)
            ?? _nativeAperture
            ?? new Area(0, 0, _bufferWidth, _bufferHeight);

        // Clamp to the decoded buffer (the aperture never extends beyond it).
        int x = Math.Clamp(crop.X, 0, _bufferWidth - 1);
        int y = Math.Clamp(crop.Y, 0, _bufferHeight - 1);
        int cw = Math.Clamp(crop.Width, 1, _bufferWidth - x);
        int ch = Math.Clamp(crop.Height, 1, _bufferHeight - y);
        Crop = new Area(x, y, cw, ch);
        FormatVersion++;
    }

    private static Area? TryGetArea(IMFAttributes attributes, Guid key)
    {
        if (!attributes.GetBlobSize(key, out uint size).Success || size < 16)
        {
            return null;
        }

        Span<byte> blob = stackalloc byte[(int)size];
        if (!attributes.GetBlob(key, blob).Success)
        {
            return null;
        }

        // MFVideoArea: MFOffset OffsetX { WORD fract; short value }, MFOffset OffsetY, SIZE { LONG cx; LONG cy }.
        int x = BinaryPrimitives.ReadInt16LittleEndian(blob[2..]);
        int y = BinaryPrimitives.ReadInt16LittleEndian(blob[6..]);
        int cx = BinaryPrimitives.ReadInt32LittleEndian(blob[8..]);
        int cy = BinaryPrimitives.ReadInt32LittleEndian(blob[12..]);
        return cx > 0 && cy > 0 && x >= 0 && y >= 0 ? new Area(x, y, cx, cy) : null;
    }

    internal readonly record struct Area(int X, int Y, int Width, int Height);
}
