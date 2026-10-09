namespace Hotshot.Gif;

/// <summary>Options controlling MP4 → GIF conversion and <see cref="GifWriter"/> encoding.</summary>
public sealed class GifOptions
{
    /// <summary>Output frame rate (5..30). Source frames are picked by timestamp; slower sources are held.</summary>
    public int FramesPerSecond { get; init; } = 15;

    /// <summary>Maximum output width in pixels; 0 keeps the source width. Height keeps the aspect ratio. Never upscales.</summary>
    public int MaxWidth { get; init; } = 800;

    /// <summary>Use ordered (Bayer 8x8) dithering for colors that are not in the palette. Flat palette colors are never dithered.</summary>
    public bool Dither { get; init; } = true;

    /// <summary>NETSCAPE2.0 loop count (number of repetitions); 0 = loop forever.</summary>
    public int LoopCount { get; init; } = 0;

    /// <summary>When false, omit the loop extension so the animation plays once.</summary>
    public bool Loop { get; init; } = true;

    /// <summary>Maximum color table size (2..256), including the transparency slot used by <see cref="OptimizeFrames"/>.</summary>
    public int MaxColors { get; init; } = 256;

    /// <summary>
    /// Inter-frame optimization: unchanged pixels become transparent, each frame is cropped to the bounds of
    /// changed pixels and identical consecutive frames are merged by extending the previous delay.
    /// </summary>
    public bool OptimizeFrames { get; init; } = true;

    internal void ValidateForWriter()
    {
        if (MaxColors is < 2 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxColors), MaxColors, "MaxColors must be between 2 and 256.");
        }

        if (LoopCount is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(LoopCount), LoopCount, "LoopCount must be between 0 and 65535.");
        }
    }

    internal void ValidateForConverter()
    {
        ValidateForWriter();
        if (FramesPerSecond is < 5 or > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(FramesPerSecond), FramesPerSecond, "FramesPerSecond must be between 5 and 30.");
        }

        if (MaxWidth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxWidth), MaxWidth, "MaxWidth must be 0 (keep source width) or positive.");
        }
    }
}

/// <summary>Result of a <see cref="GifConverter.ConvertAsync"/> call.</summary>
/// <param name="Path">Full path of the written GIF.</param>
/// <param name="Width">Logical screen width.</param>
/// <param name="Height">Logical screen height.</param>
/// <param name="FrameCount">Number of GIF frames written (after merging identical frames).</param>
/// <param name="Duration">Total animation duration (sum of frame delays).</param>
/// <param name="FileSize">Size of the GIF file in bytes.</param>
public sealed record GifResult(string Path, int Width, int Height, int FrameCount, TimeSpan Duration, long FileSize);
