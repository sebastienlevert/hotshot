namespace Hotshot.Recording;

/// <summary>Converts WGC's linear scRGB (1 = 80 nits) to opaque, 8-bit sRGB.</summary>
internal static class WindowScreenshotToneMapping
{
    private static readonly byte[] s_sdr = CreateTable(toneMap: false);
    private static readonly byte[] s_hdr = CreateTable(toneMap: true);

    /// <summary>
    /// SDR uses the IEC 61966-2-1 sRGB transfer function. Extended-range frames use per-channel
    /// Reinhard x/(1+x) first, over the entire frame, not just its bright pixels.
    /// Negative, out-of-sRGB-gamut components are clipped to black. This is an SDR rendition,
    /// not an HDR-preserving export or a display-specific color-management transform.
    /// </summary>
    internal static byte EncodeSrgb(float linear, bool toneMap)
    {
        if (!float.IsFinite(linear))
        {
            throw new ArgumentOutOfRangeException(nameof(linear), "scRGB components must be finite.");
        }

        linear = Math.Max(0, linear);
        if (toneMap)
        {
            linear /= 1 + linear;
        }

        linear = Math.Min(1, linear);
        float encoded = linear <= 0.0031308f
            ? 12.92f * linear
            : 1.055f * MathF.Pow(linear, 1 / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(encoded * 255, MidpointRounding.AwayFromZero), 0, 255);
    }

    internal static byte EncodeHalf(Half linear, bool toneMap)
    {
        ushort bits = BitConverter.HalfToUInt16Bits(linear);
        if ((bits & 0x7C00) == 0x7C00)
        {
            throw new ArgumentOutOfRangeException(nameof(linear), "scRGB components must be finite.");
        }

        return (toneMap ? s_hdr : s_sdr)[bits];
    }

    private static byte[] CreateTable(bool toneMap)
    {
        var table = new byte[ushort.MaxValue + 1];
        for (int i = 0; i < table.Length; i++)
        {
            float value = (float)BitConverter.UInt16BitsToHalf((ushort)i);
            // The capture path rejects non-finite frame data before consulting this table.
            if (float.IsFinite(value))
            {
                table[i] = EncodeSrgb(value, toneMap);
            }
        }

        return table;
    }
}
