using System.Text.Json;
using Hotshot.Core.Agents;
using Hotshot.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

if (args.Contains("--fixture"))
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    builder.Services.AddSingleton<IHotshotConnection, SyntheticConnection>();
    builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<HotshotTools>();
    await builder.Build().RunAsync();
    return;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var token = timeout.Token;
var command = args.Length > 0 ? Path.GetFullPath(args[0]) : throw new ArgumentException("Pass the actual MCP executable path.");
var arguments = args.Skip(1).ToList();
await CheckAsync(command, arguments, synthetic: false);
await CheckAsync(Environment.ProcessPath!, ["--fixture"], synthetic: true);
Console.WriteLine("PASS official MCP client handshake, tool schemas, native status, synthetic PNG content and structured errors.");

async Task CheckAsync(string executable, List<string> arguments, bool synthetic)
{
    await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
    {
        Command = executable, Arguments = arguments, Name = "Hotshot protocol test",
    }), cancellationToken: token);
    var tools = await client.ListToolsAsync(cancellationToken: token);
    if (tools.Count != 12 || tools.Any(tool => !tool.Name.StartsWith("hotshot_", StringComparison.Ordinal)))
        throw new InvalidDataException("Unexpected MCP tool inventory.");
    if (tools.Any(tool => tool.JsonSchema.ToString().Contains("\"token\"", StringComparison.Ordinal)))
        throw new InvalidDataException("CancellationToken leaked into a tool's input schema.");
    var status = await client.CallToolAsync("hotshot_status", cancellationToken: token);
    if (!synthetic)
    {
        var response = Response(status);
        if (response.Success && response.Status is null)
            throw new InvalidDataException("Native status metadata is missing.");
        if (!response.Success && response.ErrorCode != "app_unavailable")
            throw new InvalidDataException("Unexpected native status error: " + response.Error);
        return;
    }
    var capture = await client.CallToolAsync("hotshot_capture_region", new Dictionary<string, object?>
        { ["x"] = -10, ["y"] = 20, ["width"] = 2, ["height"] = 1 }, cancellationToken: token);
    var metadata = Response(capture).Capture ?? throw new InvalidDataException("Capture metadata is missing.");
    var image = capture.Content.OfType<ImageContentBlock>().Single();
    if (capture.IsError == true || metadata.Id != "synthetic" || metadata.ImageBase64 is not null ||
        image.MimeType != "image/png" || !image.DecodedData.Span.StartsWith(new byte[] { 137, 80, 78, 71 }))
        throw new InvalidDataException("Invalid MCP text/image result.");
    var denied = await client.CallToolAsync("hotshot_get_capture", new Dictionary<string, object?>
        { ["captureId"] = "synthetic" }, cancellationToken: token);
    if (denied.IsError != true || Response(denied).ErrorCode != "history_disabled")
        throw new InvalidDataException("Tool errors were not marked with isError.");
}

static AgentResponse Response(CallToolResult result) =>
    JsonSerializer.Deserialize(result.Content.OfType<TextContentBlock>().Single().Text, AgentJsonContext.Default.AgentResponse)
    ?? throw new InvalidDataException("Missing JSON response.");

sealed class SyntheticConnection : IHotshotConnection
{
    public Task<AgentResponse> SendAsync(AgentRequest request, CancellationToken token)
    {
        request.Validate();
        var response = request.Operation switch
        {
            "status" => new AgentResponse { Success = true, Status = new AgentStatus(true, false, "test") },
            "capture_region" => new AgentResponse
            {
                Success = true, Capture = new AgentCapture("synthetic", @"C:\synthetic.png", "Region", 2, 1,
                    DateTimeOffset.UtcNow, null, null, null,
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a+1sAAAAASUVORK5CYII=", 1, 1),
            },
            _ => AgentResponse.Failure("history_disabled", "Synthetic fixture denies history."),
        };
        return Task.FromResult(response);
    }
}
