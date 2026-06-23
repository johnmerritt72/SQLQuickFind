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
using Microsoft.VisualStudio.PlatformUI;
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
        // True from the moment a result is committed until the user genuinely interacts
        // again (a keystroke, or focus leaving and the combo settling). The native VS
        // DynamicCombo emits a *deferred* empty-text TextChanged echoing our programmatic
        // clear; that echo escapes the synchronous _suppressTextChanged window and would
        // otherwise re-open the popup as orphaned history. This flag covers that window.
        private static bool     _committing;

        // Themed brushes for the dropdown. Kept as mutable (non-frozen) instances so the
        // Border/ListBox/item-template that reference them update live when we recolour them
        // on a theme change — see RebuildThemeBrushes / OnThemeChanged.
        private static readonly SolidColorBrush _bgBrush      = new SolidColorBrush();
        private static readonly SolidColorBrush _textBrush    = new SolidColorBrush();
        private static readonly SolidColorBrush _borderBrush  = new SolidColorBrush();
        private static readonly SolidColorBrush _selBgBrush   = new SolidColorBrush();
        private static readonly SolidColorBrush _selTextBrush = new SolidColorBrush();
        private static bool _themeHooked;

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
            // Pull the current SSMS theme colours and keep them refreshed on theme switches.
            RebuildThemeBrushes();
            if (!_themeHooked)
            {
                VSColorTheme.ThemeChanged += OnThemeChanged;
                _themeHooked = true;
            }

            _list = new ListBox
            {
                DisplayMemberPath = "Display",
                MaxHeight = 300,
                MinWidth  = 360,
                FontSize  = 12,
                // Transparent so the rounded, themed Border behind it shows through —
                // a square ListBox background would otherwise poke past the rounded corners.
                Background      = Brushes.Transparent,
                Foreground      = _textBrush,
                BorderThickness = new Thickness(0)
            };
            // Attach the click handler directly on every ListBoxItem via container style.
            // This is more reliable inside a WPF Popup than wiring at the ListBox level,
            // because the Popup's own HWND can mis-route some bubbling events.
            var itemStyle = new Style(typeof(ListBoxItem));
            itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, _textBrush));
            itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            itemStyle.Setters.Add(new Setter(Control.TemplateProperty, BuildItemTemplate()));
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
                BorderBrush     = _borderBrush,
                BorderThickness = new Thickness(1),
                Background      = _bgBrush,
                CornerRadius    = new CornerRadius(5),
                Padding         = new Thickness(2),
                SnapsToDevicePixels = true,
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

        // ---- Theming ----

        // Recolours the shared brushes from the active VS/SSMS theme. Because the brushes are
        // mutable instances referenced by the live visual tree, changing their .Color here
        // updates the dropdown in place — no rebuild of the popup needed.
        private static void RebuildThemeBrushes()
        {
            _bgBrush.Color      = ThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey,        Color.FromRgb(0x25, 0x25, 0x26));
            _textBrush.Color    = ThemedColor(EnvironmentColors.ToolWindowTextColorKey,              Color.FromRgb(0xF1, 0xF1, 0xF1));
            _borderBrush.Color  = ThemedColor(EnvironmentColors.ToolWindowBorderColorKey,            Color.FromRgb(0x3F, 0x3F, 0x46));
            _selBgBrush.Color   = ThemedColor(EnvironmentColors.CommandBarMenuItemMouseOverColorKey, Color.FromRgb(0x3E, 0x3E, 0x40));
            _selTextBrush.Color = ThemedColor(EnvironmentColors.CommandBarMenuItemMouseOverTextColorKey, Color.FromRgb(0xFF, 0xFF, 0xFF));
        }

        private static Color ThemedColor(ThemeResourceKey key, Color fallback)
        {
            try
            {
                var c = VSColorTheme.GetThemedColor(key);
                return Color.FromArgb(c.A, c.R, c.G, c.B);
            }
            catch { return fallback; }
        }

        private static void OnThemeChanged(ThemeChangedEventArgs e)
        {
            try { RebuildThemeBrushes(); } catch { }
        }

        // ListBoxItem template: a rounded Border that fills with the themed highlight brush on
        // selection or hover. The existing click EventSetters live on the container Style, not
        // here, so the commit-on-click behaviour is unaffected.
        private static ControlTemplate BuildItemTemplate()
        {
            var bd = new FrameworkElementFactory(typeof(Border), "Bd");
            bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bd.SetValue(Border.PaddingProperty, new Thickness(6, 2, 6, 2));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            bd.SetValue(Border.SnapsToDevicePixelsProperty, true);

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);

            var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = bd };

            var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Border.BackgroundProperty, _selBgBrush, "Bd"));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, _selTextBrush));
            template.Triggers.Add(selected);

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, _selBgBrush, "Bd"));
            hover.Setters.Add(new Setter(Control.ForegroundProperty, _selTextBrush));
            template.Triggers.Add(hover);

            return template;
        }

        // The popup may only be open while the user is actually working in the toolbar box
        // (focus is in the textbox or in the popup itself) and we are not mid-commit. This is
        // the safety net that stops the popup from ever lingering as an orphan after a proc
        // opens and keyboard focus has moved to the editor.
        private static bool CanShowPopup()
        {
            if (_committing) return false;
            if (_textBox == null || _popup == null) return false;
            return _textBox.IsKeyboardFocusWithin || _popup.IsKeyboardFocusWithin;
        }

        private static void OpenPopup()
        {
            if (CanShowPopup())
            {
                if (!_popup.IsOpen) _popup.IsOpen = true;
            }
            else
            {
                _popup.IsOpen = false;
            }
        }

        private static void OnTextBoxGotFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // When the textbox is focused with empty text, show history in our popup.
            Log($"OnTextBoxGotFocus: text='{_textBox.Text ?? ""}'");
            if (_committing) return;
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
            // Ignore the native combo's deferred echo of our programmatic clear (and any
            // other text churn it generates) until the user genuinely interacts again.
            if (_committing) { _popup.IsOpen = false; return; }
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
                    OpenPopup();
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
            if (_committing) { _popup.IsOpen = false; return; }
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
            OpenPopup();
            Log($"ShowHistory: popup IsOpen={_popup.IsOpen}");
        }

        private static void OnTextBoxKeyDown(object sender, KeyEventArgs e)
        {
            // A keystroke in the box means the user is actively driving it again, so the
            // post-commit suppression window is over. This fires before the resulting
            // TextChanged, so normal search/history behaviour resumes on this very key.
            _committing = false;
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
                    // Focus has genuinely left the toolbar. Re-enable history-on-refocus, but
                    // only after any pending combo-driven TextChanged bursts (Input priority)
                    // have drained, so they don't sneak the popup back open first.
                    Application.Current?.Dispatcher.BeginInvoke(
                        (Action)(() => _committing = false), DispatcherPriority.Background);
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
                _committing = true;
                _popup.IsOpen = false;
                var pkg = SQLQuickFindPackage.Instance;
                var active = ConnectionContext.GetActiveConnection();
                if (pkg == null || active == null) return;
                pkg.History?.Add(_textBox.Text);
                _suppressTextChanged = true;
                try { _textBox.Text = ""; } finally { _suppressTextChanged = false; }
                // Keep the shell-side combo value in sync, or the shell repaints the box
                // with a stale prior search after focus settles.
                pkg.NotifyComboTextCleared();
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
                _committing = true;
                _popup.IsOpen = false;
                var pkg = SQLQuickFindPackage.Instance;
                if (pkg == null) return;
                _suppressTextChanged = true;
                try { _textBox.Text = ""; } finally { _suppressTextChanged = false; }
                // Keep the shell-side combo value in sync (see NotifyComboTextCleared).
                pkg.NotifyComboTextCleared();
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
