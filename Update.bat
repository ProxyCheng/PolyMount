@echo off
setlocal
rem Double-click to update PolyMount, or run: Update.bat [Debug|Release] [-Force] [-TrayOnly]
rem
rem Only what changed is restarted: the published service, plugins and tray are compared with
rem what is deployed first, and a build that changed only the tray leaves the service, the
rem instance workers and their mounts running. -Force restarts both regardless; -TrayOnly never
rem touches the service (and skips publishing it).
set "CONFIGURATION=Debug"
set "SWITCHES="
:parse
if "%~1"=="" goto parsed
if /i "%~1"=="Debug"   (set "CONFIGURATION=Debug"   & shift & goto parse)
if /i "%~1"=="Release" (set "CONFIGURATION=Release" & shift & goto parse)
set "SWITCHES=%SWITCHES% %1"
shift
goto parse
:parsed
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Update-PolyMount.ps1" -Configuration "%CONFIGURATION%"%SWITCHES%
set "RESULT=%ERRORLEVEL%"
echo.
if not "%RESULT%"=="0" echo Update failed. Review the error above.
exit /b %RESULT%
