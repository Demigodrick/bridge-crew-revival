@echo off
rem Bridge Crew Revival installer: double-click to install or update.
rem   install.cmd "D:\Games\Star Trek Bridge Crew"   (if the game isn't found automatically)
rem   install.cmd -Uninstall
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
