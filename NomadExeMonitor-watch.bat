@echo off
setlocal

rem Run from the folder containing this batch file.
cd /d "%~dp0"

rem Nomad connection defaults. Existing environment variables take priority.
if not defined NOMAD_ADDR set "NOMAD_ADDR=http://127.0.0.1:4646"
if not defined NOMAD_NAMESPACE set "NOMAD_NAMESPACE=*"

rem Recommended monitor options:
rem   --watch 5 : refresh every 5 seconds
rem   --wide    : show task and latest event details
rem   --verbose : print API resolution warnings to stderr
if not defined NOMAD_MONITOR_ARGS set "NOMAD_MONITOR_ARGS=--watch 5 --wide --verbose"

set "MONITOR_EXE=%~dp0NomadExeMonitor.exe"

if not exist "%MONITOR_EXE%" (
    echo ERROR: NomadExeMonitor.exe was not found.
    echo Place this batch file in the same folder as NomadExeMonitor.exe.
    echo.
    pause
    exit /b 1
)

echo Nomad EXE Monitor
echo Address   : %NOMAD_ADDR%
echo Namespace : %NOMAD_NAMESPACE%
echo Options   : %NOMAD_MONITOR_ARGS%
echo.
echo Press Ctrl+C to stop monitoring.
echo.

"%MONITOR_EXE%" %NOMAD_MONITOR_ARGS%
set "MONITOR_EXIT_CODE=%ERRORLEVEL%"

echo.
if "%MONITOR_EXIT_CODE%"=="130" (
    echo Monitoring stopped by the user.
) else if not "%MONITOR_EXIT_CODE%"=="0" (
    echo NomadExeMonitor exited with code %MONITOR_EXIT_CODE%.
) else (
    echo NomadExeMonitor finished normally.
)

pause
exit /b %MONITOR_EXIT_CODE%
