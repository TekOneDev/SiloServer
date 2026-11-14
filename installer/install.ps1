# Silo Server installer (beta). Run "Install Silo Server.cmd" rather than this file directly.
# Installs to Program Files\Silo Server, registers the SiloServer Windows service and opens the search page.
#   -Lan          also listen on the network (port 47820) and open the Windows firewall for it
#   -Https        serve HTTPS on port 47821 (with -Lan, other PCs are sent there). Makes a "Silo Server" certificate
#                 for this computer unless -Certificate names one already in the computer's store (thumbprint)
#   -Account      account the service runs as (0.1.5). Default: its own virtual account, NT SERVICE\SiloServer.
#                 Use a group managed service account (DOMAIN\svc-silo$) or a domain account (asks for its password)
#                 to index shares on other servers. LocalSystem is accepted but not recommended.
#   -Library      folder of the public mining library (default D:\PublicMiningData), first install only
param(
    [switch]$Elevated,
    [switch]$Lan,
    [switch]$Https,
    [string]$Certificate,
    [string]$Account = 'NT SERVICE\SiloServer',
    [string]$Library,
    [string]$Source
)
$ErrorActionPreference = 'Stop'
if (-not $Source) { $Source = $PSScriptRoot }
$Dest = Join-Path $env:ProgramFiles 'Silo Server'
$Data = Join-Path $env:ProgramData 'SiloServer'
$ServiceName = 'SiloServer'
$Port = 47820
$HttpsPort = 47821
$Version = '0.1.6'

function Step($text) { Write-Host "  - $text" -ForegroundColor Cyan }

# Gives an account the "Log on as a service" right (sc.exe does not, unlike services.msc).
function Grant-ServiceLogon([string]$name) {
    $sid = (New-Object Security.Principal.NTAccount($name)).Translate([Security.Principal.SecurityIdentifier])
    if (-not ('SiloLsa' -as [type])) {
        Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class SiloLsa {
  [StructLayout(LayoutKind.Sequential)] struct US { public ushort L, M; [MarshalAs(UnmanagedType.LPWStr)] public string B; }
  [StructLayout(LayoutKind.Sequential)] struct OA { public int Len; public IntPtr R, N; public uint A; public IntPtr D, Q; }
  [DllImport("advapi32.dll")] static extern uint LsaOpenPolicy(IntPtr s, ref OA a, uint acc, out IntPtr h);
  [DllImport("advapi32.dll")] static extern uint LsaAddAccountRights(IntPtr h, byte[] sid, US[] r, uint n);
  [DllImport("advapi32.dll")] static extern uint LsaClose(IntPtr h);
  [DllImport("advapi32.dll")] static extern int LsaNtStatusToWinError(uint s);
  public static int Grant(byte[] sid, string right) {
    var oa = new OA(); IntPtr h; uint st = LsaOpenPolicy(IntPtr.Zero, ref oa, 0x811, out h);
    if (st != 0) return LsaNtStatusToWinError(st);
    var r = new US { B = right, L = (ushort)(right.Length * 2), M = (ushort)(right.Length * 2 + 2) };
    st = LsaAddAccountRights(h, sid, new[] { r }, 1); LsaClose(h); return LsaNtStatusToWinError(st);
  }
}
'@
    }
    $bytes = New-Object byte[] $sid.BinaryLength
    $sid.GetBinaryForm($bytes, 0)
    $rc = [SiloLsa]::Grant($bytes, 'SeServiceLogonRight')
    if ($rc -ne 0) { throw "Could not give $name the right to log on as a service (error $rc)." }
}

# Read access for the service account to a local folder (shares are set on the file server).
function Grant-Read([string]$path, [string]$who) {
    if (-not $path -or $path.StartsWith('\\') -or -not (Test-Path $path)) { return }
    # Already there (an upgrade): skip, so a very large folder is not walked again.
    try { if ((Get-Acl $path).Access | Where-Object { $_.IdentityReference.Value -ieq $who -and $_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::ReadAndExecute }) { return } } catch { }
    # Inherited by everything below (a very large folder takes a while). Folders that block inheritance keep their own permissions.
    & icacls.exe $path /grant "${who}:(OI)(CI)RX" /C /Q | Out-Null
}

