param([string]$ArcGISProInstallDir = '')

$releasePackage = Join-Path $PSScriptRoot '..\addin\ArcGISCodexBridgeAddin\bin\x64\Release\ArcGISCodexBridgeAddin.esriAddinX'
$package = if (Test-Path -LiteralPath $releasePackage) {
    Get-Item -LiteralPath $releasePackage
}
else {
    Get-ChildItem -Path (Join-Path $PSScriptRoot '..\addin\ArcGISCodexBridgeAddin\bin') -Filter '*.esriAddinX' -Recurse |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

if (-not $package) {
    throw 'Add-in package not found. Run scripts\build-addin.ps1 first.'
}

# Resolve the package ID from Config.daml, update that one exact installed
# package path, and verify it before invoking RegisterAddIn. RegisterAddIn can
# leave an older package in place (and terminate its caller early) while ArcGIS
# Pro currently has the add-in loaded.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $entry = $archive.GetEntry('Config.daml')
    if (-not $entry) {
        throw 'Config.daml was not found in the add-in package.'
    }
    $reader = [System.IO.StreamReader]::new($entry.Open())
    try {
        $configText = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
}
finally {
    $archive.Dispose()
}

$idMatch = [regex]::Match($configText, '<AddInInfo\s+id="([^"]+)"')
if (-not $idMatch.Success) {
    throw 'Could not determine the AddInInfo id from Config.daml.'
}
$addInId = $idMatch.Groups[1].Value
$installedDirectory = Join-Path ([Environment]::GetFolderPath('MyDocuments')) "ArcGIS\AddIns\ArcGISPro\$addInId"
$installedPackage = Join-Path $installedDirectory $package.Name
New-Item -ItemType Directory -Path $installedDirectory -Force | Out-Null

$sourceHash = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash
$installedHash = if (Test-Path -LiteralPath $installedPackage) {
    (Get-FileHash -LiteralPath $installedPackage -Algorithm SHA256).Hash
}
else {
    $null
}
if ($installedHash -ne $sourceHash) {
    if (Test-Path -LiteralPath $installedPackage) {
        Copy-Item -LiteralPath $installedPackage -Destination "$installedPackage.previous" -Force
    }
    Copy-Item -LiteralPath $package.FullName -Destination $installedPackage -Force
}

$verifiedHash = (Get-FileHash -LiteralPath $installedPackage -Algorithm SHA256).Hash
if ($verifiedHash -ne $sourceHash) {
    throw "Installed add-in package verification failed: $installedPackage"
}

Write-Output "Installed and verified: $installedPackage"

if ([string]::IsNullOrWhiteSpace($ArcGISProInstallDir)) {
    $candidates = @(
        $env:ARCGIS_PRO_INSTALL_DIR,
        (Join-Path $env:ProgramFiles 'ArcGIS\Pro'),
        (Join-Path ${env:ProgramFiles(x86)} 'ArcGIS\Pro')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $_ 'bin\RegisterAddIn.exe')) }
    $ArcGISProInstallDir = $candidates | Select-Object -First 1
}
if (-not $ArcGISProInstallDir) {
    throw 'ArcGIS Pro was not found. Pass -ArcGISProInstallDir or set ARCGIS_PRO_INSTALL_DIR.'
}
$register = Join-Path $ArcGISProInstallDir 'bin\RegisterAddIn.exe'
& $register $package.FullName /s
exit $LASTEXITCODE
