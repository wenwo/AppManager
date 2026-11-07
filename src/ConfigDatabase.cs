using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace AppManager
{
    /// <summary>数据库版本闸门：新版读取并升级 v1/v2，旧版不得打开更高版本。</summary>
    internal static class DatabaseCompatibility
    {
        internal const int ApplicationId = 1095584594;
        internal const int OldestSupportedVersion = 1;
        internal const int CurrentVersion = 3;

        internal static bool IsSupportedVersion(int version)
        {
            return version >= OldestSupportedVersion && version <= CurrentVersion;
        }

        /// <summary>恢复前和恢复后都调用；返回 null 表示可安全继续。</summary>
        internal static string ValidateSnapshot(Sqlite db, int expectedApplicationId)
        {
            int app = Convert.ToInt32(db.Scalar("PRAGMA application_id"), CultureInfo.InvariantCulture);
            int version = Convert.ToInt32(db.Scalar("PRAGMA user_version"), CultureInfo.InvariantCulture);
            if (app != expectedApplicationId) return "备份不是由本程序创建。";
            if (!IsSupportedVersion(version)) return "备份数据库版本不受支持：" + version + "。";
            if (db.Scalar("PRAGMA integrity_check") != "ok") return "备份完整性检查失败。";
            if (db.Query("PRAGMA foreign_key_check").Count != 0) return "备份外键检查失败。";
            return null;
        }
    }

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
        private readonly List<string[]> groupMoves = new List<string[]>();
        private const int ApplicationId = DatabaseCompatibility.ApplicationId;
        private const int SchemaVersion = DatabaseCompatibility.CurrentVersion;
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
                else if (version < SchemaVersion)
                {
                    UpgradeSchema(db, version);
                }
                if (db.Scalar("PRAGMA quick_check") != "ok") throw new IOException("数据库完整性检查失败，请从备份恢复。");
                EnsureIndexes(db);
                LoadBaseline(db);
            }
        }

        /// <summary>版本闸门在事务内推进；后加列和索引由 EnsureIndexes 幂等补齐。</summary>
        private static void UpgradeSchema(Sqlite db, int fromVersion)
        {
            if (fromVersion != 1 && fromVersion != 2)
                throw new IOException("数据库版本无法自动升级：" + fromVersion + "。");
            db.Execute("BEGIN IMMEDIATE");
            try
            {
                db.Execute("PRAGMA user_version=" + SchemaVersion);
                db.Execute("COMMIT");
            }
            catch
            {
                db.Rollback();
                throw;
            }
        }

        /// <summary>
        /// 补齐后加的列和索引（旧库升级、在线恢复的快照都可能缺少）。
        /// add: 路径键按 NOCASE 查找；没有这个索引时每次查找都全表扫描，批量收录会退化成平方级耗时。
        /// </summary>
        private static void EnsureIndexes(Sqlite db)
        {
            EnsureCatalogRetryColumns(db);
            bool hasAddedItems = HasTable(db, "added_items");
            if (hasAddedItems) EnsureAddedOriginColumns(db);
            db.Execute("CREATE INDEX IF NOT EXISTS ix_entries_key_nocase ON entries(key COLLATE NOCASE)");
            if (hasAddedItems) db.Execute("CREATE INDEX IF NOT EXISTS ix_added_origin ON added_items(origin_key,group_id)");
            db.Execute("CREATE INDEX IF NOT EXISTS ix_catalog_retry ON catalog(item_id) WHERE indexed=2");
            db.Execute("CREATE INDEX IF NOT EXISTS ix_catalog_retry_due ON catalog(next_retry,item_id) WHERE indexed=2");
        }

        private static bool HasTable(Sqlite db, string table)
        {
            return Convert.ToInt32(db.Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name=?", table),
                CultureInfo.InvariantCulture) != 0;
        }

        private static void EnsureAddedOriginColumns(Sqlite db)
        {
            if (!HasTable(db, "added_items")) return;
            bool hasOrigin = false, hasOriginKey = false;
            foreach (string[] row in db.Query("PRAGMA table_info(added_items)"))
            {
                if (row.Length < 2) continue;
                if (row[1] == "origin") hasOrigin = true;
                else if (row[1] == "origin_key") hasOriginKey = true;
            }
            if (hasOrigin && hasOriginKey) return;
            db.Execute("BEGIN IMMEDIATE");
            try
            {
                if (!hasOrigin) db.Execute("ALTER TABLE added_items ADD COLUMN origin TEXT");
                if (!hasOriginKey) db.Execute("ALTER TABLE added_items ADD COLUMN origin_key TEXT");
                db.Execute("DROP TRIGGER IF EXISTS audit_added_items_INSERT");
                db.Execute("DROP TRIGGER IF EXISTS audit_added_items_UPDATE");
                db.Execute("DROP TRIGGER IF EXISTS audit_added_items_DELETE");
                Audit(db, "added_items", new string[] { "id", "item_id", "group_id", "path", "path_key", "name", "folder", "resource", "origin", "origin_key" });
                db.Execute("COMMIT");
            }
            catch
            {
                db.Rollback();
                throw;
            }
        }

        private static void EnsureCatalogRetryColumns(Sqlite db)
        {
            bool hasRetryCount = false, hasNextRetry = false;
            foreach (string[] row in db.Query("PRAGMA table_info(catalog)"))
            {
                if (row.Length < 2) continue;
                if (row[1] == "retry_count") hasRetryCount = true;
                else if (row[1] == "next_retry") hasNextRetry = true;
            }
            if (hasRetryCount && hasNextRetry) return;
            db.Execute("BEGIN IMMEDIATE");
            try
            {
                if (!hasRetryCount) db.Execute("ALTER TABLE catalog ADD COLUMN retry_count INTEGER NOT NULL DEFAULT 0");
                if (!hasNextRetry) db.Execute("ALTER TABLE catalog ADD COLUMN next_retry INTEGER NOT NULL DEFAULT 0");
                db.Execute("COMMIT");
            }
            catch
            {
                db.Rollback();
                throw;
            }
        }

        /// <summary>
        /// 一次性合并历史遗留的大小写重复条目：修复前同一路径的大小写变体会各自生成 entries 行，
        /// 收藏、归属、排序分散在两个 ID 上，列表里出现两条。保留收录记录最多（其次最早）的条目，
        /// 其余条目的记录与收录项改指向保留条目；保留条目已有同类记录/归属时删除重复那条（主分类覆盖每条目只留一条）。
        /// 被合并条目的键改为退役键（ID 不回收），之后的 NOCASE 查找只会命中保留条目。
        /// 一个审计事务完成，失败整体回滚、下次加载重试；完成后记标记，之后不再全表扫描
        /// （恢复旧快照时标记随快照回到未合并，会自动重新执行）。返回合并掉的条目数。
        /// </summary>
        internal int MergeCaseDuplicateEntries()
        {
            lock (gate)
            using (Sqlite db = Open())
            {
                if (db.Scalar("SELECT value FROM metadata WHERE key='entries_case_merged'") == "1") return 0;
                List<string[]> heads = db.Query("SELECT min(id) FROM entries WHERE key LIKE 'add:%' GROUP BY key COLLATE NOCASE HAVING count(*) > 1");
                if (heads.Count == 0)
                {
                    db.Execute("INSERT OR REPLACE INTO metadata(key,value) VALUES('entries_case_merged','1')");
                    return 0;
                }
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    long operation = NewOperation(db, "migration.entries.case");
                    db.Execute("UPDATE write_context SET operation_id=? WHERE id=1", operation);
                    HashSet<long> affected = new HashSet<long>();
                    int merged = 0;
                    foreach (string[] head in heads)
                    {
                        List<string[]> ids = db.Query("SELECT e.id FROM entries e WHERE e.key=(SELECT key FROM entries WHERE id=?) COLLATE NOCASE " +
                            "ORDER BY (SELECT count(*) FROM added_items a WHERE a.item_id=e.id) DESC, e.id", Number(head[0]));
                        long keep = Number(ids[0][0]);
                        for (int i = 1; i < ids.Count; i++)
                        {
                            long dup = Number(ids[i][0]);
                            foreach (string[] r in db.Query("SELECT id,section,group_id,slot FROM records WHERE item_id=?", dup))
                            {
                                string key = r[1] + ":" + keep + ":" + Number(r[2]) + ":" + (r[3] ?? string.Empty);
                                bool clash = db.Scalar("SELECT 1 FROM records WHERE record_key=?", key) != null
                                    || (r[1] == "CategoryOverrides" && db.Scalar("SELECT 1 FROM records WHERE section='CategoryOverrides' AND item_id=?", keep) != null);
                                if (clash) db.Execute("DELETE FROM records WHERE id=?", Number(r[0]));
                                else db.Execute("UPDATE records SET item_id=?,record_key=? WHERE id=?", keep, key, Number(r[0]));
                            }
                            foreach (string[] a in db.Query("SELECT id,group_id FROM added_items WHERE item_id=?", dup))
                            {
                                if (db.Scalar("SELECT 1 FROM added_items WHERE item_id=? AND group_id IS ?", keep, Foreign(Number(a[1]))) != null)
                                    db.Execute("DELETE FROM added_items WHERE id=?", Number(a[0]));
                                else db.Execute("UPDATE added_items SET item_id=? WHERE id=?", keep, Number(a[0]));
                            }
                            db.Execute("DELETE FROM listing WHERE item_id=?", dup);
                            db.Execute("DELETE FROM catalog WHERE item_id=?", dup);
                            db.Execute("UPDATE entries SET key=? WHERE id=?", "__merged__:" + dup.ToString(CultureInfo.InvariantCulture)
                                + ":" + keep.ToString(CultureInfo.InvariantCulture), dup);
                            merged++;
                        }
                        affected.Add(keep);
                    }
                    UpdateAddedCatalog(db, affected);
                    RebuildListings(db, null, affected);
                    db.Execute("UPDATE write_context SET operation_id=NULL WHERE id=1");
                    if (db.Scalar("SELECT count(*) FROM changes WHERE operation_id=?", operation) == "0") db.Execute("DELETE FROM operations WHERE id=?", operation);
                    else
                    {
                        db.Execute("UPDATE operations SET summary=? WHERE id=?", "merged case duplicates=" + merged, operation);
                        revision++;
                        db.Execute("UPDATE metadata SET value=? WHERE key='revision'", revision.ToString(CultureInfo.InvariantCulture));
                    }
                    db.Execute("INSERT OR REPLACE INTO metadata(key,value) VALUES('entries_case_merged','1')");
                    db.Execute("COMMIT");
                    LoadBaseline(db);
                    return merged;
                }
                catch
                {
                    db.Rollback();
                    LoadBaseline(db);
                    throw;
                }
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
                // 历史大小写重复条目：合并失败只记日志、不影响加载，下次加载重试
                try { store.MergeCaseDuplicateEntries(); }
                catch (Exception mergeError) { Paths.LogError(new IOException("合并大小写重复条目失败，下次加载会重试。", mergeError)); }
                AppConfig result = store.Read();
                result.Database = store;
                store.EnsureCatalog(result);
                store.RepairRw2TakenDateIndex();
                store.RepairMembershipProjections();
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
                    "CREATE TABLE added_items(id INTEGER PRIMARY KEY AUTOINCREMENT,item_id INTEGER NOT NULL REFERENCES entries(id),group_id INTEGER REFERENCES groups(id),path TEXT NOT NULL CHECK(length(path)>0),path_key TEXT NOT NULL,name TEXT NOT NULL,folder INTEGER NOT NULL CHECK(folder IN(0,1)),resource INTEGER NOT NULL CHECK(resource IN(0,1)),origin TEXT,origin_key TEXT)",
                    "CREATE UNIQUE INDEX ux_added_membership ON added_items(item_id,ifnull(group_id,0))",
                    "CREATE INDEX ix_added_path ON added_items(path_key,group_id)",
                    "CREATE INDEX ix_added_origin ON added_items(origin_key,group_id)",
                    "CREATE INDEX ix_added_group ON added_items(group_id,item_id)",
                    "CREATE INDEX ix_added_item ON added_items(item_id,id)",
                    "CREATE INDEX ix_added_software ON added_items(id) WHERE resource=0",
                    "CREATE TABLE changes(id INTEGER PRIMARY KEY AUTOINCREMENT,operation_id INTEGER NOT NULL REFERENCES operations(id),entity TEXT NOT NULL,row_id INTEGER NOT NULL,undo_sql TEXT NOT NULL,redo_sql TEXT NOT NULL)",
                    "CREATE INDEX ix_changes_operation ON changes(operation_id,id)",
                    "CREATE INDEX ix_changes_entity ON changes(entity,row_id,id)",
                    // 扫描目录与列表均为可重建投影，不把扫描噪音写进业务操作日志。
                    "CREATE TABLE catalog(item_id INTEGER PRIMARY KEY REFERENCES entries(id),name TEXT NOT NULL,search_name TEXT NOT NULL,legacy_key TEXT NOT NULL,base_group INTEGER REFERENCES groups(id),kind INTEGER NOT NULL CHECK(kind IN(0,1,2)),hidden INTEGER NOT NULL,active INTEGER NOT NULL,metadata TEXT NOT NULL,size INTEGER NOT NULL DEFAULT -1,modified INTEGER NOT NULL DEFAULT -1,indexed INTEGER NOT NULL DEFAULT 0,retry_count INTEGER NOT NULL DEFAULT 0,next_retry INTEGER NOT NULL DEFAULT 0)",
                    "CREATE INDEX ix_catalog_kind ON catalog(active,kind,hidden,search_name,item_id)",
                    "CREATE INDEX ix_catalog_legacy ON catalog(legacy_key,item_id)",
                    "CREATE INDEX ix_catalog_pending ON catalog(item_id) WHERE indexed=0",
                    "CREATE INDEX ix_catalog_retry ON catalog(item_id) WHERE indexed=2",
                    "CREATE INDEX ix_catalog_retry_due ON catalog(next_retry,item_id) WHERE indexed=2",
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
                Audit(db, "added_items", new string[] { "id", "item_id", "group_id", "path", "path_key", "name", "folder", "resource", "origin", "origin_key" });
                db.Execute("PRAGMA application_id=" + ApplicationId);
                db.Execute("PRAGMA user_version=" + SchemaVersion);
                db.Execute("COMMIT");
            }
            catch { db.Rollback(); throw; }
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
            string root, parentCollection, parsedName;
            if (CollectionKeys.TryParse(key, out root, out parentCollection, out parsedName))
            {
                name = parsedName;
                string parentKey = string.IsNullOrEmpty(parentCollection) ? root : parentCollection;
                if (!string.IsNullOrEmpty(parentKey)) parent = Group(db, parentKey);
            }
            db.Execute("INSERT INTO groups(key,name,parent_id) VALUES(?,?,?)", key, name, Foreign(parent));
            // audit triggers also insert rows and change last_insert_rowid();
            // resolve the real identity by its unique key instead.
            id = Number(db.Scalar("SELECT id FROM groups WHERE key=?", key)); groups[key] = id; return id;
        }
        private long Entry(Sqlite db, string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            long id;
            if (entries.TryGetValue(key, out id)) return id;
            // 路径键按 Windows 语义不区分大小写：同一路径的大小写变体必须复用同一条目，
            // 否则会生成两个项目并破坏集合/分类关系。
            string existing = key.StartsWith("add:", StringComparison.OrdinalIgnoreCase)
                ? db.Scalar("SELECT id FROM entries WHERE key=? COLLATE NOCASE LIMIT 1", key)
                : db.Scalar("SELECT id FROM entries WHERE key=?", key);
            if (existing != null) { id = Number(existing); if (entries.Count >= 4096) entries.Clear(); entries[key] = id; return id; }
            db.Execute("INSERT INTO entries(key) VALUES(?)", key);
            id = Number(db.Scalar("SELECT id FROM entries WHERE key=?", key)); if (entries.Count >= 4096) entries.Clear(); entries[key] = id; return id;
        }
        internal void QueueRename(string oldKey, string newKey) { lock (gate) { if (oldKey != newKey) renames.Add(new string[] { oldKey, newKey }); } }
        internal void QueueCollectionMove(string oldKey, string newKey, string newParentKey)
        {
            lock (gate)
            {
                if (oldKey != newKey)
                    groupMoves.Add(new string[] { oldKey, newKey, newParentKey ?? string.Empty });
            }
        }
        internal void QueueEntryRename(string oldKey, string newKey) { lock (gate) { if (oldKey != newKey) entryRenames.Add(new string[] { oldKey, newKey }); } }

        private void ApplyRenames(Sqlite db)
        {
            foreach (string[] rename in entryRenames)
            {
                string id = db.Scalar("SELECT id FROM entries WHERE key=?", rename[0]);
                if (id == null) continue;
                // entries 行不回收：新路径曾登记过时直接改名会违反唯一约束，使本次及之后的保存全部失败。
                // 跳过改名即可，记录与收录项按新键差量写入（复用已有行），结果与未排队改名一致。
                string target = rename[1].StartsWith("add:", StringComparison.OrdinalIgnoreCase)
                    ? db.Scalar("SELECT id FROM entries WHERE key=? COLLATE NOCASE AND id<>? LIMIT 1", rename[1], Number(id))
                    : db.Scalar("SELECT id FROM entries WHERE key=? AND id<>?", rename[1], Number(id));
                if (target != null) continue;
                db.Execute("UPDATE entries SET key=? WHERE id=?", rename[1], Number(id));
                entries.Remove(rename[0]); entries[rename[1]] = Number(id);
            }
            foreach (string[] rename in renames)
                ApplyGroupRename(db, rename[0], rename[1]);
            foreach (string[] move in groupMoves)
                ApplyGroupRename(db, move[0], move[1], move[2]);
        }

        /// <summary>
        /// 一次改名迁移整棵分组子树。按 parent_id 读取真实层级并从新根重建完整键，
        /// 避免嵌套键只改一层，或把完整旧键再次传给 CollectionKeys.Make。
        /// </summary>
        private void ApplyGroupRename(Sqlite db, string oldKey, string newKey, string newParentKey = null)
        {
            long rootId;
            if (!groups.TryGetValue(oldKey, out rootId)) return;
            List<string[]> rows = db.Query(
                "WITH RECURSIVE subtree(id,key,name,parent_id) AS (" +
                "SELECT id,key,name,parent_id FROM groups WHERE id=? " +
                "UNION SELECT g.id,g.key,g.name,g.parent_id FROM groups g JOIN subtree s ON g.parent_id=s.id) " +
                "SELECT id,key,name,parent_id FROM subtree", rootId);
            if (rows.Count == 0) return;
            if (rows.Count > CollectionKeys.MaxCollections)
                throw new IOException("集合子树超过数量上限（最多 " + CollectionKeys.MaxCollections + " 个）。");

            Dictionary<long, string[]> byId = new Dictionary<long, string[]>();
            Dictionary<long, List<long>> children = new Dictionary<long, List<long>>();
            for (int i = 0; i < rows.Count; i++)
            {
                long id = Number(rows[i][0]), parentId = Number(rows[i][3]);
                byId[id] = rows[i];
                if (parentId == 0) continue;
                List<long> list;
                if (!children.TryGetValue(parentId, out list)) { list = new List<long>(); children[parentId] = list; }
                list.Add(id);
            }
            if (!byId.ContainsKey(rootId)) return;

            Dictionary<long, string> newKeys = new Dictionary<long, string>();
            Dictionary<string, long> targetOwners = new Dictionary<string, long>(StringComparer.Ordinal);
            Queue<long> pending = new Queue<long>();
            newKeys[rootId] = newKey;
            pending.Enqueue(rootId);
            while (pending.Count > 0)
            {
                long parentId = pending.Dequeue();
                List<long> list;
                if (!children.TryGetValue(parentId, out list)) continue;
                string parentKey = newKeys[parentId];
                for (int i = 0; i < list.Count; i++)
                {
                    long id = list[i];
                    string name = byId[id][2];
                    if (string.IsNullOrEmpty(name)) throw new IOException("分组树中存在空名称，不能安全改名。");
                    string key = CollectionKeys.GetChildKey(parentKey, name);
                    newKeys[id] = key;
                    pending.Enqueue(id);
                }
            }
            foreach (KeyValuePair<long, string> entry in newKeys)
            {
                if (CollectionKeys.IsCollectionKey(entry.Value))
                {
                    if (entry.Value.Length > CollectionKeys.MaxKeyLength)
                        throw new IOException("集合路径过长（最多 " + CollectionKeys.MaxKeyLength + " 个字符）：" + entry.Value);
                    int depth = CollectionKeys.Depth(entry.Value);
                    if (depth <= 0 || depth > CollectionKeys.MaxDepth)
                        throw new IOException("集合嵌套层级超过上限（最多 " + CollectionKeys.MaxDepth + " 层）：" + entry.Value);
                }
                if (targetOwners.ContainsKey(entry.Value))
                    throw new IOException("改名目标路径重复：" + entry.Value);
                targetOwners[entry.Value] = entry.Key;
            }

            List<long> retired = new List<long>();
            foreach (KeyValuePair<string, long> entry in targetOwners)
            {
                long existingId;
                if (!groups.TryGetValue(entry.Key, out existingId) || existingId == entry.Value) continue;
                if (newKeys.ContainsKey(existingId))
                    throw new IOException("改名目标与子树内其它分组冲突：" + entry.Key);
                if (GroupHasReferences(db, existingId))
                    throw new IOException("目标分类名称已被其它分类或记录使用，不能合并不同分类。");
                retired.Add(existingId);
            }
            HashSet<long> retiredSet = new HashSet<long>();
            for (int i = 0; i < retired.Count; i++)
            {
                long targetId = retired[i];
                if (!retiredSet.Add(targetId)) continue;
                string targetKey = db.Scalar("SELECT key FROM groups WHERE id=?", targetId);
                if (targetKey == null) continue;
                string retiredKey = RetiredGroupKey(db, targetId);
                db.Execute("UPDATE groups SET key=? WHERE id=?", retiredKey, targetId);
                groups.Remove(targetKey); groups[retiredKey] = targetId;
            }

            string root, parentCollection, displayName;
            if (!CollectionKeys.TryParse(newKey, out root, out parentCollection, out displayName))
                displayName = newKey;
            foreach (KeyValuePair<long, string> entry in newKeys)
            {
                string previous = byId[entry.Key][1];
                db.Execute("UPDATE groups SET key=? WHERE id=?", entry.Value, entry.Key);
                groups.Remove(previous); groups[entry.Value] = entry.Key;
            }
            db.Execute("UPDATE groups SET name=? WHERE id=?", displayName, rootId);
            if (newParentKey != null)
            {
                long parentId = 0;
                if (!string.IsNullOrEmpty(newParentKey) && !groups.TryGetValue(newParentKey, out parentId))
                {
                    string value = db.Scalar("SELECT id FROM groups WHERE key=?", newParentKey);
                    if (value != null)
                    {
                        parentId = Number(value);
                        groups[newParentKey] = parentId;
                    }
                }
                if (parentId == 0)
                    throw new IOException("目标父集合在数据库中不存在：" + newParentKey);
                if (parentId == rootId || newKeys.ContainsKey(parentId))
                    throw new IOException("集合不能移动到自身或子集合中。");
                db.Execute("UPDATE groups SET parent_id=? WHERE id=?", parentId, rootId);
            }
        }

        private static bool GroupHasReferences(Sqlite db, long id)
        {
            if (db.Scalar("SELECT count(*) FROM records WHERE group_id=?", id) != "0") return true;
            if (db.Scalar("SELECT count(*) FROM added_items WHERE group_id=?", id) != "0") return true;
            return db.Scalar("SELECT count(*) FROM groups WHERE parent_id=?", id) != "0";
        }

        private static string RetiredGroupKey(Sqlite db, long id)
        {
            string key = "__retired__:" + id.ToString(CultureInfo.InvariantCulture);
            while (db.Scalar("SELECT id FROM groups WHERE key=?", key) != null)
                key += "_" + Guid.NewGuid().ToString("N");
            return key;
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
                                // 先删后改再插：分类覆盖有 (item_id) 唯一索引，先插新行再删旧行会
                                // 在“同一项连续移动到另一个分类”时稳定触发唯一约束错误。
                                foreach (Record r in baseline.Values) if (!next.ContainsKey(r.Key))
                                {
                                    remove.Bind(r.Key); remove.Read(); deleted++;
                                    if (r.Item != 0) affected.Add(r.Item);
                                }
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
                            revision = newRevision; baseline = next; renames.Clear(); entryRenames.Clear(); groupMoves.Clear();
                            config.Database = this; config.DatabaseRevision = revision; config.DatabaseError = null;
                            if (config.AddedItems.Store == this) config.AddedItems.Accept(addedCount);
                            else config.AddedItems = new AddedEntryList(this, addedCount);
                            return true;
                        }
                        catch
                        {
                            db.Rollback();
                            foreach (AddedEntry entry in config.AddedItems.New) { entry.RecordId = 0; entry.ItemId = 0; }
                            // 改名是本次保存的待办状态，失败后不能留在队列里反复重试；
                            // 调用方还会从数据库重载配置，保证内存状态与已提交状态一致。
                            renames.Clear(); entryRenames.Clear(); groupMoves.Clear();
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
                // 存储位置池必须整体落库：子项没有稳定身份，拆成多条记录会生成同一个键并互相覆盖。
                if (section.Name == "ResourceStoreLocations")
                {
                    AddRecord(result, "Settings", 0, 0, 0, section.Name, section.OuterXml);
                    continue;
                }
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
                    {
                        group = Group(db, CollectionKeys.GetKey(node.GetAttribute("parent"), node.GetAttribute("parentCollection"), node.GetAttribute("name")));
                        node.RemoveAttribute("parent"); node.RemoveAttribute("parentCollection"); node.RemoveAttribute("name");
                    }
                    else if (name == "CategoryOverrides")
                    { item = Entry(db, node.GetAttribute("key")); group = Group(db, node.InnerText); node.RemoveAttribute("key"); node.InnerText = ""; }
                    else if (name == "CategoryViewModes" || name == "CategoryIconNames")
                    { group = Group(db, node.GetAttribute("cat")); node.RemoveAttribute("cat"); }
                    else if (name == "AddedItems")
                    { item = Entry(db, "add:" + node.GetAttribute("path")); group = Group(db, node.GetAttribute("category")); node.RemoveAttribute("category"); }
                    string slot = name == "DesktopBackup" ? node.GetAttribute("path") + "\n" + node.GetAttribute("current") : "";
                    AddRecord(result, name, item, group,
                        name == "SidebarOrder" || name == "CustomCategories" || name == "ResourceCategories"
                            || name == "Collections" ? ordinal : 0, slot, node.OuterXml);
                    ordinal++;
                }
            }
            return result;
        }
        private static void AddRecord(Dictionary<string, Record> records, string section, long item, long group, long ordinal, string slot, string payload)
        {
            string key = section + ":" + item + ":" + group + ":" + slot;
            Record existing;
            if (records.TryGetValue(key, out existing) && !string.Equals(existing.Payload, payload, StringComparison.Ordinal))
                throw new InvalidDataException("配置记录键冲突: " + key);
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
                    else if (section == "Collections")
                    {
                        node.SetAttribute("name", r[6]);
                        string root, parentCollection, name;
                        if (CollectionKeys.TryParse(r[2], out root, out parentCollection, out name))
                        {
                            node.SetAttribute("parent", root);
                            node.SetAttribute("parentCollection", parentCollection);
                        }
                        else
                        {
                            node.SetAttribute("parent", r[7] ?? string.Empty);
                            node.SetAttribute("parentCollection", string.Empty);
                        }
                    }
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

        internal void Backup(string path)
        {
            lock (gate)
            using (Sqlite db = Open())
            {
                string target = Path.GetFullPath(path);
                if (string.Equals(target, Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("备份目标不能是当前数据库。");
                string dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    db.Backup(temp);
                    if (!File.Exists(target)) { File.Move(temp, target); return; }
                    try { File.Replace(temp, target, null); return; }
                    catch { }

                    // 文件系统不支持 Replace 时先保留旧备份，再原子换名；失败恢复旧文件。
                    string previous = target + ".previous-" + Guid.NewGuid().ToString("N");
                    File.Move(target, previous);
                    try
                    {
                        File.Move(temp, target);
                        File.Delete(previous);
                    }
                    catch
                    {
                        if (!File.Exists(target) && File.Exists(previous)) File.Move(previous, target);
                        throw;
                    }
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }
        }
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
                    string restoredError = DatabaseCompatibility.ValidateSnapshot(db, ApplicationId);
                    if (restoredError != null) throw new IOException("恢复后的数据库校验失败：" + restoredError);
                    int restoredVersion = Convert.ToInt32(db.Scalar("PRAGMA user_version"), CultureInfo.InvariantCulture);
                    if (restoredVersion < SchemaVersion) UpgradeSchema(db, restoredVersion);
                    // 恢复的快照可能来自旧版本，没有后加的索引。
                    EnsureIndexes(db);
                    long restoredRevision = Number(db.Scalar("SELECT value FROM metadata WHERE key='revision'"));
                    long nextRevision = Math.Max(revision, restoredRevision) + 1;
                    db.Execute("BEGIN IMMEDIATE");
                    NewOperation(db, "restore.snapshot", "safety=" + Path.GetFileName(safety));
                    db.Execute("UPDATE metadata SET value=? WHERE key='revision'", nextRevision.ToString(CultureInfo.InvariantCulture));
                    db.Execute("COMMIT");
                    LoadBaseline(db); renames.Clear(); entryRenames.Clear(); groupMoves.Clear();
                }
                catch
                {
                    try { db.Rollback(); } catch { }
                    db.Restore(safety); LoadBaseline(db); throw;
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
                    // 迁移类事务（XML 迁入、大小写条目合并、同名分类拆分、软件侧文件转资源）带有不受审计的完成标记或会在启动时重做，
                    // 逐项撤销会让数据与标记不一致或来回反复，只能用完整备份恢复。
                    if ((action != null && action.StartsWith("migration.", StringComparison.Ordinal)) || action == "restore.snapshot")
                        throw new IOException("初始化、数据迁移或整库恢复请使用完整备份恢复，不能逐项撤销。");
                    if (!string.IsNullOrEmpty(action) && action.StartsWith("physical.", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("该事务同时改动了磁盘文件，不能只撤销数据库记录；请使用布局备份恢复。");
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
                    LoadBaseline(db); renames.Clear(); groupMoves.Clear();
                }
                catch { db.Rollback(); throw; }
            }
        }
    }
}
