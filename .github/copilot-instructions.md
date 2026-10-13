# Hotshot repository instructions

Hotshot is a tray-resident, unpackaged WinUI 3 screenshot/recording application, shipping self-contained for `win-x64` and `win-arm64`. Read `AGENTS.md` and `CONTRIBUTING.md` alongside these instructions; `SECURITY.md` defines the integration and data boundaries.

## Build, test, and checks

Run native commands on Windows, from the repository root in PowerShell. Use the SDK selected by `global.json` (.NET 10, servicing-patch roll-forward, Microsoft Testing Platform). Visual Studio is not required. Native builds include substantial dependencies; use a spacious, task-owned artifacts directory and keep WinUI builds sequential.

```powershell
$out = Join-Path $env:TEMP ("HotshotChecks-" + [guid]::NewGuid().ToString('N'))

# App, either architecture; full x64 solution includes the integration harnesses.
dotnet build src\Hotshot.App\Hotshot.App.csproj -c Release -p:Platform=x64 --artifacts-path "$out\build-x64" -m:1
dotnet build src\Hotshot.App\Hotshot.App.csproj -c Release -p:Platform=ARM64 --artifacts-path "$out\build-arm64" -m:1
dotnet build Hotshot.slnx -c Release -p:Platform=x64 --artifacts-path "$out\solution-x64" -m:1

dotnet test --project tests\Hotshot.Core.Tests\Hotshot.Core.Tests.csproj --artifacts-path "$out\core"
dotnet test --project tests\Hotshot.Capture.Tests\Hotshot.Capture.Tests.csproj --artifacts-path "$out\capture"
dotnet test --project tests\Hotshot.Editor.Tests\Hotshot.Editor.Tests.csproj --artifacts-path "$out\editor"
dotnet test --project tests\Hotshot.Gif.Tests\Hotshot.Gif.Tests.csproj --artifacts-path "$out\gif"

# One test, or one class. These are the xUnit/Microsoft Testing Platform selectors.
dotnet test --project tests\Hotshot.Core.Tests\Hotshot.Core.Tests.csproj --artifacts-path "$out\core" --filter-method Hotshot.Core.Tests.FileNameTemplateTests.DefaultPatternProducesYearMonthTimestamp
dotnet test --project tests\Hotshot.Core.Tests\Hotshot.Core.Tests.csproj --artifacts-path "$out\core" --filter-class Hotshot.Core.Tests.FileNameTemplateTests
```

The .NET lint gate runs during build: `Directory.Build.props` enforces C# 14, nullable, .NET 10 Recommended analyzers, code-style checks, deterministic compilation and warnings-as-errors. `.editorconfig` additionally enables `CA2012`/`CA2016`. Core and MCP explicitly disallow unsafe blocks. xUnit tests forward `TestContext.Current.CancellationToken`.

For real native UI changes, use the generated-profile Product harness, not the installed application:

```powershell
dotnet build tests\Hotshot.Product.Harness\Hotshot.Product.Harness.csproj -c Release -p:Platform=x64 --artifacts-path "$out\ui" -m:1
pwsh tests\Hotshot.Product.Harness\Invoke-Acceptance.ps1 `
  -Executable "$out\ui\bin\Hotshot.Product.Harness\release_win-x64\Hotshot.Product.Harness.exe" `
  -OutputDirectory "$out\evidence" -ExpectedArchitecture x64 -SoftwareRenderer
```

Use `Platform=ARM64`, `release_win-arm64` and `ExpectedArchitecture=arm64` on an actual ARM64 Windows runner. `.github\workflows\native-ui.yml` runs both native architectures. Cross-compilation, generated scRGB references and automated accessibility peers do not establish physical HDR/mixed-DPI or human Narrator acceptance.

Other targeted checks:

```powershell
dotnet run --project tests\Hotshot.Recording.Harness\Hotshot.Recording.Harness.csproj --artifacts-path "$out\recording" -- --window-only
dotnet run --project tests\Hotshot.Agents.Harness\Hotshot.Agents.Harness.csproj -c Release -p:Platform=x64 --artifacts-path "$out\agents"
pwsh tests\Hotshot.Agents.Manifests.ps1
dotnet run --project tests\Hotshot.Mcp.Harness\Hotshot.Mcp.Harness.csproj -c Release --artifacts-path "$out\mcp-contract" -- <absolute-path-to-Hotshot.Mcp.exe>
```

`--window-only` records generated windows with audio disabled but changes the clipboard. The Recording harness's default mode can capture the monitor and microphone. Native smoke scripts temporarily replace the real profile; consult `CONTRIBUTING.md` before running them. Adding `-- --copilot` to the Descriptions harness sends its generated image to a real provider and consumes quota.

The documentation site is a separate Node.js 24+ project:

```powershell
Push-Location website
npm ci
npm test                         # Build plus source/link/search tests.
npx playwright install chromium webkit
npm run test:browser             # Desktop/mobile and accessibility checks.
npm run dev                     # http://127.0.0.1:4173/hotshot/
Pop-Location
```

Packaging and icon regeneration use the existing scripts:

```powershell
pwsh build\make-icons.ps1
dotnet tool restore
pwsh build\package.ps1 -Runtime win-x64 -Edition Full -OutputRoot "$out\packages" -BuildArtifactsPath "$out\package-build"
```

Packaging defaults to `version.txt`; use `win-arm64`/`Core` for the other runtime/edition. `dotnet-tools.json` pins Velopack's `vpk`.

## Architecture

