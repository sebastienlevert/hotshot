[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) { $Version = (Get-Content -LiteralPath (Join-Path $root 'version.txt') -Raw).Trim() }
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw 'Version must be a valid release version.' }
$platform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
$publish = Join-Path $root "artifacts\publish\$Version\$Runtime"
$releases = Join-Path $root "artifacts\releases\$Runtime"
$project = Join-Path $root 'src\Hotshot.App\Hotshot.App.csproj'

& dotnet publish $project -c Release -r $Runtime "-p:Platform=$platform" "-p:Version=$Version" `
    --self-contained true -o $publish --nologo -v:q
if ($LASTEXITCODE -ne 0) {
    throw "Publishing $Runtime failed (exit $LASTEXITCODE)."
}
if (-not (Test-Path (Join-Path $publish 'Hotshot.exe'))) {
    throw "Publish succeeded without producing Hotshot.exe."
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
