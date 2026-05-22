using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;

namespace SQLQuickFind.Services
{
    /// <summary>
    /// At runtime, walks the SSMS WPF visual tree to (a) attach a Popup-based autocomplete
    /// to the SQLQuickFind toolbar combo's underlying TextBox and (b) set a version tooltip
    /// on the "Find proc/function:" label. The VS shell does not expose per-keystroke
    /// events for toolbar DynamicCombos, so this visual-tree hook is necessary.
    /// </summary>
    internal static class ToolbarAutocomplete
    {
        private const string LabelText      = "Find proc/function:";
        private const string Marker         = "SQLQuickFind";
        private const int    MaxAttempts    = 60;
        private const int    RetryIntervalMs = 500;
        private const int    MaxItems       = 25;

        // Autocomplete state
        private static TextBox  _textBox;
        private static Popup    _popup;
        private static ListBox  _list;
        private static ComboBox _ancestorCombo;
        private static bool     _autocompleteAttached;
        private static bool     _suppressTextChanged;

        // Tooltip state
        private static bool _tooltipAttached;

        // Retry state
        private static int  _attempts;
        private static bool _diagnosticsWritten;

        public static void Schedule()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            LogPublic($"Schedule() called. Application.Current is {(Application.Current == null ? "NULL" : "set")}. Dispatcher is {(Application.Current?.Dispatcher == null ? "NULL" : "set")}.");
            if (_autocompleteAttached && _tooltipAttached) return;
            _attempts = 0;
            ScheduleAttempt(0);
        }

        // Public log entry point so the package's InitializeAsync can write a startup marker.
        public static void LogPublic(string message) => Log(message);

