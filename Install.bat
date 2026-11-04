@echo off
rem Double-click to install or update CMS21 Together+.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
