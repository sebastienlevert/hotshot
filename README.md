# Hotshot

A native WinUI 3 screenshot and screen-recording utility for Windows 10 (19041+) and Windows 11, built with .NET 10. It lives in the tray rather than leaving a main window open.

The Hotshot logo pairs a white capture viewfinder with a lightning bolt on an amber-to-coral tile. Taskbar and tray icons include sizes from 16 to 256 pixels; recording switches the tray to a dark tile with a red recording dot. Regenerate the assets with `pwsh build\make-icons.ps1`.

[Download the latest release](https://github.com/sebastienlevert/hotshot/releases/latest) · [Changelog](CHANGELOG.md) · [Report an issue](https://github.com/sebastienlevert/hotshot/issues)

## Use

Download **Hotshot-win-x64-Setup.exe** or **Hotshot-win-arm64-Setup.exe** from GitHub Releases and run it. Installation is per-user and needs no administrator privileges. No separate .NET or Windows App SDK installation is required. The first launch opens settings; closing a settings/editor window leaves the app running in the tray.

Managed portable bundles are also available. Extract the entire `Hotshot-win-<architecture>-Portable.zip` to a permanent writable folder; do not move individual files out of the bundle. Earlier development ZIPs and raw `dotnet publish` folders are unmanaged and cannot update themselves: install a current release once to enable updates.

### Automatic updates

Installed and Velopack-managed portable builds check this public repository's stable GitHub Releases shortly after startup and hourly thereafter. New versions download and are verified in the background, using separate `win-x64` and `win-arm64` static release feeds. This avoids GitHub REST API quotas, and package URLs stay pinned to the discovered release version. No GitHub account or token is required.

Once ready, Hotshot updates silently and restarts in the tray when it is safe: no capture, recording, GIF conversion, AI description, editor operation, unsaved annotations/settings, or active editor/settings window. Work in progress is never interrupted. Offline/failed checks are logged and retried automatically; prepared updates are reconsidered after startup using those same safety checks. Secondary launches never force-apply an update before the single-instance/idle checks. **About Hotshot > Automatic updates** shows status and offers an optional Check now button.

Settings, history and originals stay in `%AppData%\Hotshot`, outside the installer-owned application directory. Release packages are currently unsigned, so Windows may show a first-install SmartScreen warning; no signing credentials are stored in this repository.

| Default shortcut | Action |
|---|---|
| Print Screen | Select a region or click a window |
| Ctrl+Print Screen | Capture the monitor under the cursor |
| Shift+Print Screen | Capture the full virtual screen (all monitors) |
| Ctrl+Shift+Print Screen | Start/stop screen recording |

Settings use a PowerToys-inspired adaptive sidebar, grouped setting cards, and automatic saving. The title bar and caption buttons follow **General > App theme** (Windows default, light or dark), including inactive windows. Settings search is embedded in the title bar at every window width; the title and subtitle hide on narrow windows to leave room for search and the window buttons. **Ctrl+F** focuses it. Search individual settings by name or related keywords, choose a suggestion to jump to its highlighted card, or press Enter for all matching results. Invalid edits remain unapplied with an inline explanation; there is no global Save button.

All shortcuts are configurable under **Keyboard shortcuts**: click a shortcut, press the new combination, and Apply or Clear it. Duplicate/invalid shortcuts are rejected, and registration conflicts are shown. If Print Screen launches Snipping Tool, disable that option in Windows keyboard settings or choose another shortcut. In the selection overlay, **Space** captures/selects the monitor under the cursor and **Escape** cancels.

Screenshots are saved as PNG and copied to the clipboard as both an image and PNG data before disk saving. Captures are silent: no preview/editor window opens automatically, including for older settings profiles. Hotshot's Settings window can be captured normally, including click-to-select window captures, so you can share screenshots for feedback. The editor and recording controls remain excluded from captures.

Click the tray icon to open or focus **Editor and history**. It starts with a compact filename list and an empty editor, not the most recent screenshot. Select a filename or use the arrow keys to move through the list; the selected screenshot appears directly in the editor, without separate thumbnail previews. Add arrows, shapes, text, freehand ink, highlights, numbered steps or pixelated regions, or crop it. Undo/redo covers annotations and crops. **Save** updates the PNG while retaining the pristine original and an editable annotation sidecar under `%AppData%\Hotshot\originals`; **Copy** copies the edited image without saving it. Switching or closing prompts before discarding unsaved edits. Video entries have Open/Copy/Convert to GIF actions. Right-click the tray for capture commands, recording controls, settings, the captures folder, and exit.

Recording produces H.264 MP4 with optional system audio and/or microphone audio. Turn both audio options off for silent video. A countdown and recording control window provide pause/resume and stop/save; closing the recording control stops and saves. Window targets and single-monitor regions are supported. Regions cannot span monitors. A closed target stops recording, including when paused.

Use **Convert to GIF** on an MP4 in history, enable automatic GIF creation, or bind the Record as GIF shortcut. GIF conversion runs in the background, supports frame rate, width, dithering and looping, and keeps the original MP4. GIFs cannot contain audio and are limited to 256 colors per palette, so photographic/complex gradients are necessarily lossy. MP4/GIF clipboard copies are files rather than static images.

**Start with Windows** uses the current user's Run registry entry and is enabled by default in Release builds. Debug builds never modify that entry. Keep a portable installation in its permanent location; moving it requires running the new copy and changing the startup setting to refresh its path. Exit via the tray menu or `Hotshot.exe --exit`.

## Native workbench

The ShareX-inspired workbench keeps the WinUI experience and silent captures:

- **Organize:** mark favorites, edit tags in **Details and tags**, and filter by capture type or date. Search terms match filename, tags, application, AI description and recognized text. Favorites are retained beyond the ordinary recent-history limit.
- **Local OCR:** **Extract text** recognizes the edited image and lets you copy the result. **Screenshots > Recognize screenshot text locally** optionally indexes new captures automatically. Windows OCR language support is required; no image or text is uploaded.
- **Edit:** choose annotations from the layer selector, duplicate them with **Ctrl+D**, bring them forward/backward, resize using handles, and nudge a selected annotation with arrow keys (**Shift** moves 10 pixels). Shape fill and text size controls are available in the annotation toolbar.
- **Redact:** the **Redact** tool uses opaque black rectangles above all annotation layers. Saved redactions discard old AI/OCR text metadata so hidden content is not leaked through descriptions. Pristine originals and editable sidecars still contain the original content and remain private; share only the flattened exported image.
- **Export:** save a separate PNG/JPEG with proportional resizing, padding, backgrounds, rounded corners and shadows. Export copies omit AI/OCR metadata and never replace the editable capture. **Import image** adds existing PNG/JPEG/BMP images without changing the clipboard.
- **Capture delay:** set 0–30 seconds under **Screenshots**; the default remains immediate capture.
- **Navigate precisely:** wheel zoom stays at your chosen level; **Ctrl+1** uses actual physical-pixel 100%, **Ctrl+0** fits the image, and Space-drag/middle-drag pans. **V/A/R/T/C/P** select tools. Focus the canvas and press **Insert** to create an annotation, arrow keys to move it, or **Ctrl+arrows** to resize a box/line; Shift changes the step to 10 pixels.
- **Pin and compare:** Pin image opens an always-on-top copy without writing or copying anything. Ctrl-select two same-size screenshots and choose Compare selected screenshots for before/after/diff views, changed-pixel percentage and bounds.
- **Durable library:** saved captures remain searchable beyond the recent-history limit by default; only temporary captures are trimmed. Toggle this under General. History uses atomic replacement, a previous-valid backup and visible recovery/write errors. Missing saved files remain listed instead of silently losing their records.
- **Projects and searches:** organize selected captures into a project, add tags in batches, save named searches, rename captures, export selected flattened files or delete selected files to the Recycle Bin. Filters live in a collapsible panel. Search supports substrings and `project:`, `tag:`, `app:`, `type:`, `text:` and quoted phrases; a rebuildable in-memory trigram index accelerates single-term lookups.
- **Guided scrolling:** choose Scrolling capture from the tray/editor or `Hotshot.exe --scrolling`, select the scrolling content, scroll it yourself, and add overlapping frames. Ambiguous overlap is rejected without appending. Fixed headers should be excluded; frames must keep their position/size. The output is limited to 16,384 pixels tall / 64 megapixels.
- **Recordings:** play videos inside the workspace, extract the paused/current frame, and trim to a separate MP4. Choose a microphone in Settings; live recording controls have microphone/system mute and level meters. The recovery journal can restore a finalized playable MP4 after interruption, or archive its notice without changing the source. Unfinalized/unreadable MP4 data is preserved and explicitly reported, not claimed recovered.

### Consolidated local workflows

- **Export presets, not sharing:** export remembers its last format, width, padding, radius, shadow and background. Save/reuse/delete up to 50 named presets. No capture-sharing, uploader or Markdown-copy feature is added; only portable settings roam through OneDrive.
- **Storage and privacy:** Library actions shows measured capture/thumbnail/original/index sizes. Preview orphan cleanup or explicitly include all private originals/sidecars, then confirm permanent deletion in a second dialog. Saved capture files are not cleaned up automatically. Removing originals discards editable history; the current flattened capture remains. File-size changes and symbolic-link targets are rejected during cleanup.
- **Local backup/restore:** ZIP backups contain existing captures and metadata; private originals are separately opt-in. Restores are previewed and imported into a new capture subfolder without replacing existing files/IDs or scheduling cloud analysis. Paths and archive sizes/counts are validated. Relink missing capture selects an existing matching-format file; screenshot dimensions must match.
- **Scalable history:** `history.json` is a compatible checkpoint with a previous-valid backup. Small changes append durable transactions to `history.json.journal`; checkpoints compact every 256 transactions/8 MB and at clean exit. An incomplete final transaction is preserved for inspection and recovered; invalid completed transactions pause writes. The workspace renders bounded pages rather than rebuilding all capture rows.
- **Visual verification:** comparison keeps full-resolution sources, synchronized zoom/pan and a swipe slider. Adjust pixel tolerance, align After with X/Y offsets, and draw/type ignored rectangles. Counts/percentages exclude ignored/unmatched pixels. Set a project's favorite baseline, compare new captures against it, or export a local JSON report plus flattened before/after/diff PNGs. No overlap is reported as “nothing compared,” not a successful visual match.
- **Unobscured windows:** selected window screenshots use a bounded Windows.Graphics.Capture float16 pipeline by default, without focusing/moving the window. Hidden/minimized/protected/stale/moved/resized/DPI-changed targets fail explicitly. Disable this setting for legacy visible-screen pixels; no silent fallback occurs. The cursor setting is respected and Windows may draw its capture border. Mica/backdrop composition can differ from desktop pixels. HDR scRGB is explicitly tone-mapped to SDR sRGB with Reinhard; negative out-of-gamut values are clipped and warnings are logged/returned. PNG output does not preserve HDR brightness/gamut. Monitor/region captures still use the 8-bit GDI path.
- **Native finishing touches:** Settings/workspace bounds and splitter width are remembered and clamped to connected monitor work areas on restore. Pinned images have 20–100% opacity and reversible click-through: an independent Resume control stays clickable and a 20-second timer restores input. Trim uses video preview plus keyboard-accessible start/end sliders. Long backup/restore, copy, trimming and comparison work has cancellation. About includes a short local quick-start guide, also shown on first run.

Settings now use Windows Community Toolkit **SettingsCard** and **SettingsExpander** controls, and the editor/history split uses **GridSplitter**. Toolkit `8.3.260402-preview2` is pinned because its split WinUI dependency is compatible with Hotshot's Windows App SDK 2.x; the older stable Toolkit pulls in conflicting App SDK 1.6 build targets.

Feature research used [REA](https://github.com/morluto/rea) static inspection of the official ShareX 21.0.0 assemblies. Observed capability boundaries informed independent native implementations; no ShareX GPL source, binaries or UI are included in Hotshot. This is not a full ShareX clone: scrolling is guided rather than automated and upload services are not included.

## AI screenshot descriptions

Enable **Screenshots > Describe screenshots with Copilot** to describe new screenshots after they are saved. This is **off by default**. Enabling it sends screenshot pixels to the signed-in GitHub Copilot account and consumes that account's model quota; screenshots can contain sensitive information. No image is sent while the setting is off. Recordings and GIFs are not submitted.

The SDK's pinned runtime is bundled with Hotshot. An existing GitHub Copilot sign-in and an available image-capable model are required. Hotshot chooses the available vision model with the lowest reported quota multiplier, allows no tools, and does not load workspace instructions, skills, or session-store memory. Each image uses a separate session, which is deleted after processing. Turning the setting off cancels in-flight and queued analysis; unfinished work can resume on the next launch while the setting remains enabled.

Generated summaries and descriptions are searchable in **Editor and history**. PNG files also receive lossless Unicode **Title** (summary) and **Description** metadata; pixels are not re-encoded. Metadata is retained when saving edits, and enabled descriptions are refreshed after an edited screenshot is saved. Windows Explorer does not reliably display these fields for PNG, but metadata-aware image tools can read them. Failed/offline descriptions do not lose the saved screenshot and are reported visibly.

Automatic analysis remains available. **Review before cloud analysis** optionally holds new captures until you review/redact and save, then explicitly approve Analyze selected screenshot in the workspace. **Exclude applications from AI** accepts process names; this uses the capture's recorded application, not sensitive-content detection across the whole desktop. Use review mode for monitor captures containing other apps. The queue is bounded to 32 waiting jobs, deduplicates a capture already queued, and reports overflow. View AI processing shows queued/held/failed items and cancellation; analyze can retry a saved capture after explicit confirmation. Settings show the connected vision model and its reported quota multiplier; remaining account quota is not fabricated.

## Coding agents

**Settings > Coding agents** offers opt-in native screenshot tools through a bundled local stdio MCP server, plus ready-to-copy configurations for Copilot CLI, Claude Code, VS Code, Cursor and Codex. Agent access and historical-capture access are separately disabled by default. Enabled agents use Hotshot's existing capture/output/analysis settings; they cannot change settings, execute commands, or control the mouse/keyboard.

The repository marketplace includes capture, visual-verification and history-search skills. Returned screenshots may be sent to the requesting coding agent's model provider. See the [agent integration guide](agents/README.md) for permissions, installation commands, tools and limits.

Agent tools also offer saved-image region reads, local OCR with word coordinates, pixel comparisons and bounded request activity. Allowed-app restrictions filter live targets and historical captures and disable broad region/monitor capture; separate toggles can disable those capture modes even without an app list. These restrictions do not remove occluding windows from visible-screen pixels.

**Test connection with generated image** exercises the bundled stdio companion and native pipe without screen/history/model access, even while agent access is off. Settings shows actual native client process IDs/names and last-seen state—not inferred coding-workspace/model identities. Optionally require a timed grant for this app run and/or native Allow once/Deny approval for content requests. No grant survives restart. Status/activity/synthetic tests do not require content approval. Selected flattened screenshots can be packaged as a local evidence ZIP with a minimal manifest; no originals, AI/OCR text, absolute paths or upload is included.

The release keeps a self-contained .NET runtime for both executable processes for dependable deployment. The MCP host targets plain .NET rather than the Windows SDK projection, and packaging removes only the byte-identical in-process Copilot alias after hash verification; the app explicitly uses stdio. An SDK-compatible external runtime can be selected, and developer packaging supports `-IncludeCopilotRuntime:$false`; the default public update channel still bundles it. No component is downloaded automatically.

**About Hotshot > Show diagnostics** reports startup time, current memory, installed-file size, library health, interrupted-recording state, AI/provider status and agent/update state. Diagnostics stay local. Packages remain unsigned by choice; no signing identity or secret is required.

## Output naming

The default captures folder is `Pictures\Hotshot`; change it using the native folder picker under **General > Captures folder**. Canceling leaves the current folder unchanged. The default pattern, `{yyyy}\{MM}\{timestamp}`, produces paths such as `2026\10\2026-10-09_09-15-30.png`. Configure separate screenshot and recording/GIF patterns under **File naming**, with a live preview and token dropdowns showing example results. Choosing a token inserts it at the cursor or replaces the selected text.

Supported tokens include date/time (`{yyyy}`, `{MM}`, `{dd}`, `{HH}`, `{mm}`, `{ss}`, `{fff}`, `{timestamp}`, `{date}`, `{time}`, `{unix}`, `{now:yyyyMMdd}`), context (`{type}`, `{app}`, `{title}`, `{monitor}`, `{width}`, `{height}`), and identifiers (`{counter:4}`, `{rand:8}`, `{guid}`, `{computer}`, `{user}`). Separators create folders, extensions are automatic, invalid filename characters are sanitized, and collisions receive numeric suffixes.

Settings, history, thumbnails and logs are stored under `%AppData%\Hotshot`. History trimming does not delete saved captures. With file saving disabled, captures are held in temporary files for clipboard/history and removed when their history entries are trimmed.

## OneDrive settings across machines

When a local OneDrive folder is available, Hotshot automatically stores portable preferences in **`<OneDrive>\Apps\Hotshot\settings.json`**. Sign into the same OneDrive account on your other Windows machines and install a current Hotshot release; the shared file is loaded there. You can also choose the local root of a shared OneDrive folder under **General > Settings across devices**. Hotshot does not share the folder with other people or change OneDrive permissions.

Theme, hotkeys, filename patterns, capture/recording options, local-OCR preferences, export presets and saved searches roam. Absolute capture/runtime paths, window positions/splitter width, microphone device IDs, filename counters, first-run state, project baseline IDs, cloud-AI enable/review choices and all coding-agent permissions stay device-local. Enabling agent or cloud-image access on one machine never grants it on another. Captures, history, originals, logs and credentials are not synchronized by this feature.

The existing `%AppData%\Hotshot\settings.json` remains a full local cache/device-override file. `settings-sync.json` in that same directory records this machine's selected location and last shared baseline. Without OneDrive or while its files cannot be read, the local cache stays active and edits are queued for a later retry. Hotshot never creates a fake OneDrive root or overwrites an invalid/unavailable shared document with defaults. The OneDrive desktop client—not Hotshot—uploads/downloads the file; folder availability is not a guarantee that cloud transfer has completed.

Valid incoming changes reload every few seconds when Hotshot is idle and Settings is not actively being edited. **Reload shared preferences** applies them explicitly after pending edits are resolved. Independent field edits are merged; conflicting same-field edits are preserved locally and require an explicit resolution. The shared file keeps a backup, and invalid documents are preserved separately before explicit replacement. OneDrive may create extra conflict-copy JSON files after simultaneous offline edits: review/move those copies before resolving, or use OneDrive version history. This is not a distributed locking service.

**Use local preferences only** stops settings roaming on this device without deleting the OneDrive file. **Choose another OneDrive folder** selects the root whose `Apps\Hotshot\settings.json` should be used. The native page shows the exact shared-file path, local cache and current status.

## Build and package

Requires Windows and the .NET 10 SDK, not Visual Studio.

```powershell
dotnet tool restore
dotnet build src\Hotshot.App\Hotshot.App.csproj -p:Platform=x64
dotnet build src\Hotshot.App\Hotshot.App.csproj -p:Platform=ARM64
pwsh build\package.ps1 -Version 0.1.0 -Runtime win-x64
pwsh build\package.ps1 -Version 0.1.0 -Runtime win-arm64
```

The pinned local `vpk` tool creates installers, managed portable ZIPs, full/delta packages and architecture-specific feeds under `artifacts\releases\win-x64` and `artifacts\releases\win-arm64`. Published binaries are versioned under `artifacts\publish\<version>\<runtime>` so packaging does not replace a running development build. ARM64 can be cross-built on x64; runtime validation on a real ARM64 machine is still required.

## Release management

[Release Please](https://github.com/googleapis/release-please) opens version/changelog pull requests from Conventional Commits on `main`: use `feat:`, `fix:`, and `BREAKING CHANGE:` where appropriate. It keeps `version.txt`, `Directory.Build.props`, `CHANGELOG.md`, `.release-please-manifest.json` and both agent plugin manifests in sync.

Merge the release PR to create a tagged draft release. The same workflow tests the code, builds both architectures, generates Velopack delta packages when a previous release exists, uploads all installers/portable/update-feed assets, and only then publishes the release as latest. Clients never discover an incomplete draft. This combined workflow works with the repository's `GITHUB_TOKEN` without requiring a personal token to trigger a second release workflow.

For bootstrap or repair, manually dispatch **Release Please and publish** with an existing tagged version (for example, `0.1.0`). **Build and test** validates x64/ARM64 on pushes and PRs. GitHub must allow Actions to create pull requests; no PAT, signing certificate or private token belongs in source control.

```powershell
dotnet test --project tests\Hotshot.Core.Tests\Hotshot.Core.Tests.csproj
dotnet test --project tests\Hotshot.Capture.Tests\Hotshot.Capture.Tests.csproj
dotnet test --project tests\Hotshot.Gif.Tests\Hotshot.Gif.Tests.csproj
dotnet test --project tests\Hotshot.Editor.Tests\Hotshot.Editor.Tests.csproj
dotnet run --project tests\Hotshot.Recording.Harness\Hotshot.Recording.Harness.csproj -- --window-only
dotnet run --project tests\Hotshot.Recording.Harness\Hotshot.Recording.Harness.csproj -- --window-screenshot
dotnet run --project tests\Hotshot.Recording.Harness\Hotshot.Recording.Harness.csproj -- --soak-seconds 120
dotnet run --project tests\Hotshot.Library.Harness\Hotshot.Library.Harness.csproj
dotnet run --project tests\Hotshot.Updates.Harness\Hotshot.Updates.Harness.csproj
dotnet run --project tests\Hotshot.Descriptions.Harness\Hotshot.Descriptions.Harness.csproj
dotnet run --project tests\Hotshot.Workbench.Harness\Hotshot.Workbench.Harness.csproj -p:Platform=x64 -- --ocr
pwsh tests\Hotshot.App.Smoke.ps1 -Exe <path-to-built-Hotshot.exe>
```

Tests use xUnit and Microsoft Testing Platform. The window-only harness records its own synthetic windows, verifies PNG/clipboard, pause, target closing, GPU/CPU encoding and MP4-to-GIF, and writes media under `%TEMP%\hotshot-harness`. It changes the clipboard to a synthetic test screenshot. Omitting `--window-only` additionally records the monitor and exercises microphone capture; only do that when the desktop/audio are safe to capture.

The update harness calls the actual static GitHub release source for both architectures and validates version-pinned full/delta URLs using headers only. It requires internet access but does not install, apply an update or touch user data.
The description harness verifies Windows PNG metadata and pixel preservation with a generated image. Add `-- --copilot` to submit only that generated image through the production SDK provider; this requires Copilot sign-in and uses model quota, but never reads or submits a real screenshot.
The workbench harness renders generated text and annotations to verify local OCR, solid-redaction layering, styled PNG and JPEG output without capturing the desktop or sending data to any provider.

The app smoke test exercises native settings, global shortcuts, capture selection, filename tokens, recording controls, history and GIF conversion. It temporarily replaces and then restores settings/history, captures the real desktop locally into its own temporary directory, and changes the clipboard. Close Hotshot before running it and use a desktop that is safe to capture. Use an unmanaged Debug/publish build for this smoke so automatic updates cannot restart a fixture.
Pass `-VerifyStartup` only with a Release build to additionally exercise Start with Windows; the original Hotshot Run entry is restored afterward.
Pass `-SettingsControlsOnly` to exercise folder selection/cancellation and filename-token dropdown examples/insertion without taking captures or changing the clipboard.
Pass `-SettingsCaptureOnly` to verify that Settings can be selected and captured as a window, with the correct pixels and clipboard output, without capturing other desktop areas.
Pass `-HistoryNavigationOnly` to verify filename-only browsing and automatic editor loading with generated PNG fixtures; it does not capture the desktop or change the clipboard.
Pass `-HeaderSearchOnly` to check embedded title-bar search, caption-button separation, resizing, theme changes and search navigation without running unrelated capture or clipboard checks.
Pass `-WorkbenchOnly` to verify physical-pixel zoom, pinned windows, multi-selection and measured native visual comparisons using generated images. Supply a generated MP4 via `-VideoFixture` to verify in-app video preview/frame extraction too.
Pass `-AgentSettingsOnly` to verify native agent configuration and optional review-mode persistence without contacting any AI provider.

Consolidation coverage includes a 10,000-entry persistence/search/restart benchmark, interrupted-journal and archive-path recovery tests, a two-minute synthetic-window recording soak with pause/resize and memory checks, unobscured capture under a synthetic occluder, mixed DPI-awareness HWNDs, native per-request consent, generated connection testing, and UI Automation checks for comparison/pin/keyboard controls. Physical mixed-scale monitor movement, HDR-display fidelity, Narrator user experience and execution on real ARM64 hardware require those environments; cross-builds and synthetic/UIA checks are not claimed as substitutes.
