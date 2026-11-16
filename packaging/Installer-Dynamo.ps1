<#
.SYNOPSIS
    Installe Dynamo comme service Windows, démarré automatiquement avec Windows.

.DESCRIPTION
    À lancer depuis le dossier extrait de l'archive (de préférence par
    Installer.cmd). Le script demande lui-même les droits d'administrateur
    (fenêtre UAC).

    Le service s'exécute sous le compte LocalSystem : c'est ce qui lui permet
    d'ouvrir les fichiers, dossiers et liens des signets sur le bureau de
    l'utilisateur connecté.

    Relancer le script met Dynamo à jour : le service est remplacé, les données
    (C:\dynamo par défaut) sont conservées.

.PARAMETER DossierInstallation
    Où copier le programme. Par défaut : C:\Program Files\Dynamo.

.PARAMETER Port
    Port http du site. Par défaut : 4040.

.PARAMETER PortHttps
    Port https (certificat créé par Dynamo lui-même). Par défaut : 4343; 0 le
    désactive.

.PARAMETER DossierDonnees
    Dossier de la base de données. Par défaut : C:\dynamo.

.PARAMETER NomService
    Nom du service Windows. Par défaut : Dynamo.
#>
[CmdletBinding()]
param(
    [string] $DossierInstallation = 'C:\Program Files\Dynamo',
    [int] $Port = 4040,
    [int] $PortHttps = 4343,
    [string] $DossierDonnees = '',
    [string] $NomService = 'Dynamo',
    # Accès depuis un autre ordinateur : ouvre le port dans le Pare-feu Windows.
    # Sans cette option, seul cet ordinateur peut se connecter.
    [switch] $OuvrirPareFeu,
    [string] $ProfilsPareFeu = 'Domain,Private',        # Any si le réseau est « Public »
    [string] $AdressesPareFeu = 'LocalSubnet'           # ou l'adresse (ou la plage) de votre portable
)

$ErrorActionPreference = 'Stop'

# Dossier de ce script et son chemin complet. $PSScriptRoot peut être vide selon la
# façon dont PowerShell a été lancé : on se rabat sur le chemin du script, puis sur
# le dossier courant (les .cmd se placent dans leur dossier avant de commencer).
$cheminScript = if ($PSCommandPath) { $PSCommandPath } else { $MyInvocation.MyCommand.Path }
$racine = $PSScriptRoot
if (-not $racine -and $cheminScript) { $racine = Split-Path -Parent $cheminScript }
if (-not $racine) { $racine = (Get-Location).Path }
if (-not $cheminScript) { $cheminScript = Join-Path $racine $MyInvocation.MyCommand.Name }

