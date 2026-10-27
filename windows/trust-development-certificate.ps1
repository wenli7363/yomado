#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ApprovedThumbprint)
$ErrorActionPreference = 'Stop'
$taskCertificatePath = Join-Path $PSScriptRoot 'Yomado-Development.cer'
$taskInfo = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'SIGNING-INFO.json') -Raw | ConvertFrom-Json
$taskCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($taskCertificatePath)
if ($taskCertificate.Thumbprint -ne $ApprovedThumbprint -or $taskInfo.Thumbprint -ne $ApprovedThumbprint -or
    (Get-FileHash -LiteralPath $taskCertificatePath -Algorithm SHA256).Hash -ne $taskInfo.CertificateSha256) { throw 'Certificate fingerprint does not match the explicitly approved signer.' }
if ($taskCertificate.NotAfter -lt (Get-Date)) { throw 'Development certificate has expired.' }
foreach ($taskName in @('Root','TrustedPublisher')) {
    $taskStore = [System.Security.Cryptography.X509Certificates.X509Store]::new($taskName,'LocalMachine')
    $taskStore.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try { $taskStore.Add($taskCertificate) } finally { $taskStore.Close(); $taskStore.Dispose() }
}
Write-Host 'Exact development signer trusted. No application-control rule or boot setting was changed.'
