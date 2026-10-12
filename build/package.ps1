[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [bool]$IncludeCopilotRuntime = $true,

    [ValidateSet('Full', 'Core')]
    [string]$Edition = 'Full',

    [string]$OutputRoot,

    [string]$BuildArtifactsPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) { $Version = (Get-Content -LiteralPath (Join-Path $root 'version.txt') -Raw).Trim() }
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw 'Version must be a valid release version.' }
$platform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
$editionName = if ($PSBoundParameters.ContainsKey('Edition')) { $Edition } elseif ($IncludeCopilotRuntime) { 'Full' } else { 'Core' }
if ($PSBoundParameters.ContainsKey('Edition') -and $PSBoundParameters.ContainsKey('IncludeCopilotRuntime') -and
    $IncludeCopilotRuntime -ne ($editionName -eq 'Full')) { throw 'Edition and IncludeCopilotRuntime disagree.' }
$IncludeCopilotRuntime = $editionName -eq 'Full'
$channel = if ($IncludeCopilotRuntime) { $Runtime } else { "core-$Runtime" }
$artifacts = if ($OutputRoot) { [IO.Path]::GetFullPath($OutputRoot) } else { Join-Path $root 'artifacts' }
$publish = Join-Path $artifacts "publish\$Version\$channel"
$releases = Join-Path $artifacts "releases\$channel"
$buildArguments = if ($BuildArtifactsPath) { @('--artifacts-path', [IO.Path]::GetFullPath($BuildArtifactsPath)) } else { @() }
$project = Join-Path $root 'src\Hotshot.App\Hotshot.App.csproj'
$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
$sdkVersion = $projectXml.SelectSingleNode('//PackageReference[@Include="GitHub.Copilot.SDK"]').Version
if ($sdkVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'The Copilot SDK must have an explicit pinned version.' }