# Relance le script en administrateur au besoin, en gardant la fenêtre ouverte
# pour que le résultat reste lisible.
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
    Write-Host '=== Installation de Dynamo ===' -ForegroundColor Cyan

    $source = Join-Path $racine 'app'
    if (-not (Test-Path (Join-Path $source 'Dynamo.exe'))) {
        throw "Le dossier 'app' est introuvable à côté de ce script. Extrayez l'archive au complet avant d'installer."
    }

    # Des fichiers reçus par courriel ou téléchargés peuvent être marqués
    # « provenant d'Internet » : on retire la marque.
    Get-ChildItem $racine -Recurse -File -ErrorAction SilentlyContinue |
        Unblock-File -ErrorAction SilentlyContinue

    # Mise à jour : on retire l'ancien service (les données ne sont pas touchées).
    if (Get-Service $NomService -ErrorAction SilentlyContinue) {
        Write-Host "Arrêt et retrait du service '$NomService' existant..."
        Stop-Service $NomService -Force -ErrorAction SilentlyContinue
        sc.exe delete $NomService | Out-Null
        for ($i = 0; $i -lt 20 -and (Get-Service $NomService -ErrorAction SilentlyContinue); $i++) {
            Start-Sleep -Milliseconds 500
        }
        Start-Sleep -Seconds 1
    }

    # Le port doit être libre.
    $occupe = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    if ($occupe) {
        throw "Le port $Port est déjà utilisé par un autre programme. Relancez avec un autre port, par exemple : .\Installer-Dynamo.ps1 -Port 4041"
    }

    if ($PortHttps -gt 0 -and (Get-NetTCPConnection -LocalPort $PortHttps -State Listen -ErrorAction SilentlyContinue)) {
        throw "Le port https $PortHttps est déjà utilisé par un autre programme. Relancez avec un autre port, par exemple : .\Installer-Dynamo.ps1 -PortHttps 4444 (ou -PortHttps 0 pour désactiver https)"
    }

    Write-Host "Copie des fichiers vers $DossierInstallation..."
    $null = New-Item -ItemType Directory -Force -Path $DossierInstallation
    robocopy $source $DossierInstallation /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "La copie des fichiers a échoué (robocopy, code $LASTEXITCODE)." }

    New-Service -Name $NomService `
        -BinaryPathName "`"$DossierInstallation\Dynamo.exe`"" `
        -DisplayName $NomService `
        -Description 'Tableau de bord local : signets, connexions Oracle, requêtes, espaces de travail.' `
        -StartupType Automatic | Out-Null

    # Réglages propres à ce service (n'affectent aucun autre programme).
    $environnement = @()
    if ($Port -ne 4040) { $environnement += "DYNAMO_PORT=$Port" }
    if ($PortHttps -ne 4343) { $environnement += "DYNAMO_HTTPS_PORT=$PortHttps" }
    if ($DossierDonnees) { $environnement += "DYNAMO_HOME=$DossierDonnees" }
    if ($environnement.Count -gt 0) {
        New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$NomService" `
            -Name Environment -PropertyType MultiString -Value $environnement -Force | Out-Null
    }

    # Redémarrage automatique en cas d'arrêt inattendu (5 s, 30 s, puis 60 s).
    sc.exe failure $NomService reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null

    Write-Host 'Démarrage du service...'
    try {
        Start-Service $NomService
    }
    catch {
        throw "Le service n'a pas démarré : $($_.Exception.Message). Consultez le journal des événements Windows (Application, source Dynamo)."
    }

    # Autres ordinateurs : règle entrante pour le port (sur demande seulement).
    $regle = "$NomService (accès distant)"
    $ports = @($Port) + $(if ($PortHttps -gt 0) { @($PortHttps) } else { @() })
    Get-NetFirewallRule -DisplayName $regle -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    if ($OuvrirPareFeu) {
        New-NetFirewallRule -DisplayName $regle -Direction Inbound -Action Allow -Protocol TCP `
            -LocalPort $ports -Profile $ProfilsPareFeu.Split(',') -RemoteAddress $AdressesPareFeu | Out-Null
        if (Get-NetFirewallRule -DisplayName $regle -ErrorAction SilentlyContinue) {
            Write-Host "Pare-feu : port(s) $($ports -join ', ') ouvert(s) à $AdressesPareFeu ($ProfilsPareFeu). Définissez un mot de passe dans Settings pour autoriser les connexions."
        }
        else {
            Write-Warning "La règle du pare-feu n'a pas pu être vérifiée après sa création : une stratégie de votre organisation la bloque peut-être. Demandez à votre service informatique d'autoriser les ports $($ports -join ', ') en entrée."
        }
    }

    $adresse = "http://localhost:$Port"
    for ($i = 0; $i -lt 30; $i++) {
        try {
            $reponse = Invoke-WebRequest "$adresse/Bookmarks" -UseBasicParsing -TimeoutSec 3
            Write-Host ''
            Write-Host "Dynamo est installé et fonctionne : $adresse" -ForegroundColor Green
            Write-Host 'Il démarrera automatiquement avec Windows.'
            Write-Host "Données : $(if ($DossierDonnees) { $DossierDonnees } else { 'C:\dynamo' })"
            Start-Process $adresse
            return
        }
        catch { Start-Sleep -Seconds 1 }
    }
    Write-Warning "Le service est démarré mais le site ne répond pas encore sur $adresse. Patientez un moment, puis réessayez; au besoin, consultez le journal des événements Windows (Application, source Dynamo)."
}
catch {
    Write-Host ''
    Write-Host "ERREUR : $($_.Exception.Message)" -ForegroundColor Red
}
