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
                catch { db.Rollback(); LoadBaseline(db); throw; }
            }
        }

        private const string Rw2DateIndexMarker = "rw2_taken_date_index_v1";

        /// <summary>
        /// 一次性刷新旧版本已索引的 RW2 日期；只重置标记，实际读盘仍由后台 IndexMetadata 分批完成。
        /// 标记与重置在同一事务内提交，失败时不写标记，下次启动自动重试。
        /// </summary>
        private void RepairRw2TakenDateIndex()
        {
            try
            {
                lock (gate)
                using (Sqlite db = Open())
                {
                    if (db.Scalar("SELECT value FROM metadata WHERE key=?", Rw2DateIndexMarker) == "1") return;
                    // 未安装 WIC 解码器时先不写标记；安装后下次启动会自动重新排队旧 RW2。
                    if (WicImageDecoder.IsRw2WicDecoderRegistered() == false) return;
                    db.Execute("BEGIN IMMEDIATE");
                    try
                    {
                        db.Execute("UPDATE catalog SET indexed=0 WHERE active=1 AND indexed=1 AND lower(metadata) LIKE ?",
                            "%target=\"%.rw2\"%");
                        db.Execute("INSERT OR REPLACE INTO metadata(key,value) VALUES(?,?)", Rw2DateIndexMarker, "1");
                        db.Execute("COMMIT");
                    }
                    catch
                    {
                        db.Rollback();
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                Paths.LogError(new IOException("刷新 RW2 拍摄时间索引失败，下次启动重试。", ex));
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
                catch { db.Rollback(); LoadBaseline(db); throw; }
            }
        }

        private void RebuildListings(Sqlite db, AppConfig config, HashSet<long> affected)
        {
            using (Sqlite.Statement read = db.Prepare("SELECT c.metadata,c.kind,c.active,g.key,c.size,c.modified FROM catalog c LEFT JOIN groups g ON g.id=c.base_group WHERE c.item_id=?"))
            using (Sqlite.Statement relations = db.Prepare("SELECT r.section,g.key,r.ordinal,r.item_id FROM records r LEFT JOIN groups g ON g.id=r.group_id WHERE r.item_id=? OR r.item_id=(SELECT id FROM entries WHERE key=?) ORDER BY CASE WHEN r.item_id=? THEN 0 ELSE 1 END"))
            using (Sqlite.Statement memberships = db.Prepare("SELECT g.key,a.resource FROM added_items a LEFT JOIN groups g ON g.id=a.group_id WHERE a.item_id=?"))
            using (Sqlite.Statement sectionOf = db.Prepare("SELECT r.section FROM records r JOIN groups g ON g.id=r.group_id WHERE g.key=? AND r.section IN ('ResourceCategories','CustomCategories') LIMIT 1"))
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
                    bool resourceMember = kind == 2, softwareMember = kind != 2;
                    Dictionary<string, int> categories = new Dictionary<string, int>(StringComparer.Ordinal);
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
                            case "ItemCategories": if (exact) AddCategory(categories, relation[1], kind == 2 ? 2 : 0); break;
                            case "ExcludedCategories": if (exact) excluded.Add(relation[1]); break;
                            case "ItemOrders": if (!ranks.ContainsKey(relation[1])) ranks[relation[1]] = Number(relation[2]); break;
                        }
                    }
                    setHidden.Bind(isHidden, id); setHidden.Read();
                    // 分类覆盖属于哪个模式：按覆盖目标分类本身判断（资源/软件），不能一律当作条目的展示模式。
                    bool overrideResource = overridden ? IsResourceCategory(sectionOf, primary, kind == 2) : kind == 2;
                    AddCategory(categories, primary, overrideResource ? 2 : 0);
                    // 同一路径可同时登记在软件分类和资源分类（跨模式复制）：每条归属都要投影，不能只保留主分类。
                    // 显式分类覆盖表示在该模式里「移动到分类」：只有同一模式下的原归属不再展示，
                    // 另一模式的归属保持不变（在资源里移动不能让软件侧的分类跟着消失）。
                    memberships.Bind(id);
                    while (memberships.Read())
                    {
                        string[] membership = memberships.Row();
                        bool resource = membership[1] == "1";
                        if (overridden && resource == overrideResource) continue;
                        if (resource) resourceMember = true; else softwareMember = true;
                        AddCategory(categories, membership[0], resource ? 2 : 0);
                    }
                    if (!overridden && a.IsDesktop) AddCategory(categories, "快捷中心", kind == 2 ? 2 : 0);
                    foreach (string cat in excluded) categories.Remove(cat);
                    // 集合成员必须仍属于父分类，集合关系自身保留，移回父分类后可恢复。
                    List<string> invalid = new List<string>();
                    foreach (string cat in new List<string>(categories.Keys)) if (CollectionKeys.IsCollectionKey(cat))
                    {
                        int slash = cat.IndexOf('/', 4);
                        if (slash < 0 || !categories.ContainsKey(cat.Substring(4, slash - 4))) invalid.Add(cat);
                    }
                    foreach (string cat in invalid) categories.Remove(cat);
                    if (resourceMember) AddCategory(categories, "全部资源", 2);
                    // 软件页面只收录软件：「全部软件」覆盖软件侧全部条目（含手动登记的程序）；旧版「全部文件」不再投影。
                    if (softwareMember) AddCategory(categories, "全部软件", 0);
                    if (favorite) AddCategory(categories, "常用", kind == 2 ? 2 : 0);
                    foreach (KeyValuePair<string, int> entry in categories)
                    {
                        string category = entry.Key;
                        if (string.IsNullOrEmpty(category)) continue;
                        long rank = long.MaxValue;
                        if (!ranks.TryGetValue(category, out rank)) rank = long.MaxValue;
                        insert.Bind(Group(db, category), id, entry.Value, isHidden, rank, SearchName(a.Name), Number(row[4]), Number(row[5])); insert.Read();
                    }
                }
            }
        }

        /// <summary>
        /// 修复历史数据：同一路径在软件分类与资源分类各有归属时，旧投影只保留了优先的一条；
        /// 旧投影把手动登记的程序放在已停用的「全部文件」里，「全部软件」中缺少它们。
        /// 按 added_items 的实际归属补齐缺失的列表行，启动时只处理确实缺失的条目。
        /// </summary>
        internal void RepairMembershipProjections()
        {
            try
            {
                lock (gate)
                using (Sqlite db = Open())
                {
                    HashSet<long> affected = new HashSet<long>();
                    foreach (string[] row in db.Query("SELECT DISTINCT a.item_id FROM added_items a WHERE a.group_id IS NOT NULL"
                        + " AND NOT EXISTS (SELECT 1 FROM listing l WHERE l.item_id=a.item_id AND l.group_id=a.group_id)"
                        + " AND NOT EXISTS (SELECT 1 FROM records r WHERE r.section='CategoryOverrides' AND r.item_id=a.item_id)"
                        + " AND NOT EXISTS (SELECT 1 FROM records r WHERE r.section='ExcludedCategories' AND r.item_id=a.item_id AND r.group_id=a.group_id)"))
                        affected.Add(Number(row[0]));
                    // 只看仍有效的纯软件侧条目（kind<>2）：重建后必然投影到「全部软件」，之后不再命中，不会每次启动重复修复。
                    foreach (string[] row in db.Query("SELECT DISTINCT a.item_id FROM added_items a JOIN catalog c ON c.item_id=a.item_id"
                        + " WHERE a.resource=0 AND c.active=1 AND c.kind<>2"
                        + " AND NOT EXISTS (SELECT 1 FROM listing l JOIN groups g ON g.id=l.group_id WHERE l.item_id=a.item_id AND g.key='全部软件')"))
                        affected.Add(Number(row[0]));
                    if (affected.Count == 0) return;
                    db.Execute("BEGIN IMMEDIATE");
                    try
                    {
                        long operation = NewOperation(db, "catalog.repair");
                        db.Execute("UPDATE write_context SET operation_id=? WHERE id=1", operation);
                        UpdateAddedCatalog(db, affected);
                        RebuildListings(db, null, affected);
                        db.Execute("UPDATE write_context SET operation_id=NULL WHERE id=1");
                        int changes = Convert.ToInt32(db.Scalar("SELECT count(*) FROM changes WHERE operation_id=?", operation));
                        if (changes == 0) db.Execute("DELETE FROM operations WHERE id=?", operation);
                        else
                        {
                            revision++;
                            db.Execute("UPDATE metadata SET value=? WHERE key='revision'", revision.ToString(CultureInfo.InvariantCulture));
                        }
                        db.Execute("COMMIT");
                        LoadBaseline(db);
                    }
                    catch { db.Rollback(); throw; }
                }
            }
            catch (Exception ex) { Paths.LogError(new IOException("归属投影修复失败，后续保存时会重建。", ex)); }
        }

        /// <summary>
        /// 分类属于资源模式还是软件模式：内置名单直接判定，自定义分类看它登记在哪个分类列表；
        /// 都判断不出时沿用条目的展示模式（fallback）。软件/资源分类不允许重名，结论唯一。
        /// </summary>
        private static bool IsResourceCategory(Sqlite.Statement sectionOf, string category, bool fallback)
        {
            if (string.IsNullOrEmpty(category)) return fallback;
            if (Array.IndexOf(CategoryRules.ResourceBuiltIn, category) >= 0 || category == "全部资源") return true;
            if (Array.IndexOf(CategoryRules.BuiltIn, category) >= 0 || category == "快捷中心" || category == "常用"
                || category == "全部软件" || category == "全部文件") return false;
            sectionOf.Bind(category);
            if (!sectionOf.Read()) return fallback;
            return sectionOf.Row()[0] == "ResourceCategories";
        }

        private static void AddCategory(Dictionary<string, int> categories, string category, int kind)
        {
            if (string.IsNullOrEmpty(category)) return;
            int existing;
            if (!categories.TryGetValue(category, out existing) || existing < kind) categories[category] = kind;
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
        internal CatalogPage Page(string category, bool resources, bool showHidden, string keyword, int limit, CatalogPage after, bool byName, bool descending, string sortColumn = null, string[] extensions = null, string searchGroup = null)
        {
            if (limit < 1 || limit > 500) throw new ArgumentOutOfRangeException("limit");
            bool search = !string.IsNullOrEmpty(keyword);
            bool scopedSearch = search && !string.IsNullOrEmpty(searchGroup);
            long categoryGroup = 0;
            if (!search || scopedSearch)
            {
                lock (gate)
                {
                    if (!groups.TryGetValue(scopedSearch ? searchGroup : category, out categoryGroup)) return new CatalogPage();
                }
            }
            using (Sqlite db = Open())
            {
                CatalogPage result = new CatalogPage();
                List<object> args = new List<object>();
                string from;
                string where;
                bool metadataSort = sortColumn == "size" || sortColumn == "time";
                if (search)
                {
                    from = "catalog c";
                    where = "c.active=1 AND " + (resources ? "c.kind=2" : "c.kind<>2") + (showHidden ? "" : " AND c.hidden=0") + " AND (c.search_name LIKE ? OR EXISTS(SELECT 1 FROM listing sl JOIN groups sg ON sg.id=sl.group_id WHERE sl.item_id=c.item_id AND sg.key NOT IN('全部软件','全部文件','全部资源','常用') AND sg.name LIKE ?))";
                    args.Add(SearchPattern(keyword));
                    args.Add(SearchPattern(keyword));
                    if (scopedSearch)
                    {
                        where += " AND EXISTS(SELECT 1 FROM listing scoped WHERE scoped.item_id=c.item_id AND scoped.group_id=?)";
                        args.Add(categoryGroup);
                    }
                    byName = true;
                }
                else
                {
                    from = "listing l JOIN catalog c ON c.item_id=l.item_id";
                    where = "l.group_id=? AND " + (resources ? "l.kind=2" : "l.kind=0") + (showHidden ? "" : " AND l.hidden=0");
                    args.Add(categoryGroup);
                }
                if (extensions != null && extensions.Length > 0)
                {
                    // 先按扩展名收窄再解包 XML：曲库等场景不需要把整个分类的条目都反序列化一遍。
                    StringBuilder filter = new StringBuilder(" AND (");
                    for (int i = 0; i < extensions.Length; i++)
                    {
                        if (i > 0) filter.Append(" OR ");
                        filter.Append("c.metadata LIKE ?");
                        args.Add("%" + extensions[i] + "\"%");
                    }
                    filter.Append(")");
                    where += filter.ToString();
                }
                string countFrom = extensions != null && extensions.Length > 0
                    ? from : (search ? "catalog c" : "listing l");
                result.Total = after == null ? Convert.ToInt32(db.Scalar("SELECT count(*) FROM " + countFrom + " WHERE " + where, args.ToArray())) : after.Total;
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
                // 分类视图按该分类自己的归属类型取内容类型：同一路径可能同时属于软件分类和资源分类。
                string kindColumn = search ? "c.kind" : "l.kind";
                foreach (string[] row in db.Query("SELECT c.metadata,c.hidden,c.item_id," + rank + "," + name + ",g.key," + kindColumn + " FROM " + from + " LEFT JOIN groups g ON g.id=c.base_group WHERE " + where + " ORDER BY " + order + " LIMIT ?", args.ToArray()))
                {
                    AppInfo a = Unpack(row[0]); a.IsHidden = row[1] == "1"; result.Items.Add(a);
                    if (row[5] != null) a.Category = row[5];
                    a.DatabaseKind = Convert.ToInt32(row[6]);
                    result.LastId = Number(row[2]); result.LastRank = Number(row[3]); result.LastName = row[4];
                }
                return result;
            }
        }

        /// <summary>
        /// 集合侧边栏封面的批量查询：一次 GROUP BY 取每个集合按（名称, item_id）排序的首个成员（与 Page 的
        /// 摘要次序一致），替代逐集合分页查询。imagesOnly 时先按图片扩展名收窄；返回集合键到成员的映射，
        /// 空集合不出现在结果里。SQLite 恰好一个 MIN 聚合时裸列取达到最小值那一行的值。
        /// </summary>
        internal Dictionary<string, AppInfo> CollectionFirstMembers(IList<string> collectionKeys, bool showHidden, bool imagesOnly)
        {
            Dictionary<string, AppInfo> result = new Dictionary<string, AppInfo>(StringComparer.Ordinal);
            if (collectionKeys == null || collectionKeys.Count == 0) return result;
            StringBuilder ids = new StringBuilder();
            lock (gate)
            {
                foreach (string key in collectionKeys)
                {
                    long id;
                    if (!groups.TryGetValue(key, out id)) continue;
                    if (ids.Length > 0) ids.Append(',');
                    ids.Append(id);   // 内部自增 id，不是用户输入，直接拼接
                }
            }
            if (ids.Length == 0) return result;
            List<object> args = new List<object>();
            string where = "l.group_id IN (" + ids + ") AND l.kind=2" + (showHidden ? "" : " AND l.hidden=0");
            if (imagesOnly)
            {
                // 与 Page 的扩展名过滤同口径：先收窄再解包 XML
                StringBuilder filter = new StringBuilder(" AND (");
                string[] extensions = MediaRules.ImageExt;
                for (int i = 0; i < extensions.Length; i++)
                {
                    if (i > 0) filter.Append(" OR ");
                    filter.Append("c.metadata LIKE ?");
                    args.Add("%" + extensions[i] + "\"%");
                }
                filter.Append(")");
                where += filter.ToString();
            }
            using (Sqlite db = Open())
            {
                foreach (string[] row in db.Query(
                    "SELECT gl.key,c.metadata,c.hidden,g.key,MIN(l.name || char(31) || printf('%020d', l.item_id)) FROM listing l"
                    + " JOIN catalog c ON c.item_id=l.item_id LEFT JOIN groups g ON g.id=c.base_group JOIN groups gl ON gl.id=l.group_id"
                    + " WHERE " + where + " GROUP BY l.group_id", args.ToArray()))
                {
                    AppInfo a = Unpack(row[1]);
                    a.IsHidden = row[2] == "1";
                    if (row[3] != null) a.Category = row[3];
                    a.DatabaseKind = 2;
                    result[row[0]] = a;
                }
            }
            return result;
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
                    catch { db.Rollback(); throw; }
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
