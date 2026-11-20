<#
.SYNOPSIS
    Publishes a Dynamo release on GitHub: version, tests, commit, zip, release
    with notes and checksum, then checks it from the outside.

.DESCRIPTION
    Run from the repository, on a clean `master` that is up to date:

        .\scripts\release.ps1 -Bump patch          (1.0.0 -> 1.0.1)
        .\scripts\release.ps1 -Bump minor          (1.0.1 -> 1.1.0)
        .\scripts\release.ps1 -Version 2.0.0
        .\scripts\release.ps1 -Bump patch -DryRun  (shows the plan, changes nothing)

    Steps: checks (clean tree, on master, in step with origin, `gh` signed in) ->
    new <Version> in Dynamo.csproj -> `dotnet test` (stops, and undoes the version,
    if anything fails) -> CHANGELOG.md -> commit "Release X.Y.Z" and push -> zip
    (package.ps1) -> `gh release create vX.Y.Z` with the zip, the notes (the commit
    subjects since the last release) and the SHA-256 -> verification: the file
    downloaded from GitHub matches, and the public "latest release" is this one.

    Afterwards only the newest -Keep (3) releases stay on GitHub; the older ones
    are deleted (their tags and the CHANGELOG stay). -Keep 0 turns that off.

    Needs the GitHub CLI (`gh`), signed in with access to the repository. If a
    GITHUB_TOKEN environment variable holds an old token, remove it: gh prefers it.

.PARAMETER Notes
    A file with the release notes to use instead of the generated list.
#>
[CmdletBinding()]
param(
    [ValidateSet('patch', 'minor', 'major')] [string] $Bump,
    [string] $Version,
    [string] $Notes,
    [string] $Output = (Join-Path $env:USERPROFILE 'Downloads'),
    # How many releases stay on GitHub: the older ones are deleted after a
    # release (their git tags stay). 0 keeps them all.
    [int] $Keep = 3,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$repo = 'Fiorina161/Dynamo'
$csproj = Join-Path $root 'src\Dynamo\Dynamo.csproj'
$gitExe = (Get-Command git -ErrorAction SilentlyContinue).Source
if (-not $gitExe) { $gitExe = 'C:\Program Files\Git\cmd\git.exe' }
$ghExe = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $ghExe) { $ghExe = 'C:\Program Files\GitHub CLI\gh.exe' }

function Git { & $gitExe -C $root @args; if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed" } }
function GitOut { (& $gitExe -C $root @args) }
function Gh { & $ghExe @args; if ($LASTEXITCODE -ne 0) { throw "gh $($args -join ' ') failed" } }

if (-not $Bump -and -not $Version) { throw 'Say what to release: -Bump patch|minor|major, or -Version X.Y.Z' }

# ---- checks -------------------------------------------------------------------
if ((GitOut branch --show-current) -ne 'master') { throw 'Releases are made from master.' }
if (GitOut status --porcelain) { throw 'The working tree is not clean: commit or stash first.' }
Git fetch --tags --quiet
$ahead = [int](GitOut rev-list --count 'origin/master..master')
$behind = [int](GitOut rev-list --count 'master..origin/master')
if ($behind -gt 0) { throw "master is $behind commit(s) behind origin/master: pull first." }
if ($ghExe -and -not (Test-Path $ghExe)) { throw 'The GitHub CLI (gh) is not installed: winget install --id GitHub.cli' }
& $ghExe auth status 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'gh is not signed in (or GITHUB_TOKEN holds a bad token): run gh auth login' }

# ---- the version --------------------------------------------------------------
$xml = [xml](Get-Content $csproj)
$current = [version](($xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1))
if ($Version) {
    $next = [version]$Version
}
else {
    $next = switch ($Bump) {
        'patch' { [version]::new($current.Major, $current.Minor, $current.Build + 1) }
        'minor' { [version]::new($current.Major, $current.Minor + 1, 0) }
        'major' { [version]::new($current.Major + 1, 0, 0) }
    }
}
if ($next -le $current) { throw "The new version $next must be higher than the current one ($current)." }
$tag = "v$next"
if (GitOut tag --list $tag) { throw "The tag $tag already exists." }

$lastTag = (& $ghExe release list --repo $repo --limit 1 --json tagName --jq '.[0].tagName' 2>$null)
$range = if ($lastTag) { "$lastTag..HEAD" } else { 'HEAD' }
$subjects = @(GitOut log $range --no-merges --pretty=format:'%s' | Where-Object { $_ -and $_ -notmatch '^Release \d' })
if (-not $subjects) { throw "Nothing has changed since ${lastTag}: there is nothing to release." }

