using System;
using System.Data.SqlClient;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.SqlServer.Management.UI.VSIntegration.Editors;
using Microsoft.VisualStudio.Shell;

namespace SQLQuickFind.Services
{
    internal static class ObjectOpener
    {
        public static async Task OpenAsync(ActiveConnection baseConn, ObjectEntry entry)
        {
            string dbConnStr = ConnectionContext.BuildConnectionStringForServerDb(baseConn, entry.Db);
            string ddl = await FetchDefinitionAsync(dbConnStr, entry).ConfigureAwait(false);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (ddl == null)
            {
                System.Windows.MessageBox.Show(
                    $"Cannot retrieve definition for {entry.QualifiedDisplay} — object may be encrypted.",
                    "SQLQuickFind", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            string text = BuildScriptText(entry.Db, ddl);
            OpenInNewQueryWindow(text, entry.Db);
        }

        public static string BuildScriptText(string database, string rawDdl)
        {
            var transformed = TransformCreateToCreateOrAlter(rawDdl);
            var sb = new StringBuilder();
            sb.Append("USE [").Append(database).Append("]").Append("\r\n");
            sb.Append("GO").Append("\r\n");
            sb.Append("\r\n");
            sb.Append(transformed);
            return sb.ToString();
        }

        public static string TransformCreateToCreateOrAlter(string ddl)
        {
            if (string.IsNullOrEmpty(ddl)) return ddl;
            // Replace first occurrence of CREATE PROCEDURE|FUNCTION (case insensitive, only at first non-comment statement)
            // We use a regex with word boundaries; ignore leading whitespace/comments.
            var pattern = new Regex(
                @"(?ims)\b(CREATE)\s+(PROCEDURE|PROC|FUNCTION)\b",
                RegexOptions.Compiled);
            var m = pattern.Match(ddl);
            if (!m.Success) return ddl;
            return ddl.Substring(0, m.Index)
                 + "CREATE OR ALTER " + m.Groups[2].Value
                 + ddl.Substring(m.Index + m.Length);
        }

        private static async Task<string> FetchDefinitionAsync(string connectionString, ObjectEntry entry)
        {
            string qualified = $"[{entry.Schema}].[{entry.Name}]";
            using (var conn = new SqlConnection(connectionString))
            {
                await conn.OpenAsync().ConfigureAwait(false);

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT OBJECT_DEFINITION(OBJECT_ID(@n))";
                    cmd.Parameters.AddWithValue("@n", qualified);
                    var def = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                    if (def != null && def != DBNull.Value)
                    {
                        var s = def as string;
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }

                // Fallback: sp_helptext
                try
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "EXEC sp_helptext @n";
                        cmd.Parameters.AddWithValue("@n", qualified);
                        var sb = new StringBuilder();
                        using (var rdr = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                        {
                            while (await rdr.ReadAsync().ConfigureAwait(false))
                                sb.Append(rdr.GetString(0));
                        }
                        var text = sb.ToString();
                        if (!string.IsNullOrEmpty(text)) return text;
                    }
                }
                catch { /* fallthrough to null */ }
            }
            return null;
        }

        private static void OpenInNewQueryWindow(string text, string database)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var factory = ServiceCache.ScriptFactory;
                if (factory == null) throw new InvalidOperationException("ScriptFactory unavailable.");

                // Open the new query window already connected to the object's database so the database
                // dropdown and IntelliSense bind to the right catalog. We clone the currently-active
                // connection's UIConnectionInfo (leaving the live connection untouched) and override its
                // DATABASE before creating the script. The USE [db] header remains as a fallback for
                // execution context.
                bool opened = false;
                try
                {
                    var active = factory.CurrentlyActiveWndConnectionInfo?.UIConnectionInfo;
                    if (active != null && !string.IsNullOrEmpty(database))
                    {
                        var ci = active.Copy();
                        ci.AdvancedOptions["DATABASE"] = database;
                        factory.CreateNewBlankScript(ScriptType.Sql, ci, null);
                        opened = true;
                    }
                }
                catch
                {
                    // Undocumented SSMS API surface — if the connection-bearing overload misbehaves,
                    // degrade to a plain blank script (USE [db] header still sets the execution database).
                    opened = false;
                }

                if (!opened)
                    factory.CreateNewBlankScript(ScriptType.Sql);

                var dte = SQLQuickFindPackage.Dte;
                if (dte?.ActiveDocument?.Object("TextDocument") is EnvDTE.TextDocument td)
                {
                    var sel = td.Selection as EnvDTE.TextSelection;
                    sel?.Insert(text);
                    sel?.StartOfDocument(false);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Failed to open query window: {ex.Message}",
                    "SQLQuickFind", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
    }
}
