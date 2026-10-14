using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Hotshot.ProductHarness;

internal static class Program
{
    internal static string OutputDirectory = "";
    internal static int ExitCode = 1;
    internal static bool SettingsHeaderOnly;
    internal static bool UiAcceptance;
    internal static bool SoftwareRenderer;
    internal static AcceptanceReport? Report;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var timeout = 180;
            string? expectedArchitecture = null;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--output" when index + 1 < args.Length:
                        OutputDirectory = args[++index];
                        break;
                    case "--ui-acceptance": UiAcceptance = true; break;
                    case "--settings-header-only": SettingsHeaderOnly = true; break;
                    case "--software-renderer": SoftwareRenderer = true; break;
                    case "--expected-architecture" when index + 1 < args.Length:
                        expectedArchitecture = args[++index].ToUpperInvariant();
                        break;
                    case "--timeout-seconds" when index + 1 < args.Length:
                        timeout = int.Parse(args[++index], CultureInfo.InvariantCulture);
                        break;
                    default: throw new ArgumentException("Unknown or incomplete harness argument: " + args[index]);
                }
            }
            if (!Path.IsPathFullyQualified(OutputDirectory) || timeout is < 30 or > 300 ||
                expectedArchitecture is not (null or "X64" or "ARM64") || UiAcceptance && SettingsHeaderOnly ||
                !UiAcceptance && (SoftwareRenderer || expectedArchitecture is not null))
                throw new ArgumentException("Use --output <absolute task-owned directory> [--ui-acceptance [--expected-architecture x64|arm64] [--software-renderer]] [--settings-header-only] [--timeout-seconds 30..300].");
            OutputDirectory = Path.GetFullPath(OutputDirectory);
            Directory.CreateDirectory(OutputDirectory);
            if (UiAcceptance) Report = new AcceptanceReport(OutputDirectory, expectedArchitecture, SoftwareRenderer);
            _ = EnforceTimeoutAsync(timeout);
            var interactive = NativeProductWindow.IsInteractiveWindowStation();
            var ownDesktopReceivesInput = NativeProductWindow.OwnDesktopReceivesInput();
            Report?.SetWindowStation(interactive, ownDesktopReceivesInput);
            if (!Environment.UserInteractive || !interactive || !ownDesktopReceivesInput)
                throw new InvalidOperationException("Native UI acceptance and media generation require an interactive visible window station and this process's desktop receiving input. A service/noninteractive/locked runner is unsupported and cannot pass.");
            if (UiAcceptance)
            {
                if (RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture ||
                    expectedArchitecture is not null && !RuntimeInformation.ProcessArchitecture.ToString().Equals(expectedArchitecture, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Native architecture mismatch: process={RuntimeInformation.ProcessArchitecture}, OS={RuntimeInformation.OSArchitecture}, expected={expectedArchitecture ?? "native"}. Emulated execution is not native acceptance.");
            }
            // The referenced app module is the sole owner of Windows App SDK initialization.
            System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(AppServices).Module.ModuleHandle);
            if (UiAcceptance)
            {
                try { Report!.SetDisplayContext(Hotshot.Capture.DisplayEnvironment.Read(), null); }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    Report!.SetDisplayContext(null, exception.ToString());
                    Console.WriteLine("Read-only display metadata is unavailable; no HDR/mixed-DPI success is inferred.");
                }
            }
            Environment.SetEnvironmentVariable("HOTSHOT_SETTINGS_SYNC_MODE", "local");
            Console.WriteLine($"Starting isolated WinUI harness: process={RuntimeInformation.ProcessArchitecture}, OS={RuntimeInformation.OSArchitecture}, acceptance={UiAcceptance}.");
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(parameters =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new DemoApplication();
            });
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Report?.Fail(exception);
        }
        return ExitCode;
    }

    private static async Task EnforceTimeoutAsync(int seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
        var exception = new TimeoutException($"Native harness exceeded its {seconds}-second bound; native startup/renderer/desktop prerequisites may be unavailable.");
        Console.Error.WriteLine(exception);
        Report?.Fail(exception);
        Environment.Exit(124);
    }
}

public sealed partial class DemoApplication : Application
{
    public DemoApplication()
    {
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, args) =>
        {
            Console.Error.WriteLine(args.Exception);
            Program.Report?.Fail(args.Exception);
        };
        InitializeComponent();
        DispatcherQueue.GetForCurrentThread().TryEnqueue(async () =>
        {
            try
            {
                if (Program.UiAcceptance) await ProductAcceptance.RunAsync(Program.OutputDirectory, Program.Report!);
                else await ProductMedia.RunAsync(Program.OutputDirectory, Program.SettingsHeaderOnly);
                Program.Report?.Complete();
                Program.ExitCode = 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); Program.Report?.Fail(exception); }
            finally { Exit(); }
        });
    }
}
