@echo off
REM ============================================================
REM  SwSopAddin Host - COM Register Script
REM  Must run as Administrator!
REM  Looks for the DLL under bin\Debug first, then bin\Release.
REM ============================================================

setlocal enabledelayedexpansion

set "REGASM=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"
if not exist "%REGASM%" (
    echo [ERROR] RegAsm.exe not found at: %REGASM%
    echo .NET Framework 4.8 may be missing.
    pause
    exit /b 1
)

set "DLL=%~dp0bin\Debug\SwSopAddin.Host.dll"
if not exist "%DLL%" set "DLL=%~dp0bin\Release\SwSopAddin.Host.dll"

if not exist "%DLL%" (
    echo [ERROR] Built DLL not found. Build the project first.
    echo Looked at:
    echo   %~dp0bin\Debug\SwSopAddin.Host.dll
    echo   %~dp0bin\Release\SwSopAddin.Host.dll
    pause
    exit /b 1
)

echo Registering: %DLL%
echo.
"%REGASM%" /codebase "%DLL%"
set "ERR=%ERRORLEVEL%"
echo.

if "%ERR%"=="0" (
    echo [OK] Registered. Start SolidWorks 2024 - the "SOP" menu should appear.
) else (
    echo [FAIL] RegAsm exit code %ERR%
    echo Most common cause: not running as Administrator.
)

echo.
pause
endlocal
