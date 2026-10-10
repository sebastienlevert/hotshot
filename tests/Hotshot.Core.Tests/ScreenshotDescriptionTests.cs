using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using Hotshot.Core.Descriptions;
using Hotshot.Core.History;
using Hotshot.Core.Updates;

namespace Hotshot.Core.Tests;

public sealed class ScreenshotDescriptionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hotshot-description-tests-" + Guid.NewGuid().ToString("N"));

    public ScreenshotDescriptionTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void Metadata_RoundTripsUnicode_WithoutChangingPixels()
    {
        var png = Png();
        var description = new CaptureDescription("A caf\u00e9 chart", "A blue chart beside a \u732b icon.");
        var embedded = PngDescriptionMetadata.Embed(png, description);
        Assert.Equal(description, PngDescriptionMetadata.Read(embedded));
        Assert.Null(PngDescriptionMetadata.Read(png));
        Assert.True(embedded.AsSpan(0, png.Length - 12).SequenceEqual(png.AsSpan(0, png.Length - 12)));
        var updated = PngDescriptionMetadata.Embed(embedded, new CaptureDescription("New summary", "New description"));
        Assert.Equal(new CaptureDescription("New summary", "New description"), PngDescriptionMetadata.Read(updated));
        Assert.DoesNotContain("caf", Encoding.UTF8.GetString(updated));
    }

    [Fact]
    public void Metadata_RejectsMalformedPng_AndInvalidModelOutput()
    {
        Assert.Throws<InvalidDataException>(() => PngDescriptionMetadata.Read([1, 2, 3]));
        var png = Png();
        png[20] ^= 1;
        Assert.Throws<InvalidDataException>(() => PngDescriptionMetadata.Embed(png, new CaptureDescription("Summary", "Description")));
        Assert.Throws<InvalidDataException>(() => CaptureDescription.Parse("""{"summary":"","description":"A chart"}"""));
        Assert.Throws<InvalidDataException>(() => CaptureDescription.Parse("""{"summary":"Chart","description":""}"""));
        Assert.Equal(new CaptureDescription("Chart", "A chart"),
            CaptureDescription.Parse("```json\n{\"summary\":\"Chart\",\"description\":\"A chart\"}\n```"));
    }

    [Fact]
    public async Task OptOut_DoesNotCreateProvider_OrChangeTheImage()
    {
        var (history, item) = Fixture();
        var original = await File.ReadAllBytesAsync(item.Path, TestContext.Current.CancellationToken);
        await using var service = new ScreenshotDescriptions(history, () => throw new InvalidOperationException("Provider must not start."), false);
        Assert.False(await service.EnqueueAsync(item));
        Assert.Equal(original, await File.ReadAllBytesAsync(item.Path, TestContext.Current.CancellationToken));
        Assert.Null(item.Description);
    }

    [Fact]
    public async Task Description_IsEmbedded_Indexed_AndPreservedByEditorSave()
    {
        var (history, item) = Fixture();
        var description = new CaptureDescription("Revenue chart", "A chart of monthly revenue.");
        await using var service = new ScreenshotDescriptions(history,
            () => new Provider((_, _) => Task.FromResult(description)), true);
        Assert.True(await service.EnqueueAsync(item));
        Assert.Equal(description, PngDescriptionMetadata.Read(await File.ReadAllBytesAsync(item.Path, TestContext.Current.CancellationToken)));
        Assert.True(item.MatchesSearch("revenue"));
        Assert.Equal(new FileInfo(item.Path).Length, item.FileSize);
        var size = await service.SaveEditedImageAsync(item, Png());
        Assert.Equal(size, new FileInfo(item.Path).Length);
        Assert.Equal(description, PngDescriptionMetadata.Read(await File.ReadAllBytesAsync(item.Path, TestContext.Current.CancellationToken)));
        Assert.False(new UpdateActivity(Describing: true).CanRestart);
    }

    [Fact]
    public async Task Disabling_CancelsRequests_AndLeavesTheSavedImageIntact()
    {
        var (history, item) = Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = await File.ReadAllBytesAsync(item.Path, TestContext.Current.CancellationToken);
        await using var service = new ScreenshotDescriptions(history, () => new Provider(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new CaptureDescription("Never", "Never");
        }), true);
        var pending = service.EnqueueAsync(item);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        service.SetEnabled(false);
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(original, await File.ReadAllBytesAsync(item.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImageChangedDuringDescription_IsReprocessedWithoutOverwritingEdits()
    {
        var (history, item) = Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var service = new ScreenshotDescriptions(history, () => new Provider(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(token);
            }
            return new CaptureDescription("Updated chart", "The edited screenshot.");
        }), true);
        var pending = service.EnqueueAsync(item);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var edited = PngDescriptionMetadata.Embed(Png(), new CaptureDescription("Edit", "Edited image metadata"));
        await service.SaveEditedImageAsync(item, edited);
        release.SetResult();
        Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
        Assert.Equal("Updated chart", PngDescriptionMetadata.Read(await File.ReadAllBytesAsync(item.Path, TestContext.Current.CancellationToken))?.Summary);
    }

    [Fact]
    public async Task Shutdown_PreservesPendingWorkForNextLaunch()
    {
        var (history, item) = Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ScreenshotDescriptions(history, () => new Provider(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new CaptureDescription("Never", "Never");
        }), true);
        var pending = service.EnqueueAsync(item);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await service.DisposeAsync();
        Assert.False(await pending);
        Assert.True(item.DescriptionPending);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task Failure_IsVisible_WithoutLosingCapture()
    {
        var (history, item) = Fixture();
        await using var service = new ScreenshotDescriptions(history,
            () => new Provider((_, _) => throw new IOException("Synthetic offline failure")), true);
        string? failure = null;
        service.Failed += message => failure = message;
        Assert.False(await service.EnqueueAsync(item));
        Assert.True(File.Exists(item.Path));
        Assert.Equal("Synthetic offline failure", item.DescriptionError);
        Assert.Contains("saved", failure!);
    }

    private (HistoryStore History, HistoryItem Item) Fixture()
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, Png());
        var history = new HistoryStore(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json"), 10);
        var item = new HistoryItem { Path = path, FileSize = new FileInfo(path).Length };
        history.Add(item);
        return (history, item);
    }

    private static byte[] Png()
    {
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), 1);
        header[8] = 8;
        header[9] = 6;
        Chunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zip = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zip.Write([0, 30, 90, 180, 255]);
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", []);
        return png.ToArray();

        void Chunk(string type, byte[] data)
        {
            var buffer = new byte[data.Length + 12];
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, 4), (uint)data.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(buffer, 4);
            data.CopyTo(buffer, 8);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(data.Length + 8, 4), Crc32.HashToUInt32(buffer.AsSpan(4, data.Length + 4)));
            png.Write(buffer);
        }
    }

    private sealed class Provider(Func<byte[], CancellationToken, Task<CaptureDescription>> describe) : ICaptureDescriptionProvider
    {
        public Task<CaptureDescription> DescribeAsync(byte[] png, CancellationToken token) => describe(png, token);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
