[CmdletBinding()]
param([switch]$ApplicationOnly, [switch]$Publish)
. "$PSScriptRoot\tools.ps1"
$taskDotnet = Get-YomadoDotnet
& $taskDotnet build "$PSScriptRoot\Yomado.App\Yomado.App.csproj" -c Release --nologo
Assert-YomadoExit 'Application build'
if (!$ApplicationOnly) {
    $taskMSBuild = Get-YomadoMSBuild
    $taskWdk = Join-Path $PSScriptRoot 'packages\Microsoft.Windows.WDK.x64.10.0.26100.6584\c'
    if (!(Test-Path -LiteralPath $taskWdk)) { throw 'WDK packages are missing. Run windows\bootstrap.ps1.' }
    foreach ($taskProject in @('YomadoDriver','DeviceInstaller','PolicyTests')) {
        & $taskMSBuild "$PSScriptRoot\driver\$taskProject.vcxproj" /p:Configuration=Release /p:Platform=x64 /nologo /verbosity:minimal
        Assert-YomadoExit $taskProject
    }
    & "$taskWdk\tools\10.0.26100.0\x64\infverif.exe" /u "$PSScriptRoot\artifacts\driver\YomadoDriver.inf"
    Assert-YomadoExit 'Universal INF verification'
}
if ($Publish) {
    & $taskDotnet publish "$PSScriptRoot\Yomado.App\Yomado.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "$PSScriptRoot\artifacts\app" --nologo
    Assert-YomadoExit 'Application publish'
}
