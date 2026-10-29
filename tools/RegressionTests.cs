using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AppManager
{
    // Compile with src/*.cs and /main:AppManager.RegressionTests. No visible windows or real data writes.
    internal static class RegressionTests
    {
        private static int passed, failed;
        private static string root;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [STAThread]
        public static int Main()
        {
            Application.EnableVisualStyles();
            Control.CheckForIllegalCrossThreadCalls = true;
            root = Path.Combine(Path.GetTempPath(), "AppManagerRegression_" + Guid.NewGuid().ToString("N"));
            Paths.DataDirOverride = root;
            Paths.ConfigFileOverride = Path.Combine(root, "config.xml");
            Directory.CreateDirectory(root);
            try
            {
                Run("restore targets: missing desktop path / traversal / ADS / relative", Targets);
                Run("unsafe restore keeps source and creates no destination", RejectRestore);
                Run("copy: complete tree and file contents", CopyComplete);
                Run("copy limits: entries / bytes / timeout / depth", CopyLimits);
                Run("organizer copy failure leaves no partial target; retry succeeds", CopyRetry);
                Run("junction loop: copy rejects and preserves files", Junction);
                Run("failed restore preserves current config and files", RestoreFailure);
                Run("listing preserves old rollback data; quota refuses new restore", Retention);
                Run("DPI messages: effective DPI / suggested rectangle / repeated scaling", DpiMessages);
                Run("maximize stays in selected monitor work area and restores", MaximizeWithinScreenWorkArea);
                Run("window resize hit zones, minimum size, and fullscreen threshold", ResizeBounds);
                Run("menu hit regions and rendering at 100/125/150/175 percent", MenuDpi);
                Run("backup list row height at 100/125/150/175 percent", BackupDpi);
                Run("background status: queued update, disposal race, no handle", StatusRace);
                Run("category removal: hidden toggle, single/batch, reload and rejoin", CategoryRemoval);
                Run("category removal: override, extra membership and added file", CategoryRemovalSources);
                Run("resource defaults: remove, preserve library, reload and menu rejoin", ResourceCategoryRemoval);
                Run("category removal: failed save rolls back membership and permits retry", CategoryRemovalSaveFailure);
            }
            finally
            {
                Paths.ConfigFileOverride = null;
                Paths.DataDirOverride = null;
                Directory.Delete(root, true);
            }
            Console.WriteLine("RESULT " + passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        private static void Run(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static object Invoke(MainForm form, string method, params object[] args)
        {
            Type[] types = Array.ConvertAll(args, delegate(object arg) { return arg.GetType(); });
            return typeof(MainForm).GetMethod(method, Private, null, types, null).Invoke(form, args);
        }

        private static MainForm CategoryForm(AppConfig config, params AppInfo[] apps)
        {
            MainForm form = new MainForm(config);
            Invoke(form, "BuildControls");
            typeof(MainForm).GetField("currentCategory", Private).SetValue(form, "开发工具");
            typeof(MainForm).GetField("allApps", Private).SetValue(form, new List<AppInfo>(apps));
            // 本用例只验证归属与刷新；扫描结果在重新加载阶段注入，避免枚举真实系统环境。
            typeof(MainForm).GetField("scanRunning", Private).SetValue(form, true);
            return form;
        }

        private static void ClickConfirmation(Action action, bool accept)
        {
            bool clicked = false;
            Stopwatch elapsed = Stopwatch.StartNew();
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 10 })
            {
                timer.Tick += delegate
                {
                    Form dialog = null;
                    foreach (Form open in Application.OpenForms)
                        if (open.Modal) { dialog = open; break; }
                    if (dialog == null) return;
                    dialog.Opacity = 0;
                    foreach (Control button in dialog.Controls)
                        if (button.Text == (accept ? "确定" : "取消"))
                        {
                            timer.Stop();
                            typeof(Control).GetMethod("OnClick", Private).Invoke(button, new object[] { EventArgs.Empty });
                            clicked = true;
                            return;
                        }
                    if (elapsed.ElapsedMilliseconds > 3000) { timer.Stop(); dialog.Close(); }
                };
                timer.Start();
                action();
            }
            Check(clicked, "confirmation did not execute");
        }

        private static void CategoryRemoval()
        {
            foreach (bool showHidden in new bool[] { false, true })
            {
                AppConfig config = new AppConfig { ShowHidden = showHidden };
                AppInfo first = new AppInfo { Key = "af:regression-powershell", Name = "Windows PowerShell", Category = "开发工具", IsHidden = showHidden };
                AppInfo second = new AppInfo { Key = "af:regression-python", Name = "Python", Category = "开发工具" };
                if (showHidden) config.Hidden.Add(first.Key);
                config.Favorites.Add(first.Key);
                first.IsFavorite = true;
                using (MainForm form = CategoryForm(config, first, second))
                {
                    ClickConfirmation(delegate { Invoke(form, "RemoveFromCategory", first); }, false);
                    Check((bool)Invoke(form, "InCategory", first, "开发工具"), "cancel changed membership");
                    ClickConfirmation(delegate { Invoke(form, "RemoveFromCategory", first); }, true);
                    Check(!(bool)Invoke(form, "InCategory", first, "开发工具"), "single remove retained default category");
                    Check((bool)Invoke(form, "InCategory", first, "全部软件") && (bool)Invoke(form, "InCategory", first, "常用"), "remove affected other views");
                    Check(first.IsHidden == showHidden && config.Hidden.Contains(first.Key) == showHidden, "remove changed global hidden state");
                    Check(((List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form)).Count == 1, "grid did not refresh immediately");
                    ClickConfirmation(delegate { Invoke(form, "RemoveTargetsFromCategory", new List<AppInfo> { first, second }, "开发工具"); }, true);
                    Check(!(bool)Invoke(form, "InCategory", second, "开发工具"), "batch remove retained default category");
                }
                AppConfig loaded = AppConfig.Load();
                Check(!loaded.LoadFailed, "removed membership config could not reload");
                AppInfo fresh = new AppInfo { Key = first.Key, Name = first.Name, Category = "开发工具" };
                new Scanner(loaded).ApplyConfig(new List<AppInfo> { fresh });
                using (MainForm form = CategoryForm(loaded, fresh))
                {
                    Check(!(bool)Invoke(form, "InCategory", fresh, "开发工具"), "reload restored removed membership");
                    MenuWindow.MenuItem rejoin = (MenuWindow.MenuItem)Invoke(form, "MkAddCatMenu", fresh, "开发工具");
                    Check(!rejoin.Disabled, "software rejoin menu was disabled");
                    rejoin.Action();
                    Check((bool)Invoke(form, "InCategory", fresh, "开发工具"), "rejoin failed");
                    ClickConfirmation(delegate { Invoke(form, "RemoveFromCategory", fresh); }, true);
                    Check(!(bool)Invoke(form, "InCategory", fresh, "开发工具"), "overlapping extra/default membership survived removal");
                    Invoke(form, "SetHardCategory", fresh, "开发工具");
                    Check((bool)Invoke(form, "InCategory", fresh, "开发工具"), "move back failed");
                }
            }
        }

        private static void CategoryRemovalSources()
        {
            AppConfig config = new AppConfig { ShowHidden = true };
            AppInfo moved = new AppInfo { Key = "af:regression-edge", Name = "Microsoft Edge", Category = "开发工具" };
            AppInfo legacy = new AppInfo { Key = "lnk:regression-chrome", Name = "Google Chrome", Category = "开发工具" };
            AppInfo extra = new AppInfo { Key = "af:regression-extra", Name = "Extra", Category = "其他" };
            string path = Path.Combine(root, "category-kept.txt");
            File.WriteAllText(path, "keep");
            AppInfo file = new AppInfo { Key = "add:" + path, TargetPath = path, Name = "category-kept", Category = "开发工具", IsFile = true };
            config.CategoryOverride[moved.Key] = "开发工具";
            string legacyKey = "af:" + Scanner.NormalizeEntryName(legacy.Name);
            config.CategoryOverride[legacyKey] = "开发工具";
            CollectionOps.AddMember(config, extra.Key, "开发工具");
            CollectionOps.AddMember(config, extra.Key, "我的工具");
            config.AddedItems.Add(new AddedEntry { Path = path, Category = "开发工具" });
            config.AddedItems.Add(new AddedEntry { Path = path, Category = "开发工具" });
            using (MainForm form = CategoryForm(config, moved, legacy, extra, file))
            {
                ClickConfirmation(delegate { Invoke(form, "RemoveTargetsFromCategory", new List<AppInfo> { moved, legacy, extra, file }, "开发工具"); }, true);
                foreach (AppInfo app in new AppInfo[] { moved, legacy, extra, file })
                    Check(!(bool)Invoke(form, "InCategory", app, "开发工具"), "source survived removal: " + app.Key);
                Check(!config.CategoryOverride.ContainsKey(moved.Key) && !config.CategoryOverride.ContainsKey(legacyKey), "override was not revoked");
                Check((bool)Invoke(form, "InCategory", extra, "其他") && (bool)Invoke(form, "InCategory", extra, "我的工具"), "extra removal affected other categories");
                Check(config.AddedItems.Count == 2 && File.ReadAllText(path) == "keep", "category removal deleted library record or file");
                Check((bool)Invoke(form, "InCategory", file, "全部文件"), "file disappeared from all files");
            }
        }

        private static void ResourceCategoryRemoval()
        {
            string[] extensions = { ".png", ".mp4", ".mp3", ".txt", ".pdf", ".zip", ".bin" };
            for (int i = 0; i < CategoryRules.ResourceBuiltIn.Length; i++)
            foreach (bool showHidden in new bool[] { false, true })
            {
                string category = CategoryRules.ResourceBuiltIn[i];
                string path = Path.Combine(root, "resource-" + i + extensions[i]);
                File.WriteAllText(path, "keep resource");
                AppConfig config = new AppConfig { ShowHidden = showHidden };
                config.AddedItems.Add(new AddedEntry { Path = path, Category = category, IsResource = true });
                AppInfo app = new AppInfo { Key = "add:" + path, TargetPath = path, Name = "resource-" + i, Category = category, IsFile = true };
                string collection = CollectionKeys.Make(category, "保留集合");
                config.Collections.Add(new ResourceCollection { Parent = category, Name = "保留集合" });
                CollectionOps.AddMember(config, app.Key, collection);
                using (MainForm form = CategoryForm(config))
                {
                    typeof(MainForm).GetField("contentMode", Private).SetValue(form, Controls.SidebarControl.ContentMode.Website);
                    typeof(MainForm).GetField("currentCategory", Private).SetValue(form, category);
                    if (showHidden)
                        ClickConfirmation(delegate { Invoke(form, "RemoveTargetsFromCategory", new List<AppInfo> { app }, category); }, true);
                    else
                        ClickConfirmation(delegate { Invoke(form, "RemoveFromCategory", app); }, true);
                    Check(!(bool)Invoke(form, "InCategory", app, category), "resource stayed in default category: " + category);
                    Check((bool)Invoke(form, "InCategory", app, "全部资源"), "resource left library: " + category);
                    Check(config.ItemCategories[app.Key].Contains(collection), "category removal deleted collection membership");
                    Check(!(bool)typeof(MainForm).GetField("scanPending", Private).GetValue(form), "category-only removal requested full scan");
                }
                AppConfig loaded = AppConfig.Load();
                Scanner scanner = new Scanner(loaded);
                Dictionary<string, AppInfo> entries = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
                typeof(Scanner).GetMethod("ScanAdded", Private).Invoke(scanner, new object[] { entries });
                Check(entries.ContainsKey(path), "resource was lost when rescanning library: " + category);
                AppInfo fresh = entries[path];
                scanner.ApplyConfig(new List<AppInfo> { fresh });
                using (MainForm form = CategoryForm(loaded))
                {
                    typeof(MainForm).GetField("contentMode", Private).SetValue(form, Controls.SidebarControl.ContentMode.Website);
                    Check(!(bool)Invoke(form, "InCategory", fresh, category) && (bool)Invoke(form, "InCategory", fresh, "全部资源"), "resource removal did not survive reload: " + category);
                    List<MenuWindow.MenuItem> menus = (List<MenuWindow.MenuItem>)Invoke(form, "BuildResourceAddItems", fresh);
                    MenuWindow.MenuItem rejoin = menus.Find(delegate(MenuWindow.MenuItem item) { return item.Text == category; });
                    Check(rejoin != null && !rejoin.Disabled, "resource rejoin menu was disabled: " + category);
                    rejoin.Action();
                    Check((bool)Invoke(form, "InCategory", fresh, category), "resource menu rejoin failed: " + category);
                    Check((bool)Invoke(form, "InCategory", fresh, collection), "collection membership was lost after rejoin");
                }
                Check(File.ReadAllText(path) == "keep resource", "resource content changed");
            }
        }

        private static void CategoryRemovalSaveFailure()
        {
            AppConfig config = new AppConfig();
            AppInfo normal = new AppInfo { Key = "af:save-normal", Name = "Python", Category = "开发工具" };
            AppInfo extra = new AppInfo { Key = "af:save-extra", Name = "Extra", Category = "其他" };
            AppInfo moved = new AppInfo { Key = "lnk:save-moved", Name = "Google Chrome", Category = "开发工具" };
            CollectionOps.AddMember(config, extra.Key, "开发工具");
            string legacy = "af:" + Scanner.NormalizeEntryName(moved.Name);
            config.CategoryOverride[moved.Key] = config.CategoryOverride[legacy] = "开发工具";
            config.ExcludedCategories[normal.Key] = new List<string> { "办公软件" };
            Check(config.Save(), "save failure fixture failed");
            string before = File.ReadAllText(Paths.ConfigFile);
            using (MainForm form = CategoryForm(config))
            {
                List<AppInfo> targets = new List<AppInfo> { normal, extra, moved, normal };
                using (FileStream locked = new FileStream(Paths.ConfigFile + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                    ClickConfirmation(delegate { Invoke(form, "RemoveTargetsFromCategory", targets, "开发工具"); }, true);
                foreach (AppInfo app in targets)
                    Check((bool)Invoke(form, "InCategory", app, "开发工具"), "failed save changed membership: " + app.Key);
                Check(config.ExcludedCategories[normal.Key].Count == 1 && config.ExcludedCategories[normal.Key][0] == "办公软件", "rollback damaged prior exclusions");
                Check(config.CategoryOverride[moved.Key] == "开发工具" && config.CategoryOverride[legacy] == "开发工具", "rollback lost overrides");
                Check(File.ReadAllText(Paths.ConfigFile) == before, "failed save changed persisted config");
                Check(!(bool)typeof(MainForm).GetField("scanPending", Private).GetValue(form), "failed save requested scan");
                ClickConfirmation(delegate { Invoke(form, "RemoveTargetsFromCategory", targets, "开发工具"); }, true);
                foreach (AppInfo app in targets)
                    Check(!(bool)Invoke(form, "InCategory", app, "开发工具"), "retry after save failure did not remove item");
                before = File.ReadAllText(Paths.ConfigFile);
                MenuWindow.MenuItem rejoin = (MenuWindow.MenuItem)Invoke(form, "MkAddCatMenu", normal, "开发工具");
                using (FileStream locked = new FileStream(Paths.ConfigFile + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                    rejoin.Action();
                Check(!(bool)Invoke(form, "InCategory", normal, "开发工具"), "failed rejoin changed membership");
                Check(File.ReadAllText(Paths.ConfigFile) == before, "failed rejoin changed persisted config");
                rejoin.Action();
                Check((bool)Invoke(form, "InCategory", normal, "开发工具"), "rejoin retry failed");
            }
        }

        private static void Targets()
        {
            string desktop = DesktopOrganizer.UserDesktop;
            Check(DesktopOrganizer.IsSafeRestoreTarget(Path.Combine(desktop, "not-created-" + Guid.NewGuid(), "file.txt")), "missing target rejected");
            Check(!DesktopOrganizer.IsSafeRestoreTarget(desktop), "desktop root accepted");
            Check(!DesktopOrganizer.IsSafeRestoreTarget(Path.Combine(desktop, "..", "outside.txt")), "traversal accepted");
            Check(!DesktopOrganizer.IsSafeRestoreTarget(Path.Combine(desktop, "file.txt:stream")), "ADS accepted");
            Check(!DesktopOrganizer.IsSafeRestoreTarget("relative.txt"), "relative accepted");
        }

        private static void RejectRestore()
        {
            Directory.CreateDirectory(Paths.DesktopBackupDir);
            string source = Path.Combine(Paths.DesktopBackupDir, "reject.txt");
            File.WriteAllText(source, "keep");
            string target = Path.Combine(root, "outside", "target.txt");
            DesktopBackupEntry entry = new DesktopBackupEntry { CurrentPath = source, OriginalPath = target };
            Check(!new DesktopOrganizer(new AppConfig()).RestoreOne(entry), "unsafe restore succeeded");
            Check(File.ReadAllText(source) == "keep" && !Directory.Exists(Path.GetDirectoryName(target)), "restore changed files");
        }

        private static void CopyComplete()
        {
            string source = Path.Combine(root, "copy-source");
            Directory.CreateDirectory(Path.Combine(source, "sub"));
            File.WriteAllText(Path.Combine(source, "sub", "x.txt"), "payload");
            string dest = Path.Combine(root, "copy-target");
            SafeDirectory.Copy(source, dest, false);
            Check(File.ReadAllText(Path.Combine(dest, "sub", "x.txt")) == "payload", "incomplete copy");
        }

        private static void CopyLimits()
        {
            string source = Path.Combine(root, "copy-source");
            SafeDirectory.Budget[] budgets = {
                new SafeDirectory.Budget(1, 100, 10000),
                new SafeDirectory.Budget(100, 1, 10000),
                new SafeDirectory.Budget(100, 100, 0)
            };
            for (int i = 0; i < budgets.Length; i++)
            {
                string target = Path.Combine(root, "limit-" + i);
                bool rejected = false;
                try { SafeDirectory.Copy(source, target, false, budgets[i]); }
                catch (IOException) { rejected = true; }
                Check(rejected && !Directory.Exists(target), "limit allowed a partial success");
            }
            string deep = Path.Combine(root, "deep");
            string path = deep;
            for (int i = 0; i < 66; i++) path = Path.Combine(path, "d");
            Directory.CreateDirectory(path);
            bool depthRejected = false;
            try { SafeDirectory.Measure(deep, new SafeDirectory.Budget()); }
            catch (IOException) { depthRejected = true; }
            Check(depthRejected, "depth limit ignored");
        }

        private static void Junction()
        {
            string source = Path.Combine(root, "junction-source");
            Directory.CreateDirectory(source);
            string link = Path.Combine(source, "loop");
            ProcessStartInfo start = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + source + "\"");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            using (Process process = Process.Start(start))
            {
                Check(process.WaitForExit(10000) && process.ExitCode == 0, "cannot create test junction");
            }
            try
            {
                string dest = Path.Combine(root, "junction-target");
                bool rejected = false;
                try { SafeDirectory.Copy(source, dest, false); }
                catch (IOException) { rejected = true; }
                Check(rejected && !Directory.Exists(dest), "junction followed or silently skipped");
                rejected = false;
                try { SafeDirectory.Measure(source, new SafeDirectory.Budget()); }
                catch (IOException) { rejected = true; }
                Check(rejected, "measurement followed junction");
            }
            finally { Directory.Delete(link); }
        }

        private static void CopyRetry()
        {
            string source = Path.Combine(root, "copy-source");
            string target = Path.Combine(root, "retry-target");
            MethodInfo copy = typeof(DesktopOrganizer).GetMethod("CopyDir", BindingFlags.Static | BindingFlags.NonPublic);
            bool rejected = false;
            using (FileStream locked = new FileStream(Path.Combine(source, "sub", "x.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { copy.Invoke(null, new object[] { source, target }); }
                catch (TargetInvocationException) { rejected = true; }
            }
            Check(rejected && !Directory.Exists(target), "failed copy left registered target");
            Check(Directory.GetDirectories(root, "retry-target.copy-*").Length == 0, "staging leaked");
            copy.Invoke(null, new object[] { source, target });
            Check(File.ReadAllText(Path.Combine(target, "sub", "x.txt")) == "payload", "retry failed");
        }

        private static string Snapshot()
        {
            string snapshot = Path.Combine(Paths.BackupsDir, "fixture");
            Directory.CreateDirectory(Path.Combine(snapshot, "DesktopBackup"));
            new AppConfig().SaveTo(Path.Combine(snapshot, "config.xml"));
            File.WriteAllText(Path.Combine(snapshot, "meta.xml"), "<backup name='fixture'/>");
            File.WriteAllText(Path.Combine(snapshot, "DesktopBackup", "saved.txt"), "saved");
            return snapshot;
        }

        private static void RestoreFailure()
        {
            string snapshot = Snapshot();
            AppConfig live = new AppConfig();
            live.CustomCategories.Add("live");
            Check(live.Save(), "fixture save failed");
            string error;
            using (FileStream locked = new FileStream(Path.Combine(snapshot, "DesktopBackup", "saved.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
                Check(!LayoutBackup.Restore("fixture", out error), "locked source restore succeeded");
            Check(AppConfig.Load().CustomCategories.Contains("live"), "config changed on failure");
            Check(File.Exists(Path.Combine(Paths.DesktopBackupDir, "reject.txt")), "live file lost");
        }

        private static void Retention()
        {
            for (int i = 0; i < 3; i++)
            {
                string path = Path.Combine(Paths.BackupsDir, "_prerestore_fixture" + i);
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "unique.txt"), "only copy");
                Directory.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-30));
            }
            LayoutBackup.List();
            Check(File.Exists(Path.Combine(Paths.BackupsDir, "_prerestore_fixture0", "unique.txt")), "listing deleted data");
            int before = Directory.GetDirectories(Paths.BackupsDir, "_prerestore_*").Length;
            string error;
            Check(!LayoutBackup.Restore("fixture", out error), "quota did not block restore");
            Check(before == Directory.GetDirectories(Paths.BackupsDir, "_prerestore_*").Length, "quota created new transaction");
        }

        private static void Initialize(Form form)
        {
            IntPtr handle = form.Handle;
            typeof(DpiAwareForm).GetMethod("InitializeDpiScaling", Private).Invoke(form, null);
        }

        private static void SetDpi(Form form, int dpi, Rectangle bounds)
        {
            IntPtr rect = Marshal.AllocHGlobal(16);
            try
            {
                Marshal.Copy(new int[] { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom }, 0, rect, 4);
                Message message = Message.Create(form.Handle, 0x02E0, new IntPtr(dpi | (dpi << 16)), rect);
                typeof(DpiAwareForm).GetMethod("WndProc", Private).Invoke(form, new object[] { message });
            }
            finally { Marshal.FreeHGlobal(rect); }
        }

        private static void DpiMessages()
        {
            using (DpiAwareForm form = new DpiAwareForm())
            {
                form.FormBorderStyle = FormBorderStyle.None;
                form.ClientSize = new Size(400, 200);
                Button button = new Button { Bounds = new Rectangle(24, 60, 80, 32) };
                form.Controls.Add(button);
                Initialize(form);
                Rectangle work = Screen.PrimaryScreen.WorkingArea;
                for (int cycle = 0; cycle < 3; cycle++)
                    foreach (int dpi in new int[] { 96, 120, 144, 168, 96 })
                    {
                        Rectangle proposed = new Rectangle(work.Left + 10, work.Top + 10, DpiHelper.Scale(400, dpi), DpiHelper.Scale(200, dpi));
                        SetDpi(form, dpi, proposed);
                        Check(form.LayoutDpi == dpi && DpiHelper.ForControl(button) == dpi,
                            "paint/layout DPI diverged: " + form.LayoutDpi + " expected " + dpi);
                        Check(form.Bounds == proposed, "suggested bounds not applied");
                        Check(Math.Abs(button.Left - DpiHelper.Scale(24, dpi)) <= 2, "control scaling drift");
                    }
                form.MinimumSize = new Size(work.Width + 500, work.Height + 500);
                SetDpi(form, 144, new Rectangle(work.Right - 10, work.Bottom - 10, work.Width + 200, work.Height + 200));
                Check(work.Contains(form.Bounds), "window exceeds work area");
            }
        }

        private static void MaximizeWithinScreenWorkArea()
        {
            Screen target = Screen.PrimaryScreen;
            foreach (Screen screen in Screen.AllScreens)
            {
                if (screen.Bounds.Left < 0)
                {
                    target = screen;
                    break;
                }
            }

            using (MainForm form = new MainForm(new AppConfig()))
            {
                form.FormBorderStyle = FormBorderStyle.None;
                form.StartPosition = FormStartPosition.Manual;
                Rectangle work = target.WorkingArea;
                int width = Math.Min(600, work.Width);
                int height = Math.Min(400, work.Height);
                form.Bounds = new Rectangle(work.Left + (work.Width - width) / 2, work.Top + (work.Height - height) / 2, width, height);
                form.CreateControl();
                Rectangle before = form.Bounds;
                typeof(MainForm).GetMethod("MaximizeOnCurrentScreen", Private).Invoke(form, null);
                Check(form.WindowState == FormWindowState.Normal, "native maximize still used");
                Check(form.Bounds == work, "maximized window left its monitor work area");
                typeof(MainForm).GetMethod("RestoreFromMaximized", Private).Invoke(form, null);
                Check(form.Bounds == before, "window was not restored to its previous bounds");
            }
        }

        private static void ResizeBounds()
        {
            using (MainForm form = new MainForm(new AppConfig()))
            {
                form.ClientSize = new Size(1000, 700);
                form.MinimumSize = new Size(960, 560);
                form.CreateControl();
                MethodInfo hitTest = typeof(MainForm).GetMethod("ResizeHitTest", Private);
                Check((int)hitTest.Invoke(form, new object[] { new Point(0, 350) }) == 10, "left resize hit missing");
                Check((int)hitTest.Invoke(form, new object[] { new Point(999, 350) }) == 11, "right resize hit missing");
                Check((int)hitTest.Invoke(form, new object[] { new Point(500, 0) }) == 12, "top resize hit missing");
                Check((int)hitTest.Invoke(form, new object[] { new Point(0, 0) }) == 13, "top-left resize hit missing");
                Check((int)hitTest.Invoke(form, new object[] { new Point(999, 699) }) == 17, "bottom-right resize hit missing");
                Check((int)hitTest.Invoke(form, new object[] { new Point(500, 350) }) == 0, "client area became a resize zone");

                MethodInfo constrain = typeof(MainForm).GetMethod("ConstrainResizeBounds", Private);
                Rectangle work = Screen.PrimaryScreen.WorkingArea;
                object[] minimumArgs = { new Rectangle(work.Left + 100, work.Top + 100, 500, 300), 2, work, null };
                Rectangle minimum = (Rectangle)constrain.Invoke(form, minimumArgs);
                Check(minimum.Width == 960 && minimum.Height == 560,
                    "minimum resize bounds not enforced: " + minimum.Width + "x" + minimum.Height + " min=" + form.MinimumSize);
                Check(!(bool)minimumArgs[3], "minimum resize incorrectly entered fullscreen");

                object[] maximumArgs = { new Rectangle(work.Left, work.Top, work.Width + 1, work.Height), 2, work, null };
                constrain.Invoke(form, maximumArgs);
                Check((bool)maximumArgs[3], "maximum resize did not request fullscreen");

                typeof(MainForm).GetMethod("EnterFormFullscreen", Private).Invoke(form, null);
                Rectangle fullscreen = form.Bounds;
                IntPtr sizing = Marshal.AllocHGlobal(16);
                try
                {
                    Marshal.Copy(new int[] { work.Left, work.Top, work.Left + 100, work.Top + 100 }, 0, sizing, 4);
                    Message message = Message.Create(form.Handle, 0x0214, IntPtr.Zero, sizing);
                    typeof(MainForm).GetMethod("WndProc", Private).Invoke(form, new object[] { message });
                    Check(form.Bounds == fullscreen, "fullscreen was undone by a later sizing message");
                }
                finally { Marshal.FreeHGlobal(sizing); }
            }
        }

        private static void MenuDpi()
        {
            using (MenuWindow menu = new MenuWindow(new List<MenuWindow.MenuItem> {
                new MenuWindow.MenuItem { Text = "first" }, new MenuWindow.MenuItem { Separator = true }, new MenuWindow.MenuItem { Text = "last" }
            }, 200))
            {
                Initialize(menu);
                foreach (int dpi in new int[] { 96, 120, 144, 168, 96 })
                {
                    int height = DpiHelper.Scale(8, dpi) + 2 * DpiHelper.Scale(32, dpi) + DpiHelper.Scale(9, dpi);
                    SetDpi(menu, dpi, new Rectangle(10, 10, DpiHelper.Scale(200, dpi), height));
                    MethodInfo top = typeof(MenuWindow).GetMethod("ItemTop", Private);
                    MethodInfo at = typeof(MenuWindow).GetMethod("IndexAt", Private);
                    int last = (int)top.Invoke(menu, new object[] { 2 });
                    Check((int)at.Invoke(menu, new object[] { last + DpiHelper.Scale(16, dpi) }) == 2, "wrong row hit");
                    Check((int)at.Invoke(menu, new object[] { 0 }) == -1, "top padding hits first item");
                    Check(menu.Width == DpiHelper.Scale(200, dpi), "menu width incorrect");
                    using (Bitmap bitmap = new Bitmap(menu.Width, menu.Height)) menu.DrawToBitmap(bitmap, menu.ClientRectangle);
                }
            }
        }

        private static void BackupDpi()
        {
            using (BackupForm form = new BackupForm())
            {
                Initialize(form);
                ListBox list = (ListBox)typeof(BackupForm).GetField("list", Private).GetValue(form);
                foreach (int dpi in new int[] { 96, 120, 144, 168 })
                {
                    SetDpi(form, dpi, new Rectangle(10, 10, DpiHelper.Scale(620, dpi), DpiHelper.Scale(486, dpi)));
                    Check(list.ItemHeight == DpiHelper.Scale(54, dpi), "list row height incorrect");
                }
            }
        }

        private static void StatusRace()
        {
            MethodInfo update = typeof(MainForm).GetMethod("UpdateStatus", Private);
            for (int round = 0; round < 20; round++)
            {
                using (MainForm form = new MainForm(new AppConfig()))
                {
                    Panel panel = new Panel();
                    Controls.IosProgressBar progress = new Controls.IosProgressBar();
                    Label detail = new Label();
                    typeof(MainForm).GetField("statusBar", Private).SetValue(form, panel);
                    typeof(MainForm).GetField("statusProgress", Private).SetValue(form, progress);
                    typeof(MainForm).GetField("statusDetail", Private).SetValue(form, detail);
                    panel.Controls.Add(progress);
                    panel.Controls.Add(detail);
                    form.Controls.Add(panel);
                    if (round != 0) { IntPtr handle = panel.Handle; }
                    if (round != 0)
                    {
                        Thread oneUpdate = new Thread(delegate() { update.Invoke(form, new object[] { 1, 500, "queued" }); });
                        oneUpdate.Start();
                        Check(oneUpdate.Join(10000), "queue worker hung");
                        Application.DoEvents();
                        Check(detail.Text.Contains("queued"), "normal queued status update lost");
                    }
                    Exception failure = null;
                    Thread worker = new Thread(delegate()
                    {
                        try { for (int i = 0; i < 500; i++) update.Invoke(form, new object[] { i, 500, "file" }); }
                        catch (Exception ex) { failure = ex; }
                    });
                    worker.Start();
                    Application.DoEvents();
                    form.Dispose();
                    Check(worker.Join(10000), "status worker hung");
                    Application.DoEvents();
                    Check(failure == null, "status exception: " + failure);
                }
            }
        }
    }
}
