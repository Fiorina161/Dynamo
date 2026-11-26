# CLAUDE.md — Dynamo

Self-hosted local Windows site for a programmer's tools (README.md has the
features and the install; this file holds the design notes and decisions). Work directly in this checkout; no worktrees.
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
  passwords are encrypted, not hashed, since 26 Nov 2026: they must be sent to
  Oracle, see "Secrets in the database"). Session = random token
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
- **Secrets in the database (done, 26 Nov 2026; the owner chose "option 1" of a list
  of five: DPAPI at machine scope).** `Core/Secrets` (static): `Protect` /
  `TryUnprotect` with `ProtectedData` at `DataProtectionScope.LocalMachine` (fixed
  entropy `Dynamo secrets v1`), stored as `dpapi1:` + Base64; the package
  `System.Security.Cryptography.ProtectedData` is pinned in the csproj (it was only
  transitive). **Machine scope on purpose:** the service runs as LocalSystem and the
  console as a user, both must decrypt (a per-user scope would break that). What it
  protects: a copy of `dynamo.db` read elsewhere. What it does **not**: any account on
  this machine (they can decrypt), a stolen disk or VM image (the machine key is on it).
  The owner was told so. **Covered:** `oracle_connection.password` (the repository
  protects on `Save` and reveals on every read: `Reveal` in
  `OracleConnectionRepository`; `OracleConnectionInfo.Password` is clear **in memory
  only**) and the two secret settings `tls.ca` / `tls.leaf` (`SettingsRepository.
  GetSecret` / `SetSecret`, used by `CertificateService`). **Not covered:**
  `access.password` (already a salted hash), the other settings. **Conversion at start**
  (`Program.cs`, right after the migrations, before the certificate is made):
  `Secrets.ProtectStored` converts what is still in clear, **reads each value back
  before replacing it**, is idempotent (values with the prefix are left alone) and
  keeps no backup file (it would hold the clear passwords). A legacy value without the
  prefix is read as it is, so tests and old databases keep working. **A protected value
  that cannot be decrypted here** (database restored on another computer, Windows
  reinstalled) is `PasswordUnreadable`: password empty, connection "not ready" with its
  own message and list text ("password cannot be read here: enter it again"); saving
  with a blank password is refused, so the lost one is never "kept"; an unreadable
  certificate is simply issued again (clients must trust the new authority). The
  `dpapi1:` prefix could in theory clash with a clear password that starts with it:
  ignored on purpose. **Rehearsed on a copy of the real database** (4 connections: all
  converted, each decrypts to the same SHA-256 as before, both certificate keys read
  back; the real database was not touched). Tests: `SecretsTests` (14).
