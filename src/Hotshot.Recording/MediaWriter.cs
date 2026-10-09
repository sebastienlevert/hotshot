using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace Hotshot.Recording;

/// <summary>
/// Media Foundation SinkWriter producing an MP4 with H.264 video and optional AAC audio.
/// WriteSample/Finalize are serialized internally; sample preparation must run under the caller's GPU lock.
/// </summary>
internal sealed unsafe class MediaWriter : IDisposable
{
    public const int AudioSampleRate = 48000;
    public const int AudioChannels = 2;
    private const int AudioBitsPerSample = 16;
    private const int AacBytesPerSecond = 24000; // 192 kbps
    private const int AacProfileLevel = 0x29;     // AAC Profile L2
    private const uint H264ProfileHigh = 100;     // eAVEncH264VProfile_High
    private const uint InterlaceProgressive = 2;  // MFVideoInterlace_Progressive
    private const int MaxConsecutiveAllocatorMisses = 120;

    private readonly object _writeLock = new();
    private readonly ID3D11DeviceContext _context;
    private readonly int _width;
    private readonly int _height;
    private IMFSinkWriter? _writer;
    private IMFDXGIDeviceManager? _deviceManager;
    private IMFVideoSampleAllocatorEx? _allocator;
    private ID3D11Texture2D? _staging;
    private int _videoStream = -1;
    private int _audioStream = -1;
    private bool _finalized;
    private bool _allocatorValidated;
    private int _allocatorMisses;

    private MediaWriter(ID3D11DeviceContext context, int width, int height, VideoEncoderPath path)
    {
        _context = context;
        _width = width;
        _height = height;
        EncoderPath = path;
    }

    public VideoEncoderPath EncoderPath { get; }
    public bool HasAudio => _audioStream >= 0;
    public long VideoFramesWritten { get; private set; }
    public long AudioFramesWritten { get; private set; }

