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
    [McpServerTool(Name = "hotshot_test_connection", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
        Description("Test the native connection using a generated image only. Does not capture the desktop, read files, change settings or contact an AI provider; works with agent access disabled.")]
    public Task<CallToolResult> TestConnection(CancellationToken token) => Call(new AgentRequest { Operation = "test_connection" }, token);

    [McpServerTool(Name = "hotshot_list_windows", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("List up to 50 visible capturable windows with IDs and physical-pixel bounds. Requires native Hotshot agent access.")]
    public Task<CallToolResult> Windows(CancellationToken token, int limit = 50, int offset = 0) =>
        Call(new AgentRequest { Operation = "windows", Limit = limit, Offset = offset }, token);

    [McpServerTool(Name = "hotshot_list_monitors", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("List up to 50 monitors, physical-pixel bounds and DPI scale. Requires native Hotshot agent access.")]
    public Task<CallToolResult> Monitors(CancellationToken token, int limit = 50) => Call(new AgentRequest { Operation = "monitors", Limit = limit }, token);

    [McpServerTool(Name = "hotshot_capture_window", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true), Description("Capture one window using its listed ID and Hotshot settings. Defaults to unobscured WGC; explicit legacy mode captures visible screen pixels. Saves a capture, may replace the clipboard and run configured AI analysis. Does not focus/click; protected/hidden/minimized/changed targets fail. Respect native approvals/session access.")]
    public Task<CallToolResult> CaptureWindow(string windowId, CancellationToken token, int maxImageSize = 1600) =>
        Call(new AgentRequest { Operation = "capture_window", WindowId = windowId, MaxImageSize = maxImageSize }, token);

    [McpServerTool(Name = "hotshot_capture_region", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true), Description("Capture a rectangular region in physical virtual-screen pixels using Hotshot's saving, clipboard, cursor, delay and analysis settings. Saves a capture and may replace the clipboard or run configured AI analysis. The region must be fully on-screen.")]
    public Task<CallToolResult> CaptureRegion(int x, int y, int width, int height, CancellationToken token, int maxImageSize = 1600) =>
        Call(new AgentRequest { Operation = "capture_region", Region = new AgentRect(x, y, width, height), MaxImageSize = maxImageSize }, token);

    [McpServerTool(Name = "hotshot_capture_monitor", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true), Description("Capture a monitor by its listed 1-based index using Hotshot settings. Saves a capture and may replace the clipboard or run configured AI analysis. Prefer window or region capture to avoid unrelated desktop content.")]
    public Task<CallToolResult> CaptureMonitor(int monitorIndex, CancellationToken token, int maxImageSize = 1600) =>
        Call(new AgentRequest { Operation = "capture_monitor", MonitorIndex = monitorIndex, MaxImageSize = maxImageSize }, token);

    [McpServerTool(Name = "hotshot_search_history", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Search Hotshot filename, tags, OCR and AI descriptions. Requires separately enabled history access. Returns newest-first metadata, not image pixels.")]
    public Task<CallToolResult> SearchHistory(string query, CancellationToken token, int limit = 10, int offset = 0) =>
        Call(new AgentRequest { Operation = "search_history", Query = query, Limit = limit, Offset = offset, IncludeImage = false }, token);

    [McpServerTool(Name = "hotshot_get_capture", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Read a capture by its history ID and optionally return its PNG image. Requires separately enabled history access; never accepts arbitrary file paths.")]
    public Task<CallToolResult> GetCapture(string captureId, CancellationToken token, bool includeImage = true, int maxImageSize = 1600) =>
        Call(new AgentRequest { Operation = "get_capture", CaptureId = captureId, IncludeImage = includeImage, MaxImageSize = maxImageSize }, token);

    [McpServerTool(Name = "hotshot_get_capture_region", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
        Description("Read a high-resolution crop from a known saved screenshot using original image pixel coordinates. Requires history access. Returns crop bounds and a PNG preview; does not capture the desktop.")]
    public Task<CallToolResult> GetCaptureRegion(string captureId, int x, int y, int width, int height, CancellationToken token, int maxImageSize = 2048) =>
        Call(new AgentRequest { Operation = "get_capture_region", CaptureId = captureId, Region = new AgentRect(x, y, width, height), MaxImageSize = maxImageSize }, token);

    [McpServerTool(Name = "hotshot_compare_captures", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
        Description("Compare two known saved screenshots of identical dimensions. Returns changed-pixel count, percentage, bounding rectangle, and a PNG diff. Tolerance is per RGB channel (0-255). Differences are evidence, not proof of correctness.")]
    public Task<CallToolResult> CompareCaptures(string beforeId, string afterId, CancellationToken token, int tolerance = 8, int maxImageSize = 1600) =>
        Call(new AgentRequest { Operation = "compare_captures", BeforeId = beforeId, CaptureId = afterId, Tolerance = tolerance, MaxImageSize = maxImageSize }, token);

    [McpServerTool(Name = "hotshot_read_capture_text", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
        Description("Run local Windows OCR on a known saved screenshot. Returns text and word rectangles in original image pixels. Requires history permission and an installed OCR language; does not send pixels to a model.")]
    public Task<CallToolResult> ReadCaptureText(string captureId, CancellationToken token) =>
        Call(new AgentRequest { Operation = "read_capture_text", CaptureId = captureId }, token);

    [McpServerTool(Name = "hotshot_activity", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
        Description("Read recent native agent-request outcomes from this app run. Contains timestamps and operations, not captured pixels or file paths. Requires enabled agent access.")]
    public Task<CallToolResult> Activity(CancellationToken token, int limit = 10, int offset = 0) =>
        Call(new AgentRequest { Operation = "activity", Limit = limit, Offset = offset }, token);

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
