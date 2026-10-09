using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Hotshot.Gif.Internal;
using Vortice.MediaFoundation;

namespace Hotshot.Gif;

/// <summary>Converts videos (any Media Foundation-decodable file, e.g. Hotshot's H.264 MP4 recordings) to animated GIFs.</summary>
public static class GifConverter
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    // A decoded frame whose timestamp is at most this much after an output tick still counts as "at" the tick
    // (absorbs 100 ns rounding of container timestamps).
    private const long TimestampTolerance = TimeSpan.TicksPerMillisecond;

    // Scaled frames in flight: pending (held for its delay), queued, being encoded, being scaled.
    private const int FrameBufferCount = 4;

    /// <summary>
    /// Decodes <paramref name="inputVideoPath"/> and writes an animated GIF to <paramref name="outputGifPath"/>.
    /// The work runs on dedicated background threads (decode/scale and quantize/encode are pipelined).
    /// The GIF is written to a temporary file next to the output and moved into place on success; on cancellation or
    /// failure the partial file is deleted and an existing file at <paramref name="outputGifPath"/> is left untouched.
    /// </summary>
    /// <param name="progress">Receives values in 0..1 (based on decoded timestamps vs. media duration).</param>
    public static Task<GifResult> ConvertAsync(string inputVideoPath, string outputGifPath, GifOptions? options = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputVideoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputGifPath);
        options ??= new GifOptions();
        options.ValidateForConverter();
        string input = Path.GetFullPath(inputVideoPath);
        string output = Path.GetFullPath(outputGifPath);
        if (!File.Exists(input))
        {
            throw new FileNotFoundException("The input video does not exist.", input);
        }

        if (ct.IsCancellationRequested)
        {
            return Task.FromCanceled<GifResult>(ct);
        }

        return Task.Factory.StartNew(
            () => Convert(input, output, options, progress, ct),
            ct,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>Computes the GIF size for a source frame: fits <paramref name="maxWidth"/>, keeps the display aspect ratio, never upscales.</summary>
    internal static (int Width, int Height) ComputeOutputSize(int sourceWidth, int sourceHeight, double pixelAspectRatio, int maxWidth)
    {
        double par = pixelAspectRatio is > 0 and < 100 ? pixelAspectRatio : 1;
        double aspect = sourceWidth * par / sourceHeight;
        double width = Math.Min(sourceWidth * par, sourceWidth);
        if (maxWidth > 0)
        {
            width = Math.Min(width, maxWidth);
        }

        int w = Math.Max(1, (int)Math.Round(width));
        int h = Math.Max(1, (int)Math.Round(w / aspect));
        if (h > sourceHeight)
        {
            h = sourceHeight;
            w = Math.Clamp((int)Math.Round(h * aspect), 1, sourceWidth);
        }

        return (Math.Min(w, ushort.MaxValue), Math.Min(h, ushort.MaxValue));
    }

    private static GifResult Convert(string input, string output, GifOptions options, IProgress<double>? progress, CancellationToken ct)
    {
        string? directory = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string partial = $"{output}.{Guid.NewGuid():N}.partial";
        using ComApartmentScope com = ComApartmentScope.EnterMta();
        using MediaFoundationRuntime.Scope mf = MediaFoundationRuntime.Enter();

        VideoFrameReader reader;
        try
        {
            reader = VideoFrameReader.Open(input);
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            throw new InvalidDataException($"'{input}' could not be opened as a video: {ex.Message}", ex);
        }

        using (reader)
        {
            (int width, int height) = ComputeOutputSize(reader.Width, reader.Height, reader.PixelAspectRatio, options.MaxWidth);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var queue = new BlockingCollection<(byte[] Pixels, long Delay)>(FrameBufferCount);
            using var free = new BlockingCollection<byte[]>(FrameBufferCount);
            for (int i = 0; i < FrameBufferCount; i++)
            {
                free.Add(GC.AllocateUninitializedArray<byte>(width * height * 4, pinned: true));
            }

            var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
            var writer = new GifWriter(stream, width, height, options);
            Exception? consumerError = null;
            Task consumer = Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        foreach ((byte[] pixels, long delay) in queue.GetConsumingEnumerable(linked.Token))
                        {
                            writer.AddFrame(pixels, width * 4, TimeSpan.FromTicks(delay));
                            free.Add(pixels);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        consumerError = ex;
                        linked.Cancel();
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            bool success = false;
            try
            {
                try
                {
                    Produce(reader, options.FramesPerSecond, width, height, queue, free, progress, linked.Token);
                    queue.CompleteAdding();
                    consumer.Wait(CancellationToken.None);
                }
                catch (OperationCanceledException) when (consumerError is not null)
                {
                    // The encoder failed and canceled decoding; its exception is rethrown below.
                }

                if (consumerError is not null)
                {
                    ExceptionDispatchInfo.Throw(consumerError);
                }

                ct.ThrowIfCancellationRequested();
                writer.Dispose();
                stream.Dispose();
                File.Move(partial, output, overwrite: true);
                success = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException("GIF conversion was canceled.", ct);
            }
            finally
            {
                if (!success)
                {
                    linked.Cancel();
                    queue.CompleteAdding();
                    try
                    {
                        consumer.Wait(CancellationToken.None);
                    }
                    catch (AggregateException)
                    {
                        // Already reported through consumerError.
                    }

                    stream.Dispose();
                    TryDelete(partial);
                }
            }

            progress?.Report(1.0);
            return new GifResult(output, width, height, writer.FrameCount,
                TimeSpan.FromMilliseconds(writer.TotalCentiseconds * 10.0), new FileInfo(output).Length);
        }
    }

    private static unsafe void Produce(VideoFrameReader reader, int fps, int width, int height,
        BlockingCollection<(byte[] Pixels, long Delay)> queue, BlockingCollection<byte[]> free,
        IProgress<double>? progress, CancellationToken token)
    {
        long duration = reader.Duration;
        long TickTime(long k) => ((k * TicksPerSecond) + (fps / 2)) / fps;

        ImageScaler? scaler = null;
        int scalerVersion = -1;
        IMFSample? held = null;
        long heldTime = 0, heldDuration = 0;
        int heldId = 0, nextId = 0, assignedId = -1;
        byte[]? pending = null;
        long pendingStart = 0;
        long k = 0;
        double reported = 0;

        void Assign(long tickTime)
        {
            if (heldId == assignedId)
            {
                return;
            }

            byte[] buffer = free.Take(token);
            if (scaler is null || scalerVersion != reader.FormatVersion)
            {
                scaler = new ImageScaler(reader.Width, reader.Height, width, height);
                scalerVersion = reader.FormatVersion;
            }

            fixed (byte* dst = buffer)
            {
                nint dstPtr = (nint)dst;
                ImageScaler s = scaler;
                reader.WithPixels(held!, (src, pitch) => s.Scale(src, pitch, dstPtr));
            }

            if (pending is not null)
            {
                queue.Add((pending, tickTime - pendingStart), token);
            }

            pending = buffer;
            pendingStart = tickTime;
            assignedId = heldId;
        }

        try
        {
            while (reader.TryReadFrame(out IMFSample sample, out long timestamp, out long sampleDuration))
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (held is not null)
                    {
                        while (TickTime(k) + TimestampTolerance < timestamp)
                        {
                            Assign(TickTime(k));
                            k++;
                        }

                        held.Dispose();
                    }
                }
                catch
                {
                    sample.Dispose();
                    throw;
                }

                held = sample;
                heldTime = timestamp;
                heldDuration = sampleDuration;
                heldId = ++nextId;

                if (progress is not null && duration > 0)
                {
                    double p = Math.Clamp((double)timestamp / duration, 0, 1) * 0.99;
                    if (p - reported >= 0.01)
                    {
                        reported = p;
                        progress.Report(p);
                    }
                }
            }

            if (held is null)
            {
                throw new InvalidDataException("The video does not contain any decodable frames.");
            }

            long end = duration > 0 ? duration : heldTime + heldDuration;
            if (end <= heldTime)
            {
                end = heldTime + Math.Max(heldDuration, TickTime(1));
            }

            while (TickTime(k) < end)
            {
                token.ThrowIfCancellationRequested();
                Assign(TickTime(k));
                k++;
            }

            if (pending is null)
            {
                Assign(0);
                pendingStart = 0;
            }

            queue.Add((pending!, Math.Max(end - pendingStart, 1)), token);
        }
        finally
        {
            held?.Dispose();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
