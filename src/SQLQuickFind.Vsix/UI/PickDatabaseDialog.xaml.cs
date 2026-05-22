using System.Collections.Generic;
using System.Linq;
using System.Windows;
using SQLQuickFind.Services;

namespace SQLQuickFind.UI
{
    public partial class PickDatabaseDialog : Window
    {
        internal ObjectEntry SelectedEntry { get; private set; }
        private readonly List<ObjectEntry> _entries;

        internal PickDatabaseDialog(string searchTerm, IEnumerable<ObjectEntry> matches)
        {
            InitializeComponent();
            HeaderText.Text = $"'{searchTerm}' was found in multiple databases. Pick one:";
            _entries = matches.ToList();
            foreach (var e in _entries) DbList.Items.Add(e);
            if (_entries.Count > 0) DbList.SelectedIndex = 0;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) => Accept();
        private void DbList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Accept();

        private void Accept()
        {
            int idx = DbList.SelectedIndex;
            if (idx < 0 || idx >= _entries.Count) return;
            SelectedEntry = _entries[idx];
            DialogResult = true;
            Close();
        }
    }
}
