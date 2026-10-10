using System.Diagnostics;
using Hotshot.Capture;
using Hotshot.Core;
using Hotshot.Core.History;
using Hotshot.Core.Naming;

namespace Hotshot.CaptureFlow;

/// <summary>Context captured at hotkey time and used for naming tokens.</summary>
internal sealed record CaptureSource(DateTime Timestamp, string? AppName, string? WindowTitle, int? MonitorIndex)
{
    public static CaptureSource Now(string? app = null, string? title = null, int? monitor = null) =>
        new(DateTime.Now, app, title, monitor);
}

/// <summary>Turns a capture into clipboard data, a named file, a thumbnail and a history entry.</summary>
internal sealed class OutputPipeline(AppServices app)
{
    public event Action<string>? Failed;

    /// <summary>Must be called on the UI thread (clipboard ownership).</summary>
    public async Task<HistoryItem?> SaveScreenshotAsync(CapturedImage image, CaptureKind kind, CaptureSource source)
    {
        var general = app.Settings.General;
        var timer = Stopwatch.StartNew();
        try
        {
            var png = await Task.Run(() => ImageCodec.EncodePngAsync(image)).ConfigureAwait(true);
            var encodeMs = timer.ElapsedMilliseconds;

            if (general.CopyToClipboard && !await ClipboardService.SetImageAsync(app.MessageWindowHandle, image, png))
            {
                Failed?.Invoke("Could not copy the screenshot to the clipboard.");
            }

            var clipboardMs = timer.ElapsedMilliseconds;
            var (path, isTemporary) = ReservePath(kind, source, image.Width, image.Height);
            var item = new HistoryItem
            {
                Path = path,
                Kind = kind,
                CreatedAt = new DateTimeOffset(source.Timestamp),
                Width = image.Width,
                Height = image.Height,
                FileSize = png.Length,
                AppName = source.AppName,
                WindowTitle = source.WindowTitle,
                MonitorIndex = source.MonitorIndex,
                IsTemporary = isTemporary,
                DescriptionPending = general.DescribeScreenshots,
            };
            var thumbnailPath = Path.Combine(app.Paths.ThumbnailsDirectory, item.Id + ".png");

            await Task.Run(async () =>
            {
                await ImageCodec.WriteAtomicAsync(path, png).ConfigureAwait(false);
                try
                {
                    await ImageCodec.CreateThumbnailAsync(image, thumbnailPath).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Thumbnail failed: {ex.Message}");
                }
            }).ConfigureAwait(true);

            if (File.Exists(thumbnailPath))
            {
                item.ThumbnailPath = thumbnailPath;
            }

            await Task.Run(() => app.History.Add(item));
            if (general.DescribeScreenshots && app.Descriptions is { } descriptions)
                _ = descriptions.EnqueueAsync(item);
            Log.Info($"{kind} {image.Width}x{image.Height}: encode {encodeMs} ms, clipboard {clipboardMs} ms, total {timer.ElapsedMilliseconds} ms -> {path}");
            return item;
        }
        catch (Exception ex)
        {
            Log.Error("Saving the screenshot failed", ex);
            Failed?.Invoke($"Saving the screenshot failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Registers a finished video/GIF file (recording or conversion) in history and the clipboard.</summary>
    public async Task<HistoryItem?> AddVideoAsync(string path, CaptureKind kind, int width, int height, TimeSpan duration,
        bool isTemporary, bool copyToClipboard = true, CaptureSource? source = null)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0)
            {
                throw new IOException("The output file is missing or empty.");
            }
            var item = new HistoryItem
            {
                Path = path,
                Kind = kind,
                Width = width,
                Height = height,
                DurationSeconds = duration.TotalSeconds,
                FileSize = info.Length,
                CreatedAt = source is null ? DateTimeOffset.Now : new DateTimeOffset(source.Timestamp),
                AppName = source?.AppName,
                WindowTitle = source?.WindowTitle,
                MonitorIndex = source?.MonitorIndex,
                IsTemporary = isTemporary,
            };

            if (copyToClipboard && app.Settings.General.CopyToClipboard)
            {
                if (!await ClipboardService.SetFilesAsync(app.MessageWindowHandle, [path]))
                {
                    Failed?.Invoke("Could not copy the recording to the clipboard.");
                }
            }

            var thumbnailPath = Path.Combine(app.Paths.ThumbnailsDirectory, item.Id + ".png");
            if (await Task.Run(() => ImageCodec.CreateThumbnailAsync(path, thumbnailPath)).ConfigureAwait(true))
            {
                item.ThumbnailPath = thumbnailPath;
            }

            await Task.Run(() => app.History.Add(item));
            Log.Info($"{kind} {width}x{height} {duration:mm\\:ss} -> {path}");
            return item;
        }
        catch (Exception ex)
        {
            Log.Error("Registering the video failed", ex);
            Failed?.Invoke($"Saving the {kind.DisplayName().ToLowerInvariant()} failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Picks the output file for a new capture using the user's naming pattern, or a temp file when
    /// "save to file" is off. Increments the persistent counter when the pattern uses it.
    /// </summary>
    public (string Path, bool IsTemporary) ReservePath(CaptureKind kind, CaptureSource source, int width, int height)
    {
        var settings = app.Settings;
        if (!settings.General.SaveToFile)
        {
            var name = $"{source.Timestamp:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid().ToString("N")[..6]}{kind.Extension()}";
            return (Path.Combine(AppPaths.TempDirectory, name), true);
        }

        var pattern = kind.IsVideo() ? settings.Naming.RecordingPattern : settings.Naming.ScreenshotPattern;
        var usesCounter = FileNameTemplate.UsesCounter(pattern);
        var counter = settings.General.Counter + 1;
        var context = new NamingContext
        {
            Timestamp = source.Timestamp,
            Kind = kind,
            AppName = source.AppName,
            WindowTitle = source.WindowTitle,
            MonitorIndex = source.MonitorIndex,
            Width = width,
            Height = height,
            Counter = counter,
            ComputerName = Environment.MachineName,
            UserName = Environment.UserName,
        };

        var path = FileNameTemplate.BuildPath(settings.General.SaveFolder, pattern, context, kind.Extension());
        if (usesCounter)
        {
            settings.General.Counter = counter;
            app.SaveSettings();
        }

        return (path, false);
    }
}
