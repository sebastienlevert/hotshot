using Hotshot.Capture;

namespace Hotshot.Capture.Tests;

public sealed class NativeLifetimeTests
{
    [Fact]
    public void BitmapDisposalIsIdempotentAndRejectsAccess()
    {
        var bitmap = new GdiBitmap(8, 8);
        Assert.NotEqual(0, bitmap.Dc);
        Assert.NotEqual(0, bitmap.Handle);
        Assert.Equal(8 * 8 * 4, bitmap.Span.Length);
        bitmap.Dispose();
        bitmap.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = bitmap.Dc);
        Assert.Throws<ObjectDisposedException>(() => _ = bitmap.Handle);
        Assert.Throws<ObjectDisposedException>(() => bitmap.Copy(new PixelRect(0, 0, 8, 8)));
    }

    [Fact]
    public void BitmapRejectsOverflowBeforeAllocatingNativeResources()
    {
        Assert.Throws<OverflowException>(() => new GdiBitmap(int.MaxValue, 2));
    }

    [Fact]
    public void BitmapCopiesGeneratedPixelsWithOpaqueAlpha()
    {
        using var bitmap = new GdiBitmap(8, 8);
        bitmap.Span.Fill(23);
        var image = bitmap.Copy(new PixelRect(0, 0, 8, 8));
        Assert.Equal(23, image.Pixels[0]);
        Assert.Equal(255, image.Pixels[3]);
        Assert.Equal(8 * 8 * 4, image.Pixels.Length);
    }
}
