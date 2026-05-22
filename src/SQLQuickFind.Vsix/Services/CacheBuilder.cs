using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;

namespace SQLQuickFind.Services
{
    internal sealed class CacheBuildResult
    {
        public ObjectCache Cache { get; set; }
        public int DatabaseCount { get; set; }
        public string Error { get; set; }
    }

    internal sealed class CacheBuilder
    {
        private readonly string _connectionString;
        private readonly string _serverName;

        public CacheBuilder(string serverName, string connectionString)
        {
            _serverName = serverName;
            _connectionString = connectionString;
        }

        public async Task<CacheBuildResult> BuildAsync(IProgress<ObjectEntry> progress, CancellationToken ct)
        {
            var result = new CacheBuildResult
            {
                Cache = new ObjectCache
                {
                    ServerName = _serverName,
                    BuiltAtUtc = DateTime.UtcNow,
                    Objects = new List<ObjectEntry>()
                }
            };

            try
            {
                var dbs = await EnumerateDatabasesAsync(ct).ConfigureAwait(false);
                result.DatabaseCount = dbs.Count;
                foreach (var db in dbs)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var entries = await EnumerateObjectsAsync(db, ct).ConfigureAwait(false);
                        result.Cache.Objects.AddRange(entries);
                        foreach (var e in entries) progress?.Report(e);
                    }
                    catch
                    {
                        // Skip databases we can't read (offline mid-build, perms changed, etc.)
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }

            return result;
        }

        private async Task<List<string>> EnumerateDatabasesAsync(CancellationToken ct)
        {
            var dbs = new List<string>();
            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT name
FROM sys.databases
WHERE state_desc = 'ONLINE'
  AND HAS_DBACCESS(name) = 1
  AND database_id > 4
ORDER BY name;";
                    using (var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                    {
                        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
                            dbs.Add(rdr.GetString(0));
                    }
                }
            }
            return dbs;
        }

        private async Task<List<ObjectEntry>> EnumerateObjectsAsync(string db, CancellationToken ct)
        {
            var list = new List<ObjectEntry>();
            var csb = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = db };
            using (var conn = new SqlConnection(csb.ConnectionString))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
SELECT
    DB_NAME()                AS database_name,
    SCHEMA_NAME(o.schema_id) AS schema_name,
    o.name                   AS object_name,
    RTRIM(o.type)            AS object_type
FROM sys.objects o
WHERE o.type IN ('P','PC','FN','TF','IF')
  AND o.is_ms_shipped = 0;";
                    using (var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                    {
                        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
                        {
                            list.Add(new ObjectEntry
                            {
                                Db     = rdr.GetString(0),
                                Schema = rdr.GetString(1),
                                Name   = rdr.GetString(2),
                                Type   = rdr.GetString(3)
                            });
                        }
                    }
                }
            }
            return list;
        }
    }
}
