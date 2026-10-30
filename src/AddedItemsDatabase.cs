using System;
using System.Collections.Generic;
using System.IO;

namespace AppManager
{
    internal sealed partial class ConfigDatabase
    {
        private void SaveAdded(Sqlite db, AppConfig config, HashSet<long> affected)
        {
            AddedEntryList list = config.AddedItems;
            if (list.Store != this)
            {
                foreach (string[] row in db.Query("SELECT DISTINCT item_id FROM added_items")) affected.Add(Number(row[0]));
                db.Execute("DELETE FROM added_items");
            }
            else
            {
                using (Sqlite.Statement read = db.Prepare("SELECT item_id FROM added_items WHERE id=?"))
                using (Sqlite.Statement remove = db.Prepare("DELETE FROM added_items WHERE id=?"))
                    foreach (long id in list.Removed)
                    { read.Bind(id); if (read.Read()) affected.Add(Number(read.Row()[0])); remove.Bind(id); remove.Read(); }
                using (Sqlite.Statement update = db.Prepare("UPDATE added_items SET item_id=?,group_id=?,path=?,path_key=?,name=?,folder=?,resource=? WHERE id=? AND (item_id<>? OR group_id IS NOT ? OR path<>? OR name<>? OR folder<>? OR resource<>?)"))
                    foreach (AddedEntry e in list.Dirty.Values)
                    {
                        if (list.Removed.Contains(e.RecordId)) continue;
                        long item = Entry(db, "add:" + e.Path), group = Group(db, e.Category);
                        update.Bind(item, Foreign(group), e.Path, e.Path.ToUpperInvariant(), e.Name, e.IsFolder, e.IsResource, e.RecordId,
                            item, Foreign(group), e.Path, e.Name, e.IsFolder, e.IsResource); update.Read();
                        if (db.Changes != 0) { affected.Add(item); if (e.ItemId != 0) affected.Add(e.ItemId); }
                    }
            }
            using (Sqlite.Statement insert = db.Prepare("INSERT OR IGNORE INTO added_items(item_id,group_id,path,path_key,name,folder,resource) VALUES(?,?,?,?,?,?,?)"))
                foreach (AddedEntry e in list.New)
                {
                    if (string.IsNullOrWhiteSpace(e.Path)) throw new IOException("内容路径不能为空。");
                    long item = Entry(db, "add:" + e.Path), group = Group(db, e.Category);
                    insert.Bind(item, Foreign(group), e.Path, e.Path.ToUpperInvariant(), e.Name, e.IsFolder, e.IsResource); insert.Read();
                    if (db.Changes != 0) { e.RecordId = db.LastId; e.ItemId = item; affected.Add(item); }
                    else { e.RecordId = Number(db.Scalar("SELECT id FROM added_items WHERE item_id=? AND group_id IS ?", item, Foreign(group))); e.ItemId = item; }
                }
        }

        private void UpdateAddedCatalog(Sqlite db, HashSet<long> affected)
        {
            using (Sqlite.Statement read = db.Prepare("SELECT a.path,a.name,g.key,a.folder,a.resource FROM added_items a LEFT JOIN groups g ON g.id=a.group_id WHERE a.item_id=? ORDER BY a.resource DESC,a.id LIMIT 1"))
                foreach (long id in affected)
                {
                    read.Bind(id);
                    if (!read.Read())
                    {
                        // 系统扫描条目与手动条目使用不同命名空间；仅注销手动条目的目录投影。
                        db.Execute("UPDATE catalog SET active=0 WHERE item_id=? AND EXISTS(SELECT 1 FROM entries WHERE id=? AND key LIKE 'add:%')", id, id);
                        continue;
                    }
                    string[] row = read.Row(); string path = row[0], name = row[1]; bool folder = row[3] == "1";
                    if (string.IsNullOrEmpty(name)) name = folder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
                    string category = string.IsNullOrEmpty(row[2]) ? CategoryRules.Classify(name) : row[2];
                    AppInfo app = new AppInfo { Key = "add:" + path, Name = name, TargetPath = path, Category = category, Source = "Added", IsFile = true, IsFolder = folder, DisplayIcon = path };
                    PutCatalog(db, id, app, row[4] == "1" ? 2 : 1);
                }
        }
    }
}
