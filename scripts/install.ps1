[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string]$InstallRoot = '',
    [string]$InstallLocatorPath = '',
    [string]$CodexHome = '',
    [string]$Python = '',
    [string]$ArcGISProInstallDir = '',
    [string]$MSBuild = '',
    [string]$AddinPackage = '',
    [string]$AddinInstallDirectory = '',
    [string]$ImportConfig = '',
    [string[]]$AllowedDataRoot = @(),
    [string]$OutputRoot = '',
    [string]$QueueRoot = '',
    [switch]$SkipAddin,
    [switch]$SkipAddinRegistration,
    [switch]$SkipGlobalRegistration
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Import-Module (Join-Path $PSScriptRoot 'ArcGISBridge.Installation.psm1') -Force

$InstallRoot = Resolve-ArcGISBridgeInstallRoot `
    -InstallRoot $InstallRoot `
    -InstallLocatorPath $InstallLocatorPath
$InstallRoot = Assert-ArcGISBridgeSafeInstallRoot -InstallRoot $InstallRoot
$CodexHome = Resolve-ArcGISBridgeCodexHome -CodexHome $CodexHome
$Python = Resolve-ArcGISBridgePython -Python $Python
$sourceConfig = if ($ImportConfig) {
    [System.IO.Path]::GetFullPath($ImportConfig)
}
else {
    Join-Path $sourceRoot 'config.json'
}
$ArcGISProInstallDir = Resolve-ArcGISBridgeProInstall `
    -ArcGISProInstallDir $ArcGISProInstallDir `
    -ExistingConfig $sourceConfig

$arcgisExe = Join-Path $ArcGISProInstallDir 'bin\ArcGISPro.exe'
$productVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($arcgisExe).ProductVersion
if ($productVersion -notmatch '^3\.6(?:\.|$)') {
    throw "This release supports ArcGIS Pro 3.6 only; detected $productVersion at $ArcGISProInstallDir"
}

