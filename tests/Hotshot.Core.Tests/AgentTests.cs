using System.Buffers.Binary;
using System.Text.Json;
using Hotshot.Core.Agents;
using Hotshot.Core.Settings;

namespace Hotshot.Core.Tests;

public sealed class AgentTests
{
    [Fact]
    public void MinimalReadOnlyRequestsPreserveDefaultLimits()
    {
        var request = AgentProtocol.DecodeRequest(System.Text.Encoding.UTF8.GetBytes("""{"operation":"windows","limit":1}"""));
        Assert.Equal(1600, request.MaxImageSize);
        Assert.Equal("", request.Query);
        request.Validate();
    }
    [Fact]
    public void AccessIsOptInAndRoundTrips()
    {
        var settings = SettingsStore.Deserialize("""{"agents":null}""");
        Assert.False(settings.Agents.Enabled);
        Assert.False(settings.Agents.AllowHistoryAccess);
        settings.Agents.Enabled = true;
        settings.Agents.AllowHistoryAccess = true;
        var cloned = settings.Clone();
        Assert.True(cloned.Agents.Enabled);
        Assert.True(cloned.Agents.AllowHistoryAccess);
    }

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-1920, -100, 1920, 1080)]
    [InlineData(0, 0, 8000, 8000)]
    public void ValidPhysicalRegions(int x, int y, int width, int height) =>
        new AgentRect(x, y, width, height).Validate();

    [Theory]
    [InlineData(0, 0, 0, 100)]
    [InlineData(0, 0, -1, 100)]
    [InlineData(0, 0, 8001, 8000)]
    [InlineData(0, 0, 32769, 1)]
    [InlineData(int.MaxValue, 0, 1, 1)]
    [InlineData(0, int.MinValue, 100, 100)]
    public void InvalidRegionsAreRejected(int x, int y, int width, int height) =>
        Assert.Throws<ArgumentException>(() => new AgentRect(x, y, width, height).Validate());

    [Theory]
    [InlineData("""{"operation":"shell"}""")]
    [InlineData("""{"operation":"status","limit":0}""")]
    [InlineData("""{"operation":"status","limit":51}""")]
    [InlineData("""{"operation":"status","maxImageSize":2049}""")]
    [InlineData("""{"operation":"status","maxImageSize":255}""")]
    [InlineData("""{"operation":"status","query":null}""")]
    [InlineData("""{"operation":"capture_window","windowId":""}""")]
    [InlineData("""{"operation":"capture_region"}""")]
    [InlineData("""{"operation":"capture_monitor","monitorIndex":0}""")]
    [InlineData("""{"operation":"get_capture","captureId":null}""")]
    public void InvalidRequestsAreRejected(string json) =>
        Assert.Throws<ArgumentException>(() => AgentProtocol.DecodeRequest(System.Text.Encoding.UTF8.GetBytes(json)).Validate());

    [Fact]
    public void PipeIdentitySeparatesUsersAndSessions()
    {
        Assert.Equal(AgentProtocol.PipeName("synthetic-user", 1), AgentProtocol.PipeName("synthetic-user", 1));
        Assert.NotEqual(AgentProtocol.PipeName("synthetic-user", 1), AgentProtocol.PipeName("synthetic-user", 2));
        Assert.NotEqual(AgentProtocol.PipeName("synthetic-user", 1), AgentProtocol.PipeName("other-user", 1));
        Assert.DoesNotContain("synthetic-user", AgentProtocol.PipeName("synthetic-user", 1));
    }

    [Fact]
    public async Task FrameAndResponseRoundTripPreserveUnicode()
    {
        var response = new AgentResponse
        {
            Success = true,
            Capture = new AgentCapture("known-id", @"C:\Test folder\image.png", "Window", 4000, 2000,
                DateTimeOffset.UtcNow, "\u00e9\u65e5", null, "Synthetic text", "AQID", 1600, 800),
        };
        using var stream = new MemoryStream();
        await AgentFrames.WriteAsync(stream, AgentProtocol.Encode(response), AgentProtocol.MaxResponseBytes, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var actual = AgentProtocol.DecodeResponse(await AgentFrames.ReadAsync(stream, AgentProtocol.MaxResponseBytes, TestContext.Current.CancellationToken));
        Assert.Equal(response, actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(16385)]
    [InlineData(int.MaxValue)]
    public async Task FrameRejectsInvalidLengthBeforeAllocation(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => AgentFrames.ReadAsync(stream, AgentProtocol.MaxRequestBytes, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FrameRejectsTruncationAndOversizedWrites()
    {
        using var stream = new MemoryStream(new byte[] { 4, 0, 0, 0, 1 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => AgentFrames.ReadAsync(stream, 10, TestContext.Current.CancellationToken));
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => AgentFrames.WriteAsync(output, new byte[11], 10, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(CodingAgent.CopilotCli, "mcpServers")]
    [InlineData(CodingAgent.ClaudeCode, "mcpServers")]
    [InlineData(CodingAgent.VsCode, "servers")]
    [InlineData(CodingAgent.Cursor, "mcpServers")]
    public void ConfigurationsPreserveAbsolutePaths(CodingAgent agent, string root)
    {
        var path = Path.Combine(Path.GetTempPath(), "Hotshot with spaces", "Mcp", "Hotshot.Mcp.exe");
        using var config = JsonDocument.Parse(AgentConfiguration.Create(agent, path));
        var server = config.RootElement.GetProperty(root).GetProperty("hotshot");
        Assert.Equal(path, server.GetProperty("command").GetString());
        Assert.Equal(0, server.GetProperty("args").GetArrayLength());
    }

    [Fact]
    public void CodexUsesTomlAndRejectsRelativeExecutable()
    {
        var path = Path.Combine(Path.GetTempPath(), "Hotshot", "Hotshot.Mcp.exe");
        Assert.StartsWith("[mcp_servers.hotshot]", AgentConfiguration.Create(CodingAgent.Codex, path));
        Assert.Throws<ArgumentException>(() => AgentConfiguration.Create(CodingAgent.CopilotCli, "Hotshot.Mcp.exe"));
    }
}
