using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

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
                Run("stable category and collection ids on rename", Rename);
                Run("latest-operation undo, redo and stale rollback rejection", Undo);
                Run("stale writer rejected; failed transaction keeps all rows", Concurrency);
                Run("consistent online backup and integrity", Backup);
                Run("indexed keyset pagination, exclusions, hidden and multi-membership", Paging);
                Run("UI reads bounded database page and supports next/previous", UiPaging);
                Run("mini music includes manual files and resources without duplicate paths", MiniMusic);
                Run("XML backup export reports write failure", ExportFailure);
                Run("corrupt XML/database refuse initialization or overwrite", Corruption);
                Run("added item edits/deletes are incremental, rollback/reload preserves data", AddedEdits);
                Run("mid-transaction failure and retry preserve all-or-nothing semantics", AtomicFailure);
                Run("terminated writer recovers without exposing uncommitted rows", CrashRecovery);
                Run("metadata sorting covers whole category, both directions and keyset pages", MetadataSorting);
                Run("offline recovery keeps damaged original and restores validated snapshot", OfflineRecovery);
                Run("legacy name flags update indexed projection when removed", LegacyFlags);
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
        private static void Rename()
        {
            AppConfig c = Fixture("rename"); long parent = GroupId(c.Database, "照片"), collection = GroupId(c.Database, "col:照片/旅行");
            c.QueueCategoryRename("照片", "相片"); c.ResourceCategories[0] = "相片"; c.Collections[0].Parent = "相片";
            c.AddedItems.First(a => a.IsResource).Category = "相片";
            c.ItemCategories["add:C:\\image.jpg"][0] = "col:相片/旅行";
            Check(c.Save(), c.DatabaseError);
            Check(GroupId(c.Database, "相片") == parent && GroupId(c.Database, "col:相片/旅行") == collection, "ids changed");
            AppConfig read = ConfigDatabase.Load(Path.Combine(root, "rename", "config.xml"));
            Check(read.Collections[0].Parent == "相片" && read.ItemCategories["add:C:\\image.jpg"][0] == "col:相片/旅行", "renamed relationships lost");
            Check(c.Database.Page("col:相片/旅行", true, true, "", 10, null, false, false).Total == 1, "renamed listing lost");
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
            using (MainForm form = new MainForm(c))
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(MainForm).GetField("allApps", flags).SetValue(form, new List<AppInfo> {
                    new AppInfo { Key = "add:" + ordinary, TargetPath = ordinary, Name = "ordinary", IsFile = true }
                });
                MethodInfo read = typeof(MainForm).GetMethod("MiniMusicSongs", flags);
                List<string> all = (List<string>)read.Invoke(form, new object[] { "" });
                Check(all.Count == 2 && all.Contains(ordinary) && all.Contains(resource), "audio missing or repeated");
                List<string> collection = (List<string>)read.Invoke(form, new object[] { "col:照片/旅行" });
                Check(collection.Count == 1 && collection[0] == resource, "collection membership lost");
            }
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
            using (Sqlite db = new Sqlite(c.Database.FilePath, false)) db.Execute("DROP TRIGGER test_failure");
            Check(c.Save() && c.AddedItems.Count == count + 2, "retry lost staged rows");
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
