using System;
using System.Collections;
using System.Collections.Generic;

namespace AppManager
{
    // 大体量内容按需读取；内存仅保留未保存的改动和有界对象缓存。
    public sealed class AddedEntryList : IEnumerable<AddedEntry>
    {
        internal readonly ConfigDatabase Store;
        internal readonly List<AddedEntry> New = new List<AddedEntry>();
        internal readonly Dictionary<long, AddedEntry> Dirty = new Dictionary<long, AddedEntry>();
        internal readonly HashSet<long> Removed = new HashSet<long>();
        private readonly Dictionary<long, AddedEntry> cache = new Dictionary<long, AddedEntry>();
        private readonly Dictionary<string, List<AddedEntry>> changedPaths = new Dictionary<string, List<AddedEntry>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<AddedEntry, string> trackedPaths = new Dictionary<AddedEntry, string>();
        private readonly Dictionary<string, List<AddedEntry>> pathCache = new Dictionary<string, List<AddedEntry>>(StringComparer.OrdinalIgnoreCase);
        private int count;
        public AddedEntryList() { }
        internal AddedEntryList(ConfigDatabase store) { Store = store; using (Sqlite db = new Sqlite(Store.FilePath, true)) count = Convert.ToInt32(db.Scalar("SELECT count(*) FROM added_items")); }
        internal AddedEntryList(ConfigDatabase store, int storedCount) { Store = store; count = storedCount; }
        public int Count { get { return Store == null ? New.Count : count + New.Count - Removed.Count; } }
        public AddedEntry this[int index]
        {
            get
            {
                if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException("index");
                if (Store == null) return New[index];
                int position = 0; foreach (AddedEntry e in this) if (position++ == index) return e;
                throw new ArgumentOutOfRangeException("index");
            }
        }
        public void Add(AddedEntry entry)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            New.Add(entry); entry.Changed = Track; TrackPath(entry);
        }
        private void Track(AddedEntry entry)
        {
            if (entry.RecordId != 0) Dirty[entry.RecordId] = entry;
            TrackPath(entry);
        }
        private void TrackPath(AddedEntry entry)
        {
            pathCache.Clear();
            string old;
            if (trackedPaths.TryGetValue(entry, out old))
            {
                List<AddedEntry> previous = changedPaths[old]; previous.Remove(entry);
                if (previous.Count == 0) changedPaths.Remove(old);
            }
            string key = entry.Path ?? ""; List<AddedEntry> list;
            if (!changedPaths.TryGetValue(key, out list)) { list = new List<AddedEntry>(); changedPaths[key] = list; }
            list.Add(entry); trackedPaths[entry] = key;
        }
        internal AddedEntry Hydrate(string[] row)
        {
            long id = Convert.ToInt64(row[0]); AddedEntry value;
            if (Dirty.TryGetValue(id, out value) || cache.TryGetValue(id, out value)) return value;
            value = new AddedEntry { RecordId = id, ItemId = Convert.ToInt64(row[1]), Path = row[2], Name = row[3], Category = row[4] ?? "", IsFolder = row[5] == "1", IsResource = row[6] == "1" };
            value.Changed = Track;
            if (cache.Count >= 1024) cache.Clear(); cache[id] = value; return value;
        }
        private const string Columns = "SELECT a.id,a.item_id,a.path,a.name,g.key,a.folder,a.resource FROM added_items a LEFT JOIN groups g ON g.id=a.group_id";
        public List<AddedEntry> ByPath(string path)
        {
            List<AddedEntry> cached;
            if (pathCache.TryGetValue(path ?? "", out cached)) return cached;
            if (Store == null) return LoadPath(null, path);
            using (Sqlite db = new Sqlite(Store.FilePath, true)) return LoadPath(db, path);
        }

        /// <summary>批量查询路径归属：批量导入时只开少量只读连接，避免每个路径各开一次数据库。</summary>
        internal Dictionary<string, List<AddedEntry>> LookupMany(IEnumerable<string> paths)
        {
            Dictionary<string, List<AddedEntry>> result =
                new Dictionary<string, List<AddedEntry>>(StringComparer.OrdinalIgnoreCase);
            List<string> distinct = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;
                distinct.Add(path);
                result[path] = new List<AddedEntry>();
            }
            if (distinct.Count == 0) return result;

