using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo.RegSvrEnum;
using Microsoft.SqlServer.Management.UI.VSIntegration;
using Microsoft.VisualStudio.Shell;

namespace SQLQuickFind.Services
{
    internal sealed class ActiveConnection
    {
        public string ServerName { get; set; }
        public string DatabaseName { get; set; }
        public string ConnectionString { get; set; }
    }

    internal static class ConnectionContext
    {
        public static ActiveConnection GetActiveConnection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var ci = ServiceCache.ScriptFactory?.CurrentlyActiveWndConnectionInfo?.UIConnectionInfo;
                if (ci == null) return null;

                var server = ci.ServerName;
                if (string.IsNullOrWhiteSpace(server)) return null;

                string d = ci.AdvancedOptions?["DATABASE"];
                var db = !string.IsNullOrEmpty(d) ? d : "master";

                return new ActiveConnection
                {
                    ServerName = server,
                    DatabaseName = db,
                    ConnectionString = BuildConnectionStringFromUIConnectionInfo(ci, db)
                };
            }
            catch
            {
                return null;
            }
        }

        public static IReadOnlyList<ActiveConnection> GetObjectExplorerConnections()
        {
            // V1 limitation: SSMS does not expose a clean public API for enumerating Object Explorer
            // connections. Until we wire up the correct ServiceCache + ObjectExplorerService entry
            // points (requires runtime verification against SSMS 22), this returns empty and callers
            // fall back to "open a query window first" guidance.
            ThreadHelper.ThrowIfNotOnUIThread();
            return Array.Empty<ActiveConnection>();
        }

        public static string BuildConnectionStringForServerDb(ActiveConnection baseConn, string db)
        {
            var csb = new SqlConnectionStringBuilder(baseConn.ConnectionString) { InitialCatalog = db };
            return csb.ConnectionString;
        }

        private static string BuildConnectionStringFromUIConnectionInfo(UIConnectionInfo ci, string db)
        {
            var csb = new SqlConnectionStringBuilder
            {
                DataSource = ci.ServerName,
                InitialCatalog = db,
                ApplicationName = "SQLQuickFind",
                ConnectTimeout = 15,
                Pooling = true
            };
            switch (ci.AuthenticationType)
            {
                case 0: // Windows
                    csb.IntegratedSecurity = true;
                    break;
                case 1: // SQL auth
                    csb.UserID = ci.UserName ?? "";
                    csb.Password = ci.Password ?? "";
                    break;
                default:
                    // Azure AD / token-based — let SqlClient handle defaults; treat like integrated.
                    csb.IntegratedSecurity = true;
                    break;
            }
            return csb.ConnectionString;
        }

    }
}
