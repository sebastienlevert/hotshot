using System.ComponentModel;
using System.Text.Json;
using Hotshot.Core.Agents;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Hotshot.Mcp;

[McpServerToolType]
public sealed class HotshotTools(IHotshotConnection connection)
{
    [McpServerTool(Name = "hotshot_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Read Hotshot agent-access status and effective capture settings. Does not capture or modify anything.")]
    public Task<CallToolResult> Status(CancellationToken token) => Call(new AgentRequest { Operation = "status" }, token);

    [McpServerTool(Name = "hotshot_list_windows", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("List up to 50 visible capturable windows with IDs and physical-pixel bounds. Requires native Hotshot agent access.")]
    public Task<CallToolResult> Windows(CancellationToken token, int limit = 50) => Call(new AgentRequest { Operation = "windows", Limit = limit }, token);

    [McpServerTool(Name = "hotshot_list_monitors", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("List up to 50 monitors, physical-pixel bounds and DPI scale. Requires native Hotshot agent access.")]
    public Task<CallToolResult> Monitors(CancellationToken token, int limit = 50) => Call(new AgentRequest { Operation = "monitors", Limit = limit }, token);

    [McpServerTool(Name = "hotshot_capture_window", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true), Description("Capture the visible portion of one window using its listed ID and Hotshot settings. Saves a capture, may replace the clipboard and may run configured AI analysis. Returns metadata and a PNG image; does not focus or click the window. Occluding windows can appear.")]
    public Task<CallToolResult> CaptureWindow(string windowId, CancellationToken token, int maxImageSize = 1600) =>
        Call(new AgentRequest { Operation = "capture_window", WindowId = windowId, MaxImageSize = maxImageSize }, token);

    [McpServerTool(Name = "hotshot_capture_region", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true), Description("Capture a rectangular region in physical virtual-screen pixels using Hotshot's saving, clipboard, cursor, delay and analysis settings. Saves a capture and may replace the clipboard or run configured AI analysis. The region must be fully on-screen.")]
    public Task<CallToolResult> CaptureRegion(int x, int y, int width, int height, CancellationToken token, int maxImageSize = 1600) =>
        Call(new AgentRequest { Operation = "capture_region", Region = new AgentRect(x, y, width, height), MaxImageSize = maxImageSize }, token);

    [McpServerTool(Name = "hotshot_capture_monitor", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true), Description("Capture a monitor by its listed 1-based index using Hotshot settings. Saves a capture and may replace the clipboard or run configured AI analysis. Prefer window or region capture to avoid unrelated desktop content.")]
    public Task<CallToolResult> CaptureMonitor(int monitorIndex, CancellationToken token, int maxImageSize = 1600) =>
        Call(new AgentRequest { Operation = "capture_monitor", MonitorIndex = monitorIndex, MaxImageSize = maxImageSize }, token);

    [McpServerTool(Name = "hotshot_search_history", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Search Hotshot filename, tags, OCR and AI descriptions. Requires separately enabled history access. Returns newest-first metadata, not image pixels.")]
    public Task<CallToolResult> SearchHistory(string query, CancellationToken token, int limit = 10) =>
        Call(new AgentRequest { Operation = "search_history", Query = query, Limit = limit, IncludeImage = false }, token);

    [McpServerTool(Name = "hotshot_get_capture", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Read a capture by its history ID and optionally return its PNG image. Requires separately enabled history access; never accepts arbitrary file paths.")]
    public Task<CallToolResult> GetCapture(string captureId, CancellationToken token, bool includeImage = true, int maxImageSize = 1600) =>
        Call(new AgentRequest { Operation = "get_capture", CaptureId = captureId, IncludeImage = includeImage, MaxImageSize = maxImageSize }, token);

    private async Task<CallToolResult> Call(AgentRequest request, CancellationToken token)
    {
        AgentResponse response;
        try { response = await connection.SendAsync(request, token); }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            response = AgentResponse.Failure("request_failed", ex.Message);
        }
        var content = new List<ContentBlock>();
        var image = response.Capture?.ImageBase64;
        var textResponse = image is null ? response : response with { Capture = response.Capture! with { ImageBase64 = null } };
        content.Add(new TextContentBlock { Text = JsonSerializer.Serialize(textResponse, AgentJsonContext.Default.AgentResponse) });
        if (image is not null) content.Add(ImageContentBlock.FromBytes(Convert.FromBase64String(image), "image/png"));
        return new CallToolResult { Content = content, IsError = !response.Success };
    }
}
