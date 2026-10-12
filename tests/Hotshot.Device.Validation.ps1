[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RecordingHarness,
    [Parameter(Mandatory)][string]$ProductHarness,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$Soak,
    [switch]$UiOnly,
    [switch]$RequireMixedDpi
)
$ErrorActionPreference = 'Stop'
$RecordingHarness = (Resolve-Path -LiteralPath $RecordingHarness).Path
$ProductHarness = (Resolve-Path -LiteralPath $ProductHarness).Path
if (-not [IO.Path]::IsPathFullyQualified($OutputDirectory)) { throw 'Use an absolute output directory for this validation run.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ($UiOnly -and $Soak) { throw 'UI-only acceptance cannot also request a recording soak.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$displays = @()
$mixedDpiPassed = $false
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
    $environmentJson = & $RecordingHarness '--display-environment'
    if ($LASTEXITCODE -ne 0) { throw 'Native display facts could not be read. No hardware acceptance was inferred.' }
    $displays = @($environmentJson | ConvertFrom-Json)
    $environmentJson | Set-Content -LiteralPath (Join-Path $OutputDirectory 'displays.json') -Encoding utf8
    Invoke-Check 'hdr-color-reference' $RecordingHarness @('--hdr-reference')
    $hasMixedDpi = $displays.Count -ge 2 -and @($displays.Scale | Sort-Object -Unique).Count -ge 2
    if ($RequireMixedDpi -and -not $hasMixedDpi) {
        $checks.Add([pscustomobject]@{ name = 'physical-mixed-dpi'; passed = $false; exitCode = 3; status = 'blocked'; reason = 'Two active different-scale displays are required.' })
        throw 'Physical mixed-DPI acceptance is blocked: the required displays are not connected.'
    }
    if (-not $UiOnly -or $RequireMixedDpi) {
        Invoke-Check 'unobscured-window' $RecordingHarness @('--window-screenshot')
        $mixedDpiPassed = $hasMixedDpi
    }
    if ($UiOnly) {
        Invoke-Check 'native-product-ui' $ProductHarness @('--output', (Join-Path $OutputDirectory 'product'), '--ui-acceptance')
    } else {
        Invoke-Check 'synthetic-recording' $RecordingHarness @('--window-only')
        if ($Soak) { Invoke-Check 'recording-soak' $RecordingHarness @('--soak-seconds','120') }
        Invoke-Check 'native-product' $ProductHarness @('--output', (Join-Path $OutputDirectory 'product'))
    }
}
finally {
    $report = [ordered]@{
        schemaVersion = 2
        collectedAt = [DateTimeOffset]::UtcNow.ToString('o')
        windowsBuild = [Environment]::OSVersion.Version.ToString()
        processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        harnessVersion = (Get-Item -LiteralPath $ProductHarness).VersionInfo.FileVersion
        checks = @($checks)
        displays = @($displays)
        physicalMixedDpi = @{ verified = $mixedDpiPassed; status = $(if ($mixedDpiPassed) { 'passed' } else { 'not_verified' }) }
        hdrDisplay = @{
            supported = $(if (@($displays | Where-Object { $_.HdrSupported -eq $true }).Count -gt 0) { $true }
                elseif ($displays.Count -eq 0 -or @($displays | Where-Object { $null -eq $_.HdrSupported }).Count -gt 0) { $null } else { $false })
            enabled = $(if (@($displays | Where-Object { $_.HdrEnabled -eq $true -and $_.ActiveColorMode -eq 'HDR' }).Count -gt 0) { $true }
                elseif ($displays.Count -eq 0 -or @($displays | Where-Object { $null -eq $_.HdrEnabled }).Count -gt 0) { $null } else { $false })
            fidelityVerified = $false
            status = 'requires_controlled_display_and_human_acceptance'
        }
        humanAccessibility = @{ verified = $false; status = 'requires_human_narrator_acceptance' }
        scope = 'Interactive Windows execution against generated content and fixture-owned windows; no microphone or full-desktop capture.'
        notVerified = @('physical ARM64 device acceptance beyond this native run','HDR display fidelity','human Narrator usability') +
            $(if (-not $mixedDpiPassed) { @('physical mixed-scale monitor movement') } else { @() })
    }
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'validation.json') -Encoding utf8
}
Write-Host 'PASS available-device checks. Hardware-specific manual checks are not implied by this result.'
