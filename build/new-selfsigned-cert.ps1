<#
.SYNOPSIS
  Creates a self-signed code-signing certificate for Angelito Systems in the current user's store and exports its public
  part to artifacts\AngelitoSystems-codesign.cer. Prints the thumbprint to pass to build-installer.ps1 -CertThumbprint.
.NOTES
  A self-signed signature proves the files weren't altered after signing, but Windows only trusts it on PCs where the
  .cer was imported into "Trusted Root" and "Trusted Publishers" (e.g. by IT via group policy). Elsewhere SmartScreen
  still warns about an unknown publisher; removing that needs a certificate from a public CA (ideally EV).
#>
param([int]$Years = 5)
$ErrorActionPreference = "Stop"
$existing = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq "CN=Angelito Systems S.A.C." -and $_.NotAfter -gt (Get-Date).AddDays(30) } | Select-Object -First 1
$cert = if ($existing) { $existing } else {
  New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=Angelito Systems S.A.C." -FriendlyName "Angelito Systems (autofirmado)" `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears($Years)
}
$out = Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts"
New-Item -ItemType Directory -Force $out | Out-Null
Export-Certificate -Cert $cert -FilePath (Join-Path $out "AngelitoSystems-codesign.cer") | Out-Null
Write-Host "Certificado: $($cert.Subject)  válido hasta $($cert.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "Huella (CertThumbprint): $($cert.Thumbprint)"
