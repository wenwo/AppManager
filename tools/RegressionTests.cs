using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
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
        private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;

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
                Run("copy restore checks the original and suppresses auto stash", RestoreCopySafety);
                Run("auto stash quiet period starts when the whole restore batch ends", RestoreSuppressionStartsAtEnd);
                Run("collection thumbnails do not stay loading when the completion post is lost", CollectionThumbsLostPost);
                Run("desktop copy mode keeps same-name files and rolls back failed save", DesktopCopySafety);
                Run("self shortcuts are identified by target instead of display name", SelfShortcutTarget);
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
                Run("auto scan: existing shortcut rewrite, periodic fallback and disposal", AutoScanChanges);
                Run("category removal: hidden toggle, single/batch, reload and rejoin", CategoryRemoval);
                Run("category removal: override, extra membership and added file", CategoryRemovalSources);
                Run("category icon-only edit keeps item order; rename migrates it", CategoryIconOnlyEdit);
                Run("deleting a resource category removes covers, view mode and the add button", CategoryDeleteCleansState);
                Run("main form disposed without Close leaves no message filter behind", DisposedFormMessageFilter);
                Run("storage dialog writes back only the edited fields", StoreDialogSavesOnlyEdits);
                Run("custom category add/remove uses the same memberships and keeps an existing icon", CustomCategoryAddRemove);
                Run("classification keeps uninstaller apps and prefers specific keywords", ClassificationRules);
                Run("move to category removes only the source membership and keeps collections", MoveKeepsOtherMemberships);
                Run("resource defaults: remove, preserve library, reload and menu rejoin", ResourceCategoryRemoval);
                Run("category removal: failed save rolls back membership and permits retry", CategoryRemovalSaveFailure);
                Run("default category addition: single/batch, multiple memberships, removal and reload", DefaultCategoryAddition);
                Run("properties: software/resource menus, grid/list and complete memberships", PropertiesMenus);
                Run("properties: file metadata, folders, missing targets and packaged apps", PropertiesMetadata);
                Run("properties: DPI scrolling, responsive open and cancelled background load", PropertiesResponsiveAndDpi);
                Run("cross-volume move: no foreign overwrite, read-only source, locked file kept", CrossVolumeMoves);
                Run("rename: managed resource file renames on disk and migrates every keyed record", RenameManagedFile);
                Run("rename: invalid names, conflicts, extension rule/confirm, case-only, locked file, save-failure rollback", RenameValidation);
                Run("rename: folders rebase covers and refuse nested entries; software/referenced items keep display-name rename", RenameFoldersAndSoftware);
                Run("move into folder keeps favorite, hidden, order, membership and cover", MoveIntoFolderKeepsRecords);
                Run("resource page creates entries in the resource store", ResourceCreateEntries);
                Run("layout listing exposes pre-restore leftovers", PrerestoreListing);
                Run("layout restore keeps stash entries created after the snapshot", RestoreKeepsNewerStash);
                Run("search results only offer removal for items in the category; record delete purges keyed config", SearchContextRemoval);
                Run("scanner name merge never lets files or document shortcuts swallow software", ScannerNameMerge);
                Run("scanner merges duplicate launch targets only when arguments also match", ScannerLaunchIdentityMerge);
                Run("membership checks recognise every added category, like the listing projection", MultiMembershipCategories);
                Run("grid scroll range follows width changes so the last rows stay reachable", GridScrollFollowsWidth);
                Run("grid measures only visible rows; column width grows while scrolling to wider rows", GridVisibleMeasurement);
                Run("list header paints before rows for empty and populated lists with bounded measurement", GridHeaderFirstPaint);
                Run("list header survives filtering to empty, refill, view switches and sorting", GridHeaderRefresh);
                Run("stash scan reads root shortcuts and legacy records only, never stashed folder trees", StashScanTopLevel);
                Run("background scan uses the UI snapshot and leaves UI-owned settings to the UI thread", ScanUsesUiSnapshot);
                Run("apps folder known-folder ids resolve to real paths; app ids stay unchanged", KnownFolderIds);
                Run("grid caches category text and falls back when the provider fails", GridCategoryTextCache);
                Run("sidebar scrolls with high-resolution wheel deltas; header strip matches grid client width", ShellWheelAndHeader);
                Run("grid delete handles every selected item; arrow starts at the first item", GridBatchDeleteAndArrow);
                Run("icon cache evicts by bytes and LRU instead of clearing everything", IconCacheLru);
                Run("auto scan pause keeps pending and paused changes, resume fires once", AutoScanPauseResume);
                Run("confirm dialog follows escape/enter and round buttons respond to space", DialogKeyboard);
                Run("scan cache drops XML-invalid control characters", ScanCacheInvalidXml);
                Run("backup list ignores update leftovers", BackupListSkipsOld);
                Run("backup create removes stale temporary snapshots", BackupCleansStaleTemp);
                Run("music player caches the scaled cover per size", MusicPlayerCoverCache);
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

        private static void AutoScanChanges()
        {
            string folder = Path.Combine(root, "auto-scan");
            Directory.CreateDirectory(folder);
            string shortcut = Path.Combine(folder, "existing.lnk");
            File.WriteAllText(shortcut, "before update");
            using (Control ui = new Control())
            {
                IntPtr handle = ui.Handle;
                using (AutoScanWatcher watcher = new AutoScanWatcher(ui))
                {
                    // 只监听隔离目录，确保真实桌面变化不会令用例误通过。
                    List<FileSystemWatcher> systemWatchers = (List<FileSystemWatcher>)
                        typeof(AutoScanWatcher).GetField("_watchers", Private).GetValue(watcher);
                    foreach (FileSystemWatcher systemWatcher in systemWatchers) systemWatcher.Dispose();
                    systemWatchers.Clear();
                    System.Windows.Forms.Timer poll = (System.Windows.Forms.Timer)
                        typeof(AutoScanWatcher).GetField("_poll", Private).GetValue(watcher);
                    poll.Stop();
                    typeof(AutoScanWatcher).GetMethod("Watch", Private).Invoke(watcher, new object[] { folder, false });
                    int notifications = 0;
                    watcher.ChangesDetected += delegate { notifications++; };
                    File.WriteAllText(shortcut, "after an installer rewrote the existing shortcut");
                    PumpUntil(delegate { return notifications > 0; }, 5000);
                    Check(notifications > 0, "rewriting an existing shortcut did not trigger a scan");
                    Check(watcher.TakeNewDesktopFiles().Count == 0, "rewritten shortcut was queued for desktop moving");

                    foreach (FileSystemWatcher systemWatcher in systemWatchers) systemWatcher.EnableRaisingEvents = false;
                    PumpUntil(delegate { return false; }, 1500);
                    int beforePoll = notifications;
                    poll.Start();
                    PumpUntil(delegate { return notifications > beforePoll; }, 20000);
                    Check(notifications > beforePoll, "periodic fallback did not scan without filesystem events");
                    Check(watcher.TakeNewDesktopFiles().Count == 0, "periodic fallback queued desktop moving");

                    watcher.Dispose();
                    int afterDispose = notifications;
                    PumpUntil(delegate { return false; }, 1500);
                    Check(notifications == afterDispose && !poll.Enabled, "disposed watcher kept scheduling scans");
                }
            }
        }

        private static void PumpUntil(Func<bool> condition, int timeoutMs)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (!condition() && elapsed.ElapsedMilliseconds < timeoutMs)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
        }

        private static object Invoke(MainForm form, string method, params object[] args)
        {
            Type[] types = Array.ConvertAll(args, delegate(object arg) { return arg.GetType(); });
            return typeof(MainForm).GetMethod(method, Private, null, types, null).Invoke(form, args);
        }

        private static void PumpUntil(Func<bool> done)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (!done())
            {
                Application.DoEvents();
                Thread.Sleep(10);
                if (timer.ElapsedMilliseconds > 10000) throw new Exception("background operation timeout");
            }
            Stopwatch drain = Stopwatch.StartNew();
            while (drain.ElapsedMilliseconds < 1000) { Application.DoEvents(); Thread.Sleep(10); }
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

        // ===== 改名 =====

        private static AppConfig RenameConfig(string scope)
        {
            string dir = Path.Combine(root, scope);
            return new AppConfig
            {
                ManageRootPath = Path.Combine(dir, "manage"),
                ResourceStorePath = Path.Combine(dir, "res"),
                FileStorePath = Path.Combine(dir, "files"),
                FolderStorePath = Path.Combine(dir, "folders")
            };
        }

        private static AppInfo AddEntry(AppConfig config, string category, string path, bool folder, bool resource, string name)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (folder) Directory.CreateDirectory(path); else File.WriteAllText(path, "data");
            config.AddedItems.Add(new AddedEntry { Path = path, Category = category, IsResource = resource, IsFolder = folder, Name = name });
            return new AppInfo { Key = "add:" + path, TargetPath = path, Name = name, Category = category, IsFile = true, IsFolder = folder, Source = "Added" };
        }

        private static MainForm ResourceForm(AppConfig config, string category, params AppInfo[] apps)
        {
            MainForm form = CategoryForm(config, apps);
            typeof(MainForm).GetField("contentMode", Private).SetValue(form, Controls.SidebarControl.ContentMode.Website);
            typeof(MainForm).GetField("currentCategory", Private).SetValue(form, category);
            return form;
        }

        private static List<AddedEntry> EntriesOf(MainForm form, AppInfo app)
        {
            return (List<AddedEntry>)Invoke(form, "EntriesOf", app);
        }

        private static string TryRename(MainForm form, AppInfo app, bool folder, string name)
        {
            return (string)Invoke(form, "TryRenameOnDisk", app, app.TargetPath, folder, name);
        }

        private static TextBox FindTextBox(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                TextBox box = child as TextBox ?? FindTextBox(child);
                if (box != null) return box;
            }
            return null;
        }

        /// <summary>在改名对话框中输入 text 并点「确定」；返回对话框是否因校验失败仍保持打开（随后由本方法关闭）。</summary>
        private static bool AnswerInput(Action action, string text, out string initial, out int start, out int length)
        {
            return AnswerInput(action, text, null, out initial, out start, out length);
        }

        /// <summary>beforeAccept：点「确定」前在对话框打开期间执行（模拟后台导入等触发的保存）。</summary>
        private static bool AnswerInput(Action action, string text, Action beforeAccept, out string initial, out int start, out int length)
        {
            string seen = null;
            int selectionStart = -1, selectionLength = -1;
            bool answered = false, stayedOpen = false;
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
                    if (answered || elapsed.ElapsedMilliseconds > 5000) { stayedOpen = answered; timer.Stop(); dialog.Close(); return; }
                    TextBox box = FindTextBox(dialog);
                    if (box == null) return;
                    seen = box.Text; selectionStart = box.SelectionStart; selectionLength = box.SelectionLength;
                    if (beforeAccept != null) beforeAccept();
                    box.Text = text;
                    answered = true;
                    foreach (Control button in dialog.Controls)
                        if (button.Text == "确定") { typeof(Control).GetMethod("OnClick", Private).Invoke(button, new object[] { EventArgs.Empty }); break; }
                };
                timer.Start();
                action();
            }
            Check(answered, "input dialog did not appear");
            initial = seen; start = selectionStart; length = selectionLength;
            return stayedOpen;
        }

        private static void RenameManagedFile()
        {
            AppConfig config = RenameConfig("rename-managed");
            string dir = Path.Combine(config.ResourceStorePath, "压缩包");
            string path = Path.Combine(dir, "base-generate-code (7).zip");
            AppInfo app = AddEntry(config, "压缩包", path, false, true, "test");   // 旧版改名只改了显示名
            string key = app.Key, collection = CollectionKeys.Make("压缩包", "资料");
            config.Collections.Add(new ResourceCollection { Parent = "压缩包", Name = "资料" });
            CollectionOps.AddMember(config, key, collection);
            config.Favorites.Add(key);
            config.Hidden.Add(key);
            config.ItemOrder["压缩包"] = new List<string> { key };
            config.ExcludedCategories[key] = new List<string> { "文档" };
            string renamed = Path.Combine(dir, "改名后.zip"), newKey = "add:" + renamed;
            using (MainForm form = ResourceForm(config, "压缩包", app))
            {
                Check((bool)Invoke(form, "RenamesOnDisk", app, app.TargetPath), "managed resource treated as display-name rename");
                object mini = typeof(MainForm).GetField("miniMusic", Private).GetValue(form);
                FieldInfo playlist = mini.GetType().GetField("paths", Private);
                playlist.SetValue(mini, new List<string> { path, Path.Combine(dir, "other.mp3") });
                string seen; int start, length;
                // 对话框打开期间发生一次无关的保存（后台导入完成等）：会清空记录缓存，改名不能再改到旧对象上
                bool stayedOpen = AnswerInput(delegate { Invoke(form, "RenameAddedEntry", app); }, "改名后.zip",
                    delegate { config.Favorites.Add("af:unrelated"); Check(config.Save(), "unrelated save failed"); },
                    out seen, out start, out length);
                Check(seen == "test.zip" && start == 0 && length == 4, "prefill/selection: " + seen + " " + start + "+" + length);
                Check(!stayedOpen, "valid rename kept the dialog open");
                Check(File.Exists(renamed) && !File.Exists(path) && File.ReadAllText(renamed) == "data", "file was not renamed on disk");
                Check(app.Key == newKey && app.TargetPath == renamed && app.Name == "改名后" && app.DisplayName() == "改名后.zip", "app not updated: " + app.DisplayName());
                Check(config.Favorites.Contains(newKey) && !config.Favorites.Contains(key), "favorite not migrated");
                Check(config.Hidden.Contains(newKey) && !config.Hidden.Contains(key), "hidden not migrated");
                Check(config.ItemOrder["压缩包"].Contains(newKey) && !config.ItemOrder["压缩包"].Contains(key), "order not migrated");
                List<string> songs = (List<string>)playlist.GetValue(mini);
                Check(songs[0] == renamed && songs[1] == Path.Combine(dir, "other.mp3"), "mini player playlist kept the old path");
            }
            AppConfig loaded = AppConfig.Load();
            List<AddedEntry> records = loaded.AddedItems.ByPath(renamed);
            Check(records.Count == 1 && records[0].Name == "改名后" && records[0].IsResource && loaded.AddedItems.ByPath(path).Count == 0,
                "persisted record (stale entry written back?): " + records.Count + " / old=" + loaded.AddedItems.ByPath(path).Count);
            Check(loaded.Favorites.Contains(newKey) && !loaded.Favorites.Contains(key), "persisted favorite");
            Check(loaded.Hidden.Contains(newKey) && !loaded.Hidden.Contains(key), "persisted hidden");
            Check(loaded.ItemOrder.ContainsKey("压缩包") && loaded.ItemOrder["压缩包"].Contains(newKey), "persisted order");
            Check(loaded.ItemCategories.ContainsKey(newKey) && loaded.ItemCategories[newKey].Contains(collection) && !loaded.ItemCategories.ContainsKey(key), "persisted collection membership");
            Check(loaded.ExcludedCategories.ContainsKey(newKey) && loaded.ExcludedCategories[newKey].Contains("文档"), "persisted exclusion");
        }

        private static void RenameValidation()
        {
            Check(MainForm.ValidateEntryName("ok name.txt") == null && MainForm.ValidateEntryName(".gitignore") == null, "valid names rejected");
            foreach (string bad in new string[] { "", "a|b", "a/b", "a:b", "CON", "nul.txt", "com1 .zip", "end.", "end ", ".", "..", new string('x', 256) })
                Check(MainForm.ValidateEntryName(bad) != null, "invalid name accepted: [" + bad + "]");
            Check(MainForm.StemLength("报告.final.docx") == "报告.final".Length && MainForm.StemLength(".gitignore") == -1, "stem selection");

            AppConfig config = RenameConfig("rename-validation");
            string dir = Path.Combine(config.ResourceStorePath, "压缩包");
            AppInfo app = AddEntry(config, "压缩包", Path.Combine(dir, "pack.zip"), false, true, "pack");
            File.WriteAllText(Path.Combine(dir, "other.zip"), "x");                            // 磁盘已有同名文件
            config.AddedItems.Add(new AddedEntry { Path = Path.Combine(dir, "taken.zip"), Category = "压缩包", IsResource = true });   // 已登记的路径
            using (MainForm form = ResourceForm(config, "压缩包", app))
            {
                string original = app.TargetPath;
                Check(TryRename(form, app, false, "a|b.zip").Contains("不能包含"), "invalid character");
                Check(TryRename(form, app, false, "CON.zip").Contains("保留名称"), "reserved name");
                Check(TryRename(form, app, false, "   ") == "名称不能为空", "blank name");
                Check(TryRename(form, app, false, "other.zip").Contains("已有名为"), "disk conflict");
                Check(TryRename(form, app, false, "taken.zip").Contains("其它条目"), "registered conflict");
                string typeError = TryRename(form, app, false, "pack.png");
                Check(typeError == MediaRules.CategoryTypeHint("压缩包") + "，不能改为「.png」", "category type rule: " + typeError);
                Check(File.Exists(original) && app.TargetPath == original, "rejected rename changed the file");

                string result = null;
                ClickConfirmation(delegate { result = TryRename(form, app, false, "pack.7z"); }, false);
                Check(result == string.Empty && File.Exists(original) && app.TargetPath == original, "cancelled extension change renamed the file");
                ClickConfirmation(delegate { result = TryRename(form, app, false, "pack.7z"); }, true);
                string sevenZip = Path.Combine(dir, "pack.7z");
                Check(result == null && File.Exists(sevenZip) && !File.Exists(original) && app.TargetPath == sevenZip, "confirmed extension change");

                // 只改大小写：同一路径，Windows 上必须仍能改名
                Check(TryRename(form, app, false, "PACK.7z") == null, "case-only rename failed");
                Check(Path.GetFileName(Directory.GetFiles(dir, "*.7z")[0]) == "PACK.7z" && app.Key == "add:" + Path.Combine(dir, "PACK.7z"), "case-only rename not applied");

                string beforeLock = app.TargetPath;
                using (new FileStream(app.TargetPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    string locked = TryRename(form, app, false, "locked.7z");
                    Check(locked != null && locked.StartsWith("无法改名："), "locked file: " + locked);
                }
                Check(File.Exists(beforeLock) && app.TargetPath == beforeLock && EntriesOf(form, app)[0].Path == beforeLock, "locked rename changed records");

                string persisted = AppConfig.Load().ToDocument().OuterXml;
                string keyBefore = app.Key;
                using (Sqlite db = new Sqlite(config.Database.FilePath, false))
                {
                    db.Execute("BEGIN IMMEDIATE");
                    string failed = TryRename(form, app, false, "saved.7z");
                    db.Execute("ROLLBACK");
                    Check(failed != null && failed.StartsWith("资源库未能保存"), "save failure not reported: " + failed);
                }
                Check(File.Exists(beforeLock) && !File.Exists(Path.Combine(dir, "saved.7z")), "save failure did not restore the file name");
                Check(app.Key == keyBefore && app.TargetPath == beforeLock && app.Name == "PACK" && EntriesOf(form, app)[0].Path == beforeLock, "save failure did not restore records");
                Check(AppConfig.Load().ToDocument().OuterXml == persisted, "failed save changed persisted config");
                Check(TryRename(form, app, false, "saved.7z") == null && File.Exists(Path.Combine(dir, "saved.7z")), "retry after save failure");
            }
            AppConfig loaded = AppConfig.Load();
            Check(loaded.AddedItems.ByPath(Path.Combine(dir, "saved.7z")).Count == 1 && loaded.AddedItems.ByPath(Path.Combine(dir, "pack.zip")).Count == 0,
                "persisted path after retries");
        }

        private static void RenameFoldersAndSoftware()
        {
            AppConfig config = RenameConfig("rename-folders");
            string photos = Path.Combine(config.ResourceStorePath, "照片");
            AppInfo album = AddEntry(config, "照片", Path.Combine(photos, "Trip"), true, true, "Trip");
            string cover = Path.Combine(album.TargetPath, "封面.png");
            File.WriteAllText(cover, "png");
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "精选", Cover = cover });
            AppInfo nested = AddEntry(config, "照片", Path.Combine(photos, "嵌套"), true, true, "嵌套");
            AddEntry(config, "照片", Path.Combine(nested.TargetPath, "inner.jpg"), false, true, "inner");

            string scope = Path.Combine(root, "rename-folders");
            AppInfo exe = AddEntry(config, "开发工具", Path.Combine(scope, "apps", "tool.exe"), false, false, "tool");
            AppInfo shortcut = AddEntry(config, "开发工具", Path.Combine(config.FileStorePath, "快捷.lnk"), false, false, "快捷");
            AppInfo storedDoc = AddEntry(config, "办公软件", Path.Combine(config.FileStorePath, "笔记.txt"), false, false, "笔记");
            AppInfo outside = AddEntry(config, "办公软件", Path.Combine(scope, "elsewhere", "外部.txt"), false, false, "外部");
            AppInfo userPhoto = AddEntry(config, "照片", Path.Combine(scope, "Pictures", "IMG_1.jpg"), false, true, "IMG_1");
            AppInfo categoryDir = AddEntry(config, "文件", photos, true, true, "照片");
            AppInfo storeRoot = AddEntry(config, "文件", config.FileStorePath, true, false, "files");
            using (MainForm form = ResourceForm(config, "照片", album, nested, exe, shortcut, storedDoc, outside, userPhoto, categoryDir, storeRoot))
            {
                Check(!(bool)Invoke(form, "RenamesOnDisk", exe, exe.TargetPath), "exe would be renamed on disk");
                Check(!(bool)Invoke(form, "RenamesOnDisk", shortcut, shortcut.TargetPath), "shortcut in store would be renamed on disk");
                Check(!(bool)Invoke(form, "RenamesOnDisk", outside, outside.TargetPath), "referenced file would be renamed on disk");
                Check((bool)Invoke(form, "RenamesOnDisk", storedDoc, storedDoc.TargetPath), "stored document not renamed on disk");
                Check((bool)Invoke(form, "RenamesOnDisk", album, album.TargetPath), "resource folder not renamed on disk");
                // 资源记录但文件在存储目录之外（已收录的原位置路径再导入资源分类）：是用户自己的文件
                Check(!(bool)Invoke(form, "RenamesOnDisk", userPhoto, userPhoto.TargetPath), "user's own file outside the store would be renamed on disk");
                // 存储结构本身：资源分类目录、文件存储根
                Check(!(bool)Invoke(form, "RenamesOnDisk", categoryDir, categoryDir.TargetPath), "resource category directory would be renamed");
                Check(!(bool)Invoke(form, "RenamesOnDisk", storeRoot, storeRoot.TargetPath), "file store root would be renamed");

                Check(Invoke(form, "RenameBlocker", nested.TargetPath, true) != null, "folder with nested entries not blocked");
                Check(Invoke(form, "RenameBlocker", album.TargetPath, true) == null, "plain folder blocked");

                string renamed = Path.Combine(photos, "Trip 2024");
                Check(TryRename(form, album, true, "Trip 2024") == null && Directory.Exists(renamed) && File.Exists(Path.Combine(renamed, "封面.png")), "folder rename");
                // 意图日志：磁盘改名前记「改名」、资源库保存后记「已保存」
                string[] intentLog = File.ReadAllLines(Paths.RenameLogFile);
                Check(intentLog.Any(l => l.Contains("改名：") && l.Contains(Path.Combine(photos, "Trip") + " => ") && l.Contains("Trip 2024"))
                    && intentLog.Any(l => l.Contains("已保存") && l.Contains("Trip 2024")), "rename intent log incomplete");
                Check(config.Collections[0].Cover == Path.Combine(renamed, "封面.png"), "cover not rebased: " + config.Collections[0].Cover);
                Check(album.Name == "Trip 2024" && album.DisplayName() == "Trip 2024", "folder display name");
                Check(TryRename(form, album, true, "TRIP 2024") == null
                    && new DirectoryInfo(photos).GetDirectories("trip 2024")[0].Name == "TRIP 2024", "case-only folder rename");

                // 软件与引用条目：只改显示名，磁盘文件保持不变
                foreach (AppInfo app in new AppInfo[] { exe, outside })
                {
                    string before = app.TargetPath, seen; int start, length;
                    AnswerInput(delegate { Invoke(form, "RenameAddedEntry", app); }, "新的显示名", out seen, out start, out length);
                    Check(File.Exists(before) && app.TargetPath == before && app.Name == "新的显示名"
                        && EntriesOf(form, app)[0].Name == "新的显示名", "display-name rename touched disk: " + before);
                }
            }
            AppConfig loaded = AppConfig.Load();
            Check(loaded.Collections.Find(delegate(ResourceCollection c) { return c.Name == "精选"; }).Cover
                == Path.Combine(photos, "TRIP 2024", "封面.png"), "persisted cover");
            Check(loaded.AddedItems.ByPath(Path.Combine(photos, "TRIP 2024")).Count == 1, "persisted folder path");
        }

        private static void MoveIntoFolderKeepsRecords()
        {
            AppConfig config = RenameConfig("move-into");
            string photos = Path.Combine(config.ResourceStorePath, "照片");
            AppInfo image = AddEntry(config, "照片", Path.Combine(photos, "a.png"), false, true, "a");
            AppInfo target = AddEntry(config, "照片", Path.Combine(photos, "收纳"), true, true, "收纳");
            string key = image.Key, collection = CollectionKeys.Make("照片", "精选");
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "精选", Cover = image.TargetPath });
            CollectionOps.AddMember(config, key, collection);
            config.Favorites.Add(key);
            config.Hidden.Add(key);
            config.ItemOrder["照片"] = new List<string> { key, target.Key };
            string dest = Path.Combine(target.TargetPath, "a.png"), newKey = "add:" + dest;
            using (MainForm form = ResourceForm(config, "照片", image, target))
            {
                IntPtr handle = form.Handle;
                Check(handle != IntPtr.Zero, "resource form handle not created");
                Invoke(form, "OnGridMoveIntoFolder", image, target);
                PumpUntil(() => File.Exists(dest) && config.Favorites.Contains(newKey));
                Check(File.Exists(dest) && !File.Exists(Path.Combine(photos, "a.png")), "file not moved");
                // 本次运行内的状态（重启前常用星标、隐藏与排序都以内存为准）
                Check(config.Favorites.Contains(newKey) && !config.Favorites.Contains(key), "favorite stale in session");
                Check(config.Hidden.Contains(newKey) && !config.Hidden.Contains(key), "hidden stale in session");
                Check(config.ItemOrder["照片"].Contains(newKey) && !config.ItemOrder["照片"].Contains(key), "order stale in session");
                Check(config.Collections[0].Cover == dest, "cover stale in session");
            }
            AppConfig loaded = AppConfig.Load();
            Check(loaded.Favorites.Contains(newKey) && !loaded.Favorites.Contains(key), "favorite lost by move");
            Check(loaded.Hidden.Contains(newKey) && !loaded.Hidden.Contains(key), "hidden lost by move");
            Check(loaded.ItemOrder["照片"].Contains(newKey) && !loaded.ItemOrder["照片"].Contains(key), "order lost by move");
            Check(loaded.ItemCategories.ContainsKey(newKey) && loaded.ItemCategories[newKey].Contains(collection), "membership lost by move");
            Check(loaded.Collections.Find(delegate(ResourceCollection c) { return c.Name == "精选"; }).Cover == dest, "cover lost by move");
        }

        private static void CrossVolumeMoves()
        {
            string tempRoot = Path.GetPathRoot(Path.GetTempPath());
            string other = null;
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady
                        && !string.Equals(drive.RootDirectory.FullName, tempRoot, StringComparison.OrdinalIgnoreCase)
                        && !FileImport.SameVolume(drive.RootDirectory.FullName, tempRoot)) { other = drive.RootDirectory.FullName; break; }
                }
                catch (IOException) { }
            }
            if (other == null) { Console.WriteLine("  (skipped: no second fixed volume)"); return; }
            string far = Path.Combine(other, "AppManagerRegression_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(far);
            try
            {
                // 目标在选名之后被他人占用：移动失败，不得删除或覆盖对方文件，源保持不变，不留临时文件
                string source = Path.Combine(root, "xv-file.txt");
                File.WriteAllText(source, "mine");
                string foreign = Path.Combine(far, "taken.txt");
                File.WriteAllText(foreign, "theirs");
                bool threw = false;
                try { DesktopOrganizer.MoveFile(source, foreign); }
                catch (IOException) { threw = true; }
                Check(threw && File.ReadAllText(foreign) == "theirs" && File.ReadAllText(source) == "mine", "existing target overwritten or deleted");
                Check(Directory.GetFiles(far).Length == 1, "staging copy left behind");

                // 只读源文件跨卷移动后源消失（资源管理器同样会移走只读文件）
                File.SetAttributes(source, FileAttributes.ReadOnly);
                Check(DesktopOrganizer.MoveFile(source, Path.Combine(far, "moved.txt")) && !File.Exists(source), "read-only source not moved");
                Check(File.ReadAllText(Path.Combine(far, "moved.txt")) == "mine", "moved file content changed");

                // 目录中被占用的文件保留在源位置，其余移走；目标是完整副本，返回 false 提示源未清空
                string dir = Path.Combine(root, "xv-dir");
                Directory.CreateDirectory(Path.Combine(dir, "sub"));
                File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
                string locked = Path.Combine(dir, "sub", "locked.txt");
                File.WriteAllText(locked, "l");
                bool removed;
                using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
                    removed = DesktopOrganizer.MoveDirectory(dir, Path.Combine(far, "dir"));
                Check(!removed && File.ReadAllText(locked) == "l" && !File.Exists(Path.Combine(dir, "a.txt")), "locked file handling wrong");
                Check(File.ReadAllText(Path.Combine(far, "dir", "a.txt")) == "a"
                    && File.ReadAllText(Path.Combine(far, "dir", "sub", "locked.txt")) == "l", "target copy incomplete");

                // 正常目录：源完全移除
                string clean = Path.Combine(root, "xv-clean");
                Directory.CreateDirectory(Path.Combine(clean, "deep"));
                File.WriteAllText(Path.Combine(clean, "deep", "b.txt"), "b");
                Check(DesktopOrganizer.MoveDirectory(clean, Path.Combine(far, "clean")) && !Directory.Exists(clean), "clean directory not fully moved");
            }
            finally
            {
                foreach (string file in Directory.GetFiles(far, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(far, true);
            }
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

        private static void MoveKeepsOtherMemberships()
        {
            AppConfig config = new AppConfig();
            config.ResourceCategories.Add("回归资源");
            AppInfo pic = new AppInfo { Key = "add:C:\\regression\\move.jpg", Name = "move", TargetPath = "C:\\regression\\move.jpg",
                Category = "照片", IsFile = true };
            config.ItemCategories[pic.Key] = new List<string> { "照片", "回归资源", CollectionKeys.Make("照片", "旅行") };
            using (MainForm form = CategoryForm(config, pic))
            {
                Invoke(form, "SetHardCategory", pic, "文档");
                List<string> extra;
                Check(config.CategoryOverride[pic.Key] == "文档" && pic.Category == "文档", "move did not set the category");
                Check(config.ItemCategories.TryGetValue(pic.Key, out extra), "move dropped every extra membership");
                Check(!extra.Contains("照片"), "item stayed in its source category");
                Check(extra.Contains("回归资源") && extra.Contains(CollectionKeys.Make("照片", "旅行")),
                    "move dropped unrelated memberships: " + string.Join(",", extra.ToArray()));
            }
        }

        private static void CategoryIconOnlyEdit()
        {
            AppConfig config = new AppConfig();
            config.CustomCategories.Add("回归排序");
            config.ItemOrder["回归排序"] = new List<string> { "af:regression-order-b", "af:regression-order-a" };
            config.CategoryViewMode["回归排序"] = false;
            AppInfo a = new AppInfo { Key = "af:regression-order-a", Name = "Order A", Category = "回归排序" };
            AppInfo b = new AppInfo { Key = "af:regression-order-b", Name = "Order B", Category = "回归排序" };
            using (MainForm form = CategoryForm(config, a, b))
            {
                CategoryIconKind current = (CategoryIconKind)Invoke(form, "GetCategoryIcon", "回归排序");
                CategoryIconKind other = current == CategoryIconKind.Star ? CategoryIconKind.Code : CategoryIconKind.Star;
                Check((bool)Invoke(form, "ApplyCategoryRename", "回归排序", "回归排序", other), "icon-only edit was not applied");
                Check((CategoryIconKind)Invoke(form, "GetCategoryIcon", "回归排序") == other, "icon was not changed");
                List<string> order;
                Check(config.ItemOrder.TryGetValue("回归排序", out order) && order.Count == 2 && order[0] == b.Key,
                    "icon-only edit dropped the category order");
                Check((bool)Invoke(form, "ApplyCategoryRename", "回归排序", "回归排序新", other), "rename was not applied");
                Check(!config.ItemOrder.ContainsKey("回归排序") && config.ItemOrder.TryGetValue("回归排序新", out order) && order[0] == b.Key,
                    "rename did not migrate the category order");
                bool viewMode;
                Check(!config.CategoryViewMode.ContainsKey("回归排序") && config.CategoryViewMode.TryGetValue("回归排序新", out viewMode) && !viewMode,
                    "rename did not migrate the category view mode");
            }
            AppConfig loaded = AppConfig.Load();
            List<string> saved;
            Check(!loaded.LoadFailed && loaded.ItemOrder.TryGetValue("回归排序新", out saved) && saved.Count == 2, "renamed order was not persisted");
            bool savedViewMode;
            Check(loaded.CategoryViewMode.TryGetValue("回归排序新", out savedViewMode) && !savedViewMode, "renamed view mode was not persisted");
        }

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static void StoreDialogSavesOnlyEdits()
        {
            MethodInfo apply = typeof(MainForm).GetMethod("ApplyStoreEdits", BindingFlags.Static | BindingFlags.NonPublic);
            AppConfig cfg = new AppConfig { FolderStorePath = "", FileStorePath = "D:\\Custom\\Files\\", ResourceStorePath = "" };
            // 打开后什么都不改：不能把默认路径写进配置，也不能重写已有值的格式
            object[] args = { cfg, "", "D:\\Custom\\Files", "", null };
            Check(!(bool)apply.Invoke(null, args) && args[4] == null, "untouched dialog reported a change");
            Check(cfg.FolderStorePath == "" && cfg.FileStorePath == "D:\\Custom\\Files\\" && cfg.ResourceStorePath == "", "untouched dialog rewrote storage paths");
            // 只改一个字段
            args = new object[] { cfg, "  ", "D:\\Custom\\Files", "E:\\Res\\", null };
            Check((bool)apply.Invoke(null, args) && cfg.ResourceStorePath == "E:\\Res", "edited field was not saved");
            Check(cfg.FolderStorePath == "" && cfg.FileStorePath == "D:\\Custom\\Files\\", "untouched fields changed");
            // 相对路径被拒绝，配置不变
            args = new object[] { cfg, "relative\\dir", "D:\\Custom\\Files", "E:\\Res", null };
            Check(!(bool)apply.Invoke(null, args) && args[4] != null && cfg.FolderStorePath == "", "relative path was accepted");
            // 清空字段 = 恢复默认
            args = new object[] { cfg, "", "", "", null };
            Check((bool)apply.Invoke(null, args) && cfg.FileStorePath == "" && cfg.ResourceStorePath == "", "clearing a field did not restore the default");
        }

        private static void DisposedFormMessageFilter()
        {
            // 显示后不经 Close 直接释放：OnFormClosed 不会执行，过滤器必须在释放时注销，
            // 否则之后任何消息循环都会访问已释放窗体的 Handle 并抛 ObjectDisposedException。
            using (MainForm form = new MainForm(new AppConfig()))
            {
                typeof(MainForm).GetField("scanRunning", Private).SetValue(form, true);
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-3000, -3000);
                form.Size = new Size(900, 600);
                form.Show();
                Application.DoEvents();
            }
            // 泄漏只在过滤器处理鼠标移动等消息时暴露：主动投递一条 WM_MOUSEMOVE，不依赖测试期间真实鼠标是否在动
            using (Form probe = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                Location = new Point(-3000, -3000), Size = new Size(200, 120) })
            {
                probe.Show();
                PostMessage(probe.Handle, 0x0200, IntPtr.Zero, IntPtr.Zero);
                Application.DoEvents();
                probe.Hide();
            }
            ClickConfirmation(delegate { ConfirmDialog.Show("回归", "释放后的消息循环"); }, true);
        }

        private static void CategoryDeleteCleansState()
        {
            AppConfig config = new AppConfig();
            config.ResourceCategories.Add("回归资源");
            string coverDir = Path.Combine(Paths.DataDir, "CollectionCovers");
            Directory.CreateDirectory(coverDir);
            string cover = Path.Combine(coverDir, "regression-cover.png");
            File.WriteAllBytes(cover, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            config.Collections.Add(new ResourceCollection { Parent = "回归资源", Name = "旅行", Cover = cover });
            config.CategoryViewMode["回归资源"] = true;
            using (MainForm form = new MainForm(config))
            {
                typeof(MainForm).GetField("scanRunning", Private).SetValue(form, true);
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-3000, -3000);
                form.Size = new Size(1000, 640);
                form.Show();
                Application.DoEvents();
                typeof(MainForm).GetField("contentMode", Private).SetValue(form, Controls.SidebarControl.ContentMode.Website);
                Invoke(form, "OnSidebarSelect", "回归资源");
                Control addButton = (Control)typeof(MainForm).GetField("addBtn", Private).GetValue(form);
                Check(addButton.Visible, "add button hidden for an editable resource category");
                ClickConfirmation(delegate { Invoke(form, "DeleteCategory", "回归资源"); }, true);
                Check(!config.ResourceCategories.Contains("回归资源"), "resource category was not removed");
                Check(config.Collections.Count == 0, "collection definition survived category deletion");
                Check(!File.Exists(cover), "managed collection cover was left behind");
                Check(!config.CategoryViewMode.ContainsKey("回归资源"), "view mode survived category deletion");
                Check(!addButton.Visible, "add button stayed visible after the current category was deleted");
                Check((string)typeof(MainForm).GetField("currentCategory", Private).GetValue(form) == "全部资源", "did not return to all resources");
            }
        }

        private static void CustomCategoryAddRemove()
        {
            AppConfig config = new AppConfig();
            config.CustomCategories.Add("我的工具");
            AppInfo app = new AppInfo { Key = "af:custom-member", Name = "Custom Member", Category = "其他" };
            using (MainForm form = CategoryForm(config, app))
            {
                string iconKey = (string)Invoke(form, "CategoryIconKey", "我的工具");
                config.CategoryIconNames[iconKey] = CategoryIconKind.Code.ToString();
                Invoke(form, "EnsureCustomCategory", "我的工具", CategoryIconKind.Star);
                Check(config.CategoryIconNames[iconKey] == CategoryIconKind.Code.ToString(), "existing custom category icon was overwritten");
                Invoke(form, "EnsureCustomCategory", "新分类", CategoryIconKind.Star);
                Check(config.CategoryIconNames[(string)Invoke(form, "CategoryIconKey", "新分类")] == CategoryIconKind.Star.ToString(),
                    "new custom category icon was not saved");
                Check((string)Invoke(form, "CategoryNameError", "全部文件", false, string.Empty) != null,
                    "software reserved name 全部文件 was accepted");
                Check((string)Invoke(form, "CategoryNameError", "全部文件", true, string.Empty) != null,
                    "resource mode accepted the software reserved name 全部文件");

                CollectionOps.AddMember(config, app.Key, "我的工具");
                CollectionOps.AddMember(config, app.Key, "照片");
                typeof(MainForm).GetField("currentCategory", Private).SetValue(form, "我的工具");
                Check((bool)Invoke(form, "InCustom", app), "custom membership was not recognised");
                ClickConfirmation(delegate { Invoke(form, "RemoveFromCustom", app); }, true);
                Check(!(bool)Invoke(form, "InCustom", app), "custom membership survived removal");
                Check(config.ItemCategories[app.Key].Contains("照片") && !config.ItemCategories[app.Key].Contains("我的工具"),
                    "scoped removal touched another category");

                CollectionOps.AddMember(config, app.Key, "我的工具");
                CollectionOps.AddMember(config, app.Key, "新分类");
                typeof(MainForm).GetField("currentCategory", Private).SetValue(form, "全部软件");
                Check((bool)Invoke(form, "InCustom", app), "aggregate view did not recognise custom memberships");
                ClickConfirmation(delegate { Invoke(form, "RemoveFromCustom", app); }, true);
                Check(!config.ItemCategories[app.Key].Contains("我的工具") && !config.ItemCategories[app.Key].Contains("新分类"),
                    "aggregate removal left a custom membership");
                Check(config.ItemCategories[app.Key].Contains("照片"), "aggregate removal touched a non-custom membership");
            }
        }

        private static void ClassificationRules()
        {
            Check(!Scanner.IsNonAppEntry("Revo Uninstaller", ""), "uninstaller application was filtered out");
            Check(Scanner.IsNonAppEntry("卸载 Revo Uninstaller", ""), "uninstall shortcut was kept");
            Check(CategoryRules.Classify("QQ") == "通讯社交", "QQ was not classified as chat");
            Check(CategoryRules.Classify("QQ音乐") == "媒体娱乐", "QQ Music was classified as chat");
            Check(CategoryRules.Classify("搜狗输入法") == "办公软件", "Sogou input method was classified as a browser");
            Check(CategoryRules.Classify("搜狗浏览器") == "浏览器", "Sogou browser was not classified as a browser");
            Check(CategoryRules.Classify("Google Chrome") == "浏览器", "Chrome regression");
            Check(CategoryRules.Classify("微信") == "通讯社交", "WeChat regression");
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

        private static string SafeDesktopTarget(string name)
        {
            string path = Path.Combine(DesktopOrganizer.UserDesktop, name);
            if (DesktopOrganizer.IsSafeRestoreTarget(path)) return path;
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), name);
            Check(DesktopOrganizer.IsSafeRestoreTarget(path), "no safe desktop fixture path");
            return path;
        }

        // 集合信息页：控件没有句柄时，加载结束通知回不到界面线程；不能因此一直“加载中”并挡住之后的加载。
        private static void CollectionThumbsLostPost()
        {
            string image = Path.Combine(root, "thumb-" + Guid.NewGuid().ToString("N") + ".png");
            using (System.Drawing.Bitmap bitmap = new System.Drawing.Bitmap(32, 24)) bitmap.Save(image, System.Drawing.Imaging.ImageFormat.Png);
            using (Controls.CollectionInfoPanel panel = new Controls.CollectionInfoPanel())
            {
                Check(!panel.IsHandleCreated, "fixture: panel already has a handle");
                panel.SetInfo("照片", "旅行", "", 1, "", .5F, .5F, null, new List<string> { image });
                Type type = typeof(Controls.CollectionInfoPanel);
                PropertyInfo loading = type.GetProperty("ThumbsLoading", Private);
                FieldInfo generation = type.GetField("thumbGen", Private);
                type.GetMethod("StartThumbLoad", Private).Invoke(panel, null);
                int first = (int)generation.GetValue(panel);
                Stopwatch wait = Stopwatch.StartNew();
                while ((bool)loading.GetValue(panel, null) && wait.ElapsedMilliseconds < 5000) Thread.Sleep(10);
                Check(!(bool)loading.GetValue(panel, null), "thumbnails stayed loading after every worker finished");
                type.GetMethod("StartThumbLoad", Private).Invoke(panel, null);
                Check((int)generation.GetValue(panel) == first + 1, "a lost completion post blocked the next thumbnail load");
            }
        }

        // 还原期间登记的自动收纳抑制项先挂起，最外层还原结束才开始计时（“还原全部”超过抑制时长也不会过期）。
        private static void RestoreSuppressionStartsAtEnd()
        {
            Type type = typeof(DesktopOrganizer);
            BindingFlags hidden = BindingFlags.Static | BindingFlags.NonPublic;
            FieldInfo depth = type.GetField("restoreDepth", hidden);
            Dictionary<string, DateTime> map = (Dictionary<string, DateTime>)type.GetField("AutoStashIgnore", hidden).GetValue(null);
            MethodInfo suppress = type.GetMethod("SuppressAutoStash", hidden), end = type.GetMethod("EndRestore", hidden);
            MethodInfo suppressed = type.GetMethod("IsAutoStashSuppressed", hidden);
            string early = Path.Combine(root, "early-restore.lnk"), late = Path.Combine(root, "late-restore.lnk");
            try
            {
                lock (DesktopOrganizer.OperationSync)
                {
                    depth.SetValue(null, 2);   // 外层 RestoreAll + 第一项 RestoreOne
                    suppress.Invoke(null, new object[] { new string[] { early } });
                    end.Invoke(null, null);
                    lock (map) Check(map[early] == DateTime.MaxValue, "early restore started its quiet period mid-batch");
                    depth.SetValue(null, 2);   // 后面的某一项
                    suppress.Invoke(null, new object[] { new string[] { late } });
                    end.Invoke(null, null);
                    DateTime finished = DateTime.UtcNow;
                    end.Invoke(null, null);    // 整批结束
                    Check((int)depth.GetValue(null) == 0, "restore depth leaked");
                    lock (map) Check(map[early] == map[late] && map[early] != DateTime.MaxValue && map[early] >= finished + DesktopOrganizer.AutoStashQuiet,
                        "quiet period did not start at the end of the batch");
                    Check((bool)suppressed.Invoke(null, new object[] { early }), "early restore was not suppressed after the batch");
                }
                suppress.Invoke(null, new object[] { new string[] { late } });   // 不在还原操作中：立即计时
                lock (map) Check(map[late] != DateTime.MaxValue && map[late] <= DateTime.UtcNow + DesktopOrganizer.AutoStashQuiet, "standalone suppression never expires");
            }
            finally { depth.SetValue(null, 0); lock (map) { map.Remove(early); map.Remove(late); } }
        }

        private static void RestoreCopySafety()
        {
            Directory.CreateDirectory(Paths.DesktopBackupDir);
            string name = "AppManagerCopyRestore_" + Guid.NewGuid().ToString("N") + ".lnk";
            string original = SafeDesktopTarget(name);
            string backup = Path.Combine(Paths.DesktopBackupDir, name);
            File.WriteAllText(backup, "payload");
            AppConfig config = new AppConfig();
            DesktopOrganizer organizer = new DesktopOrganizer(config);
            try
            {
                DesktopBackupEntry entry = new DesktopBackupEntry { CurrentPath = backup, OriginalPath = original, IsCopy = true };
                Check(organizer.RestoreOne(entry), "copy restore failed: " + organizer.LastRestoreError);
                Check(File.Exists(original) && File.ReadAllText(original) == "payload" && !File.Exists(backup),
                    "copy restore deleted the only copy");
                int failed;
                Check(DesktopOrganizer.AutoStashShortcuts(config, new List<string> { original }, out failed) == 0
                    && File.Exists(original), "restored shortcut was stashed again");
            }
            finally { try { if (File.Exists(original)) File.Delete(original); } catch { } }

            string sameName = "AppManagerCopySame_" + Guid.NewGuid().ToString("N") + ".lnk";
            string sameOriginal = SafeDesktopTarget(sameName);
            string sameBackup = Path.Combine(Paths.DesktopBackupDir, sameName);
            try
            {
                File.WriteAllText(sameOriginal, "same");
                File.WriteAllText(sameBackup, "same");
                DesktopBackupEntry entry = new DesktopBackupEntry { CurrentPath = sameBackup, OriginalPath = sameOriginal, IsCopy = true };
                Check(organizer.RestoreOne(entry), "matching copy restore failed: " + organizer.LastRestoreError);
                Check(File.Exists(sameOriginal) && !File.Exists(sameBackup), "matching copy backup was not cleaned");
            }
            finally
            {
                try { if (File.Exists(sameOriginal)) File.Delete(sameOriginal); } catch { }
                try { if (File.Exists(sameBackup)) File.Delete(sameBackup); } catch { }
            }

            string differentName = "AppManagerCopyDifferent_" + Guid.NewGuid().ToString("N") + ".lnk";
            string differentOriginal = SafeDesktopTarget(differentName);
            string differentBackup = Path.Combine(Paths.DesktopBackupDir, differentName);
            try
            {
                File.WriteAllText(differentOriginal, "original");
                File.WriteAllText(differentBackup, "backup");
                DesktopBackupEntry entry = new DesktopBackupEntry { CurrentPath = differentBackup, OriginalPath = differentOriginal, IsCopy = true };
                Check(!organizer.RestoreOne(entry) && File.Exists(differentBackup) && File.ReadAllText(differentOriginal) == "original"
                    && !string.IsNullOrEmpty(organizer.LastRestoreError), "different copy content was not preserved");
            }
            finally
            {
                try { if (File.Exists(differentOriginal)) File.Delete(differentOriginal); } catch { }
                try { if (File.Exists(differentBackup)) File.Delete(differentBackup); } catch { }
            }

            string directoryName = "AppManagerCopyDir_" + Guid.NewGuid().ToString("N");
            string directoryOriginal = SafeDesktopTarget(directoryName);
            string directoryBackup = Path.Combine(Paths.DesktopBackupDir, directoryName);
            try
            {
                Directory.CreateDirectory(Path.Combine(directoryOriginal, "empty"));
                Directory.CreateDirectory(Path.Combine(directoryBackup, "empty"));
                File.WriteAllText(Path.Combine(directoryOriginal, "same.txt"), "same");
                File.WriteAllText(Path.Combine(directoryBackup, "same.txt"), "same");
                DesktopBackupEntry entry = new DesktopBackupEntry { CurrentPath = directoryBackup, OriginalPath = directoryOriginal, IsCopy = true, IsDir = true };
                MethodInfo sameCopy = typeof(DesktopOrganizer).GetMethod("SameCopyContent", StaticPrivate);
                Check((bool)sameCopy.Invoke(null, new object[] { entry }), "matching directory content was not recognised");

                File.WriteAllText(Path.Combine(directoryOriginal, "same.txt"), "original");
                File.WriteAllText(Path.Combine(directoryBackup, "same.txt"), "backup");
                Check(!organizer.RestoreOne(entry) && Directory.Exists(directoryBackup) && Directory.Exists(directoryOriginal)
                    && File.ReadAllText(Path.Combine(directoryOriginal, "same.txt")) == "original"
                    && !string.IsNullOrEmpty(organizer.LastRestoreError), "different directory content was not preserved");
            }
            finally
            {
                try { if (Directory.Exists(directoryOriginal)) Directory.Delete(directoryOriginal, true); } catch { }
                try { if (Directory.Exists(directoryBackup)) Directory.Delete(directoryBackup, true); } catch { }
            }

            string unsafeBackup = Path.Combine(Paths.DesktopBackupDir, "unsafe-" + Guid.NewGuid().ToString("N") + ".txt");
            string unsafeOriginal = Path.Combine(root, "outside-copy-original.txt");
            File.WriteAllText(unsafeBackup, "keep");
            try
            {
                DesktopBackupEntry entry = new DesktopBackupEntry { CurrentPath = unsafeBackup, OriginalPath = unsafeOriginal, IsCopy = true };
                Check(!organizer.RestoreOne(entry) && File.Exists(unsafeBackup), "unsafe copy restore deleted the backup");
            }
            finally { try { if (File.Exists(unsafeBackup)) File.Delete(unsafeBackup); } catch { } }
        }

        private static void DesktopCopySafety()
        {
            AppConfig config = RenameConfig("desktop-copy");
            config.OrganizeCopy = true;
            Check(config.Save(), config.DatabaseError);
            DesktopOrganizer organizer = new DesktopOrganizer(config);
            MethodInfo backup = typeof(DesktopOrganizer).GetMethod("BackupItem", Private);
            string sourceDir = Path.Combine(root, "desktop-copy", "source"), destDir = Path.Combine(root, "desktop-copy", "backup");
            Directory.CreateDirectory(sourceDir); Directory.CreateDirectory(destDir);
            string source = Path.Combine(sourceDir, "same.txt");
            File.WriteAllText(source, "new");
            File.WriteAllText(Path.Combine(destDir, "same.txt"), "old");
            int moved = (int)backup.Invoke(organizer, new object[] { source, destDir, "same.txt", false, false });
            Check(moved == 1 && File.Exists(source) && File.ReadAllText(source) == "new", "copy mode removed the source");
            Check(File.ReadAllText(Path.Combine(destDir, "same.txt")) == "old", "copy mode overwrote an existing file");
            Check(config.DesktopBackup.Count == 1 && config.DesktopBackup[0].IsCopy
                && string.Equals(config.DesktopBackup[0].CurrentPath, Path.Combine(destDir, "same_1.txt"), StringComparison.OrdinalIgnoreCase),
                "same-name copy was not preserved under a unique name");

            string source2 = Path.Combine(sourceDir, "rollback.txt");
            File.WriteAllText(source2, "rollback");
            using (Sqlite db = new Sqlite(config.Database.FilePath, false))
                db.Execute("CREATE TRIGGER fail_desktop_backup BEFORE INSERT ON records WHEN NEW.section='DesktopBackup' BEGIN SELECT RAISE(ABORT,'test failure'); END");
            try
            {
                int failed = (int)backup.Invoke(organizer, new object[] { source2, destDir, "rollback.txt", false, false });
                Check(failed == 0, "save failure was reported as success");
            }
            finally
            {
                using (Sqlite db = new Sqlite(config.Database.FilePath, false)) db.Execute("DROP TRIGGER IF EXISTS fail_desktop_backup");
            }
            Check(File.Exists(source2) && !File.Exists(Path.Combine(destDir, "rollback.txt")), "failed save left an orphan copy");
        }

        private static void SelfShortcutTarget()
        {
            MethodInfo create = typeof(DesktopOrganizer).GetMethod("CreateShortcut", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo skippable = typeof(DesktopOrganizer).GetMethod("IsSkippable", BindingFlags.Static | BindingFlags.NonPublic);
            string dir = Path.Combine(root, "self-shortcut");
            Directory.CreateDirectory(dir);
            string renamed = Path.Combine(dir, "addManager.lnk");
            string misleading = Path.Combine(dir, "应用中心.lnk");
            string broken = Path.Combine(dir, "broken.lnk");
            string self = Process.GetCurrentProcess().MainModule.FileName;
            string other = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
            create.Invoke(null, new object[] { renamed, self.ToUpperInvariant(), "regression" });
            create.Invoke(null, new object[] { misleading, other, "regression" });
            File.WriteAllText(broken, "not a shortcut");
            Check(File.Exists(renamed) && File.Exists(misleading), "shortcut fixtures were not created");
            Check(DesktopOrganizer.IsSelfShortcut(renamed), "renamed shortcut to the current executable was not protected");
            Check((bool)skippable.Invoke(null, new object[] { Path.GetFileName(renamed), renamed }), "full organize did not protect the renamed self shortcut");
            Check(!DesktopOrganizer.IsSelfShortcut(misleading), "same-name shortcut to another executable was protected");
            Check(!(bool)skippable.Invoke(null, new object[] { Path.GetFileName(misleading), misleading }), "full organize protected a same-name shortcut to another executable");
            Check(!DesktopOrganizer.IsSelfShortcut(broken), "broken shortcut was treated as self");
        }

        private static void ResourceCreateEntries()
        {
            AppConfig config = RenameConfig("resource-create");
            config.ResourceCategories.Add("资料");
            using (MainForm form = ResourceForm(config, "资料"))
            {
                IntPtr handle = form.Handle;
                Check(handle != IntPtr.Zero, "resource form handle not created");
                Invoke(form, "CreateManagedEntry", false);
                Invoke(form, "CreateManagedEntry", true);
            }
            string dir = Path.Combine(config.ResourceStorePath, "资料");
            List<AddedEntry> created = config.AddedItems.Where(e => string.Equals(Path.GetDirectoryName(e.Path), dir, StringComparison.OrdinalIgnoreCase)).ToList();
            Check(created.Count == 2 && created.All(e => e.IsResource), "resource page created entries outside the resource store");
            Check(created.Any(e => !e.IsFolder) && created.Any(e => e.IsFolder), "resource page did not create both file and folder");
        }

        private static void PrerestoreListing()
        {
            string id = "_prerestore_" + Guid.NewGuid().ToString("N");
            string dir = Path.Combine(Paths.BackupsDir, id);
            Directory.CreateDirectory(dir);
            try
            {
                new AppConfig().SaveTo(Path.Combine(dir, "config.xml"));
                File.WriteAllText(Path.Combine(dir, "incomplete"), "");
                LayoutBackupInfo info = LayoutBackup.List().FirstOrDefault(i => i.Id == id);
                Check(info != null && info.IsPreRestore && info.Incomplete, "pre-restore leftover not exposed as incomplete");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
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

        private static void ShellWheelAndHeader()
        {
            using (Form host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000), Size = new Size(600, 400) })
            using (Controls.SidebarControl sidebar = new Controls.SidebarControl { Location = Point.Empty, Size = new Size(220, 200) })
            {
                host.Controls.Add(sidebar);
                host.Show();
                List<Controls.SidebarControl.SidebarItem> items = new List<Controls.SidebarControl.SidebarItem>();
                // 只有中间可排序区块会滚动
                for (int i = 0; i < 40; i++) items.Add(new Controls.SidebarControl.SidebarItem { Text = "分类 " + i, Key = "c" + i, Orderable = true });
                sidebar.SetItems(items);
                FieldInfo offset = typeof(Controls.SidebarControl).GetField("scrollOffset", Private);
                // 精确式触控板：每次只上报很小的增量，累计后也必须能滚动
                for (int i = 0; i < 12; i++) sidebar.ScrollByWheel(-10);
                Check((int)offset.GetValue(sidebar) > 0, "small wheel deltas never scrolled the sidebar");
                host.Hide();
            }
            AppConfig config = new AppConfig();
            List<AppInfo> apps = new List<AppInfo>();
            for (int i = 0; i < 120; i++) apps.Add(new AppInfo { Key = "af:header-" + i, Name = "Header " + i, Category = "开发工具" });
            if (config.Database == null) Check(config.Save(), "database fixture save failed");
            config.Database.PublishCatalog(config, new List<AppInfo>(apps));
            using (MainForm form = new MainForm(config))
            {
                // 扫描标志先置位，避免 OnLoad 启动真实扫描；窗口必须真正显示，滚动条才会创建。
                typeof(MainForm).GetField("scanRunning", Private).SetValue(form, true);
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-3000, -3000);
                form.Size = new Size(1200, 520);
                form.Show();
                Application.DoEvents();
                typeof(MainForm).GetField("currentCategory", Private).SetValue(form, "开发工具");
                typeof(MainForm).GetField("allApps", Private).SetValue(form, new List<AppInfo>(apps));
                Invoke(form, "RefreshGrid");
                Application.DoEvents();
                Controls.AppGridControl grid = (Controls.AppGridControl)typeof(MainForm).GetField("grid", Private).GetValue(form);
                Control strip = (Control)typeof(MainForm).GetField("headerStrip", Private).GetValue(form);
                Check(grid.ClientSize.Width < grid.Width, "fixture did not show a vertical scrollbar");
                Check(strip.Width == grid.ClientSize.Width, "header strip width " + strip.Width + " differs from grid client width " + grid.ClientSize.Width);
            }
        }

        private static void GridBatchDeleteAndArrow()
        {
            using (Controls.AppGridControl grid = new Controls.AppGridControl { Size = new Size(640, 420) })
            {
                grid.GridMode = true;
                List<AppInfo> apps = new List<AppInfo>();
                for (int i = 0; i < 6; i++) apps.Add(new AppInfo { Key = "af:batch-" + i, Name = "Batch " + i, Category = "开发工具" });
                grid.SetApps(apps);
                MethodInfo selectSingle = typeof(Controls.AppGridControl).GetMethod("SelectSingle", Private);
                MethodInfo toggleSelected = typeof(Controls.AppGridControl).GetMethod("ToggleSelected", Private);
                MethodInfo processKey = typeof(Controls.AppGridControl).GetMethod("ProcessCmdKey", Private);
                selectSingle.Invoke(grid, new object[] { 0 });
                toggleSelected.Invoke(grid, new object[] { 1 });
                List<AppInfo> deleted = null;
                grid.DeleteSelectionRequested += delegate(List<AppInfo> list) { deleted = list; };
                object[] args = new object[] { Message.Create(IntPtr.Zero, 0x0100, (IntPtr)(int)Keys.Delete, IntPtr.Zero), Keys.Delete };
                Check((bool)processKey.Invoke(grid, args), "delete key was not consumed");
                Check(deleted != null && deleted.Count == 2, "delete did not receive every selected item");
                Check(deleted[0].Key == "af:batch-0" && deleted[1].Key == "af:batch-1", "delete received the wrong items");

                grid.ClearSelection();
                args = new object[] { Message.Create(IntPtr.Zero, 0x0100, (IntPtr)(int)Keys.Down, IntPtr.Zero), Keys.Down };
                Check((bool)processKey.Invoke(grid, args), "down key was not consumed");
                List<int> selected = (List<int>)typeof(Controls.AppGridControl).GetField("selected", Private).GetValue(grid);
                Check(selected.Count == 1 && selected[0] == 0, "down key without a selection did not start at item 0");
            }
        }

        private static void IconCacheLru()
        {
            Type type = typeof(Icons);
            IDictionary cache = (IDictionary)type.GetField("Cache", StaticPrivate).GetValue(null);
            IDictionary covers = (IDictionary)type.GetField("CoverCacheKeys", StaticPrivate).GetValue(null);
            MethodInfo store = type.GetMethod("CacheStore", StaticPrivate);
            MethodInfo take = type.GetMethod("CacheTakeClone", StaticPrivate);
            MethodInfo evict = type.GetMethod("EvictOldestCacheEntry", StaticPrivate);
            Action clear = delegate
            {
                while (cache.Count > 0) evict.Invoke(null, null);
                covers.Clear();
            };
            clear();
            try
            {
                covers["base-0"] = "icon-0";
                for (int i = 0; i <= 2000; i++)
                {
                    using (Bitmap source = new Bitmap(1, 1))
                    using (Bitmap copy = (Bitmap)store.Invoke(null, new object[] { "icon-" + i, source })) { }
                }
                Check(cache.Count == 2000, "entry cap was not enforced");
                Check(!cache.Contains("icon-0"), "oldest entry survived the entry cap");
                Check(cache.Contains("icon-2000"), "newest entry was dropped by the entry cap");
                Check(!covers.Contains("base-0"), "cover mapping survived eviction");

                clear();
                for (int i = 0; i < 64; i++)
                {
                    using (Bitmap source = new Bitmap(512, 512))
                    using (Bitmap copy = (Bitmap)store.Invoke(null, new object[] { "byte-" + i, source })) { }
                }
                using (Bitmap touched = (Bitmap)take.Invoke(null, new object[] { "byte-0" })) { }
                using (Bitmap source = new Bitmap(512, 512))
                using (Bitmap copy = (Bitmap)store.Invoke(null, new object[] { "byte-64", source })) { }
                Check(!cache.Contains("byte-1"), "byte cap did not evict the least recently used entry");
                Check(cache.Contains("byte-0"), "recently used entry was evicted first");
                Check(cache.Contains("byte-64"), "new entry was not cached");
                Check(cache.Count == 64, "byte cap left the wrong number of entries");
            }
            finally
            {
                clear();
            }
        }

        private static void AutoScanPauseResume()
        {
            using (Form host = new Form())
            {
                host.ShowInTaskbar = false;
                host.StartPosition = FormStartPosition.Manual;
                host.Location = new Point(-3000, -3000);
                host.Size = new Size(240, 160);
                host.Show();
                Application.DoEvents();
                using (AutoScanWatcher watcher = new AutoScanWatcher(host))
                {
                    System.Windows.Forms.Timer timer = (System.Windows.Forms.Timer)typeof(AutoScanWatcher).GetField("_debounce", Private).GetValue(watcher);
                    timer.Interval = 40;
                    MethodInfo enqueue = typeof(AutoScanWatcher).GetMethod("Enqueue", Private);
                    int fired = 0;
                    watcher.ChangesDetected += delegate { fired++; };

                    watcher.Pause();
                    enqueue.Invoke(watcher, new object[] { string.Empty, false });
                    Thread.Sleep(150);
                    Application.DoEvents();
                    Check(fired == 0, "paused watcher fired a scan");
                    watcher.Resume();
                    PumpUntil(delegate { return fired >= 1; });
                    Check(fired == 1, "resume did not fire the paused change exactly once");

                    enqueue.Invoke(watcher, new object[] { string.Empty, false });
                    Check(timer.Enabled, "fixture did not leave a pending debounce timer");
                    watcher.Pause();
                    Thread.Sleep(150);
                    Application.DoEvents();
                    Check(fired == 1, "pause did not stop the pending timer");
                    watcher.Resume();
                    PumpUntil(delegate { return fired >= 2; });
                    Check(fired == 2, "pending change was lost across pause/resume");

                    watcher.Pause();
                    watcher.Resume();
                    Thread.Sleep(150);
                    Application.DoEvents();
                    Check(fired == 2, "idle pause/resume fired an extra scan");
                }
            }
        }

        private static void DialogKeyboard()
        {
            bool escapeResult = true, enterResult = false;
            PressModalKey(Keys.Escape, delegate { escapeResult = ConfirmDialog.Show("回归", "按 Esc"); });
            PressModalKey(Keys.Enter, delegate { enterResult = ConfirmDialog.Show("回归", "按 Enter"); });
            Check(!escapeResult, "escape did not cancel the confirmation dialog");
            Check(enterResult, "enter did not accept the confirmation dialog");
            // 文本保存失败时的对话框：右侧按钮写明“放弃修改”，Esc 与点它等价。
            bool retryResult = true; string[] labels = null;
            using (System.Windows.Forms.Timer probe = new System.Windows.Forms.Timer { Interval = 10 })
            {
                probe.Tick += delegate
                {
                    Form dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(open => open.Modal);
                    if (dialog == null) return;
                    probe.Stop();
                    dialog.Opacity = 0;
                    labels = dialog.Controls.OfType<Controls.RoundButton>().Select(b => b.Text).ToArray();
                    typeof(Control).GetMethod("OnKeyDown", Private).Invoke(dialog, new object[] { new KeyEventArgs(Keys.Escape) });
                };
                probe.Start();
                retryResult = ConfirmDialog.Show("修改没有保存", "回归", "重试", "放弃修改");
            }
            Check(labels != null && labels[0] == "重试" && labels[1] == "放弃修改", "custom confirmation labels not shown: "
                + (labels == null ? "(none)" : string.Join("|", labels)));
            Check(!retryResult, "escape on the save-failure dialog did not choose discard");

            bool clicked = false;
            using (Controls.RoundButton button = new Controls.RoundButton { Text = "确定" })
            {
                button.Click += delegate { clicked = true; };
                MethodInfo down = typeof(Control).GetMethod("OnKeyDown", Private);
                MethodInfo up = typeof(Control).GetMethod("OnKeyUp", Private);
                down.Invoke(button, new object[] { new KeyEventArgs(Keys.Space) });
                Check(!clicked, "space clicked before key up");
                up.Invoke(button, new object[] { new KeyEventArgs(Keys.Space) });
                Check(clicked, "space did not click the round button");
            }
        }

        private static void PressModalKey(Keys key, Action show)
        {
            bool pressed = false;
            Stopwatch elapsed = Stopwatch.StartNew();
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 10 })
            {
                timer.Tick += delegate
                {
                    Form dialog = null;
                    foreach (Form form in Application.OpenForms) if (form.Modal) { dialog = form; break; }
                    if (dialog == null) return;
                    if (elapsed.ElapsedMilliseconds > 3000)
                    {
                        timer.Stop();
                        dialog.Close();
                        return;
                    }
                    timer.Stop();
                    dialog.Opacity = 0;
                    pressed = true;
                    typeof(Control).GetMethod("OnKeyDown", Private).Invoke(dialog,
                        new object[] { new KeyEventArgs(key) });
                };
                timer.Start();
                show();
            }
            Check(pressed, "modal dialog did not receive " + key);
        }

        private static void ScanCacheInvalidXml()
        {
            string path = Path.Combine(Paths.DataDir, "scan.cache.xml");
            try
            {
                if (File.Exists(path)) File.Delete(path);
                ScanCache.Save(new List<AppInfo>
                {
                    new AppInfo { Name = "Bad" + (char)1 + "Name", TargetPath = "C:\\x" + (char)2 + "y", Key = "bad" + (char)3 + "key" }
                });
                List<AppInfo> loaded = ScanCache.Load();
                Check(loaded.Count == 1, "scan cache was not written");
                Check(loaded[0].Name == "Bad Name", "invalid name character was not sanitized");
                Check(loaded[0].TargetPath.IndexOf((char)2) < 0, "invalid path character survived");
                Check(loaded[0].Key.IndexOf((char)3) < 0, "invalid key character survived");
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }

        private static void BackupListSkipsOld()
        {
            string normal = Path.Combine(Paths.BackupsDir, "20260101-000000");
            string old = normal + ".old";
            try
            {
                foreach (string dir in new string[] { normal, old })
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "config.xml"), "<config/>");
                    File.WriteAllText(Path.Combine(dir, "meta.xml"),
                        "<backup name='回归' created='2026-01-01 00:00:00' stash='0' custom='0' categories='0'/>");
                }
                List<LayoutBackupInfo> list = LayoutBackup.List();
                Check(list.Exists(delegate(LayoutBackupInfo i) { return i.Id == "20260101-000000"; }),
                    "normal backup missing from the list");
                Check(!list.Exists(delegate(LayoutBackupInfo i) { return i.Id.EndsWith(".old"); }),
                    "update leftover was listed as a backup");
            }
            finally
            {
                try { if (Directory.Exists(old)) Directory.Delete(old, true); } catch { }
                try { if (Directory.Exists(normal)) Directory.Delete(normal, true); } catch { }
            }
        }

        private static void BackupCleansStaleTemp()
        {
            string stale = Path.Combine(Paths.BackupsDir, "stale.tmp");
            string id = null;
            string error = null;
            try
            {
                Directory.CreateDirectory(stale);
                File.WriteAllText(Path.Combine(stale, "partial.xml"), "<partial/>");
                Check(LayoutBackup.Create("回归临时清理", out id, out error), "backup create failed: " + error);
                Check(!Directory.Exists(stale), "stale temporary snapshot was not removed");
                Check(Directory.Exists(Path.Combine(Paths.BackupsDir, id)), "new backup missing");
            }
            finally
            {
                try { if (Directory.Exists(stale)) Directory.Delete(stale, true); } catch { }
                try { if (!string.IsNullOrEmpty(id) && Directory.Exists(Path.Combine(Paths.BackupsDir, id))) Directory.Delete(Path.Combine(Paths.BackupsDir, id), true); } catch { }
            }
        }

        private static void MusicPlayerCoverCache()
        {
            using (Controls.MusicPlayerControl player = new Controls.MusicPlayerControl())
            {
                MusicTrackInfo track = new MusicTrackInfo { Title = "封面", Artist = "测试", Album = "测试", Cover = new Bitmap(100, 100) };
                typeof(Controls.MusicPlayerControl).GetField("track", Private).SetValue(player, track);
                typeof(Controls.MusicPlayerControl).GetField("coverRect", Private).SetValue(player, new Rectangle(0, 0, 50, 50));
                MethodInfo ensure = typeof(Controls.MusicPlayerControl).GetMethod("EnsureScaledCover", Private);
                FieldInfo scaled = typeof(Controls.MusicPlayerControl).GetField("scaledCover", Private);
                ensure.Invoke(player, null);
                Bitmap first = (Bitmap)scaled.GetValue(player);
                Check(first != null && first.Width == 50 && first.Height == 50, "cover was not scaled to the target size");
                ensure.Invoke(player, null);
                Check(ReferenceEquals(first, scaled.GetValue(player)), "cover was rescaled on every paint");
                typeof(Controls.MusicPlayerControl).GetField("coverRect", Private).SetValue(player, new Rectangle(0, 0, 20, 20));
                ensure.Invoke(player, null);
                Bitmap second = (Bitmap)scaled.GetValue(player);
                Check(second != null && second.Width == 20 && !ReferenceEquals(first, second),
                    "cover cache did not follow a size change");
            }
        }

        private static void GridCategoryTextCache()
        {
            MethodInfo categoryList = typeof(Controls.AppGridControl).GetMethod("CategoryList", Private);
            int calls = 0;
            using (Controls.AppGridControl grid = new Controls.AppGridControl())
            {
                grid.CategoryTextProvider = delegate(AppInfo a)
                {
                    calls++;
                    throw new InvalidOperationException("database unavailable");
                };
                List<AppInfo> apps = new List<AppInfo> { new AppInfo { Key = "af:cache", Name = "Cache", Category = "原分类" } };
                grid.SetApps(apps);
                List<string> first = (List<string>)categoryList.Invoke(grid, new object[] { apps[0] });
                List<string> second = (List<string>)categoryList.Invoke(grid, new object[] { apps[0] });
                Check(first.Count == 1 && first[0] == "原分类", "provider failure did not fall back to the item category");
                Check(second.Count == 1 && second[0] == "原分类", "cached fallback text changed");
                Check(calls == 1, "category provider ran on every paint");
            }
            calls = 0;
            using (Controls.AppGridControl grid = new Controls.AppGridControl())
            {
                grid.CategoryTextProvider = delegate(AppInfo a) { calls++; return "甲, 乙"; };
                List<AppInfo> apps = new List<AppInfo> { new AppInfo { Key = "af:cache2", Name = "Cache 2", Category = "原分类" } };
                grid.SetApps(apps);
                List<string> first = (List<string>)categoryList.Invoke(grid, new object[] { apps[0] });
                List<string> second = (List<string>)categoryList.Invoke(grid, new object[] { apps[0] });
                Check(first.Count == 2 && first[0] == "甲" && first[1] == "乙", "provider text was not split");
                Check(second.Count == 2, "cached provider text was lost");
                Check(calls == 1, "category provider ran again after SetApps");
            }
        }

        private static void KnownFolderIds()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            // FOLDERID_Windows / FOLDERID_System
            Check(string.Equals(Scanner.ResolveKnownFolderPath("{F38BF404-1D43-42F2-9305-67DE0B28FC23}\\notepad.exe"),
                Path.Combine(windows, "notepad.exe"), StringComparison.OrdinalIgnoreCase), "Windows known folder not resolved");
            Check(string.Equals(Scanner.ResolveKnownFolderPath("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\cmd.exe"),
                Path.Combine(Environment.SystemDirectory, "cmd.exe"), StringComparison.OrdinalIgnoreCase), "System known folder not resolved");
            // 解析后不存在的路径、普通 UWP 应用 ID、非法 GUID 都保持原样
            string missing = "{F38BF404-1D43-42F2-9305-67DE0B28FC23}\\no-such-app-regression.exe";
            Check(Scanner.ResolveKnownFolderPath(missing) == missing, "missing resolved path replaced the id");
            Check(Scanner.ResolveKnownFolderPath("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App") == "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "app id changed");
            string bad = "{ZZZZZZZZ-1D43-42F2-9305-67DE0B28FC23}\\x.exe";
            Check(Scanner.ResolveKnownFolderPath(bad) == bad && Scanner.ResolveKnownFolderPath(null) == null, "invalid id handling");
        }

        // 后台扫描只用界面线程拷贝的收录快照；收藏/隐藏/分类覆盖不在后台读，回到界面线程再 ApplyConfig。
        private static void ScanUsesUiSnapshot()
        {
            string live = Path.Combine(root, "scan-live.txt"), snap = Path.Combine(root, "scan-snapshot.txt");
            File.WriteAllText(live, "live"); File.WriteAllText(snap, "snapshot");
            AppConfig config = new AppConfig();
            config.AddedItems.Add(new AddedEntry { Path = live, Category = "工具" });
            config.Favorites.Add("add:" + snap); config.CategoryOverride["add:" + snap] = "办公软件";
            Scanner scanner = new Scanner(config);
            typeof(Scanner).GetField("nameIndex", Private).SetValue(scanner, new Dictionary<string, AppInfo>(StringComparer.Ordinal));
            typeof(Scanner).GetField("addedSnapshot", Private).SetValue(scanner, new List<AddedEntry> { new AddedEntry { Path = snap, Category = "工具" } });
            Dictionary<string, AppInfo> dict = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
            typeof(Scanner).GetMethod("ScanAdded", Private).Invoke(scanner, new object[] { dict });
            Check(dict.Count == 1 && string.Equals(dict.Values.First().TargetPath, snap, StringComparison.OrdinalIgnoreCase),
                "background scan enumerated the live added list instead of the UI snapshot");
            AppInfo found = dict.Values.First();
            Check(!found.IsFavorite && found.Category != "办公软件", "scan applied UI-owned settings on the worker");
            scanner.ApplyConfig(new List<AppInfo> { found });
            Check(found.IsFavorite && found.Category == "办公软件", "settings not applied on the UI thread");
        }

        private static void StashScanTopLevel()
        {
            MethodInfo create = typeof(DesktopOrganizer).GetMethod("CreateShortcut", BindingFlags.Static | BindingFlags.NonPublic);
            string target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
            string rootLink = Path.Combine(Paths.DesktopBackupDir, "Stash Root Regression.lnk");
            string nested = Path.Combine(Paths.DesktopBackupDir, "Folders", "Project", "sub");
            string nestedLink = Path.Combine(nested, "Stash Nested Regression.lnk");
            string legacyDir = Path.Combine(Paths.LegacyStashDir, "old", "group");
            string legacyLink = Path.Combine(legacyDir, "Stash Legacy Regression.lnk");
            string legacyOrphan = Path.Combine(legacyDir, "Stash Legacy Orphan.lnk");
            Directory.CreateDirectory(nested);
            Directory.CreateDirectory(legacyDir);
            foreach (string link in new string[] { rootLink, nestedLink, legacyLink, legacyOrphan })
                create.Invoke(null, new object[] { link, target, "regression" });
            try
            {
                AppConfig config = new AppConfig();
                config.DesktopBackup.Add(new DesktopBackupEntry { OriginalPath = "C:\\Desk\\legacy.lnk", CurrentPath = legacyLink, IsShortcut = true });
                Scanner scanner = new Scanner(config);
                typeof(Scanner).GetField("nameIndex", Private).SetValue(scanner, new Dictionary<string, AppInfo>(StringComparer.Ordinal));
                Dictionary<string, AppInfo> dict = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
                typeof(Scanner).GetMethod("ScanDesktopBackup", Private).Invoke(scanner, new object[] { dict });
                Func<string, bool> found = delegate(string name) { return dict.Values.Any(a => a.Name == name); };
                Check(found("Stash Root Regression"), "root stash shortcut missing");
                Check(found("Stash Legacy Regression"), "legacy recorded shortcut missing");
                Check(!found("Stash Nested Regression"), "shortcut inside a stashed folder became a quick-center item");
                Check(!found("Stash Legacy Orphan"), "legacy stash was scanned recursively");
            }
            finally
            {
                foreach (string link in new string[] { rootLink, nestedLink, legacyLink, legacyOrphan })
                    try { File.Delete(link); } catch { }
            }
        }

        private static void GridScrollFollowsWidth()
        {
            using (Form host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000), Size = new Size(1400, 700) })
            using (Controls.AppGridControl grid = new Controls.AppGridControl { Location = Point.Empty, Size = new Size(1300, 600) })
            {
                host.Controls.Add(grid);
                host.Show();
                grid.GridMode = true;
                List<AppInfo> apps = new List<AppInfo>();
                for (int i = 0; i < 60; i++) apps.Add(new AppInfo { Key = "af:grid-" + i, Name = "Grid " + i });
                grid.SetApps(apps);
                MethodInfo rows = typeof(Controls.AppGridControl).GetMethod("GridRows", Private);
                int wideRows = (int)rows.Invoke(grid, null), wideHeight = grid.AutoScrollMinSize.Height;
                grid.Size = new Size(420, 600);   // 窗口变窄：每行放得下的卡片变少，总行数增加
                int narrowRows = (int)rows.Invoke(grid, null), narrowHeight = grid.AutoScrollMinSize.Height;
                Check(narrowRows > wideRows, "fixture did not change the column count");
                // 高度 = 行数 × 行高 + 固定边距：两种宽度下的高度差必须正好是行数差的整数倍
                Check(narrowHeight > wideHeight && (narrowHeight - wideHeight) % (narrowRows - wideRows) == 0,
                    "scroll range kept the wide layout: wide=" + wideHeight + "/" + wideRows + " narrow=" + narrowHeight + "/" + narrowRows);
                grid.Size = new Size(1300, 600);
                Check(grid.AutoScrollMinSize.Height == wideHeight, "scroll range did not shrink back after widening");
                host.Hide();
            }
        }

        private static void GridHeaderFirstPaint()
        {
            foreach (int count in new int[] { 0, 1, 49, 50, 51, 10000 })
            using (Controls.AppGridControl grid = new Controls.AppGridControl { Size = new Size(960, 420), GridMode = false, ShowTimeColumn = true, ShowRowActions = true })
            {
                List<AppInfo> apps = new List<AppInfo>();
                for (int index = 0; index < count; index++)
                    apps.Add(new AppInfo { Key = "header:" + index, Name = "Header " + index, Category = "照片", TargetPath = Path.Combine(root, "header-" + index + ".jpg") });
                grid.SetApps(apps);
                CheckGridHeaderPaint(grid);
                int measured = (int)typeof(Controls.AppGridControl).GetField("measuredHi", Private).GetValue(grid);
                Check(count == 0 ? measured == -1 : measured >= 0 && measured < Math.Min(count, 50),
                    "header measured outside the first 50 rows: count=" + count + " measured=" + measured);
            }
        }

        private static void GridHeaderRefresh()
        {
            using (Controls.AppGridControl grid = new Controls.AppGridControl { Size = new Size(960, 420), GridMode = false, ShowTimeColumn = true, ShowRowActions = true })
            using (Bitmap bitmap = new Bitmap(grid.Width, grid.Height))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                List<AppInfo> apps = new List<AppInfo> { new AppInfo { Key = "header:photo", Name = "Photo", Category = "照片" } };
                string sorted = null;
                grid.SortRequested += delegate(string column) { sorted = column; };
                foreach (int count in new int[] { 1, 0, 1, 0 })
                {
                    grid.SetApps(count == 0 ? new List<AppInfo>() : apps);
                    int dpi = DpiHelper.ForControl(grid);
                    grid.HandleHeaderClick(graphics, new Point(DpiHelper.Scale(60, dpi), DpiHelper.Scale(18, dpi)));
                    Check(sorted == "name", "header sorting failed after filtering to " + count + " rows");
                    sorted = null;
                    grid.SetSortIndicator("name", true);
                    CheckGridHeaderPaint(grid);
                    grid.DrawToBitmap(bitmap, new Rectangle(Point.Empty, grid.Size));
                    grid.GridMode = true;
                    grid.GridMode = false;
                    CheckGridHeaderPaint(grid);
                }
            }
        }

        private static void CheckGridHeaderPaint(Controls.AppGridControl grid)
        {
            int height = DpiHelper.Scale(Controls.AppGridControl.ListHeaderH, DpiHelper.ForControl(grid));
            using (Bitmap bitmap = new Bitmap(grid.Width, height))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                grid.PaintListHeader(graphics, grid.Width);
                grid.PaintListHeader(graphics, grid.Width);
                int textPixels = 0;
                for (int pixelY = 0; pixelY < height; pixelY++)
                    for (int pixelX = 0; pixelX < bitmap.Width; pixelX++)
                    {
                        Color color = bitmap.GetPixel(pixelX, pixelY);
                        if (color.R < 200 && color.G < 200 && color.B < 200) textPixels++;
                    }
                Check(textPixels > 20, "header column labels were not rendered");
            }
        }

        /// <summary>4-6：列表首绘只测量可见行的文字；滚动到更宽的行时列宽只增不减地扩展。</summary>
        private static void GridVisibleMeasurement()
        {
            using (Form host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000), Size = new Size(1400, 700) })
            using (Controls.AppGridControl grid = new Controls.AppGridControl { Location = Point.Empty, Size = new Size(1300, 600) })
            {
                host.Controls.Add(grid);
                host.Show();
                grid.GridMode = false;
                List<AppInfo> apps = new List<AppInfo>();
                for (int i = 0; i < 5000; i++) apps.Add(new AppInfo { Key = "af:vm-" + i, Name = "App " + i, TargetPath = "C:\\x\\f" + i + ".lnk" });
                apps.Add(new AppInfo { Key = "af:vm-wide", Name = "Wide", TargetPath = "D:\\" + new string('宽', 60) + "\\f.lnk" });
                grid.SetApps(apps);
                Type t = typeof(Controls.AppGridControl);
                FieldInfo locMax = t.GetField("locTextMaxW", Private);
                FieldInfo measuredHiF = t.GetField("measuredHi", Private);
                using (Bitmap bmp = new Bitmap(grid.Width, grid.Height))
                {
                    grid.DrawToBitmap(bmp, new Rectangle(Point.Empty, grid.Size));   // 首屏只量可见行
                    int firstW = (int)locMax.GetValue(grid);
                    int firstHi = (int)measuredHiF.GetValue(grid);
                    Check(firstHi >= 0 && firstHi < apps.Count - 1, "first paint measured the whole list: " + firstHi);
                    // 滚动到底部：末行的超长位置文本被量到，列宽只增不减
                    grid.AutoScrollPosition = new Point(0, grid.AutoScrollMinSize.Height);
                    grid.DrawToBitmap(bmp, new Rectangle(Point.Empty, grid.Size));
                    int grownW = (int)locMax.GetValue(grid);
                    int grownHi = (int)measuredHiF.GetValue(grid);
                    Check(grownHi == apps.Count - 1, "scrolling did not extend measurement to the last row: " + grownHi);
                    Check(grownW > firstW, "column width did not grow for the wider row: " + firstW + " -> " + grownW);
                }
                host.Hide();
            }
        }

        private static void MultiMembershipCategories()
        {
            AppConfig config = new AppConfig();
            config.ResourceCategories.Add("回归相册");
            string path = Path.Combine(root, "multi-member.jpg");
            File.WriteAllText(path, "jpg");
            config.AddedItems.Add(new AddedEntry { Path = path, Name = "multi-member", Category = "照片", IsResource = true });
            config.AddedItems.Add(new AddedEntry { Path = path, Name = "multi-member", Category = "回归相册", IsResource = true });
            config.AddedItems.Add(new AddedEntry { Path = path, Name = "multi-member", Category = "开发工具", IsResource = false });
            AppInfo pic = new AppInfo { Key = "add:" + path, Name = "multi-member", TargetPath = path, Category = "照片", IsFile = true };
            using (MainForm form = CategoryForm(config, pic))
            {
                Check((bool)Invoke(form, "InCategory", pic, "照片"), "primary category lost");
                Check((bool)Invoke(form, "InCategory", pic, "回归相册"), "second added category not recognised");
                Check(!(bool)Invoke(form, "InCategory", pic, "文档"), "unrelated category matched");
                config.CategoryOverride[pic.Key] = "文档";   // 在资源模式里移动到“文档”
                Check((bool)Invoke(form, "InCategory", pic, "文档"), "moved category not recognised");
                Check(!(bool)Invoke(form, "InCategory", pic, "回归相册"), "same-mode membership survived a move");
                Check((bool)Invoke(form, "InCategory", pic, "开发工具"), "other-mode membership lost after a move");
            }
        }

        private static void ScannerNameMerge()
        {
            Scanner scanner = new Scanner(new AppConfig());
            typeof(Scanner).GetField("nameIndex", Private).SetValue(scanner, new Dictionary<string, AppInfo>(StringComparer.Ordinal));
            Dictionary<string, AppInfo> dict = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
            MethodInfo merge = typeof(Scanner).GetMethod("Merge", Private);
            MethodInfo byName = typeof(Scanner).GetMethod("MergeByName", Private);
            // 收纳目录里的同名文件夹先扫到
            merge.Invoke(scanner, new object[] { dict, new AppInfo { Key = "add:D:\\Manage\\Python", Name = "Python",
                TargetPath = "D:\\Manage\\Python", IsFile = true, IsFolder = true } });
            byName.Invoke(scanner, new object[] { dict, new AppInfo { Key = "af:python", Name = "Python",
                TargetPath = "shell:AppsFolder\\Python.Regression" } });
            Check(dict.Values.Count(a => a.Name == "Python") == 2, "same-name folder swallowed the software");
            // 软件先出现，再来一个同名的文档快捷方式
            byName.Invoke(scanner, new object[] { dict, new AppInfo { Key = "af:chrome", Name = "Chrome",
                TargetPath = "shell:AppsFolder\\Chrome.Regression" } });
            byName.Invoke(scanner, new object[] { dict, new AppInfo { Key = "lnk:chrome-doc", Name = "Chrome", ShortcutPath = "C:\\Desk\\Chrome.lnk",
                TargetPath = "C:\\Docs\\Chrome.pdf", IsFile = true, IsDesktop = true } });
            Check(dict.Values.Count(a => a.Name == "Chrome") == 2, "document shortcut merged into the software");
            // 软件之间仍按名称去重
            byName.Invoke(scanner, new object[] { dict, new AppInfo { Key = "lnk:chrome", Name = "Chrome", ShortcutPath = "C:\\Desk\\Chrome (2).lnk",
                TargetPath = "C:\\Program Files\\Chrome\\chrome.exe", IsDesktop = true } });
            Check(dict.Values.Count(a => a.Name == "Chrome") == 2 && dict.Values.Any(a => a.Key == "af:chrome" && a.IsDesktop),
                "software shortcuts are no longer deduplicated by name");
        }

        private static void ScannerLaunchIdentityMerge()
        {
            Scanner scanner = new Scanner(new AppConfig());
            typeof(Scanner).GetField("nameIndex", Private).SetValue(scanner, new Dictionary<string, AppInfo>(StringComparer.Ordinal));
            FieldInfo launchIndex = typeof(Scanner).GetField("launchIndex", Private);
            if (launchIndex != null) launchIndex.SetValue(scanner, new Dictionary<string, AppInfo>(StringComparer.Ordinal));
            FieldInfo arguments = typeof(AppInfo).GetField("LaunchArguments", Private);
            Dictionary<string, AppInfo> dict = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
            MethodInfo byName = typeof(Scanner).GetMethod("MergeByName", Private);
            AppInfo ideaDesktop = new AppInfo { Key = "lnk:idea-old", Name = "IDEA2025", ShortcutPath = "D:\\Desktop\\IDEA2025.lnk",
                TargetPath = "D:\\APP\\IDEA\\bin\\idea64.exe", IsDesktop = true };
            AppInfo ideaSystem = new AppInfo { Key = "af:idea-release", Name = "IntelliJ IDEA Release",
                TargetPath = "d:\\app\\idea\\bin\\IDEA64.EXE" };
            byName.Invoke(scanner, new object[] { dict, ideaDesktop });
            byName.Invoke(scanner, new object[] { dict, ideaSystem });
            Check(dict.Count == 1 && dict.Values.Single() == ideaDesktop, "same executable and arguments were kept as duplicate software");

            AppInfo wps = new AppInfo { Key = "lnk:wps", Name = "WPS Office", ShortcutPath = "D:\\Desktop\\WPS Office.lnk",
                TargetPath = "D:\\APP\\WPS\\ksolaunch.exe", IsDesktop = true };
            AppInfo wpsPhoto = new AppInfo { Key = "lnk:wps-photo", Name = "WPS图片", ShortcutPath = "D:\\Desktop\\WPS图片.lnk",
                TargetPath = "D:\\APP\\WPS\\ksolaunch.exe", IsDesktop = true };
            if (arguments != null)
            {
                arguments.SetValue(wps, "/prometheus /fromksolaunch");
                arguments.SetValue(wpsPhoto, "/photolaunch /photo");
            }
            byName.Invoke(scanner, new object[] { dict, wps });
            byName.Invoke(scanner, new object[] { dict, wpsPhoto });
            Check(dict.Count == 3, "same executable with different launch arguments was merged");

            byName.Invoke(scanner, new object[] { dict, new AppInfo { Key = "add:wps-file", Name = "WPS document",
                TargetPath = "D:\\APP\\WPS\\ksolaunch.exe", IsFile = true } });
            Check(dict.Count == 4, "file entry was merged into a software launch target");
        }

        private static void SearchContextRemoval()
        {
            AppConfig config = new AppConfig();
            string path = Path.Combine(root, "regression-search-tool.exe");
            File.WriteAllText(path, "tool");
            config.AddedItems.Add(new AddedEntry { Path = path, Name = "Search Tool", Category = "开发工具" });
            config.Favorites.Add("add:" + path);
            config.Hidden.Add("add:" + path);
            AppInfo inside = new AppInfo { Key = "add:" + path, Name = "Search Tool", TargetPath = path, Category = "开发工具", IsFile = true };
            AppInfo outside = new AppInfo { Key = "af:regression-search-out", Name = "Search Browser", Category = "浏览器" };
            using (MainForm form = CategoryForm(config, inside, outside))
            {
                typeof(MainForm).GetField("currentCategory", Private).SetValue(form, "开发工具");
                AppManager.Controls.SearchField search = (AppManager.Controls.SearchField)typeof(MainForm).GetField("search", Private).GetValue(form);
                List<AppInfo> both = new List<AppInfo> { inside, outside };
                search.Text = "Search";
                List<AppInfo> removable = (List<AppInfo>)Invoke(form, "RemovableFromCurrent", both);
                Check(removable.Count == 1 && removable[0] == inside, "search offered removal for an item outside the category");
                search.Text = string.Empty;
                removable = (List<AppInfo>)Invoke(form, "RemovableFromCurrent", both);
                Check(removable.Count == 2, "non-search removal targets changed");
                ClickConfirmation(delegate { Invoke(form, "DeleteAddedEntry", inside); }, true);
                Check(!config.Favorites.Contains(inside.Key) && !config.Hidden.Contains(inside.Key), "record delete left favorite/hidden behind");
                Check(File.Exists(path), "record delete touched the disk file");
            }
        }

        private static void RestoreKeepsNewerStash()
        {
            // 前面的配额用例留下了 3 份留底：先挪到备份目录外，结束后放回，避免触发留底上限
            string parked = Path.Combine(Paths.DataDir, "parked-prerestore");
            Directory.CreateDirectory(parked);
            foreach (string dir in Directory.GetDirectories(Paths.BackupsDir, "_prerestore_*"))
                Directory.Move(dir, Path.Combine(parked, Path.GetFileName(dir)));
            Snapshot();   // 快照：收纳目录里只有 saved.txt，清单为空
            try
            {
                Directory.CreateDirectory(Paths.DesktopBackupDir);
                string desktop = DesktopOrganizer.UserDesktop;
                string newer = Path.Combine(Paths.DesktopBackupDir, "newer.txt");
                string sameName = Path.Combine(Paths.DesktopBackupDir, "saved.txt");
                File.WriteAllText(newer, "newer");
                File.WriteAllText(sameName, "live-version");
                AppConfig live = AppConfig.Load();
                live.DesktopBackup.Clear();
                live.DesktopBackup.Add(new DesktopBackupEntry { OriginalPath = Path.Combine(desktop, "newer.txt"), CurrentPath = newer, IsShortcut = false });
                live.DesktopBackup.Add(new DesktopBackupEntry { OriginalPath = Path.Combine(desktop, "saved.txt"), CurrentPath = sameName, IsShortcut = false });
                Check(live.Save(), "fixture save failed: " + live.DatabaseError);
                string error;
                Check(LayoutBackup.Restore("fixture", out error), "restore failed: " + error);
                AppConfig loaded = AppConfig.Load();
                string renamed = Path.Combine(Paths.DesktopBackupDir, "saved (2).txt");
                Check(File.Exists(newer) && File.ReadAllText(newer) == "newer", "newer stash file was not carried back");
                Check(loaded.DesktopBackup.Exists(e => string.Equals(e.CurrentPath, newer, StringComparison.OrdinalIgnoreCase)), "newer stash record dropped");
                Check(File.ReadAllText(sameName) == "saved", "snapshot file was not restored");
                Check(File.Exists(renamed) && File.ReadAllText(renamed) == "live-version"
                    && loaded.DesktopBackup.Exists(e => string.Equals(e.CurrentPath, renamed, StringComparison.OrdinalIgnoreCase)),
                    "same-name newer stash was lost");
            }
            finally
            {
                foreach (string dir in Directory.GetDirectories(Paths.BackupsDir, "_prerestore_*"))
                    try { Directory.Delete(dir, true); } catch { }
                foreach (string dir in Directory.GetDirectories(parked))
                    try { Directory.Move(dir, Path.Combine(Paths.BackupsDir, Path.GetFileName(dir))); } catch { }
            }
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
                Rectangle oversized = new Rectangle(work.Right - 10, work.Bottom - 10, work.Width + 200, work.Height + 200);
                Rectangle targetWork = Screen.FromRectangle(oversized).WorkingArea;
                SetDpi(form, 144, oversized);
                Check(targetWork.Contains(form.Bounds), "window exceeds target monitor work area: work=" + targetWork + " bounds=" + form.Bounds);
                foreach (Screen screen in Screen.AllScreens)
                {
                    work = screen.WorkingArea;
                    form.MinimumSize = new Size(Math.Min(600, work.Width), Math.Min(300, work.Height));
                    Rectangle small = new Rectangle(work.Right - 50, work.Bottom - 50, 40, 40);
                    SetDpi(form, 96, small);
                    Check(work.Contains(form.Bounds), "minimum size pushes window beyond " + screen.DeviceName + ": " + form.Bounds);
                    Check(form.Width >= form.MinimumSize.Width && form.Height >= form.MinimumSize.Height, "minimum size ignored");
                }
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
