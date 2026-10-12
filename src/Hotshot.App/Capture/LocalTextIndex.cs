using Hotshot.Capture;
using Hotshot.Core.History;

namespace Hotshot.CaptureFlow;

internal sealed class LocalTextIndex(AppServices app) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _state = new();
    private readonly HashSet<Task> _tasks = [];
    private Task? _disposal;
    public bool IsBusy { get { lock (_state) return _tasks.Count > 0; } }
    public event Action<string>? Failed;

    public void Enqueue(HistoryItem item, byte[] png)
    {
        lock (_state)
        {
            if (_disposal is not null || _stop.IsCancellationRequested) return;
            var revision = item.ImageRevision;
            var task = Task.Run(() => IndexAsync(item, png, revision));
            _tasks.Add(task);
            _ = ObserveAsync(task);
        }
    }

    private async Task ObserveAsync(Task task)
    {
        try { await task; }
        finally { lock (_state) _tasks.Remove(task); }
    }

    private async Task IndexAsync(HistoryItem item, byte[] png, int revision)
    {
        try
        {
            await _gate.WaitAsync(_stop.Token);
            try
            {
                if (!app.Settings.General.RecognizeScreenshotText) return;
                var text = await TextRecognition.RecognizeAsync(png, _stop.Token);
                if (app.Settings.General.RecognizeScreenshotText)
                    app.History.UpdateRecognizedText(item.Id, text, null, revision);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Error("Local screenshot text recognition failed", ex);
            try { app.History.UpdateRecognizedText(item.Id, null, ex.Message, revision); }
            catch (Exception historyError) when (historyError is IOException or UnauthorizedAccessException)
            {
                Log.Error("Could not save OCR failure", historyError);
            }
            app.Post(() => Failed?.Invoke($"The screenshot was saved, but local text recognition failed: {ex.Message}"));
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_state) return new ValueTask(_disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _stop.Cancel();
        Task[] tasks;
        lock (_state) tasks = [.. _tasks];
        try { await Task.WhenAll(tasks); }
        finally { _gate.Dispose(); _stop.Dispose(); }
    }
}
