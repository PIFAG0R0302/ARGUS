<#
.SYNOPSIS
    Creates or retrieves the Argus Authenticode code-signing certificate in Cert:\CurrentUser\My and exports the public .cer file.

.DESCRIPTION
    Used for signing Argus.exe and Argus-Setup-*-win-x64.exe via Set-AuthenticodeSignature.
    To switch to a commercial certificate (e.g. Certum Open Source or SignPath) later, import your certificate into
    Cert:\CurrentUser\My and pass -SignCertThumbprint "<NEW_THUMBPRINT>" to Build-Release.ps1.
#>
[CmdletBinding()]
param(
    [string]$Subject = 'CN=Argus Open Source Release, O=Argus',
    [switch]$TrustInCurrentUserRoot
)

$ErrorActionPreference = 'Stop'
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$root = [System.IO.Path]::GetFullPath((Join-Path $scriptDir '..'))

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq $Subject -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $cert) {
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(5) `
        -FriendlyName 'Argus Authenticode Code Signing'
}

$certsDir = Join-Path $root 'certs'
if (-not (Test-Path $certsDir)) {
    New-Item -ItemType Directory -Path $certsDir -Force | Out-Null
}

$cerPath = Join-Path $certsDir 'Argus-CodeSigning.cer'
Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null

if ($TrustInCurrentUserRoot) {
    Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\CurrentUser\TrustedPublisher' | Out-Null
    Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
}

[PSCustomObject]@{
    Subject        = $cert.Subject
    Thumbprint     = $cert.Thumbprint
    NotAfter       = $cert.NotAfter.ToString('yyyy-MM-dd')
    PublicCertPath = $cerPath
}
