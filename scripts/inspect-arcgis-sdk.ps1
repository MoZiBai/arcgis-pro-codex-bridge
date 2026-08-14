param(
    [Parameter(Mandatory = $true)]
    [string[]]$Pattern,
    [string]$MemberPattern = '',
    [string]$ArcGISProInstallDir = $(if ($env:ARCGIS_PRO_INSTALL_DIR) { $env:ARCGIS_PRO_INSTALL_DIR } else { Join-Path $env:ProgramFiles 'ArcGIS\Pro' }),
    [int]$MaxTypes = 200,
    [switch]$OnlyMatchingMembers
)

if ($PSVersionTable.PSEdition -ne 'Core') {
    $pwsh = 'C:\Program Files\PowerShell\7\pwsh.exe'
    if (-not (Test-Path -LiteralPath $pwsh)) {
        throw 'ArcGIS Pro 3.6 SDK reflection requires PowerShell 7 (.NET 8). Install PowerShell 7 or invoke this script from an existing pwsh session.'
    }
    $relaunchArgs = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $PSCommandPath,
        '-Pattern'
    ) + @($Pattern)
    if (-not [string]::IsNullOrWhiteSpace($MemberPattern)) {
        $relaunchArgs += @('-MemberPattern', $MemberPattern)
    }
    if ($PSBoundParameters.ContainsKey('ArcGISProInstallDir')) {
        $relaunchArgs += @('-ArcGISProInstallDir', $ArcGISProInstallDir)
    }
    if ($PSBoundParameters.ContainsKey('MaxTypes')) {
        $relaunchArgs += @('-MaxTypes', [string]$MaxTypes)
    }
    if ($OnlyMatchingMembers) {
        $relaunchArgs += '-OnlyMatchingMembers'
    }
    & $pwsh @relaunchArgs
    exit $LASTEXITCODE
}

$ErrorActionPreference = 'Stop'
$bin = Join-Path $ArcGISProInstallDir 'bin'
$extensionRoot = Join-Path $bin 'Extensions'
$searchDirectories = @($bin) + @(
    Get-ChildItem -LiteralPath $extensionRoot -Directory -Recurse |
        Select-Object -ExpandProperty FullName
)

[System.AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($sender, $eventArgs)
    $fileName = ([System.Reflection.AssemblyName]$eventArgs.Name).Name + '.dll'
    foreach ($directory in $searchDirectories) {
        $candidate = Join-Path $directory $fileName
        if (Test-Path -LiteralPath $candidate) {
            return [System.Reflection.Assembly]::LoadFrom($candidate)
        }
    }
    return $null
})

$assemblyPaths = @(
    (Join-Path $bin 'ArcGIS.Core.dll'),
    (Join-Path $bin 'ArcGIS.Desktop.Framework.dll'),
    (Join-Path $extensionRoot 'Core\ArcGIS.Desktop.Core.dll'),
    (Join-Path $extensionRoot 'Mapping\ArcGIS.Desktop.Mapping.dll'),
    (Join-Path $extensionRoot 'Layout\ArcGIS.Desktop.Layouts.dll'),
    (Join-Path $extensionRoot 'Catalog\ArcGIS.Desktop.Catalog.dll'),
    (Join-Path $extensionRoot 'Editing\ArcGIS.Desktop.Editing.dll'),
    (Join-Path $extensionRoot 'GeoProcessing\ArcGIS.Desktop.GeoProcessing.dll'),
    (Join-Path $extensionRoot 'Sharing\ArcGIS.Desktop.Sharing.dll'),
    (Join-Path $extensionRoot 'Charts\ArcGIS.Desktop.Charts.dll'),
    (Join-Path $extensionRoot 'DataEngineering\ArcGIS.Desktop.DataEngineering.dll'),
    (Join-Path $extensionRoot 'Metadata\ArcGIS.Desktop.Metadata.dll'),
    (Join-Path $extensionRoot 'DataSourcesRaster\ArcGIS.Desktop.DataSourcesRaster.dll'),
    (Join-Path $extensionRoot 'KnowledgeGraph\ArcGIS.Desktop.KnowledgeGraph.dll'),
    (Join-Path $extensionRoot 'TaskAssistant\ArcGIS.Desktop.TaskAssistant.dll'),
    (Join-Path $extensionRoot 'Workflow\ArcGIS.Desktop.Workflow.Client.dll')
)

