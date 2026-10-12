using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Hotshot.Core.History;
using Hotshot.Core.Settings;

namespace Hotshot.Core.Descriptions;

/// <summary>Serializes description generation and coordinates metadata writes with editor saves.</summary>
public sealed class ScreenshotDescriptions : IAsyncDisposable
{
    private readonly HistoryStore _history;
    private readonly Func<ICaptureDescriptionProvider> _createProvider;
    public const int QueueCapacity = 32;
    private readonly Channel<Work> _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(QueueCapacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Dictionary<string, Work> _scheduled = new(StringComparer.Ordinal);
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
    private bool _reviewRequired;
    private bool _providerMaintenance;
    private string[] _excludedApps = [];

    public ScreenshotDescriptions(HistoryStore history, Func<ICaptureDescriptionProvider> createProvider, bool enabled)
    {
        _history = history;
        _createProvider = createProvider;
        _enabled = enabled;
        Status = enabled ? "Ready" : "Disabled";
        _worker = Task.Run(ProcessAsync);
    }

    public event Action<string>? Failed;
    public event Action? Changed;
    public bool IsBusy => Volatile.Read(ref _pending) != 0;
    public int PendingCount => Volatile.Read(ref _pending);
    public string? ActiveCaptureId { get; private set; }
    public string Status { get; private set; } = "Idle";
    public string ProviderStatus => _provider?.Status ?? "Copilot has not been contacted.";
    public IReadOnlyList<string> QueuedCaptureIds
    {
        get { lock (_state) return _scheduled.Keys.ToArray(); }
    }

    public void Cancel(string id)
    {
        lock (_state)
        {
            if (_scheduled.TryGetValue(id, out var work)) work.Cancelled = true;
            if (ActiveCaptureId == id) _active?.Cancel();
        }
        Changed?.Invoke();
    }

    public void ApplyPolicy(GeneralSettings settings)
    {
        lock (_state)
        {
            _reviewRequired = settings.ReviewBeforeAiUpload;
            _excludedApps = (settings.AiExcludedApps ?? []).ToArray();
            if (ActiveCaptureId is { } id && _scheduled.TryGetValue(id, out var work) && !PolicyAllows(work))
                _active?.Cancel();
        }
        Changed?.Invoke();
    }

    private bool PolicyAllows(Work work) => (work.Approved || !_reviewRequired) &&
        !_excludedApps.Any(name => string.Equals(Path.GetFileNameWithoutExtension(name.Trim()), work.AppName, StringComparison.OrdinalIgnoreCase));

    public void SetEnabled(bool enabled)
    {
        lock (_state)
        {
            if (_enabled == enabled) return;
            _enabled = enabled;
            _generation++;
            if (!enabled) _active?.Cancel();
        }
        Status = enabled ? "Ready" : "Disabled";
        Changed?.Invoke();
    }

    public Task<bool> EnqueueAsync(HistoryItem item, bool approved = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_state)
        {
            if (!_enabled || _providerMaintenance || item.IsVideo || _stop.IsCancellationRequested) return Task.FromResult(false);
            if (_scheduled.TryGetValue(item.Id, out var existing))
            {
                if (item.ImageRevision > existing.RequestedRevision)
                {
                    existing.RequestedRevision = item.ImageRevision;
                    existing.Followup ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                return existing.Followup?.Task ?? existing.Completion.Task;
            }
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var work = new Work(item.Id, item.Path, _generation, completion, item.AppName, approved) { RequestedRevision = item.ImageRevision };
            if (!PolicyAllows(work)) return Task.FromResult(false);
            _scheduled.Add(item.Id, work);
            Interlocked.Increment(ref _pending);
            if (!_queue.Writer.TryWrite(work))
            {
                _scheduled.Remove(item.Id);
                Interlocked.Decrement(ref _pending);
                ReportFailure(work, "The AI queue is full. Retry this capture after pending work finishes.");
                return completion.Task;
            }
            Status = $"{PendingCount} capture(s) pending";
            Changed?.Invoke();
            return completion.Task;
        }
    }

    public async Task<long> SaveEditedImageAsync(HistoryItem item, byte[] png, bool preserveMetadata = true)
    {
        await _files.WaitAsync();
        try
        {
            var description = !preserveMetadata ? null : item.Summary is { } summary && item.Description is { } text
                ? new CaptureDescription(summary, text)
                : PngDescriptionMetadata.Read(await File.ReadAllBytesAsync(item.Path));
            if (description is not null) png = PngDescriptionMetadata.Embed(png, description);
            await WriteAtomicAsync(item.Path, png, CancellationToken.None);
            _history.RecordImageChange(item.Id, clearDescription: !preserveMetadata);
            return png.LongLength;
        }
        finally { _files.Release(); }
    }

    public async Task RunFileOperationAsync(Func<Task> action)
    {
        await _files.WaitAsync();
        try { await action(); }
        finally { _files.Release(); }
    }

    public async Task RunProviderMaintenanceAsync(Func<Task> action)
    {
        ICaptureDescriptionProvider? provider;
        lock (_state)
        {
            if (_pending != 0 || _providerMaintenance || _disposed != 0)
                throw new InvalidOperationException("Wait for screenshot analysis to finish before changing its runtime.");
            _providerMaintenance = true;
            provider = _provider;
            _provider = null;
        }
        try
        {
            if (provider is not null) await provider.DisposeAsync();
            await action();
        }
        finally
        {
            lock (_state) _providerMaintenance = false;
            Changed?.Invoke();
        }
    }

    private bool MayProcess(Work work)
    {
        lock (_state) return _enabled && !work.Cancelled && PolicyAllows(work) && work.Generation == _generation && !_stop.IsCancellationRequested;
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
                    ActiveCaptureId = work.Id;
                    if (!_enabled || work.Generation != _generation) request.Cancel();
                }
                Status = "Describing screenshot";
                Changed?.Invoke();
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
                        work.ProcessedRevision = _history.Items.FirstOrDefault(item => item.Id == work.Id)?.ImageRevision ?? work.RequestedRevision;
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
                    if (_enabled && _reviewRequired && !work.Approved) _history.HoldDescriptionForReview(work.Id);
                }
                lock (_state)
                {
                    _active = null;
                    ActiveCaptureId = null;
                    _scheduled.Remove(work.Id);
                }
                request?.Dispose();
                Interlocked.Decrement(ref _pending);
                if (work.Followup is { } followup)
                {
                    if (!MayProcess(work) || !work.Completion.Task.IsCompletedSuccessfully || !work.Completion.Task.Result)
                        followup.TrySetResult(false);
                    else if (work.ProcessedRevision >= work.RequestedRevision)
                        followup.TrySetResult(true);
                    else
                    {
                        var item = _history.Items.FirstOrDefault(item => item.Id == work.Id);
                        if (item is null) followup.TrySetResult(false);
                        else _ = CompleteFollowupAsync(item, work.Approved, followup);
                    }
                }
                Status = !_enabled ? "Disabled" : IsBusy ? $"{PendingCount} capture(s) pending" : "Idle";
                Changed?.Invoke();
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

    private async Task CompleteFollowupAsync(HistoryItem item, bool approved, TaskCompletionSource<bool> completion)
    {
        try { completion.TrySetResult(await EnqueueAsync(item, approved)); }
        catch (Exception ex)
        {
            Failed?.Invoke("The updated screenshot could not be queued: " + ex.Message);
            completion.TrySetException(ex);
        }
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

    private sealed record Work(string Id, string Path, long Generation, TaskCompletionSource<bool> Completion, string? AppName, bool Approved)
    {
        public bool Cancelled { get; set; }
        public int RequestedRevision { get; set; }
        public int ProcessedRevision { get; set; } = -1;
        public TaskCompletionSource<bool>? Followup { get; set; }
    }
}
