<#
.SYNOPSIS
    Reproducible release builder for ARGUS (Windows x64 installer, portable ZIP, SHA-256 checksums, optional Authenticode signing, and Defender verification).

.DESCRIPTION
    1. Reads product version from Directory.Build.props.
    2. Runs strict Release build + full xUnit test suite.
    3. Publishes clean self-contained single-file win-x64 Argus.exe (asInvoker by default with on-demand UAC, zero UPX/obfuscation).
    4. Optionally signs Argus.exe with Authenticode / Trusted Signing.
    5. Compiles Inno Setup 6 installer into ArgusSetup\Argus-Setup-<Version>-win-x64.exe.
    6. Packages ArgusSetup\Argus-Portable-<Version>-win-x64.zip with Argus.exe, rules\, and USER-GUIDE.md.
    7. Writes ArgusSetup\SHA256SUMS.txt (UTF-8 without BOM) and verifies every artifact with Windows Defender.

.EXAMPLE
    .\scripts\Build-Release.ps1
    .\scripts\Build-Release.ps1 -SignCertThumbprint "AABBCC..." -TimestampUrl "http://timestamp.digicert.com"
#>
[CmdletBinding()]
param(
    [string]$OutputDir = '',
    [switch]$SkipTests,
    [switch]$RequireAdmin,
    [string]$SignCertThumbprint = '9D1B22482AC32CEA02F398527F9409FCA09EA78D',
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$root = [System.IO.Path]::GetFullPath((Join-Path $scriptDir '..'))
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $root 'ArgusSetup'
}

function Resolve-DotNet10 {
    foreach ($dir in @((Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'), 'C:\Program Files\dotnet')) {
        $exe = Join-Path $dir 'dotnet.exe'
        if (Test-Path $exe) {
            $sdks = & $exe --list-sdks 2>$null
            if ($sdks -match '^10\.') { return @{ Dir = $dir; Exe = $exe } }
        }
    }
    throw 'Не знайдено .NET 10 SDK.'
}

function Resolve-InnoSetupCompiler {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )
    foreach ($p in $candidates) {
        if (Test-Path $p) { return $p }
    }
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw 'Не знайдено Inno Setup 6 (ISCC.exe). Встановіть JRSoftware.InnoSetup.'
}

function Invoke-OptionalSign {
    param([string]$FilePath)
    if ([string]::IsNullOrWhiteSpace($SignCertThumbprint)) { return }

    $cleanThumb = $SignCertThumbprint.Replace(' ', '').ToUpperInvariant()
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $cleanThumb } |
        Select-Object -First 1

    if (-not $cert) {
        $initInfo = & (Join-Path $scriptDir 'Initialize-CodeSigningCert.ps1')
        $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
            Where-Object { $_.Thumbprint -eq $initInfo.Thumbprint } |
            Select-Object -First 1
    }

    if (-not $cert) {
        throw "Не знайдено сертифікат підпису коду ($cleanThumb) у Cert:\CurrentUser\My."
    }

    Write-Host "Signing $([System.IO.Path]::GetFileName($FilePath)) with Authenticode ($($cert.Thumbprint)) ..." -ForegroundColor Cyan
    $sig = $null
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl) -and $TimestampUrl -ne 'none') {
        try {
            $sig = Set-AuthenticodeSignature -FilePath $FilePath -Certificate $cert -HashAlgorithm SHA256 -TimestampServer $TimestampUrl -ErrorAction Stop
        } catch {
            $sig = $null
        }
    }
    if (-not $sig -or $sig.Status -eq 'NotSigned') {
        $sig = Set-AuthenticodeSignature -FilePath $FilePath -Certificate $cert -HashAlgorithm SHA256 -ErrorAction Stop
    }

    $verify = Get-AuthenticodeSignature -FilePath $FilePath
    if (-not $verify.SignerCertificate -or $verify.SignerCertificate.Thumbprint -ne $cert.Thumbprint) {
        throw "Перевірка Authenticode для $FilePath не пройшла (Status=$($verify.Status))."
    }
    Write-Host "  Signed OK: Subject='$($verify.SignerCertificate.Subject)' Thumbprint=$($verify.SignerCertificate.Thumbprint)" -ForegroundColor Green
}

$resolvedDotNet = Resolve-DotNet10
$dotnet = $resolvedDotNet.Exe
$env:DOTNET_ROOT = $resolvedDotNet.Dir
$env:PATH = "$($resolvedDotNet.Dir);$env:PATH"

$iscc = Resolve-InnoSetupCompiler

[xml]$propsXml = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
$version = $propsXml.Project.PropertyGroup.Version.Trim()
if ([string]::IsNullOrWhiteSpace($version)) { throw 'Не вдалося прочитати <Version> з Directory.Build.props.' }

Write-Host "=== ARGUS RELEASE PIPELINE v$version ===" -ForegroundColor Cyan

if (-not $SkipTests) {
    Write-Host '=== 1. STRICT DEBUG & RELEASE BUILD + TESTS ==='
    & (Join-Path $root 'build.ps1') -Configuration Debug -Strict -Test
    if ($LASTEXITCODE -ne 0) { throw "Debug build/tests failed: $LASTEXITCODE" }
    & (Join-Path $root 'build.ps1') -Configuration Release -Strict -Test
    if ($LASTEXITCODE -ne 0) { throw "Release build/tests failed: $LASTEXITCODE" }
}

Write-Host '=== 2. PUBLISH SELF-CONTAINED WIN-X64 PAYLOAD ==='
$stageDir = Join-Path $root 'publish\release-stage'
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

