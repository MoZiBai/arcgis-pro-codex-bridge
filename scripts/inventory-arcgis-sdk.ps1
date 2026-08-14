param(
    [string]$ArcGISProInstallDir = $(if ($env:ARCGIS_PRO_INSTALL_DIR) { $env:ARCGIS_PRO_INSTALL_DIR } else { Join-Path $env:ProgramFiles 'ArcGIS\Pro' })
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
        '-ArcGISProInstallDir', $ArcGISProInstallDir
    )
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

$assemblies = foreach ($path in $assemblyPaths) {
    if (Test-Path -LiteralPath $path) {
        [System.Reflection.Assembly]::LoadFrom($path)
    }
}

function Get-LoadableTypes([System.Reflection.Assembly]$Assembly) {
    $types = @()
    try {
        $types += $Assembly.GetTypes()
    }
    catch [System.Reflection.ReflectionTypeLoadException] {
        $types += $_.Exception.Types | Where-Object { $null -ne $_ }
    }
    try {
        # GetExportedTypes also resolves public types exposed through facade
        # assemblies on the Windows PowerShell runtime used by ArcGIS Pro.
        $types += $Assembly.GetExportedTypes()
    }
    catch [System.Reflection.ReflectionTypeLoadException] {
        $types += $_.Exception.Types | Where-Object { $null -ne $_ }
    }
    return $types | Where-Object { $null -ne $_ } | Sort-Object FullName -Unique
}

$assemblySummary = foreach ($assembly in $assemblies) {
    $types = @(
        Get-LoadableTypes $assembly |
            Where-Object {
                ($_.IsPublic -or $_.IsNestedPublic) -and
                $_.FullName -notmatch '(^|\.)Internal(\.|$)'
            }
    )
    $namespaces = $types |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_.Namespace) } |
        Group-Object Namespace |
        Sort-Object -Property @{Expression='Count';Descending=$true}, @{Expression='Name';Descending=$false} |
        Select-Object -First 20 @{Name='namespace';Expression={$_.Name}}, Count
    $entryPoints = $types |
        Where-Object {
            $_.Name -match '(Factory|Manager|Operation|ProjectItem|Definition|Exporter|Importer|Builder|Tool)$'
        } |
        Sort-Object FullName |
        Select-Object -First 80 -ExpandProperty FullName
    [ordered]@{
        assembly = $assembly.GetName().Name
        version = $assembly.GetName().Version.ToString()
        public_type_count = $types.Count
        top_namespaces = @($namespaces)
        entry_point_types = @($entryPoints)
    }
}

