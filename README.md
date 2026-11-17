# Dynamo

A self-hosted, local-only Windows site that centralizes a programmer's
everyday material: bookmarks, command-line tools, scripts, common database
queries. Everything is configured through the web interface and stored in a
local Sqlite database.

## Run

Console (development):

    dotnet run --project src\Dynamo

Then open <http://localhost:4040>.

Windows service, started automatically with Windows (asks for elevation; runs
as LocalSystem, so no password; `-RunAsUser` asks for an account and its
password instead):

    .\scripts\install-service.ps1
    .\scripts\uninstall-service.ps1

The service and the console both use port 4040: stop one before starting the
other.

**Opening local files from the service.** A service runs in session 0, which has
no desktop, so a program it starts there is invisible. Dynamo therefore starts
what a bookmark opens *in the signed-in user's session*, with that user's own
rights (`DesktopLauncher`: `WTSQueryUserToken` + `CreateProcessAsUser`, then the
shell's `start`). This needs the service to run as **LocalSystem**
(the default of `install-service.ps1`); under another account it says so instead
of failing silently. It targets the **active console session** (the one on the
physical screen); a remote-desktop-only session is not supported, and with no
one signed in it answers "No user is signed in".

The same executable is a console app when started by hand and a service when
started by the service manager.

## Remote access (Dynamo on a server or VM)

Dynamo can run on another machine (say a VM that can reach Oracle) and be used
from the browser of your own computer; queries then run on that machine.

- **This computer is never asked for anything.** A request from the computer
  where the service runs (loopback, or its own addresses) goes straight in.
- **Another computer** is refused until a password is set, then has to log in.
  The password is chosen in **Settings** (the last menu item), on the server
  itself; it is stored in the same Sqlite database as a salted PBKDF2 hash.
  The login is remembered by a session cookie without an expiry date, so it
  lasts until the browser is closed (the menu has a **Log out** button), and is
  ended for everybody when the password changes. Wrong passwords are throttled
  (5 in a row lock that address for a minute). From another computer, changing
  or removing the password needs the current one.
- **Bookmarks to files, folders and programs are off** for another computer
  (shown greyed, "this computer only"): the file is not on it, and opening one
  would start a program on the server's desktop. Bookmarks to web addresses
  work everywhere.
- The service listens on every network interface; **the Windows Firewall stays
  closed** unless you say so: `.\scripts\install-service.ps1
  -OpenFirewall` adds an inbound rule for the port, by default to the local
  subnet on the Domain and Private profiles (`-FirewallRemoteAddress` takes the
  laptop's address or a range, `-FirewallProfile Any` if the network is
  "Public"). The rule covers the http and the https ports; the uninstall script
  removes it. `DYNAMO_LOCAL_ONLY=1` makes it listen on this computer only (no
  https then either).
- **https** (port 4343, `DYNAMO_HTTPS_PORT`, 0 = off) uses a certificate Dynamo
  makes itself: a small local authority created once (valid ten years) signs the
  server certificate (valid 397 days), which **renews itself** 30 days before it
  expires and whenever the computer's names or addresses change. Everything is
  stored in the database; Settings shows the expiry, the names covered and the
  authority's SHA-256 fingerprint. **Trust the authority once** on each
  computer you browse from: download `http://<server>:4040/dynamo-certificate.cer`
  (public part only), open it, and install it in *Trusted Root Certification
  Authorities* (Firefox: Settings, Certificates, Authorities, Import), after
  checking the fingerprint against Settings. Renewals are then trusted without a
  warning; until then the browser warns. Another computer on plain http is
  **redirected to https** (a Settings option, on by default; the certificate
  download stays on http), and the session cookie is marked Secure over https.
  Let's Encrypt is not used: it only issues for public domain names.
- Anyone who gets in can run read-only SQL on every saved connection, so choose
  a long password. If the network is not trusted, https is what protects the
  password; a VPN or SSH tunnel remains an option.

## Packaging, releases and updates

    .\scripts\package.ps1            (writes Dynamo-<version>.zip to Downloads)
    .\scripts\package.ps1 -Output D:\release

Builds a zip with Dynamo published **self-contained** (the other machine needs no
.NET), the installer and uninstaller (`Installer.cmd`, `Desinstaller.cmd`), a
guide in Canadian French (`LISEZMOI.txt`), `LICENSE` and the third-party
licences (`THIRD-PARTY-NOTICES.md`, `licenses\`: Oracle's terms require their
licence text to travel with the driver). The recipient extracts it and
double-clicks `Installer.cmd`: a LocalSystem service on port 4040, data in
`C:\dynamo`, kept on update and uninstall. Your own data is never included. The
sources of what ships besides the program are in `packaging\`; edit the guide
there and rebuild.

**Versions.** The version is `<Version>` in `src\Dynamo\Dynamo.csproj`
(1.0.0 to start; raise it for each release). Settings shows it.

**Publishing a release.** With the GitHub CLI (`gh`) signed in, one command does
it all: `.\scripts\release.ps1 -Bump patch` (or `minor`, `major`, or
`-Version X.Y.Z`; add `-DryRun` to preview). It checks the tree, raises the
version, runs the tests, updates `CHANGELOG.md`, commits and pushes, builds the
zip, creates the GitHub release `v<version>` with the zip, notes and SHA-256, and
checks the published file. By hand: raise the version, run `package.ps1`, create
the release on GitHub and attach the zip with its checksum.

**Update notice.** Dynamo looks at the repository's public releases (GitHub's
API, no token, nothing about the computer sent) a little after it starts and
once a day, and when you press *Check now* in Settings. If the latest release is
newer than the running version, the menu shows "Version X available" (to
whoever sits at that computer) and Settings links to the zip and the release
notes. It only **informs**: nothing is downloaded or installed by itself; you
download the zip, extract it and run `Installer.cmd`, which updates in place and
keeps the data. Only links into this project's GitHub pages are ever shown. The
daily look can be turned off in Settings (`DYNAMO_UPDATE_CHECK=off` removes it
entirely); the computer needs internet access for it, and without it Settings
just says it could not reach GitHub.
The pages adapt to a phone: below 760 px the menu on the left folds behind a ☰
button in a top bar.

## Configuration

| Setting | Default | Purpose |
|---|---|---|
| `DYNAMO_HOME` (env) | `C:\dynamo` | Directory holding `dynamo.db`. Created if missing. |
| `DYNAMO_PORT` (env) | `4040` | Port. |
| `DYNAMO_LOCAL_ONLY` (env) | unset | `1` listens on this computer only (loopback), whatever the password setting. |

All other settings live in the database and are edited in the UI.

## Security

Read this before exposing Dynamo to anyone but yourself.

- **Oracle passwords are stored in clear** in the local Sqlite database
  (`dynamo.db`): Dynamo has to send them to Oracle. Protect that file like the
  passwords themselves, and do not share it.
- Run on its own computer, Dynamo asks for nothing. Another computer is refused
  until a password is set in Settings (kept as a salted hash), then logs in.
  This is meant for a trusted network or a VPN: it is a convenience gate for a
  personal tool, **not hardened for the open internet**.
- https uses a certificate authority Dynamo creates for itself; you choose
  whether to trust it. The authority's private key is kept in the database.
- Queries are read-only by design, but whoever can use Dynamo can run any
  `SELECT` allowed to the saved connections' accounts: give those accounts only
  the rights you are comfortable with.
- The service runs as LocalSystem, and a bookmark can start any program on the
  desktop, so only trust people with access to this computer's Dynamo.

Found a vulnerability? Please report it privately through the repository's
*Security* tab (private vulnerability reporting) rather than in a public issue.

## Third-party software

The release zip bundles .NET, Sqlite, Dapper, htmx and Oracle's data access
driver, each under its own licence: see `THIRD-PARTY-NOTICES.md`.

## Architecture

ASP.NET Core 10, Razor Pages and htmx (vendored in `wwwroot/lib`, no CDN, no
Node build), Sqlite through Dapper. Access control: see *Remote access* below
(this computer is never asked for anything). Antiforgery stays on so another
website cannot make the browser trigger actions.

- `Core/` — home directory, database and migrations, the `IModule` contract.
- `Modules/<Name>/` — one folder per use case: its `IModule`, migrations,
  repository and logic.
- `Pages/<Name>/` — that module's Razor pages and htmx fragments.
- A module is registered in `Program.cs` and appears in the navigation.

### Adding a use case

1. Create `Modules/<Name>/<Name>Module.cs` implementing `IModule` with its SQL
   migrations (append-only; applied once per module at startup).
2. Add its repository/logic beside it and the pages under `Pages/<Name>/`.
3. Register it in `Program.cs` (`AddSingleton<IModule, ...>`).

## Bookmarks

Create, edit and delete bookmarks and groups, drag them between groups, and
collapse groups. Only labels are shown. `http(s)` links open in a new tab;
anything else (files, folders, `\\server\share`, `mailto:`, custom protocols)
is opened by the service itself with the Windows default handler, since a
browser refuses `file://` links from an http page.

## Connections

The Oracle page keeps named connections: a data source (TNS alias, Easy
Connect `host:port/service` or a full descriptor), a schema and its password,
much like `tnsnames.ora` aliases. Later features refer to a connection by its
name only (`OracleConnector.OpenAsync("PROD")`). Passwords are stored in clear
in the local database and never shown again in the UI; leave the password
blank when editing to keep it. Each row has a Test button, and the edit
dialog can test unsaved values.

### Duplicating a connection

Open a connection and press **Duplicate**: a new connection form opens with the
same data source and schema and a free name ("PROD copy"). Change what should
differ (typically the schema) and save; Cancel creates nothing. Leave the
password empty to reuse the original's, which is never shown on screen.

### Deleting several connections

Tick the boxes (shift-click selects a range; the box in the header selects every
connection currently shown, so filter first to select "everything matching"),
then **Delete selected**. A confirmation lists exactly what will be deleted.
Rows hidden by the filter are never selected.

### Importing from tnsnames.ora

When a `tnsnames.ora` exists (in `TNS_ADMIN`, else in
`ORACLE_HOME\network\admin`; for the service the variable must be machine-wide),
an **Import tnsnames.ora** button appears next to *New connection*. It opens a
list of the aliases that are not connections yet, none ticked: tick the ones you
want (select-all and the filter box work on the rows shown), then **Import
selected**. From the command line, every alias at once:

    Dynamo.exe import-tnsnames [path\to\tnsnames.ora]

Without a path it reads `tnsnames.ora` in `TNS_ADMIN`. Each alias becomes a
connection (same name, full descriptor as the data source); the schema and
password stay empty, to be filled in on the Oracle page (the list flags those
that are still missing and the filter box finds them). Safe to repeat: names
that already exist are left alone, so what you filled in is kept. An alias
defined twice in the file keeps its first definition, and the command says so.
From source: `dotnet run --project src\Dynamo -- import-tnsnames`.

## Queries

Save the SELECTs you run often and run them on any Oracle connection.
A query has a name, an optional category (queries are listed by category, with
a filter box), a default connection and its SQL. Write `:name` for a value to
fill in; the run page shows one box per bind variable and remembers what you
typed. Results show in a table (default 500 rows, up to 10,000) and **Export
CSV** downloads up to 100,000 rows.

**Auto-refresh:** next to Run, type a number of seconds (1 to 3600) and press
**Start auto-refresh**: the query runs now and then again every N seconds until
you press **Stop auto-refresh** or leave the page. The next run starts N seconds
after the previous one *finished*, so a slow query never overlaps itself. A
failed run (wrong password, Oracle error, server down) stops it, so a bad
password is not retried over and over. Each refresh uses the values currently in
the form, keeps the table's scroll position, and the result header shows when it
was fetched. The interval is remembered per query.

**Sorting:** click a column header in a result table to sort by it, click again
to reverse (▲/▼ shows the column). Numeric columns sort as numbers, text
naturally (`item2` before `item10`, case ignored), NULLs always last. Shift-click
(or Escape on a focused header) goes back to the query's own order. The sort is
kept across refreshes while the columns are the same, and it applies to the
rows fetched, not to the whole table in the database. It works the same in the
Queries page and in the panels of a workspace.

Queries are read-only: only a single `SELECT` or `WITH` is accepted, and it runs
in a read-only transaction that is rolled back. Values are sent as text, so use
`TO_DATE(:since, 'YYYY-MM-DD')` for dates.

## Workspaces

A workspace shows several queries on one page, to monitor a few things at once.
Create one on the **Workspaces** page, then fill it:

- **New query** writes a query right there, with a name; it lives in the
  workspace only and is *not* added to the Queries list.
- **Add from saved** copies saved queries into the workspace. The saved queries
  are templates: each panel gets its **own copy** (same name, which you can
  change), so editing it never changes the saved query or any other workspace,
  and the other way round. The same query can be copied twice (say, for two
  connections).

Each query is a panel that fills its place with the results and keeps only a
title line. The **gear** opens its details: connection, refresh interval,
**max rows** (1 to 10,000, each panel its own), Run now, **Edit query** (name
and SQL of this panel's own query), ← → to move the panel earlier or later,
Remove, the last run.

A workspace can be changed at any time: add, remove, reorder and edit panels,
**Rename**, and **Duplicate** (a copy with the same panels, their own queries,
settings and shared values; nothing is created until you save the name).

- **Refresh per panel:** seconds (0 = off, up to 3600), kept with the workspace.
  The next run starts after the previous answer, a failed run stops that panel
  only (**Run now** retries), and nothing runs while the browser tab is hidden.
- **Shared values:** a bind name used by several queries (`:order_id`) is typed
  once in the bar at the top; Enter or **Apply** saves the values and runs every
  panel.
- **What changed:** cells that differ from the previous refresh flash, and the
  row count shows its change.
- **Pause all** stops every automatic refresh until **Resume all**; **Run all**
  runs every panel now.

## Tests

    dotnet test

### Importing bookmarks from a JSON export

    dotnet run --project src\Dynamo -- import-bookmarks C:\path\to\bookmarks.json

Sections become groups (colors are dropped) and `file:///` links become plain
Windows paths. Safe to repeat: bookmarks already in the group are skipped.
