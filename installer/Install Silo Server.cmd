@echo off
rem Installs Silo Server (beta). Windows will ask for administrator permission once.
rem Add  -Lan  after install.ps1 below to let other PCs on the network use it.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
