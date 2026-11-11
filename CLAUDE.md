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

- tnsnames.ora import (done, 11 Nov 2026): `Dynamo.exe import-tnsnames [file]`
  (default `%TNS_ADMIN%\tnsnames.ora`; the user's is
  `C:\ora\product\19.0.0\client_1\network\admin`, TNS_ADMIN is set machine-wide).
  `TnsNamesParser` (any layout, `#` comments, `A, B = (...)`, quotes) +
  `TnsNamesImporter`: **alias = connection name, full descriptor collapsed to
  one line = data source** (self-contained: works under any service account),
  schema and password left empty for the user. Idempotent: existing names are
  never touched, so filled-in credentials survive a re-import. A name defined
  several times in the file keeps its first definition and is reported
  (identical vs different). An imported connection is "not ready"
  (`OracleConnection.Complete` false): `Test` and `OpenAsync` answer a clear
  message (`ConnectionNotReadyException`) instead of an ORA error. The list
  shows `schema@host:port/service` (`DataSourceSummary`), flags what is still to
  fill in, and has a filter box. 235 connections were imported; `LAVAL.TEST` is
  defined twice in the user's file with different targets (first kept).
- Duplicate a connection (done, 11 Nov 2026): a **Duplicate** button in an
  existing connection's edit dialog opens the *new*-connection form prefilled
  (name "X copy", then "X copy 2"..., same data source and schema); **nothing is
  created until Save**, so Cancel leaves no stray copy. The password is never
  sent to the browser, so the form carries `copyFrom` (the original's id) and a
  **blank password means the original's** (`StoredPassword`, also used by the
  dialog's Test). It copies the *saved* connection, not unsaved edits in the
  dialog. If the original is deleted before saving, the password is required.
- Batch delete of connections (done, 11 Nov 2026): checkbox per row + select-all
  on the Oracle page, a live "N selected" and a "Delete selected" button that
  opens a confirmation listing exactly the connections to go (`ConfirmDelete`
  deletes nothing; `DeleteMany` does, in one transaction, in slices of 500).
  **Generic client side** (site.js): a list marked `data-selectable` with
  `[data-select-item]`, `[data-select-all]`, `[data-select-count]`,
  `[data-select-action]` gets it for free (shift-click ranges; "all" = the rows
  shown, not the ones the filter hides; **the filter deselects what it hides**,
  so nothing out of sight is ever acted on). Reusable for bookmarks/queries.
  The JS was verified once with jsdom against the real rendered page (25
  checks), not kept as a repeatable test; the server side is in
  `OracleBatchDeleteTests`.
- Queries (done): `Modules/Queries`, pages `/Queries` (list) and
  `/Queries/{id}` (run). A saved query = name, free-text category (panels, like
  bookmark groups), default connection **by name** (renaming a connection
  leaves its queries to be re-picked), SQL, notes, last bind values (JSON).
  **Read-only by design**: `SqlScanner` (comments/literals/q-quotes aware)
  accepts one SELECT/WITH only (a closing `;` is dropped) and finds the `:name`
  binds; `QueryRunner` runs it after `SET TRANSACTION READ ONLY` and always
  ROLLBACKs (pooled connection). Binds are always passed as text. Row limit 500
  default, 10,000 max on screen, 100,000 for CSV (`QueryCsv`, UTF-8 BOM);
  timeout 120 s; leaving the page cancels the statement. Pages depend on
  `IQueryRunner` so tests use a fake. The CSV button posts natively through
  `data-post-form` (site.js) because htmx cannot save a file.
- Tests: `tests/Dynamo.Tests` (NUnit 5, `dotnet test`): scanner, CSV and the
  queries pages end to end in memory (`WebApplicationFactory<Program>`; the
  fake runner means **no test touches Oracle**). A real Oracle run has only
  been tried by hand.

## Roadmap (user's list, in no fixed order)

Bookmarks (done) → Oracle connections (done) → common database queries (done)
→ command-line tools → scripts → accessed links history / other. Ideas noted
for queries, not built: an ad-hoc SQL console, non-SELECT statements,
a connection rename that updates the queries' default connection.

## Conventions

- One folder per use case: `Modules/<Name>` (IModule, migrations, repository)
  and `Pages/<Name>` (Razor pages, htmx fragments).
- Migrations are append-only per module; never edit a shipped one.
- A page handler answers htmx requests with a fragment; errors re-show the
  dialog (`HX-Retarget: #dlg`), notices use the `toast` HX-Trigger event.

## Known gaps

- No file logging (console/Event Log). Bookmarks drag and drop is only checked
  by hand (no browser test).
