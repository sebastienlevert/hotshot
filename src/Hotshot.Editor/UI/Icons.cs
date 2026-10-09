using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Hotshot.Editor.UI;

/// <summary>Segoe Fluent Icons glyphs and a few hand-drawn icons for tools without a good glyph.</summary>
internal static class Icons
{
    public const string FontFamilyName = "Segoe Fluent Icons,Segoe MDL2 Assets";

    public const string Crop = "\uE7A8";
    public const string Save = "\uE74E";
    public const string SaveAs = "\uE792";
    public const string Copy = "\uE8C8";
    public const string Undo = "\uE7A7";
    public const string Redo = "\uE7A6";
    public const string Delete = "\uE74D";
    public const string Highlight = "\uE7E6";
    public const string Pen = "\uED63";
    public const string Text = "\uE8D2";
    public const string Square = "\uE739";
    public const string Circle = "\uEA3A";
    public const string CircleFill = "\uEA3B";
    public const string Forward = "\uE72A";
    public const string Remove = "\uE738";
    public const string ZoomIn = "\uE8A3";
    public const string ZoomOut = "\uE71F";
    public const string Fit = "\uE9A6";
    public const string Background = "\uEF1F";
    public const string ClearCrop = "\uE894";
    public const string Accept = "\uE8FB";

    public static FontIcon Glyph(string glyph, double size = 16, double rotation = 0)
    {
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontFamily = new FontFamily(FontFamilyName),
            FontSize = size,
        };
        if (rotation != 0)
        {
            icon.RenderTransformOrigin = new Point(0.5, 0.5);
            icon.RenderTransform = new RotateTransform { Angle = rotation };
        }

        return icon;
    }

    /// <summary>Mouse-pointer arrow for the Select tool.</summary>
    public static ShapeIcon SelectPointer()
    {
        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = ParseGeometry("M 4,2 L 4,16.5 L 7.6,13.2 L 10.2,18.6 L 12.6,17.5 L 10,12.2 L 14.8,12.2 Z"),
            StrokeThickness = 1.3,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 18,
            Height = 20,
            Stretch = Stretch.None,
        };
        var icon = new ShapeIcon(20, 20);
        icon.AddStroked(path);
        return icon;
    }

    /// <summary>Numbered badge for the Step tool.</summary>
    public static ShapeIcon StepBadge()
    {
        var icon = new ShapeIcon(18, 18);
        icon.AddStroked(new Ellipse { StrokeThickness = 1.4 });
        icon.Children.Add(new TextBlock
        {
            Text = "1",
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 1),
        });
        return icon;
    }

    /// <summary>Mosaic icon for the pixelate (blur) tool.</summary>
    public static ShapeIcon Pixelate()
    {
        var icon = new ShapeIcon(18, 18);
        var opacities = new[] { 1, 0.35, 0.75, 0.45, 0.9, 0.3, 0.8, 0.4, 1 };
        for (var i = 0; i < 9; i++)
        {
            icon.AddFilled(new Rectangle
            {
                Width = 5,
                Height = 5,
                Opacity = opacities[i],
                RadiusX = 1,
                RadiusY = 1,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(i % 3 * 6.2, i / 3 * 6.2, 0, 0),
            });
        }

        return icon;
    }

    /// <summary>Horizontal bar showing a stroke thickness.</summary>
    public static ShapeIcon StrokeSample(double thickness)
    {
        var icon = new ShapeIcon(20, 20);
        icon.AddFilled(new Rectangle { Width = 18, Height = thickness, RadiusX = thickness / 2, RadiusY = thickness / 2, VerticalAlignment = VerticalAlignment.Center });
        return icon;
    }

    private static Geometry ParseGeometry(string data) =>
        (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), data);
}

/// <summary>Icon made of shapes. Shapes don't inherit Foreground, so the owner pushes the brush explicitly.</summary>
internal sealed class ShapeIcon : Grid
{
    private readonly List<Shape> _stroked = [];
    private readonly List<Shape> _filled = [];

    public ShapeIcon(double width, double height)
    {
        Width = width;
        Height = height;
    }

    public void AddStroked(Shape shape)
    {
        _stroked.Add(shape);
        Children.Add(shape);
    }

    public void AddFilled(Shape shape)
    {
        _filled.Add(shape);
        Children.Add(shape);
    }

    public void SetBrush(Brush brush)
    {
        foreach (var s in _stroked)
        {
            s.Stroke = brush;
        }

        foreach (var s in _filled)
        {
            s.Fill = brush;
        }

        foreach (var t in Children.OfType<TextBlock>())
        {
            t.Foreground = brush;
        }
    }
}