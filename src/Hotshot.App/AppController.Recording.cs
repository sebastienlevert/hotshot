using Hotshot.Capture;
using Hotshot.CaptureFlow;
using Hotshot.Core;
using Hotshot.Core.History;
using Hotshot.Core.Settings;
using Hotshot.Gif;
using Hotshot.Recording;
using Hotshot.Views;

namespace Hotshot;

internal sealed partial class AppController
{
    private ScreenRecorder? _recorder;
    private RecordingWindow? _recordingWindow;
    private CancellationTokenSource? _startCancellation;
    private CancellationTokenSource? _conversionCancellation;
    private Task? _startTask;
    private Task? _stopTask;
    private Task? _conversionTask;
    private bool _recordingAsGif;
    private bool _recordingTemporary;
    private CaptureSource? _recordingSource;

    private bool IsRecording => _recorder is not null;

    private bool RecordingAvailable => RecordingSupport.IsSupported;

    private void InitializeRecording() => _actions.ConvertToGifHandler = ConvertToGifAsync;

    private async Task ToggleRecordingAsync(bool asGif)
    {
        if (_exiting) return;
        if (_recorder is not null)
        {
            await StopRecordingAsync();
            return;
        }

        if (_startTask is not null)
        {
            _startCancellation?.Cancel();
            return;
        }

        if (_stopTask is not null || _capture.IsBusy) return;
        if (!RecordingAvailable)
        {
            Notify("Recording unavailable", "Windows.Graphics.Capture is not supported on this system.", isError: true);
            return;
        }

        _startCancellation = new CancellationTokenSource();
        try
        {
            _startTask = StartRecordingAsync(asGif, _startCancellation.Token);
            await _startTask;
        }
        finally
        {
            _startTask = null;
            _startCancellation.Dispose();
            _startCancellation = null;
        }
    }

