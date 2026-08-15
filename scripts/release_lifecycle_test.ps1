param(
    [string]$ArcGISProInstallDir = '',
    [string]$Python = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) `
    ('arcgis-bridge-release-' + [guid]::NewGuid().ToString('N'))
$releaseOutput = Join-Path $testRoot 'release'
$extractRoot = Join-Path $testRoot 'extracted'
$installRoot = Join-Path $testRoot 'install'
$codexHome = Join-Path $testRoot 'codex-home'
$dataRoot = Join-Path $testRoot 'data'
$outputRoot = Join-Path $testRoot 'output'
$queueRoot = Join-Path $testRoot 'queue'
$addinRoot = Join-Path $testRoot 'isolated-addin'
$installLocatorPath = Join-Path $testRoot 'locator\install-location.json'
$sourceVersionMatch = Select-String -LiteralPath (Join-Path $sourceRoot 'src\arcgis_mcp\server.py') `
    -Pattern '^VERSION\s*=\s*"([^"]+)"$' | Select-Object -First 1
if (-not $sourceVersionMatch) { throw 'Could not determine source version.' }
$sourceVersion = $sourceVersionMatch.Matches[0].Groups[1].Value

try {
    $buildParameters = @{
        OutputDirectory = $releaseOutput
        AddinPackage = (Join-Path $sourceRoot `
            'addin\ArcGISCodexBridgeAddin\bin\x64\Release\ArcGISCodexBridgeAddin.esriAddinX')
        SkipBuild = $true
        Force = $true
    }
    & (Join-Path $PSScriptRoot 'build-release.ps1') @buildParameters

    $zip = Get-ChildItem -LiteralPath $releaseOutput -Filter '*.zip' | Select-Object -First 1
    if (-not $zip) { throw 'Release ZIP was not produced.' }
    $checksumFile = "$($zip.FullName).sha256"
    $checksumText = [System.IO.File]::ReadAllText($checksumFile)
    if ($checksumText.Contains("`r")) {
        throw 'Release checksum file must use cross-platform LF line endings.'
    }
    $declaredZipHash = ($checksumText.Trim() -split '\s+')[0]
    $actualZipHash = (Get-FileHash -LiteralPath $zip.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($declaredZipHash -ne $actualZipHash) {
        throw 'Release ZIP checksum file does not match the archive.'
    }
    Expand-Archive -LiteralPath $zip.FullName -DestinationPath $extractRoot
    $packageRoot = Get-ChildItem -LiteralPath $extractRoot -Directory | Select-Object -First 1
    if (-not $packageRoot) { throw 'Release ZIP does not contain its versioned root directory.' }

    $manifestPath = Join-Path $packageRoot.FullName 'release-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.version -ne $sourceVersion -or $manifest.supported_product -ne 'ArcGIS Pro 3.6') {
        throw "Unexpected release metadata: $($manifest.version), $($manifest.supported_product)"
    }
    foreach ($entry in $manifest.files) {
        $filePath = Join-Path $packageRoot.FullName ([string]$entry.path)
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            throw "Manifest file is missing: $($entry.path)"
        }
        $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne [string]$entry.sha256) {
            throw "Manifest hash mismatch: $($entry.path)"
        }
    }

    $expectedFiles = @(
        'LICENSE',
        'README.md',
        'README.zh-CN.md',
        'config.example.json',
        'docs/installation.md',
        "docs/live-bridge-protocol-v$sourceVersion.md",
        "docs/release-v$sourceVersion.md",
        'src/arcgis_mcp/__init__.py',
        'src/arcgis_mcp/server.py',
        'src/arcgis_mcp/worker.py',
        'addin/ArcGISCodexBridgeAddin/Config.daml',
        'prebuilt/ArcGISCodexBridgeAddin.esriAddinX',
        'scripts/install.ps1',
        'scripts/install-addin.ps1',
        'scripts/uninstall.ps1',
        'scripts/status.ps1',
        'scripts/doctor.ps1',
        'scripts/ArcGISBridge.Installation.psm1',
        'scripts/mcp_probe.py',
        'release-manifest.json'
    ) | Sort-Object
    $actualFiles = @(
        Get-ChildItem -LiteralPath $packageRoot.FullName -File -Recurse |
            ForEach-Object {
                $_.FullName.Substring($packageRoot.FullName.TrimEnd('\').Length + 1).Replace('\', '/')
            } |
            Sort-Object
    )
    if (($expectedFiles -join "`n") -ne ($actualFiles -join "`n")) {
        throw "Release file set is not exact.`nExpected:`n$($expectedFiles -join "`n")`nActual:`n$($actualFiles -join "`n")"
    }

    $privateMarkers = @(
        $sourceRoot,
        [Environment]::GetFolderPath('UserProfile'),
        'ghp_',
        'github_pat_',
        '.sandbox-secrets'
    )
    $privatePatterns = @(
        '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}',
        '-----BEGIN (?:RSA|OPENSSH|EC) PRIVATE KEY-----'
    )
    foreach ($file in Get-ChildItem -LiteralPath $packageRoot.FullName -File -Recurse |
        Where-Object { $_.Extension -in @('.ps1', '.psm1', '.py', '.md', '.json', '.daml') }) {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($marker in $privateMarkers) {
            if ($text.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw "Private or credential marker '$marker' found in $($file.FullName)"
            }
        }
        foreach ($pattern in $privatePatterns) {
            if ([regex]::IsMatch(
                $text,
                $pattern,
                [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
            )) {
                throw "Private or credential pattern '$pattern' found in $($file.FullName)"
            }
        }
    }

    $addinPackage = Join-Path $packageRoot.FullName 'prebuilt\ArcGISCodexBridgeAddin.esriAddinX'
    $addinPrivacyRoot = Join-Path $testRoot 'addin-privacy'
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $addinArchive = [System.IO.Compression.ZipFile]::OpenRead($addinPackage)
    try {
        $pdbEntries = @($addinArchive.Entries | Where-Object { $_.FullName -match '(?i)\.pdb$' })
        if ($pdbEntries.Count -gt 0) {
            throw "Add-in package contains forbidden PDB entries: $($pdbEntries.FullName -join ', ')"
        }
    }
    finally {
        $addinArchive.Dispose()
    }
    [System.IO.Compression.ZipFile]::ExtractToDirectory($addinPackage, $addinPrivacyRoot)
    foreach ($file in Get-ChildItem -LiteralPath $addinPrivacyRoot -File -Recurse) {
        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        $representations = @(
            [System.Text.Encoding]::ASCII.GetString($bytes),
            [System.Text.Encoding]::Unicode.GetString($bytes)
        )
        foreach ($text in $representations) {
            foreach ($marker in $privateMarkers) {
                if ($text.IndexOf($marker, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    throw "Private or credential marker '$marker' found in Add-in entry $($file.FullName)"
                }
            }
            foreach ($pattern in $privatePatterns) {
                if ([regex]::IsMatch($text, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
                    throw "Private or credential pattern '$pattern' found in Add-in entry $($file.FullName)"
                }
            }
        }
    }

    $installParameters = @{
        InstallRoot = $installRoot
        InstallLocatorPath = $installLocatorPath
        CodexHome = $codexHome
        AllowedDataRoot = @($dataRoot)
        OutputRoot = $outputRoot
        QueueRoot = $queueRoot
        AddinInstallDirectory = $addinRoot
        SkipAddinRegistration = $true
        MSBuild = 'Z:\must-not-be-used\MSBuild.exe'
        Confirm = $false
    }
    if ($ArcGISProInstallDir) { $installParameters['ArcGISProInstallDir'] = $ArcGISProInstallDir }
    if ($Python) { $installParameters['Python'] = $Python }
    & (Join-Path $packageRoot.FullName 'scripts\install.ps1') @installParameters

    $installManifest = Get-Content -LiteralPath (Join-Path $installRoot 'install.json') -Raw | ConvertFrom-Json
    if ($installManifest.addin_install_mode -ne 'prebuilt-release' -or
        -not $installManifest.addin_registration_skipped) {
        throw 'Release installer did not select the prebuilt Add-in package.'
    }
    $installedAddin = Get-ChildItem -LiteralPath $addinRoot -Filter '*.esriAddinX' | Select-Object -First 1
    if (-not $installedAddin) { throw 'Isolated prebuilt Add-in installation did not produce a package.' }
    if ([string]$installManifest.addin_package -ne $installedAddin.FullName -or
        [string]$installManifest.addin_directory -ne $addinRoot -or
        [string]$installManifest.addin_version -ne $sourceVersion -or
        [string]::IsNullOrWhiteSpace([string]$installManifest.addin_sha256)) {
        throw 'Release installation manifest did not record the exact Add-in path, version, and hash.'
    }

    & (Join-Path $packageRoot.FullName 'scripts\status.ps1') `
        -InstallRoot $installRoot `
        -InstallLocatorPath $installLocatorPath `
        -CodexHome $codexHome `
        -Python $Python
    if ($LASTEXITCODE -ne 0) { throw "Installed release status failed with exit code $LASTEXITCODE" }

    if (-not $Python) { $Python = [string]$installManifest.python }
    $probe = Join-Path $installRoot 'tools\mcp_probe.py'
    $probeOutput = & $Python $probe `
        --python $Python `
        --server ([string]$installManifest.server) `
        --config ([string]$installManifest.config)
    if ($LASTEXITCODE -ne 0) {
        throw "Installed MCP probe failed: $($probeOutput -join [Environment]::NewLine)"
    }
    $probeResult = ($probeOutput -join [Environment]::NewLine) | ConvertFrom-Json
    if ($probeResult.server.version -ne $sourceVersion -or $probeResult.toolCount -ne 253 -or
        -not $probeResult.instructions) {
        throw "Unexpected MCP probe result: $($probeOutput -join [Environment]::NewLine)"
    }

    $previousInstalledAddin = "$($installedAddin.FullName).previous"
    Copy-Item -LiteralPath $installedAddin.FullName -Destination $previousInstalledAddin -Force
    $previousArchive = [System.IO.Compression.ZipFile]::Open(
        $previousInstalledAddin,
        [System.IO.Compression.ZipArchiveMode]::Update
    )
    try {
        $entry = $previousArchive.CreateEntry('transaction-existing-backup.txt')
        $writer = [System.IO.StreamWriter]::new($entry.Open())
        try { $writer.Write('must survive failed upgrade') }
        finally { $writer.Dispose() }
    }
    finally {
        $previousArchive.Dispose()
    }
    $candidateArchive = [System.IO.Compression.ZipFile]::Open(
        $addinPackage,
        [System.IO.Compression.ZipArchiveMode]::Update
    )
    try {
        $entry = $candidateArchive.CreateEntry('transaction-new-package.txt')
        $writer = [System.IO.StreamWriter]::new($entry.Open())
        try { $writer.Write('forces the Add-in replacement path') }
        finally { $writer.Dispose() }
    }
    finally {
        $candidateArchive.Dispose()
    }

    $installedCodexConfig = Join-Path $codexHome 'config.toml'
    $correctCodexConfig = [System.IO.File]::ReadAllBytes($installedCodexConfig)
    Import-Module (Join-Path $packageRoot.FullName 'scripts\ArcGISBridge.Installation.psm1') -Force
    Set-ArcGISBridgeGlobalMcpRegistration `
        -CodexHome $codexHome `
        -Command ([string]$installManifest.python) `
        -Server (Join-Path $installRoot 'runtime\wrong-server.py') `
        -Config ([string]$installManifest.config) `
        -WorkingDirectory $installRoot | Out-Null
    $previousRuntimeMarker = Join-Path $installRoot 'runtime.previous\transaction-marker.txt'
    New-Item -ItemType Directory -Path (Split-Path -Parent $previousRuntimeMarker) -Force | Out-Null
    [System.IO.File]::WriteAllText($previousRuntimeMarker, 'must survive failed upgrade')

    $rollbackPaths = @(
        (Join-Path $installRoot 'runtime\server.py'),
        $previousRuntimeMarker,
        (Join-Path $installRoot 'config\config.json'),
        (Join-Path $installRoot 'install.json'),
        $installedAddin.FullName,
        $previousInstalledAddin,
        $installedCodexConfig,
        $installLocatorPath
    )
    $rollbackHashes = @{}
    foreach ($path in $rollbackPaths) {
        $rollbackHashes[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
    $badLocatorParent = Join-Path $testRoot 'locator-parent-is-a-file'
    [System.IO.File]::WriteAllText($badLocatorParent, 'force failure after MCP registration')
    $badLocatorPath = Join-Path $badLocatorParent 'install-location.json'
    $rollbackTriggered = $false
    try {
        $badInstallParameters = @{} + $installParameters
        $badInstallParameters['InstallLocatorPath'] = $badLocatorPath
        & (Join-Path $packageRoot.FullName 'scripts\install.ps1') @badInstallParameters
    }
    catch {
        $rollbackTriggered = $true
    }
    if (-not $rollbackTriggered) {
        throw 'Late registration failure did not fail the transactional install test.'
    }
    foreach ($path in $rollbackPaths) {
        $afterHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($afterHash -ne $rollbackHashes[$path]) {
            throw "Transactional install did not restore $path"
        }
    }
    [System.IO.File]::WriteAllBytes($installedCodexConfig, $correctCodexConfig)

    & (Join-Path $packageRoot.FullName 'scripts\uninstall.ps1') `
        -InstallRoot $installRoot `
        -InstallLocatorPath $installLocatorPath `
        -CodexHome $codexHome `
        -RemoveAddin `
        -Confirm:$false
    if (Test-Path -LiteralPath $installRoot) {
        throw 'Release uninstall left the isolated installation root behind.'
    }
    if (Test-Path -LiteralPath $installLocatorPath) {
        throw 'Release uninstall left the isolated install locator behind.'
    }
    if (Test-Path -LiteralPath $addinRoot) {
        throw 'Release uninstall left the isolated Add-in directory behind.'
    }

    Write-Output 'release lifecycle ok: exact ZIP, privacy, exact Add-in state, late-failure rollback, 253-tool probe, and exact uninstall'
}
finally {
    $resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\')
    $resolvedTest = [System.IO.Path]::GetFullPath($testRoot).TrimEnd('\')
    if ((Split-Path -Parent $resolvedTest).TrimEnd('\') -ne $resolvedTemp -or
        (Split-Path -Leaf $resolvedTest) -notlike 'arcgis-bridge-release-*') {
        throw "Refusing to clean unexpected release test path: $resolvedTest"
    }
    if (Test-Path -LiteralPath $resolvedTest) {
        Remove-Item -LiteralPath $resolvedTest -Recurse -Force
    }
}
