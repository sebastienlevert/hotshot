using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Hotshot.Mcp;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<IHotshotConnection, HotshotConnection>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<HotshotTools>();
await builder.Build().RunAsync();
