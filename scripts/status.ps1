param(
    [string]$InstallRoot = '',
    [string]$InstallLocatorPath = '',
    [string]$CodexHome = '',
    [string]$Python = '',
    [switch]$WithArcPy
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ArcGISBridge.Installation.psm1') -Force

$failures = [System.Collections.Generic.List[string]]::new()
function Pass([string]$Message) { Write-Output "[PASS] $Message" }
function Info([string]$Message) { Write-Output "[INFO] $Message" }
function Fail([string]$Message) {
    Write-Output "[FAIL] $Message"
    $script:failures.Add($Message)
}

$InstallRoot = Resolve-ArcGISBridgeInstallRoot `
    -InstallRoot $InstallRoot `
    -InstallLocatorPath $InstallLocatorPath
$CodexHome = Resolve-ArcGISBridgeCodexHome -CodexHome $CodexHome
$manifestPath = Join-Path $InstallRoot 'install.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    Fail "Installation manifest is missing: $manifestPath"
    exit 1
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
Pass "Installation manifest: $($manifest.version), $($manifest.supported_product)"
$locatedRoot = Resolve-ArcGISBridgeInstallRoot -InstallLocatorPath $InstallLocatorPath
if ($locatedRoot.Equals($InstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
    Pass "Add-in install locator resolves to: $InstallRoot"
}
else {
    Fail "Add-in install locator resolves to '$locatedRoot', expected '$InstallRoot'"
}

$serverPath = [string]$manifest.server
$configPath = [string]$manifest.config
foreach ($entry in @(
    @{ Label = 'MCP server'; Path = $serverPath },
    @{ Label = 'Private config'; Path = $configPath },
    @{ Label = 'MCP probe'; Path = (Join-Path $InstallRoot 'tools\mcp_probe.py') }
)) {
    if (Test-Path -LiteralPath $entry.Path) {
        Pass "$($entry.Label): $($entry.Path)"
    }
    else {
        Fail "$($entry.Label) is missing: $($entry.Path)"
    }
}

if (Test-ArcGISBridgeGlobalMcpRegistration -CodexHome $CodexHome) {
    Pass "Global Codex MCP registration exists in $CodexHome"
}
else {
    Fail "Global Codex MCP registration is missing from $CodexHome"
}

if (Test-Path -LiteralPath $configPath) {
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $arcgisPython = [string]$config.arcgis_python
    if (Test-Path -LiteralPath $arcgisPython) {
        Pass "ArcGIS Python exists: $arcgisPython"
    }
    else {
        Fail "ArcGIS Python is missing: $arcgisPython"
    }
    $arcgisRoot = if ($config.arcgis_pro_install_dir) { [string]$config.arcgis_pro_install_dir } else { '' }
    $arcgisExe = if ($arcgisRoot) { Join-Path $arcgisRoot 'bin\ArcGISPro.exe' } else { '' }
    if ($arcgisExe -and (Test-Path -LiteralPath $arcgisExe)) {
        $productVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($arcgisExe).ProductVersion
        if ($productVersion -match '^3\.6(?:\.|$)') {
            Pass "Supported ArcGIS Pro version detected: $productVersion"
        }
        else {
            Fail "Unsupported ArcGIS Pro version detected: $productVersion"
        }
    }
    else {
        Fail 'ArcGIS Pro executable could not be found from the installed config.'
    }
}

$addinRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) `
    ("ArcGIS\AddIns\ArcGISPro\" + [string]$manifest.addin_id)
$addinPackage = Get-ChildItem -LiteralPath $addinRoot -Filter '*.esriAddInX' -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($addinPackage) {
    Pass "ArcGIS Pro Add-in package is installed: $($addinPackage.FullName)"
}
elseif (($manifest.PSObject.Properties.Name -contains 'addin_installed') -and $manifest.addin_installed) {
    Fail "The installation manifest expects an Add-in, but no package was found under $addinRoot"
}
else {
    Info "Add-in installation was skipped; no package was found under $addinRoot"
}

if (-not $Python) {
    $Python = [string]$manifest.python
}
try {
    $Python = Resolve-ArcGISBridgePython -Python $Python
    $probe = Join-Path $InstallRoot 'tools\mcp_probe.py'
    if ((Test-Path -LiteralPath $probe) -and (Test-Path -LiteralPath $serverPath) -and (Test-Path -LiteralPath $configPath)) {
        $arguments = @($probe, '--python', $Python, '--server', $serverPath, '--config', $configPath)
        if ($WithArcPy) { $arguments += '--with-arcpy' }
        $probeOutput = & $Python @arguments 2>&1
        if ($LASTEXITCODE -eq 0) {
            Pass 'MCP initialize, server instructions, and required tool discovery succeeded.'
            if ($WithArcPy) { Pass 'ArcPy worker probe succeeded.' }
        }
        else {
            Fail ("MCP probe failed: " + ($probeOutput -join [Environment]::NewLine))
        }
    }
}
catch {
    Fail "Python or MCP probe failed: $_"
}

$projectConfig = Join-Path ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))) '.codex\config.toml'
if (Test-Path -LiteralPath $projectConfig) {
    $projectText = [System.IO.File]::ReadAllText($projectConfig)
    if ([regex]::IsMatch($projectText, '(?m)^\s*\[mcp_servers\.(?:arcgis-pro|"arcgis-pro")\]')) {
        Info "A project-scoped arcgis-pro override also exists: $projectConfig"
    }
}

if ($failures.Count -gt 0) {
    exit 1
}
exit 0
