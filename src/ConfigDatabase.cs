using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace AppManager
{
    // 正式数据只写入 SQLite。XML 保留为导入/导出格式和迁移原件。
    internal sealed partial class ConfigDatabase
    {
        internal readonly string FilePath;
        private readonly object gate = new object();
        private long revision;
        private Dictionary<string, Record> baseline;
        private readonly Dictionary<string, long> groups = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> entries = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly List<string[]> renames = new List<string[]>();
        private readonly List<string[]> entryRenames = new List<string[]>();
        private const int ApplicationId = 1095584594;
        private const int SchemaVersion = 1;
        private static readonly string Session = Guid.NewGuid().ToString("N");
        internal long Revision { get { return revision; } }

        private sealed class Record
        {
            internal string Key, Section, Slot, Payload;
            internal long Item, Group, Ordinal;
            internal bool Same(Record r) { return Payload == r.Payload && Ordinal == r.Ordinal; }
        }
        internal ConfigDatabase(string filePath)
        {
            FilePath = filePath;
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(filePath))
            {
                using (Sqlite probe = new Sqlite(filePath, true))
                {
                    int knownVersion = Convert.ToInt32(probe.Scalar("PRAGMA user_version"));
                    int knownApp = Convert.ToInt32(probe.Scalar("PRAGMA application_id"));
                    if ((knownApp != 0 && knownApp != ApplicationId) || knownVersion > SchemaVersion ||
                        (knownApp == 0 && probe.Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'") != "0"))
                        throw new IOException("数据库身份或版本不兼容，原文件未修改。");
                }
            }
            using (Sqlite db = Open())
            {
                int version = Convert.ToInt32(db.Scalar("PRAGMA user_version"));
                int app = Convert.ToInt32(db.Scalar("PRAGMA application_id"));
                if (version > SchemaVersion || (app != 0 && app != ApplicationId)) throw new IOException("数据库格式不兼容，已停止写入。");
                if (version == 0)
                {
                    if (db.Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'") != "0")
                        throw new IOException("拒绝初始化非空未知数据库。");
                    CreateSchema(db);
                }
                if (db.Scalar("PRAGMA quick_check") != "ok") throw new IOException("数据库完整性检查失败，请从备份恢复。");
                LoadBaseline(db);
            }
        }

        private Sqlite Open() { return new Sqlite(FilePath, false); }
        internal static string DatabasePath(string xmlPath) { return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(xmlPath)), "appmanager.db"); }

        internal static AppConfig Load(string xmlPath)
        {
            try
            {
                string path = DatabasePath(xmlPath);
                AppConfig legacy = null;
                if (!File.Exists(path))
                {
                    legacy = AppConfig.LoadFrom(xmlPath);
                    if (legacy.LoadFailed) return legacy;
                    if (File.Exists(xmlPath))
                    {
                        string backup = xmlPath + ".before-sqlite";
                        if (!File.Exists(backup)) File.Copy(xmlPath, backup, false);
                    }
                }
                ConfigDatabase store = new ConfigDatabase(path);
                using (Sqlite db = store.Open())
                {
                    if (db.Scalar("SELECT value FROM metadata WHERE key='migrated'") != "1")
                    {
                        if (legacy == null) legacy = AppConfig.LoadFrom(xmlPath);
                        if (legacy.LoadFailed) return legacy;
                        if (!store.Save(legacy, "migration.xml")) throw new IOException(legacy.DatabaseError);
                    }
                }
                AppConfig result = store.Read();
                result.Database = store;
                store.EnsureCatalog(result);
                result.DatabaseRevision = store.revision;
                return result;
            }
            catch (Exception ex)
            {
                Paths.LogError(ex);
                return new AppConfig { LoadFailed = true, DatabaseError = ex.Message };
            }
        }

        internal static bool SaveDetached(AppConfig config, string xmlPath, string action)
        {
            AppConfig current = Load(xmlPath);
            if (current.LoadFailed) { config.DatabaseError = current.DatabaseError; return false; }
            config.Database = current.Database;
            config.DatabaseRevision = current.DatabaseRevision;
            return config.Database.Save(config, action);
        }

        private static void CreateSchema(Sqlite db)
        {
            db.Execute("BEGIN IMMEDIATE");
            try
            {
                string[] ddl = {
                    "CREATE TABLE metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL)",
                    "INSERT INTO metadata VALUES('revision','0')",
                    "CREATE TABLE operations(id INTEGER PRIMARY KEY AUTOINCREMENT,utc TEXT NOT NULL,action TEXT NOT NULL,summary TEXT NOT NULL DEFAULT '',reverts INTEGER REFERENCES operations(id),actor TEXT NOT NULL,session TEXT NOT NULL,pid INTEGER NOT NULL)",
                    "CREATE TABLE write_context(id INTEGER PRIMARY KEY CHECK(id=1),operation_id INTEGER REFERENCES operations(id))",
                    "INSERT INTO write_context VALUES(1,NULL)",
                    "CREATE TABLE groups(id INTEGER PRIMARY KEY AUTOINCREMENT,key TEXT NOT NULL UNIQUE,name TEXT NOT NULL,parent_id INTEGER REFERENCES groups(id) DEFERRABLE INITIALLY DEFERRED,CHECK(parent_id IS NULL OR parent_id<>id))",
                    "CREATE INDEX ix_groups_parent ON groups(parent_id,id)",
                    "CREATE TABLE entries(id INTEGER PRIMARY KEY AUTOINCREMENT,key TEXT NOT NULL UNIQUE)",
                    "CREATE TABLE records(id INTEGER PRIMARY KEY AUTOINCREMENT,record_key TEXT NOT NULL UNIQUE,section TEXT NOT NULL,item_id INTEGER REFERENCES entries(id) DEFERRABLE INITIALLY DEFERRED,group_id INTEGER REFERENCES groups(id) DEFERRABLE INITIALLY DEFERRED,ordinal INTEGER NOT NULL,slot TEXT NOT NULL,payload TEXT NOT NULL)",
                    "CREATE INDEX ix_records_group ON records(section,group_id,ordinal,item_id)",
                    "CREATE INDEX ix_records_item ON records(item_id,section,group_id)",
                    "CREATE UNIQUE INDEX ux_primary_category ON records(item_id) WHERE section='CategoryOverrides'",
                    "CREATE TABLE added_items(id INTEGER PRIMARY KEY AUTOINCREMENT,item_id INTEGER NOT NULL REFERENCES entries(id),group_id INTEGER REFERENCES groups(id),path TEXT NOT NULL CHECK(length(path)>0),path_key TEXT NOT NULL,name TEXT NOT NULL,folder INTEGER NOT NULL CHECK(folder IN(0,1)),resource INTEGER NOT NULL CHECK(resource IN(0,1)))",
                    "CREATE UNIQUE INDEX ux_added_membership ON added_items(item_id,ifnull(group_id,0))",
                    "CREATE INDEX ix_added_path ON added_items(path_key,group_id)",
                    "CREATE INDEX ix_added_group ON added_items(group_id,item_id)",
                    "CREATE INDEX ix_added_item ON added_items(item_id,id)",
                    "CREATE INDEX ix_added_software ON added_items(id) WHERE resource=0",
                    "CREATE TABLE changes(id INTEGER PRIMARY KEY AUTOINCREMENT,operation_id INTEGER NOT NULL REFERENCES operations(id),entity TEXT NOT NULL,row_id INTEGER NOT NULL,undo_sql TEXT NOT NULL,redo_sql TEXT NOT NULL)",
                    "CREATE INDEX ix_changes_operation ON changes(operation_id,id)",
                    "CREATE INDEX ix_changes_entity ON changes(entity,row_id,id)",
                    // 扫描目录与列表均为可重建投影，不把扫描噪音写进业务操作日志。
                    "CREATE TABLE catalog(item_id INTEGER PRIMARY KEY REFERENCES entries(id),name TEXT NOT NULL,search_name TEXT NOT NULL,legacy_key TEXT NOT NULL,base_group INTEGER REFERENCES groups(id),kind INTEGER NOT NULL CHECK(kind IN(0,1,2)),hidden INTEGER NOT NULL,active INTEGER NOT NULL,metadata TEXT NOT NULL,size INTEGER NOT NULL DEFAULT -1,modified INTEGER NOT NULL DEFAULT -1,indexed INTEGER NOT NULL DEFAULT 0)",
                    "CREATE INDEX ix_catalog_kind ON catalog(active,kind,hidden,search_name,item_id)",
                    "CREATE INDEX ix_catalog_legacy ON catalog(legacy_key,item_id)",
                    "CREATE INDEX ix_catalog_pending ON catalog(item_id) WHERE indexed=0",
                    "CREATE TABLE listing(group_id INTEGER NOT NULL REFERENCES groups(id),item_id INTEGER NOT NULL REFERENCES entries(id),kind INTEGER NOT NULL,hidden INTEGER NOT NULL,rank INTEGER NOT NULL,name TEXT NOT NULL,size INTEGER NOT NULL DEFAULT -1,modified INTEGER NOT NULL DEFAULT -1,PRIMARY KEY(group_id,item_id)) WITHOUT ROWID",
                    "CREATE INDEX ix_listing_page ON listing(group_id,kind,hidden,rank,name,item_id)",
                    "CREATE INDEX ix_listing_page_all ON listing(group_id,kind,rank,name,item_id)",
                    "CREATE INDEX ix_listing_name ON listing(group_id,kind,hidden,name,item_id)",
                    "CREATE INDEX ix_listing_name_all ON listing(group_id,kind,name,item_id)",
                    "CREATE INDEX ix_listing_size ON listing(group_id,kind,hidden,size,name,item_id)",
                    "CREATE INDEX ix_listing_size_all ON listing(group_id,kind,size,name,item_id)",
                    "CREATE INDEX ix_listing_modified ON listing(group_id,kind,hidden,modified,name,item_id)",
                    "CREATE INDEX ix_listing_modified_all ON listing(group_id,kind,modified,name,item_id)",
                    "CREATE INDEX ix_listing_item ON listing(item_id,group_id)"
                };
                foreach (string sql in ddl) db.Execute(sql);
                Audit(db, "groups", new string[] { "id", "key", "name", "parent_id" });
                Audit(db, "entries", new string[] { "id", "key" });
                Audit(db, "records", new string[] { "id", "record_key", "section", "item_id", "group_id", "ordinal", "slot", "payload" });
                Audit(db, "added_items", new string[] { "id", "item_id", "group_id", "path", "path_key", "name", "folder", "resource" });
                db.Execute("PRAGMA application_id=" + ApplicationId);
                db.Execute("PRAGMA user_version=" + SchemaVersion);
                db.Execute("COMMIT");
            }
            catch { db.Execute("ROLLBACK"); throw; }
        }

        private static void Audit(Sqlite db, string table, string[] columns)
        {
            string names = string.Join(",", columns);
            string[] events = { "INSERT", "UPDATE", "DELETE" };
            foreach (string ev in events)
            {
                string undo, redo;
                string oldInsert = RowInsert(table, names, columns, "OLD");
                string newInsert = RowInsert(table, names, columns, "NEW");
                string oldDelete = "'DELETE FROM " + table + " WHERE id='||quote(OLD.id)";
                string newDelete = "'DELETE FROM " + table + " WHERE id='||quote(NEW.id)";
                if (ev == "INSERT") { undo = newDelete; redo = newInsert; }
                else if (ev == "DELETE") { undo = oldInsert; redo = oldDelete; }
                else { undo = RowUpdate(table, columns, "OLD"); redo = RowUpdate(table, columns, "NEW"); }
                db.Execute("CREATE TRIGGER audit_" + table + "_" + ev + " AFTER " + ev + " ON " + table +
                    " BEGIN SELECT CASE WHEN (SELECT operation_id FROM write_context WHERE id=1) IS NULL THEN RAISE(ABORT,'missing operation context') END;" +
                    "INSERT INTO changes(operation_id,entity,row_id,undo_sql,redo_sql) SELECT operation_id,'" + table + "'," +
                    (ev == "DELETE" ? "OLD" : "NEW") + ".id," + undo + "," + redo + " FROM write_context WHERE id=1; END");
            }
        }
        private static string RowInsert(string table, string names, string[] columns, string row)
        {
            List<string> values = new List<string>();
            foreach (string column in columns) values.Add("quote(" + row + "." + column + ")");
            return "'INSERT INTO " + table + "(" + names + ") VALUES('||" + string.Join("||','||", values.ToArray()) + "||')'";
        }
        private static string RowUpdate(string table, string[] columns, string row)
        {
            List<string> sets = new List<string>();
            foreach (string column in columns) if (column != "id") sets.Add("'" + column + "='||quote(" + row + "." + column + ")");
            return "'UPDATE " + table + " SET '||" + string.Join("||','||", sets.ToArray()) + "||' WHERE id='||quote(" + row + ".id)";
        }

        private void LoadBaseline(Sqlite db)
        {
            revision = Convert.ToInt64(db.Scalar("SELECT value FROM metadata WHERE key='revision'"));
            groups.Clear(); entries.Clear();
            foreach (string[] r in db.Query("SELECT key,id FROM groups")) groups[r[0]] = Convert.ToInt64(r[1]);
            baseline = new Dictionary<string, Record>(StringComparer.Ordinal);
            using (Sqlite.Statement rows = db.Prepare("SELECT record_key,section,item_id,group_id,ordinal,slot,payload FROM records"))
            {
                rows.Bind();
                while (rows.Read())
                {
                    string[] r = rows.Row();
                    Record value = new Record { Key = r[0], Section = r[1], Item = Number(r[2]), Group = Number(r[3]), Ordinal = Number(r[4]), Slot = r[5], Payload = r[6] };
                    baseline.Add(value.Key, value);
                }
            }
        }
        private static long Number(string value) { return string.IsNullOrEmpty(value) ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture); }
        private static object Foreign(long value) { return value == 0 ? null : (object)value; }
        private static long NewOperation(Sqlite db, string action, string summary = "", long reverts = 0)
        {
            db.Execute("INSERT INTO operations(utc,action,summary,reverts,actor,session,pid) VALUES(?,?,?,?,?,?,?)", DateTime.UtcNow.ToString("o"), action, summary,
                Foreign(reverts), Environment.UserName, Session, System.Diagnostics.Process.GetCurrentProcess().Id);
            return db.LastId;
        }

        private long Group(Sqlite db, string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            long id;
            if (groups.TryGetValue(key, out id)) return id;
            long parent = 0;
            string name = key;
            if (CollectionKeys.IsCollectionKey(key))
            {
                int slash = key.IndexOf('/', 4);
                if (slash > 4) { parent = Group(db, key.Substring(4, slash - 4)); name = key.Substring(slash + 1); }
            }
            db.Execute("INSERT INTO groups(key,name,parent_id) VALUES(?,?,?)", key, name, Foreign(parent));
            id = db.LastId; groups[key] = id; return id;
        }
        private long Entry(Sqlite db, string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            long id;
            if (entries.TryGetValue(key, out id)) return id;
            string existing = db.Scalar("SELECT id FROM entries WHERE key=?", key);
            if (existing != null) { id = Number(existing); if (entries.Count >= 4096) entries.Clear(); entries[key] = id; return id; }
            db.Execute("INSERT INTO entries(key) VALUES(?)", key);
            id = db.LastId; if (entries.Count >= 4096) entries.Clear(); entries[key] = id; return id;
        }
        internal void QueueRename(string oldKey, string newKey) { lock (gate) { if (oldKey != newKey) renames.Add(new string[] { oldKey, newKey }); } }
        internal void QueueEntryRename(string oldKey, string newKey) { lock (gate) { if (oldKey != newKey) entryRenames.Add(new string[] { oldKey, newKey }); } }

        private void ApplyRenames(Sqlite db)
        {
            foreach (string[] rename in entryRenames)
            {
                string id = db.Scalar("SELECT id FROM entries WHERE key=?", rename[0]);
                if (id == null) continue;
                db.Execute("UPDATE entries SET key=? WHERE id=?", rename[1], Number(id));
                entries.Remove(rename[0]); entries[rename[1]] = Number(id);
            }
            foreach (string[] rename in renames)
            {
                long id;
                if (!groups.TryGetValue(rename[0], out id)) continue;
                if (groups.ContainsKey(rename[1])) throw new IOException("目标分类标识已存在，不能合并不同分类。");
                int slash = CollectionKeys.IsCollectionKey(rename[1]) ? rename[1].IndexOf('/', 4) : -1;
                string displayName = slash < 0 ? rename[1] : rename[1].Substring(slash + 1);
                db.Execute("UPDATE groups SET key=?,name=? WHERE id=?", rename[1], displayName, id);
                groups.Remove(rename[0]); groups.Add(rename[1], id);
                foreach (string[] child in db.Query("SELECT id,key,name FROM groups WHERE parent_id=?", id))
                {
                    string key = CollectionKeys.Make(rename[1], child[2]);
                    db.Execute("UPDATE groups SET key=? WHERE id=?", key, Number(child[0]));
                    groups.Remove(child[1]); groups[key] = Number(child[0]);
                }
            }
        }

        internal bool Save(AppConfig config, string action)
        {
            lock (gate)
            {
                try
                {
                    using (Sqlite db = Open())
                    {
                        db.Execute("BEGIN IMMEDIATE");
                        try
                        {
                            if ((config.Database == this && config.DatabaseRevision != revision) || Number(db.Scalar("SELECT value FROM metadata WHERE key='revision'")) != revision)
                                throw new IOException("数据库已被其他操作更新，请重新加载后再修改，未覆盖较新数据。");
                            long operation = NewOperation(db, action);
                            db.Execute("UPDATE write_context SET operation_id=? WHERE id=1", operation);
                            ApplyRenames(db);
                            Dictionary<string, Record> next = Encode(db, config.ToDocument(false));
                            next.Remove("Settings:0:0:AddedItems");
                            HashSet<long> affected = new HashSet<long>();
                            int inserted = 0, updated = 0, deleted = 0;
                            using (Sqlite.Statement insert = db.Prepare("INSERT INTO records(record_key,section,item_id,group_id,ordinal,slot,payload) VALUES(?,?,?,?,?,?,?)"))
                            using (Sqlite.Statement update = db.Prepare("UPDATE records SET ordinal=?,payload=? WHERE record_key=?"))
                            using (Sqlite.Statement remove = db.Prepare("DELETE FROM records WHERE record_key=?"))
                            {
                                foreach (Record r in next.Values)
                                {
                                    Record before;
                                    if (!baseline.TryGetValue(r.Key, out before))
                                    {
                                        insert.Bind(r.Key, r.Section, Foreign(r.Item), Foreign(r.Group), r.Ordinal, r.Slot, r.Payload); insert.Read(); inserted++;
                                    }
                                    else if (!r.Same(before)) { update.Bind(r.Ordinal, r.Payload, r.Key); update.Read(); updated++; }
                                    else continue;
                                    if (r.Item != 0) affected.Add(r.Item);
                                }
                                foreach (Record r in baseline.Values) if (!next.ContainsKey(r.Key))
                                {
                                    remove.Bind(r.Key); remove.Read(); deleted++;
                                    if (r.Item != 0) affected.Add(r.Item);
                                }
                            }
                            SaveAdded(db, config, affected);
                            UpdateAddedCatalog(db, affected);
                            ExpandLegacyAffected(db, affected);
                            RebuildListings(db, config, affected);
                            int changes = Convert.ToInt32(db.Scalar("SELECT count(*) FROM changes WHERE operation_id=?", operation));
                            db.Execute("UPDATE write_context SET operation_id=NULL WHERE id=1");
                            if (changes == 0)
                                db.Execute("DELETE FROM operations WHERE id=?", operation);
                            else
                                db.Execute("UPDATE operations SET summary=? WHERE id=?", "+" + inserted + " ~" + updated + " -" + deleted + "; rows=" + changes, operation);
                            long newRevision = changes == 0 ? revision : revision + 1;
                            db.Execute("UPDATE metadata SET value=? WHERE key='revision'", newRevision.ToString(CultureInfo.InvariantCulture));
                            db.Execute("INSERT OR REPLACE INTO metadata(key,value) VALUES('migrated','1')");
                            int addedCount = Convert.ToInt32(db.Scalar("SELECT count(*) FROM added_items"));
                            db.Execute("COMMIT");
                            revision = newRevision; baseline = next; renames.Clear(); entryRenames.Clear();
                            config.Database = this; config.DatabaseRevision = revision; config.DatabaseError = null;
                            if (config.AddedItems.Store == this) config.AddedItems.Accept(addedCount);
                            else config.AddedItems = new AddedEntryList(this, addedCount);
                            return true;
                        }
                        catch
                        {
                            db.Rollback();
                            foreach (AddedEntry entry in config.AddedItems.New) { entry.RecordId = 0; entry.ItemId = 0; }
                            LoadBaseline(db); throw;
                        }
                    }
                }
                catch (Exception ex)
                {
                    config.DatabaseError = ex.Message;
                    Paths.LogError(new IOException("数据库保存失败，事务已撤销。 action=" + action + "; revision=" + revision, ex));
                    return false;
                }
            }
        }

        private Dictionary<string, Record> Encode(Sqlite db, XmlDocument doc)
        {
            Dictionary<string, Record> result = new Dictionary<string, Record>(baseline.Count + 32, StringComparer.Ordinal);
            foreach (XmlElement section in doc.DocumentElement.ChildNodes)
            {
                if (!section.HasChildNodes || section.FirstChild is XmlText)
                {
                    AddRecord(result, "Settings", 0, 0, 0, section.Name, section.OuterXml);
                    continue;
                }
                int ordinal = 0;
                foreach (XmlElement original in section.ChildNodes)
                {
                    XmlElement node = (XmlElement)original.CloneNode(true);
                    string name = section.Name;
                    long item = 0, group = 0;
                    if (name == "ItemCategories" || name == "ExcludedCategories")
                    {
                        item = Entry(db, node.GetAttribute("key"));
                        foreach (XmlElement category in node.ChildNodes)
                            AddRecord(result, name, item, Group(db, category.InnerText), 0, "", "<Item />");
                        continue;
                    }
                    if (name == "ItemOrders")
                    {
                        group = Group(db, node.GetAttribute("cat")); int rank = 0;
                        foreach (XmlElement key in node.ChildNodes)
                            AddRecord(result, name, Entry(db, key.InnerText), group, rank++, "", "<Item />");
                        continue;
                    }
                    if (name == "Favorites" || name == "Hidden") { item = Entry(db, node.InnerText); node.InnerText = ""; }
                    else if (name == "CustomCategories" || name == "ResourceCategories" || name == "SidebarOrder")
                    { group = Group(db, node.InnerText); node.InnerText = ""; }
                    else if (name == "Collections")
                    { group = Group(db, CollectionKeys.Make(node.GetAttribute("parent"), node.GetAttribute("name"))); node.RemoveAttribute("parent"); node.RemoveAttribute("name"); }
                    else if (name == "CategoryOverrides")
                    { item = Entry(db, node.GetAttribute("key")); group = Group(db, node.InnerText); node.RemoveAttribute("key"); node.InnerText = ""; }
                    else if (name == "CategoryViewModes" || name == "CategoryIconNames")
                    { group = Group(db, node.GetAttribute("cat")); node.RemoveAttribute("cat"); }
                    else if (name == "AddedItems")
                    { item = Entry(db, "add:" + node.GetAttribute("path")); group = Group(db, node.GetAttribute("category")); node.RemoveAttribute("category"); }
                    string slot = name == "DesktopBackup" ? node.GetAttribute("path") + "\n" + node.GetAttribute("current") : "";
                    AddRecord(result, name, item, group,
                        name == "SidebarOrder" || name == "CustomCategories" || name == "ResourceCategories" ? ordinal : 0, slot, node.OuterXml);
                    ordinal++;
                }
            }
            return result;
        }
        private static void AddRecord(Dictionary<string, Record> records, string section, long item, long group, long ordinal, string slot, string payload)
        {
            string key = section + ":" + item + ":" + group + ":" + slot;
            records[key] = new Record { Key = key, Section = section, Item = item, Group = group, Ordinal = ordinal, Slot = slot, Payload = payload };
        }
        internal AppConfig Read()
        {
            lock (gate)
            using (Sqlite db = Open())
            {
                XmlDocument doc = new XmlDocument(); doc.LoadXml("<AppManagerConfig />");
                Dictionary<string, XmlElement> sections = new Dictionary<string, XmlElement>();
                Dictionary<string, XmlElement> parents = new Dictionary<string, XmlElement>();
                using (Sqlite.Statement rows = db.Prepare("SELECT r.section,e.key,g.key,r.ordinal,r.slot,r.payload,g.name,p.key,r.record_key FROM records r LEFT JOIN entries e ON e.id=r.item_id LEFT JOIN groups g ON g.id=r.group_id LEFT JOIN groups p ON p.id=g.parent_id ORDER BY r.section,r.ordinal,r.id"))
                {
                  rows.Bind();
                  while (rows.Read())
                  {
                    string[] r = rows.Row();
                    string section = r[0];
                    if (section == "Settings") { AppendFragment(doc, doc.DocumentElement, r[5]); continue; }
                    XmlElement target;
                    if (!sections.TryGetValue(section, out target)) { target = doc.CreateElement(section); doc.DocumentElement.AppendChild(target); sections[section] = target; }
                    if (section == "ItemCategories" || section == "ExcludedCategories" || section == "ItemOrders")
                    {
                        string identity = section + ":" + (section == "ItemOrders" ? r[2] : r[1]);
                        XmlElement parent;
                        if (!parents.TryGetValue(identity, out parent))
                        { parent = doc.CreateElement("Item"); parent.SetAttribute(section == "ItemOrders" ? "cat" : "key", section == "ItemOrders" ? r[2] : r[1]); target.AppendChild(parent); parents[identity] = parent; }
                        XmlElement child = doc.CreateElement(section == "ItemOrders" ? "Key" : "Category");
                        child.InnerText = section == "ItemOrders" ? r[1] : r[2]; parent.AppendChild(child); continue;
                    }
                    XmlElement node = AppendFragment(doc, target, r[5]);
                    if (section == "Favorites" || section == "Hidden") node.InnerText = r[1];
                    else if (section == "CustomCategories" || section == "ResourceCategories" || section == "SidebarOrder") node.InnerText = r[2];
                    else if (section == "Collections") { node.SetAttribute("name", r[6]); node.SetAttribute("parent", r[7]); }
                    else if (section == "CategoryOverrides") { node.SetAttribute("key", r[1]); node.InnerText = r[2]; }
                    else if (section == "CategoryViewModes" || section == "CategoryIconNames") node.SetAttribute("cat", r[2]);
                    else if (section == "AddedItems") node.SetAttribute("category", r[2] ?? "");
                  }
                }
                AppConfig result = AppConfig.ReadDocument(doc);
                result.AddedItems = new AddedEntryList(this);
                if (result.LoadFailed) throw new IOException("数据库内容解析失败，已禁止覆盖。");
                result.Database = this;
                result.DatabaseRevision = revision;
                return result;
            }
        }
        private static XmlElement AppendFragment(XmlDocument doc, XmlElement parent, string xml)
        {
            XmlDocument fragment = new XmlDocument(); fragment.XmlResolver = null; fragment.LoadXml(xml);
            XmlElement node = (XmlElement)doc.ImportNode(fragment.DocumentElement, true); parent.AppendChild(node); return node;
        }

        internal void Backup(string path) { lock (gate) using (Sqlite db = Open()) db.Backup(path); }
        internal void NoteRecovery(string safety)
        {
            lock (gate) using (Sqlite db = Open())
            {
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    NewOperation(db, "restore.snapshot", "safety=" + Path.GetFileName(safety));
                    db.Execute("UPDATE metadata SET value=CAST(value AS INTEGER)+1 WHERE key='revision'");
                    db.Execute("COMMIT"); LoadBaseline(db);
                }
                catch { db.Rollback(); throw; }
            }
        }
        internal string Restore(string path)
        {
            lock (gate)
            using (Sqlite db = Open())
            {
                if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase)) throw new IOException("恢复来源不能是当前数据库。");
                string safety = FilePath + ".before-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".db";
                db.Backup(safety);
                db.Restore(path);
                try
                {
                    long restoredRevision = Number(db.Scalar("SELECT value FROM metadata WHERE key='revision'"));
                    long nextRevision = Math.Max(revision, restoredRevision) + 1;
                    db.Execute("BEGIN IMMEDIATE");
                    NewOperation(db, "restore.snapshot", "safety=" + Path.GetFileName(safety));
                    db.Execute("UPDATE metadata SET value=? WHERE key='revision'", nextRevision.ToString(CultureInfo.InvariantCulture));
                    db.Execute("COMMIT");
                    LoadBaseline(db); renames.Clear(); entryRenames.Clear();
                }
                catch
                {
                    db.Rollback(); db.Restore(safety); LoadBaseline(db); throw;
                }
                return safety;
            }
        }
        internal List<string[]> History(int limit)
        {
            if (limit < 1 || limit > 1000) throw new ArgumentOutOfRangeException("limit");
            using (Sqlite db = Open()) return db.Query("SELECT id,utc,action,summary,reverts,actor,session,pid FROM operations ORDER BY id DESC LIMIT ?", limit);
        }
        internal string ChangeDetails(long operation)
        {
            using (Sqlite db = Open())
            {
                StringBuilder text = new StringBuilder();
                foreach (string[] row in db.Query("SELECT id,entity,row_id,undo_sql,redo_sql FROM changes WHERE operation_id=? ORDER BY id LIMIT 100", operation))
                { text.AppendLine(row[0] + " " + row[1] + " #" + row[2]); text.AppendLine("BEFORE: " + row[3]); text.AppendLine("AFTER: " + row[4]); text.AppendLine(); }
                return text.ToString();
            }
        }
        internal void ExportChanges(long operation, string path)
        {
            string target = Path.GetFullPath(path);
            if (string.Equals(target, Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target, Path.GetFullPath(FilePath) + "-wal", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target, Path.GetFullPath(FilePath) + "-shm", StringComparison.OrdinalIgnoreCase)) throw new IOException("导出目标不能覆盖当前数据库。");
            using (Sqlite db = Open())
            using (Sqlite.Statement rows = db.Prepare("SELECT id,entity,row_id,undo_sql,redo_sql FROM changes WHERE operation_id=? ORDER BY id"))
            using (XmlWriter writer = XmlWriter.Create(path, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
            {
                writer.WriteStartElement("Operation"); writer.WriteAttributeString("id", operation.ToString()); rows.Bind(operation);
                while (rows.Read())
                {
                    string[] row = rows.Row(); writer.WriteStartElement("Change"); writer.WriteAttributeString("id", row[0]);
                    writer.WriteAttributeString("entity", row[1]); writer.WriteAttributeString("row", row[2]);
                    writer.WriteElementString("Undo", row[3]); writer.WriteElementString("Redo", row[4]); writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
        }
        // 仅撤销最新一次业务事务，避免逆向覆盖后续修改。撤销自身也会产生完整日志。
        internal void Undo(long operation)
        {
            lock (gate)
            using (Sqlite db = Open())
            {
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    if (Number(db.Scalar("SELECT max(id) FROM operations WHERE action NOT IN('catalog.discover','catalog.rebuild')")) != operation) throw new IOException("只允许撤销最新业务事务；请先检查操作记录。");
                    string action = db.Scalar("SELECT action FROM operations WHERE id=?", operation);
                    if (action == "migration.xml" || action == "restore.snapshot") throw new IOException("初始化或整库恢复请使用完整备份恢复，不能逐项撤销。");
                    long undo = NewOperation(db, "rollback", "", operation);
                    db.Execute("UPDATE write_context SET operation_id=? WHERE id=1", undo);
                    db.Execute("DELETE FROM listing"); db.Execute("DELETE FROM catalog");
                    List<string[]> changes = db.Query("SELECT entity,undo_sql FROM changes WHERE operation_id=? ORDER BY id DESC", operation);
                    foreach (string[] change in changes)
                    {
                        // 标识注册表保留历史 ID；撤销成员关系和名称，避免影响稍后扫描登记的条目。
                        if ((change[0] == "entries" || change[0] == "groups") && change[1].StartsWith("DELETE FROM ", StringComparison.Ordinal)) continue;
                        db.Execute(change[1]);
                    }
                    // 外键对象也可能被撤销，先清空可重建投影。
                    db.Execute("INSERT OR REPLACE INTO metadata(key,value) VALUES('catalog_invalid','1')");
                    db.Execute("UPDATE operations SET summary=? WHERE id=?", "undo " + operation + "; rows=" + changes.Count, undo);
                    db.Execute("UPDATE write_context SET operation_id=NULL WHERE id=1");
                    db.Execute("UPDATE metadata SET value=CAST(value AS INTEGER)+1 WHERE key='revision'");
                    db.Execute("COMMIT");
                    LoadBaseline(db); renames.Clear();
                }
                catch { db.Execute("ROLLBACK"); throw; }
            }
        }
    }
}
