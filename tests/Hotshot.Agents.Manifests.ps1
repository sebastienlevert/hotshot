[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = (Get-Content -LiteralPath (Join-Path $root 'version.txt') -Raw).Trim()
$pluginRoot = Join-Path $root 'agents\hotshot'
function Read-Json([string]$path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable }
function Assert-True([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }

$plugin = Read-Json (Join-Path $pluginRoot 'plugin.json')
Assert-True ($plugin.'$schema' -eq 'https://agent-plugins.org/schemas/1.0.0/plugin.schema.json') 'Invalid Agent Plugins schema.'
Assert-True ($plugin.name -eq 'hotshot' -and $plugin.version -eq $version) 'Plugin/app version mismatch.'
$allowed = '$schema', 'name', 'version', 'description', 'author', 'homepage', 'repository', 'license', 'keywords', 'extensions'
foreach ($key in $plugin.Keys) { Assert-True ($key -in $allowed) "Unknown portable plugin field: $key" }
$claude = Read-Json (Join-Path $pluginRoot '.claude-plugin\plugin.json')
Assert-True ($claude.name -eq 'hotshot' -and $claude.version -eq $version) 'Claude plugin/app version mismatch.'

$mcp = Read-Json (Join-Path $pluginRoot 'mcp.json')
Assert-True ($mcp.'$schema' -eq 'https://agent-plugins.org/schemas/1.0.0/mcp.schema.json') 'Invalid MCP schema.'
$server = $mcp.mcpServers.hotshot
Assert-True ($server.type -eq 'stdio' -and $server.command -eq 'powershell.exe') 'Plugin must launch the local stdio bridge.'
Assert-True ($server.args[-1] -eq '${PLUGIN_ROOT}\scripts\start-mcp.ps1') 'Portable launcher path differs.'
$legacy = Read-Json (Join-Path $pluginRoot '.mcp.json')
Assert-True ($legacy.mcpServers.hotshot.args[-1] -eq '${CLAUDE_PLUGIN_ROOT}\scripts\start-mcp.ps1') 'Claude launcher path differs.'
Assert-True (Test-Path -LiteralPath (Join-Path $pluginRoot 'scripts\start-mcp.ps1')) 'Missing launcher.'

foreach ($path in '.github\plugin\marketplace.json', '.claude-plugin\marketplace.json') {
    $marketplace = Read-Json (Join-Path $root $path)
    Assert-True ($marketplace.name -eq 'hotshot' -and $marketplace.plugins.Count -eq 1) 'Invalid marketplace inventory.'
    $entry = $marketplace.plugins[0]
    Assert-True ($entry.name -eq $plugin.name -and $entry.source -eq './agents/hotshot') 'Marketplace source/name differs.'
}
foreach ($name in 'capture', 'verify-ui', 'search-captures') {
    $skill = Get-Content -LiteralPath (Join-Path $pluginRoot "skills\$name\SKILL.md") -Raw
    Assert-True ($skill.StartsWith("---`n") -or $skill.StartsWith("---`r`n")) "Missing skill frontmatter: $name"
    Assert-True ($skill -match "(?m)^name: $name\r?$" -and $skill -match '(?m)^description: .+') "Invalid skill metadata: $name"
}
Write-Host 'PASS plugin versions, marketplace sources, stdio launchers and portable skills.'
