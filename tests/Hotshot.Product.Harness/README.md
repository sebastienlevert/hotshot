# Native Product UI acceptance

This executable hosts the **real WinUI settings, workspace/editor, comparison
and guided setup** in a generated, isolated profile. `--ui-acceptance` performs
native control/automation checks, keyboard-focus traversal, XAML rendering and
Win2D draw/export checks. It never invokes capture, WGC, recording, audio,
clipboard, global input injection, user-library or signed-in provider flows.
The accessibility fixture also hosts real native controls.
The self-contained harness includes Microsoft's Windows Desktop UI Automation
client for native-only TextBox patterns; it does not host WPF UI or enable WPF
XAML compilation. UIA queries stay scoped to this harness's own HWND/process.

Build and execute **on the matching native architecture**, using a task-owned
output directory (local validation uses the session's C-drive artifact area):

```powershell
$output = 'C:\HotshotNativeUi\x64'
dotnet build tests\Hotshot.Product.Harness\Hotshot.Product.Harness.csproj -c Release -p:Platform=x64 --artifacts-path "$output\build" -warnaserror
.\tests\Hotshot.Product.Harness\Invoke-Acceptance.ps1 `
  -Executable "$output\build\bin\Hotshot.Product.Harness\release_win-x64\Hotshot.Product.Harness.exe" `
  -OutputDirectory "$output\evidence" -ExpectedArchitecture x64 -SoftwareRenderer
```

For native ARM64, use `Platform=ARM64`, `release_win-arm64` and
`ExpectedArchitecture=arm64`. The project selects the matching RID; it rejects
mismatched explicit RIDs. The application module remains the single Windows App
SDK bootstrap owner. The harness owns `ProductApp.xaml`; application XBF/resource
artifacts must remain separate from that application's original executable.

`ui-acceptance.json` records progress and actual process/OS architecture, every
required check, anonymous read-only display/HDR context, backend limitations and
exact exceptions. Unknown/unsupported color queries remain unverified; display
metadata is not capture/fidelity or mixed-DPI movement evidence. The supervising
script bounds native startup/hangs, records exit status and refuses incomplete
reports. `RenderTargetBitmap` evidence contains this harness's native XAML tree,
**not a desktop screenshot**; composition-only Win2D content is proved
separately by an observed native draw and generated annotated-image export.
`-SoftwareRenderer` selects Win2D software rendering in the workspace fixture,
not a virtual display or a production setting.
System backdrops are disabled and native XAML roots use an opaque neutral fixture
background, so generated images remain legible without sampling the desktop.

Missing interactive desktop, renderer/bootstrap failure, unsupported features,
timeouts and skipped required checks **fail** acceptance. A hosted run does not
prove physical ARM64, hardware GPU quality, HDR, mixed-DPI movement or human
Narrator usability. Those need separate real-environment evidence.

Editor keyboard workflows include tool selection + Insert, PageUp/PageDown
annotation selection, arrows to move (Shift: 10 pixels), Ctrl+arrows to resize
annotations/crop, Crop + Insert to initialize a crop, Alt+arrows to pan, +/-
zoom, Ctrl+0/Ctrl+1 fit/actual and Escape to cancel/deselect. The automated
fixture calls the shared native keyboard handler and exercises keyboard focus;
it does not claim physical input delivery. Human acceptance must separately
cover Narrator names/status speech, Tab/Shift+Tab return after hidden controls
and asynchronous saves, and live OS contrast/text-scale transitions.

The original publication/media fixture remains available with `--output`
(optionally `--settings-header-only`); it is **not run by native UI CI**. Unlike
acceptance, the full media fixture requires own-window capture and recording.
All fixture files stay under the supplied output directory.
