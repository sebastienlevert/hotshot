using System.Runtime.InteropServices;
using System.Text.Json;

namespace Hotshot.ProductHarness;

internal sealed class AcceptanceReport
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<AcceptanceCheck> _checks = [];
    private string _outcome = "running";
    private string? _failure;
    private bool? _interactiveWindowStation;
    private bool? _ownDesktopReceivesInput;
    private bool? _editorSoftwareRenderer;
    private IReadOnlyList<Hotshot.Capture.DisplayColorInfo>? _displayContext;
    private string? _displayContextFailure;

    public AcceptanceReport(string output, string? expectedArchitecture, bool softwareRenderer)
    {
        _path = Path.Combine(output, "ui-acceptance.json");
        ExpectedArchitecture = expectedArchitecture;
        RequestedSoftwareRenderer = softwareRenderer;
        Write();
    }

    public string? ExpectedArchitecture { get; }
    public bool RequestedSoftwareRenderer { get; }

    public void SetWindowStation(bool interactive, bool ownDesktopReceivesInput)
    {
        lock (_gate)
        {
            _interactiveWindowStation = interactive;
            _ownDesktopReceivesInput = ownDesktopReceivesInput;
            Write();
        }
    }

    public void SetEditorRenderer(bool software)
    {
        lock (_gate) { _editorSoftwareRenderer = software; Write(); }
    }

    public void SetDisplayContext(IReadOnlyList<Hotshot.Capture.DisplayColorInfo>? displays, string? failure)
    {
        lock (_gate) { _displayContext = displays; _displayContextFailure = failure; Write(); }
    }

    public async Task CheckAsync(string name, Func<Task> action)
    {
        var check = new AcceptanceCheck(name);
        lock (_gate) { _checks.Add(check); Write(); }
        try
        {
            await action().WaitAsync(TimeSpan.FromSeconds(45));
            lock (_gate) { check.Outcome = "passed"; Write(); }
            Console.WriteLine("PASS " + name);
        }
        catch (Exception exception)
        {
            lock (_gate) { check.Outcome = "failed"; check.Failure = exception.ToString(); Write(); }
            throw;
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_checks.Count == 0 || _checks.Any(check => check.Outcome != "passed") || _failure is not null)
                throw new InvalidOperationException("Every required native UI check must pass before acceptance can pass.");
            _outcome = "passed";
            Write();
        }
    }

    public void Fail(Exception exception)
    {
        lock (_gate)
        {
            _outcome = "failed";
            _failure = exception.ToString();
            Write();
        }
    }

    private void Write()
    {
        var evidence = new
        {
            schemaVersion = 1,
            mode = "native-ui-acceptance",
            outcome = _outcome,
            collectedAt = DateTimeOffset.UtcNow,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            expectedArchitecture = ExpectedArchitecture,
            nativeArchitecture = RuntimeInformation.ProcessArchitecture == RuntimeInformation.OSArchitecture,
            osVersion = Environment.OSVersion.Version.ToString(),
            framework = RuntimeInformation.FrameworkDescription,
            applicationVersion = typeof(AppServices).Assembly.GetName().Version?.ToString(),
            environmentUserInteractive = Environment.UserInteractive,
            interactiveWindowStation = _interactiveWindowStation,
            ownDesktopReceivesInput = _ownDesktopReceivesInput,
            readOnlyDisplayContext = new
            {
                outcome = _displayContext is null ? "unavailable" : _displayContext.Any(display => display.NativeStatus != 0) ? "partial" : "available",
                displays = _displayContext,
                failure = _displayContextFailure,
                interpretation = "Read-only anonymous display facts, not an HDR capture/fidelity or mixed-DPI movement check. Unknown/unsupported native color queries remain unverified.",
            },
            renderer = new
            {
                requestedEditorSoftwareRenderer = RequestedSoftwareRenderer,
                actualEditorSoftwareRenderer = _editorSoftwareRenderer,
                xamlBackend = "WinUI native composition; GPU/backend selection is not inferred from a successful render.",
                ownWindowEvidence = "RenderTargetBitmap of native XAML controls on an opaque neutral fixture background; may omit composition-only/Win2D surfaces. Editor draw and generated-image export are checked separately.",
            },
            checks = _checks,
            failure = _failure,
            prerequisites = new[]
            {
                "Windows 10 build 19041 or newer, native process/OS architecture matching the selected matrix entry.",
                "An interactive visible window station and functioning WinUI/Windows App SDK native composition.",
                "Win2D/D3D support (software rendering is permitted for the editor); no recording or desktop capture is required.",
            },
            limitations = new[]
            {
                "Hosted execution is not physical ARM64 hardware, GPU fidelity, physical HDR, mixed-DPI monitor movement, or human Narrator acceptance.",
                "System high-contrast/text-scale transitions are not changed or verified; accessibility-evidence.json reports notification capability separately.",
                "Focus traversal and automation peers are exercised in real native controls; no global keyboard input is injected.",
                "No Windows.Graphics.Capture, desktop capture, recording, audio, clipboard, signed-in provider, or real user-library operation is performed.",
                "Unsupported/no interactive desktop, unavailable renderer, skipped required checks and timeouts are failures, not passes.",
            },
            privacy = "Only generated images, synthetic fixture data, native control evidence and harness diagnostics are written below --output. The fixture profile is removed after normal completion.",
        };
        // Replace atomically so an externally terminated run still leaves parseable progress evidence.
        var pending = _path + ".pending";
        File.WriteAllText(pending, JsonSerializer.Serialize(evidence, JsonOptions));
        File.Move(pending, _path, overwrite: true);
    }

    private sealed class AcceptanceCheck(string name)
    {
        public string Name { get; } = name;
        public string Outcome { get; set; } = "running";
        public string? Failure { get; set; }
    }
}
