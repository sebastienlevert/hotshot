# Contributing to Hotshot

Thanks for helping make Hotshot a better native Windows capture tool. Documentation, bug reports, accessibility feedback, regression tests, and focused code changes are all welcome.

For installation and everyday use, start with the [README](README.md). This guide covers working on the repository.

## Before you start

For a bug, [open an issue](https://github.com/sebastienlevert/hotshot/issues) with your Hotshot version, Windows build, x64/ARM64 architecture, reproduction steps, and expected/actual behavior. Include display scaling or audio-device information when relevant.

For a substantial feature or architectural change, discuss the problem and proposed user experience in an issue before implementing it. Small fixes and documentation improvements can go straight to a pull request.

Review diagnostics and screenshots before attaching them. Remove private paths, account information, credentials, and sensitive pixels. Do not upload a whole application-data folder or private originals.

## Development prerequisites

| Requirement | Why |
|---|---|
| Windows 10 build 19041+ or Windows 11 | The app uses WinUI, Windows capture APIs, and Media Foundation. |
| .NET 10 SDK | Builds and tests the projects. Visual Studio is optional, not required. |
| Git | Clones the repository and prepares changes. |
| PowerShell 7 (`pwsh`) | Runs icon generation, native UI smoke checks, and packaging scripts. |

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and [PowerShell](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows) if needed. Plan for substantial free disk space: self-contained x64/ARM64 builds include native dependencies and the Copilot runtime.

The repository's [global.json](global.json) selects **Microsoft Testing Platform**; it does not pin an SDK version. [dotnet-tools.json](dotnet-tools.json) pins the `vpk` packaging tool. A Copilot account is needed only for optional live AI checks, not ordinary builds or local image tests.

## Get the source and build

If you do not have repository write access, fork it first and clone your fork instead of the upstream URL below.

```powershell
git clone https://github.com/sebastienlevert/hotshot.git
Set-Location hotshot
dotnet build src\Hotshot.App\Hotshot.App.csproj -p:Platform=x64
```

The build restores its NuGet dependencies. There is no separate frontend package install.

Build the other architecture or a release configuration with:

```powershell
dotnet build src\Hotshot.App\Hotshot.App.csproj -p:Platform=ARM64
dotnet build src\Hotshot.App\Hotshot.App.csproj -c Release -p:Platform=x64
```

ARM64 can be cross-built on x64. A successful cross-build does **not** establish runtime behavior on physical ARM64 hardware.

### Run a development copy safely

> [!WARNING]
> A normal development launch uses the real `%AppData%\Hotshot` profile. Debug mode is not an isolated sandbox. Back up settings/history and private originals before experimenting with persistence, migration, cleanup, or recovery.

Finish any recording or unsaved edits, then exit the installed tray app before launching a development copy. Otherwise, Hotshot's single-instance handling can forward your command to the already-running version.

To keep development preference changes out of your real OneDrive folder, set the process-local override before launching:

```powershell
$env:HOTSHOT_SETTINGS_SYNC_MODE = 'local'
dotnet run --project src\Hotshot.App\Hotshot.App.csproj -p:Platform=x64
```

This disables OneDrive settings roaming for that launch; **it does not isolate the local profile**. The harnesses described below provide isolation where noted. Debug builds do not modify the Windows startup registration, and unmanaged development builds cannot auto-update themselves.

After exiting the development app, remove the override from that terminal before starting your normal installed copy:

```powershell
Remove-Item Env:\HOTSHOT_SETTINGS_SYNC_MODE
```

## Repository map

| Path | Responsibility |
|---|---|
| `src\Hotshot.Core` | Pure `net10.0` settings, portable preference merge/persistence, filename tokens, history/search, hotkeys, and shared models. No Windows APIs. |
| `src\Hotshot.Capture` | GDI/window capture, image encoding, clipboard, monitor/window enumeration, and capture pixel handling. |
| `src\Hotshot.Recording` | Windows.Graphics.Capture, D3D11/Vortice, Media Foundation H.264/AAC MP4 encoding, and WASAPI/NAudio audio. |
| `src\Hotshot.Gif` | MP4 decoding, quantization, and managed GIF writing. |
| `src\Hotshot.Editor` | Reusable Win2D annotation UI, document model, rendering, and export. |
| `src\Hotshot.App` | Tray lifecycle, global shortcuts, capture overlay, WinUI settings/workspace, AI descriptions, native agent bridge, and updates. |
| `src\Hotshot.Mcp` | Separate stdio MCP companion; talks to the native app through a local named pipe. |
| `tests` | Unit suites, isolated/native integration harnesses, media fixtures, and UI Automation smoke checks. |
| `agents` | Coding-agent documentation, plugin/marketplace assets, launchers, and portable skills. |
| `build` | Logo/icon generation and installer, managed-portable, and update-feed packaging. |
| `.github\workflows` | x64/ARM64 CI and Release Please/package/publish automation. |

Keep portable logic in Core, Windows/media work in its corresponding library, and app coordination in App. See [AGENTS.md](AGENTS.md) for the repository's concise implementation guide.

## Design and implementation rules

### Preserve the user experience

Captures are silent: no automatic preview/editor window. Clipboard delivery comes before disk saving. Tray activation opens or focuses the filename-based workspace without selecting the newest capture.

Keep settings native and adaptive, with title-bar search, grouped cards, and automatic validated persistence. Prefer existing Windows Community Toolkit controls and helpers over inventing parallel UI patterns. Do not block the UI thread with encoding, disk access, or long-running work.

Show actionable errors. A protected, unavailable, or changed capture target must not turn into a different screenshot or a success-shaped fallback.

### Follow the codebase

Use the latest configured C#, nullable annotations, file-scoped namespaces, and sealed classes by default. Reuse existing helpers and keep changes scoped to the requested behavior.

Win32 interop uses source-generated `[LibraryImport]` in project-specific `Native*.cs` files; COM, D3D, and Media Foundation use Vortice. Libraries must not contain `.xaml` files: build reusable library UI in code because unpackaged PRI merging is fragile. XAML is allowed in the app.

Capture coordinates are **physical pixels**. Maintain Per-Monitor V2 DPI behavior, including negative virtual-screen coordinates, mixed display scales, and target changes between enumeration and capture.

### Protect data and permissions

User data belongs in `%AppData%\Hotshot`, not the installer-owned `%LocalAppData%\Hotshot` directory. Keep persistence atomic, recoverable, and backward-compatible; never repair a settings/history problem by silently resetting user data.

Portable OneDrive preferences are a whitelist. Capture/runtime paths, microphone IDs, counters, layouts, project baseline IDs, cloud-AI enable/review choices, and **all agent permissions** stay local. Invalid shared files and conflicting edits must remain recoverable. A local OneDrive file is not proof of completed cloud transport.

Cloud image analysis and agent/history access are opt-in. Do not enable them through migration, roaming settings, a test fixture, or an agent request. Review/redaction must not leak private originals or old text metadata through a flattened export.

Do not add credentials, private screenshots, app-data dumps, generated binaries, or machine-specific paths to commits. Keep automatic update restarts behind the existing idle/unsaved-work checks.

### Respect compatibility decisions

Windows Community Toolkit's preview dependency is intentionally pinned for the split WinUI/Windows App SDK 2.x packages. Older stable Toolkit packages introduce conflicting App SDK 1.6 build targets; do not replace the pin without proving compatibility.

The MCP companion targets plain `net10.0` to avoid unnecessary Windows SDK projection payload. The app uses the bundled Copilot runtime over stdio. Packaging removes an in-process-only alias only after verifying that its bytes match; do not replace this with an unchecked binary deletion or shared-runtime shortcut.

## Test the change you made

Start with the smallest suite or harness that covers the behavior. Add a regression test for a bug fix, then expand coverage if the change crosses project boundaries.

### Unit tests

Run these from the repository root. The `--project` syntax is intentional for Microsoft Testing Platform:

```powershell
dotnet test --project tests\Hotshot.Core.Tests\Hotshot.Core.Tests.csproj
dotnet test --project tests\Hotshot.Capture.Tests\Hotshot.Capture.Tests.csproj
dotnet test --project tests\Hotshot.Editor.Tests\Hotshot.Editor.Tests.csproj
dotnet test --project tests\Hotshot.Gif.Tests\Hotshot.Gif.Tests.csproj
```

Core covers portable logic such as settings merging, naming, and library persistence. Capture, Editor, and GIF suites cover their corresponding image/document/media behavior.

### Targeted integration harnesses

Use an interactive Windows desktop for native capture/UI checks.

| Area | Command | Important side effects |
|---|---|---|
| Library persistence/search benchmark | `dotnet run --project tests\Hotshot.Library.Harness\Hotshot.Library.Harness.csproj -c Release` | Generated data, not the user's library. |
| Native agent bridge and settings reload | `dotnet run --project tests\Hotshot.Agents.Harness\Hotshot.Agents.Harness.csproj -c Release -p:Platform=x64` | Isolated profile, generated images, and its own synthetic window/region. No provider upload. |
| Recording pipeline | `dotnet run --project tests\Hotshot.Recording.Harness\Hotshot.Recording.Harness.csproj -- --window-only` | Records synthetic windows and changes the clipboard to a generated capture. |
| Unobscured window capture | `dotnet run --project tests\Hotshot.Recording.Harness\Hotshot.Recording.Harness.csproj -- --window-screenshot` | Synthetic targets, occlusion, protection, and target-state checks. |
| Two-minute recording soak | `dotnet run --project tests\Hotshot.Recording.Harness\Hotshot.Recording.Harness.csproj -- --soak-seconds 120` | Synthetic window; audio disabled; checks timeline, resizing, and memory growth. |
| PNG description metadata | `dotnet run --project tests\Hotshot.Descriptions.Harness\Hotshot.Descriptions.Harness.csproj` | Generated image; no model request by default. |
| OCR, redaction, and styled export | `dotnet run --project tests\Hotshot.Workbench.Harness\Hotshot.Workbench.Harness.csproj -p:Platform=x64 -- --ocr` | Generated text/images; requires Windows OCR support. |
| Update source | `dotnet run --project tests\Hotshot.Updates.Harness\Hotshot.Updates.Harness.csproj` | Makes network requests to the real release source; does not install or apply updates. |

The recording harness **without** `--window-only` can also capture the monitor and exercise microphone input. Run that only when the desktop and audio are safe to capture. Its ordinary media output goes under `%TEMP%\hotshot-harness`.

Adding `-- --copilot` to the description harness submits only its generated test image to the production provider, requires Copilot sign-in, and consumes quota. Do not use that flag as a routine offline test.

For MCP companion build/publish commands, manifest checks, and the official client protocol harness, see [agents/README.md](agents/README.md#development). Native permission changes need bridge coverage as well as protocol/manifest coverage.

### Native UI smoke checks

Exit Hotshot first. Use an **unmanaged Debug/build output**, not an installed auto-updating release. The smoke script temporarily replaces/restores the real settings/history profile and forces local-only settings for its fixture. It is not a reason to skip a backup when changing data migrations.

For example, after the default x64 Debug build:

```powershell
pwsh tests\Hotshot.App.Smoke.ps1 -Exe src\Hotshot.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Hotshot.exe -SettingsControlsOnly
```

If you changed the build configuration, architecture, or artifacts path, use the actual `Hotshot.exe` path printed by your build instead.

| Selector | Coverage |
|---|---|
| `-SettingsControlsOnly` | Folder selection/cancel, token examples, and automatic persistence; no capture or clipboard changes. |
| `-HeaderSearchOnly` | Embedded title-bar search, compact layouts, theme changes, and search navigation. |
| `-HistoryNavigationOnly` | Filename browsing and automatic editor loading using generated PNGs. |
| `-SettingsCaptureOnly` | Selection/capture of the actual Settings window; changes the clipboard. |
| `-WorkbenchOnly` | Generated-image zoom, pinning, selection, comparison, and keyboard controls. |
| `-AgentSettingsOnly` | Native agent configuration, consent, and optional review-mode persistence without an AI request. |

With no selector, the smoke captures the real desktop locally and changes the clipboard; use a safe desktop. `-VerifyStartup` additionally tests/restores the startup registry entry and requires a Release build. `-VideoFixture` supplies an MP4 for video preview/frame-extraction coverage.

A synthetic test or cross-build is not a substitute for physical mixed-scale monitors, HDR display fidelity, Narrator usability, or real ARM64 execution. State what you actually tested and any remaining hardware gaps.

## Website and documentation

The marketing site and full documentation live under `website` and are published to [GitHub Pages](https://sebastienlevert.github.io/hotshot/). The static generator uses the maintained `README.md`, `CONTRIBUTING.md`, `agents/README.md`, and `CHANGELOG.md` as its documentation sources; edit those guides rather than generated HTML.

Website work requires **Node.js 24 or newer**. It is independent of the native app build:

```powershell
Set-Location website
npm ci
npm test
npx playwright install chromium
npm run test:browser
npm run dev
```

The preview is at `http://127.0.0.1:4173/hotshot/`. `npm run build` writes generated pages to `website\dist`, which must not be committed. The site has no runtime framework, external font/CDN dependency, account requirement, or analytics. Its CSS and browser script are inlined into each page; the local search index contains only public documentation.

Page definitions and section mappings are in `website\src\content.mjs`. When renaming a source heading, update its mapping; the build fails if a required heading disappears. Update the common templates/theme for visual changes. Preserve the `/hotshot/` project base path for routes, search, assets, sitemap, and the 404 page. `HOTSHOT_SITE_BASE` and `HOTSHOT_SITE_ORIGIN` support another deployment location; rebuild before previewing with different values.

The **Website and documentation** workflow builds and checks source links/search, runs desktop/mobile browser and accessibility checks, and deploys only from `main`. Pull requests are checked without publishing. Repository Settings > Pages must use **GitHub Actions** as the source. Changes to the guides, release version, or logo also rebuild the site.

## Submit a pull request

Create a focused branch and make the smallest complete change. Update directly related user/developer documentation and include regression coverage where applicable.

Use [Conventional Commits](https://www.conventionalcommits.org/) for commit subjects and the PR title:

```text
fix: preserve local permissions when reloading shared settings
feat: add a native capture workflow
docs: clarify installation and first capture
```

Explain the user-visible result, why the change is needed, how you verified it, and any compatibility or hardware limitations. For UI changes, attach a safe screenshot or recording when useful.

Before requesting review, confirm that the branch builds for the affected architectures, the relevant tests pass, no secrets/private fixtures/build outputs are included, and settings/history/permission behavior is preserved. Keep unrelated refactoring and dependency upgrades in separate PRs.

The **Build and test** workflow builds x64/ARM64, checks version consistency and agent manifests, runs the x64 MCP contract and unit suites, and exercises the large-library benchmark. Native/hardware checks that cannot run in hosted CI should be described in the PR.

## Packaging and releases

Most contributors do **not** need to package an installer.

For local packaging, restore the pinned tool and use the version already in `version.txt`:

```powershell
dotnet tool restore
pwsh build\package.ps1 -Runtime win-x64
pwsh build\package.ps1 -Runtime win-arm64
```

The script publishes the app, MCP companion, agent assets, and bundled Copilot runtime, then creates installer, managed-portable, and update-feed assets. Defaults are `artifacts\publish\<version>\<runtime>` and `artifacts\releases\<runtime>`. `-OutputRoot` and `-BuildArtifactsPath` can move large outputs to another drive.

`-IncludeCopilotRuntime:$false` is an opt-out for custom developer packages; public releases keep the runtime bundled. Such a package needs a separately configured SDK-compatible runtime for AI descriptions. Regenerate the logo/tray assets only when needed with `pwsh build\make-icons.ps1`.

### Maintainer release flow

[Release Please](https://github.com/googleapis/release-please) owns `version.txt`, `Directory.Build.props`, `CHANGELOG.md`, `.release-please-manifest.json`, and both agent plugin versions. **Do not manually bump these files or create release tags in an ordinary feature/fix PR.**

After changes land on `main`, review and merge the release PR. The combined [release workflow](.github/workflows/release.yml) creates a tag/draft, tests and packages both architectures, uploads the installers/portable bundles/feed files, and only then publishes the release as latest. Never publish a partial draft; update clients must not discover incomplete assets.

For repair/bootstrap, maintainers can dispatch **Release Please and publish** with an existing tagged version. Keep tag, manifest, MSBuild, and plugin versions consistent. GitHub must allow Actions to create pull requests; no personal token or signing credential belongs in source control. Release packages are currently unsigned.

## License and provenance

Contributions are made under Hotshot's [MIT license](LICENSE). Use dependencies and assets with compatible licensing and preserve required notices.

ShareX capability research used [REA](https://github.com/morluto/rea) to inspect the official ShareX assemblies. It informed independent native implementations; no ShareX GPL source, binaries, or UI are included. Preserve that boundary rather than copying implementation code.
