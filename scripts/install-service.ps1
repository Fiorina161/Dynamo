<#
.SYNOPSIS
    Publishes Dynamo and installs it as a Windows service that starts
    automatically with Windows.

.DESCRIPTION
    Run it from any PowerShell: it asks for elevation itself (UAC prompt).

    The service runs under the account you give (an administrator account, so
    tools it launches can be elevated). A prompt asks for the password. With
    -LocalSystem it runs as LocalSystem instead: no password, and it has no
    user profile (no per-user environment, no per-user tnsnames).

    A service runs in session 0, which has no desktop. Dynamo opens bookmarked
    files, folders and links in the signed-in user's own session (see
    DesktopLauncher), which requires the right a LocalSystem service has:
    prefer -LocalSystem if bookmarks must open on your screen. Under another
    account the bookmark answers with a message instead of opening.

    If DYNAMO_HOME is defined as a *machine* environment variable the service
    uses it; otherwise its data lives in C:\dynamo.
#>
[CmdletBinding()]
param(
    [string] $InstallDir = 'C:\Program Files\Dynamo',
    [switch] $LocalSystem,
    # Let other computers reach the site: opens the port in the Windows Firewall.
    # Without it only this computer can connect, whatever the password setting.
    [switch] $OpenFirewall,
    [string] $FirewallProfile = 'Domain,Private',      # use Any if the network is "Public"
    [string] $FirewallRemoteAddress = 'LocalSubnet'    # or the laptop's address / a range
)

$ErrorActionPreference = 'Stop'
$name = 'Dynamo'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()

# Re-launch elevated when needed, keeping the window open to show the result.
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $self = @('-NoProfile', '-NoExit', '-File', "`"$PSCommandPath`"", '-InstallDir', "`"$InstallDir`"")
    if ($LocalSystem) { $self += '-LocalSystem' }
    if ($OpenFirewall) {
        $self += '-OpenFirewall'
        $self += '-FirewallProfile'
        $self += "`"$FirewallProfile`""
        $self += '-FirewallRemoteAddress'
        $self += "`"$FirewallRemoteAddress`""
    }
    Start-Process (Get-Process -Id $PID).Path -Verb RunAs -ArgumentList $self
    return
}

$project = Join-Path $PSScriptRoot '..\src\Dynamo\Dynamo.csproj'

$credential = $null
if (-not $LocalSystem) {
    $credential = Get-Credential -UserName "$env:USERDOMAIN\$env:USERNAME" `
        -Message 'Administrator account the Dynamo service runs as'
}

if (Get-Service $name -ErrorAction SilentlyContinue) {
    Write-Host 'Removing the existing service...'
    Stop-Service $name -Force -ErrorAction SilentlyContinue
    sc.exe delete $name | Out-Null
    Start-Sleep 2
}

Write-Host "Publishing to $InstallDir..."
dotnet publish $project -c Release -o $InstallDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

$params = @{
    Name           = $name
    BinaryPathName = "`"$InstallDir\Dynamo.exe`""
    DisplayName    = 'Dynamo'
    Description    = 'Local developer dashboard (bookmarks, commands, queries).'
    StartupType    = 'Automatic'
}
if ($credential) { $params.Credential = $credential }
New-Service @params | Out-Null

# Restart on failure (after 5 s, then 30 s, then 60 s); counter reset daily.
sc.exe failure $name reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null

try {
    Start-Service $name
}
catch {
    Write-Warning "The service did not start: $($_.Exception.Message)"
    if ($credential) {
        Write-Warning ('If this is error 1069 (logon failure), the account lacks the ' +
            '"Log on as a service" right. Open services.msc, Dynamo > Properties > ' +
            'Log On, re-enter the password and press OK (that grants the right), ' +
            'then start the service.')
    }
    return
}

# Other computers: an inbound rule for the port (opt-in), nothing otherwise.
$ruleName = "$name (remote access)"
$portNumber = [Environment]::GetEnvironmentVariable('DYNAMO_PORT', 'Machine')
if (-not $portNumber) { $portNumber = 4040 }
$httpsNumber = [Environment]::GetEnvironmentVariable('DYNAMO_HTTPS_PORT', 'Machine')
if (-not $httpsNumber) { $httpsNumber = 4343 }
$ports = @($portNumber) + $(if ([int]$httpsNumber -gt 0) { @($httpsNumber) } else { @() })
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if ($OpenFirewall) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP `
        -LocalPort $ports -Profile $FirewallProfile.Split(',') -RemoteAddress $FirewallRemoteAddress | Out-Null
    if (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue) {
        Write-Host "Firewall: port(s) $($ports -join ', ') open to $FirewallRemoteAddress ($FirewallProfile). Set a password in Settings to allow logins."
    }
    else {
        Write-Warning "The firewall rule could not be verified after creating it: a policy of your organisation may block it. Ask IT to allow inbound ports $($ports -join ', ')."
    }
}

# Wait for the site to answer.
for ($i = 0; $i -lt 20; $i++) {
    try {
        $r = Invoke-WebRequest http://localhost:4040/Bookmarks -UseBasicParsing -TimeoutSec 3
        Write-Host "Dynamo is running at http://localhost:4040 (HTTP $($r.StatusCode)), starts automatically."
        return
    }
    catch { Start-Sleep 1 }
}
Write-Warning 'The service started but the site does not answer yet. Check the Windows Event Log (Application, source Dynamo).'
