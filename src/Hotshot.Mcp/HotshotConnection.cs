using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Hotshot.Core.Agents;

namespace Hotshot.Mcp;

public interface IHotshotConnection
{
    Task<AgentResponse> SendAsync(AgentRequest request, CancellationToken token);
}

public sealed class HotshotConnection : IHotshotConnection
{
    public async Task<AgentResponse> SendAsync(AgentRequest request, CancellationToken token)
    {
        request.Validate();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(95));
        token = deadline.Token;
        var identity = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows user cannot be identified.");
        using var pipe = new NamedPipeClientStream(".", AgentProtocol.PipeName(identity, Process.GetCurrentProcess().SessionId),
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(3000, token); }
        catch (TimeoutException)
        {
            return AgentResponse.Failure("app_unavailable", "Start Hotshot in this Windows session and enable Settings > Coding agents > Agent access.");
        }
        await AgentFrames.WriteAsync(pipe, AgentProtocol.Encode(request), AgentProtocol.MaxRequestBytes, token);
        return AgentProtocol.DecodeResponse(await AgentFrames.ReadAsync(pipe, AgentProtocol.MaxResponseBytes, token));
    }
}
