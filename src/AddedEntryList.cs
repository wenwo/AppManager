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
            List<AddedEntry> result = new List<AddedEntry>(); HashSet<long> ids = new HashSet<long>();
            if (Store != null) using (Sqlite db = new Sqlite(Store.FilePath, true))
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
