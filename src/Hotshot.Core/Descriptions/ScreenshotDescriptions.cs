using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Hotshot.Core.History;

namespace Hotshot.Core.Descriptions;

/// <summary>Serializes description generation and coordinates metadata writes with editor saves.</summary>
public sealed class ScreenshotDescriptions : IAsyncDisposable
{
    private readonly HistoryStore _history;
    private readonly Func<ICaptureDescriptionProvider> _createProvider;
    private readonly Channel<Work> _queue = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _files = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _state = new();
    private readonly Task _worker;
    private ICaptureDescriptionProvider? _provider;
    private CancellationTokenSource? _active;
    private bool _enabled;
    private long _generation;
    private int _pending;
    private int _disposed;

    public ScreenshotDescriptions(HistoryStore history, Func<ICaptureDescriptionProvider> createProvider, bool enabled)
    {
        _history = history;
        _createProvider = createProvider;
        _enabled = enabled;
        _worker = Task.Run(ProcessAsync);
    }

    public event Action<string>? Failed;
    public bool IsBusy => Volatile.Read(ref _pending) != 0;

    public void SetEnabled(bool enabled)
    {
        lock (_state)
        {
            if (_enabled == enabled) return;
            _enabled = enabled;
            _generation++;
            if (!enabled) _active?.Cancel();
        }
    }

    public Task<bool> EnqueueAsync(HistoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_state)
        {
            if (!_enabled || item.IsVideo || _stop.IsCancellationRequested) return Task.FromResult(false);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Increment(ref _pending);
            if (!_queue.Writer.TryWrite(new Work(item.Id, item.Path, _generation, completion)))
            {
                Interlocked.Decrement(ref _pending);
                throw new InvalidOperationException("Screenshot description processing has stopped.");
            }
            return completion.Task;
        }
    }

    public async Task<long> SaveEditedImageAsync(HistoryItem item, byte[] png)
    {
        await _files.WaitAsync();
        try
        {
            var description = item.Summary is { } summary && item.Description is { } text
                ? new CaptureDescription(summary, text)
                : PngDescriptionMetadata.Read(await File.ReadAllBytesAsync(item.Path));
            if (description is not null) png = PngDescriptionMetadata.Embed(png, description);
            await WriteAtomicAsync(item.Path, png, CancellationToken.None);
            return png.LongLength;
        }
        finally { _files.Release(); }
    }

    private bool MayProcess(Work work)
    {
        lock (_state) return _enabled && work.Generation == _generation && !_stop.IsCancellationRequested;
    }

    private async Task ProcessAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync())
        {
            CancellationTokenSource? request = null;
            try
            {
                if (!MayProcess(work))
                {
                    work.Completion.SetResult(false);
                    continue;
                }
                _history.SetDescriptionPending(work.Id, true);
                request = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                request.CancelAfter(TimeSpan.FromMinutes(3));
                lock (_state)
                {
                    _active = request;
                    if (!_enabled || work.Generation != _generation) request.Cancel();
                }
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    byte[] png;
                    await _files.WaitAsync(request.Token);
                    try { png = await File.ReadAllBytesAsync(work.Path, request.Token); }
                    finally { _files.Release(); }
                    if (!MayProcess(work)) { work.Completion.SetResult(false); break; }
                    _provider ??= _createProvider();
                    var description = await _provider.DescribeAsync(png, request.Token);
                    await _files.WaitAsync(request.Token);
                    try
                    {
                        var current = await File.ReadAllBytesAsync(work.Path, request.Token);
                        if (!SHA256.HashData(png).AsSpan().SequenceEqual(SHA256.HashData(current)))
                        {
                            if (attempt == 1) throw new IOException("The screenshot changed repeatedly while Copilot was describing it.");
                            continue;
                        }
                        if (!MayProcess(work)) { work.Completion.SetResult(false); break; }
                        var annotated = PngDescriptionMetadata.Embed(current, description);
                        await WriteAtomicAsync(work.Path, annotated, request.Token);
                        _history.UpdateDescription(work.Id, description.Summary, description.Description, null, annotated.LongLength);
                        work.Completion.SetResult(true);
                        break;
                    }
                    finally { _files.Release(); }
                }
            }
            catch (OperationCanceledException)
            {
                if (!_stop.IsCancellationRequested && MayProcess(work))
                    ReportFailure(work, "Copilot took too long to describe this screenshot.");
                else
                {
                    work.Completion.TrySetResult(false);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or
                KeyNotFoundException or TimeoutException or UnauthorizedAccessException or ArgumentException)
            {
                ReportFailure(work, ex.Message);
            }
            finally
            {
                if (!_stop.IsCancellationRequested && !MayProcess(work))
                {
                    try { _history.SetDescriptionPending(work.Id, false); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Failed?.Invoke($"Screenshot description cancellation could not be saved: {ex.Message}");
                    }
                }
                lock (_state) _active = null;
                request?.Dispose();
                Interlocked.Decrement(ref _pending);
            }
        }
    }

    private void ReportFailure(Work work, string error)
    {
        try { _history.UpdateDescription(work.Id, null, null, error); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error += $" The failure could not be recorded in history: {ex.Message}";
        }
        try { Failed?.Invoke($"The screenshot was saved, but its AI description failed: {error}"); }
        finally { work.Completion.TrySetResult(false); }
    }

    private static async Task WriteAtomicAsync(string path, byte[] data, CancellationToken cancellationToken)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, data, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_state) { _enabled = false; _stop.Cancel(); }
        _queue.Writer.TryComplete();
        await _worker;
        if (_provider is not null) await _provider.DisposeAsync();
        _files.Dispose();
        _stop.Dispose();
    }

    private sealed record Work(string Id, string Path, long Generation, TaskCompletionSource<bool> Completion);
}
