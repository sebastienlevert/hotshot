[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [bool]$IncludeCopilotRuntime = $true,

    [string]$OutputRoot,

    [string]$BuildArtifactsPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) { $Version = (Get-Content -LiteralPath (Join-Path $root 'version.txt') -Raw).Trim() }
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw 'Version must be a valid release version.' }
$platform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
$artifacts = if ($OutputRoot) { [IO.Path]::GetFullPath($OutputRoot) } else { Join-Path $root 'artifacts' }
$publish = Join-Path $artifacts "publish\$Version\$Runtime"
$releases = Join-Path $artifacts "releases\$Runtime"
$buildArguments = if ($BuildArtifactsPath) { @('--artifacts-path', [IO.Path]::GetFullPath($BuildArtifactsPath)) } else { @() }
$project = Join-Path $root 'src\Hotshot.App\Hotshot.App.csproj'

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

Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $publish -Force
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $publish -Force
Push-Location $root
try {
    & dotnet tool run vpk -- pack --packId Hotshot --packVersion $Version --packDir $publish `
        --packTitle Hotshot --packAuthors 'Sebastien Levert' --mainExe Hotshot.exe `
        --icon (Join-Path $root 'src\Hotshot.App\Assets\Hotshot.ico') `
        --runtime $Runtime --channel $Runtime --outputDir $releases `
        --releaseNotes (Join-Path $root 'CHANGELOG.md')
    if ($LASTEXITCODE -ne 0) { throw "Velopack packaging $Runtime failed (exit $LASTEXITCODE)." }
} finally { Pop-Location }
Write-Host "Installer, managed portable bundle and update feed: $releases"
