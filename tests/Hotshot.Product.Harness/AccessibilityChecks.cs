using System.Runtime.InteropServices;
using System.Text.Json;
using Hotshot.Editor;
using Hotshot.Editor.Model;
using Hotshot.Editor.UI;
using Hotshot.Views;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Input;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Hotshot.ProductHarness;

/// <summary>Generated-only native checks; UIA invocation is not human screen-reader acceptance.</summary>
internal static class AccessibilityChecks
{
    private static readonly JsonSerializerOptions EvidenceOptions = new() { WriteIndented = true };

    public static async Task RunAsync(string outputDirectory)
    {
        if (!Path.IsPathFullyQualified(outputDirectory))
            throw new ArgumentException("Accessibility evidence requires an absolute task-owned output directory.", nameof(outputDirectory));
        Directory.CreateDirectory(outputDirectory);
        var fixture = Path.Combine(outputDirectory, "accessibility-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var passed = new List<string>();
        string? failure = null;
        using var editor = new EditorView();
        var editorRoot = (Grid)editor.Content;
        var commands = editorRoot.Children.OfType<CommandBar>().Single();
        var canvas = editorRoot.Children.OfType<CanvasControl>().Single();
        canvas.ForceSoftwareRenderer = true;
        var tools = CommandControl<ComboBox>(commands, "EditorTool");
        var color = CommandControl<DropDownButton>(commands, "EditorColor");
        var stroke = CommandControl<ComboBox>(commands, "EditorStroke");
        var preview = CommandControl<ToolPreview>(commands, "EditorToolPreview");
        preview.Canvas.ForceSoftwareRenderer = true;
        var layers = CommandControl<ComboBox>(commands, "EditorLayers");
        var text = CommandControl<TextBox>(commands, "EditorText");
        var fontSize = CommandControl<NumberBox>(commands, "EditorFontSize");
        var status = Find<TextBlock>(editorRoot, "EditorStatus");
        var zoom = Find<TextBlock>(editorRoot, "EditorZoomStatus");
        var undo = commands.PrimaryCommands.OfType<AppBarButton>().Single(button => button.Label == "Undo");
        var redo = commands.PrimaryCommands.OfType<AppBarButton>().Single(button => button.Label == "Redo");
        var description = UiStyles.Description("Synthetic accessibility fixture. No user captures or private data.");
        var card = UiStyles.Card(description);
        var fixtureStatus = UiStyles.Description("Fixture ready.");
        UiStyles.LiveStatus(fixtureStatus, "FixtureLiveStatus");
        var root = new Grid { RowSpacing = 8, Padding = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(card);
        Grid.SetRow(editor, 1);
        root.Children.Add(editor);
        Grid.SetRow(fixtureStatus, 2);
        root.Children.Add(fixtureStatus);
        var window = new Window { Title = "Hotshot generated accessibility checks", Content = root };
        var accessibility = new AccessibilitySettings();
        var uiSettings = new UISettings();
        try
        {
            Resize(window, root, 1100, 800);
            window.Activate();
            await Task.Delay(200);
            var image = Path.Combine(fixture, "image.png");
            using (var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(forceSoftwareRenderer: true), 640, 480, 96))
            {
                using (var drawing = target.CreateDrawingSession())
                {
                    drawing.Clear(Color.FromArgb(255, 245, 245, 245));
                    drawing.FillRectangle(40, 40, 160, 80, Color.FromArgb(255, 30, 90, 150));
                }
                await target.SaveAsync(image, CanvasBitmapFileFormat.Png);
            }
            var options = new EditorOptions
            {
                ImagePath = image,
                OriginalBackupPath = Path.Combine(fixture, "original.png"),
                AnnotationsPath = Path.Combine(fixture, "annotations.json"),
            };
            await editor.LoadAsync(options);
            tools.SelectedItem = EditorTool.Text;
            commands.IsOpen = true;
            await Task.Delay(150);

            RequireRole(tools, "Annotation tool", AutomationControlType.ComboBox);
            RequireRole(layers, "Annotation layers", AutomationControlType.ComboBox);
            RequireRole(text, "Annotation text", AutomationControlType.Edit);
            RequireRole(undo, "Undo", AutomationControlType.Button);
            Require(Peer(canvas).GetName() == "Annotation canvas" && canvas.IsTabStop && canvas.UseSystemFocusVisuals &&
                canvas.Focus(FocusState.Keyboard), "The named canvas must accept native keyboard focus.");
            Require(ReferenceEquals(FocusManager.GetFocusedElement(root.XamlRoot), canvas), "Canvas focus did not stick.");
            Require(Peer(canvas).GetHelpText().Contains("Insert", StringComparison.Ordinal) &&
                AutomationProperties.GetDescribedBy(canvas).Contains(status) &&
                AutomationProperties.GetControlledPeers(layers).Contains(canvas), "Canvas instructions or relationships are missing.");
            foreach (var button in commands.PrimaryCommands.Concat(commands.SecondaryCommands).OfType<AppBarButton>())
            {
                Require(Peer(button).GetName() == button.Label, "An editor icon button lost its accessible name.");
                Require(Peer(button).GetPattern(PatternInterface.Invoke) is IInvokeProvider, "An editor icon button lost its native Invoke pattern.");
            }
            Pass("Native UIA editor control roles, icon names/Invoke patterns, canvas help and relationships");

            tools.SelectedItem = EditorTool.Rectangle;
            Require(editor.ProcessCanvasKey(VirtualKey.Insert, VirtualKeyModifiers.None), "Insert did not add an annotation.");
            var inserted = await ReadDocumentAsync();
            var rectangle = inserted.Annotations.OfType<RectangleAnnotation>().Single().Rect;
            var layerItem = layers.Items[0];
            Require(editor.ProcessCanvasKey(VirtualKey.Right, VirtualKeyModifiers.Shift), "Shift+Right did not move the annotation.");
            Require(editor.ProcessCanvasKey(VirtualKey.Down, VirtualKeyModifiers.Control), "Ctrl+Down did not resize the annotation.");
            var moved = (await ReadDocumentAsync()).Annotations.OfType<RectangleAnnotation>().Single().Rect;
            Require(moved.X == rectangle.X + 10 && moved.Height == rectangle.Height + 1 &&
                ReferenceEquals(layerItem, layers.Items[0]), "Keyboard geometry or stable native layer items regressed.");
            await InvokeAsync(undo);
            Require((await ReadDocumentAsync()).Annotations.OfType<RectangleAnnotation>().Single().Rect.Height == rectangle.Height,
                "Native Undo did not reverse the keyboard resize.");
            await InvokeAsync(redo);
            Require((await ReadDocumentAsync()).Annotations.OfType<RectangleAnnotation>().Single().Rect.Height == moved.Height,
                "Native Redo did not restore the keyboard resize.");
            Pass("Shared canvas KeyDown handler: Insert, move/resize, stable layers; real UIA Undo/Redo");

            commands.IsOpen = true;
            await Task.Delay(100);
            tools.SelectedItem = EditorTool.Text;
            await Task.Delay(100);
            Require(text.Focus(FocusState.Keyboard), "Annotation text cannot receive keyboard focus.");
            Require(!editor.CanInvokeShortcut(VirtualKeyModifiers.None) &&
                !editor.CanInvokeShortcut(VirtualKeyModifiers.Control), "Editor shortcuts must not consume text editing.");
            tools.SelectedItem = EditorTool.Arrow;
            Require(ReferenceEquals(FocusManager.GetFocusedElement(root.XamlRoot), canvas),
                "Hiding the focused text field did not recover canvas focus.");
            Require(tools.Focus(FocusState.Keyboard) && !editor.CanInvokeShortcut(VirtualKeyModifiers.None),
                "Single-letter canvas shortcuts must not hijack a native ComboBox.");
            Require(canvas.Focus(FocusState.Keyboard) && editor.CanInvokeShortcut(VirtualKeyModifiers.None),
                "Canvas tool shortcuts stopped being available.");
            Pass("Native focus recovery and editable-control/ComboBox shortcut guards");

            tools.SelectedItem = EditorTool.Crop;
            Require(editor.ProcessCanvasKey(VirtualKey.Insert, VirtualKeyModifiers.None), "Keyboard crop creation failed.");
            var initialCrop = (await ReadDocumentAsync()).Crop ?? throw new InvalidDataException("Crop was not persisted.");
            editor.ProcessCanvasKey(VirtualKey.Right, VirtualKeyModifiers.Shift);
            editor.ProcessCanvasKey(VirtualKey.Down, VirtualKeyModifiers.Control);
            var editedCrop = (await ReadDocumentAsync()).Crop ?? throw new InvalidDataException("Crop was lost.");
            Require(editedCrop.X == initialCrop.X + 10 && editedCrop.Height == initialCrop.Height + 1,
                "Keyboard crop movement/resizing failed.");
            editor.ProcessCanvasKey(VirtualKey.Add, VirtualKeyModifiers.None);
            await Task.Delay(100);
            var zoomed = zoom.Text;
            editor.ProcessCanvasKey(VirtualKey.Subtract, VirtualKeyModifiers.None);
            await Task.Delay(100);
            Require(zoom.Text != zoomed && editor.ProcessCanvasKey(VirtualKey.Right, VirtualKeyModifiers.Menu),
                "Keyboard zoom or pan failed.");
            Require(editor.ProcessCanvasKey(VirtualKey.PageDown, VirtualKeyModifiers.None) &&
                layers.SelectedItem is not null && editor.ProcessCanvasKey(VirtualKey.Escape, VirtualKeyModifiers.None) &&
                layers.SelectedItem is null, "Keyboard annotation selection or Escape failed.");
            Pass("Shared canvas KeyDown handler: crop creation/move/resize, zoom/pan, selection and Escape");

            var livePeer = Peer(fixtureStatus);
            Require(livePeer.GetLiveSetting() == AutomationLiveSetting.Polite &&
                Peer(status).GetLiveSetting() == AutomationLiveSetting.Polite, "Status peers must be polite live regions.");
            fixtureStatus.Text = "Generated status update.";
            string? reportedError = null;
            editor.Failed += message => reportedError = message;
            await editor.LoadAsync(new EditorOptions
            {
                ImagePath = options.ImagePath,
                OriginalBackupPath = options.OriginalBackupPath,
                AnnotationsPath = options.AnnotationsPath,
                SaveImageAsync = (_, _) => throw new IOException("Synthetic save rejection."),
            });
            Require(!await editor.SaveAsync() && reportedError?.Contains("Synthetic save rejection.", StringComparison.Ordinal) == true &&
                status.Text.Contains("Synthetic save rejection.", StringComparison.Ordinal), "A failed edit did not expose a native error status.");
            editor.SetHostBusy(true);
            Require(!editor.IsEnabled && !editor.CanInvokeShortcut(VirtualKeyModifiers.Control) &&
                !editor.ProcessCanvasKey(VirtualKey.Insert, VirtualKeyModifiers.None), "Busy editor still accepts mutation commands.");
            editor.SetHostBusy(false);
            Require(editor.IsEnabled, "Busy completion did not re-enable the editor.");
            Pass("Polite native live-region peers, generated save failure and busy command guards");

            Color? lightCardColor = null;
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = theme;
                foreach (var width in new[] { 1100, 520 })
                {
                    Resize(window, root, width, 800);
                    tools.FontSize = width == 520 ? 26 : 14;
                    text.FontSize = width == 520 ? 26 : 14;
                    fontSize.FontSize = width == 520 ? 26 : 14;
                    description.FontSize = width == 520 ? 26 : 13;
                    tools.SelectedItem = EditorTool.Text;
                    commands.IsOpen = true;
                    await Task.Delay(180);
                    Require(tools.ActualHeight > 0 && tools.ActualWidth > 0 &&
                        !Peer(tools).IsOffscreen() && tools.Focus(FocusState.Keyboard), "Scaled tool template is hidden or cannot be focused.");
                    // Focusing a primary command can dismiss native overflow; reopen it before its text command.
                    commands.IsOpen = true;
                    await Task.Delay(100);
                    Require(text.Visibility == Visibility.Visible && text.Focus(FocusState.Keyboard) &&
                        !Peer(text).IsOffscreen(), $"Scaled annotation text template is hidden or cannot be focused ({theme}, {width} DIPs, overflow={commands.IsOpen}, text={text.ActualWidth:F0}x{text.ActualHeight:F0}).");
                    Require(description.IsTextScaleFactorEnabled && description.TextWrapping == TextWrapping.Wrap &&
                        description.ActualHeight > 0 && description.ActualWidth <= root.ActualWidth,
                        "Description text lost scale support or layout containment.");
                    var contrast = accessibility.HighContrast;
                    var opacityMatches = contrast
                        ? description.Opacity == 1 && status.Opacity == 1
                        : Math.Abs(description.Opacity - 0.72) < 0.000001 && Math.Abs(status.Opacity - 0.75) < 0.000001;
                    Require(opacityMatches, $"Description/status contrast opacity is incorrect (highContrast={contrast}, description={description.Opacity:R}, status={status.Opacity:R}).");
                    Require(canvas.ActualHeight > 0, "Large local fonts consumed the entire annotation canvas.");
                    if (card.Background is not SolidColorBrush background || card.BorderBrush is not SolidColorBrush border)
                        throw new InvalidDataException("Native card brushes are unavailable.");
                    if (accessibility.HighContrast)
                        Require(background.Color == uiSettings.GetColorValue(UIColorType.Background) &&
                            border.Color == uiSettings.GetColorValue(UIColorType.Foreground), "Active high-contrast palette was not honored.");
                    else if (theme == ElementTheme.Light) lightCardColor = background.Color;
                    else Require(background.Color != lightCardColor, "Card did not update with the live element theme.");
                }
            }
            Pass("Live native light/dark themes, current contrast palette, compact templates and local large-font layout");

            commands.IsOpen = false;
            Resize(window, root, 1100, 800);
            tools.SelectedItem = EditorTool.Arrow;
            await Task.Delay(100);
            Require(tools.Focus(FocusState.Keyboard), "Tool combo could not receive focus before native navigation.");
            Require(FocusManager.TryMoveFocus(FocusNavigationDirection.Next,
                new FindNextElementOptions { SearchRoot = root }), "Native next-focus navigation failed.");
            Require(FocusManager.GetFocusedElement(root.XamlRoot) is Control { Visibility: Visibility.Visible, IsEnabled: true },
                "Native focus navigation reached an unavailable control.");
            Pass("Native FocusManager next-focus traversal");

            tools.FontSize = 14;
            tools.SelectedItem = EditorTool.Arrow;
            commands.IsOpen = false;
            await Task.Delay(150);
            var controls = new FrameworkElement[] { tools, color, stroke, preview };
            var previousRight = double.NegativeInfinity;
            var buttonCenter = undo.TransformToVisual(commands).TransformPoint(new Windows.Foundation.Point(0, undo.ActualHeight / 2)).Y;
            foreach (var control in controls)
            {
                var bounds = control.TransformToVisual(commands).TransformBounds(new Windows.Foundation.Rect(0, 0, control.ActualWidth, control.ActualHeight));
                Require(control.ActualWidth > 0 && bounds.Left - previousRight >= 7 &&
                    Math.Abs(bounds.Y + bounds.Height / 2 - buttonCenter) <= 1,
                    $"Editor control spacing/alignment is wrong: {AutomationProperties.GetAutomationId(control)}, bounds={bounds}, button center={buttonCenter}.");
                previousRight = bounds.Right;
            }
            RequireRole(color, "Annotation color", AutomationControlType.Button);
            color.Flyout.ShowAt(color);
            await Task.Delay(200);
            var picker = VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
                .Select(popup => TryFind<ColorPicker>(popup.Child, "EditorColorPicker")).FirstOrDefault(found => found is not null)
                ?? throw new InvalidDataException("The full color picker did not open.");
            Require(picker.IsAlphaEnabled && picker.IsAlphaSliderVisible && picker.IsHexInputVisible &&
                picker.IsColorChannelTextInputVisible, "Full RGB, hex and transparency controls must be available.");
            picker.Color = Color.FromArgb(73, 7, 133, 215);
            stroke.SelectedIndex = 2;
            color.Flyout.Hide();
            Require(preview.Annotation is ArrowAnnotation { StrokeWidth: 10 } custom &&
                custom.Color == new RgbaColor(7, 133, 215, 73), "The preview did not reflect the custom RGBA color and thick stroke.");
            Require(editor.ProcessCanvasKey(VirtualKey.Insert, VirtualKeyModifiers.None), "Custom-colored arrow insertion failed.");
            var colored = (await ReadDocumentAsync()).Annotations.OfType<ArrowAnnotation>().Last();
            Require(colored.Color == new RgbaColor(7, 133, 215, 73) && colored.StrokeWidth == 10,
                "The real annotation/save did not retain the full color and stroke.");
            tools.SelectedItem = EditorTool.Arrow;
            picker.Color = Color.FromArgb(255, RgbaColor.Green.R, RgbaColor.Green.G, RgbaColor.Green.B);
            using (var target = new CanvasRenderTarget(canvas.Device, 112, 40, 96))
            {
                using (var drawing = target.CreateDrawingSession())
                {
                    drawing.Clear(default(Color));
                    preview.Render(drawing, canvas.Device);
                }
                var thickPixels = target.GetPixelBytes();
                var thick = GreenPixels(thickPixels);
                await target.SaveAsync(Path.Combine(outputDirectory, "editor-green-thick-preview.png"), CanvasBitmapFileFormat.Png);
                stroke.SelectedIndex = 0;
                using (var drawing = target.CreateDrawingSession())
                {
                    drawing.Clear(default(Color));
                    preview.Render(drawing, canvas.Device);
                }
                Require(thick > GreenPixels(target.GetPixelBytes()) * 1.3,
                    "The thick green preview must visibly differ from the thin preview.");
            }
            editor.UpdateCanvasPointer(true);
            Require(editor.EditingCursorShape == InputSystemCursorShape.Cross, "Drawing must use an editing crosshair.");
            tools.SelectedItem = EditorTool.Text;
            Require(editor.EditingCursorShape == InputSystemCursorShape.IBeam, "Text must use its editing cursor.");
            tools.SelectedItem = EditorTool.Select;
            Require(editor.EditingCursorShape == InputSystemCursorShape.SizeAll, "Selection must use the move cursor.");
            editor.SetHostBusy(true);
            Require(editor.EditingCursorShape == InputSystemCursorShape.Wait, "Busy editing must use the wait cursor.");
            editor.SetHostBusy(false);
            editor.UpdateCanvasPointer(false);
            Require(editor.EditingCursorShape is null, "Leaving the canvas must restore the normal pointer.");
            Pass("Spaced/centered editor controls, full native color picker, RGBA save, rendered thick green preview and scoped editing cursors");

            async Task<AnnotationDocument> ReadDocumentAsync()
            {
                Require(await editor.SaveAsync(), "Generated annotation sidecar could not be saved.");
                return AnnotationDocument.FromJson(await File.ReadAllTextAsync(options.AnnotationsPath));
            }

            static int GreenPixels(byte[] pixels)
            {
                var count = 0;
                for (var index = 0; index < pixels.Length; index += 4)
                    if (pixels[index + 1] > pixels[index] + 35 && pixels[index + 1] > pixels[index + 2] + 35) count++;
                return count;
            }
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            throw;
        }
        finally
        {
            window.Close();
            var evidence = new
            {
                schemaVersion = 1,
                collectedAt = DateTimeOffset.UtcNow,
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                windowsBuild = Environment.OSVersion.Version.ToString(),
                fixtureSoftwareRenderer = canvas.ForceSoftwareRenderer,
                highContrastActive = accessibility.HighContrast,
                directHighContrastNotificationsAvailable = UiStyles.HighContrastNotificationsAvailable &&
                    editor.HighContrastNotificationsAvailable,
                unavailableCapabilities = UiStyles.HighContrastNotificationsAvailable && editor.HighContrastNotificationsAvailable
                    ? Array.Empty<string>()
                    : ["Direct HighContrastChanged event registration; system color and element theme notifications remain active. Contrast-setting transitions are not verified."],
                systemTextScaleFactor = uiSettings.TextScaleFactor,
                passed,
                failure,
                notVerified = new[]
                {
                    "Human Narrator speech/navigation and external UIA client event delivery",
                    "Physical keyboard event delivery (shared production KeyDown handler is called directly)",
                    "System contrast/text-scale transitions (settings unchanged; current values read only)",
                    "Physical ARM64 and mixed-DPI/HDR displays",
                },
                privacy = "Generated fixture only. No desktop capture, clipboard, audio, user profile, OneDrive or provider access.",
            };
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "accessibility-evidence.json"),
                JsonSerializer.Serialize(evidence, EvidenceOptions));
            Directory.Delete(fixture, recursive: true);
        }

        void Pass(string check)
        {
            passed.Add(check);
            Console.WriteLine("PASS accessibility: " + check);
        }
    }

    private static void Resize(Window window, FrameworkElement root, int width, int height)
    {
        var scale = root.XamlRoot?.RasterizationScale ?? 1;
        window.AppWindow.Resize(new SizeInt32((int)(width * scale), (int)(height * scale)));
    }

    private static T CommandControl<T>(CommandBar commands, string id) where T : FrameworkElement =>
        commands.PrimaryCommands.Concat(commands.SecondaryCommands).OfType<AppBarElementContainer>()
            .Select(command => command.Content is T direct ? direct :
                command.Content is Panel panel ? panel.Children.OfType<T>().SingleOrDefault() : null)
            .Single(control => control is not null && AutomationProperties.GetAutomationId(control) == id)
            ?? throw new InvalidOperationException("Missing editor command: " + id);

    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement
    {
        if (root is T element && AutomationProperties.GetAutomationId(element) == id) return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (TryFind<T>(child, id) is { } found) return found;
        }
        throw new InvalidOperationException("Missing native fixture control: " + id);
    }

    private static T? TryFind<T>(DependencyObject root, string id) where T : FrameworkElement
    {
        if (root is T element && AutomationProperties.GetAutomationId(element) == id) return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (TryFind<T>(VisualTreeHelper.GetChild(root, index), id) is { } found) return found;
        return null;
    }

    private static AutomationPeer Peer(FrameworkElement element) =>
        FrameworkElementAutomationPeer.CreatePeerForElement(element) ??
        throw new InvalidOperationException("Native automation peer is missing: " + AutomationProperties.GetAutomationId(element));

    private static void RequireRole(FrameworkElement control, string name, AutomationControlType role) =>
        Require(Peer(control).GetName() == name && Peer(control).GetAutomationControlType() == role,
            "Incorrect native UIA name or role: " + name);

    private static async Task InvokeAsync(AppBarButton button)
    {
        if (Peer(button).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException("Missing native Invoke pattern: " + button.Label);
        invoke.Invoke();
        await Task.Delay(100);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
