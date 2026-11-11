<#
.SYNOPSIS
    Removes the Dynamo Windows service. Data in DYNAMO_HOME is left untouched.
#>
$ErrorActionPreference = 'Stop'
if (Get-Service Dynamo -ErrorAction SilentlyContinue) {
    Stop-Service Dynamo -Force
    sc.exe delete Dynamo | Out-Null
    Write-Host 'Dynamo service removed.'
}
else {
    Write-Host 'Dynamo service is not installed.'
}
