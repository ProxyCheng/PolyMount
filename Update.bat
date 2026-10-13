@echo off
setlocal
rem Double-click for Debug, or run Update.bat Release from a terminal.
set "CONFIGURATION=%~1"
if not defined CONFIGURATION set "CONFIGURATION=Debug"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Update-PolyMount.ps1" -Configuration "%CONFIGURATION%"
set "RESULT=%ERRORLEVEL%"
echo.
if not "%RESULT%"=="0" echo Update failed. Review the error above.
exit /b %RESULT%