    private async Task StartRecordingAsync(bool asGif, CancellationToken token)
    {
        try
        {
            var (appName, title) = WindowEnumerator.GetForegroundInfo();
            var selection = await _capture.SelectRecordingTargetAsync();
            if (selection is null) return;
            token.ThrowIfCancellationRequested();

            var monitor = Monitors.FromRect(selection.Rect);
            RecordingTarget target;
            if (selection is WindowSelection window)
            {
                target = new WindowTarget(window.Window.Handle);
                appName = WindowEnumerator.GetProcessName(window.Window.ProcessId);
                title = window.Window.Title;
            }
            else
            {
                if (selection.Rect.Intersect(monitor.Bounds) != selection.Rect)
                {
                    throw new InvalidOperationException("Recording regions must fit within one monitor. Select a region on a single monitor.");
                }

                var crop = selection.Rect.Offset(-monitor.Bounds.X, -monitor.Bounds.Y);
                target = new MonitorTarget(monitor.Handle, new RectInt(crop.X, crop.Y, crop.Width, crop.Height));
            }

            var settings = _app.Settings.Recording;
            _recordingAsGif = asGif || settings.AlsoCreateGif;
            _recordingWindow = new RecordingWindow(_app, StopRecordingAsync);
            _recordingWindow.Activate();
            for (var seconds = settings.CountdownSeconds; seconds > 0; seconds--)
            {
                _recordingWindow.SetStatus($"Recording starts in {seconds}...");
                await Task.Delay(1000, token);
            }

            _recordingWindow.SetStatus("Starting recorder...");
            var source = CaptureSource.Now(appName, title, monitor.Index);
            _recordingSource = source;
            var (path, temporary) = _pipeline.ReservePath(CaptureKind.Recording, source, selection.Rect.Width, selection.Rect.Height);
            _recordingTemporary = temporary;
            var factor = settings.Quality switch
            {
                RecordingQuality.Standard => 0.06,
                RecordingQuality.Maximum => 0.2,
                _ => 0.1,
            };
            var bitrate = (int)Math.Clamp(selection.Rect.Width * (double)selection.Rect.Height * settings.FramesPerSecond * factor,
                2_000_000, 40_000_000);
            _recorder = await ScreenRecorder.StartAsync(new RecordingOptions
            {
                Target = target,
                OutputPath = path,
                FramesPerSecond = settings.FramesPerSecond,
                VideoBitrate = bitrate,
                CaptureCursor = settings.IncludeCursor,
                CaptureSystemAudio = !asGif && settings.CaptureSystemAudio,
                CaptureMicrophone = !asGif && settings.CaptureMicrophone,
                MicrophoneDeviceId = settings.MicrophoneDeviceId,
                Log = Log.Info,
            }, token);
            token.ThrowIfCancellationRequested();
            var activeRecorder = _recorder;
            activeRecorder.TargetClosed += (_, _) => _app.Post(() =>
            {
                if (ReferenceEquals(_recorder, activeRecorder)) _ = StopRecordingAsync();
            });
            activeRecorder.Failed += (_, error) => _app.Post(() =>
            {
                if (!ReferenceEquals(_recorder, activeRecorder)) return;
                Log.Error("Recording failed", error);
                Notify("Recording error", error.Message, isError: true);
                _ = StopRecordingAsync();
            });
            _recordingWindow.SetRecorder(_recorder);
            _tray.SetRecording(true, "Hotshot - recording (click to stop)");
            if (_recorder.Warnings.Count > 0)
            {
                Notify("Recording started with a warning", string.Join(Environment.NewLine, _recorder.Warnings), isError: true);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (_recorder is { } recorder)
            {
                await recorder.CancelAsync();
                await recorder.DisposeAsync();
                _recorder = null;
            }

            Log.Info("Recording startup cancelled.");
        }
        catch (Exception ex)
        {
            if (_recorder is { } recorder)
            {
                await recorder.CancelAsync();
                await recorder.DisposeAsync();
                _recorder = null;
            }

            Log.Error("Could not start recording", ex);
            Notify("Could not start recording", ex.Message, isError: true);
        }
        finally
        {
            if (_recorder is null)
            {
                _recordingWindow?.Close();
                _recordingWindow = null;
            }
        }
    }

    private async Task StopRecordingAsync()
    {
        if (_stopTask is not null)
        {
            await _stopTask;
            return;
        }

        if (_recorder is not { } recorder)
        {
            _startCancellation?.Cancel();
            return;
        }

        try
        {
            _stopTask = FinishRecordingAsync(recorder);
            await _stopTask;
        }
        finally
        {
            _stopTask = null;
        }
    }

    private async Task FinishRecordingAsync(ScreenRecorder recorder)
    {
        try
        {
            _recordingWindow?.SetStatus("Saving recording...");
            var result = await recorder.StopAsync();
            var item = await _pipeline.AddVideoAsync(result.Path, CaptureKind.Recording, result.Width, result.Height,
                result.Duration, _recordingTemporary, source: _recordingSource);
            if (item is not null)
            {
                if (_recordingAsGif && !_exiting) await ConvertToGifAsync(item);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not save recording", ex);
            Notify("Could not save recording", ex.Message, isError: true);
        }
        finally
        {
            await recorder.DisposeAsync();
            _recorder = null;
            _recordingWindow?.Close();
            _recordingWindow = null;
            _tray.SetRecording(false);
        }
    }

    private async Task ConvertToGifAsync(HistoryItem item)
    {
        if (_exiting) return;
        if (_conversionTask is not null)
        {
            Notify("GIF conversion", "A GIF conversion is already running.");
            return;
        }

        _conversionCancellation = new CancellationTokenSource();
        try
        {
            _conversionTask = ConvertToGifCoreAsync(item, _conversionCancellation.Token);
            await _conversionTask;
        }
        finally
        {
            _conversionTask = null;
            _conversionCancellation.Dispose();
            _conversionCancellation = null;
        }
    }

    private async Task ConvertToGifCoreAsync(HistoryItem item, CancellationToken token)
    {
        try
        {
            var settings = _app.Settings.Gif;
            var source = new CaptureSource(item.CreatedAt.LocalDateTime, item.AppName, item.WindowTitle, item.MonitorIndex);
            var (path, temporary) = _pipeline.ReservePath(CaptureKind.Gif, source, item.Width, item.Height);
            Notify("Creating GIF", $"Converting {item.FileName}. The MP4 will be kept.");
            var progress = new Progress<double>(value =>
            {
                _tray.SetTooltip($"Hotshot - GIF conversion {value:P0}");
                _recordingWindow?.SetStatus($"Creating GIF {value:P0}...");
            });
            var result = await GifConverter.ConvertAsync(item.Path, path, new GifOptions
            {
                FramesPerSecond = settings.FramesPerSecond,
                MaxWidth = settings.MaxWidth,
                Dither = settings.Dither,
                Loop = settings.Loop,
            }, progress, token);
            var converted = await _pipeline.AddVideoAsync(result.Path, CaptureKind.Gif, result.Width, result.Height,
                result.Duration, temporary, source: source);
            if (converted is not null && !_exiting)
            {
                Notify("GIF ready", converted.FileName, onClick: () => _actions.Open(converted));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Log.Info("GIF conversion cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error("GIF conversion failed", ex);
            Notify("GIF conversion failed", ex.Message, isError: true);
        }
        finally
        {
            _tray.SetTooltip(IsRecording ? "Hotshot - recording (click to stop)" : "Hotshot");
        }
    }

    private async Task ShutdownRecordingAsync()
    {
        _startCancellation?.Cancel();
        _conversionCancellation?.Cancel();
        if (_startTask is { } start) await start;
        await StopRecordingAsync();
        if (_conversionTask is { } conversion) await conversion;
    }
}
