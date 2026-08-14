param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'ArcGISCodexBridge'),
    [string]$CodexHome = '',
    [string]$Python = ''
)

$ErrorActionPreference = 'Continue'
& (Join-Path $PSScriptRoot 'status.ps1') `
    -InstallRoot $InstallRoot `
    -CodexHome $CodexHome `
    -Python $Python `
    -WithArcPy
$statusCode = $LASTEXITCODE

if ($statusCode -ne 0) {
    Write-Output '[INFO] If ArcPy reports that a DLL contains a virus or potentially unwanted software, check Windows Security Protection History first.'
    Write-Output '[INFO] Prefer repairing ArcGIS Pro or allowing only a verified Esri-signed file; do not exclude the whole ArcGIS directory.'
}
exit $statusCode