    public static MediaWriter Create(string path, ID3D11Device device, ID3D11DeviceContext context, int width, int height, int fps, int bitrate, bool withAudio, VideoEncoderPath encoderPath)
    {
        var writer = new MediaWriter(context, width, height, encoderPath);
        try
        {
            writer.Initialize(path, device, fps, bitrate, withAudio);
            return writer;
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Encodes a few frames through the GPU path into a scratch file to catch drivers/encoders that accept the
    /// configuration but fail when fed D3D11 surfaces. Caller must not hold the GPU lock.
    /// </summary>
    public static bool ProbeGpuPath(ID3D11Device device, ID3D11DeviceContext context, object gpuLock, ID3D11Texture2D source, int width, int height, int fps, Action<string> log)
    {
        string scratch = Path.Combine(Path.GetTempPath(), $"hotshot-gpu-probe-{Guid.NewGuid():N}.mp4");
        try
        {
            using (var writer = Create(scratch, device, context, width, height, fps, 2_000_000, withAudio: false, VideoEncoderPath.Gpu))
            {
                long frameDuration = 10_000_000L / fps;
                for (int i = 0; i < 3; i++)
                {
                    IMFSample? sample;
                    lock (gpuLock)
                    {
                        sample = writer.PrepareVideoSample(source);
                    }

                    if (sample is null)
                    {
                        throw new InvalidOperationException("The GPU sample allocator returned no sample.");
                    }

                    using (sample)
                    {
                        writer.WriteVideo(sample, i * frameDuration, frameDuration);
                    }
                }

                writer.FinalizeFile();
            }

            return new FileInfo(scratch).Length > 0;
        }
        catch (Exception ex)
        {
            log($"GPU encoder path unavailable, using CPU path: {ex.Message}");
            return false;
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    private void Initialize(string path, ID3D11Device device, int fps, int bitrate, bool withAudio)
    {
        bool gpu = EncoderPath == VideoEncoderPath.Gpu;
        using var attributes = MediaFactory.MFCreateAttributes(4);
        attributes.Set(TranscodeAttributeKeys.TranscodeContainertype, TranscodeContainerTypeGuids.Mpeg4);
        if (gpu)
        {
            _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
            _deviceManager.ResetDevice(device).CheckError();
            attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
            attributes.Set(SinkWriterAttributeKeys.D3DManager, _deviceManager);
        }
        else
        {
            // Deterministic fallback: Microsoft software H.264 encoder, present on x64 and ARM64.
            attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 0u);
            var stagingDescription = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)_width, (uint)_height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None);
            _staging = device.CreateTexture2D(stagingDescription);
        }

        _writer = MediaFactory.MFCreateSinkWriterFromURL(path, null!, attributes);

        using (var videoOutput = MediaFactory.MFCreateMediaType())
        {
            videoOutput.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            videoOutput.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            videoOutput.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
            videoOutput.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceProgressive);
            videoOutput.Set(MediaTypeAttributeKeys.Mpeg2Profile, H264ProfileHigh);
            MediaFactory.MFSetAttributeSize(videoOutput, MediaTypeAttributeKeys.FrameSize, (uint)_width, (uint)_height);
            MediaFactory.MFSetAttributeRatio(videoOutput, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1);
            MediaFactory.MFSetAttributeRatio(videoOutput, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
            _videoStream = _writer.AddStream(videoOutput);
        }

        using (var videoInput = CreateVideoInputType(fps, gpu))
        {
            _writer.SetInputMediaType(_videoStream, videoInput, null!);

            if (gpu)
            {
                nint allocator = MediaFactory.MFCreateVideoSampleAllocatorEx(typeof(IMFVideoSampleAllocatorEx).GUID);
                _allocator = new IMFVideoSampleAllocatorEx(allocator);
                _allocator.SetDirectXManager(_deviceManager!);
                using var allocatorAttributes = MediaFactory.MFCreateAttributes(1);
                allocatorAttributes.Set(TransformAttributeKeys.D3D11Bindflags, (uint)(BindFlags.RenderTarget | BindFlags.ShaderResource));
                // Pooled surfaces: a slot is recycled only once the encoder has released the sample.
                _allocator.InitializeSampleAllocatorEx(4, 24, allocatorAttributes, videoInput);
            }
        }

        if (withAudio)
        {
            using (var audioOutput = MediaFactory.MFCreateMediaType())
            {
                audioOutput.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                audioOutput.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
                audioOutput.Set(MediaTypeAttributeKeys.AudioBitsPerSample, (uint)AudioBitsPerSample);
                audioOutput.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)AudioSampleRate);
                audioOutput.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)AudioChannels);
                audioOutput.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)AacBytesPerSecond);
                audioOutput.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);
                audioOutput.Set(MediaTypeAttributeKeys.AacAudioProfileLevelIndication, (uint)AacProfileLevel);
                _audioStream = _writer.AddStream(audioOutput);
            }

            using var audioInput = MediaFactory.MFCreateMediaType();
            int blockAlign = AudioChannels * AudioBitsPerSample / 8;
            audioInput.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            audioInput.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            audioInput.Set(MediaTypeAttributeKeys.AudioBitsPerSample, (uint)AudioBitsPerSample);
            audioInput.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)AudioSampleRate);
            audioInput.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)AudioChannels);
            audioInput.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)blockAlign);
            audioInput.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(blockAlign * AudioSampleRate));
            audioInput.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
            _writer.SetInputMediaType(_audioStream, audioInput, null!);
        }

        _writer.BeginWriting();
    }

    private IMFMediaType CreateVideoInputType(int fps, bool gpu)
    {
        var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        // ARGB32 maps to DXGI_FORMAT_B8G8R8A8_UNORM, the WGC frame format; RGB32 + positive stride = top-down BGRX in memory.
        type.Set(MediaTypeAttributeKeys.Subtype, gpu ? VideoFormatGuids.Argb32 : VideoFormatGuids.Rgb32);
        type.Set(MediaTypeAttributeKeys.InterlaceMode, InterlaceProgressive);
        MediaFactory.MFSetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, (uint)_width, (uint)_height);
        MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.FrameRate, (uint)fps, 1);
        MediaFactory.MFSetAttributeRatio(type, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1);
        if (!gpu)
        {
            type.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(_width * 4));
            type.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
        }

        return type;
    }

    /// <summary>
    /// Copies <paramref name="source"/> into a sample the encoder can own. Returns null when the GPU pool is momentarily
    /// exhausted (frame skipped). Must be called under the GPU lock.
    /// </summary>
    public IMFSample? PrepareVideoSample(ID3D11Texture2D source) =>
        EncoderPath == VideoEncoderPath.Gpu ? PrepareGpuSample(source) : PrepareCpuSample(source);

    private IMFSample? PrepareGpuSample(ID3D11Texture2D source)
    {
        IMFSample sample;
        try
        {
            sample = _allocator!.AllocateSample();
            _allocatorMisses = 0;
        }
        catch (SharpGenException) when (++_allocatorMisses < MaxConsecutiveAllocatorMisses)
        {
            return null;
        }

        try
        {
            using var buffer = sample.GetBufferByIndex(0);
            using var dxgiBuffer = buffer.QueryInterface<IMFDXGIBuffer>();
            using var texture = new ID3D11Texture2D(dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID));
            if (!_allocatorValidated)
            {
                var description = texture.Description;
                if (description.Format != Format.B8G8R8A8_UNorm || description.Width != (uint)_width || description.Height != (uint)_height)
                {
                    throw new InvalidOperationException($"Unexpected encoder surface {description.Format} {description.Width}x{description.Height}.");
                }

                _allocatorValidated = true;
            }

            _context.CopyResource(texture, source);
            if (buffer.CurrentLength == 0)
            {
                buffer.CurrentLength = buffer.MaxLength;
            }

            return sample;
        }
        catch
        {
            sample.Dispose();
            throw;
        }
    }

    private IMFSample PrepareCpuSample(ID3D11Texture2D source)
    {
        var staging = _staging!;
        _context.CopyResource(staging, source);
        var mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int rowBytes = _width * 4;
            int size = rowBytes * _height;
            using var buffer = MediaFactory.MFCreateMemoryBuffer(size);
            buffer.Lock(out nint destination, out _, out _);
            try
            {
                byte* src = (byte*)mapped.DataPointer;
                byte* dst = (byte*)destination;
                if (mapped.RowPitch == rowBytes)
                {
                    Buffer.MemoryCopy(src, dst, size, size);
                }
                else
                {
                    for (int y = 0; y < _height; y++)
                    {
                        Buffer.MemoryCopy(src + (long)y * mapped.RowPitch, dst + (long)y * rowBytes, rowBytes, rowBytes);
                    }
                }
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.CurrentLength = size;
            var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            return sample;
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    public void WriteVideo(IMFSample sample, long time, long duration)
    {
        sample.SampleTime = time;
        sample.SampleDuration = duration;
        lock (_writeLock)
        {
            if (_finalized || _writer is null)
            {
                return;
            }

            _writer.WriteSample(_videoStream, sample);
            VideoFramesWritten++;
        }
    }

    /// <summary>Writes interleaved 16-bit stereo PCM.</summary>
    public void WriteAudio(ReadOnlySpan<short> pcm, long time, long duration)
    {
        if (_audioStream < 0 || pcm.IsEmpty)
        {
            return;
        }

        int bytes = pcm.Length * sizeof(short);
        using var buffer = MediaFactory.MFCreateMemoryBuffer(bytes);
        buffer.Lock(out nint destination, out _, out _);
        try
        {
            MemoryMarshal.AsBytes(pcm).CopyTo(new Span<byte>((void*)destination, bytes));
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = bytes;
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = time;
        sample.SampleDuration = duration;
        lock (_writeLock)
        {
            if (_finalized || _writer is null)
            {
                return;
            }

            _writer.WriteSample(_audioStream, sample);
            AudioFramesWritten += pcm.Length / AudioChannels;
        }
    }

    /// <summary>Finalizes the container. Further writes are ignored.</summary>
    public void FinalizeFile()
    {
        lock (_writeLock)
        {
            if (_finalized || _writer is null)
            {
                return;
            }

            _finalized = true;
            _writer.Finalize();
        }
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            _finalized = true;
            _writer?.Dispose();
            _writer = null;
        }

        if (_allocator is not null)
        {
            try
            {
                _allocator.UninitializeSampleAllocator();
            }
            catch
            {
            }

            _allocator.Dispose();
            _allocator = null;
        }

        _deviceManager?.Dispose();
        _deviceManager = null;
        _staging?.Dispose();
        _staging = null;
    }

    internal static void TryDelete(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
            catch
            {
                return;
            }
        }
    }
}
