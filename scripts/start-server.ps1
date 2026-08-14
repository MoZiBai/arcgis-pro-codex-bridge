param(
    [string]$Config = (Join-Path $PSScriptRoot "..\config.json")
)

$server = Join-Path $PSScriptRoot "..\src\arcgis_mcp\server.py"
python $server --config $Config
