# SQLQuickFind — Requirements & Design Spec

## Context

SSMS 22 ships with no fast way to jump to a stored procedure or function by name when you don't remember which database it lives in. Today the workflow is: expand Object Explorer → expand the correct server → expand each database → expand `Programmability\Stored Procedures` → scroll. This is slow when you work across many databases (a common reality in shops with hundreds of named DBs).

**SQLQuickFind** is an SSMS 22 VSIX extension that adds a search textbox to the SSMS toolbar. The user types an object name (or substring), presses Enter, and the matching stored procedure or function opens in a new query window as `CREATE OR ALTER` DDL — ready to inspect or edit. To make searches near-instant, the extension caches all object names per server.

This project builds on the platform conventions documented in [docs/ssms-22-extension-development-guide.md](docs/ssms-22-extension-development-guide.md), which was learned from building the prior SQLParity extension.

---

## Scope

### In scope

- A new SSMS 22 toolbar named **SQLQuickFind** with a search textbox, autocomplete dropdown, refresh button, and progress indicator
- Searching across **stored procedures** and **functions** (scalar `FN`, table-valued `TF`, inline table-valued `IF`)
- Per-server persistent object-name cache stored under `%LOCALAPPDATA%\SQLQuickFind\`
- Substring matching by default (case-insensitive)
- Active-database-scoped autocomplete dropdown while typing
- Server-wide search on Enter, with a database picker if matches span multiple DBs
- Recent-searches history (last 15, global across servers) shown on textbox focus
- Auto-detection of active query window's connection; popup picker if none is open
- "Open as `CREATE OR ALTER` in new query window" on find

### Out of scope (YAGNI)

- Searching tables, views, triggers, synonyms, indexes, constraints (procs + functions only for v1)
- Regex / wildcard / fuzzy matching (substring only)
- Searching object **bodies** (only names are cached)
- Editing or saving changes back to the database (read-only — opens a script the user can choose to execute)
- SSMS 21 / SSMS 20 support (target SSMS 22 only; manifest range `[22.0,23.0)`)
- Cross-server search in a single query (cache and search are always scoped to one server)
- Permission/security trimming (the cache reflects whatever the connection's account can see; no extra filtering)

---

## User flows

### Flow 1 — search with a query window open

1. User has a query window open connected to `Server1\Sales`.
2. User clicks the SQLQuickFind toolbar textbox.
3. Dropdown shows the last 15 searches (global history).
4. User types `usp_Order`.
5. As they type, the dropdown filters to show matches **in the `Sales` database only**, formatted `[Sales].[dbo].[usp_OrderInsert]`.
6. User presses **Enter**.
7. Extension searches the **server-wide** cache for `usp_Order`.
   - If a single match → open it as `CREATE OR ALTER` in a new query window.
   - If multiple matches across DBs → show DB-picker dialog (see Flow 4).
   - If no match → run cache refresh once silently, retry; if still nothing → `MessageBox.Show("No objects found")`.

### Flow 2 — search with no query window open

1. User has Object Explorer connected to `Server1` and `Server2`, but no query window is open.
2. User clicks the textbox, types a name, presses Enter.
3. Extension shows a small modal: **"Select database connection"** listing `Server1` and `Server2` (the connected OE servers).
4. User picks `Server2`, presses OK.
5. Search proceeds as in Flow 1, but against `Server2`'s cache.

### Flow 3 — pick from autocomplete

1. User types `usp_`.
2. Dropdown shows matches in active DB.
3. User clicks `[Sales].[dbo].[usp_OrderInsert]`.
4. Extension opens that specific object directly (no DB-picker needed since the user already chose).

### Flow 4 — multi-database match on Enter

1. User types `usp_GetCustomer` and presses Enter (not picking from dropdown).
2. Cache lookup finds matches in `Sales.dbo.usp_GetCustomer` and `Reporting.dbo.usp_GetCustomer`.
3. Extension shows a small modal: **"Multiple objects match. Pick a database:"** listing `Sales` and `Reporting`.
4. User picks `Sales`, presses OK → object opens as `CREATE OR ALTER` in new query window.

### Flow 5 — first connection to a server (cache build)

1. User connects to `Server3` in Object Explorer for the first time.
2. Extension detects the new connection, starts building the cache in the background.
3. A small spinner appears next to the SQLQuickFind textbox.
4. The user can immediately type a search — matches are returned against the **partial** cache as it fills.
5. When the cache finishes, the spinner disappears and a status-bar message says: `SQLQuickFind: cached N objects from Server3 across M databases`.

### Flow 6 — manual refresh

1. User has added a new stored procedure outside SSMS.
2. User clicks the **Refresh** button on the SQLQuickFind toolbar.
3. Extension rebuilds the cache for the **current server** (whichever the active query window or OE focus implies). Spinner shows during rebuild.
4. Old cache file is replaced atomically when complete.

---

## Functional requirements

### FR-1 — Toolbar UI

- A new toolbar named **SQLQuickFind**, registered in the `.vsct` file, available via View → Toolbars.
- Contents, left to right:
  - **Search textbox** (editable combobox), ~250 px wide, with `Find a stored proc or function…` watermark when empty.
  - **Refresh button** (icon button) — triggers cache rebuild for current server.
  - **Spinner / status indicator** — visible only while a cache build is in progress.
- The textbox supports:
  - Standard text input + clipboard ops
  - `Enter` → run search
  - `Esc` → clear textbox and close dropdown
  - Focus / click on empty box → show recent-searches dropdown
  - Typing → show autocomplete dropdown (active-DB scope)
  - `↑`/`↓` → navigate dropdown; `Enter` → pick highlighted
  - Click an item in dropdown → pick it

### FR-2 — Object scope (cached + searched)

For each database, cache rows from:

```sql
SELECT
    DB_NAME()                AS database_name,
    SCHEMA_NAME(o.schema_id) AS schema_name,
    o.name                   AS object_name,
    o.type                   AS object_type      -- 'P','PC','FN','TF','IF'
