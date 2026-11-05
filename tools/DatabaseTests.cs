using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using System.Xml;

namespace AppManager
{
    // Compile with src/**/*.cs, /main:AppManager.DatabaseTests. All data is isolated in TEMP.
    internal static class DatabaseTests
    {
        private static int passed, failed;
        private static string root;
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--crash-writer")
            {
                using (Sqlite db = new Sqlite(args[1], false))
                {
                    db.Execute("BEGIN IMMEDIATE");
                    db.Execute("INSERT INTO operations(utc,action,actor,session,pid) VALUES('test','uncommitted','test','test',0)");
                    long operation = db.LastId; db.Execute("UPDATE write_context SET operation_id=? WHERE id=1", operation);
                    db.Execute("INSERT INTO groups(key,name) VALUES('uncommitted','uncommitted')");
                    Process.GetCurrentProcess().Kill();
                }
                return 1;
            }
            root = Path.Combine(Path.GetTempPath(), "AppManagerDatabaseTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Paths.DataDirOverride = root; Paths.ConfigFileOverride = Path.Combine(root, "config.xml");
            try
            {
                Run("migration, all fields, idempotence and original XML preserved", Migration);
                Run("RW2 capture-date index refresh is targeted, idempotent and updates listings", Rw2MetadataIndexRefresh);
                Run("stable category and collection ids on rename", Rename);
                Run("nested collection keys, indexes and parent ids survive reload", CollectionTree);
                Run("nested collection subtree rename migrates keys, references and ids", CollectionSubtreeRename);
                Run("nested collection rename conflicts and failed saves are retryable", CollectionSubtreeConflictAndRollback);
                Run("nested collection subtree delete clears only its references", CollectionSubtreeDelete);
                Run("v1 database upgrades to v2 and v1 snapshots remain restorable", SchemaUpgrade);
                Run("collection import rejects malformed, orphaned, cyclic and duplicate trees", CollectionImportValidation);
                Run("latest-operation undo, redo and stale rollback rejection", Undo);
                Run("stale writer rejected; failed transaction keeps all rows", Concurrency);
                Run("consistent online backup and integrity", Backup);
                Run("backup overwrite is atomic and reflects the latest commit", BackupOverwrite);
                Run("indexed keyset pagination, exclusions, hidden and multi-membership", Paging);
                Run("UI reads bounded database page and supports next/previous", UiPaging);
                Run("mini music includes manual files and resources without duplicate paths", MiniMusic);
                Run("XML backup export reports write failure", ExportFailure);
                Run("corrupt XML/database refuse initialization or overwrite", Corruption);
                Run("added item edits/deletes are incremental, rollback/reload preserves data", AddedEdits);
                Run("mid-transaction failure and retry preserve all-or-nothing semantics", AtomicFailure);
                Run("consecutive category moves and used rename targets leave a retryable session", CategoryConflicts);
                Run("duplicate category membership merges instead of failing", DuplicateMembership);
                Run("physical disk transactions cannot be undone piecemeal", PhysicalUndo);
                Run("ambak import keeps local storage paths and drops forged desktop rows", AmbakSanitize);
                Run("resource storage locations persist as one ordered SQLite value", StorageLocationPoolSqlite);
                Run("legacy storage location records upgrade atomically and survive failed saves", StorageLocationUpgrade);
                Run("bulk path lookup matches case variants and staged rows", BulkLookup);
                Run("folder range and registered path lookups read the database directly", FolderRangeLookup);
                Run("case-insensitive entry lookup uses an index, also after online restore", NocaseIndex);
                Run("batched save failures are deferred on the worker and reported once", BatchSaveFailure);
                Run("recycle bin detection never treats network paths as recyclable", RecycleDetection);
                Run("desktop operation detection follows the operation lock held by another thread", DesktopOperationDetection);
                Run("moving within one mode keeps the item's categories in the other mode", CrossModeMove);
                Run("legacy cross-mode same-name categories split once without touching the other mode", CrossModeSplit);
                Run("legacy case-variant entries merge into one identity on load, once", CaseDuplicateMerge);
                Run("terminated writer recovers without exposing uncommitted rows", CrashRecovery);
                Run("metadata sorting covers whole category, both directions and keyset pages", MetadataSorting);
                Run("offline recovery keeps damaged original and restores validated snapshot", OfflineRecovery);
                Run("legacy name flags update indexed projection when removed", LegacyFlags);
                Run("collection first members batch query picks cover candidates and honours hidden", CollectionFirstMembers);
                if (args.Length > 0 && args[0] == "--scale") Run("300000 items, page plans, single relation update and restart", Scale);
                if (args.Length == 2 && args[0] == "--verify-copy") Run("current user XML copy migrates without semantic data loss", delegate { VerifyCopy(args[1]); });
            }
            finally
            {
                Paths.ConfigFileOverride = null; Paths.DataDirOverride = null;
                try { Directory.Delete(root, true); } catch { }
            }
            Console.WriteLine("RESULT " + passed + " passed, " + failed + " failed"); return failed == 0 ? 0 : 1;
        }
        private static void Run(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static string Folder(string name) { string p = Path.Combine(root, name); Directory.CreateDirectory(p); return p; }
        private static AppConfig Reload(string name) { return ConfigDatabase.Load(Path.Combine(root, name, "config.xml")); }
        private static AppConfig Fixture(string name)
        {
            string path = Path.Combine(Folder(name), "config.xml");
            AppConfig c = new AppConfig(); c.Favorites.Add("add:C:\\file.txt"); c.Hidden.Add("lnk:hidden");
            c.CustomCategories.Add("工具"); c.ResourceCategories.Add("照片");
            c.CategoryViewMode["工具"] = false; c.CategoryIconNames["工具"] = "Folder"; c.SidebarOrder.Add("工具");
            c.Collections.Add(new ResourceCollection { Parent = "照片", Name = "旅行", Description = "中文 & ' 引号", Cover = "C:\\cover.jpg", CoverFocusX = .25F });
            c.AddedItems.Add(new AddedEntry { Path = "C:\\file.txt", Name = "file", Category = "工具" });
            c.AddedItems.Add(new AddedEntry { Path = "C:\\image.jpg", Name = "image", Category = "照片", IsResource = true });
            c.CategoryOverride["add:C:\\file.txt"] = "工具";
            c.ItemCategories["add:C:\\image.jpg"] = new List<string> { "col:照片/旅行" };
            c.ExcludedCategories["add:C:\\file.txt"] = new List<string> { "其他" };
            c.ItemOrder["工具"] = new List<string> { "add:C:\\file.txt" };
            c.DesktopBackup.Add(new DesktopBackupEntry { OriginalPath = "C:\\desk.txt", CurrentPath = "C:\\backup.txt", IsCopy = true });
            Check(c.SaveTo(path), "fixture export"); return ConfigDatabase.Load(path);
        }
        private static void Migration()
        {
            AppConfig c = Fixture("migration"); Check(!c.LoadFailed, c.DatabaseError);
            string path = Path.Combine(root, "migration", "config.xml");
            string before = File.ReadAllText(path);
            Check(File.Exists(path + ".before-sqlite"), "migration backup missing");
            Check(c.Favorites.Count == 1 && c.Hidden.Count == 1 && c.CustomCategories[0] == "工具", "lists lost");
            Check(c.Collections[0].Description == "中文 & ' 引号" && c.Collections[0].CoverFocusX == .25F, "collection lost");
            Check(c.AddedItems.Count == 2 && c.AddedItems.Any(a => a.IsResource), "added entries lost");
            Check(c.ItemCategories["add:C:\\image.jpg"][0] == "col:照片/旅行", "membership lost");
            Check(c.DesktopBackup[0].IsCopy && !c.CategoryViewMode["工具"], "settings lost");
            int operations = c.Database.History(100).Count;
            Check(c.Save(), c.DatabaseError); Check(c.Database.History(100).Count == operations, "no-op wrote audit operation");
            AppConfig again = ConfigDatabase.Load(path); Check(!again.LoadFailed && again.AddedItems.Count == 2, "idempotence");
            again.Favorites.Clear(); Check(again.Save(), again.DatabaseError);
            Check(File.ReadAllText(path) == before, "legacy XML modified");
            Check(again.Database.Check().Contains("foreign_key_errors=0"), "foreign keys");
        }
        private static long GroupId(ConfigDatabase db, string key)
        { using (Sqlite s = new Sqlite(db.FilePath, true)) return Convert.ToInt64(s.Scalar("SELECT id FROM groups WHERE key=?", key)); }

        /// <summary>4-4：集合封面候选的批量查询与逐集合 Page 的首位成员口径一致，且遵守隐藏过滤。</summary>
        private static void CollectionFirstMembers()
        {
            AppConfig c = Fixture("collection-covers");
            // 旅行集合：名字更小的非图片成员 + 名字更大的第二张图片
            c.AddedItems.Add(new AddedEntry { Path = "C:\\aaa-doc.txt", Name = "aaa", Category = "照片", IsResource = true });
            c.ItemCategories["add:C:\\aaa-doc.txt"] = new List<string> { "col:照片/旅行" };
            c.AddedItems.Add(new AddedEntry { Path = "C:\\zzz.png", Name = "zzz", Category = "照片", IsResource = true });
            c.ItemCategories["add:C:\\zzz.png"] = new List<string> { "col:照片/旅行" };
            c.Collections.Add(new ResourceCollection { Parent = "照片", Name = "空集" });
            Check(c.Save(), c.DatabaseError);
            List<string> keys = new List<string> { "col:照片/旅行", "col:照片/空集" };
            Dictionary<string, AppInfo> images = c.Database.CollectionFirstMembers(keys, true, true);
            Dictionary<string, AppInfo> first = c.Database.CollectionFirstMembers(keys, true, false);
            AppInfo dbg; string got = images.TryGetValue("col:照片/旅行", out dbg) ? dbg.TargetPath : "<none:" + images.Count + ">";
            got += " len=" + got.Length + " keys=" + string.Join("|", images.Keys);
            Check(images.ContainsKey("col:照片/旅行") && images["col:照片/旅行"].TargetPath == "C:\\image.jpg", "first image member wrong: " + got);
            Check(first.ContainsKey("col:照片/旅行") && first["col:照片/旅行"].TargetPath == "C:\\aaa-doc.txt", "first member wrong");
            Check(!images.ContainsKey("col:照片/空集") && !first.ContainsKey("col:照片/空集"), "empty collection yielded a member");
            // 隐藏首个图片成员后（不显示隐藏项）落到下一张图片
            c.Hidden.Add("add:C:\\image.jpg"); Check(c.Save(), c.DatabaseError);
            images = c.Database.CollectionFirstMembers(keys, false, true);
            Check(images.ContainsKey("col:照片/旅行") && images["col:照片/旅行"].TargetPath == "C:\\zzz.png", "hidden filter ignored");
        }
        private static void Rename()
        {
            AppConfig c = Fixture("rename"); long parent = GroupId(c.Database, "照片"), collection = GroupId(c.Database, "col:照片/旅行");
            c.QueueCategoryRename("照片", "相片"); c.ResourceCategories[0] = "相片"; c.Collections[0].Parent = "相片";
            c.AddedItems.First(a => a.IsResource).Category = "相片";
            c.ItemCategories["add:C:\\image.jpg"][0] = "col:相片/旅行";
            Check(c.Save(), c.DatabaseError);
            long renamedParent = GroupId(c.Database, "相片"), renamedCollection = GroupId(c.Database, "col:相片/旅行");
            Check(renamedParent == parent && renamedCollection == collection,
                "ids changed parent " + parent + "=>" + renamedParent + ", collection " + collection + "=>" + renamedCollection);
            AppConfig read = ConfigDatabase.Load(Path.Combine(root, "rename", "config.xml"));
            Check(read.Collections[0].Parent == "相片" && read.ItemCategories["add:C:\\image.jpg"][0] == "col:相片/旅行", "renamed relationships lost");
            Check(c.Database.Page("col:相片/旅行", true, true, "", 10, null, false, false).Total == 1, "renamed listing lost");
        }
        private static void CollectionTree()
        {
            AppConfig c = Fixture("collection-tree");
            string rootKey = CollectionKeys.Make("照片", "旅行");
            string childKey = rootKey + "/2024";
            string grandKey = childKey + "/春";
            c.Collections.Add(new ResourceCollection { Parent = "照片", Name = "2024", ParentCollection = rootKey });
            c.Collections.Add(new ResourceCollection { Parent = "照片", Name = "春", ParentCollection = childKey });
            CollectionOps.AddMember(c, "add:C:\\image.jpg", childKey);
            CollectionOps.AddMember(c, "add:C:\\image.jpg", grandKey);
            Check(c.Save(), c.DatabaseError);

            AppConfig read = Reload("collection-tree");
            Check(!read.LoadFailed, read.DatabaseError);
            ResourceCollection child = read.Collections.Single(x => x.Name == "2024");
            ResourceCollection grand = read.Collections.Single(x => x.Name == "春");
            Check(child.Parent == "照片" && child.ParentCollection == rootKey, "child parent lost");
            Check(grand.Parent == "照片" && grand.ParentCollection == childKey, "grandchild parent lost");
            CollectionIndex index = new CollectionIndex(read.Collections);
            Check(index.Find(childKey) == child && index.Children(rootKey).Count == 1, "collection index mismatch");
            Check(index.IsDescendantOrSelf(rootKey, grandKey) && !index.IsDescendantOrSelf(rootKey, rootKey + "2"),
                "descendant boundary mismatch");
            Check(read.ItemCategories["add:C:\\image.jpg"].Contains(childKey)
                && read.ItemCategories["add:C:\\image.jpg"].Contains(grandKey), "nested memberships lost");
            string parsedParent, parsedParentCollection, parsedName;
            Check(CollectionKeys.TryParse(grandKey, out parsedParent, out parsedParentCollection, out parsedName)
                && parsedParent == "照片" && parsedParentCollection == childKey && parsedName == "春"
                && CollectionKeys.Depth(grandKey) == 3, "nested key parsing mismatch");

            long rootId = GroupId(read.Database, rootKey), childId = GroupId(read.Database, childKey), grandId = GroupId(read.Database, grandKey);
            using (Sqlite db = new Sqlite(read.Database.FilePath, true))
            {
                Check(db.Scalar("SELECT parent_id FROM groups WHERE id=?", childId) == rootId.ToString(), "child parent_id not direct collection");
                Check(db.Scalar("SELECT parent_id FROM groups WHERE id=?", grandId) == childId.ToString(), "grandchild parent_id not direct collection");
                Check(db.Scalar("PRAGMA user_version") == "2", "database did not upgrade to v2");
            }
        }

        private sealed class NestedCollectionFixture
        {
            internal AppConfig Config;
            internal ResourceCollection Root, Child, Grand, Sibling;
            internal string RootKey, ChildKey, GrandKey, SiblingKey;
            internal string ItemKey = "add:C:\\image.jpg";
            internal string ExcludedKey = "add:C:\\file.txt";
        }

        private static NestedCollectionFixture NestedCollections(string name, string siblingName)
        {
            AppConfig c = Fixture(name);
            ResourceCollection root = c.Collections.Single(x => x.Parent == "照片" && x.Name == "旅行" && x.ParentCollection == "");
            string rootKey = CollectionKeys.GetKey(root);
            string childKey = rootKey + "/2024";
            string grandKey = childKey + "/十月";
            ResourceCollection child = new ResourceCollection { Parent = "照片", ParentCollection = rootKey, Name = "2024" };
            ResourceCollection grand = new ResourceCollection { Parent = "照片", ParentCollection = childKey, Name = "十月" };
            ResourceCollection sibling = new ResourceCollection { Parent = "照片", Name = siblingName };
            c.Collections.Add(child); c.Collections.Add(grand); c.Collections.Add(sibling);
            string siblingKey = CollectionKeys.GetKey(sibling);
            CollectionOps.AddMember(c, "add:C:\\image.jpg", childKey);
            CollectionOps.AddMember(c, "add:C:\\image.jpg", grandKey);
            CollectionOps.AddMember(c, "add:C:\\image.jpg", siblingKey);
            c.ExcludedCategories["add:C:\\file.txt"].Add(childKey);
            c.ItemOrder[childKey] = new List<string> { "add:C:\\image.jpg" };
            c.ItemOrder[siblingKey] = new List<string> { "add:C:\\image.jpg" };
            c.CategoryViewMode[grandKey] = false;
            c.CategoryIconNames["resource:" + childKey] = "Star";
            c.CategoryIconNames[grandKey] = "Code";
            c.SidebarOrder.Add(grandKey);
            Check(c.Save(), c.DatabaseError);
            return new NestedCollectionFixture
            {
                Config = c, Root = root, Child = child, Grand = grand, Sibling = sibling,
                RootKey = rootKey, ChildKey = childKey, GrandKey = grandKey, SiblingKey = siblingKey
            };
        }

        private static void CollectionSubtreeRename()
        {
            NestedCollectionFixture f = NestedCollections("collection-subtree-rename", "证件");
            long rootId = GroupId(f.Config.Database, f.RootKey), childId = GroupId(f.Config.Database, f.ChildKey);
            long grandId = GroupId(f.Config.Database, f.GrandKey), siblingId = GroupId(f.Config.Database, f.SiblingKey);
            Dictionary<string, string> map = CollectionOps.RenameSubtree(f.Config, f.Root, "远行");
            string newRoot = "col:照片/远行", newChild = newRoot + "/2024", newGrand = newChild + "/十月";
            Check(map.Count == 3 && map[f.RootKey] == newRoot && map[f.ChildKey] == newChild && map[f.GrandKey] == newGrand,
                "subtree key map mismatch");
            Check(f.Root.Name == "远行" && f.Child.ParentCollection == newRoot && f.Grand.ParentCollection == newChild,
                "nested definitions did not follow rename");
            Check(CollectionOps.FindByKey(f.Config, newGrand) == f.Grand && CollectionOps.FindByKey(f.Config, f.GrandKey) == null,
                "exact nested lookup mismatch");
            List<string> members = f.Config.ItemCategories[f.ItemKey];
            Check(members.Contains(newRoot) && members.Contains(newChild) && members.Contains(newGrand)
                && !members.Contains(f.RootKey) && !members.Contains(f.ChildKey) && !members.Contains(f.GrandKey),
                "membership keys did not migrate");
            Check(f.Config.ExcludedCategories[f.ExcludedKey].Contains(newChild)
                && !f.Config.ExcludedCategories[f.ExcludedKey].Contains(f.ChildKey)
                && f.Config.ExcludedCategories[f.ExcludedKey].Contains("其他"), "excluded references did not migrate");
            Check(f.Config.ItemOrder.ContainsKey(newChild) && !f.Config.ItemOrder.ContainsKey(f.ChildKey)
                && f.Config.ItemOrder.ContainsKey(f.SiblingKey), "order keys did not migrate or sibling was touched");
            Check(f.Config.CategoryViewMode.ContainsKey(newGrand) && !f.Config.CategoryViewMode.ContainsKey(f.GrandKey),
                "view mode did not migrate");
            Check(f.Config.CategoryIconNames["resource:" + newChild] == "Star" && f.Config.CategoryIconNames[newGrand] == "Code"
                && !f.Config.CategoryIconNames.ContainsKey("resource:" + f.ChildKey)
                && !f.Config.CategoryIconNames.ContainsKey(f.GrandKey), "collection icon keys did not migrate");
            Check(f.Config.SidebarOrder.Contains(newGrand) && !f.Config.SidebarOrder.Contains(f.GrandKey), "sidebar reference did not migrate");

            f.Config.QueueCategoryRename(f.RootKey, newRoot);
            Check(f.Config.Save(), f.Config.DatabaseError);
            Check(GroupId(f.Config.Database, newRoot) == rootId && GroupId(f.Config.Database, newChild) == childId
                && GroupId(f.Config.Database, newGrand) == grandId && GroupId(f.Config.Database, f.SiblingKey) == siblingId,
                "subtree rename changed group identities");
            using (Sqlite db = new Sqlite(f.Config.Database.FilePath, true))
            {
                Check(db.Scalar("SELECT id FROM groups WHERE key=?", f.RootKey) == null
                    && db.Scalar("SELECT id FROM groups WHERE key=?", f.ChildKey) == null
                    && db.Scalar("SELECT id FROM groups WHERE key=?", f.GrandKey) == null, "old group keys remained");
                Check(db.Scalar("SELECT parent_id FROM groups WHERE id=?", childId) == rootId.ToString()
                    && db.Scalar("SELECT parent_id FROM groups WHERE id=?", grandId) == childId.ToString(),
                    "rename changed group parent links");
            }
            AppConfig read = Reload("collection-subtree-rename");
            Check(!read.LoadFailed, read.DatabaseError);
            ResourceCollection readGrand = read.Collections.Single(x => x.Name == "十月");
            Check(CollectionKeys.GetKey(readGrand) == newGrand && readGrand.ParentCollection == newChild, "reloaded nested path mismatch");
            Check(read.ItemCategories[f.ItemKey].Contains(newGrand) && !read.ItemCategories[f.ItemKey].Contains(f.GrandKey),
                "reloaded membership mismatch");
            Check(read.Database.Page(newGrand, true, true, "", 10, null, false, false).Total == 1, "renamed nested listing lost");
            Check(read.Database.Check().Contains("foreign_key_errors=0"), "rename broke foreign keys");

            long renamedRootId = GroupId(read.Database, newRoot), renamedChildId = GroupId(read.Database, newChild);
            long renamedGrandId = GroupId(read.Database, newGrand), renamedSiblingId = GroupId(read.Database, f.SiblingKey);
            read.ResourceCategories[0] = "相片";
            read.AddedItems.First(x => x.IsResource).Category = "相片";
            read.QueueCategoryRename("照片", "相片");
            CollectionOps.RenameParent(read, "照片", "相片");
            string parentRoot = "col:相片/远行", parentChild = parentRoot + "/2024", parentGrand = parentChild + "/十月";
            Check(read.Collections.Any(x => CollectionKeys.GetKey(x) == parentGrand && x.ParentCollection == parentChild),
                "parent category rename did not migrate nested ParentCollection");
            Check(read.ItemCategories[f.ItemKey].Contains(parentGrand)
                && !read.ItemCategories[f.ItemKey].Contains(newGrand), "parent category rename did not migrate memberships");
            Check(read.CategoryIconNames.ContainsKey("resource:" + parentChild)
                && read.CategoryIconNames.ContainsKey(parentGrand) && read.ItemOrder.ContainsKey(parentChild),
                "parent category rename did not migrate collection references");
            Check(read.Save(), read.DatabaseError);
            Check(GroupId(read.Database, parentRoot) == renamedRootId && GroupId(read.Database, parentChild) == renamedChildId
                && GroupId(read.Database, parentGrand) == renamedGrandId
                && GroupId(read.Database, "col:相片/证件") == renamedSiblingId,
                "parent category rename changed nested ids");
            AppConfig parentRead = Reload("collection-subtree-rename");
            Check(parentRead.Collections.Single(x => x.Name == "十月").ParentCollection == parentChild,
                "reloaded parent category rename mismatch");
        }

        private static void CollectionSubtreeConflictAndRollback()
        {
            NestedCollectionFixture f = NestedCollections("collection-subtree-conflicts", "远行");
            bool rejected = false;
            try { CollectionOps.RenameSubtree(f.Config, f.Root, "远行"); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "rename onto an existing sibling was accepted");
            Check(f.Config.Collections.Any(x => x.Name == "旅行" && x.ParentCollection == "")
                && f.Config.Collections.Any(x => x.Name == "2024" && x.ParentCollection == f.RootKey)
                && f.Config.ItemCategories[f.ItemKey].Contains(f.ChildKey), "conflicted rename mutated memory");

            using (Sqlite db = new Sqlite(f.Config.Database.FilePath, false))
                db.Execute("CREATE TRIGGER test_collection_rename_failure BEFORE UPDATE ON groups BEGIN SELECT RAISE(ABORT,'injected collection rename failure'); END");
            string newRoot = "col:照片/远行2";
            CollectionOps.RenameSubtree(f.Config, f.Root, "远行2");
            f.Config.QueueCategoryRename(f.RootKey, newRoot);
            Check(!f.Config.Save(), "injected collection rename failure was accepted");
            Check(f.Config.Collections.Any(x => x.Name == "旅行" && x.ParentCollection == "")
                && f.Config.Collections.Any(x => x.Name == "2024" && x.ParentCollection == f.RootKey)
                && !f.Config.Collections.Any(x => x.Name == "远行2")
                && f.Config.ItemCategories[f.ItemKey].Contains(f.GrandKey)
                && !f.Config.ItemCategories[f.ItemKey].Contains(newRoot + "/2024/十月"),
                "failed save did not reload the committed collection tree");
            using (Sqlite db = new Sqlite(f.Config.Database.FilePath, false)) db.Execute("DROP TRIGGER test_collection_rename_failure");

            ResourceCollection retry = f.Config.Collections.Single(x => x.Name == "旅行" && x.ParentCollection == "");
            CollectionOps.RenameSubtree(f.Config, retry, "远行2");
            f.Config.QueueCategoryRename(f.RootKey, newRoot);
            Check(f.Config.Save(), "retry after failed nested rename failed: " + f.Config.DatabaseError);
            Check(f.Config.Collections.Any(x => x.Name == "2024" && x.ParentCollection == newRoot)
                && f.Config.ItemCategories[f.ItemKey].Contains(newRoot + "/2024/十月"), "retry did not migrate the subtree");
        }

        private static void CollectionSubtreeDelete()
        {
            NestedCollectionFixture f = NestedCollections("collection-subtree-delete", "证件");
            long siblingId = GroupId(f.Config.Database, f.SiblingKey);
            Check(CollectionOps.CountAffectedMembers(f.Config, CollectionOps.Subtree(f.Config, f.Root)) == 1,
                "affected member count mismatch");
            List<ResourceCollection> removed = CollectionOps.RemoveSubtree(f.Config, f.Root);
            Check(removed.Count == 3 && f.Config.Collections.Count == 1 && f.Config.Collections[0].Name == "证件",
                "subtree delete removed the wrong definitions");
            Check(f.Config.ItemCategories[f.ItemKey].Count == 1 && f.Config.ItemCategories[f.ItemKey][0] == f.SiblingKey,
                "subtree delete damaged sibling membership");
            Check(!f.Config.ExcludedCategories[f.ExcludedKey].Contains(f.ChildKey)
                && f.Config.ExcludedCategories[f.ExcludedKey].Contains("其他"), "subtree delete left an excluded reference");
            Check(!f.Config.ItemOrder.ContainsKey(f.ChildKey) && f.Config.ItemOrder.ContainsKey(f.SiblingKey),
                "subtree delete left an order reference");
            Check(!f.Config.CategoryViewMode.ContainsKey(f.GrandKey), "subtree delete left a view reference");
            Check(!f.Config.CategoryIconNames.ContainsKey("resource:" + f.ChildKey)
                && !f.Config.CategoryIconNames.ContainsKey(f.GrandKey), "subtree delete left an icon reference");
            Check(!f.Config.SidebarOrder.Contains(f.GrandKey), "subtree delete left a sidebar reference");
            Check(f.Config.Save(), f.Config.DatabaseError);
            Check(GroupId(f.Config.Database, f.SiblingKey) == siblingId, "subtree delete changed the sibling id");
            AppConfig read = Reload("collection-subtree-delete");
            Check(!read.LoadFailed && read.Collections.Count == 1 && read.Collections[0].Name == "证件", "reloaded tree is wrong");
            Check(read.ItemCategories[f.ItemKey].Count == 1 && read.ItemCategories[f.ItemKey][0] == f.SiblingKey
                && !read.ItemOrder.ContainsKey(f.ChildKey) && !read.CategoryViewMode.ContainsKey(f.GrandKey),
                "reloaded references were not cleaned");
            Check(read.Database.Check().Contains("foreign_key_errors=0"), "subtree delete broke foreign keys");
        }
        private static void SchemaUpgrade()
        {
            AppConfig c = Fixture("schema-upgrade");
            string path = Path.Combine(root, "schema-upgrade", "config.xml");
            using (Sqlite db = new Sqlite(c.Database.FilePath, false)) db.Execute("PRAGMA user_version=1");
            AppConfig upgraded = ConfigDatabase.Load(path);
            Check(!upgraded.LoadFailed && upgraded.Favorites.Count == 1, "v1 database did not reopen");
            using (Sqlite db = new Sqlite(upgraded.Database.FilePath, true)) Check(db.Scalar("PRAGMA user_version") == "2", "v1 was not upgraded");

            string snapshot = Path.Combine(root, "schema-v1-snapshot.db");
            upgraded.Database.Backup(snapshot);
            using (Sqlite db = new Sqlite(snapshot, false)) db.Execute("PRAGMA user_version=1");
            upgraded.Favorites.Add("add:after-snapshot");
            Check(upgraded.Save(), upgraded.DatabaseError);
            string safety = upgraded.Database.Restore(snapshot);
            Check(File.Exists(safety), "safety copy missing");
            Check(upgraded.Database.Read().Favorites.Count == 1, "v1 snapshot restore mismatch");
            using (Sqlite db = new Sqlite(upgraded.Database.FilePath, true))
                Check(db.Scalar("PRAGMA user_version") == "2", "restored v1 snapshot was not upgraded");

            using (Sqlite db = new Sqlite(snapshot, false)) db.Execute("PRAGMA user_version=3");
            bool rejected = false;
            try { new ConfigDatabase(snapshot); }
            catch (IOException) { rejected = true; }
            Check(rejected, "future database version was accepted");
        }
        private static void CollectionImportValidation()
        {
            string rootKey = CollectionKeys.Make("照片", "旅行");
            string childKey = rootKey + "/2024";
            AppConfig valid = new AppConfig();
            valid.Collections.Add(new ResourceCollection { Parent = "照片", Name = "旅行" });
            valid.Collections.Add(new ResourceCollection { Parent = "照片", Name = "2024", ParentCollection = rootKey });
            valid.ItemCategories["add:C:\\image.jpg"] = new List<string> { childKey };
            Check(valid.ValidateImportedContent(true) == null, "valid nested tree rejected");

            AppConfig missing = new AppConfig();
            missing.Collections.Add(new ResourceCollection { Parent = "照片", Name = "2024", ParentCollection = rootKey + "/缺失" });
            Check(missing.ValidateImportedContent(true) != null, "missing parent accepted");

            AppConfig mismatch = new AppConfig();
            mismatch.Collections.Add(new ResourceCollection { Parent = "照片", Name = "旅行" });
            mismatch.Collections.Add(new ResourceCollection { Parent = "视频", Name = "2024", ParentCollection = rootKey });
            Check(mismatch.ValidateImportedContent(true) != null, "root category mismatch accepted");

            AppConfig malformed = new AppConfig();
            malformed.Collections.Add(new ResourceCollection { Parent = "照片", Name = "2024", ParentCollection = "照片/旅行" });
            Check(malformed.ValidateImportedContent(true) != null, "malformed parent key accepted");

            AppConfig duplicate = new AppConfig();
            duplicate.Collections.Add(new ResourceCollection { Parent = "照片", Name = "Travel" });
            duplicate.Collections.Add(new ResourceCollection { Parent = "照片", Name = "travel" });
            Check(duplicate.ValidateImportedContent(true) != null, "case-insensitive sibling duplicate accepted");

            AppConfig orphan = new AppConfig();
            orphan.Collections.Add(new ResourceCollection { Parent = "照片", Name = "旅行" });
            orphan.ItemCategories["add:C:\\image.jpg"] = new List<string> { childKey };
            Check(orphan.ValidateImportedContent(true) != null, "orphan membership accepted");

            AppConfig caseReference = new AppConfig();
            caseReference.Collections.Add(new ResourceCollection { Parent = "照片", Name = "Travel" });
            caseReference.ItemCategories["add:C:\\image.jpg"] = new List<string> { CollectionKeys.Make("照片", "travel") };
            Check(caseReference.ValidateImportedContent(true) != null, "case-variant membership key accepted");

            AppConfig unknownRoot = new AppConfig();
            unknownRoot.Collections.Add(new ResourceCollection { Parent = "不存在", Name = "旅行" });
            Check(unknownRoot.ValidateImportedContent(true) != null, "unknown root category accepted");

            AppConfig deep = new AppConfig();
            string parentKey = string.Empty;
            for (int i = 1; i <= CollectionKeys.MaxDepth + 1; i++)
            {
                ResourceCollection collection = new ResourceCollection { Parent = "照片", Name = "D" + i, ParentCollection = parentKey };
                deep.Collections.Add(collection);
                parentKey = CollectionKeys.GetKey(collection);
            }
            Check(deep.ValidateImportedContent(true) != null, "over-depth tree accepted");

            AppConfig versioned = new AppConfig();
            Check(versioned.ToDocument(false).DocumentElement.GetAttribute("backupFormatVersion") == "2", "backup format version missing");
            XmlDocument future = versioned.ToDocument(false);
            future.DocumentElement.SetAttribute("backupFormatVersion", "3");
            string futurePath = Path.Combine(root, "future-format.ambak");
            future.Save(futurePath);
            Check(AppConfig.ImportFrom(futurePath) == null, "future backup format accepted");

            XmlDocument incomplete = valid.ToDocument(false);
            ((XmlElement)incomplete.DocumentElement.SelectSingleNode("Collections/Item")).SetAttribute("parent", string.Empty);
            string incompletePath = Path.Combine(root, "incomplete-collection.ambak");
            incomplete.Save(incompletePath);
            AppConfig incompleteImport = AppConfig.ImportFrom(incompletePath);
            Check(incompleteImport != null && incompleteImport.ValidateImportedContent(true) != null,
                "incomplete collection was silently dropped");
        }
        private static void Undo()
        {
            AppConfig c = Fixture("undo"); c.Favorites.Add("af:new"); c.CustomCategories.Add("新增"); Check(c.Save(), c.DatabaseError);
            long operation = Convert.ToInt64(c.Database.History(1)[0][0]);
            c.Database.Undo(operation);
            AppConfig read = c.Database.Read(); Check(!read.Favorites.Contains("af:new") && !read.CustomCategories.Contains("新增"), "undo incomplete");
            bool rejected = false; try { c.Database.Undo(operation); } catch (IOException) { rejected = true; } Check(rejected, "stale undo accepted");
            long undo = Convert.ToInt64(c.Database.History(1)[0][0]); c.Database.Undo(undo);
            read = c.Database.Read(); Check(read.Favorites.Contains("af:new") && read.CustomCategories.Contains("新增"), "redo incomplete");
            Check(c.Database.Check().Contains("foreign_key_errors=0"), "undo foreign keys");
        }
        private static void Concurrency()
        {
            AppConfig first = Fixture("writers"), stale = ConfigDatabase.Load(Path.Combine(root, "writers", "config.xml"));
            first.Favorites.Add("first"); Check(first.Save(), first.DatabaseError);
            stale.Favorites.Add("stale"); Check(!stale.Save() && stale.DatabaseError.Contains("更新"), "stale writer overwritten");
            AppConfig current = ConfigDatabase.Load(Path.Combine(root, "writers", "config.xml"));
            Check(current.Favorites.Contains("first") && !current.Favorites.Contains("stale"), "lost newer commit");
            int count = current.Database.History(100).Count;
            using (Sqlite locked = new Sqlite(current.Database.FilePath, false))
            {
                locked.Execute("BEGIN IMMEDIATE"); current.Favorites.Add("blocked"); Check(!current.Save(), "lock ignored"); locked.Execute("ROLLBACK");
            }
            Check(current.Database.History(100).Count == count, "failed write recorded as successful");
            Check(current.Save(), "retry failed: " + current.DatabaseError);
        }
        private static void Backup()
        {
            AppConfig c = Fixture("backup"); string path = Path.Combine(root, "snapshot.db"); c.Database.Backup(path);
            using (Sqlite db = new Sqlite(path, true))
            { Check(db.Scalar("PRAGMA integrity_check") == "ok" && db.Scalar("PRAGMA journal_mode") == "delete", "backup incomplete"); }
            c.Favorites.Clear(); Check(c.Save(), c.DatabaseError);
            using (Sqlite db = new Sqlite(path, true)) Check(db.Scalar("SELECT count(*) FROM records WHERE section='Favorites'") == "1", "backup mutated");
            string safety = c.Database.Restore(path);
            Check(File.Exists(safety) && c.Database.Read().Favorites.Count == 1, "restore or safety copy failed");
            Check(!c.Save(), "pre-restore in-memory state overwrote restored data");
        }

        private static void BackupOverwrite()
        {
            AppConfig c = Fixture("backup-overwrite");
            string path = Path.Combine(root, "overwrite.db");
            c.Database.Backup(path);
            c.Favorites.Add("backup-second");
            Check(c.Save(), c.DatabaseError);
            c.Database.Backup(path);
            using (Sqlite db = new Sqlite(path, true))
            {
                Check(db.Scalar("PRAGMA integrity_check") == "ok", "overwritten backup integrity");
                Check(Convert.ToInt32(db.Scalar("SELECT count(*) FROM records WHERE section='Favorites'")) == 2,
                    "overwritten backup kept stale rows");
            }
            bool rejected = false;
            try { c.Database.Backup(c.Database.FilePath); }
            catch (IOException) { rejected = true; }
            Check(rejected, "backup over the live database was accepted");
            Check(Directory.GetFiles(root, "overwrite.db.tmp-*").Length == 0, "backup temporary file left behind");
        }
        private static void Paging()
        {
            AppConfig c = Fixture("paging");
            for (int i = 0; i < 503; i++) c.AddedItems.Add(new AddedEntry { Path = "C:\\photo" + i + ".jpg", Name = "照片" + i.ToString("D4"), Category = "照片", IsResource = true });
            c.Hidden.Add("add:C:\\photo0.jpg"); c.ExcludedCategories["add:C:\\photo1.jpg"] = new List<string> { "照片" };
            Check(c.Save(), c.DatabaseError);
            HashSet<string> keys = new HashSet<string>(); CatalogPage page = null; int total = 0;
            do
            {
                page = c.Database.Page("照片", true, false, "", 100, page, false, false);
                total = page.Total; foreach (AppInfo a in page.Items) Check(keys.Add(a.Key), "duplicate page row");
            } while (page.Items.Count > 0);
            Check(keys.Count == 502 && total == 502, "missing rows: " + keys.Count + "/" + total);
            Check(c.Database.Page("照片", true, true, "", 10, null, false, false).Total == 503, "hidden filter");
            Check(c.Database.Page("全部资源", true, true, "", 10, null, false, false).Total == 504, "exclusion removed physical item");
            Check(c.Database.Page("照片", true, false, "照片0002", 10, null, true, false).Total == 1, "search");
        }

        private static void Rw2MetadataIndexRefresh()
        {
            if (WicImageDecoder.IsRw2WicDecoderRegistered() == false)
            {
                Console.WriteLine("  RW2 WIC decoder unavailable; migration retry is deferred until it is installed");
                return;
            }
            string folder = Folder("rw2-index");
            string path = Path.Combine(folder, "config.xml");
            string rw2 = Path.Combine(folder, "photo.rw2");
            string jpg = Path.Combine(folder, "photo.jpg");
            using (Bitmap bitmap = new Bitmap(4, 2))
            {
                PropertyItem item = (PropertyItem)FormatterServices.GetUninitializedObject(typeof(PropertyItem));
                item.Id = 0x9003; item.Type = 2;
                item.Value = System.Text.Encoding.ASCII.GetBytes("2020:01:02 03:04:05\0");
                item.Len = item.Value.Length;
                bitmap.SetPropertyItem(item);
                bitmap.Save(rw2, ImageFormat.Jpeg);
            }
            File.WriteAllText(jpg, "not an image");
            File.SetLastWriteTime(rw2, new DateTime(2030, 1, 1, 0, 0, 0));

            AppConfig source = new AppConfig();
            source.ResourceCategories.Add("照片");
            source.AddedItems.Add(new AddedEntry { Path = rw2, Name = "photo", Category = "照片", IsResource = true });
            source.AddedItems.Add(new AddedEntry { Path = jpg, Name = "plain", Category = "照片", IsResource = true });
            Check(source.SaveTo(path), "RW2 fixture export");
            AppConfig loaded = ConfigDatabase.Load(path); Check(!loaded.LoadFailed, loaded.DatabaseError);

            long rw2Id, jpgId;
            using (Sqlite db = new Sqlite(ConfigDatabase.DatabasePath(path), false))
            {
                rw2Id = Convert.ToInt64(db.Scalar("SELECT id FROM entries WHERE key=?", "add:" + rw2));
                jpgId = Convert.ToInt64(db.Scalar("SELECT id FROM entries WHERE key=?", "add:" + jpg));
                db.Execute("UPDATE catalog SET indexed=1,modified=111 WHERE item_id=?", rw2Id);
                db.Execute("UPDATE listing SET modified=111 WHERE item_id=?", rw2Id);
                db.Execute("UPDATE catalog SET indexed=1,modified=222 WHERE item_id=?", jpgId);
                db.Execute("UPDATE listing SET modified=222 WHERE item_id=?", jpgId);
                db.Execute("DELETE FROM metadata WHERE key='rw2_taken_date_index_v1'");
            }

            AppConfig refreshed = ConfigDatabase.Load(path); Check(!refreshed.LoadFailed, refreshed.DatabaseError);
            using (Sqlite db = new Sqlite(ConfigDatabase.DatabasePath(path), true))
            {
                Check(db.Scalar("SELECT indexed FROM catalog WHERE item_id=?", rw2Id) == "0", "RW2 was not queued for reindex");
                Check(db.Scalar("SELECT indexed FROM catalog WHERE item_id=?", jpgId) == "1", "non-RW2 was reindexed");
                Check(db.Scalar("SELECT value FROM metadata WHERE key='rw2_taken_date_index_v1'") == "1", "migration marker missing");
                Check(db.Scalar("SELECT modified FROM listing WHERE item_id=?", rw2Id) == "111", "listing changed before reindex");
            }

            refreshed.Database.IndexMetadata(null);
            long expected = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Unspecified).ToUniversalTime().Ticks;
            using (Sqlite db = new Sqlite(ConfigDatabase.DatabasePath(path), true))
            {
                Check(Convert.ToInt64(db.Scalar("SELECT modified FROM catalog WHERE item_id=?", rw2Id)) == expected,
                    "RW2 catalog date not refreshed");
                Check(Convert.ToInt64(db.Scalar("SELECT modified FROM listing WHERE item_id=?", rw2Id)) == expected,
                    "RW2 listing date not refreshed");
                Check(Convert.ToInt64(db.Scalar("SELECT modified FROM listing WHERE item_id=?", jpgId)) == 222,
                    "non-RW2 listing date changed");
            }

            AppConfig again = ConfigDatabase.Load(path); Check(!again.LoadFailed, again.DatabaseError);
            using (Sqlite db = new Sqlite(ConfigDatabase.DatabasePath(path), true))
                Check(db.Scalar("SELECT indexed FROM catalog WHERE item_id=?", rw2Id) == "1", "migration was not idempotent");
        }
        private static void UiPaging()
        {
            AppConfig c = ConfigDatabase.Load(Path.Combine(root, "paging", "config.xml"));
            using (MainForm form = new MainForm(c))
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                Type type = typeof(MainForm);
                type.GetMethod("BuildControls", flags).Invoke(form, null);
                type.GetField("contentMode", flags).SetValue(form, Controls.SidebarControl.ContentMode.Website);
                type.GetField("currentCategory", flags).SetValue(form, "照片");
                MethodInfo read = type.GetMethod("ReadDatabasePage", flags);
                List<AppInfo> first = (List<AppInfo>)read.Invoke(form, new object[] { "" });
                type.GetField("shownApps", flags).SetValue(form, first);
                type.GetField("databasePageNumber", flags).SetValue(form, 1);
                List<AppInfo> next = (List<AppInfo>)read.Invoke(form, new object[] { "" });
                Check(first.Count == 200 && next.Count == 200 && !first.Any(a => next.Any(b => a.Key == b.Key)), "UI loaded all rows or duplicate page");
            }
        }
        private static void MiniMusic()
        {
            AppConfig c = Fixture("mini-music");
            string ordinary = Path.Combine(Folder("music-files"), "ordinary.wav");
            string resource = Path.Combine(root, "music-files", "resource.mp3");
            File.WriteAllText(ordinary, ""); File.WriteAllText(resource, "");
            c.AddedItems.Add(new AddedEntry { Path = ordinary, Name = "ordinary", Category = "工具" });
            c.AddedItems.Add(new AddedEntry { Path = resource, Name = "resource", Category = "照片", IsResource = true });
            c.ItemCategories["add:" + resource] = new List<string> { "col:照片/旅行" };
            Check(c.Save(), c.DatabaseError);
            string gone = Path.Combine(root, "music-files", "gone.mp3");
            using (MainForm form = new MainForm(c))
            {
                IntPtr handle = form.Handle;   // 结果经 RunOnUi 回到界面线程，需要句柄
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(MainForm).GetField("allApps", flags).SetValue(form, new List<AppInfo> {
                    new AppInfo { Key = "add:" + ordinary, TargetPath = ordinary, Name = "ordinary", IsFile = true },
                    new AppInfo { Key = "add:" + gone, TargetPath = gone, Name = "gone", IsFile = true }
                });
                // 曲库查询在后台执行：调用立即返回，结果在界面线程逐个送达
                Dictionary<string, List<string>> results = new Dictionary<string, List<string>>();
                int uiThread = System.Threading.Thread.CurrentThread.ManagedThreadId; bool onUi = true;
                typeof(MainForm).GetMethod("QueryMiniMusicSongs", flags).Invoke(form, new object[] { new List<string> { "", "col:照片/旅行" },
                    new Action<string, List<string>>(delegate(string key, List<string> songs)
                    { results[key] = songs; onUi &= System.Threading.Thread.CurrentThread.ManagedThreadId == uiThread; }) });
                Check(results.Count == 0, "song lists were queried synchronously on the UI thread");
                Stopwatch wait = Stopwatch.StartNew();
                while (results.Count < 2 && wait.ElapsedMilliseconds < 10000) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                Check(results.Count == 2 && onUi, "song lists were not delivered on the UI thread");
                List<string> all = results[""];
                Check(all.Count == 2 && all.Contains(ordinary) && all.Contains(resource), "audio missing, repeated or missing file kept");
                List<string> collection = results["col:照片/旅行"];
                Check(collection.Count == 1 && collection[0] == resource, "collection membership lost");
            }
            // 纯函数部分：扫描条目与库内条目去重（大小写不同也算同一路径），不存在的文件剔除
            List<string> merged = MainForm.CollectMiniMusicSongs(c.Database, "", false, new string[] { ordinary.ToUpperInvariant(), gone, null, "" });
            Check(merged.Count == 2 && merged.Contains(resource) && merged.Contains(ordinary), "song merge kept duplicates or missing files");
        }
        private static void ExportFailure()
        {
            AppConfig c = Fixture("export-failure");
            string target = Path.Combine(root, "export.ambak");
            Check(c.ExportTo(target) && AppConfig.ImportFrom(target) != null, "valid export failed");
            using (FileStream locked = new FileStream(target + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                Check(!c.ExportTo(target), "failed export reported success");
            Check(AppConfig.ImportFrom(target) != null, "failed export damaged previous backup");
        }
        private static void Corruption()
        {
            string path = Path.Combine(Folder("badxml"), "config.xml"); File.WriteAllText(path, "<broken>");
            Check(ConfigDatabase.Load(path).LoadFailed && !File.Exists(ConfigDatabase.DatabasePath(path)), "bad XML initialized DB");
            string db = ConfigDatabase.DatabasePath(Path.Combine(Folder("baddb"), "config.xml")); File.WriteAllText(db, "not sqlite");
            Check(ConfigDatabase.Load(Path.Combine(root, "baddb", "config.xml")).LoadFailed && File.ReadAllText(db) == "not sqlite", "bad DB overwritten");
        }
        private static void AddedEdits()
        {
            AppConfig c = Fixture("edits");
            AddedEntry entry = c.AddedItems.ByPath("C:\\image.jpg")[0];
            long item = entry.ItemId, row = entry.RecordId;
            entry.Name = "renamed"; Check(c.Save(), c.DatabaseError);
            Check(c.AddedItems.ByPath("C:\\image.jpg")[0].Name == "renamed", "field change was lost");
            c.AddedItems.Remove(c.AddedItems.ByPath("C:\\image.jpg")[0]); Check(c.Save(), c.DatabaseError);
            Check(c.AddedItems.Count == 1, "delete not committed");
            long operation = Convert.ToInt64(c.Database.History(1)[0][0]); c.Database.Undo(operation);
            AppConfig read = ConfigDatabase.Load(Path.Combine(root, "edits", "config.xml"));
            AddedEntry restored = read.AddedItems.ByPath("C:\\image.jpg")[0];
            Check(restored.Name == "renamed" && restored.RecordId == row && restored.ItemId == item, "rollback changed stable ids or content");
            Check(read.Database.Page("照片", true, true, "", 10, null, true, false).Total == 1, "rollback did not rebuild directory");
            Check(!c.Save(), "old in-memory state overwrote rollback");
            read.QueueEntryRename("add:C:\\image.jpg", "add:C:\\moved.jpg");
            restored.Path = "C:\\moved.jpg";
            read.ItemCategories["add:C:\\moved.jpg"] = read.ItemCategories["add:C:\\image.jpg"]; read.ItemCategories.Remove("add:C:\\image.jpg");
            Check(read.Save(), read.DatabaseError);
            Check(read.AddedItems.ByPath("C:\\moved.jpg")[0].ItemId == item, "path change replaced stable item identity");
            Check(read.Database.Page("col:照片/旅行", true, true, "", 10, null, true, false).Items[0].TargetPath == "C:\\moved.jpg", "path change broke collection");
        }
        private static void AtomicFailure()
        {
            AppConfig c = Fixture("atomic"); int count = c.AddedItems.Count, operations = c.Database.History(100).Count;
            using (Sqlite db = new Sqlite(c.Database.FilePath, false))
                db.Execute("CREATE TRIGGER test_failure BEFORE INSERT ON added_items WHEN NEW.name='fail' BEGIN SELECT RAISE(ABORT,'injected test failure'); END");
            c.AddedItems.Add(new AddedEntry { Path = "C:\\first.jpg", Name = "first", Category = "照片", IsResource = true });
            c.AddedItems.Add(new AddedEntry { Path = "C:\\second.jpg", Name = "fail", Category = "照片", IsResource = true });
            Check(!c.Save(), "injected failure accepted");
            AppConfig read = ConfigDatabase.Load(Path.Combine(root, "atomic", "config.xml"));
            Check(read.AddedItems.Count == count && read.Database.History(100).Count == operations, "partial transaction was committed");
            Check(c.AddedItems.Count == count, "failed save left dirty in-memory rows");
            using (Sqlite db = new Sqlite(c.Database.FilePath, false)) db.Execute("DROP TRIGGER test_failure");
            c.AddedItems.Add(new AddedEntry { Path = "C:\\retry.jpg", Name = "retry", Category = "照片", IsResource = true });
            Check(c.Save() && c.AddedItems.Count == count + 1, "retry after rollback failed");
            Check(c.AddedItems.ByPath("C:\\first.jpg").Count == 0, "reload kept uncommitted rows");
        }
        private static void CategoryConflicts()
        {
            AppConfig c = Fixture("category-conflicts");
            string key = "add:C:\\file.txt";
            c.CategoryOverride[key] = "办公软件";
            Check(c.Save(), c.DatabaseError);
            c.CategoryOverride[key] = "浏览器";
            Check(c.Save(), "second category move failed: " + c.DatabaseError);
            AppConfig read = Reload("category-conflicts");
            Check(read.CategoryOverride[key] == "浏览器", "second category move was lost");
            Check(read.Database.Page("浏览器", false, true, "", 10, null, true, false).Total == 1, "moved category projection missing");

            AppConfig rename = Fixture("rename-conflicts");
            rename.CustomCategories.Add("甲"); rename.CustomCategories.Add("乙");
            rename.CategoryOverride["add:C:\\file.txt"] = "甲";
            rename.CategoryOverride["add:C:\\image.jpg"] = "乙";
            Check(rename.Save(), rename.DatabaseError);
            rename.QueueCategoryRename("甲", "乙");
            Check(!rename.Save() && !string.IsNullOrEmpty(rename.DatabaseError), "rename onto a used category was accepted");
            Check(rename.CustomCategories.Contains("甲") && rename.CustomCategories.Contains("乙"), "failed rename polluted memory");
            Check(rename.Save(), "used rename target poisoned the next save: " + rename.DatabaseError);
        }

        private static void DuplicateMembership()
        {
            AppConfig c = Fixture("duplicate-membership");
            string path = Path.Combine(root, "duplicate-membership", "dup.txt");
            File.WriteAllText(path, "x");
            AddedEntry first = new AddedEntry { Path = path, Category = "照片", IsResource = true };
            AddedEntry second = new AddedEntry { Path = path, Category = "文件", IsResource = true };
            c.AddedItems.Add(first); c.AddedItems.Add(second);
            Check(c.Save(), c.DatabaseError);
            second.Category = "照片";
            Check(c.Save(), "duplicate membership merge failed: " + c.DatabaseError);
            AppConfig read = Reload("duplicate-membership");
            Check(read.AddedItems.ByPath(path).Count == 1, "duplicate membership remained: " + read.AddedItems.ByPath(path).Count);
            Check(read.Database.Page("照片", true, true, "", 10, null, true, false).Total == 2, "merged category listing missing");
            Check(read.Database.Page("文件", true, true, "", 10, null, true, false).Total == 0, "old category listing remained");
            Check(read.Database.Check().Contains("foreign_key_errors=0"), "merge broke foreign keys");
        }

        private static string NocasePlan(string path, string sql)
        {
            using (Sqlite s = new Sqlite(path, true))
                return string.Join(" | ", s.Query("EXPLAIN QUERY PLAN " + sql, "add:C:\\X", 1).Select(r => r[r.Length - 1]).ToArray());
        }

        private static void NocaseIndex()
        {
            AppConfig c = Fixture("nocase-index");
            string lookup = NocasePlan(c.Database.FilePath, "SELECT id FROM entries WHERE key=? COLLATE NOCASE AND id<>? LIMIT 1");
            Check(lookup.Contains("ix_entries_key_nocase"), "case-insensitive lookup scans the table: " + lookup);
            // 旧快照没有该索引：在线恢复后必须补齐，否则恢复后批量收录又退化成全表扫描。
            string snapshot = Path.Combine(root, "nocase-index", "snapshot.db");
            c.Database.Backup(snapshot);
            using (Sqlite s = new Sqlite(snapshot, false)) s.Execute("DROP INDEX ix_entries_key_nocase");
            c.Database.Restore(snapshot);
            using (Sqlite s = new Sqlite(c.Database.FilePath, true))
                Check(s.Scalar("SELECT count(*) FROM sqlite_master WHERE type='index' AND name='ix_entries_key_nocase'") == "1",
                    "online restore dropped the case-insensitive index");
        }

        private static void BatchSaveFailure()
        {
            AppConfig c = Fixture("batch-failure");
            int raised = 0;
            c.SaveFailed += delegate { raised++; };
            // 另一个写入者推进修订号：本会话接下来的保存都会被拒绝。
            using (Sqlite s = new Sqlite(c.Database.FilePath, false))
                s.Execute("UPDATE metadata SET value=CAST(value AS INTEGER)+1 WHERE key='revision'");
            c.Favorites.Add("batch-pending");
            bool first, second;
            string error;
            AppConfig.BeginSaveBatch();
            try { first = c.Save("batch.first"); second = c.Save("batch.second"); }
            finally { error = AppConfig.EndSaveBatch(); }
            Check(!first && !second, "stale batch save was accepted");
            Check(raised == 0 && c.Favorites.Contains("batch-pending"), "batch failure reloaded or notified on the worker thread");
            Check(error != null && AppConfig.EndSaveBatch() == null, "batch failure was not reported exactly once");
            c.ReportSaveFailure(error);
            Check(raised == 1 && !c.Favorites.Contains("batch-pending"), "report did not restore committed state");
            c.Favorites.Add("after-report");
            Check(c.Save("after.report"), c.DatabaseError);
            // 批量范围外的失败保持原语义：立即恢复并通知。
            using (Sqlite s = new Sqlite(c.Database.FilePath, false))
                s.Execute("UPDATE metadata SET value=CAST(value AS INTEGER)+1 WHERE key='revision'");
            c.Favorites.Add("immediate-pending");
            Check(!c.Save("immediate") && raised == 2 && !c.Favorites.Contains("immediate-pending"), "non-batch failure lost its reload");
        }

        private static void RecycleDetection()
        {
            Check(!Launcher.CanUseRecycleBin(@"\\server\share\file.txt"), "UNC path treated as recyclable");
            Check(!Launcher.CanUseRecycleBin(null) && !Launcher.CanUseRecycleBin(""), "empty path treated as recyclable");
            string local = Path.Combine(root, "recycle-detect.txt");
            DriveInfo drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(local)));
            Check(Launcher.CanUseRecycleBin(local) == (drive.IsReady && drive.DriveType == DriveType.Fixed),
                "fixed local drive detection depends on per-user recycle folder");
        }

