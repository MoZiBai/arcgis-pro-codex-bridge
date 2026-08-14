Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ArcGISBridge.Installation.psm1') -Force

$root = Join-Path ([System.IO.Path]::GetTempPath()) `
    ('arcgis-bridge-install-contract-' + [guid]::NewGuid().ToString('N'))
$codexHome = Join-Path $root '.codex'
$configPath = Join-Path $codexHome 'config.toml'
New-Item -ItemType Directory -Path $codexHome -Force | Out-Null

try {
    $initial = @'
model = "test-model"

[model_providers.custom]
experimental_bearer_token = "must-preserve"

[mcp_servers.fastctx]
command = "fastctx"
args = ["serve"]
'@
    [System.IO.File]::WriteAllText(
        $configPath,
        $initial,
        [System.Text.UTF8Encoding]::new($false)
    )

    $parameters = @{
        CodexHome = $codexHome
        Command = 'C:\Tools\Python\python.exe'
        Server = 'C:\Tools\ArcGISBridge\runtime\server.py'
        Config = 'C:\Tools\ArcGISBridge\config\config.json'
        WorkingDirectory = 'C:\Tools\ArcGISBridge'
    }
    Set-ArcGISBridgeGlobalMcpRegistration @parameters | Out-Null
    Set-ArcGISBridgeGlobalMcpRegistration @parameters | Out-Null

    $applied = [System.IO.File]::ReadAllText($configPath)
    if ($applied -notmatch 'experimental_bearer_token\s*=\s*"must-preserve"') {
        throw 'Global registration did not preserve an unrelated credential field.'
    }
    if ($applied -notmatch '(?m)^\[mcp_servers\.fastctx\]$') {
        throw 'Global registration did not preserve another MCP server.'
    }
    $arcgisHeaders = [regex]::Matches(
        $applied,
        '(?m)^\[mcp_servers\.(?:arcgis-pro|"arcgis-pro")\]$'
    ).Count
    if ($arcgisHeaders -ne 1) {
        throw "Global registration must be idempotent; found $arcgisHeaders arcgis-pro tables."
    }
    if (-not (Test-ArcGISBridgeGlobalMcpRegistration -CodexHome $codexHome)) {
        throw 'Global registration was not detected after apply.'
    }

    if (-not (Remove-ArcGISBridgeGlobalMcpRegistration -CodexHome $codexHome)) {
        throw 'Global registration removal reported no change.'
    }
    $removed = [System.IO.File]::ReadAllText($configPath)
    if ($removed -match '(?m)^\[mcp_servers\.(?:arcgis-pro|"arcgis-pro")\]$') {
        throw 'Global registration removal left the arcgis-pro table behind.'
    }
    if ($removed -notmatch 'experimental_bearer_token\s*=\s*"must-preserve"' -or
        $removed -notmatch '(?m)^\[mcp_servers\.fastctx\]$') {
        throw 'Global registration removal damaged unrelated configuration.'
    }

    $addinInstaller = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'install-addin.ps1') -Raw
    if ($addinInstaller -notmatch '\$null\s+-ne\s+\$registrationExitCode' -or
        $addinInstaller -notmatch 'hash-verified package will load after ArcGIS Pro restarts') {
        throw 'Add-in installer must tolerate RegisterAddIn returning no exit code while Pro is open.'
    }

    Write-Output 'installation contract ok: global apply/remove is idempotent, unrelated config is preserved, and open-Pro registration is restart-safe'
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