$versionMatch = Select-String -LiteralPath (Join-Path $sourceRoot 'src\arcgis_mcp\server.py') `
    -Pattern '^VERSION\s*=\s*"([^"]+)"$' | Select-Object -First 1
if (-not $versionMatch) {
    throw 'Could not determine the MCP bridge version from server.py.'
}
$version = $versionMatch.Matches[0].Groups[1].Value

$runtimeRoot = Join-Path $InstallRoot 'runtime'
$configRoot = Join-Path $InstallRoot 'config'
$configPath = Join-Path $configRoot 'config.json'
$toolsRoot = Join-Path $InstallRoot 'tools'
$serverPath = Join-Path $runtimeRoot 'server.py'
$defaultDataRoot = Join-Path $InstallRoot 'data'
$defaultOutputRoot = Join-Path $InstallRoot 'outputs'
$defaultQueueRoot = Join-Path $InstallRoot 'queue'
$manifestPath = Join-Path $InstallRoot 'install.json'

$existing = $null
if (Test-Path -LiteralPath $configPath) {
    $existing = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
}
elseif ($ImportConfig) {
    if (-not (Test-Path -LiteralPath $sourceConfig)) {
        throw "Import config was not found: $sourceConfig"
    }
    $existing = Get-Content -LiteralPath $sourceConfig -Raw | ConvertFrom-Json
}
$settings = [ordered]@{}
if ($existing) {
    foreach ($property in $existing.PSObject.Properties) {
        $settings[$property.Name] = $property.Value
    }
}

$arcgisPython = Join-Path $ArcGISProInstallDir 'bin\Python\envs\arcgispro-py3\python.exe'
if (-not (Test-Path -LiteralPath $arcgisPython)) {
    throw "ArcGIS Pro Python was not found: $arcgisPython"
}
$settings['arcgis_python'] = $arcgisPython
$settings['arcgis_pro_install_dir'] = $ArcGISProInstallDir
if ($AllowedDataRoot.Count -gt 0) {
    $settings['allowed_data_roots'] = @($AllowedDataRoot | ForEach-Object { [System.IO.Path]::GetFullPath($_) })
}
elseif (-not $settings.Contains('allowed_data_roots')) {
    $settings['allowed_data_roots'] = @($defaultDataRoot)
}
if (-not [string]::IsNullOrWhiteSpace($OutputRoot)) {
    $settings['output_root'] = [System.IO.Path]::GetFullPath($OutputRoot)
}
elseif (-not $settings.Contains('output_root')) {
    $settings['output_root'] = $defaultOutputRoot
}
if (-not [string]::IsNullOrWhiteSpace($QueueRoot)) {
    $settings['live_bridge_queue'] = [System.IO.Path]::GetFullPath($QueueRoot)
}
elseif (-not $settings.Contains('live_bridge_queue')) {
    $settings['live_bridge_queue'] = $defaultQueueRoot
}

$defaults = [ordered]@{
    live_bridge_timeout_seconds = 180
    live_arcpy_worker_fallback = $true
    timeout_seconds = 1800
    allow_destructive_tools = $false
    allow_source_editing = $false
    allow_schema_changes = $false
    allow_external_publishing = $false
    allow_external_workflow_actions = $false
    allow_framework_ui_control = $false
    allowed_framework_commands = @()
    allowed_framework_panes = @()
    allowed_framework_tabs = @()
    allowed_framework_tools = @()
    allowed_backstage_pages = @()
}
foreach ($entry in $defaults.GetEnumerator()) {
    if (-not $settings.Contains($entry.Key)) {
        $settings[$entry.Key] = $entry.Value
    }
}

if (-not $PSCmdlet.ShouldProcess($InstallRoot, "Install ArcGIS Pro MCP bridge $version")) {
    return
}

$existingManifest = if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
}
else { $null }
$damlText = Get-Content -LiteralPath (Join-Path $sourceRoot 'addin\ArcGISCodexBridgeAddin\Config.daml') -Raw
try { $damlXml = [xml]$damlText }
catch { throw "Source Config.daml is not valid XML: $_" }
$addInInfo = $damlXml.SelectSingleNode('//*[local-name()="AddInInfo"]')
$addinId = if ($addInInfo) { [string]$addInInfo.GetAttribute('id') } else { '' }
if ([string]::IsNullOrWhiteSpace($addinId)) { throw 'Could not determine the Add-in ID from Config.daml.' }
$resolvedAddinDirectory = if (-not [string]::IsNullOrWhiteSpace($AddinInstallDirectory)) {
    [System.IO.Path]::GetFullPath($AddinInstallDirectory)
}
elseif ($existingManifest -and $existingManifest.addin_directory) {
    [System.IO.Path]::GetFullPath([string]$existingManifest.addin_directory)
}
else {
    Join-Path ([Environment]::GetFolderPath('MyDocuments')) "ArcGIS\AddIns\ArcGISPro\$addinId"
}

$selectedAddinPackage = ''
$selectedAddinInfo = $null
$addinInstallMode = 'skipped-no-addin'
if (-not $SkipAddin) {
    $prebuiltPackage = Join-Path $sourceRoot 'prebuilt\ArcGISCodexBridgeAddin.esriAddinX'
    if (-not [string]::IsNullOrWhiteSpace($AddinPackage)) {
        $selectedAddinPackage = [System.IO.Path]::GetFullPath($AddinPackage)
        $addinInstallMode = 'explicit-package'
    }
    elseif (Test-Path -LiteralPath $prebuiltPackage -PathType Leaf) {
        $selectedAddinPackage = $prebuiltPackage
        $addinInstallMode = 'prebuilt-release'
    }
    else {
        & (Join-Path $PSScriptRoot 'build-addin.ps1') `
            -ArcGISProInstallDir $ArcGISProInstallDir `
            -MSBuild $MSBuild
        if ($LASTEXITCODE -ne 0) { throw "Add-in build failed with exit code $LASTEXITCODE" }
        $selectedAddinPackage = Join-Path $sourceRoot `
            'addin\ArcGISCodexBridgeAddin\bin\x64\Release\ArcGISCodexBridgeAddin.esriAddinX'
        $addinInstallMode = 'source-build'
    }
    $selectedAddinInfo = Get-ArcGISBridgeAddinPackageInfo -Package $selectedAddinPackage
    if ($selectedAddinInfo.version -ne $version -or $selectedAddinInfo.id -ne $addinId) {
        throw "Add-in package identity $($selectedAddinInfo.id) version $($selectedAddinInfo.version) does not match bridge $addinId version $version."
    }
    $recordedAddinPackage = if ($existingManifest -and $existingManifest.addin_package) {
        [System.IO.Path]::GetFullPath([string]$existingManifest.addin_package)
    }
    else { '' }
    $installedAddinPackage = if ($recordedAddinPackage -and
        (Split-Path -Parent $recordedAddinPackage).TrimEnd('\').Equals(
            $resolvedAddinDirectory.TrimEnd('\'),
            [StringComparison]::OrdinalIgnoreCase
        ) -and
        [System.IO.Path]::GetExtension($recordedAddinPackage) -ieq '.esriAddinX') {
        $recordedAddinPackage
    }
    else {
        Join-Path $resolvedAddinDirectory ([System.IO.Path]::GetFileName($selectedAddinPackage))
    }
}
else {
    $installedAddinPackage = if ($existingManifest -and $existingManifest.addin_package) {
        [System.IO.Path]::GetFullPath([string]$existingManifest.addin_package)
    }
    else {
        Get-ChildItem -LiteralPath $resolvedAddinDirectory -Filter '*.esriAddInX' -File -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty FullName
    }
    if ($installedAddinPackage -and (Test-Path -LiteralPath $installedAddinPackage -PathType Leaf)) {
        $selectedAddinInfo = Get-ArcGISBridgeAddinPackageInfo -Package $installedAddinPackage
        if ($selectedAddinInfo.version -ne $version -or $selectedAddinInfo.id -ne $addinId) {
            throw "-SkipAddin would leave incompatible Add-in $($selectedAddinInfo.version); bridge $version requires the same Add-in version."
        }
        $addinInstallMode = 'existing-compatible'
    }
    else {
        $installedAddinPackage = ''
    }
}

