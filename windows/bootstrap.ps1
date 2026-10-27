[CmdletBinding()]
param([string]$SdkVersion = '10.0.402', [switch]$ApplicationOnly)
$ErrorActionPreference = 'Stop'
$taskTools = Join-Path $PSScriptRoot '.tools'
New-Item -ItemType Directory -Path $taskTools -Force | Out-Null
$taskDotnet = Join-Path $taskTools 'dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $taskDotnet)) {
    $taskInstaller = Join-Path $taskTools 'dotnet-install.ps1'
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $taskInstaller
    & $taskInstaller -Version $SdkVersion -Architecture x64 -InstallDir (Join-Path $taskTools 'dotnet') -NoPath
    if ($LASTEXITCODE -ne 0) { throw '.NET SDK installation failed.' }
}
if (!$ApplicationOnly) {
    $taskNuget = Join-Path $taskTools 'nuget.exe'
    if (!(Test-Path -LiteralPath $taskNuget)) { Invoke-WebRequest 'https://dist.nuget.org/win-x86-commandline/v6.14.0/nuget.exe' -OutFile $taskNuget }
    if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'packages\Microsoft.Windows.WDK.x64.10.0.26100.6584'))) {
        & $taskNuget install Microsoft.Windows.WDK.x64 -Version 10.0.26100.6584 -OutputDirectory (Join-Path $PSScriptRoot 'packages') -Source 'https://api.nuget.org/v3/index.json' -NonInteractive -DirectDownload -Verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw 'WDK package restore failed.' }
    }
}
Write-Host 'Local build dependencies ready. No driver or trust certificate was installed.'
