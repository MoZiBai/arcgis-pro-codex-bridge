param(
    [string]$Configuration = 'Release',
    [string]$ArcGISProInstallDir = '',
    [string]$MSBuild = ''
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\addin\ArcGISCodexBridgeAddin\ArcGISCodexBridgeAddin.csproj'

if ([string]::IsNullOrWhiteSpace($ArcGISProInstallDir)) {
    $candidates = @(
        $env:ARCGIS_PRO_INSTALL_DIR,
        (Join-Path $env:ProgramFiles 'ArcGIS\Pro'),
        (Join-Path ${env:ProgramFiles(x86)} 'ArcGIS\Pro')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $_ 'bin\ArcGISPro.exe')) }
    $ArcGISProInstallDir = $candidates | Select-Object -First 1
}
if (-not $ArcGISProInstallDir) {
    throw 'ArcGIS Pro was not found. Pass -ArcGISProInstallDir or set ARCGIS_PRO_INSTALL_DIR.'
}

if ([string]::IsNullOrWhiteSpace($MSBuild)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $MSBuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
            Select-Object -First 1
    }
}
if (-not $MSBuild -or -not (Test-Path -LiteralPath $MSBuild)) {
    throw 'MSBuild was not found. Install Visual Studio Build Tools or pass -MSBuild.'
}

$dotnetRoot = Join-Path $PSScriptRoot '..\.dotnet'
$localDotnet = Join-Path $dotnetRoot 'dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$sdkVersion = (& $dotnet --version).Trim()
$resolvedDotnetRoot = Split-Path $dotnet -Parent
$sdkRoot = if (Test-Path -LiteralPath (Join-Path $resolvedDotnetRoot "sdk\$sdkVersion\Sdks")) {
    Join-Path $resolvedDotnetRoot "sdk\$sdkVersion\Sdks"
}
else {
    throw "Could not find MSBuild SDKs for .NET SDK $sdkVersion under $resolvedDotnetRoot"
}
$env:MSBuildSDKsPath = $sdkRoot
$env:DOTNET_HOST_PATH = $dotnet
$env:MSBuildEnableWorkloadResolver = 'false'

# Esri's packaging target can retain files in obj/.../temp_archive even after a
# normal MSBuild clean. Remove only these resolved build artifacts so an old PDB
# cannot be carried into a new Release package.
$projectRoot = Split-Path -Parent $project
$staleArchive = Join-Path $projectRoot "obj\x64\$Configuration\temp_archive"
$stalePackage = Join-Path $projectRoot "bin\x64\$Configuration\ArcGISCodexBridgeAddin.esriAddinX"
foreach ($artifact in @($staleArchive, $stalePackage)) {
    if (Test-Path -LiteralPath $artifact) {
        Remove-Item -LiteralPath $artifact -Recurse -Force
    }
}

& $MSBuild $project -restore -t:Rebuild -p:Configuration=$Configuration -p:Platform=x64 "-p:ArcGISProInstallDir=$ArcGISProInstallDir\"
exit $LASTEXITCODE
