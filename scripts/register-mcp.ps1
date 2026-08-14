param(
    [string]$Python = 'python',
    [string]$Config = (Join-Path $PSScriptRoot '..\config.json')
)

$server = Join-Path $PSScriptRoot '..\src\arcgis_mcp\server.py'
codex mcp add arcgis-pro -- $Python $server --config $Config
