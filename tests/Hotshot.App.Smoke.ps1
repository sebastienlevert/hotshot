[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Exe,

    [switch]$VerifyStartup,

    [switch]$ThemeSearchOnly,

    [switch]$HeaderSearchOnly,

    [switch]$SettingsControlsOnly,

    [switch]$SettingsCaptureOnly,

    [switch]$HistoryNavigationOnly,

    [switch]$AgentSettingsOnly,

    [switch]$WorkbenchOnly,

    [string]$VideoFixture
)

$ErrorActionPreference = 'Stop'
$env:HOTSHOT_SETTINGS_SYNC_MODE = 'local'
$Exe = (Resolve-Path -LiteralPath $Exe).Path
if (Get-Process -Name Hotshot -ErrorAction SilentlyContinue) {
    throw 'Close Hotshot before running the smoke test; existing instances are never interrupted.'
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class HotshotSmokeNative {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterClipboardFormat(string name);
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput {
        public ushort Key, Scan;
        public uint Flags, Time;
        public UIntPtr Extra;
    }
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
    public static void keybd_event(byte key, byte scan, uint flags, UIntPtr extra) {
        var input = new Input { Type = 1, Data = new InputUnion {
            Keyboard = new KeyboardInput { Key = key, Scan = scan, Flags = flags, Extra = extra }
        }};
        if (SendInput(1, new[] { input }, Marshal.SizeOf<Input>()) != 1) {
            var error = Marshal.GetLastWin32Error();
            throw new System.ComponentModel.Win32Exception(error,
                $"Keyboard input could not be injected (Win32 {error}, INPUT size {Marshal.SizeOf<Input>()}).");
        }
    }
    [DllImport("user32.dll")]
    public static extern bool SetPhysicalCursorPos(int x, int y);
    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")]
    public static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    public static uint ScreenPixel(int x, int y) {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        var dc = GetDC(IntPtr.Zero);
        try { return GetPixel(dc, x, y); }
        finally { ReleaseDC(IntPtr.Zero, dc); SetThreadDpiAwarenessContext(previous); }
    }
    [DllImport("user32.dll")]
    public static extern IntPtr GetDlgItem(IntPtr hwnd, int id);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    public static int ClientWidth(IntPtr hwnd) {
        if (!GetClientRect(hwnd, out var rect)) {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        return rect.Right - rect.Left;
    }
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, uint size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    public static extern int GetFrameBounds(IntPtr hwnd, uint attribute, out Rect value, uint size);
}
'@

$data = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Hotshot'
$settingsFile = Join-Path $data 'settings.json'
$historyFile = Join-Path $data 'history.json'
$backupSettings = if (Test-Path $settingsFile) { [IO.File]::ReadAllBytes($settingsFile) } else { $null }
$backupHistory = if (Test-Path $historyFile) { [IO.File]::ReadAllBytes($historyFile) } else { $null }
$historyBackupFile = $historyFile + '.backup'
$backupHistoryRecovery = if (Test-Path -LiteralPath $historyBackupFile) { [IO.File]::ReadAllBytes($historyBackupFile) } else { $null }
$historyJournalFile = $historyFile + '.journal'
$backupHistoryJournal = if (Test-Path -LiteralPath $historyJournalFile) { [IO.File]::ReadAllBytes($historyJournalFile) } else { $null }
$recordingJournal = Join-Path $data 'pending-recording.json'
$backupRecordingJournal = if (Test-Path -LiteralPath $recordingJournal) { [IO.File]::ReadAllBytes($recordingJournal) } else { $null }
$output = Join-Path ([IO.Path]::GetTempPath()) ("hotshot-app-smoke-" + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($data) | Out-Null
[IO.Directory]::CreateDirectory($output) | Out-Null
$process = $null
$items = @()
$runKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
$runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runKeyPath)
$backupStartup = if ($runKey) { $runKey.GetValue('Hotshot', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } else { $null }
$backupStartupKind = if ($null -ne $backupStartup) { $runKey.GetValueKind('Hotshot') } else { [Microsoft.Win32.RegistryValueKind]::String }
if ($runKey) { $runKey.Dispose() }

function Get-StartupValue {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runKeyPath)
    try { if ($key) { $key.GetValue('Hotshot') } }
    finally { if ($key) { $key.Dispose() } }
}

function Wait-For([scriptblock]$Condition, [string]$Description, [int]$Seconds = 15) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $result = & $Condition
        if ($result -and ($result -isnot [IntPtr] -or $result -ne [IntPtr]::Zero)) { return $result }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out: $Description"
}

function Command([string]$Argument) {
    $forwarder = Start-Process -FilePath $Exe -ArgumentList $Argument -PassThru -Wait
    if ($forwarder.ExitCode -ne 0) { throw "Command failed: $Argument" }
}

function Read-AppJson([string]$Path) {
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    $reader = [IO.StreamReader]::new($stream)
    try { $reader.ReadToEnd() | ConvertFrom-Json }
    finally { $reader.Dispose(); $stream.Dispose() }
}

function Read-CaptureHistory {
    $items = @{}
    if (Test-Path $historyFile) { foreach ($item in @(Read-AppJson $historyFile)) { $items[$item.id] = $item } }
    if (Test-Path -LiteralPath $historyJournalFile) {
        $stream = [IO.File]::Open($historyJournalFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        $reader = [IO.StreamReader]::new($stream)
        try { $journal = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
        $lines = $journal.Split("`n")
        for ($i = 0; $i -lt $lines.Length - 1; $i++) {
            if (-not $lines[$i]) { continue }
            $transaction = $lines[$i] | ConvertFrom-Json
            foreach ($id in $transaction.Removed) { $items.Remove($id) }
            foreach ($item in $transaction.Upserts) { $items[$item.id] = $item }
        }
    }
    @($items.Values | Sort-Object createdAt -Descending)
}

function Element([IntPtr]$Hwnd, [string]$Name, $ControlType = $null) {
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($Hwnd)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    if ($null -ne $ControlType) {
        $typeCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType)
        $condition = [System.Windows.Automation.AndCondition]::new($condition, $typeCondition)
    }
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Click($Element) {
    if ($null -eq $Element) { throw 'Required button was not found.' }
    $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Workspace-Action([IntPtr]$Hwnd, [string]$Name) {
    Click (Workspace-Control $Hwnd $Name)
}

function Workspace-Control([IntPtr]$Hwnd, [string]$Name) {
    $control = Element $Hwnd $Name
    if (-not $control -or $control.Current.IsOffscreen) {
        $commands = Element-ById $Hwnd 'WorkspaceCommands'
        $more = $commands.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'MoreButton'))
        Click $more
        $group = switch ($Name) {
            { $_ -in 'Pin image', 'Compare selected screenshots' } { 'Image'; break }
            { $_ -in 'Describe screenshot', 'Cancel description', 'View description progress' } { 'Copilot'; break }
            { $_ -in 'Extract video frame', 'Trim recording', 'Recover interrupted recording', 'Dismiss recovery notice' } { 'Video'; break }
            { $_ -in 'Rename capture', 'Organize selected', 'Delete selected', 'Export selected files', 'Save this search', 'Delete saved search',
                'Storage and privacy', 'Back up library', 'Restore library backup', 'Find missing file',
                'Set project baseline', 'Compare with project baseline', 'Package screenshots' } { 'Library'; break }
        }
        if ($group) { Click (Wait-For { App-Control $group } "workspace group $group") }
        $control = Wait-For { App-Control $Name } "workspace command $Name"
    }
    return $control
}

function Element-ById([IntPtr]$Hwnd, [string]$Id) {
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($Hwnd)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Settings-PaneToggle([IntPtr]$Hwnd) {
    $header = Element-ById $Hwnd 'SettingsTitleBar'
    if (-not $header) { return $null }
    foreach ($id in 'PaneToggleButton', 'PART_PaneToggleButton', 'TogglePaneButton') {
        $button = $header.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id))
        if ($button -and -not $button.Current.IsOffscreen) { return $button }
    }
    return $null
}

function Select-SettingsPage([IntPtr]$Hwnd, [string]$Page) {
    $item = Element-ById $Hwnd "Settings$Page"
    if (-not $item -or $item.Current.IsOffscreen) {
        Click (Settings-PaneToggle $Hwnd)
        $item = Wait-For { Element-ById $Hwnd "Settings$Page" } "settings navigation $Page"
    }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $item = Element-ById $Hwnd "Settings$Page"
    # The title-bar toggle has no open/closed name; expanded items are wider than the compact icon rail.
    if ($item -and -not $item.Current.IsOffscreen -and
        $item.Current.BoundingRectangle.Width -gt 80 * ([HotshotSmokeNative]::GetDpiForWindow($Hwnd) / 96.0)) {
        Click (Settings-PaneToggle $Hwnd)
    }
}

function Settings-Window {
    $owner = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    foreach ($window in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $owner)) {
        if ($window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SettingsSearch'))) {
            return [IntPtr]$window.Current.NativeWindowHandle
        }
    }
    return [IntPtr]::Zero
}