$keyTypeNames = @(
    'ArcGIS.Desktop.Core.Project',
    'ArcGIS.Desktop.Core.ItemFactory',
    'ArcGIS.Desktop.Core.FavoritesManager',
    'ArcGIS.Desktop.Catalog.FolderConnectionProjectItem',
    'ArcGIS.Desktop.Catalog.GDBProjectItem',
    'ArcGIS.Desktop.Catalog.ServerConnectionManager',
    'ArcGIS.Desktop.Catalog.ServerConnectionProjectItem',
    'ArcGIS.Desktop.Mapping.StyleProjectItem',
    'ArcGIS.Desktop.Mapping.MapFactory',
    'ArcGIS.Desktop.Mapping.Map',
    'ArcGIS.Desktop.Mapping.MapView',
    'ArcGIS.Desktop.Mapping.LayerFactory',
    'ArcGIS.Desktop.Mapping.FeatureLayer',
    'ArcGIS.Desktop.Mapping.RasterLayer',
    'ArcGIS.Desktop.Mapping.SelectionSet',
    'ArcGIS.Desktop.Mapping.Bookmark',
    'ArcGIS.Desktop.Mapping.Camera',
    'ArcGIS.Desktop.Mapping.StandaloneTable',
    'ArcGIS.Desktop.Mapping.StandaloneTableFactory',
    'ArcGIS.Desktop.Mapping.FieldDescription',
    'ArcGIS.Desktop.Mapping.TimeParameters',
    'ArcGIS.Desktop.Mapping.DefinitionQuery',
    'ArcGIS.Core.CIM.CIMFeatureLayer',
    'ArcGIS.Core.CIM.CIMDisplayFilter',
    'ArcGIS.Core.CIM.CIMDisplayFilterGroup',
    'ArcGIS.Core.CIM.CIMChart',
    'ArcGIS.Core.Data.Table',
    'ArcGIS.Core.Data.TableDefinition',
    'ArcGIS.Core.Data.RelationshipClass',
    'ArcGIS.Core.Data.RelationshipClassDefinition',
    'ArcGIS.Core.Data.AttributedRelationshipClass',
    'ArcGIS.Core.Data.AttributedRelationshipClassDefinition',
    'ArcGIS.Core.Data.QueryFilter',
    'ArcGIS.Core.Data.SpatialQueryFilter',
    'ArcGIS.Core.Data.RowCursor',
    'ArcGIS.Core.Data.Row',
    'ArcGIS.Core.Data.Feature',
    'ArcGIS.Core.Data.Field',
    'ArcGIS.Desktop.Editing.EditOperation',
    'ArcGIS.Desktop.Layouts.LayoutFactory',
    'ArcGIS.Desktop.Layouts.LayoutProjectItem',
    'ArcGIS.Desktop.Layouts.ElementFactory',
    'ArcGIS.Desktop.Layouts.Layout',
    'ArcGIS.Desktop.Layouts.MapFrame',
    'ArcGIS.Desktop.Layouts.MapSeries',
    'ArcGIS.Desktop.Reports.ReportFactory',
    'ArcGIS.Desktop.Reports.Report',
    'ArcGIS.Desktop.Reports.ReportProjectItem',
    'ArcGIS.Desktop.Presentations.PresentationFactory',
    'ArcGIS.Desktop.Presentations.PresentationProjectItem',
    'ArcGIS.Desktop.Presentations.Presentation',
    'ArcGIS.Desktop.Presentations.PresentationPageFactory',
    'ArcGIS.Desktop.Layouts.PNGFormat',
    'ArcGIS.Desktop.Layouts.JPEGFormat',
    'ArcGIS.Desktop.Layouts.TIFFFormat',
    'ArcGIS.Desktop.Layouts.SVGFormat',
    'ArcGIS.Desktop.Layouts.PDFFormat',
    'ArcGIS.Desktop.Core.Geoprocessing.Geoprocessing'
)

$keyTypes = foreach ($typeName in $keyTypeNames) {
    $type = $null
    foreach ($assembly in $assemblies) {
        $candidate = Get-LoadableTypes $assembly |
            Where-Object { $_.FullName -eq $typeName } |
            Select-Object -First 1
        if ($null -ne $candidate) {
            $type = $candidate
            break
        }
    }
    if ($null -eq $type) { continue }
    $methods = $type.GetMethods([System.Reflection.BindingFlags]'Public,Instance,Static') |
        Where-Object { -not $_.IsSpecialName } |
        ForEach-Object {
            $parameters = $_.GetParameters() | ForEach-Object {
                "$($_.ParameterType.Name) $($_.Name)"
            }
            $scope = if ($_.IsStatic) { 'static ' } else { '' }
            "$scope$($_.ReturnType.Name) $($_.Name)($($parameters -join ', '))"
        } |
        Sort-Object -Unique
    $properties = $type.GetProperties([System.Reflection.BindingFlags]'Public,Instance,Static') |
        ForEach-Object {
            $accessor = $_.GetMethod
            $scope = if ($null -ne $accessor -and $accessor.IsStatic) { 'static ' } else { '' }
            "$scope$($_.PropertyType.Name) $($_.Name)"
        } |
        Sort-Object -Unique
    [ordered]@{
        type = $type.FullName
        methods = @($methods)
        properties = @($properties)
    }
}

[ordered]@{
    arcgis_pro_install = $ArcGISProInstallDir
    generated_at = (Get-Date).ToString('o')
    assemblies = @($assemblySummary)
    key_types = @($keyTypes)
} | ConvertTo-Json -Depth 8
