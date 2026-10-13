using Hotshot.Capture;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hotshot.Views;

internal sealed class ScrollingCaptureWindow : Window
{
    private readonly TaskCompletionSource<CapturedImage?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ScrollingImage _image;
    private bool _busy;
    private bool _closeRequested;
    private readonly bool _includeCursor;

    public ScrollingCaptureWindow(PixelRect bounds, CapturedImage first, bool includeCursor = false)
    {
        _image = new ScrollingImage(first);
        _includeCursor = includeCursor;
        Title = "Hotshot scrolling capture";
        var status = new TextBlock { Text = "1 part captured.", TextWrapping = TextWrapping.Wrap };
        var next = new Button { Content = "Add next part" };
        var save = new Button { Content = "Save" };
        var cancel = new Button { Content = "Cancel" };
        var body = new StackPanel { Spacing = 12, Padding = new Thickness(20) };
        body.Children.Add(UiStyles.Description("Scroll down, keeping a quarter of the previous area visible. Don't move or resize the window. Avoid fixed headers."));
        body.Children.Add(status); body.Children.Add(next); body.Children.Add(save); body.Children.Add(cancel);
        next.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true; next.IsEnabled = save.IsEnabled = false;
            try
            {
                AppWindow.Hide();
                await Task.Delay(250);
                if (bounds.Intersect(Monitors.VirtualScreen) != bounds) throw new InvalidOperationException("The target region is no longer on screen.");
                CapturedImage frame;
                using (var screenshot = ScreenSnapshot.Take(bounds, _includeCursor)) frame = screenshot.ToImage();
                await Task.Run(() => _image.Append(frame));
                status.Text = $"{_image.FrameCount} parts captured.";
            }
            catch (Exception ex) { Log.Error("Scrolling capture failed", ex); status.Text = ex.Message; }
            finally
            {
                _busy = false;
                if (_closeRequested) Close();
                else { next.IsEnabled = save.IsEnabled = true; AppWindow.Show(); Activate(); }
            }
        };
        save.Click += (_, _) => { _completion.TrySetResult(_image.Image); Close(); };
        cancel.Click += (_, _) => Close();
        Closed += (_, _) => _completion.TrySetResult(null);
        AppWindow.Closing += (_, args) => { if (_busy) args.Cancel = true; };
        Content = body;
        WindowTools.Configure(this, 520, 350);
    }

    public Task<CapturedImage?> Completion => _completion.Task;
    public void RequestClose()
    {
        _closeRequested = true;
        if (!_busy) Close();
    }
}
