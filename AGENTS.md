# Hotshot — agent guide

Hotshot is a native Windows (WinUI 3 / Windows App SDK 2.x, .NET 10) screenshot and screen-recording utility.
It lives in the tray, reacts to global hotkeys, and ships self-contained for **win-x64** and **win-arm64**.

## Layout
| Path | Purpose |
|---|---|
| `src/Hotshot.Core` | Pure `net10.0`: settings, file-naming token engine, history index, hotkey model. No Windows APIs. Unit tested. |
| `src/Hotshot.Capture` | Screenshots (GDI BitBlt of the virtual screen), PNG encoding, Win32 clipboard, monitor/window enumeration. |
| `src/Hotshot.Recording` | Screen recorder: Windows.Graphics.Capture → D3D11 (Vortice) → Media Foundation SinkWriter (H.264/AAC MP4) + WASAPI audio (NAudio). |
| `src/Hotshot.Gif` | MP4 → GIF converter: Media Foundation SourceReader decode + managed quantizer/LZW GIF writer. |
| `src/Hotshot.Editor` | Reusable Win2D annotation surface (code-only UI, no `.xaml` files), document model and rendering/export. |
| `src/Hotshot.App` | WinUI 3 exe: tray icon, hotkeys, Win32 capture overlay, editor/history workspace, PowerToys-style settings and recording controls. |
| `tests/*` | xUnit tests, native UI smoke script and media harnesses. |
| `build/` | Icon generation and Velopack installer/managed-portable/update-feed packaging. |
| `.github/workflows/` | x64/ARM64 CI and combined Release Please + draft-to-published release automation. |

## Build & test
```powershell
dotnet build src\Hotshot.App\Hotshot.App.csproj -p:Platform=x64      # or -p:Platform=ARM64
dotnet tool restore                                                     # pinned vpk release tool
dotnet test --project tests\Hotshot.Core.Tests\Hotshot.Core.Tests.csproj
dotnet test --project tests\Hotshot.Gif.Tests\Hotshot.Gif.Tests.csproj
dotnet test --project tests\Hotshot.Editor.Tests\Hotshot.Editor.Tests.csproj
dotnet run --project tests\Hotshot.Recording.Harness\Hotshot.Recording.Harness.csproj -- --window-only
pwsh build\make-icons.ps1                                              # regenerate icons
pwsh build\package.ps1 -Version 0.1.0 -Runtime win-x64                 # installer + update feed
```
Only the `dotnet` CLI is required (no Visual Studio). The app is unpackaged (`WindowsPackageType=None`) and Windows App SDK self-contained.

## Conventions
- C# latest, nullable enabled, file-scoped namespaces, `sealed` by default.
- Win32 interop via `[LibraryImport]` source-generated P/Invoke in a `Native*.cs` file per project; COM/D3D/MF via Vortice.
- Libraries must not contain `.xaml` files (unpackaged PRI merging is fragile); build UI in code there. XAML is fine in `Hotshot.App`.
- Physical pixels everywhere for capture coordinates (process is Per-Monitor V2 DPI aware).
- User data lives in `%AppData%\Hotshot` (settings.json, history.json, thumbnails, originals). `%LocalAppData%\Hotshot` belongs to the Velopack installer.
- Never block the UI thread on encoding or disk IO; clipboard first, then save.
- Captures are silent. Tray activation opens/focuses the editor/history workspace without selecting the newest item. Settings use adaptive navigation, grouped cards and automatic validated persistence.
- Stable GitHub Releases supply automatic updates on process-architecture-specific channels. Restart only when capture/recording/conversion/editor/settings are idle; retain user data outside installer directories.
- Use Conventional Commits. Release Please owns version.txt, the MSBuild version, changelog and release manifest; publish drafts only after both RID assets/feed files are uploaded.
