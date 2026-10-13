<img src="https://raw.githubusercontent.com/sebastienlevert/hotshot/main/src/Hotshot.App/Assets/Logo.png" alt="Hotshot logo" width="80" height="80">

# Hotshot

**Capture fast. Edit when you need to. Stay in your flow.**

Hotshot is a native Windows screenshot and screen-recording app. It lives in your system tray: press a shortcut, choose what to capture, and paste the result. Screenshots are copied and saved without opening a preview window.

[Website](https://sebastienlevert.github.io/hotshot/) | [Full documentation](https://sebastienlevert.github.io/hotshot/docs/) | [Download Hotshot](https://github.com/sebastienlevert/hotshot/releases/latest) | [Quick start](#your-first-screenshot) | [Changelog](https://github.com/sebastienlevert/hotshot/blob/main/CHANGELOG.md) | [Report an issue](https://github.com/sebastienlevert/hotshot/issues) | [Contribute](CONTRIBUTING.md)

**Works on:** Windows 10 build 19041 or newer and Windows 11, on x64 and ARM64. The release includes .NET and the Windows App SDK; you do not need to install them separately. Basic capture and editing do not require an account.

**Jump to:** [Settings](#set-it-up-your-way) | [Editing and library](#edit-organize-and-compare) | [Video and GIFs](#record-video-and-make-gifs) | [OneDrive](#keep-settings-across-machines-with-onedrive) | [AI and agents](#optional-ai-and-coding-agents) | [Troubleshooting](#troubleshooting)

## What can I do with it?

| You want to... | Hotshot helps you... |
|---|---|
| Capture something quickly | Grab a region, a window, one monitor, or all monitors with global shortcuts. |
| Explain what you captured | Add arrows, text, shapes, numbered steps, highlights, crops, and opaque redactions. |
| Find an older capture | Browse filenames and search tags, projects, recognized text, and optional AI descriptions. |
| Record a walkthrough | Record MP4 with optional system/microphone audio, pause, trim, extract frames, and convert to GIF. |
| Check a visual change | Compare screenshots with a swipe view, pixel differences, alignment, ignored regions, and project baselines. |
| Keep your setup consistent | Roam portable preferences across machines through OneDrive, without copying device permissions. |
| Work with a coding agent | Opt into local MCP screenshot tools and portable skills for supported coding clients. |

## Install Hotshot

Open the [latest release](https://github.com/sebastienlevert/hotshot/releases/latest) and choose **one** of these downloads:

| Your Windows PC | Recommended installer | Portable alternative |
|---|---|---|
| Intel or AMD processor, x64 | [Hotshot-Core-win-x64-Setup.exe](https://github.com/sebastienlevert/hotshot/releases/latest/download/Hotshot-Core-win-x64-Setup.exe) | [Hotshot-Core-win-x64-Portable.zip](https://github.com/sebastienlevert/hotshot/releases/latest/download/Hotshot-Core-win-x64-Portable.zip) |
| ARM processor, ARM64 | [Hotshot-Core-win-arm64-Setup.exe](https://github.com/sebastienlevert/hotshot/releases/latest/download/Hotshot-Core-win-arm64-Setup.exe) | [Hotshot-Core-win-arm64-Portable.zip](https://github.com/sebastienlevert/hotshot/releases/latest/download/Hotshot-Core-win-arm64-Portable.zip) |

Not sure which one you need? Check **Windows Settings > System > About > System type**.

Run the installer and launch Hotshot. Installation is for your Windows user and does not require administrator privileges. The first launch opens Settings; closing that window keeps Hotshot running in the tray.

### Core or Full?

Releases from **0.1.14** offer two editions. **Core** is the smaller download: capture, editing, recording, OCR, comparisons, OneDrive settings, and MCP tools are included; the cloud-AI runtime is optional. Core assets have `Hotshot-Core-` in their name. **Full** includes the Copilot runtime and keeps the existing `Hotshot-win-` asset names.

For Full, choose the [x64 installer](https://github.com/sebastienlevert/hotshot/releases/latest/download/Hotshot-win-x64-Setup.exe) or [ARM64 installer](https://github.com/sebastienlevert/hotshot/releases/latest/download/Hotshot-win-arm64-Setup.exe); Full portable ZIPs are also on the release page. The 0.1.14 x64 Core installer is about **151 MB**, versus **206 MB** for Full.

Choose one edition, not both. Both use the same native app and local data; each updates on its own architecture/edition channel. Existing Full installations remain Full and are not silently switched to Core.

In Core, open **Screenshots > Optional Copilot component** to explicitly install the verified component, or choose an SDK-compatible external runtime. Installing software is separate from enabling image uploads, signing in, or authorizing a coding agent.

For the portable version, extract the **entire ZIP** to a permanent, writable folder and run `Hotshot.exe`. Keep its files together. Moving it later may require updating the **Start with Windows** setting.

> [!NOTE]
> Release packages are currently unsigned, so Windows may display a SmartScreen warning. Download only from this repository's official releases. The `.nupkg`, delta, JSON, and `RELEASES` assets are update-system files, not the downloads you need to start using Hotshot.

## Your first screenshot

1. Start Hotshot. Look for its amber-and-coral lightning icon near the Windows clock; it may be under **Show hidden icons**.
2. Press **Print Screen**. Drag to select a region, or click a window.
3. Paste into another app with **Ctrl+V**. By default, the screenshot is also saved under **Hotshot in your Pictures folder**, with date-based subfolders.
4. Click the tray icon when you want to review or edit it. Choose its filename to load it into the editor.

**No window appears after a screenshot. That is intentional.** Capturing, copying, and saving happen without opening the editor or changing its selection.

The first-run guide walks through folder readiness, registered shortcuts, output choices, OneDrive status, privacy, and an optional generated-image agent test. Choices apply on **Finish**; Cancel or a settings handoff discards staged choices. Skipping a check does not invent a successful result or enable a permission. Reopen the guide from **About Hotshot > Quick-start guide**.

If Print Screen opens Snipping Tool instead, see [Troubleshooting](#troubleshooting).

### Shortcuts and tray controls

| Shortcut | Action |
|---|---|
| **Print Screen** | Select a region or click a window. |
| **Ctrl+Print Screen** | Capture the monitor under the pointer. |
| **Shift+Print Screen** | Capture all monitors. |
| **Ctrl+Shift+Print Screen** | Start or stop a screen recording. |
| **Space**, in the capture overlay | Select/capture the monitor under the pointer. |
| **Escape**, in the capture overlay | Cancel without taking a screenshot. |

Change shortcuts under **Settings > Keyboard shortcuts**. Select a shortcut, press the new combination, then choose **Apply** or **Clear**. Hotshot reports invalid, duplicate, or unavailable shortcuts.

**Click the tray icon** to open or focus Editor and history. **Right-click it** for capture commands, recording controls, Settings, the captures folder, and Exit. Closing Settings or the editor does not quit the tray app.

## Set it up your way

Right-click the tray icon and open **Settings**. Changes save automatically when valid; there is no global Save button. Invalid edits show an explanation and remain unapplied.

| Setting | Where to find it |
|---|---|
| Light, dark, or Windows-default appearance | **General > App theme** |
| Choose where captures go | **General > Captures folder**; use the folder picker |
| Turn clipboard copying or file saving on/off | **General > Copy captures to clipboard / Save captures to files** |
| Run when you sign into Windows | **General > Start with Windows**; enabled by default in release builds |
| Keep preferences in OneDrive or only on this PC | **General > Settings across devices** |
| Delay a screenshot, include the pointer, or recognize text | **Screenshots** |
| Choose audio sources, microphone, quality, and frame rate | **Screen recording** |
| Set GIF size, frame rate, dithering, and looping | **GIF conversion** |
| Customize shortcuts or filenames | **Keyboard shortcuts / File naming** |

The hamburger navigation button and search box both sit inside the Settings title bar, including on narrow windows. Use the hamburger to open/close the sidebar. Press **Ctrl+F**, search for a setting, and choose a result to jump to it.

## Capture screenshots

For most captures, use the shortcuts above. Under **Screenshots**, you can add a delay of up to 30 seconds to make time for opening a menu or preparing a window.

**Window captures:** by default, **Unobscured window screenshots** captures the selected window even if another window overlaps it, without moving or focusing it. Windows may show a capture border. Hidden, minimized, protected, or changed targets can fail explicitly; Hotshot does not silently substitute a desktop screenshot. Turn this setting off if you specifically want the visible-screen version.

**Scrolling captures:** choose **Scrolling capture** from the tray or workspace. Select the content, scroll it yourself, and add overlapping frames. Exclude fixed headers and keep the capture area in the same position and size. Hotshot rejects ambiguous overlaps rather than stitching a misleading result. This is guided capture, not automatic scrolling.

You can capture Hotshot's own Settings window for feedback. The editor and recording controls are excluded from capture.

## Edit, organize, and compare

Click the tray icon and select a filename. The workspace opens with an empty editor until you choose a capture; arrow keys move between filenames and load the selected item directly.

### Edit and export

Annotate with arrows, shapes, text, ink, highlights, numbered steps, or pixelation. Crop the image, manage layers, and undo/redo edits. Use **Redact** for an opaque black cover over sensitive content.

| Action | What it does |
|---|---|
| **Save** | Updates the capture while retaining a private original and editable annotation sidecar. |
| **Copy** | Copies the edited image without saving it. |
| **Export** | Creates a separate flattened PNG/JPEG with optional resizing, padding, background, rounded corners, and shadow. Does not replace the editable capture. |
| **Import image** | Adds an existing PNG/JPEG/BMP without changing the clipboard. |

Export remembers your last style. Save named presets to reuse a look; up to 50 presets are supported. Flattened exports omit AI/OCR text metadata.

> [!WARNING]
> **Redacting the visible image does not erase its private original or editable sidecar.** Saved redactions remove old AI/OCR text metadata, but originals can still contain sensitive content. Share only the flattened exported image, not the application-data folder or a backup containing originals. Pixelation is not the same as opaque redaction.

<details>
<summary>Editor keyboard and navigation reference</summary>

| Control | Action |
|---|---|
| Mouse wheel | Zoom. |
| **Ctrl+1** | View at actual physical-pixel size, 100%. |
| **Ctrl+0** | Fit the image. |
| **Space+drag** or middle-button drag | Pan. |
| **V / A / R / T / C / P** | Select / arrow / rectangle / text / crop / pen. |
| **Ctrl+D** | Duplicate the selected annotation. |
| Arrow keys, with the canvas focused | Move the selected annotation; **Shift** uses a larger step. |
| **Ctrl+arrow keys** | Resize a box or line. |
| **Insert**, with the canvas focused | Create an annotation. |

</details>

### Find and organize captures

Search by filename, application, tags, recognized text, or AI description. Use **Details and tags** to add tags and favorites, or organize multiple selected captures into a project. Save searches you use often.

Search understands ordinary words, quoted phrases, and filters such as `project:`, `tag:`, `app:`, `type:`, and `text:`. For example, `project:website tag:bug` narrows the library to captures matching both filters.

Saved captures stay searchable beyond the recent-history limit by default. History trimming does **not** delete your saved capture files. With file saving disabled, temporary captures can be removed when their history entries are trimmed.

### Pin, compare, and protect your library

**Pin image** keeps a capture on top of other windows. Adjust its opacity or temporarily enable click-through; a separate Resume control and a 20-second timeout let you regain input.

Ctrl-select two same-size screenshots and choose **Compare selected screenshots**. Switch between before/after/diff views, use synchronized zoom and a swipe slider, and adjust tolerance, alignment, or ignored rectangles. Set project baselines for repeat comparisons. Export local JSON/PNG reports when you need evidence of a change.

Open **Library actions** for reviewed storage cleanup, local ZIP backup/restore, missing-file relinking, batch export, and deletion to the Recycle Bin. Including private originals in backups is opt-in. Restores add a new subfolder rather than replacing your existing library. Cleanup that removes originals also removes their editable history.

## Record video and make GIFs

1. Press **Ctrl+Shift+Print Screen** or use the tray recording command to choose a window or a region on one monitor.
2. Use the recording controls to pause/resume, mute microphone/system audio, and watch audio levels.
3. Stop with the shortcut or recording controls. Hotshot saves an H.264 MP4. Closing the recording controls also stops and saves.

Turn both audio options off under **Screen recording** for a silent video. Closing the recorded target ends the recording, even while paused. Recording regions cannot span monitors.

Select a video in the workspace to play it, extract a frame, or trim it to a **separate MP4** using the start/end sliders. If recording was interrupted, the recovery action can recover a finalized, playable MP4; unreadable or unfinalized data is preserved and reported rather than claimed recovered.

For a GIF, select an MP4 and choose **Convert to GIF**, enable automatic GIF creation, or assign the **Record as GIF** shortcut. Conversion runs in the background and keeps the MP4. GIFs have no audio and use at most 256 colors per palette, so complex gradients and photographs lose detail. MP4/GIF clipboard copies are files, not static images.

## Choose folders and filenames

The default location is **Hotshot in your Pictures folder**. Change it under **General > Captures folder**. Canceling the picker keeps the current location.

Under **File naming**, set separate patterns for screenshots and recordings/GIFs. The live preview shows the result; the token dropdown includes examples and inserts a token at the cursor.

| Pattern | Example relative to your captures folder |
|---|---|
| `{yyyy}/{MM}/{timestamp}` (default) | `2026\10\2026-10-11_14-30-05.png` |
| `{app}\{timestamp}` | `notepad\2026-10-11_14-30-05.png` |
| `{date}-{counter:4}` | `2026-10-11-0001.png` |

Both `/` and `\` create subfolders in a naming pattern. Hotshot supplies the file extension, sanitizes invalid filename characters, and adds a numeric suffix when a filename already exists.

<details>
<summary>All filename tokens</summary>

| Category | Tokens |
|---|---|
| Calendar | `{yyyy}`, `{yy}`, `{MM}`, `{MMM}`, `{MMMM}`, `{dd}`, `{ddd}` |
| Clock | `{HH}`, `{hh}`, `{tt}`, `{mm}`, `{ss}`, `{fff}` |
| Combined/custom time | `{timestamp}`, `{date}`, `{time}`, `{unix}`, `{now:yyyyMMdd}`; `now:` accepts custom .NET date formats |
| Capture context | `{type}`, `{app}`, `{title}`, `{monitor}`, `{width}`, `{height}` |
| Identifiers | `{counter}`, `{counter:4}`, `{rand}`, `{rand:8}`, `{guid}`, `{computer}`, `{user}` |

Counters are local to each machine; they are not a shared OneDrive sequence.

</details>

## Keep settings across machines with OneDrive

When a local OneDrive folder is available, Hotshot automatically creates or loads:

```text
<OneDrive>\Apps\Hotshot\settings.json
```

On your other Windows machines, sign into the same OneDrive account, let that folder synchronize, and install a current Hotshot release. Each machine reads the portable preferences while keeping its own device settings.

| Shared preferences | Kept on each machine |
|---|---|
| Theme, startup preference, hotkeys, filename patterns | Capture folder, runtime path, microphone device ID |
| Clipboard/save choices, capture and recording options, GIF options | Window positions, splitter width, counters, first-run state |
| Local-OCR preference, AI exclusion list, export presets, saved searches | Cloud-AI enable/review choices, all coding-agent permissions, project baseline IDs |

**This syncs settings, not captures or history.** Hotshot does not share a folder with other people or change OneDrive permissions. The OneDrive desktop client handles uploads/downloads; seeing a local file does not prove cloud transfer is complete.

Under **General > Settings across devices**, choose another OneDrive root, reload shared preferences, or select **Use local preferences only**. The page shows the exact file paths and status. Local-only mode does not delete the shared file.

Offline edits stay in the local cache and are retried. Incoming changes apply when the app is idle and Settings is not being edited. Independent field edits merge; conflicting edits require you to choose which preferences to keep. If OneDrive creates extra settings conflict-copy JSON files, review and move those copies before resolving the conflict. Invalid files are not silently replaced with defaults.

## Optional AI and coding agents

### Search screenshots by description

Open **Settings > Screenshots > Copilot account > Sign in with GitHub** and complete authorization in your browser. No separate Copilot CLI or GitHub CLI installation is required. Full bundles the runtime; in Core, install the optional Copilot component first. **Check sign-in** can also reuse an existing Copilot/GitHub CLI sign-in. Browser cookies alone are not authorization, and environment credentials can take priority over stored sign-ins.

Sign-in uses the runtime's GitHub browser authorization with PKCE and Windows secure credential storage. Hotshot does not request a pasted token, embed an OAuth secret, or put credentials in settings, OneDrive or logs. Pending sign-in is cancellable, times out after five minutes, and is stopped when Settings closes. Plaintext token storage is refused.

Then explicitly enable **Describe screenshots with Copilot** to generate searchable summaries and descriptions. This is **off by default**, requires a GitHub Copilot subscription and an image-capable model, and uses your account's quota. Signing in does not enable uploads, change review/exclusion rules or grant coding-agent access. Account checks and sign-in send no screenshot or model prompt.

Descriptions are added after saving, without delaying the clipboard. PNG files also receive Unicode **Title** and **Description** metadata. Windows Explorer may not display those PNG fields even though metadata-aware tools can read them. Recordings and GIFs are not sent for descriptions.

For more control, enable **Review before cloud analysis**, review/redact and save the screenshot, then choose **Analyze selected screenshot**. **Exclude applications from AI** uses process names and the capture's recorded application; it does not detect sensitive content in other windows on a monitor capture. Use **View AI processing** to inspect, cancel, or retry work.

Want searchable text without cloud AI? Enable **Screenshots > Recognize screenshot text locally**, or choose **Extract text** for a selected image. Windows OCR language support is required.

### Let a coding agent use Hotshot

Open **Settings > Coding agents** to connect Copilot CLI, Claude Code, VS Code, Cursor, or Codex through the bundled local MCP server. Copy the generated configuration into your client's configuration, or use the repository's plugins and skills.

**Agent access** and **Allow history access** are separate, off-by-default permissions. You can restrict target apps/capture modes, require a timed grant, or approve each content request with **Allow once / Deny**. Test the connection with a generated image without reading your screen or library.

Agents can request screenshots, read permitted captures, use OCR, and compare images. They cannot change settings, execute shell commands, control mouse/keyboard input, or record video through Hotshot.

Read the [coding-agent setup and tool reference](https://github.com/sebastienlevert/hotshot/blob/main/agents/README.md) for installation commands, supported tools, and permission details.

### Know what leaves your machine

| Feature | Data handling |
|---|---|
| Capture, editing, comparison, local OCR | Processed locally by Hotshot; no image upload is required. |
| Copilot descriptions, if enabled | Screenshot pixels go to your signed-in GitHub Copilot account and consume model quota. |
| Coding-agent access, if enabled | The requesting client receives permitted images/text and may send them to its own model provider. |
| OneDrive settings | Portable preferences go into your local OneDrive folder; the OneDrive client handles cloud transport. |

If your **captures folder** is backed up by OneDrive or another service, that service may upload images independently of Hotshot's settings-sync feature. Review your chosen folder as well as your AI/agent permissions.

## Updates and local data

Installed and managed-portable releases check for updates shortly after startup and hourly. Updates download in the background and restart Hotshot only when capture, recording, conversion, AI work, and editing are idle, with no active editor/Settings window or unsaved work.

Check status or choose **Check now** under **Settings > About Hotshot > Automatic updates**. Downloading updates does not require a GitHub account. Older development ZIPs and raw build outputs cannot update themselves; use a current installer or managed-portable bundle.

| Location | Contents |
|---|---|
| Your chosen captures folder | Saved PNG, MP4, GIF, and exported files |
| `%AppData%\Hotshot` | Local settings/cache, sync configuration, history, thumbnails, logs, and private originals/sidecars |
| `<OneDrive>\Apps\Hotshot\settings.json` | Portable preferences, when settings roaming is enabled |
| `%LocalAppData%\Hotshot` | Installer-managed application files; not the place to keep user captures |

Updates keep your profile outside the installer directory. Use the workspace's backup/cleanup actions instead of manually deleting application-data files.

## Troubleshooting

| What you see | What to try |
|---|---|
| Print Screen opens Snipping Tool | Disable Windows' Print Screen shortcut for screen snipping, or assign a different shortcut in Hotshot. |
| Nothing opens after a screenshot | This is expected. Paste with **Ctrl+V**, open the captures folder, or click the tray icon and select a filename. |
| You cannot find the tray icon | Check Windows' hidden tray icons and confirm Hotshot is running. |
| A capture is not copied or saved | Check the clipboard/file switches under General and your capture-folder permissions. An MP4/GIF clipboard item is a file. |
| A window capture fails | Restore/show the window and select it again. Protected targets cannot be captured. For visible-screen capture, explicitly disable **Unobscured window screenshots**. |
| OCR finds no text | Check Windows OCR language support and whether the image contains readable text. |
| Copilot descriptions fail | Check your Copilot sign-in/model availability and **View AI processing**. The saved screenshot remains available. |
| A coding agent cannot connect | Keep Hotshot running in the same Windows session; check permissions, timed grants, and the copied executable path. Reconnect the client's MCP server after an update. |
| OneDrive settings do not change on another PC | Check both OneDrive clients, the shared path, and sync/conflict status. Resolve pending edits and use **Reload shared preferences** when idle. |
| An update is ready but not applied | Finish current work and leave the editor/Settings inactive or closed. Hotshot will not interrupt active work to restart. |

For a bug report, include your Hotshot version, Windows build, architecture, steps to reproduce, and expected/actual behavior. **About Hotshot > Show diagnostics** provides local diagnostic information; review and redact paths or private details before attaching it. Never attach private originals, a whole app-data folder, or credentials.

Use the [worked workflows](docs/recipes.md) for complete redacted bug-report, comparison, and coding-agent recipes. The [support guide](SUPPORT.md) explains safe diagnostics and reporting; suspected vulnerabilities belong in [private security reporting](SECURITY.md), not a public issue.
The [compatibility matrix](docs/compatibility.md) separates supported targets from actual device evidence. Physical ARM64, mixed-scale displays, HDR fidelity, and Narrator acceptance are not implied by a cross-build or synthetic check.

## Current boundaries

Hotshot is Windows-only. It does not include upload services, public capture sharing, or fully automated scrolling. HDR window captures are tone-mapped to SDR PNG rather than preserving HDR brightness/gamut; monitor/region captures use the visible-screen 8-bit path. Guided scrolling is limited to 16,384 pixels in height and 64 megapixels. GIF output is necessarily lossy.

## Contribute

Bug reports, documentation improvements, accessibility feedback, and focused code changes are welcome. See **[CONTRIBUTING.md](CONTRIBUTING.md)** for Windows development setup, architecture, testing, pull requests, and releases. If you are reading an installed copy, use the [online contributor guide](https://github.com/sebastienlevert/hotshot/blob/main/CONTRIBUTING.md).

Hotshot is licensed under the [MIT license](https://github.com/sebastienlevert/hotshot/blob/main/LICENSE).
