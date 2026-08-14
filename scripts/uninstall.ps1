[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'ArcGISCodexBridge'),
    [string]$CodexHome = '',
    [switch]$RemoveAddin
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ArcGISBridge.Installation.psm1') -Force

$InstallRoot = Assert-ArcGISBridgeSafeInstallRoot -InstallRoot $InstallRoot
$CodexHome = Resolve-ArcGISBridgeCodexHome -CodexHome $CodexHome
$manifestPath = Join-Path $InstallRoot 'install.json'
$manifest = if (Test-Path -LiteralPath $manifestPath) {
    Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
}
else {
    $null
}

if ($PSCmdlet.ShouldProcess((Join-Path $CodexHome 'config.toml'), 'Remove global arcgis-pro MCP registration')) {
    $removed = Remove-ArcGISBridgeGlobalMcpRegistration -CodexHome $CodexHome
    if ($removed) {
        Write-Output '[PASS] Removed global Codex MCP registration: arcgis-pro'
    }
    else {
        Write-Output '[INFO] No global arcgis-pro MCP registration was present.'
    }
}

if ($RemoveAddin) {
    if (-not $manifest -or -not $manifest.addin_id) {
        throw 'The installation manifest is required to identify the exact Add-in directory.'
    }
    $addinRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) `
        ("ArcGIS\AddIns\ArcGISPro\" + [string]$manifest.addin_id)
    $expectedParent = [System.IO.Path]::GetFullPath(
        (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'ArcGIS\AddIns\ArcGISPro')
    ).TrimEnd('\')
    $resolvedAddin = [System.IO.Path]::GetFullPath($addinRoot).TrimEnd('\')
    if ((Split-Path -Parent $resolvedAddin).TrimEnd('\') -ne $expectedParent) {
        throw "Refusing to remove unexpected Add-in path: $resolvedAddin"
    }
    if ((Test-Path -LiteralPath $resolvedAddin) -and $PSCmdlet.ShouldProcess($resolvedAddin, 'Remove ArcGIS Pro Add-in')) {
        Remove-Item -LiteralPath $resolvedAddin -Recurse -Force
        Write-Output "[PASS] Removed ArcGIS Pro Add-in: $resolvedAddin"
    }
}

if ((Test-Path -LiteralPath $InstallRoot) -and $PSCmdlet.ShouldProcess($InstallRoot, 'Remove installed bridge runtime and private config')) {
    Remove-Item -LiteralPath $InstallRoot -Recurse -Force
    Write-Output "[PASS] Removed bridge installation: $InstallRoot"
}

Write-Output 'Restart Codex after uninstalling. Restart ArcGIS Pro if the Add-in was removed.'
