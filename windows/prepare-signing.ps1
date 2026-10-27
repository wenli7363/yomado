[CmdletBinding()]
param([string]$SdkRoot, [string]$CertificateThumbprint)
. "$PSScriptRoot\tools.ps1"
$taskPackage = Join-Path $PSScriptRoot 'artifacts\Yomado-Windows-0.1.0-dev-x64'
if (!(Test-Path -LiteralPath "$taskPackage\driver\YomadoDriver.cdf")) { throw 'Run package.ps1 first.' }
if (!$SdkRoot) {
    $taskKits = Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots' -ErrorAction SilentlyContinue
    $SdkRoot = $taskKits.KitsRoot10
}
$taskSigner = Join-Path $SdkRoot 'bin\10.0.26100.0\x64\signtool.exe'
$taskMakeCat = Join-Path $SdkRoot 'bin\10.0.26100.0\x64\makecat.exe'
if (!(Test-Path -LiteralPath $taskSigner)) { throw 'Windows SDK signing tools were not found.' }
$taskStore = [System.Security.Cryptography.X509Certificates.X509Store]::new('My','CurrentUser')
$taskStore.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
try {
    if ($CertificateThumbprint) {
        $taskMatches = $taskStore.Certificates.Find([System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,$CertificateThumbprint,$false)
        if ($taskMatches.Count -ne 1 -or !$taskMatches[0].HasPrivateKey) { throw 'An existing signing certificate with a private key was not found.' }
        $taskCertificate = $taskMatches[0]
    } else {
        # Preparation adds a signing identity to CurrentUser\My only. It adds
        # nothing to Root/TrustedPublisher and never installs a driver.
        $taskRsa = [System.Security.Cryptography.RSA]::Create(3072)
        $taskRequest = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
            'CN=Yomado Local Development', $taskRsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $taskRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature,$true))
        $taskPurposes = [System.Security.Cryptography.OidCollection]::new()
        $taskPurposes.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')) | Out-Null
        $taskRequest.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($taskPurposes,$true))
        $taskEphemeral = $taskRequest.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5),[DateTimeOffset]::UtcNow.AddDays(90))
        $taskPassword = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
        $taskBytes = $taskEphemeral.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx,$taskPassword)
        $taskCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($taskBytes,$taskPassword,
            [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet -bor [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet)
        $taskStore.Add($taskCertificate)
        [Array]::Clear($taskBytes,0,$taskBytes.Length)
        $taskPassword = $null
        $taskEphemeral.Dispose(); $taskRsa.Dispose()
    }
    $taskThumbprint = $taskCertificate.Thumbprint
    [IO.File]::WriteAllBytes("$taskPackage\Yomado-Development.cer",$taskCertificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    foreach ($taskFile in @("$taskPackage\Yomado.exe","$taskPackage\driver\YomadoDriver.dll","$taskPackage\driver\YomadoDevice.exe")) {
        & $taskSigner sign /s My /sha1 $taskThumbprint /fd SHA256 $taskFile
        Assert-YomadoExit 'Development Authenticode signing'
    }
    # Catalog member hashes must be regenerated after signing the DLL.
    Push-Location "$taskPackage\driver"
    try { & $taskMakeCat -v -r YomadoDriver.cdf; Assert-YomadoExit 'Signed DLL catalog generation' } finally { Pop-Location }
    & $taskSigner sign /s My /sha1 $taskThumbprint /fd SHA256 "$taskPackage\driver\YomadoDriver.cat"
    Assert-YomadoExit 'Development catalog signing'
    [ordered]@{ Subject=$taskCertificate.Subject; Thumbprint=$taskThumbprint; CertificateSha256=(Get-FileHash -LiteralPath "$taskPackage\Yomado-Development.cer" -Algorithm SHA256).Hash;
        ExpiresUtc=$taskCertificate.NotAfter.ToUniversalTime().ToString('o'); TrustRequiredOnTarget=$true;
        Purpose='Local development only; not a production publisher certificate.' } | ConvertTo-Json | Set-Content -LiteralPath "$taskPackage\SIGNING-INFO.json" -Encoding utf8
    Copy-Item -LiteralPath "$PSScriptRoot\trust-development-certificate.ps1" -Destination $taskPackage -Force
    Copy-Item -LiteralPath "$PSScriptRoot\README.md","$PSScriptRoot\THIRD-PARTY-NOTICES.md","$PSScriptRoot\install-driver.ps1" -Destination $taskPackage -Force
    Compress-Archive -Path "$taskPackage\*" -DestinationPath "$taskPackage.zip" -Force
    $taskHash = (Get-FileHash -LiteralPath "$taskPackage.zip" -Algorithm SHA256).Hash
    "$taskHash  $([IO.Path]::GetFileName("$taskPackage.zip"))" | Set-Content -LiteralPath "$taskPackage.zip.sha256" -Encoding ascii
    Write-Host "Prepared development signer: $taskThumbprint"
    Write-Host 'Package preparation does not install certificates or devices. Use the machine-owner-approved installation workflow.'
} finally { $taskStore.Close(); $taskStore.Dispose() }
