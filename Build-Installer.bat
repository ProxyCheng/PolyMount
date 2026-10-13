@echo off
setlocal
set "VERSION=%~1"
if not defined VERSION set "VERSION=0.2.0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-Installer.ps1" -Version "%VERSION%"
set "RESULT=%ERRORLEVEL%"
echo.
exit /b %RESULT%
