@echo off
REM ============================================================
REM  SwSopAddin Host - COM Unregister Script
REM  Must run as Administrator!
REM ============================================================

setlocal enabledelayedexpansion

set "REGASM=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"
if not exist "%REGASM%" (
    echo [ERROR] RegAsm.exe not found.
    pause
    exit /b 1
)

set "DLL=%~dp0bin\Debug\SwSopAddin.Host.dll"
if not exist "%DLL%" set "DLL=%~dp0bin\Release\SwSopAddin.Host.dll"

if not exist "%DLL%" (
    echo [ERROR] DLL not found, cannot unregister via RegAsm.
    echo If you deleted bin/, manually remove these registry keys:
    echo   HKLM\SOFTWARE\SolidWorks\AddIns\{B3F5C7A1-8E2D-4A9B-9C3F-1D8E5A7B6C9D}
    echo   HKCU\SOFTWARE\SolidWorks\AddInsStartup\{B3F5C7A1-8E2D-4A9B-9C3F-1D8E5A7B6C9D}
    pause
    exit /b 1
)

echo Unregistering: %DLL%
echo.
"%REGASM%" /unregister "%DLL%"
set "ERR=%ERRORLEVEL%"
echo.

if "%ERR%"=="0" (
    echo [OK] Unregistered. Restart SolidWorks for the change to take effect.
) else (
    echo [FAIL] RegAsm exit code %ERR%
)

echo.
pause
endlocal
