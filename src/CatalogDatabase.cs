using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Diagnostics;

namespace AppManager
{
    internal sealed class CatalogPage
    {
        internal List<AppInfo> Items = new List<AppInfo>();
        internal int Total;
        internal long LastRank, LastId;
        internal string LastName;
    }

    internal sealed partial class ConfigDatabase
    {
        private static string Pack(AppInfo a)
        {
            XmlDocument doc = new XmlDocument(); XmlElement n = doc.CreateElement("App"); doc.AppendChild(n);
            n.SetAttribute("key", a.Key); n.SetAttribute("name", a.Name); n.SetAttribute("target", a.TargetPath);
            n.SetAttribute("shortcut", a.ShortcutPath); n.SetAttribute("install", a.InstallLocation);
            n.SetAttribute("uninstall", a.UninstallString); n.SetAttribute("icon", a.DisplayIcon);
            n.SetAttribute("category", a.Category); n.SetAttribute("source", a.Source);
            n.SetAttribute("uwp", a.IsUwp ? "1" : "0"); n.SetAttribute("desktop", a.IsDesktop ? "1" : "0");
            n.SetAttribute("file", a.IsFile ? "1" : "0"); n.SetAttribute("folder", a.IsFolder ? "1" : "0");
            return n.OuterXml;
        }
        private static AppInfo Unpack(string xml)
        {
            XmlDocument doc = new XmlDocument(); doc.XmlResolver = null; doc.LoadXml(xml); XmlElement n = doc.DocumentElement;
            return new AppInfo {
                Key = n.GetAttribute("key"), Name = n.GetAttribute("name"), TargetPath = n.GetAttribute("target"),
                ShortcutPath = n.GetAttribute("shortcut"), InstallLocation = n.GetAttribute("install"),
                UninstallString = n.GetAttribute("uninstall"), DisplayIcon = n.GetAttribute("icon"),
                Category = n.GetAttribute("category"), Source = n.GetAttribute("source"), IsUwp = n.GetAttribute("uwp") == "1",
                IsDesktop = n.GetAttribute("desktop") == "1", IsFile = n.GetAttribute("file") == "1", IsFolder = n.GetAttribute("folder") == "1"
            };
        }
        private void PutCatalog(Sqlite db, long id, AppInfo app, int kind)
        {
            long group = Group(db, app.Category);
            string packed = Pack(app);
            string legacy = "af:" + Scanner.NormalizeEntryName(app.Name);
            db.Execute("INSERT OR IGNORE INTO catalog(item_id,name,search_name,legacy_key,base_group,kind,hidden,active,metadata) VALUES(?,?,?,?,?,?,0,1,?)",
                id, app.Name, SearchName(app.Name), legacy, Foreign(group), kind, packed);
            db.Execute("UPDATE catalog SET name=?,search_name=?,legacy_key=?,base_group=?,kind=?,active=1,metadata=?,indexed=0 WHERE item_id=? AND (metadata<>? OR kind<>? OR active<>1)",
                app.Name, SearchName(app.Name), legacy, Foreign(group), kind, packed, id, packed, kind);
        }
        private static void ExpandLegacyAffected(Sqlite db, HashSet<long> affected)
        {
            using (Sqlite.Statement rows = db.Prepare("SELECT c.item_id FROM entries e JOIN catalog c ON c.legacy_key=e.key WHERE e.id=? AND e.key LIKE 'af:%'"))
                foreach (long id in new List<long>(affected))
                { rows.Bind(id); while (rows.Read()) affected.Add(Number(rows.Row()[0])); }
        }

        private void EnsureCatalog(AppConfig config)
        {
            lock (gate)
            using (Sqlite db = Open())
            {
                if (db.Scalar("SELECT value FROM metadata WHERE key='catalog_invalid'") != "1") return;
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    long op = NewOperation(db, "catalog.rebuild"); db.Execute("UPDATE write_context SET operation_id=? WHERE id=1", op);
                    HashSet<long> affected = new HashSet<long>();
                    foreach (string[] row in db.Query("SELECT DISTINCT item_id FROM added_items")) affected.Add(Number(row[0]));
                    UpdateAddedCatalog(db, affected);
                    RebuildListings(db, config, affected);
                    db.Execute("UPDATE write_context SET operation_id=NULL WHERE id=1");
                    if (db.Scalar("SELECT count(*) FROM changes WHERE operation_id=?", op) == "0") db.Execute("DELETE FROM operations WHERE id=?", op);
                    else { revision++; db.Execute("UPDATE metadata SET value=? WHERE key='revision'", revision.ToString()); }
                    db.Execute("DELETE FROM metadata WHERE key='catalog_invalid'");
                    db.Execute("COMMIT");
                }
                catch { db.Execute("ROLLBACK"); LoadBaseline(db); throw; }
            }
        }

