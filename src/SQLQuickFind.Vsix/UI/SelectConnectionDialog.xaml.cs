using System.Collections.Generic;
using System.Windows;
using SQLQuickFind.Services;

namespace SQLQuickFind.UI
{
    public partial class SelectConnectionDialog : Window
    {
        internal ActiveConnection SelectedConnection { get; private set; }
        private readonly IReadOnlyList<ActiveConnection> _connections;

        internal SelectConnectionDialog(IReadOnlyList<ActiveConnection> connections)
        {
            InitializeComponent();
            _connections = connections;
            foreach (var c in connections) ServerList.Items.Add(c.ServerName);
            if (connections.Count > 0) ServerList.SelectedIndex = 0;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) => Accept();
        private void ServerList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => Accept();

        private void Accept()
        {
            int idx = ServerList.SelectedIndex;
            if (idx < 0 || idx >= _connections.Count) return;
            SelectedConnection = _connections[idx];
            DialogResult = true;
            Close();
        }
    }
}
