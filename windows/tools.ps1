$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
function Get-YomadoDotnet {
    $taskLocal = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $taskLocal) { return $taskLocal }
    $taskGlobal = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($taskGlobal -and (& $taskGlobal.Source --list-sdks | Where-Object { $_ -match '^10\.' })) { return $taskGlobal.Source }
    throw 'A .NET 10 SDK is required. Run windows\bootstrap.ps1 or install the SDK.'
}
function Get-YomadoMSBuild {
    $taskVswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (!(Test-Path -LiteralPath $taskVswhere)) { throw 'Visual Studio 2022 C++ Build Tools are required.' }
    $taskRoot = & $taskVswhere -latest -products '*' -version '[17.0,18.0)' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (!$taskRoot) { throw 'Visual Studio C++ tools were not found.' }
    return Join-Path $taskRoot 'MSBuild\Current\Bin\MSBuild.exe'
}
function Assert-YomadoExit([string]$Action) { if ($LASTEXITCODE -ne 0) { throw "$Action failed ($LASTEXITCODE)." } }
