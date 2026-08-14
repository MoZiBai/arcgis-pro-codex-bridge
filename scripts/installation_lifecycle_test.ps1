param(
    [string]$ArcGISProInstallDir = '',
    [string]$Python = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) `
    ('arcgis-bridge-lifecycle-' + [guid]::NewGuid().ToString('N'))
$installRoot = Join-Path $testRoot 'install'
$codexHome = Join-Path $testRoot 'codex-home'
$dataRoot = Join-Path $testRoot 'data'
$outputRoot = Join-Path $testRoot 'output'
$queueRoot = Join-Path $testRoot 'queue'

try {
    $installParameters = @{
        InstallRoot = $installRoot
        CodexHome = $codexHome
        AllowedDataRoot = @($dataRoot)
        OutputRoot = $outputRoot
        QueueRoot = $queueRoot
        SkipAddin = $true
        Confirm = $false
    }
    if ($ArcGISProInstallDir) { $installParameters['ArcGISProInstallDir'] = $ArcGISProInstallDir }
    if ($Python) { $installParameters['Python'] = $Python }
    & (Join-Path $PSScriptRoot 'install.ps1') @installParameters
    if ($LASTEXITCODE -ne 0) { throw "Isolated install failed with exit code $LASTEXITCODE" }

    & (Join-Path $PSScriptRoot 'status.ps1') `
        -InstallRoot $installRoot `
        -CodexHome $codexHome `
        -Python $Python
    if ($LASTEXITCODE -ne 0) { throw "Isolated status failed with exit code $LASTEXITCODE" }

    & (Join-Path $PSScriptRoot 'uninstall.ps1') `
        -InstallRoot $installRoot `
        -CodexHome $codexHome `
        -Confirm:$false
    if ($LASTEXITCODE -ne 0) { throw "Isolated uninstall failed with exit code $LASTEXITCODE" }
    if (Test-Path -LiteralPath $installRoot) {
        throw 'Isolated uninstall left the installation root behind.'
    }

    Write-Output 'installation lifecycle ok: isolated install, global registration, probe, status, and uninstall'
}
finally {
    $resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\')
    $resolvedTest = [System.IO.Path]::GetFullPath($testRoot).TrimEnd('\')
    if ((Split-Path -Parent $resolvedTest).TrimEnd('\') -ne $resolvedTemp -or
        (Split-Path -Leaf $resolvedTest) -notlike 'arcgis-bridge-lifecycle-*') {
        throw "Refusing to clean unexpected lifecycle test path: $resolvedTest"
    }
    if (Test-Path -LiteralPath $resolvedTest) {
        Remove-Item -LiteralPath $resolvedTest -Recurse -Force
    }
}
