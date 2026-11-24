<#
.SYNOPSIS
    Replaces Dynamo's program files with a new version, then checks it came up.

.DESCRIPTION
    Started by the running service (UpdateInstaller), as the same account, and
    detached from it: the service is stopped from here. In order:

      1. waits a moment so the page that asked for the update is delivered;
      2. copies the current program files to <DataDir>\update\backup;
      3. stops the service, copies the database file aside;
      4. replaces the program files with the new ones (robocopy /MIR);
      5. starts the service and waits for the site to answer;
      6. if anything fails, puts the old files (and the database copy) back and
         starts the old version again.

    The service itself (its name, account, start type, port and other settings,
    the firewall rule) is never touched. Everything is written to -Log, and
    <DataDir>\update\last-result.txt says "ok ..." or "failed ...".

    -NoService (tests only) skips stopping and starting the service.
#>
param(
    [Parameter(Mandatory)] [int] $ServicePid,
    [Parameter(Mandatory)] [string] $Source,
    [Parameter(Mandatory)] [string] $InstallDir,
    [Parameter(Mandatory)] [string] $DataDir,
    [int] $Port = 4040,
    [string] $OldVersion = '',
    [string] $NewVersion = '',
    [string] $Log = '',
    [string] $HealthUrl = '',
    [int] $HealthSeconds = 90,
    [int] $DelaySeconds = 3,
    [switch] $NoService
)

$ErrorActionPreference = 'Stop'
if (-not $Log) { $Log = Join-Path $DataDir 'update.log' }
if (-not $HealthUrl) { $HealthUrl = "http://localhost:$Port/Bookmarks" }
$updateDir = Join-Path $DataDir 'update'
$backup = Join-Path $updateDir 'backup'
$resultFile = Join-Path $updateDir 'last-result.txt'
$dbFile = Join-Path $DataDir 'dynamo.db'
$dbCopy = Join-Path $updateDir 'dynamo.db.before-update'

function Write-Log([string] $message) {
    ('{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $message) | Add-Content -Path $Log -Encoding UTF8
}

function Copy-Tree([string] $from, [string] $to) {
    $null = New-Item -ItemType Directory -Force -Path $to
    & robocopy $from $to /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    # robocopy: 0..7 are success codes, 8 and above are failures.
    if ($LASTEXITCODE -ge 8) { throw "Copying files failed (robocopy code $LASTEXITCODE)." }
}

function Wait-Site {
    $deadline = (Get-Date).AddSeconds($HealthSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest $HealthUrl -UseBasicParsing -TimeoutSec 3
            if ($r.StatusCode -eq 200) { return $true }
        }
        catch { }
        Start-Sleep -Seconds 1
    }
    return $false
}

$serviceName = $null
$stopped = $false
$swapped = $false
try {
    Write-Log "=== Update $OldVersion -> $NewVersion ==="
    if (-not (Test-Path (Join-Path $Source 'Dynamo.exe'))) { throw "No Dynamo.exe in $Source." }
    if (-not $NoService) {
        $serviceName = (Get-CimInstance Win32_Service -Filter "ProcessId=$ServicePid" |
            Select-Object -First 1).Name
        if (-not $serviceName) { throw "No Windows service runs process $ServicePid." }
        Write-Log "Service: $serviceName"
    }

    Start-Sleep -Seconds $DelaySeconds

    $null = New-Item -ItemType Directory -Force -Path $updateDir
    Write-Log 'Backing up the current program files...'
    Copy-Tree $InstallDir $backup

    if ($serviceName) {
        Write-Log 'Stopping the service...'
        Stop-Service -Name $serviceName -Force
        (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    }
    $stopped = $true
    if (Test-Path $dbFile) { Copy-Item $dbFile $dbCopy -Force }

    Write-Log 'Replacing the program files...'
    $swapped = $true
    Copy-Tree $Source $InstallDir

    if ($serviceName) {
        Write-Log 'Starting the service...'
        Start-Service -Name $serviceName
    }
    if (-not (Wait-Site)) { throw "The new version did not answer at $HealthUrl within $HealthSeconds s." }

    Write-Log "Updated to $NewVersion."
    Set-Content -Path $resultFile -Value "ok $NewVersion" -Encoding UTF8
    Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $updateDir 'files') -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem $updateDir -Filter '*.zip' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}
catch {
    $reason = $_.Exception.Message
    Write-Log "FAILED: $reason"
    try {
        if ($swapped) {
            Write-Log 'Putting the old version back...'
            if ($serviceName) { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue }
            if ($serviceName) { (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60)) }
            Copy-Tree $backup $InstallDir
            if (Test-Path $dbCopy) { Copy-Item $dbCopy $dbFile -Force }
        }
        if ($serviceName -and $stopped) {
            Start-Service -Name $serviceName
            Write-Log 'The old version was started again.'
        }
        Set-Content -Path $resultFile -Value "failed: version $NewVersion was not installed ($reason). The previous version is running again." -Encoding UTF8
    }
    catch {
        Write-Log "ROLLBACK FAILED: $($_.Exception.Message)"
        Set-Content -Path $resultFile -Value "failed: version $NewVersion was not installed ($reason), and putting the old version back failed too: $($_.Exception.Message). Backup: $backup" -Encoding UTF8
    }
    exit 1
}
