using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Hotshot.Views;

internal sealed class PinnedImageWindow : Window
{
    private NativePinnedInteraction? _interaction;
    private bool _updating;

    public PinnedImageWindow(BitmapImage image)
    {
        Title = "Hotshot pinned image";
        var panel = new Grid { RowSpacing = 8, Padding = new Thickness(8) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var preview = new Image { Source = image, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
        var opacity = new Slider { Minimum = 20, Maximum = 100, Value = 100, Width = 150, Header = "Window opacity" };
        var opacityLabel = new TextBlock { Text = "100%", VerticalAlignment = VerticalAlignment.Center };
        var through = new CheckBox { Content = "Click behind image (20 s)", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(opacity, "PinnedOpacity");
        AutomationProperties.SetAutomationId(through, "PinnedClickThrough");
        ToolTipService.SetToolTip(through, "Click Resume or wait 20 seconds to interact with this image again.");
        var close = new Button { Content = "Close", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(close, "PinnedClose");
        close.Click += (_, _) => Close();
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        controls.Children.Add(opacity); controls.Children.Add(opacityLabel); controls.Children.Add(through); controls.Children.Add(close);
        panel.Children.Add(new ScrollViewer { Content = controls, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Grid.SetRow(preview, 1); panel.Children.Add(preview);
        var status = new TextBlock { Text = "Stays on top • Esc closes • Not included in captures", TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(status, "PinnedStatus");
        Grid.SetRow(status, 2); panel.Children.Add(status);
        Content = panel;
        WindowTools.Configure(this, 600, 450);
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = true;
        var escape = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, args) => { Close(); args.Handled = true; };
        panel.KeyboardAccelerators.Add(escape);
        NativePinnedInteraction Interaction() => _interaction ??= new NativePinnedInteraction(
            WinRT.Interop.WindowNative.GetWindowHandle(this),
            () =>
            {
                _updating = true; through.IsChecked = false; _updating = false;
                Title = "Hotshot pinned image";
                status.Text = "Mouse restored • Esc closes • Not included in captures";
            },
            ex =>
            {
                Log.Error("Could not restore pinned image input; closing pin safely", ex);
                Close();
            });
        opacity.ValueChanged += (_, args) =>
        {
            try
            {
                Interaction().SetOpacity(args.NewValue);
                opacityLabel.Text = $"{args.NewValue:F0}%";
            }
            catch (Exception ex)
            {
                Log.Error("Pinned image opacity failed", ex);
                status.Text = "Could not change opacity.";
            }
        };
        through.Checked += (_, _) =>
        {
            if (_updating) return;
            try
            {
                Interaction().Enable();
                Title = "Hotshot pinned image — click-through (20 s)";
                status.Text = "Click behind the image. Click Resume or wait 20 seconds to stop.";
            }
            catch (Exception ex)
            {
                Log.Error("Pinned image click-through failed", ex);
                _updating = true; through.IsChecked = false; _updating = false;
                status.Text = "Could not enable clicking behind the image.";
            }
        };
        through.Unchecked += (_, _) => { if (!_updating) _interaction?.Restore(); };
        Closed += (_, _) => { _interaction?.Dispose(); _interaction = null; };
    }

    public static async Task<PinnedImageWindow> CreateAsync(byte[] png)
    {
        var image = new BitmapImage();
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        await image.SetSourceAsync(stream);
        return new PinnedImageWindow(image);
    }
}
