# Native Product UI acceptance

This executable hosts the **real WinUI settings, workspace/editor, comparison
and guided setup** in a generated, isolated profile. `--ui-acceptance` performs
native control/automation checks, keyboard-focus traversal, XAML rendering and
Win2D draw/export checks. It never invokes capture, WGC, recording, audio,
clipboard, global input injection, user-library or signed-in provider flows.
The accessibility fixture also hosts real native controls.
Editor checks cover centered controls with gaps, full RGB/hex/transparency
selection, custom-color saves, canvas-only editing cursors and a rendered
thick green arrow preview beside the controls.
The editor title must display an entire long, unbroken filename, including its
last character, at 520/700/1120 DIPs in light/dark themes. Filenames wrap above
the commands rather than sharing their limited width; native character bounds
and generated images verify that neither the name nor toolbar is clipped.
Editor feedback checks verify native capture visibility and selectable editor
and Settings windows while other Hotshot controls stay out of screenshot
targets. The existing assistant window allowlist is unchanged. These checks
read only this fixture's window flags; they do not capture desktop pixels.
Window snapping checks use generated windows from this process only: full
physical-pixel bounds, topmost selection and hidden-window filtering. They do
not capture the desktop or move the user's pointer.
Capture-overlay checks draw a generated bitmap, then queue window-local keyboard
and mouse messages through the native message loop. Escape must cancel both
screenshot and recording selection before and during a drag, without returning
a selection or retaining keyboard/mouse capture. Dialog-key ownership is also
checked so keyboard navigation cannot swallow Escape, Enter or arrow keys.
These checks never capture desktop pixels or inject global input.
The recording timer fixture checks countdown through zero, hiding before
startup, timer-only Mini/Small layouts in all four corners, elapsed/pause
feedback and retained pause/stop controls without starting a recorder.
Native settings choices must persist both preferences. The Mini/Small images
use their actual pixel sizes; layout checks reject clipped timer text.
The timer's native capture-exclusion flag must also remain set.
General, screenshot, GIF, recording and naming settings verify that their
920-DIP column is centered in the content area beside navigation at compact,
normal and 1920-DIP widths in both themes. Headings and card text stay
left-aligned within the centered column; compact windows use the full width.
Normal and wide windows exercise both collapsed and expanded navigation panes;
the wide General/GIF-page images preserve their native aspect ratio.
Default Settings pages also check that descriptions stay under 30 words,
technical setup text stays out of the default view, and Copilot permissions
remain unchanged. `settings-plain-language.png` shows the simplified page.
Generated-image policy checks exercise the production derivative-source helper,
screenshot output/history persistence, app exclusions and unknown-source review
without allowing a description provider to start.
The real Settings sign-in controls also run against generated auth clients:
signed-out checks, browser handoff, verified success, cancellation, redacted
errors and unchanged upload/agent permissions. The fixture never performs
GitHub authorization or writes real credentials.
The self-contained harness includes Microsoft's Windows Desktop UI Automation
client for native-only TextBox patterns; it does not host WPF UI or enable WPF
XAML compilation. UIA queries stay scoped to this harness's own HWND/process.
First-launch checks verify a ready workspace with no automatic tour, quiet
background startup, explicit commands and shortcut-attention handling. Guided
setup is exercised only as an optional, manually opened settings feature.
Capture-default checks use supplied synthetic account paths: work/school
OneDrive before personal, a redirected Pictures folder when applicable, and
local Pictures without OneDrive. They never query the user's OneDrive accounts.

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

The publication/media fixture remains available with `--output`
(optionally `--settings-header-only`); it is **not part of default native UI CI**. Unlike
acceptance, the full media fixture requires own-window capture and recording.
All fixture files stay under the supplied output directory.

For publication, dispatch Native WinUI acceptance with `generate_media: true`.
After normal acceptance, x64 generates current editor/Settings/comparison PNGs
and a silent walkthrough from this harness's own windows. The evidence records
the exact source commit, image dimensions and text below the arrow. Windows use
1100x800 DIPs for readable UI; no user profile, desktop, clipboard or provider is
used. This opt-in media run requires an interactive desktop just like acceptance.
