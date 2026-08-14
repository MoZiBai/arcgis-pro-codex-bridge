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
    Expand-Archive -LiteralPath $zip.FullName -DestinationPath $extractRoot
    $packageRoot = Get-ChildItem -LiteralPath $extractRoot -Directory | Select-Object -First 1
    if (-not $packageRoot) { throw 'Release ZIP does not contain its versioned root directory.' }

    $manifestPath = Join-Path $packageRoot.FullName 'release-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.version -ne '0.28.0' -or $manifest.supported_product -ne 'ArcGIS Pro 3.6') {
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
        'README.md',
        'config.example.json',
        'docs/installation.md',
        'docs/release-v0.28.0.md',
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

    $installParameters = @{
        InstallRoot = $installRoot
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
    if ($probeResult.server.version -ne '0.28.0' -or $probeResult.toolCount -ne 253 -or
        -not $probeResult.instructions) {
        throw "Unexpected MCP probe result: $($probeOutput -join [Environment]::NewLine)"
    }

    & (Join-Path $packageRoot.FullName 'scripts\uninstall.ps1') `
        -InstallRoot $installRoot `
        -CodexHome $codexHome `
        -Confirm:$false
    if (Test-Path -LiteralPath $installRoot) {
        throw 'Release uninstall left the isolated installation root behind.'
    }

    Write-Output 'release lifecycle ok: exact ZIP, hashes, privacy scan, no-MSBuild prebuilt install, 253-tool MCP probe, and uninstall'
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
