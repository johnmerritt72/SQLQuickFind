# SQLQuickFind

A SQL Server Management Studio (SSMS) 22 extension that lets you jump to any stored procedure or function across all the databases on a server, without expanding the Object Explorer tree.

Type part of an object name in the toolbar, press Enter, and the matching object opens as a `CREATE OR ALTER` script in a new query window — ready to inspect or edit.

## Why

The default SSMS workflow for "find that proc named `usp_GetSomething`" is: expand the right server → expand the right database (which you may have forgotten) → expand `Programmability\Stored Procedures` → scroll. In a shop with hundreds of objects/stored procedures this is slow.

SQLQuickFind caches every user proc and function name on a server (one-time, persisted to disk) and gives you instant substring search across them all.

## Requirements

- **SQL Server Management Studio 22** (Windows, x64). The manifest targets SSMS 22 only — SSMS 21 and earlier are not supported.
- **.NET Framework 4.7.2** or later (ships with SSMS 22).

## Install

1. Download `SQLQuickFind.vsix` from the [Releases](../../releases) page.
2. Double-click it (or run `VSIXInstaller.exe SQLQuickFind.vsix` from a shell).
3. The installer dialog will offer to install to your SSMS 22 instance. Click **Install**, wait, and **Close**.
4. Launch SSMS 22. The **SQLQuickFind** toolbar appears automatically. If you don't see it, go to **View → Toolbars → SQLQuickFind**.

## Usage

The toolbar has three things: a search textbox labelled **Find proc/function:**, a **Refresh** button, and a tooltip on the label that shows the installed version.

### First search on a new server

Make a connection to any database. The first time you search, SQLQuickFind enumerates user databases and reads `sys.objects` from each — typically a few seconds for a hundred databases. The cache is persisted under `%LOCALAPPDATA%\SQLQuickFind\cache\` and reused across SSMS sessions.

### Searching

- **Click** in the textbox while it's empty → a dropdown of your 15 most recent searches appears. Click one to re-run that search.
- **Type** a substring of an object name → a dropdown of matches appears, formatted `[database].[schema].[name]`. Matches are case-insensitive substrings across all databases on the active server, ranked by exact > starts-with > substring.
- **Click an item** in the dropdown → it opens that object directly in a new query window.
- **Press Enter** → searches all databases on the server, applies the active query window's database when the result is unambiguous, and shows a database picker if multiple databases contain a matching object. If nothing matches, the cache is silently rebuilt once and the search retries before showing "No objects found".

### What opens

The result opens in a new query window connected to the matched server and database, with content like:

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

The `USE [database]` + `GO` header is **always** prepended, so accidentally hitting F5 won't deploy the proc to the wrong database. Encrypted procedures (`WITH ENCRYPTION`) fall back to `sp_helptext`, and if both fail you'll get a "Cannot retrieve definition" notice.

### Refreshing the cache

Click the **Refresh** button to rebuild the cache for the current server (after creating, renaming, or dropping procs/functions outside SSMS). The cache also auto-rebuilds silently on a cache-miss before showing "No objects found", so manual refresh is only needed when you know something changed.

## What's cached vs. what's not

**Cached:** Stored procedures (`P`, `PC`) and functions (`FN`, `TF`, `IF`) from every accessible user database on a server. System databases (`master`, `model`, `msdb`, `tempdb`) and any database without `HAS_DBACCESS` are skipped.

**Not cached:** Tables, views, triggers, synonyms, indexes, constraints. The object body is also not cached — only the name is searchable, and the DDL is fetched on demand via `OBJECT_DEFINITION` when you open something.

## Where things live

| File / folder | Purpose |
|---|---|
| `%LOCALAPPDATA%\SQLQuickFind\cache\<server>.json` | Per-server object name cache |
| `%LOCALAPPDATA%\SQLQuickFind\history.json` | Last 15 search terms (global across servers) |
| `%LOCALAPPDATA%\SQLQuickFind\toolbar-attach.log` | Diagnostic log for the toolbar autocomplete attachment (small, append-only) |
| `%LOCALAPPDATA%\SQLQuickFind\error.log` | Errors from the extension's background work (append-only) |

Deleting these files is safe — they'll be regenerated as needed.

## Known limitations

- Procs and functions only; no support yet for tables, views, triggers, synonyms.
- The autocomplete dropdown is anchored to the toolbar's underlying WPF textbox via runtime visual-tree walking. If a future SSMS update changes that tree shape, autocomplete might silently stop working — the toolbar-attach log will diagnose what was found.

## Building from source

```powershell
# From the repo root:
& "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" `
    SQLQuickFind.sln -t:Restore -p:Configuration=Release -v:minimal
& "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe" `
    SQLQuickFind.sln -t:Build -p:Configuration=Release -v:minimal
```

The output `.vsix` will be at `src\SQLQuickFind.Vsix\bin\Release\SQLQuickFind.vsix`.

You'll need Visual Studio 2022 with the **Visual Studio extension development** workload installed. Adjust the MSBuild path if your VS edition is Community or Enterprise instead of Professional.

See [docs/ssms-22-extension-development-guide.md](docs/ssms-22-extension-development-guide.md) for the hard-won details on building VSIX projects for SSMS 22 specifically. Most public guides target Visual Studio extensions and don't carry over cleanly.

## License

[MIT](LICENSE) — Copyright (c) 2026 John Merritt.
