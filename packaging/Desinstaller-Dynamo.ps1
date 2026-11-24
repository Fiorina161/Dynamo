<#
.SYNOPSIS
    Désinstalle Dynamo : retire le service et les fichiers du programme.

.DESCRIPTION
    Les données (bookmarks, connexions, requêtes... dans C:\dynamo par défaut)
    ne sont PAS supprimées : réinstaller Dynamo plus tard les retrouvera. Pour
    tout effacer, supprimez vous-même le dossier de données.

.PARAMETER DossierInstallation
    Dossier du programme. Par défaut : C:\Program Files\Dynamo.

.PARAMETER NomService
    Nom du service Windows. Par défaut : Dynamo.
#>
[CmdletBinding()]
param(
    [string] $DossierInstallation = 'C:\Program Files\Dynamo',
    [string] $NomService = 'Dynamo'
)

$ErrorActionPreference = 'Stop'

# Chemin de ce script ($PSCommandPath peut être vide selon le lancement).
$cheminScript = if ($PSCommandPath) { $PSCommandPath } else { $MyInvocation.MyCommand.Path }

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Les droits d''administrateur sont nécessaires : acceptez la fenêtre qui va s''ouvrir.'
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit', '-File', "`"$cheminScript`"")
    foreach ($key in $PSBoundParameters.Keys) {
        $arguments += "-$key"
        $arguments += "`"$($PSBoundParameters[$key])`""
    }
    Start-Process (Get-Process -Id $PID).Path -Verb RunAs -ArgumentList $arguments
    return
}

try {
    Write-Host ''
    Write-Host '=== Désinstallation de Dynamo ===' -ForegroundColor Cyan

    Get-NetFirewallRule -DisplayName "$NomService (accès distant)" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    if (Get-Service $NomService -ErrorAction SilentlyContinue) {
        Stop-Service $NomService -Force -ErrorAction SilentlyContinue
        sc.exe delete $NomService | Out-Null
        for ($i = 0; $i -lt 20 -and (Get-Service $NomService -ErrorAction SilentlyContinue); $i++) {
            Start-Sleep -Milliseconds 500
        }
        Write-Host "Le service '$NomService' a été retiré."
    }
    else {
        Write-Host "Le service '$NomService' n'est pas installé."
    }

    if (Test-Path $DossierInstallation) {
        Start-Sleep -Seconds 1
        Remove-Item $DossierInstallation -Recurse -Force
        Write-Host "Les fichiers du programme ont été supprimés ($DossierInstallation)."
    }

    Write-Host ''
    Write-Host 'Vos données n''ont pas été touchées (C:\dynamo par défaut).' -ForegroundColor Green
}
catch {
    Write-Host ''
    Write-Host "ERREUR : $($_.Exception.Message)" -ForegroundColor Red
}