            if (Store != null)
            {
                using (Sqlite db = new Sqlite(Store.FilePath, true))
                {
                    for (int offset = 0; offset < distinct.Count; offset += 256)
                    {
                        int count = Math.Min(256, distinct.Count - offset);
                        List<string> chunk = distinct.GetRange(offset, count);
                        List<string> marks = chunk.ConvertAll(delegate(string value) { return "?"; });
                        object[] args = new object[count];
                        for (int i = 0; i < count; i++) args[i] = chunk[i].ToUpperInvariant();
                        foreach (string[] row in db.Query(Columns + " WHERE a.path_key IN (" + string.Join(",", marks.ToArray()) + ") ORDER BY a.id", args))
                        {
                            if (Removed.Contains(Convert.ToInt64(row[0]))) continue;
                            AddedEntry entry = Hydrate(row);
                            List<AddedEntry> list;
                            if (result.TryGetValue(entry.Path, out list) && !list.Contains(entry)) list.Add(entry);
                        }
                    }
                }
            }
            foreach (string path in distinct)
            {
                List<AddedEntry> staged;
                if (!changedPaths.TryGetValue(path, out staged)) continue;
                List<AddedEntry> list = result[path];
                foreach (AddedEntry entry in staged)
                    if (!Removed.Contains(entry.RecordId) && !list.Contains(entry)) list.Add(entry);
            }
            return result;
        }

        /// <summary>批量预读未缓存的路径：共用一个只读连接，避免列表逐行查询时每条都新开数据库连接。</summary>
        internal void Prefetch(IEnumerable<string> paths)
        {
            if (Store == null) return;
            List<string> distinct = new List<string>(); HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
                if (!string.IsNullOrEmpty(path) && seen.Add(path) && distinct.Count < 256) distinct.Add(path);
            List<string> missing = distinct.FindAll(delegate(string path) { return !pathCache.ContainsKey(path); });
            if (missing.Count == 0) return;
            // 缓存满 512 会整体清空：放不下时先腾空并整批重读，避免预读中途把自己刚读到的结果清掉
            if (pathCache.Count + missing.Count >= 512) { pathCache.Clear(); missing = distinct; }
            using (Sqlite db = new Sqlite(Store.FilePath, true))
                foreach (string path in missing) LoadPath(db, path);
        }
        private List<AddedEntry> LoadPath(Sqlite db, string path)
        {
            List<AddedEntry> result = new List<AddedEntry>(); HashSet<long> ids = new HashSet<long>();
            if (db != null)
                foreach (string[] row in db.Query(Columns + " WHERE a.path_key=? ORDER BY a.id", (path ?? "").ToUpperInvariant()))
                {
                    AddedEntry entry = Hydrate(row);
                    if (!Removed.Contains(entry.RecordId) && string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase)) { result.Add(entry); ids.Add(entry.RecordId); }
                }
            List<AddedEntry> staged;
            if (changedPaths.TryGetValue(path ?? "", out staged)) foreach (AddedEntry entry in staged)
                if ((entry.RecordId == 0 || (!ids.Contains(entry.RecordId) && !Removed.Contains(entry.RecordId))) && !result.Contains(entry)) result.Add(entry);
            if (pathCache.Count >= 512) pathCache.Clear(); pathCache[path ?? ""] = result;
            return result;
        }
        /// <summary>位于指定文件夹之内（不含文件夹自身）的收录项，包括尚未保存的改动。</summary>
        internal List<AddedEntry> EntriesUnder(string folder)
        {
            List<AddedEntry> result = new List<AddedEntry>();
            if (string.IsNullOrEmpty(folder)) return result;
            string prefix = folder.TrimEnd('\\', '/') + "\\";
            Predicate<AddedEntry> under = delegate(AddedEntry e)
            { return e.Path != null && e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !result.Contains(e); };
            if (Store != null)
            {
                string key = prefix.ToUpperInvariant();
                using (Sqlite db = new Sqlite(Store.FilePath, true))
                    foreach (string[] row in db.Query(Columns + " WHERE substr(a.path_key,1,?)=? ORDER BY a.id", key.Length, key))
                    {
                        if (Removed.Contains(Convert.ToInt64(row[0]))) continue;
                        AddedEntry entry = Hydrate(row);   // 未保存的改名以内存对象为准，下面按当前路径复核
                        if (under(entry)) result.Add(entry);
                    }
                foreach (AddedEntry entry in Dirty.Values)
                    if (!Removed.Contains(entry.RecordId) && under(entry)) result.Add(entry);
            }
            foreach (AddedEntry entry in New) if (under(entry)) result.Add(entry);
            return result;
        }
        public bool Remove(AddedEntry entry)
        {
            pathCache.Clear();
            string tracked;
            if (trackedPaths.TryGetValue(entry, out tracked))
            {
                changedPaths[tracked].Remove(entry); trackedPaths.Remove(entry);
            }
            if (entry.RecordId == 0 || Store == null) return New.Remove(entry);
            Dirty.Remove(entry.RecordId); cache.Remove(entry.RecordId); return Removed.Add(entry.RecordId);
        }
        public void RemoveAt(int index) { Remove(this[index]); }
        public void Clear() { foreach (AddedEntry entry in new List<AddedEntry>(this)) Remove(entry); }
        public int IndexOf(AddedEntry entry) { int i = 0; foreach (AddedEntry value in this) { if (value == entry || (entry.RecordId != 0 && value.RecordId == entry.RecordId)) return i; i++; } return -1; }
        internal IEnumerable<AddedEntry> SoftwareEntries() { return Enumerate(true); }
        public IEnumerator<AddedEntry> GetEnumerator() { return Enumerate(false).GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
        private IEnumerable<AddedEntry> Enumerate(bool softwareOnly)
        {
            if (Store != null)
            {
                long after = 0;
                while (true)
                {
                    List<string[]> batch;
                    using (Sqlite db = new Sqlite(Store.FilePath, true)) batch = db.Query(Columns + " WHERE a.id>?" + (softwareOnly ? " AND a.resource=0" : "") + " ORDER BY a.id LIMIT 256", after);
                    if (batch.Count == 0) break;
                    foreach (string[] row in batch)
                    {
                        after = Convert.ToInt64(row[0]);
                        if (Removed.Contains(after)) continue;
                        AddedEntry entry = Hydrate(row); if (!softwareOnly || !entry.IsResource) yield return entry;
                    }
                }
            }
            foreach (AddedEntry entry in New) if (!softwareOnly || !entry.IsResource) yield return entry;
        }
        internal void Accept(int storedCount)
        {
            New.Clear(); Dirty.Clear(); Removed.Clear(); cache.Clear(); changedPaths.Clear(); trackedPaths.Clear(); pathCache.Clear();
            count = storedCount;
        }
    }
}
