# Přenosný build pro flashku: JEDEN soubor FastCopyManager.exe (self-contained, bez instalace .NET).
# Uvnitř je i 7za.exe (7-Zip Extra) a písmo; nativní části WPF se při startu rozbalí do %TEMP%\.net.
# Použití:  .\tools\publish.ps1            -> publish\FastCopyManager.exe
#           .\tools\publish.ps1 -Sign      -> navíc podepíše self-signed certifikátem (jen pro vlastní stroje)
param(
    [switch] $Sign
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out  = Join-Path $root 'publish'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish "$root\src\FastCopyManager\FastCopyManager.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish selhal.' }

$exe = Join-Path $out 'FastCopyManager.exe'
"Hotovo: $exe ($([int]((Get-Item $exe).Length / 1MB)) MB), souborů ve složce: $((Get-ChildItem $out -File | Measure-Object).Count)"

if ($Sign) {
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
    Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -TimestampServer 'http://timestamp.digicert.com' | Format-List Status, StatusMessage
}
