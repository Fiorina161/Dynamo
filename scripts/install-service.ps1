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

    A service never runs in your interactive session, so a bookmark that
    opens a file or folder shows nothing on your desktop. For that, run the
    console (dotnet run / Dynamo.exe) instead; stop the service first, both
    use port 4040.

    If DYNAMO_HOME is defined as a *machine* environment variable the service
    uses it; otherwise its data lives in C:\dynamo.
#>
[CmdletBinding()]
param(
    [string] $InstallDir = 'C:\Program Files\Dynamo',
    [switch] $LocalSystem
)

$ErrorActionPreference = 'Stop'
$name = 'Dynamo'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()

# Re-launch elevated when needed, keeping the window open to show the result.
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $self = @('-NoProfile', '-NoExit', '-File', "`"$PSCommandPath`"", '-InstallDir', "`"$InstallDir`"")
    if ($LocalSystem) { $self += '-LocalSystem' }
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