# Never reuse a publish tree: a skipped SDK download must not retain an earlier Full runtime.
if (Test-Path -LiteralPath $publish) {
    if ((Get-Item -LiteralPath $publish).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Publish path is a link.' }
    Remove-Item -LiteralPath $publish -Recurse -Force
}

& dotnet publish $project -c Release -r $Runtime "-p:Platform=$platform" "-p:Version=$Version" `
    "-p:CopilotSkipCliDownload=$(-not $IncludeCopilotRuntime)" --self-contained true -o $publish --nologo -v:q @buildArguments
if ($LASTEXITCODE -ne 0) {
    throw "Publishing $Runtime failed (exit $LASTEXITCODE)."
}
if (-not (Test-Path (Join-Path $publish 'Hotshot.exe'))) {
    throw "Publish succeeded without producing Hotshot.exe."
}

$mcp = Join-Path $publish 'Mcp'
& dotnet publish (Join-Path $root 'src\Hotshot.Mcp\Hotshot.Mcp.csproj') -c Release -r $Runtime "-p:Version=$Version" `
    --self-contained true -o $mcp --nologo -v:q @buildArguments
if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $mcp 'Hotshot.Mcp.exe'))) {
    throw "Publishing the $Runtime MCP companion failed."
}
Copy-Item -LiteralPath (Join-Path $root 'agents') -Destination $publish -Recurse -Force
$native = Join-Path $publish "runtimes\$Runtime\native"
$inProcess = Join-Path $native 'copilot_runtime.dll'
$stdio = Join-Path $native 'runtime.node'
if (-not $IncludeCopilotRuntime -and (Test-Path -LiteralPath $native -PathType Container)) {
    $resolved = (Resolve-Path -LiteralPath $native).Path
    if ($resolved -ne $native) { throw 'Unexpected optional runtime directory resolution.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
if (Test-Path -LiteralPath $inProcess) {
    if (-not (Test-Path -LiteralPath $stdio) -or
        (Get-FileHash -LiteralPath $inProcess).Hash -ne (Get-FileHash -LiteralPath $stdio).Hash) {
        throw 'The optional in-process Copilot alias differs from the verified stdio runtime; no binary was removed.'
    }
    # The app explicitly uses ForStdio; its in-process-only byte-identical alias is not needed.
    Remove-Item -LiteralPath $inProcess
}
if ($IncludeCopilotRuntime) {
    foreach ($file in 'copilot-runtime.exe', 'runtime.node') {
        if (-not (Test-Path -LiteralPath (Join-Path $native $file) -PathType Leaf)) { throw "Full runtime is missing $file." }
        $stream = [IO.File]::OpenRead((Join-Path $native $file))
        $reader = [IO.BinaryReader]::new($stream)
        try {
            if ($reader.ReadUInt16() -ne 0x5A4D) { throw "$file is not a PE binary." }
            $stream.Position = 0x3C
            $stream.Position = $reader.ReadUInt32()
            if ($reader.ReadUInt32() -ne 0x00004550) { throw "$file has an invalid PE header." }
            $machine = $reader.ReadUInt16()
            $expectedMachine = if ($Runtime -eq 'win-arm64') { 0xAA64 } else { 0x8664 }
            if ($machine -ne $expectedMachine) { throw "$file does not match $Runtime." }
        } finally { $reader.Dispose() }
    }
}
@{
    schemaVersion = 1
    edition = $editionName.ToLowerInvariant()
    runtime = $Runtime
    appVersion = $Version
    copilotSdkVersion = $sdkVersion
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publish 'Hotshot.Distribution.json') -Encoding utf8NoBOM

Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $publish -Force
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $publish -Force
Push-Location $root
try {
    & dotnet tool run vpk -- pack --packId Hotshot --packVersion $Version --packDir $publish `
        --packTitle $(if ($IncludeCopilotRuntime) { 'Hotshot' } else { 'Hotshot Core' }) --packAuthors 'Sebastien Levert' --mainExe Hotshot.exe `
        --icon (Join-Path $root 'src\Hotshot.App\Assets\Hotshot.ico') `
        --runtime $Runtime --channel $channel --outputDir $releases `
        --releaseNotes (Join-Path $root 'CHANGELOG.md')
    if ($LASTEXITCODE -ne 0) { throw "Velopack packaging $Runtime failed (exit $LASTEXITCODE)." }
} finally { Pop-Location }
if (-not $IncludeCopilotRuntime) {
    $assetsPath = Join-Path $releases "assets.$channel.json"
    $assets = @(Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json)
    foreach ($kind in 'Setup.exe', 'Portable.zip') {
        $sourceName = "Hotshot-$channel-$kind"
        $targetName = "Hotshot-Core-$Runtime-$kind"
        Move-Item -LiteralPath (Join-Path $releases $sourceName) -Destination (Join-Path $releases $targetName) -Force
        foreach ($asset in $assets) {
            if ($asset.RelativeFileName -eq $sourceName) { $asset.RelativeFileName = $targetName }
        }
    }
    ConvertTo-Json -InputObject $assets -Depth 10 -Compress | Set-Content -LiteralPath $assetsPath -Encoding utf8NoBOM
}
if ($IncludeCopilotRuntime) {
    $componentName = "Hotshot-Copilot-$Version-$Runtime.zip"
    $componentPath = Join-Path $releases $componentName
    if (Test-Path -LiteralPath $componentPath) { Remove-Item -LiteralPath $componentPath }
    $componentEntries = @(Get-ChildItem -LiteralPath $native -Recurse -Force)
    if ($componentEntries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
        throw 'Component source contains a link.'
    }
    $componentFiles = @($componentEntries | Where-Object { -not $_.PSIsContainer })
    if ($componentFiles.Count -lt 2 -or $componentFiles.Count -gt 256) { throw 'Unexpected component file count.' }
    $files = @($componentFiles | Sort-Object FullName | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Component source contains a link.' }
        @{
            path = [IO.Path]::GetRelativePath($native, $_.FullName).Replace('\', '/')
            bytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
    $unpackedBytes = ($componentFiles | Measure-Object Length -Sum).Sum
    if ($unpackedBytes -gt 512MB) { throw 'Component exceeds its unpacked limit.' }
    $zip = [IO.Compression.ZipFile]::Open($componentPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in ($componentFiles | Sort-Object FullName)) {
            $relative = [IO.Path]::GetRelativePath($native, $file.FullName).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative,
                [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose() }
    $archiveBytes = (Get-Item -LiteralPath $componentPath).Length
    if ($archiveBytes -gt 256MB) { throw 'Component exceeds its archive limit.' }
    @{
        schemaVersion = 1
        appVersion = $Version
        runtime = $Runtime
        copilotSdkVersion = $sdkVersion
        fileName = $componentName
        archiveBytes = $archiveBytes
        unpackedBytes = [long]$unpackedBytes
        sha256 = (Get-FileHash -LiteralPath $componentPath -Algorithm SHA256).Hash
        files = $files
    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $releases "Hotshot-Copilot-$Version-$Runtime.json") -Encoding utf8NoBOM
}
Write-Host "Installer, managed portable bundle and update feed: $releases"