$locatorPath = Get-ArcGISBridgeInstallLocatorPath -InstallLocatorPath $InstallLocatorPath
$locatorDirectory = Split-Path -Parent $locatorPath
$probePath = Join-Path $toolsRoot 'mcp_probe.py'
$codexConfigPath = Join-Path $CodexHome 'config.toml'
$previousRuntime = Join-Path $InstallRoot 'runtime.previous'
$configExistedBefore = Test-Path -LiteralPath $configPath -PathType Leaf
$configBefore = if ($configExistedBefore) { [System.IO.File]::ReadAllBytes($configPath) } else { $null }
$manifestExistedBefore = Test-Path -LiteralPath $manifestPath -PathType Leaf
$manifestBefore = if ($manifestExistedBefore) { [System.IO.File]::ReadAllBytes($manifestPath) } else { $null }
$probeExistedBefore = Test-Path -LiteralPath $probePath -PathType Leaf
$probeBefore = if ($probeExistedBefore) { [System.IO.File]::ReadAllBytes($probePath) } else { $null }
$codexConfigExistedBefore = Test-Path -LiteralPath $codexConfigPath -PathType Leaf
$codexConfigBefore = if ($codexConfigExistedBefore) { [System.IO.File]::ReadAllBytes($codexConfigPath) } else { $null }
$locatorExistedBefore = Test-Path -LiteralPath $locatorPath -PathType Leaf
$locatorBefore = if ($locatorExistedBefore) { [System.IO.File]::ReadAllBytes($locatorPath) } else { $null }
$codexHomeExistedBefore = Test-Path -LiteralPath $CodexHome -PathType Container
$locatorDirectoryExistedBefore = Test-Path -LiteralPath $locatorDirectory -PathType Container
$addinDirectoryExistedBefore = Test-Path -LiteralPath $resolvedAddinDirectory -PathType Container
$installRootExistedBefore = Test-Path -LiteralPath $InstallRoot -PathType Container
$configRootExistedBefore = Test-Path -LiteralPath $configRoot -PathType Container
$toolsRootExistedBefore = Test-Path -LiteralPath $toolsRoot -PathType Container
$runtimeExistedBefore = Test-Path -LiteralPath $runtimeRoot -PathType Container
$previousRuntimeExistedBefore = Test-Path -LiteralPath $previousRuntime -PathType Container
$addinExistedBefore = $installedAddinPackage -and (Test-Path -LiteralPath $installedAddinPackage -PathType Leaf)
$addinPreviousPackage = if ($installedAddinPackage) { "$installedAddinPackage.previous" } else { '' }
$addinPreviousExistedBefore = $addinPreviousPackage -and (Test-Path -LiteralPath $addinPreviousPackage -PathType Leaf)

