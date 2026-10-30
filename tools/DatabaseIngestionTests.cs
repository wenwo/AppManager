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
            Run("failed physical move keeps source bytes and does not register a phantom item", FailedMove);
            Run("software registration, new file/folder and scanner publish preserve manual rows", SoftwareAndScan);
            Run("same content in multiple categories has complete listing projections", MultipleCategories);
            Run("case variants of the same Windows path resolve to one content identity", CaseVariants);
            Run("XML import replaces intended state and preserves prior audit chain", Import);
            Run("path move preserves stable identity, favorites, hidden, ordering and collection", Move);
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
                Check(Reload().Database.Page("另一分类", true, true, "", 10, null, true, false).Total == 1, "extra category not indexed");
            }
            Check(Reload().Database.Page(Collection, true, true, "", 10, null, true, false).Total == 2, "copy collection lost on restart");
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
            AppConfig c = Fixture(); string path = Source("move.txt", "keep physical contents"); string key = "add:" + path;
            c.AddedItems.Add(new AddedEntry { Path = path, Category = "资料", IsResource = true });
            c.Favorites.Add(key); c.Hidden.Add(key); c.ItemOrder["资料"] = new List<string> { key }; c.ItemCategories[key] = new List<string> { Collection }; Save(c);
            long id = Id(c.Database, key); string folder = Path.Combine(scope, "target"); Directory.CreateDirectory(folder);
            using (MainForm form = Form(c, true)) Invoke(form, "OnGridMoveIntoFolder",
                new AppInfo { Key = key, TargetPath = path, Name = "move", IsFile = true }, new AppInfo { TargetPath = folder, IsFolder = true });
            AppConfig read = Reload(); string moved = Path.Combine(folder, "move.txt"), newKey = "add:" + moved;
            Check(!File.Exists(path) && File.ReadAllText(moved) == "keep physical contents", "file move corrupt");
            Check(Id(read.Database, newKey) == id && read.AddedItems.ByPath(moved).Count == 1, "identity changed on move");
            Check(read.Favorites.Contains(newKey) && read.Hidden.Contains(newKey) && read.ItemOrder["资料"][0] == newKey && read.ItemCategories[newKey].Contains(Collection), "relationship not moved");
            Check(read.Database.Page(Collection, true, true, "", 10, null, true, false).Total == 1, "moved projection missing"); Integrity(read.Database);
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
