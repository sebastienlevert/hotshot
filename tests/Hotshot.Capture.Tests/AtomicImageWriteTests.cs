using Hotshot.Capture;

namespace Hotshot.Capture.Tests;

public sealed class AtomicImageWriteTests
{
    [Fact]
    public async Task ConcurrentWritesPublishOneCompleteImageWithoutSharedStaging()
    {
        var directory = CreateDirectory();
        try
        {
            var path = Path.Combine(directory, "capture.png");
            var images = Enumerable.Range(0, 12).Select(index => Enumerable.Repeat((byte)index, 32_768).ToArray()).ToArray();
            await Task.WhenAll(images.Select(image => ImageCodec.WriteAtomicAsync(path, image, TestContext.Current.CancellationToken)));
            var actual = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(32_768, actual.Length);
            Assert.All(actual, value => Assert.Equal(actual[0], value));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CanceledWritePreservesExistingFile()
    {
        var directory = CreateDirectory();
        try
        {
            var path = Path.Combine(directory, "capture.png");
            await File.WriteAllBytesAsync(path, [1, 2, 3], TestContext.Current.CancellationToken);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageCodec.WriteAtomicAsync(path, [4, 5, 6], cancellation.Token));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task FailedCommitRemovesItsOwnTemporaryFile()
    {
        var directory = CreateDirectory();
        try
        {
            var destination = Path.Combine(directory, "directory-not-file");
            Directory.CreateDirectory(destination);
            var error = await Record.ExceptionAsync(() => ImageCodec.WriteAtomicAsync(destination, [1, 2, 3], TestContext.Current.CancellationToken));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Empty(Directory.GetFiles(directory));
            Assert.True(Directory.Exists(destination));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Hotshot.AtomicImage." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
