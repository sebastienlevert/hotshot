namespace Hotshot.Capture;

public sealed class ScrollingImage
{
    private CapturedImage _last;
    public CapturedImage Image { get; private set; }
    public int FrameCount { get; private set; } = 1;
    public ScrollingImage(CapturedImage first) => Image = _last = first;

    public int Append(CapturedImage next)
    {
        if (next.Width != _last.Width || next.Height != _last.Height)
            throw new ArgumentException("Scrolling frames must have the same size. Keep the window and selected region fixed.");
        var min = Math.Max(16, next.Height / 8);
        var best = double.MaxValue; var runnerUp = double.MaxValue; var overlap = 0;
        for (var candidate = min; candidate < next.Height; candidate++)
        {
            var error = Error(_last, next, candidate);
            if (error < best) { runnerUp = best; best = error; overlap = candidate; }
            else runnerUp = Math.Min(runnerUp, error);
        }
        if (overlap == 0 || best > 3 || runnerUp - best < 0.1)
            throw new InvalidOperationException("No unambiguous scroll overlap was found. Scroll less, exclude fixed headers, and try again. Nothing was appended.");
        var height = Image.Height + next.Height - overlap;
        if (height > 16384 || (long)height * next.Width > 64_000_000)
            throw new InvalidOperationException("The scrolling image reached the 16,384-pixel / 64-megapixel limit.");
        var pixels = new byte[checked(height * next.Width * 4)];
        Image.Pixels.AsSpan(0, Image.Height * Image.Stride).CopyTo(pixels);
        next.Pixels.AsSpan(overlap * next.Stride, (next.Height - overlap) * next.Stride)
            .CopyTo(pixels.AsSpan(Image.Height * next.Stride));
        Image = new CapturedImage(next.Width, height, pixels);
        _last = next;
        FrameCount++;
        return overlap;
    }

    private static double Error(CapturedImage before, CapturedImage after, int overlap)
    {
        long difference = 0; var count = 0;
        for (var row = 0; row < 24; row++)
        {
            var y = row * (overlap - 1) / 23;
            for (var column = 0; column < 32; column++)
            {
                var x = column * (before.Width - 1) / 31;
                var first = ((before.Height - overlap + y) * before.Width + x) * 4;
                var second = (y * after.Width + x) * 4;
                for (var channel = 0; channel < 3; channel++)
                {
                    difference += Math.Abs(before.Pixels[first + channel] - after.Pixels[second + channel]);
                    count++;
                }
            }
        }
        return difference / (double)count;
    }
}
