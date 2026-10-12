using Hotshot.Capture;

namespace Hotshot.Capture.Tests;

public sealed class ComparisonTests
{
    [Fact]
    public void ExplicitCancellationStopsComparisonBeforeAllocation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ImageComparison.CompareCancellable(Solid(2, 2, 0), Solid(2, 2, 0), new ComparisonOptions(), 8, cancellation.Token));
    }
    [Fact]
    public void LegacyApiKeepsStrictDimensionsAndPopulatesComparedCount()
    {
        var image = Solid(3, 2, 0);
        var result = ImageComparison.Compare(image, image);
        Assert.Equal(6, result.ComparedPixels);
        Assert.Equal(new PixelRect(0, 0, 3, 2), result.ComparedBounds);
        Assert.Equal(0, result.ChangedPercent);
        Assert.Equal(8, result.Tolerance);
        Assert.Throws<ArgumentException>(() => ImageComparison.Compare(image, Solid(2, 2, 0)));
    }

    [Fact]
    public void ExplicitOptionsAllowMismatchedSizesAndIgnoreNonoverlap()
    {
        var result = ImageComparison.Compare(Solid(4, 3, 0), Solid(2, 2, 40), new ComparisonOptions(), 0);
        Assert.Equal(4, result.ComparedPixels);
        Assert.Equal(4, result.ChangedPixels);
        Assert.Equal(100, result.ChangedPercent);
        Assert.Equal(new PixelRect(0, 0, 2, 2), result.ChangedBounds);
        Assert.Equal(4, result.Difference.Width);
        Assert.Equal(3, result.Difference.Height);
        Assert.Equal(new byte[] { 72, 72, 72, 255 }, result.Difference.Pixels.AsSpan(44, 4).ToArray());
    }

    [Theory]
    [InlineData(1, 1, 1, 1)]
    [InlineData(-1, -1, 0, 0)]
    [InlineData(1, -1, 1, 0)]
    [InlineData(-1, 1, 0, 1)]
    public void OffsetsCountOnlyIntersectionInBeforeCoordinates(int dx, int dy, int left, int top)
    {
        var result = ImageComparison.Compare(Solid(3, 3, 0), Solid(3, 3, 50),
            new ComparisonOptions { OffsetX = dx, OffsetY = dy });
        Assert.Equal(4, result.ComparedPixels);
        Assert.Equal(4, result.ChangedPixels);
        Assert.Equal(100, result.ChangedPercent);
        Assert.Equal(new PixelRect(left, top, 2, 2), result.ComparedBounds);
        Assert.Equal(result.ComparedBounds, result.ChangedBounds);
    }

    [Fact]
    public void AlignmentSamplesCorrectAfterPixels()
    {
        var before = Solid(4, 3, 0);
        var after = Solid(3, 2, 0);
        before.Pixels[(1 * 4 + 2) * 4] = 100;
        after.Pixels[(0 * 3 + 1) * 4] = 100;
        var options = new ComparisonOptions { OffsetX = 1, OffsetY = 1 };
        Assert.Equal(0, ImageComparison.Compare(before, after, options, 0).ChangedPixels);
        after.Pixels[(1 * 3 + 2) * 4 + 2] = 90;
        var result = ImageComparison.Compare(before, after, options, 0);
        Assert.Equal(6, result.ComparedPixels);
        Assert.Equal(1, result.ChangedPixels);
        Assert.Equal(new PixelRect(3, 2, 1, 1), result.ChangedBounds);
        Assert.Equal(100d / 6, result.ChangedPercent, 8);
    }

    [Fact]
    public void NegativeAlignmentSamplesCorrectAfterPixels()
    {
        var before = Solid(2, 2, 0);
        var after = Solid(3, 3, 0);
        before.Pixels[0] = 120;
        after.Pixels[(1 * 3 + 1) * 4] = 120;
        var result = ImageComparison.Compare(before, after, new ComparisonOptions { OffsetX = -1, OffsetY = -1 }, 0);
        Assert.Equal(4, result.ComparedPixels);
        Assert.Equal(0, result.ChangedPixels);
    }

    [Fact]
    public void IgnoredRectanglesAreUnionedClippedAndExcludedFromDenominator()
    {
        var result = ImageComparison.Compare(Solid(4, 3, 0), Solid(4, 3, 40), new ComparisonOptions
        {
            IgnoredRegions = [new(-2, -2, 4, 4), new(1, 1, 2, 2), new(1, 1, 2, 2), new(100, 100, 4, 4)],
        });
        Assert.Equal(5, result.ComparedPixels);
        Assert.Equal(5, result.ChangedPixels);
        Assert.Equal(100, result.ChangedPercent);
        Assert.Equal(new PixelRect(0, 0, 4, 3), result.ChangedBounds);
    }

    [Fact]
    public void IgnoredChangeDoesNotAffectBoundsOrPercentage()
    {
        var before = Solid(4, 2, 0);
        var after = Solid(4, 2, 0);
        after.Pixels[0] = 50;
        after.Pixels[(1 * 4 + 3) * 4] = 50;
        var result = ImageComparison.Compare(before, after, new ComparisonOptions { IgnoredRegions = [new(0, 0, 1, 1)] });
        Assert.Equal(7, result.ComparedPixels);
        Assert.Equal(1, result.ChangedPixels);
        Assert.Equal(100d / 7, result.ChangedPercent, 8);
        Assert.Equal(new PixelRect(3, 1, 1, 1), result.ChangedBounds);
    }

    [Fact]
    public void ExclusionsUseBeforeCoordinatesAfterAlignment()
    {
        var result = ImageComparison.Compare(Solid(3, 3, 0), Solid(3, 3, 50), new ComparisonOptions
        {
            OffsetX = 1, OffsetY = 1,
            IgnoredRegions = [new(0, 0, 2, 2), new(2, 2, 1, 1)],
        });
        Assert.Equal(2, result.ComparedPixels);
        Assert.Equal(2, result.ChangedPixels);
        Assert.Equal(100, result.ChangedPercent);
        Assert.Equal(new PixelRect(1, 1, 2, 2), result.ChangedBounds);
    }

    [Fact]
    public void AllIgnoredReturnsZeroAndOpaqueNeutralDifference()
    {
        var result = ImageComparison.Compare(Solid(2, 2, 0), Solid(2, 2, 255),
            new ComparisonOptions { IgnoredRegions = [new(0, 0, 2, 2)] });
        Assert.Equal(0, result.ComparedPixels);
        Assert.Equal(0, result.ChangedPixels);
        Assert.Equal(0, result.ChangedPercent);
        Assert.Null(result.ChangedBounds);
        Assert.Equal(new PixelRect(0, 0, 2, 2), result.ComparedBounds);
        Assert.Equal(Enumerable.Repeat(new byte[] { 72, 72, 72, 255 }, 4).SelectMany(p => p).ToArray(), result.Difference.Pixels);
    }

    [Theory]
    [InlineData(3, 0)]
    [InlineData(0, -3)]
    [InlineData(int.MaxValue, int.MinValue)]
    [InlineData(int.MinValue, int.MaxValue)]
    public void ZeroIntersectionAndExtremeOffsetsAreSafe(int dx, int dy)
    {
        var result = ImageComparison.Compare(Solid(3, 3, 0), Solid(3, 3, 255),
            new ComparisonOptions { OffsetX = dx, OffsetY = dy });
        Assert.Equal(0, result.ComparedPixels);
        Assert.Equal(0, result.ChangedPixels);
        Assert.Equal(0, result.ChangedPercent);
        Assert.Null(result.ComparedBounds);
        Assert.Null(result.ChangedBounds);
    }

    [Fact]
    public void RectangleEndpointOverflowDoesNotWrapIntoImage()
    {
        var result = ImageComparison.Compare(Solid(2, 2, 0), Solid(2, 2, 255), new ComparisonOptions
        {
            IgnoredRegions = [new(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue),
                new(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue), new(-1, -1, int.MaxValue, int.MaxValue)],
        });
        Assert.Equal(0, result.ComparedPixels);
    }

    [Fact]
    public void EmptyRectanglesAreNoOps()
    {
        var result = ImageComparison.Compare(Solid(2, 2, 0), Solid(2, 2, 255), new ComparisonOptions
        {
            IgnoredRegions = [new(0, 0, 0, 2), new(0, 0, 2, 0)],
        });
        Assert.Equal(4, result.ComparedPixels);
    }

    [Fact]
    public void ResultOptionsAreAnIndependentSnapshot()
    {
        var regions = new[] { new PixelRect(0, 0, 1, 1) };
        var result = ImageComparison.Compare(Solid(2, 2, 0), Solid(2, 2, 20),
            new ComparisonOptions { OffsetX = 1, IgnoredRegions = regions }, 5);
        regions[0] = new PixelRect(0, 0, 2, 2);
        Assert.Equal(1, result.Options.OffsetX);
        Assert.Equal(new PixelRect(0, 0, 1, 1), Assert.Single(result.Options.IgnoredRegions));
        Assert.Equal(5, result.Tolerance);
    }

    [Fact]
    public void MaximumIgnoredRectangleCountIsAccepted()
    {
        var image = Solid(2, 2, 0);
        var result = ImageComparison.Compare(image, image, new ComparisonOptions
        {
            IgnoredRegions = Enumerable.Repeat(new PixelRect(), ImageComparison.MaximumIgnoredRegions).ToArray(),
        });
        Assert.Equal(4, result.ComparedPixels);
        Assert.Equal(ImageComparison.MaximumIgnoredRegions, result.Options.IgnoredRegions.Count);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    [InlineData(0, -1)]
    public void NegativeRectangleDimensionsAreRejectedEvenWithoutOverlap(int width, int height)
    {
        var image = Solid(2, 2, 0);
        Assert.Throws<ArgumentException>(() => ImageComparison.Compare(image, image, new ComparisonOptions
        {
            OffsetX = int.MaxValue, IgnoredRegions = [new PixelRect(0, 0, width, height)],
        }));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void ToleranceIsValidatedOnBothOverloads(int tolerance)
    {
        var image = Solid(1, 1, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageComparison.Compare(image, image, tolerance));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageComparison.Compare(image, image, new ComparisonOptions(), tolerance));
    }

    [Fact]
    public void GeometryAndNullArgumentsAreValidated()
    {
        var image = Solid(1, 1, 0);
        Assert.Throws<ArgumentNullException>(() => ImageComparison.Compare(null!, image));
        Assert.Throws<ArgumentNullException>(() => ImageComparison.Compare(image, null!, new ComparisonOptions()));
        Assert.Throws<ArgumentNullException>(() => ImageComparison.Compare(image, image, (ComparisonOptions)null!));
        Assert.Throws<ArgumentNullException>(() => ImageComparison.Compare(image, image, new ComparisonOptions { IgnoredRegions = null! }));
        Assert.Throws<ArgumentException>(() => ImageComparison.Compare(image, image, new ComparisonOptions { IgnoredRegions = [new(0, 0, -1, 1)] }));
        Assert.Throws<ArgumentException>(() => ImageComparison.Compare(image, image, new ComparisonOptions
        {
            IgnoredRegions = Enumerable.Repeat(new PixelRect(), ImageComparison.MaximumIgnoredRegions + 1).ToArray(),
        }));
        Assert.Throws<ArgumentException>(() => new CapturedImage(64_000_001, 1, []));
        Assert.Throws<ArgumentException>(() => new CapturedImage(int.MaxValue, int.MaxValue, []));
        Assert.Throws<ArgumentException>(() => new CapturedImage(2, 2, new byte[15]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapturedImage(0, 2, []));
    }

    [Fact]
    public void ComparisonDoesNotMutateInputsAndIgnoresAlpha()
    {
        var before = Solid(1, 1, 10);
        var after = Solid(1, 1, 10);
        before.Pixels[3] = 0;
        var originalBefore = before.Pixels.ToArray();
        var originalAfter = after.Pixels.ToArray();
        Assert.Equal(0, ImageComparison.Compare(before, after, 0).ChangedPixels);
        Assert.Equal(originalBefore, before.Pixels);
        Assert.Equal(originalAfter, after.Pixels);
        Assert.Equal(0, ImageComparison.Compare(before, Solid(1, 1, 255), 255).ChangedPixels);
    }

    [Fact]
    public void BandedExclusionsMatchIndependentPixelReference()
    {
        var random = new Random(48261);
        for (var sample = 0; sample < 100; sample++)
        {
            var before = RandomImage(random.Next(1, 9), random.Next(1, 9));
            var after = RandomImage(random.Next(1, 9), random.Next(1, 9));
            var options = new ComparisonOptions
            {
                OffsetX = random.Next(-8, 9), OffsetY = random.Next(-8, 9),
                IgnoredRegions = Enumerable.Range(0, 6).Select(_ =>
                    new PixelRect(random.Next(-10, 11), random.Next(-10, 11), random.Next(0, 13), random.Next(0, 13))).ToArray(),
            };
            var tolerance = random.Next(0, 256);
            var result = ImageComparison.Compare(before, after, options, tolerance);
            long compared = 0, changed = 0;
            int left = before.Width, top = before.Height, right = -1, bottom = -1;
            for (var y = 0; y < before.Height; y++)
            for (var x = 0; x < before.Width; x++)
            {
                var ax = x - options.OffsetX; var ay = y - options.OffsetY;
                var i = (y * before.Width + x) * 4;
                var excluded = ax < 0 || ay < 0 || ax >= after.Width || ay >= after.Height ||
                    options.IgnoredRegions.Any(rect => x >= rect.X && y >= rect.Y &&
                        x < (long)rect.X + rect.Width && y < (long)rect.Y + rect.Height);
                if (excluded)
                {
                    Assert.Equal(new byte[] { 72, 72, 72, 255 }, result.Difference.Pixels.AsSpan(i, 4).ToArray());
                    continue;
                }
                compared++;
                var j = (ay * after.Width + ax) * 4;
                var differs = Enumerable.Range(0, 3).Any(channel =>
                    Math.Abs(before.Pixels[i + channel] - after.Pixels[j + channel]) > tolerance);
                if (differs)
                {
                    changed++;
                    left = Math.Min(left, x); top = Math.Min(top, y);
                    right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                }
                Assert.Equal(differs ? (byte)80 : (byte)(after.Pixels[j] / 3), result.Difference.Pixels[i]);
                Assert.Equal(differs ? (byte)40 : (byte)(after.Pixels[j + 1] / 3), result.Difference.Pixels[i + 1]);
                Assert.Equal(differs ? (byte)255 : (byte)(after.Pixels[j + 2] / 3), result.Difference.Pixels[i + 2]);
                Assert.Equal((byte)255, result.Difference.Pixels[i + 3]);
            }
            Assert.Equal(compared, result.ComparedPixels);
            Assert.Equal(changed, result.ChangedPixels);
            Assert.Equal(compared == 0 ? 0 : changed * 100d / compared, result.ChangedPercent, 10);
            PixelRect? bounds = changed == 0 ? null : new PixelRect(left, top, right - left + 1, bottom - top + 1);
            Assert.Equal(bounds, result.ChangedBounds);
        }

        CapturedImage RandomImage(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            random.NextBytes(pixels);
            return new CapturedImage(width, height, pixels);
        }
    }

    private static CapturedImage Solid(int width, int height, byte channel)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = channel;
            pixels[i + 3] = 255;
        }
        return new CapturedImage(width, height, pixels);
    }
}
