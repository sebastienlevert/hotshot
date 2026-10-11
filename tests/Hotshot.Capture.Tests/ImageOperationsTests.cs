using Hotshot.Capture;

namespace Hotshot.Capture.Tests;

public sealed class ImageOperationsTests
{
    [Fact]
    public void ComparisonMeasuresExactThresholdPercentageAndBounds()
    {
        var before = new CapturedImage(2, 2, new byte[16]);
        var pixels = new byte[16];
        pixels[4] = 5;
        var after = new CapturedImage(2, 2, pixels);
        Assert.Equal(0, ImageComparison.Compare(before, after, 5).ChangedPixels);
        var result = ImageComparison.Compare(before, after, 4);
        Assert.Equal(1, result.ChangedPixels);
        Assert.Equal(25, result.ChangedPercent);
        Assert.Equal(new PixelRect(1, 0, 1, 1), result.ChangedBounds);
        Assert.Null(ImageComparison.Compare(before, before).ChangedBounds);
        Assert.Throws<ArgumentException>(() => ImageComparison.Compare(before, new CapturedImage(3, 2, new byte[24])));
    }

    [Fact]
    public void CropPreservesPixelsAndRejectsOutOfBounds()
    {
        var pixels = Enumerable.Range(0, 4 * 3 * 4).Select(value => (byte)value).ToArray();
        var image = new CapturedImage(4, 3, pixels);
        var crop = image.Crop(new PixelRect(1, 1, 2, 1));
        Assert.Equal(pixels.AsSpan(20, 8).ToArray(), crop.Pixels);
        Assert.Throws<ArgumentException>(() => image.Crop(new PixelRect(-1, 0, 2, 2)));
        Assert.Throws<ArgumentException>(() => image.Crop(new PixelRect(3, 2, 2, 1)));
    }

    [Fact]
    public void ScrollingReconstructsExactPixelsAndRejectsAmbiguity()
    {
        var pixels = Enumerable.Range(0, 80 * 100).SelectMany(i => new byte[]
            { (byte)((i * 29) % 251), (byte)((i * 17 + i / 80 * 11) % 251), (byte)((i * 7) % 251), 255 }).ToArray();
        var image = new CapturedImage(80, 100, pixels);
        var scrolling = new ScrollingImage(image.Crop(new PixelRect(0, 0, 80, 70)));
        Assert.Equal(40, scrolling.Append(image.Crop(new PixelRect(0, 30, 80, 70))));
        Assert.Equal(2, scrolling.FrameCount);
        Assert.Equal(image.Pixels, scrolling.Image.Pixels);
        var uniform = new CapturedImage(80, 70, new byte[80 * 70 * 4]);
        Assert.Throws<InvalidOperationException>(() => new ScrollingImage(uniform).Append(uniform));
    }

    [Fact]
    public void ImageAllocationRejectsOversizedAndIncompleteBuffers()
    {
        Assert.Throws<ArgumentException>(() => new CapturedImage(8001, 8000, []));
        Assert.Throws<ArgumentException>(() => new CapturedImage(2, 2, new byte[15]));
    }
}