function Assert-EmbeddedSettingsSearch([IntPtr]$Hwnd) {
    $search = Element-ById $Hwnd 'SettingsSearch'
    $header = Element-ById $Hwnd 'SettingsTitleBar'
    if (-not $header -or -not $search -or $search.Current.IsOffscreen) { throw 'Embedded title bar search is missing.' }
    $dpi = [HotshotSmokeNative]::GetDpiForWindow($Hwnd) / 96.0
    $headerBounds = $header.Current.BoundingRectangle
    $searchBounds = $search.Current.BoundingRectangle
    $pane = Settings-PaneToggle $Hwnd
    if (-not $pane) { throw 'Navigation toggle is missing from the title bar.' }
    $paneBounds = $pane.Current.BoundingRectangle
    if ($paneBounds.Left -lt $headerBounds.Left - 1 -or $paneBounds.Right -gt $headerBounds.Right + 1 -or
        $paneBounds.Top -lt $headerBounds.Top - 1 -or $paneBounds.Bottom -gt $headerBounds.Bottom + 1) {
        throw 'Navigation toggle is underneath the title bar instead of embedded inside it.'
    }
    if ($paneBounds.IntersectsWith($searchBounds)) { throw 'Navigation toggle overlaps title bar search.' }
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($Hwnd)
    foreach ($id in 'TogglePaneButton', 'PaneToggleButton', 'PART_PaneToggleButton') {
        $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id))
        foreach ($button in $buttons) {
            if (-not $button.Current.IsOffscreen -and $button.Current.BoundingRectangle.Top -ge $headerBounds.Bottom) {
                throw 'A duplicate navigation toggle is still visible beneath the title bar.'
            }
        }
    }
    if ($headerBounds.Height -lt 48 * $dpi - 1) { throw 'Title bar is shorter than its tall caption buttons.' }
    if ($searchBounds.Top -lt $headerBounds.Top - 1 -or $searchBounds.Bottom -gt $headerBounds.Bottom + 1) {
        throw 'Search is outside the title bar instead of embedded inside it.'
    }
    foreach ($name in 'Minimize', 'Maximize', 'Restore', 'Close') {
        $button = Element $Hwnd $name ([System.Windows.Automation.ControlType]::Button)
        if ($button -and -not $button.Current.IsOffscreen -and
            ($searchBounds.IntersectsWith($button.Current.BoundingRectangle) -or $paneBounds.IntersectsWith($button.Current.BoundingRectangle))) {
            throw "Search or navigation overlaps the $name caption button."
        }
    }
    if ($searchBounds.Width -lt 120 * $dpi) { throw 'Embedded search is too narrow to use.' }
}

function Toggle-Control([IntPtr]$Hwnd, [string]$Name) {
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($Hwnd)
    $nameCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $patternCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsTogglePatternAvailableProperty, $true)
    $condition = [System.Windows.Automation.AndCondition]::new($nameCondition, $patternCondition)
    $control = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if (-not $control) { throw "Toggle control not found: $Name" }
    $control.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
}

function App-Control([string]$Name) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $owner = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    foreach ($window in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $owner)) {
        $control = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($control -and -not $control.Current.IsOffscreen) { return $control }
    }
}

function Select-NamingToken([IntPtr]$Hwnd, [string]$Id, [string]$Label) {
    $combo = Element-ById $Hwnd $Id
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $item = Wait-For {
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $owner = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
        $name = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Label)
        foreach ($window in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $owner)) {
            $found = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $name)
            if ($found) { return $found }
        }
    } "token with example: $Label"
    if ($item.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::IsScrollItemPatternAvailableProperty)) {
        $item.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView()
    }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}

function Set-Pattern([IntPtr]$Hwnd, [string]$Id, [string]$Value) {
    $input = Element-ById $Hwnd $Id
    $input.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
    $input.SetFocus()
    $range = $input.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange
    $range.MoveEndpointByRange([System.Windows.Automation.Text.TextPatternRangeEndpoint]::Start,
        $range, [System.Windows.Automation.Text.TextPatternRangeEndpoint]::End)
    $range.Select()
}

function Control-Key([byte]$Key) {
    try {
        [HotshotSmokeNative]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
        [HotshotSmokeNative]::keybd_event($Key, 0, 0, [UIntPtr]::Zero)
    } finally {
        [HotshotSmokeNative]::keybd_event($Key, 0, 2, [UIntPtr]::Zero)
        [HotshotSmokeNative]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
    }
}

function Select-Region {
    $overlay = Wait-For { [HotshotSmokeNative]::FindWindow('Hotshot.CaptureOverlay', [NullString]::Value) } 'capture overlay'
    $start = [IntPtr]((40 -shl 16) -bor 40)
    $end = [IntPtr]((159 -shl 16) -bor 199)
    [HotshotSmokeNative]::PostMessage($overlay, 0x201, [IntPtr]1, $start) | Out-Null
    [HotshotSmokeNative]::PostMessage($overlay, 0x200, [IntPtr]1, $end) | Out-Null
    [HotshotSmokeNative]::PostMessage($overlay, 0x202, [IntPtr]0, $end) | Out-Null
}