# The private key of the HTTPS certificate must be readable by the service account.
function Grant-KeyRead($cert, [string]$who) {
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
    $name = $null
    if ($rsa -is [Security.Cryptography.RSACng]) { $name = $rsa.Key.UniqueName }
    elseif ($rsa) { $name = $rsa.CspKeyContainerInfo.UniqueKeyContainerName }
    if (-not $name) { throw "Could not find the private key of certificate $($cert.Thumbprint)." }
    $paths = @((Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$name"), (Join-Path $env:ProgramData "Microsoft\Crypto\RSA\MachineKeys\$name"))
    $file = $paths | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $file) { throw "The private key file of certificate $($cert.Thumbprint) was not found." }
    & icacls.exe $file /grant "${who}:R" | Out-Null
}

if (-not $Elevated) {
    Write-Host ""
    Write-Host "Installing Silo Server $Version" -ForegroundColor White
    Write-Host "Windows will ask for permission to install the Silo Server service."
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated', '-Source', "`"$Source`"")
    if ($Lan) { $argList += '-Lan' }
    if ($Library) { $argList += @('-Library', "`"$Library`"") }
    if ($Https) { $argList += '-Https' }
    if ($Certificate) { $argList += @('-Certificate', "`"$Certificate`"") }
    if ($Account) { $argList += @('-Account', "`"$Account`"") }
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
# Settings, plugins and the index: SYSTEM and Administrators only (the service checks this again at every start).
& icacls.exe $Data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
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
    if ($Https) {
        Step 'Setting up HTTPS'
        $cert = $null
        if ($Certificate) {
            $thumb = ($Certificate -replace '[\s:]', '')
            $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Thumbprint -eq $thumb } | Select-Object -First 1
            if (-not $cert) { throw "No certificate with thumbprint $thumb in the computer's store (Cert:\LocalMachine\My)." }
            if (-not $cert.HasPrivateKey) { throw "Certificate $thumb has no private key." }
        } else {
            $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.FriendlyName -eq 'Silo Server' -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(60) } |
                Sort-Object NotAfter -Descending | Select-Object -First 1
            if (-not $cert) {
                $names = @($env:COMPUTERNAME, 'localhost')
                try { $fqdn = [Net.Dns]::GetHostEntry($env:COMPUTERNAME).HostName; if ($fqdn -and $names -notcontains $fqdn) { $names = @($fqdn) + $names } } catch { }
                $cert = New-SelfSignedCertificate -DnsName $names -CertStoreLocation Cert:\LocalMachine\My -FriendlyName 'Silo Server' `
                    -NotAfter (Get-Date).AddYears(5) -KeyAlgorithm RSA -KeyLength 3072 -KeyExportPolicy NonExportable `
                    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.1')
                Write-Host "    Made a certificate for $($names -join ', ') (valid until $($cert.NotAfter.ToString('yyyy-MM-dd')))."
                # Trusted on this computer; other PCs need it too (Group Policy, or import silo-server.cer).
                $root = New-Object Security.Cryptography.X509Certificates.X509Store('Root', 'LocalMachine')
                $root.Open('ReadWrite'); $root.Add([Security.Cryptography.X509Certificates.X509Certificate2]::new($cert.RawData)); $root.Close()
            }
            Export-Certificate -Cert $cert -FilePath (Join-Path $Dest 'silo-server.cer') -Force | Out-Null
            Write-Host "    Other PCs must trust it: deploy $Dest\silo-server.cer by Group Policy, or use a certificate from your company CA with -Certificate."
        }
        $listen = @("https://*:$HttpsPort", $(if ($Lan) { "http://*:$Port" } else { "http://localhost:$Port" }))
        $settings | Add-Member -NotePropertyName listen -NotePropertyValue $listen -Force
        $settings | Add-Member -NotePropertyName certificate -NotePropertyValue $(if ($Certificate) { $cert.Thumbprint } else { $null }) -Force
    }
    if ($Lan -or $Library -or $Https) { $settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding UTF8 }

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

    # 0.1.5: the service runs as its own account, not LocalSystem.
    Step "Running the service as $Account"
    # Its own service identity (NT SERVICE\SiloServer) is in its token whatever the account, so permissions can name it.
    sc.exe sidtype $ServiceName unrestricted | Out-Null
    $svcId = "NT SERVICE\$ServiceName"
    $password = ''
    if ($Account -eq 'LocalSystem') {
        Write-Host "    LocalSystem can do anything on this computer; a plugin bug would too. Prefer the default account." -ForegroundColor Yellow
    } elseif ($Account -ieq $svcId) {
        # Virtual account: no password, few rights; on the network it acts as this computer's account.
    } elseif ($Account.EndsWith('$')) {
        # Group managed service account: Windows keeps the password.
        if (Get-Command Test-ADServiceAccount -ErrorAction SilentlyContinue) {
            if (-not (Test-ADServiceAccount ($Account.Split('\')[-1].TrimEnd('$')))) { throw "$Account cannot be used on this computer. Allow it with Set-ADServiceAccount -PrincipalsAllowedToRetrieveManagedPassword, then try again." }
        }
        Grant-ServiceLogon $Account
    } else {
        $cred = Get-Credential -UserName $Account -Message "Password for $Account, the account Silo Server will run as"
        if (-not $cred) { throw 'No password given.' }
        Grant-ServiceLogon $Account
        $password = $cred.GetNetworkCredential().Password
    }
    $svc = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
    $res = Invoke-CimMethod -InputObject $svc -MethodName Change -Arguments @{ StartName = $(if ($Account -eq 'LocalSystem') { 'LocalSystem' } else { $Account }); StartPassword = $password }
    if ($res.ReturnValue -ne 0) { throw "Could not set the service account to $Account (Win32_Service.Change returned $($res.ReturnValue))." }

    # Data folder: SYSTEM, Administrators and the service. Program folder and the library: read.
    & icacls.exe $Data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "${svcId}:(OI)(CI)M" | Out-Null
    if ($Account -ne 'LocalSystem' -and $Account -ine $svcId) { & icacls.exe $Data /grant "${Account}:(OI)(CI)M" | Out-Null }
    Grant-Read $Dest $svcId
    $unc = @()
    foreach ($src in @($settings.sources)) {
        $r = $src.config.root
        if (-not $r) { continue }
        if ($r.StartsWith('\\')) { $unc += $r; continue }
        if ($Account -ne 'LocalSystem') { Step "Letting the service read $r"; Grant-Read $r $svcId }
    }
    if ($unc.Count -gt 0 -and $Account -ieq $svcId) {
        Write-Host "    Shares on other servers are read as this computer's account ($env:COMPUTERNAME`$). Give it Read on: $($unc -join ', ')," -ForegroundColor Yellow
        Write-Host "    or install with -Account DOMAIN\svc-silo`$ (a managed service account that can read them)." -ForegroundColor Yellow
    }
    if ($Https -and $cert) { Grant-KeyRead $cert $svcId }

    if ($Lan -or $Https) {
        Get-NetFirewallRule -DisplayName 'Silo Server*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    }
    if ($Lan) {
        Step "Opening the firewall for port $Port (domain and private networks)"
        New-NetFirewallRule -DisplayName 'Silo Server' -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile Domain, Private | Out-Null
        if (-not $Https) { Write-Host "    Searches cross the network unencrypted. Add -Https to encrypt them." -ForegroundColor Yellow }
    }
    if ($Https) {
        Step "Opening the firewall for HTTPS port $HttpsPort (domain and private networks)"
        New-NetFirewallRule -DisplayName 'Silo Server (HTTPS)' -Direction Inbound -Protocol TCP -LocalPort $HttpsPort -Action Allow -Profile Domain, Private | Out-Null
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
