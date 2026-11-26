# Dynamo

A small, self-hosted Windows site that gathers a programmer's and Oracle DBA's
everyday tools in one place: bookmarks, saved Oracle connections and
live-refreshing workspaces of queries. It runs as a Windows service on your own
machine (or a VM) and is used from the browser. Everything is configured in the
web interface and stored in a local Sqlite database; it works fully offline.

## Features

**Bookmarks**
- Groups with an optional colour, drag and drop, collapsible groups.
- Opens web links in a new tab, and files, folders, network shares and other
  protocols on the server's desktop.
- Import from a JSON export of another bookmarks tool.

**Oracle connections**
- Named connections (TNS alias, Easy Connect or full descriptor, schema,
  password), with test, duplicate, pin, batch delete and a filter.
- Import from `tnsnames.ora`.
- **DBA connections** (log in AS SYSDBA) and a **pastel colour** per connection,
  used for the title bar of its panels so production never looks like test.
- A **health page**: every connection's state, login time and Oracle version.

**Shared folders**
- List and **download** the files of a network folder (`\\server\share\folder`),
  read with an account you save with it (the password is stored encrypted).
- Browse sub-folders, filter by name, sort by name, size or date, page through
  long folders; downloads can be resumed. Read only: it never writes or deletes.

**Workspaces**
- A workspace shows several saved queries as panels, each refreshing on its own;
  bind variables are shared across panels.
- Panels can be reordered by dragging, collapsed, made full width, duplicated
  between workspaces, and **resized with the mouse** (in whole rows).
- Results: sort, spreadsheet-style column filters, pinned rows, three highlight
  boxes, row numbers, CSV export, **copy as TSV / Markdown / table**, vertical
  view of a one-row result, readable sizes (KB/MB/GB) and a window for long text.
- **Execution plan** of any panel's query (`EXPLAIN PLAN`, without running it).
- Queries are read-only by design: a single `SELECT`, in a read-only
  transaction that is always rolled back.

**DBA tools** (on DBA connections only)
- Ready-made panels: active and blocking sessions, locks, long operations,
  tablespaces, temp, undo, FRA, top SQL, wait events, RMAN backups, scheduler
  failures, invalid objects, alert-log errors, Data Guard lag and more.
- A **sessions panel with a Kill button** (red, with a confirmation).

**Running it**
- Light, dark or system theme; works on a phone.
- Optional **remote access** with a password and **https** (a certificate
  authority Dynamo creates for itself), for a service on a VM.
- A notice for new versions and a one-click **Update now** from Settings.

## Install

**From a release (recommended).** Download `Dynamo-<version>.zip` from the
[Releases](../../releases) page, extract it completely and double-click
`Installer.cmd` (it asks for administrator rights). It installs a Windows service
on port 4040, keeps your data in `C:\dynamo`, and replaces an existing install in
place. The zip carries its own .NET runtime. Then open <http://localhost:4040>.
`LISEZMOI.txt` in the zip is the user guide (in Canadian French).

**From source.** You need the .NET 10 SDK.

    dotnet run --project src\Dynamo           # console, for development
    .\scripts\install-service.ps1             # Windows service
    dotnet test                               # the tests

Settings (environment variables):

| Variable | Default | Purpose |
|---|---|---|
| `DYNAMO_HOME` | `C:\dynamo` | Folder holding `dynamo.db`. |
| `DYNAMO_PORT` | `4040` | Port. |
| `DYNAMO_LOCAL_ONLY` | unset | `1` listens on this computer only. |

## Security

Dynamo is a personal tool for a trusted network, **not hardened for the open
internet**.

- Oracle passwords (and the private keys of the https certificates) are
  **encrypted** in `dynamo.db` with Windows DPAPI at machine scope: a copy of the
  file read on another computer shows nothing useful. This protects the file, not
  the running machine (any account on it can decrypt), and not a stolen disk or VM
  image. After restoring the database on another computer, or reinstalling Windows,
  enter the passwords again.
- On its own computer Dynamo asks for nothing; another computer is refused until
  a password is set in Settings.
- Whoever can use Dynamo can run any `SELECT` the saved accounts allow, and (with a
  DBA connection) kill sessions: give those accounts only the rights you accept.

To report a vulnerability, use the repository's *Security* tab (private
reporting).

## Licence

MIT (see `LICENSE`). Third-party components, including Oracle's driver and its
terms, are listed in `THIRD-PARTY-NOTICES.md`. Design notes and decisions are in
`CLAUDE.md`.
