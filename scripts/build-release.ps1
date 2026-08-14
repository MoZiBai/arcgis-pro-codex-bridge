[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$OutputDirectory = '',
    [string]$ArcGISProInstallDir = '',
    [string]$MSBuild = '',
    [string]$AddinPackage = '',
    [switch]$SkipBuild,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $sourceRoot 'dist'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

$versionMatch = Select-String -LiteralPath (Join-Path $sourceRoot 'src\arcgis_mcp\server.py') `
    -Pattern '^VERSION\s*=\s*"([^"]+)"$' | Select-Object -First 1
if (-not $versionMatch) {
    throw 'Could not determine the MCP bridge version from server.py.'
}
$sourceVersion = $versionMatch.Matches[0].Groups[1].Value
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $sourceVersion
}
elseif ($Version -ne $sourceVersion) {
    throw "Requested release version $Version does not match source version $sourceVersion."
}

if ([string]::IsNullOrWhiteSpace($AddinPackage)) {
    $AddinPackage = Join-Path $sourceRoot `
        'addin\ArcGISCodexBridgeAddin\bin\x64\Release\ArcGISCodexBridgeAddin.esriAddinX'
}
else {
    $AddinPackage = [System.IO.Path]::GetFullPath($AddinPackage)
}
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build-addin.ps1') `
        -ArcGISProInstallDir $ArcGISProInstallDir `
        -MSBuild $MSBuild
    if ($LASTEXITCODE -ne 0) {
        throw "Add-in build failed with exit code $LASTEXITCODE"
    }
}
if (-not (Test-Path -LiteralPath $AddinPackage -PathType Leaf)) {
    throw "Add-in package was not found: $AddinPackage"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($AddinPackage)
try {
    $damlEntry = $archive.GetEntry('Config.daml')
    if (-not $damlEntry) {
        throw 'Config.daml was not found in the Add-in package.'
    }
    $reader = [System.IO.StreamReader]::new($damlEntry.Open())
    try {
        $packageDaml = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }
}
finally {
    $archive.Dispose()
}
try { $packageXml = [xml]$packageDaml }
catch { throw "Packaged Config.daml is not valid XML: $_" }
$packageAddInInfo = $packageXml.SelectSingleNode('//*[local-name()="AddInInfo"]')
$packageVersion = if ($packageAddInInfo) { [string]$packageAddInInfo.GetAttribute('version') } else { '' }
$packageId = if ($packageAddInInfo) { [string]$packageAddInInfo.GetAttribute('id') } else { '' }
if ([string]::IsNullOrWhiteSpace($packageVersion) -or
    [string]::IsNullOrWhiteSpace($packageId)) {
    throw 'Could not determine the version and ID from the Add-in package.'
}
if ($packageVersion -ne $Version) {
    throw "Add-in package version $packageVersion does not match release $Version."
}

$releaseName = "arcgis-pro-codex-bridge-$Version-win-x64"
$releaseRoot = Join-Path $OutputDirectory $releaseName
$zipPath = Join-Path $OutputDirectory "$releaseName.zip"
$checksumPath = "$zipPath.sha256"
foreach ($target in @($releaseRoot, $zipPath, $checksumPath)) {
    if (Test-Path -LiteralPath $target) {
        if (-not $Force) {
            throw "Release target already exists; pass -Force to replace it: $target"
        }
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stagingContainer = Join-Path $OutputDirectory ('.release-staging-' + [guid]::NewGuid().ToString('N'))
$stagingRoot = Join-Path $stagingContainer $releaseName
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
try {
    $releaseNotesRelative = "docs\release-v$Version.md"
    $protocolRelative = "docs\live-bridge-protocol-v$Version.md"
    $releaseFiles = @(
        'README.md',
        'README.zh-CN.md',
        'config.example.json',
        'docs\installation.md',
        $protocolRelative,
        $releaseNotesRelative,
        'src\arcgis_mcp\__init__.py',
        'src\arcgis_mcp\server.py',
        'src\arcgis_mcp\worker.py',
        'addin\ArcGISCodexBridgeAddin\Config.daml',
        'scripts\install.ps1',
        'scripts\install-addin.ps1',
        'scripts\uninstall.ps1',
        'scripts\status.ps1',
        'scripts\doctor.ps1',
        'scripts\ArcGISBridge.Installation.psm1',
        'scripts\mcp_probe.py'
    )
    foreach ($relativePath in $releaseFiles) {
        $source = Join-Path $sourceRoot $relativePath
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Required release file is missing: $source"
        }
        $destination = Join-Path $stagingRoot $relativePath
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }

    $prebuiltRoot = Join-Path $stagingRoot 'prebuilt'
    New-Item -ItemType Directory -Path $prebuiltRoot -Force | Out-Null
    Copy-Item -LiteralPath $AddinPackage `
        -Destination (Join-Path $prebuiltRoot 'ArcGISCodexBridgeAddin.esriAddinX') -Force

    $manifestFiles = @(
        Get-ChildItem -LiteralPath $stagingRoot -File -Recurse |
            Sort-Object FullName |
            ForEach-Object {
                [ordered]@{
                    path = $_.FullName.Substring($stagingRoot.TrimEnd('\').Length + 1).Replace('\', '/')
                    size = $_.Length
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            }
    )
    $manifest = [ordered]@{
        product = 'ArcGIS Pro Bridge for Codex'
        version = $Version
        platform = 'Windows x64'
        supported_product = 'ArcGIS Pro 3.6'
        addin_id = $packageId
        created_at_utc = [DateTime]::UtcNow.ToString('o')
        files = $manifestFiles
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $stagingRoot 'release-manifest.json'),
        ($manifest | ConvertTo-Json -Depth 6) + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false)
    )

    Move-Item -LiteralPath $stagingRoot -Destination $releaseRoot
}
finally {
    if (Test-Path -LiteralPath $stagingContainer) {
        Remove-Item -LiteralPath $stagingContainer -Recurse -Force
    }
}

Compress-Archive -LiteralPath $releaseRoot -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText(
    $checksumPath,
    "$zipHash  $([System.IO.Path]::GetFileName($zipPath))`n",
    [System.Text.UTF8Encoding]::new($false)
)

Write-Output "[PASS] Release directory: $releaseRoot"
Write-Output "[PASS] Release archive: $zipPath"
Write-Output "[PASS] SHA256: $zipHash"
