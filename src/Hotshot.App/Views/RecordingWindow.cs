using Hotshot.Recording;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hotshot.Views;

internal sealed class RecordingWindow : Window
{
    private readonly TextBlock _status = new() { FontSize = 20, TextWrapping = TextWrapping.Wrap };
    private readonly Button _pause = new() { Content = "Pause", IsEnabled = false };
    private readonly Button _stop = new() { Content = "Cancel" };
    private readonly DispatcherQueueTimer _timer;
    private ScreenRecorder? _recorder;
    private readonly ToggleSwitch _microphoneMute = new() { Header = "Mute microphone" };
    private readonly ToggleSwitch _systemMute = new() { Header = "Mute system audio" };
    private readonly ProgressBar _microphoneMeter = new() { Minimum = 0, Maximum = 1 };
    private readonly ProgressBar _systemMeter = new() { Minimum = 0, Maximum = 1 };

    public RecordingWindow(AppServices app, Func<Task> stop)
    {
        Title = "Hotshot recording";
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        var panel = new StackPanel { Padding = new Thickness(16), Spacing = 12 };
        panel.Children.Add(UiStyles.Card(_status));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _pause.Click += (_, _) =>
        {
            if (_recorder is not { } recorder)
            {
                return;
            }

            if (recorder.IsPaused) recorder.Resume(); else recorder.Pause();
            _pause.Content = recorder.IsPaused ? "Resume" : "Pause";
            Update();
        };
        _stop.Click += async (_, _) =>
        {
            _stop.IsEnabled = false;
            await stop();
        };
        buttons.Children.Add(_pause);
        buttons.Children.Add(_stop);
        panel.Children.Add(buttons);
        panel.Children.Add(_microphoneMute);
        panel.Children.Add(_microphoneMeter);
        panel.Children.Add(_systemMute);
        panel.Children.Add(_systemMeter);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_microphoneMeter, "Microphone audio level");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_systemMeter, "System audio level");
        _microphoneMute.Toggled += (_, _) => { if (_recorder is { } recorder) recorder.MicrophoneMuted = _microphoneMute.IsOn; };
        _systemMute.Toggled += (_, _) => { if (_recorder is { } recorder) recorder.SystemAudioMuted = _systemMute.IsOn; };
        Content = panel;
        UiStyles.ApplyTheme(this, app.Settings.General.Theme);
        _timer = app.Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => Update();
        Closed += async (_, _) =>
        {
            _timer.Stop();
            await stop();
        };
        WindowTools.Configure(this, 430, 430, compact: true);
        WindowTools.PlaceNearTray(this);
    }

    public void SetStatus(string status) => _status.Text = status;

    public void SetRecorder(ScreenRecorder recorder)
    {
        _recorder = recorder;
        _microphoneMute.IsEnabled = recorder.HasMicrophone;
        _systemMute.IsEnabled = recorder.HasSystemAudio;
        _pause.IsEnabled = true;
        _stop.Content = "Stop and save";
        _timer.Start();
        Update();
    }

    private void Update()
    {
        if (_recorder is { } recorder)
        {
            _status.Text = $"{(recorder.IsPaused ? "Paused" : "Recording")} {recorder.Elapsed:hh\\:mm\\:ss} - {recorder.Width} x {recorder.Height}";
            _microphoneMeter.Value = recorder.IsPaused ? 0 : recorder.MicrophonePeak;
            _systemMeter.Value = recorder.IsPaused ? 0 : recorder.SystemAudioPeak;
        }
    }
}