function Get-LoadableTypes([System.Reflection.Assembly]$Assembly) {
    $types = @()
    try {
        $types += $Assembly.GetTypes()
    }
    catch [System.Reflection.ReflectionTypeLoadException] {
        $types += $_.Exception.Types
    }
    try {
        $types += $Assembly.GetExportedTypes()
    }
    catch [System.Reflection.ReflectionTypeLoadException] {
        $types += $_.Exception.Types
    }
    return $types | Where-Object { $null -ne $_ } | Sort-Object FullName -Unique
}

$assemblies = foreach ($path in $assemblyPaths) {
    if (Test-Path -LiteralPath $path) {
        [System.Reflection.Assembly]::LoadFrom($path)
    }
}

$regex = ($Pattern | ForEach-Object { "(?:$_)" }) -join '|'
$matches = foreach ($assembly in $assemblies) {
    Get-LoadableTypes $assembly |
        Where-Object {
            ($_.IsPublic -or $_.IsNestedPublic) -and
            $_.FullName -notmatch '(^|\.)Internal(\.|$)'
        } |
        ForEach-Object {
            $type = $_
            $methods = $type.GetMethods([System.Reflection.BindingFlags]'Public,Instance,Static') |
                Where-Object { -not $_.IsSpecialName } |
                ForEach-Object {
                    $parameters = $_.GetParameters() | ForEach-Object {
                        "$($_.ParameterType.FullName) $($_.Name)"
                    }
                    $scope = if ($_.IsStatic) { 'static ' } else { '' }
                    "$scope$($_.ReturnType.FullName) $($_.Name)($($parameters -join ', '))"
                } |
                Sort-Object -Unique
            $constructors = $type.GetConstructors([System.Reflection.BindingFlags]'Public,Instance') |
                ForEach-Object {
                    $parameters = $_.GetParameters() | ForEach-Object {
                        "$($_.ParameterType.FullName) $($_.Name)"
                    }
                    "$($type.FullName)($($parameters -join ', '))"
                } |
                Sort-Object -Unique
            $properties = $type.GetProperties([System.Reflection.BindingFlags]'Public,Instance,Static') |
                ForEach-Object {
                    $accessor = $_.GetMethod
                    $scope = if ($null -ne $accessor -and $accessor.IsStatic) { 'static ' } else { '' }
                    "$scope$($_.PropertyType.FullName) $($_.Name)"
                } |
                Sort-Object -Unique
            $typeMatches = $type.FullName -match $regex
            $memberMatches = -not [string]::IsNullOrWhiteSpace($MemberPattern) -and (
                ($methods -match $MemberPattern) -or ($properties -match $MemberPattern)
            )
            if (-not $typeMatches -and -not $memberMatches) {
                return
            }
            $outputMethods = $methods
            $outputProperties = $properties
            if ($OnlyMatchingMembers -and -not [string]::IsNullOrWhiteSpace($MemberPattern)) {
                $outputMethods = @($methods | Where-Object { $_ -match $MemberPattern })
                $outputProperties = @($properties | Where-Object { $_ -match $MemberPattern })
            }
            [pscustomobject][ordered]@{
                assembly = $assembly.GetName().Name
                type = $type.FullName
                base_type = $type.BaseType.FullName
                enum_values = if ($type.IsEnum) { @([Enum]::GetNames($type)) } else { @() }
                constructors = @($constructors)
                methods = @($outputMethods)
                properties = @($outputProperties)
            }
        }
}

$matches |
    Sort-Object -Property type -Unique |
    Select-Object -First $MaxTypes |
    ConvertTo-Json -Depth 6
