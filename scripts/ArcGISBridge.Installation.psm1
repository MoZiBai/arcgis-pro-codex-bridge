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
    param([Parameter(Mandatory)][string]$CodexHome)

    $resolvedHome = Resolve-ArcGISBridgeCodexHome -CodexHome $CodexHome
    $configPath = Join-Path $resolvedHome 'config.toml'
    if (-not (Test-Path -LiteralPath $configPath)) {
        return $false
    }
    $original = [System.IO.File]::ReadAllText($configPath)
    $updated = Remove-ArcGISBridgeMcpSectionText -Text $original
    if ($updated -eq $original) {
        return $false
    }
    Write-ArcGISBridgeAtomicText -Path $configPath -Content $updated.TrimEnd("`r", "`n")
    return $true
}

function Test-ArcGISBridgeGlobalMcpRegistration {
    param([Parameter(Mandatory)][string]$CodexHome)

    $resolvedHome = Resolve-ArcGISBridgeCodexHome -CodexHome $CodexHome
    $configPath = Join-Path $resolvedHome 'config.toml'
    if (-not (Test-Path -LiteralPath $configPath)) {
        return $false
    }
    $text = [System.IO.File]::ReadAllText($configPath)
    return [regex]::IsMatch(
        $text,
        '(?m)^[ \t]*\[mcp_servers\.(?:arcgis-pro|"arcgis-pro")\][^\r\n]*$'
    )
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
    'Set-ArcGISBridgeGlobalMcpRegistration',
    'Remove-ArcGISBridgeGlobalMcpRegistration',
    'Test-ArcGISBridgeGlobalMcpRegistration',
    'Resolve-ArcGISBridgePython',
    'Resolve-ArcGISBridgeProInstall',
    'Assert-ArcGISBridgeSafeInstallRoot'
)