New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
$staging = Join-Path $InstallRoot ('.staging-' + [guid]::NewGuid().ToString('N'))
$stagedRuntime = Join-Path $staging 'runtime'
$previousRuntimeTransactionBackup = Join-Path $staging 'runtime-previous-before'
$addinTransactionBackup = Join-Path $staging 'addin-before.esriAddInX'
$addinPreviousTransactionBackup = Join-Path $staging 'addin-previous-before.esriAddInX'
$runtimeBackedUp = $false
$newRuntimePlaced = $false
$installSucceeded = $false
$createdDataDirectories = [System.Collections.Generic.List[string]]::new()
try {
    New-Item -ItemType Directory -Path $stagedRuntime -Force | Out-Null
    foreach ($name in @('__init__.py', 'server.py', 'worker.py')) {
        Copy-Item -LiteralPath (Join-Path $sourceRoot "src\arcgis_mcp\$name") `
            -Destination (Join-Path $stagedRuntime $name) -Force
    }
    if ($addinExistedBefore) {
        Copy-Item -LiteralPath $installedAddinPackage -Destination $addinTransactionBackup -Force
    }
    if ($addinPreviousExistedBefore) {
        Copy-Item -LiteralPath $addinPreviousPackage -Destination $addinPreviousTransactionBackup -Force
    }
    if ($previousRuntimeExistedBefore) {
        Move-Item -LiteralPath $previousRuntime -Destination $previousRuntimeTransactionBackup
    }
    if ($runtimeExistedBefore) {
        Move-Item -LiteralPath $runtimeRoot -Destination $previousRuntime
        $runtimeBackedUp = $true
    }
    Move-Item -LiteralPath $stagedRuntime -Destination $runtimeRoot
    $newRuntimePlaced = $true

    New-Item -ItemType Directory -Path $configRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
    foreach ($path in @($settings['allowed_data_roots']) + @($settings['output_root'], $settings['live_bridge_queue'])) {
        $resolvedDataDirectory = [System.IO.Path]::GetFullPath([string]$path).TrimEnd('\')
        $cursor = $resolvedDataDirectory
        while ($cursor -and -not (Test-Path -LiteralPath $cursor)) {
            if (-not $createdDataDirectories.Contains($cursor)) {
                $createdDataDirectories.Add($cursor)
            }
            $parent = Split-Path -Parent $cursor
            if (-not $parent -or $parent -eq $cursor) { break }
            $cursor = $parent
        }
        New-Item -ItemType Directory -Path $resolvedDataDirectory -Force | Out-Null
    }
    [System.IO.File]::WriteAllText(
        $configPath,
        ($settings | ConvertTo-Json -Depth 8) + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false)
    )
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'mcp_probe.py') -Destination $probePath -Force

    if (-not $SkipAddin) {
        & (Join-Path $PSScriptRoot 'install-addin.ps1') `
            -ArcGISProInstallDir $ArcGISProInstallDir `
            -Package $selectedAddinPackage `
            -ExpectedVersion $version `
            -InstallDirectory $resolvedAddinDirectory `
            -InstalledPackagePath $installedAddinPackage `
            -SkipRegistration:$SkipAddinRegistration
        if ($LASTEXITCODE -ne 0) { throw "Add-in installation failed with exit code $LASTEXITCODE" }
        $selectedAddinInfo = Get-ArcGISBridgeAddinPackageInfo -Package $installedAddinPackage
    }

    $addinInstalled = $installedAddinPackage -and (Test-Path -LiteralPath $installedAddinPackage -PathType Leaf)
    $manifest = [ordered]@{
        product = 'ArcGIS Pro Bridge for Codex'
        version = $version
        installed_at_utc = [DateTime]::UtcNow.ToString('o')
        supported_product = 'ArcGIS Pro 3.6'
        python = $Python
        server = $serverPath
        config = $configPath
        addin_id = $addinId
        addin_installed = [bool]$addinInstalled
        addin_install_mode = $addinInstallMode
        addin_install_skipped_this_run = [bool]$SkipAddin
        addin_registration_skipped = [bool]($SkipAddin -or $SkipAddinRegistration)
        addin_directory = $resolvedAddinDirectory
        addin_package = $(if ($addinInstalled) { [System.IO.Path]::GetFullPath($installedAddinPackage) } else { '' })
        addin_version = $(if ($selectedAddinInfo) { [string]$selectedAddinInfo.version } else { '' })
        addin_sha256 = $(if ($selectedAddinInfo) { [string]$selectedAddinInfo.sha256 } else { '' })
    }
    [System.IO.File]::WriteAllText(
        $manifestPath,
        ($manifest | ConvertTo-Json -Depth 4) + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false)
    )
    if (-not $SkipGlobalRegistration) {
        Set-ArcGISBridgeGlobalMcpRegistration `
            -CodexHome $CodexHome `
            -Command $Python `
            -Server $serverPath `
            -Config $configPath `
            -WorkingDirectory $InstallRoot | Out-Null
    }
    Set-ArcGISBridgeInstallLocator -InstallRoot $InstallRoot -InstallLocatorPath $InstallLocatorPath | Out-Null
    $installSucceeded = $true
}
catch {
    $installError = $_
    if ($newRuntimePlaced) {
        if (Test-Path -LiteralPath $runtimeRoot) { Remove-Item -LiteralPath $runtimeRoot -Recurse -Force }
    }
    if ($runtimeBackedUp -and (Test-Path -LiteralPath $previousRuntime)) {
        Move-Item -LiteralPath $previousRuntime -Destination $runtimeRoot
    }
    if ($previousRuntimeExistedBefore -and
        (Test-Path -LiteralPath $previousRuntimeTransactionBackup -PathType Container)) {
        Move-Item -LiteralPath $previousRuntimeTransactionBackup -Destination $previousRuntime
    }
    if ($configExistedBefore) { [System.IO.File]::WriteAllBytes($configPath, $configBefore) }
    elseif (Test-Path -LiteralPath $configPath) { Remove-Item -LiteralPath $configPath -Force }
    if ($manifestExistedBefore) { [System.IO.File]::WriteAllBytes($manifestPath, $manifestBefore) }
    elseif (Test-Path -LiteralPath $manifestPath) { Remove-Item -LiteralPath $manifestPath -Force }
    if ($probeExistedBefore) { [System.IO.File]::WriteAllBytes($probePath, $probeBefore) }
    elseif (Test-Path -LiteralPath $probePath) { Remove-Item -LiteralPath $probePath -Force }
    if (-not $SkipGlobalRegistration) {
        if ($codexConfigExistedBefore) { [System.IO.File]::WriteAllBytes($codexConfigPath, $codexConfigBefore) }
        elseif (Test-Path -LiteralPath $codexConfigPath) { Remove-Item -LiteralPath $codexConfigPath -Force }
    }
    if ($locatorExistedBefore) { [System.IO.File]::WriteAllBytes($locatorPath, $locatorBefore) }
    elseif (Test-Path -LiteralPath $locatorPath) { Remove-Item -LiteralPath $locatorPath -Force }
    if (-not $SkipAddin -and $installedAddinPackage) {
        if ($addinExistedBefore -and (Test-Path -LiteralPath $addinTransactionBackup)) {
            Copy-Item -LiteralPath $addinTransactionBackup -Destination $installedAddinPackage -Force
        }
        elseif (Test-Path -LiteralPath $installedAddinPackage) {
            Remove-Item -LiteralPath $installedAddinPackage -Force
        }
        if ($addinPreviousExistedBefore -and (Test-Path -LiteralPath $addinPreviousTransactionBackup)) {
            Copy-Item -LiteralPath $addinPreviousTransactionBackup -Destination $addinPreviousPackage -Force
        }
        elseif ($addinPreviousPackage -and (Test-Path -LiteralPath $addinPreviousPackage)) {
            Remove-Item -LiteralPath $addinPreviousPackage -Force
        }
    }
    foreach ($createdDirectory in @($createdDataDirectories | Sort-Object Length -Descending)) {
        if ((Test-Path -LiteralPath $createdDirectory -PathType Container) -and
            -not (Get-ChildItem -LiteralPath $createdDirectory -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $createdDirectory -Force
        }
    }
    foreach ($directoryState in @(
        @{ Path = $toolsRoot; Existed = $toolsRootExistedBefore },
        @{ Path = $configRoot; Existed = $configRootExistedBefore },
        @{ Path = $resolvedAddinDirectory; Existed = $addinDirectoryExistedBefore },
        @{ Path = $locatorDirectory; Existed = $locatorDirectoryExistedBefore },
        @{ Path = $CodexHome; Existed = $codexHomeExistedBefore }
    )) {
        if (-not $directoryState.Existed -and
            (Test-Path -LiteralPath $directoryState.Path -PathType Container) -and
            -not (Get-ChildItem -LiteralPath $directoryState.Path -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $directoryState.Path -Force
        }
    }
    throw $installError
}
finally {
    if (Test-Path -LiteralPath $staging) {
        try { Remove-Item -LiteralPath $staging -Recurse -Force }
        catch { Write-Warning "Could not remove installer staging directory $staging`: $_" }
    }
    if (-not $installSucceeded -and -not $installRootExistedBefore -and
        (Test-Path -LiteralPath $InstallRoot -PathType Container) -and
        -not (Get-ChildItem -LiteralPath $InstallRoot -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $InstallRoot -Force
    }
}

Write-Output "[PASS] Installed ArcGIS Pro MCP bridge $version at $InstallRoot"
Write-Output "[PASS] Wrote Add-in install locator: $locatorPath"
if (-not $SkipGlobalRegistration) { Write-Output '[PASS] Registered global Codex MCP: arcgis-pro' }
if ($SkipAddin) { Write-Output "[INFO] Add-in installation skipped; compatible existing state: $addinInstallMode" }
else { Write-Output "[PASS] Installed the ArcGIS Pro Add-in ($addinInstallMode)." }
Write-Output 'Restart Codex after installation. Restart ArcGIS Pro when the Add-in changed.'
