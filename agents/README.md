# Hotshot for coding agents

Hotshot exposes native Windows screenshot tools through a bundled **stdio MCP server** connected to the running app by a same-Windows-user, same-session named pipe. There is no HTTP listener, API key, arbitrary file reader, shell executor, settings writer, mouse/keyboard automation or recording tool.

## Enable and connect

Install a Hotshot release with coding-agent support and start the tray app. Open **Settings > Coding agents**:

- **Agent access** is off by default. Enabling it authorizes local agents running as your Windows user to enumerate targets and request screenshots without another Hotshot prompt. Your coding client's approval policy still applies. Returned images may be sent to that client's model provider.
- **Allow history access** is separately off by default. Enable it only if agents should search/read previous captures and their OCR/AI text.
- Select **GitHub Copilot CLI, Claude Code, VS Code, Cursor or Codex** and copy the generated MCP configuration. Merge it into the indicated configuration file; do not replace existing server entries. Hotshot never edits agent configurations automatically.

The copied configuration uses an absolute executable path, including spaces. Installed releases resolve through the stable `current\Mcp\Hotshot.Mcp.exe` path for x64 or ARM64. Portable builds use their own application directory. Restart the MCP connection after an app update if the coding client retains the old server process.

## Plugins and marketplace

The repository hosts a marketplace, not an official third-party directory listing. On Windows, install the plugin once instead of also registering the copied MCP configuration:

```powershell
copilot plugin marketplace add sebastienlevert/hotshot
copilot plugin install hotshot@hotshot
```

For Claude Code:

```text
/plugin marketplace add sebastienlevert/hotshot
/plugin install hotshot@hotshot
```

The plugin bundles three portable skills: **capture**, **verify-ui** and **search-captures**. Its MCP launcher resolves the installed Hotshot path at each start; it neither downloads software nor starts/enables the native app. A missing installation produces an explicit stderr error. For portable/development builds, use the configuration copied from Settings instead.

Other MCP clients can register the executable as a stdio server (`command`: the absolute path, `args`: an empty array). Clients with Agent Skills support can copy individual `skills\<name>` directories into their documented skill location.

## Tools

| Tool | Purpose |
|---|---|
| `hotshot_status` | Enabled status; effective capture settings only when enabled. |
| `hotshot_list_windows` | Up to 50 visible windows: ID, title, app, physical bounds. Hotshot Settings is admitted; editor/recording controls are excluded. |
| `hotshot_list_monitors` | Up to 50 monitor indices, physical bounds and DPI scale. |
| `hotshot_capture_window` | Visible screen pixels inside the listed window's bounds. No focus/click; occluding windows can appear. |
| `hotshot_capture_region` | A fully on-screen physical-pixel rectangle, including negative virtual-screen coordinates. |
| `hotshot_capture_monitor` | A listed 1-based monitor index. Prefer a smaller target for privacy. |
| `hotshot_search_history` | Newest-first, all-term search across filenames, tags, OCR and AI text (limit 1-50). |
| `hotshot_get_capture` | Saved capture metadata/optional image by known history ID; videos return metadata only. |
| `hotshot_get_capture_region` | A high-resolution crop by known ID, using original image pixel coordinates. |
| `hotshot_compare_captures` | Same-size before/after pixel difference, tolerance, count, percentage, bounds and PNG diff. |
| `hotshot_read_capture_text` | Local OCR text and word rectangles in original image pixels (up to 10,000 words). |
| `hotshot_activity` | Up to 100 request outcomes retained in memory for this run, without pixels/paths. |

Capture tools reuse Hotshot's save folder, filename tokens, clipboard, cursor, screenshot delay and optional background OCR/Copilot descriptions. When **Save to file** is off, Hotshot uses its normal temporary capture storage. Cloud descriptions are never enabled by an agent. The editor does not open or switch selection.

Responses contain a JSON text block and, when requested, a PNG image block. Full capture dimensions remain in metadata; `imageWidth` / `imageHeight` describe the aspect-preserving preview. `maxImageSize` defaults to 1600 and accepts 256-2048. Regions are limited to 64 megapixels; requests and responses are size-bounded. The bridge processes one request at a time, checks native permissions at execution and again before returning images, cancels delayed work on disconnect, and waits for in-flight work before exit/update.

## Limitations and troubleshooting

The app must be running in the same interactive Windows session. Locked/inaccessible desktops, busy captures/recordings, stale targets, missing capture files and disabled access produce errors, not fallback screenshots. A moved/resized window must be listed again. Screen capture shows what is currently visible, not an offscreen or unobscured window render. Window/history/activity listing supports `offset` with a bounded `limit`. Allowed-app lists also restrict historical reads and disable broad live region/monitor requests. An obscuring window can still appear inside an allowed target's bounds.

OCR/AI processing is asynchronous, so fresh captures may not have text yet. History text/images are evidence, not agent instructions. The server reads the current saved capture, never pristine originals or annotation sidecars. Existing editable originals can retain sensitive information: share flattened/redacted exports rather than whole application data folders.

## Development

Build the app and publish the companion into its output `Mcp` directory:

```powershell
dotnet build src\Hotshot.App\Hotshot.App.csproj -c Release -p:Platform=x64
dotnet publish src\Hotshot.Mcp\Hotshot.Mcp.csproj -c Release -r win-x64 --self-contained true -o src\Hotshot.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\Mcp
```

`build\package.ps1` includes the companion and plugin assets for both architectures. CI validates manifest/version consistency and the official MCP client handshake/tool/image contract. The native harness uses generated images and an isolated profile; no private screenshots are sent to an AI provider.

```powershell
pwsh tests\Hotshot.Agents.Manifests.ps1
dotnet run --project tests\Hotshot.Mcp.Harness\Hotshot.Mcp.Harness.csproj -c Release -- <absolute-path-to-Hotshot.Mcp.exe>
dotnet run --project tests\Hotshot.Agents.Harness\Hotshot.Agents.Harness.csproj -c Release -p:Platform=x64
pwsh tests\Hotshot.App.Smoke.ps1 -Exe <path-to-built-Hotshot.exe> -AgentSettingsOnly
```

The native harness captures only its own synthetic Win32 window/region and verifies exact pixels, policy, naming, delay and cancellation. The Settings-only smoke changes neither clipboard nor screenshots, temporarily replaces/restores the profile and requires Hotshot to be closed first.