- **Shared folders (done, 26 Nov 2026; the owner's request: "list and download files
  in a shared folder, ask for user and password and save them"; first candidate
  was a folder of an internal ftp server; the dialog's hints are generic ("e.g. ...") on purpose:
  a grey hint that looked like a value made the owner save an empty Folder field, 26 Nov 2026).**
  `Modules/Shares`, pages `/Shares` (list + dialog) and `/Shares/{id}` (browse; handler
  `Download`), menu "Shared folders" (order 36: after Workspaces, 26 Nov 2026, owner's request). Table `shared_folder` (name, path,
  username, password); **the password is protected like the Oracle ones** (`Secrets`,
  `Reveal` / `PasswordUnreadable` in `SharedFolderRepository`, same "enter it again"
  behaviour). **Network paths only (`SharePath.Parse`): `\\server\share[\folder]`, a user
  and a password are required.** This is a security decision: Dynamo can run as LocalSystem
  and anyone logged in can add a folder, so a local path (`C:\`) would let a remote user
  download anything LocalSystem can read (including `dynamo.db`); with an explicit
  account the reach is what that account can read over the network. Never relax it. `Resolve`
  makes every path from the address bar safe (no `..`, no `.`/`...`, no drive, no `:` so no
  stream names, no wildcard; a leading `\` is read as relative, so it stays inside; the result
  must start with the root). **Login:** `ShareConnector` (`IShareConnector`, faked in tests)
  = `WNetAddConnection2` to `\\server\share` with the saved account, no drive letter, before
  each listing or download (this is how a LocalSystem service reads a share); on error 1219
  (one account per server per process) it drops the connections it made to that server and
  retries; any other Windows error becomes a message (`Describe`). `ShareBrowser`
  (`IShareBrowser`): `ListAsync` (folders first, then name/size/date, filter, pages of 200,
  at most 50,000 entries read, breadcrumb), `OpenAsync` (`FileShare.ReadWrite | Delete` so
  a trace still being written can be read, range requests on), `TestAsync` (the dialog's Test:
  connect + count). Read only by construction (no write, delete or rename code exists).
  Downloads are logged with the client address. A non-UNC path skips the login (only the
  tests use that: they register local temp folders directly in the repository, because the
  dialog refuses them). Tests: `SharedFolderTests` (60: path parsing and traversal, listing,
  sort, filter, paging, open, login with the fake connector, the dialog's validation, the
  encrypted password, pages, real HTTP range download). A smoke test calls the real
  `WNetAddConnection2` against a share that does not exist, to prove the Windows call works.
  **Not verified: a real network share** (no account to one was available): the login with real
  credentials, listing and downloading from a real `\\server\share`, and the LocalSystem
  service reading it. Try it once with the real folder. Verified in a real Edge on a local demo
  folder (list, browse, sort, paging, dialog; download, range and traversal over real HTTP).
- **Shared folders, navigation (26 Nov 2026, owner's request: "folders should be
  visually distinct" and "when I drill into a folder I don't know how to go back").**
  Folder rows are `tr.folder-row` (a tint of the accent, a bar on the left, a bold name,
  a `▸` in the accent colour). Three ways up, shown only below the top: the **↑ Up**
  button (`.up-btn`) and the `..` row (`.up-row`, first page only) both go to the parent
  (`listing.Breadcrumb[^2]`, or the top of the share), and the breadcrumb, whose parts are
  underlined links with the current folder in bold and not a link. At the top of the
  share there is no Up. The Up link keeps the name filter and the sort, like every other
  link of the page.
- **Substring filters of a shared folder (done, 26 Nov 2026, owner's request).** Table
  `shared_folder_filter` (module migration 2: folder_id with `ON DELETE CASCADE`, text,
  `active` = ticked, position); `SharedFolderRepository.GetFilters / SaveFilter /
  DeleteFilter / SetFilterActive / ClearActiveFilters / FilterProblem`. **The tick is
  stored in the database (per folder, shared by every browser)**, so every link (sort,
  page, sub-folder) keeps the filter without carrying it in the address. **Semantics:**
  nothing ticked = every file; otherwise a *file* is listed when its name contains **any**
  ticked substring (OR); **folders are never filtered by the substrings** (the tree must
  stay walkable) but the name filter box (`q`) still applies to everything, and the two
  combine with AND. **Case and accents never matter (owner's request: é, è and e match e):**
  `TextMatch.Fold` (FormD, drop NonSpacingMark, lower case; independent of the culture; `œ`
  and `ø` do not decompose and stay) is used for the substrings, for the name box, and for
  the duplicate check ("été" and "ETE" are the same substring). UI: the "Substrings" box
  above the file table (chips with a checkbox that posts `ToggleFilter` and a ✎ that opens
  `_FilterForm`; "+ Add"; "Untick all"); every change answers `HX-Refresh`, so the page
  reloads on the same address. Handlers check that a filter belongs to the folder in the
  address (404 otherwise). `ListAsync` got the optional trailing parameter `anyOf` (after
  the cancellation token) to keep the existing callers. Tests are in `SharedFolderTests`.
- Fonts (24 Nov 2026, owner's choice after trying Saira Semi Condensed, then
  Iosevka + Michroma, which "did not look good on my screen"): **plain system
  fonts, no font files**: `--font: sans-serif` for the text, `--font-title:
  "Arial Black", Arial, sans-serif` for the titles (section titles, bookmark
  titles, result column headers, page title, menu name; one weight, so 400 and
  smaller sizes; serif was tried first and dropped, 24 Nov 2026). Do not
  add web fonts unasked; if one is ever wanted it must be bundled (Dynamo works
  offline, never a CDN link). Code and SQL keep Consolas.
- Every setting is edited in the web UI and stored in Sqlite.
- Oracle connections (done): `Modules/OracleConnections`, page `/Oracle`.
  A connection is a unique name (case-insensitive) + data source (TNS alias
  via TNS_ADMIN, Easy Connect `host:port/service`, or a full descriptor) +
  schema (user) + password, in table `oracle_connection`. The password is
  encrypted in the database (see "Secrets in the database"), never rendered back in HTML; a
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
- **Queries section removed (23 Nov 2026, the owner's decision: it was
  redundant, "a workspace of one query").** No `/Queries` page, no menu entry, no
  saved queries, no `QueryRepository`, no "Add from saved". What remains of
  `Modules/Queries` is the machinery workspaces use: `SqlScanner` (comments/
  literals/q-quotes aware; accepts **one SELECT/WITH only**, a closing `;` is
  dropped; finds the `:name` binds), `QueryRunner` (runs it after `SET
  TRANSACTION READ ONLY` and always ROLLBACKs; binds always passed as text; row
  limit 500 default, 10,000 max on screen, 100,000 for CSV; timeout 120 s;
  leaving the page cancels the statement), `QueryCsv` (UTF-8 BOM),
  `QueryErrorText`; pages depend on `IQueryRunner` so tests use a fake. The CSV
  button posts natively through `data-post-form` (htmx cannot save a file).
  **`QueriesModule` still exists, with an empty `Href`, only to keep its two
  tables and migrations (`saved_query`, `collapsed_category`): the layout and
  the landing page skip modules without an `Href`. Those tables are never read
  and never dropped** (nothing is deleted from the owner's database).
  **Data migration (Workplaces migration 5):** every saved query became a panel
  of a new workspace "Saved queries" ("Saved queries (from Queries)" if that name
  was taken), named "Category / Name" when it had a category, ordered as the page
  listed them (categories first); notes were dropped; no workspace is created
  when there was no saved query. **Rehearsed on a copy of the real database**
  (two saved queries, Ambulances and Services externes, none already a panel)
  and tested in `WorkplaceMigrationTests`. The page-level auto-refresh
  (`initAutoRefresh`, "Visible rows" input with localStorage, `body.wide-results`)
  went with it; refresh is per panel (see Workspaces).
- Workspaces (done, 12 Nov 2026; first "watches", then "data workplaces": the
  label is now "Workspaces" but code, folders and the `/Workplaces` address keep
  the second name, as `/Oracle` does for "Connections"): `Modules/Workplaces`,
  pages `/Workplaces` (list) and `/Workplaces/{id}` (the old `/Watches...`
  addresses redirect). **Storage keeps the first name** (tables `watch`,
  `watch_panel`, module id `watches`: migrations are append-only). A workspace =
  name + shared bind values (JSON) + panels. **A panel owns its query** (decided
  12 Nov 2026): `watch_panel` has `name`, `sql_text`, `last_params` (seed
  values), `connection_name`, refresh seconds, `max_rows`, `view_rows`,
  position; no link to any other table. Panels come from **"New query"**
  (`AddNew`, written in the workspace) or **"Import from workspace"**
  (`ImportPanels`, 23 Nov 2026: replaces "Add from saved"; the dialog lists the
  *other* workspaces that have panels, a `<select>` re-renders it with `?from=`,
  the panels are ticked with the generic `data-selectable` hooks; each ticked
  panel is copied **with all its settings** (query, connection, interval, max and
  visible rows, seed values) to the end, in source order, once each; ids that are
  not the source's, or the same workspace, are ignored). Editing a panel
  (`SetPanelQuery`: name <= 100, one SELECT/WITH) never touches its original or
  other copies. Migration 3 rebuilt `watch_panel` and turned every
  existing panel into a copy (effective connection = its own, else the query's);
  rehearsed on a copy of the real database (same panels, intervals, row limits,
  shared values). A blank connection posted by `PanelSettings` leaves the
  connection unchanged. **Bind values are per workspace, by name** (a name used
  by several panels is one input; no per-panel values). Panels run through
  `ViewModel.OnPostRunPanel` (per-panel `max_rows`, default 500, 1..10,000; no
  CSV in the first version, since added); results table is the shared
  `_ResultTable` partial. **UI choices**: a panel is one title line + results,
  details/settings behind a gear (client-side only). **Layout (23 Nov 2026,
  replaces the grid): CSS columns** (`.workplace-grid { columns: 560px }`), so
  panels flow down the columns and leave no hole under a short panel; the order
  is a *sequence* (down column one, then two), not a screen position; a
  full-width panel is `column-span: all`. The cost of columns: the browser
  rebalances them by height, so a panel whose height changes makes the others
  hop; **each panel is therefore always exactly as tall as its Visible rows**
  (`fitResults` sets `min-height` = header + N rows on `.panel-results`; the
  measured row/header heights are remembered in localStorage `resultMetrics` so
  the first paint is right; a longer result scrolls, a shorter one, an error or no
  rows leaves blank space; the browser's scrollbar thickness, measured with a
  hidden probe, is added to every panel so N rows are N *whole* rows even with a
  horizontal scrollbar, and the panels stay the same height). **History: 23 Nov 2026 first "reserve N rows" (wasted
  room: 370 px for an error), then "the tallest it has held, capped by N" (a
  high-water mark kept in localStorage) to save space; on 24 Nov 2026 the owner
  said the number set acts as a max and wants it respected, so it is exact again.
  Do not bring the high-water mark back unasked.** "Max rows" (rows fetched) is a
  different setting and stays a maximum.
  **Reordering (23 Nov 2026): drag the ⠿ handle** (`data-panel-grip`) with
  **pointer events** (mouse, pen, touch; `touch-action: none` on the handle),
  a `.panel-drop-marker` line shows "before/after the panel nearest to the
  pointer", the DOM moves only on drop, the page autoscrolls near its edges,
  Escape cancels, and ArrowUp/Down on the handle move one place. **One POST
  `ReorderPanels` with the whole order** (antiforgery header from `hx-headers`;
  the list must be exactly the workspace's panels or it answers 409 and the page
  reloads; non-numbers 400). The earlier/later buttons (↑ ↓, `MovePanel`,
  `HX-Refresh`) remain for phones and as the fallback. **History: drag and drop
  was built and removed on 12 Nov 2026 (the owner did not like how it worked;
  what exactly was never said; its first save also failed); the owner asked for
  it again on 23 Nov 2026, so it is wanted. This version is handle-only, with a
  landing marker instead of moving panels live: if the owner dislikes it too,
  ask what bothers them.** Verified
  with jsdom (26 checks) **and a real Edge driven through the DevTools protocol
  with real mouse events** (script `cdp-drag.js` in the scratch dir: drag Alpha
  after Delta, Delta to the top, a click on the handle, reload; server order
  checked each time). **Collapsing (23 Nov 2026; the owner wanted it even though
  drag and drop worked):** `watch_panel.collapsed` (migration 6, default 0, copied
  by Duplicate and by an import), a ▾ button (`data-panel-collapse`) and a click
  on the title (`data-panel-title`) toggle `.panel.collapsed` at once and save
  with a POST (`CollapsePanel`, form `collapsed=true|false`; **fetch with the
  antiforgery header, a refused save reloads the page**); "Collapse all / Expand
  all" (`data-workplace-collapse-all`, `CollapseAll`) collapses every panel when
  any is open, else expands them all. CSS reuses the bookmark groups'
  `.group.collapsed` look and hides `.panel-form` and `.panel-results` with
  `display: none`: **a collapsed panel keeps refreshing** (its results area stays
  in the page; the height mark ignores a hidden area, and expanding calls
  `fitResults` again). The title line gets `[data-panel-info]` (filled in
  `initWorkplace.show()`): refresh state, row change (`lastDelta`), and the
  filter line. **The title bar of a panel is only "N rows · X ms" (24 Nov 2026: the
  owner asked to drop the "more rows exist, raise Max rows or export" hint, as
  they had the clock); the details status (⚙) still says "more rows exist" when
  the row limit was reached, so a cut result is never silent.** The ⚙ of
  a collapsed panel expands it (and opens its details). Verified with jsdom (19
  checks) and a real Edge on live Oracle data (collapse, info line, refresh while
  collapsed, Collapse all, reload, expand by title). Refresh is
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
  "save this panel as a query", panel sizes.
  Scripts verified with jsdom (refresh: 26 checks, real htmx,
  stand-in server), kept only in the scratch dir; the server side is in
  `WorkplacesTests` and `WorkplaceMigrationTests`.
- **DBA features (done, 24 Nov 2026; the owner's list, built in one go; none of
  it has been run against a real Oracle: see "Not verified").**
  **DBA connection:** `oracle_connection.dba` (migration 3) = log in AS SYSDBA
  (`OracleConnectionStringBuilder.DBAPrivilege = "SYSDBA"`, in
  `OracleConnector.ConnectionStringOf`); `OracleConnector.IsDba(name)`;
  `ConnectionChoice` carries `Dba` and `Color`. **Colour:** `oracle_connection.color`
  (migration 4), six pastels (`ConnectionColors`: blue, teal, green, sand, rose,
  violet; the shades are the `--conn-*` variables of site.css, light and dark); the
  panel's title bar takes it (`.panel.conn-<key> > header`, set by the server; site.js
  "Panel title colour" follows a change of the connection picker through
  `data-color` on each `<option>`). **Everything DBA is gated on a DBA connection and
  checked again on the server:** the buttons "Add DBA panels" / "New DBA workspace"
  exist only when one exists, `ViewModel.DbaProblem` refuses a non-DBA connection, the
  sessions panel refuses to run, to be moved to a non-DBA connection (400), or to kill.
  **Templates:** `Modules/Workplaces/DbaTemplates.cs`, hard-coded (`All`: key, group,
  title, description, SQL, refresh seconds, kind); to add one, add it there. They are
  ordinary panels once added (`WorkplaceRepository.AddTemplates`); a test checks every
  template is one SELECT/WITH with no binds. **Panel kind:** `watch_panel.kind`
  (migration 8 of Workplaces, `query` or `sessions`, copied by Duplicate and import).
  **Sessions panel and Kill:** `SessionKiller` (`ISessionKiller`, faked in tests) is the
  **only code in Dynamo that writes to Oracle**; guards: DBA connection, the session
  still exists with that serial, not `BACKGROUND`, not Dynamo's own session
  (`sys_context('USERENV','SID')`), integers only because ALTER SYSTEM takes no binds;
  ORA-00031 (marked for kill) counts as success; the request is logged with the
  client address; the red confirmation (`_KillForm`) is fetched with ids only and
  re-reads the session. `QueryRunner` stays read-only. The Kill column is built in
  `_ResultTable` from the columns INST_ID, SID, SERIAL when the panel passes
  `KillUrl`, and is the **first** column after `#` (a wide result scrolls sideways);
  `keyRows` (pin keys) skips it. **Plan:** `PlanRunner` (`IPlanRunner`):
  `EXPLAIN PLAN SET STATEMENT_ID` + `DBMS_XPLAN.DISPLAY`, then ROLLBACK, **not** in the
  read-only transaction (EXPLAIN PLAN inserts into PLAN_TABLE), available on every
  connection; format BASIC/TYPICAL/ALL only. **Health page** `/Oracle/Health`:
  `OracleConnector.ProbeAsync` (real, **unpooled** login + `select 1 from dual`, so
  times are honest; `TestAsync` is built on it); the page lists rows that each probe
  themselves (`hx-trigger="load, retest from:body"`; a row that has its answer does
  **not** ask on load again, or it would never stop). **Result tools** (site.js
  "Result tools"): the ⋯ menu copies the visible rows in screen order (TSV with Excel
  quoting, Markdown, formatted HTML + TSV; the copy uses a hidden textarea and the
  `copy` event because the clipboard API needs https and Dynamo may be on http),
  vertical view of a one-row result (`.results-vertical`, preference per panel in
  localStorage, re-applied after each swap and `resultsorted`), readable sizes (columns
  whose name matches `(^|_)bytes($|_)`: the raw number stays in the cell and
  `data-human` is shown by CSS `font-size:0` + `::after`, so sort, filter, copy and
  highlight still see the number), and the whole text of a cell: a click on a `.long`
  cell (over 120 characters, or visibly cut) or an Alt-click sends a `cellview` event and
  does **not** pin the row. Tests: `DbaConnectionTests`, `DbaFeaturesTests`,
  `ConnectionHealthTests`; jsdom (42 checks for the result tools, kept in the scratch
  dir) and static renders of the real pages in headless Edge (colours, dialogs, Kill
  column, vertical view, health). **Not verified:** any SQL of the templates against a
  real Oracle (written from the documentation, 12c and later in mind; the alert-log one
  needs 12c), the SYSDBA login, EXPLAIN PLAN through the driver (binds as text), the
  real `ALTER SYSTEM KILL SESSION`, and the health probe against a live database.
  Run them once on a test database before relying on them.
- **The "Visible rows" text box was removed (26 Nov 2026, owner's request: the
  resize bar replaces it).** The details (⚙) no longer show it; the field stays as a
  **hidden input** `name=viewRows` (same `data-view-rows-for` / `-default` and
  `hx-post`), because the resize bar saves through it. The **Settings default
  (`results.default_rows`) stays** and is what new panels follow (`view_rows` 0);
  `PanelSettings` still accepts `viewRows`. Double-click on the bar = back to 0.
- **Resizing a panel with the mouse (done, 24 Nov 2026).** A bar at the bottom of
  every panel (`.panel-resize`, `data-panel-resize`) drags the height by **whole
  rows**: it does not add a free pixel height, because a panel is exactly as tall as
  its Visible rows (the owner's rule, see Workspaces). So a resize **sets
  `view_rows`**: site.js ("Resizing a panel") puts the number in the details' own
  `input[name=viewRows]` and dispatches `input` (the height follows at once) while
  dragging and `change` on release, which makes the input's existing `hx-post`
  (PanelSettings) save it: **no server code, and the form carries `seconds` and the
  rest, so nothing else is reset** (PanelSettings sets the refresh to 0 when `seconds`
  is missing: never post it alone). Rows = `round((pointerY - top of .panel-results -
  head - 1 - scrollbar) / row)` from the remembered metrics, clamped 1..1000;
  Escape or pointercancel restores the old value; arrow keys on the bar = one row;
  double-click = 0 = the default. Verified with jsdom (18 checks, `resize.js` in the
  scratch dir) and a real Edge with real mouse events (`cdp-resize.js`: drag 160 px,
  server value, refresh unchanged, arrow key, double-click). Hidden when a panel is
  collapsed.
- Sortable results (done, 12 Nov 2026): **client side only**, in site.js, for
  every `.results table` (the shared `_ResultTable` in the workspace panels).
  Click a header = ascending, again = descending
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
  asks `api.github.com/repos/Fiorina161/Dynamo/releases/latest` (no auth) **only
  on "Check now" (Settings, section Updates); since 24 Nov 2026 (owner's
  decision) there is no daily look, no checkbox and no background service, so
  after a restart the menu note is empty until someone checks. The old
  `updates.check` row stays in the table unread, and `DYNAMO_UPDATE_CHECK` means
  nothing any more.** Strictly newer than the running
  version = update (tags `v1.2.3`, `1.2`, `2` all parse). It only **informs**
  (menu note "Version X available" for this computer only, Settings link to the
  zip and the notes). **"What's new since <running version>" (24 Nov 2026):** when
  a newer release exists, `UpdateChecker` also reads the project's `CHANGELOG.md`
  from the **fixed** address `UpdateChecker.ChangelogUrl`
  (`raw.githubusercontent.com/Fiorina161/Dynamo/master/CHANGELOG.md`, the only new
  host; nothing from an answer chooses it) and `ParseChangelog` keeps the entries
  *after the running version up to the latest* (so skipped versions are covered,
  which the release notes alone cannot do since only 3 releases are kept), newest
  first; format written by `release.ps1`: `## X.Y.Z - date` then `- subject`
  lines, **keep it**. Bounded (1 MB, 40 entries, 60 lines of 400 chars, control
  characters dropped) and **rendered as plain text** (Razor-encoded, no links).
  Best effort: a failure or an unusable file falls back to the latest release's
  own notes (`ItemsInNotes`, the bullets before its "### Install" heading), never
  an error; nothing is asked when no newer release exists. Tests in
  `UpdateTests` (parsing, defensiveness, skipped versions, fallback, plain text
  with a hostile changelog, only the two fixed addresses requested). Only https links into `github.com/Fiorina161/Dynamo/` are ever shown (anything
  else falls back to the releases page). Failures are messages, never errors.
  Tested with a fake GitHub (`UpdateTests`).
- **Update now (done, 20 Nov 2026; reverses the earlier "inform only, no
  install, by design"; the owner asked for a one-click upgrade).** Settings >
  Updates > button, `UpdateInstaller` (Modules/Settings) + the embedded script
  `apply-update.ps1`. **Only from this computer** (a remote browser gets 403
  and no button: whatever is installed runs as LocalSystem) and **only when
  running as a Windows service**. It re-asks GitHub, then accepts only the
  exact asset `https://github.com/Fiorina161/Dynamo/releases/download/<tag>/
  Dynamo-<version>.zip`, only a strictly newer version, **only with a SHA-256**
  (the asset's `digest`, else the line release.ps1 writes in the release
  notes: keep that format) matching the download, and the staged `app\Dynamo.exe`
  must report that version. Then PowerShell 5.1 runs the script detached: backup
  of the install folder to `<home>\update\backup`, stop the service, copy
  `dynamo.db` aside, robocopy /MIR the new files over `AppContext.BaseDirectory`,
  start, wait for `http://localhost:<port>/Bookmarks`; on any failure the old
  files and database copy are restored (`last-result.txt` says `failed...`,
  shown in Settings; log `<home>\update.log`). **The service registration is
  never touched** (name, account, env, firewall), unlike `Installer.cmd`.
  The page waits for the restart with `[data-wait-restart]` + `data-wait-version`
  (site.js): **it polls `/Settings` every second and goes to the new page as soon
  as the menu title (`.version`) shows the version being installed; coming back
  after a down is a second way out (a rolled-back update then says so), and it
  ends with `location.assign`, never `reload()` (the page came from a POST).
  Bug found 24 Nov 2026: it used to finish only after SEEING the site go down,
  but the restart lasts about two seconds, so a 2 s poll could miss it and the
  counter ran for ever (a refresh then showed the update done). jsdom check
  (7 scenarios incl. "never seen down") in the scratch dir.** **Limits
  to remember:** the checksum comes from the same GitHub account as the file and
  releases are not signed, so a stolen account would still get code onto the
  machine; the script is embedded in the running version, so the *first*
  version with the button is installed by hand. **Verified on the real service
  (20 Nov 2026, 1.5.0 -> 1.5.1, started from the Settings button by the
  owner):** the detached script survives the service's stop (children of a
  service outlive it), the whole swap took 7 s, the service kept its name,
  account (LocalSystem), start type and firewall rule, and `update.log` /
  `last-result.txt` read as designed. Not exercised on a real machine: the
  rollback (only on temp folders in tests), and a packaged install on another
  computer. Tests: `UpdateInstallerTests`
  (refusals, hand-over, Settings page, the script on temp folders incl.
  rollback).
- **History flattened (24 Nov 2026, owner's decision): the repository is one
  commit, "Dynamo 1.0.0", and the version restarts at 1.0.0** (it was 2.3.8; all
  older releases and tags were deleted from GitHub; CHANGELOG.md holds only 1.0.0).
  A full copy of the old history is the bundle `Dynamo-history-before-1.0.0.bundle`
  the owner keeps in Downloads (`git clone` it to read it). **Consequence: a
  machine still running 2.x never sees 1.0.x as an update** (only a strictly
  newer version counts), so it is reinstalled once by hand from the 1.0.0 zip.
  Older notes in this file that mention versions 1.5.x, 2.x are history.
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
  the version and the changes, then run it for real. **"Deploy" (decision of
  20 Nov 2026) = commit + push + release in one go, and never the install
  script: the owner installs through Settings > Updates > Update now.**
  "Install" alone still means `scripts/install-service.ps1`. **Only the newest 3
  releases stay on GitHub** (owner's decision, 20 Nov 2026: 17 were pointless):
  `release.ps1` deletes the older ones after each release (`-Keep 3`, `-Keep 0`
  to disable); their git tags and CHANGELOG.md stay, and the update check only
  reads the latest. The first 14 were deleted by hand that day.
  **The script** checks a clean `master`
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
- Menu option (done, 20 Nov 2026): Settings > Menu has a checkbox "Show
  Bookmarks in the menu" (`MenuOptions`, key `menu.hidden.<module id>` in the
  `setting` table; Settings can never be hidden). **It only hides the menu entry
  and makes `/` open the first visible section: the Bookmarks page itself still
  answers at `/Bookmarks`**, on purpose: the update script and the installers
  use `/Bookmarks` as their health check, and nothing that links there breaks.
  The page's notice/error now show at the top of Settings for every section.
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
  been tried by hand (on 23 Nov 2026 a copy of the real database, with its
  TNS alias, showed live Oracle results in the new layout). **Never look for a
  short text (a name like "D3") in a whole page: hashed `?v=...` addresses
  contain such strings by chance and the test then passes or fails with the CSS
  file's hash (this was the one flaky test); anchor on `data-panel="<id>"`.**

## Roadmap (in no fixed order)

Bookmarks (done) → Oracle connections (done) → workspaces of queries (done; the
separate Queries page was removed) → command-line tools → scripts → accessed
links history / other. Ideas noted for queries, not built: an ad-hoc SQL
console, non-SELECT statements.

## Conventions

- Architecture: ASP.NET Core 10, Razor Pages and htmx (vendored in `wwwroot/lib`, no
  CDN, no Node build), Sqlite through Dapper. `Core/` = home directory, database,
  migrations, the `IModule` contract. **Adding a use case:** create
  `Modules/<Name>/<Name>Module.cs` implementing `IModule` with its SQL migrations
  (append-only, applied once per module at startup), its repository/logic beside it
  and the pages under `Pages/<Name>/`, then register it in `Program.cs`
  (`AddSingleton<IModule, ...>`): it appears in the navigation.
- One folder per use case: `Modules/<Name>` (IModule, migrations, repository)
  and `Pages/<Name>` (Razor pages, htmx fragments).
- Migrations are append-only per module; never edit a shipped one.
- **Query results are shown by one code path (decided 18 Nov 2026, when there
  were two pages; now only the workspace panels use it, keep it that way).**
  Running and CSV export go through `Pages/Shared/QueryActions` (run ->
  `ResultsModel`, CSV, bind parsing); the summary line (rows · ms; **no clock since 24 Nov 2026, the owner did not want
  the time stamp in the title bar**: the details still say "last run HH:MM:SS") is the partial
  `Shared/_ResultSummary`, the table `Shared/_ResultTable`, the button
  `Shared/_ExportButton`. A panel shows the summary in its title bar through an
  htmx out-of-band swap (`_PanelResults`, `#panel-count-<id>`). A change to how
  results are shown or exported goes there, never into one page only. Still
  **Height and row numbers (20 Nov 2026):** the shared `_ResultTable` has a
  `#` column (`.rownum`, numbers are a CSS counter, so they follow the order
  after a sort and are not copied; the header is not sortable). The height is a
  number of rows (**0 = the default number of rows from Settings > Results,
  `ResultOptions.DefaultRows`, key `results.default_rows`, 1..1000, 10 out of the
  box; owner's request 24 Nov 2026, it used to be hard-coded 10**; otherwise
  1..1000 of the panel's own; new panels are created at 0, so they follow the
  setting; **migration 7 turned every panel at the old 10 into 0** (a number
  someone chose stays), the page puts the setting on the input as
  `data-view-rows-default` and `viewRowsOf` in site.js resolves 0 with it) set by an input marked
  `data-view-rows-for="<results container>"` (site.js `fitResults`: header + the
  first N rows measured, so no per-row height is assumed), saved per panel:
  `watch_panel.view_rows` (migration 4) with the other panel settings, copied by
  Duplicate and by an import. Checked with jsdom and a faked `offsetHeight` (no layout
  there), so the real pixel height has not been seen. **Column filters (20 Nov
  2026), spreadsheet style:** each header has a ▾ button (`button.col-filter`,
  no text, so header text-based logic is unaffected) that opens one shared list
  (`.filter-menu`, built in site.js): the column's distinct values with counts,
  a search box, "(Select all)" (applies to the values listed, so after a search
  it acts on those) and "Clear this filter"; NULL and the empty text are their
  own values. Ticks apply live. **A filter stores the values left out, not the
  ones kept**, so a value that appears in a refresh is shown rather than
  silently hidden; columns combine (all must pass). Rows are hidden with the
  `hidden` attribute, so the `#` counter numbers only the visible rows;
  `applyFilters` runs at the end of `sortTable`, i.e. after every swap, sort and
  pin, and a filter whose column is gone or renamed is dropped. A "Showing X of
  Y rows · Clear filters" line is added above the table; filters are kept per
  page in localStorage (`filters:<path>#<container id>`), like the pins. The
  highlight counts and the visible-rows height only consider shown rows
  (event `resultsorted`). Client side only, on the fetched rows. Checked with
  jsdom (26 checks) and a real Edge render of the open list. Not built: text
  conditions ("contains", "greater than"), filtering by colour, a filter on a
  column across several panels. **Full width (20 Nov 2026):** a ↔ button
  (partial `Shared/_WideButton`, `data-wide-toggle`) in the title bar of every
  result. It makes the panel span every column (`.panel.full-width`, a column
  span, kept per panel in localStorage, `wide:<path>#<panel id>`); the Queries
  page's "menu folds away" mode went with that page. **Do not
  call the class `wide`: `.group.wide` is the settings sections' max-width
  (caught in a render).** Checked with jsdom (12 checks) and a real Edge render.
  Not built: hiding the menu from a workspace panel, a width in pixels, lifting
  the 420px cap on a cell's width. **Highlight boxes (20 Nov
  2026; up to three since the same day: yellow, sky blue, green-yellow, so
  three things can be followed at once; **how many are rendered is a setting,
  0 to 3, default 1**: Settings > Results, `ResultOptions`, key
  `results.highlight_boxes`, 0 renders nothing, the stored text of a box that is
  no longer shown is simply not applied):** the partial `Shared/_HighlightBox`
  above the panels (scope `[data-workplace]`, so
  every panel at once; only rendered on a workspace that has panels). Typing marks the text, case-insensitive, with
  `<mark class="hl hl1|hl2|hl3">` in every cell (not NULL cells; each cell is
  cut up once from its plain text, so marks never nest, and where two terms
  overlap the lower box wins) and shows a match count per box;
  Escape or × clears; re-applied after each swap (refreshes); the text is kept
  per page in localStorage. Client side only (site.js "Highlighting"); marks
  only wrap the cell's own text node, so sort/pin/change-flash, which read
  `textContent`, are unaffected. jsdom check (13 checks, three boxes) in the
  scratch dir. Not built: "show only matching rows" (a filter, not a
  highlight), more than three terms, regular expressions. **Pinned rows (20 Nov
  2026):** click a row to pin it to the top (again to unpin; the `#` header
  unpins all); pinned rows stay first whatever the sort and keep the sort's
  order among themselves, in **reverse video** (text colour as background, page
  colour as text; highlights keep their colour with dark text) (`tr.pinned`). Client side
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
