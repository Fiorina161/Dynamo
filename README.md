# Dynamo

A self-hosted, local-only Windows site that centralizes a programmer's
everyday material: bookmarks, command-line tools, scripts, common database
queries. Everything is configured through the web interface and stored in a
local Sqlite database.

## Run

Console (development, or when you want launched files on your desktop):

    dotnet run --project src\Dynamo

Then open <http://localhost:4040>.

Windows service, started automatically with Windows (asks for elevation, then
for the password of the administrator account to run as; `-LocalSystem` skips
the password):

    .\scripts\install-service.ps1
    .\scripts\uninstall-service.ps1

The service and the console both use port 4040: stop one before starting the
other. A service cannot show windows on your desktop, so use the console when
a bookmark must open a file or folder visibly.

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

### Importing from the old browser extension

    dotnet run --project src\Dynamo -- import-bookmarks C:\My\CustomTab\bookmarks.json

Sections become groups (colors are dropped) and `file:///` links become plain
Windows paths. Safe to repeat: bookmarks already in the group are skipped.
