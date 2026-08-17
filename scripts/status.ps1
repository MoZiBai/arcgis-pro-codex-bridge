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

if (Test-ArcGISBridgeGlobalMcpRegistration `
    -CodexHome $CodexHome `
    -Command ([string]$manifest.python) `
    -Server $serverPath `
    -Config $configPath `
    -WorkingDirectory $InstallRoot) {
    Pass "Global Codex MCP registration exactly matches $InstallRoot"
}
else {
    Fail "Global Codex MCP registration is missing or points to a different installation under $CodexHome"
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
    $signingKeyPath = if (($config.PSObject.Properties.Name -contains 'live_bridge_hmac_key_file') -and
        $config.live_bridge_hmac_key_file) {
        [System.IO.Path]::GetFullPath([string]$config.live_bridge_hmac_key_file)
    }
    else { '' }
    if (-not $signingKeyPath -or -not (Test-Path -LiteralPath $signingKeyPath -PathType Leaf)) {
        Fail 'Live bridge signing key is missing from the private configuration.'
    }
    else {
        try {
            $signingKey = [Convert]::FromBase64String(
                [System.IO.File]::ReadAllText($signingKeyPath).Trim())
            if ($signingKey.Length -lt 32) {
                Fail 'Live bridge signing key contains fewer than 32 bytes.'
            }
            elseif (-not (Test-ArcGISBridgePrivatePathAcl -Path $signingKeyPath)) {
                Fail "Live bridge signing key ACL is not private: $signingKeyPath"
            }
            else {
                Pass 'Live bridge signing key exists and has a private Windows ACL.'
            }
        }
        catch {
            Fail "Live bridge signing key is invalid: $_"
        }
    }
    $queuePath = if (($config.PSObject.Properties.Name -contains 'live_bridge_queue') -and
        $config.live_bridge_queue) {
        [System.IO.Path]::GetFullPath([string]$config.live_bridge_queue)
    }
    else { '' }
    if (-not $queuePath -or -not (Test-ArcGISBridgePrivatePathAcl -Path $queuePath)) {
        Fail "Live bridge queue ACL is missing or not private: $queuePath"
    }
    else {
        Pass 'Live bridge queue has a private Windows ACL.'
    }
}

$addinRoot = if ($manifest.PSObject.Properties.Name -contains 'addin_directory' -and $manifest.addin_directory) {
    [System.IO.Path]::GetFullPath([string]$manifest.addin_directory)
}
else {
    Join-Path ([Environment]::GetFolderPath('MyDocuments')) `
        ("ArcGIS\AddIns\ArcGISPro\" + [string]$manifest.addin_id)
}
$addinPackage = if ($manifest.PSObject.Properties.Name -contains 'addin_package' -and $manifest.addin_package) {
    [System.IO.Path]::GetFullPath([string]$manifest.addin_package)
}
else {
    Get-ChildItem -LiteralPath $addinRoot -Filter '*.esriAddInX' -File -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
$addinPathIsOwned = $true
if ($addinPackage) {
    if (-not (Test-ArcGISBridgePathWithinDirectory `
        -Directory $addinRoot `
        -Path $addinPackage)) {
        Fail "Recorded Add-in package is outside its owned directory: $addinPackage"
        $addinPathIsOwned = $false
    }
}
if ($addinPathIsOwned -and $addinPackage -and (Test-Path -LiteralPath $addinPackage -PathType Leaf)) {
    try {
        $addinInfo = Get-ArcGISBridgeAddinPackageInfo -Package $addinPackage
        if ($addinInfo.id -ne [string]$manifest.addin_id) {
            Fail "Installed Add-in ID $($addinInfo.id) does not match $($manifest.addin_id)"
        }
        elseif ($addinInfo.version -ne [string]$manifest.version) {
            Fail "Installed Add-in version $($addinInfo.version) does not match MCP $($manifest.version)"
        }
        elseif (($manifest.PSObject.Properties.Name -contains 'addin_version') -and
            $manifest.addin_version -and $addinInfo.version -ne [string]$manifest.addin_version) {
            Fail "Installed Add-in version does not match the installation manifest: $addinPackage"
        }
        elseif (($manifest.PSObject.Properties.Name -contains 'addin_sha256') -and
            $manifest.addin_sha256 -and $addinInfo.sha256 -ne [string]$manifest.addin_sha256) {
            Fail "Installed Add-in SHA-256 does not match the installation manifest: $addinPackage"
        }
        else {
            Pass "ArcGIS Pro Add-in identity, version, and SHA-256 are valid: $addinPackage"
        }
    }
    catch {
        Fail "ArcGIS Pro Add-in package validation failed: $_"
    }
}
elseif ($addinPathIsOwned -and
    ($manifest.PSObject.Properties.Name -contains 'addin_installed') -and $manifest.addin_installed) {
    Fail "The installation manifest expects an Add-in, but no package was found: $addinPackage"
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
