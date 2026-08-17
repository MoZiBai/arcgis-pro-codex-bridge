Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-ArcGISBridgeCodexHome {
    param([string]$CodexHome = '')

    if (-not [string]::IsNullOrWhiteSpace($CodexHome)) {
        return [System.IO.Path]::GetFullPath($CodexHome)
    }
    if (-not [string]::IsNullOrWhiteSpace($env:CODEX_HOME)) {
        return [System.IO.Path]::GetFullPath($env:CODEX_HOME)
    }
    $profileRoot = [Environment]::GetFolderPath('UserProfile')
    if ([string]::IsNullOrWhiteSpace($profileRoot)) {
        throw 'Could not determine the current user profile. Pass -CodexHome explicitly.'
    }
    return Join-Path $profileRoot '.codex'
}

function Get-ArcGISBridgeInstallLocatorPath {
    param([string]$InstallLocatorPath = '')

    if (-not [string]::IsNullOrWhiteSpace($InstallLocatorPath)) {
        return [System.IO.Path]::GetFullPath($InstallLocatorPath)
    }
    return Join-Path $env:LOCALAPPDATA 'ArcGISCodexBridge\install-location.json'
}

function Resolve-ArcGISBridgeInstallRoot {
    param(
        [string]$InstallRoot = '',
        [string]$InstallLocatorPath = ''
    )

    if (-not [string]::IsNullOrWhiteSpace($InstallRoot)) {
        return [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ARCGIS_CODEX_BRIDGE_INSTALL_ROOT)) {
        return [System.IO.Path]::GetFullPath($env:ARCGIS_CODEX_BRIDGE_INSTALL_ROOT).TrimEnd('\')
    }
    $locatorPath = Get-ArcGISBridgeInstallLocatorPath -InstallLocatorPath $InstallLocatorPath
    if (Test-Path -LiteralPath $locatorPath -PathType Leaf) {
        try {
            $locator = Get-Content -LiteralPath $locatorPath -Raw | ConvertFrom-Json
            if ($locator.install_root) {
                return [System.IO.Path]::GetFullPath([string]$locator.install_root).TrimEnd('\')
            }
        }
        catch {
            Write-Verbose "Install locator could not be read: $_"
        }
    }
    return (Join-Path $env:LOCALAPPDATA 'ArcGISCodexBridge').TrimEnd('\')
}

function Set-ArcGISBridgeInstallLocator {
    param(
        [Parameter(Mandatory)][string]$InstallRoot,
        [string]$InstallLocatorPath = ''
    )

    $locatorPath = Get-ArcGISBridgeInstallLocatorPath -InstallLocatorPath $InstallLocatorPath
    $content = [ordered]@{ install_root = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') } |
        ConvertTo-Json -Compress
    Write-ArcGISBridgeAtomicText -Path $locatorPath -Content ($content + [Environment]::NewLine)
    return $locatorPath
}

function Remove-ArcGISBridgeInstallLocator {
    param(
        [Parameter(Mandatory)][string]$InstallRoot,
        [string]$InstallLocatorPath = ''
    )

    $locatorPath = Get-ArcGISBridgeInstallLocatorPath -InstallLocatorPath $InstallLocatorPath
    if (-not (Test-Path -LiteralPath $locatorPath -PathType Leaf)) {
        return $false
    }
    try {
        $locator = Get-Content -LiteralPath $locatorPath -Raw | ConvertFrom-Json
        $locatedRoot = [System.IO.Path]::GetFullPath([string]$locator.install_root).TrimEnd('\')
        $targetRoot = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
        if (-not $locatedRoot.Equals($targetRoot, [StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }
    catch {
        return $false
    }
    Remove-Item -LiteralPath $locatorPath -Force
    return $true
}

function ConvertTo-ArcGISBridgeTomlString {
    param([Parameter(Mandatory)][string]$Value)

    $escaped = $Value.Replace('\', '\\').Replace('"', '\"')
    $escaped = $escaped.Replace("`r", '\r').Replace("`n", '\n').Replace("`t", '\t')
    return '"' + $escaped + '"'
}

function Remove-ArcGISBridgeMcpSectionText {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [string]$ServerName = 'arcgis-pro'
    )

    $escaped = [regex]::Escape($ServerName)
    $header = '(?:' + $escaped + '|"' + $escaped + '")'
    $pattern = '(?ms)^[ \t]*\[mcp_servers\.' + $header + '(?:\.[^\]\r\n]+)?\][^\r\n]*(?:\r?\n|$).*?(?=^[ \t]*\[[^\]\r\n]+\][^\r\n]*(?:\r?\n|$)|\z)'
    $result = $Text
    while ([regex]::IsMatch($result, $pattern)) {
        $result = [regex]::Replace($result, $pattern, '', 1)
    }
    return $result
}

function Get-ArcGISBridgeMcpSectionText {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [string]$ServerName = 'arcgis-pro'
    )

    $escaped = [regex]::Escape($ServerName)
    $header = '(?:' + $escaped + '|"' + $escaped + '")'
    $pattern = '(?ms)^[ \t]*\[mcp_servers\.' + $header + '(?:\.[^\]\r\n]+)?\][^\r\n]*(?:\r?\n|$).*?(?=^[ \t]*\[[^\]\r\n]+\][^\r\n]*(?:\r?\n|$)|\z)'
    $match = [regex]::Match($Text, $pattern)
    return $(if ($match.Success) { $match.Value } else { '' })
}

function Write-ArcGISBridgeAtomicText {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Content
    )

    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $temporary = Join-Path $directory ('.arcgis-bridge-' + [guid]::NewGuid().ToString('N') + '.tmp')
    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($temporary, $Content, $encoding)
    try {
        if (Test-Path -LiteralPath $Path) {
            $timestamp = Get-Date -Format 'yyyyMMddHHmmss'
            $backup = "$Path.arcgis-pro.$timestamp.backup"
            [System.IO.File]::Replace($temporary, $Path, $backup, $true)
        }
        else {
            [System.IO.File]::Move($temporary, $Path)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Set-ArcGISBridgeGlobalMcpRegistration {
    param(
        [Parameter(Mandatory)][string]$CodexHome,
        [Parameter(Mandatory)][string]$Command,
        [Parameter(Mandatory)][string]$Server,
        [Parameter(Mandatory)][string]$Config,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [int]$StartupTimeoutSeconds = 30,
        [int]$ToolTimeoutSeconds = 1800,
        [ValidateSet('auto', 'prompt', 'writes', 'approve')]
        [string]$ApprovalMode = 'auto'
    )

    $resolvedHome = Resolve-ArcGISBridgeCodexHome -CodexHome $CodexHome
    $configPath = Join-Path $resolvedHome 'config.toml'
    $original = if (Test-Path -LiteralPath $configPath) {
        [System.IO.File]::ReadAllText($configPath)
    }
    else {
        ''
    }
    $withoutServer = Remove-ArcGISBridgeMcpSectionText -Text $original
    $newline = if ($original.Contains("`r`n")) { "`r`n" } else { "`n" }
    $prefix = $withoutServer.TrimEnd("`r", "`n")
    if ($prefix.Length -gt 0) {
        $prefix += $newline + $newline
    }
    $lines = @(
        '[mcp_servers.arcgis-pro]'
        ('command = ' + (ConvertTo-ArcGISBridgeTomlString $Command))
        'args = ['
        ('  ' + (ConvertTo-ArcGISBridgeTomlString $Server) + ',')
        '  "--config",'
        ('  ' + (ConvertTo-ArcGISBridgeTomlString $Config) + ',')
        ']'
        ('cwd = ' + (ConvertTo-ArcGISBridgeTomlString $WorkingDirectory))
        "startup_timeout_sec = $StartupTimeoutSeconds"
        "tool_timeout_sec = $ToolTimeoutSeconds"
        'enabled = true'
        'required = false'
        ('default_tools_approval_mode = ' + (ConvertTo-ArcGISBridgeTomlString $ApprovalMode))
    )
    $updated = $prefix + ($lines -join $newline) + $newline
    if ($updated -ne $original) {
        Write-ArcGISBridgeAtomicText -Path $configPath -Content $updated
    }
    return $configPath
}

function Remove-ArcGISBridgeGlobalMcpRegistration {
    param(
        [Parameter(Mandatory)][string]$CodexHome,
        [string]$Command = '',
        [string]$Server = '',
        [string]$Config = '',
        [string]$WorkingDirectory = ''
    )

    $resolvedHome = Resolve-ArcGISBridgeCodexHome -CodexHome $CodexHome
    $configPath = Join-Path $resolvedHome 'config.toml'
    if (-not (Test-Path -LiteralPath $configPath)) {
        return $false
    }
    $original = [System.IO.File]::ReadAllText($configPath)
    if (-not (Test-ArcGISBridgeGlobalMcpRegistration `
        -CodexHome $resolvedHome `
        -Command $Command `
        -Server $Server `
        -Config $Config `
        -WorkingDirectory $WorkingDirectory)) {
        return $false
    }
    $updated = Remove-ArcGISBridgeMcpSectionText -Text $original
    if ($updated -eq $original) {
        return $false
    }
    Write-ArcGISBridgeAtomicText -Path $configPath -Content $updated.TrimEnd("`r", "`n")
    return $true
}

function Test-ArcGISBridgeGlobalMcpRegistration {
    param(
        [Parameter(Mandatory)][string]$CodexHome,
        [string]$Command = '',
        [string]$Server = '',
        [string]$Config = '',
        [string]$WorkingDirectory = ''
    )

    $resolvedHome = Resolve-ArcGISBridgeCodexHome -CodexHome $CodexHome
    $configPath = Join-Path $resolvedHome 'config.toml'
    if (-not (Test-Path -LiteralPath $configPath)) {
        return $false
    }
    $text = [System.IO.File]::ReadAllText($configPath)
    $section = Get-ArcGISBridgeMcpSectionText -Text $text
    if ([string]::IsNullOrWhiteSpace($section)) {
        return $false
    }
    $lineExpectations = @(
        @{ Name = 'command'; Value = $Command; Indented = $false; Comma = $false },
        @{ Name = 'server'; Value = $Server; Indented = $true; Comma = $true },
        @{ Name = 'config'; Value = $Config; Indented = $true; Comma = $true },
        @{ Name = 'cwd'; Value = $WorkingDirectory; Indented = $false; Comma = $false }
    )
    foreach ($expectation in $lineExpectations) {
        if ([string]::IsNullOrWhiteSpace([string]$expectation.Value)) {
            continue
        }
        $toml = [regex]::Escape((ConvertTo-ArcGISBridgeTomlString ([string]$expectation.Value)))
        $pattern = if ($expectation.Name -in @('command', 'cwd')) {
            '(?m)^\s*' + $expectation.Name + '\s*=\s*' + $toml + '\s*$'
        }
        else {
            '(?m)^\s*' + $toml + ',?\s*$'
        }
        if (-not [regex]::IsMatch($section, $pattern)) {
            return $false
        }
    }
    return $true
}

function Get-ArcGISBridgeAddinPackageInfo {
    param(
        [Parameter(Mandatory)][string]$Package,
        [switch]$AllowBackupPackage
    )

    $resolvedPackage = [System.IO.Path]::GetFullPath($Package)
    if (-not (Test-Path -LiteralPath $resolvedPackage -PathType Leaf)) {
        throw "Add-in package was not found: $resolvedPackage"
    }
    $isAddinPackage = [System.IO.Path]::GetExtension($resolvedPackage) -ieq '.esriAddinX'
    $isBackupPackage = $AllowBackupPackage -and $resolvedPackage.EndsWith(
        '.esriAddinX.previous',
        [StringComparison]::OrdinalIgnoreCase
    )
    if (-not $isAddinPackage -and -not $isBackupPackage) {
        throw "Expected an .esriAddinX package: $resolvedPackage"
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($resolvedPackage)
    try {
        $entry = $archive.GetEntry('Config.daml')
        if (-not $entry) {
            throw 'Config.daml was not found in the Add-in package.'
        }
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { $configText = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally {
        $archive.Dispose()
    }
    try { $configXml = [xml]$configText }
    catch { throw "Config.daml is not valid XML: $_" }
    $addInInfo = $configXml.SelectSingleNode('//*[local-name()="AddInInfo"]')
    $addInId = if ($addInInfo) { [string]$addInInfo.GetAttribute('id') } else { '' }
    $addInVersion = if ($addInInfo) { [string]$addInInfo.GetAttribute('version') } else { '' }
    if ([string]::IsNullOrWhiteSpace($addInId) -or
        [string]::IsNullOrWhiteSpace($addInVersion)) {
        throw 'Could not determine Add-in ID and version from Config.daml.'
    }
    return [pscustomobject]@{
        id = $addInId
        version = $addInVersion
        package = $resolvedPackage
        sha256 = (Get-FileHash -LiteralPath $resolvedPackage -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Test-ArcGISBridgePathWithinDirectory {
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$Path,
        [switch]$AllowDirectoryItself
    )

    $resolvedDirectory = [System.IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    $resolvedPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if ($resolvedPath.Equals($resolvedDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        return [bool]$AllowDirectoryItself
    }
    return $resolvedPath.StartsWith(
        $resolvedDirectory + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase
    )
}

function Set-ArcGISBridgePrivatePathAcl {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$Container
    )

    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Private ACL configuration is supported on Windows only.'
    }
    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved)) {
        throw "Cannot secure a missing path: $resolved"
    }
    $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    if (-not $currentSid) {
        throw 'Could not resolve the current Windows user SID.'
    }
    $security = if ($Container) {
        [System.Security.AccessControl.DirectorySecurity]::new()
    }
    else {
        [System.Security.AccessControl.FileSecurity]::new()
    }
    $security.SetOwner($currentSid)
    $security.SetAccessRuleProtection($true, $false)
    $inheritance = if ($Container) {
        [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
            [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
    }
    else {
        [System.Security.AccessControl.InheritanceFlags]::None
    }
    $sids = @(
        $currentSid,
        [System.Security.Principal.SecurityIdentifier]::new(
            [System.Security.Principal.WellKnownSidType]::LocalSystemSid, $null),
        [System.Security.Principal.SecurityIdentifier]::new(
            [System.Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
    )
    foreach ($sid in $sids) {
        $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [System.Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Allow)
        [void]$security.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $resolved -AclObject $security
    return $resolved
}

function Test-ArcGISBridgePrivatePathAcl {
    param([Parameter(Mandatory)][string]$Path)

    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or
        -not (Test-Path -LiteralPath $Path)) {
        return $false
    }
    $acl = Get-Acl -LiteralPath $Path
    if (-not $acl.AreAccessRulesProtected) {
        return $false
    }
    $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $blockedSids = @('S-1-1-0', 'S-1-5-11', 'S-1-5-32-545')
    $currentUserAllowed = $false
    foreach ($rule in $acl.Access) {
        $sid = try {
            $rule.IdentityReference.Translate(
                [System.Security.Principal.SecurityIdentifier]).Value
        }
        catch { [string]$rule.IdentityReference }
        if ($rule.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Allow) {
            if ($blockedSids -contains $sid) {
                return $false
            }
            if ($sid -eq $currentSid -and
                ($rule.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::FullControl)) {
                $currentUserAllowed = $true
            }
        }
    }
    return $currentUserAllowed
}

function Resolve-ArcGISBridgePython {
    param([string]$Python = '')

    $candidate = if ([string]::IsNullOrWhiteSpace($Python)) {
        (Get-Command python -ErrorAction Stop).Source
    }
    elseif (Test-Path -LiteralPath $Python) {
        (Resolve-Path -LiteralPath $Python).Path
    }
    else {
        (Get-Command $Python -ErrorAction Stop).Source
    }
    $versionText = (& $candidate -c "import sys; print('.'.join(map(str, sys.version_info[:3])))").Trim()
    $parsed = [version]'0.0'
    if ($LASTEXITCODE -ne 0 -or -not [version]::TryParse($versionText, [ref]$parsed)) {
        throw "Could not determine Python version for $candidate"
    }
    if ($parsed -lt [version]'3.10') {
        throw "Python 3.10 or newer is required; found $versionText at $candidate"
    }
    return $candidate
}

function Resolve-ArcGISBridgeProInstall {
    param(
        [string]$ArcGISProInstallDir = '',
        [string]$ExistingConfig = ''
    )

    $candidates = [System.Collections.Generic.List[string]]::new()
    foreach ($value in @($ArcGISProInstallDir, $env:ARCGIS_PRO_INSTALL_DIR)) {
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            $candidates.Add($value)
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($ExistingConfig) -and (Test-Path -LiteralPath $ExistingConfig)) {
        try {
            $existing = Get-Content -LiteralPath $ExistingConfig -Raw | ConvertFrom-Json
            if ($existing.arcgis_python) {
                $pythonPath = [System.IO.Path]::GetFullPath([string]$existing.arcgis_python)
                $cursor = Split-Path -Parent $pythonPath
                while ($cursor) {
                    if (Test-Path -LiteralPath (Join-Path $cursor 'bin\ArcGISPro.exe')) {
                        $candidates.Add($cursor)
                        break
                    }
                    $parent = Split-Path -Parent $cursor
                    if ($parent -eq $cursor) { break }
                    $cursor = $parent
                }
            }
        }
        catch {
            Write-Verbose "Existing config could not be used for ArcGIS discovery: $_"
        }
    }
    foreach ($registryPath in @(
        'HKLM:\SOFTWARE\ESRI\ArcGISPro',
        'HKLM:\SOFTWARE\WOW6432Node\ESRI\ArcGISPro'
    )) {
        try {
            $installDir = (Get-ItemProperty -LiteralPath $registryPath -ErrorAction Stop).InstallDir
            if ($installDir) { $candidates.Add([string]$installDir) }
        }
        catch { }
    }
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($base) { $candidates.Add((Join-Path $base 'ArcGIS\Pro')) }
    }
    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        $resolved = [System.IO.Path]::GetFullPath($candidate).TrimEnd('\')
        if (Test-Path -LiteralPath (Join-Path $resolved 'bin\ArcGISPro.exe')) {
            return $resolved
        }
    }
    throw 'ArcGIS Pro was not found. Pass -ArcGISProInstallDir or set ARCGIS_PRO_INSTALL_DIR.'
}

function Assert-ArcGISBridgeSafeInstallRoot {
    param([Parameter(Mandatory)][string]$InstallRoot)

    $resolved = [System.IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
    $forbidden = @(
        [System.IO.Path]::GetPathRoot($resolved).TrimEnd('\'),
        [Environment]::GetFolderPath('UserProfile').TrimEnd('\'),
        (Resolve-ArcGISBridgeCodexHome).TrimEnd('\')
    )
    if ($forbidden -contains $resolved) {
        throw "Refusing to use broad or protected install root: $resolved"
    }
    return $resolved
}

Export-ModuleMember -Function @(
    'Resolve-ArcGISBridgeCodexHome',
    'Get-ArcGISBridgeInstallLocatorPath',
    'Resolve-ArcGISBridgeInstallRoot',
    'Set-ArcGISBridgeInstallLocator',
    'Remove-ArcGISBridgeInstallLocator',
    'Set-ArcGISBridgeGlobalMcpRegistration',
    'Remove-ArcGISBridgeGlobalMcpRegistration',
    'Test-ArcGISBridgeGlobalMcpRegistration',
    'Get-ArcGISBridgeAddinPackageInfo',
    'Test-ArcGISBridgePathWithinDirectory',
    'Set-ArcGISBridgePrivatePathAcl',
    'Test-ArcGISBridgePrivatePathAcl',
    'Resolve-ArcGISBridgePython',
    'Resolve-ArcGISBridgeProInstall',
    'Assert-ArcGISBridgeSafeInstallRoot'
)
