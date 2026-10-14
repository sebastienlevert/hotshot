using Hotshot.Capture;
using Hotshot.Core.Settings;
using Hotshot.Recording;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Hotshot.Views;

internal sealed class RecordingWindow : Window
{
    private readonly AppServices _app;
    private readonly MonitorInfo _monitor;
    private readonly TextBlock _status = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.NoWrap,
    };
    private readonly Button _clock = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Thickness(4), MinWidth = 0, MinHeight = 0,
    };
    private readonly Button _pause = new() { Content = "Pause", IsEnabled = false };
    private readonly Button _stop = new() { Content = "Cancel" };
    private readonly ToggleSwitch _microphoneMute = new() { Header = "Mute microphone" };
    private readonly ToggleSwitch _systemMute = new() { Header = "Mute system audio" };
    private readonly ProgressBar _microphoneMeter = new() { Minimum = 0, Maximum = 1 };
    private readonly ProgressBar _systemMeter = new() { Minimum = 0, Maximum = 1 };
    private readonly DispatcherQueueTimer _timer;
    private ScreenRecorder? _recorder;
    private bool _showingTimer;
    private RecordingTimerSize? _size;
    private RecordingTimerPlacement? _placement;
    internal int? CountdownValue { get; private set; }

    public RecordingWindow(AppServices app, Func<Task> stop, MonitorInfo monitor)
    {
        _app = app;
        _monitor = monitor;
        Title = "Hotshot recording";
        _clock.Content = _status;
        AutomationProperties.SetAutomationId(_status, "RecordingStatus");
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        AutomationProperties.SetAutomationId(_clock, "RecordingTimer");
        AutomationProperties.SetName(_clock, "Recording timer. Click for controls.");
        ToolTipService.SetToolTip(_clock, "Click for recording controls");
        var controls = new StackPanel { Spacing = 12, MinWidth = 220 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _pause.Click += (_, _) =>
        {
            if (_recorder is not { } recorder) return;
            if (recorder.IsPaused) recorder.Resume(); else recorder.Pause();
            _pause.Content = recorder.IsPaused ? "Resume" : "Pause";
            Update();
            UiStyles.AnnounceStatus(_status);
        };
        _stop.Click += async (_, _) => { _stop.IsEnabled = false; await stop(); };
        buttons.Children.Add(_pause);
        buttons.Children.Add(_stop);
        controls.Children.Add(buttons);
        controls.Children.Add(_microphoneMute);
        controls.Children.Add(_microphoneMeter);
        controls.Children.Add(_systemMute);
        controls.Children.Add(_systemMeter);
        AutomationProperties.SetName(_microphoneMeter, "Microphone audio level");
        AutomationProperties.SetName(_systemMeter, "System audio level");
        _microphoneMute.Toggled += (_, _) => { if (_recorder is { } recorder) recorder.MicrophoneMuted = _microphoneMute.IsOn; };
        _systemMute.Toggled += (_, _) => { if (_recorder is { } recorder) recorder.SystemAudioMuted = _systemMute.IsOn; };
        _clock.Flyout = new Flyout { Content = controls };
        Content = new Grid { Children = { _clock } };
        UiStyles.ApplyTheme(this, app.Settings.General.Theme);
        var cancel = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        cancel.Invoked += (sender, args) =>
        {
            if (_showingTimer) return;
            args.Handled = true;
            _ = _app.RunObservedAsync(stop, "Could not cancel recording");
        };
        ((Grid)Content).KeyboardAccelerators.Add(cancel);
        _timer = app.Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => Update();
        Closed += async (_, _) => { _timer.Stop(); await stop(); };
        WindowTools.Configure(this, 140, 140, compact: true);
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.SetBorderAndTitleBar(false, false);
        var width = Math.Min(monitor.WorkArea.Width, (int)Math.Round(140 * monitor.Scale));
        var height = Math.Min(monitor.WorkArea.Height, (int)Math.Round(140 * monitor.Scale));
        AppWindow.MoveAndResize(new RectInt32(monitor.WorkArea.X + (monitor.WorkArea.Width - width) / 2,
            monitor.WorkArea.Y + (monitor.WorkArea.Height - height) / 2, width, height));
    }

    internal async Task CountdownAsync(int seconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var values = RecordingTimerLayout.Countdown(seconds);
        if (values.Count > 0)
        {
            _status.FontSize = 48;
            _status.Text = values[0].ToString(System.Globalization.CultureInfo.InvariantCulture);
            Activate();
            foreach (var value in values)
            {
                token.ThrowIfCancellationRequested();
                CountdownValue = value;
                _status.Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                UiStyles.AnnounceStatus(_status);
                await Task.Delay(value == 0 ? 150 : 1000, token);
            }
        }
        token.ThrowIfCancellationRequested();
        _clock.Flyout.Hide();
        AppWindow.Hide();
    }

    public void HideForSaving()
    {
        _timer.Stop();
        _clock.Flyout.Hide();
        AppWindow.Hide();
    }

    public void SetStatus(string status)
    {
        _status.Text = status;
        UiStyles.AnnounceStatus(_status);
    }

    public void SetRecorder(ScreenRecorder recorder)
    {
        _recorder = recorder;
        _microphoneMute.IsEnabled = recorder.HasMicrophone;
        _systemMute.IsEnabled = recorder.HasSystemAudio;
        _pause.IsEnabled = true;
        _stop.Content = "Stop and save";
        ShowTimer(recorder.Elapsed, recorder.IsPaused);
        _timer.Start();
        UiStyles.AnnounceStatus(_status);
    }

    internal void ShowTimer(TimeSpan elapsed, bool paused)
    {
        var show = !_showingTimer || !AppWindow.IsVisible;
        _showingTimer = true;
        CountdownValue = null;
        _status.Text = RecordingTimerLayout.Elapsed(elapsed);
        _status.Opacity = paused ? 0.6 : 1;
        ApplyTimerLayout();
        AutomationProperties.SetHelpText(_clock, paused ? "Paused. Click to resume or stop." : "Recording. Click to pause or stop.");
        if (show) AppWindow.Show(false);
    }

    private void ApplyTimerLayout()
    {
        var settings = _app.Settings.Recording;
        if (_size == settings.TimerSize && _placement == settings.TimerPlacement) return;
        _size = settings.TimerSize;
        _placement = settings.TimerPlacement;
        _status.FontSize = settings.TimerSize == RecordingTimerSize.Mini ? 14 : 22;
        var work = _monitor.WorkArea;
        var bounds = RecordingTimerLayout.Place(work.X, work.Y, work.Width, work.Height, _monitor.Scale,
            settings.TimerSize, settings.TimerPlacement);
        AppWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
    }

    private void Update()
    {
        if (_recorder is not { } recorder) return;
        ShowTimer(recorder.Elapsed, recorder.IsPaused);
        _microphoneMeter.Value = recorder.IsPaused ? 0 : recorder.MicrophonePeak;
        _systemMeter.Value = recorder.IsPaused ? 0 : recorder.SystemAudioPeak;
    }
}
