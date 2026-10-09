using System.Globalization;

namespace Hotshot.Editor.Model;

/// <summary>Straight (non-premultiplied) 8-bit RGBA color, independent of any UI framework.</summary>
internal readonly record struct RgbaColor(byte R, byte G, byte B, byte A = 255)
{
    public static RgbaColor Red { get; } = new(0xFF, 0x3B, 0x30);
    public static RgbaColor Orange { get; } = new(0xFF, 0x95, 0x00);
    public static RgbaColor Yellow { get; } = new(0xFF, 0xD6, 0x0A);
    public static RgbaColor Green { get; } = new(0x30, 0xD1, 0x58);
    public static RgbaColor Blue { get; } = new(0x0A, 0x84, 0xFF);
    public static RgbaColor Purple { get; } = new(0xBF, 0x5A, 0xF2);
    public static RgbaColor Black { get; } = new(0x00, 0x00, 0x00);
    public static RgbaColor White { get; } = new(0xFF, 0xFF, 0xFF);

    public static IReadOnlyList<(string Name, RgbaColor Color)> Palette { get; } =
    [
        ("Red", Red), ("Orange", Orange), ("Yellow", Yellow), ("Green", Green),
        ("Blue", Blue), ("Purple", Purple), ("Black", Black), ("White", White),
    ];

    /// <summary>Perceived brightness in [0, 1].</summary>
    public float Luminance => (0.299f * R + 0.587f * G + 0.114f * B) / 255f;

    /// <summary>Black or white, whichever reads better on top of this color.</summary>
    public RgbaColor Contrasting => Luminance > 0.62f ? Black : White;

    public RgbaColor WithAlpha(byte alpha) => this with { A = alpha };

    public string ToHex() => A == 255
        ? $"#{R:X2}{G:X2}{B:X2}"
        : $"#{R:X2}{G:X2}{B:X2}{A:X2}";

    public override string ToString() => ToHex();

    public static bool TryParse(string? text, out RgbaColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim().TrimStart('#');
        if ((s.Length != 6 && s.Length != 8) || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
        {
            return false;
        }

        color = s.Length == 6
            ? new RgbaColor((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : new RgbaColor((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static RgbaColor Parse(string text) =>
        TryParse(text, out var c) ? c : throw new FormatException($"Invalid color '{text}'.");
}