Write-Host "Release $current -> $next  (tag $tag), changes since ${lastTag}:"
$subjects | ForEach-Object { Write-Host "  - $_" }
if ($ahead -gt 0) { Write-Host "($ahead local commit(s) not yet pushed: they are pushed with the release)" }
if ($DryRun) { Write-Host 'Dry run: nothing was changed.'; return }

# ---- version, tests -------------------------------------------------------------
$csprojText = Get-Content $csproj -Raw
$updated = $csprojText -replace '<Version>[^<]+</Version>', "<Version>$next</Version>"
Set-Content $csproj $updated -NoNewline -Encoding UTF8
try {
    Write-Host 'Running the tests...'
    & dotnet test (Join-Path $root 'Dynamo.slnx') --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'The tests failed: no release made.' }
}
catch {
    Set-Content $csproj $csprojText -NoNewline -Encoding UTF8
    throw
}

# ---- changelog, commit, push ---------------------------------------------------------
$changelog = Join-Path $root 'CHANGELOG.md'
$entry = "## $next - $(Get-Date -Format 'yyyy-MM-dd')`n`n" + (($subjects | ForEach-Object { "- $_" }) -join "`n") + "`n`n"
if (Test-Path $changelog) {
    $old = Get-Content $changelog -Raw
    $head = "# Changelog`n`n"
    $body = if ($old.StartsWith($head)) { $old.Substring($head.Length) } else { $old }
    Set-Content $changelog ($head + $entry + $body) -NoNewline -Encoding UTF8
}
else {
    Set-Content $changelog ("# Changelog`n`n" + $entry) -NoNewline -Encoding UTF8
}
Git add $csproj $changelog
Git commit -q -m "Release $next`n`nCo-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
Git push origin master
$sha = (GitOut rev-parse HEAD).Trim()

# ---- the zip -------------------------------------------------------------------
& (Join-Path $PSScriptRoot 'package.ps1') -Output $Output
$zip = Join-Path $Output "Dynamo-$next.zip"
if (-not (Test-Path $zip)) { throw "The zip was not built: $zip" }
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()

# ---- the release ------------------------------------------------------------------
if ($Notes) {
    $notesText = Get-Content $Notes -Raw
}
else {
    $notesText = "## Dynamo $next`n`n" + (($subjects | ForEach-Object { "- $_" }) -join "`n") + @"


### Install or update
Download ``Dynamo-$next.zip`` below, extract it completely and double-click ``Installer.cmd`` (it asks for administrator rights). An existing install is replaced in place and its data (``C:\dynamo``) is kept. The French guide is ``LISEZMOI.txt``.

### Checksum (SHA-256)
``$hash``  ``Dynamo-$next.zip``
"@
}
$notesFile = Join-Path ([IO.Path]::GetTempPath()) "dynamo-release-notes-$next.md"
Set-Content $notesFile $notesText -Encoding UTF8
Gh release create $tag $zip --repo $repo --target $sha --title "Dynamo $next" --notes-file $notesFile
Remove-Item $notesFile -Force -ErrorAction SilentlyContinue

# ---- verification, from the outside ---------------------------------------------------
Write-Host 'Checking the published release...'
$check = Join-Path ([IO.Path]::GetTempPath()) "dynamo-check-$next.zip"
Invoke-WebRequest "https://github.com/$repo/releases/download/$tag/Dynamo-$next.zip" -OutFile $check -UseBasicParsing
$downloaded = (Get-FileHash $check -Algorithm SHA256).Hash.ToLower()
Remove-Item $check -Force
if ($downloaded -ne $hash) { throw "The published file does not match the zip built here (SHA-256 $downloaded vs $hash)." }
$latest = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'dynamo-release'; Accept = 'application/vnd.github+json' }
if ($latest.tag_name -ne $tag) { throw "GitHub's latest release is $($latest.tag_name), not $tag." }
Write-Host "Released ${tag}: $($latest.html_url)"
Write-Host "SHA-256 $hash (matches the file on GitHub)."

# ---- keep only the newest releases ------------------------------------------------------------
# A failure here never undoes the release: it is only a warning.
if ($Keep -gt 0) {
    try {
        $published = & $ghExe release list --repo $repo --limit 200 --json tagName,publishedAt | ConvertFrom-Json |
            Sort-Object publishedAt -Descending
        foreach ($old in ($published | Select-Object -Skip $Keep)) {
            & $ghExe release delete $old.tagName --repo $repo --yes 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) { Write-Host "Removed the old release $($old.tagName) (its tag stays)." }
            else { Write-Warning "Could not remove the old release $($old.tagName)." }
        }
    }
    catch { Write-Warning "Could not tidy the old releases: $($_.Exception.Message)" }
}
