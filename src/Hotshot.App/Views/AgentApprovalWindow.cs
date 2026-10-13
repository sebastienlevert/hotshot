using Hotshot.Core.Agents;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Hotshot.Views;

internal sealed class AgentApprovalWindow : Window
{
    private readonly TaskCompletionSource<bool> _decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public AgentApprovalWindow(AgentRequest request, AgentClient client, CancellationToken token)
    {
        Title = "Hotshot agent approval";
        var body = new StackPanel { Spacing = 12, Padding = new Thickness(20) };
        body.Children.Add(new TextBlock { Text = "Allow this assistant request?", FontSize = 22, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(UiStyles.Description($"{client.ProcessName} requests {request.Operation}.\n" +
            "This is the local app, not a verified AI account. It may send images to an AI service."));
        body.Children.Add(UiStyles.Description($"Target: {request.WindowId ?? request.CaptureId ?? (request.MonitorIndex is { } index ? "Monitor " + index : request.Region?.ToString() ?? request.Query)}"));
        var allow = new Button { Content = "Allow once" };
        var deny = new Button { Content = "Don't allow" };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(allow, "AgentAllowOnce");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(deny, "AgentDeny");
        allow.Click += (_, _) => { _decision.TrySetResult(true); Close(); };
        deny.Click += (_, _) => Close();
        body.Children.Add(allow); body.Children.Add(deny);
        Content = body;
        WindowTools.Configure(this, 600, 420, compact: true);
        var registration = token.Register(() => body.DispatcherQueue.TryEnqueue(Close));
        Closed += (_, _) => { registration.Dispose(); _decision.TrySetResult(false); };
    }
    public Task<bool> Decision => _decision.Task;
}