- **Resident app and capture flow:** `AppController` composes the tray/message window, hotkeys, capture, recording, background processors, bridge and updater, and coordinates shutdown. `AppServices` owns shared settings/history services, dispatcher posting and observed asynchronous operations. Hotkeys and agent requests converge on `CaptureCoordinator` and `OutputPipeline`; the pipeline handles naming, clipboard, atomic image publication, thumbnails and history before queuing OCR/AI. Recording/GIF outputs enter history through `AddVideoAsync`.
- **Portable domain versus native adapters:** Core is pure `net10.0`: settings/roaming merge, filename tokens, history/search/journal, policies and models. Capture supplies GDI/clipboard/monitor APIs; Recording uses Windows.Graphics.Capture, D3D11/Vortice, Media Foundation and WASAPI/NAudio; GIF decodes media and writes managed GIFs. Editor provides the reusable Win2D annotation document/surface. App supplies native windows and orchestration rather than moving Windows dependencies into Core.
- **Persistence and editing:** `HistoryStore` combines checkpoints with an append journal and invalidates its search index after metadata changes. The workspace pairs saved images with private originals and annotation sidecars. `ScreenshotDescriptions` serializes analysis through a bounded queue and coordinates image/metadata writes with editor saves, including image-revision changes.
- **Agent and cloud boundaries:** `Hotshot.Mcp` is a separate plain-`net10.0` stdio companion. Its same-user/same-session named-pipe bridge reuses native capture and checks permissions before execution and before returning content. Copilot descriptions use an owned stdio runtime in Empty mode with tools/hooks/skills disabled. GitHub sign-in uses a separate tool-disabled authentication session on the same private SDK profile; runtime/component management and account changes coordinate with the description worker and update idle gates.
- **Website:** `website\src\build.mjs` produces static pages from maintained Markdown guides; `content.mjs` maps headings to routes. Shared templates, CSS and browser JS are inlined. GitHub Pages serves the `/hotshot/` project path; native application builds do not build the site.

## Repository-specific conventions

- Preserve silent captures: never automatically open the editor or select the latest capture. Clipboard delivery precedes disk saving; tray activation opens/focuses the filename-based workspace without changing selection. Settings use native adaptive cards, automatic validated persistence, and title-bar search and navigation toggle.
- Capture geometry is in physical pixels, including negative virtual-screen coordinates. Retain Per-Monitor V2 thread/window DPI handling and target revalidation. Unobscured window capture uses float16 scRGB and produces an SDR rendition; protected/unavailable targets fail rather than silently switching to desktop capture.
- Win32 bindings use source-generated `[LibraryImport]` in project-specific `Native*.cs`; COM/D3D/MF use Vortice. GDI bitmap/DC ownership uses `SafeHandle`; media workers must finish before their native dependencies are released.
- Reusable libraries build UI in code, without `.xaml`, because unpackaged PRI merging is fragile. XAML is allowed in App. Product/Agents harnesses disable their duplicate Windows App SDK initializers and explicitly initialize the App module; Product uses its own `ProductApp.xaml` to avoid application resource collisions.
- `AppSettings` drafts and portable snapshots are cloned, validated and merged; avoid mutating an original while projecting preferences. `PortableSettings.From`/`ApplyTo` define the OneDrive whitelist, with three-way merge and preserved conflicts/invalid documents. Paths, microphone IDs, counters, layouts, baseline IDs, cloud-AI enable/review and all agent permissions remain local.
- `%AppData%\Hotshot` holds settings/history/thumbnails/originals; `%LocalAppData%\Hotshot` is installer-owned. A normal development launch still uses the real local profile. `HOTSHOT_SETTINGS_SYNC_MODE=local` disables roaming only; an artifacts directory does not isolate app data. Prefer harnesses that explicitly construct isolated `AppPaths`.
- File publication uses unique same-directory staging, cancellation before replacement and preservation of the primary error if cleanup fails. Preserve recovery journals and invalid data instead of resetting a profile. Route background UI failures through the existing `AppServices` observation/notification boundary.
- `CaptureSource.AppName` is authorization provenance, not a workflow label. Preserve it through frame extraction, trimming and other derivatives. Unknown sources require per-image AI approval; known excluded apps remain denied after approval. Redaction flattens exported pixels but does not erase private originals/sidecars; remove stale OCR/description metadata when saving redactions.
- Authentication, image uploads and agent/history access are separate permissions. GitHub PKCE sign-in must not enable uploads or roam credentials; refuse plaintext storage and never log authentication exceptions/URLs/tokens. Full bundles Copilot; Core can install a verified optional component. An explicitly selected external runtime takes priority; managed components validate pinned SDK/architecture/SHA256 compatibility.
- Preserve the intentionally pinned preview Windows Community Toolkit dependency: older stable releases introduce App SDK 1.6 targets incompatible with the split App SDK 2.x packages. The App assembly name is `Hotshot`, including friend-assembly references.
- Edit Markdown documentation sources, not `website\dist`. Update `website\src\content.mjs` when source headings change. Preserve the `/hotshot/` base path, self-hosted assets, reduced motion, and orange/coral-and-ink theme through shared `--cp-*` color tokens.
- Use Conventional Commits. Release Please owns `version.txt`, MSBuild version, changelog, release manifest and plugin versions. Full/Core have distinct architecture-specific update channels. Publish a draft only after both RID asset/feed sets are uploaded; restart updates only when capture/recording/conversion/AI/account/component/editor/settings work is idle.
