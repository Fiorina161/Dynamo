# Dynamo

A self-hosted, local-only Windows site that centralizes a programmer's
everyday material: bookmarks, command-line tools, scripts, common database
queries. Everything is configured through the web interface and stored in a
local Sqlite database.

## Run

Console (development):

    dotnet run --project src\Dynamo

Then open <http://localhost:4040>.

Windows service, started automatically with Windows (asks for elevation, then
for the password of the administrator account to run as; `-LocalSystem` skips
the password):

    .\scripts\install-service.ps1
    .\scripts\uninstall-service.ps1

The service and the console both use port 4040: stop one before starting the
other.

**Opening local files from the service.** A service runs in session 0, which has
no desktop, so a program it starts there is invisible. Dynamo therefore starts
what a bookmark opens *in the signed-in user's session*, with that user's own
rights (`DesktopLauncher`: `WTSQueryUserToken` + `CreateProcessAsUser`, then the
shell's `start`). This needs the service to run as **LocalSystem**
(`install-service.ps1 -LocalSystem`); under another account it says so instead
of failing silently. It targets the **active console session** (the one on the
physical screen); a remote-desktop-only session is not supported, and with no
one signed in it answers "No user is signed in".

The same executable is a console app when started by hand and a service when
started by the service manager.

## Configuration

| Setting | Default | Purpose |
|---|---|---|
| `DYNAMO_HOME` (env) | `C:\dynamo` | Directory holding `dynamo.db`. Created if missing. |
| `DYNAMO_PORT` (env) | `4040` | Port, loopback only. |

All other settings live in the database and are edited in the UI.

## Architecture

ASP.NET Core 10, Razor Pages and htmx (vendored in `wwwroot/lib`, no CDN, no
Node build), Sqlite through Dapper. No authentication: it only listens on
`localhost`. Antiforgery stays on so another website cannot make the browser
trigger actions.

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

## Oracle connections

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

Queries are read-only: only a single `SELECT` or `WITH` is accepted, and it runs
in a read-only transaction that is rolled back. Values are sent as text, so use
`TO_DATE(:since, 'YYYY-MM-DD')` for dates.

## Tests

    dotnet test

### Importing from the old browser extension

    dotnet run --project src\Dynamo -- import-bookmarks C:\My\CustomTab\bookmarks.json

Sections become groups (colors are dropped) and `file:///` links become plain
Windows paths. Safe to repeat: bookmarks already in the group are skipped.
