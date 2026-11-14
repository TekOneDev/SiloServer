@echo off
rem Installs Silo Server (beta). Windows will ask for administrator permission once.
rem Add  -Lan  (other PCs may use it),  -Https  (encrypted), or  -Account CORP\svc-silo$  after install.ps1 below.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
