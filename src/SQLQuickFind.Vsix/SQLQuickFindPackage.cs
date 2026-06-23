using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using SQLQuickFind.Services;
using SQLQuickFind.UI;
using Task = System.Threading.Tasks.Task;

namespace SQLQuickFind
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PkgCmdId.PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class SQLQuickFindPackage : AsyncPackage
    {
        private static readonly Guid CommandSet = new Guid(PkgCmdId.CommandSetGuidString);

        private HistoryStore _history;
        private readonly ConcurrentDictionary<string, ObjectCache> _cachesByServer
            = new ConcurrentDictionary<string, ObjectCache>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _buildingServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _buildLock = new object();
        private string _currentText = "";

        internal static DTE2 Dte { get; private set; }
        internal static SQLQuickFindPackage Instance { get; private set; }
        internal HistoryStore History => _history;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            try
            {
                ToolbarAutocomplete.LogPublic($"==== InitializeAsync start. Assembly={typeof(SQLQuickFindPackage).Assembly.GetName().Version} ====");
                Instance = this;
                Dte = (DTE2)await GetServiceAsync(typeof(DTE)).ConfigureAwait(true);
                _history = HistoryStore.CreateDefault();

                var commandService = await GetServiceAsync(typeof(IMenuCommandService)).ConfigureAwait(true) as OleMenuCommandService;
                if (commandService == null) return;

                var comboCmd = new OleMenuCommand(OnComboExecute, new CommandID(CommandSet, PkgCmdId.SearchComboCmdId));
                commandService.AddCommand(comboCmd);

                var comboFillCmd = new OleMenuCommand(OnComboFillList, new CommandID(CommandSet, PkgCmdId.SearchComboGetListId));
                commandService.AddCommand(comboFillCmd);

                var refreshCmd = new MenuCommand(OnRefreshExecute, new CommandID(CommandSet, PkgCmdId.RefreshCmdId));
                commandService.AddCommand(refreshCmd);

                // Attach the type-ahead autocomplete popup to the toolbar combo's TextBox.
                ToolbarAutocomplete.Schedule();
                ToolbarAutocomplete.LogPublic("InitializeAsync completed without error.");
            }
            catch (Exception ex)
            {
                ToolbarAutocomplete.LogPublic($"InitializeAsync THREW: {ex.GetType().Name}: {ex.Message}");
                LogError(ex, "InitializeAsync");
            }
        }

        // ---- Combo command handlers ----

        private void OnComboExecute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var args = e as OleMenuCmdEventArgs;
                if (args == null) return;

                // Case 1: VS is asking for our current text value to display.
                if (args.InValue == null && args.OutValue != IntPtr.Zero)
                {
                    Marshal.GetNativeVariantForObject(_currentText, args.OutValue);
                    return;
                }

                // Case 2: User typed or pressed Enter -- args.InValue holds the text.
                if (args.InValue != null)
                {
                    string newText = args.InValue.ToString() ?? "";
                    _currentText = newText;
                    if (!string.IsNullOrWhiteSpace(newText))
                    {
                        // Treat any committed text (Enter / selection) as a search trigger.
                        ExecuteSearch(newText);
                    }
                }
            }
            catch (Exception ex)
            {
                LogError(ex, "OnComboExecute");
            }
        }

        private void OnComboFillList(object sender, EventArgs e)
        {
            // The native DynamicCombo dropdown is intentionally empty -- all autocomplete
            // (cache matches + history) is rendered via the custom WPF Popup attached by
            // ToolbarAutocomplete. Two competing dropdowns made the UX confusing.
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var args = e as OleMenuCmdEventArgs;
                if (args == null || args.OutValue == IntPtr.Zero) return;
                Marshal.GetNativeVariantForObject(Array.Empty<string>(), args.OutValue);
            }
            catch (Exception ex)
            {
                LogError(ex, "OnComboFillList");
            }
        }

        // ---- Refresh button ----

        private void OnRefreshExecute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var active = ConnectionContext.GetActiveConnection()
                          ?? ConnectionContext.GetObjectExplorerConnections().FirstOrDefault();
                if (active == null)
                {
                    MessageBox.Show("Open a query window or connect to a server first.",
                        "SQLQuickFind", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                _cachesByServer.TryRemove(active.ServerName, out _);
                _ = StartCacheBuildAsync(active);
                SetStatus($"SQLQuickFind: rebuilding cache for {active.ServerName}...");
            }
            catch (Exception ex)
            {
                LogError(ex, "OnRefreshExecute");
            }
        }

        // Public-ish wrapper so ToolbarAutocomplete can re-trigger an Enter-press flow
        // (server-wide search + DB picker) when a history item is picked.
        internal void RunEnterSearch(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!string.IsNullOrWhiteSpace(text)) ExecuteSearch(text);
        }

        // The WPF autocomplete popup commits a search by clearing the toolbar textbox
        // directly, bypassing OnComboExecute. _currentText is the value the shell reads
        // back to repaint the DynamicCombo, so it must be cleared too — otherwise the
        // shell later resurrects a prior search into the box. See OnComboExecute Case 1.
        internal void NotifyComboTextCleared()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _currentText = "";
        }

        // ---- Search execution ----

        private void ExecuteSearch(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            ActiveConnection active = ConnectionContext.GetActiveConnection();
            if (active == null)
            {
                // No query window — fall back to OE picker (currently a v1 stub returning empty).
                var conns = ConnectionContext.GetObjectExplorerConnections();
                if (conns.Count == 0)
                {
                    MessageBox.Show(
                        "Connect to a server in Object Explorer or open a query window first.",
                        "SQLQuickFind", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (conns.Count == 1)
                {
                    active = conns[0];
                }
                else
                {
                    var dlg = new SelectConnectionDialog(conns);
                    new System.Windows.Interop.WindowInteropHelper(dlg)
                    { Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle };
                    var result = dlg.ShowDialog();
                    if (result != true || dlg.SelectedConnection == null) return;
                    active = dlg.SelectedConnection;
                }
            }

            // Find matches across the whole server cache (Enter-press scope is server-wide).
            var entry = ResolveMatch(text, active);
            if (entry == null) return; // Either no match (message already shown) or user cancelled.

            _history.Add(text);

            // Fire-and-forget the async DDL fetch + open.
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
            {
                try { await ObjectOpener.OpenAsync(active, entry); }
                catch (Exception ex) { LogError(ex, "ObjectOpener.OpenAsync"); }
            });
        }

        private ObjectEntry ResolveMatch(string text, ActiveConnection active)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Make sure we have a cache (kicks off build if first time)
            var cache = GetOrTriggerCacheBuild(active);
            var matches = cache?.SearchAll(text).ToList() ?? new List<ObjectEntry>();

            // If we have no matches and a build is not currently running, do a synchronous
            // refresh attempt before declaring "no objects found".
            if (matches.Count == 0 && !IsBuilding(active.ServerName))
            {
                // Force a rebuild and wait briefly. This blocks the UI thread, so cap the wait.
                ThreadHelper.JoinableTaskFactory.Run(async delegate
                {
                    await RebuildCacheBlockingAsync(active, TimeSpan.FromSeconds(30));
                });
                cache = _cachesByServer.TryGetValue(active.ServerName, out var c) ? c : null;
                matches = cache?.SearchAll(text).ToList() ?? new List<ObjectEntry>();
            }

            if (matches.Count == 0)
            {
                MessageBox.Show("No objects found.", "SQLQuickFind",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }
            if (matches.Count == 1) return matches[0];

            // Multiple matches. If they're all in the same DB, just take the first (top-ranked).
            var distinctDbs = matches.Select(m => m.Db).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (distinctDbs == 1) return matches[0];

            // Otherwise: prompt user to pick which DB-qualified match.
            var dlg = new PickDatabaseDialog(text, matches);
            var helper = new System.Windows.Interop.WindowInteropHelper(dlg)
            { Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle };
            var result = dlg.ShowDialog();
            return result == true ? dlg.SelectedEntry : null;
        }

        // ---- Cache management ----

        internal ObjectCache GetOrTriggerCacheBuild(ActiveConnection active)
        {
            if (_cachesByServer.TryGetValue(active.ServerName, out var cache))
                return cache;

            // Try loading from disk first.
            var loaded = ObjectCache.Load(active.ServerName);
            if (loaded != null)
            {
                _cachesByServer[active.ServerName] = loaded;
                return loaded;
            }

            // Nothing on disk; kick off a build in background.
            _ = StartCacheBuildAsync(active);
            return null;
        }

        private bool IsBuilding(string server)
        {
            lock (_buildLock) return _buildingServers.Contains(server);
        }

        private async Task StartCacheBuildAsync(ActiveConnection active)
        {
            lock (_buildLock)
            {
                if (!_buildingServers.Add(active.ServerName)) return;
            }
            try
            {
                var builder = new CacheBuilder(active.ServerName, active.ConnectionString);
                var result = await builder.BuildAsync(null, CancellationToken.None).ConfigureAwait(false);
                if (result.Error == null && result.Cache != null)
                {
                    result.Cache.Save();
                    _cachesByServer[active.ServerName] = result.Cache;
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    SetStatus($"SQLQuickFind: cached {result.Cache.Objects.Count} objects from {active.ServerName} across {result.DatabaseCount} databases");
                }
                else
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    SetStatus($"SQLQuickFind: cache build failed for {active.ServerName} -- {result.Error}");
                }
            }
            catch (Exception ex)
            {
                LogError(ex, "StartCacheBuildAsync");
            }
            finally
            {
                lock (_buildLock) _buildingServers.Remove(active.ServerName);
            }
        }

        private async Task RebuildCacheBlockingAsync(ActiveConnection active, TimeSpan timeout)
        {
            using (var cts = new CancellationTokenSource(timeout))
            {
                try
                {
                    var builder = new CacheBuilder(active.ServerName, active.ConnectionString);
                    var result = await builder.BuildAsync(null, cts.Token).ConfigureAwait(false);
                    if (result.Error == null && result.Cache != null)
                    {
                        result.Cache.Save();
                        _cachesByServer[active.ServerName] = result.Cache;
                    }
                }
                catch { /* swallow — the calling code checks the cache state afterwards */ }
            }
        }

        // ---- Helpers ----

        private void SetStatus(string text)
        {
            try { if (Dte != null) Dte.StatusBar.Text = text; } catch { }
        }

        private static void LogError(Exception ex, string context)
        {
            try
            {
                string errorLogDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SQLQuickFind");
                Directory.CreateDirectory(errorLogDir);
                string errorLogPath = Path.Combine(errorLogDir, "error.log");
                string message = $"[{DateTime.Now:o}] Context: {context}\r\n{ex}\r\n----------------------\r\n";
                File.AppendAllText(errorLogPath, message);
            }
            catch { /* swallow */ }
        }
    }
}
