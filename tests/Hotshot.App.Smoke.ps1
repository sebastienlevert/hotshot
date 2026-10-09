[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Exe,

    [switch]$VerifyStartup,

    [switch]$ThemeSearchOnly,

    [switch]$SettingsControlsOnly,

    [switch]$HistoryNavigationOnly
)

$ErrorActionPreference = 'Stop'
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
    [DllImport("user32.dll")]
    public static extern IntPtr GetDlgItem(IntPtr hwnd, int id);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
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
}
'@

$data = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Hotshot'
$settingsFile = Join-Path $data 'settings.json'
$historyFile = Join-Path $data 'history.json'
$backupSettings = if (Test-Path $settingsFile) { [IO.File]::ReadAllBytes($settingsFile) } else { $null }
$backupHistory = if (Test-Path $historyFile) { [IO.File]::ReadAllBytes($historyFile) } else { $null }
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
    if (Test-Path $historyFile) { @(Read-AppJson $historyFile) }
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

function Element-ById([IntPtr]$Hwnd, [string]$Id) {
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($Hwnd)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Select-SettingsPage([IntPtr]$Hwnd, [string]$Page) {
    $item = Element-ById $Hwnd "Settings$Page"
    if (-not $item) {
        Click (Element-ById $Hwnd 'TogglePaneButton')
        $item = Wait-For { Element-ById $Hwnd "Settings$Page" } "settings navigation $Page"
    }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $pane = Element-ById $Hwnd 'TogglePaneButton'
    if ($pane -and $pane.Current.Name -eq 'Close Navigation') { Click $pane }
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
    }
    [IO.File]::WriteAllText($settingsFile, ($fixture | ConvertTo-Json -Depth 5))
    [IO.File]::WriteAllText($historyFile, '[]')
    if ($HistoryNavigationOnly) {
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
            }
        }
        [IO.File]::WriteAllText($historyFile, (ConvertTo-Json -InputObject $fixtures -Depth 4))
    }
    $startupCommand = if ($HistoryNavigationOnly) { '--history' } else { '--settings' }
    $process = Start-Process -FilePath $Exe -ArgumentList $startupCommand -PassThru
    if ($HistoryNavigationOnly) {
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
            $rows[$size] = Element $workspace "Capture capture-$size.png" ([System.Windows.Automation.ControlType]::ListItem)
            if (-not $rows[$size]) { throw "Filename history row is missing: capture-$size.png" }
            $rows[$size].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Wait-For { Element $workspace "$size x $([int]($size * 0.75)) pixels" } "automatic editor preview for capture-$size.png" | Out-Null
            if (-not $list.Current.IsEnabled) { throw 'Filename history was disabled while browsing.' }
        }
        foreach ($size in 80, 96, 80, 64) {
            $rows[$size].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        }
        Wait-For { Element $workspace '64 x 48 pixels' } 'latest selection wins during rapid navigation' | Out-Null
        if (-not $rows[64].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) {
            throw 'The selected filename and editor preview are out of sync.'
        }
        Write-Host 'PASS filename-only history, automatic editor previews, enabled navigation and latest-selection consistency'
        return
    }
    $settings = Wait-For { [HotshotSmokeNative]::FindWindow([NullString]::Value, 'Hotshot settings') } 'native settings window'
    foreach ($page in 'General', 'Hotkeys', 'Naming', 'Capture', 'Recording', 'Gif', 'About') {
        Select-SettingsPage $settings $page
        Start-Sleep -Milliseconds 100
    }
    Select-SettingsPage $settings 'General'
    $copyToggle = Toggle-Control $settings 'Copy captures to clipboard'
    $copyToggle.Toggle()
    Wait-For { -not (Read-AppJson $settingsFile).general.copyToClipboard } 'automatic settings save' | Out-Null
    $copyToggle.Toggle()
    Wait-For { (Read-AppJson $settingsFile).general.copyToClipboard } 'automatic clipboard setting restore' | Out-Null
    $savedSettings = Read-AppJson $settingsFile
    if ($savedSettings.general.showPreview -or $savedSettings.general.openEditorAfterCapture) { throw 'Legacy profile still opens capture windows.' }
    if (Element $settings 'Save settings') { throw 'Settings still require a manual Save button.' }
    Write-Host 'PASS adaptive settings modules, automatic persistence and legacy silent-capture migration'

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

    if ($ThemeSearchOnly) {
        $search = Wait-For { Element-ById $settings 'SettingsSearch' } 'title bar settings search'
        if ($search.Current.IsOffscreen) { throw 'Search is hidden when the sidebar collapses.' }
        $windowBounds = [System.Windows.Automation.AutomationElement]::FromHandle($settings).Current.BoundingRectangle
        $dpi = [HotshotSmokeNative]::GetDpiForWindow($settings) / 96.0
        if (($search.Current.BoundingRectangle.Top - $windowBounds.Top) / $dpi -gt 100) { throw 'Search is not in the window header.' }
        if ([HotshotSmokeNative]::ClientWidth($settings) / $dpi -lt 760 -and
            $search.Current.BoundingRectangle.Width -lt $windowBounds.Width * 0.85) {
            throw 'Compact header search does not use at least 85% of the narrow window width.'
        }
        $theme = Element $settings 'App theme' ([System.Windows.Automation.ControlType]::ComboBox)
        foreach ($choice in 'Dark', 'Light', 'Dark') {
            $theme.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
            $item = Wait-For { App-Control $choice } "$choice theme option"
            $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Wait-For { (Read-AppJson $settingsFile).general.theme -eq $choice.ToLowerInvariant() } 'theme persistence' | Out-Null
            [int]$mode = -1
            if ([HotshotSmokeNative]::DwmGetWindowAttribute($settings, 20, [ref]$mode, 4) -ne 0 -or
                $mode -ne $(if ($choice -eq 'Dark') { 1 } else { 0 })) { throw 'Window chrome does not follow app theme.' }
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
                if ([HotshotSmokeNative]::ClientWidth($settings) / $dpi -lt 760) {
                    return $searchBounds.Width -ge $bounds.Width * 0.85 -and
                        ($searchBounds.Left - $bounds.Left) / $dpi -le 32
                }
                return ($searchBounds.Top - $bounds.Top) / $dpi -lt 48 -and
                    $searchBounds.Width -ge 150 * $dpi
            } "responsive search at $width DIPs" | Out-Null
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
        Write-Host "PASS full-width compact search at $($compactWidths | Sort-Object -Unique) DIPs and preserved query/focus"
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
