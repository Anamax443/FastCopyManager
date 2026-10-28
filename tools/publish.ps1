# Přenosný (self-contained) build pro flashku; na cílovém PC není potřeba .NET.
# Publikuje FastCopyManager a podepíše exe self-signed certifikátem (jen pro vlastní stroje).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out  = Join-Path $root 'publish'

dotnet publish "$root\src\FastCopyManager\FastCopyManager.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $out

# 7-Zip (7za.exe) pro vytváření 7z přímo z flashky, bez instalace; licence musí být přiložena.
$tools = Join-Path $out 'Tools'
New-Item -ItemType Directory -Force $tools | Out-Null
$vendor = Join-Path $root 'third_party\7-Zip'
Copy-Item "$vendor\7za.exe" $tools -Force
Copy-Item "$vendor\License.txt" (Join-Path $tools '7-Zip-License.txt') -Force
Copy-Item "$vendor\SOURCE.txt" (Join-Path $tools '7-Zip-SOURCE.txt') -Force

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
