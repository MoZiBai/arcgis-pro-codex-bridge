Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ArcGISBridge.Installation.psm1') -Force

$root = Join-Path ([System.IO.Path]::GetTempPath()) `
    ('arcgis-bridge-install-contract-' + [guid]::NewGuid().ToString('N'))
$codexHome = Join-Path $root '.codex'
$configPath = Join-Path $codexHome 'config.toml'
New-Item -ItemType Directory -Path $codexHome -Force | Out-Null

try {
    $privateDirectory = Join-Path $root 'private-queue'
    $privateFile = Join-Path $privateDirectory 'live-bridge.key'
    New-Item -ItemType Directory -Path $privateDirectory -Force | Out-Null
    [System.IO.File]::WriteAllText($privateFile, 'contract-secret')
    Set-ArcGISBridgePrivatePathAcl -Path $privateDirectory -Container | Out-Null
    Set-ArcGISBridgePrivatePathAcl -Path $privateFile | Out-Null
    if (-not (Test-ArcGISBridgePrivatePathAcl -Path $privateDirectory) -or
        -not (Test-ArcGISBridgePrivatePathAcl -Path $privateFile)) {
        throw 'Private queue/key ACL contract failed.'
    }

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
    if ($applied -notmatch '(?m)^\[mcp_servers\.fastctx\]\r?$') {
        throw 'Global registration did not preserve another MCP server.'
    }
    $arcgisHeaders = [regex]::Matches(
        $applied,
        '(?m)^\[mcp_servers\.(?:arcgis-pro|"arcgis-pro")\]\r?$'
    ).Count
    if ($arcgisHeaders -ne 1) {
        throw "Global registration must be idempotent; found $arcgisHeaders arcgis-pro tables."
    }
    if (-not (Test-ArcGISBridgeGlobalMcpRegistration -CodexHome $codexHome)) {
        throw 'Global registration was not detected after apply.'
    }
    if (-not (Test-ArcGISBridgeGlobalMcpRegistration @parameters)) {
        throw 'Global registration did not exactly match its installed command and paths.'
    }
    $wrongParameters = @{} + $parameters
    $wrongParameters['Server'] = 'C:\Tools\DifferentBridge\runtime\server.py'
    if (Test-ArcGISBridgeGlobalMcpRegistration @wrongParameters) {
        throw 'Exact registration validation accepted a different installation.'
    }
    if (Remove-ArcGISBridgeGlobalMcpRegistration `
        -CodexHome $codexHome `
        -Server ([string]$wrongParameters.Server)) {
        throw 'A mismatched installation removed the active arcgis-pro registration.'
    }

    if (-not (Remove-ArcGISBridgeGlobalMcpRegistration `
        -CodexHome $codexHome `
        -Command ([string]$parameters.Command) `
        -Server ([string]$parameters.Server) `
        -Config ([string]$parameters.Config) `
        -WorkingDirectory ([string]$parameters.WorkingDirectory))) {
        throw 'Global registration removal reported no change.'
    }
    $removed = [System.IO.File]::ReadAllText($configPath)
    if ($removed -match '(?m)^\[mcp_servers\.(?:arcgis-pro|"arcgis-pro")\]\r?$') {
        throw 'Global registration removal left the arcgis-pro table behind.'
    }
    if ($removed -notmatch 'experimental_bearer_token\s*=\s*"must-preserve"' -or
        $removed -notmatch '(?m)^\[mcp_servers\.fastctx\]\r?$') {
        throw 'Global registration removal damaged unrelated configuration.'
    }

    $addinInstaller = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'install-addin.ps1') -Raw
    if ($addinInstaller -notmatch '\$null\s+-ne\s+\$registrationExitCode' -or
        $addinInstaller -notmatch 'hash-verified package will load after ArcGIS Pro restarts') {
        throw 'Add-in installer must tolerate RegisterAddIn returning no exit code while Pro is open.'
    }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $syntheticPackage = Join-Path $root 'synthetic-source.esriAddinX'
    $archive = [System.IO.Compression.ZipFile]::Open(
        $syntheticPackage,
        [System.IO.Compression.ZipArchiveMode]::Create
    )
    try {
        $entry = $archive.CreateEntry('Config.daml')
        $writer = [System.IO.StreamWriter]::new($entry.Open())
        try {
            $writer.Write('<AddInInfo version="9.9.9" id="contract-addin"></AddInInfo>')
        }
        finally { $writer.Dispose() }
    }
    finally { $archive.Dispose() }
    $addinTargetDirectory = Join-Path $root 'exact-addin-target'
    $addinTarget = Join-Path $addinTargetDirectory 'preserved-installed-name.esriAddinX'
    & (Join-Path $PSScriptRoot 'install-addin.ps1') `
        -Package $syntheticPackage `
        -ExpectedVersion '9.9.9' `
        -InstallDirectory $addinTargetDirectory `
        -InstalledPackagePath $addinTarget `
        -SkipRegistration
    if (-not (Test-Path -LiteralPath $addinTarget -PathType Leaf) -or
        (Test-Path -LiteralPath (Join-Path $addinTargetDirectory 'synthetic-source.esriAddinX'))) {
        throw 'Add-in installer did not preserve the manifest-owned target package path.'
    }
    $targetInfo = Get-ArcGISBridgeAddinPackageInfo -Package $addinTarget
    if ($targetInfo.id -ne 'contract-addin' -or $targetInfo.version -ne '9.9.9') {
        throw 'Exact Add-in target did not retain the selected package identity.'
    }
    if (-not (Test-ArcGISBridgePathWithinDirectory `
        -Directory $addinTargetDirectory `
        -Path $addinTarget) -or
        (Test-ArcGISBridgePathWithinDirectory `
            -Directory $addinTargetDirectory `
            -Path ($addinTargetDirectory + '-other\package.esriAddinX'))) {
        throw 'PowerShell 5.1-compatible path ownership accepted an adjacent directory.'
    }

    $locatorPath = Join-Path $root 'locator\install-location.json'
    $customRoot = Join-Path $root 'custom-install'
    $differentRoot = Join-Path $root 'different-install'
    Set-ArcGISBridgeInstallLocator `
        -InstallRoot $customRoot `
        -InstallLocatorPath $locatorPath | Out-Null
    $resolvedRoot = Resolve-ArcGISBridgeInstallRoot -InstallLocatorPath $locatorPath
    if (-not $resolvedRoot.Equals(
        [System.IO.Path]::GetFullPath($customRoot).TrimEnd('\'),
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw "Custom install locator did not resolve correctly: $resolvedRoot"
    }
    if (Remove-ArcGISBridgeInstallLocator `
        -InstallRoot $differentRoot `
        -InstallLocatorPath $locatorPath) {
        throw 'Install locator removed itself for a non-matching installation root.'
    }
    if (-not (Test-Path -LiteralPath $locatorPath)) {
        throw 'Non-matching uninstall damaged the active install locator.'
    }
    if (-not (Remove-ArcGISBridgeInstallLocator `
        -InstallRoot $customRoot `
        -InstallLocatorPath $locatorPath)) {
        throw 'Matching uninstall did not remove the install locator.'
    }

    $ownedInstallRoot = Join-Path $root 'owned-install'
    $ownedLocatorPath = Join-Path $root 'owned-locator\install-location.json'
    New-Item -ItemType Directory -Path $ownedInstallRoot -Force | Out-Null
    $ownedManifest = [ordered]@{
        python = [string]$parameters.Command
        server = (Join-Path $ownedInstallRoot 'runtime\server.py')
        config = (Join-Path $ownedInstallRoot 'config\config.json')
        addin_id = [string]$targetInfo.id
        addin_directory = $addinTargetDirectory
        addin_package = $addinTarget
        addin_sha256 = [string]$targetInfo.sha256
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $ownedInstallRoot 'install.json'),
        ($ownedManifest | ConvertTo-Json -Depth 4),
        [System.Text.UTF8Encoding]::new($false)
    )
    Set-ArcGISBridgeGlobalMcpRegistration `
        -CodexHome $codexHome `
        -Command ([string]$ownedManifest.python) `
        -Server ([string]$ownedManifest.server) `
        -Config ([string]$ownedManifest.config) `
        -WorkingDirectory $ownedInstallRoot | Out-Null
    Set-ArcGISBridgeInstallLocator `
        -InstallRoot $ownedInstallRoot `
        -InstallLocatorPath $ownedLocatorPath | Out-Null
    & (Join-Path $PSScriptRoot 'uninstall.ps1') `
        -InstallRoot $ownedInstallRoot `
        -InstallLocatorPath $ownedLocatorPath `
        -CodexHome $codexHome `
        -RemoveAddin `
        -Confirm:$false
    if ((Test-Path -LiteralPath $ownedInstallRoot) -or
        (Test-Path -LiteralPath $ownedLocatorPath) -or
        (Test-Path -LiteralPath $addinTargetDirectory) -or
        (Test-ArcGISBridgeGlobalMcpRegistration -CodexHome $codexHome)) {
        throw 'Exact uninstall lifecycle left an owned runtime, locator, Add-in, or registration behind.'
    }

    & (Join-Path $PSScriptRoot 'install-addin.ps1') `
        -Package $syntheticPackage `
        -ExpectedVersion '9.9.9' `
        -InstallDirectory $addinTargetDirectory `
        -InstalledPackagePath $addinTarget `
        -SkipRegistration
    $preflightInfo = Get-ArcGISBridgeAddinPackageInfo -Package $addinTarget
    $preflightInstallRoot = Join-Path $root 'preflight-install'
    $preflightLocatorPath = Join-Path $root 'preflight-locator\install-location.json'
    New-Item -ItemType Directory -Path $preflightInstallRoot -Force | Out-Null
    $preflightManifest = [ordered]@{
        python = [string]$parameters.Command
        server = (Join-Path $preflightInstallRoot 'runtime\server.py')
        config = (Join-Path $preflightInstallRoot 'config\config.json')
        addin_id = [string]$preflightInfo.id
        addin_directory = $addinTargetDirectory
        addin_package = $addinTarget
        addin_sha256 = [string]$preflightInfo.sha256
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $preflightInstallRoot 'install.json'),
        ($preflightManifest | ConvertTo-Json -Depth 4),
        [System.Text.UTF8Encoding]::new($false)
    )
    Set-ArcGISBridgeGlobalMcpRegistration `
        -CodexHome $codexHome `
        -Command ([string]$preflightManifest.python) `
        -Server ([string]$preflightManifest.server) `
        -Config ([string]$preflightManifest.config) `
        -WorkingDirectory $preflightInstallRoot | Out-Null
    Set-ArcGISBridgeInstallLocator `
        -InstallRoot $preflightInstallRoot `
        -InstallLocatorPath $preflightLocatorPath | Out-Null
    $changedArchive = [System.IO.Compression.ZipFile]::Open(
        $addinTarget,
        [System.IO.Compression.ZipArchiveMode]::Update
    )
    try {
        $entry = $changedArchive.CreateEntry('changed-after-install.txt')
        $writer = [System.IO.StreamWriter]::new($entry.Open())
        try { $writer.Write('must make uninstall preflight fail') }
        finally { $writer.Dispose() }
    }
    finally { $changedArchive.Dispose() }
    $preflightRejected = $false
    try {
        & (Join-Path $PSScriptRoot 'uninstall.ps1') `
            -InstallRoot $preflightInstallRoot `
            -InstallLocatorPath $preflightLocatorPath `
            -CodexHome $codexHome `
            -RemoveAddin `
            -Confirm:$false
    }
    catch { $preflightRejected = $true }
    if (-not $preflightRejected -or
        -not (Test-Path -LiteralPath $preflightInstallRoot) -or
        -not (Test-Path -LiteralPath $preflightLocatorPath) -or
        -not (Test-Path -LiteralPath $addinTarget) -or
        -not (Test-ArcGISBridgeGlobalMcpRegistration `
            -CodexHome $codexHome `
            -Command ([string]$preflightManifest.python) `
            -Server ([string]$preflightManifest.server) `
            -Config ([string]$preflightManifest.config) `
            -WorkingDirectory $preflightInstallRoot)) {
        throw 'Changed Add-in preflight did not preserve the entire installation before refusing uninstall.'
    }

    Write-Output 'installation contract ok: private ACLs, exact ownership, config preservation, Add-in target/uninstall preflight, restart safety, and custom locators'
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