FROM sys.objects o
WHERE o.type IN ('P','PC','FN','TF','IF')
  AND o.is_ms_shipped = 0;
```

Enumerate databases via:

```sql
SELECT name
FROM sys.databases
WHERE state_desc = 'ONLINE'
  AND HAS_DBACCESS(name) = 1
  AND database_id > 4;   -- skip master/tempdb/model/msdb
```

System databases excluded. Offline/restoring DBs skipped. Inaccessible DBs (no `HAS_DBACCESS`) skipped without erroring.

### FR-3 — Matching

- **Case-insensitive substring** match against `object_name` only (schema and DB names are display-only; they don't participate in matching).
- Autocomplete and Enter-press both search across **all DBs** on the active server (`LIKE '%input%'`). Results are formatted `[db].[schema].[name]`.
- Result ranking: exact-name match first, then starts-with, then substring. Stable alphabetical within each tier.
- **History note:** revised from the original active-DB-only autocomplete scope after v1.4 testing showed it was too restrictive — when the active query window pointed at `master`, nothing ever matched.

### FR-4 — Active connection detection

The extension determines the "active server" / "active database" in this order:

1. If a query window has focus → use its `ServerName` and `DatabaseName` (read via `IVsRunningDocumentTable` + SSMS scripting interfaces).
2. Else, if exactly one Object Explorer connection exists → use it (database defaults to `master`, autocomplete falls back to server-wide).
3. Else → show "Select database connection" modal listing all connected OE servers; the user picks one. The chosen server becomes the active server for that single search.

### FR-5 — Open as CREATE OR ALTER

When an object is picked, the extension:

1. Opens a **new query window** connected to the matched server + database.
2. Prepends a `USE [<database>]` + `GO` header so accidental execution can't target the wrong database.
3. Inserts the object's DDL with the leading `CREATE PROCEDURE` / `CREATE FUNCTION` replaced by `CREATE OR ALTER PROCEDURE` / `CREATE OR ALTER FUNCTION`.
4. Does **not** execute the script.

The resulting query window content looks like:

```sql
USE [Sales]
GO

CREATE OR ALTER PROCEDURE [dbo].[usp_GetCustomer]
    @CustomerId int
AS
BEGIN
    ...
