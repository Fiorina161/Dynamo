# CLAUDE.md — Dynamo

Self-hosted local Windows site for a programmer's tools (see README.md for
run/install and architecture). Work directly in this checkout; no worktrees.
Comments wrap at column 80. Commit and push only when the user asks.

## Git

Solo repo: `master` is the main line; no pull requests, no review branches.

**In an interactive session Claude handles git itself**: `git fetch` first,
check `git branch --show-current` before every commit or push, stage explicit paths,
and when the user says "push" or "commit and push" run `git push origin master`.
Commit subjects are short imperative English ("Add ...", "Fix ..."), with the
Co-Authored-By trailer. Never force-push. `.claude/settings.json` (checked in)
pre-approves these commands and denies force-pushes.

**A background session** (a job started without a terminal) is forbidden by its
harness from pushing to `master`, whatever the user asks. There Claude commits
on a working branch, pushes that branch, and gives the user the one command that
fast-forwards `master`:

    git push origin <branch>:master

Then `git checkout master && git pull`, and delete the branch.

## Decisions made with the user

- Stack: ASP.NET Core 10, Razor Pages + htmx (vendored, offline), Sqlite via
  Dapper. No Node build. NuGet uses the repo's `nuget.config` (nuget.org only:
  the machine's global feed answers 401).
- Home: `DYNAMO_HOME`, else `C:\dynamo`; `dynamo.db` inside. Port 4040,
  **all interfaces** since 16 Nov 2026 (it was loopback only;
  `DYNAMO_LOCAL_ONLY=1` restores that). Windows service runs under an
  administrator account (tools may need elevation); console when not launched by
  the service manager.
