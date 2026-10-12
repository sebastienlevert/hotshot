using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Editing;
using Windows.Storage;

namespace Hotshot.Views;

internal static class VideoTrimDialog
{
    public static async Task<(TimeSpan Start, TimeSpan End)?> ShowAsync(string path, XamlRoot root)
    {
        var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(path));
        var duration = clip.OriginalDuration.TotalSeconds;
        using var player = new MediaPlayer { Source = MediaSource.CreateFromUri(new Uri(path)), AutoPlay = false };
        var preview = new MediaPlayerElement { AreTransportControlsEnabled = true, Height = 220 };
        preview.SetMediaPlayer(player);
        var start = new Slider { Header = "Trim start", Minimum = 0, Maximum = duration, Value = 0, StepFrequency = 0.1 };
        var end = new Slider { Header = "Trim end", Minimum = 0, Maximum = duration, Value = duration, StepFrequency = 0.1 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(start, "VideoTrimStart");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(end, "VideoTrimEnd");
        var summary = new TextBlock();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(summary, "VideoTrimSummary");
        void Update(bool seeking)
        {
            summary.Text = $"Start {TimeSpan.FromSeconds(start.Value):g} / End {TimeSpan.FromSeconds(end.Value):g} / Kept {Math.Max(0, end.Value - start.Value):F1}s";
            if (seeking) player.PlaybackSession.Position = TimeSpan.FromSeconds(start.Value);
        }
        start.ValueChanged += (_, _) => Update(true);
        end.ValueChanged += (_, _) => { Update(false); player.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, end.Value - 0.1)); };
        Update(false);
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(preview); body.Children.Add(start); body.Children.Add(end); body.Children.Add(summary);
        body.Children.Add(UiStyles.Description("Drag the start/end handles or use arrow keys. A separate MP4 is written; the original remains unchanged."));
        var dialog = new ContentDialog { Title = "Trim recording", Content = new ScrollViewer { Content = body, MaxHeight = 500 },
            PrimaryButtonText = "Choose destination", CloseButtonText = "Cancel", XamlRoot = root };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (end.Value <= start.Value) { args.Cancel = true; summary.Text = "Trim end must be after trim start."; }
        };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
            return (TimeSpan.FromSeconds(start.Value), TimeSpan.FromSeconds(end.Value));
        }
        finally { preview.SetMediaPlayer(null); }
    }
}
