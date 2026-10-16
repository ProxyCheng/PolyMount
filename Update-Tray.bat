@echo off
setlocal
rem Double-click to update the tray alone (Debug), or run Update-Tray.bat Release from a terminal.
rem The supervisor and every instance worker keep running: no remount, no cache lost.
set "CONFIGURATION=%~1"
if not defined CONFIGURATION set "CONFIGURATION=Debug"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Update-PolyMount.ps1" -Configuration "%CONFIGURATION%" -TrayOnly
set "RESULT=%ERRORLEVEL%"
echo.
if not "%RESULT%"=="0" echo Update failed. Review the error above.
exit /b %RESULT%