        private static void ScheduleAttempt(int delayMs)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                Log($"ScheduleAttempt: dispatcher is NULL after attempt {_attempts}; giving up.");
                return;
            }
            if (delayMs <= 0)
            {
                dispatcher.BeginInvoke((Action)TryAttach, DispatcherPriority.ApplicationIdle);
            }
            else
            {
                var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(delayMs)
                };
                timer.Tick += (s, e) => { timer.Stop(); TryAttach(); };
                timer.Start();
            }
        }

        private static void TryAttach()
        {
            try
            {
                _attempts++;
                var window = Application.Current?.MainWindow;
                if (window == null)
                {
                    if (_attempts < MaxAttempts) ScheduleAttempt(RetryIntervalMs);
                    else WriteDiagnostics(window);
                    return;
                }

                if (!_autocompleteAttached)
                {
                    var tb = FindToolbarTextBox(window);
                    if (tb != null)
                    {
                        _textBox = tb;
                        AttachHandlers();
                        CreatePopup();
                        _autocompleteAttached = true;
                        Log($"Autocomplete attached on attempt {_attempts}.");
                    }
                }

                if (!_tooltipAttached)
                {
                    var label = FindLabel(window, LabelText);
                    if (label != null)
                    {
                        ToolTipService.SetToolTip(label, BuildVersionTooltip());
                        _tooltipAttached = true;
                        Log($"Version tooltip attached on attempt {_attempts}.");
                    }
                }

                if (_autocompleteAttached && _tooltipAttached) return;

                if (_attempts < MaxAttempts)
                {
                    ScheduleAttempt(RetryIntervalMs);
                }
                else if (!_diagnosticsWritten)
                {
                    WriteDiagnostics(window);
                    _diagnosticsWritten = true;
                }
            }
            catch (Exception ex)
            {
                Log($"TryAttach #{_attempts} threw: {ex.Message}");
                if (_attempts < MaxAttempts) ScheduleAttempt(RetryIntervalMs);
            }
        }

        // ---- Discovery helpers ----

        private static TextBox FindToolbarTextBox(DependencyObject root)
        {
            // Strategy 1: TextBox whose ancestor chain contains an identifier mentioning "SQLQuickFind".
            foreach (var tb in WalkTree(root).OfType<TextBox>())
            {
                if (AncestorChainMentions(tb, Marker)) return tb;
            }
            // Strategy 2: Find label by text, then look for a TextBox sibling inside the same ToolBar.
            var label = FindLabel(root, LabelText);
            if (label != null)
            {
                var toolbar = FindAncestor(label, t => IsToolBar(t));
                if (toolbar != null)
                {
                    foreach (var tb in WalkTree(toolbar).OfType<TextBox>())
                        return tb;
                }
            }
            return null;
        }

        private static FrameworkElement FindLabel(DependencyObject root, string text)
        {
            // Strip the trailing colon and compare case-insensitively as a substring,
            // so we tolerate accelerator markup (e.g. "Find _proc/function:") and
            // minor whitespace/punctuation differences from the vsct ButtonText.
            string needle = text.TrimEnd(':').Trim();
            foreach (var node in WalkTree(root))
            {
                string content = null;
                if (node is TextBlock tb) content = tb.Text;
                else if (node is AccessText at) content = at.Text;
                else if (node is Label lbl) content = lbl.Content as string;
                else if (node is ContentControl cc) content = cc.Content as string;

                if (!string.IsNullOrEmpty(content) &&
                    content.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    node is FrameworkElement fe)
                {
                    Log($"FindLabel: matched on {node.GetType().Name} with content '{content}'");
                    return fe;
                }
            }
            return null;
        }

        private static bool AncestorChainMentions(DependencyObject node, string marker)
        {
            for (var p = node; p != null; p = VisualTreeHelper.GetParent(p))
            {
                if (p is FrameworkElement fe)
                {
                    if (Contains(AutomationProperties.GetAutomationId(fe), marker)) return true;
                    if (Contains(AutomationProperties.GetName(fe), marker))         return true;
                    if (Contains(fe.Name, marker))                                  return true;
                    if (fe.Tag is string t && Contains(t, marker))                  return true;
                }
            }
            return false;
        }

        private static DependencyObject FindAncestor(DependencyObject start, Func<DependencyObject, bool> match)
        {
            for (var p = start; p != null; p = VisualTreeHelper.GetParent(p))
                if (match(p)) return p;
            return null;
        }

        private static bool IsToolBar(DependencyObject d)
        {
            var t = d.GetType();
            while (t != null)
            {
                if (t.Name == "ToolBar" || t.Name == "VsToolBar" || t.Name == "ToolBarTray")
                    return true;
                t = t.BaseType;
            }
            return false;
        }

        private static IEnumerable<DependencyObject> WalkTree(DependencyObject root)
        {
            if (root == null) yield break;
            yield return root;
            int count;
            try { count = VisualTreeHelper.GetChildrenCount(root); }
            catch { yield break; }
            for (int i = 0; i < count; i++)
            {
                DependencyObject child;
                try { child = VisualTreeHelper.GetChild(root, i); }
                catch { continue; }
                foreach (var c in WalkTree(child)) yield return c;
            }
        }

        private static bool Contains(string s, string needle) =>
            !string.IsNullOrEmpty(s) && s.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        // ---- Autocomplete UI ----

        private static void AttachHandlers()
        {
            _textBox.TextChanged       += OnTextChanged;
            _textBox.PreviewKeyDown    += OnTextBoxKeyDown;
            _textBox.LostKeyboardFocus += OnTextBoxLostFocus;
            _textBox.GotKeyboardFocus  += OnTextBoxGotFocus;

            // Suppress the native combo dropdown so it doesn't compete with our Popup.
            _ancestorCombo = FindAncestor(_textBox, p => p is ComboBox) as ComboBox;
            Log($"AttachHandlers: ancestorCombo={_ancestorCombo?.GetType().Name ?? "NULL"}");
            if (_ancestorCombo != null)
            {
                _ancestorCombo.DropDownOpened += (s, e) =>
                {
                    try { _ancestorCombo.IsDropDownOpen = false; } catch { }
                };
            }
        }

        private static void CreatePopup()
        {
            _list = new ListBox
            {
                DisplayMemberPath = "Display",
                MaxHeight = 300,
                MinWidth  = 360,
                FontSize  = 12
            };
            // Attach the click handler directly on every ListBoxItem via container style.
            // This is more reliable inside a WPF Popup than wiring at the ListBox level,
            // because the Popup's own HWND can mis-route some bubbling events.
            var itemStyle = new Style(typeof(ListBoxItem));
            itemStyle.Setters.Add(new EventSetter
            {
                Event   = ListBoxItem.MouseLeftButtonUpEvent,
                Handler = new MouseButtonEventHandler(OnItemMouseUp)
            });
            itemStyle.Setters.Add(new EventSetter
            {
                Event   = ListBoxItem.PreviewMouseLeftButtonDownEvent,
                Handler = new MouseButtonEventHandler(OnItemPreviewDown)
            });
            _list.ItemContainerStyle = itemStyle;

            // Backup: also wire at ListBox level (in case style hooks miss for any reason).
            _list.MouseLeftButtonUp        += OnListClick;
            _list.PreviewMouseLeftButtonUp += OnListClick;
            _list.MouseDoubleClick         += OnListDoubleClick;
            _list.PreviewKeyDown           += OnListKeyDown;

            var border = new Border
            {
                BorderBrush     = SystemColors.ActiveBorderBrush,
                BorderThickness = new Thickness(1),
                Background      = SystemColors.WindowBrush,
                Child           = _list
            };

            _popup = new Popup
            {
                PlacementTarget    = _textBox,
                Placement          = PlacementMode.Bottom,
                // Manage open/close ourselves via focus tracking. StaysOpen=false interprets
                // any mouse-up outside the popup as a click-to-dismiss, which kills the popup
                // on the same click that opened it.
                StaysOpen          = true,
                AllowsTransparency = true,
                Child              = border
            };
        }

        private static void OnTextBoxGotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // When the textbox is focused with empty text, show history in our popup.
            Log($"OnTextBoxGotFocus: text='{_textBox.Text ?? ""}'");
            if (string.IsNullOrEmpty(_textBox.Text)) ShowHistory();
        }

        private static void OnItemPreviewDown(object sender, MouseButtonEventArgs e)
        {
            // Commit on mouse-down rather than mouse-up. The popup's textbox loses focus on
            // mouse-down, which fires our deferred close handler before MouseUp arrives, so
            // OnItemMouseUp never sees the click. Acting on PreviewDown sidesteps the race.
            Log($"OnItemPreviewDown: sender={sender?.GetType().Name}, DataContext={(sender as ListBoxItem)?.DataContext?.GetType().Name}");
            if (sender is ListBoxItem lbi && lbi.DataContext is DropdownItem item)
            {
                e.Handled = true;
                CommitItem(item);
            }
        }

        private static void OnItemMouseUp(object sender, MouseButtonEventArgs e)
        {
            // Backup path (rare). PreviewDown already handles the normal case.
            Log($"OnItemMouseUp: sender={sender?.GetType().Name}, DataContext={(sender as ListBoxItem)?.DataContext?.GetType().Name}");
            if (sender is ListBoxItem lbi && lbi.DataContext is DropdownItem item)
            {
                e.Handled = true;
                CommitItem(item);
            }
        }

        private static void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressTextChanged) return;
            try
            {
                string text = _textBox.Text ?? "";
                if (string.IsNullOrWhiteSpace(text)) { ShowHistory(); return; }

                var pkg = SQLQuickFindPackage.Instance;
                var active = ConnectionContext.GetActiveConnection();
                if (pkg == null || active == null) { _popup.IsOpen = false; return; }

                var cache = pkg.GetOrTriggerCacheBuild(active);
                if (cache == null) { _popup.IsOpen = false; return; }

                // Search across all DBs on the server. Results show as [db].[schema].[name].
                var matches = cache.SearchAll(text).Take(MaxItems)
                                   .Select(m => new DropdownItem { Entry = m, Display = m.QualifiedDisplay })
                                   .ToList();
                _list.ItemsSource = matches;
                if (matches.Count > 0)
                {
                    _list.SelectedIndex = 0;
                    if (!_popup.IsOpen) _popup.IsOpen = true;
                }
                else
                {
                    _popup.IsOpen = false;
                }
            }
            catch { /* don't crash the shell */ }
        }

        private static void ShowHistory()
        {
            var pkg = SQLQuickFindPackage.Instance;
            var history = pkg?.History?.GetAll();
            Log($"ShowHistory: history count = {history?.Count ?? -1}");
            if (history == null || history.Count == 0)
            {
                _popup.IsOpen = false;
                return;
            }
            var items = history.Select(h => new DropdownItem
            {
                HistoryText = h,
                Display     = h
            }).ToList();
            _list.ItemsSource   = items;
            _list.SelectedIndex = 0;
            if (!_popup.IsOpen) _popup.IsOpen = true;
            Log($"ShowHistory: popup IsOpen={_popup.IsOpen}");
        }

        private static void OnTextBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && _popup.IsOpen && _list.Items.Count > 0)
            {
                _list.Focus();
                if (_list.SelectedIndex < 0) _list.SelectedIndex = 0;
                (_list.ItemContainerGenerator.ContainerFromIndex(_list.SelectedIndex) as ListBoxItem)?.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (_popup.IsOpen) { _popup.IsOpen = false; e.Handled = true; }
            }
            else if (e.Key == Key.Enter && _popup.IsOpen && _list.SelectedItem is DropdownItem item)
            {
                e.Handled = true;
                CommitItem(item);
            }
        }

        private static void OnTextBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // Defer so the new focus state is settled before we decide whether to close.
            // If focus stayed in the textbox OR moved into our popup's UI, keep the popup open.
            Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
            {
                try
                {
                    if (_popup == null) return;
                    if (_popup.IsKeyboardFocusWithin) return;
                    if (_textBox != null && _textBox.IsKeyboardFocusWithin) return;
                    _popup.IsOpen = false;
                }
                catch { }
            }), DispatcherPriority.Input);
        }

        private static void OnListClick(object sender, MouseButtonEventArgs e)
        {
            // Find the ListBoxItem we actually clicked on (rather than trusting SelectedItem timing).
            var lbi = FindAncestorListBoxItem(e.OriginalSource as DependencyObject);
            Log($"OnListClick: OriginalSource={e.OriginalSource?.GetType().Name ?? "null"}, found ListBoxItem={lbi != null}, DataContext={lbi?.DataContext?.GetType().Name ?? "null"}");
            if (lbi?.DataContext is DropdownItem item)
            {
                e.Handled = true;
                CommitItem(item);
            }
        }

        private static void OnListDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var lbi = FindAncestorListBoxItem(e.OriginalSource as DependencyObject);
            if (lbi?.DataContext is DropdownItem item)
            {
                e.Handled = true;
                CommitItem(item);
            }
        }

        private static ListBoxItem FindAncestorListBoxItem(DependencyObject node)
        {
            while (node != null && !(node is ListBoxItem))
                node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);
            return node as ListBoxItem;
        }

        private static void OnListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && _list.SelectedItem is DropdownItem item)
            {
                e.Handled = true;
                CommitItem(item);
            }
            else if (e.Key == Key.Escape)
            {
                _popup.IsOpen = false;
                _textBox.Focus();
                e.Handled = true;
            }
        }

        private static void CommitItem(DropdownItem item)
        {
            if (item == null) return;
            if (item.Entry != null) CommitEntry(item.Entry);
            else if (item.HistoryText != null) CommitHistory(item.HistoryText);
        }

        private static void CommitEntry(ObjectEntry entry)
        {
            try
            {
                _popup.IsOpen = false;
                var pkg = SQLQuickFindPackage.Instance;
                var active = ConnectionContext.GetActiveConnection();
                if (pkg == null || active == null) return;
                pkg.History?.Add(_textBox.Text);
                _suppressTextChanged = true;
                try { _textBox.Text = ""; } finally { _suppressTextChanged = false; }
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                {
                    try { await ObjectOpener.OpenAsync(active, entry); }
                    catch { /* opener shows its own error UI */ }
                });
            }
            catch { /* swallow */ }
        }

        private static void CommitHistory(string text)
        {
            try
            {
                _popup.IsOpen = false;
                var pkg = SQLQuickFindPackage.Instance;
                if (pkg == null) return;
                _suppressTextChanged = true;
                try { _textBox.Text = ""; } finally { _suppressTextChanged = false; }
                pkg.RunEnterSearch(text);
            }
            catch { /* swallow */ }
        }

        // Items in the popup are uniform DropdownItems whose Display is what the user sees.
        // Exactly one of Entry / HistoryText is non-null.
        private sealed class DropdownItem
        {
            public string Display { get; set; }
            public ObjectEntry Entry { get; set; }
            public string HistoryText { get; set; }
        }

        // ---- Version tooltip ----

        private static string BuildVersionTooltip()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var ver = asm.GetName().Version?.ToString() ?? "(unknown)";
                return $"SQLQuickFind v{ver}\nType part of a stored procedure or function name. Press Enter to search.";
            }
            catch
            {
                return "SQLQuickFind";
            }
        }

        // ---- Diagnostics ----

        private static string LogPath
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SQLQuickFind");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "toolbar-attach.log");
            }
        }

        private static void Log(string message)
        {
            try { File.AppendAllText(LogPath, $"[{DateTime.Now:o}] {message}\r\n"); }
            catch { }
        }

        private static void WriteDiagnostics(DependencyObject window)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("=== DIAGNOSTIC DUMP ===");
                sb.AppendLine($"Time: {DateTime.Now:o}");
                sb.AppendLine($"Autocomplete attached: {_autocompleteAttached}");
                sb.AppendLine($"Tooltip attached:      {_tooltipAttached}");
                sb.AppendLine($"MainWindow:            {(window == null ? "(null)" : window.GetType().FullName)}");

                if (window != null)
                {
                    int textBoxCount = 0;
                    foreach (var node in WalkTree(window).OfType<TextBox>())
                    {
                        textBoxCount++;
                        if (textBoxCount > 25) { sb.AppendLine($"...stopping after 25 textboxes"); break; }
                        sb.AppendLine($"TextBox #{textBoxCount}: {DescribeAncestorChain(node, 12)}");
                    }
                    sb.AppendLine($"Total TextBox count (capped at 25): {textBoxCount}");

                    sb.AppendLine();
                    sb.AppendLine("--- TextBlocks containing 'SQLQuickFind' ---");
                    int hits = 0;
                    foreach (var node in WalkTree(window).OfType<TextBlock>())
                    {
                        if (node.Text != null && node.Text.IndexOf(Marker, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            hits++;
                            sb.AppendLine($"  Text='{node.Text}' AutomationId='{AutomationProperties.GetAutomationId(node)}' Name='{node.Name}'");
                        }
                    }
                    sb.AppendLine($"Total: {hits}");

                    sb.AppendLine();
                    sb.AppendLine($"--- TextBlocks with Text == '{LabelText}' ---");
                    int labelHits = 0;
                    foreach (var node in WalkTree(window).OfType<TextBlock>())
                    {
                        if (node.Text == LabelText)
                        {
                            labelHits++;
                            sb.AppendLine($"  AutomationId='{AutomationProperties.GetAutomationId(node)}' Name='{node.Name}'");
                            sb.AppendLine($"  Ancestor chain: {DescribeAncestorChain(node, 10)}");
                        }
                    }
                    sb.AppendLine($"Total: {labelHits}");
                }

                File.AppendAllText(LogPath, sb.ToString());
            }
            catch (Exception ex)
            {
                Log($"WriteDiagnostics threw: {ex.Message}");
            }
        }

        private static string DescribeAncestorChain(DependencyObject node, int maxDepth)
        {
            var sb = new StringBuilder();
            var p = node;
            for (int i = 0; p != null && i < maxDepth; i++)
            {
                sb.Append($"\n    {i}: {p.GetType().Name}");
                if (p is FrameworkElement fe)
                {
                    var id   = AutomationProperties.GetAutomationId(fe);
                    var aname = AutomationProperties.GetName(fe);
                    if (!string.IsNullOrEmpty(fe.Name))  sb.Append($" Name='{fe.Name}'");
                    if (!string.IsNullOrEmpty(id))       sb.Append($" AutomationId='{id}'");
                    if (!string.IsNullOrEmpty(aname))    sb.Append($" AutoName='{aname}'");
                    if (fe.Tag is string tag)            sb.Append($" Tag='{tag}'");
                }
                p = VisualTreeHelper.GetParent(p);
            }
            return sb.ToString();
        }
    }
}
