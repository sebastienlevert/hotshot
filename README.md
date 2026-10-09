# Hotshot

A native WinUI 3 screenshot and screen-recording utility for Windows 10 (19041+) and Windows 11, built with .NET 10. It lives in the tray rather than leaving a main window open.

The Hotshot logo pairs a white capture viewfinder with a lightning bolt on an amber-to-coral tile. Taskbar and tray icons include sizes from 16 to 256 pixels; recording switches the tray to a dark tile with a red recording dot. Regenerate the assets with `pwsh build\make-icons.ps1`.

[Download the latest release](https://github.com/sebastienlevert/hotshot/releases/latest) · [Changelog](CHANGELOG.md) · [Report an issue](https://github.com/sebastienlevert/hotshot/issues)

## Use

Download **Hotshot-win-x64-Setup.exe** or **Hotshot-win-arm64-Setup.exe** from GitHub Releases and run it. Installation is per-user and needs no administrator privileges. No separate .NET or Windows App SDK installation is required. The first launch opens settings; closing a settings/editor window leaves the app running in the tray.

Managed portable bundles are also available. Extract the entire `Hotshot-win-<architecture>-Portable.zip` to a permanent writable folder; do not move individual files out of the bundle. Earlier development ZIPs and raw `dotnet publish` folders are unmanaged and cannot update themselves: install a current release once to enable updates.

### Automatic updates

Installed and Velopack-managed portable builds check this public repository's stable GitHub Releases shortly after startup and hourly thereafter. New versions download and are verified in the background, using separate `win-x64` and `win-arm64` static release feeds. This avoids GitHub REST API quotas, and package URLs stay pinned to the discovered release version. No GitHub account or token is required.

Once ready, Hotshot updates silently and restarts in the tray when it is safe: no capture, recording, GIF conversion, editor operation, unsaved annotations/settings, or active editor/settings window. Work in progress is never interrupted. Offline/failed checks are logged and retried automatically; prepared updates are reconsidered after startup using those same safety checks. Secondary launches never force-apply an update before the single-instance/idle checks. **About Hotshot > Automatic updates** shows status and offers an optional Check now button.

Settings, history and originals stay in `%AppData%\Hotshot`, outside the installer-owned application directory. Release packages are currently unsigned, so Windows may show a first-install SmartScreen warning; no signing credentials are stored in this repository.

| Default shortcut | Action |
|---|---|
| Print Screen | Select a region or click a window |
| Ctrl+Print Screen | Capture the monitor under the cursor |
| Shift+Print Screen | Capture the full virtual screen (all monitors) |
| Ctrl+Shift+Print Screen | Start/stop screen recording |

Settings use a PowerToys-inspired adaptive sidebar, searchable modules, grouped setting cards, and automatic saving. Choose Windows default, light or dark under **General > App theme**. Invalid edits remain unapplied with an inline explanation; there is no global Save button.

All shortcuts are configurable under **Keyboard shortcuts**: click a shortcut, press the new combination, and Apply or Clear it. Duplicate/invalid shortcuts are rejected, and registration conflicts are shown. If Print Screen launches Snipping Tool, disable that option in Windows keyboard settings or choose another shortcut. In the selection overlay, **Space** captures/selects the monitor under the cursor and **Escape** cancels.

Screenshots are saved as PNG and copied to the clipboard as both an image and PNG data before disk saving. Captures are silent: no preview/editor window opens automatically, including for older settings profiles.

Click the tray icon to open or focus **Editor and history**. It starts with a thumbnail history pane and an empty editor, not the most recent screenshot. Select a screenshot to add arrows, shapes, text, freehand ink, highlights, numbered steps or pixelated regions, or crop it. Undo/redo covers annotations and crops. **Save** updates the PNG while retaining the pristine original and an editable annotation sidecar under `%AppData%\Hotshot\originals`; **Copy** copies the edited image without saving it. Switching or closing prompts before discarding unsaved edits. Video entries have Open/Copy/Convert to GIF actions. Right-click the tray for capture commands, recording controls, settings, the captures folder, and exit.

Recording produces H.264 MP4 with optional system audio and/or microphone audio. Turn both audio options off for silent video. A countdown and recording control window provide pause/resume and stop/save; closing the recording control stops and saves. Window targets and single-monitor regions are supported. Regions cannot span monitors. A closed target stops recording, including when paused.

Use **Convert to GIF** on an MP4 in history, enable automatic GIF creation, or bind the Record as GIF shortcut. GIF conversion runs in the background, supports frame rate, width, dithering and looping, and keeps the original MP4. GIFs cannot contain audio and are limited to 256 colors per palette, so photographic/complex gradients are necessarily lossy. MP4/GIF clipboard copies are files rather than static images.

**Start with Windows** uses the current user's Run registry entry and is enabled by default in Release builds. Debug builds never modify that entry. Keep a portable installation in its permanent location; moving it requires running the new copy and changing the startup setting to refresh its path. Exit via the tray menu or `Hotshot.exe --exit`.

## Output naming

The default captures folder is `Pictures\Hotshot`. The default pattern, `{yyyy}\{MM}\{timestamp}`, produces paths such as `2026\10\2026-10-09_09-15-30.png`. Configure separate screenshot and recording/GIF patterns, with a live preview and token list, under **File naming**.

Supported tokens include date/time (`{yyyy}`, `{MM}`, `{dd}`, `{HH}`, `{mm}`, `{ss}`, `{fff}`, `{timestamp}`, `{date}`, `{time}`, `{unix}`, `{now:yyyyMMdd}`), context (`{type}`, `{app}`, `{title}`, `{monitor}`, `{width}`, `{height}`), and identifiers (`{counter:4}`, `{rand:8}`, `{guid}`, `{computer}`, `{user}`). Separators create folders, extensions are automatic, invalid filename characters are sanitized, and collisions receive numeric suffixes.

Settings, history, thumbnails and logs are stored under `%AppData%\Hotshot`. History trimming does not delete saved captures. With file saving disabled, captures are held in temporary files for clipboard/history and removed when their history entries are trimmed.

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

[Release Please](https://github.com/googleapis/release-please) opens version/changelog pull requests from Conventional Commits on `main`: use `feat:`, `fix:`, and `BREAKING CHANGE:` where appropriate. It keeps `version.txt`, `Directory.Build.props`, `CHANGELOG.md` and `.release-please-manifest.json` in sync.

Merge the release PR to create a tagged draft release. The same workflow tests the code, builds both architectures, generates Velopack delta packages when a previous release exists, uploads all installers/portable/update-feed assets, and only then publishes the release as latest. Clients never discover an incomplete draft. This combined workflow works with the repository's `GITHUB_TOKEN` without requiring a personal token to trigger a second release workflow.

For bootstrap or repair, manually dispatch **Release Please and publish** with an existing tagged version (for example, `0.1.0`). **Build and test** validates x64/ARM64 on pushes and PRs. GitHub must allow Actions to create pull requests; no PAT, signing certificate or private token belongs in source control.

```powershell
dotnet test --project tests\Hotshot.Core.Tests\Hotshot.Core.Tests.csproj
dotnet test --project tests\Hotshot.Gif.Tests\Hotshot.Gif.Tests.csproj
dotnet test --project tests\Hotshot.Editor.Tests\Hotshot.Editor.Tests.csproj
dotnet run --project tests\Hotshot.Recording.Harness\Hotshot.Recording.Harness.csproj -- --window-only
dotnet run --project tests\Hotshot.Updates.Harness\Hotshot.Updates.Harness.csproj
pwsh tests\Hotshot.App.Smoke.ps1 -Exe <path-to-built-Hotshot.exe>
```

Tests use xUnit and Microsoft Testing Platform. The window-only harness records its own synthetic windows, verifies PNG/clipboard, pause, target closing, GPU/CPU encoding and MP4-to-GIF, and writes media under `%TEMP%\hotshot-harness`. It changes the clipboard to a synthetic test screenshot. Omitting `--window-only` additionally records the monitor and exercises microphone capture; only do that when the desktop/audio are safe to capture.

The update harness calls the actual static GitHub release source for both architectures and validates version-pinned full/delta URLs using headers only. It requires internet access but does not install, apply an update or touch user data.

The app smoke test exercises native settings, global shortcuts, capture selection, filename tokens, recording controls, history and GIF conversion. It temporarily replaces and then restores settings/history, captures the real desktop locally into its own temporary directory, and changes the clipboard. Close Hotshot before running it and use a desktop that is safe to capture. Use an unmanaged Debug/publish build for this smoke so automatic updates cannot restart a fixture.
Pass `-VerifyStartup` only with a Release build to additionally exercise Start with Windows; the original Hotshot Run entry is restored afterward.
