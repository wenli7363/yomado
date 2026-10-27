#Requires -RunAsAdministrator
[CmdletBinding()]
param([switch]$Remove, [string]$PackageRoot = (Join-Path $PSScriptRoot 'driver'))
$ErrorActionPreference = 'Stop'
try {
    $taskRunning = [System.Threading.Mutex]::OpenExisting('Local\Yomado.Windows')
    $taskRunning.Dispose()
    throw 'Close Yomado from its tray menu before installing or removing the display driver.'
} catch [System.Threading.WaitHandleCannotBeOpenedException] { }
$taskInstaller = Join-Path $PackageRoot 'YomadoDevice.exe'
if (!(Test-Path -LiteralPath $taskInstaller)) { throw 'YomadoDevice.exe is missing.' }
if ((Get-AuthenticodeSignature -LiteralPath $taskInstaller).Status -ne 'Valid') { throw 'The device utility must have a valid trusted signature before execution.' }
if ($Remove) {
    & $taskInstaller --remove
    if ($LASTEXITCODE -ne 0) { throw "Device removal failed ($LASTEXITCODE)." }
    Write-Host 'Yomado root device removed. The staged driver package and certificates were not deleted.'
    return
}
$taskInf = Join-Path $PackageRoot 'YomadoDriver.inf'
$taskCat = Join-Path $PackageRoot 'YomadoDriver.cat'
if (!(Test-Path -LiteralPath $taskInf) -or !(Test-Path -LiteralPath $taskCat)) { throw 'The driver INF/catalog is missing.' }
if ((Get-AuthenticodeSignature -LiteralPath $taskCat).Status -ne 'Valid') { throw 'The driver catalog must have a valid trusted signature. No trust settings have been changed.' }
& pnputil.exe /add-driver $taskInf
if ($LASTEXITCODE -ne 0) { throw "Driver staging failed ($LASTEXITCODE)." }
& $taskInstaller --install
if ($LASTEXITCODE -ne 0) { throw "Device registration failed ($LASTEXITCODE)." }
# /install only binds matching devices that already exist. Register the root
# device first, then bind the staged package; staging alone is not installation.
& pnputil.exe /add-driver $taskInf /install
if ($LASTEXITCODE -ne 0) { throw "Driver binding failed ($LASTEXITCODE)." }
$taskDevices = @(Get-PnpDevice -Class Display -PresentOnly | Where-Object InstanceId -like 'ROOT\YOMADOVIRTUALDISPLAY\*')
if ($taskDevices.Count -ne 1) { throw 'Expected exactly one registered Yomado root device.' }
$taskProblem = Get-PnpDeviceProperty -InstanceId $taskDevices[0].InstanceId -KeyName DEVPKEY_Device_ProblemCode
if ($taskProblem.Data -ne 0) { throw "Driver bound but failed to start (device problem code $($taskProblem.Data))." }
Write-Host 'Driver device started. Open Yomado and explicitly create a virtual screen.'