- Access (done, 16 Nov 2026; replaces "no authentication, local only"). Use case
  from the user: the service runs on a VM, the browser is on the laptop.
  **This computer is never asked** (`ClientAccess.IsLocal`: loopback or the
  connection's own local address; **a request with X-Forwarded-For / Forwarded /
  X-Real-IP is always remote**, so a local reverse proxy cannot bypass it).
  **Another computer: 403 page while no password is set; with one, it logs in**
  (`AccessMiddleware` runs before static files; open: `/Login` and
  `/css`, `/js`, `/lib`, `/favicon.svg`; htmx gets 401 + `HX-Redirect`).
  Password in the `setting` table (`Modules/Settings`, key `access.password`) as
  **salted PBKDF2-SHA256, 600,000 iterations, never in clear** (the Oracle
  passwords stay in clear: they must be sent to Oracle). Session = random token
  in a cookie **without Expires** (lasts until the browser closes), HttpOnly,
  SameSite=Strict, not Secure (plain http), kept in memory in `SessionStore`
  with the password stamp, so **changing or removing the password ends every
  session** and a service restart does too. `LoginThrottle`: 5 failures in a
  row from an address lock it 1 minute. **Settings** (menu, last): set /
  change / remove; **from another computer the current password is required**
  (also throttled); a remote user who changes it stays logged in. Antiforgery
  stays on (login and logout are Razor pages; a `<form action=...>` gets no
  token unless it uses `asp-page`: that bug was caught by a test).
  **Bookmarks to local targets are shown off (greyed) to another computer and
  `Open` refuses them** (they would start programs on the server's desktop).
  **https (done, 16 Nov 2026)**: second port 4343 (`DYNAMO_HTTPS_PORT`, 0 = off,
  also off with `DYNAMO_LOCAL_ONLY`), Kestrel `ServerCertificateSelector` reads
  `CertificateService.Current`, so a renewal needs no restart. **Self-signed by a
  local CA** (no public domain name, private VM: Let's Encrypt is impossible):
  CA RSA-3072 valid 10 years, created once; server cert RSA-2048 valid 397 days
  signed by it, SAN = machine name, FQDN, localhost, every IPv4; both stored as
  PFX (with key) in the `setting` table (`tls.ca`, `tls.leaf`, `tls.names`).
  **The user trusts the CA once; renewals stay trusted** (a plain self-signed
  cert would need re-trusting yearly). `Ensure()` runs at start and every 6 h
  (`CertificateRenewalService`): reissues when < 30 days left, when the names or
  addresses changed, when forced (Settings), or when the stored cert is
  unreadable. Another computer on http is 307-redirected to https
  (`tls.require_remote`, default on; this computer never; the CA download
  `/dynamo-certificate.cer`, public part only, stays on http and is the one path
  open before login); session cookie is `Secure` over https. Verified over real
  sockets (chain validated with only the downloaded CA against every address)
  and **as LocalSystem** through the packaged installer with a throwaway service
  `DynamoTest` (ports 4043/4445). Not verified: the firewall rule creation
  (in one test the rule was not visible afterwards; the installers now
  verify it and warn). **The firewall is opt-in** (`-OpenFirewall`
  / `-OuvrirPareFeu`: inbound rule `Dynamo (remote access)` for the http and
  https ports, default LocalSubnet + Domain,Private; removed by the
  uninstallers). Verified over a real socket (own LAN address,
  proxy headers, login, cookie attributes) and in memory with a test header
  `X-Test-Client` (`RemoteAccessTests`). Antiforgery stays on on purpose: it
  stops a web page from making the browser launch bookmarks.
- Local-file bookmarks are opened by the server (`Launcher` -> `Core/
  DesktopLauncher`). **A service runs in session 0 (no desktop): anything it
  starts with Process.Start is invisible, which made bookmarks "do nothing"
  (12 Nov 2026, found with the real service: SessionId 0 vs the user's 3).**
  From a service, `DesktopLauncher` starts `cmd /c start "" "<target>"` in the
  **active console session** with the user's own token (`WTSQueryUserToken` +
  `DuplicateTokenEx` + `CreateEnvironmentBlock` + `CreateProcessAsUser`,
  desktop `winsta0\default`, no console window, waits <= 10 s to report a
  non-zero exit code); this needs **LocalSystem** (else a message, never a
  silent nothing). As a console app it still uses `Process.Start`. Not
  supported: a remote-desktop-only session (the active console is used); no
  one signed in -> "No user is signed in". The token step can only be tested
  as LocalSystem; the rest is tested with a copy of the test process's own
  token (`DesktopLauncherTests`). Reuse `DesktopLauncher` for any future
  "run a program for the user" feature (command-line tools module).
- UI: sober, minimal color (one accent), light/dark/system theme. **Small
  screens (< 760 px, a phone)**: the sidebar becomes a sticky top bar and the
  menu (links, theme, log out) folds behind a hamburger button (`#menu-toggle`,
  `.side.open`; closes on Escape or a tap outside); larger tap targets and
  tighter margins. The desktop layout is unchanged. **Menu icons**: each module
  supplies its own (`IModule.Icon`, the inside of a 24x24 `<svg>`, strokes only,
  drawn by hand: no icon CDN or font, Dynamo works offline and the VM may have
  no internet); they take `currentColor`, so they are monochrome and follow the
  theme and the active state.
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
  (default `%TNS_ADMIN%\tnsnames.ora`; for a Windows service `TNS_ADMIN`
  must be set machine-wide, not just for the user).
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
  fill in, and has a filter box. A file with hundreds of aliases imports fine;
  an alias defined twice with different targets keeps its first definition.
- Pick-and-import from the UI (done, 12 Nov 2026): an **Import tnsnames.ora**
  button on the Connections page, **only when a file exists**
  (TnsNamesLocator.Find: $TNS_ADMIN\tnsnames.ora, else $ORACLE_HOME\network\
  admin; read at request time, so a service needs machine-wide variables). It
  opens _ImportForm: the aliases that are not connections yet (Inspect),
  **none ticked**, flagged when defined twice, with select-all + filter (the
  generic data-selectable/data-filter hooks; Enter in the filter does not
  submit). Import(file, only) re-reads the file and imports just the picked
  aliases (never touches an existing connection); the client posts alias names,
  never a path. The CLI import-tnsnames still imports everything. jsdom
  check (15 checks) kept in the scratch dir; server side in
  TnsNamesImportPageTests.
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
- Auto-refresh of a query (done, 11 Nov 2026): client side only
  (`initAutoRefresh` in site.js, markup `data-auto-refresh="#run"` on the run
  page; no server change except a "fetched at" time in the results header).
  Re-submits the htmx form every N s (1..3600). **Decisions**: the next run is
  scheduled *after the previous answer* (no overlap on a slow query); **a failed
  run stops it** (a wrong password must not be retried: account lockout); each
  refresh reads the live form values; scroll position of the table is kept; the
  interval is remembered per query in localStorage; it dies with the page. It
  detects failure by an `.error` in the swapped `#results`. The interval input
  has no `name` so it is never posted. Verified with jsdom (fake clock +
  fake htmx, then real htmx.min.js against a stand-in server; jsdom needs
  `XPathEvaluator` stubbed for htmx), scripts kept only in the scratch dir,
  not in the repo.
- Workspaces (done, 12 Nov 2026; first "watches", then "data workplaces": the
  label is now "Workspaces" but code, folders and the `/Workplaces` address keep
  the second name, as `/Oracle` does for "Connections"): `Modules/Workplaces`,
  pages `/Workplaces` (list) and `/Workplaces/{id}` (the old `/Watches...`
  addresses redirect). **Storage keeps the first name** (tables `watch`,
  `watch_panel`, module id `watches`: migrations are append-only). A workspace =
  name + shared bind values (JSON) + panels. **A panel owns its query** (decided
  12 Nov 2026): `watch_panel` has `name`, `sql_text`, `last_params` (seed
  values), `connection_name`, refresh seconds, `max_rows`, position; **no link
  to `saved_query`**. The saved queries are *templates*: "Add from saved"
  (`AddCopies`) copies name/SQL/connection/last values into a new panel, "New
  query" (`AddNew`) writes one in the workspace (never added to Queries);
  editing a panel (`SetPanelQuery`, same rules as a saved query: name <= 100,
  one SELECT/WITH) or the saved query never touches the other. Deleting a saved
  query leaves panels alone. Migration 3 rebuilt `watch_panel` and turned every
  existing panel into a copy (effective connection = its own, else the query's);
  rehearsed on a copy of the real database (same panels, intervals, row limits,
  shared values). A blank connection posted by `PanelSettings` leaves the
  connection unchanged. **Bind values are per workspace, by name** (a name used
  by several panels is one input; no per-panel values). Panels run through
  `ViewModel.OnPostRunPanel` (per-panel `max_rows`, default 500, 1..10,000; no
  CSV); results table is the shared `_ResultTable` partial. **UI choices**: a
  panel is one title line + results, details/settings behind a gear (client-side
  only); **← → buttons move a panel** (`MovePanel`, answers `HX-Refresh`).
  **Drag and drop was built and removed on request** (12 Nov 2026: the
  owner did not like how it worked; do not bring it back unasked). Refresh is
  client side, per
  panel: next run after the previous answer, **a failed run stops that panel
  only**, nothing runs while the tab is hidden, Pause all / Run all, staggered
  first runs (on window `load`, after htmx is ready), cells that differ from the
  previous result flash (same columns, by position), row count delta in the
  details. Structural changes (add/remove/rename/edit) answer `HX-Refresh`.
  Rename, **Duplicate** (name dialog prefilled "X copy";
  `WorkplaceRepository.Duplicate` copies the panels with their own queries,
  settings and values). The bookmarks `dragend` handler ignores drags it did not
  start (a fix kept from the drag attempt). Not built: per-panel bind values,
  "workspace from selection" on the Queries list, "save this panel as a query",
  panel sizes. Scripts verified with jsdom (refresh: 26 checks, real htmx,
  stand-in server), kept only in the scratch dir; the server side is in
  `WorkplacesTests` and `WorkplaceMigrationTests`.
- Sortable results (done, 12 Nov 2026): **client side only**, in site.js, for
  every `.results table` (the shared `_ResultTable`, so the Queries run page
  and the workspace panels). Click a header = ascending, again = descending
  (no "unsorted" state; **Shift-click or Escape restores the query's order**,
  rows carry `data-row`). Numeric columns (`th.num`) compare as numbers, text
  with `localeCompare(numeric, base)`, **NULLs always last**, ties keep the
  original order. Only the fetched rows are sorted, not the database's.
  The choice is kept on the stable container (`#results`, a panel's
  `.panel-results`) and re-applied after every swap if the column (by index
  and header name) is unchanged, else dropped; a panel re-snapshots after a
  manual sort so change highlighting does not flash falsely
  (`resultsorted`). Headers are focusable (`tabindex`, Enter/Space). jsdom
  check (41 checks, both pages) kept in the scratch dir.
- Versions and the update notice (done, 16 Nov 2026). The repository is meant to
  become **public**, so releases need no token. `<Version>` in `Dynamo.csproj`
  (1.0.0) is the single source: the zip is `Dynamo-<version>.zip`, the GitHub
  release is tagged `v<version>`, `AppVersion.Current` reads it. `UpdateChecker`
  asks `api.github.com/repos/Fiorina161/Dynamo/releases/latest` (no auth) at
  start +45 s, daily, and on "Check now" (Settings, section Updates, also the
  on/off checkbox `updates.check`, default on; `DYNAMO_UPDATE_CHECK=off` removes
  the background service, the tests set it). Strictly newer than the running
  version = update (tags `v1.2.3`, `1.2`, `2` all parse). It only **informs**
  (menu note "Version X available" for this computer only, Settings link to the
  zip and the notes); **no download or install, by design**: self-updating a
  LocalSystem service needs signed releases and a swap with rollback, not built.
  Only https links into `github.com/Fiorina161/Dynamo/` are ever shown (anything
  else falls back to the releases page). Failures are messages, never errors.
  Tested with a fake GitHub (`UpdateTests`).
- Going public (16 Nov 2026): history and files scanned (no secrets, identity or
  machine names), personal references scrubbed from these notes and the README,
  `THIRD-PARTY-NOTICES.md` added (Oracle's driver licence must be included with
  every distribution: `packaging/licenses/`, copied into the zip by
  `package.ps1`), README Security section, history squashed to one commit.
- Install folder hygiene: `scripts/install-service.ps1` publishes a
  framework-dependent build; if the folder still holds the self-contained
  package's runtime files (`hostfxr.dll`...), the service fails with "No
  frameworks were found" (found 16 Nov 2026), so the script now empties a folder
  that already holds `Dynamo.exe` before publishing. The packaged installer
  mirrors with robocopy, so it is already clean.
- **Releases are handled by Claude** (decision of 16 Nov 2026), with
  `scripts/release.ps1 -Bump patch|minor|major` (or `-Version X.Y.Z`, `-DryRun`
  to preview). When the owner says "release": run the dry run, tell the owner
  the version and the changes, then run it for real. It checks a clean `master`
  in step with origin, `gh` signed in, bumps `<Version>`, runs `dotnet test`,
  writes `CHANGELOG.md`, commits "Release X.Y.Z" and pushes, builds the zip,
  creates the GitHub release `vX.Y.Z` (tag on that commit) with notes (commit
  subjects since the last release) and the SHA-256, then **verifies from the
  outside** (downloaded file's hash, public "latest release" tag). Semver: patch
  for fixes, minor for features, major for a break. Commit subjects are release
  notes: write them for users. The first release is tagged `1.0.0` (no `v`);
  Dynamo's update check accepts both. **`gh` must be signed in**: an old
  `GITHUB_TOKEN` environment variable overrides it and made `gh` fail with 401
  (removed from the user's environment on 16 Nov 2026): if it fails again, check
  that variable first. Needs internet; the token scopes are repo and workflow.
- Packaging (done, 12 Nov 2026): `scripts/package.ps1` builds
  `Dynamo-<version>.zip` in Downloads: self-contained win-x64 publish (`app`,
  ~112 MB, zip ~50 MB) + `packaging/` (French guide `LISEZMOI.txt`,
  `Installer-Dynamo.ps1`/`Desinstaller-Dynamo.ps1` and their `.cmd` launchers)
  + `VERSION.txt` (date, commit, "modifications non validees" if dirty). The
  installer self-elevates, installs as **LocalSystem**, replaces an existing
  service (update), keeps the data, and takes `-Port`, `-DossierDonnees`,
  `-DossierInstallation`, `-NomService` (port and data folder go in the
  service's own registry `Environment`, so no machine-wide variable). Scripts
  and guide are written **UTF-8 with BOM** by `package.ps1` (Windows PowerShell
  5.1 and Notepad need it for accents). Verified with a throwaway service
  `DynamoTest` on port 4043 (install, update over it, uninstall; the real
  service untouched). The guide must be kept in step with the features.
- Tests: `tests/Dynamo.Tests` (NUnit 5, `dotnet test`): scanner, CSV and the
  queries pages end to end in memory (`WebApplicationFactory<Program>`; the
  fake runner means **no test touches Oracle**). A real Oracle run has only
  been tried by hand.

## Roadmap (in no fixed order)

Bookmarks (done) → Oracle connections (done) → common database queries (done)
→ workspaces (done) → command-line tools → scripts → accessed links
history / other. Ideas noted for queries, not built: an ad-hoc SQL console,
non-SELECT statements, a connection rename that updates the queries' default
connection.

## Conventions

- One folder per use case: `Modules/<Name>` (IModule, migrations, repository)
  and `Pages/<Name>` (Razor pages, htmx fragments).
- Migrations are append-only per module; never edit a shipped one.
- **Query results look and behave the same on the Queries run page and in the
  workspace panels (decided 18 Nov 2026): one code path.** Running and CSV
  export go through `Pages/Queries/QueryActions` (run -> `ResultsModel`, CSV,
  bind parsing); the summary line (time · rows · ms) is the partial
  `Shared/_ResultSummary`, the table `Shared/_ResultTable`, the button
  `Shared/_ExportButton`. A panel shows the summary in its title bar through an
  htmx out-of-band swap (`_PanelResults`, `#panel-count-<id>`). A change to how
  results are shown or exported goes there, never into one page only. Still
  **Height and row numbers (20 Nov 2026):** the shared `_ResultTable` has a
  `#` column (`.rownum`, numbers are a CSS counter, so they follow the order
  after a sort and are not copied; the header is not sortable). The height is a
  number of rows (default 10, 1..1000) set by an input marked
  `data-view-rows-for="<results container>"` (site.js `fitResults`: header + the
  first N rows measured, so no per-row height is assumed). Queries page: input
  "Visible rows", not posted, remembered in localStorage per query; panels:
  `watch_panel.view_rows` (migration 4), saved with the other panel settings and
  copied by Duplicate. Checked with jsdom and a faked `offsetHeight` (no layout
  there), so the real pixel height has not been seen. **Pinned rows (20 Nov
  2026):** click a row to pin it to the top (again to unpin; the `#` header
  unpins all); pinned rows stay first whatever the sort and keep the sort's
  order among themselves, tinted with an accent bar (`tr.pinned`). Client side
  only, in site.js next to the sort (`sortTable` applies them, so every
  re-sort and refresh keeps them). **A row is its first column's value plus its
  rank among rows with that value**, so a pin survives a refresh even when the
  other cells change (it would be lost if the first column changes); kept per
  results container in localStorage (`pins:<path>#<container id>`). A click
  with text selected, or on a link/button, does not pin. jsdom check (15
  checks) in the scratch dir. Still
  separate on purpose: the refresh timers (`initAutoRefresh` for one query,
  `initWorkplace` for many panels) and the panel-only change flash. Checked
  with jsdom + the real htmx (scratch dir `oob.js`), not kept in the repo.
- A page handler answers htmx requests with a fragment; errors re-show the
  dialog (`HX-Retarget: #dlg`), notices use the `toast` HX-Trigger event.

## Known gaps

- No file logging (console/Event Log). Bookmarks drag and drop is only checked
  by hand (no browser test).
