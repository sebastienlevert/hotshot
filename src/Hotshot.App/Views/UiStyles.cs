using Hotshot.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.UI;
using Windows.Foundation;
using Hotshot.Interop;
using System.Runtime.InteropServices;

namespace Hotshot.Views;

internal static class UiStyles
{
    internal static bool HighContrastNotificationsAvailable { get; private set; } = true;

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
        var colors = new Windows.UI.ViewManagement.UISettings();
        var accessibility = new Windows.UI.ViewManagement.AccessibilitySettings();
        var closed = false;
        void Update()
        {
            if (closed) return;
            var dark = root.ActualTheme == ElementTheme.Dark;
            var background = dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
            var foreground = dark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 24, 24, 24);
            if (accessibility.HighContrast)
            {
                background = colors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
                foreground = colors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground);
            }
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
            if (accessibility.HighContrast)
            {
                titleBar.InactiveForegroundColor = titleBar.ButtonInactiveForegroundColor = foreground;
                titleBar.ButtonHoverBackgroundColor = titleBar.ButtonPressedBackgroundColor = foreground;
                titleBar.ButtonHoverForegroundColor = titleBar.ButtonPressedForegroundColor = background;
            }
        }

        RoutedEventHandler loaded = (_, _) => Update();
        TypedEventHandler<FrameworkElement, object> changed = (_, _) => Update();
        root.Loaded += loaded;
        root.ActualThemeChanged += changed;
        TypedEventHandler<Windows.UI.ViewManagement.UISettings, object> colorsChanged = (_, _) => root.DispatcherQueue.TryEnqueue(Update);
        TypedEventHandler<Windows.UI.ViewManagement.AccessibilitySettings, object> contrastChanged = (_, _) => root.DispatcherQueue.TryEnqueue(Update);
        colors.ColorValuesChanged += colorsChanged;
        var contrastSubscribed = TrySubscribeContrast(accessibility, contrastChanged);
        window.Closed += (_, _) =>
        {
            closed = true;
            root.Loaded -= loaded;
            root.ActualThemeChanged -= changed;
            colors.ColorValuesChanged -= colorsChanged;
            if (contrastSubscribed) accessibility.HighContrastChanged -= contrastChanged;
        };
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
        var colors = new Windows.UI.ViewManagement.UISettings();
        var accessibility = new Windows.UI.ViewManagement.AccessibilitySettings();
        void Update()
        {
            if (accessibility.HighContrast)
            {
                card.Background = new SolidColorBrush(colors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background));
                card.BorderBrush = new SolidColorBrush(colors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground));
                return;
            }
            var dark = card.ActualTheme == ElementTheme.Dark;
            card.Background = new SolidColorBrush(dark ? Color.FromArgb(255, 43, 43, 43) : Color.FromArgb(255, 255, 255, 255));
            card.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 59, 59, 59) : Color.FromArgb(255, 229, 229, 229));
        }

        BindSystemAppearance(card, colors, accessibility, Update);
        Update();
        return card;
    }

    public static TextBlock Description(string text)
    {
        var description = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        var colors = new Windows.UI.ViewManagement.UISettings();
        var accessibility = new Windows.UI.ViewManagement.AccessibilitySettings();
        void Update() => description.Opacity = accessibility.HighContrast ? 1 : 0.72;
        BindSystemAppearance(description, colors, accessibility, Update);
        Update();
        return description;
    }

    public static void LiveStatus(TextBlock status, string automationId)
    {
        AutomationProperties.SetAutomationId(status, automationId);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        status.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
        {
            AnnounceStatus(status);
        });
    }

    public static void AnnounceStatus(TextBlock status)
    {
        if (status.IsLoaded)
            FrameworkElementAutomationPeer.CreatePeerForElement(status)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private static void BindSystemAppearance(FrameworkElement element,
        Windows.UI.ViewManagement.UISettings colors,
        Windows.UI.ViewManagement.AccessibilitySettings accessibility, Action update)
    {
        var subscribed = false;
        var contrastSubscribed = false;
        void QueueUpdate() => element.DispatcherQueue.TryEnqueue(() => { if (element.IsLoaded) update(); });
        TypedEventHandler<Windows.UI.ViewManagement.UISettings, object> colorsChanged = (_, _) => QueueUpdate();
        TypedEventHandler<Windows.UI.ViewManagement.AccessibilitySettings, object> contrastChanged = (_, _) => QueueUpdate();
        element.ActualThemeChanged += (_, _) => update();
        element.Loaded += (_, _) =>
        {
            if (!subscribed)
            {
                colors.ColorValuesChanged += colorsChanged;
                contrastSubscribed = TrySubscribeContrast(accessibility, contrastChanged);
                subscribed = true;
            }
            update();
        };
        element.Unloaded += (_, _) =>
        {
            colors.ColorValuesChanged -= colorsChanged;
            if (contrastSubscribed) accessibility.HighContrastChanged -= contrastChanged;
            subscribed = false;
            contrastSubscribed = false;
        };
    }

    private static bool TrySubscribeContrast(Windows.UI.ViewManagement.AccessibilitySettings accessibility,
        TypedEventHandler<Windows.UI.ViewManagement.AccessibilitySettings, object> changed)
    {
        try
        {
            accessibility.HighContrastChanged += changed;
            return true;
        }
        catch (COMException)
        {
            // Some unpackaged desktops expose the palette but not this event; keep color/theme updates active.
            HighContrastNotificationsAvailable = false;
            return false;
        }
    }
}
