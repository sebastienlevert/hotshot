[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RecordingHarness,
    [Parameter(Mandatory)][string]$ProductHarness,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$Soak
)
$ErrorActionPreference = 'Stop'
$RecordingHarness = (Resolve-Path -LiteralPath $RecordingHarness).Path
$ProductHarness = (Resolve-Path -LiteralPath $ProductHarness).Path
if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory)) { throw 'Use an absolute output directory for this validation run.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$checks = [Collections.Generic.List[object]]::new()
function Invoke-Check([string]$Name, [string]$Executable, [string[]]$Arguments) {
    $log = Join-Path $OutputDirectory "$Name.log"
    & $Executable @Arguments *> $log
    $code = $LASTEXITCODE
    $checks.Add([pscustomobject]@{ name = $Name; passed = $code -eq 0; exitCode = $code })
    if ($code -ne 0) {
        Get-Content -LiteralPath $log -Tail 30
        throw "$Name failed; its evidence log was retained."
    }
}
try {
    Invoke-Check 'unobscured-window' $RecordingHarness @('--window-screenshot')
    Invoke-Check 'synthetic-recording' $RecordingHarness @('--window-only')
    if ($Soak) { Invoke-Check 'recording-soak' $RecordingHarness @('--soak-seconds','120') }
    Invoke-Check 'native-product' $ProductHarness @('--output', (Join-Path $OutputDirectory 'product'))
}
finally {
    $report = [ordered]@{
        schemaVersion = 1
        collectedAt = [DateTimeOffset]::UtcNow.ToString('o')
        windowsBuild = [Environment]::OSVersion.Version.ToString()
        processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        checks = @($checks)
        scope = 'Interactive Windows execution against generated content and fixture-owned windows; no microphone or full-desktop capture.'
        notVerified = @('physical ARM64 unless this run is on that device','mixed-scale monitor movement','HDR display fidelity','Narrator usability')
    }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'validation.json') -Encoding utf8
}
Write-Host 'PASS available-device checks. Hardware-specific manual checks are not implied by this result.'
