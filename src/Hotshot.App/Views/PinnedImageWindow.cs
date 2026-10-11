using Hotshot.Editor;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Hotshot.Views;

internal sealed class PinnedImageWindow : Window
{
    public PinnedImageWindow(BitmapImage image)
    {
        Title = "Hotshot pinned image";
        var panel = new Grid();
        panel.Children.Add(new Image { Source = image, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform });
        Content = panel;
        WindowTools.Configure(this, 600, 450);
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = true;
        var escape = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, args) => { Close(); args.Handled = true; };
        panel.KeyboardAccelerators.Add(escape);
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
