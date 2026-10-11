namespace Hotshot.Editor;

public enum ImageExportFormat { Png, Jpeg }
public enum ExportBackground { Transparent, White, Charcoal, Blue }

public sealed record ImageExportOptions(ImageExportFormat Format = ImageExportFormat.Png, int Width = 0,
    int Padding = 0, int CornerRadius = 0, bool Shadow = false, ExportBackground Background = ExportBackground.Transparent)
{
    public (int ImageWidth, int ImageHeight, int CanvasWidth, int CanvasHeight) GetLayout(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth < 1 || sourceHeight < 1 || Width < 0 || Width > 16384 ||
            Padding < 0 || Padding > 400 || CornerRadius < 0 || CornerRadius > 400 ||
            !Enum.IsDefined(Format) || !Enum.IsDefined(Background))
            throw new ArgumentException("Export dimensions or style are outside the supported range.");
        var width = Width == 0 ? sourceWidth : Width;
        var height = Math.Max(1, (int)Math.Round((double)sourceHeight * width / sourceWidth));
        var canvasWidth = checked(width + 2 * Padding);
        var canvasHeight = checked(height + 2 * Padding);
        if (canvasWidth > 16384 || canvasHeight > 16384 || (long)canvasWidth * canvasHeight > 64_000_000)
            throw new ArgumentException("Export must be at most 16,384 pixels per side and 64 megapixels.");
        return (width, height, canvasWidth, canvasHeight);
    }
}