try {
    $fixture = @{
        general = @{
            saveFolder = $output; startWithWindows = $false; firstRunCompleted = $true
            copyToClipboard = $true; saveToFile = $true; showPreview = $true; openEditorAfterCapture = $true
        }
        hotkeys = @{
            regionScreenshot = 'Ctrl+Alt+F9'; monitorScreenshot = 'Ctrl+Alt+F11'
            allMonitorsScreenshot = 'Ctrl+Alt+F10'; toggleRecording = 'Ctrl+Alt+F12'
        }
        naming = @{
            screenshotPattern = '{yyyy}\{MM}\{timestamp}_{counter:4}'
            recordingPattern = '{yyyy}\{MM}\{timestamp}_{counter:4}'
        }
        recording = @{ countdownSeconds = 0; captureSystemAudio = $false; captureMicrophone = $false }
        gif = @{ maxWidth = 320; framesPerSecond = 10 }
        agents = @{ allowedApps = @('Hotshot'); enabled = $false; allowHistoryAccess = $false }
        capture = @{ unobscuredWindows = -not $SettingsCaptureOnly }
    }
    [IO.File]::WriteAllText($settingsFile, ($fixture | ConvertTo-Json -Depth 5))
    [IO.File]::WriteAllText($historyFile, '[]')
    if (Test-Path -LiteralPath $historyJournalFile) { Remove-Item -LiteralPath $historyJournalFile }
    if (Test-Path -LiteralPath $recordingJournal) { Remove-Item -LiteralPath $recordingJournal }
    if ($HistoryNavigationOnly -or $WorkbenchOnly) {
        Add-Type -AssemblyName System.Drawing
        $fixtures = @()
        foreach ($size in 64, 80, 96) {
            $path = Join-Path $output "capture-$size.png"
            $bitmap = [Drawing.Bitmap]::new($size, [int]($size * 0.75))
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([Drawing.Color]::CornflowerBlue)
                $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
            } finally { $graphics.Dispose(); $bitmap.Dispose() }
            $fixtures += @{
                id = [Guid]::NewGuid().ToString('N'); path = $path; kind = 'Region'
                width = $size; height = [int]($size * 0.75); createdAt = [DateTimeOffset]::Now.ToString('o')
                fileSize = (Get-Item -LiteralPath $path).Length; thumbnailPath = $path
                summary = if ($size -eq 96) { 'Synthetic geometry' } else { $null }
                description = if ($size -eq 96) { 'Blue geometry on a generated image.' } else { $null }
                recognizedText = if ($size -eq 80) { 'Invoice total 123.45' } else { $null }
                tags = @(if ($size -eq 80) { 'finance' })
            }
            if ($WorkbenchOnly) {
                $path = Join-Path $output 'capture-96-after.png'
                $bitmap = [Drawing.Bitmap]::new(96, 72)
                $graphics = [Drawing.Graphics]::FromImage($bitmap)
                try {
                    $graphics.Clear([Drawing.Color]::CornflowerBlue)
                    $bitmap.SetPixel(10, 10, [Drawing.Color]::Red)
                    $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
                } finally { $graphics.Dispose(); $bitmap.Dispose() }
                $fixtures += @{ id = [Guid]::NewGuid().ToString('N'); path = $path; kind = 'Region';
                    width = 96; height = 72; createdAt = [DateTimeOffset]::Now.ToString('o'); fileSize = (Get-Item -LiteralPath $path).Length }
                if ($VideoFixture) {
                    $path = Join-Path $output 'generated-recording.mp4'
                    Copy-Item -LiteralPath $VideoFixture -Destination $path
                    $fixtures += @{ id = [Guid]::NewGuid().ToString('N'); path = $path; kind = 'Recording';
                        width = 640; height = 480; durationSeconds = 2; createdAt = [DateTimeOffset]::Now.ToString('o');
                        fileSize = (Get-Item -LiteralPath $path).Length }
                }
            }
        }
        [IO.File]::WriteAllText($historyFile, (ConvertTo-Json -InputObject $fixtures -Depth 4))
    }
    $startupCommand = if ($HistoryNavigationOnly -or $WorkbenchOnly) { '--history' } else { '--settings' }
    $process = Start-Process -FilePath $Exe -ArgumentList $startupCommand -PassThru
    if ($HistoryNavigationOnly -or $WorkbenchOnly) {
        $workspace = Wait-For {
            $owner = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
            foreach ($window in [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $owner)) {
                if ($window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'CaptureHistory'))) {
                    return [IntPtr]$window.Current.NativeWindowHandle
                }
            }
        } 'filename history workspace'
        $list = Element-ById $workspace 'CaptureHistory'
        if ($list.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Image))) { throw 'History still displays thumbnail previews.' }
        $rows = @{}
        foreach ($size in 64, 80, 96) {
            $rows[$size] = Wait-For { Element $workspace "Capture capture-$size.png" ([System.Windows.Automation.ControlType]::ListItem) } "filename row capture-$size.png"
            $rows[$size].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Wait-For { Element $workspace "$size x $([int]($size * 0.75)) pixels" } "automatic editor preview for capture-$size.png" | Out-Null
            if (-not $list.Current.IsEnabled) { throw 'Filename history was disabled while browsing.' }
        }
        if ($WorkbenchOnly) {
            $zoom = Element $workspace '100% zoom'
            if (-not $zoom) {
                $commands = Element-ById $workspace 'EditorCommands'
                $more = $commands.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'MoreButton'))
                Click $more
                $zoom = Wait-For { App-Control '100% zoom' } '100 percent zoom command'
            }
            Click $zoom
            Wait-For {
                $status = Element-ById $workspace 'EditorZoomStatus'
                $status -and $status.Current.Name.StartsWith('100%')
            } 'actual physical-pixel zoom' | Out-Null
            Workspace-Action $workspace 'Pin image'
            $pin = Wait-For { [HotshotSmokeNative]::FindWindow([NullString]::Value, 'Hotshot pinned image') } 'pinned image window'
            (Element-ById $pin 'PinnedOpacity').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue(70)
            (Element-ById $pin 'PinnedClickThrough').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
            $recovery = Wait-For { [HotshotSmokeNative]::FindWindow('Hotshot.PinnedRecovery', [NullString]::Value) } 'click-through pin recovery window'
            [HotshotSmokeNative]::PostMessage($recovery, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
            Wait-For { [HotshotSmokeNative]::FindWindow([NullString]::Value, 'Hotshot pinned image') } 'pinned mouse input resumed' | Out-Null
            [HotshotSmokeNative]::PostMessage($pin, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
            $rows[96].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            $after = Wait-For { Element $workspace 'Capture capture-96-after.png' ([System.Windows.Automation.ControlType]::ListItem) } 'after capture row'
            $after.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).AddToSelection()
            Workspace-Action $workspace 'Compare selected screenshots'
            $comparison = Wait-For { [HotshotSmokeNative]::FindWindow([NullString]::Value, 'Hotshot screenshot comparison') } 'native visual comparison'
            Wait-For {
                $summary = Element-ById $comparison 'ComparisonSummary'
                $summary -and $summary.Current.Name.Contains('0.01% changed')
            } 'native measured visual diff' | Out-Null
            Click (Element-ById $comparison 'ComparisonActualPixels')
            Wait-For { (Element-ById $comparison 'ComparisonZoomStatus').Current.Name -eq '100%' } 'comparison physical-pixel zoom' | Out-Null
            $mode = Element-ById $comparison 'ComparisonMode'
            $mode.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
            (Wait-For { App-Control 'Swipe' } 'comparison swipe mode').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            (Wait-For { Element-ById $comparison 'ComparisonSwipe' } 'comparison swipe slider after mode selection').GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue(25)
            $ignored = Element-ById $comparison 'ComparisonIgnoredRegions'
            if (-not $ignored) {
                $expander = Element $comparison 'Advanced'
                $expander.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
                $ignored = Wait-For { Element-ById $comparison 'ComparisonIgnoredRegions' } 'ignored comparison regions'
            }
            $ignored.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('10,10,1,1')
            Wait-For { (Element-ById $comparison 'ComparisonDetails').Current.Name.Contains('0 changed / 6,911 compared pixels') } 'ignored pixel excluded from comparison denominator' | Out-Null
            [HotshotSmokeNative]::PostMessage($comparison, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
            foreach ($label in 'Rename capture', 'Organize selected', 'Delete selected', 'Export selected files', 'Save this search',
                'View description progress', 'Extract video frame', 'Trim recording', 'Recover interrupted recording',
                'Storage and privacy', 'Back up library', 'Restore library backup', 'Find missing file',
                'Set project baseline', 'Compare with project baseline', 'Package screenshots') {
                Workspace-Control $workspace $label | Out-Null
                [HotshotSmokeNative]::SetForegroundWindow($workspace) | Out-Null
                [HotshotSmokeNative]::keybd_event(0x1b, 0, 0, [UIntPtr]::Zero)
                [HotshotSmokeNative]::keybd_event(0x1b, 0, 2, [UIntPtr]::Zero)
                Start-Sleep -Milliseconds 100
                [HotshotSmokeNative]::keybd_event(0x1b, 0, 0, [UIntPtr]::Zero)
                [HotshotSmokeNative]::keybd_event(0x1b, 0, 2, [UIntPtr]::Zero)
                Start-Sleep -Milliseconds 100
            }
            $rows[96].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Workspace-Action $workspace 'Export...'
            $name = Wait-For { Element-ById $workspace 'ExportPresetName' } 'named export preset field'
            $name.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Synthetic native export')
            foreach ($entry in @{ ExportWidth = '80'; ExportPadding = '8' }.GetEnumerator()) {
                $number = Element-ById $workspace $entry.Key
                $edit = $number.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsValuePatternAvailableProperty, $true))
                $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($entry.Value)
                $edit.SetFocus()
                [HotshotSmokeNative]::keybd_event(0x0d, 0, 0, [UIntPtr]::Zero)
                [HotshotSmokeNative]::keybd_event(0x0d, 0, 2, [UIntPtr]::Zero)
            }
            Click (Wait-For { App-Control 'Choose destination' } 'export preference confirmation')
            $savePicker = Wait-For { [HotshotSmokeNative]::FindWindow([NullString]::Value, 'Export screenshot') } 'native export file picker'
            $cancelPicker = Wait-For { [HotshotSmokeNative]::GetDlgItem($savePicker, 2) } 'native export cancel'
            [HotshotSmokeNative]::PostMessage($savePicker, 0x111, [IntPtr]2, $cancelPicker) | Out-Null
            Wait-For {
                $saved = Read-AppJson $settingsFile
                $saved.export.last.width -eq 80 -and $saved.export.last.padding -eq 8 -and @($saved.export.presets).Count -eq 1
            } 'export last-style and named preset persistence' | Out-Null
            Wait-For { $control = Element $workspace 'Save'; $control -and $control.Current.IsEnabled } 'export operation idle' | Out-Null
            Workspace-Action $workspace 'Export...'
            Wait-For { Element-ById $workspace 'ExportPresetName' } 'reopened export preset dialog' | Out-Null
            $preset = Element-ById $workspace 'ExportPresets'
            $preset.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
            (Wait-For { App-Control 'Synthetic native export' } 'saved export preset option').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            [HotshotSmokeNative]::SetForegroundWindow($workspace) | Out-Null
            [HotshotSmokeNative]::keybd_event(0x1b, 0, 0, [UIntPtr]::Zero)
            [HotshotSmokeNative]::keybd_event(0x1b, 0, 2, [UIntPtr]::Zero)
            Write-Host 'PASS native named export presets and remembered last style without sharing or writing an export.'
            $rows[96] = Element $workspace 'Capture capture-96.png' ([System.Windows.Automation.ControlType]::ListItem)
            $rows[96].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Workspace-Action $workspace 'Details and tags'
            (Wait-For { Element-ById $workspace 'CaptureProjectName' } 'native project collection field').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Synthetic project')
            $saveDialog = Wait-For {
                $root = [System.Windows.Automation.AutomationElement]::FromHandle($workspace)
                $condition = [System.Windows.Automation.AndCondition]::new(
                    [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Save'),
                    [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsEnabledProperty, $true),
                    [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsInvokePatternAvailableProperty, $true))
                $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            } 'project details confirmation'
            Click $saveDialog
            Wait-For { @(Read-CaptureHistory) | Where-Object { $_.path -eq (Join-Path $output 'capture-96.png') -and $_.project -eq 'Synthetic project' } } 'project assignment persisted' | Out-Null
            Wait-For { $control = Element $workspace 'Save'; $control -and $control.Current.IsEnabled } 'project tools idle' | Out-Null
            Workspace-Action $workspace 'Set project baseline'
            Click (Wait-For { App-Control 'Set baseline' } 'project baseline confirmation')
            Wait-For { @((Read-AppJson $settingsFile).library.baselines).Count -eq 1 } 'project baseline stored in settings' | Out-Null
            Wait-For { $control = Element $workspace 'Save'; $control -and $control.Current.IsEnabled } 'baseline tools idle' | Out-Null
            Workspace-Action $workspace 'Compare with project baseline'
            $baselineWindow = Wait-For { [HotshotSmokeNative]::FindWindow([NullString]::Value, 'Hotshot screenshot comparison') } 'project baseline comparison'
            Wait-For { $summary = Element-ById $baselineWindow 'ComparisonSummary'; $summary -and $summary.Current.Name.Contains('0.00% changed') } 'baseline compares saved evidence' | Out-Null
            [HotshotSmokeNative]::PostMessage($baselineWindow, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
            Write-Host 'PASS native project assignment, favorite baseline persistence and baseline comparison.'
            Write-Host 'PASS native physical-pixel zoom, pin window, multi-selection, exact visual diff and workbench command accessibility.'
            if ($VideoFixture) {
                $video = Element $workspace 'Capture generated-recording.mp4' ([System.Windows.Automation.ControlType]::ListItem)
                $video.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                Wait-For {
                    $preview = Element-ById $workspace 'WorkspaceVideoPreview'
                    $preview -and -not $preview.Current.IsOffscreen
                } 'in-app recording preview' | Out-Null
                Workspace-Action $workspace 'Trim recording'
                $start = Wait-For { Element-ById $workspace 'VideoTrimStart' } 'visual trim start handle'
                $end = Element-ById $workspace 'VideoTrimEnd'
                $start.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue(0.5)
                $end.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).SetValue(1.5)
                Wait-For { (Element-ById $workspace 'VideoTrimSummary').Current.Name.Contains('Kept 1.0s') } 'visual trim kept duration' | Out-Null
                [HotshotSmokeNative]::SetForegroundWindow($workspace) | Out-Null
                [HotshotSmokeNative]::keybd_event(0x1b, 0, 0, [UIntPtr]::Zero)
                [HotshotSmokeNative]::keybd_event(0x1b, 0, 2, [UIntPtr]::Zero)
                Wait-For { -not (Element-ById $workspace 'VideoTrimStart') } 'visual trim canceled' | Out-Null
                Wait-For { $control = Element $workspace 'Copy'; $control -and $control.Current.IsEnabled } 'recording tools idle after trim preview' | Out-Null
                $count = (Read-CaptureHistory).Count
                Workspace-Action $workspace 'Extract video frame'
                Wait-For { (Read-CaptureHistory).Count -eq $count + 1 } 'native video frame saved' 30 | Out-Null
                $frame = @(Read-CaptureHistory)[0]
                $bitmap = [Drawing.Bitmap]::new($frame.path)
                try {
                    $pixel = $bitmap.GetPixel(10, 10)
                    if ($pixel.R -lt 240 -or $pixel.G -lt 55 -or $pixel.G -gt 85) { throw 'Native extracted frame differs from the generated recording.' }
                } finally { $bitmap.Dispose() }
                Write-Host 'PASS native video preview and frame extraction with verified synthetic pixels.'
            }
            return
        }
        foreach ($size in 80, 96, 80, 64) {
            $rows[$size].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        }
        Wait-For { Element $workspace '64 x 48 pixels' } 'latest selection wins during rapid navigation' | Out-Null
        if (-not $rows[64].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) {
            throw 'The selected filename and editor preview are out of sync.'
        }
        $search = Element $workspace 'Search captures' ([System.Windows.Automation.ControlType]::Edit)
        $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('blue geometry')
        Wait-For {
            (Element $workspace 'Capture capture-96.png' ([System.Windows.Automation.ControlType]::ListItem)) -and
                -not (Element $workspace 'Capture capture-64.png' ([System.Windows.Automation.ControlType]::ListItem))
        } 'description matches in history search' | Out-Null
        $describedRow = Element $workspace 'Capture capture-96.png' ([System.Windows.Automation.ControlType]::ListItem)
        $describedRow.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Wait-For { Element $workspace '96 x 72 pixels' } 'described screenshot ready to save' | Out-Null
        Click (Element $workspace 'Save')
        Wait-For {
            $saved = @(Read-CaptureHistory) | Where-Object path -eq (Join-Path $output 'capture-96.png')
            Test-Path -LiteralPath (Join-Path $data ("originals\" + $saved.id + '.json'))
        } 'editor save with embedded description' | Out-Null
        $storedMetadata = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes((Join-Path $output 'capture-96.png')))
        if (-not $storedMetadata.Contains('Synthetic geometry') -or -not $storedMetadata.Contains('Blue geometry on a generated image.')) {
            throw 'Editor saving discarded the screenshot description metadata.'
        }
        $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('')
        $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('finance invoice')
        Wait-For {
            (Element $workspace 'Capture capture-80.png' ([System.Windows.Automation.ControlType]::ListItem)) -and
                -not (Element $workspace 'Capture capture-96.png' ([System.Windows.Automation.ControlType]::ListItem))
        } 'tags and recognized text search' | Out-Null
        $search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('')
        Workspace-Action $workspace 'Favorite'
        Wait-For { @(Read-CaptureHistory) | Where-Object isFavorite } 'favorite persisted' | Out-Null
        (Element-ById $workspace 'CaptureLibraryFilters').GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Wait-For { Element $workspace 'Favorites only' } 'expanded library filters' | Out-Null
        (Toggle-Control $workspace 'Favorites only').Toggle()
        Wait-For {
            (Element $workspace 'Capture capture-96.png' ([System.Windows.Automation.ControlType]::ListItem)) -and
                -not (Element $workspace 'Capture capture-64.png' ([System.Windows.Automation.ControlType]::ListItem))
        } 'favorites filter' | Out-Null
        (Toggle-Control $workspace 'Favorites only').Toggle()
        if (-not (Element $workspace 'Resize capture list')) { throw 'Toolkit history/editor splitter is unavailable.' }
        Write-Host 'PASS filename-only history, automatic editor previews, enabled navigation and latest-selection consistency'
        Write-Host 'PASS stored AI descriptions participate in capture search without thumbnail previews'
        Write-Host 'PASS editor saves preserve embedded summary/description metadata'
        Write-Host 'PASS favorites persist, tags/OCR text search and Toolkit resizable history layout'
        return
    }
    $settings = Wait-For { Settings-Window } 'native settings window'
    if ($AgentSettingsOnly) {
        Select-SettingsPage $settings 'Agents'
        $saved = Read-AppJson $settingsFile
        if ($saved.agents.enabled -or $saved.agents.allowHistoryAccess) { throw 'Agent permissions must default to off.' }
        $access = Toggle-Control $settings 'Allow assistant access'
        $access.Toggle()
        Wait-For { (Read-AppJson $settingsFile).agents.enabled } 'native agent access persistence' | Out-Null
        $historyAccess = Toggle-Control $settings 'Allow saved screenshots'
        $historyAccess.Toggle()
        Wait-For { (Read-AppJson $settingsFile).agents.allowHistoryAccess } 'separate native history permission' | Out-Null
        $historyAccess.Toggle()
        $access.Toggle()
        Wait-For {
            $saved = Read-AppJson $settingsFile
            -not $saved.agents.enabled -and -not $saved.agents.allowHistoryAccess
        } 'native permission disable persistence' | Out-Null
        $picker = Element $settings 'Coding agent' ([System.Windows.Automation.ControlType]::ComboBox)
        foreach ($name in 'GitHub Copilot CLI', 'Claude Code', 'VS Code', 'Cursor', 'Codex') {
            $picker.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
            (Wait-For { App-Control $name } "$name connection option").GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            $manual = Element $settings 'Manual setup'
            $manual.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
            $scroll = Wait-For { Element-ById $settings 'SettingsContentScroll' } 'settings content after picker closes'
            $scroll.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(-1, 50)
            $preview = Wait-For { Element-ById $settings 'AgentMcpConfiguration' } "$name configuration preview"
            $value = $preview.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
            if ($name -eq 'Codex') {
                if (-not $value.StartsWith('[mcp_servers.hotshot]')) { throw 'Codex connection is not TOML.' }
            } else {
                $json = $value | ConvertFrom-Json
                $server = if ($name -eq 'VS Code') { $json.servers.hotshot } else { $json.mcpServers.hotshot }
                if ($server.command -ne (Join-Path (Split-Path $Exe -Parent) 'Mcp\Hotshot.Mcp.exe')) { throw "Wrong $name MCP executable." }
            }
        }
        Assert-EmbeddedSettingsSearch $settings
        Select-SettingsPage $settings 'Capture'
        $review = Toggle-Control $settings 'Review before sending'
        $review.Toggle()
        Wait-For { (Read-AppJson $settingsFile).general.reviewBeforeAiUpload } 'review policy persistence without contacting Copilot' | Out-Null
        $review.Toggle()
        Wait-For { -not (Read-AppJson $settingsFile).general.reviewBeforeAiUpload } 'review policy restoration' | Out-Null
        foreach ($page in 'Agents', 'Capture', 'About', 'General', 'Agents') { Select-SettingsPage $settings $page; Start-Sleep -Milliseconds 150 }
        if (Test-Path -LiteralPath (Join-Path (Split-Path $Exe -Parent) 'Mcp\Hotshot.Mcp.exe')) {
            $scroll = Element-ById $settings 'SettingsContentScroll'
            $scroll.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(-1, 55)
            Click (Wait-For { Element $settings 'Test connection' ([System.Windows.Automation.ControlType]::Button) } 'synthetic native connection test')
            Wait-For { Element-ById $settings 'AgentSyntheticConnectionImage' } 'generated connection image without capture/model request' 30 | Out-Null
            [HotshotSmokeNative]::SetForegroundWindow($settings) | Out-Null
            [HotshotSmokeNative]::keybd_event(0x1b, 0, 0, [UIntPtr]::Zero)
            [HotshotSmokeNative]::keybd_event(0x1b, 0, 2, [UIntPtr]::Zero)
            Wait-For { -not (Element-ById $settings 'AgentSyntheticConnectionImage') } 'connection result closed' | Out-Null
            Write-Host 'PASS stdio companion/native generated-image connection test with access disabled.'
        }
        $scroll = Element-ById $settings 'SettingsContentScroll'
        $scroll.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(-1, 0)
        $access = Toggle-Control $settings 'Allow assistant access'
        $access.Toggle()
        Wait-For { (Read-AppJson $settingsFile).agents.enabled } 'test agent access' | Out-Null
        $timed = Toggle-Control $settings 'Temporary access only'
        $timed.Toggle()
        Wait-For { (Read-AppJson $settingsFile).agents.sessionAccessOnly } 'session policy persistence' | Out-Null
        Click (Element $settings 'Allow temporary access' ([System.Windows.Automation.ControlType]::Button))
        $approval = Toggle-Control $settings 'Ask each time'
        $approval.Toggle()
        Wait-For { (Read-AppJson $settingsFile).agents.approveRequests } 'request approval policy persistence' | Out-Null
        foreach ($decision in 'AgentDeny', 'AgentAllowOnce') {
            $probeInfo = [Diagnostics.ProcessStartInfo]::new('pwsh')
            $probeInfo.UseShellExecute = $false; $probeInfo.RedirectStandardOutput = $true; $probeInfo.RedirectStandardError = $true
            foreach ($argument in '-NoProfile', '-File', (Join-Path $PSScriptRoot 'Hotshot.Agent.Probe.ps1')) { $probeInfo.ArgumentList.Add($argument) }
            $probe = [Diagnostics.Process]::Start($probeInfo)
            $approvalWindow = Wait-For {
                if ($probe.HasExited) { throw "Agent probe exited before consent. $($probe.StandardError.ReadToEnd()) $($probe.StandardOutput.ReadToEnd())" }
                [HotshotSmokeNative]::FindWindow([NullString]::Value, 'Hotshot agent approval')
            } 'native per-request consent'
            Click (Element-ById $approvalWindow $decision)
            if (-not $probe.WaitForExit(10000)) { throw 'Read-only agent probe did not finish after native consent.' }
            $reply = $probe.StandardOutput.ReadToEnd() | ConvertFrom-Json
            if ($decision -eq 'AgentDeny' -and $reply.errorCode -ne 'approval_denied') { throw 'Native denial was bypassed.' }
            if ($decision -eq 'AgentAllowOnce' -and -not $reply.success) { throw 'Native one-time approval did not allow the read-only request.' }
        }
        Click (Element $settings 'End access' ([System.Windows.Automation.ControlType]::Button))
        $approval.Toggle(); $timed.Toggle(); $access.Toggle()
        Wait-For { $saved = Read-AppJson $settingsFile; -not $saved.agents.enabled -and -not $saved.agents.sessionAccessOnly -and -not $saved.agents.approveRequests } 'agent test permissions disabled again' | Out-Null
        Write-Host 'PASS native timed-session grant/revoke and deny/allow-once consent for a read-only local client.'
        Select-SettingsPage $settings 'About'
        $scroll = Element-ById $settings 'SettingsContentScroll'
        $scroll.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(-1, 100)
        Click (Wait-For { Element $settings 'Show details' ([System.Windows.Automation.ControlType]::Button) } 'diagnostics command')
        $report = Wait-For { Element-ById $settings 'DiagnosticsReport' } 'native diagnostic report'
        Wait-For {
            $report = Element-ById $settings 'DiagnosticsReport'
            if (-not $report) { return $false }
            $value = $report.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
            $value.Contains('Resident startup:') -and $value.Contains('Working set:') -and $value.Contains('Library:')
        } 'diagnostic startup, memory and library metrics realized' | Out-Null
        [HotshotSmokeNative]::SetForegroundWindow($settings) | Out-Null
        [HotshotSmokeNative]::keybd_event(0x1b, 0, 0, [UIntPtr]::Zero)
        [HotshotSmokeNative]::keybd_event(0x1b, 0, 2, [UIntPtr]::Zero)
        Wait-For { -not (Element-ById $settings 'DiagnosticsReport') } 'diagnostic dialog closed' | Out-Null
        Select-SettingsPage $settings 'Agents'
        Write-Host 'PASS native agent permissions autosave independently, remain off, and five client configurations resolve the bundled companion.'
        Write-Host 'PASS native diagnostics expose real startup, memory, installed size and library health.'
        return
    }
    if (-not $SettingsCaptureOnly -and -not $HeaderSearchOnly) {
        foreach ($page in 'General', 'Hotkeys', 'Naming', 'Capture', 'Recording', 'Gif', 'About') {
            Select-SettingsPage $settings $page
            Start-Sleep -Milliseconds 100
        }
        Select-SettingsPage $settings 'General'
        $settingsScroll = Element-ById $settings 'SettingsContentScroll'
        $settingsScroll.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(-1, 100)
        Wait-For { Element-ById $settings 'SettingsSyncStatus' } 'native settings-across-devices status' | Out-Null
        foreach ($label in 'Use OneDrive preferences','Choose another OneDrive folder','Use local preferences only','Reload shared preferences') {
            Wait-For { Element $settings $label ([System.Windows.Automation.ControlType]::Button) } "settings synchronization control $label" | Out-Null
        }
        $settingsScroll.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern).SetScrollPercent(-1, 0)
        $copyToggle = Toggle-Control $settings 'Copy captures to clipboard'
        $copyToggle.Toggle()
        Wait-For { -not (Read-AppJson $settingsFile).general.copyToClipboard } 'automatic settings save' | Out-Null
        $copyToggle.Toggle()
        Wait-For { (Read-AppJson $settingsFile).general.copyToClipboard } 'automatic clipboard setting restore' | Out-Null
        $savedSettings = Read-AppJson $settingsFile
        if ($savedSettings.general.showPreview -or $savedSettings.general.openEditorAfterCapture) { throw 'Legacy profile still opens capture windows.' }
        if (Element $settings 'Save settings') { throw 'Settings still require a manual Save button.' }
        Write-Host 'PASS adaptive settings modules, automatic persistence and legacy silent-capture migration'
    }

    if ($SettingsCaptureOnly) {
        if (-not [HotshotSmokeNative]::SetWindowPos($settings, [IntPtr](-1), 0, 0, 0, 0, 0x13)) {
            throw 'The test Settings window could not be kept visible for the capture check.'
        }
        Start-Sleep -Milliseconds 250
        [uint]$affinity = 99
        if ([HotshotSmokeNative]::GetWindowDisplayAffinity($settings, [ref]$affinity) -and $affinity -ne 0) {
            throw 'Settings is still excluded from screenshots.'
        }
        [HotshotSmokeNative+Rect]$frame = [HotshotSmokeNative+Rect]::new()
        if ([HotshotSmokeNative]::GetFrameBounds($settings, 9, [ref]$frame, 16) -ne 0) {
            throw 'Settings frame bounds could not be read.'
        }
        [HotshotSmokeNative]::SetForegroundWindow($settings) | Out-Null
        $searchInput = (Element-ById $settings 'SettingsSearch').FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsValuePatternAvailableProperty, $true))
        $searchInput.SetFocus()
        Start-Sleep -Milliseconds 300
        $expectedPixels = @{}
        foreach ($offset in 80, 160, 240) {
            $expectedPixels[$offset] = [HotshotSmokeNative]::ScreenPixel($frame.Left + $offset, $frame.Top + $offset)
            if ($expectedPixels[$offset] -eq [uint32]::MaxValue) {
                throw 'Desktop pixels are unavailable. Reconnect to the Windows session and unlock it before running this capture check.'
            }
        }
        Command '--region'
        $overlay = Wait-For { [HotshotSmokeNative]::FindWindow('Hotshot.CaptureOverlay', [NullString]::Value) } 'settings capture overlay'
        if (-not [HotshotSmokeNative]::SetPhysicalCursorPos([int](($frame.Left + $frame.Right) / 2), [int](($frame.Top + $frame.Bottom) / 2))) {
            throw 'The Settings capture pointer could not be positioned on the interactive desktop.'
        }
        Start-Sleep -Milliseconds 100
        [HotshotSmokeNative]::mouse_event(0x2, 0, 0, 0, [UIntPtr]::Zero)
        [HotshotSmokeNative]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero)
        Wait-For { (Read-CaptureHistory).Count -eq 1 } 'settings screenshot saved' | Out-Null
        $capture = @(Read-CaptureHistory)[0]
        if ($capture.kind -ne 'window' -or $capture.width -ne ($frame.Right - $frame.Left) -or
            $capture.height -ne ($frame.Bottom - $frame.Top)) {
            throw "Click-to-select did not capture the Settings window. Actual: $($capture.kind) $($capture.width)x$($capture.height), process $($capture.appName); expected Window $(($frame.Right - $frame.Left))x$(($frame.Bottom - $frame.Top))."
        }
        if (-not [HotshotSmokeNative]::IsClipboardFormatAvailable(8)) { throw 'Settings screenshot was not copied to the clipboard.' }
        Add-Type -AssemblyName System.Drawing
        $bitmap = [Drawing.Bitmap]::new($capture.path)
        try {
            $matches = 0
            foreach ($offset in 80, 160, 240) {
                $pixel = $bitmap.GetPixel($offset, $offset)
                $expected = $expectedPixels[$offset]
                $actual = [uint32]$pixel.R -bor ([uint32]$pixel.G -shl 8) -bor ([uint32]$pixel.B -shl 16)
                Write-Host ("Settings pixel {0}: expected=0x{1:X6}, actual=0x{2:X6}" -f $offset, $expected, $actual)
                if ($expected -eq $actual) { $matches++ }
            }
            if ($matches -lt 2) { throw 'Settings screenshot pixels do not match the visible Settings window.' }
        } finally { $bitmap.Dispose() }
        Write-Host 'PASS capturable Settings affinity, click-to-select window capture, visible pixels and clipboard output'
        Command '--scrolling'
        $overlay = Wait-For { [HotshotSmokeNative]::FindWindow('Hotshot.CaptureOverlay', [NullString]::Value) } 'guided scrolling selection'
        $left = $frame.Left + 100 - [HotshotSmokeNative]::GetSystemMetrics(76)
        $top = $frame.Top + 100 - [HotshotSmokeNative]::GetSystemMetrics(77)
        $start = [IntPtr](($top -shl 16) -bor ($left -band 0xffff))
        $end = [IntPtr]((($top + 120) -shl 16) -bor (($left + 200) -band 0xffff))
        [HotshotSmokeNative]::PostMessage($overlay, 0x201, [IntPtr]1, $start) | Out-Null
        [HotshotSmokeNative]::PostMessage($overlay, 0x200, [IntPtr]1, $end) | Out-Null
        [HotshotSmokeNative]::PostMessage($overlay, 0x202, [IntPtr]::Zero, $end) | Out-Null
        $scrolling = Wait-For { [HotshotSmokeNative]::FindWindow([NullString]::Value, 'Hotshot scrolling capture') } 'guided scrolling controls'
        Wait-For { Element $scrolling 'Add next frame' } 'scrolling append control' | Out-Null
        Click (Element $scrolling 'Cancel')
        Wait-For { [HotshotSmokeNative]::FindWindow([NullString]::Value, 'Hotshot scrolling capture') -eq [IntPtr]::Zero } 'scrolling cancellation' | Out-Null
        if ((Read-CaptureHistory).Count -ne 1) { throw 'Canceling guided scrolling saved an unwanted capture.' }
        Command '--region'
        $overlay = Wait-For { [HotshotSmokeNative]::FindWindow('Hotshot.CaptureOverlay', [NullString]::Value) } 'keyboard capture overlay'
        [HotshotSmokeNative]::SetPhysicalCursorPos($frame.Left + 100, $frame.Top + 100) | Out-Null
        Start-Sleep -Milliseconds 100
        [HotshotSmokeNative]::PostMessage($overlay, 0x100, [IntPtr]82, [IntPtr]::Zero) | Out-Null
        [HotshotSmokeNative]::PostMessage($overlay, 0x100, [IntPtr]39, [IntPtr]::Zero) | Out-Null
        [HotshotSmokeNative]::PostMessage($overlay, 0x100, [IntPtr]13, [IntPtr]::Zero) | Out-Null
        Wait-For { (Read-CaptureHistory).Count -eq 2 } 'keyboard-selected region' | Out-Null
        $keyboard = @(Read-CaptureHistory)[0]
        if ($keyboard.kind -ne 'region' -or $keyboard.width -ne 102 -or $keyboard.height -ne 61) { throw 'Keyboard region geometry is wrong.' }
        Write-Host 'PASS guided scrolling controls/cancel without saving and keyboard-only region selection.'
        return
    }

    if ($SettingsControlsOnly) {
        if (Element $settings 'Captures folder' ([System.Windows.Automation.ControlType]::Edit)) {
            throw 'Captures folder still uses an editable textbox.'
        }
        $browse = Element-ById $settings 'CapturesFolderPicker'
        Click $browse
        $picker = Wait-For { [HotshotSmokeNative]::FindWindow([NullString]::Value, 'Choose captures folder') } 'native folder picker'
        # PowerShell's managed UIA exposes native picker buttons as panes without InvokePattern.
        $cancel = Wait-For { [HotshotSmokeNative]::GetDlgItem($picker, 2) } 'native picker Cancel control'
        if (-not [HotshotSmokeNative]::PostMessage($picker, 0x111, [IntPtr]2, $cancel)) { throw 'Could not cancel the folder picker.' }
        Wait-For { $browse.Current.IsEnabled } 'folder picker cancellation' | Out-Null
        if ((Read-AppJson $settingsFile).general.saveFolder -ne $output) { throw 'Canceling changed the captures folder.' }
        Write-Host 'PASS native folder picker and cancellation without changing the saved folder'

        $chosenFolder = Join-Path $output 'chosen'
        [IO.Directory]::CreateDirectory($chosenFolder) | Out-Null
        Click $browse
        $picker = Wait-For { [HotshotSmokeNative]::FindWindow([NullString]::Value, 'Choose captures folder') } 'native folder picker selection'
        $folder = Wait-For { Element $picker 'chosen' ([System.Windows.Automation.ControlType]::ListItem) } 'folder picker directory entry'
        $folder.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        [HotshotSmokeNative]::SetForegroundWindow($picker) | Out-Null
        $commit = Wait-For { [HotshotSmokeNative]::GetDlgItem($picker, 1) } 'folder picker commit button'
        if (-not [HotshotSmokeNative]::PostMessage($picker, 0x111, [IntPtr]1, $commit)) { throw 'Could not select the chosen folder.' }
        Wait-For { (Read-AppJson $settingsFile).general.saveFolder -eq $chosenFolder } 'selected folder automatic save' | Out-Null
        if ((Element-ById $settings 'CapturesFolderPath').Current.Name -ne $chosenFolder) { throw 'Selected folder is not displayed.' }
        Write-Host 'PASS selected captures folder is displayed and saved automatically'

        Select-SettingsPage $settings 'Naming'
        $year = Get-Date -Format yyyy
        Set-Pattern $settings 'NamingRegionPattern' 'capture_'
        Select-NamingToken $settings 'NamingRegionTokens' "{yyyy} - Year. Example: $year"
        Wait-For { (Read-AppJson $settingsFile).naming.screenshotPattern -eq 'capture_{yyyy}' } 'year token inserted at cursor' | Out-Null
        if ((Element-ById $settings 'NamingRegionPreview').Current.Name -ne "Example: capture_$year.png") {
            throw 'Screenshot preview does not match the inserted token example.'
        }
        Set-Pattern $settings 'NamingRegionPattern' 'capture_{yyyy}_'
        Select-NamingToken $settings 'NamingRegionTokens' '{counter:4} - Incrementing number, 4 digits. Example: 0042'
        Wait-For { (Read-AppJson $settingsFile).naming.screenshotPattern -eq 'capture_{yyyy}_{counter:4}' } 'padded counter insertion' | Out-Null
        if ((Element-ById $settings 'NamingRegionPreview').Current.Name -ne "Example: capture_$($year)_0042.png") {
            throw 'Padded counter preview does not match its dropdown example.'
        }
        Set-Pattern $settings 'NamingRecordingPattern' 'video_'
        Select-NamingToken $settings 'NamingRecordingTokens' '{type} - Capture type. Example: recording'
        Wait-For { (Read-AppJson $settingsFile).naming.recordingPattern -eq 'video_{type}' } 'recording type token insertion' | Out-Null
        if ((Element-ById $settings 'NamingRecordingPreview').Current.Name -ne 'Example: video_recording.mp4') {
            throw 'Recording token preview has the wrong capture type or extension.'
        }
        Write-Host 'PASS token dropdown examples, cursor insertion, padded counters and capture-specific filename previews'
        return
    }

    if ($ThemeSearchOnly -or $HeaderSearchOnly) {
        $search = Wait-For { Element-ById $settings 'SettingsSearch' } 'title bar settings search'
        if ($search.Current.IsOffscreen) { throw 'Search is hidden when the sidebar collapses.' }
        $windowBounds = [System.Windows.Automation.AutomationElement]::FromHandle($settings).Current.BoundingRectangle
        $dpi = [HotshotSmokeNative]::GetDpiForWindow($settings) / 96.0
        if (($search.Current.BoundingRectangle.Top - $windowBounds.Top) / $dpi -gt 100) { throw 'Search is not in the window header.' }
        Assert-EmbeddedSettingsSearch $settings
        $theme = Element $settings 'App theme' ([System.Windows.Automation.ControlType]::ComboBox)
        foreach ($choice in 'Dark', 'Light', 'Dark') {
            $theme.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
            $item = Wait-For { App-Control $choice } "$choice theme option"
            $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Wait-For { (Read-AppJson $settingsFile).general.theme -eq $choice.ToLowerInvariant() } 'theme persistence' | Out-Null
            Wait-For {
                [int]$mode = -1
                [HotshotSmokeNative]::DwmGetWindowAttribute($settings, 20, [ref]$mode, 4) -eq 0 -and
                    $mode -eq $(if ($choice -eq 'Dark') { 1 } else { 0 })
            } 'window chrome follows app theme' | Out-Null
        }
        Write-Host 'PASS dark/light app theme updates native title bar mode immediately'
        $editableSearch = $search.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsValuePatternAvailableProperty, $true))
        if (-not $editableSearch) { throw 'Title bar search has no accessible editable input.' }
        $editableSearch.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('microphone')
        $editableSearch.SetFocus()
        $compactWidths = @()
        $inlineWidths = @()
        foreach ($width in 520, 900, 750, 780, 400, 900, 520) {
            $pixels = [int]($width * $dpi)
            if (-not [HotshotSmokeNative]::SetWindowPos($settings, [IntPtr]::Zero, 0, 0,
                $pixels, [int]$windowBounds.Height, 0x16)) { throw 'Settings window resize failed.' }
            Start-Sleep -Milliseconds 250
            $bounds = [System.Windows.Automation.AutomationElement]::FromHandle($settings).Current.BoundingRectangle
            $searchBounds = (Element-ById $settings 'SettingsSearch').Current.BoundingRectangle
            Write-Host ("Search layout at {0} DIPs: window={1:N0}, search={2:N0}, left={3:N0}, top={4:N0}" -f
                $width, ($bounds.Width / $dpi), ($searchBounds.Width / $dpi),
                (($searchBounds.Left - $bounds.Left) / $dpi), (($searchBounds.Top - $bounds.Top) / $dpi))
            Wait-For {
                $search = Element-ById $settings 'SettingsSearch'
                $bounds = [System.Windows.Automation.AutomationElement]::FromHandle($settings).Current.BoundingRectangle
                $searchBounds = $search.Current.BoundingRectangle
                $headerBounds = (Element-ById $settings 'SettingsTitleBar').Current.BoundingRectangle
                return $searchBounds.Top -ge $headerBounds.Top -and $searchBounds.Bottom -le $headerBounds.Bottom -and
                    $searchBounds.Width -ge 120 * $dpi
            } "responsive search at $width DIPs" | Out-Null
            Assert-EmbeddedSettingsSearch $settings
            $actualWidth = [int]($bounds.Width / $dpi)
            if ([HotshotSmokeNative]::ClientWidth($settings) / $dpi -lt 760) { $compactWidths += $actualWidth }
            else { $inlineWidths += $actualWidth }
            $search = Element-ById $settings 'SettingsSearch'
            $editableSearch = $search.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsValuePatternAvailableProperty, $true))
            if ($search.Current.IsOffscreen -or
                $editableSearch.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'microphone') {
                throw 'Resizing hid search or lost the query.'
            }
            Wait-For { $editableSearch.Current.HasKeyboardFocus } 'search focus preserved after resize' | Out-Null
        }
        Write-Host "PASS embedded compact search at $($compactWidths | Sort-Object -Unique) DIPs, no caption overlap and preserved query/focus"
        if ($inlineWidths.Count -gt 0) { Write-Host "PASS inline search at $($inlineWidths | Sort-Object -Unique) DIPs" }
        else { Write-Host 'SKIP inline search resizing: this desktop clamps the window below the wide-layout breakpoint.' }
        $query = Element-ById $settings 'QueryButton'
        if (-not $query -and $search.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::IsInvokePatternAvailableProperty)) { $query = $search }
        if (-not $query) {
            $scope = Element-ById $settings 'SettingsSearch'
            $query = $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsInvokePatternAvailableProperty, $true))
        }
        Click $query
        $result = Wait-For { App-Control 'Record microphone - Screen recording' } 'per-setting microphone search result'
        Click $result
        Wait-For { Element $settings 'Record microphone' } 'jump to microphone setting' | Out-Null
        $editableSearch.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('zzzznonexistent')
        Click $query
        Wait-For { Element $settings 'No settings match "zzzznonexistent".' } 'no-match search result' | Out-Null
        if ((Element-ById $settings 'SettingsSearch').Current.IsOffscreen) { throw 'Search disappeared during navigation.' }
        Write-Host 'PASS title bar search, setting-specific results/navigation and explicit no-match state'
        return
    }

    if ($VerifyStartup) {
        Select-SettingsPage $settings 'General'
        $toggle = Toggle-Control $settings 'Start with Windows'
        $toggle.Toggle()
        Wait-For { (Get-StartupValue) -eq "`"$Exe`" --background" } 'release startup registration' | Out-Null
        $toggle.Toggle()
        Wait-For { $null -eq (Get-StartupValue) } 'release startup removal' | Out-Null
        Write-Host 'PASS release Start with Windows enable/disable'
    }

    Command '--monitor'
    Wait-For { (Read-CaptureHistory).Count -eq 1 } 'monitor screenshot' | Out-Null
    if (-not [HotshotSmokeNative]::IsClipboardFormatAvailable(8) -or
        -not [HotshotSmokeNative]::IsClipboardFormatAvailable([HotshotSmokeNative]::RegisterClipboardFormat('PNG'))) {
        throw 'Screenshot clipboard formats are missing.'
    }
    Write-Host 'PASS command forwarding, monitor PNG and clipboard DIB+PNG'

    [HotshotSmokeNative]::SetForegroundWindow($settings) | Out-Null
    foreach ($key in 0x10, 0x11, 0x12) { [HotshotSmokeNative]::keybd_event($key, 0, 2, [UIntPtr]::Zero) }
    try {
        [HotshotSmokeNative]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
        [HotshotSmokeNative]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 50
        [HotshotSmokeNative]::keybd_event(0x79, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 50
    } finally {
        [HotshotSmokeNative]::keybd_event(0x79, 0, 2, [UIntPtr]::Zero)
        [HotshotSmokeNative]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
        [HotshotSmokeNative]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
    }
    Wait-For { (Read-CaptureHistory).Count -eq 2 } 'configured global full-screen hotkey' | Out-Null
    Write-Host 'PASS configured global full-screen hotkey'

    Command '--region'
    Select-Region
    Wait-For { (Read-CaptureHistory).Count -eq 3 } 'region screenshot' | Out-Null
    $region = @(Read-CaptureHistory)[0]
    if ($region.width -ne 160 -or $region.height -ne 120) { throw 'Region dimensions are wrong.' }
    if ($region.path -notlike '*\????\??\*_0003.png') { throw 'Naming tokens/counter were not applied.' }
    Write-Host 'PASS exact region dimensions and year/month/timestamp/counter naming'
    Start-Sleep -Milliseconds 700
    if ([HotshotSmokeNative]::FindWindow([NullString]::Value, 'Hotshot capture') -ne [IntPtr]::Zero -or
        [HotshotSmokeNative]::FindWindow([NullString]::Value, 'Hotshot editor') -ne [IntPtr]::Zero) {
        throw 'A capture opened a preview/editor automatically.'
    }
    Write-Host 'PASS screenshots remain silent'

    $trayOwner = [HotshotSmokeNative]::FindWindow('Hotshot.MessageWindow', [NullString]::Value)
    if ($trayOwner -eq [IntPtr]::Zero) { throw 'Tray owner window is missing.' }
    [HotshotSmokeNative]::PostMessage($trayOwner, 0x8001, [IntPtr]::Zero, [IntPtr]0x10400) | Out-Null
    $workspace = Wait-For { [HotshotSmokeNative]::FindWindow([NullString]::Value, 'Hotshot editor') } 'editor and history workspace'
    if (-not (Element $workspace 'Choose a capture to edit')) { throw 'Workspace opened the latest screenshot automatically.' }
    [HotshotSmokeNative]::PostMessage($trayOwner, 0x8001, [IntPtr]::Zero, [IntPtr]0x10400) | Out-Null
    Start-Sleep -Milliseconds 200
    if ([HotshotSmokeNative]::FindWindow([NullString]::Value, 'Hotshot editor') -ne $workspace) {
        throw 'A second tray activation closed or replaced the workspace.'
    }
    Write-Host 'PASS tray activation opens/focuses editor and history without selecting the latest screenshot'
    $row = Element $workspace ("Capture " + [IO.Path]::GetFileName($region.path)) ([System.Windows.Automation.ControlType]::ListItem)
    $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $canvas = Wait-For { Element-ById $workspace 'AnnotationCanvas' } 'annotation canvas'
    Wait-For { $tool = Element $workspace 'Annotation tool'; $tool -and $tool.Current.IsEnabled -and
        (Element $workspace "$($region.width) x $($region.height) pixels") } 'image loaded for editing' | Out-Null
    [HotshotSmokeNative]::SetForegroundWindow($workspace) | Out-Null
    $bounds = $canvas.Current.BoundingRectangle
    $dpi = [HotshotSmokeNative]::GetDpiForWindow($workspace) / 96.0
    $scale = [Math]::Min(($bounds.Width - 32 * $dpi) / $region.width, ($bounds.Height - 32 * $dpi) / $region.height)
    $left = $bounds.Left + ($bounds.Width - $region.width * $scale) / 2
    $top = $bounds.Top + ($bounds.Height - $region.height * $scale) / 2
    [HotshotSmokeNative]::SetPhysicalCursorPos([int]($left + 32 * $scale), [int]($top + 36 * $scale)) | Out-Null
    [HotshotSmokeNative]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 100
    [HotshotSmokeNative]::SetPhysicalCursorPos([int]($left + 128 * $scale), [int]($top + 84 * $scale)) | Out-Null
    Start-Sleep -Milliseconds 100
    [HotshotSmokeNative]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    Control-Key 0x53
    $annotations = Join-Path $data ("originals\" + $region.id + '.json')
    $original = Join-Path $data ("originals\" + $region.id + '.png')
    Wait-For { Test-Path -LiteralPath $annotations } 'editor saved annotations' | Out-Null
    $document = Get-Content -Raw $annotations | ConvertFrom-Json
    if ($document.annotations.Count -ne 1 -or $document.annotations[0].type -ne 'arrow') { throw 'Drawn arrow was not preserved.' }
    if (-not (Test-Path -LiteralPath $original)) { throw 'Editor did not keep the pristine original.' }
    Write-Host 'PASS history starts unselected, real annotation drawing, Save and original/sidecar preservation'
    Control-Key 0x43
    Start-Sleep -Milliseconds 500
    [System.Windows.Automation.AutomationElement]::FromHandle($workspace).GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()

    Command '--record'
    Select-Region
    $recording = Wait-For { [HotshotSmokeNative]::FindWindow([NullString]::Value, 'Hotshot recording') } 'recording controls'
    $pause = Wait-For { $button = Element $recording 'Pause'; if ($button -and $button.Current.IsEnabled) { $button } } 'recorder ready'
    Start-Sleep -Seconds 1
    Click $pause
    Start-Sleep -Milliseconds 300
    Click (Element $recording 'Resume')
    Start-Sleep -Seconds 1
    Click (Element $recording 'Stop and save')
    Wait-For { (Read-CaptureHistory).Count -eq 4 } 'MP4 recording output' | Out-Null
    Write-Host 'PASS region recording with pause/resume and MP4 output'

    Command '--history'
    $history = Wait-For { [HotshotSmokeNative]::FindWindow([NullString]::Value, 'Hotshot editor') } 'native editor/history'
    $video = @(Read-CaptureHistory)[0]
    $row = Element $history ("Capture " + [IO.Path]::GetFileName($video.path)) ([System.Windows.Automation.ControlType]::ListItem)
    $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-For { Element $history "$($video.width) x $($video.height) - open or copy this recording using the toolbar." } 'video actions enabled' | Out-Null
    $more = Element-ById $history 'MoreButton'
    if (-not $more) {
        foreach ($name in 'More', 'See more', 'More options') {
            $more = Element $history $name
            if ($more) { break }
        }
    }
    if ($more) { Click $more }
    $convert = Wait-For { App-Control 'Convert to GIF' } 'GIF command in toolbar overflow'
    Click $convert
    Wait-For { (Read-CaptureHistory).Count -eq 5 } 'GIF conversion output' 30 | Out-Null
    $items = @(Read-CaptureHistory)
    if (($items | Where-Object kind -eq 'gif').Count -ne 1 -or
        ($items | Where-Object kind -eq 'recording').Count -ne 1) {
        throw 'GIF conversion did not preserve the MP4 in history.'
    }
    foreach ($capture in $items) {
        if (-not (Test-Path -LiteralPath $capture.path) -or (Get-Item -LiteralPath $capture.path).Length -eq 0) {
            throw 'A history output is missing or empty.'
        }
    }
    Write-Host 'PASS native history and GIF conversion with original MP4 preserved'
} catch {
    Write-Host $_.ScriptStackTrace
    throw
} finally {
    $shutdownFailed = $false
    if ($process -and -not $process.HasExited) {
        Command '--exit'
        if (-not $process.WaitForExit(15000)) { Stop-Process -Id $process.Id; $shutdownFailed = $true }
    }
    $items = @(Read-CaptureHistory)
    foreach ($capture in $items) {
        if ($capture.path.StartsWith($output + '\', [StringComparison]::OrdinalIgnoreCase) -and
            $capture.thumbnailPath -and (Test-Path -LiteralPath $capture.thumbnailPath)) {
            Remove-Item -LiteralPath $capture.thumbnailPath
        }
        if ($capture.path.StartsWith($output + '\', [StringComparison]::OrdinalIgnoreCase)) {
            foreach ($extension in '.png', '.json') {
                $sidecar = Join-Path $data ("originals\" + $capture.id + $extension)
                if (Test-Path -LiteralPath $sidecar) { Remove-Item -LiteralPath $sidecar }
            }
        }
    }
    if ($null -ne $backupSettings) { [IO.File]::WriteAllBytes($settingsFile, $backupSettings) }
    else { Remove-Item -LiteralPath $settingsFile -ErrorAction SilentlyContinue }
    if ($null -ne $backupHistory) { [IO.File]::WriteAllBytes($historyFile, $backupHistory) }
    else { Remove-Item -LiteralPath $historyFile -ErrorAction SilentlyContinue }
    if ($null -ne $backupHistoryRecovery) { [IO.File]::WriteAllBytes($historyBackupFile, $backupHistoryRecovery) }
    else { Remove-Item -LiteralPath $historyBackupFile -ErrorAction SilentlyContinue }
    if ($null -ne $backupHistoryJournal) { [IO.File]::WriteAllBytes($historyJournalFile, $backupHistoryJournal) }
    else { Remove-Item -LiteralPath $historyJournalFile -ErrorAction SilentlyContinue }
    if ($null -ne $backupRecordingJournal) { [IO.File]::WriteAllBytes($recordingJournal, $backupRecordingJournal) }
    else { Remove-Item -LiteralPath $recordingJournal -ErrorAction SilentlyContinue }
    if ($VerifyStartup) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($runKeyPath)
        try {
            if ($null -ne $backupStartup) { $key.SetValue('Hotshot', $backupStartup, $backupStartupKind) }
            else { $key.DeleteValue('Hotshot', $false) }
        } finally { $key.Dispose() }
    }
    Write-Host "Smoke outputs (local only): $output"
    if ($shutdownFailed) { throw 'Hotshot did not exit gracefully.' }
}
