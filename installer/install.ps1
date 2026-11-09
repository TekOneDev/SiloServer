# Silo Server installer (beta). Run "Install Silo Server.cmd" rather than this file directly.
# Installs to Program Files\Silo Server, registers the SiloServer Windows service and opens the search page.
#   -Lan        also listen on the network (port 47820) and open the Windows firewall for it
#   -Library    folder of the public mining library (default D:\PublicMiningData), first install only
param(
    [switch]$Elevated,
    [switch]$Lan,
    [string]$Library,
    [string]$Source
)
$ErrorActionPreference = 'Stop'
if (-not $Source) { $Source = $PSScriptRoot }
$Dest = Join-Path $env:ProgramFiles 'Silo Server'
$Data = Join-Path $env:ProgramData 'SiloServer'
$ServiceName = 'SiloServer'
$Port = 47820
$Version = '0.1.0'

function Step($text) { Write-Host "  - $text" -ForegroundColor Cyan }

if (-not $Elevated) {
    Write-Host ""
    Write-Host "Installing Silo Server $Version" -ForegroundColor White
    Write-Host "Windows will ask for permission to install the Silo Server service."
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated', '-Source', "`"$Source`"")
    if ($Lan) { $argList += '-Lan' }
    if ($Library) { $argList += @('-Library', "`"$Library`"") }
    try { $p = Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -Wait -PassThru }
    catch { Write-Host "Installation was cancelled." -ForegroundColor Yellow; exit 1 }
    if ($p.ExitCode -ne 0) { Write-Host "Installation failed (code $($p.ExitCode)). See $Data\install.log" -ForegroundColor Red; exit $p.ExitCode }
    Start-Process "http://localhost:$Port/"
    Write-Host ""
    Write-Host "Silo Server is installed and indexing. Search at http://localhost:$Port/" -ForegroundColor Green
    Write-Host "Server status and settings: http://localhost:$Port/admin"
    exit 0
}

# Part 2, elevated.
New-Item -ItemType Directory -Force -Path $Data | Out-Null
Start-Transcript -Path (Join-Path $Data 'install.log') -Force | Out-Null
try {
    Step 'Stopping any running copy'
    if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
        Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
        for ($i = 0; $i -lt 120 -and (Get-Process -Name 'SiloServer' -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
        # Never start the new version while the old one is still closing its index.
        Get-Process -Name 'SiloServer', 'Silo.PluginHost' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }

    Step "Copying files to $Dest"
    New-Item -ItemType Directory -Force -Path $Dest | Out-Null
    $target = Join-Path $Dest 'App'
    # (A customised rules file belongs in $Data\rules.json, which upgrades leave alone.)
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Copy-Item (Join-Path $Source 'App') $target -Recurse -Force
    Copy-Item (Join-Path $Source 'uninstall.ps1') $Dest -Force
    Copy-Item (Join-Path $Source 'README.md') $Dest -Force -ErrorAction SilentlyContinue
    Get-ChildItem $Dest -Recurse -File | Unblock-File

    Step 'Writing settings'
    $settingsPath = Join-Path $Data 'settings.json'
    # First install: the server writes its defaults, then this adjusts them.
    if (-not (Test-Path $settingsPath)) { & (Join-Path $target 'SiloServer.exe') init | Out-Null }
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
    if ($Lan) { $settings | Add-Member -NotePropertyName listen -NotePropertyValue @("http://*:$Port") -Force }
    if ($Library) {
        $settings | Add-Member -NotePropertyName sources -NotePropertyValue @([pscustomobject]@{
            id = 'public-library'; name = 'Public mining library'; plugin = 'silo.source.folder'; enabled = $true; rescanMinutes = 360
            config = [pscustomobject]@{ root = $Library; access = 'public'; sidecars = $true; exclude = @('_state', '_reference', '*.part', '*.tmp', '~$*', 'Thumbs.db', 'desktop.ini', 'library.csv') } }) -Force
    }
    if ($Lan -or $Library) { $settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding UTF8 }

    Step 'Registering the SiloServer service'
    $exe = Join-Path $target 'SiloServer.exe'
    if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
        New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'Silo Server' -StartupType Automatic | Out-Null
    } else {
        sc.exe config $ServiceName binPath= "`"$exe`"" | Out-Null
    }
    sc.exe config $ServiceName start= delayed-auto | Out-Null
    sc.exe description $ServiceName "Indexes mining reports and technical files, and serves Silo search at http://localhost:$Port/." | Out-Null
    sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

    if ($Lan) {
        Step "Opening the firewall for port $Port (domain and private networks)"
        Get-NetFirewallRule -DisplayName 'Silo Server' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        New-NetFirewallRule -DisplayName 'Silo Server' -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile Domain, Private | Out-Null
    }

    Step 'Starting the service'
    Start-Service $ServiceName
    $ok = $false
    for ($i = 0; $i -lt 60 -and -not $ok; $i++) {
        Start-Sleep -Milliseconds 500
        try { $r = Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 "http://localhost:$Port/api/v1/"; $ok = $r.StatusCode -eq 200 } catch { }
    }
    if (-not $ok) { throw "The service did not answer on port $Port. See $Data\logs and the Windows Event Viewer (Application log)." }

    Step 'Adding Start menu shortcuts'
    $menu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
    $sh = New-Object -ComObject WScript.Shell
    foreach ($s in @(@('Silo Search', "http://localhost:$Port/"), @('Silo Server status', "http://localhost:$Port/admin"))) {
        $lnk = $sh.CreateShortcut((Join-Path $menu ($s[0] + '.url')))
        $lnk.TargetPath = $s[1]
        $lnk.Save()
    }

    Step 'Registering uninstaller'
    $un = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SiloServer'
    New-Item -Path $un -Force | Out-Null
    Set-ItemProperty $un DisplayName 'Silo Server (beta)'
    Set-ItemProperty $un Publisher 'Silo'
    Set-ItemProperty $un DisplayVersion $Version
    Set-ItemProperty $un DisplayIcon $exe
    Set-ItemProperty $un UninstallString "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$Dest\uninstall.ps1`""
    Set-ItemProperty $un NoModify 1
    Set-ItemProperty $un NoRepair 1

    Write-Host "Done." -ForegroundColor Green
    Stop-Transcript | Out-Null
    exit 0
}
catch {
    Write-Host $_ -ForegroundColor Red
    Stop-Transcript | Out-Null
    Read-Host "Installation failed. Press Enter to close"
    exit 1
}
