@echo off
chcp 65001 >nul
setlocal EnableExtensions

set "BASE_DIR=%~dp0"
set "CER_FILE=%BASE_DIR%Argus-CodeSigning.cer"

if not exist "%CER_FILE%" (
    echo [ERROR] Argus-CodeSigning.cer not found in "%BASE_DIR%".
    pause
    exit /b 1
)

echo === ARGUS Certificate & Unblock Utility ===
echo.

net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [i] Requesting Administrator privileges ^(UAC^) to install certificate into LocalMachine...
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs" 2>nul
    if %errorlevel% equ 0 exit /b 0
    echo [WARN] UAC declined or unavailable. Falling back to CurrentUser certificate store...
    certutil.exe -user -addstore -f Root "%CER_FILE%"
    certutil.exe -user -addstore -f TrustedPublisher "%CER_FILE%"
) else (
    echo [1/2] Importing Argus-CodeSigning.cer into LocalMachine\Root and TrustedPublisher...
    certutil.exe -addstore -f Root "%CER_FILE%"
    certutil.exe -addstore -f TrustedPublisher "%CER_FILE%"
)

echo.
echo [2/2] Removing Windows Mark-of-the-Web ^(Zone.Identifier^) from ARGUS files...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath '%BASE_DIR%' -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue; $exe = Join-Path '%BASE_DIR%' 'Argus.exe'; if (Test-Path $exe) { $sig = Get-AuthenticodeSignature -FilePath $exe; Write-Host ('  Argus.exe signature status: ' + $sig.Status) }"

echo.
echo [OK] Done. Note: if Windows 11 Smart App Control (SAC) is in full Enforcement mode,
echo      only a public CA certificate (Trusted Signing / SignPath) or disabling SAC in Windows Security applies.
echo.
pause
endlocal
