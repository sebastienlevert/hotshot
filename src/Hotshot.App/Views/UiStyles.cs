using Hotshot.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.UI;
using Windows.Foundation;
using Hotshot.Interop;

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

    public static void BindTitleBarTheme(Window window)
    {
        if (window.Content is not FrameworkElement root || !AppWindowTitleBar.IsCustomizationSupported()) return;
        void Update()
        {
            var dark = root.ActualTheme == ElementTheme.Dark;
            var background = dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
            var foreground = dark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 24, 24, 24);
            var inactive = dark ? Color.FromArgb(255, 160, 160, 160) : Color.FromArgb(255, 110, 110, 110);
            var titleBar = window.AppWindow.TitleBar;
            var immersiveDark = dark ? 1 : 0;
            var result = Win32.DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(window),
                Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, in immersiveDark, sizeof(int));
            if (result < 0) Log.Warn($"Could not update title bar dark mode (0x{result:X8}).");
            titleBar.BackgroundColor = titleBar.InactiveBackgroundColor = background;
            titleBar.ForegroundColor = titleBar.ButtonForegroundColor = foreground;
            titleBar.InactiveForegroundColor = titleBar.ButtonInactiveForegroundColor = inactive;
            titleBar.ButtonBackgroundColor = titleBar.ButtonInactiveBackgroundColor = background;
            titleBar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(255, 55, 55, 55) : Color.FromArgb(255, 224, 224, 224);
            titleBar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(255, 70, 70, 70) : Color.FromArgb(255, 205, 205, 205);
            titleBar.ButtonHoverForegroundColor = titleBar.ButtonPressedForegroundColor = foreground;
        }

        RoutedEventHandler loaded = (_, _) => Update();
        TypedEventHandler<FrameworkElement, object> changed = (_, _) => Update();
        root.Loaded += loaded;
        root.ActualThemeChanged += changed;
        window.Closed += (_, _) => { root.Loaded -= loaded; root.ActualThemeChanged -= changed; };
        Update();
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