$win64Obj = Join-Path $root 'src\Argus.App\obj\Release\net10.0-windows\win-x64'
if (Test-Path $win64Obj) { Remove-Item $win64Obj -Recurse -Force }

$requireAdminFlag = if ($RequireAdmin.IsPresent) { 'true' } else { 'false' }
$appProj = Join-Path $root 'src\Argus.App\Argus.App.csproj'
& $dotnet publish $appProj -c Release -r win-x64 --self-contained true `
    -p:ArgusRequireAdmin=$requireAdminFlag `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -p:TreatWarningsAsErrors=true `
    -o $stageDir
if ($LASTEXITCODE -ne 0) { throw "Release publish failed: $LASTEXITCODE" }

$stagedExe = Join-Path $stageDir 'Argus.exe'
Invoke-OptionalSign -FilePath $stagedExe

# Keep root Argus.exe synced with the release binary (if not currently running)
$rootExe = Join-Path $root 'Argus.exe'
$standaloneHash = (Get-FileHash -Path $stagedExe -Algorithm SHA256).Hash.ToLowerInvariant()
try {
    Copy-Item -Path $stagedExe -Destination $rootExe -Force -ErrorAction Stop
} catch {
    Write-Host "Примітка: кореневий Argus.exe зараз запущено, тому оновлені файли записано в ArgusSetup\." -ForegroundColor Yellow
}

# Stage external JSON rules, user guide, public code-signing cert, and one-click unblock helper
Copy-Item -Path (Join-Path $root 'rules') -Destination (Join-Path $stageDir 'rules') -Recurse -Force
Copy-Item -Path (Join-Path $root 'docs\USER-GUIDE.md') -Destination (Join-Path $stageDir 'USER-GUIDE.md') -Force
$pubCer = Join-Path $root 'certs\Argus-CodeSigning.cer'
if (Test-Path $pubCer) {
    Copy-Item -Path $pubCer -Destination (Join-Path $stageDir 'Argus-CodeSigning.cer') -Force
}
$certInstaller = Join-Path $scriptDir 'Install-Certificate.cmd'
if (Test-Path $certInstaller) {
    Copy-Item -Path $certInstaller -Destination (Join-Path $stageDir 'Install-Certificate.cmd') -Force
}

$resolvedOut = [System.IO.Path]::GetFullPath($OutputDir)
if (Test-Path $resolvedOut) { Remove-Item $resolvedOut -Recurse -Force }
New-Item -ItemType Directory -Path $resolvedOut -Force | Out-Null

Write-Host '=== 3. COMPILE INNO SETUP 6 INSTALLER ==='
$issPath = Join-Path $root 'installer\Argus.iss'
& $iscc "/DMyAppVersion=$version" "/DPayloadDir=$stageDir" "/DOutputDirPath=$resolvedOut" $issPath
if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed with exit code $LASTEXITCODE" }

$setupExe = Join-Path $resolvedOut "Argus-Setup-$version-win-x64.exe"
if (-not (Test-Path $setupExe)) { throw "Очікуваний інсталятор не створено: $setupExe" }
Invoke-OptionalSign -FilePath $setupExe

Write-Host '=== 4. PACKAGE PORTABLE ZIP & DOCS ==='
$portableZip = Join-Path $resolvedOut "Argus-Portable-$version-win-x64.zip"
Compress-Archive -Path (Join-Path $stageDir '*') -DestinationPath $portableZip -CompressionLevel Optimal -Force
Copy-Item -Path (Join-Path $root 'docs\USER-GUIDE.md') -Destination (Join-Path $resolvedOut 'USER-GUIDE.md') -Force
if (Test-Path $pubCer) {
    Copy-Item -Path $pubCer -Destination (Join-Path $resolvedOut 'Argus-CodeSigning.cer') -Force
}
if (Test-Path $certInstaller) {
    Copy-Item -Path $certInstaller -Destination (Join-Path $resolvedOut 'Install-Certificate.cmd') -Force
}

Write-Host '=== 5. GENERATE SHA-256 CHECKSUMS ==='
$checksumLines = [System.Collections.Generic.List[string]]::new()
foreach ($artifact in @($setupExe, $portableZip)) {
    $hash = (Get-FileHash -Path $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
    $name = [System.IO.Path]::GetFileName($artifact)
    $checksumLines.Add("$hash  $name")
}
$checksumLines.Add("$standaloneHash  Argus.exe (standalone)")

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
$shaPath = Join-Path $resolvedOut 'SHA256SUMS.txt'
[System.IO.File]::WriteAllLines($shaPath, $checksumLines, $utf8NoBom)

# Clean staging folder
Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host '=== 6. WINDOWS DEFENDER VERIFICATION ==='
$mpCmd = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
if (Test-Path $mpCmd) {
    & $mpCmd -Scan -ScanType 3 -File $resolvedOut | Out-Null
    Write-Host "DEFENDER_SETUP_DIR_EXIT: $LASTEXITCODE"
    if ($LASTEXITCODE -ne 0) { throw "Windows Defender scan failed on $resolvedOut (exit $LASTEXITCODE)" }

    & $mpCmd -Scan -ScanType 3 -File $rootExe | Out-Null
    Write-Host "DEFENDER_ROOT_EXE_EXIT: $LASTEXITCODE"
    if ($LASTEXITCODE -ne 0) { throw "Windows Defender scan failed on $rootExe (exit $LASTEXITCODE)" }
}

Write-Host '=== RELEASE ARTIFACTS READY ===' -ForegroundColor Green
Get-ChildItem $resolvedOut | Select-Object Name, @{N='SizeMB';E={[math]::Round($_.Length / 1MB, 2)}} | Format-Table -AutoSize
Get-Content $shaPath
