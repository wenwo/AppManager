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
                Run("default category addition: single/batch, multiple memberships, removal and reload", DefaultCategoryAddition);
                Run("properties: software/resource menus, grid/list and complete memberships", PropertiesMenus);
                Run("properties: file metadata, folders, missing targets and packaged apps", PropertiesMetadata);
                Run("properties: DPI scrolling, responsive open and cancelled background load", PropertiesResponsiveAndDpi);
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
            if (config.Database == null) Check(config.Save(), "database fixture save failed");
            config.Database.PublishCatalog(config, new List<AppInfo>(apps));
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

        private static void InspectProperties(Action open, string categories, string path, Action<Form> inspect = null, bool waitForMetadata = false)
        {
            bool inspected = false;
            Exception failure = null;
            Stopwatch elapsed = Stopwatch.StartNew();
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 10 })
            {
                timer.Tick += delegate
                {
                    Form dialog = null;
                    foreach (Form form in Application.OpenForms) if (form.Modal) { dialog = form; break; }
                    if (dialog == null) return;
                    if (waitForMetadata && (bool)dialog.GetType().GetField("loading", Private).GetValue(dialog) && elapsed.ElapsedMilliseconds < 5000) return;
                    timer.Stop();
                    dialog.Opacity = 0;
                    try
                    {
                        TextBox category = dialog.Controls.Find("CategoryValue", true)[0] as TextBox;
                        TextBox target = dialog.Controls.Find("TargetPathValue", true)[0] as TextBox;
                        Check(category != null && target != null, "property fields missing");
                        Check(category.Text == categories && target.Text == path, "property content truncated or stale");
                        foreach (TextBox box in new TextBox[] { category, target })
                        {
                            Check(box.ReadOnly && box.Multiline && box.ScrollBars == ScrollBars.Vertical, "long values cannot be read safely");
                            box.SelectAll();
                            Check(box.SelectedText == box.Text, "property content cannot be selected for copy");
                            Check(dialog.ClientRectangle.Contains(dialog.RectangleToClient(box.RectangleToScreen(box.ClientRectangle))), "property field exceeds dialog");
                        }
                        Check(!category.Bounds.IntersectsWith(target.Bounds), "property fields overlap");
                        if (waitForMetadata) Check(!(bool)dialog.GetType().GetField("loading", Private).GetValue(dialog), "metadata did not finish");
                        if (inspect != null) inspect(dialog);
                        inspected = true;
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { dialog.DialogResult = DialogResult.OK; dialog.Close(); }
                };
                timer.Start();
                open();
            }
            if (failure != null) throw failure;
            Check(inspected, "properties did not open");
        }

        private static void PropertiesMenus()
        {
            foreach (bool resource in new bool[] { false, true })
            {
                AppConfig config = new AppConfig();
                string category = resource ? "照片" : "开发工具";
                string path = Path.Combine(root, resource ? "property.png" : "property.exe");
                File.WriteAllText(path, "keep");
                AppInfo app = new AppInfo { Key = "property:" + resource, Name = "Property", TargetPath = path,
                    Category = category, IsFile = resource, IsFavorite = true, Icon = new Bitmap(16, 16) };
                config.Favorites.Add(app.Key);
                string extra = "我的分类" + new string('长', 100);
                CollectionOps.AddMember(config, app.Key, extra);
                CollectionOps.AddMember(config, app.Key, "办公软件");
                CollectionOps.AddMember(config, app.Key, "col:" + category + "/fixture");
                config.ExcludedCategories[app.Key] = new List<string> { "办公软件" };
                if (resource) config.AddedItems.Add(new AddedEntry { Path = path, Category = category, IsResource = true });
                using (MainForm form = CategoryForm(config, app))
                {
                    typeof(MainForm).GetField("contentMode", Private).SetValue(form,
                        resource ? Controls.SidebarControl.ContentMode.Website : Controls.SidebarControl.ContentMode.Software);
                    Controls.AppGridControl grid = (Controls.AppGridControl)typeof(MainForm).GetField("grid", Private).GetValue(form);
                    foreach (bool gridMode in new bool[] { true, false })
                        foreach (bool all in new bool[] { false, true })
                        {
                            grid.GridMode = gridMode;
                            typeof(MainForm).GetField("currentCategory", Private).SetValue(form, all ? (resource ? "全部资源" : "全部软件") : category);
                            List<MenuWindow.MenuItem> menu = (List<MenuWindow.MenuItem>)Invoke(form, "BuildContextMenu", app);
                            Check(menu.FindAll(delegate(MenuWindow.MenuItem m) { return m.Text == "属性…"; }).Count == 1, "missing or duplicate properties menu");
                            MenuWindow.MenuItem properties = FindMenu(menu, "属性…");
                            Check(!properties.Disabled, "properties unexpectedly disabled");
                            InspectProperties(properties.Action, category + ", 常用, " + extra, path);
                        }
                }
                app.Icon.Dispose();
                Check(File.ReadAllText(path) == "keep", "properties changed original file");
            }
        }

        private static void PropertiesMetadata()
        {
            string photo = Path.Combine(root, "property-metadata.png");
            using (Bitmap bitmap = new Bitmap(3, 2)) bitmap.Save(photo, System.Drawing.Imaging.ImageFormat.Png);
            AppInfo[] apps = {
                new AppInfo { TargetPath = photo, IsFile = true },
                new AppInfo { TargetPath = root, IsFile = true, IsFolder = true },
                new AppInfo { TargetPath = Path.Combine(root, "missing.txt"), IsFile = true },
                new AppInfo { TargetPath = "shell:AppsFolder\\Fixture.App_123!App", IsUwp = true }
            };
            foreach (AppInfo app in apps)
            {
                object[] args = { app, "分类一, 分类二", null, CancellationToken.None };
                List<string[]> rows = (List<string[]>)typeof(PropertiesDialog).GetMethod("BuildRows", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);
                using (Bitmap preview = (Bitmap)args[2])
                {
                    Check(rows.Exists(delegate(string[] row) { return row[0] == "所属" && row[1] == "分类一, 分类二"; }), "metadata lost categories");
                    Check(rows.Exists(delegate(string[] row) { return row[0] == "路径" && row[1] == app.TargetPath; }), "metadata lost full path");
                    if (app.TargetPath == photo)
                        Check(preview != null && rows.Exists(delegate(string[] row) { return row[0] == "尺寸" && row[1] == "3 × 2"; }), "photo metadata was lost");
                    if (!File.Exists(app.TargetPath) && !Directory.Exists(app.TargetPath))
                        Check(!rows.Exists(delegate(string[] row) { return row[0] == "创建时间" || row[0] == "修改时间"; }), "non-file target has fake timestamps");
                }
                if (app.IsUwp || app.TargetPath.EndsWith("missing.txt"))
                    InspectProperties(delegate { PropertiesDialog.Show(app, "分类一, 分类二"); }, "分类一, 分类二", app.TargetPath);
            }
        }

        private static void PropertiesResponsiveAndDpi()
        {
            string photo = Path.Combine(root, "property-async.png");
            using (Bitmap bitmap = new Bitmap(3, 2)) bitmap.Save(photo, System.Drawing.Imaging.ImageFormat.Png);
            AppInfo app = new AppInfo { TargetPath = photo, Name = "Photo", IsFile = true, Icon = new Bitmap(16, 16) };
            SemaphoreSlim gate = (SemaphoreSlim)typeof(PropertiesDialog).GetField("PreviewGate", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            object load = null;
            int held = 1;
            gate.Wait();
            Stopwatch elapsed = Stopwatch.StartNew();
            using (System.Threading.Timer release = new System.Threading.Timer(delegate
            {
                if (Interlocked.Exchange(ref held, 0) == 1) gate.Release();
            }, null, 2000, Timeout.Infinite))
            try
            {
                InspectProperties(delegate { PropertiesDialog.Show(app, "照片"); }, "照片", photo, delegate(Form dialog)
                {
                    Check(elapsed.ElapsedMilliseconds < 1000, "opening waited for image decoding");
                    load = dialog.GetType().GetField("load", Private).GetValue(dialog);
                    foreach (int dpi in new int[] { 96, 144, 168, 192, 240, 288 })
                    {
                        Rectangle work = Screen.PrimaryScreen.WorkingArea;
                        SetDpi(dialog, dpi, new Rectangle(work.Location, new Size(520 * dpi / 96, 440 * dpi / 96)));
                        Control done = dialog.Controls["PropertiesDone"];
                        Panel content = (Panel)dialog.Controls["PropertyContent"];
                        Check(work.Contains(dialog.Bounds), "properties exceed work area");
                        Check(dialog.ClientRectangle.Contains(done.Bounds), "Done button is clipped at DPI " + dpi);
                        Check(content.Bottom <= done.Top, "scrolling content overlaps Done button");
                        TextBox target = (TextBox)content.Controls["TargetPathValue"];
                        content.ScrollControlIntoView(target);
                        Check(content.ClientRectangle.Contains(target.Bounds), "path cannot be scrolled into view at DPI " + dpi);
                    }
                });
            }
            finally { if (Interlocked.Exchange(ref held, 0) == 1) gate.Release(); }
            Check(load != null && (bool)load.GetType().GetField("closed", Private).GetValue(load), "closing did not cancel metadata");
            Stopwatch cancellation = Stopwatch.StartNew();
            while (!(bool)load.GetType().GetField("finished", Private).GetValue(load) && cancellation.ElapsedMilliseconds < 3000) Thread.Sleep(10);
            Check((bool)load.GetType().GetField("finished", Private).GetValue(load), "cancelled load did not finish");
            Check(load.GetType().GetField("result", Private).GetValue(load) == null, "closed dialog retained background result");
            InspectProperties(delegate { PropertiesDialog.Show(app, "照片"); }, "照片", photo, delegate(Form dialog)
            {
                Check(dialog.GetType().GetField("preview", Private).GetValue(dialog) != null, "background preview was not delivered");
                List<string[]> rows = (List<string[]>)dialog.GetType().GetField("rows", Private).GetValue(dialog);
                Check(rows.Exists(delegate(string[] row) { return row[0] == "尺寸" && row[1] == "3 × 2"; }), "background metadata was not delivered");
            }, true);
            app.Icon.Dispose();
        }

        private static MenuWindow.MenuItem FindMenu(List<MenuWindow.MenuItem> items, string text)
        {
            MenuWindow.MenuItem item = items.Find(delegate(MenuWindow.MenuItem m) { return m.Text == text; });
            Check(item != null, "menu missing: " + text);
            return item;
        }

        private static void DefaultCategoryAddition()
        {
            AppConfig config = new AppConfig();
            AppInfo first = new AppInfo { Key = "af:category-add-first", Name = "First", Category = "开发工具", TargetPath = Path.Combine(root, "kept.exe") };
            AppInfo second = new AppInfo { Key = "af:category-add-second", Name = "Second", Category = "浏览器" };
            File.WriteAllText(first.TargetPath, "keep");
            config.CustomCategories.Add("我的工具");
            CollectionOps.AddMember(config, first.Key, "我的工具");
            config.CategoryOverride[second.Key] = "浏览器";
            config.Favorites.Add(first.Key);
            first.IsFavorite = true;
            List<AppInfo> targets = new List<AppInfo> { first, second };
            using (MainForm form = CategoryForm(config, first, second))
            {
                List<MenuWindow.MenuItem> single = (List<MenuWindow.MenuItem>)Invoke(form, "BuildAddCategoryItems", first, false);
                Check(FindMenu(single, "开发工具").Disabled, "original category should be disabled");
                Check(!single.Exists(delegate(MenuWindow.MenuItem m) { return m.Text == "我的工具"; }), "default menu included custom category");
                FindMenu(single, "办公软件").Action();
                FindMenu(single, "系统工具").Action();
                Check(first.Category == "开发工具" && !config.CategoryOverride.ContainsKey(first.Key), "addition replaced primary category");
                foreach (string category in new string[] { "开发工具", "办公软件", "系统工具", "我的工具", "常用" })
                    Check((bool)Invoke(form, "InCategory", first, category), "addition lost membership: " + category);
                single = (List<MenuWindow.MenuItem>)Invoke(form, "BuildAddCategoryItems", first, false);
                Check(FindMenu(single, "办公软件").Disabled, "existing membership was not disabled");
                int count = config.ItemCategories[first.Key].Count;
                FindMenu(single, "办公软件").Action();
                Check(config.ItemCategories[first.Key].Count == count, "duplicate membership added");

                foreach (bool readOnly in new bool[] { false, true })
                {
                    List<MenuWindow.MenuItem> batch = new List<MenuWindow.MenuItem>();
                    Invoke(form, "BuildSoftwareMultiMenu", batch, first, targets, readOnly);
                    List<MenuWindow.MenuItem> defaults = FindMenu(batch, "添加到默认分类…").SubItems;
                    MenuWindow.MenuItem office = FindMenu(defaults, "办公软件");
                    Check(office.Disabled == readOnly, "batch availability did not reflect partial/all membership");
                    if (!office.Disabled) office.Action();
                    if (readOnly) FindMenu(defaults, "快捷中心").Action();
                }
                Check(config.CategoryOverride[second.Key] == "浏览器" && second.Category == "浏览器", "batch addition changed existing override");
                Check((bool)Invoke(form, "InCategory", second, "浏览器"), "batch addition lost original membership");
                Check(config.Database.Page("办公软件", false, true, "", 20, null, false, false).Total == 2, "database list lost batch membership");
                Check(File.ReadAllText(first.TargetPath) == "keep", "addition moved or changed the file");
            }

            AppConfig loaded = AppConfig.Load();
            Check(!loaded.LoadFailed, "addition could not reload");
            new Scanner(loaded).ApplyConfig(targets);
            using (MainForm form = CategoryForm(loaded, first, second))
            {
                foreach (AppInfo app in targets)
                    foreach (string category in new string[] { "办公软件", "快捷中心" })
                        Check((bool)Invoke(form, "InCategory", app, category), "reload lost membership: " + category);
                Check((bool)Invoke(form, "InCategory", first, "我的工具") && (bool)Invoke(form, "InCategory", first, "系统工具"), "reload lost other memberships");
                ClickConfirmation(delegate { Invoke(form, "RemoveTargetsFromCategory", new List<AppInfo> { first }, "办公软件"); }, true);
                Check(!(bool)Invoke(form, "InCategory", first, "办公软件"), "removal kept target membership");
                foreach (string category in new string[] { "开发工具", "系统工具", "我的工具", "快捷中心" })
                    Check((bool)Invoke(form, "InCategory", first, category), "removal affected another category: " + category);
            }
            AppConfig afterRemoval = AppConfig.Load();
            Check(!afterRemoval.LoadFailed, "removal could not reload");
            Check(afterRemoval.Database.Page("办公软件", false, true, "", 20, null, false, false).Total == 1, "removal affected other batch member");
            Check(afterRemoval.Database.Page("开发工具", false, true, "", 20, null, false, false).Total == 1, "original category lost after reload");
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
                Check(config.AddedItems.Count == 1 && File.ReadAllText(path) == "keep", "category removal deleted library record or file");
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
                CatalogPage entries = loaded.Database.Page("全部资源", true, true, "", 100, null, true, false);
                AppInfo fresh = entries.Items.Find(delegate(AppInfo a) { return a.TargetPath == path; });
                Check(fresh != null, "resource was lost when reloading library: " + category);
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
            string before = AppConfig.Load().ToDocument().OuterXml;
            using (MainForm form = CategoryForm(config))
            {
                List<AppInfo> targets = new List<AppInfo> { normal, extra, moved, normal };
                using (Sqlite locked = new Sqlite(config.Database.FilePath, false))
                {
                    locked.Execute("BEGIN IMMEDIATE");
                    ClickConfirmation(delegate { Invoke(form, "RemoveTargetsFromCategory", targets, "开发工具"); }, true);
                    locked.Execute("ROLLBACK");
                }
                foreach (AppInfo app in targets)
                    Check((bool)Invoke(form, "InCategory", app, "开发工具"), "failed save changed membership: " + app.Key);
                Check(config.ExcludedCategories[normal.Key].Count == 1 && config.ExcludedCategories[normal.Key][0] == "办公软件", "rollback damaged prior exclusions");
                Check(config.CategoryOverride[moved.Key] == "开发工具" && config.CategoryOverride[legacy] == "开发工具", "rollback lost overrides");
                Check(AppConfig.Load().ToDocument().OuterXml == before, "failed save changed persisted config");
                Check(!(bool)typeof(MainForm).GetField("scanPending", Private).GetValue(form), "failed save requested scan");
                ClickConfirmation(delegate { Invoke(form, "RemoveTargetsFromCategory", targets, "开发工具"); }, true);
                foreach (AppInfo app in targets)
                    Check(!(bool)Invoke(form, "InCategory", app, "开发工具"), "retry after save failure did not remove item");
                before = AppConfig.Load().ToDocument().OuterXml;
                MenuWindow.MenuItem rejoin = (MenuWindow.MenuItem)Invoke(form, "MkAddCatMenu", normal, "开发工具");
                using (Sqlite locked = new Sqlite(config.Database.FilePath, false))
                {
                    locked.Execute("BEGIN IMMEDIATE"); rejoin.Action(); locked.Execute("ROLLBACK");
                }
                Check(!(bool)Invoke(form, "InCategory", normal, "开发工具"), "failed rejoin changed membership");
                Check(AppConfig.Load().ToDocument().OuterXml == before, "failed rejoin changed persisted config");
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
