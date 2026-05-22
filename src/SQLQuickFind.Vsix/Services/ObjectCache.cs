using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace SQLQuickFind.Services
{
    internal sealed class ObjectEntry
    {
        public string Db { get; set; }
        public string Schema { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }

        public string QualifiedDisplay => $"[{Db}].[{Schema}].[{Name}]";
    }

    internal sealed class ObjectCache
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string ServerName { get; set; }
        public DateTime BuiltAtUtc { get; set; }
        public List<ObjectEntry> Objects { get; set; } = new List<ObjectEntry>();

        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public IEnumerable<ObjectEntry> Search(string text, string activeDb)
        {
            if (string.IsNullOrEmpty(text)) return Enumerable.Empty<ObjectEntry>();
            var pool = string.IsNullOrEmpty(activeDb)
                ? Objects
                : Objects.Where(o => string.Equals(o.Db, activeDb, StringComparison.OrdinalIgnoreCase)).ToList();
            return Rank(pool, text);
        }

        public IEnumerable<ObjectEntry> SearchAll(string text)
        {
            if (string.IsNullOrEmpty(text)) return Enumerable.Empty<ObjectEntry>();
            return Rank(Objects, text);
        }

        private static IEnumerable<ObjectEntry> Rank(IEnumerable<ObjectEntry> pool, string text)
        {
            var matches = pool.Where(o => o.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            return matches
                .Select(o => new
                {
                    Entry = o,
                    Tier = string.Equals(o.Name, text, StringComparison.OrdinalIgnoreCase) ? 0
                         : o.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 1
                         : 2
                })
                .OrderBy(x => x.Tier)
                .ThenBy(x => x.Entry.Db, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Entry.Schema, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Entry.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Entry);
        }

        public static string CacheDir
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SQLQuickFind", "cache");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string CachePath(string serverName)
        {
            var key = SanitizeServerKey(serverName);
            return Path.Combine(CacheDir, key + ".json");
        }

        private static string SanitizeServerKey(string serverName)
        {
            var sb = new StringBuilder();
            foreach (var c in serverName)
            {
                if (char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_') sb.Append(c);
                else sb.Append('_');
            }
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(serverName));
                sb.Append('_').Append(BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant());
            }
            return sb.ToString();
        }

        public static ObjectCache Load(string serverName)
        {
            var path = CachePath(serverName);
            try
            {
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                var cache = Serializer.Deserialize<ObjectCache>(json);
                if (cache != null && cache.SchemaVersion == CurrentSchemaVersion) return cache;
            }
            catch { /* corrupt: treat as missing */ }
            return null;
        }

        public void Save()
        {
            var path = CachePath(ServerName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Serializer.Serialize(this));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }
}
