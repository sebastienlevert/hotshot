using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Hotshot.Mcp;
using Hotshot.Core.Agents;
using System.Text.Json;

if (args.Contains("--test-connection"))
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var result = await new HotshotConnection().SendAsync(new AgentRequest { Operation = "test_connection" }, timeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(result, AgentJsonContext.Default.AgentResponse));
    Environment.ExitCode = result.Success ? 0 : 1;
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<IHotshotConnection, HotshotConnection>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<HotshotTools>();
await builder.Build().RunAsync();
