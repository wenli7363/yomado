[CmdletBinding()]
param([string]$SdkRoot)
. "$PSScriptRoot\tools.ps1"
if (!(Test-Path -LiteralPath "$PSScriptRoot\artifacts\app\Yomado.exe")) { throw 'Run windows\build.ps1 -Publish first.' }
if (!(Test-Path -LiteralPath "$PSScriptRoot\artifacts\driver\YomadoDriver.dll")) { throw 'The driver has not been built.' }
if (!$SdkRoot) {
    $taskKits = Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots' -ErrorAction SilentlyContinue
    if (!$taskKits) { $taskKits = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -ErrorAction SilentlyContinue }
    $SdkRoot = $taskKits.KitsRoot10
}
if (!$SdkRoot) { throw 'Windows SDK root was not found; pass -SdkRoot explicitly.' }
$taskMakeCat = Join-Path $SdkRoot 'bin\10.0.26100.0\x64\makecat.exe'
if (!(Test-Path -LiteralPath $taskMakeCat)) { throw 'Windows SDK 10.0.26100.0 makecat.exe was not found.' }
$taskPackage = Join-Path $PSScriptRoot 'artifacts\Yomado-Windows-0.1.0-dev-x64'
New-Item -ItemType Directory -Path "$taskPackage\driver" -Force | Out-Null
Copy-Item "$PSScriptRoot\artifacts\app\*" -Destination $taskPackage -Recurse -Force
foreach ($taskFile in @('YomadoDriver.dll','YomadoDriver.inf','YomadoDevice.exe','LICENSE')) {
    Copy-Item -LiteralPath "$PSScriptRoot\artifacts\driver\$taskFile" -Destination "$taskPackage\driver\$taskFile" -Force
}
@'
[CatalogHeader]
Name=YomadoDriver.cat
PublicVersion=0x0000001
CatalogVersion=2
HashAlgorithms=SHA256
EncodingType=0x00010001
CATATTR1=0x10010001:OSAttr:2:10.0
[CatalogFiles]
<HASH>YomadoDriver.inf=YomadoDriver.inf
<HASH>YomadoDriver.dll=YomadoDriver.dll
'@ | Set-Content -LiteralPath "$taskPackage\driver\YomadoDriver.cdf" -Encoding ascii
Push-Location "$taskPackage\driver"
try { & $taskMakeCat -v -r YomadoDriver.cdf; Assert-YomadoExit 'Catalog generation' } finally { Pop-Location }
Copy-Item -LiteralPath "$PSScriptRoot\install-driver.ps1","$PSScriptRoot\README.md","$PSScriptRoot\THIRD-PARTY-NOTICES.md" -Destination $taskPackage -Force
Copy-Item -LiteralPath "$PSScriptRoot\..\LICENSE" -Destination "$taskPackage\LICENSE" -Force
$taskArchive = "$taskPackage.zip"
Compress-Archive -Path "$taskPackage\*" -DestinationPath $taskArchive -Force
$taskHash = (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash
"$taskHash  $([IO.Path]::GetFileName($taskArchive))" | Set-Content -LiteralPath "$taskArchive.sha256" -Encoding ascii
Write-Host "Development package: $taskArchive"
Write-Host 'The package is unsigned. Signing and installation remain separate steps.'
