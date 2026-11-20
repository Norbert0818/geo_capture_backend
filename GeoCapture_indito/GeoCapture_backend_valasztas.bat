@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0geocapture_dev.ps1" -ChooseBackend -SetupOnly
set "geoLauncherExit=%errorlevel%"
if not "%geoLauncherExit%"=="0" pause
exit /b %geoLauncherExit%
