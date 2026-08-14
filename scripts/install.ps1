[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'ArcGISCodexBridge'),
    [string]$CodexHome = '',
    [string]$Python = '',
    [string]$ArcGISProInstallDir = '',
    [string]$MSBuild = '',
    [string]$ImportConfig = '',
    [string[]]$AllowedDataRoot = @(),
    [string]$OutputRoot = '',
    [string]$QueueRoot = '',
    [switch]$SkipAddin,
    [switch]$SkipGlobalRegistration
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Import-Module (Join-Path $PSScriptRoot 'ArcGISBridge.Installation.psm1') -Force

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

if (-not $SkipAddin) {
    & (Join-Path $PSScriptRoot 'build-addin.ps1') `
        -ArcGISProInstallDir $ArcGISProInstallDir `
        -MSBuild $MSBuild
    if ($LASTEXITCODE -ne 0) { throw "Add-in build failed with exit code $LASTEXITCODE" }
}

New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
$staging = Join-Path $InstallRoot ('.staging-' + [guid]::NewGuid().ToString('N'))
$stagedRuntime = Join-Path $staging 'runtime'
New-Item -ItemType Directory -Path $stagedRuntime -Force | Out-Null
try {
    foreach ($name in @('__init__.py', 'server.py', 'worker.py')) {
        Copy-Item -LiteralPath (Join-Path $sourceRoot "src\arcgis_mcp\$name") `
            -Destination (Join-Path $stagedRuntime $name) -Force
    }

    $previousRuntime = Join-Path $InstallRoot 'runtime.previous'
    if (Test-Path -LiteralPath $previousRuntime) {
        Remove-Item -LiteralPath $previousRuntime -Recurse -Force
    }
    if (Test-Path -LiteralPath $runtimeRoot) {
        Move-Item -LiteralPath $runtimeRoot -Destination $previousRuntime
    }
    try {
        Move-Item -LiteralPath $stagedRuntime -Destination $runtimeRoot
    }
    catch {
        if ((Test-Path -LiteralPath $previousRuntime) -and -not (Test-Path -LiteralPath $runtimeRoot)) {
            Move-Item -LiteralPath $previousRuntime -Destination $runtimeRoot
        }
        throw
    }

    New-Item -ItemType Directory -Path $configRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
    foreach ($path in @($settings['allowed_data_roots']) + @($settings['output_root'], $settings['live_bridge_queue'])) {
        New-Item -ItemType Directory -Path ([string]$path) -Force | Out-Null
    }
    $json = $settings | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText(
        $configPath,
        $json + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false)
    )
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'mcp_probe.py') `
        -Destination (Join-Path $toolsRoot 'mcp_probe.py') -Force

    $damlText = Get-Content -LiteralPath (Join-Path $sourceRoot 'addin\ArcGISCodexBridgeAddin\Config.daml') -Raw
    $addinIdMatch = [regex]::Match($damlText, '<AddInInfo\s+id="([^"]+)"')
    if (-not $addinIdMatch.Success) {
        throw 'Could not determine the Add-in ID from Config.daml.'
    }
    $manifest = [ordered]@{
        product = 'ArcGIS Pro Bridge for Codex'
        version = $version
        installed_at_utc = [DateTime]::UtcNow.ToString('o')
        supported_product = 'ArcGIS Pro 3.6'
        python = $Python
        server = $serverPath
        config = $configPath
        addin_id = $addinIdMatch.Groups[1].Value
        addin_installed = (-not $SkipAddin)
        source = $sourceRoot
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $InstallRoot 'install.json'),
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

    if (-not $SkipAddin) {
        & (Join-Path $PSScriptRoot 'install-addin.ps1') -ArcGISProInstallDir $ArcGISProInstallDir
        if ($LASTEXITCODE -ne 0) { throw "Add-in installation failed with exit code $LASTEXITCODE" }
    }
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}

Write-Output "[PASS] Installed ArcGIS Pro MCP bridge $version at $InstallRoot"
if (-not $SkipGlobalRegistration) {
    Write-Output "[PASS] Registered global Codex MCP: arcgis-pro"
}
if ($SkipAddin) {
    Write-Output '[INFO] Add-in build and installation were skipped.'
}
else {
    Write-Output '[PASS] Built and installed the ArcGIS Pro Add-in.'
}
Write-Output 'Restart Codex after installation. Restart ArcGIS Pro when the Add-in changed.'
