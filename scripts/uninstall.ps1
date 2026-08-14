[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$InstallRoot = '',
    [string]$InstallLocatorPath = '',
    [string]$CodexHome = '',
    [switch]$RemoveAddin
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ArcGISBridge.Installation.psm1') -Force

$InstallRoot = Resolve-ArcGISBridgeInstallRoot `
    -InstallRoot $InstallRoot `
    -InstallLocatorPath $InstallLocatorPath
$InstallRoot = Assert-ArcGISBridgeSafeInstallRoot -InstallRoot $InstallRoot
$CodexHome = Resolve-ArcGISBridgeCodexHome -CodexHome $CodexHome
$manifestPath = Join-Path $InstallRoot 'install.json'
$manifest = if (Test-Path -LiteralPath $manifestPath) {
    Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
}
else {
    $null
}

$addinRoot = ''
$resolvedPackage = ''
$previousPackage = ''
$removePreviousPackage = $false
if ($RemoveAddin) {
    if (-not $manifest -or -not $manifest.addin_id) {
        throw 'The installation manifest is required to identify the exact Add-in directory.'
    }
    $addinRoot = if ($manifest.PSObject.Properties.Name -contains 'addin_directory' -and $manifest.addin_directory) {
        [System.IO.Path]::GetFullPath([string]$manifest.addin_directory).TrimEnd('\')
    }
    else {
        [System.IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('MyDocuments')) `
            ("ArcGIS\AddIns\ArcGISPro\" + [string]$manifest.addin_id))).TrimEnd('\')
    }
    $addinPackage = if ($manifest.PSObject.Properties.Name -contains 'addin_package' -and $manifest.addin_package) {
        [System.IO.Path]::GetFullPath([string]$manifest.addin_package)
    }
    else {
        Get-ChildItem -LiteralPath $addinRoot -Filter '*.esriAddInX' -File -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty FullName
    }
    if ($addinPackage) {
        $resolvedPackage = [System.IO.Path]::GetFullPath($addinPackage)
        if (-not (Test-ArcGISBridgePathWithinDirectory `
            -Directory $addinRoot `
            -Path $resolvedPackage)) {
            throw "Refusing to remove Add-in package outside its recorded directory: $resolvedPackage"
        }
        if (Test-Path -LiteralPath $resolvedPackage -PathType Leaf) {
            $addinInfo = Get-ArcGISBridgeAddinPackageInfo -Package $resolvedPackage
            if ($addinInfo.id -ne [string]$manifest.addin_id) {
                throw "Refusing to remove Add-in with unexpected ID $($addinInfo.id): $resolvedPackage"
            }
            if (($manifest.PSObject.Properties.Name -contains 'addin_sha256') -and
                $manifest.addin_sha256 -and $addinInfo.sha256 -ne [string]$manifest.addin_sha256) {
                throw "Refusing to remove an Add-in changed since installation: $resolvedPackage"
            }
            $previousPackage = "$resolvedPackage.previous"
            if (Test-Path -LiteralPath $previousPackage -PathType Leaf) {
                try {
                    $previousInfo = Get-ArcGISBridgeAddinPackageInfo `
                        -Package $previousPackage `
                        -AllowBackupPackage
                    $removePreviousPackage = $previousInfo.id -eq [string]$manifest.addin_id
                }
                catch {
                    Write-Output "[INFO] Will leave an unrecognized Add-in backup intact: $previousPackage"
                }
            }
        }
    }
}

$registrationExists = Test-ArcGISBridgeGlobalMcpRegistration -CodexHome $CodexHome
$registrationMatches = $manifest -and (Test-ArcGISBridgeGlobalMcpRegistration `
    -CodexHome $CodexHome `
    -Command ([string]$manifest.python) `
    -Server ([string]$manifest.server) `
    -Config ([string]$manifest.config) `
    -WorkingDirectory $InstallRoot)
if ($registrationMatches) {
    if ($PSCmdlet.ShouldProcess(
        (Join-Path $CodexHome 'config.toml'),
        'Remove matching global arcgis-pro MCP registration'
    )) {
        $removed = Remove-ArcGISBridgeGlobalMcpRegistration `
            -CodexHome $CodexHome `
            -Command ([string]$manifest.python) `
            -Server ([string]$manifest.server) `
            -Config ([string]$manifest.config) `
            -WorkingDirectory $InstallRoot
        if ($removed) { Write-Output '[PASS] Removed matching global Codex MCP registration: arcgis-pro' }
    }
}
elseif ($registrationExists) {
    Write-Output '[INFO] Left the global arcgis-pro registration intact because it belongs to a different installation.'
}
else {
    Write-Output '[INFO] No global arcgis-pro MCP registration was present.'
}

if ($RemoveAddin) {
    if ($resolvedPackage -and
        (Test-Path -LiteralPath $resolvedPackage -PathType Leaf) -and
        $PSCmdlet.ShouldProcess($resolvedPackage, 'Remove exact ArcGIS Pro Add-in package')) {
        Remove-Item -LiteralPath $resolvedPackage -Force
        if ($removePreviousPackage) {
            Remove-Item -LiteralPath $previousPackage -Force
        }
        Write-Output "[PASS] Removed exact ArcGIS Pro Add-in package: $resolvedPackage"
    }
    if ((Test-Path -LiteralPath $addinRoot -PathType Container) -and
        -not (Get-ChildItem -LiteralPath $addinRoot -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $addinRoot -Force
    }
}

if ((Test-Path -LiteralPath $InstallRoot) -and $PSCmdlet.ShouldProcess($InstallRoot, 'Remove installed bridge runtime and private config')) {
    Remove-Item -LiteralPath $InstallRoot -Recurse -Force
    Write-Output "[PASS] Removed bridge installation: $InstallRoot"
}
if ($PSCmdlet.ShouldProcess(
    (Get-ArcGISBridgeInstallLocatorPath -InstallLocatorPath $InstallLocatorPath),
    'Remove matching bridge install locator'
)) {
    if (Remove-ArcGISBridgeInstallLocator `
        -InstallRoot $InstallRoot `
        -InstallLocatorPath $InstallLocatorPath) {
        Write-Output '[PASS] Removed matching Add-in install locator.'
    }
}

Write-Output 'Restart Codex after uninstalling. Restart ArcGIS Pro if the Add-in was removed.'
