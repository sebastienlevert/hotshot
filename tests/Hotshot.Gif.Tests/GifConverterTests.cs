using Hotshot.Gif.Tests.Support;

namespace Hotshot.Gif.Tests;

public sealed class GifConverterTests
{
    [Fact]
    public async Task ConvertProducesAPlayableGifFromSyntheticVideo()
    {
        string input = CreateVideo();
        string output = TestPaths.Unique("converter", ".gif");

        var result = await GifConverter.ConvertAsync(input, output, new GifOptions { MaxWidth = 32, FramesPerSecond = 10 },
                ct: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var gif = ParsedGif.Parse(output);
        Assert.Equal((32, 24), (result.Width, result.Height));
        Assert.Equal(result.FrameCount, gif.Frames.Count);
        Assert.Equal(120, gif.TotalDelayCentiseconds);
        Assert.Equal(TimeSpan.FromSeconds(1.2), result.Duration);
        Assert.Equal(new FileInfo(output).Length, result.FileSize);
        AssertInputReleased(input);
        AssertNoPartialFiles(output);
    }

    [Fact]
    public async Task CancellationPreservesExistingOutputAndReleasesThePipeline()
    {
        string input = CreateVideo();
        string output = TestPaths.Unique("converter-cancel", ".gif");
        byte[] original = "existing output"u8.ToArray();
        await File.WriteAllBytesAsync(output, original, TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var progress = new SyncProgress(_ => cancellation.Cancel());

        Task<GifResult> conversion = GifConverter.ConvertAsync(input, output, progress: progress, ct: cancellation.Token);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => conversion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(conversion.IsCanceled);
        Assert.Equal(original, await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        AssertInputReleased(input);
        AssertNoPartialFiles(output);
    }

    [Fact]
    public async Task ProducerFailureIsNotWrappedAndReleasesThePipeline()
    {
        string input = CreateVideo();
        string output = TestPaths.Unique("converter-failure", ".gif");
        byte[] original = "existing output"u8.ToArray();
        await File.WriteAllBytesAsync(output, original, TestContext.Current.CancellationToken);
        var expected = new InvalidOperationException("Synthetic progress failure.");
        var progress = new SyncProgress(_ => throw expected);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GifConverter.ConvertAsync(input, output, progress: progress, ct: TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        Assert.Same(expected, error);
        Assert.Equal(original, await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        AssertInputReleased(input);
        AssertNoPartialFiles(output);
    }

    private static string CreateVideo()
    {
        string path = TestPaths.Unique("converter-source", ".mp4");
        TestVideo.CreateMp4(path, 64, 48, 10, 12,
            static (index, pixels, _, _) => pixels.Fill(index < 6 ? 0xFFFF0000u : 0xFF0000FFu));
        return path;
    }

    private static void AssertInputReleased(string input)
    {
        using var file = new FileStream(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(file.Length > 0);
    }

    private static void AssertNoPartialFiles(string output) =>
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(output)!, Path.GetFileName(output) + ".*.partial"));
}
