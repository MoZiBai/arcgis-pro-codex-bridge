param(
    [string]$Python = 'python',
    [string]$Config = (Join-Path $PSScriptRoot '..\config.json'),
    [string]$CodexHome = ''
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ArcGISBridge.Installation.psm1') -Force

$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$server = Join-Path $root 'src\arcgis_mcp\server.py'
$resolvedPython = Resolve-ArcGISBridgePython -Python $Python
$resolvedConfig = [System.IO.Path]::GetFullPath($Config)
if (-not (Test-Path -LiteralPath $resolvedConfig)) {
    throw "Config file not found: $resolvedConfig"
}
$resolvedCodexHome = Resolve-ArcGISBridgeCodexHome -CodexHome $CodexHome
Set-ArcGISBridgeGlobalMcpRegistration `
    -CodexHome $resolvedCodexHome `
    -Command $resolvedPython `
    -Server $server `
    -Config $resolvedConfig `
    -WorkingDirectory $root | Out-Null

Write-Output "Registered global Codex MCP 'arcgis-pro' in $resolvedCodexHome"
Write-Output 'Restart Codex to load the updated MCP configuration.'