        // 扫描在后台完成，发布目录时和关系更新共用写锁；失败不会清空上次可用结果。
        internal void PublishCatalog(AppConfig config, List<AppInfo> apps)
        {
            lock (gate)
            using (Sqlite db = Open())
            {
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    if (config.DatabaseRevision != revision || Number(db.Scalar("SELECT value FROM metadata WHERE key='revision'")) != revision)
                        throw new IOException("目录发布遇到较新数据，请重新加载。");
                    long operation = NewOperation(db, "catalog.discover"); db.Execute("UPDATE write_context SET operation_id=? WHERE id=1", operation);
                    db.Execute("UPDATE catalog SET active=0 WHERE kind<>2 AND NOT EXISTS(SELECT 1 FROM added_items a WHERE a.item_id=catalog.item_id)");
                    HashSet<long> affected = new HashSet<long>();
                    foreach (AppInfo a in apps)
                    {
                        long id = Entry(db, a.Key);
                        PutCatalog(db, id, a, a.IsFile ? 1 : 0); affected.Add(id);
                    }
                    foreach (string[] row in db.Query("SELECT item_id FROM catalog WHERE active=0")) affected.Add(Number(row[0]));
                    RebuildListings(db, config, affected);
                    db.Execute("UPDATE write_context SET operation_id=NULL WHERE id=1");
                    int changes = Convert.ToInt32(db.Scalar("SELECT count(*) FROM changes WHERE operation_id=?", operation));
                    if (changes == 0) db.Execute("DELETE FROM operations WHERE id=?", operation);
                    else { revision++; db.Execute("UPDATE operations SET summary=? WHERE id=?", "new identities=" + changes, operation); db.Execute("UPDATE metadata SET value=? WHERE key='revision'", revision.ToString()); }
                    db.Execute("COMMIT");
                    config.DatabaseRevision = revision;
                }
                catch { db.Execute("ROLLBACK"); LoadBaseline(db); throw; }
            }
        }

        private void RebuildListings(Sqlite db, AppConfig config, HashSet<long> affected)
        {
            using (Sqlite.Statement read = db.Prepare("SELECT c.metadata,c.kind,c.active,g.key,c.size,c.modified FROM catalog c LEFT JOIN groups g ON g.id=c.base_group WHERE c.item_id=?"))
            using (Sqlite.Statement relations = db.Prepare("SELECT r.section,g.key,r.ordinal,r.item_id FROM records r LEFT JOIN groups g ON g.id=r.group_id WHERE r.item_id=? OR r.item_id=(SELECT id FROM entries WHERE key=?) ORDER BY CASE WHEN r.item_id=? THEN 0 ELSE 1 END"))
            using (Sqlite.Statement remove = db.Prepare("DELETE FROM listing WHERE item_id=?"))
            using (Sqlite.Statement insert = db.Prepare("INSERT OR IGNORE INTO listing(group_id,item_id,kind,hidden,rank,name,size,modified) VALUES(?,?,?,?,?,?,?,?)"))
            using (Sqlite.Statement setHidden = db.Prepare("UPDATE catalog SET hidden=? WHERE item_id=?"))
            {
                foreach (long id in affected)
                {
                    remove.Bind(id); remove.Read();
                    read.Bind(id); if (!read.Read()) continue; string[] row = read.Row(); if (row[2] != "1") continue;
                    AppInfo a = Unpack(row[0]); int kind = Convert.ToInt32(row[1]);
                    if (row[3] != null) a.Category = row[3];
                    string legacy = "af:" + Scanner.NormalizeEntryName(a.Name);
                    bool isHidden = false, favorite = false, overridden = false;
                    string primary = a.Category;
                    HashSet<string> categories = new HashSet<string>(StringComparer.Ordinal);
                    HashSet<string> excluded = new HashSet<string>(StringComparer.Ordinal);
                    Dictionary<string, long> ranks = new Dictionary<string, long>(StringComparer.Ordinal);
                    relations.Bind(id, legacy, id);
                    while (relations.Read())
                    {
                        string[] relation = relations.Row(); bool exact = Number(relation[3]) == id;
                        switch (relation[0])
                        {
                            case "Favorites": favorite = true; break;
                            case "Hidden": isHidden = true; break;
                            case "CategoryOverrides": if (!overridden && !string.IsNullOrEmpty(relation[1])) { primary = relation[1]; overridden = true; } break;
                            case "ItemCategories": if (exact) categories.Add(relation[1]); break;
                            case "ExcludedCategories": if (exact) excluded.Add(relation[1]); break;
                            case "ItemOrders": if (!ranks.ContainsKey(relation[1])) ranks[relation[1]] = Number(relation[2]); break;
                        }
                    }
                    setHidden.Bind(isHidden, id); setHidden.Read();
                    categories.Add(primary);
                    if (!overridden && a.IsDesktop) categories.Add("快捷中心");
                    foreach (string cat in excluded) categories.Remove(cat);
                    // 集合成员必须仍属于父分类，集合关系自身保留，移回父分类后可恢复。
                    List<string> invalid = new List<string>();
                    foreach (string cat in categories) if (CollectionKeys.IsCollectionKey(cat))
                    {
                        int slash = cat.IndexOf('/', 4);
                        if (slash < 0 || !categories.Contains(cat.Substring(4, slash - 4))) invalid.Add(cat);
                    }
                    foreach (string cat in invalid) categories.Remove(cat);
                    categories.Add(kind == 2 ? "全部资源" : (kind == 0 ? "全部软件" : "全部文件"));
                    if (favorite) categories.Add("常用");
                    foreach (string category in categories)
                    {
                        if (string.IsNullOrEmpty(category)) continue;
                        long rank = long.MaxValue;
                        if (!ranks.TryGetValue(category, out rank)) rank = long.MaxValue;
                        insert.Bind(Group(db, category), id, kind == 2 ? 2 : 0, isHidden, rank, SearchName(a.Name), Number(row[4]), Number(row[5])); insert.Read();
                    }
                }
            }
        }

        private static string SearchName(string value)
        {
            StringBuilder s = new StringBuilder();
            foreach (char c in (value ?? "").ToLowerInvariant()) if (char.IsLetterOrDigit(c)) s.Append(c);
            return s.ToString();
        }
        private static string SearchPattern(string value)
        {
            string normal = SearchName(value); if (normal.Length > 64) normal = normal.Substring(0, 64);
            StringBuilder s = new StringBuilder("%"); foreach (char c in normal) { s.Append(c); s.Append('%'); } return s.ToString();
        }

        // 游标翻页，不使用深 OFFSET。name/id 为稳定次序，防止同名条目跨页重复或漏读。
        internal CatalogPage Page(string category, bool resources, bool showHidden, string keyword, int limit, CatalogPage after, bool byName, bool descending, string sortColumn = null)
        {
            if (limit < 1 || limit > 500) throw new ArgumentOutOfRangeException("limit");
            lock (gate)
            using (Sqlite db = Open())
            {
                CatalogPage result = new CatalogPage();
                List<object> args = new List<object>();
                string from;
                string where;
                bool search = !string.IsNullOrEmpty(keyword);
                bool metadataSort = sortColumn == "size" || sortColumn == "time";
                if (search)
                {
                    from = "catalog c";
                    where = "c.active=1 AND " + (resources ? "c.kind=2" : "c.kind<>2") + (showHidden ? "" : " AND c.hidden=0") + " AND (c.search_name LIKE ? OR EXISTS(SELECT 1 FROM listing sl JOIN groups sg ON sg.id=sl.group_id WHERE sl.item_id=c.item_id AND sg.key NOT IN('全部软件','全部文件','全部资源','常用') AND sg.name LIKE ?))";
                    args.Add(SearchPattern(keyword));
                    args.Add(SearchPattern(keyword));
                    byName = true;
                }
                else
                {
                    long group;
                    if (!groups.TryGetValue(category, out group)) return result;
                    from = "listing l JOIN catalog c ON c.item_id=l.item_id";
                    where = "l.group_id=? AND " + (resources ? "l.kind=2" : "l.kind=0") + (showHidden ? "" : " AND l.hidden=0");
                    args.Add(group);
                }
                result.Total = after == null ? Convert.ToInt32(db.Scalar("SELECT count(*) FROM " + (search ? "catalog c" : "listing l") + " WHERE " + where, args.ToArray())) : after.Total;
                string name = search ? "c.search_name" : "l.name";
                string itemId = search ? "c.item_id" : "l.item_id";
                string rank = metadataSort ? (search ? "c." : "l.") + (sortColumn == "size" ? "size" : "modified") : (search || byName ? "0" : "l.rank");
                string compare = descending ? "<" : ">";
                if (after != null)
                {
                    if (metadataSort)
                    { where += " AND (" + rank + "," + name + "," + itemId + ")" + compare + "(?,?,?)"; args.Add(after.LastRank); args.Add(after.LastName); args.Add(after.LastId); }
                    else if (search || byName)
                    { where += " AND (" + name + "," + itemId + ")" + compare + "(?,?)"; args.Add(after.LastName); args.Add(after.LastId); }
                    else
                    { where += " AND (l.rank,l.name,l.item_id)>(?,?,?)"; args.Add(after.LastRank); args.Add(after.LastName); args.Add(after.LastId); }
                }
                string order;
                if (metadataSort) order = rank + (descending ? " DESC," + name + " DESC," + itemId + " DESC" : "," + name + "," + itemId);
                else order = (search || byName ? name : "l.rank,l.name") + (descending && (search || byName) ? " DESC," + itemId + " DESC" : "," + itemId);
                args.Add(limit);
                foreach (string[] row in db.Query("SELECT c.metadata,c.hidden,c.item_id," + rank + "," + name + ",g.key,c.kind FROM " + from + " LEFT JOIN groups g ON g.id=c.base_group WHERE " + where + " ORDER BY " + order + " LIMIT ?", args.ToArray()))
                {
                    AppInfo a = Unpack(row[0]); a.IsHidden = row[1] == "1"; result.Items.Add(a);
                    if (row[5] != null) a.Category = row[5];
                    a.DatabaseKind = Convert.ToInt32(row[6]);
                    result.LastId = Number(row[2]); result.LastRank = Number(row[3]); result.LastName = row[4];
                }
                return result;
            }
        }

        // 文件元数据分批索引，目录切换和分页不触发全分类同步读盘。
        internal void IndexMetadata(Func<bool> canceled)
        {
            Stopwatch budget = Stopwatch.StartNew();
            while (budget.Elapsed < TimeSpan.FromSeconds(45))
            {
                if (canceled != null && canceled()) return;
                List<string[]> batch;
                lock (gate) using (Sqlite db = Open()) batch = db.Query("SELECT item_id,metadata FROM catalog WHERE indexed=0 AND active=1 ORDER BY item_id LIMIT 128");
                if (batch.Count == 0) return;
                List<long[]> values = new List<long[]>();
                foreach (string[] row in batch)
                {
                    long size = -1, modified = -1;
                    try
                    {
                        AppInfo app = Unpack(row[1]);
                        if (!app.IsUwp && !string.IsNullOrEmpty(app.TargetPath) && !app.TargetPath.StartsWith("\\\\", StringComparison.Ordinal))
                        {
                            if (app.IsFolder) { DirectoryInfo folder = new DirectoryInfo(app.TargetPath); if (folder.Exists) modified = folder.LastWriteTimeUtc.Ticks; }
                            else
                            {
                                FileInfo file = new FileInfo(app.TargetPath);
                                if (file.Exists)
                                {
                                    size = file.Length; DateTime taken;
                                    modified = MediaInfo.TryGetTakenDate(app.TargetPath, out taken) ? taken.ToUniversalTime().Ticks : file.LastWriteTimeUtc.Ticks;
                                }
                            }
                        }
                    }
                    catch { }
                    values.Add(new long[] { Number(row[0]), size, modified });
                }
                lock (gate)
                using (Sqlite db = Open())
                {
                    db.Execute("BEGIN IMMEDIATE");
                    try
                    {
                        using (Sqlite.Statement catalog = db.Prepare("UPDATE catalog SET size=?,modified=?,indexed=1 WHERE item_id=? AND metadata=?"))
                        using (Sqlite.Statement listing = db.Prepare("UPDATE listing SET size=?,modified=? WHERE item_id=? AND EXISTS(SELECT 1 FROM catalog c WHERE c.item_id=listing.item_id AND c.metadata=?) AND (size<>? OR modified<>?)"))
                            for (int i = 0; i < batch.Count; i++)
                            {
                                long[] v = values[i]; catalog.Bind(v[1], v[2], v[0], batch[i][1]); catalog.Read();
                                listing.Bind(v[1], v[2], v[0], batch[i][1], v[1], v[2]); listing.Read();
                            }
                        db.Execute("COMMIT");
                    }
                    catch { db.Execute("ROLLBACK"); throw; }
                }
            }
        }

        internal string Check()
        {
            using (Sqlite db = Open())
            {
                string integrity = db.Scalar("PRAGMA integrity_check");
                int violations = db.Query("PRAGMA foreign_key_check").Count;
                return "integrity=" + integrity + "; foreign_key_errors=" + violations + "; schema=" + db.Scalar("PRAGMA user_version") +
                    "; records=" + db.Scalar("SELECT count(*) FROM records") + "; items=" + db.Scalar("SELECT count(*) FROM catalog") +
                    "; memberships=" + db.Scalar("SELECT count(*) FROM listing") + "; journal=" + db.Scalar("PRAGMA journal_mode");
            }
        }
    }
}