END
```

DDL source — use `OBJECT_DEFINITION(OBJECT_ID(@qualified_name))`. If `OBJECT_DEFINITION` returns NULL (encrypted object), fall back to `EXEC sp_helptext @qualified_name` and stitch the rows. If both fail (encrypted with no helptext), show a message: `Cannot retrieve definition — object may be encrypted.`

The `CREATE` → `CREATE OR ALTER` transformation must:
- Preserve leading comments / whitespace before the `CREATE` keyword (after the `USE`/`GO` header).
- Handle the (rare) case where the definition begins with `ALTER` already — leave it as-is since `ALTER` is acceptable.
- The database name in `USE [...]` is properly bracketed to handle names with spaces or reserved words.

### FR-6 — Cache persistence

- Location: `%LOCALAPPDATA%\SQLQuickFind\cache\<server-key>.json`
- `<server-key>` = sanitized server name (replace `\`, `:`, etc. with `_`); collisions resolved by appending a short hash of the raw name.
- File format (JSON):

```json
{
  "schemaVersion": 1,
  "serverName": "MYSERVER\\SQL2022",
  "builtAtUtc": "2026-05-20T14:32:11Z",
  "objects": [
    { "db": "Sales",     "schema": "dbo", "name": "usp_OrderInsert", "type": "P"  },
    { "db": "Reporting", "schema": "dbo", "name": "fn_DateOnly",     "type": "FN" }
  ]
}
```

- Writes are atomic: write to `*.json.tmp`, then `File.Replace`.
- Loaded into memory on first search for that server in the SSMS session.
- Refresh button → rebuild + overwrite.
- Auto-rebuild on cache miss: if a server-wide Enter-press search returns 0 results, silently rebuild the cache once, then retry the search before showing "No objects found".

### FR-7 — History

- Last **15** search strings kept (most recent first), global across all servers.
- Persisted at `%LOCALAPPDATA%\SQLQuickFind\history.json`.
- A search string is added to history when **Enter** is pressed and a result is opened (not on every keystroke, not on autocomplete pick — only when Enter actually opens something).
- Duplicate searches move to the top instead of creating a second entry.
- Shown when the user focuses or clicks an empty textbox.

### FR-8 — "Select database connection" prompt

- WPF modal dialog, non-resizable, ~400×200.
- Title: `Select Database Connection`.
- Listbox of connected Object Explorer servers (just the server names — not registered servers from the file menu).
- OK / Cancel buttons. Cancel aborts the search.

### FR-9 — "Multiple matches — pick database" prompt

- WPF modal dialog, non-resizable, ~400×250.
- Title: `Multiple matches found`.
- Body: `'<search>' was found in multiple databases. Pick one:`
- Listbox of database names that contain a match.
- OK / Cancel buttons. Cancel aborts.

### FR-10 — Not found

- After the auto-rebuild retry, if no matches: `MessageBox.Show("No objects found.", "SQLQuickFind", OK, Information)`.

### FR-11 — Cache build progress

- Spinner icon in toolbar visible only while a build is running.
- Searches against an in-progress cache return matches from whatever has been loaded so far (no error, no blocking).
- On completion: status bar message `SQLQuickFind: cached N objects from <server> across M databases`.
- On failure (e.g., login fails mid-build): status bar message `SQLQuickFind: cache build failed for <server> — <error>`. Previous cache file (if any) is preserved.

---

## Architecture

### Project layout

```
SQLQuickFind/
├── docs/
│   └── ssms-22-extension-development-guide.md      (existing)
├── src/
│   └── SQLQuickFind.Vsix/
│       ├── SQLQuickFind.Vsix.csproj                (old-style csproj, ToolsVersion 15)
│       ├── source.extension.vsixmanifest
│       ├── SQLQuickFindPackage.cs                  (AsyncPackage)
│       ├── SQLQuickFindPackage.vsct                (toolbar + commands)
│       ├── UI/
│       │   ├── SearchToolbarControl.xaml(.cs)      (textbox + dropdown + refresh + spinner)
│       │   ├── SelectConnectionDialog.xaml(.cs)
│       │   └── PickDatabaseDialog.xaml(.cs)
│       ├── Services/
│       │   ├── ConnectionContext.cs                (resolves active server/DB)
│       │   ├── ObjectCache.cs                      (load/save/query the per-server cache)
│       │   ├── CacheBuilder.cs                     (runs the SELECT against sys.objects)
│       │   ├── HistoryStore.cs                     (15-entry MRU list)
│       │   └── ObjectOpener.cs                     (fetch DDL, transform CREATE→CREATE OR ALTER, open in new query window)
│       └── Properties/
│           └── AssemblyInfo.cs
└── SQLQuickFind.sln
```

### Key SSMS integration points

| Need                                            | API / approach |
|------------------------------------------------ |----------------|
| Detect active query window connection           | `IVsMonitorSelection` + SSMS scripting (`Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache`) |
| Enumerate Object Explorer connections           | `ObjectExplorerService.Tree.Nodes` from `ServiceCache.ScriptFactory` / `ObjectExplorerService` |
| Open new query window with text and connection  | `ServiceCache.ScriptFactory.CreateNewBlankScript(...)` then write to its `IVsTextLines` |
| Run a SQL query (for cache build / OBJECT_DEFINITION) | `Microsoft.Data.SqlClient` (or `System.Data.SqlClient`) using the connection string built from the SSMS connection |
| Toolbar + textbox in `.vsct`                    | `<Combo idCommandId="..." type="DynamicCombo">` with `<CommandFlag>IconAndText</CommandFlag>` for the textbox; populated/handled by a `MenuCommand` of type `OleMenuCommand` |

> Note: the SSMS scripting API surface (`Microsoft.SqlServer.Management.UI.VSIntegration`) is undocumented but widely used in community SSMS extensions. The existing development guide implies SQLParity / SSMSLogger have already exercised these — patterns should be lifted directly from those projects to avoid re-discovery.

### Threading

- All UI code on the main thread (`JoinableTaskFactory.SwitchToMainThreadAsync`).
- Cache builds run on `Task.Run` with cancellation when SSMS closes or another build starts for the same server.
- SQL queries use async (`SqlCommand.ExecuteReaderAsync`).
- Cache reads from disk are sync but small (<1 MB typically) — fine to load on the main thread the first time a server is searched.

---

## Critical files to create

| File                                                          | Purpose |
|---------------------------------------------------------------|---------|
| `src/SQLQuickFind.Vsix/SQLQuickFind.Vsix.csproj`              | Old-style VSIX project; see development guide for required properties |
| `src/SQLQuickFind.Vsix/source.extension.vsixmanifest`         | Targets `Microsoft.VisualStudio.Ssms` `[22.0,23.0)` |
| `src/SQLQuickFind.Vsix/SQLQuickFindPackage.cs`                | `AsyncPackage` with `[ProvideAutoLoad(ShellInitialized, BackgroundLoad)]` |
| `src/SQLQuickFind.Vsix/SQLQuickFindPackage.vsct`              | Toolbar + textbox command + refresh command |
| `src/SQLQuickFind.Vsix/UI/SearchToolbarControl.xaml(.cs)`     | The hosted WPF combobox + popup |
| `src/SQLQuickFind.Vsix/Services/ConnectionContext.cs`         | Resolves "what server/DB am I on?" |
| `src/SQLQuickFind.Vsix/Services/ObjectCache.cs`               | Per-server cache load/save + in-memory dictionary |
| `src/SQLQuickFind.Vsix/Services/CacheBuilder.cs`              | Runs `sys.objects` enumeration; emits incremental progress |
| `src/SQLQuickFind.Vsix/Services/HistoryStore.cs`              | 15-entry MRU |
| `src/SQLQuickFind.Vsix/Services/ObjectOpener.cs`              | `OBJECT_DEFINITION` fetch, `CREATE` → `CREATE OR ALTER` transform, open query window |

The SQLParity VSIX referenced in the existing development guide should be inspected first to copy proven patterns for: SSMS toolbar registration, active-connection detection, and opening a new query window with text. Re-deriving these from MSDN docs alone is expensive.

---

## Verification plan

End-to-end (manual, in SSMS 22 against a real SQL Server):

1. **Build + install** following the development guide. Confirm the SQLQuickFind toolbar appears under View → Toolbars after restarting SSMS.
2. **Cache build** — connect Object Explorer to a server with at least 3 databases and ≥20 procs/functions across them. Confirm the spinner appears and the status bar shows `cached N objects across M databases`. Open `%LOCALAPPDATA%\SQLQuickFind\cache\` and verify the JSON file looks right.
3. **Active-DB autocomplete** — open a query window connected to one of those DBs, type a substring, confirm dropdown shows only matches in that DB formatted `[db].[schema].[name]`.
4. **Enter-press single match** — type a unique proc name, press Enter. Confirm a new query window opens with `CREATE OR ALTER PROCEDURE ...` text and connected to the right DB.
5. **Enter-press multi-DB match** — pre-create the same-named proc in two DBs, press Enter, confirm the DB-picker modal appears, pick one, confirm the right DB's version opens.
6. **No query window flow** — close all query windows, press Enter, confirm the "Select database connection" prompt lists OE-connected servers.
7. **History** — perform 16 distinct searches that open results; confirm only the most recent 15 appear; click empty textbox and confirm dropdown shows them in MRU order.
8. **Cache miss auto-rebuild** — add a new proc directly via SQL (bypassing SSMS), search for it, confirm the silent rebuild fires and the proc opens (no "No objects found" message).
9. **Not found** — search for a string that doesn't match anything; confirm `MessageBox.Show("No objects found.")`.
10. **Refresh button** — rename a proc out-of-band, click Refresh, confirm the new name appears in autocomplete and the old one is gone.
11. **Persistence** — close SSMS, reopen, search again, confirm autocomplete responds instantly (no rebuild on launch).
12. **Encrypted object fallback** — create a procedure `WITH ENCRYPTION`, search for it, confirm the "Cannot retrieve definition" message.

---

## Open questions / future enhancements (not for v1)

- Keyboard shortcut to focus the textbox from anywhere (e.g. `Ctrl+;`).
- Object-body search (full-text grep across cached DDL).
- Show object type icon (proc vs function) in the dropdown.
- "Find references" — list which other objects reference the selected one.
- Background auto-refresh when SSMS detects a DDL change via DDL trigger / Service Broker (out of scope; would need server-side install).
- Support for tables / views / triggers / synonyms.
