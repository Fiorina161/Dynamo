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
API, no token, nothing about the computer sent) only when you press *Check now*
in Settings; it never looks by itself. If the latest release is
newer than the running version, the menu shows "Version X available" (to
whoever sits at that computer) and Settings links to the zip and the release
notes, and lists **What's new since the version you run**: every change of every
release in between, newest first, read from the repository's `CHANGELOG.md` (one
more read of a public file on `raw.githubusercontent.com`, a fixed address; if it
cannot be read, the latest release's own notes are shown instead). That text is
always shown as plain text, never as links or markup, and the list is bounded.
Only links into this project's GitHub pages are ever shown. Dynamo
never looks by itself: you press "Check now" in Settings, and the computer needs
internet access for it, and without it Settings
just says it could not reach GitHub.

**Update now.** When a newer release exists, Settings shows an *Update now*
button (only on the computer where Dynamo runs, and only when it runs as a
Windows service). It downloads the release zip from this project's GitHub
page, checks it, and starts a script that stops the service, replaces the
program files in place (the service itself, its settings, the data and the
firewall rule are untouched), starts it and waits for the site to answer; if
the new version does not come up, the old files and a copy of the database file
taken before the swap are put back. The log is `update.log` in the data folder.
The service runs as LocalSystem, so the limits matter: only the exact release
asset of this project is fetched
(`https://github.com/Fiorina161/Dynamo/releases/download/<tag>/Dynamo-<version>.zip`),
only a **strictly newer** version, only if the release publishes a **SHA-256**
(GitHub's asset digest, else the one in the release notes) that the file matches
and the program inside reports that version. The checksum travels through the
same GitHub account as the file, and releases are not code-signed: it protects
against a corrupted or altered download, not against a compromised account. The
manual route (zip + `Installer.cmd`) remains, and the first version that has the
button must be installed by hand.
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

## Workspaces

Database queries live in workspaces. A workspace shows several queries on one
page, each refreshing on its own, to follow a few things at once (a workspace of
one query is how you run a single one). Create one on the **Workspaces** page,
then fill it:

- **New query** writes a query right there: a name, a connection and its SQL.
  Write `:name` for a value to fill in. Only a single `SELECT` or `WITH` is
  accepted, and it runs in a read-only transaction that is rolled back. Values
  are sent as text, so use `TO_DATE(:since, 'YYYY-MM-DD')` for dates.
- **Import from workspace** copies queries from another workspace: pick the
  workspace, tick the panels (select all, filter box), and each becomes this
  workspace's **own copy** with its connection and settings. Editing a copy never
  changes the original, and the other way round. The same query can be imported
  twice (say, for two connections).

(The separate **Queries** page of earlier versions was removed. Updating copied
its saved queries into a workspace named **Saved queries**, one panel each,
named `Category / Name` when they had a category; the old tables are left
untouched in the database.)

Each query is a **panel**. The panels flow down the columns of the page (as many
as fit, each at least 560 px wide), so a short panel leaves no hole: the next one
takes its place. A panel is always exactly as tall as its *visible rows* (the
default number of rows from Settings, 10 until you change it, unless the panel has a
number of its own): a longer result scrolls, a shorter one, an error or no rows leaves blank
space, so the number you set is the number you get and a refresh never makes the
layout jump. The order is a sequence, not a position:

- **Drag** the ⠿ handle in a panel's title bar to put it elsewhere in the order
  (a line shows where it lands; the page scrolls near its edges; Escape cancels).
  It works with the mouse, a pen or a finger. On the handle, the ↑ and ↓ keys move
  the panel one place, and the details have **earlier / later** buttons too.
- **↔** gives a panel the full width of the page (it spans every column); kept per
  panel in this browser.
- **▾** (or a click on the title) collapses a panel to its title line, and again to
  expand it; **Collapse all / Expand all** in the page header do every panel. A
  collapsed panel keeps refreshing, so its line stays current: the status dot, rows
  and duration, plus a small text with its refresh state
  (`every 10 s`, `paused`, `stopped`), the change in row count (`+3 rows`) and
  `Showing 4 of 6 rows` when a filter is on. It is saved with the panel (so a
  duplicate and every browser agree), and the ⚙ of a collapsed panel expands it.

The **gear** opens a panel's details: connection, refresh interval, **max rows**
(1 to 10,000, rows fetched), **visible rows** (0 to 1,000, the height of the
table before it scrolls; **0 = the default number of rows set in Settings, Results**,
which is where new panels start, so changing that one number resizes every panel
that follows it), Run now, **Export CSV** (up to 100,000
rows), **Edit query** (name and SQL), earlier / later, Remove, the last run.

A workspace can be changed at any time: add, remove, reorder and edit panels,
**Rename**, and **Duplicate** (a copy with the same panels, their own queries,
settings and shared values; nothing is created until you save the name).

- **Refresh per panel:** seconds (0 = off, up to 3600), kept with the workspace.
  The next run starts after the previous answer, a failed run (wrong password,
  Oracle error, server down) stops that panel only (**Run now** retries, so a bad
  password is not retried over and over), and nothing runs while the browser tab
  is hidden. The title bar shows when the result was fetched, its rows and its
  duration.
- **Shared values:** a bind name used by several queries (`:order_id`) is typed
  once in the bar at the top; Enter or **Apply** saves the values and runs every
  panel.
- **What changed:** cells that differ from the previous refresh flash, and the
  row count shows its change.
- **Pause all** stops every automatic refresh until **Resume all**; **Run all**
  runs every panel now.

In the tables of results, all in the browser and on the rows fetched:

- **Sorting:** click a column header to sort by it, click again to reverse (▲/▼
  shows the column). Numeric columns sort as numbers, text naturally (`item2`
  before `item10`, case ignored), NULLs always last. Shift-click (or Escape on a
  focused header) goes back to the query's own order. Kept across refreshes while
  the columns are the same.
- **Filtering** like in a spreadsheet: the ▾ in a header lists the column's values
  with counts; untick the ones to hide. Columns combine, NULL is its own value, and
  a value that first shows up in a refresh is visible (a filter stores what is
  hidden). The `#` column numbers the rows shown.
- **Pinning:** click a row to pin it to the top (again to unpin; the `#` header
  unpins all), whatever the sort.
- **Highlighting:** up to three boxes (Settings, *Results*; one by default), each
  its own colour, mark what you type in every panel at once.
- **Row numbers, NULL in a faded italic, a grid of faint lines.**

## Tests


    dotnet test

### Importing bookmarks from a JSON export

    dotnet run --project src\Dynamo -- import-bookmarks C:\path\to\bookmarks.json

Sections become groups (colors are dropped) and `file:///` links become plain
Windows paths. Safe to repeat: bookmarks already in the group are skipped.
