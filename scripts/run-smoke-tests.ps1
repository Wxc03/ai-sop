# W11+ Phase 0 real-machine API writability smoke test runner.
# Run this with: powershell -ExecutionPolicy Bypass -NoExit -File run-smoke-tests.ps1
#
# Just runs vstest, writes everything to smoke-output.txt, prints tail to console.
# The wrapper cmd uses -NoExit so the window stays open after the script ends.

Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
$ErrorActionPreference = "Continue"

$repoRoot = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $repoRoot "src\SwSopAddin.Tests\bin\Debug\SwSopAddin.Tests.dll"

if (-not (Test-Path $dll)) {
    Write-Host "[ERROR] Tests DLL not found: $dll" -ForegroundColor Red
    Write-Host "        Please MSBuild the whole solution first (VS Ctrl+Shift+B)."
    Read-Host "Press Enter to exit"
    exit 1
}

$vstest = $null
try {
    $candidate = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\' `
        -Recurse -Filter 'vstest.console.exe' -ErrorAction SilentlyContinue `
        | Select-Object -First 1
    if ($candidate) { $vstest = $candidate.FullName }
} catch {}

if (-not $vstest) {
    Write-Host "[ERROR] vstest.console.exe not found." -ForegroundColor Red
    Read-Host "Press Enter to exit"
    exit 1
}

$logPath = Join-Path $PSScriptRoot "smoke-output.txt"
Write-Host "=== Smoke Tests ===" -ForegroundColor Cyan
Write-Host "VSTEST      = $vstest"
Write-Host "DLL         = $dll"
Write-Host "LOG         = $logPath"
Write-Host ""

# Run vstest, capture all output. /logger:console;verbosity=detailed makes
# Inconclusive messages visible (so user can see WHY each was skipped).
$p = Start-Process -FilePath $vstest `
    -ArgumentList "`"$dll`" /TestCaseFilter:`"FullyQualifiedName~SwLayoutApiSmokeTests`" /logger:`"console;verbosity=detailed`"" `
    -NoNewWindow -Wait -PassThru `
    -RedirectStandardOutput $logPath -RedirectStandardError "$logPath.err"

Write-Host ""
Write-Host ("Exit code: " + $p.ExitCode) -ForegroundColor Cyan
if ($p.ExitCode -eq 0) {
    Write-Host "=== All Passed (no Fail) ===" -ForegroundColor Green
} else {
    Write-Host "=== Some tests FAILED ===" -ForegroundColor Red
}
Write-Host "Full output: $logPath"
Write-Host ""
Write-Host "---- Full log ----" -ForegroundColor Yellow
if (Test-Path $logPath) {
    Get-Content $logPath
}
Write-Host ""
Write-Host "---- End ----" -ForegroundColor Yellow

Read-Host "Press Enter to exit"
