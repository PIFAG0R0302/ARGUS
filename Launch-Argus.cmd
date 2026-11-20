@echo off
setlocal
set "ROOT=%~dp0"
set "DOTNET_DIR=%LOCALAPPDATA%\Microsoft\dotnet"
if exist "%DOTNET_DIR%\dotnet.exe" (
    set "DOTNET_ROOT=%DOTNET_DIR%"
    set "PATH=%DOTNET_DIR%;%PATH%"
)

set "EXE=%ROOT%src\Argus.App\bin\Release\net10.0-windows\Argus.exe"
if not exist "%EXE%" (
    echo Building Argus Release...
    powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%build.ps1" -Configuration Release
    if errorlevel 1 (
        pause
        exit /b 1
    )
)

start "" "%EXE%" %*
endlocal
