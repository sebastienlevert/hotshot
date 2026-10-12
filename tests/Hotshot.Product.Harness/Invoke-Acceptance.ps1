[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$ExpectedArchitecture,
    [ValidateRange(30, 300)][int]$TimeoutSeconds = 180,
    [switch]$SoftwareRenderer
)

$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $output | Out-Null
$evidence = [ordered]@{
    schemaVersion = 1
    mode = 'native-ui-process-supervision'
    outcome = 'failed'
    expectedArchitecture = $ExpectedArchitecture
    runnerProcessArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    runnerOsArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    timeoutSeconds = $TimeoutSeconds
    exitCode = $null
    timedOut = $false
    failure = $null
}
$process = $null
try {
    foreach ($reportName in 'ui-acceptance.json', 'accessibility-evidence.json', 'process-evidence.json') {
        $priorReport = Join-Path $output $reportName
        if (Test-Path -LiteralPath $priorReport) { Remove-Item -LiteralPath $priorReport -Force }
    }
    if ([IO.Path]::GetFileName($Executable) -ne 'Hotshot.Product.Harness.exe') {
        throw 'Only Hotshot.Product.Harness.exe may be launched. Never use the production Hotshot.exe for isolated acceptance.'
    }
    if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
        throw "Native Product harness executable not found: $Executable"
    }
    $arguments = @('--ui-acceptance', '--output', "`"$output`"", '--expected-architecture', $ExpectedArchitecture,
        '--timeout-seconds', $TimeoutSeconds)
    if ($SoftwareRenderer) { $arguments += '--software-renderer' }
    $process = Start-Process -FilePath ([IO.Path]::GetFullPath($Executable)) -ArgumentList $arguments `
        -WorkingDirectory ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Executable))) `
        -PassThru -RedirectStandardOutput "$output\stdout.log" -RedirectStandardError "$output\stderr.log"
    if (-not $process.WaitForExit(($TimeoutSeconds + 15) * 1000)) {
        $evidence.timedOut = $true
        $process.Kill($true)
        $process.WaitForExit()
        throw "Native WinUI acceptance exceeded the $TimeoutSeconds-second bound (including startup/cleanup)."
    }
    $process.Refresh()
    $evidence.exitCode = $process.ExitCode
    if ($process.ExitCode -ne 0) {
        throw "Native WinUI acceptance failed with exit code $($process.ExitCode). Inspect stderr.log and ui-acceptance.json for exact prerequisites/failure."
    }
    $report = Get-Content -LiteralPath "$output\ui-acceptance.json" -Raw | ConvertFrom-Json
    if ($report.outcome -ne 'passed' -or $report.processArchitecture -ne $ExpectedArchitecture -or
        $report.osArchitecture -ne $ExpectedArchitecture -or -not $report.nativeArchitecture -or
        $report.checks.Count -lt 8 -or @($report.checks | Where-Object outcome -ne 'passed').Count -ne 0) {
        throw 'Native UI report is incomplete, failed, skipped, or has an unexpected/emulated architecture.'
    }
    $evidence.outcome = 'passed'
    Get-Content -LiteralPath "$output\stdout.log"
}
catch {
    $evidence.failure = $_.Exception.ToString()
    if (Test-Path -LiteralPath "$output\stdout.log") { Get-Content -LiteralPath "$output\stdout.log" }
    if (Test-Path -LiteralPath "$output\stderr.log") { Get-Content -LiteralPath "$output\stderr.log" }
    throw
}
finally {
    $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$output\process-evidence.json" -Encoding utf8
    if ($null -ne $process) { $process.Dispose() }
}
