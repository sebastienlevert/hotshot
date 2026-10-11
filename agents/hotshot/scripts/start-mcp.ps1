[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$executable = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Hotshot\current\Mcp\Hotshot.Mcp.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    [Console]::Error.WriteLine('Hotshot MCP is not installed. Install the latest Hotshot Windows release, start it, and open Settings > Coding agents.')
    exit 1
}

& $executable
exit $LASTEXITCODE
