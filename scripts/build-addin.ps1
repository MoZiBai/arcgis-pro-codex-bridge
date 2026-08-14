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

& $MSBuild $project -restore -t:Build -p:Configuration=$Configuration -p:Platform=x64 "-p:ArcGISProInstallDir=$ArcGISProInstallDir\"
exit $LASTEXITCODE
