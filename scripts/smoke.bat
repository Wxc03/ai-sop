@echo off
REM W11+ Phase 0 smoke test wrapper.
REM - Uses -ExecutionPolicy Bypass to allow .ps1 even under Restricted policy.
REM - Uses -NoExit so the PowerShell window stays open after the script ends
REM   (so you can see the test results, copy them, then close manually).

powershell.exe -ExecutionPolicy Bypass -NoExit -File "%~dp0run-smoke-tests.ps1"
