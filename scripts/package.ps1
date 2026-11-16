<#
.SYNOPSIS
    Builds the zip to send to someone else: Dynamo published self-contained
    (no .NET needed on the target machine), the installer scripts and the
    French guide from packaging\.

.DESCRIPTION
    Output: Dynamo-<date>.zip in Downloads (or -Output). The data of this
    machine (C:\dynamo) is never included: only the program.

    The scripts and the guide are saved as UTF-8 with a byte-order mark, so
    Windows PowerShell 5.1 and Notepad read the French accents correctly.
#>
param(
    [string] $Output = (Join-Path $env:USERPROFILE 'Downloads')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Dynamo\Dynamo.csproj'
$date = Get-Date -Format 'yyyy-MM-dd'
# The release version, from the project file (the zip is Dynamo-<version>.zip and the
# GitHub release is tagged v<version>: the update notice compares the two).
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> in Dynamo.csproj' }
$commit = (git -C $root rev-parse --short HEAD).Trim()
$dirty = [bool](git -C $root status --porcelain)

$work = Join-Path ([IO.Path]::GetTempPath()) ('dynamo-package-' + [guid]::NewGuid().ToString('N'))
$folder = Join-Path $work 'Dynamo'
$app = Join-Path $folder 'app'

try {
    Write-Host 'Publishing (self-contained, win-x64)...'
    dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:DebugType=none -p:DebugSymbols=false -o $app --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

    $utf8Bom = New-Object System.Text.UTF8Encoding($true)
    # The Oracle driver's licence text (its terms require a copy with every distribution).
    Copy-Item (Join-Path $root 'packaging\licenses') (Join-Path $folder 'licenses') -Recurse
    foreach ($file in Get-ChildItem (Join-Path $root 'packaging') -File) {
        $target = Join-Path $folder $file.Name
        if ($file.Extension -in '.ps1', '.txt') {
            [IO.File]::WriteAllText($target, [IO.File]::ReadAllText($file.FullName), $utf8Bom)
        }
        else {
            Copy-Item $file.FullName $target
        }
    }

    # The licences that must travel with the program (see THIRD-PARTY-NOTICES.md).
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $folder 'LICENSE')
    Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') (Join-Path $folder 'THIRD-PARTY-NOTICES.md')

    $label = "Dynamo $version - $date (commit $commit" + $(if ($dirty) { ', avec modifications non validees' } else { '' }) + ')'
    Set-Content -Path (Join-Path $folder 'VERSION.txt') -Value $label -Encoding ASCII

    $null = New-Item -ItemType Directory -Force -Path $Output
    $zip = Join-Path $Output "Dynamo-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Write-Host 'Compressing...'
    Compress-Archive -Path $folder -DestinationPath $zip -CompressionLevel Optimal

    $size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "Package: $zip ($size MB)"
    Write-Host $label
    Write-Host "To publish: create the release v$version on GitHub and attach this zip."
}
finally {
    if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}