        private static bool Listed(AppConfig c, string category, bool resources, string key)
        {
            return c.Database.Page(category, resources, true, "", 200, null, true, false).Items.Any(a => a.Key == key);
        }

        private static void CaseDuplicateMerge()
        {
            string path = Path.Combine(Folder("case-merge"), "config.xml");
            const string key = "add:C:\\Case\\File.txt";
            AppConfig seed = new AppConfig();
            seed.CustomCategories.Add("工具");
            seed.AddedItems.Add(new AddedEntry { Path = "C:\\Case\\File.txt", Name = "File", Category = "工具" });
            seed.Favorites.Add(key);
            seed.ItemOrder["工具"] = new List<string> { key };
            Check(seed.SaveTo(path), "fixture export");
            AppConfig c = ConfigDatabase.Load(path); Check(!c.LoadFailed, c.DatabaseError);
            string file = c.Database.FilePath;
            // 模拟修复前留下的数据：小写变体有自己的条目、收录记录（与保留条目重复的“工具”+ 新的资源“照片”）、收藏和排序
            using (Sqlite s = new Sqlite(file, false))
            {
                s.Execute("BEGIN IMMEDIATE");
                s.Execute("INSERT INTO operations(utc,action,actor,session,pid) VALUES('t','legacy','t','t',0)");
                s.Execute("UPDATE write_context SET operation_id=? WHERE id=1", s.LastId);
                s.Execute("INSERT INTO entries(key) VALUES(?)", "add:c:\\case\\file.txt");
                long dup = Convert.ToInt64(s.Scalar("SELECT id FROM entries WHERE key=?", "add:c:\\case\\file.txt"));
                s.Execute("INSERT OR IGNORE INTO groups(key,name) VALUES('照片','照片')");
                long tools = Convert.ToInt64(s.Scalar("SELECT id FROM groups WHERE key='工具'"));
                long photos = Convert.ToInt64(s.Scalar("SELECT id FROM groups WHERE key='照片'"));
                const string insertAdded = "INSERT INTO added_items(item_id,group_id,path,path_key,name,folder,resource) VALUES(?,?,?,?,?,0,?)";
                s.Execute(insertAdded, dup, tools, "c:\\case\\file.txt", "C:\\CASE\\FILE.TXT", "file", 0);
                s.Execute(insertAdded, dup, photos, "c:\\case\\file.txt", "C:\\CASE\\FILE.TXT", "file", 1);
                const string insertRecord = "INSERT INTO records(record_key,section,item_id,group_id,ordinal,slot,payload) VALUES(?,?,?,?,?,'','<Item />')";
                s.Execute(insertRecord, "Favorites:" + dup + ":0:", "Favorites", dup, null, 0);
                s.Execute(insertRecord, "ItemOrders:" + dup + ":" + tools + ":", "ItemOrders", dup, tools, 1);
                s.Execute("UPDATE write_context SET operation_id=NULL WHERE id=1");
                s.Execute("UPDATE metadata SET value=CAST(value AS INTEGER)+1 WHERE key='revision'");
                s.Execute("DELETE FROM metadata WHERE key='entries_case_merged'");
                s.Execute("COMMIT");
            }
            AppConfig r = ConfigDatabase.Load(path); Check(!r.LoadFailed, r.DatabaseError);
            using (Sqlite s = new Sqlite(file, true))
            {
                Check(s.Scalar("SELECT count(*) FROM entries WHERE key=? COLLATE NOCASE", key) == "1", "case variants were not merged");
                Check(s.Scalar("SELECT count(*) FROM added_items WHERE path_key='C:\\CASE\\FILE.TXT'") == "2"
                    && s.Scalar("SELECT count(DISTINCT item_id) FROM added_items WHERE path_key='C:\\CASE\\FILE.TXT'") == "1", "memberships were lost, duplicated or still split");
                Check(s.Scalar("SELECT count(*) FROM metadata WHERE key='entries_case_merged' AND value='1'") == "1", "merge flag missing");
            }
            Check(r.Favorites.Count(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) == 1, "favorite duplicated or lost");
            Check(r.ItemOrder["工具"].Count == 1, "order kept both variants");
            Func<string, bool, bool> listed = delegate(string category, bool resources)
            {
                return r.Database.Page(category, resources, true, "", 200, null, true, false).Items
                    .Count(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase)) == 1;
            };
            Check(listed("工具", false) && listed("照片", true), "merged item is not listed once in each mode");
            long revision = r.Database.Revision;
            AppConfig again = ConfigDatabase.Load(path);
            Check(!again.LoadFailed && again.Database.Revision == revision, "merge ran again on an already merged database");
            // 迁移事务不能逐项撤销（完成标记不受审计，撤销后数据与标记会不一致）
            long merge = again.Database.History(20).Where(h => h[2] == "migration.entries.case").Select(h => Convert.ToInt64(h[0])).First();
            string undoError = null;
            try { again.Database.Undo(merge); } catch (IOException ex) { undoError = ex.Message; }
            Check(undoError != null && undoError.Contains("迁移"), "migration transaction could be undone: " + undoError);
        }

        private static void CrossModeSplit()
        {
            string path = Path.Combine(Folder("cross-split"), "config.xml");
            const string tool = "add:C:\\split\\tool.exe", pic = "add:C:\\split\\pic.jpg", both = "add:C:\\split\\both.txt", doc = "add:C:\\split\\doc.pdf";
            AppConfig seed = new AppConfig();
            seed.CustomCategories.Add("工具"); seed.ResourceCategories.Add("工具");   // 两边都是自定义分类
            seed.CustomCategories.Add("照片");                                        // 软件自定义分类与资源内置分类同名
            seed.AddedItems.Add(new AddedEntry { Path = "C:\\split\\tool.exe", Name = "tool", Category = "工具" });
            seed.AddedItems.Add(new AddedEntry { Path = "C:\\split\\pic.jpg", Name = "pic", Category = "工具", IsResource = true });
            seed.AddedItems.Add(new AddedEntry { Path = "C:\\split\\both.txt", Name = "both", Category = "工具" });
            seed.AddedItems.Add(new AddedEntry { Path = "C:\\split\\both.txt", Name = "both", Category = "文件", IsResource = true });
            seed.AddedItems.Add(new AddedEntry { Path = "C:\\split\\doc.pdf", Name = "doc", Category = "文档", IsResource = true });
            seed.CategoryOverride[doc] = "工具";
            seed.ItemOrder["工具"] = new List<string> { tool, pic, both };
            seed.ItemCategories[both] = new List<string> { "工具" };
            seed.Collections.Add(new ResourceCollection { Parent = "工具", Name = "旅行" });
            seed.ItemCategories[pic] = new List<string> { CollectionKeys.Make("工具", "旅行") };
            seed.CategoryViewMode["工具"] = false;
            seed.CategoryIconNames[CategoryIcons.ConfigKey(true, "工具")] = "Star";
            seed.CategoryIconNames[CategoryIcons.ConfigKey(false, "工具")] = "Code";
            Check(seed.SaveTo(path), "fixture export");
            AppConfig c = ConfigDatabase.Load(path); Check(!c.LoadFailed, c.DatabaseError);
            List<string[]> renamed = c.SeparateCrossModeCategories();
            Check(renamed.Count == 2, "expected two splits, got " + renamed.Count);
            Check(c.Save("migration.categories.split"), c.DatabaseError);

            AppConfig r = ConfigDatabase.Load(path); Check(!r.LoadFailed, r.DatabaseError);
            Check(r.CustomCategories.Contains("工具") && r.ResourceCategories.Contains("工具（资源）") && !r.ResourceCategories.Contains("工具"), "resource side was not renamed");
            Check(r.CustomCategories.Contains("照片（软件）") && !r.CustomCategories.Contains("照片"), "software side of a built-in clash was not renamed");
            Func<string, bool, string> categoryOf = delegate(string p, bool resource) { return r.AddedItems.ByPath(p).First(e => e.IsResource == resource).Category; };
            Check(categoryOf("C:\\split\\tool.exe", false) == "工具" && categoryOf("C:\\split\\pic.jpg", true) == "工具（资源）", "added items moved on the wrong side");
            Check(categoryOf("C:\\split\\both.txt", false) == "工具", "software record of a dual-mode file was moved");
            Check(r.CategoryOverride[doc] == "工具（资源）", "resource override did not follow");
            Check(r.ItemOrder["工具"].SequenceEqual(new[] { tool, both }) && r.ItemOrder["工具（资源）"].SequenceEqual(new[] { pic, both }), "order was not split by mode");
            Check(r.ItemCategories[both].Contains("工具") && r.ItemCategories[both].Contains("工具（资源）"), "dual-mode membership not kept on both sides");
            Check(r.Collections[0].Parent == "工具（资源）" && r.ItemCategories[pic].Contains(CollectionKeys.Make("工具（资源）", "旅行")), "collection did not follow");
            Check(!r.CategoryViewMode["工具（资源）"] && r.CategoryIconNames[CategoryIcons.ConfigKey(true, "工具（资源）")] == "Star"
                && r.CategoryIconNames[CategoryIcons.ConfigKey(false, "工具")] == "Code", "view mode or icon not migrated");
            Check(Listed(r, "工具（资源）", true, pic) && Listed(r, "工具", false, tool) && !Listed(r, "工具", true, pic), "listing was not split");
            Check(r.SeparateCrossModeCategories().Count == 0, "split is not idempotent");
        }

        private static void CrossModeMove()
        {
            const string key = "add:C:\\file.txt";
            AppConfig c = Fixture("cross-move");
            c.CategoryOverride.Remove(key);
            // 同一文件既在软件分类“工具”，又跨模式复制到了资源分类“照片”
            c.AddedItems.Add(new AddedEntry { Path = "C:\\file.txt", Name = "file", Category = "照片", IsResource = true });
            Check(c.Save("cross.copy"), c.DatabaseError);
            Check(Listed(c, "工具", false, key) && Listed(c, "照片", true, key), "cross-mode copy not listed in both modes");
            // 在资源模式里移动到“文档”：资源侧离开“照片”，软件侧“工具”必须保留
            c.CategoryOverride[key] = "文档";
            Check(c.Save("cross.move.resource"), c.DatabaseError);
            Check(Listed(c, "文档", true, key) && !Listed(c, "照片", true, key), "resource move did not move within resources");
            Check(Listed(c, "工具", false, key), "resource move removed the software category");
            // 在软件模式里移动到自定义软件分类：软件侧离开“工具”，资源侧“照片”保留
            c.CustomCategories.Add("回归软件");
            c.CategoryOverride[key] = "回归软件";
            Check(c.Save("cross.move.software"), c.DatabaseError);
            Check(Listed(c, "回归软件", false, key) && !Listed(c, "工具", false, key), "software move did not move within software");
            Check(Listed(c, "照片", true, key) && !Listed(c, "回归软件", true, key), "software move changed the resource side");
        }

        private static void DesktopOperationDetection()
        {
            Check(!DesktopOrganizer.IsOperationRunning, "idle lock reported busy");
            using (System.Threading.ManualResetEvent held = new System.Threading.ManualResetEvent(false))
            using (System.Threading.ManualResetEvent release = new System.Threading.ManualResetEvent(false))
            {
                System.Threading.Thread worker = new System.Threading.Thread(delegate()
                {
                    DesktopOrganizer.RunExclusive(delegate { held.Set(); release.WaitOne(10000); });
                });
                worker.Start();
                Check(held.WaitOne(10000), "worker did not take the lock");
                bool busy = DesktopOrganizer.IsOperationRunning;
                release.Set();
                worker.Join(10000);
                Check(busy, "running operation not detected");
            }
            Check(!DesktopOrganizer.IsOperationRunning, "lock still reported busy after the operation");
        }

        private static void PhysicalUndo()
        {
            AppConfig c = Fixture("physical-undo");
            c.Favorites.Add("physical-kept");
            Check(c.Save("physical.files.move"), c.DatabaseError);
            long operation = Convert.ToInt64(c.Database.History(1)[0][0]);
            bool rejected = false;
            try { c.Database.Undo(operation); }
            catch (IOException) { rejected = true; }
            Check(rejected, "physical transaction was undone without its files");
            AppConfig read = c.Database.Read();
            Check(read.Favorites.Contains("physical-kept"), "rejected undo changed data");
        }

        private static void AmbakSanitize()
        {
            AppConfig local = Fixture("ambak-local");
            Directory.CreateDirectory(Paths.DesktopBackupDir);
            string desktop = Path.Combine(DesktopOrganizer.UserDesktop, "ambak-" + Guid.NewGuid().ToString("N") + ".txt");
            if (!DesktopOrganizer.IsSafeRestoreTarget(desktop))
                desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                    "ambak-" + Guid.NewGuid().ToString("N") + ".txt");
            Check(DesktopOrganizer.IsSafeRestoreTarget(desktop), "no safe desktop fixture path");

            string kept = Path.Combine(Paths.DesktopBackupDir, "kept.txt");
            string unsafeOriginal = Path.Combine(Paths.DesktopBackupDir, "unsafe-original.txt");
            string outside = Path.Combine(root, "ambak-outside.txt");
            File.WriteAllText(kept, "kept"); File.WriteAllText(unsafeOriginal, "unsafe"); File.WriteAllText(outside, "outside");

            string localStoreOne = Path.Combine(root, "ambak-store-one"), localStoreTwo = Path.Combine(root, "ambak-store-two");
            local.ResourceStoreLocations.Add(new ResourceStoreLocation(localStoreOne, true));
            local.ResourceStoreLocations.Add(new ResourceStoreLocation(localStoreTwo, false));
            local.ResourceStorePath = localStoreOne;

            AppConfig imported = new AppConfig();
            imported.ManageRootPath = "D:\\foreign";
            imported.FolderStorePath = "D:\\foreign-folders";
            imported.FileStorePath = "D:\\foreign-files";
            imported.ResourceStorePath = "D:\\foreign-resources";
            imported.DesktopBackup.Add(new DesktopBackupEntry { CurrentPath = kept, OriginalPath = desktop, IsCopy = true });
            imported.DesktopBackup.Add(new DesktopBackupEntry { CurrentPath = outside, OriginalPath = desktop, IsCopy = true });
            imported.DesktopBackup.Add(new DesktopBackupEntry { CurrentPath = unsafeOriginal, OriginalPath = outside, IsCopy = true });
            int dropped;
            string error = imported.PrepareAmbakImport(local, out dropped);
            Check(error == null, "valid ambak rejected: " + error);
            Check(dropped == 2 && imported.DesktopBackup.Count == 1, "forged desktop rows were not dropped");
            Check(!imported.DesktopBackup[0].IsCopy, "imported copy flag was trusted");
            Check(imported.ManageRootPath == local.ManageRootPath && imported.FolderStorePath == local.FolderStorePath
                && imported.FileStorePath == local.FileStorePath && imported.ResourceStorePath == local.ResourceStorePath,
                "import overwrote local storage paths");
            Check(imported.ResourceStoreLocations.Count == 2
                && imported.ResourceStoreLocations[0].Path == localStoreOne && imported.ResourceStoreLocations[0].Enabled
                && imported.ResourceStoreLocations[1].Path == localStoreTwo && !imported.ResourceStoreLocations[1].Enabled,
                "import did not keep the local storage location pool");

            Check(local.Save("ambak.local.store"), local.DatabaseError);
            AppConfig localReloaded = Reload("ambak-local");
            CheckStoreLocations(localReloaded, local.ResourceStoreLocations, "local ambak storage pool");
            CheckCanonicalStorageRecord(localReloaded, 2, "local ambak storage pool");

            string importedPath = Path.Combine(Folder("ambak-imported"), "config.xml");
            Check(imported.SaveTo(importedPath), "imported ambak XML export failed");
            AppConfig importedDb = ConfigDatabase.Load(importedPath);
            Check(!importedDb.LoadFailed, importedDb.DatabaseError);
            AppConfig importedReloaded = ConfigDatabase.Load(importedPath);
            CheckStoreLocations(importedReloaded, imported.ResourceStoreLocations, "imported ambak storage pool");
            CheckCanonicalStorageRecord(importedReloaded, 2, "imported ambak storage pool");

            AppConfig bad = new AppConfig(); bad.CustomCategories.Add("Bad/Name");
            Check(bad.PrepareAmbakImport(local, out dropped) != null, "slash category accepted");
            AppConfig cross = new AppConfig(); cross.CustomCategories.Add("Same"); cross.ResourceCategories.Add("Same");
            Check(cross.PrepareAmbakImport(local, out dropped) != null, "cross-mode duplicate accepted");
            AppConfig reserved = new AppConfig(); reserved.CustomCategories.Add("全部文件");
            Check(reserved.PrepareAmbakImport(local, out dropped) != null, "software reserved name accepted");
            AppConfig reservedResource = new AppConfig(); reservedResource.ResourceCategories.Add("全部文件");
            Check(reservedResource.PrepareAmbakImport(local, out dropped) != null, "resource category accepted a software reserved name");

            string oversized = Path.Combine(root, "oversized.ambak");
            try
            {
                using (FileStream file = new FileStream(oversized, FileMode.Create, FileAccess.Write, FileShare.None))
                    file.SetLength(SafeXml.MaxXmlBytes + 1);
                bool rejected = false;
                try { SafeXml.Load(oversized); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected && AppConfig.ImportFrom(oversized) == null, "oversized XML was accepted");
            }
            finally { try { if (File.Exists(oversized)) File.Delete(oversized); } catch { } }
        }

        private const string StorageRecordKey = "Settings:0:0:ResourceStoreLocations";
        private const string LegacyStorageRecordKey = "ResourceStoreLocations:0:0:";

        private static void StorageLocationPoolSqlite()
        {
            string path = Path.Combine(Folder("store-locations-sqlite"), "config.xml");
            AppConfig empty = ConfigDatabase.Load(path);
            Check(!empty.LoadFailed, empty.DatabaseError);
            Check(empty.Save("store.empty"), empty.DatabaseError);
            CheckCanonicalStorageRecord(empty, 0, "empty storage pool");
            CheckStoreLocations(ConfigDatabase.Load(path), new List<ResourceStoreLocation>(), "empty storage pool");

            string first = "S:\\StoreOne";
            List<ResourceStoreLocation> one = new List<ResourceStoreLocation> { new ResourceStoreLocation(first, true) };
            AppConfig c = ConfigDatabase.Load(path);
            c.ResourceStoreLocations = one;
            Check(c.Save("store.one"), c.DatabaseError);
            AppConfig loaded = ConfigDatabase.Load(path);
            CheckStoreLocations(loaded, one, "single storage pool");
            CheckCanonicalStorageRecord(loaded, 1, "single storage pool");

            string disabled = "S:\\StoreDisabled";
            string escaped = "S:\\资料 & '精选'\\位置";
            List<ResourceStoreLocation> three = new List<ResourceStoreLocation>
            {
                new ResourceStoreLocation(first, true),
                new ResourceStoreLocation(disabled, false),
                new ResourceStoreLocation(escaped, true)
            };
            c = ConfigDatabase.Load(path);
            c.ResourceStoreLocations = three;
            Check(c.Save("store.three"), c.DatabaseError);
            loaded = ConfigDatabase.Load(path);
            CheckStoreLocations(loaded, three, "three storage locations");
            string payload = CheckCanonicalStorageRecord(loaded, 3, "three storage locations");
            Check(payload.Contains("&amp;"), "storage path ampersand was not XML escaped");

            List<ResourceStoreLocation> reordered = new List<ResourceStoreLocation>
            {
                new ResourceStoreLocation(escaped, true),
                new ResourceStoreLocation(disabled, true),
                new ResourceStoreLocation(first, false)
            };
            c = ConfigDatabase.Load(path);
            c.ResourceStoreLocations = reordered;
            Check(c.Save("store.reorder"), c.DatabaseError);
            loaded = ConfigDatabase.Load(path);
            CheckStoreLocations(loaded, reordered, "reordered storage locations");
            CheckCanonicalStorageRecord(loaded, 3, "reordered storage locations");

            List<ResourceStoreLocation> disabledAll = new List<ResourceStoreLocation>
            {
                new ResourceStoreLocation(first, false),
                new ResourceStoreLocation(disabled, false),
                new ResourceStoreLocation(escaped, false)
            };
            c = ConfigDatabase.Load(path);
            c.ResourceStoreLocations = disabledAll;
            Check(c.Save("store.disabled"), c.DatabaseError);
            loaded = ConfigDatabase.Load(path);
            CheckStoreLocations(loaded, disabledAll, "all-disabled storage locations");
            Check(ResourceStore.ActiveRoots(loaded, "S:\\fallback").Count == 0, "all-disabled pool accepted new files");
            CheckCanonicalStorageRecord(loaded, 3, "all-disabled storage locations");

            List<ResourceStoreLocation> many = new List<ResourceStoreLocation>();
            for (int i = 0; i < 100; i++)
                many.Add(new ResourceStoreLocation(Path.Combine("S:\\Stores", "位置-" + i), i % 7 != 0));
            c = ConfigDatabase.Load(path);
            c.ResourceStoreLocations = many;
            Check(c.Save("store.hundred"), c.DatabaseError);
            loaded = ConfigDatabase.Load(path);
            CheckStoreLocations(loaded, many, "hundred storage locations");
            CheckCanonicalStorageRecord(loaded, 100, "hundred storage locations");

            string conflictPath = Path.Combine(Folder("store-locations-conflict"), "config.xml");
            AppConfig conflict = ConfigDatabase.Load(conflictPath);
            conflict.Collections.Add(new ResourceCollection { Parent = "照片", Name = "重复", Description = "first" });
            conflict.Collections.Add(new ResourceCollection { Parent = "照片", Name = "重复", Description = "second" });
            Check(!conflict.Save("store.conflict"), "conflicting record key was saved");
            string error = conflict.DatabaseError ?? string.Empty;
            Check(error.Contains("配置记录键冲突") && error.IndexOf('<') < 0,
                "record key conflict error leaked XML: " + error);
            AppConfig conflictReloaded = ConfigDatabase.Load(conflictPath);
            Check(conflictReloaded.Collections.Count == 0, "failed conflict save changed collections");
            conflictReloaded.Collections.Add(new ResourceCollection { Parent = "照片", Name = "正常", Description = "ok" });
            Check(conflictReloaded.Save("store.conflict.recover"), conflictReloaded.DatabaseError);
            Check(ConfigDatabase.Load(conflictPath).Collections.Count == 1, "conflict guard rejected a valid save");
        }

        private static void StorageLocationUpgrade()
        {
            string path = Path.Combine(Folder("store-locations-upgrade"), "config.xml");
            string legacyPath = "S:\\Legacy & '旧位置'";
            string addedPath = "S:\\AddedLater";
            AppConfig seed = ConfigDatabase.Load(path);
            Check(!seed.LoadFailed, seed.DatabaseError);
            seed.ResourceStoreLocations.Add(new ResourceStoreLocation(legacyPath, true));
            Check(seed.Save("store.upgrade.seed"), seed.DatabaseError);
            CheckCanonicalStorageRecord(seed, 1, "storage upgrade seed");

            ReplaceStorageRecordWithLegacy(seed, legacyPath, true);
            AppConfig legacy = ConfigDatabase.Load(path);
            Check(!legacy.LoadFailed, legacy.DatabaseError);
            List<ResourceStoreLocation> expected = new List<ResourceStoreLocation> { new ResourceStoreLocation(legacyPath, true) };
            CheckStoreLocations(legacy, expected, "legacy storage record");
            CheckLegacyStorageRecord(legacy, 1, "legacy storage record");

            BumpRevision(legacy);
            legacy.ResourceStoreLocations.Add(new ResourceStoreLocation(addedPath, true));
            Check(!legacy.Save("store.upgrade.fail"), "stale storage upgrade save was accepted");
            CheckStoreLocations(legacy, expected, "storage upgrade failed save reload");
            CheckLegacyStorageRecord(legacy, 1, "storage upgrade failed save");

            legacy.ResourceStoreLocations.Add(new ResourceStoreLocation(addedPath, true));
            Check(legacy.Save("store.upgrade"), legacy.DatabaseError);
            expected.Add(new ResourceStoreLocation(addedPath, true));
            AppConfig upgraded = ConfigDatabase.Load(path);
            CheckStoreLocations(upgraded, expected, "upgraded storage locations");
            CheckCanonicalStorageRecord(upgraded, 2, "upgraded storage locations");

            BumpRevision(upgraded);
            upgraded.ResourceStoreLocations.RemoveAt(1);
            Check(!upgraded.Save("store.upgrade.after.success.fail"), "stale post-upgrade save was accepted");
            CheckStoreLocations(upgraded, expected, "post-upgrade failed save reload");
            CheckCanonicalStorageRecord(upgraded, 2, "post-upgrade failed save");
            CheckStoreLocations(ConfigDatabase.Load(path), expected, "post-upgrade persisted state");
        }

        private static void CheckStoreLocations(AppConfig config, IList<ResourceStoreLocation> expected, string label)
        {
            Check(config != null && !config.LoadFailed, label + " reload failed: " + (config == null ? "null" : config.DatabaseError));
            Check(config.ResourceStoreLocations.Count == expected.Count, label + " count changed");
            for (int i = 0; i < expected.Count; i++)
            {
                ResourceStoreLocation actual = config.ResourceStoreLocations[i];
                ResourceStoreLocation wanted = expected[i];
                Check(string.Equals(actual.Path, wanted.Path, StringComparison.OrdinalIgnoreCase)
                    && actual.Enabled == wanted.Enabled, label + " item " + i + " changed");
            }
            string legacy = string.Empty;
            for (int i = 0; i < expected.Count; i++)
                if (expected[i].Enabled) { legacy = expected[i].Path; break; }
            Check(string.Equals(config.ResourceStorePath ?? string.Empty, legacy, StringComparison.OrdinalIgnoreCase),
                label + " legacy path mismatch");
        }

        private static string CheckCanonicalStorageRecord(AppConfig config, int items, string label)
        {
            using (Sqlite db = new Sqlite(config.Database.FilePath, true))
            {
                Check(db.Scalar("SELECT count(*) FROM records WHERE record_key=?", StorageRecordKey) == "1",
                    label + " did not keep exactly one canonical record");
                Check(db.Scalar("SELECT count(*) FROM records WHERE section='ResourceStoreLocations' OR record_key LIKE 'ResourceStoreLocations:%'") == "0",
                    label + " left legacy storage records");
                string payload = db.Scalar("SELECT payload FROM records WHERE record_key=?", StorageRecordKey);
                System.Xml.XmlDocument xml = new System.Xml.XmlDocument(); xml.LoadXml(payload);
                Check(xml.DocumentElement != null && xml.DocumentElement.Name == "ResourceStoreLocations",
                    label + " canonical payload root is wrong");
                Check(xml.DocumentElement.SelectNodes("Item").Count == items, label + " canonical payload item count is wrong");
                return payload;
            }
        }

        private static void CheckLegacyStorageRecord(AppConfig config, int items, string label)
        {
            using (Sqlite db = new Sqlite(config.Database.FilePath, true))
            {
                Check(db.Scalar("SELECT count(*) FROM records WHERE record_key=?", StorageRecordKey) == "0",
                    label + " unexpectedly created a canonical record");
                Check(db.Scalar("SELECT count(*) FROM records WHERE record_key=?", LegacyStorageRecordKey) == "1",
                    label + " lost the legacy record");
                string payload = db.Scalar("SELECT payload FROM records WHERE record_key=?", LegacyStorageRecordKey);
                System.Xml.XmlDocument xml = new System.Xml.XmlDocument(); xml.LoadXml(payload);
                Check(xml.DocumentElement != null && xml.DocumentElement.Name == "Item", label + " legacy payload root is wrong");
                Check(items == 1, label + " fixture expects one legacy item");
            }
        }

        private static void ReplaceStorageRecordWithLegacy(AppConfig config, string path, bool enabled)
        {
            System.Xml.XmlDocument item = new System.Xml.XmlDocument();
            System.Xml.XmlElement node = item.CreateElement("Item");
            node.SetAttribute("path", path); node.SetAttribute("enabled", enabled ? "true" : "false");
            using (Sqlite db = new Sqlite(config.Database.FilePath, false))
            {
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    db.Execute("INSERT INTO operations(utc,action,actor,session,pid) VALUES(?,?,?,?,?)",
                        DateTime.UtcNow.ToString("o"), "fixture.legacy.store", "test", "test", 0);
                    long operation = db.LastId;
                    db.Execute("UPDATE write_context SET operation_id=? WHERE id=1", operation);
                    db.Execute("DELETE FROM records WHERE record_key=? OR section='ResourceStoreLocations'",
                        StorageRecordKey);
                    db.Execute("INSERT INTO records(record_key,section,ordinal,slot,payload) VALUES(?,?,0,'',?)",
                        LegacyStorageRecordKey, "ResourceStoreLocations", node.OuterXml);
                    db.Execute("UPDATE write_context SET operation_id=NULL WHERE id=1");
                    db.Execute("UPDATE metadata SET value=CAST(value AS INTEGER)+1 WHERE key='revision'");
                    db.Execute("COMMIT");
                }
                catch { db.Rollback(); throw; }
            }
        }

        private static void BumpRevision(AppConfig config)
        {
            using (Sqlite db = new Sqlite(config.Database.FilePath, false))
                db.Execute("UPDATE metadata SET value=CAST(value AS INTEGER)+1 WHERE key='revision'");
        }

        private static void BulkLookup()
        {
            AppConfig c = Fixture("bulk-lookup");
            string staged = Path.Combine(root, "bulk-lookup", "staged.txt");
            c.AddedItems.Add(new AddedEntry { Path = staged, Category = "工具" });
            Dictionary<string, List<AddedEntry>> found = c.AddedItems.LookupMany(new string[] { "C:\\FILE.TXT", staged.ToUpperInvariant(), staged.ToUpperInvariant() });
            Check(found["C:\\FILE.TXT"].Count == 1, "stored path lookup failed");
            Check(found[staged.ToUpperInvariant()].Count == 1, "staged path lookup failed or duplicated");
            c.AddedItems.Prefetch(new string[] { "C:\\FILE.TXT", staged.ToUpperInvariant() });
            Dictionary<string, List<AddedEntry>> cache = (Dictionary<string, List<AddedEntry>>)typeof(AddedEntryList)
                .GetField("pathCache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(c.AddedItems);
            Check(cache.ContainsKey("C:\\FILE.TXT") && cache["C:\\FILE.TXT"].Count == 1, "stored path was not cached by batch prefetch");
            Check(cache.ContainsKey(staged.ToUpperInvariant()) && cache[staged.ToUpperInvariant()].Count == 1, "staged path was not cached by batch prefetch");
        }

        // 导入预处理在后台直接读库：文件夹内收录项用 path_key 范围查询，已登记路径批量查询。
        private static void FolderRangeLookup()
        {
            AppConfig c = Fixture("range-lookup");
            c.AddedItems.Add(new AddedEntry { Path = "D:\\Lib\\Photos", Category = "工具", IsFolder = true });
            c.AddedItems.Add(new AddedEntry { Path = "D:\\Lib\\Photos\\a.jpg", Category = "工具" });
            c.AddedItems.Add(new AddedEntry { Path = "D:\\Lib\\Photos2\\b.jpg", Category = "工具" });
            c.AddedItems.Add(new AddedEntry { Path = "D:\\Lib\\Photos_x\\c.jpg", Category = "工具" });
            c.AddedItems.Add(new AddedEntry { Path = "D:\\Lib\\Photos]\\d.jpg", Category = "工具" });
            c.AddedItems.Add(new AddedEntry { Path = "D:\\Empty\\e.jpg", Category = "工具" });
            Check(c.Save(), c.DatabaseError);
            string db = c.Database.FilePath;
            // 范围只含文件夹内部：不含文件夹自身，也不含同前缀的相邻文件夹（Photos2 / Photos_x / Photos]）。
            Check(AddedEntryList.HasEntriesUnder(db, "d:\\lib\\photos"), "lower-case folder missed its entries");
            Check(AddedEntryList.HasEntriesUnder(db, "D:\\Lib\\Photos\\"), "trailing separator missed entries");
            Check(!AddedEntryList.HasEntriesUnder(db, "D:\\Lib\\Photos\\a.jpg"), "file treated as folder with entries");
            Check(!AddedEntryList.HasEntriesUnder(db, "D:\\Lib\\Phot"), "partial name matched sibling folders");
            Check(!AddedEntryList.HasEntriesUnder(db, "D:\\Empty2"), "unrelated folder matched");
            Check(AddedEntryList.HasEntriesUnder(db, "D:\\"), "drive root missed entries");
            Check(!AddedEntryList.HasEntriesUnder(db, ""), "empty folder matched");
            List<string> under = c.AddedItems.EntriesUnder("D:\\LIB\\PHOTOS").Select(e => e.Path).ToList();
            Check(under.Count == 1 && under[0] == "D:\\Lib\\Photos\\a.jpg", "EntriesUnder range wrong: " + string.Join("|", under.ToArray()));
            Check(c.AddedItems.EntriesUnder("D:\\Lib\\Photos2").Count == 1 && c.AddedItems.EntriesUnder("D:\\Lib\\Photos]").Count == 1,
                "sibling folder entries lost");
            // 未保存的新增仍要算进去（界面线程路径）；后台读库只看已提交数据。
            c.AddedItems.Add(new AddedEntry { Path = "D:\\Empty\\staged.jpg", Category = "工具" });
            Check(c.AddedItems.EntriesUnder("d:\\empty").Count == 2, "staged entry missing from EntriesUnder");
            Dictionary<string, string> registered = AddedEntryList.RegisteredPaths(db,
                new string[] { "d:\\lib\\photos\\A.JPG", "D:\\LIB\\PHOTOS\\A.JPG", "D:\\missing.jpg", "", null });
            Check(registered.Count == 1 && registered["d:\\lib\\photos\\a.jpg"] == "D:\\Lib\\Photos\\a.jpg",
                "registered path lookup must map case variants to the stored path once");
            List<string> many = new List<string>();
            for (int i = 0; i < 600; i++) many.Add("D:\\none\\" + i + ".jpg");
            many.Add("D:\\EMPTY\\E.JPG");
            Check(AddedEntryList.RegisteredPaths(db, many).Count == 1, "lookup across 256-item chunks failed");
        }

        private static void CrashRecovery()
        {
            AppConfig c = Fixture("crash"); int operations = c.Database.History(100).Count;
            ProcessStartInfo info = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                "--crash-writer \"" + c.Database.FilePath + "\"") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            using (Process process = Process.Start(info))
            {
                if (!process.WaitForExit(15000)) { process.Kill(); throw new Exception("crash helper timeout"); }
            }
            using (Sqlite db = new Sqlite(c.Database.FilePath, false)) Check(db.Scalar("SELECT count(*) FROM groups WHERE key='uncommitted'") == "0", "uncommitted row survived crash");
            Check(c.Database.History(100).Count == operations && c.Database.Check().Contains("integrity=ok"), "crash damaged committed data or audit");
        }
        private static void MetadataSorting()
        {
            string folder = Folder("metadata"); AppConfig c = ConfigDatabase.Load(Path.Combine(folder, "config.xml"));
            for (int i = 0; i < 35; i++)
            {
                string path = Path.Combine(folder, "file" + i + ".txt"); File.WriteAllBytes(path, new byte[35 - i]);
                c.AddedItems.Add(new AddedEntry { Path = path, Category = "文档", IsResource = true });
            }
            Check(c.Save(), c.DatabaseError); c.Database.IndexMetadata(null);
            CatalogPage page = null; long previous = long.MaxValue; HashSet<string> seen = new HashSet<string>();
            do
            {
                page = c.Database.Page("文档", true, false, "", 7, page, false, true, "size");
                foreach (AppInfo app in page.Items)
                { long size = new FileInfo(app.TargetPath).Length; Check(size <= previous && seen.Add(app.Key), "size sort is local to page or duplicates rows"); previous = size; }
            } while (page.Items.Count > 0);
            Check(seen.Count == 35, "metadata sort lost rows");
            page = c.Database.Page("文档", true, false, "", 7, null, false, false, "size");
            Check(new FileInfo(page.Items[0].TargetPath).Length == 1, "ascending metadata order incorrect");
        }
        private static void OfflineRecovery()
        {
            AppConfig c = Fixture("recovery-source"); string snapshot = Path.Combine(root, "recovery-snapshot.db"); c.Database.Backup(snapshot);
            string target = Path.Combine(Folder("recovery-target"), "appmanager.db"); File.WriteAllText(target, "damaged-original");
            string safety = DatabaseRecovery.Restore(target, snapshot);
            Check(File.ReadAllText(safety) == "damaged-original", "damaged original was not preserved");
            AppConfig read = ConfigDatabase.Load(Path.Combine(root, "recovery-target", "config.xml"));
            Check(!read.LoadFailed && read.AddedItems.Count == 2 && read.Database.History(1)[0][2] == "restore.snapshot", "recovery did not restore or record data");
        }
        private static void LegacyFlags()
        {
            AppConfig c = Fixture("legacy-flags"); c.Hidden.Add("af:image"); c.Favorites.Add("af:image");
            Check(c.Save(), c.DatabaseError);
            Check(c.Database.Page("照片", true, false, "", 10, null, true, false).Total == 0, "legacy hidden not applied");
            c.Hidden.Remove("af:image"); c.Favorites.Remove("af:image"); Check(c.Save(), c.DatabaseError);
            Check(c.Database.Page("照片", true, false, "", 10, null, true, false).Total == 1, "legacy removal left hidden projection stale");
            Check(c.Database.Page("常用", true, true, "", 10, null, true, false).Total == 0, "legacy removal left favorite projection stale");
        }
        private static void Scale()
        {
            string path = Path.Combine(Folder("scale"), "config.xml"); AppConfig c = ConfigDatabase.Load(path);
            const int count = 300000; Stopwatch timer = Stopwatch.StartNew();
            for (int i = 0; i < count; i++) c.AddedItems.Add(new AddedEntry { Path = "Z:\\scale\\" + i + ".jpg", Name = "photo" + i.ToString("D6"), Category = "照片", IsResource = true });
            Check(c.Save(), c.DatabaseError); Console.WriteLine("SCALE import_ms=" + timer.ElapsedMilliseconds);
            timer.Restart(); CatalogPage page = c.Database.Page("照片", true, false, "", 200, null, false, false);
            Check(page.Total == count && page.Items.Count == 200, "large page count");
            Console.WriteLine("SCALE first_page_ms=" + timer.ElapsedMilliseconds);
            timer.Restart(); for (int i = 0; i < 100; i++) page = c.Database.Page("照片", true, false, "", 200, page, false, false);
            Console.WriteLine("SCALE 100_pages_ms=" + timer.ElapsedMilliseconds);
            timer.Restart(); c.ItemCategories["add:Z:\\scale\\123.jpg"] = new List<string> { "精选" }; Check(c.Save(), c.DatabaseError);
            Console.WriteLine("SCALE single_relation_save_ms=" + timer.ElapsedMilliseconds);
            using (Sqlite db = new Sqlite(c.Database.FilePath, true)) Check(Convert.ToInt32(db.Scalar("SELECT count(*) FROM changes WHERE operation_id=?", Convert.ToInt64(c.Database.History(1)[0][0]))) <= 5, "single update rewrote all rows");
            using (Sqlite db = new Sqlite(c.Database.FilePath, true))
            {
                string plan = string.Join(" | ", db.Query("EXPLAIN QUERY PLAN SELECT item_id FROM listing WHERE group_id=? AND kind=2 AND hidden=0 ORDER BY rank,name,item_id LIMIT 200", GroupId(c.Database, "照片")).Select(r => r[3]));
                Console.WriteLine("SCALE plan=" + plan); Check(plan.Contains("ix_listing_page") && !plan.Contains("TEMP B-TREE"), "page does not use ordered index");
            }
            timer.Restart(); AppConfig read = ConfigDatabase.Load(path); Check(read.AddedItems.Count == count, "restart lost data");
            Console.WriteLine("SCALE restart_ms=" + timer.ElapsedMilliseconds);
            Console.WriteLine("SCALE db_bytes=" + new FileInfo(c.Database.FilePath).Length);
        }
        private static string Canonical(AppConfig config)
        {
            System.Xml.XmlDocument doc = config.ToDocument();
            List<string> sections = new List<string>();
            foreach (System.Xml.XmlElement section in doc.DocumentElement.ChildNodes)
            {
                List<string> items = new List<string>();
                foreach (System.Xml.XmlNode node in section.ChildNodes) items.Add(node.OuterXml);
                items.Sort(StringComparer.Ordinal); sections.Add(section.Name + "|" + string.Join("\n", items.Distinct().ToArray()));
            }
            sections.Sort(StringComparer.Ordinal); return string.Join("\n", sections.ToArray());
        }
        private static void VerifyCopy(string source)
        {
            string copy = Path.Combine(Folder("user-copy"), "config.xml"); File.Copy(source, copy);
            AppConfig expected = AppConfig.LoadFrom(copy); Check(!expected.LoadFailed, "source copy invalid");
            AppConfig migrated = ConfigDatabase.Load(copy); Check(!migrated.LoadFailed, migrated.DatabaseError);
            Check(Canonical(expected) == Canonical(migrated), "real configuration changed during migration");
            Console.WriteLine("COPY favorites=" + migrated.Favorites.Count + "; added=" + migrated.AddedItems.Count + "; collections=" + migrated.Collections.Count);
        }
    }
}
