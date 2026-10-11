using Hotshot.Capture;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Hotshot.Views;

internal sealed class ComparisonWindow : Window
{
    private ComparisonWindow(BitmapImage[] images, ImageComparisonResult result)
    {
        Title = "Hotshot visual comparison";
        var image = new Image { Source = images[2], Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
        var mode = new ComboBox { ItemsSource = new[] { "Before", "After", "Difference" }, SelectedIndex = 2 };
        mode.SelectionChanged += (_, _) => image.Source = images[Math.Max(0, mode.SelectedIndex)];
        var root = new Grid { Padding = new Thickness(16), RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(mode);
        var summary = new TextBlock { Text = $"{result.ChangedPixels:N0} changed pixels ({result.ChangedPercent:F2}%). Red highlights changes; tolerance is 8/255 per channel.", TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(summary, 1); root.Children.Add(summary);
        Grid.SetRow(image, 2); root.Children.Add(image);
        Content = root;
        WindowTools.Configure(this, 950, 700);
    }

    public static async Task<ComparisonWindow> CreateAsync(CapturedImage before, CapturedImage after, ImageComparisonResult result)
    {
        var images = new List<BitmapImage>();
        foreach (var image in new[] { before, after, result.Difference })
        {
            var png = await Task.Run(() => ImageCodec.EncodePngAsync(image.Downscale(2048)));
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream)) { writer.WriteBytes(png); await writer.StoreAsync(); writer.DetachStream(); }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            images.Add(bitmap);
        }
        return new ComparisonWindow(images.ToArray(), result);
    }
}
