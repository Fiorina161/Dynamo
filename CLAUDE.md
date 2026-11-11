# CLAUDE.md — Dynamo

Self-hosted local Windows site for a programmer's tools (see README.md for
run/install and architecture). Work directly in this checkout; no worktrees.
Comments wrap at column 80. Commit and push only when the user asks.

## Git

Solo repo: `master` is the main line and the user wants no pull requests.
Claude's background sessions may not push to `master` themselves, so Claude
commits on a working branch, pushes that branch, and gives the user the one
command that fast-forwards `master`:

    git push origin <branch>:master

Then `git checkout master && git pull`, and delete the branch. Never force-push.

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
- Oracle connections (done): `Modules/OracleConnections`, page `/Oracle`.
  A connection is a unique name (case-insensitive) + data source (TNS alias
  via TNS_ADMIN, Easy Connect `host:port/service`, or a full descriptor) +
  schema (user) + password, in table `oracle_connection`. The password is
  stored in clear (no security by design), never rendered back in HTML; a
  blank password on edit keeps the stored one. The page has Test buttons
  (row and dialog; the dialog tests unsaved values). **Other features must
  get Oracle only through `OracleConnector`** (singleton):
  `await using var conn = await connector.OpenAsync("PROD");`, and
  `connector.Names()` for pickers; the data source and password never leave
  the module. Connect timeout 15 s. Driver `Oracle.ManagedDataAccess.Core`;
  `System.Security.Cryptography.Pkcs` is pinned directly because the
  driver's own dependency has known vulnerabilities (NU1902/NU1903).

## Roadmap (user's list, in no fixed order)

Bookmarks (done) → Oracle connections (done) → common database queries
(next natural step: uses `OracleConnector`) → command-line tools → scripts →
accessed links history / other.

## Conventions

- One folder per use case: `Modules/<Name>` (IModule, migrations, repository)
  and `Pages/<Name>` (Razor pages, htmx fragments).
- Migrations are append-only per module; never edit a shipped one.
- A page handler answers htmx requests with a fragment; errors re-show the
  dialog (`HX-Retarget: #dlg`), notices use the `toast` HX-Trigger event.

## Known gaps

- Groups cannot be reordered (only bookmarks drag). Dropping into a collapsed
  group is not possible. No tests yet. No file logging (console/Event Log).
