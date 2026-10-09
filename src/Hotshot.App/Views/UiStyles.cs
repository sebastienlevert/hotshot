using Hotshot.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Hotshot.Views;

internal static class UiStyles
{
    public static void ApplyTheme(Window? window, AppTheme theme)
    {
        if (window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                AppTheme.Light => ElementTheme.Light,
                AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
    }

    public static Border Card(UIElement content)
    {
        var card = new Border
        {
            Child = content,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
        };
        void Update()
        {
            var dark = card.ActualTheme == ElementTheme.Dark;
            card.Background = new SolidColorBrush(dark ? Color.FromArgb(255, 43, 43, 43) : Color.FromArgb(255, 255, 255, 255));
            card.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 59, 59, 59) : Color.FromArgb(255, 229, 229, 229));
        }

        card.Loaded += (_, _) => Update();
        card.ActualThemeChanged += (_, _) => Update();
        Update();
        return card;
    }

    public static TextBlock Description(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.72,
        FontSize = 13,
    };
}
