# Publikuje FastCopyManager a podepíše exe self-signed certifikátem (jen pro vlastní stroje).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out  = Join-Path $root 'publish'

dotnet publish "$root\src\FastCopyManager\FastCopyManager.csproj" -c Release -r win-x64 --self-contained false -o $out

$subject = 'CN=FastCopyManager (AXIMA)'
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object Subject -eq $subject | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5)
    # důvěřovat certifikátu na tomto stroji (vyžaduje potvrzení)
    $tmp = Join-Path $env:TEMP 'fcm.cer'
    Export-Certificate -Cert $cert -FilePath $tmp | Out-Null
    Import-Certificate -FilePath $tmp -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
    Import-Certificate -FilePath $tmp -CertStoreLocation Cert:\CurrentUser\TrustedPublisher | Out-Null
}
Set-AuthenticodeSignature -FilePath "$out\FastCopyManager.exe" -Certificate $cert -TimestampServer 'http://timestamp.digicert.com' | Format-List Status, StatusMessage
