# Removes Silo Server. The index and settings in C:\ProgramData\SiloServer are kept unless you add -RemoveData.
param([switch]$RemoveData)
$ErrorActionPreference = 'Continue'
$id = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $id.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($RemoveData) { $a += '-RemoveData' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $a
    exit
}
$Dest = Join-Path $env:ProgramFiles 'Silo Server'
$Data = Join-Path $env:ProgramData 'SiloServer'
Write-Host "Removing Silo Server"
if (Get-Service SiloServer -ErrorAction SilentlyContinue) {
    Stop-Service SiloServer -Force
    for ($i = 0; $i -lt 60 -and (Get-Process -Name 'SiloServer' -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
    Get-Process -Name 'SiloServer', 'Silo.PluginHost' -ErrorAction SilentlyContinue | Stop-Process -Force
    sc.exe delete SiloServer | Out-Null
}
Get-NetFirewallRule -DisplayName 'Silo Server' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
$menu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
Remove-Item (Join-Path $menu 'Silo Search.url'), (Join-Path $menu 'Silo Server status.url') -ErrorAction SilentlyContinue
Remove-Item 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SiloServer' -Recurse -ErrorAction SilentlyContinue
Set-Location $env:TEMP
Remove-Item $Dest -Recurse -Force -ErrorAction SilentlyContinue
if ($RemoveData) { Remove-Item $Data -Recurse -Force -ErrorAction SilentlyContinue; Write-Host "Removed the index and settings." }
else { Write-Host "Kept the index and settings in $Data (run with -RemoveData to delete them)." }
Write-Host "Silo Server is removed." -ForegroundColor Green
Start-Sleep -Seconds 3
