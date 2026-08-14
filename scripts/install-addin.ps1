param(
    [string]$ArcGISProInstallDir = '',
    [string]$Package = '',
    [string]$ExpectedVersion = '',
    [string]$InstallDirectory = '',
    [switch]$SkipRegistration
)

$releasePackage = Join-Path $PSScriptRoot '..\addin\ArcGISCodexBridgeAddin\bin\x64\Release\ArcGISCodexBridgeAddin.esriAddinX'
$packageItem = if (-not [string]::IsNullOrWhiteSpace($Package)) {
    $resolvedPackage = [System.IO.Path]::GetFullPath($Package)
    if (-not (Test-Path -LiteralPath $resolvedPackage -PathType Leaf)) {
        throw "Add-in package was not found: $resolvedPackage"
    }
    Get-Item -LiteralPath $resolvedPackage
}
elseif (Test-Path -LiteralPath $releasePackage) {
    Get-Item -LiteralPath $releasePackage
}
else {
    Get-ChildItem -Path (Join-Path $PSScriptRoot '..\addin\ArcGISCodexBridgeAddin\bin') -Filter '*.esriAddinX' -Recurse |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

if (-not $packageItem) {
    throw 'Add-in package not found. Run scripts\build-addin.ps1 first.'
}
if ($packageItem.Extension -ine '.esriAddinX') {
    throw "Expected an .esriAddinX package: $($packageItem.FullName)"
}

# Resolve the package ID from Config.daml, update that one exact installed
# package path, and verify it before invoking RegisterAddIn. RegisterAddIn can
# leave an older package in place (and terminate its caller early) while ArcGIS
# Pro currently has the add-in loaded.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($packageItem.FullName)
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
$versionMatch = [regex]::Match($configText, '<AddInInfo\s+[^>]*version="([^"]+)"')
if (-not $versionMatch.Success) {
    throw 'Could not determine the AddInInfo version from Config.daml.'
}
$packageVersion = $versionMatch.Groups[1].Value
if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and $packageVersion -ne $ExpectedVersion) {
    throw "Add-in package version $packageVersion does not match MCP version $ExpectedVersion."
}
$installedDirectory = if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    Join-Path ([Environment]::GetFolderPath('MyDocuments')) "ArcGIS\AddIns\ArcGISPro\$addInId"
}
else {
    [System.IO.Path]::GetFullPath($InstallDirectory)
}
$installedPackage = Join-Path $installedDirectory $packageItem.Name
New-Item -ItemType Directory -Path $installedDirectory -Force | Out-Null

$sourceHash = (Get-FileHash -LiteralPath $packageItem.FullName -Algorithm SHA256).Hash
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
    Copy-Item -LiteralPath $packageItem.FullName -Destination $installedPackage -Force
}

$verifiedHash = (Get-FileHash -LiteralPath $installedPackage -Algorithm SHA256).Hash
if ($verifiedHash -ne $sourceHash) {
    throw "Installed add-in package verification failed: $installedPackage"
}

Write-Output "Installed and verified: $installedPackage"

if ($SkipRegistration) {
    Write-Output "Skipped RegisterAddIn by request; package version $packageVersion was hash-verified."
    return
}

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
& $register $packageItem.FullName /s
$registrationExitCode = $LASTEXITCODE
if ($null -ne $registrationExitCode -and [int]$registrationExitCode -ne 0) {
    throw "RegisterAddIn failed with exit code $registrationExitCode"
}
if ($null -eq $registrationExitCode) {
    Write-Output 'RegisterAddIn returned no exit code; the hash-verified package will load after ArcGIS Pro restarts.'
}
else {
    Write-Output "Registered Add-in package: $($packageItem.FullName)"
}
