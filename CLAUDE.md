# CLAUDE.md — Dynamo

Self-hosted local Windows site for a programmer's tools (see README.md for
run/install and architecture). Work directly in this checkout; no worktrees.
Comments wrap at column 80. Commit and push only when the user asks.

## Decisions made with the user

- Stack: ASP.NET Core 10, Razor Pages + htmx (vendored, offline), Sqlite via
  Dapper. No Node build. NuGet uses the repo's `nuget.config` (nuget.org only:
  the machine's global feed answers 401).
- Home: `DYNAMO_HOME`, else `C:\dynamo`; `dynamo.db` inside. Port 4040,
  loopback only. Windows service runs under an administrator account (tools may
  need elevation); console when not launched by the service manager.
- No authentication or authorization (local only). Antiforgery stays on on
  purpose: it stops a web page from making the browser launch bookmarks.
- Local-file bookmarks are opened by the server (`Launcher`,
  `UseShellExecute`). A Session-0 service cannot show windows on the desktop;
  use the console for that.
- UI: sober, minimal color (one accent), light/dark/system theme.
- Every setting is edited in the web UI and stored in Sqlite.
- Multiple Oracle connections are planned (a connections module: name, host,
  port, service, user, password stored in Sqlite as the user wants no
  security; package `Oracle.ManagedDataAccess.Core`).

## Roadmap (user's list, in no fixed order)

Bookmarks (done) → command-line tools → scripts → common database queries
(needs Oracle connections) → accessed links history / other.

## Conventions

- One folder per use case: `Modules/<Name>` (IModule, migrations, repository)
  and `Pages/<Name>` (Razor pages, htmx fragments).
- Migrations are append-only per module; never edit a shipped one.
- A page handler answers htmx requests with a fragment; errors re-show the
  dialog (`HX-Retarget: #dlg`), notices use the `toast` HX-Trigger event.

## Known gaps

- Groups cannot be reordered (only bookmarks drag). Dropping into a collapsed
  group is not possible. No tests yet. No file logging (console/Event Log).
