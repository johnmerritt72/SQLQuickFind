using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace SQLQuickFind.Services
{
    internal sealed class HistoryStore
    {
        public const int MaxEntries = 15;

        private readonly string _path;
        private readonly List<string> _items = new List<string>();
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public HistoryStore(string path)
        {
            _path = path;
            Load();
        }

        public static HistoryStore CreateDefault()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SQLQuickFind");
            Directory.CreateDirectory(dir);
            return new HistoryStore(Path.Combine(dir, "history.json"));
        }

        public IReadOnlyList<string> GetAll() => _items.AsReadOnly();

        public void Add(string search)
        {
            if (string.IsNullOrWhiteSpace(search)) return;
            _items.RemoveAll(s => string.Equals(s, search, StringComparison.OrdinalIgnoreCase));
            _items.Insert(0, search);
            while (_items.Count > MaxEntries) _items.RemoveAt(_items.Count - 1);
            Save();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                var json = File.ReadAllText(_path);
                var list = Serializer.Deserialize<List<string>>(json);
                if (list != null)
                {
                    _items.Clear();
                    _items.AddRange(list.Take(MaxEntries));
                }
            }
            catch { /* corrupt file: start fresh */ }
        }

        private void Save()
        {
            try
            {
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, Serializer.Serialize(_items));
                if (File.Exists(_path)) File.Replace(tmp, _path, null);
                else File.Move(tmp, _path);
            }
            catch { /* best effort */ }
        }
    }
}
