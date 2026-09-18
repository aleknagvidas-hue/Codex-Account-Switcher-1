@echo off
setlocal
cd /d "%~dp0"

set "NO_PAUSE=0"
if /I "%~1"=="--no-pause" set "NO_PAUSE=1"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: .NET SDK was not found.
    echo Install .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0
    set "RESULT=1"
    goto finish
)

dotnet --list-sdks | findstr /B "8." >nul
if errorlevel 1 (
    echo ERROR: .NET 8 SDK was not found.
    echo Install .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0
    set "RESULT=1"
    goto finish
)

echo Building Codex Account Switcher for Windows x64...
dotnet publish ".\src\CodexAccountSwitcher\CodexAccountSwitcher.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained false ^
    -p:PublishSingleFile=true ^
    -p:DebugType=None ^
    -p:DebugSymbols=false ^
    -o ".\artifacts\win-x64"

if errorlevel 1 (
    echo.
    echo ERROR: Build failed. Review the messages above.
    set "RESULT=1"
    goto finish
)

if not exist ".\artifacts\win-x64\CodexAccountSwitcher.exe" (
    echo.
    echo ERROR: Build completed without the expected executable.
    set "RESULT=1"
    goto finish
)

echo.
echo Build completed successfully.
echo Output: %~dp0artifacts\win-x64\CodexAccountSwitcher.exe
set "RESULT=0"

:finish
if "%NO_PAUSE%"=="0" pause
exit /b %RESULT%
