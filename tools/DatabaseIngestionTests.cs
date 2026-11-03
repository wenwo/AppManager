using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;

namespace AppManager
{
    // Integration tests invoke real ingestion handlers on hidden forms and use only isolated data.
    internal static class DatabaseIngestionTests
    {
        private static string root, scope, xml;
        private static int passed, failed, conflicts;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly string Collection = CollectionKeys.Make("资料", "批次 α'&");
        private sealed class IngestionForm : MainForm
        {
            internal IngestionForm(AppConfig c) : base(c) { ShowInTaskbar = false; Opacity = 0; }
            protected override void OnLoad(EventArgs e) { }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            root = Path.Combine(Path.GetTempPath(), "AppManagerIngestion_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); Console.WriteLine("ARTIFACTS=" + root);
            Application.EnableVisualStyles();
            Run("XML migration: all fields and relationships survive restart", Migration);
            Run("manual batch, grid drop and collection drop preserve bytes and memberships", UiIngestion);
            Run("external effects, queued copies and fixed destinations", ExternalCopyQueue);
            Run("cross-mode copy keeps the original software category", CrossModeCopy);
            Run("failed physical move keeps source bytes and does not register a phantom item", FailedMove);
            Run("software registration, new file/folder and scanner publish preserve manual rows", SoftwareAndScan);
            Run("same content in multiple categories has complete listing projections", MultipleCategories);
            Run("case variants of the same Windows path resolve to one content identity", CaseVariants);
            Run("XML import replaces intended state and preserves prior audit chain", Import);
            Run("path move preserves stable identity, favorites, hidden, ordering and collection", Move);
            Run("move guards: shortcut targets stay, reused path key saves, folder contents are found", MoveGuards);
            Run("import preparation runs in the queue: folder guard reads the database, empty batch calls back", QueuedPreparation);
            Run("add-software picker lists only registrable executables", SoftwarePicker);
            Run("cut and move-out: vanished sources purge entries, unavailable folders do not, in-app paste moves", MoveOut);
            Run("in-app move into a folder drops the old path's move-out watch and cut state", MoveIntoFolderForgetsWatch);
            Run("resource types: drag, batch add, move menu, cut paste, hover and picker share one rule", ResourceTypes);
            Run("backup, restore, undo and redo match complete state fingerprints", Restore);
            Run("four writers and scanner publishing: no lost commits or orphan relationships", ConcurrentWriters);
            Run("live backup during ingestion is an internally consistent committed snapshot", ConcurrentBackup);
            if (args.Length == 1) Run("running database: read-only snapshot passes full integrity and roundtrip", delegate { LiveSnapshot(args[0]); });
            Console.WriteLine("RESULT passed=" + passed + "; failed=" + failed + "; handled_conflicts=" + conflicts);
            Paths.DataDirOverride = null; Paths.ConfigFileOverride = null;
            return failed == 0 ? 0 : 1;
        }
        private static void Run(string name, Action action)
        {
            scope = Path.Combine(root, (passed + failed).ToString("D2")); Directory.CreateDirectory(scope);
            Paths.DataDirOverride = scope; Paths.ConfigFileOverride = xml = Path.Combine(scope, "config.xml");
            Stopwatch timer = Stopwatch.StartNew();
            try { action(); passed++; Console.WriteLine("PASS " + name + "; ms=" + timer.ElapsedMilliseconds); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
        }
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static AppConfig Fixture()
        {
            AppConfig c = ConfigDatabase.Load(xml); Check(!c.LoadFailed, c.DatabaseError);
            c.AutoScan = false; c.AutoOrganizeDesktop = false; c.CloseToTray = false;
            c.ManageRootPath = Path.Combine(scope, "managed"); c.FileStorePath = Path.Combine(scope, "files");
            c.FolderStorePath = Path.Combine(scope, "folders"); c.ResourceStorePath = Path.Combine(scope, "resources");
            c.ResourceCategories.Add("资料"); c.ResourceCategories.Add("另一分类"); c.CustomCategories.Add("软件测试");
            c.Collections.Add(new ResourceCollection { Parent = "资料", Name = "批次 α'&", Description = "中文 <>&'" });
            Save(c); return c;
        }
        private static void Save(AppConfig c) { Check(c.Save(), c.DatabaseError); }
        private static AppConfig Reload() { AppConfig c = ConfigDatabase.Load(xml); Check(!c.LoadFailed, c.DatabaseError); return c; }
        private static string Source(string relative, string text)
        { string p = Path.Combine(scope, "source", relative); Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllText(p, text, new UTF8Encoding(false)); return p; }
        private static string Fingerprint(AppConfig c)
        {
            List<string> sections = new List<string>();
            foreach (XmlElement section in c.ToDocument().DocumentElement.ChildNodes)
            {
                List<string> children = new List<string>();
                foreach (XmlNode child in section.ChildNodes) children.Add(child.OuterXml);
                children.Sort(StringComparer.Ordinal); sections.Add(section.Name + ":" + string.Join("\n", children.ToArray()));
            }
            sections.Sort(StringComparer.Ordinal); return Hash(Encoding.UTF8.GetBytes(string.Join("\n", sections.ToArray())));
        }
        private static string Hash(byte[] bytes) { using (SHA256 h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(bytes)); }
        private static void Integrity(ConfigDatabase store)
        {
            using (Sqlite db = new Sqlite(store.FilePath, true))
            {
                Check(db.Scalar("PRAGMA integrity_check") == "ok", "physical integrity");
                Check(db.Query("PRAGMA foreign_key_check").Count == 0, "orphan foreign key");
                Check(db.Scalar("SELECT operation_id FROM write_context WHERE id=1") == null, "open audit context");
                Check(db.Scalar("SELECT count(*) FROM changes c LEFT JOIN operations o ON o.id=c.operation_id WHERE o.id IS NULL") == "0", "orphan audit row");
                Check(db.Scalar("SELECT count(*) FROM (SELECT item_id,group_id,count(*) n FROM added_items GROUP BY item_id,group_id HAVING n>1)") == "0", "duplicate memberships");
            }
        }
        private static long Id(ConfigDatabase store, string key)
        { using (Sqlite db = new Sqlite(store.FilePath, true)) return Convert.ToInt64(db.Scalar("SELECT id FROM entries WHERE key=?", key)); }
        private static object Invoke(MainForm form, string name, params object[] args)
        {
            MethodInfo method = typeof(MainForm).GetMethods(Private).Single(m => m.Name == name && m.GetParameters().Length == args.Length &&
                m.GetParameters().Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])).All(v => v));
            return method.Invoke(form, args);
        }
        private static void Set(MainForm form, string name, object value) { typeof(MainForm).GetField(name, Private).SetValue(form, value); }
        private static MainForm Form(AppConfig c, bool resource)
        {
            MainForm form = new IngestionForm(c); Invoke(form, "BuildControls"); IntPtr handle = form.Handle;
            Set(form, "contentMode", resource ? Controls.SidebarControl.ContentMode.Website : Controls.SidebarControl.ContentMode.Software);
            Set(form, "currentCategory", resource ? "资料" : "软件测试"); Set(form, "scanRunning", true);
            form.Show();
            return form;
        }
        private static void PumpUntil(Func<bool> done)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (!done()) { Application.DoEvents(); Thread.Sleep(10); if (timer.ElapsedMilliseconds > 10000) throw new Exception("UI ingestion timeout"); }
            // Completion timers belong to the original handlers; let them dispose before destroying the form.
            Stopwatch drain = Stopwatch.StartNew(); while (drain.ElapsedMilliseconds < 1000) { Application.DoEvents(); Thread.Sleep(10); }
        }
        private static void Migration()
        {
            AppConfig source = new AppConfig(); source.CustomCategories.Add("软件测试"); source.ResourceCategories.Add("资料");
            source.Collections.Add(new ResourceCollection { Parent = "资料", Name = "批次 α'&", Cover = "封面.jpg", Description = "Unicode <>&'" });
            for (int i = 0; i < 1000; i++)
            {
                string path = "Z:\\migration\\" + i + ".txt", key = "add:" + path;
                source.AddedItems.Add(new AddedEntry { Path = path, Name = "资料 " + i, Category = "资料", IsResource = true });
                source.ItemCategories[key] = new List<string> { Collection, "另一分类" };
                if (i % 7 == 0) source.Favorites.Add(key);
                if (i % 11 == 0) source.Hidden.Add(key);
                if (i % 13 == 0) source.ExcludedCategories[key] = new List<string> { "另一分类" };
            }
            source.ItemOrder["资料"] = source.AddedItems.Take(30).Select(a => "add:" + a.Path).Reverse().ToList();
            source.DesktopBackup.Add(new DesktopBackupEntry { OriginalPath = "old", CurrentPath = "new", IsCopy = true });
            string before = Fingerprint(source); Check(source.SaveTo(xml), "source export");
            AppConfig c = Reload(); Check(Fingerprint(c) == before && Fingerprint(Reload()) == before, "semantic data loss"); Integrity(c.Database);
        }
        private static void UiIngestion()
        {
            AppConfig c = Fixture(); List<string> texts = new List<string>();
            using (MainForm form = Form(c, true))
            {
                string a = Source("dialog\\同名 ' 中文.txt", "dialog-a"), b = Source("dialog2\\同名 ' 中文.txt", "dialog-b");
                texts.AddRange(new string[] { Hash(File.ReadAllBytes(a)), Hash(File.ReadAllBytes(b)) });
                Invoke(form, "AddPaths", new string[] { a, b }, "资料", Collection); PumpUntil(() => c.AddedItems.Count == 2);
                string grid = Source("grid.txt", "grid"); Set(form, "currentCollection", Collection); Set(form, "collectionEntered", true); Invoke(form, "UpdateCollectionUi");
                texts.Add(Hash(File.ReadAllBytes(grid))); Invoke(form, "QueueImport", new string[] { grid }, ImportOperation.Move, "资料", Collection, true, (uint)0, null); PumpUntil(() => c.AddedItems.Count == 3);
                string collection = Source("collection.txt", "collection");
                texts.Add(Hash(File.ReadAllBytes(collection))); Invoke(form, "QueueImport", new string[] { collection }, ImportOperation.Move, "资料", Collection, true, (uint)0, null); PumpUntil(() => c.AddedItems.Count == 4);
                string existing = c.AddedItems.First().Path;
                Invoke(form, "AddPaths", new string[] { existing }, "资料", Collection); PumpUntil(() => c.AddedItems.Count == 4);
                Check(!File.Exists(a) && !File.Exists(b) && !File.Exists(grid) && !File.Exists(collection), "source not moved");
            }
            AppConfig read = Reload(); Check(read.AddedItems.Count == 4, "duplicate or lost ingestion");
            List<string> actual = read.AddedItems.Select(a => Hash(File.ReadAllBytes(a.Path))).OrderBy(t => t).ToList(); texts.Sort();
            Check(actual.SequenceEqual(texts), "file bytes changed or collision overwrote content");
            Check(read.Database.Page(Collection, true, true, "", 10, null, true, false).Total == 4, "collection missing ingestion link"); Integrity(read.Database);
        }
        private static void SoftwareAndScan()
        {
            AppConfig c = Fixture(); string executable = Source("tool.exe", "synthetic executable, never launched");
            using (MainForm form = Form(c, false))
            {
                Invoke(form, "AddPaths", new string[] { executable }, "软件测试", null); PumpUntil(() => c.AddedItems.Count == 1);
                Invoke(form, "CreateManagedEntry", false); Invoke(form, "CreateManagedEntry", true);
            }
            Check(File.Exists(executable), "software EXE unexpectedly moved");
            string before = Fingerprint(c);
            c.Database.PublishCatalog(c, new List<AppInfo> { new AppInfo { Key = "af:synthetic", Name = "synthetic", Category = "软件测试" } });
            c.Database.PublishCatalog(c, new List<AppInfo>());
            AppConfig read = Reload(); Check(read.AddedItems.Count == 3 && Fingerprint(read) == before, "scanner removed manual content");
            Check(read.Database.Page("全部文件", false, true, "", 10, null, true, false).Total == 3, "manual catalog lost to scanner"); Integrity(read.Database);
        }
        private static void ExternalCopyQueue()
        {
            AppConfig c = Fixture();
            string a = Source("copy1\\same.txt", "first"), b = Source("copy2\\same.txt", "second");
            using (MainForm form = Form(c, true))
            {
                Invoke(form, "InitializeImportUi");
                Controls.AppGridControl grid = (Controls.AppGridControl)typeof(MainForm).GetField("grid", Private).GetValue(form);
                Controls.SidebarControl sidebar = (Controls.SidebarControl)typeof(MainForm).GetField("sidebar", Private).GetValue(form);
                Controls.CollectionSidebar collections = (Controls.CollectionSidebar)typeof(MainForm).GetField("collectionSidebar", Private).GetValue(form);
                Check(grid.ExternalDrop != null && sidebar.ExternalDrop != null && collections.ExternalDrop != null, "drop handlers not connected");
                Controls.CollectionInfoPanel info = (Controls.CollectionInfoPanel)typeof(MainForm).GetField("collectionInfo", Private).GetValue(form);
                Set(form, "currentCollection", Collection); Set(form, "collectionEntered", false); info.Visible = true;
                Check((string)typeof(MainForm).GetProperty("ImportCollectionKey", Private).GetValue(form, null) == Collection, "collection info drop lost membership");
                info.Visible = false;
                DataObject data = new DataObject(DataFormats.FileDrop, new string[] { a });
                DragEventArgs copy = new DragEventArgs(data, 8, 0, 0, DragDropEffects.Copy | DragDropEffects.Move, DragDropEffects.None);
                Check(grid.ExternalDrop(copy, false) == DragDropEffects.Copy, "Ctrl drop effect");
                DragEventArgs restricted = new DragEventArgs(data, 4, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
                Check(sidebar.ExternalDrop("资料", restricted, false) == DragDropEffects.Copy, "copy-only source effect");
                Check(collections.ExternalDrop(Collection, copy, false) == DragDropEffects.Copy, "collection effect");
                Invoke(form, "HideImportTip");
                Invoke(form, "QueueImport", new string[] { a }, ImportOperation.Copy, "资料", Collection, true, (uint)0, null);
                Invoke(form, "QueueImport", new string[] { b }, ImportOperation.Copy, "资料", Collection, true, (uint)0, null);
                Set(form, "currentCategory", "另一分类");
                PumpUntil(() => !(bool)typeof(MainForm).GetField("importRunning", Private).GetValue(form));
                Check(File.Exists(a) && File.Exists(b), "copy removed sources");
                Check(c.AddedItems.Count == 2 && c.AddedItems.All(i => i.Category == "资料"), "destination changed while queued");
                Check(c.AddedItems.Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2, "queued filename conflict");
                string existing = c.AddedItems.First().Path;
                Invoke(form, "QueueImport", new string[] { existing.ToUpperInvariant() }, ImportOperation.Copy, "另一分类", null, true, (uint)0, null);
                PumpUntil(() => !(bool)typeof(MainForm).GetField("importRunning", Private).GetValue(form));
                Check(c.AddedItems.Count == 2 && File.Exists(existing), "reference was copied or duplicated by casing");
                Check(Reload().Database.Page("资料", true, true, "", 10, null, true, false).Total == 2, "original category lost after copy");
                Check(Reload().Database.Page("另一分类", true, true, "", 10, null, true, false).Total == 1, "extra category not indexed");
            }
            Check(Reload().Database.Page(Collection, true, true, "", 10, null, true, false).Total == 2, "copy collection lost on restart");
        }
        private static void CrossModeCopy()
        {
            AppConfig c = Fixture();
            string path = Source("cross-mode.txt", "same content");
            c.AddedItems.Add(new AddedEntry { Path = path, Category = "软件测试", IsResource = false }); Save(c);
            using (MainForm form = Form(c, true))
            {
                Set(form, "currentCategory", "资料");
                Invoke(form, "QueueImport", new string[] { path }, ImportOperation.Copy, "资料", null, true, (uint)0, null);
                PumpUntil(() => !(bool)typeof(MainForm).GetField("importRunning", Private).GetValue(form));
            }
            AppConfig read = Reload();
            Check(read.AddedItems.Count == 2, "cross-mode copy did not keep both memberships");
            Check(read.Database.Page("软件测试", false, true, "", 10, null, true, false).Total == 1, "original software category lost");
            Check(read.Database.Page("资料", true, true, "", 10, null, true, false).Total == 1, "resource category missing");
            Integrity(read.Database);
        }

        private static void FailedMove()
        {
            AppConfig c = Fixture(); string good = Source("good.txt", "good bytes"), locked = Source("locked.txt", "locked bytes");
            using (MainForm form = Form(c, true))
            using (FileStream input = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Invoke(form, "AddPaths", new string[] { good, locked }, "资料", Collection); PumpUntil(() => c.AddedItems.Count == 1);
                Check(File.Exists(locked) && File.Exists(good), "batch failure must preserve every source");
                Check(c.AddedItems.All(a => Path.GetFileName(a.Path) != "locked.txt"), "failed file was registered");
                Check(File.ReadAllText(c.AddedItems.First().Path) == "good bytes", "successful move lost bytes");
            }
            Check(File.ReadAllText(locked) == "locked bytes", "failed copy changed original");
            AppConfig read = Reload(); Check(read.AddedItems.Count == 1 && read.Database.Page(Collection, true, true, "", 10, null, true, false).Total == 1, "failure persisted a partial relation"); Integrity(read.Database);
        }
        private static void MultipleCategories()
        {
            AppConfig c = Fixture(); string path = Source("shared.txt", "shared");
            c.AddedItems.Add(new AddedEntry { Path = path, Category = "资料", IsResource = true });
            c.AddedItems.Add(new AddedEntry { Path = path, Category = "另一分类", IsResource = true });
            c.AddedItems.Add(new AddedEntry { Path = path, Category = "资料", IsResource = true }); Save(c);
            AppConfig read = Reload(); Check(read.AddedItems.Count == 2, "duplicate relation not deduplicated");
            Check(read.Database.Page("全部资源", true, true, "", 10, null, true, false).Total == 1, "one content became multiple resources");
            foreach (string category in new string[] { "资料", "另一分类" })
                Check(read.Database.Page(category, true, true, "", 10, null, true, false).Total == 1, "missing membership projection for " + category);
            Integrity(read.Database);
        }
        private static void CaseVariants()
        {
            AppConfig c = Fixture(); string path = Source("CaseSensitiveName.txt", "same Windows file");
            c.AddedItems.Add(new AddedEntry { Path = path, Category = "资料", IsResource = true }); Save(c);
            long id = Id(c.Database, "add:" + path);
            c.AddedItems.Add(new AddedEntry { Path = path.ToUpperInvariant(), Category = "资料", IsResource = true }); Save(c);
            AppConfig read = Reload(); Check(read.AddedItems.ByPath(path).Count == 1 && read.AddedItems.Count == 1, "case variants duplicated the same file");
            Check(read.Database.Page("资料", true, true, "", 10, null, true, false).Total == 1, "case variants duplicated projection");
            Check(Id(read.Database, "add:" + path) == id, "identity changed"); Integrity(read.Database);
        }
        private static void Import()
        {
            AppConfig c = Fixture(); c.AddedItems.Add(new AddedEntry { Path = "Z:\\before.txt", Category = "资料", IsResource = true }); Save(c);
            int history = c.Database.History(1000).Count;
            AppConfig incoming = new AppConfig(); incoming.ResourceCategories.Add("资料");
            incoming.AddedItems.Add(new AddedEntry { Path = "Z:\\after.txt", Name = "导入", Category = "资料", IsResource = true });
            incoming.Favorites.Add("add:Z:\\after.txt"); string expected = Fingerprint(incoming);
            string backup = Path.Combine(scope, "incoming.ambak"); Check(incoming.ExportTo(backup), "export");
            AppConfig imported = AppConfig.ImportFrom(backup); Check(imported != null, "import parse"); Save(imported);
            AppConfig read = Reload(); Check(Fingerprint(read) == expected, "import left old rows or lost fields");
            Check(read.Database.History(1000).Count > history, "import erased audit chain"); Integrity(read.Database);
        }
        private static void Move()
        {
            AppConfig c = Fixture();
            string path = Path.Combine(scope, "files", "move.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "keep physical contents", new UTF8Encoding(false));
            string key = "add:" + path;
            c.AddedItems.Add(new AddedEntry { Path = path, Category = "资料", IsResource = true });
            c.Favorites.Add(key); c.Hidden.Add(key); c.ItemOrder["资料"] = new List<string> { key }; c.ItemCategories[key] = new List<string> { Collection }; Save(c);
            long id = Id(c.Database, key); string folder = Path.Combine(scope, "target"); Directory.CreateDirectory(folder);
            string moved = Path.Combine(folder, "move.txt");
            using (MainForm form = Form(c, true))
            {
                Invoke(form, "OnGridMoveIntoFolder",
                    new AppInfo { Key = key, TargetPath = path, Name = "move", IsFile = true }, new AppInfo { TargetPath = folder, IsFolder = true });
                // The move runs on the background thread; wait for the save transaction before checking stable identity.
                PumpUntil(() => c.Database.History(5).Any(row => row[2] == "physical.files.move"));
            }
            AppConfig read = Reload(); string newKey = "add:" + moved;
            Check(!File.Exists(path) && File.ReadAllText(moved) == "keep physical contents", "file move corrupt");
            Check(Id(read.Database, newKey) == id && read.AddedItems.ByPath(moved).Count == 1, "identity changed on move");
            Check(read.Favorites.Contains(newKey) && read.Hidden.Contains(newKey) && read.ItemOrder["资料"][0] == newKey && read.ItemCategories[newKey].Contains(Collection), "relationship not moved");
            Check(read.Database.Page(Collection, true, true, "", 10, null, true, false).Total == 1, "moved projection missing"); Integrity(read.Database);
        }
        private static void MoveGuards()
        {
            AppConfig c = Fixture(); string folder = Path.Combine(scope, "target"); Directory.CreateDirectory(folder);
            // 快捷方式条目（lnk:）的目标是用户自己的文件：拖入目录条目不得搬动它
            string user = Source("user.txt", "user file");
            using (MainForm form = Form(c, true)) Invoke(form, "OnGridMoveIntoFolder",
                new AppInfo { Key = "lnk:" + user, TargetPath = user, Name = "user", IsFile = true }, new AppInfo { TargetPath = folder, IsFolder = true });
            Check(File.ReadAllText(user) == "user file" && !File.Exists(Path.Combine(folder, "user.txt")), "shortcut target was moved");

            // entries 行不回收：路径改到曾登记过的位置时，保存不能失败，后续保存也不能被卡住
            string stale = Path.Combine(folder, "reuse.txt");
            c.AddedItems.Add(new AddedEntry { Path = stale, Category = "资料", IsResource = true }); Save(c);
            foreach (AddedEntry e in c.AddedItems.ByPath(stale)) c.AddedItems.Remove(e);
            Save(c); Check(Id(c.Database, "add:" + stale) != 0, "fixture: stale entry row missing");
            string reuse = Source("reuse.txt", "reuse"); string oldKey = "add:" + reuse, newKey = "add:" + stale;
            c.AddedItems.Add(new AddedEntry { Path = reuse, Category = "资料", IsResource = true }); Save(c);
            c.QueueEntryRename(oldKey, newKey);
            foreach (AddedEntry e in c.AddedItems.ByPath(reuse)) e.Path = stale;
            Check(c.Save(), "rename onto a previously registered key failed: " + c.DatabaseError);
            c.Favorites.Add(newKey); Check(c.Save(), "later save blocked by the failed rename: " + c.DatabaseError);
            AppConfig read = Reload();
            Check(read.AddedItems.ByPath(stale).Count == 1 && read.AddedItems.ByPath(reuse).Count == 0 && read.Favorites.Contains(newKey), "renamed entry lost");
            Integrity(read.Database);

            // 文件夹内的收录项：数据库中、未保存的新增与改名都能找到，已删除的不算
            string box = Path.Combine(scope, "box"), inner = Path.Combine(box, "inner.txt"), boxed = Path.Combine(box + "ed", "x.txt");
            read.AddedItems.Add(new AddedEntry { Path = inner, Category = "资料", IsResource = true });
            read.AddedItems.Add(new AddedEntry { Path = boxed, Category = "资料", IsResource = true }); Save(read);
            Check(read.AddedItems.EntriesUnder(box).Count == 1, "folder contents not found or sibling prefix matched");
            foreach (AddedEntry e in read.AddedItems.ByPath(inner)) e.Path = Path.Combine(scope, "out.txt");
            Check(read.AddedItems.EntriesUnder(box).Count == 0, "unsaved move out of the folder ignored");
            read.AddedItems.Add(new AddedEntry { Path = Path.Combine(box, "new.txt"), Category = "资料", IsResource = true });
            Check(read.AddedItems.EntriesUnder(box + "\\").Count == 1, "unsaved addition inside the folder ignored");
        }
        // “添加软件”候选只列可登记的 .exe：已登记（含大小写变体）、UWP、网址、文件夹/文档快捷方式和重复目标都不列。
        private static void SoftwarePicker()
        {
            AppConfig c = Fixture();
            string tool = Source("apps\\tool.exe", "exe"), known = Source("apps\\known.exe", "exe"), folder = Path.Combine(scope, "source", "apps");
            c.AddedItems.Add(new AddedEntry { Path = known, Category = "软件测试" }); Save(c);
            using (MainForm form = Form(c, false))
            {
                List<AppInfo> apps = new List<AppInfo>
                {
                    new AppInfo { Name = "B tool", TargetPath = tool },
                    new AppInfo { Name = "A tool again", TargetPath = tool.ToUpperInvariant() },
                    new AppInfo { Name = "known", TargetPath = known.ToUpperInvariant() },
                    new AppInfo { Name = "folder link", TargetPath = folder, ShortcutPath = Path.Combine(scope, "folder.lnk") },
                    new AppInfo { Name = "document link", TargetPath = Source("apps\\readme.txt", "doc") },
                    new AppInfo { Name = "store app", TargetPath = "shell:AppsFolder\\Microsoft.App!App" },
                    new AppInfo { Name = "web", TargetPath = "https://example.com/setup.exe" },
                    new AppInfo { Name = "empty", ShortcutPath = Path.Combine(scope, "empty.lnk") },
                    null,
                };
                List<AppInfo> picked = (List<AppInfo>)Invoke(form, "PickableSoftware", apps);
                Check(picked.Count == 1 && string.Equals(picked[0].TargetPath, tool, StringComparison.OrdinalIgnoreCase),
                    "picker listed non-software or registered items: " + string.Join("|", picked.Select(a => a.Name).ToArray()));
            }
        }
        // 导入预处理在导入队列（后台）里读库完成：入队调用立即返回，文件夹内有收录项时拒绝移动，空批次也异步回调。
        private static void QueuedPreparation()
        {
            AppConfig c = Fixture();
            string guarded = Path.Combine(scope, "guarded"), kept = Path.Combine(guarded, "kept.txt");
            Directory.CreateDirectory(guarded); File.WriteAllText(kept, "kept");
            c.AddedItems.Add(new AddedEntry { Path = kept, Category = "资料", IsResource = true }); Save(c);
            using (MainForm form = Form(c, true))
            {
                ImportBatch refused = null, empty = null;
                Invoke(form, "QueueImport", new string[] { guarded.ToLowerInvariant() }, ImportOperation.Move, "资料", null, true, (uint)0,
                    new Action<ImportBatch>(delegate(ImportBatch b) { refused = b; }));
                Invoke(form, "QueueImport", new string[0], ImportOperation.Move, "资料", null, true, (uint)0,
                    new Action<ImportBatch>(delegate(ImportBatch b) { empty = b; }));
                Check(refused == null && empty == null, "preparation ran synchronously on the UI thread");
                PumpUntil(() => refused != null && empty != null && !(bool)typeof(MainForm).GetField("importRunning", Private).GetValue(form));
                Check(refused.Items.Count == 1 && refused.Items[0].State == FileImport.Failed && refused.Items[0].Error.Contains("已收录"),
                    "folder with registered contents was not refused: " + refused.Items[0].State + " " + refused.Items[0].Error);
                Check(empty.Items.Count == 0, "empty batch callback carried items");
            }
            Check(File.ReadAllText(kept) == "kept" && Directory.Exists(guarded), "refused folder move touched the source");
            AppConfig read = Reload();
            Check(read.AddedItems.ByPath(kept).Count == 1 && read.AddedItems.Count == c.AddedItems.Count, "refused move changed registrations");
            Integrity(read.Database);
        }
        private static void MoveOut()
        {
            AppConfig c = Fixture();
            string cut = Source("cut.txt", "cut"), cutKey = "add:" + cut;
            string offline = Source("offline\\keep.txt", "keep"), offlineKey = "add:" + offline;
            string paste = Source("paste.txt", "paste"), pasteKey = "add:" + paste;
            string flap = Source("flap.txt", "flap"), flapKey = "add:" + flap;
            foreach (string path in new[] { cut, offline, paste, flap }) c.AddedItems.Add(new AddedEntry { Path = path, Category = "资料", IsResource = true });
            c.Favorites.Add(cutKey); c.ItemCategories[cutKey] = new List<string> { Collection }; Save(c);
            using (MainForm form = Form(c, true))
            {
                AppInfo a = new AppInfo { Key = cutKey, TargetPath = cut, Name = "cut", IsFile = true, Category = "资料" };
                AppInfo b = new AppInfo { Key = offlineKey, TargetPath = offline, Name = "keep", IsFile = true, Category = "资料" };
                AppInfo f = new AppInfo { Key = flapKey, TargetPath = flap, Name = "flap", IsFile = true, Category = "资料" };
                Invoke(form, "WatchMoveOut", new List<AppInfo> { a, b, f }, DateTime.MaxValue);
                Invoke(form, "CheckMoveOuts");
                Check(c.AddedItems.ByPath(cut).Count == 1, "purged while the source is still in place");
                File.Move(cut, Path.Combine(scope, "elsewhere.txt"));                                  // 被资源管理器移走
                Directory.Move(Path.GetDirectoryName(offline), Path.Combine(scope, "offline-gone"));   // 所在目录也不可用：视为卷离线
                string flapAway = Path.Combine(scope, "flap-away.txt");
                File.Move(flap, flapAway);
                Invoke(form, "CheckMoveOuts");
                Check(c.AddedItems.ByPath(cut).Count == 1 && c.AddedItems.ByPath(flap).Count == 1,
                    "entry was purged after a single missing check");

                // 文件重新出现即清零，随后必须再连续缺失两次才清理
                File.Move(flapAway, flap);
                Invoke(form, "CheckMoveOuts");
                Check(c.AddedItems.ByPath(cut).Count == 0 && c.AddedItems.ByPath(flap).Count == 1,
                    "missing counter did not reset when the source returned");
                File.Move(flap, Path.Combine(scope, "flap-away-2.txt"));
                Invoke(form, "CheckMoveOuts");
                Check(c.AddedItems.ByPath(flap).Count == 1, "entry was purged after a single missing check");
                Invoke(form, "CheckMoveOuts");
                Check(c.AddedItems.ByPath(flap).Count == 0, "entry survived two consecutive missing checks");

                // 本应用剪切后粘贴回应用：按「移动到分类」改归属，剪切随之结束
                Type stateType = typeof(MainForm).GetNestedType("CutState", BindingFlags.NonPublic);
                object state = Activator.CreateInstance(stateType);
                AppInfo p = new AppInfo { Key = pasteKey, TargetPath = paste, Name = "paste", IsFile = true, Category = "资料" };
                stateType.GetField("Apps").SetValue(state, new List<AppInfo> { p });
                stateType.GetField("Resource").SetValue(state, true);
                Set(form, "cutState", state); Set(form, "currentCategory", "另一分类");
                Invoke(form, "PasteCutItems");
                Check(typeof(MainForm).GetField("cutState", Private).GetValue(form) == null, "cut not finished after paste");
            }
            AppConfig read = Reload();
            Check(read.AddedItems.ByPath(cut).Count == 0 && !read.Favorites.Contains(cutKey) && !read.ItemCategories.ContainsKey(cutKey), "moved-out entry not purged");
            Check(read.AddedItems.ByPath(flap).Count == 0, "transient then moved-out entry not purged");
            Check(read.AddedItems.ByPath(offline).Count == 1, "entry purged although its folder is unavailable");
            string category; Check(read.CategoryOverride.TryGetValue(pasteKey, out category) && category == "另一分类" && File.Exists(paste), "in-app paste did not move the category");
            Check(read.Database.Page("资料", true, true, "", 10, null, true, false).Total == 1, "moved item still listed in the original category");
            Check(read.Database.Page("另一分类", true, true, "", 10, null, true, false).Total == 1, "moved item missing from the target category");
            Integrity(read.Database);
        }
        // 应用内拖入文件夹：旧路径的移出检测与同一项的剪切状态随之撤掉
        private static void MoveIntoFolderForgetsWatch()
        {
            AppConfig c = Fixture();
            using (MainForm form = Form(c, true))
            {
                Type stateType = typeof(MainForm).GetNestedType("CutState", BindingFlags.NonPublic);
                string library = Path.Combine(scope, "resources"), into = Path.Combine(library, "into");
                Directory.CreateDirectory(into);
                string drag = Path.Combine(library, "drag.txt"), dragKey = "add:" + drag;
                File.WriteAllText(drag, "drag");
                c.AddedItems.Add(new AddedEntry { Path = drag, Category = "资料", IsResource = true }); Save(c);
                AppInfo d = new AppInfo { Key = dragKey, TargetPath = drag, Name = "drag", IsFile = true, Category = "资料" };
                Invoke(form, "WatchMoveOut", new List<AppInfo> { d }, DateTime.MaxValue);
                object dragCut = Activator.CreateInstance(stateType);
                stateType.GetField("Apps").SetValue(dragCut, new List<AppInfo> { d });
                stateType.GetField("Resource").SetValue(dragCut, true);
                Set(form, "cutState", dragCut);
                Invoke(form, "OnGridMoveIntoFolder", d, new AppInfo { TargetPath = into, IsFolder = true, IsFile = true });
                PumpUntil(() => !(bool)typeof(MainForm).GetField("moveIntoFolderRunning", Private).GetValue(form));
                Check(File.Exists(Path.Combine(into, "drag.txt")) && !File.Exists(drag), "fixture: drag into folder did not move the file");
                Check(typeof(MainForm).GetField("cutState", Private).GetValue(form) == null, "cut state survived an in-app move into a folder");
                System.Collections.IList watches = (System.Collections.IList)typeof(MainForm).GetField("moveOutWatches", Private).GetValue(form);
                Check(!watches.Cast<object>().Any(w => string.Equals((string)w.GetType().GetField("Path").GetValue(w), drag, StringComparison.OrdinalIgnoreCase)),
                    "move-out watch survived an in-app move into a folder");
                Check(c.AddedItems.ByPath(Path.Combine(into, "drag.txt")).Count == 1, "moved entry lost its registration");
            }
            AppConfig read = Reload();
            Check(read.AddedItems.ByPath(Path.Combine(scope, "resources", "into", "drag.txt")).Count == 1
                && read.AddedItems.ByPath(Path.Combine(scope, "resources", "drag.txt")).Count == 0, "move into folder was not persisted");
            Integrity(read.Database);
        }
        private static void ResourceTypes()
        {
            AppConfig c = Fixture();
            string jpg = Source("types\\a.jpg", "jpg"), zip = Source("types\\b.zip", "zip"), album = Path.Combine(scope, "source", "types", "album.jpg.d");
            Directory.CreateDirectory(album);
            foreach (string path in new[] { jpg, zip }) c.AddedItems.Add(new AddedEntry { Path = path, Category = "文件", IsResource = true });
            c.AddedItems.Add(new AddedEntry { Path = album, Category = "文件", IsFolder = true, IsResource = true }); Save(c);
            Func<string, bool, AppInfo> app = (path, folder) => new AppInfo { Key = "add:" + path, TargetPath = path, Name = Path.GetFileName(path), IsFile = true, IsFolder = folder, Category = "文件" };
            AppInfo photo = app(jpg, false), archive = app(zip, false), folderItem = app(album, true);
            MethodInfo canMove = typeof(MainForm).GetMethod("CanMoveTargetsTo", BindingFlags.Static | BindingFlags.NonPublic);
            using (MainForm form = Form(c, true))
            {
                Func<AppInfo, string, bool> inCategory = (a, category) => (bool)Invoke(form, "InCategory", a, category);
                // 拖到侧栏分类 / 批量加入：类型不符的跳过，文件夹整体收纳
                Invoke(form, "AddItemsToCategory", new List<AppInfo> { photo, archive, folderItem }, "照片");
                Check(inCategory(photo, "照片") && !inCategory(archive, "照片") && inCategory(folderItem, "照片"), "drag to category ignored type rule");
                Control status = (Control)typeof(MainForm).GetField("statusLabel", Private).GetValue(form);
                Check(status.Text.Contains("已跳过 1 个"), "partial skip not reported: " + status.Text);
                Invoke(form, "AddItemsToCategory", new List<AppInfo> { archive }, "视频");
                Check(!inCategory(archive, "视频") && status.Text.StartsWith("未加入"), "mismatched-only add not refused: " + status.Text);

                // 「移动到分类」菜单可用性与导入一致：文件夹可移入类型分类，压缩包不能进照片
                Check((bool)canMove.Invoke(null, new object[] { new List<AppInfo> { folderItem }, "视频" }), "folder refused by move menu");
                Check(!(bool)canMove.Invoke(null, new object[] { new List<AppInfo> { archive }, "照片" }), "archive accepted by photo move menu");

                // 拖动经过：全部不符时禁止，文件夹允许
                Set(form, "currentCategory", "照片");
                Func<string[], DragDropEffects> hover = paths =>
                {
                    DataObject data = new DataObject(); System.Collections.Specialized.StringCollection list = new System.Collections.Specialized.StringCollection();
                    list.AddRange(paths); data.SetFileDropList(list); Set(form, "dragProbe", null);
                    return (DragDropEffects)Invoke(form, "HandleExternalImport", null, null,
                        new DragEventArgs(data, 1, 0, 0, DragDropEffects.Copy | DragDropEffects.Move, DragDropEffects.None), false);
                };
                Check(hover(new[] { zip }) == DragDropEffects.None, "mismatched drag accepted while hovering");
                Check(hover(new[] { album }) != DragDropEffects.None && hover(new[] { zip, jpg }) != DragDropEffects.None, "valid drag refused while hovering");

                // 应用内粘贴剪切：类型不符的跳过，剪切照常结束
                Type stateType = typeof(MainForm).GetNestedType("CutState", BindingFlags.NonPublic);
                object state = Activator.CreateInstance(stateType);
                stateType.GetField("Apps").SetValue(state, new List<AppInfo> { archive, folderItem });
                stateType.GetField("Resource").SetValue(state, true);
                Set(form, "cutState", state); Set(form, "currentCategory", "音乐");
                Invoke(form, "PasteCutItems");
                Check(c.CategoryOverride.ContainsKey(folderItem.Key) && c.CategoryOverride[folderItem.Key] == "音乐" && !c.CategoryOverride.ContainsKey(archive.Key),
                    "cut paste ignored type rule");

                // 软件页的自定义分类可与资源分类同名：软件不受资源类型约束
                c.CustomCategories.Add("音乐"); Save(c);
                Set(form, "contentMode", Controls.SidebarControl.ContentMode.Software);
                AppInfo tool = new AppInfo { Key = "lnk:C:\\Tools\\player.lnk", TargetPath = "C:\\Tools\\player.exe", Name = "player", Category = "其他" };
                Invoke(form, "AddItemsToCategory", new List<AppInfo> { tool }, "音乐");
                Check(inCategory(tool, "音乐"), "software item blocked by a resource type rule on a same-named software category");
            }

            // 选择导入分类：只列出能收下内容的分类，同类文件自动推荐
            string[] all = CategoryRules.ResourceBuiltIn;
            string preferred = "文件";
            List<string> choices = MainForm.ResourceImportChoices(all, new[] { "x.jpg", "y.png" }, new[] { false, false }, ref preferred);
            Check(preferred == "照片" && choices.Contains("证件") && choices.Contains("文件") && !choices.Contains("压缩包"), "photo picker wrong: " + string.Join(",", choices));
            preferred = "文件";
            choices = MainForm.ResourceImportChoices(all, new[] { "x.jpg", "y.pdf" }, new[] { false, false }, ref preferred);
            Check(preferred == "文件" && choices.Contains("照片") && choices.Contains("文档"), "mixed picker wrong: " + preferred);
            preferred = "文件";
            choices = MainForm.ResourceImportChoices(all, new[] { "pack.zip", "album" }, new[] { false, true }, ref preferred);
            Check(preferred == "文件" && choices.Count == all.Length, "folder picker wrong: " + string.Join(",", choices));

            // 分类接收清单：补充常见格式；证件不收脚本；查看器清单不受影响（新格式双击交给系统程序）
            string[][] accepted = {
                new[] { "照片", "IMG_0001.HEIC", "scan.tiff", "a.avif" }, new[] { "视频", "clip.m2ts", "old.rmvb", "phone.3gp" },
                new[] { "文档", "report.wps", "sheet.et", "invoice.ofd", "a.odt" }, new[] { "证件", "id.heic", "license.ofd", "id.pdf", "id.txt" },
                new[] { "压缩包", "a.tar.zst", "setup.cab" } };
            foreach (string[] row in accepted)
                for (int i = 1; i < row.Length; i++) Check(MediaRules.MatchCategoryExt(row[0], row[i]), row[0] + " refused " + row[i]);
            string[][] refused = { new[] { "证件", "run.bat", "tool.ps1", "app.js", "q.sql" }, new[] { "视频", "stream.ts" }, new[] { "照片", "a.pdf" } };
            foreach (string[] row in refused)
                for (int i = 1; i < row.Length; i++) Check(!MediaRules.MatchCategoryExt(row[0], row[i]), row[0] + " accepted " + row[i]);
            Check(!MediaRules.IsImage("a.heic") && !MediaRules.IsVideo("a.rmvb") && MediaRules.IsText("a.ts"), "viewer type lists changed");
            Integrity(Reload().Database);
        }
        private static void Restore()
        {
            AppConfig c = Fixture(); c.AddedItems.Add(new AddedEntry { Path = "Z:\\restore.txt", Category = "资料", IsResource = true }); Save(c);
            string initial = Fingerprint(c), snapshot = Path.Combine(scope, "snapshot.db"); c.Database.Backup(snapshot);
            string layout, error; Check(LayoutBackup.Create("integration", out layout, out error), error);
            c.Favorites.Add("add:Z:\\restore.txt"); c.ItemCategories["add:Z:\\restore.txt"] = new List<string> { Collection }; Save(c);
            string changed = Fingerprint(c); long op = Convert.ToInt64(c.Database.History(1)[0][0]); c.Database.Undo(op);
            c = Reload(); Check(Fingerprint(c) == initial, "undo fingerprint mismatch");
            c.Database.Undo(Convert.ToInt64(c.Database.History(1)[0][0])); c = Reload(); Check(Fingerprint(c) == changed, "redo mismatch");
            c.Database.Restore(snapshot); c = Reload(); Check(Fingerprint(c) == initial, "database restore mismatch");
            c.Favorites.Add("temporary"); Save(c); int history = c.Database.History(1000).Count;
            Check(LayoutBackup.Restore(layout, out error), error); c = Reload();
            Check(Fingerprint(c) == initial && c.Database.History(1000).Count > history, "layout restore lost state or audit history"); Integrity(c.Database);
        }
        private static void ConcurrentWriters()
        {
            AppConfig initial = Fixture(); List<Exception> errors = new List<Exception>(); List<Thread> threads = new List<Thread>();
            List<string> accepted = new List<string>(), rejected = new List<string>();
            Action<AppConfig, string> stage = delegate(AppConfig c, string path)
            {
                string key = "add:" + path;
                c.AddedItems.Add(new AddedEntry { Path = path, Category = "资料", IsResource = true }); c.Favorites.Add(key); c.ItemCategories[key] = new List<string> { Collection };
            };
            for (int worker = 0; worker < 4; worker++)
            {
                int index = worker;
                threads.Add(new Thread(delegate()
                {
                    try
                    {
                        for (int i = 0; i < 20; i++)
                        {
                            AppConfig c = Reload(); string path = "Z:\\concurrent\\" + index + "-" + i + ".txt"; stage(c, path);
                            if (c.Save()) { lock (accepted) accepted.Add(path); }
                            else
                            {
                                Check(c.DatabaseError.Contains("更新"), "unexpected write failure: " + c.DatabaseError);
                                Interlocked.Increment(ref conflicts); lock (rejected) rejected.Add(path);
                            }
                        }
                    }
                    catch (Exception ex) { lock (errors) errors.Add(ex); }
                }));
            }
            threads.Add(new Thread(delegate()
            {
                try
                {
                    for (int i = 0; i < 15; i++)
                    {
                        AppConfig c = Reload();
                        try { c.Database.PublishCatalog(c, new List<AppInfo> { new AppInfo { Key = "af:parallel", Name = "parallel", Category = "软件测试" } }); }
                        catch (IOException ex) { Check(ex.Message.Contains("重新加载"), "unexpected scanner failure: " + ex.Message); Interlocked.Increment(ref conflicts); }
                    }
                }
                catch (Exception ex) { lock (errors) errors.Add(ex); }
            }));
            foreach (Thread thread in threads) thread.Start();
            foreach (Thread thread in threads) Check(thread.Join(60000), "writer timed out");
            Check(errors.Count == 0, string.Join("\n", errors.Select(e => e.ToString()).ToArray()));
            // Separate sessions intentionally reject stale writes. Verify acknowledgements before replaying rejected commands.
            AppConfig read = Reload();
            Check(read.AddedItems.Count == accepted.Count && read.Favorites.Count == accepted.Count && read.ItemCategories.Count == accepted.Count, "lost acknowledged commit or partially persisted rejected write");
            foreach (string path in accepted) Check(read.AddedItems.ByPath(path).Count == 1 && read.Favorites.Contains("add:" + path), "acknowledged content missing");
            foreach (string path in rejected) Check(read.AddedItems.ByPath(path).Count == 0 && !read.ItemCategories.ContainsKey("add:" + path), "rejected write leaked content or relationships");
            foreach (string path in rejected) { read = Reload(); stage(read, path); Save(read); }
            read = Reload(); Check(read.AddedItems.Count == 80 && read.Favorites.Count == 80 && read.ItemCategories.Count == 80, "explicit replay lost data");
            Console.WriteLine("CONCURRENT accepted=" + accepted.Count + "; rejected=" + rejected.Count + "; explicit_replay=" + rejected.Count);
            Check(read.Database.Page(Collection, true, true, "", 100, null, true, false).Total == 80, "concurrent listing incomplete"); Integrity(read.Database);
        }
        private static void ConcurrentBackup()
        {
            AppConfig c = Fixture(); Exception failure = null; ManualResetEvent ready = new ManualResetEvent(false);
            Thread writer = new Thread(delegate()
            {
                try
                {
                    for (int i = 0; i < 100; i++)
                    {
                        string path = "Z:\\backup\\" + i + ".txt", key = "add:" + path;
                        c.AddedItems.Add(new AddedEntry { Path = path, Category = "资料", IsResource = true }); c.Favorites.Add(key); Save(c);
                        if (i == 5) ready.Set(); Thread.Sleep(1);
                    }
                }
                catch (Exception ex) { failure = ex; ready.Set(); }
            });
            writer.Start(); Check(ready.WaitOne(10000), "writer never started");
            string snap = Path.Combine(scope, "concurrent.db");
            using (Sqlite db = new Sqlite(c.Database.FilePath, true)) db.Backup(snap);
            Check(writer.Join(60000), "backup writer timeout"); Check(failure == null, "backup writer failed: " + failure);
            using (Sqlite db = new Sqlite(snap, true))
            {
                int items = Convert.ToInt32(db.Scalar("SELECT count(*) FROM added_items"));
                Check(items >= 6 && items <= 100, "backup state outside committed range");
                Check(db.Scalar("SELECT count(*) FROM records WHERE section='Favorites'") == items.ToString(), "backup split a transaction");
                Check(db.Scalar("PRAGMA integrity_check") == "ok" && db.Query("PRAGMA foreign_key_check").Count == 0, "backup integrity");
                Check(db.Scalar("SELECT operation_id FROM write_context WHERE id=1") == null, "backup contains active transaction");
                Console.WriteLine("SNAPSHOT committed_items=" + items + "; final_items=" + Reload().AddedItems.Count);
            }
            Integrity(c.Database);
        }
        private static void LiveSnapshot(string path)
        {
            string snapshot = Path.Combine(scope, "appmanager.db");
            using (Sqlite live = new Sqlite(Path.GetFullPath(path), true)) live.Backup(snapshot);
            AppConfig copy = Reload(); Integrity(copy.Database); string before = Fingerprint(copy);
            using (Sqlite db = new Sqlite(snapshot, true))
            {
                string aliases = db.Scalar("SELECT count(*) FROM (SELECT path_key FROM added_items GROUP BY path_key HAVING count(DISTINCT item_id)>1)");
                Console.WriteLine("LIVE_COPY duplicate_path_identities=" + aliases);
                Check(aliases == "0", "running database already contains duplicate path identities");
            }
            string exported = Path.Combine(scope, "snapshot.ambak"); Check(copy.ExportTo(exported), "live snapshot export");
            Check(Fingerprint(AppConfig.ImportFrom(exported)) == before, "live snapshot XML roundtrip mismatch");
            Console.WriteLine("LIVE_COPY added=" + copy.AddedItems.Count + "; favorites=" + copy.Favorites.Count + "; collections=" + copy.Collections.Count);
        }
    }
}
