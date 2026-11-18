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
        private static string onlyTests;
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;

        [STAThread]
        public static int Main()
        {
            Application.EnableVisualStyles();
            Control.CheckForIllegalCrossThreadCalls = true;
            string[] arguments = Environment.GetCommandLineArgs();
            if (arguments.Length > 1) onlyTests = arguments[1];
            root = Path.Combine(Path.GetTempPath(), "AppManagerRegression_" + Guid.NewGuid().ToString("N"));
            Paths.DataDirOverride = root;
            Paths.ConfigFileOverride = Path.Combine(root, "config.xml");
            Directory.CreateDirectory(root);
            // 桌面也换成临时目录：用例不在真实桌面上建文件，清理失败也不会留下残留
            DesktopOrganizer.UserDesktopOverride = Path.Combine(root, "Desktop");
            DesktopOrganizer.PublicDesktopOverride = Path.Combine(root, "PublicDesktop");
            Directory.CreateDirectory(DesktopOrganizer.UserDesktopOverride);
            Directory.CreateDirectory(DesktopOrganizer.PublicDesktopOverride);
            try
            {
                Run("restore targets: missing desktop path / traversal / ADS / relative", Targets);
                Run("unsafe restore keeps source and creates no destination", RejectRestore);
                Run("copy restore checks the original and suppresses auto stash", RestoreCopySafety);
                Run("auto stash quiet period starts when the whole restore batch ends", RestoreSuppressionStartsAtEnd);
                Run("collection thumbnails do not stay loading when the completion post is lost", CollectionThumbsLostPost);
                Run("desktop copy mode keeps same-name files and rolls back failed save", DesktopCopySafety);
                Run("auto stash reuses a matching backup instead of adding a suffix", AutoStashReusesMatchingBackup);
                Run("self shortcuts are identified by target instead of display name", SelfShortcutTarget);
                Run("copy: complete tree and file contents", CopyComplete);
                Run("copy limits: entries / bytes / timeout / depth", CopyLimits);
                Run("organizer copy failure leaves no partial target; retry succeeds", CopyRetry);
                Run("junction loop: copy rejects and preserves files", Junction);
                Run("failed restore preserves current config and files", RestoreFailure);
                Run("listing preserves old rollback data; quota refuses new restore", Retention);
                Run("manifest makes the process per-monitor DPI aware from startup", ManifestDpiAwareness);
                Run("DPI messages: effective DPI / suggested rectangle / repeated scaling", DpiMessages);
                Run("maximize stays in selected monitor work area and restores", MaximizeWithinScreenWorkArea);
                Run("window resize hit zones, minimum size, and fullscreen threshold", ResizeBounds);
                Run("non-client grid scrollbar keeps the right resize edge active", ResizeOverGridScrollbar);
                Run("menu hit regions and rendering at 100/125/150/175 percent", MenuDpi);
                Run("backup cards: size, grouping and inline buttons at 100/125/150/175 percent", BackupDpi);
                Run("backup cards: texts, keyboard and mouse actions, busy state, selection and scrolling", BackupCards);
                Run("backup cards: one wheel notch scrolls the list once from the list, a button or the window", BackupWheel);
                Run("background status: queued update, disposal race, no handle", StatusRace);
                Run("rounded path and progress bar: one-pixel fill renders without invalid arcs", RoundedPathNarrow);
                Run("auto scan: existing shortcut rewrite, periodic fallback and disposal", AutoScanChanges);
                Run("category removal: hidden toggle, single/batch, reload and rejoin", CategoryRemoval);
                Run("category removal: override, extra membership and added file", CategoryRemovalSources);
                Run("category icon-only edit keeps item order; rename migrates it", CategoryIconOnlyEdit);
                Run("deleting a resource category removes covers, view mode and the add button", CategoryDeleteCleansState);
                Run("main form disposed without Close leaves no message filter or tray icon behind", DisposedFormMessageFilter);
                Run("storage dialog writes back only the edited fields", StoreDialogSavesOnlyEdits);
                Run("storage locations keep order, disabled roots and legacy first-path compatibility", ResourceStoreLocationsPool);
                Run("custom category add/remove uses the same memberships and keeps an existing icon", CustomCategoryAddRemove);
                Run("classification keeps uninstaller apps and prefers specific keywords", ClassificationRules);
                Run("move to category removes only the source membership and keeps collections", MoveKeepsOtherMemberships);
                Run("resource defaults: remove, preserve library, reload and menu rejoin", ResourceCategoryRemoval);
                Run("category removal: failed save rolls back membership and permits retry", CategoryRemovalSaveFailure);
                Run("default category addition: single/batch, multiple memberships, removal and reload", DefaultCategoryAddition);
                Run("properties: software/resource menus, grid/list and complete memberships", PropertiesMenus);
                Run("context menu order: copy/hide early, remove/delete last", ContextMenuOrder);
                Run("properties: file metadata, folders, missing targets and packaged apps", PropertiesMetadata);
                Run("properties: DPI scrolling, responsive open and cancelled background load", PropertiesResponsiveAndDpi);
                Run("cross-volume move: no foreign overwrite, read-only source, locked file kept", CrossVolumeMoves);
                Run("rename: managed resource file renames on disk and migrates every keyed record", RenameManagedFile);
                Run("rename: invalid names, conflicts, extension rule/confirm, case-only, locked file, save-failure rollback", RenameValidation);
                Run("rename: folders rebase covers and refuse nested entries; software/referenced items keep display-name rename", RenameFoldersAndSoftware);
                Run("move into folder keeps favorite, hidden, order, membership and cover", MoveIntoFolderKeepsRecords);
                Run("legacy stores: resource entries move into typed resource folders; software, nested, deep and loose items stay", LegacyStoreMoves);
                Run("legacy stores: save failure moves the batch back; entries removed meanwhile go back unregistered", LegacyStoreMoveFailures);
                Run("legacy stores: interrupted batch is re-registered from the journal without deleting files", LegacyStoreMoveResume);
                Run("legacy stores: default locations under the manage root skip the store structure", LegacyStoreDefaultRoots);
                Run("legacy stores: startup prompt leaves files on decline and moves them on accept", LegacyStoreStartupPrompt);
                Run("resource page creates entries in the resource store", ResourceCreateEntries);
                Run("resource page creation falls back to the next enabled storage location", ResourceCreateFallsBackToNextStore);
                Run("software page files migrate to typed resource categories; executables and resource relations stay", SoftwareFilesMigrate);
                Run("scanner keeps programs, protocols and packaged apps; drops folder and document targets", SoftwareScanTargets);
                Run("software page refuses external drops, file imports, new files and the all-files view", SoftwarePageRefusesFiles);
                Run("desktop stash moves software shortcuts only; folder and document shortcuts stay on the desktop", StashOnlySoftwareShortcuts);
                Run("import recovery and history still recognise old software batches after migration", ImportRecoveryAfterMigration);
                Run("music scan reuses copied origins after repeat, rename and category changes", MusicScanReusesCopiedOrigins);
                Run("queued in-memory imports see the earlier commit before the final duplicate check", QueuedMemoryImportsUseFreshSnapshot);
                Run("music cleanup skips unreachable parents and removes only music memberships", MissingMusicCleanup);
                Run("layout listing exposes pre-restore leftovers", PrerestoreListing);
                Run("layout restore keeps stash entries created after the snapshot", RestoreKeepsNewerStash);
                Run("search results only offer removal for items in the category; record delete purges keyed config", SearchContextRemoval);
                Run("search follows the selected category and direct collection scope in database and fallback", SearchScopeFollowsCategory);
                Run("search field scales once and clear button follows input", SearchFieldClearButton);
                Run("scanner name merge never lets files or document shortcuts swallow software", ScannerNameMerge);
                Run("scanner merges duplicate launch targets only when arguments also match", ScannerLaunchIdentityMerge);
                Run("restored shortcut aliases merge through resolve, add and name merge", RestoreAliasShortcutScan);
                Run("newer desktop shortcut replaces launch target while keeping identity", NewerShortcutKeepsIdentity);
                Run("membership checks recognise every added category, like the listing projection", MultiMembershipCategories);
                Run("grid scroll range follows width changes so the last rows stay reachable", GridScrollFollowsWidth);
                Run("wide windows spread grid cards and list columns evenly; narrow layout is unchanged", WideLayoutSpreads);
                Run("toolbar reserves room for long counts and lets the mini player fill the gap", ToolbarCountAndPlayerWidth);
                Run("grid measures only visible rows; column width grows while scrolling to wider rows", GridVisibleMeasurement);
                Run("list header paints before rows for empty and populated lists with bounded measurement", GridHeaderFirstPaint);
                Run("main grid paint failures render a fallback instead of throwing", GridPaintFailureFallback);
                Run("list header survives filtering to empty, refill, view switches and sorting", GridHeaderRefresh);
                Run("stash scan reads root shortcuts and legacy records only, never stashed folder trees", StashScanTopLevel);
                Run("background scan uses the UI snapshot and leaves UI-owned settings to the UI thread", ScanUsesUiSnapshot);
                Run("apps folder known-folder ids resolve to real paths; app ids stay unchanged", KnownFolderIds);
                Run("grid caches category text and falls back when the provider fails", GridCategoryTextCache);
                Run("selection footer resolves names, locations, sources and packaged ids", SelectionFooterText);
                Run("grid selection fires once per change and restores a ten-thousand item selection", GridSelectionNotifications);
                Run("drag ghost keeps independent snapshots, session ids and idempotent lifecycle", DragGhostLifecycle);
                Run("drag ghost rapid begin/end reuses one layered window and releases GDI handles", DragGhostRapidLifecycle);
                Run("grid starts drag visuals only after the threshold and cancels on escape", GridDragVisualLifecycle);
                Run("grid drag cleanup clears capture, cancel mode and list refresh without a drop", GridDragCleanup);
                Run("drag intent maps reorder, category, collection and folder semantics", DragIntentSemantics);
                Run("grid reorder persists in software, resource and collection scopes", GridReorderScopes);
                Run("OLE export payload count follows the actual distinct exportable paths", DragExportPayload);
                Run("sidebar scrolls with high-resolution wheel deltas; header strip matches grid client width", ShellWheelAndHeader);
                Run("collection sidebar keeps its width and alignment across tray hide/restore", CollectionSidebarTrayRestore);
                Run("settings page rows, dependent switches, inline storage actions, scrolling and DPI layout", SettingsPageRows);
                Run("settings in the main window: rescan feedback, storage edits with save rollback, wheel routing", SettingsRescanFeedback);
                Run("minimize keeps the laid-out controls and restore lays out at the real size", MinimizeKeepsLayout);
                Run("tray hide keeps the music playlist and current track", TrayHideKeepsMusicPlaylist);
                Run("opening settings keeps the music playlist and position", SettingsKeepsMusicPlaylist);
                Run("opening a video pauses music and keeps the playlist and position", VideoKeepsMusicPlaylist);
                Run("collection sidebar tree toggles without selecting and keeps scroll anchors", CollectionSidebarTreeInteraction);
                Run("collection tree auto-expands and searches only direct members", CollectionTreeSearchAndInfo);
                Run("collection creation uses active folder scope and enforces tree limits", CollectionCreateScopeAndLimits);
                Run("collection draft validation covers rename, move and self-nesting", CollectionDraftValidation);
                Run("collection edit page creates, keeps creating, renames and moves in one save", CollectionEditPageFlow);
                Run("collection edit page keeps pending members, asks before dropping edits and cleans up failed covers", CollectionEditPageGuards);
                Run("collection deletion falls back to the nearest surviving ancestor", CollectionDeleteFallback);
                Run("collection move keeps ids, order and references across parents", CollectionMovePersistence);
                Run("collection move rejects invalid targets and failed saves stay retryable", CollectionMoveGuards);
                Run("collection internal drag reports one final drop and cancels on escape", CollectionSidebarInternalDrag);
                Run("collection move remaps the UI selection and is blocked while editing", CollectionMoveUi);
                Run("grid delete handles every selected item; arrow starts at the first item", GridBatchDeleteAndArrow);
                Run("icon cache evicts by bytes and LRU instead of clearing everything", IconCacheLru);
                Run("auto scan pause keeps pending and paused changes, resume fires once", AutoScanPauseResume);
                Run("viewer context menu targets the viewed item despite grid multi-selection", ViewerResourceSingleTarget);
                Run("viewer delete/rename protects unsaved text before path changes", ViewerMutationProtectsDirtyText);
                Run("viewer dirty text completes delete/rename after save or explicit discard", ViewerDirtyMutationSuccess);
                Run("viewer delete cancel/failure and escape keep the viewer open", ViewerActionOutcomeKeepsViewer);
                Run("viewer external deletion closes and removes the stale record", ViewerMissingResource);
                Run("viewer closes without purging a valid resource outside the current list", ViewerResourceOutsideCurrentList);
                Run("missing resources can be deleted from the context menu", MissingResourceDelete);
                Run("viewer lazy submenu actions are wrapped before execution", ViewerLazySubmenuAction);
                Run("confirm dialog follows escape/enter and round buttons respond to space", DialogKeyboard);
                Run("scan cache drops XML-invalid control characters", ScanCacheInvalidXml);
                Run("add-software picker tells installed apps from system tools", SoftwarePickerRules);
                Run("add-software picker joins the category and skips missing programs", SoftwarePickerAdds);
                Run("local scan scope lists folders, drives and added folders in one checklist", LocalScanScopeRows);
                Run("local scan scope dialog toggles, removes and returns merged roots", LocalScanScopeDialogSelection);
                Run("local scan scope expands subfolders in place", LocalScanScopeTree);
                Run("local scan scope dialog reports problems inline instead of popping up", LocalScanScopeDialogInline);
                Run("local scan result folds the unconfirmed question into a checkbox", LocalScanResultOption);
                Run("local scan progress keeps the window until the worker stops", LocalScanProgressLifetime);
                Run("local scan result enter never starts a destructive move", LocalScanResultKeyboard);
                Run("local scan result headline and key numbers", LocalScanResultSummaries);
                Run("backup list ignores update leftovers", BackupListSkipsOld);
                Run("backup create removes stale temporary snapshots", BackupCleansStaleTemp);
                Run("music player caches the scaled cover per size", MusicPlayerCoverCache);
                Run("import history sorts problems first and names each record's state", ImportHistoryRules);
                Run("import history dialog loads the journal and hands back the batch to clean up", ImportHistoryDialogFlow);
                Run("import history cleanup deletes sources through the import queue; closing deletes nothing", ImportHistoryCleanupEndToEnd);
                Run("message dialog keeps MessageBox buttons, results, Esc and default button", MessageDialogResults);
                Run("category picker preselects the suggestion, confirms with Enter, cancels with Esc", CategoryPickerFlow);
                Run("duplicate dialog lists keepable rows first and returns only checked sources", DuplicateDialogFlow);
                Run("desktop backup cards show names and raise remove only when idle", DesktopBackupListRules);
                Run("check rows toggle, radio rows stay exclusive and follow arrow keys", CheckRowBehaviour);
                Run("main grid scrolls itself: clamps, follows the wheel and keeps the old position API", GridThinScroll);
                Run("thin list scrollbar appears only on overflow and narrows the rows", ThinListScrollBar);
                Run("themed tooltip sizes bound and directly shown text, skips empty text", ThemedToolTipMeasure);
                Run("review fixes: long text boxes open safely, keyboard restore, left-click only, radio tab stop, MTA fallback", ReviewFollowUps);
                Run("operation history names actions and explains each change in plain words", OperationHistoryRules);
                Run("database panel lists operations, pages details and undoes only the latest one", DatabaseFormFlow);
                Run("path picker checks paths, asks before creating or replacing and keeps failed validation in the window", PathPickerFlow);
                Run("cover picker lists thumbnails, previews, blocks unreadable images, opens typed folders and remembers the last cover", CoverPickerFlow);
                Run("add page lists by type, checks across folders, previews, takes drops and typed paths and drops vanished files", MediaAddPageFlow);
                Run("add page for folders and software: checks folders, blocks drives, lists installed software with .exe files", MediaAddPageFolderAndSoftware);
                Run("add page opens inside the main window, stays on top, follows resizes and restores the main content", MediaAddPageInMainWindow);
                Run("embedded pages: cover page inside the main window keeps the edit, desktop backup list is a busy-aware settings section", EmbeddedPages);
                Run("line icons scale with the display DPI, stay centred and ignore the size of their box", IconsScaleWithDpi);
                Run("retrospective fixes: repair records visible, batch adds skip scrollbar sync, Alt+F4 on yes/no, drag across duplicate rows, backup list waits and stays busy",
                    RetrospectiveFixes);
                Run("exporting change details writes a temp file first: a failure keeps the old file and leaves no partial file", ExportChangesAtomic);
                Run("music state restores paused on startup and is written back on exit", MusicStateAcrossRestart);
            }
            finally
            {
                Paths.ConfigFileOverride = null;
                Paths.DataDirOverride = null;
                DesktopOrganizer.UserDesktopOverride = null;
                DesktopOrganizer.PublicDesktopOverride = null;
                Directory.Delete(root, true);
            }
            Console.WriteLine("RESULT " + passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        private static void Run(string name, Action action)
        {
            if (!string.IsNullOrEmpty(onlyTests)
                && name.IndexOf(onlyTests, StringComparison.OrdinalIgnoreCase) < 0) return;
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
                    Control button = ConfirmButton(dialog, accept);
                    if (button != null)
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

        /// <summary>
        /// 确认框上的按钮：accept 取主按钮，否则取取消一侧。主按钮的文字写的是具体动作（「删除」「还原」「移除」……），
        /// 不再一律是「确定」，所以先按文字找，找不到再按主按钮 / 非主按钮来认。
        /// </summary>
        private static Control ConfirmButton(Form dialog, bool accept)
        {
            Control fallback = null;
            foreach (Control child in dialog.Controls)
            {
                if (child.Text == (accept ? "确定" : "取消")) return child;
                Controls.RoundButton round = child as Controls.RoundButton;
                if (round == null) continue;
                if (accept ? (round.Accent && fallback == null) : !round.Accent) fallback = child;
            }
            return fallback;
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
                            // 放得下就不挂滚动条；超过两行放不下时必须有，长内容才能滚动着看全
                            bool overflow = (box.GetLineFromCharIndex(box.TextLength) + 1) * box.Font.Height > box.ClientSize.Height;
                            Check(box.ReadOnly && box.Multiline && (!overflow || box.ScrollBars == ScrollBars.Vertical),
                                "long values cannot be read safely");
                            Check(overflow || box.ScrollBars == ScrollBars.None, "a short value still carries a scrollbar");
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

        private static MainForm SearchForm(AppConfig config, Controls.SidebarControl.ContentMode mode, string category, params AppInfo[] apps)
        {
            MainForm form = new MainForm(config);
            Invoke(form, "BuildControls");
            typeof(MainForm).GetField("contentMode", Private).SetValue(form, mode);
            typeof(MainForm).GetField("currentCategory", Private).SetValue(form, category);
            typeof(MainForm).GetField("allApps", Private).SetValue(form, new List<AppInfo>(apps));
            typeof(MainForm).GetField("scanRunning", Private).SetValue(form, true);
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
                AppManager.Controls.MusicPlaybackSession session = (AppManager.Controls.MusicPlaybackSession)typeof(MainForm).GetField("musicSession", Private).GetValue(form);
                FieldInfo playlist = typeof(AppManager.Controls.MusicPlaybackSession).GetField("paths", Private);
                playlist.SetValue(session, new List<string> { path, Path.Combine(dir, "other.mp3") });
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
                List<string> songs = (List<string>)playlist.GetValue(session);
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
                    Check(locked != null && locked.StartsWith("无法重命名："), "locked file: " + locked);
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

        // ===== 旧版存储位置整理 =====

        private static Func<List<MainForm.LegacyMove>, List<MainForm.LegacyMove>, bool> LegacyCommit(MainForm form)
        {
            return delegate(List<MainForm.LegacyMove> done, List<MainForm.LegacyMove> stale) { return (bool)Invoke(form, "CommitLegacyMoves", done, stale); };
        }

        private static List<MainForm.LegacyMove> FindLegacy(MainForm form, List<string> skipped)
        {
            return MainForm.FindLegacyMoves((MainForm.LegacyStoreScan)Invoke(form, "SnapshotLegacyStores"), skipped);
        }

        private static string LegacyTarget(List<MainForm.LegacyMove> moves, string source)
        {
            MainForm.LegacyMove move = moves.FirstOrDefault(m => string.Equals(m.Source, source, StringComparison.OrdinalIgnoreCase));
            return move == null ? null : move.Target;
        }

        private static List<AddedEntry> LegacyEntries(MainForm form, string path)
        {
            return (List<AddedEntry>)Invoke(form, "EntriesAt", path);
        }

        private static void WriteLegacyJournal(List<MainForm.LegacyMove> moves)
        {
            Check((bool)typeof(MainForm).GetMethod("WriteLegacyJournal", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { moves }),
                "legacy journal not written");
        }

        private static void LegacyStoreMoves()
        {
            AppConfig config = RenameConfig("legacy-store");
            string files = config.FileStorePath, folders = config.FolderStorePath, res = config.ResourceStorePath;
            AppInfo photo = AddEntry(config, "照片", Path.Combine(files, "a.png"), false, true, "a");
            AppInfo album = AddEntry(config, "文件", Path.Combine(folders, "相册"), true, true, "相册");
            string cover = Path.Combine(album.TargetPath, "cover.png");
            File.WriteAllText(cover, "png");                                                    // 未单独收录：随文件夹一起搬
            AppInfo dotted = AddEntry(config, "文件", Path.Combine(folders, "2026.10"), true, true, "2026.10");
            Directory.CreateDirectory(Path.Combine(res, "文件", "2026.10"));                     // 目标已有同名文件夹
            AppInfo doc = AddEntry(config, "文档", Path.Combine(files, "note.txt"), false, true, "note");
            config.AddedItems.Add(new AddedEntry { Path = Path.Combine(res, "文档", "note.txt"), Category = "文档", IsResource = true });   // 目标路径已收录
            AppInfo twin = AddEntry(config, "文档", Path.Combine(folders, "note.txt"), false, true, "note");   // 另一个旧版位置的同名文件
            // 留在原处：软件条目、内有单独收录项的文件夹、更深层的条目、已失效的条目、未收录的文件、已在资源存储位置的条目
            AppInfo exe = AddEntry(config, "开发工具", Path.Combine(files, "tool.exe"), false, false, "tool");
            AppInfo nested = AddEntry(config, "文件", Path.Combine(folders, "嵌套"), true, true, "嵌套");
            AppInfo inside = AddEntry(config, "文档", Path.Combine(nested.TargetPath, "inside.txt"), false, true, "inside");
            AppInfo deep = AddEntry(config, "文档", Path.Combine(files, "sub", "deep.txt"), false, true, "deep");
            AppInfo missing = AddEntry(config, "文档", Path.Combine(files, "missing.txt"), false, true, "missing");
            File.Delete(missing.TargetPath);
            string loose = Path.Combine(files, "未收录.txt");
            File.WriteAllText(loose, "x");
            AppInfo stored = AddEntry(config, "照片", Path.Combine(res, "照片", "b.png"), false, true, "b");
            config.Favorites.Add(photo.Key);
            config.Collections.Add(new ResourceCollection { Parent = "文件", Name = "精选", Cover = cover });
            string photoTarget = Path.Combine(res, "照片", "a.png");
            using (MainForm form = ResourceForm(config, "照片", photo, album, dotted, doc, twin, exe, nested, inside, deep, missing, stored))
            {
                Check(form.Handle != IntPtr.Zero, "form handle not created");
                List<string> skipped = new List<string>();
                List<MainForm.LegacyMove> moves = FindLegacy(form, skipped);
                Check(moves.Count == 5, "movable legacy entries: " + string.Join(", ", moves.Select(m => m.Source).ToArray()));
                Check(LegacyTarget(moves, photo.TargetPath) == photoTarget
                    && LegacyTarget(moves, album.TargetPath) == Path.Combine(res, "文件", "相册")
                    && LegacyTarget(moves, dotted.TargetPath) == Path.Combine(res, "文件", "2026.10_1")
                    && LegacyTarget(moves, doc.TargetPath) == Path.Combine(res, "文档", "note_1.txt")
                    && LegacyTarget(moves, twin.TargetPath) == Path.Combine(res, "文档", "note_2.txt"),
                    "legacy targets: " + string.Join(", ", moves.Select(m => m.Target).ToArray()));
                Check(skipped.Count == 2 && skipped.Any(s => s.StartsWith("tool.exe：")) && skipped.Any(s => s.StartsWith("嵌套：")),
                    "skipped: " + string.Join(" | ", skipped.ToArray()));

                MainForm.LegacyMoveResult result = new MainForm.LegacyMoveResult();
                List<string> progress = new List<string>();
                MainForm.RunLegacyMoves(moves, LegacyCommit(form), delegate(int index, int total, string name) { progress.Add(name); },
                    CancellationToken.None, result);
                Check(result.Moved == 5 && result.Failed.Count == 0 && result.Kept.Count == 0 && result.Error == null && progress.Count == 5,
                    "legacy move result: " + result.Moved + " " + result.Error + " " + string.Join(" | ", result.Failed.ToArray()));
                foreach (MainForm.LegacyMove m in moves)
                {
                    Check((m.Folder ? Directory.Exists(m.Target) : File.Exists(m.Target)) && !File.Exists(m.Source) && !Directory.Exists(m.Source),
                        "not moved: " + m.Source);
                    Check(LegacyEntries(form, m.Source).Count == 0 && LegacyEntries(form, m.Target).Count == 1, "records not rewritten: " + m.Source);
                }
                string movedCover = Path.Combine(res, "文件", "相册", "cover.png");
                Check(File.Exists(movedCover) && config.Collections[0].Cover == movedCover, "cover did not follow the folder");
                Check(config.Favorites.Contains("add:" + photoTarget) && !config.Favorites.Contains(photo.Key), "favorite did not follow");
                Check(File.Exists(exe.TargetPath) && Directory.Exists(nested.TargetPath) && File.Exists(inside.TargetPath) && File.Exists(deep.TargetPath)
                    && File.Exists(loose) && File.Exists(stored.TargetPath), "items that should stay were moved");
                Check(!File.Exists(MainForm.LegacyMoveJournal), "journal left after a committed batch");
                Check(FindLegacy(form, new List<string>()).Count == 0, "second scan found work again");
            }
            AppConfig loaded = AppConfig.Load();
            Check(loaded.AddedItems.ByPath(photoTarget).Count == 1 && loaded.AddedItems.ByPath(photo.TargetPath).Count == 0, "move not persisted");
            Check(loaded.Favorites.Contains("add:" + photoTarget), "favorite not persisted");
            Check(loaded.AddedItems.ByPath(exe.TargetPath).Count == 1 && loaded.AddedItems.ByPath(missing.TargetPath).Count == 1
                && loaded.AddedItems.ByPath(deep.TargetPath).Count == 1, "entries that should stay changed");
        }

        private static void LegacyStoreMoveFailures()
        {
            AppConfig config = RenameConfig("legacy-store-fail");
            config.FolderStorePath = config.FileStorePath;   // 两个旧版位置相同（常见配置）：每项只处理一次
            string store = config.FileStorePath, res = config.ResourceStorePath;
            AppInfo photo = AddEntry(config, "照片", Path.Combine(store, "c.png"), false, true, "c");
            AppInfo folder = AddEntry(config, "文件", Path.Combine(store, "夹"), true, true, "夹");
            AppInfo gone = AddEntry(config, "文档", Path.Combine(store, "gone.txt"), false, true, "gone");
            AppInfo locked = AddEntry(config, "文档", Path.Combine(store, "locked.txt"), false, true, "locked");
            using (MainForm form = ResourceForm(config, "照片", photo, folder, gone, locked))
            {
                Check(form.Handle != IntPtr.Zero, "form handle not created");
                List<MainForm.LegacyMove> moves = FindLegacy(form, new List<string>());
                Check(moves.Count == 4, "same legacy root handled twice: " + moves.Count);

                // 保存失败：整批搬回原处，记录不变，日志清除
                MainForm.LegacyMoveResult result = new MainForm.LegacyMoveResult();
                using (Sqlite db = new Sqlite(config.Database.FilePath, false))
                {
                    db.Execute("BEGIN IMMEDIATE");
                    MainForm.RunLegacyMoves(moves, LegacyCommit(form), null, CancellationToken.None, result);
                    db.Execute("ROLLBACK");
                }
                Check(result.Error != null && result.Error.StartsWith("资源库未能保存") && result.Moved == 0, "save failure not reported: " + result.Error);
                foreach (MainForm.LegacyMove m in moves)
                    Check((m.Folder ? Directory.Exists(m.Source) : File.Exists(m.Source)) && !File.Exists(m.Target) && !Directory.Exists(m.Target)
                        && LegacyEntries(form, m.Source).Count == 1, "save failure did not move back: " + m.Source);
                Check(!File.Exists(MainForm.LegacyMoveJournal), "journal left after a full rollback");

                // 重试：被占用的文件单独失败；搬运期间被删除的条目文件搬回原处、不登记；其余照常
                moves = FindLegacy(form, new List<string>());
                result = new MainForm.LegacyMoveResult();
                Func<List<MainForm.LegacyMove>, List<MainForm.LegacyMove>, bool> commit = LegacyCommit(form);
                using (new FileStream(locked.TargetPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    MainForm.RunLegacyMoves(moves, delegate(List<MainForm.LegacyMove> done, List<MainForm.LegacyMove> stale)
                    {
                        foreach (AddedEntry e in LegacyEntries(form, gone.TargetPath)) config.AddedItems.Remove(e);
                        return commit(done, stale);
                    }, null, CancellationToken.None, result);
                Check(result.Moved == 2 && result.Error == null && result.Failed.Count == 1 && result.Failed[0].StartsWith("locked.txt："),
                    "retry result: " + result.Moved + " " + result.Error + " " + string.Join(" | ", result.Failed.ToArray()));
                Check(File.Exists(locked.TargetPath) && LegacyEntries(form, locked.TargetPath).Count == 1, "locked file changed");
                string goneTarget = Path.Combine(res, "文档", "gone.txt");
                Check(File.Exists(gone.TargetPath) && !File.Exists(goneTarget) && LegacyEntries(form, goneTarget).Count == 0,
                    "entry removed during the move was registered or not moved back");
                Check(File.Exists(Path.Combine(res, "照片", "c.png")) && Directory.Exists(Path.Combine(res, "文件", "夹")), "retry did not move");
                Check(!File.Exists(MainForm.LegacyMoveJournal), "journal left after the retry");
            }
        }

        private static void LegacyStoreMoveResume()
        {
            AppConfig config = RenameConfig("legacy-store-resume");
            string files = config.FileStorePath, res = config.ResourceStorePath;
            AppInfo moved = AddEntry(config, "文档", Path.Combine(files, "moved.txt"), false, true, "moved");           // 已搬，日志已标记
            AppInfo unmarked = AddEntry(config, "文档", Path.Combine(files, "unmarked.txt"), false, true, "unmarked"); // 已搬，标记未写入
            AppInfo copied = AddEntry(config, "文件", Path.Combine(files, "copied"), true, true, "copied");            // 跨卷：副本就位、源未删完
            AppInfo pending = AddEntry(config, "文档", Path.Combine(files, "pending.txt"), false, true, "pending");     // 还没搬
            string docs = Path.Combine(res, "文档");
            string movedTarget = Path.Combine(docs, "moved.txt"), unmarkedTarget = Path.Combine(docs, "unmarked.txt");
            string copiedTarget = Path.Combine(res, "文件", "copied"), pendingTarget = Path.Combine(docs, "pending.txt");
            Directory.CreateDirectory(docs);
            File.Move(moved.TargetPath, movedTarget);
            File.Move(unmarked.TargetPath, unmarkedTarget);
            Directory.CreateDirectory(copiedTarget);
            File.WriteAllText(Path.Combine(copiedTarget, "x.txt"), "x");
            WriteLegacyJournal(new List<MainForm.LegacyMove>
            {
                new MainForm.LegacyMove { Source = moved.TargetPath, Target = movedTarget, Moved = true },
                new MainForm.LegacyMove { Source = unmarked.TargetPath, Target = unmarkedTarget },
                new MainForm.LegacyMove { Source = copied.TargetPath, Target = copiedTarget, Folder = true, Moved = true },
                new MainForm.LegacyMove { Source = pending.TargetPath, Target = pendingTarget }
            });
            File.AppendAllText(MainForm.LegacyMoveJournal, "garbage\r\n1\t0\trelative\tpath\r\n");
            using (MainForm form = ResourceForm(config, "文档", moved, unmarked, copied, pending))
            {
                Check(MainForm.ResumeLegacyMoves(LegacyCommit(form)), "journal did not resume");
                Check(LegacyEntries(form, movedTarget).Count == 1 && LegacyEntries(form, moved.TargetPath).Count == 0, "moved entry not re-registered");
                Check(LegacyEntries(form, unmarkedTarget).Count == 1 && LegacyEntries(form, unmarked.TargetPath).Count == 0, "unmarked entry not re-registered");
                Check(LegacyEntries(form, copiedTarget).Count == 1 && Directory.Exists(copied.TargetPath) && File.Exists(Path.Combine(copiedTarget, "x.txt")),
                    "cross-volume copy not re-registered, or a copy was deleted");
                Check(LegacyEntries(form, pending.TargetPath).Count == 1 && File.Exists(pending.TargetPath) && !File.Exists(pendingTarget), "unmoved entry changed");
                Check(!File.Exists(MainForm.LegacyMoveJournal), "journal not removed after resume");

                // 已提交后才中断（记录已在新位置）：重放不改动任何东西
                WriteLegacyJournal(new List<MainForm.LegacyMove> { new MainForm.LegacyMove { Source = moved.TargetPath, Target = movedTarget, Moved = true } });
                Check(!MainForm.ResumeLegacyMoves(LegacyCommit(form)) && LegacyEntries(form, movedTarget).Count == 1 && File.Exists(movedTarget)
                    && !File.Exists(MainForm.LegacyMoveJournal), "committed journal replay changed records");
            }
            Check(AppConfig.Load().AddedItems.ByPath(movedTarget).Count == 1, "resume not persisted");
        }

        /// <summary>启动入口：后台线程查找、界面线程询问，「以后再说」不动文件，「移动」后搬完并登记。</summary>
        private static void LegacyStoreStartupPrompt()
        {
            AppConfig config = RenameConfig("legacy-store-startup");
            AppInfo photo = AddEntry(config, "照片", Path.Combine(config.FileStorePath, "s.png"), false, true, "s");
            string target = Path.Combine(config.ResourceStorePath, "照片", "s.png");
            using (MainForm form = ResourceForm(config, "照片", photo))
            {
                Check(form.Handle != IntPtr.Zero, "form handle not created");
                AnswerLegacyPrompt(form, "以后再说");
                Check(File.Exists(photo.TargetPath) && !File.Exists(target) && LegacyEntries(form, photo.TargetPath).Count == 1, "declined prompt moved files");
                AnswerLegacyPrompt(form, "移动");
                Check(!File.Exists(photo.TargetPath) && File.Exists(target) && LegacyEntries(form, target).Count == 1
                    && LegacyEntries(form, photo.TargetPath).Count == 0, "accepted prompt did not move and register");
                Check(!File.Exists(MainForm.LegacyMoveJournal), "journal left after the startup flow");
            }
        }

        private static void AnswerLegacyPrompt(MainForm form, string answer)
        {
            bool answered = false;
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 10 })
            {
                timer.Tick += delegate
                {
                    foreach (Form open in Application.OpenForms)
                    {
                        if (!open.Modal) continue;
                        open.Opacity = 0;
                        foreach (Control button in open.Controls)
                            if (button.Text == answer)
                            {
                                timer.Stop();
                                typeof(Control).GetMethod("OnClick", Private).Invoke(button, new object[] { EventArgs.Empty });
                                answered = true;
                                return;
                            }
                    }
                };
                timer.Start();
                Invoke(form, "StartLegacyStoreMigration");
                FieldInfo running = typeof(MainForm).GetField("importRunning", Private);
                PumpUntil(() => answered && !(bool)running.GetValue(form));
            }
            Check(answered, "legacy prompt was not shown");
        }

        private static void LegacyStoreDefaultRoots()
        {
            string manage = Path.Combine(root, "legacy-store-default", "manage");
            AppConfig config = new AppConfig { ManageRootPath = manage };
            AppInfo file = AddEntry(config, "文档", Path.Combine(manage, "Files", "x.txt"), false, true, "x");
            AppInfo folder = AddEntry(config, "文件", Path.Combine(manage, "相册"), true, true, "相册");
            AppInfo stored = AddEntry(config, "照片", Path.Combine(manage, "Resources", "照片", "y.png"), false, true, "y");
            AppInfo filesRoot = AddEntry(config, "文件", Path.Combine(manage, "Files"), true, true, "Files");          // 存储结构本身
            AppInfo resourcesRoot = AddEntry(config, "文件", Path.Combine(manage, "Resources"), true, true, "Resources");
            using (MainForm form = ResourceForm(config, "文档", file, folder, stored, filesRoot, resourcesRoot))
            {
                List<string> skipped = new List<string>();
                List<MainForm.LegacyMove> moves = FindLegacy(form, skipped);
                Check(moves.Count == 2 && skipped.Count == 0
                    && LegacyTarget(moves, file.TargetPath) == Path.Combine(manage, "Resources", "文档", "x.txt")
                    && LegacyTarget(moves, folder.TargetPath) == Path.Combine(manage, "Resources", "文件", "相册"),
                    "default legacy roots: " + string.Join(", ", moves.Select(m => m.Source + " => " + m.Target).ToArray())
                    + " | " + string.Join(" | ", skipped.ToArray()));
            }
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

        private static void ContextMenuOrder()
        {
            AppConfig softwareConfig = new AppConfig();
            string firstPath = Path.Combine(root, "menu-order-first.exe");
            string secondPath = Path.Combine(root, "menu-order-second.exe");
            File.WriteAllText(firstPath, "keep");
            File.WriteAllText(secondPath, "keep");
            AppInfo first = new AppInfo
            {
                Key = "menu-order:first", Name = "First", TargetPath = firstPath,
                Category = "开发工具", IsFavorite = true, UninstallString = "fixture"
            };
            AppInfo second = new AppInfo
            {
                Key = "menu-order:second", Name = "Second", TargetPath = secondPath, Category = "开发工具"
            };
            softwareConfig.Favorites.Add(first.Key);
            softwareConfig.CustomCategories.Add("我的工具");
            CollectionOps.AddMember(softwareConfig, first.Key, "我的工具");
            using (MainForm form = CategoryForm(softwareConfig, first, second))
            {
                typeof(MainForm).GetField("contentMode", Private).SetValue(form,
                    Controls.SidebarControl.ContentMode.Software);
                List<MenuWindow.MenuItem> single = (List<MenuWindow.MenuItem>)
                    Invoke(form, "BuildContextMenu", first);
                CheckMenuOrder(single, "打开", "复制名称", "复制路径", "隐藏", "从常用移除",
                    "添加到默认分类", "添加到自定义", "从「开发工具」移除", "从自定义移除",
                    "卸载", "属性…");

                List<AppInfo> targets = new List<AppInfo> { first, second };
                List<MenuWindow.MenuItem> batch = (List<MenuWindow.MenuItem>)
                    Invoke(form, "BuildContextMenu", first, targets);
                CheckMenuOrder(batch, "已选择 2 个项目", "打开", "复制名称", "复制路径", "隐藏",
                    "添加到常用", "添加到默认分类", "添加到自定义", "从「开发工具」移除", "从自定义移除");

                typeof(MainForm).GetField("currentCategory", Private).SetValue(form, "全部软件");
                List<MenuWindow.MenuItem> readOnly = (List<MenuWindow.MenuItem>)
                    Invoke(form, "BuildContextMenu", first, targets);
                CheckMenuOrder(readOnly, "已选择 2 个项目", "打开", "复制名称", "复制路径", "添加到默认分类");
            }

            AppConfig resourceConfig = RenameConfig("menu-order-resource");
            AppInfo photo1 = AddEntry(resourceConfig, "照片",
                Path.Combine(resourceConfig.ResourceStorePath, "照片", "order-1.jpg"), false, true, "order-1");
            AppInfo photo2 = AddEntry(resourceConfig, "照片",
                Path.Combine(resourceConfig.ResourceStorePath, "照片", "order-2.jpg"), false, true, "order-2");
            using (MainForm form = ResourceForm(resourceConfig, "照片", photo1, photo2))
            {
                List<MenuWindow.MenuItem> single = (List<MenuWindow.MenuItem>)
                    Invoke(form, "BuildContextMenu", photo1);
                CheckMenuOrder(single, "打开文件", "复制名称", "复制路径", "隐藏", "移动到分类",
                    "添加到集合", "从「照片」移除", "删除", "属性…");

                List<AppInfo> targets = new List<AppInfo> { photo1, photo2 };
                List<MenuWindow.MenuItem> batch = (List<MenuWindow.MenuItem>)
                    Invoke(form, "BuildContextMenu", photo1, targets);
                CheckMenuOrder(batch, "已选择 2 个项目", "打开", "复制名称", "复制路径", "隐藏",
                    "移动到分类", "添加到集合", "从「照片」移除", "删除");
            }
        }

        private static void CheckMenuOrder(List<MenuWindow.MenuItem> items, params string[] texts)
        {
            int previous = -1;
            foreach (string text in texts)
            {
                int current = items.FindIndex(delegate(MenuWindow.MenuItem item)
                {
                    return item != null && item.Text == text;
                });
                Check(current >= 0, "menu item missing: " + text);
                Check(current > previous, "menu order is wrong after: " + text);
                previous = current;
            }
        }

        private static void PropertiesMetadata()
        {
            string photo = Path.Combine(root, "property-metadata.png");
            using (Bitmap bitmap = new Bitmap(3, 2)) bitmap.Save(photo, System.Drawing.Imaging.ImageFormat.Png);
            string video = Path.Combine(root, "property-metadata.3gp");
            File.WriteAllBytes(video, new byte[0]);
            AppInfo[] apps = {
                new AppInfo { TargetPath = photo, IsFile = true },
                new AppInfo { TargetPath = video, IsFile = true },
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
                    if (app.TargetPath == video)
                        Check(rows.Exists(delegate(string[] row) { return row[0] == "类型" && row[1] == "视频 · 3GP"; }), "3gp type row was not recognised as video");
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
                    List<MenuWindow.MenuItem> defaults = FindMenu(batch, "添加到默认分类").SubItems;
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

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int command);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        /// <summary>
        /// 只在本测试进程的顶层窗口里找系统对话框（#32770），title 为 null 时匹配任意标题。
        /// FindWindow 是全系统范围：会命中其它程序的隐藏对话框，兜底时把 WM_CLOSE 发给别的进程，
        /// 自己弹出的对话框反而没人关，测试就永久卡住。
        /// </summary>
        private static IntPtr FindOwnDialog(string title)
        {
            uint self = GetCurrentProcessId();
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
            {
                uint owner;
                GetWindowThreadProcessId(hwnd, out owner);
                if (owner != self) return true;
                System.Text.StringBuilder name = new System.Text.StringBuilder(16);
                GetClassName(hwnd, name, name.Capacity);
                if (name.ToString() != "#32770") return true;
                if (title != null)
                {
                    System.Text.StringBuilder text = new System.Text.StringBuilder(256);
                    GetWindowText(hwnd, text, text.Capacity);
                    if (text.ToString() != title) return true;
                }
                found = hwnd;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        private static void StoreDialogSavesOnlyEdits()
        {
            MethodInfo apply = typeof(MainForm).GetMethod("ApplyStoreEdits", BindingFlags.Static | BindingFlags.NonPublic, null,
                new Type[] { typeof(AppConfig), typeof(string), typeof(string).MakeByRefType() }, null);
            AppConfig cfg = new AppConfig { FolderStorePath = "D:\\Old\\Folders", FileStorePath = "D:\\Custom\\Files\\", ResourceStorePath = "" };
            // 打开后什么都不改：不能把默认路径写进配置
            object[] args = { cfg, "", null };
            Check(!(bool)apply.Invoke(null, args) && args[2] == null && cfg.ResourceStorePath == "", "untouched dialog reported a change");
            args = new object[] { cfg, "  E:\\Res\\ ", null };
            Check((bool)apply.Invoke(null, args) && cfg.ResourceStorePath == "E:\\Res", "edited path was not saved");
            // 规范化后相同：不重写已有值
            args = new object[] { cfg, "E:\\Res\\", null };
            Check(!(bool)apply.Invoke(null, args) && cfg.ResourceStorePath == "E:\\Res", "equivalent path reported a change");
            // 相对路径被拒绝，配置不变
            args = new object[] { cfg, "relative\\dir", null };
            Check(!(bool)apply.Invoke(null, args) && args[2] != null && cfg.ResourceStorePath == "E:\\Res", "relative path was accepted");
            // 清空 = 恢复默认
            args = new object[] { cfg, "", null };
            Check((bool)apply.Invoke(null, args) && cfg.ResourceStorePath == "", "clearing did not restore the default");
            // 旧版文件/文件夹位置不再经由对话框修改
            Check(cfg.FolderStorePath == "D:\\Old\\Folders" && cfg.FileStorePath == "D:\\Custom\\Files\\", "dialog changed the legacy store paths");
        }

        private static void ResourceStoreLocationsPool()
        {
            string dir = Path.Combine(root, "store-locations");
            Directory.CreateDirectory(dir);
            string first = Path.Combine(dir, "first"), second = Path.Combine(dir, "second"),
                third = Path.Combine(dir, "third"), fallback = Path.Combine(dir, "fallback");

            AppConfig legacy = new AppConfig { ResourceStorePath = first };
            List<ResourceStoreLocation> editable = ResourceStore.EditableLocations(legacy);
            Check(editable.Count == 1 && editable[0].Enabled && SameText(editable[0].Path, first),
                "legacy single path did not become the first location");
            Check(ResourceStore.ActiveRoots(legacy, fallback).SequenceEqual(new[] { first }, StringComparer.OrdinalIgnoreCase),
                "legacy path was not used as the active root");

            string legacyXml = Path.Combine(dir, "legacy.xml");
            Check(legacy.SaveTo(legacyXml), "legacy config save failed");
            AppConfig legacyLoaded = AppConfig.LoadFrom(legacyXml);
            List<ResourceStoreLocation> legacyEditable = ResourceStore.EditableLocations(legacyLoaded);
            Check(legacyLoaded != null && legacyLoaded.ResourceStoreLocations.Count == 0
                && SameText(legacyLoaded.ResourceStorePath, first)
                && legacyEditable.Count == 1 && legacyEditable[0].Enabled
                && SameText(legacyEditable[0].Path, first),
                "legacy single path did not stay compatible with the location pool");

            AppConfig pool = new AppConfig { ResourceStorePath = first };
            pool.ResourceStoreLocations.Add(new ResourceStoreLocation(first, true));
            pool.ResourceStoreLocations.Add(new ResourceStoreLocation(second, false));
            pool.ResourceStoreLocations.Add(new ResourceStoreLocation(third, true));
            Check(ResourceStore.ActiveRoots(pool, fallback).SequenceEqual(new[] { first, third }, StringComparer.OrdinalIgnoreCase),
                "enabled locations were not returned in order");
            Check(ResourceStore.KnownRoots(pool, fallback).SequenceEqual(new[] { first, second, third }, StringComparer.OrdinalIgnoreCase),
                "disabled locations were lost from the known roots");
            Check(SameText(ResourceStore.PrimaryRoot(pool, fallback), first), "primary root was not the first enabled location");

            string poolXml = Path.Combine(dir, "pool.xml");
            Check(pool.SaveTo(poolXml), "multi-location config save failed");
            AppConfig poolLoaded = AppConfig.LoadFrom(poolXml);
            Check(poolLoaded != null && poolLoaded.ResourceStoreLocations.Count == 3
                && SameText(poolLoaded.ResourceStorePath, first)
                && SameText(poolLoaded.ResourceStoreLocations[1].Path, second)
                && !poolLoaded.ResourceStoreLocations[1].Enabled
                && SameText(poolLoaded.ResourceStoreLocations[2].Path, third),
                "location order or enabled flags did not round-trip");

            pool.ResourceStoreLocations[0].Enabled = false;
            pool.ResourceStoreLocations[2].Enabled = false;
            Check(ResourceStore.ActiveRoots(pool, fallback).Count == 0, "all-disabled pool still accepted new files");
            Check(ResourceStore.KnownRoots(pool, fallback).Count == 3, "all-disabled pool forgot its known roots");
            string disabledXml = Path.Combine(dir, "disabled.xml");
            Check(pool.SaveTo(disabledXml), "all-disabled config save failed");
            AppConfig disabledLoaded = AppConfig.LoadFrom(disabledXml);
            Check(disabledLoaded != null && disabledLoaded.ResourceStoreLocations.Count == 3
                && disabledLoaded.ResourceStoreLocations.All(l => !l.Enabled)
                && string.IsNullOrEmpty(disabledLoaded.ResourceStorePath),
                "all-disabled pool did not keep locations while clearing the legacy field");

            MethodInfo applyList = typeof(MainForm).GetMethod("ApplyStoreEdits", BindingFlags.Static | BindingFlags.NonPublic, null,
                new Type[] { typeof(AppConfig), typeof(List<ResourceStoreLocation>), typeof(string).MakeByRefType() }, null);
            AppConfig edit = new AppConfig();
            object[] args = { edit, new List<ResourceStoreLocation> {
                new ResourceStoreLocation(first, true), new ResourceStoreLocation(first, false) }, null };
            Check(!(bool)applyList.Invoke(null, args) && args[2] != null && ((string)args[2]).Contains("重复"),
                "duplicate locations were accepted");
            args = new object[] { edit, new List<ResourceStoreLocation> {
                new ResourceStoreLocation(first, true), new ResourceStoreLocation(Path.Combine(first, "nested"), true) }, null };
            Check(!(bool)applyList.Invoke(null, args) && args[2] != null && ((string)args[2]).Contains("包含"),
                "nested locations were accepted");
            args = new object[] { edit, new List<ResourceStoreLocation> { new ResourceStoreLocation(first, false) }, null };
            Check((bool)applyList.Invoke(null, args) && edit.ResourceStoreLocations.Count == 1
                && !edit.ResourceStoreLocations[0].Enabled && edit.ResourceStorePath == "",
                "all-disabled edit did not clear the legacy path");
        }

        private static bool SameText(string a, string b)
        {
            return string.Equals((a ?? string.Empty).TrimEnd('\\', '/'),
                (b ?? string.Empty).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        // 启动后恢复成暂停在上次位置（不创建播放内核）；退出时把之后的调整写回。
        // 覆盖真实路径：视频停掉音乐后退出、读取期间已在放别的、状态文件读取出错。状态文件放在临时数据目录。
        private static void MusicStateAcrossRestart()
        {
            string stateFile = MusicResumeState.FilePath;
            string dir = Path.Combine(root, "music-restart");
            Directory.CreateDirectory(dir);
            string first = Path.Combine(dir, "first.mp3"), second = Path.Combine(dir, "second.mp3"), other = Path.Combine(dir, "other.mp3");
            foreach (string song in new[] { first, second, other }) File.WriteAllBytes(song, new byte[16]);
            FieldInfo restored = typeof(MainForm).GetField("musicStateRestored", Private);
            try
            {
                MusicResumeState saved = new MusicResumeState { Mode = Controls.PlaybackMode.RepeatOne, Volume = 22, Muted = true,
                    Index = 1, Position = 61.5, Duration = 200, CollectionKey = "col:音乐/回归" };
                saved.Tracks.Add(first);
                saved.Tracks.Add(second);
                saved.Save(stateFile);

                // 一、恢复后调整，再被视频停掉音乐（StopMusicPlayer），然后正常关闭窗口
                using (MainForm form = MusicStateForm())
                {
                    PumpUntil(delegate { return (bool)restored.GetValue(form); }, 10000);
                    Check((bool)restored.GetValue(form), "saved music state was never read");
                    Controls.MusicPlaybackSession session = MusicSessionOf(form);
                    Controls.MiniMusicPlayerControl mini = (Controls.MiniMusicPlayerControl)typeof(MainForm).GetField("miniMusic", Private).GetValue(form);
                    Check(session.IsRestorePending && session.Index == 1 && session.CurrentPath == second && session.Position == 61.5,
                        "track/position not restored: " + session.Index + " @ " + session.Position);
                    Check(session.Mode == Controls.PlaybackMode.RepeatOne && session.Volume == 22 && session.Muted, "mode/volume/mute not restored");
                    Check(!session.IsPlaying && session.Engine == null, "restore started playback or loaded the engine");
                    Check(mini.CollectionKey == "col:音乐/回归", "mini player collection not restored");
                    session.Seek(90);
                    session.SetMode(Controls.PlaybackMode.Shuffle);
                    Invoke(form, "StopMusicPlayer");
                    Check(!session.HasTracks, "fixture: stopping did not clear the session");
                    session.SetVolume(45);   // 停掉之后再调的音量也要记住
                    form.Close();
                }
                MusicResumeState after = MusicResumeState.Load(stateFile);
                Check(after != null && after.Tracks.Count == 2 && after.Index == 1 && after.Position == 90 && after.Duration == 200,
                    "music stopped by video was not remembered on exit");
                Check(after.Volume == 45 && !after.Muted && after.Mode == Controls.PlaybackMode.Shuffle
                    && after.CollectionKey == "col:音乐/回归", "exit did not save mode/volume/collection");
                Check(!File.Exists(stateFile + ".tmp"), "exit left a temporary state file");

                // 二、读取结果到达时用户已经在放别的：列表以用户为准，音量/顺序这些全局设置仍然恢复
                using (MainForm form = MusicStateForm())
                {
                    PumpUntil(delegate { return (bool)restored.GetValue(form); }, 10000);
                    Controls.MusicPlaybackSession session = MusicSessionOf(form);
                    session.Stop();
                    session.SetVolume(80);
                    session.SetMode(Controls.PlaybackMode.Sequential);
                    Check(session.Restore(new List<string> { other }, 0, 3, 100), "fixture could not start another list");
                    Invoke(form, "RestoreMusicState");
                    PumpUntil(delegate { return session.Volume == 45; }, 10000);
                    Check(session.Volume == 45 && session.Mode == Controls.PlaybackMode.Shuffle, "global settings were not restored while playing");
                    Check(session.PathCount == 1 && session.CurrentPath == other && session.Position == 3, "late restore replaced the user's list");
                    form.Close();
                }
                after = MusicResumeState.Load(stateFile);
                Check(after.Tracks.Count == 1 && after.Tracks[0] == other && after.Position == 3, "exit did not save the user's own list");

                // 三、状态文件读取出错：本次没放音乐就退出，不得覆盖原文件
                string unreadable = "<music version=\"9\" volume=\"7\"/>";
                File.WriteAllText(stateFile, unreadable);
                long logBefore = File.Exists(Paths.LogFile) ? new FileInfo(Paths.LogFile).Length : 0;
                using (MainForm form = MusicStateForm())
                {
                    PumpUntil(delegate
                    {
                        return File.Exists(Paths.LogFile) && new FileInfo(Paths.LogFile).Length > logBefore
                            && File.ReadAllText(Paths.LogFile).Contains("音乐播放状态版本不受支持");
                    }, 10000);
                    Application.DoEvents();
                    Check(File.ReadAllText(Paths.LogFile).Contains("音乐播放状态版本不受支持"), "fixture: unreadable state was never attempted");
                    Check(!(bool)restored.GetValue(form), "failed read was treated as a completed restore");
                    form.Close();
                }
                Check(File.ReadAllText(stateFile) == unreadable, "exit overwrote a state file that could not be read");
            }
            finally
            {
                if (File.Exists(stateFile)) File.Delete(stateFile);
            }
        }

        private static MainForm MusicStateForm()
        {
            MainForm form = new MainForm(new AppConfig());
            typeof(MainForm).GetField("scanRunning", Private).SetValue(form, true);
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-3000, -3000);
            form.Size = new Size(1200, 700);
            form.Show();
            return form;
        }

        private static Controls.MusicPlaybackSession MusicSessionOf(MainForm form)
        {
            return (Controls.MusicPlaybackSession)typeof(MainForm).GetField("musicSession", Private).GetValue(form);
        }

        private static void DisposedFormMessageFilter()
        {
            // 显示后不经 Close 直接释放：OnFormClosed 不会执行，过滤器必须在释放时注销，
            // 否则之后任何消息循环都会访问已释放窗体的 Handle 并抛 ObjectDisposedException。
            NotifyIcon tray;
            using (MainForm form = new MainForm(new AppConfig()))
            {
                typeof(MainForm).GetField("scanRunning", Private).SetValue(form, true);
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-3000, -3000);
                form.Size = new Size(900, 600);
                form.Show();
                Application.DoEvents();
                tray = (NotifyIcon)typeof(MainForm).GetField("tray", Private).GetValue(form);
                Check(tray != null && tray.Visible, "fixture did not create the tray icon");
            }
            // 托盘图标同样只在 OnFormClosed 里移除：释放时不撤掉，进程退出后通知区会残留无主图标
            Check(!tray.Visible, "tray icon survived dispose without Close");
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
                Check((bool)Invoke(form, "InCategory", file, "全部软件"), "software-side entry disappeared from 全部软件");
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
            Check(path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "desktop fixture is outside the temp root: " + path);
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

        private static void AutoStashReusesMatchingBackup()
        {
            AppConfig config = new AppConfig();
            MethodInfo create = typeof(DesktopOrganizer).GetMethod("CreateShortcut", BindingFlags.Static | BindingFlags.NonPublic);
            string name = "UpdateTool-" + Guid.NewGuid().ToString("N") + ".lnk";
            string targetA = Path.Combine(root, "update-a.exe"), targetB = Path.Combine(root, "update-b.exe");
            File.WriteAllText(targetA, "a"); File.WriteAllText(targetB, "b");
            string[] sources = new string[3];
            for (int i = 0; i < sources.Length; i++)
            {
                string dir = Path.Combine(root, "update-source-" + i);
                Directory.CreateDirectory(dir);
                sources[i] = Path.Combine(dir, name);
            }
            create.Invoke(null, new object[] { sources[0], targetA, "regression" });
            create.Invoke(null, new object[] { sources[1], targetA, "regression" });
            create.Invoke(null, new object[] { sources[2], targetB, "regression" });
            string backup = Path.Combine(Paths.DesktopBackupDir, name);
            string suffixed = Path.Combine(Paths.DesktopBackupDir, Path.GetFileNameWithoutExtension(name) + "_1.lnk");
            int failed;
            Check(DesktopOrganizer.AutoStashShortcuts(config, new List<string> { sources[0] }, out failed) == 1 && failed == 0,
                "first matching backup fixture was not stashed");
            Check(DesktopOrganizer.AutoStashShortcuts(config, new List<string> { sources[1] }, out failed) == 1 && failed == 0,
                "same target shortcut was not accepted");
            Check(File.Exists(backup) && !File.Exists(suffixed), "same target created a suffixed backup");
            Check(!File.Exists(sources[1]) && config.DesktopBackup.Count == 1, "same target left a duplicate source or record");

            string lockedFirst = Path.Combine(root, "locked-first.lnk");
            string lockedSecondDir = Path.Combine(root, "locked-second");
            Directory.CreateDirectory(lockedSecondDir);
            string lockedSecond = Path.Combine(lockedSecondDir, "locked-first.lnk");
            create.Invoke(null, new object[] { lockedFirst, targetA, "regression" });
            create.Invoke(null, new object[] { lockedSecond, targetA, "regression" });
            Check(DesktopOrganizer.AutoStashShortcuts(config, new List<string> { lockedFirst }, out failed) == 1 && failed == 0,
                "locked-source fixture was not stashed");
            string lockedBackup = Path.Combine(Paths.DesktopBackupDir, "locked-first.lnk");
            DesktopBackupEntry lockedEntry = config.DesktopBackup.Single(entry => entry.CurrentPath == lockedBackup);
            using (new FileStream(lockedSecond, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Check(DesktopOrganizer.AutoStashShortcuts(config, new List<string> { lockedSecond }, out failed) == 1 && failed == 0,
                    "locked source was not copied into the existing backup");
                Check(File.Exists(lockedSecond) && lockedEntry.IsCopy,
                    "reused backup did not record that the source still exists: source=" + File.Exists(lockedSecond)
                    + " copy=" + lockedEntry.IsCopy + " records=" + config.DesktopBackup.Count
                    + " backups=" + string.Join(" | ", Directory.GetFiles(Paths.DesktopBackupDir, "locked-*.lnk")));
            }

            int recordsBeforeDifferentTarget = config.DesktopBackup.Count;
            Check(DesktopOrganizer.AutoStashShortcuts(config, new List<string> { sources[2] }, out failed) == 1 && failed == 0,
                "different target shortcut was not stashed");
            Check(File.Exists(suffixed) && config.DesktopBackup.Count == recordsBeforeDifferentTarget + 1,
                "different targets did not keep distinct backups");
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

        private static void ResourceCreateFallsBackToNextStore()
        {
            string dir = Path.Combine(root, "resource-create-fallback");
            Directory.CreateDirectory(dir);
            string blocked = Path.Combine(dir, "blocked");
            string usable = Path.Combine(dir, "usable");
            File.WriteAllText(blocked, "not a directory");
            Directory.CreateDirectory(usable);

            AppConfig config = new AppConfig
            {
                ManageRootPath = Path.Combine(dir, "manage"),
                ResourceStorePath = blocked
            };
            config.ResourceStoreLocations.Add(new ResourceStoreLocation(blocked, true));
            config.ResourceStoreLocations.Add(new ResourceStoreLocation(usable, true));
            config.ResourceCategories.Add("资料");
            using (MainForm form = ResourceForm(config, "资料"))
            {
                Check(form.Handle != IntPtr.Zero, "resource form handle not created");
                Invoke(form, "CreateManagedEntry", false);
                Invoke(form, "CreateManagedEntry", true);
            }

            string expected = Path.Combine(usable, "资料");
            List<AddedEntry> created = config.AddedItems.Where(e => string.Equals(Path.GetDirectoryName(e.Path), expected,
                StringComparison.OrdinalIgnoreCase)).ToList();
            Check(created.Count == 2 && created.All(e => e.IsResource),
                "resource creation did not fail over to the next enabled location");
            Check(!Directory.Exists(Path.Combine(blocked, "资料")), "blocked location was used for new entries");
        }

        private static void SoftwareFilesMigrate()
        {
            AppConfig config = new AppConfig();
            config.CustomCategories.Add("我的工具");
            config.ResourceCategories.Add("旅行资料");
            Func<string, string> p = delegate(string name) { return "C:\\SoftwareMigrate\\" + name; };
            config.AddedItems.Add(new AddedEntry { Path = p("tool.exe"), Category = "开发工具" });
            config.AddedItems.Add(new AddedEntry { Path = p("photo.JPG"), Category = "开发工具" });
            config.AddedItems.Add(new AddedEntry { Path = p("notes.txt"), Category = "快捷中心" });
            config.AddedItems.Add(new AddedEntry { Path = p("Project"), Category = "我的工具", IsFolder = true });
            config.AddedItems.Add(new AddedEntry { Path = p("data.bin"), Category = "其他" });
            config.AddedItems.Add(new AddedEntry { Path = p("odd.exe"), Category = "其他", IsFolder = true });   // 名字像程序的文件夹
            config.AddedItems.Add(new AddedEntry { Path = p("clip.mp4"), Category = "开发工具" });
            config.AddedItems.Add(new AddedEntry { Path = p("clip.mp4"), Category = "我的工具" });
            config.AddedItems.Add(new AddedEntry { Path = p("both.pdf"), Category = "开发工具" });
            config.AddedItems.Add(new AddedEntry { Path = p("both.pdf"), Category = "旅行资料", IsResource = true });
            config.AddedItems.Add(new AddedEntry { Path = p("Game.url"), Category = "其他" });     // 启动文件按软件保留
            config.AddedItems.Add(new AddedEntry { Path = p("App.LNK"), Category = "开发工具" });
            string photo = "add:" + p("photo.JPG"), notes = "add:" + p("notes.txt"), tool = "add:" + p("tool.exe"), both = "add:" + p("both.pdf");
            config.Favorites.Add(photo);
            config.CategoryOverride[photo] = "开发工具";
            config.CategoryOverride[notes] = "文档";   // 资源侧的分类覆盖保留
            config.ItemCategories[photo] = new List<string> { "我的工具", "col:照片/旅行" };
            config.ExcludedCategories[photo] = new List<string> { "其他", "照片" };
            config.ExcludedCategories[both] = new List<string> { "开发工具", "旅行资料" };   // 用户在资源页面设的排除不能被撤销
            config.ItemOrder["开发工具"] = new List<string> { tool, "add:" + p("PHOTO.jpg") };   // 关系键与收录路径大小写不同
            config.ItemOrder["我的工具"] = new List<string> { "add:" + p("Project") };
            config.ItemOrder["照片"] = new List<string> { photo };
            config.ItemOrder["常用"] = new List<string> { tool, photo };   // 两个页面共用的常用排序不动

            List<string[]> migrated = config.MigrateSoftwareFilesToResources();
            Func<string, string> resourceOf = delegate(string name)
            {
                List<AddedEntry> rows = config.AddedItems.ByPath(p(name));
                return rows.Count == 1 && rows[0].IsResource ? rows[0].Category : null;
            };
            Check(migrated.Count == 7, "migrated count " + migrated.Count);
            List<string> software = config.AddedItems.SoftwareEntries().Select(e => Path.GetFileName(e.Path)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Check(software.SequenceEqual(new[] { "App.LNK", "Game.url", "tool.exe" }), "launchers left the software page or files stayed: " + string.Join("|", software.ToArray()));
            Check(resourceOf("photo.JPG") == "照片" && resourceOf("notes.txt") == "文档" && resourceOf("clip.mp4") == "视频",
                "typed files went to the wrong resource category");
            Check(resourceOf("Project") == "文件" && resourceOf("data.bin") == "文件" && resourceOf("odd.exe") == "文件", "folders and other files not sent to 文件");
            Check(resourceOf("both.pdf") == "旅行资料" && config.AddedItems.Count == 10, "existing resource row replaced or duplicate rows kept");
            Check(!config.CategoryOverride.ContainsKey(photo) && config.CategoryOverride[notes] == "文档", "software override kept or resource override lost");
            Check(config.ItemCategories[photo].SequenceEqual(new[] { "col:照片/旅行" }), "software membership kept or collection lost");
            Check(!config.ExcludedCategories.ContainsKey(photo), "exclusion from a software category or the new target category kept");
            Check(config.ExcludedCategories[both].SequenceEqual(new[] { "旅行资料" }), "resource-page exclusion revoked or software exclusion kept");
            Check(config.ItemOrder["开发工具"].SequenceEqual(new[] { tool }) && !config.ItemOrder.ContainsKey("我的工具"), "software order kept migrated entries");
            Check(config.ItemOrder["照片"].SequenceEqual(new[] { photo }) && config.Favorites.Contains(photo), "resource order or favorite lost");
            Check(config.ItemOrder["常用"].SequenceEqual(new[] { tool, photo }), "shared 常用 order changed");
            Check(config.MigrateSoftwareFilesToResources().Count == 0 && config.AddedItems.Count == 10, "migration is not idempotent");
        }

        private static void SoftwareScanTargets()
        {
            Func<AppInfo, bool> keep = Scanner.IsSoftwareTarget;
            Check(keep(new AppInfo { TargetPath = "C:\\Apps\\tool.exe" }) && keep(new AppInfo { IsUwp = true, IsFile = true }), "software dropped");
            foreach (string program in new[] { "C:\\Windows\\System32\\compmgmt.msc", "C:\\Tools\\start.BAT", "C:\\Start\\App.appref-ms", "C:\\Start\\Game.url",
                "C:\\Tools\\hotkeys.ahk", "C:\\Tools\\task.vbs", "C:\\Tools\\tool.jar", "steam://rungameid/730" })
                Check(keep(new AppInfo { TargetPath = program, IsFile = true }), "program entry dropped: " + program);
            // 启动文件（手动登记里按软件保留、不当普通文件改名删除）：只认 exe/lnk/url/appref-ms，非法字符不抛异常
            Check(Scanner.IsLauncherPath("C:\\A\\b.EXE") && Scanner.IsLauncherPath("C:\\A\\b.lnk") && Scanner.IsLauncherPath("C:\\A|b.url")
                && !Scanner.IsLauncherPath("C:\\A\\run.bat") && !Scanner.IsLauncherPath("C:\\A.lnk\\file") && !Scanner.IsLauncherPath(null),
                "launcher path rule wrong");
            foreach (string document in new[] { "C:\\Docs\\readme.txt", "C:\\Docs\\report.PDF", "C:\\Docs\\a.b\\noext", "" })
                Check(!keep(new AppInfo { TargetPath = document, IsFile = true }), "document entry kept: " + document);
            Check(!keep(new AppInfo { TargetPath = "C:\\Projects\\tool.exe", IsFile = true, IsFolder = true }) && !keep(null), "folder entry kept");

            // 真实快捷方式走扫描入口：文件夹、文档快捷方式不收录，程序快捷方式照常收录
            string dir = Path.Combine(root, "scan-targets");
            Directory.CreateDirectory(dir);
            string doc = Path.Combine(dir, "readme.txt");
            File.WriteAllText(doc, "doc");
            string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
            MethodInfo create = typeof(DesktopOrganizer).GetMethod("CreateShortcut", BindingFlags.Static | BindingFlags.NonPublic);
            string[][] links = { new[] { "Folder Link", dir }, new[] { "Doc Link", doc }, new[] { "App Link", notepad } };
            Scanner scanner = new Scanner(new AppConfig());
            typeof(Scanner).GetField("nameIndex", Private).SetValue(scanner, new Dictionary<string, AppInfo>(StringComparer.Ordinal));
            Dictionary<string, AppInfo> dict = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
            MethodInfo add = typeof(Scanner).GetMethod("AddShortcut", Private);
            foreach (string[] link in links)
            {
                string file = Path.Combine(dir, link[0] + ".lnk");
                create.Invoke(null, new object[] { file, link[1], "regression" });
                add.Invoke(scanner, new object[] { dict, file, "Desktop", false });
            }
            Check(dict.Values.Select(a => a.Name).SequenceEqual(new[] { "App Link" }),
                "scanned shortcuts: " + string.Join("|", dict.Values.Select(a => a.Name).ToArray()));
        }

        private static void StashOnlySoftwareShortcuts()
        {
            MethodInfo create = typeof(DesktopOrganizer).GetMethod("CreateShortcut", BindingFlags.Static | BindingFlags.NonPublic);
            string desk = Path.Combine(root, "stash-only-software");
            string project = Path.Combine(desk, "Project"), doc = Path.Combine(desk, "plan.txt");
            Directory.CreateDirectory(project);
            File.WriteAllText(doc, "plan");
            string tag = Guid.NewGuid().ToString("N");
            string folderLink = Path.Combine(desk, "Folder-" + tag + ".lnk"), docLink = Path.Combine(desk, "Doc-" + tag + ".lnk");
            string appLink = Path.Combine(desk, "App-" + tag + ".lnk");
            create.Invoke(null, new object[] { folderLink, project, "regression" });
            create.Invoke(null, new object[] { docLink, doc, "regression" });
            create.Invoke(null, new object[] { appLink, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"), "regression" });
            Check(!DesktopOrganizer.IsSoftwareShortcut(folderLink) && !DesktopOrganizer.IsSoftwareShortcut(docLink)
                && DesktopOrganizer.IsSoftwareShortcut(appLink), "shortcut target classification wrong");
            AppConfig config = new AppConfig();
            string stashed = Path.Combine(Paths.DesktopBackupDir, Path.GetFileName(appLink));
            try
            {
                int failed;
                int moved = DesktopOrganizer.AutoStashShortcuts(config, new List<string> { folderLink, docLink, appLink }, out failed);
                Check(moved == 1 && failed == 0 && File.Exists(stashed) && !File.Exists(appLink), "software shortcut was not stashed");
                Check(File.Exists(folderLink) && File.Exists(docLink), "folder or document shortcut was taken off the desktop");
                Check(config.DesktopBackup.Count == 1 && string.Equals(config.DesktopBackup[0].OriginalPath, appLink, StringComparison.OrdinalIgnoreCase),
                    "stash records include skipped shortcuts");
            }
            finally { try { if (File.Exists(stashed)) File.Delete(stashed); } catch { } }
        }

        private static void ImportRecoveryAfterMigration()
        {
            AppConfig config = RenameConfig("recovery-after-migration");
            string target = Path.Combine(root, "recovery-after-migration", "files", "moved.txt");
            // 旧的软件页面批次：入库路径已随迁移改为资源「文档」记录
            config.AddedItems.Add(new AddedEntry { Path = target, Category = "文档", IsResource = true });
            using (MainForm form = CategoryForm(config))
            {
                MethodInfo registered = typeof(MainForm).GetMethod("IsBatchTargetRegistered", Private);
                Func<bool, string, string, bool> check = delegate(bool resource, string category, string path)
                {
                    return (bool)registered.Invoke(form, new object[] { new ImportBatch { Resource = resource, Category = category }, path });
                };
                Check(check(false, "开发工具", target), "migrated software batch target not recognised; recovery would withdraw a library copy");
                Check(check(true, "文档", target) && !check(true, "照片", target), "resource batch matching changed");
                Check(!check(false, "开发工具", target + ".missing"), "unregistered target treated as registered");
            }
        }

        private static ImportBatch RunMusicScanImport(MainForm form, AppConfig config, string source)
        {
            MethodInfo registered = typeof(MainForm).GetMethod("RegisteredPathsInMemory", Private);
            Func<List<string>, Dictionary<string, string>> lookup = delegate(List<string> paths)
            { return (Dictionary<string, string>)registered.Invoke(form, new object[] { paths, true }); };
            MethodInfo prepare = typeof(MainForm).GetMethod("PrepareImportBatchMultiStore", Private);
            ImportBatch batch = (ImportBatch)prepare.Invoke(form, new object[] { new[] { source }, ImportOperation.Copy, "音乐", null, true, (uint)0,
                config.ResourceStorePath, Path.Combine(config.ResourceStorePath, "音乐"), lookup,
                new Func<string, bool>(delegate(string folder) { return config.AddedItems.EntriesUnder(folder).Count > 0; }),
                new List<string> { config.ResourceStorePath } });
            batch.JournalDirectory = Path.Combine(root, "music-scan-origin-journals");
            batch.PreserveCategoryExclusions = true;
            batch.ResolveTarget = delegate(ImportItem item)
            {
                string name = Path.GetFileName(item.Source);
                string target = Path.Combine(config.ResourceStorePath, "音乐", name);
                int suffix = 1;
                while (File.Exists(target)) target = Path.Combine(config.ResourceStorePath, "音乐",
                    Path.GetFileNameWithoutExtension(name) + "_" + suffix++ + Path.GetExtension(name));
                return target;
            };
            FileImport.Run(batch, delegate(ImportBatch value) { return (bool)Invoke(form, "CommitImport", value); }, null);
            return batch;
        }

        private static void MusicScanReusesCopiedOrigins()
        {
            AppConfig config = RenameConfig("music-scan-origin");
            using (MainForm form = ResourceForm(config, "音乐"))
            {
                string source = Path.Combine(root, "music-scan-origin-source", "song.mp3");
                Directory.CreateDirectory(Path.GetDirectoryName(source));
                File.WriteAllText(source, "music");

                ImportBatch first = RunMusicScanImport(form, config, source);
                Check(first.Items[0].State == FileImport.Copied && File.Exists(first.Items[0].Target) && File.Exists(source),
                    "initial music scan did not preserve and copy the source");
                string target = first.Items[0].Target;
                AddedEntry entry = config.AddedItems.ByPath(target).Single();
                Check(entry.OriginPath == source && config.AddedItems.Count == 1, "copied origin was not registered");

                ImportBatch repeated = RunMusicScanImport(form, config, source);
                Check(repeated.Items[0].State == FileImport.Linked && repeated.Items[0].Target == target,
                    "repeat scan copied the same source again");
                Check(config.AddedItems.Count == 1 && !File.Exists(Path.Combine(config.ResourceStorePath, "音乐", "song_1.mp3")),
                    "repeat scan created a duplicate registration or file");

                string renamed = Path.Combine(config.ResourceStorePath, "音乐", "renamed.mp3");
                File.Move(target, renamed); entry.Path = renamed;
                Check(config.Save(), config.DatabaseError);
                ImportBatch afterRename = RunMusicScanImport(form, config, source);
                Check(afterRename.Items[0].State == FileImport.Linked && afterRename.Items[0].Target == renamed,
                    "renaming the registered target broke origin deduplication");

                entry = config.AddedItems.ByPath(renamed).Single(); entry.Category = "文档";
                config.ExcludedCategories["add:" + renamed] = new List<string> { "音乐" };
                Check(config.Save(), config.DatabaseError);
                ImportBatch otherCategory = RunMusicScanImport(form, config, source);
                Check(otherCategory.Items[0].State == FileImport.Linked && otherCategory.Items[0].Target == renamed,
                    "origin in another category was copied again");
                Check(config.ItemCategories["add:" + renamed].Contains("音乐"),
                    "music membership was not added to the existing resource");
                Check(config.ExcludedCategories["add:" + renamed].Contains("音乐"),
                    "music scan restored a user exclusion");
                Check(config.AddedItems.Count == 1 && File.Exists(source), "origin deduplication changed source or entry count");
            }

            AppConfig memory = new AppConfig();
            memory.AddedItems.Add(new AddedEntry { Path = "D:\\library\\song.mp3", Category = "音乐", IsResource = true,
                OriginPath = "C:\\source\\song.mp3" });
            using (MainForm form = new MainForm(memory))
            {
                Dictionary<string, string> found = (Dictionary<string, string>)Invoke(form, "RegisteredPathsInMemory",
                    new List<string> { "c:\\SOURCE\\SONG.MP3" }, true);
                Check(found["c:\\SOURCE\\SONG.MP3"] == "D:\\library\\song.mp3", "in-memory origin lookup failed");
            }
        }

        private static void QueuedMemoryImportsUseFreshSnapshot()
        {
            string dir = Path.Combine(root, "queued-memory-duplicates");
            Directory.CreateDirectory(dir);
            string first = Path.Combine(dir, "first.txt"), second = Path.Combine(dir, "second.txt");
            File.WriteAllText(first, "same queued content");
            File.WriteAllText(second, "same queued content");
            AppConfig config = new AppConfig
            {
                ManageRootPath = Path.Combine(dir, "manage"),
                ResourceStorePath = Path.Combine(dir, "store")
            };
            using (MainForm form = new MainForm(config))
            {
                Invoke(form, "BuildControls");
                IntPtr handle = form.Handle;
                typeof(MainForm).GetField("contentMode", Private).SetValue(form, Controls.SidebarControl.ContentMode.Website);
                typeof(MainForm).GetField("currentCategory", Private).SetValue(form, "文档");
                typeof(MainForm).GetField("scanRunning", Private).SetValue(form, true);
                MethodInfo queue = typeof(MainForm).GetMethod("QueueImport", Private, null, new[]
                {
                    typeof(string[]), typeof(ImportOperation), typeof(string), typeof(string), typeof(bool), typeof(uint),
                    typeof(Action<ImportBatch>), typeof(int), typeof(bool), typeof(bool), typeof(ImportDuplicateDecision), typeof(bool)
                }, null);
                queue.Invoke(form, new object[] { new[] { first }, ImportOperation.Copy, "文档", null, true, (uint)0,
                    null, 0, false, false, null, false });
                queue.Invoke(form, new object[] { new[] { second }, ImportOperation.Copy, "文档", null, true, (uint)0,
                    null, 0, false, false, null, false });
                FieldInfo running = typeof(MainForm).GetField("importRunning", Private);
                PumpUntil(() => !(bool)running.GetValue(form));
                Check(config.AddedItems.Count == 1, "queued duplicate was copied before the first commit became visible");
                Check(File.Exists(first) && File.Exists(second), "copy duplicate check removed a source");
            }
        }

        private static void MissingMusicCleanup()
        {
            string dir = Path.Combine(root, "missing-music-cleanup-files");
            Directory.CreateDirectory(dir);
            string kept = Path.Combine(dir, "kept.mp3");
            string gone = Path.Combine(dir, "gone.mp3");
            string offline = Path.Combine(dir, "offline", "gone.mp3");
            File.WriteAllText(kept, "music");

            List<string> found = MissingResourceCleanup.FindMissingWithReachableParent(
                new string[] { kept, gone, offline, gone });
            Check(found.Count == 1 && string.Equals(found[0], gone, StringComparison.OrdinalIgnoreCase),
                "missing scan did not isolate the reachable missing path");

            AppConfig config = RenameConfig("missing-music-cleanup");
            AppInfo keepEntry = AddEntry(config, "音乐", kept, false, true, "kept");
            AppInfo goneEntry = AddEntry(config, "音乐", gone, false, true, "gone");
            string sharedPath = Path.Combine(dir, "shared.mp3");
            AddEntry(config, "照片", sharedPath, false, true, "shared-photo");
            config.AddedItems.Add(new AddedEntry { Path = sharedPath, Category = "音乐", IsResource = true,
                IsFolder = false, Name = "shared-music" });
            File.Delete(goneEntry.TargetPath);
            File.Delete(sharedPath);
            config.Favorites.Add("add:" + gone);
            config.Favorites.Add("add:" + sharedPath);
            config.Favorites.Add(keepEntry.Key);

            using (MainForm form = ResourceForm(config, "音乐", keepEntry, goneEntry))
            {
                int removed = (int)Invoke(form, "RemoveMissingMusicEntries",
                    new List<string> { gone, sharedPath });
                Check(removed == 2, "missing music cleanup removed the wrong number of memberships");
                Check(config.AddedItems.ByPath(gone).Count == 0, "missing music entry was not removed");
                List<AddedEntry> shared = config.AddedItems.ByPath(sharedPath);
                Check(shared.Count == 1 && shared[0].Category == "照片",
                    "music cleanup removed the same path from another category");
                Check(!config.Favorites.Contains("add:" + gone), "orphaned music favorite was not purged");
                Check(config.Favorites.Contains("add:" + sharedPath), "shared path favorite was purged");
                Check(config.AddedItems.ByPath(kept).Count == 1 && config.Favorites.Contains(keepEntry.Key),
                    "existing music entry or favorite was changed");
            }
        }

        private static void SoftwarePageRefusesFiles()
        {
            AppConfig config = RenameConfig("software-refuses");
            string dir = Path.Combine(root, "software-refuses-source");
            string exe = Path.Combine(dir, "tool.exe"), doc = Path.Combine(dir, "notes.txt"), folder = Path.Combine(dir, "Folder");
            Directory.CreateDirectory(folder);
            File.WriteAllText(exe, "exe");
            File.WriteAllText(doc, "doc");
            using (MainForm form = CategoryForm(config))
            {
                IntPtr handle = form.Handle;
                Check(handle != IntPtr.Zero, "form handle not created");
                int before = config.AddedItems.Count;

                Invoke(form, "RebuildSidebar");
                List<Controls.SidebarControl.SidebarItem> items = (List<Controls.SidebarControl.SidebarItem>)typeof(MainForm).GetField("sidebarItems", Private).GetValue(form);
                Check(items.Any(i => i.Key == "全部软件") && !items.Any(i => i.Key == "全部文件"), "software sidebar still offers 全部文件");

                // 外部拖入：空白区、当前分类与侧栏分类行，经过和松手都拒绝（单独拖 exe 也一样）
                MethodInfo handleDrop = typeof(MainForm).GetMethod("HandleExternalImport", Private);
                Func<string[], string, bool, DragDropEffects> drag = delegate(string[] paths, string category, bool drop)
                {
                    DataObject data = new DataObject();
                    System.Collections.Specialized.StringCollection list = new System.Collections.Specialized.StringCollection();
                    list.AddRange(paths);
                    data.SetFileDropList(list);
                    typeof(MainForm).GetField("dragProbe", Private).SetValue(form, null);
                    return (DragDropEffects)handleDrop.Invoke(form, new object[] { category, null,
                        new DragEventArgs(data, 0, 0, 0, DragDropEffects.Copy | DragDropEffects.Move, DragDropEffects.None), drop });
                };
                foreach (string[] paths in new[] { new[] { exe }, new[] { exe, doc, folder } })
                    foreach (bool drop in new[] { false, true })
                        Check(drag(paths, null, drop) == DragDropEffects.None && drag(paths, "开发工具", drop) == DragDropEffects.None,
                            "software page accepted an external drop (" + paths.Length + " items, drop=" + drop + ")");

                // 新建文件/文件夹：软件页面不创建任何文件
                Invoke(form, "CreateManagedEntry", false);
                Invoke(form, "CreateManagedEntry", true);

                // 导入批次：软件页面只原位登记 exe，文件与文件夹跳过，不复制不移动
                MethodInfo prepare = typeof(MainForm).GetMethod("PrepareImportBatch", Private);
                ImportBatch batch = (ImportBatch)prepare.Invoke(form, new object[] { new[] { exe, doc, folder }, ImportOperation.Move, "开发工具", null, false, (uint)0,
                    Path.Combine(root, "software-refuses", "folders"), Path.Combine(root, "software-refuses", "files"),
                    new Func<List<string>, Dictionary<string, string>>(delegate { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }),
                    new Func<string, bool>(delegate { return false; }) });
                Func<string, ImportItem> item = delegate(string path) { return batch.Items.Single(i => string.Equals(i.Source, path, StringComparison.OrdinalIgnoreCase)); };
                Check(item(exe).Reference && item(exe).State == FileImport.Waiting, "software executable not registered in place");
                Check(item(doc).State == FileImport.Skipped && item(folder).State == FileImport.Skipped, "software page would import a file or folder");

                Check(config.AddedItems.Count == before && File.Exists(doc) && Directory.Exists(folder) && File.Exists(exe), "software page created, moved or registered files");
                Check(!Directory.Exists(config.ManageRootPath) && !Directory.Exists(config.FileStorePath) && !Directory.Exists(config.FolderStorePath)
                    && !Directory.Exists(config.ResourceStorePath), "software page created a storage directory");
            }
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
                // 主列表改用自绘细滚动条：内容溢出时也不再占用客户区宽度
                Check(grid.AutoScrollMinSize.Height > grid.ClientSize.Height, "fixture does not overflow the grid");
                Check(grid.ClientSize.Width == grid.Width, "a system scrollbar still narrows the grid");
                Check(strip.Width == grid.ClientSize.Width, "header strip width " + strip.Width + " differs from grid client width " + grid.ClientSize.Width);
            }
        }

        private static void CollectionSidebarTrayRestore()
        {
            AppConfig config = RenameConfig("collection-sidebar-tray");
            config.AutoScan = false;
            config.CloseToTray = true;
            string collectionKey = CollectionKeys.Make("照片", "旅行");
            AppInfo image = AddEntry(config, "照片", Path.Combine(config.ResourceStorePath, "照片", "trip.png"),
                false, true, "trip");
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "旅行" });
            CollectionOps.AddMember(config, image.Key, collectionKey);
            Check(config.Save(), "database fixture save failed");
            config.Database.PublishCatalog(config, new List<AppInfo> { image });

            using (MainForm form = new MainForm(config))
            {
                typeof(MainForm).GetField("scanRunning", Private).SetValue(form, true);
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-3000, -3000);
                form.ClientSize = new Size(1200, 720);
                form.Show();
                Application.DoEvents();

                typeof(MainForm).GetField("allApps", Private).SetValue(form, new List<AppInfo> { image });
                Invoke(form, "OnModeChanged", Controls.SidebarControl.ContentMode.Website);
                Application.DoEvents();

                Controls.SidebarControl sidebar = (Controls.SidebarControl)typeof(MainForm).GetField("sidebar", Private).GetValue(form);
                Controls.CollectionSidebar collection = (Controls.CollectionSidebar)typeof(MainForm).GetField("collectionSidebar", Private).GetValue(form);
                Controls.AppGridControl grid = (Controls.AppGridControl)typeof(MainForm).GetField("grid", Private).GetValue(form);
                Controls.CollectionInfoPanel info = (Controls.CollectionInfoPanel)typeof(MainForm).GetField("collectionInfo", Private).GetValue(form);
                Control settings = (Control)typeof(MainForm).GetField("settingsPage", Private).GetValue(form);

                // 夹具必须真正进入可编辑资源分类并显示出集合栏，否则后续断言没有意义。
                Check(collection.Visible, "fixture did not show the collection sidebar");
                Check(collection.Width > 0, "fixture collection sidebar has no width");
                Check(collection.Left == sidebar.Width, "fixture collection sidebar is not beside the main sidebar");
                Check(grid.Left == collection.Right, "fixture grid did not start at the collection sidebar's right edge");
                int expandedWidth = collection.Width;

                Invoke(form, "MaximizeOnCurrentScreen");
                Application.DoEvents();
                Check(collection.Width == expandedWidth && grid.Left == collection.Right,
                    "maximized layout lost the collection sidebar alignment");
                Invoke(form, "RestoreFromMaximized");
                Application.DoEvents();
                Check(collection.Width == expandedWidth && grid.Left == collection.Right,
                    "restoring from maximized lost the collection sidebar alignment");

                Invoke(form, "HideToTray");
                Application.DoEvents();
                Check(!form.Visible, "HideToTray did not hide the form");
                // 模拟隐藏期间的自动扫描/封面回调：刷新集合 UI 后再布局。
                Invoke(form, "UpdateCollectionUi");
                Invoke(form, "LayoutControls");
                Application.DoEvents();
                Check(collection.Width == expandedWidth, "hidden relayout collapsed the collection sidebar: " + collection.Width);
                Check(collection.Left == sidebar.Width, "hidden collection sidebar moved left: " + collection.Left + " / " + sidebar.Width);
                Check(grid.Left == collection.Right, "hidden grid moved away from the collection sidebar: " + grid.Left + " / " + collection.Right);

                Invoke(form, "ShowFromTray");
                Application.DoEvents();
                Check(form.Visible, "ShowFromTray did not show the form");
                Check(collection.Visible, "collection sidebar stayed hidden after ShowFromTray");
                Check(collection.Width == expandedWidth, "restored collection sidebar width changed: " + collection.Width);
                Check(collection.Left == sidebar.Width, "restored collection sidebar moved left");
                Check(grid.Left == collection.Right, "restored grid moved away from the collection sidebar");

                // 收起/展开集合栏后，再隐藏和恢复，宽度与网格起点仍须一致。
                typeof(Controls.CollectionSidebar).GetMethod("ToggleCollapsed", Private).Invoke(collection, null);
                Application.DoEvents();
                int collapsedWidth = collection.Width;
                Check(collection.Collapsed && collapsedWidth > 0 && collapsedWidth < expandedWidth, "collection sidebar did not collapse");
                Check(grid.Left == collection.Right, "grid did not follow a collapsed collection sidebar");
                Invoke(form, "HideToTray");
                Application.DoEvents();
                Invoke(form, "UpdateCollectionUi");
                Invoke(form, "LayoutControls");
                Application.DoEvents();
                Check(collection.Width == collapsedWidth, "hidden relayout lost the collapsed width");
                Invoke(form, "ShowFromTray");
                Application.DoEvents();
                Check(collection.Width == collapsedWidth && grid.Left == collection.Right, "collapsed collection state changed after restore");
                typeof(Controls.CollectionSidebar).GetMethod("ToggleCollapsed", Private).Invoke(collection, null);
                Application.DoEvents();
                Check(collection.Width == expandedWidth && grid.Left == collection.Right, "collection sidebar did not expand again");

                // 集合信息页：隐藏/恢复后保持原内容区左边界，不被集合栏宽度覆盖。
                typeof(MainForm).GetField("currentCollection", Private).SetValue(form, collectionKey);
                typeof(MainForm).GetField("collectionEntered", Private).SetValue(form, false);
                Invoke(form, "UpdateCollectionUi");
                Application.DoEvents();
                Check(info.Visible, "fixture collection info page did not open");
                Check(info.Left == collection.Right, "collection info did not start at the collection sidebar's right edge");
                Invoke(form, "HideToTray");
                Application.DoEvents();
                Invoke(form, "UpdateCollectionUi");
                Invoke(form, "LayoutControls");
                Application.DoEvents();
                Check(info.Left == collection.Right, "hidden collection info left edge drifted");
                Invoke(form, "ShowFromTray");
                Application.DoEvents();
                Check(info.Visible && info.Left == collection.Right, "collection info moved or stayed hidden after restore");

                // 设置页打开期间集合栏不得出现；关闭设置后集合栏和网格必须恢复对齐。
                typeof(MainForm).GetField("currentCollection", Private).SetValue(form, null);
                typeof(MainForm).GetField("collectionEntered", Private).SetValue(form, true);
                Invoke(form, "RefreshGrid");
                Application.DoEvents();
                Check(grid.Visible && !info.Visible, "fixture did not return to the grid before opening settings");
                Invoke(form, "OpenSettings");
                Application.DoEvents();
                Check(settings.Visible, "settings page did not open");
                Check(!collection.Visible, "collection sidebar remained visible in settings");
                Invoke(form, "HideToTray");
                Application.DoEvents();
                Invoke(form, "UpdateCollectionUi");
                Invoke(form, "LayoutControls");
                Application.DoEvents();
                Invoke(form, "ShowFromTray");
                Application.DoEvents();
                Check(settings.Visible, "settings page did not survive tray restore");
                Check(!collection.Visible, "collection sidebar appeared after restoring settings from tray");
                Invoke(form, "CloseSettings");
                Application.DoEvents();
                Check(!settings.Visible, "settings page did not close");
                Check(collection.Visible && collection.Width == expandedWidth, "collection sidebar did not return after closing settings");
                Check(collection.Left == sidebar.Width && grid.Left == collection.Right, "grid and collection sidebar are misaligned after closing settings");
            }
        }

        /// <summary>按标题找设置页的一行（行模型是私有嵌套类型，只能反射访问）；找不到返回 -1。</summary>
        private static int SettingsRowIndex(Controls.SettingsPage page, string title)
        {
            IList rows = (IList)typeof(Controls.SettingsPage).GetField("rows", Private).GetValue(page);
            for (int i = 0; i < rows.Count; i++)
                if ((string)rows[i].GetType().GetField("Title").GetValue(rows[i]) == title) return i;
            return -1;
        }

        private static object SettingsRowField(Controls.SettingsPage page, string title, string field)
        {
            int index = SettingsRowIndex(page, title);
            if (index < 0) throw new Exception("settings row not found: " + title);
            object row = ((IList)typeof(Controls.SettingsPage).GetField("rows", Private).GetValue(page))[index];
            return row.GetType().GetField(field).GetValue(row);
        }

        /// <summary>设置页内容坐标换算为客户区坐标（内容可滚动）。</summary>
        private static Point SettingsClient(Controls.SettingsPage page, Point content)
        {
            return (Point)typeof(Controls.SettingsPage).GetMethod("ToClient", Private).Invoke(page, new object[] { content });
        }

        /// <summary>在设置页的两个内容坐标处依次按下、松开指定鼠标键。</summary>
        private static void SettingsPress(Controls.SettingsPage page, MouseButtons button, Point down, Point up)
        {
            Point a = SettingsClient(page, down), b = SettingsClient(page, up);
            typeof(Control).GetMethod("OnMouseDown", Private).Invoke(page, new object[] { new MouseEventArgs(button, 1, a.X, a.Y, 0) });
            typeof(Control).GetMethod("OnMouseUp", Private).Invoke(page, new object[] { new MouseEventArgs(button, 1, b.X, b.Y, 0) });
        }

        /// <summary>行的文字区域中点（内容坐标），避开右侧的开关和按钮。</summary>
        private static Point SettingsRowPoint(Controls.SettingsPage page, string title)
        {
            Rectangle r = (Rectangle)SettingsRowField(page, title, "Bounds");
            return new Point(r.Left + 90, r.Top + r.Height / 2);
        }

        private static void ClickSettingsRow(Controls.SettingsPage page, string title, MouseButtons button)
        {
            Point p = SettingsRowPoint(page, title);
            SettingsPress(page, button, p, p);
        }

        /// <summary>点击某一行里的小点击区（StoreToggle / MoveUp / MoveDown）；该行没有这个点击区时返回 false。</summary>
        private static bool ClickSettingsZone(Controls.SettingsPage page, string title, string kind)
        {
            int row = SettingsRowIndex(page, title);
            foreach (object zone in (IEnumerable)typeof(Controls.SettingsPage).GetField("zones", Private).GetValue(page))
            {
                if ((int)zone.GetType().GetField("Row").GetValue(zone) != row
                    || zone.GetType().GetField("Kind").GetValue(zone).ToString() != kind) continue;
                Rectangle b = (Rectangle)zone.GetType().GetField("Bounds").GetValue(zone);
                Point p = new Point(b.Left + b.Width / 2, b.Top + b.Height / 2);
                SettingsPress(page, MouseButtons.Left, p, p);
                return true;
            }
            return false;
        }

        /// <summary>截取设置页，返回内容坐标 content 处的像素颜色。</summary>
        private static int SettingsPixel(Controls.SettingsPage page, Point content)
        {
            Point p = SettingsClient(page, content);
            using (Bitmap shot = new Bitmap(page.Width, page.Height))
            {
                page.DrawToBitmap(shot, new Rectangle(Point.Empty, shot.Size));
                return shot.GetPixel(p.X, p.Y).ToArgb();
            }
        }

        /// <summary>点击设置页左侧目录的某一项（目录不滚动，按客户区坐标按下、松开）。</summary>
        private static void ClickSettingsNav(Controls.SettingsPage page, string title)
        {
            foreach (object item in (IEnumerable)typeof(Controls.SettingsPage).GetField("navItems", Private).GetValue(page))
            {
                if ((string)item.GetType().GetField("Title").GetValue(item) != title) continue;
                Rectangle b = (Rectangle)item.GetType().GetField("Bounds").GetValue(item);
                Point p = new Point(b.Left + b.Width / 2, b.Top + b.Height / 2);
                typeof(Control).GetMethod("OnMouseDown", Private).Invoke(page, new object[] { new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0) });
                typeof(Control).GetMethod("OnMouseUp", Private).Invoke(page, new object[] { new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0) });
                return;
            }
            throw new Exception("settings nav item not found: " + title);
        }

        /// <summary>嵌入设置页右侧的假面板：可以手动设置忙碌状态，记录收到的滚轮。</summary>
        private sealed class FakeSettingsPanel : Control, Controls.ISettingsPanel
        {
            private bool busy;
            public int Wheel;
            public bool Busy { get { return busy; } }
            public event EventHandler BusyChanged;
            public void SetBusy(bool value)
            {
                busy = value;
                if (BusyChanged != null) BusyChanged(this, EventArgs.Empty);
            }
            public bool ScrollByWheel(int delta) { Wheel += delta; return true; }
        }

        private static void SettingsPageRows()
        {
            const string HiddenRow = "显示隐藏项", TrayRow = "关闭时隐藏到托盘", ScanRow = "自动扫描应用与桌面变化", OrganizeRow = "自动收纳桌面新快捷方式";
            const string RescanRow = "重新扫描", TestRow = "运行自测", AddRow = "添加存储位置…";
            const string StoreA = "D:\\Store\\A", StoreB = "E:\\Store\\B", StoreC = "F:\\Store\\C", DefaultStore = "C:\\Default\\Resources";
            using (DpiAwareForm host = new DpiAwareForm())
            using (Controls.SettingsPage page = new Controls.SettingsPage())
            {
                host.ShowInTaskbar = false;
                host.FormBorderStyle = FormBorderStyle.None;
                host.StartPosition = FormStartPosition.Manual;
                host.Location = new Point(-32000, -32000);
                host.ClientSize = new Size(1000, 560);
                page.SetBounds(0, 0, 1000, 1400);   // 足够高：先在不滚动的状态下验证点击
                host.Controls.Add(page);
                int hiddenEvents = 0, trayEvents = 0, scanEvents = 0, organizeEvents = 0, rescanEvents = 0, testEvents = 0, addEvents = 0, backEvents = 0;
                List<string> storeEvents = new List<string>();
                page.ShowHiddenChanged += delegate { hiddenEvents++; };
                page.CloseToTrayChanged += delegate { trayEvents++; };
                page.AutoScanChanged += delegate { scanEvents++; };
                page.AutoOrganizeDesktopChanged += delegate { organizeEvents++; };
                page.RescanRequested += delegate { rescanEvents++; };
                page.SelfTestRequested += delegate { testEvents++; };
                page.StorageAddRequested += delegate { addEvents++; };
                page.BackRequested += delegate { backEvents++; };
                page.StorageEnabledRequested += delegate(int index, bool enabled) { storeEvents.Add("enable " + index + " " + enabled); };
                page.StorageMoveRequested += delegate(int index, int delta) { storeEvents.Add("move " + index + " " + delta); };
                Controls.RoundButton back = (Controls.RoundButton)typeof(Controls.SettingsPage).GetField("backBtn", Private).GetValue(page);
                FieldInfo scroll = typeof(Controls.SettingsPage).GetField("scrollOffset", Private);
                List<ResourceStoreLocation> three = new List<ResourceStoreLocation> {
                    new ResourceStoreLocation(StoreA, true), new ResourceStoreLocation(StoreB, false), new ResourceStoreLocation(StoreC, true) };
                FakeSettingsPanel backupPanel = new FakeSettingsPanel();
                page.HostPanel(Controls.SettingsPage.SettingsSection.Backup, backupPanel);
                host.Show();
                try
                {
                    Application.DoEvents();
                    page.Bind(false, true, false, true, three, DefaultStore);
                    Check(hiddenEvents + scanEvents + organizeEvents + storeEvents.Count == 0, "binding raised change events");

                    // 默认显示「常规」；目录切换只显示所选目录的行，嵌入面板只在自己的目录下可见。
                    Check(page.Section == Controls.SettingsPage.SettingsSection.General && SettingsRowIndex(page, HiddenRow) >= 0
                        && SettingsRowIndex(page, ScanRow) < 0 && SettingsRowIndex(page, StoreA) < 0, "general section does not show only its own rows");
                    Check(!backupPanel.Visible, "a hosted panel is visible outside its section");
                    ClickSettingsNav(page, "布局备份");
                    Check(page.Section == Controls.SettingsPage.SettingsSection.Backup && backupPanel.Visible
                        && ((IList)typeof(Controls.SettingsPage).GetField("rows", Private).GetValue(page)).Count == 0,
                        "selecting the backup section did not show its hosted panel");
                    Check(backupPanel.Left >= page.NavWidth && backupPanel.Right <= page.Width && backupPanel.Top >= back.Bottom,
                        "hosted panel is not placed in the right pane: " + backupPanel.Bounds);
                    Check(page.HandleWheel(-120) && backupPanel.Wheel == -120, "wheel on a hosted section was not handed to the panel");

                    // 面板忙碌：「返回」和目录都不能用，停在当前目录；结束后恢复。
                    backupPanel.SetBusy(true);
                    Check(page.Busy && !back.Enabled, "busy panel did not disable the back button");
                    ClickSettingsNav(page, "常规");
                    Check(page.Section == Controls.SettingsPage.SettingsSection.Backup && backupPanel.Visible, "section changed while the panel was busy");
                    Check(!page.SelectSection(Controls.SettingsPage.SettingsSection.General), "SelectSection succeeded while busy");
                    typeof(Control).GetMethod("OnClick", Private).Invoke(back, new object[] { EventArgs.Empty });
                    Check(backEvents == 0, "back was requested while the panel was busy");
                    backupPanel.SetBusy(false);
                    Check(!page.Busy && back.Enabled, "back button stayed disabled after the panel finished");
                    ClickSettingsNav(page, "常规");
                    Check(page.Section == Controls.SettingsPage.SettingsSection.General && !backupPanel.Visible, "nav did not return to the general section");
                    typeof(Control).GetMethod("OnClick", Private).Invoke(back, new object[] { EventArgs.Empty });
                    Check(backEvents == 1, "back button did not request leaving");
                    Check(page.HandleWheel(0), "a row section did not claim the wheel");

                    // 尺寸只由页面按当前 DPI 计算：切换 DPI 不叠加框架缩放，之后再改页面大小也不退回未缩放尺寸。
                    page.SelectSection(Controls.SettingsPage.SettingsSection.Scan);
                    foreach (int dpi in new int[] { 144, 120, 96 })
                    {
                        SetDpi(host, dpi, host.Bounds);
                        for (int pass = 0; pass < 2; pass++)
                        {
                            Rectangle row = (Rectangle)SettingsRowField(page, OrganizeRow, "Bounds");
                            Check(row.Height == DpiHelper.Scale(64, dpi), "row height not scaled at " + dpi + " DPI: " + row);
                            Check(back.Size == new Size(DpiHelper.Scale(74, dpi), DpiHelper.Scale(30, dpi)),
                                "back button size wrong at " + dpi + " DPI (pass " + pass + "): " + back.Size);
                            Check(page.NavWidth == DpiHelper.Scale(220, dpi) && back.Right <= page.NavWidth
                                && row.Left >= page.NavWidth && row.Right <= page.Width,
                                "settings layout is not split into nav and content at " + dpi + " DPI: " + row + " / " + back.Bounds);
                            page.Width += 20;   // 第二轮：普通改大小触发的重新布局
                        }
                    }

                    // 整行点击切换开关：只认左键；按下后移到别的行再松开视为取消。
                    page.SelectSection(Controls.SettingsPage.SettingsSection.General);
                    ClickSettingsRow(page, HiddenRow, MouseButtons.Left);
                    Check(page.ShowHidden && hiddenEvents == 1 && (bool)SettingsRowField(page, HiddenRow, "Checked"),
                        "left click on a toggle row did not toggle once");
                    ClickSettingsRow(page, HiddenRow, MouseButtons.Right);
                    Check(page.ShowHidden && hiddenEvents == 1, "right click toggled the row");
                    SettingsPress(page, MouseButtons.Left, SettingsRowPoint(page, HiddenRow), SettingsRowPoint(page, TrayRow));
                    Check(page.ShowHidden && page.CloseToTray && hiddenEvents == 1 && trayEvents == 0, "releasing on another row still activated a row");

                    // 悬停高亮画到卡片左缘；没有悬停的行保持卡片底色。
                    Rectangle hidden = (Rectangle)SettingsRowField(page, HiddenRow, "Bounds");
                    Rectangle tray = (Rectangle)SettingsRowField(page, TrayRow, "Bounds");
                    Point hover = SettingsClient(page, SettingsRowPoint(page, HiddenRow));
                    typeof(Control).GetMethod("OnMouseMove", Private).Invoke(page, new object[] { new MouseEventArgs(MouseButtons.None, 0, hover.X, hover.Y, 0) });
                    Check(SettingsPixel(page, new Point(hidden.Left + 6, hidden.Top + hidden.Height / 2)) == Theme.CardHover.ToArgb(), "hovered row is not highlighted");
                    Check(SettingsPixel(page, new Point(tray.Left + 6, tray.Top + tray.Height / 2)) == Theme.Card.ToArgb(), "a row that is not hovered is highlighted");
                    // 目录底色从页面顶边起就是侧栏色：不能有抗锯齿混出来的浅线
                    using (Bitmap shot = new Bitmap(page.Width, page.Height))
                    {
                        page.DrawToBitmap(shot, new Rectangle(Point.Empty, shot.Size));
                        Check(shot.GetPixel(4, 0).ToArgb() == Theme.SidebarBg.ToArgb() && shot.GetPixel(4, page.Height - 1).ToArgb() == Theme.SidebarBg.ToArgb(),
                            "nav column is not filled edge to edge: " + shot.GetPixel(4, 0));
                    }

                    // 「自动收纳」依赖自动扫描：扫描关闭时置灰、点击无效、保留原值；重新开启后恢复。
                    page.SelectSection(Controls.SettingsPage.SettingsSection.Scan);
                    Check((bool)SettingsRowField(page, OrganizeRow, "Enabled"), "organize row disabled while auto scan is on");
                    ClickSettingsRow(page, ScanRow, MouseButtons.Left);
                    Check(!page.AutoScan && scanEvents == 1, "auto scan row did not switch off");
                    Check(!(bool)SettingsRowField(page, OrganizeRow, "Enabled"), "organize row stayed enabled without auto scan");
                    Check(((string)SettingsRowField(page, OrganizeRow, "Subtitle")).Contains("自动扫描"), "disabled organize row does not explain the dependency");
                    ClickSettingsRow(page, OrganizeRow, MouseButtons.Left);
                    Check(!page.AutoOrganizeDesktop && organizeEvents == 0, "disabled organize row still toggled");
                    Rectangle organize = (Rectangle)SettingsRowField(page, OrganizeRow, "Bounds");
                    hover = SettingsClient(page, SettingsRowPoint(page, OrganizeRow));
                    typeof(Control).GetMethod("OnMouseMove", Private).Invoke(page, new object[] { new MouseEventArgs(MouseButtons.None, 0, hover.X, hover.Y, 0) });
                    Check(SettingsPixel(page, new Point(organize.Left + 6, organize.Top + organize.Height / 2)) == Theme.Card.ToArgb(), "disabled row is highlighted on hover");
                    ClickSettingsRow(page, ScanRow, MouseButtons.Left);
                    ClickSettingsRow(page, OrganizeRow, MouseButtons.Left);
                    Check(page.AutoScan && page.AutoOrganizeDesktop && scanEvents == 2 && organizeEvents == 1, "organize row did not recover after enabling auto scan");

                    // 立即执行的行：进行中不重复触发，结束后给出结果并可再次触发。
                    ClickSettingsRow(page, RescanRow, MouseButtons.Left);
                    Check(rescanEvents == 1, "rescan row click did not request a scan");
                    page.SetRescanState(Controls.SettingsPage.RescanState.Running);
                    Check((string)SettingsRowField(page, RescanRow, "ButtonText") == "扫描中…", "running rescan row does not say so");
                    ClickSettingsRow(page, RescanRow, MouseButtons.Left);
                    Check(rescanEvents == 1, "rescan row fired again while running");
                    page.SetRescanState(Controls.SettingsPage.RescanState.Failed);
                    Check(((string)SettingsRowField(page, RescanRow, "Subtitle")).Contains("失败") && (bool)SettingsRowField(page, RescanRow, "Warning"),
                        "failed rescan is not reported");
                    page.SetRescanState(Controls.SettingsPage.RescanState.Done);
                    Check(((string)SettingsRowField(page, RescanRow, "Subtitle")).Contains("完成"), "finished rescan is not reported");
                    ClickSettingsRow(page, RescanRow, MouseButtons.Left);
                    Check(rescanEvents == 2, "rescan row cannot be used again after finishing");

                    // 自测：运行中不重复触发；结果写在这一行的说明里，未全部通过时用警示色。
                    page.SelectSection(Controls.SettingsPage.SettingsSection.Diagnostics);
                    page.SetSelfTestRunning(true);
                    ClickSettingsRow(page, TestRow, MouseButtons.Left);
                    Check(testEvents == 0, "self test row fired while a self test is running");
                    page.SetSelfTestResult("上次结果：通过 9/10，详见 selftest.log。", true);
                    page.SetSelfTestRunning(false);
                    Check(((string)SettingsRowField(page, TestRow, "Subtitle")).Contains("9/10") && (bool)SettingsRowField(page, TestRow, "Warning"),
                        "self test result is not shown on the row");
                    ClickSettingsRow(page, TestRow, MouseButtons.Left);
                    Check(testEvents == 1, "self test row did not request a run");

                    // 存储位置：整行不可点，只有开关和排序按钮响应；首尾位置的越界方向不可用。
                    page.SelectSection(Controls.SettingsPage.SettingsSection.Storage);
                    three[0].Enabled = false;
                    Check((bool)SettingsRowField(page, StoreA, "Checked"), "page shares the caller's location objects instead of copying them");
                    three[0].Enabled = true;
                    Check(((string)SettingsRowField(page, StoreA, "Subtitle")).Contains("优先"), "first enabled location is not marked as preferred");
                    Check(((string)SettingsRowField(page, StoreB, "Subtitle")).Contains("已停用") && !(bool)SettingsRowField(page, StoreB, "Checked"),
                        "disabled location is not shown as disabled");
                    Check(!((string)SettingsRowField(page, StoreC, "Subtitle")).Contains("优先"), "a later location is marked as preferred");
                    ClickSettingsRow(page, StoreA, MouseButtons.Left);
                    Check(storeEvents.Count == 0, "clicking a location's text changed it");
                    // 路径过长时界面会省略中间：悬停存储位置行给出完整路径，移到别的行后清除。
                    ToolTip tip = (ToolTip)typeof(Controls.SettingsPage).GetField("pathTip", Private).GetValue(page);
                    hover = SettingsClient(page, SettingsRowPoint(page, StoreB));
                    typeof(Control).GetMethod("OnMouseMove", Private).Invoke(page, new object[] { new MouseEventArgs(MouseButtons.None, 0, hover.X, hover.Y, 0) });
                    Check(tip.GetToolTip(page) == StoreB, "hovering a location does not offer its full path: " + tip.GetToolTip(page));
                    hover = SettingsClient(page, SettingsRowPoint(page, AddRow));
                    typeof(Control).GetMethod("OnMouseMove", Private).Invoke(page, new object[] { new MouseEventArgs(MouseButtons.None, 0, hover.X, hover.Y, 0) });
                    Check(string.IsNullOrEmpty(tip.GetToolTip(page)), "path tooltip stayed on a row that is not a location");
                    Check(ClickSettingsZone(page, StoreB, "StoreToggle") && storeEvents.Count == 1 && storeEvents[0] == "enable 1 True",
                        "location switch did not request enabling: " + string.Join(",", storeEvents.ToArray()));
                    Check(ClickSettingsZone(page, StoreA, "StoreToggle") && storeEvents.Count == 2 && storeEvents[1] == "enable 0 False",
                        "location switch did not request disabling");
                    Check(ClickSettingsZone(page, StoreA, "MoveDown") && storeEvents.Count == 3 && storeEvents[2] == "move 0 1", "move down was not requested");
                    Check(ClickSettingsZone(page, StoreC, "MoveUp") && storeEvents.Count == 4 && storeEvents[3] == "move 2 -1", "move up was not requested");
                    Check(ClickSettingsZone(page, StoreA, "MoveUp") && ClickSettingsZone(page, StoreC, "MoveDown") && storeEvents.Count == 4,
                        "moving past the ends of the list was requested");
                    ClickSettingsRow(page, AddRow, MouseButtons.Left);
                    Check(addEvents == 1, "add location row did not request a folder");
                    Check(hiddenEvents == 1 && scanEvents == 2 && organizeEvents == 1, "location actions raised unrelated events");

                    // 只有一个位置：没有排序按钮。全部停用：页说明改为警示。没有显式位置：显示默认位置，仍可添加。
                    FieldInfo noteWarning = typeof(Controls.SettingsPage).GetField("paneNoteWarning", Private);
                    Check(!(bool)noteWarning.GetValue(page), "storage note is a warning while locations are enabled");
                    page.Bind(true, false, true, true, new List<ResourceStoreLocation> { new ResourceStoreLocation(StoreA, false) }, DefaultStore);
                    Check(page.Section == Controls.SettingsPage.SettingsSection.Storage, "rebinding changed the selected section");
                    Check(!ClickSettingsZone(page, StoreA, "MoveUp") && !ClickSettingsZone(page, StoreA, "MoveDown"), "a single location offers reordering");
                    Check((bool)noteWarning.GetValue(page), "all-disabled locations are not flagged");
                    page.Bind(true, false, true, true, null, DefaultStore);
                    Check(SettingsRowIndex(page, DefaultStore) >= 0 && SettingsRowIndex(page, AddRow) >= 0, "empty location list does not show the default location");
                    ClickSettingsRow(page, DefaultStore, MouseButtons.Left);
                    Check(storeEvents.Count == 4 && addEvents == 1, "the default location row is clickable");
                    page.SelectSection(Controls.SettingsPage.SettingsSection.Scan);
                    Check(page.AutoOrganizeDesktop && !(bool)SettingsRowField(page, OrganizeRow, "Enabled"), "binding with auto scan off lost the organize value or left it enabled");
                    Check(hiddenEvents == 1 && scanEvents == 2 && organizeEvents == 1, "rebinding raised change events");

                    // 滚动：页面变矮后内容可滚动；高分辨率滚轮的小增量累计后与一次整格等效；滚出视口的行不响应点击。
                    page.Bind(false, true, false, true, three, DefaultStore);
                    page.SelectSection(Controls.SettingsPage.SettingsSection.Storage);
                    Check((int)scroll.GetValue(page) == 0, "tall page scrolled without need");
                    page.Height = DpiHelper.Scale(300, 96);
                    page.ScrollByWheel(-120);
                    int oneNotch = (int)scroll.GetValue(page);
                    Check(oneNotch > 0, "short page did not scroll with the wheel");
                    page.ScrollToTop();
                    for (int i = 0; i < 4; i++) page.ScrollByWheel(-30);
                    Check((int)scroll.GetValue(page) == oneNotch,
                        "high-resolution wheel deltas did not add up: " + scroll.GetValue(page) + " / " + oneNotch);
                    page.ScrollByWheel(-120 * 200);
                    int bottom = (int)scroll.GetValue(page);
                    Rectangle last = (Rectangle)SettingsRowField(page, AddRow, "Bounds");
                    Check(SettingsClient(page, new Point(0, last.Bottom)).Y <= page.Height, "scrolling to the end does not reveal the last row");
                    ClickSettingsRow(page, AddRow, MouseButtons.Left);
                    Check(addEvents == 2, "a row scrolled into view is not clickable");
                    Check(SettingsClient(page, SettingsRowPoint(page, StoreA)).Y < DpiHelper.Scale(92, 96), "fixture did not scroll the first row under the header");
                    ClickSettingsZone(page, StoreA, "StoreToggle");
                    Check(storeEvents.Count == 4, "a row scrolled under the fixed header still reacted");
                    Check(back.Top == DpiHelper.Scale(16, 96), "the back button scrolled with the content");
                    ClickSettingsNav(page, "常规");
                    Check(page.Section == Controls.SettingsPage.SettingsSection.General && (int)scroll.GetValue(page) == 0,
                        "switching sections kept the previous section's scroll position");
                    page.SelectSection(Controls.SettingsPage.SettingsSection.Storage);

                    // 滚动条：拖动滑块滚动；捕获丢失或页面隐藏时结束拖动（否则下次移动鼠标页面会跟着滚）；点轨道空白处跳转。
                    Action<string, MouseButtons, Point> fire = delegate(string handler, MouseButtons button, Point at)
                    {
                        typeof(Control).GetMethod(handler, Private).Invoke(page, new object[] { new MouseEventArgs(button, 1, at.X, at.Y, 0) });
                    };
                    MethodInfo thumbRect = typeof(Controls.SettingsPage).GetMethod("ScrollThumbRect", Private);
                    FieldInfo dragging = typeof(Controls.SettingsPage).GetField("scrollDragging", Private);
                    page.ScrollToTop();
                    Rectangle thumb = (Rectangle)thumbRect.Invoke(page, null);
                    Check(thumb.Height > 0, "scrollable page has no scroll thumb");
                    Point grab = new Point(thumb.Left + thumb.Width / 2, thumb.Top + 5);
                    fire("OnMouseDown", MouseButtons.Left, grab);
                    Check((bool)dragging.GetValue(page), "pressing the scroll thumb did not start a drag");
                    fire("OnMouseMove", MouseButtons.Left, new Point(grab.X, grab.Y + 20));
                    int draggedTo = (int)scroll.GetValue(page);
                    Check(draggedTo > 0 && draggedTo <= bottom, "dragging the scroll thumb did not scroll: " + draggedTo);
                    typeof(Control).GetMethod("OnMouseCaptureChanged", Private).Invoke(page, new object[] { EventArgs.Empty });
                    Check(!(bool)dragging.GetValue(page), "losing mouse capture did not end the scroll drag");
                    page.ScrollToTop();
                    fire("OnMouseMove", MouseButtons.None, new Point(grab.X, grab.Y + 90));
                    Check((int)scroll.GetValue(page) == 0, "the page kept scrolling after the drag was cancelled");
                    page.ScrollByWheel(-120 * 200);
                    fire("OnMouseDown", MouseButtons.Left, (Point)SettingsClient(page, SettingsRowPoint(page, AddRow)));
                    typeof(Control).GetMethod("OnMouseCaptureChanged", Private).Invoke(page, new object[] { EventArgs.Empty });
                    fire("OnMouseUp", MouseButtons.Left, (Point)SettingsClient(page, SettingsRowPoint(page, AddRow)));
                    Check(addEvents == 2, "a press that lost mouse capture still activated the row");
                    thumb = (Rectangle)thumbRect.Invoke(page, null);
                    fire("OnMouseDown", MouseButtons.Left, new Point(thumb.Left + thumb.Width / 2, thumb.Top + 5));
                    page.Visible = false;
                    Check(!(bool)dragging.GetValue(page), "hiding the page did not end the scroll drag");
                    page.Visible = true;
                    Rectangle track = (Rectangle)typeof(Controls.SettingsPage).GetMethod("ScrollTrackRect", Private).Invoke(page, null);
                    page.ScrollToTop();
                    fire("OnMouseDown", MouseButtons.Left, new Point(track.Left + track.Width / 2, track.Bottom - 2));
                    Check((int)scroll.GetValue(page) == bottom, "clicking the end of the scroll track did not jump there: " + scroll.GetValue(page) + " / " + bottom);
                    fire("OnMouseUp", MouseButtons.Left, new Point(track.Left + track.Width / 2, track.Bottom - 2));
                    Check(!(bool)dragging.GetValue(page), "releasing the mouse did not end the scroll drag");
                    page.Height = 1400;
                    Check(bottom > 0 && (int)scroll.GetValue(page) == 0, "scroll offset was not clamped after the page grew");
                }
                finally
                {
                    host.Close();
                }
            }
        }

        private static void SettingsRescanFeedback()
        {
            AppConfig config = RenameConfig("settings-rescan");
            config.AutoScan = false;
            string first = Path.Combine(root, "settings-rescan", "res"), second = Path.Combine(root, "settings-rescan", "res2");
            config.ResourceStoreLocations.Add(new ResourceStoreLocation(first, true));
            config.ResourceStoreLocations.Add(new ResourceStoreLocation(second, true));
            Check(config.Save(), "settings fixture save failed");
            using (MainForm form = new MainForm(config))
            {
                FieldInfo running = typeof(MainForm).GetField("scanRunning", Private);
                FieldInfo scannerField = typeof(MainForm).GetField("scanner", Private);
                FieldInfo state = typeof(Controls.SettingsPage).GetField("rescanState", Private);
                running.SetValue(form, true);   // 避免 OnLoad 启动真实扫描
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-3000, -3000);
                form.ClientSize = new Size(1200, 1100);   // 足够高：设置页各行无需滚动即可点击
                form.Show();
                Application.DoEvents();
                Controls.SettingsPage page = (Controls.SettingsPage)typeof(MainForm).GetField("settingsPage", Private).GetValue(form);
                Invoke(form, "OpenSettings");
                Application.DoEvents();
                Check((Controls.SettingsPage.RescanState)state.GetValue(page) == Controls.SettingsPage.RescanState.Idle, "rescan row not idle when settings open");
                page.SelectSection(Controls.SettingsPage.SettingsSection.Scan);

                // 扫描器置空：后台扫描必然失败，走完真实的开始/结束回调而不枚举真实系统环境。
                object scanner = scannerField.GetValue(form);
                scannerField.SetValue(form, null);
                running.SetValue(form, false);
                try
                {
                    ClickSettingsRow(page, "重新扫描", MouseButtons.Left);
                    Check((Controls.SettingsPage.RescanState)state.GetValue(page) == Controls.SettingsPage.RescanState.Running, "rescan row did not show the running scan");
                    PumpUntil(delegate { return (Controls.SettingsPage.RescanState)state.GetValue(page) != Controls.SettingsPage.RescanState.Running; });
                }
                finally { scannerField.SetValue(form, scanner); }
                Check((Controls.SettingsPage.RescanState)state.GetValue(page) == Controls.SettingsPage.RescanState.Failed, "failed scan was not reported on the rescan row");
                Check(!(bool)running.GetValue(form), "scan flag stayed set after the failed scan");

                // 扫描回调丢失（扫描标志已复位但没有结束通知）：重新进入设置页时不能还卡在「正在扫描」。
                running.SetValue(form, true);
                ClickSettingsRow(page, "重新扫描", MouseButtons.Left);
                Check((Controls.SettingsPage.RescanState)state.GetValue(page) == Controls.SettingsPage.RescanState.Running, "queued rescan is not shown as running");
                Invoke(form, "CloseSettings");
                Invoke(form, "OpenSettings");
                Check((Controls.SettingsPage.RescanState)state.GetValue(page) == Controls.SettingsPage.RescanState.Running, "reopening settings dropped a scan that is still running");
                running.SetValue(form, false);
                Invoke(form, "CloseSettings");
                Invoke(form, "OpenSettings");
                Check((Controls.SettingsPage.RescanState)state.GetValue(page) == Controls.SettingsPage.RescanState.Idle, "rescan row stayed stuck after the scan flag cleared");
                running.SetValue(form, true);   // 关闭窗体前不再启动补扫

                // 存储位置就地修改：停用、排序立即写入配置并落库，页面按生效后的配置刷新。
                page.SelectSection(Controls.SettingsPage.SettingsSection.Storage);
                Check(ClickSettingsZone(page, first, "StoreToggle"), "first location has no switch");
                Check(!config.ResourceStoreLocations[0].Enabled && config.ResourceStoreLocations[1].Enabled
                    && SameText(config.ResourceStorePath, second), "disabling a location did not move the legacy path to the next enabled one");
                Check(!(bool)SettingsRowField(page, first, "Checked") && ((string)SettingsRowField(page, second, "Subtitle")).Contains("优先"),
                    "settings page did not refresh after disabling a location");
                Check(ClickSettingsZone(page, first, "MoveDown"), "first location has no move-down button");
                Check(SameText(config.ResourceStoreLocations[0].Path, second) && SameText(config.ResourceStoreLocations[1].Path, first)
                    && !config.ResourceStoreLocations[1].Enabled, "moving a location did not swap the order or lost its enabled flag");
                AppConfig loaded = AppConfig.Load();
                Check(loaded.ResourceStoreLocations.Count == 2 && SameText(loaded.ResourceStoreLocations[0].Path, second)
                    && !loaded.ResourceStoreLocations[1].Enabled, "location edits were not persisted");

                // 保存失败：配置还原为数据库里的值，页面仍显示原状态，之后可以重试。
                // 提示由 AppConfig.SaveFailed 统一给出一次；这里摘掉它（否则模态提示会阻塞用例），同时确认存储位置自己不再另弹一个。
                FieldInfo saveFailed = typeof(AppConfig).GetField("SaveFailed", BindingFlags.Instance | BindingFlags.NonPublic);
                Check(saveFailed != null, "save failure event was not found");
                saveFailed.SetValue(config, null);
                bool unexpectedDialog;
                using (Sqlite db = new Sqlite(config.Database.FilePath, false))
                {
                    db.Execute("BEGIN IMMEDIATE");
                    try
                    {
                        bool extraPrompt = AnswerMessageDialog("资源存储位置", 2,
                            delegate { ClickSettingsZone(page, second, "StoreToggle"); }, out unexpectedDialog);
                        Check(!extraPrompt && !unexpectedDialog, "failed location save raised its own prompt on top of the shared one");
                    }
                    finally { db.Execute("ROLLBACK"); }
                }
                Check(config.ResourceStoreLocations.Count == 2 && config.ResourceStoreLocations[0].Enabled
                    && SameText(config.ResourceStorePath, second), "failed location save left the edit in memory");
                Check((bool)SettingsRowField(page, second, "Checked"), "settings page shows an edit that was not saved");
                Check(ClickSettingsZone(page, second, "StoreToggle") && !config.ResourceStoreLocations[0].Enabled,
                    "location edit could not be retried after a failed save");
                Check(AppConfig.Load().ResourceStoreLocations.All(l => !l.Enabled), "retried location edit was not persisted");

                // 添加位置：已在列表里的重新启用；新位置加到末尾；与已有位置互相包含时提示并保持原样。
                string third = Path.Combine(root, "settings-rescan", "res3");
                Invoke(form, "AddStoreLocation", first);
                Check(config.ResourceStoreLocations.Count == 2 && config.ResourceStoreLocations[1].Enabled
                    && SameText(config.ResourceStorePath, first), "re-adding a disabled location did not enable it in place");
                Invoke(form, "AddStoreLocation", third);
                Check(config.ResourceStoreLocations.Count == 3 && SameText(config.ResourceStoreLocations[2].Path, third)
                    && config.ResourceStoreLocations[2].Enabled && SettingsRowIndex(page, third) >= 0, "a new location was not appended and shown");
                bool rejected = AnswerMessageDialog("资源存储位置", 2,
                    delegate { Invoke(form, "AddStoreLocation", Path.Combine(third, "inner")); });
                Check(rejected && config.ResourceStoreLocations.Count == 3, "a nested location was accepted or rejected silently");
                Check(AppConfig.Load().ResourceStoreLocations.Count == 3, "added location was not persisted");
                // 选择窗口里的校验：重复和互相包含当场给出原因，且不改动配置；新位置通过
                int before = config.ResourceStoreLocations.Count;
                Check(((string)Invoke(form, "StoreLocationError", Path.Combine(third, "inner"))).Contains("互相包含")
                    && Invoke(form, "StoreLocationError", Path.Combine(root, "settings-rescan", "res4")) == null
                    && Invoke(form, "StoreLocationError", first) == null && config.ResourceStoreLocations.Count == before,
                    "store location check in the picker is wrong or changed the config");

                // 一直在用默认位置时添加第一个位置：默认位置作为第一项保留，已有资源仍被识别为库内文件。
                config.ResourceStoreLocations.Clear();
                config.ResourceStorePath = string.Empty;
                Check(config.Save(), "default-location fixture save failed");
                string fallback = ResourceStore.Normalize((string)Invoke(form, "DefaultResourceStoreRoot"));
                Invoke(form, "BindSettingsPage");
                Check(SettingsRowIndex(page, fallback) >= 0 && !ClickSettingsZone(page, fallback, "StoreToggle"),
                    "default location is not shown as a plain row");
                Invoke(form, "AddStoreLocation", third);
                Check(config.ResourceStoreLocations.Count == 2 && SameText(config.ResourceStoreLocations[0].Path, fallback)
                    && config.ResourceStoreLocations[0].Enabled && SameText(config.ResourceStoreLocations[1].Path, third),
                    "adding the first location dropped the default location");
                Check(ResourceStore.KnownRoots(config, fallback).Count == 2 && ClickSettingsZone(page, fallback, "StoreToggle")
                    && !config.ResourceStoreLocations[0].Enabled && ResourceStore.KnownRoots(config, fallback).Count == 2,
                    "the kept default location cannot be disabled while staying known");

                // 滚轮路由：光标在设置页上时滚动设置页并吞掉消息；设置页关闭后不再拦截。
                // 位置多到一屏放不下，存储位置页才能滚动。
                for (int i = 0; i < 6; i++) Invoke(form, "AddStoreLocation", Path.Combine(root, "settings-rescan", "extra" + i));
                form.ClientSize = new Size(1200, 600);
                Application.DoEvents();
                FieldInfo scroll = typeof(Controls.SettingsPage).GetField("scrollOffset", Private);
                Point screen = page.PointToScreen(new Point(page.Width / 2, page.Height / 2));
                IntPtr position = new IntPtr(((screen.Y & 0xFFFF) << 16) | (screen.X & 0xFFFF));
                Message wheel = Message.Create(form.Handle, 0x020A, new IntPtr(-120 << 16), position);
                // 操作记录页的列表是原生列表框：滚轮不拦截，由列表自己滚；布局备份页交给备份列表并吞掉消息
                page.SelectSection(Controls.SettingsPage.SettingsSection.Data);
                Check(!form.PreFilterMessage(ref wheel), "wheel over the operation history was swallowed instead of reaching the list");
                page.SelectSection(Controls.SettingsPage.SettingsSection.Backup);
                Check(form.PreFilterMessage(ref wheel), "wheel over the layout backups was not routed to the backup list");
                page.SelectSection(Controls.SettingsPage.SettingsSection.Storage);
                Check((int)scroll.GetValue(page) == 0, "settings page was scrolled before any wheel input");
                Check(form.PreFilterMessage(ref wheel) && (int)scroll.GetValue(page) > 0, "wheel over the settings page did not scroll it");
                Invoke(form, "CloseSettings");
                wheel = Message.Create(form.Handle, 0x020A, new IntPtr(-120 << 16), position);
                Check(!form.PreFilterMessage(ref wheel), "closed settings page still swallowed the wheel");
                Invoke(form, "OpenSettings");
                Check((int)scroll.GetValue(page) == 0, "reopening settings did not return to the top");

                // 滚动条滑块必须避开主窗体的边缘缩放区，否则按住滑块会变成拖动窗口边框。
                MethodInfo thumbRect = typeof(Controls.SettingsPage).GetMethod("ScrollThumbRect", Private);
                foreach (int wheelDelta in new int[] { 0, -120 * 200 })
                {
                    page.ScrollByWheel(wheelDelta);
                    Rectangle thumb = (Rectangle)thumbRect.Invoke(page, null);
                    Check(thumb.Height > 0, "fixture settings page is not scrollable");
                    foreach (Point corner in new Point[] { new Point(thumb.Right - 1, thumb.Top), new Point(thumb.Right - 1, thumb.Bottom - 1) })
                        Check((int)Invoke(form, "ResizeHitTest", new Point(page.Left + corner.X, page.Top + corner.Y)) == 0,
                            "scroll thumb overlaps the window resize zone at " + corner);
                }

                // 右侧面板忙碌（备份、恢复、撤销进行中）：Esc / 返回都不能离开设置页，结束后恢复。
                FakeSettingsPanel busyPanel = new FakeSettingsPanel();
                page.HostPanel(Controls.SettingsPage.SettingsSection.Diagnostics, busyPanel);
                page.SelectSection(Controls.SettingsPage.SettingsSection.Diagnostics);
                FieldInfo open = typeof(MainForm).GetField("settingsOpen", Private);
                busyPanel.SetBusy(true);
                Invoke(form, "CloseSettings");
                Check((bool)open.GetValue(form) && page.Visible, "settings page closed while a panel was busy");
                busyPanel.SetBusy(false);
                Invoke(form, "CloseSettings");
                Check(!(bool)open.GetValue(form) && !page.Visible, "settings page could not close after the panel finished");
            }
        }

        private static void MinimizeKeepsLayout()
        {
            AppConfig config = RenameConfig("minimize-layout");
            config.AutoScan = false;
            using (MainForm form = new MainForm(config))
            {
                typeof(MainForm).GetField("scanRunning", Private).SetValue(form, true);
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-3000, -3000);
                form.ClientSize = new Size(1200, 720);
                form.Show();
                Application.DoEvents();

                Control sidebar = (Control)typeof(MainForm).GetField("sidebar", Private).GetValue(form);
                Control grid = (Control)typeof(MainForm).GetField("grid", Private).GetValue(form);
                Control search = (Control)typeof(MainForm).GetField("search", Private).GetValue(form);
                Size client = form.ClientSize;
                Rectangle gridBounds = grid.Bounds;
                Rectangle sidebarBounds = sidebar.Bounds;
                Rectangle searchBounds = search.Bounds;
                Check(gridBounds.Width > 0 && gridBounds.Height > 0, "fixture grid has no size");

                // 途径一：任务栏点击走 SC_MINIMIZE，由窗体自己把 WindowState 置为最小化。
                form.WindowState = FormWindowState.Minimized;
                Application.DoEvents();
                // 夹具必须真正出现 0×0 客户区，否则后续断言没有意义。
                Check(form.ClientSize.IsEmpty, "fixture client area did not collapse while minimized: " + form.ClientSize);
                Check(grid.Bounds == gridBounds && sidebar.Bounds == sidebarBounds && search.Bounds == searchBounds,
                    "minimizing collapsed the laid-out controls: grid " + grid.Bounds);
                // 最小化期间的后台回调（导入进度、扫描完成）也会请求布局，同样不能按 0×0 排版。
                Invoke(form, "LayoutControls");
                Check(grid.Bounds == gridBounds && sidebar.Bounds == sidebarBounds && search.Bounds == searchBounds,
                    "layout requested while minimized collapsed the controls: grid " + grid.Bounds);
                // 还原时必须按真实客户区重新排版：先人为破坏一个控件的几何，还原后应被纠正。
                grid.Size = new Size(10, 10);
                form.WindowState = FormWindowState.Normal;
                Application.DoEvents();
                Check(form.ClientSize == client, "restore changed the client size: " + form.ClientSize);
                Check(grid.Bounds == gridBounds && sidebar.Bounds == sidebarBounds && search.Bounds == searchBounds,
                    "restore did not lay out at the real size: grid " + grid.Bounds);

                // 途径二：Win+D、任务栏还原等由系统直接改窗口状态，不经过 WindowState 赋值。
                ShowWindow(form.Handle, 6);   // SW_MINIMIZE
                Application.DoEvents();
                Check(form.WindowState == FormWindowState.Minimized && form.ClientSize.IsEmpty,
                    "native minimize did not collapse the client area: " + form.ClientSize);
                Check(grid.Bounds == gridBounds && sidebar.Bounds == sidebarBounds,
                    "native minimize collapsed the laid-out controls: grid " + grid.Bounds);
                ShowWindow(form.Handle, 9);   // SW_RESTORE
                Application.DoEvents();
                Check(form.WindowState == FormWindowState.Normal && form.ClientSize == client, "native restore changed the window: " + form.ClientSize);
                Check(grid.Bounds == gridBounds && sidebar.Bounds == sidebarBounds && search.Bounds == searchBounds,
                    "native restore did not lay out at the real size: grid " + grid.Bounds);

                // 相邻边界：整屏状态下最小化再还原，仍按整屏尺寸排版。
                Invoke(form, "MaximizeOnCurrentScreen");
                Application.DoEvents();
                Rectangle maximizedBounds = form.Bounds;
                Rectangle maximizedGrid = grid.Bounds;
                Check(maximizedGrid != gridBounds, "fixture maximize did not change the grid bounds");
                form.WindowState = FormWindowState.Minimized;
                Application.DoEvents();
                Check(grid.Bounds == maximizedGrid, "minimizing a maximized window collapsed the grid: " + grid.Bounds);
                ShowWindow(form.Handle, 9);
                Application.DoEvents();
                Check(form.Bounds == maximizedBounds && grid.Bounds == maximizedGrid,
                    "restoring a maximized window lost its layout: form " + form.Bounds + " grid " + grid.Bounds);
            }
        }

        private static void TrayHideKeepsMusicPlaylist()
        {
            AppConfig config = RenameConfig("tray-music");
            config.AutoScan = false;
            config.CloseToTray = true;
            Check(config.Save(), "database fixture save failed");
            string song = Path.Combine(config.ResourceStorePath, "tray-song.mp3");
            using (MainForm form = new MainForm(config))
            {
                typeof(MainForm).GetField("scanRunning", Private).SetValue(form, true);
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-3000, -3000);
                form.Size = new Size(1000, 640);
                form.Show();
                Application.DoEvents();

                Controls.MusicPlaybackSession session = (Controls.MusicPlaybackSession)
                    typeof(MainForm).GetField("musicSession", Private).GetValue(form);
                typeof(Controls.MusicPlaybackSession).GetField("paths", Private)
                    .SetValue(session, new List<string> { song });
                typeof(Controls.MusicPlaybackSession).GetField("index", Private).SetValue(session, 0);

                Invoke(form, "HideToTray");
                Application.DoEvents();
                Check(!form.Visible, "HideToTray did not hide the form");
                Check(session.PathCount == 1 && session.Index == 0 && session.CurrentPath == song,
                    "hiding to tray stopped or cleared the music session");

                Invoke(form, "ShowFromTray");
                Application.DoEvents();
                Check(form.Visible && session.PathCount == 1 && session.CurrentPath == song,
                    "restoring from tray lost the music session");
            }
        }

        // 完整播放器打开时进入设置页：只收起播放器，不清空列表和进度；返回后迷你播放器接管同一会话
        private static void SettingsKeepsMusicPlaylist()
        {
            AppConfig config = RenameConfig("settings-music");
            config.AutoScan = false;
            Check(config.Save(), "database fixture save failed");
            string song = Path.Combine(config.ResourceStorePath, "settings-song.mp3");
            string next = Path.Combine(config.ResourceStorePath, "settings-next.mp3");
            using (MainForm form = new MainForm(config))
            {
                typeof(MainForm).GetField("scanRunning", Private).SetValue(form, true);
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-3000, -3000);
                form.Size = new Size(1200, 700);
                form.Show();
                Application.DoEvents();

                Controls.MusicPlaybackSession session = MusicSessionOf(form);
                Control player = (Control)typeof(MainForm).GetField("musicPlayer", Private).GetValue(form);
                Control mini = (Control)typeof(MainForm).GetField("miniMusic", Private).GetValue(form);
                session.Stop();   // 启动时的后台状态恢复可能先到，测试列表以这里为准
                Check(session.Restore(new List<string> { song, next }, 1, 42, 180), "fixture could not load a playlist");
                Invoke(form, "ExpandMiniMusicPlayer", 1);
                Application.DoEvents();
                Check(player.Visible, "fixture: full music player did not open");

                Invoke(form, "OpenSettings");
                Application.DoEvents();
                Check(!player.Visible, "settings page left the full music player visible");
                Check(session.PathCount == 2 && session.Index == 1 && session.CurrentPath == next && session.Position == 42,
                    "opening settings stopped or reset the music session: " + session.PathCount + " @ " + session.Position);
                Check(typeof(MainForm).GetField("stoppedMusicState", Private).GetValue(form) == null,
                    "opening settings went through the stop path");

                Invoke(form, "OpenSettings");   // 重复进入为空操作
                Invoke(form, "CloseSettings");
                Application.DoEvents();
                Check(mini.Visible, "mini player not shown after leaving settings");
                Check(session.PathCount == 2 && session.CurrentPath == next && session.Position == 42,
                    "leaving settings lost the music session");
            }
            // 关闭窗口会写下播放状态：删掉，免得后面的用例启动时恢复出这里的测试列表
            if (File.Exists(MusicResumeState.FilePath)) File.Delete(MusicResumeState.FilePath);
        }

        // 打开视频只暂停音乐：视频加载失败、视频开始播放（PlaybackStarting）都不清空音乐列表和进度
        private static void VideoKeepsMusicPlaylist()
        {
            string dir = Path.Combine(root, "video-music");
            Directory.CreateDirectory(dir);
            string song = Path.Combine(dir, "song.mp3"), video = Path.Combine(dir, "clip.mp4");
            File.WriteAllBytes(song, new byte[16]);
            File.WriteAllBytes(video, new byte[16]);
            using (MainForm form = MusicStateForm())
            {
                Application.DoEvents();
                Controls.MusicPlaybackSession session = MusicSessionOf(form);
                session.Stop();   // 启动时的后台状态恢复可能先到，测试列表以这里为准
                Check(session.Restore(new List<string> { song }, 0, 33, 120), "fixture could not load a playlist");
                AppInfo clip = new AppInfo { Key = "add:" + video, TargetPath = video, Name = "clip", IsFile = true, Source = "Added" };
                typeof(MainForm).GetField("shownApps", Private).SetValue(form, new List<AppInfo> { clip });

                Invoke(form, "ShowVideoPlayer", clip);   // 测试环境没有可用的视频内核或文件无效：加载失败
                Application.DoEvents();
                Check(session.PathCount == 1 && session.CurrentPath == song && session.Position == 33,
                    "opening a video cleared the music session: " + session.PathCount + " @ " + session.Position);

                Control player = (Control)typeof(MainForm).GetField("videoPlayer", Private).GetValue(form);
                Action starting = (Action)typeof(Controls.VideoPlayerControl).GetField("PlaybackStarting", Private).GetValue(player);
                Check(starting != null, "fixture: video PlaybackStarting has no handler");
                starting();
                Check(session.PathCount == 1 && session.CurrentPath == song && session.Position == 33 && !session.IsPlaying,
                    "video playback start cleared or kept playing the music session");
                Check(typeof(MainForm).GetField("stoppedMusicState", Private).GetValue(form) == null,
                    "video playback went through the stop path");
                Invoke(form, "HideVideoPlayer");
            }
            if (File.Exists(MusicResumeState.FilePath)) File.Delete(MusicResumeState.FilePath);
        }

        private static void CollectionSidebarTreeInteraction()
        {
            using (Form host = new Form
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-3000, -3000),
                Size = new Size(320, 420)
            })
            using (Controls.CollectionSidebar sidebar = new Controls.CollectionSidebar { Dock = DockStyle.Fill })
            {
                host.Controls.Add(sidebar);
                host.Show();
                Application.DoEvents();

                string rootKey = CollectionKeys.Make("照片", "旅行");
                string childKey = rootKey + "/2024";
                string siblingKey = CollectionKeys.Make("照片", "风景");
                List<Controls.CollectionSidebar.Item> items = new List<Controls.CollectionSidebar.Item>();
                items.Add(new Controls.CollectionSidebar.Item { IsAll = true, Name = "全部" });
                items.Add(new Controls.CollectionSidebar.Item
                {
                    Key = rootKey, Name = "旅行", Depth = 0, HasChildren = true, Expanded = false,
                    Path = "照片 / 旅行"
                });
                items.Add(new Controls.CollectionSidebar.Item
                {
                    Key = childKey, Name = "2024", Depth = 1, ParentKey = rootKey,
                    Path = "照片 / 旅行 / 2024"
                });
                items.Add(new Controls.CollectionSidebar.Item
                {
                    Key = siblingKey, Name = "风景", Depth = 0, Path = "照片 / 风景"
                });
                sidebar.SetItems(items);

                string toggledKey = null;
                bool toggledExpanded = false;
                string selectedKey = null;
                string activatedKey = null;
                sidebar.ToggleRequested += delegate(string key, bool expanded) { toggledKey = key; toggledExpanded = expanded; };
                sidebar.ItemSelected += delegate(string key) { selectedKey = key; };
                sidebar.ItemActivated += delegate(string key) { activatedKey = key; };

                Type type = typeof(Controls.CollectionSidebar);
                MethodInfo itemRect = type.GetMethod("ItemRect", Private);
                MethodInfo arrowRect = type.GetMethod("ArrowRect", Private);
                Rectangle rootRow = (Rectangle)itemRect.Invoke(sidebar, new object[] { 1 });
                Rectangle rootArrow = (Rectangle)arrowRect.Invoke(sidebar, new object[] { items[1], rootRow });
                Point arrowPoint = new Point(rootArrow.X + rootArrow.Width / 2, rootArrow.Y + rootArrow.Height / 2);
                bool arrowHit = (bool)type.GetMethod("IsArrowAt", Private).Invoke(sidebar, new object[] { arrowPoint, 1 });
                int arrowIndex = (int)type.GetMethod("IndexAt", Private).Invoke(sidebar, new object[] { arrowPoint.Y });
                type.GetMethod("OnMouseClick", Private).Invoke(sidebar,
                    new object[] { new MouseEventArgs(MouseButtons.Left, 1, arrowPoint.X, arrowPoint.Y, 0) });
                Check(toggledKey == rootKey && toggledExpanded,
                    "arrow did not request expansion; hit=" + arrowHit + " index=" + arrowIndex + " rect=" + rootArrow);
                Check(selectedKey == null && activatedKey == null, "arrow click selected or entered the collection");

                type.GetMethod("OnMouseDoubleClick", Private).Invoke(sidebar,
                    new object[] { new MouseEventArgs(MouseButtons.Left, 2, arrowPoint.X, arrowPoint.Y, 0) });
                Check(activatedKey == null, "double-clicking the arrow entered the collection");

                Rectangle siblingRow = (Rectangle)itemRect.Invoke(sidebar, new object[] { 3 });
                type.GetMethod("OnMouseDoubleClick", Private).Invoke(sidebar,
                    new object[] { new MouseEventArgs(MouseButtons.Left, 2,
                        siblingRow.X + siblingRow.Width / 2, siblingRow.Y + siblingRow.Height / 2, 0) });
                Check(activatedKey == siblingKey, "double-clicking a collection row did not enter it");

                sidebar.SetActive(rootKey);
                selectedKey = null;
                type.GetMethod("OnKeyDown", Private).Invoke(sidebar, new object[] { new KeyEventArgs(Keys.Down) });
                Check(selectedKey == childKey, "down arrow did not move selection to the first child");
                Check(!sidebar.TabStop && sidebar.CanFocus, "collection sidebar cannot receive click focus without taking Tab");

                List<Controls.CollectionSidebar.Item> scrollItems = new List<Controls.CollectionSidebar.Item>();
                scrollItems.Add(new Controls.CollectionSidebar.Item { IsAll = true, Name = "全部" });
                scrollItems.Add(new Controls.CollectionSidebar.Item
                {
                    Key = rootKey, Name = "旅行", Depth = 0, HasChildren = true, Expanded = true
                });
                scrollItems.Add(new Controls.CollectionSidebar.Item
                {
                    Key = childKey, Name = "2024", Depth = 1, ParentKey = rootKey
                });
                for (int i = 0; i < 30; i++)
                    scrollItems.Add(new Controls.CollectionSidebar.Item
                    {
                        Key = CollectionKeys.Make("照片", "项目 " + i), Name = "项目 " + i, Depth = 0
                    });
                sidebar.SetItems(scrollItems);
                int rowHeight = (int)type.GetProperty("RowH", Private).GetValue(sidebar, null);
                type.GetMethod("SetScroll", Private).Invoke(sidebar, new object[] { rowHeight * 2 + 5 });

                List<Controls.CollectionSidebar.Item> collapsed = new List<Controls.CollectionSidebar.Item>();
                collapsed.Add(new Controls.CollectionSidebar.Item { IsAll = true, Name = "全部" });
                collapsed.Add(new Controls.CollectionSidebar.Item
                {
                    Key = rootKey, Name = "旅行", Depth = 0, HasChildren = true, Expanded = false
                });
                for (int i = 0; i < 30; i++)
                    collapsed.Add(new Controls.CollectionSidebar.Item
                    {
                        Key = CollectionKeys.Make("照片", "项目 " + i), Name = "项目 " + i, Depth = 0
                    });
                sidebar.SetItems(collapsed);
                int restoredOffset = (int)type.GetField("scrollOffset", Private).GetValue(sidebar);
                Check(restoredOffset == rowHeight, "collapsing above the anchor did not fall back to the nearest visible parent");
            }
        }

        private static void CollectionTreeSearchAndInfo()
        {
            AppConfig config = RenameConfig("collection-tree-ui");
            config.AutoScan = false;
            string rootKey = CollectionKeys.Make("照片", "旅行");
            string childKey = rootKey + "/2024";
            string grandKey = childKey + "/十月";
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "旅行" });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "2024", ParentCollection = rootKey });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "十月", ParentCollection = childKey });
            AppInfo direct = AddEntry(config, "照片", Path.Combine(config.ResourceStorePath, "照片", "trip-root.png"),
                false, true, "trip root");
            AppInfo nested = AddEntry(config, "照片", Path.Combine(config.ResourceStorePath, "照片", "trip-grand.png"),
                false, true, "trip grand");
            CollectionOps.AddMember(config, direct.Key, rootKey);
            CollectionOps.AddMember(config, nested.Key, grandKey);
            Check(config.Save(), "database fixture save failed");
            List<string> directMemberships;
            Check(config.ItemCategories.TryGetValue(direct.Key, out directMemberships) && directMemberships.Contains(rootKey),
                "fixture lost the direct collection membership before publishing");
            int savedDirect = config.Database.Page(rootKey, true, true, "", 10, null, true, false).Total;
            Check(savedDirect == 1, "fixture projection has " + savedDirect + " direct members after save");

            using (MainForm form = ResourceForm(config, "照片"))
            {
                int formDirect = config.Database.Page(rootKey, true, true, "", 10, null, true, false).Total;
                Check(formDirect == 1, "ResourceForm lost the direct collection projection: " + formDirect);
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-3000, -3000);
                form.ClientSize = new Size(1000, 680);
                form.Show();
                Application.DoEvents();

                Controls.CollectionSidebar sidebar = (Controls.CollectionSidebar)typeof(MainForm).GetField("collectionSidebar", Private).GetValue(form);
                Controls.CollectionInfoPanel info = (Controls.CollectionInfoPanel)typeof(MainForm).GetField("collectionInfo", Private).GetValue(form);
                HashSet<string> expanded = (HashSet<string>)typeof(MainForm).GetField("expandedCollections", Private).GetValue(form);
                FieldInfo itemField = typeof(Controls.CollectionSidebar).GetField("items", Private);

                typeof(MainForm).GetField("currentCollection", Private).SetValue(form, null);
                typeof(MainForm).GetField("collectionEntered", Private).SetValue(form, true);
                Invoke(form, "UpdateCollectionUi");
                List<Controls.CollectionSidebar.Item> visible = (List<Controls.CollectionSidebar.Item>)itemField.GetValue(sidebar);
                Check(visible.Count == 2, "collapsed collection tree should show only the root collection");

                Invoke(form, "OnCollectionToggleRequested", rootKey, true);
                visible = (List<Controls.CollectionSidebar.Item>)itemField.GetValue(sidebar);
                Check(visible.Count == 3, "expanding the root did not reveal its direct child");
                Invoke(form, "OnCollectionToggleRequested", childKey, true);
                visible = (List<Controls.CollectionSidebar.Item>)itemField.GetValue(sidebar);
                Check(visible.Count == 4, "expanding the child did not reveal the grandchild");
                Check(visible[1].Depth == 0 && visible[2].Depth == 1 && visible[3].Depth == 2,
                    "collection rows lost nested depth");
                Check(visible[3].ParentKey == childKey && visible[3].Path == "照片 / 旅行 / 2024 / 十月",
                    "collection row path or parent key is wrong");

                expanded.Clear();
                typeof(MainForm).GetField("currentCollection", Private).SetValue(form, grandKey);
                typeof(MainForm).GetField("collectionEntered", Private).SetValue(form, true);
                typeof(MainForm).GetField("revealActiveCollection", Private).SetValue(form, true);
                Invoke(form, "UpdateCollectionUi");
                visible = (List<Controls.CollectionSidebar.Item>)itemField.GetValue(sidebar);
                Check(visible.Count == 4 && expanded.Contains(rootKey) && expanded.Contains(childKey),
                    "selecting a deep collection did not expand its ancestors before building rows");

                typeof(MainForm).GetField("currentCollection", Private).SetValue(form, rootKey);
                typeof(MainForm).GetField("collectionEntered", Private).SetValue(form, false);
                typeof(MainForm).GetField("revealActiveCollection", Private).SetValue(form, false);
                Invoke(form, "UpdateCollectionUi");
                Check(info.Visible, "collection info page did not open");
                int infoCount = (int)typeof(Controls.CollectionInfoPanel).GetField("count", Private).GetValue(info);
                int pageCount = config.Database.Page(rootKey, true, true, "", 10, null, true, false).Total;
                Check(infoCount == 1,
                    "collection info counted descendants as direct members; info=" + infoCount + " page=" + pageCount);
                Check((int)typeof(Controls.CollectionInfoPanel).GetField("childFolderCount", Private).GetValue(info) == 1,
                    "collection info did not show the direct child folder count");

                typeof(MainForm).GetField("currentCollection", Private).SetValue(form, grandKey);
                Invoke(form, "UpdateCollectionUi");
                Check((string)typeof(Controls.CollectionInfoPanel).GetField("parent", Private).GetValue(info) == "照片 / 旅行 / 2024",
                    "nested collection info did not show the full parent path");

                typeof(MainForm).GetField("currentCollection", Private).SetValue(form, rootKey);
                MethodInfo readPage = typeof(MainForm).GetMethod("ReadDatabasePage", Private);
                List<AppInfo> found = (List<AppInfo>)readPage.Invoke(form, new object[] { "trip" });
                Check(found.Count == 1 && found[0].Name == "trip root", "collection search included a descendant member");
                typeof(MainForm).GetField("currentCollection", Private).SetValue(form, null);
                found = (List<AppInfo>)readPage.Invoke(form, new object[] { "trip" });
                Check(found.Count == 2, "category search no longer finds all direct category members");
            }
        }

        private static void CollectionCreateScopeAndLimits()
        {
            AppConfig config = RenameConfig("collection-create-scope");
            config.AutoScan = false;
            string rootKey = CollectionKeys.Make("照片", "旅行");
            string childKey = rootKey + "/2024";
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "旅行" });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "2024", ParentCollection = rootKey });
            Check(config.Save(), "database fixture save failed");

            using (MainForm form = ResourceForm(config, "照片"))
            {
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-3000, -3000);
                form.ClientSize = new Size(1000, 680);
                form.Show();
                Application.DoEvents();

                Controls.CollectionSidebar sidebar = (Controls.CollectionSidebar)typeof(MainForm).GetField("collectionSidebar", Private).GetValue(form);
                Controls.SearchField search = (Controls.SearchField)typeof(MainForm).GetField("search", Private).GetValue(form);
                PropertyInfo active = typeof(MainForm).GetProperty("ActiveCollectionKey", Private);
                typeof(MainForm).GetField("currentCollection", Private).SetValue(form, rootKey);
                typeof(MainForm).GetField("collectionEntered", Private).SetValue(form, true);
                Invoke(form, "UpdateCollectionUi");
                Check(sidebar.Visible, "collection sidebar did not become visible");
                Check((string)active.GetValue(form, null) == rootKey, "entered collection was not the active creation scope");

                search.Text = "trip";
                Check((string)active.GetValue(form, null) == null, "search mode still used the collection as creation scope");
                search.Text = string.Empty;
                typeof(MainForm).GetField("collectionEntered", Private).SetValue(form, false);
                Invoke(form, "UpdateCollectionUi");
                Check((string)active.GetValue(form, null) == null, "collection info page still used the collection as creation scope");

                Func<string, string, string> check = delegate(string name, string parentCollection)
                {
                    return CollectionOps.ValidateDraft(config, "照片", parentCollection, name, null);
                };
                Check(check("2024", rootKey) != null, "same-parent duplicate name was accepted");
                Check(check("2024", childKey) == null, "same name under a different parent was rejected");

                string depth15 = rootKey;
                for (int depth = 2; depth <= 15; depth++) depth15 += "/L" + depth;
                Check(check("L16", depth15) == null, "collection at the 16th level was rejected");
                Check(check("L17", depth15 + "/L16") != null, "collection beyond the 16th level was accepted");

                string longParent = rootKey;
                for (int depth = 0; depth < 26; depth++) longParent += "/" + new string('x', 80);
                string longError = check("y", longParent);
                Check(longError != null && longError.Contains("过长"), "overlong collection key was not rejected");
            }
        }

        // 编辑页的校验：改名、换位置、两者一起改，以及不能放进自己的子树。
        private static void CollectionDraftValidation()
        {
            AppConfig config = new AppConfig();
            string rootA = CollectionKeys.Make("照片", "旅行");
            string rootB = CollectionKeys.Make("照片", "风景");
            string childA = rootA + "/2024";
            ResourceCollection travel = new ResourceCollection { Parent = "照片", Name = "旅行" };
            ResourceCollection year = new ResourceCollection { Parent = "照片", Name = "2024", ParentCollection = rootA };
            config.Collections.Add(travel);
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "风景" });
            config.Collections.Add(year);
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "2025", ParentCollection = rootA });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "十月", ParentCollection = childA });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "2024", ParentCollection = rootB });
            config.Collections.Add(new ResourceCollection { Parent = "文档", Name = "旅行" });

            Check(CollectionOps.ValidateDraft(config, "照片", rootA, "2024", year) == null, "an unchanged collection was rejected");
            Check(CollectionOps.ValidateDraft(config, "照片", string.Empty, "旅 行", travel) == null, "a plain rename was rejected");
            Check(CollectionOps.ValidateDraft(config, "照片", string.Empty, "旅行", null) != null, "creating a duplicate root name was accepted");
            Check(CollectionOps.ValidateDraft(config, "文档", string.Empty, "风景", null) == null, "a name used in another category was rejected");
            Check(CollectionOps.ValidateDraft(config, "照片", rootA, " 2025 ", year) != null, "renaming onto a sibling was accepted");
            Check(CollectionOps.ValidateDraft(config, "照片", rootA, "  ", year) != null, "a blank name was accepted");
            Check(CollectionOps.ValidateDraft(config, "照片", rootA, "a/b", year) != null, "a name with a slash was accepted");

            // 只改大小写不算和自己重名
            ResourceCollection lower = new ResourceCollection { Parent = "照片", Name = "abc" };
            config.Collections.Add(lower);
            Check(CollectionOps.ValidateDraft(config, "照片", string.Empty, "ABC", lower) == null, "a case-only rename was rejected");
            Check(CollectionOps.ValidateDraft(config, "照片", string.Empty, "ABC", null) != null, "a case-insensitive duplicate was accepted");

            // 换位置：目标里有同名的要拦住；同时改名可以避开；原位置有新名字时要求分两步
            Check(CollectionOps.ValidateDraft(config, "照片", rootB, "2024", year) != null, "moving onto a same-named sibling was accepted");
            Check(CollectionOps.ValidateDraft(config, "照片", rootB, "2026", year) == null, "moving with a new free name was rejected");
            string twoSteps = CollectionOps.ValidateDraft(config, "照片", string.Empty, "2025", year);
            Check(twoSteps != null && twoSteps.Contains("原来的位置"), "rename plus move was accepted while the old parent still has the new name");
            Check(CollectionOps.ValidateDraft(config, "照片", string.Empty, "2024", year) == null, "moving to the top level was rejected");

            // 不能放进自己或自己的子文件夹
            Check(CollectionOps.ValidateDraft(config, "照片", rootA, "旅行", travel) != null, "moving a collection into itself was accepted");
            Check(CollectionOps.ValidateDraft(config, "照片", childA + "/十月", "旅行", travel) != null, "moving a collection into its descendant was accepted");

            // 整棵子树都要在层级上限内：2024 下面还有「十月」，放到第 15 层会让「十月」落到第 17 层
            string depth14 = rootB;
            for (int depth = 2; depth <= 14; depth++) depth14 += "/L" + depth;
            Check(CollectionOps.ValidateDraft(config, "照片", depth14, "2024", year) == null, "a subtree ending at the 16th level was rejected");
            string deepError = CollectionOps.ValidateDraft(config, "照片", depth14 + "/L15", "2024", year);
            Check(deepError != null && deepError.Contains("层级"), "a subtree beyond the 16th level was accepted");
        }

        // 编辑页：新建（重名拦截、连续新建、换位置）、取消、后台刷新不关页、编辑时改名加移动一次保存、保存失败可重试。
        private static void CollectionEditPageFlow()
        {
            AppConfig config = RenameConfig("collection-edit-page");
            config.AutoScan = false;
            string rootA = CollectionKeys.Make("照片", "P旅行");
            string rootB = CollectionKeys.Make("照片", "P风景");
            string childA = rootA + "/P2024";
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "P旅行" });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "P风景" });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "P2024", ParentCollection = rootA });
            AppInfo item = AddEntry(config, "照片",
                Path.Combine(config.ResourceStorePath, "照片", "edit-page-photo.png"), false, true, "edit page photo");
            CollectionOps.AddMember(config, item.Key, childA);
            Check(config.Save(), "collection edit page fixture save failed");
            long childId;
            using (Sqlite db = new Sqlite(config.Database.FilePath, false)) childId = GroupIdAt(db, childA);

            using (MainForm form = ResourceForm(config, "照片", item))
            {
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-3000, -3000);
                form.ClientSize = new Size(1000, 680);
                form.Show();
                Application.DoEvents();

                Type formType = typeof(MainForm), panelType = typeof(Controls.CollectionInfoPanel);
                Controls.CollectionInfoPanel info = (Controls.CollectionInfoPanel)formType.GetField("collectionInfo", Private).GetValue(form);
                Controls.AppGridControl grid = (Controls.AppGridControl)formType.GetField("grid", Private).GetValue(form);
                Controls.SearchField search = (Controls.SearchField)formType.GetField("search", Private).GetValue(form);
                HashSet<string> expanded = (HashSet<string>)formType.GetField("expandedCollections", Private).GetValue(form);
                FieldInfo current = formType.GetField("currentCollection", Private);
                FieldInfo entered = formType.GetField("collectionEntered", Private);
                MethodInfo create = formType.GetMethod("CreateCollectionUnder", Private);
                MethodInfo submit = panelType.GetMethod("Submit", Private);
                FieldInfo issue = panelType.GetField("nameIssue", Private);
                Action<string> begin = delegate(string parentKey) { create.Invoke(form, new object[] { parentKey, null }); };
                Func<TextBox> nameBox = delegate { return (TextBox)panelType.GetField("nameBox", Private).GetValue(info); };
                Func<TextBox> descBox = delegate { return (TextBox)panelType.GetField("descBox", Private).GetValue(info); };

                current.SetValue(form, null);
                entered.SetValue(form, true);
                Invoke(form, "UpdateCollectionUi");
                Check(!info.Visible && grid.Visible, "fixture did not start on the content grid");

                // 新建页盖住网格；重名立刻有提示，提交不落库
                begin(rootA);
                Check(info.IsCreating && info.Visible && !grid.Visible, "create page did not replace the grid");
                Check(info.EditParentKey == rootA, "create page did not start at the requested parent");
                long revision = config.DatabaseRevision;
                nameBox().Text = "p2024";
                Check(issue.GetValue(info) != null, "duplicate name was not flagged while typing");
                submit.Invoke(info, new object[] { false });
                Check(info.IsCreating && config.DatabaseRevision == revision, "duplicate name was saved");

                // 连续新建：建好后留在新建页，位置保留、其余清空
                nameBox().Text = " P十月 ";
                descBox().Text = "国庆";
                Check(issue.GetValue(info) == null, "free name was flagged");
                submit.Invoke(info, new object[] { true });
                ResourceCollection october = CollectionOps.FindByKey(config, rootA + "/P十月");
                Check(october != null && october.Description == "国庆" && config.DatabaseRevision > revision,
                    "create-and-continue did not save the collection");
                Check(info.IsCreating && nameBox().Text.Length == 0 && descBox().Text.Length == 0 && info.EditParentKey == rootA,
                    "create-and-continue did not reset the page for the next collection");
                Check(current.GetValue(form) == null && expanded.Contains(rootA), "create-and-continue changed the selection or left the parent collapsed");

                // 后台刷新不关新建页；搜索词一变就放弃
                nameBox().Text = "草稿";
                Invoke(form, "RefreshGrid");
                Check(info.IsCreating && nameBox().Text == "草稿", "a background refresh closed the create page");
                search.Text = "x";
                Invoke(form, "RefreshGrid");
                Check(!info.IsEditing, "starting a search kept the create page open");
                search.Text = string.Empty;
                Invoke(form, "RefreshGrid");

                // 新建页开着时再点新建：只换位置；换到最外层后创建并选中
                begin(rootA);
                nameBox().Text = "P新根";
                begin(string.Empty);
                Check(info.IsCreating && info.EditParentKey.Length == 0 && nameBox().Text == "P新根",
                    "re-opening the create page lost the typed name or kept the old location");
                submit.Invoke(info, new object[] { false });
                string newRoot = CollectionKeys.Make("照片", "P新根");
                Check(CollectionOps.FindByKey(config, newRoot) != null && !info.IsEditing, "create did not save or did not close the page");
                Check((string)current.GetValue(form) == newRoot && !(bool)entered.GetValue(form) && info.Visible,
                    "created collection was not selected on its info page");

                // 取消：回到打开前的内容（信息页 / 网格）
                begin(newRoot);
                info.CancelEdit();
                Check(!info.IsEditing && info.Visible && !grid.Visible, "cancel from the info page did not restore it");
                entered.SetValue(form, true);
                Invoke(form, "RefreshGrid");
                Check(!info.Visible && grid.Visible, "entering the collection did not show its contents");
                begin(newRoot);
                Check(info.IsCreating && !grid.Visible, "create page did not open over the collection contents");
                info.CancelEdit();
                Check(!info.Visible && grid.Visible, "cancel from the contents did not restore the grid");
                Check(CollectionOps.FindByKey(config, newRoot + "/草稿") == null, "cancelled drafts were saved");

                // 编辑：改名 + 换位置一次保存，子树、成员和数据库里的分组 ID 都跟着走
                Invoke(form, "EditCollection", CollectionOps.FindByKey(config, childA));
                Check(info.IsEditing && !info.IsCreating && info.EditParentKey == rootA && nameBox().Text == "P2024",
                    "edit page did not load the collection");
                revision = config.DatabaseRevision;
                nameBox().Text = "P改名";
                info.SetEditLocation(rootB);
                submit.Invoke(info, new object[] { false });
                string movedKey = rootB + "/P改名";
                Check(!info.IsEditing && CollectionOps.FindByKey(config, movedKey) != null
                    && CollectionOps.FindByKey(config, childA) == null, "rename plus move was not applied");
                Check((string)current.GetValue(form) == movedKey && expanded.Contains(rootB), "rename plus move lost the selection or left the new parent collapsed");
                Check(config.ItemCategories[item.Key].Contains(movedKey) && !config.ItemCategories[item.Key].Contains(childA),
                    "rename plus move did not migrate the member");
                AppConfig reloaded = AppConfig.Load();
                Check(CollectionOps.FindByKey(reloaded, movedKey) != null && CollectionOps.FindByKey(reloaded, childA) == null
                    && reloaded.ItemCategories[item.Key].Contains(movedKey), "rename plus move was not persisted");
                using (Sqlite db = new Sqlite(config.Database.FilePath, false))
                    Check(GroupIdAt(db, movedKey) == childId, "rename plus move replaced the database group instead of updating it");

                // 只改大小写可以保存
                Invoke(form, "EditCollection", CollectionOps.FindByKey(config, movedKey));
                nameBox().Text = "p改名";
                Check(issue.GetValue(info) == null, "case-only rename was flagged as a duplicate");
                submit.Invoke(info, new object[] { false });
                string casedKey = rootB + "/p改名";
                Check(!info.IsEditing && CollectionOps.FindByKey(AppConfig.Load(), casedKey) != null, "case-only rename was not persisted");

                // 保存失败：数据不变，编辑页和已填内容保留，解除占用后直接重试成功
                Invoke(form, "EditCollection", CollectionOps.FindByKey(config, casedKey));
                revision = config.DatabaseRevision;
                nameBox().Text = "P重试";
                descBox().Text = "失败后还在";
                using (Sqlite db = new Sqlite(config.Database.FilePath, false))
                {
                    db.Execute("BEGIN IMMEDIATE");
                    try { submit.Invoke(info, new object[] { false }); }
                    finally { db.Execute("ROLLBACK"); }
                }
                Check(info.IsEditing && nameBox().Text == "P重试" && descBox().Text == "失败后还在",
                    "a failed save closed the edit page or lost the typed content");
                Check(config.DatabaseRevision == revision && CollectionOps.FindByKey(config, casedKey) != null
                    && CollectionOps.FindByKey(config, rootB + "/P重试") == null, "a failed save changed the collection tree");
                submit.Invoke(info, new object[] { false });
                ResourceCollection retried = CollectionOps.FindByKey(config, rootB + "/P重试");
                Check(!info.IsEditing && retried != null && retried.Description == "失败后还在" && config.DatabaseRevision > revision,
                    "retry after a failed save did not succeed");
            }
        }

        // 编辑页的几处保护：带着待加入资源新建时换位置不丢资源；编辑到一半再新建要先确认；新建先退出查看器；
        // 点「编辑」和取消编辑不重新注入信息页；封面存不下 / 保存失败不留孤儿文件；位置菜单不让选自己和子文件夹。
        private static void CollectionEditPageGuards()
        {
            AppConfig config = RenameConfig("collection-edit-guards");
            config.AutoScan = false;
            string rootA = CollectionKeys.Make("照片", "G旅行");
            string rootB = CollectionKeys.Make("照片", "G风景");
            string childA = rootA + "/G2024";
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "G旅行" });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "G风景" });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "G2024", ParentCollection = rootA, Description = "原简介" });
            AppInfo first = AddEntry(config, "照片", Path.Combine(config.ResourceStorePath, "照片", "guard-1.png"), false, true, "guard 1");
            AppInfo second = AddEntry(config, "照片", Path.Combine(config.ResourceStorePath, "照片", "guard-2.png"), false, true, "guard 2");
            Check(config.Save(), "collection edit guards fixture save failed");

            using (MainForm form = ResourceForm(config, "照片", first, second))
            {
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-3000, -3000);
                form.ClientSize = new Size(1000, 680);
                form.Show();
                Application.DoEvents();

                Type formType = typeof(MainForm), panelType = typeof(Controls.CollectionInfoPanel);
                Controls.CollectionInfoPanel info = (Controls.CollectionInfoPanel)formType.GetField("collectionInfo", Private).GetValue(form);
                Control imageViewer = (Control)formType.GetField("imageViewer", Private).GetValue(form);
                FieldInfo current = formType.GetField("currentCollection", Private);
                FieldInfo entered = formType.GetField("collectionEntered", Private);
                MethodInfo create = formType.GetMethod("CreateCollectionUnder", Private);
                MethodInfo submit = panelType.GetMethod("Submit", Private);
                Func<TextBox> nameBox = delegate { return (TextBox)panelType.GetField("nameBox", Private).GetValue(info); };
                Func<TextBox> descBox = delegate { return (TextBox)panelType.GetField("descBox", Private).GetValue(info); };
                Action<string> begin = delegate(string parentKey) { create.Invoke(form, new object[] { parentKey, null }); };

                current.SetValue(form, null);
                entered.SetValue(form, true);
                Invoke(form, "RefreshGrid");

                // 带着两项资源新建：再点一次「新建」只换位置，名称和待加入的资源都还在；创建后一起落库、不留在新建页
                create.Invoke(form, new object[] { rootA, new List<AppInfo> { first, second } });
                Check(info.IsCreating && (int)panelType.GetField("joinCount", Private).GetValue(info) == 2,
                    "create page did not take the items to join");
                nameBox().Text = "G合集";
                begin(rootB);
                Check(info.IsCreating && nameBox().Text == "G合集" && info.EditParentKey == rootB
                    && (int)panelType.GetField("joinCount", Private).GetValue(info) == 2,
                    "re-opening the create page dropped the typed name or the items to join");
                submit.Invoke(info, new object[] { true });
                string joinedKey = rootB + "/G合集";
                Check(CollectionOps.FindByKey(config, joinedKey) != null && !info.IsEditing,
                    "creating with items to join did not save or stayed on the create page");
                Check(config.ItemCategories[first.Key].Contains(joinedKey) && config.ItemCategories[second.Key].Contains(joinedKey),
                    "items were not added to the new collection");
                Check(current.GetValue(form) == null, "creating with items to join changed the selection");
                AppConfig reloaded = AppConfig.Load();
                Check(CollectionOps.FindByKey(reloaded, joinedKey) != null && reloaded.ItemCategories[first.Key].Contains(joinedKey),
                    "collection and members were not persisted together");

                // 新建先退出查看器，不把它盖在下面
                imageViewer.Visible = true;
                begin(rootA);
                Check(info.IsCreating && !imageViewer.Visible, "create page left the image viewer open underneath");

                // 封面存不下：停在新建页并提示，不落库
                string coverDir = Path.Combine(Paths.DataDir, "CollectionCovers");
                int coversBefore = Directory.Exists(coverDir) ? Directory.GetFiles(coverDir).Length : 0;
                long revision = config.DatabaseRevision;
                nameBox().Text = "G封面";
                MethodInfo paste = panelType.GetMethod("SetPastedCover", Private);
                paste.Invoke(info, new object[] { new Bitmap(40, 30) });
                string realData = Paths.DataDirOverride;
                string blocked = Path.Combine(root, "cover-blocked-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(blocked);
                File.WriteAllText(Path.Combine(blocked, "CollectionCovers"), "not a folder");
                Paths.DataDirOverride = blocked;
                try { submit.Invoke(info, new object[] { false }); }
                finally { Paths.DataDirOverride = realData; }
                Check(info.IsCreating && nameBox().Text == "G封面" && config.DatabaseRevision == revision
                    && panelType.GetField("notice", Private).GetValue(info) != null,
                    "an unsaved pasted cover did not stop the create with a notice");

                // 保存失败：不留孤儿封面文件，内容保留；重试成功后封面文件在
                using (Sqlite db = new Sqlite(config.Database.FilePath, false))
                {
                    db.Execute("BEGIN IMMEDIATE");
                    try { submit.Invoke(info, new object[] { false }); }
                    finally { db.Execute("ROLLBACK"); }
                }
                // 「保存失败」的提示是排进消息队列的：在这里让它显示并关掉。留到后面，它会叠在下一个确认框上面，
                // 而确认框的点击只认最先打开的那个，上面这个没人关，用例就一直卡住。
                Check(AnswerMessageDialog("保存失败", 2, delegate { Application.DoEvents(); }), "a failed create did not tell the user");
                Check(info.IsCreating && nameBox().Text == "G封面" && CollectionOps.FindByKey(config, rootA + "/G封面") == null,
                    "a failed create closed the page or saved the collection");
                Check((Directory.Exists(coverDir) ? Directory.GetFiles(coverDir).Length : 0) == coversBefore,
                    "a failed create left an orphan cover file");
                submit.Invoke(info, new object[] { false });
                ResourceCollection covered = CollectionOps.FindByKey(config, rootA + "/G封面");
                Check(covered != null && File.Exists(covered.Cover) && Directory.GetFiles(coverDir).Length == coversBefore + 1,
                    "retrying the create did not save the collection with its pasted cover");

                // 信息页点「编辑」再取消：不重新注入信息页（改过的计数还在）
                Invoke(form, "OnCollectionSelected", childA);
                Check(info.Visible && !info.IsEditing, "fixture did not show the collection info page");
                FieldInfo count = panelType.GetField("count", Private);
                count.SetValue(info, 999);
                panelType.GetMethod("RequestEdit", Private).Invoke(info, null);
                Check(info.IsEditing && !info.IsCreating && info.EditParentKey == rootA && nameBox().Text == "G2024",
                    "the edit button did not open the edit page in place");
                Check(!info.HasUnsavedEdit, "an untouched edit page reported unsaved changes");
                info.CancelEdit();
                Check(!info.IsEditing && info.Visible && (int)count.GetValue(info) == 999,
                    "edit and cancel rebuilt the info page");

                // 没改动时新建不打扰；改过内容再新建要先确认：取消则留在编辑页，确定才打开新建页
                panelType.GetMethod("RequestEdit", Private).Invoke(info, null);
                begin(rootA);
                Check(info.IsCreating, "an untouched edit page blocked creating");
                info.CancelEdit();
                panelType.GetMethod("RequestEdit", Private).Invoke(info, null);
                descBox().Text = "改到一半";
                Check(info.HasUnsavedEdit, "a changed description was not reported as unsaved");
                ClickConfirmation(delegate { begin(rootA); }, false);
                Check(info.IsEditing && !info.IsCreating && descBox().Text == "改到一半",
                    "declining the confirmation dropped the unsaved edit");
                ClickConfirmation(delegate { begin(rootA); }, true);
                Check(info.IsCreating, "accepting the confirmation did not open the create page");
                info.CancelEdit();
                Check(CollectionOps.FindByKey(config, childA).Description == "原简介", "the dropped edit was saved");

                // 位置菜单：自己和子文件夹不能选；有子文件夹的悬停才展开；选中即换位置
                Invoke(form, "EditCollection", CollectionOps.FindByKey(config, rootA));
                MethodInfo buildMenu = formType.GetMethod("BuildLocationMenu", Private);
                List<MenuWindow.MenuItem> menu = (List<MenuWindow.MenuItem>)buildMenu.Invoke(form,
                    new object[] { new CollectionIndex(config.Collections), null, string.Empty, rootA });
                MenuWindow.MenuItem self = menu.Find(delegate(MenuWindow.MenuItem m) { return m.Text.StartsWith("G旅行", StringComparison.Ordinal); });
                MenuWindow.MenuItem other = menu.Find(delegate(MenuWindow.MenuItem m) { return m.Text == "G风景"; });
                Check(self != null && self.Disabled && self.SubItemsFactory == null && self.Action == null,
                    "the collection being edited can be picked as its own location");
                Check(other != null && !other.Disabled && other.SubItemsFactory != null, "a collection with subfolders does not expand");
                List<MenuWindow.MenuItem> sub = other.SubItemsFactory();
                Check(sub.Count == 3 && sub[0].Action != null && sub[1].Separator && sub[2].Text == "G合集",
                    "the submenu does not offer the collection itself plus its subfolders");
                sub[2].Action();
                Check(info.EditParentKey == joinedKey, "picking a location did not change the edit page");
                info.CancelEdit();
            }
        }

        private static void CollectionDeleteFallback()
        {
            AppConfig config = RenameConfig("collection-delete-fallback");
            config.AutoScan = false;
            string rootKey = CollectionKeys.Make("照片", "旅行");
            string childKey = rootKey + "/2024";
            string grandKey = childKey + "/十月";
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "旅行" });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "2024", ParentCollection = rootKey });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "十月", ParentCollection = childKey });
            Check(config.Save(), "database fixture save failed");

            using (MainForm form = ResourceForm(config, "照片"))
            {
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-3000, -3000);
                form.ClientSize = new Size(1000, 680);
                form.Show();
                Application.DoEvents();

                Controls.CollectionSidebar sidebar = (Controls.CollectionSidebar)typeof(MainForm).GetField("collectionSidebar", Private).GetValue(form);
                HashSet<string> expanded = (HashSet<string>)typeof(MainForm).GetField("expandedCollections", Private).GetValue(form);
                typeof(MainForm).GetField("currentCollection", Private).SetValue(form, grandKey);
                typeof(MainForm).GetField("collectionEntered", Private).SetValue(form, true);
                typeof(MainForm).GetField("revealActiveCollection", Private).SetValue(form, true);
                Invoke(form, "UpdateCollectionUi");
                Check(expanded.Contains(rootKey) && expanded.Contains(childKey), "fixture did not reveal the nested collection");

                ResourceCollection child = CollectionOps.FindByKey(config, childKey);
                Check(child != null, "fixture lost the parent collection");
                ClickConfirmation(delegate { Invoke(form, "DeleteCollection", child); }, true);

                Check((string)typeof(MainForm).GetField("currentCollection", Private).GetValue(form) == rootKey,
                    "deleting the current collection's parent did not navigate to the nearest surviving ancestor");
                Check((bool)typeof(MainForm).GetField("collectionEntered", Private).GetValue(form),
                    "fallback ancestor did not open its content view");
                Check((string)typeof(Controls.CollectionSidebar).GetField("activeKey", Private).GetValue(sidebar) == rootKey,
                    "fallback ancestor was not selected in the collection sidebar");
                Check(!expanded.Contains(childKey) && !expanded.Contains(grandKey),
                    "deleted collection keys remained expanded");
                Check(CollectionOps.FindByKey(config, childKey) == null && CollectionOps.FindByKey(config, grandKey) == null,
                    "deleted subtree remained in memory");
                AppConfig loaded = AppConfig.Load();
                Check(CollectionOps.FindByKey(loaded, rootKey) != null
                    && CollectionOps.FindByKey(loaded, childKey) == null
                    && CollectionOps.FindByKey(loaded, grandKey) == null,
                    "deleted subtree was not persisted correctly");

                ResourceCollection root = CollectionOps.FindByKey(config, rootKey);
                Check(root != null, "fixture lost the root collection");
                ClickConfirmation(delegate { Invoke(form, "DeleteCollection", root); }, true);
                Check((string)typeof(MainForm).GetField("currentCollection", Private).GetValue(form) == null,
                    "deleting the whole tree did not return to all collections");
                Check(config.Collections.Count == 0, "top-level deletion left collection definitions behind");
                Check(AppConfig.Load().Collections.Count == 0, "top-level deletion was not persisted");
            }
        }

        private static CollectionMoveRequest MoveRequest(CollectionMoveKind kind, string source, string target)
        {
            CollectionMoveRequest request = new CollectionMoveRequest();
            request.Kind = kind;
            request.SourceKey = source ?? string.Empty;
            request.TargetKey = target ?? string.Empty;
            return request;
        }

        private static CollectionMovePlan PlanMove(AppConfig config, CollectionMoveKind kind, string source, string target)
        {
            CollectionMovePlan plan;
            string error;
            Check(CollectionOps.TryBuildMovePlan(config, MoveRequest(kind, source, target), out plan, out error),
                "collection move plan failed: " + error);
            Check(plan != null, "collection move plan is null");
            return plan;
        }

        private static void RejectMove(AppConfig config, CollectionMoveKind kind, string source, string target, string messagePart)
        {
            CollectionMovePlan plan;
            string error;
            Check(!CollectionOps.TryBuildMovePlan(config, MoveRequest(kind, source, target), out plan, out error),
                "invalid collection move was accepted: " + kind + " " + source + " -> " + target);
            Check(error != null && error.IndexOf(messagePart, StringComparison.Ordinal) >= 0,
                "collection move error did not mention " + messagePart + ": " + error);
        }

        private static List<string> CollectionChildNames(AppConfig config, string category, string parentCollection)
        {
            List<string> names = new List<string>();
            for (int i = 0; i < config.Collections.Count; i++)
            {
                ResourceCollection c = config.Collections[i];
                if (c != null && c.Parent == category
                    && string.Equals(c.ParentCollection ?? string.Empty, parentCollection ?? string.Empty,
                        StringComparison.Ordinal))
                    names.Add(c.Name);
            }
            return names;
        }

        private static long GroupIdAt(Sqlite db, string key)
        {
            string value = db.Scalar("SELECT id FROM groups WHERE key=?", key);
            Check(value != null, "missing group: " + key);
            return long.Parse(value);
        }

        private static long OperationCount(Sqlite db)
        {
            return long.Parse(db.Scalar("SELECT count(*) FROM operations"));
        }

        private static void CollectionMoveGuards()
        {
            AppConfig config = RenameConfig("collection-move-guards");
            config.AutoScan = false;
            string rootA = CollectionKeys.Make("照片", "旅行");
            string rootB = CollectionKeys.Make("照片", "风景");
            string rootC = CollectionKeys.Make("照片", "人物");
            string childA = rootA + "/2024";
            string grandA = childA + "/十月";
            string childB = rootB + "/2025";
            string childC = rootC + "/2024";
            string videoTarget = CollectionKeys.Make("视频", "其他");
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "旅行" });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "风景" });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "人物" });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "2024", ParentCollection = rootA });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "十月", ParentCollection = childA });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "2025", ParentCollection = rootB });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "2024", ParentCollection = rootC });
            config.Collections.Add(new ResourceCollection { Parent = "视频", Name = "其他" });

            string deepParent = string.Empty;
            string deepKey = null;
            for (int i = 1; i <= CollectionKeys.MaxDepth; i++)
            {
                ResourceCollection node = new ResourceCollection
                {
                    Parent = "照片",
                    Name = "D" + i.ToString(),
                    ParentCollection = deepParent
                };
                config.Collections.Add(node);
                deepKey = CollectionKeys.GetKey(node);
                deepParent = deepKey;
            }
            string deepSource = CollectionKeys.Make("照片", "深度源");
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "深度源" });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "子", ParentCollection = deepSource });

            string longParentName = "很长的目标父集合";
            string longParent = CollectionKeys.Make("照片", longParentName);
            string longName = new string('x', CollectionKeys.MaxKeyLength - 10);
            string longSource = CollectionKeys.Make("照片", longName);
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = longParentName });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = longName });
            Check(config.Save(), "collection move guard fixture save failed");
            long revision = config.DatabaseRevision;
            long operations;
            using (Sqlite db = new Sqlite(config.Database.FilePath, false)) operations = OperationCount(db);

            RejectMove(config, CollectionMoveKind.After, "col:照片/不存在", rootA, "源集合");
            RejectMove(config, CollectionMoveKind.After, rootA, "col:照片/不存在", "目标集合");
            RejectMove(config, CollectionMoveKind.Into, rootA, videoTarget, "资源分类");
            RejectMove(config, CollectionMoveKind.Into, childA, grandA, "自身或子集合");
            RejectMove(config, CollectionMoveKind.After, childA, grandA, "自身或子集合");
            RejectMove(config, CollectionMoveKind.Into, childA, rootC, "同名");
            RejectMove(config, CollectionMoveKind.Into, longSource, longParent, "过长");
            RejectMove(config, CollectionMoveKind.Into, deepSource, deepKey, "层级");

            CollectionMovePlan noOp = PlanMove(config, CollectionMoveKind.Before, rootA, rootB);
            Check(noOp.NoOp && !noOp.ChangesParent && noOp.KeyMap.Count == 0,
                "same-parent insert before the next sibling was not a no-op");
            noOp = PlanMove(config, CollectionMoveKind.After, rootB, rootA);
            Check(noOp.NoOp && !noOp.ChangesParent && noOp.KeyMap.Count == 0,
                "same-parent insert after the previous sibling was not a no-op");
            noOp = PlanMove(config, CollectionMoveKind.Before, rootA, rootA);
            Check(noOp.NoOp, "dropping a collection onto itself was not a no-op");
            noOp = PlanMove(config, CollectionMoveKind.Into, childA, rootA);
            Check(noOp.NoOp, "dropping into the current parent was not a no-op");
            noOp = PlanMove(config, CollectionMoveKind.ToRoot, rootA, null);
            Check(noOp.NoOp, "dropping a root collection on all was not a no-op");
            CollectionOps.ApplyMovePlan(config, noOp);
            Check(config.DatabaseRevision == revision && string.Join("|", CollectionChildNames(config, "照片", string.Empty).ToArray())
                == "旅行|风景|人物|D1|深度源|" + longParentName + "|" + longName,
                "a no-op changed the collection order or revision");
            using (Sqlite db = new Sqlite(config.Database.FilePath, false))
                Check(OperationCount(db) == operations, "a no-op created an audit operation");

            string persisted = AppConfig.Load().ToDocument().OuterXml;
            long childAId;
            using (Sqlite db = new Sqlite(config.Database.FilePath, false))
                childAId = GroupIdAt(db, childA);
            CollectionMovePlan plan = PlanMove(config, CollectionMoveKind.Into, childA, rootB);
            string movedChild = rootB + "/2024";
            using (Sqlite db = new Sqlite(config.Database.FilePath, false))
            {
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    CollectionOps.ApplyMovePlan(config, plan);
                    Check(!config.Save(), "collection move save unexpectedly succeeded under a write lock");
                }
                finally { db.Execute("ROLLBACK"); }
            }
            Check(config.DatabaseRevision == revision, "failed collection move save changed the revision");
            Check(CollectionOps.FindByKey(config, childA) != null
                && CollectionOps.FindByKey(config, movedChild) == null,
                "failed collection move save was not rolled back");
            using (Sqlite db = new Sqlite(config.Database.FilePath, false))
                Check(GroupIdAt(db, childA) == childAId, "failed collection move save changed the group id");
            Check(AppConfig.Load().ToDocument().OuterXml == persisted, "failed collection move save changed persisted config");
            using (Sqlite db = new Sqlite(config.Database.FilePath, false))
                Check(OperationCount(db) == operations, "failed collection move save left an audit operation");

            plan = PlanMove(config, CollectionMoveKind.Into, childA, rootB);
            CollectionOps.ApplyMovePlan(config, plan);
            Check(config.Save(), "collection move retry after save failure did not succeed");
            Check(CollectionOps.FindByKey(config, movedChild) != null && CollectionOps.FindByKey(config, childA) == null,
                "collection move retry was not persisted");
            Check(config.DatabaseRevision > revision, "collection move retry did not create a revision");
        }

        private static void CollectionMovePersistence()
        {
            AppConfig config = RenameConfig("collection-move-persist");
            config.AutoScan = false;
            string rootA = CollectionKeys.Make("照片", "旅行");
            string rootB = CollectionKeys.Make("照片", "风景");
            string rootC = CollectionKeys.Make("照片", "人物");
            string childA = rootA + "/2024";
            string grandA = childA + "/十月";
            string childB = rootB + "/2025";
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "旅行" });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "风景" });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "人物" });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "2024", ParentCollection = rootA });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "十月", ParentCollection = childA });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "2025", ParentCollection = rootB });
            AppInfo item = AddEntry(config, "照片",
                Path.Combine(config.ResourceStorePath, "照片", "move-photo.png"), false, true, "move photo");
            CollectionOps.AddMember(config, item.Key, childA);
            CollectionOps.AddMember(config, item.Key, grandA);
            config.ExcludedCategories[item.Key] = new List<string> { childA };
            config.ItemOrder[childA] = new List<string> { item.Key };
            config.CategoryViewMode[childA] = false;
            config.CategoryIconNames["resource:" + grandA] = "move-icon";
            config.SidebarOrder.Add(childA);
            Check(config.Save(), "collection move fixture save failed");
            long revision = config.DatabaseRevision;
            long categoryId, rootAId, rootBId, childAId, grandAId, recordGroup, addedId, addedGroup, listingRows;
            using (Sqlite db = new Sqlite(config.Database.FilePath, false))
            {
                categoryId = GroupIdAt(db, "照片");
                rootAId = GroupIdAt(db, rootA);
                rootBId = GroupIdAt(db, rootB);
                childAId = GroupIdAt(db, childA);
                grandAId = GroupIdAt(db, grandA);
                string itemId = db.Scalar("SELECT id FROM entries WHERE key=?", item.Key);
                Check(itemId != null, "fixture entry was not persisted");
                recordGroup = long.Parse(db.Scalar(
                    "SELECT group_id FROM records WHERE section='ItemCategories' AND item_id=? AND group_id=?",
                    itemId, childAId));
                string[] added = db.Query("SELECT id,group_id FROM added_items WHERE path=?", item.TargetPath)[0];
                addedId = long.Parse(added[0]);
                addedGroup = long.Parse(added[1]);
                listingRows = long.Parse(db.Scalar(
                    "SELECT count(*) FROM listing l JOIN entries e ON e.id=l.item_id WHERE e.key=? AND l.group_id=?",
                    item.Key, grandAId));
                Check(listingRows > 0, "fixture listing row was not persisted");
            }

            CollectionMovePlan plan = PlanMove(config, CollectionMoveKind.After, rootA, rootB);
            Check(!plan.NoOp && !plan.ChangesParent && plan.KeyMap.Count == 0,
                "same-parent reorder was treated as a parent change or no-op");
            CollectionOps.ApplyMovePlan(config, plan);
            Check(string.Join("|", CollectionChildNames(config, "照片", string.Empty).ToArray()) == "风景|旅行|人物",
                "same-parent reorder did not move the collection after its target");
            Check(config.Save(), "same-parent reorder save failed");
            Check(config.DatabaseRevision > revision, "same-parent reorder did not create a database revision");
            Check(string.Join("|", CollectionChildNames(AppConfig.Load(), "照片", string.Empty).ToArray()) == "风景|旅行|人物",
                "same-parent reorder was not persisted");

            ResourceCollection source = CollectionOps.FindByKey(config, childA);
            ResourceCollection target = CollectionOps.FindByKey(config, rootB);
            Check(source != null && target != null, "fixture lost the child or target collection");
            plan = PlanMove(config, CollectionMoveKind.Into, childA, rootB);
            string movedChild = rootB + "/2024";
            string movedGrand = movedChild + "/十月";
            Check(plan.ChangesParent && plan.DatabaseParentKey == rootB && plan.NewRootKey == movedChild,
                "moving into another collection computed the wrong target");
            Check(plan.KeyMap.ContainsKey(childA) && plan.KeyMap[childA] == movedChild
                && plan.KeyMap.ContainsKey(grandA) && plan.KeyMap[grandA] == movedGrand,
                "moving a subtree did not map every descendant key");
            CollectionOps.ApplyMovePlan(config, plan);
            Check(CollectionOps.FindByKey(config, childA) == null && CollectionOps.FindByKey(config, grandA) == null,
                "old subtree keys remained after the move");
            Check(CollectionOps.FindByKey(config, movedChild) != null && CollectionOps.FindByKey(config, movedGrand) != null,
                "new subtree keys were not created");
            Check(string.Join("|", CollectionChildNames(config, "照片", rootB).ToArray()) == "2025|2024",
                "moving into a collection did not append after its existing children");
            List<string> categories;
            Check(config.ItemCategories.TryGetValue(item.Key, out categories)
                && categories.Contains(movedChild) && categories.Contains(movedGrand)
                && !categories.Contains(childA) && !categories.Contains(grandA),
                "member references were not remapped with the moved subtree");
            Check(config.ExcludedCategories[item.Key].Contains(movedChild)
                && config.ItemOrder.ContainsKey(movedChild) && config.CategoryViewMode.ContainsKey(movedChild)
                && config.CategoryIconNames.ContainsKey("resource:" + movedGrand)
                && config.SidebarOrder.Contains(movedChild),
                "view, order, exclusion or icon references were not remapped");
            Check(config.Save(), "cross-parent move save failed");
            Check(config.DatabaseRevision > revision, "cross-parent move did not create a database revision");

            using (Sqlite db = new Sqlite(config.Database.FilePath, false))
            {
                Check(db.Scalar("SELECT id FROM groups WHERE key=?", childA) == null
                    && db.Scalar("SELECT id FROM groups WHERE key=?", grandA) == null,
                    "old group keys remained in the database");
                Check(GroupIdAt(db, movedChild) == childAId && GroupIdAt(db, movedGrand) == grandAId,
                    "moving the subtree rebuilt group ids");
                Check(long.Parse(db.Scalar("SELECT parent_id FROM groups WHERE id=?", childAId)) == rootBId,
                    "moved root parent_id was not updated");
                Check(long.Parse(db.Scalar("SELECT parent_id FROM groups WHERE id=?", grandAId)) == childAId,
                    "descendant parent_id changed during the move");
                string itemId = db.Scalar("SELECT id FROM entries WHERE key=?", item.Key);
                Check(long.Parse(db.Scalar(
                    "SELECT group_id FROM records WHERE section='ItemCategories' AND item_id=? AND group_id=?",
                    itemId, childAId)) == recordGroup,
                    "member record group_id changed during the move");
                string[] added = db.Query("SELECT id,group_id FROM added_items WHERE path=?", item.TargetPath)[0];
                Check(long.Parse(added[0]) == addedId && long.Parse(added[1]) == addedGroup,
                    "added item identity or group changed during the move");
                Check(long.Parse(db.Scalar(
                    "SELECT count(*) FROM listing l JOIN entries e ON e.id=l.item_id WHERE e.key=? AND l.group_id=?",
                    item.Key, grandAId)) == listingRows,
                    "listing membership changed during the move");
            }

            string renamedRootA = CollectionKeys.Make("照片", "旅行旧");
            Dictionary<string, string> oldParentRename = CollectionOps.RenameSubtree(config,
                CollectionOps.FindByKey(config, rootA), "旅行旧");
            Check(oldParentRename.Count == 1 && oldParentRename[rootA] == renamedRootA,
                "renaming the old parent produced the wrong key map");
            config.QueueCategoryRename(rootA, renamedRootA);
            Check(config.Save(), "renaming the old parent after a move failed");
            Check(CollectionOps.FindByKey(config, movedChild) != null && CollectionOps.FindByKey(config, movedGrand) != null,
                "renaming the old parent moved the subtree that no longer belonged to it");

            string renamedRootB = CollectionKeys.Make("照片", "风景新");
            string renamedMovedChild = renamedRootB + "/2024";
            string renamedMovedGrand = renamedMovedChild + "/十月";
            Dictionary<string, string> newParentRename = CollectionOps.RenameSubtree(config,
                CollectionOps.FindByKey(config, rootB), "风景新");
            Check(newParentRename.ContainsKey(rootB) && newParentRename[rootB] == renamedRootB
                && newParentRename.ContainsKey(movedChild) && newParentRename[movedChild] == renamedMovedChild,
                "renaming the new parent did not map the moved subtree");
            config.QueueCategoryRename(rootB, renamedRootB);
            Check(config.Save(), "renaming the new parent after a move failed");
            Check(CollectionOps.FindByKey(config, renamedMovedChild) != null
                && CollectionOps.FindByKey(config, renamedMovedGrand) != null
                && CollectionOps.FindByKey(config, movedChild) == null,
                "renaming the new parent did not move its nested subtree");
            Check(config.ItemCategories[item.Key].Contains(renamedMovedChild)
                && config.ItemCategories[item.Key].Contains(renamedMovedGrand)
                && config.ItemOrder.ContainsKey(renamedMovedChild)
                && config.CategoryIconNames.ContainsKey("resource:" + renamedMovedGrand),
                "renaming the new parent did not remap nested references");
            movedChild = renamedMovedChild;
            movedGrand = renamedMovedGrand;

            plan = PlanMove(config, CollectionMoveKind.ToRoot, movedChild, null);
            string rootKey = CollectionKeys.Make("照片", "2024");
            string rootGrand = rootKey + "/十月";
            Check(plan.ChangesParent && plan.DatabaseParentKey == "照片" && plan.NewRootKey == rootKey,
                "moving a collection to the category root computed the wrong parent");
            CollectionOps.ApplyMovePlan(config, plan);
            Check(config.ItemCategories[item.Key].Contains(rootKey)
                && config.ItemCategories[item.Key].Contains(rootGrand)
                && config.ItemOrder.ContainsKey(rootKey)
                && config.CategoryIconNames.ContainsKey("resource:" + rootGrand),
                "moving to the root did not remap descendant references");
            Check(config.Save(), "root move save failed");
            Check(string.Join("|", CollectionChildNames(config, "照片", string.Empty).ToArray()) == "风景新|旅行旧|人物|2024",
                "root move did not append at the end of the category");
            AppConfig loaded = AppConfig.Load();
            Check(CollectionOps.FindByKey(loaded, rootKey) != null && CollectionOps.FindByKey(loaded, rootGrand) != null
                && CollectionOps.FindByKey(loaded, movedChild) == null,
                "root move was not persisted");
            using (Sqlite db = new Sqlite(config.Database.FilePath, false))
            {
                Check(GroupIdAt(db, rootKey) == childAId && GroupIdAt(db, rootGrand) == grandAId,
                    "root move rebuilt subtree ids");
                Check(long.Parse(db.Scalar("SELECT parent_id FROM groups WHERE id=?", childAId)) == categoryId,
                    "root move left the old collection parent_id");
                Check(long.Parse(db.Scalar("SELECT parent_id FROM groups WHERE id=?", grandAId)) == childAId,
                    "root move altered a descendant parent_id");
            }
        }

        private static void CollectionSidebarInternalDrag()
        {
            using (Form host = new Form
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-3000, -3000),
                Size = new Size(320, 420)
            })
            using (Controls.CollectionSidebar sidebar = new Controls.CollectionSidebar { Dock = DockStyle.Fill })
            {
                host.Controls.Add(sidebar);
                host.Show();
                Application.DoEvents();

                string rootA = CollectionKeys.Make("照片", "旅行");
                string childA = rootA + "/2024";
                string childB = rootA + "/2025";
                string rootB = CollectionKeys.Make("照片", "风景");
                List<Controls.CollectionSidebar.Item> items = new List<Controls.CollectionSidebar.Item>();
                items.Add(new Controls.CollectionSidebar.Item { IsAll = true, Name = "全部" });
                items.Add(new Controls.CollectionSidebar.Item
                {
                    Key = rootA, Name = "旅行", Depth = 0, HasChildren = true, Expanded = true
                });
                items.Add(new Controls.CollectionSidebar.Item { Key = childA, Name = "2024", Depth = 1, ParentKey = rootA });
                items.Add(new Controls.CollectionSidebar.Item { Key = childB, Name = "2025", Depth = 1, ParentKey = rootA });
                items.Add(new Controls.CollectionSidebar.Item { Key = rootB, Name = "风景", Depth = 0 });
                sidebar.SetItems(items);

                Type type = typeof(Controls.CollectionSidebar);
                MethodInfo itemRect = type.GetMethod("ItemRect", Private);
                CollectionMoveRequest last = null;
                int feedback = 0, drops = 0;
                sidebar.MoveFeedback = delegate(CollectionMoveRequest request)
                {
                    feedback++;
                    last = request;
                    return DragDropEffects.Move;
                };
                sidebar.MoveRequested += delegate(CollectionMoveRequest request) { drops++; last = request; };

                Rectangle sourceRow = (Rectangle)itemRect.Invoke(sidebar, new object[] { 1 });
                Rectangle targetRow = (Rectangle)itemRect.Invoke(sidebar, new object[] { 4 });
                Point source = new Point(sourceRow.X + sourceRow.Width / 2, sourceRow.Y + sourceRow.Height / 2);
                Point target = new Point(targetRow.X + targetRow.Width / 2, targetRow.Y + targetRow.Height / 2);
                type.GetMethod("OnMouseDown", Private).Invoke(sidebar,
                    new object[] { new MouseEventArgs(MouseButtons.Left, 1, source.X, source.Y, 0) });
                Check(sidebar.IsInternalDragActive, "mouse down did not start an internal drag candidate");
                type.GetMethod("BeginInternalDrag", Private).Invoke(sidebar, null);
                int beforeDropFeedback = feedback;
                type.GetMethod("OnMouseUp", Private).Invoke(sidebar,
                    new object[] { new MouseEventArgs(MouseButtons.Left, 1, target.X, target.Y, 0) });
                Check(drops == 1 && last != null && last.Kind == CollectionMoveKind.Into
                    && last.SourceKey == rootA && last.TargetKey == rootB,
                    "internal drag did not report exactly one final move");
                Check(feedback > beforeDropFeedback && !sidebar.IsInternalDragActive && !sidebar.Capture,
                    "internal drag feedback, capture or cleanup was incomplete");

                last = null;
                drops = 0;
                sidebar.MoveFeedback = delegate(CollectionMoveRequest request) { last = request; return DragDropEffects.None; };
                type.GetMethod("OnMouseDown", Private).Invoke(sidebar,
                    new object[] { new MouseEventArgs(MouseButtons.Left, 1, source.X, source.Y, 0) });
                type.GetMethod("BeginInternalDrag", Private).Invoke(sidebar, null);
                type.GetMethod("OnMouseUp", Private).Invoke(sidebar,
                    new object[] { new MouseEventArgs(MouseButtons.Left, 1, target.X, target.Y, 0) });
                Check(drops == 0 && last != null && !sidebar.IsInternalDragActive,
                    "a rejected drop still requested a collection move");

                drops = 0;
                sidebar.MoveFeedback = delegate(CollectionMoveRequest request) { last = request; return DragDropEffects.Move; };
                type.GetMethod("OnMouseDown", Private).Invoke(sidebar,
                    new object[] { new MouseEventArgs(MouseButtons.Left, 1, source.X, source.Y, 0) });
                type.GetMethod("BeginInternalDrag", Private).Invoke(sidebar, null);
                type.GetMethod("OnKeyDown", Private).Invoke(sidebar, new object[] { new KeyEventArgs(Keys.Escape) });
                Check(!sidebar.IsInternalDragActive && !sidebar.Capture, "escape did not end and release the internal drag");
                type.GetMethod("OnMouseUp", Private).Invoke(sidebar,
                    new object[] { new MouseEventArgs(MouseButtons.Left, 1, target.X, target.Y, 0) });
                Check(drops == 0, "escape still requested a collection move");

                int externalCalls = 0;
                sidebar.ExternalDrop = delegate(string key, DragEventArgs args, bool drop)
                {
                    externalCalls++;
                    return DragDropEffects.Move;
                };
                typeof(Controls.CollectionSidebar).GetField("internalDragging", Private).SetValue(sidebar, true);
                DragEventArgs drag = new DragEventArgs(new DataObject(), 0, target.X, target.Y,
                    DragDropEffects.Move, DragDropEffects.None);
                type.GetMethod("OnDragEnter", Private).Invoke(sidebar, new object[] { drag });
                Check(drag.Effect == DragDropEffects.None && externalCalls == 0,
                    "an internal drag was treated as an external file import");
                sidebar.CancelInternalDrag();

                typeof(Controls.CollectionSidebar).GetField("internalDragging", Private).SetValue(sidebar, true);
                typeof(Controls.CollectionSidebar).GetField("internalDragSourceKey", Private).SetValue(sidebar, rootB);
                Rectangle expandedRow = (Rectangle)itemRect.Invoke(sidebar, new object[] { 1 });
                CollectionMoveRequest after = (CollectionMoveRequest)type.GetMethod("BuildInternalDropRequest", Private)
                    .Invoke(sidebar, new object[] { new Point(expandedRow.X + expandedRow.Width / 2, expandedRow.Bottom - 1) });
                int lastSubtree = (int)type.GetMethod("LastVisibleSubtreeIndex", Private).Invoke(sidebar, new object[] { 1 });
                Check(after != null && after.Kind == CollectionMoveKind.After && after.TargetKey == rootA && lastSubtree == 3,
                    "expanded target did not draw the after indicator below its visible subtree");
                sidebar.CancelInternalDrag();

                List<Controls.CollectionSidebar.Item> scrollItems = new List<Controls.CollectionSidebar.Item>();
                scrollItems.Add(new Controls.CollectionSidebar.Item { IsAll = true, Name = "全部" });
                scrollItems.Add(new Controls.CollectionSidebar.Item { Key = rootA, Name = "旅行", Depth = 0 });
                for (int i = 0; i < 40; i++)
                    scrollItems.Add(new Controls.CollectionSidebar.Item
                    {
                        Key = CollectionKeys.Make("照片", "项目" + i.ToString()), Name = "项目" + i.ToString(), Depth = 0
                    });
                sidebar.SetItems(scrollItems);
                int rowHeight = (int)type.GetProperty("RowH", Private).GetValue(sidebar, null);
                type.GetMethod("SetScroll", Private).Invoke(sidebar, new object[] { rowHeight });

                string mappedRoot = CollectionKeys.Make("照片", "旅行新");
                Dictionary<string, string> keyMap = new Dictionary<string, string>(StringComparer.Ordinal);
                keyMap[rootA] = mappedRoot;
                List<Controls.CollectionSidebar.Item> mappedItems = new List<Controls.CollectionSidebar.Item>();
                mappedItems.Add(new Controls.CollectionSidebar.Item { IsAll = true, Name = "全部" });
                for (int i = 0; i < 40; i++)
                    mappedItems.Add(new Controls.CollectionSidebar.Item
                    {
                        Key = i == 19 ? mappedRoot : CollectionKeys.Make("照片", "新项目" + i.ToString()),
                        Name = i == 19 ? "旅行新" : "新项目" + i.ToString(),
                        Depth = 0
                    });
                sidebar.SetItems(mappedItems, null, keyMap);
                int mappedIndex = (int)type.GetMethod("IndexOfKey", Private).Invoke(sidebar, new object[] { mappedRoot });
                int mappedOffset = (int)type.GetField("scrollOffset", Private).GetValue(sidebar);
                Check(mappedIndex == 20 && mappedOffset == mappedIndex * rowHeight,
                    "collection key map did not preserve the scroll anchor");
            }
        }

        private static void CollectionMoveUi()
        {
            AppConfig config = RenameConfig("collection-move-ui");
            config.AutoScan = false;
            string rootA = CollectionKeys.Make("照片", "UI旅行");
            string rootB = CollectionKeys.Make("照片", "UI风景");
            string childA = rootA + "/UI2024";
            string grandA = childA + "/UI十月";
            string childB = rootB + "/UI2025";
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "UI旅行" });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "UI风景" });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "UI2024", ParentCollection = rootA });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "UI十月", ParentCollection = childA });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "UI2025", ParentCollection = rootB });
            for (int i = 0; i < 30; i++)
                config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "UI填充" + i.ToString() });
            Check(config.Save(), "collection move UI fixture save failed");

            using (MainForm form = ResourceForm(config, "照片"))
            {
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-3000, -3000);
                form.ClientSize = new Size(1000, 680);
                form.Show();
                Application.DoEvents();

                Controls.CollectionSidebar sidebar = (Controls.CollectionSidebar)
                    typeof(MainForm).GetField("collectionSidebar", Private).GetValue(form);
                HashSet<string> expanded = (HashSet<string>)typeof(MainForm).GetField("expandedCollections", Private).GetValue(form);
                typeof(MainForm).GetField("currentCollection", Private).SetValue(form, grandA);
                typeof(MainForm).GetField("collectionEntered", Private).SetValue(form, true);
                typeof(MainForm).GetField("revealActiveCollection", Private).SetValue(form, false);
                expanded.Add(rootA);
                expanded.Add(childA);
                Invoke(form, "UpdateCollectionUi");
                Check((string)typeof(Controls.CollectionSidebar).GetField("activeKey", Private).GetValue(sidebar) == grandA,
                    "collection move UI fixture did not select the descendant collection");

                long revision = config.DatabaseRevision;
                CollectionMoveRequest noOp = MoveRequest(CollectionMoveKind.After, rootB, rootA);
                Check((DragDropEffects)Invoke(form, "ResolveCollectionMoveFeedback", noOp) == DragDropEffects.None,
                    "the UI offered a move for a same-parent no-op");
                Invoke(form, "MoveCollection", noOp);
                Check(config.DatabaseRevision == revision, "the UI saved a same-parent no-op");

                CollectionMoveRequest request = MoveRequest(CollectionMoveKind.Into, childA, rootB);
                Invoke(form, "MoveCollection", request);
                string movedChild = rootB + "/UI2024";
                string movedGrand = movedChild + "/UI十月";
                string actualCurrent = (string)typeof(MainForm).GetField("currentCollection", Private).GetValue(form);
                Check(actualCurrent == movedGrand,
                    "moving a collection did not remap the selected descendant: current=" + actualCurrent
                    + "; saved=" + config.DatabaseRevision + "; error=" + config.DatabaseError
                    + "; old=" + (CollectionOps.FindByKey(config, childA) != null)
                    + "; moved=" + (CollectionOps.FindByKey(config, movedChild) != null));
                Check((bool)typeof(MainForm).GetField("collectionEntered", Private).GetValue(form),
                    "moving a collection lost the entered content state");
                Check(expanded.Contains(rootB) && expanded.Contains(movedChild) && !expanded.Contains(childA),
                    "moving a collection did not expand and remap its new parent chain");
                Check((string)typeof(Controls.CollectionSidebar).GetField("activeKey", Private).GetValue(sidebar) == movedGrand,
                    "the sidebar did not select the remapped descendant");
                Check(config.DatabaseRevision > revision, "the UI collection move was not saved");
                Check(string.Join("|", CollectionChildNames(config, "照片", rootB).ToArray()) == "UI2025|UI2024",
                    "the UI collection move did not append after the target's children");
                AppConfig loaded = AppConfig.Load();
                Check(CollectionOps.FindByKey(loaded, movedGrand) != null && CollectionOps.FindByKey(loaded, childA) == null,
                    "the UI collection move was not persisted");

                string beforeFailure = string.Join("|", CollectionChildNames(config, "照片", string.Empty).ToArray());
                long failureRevision = config.DatabaseRevision;
                FieldInfo saveFailed = typeof(AppConfig).GetField("SaveFailed",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Check(saveFailed != null, "save failure event was not found");
                saveFailed.SetValue(config, null);   // 本用例自行验证恢复，避免模态保存失败提示阻塞
                using (Sqlite db = new Sqlite(config.Database.FilePath, false))
                {
                    db.Execute("BEGIN IMMEDIATE");
                    try { Invoke(form, "MoveCollection", MoveRequest(CollectionMoveKind.Into, rootB, rootA)); }
                    finally { db.Execute("ROLLBACK"); }
                }
                Check(config.DatabaseRevision == failureRevision, "failed UI collection move changed the revision");
                Check((string)typeof(MainForm).GetField("currentCollection", Private).GetValue(form) == movedGrand
                    && (bool)typeof(MainForm).GetField("collectionEntered", Private).GetValue(form),
                    "failed UI collection move lost the selected collection or content state");
                Check(expanded.Contains(rootB) && expanded.Contains(movedChild),
                    "failed UI collection move lost the expanded parent chain");
                Check((string)typeof(Controls.CollectionSidebar).GetField("activeKey", Private).GetValue(sidebar) == movedGrand,
                    "failed UI collection move lost the sidebar selection");
                Check(string.Join("|", CollectionChildNames(config, "照片", string.Empty).ToArray()) == beforeFailure,
                    "failed UI collection move changed the collection tree");

                int rowHeight = (int)typeof(Controls.CollectionSidebar).GetProperty("RowH", Private)
                    .GetValue(sidebar, null);
                Type sidebarType = typeof(Controls.CollectionSidebar);
                sidebarType.GetMethod("SetScrollOffset", Private).Invoke(sidebar,
                    new object[] { rowHeight * 3 + 7 });
                int failureScroll = (int)sidebarType.GetProperty("ScrollOffset", Private)
                    .GetValue(sidebar, null);
                Check(failureScroll > 0, "collection move failure fixture could not scroll the sidebar");
                using (Sqlite db = new Sqlite(config.Database.FilePath, false))
                {
                    db.Execute("BEGIN IMMEDIATE");
                    try { Invoke(form, "MoveCollection", MoveRequest(CollectionMoveKind.Into, rootB, rootA)); }
                    finally { db.Execute("ROLLBACK"); }
                }
                int restoredScroll = (int)sidebarType.GetProperty("ScrollOffset", Private)
                    .GetValue(sidebar, null);
                Check(restoredScroll == failureScroll,
                    "failed UI collection move restored a different scroll offset: " + restoredScroll
                    + " != " + failureScroll);

                Invoke(form, "EditCollection", CollectionOps.FindByKey(config, movedChild));
                Controls.CollectionInfoPanel info = (Controls.CollectionInfoPanel)
                    typeof(MainForm).GetField("collectionInfo", Private).GetValue(form);
                Check(info.IsEditing, "collection edit state did not start");
                long editingRevision = config.DatabaseRevision;
                CollectionMoveRequest blocked = MoveRequest(CollectionMoveKind.ToRoot, movedChild, null);
                Check((DragDropEffects)Invoke(form, "ResolveCollectionMoveFeedback", blocked) == DragDropEffects.None,
                    "editing did not reject collection drag feedback");
                Invoke(form, "MoveCollection", blocked);
                Check(info.IsEditing && config.DatabaseRevision == editingRevision
                    && CollectionOps.FindByKey(config, movedChild) != null,
                    "editing did not block a collection move");
                info.EndEdit();
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

        private static void ViewerResourceSingleTarget()
        {
            AppConfig config = RenameConfig("viewer-single-target");
            AppInfo first = AddEntry(config, "文档", Path.Combine(config.ResourceStorePath, "文档", "first.txt"),
                false, true, "first");
            AppInfo second = AddEntry(config, "文档", Path.Combine(config.ResourceStorePath, "文档", "second.txt"),
                false, true, "second");
            using (MainForm form = ResourceForm(config, "文档", first, second))
            {
                ShowMain(form);
                SelectForTest(form, first, second);
                Controls.TextViewerControl viewer = OpenTextViewer(form, first, first, second);
                MenuWindow.MenuItem hide = FindMenuItem(ViewerMenu(form, viewer, first.TargetPath), "隐藏");
                Check(hide != null && !hide.Disabled, "viewer hide menu missing");
                hide.Action();
                Check(first.IsHidden && !second.IsHidden, "viewer hide affected the grid selection");
                Check(config.Hidden.Contains(first.Key) && !config.Hidden.Contains(second.Key),
                    "viewer hide persisted the wrong targets");
                CleanupTextViewer(form);
            }

            config = RenameConfig("viewer-single-collection");
            first = AddEntry(config, "文档", Path.Combine(config.ResourceStorePath, "文档", "first.txt"),
                false, true, "first");
            second = AddEntry(config, "文档", Path.Combine(config.ResourceStorePath, "文档", "second.txt"),
                false, true, "second");
            config.Collections.Add(new ResourceCollection { Parent = "文档", Name = "回归集合" });
            string collection = CollectionKeys.Make("文档", "回归集合");
            CollectionOps.AddMember(config, first.Key, collection);
            CollectionOps.AddMember(config, second.Key, collection);
            using (MainForm form = ResourceForm(config, "文档", first, second))
            {
                ShowMain(form);
                typeof(MainForm).GetField("currentCollection", Private).SetValue(form, collection);
                SelectForTest(form, first, second);
                Controls.TextViewerControl viewer = OpenTextViewer(form, first, first, second);
                MenuWindow.MenuItem remove = FindMenuItem(ViewerMenu(form, viewer, first.TargetPath), "从集合移除");
                Check(remove != null && !remove.Disabled, "viewer collection menu missing");
                remove.Action();
                Check(!HasCollection(config, first, collection) && HasCollection(config, second, collection),
                    "viewer collection action used the grid selection");
                CleanupTextViewer(form);
            }
        }

        private static void ViewerMutationProtectsDirtyText()
        {
            AppConfig config = RenameConfig("viewer-dirty-text");
            AppInfo app = AddEntry(config, "文档", Path.Combine(config.ResourceStorePath, "文档", "dirty.txt"),
                false, true, "dirty");
            using (MainForm form = ResourceForm(config, "文档", app))
            {
                ShowMain(form);
                Controls.TextViewerControl viewer = OpenTextViewer(form, app, app);
                MakeTextDirty(viewer);
                MenuWindow.MenuItem delete = FindMenuItem(ViewerMenu(form, viewer, app.TargetPath), "删除");
                Check(delete != null && !delete.Disabled, "viewer delete menu missing");

                bool confirm, native, unexpected;
                RunDeleteFlow(delete.Action, false, null, 0, out confirm, out native, out unexpected);
                Check(confirm && !native && !unexpected, "delete cancellation did not stop at confirmation");
                Check(File.Exists(app.TargetPath) && config.AddedItems.ByPath(app.TargetPath).Count > 0,
                    "delete cancellation removed the file or record");
                Check(viewer.Visible && viewer.CurrentPath == app.TargetPath && (bool)ReadField(viewer, "dirty"),
                    "delete cancellation closed the dirty viewer");

                RunDeleteFlow(delete.Action, true, "文本", 2, out confirm, out native, out unexpected);
                Check(confirm && native && !unexpected, "delete did not ask about unsaved text before the disk change");
                Check(File.Exists(app.TargetPath) && config.AddedItems.ByPath(app.TargetPath).Count > 0,
                    "cancelled save did not abort deletion");
                Check(viewer.Visible && viewer.CurrentPath == app.TargetPath && (bool)ReadField(viewer, "dirty"),
                    "cancelled save closed the dirty viewer");

                string oldPath = app.TargetPath;
                string renamedPath = Path.Combine(Path.GetDirectoryName(oldPath), "renamed.txt");
                string result = null;
                bool answered = AnswerMessageDialog("文本", 2,
                    delegate { result = (string)Invoke(form, "TryRenameOnDisk", app, oldPath, false, "renamed.txt"); });
                Check(answered, "disk rename did not ask about unsaved text");
                Check(result == string.Empty && File.Exists(oldPath) && !File.Exists(renamedPath),
                    "cancelled save did not abort the disk rename");
                Check(app.TargetPath == oldPath && EntriesOf(form, app)[0].Path == oldPath,
                    "cancelled save changed the renamed path");
                Check(viewer.Visible && viewer.CurrentPath == oldPath && (bool)ReadField(viewer, "dirty"),
                    "cancelled save closed the renamed viewer");
                CleanupTextViewer(form);
            }
        }

        private static void ViewerDirtyMutationSuccess()
        {
            AppConfig config = RenameConfig("viewer-dirty-delete-save");
            AppInfo app = AddEntry(config, "文档", Path.Combine(config.ResourceStorePath, "文档", "delete-save.txt"),
                false, true, "delete-save");
            using (MainForm form = ResourceForm(config, "文档", app))
            {
                ShowMain(form);
                Controls.TextViewerControl viewer = OpenTextViewer(form, app, app);
                MakeTextDirty(viewer);
                MenuWindow.MenuItem delete = FindMenuItem(ViewerMenu(form, viewer, app.TargetPath), "删除");
                Check(delete != null && !delete.Disabled, "viewer delete menu missing for save-success case");
                bool confirm, native, unexpected;
                RunDeleteFlow(delete.Action, true, "文本", 6, out confirm, out native, out unexpected);
                Check(confirm && native && !unexpected, "save-before-delete opened an old-path dialog");
                Check(!File.Exists(app.TargetPath) && config.AddedItems.ByPath(app.TargetPath).Count == 0,
                    "save-before-delete did not remove the file and record");
                Check(!viewer.Visible, "save-before-delete left the viewer open");
                CleanupTextViewer(form);
            }

            config = RenameConfig("viewer-dirty-delete-discard");
            app = AddEntry(config, "文档", Path.Combine(config.ResourceStorePath, "文档", "delete-discard.txt"),
                false, true, "delete-discard");
            using (MainForm form = ResourceForm(config, "文档", app))
            {
                ShowMain(form);
                Controls.TextViewerControl viewer = OpenTextViewer(form, app, app);
                MakeTextDirty(viewer);
                MenuWindow.MenuItem delete = FindMenuItem(ViewerMenu(form, viewer, app.TargetPath), "删除");
                Check(delete != null && !delete.Disabled, "viewer delete menu missing for discard-success case");
                bool confirm, native, unexpected;
                RunDeleteFlow(delete.Action, true, "文本", 7, out confirm, out native, out unexpected);
                Check(confirm && native && !unexpected, "discard-before-delete opened an old-path dialog");
                Check(!File.Exists(app.TargetPath) && config.AddedItems.ByPath(app.TargetPath).Count == 0,
                    "discard-before-delete did not remove the file and record");
                Check(!viewer.Visible, "discard-before-delete left the viewer open");
                CleanupTextViewer(form);
            }

            ViewerDirtyRenameSuccess(true);
            ViewerDirtyRenameSuccess(false);
        }

        private static void ViewerDirtyRenameSuccess(bool save)
        {
            AppConfig config = RenameConfig(save ? "viewer-dirty-rename-save" : "viewer-dirty-rename-discard");
            string name = save ? "rename-save.txt" : "rename-discard.txt";
            AppInfo app = AddEntry(config, "文档", Path.Combine(config.ResourceStorePath, "文档", name),
                false, true, name);
            using (MainForm form = ResourceForm(config, "文档", app))
            {
                ShowMain(form);
                Controls.TextViewerControl viewer = OpenTextViewer(form, app, app);
                MakeTextDirty(viewer);
                string oldPath = app.TargetPath;
                string newName = save ? "renamed-save.txt" : "renamed-discard.txt";
                string newPath = Path.Combine(Path.GetDirectoryName(oldPath), newName);
                string result = null;
                Action mutation = delegate
                {
                    result = (string)Invoke(form, "TryRenameOnDisk", app, oldPath, false, newName);
                };
                Action wrapped = (Action)Invoke(form, "WrapViewerMenuAction", mutation, oldPath, (Control)viewer);
                bool unexpected;
                bool answered = AnswerMessageDialog("文本", save ? 6 : 7, wrapped, out unexpected);
                Check(answered && !unexpected, "dirty rename opened an old-path or unexpected dialog");
                Check(result == null, "dirty rename failed: " + result);
                Check(!File.Exists(oldPath) && File.Exists(newPath), "dirty rename did not move the file");
                Check(app.TargetPath == newPath && config.AddedItems.ByPath(newPath).Count == 1
                    && config.AddedItems.ByPath(oldPath).Count == 0, "dirty rename lost the collection record");
                string content = File.ReadAllText(newPath);
                Check(save ? content.Contains("viewer regression edit") : !content.Contains("viewer regression edit"),
                    save ? "saved rename lost the edited text" : "discard rename wrote unsaved text");
                Check(!viewer.Visible, "dirty rename left the viewer open");
                CleanupTextViewer(form);
            }
        }

        private static void ViewerActionOutcomeKeepsViewer()
        {
            AppConfig config = RenameConfig("viewer-action-outcome");
            AppInfo app = AddEntry(config, "文档", Path.Combine(config.ResourceStorePath, "文档", "locked.txt"),
                false, true, "locked");
            using (MainForm form = ResourceForm(config, "文档", app))
            {
                ShowMain(form);
                Controls.TextViewerControl viewer = OpenTextViewer(form, app, app);
                MenuWindow.MenuItem delete = FindMenuItem(ViewerMenu(form, viewer, app.TargetPath), "删除");
                Check(delete != null && !delete.Disabled, "viewer delete menu missing for failure case");
                bool confirm, native, unexpected;
                using (FileStream locked = new FileStream(app.TargetPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    // 失败提示只有一个「知道了」按钮，编号 2 对应它（见 ClickMessageDialog）。
                    RunDeleteFlow(delete.Action, true, "删除", 2, out confirm, out native, out unexpected);
                }
                Check(confirm, "delete confirmation was not executed");
                Check(native && !unexpected, "failed delete did not show exactly one failure prompt");
                Check(File.Exists(app.TargetPath) && config.AddedItems.ByPath(app.TargetPath).Count > 0,
                    "failed delete removed the file or record");
                Check(viewer.Visible && viewer.CurrentPath == app.TargetPath,
                    "failed delete closed the viewer");

                List<MenuWindow.MenuItem> items = ViewerMenu(form, viewer, app.TargetPath);
                using (MenuWindow menu = new MenuWindow(items, 230))
                {
                    Message message = Message.Create(IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);
                    object[] args = { message, Keys.Escape };
                    bool handled = (bool)typeof(MenuWindow).GetMethod("ProcessCmdKey", Private).Invoke(menu, args);
                    Check(handled, "escape was not handled by the viewer menu");
                }
                Check(viewer.Visible && !app.IsHidden && File.Exists(app.TargetPath),
                    "escape executed a viewer menu action or closed the viewer");
                CleanupTextViewer(form);
            }
        }

        private static void ViewerMissingResource()
        {
            AppConfig config = RenameConfig("viewer-missing-resource");
            AppInfo app = AddEntry(config, "文档", Path.Combine(config.ResourceStorePath, "文档", "missing.txt"),
                false, true, "missing");
            using (MainForm form = ResourceForm(config, "文档", app))
            {
                ShowMain(form);
                Controls.TextViewerControl viewer = OpenTextViewer(form, app, app);
                File.Delete(app.TargetPath);
                object result = Invoke(form, "BuildViewerResourceMenu", (Control)viewer, app.TargetPath);
                Check(result == null, "missing viewer resource built a menu");
                Check(!viewer.Visible && viewer.CurrentPath == null, "missing viewer resource left the viewer open");
                Check(config.AddedItems.ByPath(app.TargetPath).Count == 1,
                    "first missing observation purged the collection record");
                Application.DoEvents();
                Check(config.AddedItems.ByPath(app.TargetPath).Count == 1,
                    "first missing observation after queueing purged the collection record");
                System.Windows.Forms.Timer watchTimer =
                    (System.Windows.Forms.Timer)typeof(MainForm).GetField("moveOutTimer", Private).GetValue(form);
                Check(watchTimer != null && watchTimer.Enabled, "missing viewer resource was not watched");
                watchTimer.Stop();

                File.WriteAllText(app.TargetPath, "restored");
                Invoke(form, "CheckMoveOuts");
                Check(config.AddedItems.ByPath(app.TargetPath).Count == 1,
                    "reappeared viewer resource was purged");
                File.Delete(app.TargetPath);
                Invoke(form, "CheckMoveOuts");
                Check(config.AddedItems.ByPath(app.TargetPath).Count == 1,
                    "one later missing observation purged the collection record");
                Invoke(form, "CheckMoveOuts");
                Check(config.AddedItems.ByPath(app.TargetPath).Count == 0,
                    "two consecutive missing observations kept the stale record");
                Check(config.Database.Page("文档", true, true, "", 10, null, true, false).Total == 0,
                    "missing viewer resource remained in the database listing");
                Check((bool)typeof(MainForm).GetField("scanPending", Private).GetValue(form),
                    "missing viewer resource did not request a rescan");
                CleanupTextViewer(form);
            }

            config = RenameConfig("viewer-missing-parent");
            app = AddEntry(config, "文档", Path.Combine(config.ResourceStorePath, "文档", "missing-parent", "orphan.txt"),
                false, true, "missing-parent");
            using (MainForm form = ResourceForm(config, "文档", app))
            {
                ShowMain(form);
                Controls.TextViewerControl viewer = OpenTextViewer(form, app, app);
                File.Delete(app.TargetPath);
                Directory.Delete(Path.GetDirectoryName(app.TargetPath), true);
                object result = Invoke(form, "BuildViewerResourceMenu", (Control)viewer, app.TargetPath);
                Check(result == null && !viewer.Visible, "unreachable viewer resource left the viewer open");
                Check(config.AddedItems.ByPath(app.TargetPath).Count == 1,
                    "unreachable parent immediately purged the collection record");
                IList watches = (IList)typeof(MainForm).GetField("moveOutWatches", Private).GetValue(form);
                Check(watches.Count == 0, "unreachable parent started a destructive move-out watch");
                Invoke(form, "CheckMoveOuts");
                Invoke(form, "CheckMoveOuts");
                Check(config.AddedItems.ByPath(app.TargetPath).Count == 1,
                    "unreachable parent lost its record after missing checks");
                CleanupTextViewer(form);
            }

            config = RenameConfig("viewer-missing-dirty");
            app = AddEntry(config, "文档", Path.Combine(config.ResourceStorePath, "文档", "missing-dirty.txt"),
                false, true, "missing-dirty");
            using (MainForm form = ResourceForm(config, "文档", app))
            {
                ShowMain(form);
                Controls.TextViewerControl viewer = OpenTextViewer(form, app, app);
                MakeTextDirty(viewer);
                File.Delete(app.TargetPath);
                object result = null;
                bool answered = AnswerMessageDialog("文本", 7,
                    delegate { result = Invoke(form, "BuildViewerResourceMenu", (Control)viewer, app.TargetPath); });
                Check(answered, "external deletion discarded unsaved text without asking");
                Check(result == null && !viewer.Visible, "external deletion left the dirty viewer open");
                Check(config.AddedItems.ByPath(app.TargetPath).Count == 1,
                    "dirty external deletion purged on the first missing observation");
                PumpUntil(delegate { return config.AddedItems.ByPath(app.TargetPath).Count == 0; });
                Check(config.Database.Page("文档", true, true, "", 10, null, true, false).Total == 0,
                    "dirty external deletion kept a database ghost");
                CleanupTextViewer(form);
            }
        }

        private static void ViewerResourceOutsideCurrentList()
        {
            AppConfig config = RenameConfig("viewer-outside-list");
            AppInfo app = AddEntry(config, "文档", Path.Combine(config.ResourceStorePath, "文档", "outside.txt"),
                false, true, "outside");
            using (MainForm form = ResourceForm(config, "文档", app))
            {
                ShowMain(form);
                Controls.TextViewerControl viewer = OpenTextViewer(form, app, app);
                typeof(MainForm).GetField("shownApps", Private).SetValue(form, new List<AppInfo>());
                object result = Invoke(form, "BuildViewerResourceMenu", (Control)viewer, app.TargetPath);
                Check(result == null && !viewer.Visible,
                    "viewer resource outside the current list left the viewer open");
                Check(File.Exists(app.TargetPath) && config.AddedItems.ByPath(app.TargetPath).Count > 0,
                    "viewer closed for an out-of-list path and purged its valid record");
                CleanupTextViewer(form);
            }
        }

        private static void ViewerLazySubmenuAction()
        {
            AppConfig config = RenameConfig("viewer-lazy-submenu");
            AppInfo app = AddEntry(config, "文档", Path.Combine(config.ResourceStorePath, "文档", "lazy.txt"),
                false, true, "lazy");
            using (MainForm form = ResourceForm(config, "文档", app))
            {
                ShowMain(form);
                Controls.TextViewerControl viewer = OpenTextViewer(form, app, app);
                List<MenuWindow.MenuItem> top = new List<MenuWindow.MenuItem>();
                MenuWindow.MenuItem parent = new MenuWindow.MenuItem { Text = "lazy" };
                MenuWindow.MenuItem keep = new MenuWindow.MenuItem { Text = "keep", Action = delegate { } };
                MenuWindow.MenuItem invalidate = new MenuWindow.MenuItem
                {
                    Text = "delete",
                    Action = delegate { File.Delete(app.TargetPath); }
                };
                parent.SubItemsFactory = delegate
                {
                    return new List<MenuWindow.MenuItem> { keep, invalidate };
                };
                top.Add(parent);
                Invoke(form, "WrapViewerMenuItems", top, app.TargetPath, (Control)viewer,
                    new HashSet<MenuWindow.MenuItem>());

                List<MenuWindow.MenuItem> lazy = parent.SubItemsFactory();
                Check(lazy.Count == 2, "lazy submenu fixture changed");
                lazy[0].Action();
                Check(viewer.Visible && File.Exists(app.TargetPath),
                    "lazy submenu no-op closed the viewer");
                lazy[1].Action();
                Check(!viewer.Visible, "lazy submenu action was not checked for resource invalidation");
                CleanupTextViewer(form);
            }
        }

        private static void ShowMain(MainForm form)
        {
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-3000, -3000);
            form.Size = new Size(1000, 700);
            form.Show();
            Application.DoEvents();
        }

        private static Controls.TextViewerControl OpenTextViewer(MainForm form, AppInfo current, params AppInfo[] sequence)
        {
            List<AppInfo> list = new List<AppInfo>(sequence);
            if (!list.Contains(current)) list.Insert(0, current);
            typeof(MainForm).GetField("shownApps", Private).SetValue(form, list);
            Invoke(form, "ShowTextViewer", current);
            Controls.TextViewerControl viewer =
                (Controls.TextViewerControl)typeof(MainForm).GetField("textViewer", Private).GetValue(form);
            PumpUntil(delegate
            {
                return viewer.CurrentPath == current.TargetPath
                    && !(bool)ReadField(viewer, "loading");
            }, 5000);
            Check(viewer.Visible && viewer.CurrentPath == current.TargetPath, "text viewer did not open");
            return viewer;
        }

        private static void MakeTextDirty(Controls.TextViewerControl viewer)
        {
            RichTextBox box = (RichTextBox)ReadField(viewer, "textBox");
            Check(!box.ReadOnly, "text fixture is read-only");
            box.Select(box.TextLength, 0);
            box.SelectedText = "\nviewer regression edit";
            Application.DoEvents();
            Check((bool)ReadField(viewer, "dirty"), "text edit was not marked dirty");
        }

        private static void SelectForTest(MainForm form, params AppInfo[] apps)
        {
            List<AppInfo> list = new List<AppInfo>(apps);
            typeof(MainForm).GetField("shownApps", Private).SetValue(form, list);
            Controls.AppGridControl grid =
                (Controls.AppGridControl)typeof(MainForm).GetField("grid", Private).GetValue(form);
            grid.SetApps(list);
            Type type = typeof(Controls.AppGridControl);
            type.GetMethod("SelectSingle", Private).Invoke(grid, new object[] { 0 });
            for (int i = 1; i < list.Count; i++)
                type.GetMethod("ToggleSelected", Private).Invoke(grid, new object[] { i });
            Check(grid.SelectedCount == list.Count, "grid multi-selection fixture failed");
        }

        private static List<MenuWindow.MenuItem> ViewerMenu(MainForm form, Control viewer, string path)
        {
            List<MenuWindow.MenuItem> items = (List<MenuWindow.MenuItem>)
                Invoke(form, "BuildViewerResourceMenu", viewer, path);
            Check(items != null, "viewer menu was not built");
            return items;
        }

        private static MenuWindow.MenuItem FindMenuItem(List<MenuWindow.MenuItem> items, string text)
        {
            if (items == null) return null;
            foreach (MenuWindow.MenuItem item in items)
            {
                if (item == null) continue;
                if (!item.Separator && item.Text == text) return item;
                MenuWindow.MenuItem nested = FindMenuItem(item.SubItems, text);
                if (nested != null) return nested;
            }
            return null;
        }

        private static bool HasCollection(AppConfig config, AppInfo app, string collection)
        {
            List<string> categories;
            return app != null && config.ItemCategories.TryGetValue(app.Key, out categories)
                && categories.Contains(collection);
        }

        private static void CleanupTextViewer(MainForm form)
        {
            Controls.TextViewerControl viewer =
                (Controls.TextViewerControl)typeof(MainForm).GetField("textViewer", Private).GetValue(form);
            if (viewer == null || viewer.IsDisposed) return;
            if ((bool)ReadField(viewer, "dirty"))
                typeof(Controls.TextViewerControl).GetMethod("MarkClean", Private).Invoke(viewer, null);
            Invoke(form, "HideTextViewer");
            Application.DoEvents();
        }

        private static object ReadField(object target, string name)
        {
            return target.GetType().GetField(name, Private).GetValue(target);
        }

        // 自绘提示框：按钮组合、返回值、Esc 和默认按钮都要与系统提示框一致，替换后调用方的判断不变。
        private static void MessageDialogResults()
        {
            DialogResult ok = DialogResult.None, yes = DialogResult.None, no = DialogResult.None, cancel = DialogResult.None;
            Check(AnswerMessageDialog("回归提示", 2, delegate { ok = MessageDialog.Show("只有一个按钮", "回归提示", MessageBoxButtons.OK, MessageBoxIcon.None); })
                && ok == DialogResult.OK, "single-button prompt did not return OK: " + ok);
            Check(AnswerMessageDialog("回归提示", 6, delegate { yes = MessageDialog.Show(null, "是否保存", "回归提示", MessageBoxButtons.YesNoCancel, MessageBoxIcon.None); })
                && yes == DialogResult.Yes, "yes was not returned: " + yes);
            Check(AnswerMessageDialog("回归提示", 7, delegate { no = MessageDialog.Show(null, "是否保存", "回归提示", MessageBoxButtons.YesNo, MessageBoxIcon.None); })
                && no == DialogResult.No, "no was not returned: " + no);
            Check(AnswerMessageDialog("回归提示", 2, delegate { cancel = MessageDialog.Show(null, "是否保存", "回归提示", MessageBoxButtons.YesNoCancel, MessageBoxIcon.None); })
                && cancel == DialogResult.Cancel, "cancel was not returned: " + cancel);

            // Esc：确定/取消 → 取消；是/否没有 Esc（与系统一致，窗口不关）；回车选默认按钮（这里是第二个）
            DialogResult esc = DialogResult.None, enter = DialogResult.None;
            bool stayed = false;
            PressModalKey(Keys.Escape, delegate { esc = MessageDialog.Show(null, "继续？", "回归提示", MessageBoxButtons.OKCancel, MessageBoxIcon.None); });
            Check(esc == DialogResult.Cancel, "escape on OK/Cancel did not cancel: " + esc);
            using (System.Windows.Forms.Timer probe = new System.Windows.Forms.Timer { Interval = 10 })
            {
                probe.Tick += delegate
                {
                    Form dialog = FindMessageDialog("回归提示");
                    if (dialog == null) return;
                    probe.Stop();
                    dialog.Opacity = 0;
                    MethodInfo key = typeof(Control).GetMethod("OnKeyDown", Private);
                    key.Invoke(dialog, new object[] { new KeyEventArgs(Keys.Escape) });
                    stayed = !dialog.IsDisposed && dialog.Visible;
                    key.Invoke(dialog, new object[] { new KeyEventArgs(Keys.Enter) });
                };
                probe.Start();
                enter = MessageDialog.Show(null, "删除？", "回归提示", MessageBoxButtons.YesNo, MessageBoxIcon.None, MessageBoxDefaultButton.Button2);
            }
            Check(stayed, "escape closed a yes/no prompt that has no cancel");
            Check(enter == DialogResult.No, "enter did not choose the default (second) button: " + enter);

            // 不支持的组合（如 中止/重试/忽略）退回系统提示框，不抛异常
            bool native = false;
            using (System.Windows.Forms.Timer probe = new System.Windows.Forms.Timer { Interval = 10 })
            {
                probe.Tick += delegate
                {
                    IntPtr hwnd = FindOwnDialog("回归原生");
                    if (hwnd == IntPtr.Zero) return;
                    probe.Stop();
                    native = true;
                    PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
                };
                probe.Start();
                MessageDialog.Show(null, "原生", "回归原生", MessageBoxButtons.RetryCancel, MessageBoxIcon.None);
            }
            Check(native, "unsupported button set did not fall back to the system prompt");
        }

        private static void CategoryPickerFlow()
        {
            List<string> names = new List<string> { "照片", "视频", "文档", "文件" };
            List<CategoryIconKind> icons = new List<CategoryIconKind>();
            foreach (string name in names) icons.Add(CategoryIcons.ForCategory(name));
            Func<Keys[], string> run = delegate(Keys[] keys)
            {
                string picked = "unset";
                using (System.Windows.Forms.Timer probe = new System.Windows.Forms.Timer { Interval = 10 })
                {
                    probe.Tick += delegate
                    {
                        Form dialog = FindModalForm();
                        if (dialog == null) return;
                        probe.Stop();
                        dialog.Opacity = 0;
                        Control tiles = dialog.Controls.OfType<Controls.ScrollListBase>().First();
                        MethodInfo down = typeof(Control).GetMethod("OnKeyDown", Private);
                        MethodInfo cmd = dialog.GetType().GetMethod("ProcessCmdKey", Private);
                        foreach (Keys key in keys)
                        {
                            if (key == Keys.Enter || key == Keys.Escape) cmd.Invoke(dialog, new object[] { new Message(), key });
                            else down.Invoke(tiles, new object[] { new KeyEventArgs(key) });
                        }
                        if (!dialog.IsDisposed && dialog.Visible) dialog.Close();
                    };
                    probe.Start();
                    picked = CategoryPickerDialog.Show(null, "导入到…", "回归", names, icons, "文档");
                }
                return picked;
            };
            Check(run(new Keys[] { Keys.Enter }) == "文档", "the suggested category was not preselected");
            Check(run(new Keys[] { Keys.Right, Keys.Enter }) == "文件", "arrow keys did not move the selection");
            Check(run(new Keys[] { Keys.Up, Keys.Up, Keys.Enter }) == "文档", "moving past the first row wrapped or jumped");
            Check(run(new Keys[] { Keys.Escape }) == null, "escape did not cancel");
        }

        private static void DuplicateDialogFlow()
        {
            ImportDuplicateReport report = new ImportDuplicateReport();
            report.Add(ImportDuplicateKind.OtherCategoryContent, null, "C:\\in\\reuse.jpg", "D:\\lib\\文档\\reuse.jpg", "", false, "h0");
            report.Add(ImportDuplicateKind.SameCategoryContent, null, "C:\\in\\a.jpg", "D:\\lib\\照片\\a.jpg", "", true, "h1");
            report.Add(ImportDuplicateKind.BatchContentDuplicate, null, "C:\\in\\copy\\b.jpg", "C:\\in\\b.jpg", "", true, "h2");
            report.Add(ImportDuplicateKind.Unchecked, null, "\\\\nas\\c.jpg", null, "", false, null);
            report.Findings[2].Keep = true;

            List<ImportDuplicateFinding> rows = ImportDuplicateDialog.Rows(report);
            Check(rows.Count == 4 && rows[0].CanKeep && rows[1].CanKeep && !rows[2].CanKeep, "keepable rows are not listed first");
            Check(ImportDuplicateDialog.Outcome(report.Findings[0], false) == "复用已有" && ImportDuplicateDialog.Outcome(report.Findings[1], true) == "仍然添加"
                && ImportDuplicateDialog.Outcome(report.Findings[1], false) == "跳过" && ImportDuplicateDialog.Outcome(report.Findings[3], false) == "照常导入",
                "row outcome text wrong");
            Check(ImportDuplicateDialog.Detail(report.Findings[2]).Contains("与本次选择的这一项重复") && ImportDuplicateDialog.Detail(report.Findings[1]).Contains("库里已有")
                && ImportDuplicateDialog.Detail(report.Findings[3]) == "\\\\nas\\c.jpg", "row detail text wrong");
            for (int i = 0; i < ImportDuplicateDialog.MaxRows + 5; i++)
                report.Add(ImportDuplicateKind.SameCategoryContent, null, "C:\\in\\many" + i + ".jpg", "D:\\lib\\m" + i, "", true, "m" + i);
            Check(ImportDuplicateDialog.Rows(report).Count == ImportDuplicateDialog.MaxRows, "row list is not capped");

            ImportDuplicateReport small = new ImportDuplicateReport();
            small.Add(ImportDuplicateKind.SameCategoryContent, null, "C:\\in\\a.jpg", "D:\\lib\\a.jpg", "", true, "h1");
            small.Add(ImportDuplicateKind.SameCategoryContent, null, "C:\\in\\b.jpg", "D:\\lib\\b.jpg", "", true, "h2");
            small.Add(ImportDuplicateKind.OtherCategoryContent, null, "C:\\in\\r.jpg", "D:\\lib\\r.jpg", "", false, "h3");
            Func<int[], bool, ICollection<string>> run = delegate(int[] toggles, bool confirm)
            {
                ICollection<string> keep = null;
                using (System.Windows.Forms.Timer probe = new System.Windows.Forms.Timer { Interval = 10 })
                {
                    probe.Tick += delegate
                    {
                        Form dialog = FindModalForm();
                        if (dialog == null) return;
                        probe.Stop();
                        dialog.Opacity = 0;
                        MethodInfo toggle = dialog.GetType().GetMethod("Toggle", Private);
                        foreach (int index in toggles) toggle.Invoke(dialog, new object[] { index });
                        dialog.GetType().GetMethod("ProcessCmdKey", Private).Invoke(dialog, new object[] { new Message(), confirm ? Keys.Enter : Keys.Escape });
                        if (!dialog.IsDisposed && dialog.Visible) dialog.Close();
                    };
                    probe.Start();
                    keep = ImportDuplicateDialog.Show(null, small);
                }
                return keep;
            };
            ICollection<string> kept = run(new int[] { 1, 2 }, true);   // 第 3 行不可勾选，点了也不算
            Check(kept != null && kept.Count == 1 && kept.Contains("C:\\in\\b.jpg"), "checked sources wrong: " + (kept == null ? "null" : string.Join("|", kept.ToArray())));
            Check(run(new int[] { 0 }, false) == null, "cancel did not return null");
            Check(ImportDuplicateDialog.Show(null, new ImportDuplicateReport()).Count == 0, "a report without findings prompted");
        }

        private static void DesktopBackupListRules()
        {
            DesktopBackupEntry folder = new DesktopBackupEntry { CurrentPath = "D:\\stash\\项目\\", OriginalPath = "C:\\Users\\me\\Desktop\\项目", IsDir = true };
            DesktopBackupEntry odd = new DesktopBackupEntry { CurrentPath = "D:\\stash\\bad|name.txt", OriginalPath = "" };
            Check(Controls.DesktopBackupListControl.DisplayName(folder) == "项目", "folder name wrong: " + Controls.DesktopBackupListControl.DisplayName(folder));
            Check(Controls.DesktopBackupListControl.DisplayName(odd).EndsWith("name.txt"), "invalid path characters threw or lost the name");
            Check(Controls.DesktopBackupListControl.OriginText(odd) == "原位置未知", "missing origin not explained");

            using (Controls.DesktopBackupListControl list = new Controls.DesktopBackupListControl())
            {
                list.Size = new Size(500, 300);
                IntPtr handle = list.Handle;
                List<DesktopBackupEntry> removed = new List<DesktopBackupEntry>();
                list.RemoveRequested += delegate(DesktopBackupEntry e) { removed.Add(e); };
                list.SetItems(new DesktopBackupEntry[] { folder, odd, null });
                Check(list.Count == 2, "null entries were not skipped");
                MethodInfo down = typeof(Control).GetMethod("OnKeyDown", Private);
                down.Invoke(list, new object[] { new KeyEventArgs(Keys.Down) });
                down.Invoke(list, new object[] { new KeyEventArgs(Keys.Down) });
                down.Invoke(list, new object[] { new KeyEventArgs(Keys.Delete) });
                Check(removed.Count == 1 && removed[0] == odd, "delete did not remove the selected card");
                list.SetBusy(true);
                down.Invoke(list, new object[] { new KeyEventArgs(Keys.Delete) });
                Check(removed.Count == 1, "remove was requested while busy");
                list.SetBusy(false);
                list.SetItems(new DesktopBackupEntry[] { folder });   // 清单变短：选中落到最后一项，不越界
                down.Invoke(list, new object[] { new KeyEventArgs(Keys.Delete) });
                Check(removed.Count == 2 && removed[1] == folder, "selection was lost after the list shrank");
            }
        }

        private static void CheckRowBehaviour()
        {
            using (Form host = new Form())
            {
                Controls.CheckRow box = new Controls.CheckRow { Text = "勾选" };
                Controls.CheckRow first = new Controls.CheckRow { Radio = true, Text = "一", Checked = true };
                Controls.CheckRow second = new Controls.CheckRow { Radio = true, Text = "二" };
                host.Controls.AddRange(new Control[] { box, first, second });
                int changes = 0;
                box.CheckedChanged += delegate { changes++; };
                MethodInfo click = typeof(Control).GetMethod("OnClick", Private);
                MethodInfo down = typeof(Control).GetMethod("OnKeyDown", Private);
                click.Invoke(box, new object[] { EventArgs.Empty });
                down.Invoke(box, new object[] { new KeyEventArgs(Keys.Space) });
                Check(!box.Checked && changes == 2, "check row did not toggle on click and space");
                click.Invoke(second, new object[] { EventArgs.Empty });
                Check(second.Checked && !first.Checked, "radio rows are not exclusive");
                click.Invoke(second, new object[] { EventArgs.Empty });
                Check(second.Checked, "clicking a checked radio row cleared it");
                down.Invoke(second, new object[] { new KeyEventArgs(Keys.Up) });
                Check(first.Checked && !second.Checked, "arrow up did not select the previous radio row");
                down.Invoke(first, new object[] { new KeyEventArgs(Keys.Up) });
                Check(first.Checked, "arrow up wrapped past the first radio row");
                Check(!box.Checked, "a radio row changed an unrelated check row");
            }
        }

        private static void GridThinScroll()
        {
            using (Form host = new Form())
            using (Controls.AppGridControl grid = new Controls.AppGridControl())
            {
                host.ClientSize = new Size(600, 300);
                grid.Dock = DockStyle.Fill;
                host.Controls.Add(grid);
                IntPtr handle = host.Handle;
                List<AppInfo> apps = new List<AppInfo>();
                for (int i = 0; i < 80; i++) apps.Add(new AppInfo { Key = "k" + i, Name = "条目 " + i, TargetPath = "C:\\x\\" + i + ".exe" });
                grid.SetApps(apps);
                int content = grid.AutoScrollMinSize.Height;
                Check(content > grid.ClientSize.Height, "fixture does not overflow");
                Check((GetWindowLong(grid.Handle, -16) & 0x00300000) == 0, "system scrollbars are still shown");
                grid.AutoScrollPosition = new Point(0, 120);
                Check(grid.AutoScrollPosition.Y == -120, "position API changed meaning: " + grid.AutoScrollPosition.Y);
                grid.AutoScrollPosition = new Point(0, 1000000);
                int max = content - grid.ClientSize.Height;
                Check(grid.AutoScrollPosition.Y == -max, "scrolling past the end was not clamped");
                typeof(Control).GetMethod("OnMouseWheel", Private).Invoke(grid, new object[] { new HandledMouseEventArgs(MouseButtons.None, 0, 10, 10, 120) });
                Check(grid.AutoScrollPosition.Y > -max, "wheel up did not scroll");
                grid.SetApps(apps.GetRange(0, 2));
                Check(grid.AutoScrollPosition.Y == 0, "position was not clamped after the content shrank");
            }
        }

        private static void ThinListScrollBar()
        {
            using (Form host = new Form())
            {
                host.ClientSize = new Size(300, 200);
                SoftwarePickerDialog.PickerList list = new SoftwarePickerDialog.PickerList();
                list.ItemHeight = 30;
                list.SetBounds(0, 0, 280, 180);
                host.Controls.Add(list);
                IntPtr handle = host.Handle;
                int fullWidth = list.ClientSize.Width;
                list.Items.AddRange(new object[] { "a", "b" });
                Check(list.RowWidth == fullWidth && (GetWindowLong(list.Handle, -16) & 0x00200000) == 0, "a short list reserved a scrollbar");
                for (int i = 0; i < 30; i++) list.Items.Add("row " + i);
                Check(list.RowWidth < fullWidth && list.RowBounds(0).Width == list.RowWidth, "an overflowing list did not show the thin scrollbar");
                list.TopIndex = 10;
                Check(list.TopIndex == 10, "the list could not scroll without a system scrollbar");
                list.Items.Clear();
                Check(list.RowWidth == fullWidth, "the thin scrollbar stayed after clearing the list");
            }
        }

        private static void RetrospectiveFixes()
        {
            // 1. 启动时的「修复项目目录」和扫描登记一样：默认不列出，也不挡住用户上一次操作的撤销
            Check(OperationHistory.IsSystem("catalog.repair") && OperationHistory.IsSystem("catalog.rebuild")
                && OperationHistory.IsSystem("catalog.discover") && !OperationHistory.IsSystem("catalogue"), "system record range differs from the undo rule");
            string path = Path.Combine(root, "retrospective-history", "config.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            AppConfig c = new AppConfig();
            c.CustomCategories.Add("工具");
            Check(c.SaveTo(path), "fixture export");
            c = ConfigDatabase.Load(path);
            Check(!c.LoadFailed, c.DatabaseError);
            c.CustomCategories.Add("游戏");
            Check(c.Save("OnAddCategory"), c.DatabaseError);
            long business = c.Database.LatestBusinessOperation();
            using (Sqlite db = new Sqlite(c.Database.FilePath, false))
                foreach (string action in new string[] { "catalog.repair", "catalog.discover" })
                    db.Execute("INSERT INTO operations(utc,action,summary,actor,session,pid) VALUES(?,?,?,?,?,?)",
                        DateTime.UtcNow.ToString("o"), action, "rows=1", "test", "test", 1);
            List<string[]> visible = c.Database.HistoryPage(0, 1000, false);
            Check(c.Database.LatestBusinessOperation() == business && visible.Count > 1 && Convert.ToInt64(visible[0][0]) == business
                && visible.All(row => !row[2].StartsWith("catalog.")), "a startup repair blocks undo or is listed by default");

            // 1b. 操作记录面板：切换记录后稍等再读明细；维护操作失败后重新读取记录，撤销资格不停在旧状态
            using (DatabasePanel dialog = new DatabasePanel(c.Database))
            using (Form panelHost = HostSettingsPanel(dialog))
            {
                Func<string, object> field = delegate(string name) { return typeof(DatabasePanel).GetField(name, Private).GetValue(dialog); };
                Func<bool> loaded = delegate
                {
                    return (string)field("placeholder") != "正在读取操作记录…" && (string)field("detailText") != "正在读取明细…";
                };
                PumpUntil(loaded, 15000);
                ListBox ops = (ListBox)field("opList");
                Control undo = (Control)field("undo");
                Check(ops.Items.Count > 1 && undo.Visible, "the latest business operation was not offered for undo after a startup repair");
                ops.SelectedIndex = 1;
                Check(((System.Windows.Forms.Timer)field("detailDelay")).Enabled && (string)field("detailText") == "正在读取明细…",
                    "details were read immediately instead of after the selection settled");
                ops.SelectedIndex = 0;
                PumpUntil(loaded, 15000);
                Check(!((System.Windows.Forms.Timer)field("detailDelay")).Enabled && (string)field("detailText") == null, "details never loaded after the selection settled");

                int versionBefore = (int)field("historyVersion");
                MethodInfo work = typeof(DatabasePanel).GetMethod("Work", Private);
                work.Invoke(dialog, new object[] { "正在测试…", (Func<string>)delegate { throw new IOException("模拟失败"); }, (Action<string>)delegate { } });
                Check(dialog.Busy, "the panel is not busy while maintenance runs");
                PumpUntil(delegate { return !(bool)field("running") && (int)field("historyVersion") > versionBefore; }, 15000);
                Label failure = (Label)field("status");
                Check(failure.Text.Contains("模拟失败") && failure.ForeColor == Theme.WarningText, "the failure was not reported in the status line: " + failure.Text);
                Check((int)field("historyVersion") > versionBefore && !dialog.Busy, "a failed maintenance step did not reload the history");
                PumpUntil(loaded, 15000);
            }
            c.Database.Undo(business);   // 修复和扫描记录更新也不挡：直接能撤销

            // 2. 批量加入（AddRange 内部暂停重绘）期间不逐项同步滑块，恢复重绘后同步一次
            using (Form host = new Form())
            {
                host.ClientSize = new Size(300, 200);
                SoftwarePickerDialog.PickerList list = new SoftwarePickerDialog.PickerList();
                list.ItemHeight = 30;
                list.SetBounds(0, 0, 280, 180);
                host.Controls.Add(list);
                IntPtr handle = host.Handle, listHandle = list.Handle;   // 宿主未显示时子控件不会自动建句柄
                int fullWidth = list.ClientSize.Width;
                list.BeginUpdate();
                for (int i = 0; i < 30; i++) list.Items.Add("row " + i);
                Check(list.RowWidth == fullWidth, "the scrollbar was synced for every row while redraw was suspended");
                list.EndUpdate();
                Check(list.RowWidth < fullWidth, "the scrollbar was not synced after redraw resumed");
                list.Items.Clear();
                object[] many = Enumerable.Range(0, 5000).Select(i => (object)("item " + i)).ToArray();
                Stopwatch fill = Stopwatch.StartNew();
                list.Items.AddRange(many);
                fill.Stop();
                Check(list.RowWidth < fullWidth && fill.ElapsedMilliseconds < 2000, "adding 5000 rows was slow or left the scrollbar hidden: " + fill.ElapsedMilliseconds + "ms");
            }

            // 3. 「是/否」提示框和系统提示框一样关不掉：Alt+F4（用户关闭）不能悄悄当成「否」；有取消的组合照常关闭
            using (MessageForm yesNo = new MessageForm("保存", "是否保存修改？", new string[] { "是", "否" },
                new DialogResult[] { DialogResult.Yes, DialogResult.No }, 0, DialogResult.None))
            {
                yesNo.Opacity = 0;
                yesNo.Show();
                yesNo.Close();
                Check(yesNo.Visible && !yesNo.IsDisposed, "a yes/no message box closed without an answer");
                typeof(Control).GetMethod("OnClick", Private).Invoke(FindControlByText(yesNo, "是"), new object[] { EventArgs.Empty });
                Check(!yesNo.Visible && yesNo.Result == DialogResult.Yes, "answering yes did not close the message box");
            }
            using (MessageForm okCancel = new MessageForm("提示", "继续吗？", new string[] { "确定", "取消" },
                new DialogResult[] { DialogResult.OK, DialogResult.Cancel }, 0, DialogResult.Cancel))
            {
                okCancel.Opacity = 0;
                okCancel.Show();
                okCancel.Close();
                Check(!okCancel.Visible && okCancel.Result == DialogResult.Cancel, "a cancellable message box could not be closed");
            }

            // 4. 重复文件列表：在一行按下、拖到另一行松开不算点击；同一行按下并松开才切换
            ImportDuplicateReport report = new ImportDuplicateReport();
            report.Add(ImportDuplicateKind.SameCategoryContent, null, "C:\\in\\a.jpg", "D:\\lib\\a.jpg", "", true, "h1");
            report.Add(ImportDuplicateKind.SameCategoryContent, null, "C:\\in\\b.jpg", "D:\\lib\\b.jpg", "", true, "h2");
            ICollection<string> kept = null;
            using (System.Windows.Forms.Timer probe = new System.Windows.Forms.Timer { Interval = 10 })
            {
                probe.Tick += delegate
                {
                    Form dialog = FindModalForm();
                    if (dialog == null) return;
                    probe.Stop();
                    dialog.Opacity = 0;
                    ListBox rows = (ListBox)dialog.GetType().GetField("list", Private).GetValue(dialog);
                    Rectangle first = rows.GetItemRectangle(0), second = rows.GetItemRectangle(1);
                    Point a = new Point(first.Left + 20, first.Top + first.Height / 2), b = new Point(second.Left + 20, second.Top + second.Height / 2);
                    MethodInfo down = typeof(Control).GetMethod("OnMouseDown", Private), up = typeof(Control).GetMethod("OnMouseUp", Private);
                    down.Invoke(rows, new object[] { new MouseEventArgs(MouseButtons.Left, 1, a.X, a.Y, 0) });
                    up.Invoke(rows, new object[] { new MouseEventArgs(MouseButtons.Left, 1, b.X, b.Y, 0) });   // 旧逻辑会在这里勾上第二行
                    down.Invoke(rows, new object[] { new MouseEventArgs(MouseButtons.Left, 1, a.X, a.Y, 0) });
                    up.Invoke(rows, new object[] { new MouseEventArgs(MouseButtons.Left, 1, a.X, a.Y, 0) });
                    dialog.GetType().GetMethod("ProcessCmdKey", Private).Invoke(dialog, new object[] { new Message(), Keys.Enter });
                    if (!dialog.IsDisposed && dialog.Visible) dialog.Close();
                };
                probe.Start();
                kept = ImportDuplicateDialog.Show(null, report);
            }
            Check(kept != null && kept.Count == 1 && kept.Contains("C:\\in\\a.jpg"),
                "dragging across rows toggled the wrong row: " + (kept == null ? "null" : string.Join("|", kept.ToArray())));

            // 5. 正在编辑的多行框不切换滚动条（切换会重建句柄、清空撤销记录），焦点离开后再补上
            using (Form host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000) })
            {
                TextBox box = new TextBox { Multiline = true, Bounds = new Rectangle(0, 0, 200, 40) };
                Button other = new Button { Bounds = new Rectangle(0, 60, 80, 30) };
                InputDialog.ScrollBarsWhenNeeded(box);
                host.Controls.Add(box);
                host.Controls.Add(other);
                host.Show();
                Application.DoEvents();
                box.Focus();
                Check(box.Focused, "the text box could not take focus");
                {
                    IntPtr before = box.Handle;
                    box.Paste(string.Join("\r\n", Enumerable.Range(0, 30).Select(i => "第 " + i + " 行").ToArray()));
                    Application.DoEvents();
                    Check(box.ScrollBars == ScrollBars.None && box.Handle == before && box.CanUndo, "editing recreated the text box and lost its undo history");
                    other.Focus();
                    PumpUntil(delegate { return box.ScrollBars == ScrollBars.Vertical; }, 2000);
                    Check(box.ScrollBars == ScrollBars.Vertical, "the scrollbar did not appear after leaving the overflowing text box");
                }
                host.Hide();
            }

            // 7. 扫描范围：盘符已消失（拔掉的 U 盘）提示「磁盘未就绪」，不说成不支持
            char missing = "ZYXWVUTSRQPONMLKJIHGFED".FirstOrDefault(letter => new DriveInfo(letter + ":\\").DriveType == DriveType.NoRootDirectory);
            if (missing != '\0')
            {
                string normalized, reason;
                Check(!LocalScanScope.TryValidate(missing + @":\照片", null, out normalized, out reason) && reason == "磁盘未就绪。",
                    "a vanished drive was not reported as not ready: " + reason);
            }
            else Check(false, "no free drive letter to test a vanished drive");

            // 6. 布局备份：读取正忙时等它结束，不返回空列表（会被显示成「还没有备份」）；操作成功后保持停用直到新列表换上
            object listLock = typeof(LayoutBackup).GetField("listLock", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            string backupId = "20260101-retro01";
            string backupDir = Path.Combine(Paths.BackupsDir, backupId);
            Directory.CreateDirectory(backupDir);
            File.WriteAllText(Path.Combine(backupDir, "config.xml"), "<AppConfig />");
            File.WriteAllText(Path.Combine(backupDir, "meta.xml"), "<Backup name=\"retro\" />");
            try
            {
                List<LayoutBackupInfo> listed = null;
                Thread reader;
                Monitor.Enter(listLock);
                try
                {
                    reader = new Thread(delegate() { listed = LayoutBackup.List(); });
                    reader.Start();
                    Check(!reader.Join(300), "listing returned while another read held the lock");
                }
                finally { Monitor.Exit(listLock); }
                Check(reader.Join(10000) && listed != null && listed.Any(i => i.Id == backupId), "listing gave up instead of waiting for the earlier read");

                using (LayoutBackupPanel form = new LayoutBackupPanel())
                using (Form panelHost = HostSettingsPanel(form))
                {
                    Controls.BackupListControl cards = (Controls.BackupListControl)typeof(LayoutBackupPanel).GetField("list", Private).GetValue(form);
                    FieldInfo busy = typeof(Controls.BackupListControl).GetField("busy", Private);
                    FieldInfo running = typeof(LayoutBackupPanel).GetField("operationRunning", Private);
                    FieldInfo reloading = typeof(LayoutBackupPanel).GetField("reloadRunning", Private);
                    FieldInfo loading = typeof(Controls.BackupListControl).GetField("loading", Private);
                    PumpUntil(delegate { return !(bool)loading.GetValue(cards) && !(bool)reloading.GetValue(form); }, 15000);
                    MethodInfo operate = typeof(LayoutBackupPanel).GetMethod("RunBackupOperation", Private);
                    Action reload = (Action)Delegate.CreateDelegate(typeof(Action), form, typeof(LayoutBackupPanel).GetMethod("ReloadList", Private, null, Type.EmptyTypes, null));
                    Monitor.Enter(listLock);
                    try
                    {
                        operate.Invoke(form, new object[] { backupId, "正在重命名…", "失败：", (Func<string>)delegate { return null; }, reload });
                        PumpUntil(delegate { return !(bool)running.GetValue(form); }, 15000);
                        Check(!(bool)running.GetValue(form) && (bool)busy.GetValue(cards), "cards became clickable before the refreshed list arrived");
                    }
                    finally { Monitor.Exit(listLock); }
                    PumpUntil(delegate { return !(bool)busy.GetValue(cards); }, 15000);
                    Check(!(bool)busy.GetValue(cards) && !(bool)reloading.GetValue(form) && !form.Busy, "cards stayed disabled after the list was refreshed");
                }
            }
            finally { Directory.Delete(backupDir, true); }
        }

        private static void ExportChangesAtomic()
        {
            string dir = Path.Combine(root, "export-changes");
            Directory.CreateDirectory(dir);
            AppConfig c = new AppConfig();
            c.CustomCategories.Add("工具");
            Check(c.SaveTo(Path.Combine(dir, "config.xml")), "fixture export");
            c = ConfigDatabase.Load(Path.Combine(dir, "config.xml"));
            Check(!c.LoadFailed, c.DatabaseError);
            c.CustomCategories.Add("游戏");
            Check(c.Save("OnAddCategory"), c.DatabaseError);
            long good = c.Database.LatestBusinessOperation();
            string target = Path.Combine(dir, "明细.xml");
            // 不用通配符：Windows 下「明细.xml.*」也会匹配「明细.xml」本身
            Func<string, int> sideFiles = delegate(string name)
            {
                return Directory.GetFiles(dir).Count(f => Path.GetFileName(f).StartsWith(name + ".", StringComparison.OrdinalIgnoreCase));
            };
            Func<int> leftovers = delegate { return sideFiles("明细.xml"); };

            // 正常导出：新文件完整，换掉旧文件，不留临时文件
            File.WriteAllText(target, "旧内容");
            c.Database.ExportChanges(good, target);
            string exported = File.ReadAllText(target);
            Check(exported.Contains("<Operation") && exported.Contains("</Operation>") && exported.Contains("游戏") && leftovers() == 0,
                "export did not replace the old file cleanly: " + leftovers());

            // 写到一半失败（记录里有 XML 不允许的控制字符）：已有的文件原样保留，也不留半截临时文件
            long broken;
            using (Sqlite db = new Sqlite(c.Database.FilePath, false))
            {
                db.Execute("INSERT INTO operations(utc,action,summary,actor,session,pid) VALUES(?,?,?,?,?,?)",
                    DateTime.UtcNow.ToString("o"), "OnRenameCategory", "rows=2", "test", "test", 1);
                broken = Convert.ToInt64(db.Scalar("SELECT max(id) FROM operations"));
                db.Execute("INSERT INTO changes(operation_id,entity,row_id,undo_sql,redo_sql) VALUES(?,?,?,?,?)", broken, "groups", 1, "SELECT 1", "SELECT 1");
                db.Execute("INSERT INTO changes(operation_id,entity,row_id,undo_sql,redo_sql) VALUES(?,?,?,?,?)", broken, "groups", 1, "SELECT '\u0001'", "SELECT 1");
            }
            bool failed = false;
            try { c.Database.ExportChanges(broken, target); }
            catch (ArgumentException) { failed = true; }
            Check(failed && File.ReadAllText(target) == exported && leftovers() == 0,
                "a failed export damaged the existing file or left a partial file: failed=" + failed + " leftovers=" + leftovers());

            // 目标不存在时失败：不生成目标文件
            string fresh = Path.Combine(dir, "新明细.xml");
            failed = false;
            try { c.Database.ExportChanges(broken, fresh); }
            catch (ArgumentException) { failed = true; }
            Check(failed && !File.Exists(fresh) && sideFiles("新明细.xml") == 0, "a failed export created a partial file");

            // 只读的已有文件：不覆盖，内容和只读属性都不变，也不留临时或旧文件副本
            string locked = Path.Combine(dir, "只读.xml");
            File.WriteAllText(locked, "保留");
            File.SetAttributes(locked, FileAttributes.ReadOnly);
            try
            {
                failed = false;
                try { c.Database.ExportChanges(good, locked); }
                catch (IOException ex) { failed = ex.Message.Contains("只读"); }
                Check(failed && File.ReadAllText(locked) == "保留" && (File.GetAttributes(locked) & FileAttributes.ReadOnly) != 0
                    && sideFiles("只读.xml") == 0, "a read-only export target was overwritten or left side files: " + sideFiles("只读.xml"));
            }
            finally { File.SetAttributes(locked, FileAttributes.Normal); }
        }

        private static void ThemedToolTipMeasure()
        {
            using (Form host = new Form())
            using (Controls.ThemedToolTip tip = new Controls.ThemedToolTip())
            {
                Button bound = new Button();
                host.Controls.Add(bound);
                MethodInfo measure = typeof(Controls.ThemedToolTip).GetMethod("OnPopupMeasure", Private);
                Func<Control, PopupEventArgs> popup = delegate(Control control)
                {
                    PopupEventArgs e = new PopupEventArgs(host, control, false, new Size(1, 1));
                    measure.Invoke(tip, new object[] { tip, e });
                    return e;
                };
                Check(popup(host).Cancel, "a tooltip with no text was shown");
                tip.SetToolTip(bound, "很长的一段说明文字，用来确认提示会按最大宽度换行，而不是拉成一整行横穿屏幕。很长的一段说明文字，用来确认提示会按最大宽度换行。");
                PopupEventArgs boundTip = popup(bound);
                Check(!boundTip.Cancel && boundTip.ToolTipSize.Width <= DpiHelper.Scale(440, DpiHelper.ForControl(bound)) + 2
                    && boundTip.ToolTipSize.Height > boundTip.ToolTipSize.Width / 20, "bound tooltip was not wrapped: " + boundTip.ToolTipSize);
                // 直接 Show(文字, 窗口, …) 的提示不挂在控件上：要用 Show 时给的文字来量
                typeof(Controls.ThemedToolTip).GetField("shownText", Private).SetValue(tip, "当前以管理员身份运行");
                PopupEventArgs shown = popup(host);
                Check(!shown.Cancel && shown.ToolTipSize.Width > 20, "directly shown tooltip was cancelled or unsized");
            }
        }

        // 三个大弹窗改成窗口内页面：选封面页嵌在主窗口里（编辑页留在下面、选完带着封面回来）；
        // 桌面备份清单是设置页里的一个目录，忙碌时挡住返回和目录切换。
        private static void EmbeddedPages()
        {
            AppConfig config = RenameConfig("embedded-pages");
            config.AutoScan = false;
            string key = CollectionKeys.Make("照片", "E旅行");
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "E旅行", Description = "原简介" });
            string imageDir = Path.Combine(root, "embedded-pages-images");
            Directory.CreateDirectory(imageDir);
            string image = Path.Combine(imageDir, "cover.png");
            using (Bitmap bitmap = new Bitmap(64, 40)) bitmap.Save(image, System.Drawing.Imaging.ImageFormat.Png);
            AppInfo item = AddEntry(config, "照片", Path.Combine(config.ResourceStorePath, "照片", "embedded-photo.png"), false, true, "embedded photo");
            Check(config.Save(), "embedded pages fixture save failed");

            using (MainForm form = ResourceForm(config, "照片", item))
            {
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-3000, -3000);
                form.ClientSize = new Size(1000, 680);
                form.Show();
                Application.DoEvents();

                Type formType = typeof(MainForm), panelType = typeof(Controls.CollectionInfoPanel);
                Controls.CollectionInfoPanel info = (Controls.CollectionInfoPanel)formType.GetField("collectionInfo", Private).GetValue(form);
                Control sidebar = (Control)formType.GetField("sidebar", Private).GetValue(form);
                Controls.SettingsPage settings = (Controls.SettingsPage)formType.GetField("settingsPage", Private).GetValue(form);
                FieldInfo coverField = formType.GetField("coverPage", Private);
                FieldInfo editCover = panelType.GetField("editCoverPath", Private);
                MethodInfo keys = formType.GetMethod("ProcessCmdKey", Private);
                Func<CoverPickerDialog> openCover = delegate
                {
                    Invoke(form, "OnCollectionPickCover");
                    Application.DoEvents();
                    return (CoverPickerDialog)coverField.GetValue(form);
                };

                formType.GetField("currentCollection", Private).SetValue(form, null);
                formType.GetField("collectionEntered", Private).SetValue(form, true);
                Invoke(form, "RefreshGrid");

                // 不在编辑页时点不开
                Check(openCover() == null, "the cover page opened outside the edit page");

                Invoke(form, "OnCollectionSelected", key);
                Check(info.Visible && !info.IsEditing, "fixture did not show the collection info page");
                panelType.GetMethod("RequestEdit", Private).Invoke(info, null);
                TextBox desc = (TextBox)panelType.GetField("descBox", Private).GetValue(info);
                desc.Text = "改到一半";
                Check(info.IsEditing && info.HasUnsavedEdit, "fixture did not enter the edit page");
                string coverBefore = (string)editCover.GetValue(info);

                // 1. 打开：嵌在主窗口里、铺满标题行以下、在最上面；编辑页原样留着，侧栏收起（它的标题在标题行里，页面盖不到）
                CoverPickerDialog page = openCover();
                Check(page != null && !page.TopLevel && page.Parent == form && page.Visible, "the cover page did not open inside the main window");
                Check(page.Left == 0 && page.Top > 0 && page.Width == form.ClientSize.Width && page.Bottom == form.ClientSize.Height,
                    "the cover page does not fill the area below the title row: " + page.Bounds);
                Check(form.Controls.GetChildIndex(page) == 0 && !sidebar.Visible, "the cover page is not on top or the sidebar title still shows");
                Check(info.IsEditing && desc.Text == "改到一半", "opening the cover page ended the edit");
                Check(!(bool)formType.GetProperty("CanImportHere", Private).GetValue(form, null), "drops and pastes still import while the cover page is open");

                // 后台刷新、状态条不能把别的控件顶到页面上面，也不能结束编辑；窗口缩放时页面跟着铺满
                Invoke(form, "RefreshGrid");
                Invoke(form, "ShowStatusBar", "测试", 0, 0);
                Application.DoEvents();
                Check(form.Controls.GetChildIndex(page) == 0 && info.IsEditing && desc.Text == "改到一半", "a refresh surfaced controls over the cover page or ended the edit");
                Invoke(form, "HideStatusBar");
                Size resized = new Size(form.MinimumSize.Width + 40, form.MinimumSize.Height + 40);
                form.ClientSize = resized;
                Application.DoEvents();
                Check(page.Width == resized.Width && page.Bottom == resized.Height, "the cover page did not follow the window size: " + page.Bounds);
                Check(ReferenceEquals(openCover(), page), "a second cover page was opened");

                // 2. Esc 取消：页面关闭并释放，编辑页和已填内容都在，封面没变，侧栏恢复
                keys.Invoke(form, new object[] { new Message(), Keys.Escape });
                Application.DoEvents();
                Check(coverField.GetValue(form) == null && page.IsDisposed, "escape did not close the cover page");
                Check(info.IsEditing && desc.Text == "改到一半" && (string)editCover.GetValue(info) == coverBefore && sidebar.Visible,
                    "cancelling the cover page changed the edit page");

                // 3. 选定：结果交回编辑页（保存时才写入配置）
                page = openCover();
                page.ResultPath = image;
                page.DialogResult = DialogResult.OK;
                page.Close();
                Application.DoEvents();
                Check(coverField.GetValue(form) == null && info.IsEditing && desc.Text == "改到一半", "picking a cover closed the edit page");
                Check(string.Equals((string)editCover.GetValue(info), image, StringComparison.OrdinalIgnoreCase), "the picked cover did not reach the edit page");
                Check(string.IsNullOrEmpty(CollectionOps.FindByKey(config, key).Cover), "the cover was written before saving");

                // 4. 进入设置页：放弃正在进行的选封面
                page = openCover();
                Check(page != null, "the cover page did not reopen");
                Invoke(form, "OpenSettings");
                Application.DoEvents();
                Check(coverField.GetValue(form) == null && page.IsDisposed && settings.Visible, "opening settings left the cover page behind");

                // 5. 桌面备份清单是设置页里的目录：空清单不能全部还原；忙碌时不能切目录、不能离开设置页
                Invoke(form, "OpenSettingsSection", Controls.SettingsPage.SettingsSection.DesktopBackup);
                Controls.DesktopBackupPanel backup = (Controls.DesktopBackupPanel)formType.GetField("desktopBackupPanel", Private).GetValue(form);
                Control restoreAll = (Control)typeof(Controls.DesktopBackupPanel).GetField("restoreAll", Private).GetValue(backup);
                Check(settings.Section == Controls.SettingsPage.SettingsSection.DesktopBackup && backup.Visible && backup.Parent == settings,
                    "the desktop backup list did not open as a section of the settings page");
                Check(!restoreAll.Enabled, "restore-all is offered for an empty list");
                config.DesktopBackup.Add(new DesktopBackupEntry { CurrentPath = "D:\\stash\\a.lnk", OriginalPath = "C:\\Desktop\\a.lnk" });
                try
                {
                    Invoke(form, "RefreshDesktopBackupPanel");
                    Check(restoreAll.Enabled, "restore-all stayed disabled with an entry in the list");
                    int busyEvents = 0;
                    backup.BusyChanged += delegate { busyEvents++; };
                    backup.SetBusy(true);
                    Check(backup.Busy && settings.Busy && !restoreAll.Enabled && busyEvents == 1, "the busy state did not reach the settings page");
                    Check(!settings.SelectSection(Controls.SettingsPage.SettingsSection.General), "the section changed while the list was busy");
                    Invoke(form, "CloseSettings");
                    Check(settings.Visible, "settings closed while the list was busy");
                    backup.SetBusy(false);
                    backup.SetBusy(false);   // 重复设置不重复通知
                    Check(!backup.Busy && restoreAll.Enabled && busyEvents == 2, "leaving the busy state was not reported once");
                }
                finally { config.DesktopBackup.Clear(); }
                Invoke(form, "CloseSettings");
                Check(!settings.Visible, "settings stayed open");
            }
        }

        // 线条图标随显示缩放变大（以前写死像素，125% 以上图标比字小一圈）：192 DPI 下墨迹约为 96 DPI 的两倍，
        // 大小只跟 DPI 有关、与传入的定位框大小无关，并且始终画在定位框正中。
        private static void IconsScaleWithDpi()
        {
            const int canvas = 120;
            Func<UiIconKind, int, int, Rectangle> ink = delegate(UiIconKind kind, int dpi, int box)
            {
                using (Bitmap bitmap = new Bitmap(canvas, canvas))
                {
                    using (Graphics g = Graphics.FromImage(bitmap))
                    {
                        g.Clear(Color.White);
                        Draw.DrawIcon(g, kind, new Rectangle((canvas - box) / 2, (canvas - box) / 2, box, box), Color.Black, dpi);
                    }
                    int left = canvas, top = canvas, right = -1, bottom = -1;
                    for (int y = 0; y < canvas; y++)
                        for (int x = 0; x < canvas; x++)
                            if (bitmap.GetPixel(x, y).R < 128)
                            {
                                left = Math.Min(left, x); top = Math.Min(top, y);
                                right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                            }
                    return right < 0 ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
                }
            };
            foreach (UiIconKind kind in new UiIconKind[] { UiIconKind.Add, UiIconKind.Organize, UiIconKind.Search, UiIconKind.PanelLeftClose })
            {
                Rectangle normal = ink(kind, 96, 18), doubled = ink(kind, 192, 18), bigBox = ink(kind, 96, 60), low = ink(kind, 72, 18);
                Check(normal.Width >= 9 && normal.Width <= 18 && normal.Height >= 9 && normal.Height <= 19, kind + ": unexpected size at 96 DPI: " + normal);
                Check(Math.Abs(doubled.Width - normal.Width * 2) <= 3 && Math.Abs(doubled.Height - normal.Height * 2) <= 3,
                    kind + ": did not double at 192 DPI: " + normal.Size + " -> " + doubled.Size);
                Check(Math.Abs(bigBox.Width - normal.Width) <= 1 && Math.Abs(bigBox.Height - normal.Height) <= 1,
                    kind + ": grew with its positioning box: " + bigBox.Size);
                Check(low.Size == normal.Size, kind + ": shrank below the 96 DPI size");   // 低于 96 的值按 96 画
                foreach (Rectangle r in new Rectangle[] { normal, doubled, bigBox })
                    Check(Math.Abs(r.Left + r.Right - canvas) <= 3 && Math.Abs(r.Top + r.Bottom - canvas) <= 3, kind + ": not centred: " + r);
            }
            // 圆角规格只有三档，且从大到小
            Check(Theme.RadiusCard > Theme.RadiusControl && Theme.RadiusControl > Theme.RadiusChip && Theme.RadiusChip > 0, "radius tokens out of order");
        }

        private static void ReviewFollowUps()
        {
            // 1. 文本框一打开就放不下：句柄创建时不能重建句柄（曾抛「创建窗口句柄时出错」），显示后才切换滚动条
            using (Form host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000) })
            {
                TextBox box = new TextBox { Multiline = true, Bounds = new Rectangle(0, 0, 200, 40) };
                InputDialog.ScrollBarsWhenNeeded(box);
                box.Text = string.Join("\r\n", Enumerable.Range(0, 30).Select(i => "第 " + i + " 行").ToArray());
                host.Controls.Add(box);
                host.Show();
                PumpUntil(delegate { return box.ScrollBars == ScrollBars.Vertical; }, 2000);
                Check(box.ScrollBars == ScrollBars.Vertical && box.Text.StartsWith("第 0 行"), "overflowing text box did not get its scrollbar after opening");
                box.Text = "短";
                Check(box.ScrollBars == ScrollBars.None, "scrollbar stayed after the text became short");
                host.Hide();
            }

            // 1b. 集合编辑页的简介框：无边框多行框在创建过程中会自己调一次高度（触发尺寸变化），
            // 文字又长到要换行时，曾在句柄还没建完时切换滚动条而抛「创建窗口句柄时出错」
            using (Form host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000) })
            {
                Panel panel = new Panel { Visible = false };
                TextBox box = new TextBox { Multiline = true, BorderStyle = BorderStyle.None, Font = Theme.BodyFont };
                InputDialog.ScrollBarsWhenNeeded(box);
                panel.Controls.Add(box);
                host.Controls.Add(panel);
                host.Show();
                Application.DoEvents();
                box.Text = "2026 年春天出门拍的照片，按地点再分了子集合。";
                panel.Visible = true;   // 修复前在这里抛异常
                Check(box.IsHandleCreated && box.ScrollBars == ScrollBars.None, "the scrollbar was switched while the handle was still being created");
                PumpUntil(delegate { return box.ScrollBars == ScrollBars.Vertical; }, 2000);
                Check(box.ScrollBars == ScrollBars.Vertical && box.Text.StartsWith("2026"), "wrapping text in a borderless box did not get its scrollbar after opening");
                box.Text = "";   // 相邻边界：空简介不需要滚动条
                Check(box.ScrollBars == ScrollBars.None, "scrollbar stayed after the borderless box was emptied");
                host.Hide();
            }

            // 3. 桌面备份清单：松开回车才还原；按住不放产生的重复回车不算
            using (Controls.DesktopBackupListControl list = new Controls.DesktopBackupListControl())
            {
                list.Size = new Size(400, 300);
                IntPtr handle = list.Handle;
                DesktopBackupEntry entry = new DesktopBackupEntry { CurrentPath = "D:\\stash\\a.txt", OriginalPath = "C:\\a.txt" };
                List<DesktopBackupEntry> restored = new List<DesktopBackupEntry>();
                list.RestoreRequested += delegate(DesktopBackupEntry e) { restored.Add(e); };
                list.SetItems(new DesktopBackupEntry[] { entry });
                MethodInfo down = typeof(Control).GetMethod("OnKeyDown", Private), up = typeof(Control).GetMethod("OnKeyUp", Private);
                FieldInfo repeat = typeof(Controls.DesktopBackupListControl).GetField("enterRepeat", Private);
                down.Invoke(list, new object[] { new KeyEventArgs(Keys.Down) });
                repeat.SetValue(list, true);
                down.Invoke(list, new object[] { new KeyEventArgs(Keys.Enter) });
                up.Invoke(list, new object[] { new KeyEventArgs(Keys.Enter) });
                Check(restored.Count == 0, "a held-down enter restored an entry");
                repeat.SetValue(list, false);
                down.Invoke(list, new object[] { new KeyEventArgs(Keys.Enter) });
                Check(restored.Count == 0, "restore started on key down");
                up.Invoke(list, new object[] { new KeyEventArgs(Keys.Enter) });
                Check(restored.Count == 1 && restored[0] == entry, "enter did not restore the selected entry");
            }

            // 4 + 5. 勾选项只认左键；单选组只在选中项上停 Tab
            using (Form host = new Form())
            {
                Controls.CheckRow box = new Controls.CheckRow { Text = "勾选" };
                Controls.CheckRow first = new Controls.CheckRow { Radio = true, Checked = true };
                Controls.CheckRow second = new Controls.CheckRow { Radio = true };
                host.Controls.AddRange(new Control[] { box, first, second });
                MethodInfo click = typeof(Control).GetMethod("OnClick", Private);
                click.Invoke(box, new object[] { new MouseEventArgs(MouseButtons.Right, 1, 2, 2, 0) });
                Check(!box.Checked, "right click toggled a check row");
                click.Invoke(box, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 2, 2, 0) });
                Check(box.Checked, "left click did not toggle a check row");
                Check(first.TabStop && !second.TabStop && box.TabStop, "radio group tab stops wrong before change");
                second.Checked = true;
                Check(!first.TabStop && second.TabStop, "radio group tab stop did not follow the selection");
            }

            // 7. 非 STA 线程（线程池等）不建自绘提示框，退回系统提示框
            MethodInfo onUi = typeof(MessageDialog).GetMethod("OnUiThread", BindingFlags.Static | BindingFlags.NonPublic);
            bool fromMta = true;
            Thread worker = new Thread(delegate() { fromMta = (bool)onUi.Invoke(null, new object[] { null }); });
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
            worker.Join();
            Check(!fromMta, "a non-STA thread was treated as the UI thread");
            using (TextBox noHandle = new TextBox())
                Check((bool)onUi.Invoke(null, new object[] { noHandle }), "UI thread with an unhandled owner was rejected");
        }

        /// <summary>关掉测试没预料到的模态窗口。「是/否」提示框和系统提示框一样不能直接关，先点「否」或「取消」。</summary>
        private static void DismissModal(Form modal)
        {
            if (modal is MessageForm && (ClickMessageDialog(modal, 7) || ClickMessageDialog(modal, 2))) return;
            modal.Close();
        }

        private static Form FindModalForm()
        {
            foreach (Form form in Application.OpenForms)
                if (form.Visible && form.Modal) return form;
            return null;
        }

        private static Control FindControlByText(Control parent, string text)
        {
            if (parent == null) return null;
            if (parent.Text == text) return parent;
            foreach (Control child in parent.Controls)
            {
                Control found = FindControlByText(child, text);
                if (found != null) return found;
            }
            return null;
        }

        private static void RunDeleteFlow(Action action, bool accept, string nativeTitle, int nativeCommand,
            out bool confirmClicked, out bool nativeClicked, out bool unexpected)
        {
            bool confirmed = false, answered = false, stray = false;
            Stopwatch elapsed = Stopwatch.StartNew();
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 10 })
            {
                timer.Tick += delegate
                {
                    if (!confirmed)
                    {
                        Form dialog = FindModalForm();
                        if (dialog == null) return;
                        dialog.Opacity = 0;
                        Control button = FindControlByText(dialog, accept ? "确定" : "取消") ?? ConfirmButton(dialog, accept);
                        if (button == null)
                        {
                            if (elapsed.ElapsedMilliseconds > 3000) { stray = true; dialog.Close(); }
                            return;
                        }
                        typeof(Control).GetMethod("OnClick", Private).Invoke(button, new object[] { EventArgs.Empty });
                        confirmed = true;
                        return;
                    }
                    if (nativeTitle != null && !answered)
                    {
                        Form message = FindMessageDialog(nativeTitle);
                        if (message != null && ClickMessageDialog(message, nativeCommand))
                        {
                            answered = true;
                            return;
                        }
                    }
                    if (elapsed.ElapsedMilliseconds > 300 && !answered)
                    {
                        IntPtr other = FindOwnDialog(null);
                        Form modal = FindModalForm();
                        if (other != IntPtr.Zero || modal != null)
                        {
                            stray = true;
                            answered = true;
                            if (other != IntPtr.Zero) PostMessage(other, 0x0010, IntPtr.Zero, IntPtr.Zero);
                            if (modal != null) DismissModal(modal);
                            return;
                        }
                    }
                    if (elapsed.ElapsedMilliseconds > 8000)
                    {
                        stray = true;
                        IntPtr other = FindOwnDialog(null);
                        if (other != IntPtr.Zero) PostMessage(other, 0x0010, IntPtr.Zero, IntPtr.Zero);
                        Form modal = FindModalForm();
                        if (modal != null) DismissModal(modal);
                        timer.Stop();
                    }
                };
                timer.Start();
                action();
                timer.Stop();
            }
            confirmClicked = confirmed;
            nativeClicked = answered;
            unexpected = stray;
        }

        /// <summary>找标题为 title 的自绘提示框（MessageDialog）；title 为 null 时匹配任意标题。</summary>
        private static Form FindMessageDialog(string title)
        {
            foreach (Form form in Application.OpenForms)
                if (form.Visible && form.Modal && form is MessageForm && (title == null || form.Text == title)) return form;
            return null;
        }

        /// <summary>
        /// 点自绘提示框上的按钮，沿用系统提示框的按钮编号：1 确定、6 是、7 否、2 取消；
        /// 只有一个按钮的提示框没有「取消」，2 对应它唯一的「知道了」（系统提示框里 2 同样等于关掉它）。
        /// </summary>
        private static bool ClickMessageDialog(Form dialog, int command)
        {
            string text = command == 6 ? "是" : (command == 7 ? "否" : (command == 1 ? "确定" : "取消"));
            Control button = null;
            foreach (Control child in dialog.Controls) if (child.Text == text) button = child;
            if (button == null && command == 2)
                foreach (Control child in dialog.Controls) if (child.Text == "知道了") button = child;
            if (button == null && command == 1) button = ConfirmButton(dialog, true);   // 主按钮写的是具体动作时
            if (button == null) return false;
            dialog.Opacity = 0;
            typeof(Control).GetMethod("OnClick", Private).Invoke(button, new object[] { EventArgs.Empty });
            return true;
        }

        private static bool AnswerMessageDialog(string title, int command, Action action)
        {
            bool unexpected;
            return AnswerMessageDialog(title, command, action, out unexpected);
        }

        /// <summary>执行 action，期间出现标题为 title 的提示框就按 command 回答；返回是否回答过，unexpected 表示还冒出了别的对话框。</summary>
        private static bool AnswerMessageDialog(string title, int command, Action action, out bool unexpected)
        {
            bool answered = false;
            bool stray = false;
            Stopwatch elapsed = Stopwatch.StartNew();
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 10 })
            {
                timer.Tick += delegate
                {
                    Form message = answered ? null : FindMessageDialog(title);
                    if (message != null && ClickMessageDialog(message, command))
                    {
                        answered = true;
                        return;
                    }
                    Form modal = FindModalForm();
                    IntPtr other = FindOwnDialog(null);
                    if (!answered)
                    {
                        if (modal != null && elapsed.ElapsedMilliseconds > 300) DismissModal(modal);
                    }
                    else if (modal != null || other != IntPtr.Zero)
                    {
                        stray = true;
                        if (modal != null) DismissModal(modal);
                        if (other != IntPtr.Zero) PostMessage(other, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    }
                    if (elapsed.ElapsedMilliseconds > 8000)
                    {
                        stray = true;
                        if (modal != null) DismissModal(modal);
                        if (other != IntPtr.Zero) PostMessage(other, 0x0010, IntPtr.Zero, IntPtr.Zero);
                        timer.Stop();
                    }
                };
                timer.Start();
                action();
                timer.Stop();
            }
            unexpected = stray;
            return answered;
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

        // 系统自带工具按启动目标识别（Windows 目录、shell 虚拟项、系统应用包），用户安装的软件和应用商店应用不算；
        // 对话框默认不列系统工具，搜索时名称直接包含的排在模糊匹配之前；链接目标随扫描缓存保存。
        private static void SoftwarePickerRules()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
            string apps = Scanner.AppsFolderPrefix;
            AppInfo installed = new AppInfo { Key = "lnk:picker-apifox", Name = "Apifox", TargetPath = "D:\\APP\\ApiFox\\Apifox.exe", Category = "开发工具" };
            AppInfo cmd = new AppInfo { Key = "af:picker-cmd", Name = "命令提示符", TargetPath = windows + "\\system32\\cmd.exe", LinkTarget = windows + "\\system32\\cmd.exe" };
            AppInfo taskmgr = new AppInfo { Key = "af:picker-taskmgr", Name = "任务管理器", IsUwp = true,
                TargetPath = apps + "Microsoft.AutoGenerated.{5E8FC15D-96C5-8C7D-B1AF-2757B67E4C6D}", LinkTarget = windows.ToUpperInvariant() + "\\System32\\taskmgr.exe" };
            AppInfo excel = new AppInfo { Key = "af:picker-excel", Name = "Excel", IsUwp = true,
                TargetPath = apps + "Microsoft.Office.EXCEL.EXE.15", LinkTarget = "C:\\Program Files\\Microsoft Office\\root\\Office16\\EXCEL.EXE" };
            AppInfo control = new AppInfo { Key = "af:picker-control", Name = "控制面板", IsUwp = true,
                TargetPath = apps + "Microsoft.Windows.ControlPanel", LinkTarget = "::{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}" };
            AppInfo settings = new AppInfo { Key = "af:picker-settings", Name = "设置", IsUwp = true,
                TargetPath = apps + "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel" };
            AppInfo calc = new AppInfo { Key = "af:picker-calc", Name = "计算器", IsUwp = true, TargetPath = apps + "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" };
            AppInfo storeShortcut = new AppInfo { Key = "lnk:picker-store", Name = "商店应用快捷方式", IsUwp = true, TargetPath = windows + "\\explorer.exe" };
            AppInfo shellShortcut = new AppInfo { Key = "lnk:picker-shell", Name = "此电脑", IsUwp = true };
            AppInfo lookalike = new AppInfo { Key = "lnk:picker-lookalike", Name = "Lookalike", TargetPath = windows + "Apps\\tool.exe" };
            // MSI「播发」快捷方式的目标是安装缓存里的图标占位程序：是用户安装的软件
            AppInfo advertised = new AppInfo { Key = "lnk:picker-advertised", Name = "Advertised",
                TargetPath = windows.ToUpperInvariant() + "\\Installer\\{90140000-0011-0000-0000-0000000FF1CE}\\wordicon.exe" };

            foreach (AppInfo app in new AppInfo[] { cmd, taskmgr, control, settings })
                Check(Scanner.IsSystemComponent(app), "system tool not recognised: " + app.Name);
            foreach (AppInfo app in new AppInfo[] { installed, excel, calc, storeShortcut, shellShortcut, lookalike, advertised, new AppInfo(), null })
                Check(!Scanner.IsSystemComponent(app), "installed software treated as a system tool: " + (app == null ? "null" : app.Name));

            List<AppInfo> all = new List<AppInfo> { installed, cmd, taskmgr, excel, control, settings, calc, null };
            Func<List<AppInfo>, string> names = delegate(List<AppInfo> list) { return string.Join("|", list.ConvertAll(delegate(AppInfo a) { return a.Name; }).ToArray()); };
            Check(names(SoftwarePickerDialog.Filter(all, "", false)) == "Apifox|Excel|计算器", "default list wrong: " + names(SoftwarePickerDialog.Filter(all, "", false)));
            Check(SoftwarePickerDialog.Filter(all, null, true).Count == 7, "system tools missing when shown");
            Check(SoftwarePickerDialog.Filter(all, "管理", false).Count == 0 && names(SoftwarePickerDialog.Filter(all, " 管理 ", true)) == "任务管理器",
                "search ignored the system-tool switch");
            Check(SoftwarePickerDialog.Filter(null, "x", true).Count == 0, "null candidates not tolerated");
            AppInfo fuzzy = new AppInfo { Key = "lnk:picker-editor", Name = "Editor X", TargetPath = "D:\\APP\\EditorX\\editor.exe" };
            Check(names(SoftwarePickerDialog.Filter(new List<AppInfo> { fuzzy, excel, installed }, "ex", false)) == "Excel|Editor X",
                "direct name match was not ranked before the fuzzy match");

            Check(SoftwarePickerDialog.Describe(installed) == "开发工具  ·  D:\\APP\\ApiFox\\Apifox.exe", "installed description wrong: " + SoftwarePickerDialog.Describe(installed));
            Check(SoftwarePickerDialog.Describe(excel).EndsWith("EXCEL.EXE") && SoftwarePickerDialog.Describe(calc) == "应用商店应用"
                && SoftwarePickerDialog.Describe(settings) == "系统组件" && SoftwarePickerDialog.Describe(control) == "系统组件"
                && SoftwarePickerDialog.Describe(null) == "", "packaged description wrong");

            Check(SoftwarePickerDialog.Where(installed) == "D:\\APP\\ApiFox\\Apifox.exe" && SoftwarePickerDialog.Where(calc) == "应用商店应用"
                && SoftwarePickerDialog.Where(null) == "", "software location wrong: " + SoftwarePickerDialog.Where(installed));

            // 关掉「显示系统工具」时已勾选的系统工具一并取消，不会把看不见的条目加进分类；被搜索筛掉的勾选保留
            using (Form host = new Form())
            {
                host.ShowInTaskbar = false;
                host.StartPosition = FormStartPosition.Manual;
                host.Location = new Point(-3000, -3000);
                host.ClientSize = new Size(1100, 700);
                Controls.MediaAddPage page = Controls.MediaAddPage.ForSoftware("测试", new List<AppInfo> { installed, cmd, excel }, true, null);
                page.Dock = DockStyle.Fill;
                host.Controls.Add(page);
                host.Show();
                Check(page.ShowingInstalled && page.ListedApps.Count == 3, "the add-software page did not start with installed software");
                page.ToggleAllListed();
                ((Controls.ToggleSwitch)typeof(Controls.MediaAddPage).GetField("systemToggle", Private).GetValue(page)).Checked = false;
                ((Controls.SearchField)typeof(Controls.MediaAddPage).GetField("appSearch", Private).GetValue(page)).Text = "Excel";
                Check(page.ListedApps.Count == 1 && names(page.CheckedApps) == "Apifox|Excel",
                    "hidden system tool stayed checked or a searched-out selection was dropped: " + names(page.CheckedApps));
            }

            string path = Path.Combine(Paths.DataDir, "scan.cache.xml");
            try
            {
                if (File.Exists(path)) File.Delete(path);
                ScanCache.Save(new List<AppInfo> { taskmgr, installed });
                List<AppInfo> loaded = ScanCache.Load();
                Check(loaded.Count == 2 && loaded[0].LinkTarget == taskmgr.LinkTarget && loaded[1].LinkTarget == "", "link target lost in the scan cache");
                Check(Scanner.IsSystemComponent(loaded[0]) && !Scanner.IsSystemComponent(loaded[1]), "cached entries classified differently");
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }

        // 勾选添加走「加入分类」：原分类保留、不登记程序文件；应用商店应用同样可加；程序文件已不存在的条目提示后跳过；加入后不再出现在候选里。
        private static void SoftwarePickerAdds()
        {
            AppConfig config = new AppConfig();
            string exe = Path.Combine(root, "picker-tool.exe");
            File.WriteAllText(exe, "exe");
            AppInfo tool = new AppInfo { Key = "lnk:regression-picker-tool", Name = "Picker Tool", TargetPath = exe, Category = "其他" };
            AppInfo store = new AppInfo { Key = "af:regression-picker-store", Name = "Picker Store", IsUwp = true,
                TargetPath = Scanner.AppsFolderPrefix + "Regression.Picker_8wekyb3d8bbwe!App", Category = "其他" };
            AppInfo gone = new AppInfo { Key = "lnk:regression-picker-gone", Name = "Picker Gone", TargetPath = Path.Combine(root, "picker-gone.exe"), Category = "其他" };
            AppInfo member = new AppInfo { Key = "af:regression-picker-member", Name = "Picker Member", TargetPath = exe, Category = "开发工具" };
            List<AppInfo> scanned = new List<AppInfo> { tool, store, gone, member };
            using (MainForm form = CategoryForm(config, tool, store, gone, member))
            {
                Func<string> offered = delegate
                {
                    List<AppInfo> list = (List<AppInfo>)Invoke(form, "PickableSoftware", scanned, "开发工具");
                    return string.Join("|", list.ConvertAll(delegate(AppInfo a) { return a.Name; }).ToArray());
                };
                Check(offered() == "Picker Gone|Picker Store|Picker Tool", "candidates wrong: " + offered());
                bool reported = AnswerMessageDialog("添加软件", 2,
                    delegate { Invoke(form, "AddPickedSoftware", new List<AppInfo> { tool, store, gone }, "开发工具"); });
                Check(reported, "missing program was not reported");
                Check((bool)Invoke(form, "InCategory", tool, "开发工具") && (bool)Invoke(form, "InCategory", store, "开发工具"), "picked software did not join the category");
                Check(!(bool)Invoke(form, "InCategory", gone, "开发工具"), "missing program joined the category");
                Check((bool)Invoke(form, "InCategory", tool, "其他") && (bool)Invoke(form, "InCategory", store, "全部软件"), "joining changed the original category");
                Check(config.AddedItems.Count == 0, "picker registered program files");
                Check(offered() == "Picker Gone", "joined software still offered: " + offered());
                Check(((List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form)).Count == 3, "grid did not refresh after adding");
            }
            AppConfig loaded = AppConfig.Load();
            Check(!loaded.LoadFailed, "picker config could not reload");
            Check(loaded.ItemCategories.ContainsKey(tool.Key) && loaded.ItemCategories[tool.Key].Contains("开发工具")
                && loaded.ItemCategories.ContainsKey(store.Key) && !loaded.ItemCategories.ContainsKey(gone.Key), "picked membership was not saved");
        }

        // 范围清单：常用文件夹、磁盘、自己添加的文件夹各成一组；不可用磁盘排在后面且不可勾选；
        // 已有对应行的范围不重复列出；被上级范围包含的行标出最上层的那个范围。
        private static void LocalScanScopeRows()
        {
            string folder = Path.Combine(root, "scan-scope-rows");
            string pictures = Path.Combine(folder, "pictures"), extra = Path.Combine(folder, "extra"), inner = Path.Combine(extra, "inner");
            foreach (string dir in new string[] { pictures, inner }) Directory.CreateDirectory(dir);
            string drive = LocalScanScope.NormalizePath(Path.GetPathRoot(folder));
            List<LocalScanScopeShortcut> shortcuts = new List<LocalScanScopeShortcut>
            {
                new LocalScanScopeShortcut { Label = "图片", Path = pictures, Primary = true }
            };
            List<LocalScanScopeDrive> drives = new List<LocalScanScopeDrive>
            {
                new LocalScanScopeDrive { Path = @"Z:\", Title = @"Z:\", Supported = false, UnsupportedReason = "不支持网络位置。" },
                new LocalScanScopeDrive { Path = drive, Title = drive + "  系统", Detail = "固定磁盘", Supported = true }
            };
            List<string> known = new List<string> { pictures, extra, inner.ToUpperInvariant(), drive, inner };
            Func<List<LocalScanScopeRow>, string> shape = delegate(List<LocalScanScopeRow> rows)
            {
                return string.Join("|", rows.ConvertAll(delegate(LocalScanScopeRow row)
                {
                    return row.Kind + ":" + row.Title + (row.Checked ? "*" : "") + (row.Enabled ? "" : "-");
                }).ToArray());
            };

            List<LocalScanScopeRow> loading = LocalScanScope.BuildRows(shortcuts, null, null, known, new List<string> { pictures });
            Check(shape(loading) == "Header:常用文件夹-|Shortcut:图片*|Header:磁盘-|Note:正在读取磁盘…-|Header:已添加的文件夹-|Custom:extra|Custom:INNER",
                "rows while drives load wrong: " + shape(loading));

            List<LocalScanScopeRow> loaded = LocalScanScope.BuildRows(shortcuts, drives, null, known,
                new List<string> { extra, inner, @"Z:\" });
            Check(shape(loaded) == "Header:常用文件夹-|Shortcut:图片|Header:磁盘-|Drive:" + drive + "  系统|Drive:Z:\\-|Header:已添加的文件夹-|Custom:extra*|Custom:INNER*",
                "rows after drives load wrong: " + shape(loaded));
            LocalScanScopeRow innerRow = loaded[loaded.Count - 1];
            Check(string.Equals(innerRow.CoveredBy, extra, StringComparison.OrdinalIgnoreCase) && loaded[loaded.Count - 2].CoveredBy == null,
                "nested folder was not marked as covered by its parent: " + innerRow.CoveredBy);
            Check(loaded[4].Detail == "不支持网络位置。" && !loaded[4].Checked, "unavailable drive row is not explained or can be checked");

            List<LocalScanScopeRow> wholeDrive = LocalScanScope.BuildRows(shortcuts, drives, null, known,
                new List<string> { drive, extra, inner });
            Check(wholeDrive[3].Checked && wholeDrive[3].CoveredBy == null
                && string.Equals(wholeDrive[wholeDrive.Count - 1].CoveredBy, drive, StringComparison.OrdinalIgnoreCase),
                "folders under a checked drive are not attributed to the drive");

            List<LocalScanScopeRow> failed = LocalScanScope.BuildRows(null, new List<LocalScanScopeDrive>(), "拒绝访问", new List<string> { drive }, null);
            Check(shape(failed) == "Header:磁盘-|Note:读取磁盘失败：拒绝访问-|Header:已添加的文件夹-|Custom:" + drive && failed[1].Warning,
                "drive failure or a drive without its own row is not listed: " + shape(failed));
            Check(shape(LocalScanScope.BuildRows(null, new List<LocalScanScopeDrive>(), null, null, null)) == "Header:磁盘-|Note:没有可扫描的磁盘。-",
                "empty drive list is not explained");

            Check(LocalScanScope.Summary(LocalScanScope.Resolve(new string[0], null)) == "请至少勾选一个位置。", "empty summary wrong");
            Check(LocalScanScope.Summary(LocalScanScope.Resolve(new[] { extra, inner, pictures }, null)) == "将扫描 2 个文件夹。已合并 1 个重复或重叠范围。",
                "folder summary wrong: " + LocalScanScope.Summary(LocalScanScope.Resolve(new[] { extra, inner, pictures }, null)));
            Check(LocalScanScope.Summary(LocalScanScope.Resolve(new[] { drive, extra }, null)) == "将扫描 1 个磁盘。已合并 1 个重复或重叠范围。整盘扫描可能耗时较长。",
                "drive summary wrong: " + LocalScanScope.Summary(LocalScanScope.Resolve(new[] { drive, extra }, null)));
            string missing = Path.Combine(folder, "missing");
            Check(LocalScanScope.Summary(LocalScanScope.Resolve(new[] { missing }, null)).StartsWith("没有可用范围：" + missing)
                && LocalScanScope.Summary(LocalScanScope.Resolve(new[] { missing, extra }, null)) == "将扫描 1 个文件夹。另有 1 个范围不可用。",
                "unavailable range is not reported in the summary");
        }

        // 范围窗口：上次的范围恢复为勾选（失效和被排除的丢弃）；点一下切换勾选；只有自己添加的文件夹能移除；
        // 没有勾选时不能开始；开始后返回合并后的根目录。
        private static void LocalScanScopeDialogSelection()
        {
            string folder = Path.Combine(root, "scan-scope-dialog");
            string extra = Path.Combine(folder, "extra"), inner = Path.Combine(extra, "inner"), store = Path.Combine(folder, "store");
            foreach (string dir in new string[] { inner, Path.Combine(store, "sub") }) Directory.CreateDirectory(dir);
            using (LocalScanScopeDialog dialog = new LocalScanScopeDialog("回归", LocalScanScopeKind.Photos,
                new[] { extra, inner, Path.Combine(folder, "missing"), Path.Combine(store, "sub") }, new[] { store }))
            {
                ListBox list = (ListBox)typeof(LocalScanScopeDialog).GetField("list", Private).GetValue(dialog);
                Label summary = (Label)typeof(LocalScanScopeDialog).GetField("summaryLabel", Private).GetValue(dialog);
                Control start = (Control)typeof(LocalScanScopeDialog).GetField("startButton", Private).GetValue(dialog);
                Func<string> custom = delegate
                {
                    List<string> names = new List<string>();
                    foreach (LocalScanScopeRow row in list.Items)
                        if (row.Kind == LocalScanScopeRowKind.Custom)
                            names.Add(row.Title + (row.Checked ? "*" : "") + (row.CoveredBy == null ? "" : "<"));
                    return string.Join("|", names.ToArray());
                };
                Func<string, int> indexOf = delegate(string title)
                {
                    for (int i = 0; i < list.Items.Count; i++)
                        if (((LocalScanScopeRow)list.Items[i]).Title == title) return i;
                    return -1;
                };
                Action<string, object[]> invoke = delegate(string name, object[] args)
                {
                    typeof(LocalScanScopeDialog).GetMethod(name, Private).Invoke(dialog, args);
                };
                Action<string, int> callAt = delegate(string name, int index) { invoke(name, new object[] { index }); };
                Check(custom() == "extra*|inner*<", "previous ranges were not restored as checked rows: " + custom());
                Check(summary.Text == "将扫描 1 个文件夹。已合并 1 个重复或重叠范围。" && start.Enabled, "initial summary wrong: " + summary.Text);

                callAt("Toggle", indexOf("extra"));
                Check(custom() == "extra|inner*" && summary.Text == "将扫描 1 个文件夹。", "unchecking the parent went wrong: " + custom() + " / " + summary.Text);
                callAt("RemoveCustom", indexOf("磁盘"));
                callAt("Toggle", indexOf("磁盘"));
                Check(custom() == "extra|inner*", "a group header reacted to toggle or remove: " + custom());
                callAt("RemoveCustom", indexOf("inner"));
                Check(custom() == "extra" && summary.Text == "请至少勾选一个位置。" && !start.Enabled,
                    "removing the last checked folder left the dialog startable: " + custom() + " / " + summary.Text);
                invoke("StartScan", new object[0]);
                Check(dialog.DialogResult != DialogResult.OK && dialog.ResultRoots.Count == 0, "scan started without a checked range");

                callAt("Toggle", indexOf("extra"));
                Check(custom() == "extra*" && start.Enabled, "re-checking a folder failed: " + custom());
                invoke("StartScan", new object[0]);
                Check(dialog.DialogResult == DialogResult.OK && dialog.ResultRoots.Count == 1
                    && string.Equals(dialog.ResultRoots[0], extra, StringComparison.OrdinalIgnoreCase), "confirmed roots wrong");
            }
        }

        // 展开：子文件夹按资源管理器的名称顺序列出，隐藏和排除范围内的不列；清单在原地逐级缩进，
        // 上级已勾选的子文件夹标为“随上级扫描”；看不到对应行的已勾选范围列在「已添加的文件夹」。
        private static void LocalScanScopeTree()
        {
            string folder = Path.Combine(root, "scan-scope-tree");
            string a = Path.Combine(folder, "a"), store = Path.Combine(a, "store"), empty = Path.Combine(folder, "empty");
            string a1 = Path.Combine(a, "a1"), a2 = Path.Combine(a, "a2"), a10 = Path.Combine(a, "a10"), hidden = Path.Combine(a, "hidden");
            foreach (string dir in new string[] { a1, a2, a10, hidden, Path.Combine(store, "x"), empty }) Directory.CreateDirectory(dir);
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
            File.WriteAllText(Path.Combine(a, "file.txt"), "x");

            LocalScanScopeChildren listed = LocalScanScope.ListSubfolders(a, new[] { store });
            Check(listed.Error == null && listed.Omitted == 0 && !listed.Loading
                && string.Join("|", listed.Paths.ConvertAll(delegate(string path) { return Path.GetFileName(path); }).ToArray()) == "a1|a2|a10",
                "subfolders wrong: " + string.Join("|", listed.Paths.ToArray()) + " / " + listed.Error);
            Check(LocalScanScope.ListSubfolders(empty, null).Paths.Count == 0 && LocalScanScope.ListSubfolders(empty, null).Error == null,
                "empty folder not listed as empty");
            LocalScanScopeChildren missing = LocalScanScope.ListSubfolders(Path.Combine(folder, "missing"), null);
            Check(missing.Paths.Count == 0 && !string.IsNullOrEmpty(missing.Error), "unreadable folder did not report an error");

            List<LocalScanScopeShortcut> shortcuts = new List<LocalScanScopeShortcut> { new LocalScanScopeShortcut { Label = "图片", Path = a } };
            List<LocalScanScopeDrive> drives = new List<LocalScanScopeDrive>();
            Dictionary<string, LocalScanScopeChildren> children = new Dictionary<string, LocalScanScopeChildren>(StringComparer.OrdinalIgnoreCase);
            children[a] = listed;
            HashSet<string> expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a.ToUpperInvariant(), a1 };
            Func<List<LocalScanScopeRow>, string> shape = delegate(List<LocalScanScopeRow> rows)
            {
                return string.Join("|", rows.ConvertAll(delegate(LocalScanScopeRow row)
                {
                    return row.Depth + row.Kind.ToString().Substring(0, 1) + ":" + row.Title + (row.Checked ? "*" : "") + (row.Implied ? "~" : "")
                        + (row.Expanded ? ">" : "") + (row.Warning ? "!" : "");
                }).ToArray());
            };
            List<LocalScanScopeRow> open = LocalScanScope.BuildRows(shortcuts, drives, null, new List<string> { a2 }, new List<string> { a },
                expanded, children);
            Check(shape(open) == "0H:常用文件夹|0S:图片*>|1F:a1~>|2N:正在读取子文件夹…|1F:a2~|1F:a10~|0H:磁盘|0N:没有可扫描的磁盘。",
                "expanded rows wrong: " + shape(open));
            Check(open[1].Expandable && open[2].Expandable && !open[0].Expandable && !open[3].Expandable
                && string.Equals(open[4].CoveredBy, a, StringComparison.OrdinalIgnoreCase), "expand marks or coverage wrong");

            List<LocalScanScopeRow> picked = LocalScanScope.BuildRows(shortcuts, drives, null, new List<string> { a2 }, new List<string> { a2 },
                expanded, children);
            Check(shape(picked) == "0H:常用文件夹|0S:图片>|1F:a1>|2N:正在读取子文件夹…|1F:a2*|1F:a10|0H:磁盘|0N:没有可扫描的磁盘。",
                "checked subfolder wrong: " + shape(picked));
            List<LocalScanScopeRow> closed = LocalScanScope.BuildRows(shortcuts, drives, null, new List<string> { a2 }, new List<string> { a2 },
                new HashSet<string>(), children);
            Check(shape(closed) == "0H:常用文件夹|0S:图片|0H:磁盘|0N:没有可扫描的磁盘。|0H:已添加的文件夹|0C:a2*",
                "a checked subfolder disappeared when its parent was collapsed: " + shape(closed));

            children[a] = new LocalScanScopeChildren { Error = "拒绝访问" };
            Check(shape(LocalScanScope.BuildRows(shortcuts, drives, null, null, null, expanded, children))
                == "0H:常用文件夹|0S:图片>|1N:无法读取：拒绝访问!|0H:磁盘|0N:没有可扫描的磁盘。", "read failure is not shown under the folder");
            LocalScanScopeChildren many = new LocalScanScopeChildren { Omitted = 5, OmittedMore = true };
            many.Paths.Add(a1);
            children[a] = many;
            children[a1] = new LocalScanScopeChildren();
            Check(shape(LocalScanScope.BuildRows(shortcuts, drives, null, null, null, expanded, children))
                == "0H:常用文件夹|0S:图片>|1F:a1>|2N:没有可扫描的子文件夹。|1N:另有至少 5 个子文件夹未列出，可在下方输入路径添加。|0H:磁盘|0N:没有可扫描的磁盘。",
                "empty or truncated subfolder list is not explained");

            string reason;
            Check(LocalScanScope.CleanTypedPath("  \"C:\\Photos\\2024\" ", out reason) == "C:\\Photos\\2024" && reason == null, "quoted path was not cleaned");
            Check(LocalScanScope.CleanTypedPath("   ", out reason) == null && reason == null, "blank input reported an error");
            Check(LocalScanScope.CleanTypedPath("photos\\2024", out reason) == null && !string.IsNullOrEmpty(reason)
                && LocalScanScope.CleanTypedPath("C:photos", out reason) == null, "relative path was accepted");
            Check(LocalScanScope.CleanTypedPath("%WINDIR%", out reason) == Environment.GetEnvironmentVariable("WINDIR")
                && LocalScanScope.CleanTypedPath("\\\\server\\share", out reason) == "\\\\server\\share", "environment variable or UNC path was not passed on");
        }

        // 范围窗口不再弹出消息框或文件夹浏览框：路径无效、已被上级包含、开始前发现范围失效，都只在提示行里说明。
        private static void LocalScanScopeDialogInline()
        {
            string folder = Path.Combine(root, "scan-scope-inline");
            string extra = Path.Combine(folder, "extra"), inner = Path.Combine(extra, "inner"), other = Path.Combine(folder, "other");
            string store = Path.Combine(folder, "store"), missing = Path.Combine(folder, "missing");
            foreach (string dir in new string[] { inner, other, store }) Directory.CreateDirectory(dir);
            using (LocalScanScopeDialog dialog = new LocalScanScopeDialog("回归", LocalScanScopeKind.Photos, new[] { extra }, new[] { store }))
            {
                ListBox list = (ListBox)typeof(LocalScanScopeDialog).GetField("list", Private).GetValue(dialog);
                Label summary = (Label)typeof(LocalScanScopeDialog).GetField("summaryLabel", Private).GetValue(dialog);
                Func<string, object[], object> invoke = delegate(string name, object[] args)
                {
                    return typeof(LocalScanScopeDialog).GetMethod(name, Private).Invoke(dialog, args);
                };
                Func<string> rows = delegate
                {
                    List<string> names = new List<string>();
                    foreach (LocalScanScopeRow row in list.Items)
                        if (row.Kind == LocalScanScopeRowKind.Custom || row.Kind == LocalScanScopeRowKind.Folder)
                            names.Add(row.Depth + ":" + row.Title + (row.Checked ? "*" : "") + (row.Implied ? "~" : "") + (row.Expanded ? ">" : ""));
                    return string.Join("|", names.ToArray());
                };
                Func<string, int> indexOf = delegate(string title)
                {
                    for (int i = 0; i < list.Items.Count; i++)
                        if (((LocalScanScopeRow)list.Items[i]).Title == title) return i;
                    return -1;
                };

                bool allAdded = (bool)invoke("AddPaths", new object[] { new string[] { "\"" + other + "\"", missing, "relative\\path", store, "  " } });
                Check(!allAdded && rows() == "0:extra*|0:other*", "typed paths were not added one by one: " + rows());
                Check(summary.Text.StartsWith("有 3 项没有添加，例如 " + missing) && summary.ForeColor == Theme.DeleteRed,
                    "rejected paths were not explained inline: " + summary.Text);
                Check((bool)invoke("AddPaths", new object[] { new string[] { inner } }) && rows() == "0:extra*|0:other*|0:inner*",
                    "a valid path was rejected: " + rows());
                Check(summary.Text == "将扫描 2 个文件夹。已合并 1 个重复或重叠范围。" && summary.ForeColor != Theme.DeleteRed,
                    "the notice was not replaced by the summary after a clean action: " + summary.Text);

                invoke("Toggle", new object[] { indexOf("inner") });
                Check(rows() == "0:extra*|0:other*|0:inner~", "unchecking a covered folder went wrong: " + rows());
                invoke("Toggle", new object[] { indexOf("inner") });
                Check(rows() == "0:extra*|0:other*|0:inner~" && summary.Text.Contains("已包含在 " + extra) && summary.ForeColor != Theme.DeleteRed,
                    "clicking a folder that its parent already covers was not explained: " + summary.Text);

                // 展开要等后台读完子文件夹：建出窗口句柄（不显示窗口）并泵消息
                IntPtr handle = dialog.Handle;
                invoke("ToggleExpand", new object[] { indexOf("extra") });
                PumpUntil(delegate { return rows().Contains("1:inner"); });
                Check(rows() == "0:extra*>|1:inner~|0:other*", "expanding did not list the subfolder in place: " + rows());
                invoke("Toggle", new object[] { indexOf("extra") });
                invoke("Toggle", new object[] { indexOf("inner") });
                Check(rows() == "0:extra>|1:inner*|0:other*", "a subfolder could not be checked on its own: " + rows());
                invoke("ToggleExpand", new object[] { indexOf("extra") });
                Check(rows() == "0:extra|0:other*|0:inner*", "collapsing hid a checked subfolder: " + rows());

                Directory.Delete(other);
                invoke("StartScan", new object[0]);
                Check(dialog.DialogResult != DialogResult.OK && rows() == "0:extra|0:other|0:inner*"
                    && summary.Text.StartsWith("已取消勾选当前不可用的范围：" + other) && summary.Text.EndsWith("确认其余范围后再点「开始扫描」。")
                    && summary.ForeColor == Theme.DeleteRed, "a vanished range was not unchecked and explained: " + summary.Text + " / " + rows());
                invoke("StartScan", new object[0]);
                Check(dialog.DialogResult == DialogResult.OK && dialog.ResultRoots.Count == 1
                    && string.Equals(dialog.ResultRoots[0], inner, StringComparison.OrdinalIgnoreCase), "confirmed roots wrong after the fix-up");
            }
        }

        // 音乐的「无法确认的文件」不再单独弹问：结果窗口多一个默认不勾的勾选项；不勾就没内容可导入时主按钮不可用。
        private static void LocalScanResultOption()
        {
            LocalMusicScanResult music = new LocalMusicScanResult();
            Check(MainForm.MusicUnconfirmedOption(music) == null, "option offered without unconfirmed files");
            music.UnconfirmedFiles.AddRange(new string[] { "D:\\m\\a.mp3", "D:\\m\\b.mp3", "D:\\m\\c.mp3" });
            string optionText = MainForm.MusicUnconfirmedOption(music);
            Check(optionText == "同时导入 3 个无法确认是否为歌曲的文件（如 a.mp3、b.mp3）", "option text wrong: " + optionText);

            bool probed = false, stayedOpen = false;
            string shownText = null;
            LocalScanResultDecision decision;
            using (System.Windows.Forms.Timer probe = new System.Windows.Forms.Timer { Interval = 10 })
            {
                probe.Tick += delegate
                {
                    Form dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(open => open.Modal);
                    if (dialog == null) return;
                    probe.Stop();
                    dialog.Opacity = 0;
                    probed = true;
                    MethodInfo key = dialog.GetType().GetMethod("ProcessCmdKey", Private);
                    key.Invoke(dialog, new object[] { new Message(), Keys.Enter });
                    stayedOpen = !dialog.IsDisposed && dialog.Visible;
                    Controls.CheckRow option = dialog.Controls.OfType<Controls.CheckRow>().FirstOrDefault();
                    if (option != null)
                    {
                        shownText = option.Text;
                        stayedOpen = stayedOpen && !option.Checked;
                        option.Checked = true;
                    }
                    if (stayedOpen) key.Invoke(dialog, new object[] { new Message(), Keys.Enter });
                    if (!dialog.IsDisposed && dialog.Visible) dialog.Close();
                };
                probe.Start();
                decision = LocalScanResultDialog.Show("回归", "没有确认是歌曲的文件", null, "明细", "继续", true, false, optionText, true);
            }
            Check(probed && shownText == optionText, "option checkbox missing: " + shownText);
            Check(stayedOpen, "import was possible with nothing to import");
            Check(decision.Action == LocalScanResultAction.Import && decision.OptionChecked && decision.Operation == ImportOperation.Copy,
                "checking the option did not enable the import");
        }

        // 进度窗口：扫描结束前关窗口只发出取消，窗口等后台线程退出才关闭；取消后迟到的进度不覆盖「正在取消」。
        // 导入记录窗口的整理规则：整体状态、明细排序与上限、文件名、提示文字。
        private static void ImportHistoryRules()
        {
            Func<string, string, string, ImportItem> item = delegate(string state, string source, string error)
            {
                return new ImportItem { State = state, Source = source, Target = "D:\\lib\\" + ImportHistoryDialog.DisplayName(source), Error = error };
            };
            Func<List<ImportHistoryDialog.Row>, string> labels = delegate(List<ImportHistoryDialog.Row> rows)
            {
                return string.Join("|", rows.ConvertAll(delegate(ImportHistoryDialog.Row row) { return row.Label; }).ToArray());
            };

            ImportBatch done = new ImportBatch { Category = "文档", Operation = ImportOperation.Move, Committed = true, Finished = true };
            done.Items.Add(item(FileImport.Moved, "C:\\a\\1.txt", null));
            done.Items.Add(item(FileImport.Skipped, "C:\\a\\2.txt", null));
            Check(ImportHistoryDialog.Classify(done) == ImportBatchStatus.Done, "moved + skipped batch is not done");
            Check(ImportHistoryDialog.NoticeText(done) == "", "clean batch has a notice");

            ImportBatch copy = new ImportBatch { Category = "文档", Collection = "合同", Operation = ImportOperation.Copy, Committed = true, Finished = true };
            copy.Items.Add(item(FileImport.Copied, "C:\\a\\1.txt", null));
            ImportHistoryDialog.Row copied = new ImportHistoryDialog.Row(copy, copy.Items[0]);
            Check(ImportHistoryDialog.Classify(copy) == ImportBatchStatus.Done && !MainForm.CanRetryDelete(copy), "copy batch treated as awaiting cleanup");
            Check(copied.Label == FileImport.Copied && copied.Tone == ImportTone.Good && copied.ErrorLines == 0, "copied item of a copy batch shown as a problem");
            Check(ImportHistoryDialog.Title(copy) == "文档 › 合同" && ImportHistoryDialog.Title(new ImportBatch()) == "未指定分类", "record title wrong");

            // 移入但部分失败：原件全部保留，等用户确认清理；引用项没有原件可删
            ImportBatch cleanup = new ImportBatch { Category = "照片", Operation = ImportOperation.Move, Committed = true, Finished = true,
                Error = "部分失败，全部源文件保留。" };
            cleanup.Items.Add(item(FileImport.Copied, "C:\\p\\a.jpg", null));
            cleanup.Items.Add(item(FileImport.Failed, "C:\\p\\b.jpg", "磁盘空间不足。"));
            cleanup.Items.Add(item(FileImport.DeleteFailed, "C:\\p\\c.jpg", "拒绝访问。"));
            cleanup.Items.Add(item(FileImport.Linked, "C:\\p\\d.jpg", null));
            ImportItem reference = item(FileImport.Copied, "C:\\p\\e.jpg", null);
            reference.Reference = true;
            cleanup.Items.Add(reference);
            ImportHistoryDialog.Record record = new ImportHistoryDialog.Record(cleanup, new DateTime(2026, 11, 9, 22, 4, 0));
            Check(record.Status == ImportBatchStatus.Cleanup && MainForm.CanRetryDelete(cleanup), "partially failed move is not awaiting cleanup");
            Check(record.Stored == 3 && record.Existing == 1 && record.Failed == 1 && record.Cleanup == 2 && !record.HasRolledBack,
                "record numbers wrong: " + record.Stored + "/" + record.Existing + "/" + record.Failed + "/" + record.Cleanup);
            Check(labels(record.Rows) == "失败|源文件删除失败|原件待清理|已收录|已复制", "problem rows not listed first: " + labels(record.Rows));
            Check(record.Rows[0].Tone == ImportTone.Bad && record.Rows[1].Tone == ImportTone.Warn && record.Rows[2].Tone == ImportTone.Warn
                && record.Rows[3].Tone == ImportTone.Neutral && record.Rows[4].Tone == ImportTone.Good, "row tones wrong");
            Check(!MainForm.AwaitsSourceDelete(cleanup, reference), "reference item treated as having a source to delete");
            Check(ImportHistoryDialog.NoticeText(cleanup) == "部分失败，全部源文件保留。\r\n可重试删除已入库项的源文件；复制失败项需重新扫描或重新导入。",
                "notice wrong: " + ImportHistoryDialog.NoticeText(cleanup));
            Check(ImportHistoryDialog.RowText(record.Rows[1]) == "源文件删除失败：C:\\p\\c.jpg\r\n目标：D:\\lib\\c.jpg\r\n拒绝访问。", "row text wrong");

            // 入库没保存成功：副本已撤回，不提供清理原件
            ImportBatch notSaved = new ImportBatch { Category = "视频", Operation = ImportOperation.Move, Committed = false, Finished = true, Error = "入库记录保存失败" };
            notSaved.Items.Add(item(FileImport.RolledBack, "C:\\v\\a.mp4", null));
            notSaved.Items.Add(item(FileImport.Copied, "C:\\v\\b.mp4", null));
            ImportHistoryDialog.Record rolled = new ImportHistoryDialog.Record(notSaved, DateTime.Now);
            Check(rolled.Status == ImportBatchStatus.NotSaved && !MainForm.CanRetryDelete(notSaved) && rolled.Cleanup == 0, "uncommitted batch offers cleanup");
            Check(rolled.HasRolledBack && rolled.Failed == 1 && rolled.Rows[0].Tone == ImportTone.Bad && rolled.Rows[1].Tone == ImportTone.Neutral
                && rolled.Rows[1].Label == FileImport.Copied, "rolled back batch shown wrong");

            ImportBatch unfinished = new ImportBatch { Category = "文档", Operation = ImportOperation.Move, Committed = true };
            unfinished.Items.Add(item(FileImport.Waiting, "C:\\a\\1.txt", null));
            unfinished.Items.Add(item(null, null, null));
            Check(ImportHistoryDialog.Classify(unfinished) == ImportBatchStatus.Unfinished, "interrupted batch not marked unfinished");
            List<ImportHistoryDialog.Row> odd = ImportHistoryDialog.BuildRows(unfinished);
            Check(odd[1].Label == "未知" && odd[1].Name == "（未知来源）" && odd[1].Tone == ImportTone.Neutral, "item without state or source not tolerated");

            ImportBatch partial = new ImportBatch { Category = "文档", Operation = ImportOperation.Copy, Committed = true, Finished = true };
            partial.Items.Add(item(FileImport.Copied, "C:\\a\\1.txt", null));
            partial.Items.Add(item(FileImport.Failed, "C:\\a\\2.txt", "文件正被另一个程序使用。"));
            Check(ImportHistoryDialog.Classify(partial) == ImportBatchStatus.Partial, "copy batch with a failure is not partial");
            Check(ImportHistoryDialog.NoticeText(partial) == "复制失败项需重新扫描或重新导入；本记录不能重试复制。", "failure hint missing");
            done.Error = "磁盘空间不足，已停止后续操作。";
            Check(ImportHistoryDialog.Classify(done) == ImportBatchStatus.Partial, "batch error alone does not mark the record");
            done.Error = null;

            // 状态正常但恢复时留下了说明：排在正常项前面；文件再多也是问题项在最前，后面的正常项保持原顺序
            ImportBatch large = new ImportBatch { Category = "照片", Operation = ImportOperation.Move, Committed = true, Finished = true };
            for (int i = 0; i < ImportHistoryDialog.PageRows + 500; i++) large.Items.Add(item(FileImport.Moved, "E:\\DCIM\\" + i + ".jpg", null));
            ImportItem noted = item(FileImport.Moved, "E:\\DCIM\\noted.jpg", "临时副本需核对：D:\\lib\\noted.jpg.import-1");
            ImportItem broken = item(FileImport.Failed, "E:\\DCIM\\broken.jpg", "第一行\n第二行");
            large.Items.Add(noted);
            large.Items.Add(broken);
            ImportHistoryDialog.Record capped = new ImportHistoryDialog.Record(large, DateTime.Now);
            Check(capped.Rows.Count == large.Items.Count && capped.Rows[0].Item == broken && capped.Rows[1].Item == noted
                && capped.Rows[2].Item == large.Items[0]
                && capped.Rows[capped.Rows.Count - 1].Item == large.Items[ImportHistoryDialog.PageRows + 499],
                "problem rows not first or normal rows reordered");
            Check(capped.Rows[0].ErrorLines == 2 && capped.Rows[1].ErrorLines == 1 && capped.Rows[1].Tone == ImportTone.Good, "error lines wrong");
            Check(capped.Stored == ImportHistoryDialog.PageRows + 501 && capped.Failed == 1, "numbers must cover every row, not only the first page");

            Check(ImportHistoryDialog.DisplayName("C:\\a\\b.txt") == "b.txt" && ImportHistoryDialog.DisplayName("C:\\a\\dir\\") == "dir"
                && ImportHistoryDialog.DisplayName("C:/a/b") == "b" && ImportHistoryDialog.DisplayName("C:\\") == "C:", "display name wrong");
            Check(ImportHistoryDialog.DisplayName(null) == "（未知来源）" && ImportHistoryDialog.DisplayName("") == "（未知来源）"
                && ImportHistoryDialog.DisplayName("\\\\") == "\\\\" && ImportHistoryDialog.DisplayName("C:\\a\\x<>|?*\"\n.txt") == "x<>|?*\"\n.txt",
                "display name does not tolerate empty or invalid paths");

            DateTime now = new DateTime(2026, 11, 14, 9, 30, 0);
            Check(ImportHistoryDialog.ListTime(new DateTime(2026, 11, 14, 0, 5, 0), now) == "今天 00:05"
                && ImportHistoryDialog.ListTime(new DateTime(2026, 11, 13, 23, 59, 0), now) == "11-13 23:59"
                && ImportHistoryDialog.ListTime(new DateTime(2025, 12, 31, 23, 59, 0), now) == "2025-12-31", "list time wrong");
            HashSet<string> names = new HashSet<string>();
            foreach (ImportBatchStatus status in Enum.GetValues(typeof(ImportBatchStatus)))
                Check(names.Add(ImportHistoryDialog.StatusText(status)), "two states share one label: " + status);
        }

        // 导入记录窗口：后台读取日志（坏文件只计数不中断）、按时间倒序列出；可清理的批次点按钮后交回调用方，
        // 导入进行中或批次不需要清理时不提供按钮；文件很多的记录先列一页，点「显示更多」接着列；读取完成前关闭窗口不出错。
        private static void ImportHistoryDialogFlow()
        {
            string journal = Path.Combine(root, "import-history", "Imports");
            ImportBatch pending = new ImportBatch { Category = "照片", Operation = ImportOperation.Move, Committed = true, Finished = true,
                DeleteAuthorized = true, JournalDirectory = journal };
            pending.Items.Add(new ImportItem { State = FileImport.DeleteFailed, Source = "C:\\src\\a&b.jpg", Target = "D:\\lib\\a&b.jpg", Error = "拒绝访问。" });
            pending.Items.Add(new ImportItem { State = FileImport.Moved, Source = "C:\\src\\c.jpg", Target = "D:\\lib\\c.jpg" });
            FileImport.Save(pending);
            ImportBatch clean = new ImportBatch { Category = "文档", Operation = ImportOperation.Copy, Committed = true, Finished = true, JournalDirectory = journal };
            clean.Items.Add(new ImportItem { State = FileImport.Copied, Source = "C:\\src\\d.txt", Target = "D:\\lib\\d.txt" });
            FileImport.Save(clean);
            File.SetLastWriteTime(Path.Combine(journal, clean.Id + ".xml"), DateTime.Now.AddDays(-3));
            string damaged = Path.Combine(journal, Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(damaged, "<ImportBatch><Id>");
            File.SetLastWriteTime(damaged, DateTime.Now.AddDays(-5));
            ImportBatch many = new ImportBatch { Category = "音乐", Operation = ImportOperation.Copy, Committed = true, Finished = true, JournalDirectory = journal };
            for (int i = 0; i < ImportHistoryDialog.PageRows + 5; i++)
                many.Items.Add(new ImportItem { State = FileImport.Copied, Source = "E:\\m\\" + i + ".mp3", Target = "D:\\lib\\" + i + ".mp3" });
            FileImport.Save(many);
            File.SetLastWriteTime(Path.Combine(journal, many.Id + ".xml"), DateTime.Now.AddDays(-4));

            Func<ImportHistoryDialog, string, object> field = delegate(ImportHistoryDialog dialog, string name)
            {
                return typeof(ImportHistoryDialog).GetField(name, Private).GetValue(dialog);
            };
            // 显示窗口并等读取结束（左侧出现记录，或占位文字不再是“正在读取”），再在界面线程里执行检查
            Action<ImportHistoryDialog, Action> whenLoaded = delegate(ImportHistoryDialog dialog, Action inspect)
            {
                Stopwatch waited = Stopwatch.StartNew();
                using (System.Windows.Forms.Timer probe = new System.Windows.Forms.Timer { Interval = 10 })
                {
                    probe.Tick += delegate
                    {
                        bool loaded = ((ListBox)field(dialog, "batchList")).Items.Count > 0
                            || (string)field(dialog, "placeholder") != "正在读取导入记录…";
                        if (!dialog.Visible || (!loaded && waited.ElapsedMilliseconds < 15000)) return;
                        probe.Stop();
                        dialog.Opacity = 0;
                        try { if (loaded) inspect(); }
                        finally { if (dialog.Visible) dialog.Close(); }
                    };
                    probe.Start();
                    ConfirmDialog.ShowModal(dialog);
                }
            };

            int records = -1, rows = -1;
            bool retryShown = false;
            string summary = null, first = null, hint = null;
            ImportBatch handed = null;
            bool cancelAsked = false, cancelKept = false, confirmAsked = false, enterKept = false, escapeKept = false;
            using (ImportHistoryDialog dialog = new ImportHistoryDialog(journal, delegate { return false; }))
            {
                whenLoaded(dialog, delegate
                {
                    ListBox batches = (ListBox)field(dialog, "batchList");
                    records = batches.Items.Count;
                    first = batches.Items.Count > 0 ? batches.Items[0].ToString() : null;
                    rows = ((ListBox)field(dialog, "itemList")).Items.Count;
                    retryShown = ((Control)field(dialog, "retry")).Visible;
                    hint = ((Control)field(dialog, "footer")).Text;
                    summary = (string)field(dialog, "headerSummary");
                    MethodInfo request = typeof(ImportHistoryDialog).GetMethod("RequestRetry", Private);
                    // 确认框默认按钮是「取消」：直接回车、按 Esc 都不删除
                    Func<Keys, bool> pressOnConfirm = delegate(Keys key)
                    {
                        bool pressed = false;
                        using (System.Windows.Forms.Timer press = new System.Windows.Forms.Timer { Interval = 10 })
                        {
                            press.Tick += delegate
                            {
                                Form message = FindMessageDialog("导入记录");
                                if (message == null) return;
                                press.Stop();
                                message.Opacity = 0;
                                typeof(Control).GetMethod("OnKeyDown", Private).Invoke(message, new object[] { new KeyEventArgs(key) });
                                pressed = true;
                            };
                            press.Start();
                            request.Invoke(dialog, null);
                        }
                        return pressed && dialog.Visible && dialog.RetryBatch == null;
                    };
                    enterKept = pressOnConfirm(Keys.Enter);
                    escapeKept = pressOnConfirm(Keys.Escape);
                    // 源文件删除后不进回收站：每次都要确认；点「取消」什么也不交回，窗口留着
                    cancelAsked = AnswerMessageDialog("导入记录", 2, delegate { request.Invoke(dialog, null); });
                    cancelKept = dialog.Visible && dialog.RetryBatch == null;
                    confirmAsked = AnswerMessageDialog("导入记录", 1, delegate { request.Invoke(dialog, null); });
                });
                handed = dialog.RetryBatch;
            }
            Check(records == 3 && first != null && first.StartsWith("照片，原件待清理"), "journal not listed newest first: " + records + " / " + first);
            Check(summary == "共 3 次导入 · 1 次需留意 · 1 条记录无法读取", "header summary wrong: " + summary);
            Check(rows == 2 && retryShown && hint.Contains("不进入回收站"), "pending batch does not offer cleanup");
            Check(cancelAsked && cancelKept && confirmAsked, "cleanup did not ask first, or cancelling still handed the batch back");
            Check(enterKept && escapeKept, "pressing Enter or Esc on the cleanup confirmation deleted the sources");
            Check(handed != null && handed.Id == pending.Id && handed.DeleteAuthorized, "cleanup request not handed back to the caller");
            string confirmText = ImportHistoryDialog.DeleteConfirmText(pending);
            // 这批记录没有文件信息（旧记录）：写明项数和后果，不写「0 个文件」，也不说成只剩文件夹
            Check(confirmText.Contains("确定删除 1 项") && confirmText.Contains("不进回收站") && !confirmText.Contains("0 个文件")
                && !confirmText.Contains("文件夹"), "cleanup confirmation does not say what will be deleted: " + confirmText);
            // 只数还要删的：已删掉的文件、只登记位置的项目、已完成的项目都不算；当时有失败项的批次先说明原因
            ImportBatch partial = new ImportBatch { Operation = ImportOperation.Move, Committed = true, DeleteAuthorized = false };
            ImportItem copied = new ImportItem { State = FileImport.Copied, Source = @"C:\src\x", Folder = true };
            copied.Files.Add(new ImportFileStamp { Relative = "a.jpg", Length = 2048 });
            copied.Files.Add(new ImportFileStamp { Relative = "b.jpg", Length = 1024, Deleted = true });
            partial.Items.Add(copied);
            partial.Items.Add(new ImportItem { State = FileImport.Copied, Reference = true, Source = @"C:\src\ref" });
            partial.Items.Add(new ImportItem { State = FileImport.Moved, Source = @"C:\src\done" });
            string partialText = ImportHistoryDialog.DeleteConfirmText(partial);
            Check(partialText.StartsWith("该批次当时没有全部完成") && partialText.Contains("确定删除 1 项") && partialText.Contains("（1 个文件，共 2.0 KB）"),
                "cleanup confirmation counts the wrong files: " + partialText);
            copied.Files[0].Deleted = true;   // 上次删到一半中断：文件都删了，只剩源文件夹
            string foldersOnly = ImportHistoryDialog.DeleteConfirmText(partial);
            Check(foldersOnly.Contains("只剩空的源文件夹") && !foldersOnly.Contains("0 个文件"), "an empty-folder cleanup was described as 0 files: " + foldersOnly);

            // 导入进行中：不提供清理按钮并说明原因；不需要清理的记录同样没有按钮；直接关闭不交回批次
            bool busyHidden = false, cleanHidden = false;
            string busyHint = null;
            int cleanRows = -1, firstPage = -1, allRows = -1, selectedAfterMore = -1, afterExtraClick = -1;
            string lastOfFirstPage = null;
            using (ImportHistoryDialog dialog = new ImportHistoryDialog(journal, delegate { return true; }))
            {
                whenLoaded(dialog, delegate
                {
                    Control retry = (Control)field(dialog, "retry");
                    busyHidden = !retry.Visible;
                    busyHint = ((Control)field(dialog, "footer")).Text;
                    typeof(ImportHistoryDialog).GetMethod("RequestRetry", Private).Invoke(dialog, null);
                    ((ListBox)field(dialog, "batchList")).SelectedIndex = 1;
                    cleanHidden = !retry.Visible && ((Control)field(dialog, "footer")).Text == "";
                    ListBox items = (ListBox)field(dialog, "itemList");
                    cleanRows = items.Items.Count;
                    ((ListBox)field(dialog, "batchList")).SelectedIndex = 2;
                    firstPage = items.Items.Count;
                    lastOfFirstPage = items.Items[items.Items.Count - 1].ToString();
                    MethodInfo more = typeof(ImportHistoryDialog).GetMethod("ShowMoreRows", Private);
                    more.Invoke(dialog, null);
                    allRows = items.Items.Count;
                    selectedAfterMore = items.SelectedIndex;
                    more.Invoke(dialog, null);
                    afterExtraClick = items.Items.Count;
                });
                Check(dialog.RetryBatch == null, "closing the dialog still returned a batch");
            }
            Check(busyHidden && busyHint.StartsWith("正在导入"), "cleanup offered while an import is running: " + busyHint);
            Check(cleanHidden && cleanRows == 1, "record without leftovers offers cleanup");
            Check(firstPage == ImportHistoryDialog.PageRows + 1 && lastOfFirstPage == "显示更多", "long record not paged: " + firstPage + " / " + lastOfFirstPage);
            Check(allRows == ImportHistoryDialog.PageRows + 5 && selectedAfterMore == ImportHistoryDialog.PageRows && afterExtraClick == allRows,
                "show-more did not append the remaining rows: " + allRows + " / " + selectedAfterMore + " / " + afterExtraClick);

            // 没有日志目录：显示空状态
            string emptyText = null;
            using (ImportHistoryDialog dialog = new ImportHistoryDialog(Path.Combine(root, "import-history", "missing"), delegate { return false; }))
                whenLoaded(dialog, delegate { emptyText = (string)field(dialog, "placeholder"); });
            Check(emptyText == "还没有导入记录", "empty journal state wrong: " + emptyText);

            // 读取还没结束就关闭：后台线程不得再回调已关闭的窗口
            using (ImportHistoryDialog dialog = new ImportHistoryDialog(journal, delegate { return false; }))
            {
                dialog.Opacity = 0;
                dialog.Shown += delegate { dialog.Close(); };
                ConfirmDialog.ShowModal(dialog);
                PumpUntil(delegate { return false; }, 300);
                Check(((ListBox)field(dialog, "batchList")).Items.Count == 0 && dialog.RetryBatch == null, "closed dialog still received the journal");
            }
        }

        // 操作记录的文字规则：内部名称换成中文、摘要换成一句话、改动语句按不可信输入解析并说明改了什么。
        private static void OperationHistoryRules()
        {
            Check(OperationHistory.Title("physical.files.import") == "导入文件" && OperationHistory.Title("CreateCollectionUnder") == "新建集合"
                && OperationHistory.Title("migration.future.step") == "数据升级" && OperationHistory.IsKnown("migration.future.step"), "known action names wrong");
            Check(OperationHistory.Title("SomeFutureMethod") == "修改数据" && !OperationHistory.IsKnown("SomeFutureMethod")
                && OperationHistory.Title(null) == "修改数据" && !OperationHistory.IsKnown(""), "unknown action must fall back without throwing");
            Check(OperationHistory.IsSystem("catalog.discover") && !OperationHistory.IsSystem("catalogue") && !OperationHistory.IsSystem(null), "system records misjudged");
            Check(OperationHistory.UndoBlock("physical.files.move").Contains("布局备份") && OperationHistory.UndoBlock("migration.xml").Contains("从备份恢复")
                && OperationHistory.UndoBlock("restore.snapshot") != null && OperationHistory.UndoBlock("OnAddCategory") == null, "undo rules differ from the database");

            Check(OperationHistory.ChangeCount("+1 ~0 -0; rows=3") == 3 && OperationHistory.ChangeCount("undo 4; rows=12") == 12
                && OperationHistory.ChangeCount("rows=x") == -1 && OperationHistory.ChangeCount("") == -1 && OperationHistory.ChangeCount(null) == -1, "change count wrong");
            Check(OperationHistory.SummaryText("OnAddCategory", "+1 ~0 -0; rows=2", 0, null) == "改动 2 处"
                && OperationHistory.SummaryText("catalog.discover", "new identities=7", 0, null) == "登记 7 个新项目"
                && OperationHistory.SummaryText("restore.snapshot", "safety=a.db", 0, null) == "恢复前的数据已留底"
                && OperationHistory.SummaryText("rollback", "undo 4; rows=1", 4, null) == "撤销了之前的一次操作"
                && OperationHistory.SummaryText("rollback", "undo 4; rows=1", 4, delegate(long id) { return id == 4 ? "新建分类" : null; }) == "撤销了「新建分类」"
                && OperationHistory.SummaryText("x", null, 0, null) == "", "summary text wrong");

            bool ok;
            Check(OperationHistory.CheckText("integrity=ok; foreign_key_errors=0; schema=9; records=12; items=3; memberships=5; journal=wal", out ok)
                == "检查通过，数据库没有损坏（3 个项目，12 条配置记录）。" && ok, "healthy check text wrong");
            Check(OperationHistory.CheckText("integrity=row 3 missing; foreign_key_errors=0", out ok).StartsWith("数据库文件有损坏") && !ok, "corruption not reported");
            Check(OperationHistory.CheckText("integrity=ok; foreign_key_errors=2", out ok).StartsWith("发现 2 处") && !ok, "foreign key errors not reported");
            Check(OperationHistory.CheckText(null, out ok) == "没有得到检查结果，请稍后再试。" && !ok, "empty check result treated as healthy");

            // 语句解析：文本里的逗号、括号、单引号、换行都不能打断解析；不符合格式的一律返回 null
            Dictionary<string, string> values = OperationHistory.ParseValues(
                "INSERT INTO groups(id,key,name,parent_id) VALUES(7,'col:照片/it''s, (a)\nb','it''s, (a)',NULL)");
            Check(values != null && values["id"] == "7" && values["key"] == "col:照片/it's, (a)\nb" && values["name"] == "it's, (a)" && values["parent_id"] == null,
                "insert statement parsed wrong");
            values = OperationHistory.ParseValues("UPDATE records SET record_key='k',ordinal=-3,payload='<a b=''1''>x, y WHERE id=9</a>',slot=X'00ff' WHERE id=12");
            Check(values != null && values["ordinal"] == "-3" && values["payload"] == "<a b='1'>x, y WHERE id=9</a>" && values["slot"] == "（二进制数据）" && values["id"] == "12",
                "update statement parsed wrong");
            foreach (string bad in new string[] { null, "", "DELETE FROM groups WHERE id=7", "DROP TABLE groups", "INSERT INTO groups(id,key) VALUES(7,'abc",
                "INSERT INTO groups(id,key) VALUES(7)", "INSERT INTO groups(id) VALUES(1); DROP TABLE groups", "INSERT INTO groups(id", "UPDATE groups SET name='a'",
                "UPDATE groups SET name= WHERE id=1", "UPDATE groups SET name='a' WHERE id=1 OR 1=1" })
                Check(OperationHistory.ParseValues(bad) == null, "malformed statement accepted: " + bad);

            Dictionary<long, string> groups = new Dictionary<long, string>(), entries = new Dictionary<long, string>();
            groups[5] = "照片"; groups[6] = "col:照片/旅行"; entries[9] = "add:C:\\p\\a.jpg"; entries[10] = "af:记事本";
            entries[11] = "lnk:C:\\s\\画图.LNK";
            // 项目键的四种写法：按路径登记的取文件名（快捷方式不带扩展名），按名称登记的去前缀，没有前缀的（含盘符）原样显示
            Check(OperationHistory.EntryName("add:C:\\p\\a.jpg") == "a.jpg" && OperationHistory.EntryName("lnk:C:\\s\\画图.LNK") == "画图"
                && OperationHistory.EntryName("af:记事本") == "记事本" && OperationHistory.EntryName("reg:7-Zip") == "7-Zip"
                && OperationHistory.EntryName("C:\\x\\y.txt") == "C:\\x\\y.txt" && OperationHistory.EntryName("add:") == "add:"
                && OperationHistory.EntryName("lnk:.lnk") == ".lnk" && OperationHistory.EntryName(null) == "（未命名）", "entry display name wrong");
            Check(OperationHistory.EntryPath("lnk:C:\\s\\画图.LNK") == "C:\\s\\画图.LNK" && OperationHistory.EntryPath("af:记事本") == ""
                && OperationHistory.EntryPath("add:") == "" && OperationHistory.EntryPath(null) == "", "entry path wrong");
            // 旧版本用过的内部名称也要有中文名
            Check(OperationHistory.Title("CommitImport") == "导入文件" && OperationHistory.Title("OnCreateCollection") == "新建集合"
                && OperationHistory.Title("DeleteEntriesDisk") == "删除文件" && OperationHistory.Title("RenameAddedEntry") == "修改显示名称"
                && OperationHistory.Title("MakeSetCat") == "修改所属分类", "legacy action names not translated");
            Func<string, string, string, string> text = delegate(string entity, string undoSql, string redoSql)
            {
                ChangeRow row = OperationHistory.Parse(1, entity, undoSql, redoSql);
                OperationHistory.Describe(row, groups, entries);
                return OperationHistory.RowText(row);
            };
            Func<string, string, string, string, string, string> record = delegate(string section, string item, string group, string ordinal, string payload)
            {
                return "INSERT INTO records(id,record_key,section,item_id,group_id,ordinal,slot,payload) VALUES(1,'k','" + section + "'," + item + "," + group
                    + "," + ordinal + ",''," + payload + ")";
            };
            string delete = "DELETE FROM records WHERE id=1";
            Check(text("records", delete, record("ItemCategories", "9", "6", "0", "'<Item />'")) == "新增 归属：a.jpg\r\n加入 照片 / 旅行", "membership text wrong");
            Check(text("records", record("ItemCategories", "9", "6", "0", "'<Item />'"), delete) == "删除 归属：a.jpg\r\n移出 照片 / 旅行", "removed membership text wrong");
            Check(text("records", delete, record("Favorites", "10", "NULL", "0", "'<Item />'")) == "新增 收藏：记事本", "favorite text wrong");
            Check(text("records", delete, record("Favorites", "77", "NULL", "0", "'<Item />'")) == "新增 收藏：已不存在的项目", "missing entry not explained");
            Check(text("records", delete, record("Hidden", "11", "NULL", "0", "'<Item />'")) == "新增 隐藏：画图\r\nC:\\s\\画图.LNK", "shortcut entry text wrong");
            Check(text("records", "UPDATE records SET section='CustomCategories',group_id=5,ordinal=0,payload='<Item />' WHERE id=1",
                "UPDATE records SET section='CustomCategories',group_id=5,ordinal=2,payload='<Item />' WHERE id=1") == "修改 自定义分类：照片\r\n第 1 位 → 第 3 位"
                && text("records", "UPDATE records SET section='CategoryViewModes',group_id=5,ordinal=0,payload='<Item grid=\"1\" />' WHERE id=1",
                    "UPDATE records SET section='CategoryViewModes',group_id=5,ordinal=0,payload='<Item grid=\"0\" />' WHERE id=1") == "修改 显示方式：照片\r\n内容有调整",
                "a modified record must say what changed");
            Check(text("entries", "UPDATE entries SET key='add:C:\\p\\old.jpg' WHERE id=9", "UPDATE entries SET key='add:C:\\p\\a.jpg' WHERE id=9")
                == "修改 项目登记：a.jpg\r\n原为：C:\\p\\old.jpg → C:\\p\\a.jpg"
                && text("entries", "UPDATE entries SET key='af:旧名' WHERE id=10", "UPDATE entries SET key='af:记事本' WHERE id=10") == "修改 项目登记：记事本\r\n原为：旧名",
                "renamed entry must show the old name");
            Check(text("records", "UPDATE records SET section='CategoryOverrides',item_id=9,group_id=5,ordinal=0 WHERE id=1",
                "UPDATE records SET section='CategoryOverrides',item_id=9,group_id=99,ordinal=0 WHERE id=1") == "修改 所属分类：a.jpg\r\n照片 → 已删除的分类或集合",
                "category change text wrong");
            Check(text("records", "UPDATE records SET section='ItemOrders',item_id=9,group_id=5,ordinal=0 WHERE id=1",
                "UPDATE records SET section='ItemOrders',item_id=9,group_id=5,ordinal=4 WHERE id=1") == "修改 排序：a.jpg\r\n照片 中第 1 位 → 第 5 位", "order text wrong");
            Check(text("records", "UPDATE records SET section='Settings',slot='CloseToTray',payload='<CloseToTray>true</CloseToTray>' WHERE id=1",
                "UPDATE records SET section='Settings',slot='CloseToTray',payload='<CloseToTray>false</CloseToTray>' WHERE id=1")
                == "修改 设置：关闭时最小化到托盘\r\n开 → 关", "setting text wrong");
            Check(text("records", "INSERT INTO records(id,section,slot,payload) VALUES(1,'Settings','Favorites','<Favorites />')", delete)
                == "删除 内部标记：「收藏」为空的占位记录\r\n程序自用，不代表内容有增减", "empty-list placeholder shown as a setting");
            Check(text("groups", "DELETE FROM groups WHERE id=6", "INSERT INTO groups(id,key,name,parent_id) VALUES(6,'col:照片/旅行','旅行',5)") == "新增 集合：照片 / 旅行"
                && text("groups", "UPDATE groups SET key='工具',name='工具',parent_id=NULL WHERE id=3", "UPDATE groups SET key='软件',name='软件',parent_id=NULL WHERE id=3")
                    == "修改 分类：软件\r\n原名：工具", "group text wrong");
            Check(text("added_items", "DELETE FROM added_items WHERE id=1",
                "INSERT INTO added_items(id,item_id,group_id,path,path_key,name,folder,resource,origin,origin_key) VALUES(1,9,5,'C:\\p\\a.jpg','c:\\p\\a.jpg','a',0,1,NULL,NULL)")
                == "新增 文件：a\r\nC:\\p\\a.jpg  ·  所在：照片", "added file text wrong");
            Check(text("added_items", "UPDATE added_items SET group_id=5,path='C:\\p\\a.jpg',name='a',folder=0,resource=1 WHERE id=1",
                "UPDATE added_items SET group_id=5,path='C:\\p\\a.jpg',name='b',folder=0,resource=1 WHERE id=1") == "修改 文件：b\r\n名称：a → b", "rename text wrong");
            Check(text("entries", "DELETE FROM entries WHERE id=9", "INSERT INTO entries(id,key) VALUES(9,'add:C:\\p\\a.jpg')") == "新增 项目登记：a.jpg\r\nC:\\p\\a.jpg",
                "entry text wrong");
            Check(text("records", "garbage", "garbage").StartsWith("其它 其它：无法解读的改动") && text("unknown_table", "DELETE FROM unknown_table WHERE id=1",
                "INSERT INTO unknown_table(id) VALUES(1)") == "新增 其它：unknown_table", "unreadable change must be labelled, not thrown");
            Check(OperationHistory.SettingValue("<A><B>1</B></A>") == "（多项内容）" && OperationHistory.SettingValue("<A>") == "（无法读取）"
                && OperationHistory.SettingValue("<!DOCTYPE a [<!ENTITY e \"x\">]><a>&e;</a>") == "（无法读取）" && OperationHistory.SettingValue("") == "（空）",
                "setting value must not expand entities or throw");
            ChangeRow lengthy = OperationHistory.Parse(1, "entries", "DELETE FROM entries WHERE id=1",
                "INSERT INTO entries(id,key) VALUES(1,'add:C:\\" + new string('长', 900) + "\nx')");
            OperationHistory.Describe(lengthy, groups, entries);
            Check(lengthy.Title.Length <= 201 && lengthy.Detail.Length <= 401 && lengthy.Title.IndexOf('\n') < 0, "long text not clipped to one line");

            // 列表规则：撤销与再撤销交替标记；只有最新的业务操作可撤销；格式不对的行跳过
            List<string[]> rows = new List<string[]>();
            rows.Add(new string[] { "5", "2026-11-14T05:00:00.0000000Z", "rollback", "undo 4; rows=1", "4" });
            rows.Add(new string[] { "4", "2026-11-14T04:00:00.0000000Z", "rollback", "undo 3; rows=1", "3" });
            rows.Add(new string[] { "3", "2026-11-14T03:00:00.0000000Z", "OnAddCategory", "+1 ~0 -0; rows=2", null });
            rows.Add(new string[] { "2", "not a time", "physical.files.import", "+1 ~0 -0; rows=3", null });
            rows.Add(new string[] { "x", "2026-11-14T03:00:00.0000000Z", "OnAddCategory", "", null });
            rows.Add(new string[] { "1", "2026-11-14T01:00:00.0000000Z", "catalog.discover", "new identities=2", null });
            List<DatabasePanel.Op> ops = DatabasePanel.ReadOps(rows);
            DatabasePanel.Annotate(ops);
            Check(ops.Count == 5 && ops[0].TimeKnown && ops[0].Time == new DateTime(2026, 11, 14, 5, 0, 0, DateTimeKind.Utc).ToLocalTime() && !ops[3].TimeKnown,
                "operation rows read wrong");
            Check(!ops[0].Reverted && ops[1].Reverted && !ops[2].Reverted, "undo of an undo must make the original effective again");
            Check(ops[0].ToString() == "撤销操作，撤销了「撤销操作」" && ops[2].ToString() == "新建分类，改动 2 处" && ops[4].ToString() == "扫描登记新项目，登记 2 个新项目",
                "list line wrong: " + ops[0] + " / " + ops[2] + " / " + ops[4]);
            Check(DatabasePanel.CanUndo(ops[0], 5) && DatabasePanel.NoticeText(ops[0], 5).StartsWith("这是一次撤销") && DatabasePanel.StatusText(ops[0], 5) == "可撤销",
                "latest undo record must be undoable");
            Check(!DatabasePanel.CanUndo(ops[2], 5) && DatabasePanel.NoticeText(ops[2], 5).StartsWith("只能撤销最新的一次操作")
                && DatabasePanel.NoticeText(ops[1], 5) == "这次操作已经被撤销。" && DatabasePanel.StatusText(ops[1], 5) == "已撤销", "older records offered for undo");
            Check(!DatabasePanel.CanUndo(ops[3], 2) && DatabasePanel.NoticeText(ops[3], 2).Contains("布局备份") && DatabasePanel.StatusText(ops[3], 2) == "不可撤销",
                "operation that touched files offered for undo");
            Check(!DatabasePanel.CanUndo(ops[4], 1) && DatabasePanel.StatusText(ops[4], 1) == "系统记录" && !DatabasePanel.CanUndo(null, 0), "system record offered for undo");
        }

        // 窗口的完整链路：默认不列系统记录；最新的业务操作可撤销；明细和更早的记录分页读取；撤销后数据库确实退回。
        /// <summary>路径选择窗口：后台路径检查的各种结果，以及窗口里「再点一次才新建/替换」、业务校验不通过时留在窗口里的流程。</summary>
        private static void PathPickerFlow()
        {
            string dir = Path.Combine(root, "path-picker");
            Directory.CreateDirectory(dir);
            string existing = Path.Combine(dir, "已有.db"), other = Path.Combine(dir, "notes.txt"), locked = Path.Combine(dir, "只读.db");
            File.WriteAllText(existing, "db");
            File.WriteAllText(other, "text");
            File.WriteAllText(locked, "db");
            File.SetAttributes(locked, FileAttributes.ReadOnly);
            string[] db = new string[] { ".db" };
            try
            {
                // 选文件夹：已有的直接通过；文件、缺失的文件夹报错；允许新建时先提示，同一路径再确认才新建
                PathPickerCheck check = PathPickerDialog.CheckPath(PathPickerMode.Folder, dir + "\\", null, false, null, null);
                Check(check.Error == null && !check.AskCreate && check.Path == dir, "existing folder rejected: " + check.Error + " / " + check.Path);
                check = PathPickerDialog.CheckPath(PathPickerMode.Folder, existing, null, true, null, null);
                Check(check.Error != null && check.Error.Contains("文件"), "a file was accepted as a folder");
                string fresh = Path.Combine(dir, "新建", "子目录");
                check = PathPickerDialog.CheckPath(PathPickerMode.Folder, fresh, null, false, null, null);
                Check(check.Error != null && !Directory.Exists(fresh), "a missing folder was accepted without permission to create it");
                check = PathPickerDialog.CheckPath(PathPickerMode.Folder, fresh, null, true, null, null);
                Check(check.AskCreate && check.Error == null && !Directory.Exists(fresh), "a missing folder was created without asking first");
                check = PathPickerDialog.CheckPath(PathPickerMode.Folder, fresh, null, true, Path.Combine(dir, "别的"), null);
                Check(check.AskCreate && !Directory.Exists(fresh), "confirming a different path created this folder");
                check = PathPickerDialog.CheckPath(PathPickerMode.Folder, fresh, null, true, fresh, null);
                Check(check.Error == null && !check.AskCreate && Directory.Exists(fresh), "a confirmed folder was not created: " + check.Error);
                check = PathPickerDialog.CheckPath(PathPickerMode.Folder, "C:\\bad|name", null, true, null, null);
                Check(check.Error != null, "an invalid path was accepted");

                // 打开文件：只接受存在的、扩展名匹配的文件
                check = PathPickerDialog.CheckPath(PathPickerMode.OpenFile, existing, db, false, null, null);
                Check(check.Error == null && check.Path == existing, "an existing backup was rejected: " + check.Error);
                Check(PathPickerDialog.CheckPath(PathPickerMode.OpenFile, other, db, false, null, null).Error != null, "a file of the wrong type was accepted");
                Check(PathPickerDialog.CheckPath(PathPickerMode.OpenFile, Path.Combine(dir, "没有.db"), db, false, null, null).Error != null, "a missing file was accepted");
                Check(PathPickerDialog.CheckPath(PathPickerMode.OpenFile, dir, db, false, null, null).Error != null, "a folder was accepted as a file");

                // 保存：补扩展名；同名文件先提示、确认后才接受；只读同名文件、缺失的文件夹、只写了文件夹都报错
                check = PathPickerDialog.CheckPath(PathPickerMode.SaveFile, Path.Combine(dir, "新备份"), db, false, null, null);
                Check(check.Error == null && !check.AskOverwrite && check.Path == Path.Combine(dir, "新备份.db"), "missing extension was not added: " + check.Path);
                check = PathPickerDialog.CheckPath(PathPickerMode.SaveFile, Path.Combine(dir, "已有"), db, false, null, null);
                Check(check.AskOverwrite && check.Path == existing, "an existing file was replaced without asking");
                check = PathPickerDialog.CheckPath(PathPickerMode.SaveFile, existing, db, false, null, existing);
                Check(check.Error == null && !check.AskOverwrite, "a confirmed replacement was refused");
                Check(PathPickerDialog.CheckPath(PathPickerMode.SaveFile, locked, db, false, null, locked).Error != null, "a read-only file was offered for replacement");
                Check(PathPickerDialog.CheckPath(PathPickerMode.SaveFile, Path.Combine(dir, "没有", "a.db"), db, false, null, null).Error != null, "saving into a missing folder was accepted");
                Check(PathPickerDialog.CheckPath(PathPickerMode.SaveFile, dir + "\\", db, false, null, null).Error != null, "a folder without a file name was accepted");

                // 列文件：只列匹配的类型，不列隐藏文件，超过上限只计数
                string many = Path.Combine(dir, "many");
                Directory.CreateDirectory(many);
                for (int i = 0; i < PathPickerDialog.MaxListedFiles + 3; i++) File.WriteAllText(Path.Combine(many, "b" + i + ".db"), "x");
                File.WriteAllText(Path.Combine(many, "skip.txt"), "x");
                string hidden = Path.Combine(many, "hidden.db");
                File.WriteAllText(hidden, "x");
                File.SetAttributes(hidden, FileAttributes.Hidden);
                int omitted;
                string error;
                List<string> files = PathPickerDialog.ListFiles(many, db, out omitted, out error);
                Check(error == null && files.Count == PathPickerDialog.MaxListedFiles && omitted == 3
                    && files.All(f => f.EndsWith(".db") && !f.EndsWith("hidden.db")) && Path.GetFileName(files[1]) == "b1.db",
                    "file listing did not filter, sort or cap: " + files.Count + " / " + omitted);
                PathPickerDialog.ListFiles(Path.Combine(dir, "不存在"), db, out omitted, out error);
                Check(error != null, "listing a missing folder reported no error");

                // 窗口流程：业务校验不通过时留在窗口里提示；保存到同名文件要点两次；输入变化后需要重新确认
                Func<PathPickerDialog, string, object> field = delegate(PathPickerDialog d, string name) { return typeof(PathPickerDialog).GetField(name, Private).GetValue(d); };
                Action<PathPickerDialog> confirm = delegate(PathPickerDialog d)
                {
                    d.Confirm();
                    PumpUntil(delegate { return !(bool)field(d, "checking"); }, 15000);
                };
                using (PathPickerDialog picker = new PathPickerDialog(new PathPickerOptions
                {
                    Title = "测试", Mode = PathPickerMode.Folder, ConfirmText = "添加", AllowCreateFolder = true, InitialFolder = dir,
                    Validate = delegate(string path)
                    {
                        if (path == Path.Combine(dir, "会抛异常")) throw new InvalidOperationException("校验出错");
                        return path == fresh || path == Path.Combine(dir, "被拒绝") ? "存储位置不能互相包含" : null;
                    }
                }))
                {
                    picker.Opacity = 0;
                    picker.Show();
                    Application.DoEvents();
                    TextBox box = (TextBox)field(picker, "pathBox");
                    Label notice = (Label)field(picker, "noticeLabel");
                    Check(box.Text == dir && notice.Text.Contains(dir), "initial folder was not preselected: " + box.Text);
                    box.Text = fresh;
                    confirm(picker);
                    Check(picker.Visible && picker.ResultPath == null && notice.Text.Contains("互相包含"), "a failed validation closed the picker: " + notice.Text);
                    // 业务校验不通过的新路径：不提示新建，点几次都不会先建出空文件夹
                    string rejected = Path.Combine(dir, "被拒绝");
                    box.Text = rejected;
                    confirm(picker);
                    confirm(picker);
                    Check(picker.Visible && !Directory.Exists(rejected) && notice.Text.Contains("互相包含") && field(picker, "confirmedCreate") == null,
                        "a rejected new folder was offered for creation or created: " + notice.Text);
                    // 校验本身出错：留在窗口里说明原因，可以换个路径重试
                    box.Text = Path.Combine(dir, "会抛异常");
                    confirm(picker);
                    Check(picker.Visible && notice.Text.Contains("校验出错") && !Directory.Exists(Path.Combine(dir, "会抛异常")), "a validation exception escaped or closed the picker");
                    string later = Path.Combine(dir, "稍后新建");
                    box.Text = later;
                    confirm(picker);
                    Check(picker.Visible && !Directory.Exists(later) && notice.Text.Contains("新建"), "a missing folder was not announced before creating it");
                    box.Text = later + "2";
                    confirm(picker);
                    Check(picker.Visible && !Directory.Exists(later + "2") && !Directory.Exists(later), "changing the path skipped the second confirmation");
                    confirm(picker);
                    Check(picker.DialogResult == DialogResult.OK && picker.ResultPath == later + "2" && Directory.Exists(later + "2"),
                        "confirming twice did not create and return the folder: " + picker.ResultPath);
                }
                using (PathPickerDialog picker = new PathPickerDialog(new PathPickerOptions
                {
                    Title = "测试", Mode = PathPickerMode.SaveFile, ConfirmText = "备份", InitialFolder = dir, FileName = "已有.db", Extensions = db
                }))
                {
                    picker.Opacity = 0;
                    picker.Show();
                    PumpUntil(delegate { return ((ListBox)field(picker, "list")).Items.Cast<object>().Any(r => r.ToString() == "已有.db"); }, 15000);
                    Check(((ListBox)field(picker, "list")).Items.Cast<object>().Any(r => r.ToString() == "已有.db")
                        && !((ListBox)field(picker, "list")).Items.Cast<object>().Any(r => r.ToString() == "notes.txt"),
                        "the start folder was not expanded with matching files only");
                    TextBox box = (TextBox)field(picker, "pathBox");
                    Check(box.Text == existing, "default file name not placed in the start folder: " + box.Text);
                    confirm(picker);
                    Check(picker.Visible && picker.ResultPath == null && ((Label)field(picker, "noticeLabel")).Text.Contains("替换"), "an existing file was replaced on the first click");
                    confirm(picker);
                    Check(picker.DialogResult == DialogResult.OK && picker.ResultPath == existing, "confirming the replacement did not return the file");
                }

                // 添加页确定前用：已删除的文件和其实是文件夹的路径都算不可用；类型说明过长时缩短
                string sub = Path.Combine(dir, "多选子目录");
                Directory.CreateDirectory(sub);
                string first = Path.Combine(dir, "a.db");
                File.WriteAllText(first, "db");
                Check(PathPickerDialog.UnavailableFiles(new string[] { first, sub, Path.Combine(dir, "没有.db") }).Count == 2,
                    "a folder or a missing file was treated as an available file");
                Check(PathPickerDialog.DescribeExtensions(new string[] { ".a", ".b" }) == ".a / .b"
                    && PathPickerDialog.DescribeExtensions(MediaRules.ImageExt).EndsWith("等 " + MediaRules.ImageExt.Length + " 种"),
                    "long extension lists were not shortened");

                // 添加页一次最多勾选 MaxCheckedFiles 项：拖入超出的不加入并说明，再勾也勾不上
                using (Form host = new Form())
                {
                    host.ShowInTaskbar = false;
                    host.StartPosition = FormStartPosition.Manual;
                    host.Location = new Point(-3000, -3000);
                    host.ClientSize = new Size(1100, 700);
                    Controls.MediaAddPage page = new Controls.MediaAddPage("测试", "文件", db, dir);
                    page.Dock = DockStyle.Fill;
                    host.Controls.Add(page);
                    host.Show();
                    string[] overLimit = new string[PathPickerDialog.MaxCheckedFiles + 1];
                    for (int i = 0; i < overLimit.Length; i++) overLimit[i] = Path.Combine(dir, "上限", i + ".db");
                    page.DropMany(overLimit);
                    PumpUntil(delegate { return !page.Checking; }, 15000);
                    Check(page.CheckedFiles.Count == PathPickerDialog.MaxCheckedFiles && page.StatusText.Contains("最多"),
                        "the checked-file limit was not enforced: " + page.StatusText);
                    page.ToggleFile(Path.Combine(dir, "再加一个.db"));
                    Check(page.CheckedFiles.Count == PathPickerDialog.MaxCheckedFiles, "toggling passed the limit");
                }
            }
            finally { File.SetAttributes(locked, FileAttributes.Normal); }
        }

        private static void CoverPickerFlow()
        {
            string dir = Path.Combine(root, "cover-picker");
            string sub = Path.Combine(dir, "子文件夹");
            Directory.CreateDirectory(sub);
            string wide = Path.Combine(dir, "a-宽图.png"), tall = Path.Combine(dir, "b-竖图.png"), broken = Path.Combine(dir, "c-损坏.jpg");
            using (Bitmap bmp = new Bitmap(16, 10)) bmp.Save(wide, System.Drawing.Imaging.ImageFormat.Png);
            using (Bitmap bmp = new Bitmap(6, 12)) bmp.Save(tall, System.Drawing.Imaging.ImageFormat.Png);
            File.WriteAllText(broken, "not an image");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "text");

            Check(CoverPickerDialog.IsWithin("C:\\a", "C:\\a\\b") && CoverPickerDialog.IsWithin("C:\\", "C:\\x")
                && !CoverPickerDialog.IsWithin("C:\\a", "C:\\ab") && !CoverPickerDialog.IsWithin(null, "C:\\a"),
                "place matching treats a sibling with the same prefix as inside");
            Check(CoverPickerDialog.DescribeImage(1920, 1080, 2048) == "1920 × 1080 · 2.0 KB" && CoverPickerDialog.DescribeImage(0, 0, -1) == string.Empty,
                "image description is wrong: " + CoverPickerDialog.DescribeImage(1920, 1080, 2048));

            // 防误点计时：开机 24.9 天后 TickCount 为负、49.7 天回绕，都只按差值判断
            Check(AppManager.Controls.CoverImageGrid.InSuppressWindow(int.MinValue + 10, int.MinValue + 100, 500)
                && !AppManager.Controls.CoverImageGrid.InSuppressWindow(int.MinValue + 10, int.MinValue + 900, 500)
                && AppManager.Controls.CoverImageGrid.InSuppressWindow(int.MaxValue - 100, int.MinValue + 100, 500)
                && !AppManager.Controls.CoverImageGrid.InSuppressWindow(-5000, -100, 500),
                "click suppression breaks when the tick count is negative or wraps");

            Type gridType = typeof(AppManager.Controls.CoverImageGrid);
            Func<object, string, object> field = delegate(object o, string name) { return o.GetType().GetField(name, Private).GetValue(o); };
            using (CoverPickerDialog picker = new CoverPickerDialog(dir))
            {
                picker.Opacity = 0;
                picker.Show();
                AppManager.Controls.CoverImageGrid grid = (AppManager.Controls.CoverImageGrid)field(picker, "grid");
                TextBox box = (TextBox)field(picker, "pathBox");
                Control confirmButton = (Control)field(picker, "confirmButton");
                Action confirm = delegate
                {
                    picker.Confirm();
                    PumpUntil(delegate
                    {
                        return !(bool)field(picker, "checking") && !(bool)field(picker, "previewLoading") && !(bool)field(picker, "confirmAfterPreview");
                    }, 15000);
                };
                Action<int> select = delegate(int index)
                {
                    gridType.GetMethod("SelectIndex", Private).Invoke(grid, new object[] { index, true });
                    PumpUntil(delegate { return !(bool)field(picker, "previewLoading"); }, 15000);
                };

                // 先列子文件夹再列图片，不是图片的文件不列；露出来的缩略图在后台解出，解不开的标记失败
                PumpUntil(delegate { return grid.Count == 4; }, 15000);
                Check(grid.Count == 4 && !confirmButton.Enabled && box.Text.Length == 0, "folder listing is wrong: " + grid.Count);
                PumpUntil(delegate { return grid.ThumbCount == 2 && grid.FailedCount == 1; }, 15000);
                Check(grid.ThumbCount == 2 && grid.FailedCount == 1, "thumbnails did not load: " + grid.ThumbCount + "/" + grid.FailedCount);

                // 解不开的图片：说明原因，不能当封面
                select(3);
                Check(box.Text == broken && picker.StatusText.Contains("无法读取") && !confirmButton.Enabled, "an unreadable image was offered: " + picker.StatusText);
                picker.Confirm();
                Check(picker.Visible && picker.ResultPath == null, "an unreadable image closed the picker");
                // 手快：选中后预览还没读完就确定（双击），读完发现解不开也不能返回
                MethodInfo choose = typeof(CoverPickerDialog).GetMethod("ChooseImage", Private);
                choose.Invoke(picker, new object[] { tall });
                choose.Invoke(picker, new object[] { broken });
                picker.Confirm();
                PumpUntil(delegate { return !(bool)field(picker, "previewLoading") && !(bool)field(picker, "checking"); }, 15000);
                Application.DoEvents();
                Check(picker.Visible && picker.ResultPath == null && picker.StatusText.Contains("无法读取"),
                    "a fast confirm returned an unreadable image: " + picker.StatusText);

                // 输入文件夹路径确定：进入文件夹，不关闭；上一级回来时光标落在刚才的文件夹上
                box.Text = sub;
                confirm();
                PumpUntil(delegate { return grid.EmptyText.Contains("没有图片"); }, 15000);
                Check(picker.Visible && (string)field(picker, "currentFolder") == sub && box.Text.Length == 0, "a typed folder was not opened");
                picker.GoUp();
                PumpUntil(delegate { return grid.Count == 4; }, 15000);
                Check((string)field(picker, "currentFolder") == dir && grid.SelectedFolder == sub, "going up did not reveal the folder: " + grid.SelectedFolder);

                // 不存在的图片：留在窗口里说明
                box.Text = Path.Combine(dir, "没有.png");
                confirm();
                Check(picker.Visible && picker.StatusText.Contains("不存在"), "a missing image was not reported: " + picker.StatusText);

                // 刚进入文件夹的双击间隔内不响应点击：双击文件夹的第二下不会把新文件夹里的图片当封面用掉
                grid.SuppressClicksBriefly();
                Rectangle cell = (Rectangle)gridType.GetMethod("ClientCell", Private).Invoke(grid, new object[] { 1 });
                MouseEventArgs click = new MouseEventArgs(MouseButtons.Left, 1, cell.X + cell.Width / 2, cell.Y + cell.Width / 2, 0);
                gridType.GetMethod("OnMouseDown", Private).Invoke(grid, new object[] { click });
                gridType.GetMethod("OnMouseDoubleClick", Private).Invoke(grid, new object[] { click });
                Check(picker.Visible && grid.SelectedImage == null, "a click right after opening a folder chose an image");

                // 从别的文件夹拖进多张：进入图片所在文件夹并选中第一张，「只用第一张」的提示在读完文件夹后仍在
                picker.Navigate(sub);
                PumpUntil(delegate { return grid.EmptyText.Contains("没有图片"); }, 15000);
                picker.DropPath(tall, true);
                PumpUntil(delegate { return grid.SelectedImage == tall; }, 15000);
                Application.DoEvents();
                Check((string)field(picker, "currentFolder") == dir && box.Text == tall && picker.StatusText.Contains("只能选一张"),
                    "dropping several images lost the notice: " + picker.StatusText);

                // 正常图片：预览给出原始尺寸
                select(1);
                Check(box.Text == wide && picker.StatusText.StartsWith("16 × 10") && confirmButton.Enabled, "image preview is wrong: " + picker.StatusText);
                // 预览还在读时确定：读完能用就继续确定并返回，记住位置
                choose.Invoke(picker, new object[] { tall });
                picker.Confirm();
                Check(picker.Visible && (bool)field(picker, "confirmAfterPreview") && !confirmButton.Enabled, "a confirm during preview did not wait");
                PumpUntil(delegate { return !picker.Visible; }, 15000);
                Check(picker.DialogResult == DialogResult.OK && picker.ResultPath == tall, "the chosen image was not returned: " + picker.ResultPath);
            }

            // 再次打开：回到上次的文件夹并选中上次用的图片
            using (CoverPickerDialog picker = new CoverPickerDialog(null))
            {
                picker.Opacity = 0;
                picker.Show();
                AppManager.Controls.CoverImageGrid grid = (AppManager.Controls.CoverImageGrid)field(picker, "grid");
                PumpUntil(delegate { return grid.SelectedImage != null; }, 15000);
                Check((string)field(picker, "currentFolder") == dir && grid.SelectedImage == tall && ((TextBox)field(picker, "pathBox")).Text == tall,
                    "the last cover was not reselected: " + grid.SelectedImage);
            }

            // 缩略图还在解码时关闭：这一批任务被取消，后台线程全部退出，不抛异常
            string many = Path.Combine(root, "cover-picker-many");
            Directory.CreateDirectory(many);
            for (int i = 0; i < 60; i++)
                using (Bitmap bmp = new Bitmap(1600, 1200)) bmp.Save(Path.Combine(many, "p" + i + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            CoverPickerDialog closing = new CoverPickerDialog(many);
            closing.Opacity = 0;
            closing.Show();
            AppManager.Controls.CoverImageGrid closingGrid = (AppManager.Controls.CoverImageGrid)field(closing, "grid");
            PumpUntil(delegate { return closingGrid.Count == 60; }, 15000);
            closingGrid.Refresh();   // 画一次，让露出来的格子排队解码
            object closingBatch = field(closingGrid, "batch");
            Func<int> active = delegate { lock (closingBatch) return (int)field(closingBatch, "Active"); };
            Check(active() > 0, "no thumbnail decode was running before closing");
            closing.Close();
            closing.Dispose();
            PumpUntil(delegate { return active() == 0; }, 15000);
            CancellationTokenSource closingCancel = (CancellationTokenSource)field(closingBatch, "Cancel");
            Check(closingCancel.IsCancellationRequested && active() == 0 && closingGrid.ThumbCount == 0,
                "closing the picker left thumbnail work running: active=" + active());

            // 缩略图解码：取正方形中间，解不开返回 null
            using (Bitmap tile = AppManager.Controls.CoverImageGrid.DecodeTile(tall, 40, CancellationToken.None))
                Check(tile != null && tile.Width == 40 && tile.Height == 40, "tile decode size is wrong");
            Check(AppManager.Controls.CoverImageGrid.DecodeTile(broken, 40, CancellationToken.None) == null, "a broken image produced a tile");
        }

        private static void MediaAddPageFlow()
        {
            string dir = Path.Combine(root, "add-page");
            string sub = Path.Combine(dir, "子文件夹");
            Directory.CreateDirectory(sub);
            string a = Path.Combine(dir, "a-宽图.png"), b = Path.Combine(dir, "b-竖图.png"), broken = Path.Combine(dir, "c-损坏.jpg");
            string d = Path.Combine(sub, "d.png"), notes = Path.Combine(dir, "notes.txt"), hidden = Path.Combine(dir, "e-隐藏.png");
            using (Bitmap bmp = new Bitmap(16, 10)) bmp.Save(a, System.Drawing.Imaging.ImageFormat.Png);
            using (Bitmap bmp = new Bitmap(6, 12)) { bmp.Save(b, System.Drawing.Imaging.ImageFormat.Png); bmp.Save(d, System.Drawing.Imaging.ImageFormat.Png); }
            File.WriteAllText(broken, "not an image");
            File.WriteAllText(notes, "text");
            File.WriteAllText(hidden, "x");
            File.SetAttributes(hidden, FileAttributes.Hidden);
            string song = Path.Combine(dir, "song.mp3");
            File.WriteAllText(song, "not really mp3");

            Check(AppManager.Controls.MediaAddPage.IsImageOnly(MediaRules.ImageExt) && !AppManager.Controls.MediaAddPage.IsImageOnly(new string[0])
                && !AppManager.Controls.MediaAddPage.IsImageOnly(null) && !AppManager.Controls.MediaAddPage.IsImageOnly(MediaRules.AudioExt)
                && !AppManager.Controls.MediaAddPage.IsImageOnly(new string[] { ".png", ".pdf" }),
                "image-only detection is wrong");

            // 带文件信息的列举：按名称顺序，隐藏文件不列，超过上限只计数，大小来自目录枚举
            int omitted;
            string error;
            List<FileInfo> infos = PathPickerDialog.ListFileInfos(dir, MediaRules.ImageExt, 2, null, out omitted, out error);
            Check(error == null && infos.Count == 2 && omitted == 1 && infos[0].FullName == a && infos[1].FullName == b && infos[0].Length > 0,
                "file info listing is wrong: " + infos.Count + "/" + omitted);
            PathPickerDialog.ListFileInfos(Path.Combine(dir, "不存在"), MediaRules.ImageExt, 10, null, out omitted, out error);
            Check(error != null, "a missing folder listed without an error");
            // 换了文件夹：读取马上停下，不再继续枚举
            infos = PathPickerDialog.ListFileInfos(dir, MediaRules.ImageExt, 10, delegate { return true; }, out omitted, out error);
            Check(infos.Count == 0 && error == null, "a cancelled listing kept enumerating: " + infos.Count);

            Type gridType = typeof(AppManager.Controls.CoverImageGrid);
            Type pageType = typeof(AppManager.Controls.MediaAddPage);
            Func<object, string, object> field = delegate(object o, string name) { return o.GetType().GetField(name, Private).GetValue(o); };
            MethodInfo check = pageType.GetMethod("Check", Private);
            MethodInfo processKey = pageType.GetMethod("ProcessCmdKey", Private);

            using (Form host = new Form())
            {
                host.ShowInTaskbar = false;
                host.StartPosition = FormStartPosition.Manual;
                host.Location = new Point(-3000, -3000);
                host.ClientSize = new Size(1100, 700);
                AppManager.Controls.MediaAddPage page = new AppManager.Controls.MediaAddPage("测试", "照片", MediaRules.ImageExt, dir);
                page.Dock = DockStyle.Fill;
                List<string> confirmed = null;
                int cancelled = 0;
                page.Confirmed += delegate(List<string> list) { confirmed = list; };
                page.Cancelled += delegate { cancelled++; };
                host.Controls.Add(page);
                host.Show();
                AppManager.Controls.CoverImageGrid grid = (AppManager.Controls.CoverImageGrid)field(page, "grid");
                TextBox box = (TextBox)field(page, "pathBox");
                Control confirmButton = (Control)field(page, "confirmButton");
                Func<bool> idle = delegate { return !page.Loading && !page.Checking && !page.PreviewLoading; };

                // 照片用缩略图网格：先子文件夹，再按名称列出图片；隐藏文件和别的类型不列
                PumpUntil(delegate { return grid.Count == 4 && !page.Loading; }, 15000);
                Check(grid.Count == 4 && page.ListedFiles.Count == 3 && !confirmButton.Enabled && page.CheckedFiles.Count == 0,
                    "folder listing is wrong: " + grid.Count + "/" + page.ListedFiles.Count);
                host.Refresh();

                // 跨文件夹勾选：数量、来自几个文件夹和按钮文字跟着变
                page.ToggleFile(a);
                page.Navigate(sub);
                PumpUntil(delegate { return grid.Count == 1 && !page.Loading; }, 15000);
                page.ToggleFile(d);
                Check(page.CheckedFiles.Count == 2 && page.StatusText.Contains("来自 2 个文件夹") && confirmButton.Enabled && confirmButton.Text == "添加 2 个",
                    "checking across folders is wrong: " + page.StatusText + " / " + confirmButton.Text);

                // 全选当前文件夹：已全部勾上时再按一次是全部取消
                page.ToggleAllListed();
                Check(page.CheckedFiles.Count == 1 && !page.CheckedFiles.Contains(d), "select-all did not clear a fully checked folder");
                page.ToggleAllListed();
                Check(page.CheckedFiles.Count == 2 && page.CheckedFiles[1] == d, "select-all did not check the folder");

                // 「已选的文件」：几个文件夹里勾的列在一起；取消的留在原处，可以再勾回来
                page.ShowChecked();
                Check(page.ShowingChecked && grid.Count == 2 && page.ListedFiles.Count == 2, "the checked view is wrong: " + grid.Count);
                page.ToggleFile(d);
                Check(grid.Count == 2 && page.CheckedFiles.Count == 1, "unchecking in the checked view removed the tile");
                page.ToggleFile(d);
                host.Refresh();

                // 点格子右上角的勾选框切换勾选（双击不再重复切换）；单击图片只预览，双击才勾选
                page.Navigate(dir);
                PumpUntil(delegate { return grid.Count == 4 && !page.Loading; }, 15000);
                Rectangle cell = (Rectangle)gridType.GetMethod("ClientCell", Private).Invoke(grid, new object[] { 2 });
                Rectangle checkBox = (Rectangle)gridType.GetMethod("CheckBoxRect", Private).Invoke(grid, new object[] { new Rectangle(cell.X, cell.Y, cell.Width, cell.Width) });
                MouseEventArgs onBox = new MouseEventArgs(MouseButtons.Left, 1, checkBox.X + checkBox.Width / 2, checkBox.Y + checkBox.Height / 2, 0);
                gridType.GetMethod("OnMouseDown", Private).Invoke(grid, new object[] { onBox });
                gridType.GetMethod("OnMouseDoubleClick", Private).Invoke(grid, new object[] { onBox });
                Check(page.CheckedFiles.Count == 3 && page.CheckedFiles[2] == b, "clicking the check box did not check exactly once");
                Rectangle brokenCell = (Rectangle)gridType.GetMethod("ClientCell", Private).Invoke(grid, new object[] { 3 });
                MouseEventArgs onTile = new MouseEventArgs(MouseButtons.Left, 1, brokenCell.X + brokenCell.Width / 3, brokenCell.Y + brokenCell.Width * 2 / 3, 0);
                gridType.GetMethod("OnMouseDown", Private).Invoke(grid, new object[] { onTile });
                PumpUntil(delegate { return !page.PreviewLoading; }, 15000);
                Check(!page.CheckedFiles.Contains(broken) && page.PreviewPath == broken && page.PreviewFailed,
                    "a single click checked the image or the broken preview was not reported");
                gridType.GetMethod("OnMouseDoubleClick", Private).Invoke(grid, new object[] { onTile });
                Check(page.CheckedFiles.Contains(broken), "a double click did not check the image");
                page.ToggleFile(broken);
                host.Refresh();

                // 预览：图片给出原始尺寸和大小；音乐读不出标签时用文件名
                page.StartPreview(a);
                PumpUntil(delegate { return !page.PreviewLoading; }, 15000);
                Check(page.PreviewText.Contains("尺寸：16 × 10") && page.PreviewText.Contains("大小：") && page.PreviewText.Contains("位置：" + dir),
                    "image preview is wrong: " + page.PreviewText);
                page.StartPreview(song);
                PumpUntil(delegate { return !page.PreviewLoading; }, 15000);
                Check(page.PreviewText.Contains("标题：song") && !page.PreviewFailed, "music preview is wrong: " + page.PreviewText);
                page.StartPreview(Path.Combine(dir, "没有.png"));
                PumpUntil(delegate { return !page.PreviewLoading; }, 15000);
                Check(page.PreviewFailed, "a missing file previewed without an error");
                host.Refresh();

                // 拖进来：类型匹配的勾上，其余跳过并说明；只拖进一个文件夹时打开它
                page.ToggleFile(b);
                page.DropMany(new string[] { b, notes });
                PumpUntil(delegate { return !page.Checking; }, 15000);
                Check(page.CheckedFiles.Contains(b) && page.StatusText.Contains("跳过"), "dropping mixed files is wrong: " + page.StatusText);
                page.DropMany(new string[] { sub });
                PumpUntil(delegate { return idle() && page.CurrentFolder == sub; }, 15000);
                Check(page.CurrentFolder == sub && box.Text.Length == 0, "dropping a folder did not open it: " + page.CurrentFolder);

                // 输入框回车：不存在的说明原因并留着；文件勾上并转到它所在的文件夹选中它
                box.Text = Path.Combine(dir, "没有.png");
                check.Invoke(page, new object[] { false });
                PumpUntil(delegate { return idle(); }, 15000);
                Check(page.StatusText.Contains("不存在") && box.Text.Length > 0, "a missing typed file was not reported: " + page.StatusText);
                page.ToggleFile(b);
                box.Text = b;
                check.Invoke(page, new object[] { false });
                PumpUntil(delegate { return idle() && page.CurrentFolder == dir && grid.SelectedImage == b; }, 15000);
                Check(page.CheckedFiles.Contains(b) && grid.SelectedImage == b && page.PreviewPath == b && box.Text.Length == 0,
                    "a typed file was not checked and revealed");

                // 焦点在「全选」「上一级」上按回车：等同点那个按钮，不会把勾选的文件添加掉
                Control allButton = (Control)field(page, "allButton");
                allButton.Focus();
                int beforeAll = page.CheckedFiles.Count;
                processKey.Invoke(page, new object[] { new Message(), Keys.Enter });
                Check(allButton.Focused && confirmed == null && !page.Checking && page.CheckedFiles.Count != beforeAll,
                    "enter on the select-all button confirmed instead of toggling: " + page.CheckedFiles.Count);
                Check(page.CheckedFiles.Contains(broken), "enter on the select-all button did not check the rest of the folder");
                page.ToggleFile(broken);
                Check(page.CheckedFiles.Count == beforeAll, "fixture did not return to the previous selection");

                // 确定时已经不在的文件取消勾选并留在页面里说明，不交给宿主
                File.Delete(b);
                page.Confirm();
                PumpUntil(delegate { return !page.Checking; }, 15000);
                Check(confirmed == null && !page.CheckedFiles.Contains(b) && page.StatusText.Contains("已不存在"),
                    "a vanished file was handed over: " + page.StatusText);

                // 确定：按勾选先后交出
                page.Confirm();
                PumpUntil(delegate { return confirmed != null; }, 15000);
                Check(confirmed != null && confirmed.Count == 2 && confirmed[0] == a && confirmed[1] == d,
                    "the checked files were not returned in order: " + (confirmed == null ? "null" : string.Join("|", confirmed.ToArray())));

                // Esc：经消息队列通知宿主（不在按键处理中途被释放）
                processKey.Invoke(page, new object[] { new Message(), Keys.Escape });
                Check(cancelled == 0, "cancel was raised synchronously");
                PumpUntil(delegate { return cancelled == 1; }, 5000);
                Check(cancelled == 1, "escape did not cancel");
            }

            // 再次打开同一类：回到上次添加时的文件夹
            using (AppManager.Controls.MediaAddPage again = new AppManager.Controls.MediaAddPage("测试", "照片", MediaRules.ImageExt, null))
                Check(again.CurrentFolder == dir, "the last folder was not remembered: " + again.CurrentFolder);

            // 其它类型用列表：勾选框、双击、单击文件夹进入，空文件夹给出说明，「已选的文件」第二行是所在文件夹
            using (Form host = new Form())
            {
                host.ShowInTaskbar = false;
                host.StartPosition = FormStartPosition.Manual;
                host.Location = new Point(-3000, -3000);
                host.ClientSize = new Size(1100, 700);
                AppManager.Controls.MediaAddPage page = new AppManager.Controls.MediaAddPage("测试", "文件", new string[] { ".txt" }, dir);
                page.Dock = DockStyle.Fill;
                host.Controls.Add(page);
                host.Show();
                ListBox list = (ListBox)field(page, "fileList");
                PumpUntil(delegate { return list.Items.Count == 2 && !page.Loading; }, 15000);
                Check(list.Items.Count == 2 && page.ListedFiles.Count == 1 && page.ListedFiles[0] == notes, "list mode listing is wrong: " + list.Items.Count);
                host.Refresh();
                Rectangle fileRow = list.GetItemRectangle(1);
                MouseEventArgs onBox = new MouseEventArgs(MouseButtons.Left, 1, fileRow.X + 14, fileRow.Y + fileRow.Height / 2, 0);
                pageType.GetMethod("FileList_MouseDown", Private).Invoke(page, new object[] { list, onBox });
                pageType.GetMethod("FileList_MouseDoubleClick", Private).Invoke(page, new object[] { list, onBox });
                Check(page.CheckedFiles.Count == 1 && page.CheckedFiles[0] == notes, "the list check box did not check exactly once");
                MouseEventArgs onName = new MouseEventArgs(MouseButtons.Left, 2, fileRow.X + 200, fileRow.Y + fileRow.Height / 2, 0);
                pageType.GetMethod("FileList_MouseDown", Private).Invoke(page, new object[] { list, onName });
                pageType.GetMethod("FileList_MouseDoubleClick", Private).Invoke(page, new object[] { list, onName });
                Check(page.CheckedFiles.Count == 0, "a double click on a list row did not toggle");
                page.ToggleFile(notes);
                host.Refresh();
                page.StartPreview(notes);
                PumpUntil(delegate { return !page.PreviewLoading; }, 15000);
                Check(page.PreviewText.Contains("大小：") && !page.PreviewText.Contains("尺寸"), "file preview is wrong: " + page.PreviewText);

                Rectangle folderRow = list.GetItemRectangle(0);
                MouseEventArgs onFolder = new MouseEventArgs(MouseButtons.Left, 1, folderRow.X + 200, folderRow.Y + folderRow.Height / 2, 0);
                pageType.GetMethod("FileList_MouseDown", Private).Invoke(page, new object[] { list, onFolder });
                pageType.GetMethod("FileList_MouseUp", Private).Invoke(page, new object[] { list, onFolder });
                PumpUntil(delegate { return page.CurrentFolder == sub && !page.Loading; }, 15000);
                Check(page.CurrentFolder == sub && list.Items.Count == 1 && page.ListedFiles.Count == 0, "clicking a folder row did not open it");
                page.ToggleAllListed();
                Check(page.CheckedFiles.Count == 1 && page.StatusText.Contains("没有可以勾选"), "select-all in an empty folder is wrong: " + page.StatusText);
                page.ShowChecked();
                Check(list.Items.Count == 1 && page.ListedFiles[0] == notes, "the checked list view is wrong");
                host.Refresh();

                // 文件类型不限的分类：拖进文件夹不会被当成文件勾上
                using (AppManager.Controls.MediaAddPage any = new AppManager.Controls.MediaAddPage("测试", "文件", new string[0], sub))
                {
                    any.Dock = DockStyle.Fill;
                    host.Controls.Add(any);
                    any.BringToFront();
                    PumpUntil(delegate { return !any.Loading; }, 15000);
                    any.DropMany(new string[] { dir });
                    PumpUntil(delegate { return !any.Checking && any.CurrentFolder == dir; }, 15000);
                    Check(any.CurrentFolder == dir && any.CheckedFiles.Count == 0, "an unrestricted drop of a folder checked it as a file");
                    any.DropMany(new string[] { sub, notes });
                    PumpUntil(delegate { return !any.Checking; }, 15000);
                    Check(any.CheckedFiles.Count == 1 && any.CheckedFiles[0] == notes && any.StatusText.Contains("1 个文件夹没有添加"),
                        "an unrestricted drop of a folder and a file is wrong: " + any.StatusText);
                    host.Controls.Remove(any);
                }

                // 检查还在后台时关闭：结果作废，不再通知宿主
                bool late = false;
                page.Confirmed += delegate { late = true; };
                page.Confirm();
                host.Close();
                PumpUntil(delegate { return false; }, 500);
                Check(!late && page.IsDisposed, "a confirm finished after the page was closed");
            }
        }

        private static void MediaAddPageFolderAndSoftware()
        {
            Type pageType = typeof(AppManager.Controls.MediaAddPage);
            Func<object, string, object> field = delegate(object o, string name) { return o.GetType().GetField(name, Private).GetValue(o); };
            Func<Form> newHost = delegate
            {
                Form f = new Form();
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-3000, -3000);
                f.ClientSize = new Size(1100, 700);
                return f;
            };

            Check(AppManager.Controls.MediaAddPage.IsDriveRoot("C:\\") && AppManager.Controls.MediaAddPage.IsDriveRoot("D:")
                && !AppManager.Controls.MediaAddPage.IsDriveRoot("C:\\Users") && !AppManager.Controls.MediaAddPage.IsDriveRoot(null)
                && !AppManager.Controls.MediaAddPage.IsDriveRoot("\\\\nas\\photos") && !AppManager.Controls.MediaAddPage.IsDriveRoot("\\\\nas\\photos\\"),
                "drive root detection is wrong");

            // 添加文件夹：中栏只列文件夹，可勾选多个；右栏是当前所在的文件夹；整盘不能加；确定时已不在的取消勾选
            string dir = Path.Combine(root, "add-folders");
            string first = Path.Combine(dir, "甲"), second = Path.Combine(dir, "乙"), dropped = Path.Combine(root, "add-folders-drop");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            Directory.CreateDirectory(dropped);
            Directory.CreateDirectory(Path.Combine(first, "里面"));
            string loose = Path.Combine(dir, "散文件.txt");
            File.WriteAllText(loose, "x");
            using (Form host = newHost())
            {
                AppManager.Controls.MediaAddPage page = AppManager.Controls.MediaAddPage.ForFolders("测试", dir);
                List<string> confirmed = null;
                page.Confirmed += delegate(List<string> list) { confirmed = list; };
                page.Dock = DockStyle.Fill;
                host.Controls.Add(page);
                host.Show();
                ListBox rows = (ListBox)field(page, "fileList");
                PumpUntil(delegate { return !page.Loading && !page.PreviewLoading && rows.Items.Count == 2; }, 15000);
                Check(rows.Items.Count == 2 && page.ListedFiles.Count == 2, "folder mode listed files or missed folders: " + rows.Items.Count);
                Check(page.PreviewPath == dir && page.PreviewText.Contains("2 个文件夹、1 个文件"), "current folder preview is wrong: " + page.PreviewText);
                host.Refresh();

                // 点勾选框只勾选不进入；点名字进入
                string row0 = page.ListedFiles[0];
                Rectangle r0 = rows.GetItemRectangle(0);
                MouseEventArgs onBox = new MouseEventArgs(MouseButtons.Left, 1, r0.X + 14, r0.Y + r0.Height / 2, 0);
                pageType.GetMethod("FileList_MouseDown", Private).Invoke(page, new object[] { rows, onBox });
                pageType.GetMethod("FileList_MouseUp", Private).Invoke(page, new object[] { rows, onBox });
                Check(page.CheckedFiles.Count == 1 && page.CheckedFiles[0] == row0 && page.CurrentFolder == dir, "the folder check box navigated or did not check");
                MouseEventArgs onName = new MouseEventArgs(MouseButtons.Left, 1, r0.X + 200, r0.Y + r0.Height / 2, 0);
                pageType.GetMethod("FileList_MouseDown", Private).Invoke(page, new object[] { rows, onName });
                pageType.GetMethod("FileList_MouseUp", Private).Invoke(page, new object[] { rows, onName });
                PumpUntil(delegate { return page.CurrentFolder == row0 && !page.Loading && !page.PreviewLoading; }, 15000);
                Check(page.CurrentFolder == row0 && page.PreviewPath == row0, "clicking a folder name did not open it");

                // 右栏按钮勾选当前所在的文件夹（再按取消）
                page.GoUp();
                PumpUntil(delegate { return page.CurrentFolder == dir && !page.Loading && !page.PreviewLoading; }, 15000);
                pageType.GetMethod("TogglePreviewed", Private).Invoke(page, null);
                Check(page.CheckedFiles.Contains(dir) && page.CheckedFiles.Count == 2, "the current folder was not checked from the preview");
                pageType.GetMethod("TogglePreviewed", Private).Invoke(page, null);
                Check(!page.CheckedFiles.Contains(dir), "the preview button did not uncheck the current folder");

                // 整个磁盘不能加
                page.ToggleFile(Path.GetPathRoot(dir));
                Check(page.CheckedFiles.Count == 1 && page.StatusText.Contains("整个磁盘"), "a whole drive was checked: " + page.StatusText);

                // 拖进文件夹和文件：文件夹勾上，文件跳过
                page.DropMany(new string[] { dropped, loose });
                PumpUntil(delegate { return !page.Checking; }, 15000);
                Check(page.CheckedFiles.Contains(dropped) && page.CheckedFiles.Count == 2 && page.StatusText.Contains("1 个文件没有添加"),
                    "dropping into the folder page is wrong: " + page.StatusText);

                // 「已选的文件夹」里第二行是上级文件夹
                page.ShowChecked();
                Check(rows.Items.Count == 2 && page.ListedFiles.Count == 2, "the checked folders view is wrong");
                host.Refresh();

                // 输入文件夹路径后点「添加」：直接加入；已经不在的取消勾选并说明
                Directory.Delete(dropped);
                ((TextBox)field(page, "pathBox")).Text = second;
                page.Confirm();
                PumpUntil(delegate { return !page.Checking; }, 15000);
                Check(confirmed == null && page.CheckedFiles.Contains(second) && !page.CheckedFiles.Contains(dropped) && page.StatusText.Contains("已不存在"),
                    "typed folder or vanished folder handling is wrong: " + page.StatusText);
                page.Confirm();
                PumpUntil(delegate { return confirmed != null; }, 15000);
                Check(confirmed != null && confirmed.Count == 2 && confirmed[0] == row0 && confirmed[1] == second,
                    "checked folders were not returned in order: " + (confirmed == null ? "null" : string.Join("|", confirmed.ToArray())));
            }

            // 添加软件：先列已安装的软件；搜索、全选、预览；也能到位置里勾 .exe；两边一起交出
            string exeDir = Path.Combine(root, "add-software");
            Directory.CreateDirectory(exeDir);
            string tool = Path.Combine(exeDir, "便携工具.exe");
            File.WriteAllText(tool, "MZ");
            File.WriteAllText(Path.Combine(exeDir, "说明.txt"), "x");
            AppInfo alpha = new AppInfo { Key = "lnk:add-alpha", Name = "Alpha", TargetPath = Path.Combine(exeDir, "alpha.exe"), Category = "开发工具" };
            AppInfo beta = new AppInfo { Key = "lnk:add-beta", Name = "Beta", TargetPath = Path.Combine(exeDir, "beta.exe") };
            using (Form host = newHost())
            {
                AppManager.Controls.MediaAddPage page = AppManager.Controls.MediaAddPage.ForSoftware("测试", new List<AppInfo> { alpha, beta }, false, exeDir);
                List<string> confirmed = null;
                List<AppInfo> confirmedApps = null;
                page.Confirmed += delegate(List<string> list) { confirmed = list; confirmedApps = page.CheckedApps; };
                page.Dock = DockStyle.Fill;
                host.Controls.Add(page);
                host.Show();
                Check(page.ShowingInstalled && page.ListedApps.Count == 2, "the add-software page did not list installed software");
                // 列已安装的软件时没有「上一级」：退格不会跳到别的文件夹
                page.GoUp();
                Check(page.ShowingInstalled && page.ListedApps.Count == 2, "going up left the installed software list");
                Controls.SearchField search = (Controls.SearchField)field(page, "appSearch");
                search.Text = "Beta";
                Check(page.ListedApps.Count == 1 && page.ListedApps[0] == beta, "searching installed software is wrong");
                search.Text = string.Empty;
                page.ToggleAllListed();
                Check(page.CheckedApps.Count == 2 && page.StatusText.Contains("2 个软件"), "select-all installed software is wrong: " + page.StatusText);
                page.ToggleApp(alpha);
                PumpUntil(delegate { return page.AppIconCount == 2; }, 15000);
                host.Refresh();
                page.StartAppPreview(beta);
                PumpUntil(delegate { return !page.PreviewLoading; }, 15000);
                Check(page.PreviewApp == beta && page.PreviewText.Contains("所在分类：未分类") && page.PreviewText.Contains("位置：" + beta.TargetPath),
                    "installed software preview is wrong: " + page.PreviewText);
                host.Refresh();

                // 便携程序：到位置里只列 .exe，勾上后和已安装的一起交出
                page.Navigate(exeDir);
                PumpUntil(delegate { return !page.Loading; }, 15000);
                Check(!page.ShowingInstalled && page.ListedFiles.Count == 1 && page.ListedFiles[0] == tool && page.ListedApps.Count == 0,
                    "browsing for programs listed other files or kept the installed list");
                // 文件夹里的全选只勾这里的程序，不连带刚才列过的已安装软件
                page.ToggleAllListed();
                Check(page.CheckedFiles.Count == 1 && page.CheckedApps.Count == 1, "select-all in a folder also checked installed software");
                page.ToggleFile(tool);
                // 拖进文件夹和程序：程序勾上；提示不指向软件页没有的「添加文件夹…」
                page.DropMany(new string[] { exeDir, tool });
                PumpUntil(delegate { return !page.Checking; }, 15000);
                Check(page.CheckedFiles.Contains(tool) && page.StatusText.Contains("1 个文件夹没有添加") && !page.StatusText.Contains("添加文件夹")
                    && page.StatusText.Contains("已选的软件"), "the drop notice on the software page is wrong: " + page.StatusText);
                page.StartPreview(tool);
                PumpUntil(delegate { return !page.PreviewLoading; }, 15000);
                Check(page.PreviewText.Contains("大小："), "program preview is wrong: " + page.PreviewText);
                host.Refresh();
                page.ShowChecked();
                Check(page.ListedApps.Count == 1 && page.ListedFiles.Count == 1, "the checked software view is wrong");
                host.Refresh();
                page.Confirm();
                PumpUntil(delegate { return confirmed != null; }, 15000);
                Check(confirmed != null && confirmed.Count == 1 && confirmed[0] == tool && confirmedApps.Count == 1 && confirmedApps[0] == beta,
                    "installed software and programs were not handed over together");
            }
        }

        private static void MediaAddPageInMainWindow()
        {
            AppConfig config = RenameConfig("add-page-main");
            config.AutoScan = false;
            string collectionKey = CollectionKeys.Make("照片", "旅行");
            AppInfo image = AddEntry(config, "照片", Path.Combine(config.ResourceStorePath, "照片", "trip.png"), false, true, "trip");
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "旅行" });
            CollectionOps.AddMember(config, image.Key, collectionKey);
            Check(config.Save(), "database fixture save failed");
            config.Database.PublishCatalog(config, new List<AppInfo> { image });

            using (MainForm form = new MainForm(config))
            {
                typeof(MainForm).GetField("scanRunning", Private).SetValue(form, true);
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-3000, -3000);
                form.ClientSize = new Size(1200, 720);
                form.Show();
                Application.DoEvents();
                typeof(MainForm).GetField("allApps", Private).SetValue(form, new List<AppInfo> { image });
                Invoke(form, "OnModeChanged", Controls.SidebarControl.ContentMode.Website);
                Application.DoEvents();

                Func<string, object> field = delegate(string name) { return typeof(MainForm).GetField(name, Private).GetValue(form); };
                Controls.SidebarControl sidebar = (Controls.SidebarControl)field("sidebar");
                Controls.CollectionSidebar collection = (Controls.CollectionSidebar)field("collectionSidebar");
                Controls.AppGridControl grid = (Controls.AppGridControl)field("grid");
                Control settings = (Control)field("settingsPage");
                Control addBtn = (Control)field("addBtn");
                Check(collection.Visible && grid.Visible && addBtn.Visible, "fixture did not open an editable resource category");

                // 在主窗口里打开：盖住标题行以下，侧栏、集合栏、列表和添加按钮都收起
                Invoke(form, "PickFiles");
                Application.DoEvents();
                Controls.MediaAddPage page = (Controls.MediaAddPage)field("addPage");
                Check(page != null && page.Visible && form.Controls.GetChildIndex(page) == 0, "the add page did not open on top");
                Check(page.Left == 0 && page.Width == form.ClientSize.Width && page.Bottom == form.ClientSize.Height, "the add page does not fill the window: " + page.Bounds);
                Check(!sidebar.Visible && !collection.Visible && !grid.Visible && !addBtn.Visible, "main content stayed visible under the add page");

                // 打开期间的刷新、集合同步和状态条都不能把别的控件顶到页面上面
                Invoke(form, "RefreshGrid");
                Invoke(form, "UpdateCollectionUi");
                Invoke(form, "ShowStatusBar", "测试", 0, 0);
                Application.DoEvents();
                Check(!grid.Visible && !collection.Visible && form.Controls.GetChildIndex(page) == 0, "a refresh surfaced controls over the add page");
                Invoke(form, "HideStatusBar");
                Check(!(bool)typeof(MainForm).GetProperty("CanImportHere", Private).GetValue(form, null), "drops and pastes still import while the add page is open");

                // 再点添加不会叠第二个页面；窗口缩放时页面跟着铺满
                Invoke(form, "PickFiles");
                Check(ReferenceEquals(field("addPage"), page), "a second add page was opened");
                // 新尺寸按窗口最小尺寸来取：最小尺寸随显示缩放变大，125% 下写死的 1000×640 小于它，窗口根本缩不到
                Size resized = new Size(form.MinimumSize.Width + 40, form.MinimumSize.Height + 40);
                form.ClientSize = resized;
                Application.DoEvents();
                Check(form.ClientSize == resized && page.Width == resized.Width && page.Bottom == resized.Height,
                    "the add page did not follow the window size: " + page.Bounds + " in " + form.ClientSize);

                // Esc 返回：页面释放，侧栏、集合栏和列表按原样恢复
                typeof(Controls.MediaAddPage).GetMethod("ProcessCmdKey", Private).Invoke(page, new object[] { new Message(), Keys.Escape });
                PumpUntil(delegate { return field("addPage") == null; }, 5000);
                Application.DoEvents();
                Check(field("addPage") == null && page.IsDisposed, "escape did not close the add page");
                Check(sidebar.Visible && collection.Visible && grid.Visible && addBtn.Visible && grid.Left == collection.Right,
                    "main content did not return after closing the add page");

                // 托盘菜单「设置」：先关掉添加页再进设置页
                Invoke(form, "PickFiles");
                page = (Controls.MediaAddPage)field("addPage");
                Invoke(form, "OpenSettings");
                Application.DoEvents();
                Check(field("addPage") == null && page.IsDisposed && settings.Visible && !sidebar.Visible, "opening settings did not close the add page");
                Invoke(form, "CloseSettings");
                Application.DoEvents();
                Check(sidebar.Visible && collection.Visible && grid.Visible, "main content did not return after settings");

                // 添加文件夹、添加软件也在主窗口里打开，关闭后主内容区恢复
                Invoke(form, "PickFolder");
                page = (Controls.MediaAddPage)field("addPage");
                Check(page != null && !page.ShowingInstalled && !sidebar.Visible, "the add-folder page did not open in the main window");
                Invoke(form, "CloseAddPage");
                Invoke(form, "PickSoftware");
                page = (Controls.MediaAddPage)field("addPage");
                Check(page != null && page.ShowingInstalled && !sidebar.Visible, "the add-software page did not open in the main window");
                Invoke(form, "CloseAddPage");
                Application.DoEvents();
                Check(field("addPage") == null && page.IsDisposed && sidebar.Visible && grid.Visible, "main content did not return after the add pages");
            }
        }

        private static void DatabaseFormFlow()
        {
            string path = Path.Combine(root, "database-form", "config.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            AppConfig c = new AppConfig();
            c.CustomCategories.Add("工具");
            c.ResourceCategories.Add("音乐");
            Check(c.SaveTo(path), "fixture export");
            c = ConfigDatabase.Load(path);
            Check(!c.LoadFailed, c.DatabaseError);
            for (int i = 0; i < DatabasePanel.PageRows; i++)
                c.AddedItems.Add(new AddedEntry { Path = "D:\\lib\\track" + i + ".mp3", Name = "track" + i, Category = "音乐", IsResource = true });
            Check(c.Save("phone.music.import"), c.DatabaseError);
            c.CustomCategories.Add("游戏");
            Check(c.Save("OnAddCategory"), c.DatabaseError);
            long newest = Convert.ToInt64(c.Database.History(1)[0][0]);
            int business = c.Database.HistoryPage(0, 1000, false).Count;
            // 扫描登记比业务操作更新，数量超过一页：既不能挡住撤销，也不能默认列出来
            using (Sqlite db = new Sqlite(c.Database.FilePath, false))
                for (int i = 0; i < DatabasePanel.PageOps + 5; i++)
                    db.Execute("INSERT INTO operations(utc,action,summary,actor,session,pid) VALUES(?,?,?,?,?,?)",
                        DateTime.UtcNow.ToString("o"), "catalog.discover", "new identities=1", "test", "test", 1);
            Check(c.Database.LatestBusinessOperation() == newest && c.Database.HistoryPage(0, 1000, false).Count == business
                && c.Database.HistoryPage(newest, 1000, false).Count == business - 1, "history paging or the undo candidate is wrong");

            Func<DatabasePanel, string, object> field = delegate(DatabasePanel dialog, string name)
            {
                return typeof(DatabasePanel).GetField(name, Private).GetValue(dialog);
            };
            Action<DatabasePanel> loaded = delegate(DatabasePanel dialog)
            {
                PumpUntil(delegate
                {
                    return (string)field(dialog, "placeholder") != "正在读取操作记录…" && (string)field(dialog, "detailText") != "正在读取明细…";
                }, 15000);
            };

            using (DatabasePanel dialog = new DatabasePanel(c.Database))
            using (Form panelHost = HostSettingsPanel(dialog))
            {
                int changed = 0;
                dialog.DataChanged += delegate { changed++; };
                loaded(dialog);
                ListBox ops = (ListBox)field(dialog, "opList"), rows = (ListBox)field(dialog, "changeList");
                Control undo = (Control)field(dialog, "undo"), notice = (Control)field(dialog, "notice");
                Check(ops.Items.Count == business && ops.Items[0].ToString() == "新建分类，改动 2 处" && (string)field(dialog, "headerSummary") == "共 " + business + " 次操作",
                    "business operations not listed newest first: " + ops.Items.Count + " / " + ops.Items[0]);
                Check(undo.Visible && notice.Text.StartsWith("可以撤销") && rows.Items.Count == 2 && rows.Items[0].ToString() == "新增 分类：游戏",
                    "latest operation not offered for undo: " + notice.Text + " / " + rows.Items.Count);

                // 改动很多的操作：先列一页，点「显示更多」接着列，选中新列出的第一行
                ops.SelectedIndex = 1;
                loaded(dialog);
                Check(ops.Items[1].ToString().StartsWith("导入手机音乐") && !undo.Visible && notice.Text.StartsWith("只能撤销最新的一次操作"),
                    "older operation offered for undo: " + ops.Items[1] + " / " + notice.Text);
                Check(rows.Items.Count == DatabasePanel.PageRows + 1 && rows.Items[rows.Items.Count - 1].ToString() == "显示更多"
                    && (int)field(dialog, "added") >= DatabasePanel.PageRows * 2, "long operation not paged: " + rows.Items.Count);
                MethodInfo more = typeof(DatabasePanel).GetMethod("LoadDetails", Private);
                more.Invoke(dialog, new object[] { true });
                PumpUntil(delegate { return rows.Items.Count != DatabasePanel.PageRows + 1; }, 15000);
                Check(rows.Items.Count >= DatabasePanel.PageRows * 2 && rows.SelectedIndex == DatabasePanel.PageRows && rows.Items[DatabasePanel.PageRows] is ChangeRow,
                    "show-more did not append the next page: " + rows.Items.Count + " / " + rows.SelectedIndex);
                HashSet<long> seen = new HashSet<long>();
                foreach (object item in rows.Items) if (item is ChangeRow) Check(seen.Add(((ChangeRow)item).Id), "a change is listed twice after paging");

                // 显示系统记录：一页 PageOps 条，末尾可继续往更早读；业务操作仍是唯一可撤销的那条
                typeof(Control).GetMethod("OnClick", Private).Invoke((Control)field(dialog, "filter"), new object[] { EventArgs.Empty });
                PumpUntil(delegate { return ops.Items.Count == DatabasePanel.PageOps + 1; }, 15000);
                loaded(dialog);
                Check(ops.Items.Count == DatabasePanel.PageOps + 1 && ops.Items[0].ToString() == "扫描登记新项目，登记 1 个新项目"
                    && ops.Items[DatabasePanel.PageOps].ToString() == "显示更早的记录" && ((Control)field(dialog, "filter")).Text == "隐藏系统自动记录",
                    "system records not paged: " + ops.Items.Count);
                Check(!undo.Visible && notice.Text.StartsWith("扫描时自动产生的记录"), "system record offered for undo");
                typeof(DatabasePanel).GetMethod("LoadHistory", Private).Invoke(dialog, new object[] { true });
                PumpUntil(delegate { return ops.Items.Count == DatabasePanel.PageOps + 5 + business; }, 15000);
                loaded(dialog);
                Check(ops.Items.Count == DatabasePanel.PageOps + 5 + business && ops.Items[ops.Items.Count - 1] is DatabasePanel.Op,
                    "earlier records not appended: " + ops.Items.Count);
                ops.SelectedIndex = DatabasePanel.PageOps + 5;
                loaded(dialog);
                Check(ops.SelectedItem.ToString() == "新建分类，改动 2 处" && undo.Visible, "scan records must not block undoing the latest business operation");

                typeof(DatabasePanel).GetMethod("StartUndo", Private).Invoke(dialog, new object[] { newest });
                Check(dialog.Busy && !((Control)field(dialog, "maintain")).Enabled && !ops.Enabled, "panel stays usable while the undo is running");
                PumpUntil(delegate { return !dialog.Busy; }, 15000);
                Check(changed == 1 && ((Control)field(dialog, "status")).Text.StartsWith("已撤销"), "successful undo was not reported in place");
                // 面板不关闭：撤销后按新内容重新读取，最新一条就是这次撤销
                // 此时还显示着系统记录，被撤销的那条不在第一页里，摘要只写「之前的一次操作」
                PumpUntil(delegate { return ops.Items.Count > 0 && ops.Items[0].ToString().StartsWith("撤销操作"); }, 15000);
                Check(ops.Items.Count > 0 && ops.Items[0].ToString().StartsWith("撤销操作"), "history was not reloaded after the undo: " + (ops.Items.Count > 0 ? ops.Items[0] : null));
            }
            AppConfig after = ConfigDatabase.Load(path);
            Check(!after.LoadFailed && !after.CustomCategories.Contains("游戏") && after.CustomCategories.Contains("工具")
                && after.AddedItems.Count == DatabasePanel.PageRows, "undo did not roll back exactly the latest operation");

            // 撤销本身是最新的一条：可以再撤销；被撤销的那条标为已撤销
            using (DatabasePanel dialog = new DatabasePanel(after.Database))
            using (Form panelHost = HostSettingsPanel(dialog))
            {
                loaded(dialog);
                ListBox ops = (ListBox)field(dialog, "opList");
                Check(ops.Items[0].ToString() == "撤销操作，撤销了「新建分类」" && ((Control)field(dialog, "undo")).Visible
                    && ((Control)field(dialog, "notice")).Text.StartsWith("这是一次撤销"), "undo record wrong: " + ops.Items[0]);
                Check(((DatabasePanel.Op)ops.Items[1]).Reverted && DatabasePanel.StatusText((DatabasePanel.Op)ops.Items[1], (long)field(dialog, "latestBusiness")) == "已撤销",
                    "undone operation not marked");
            }

            // 读取还没结束就释放面板（主窗口关闭）：后台线程不得再回调已释放的面板
            DatabasePanel early = new DatabasePanel(after.Database);
            using (Form panelHost = HostSettingsPanel(early, false))
            {
                early.Dispose();
                PumpUntil(delegate { return false; }, 300);
                Check(((IList)field(early, "ops")).Count == 0, "disposed panel still received the history");
            }

            // 换成另一个数据库对象（主窗口整体重载配置后）：清掉旧记录并按新对象重新读取
            using (DatabasePanel dialog = new DatabasePanel(null))
            using (Form panelHost = HostSettingsPanel(dialog))
            {
                Check((string)field(dialog, "placeholder") == "数据库不可用，无法读取操作记录。", "missing database is not explained");
                typeof(DatabasePanel).GetMethod("CheckDatabase", Private).Invoke(dialog, null);
                Check(((Control)field(dialog, "status")).Text.Contains("数据库不可用") && !dialog.Busy, "maintenance without a database did nothing visible");
                dialog.SetDatabase(after.Database);
                loaded(dialog);
                Check(((ListBox)field(dialog, "opList")).Items.Count > 0, "history was not read after the database was set");
            }
        }

        /// <summary>把设置页面板放进一个屏幕外的宿主窗口并显示（面板显示后才开始读取）。</summary>
        private static Form HostSettingsPanel(Control panel)
        {
            return HostSettingsPanel(panel, true);
        }

        /// <summary>pump 为 false 时显示后不处理消息：面板已发起读取，但结果还没交回界面线程。</summary>
        private static Form HostSettingsPanel(Control panel, bool pump)
        {
            DpiAwareForm host = new DpiAwareForm();
            host.ShowInTaskbar = false;
            host.FormBorderStyle = FormBorderStyle.None;
            host.StartPosition = FormStartPosition.Manual;
            host.Location = new Point(-32000, -32000);
            host.ClientSize = new Size(960, 600);
            panel.Bounds = new Rectangle(0, 0, 960, 600);
            host.Controls.Add(panel);
            host.Show();
            if (pump) Application.DoEvents();
            return host;
        }

        // 主窗口入口的完整链路：打开导入记录后直接关闭，不删任何东西；点「删除已入库项的源文件」后，
        // 由导入队列校验并删掉原件、入库副本保留、日志改为已移动。
        private static void ImportHistoryCleanupEndToEnd()
        {
            string dir = Path.Combine(root, "import-history-cleanup");
            Directory.CreateDirectory(dir);
            string source = Path.Combine(dir, "report.txt");
            File.WriteAllText(source, "history cleanup content");
            AppConfig config = new AppConfig
            {
                ManageRootPath = Path.Combine(dir, "manage"),
                ResourceStorePath = Path.Combine(dir, "store")
            };
            using (MainForm form = new MainForm(config))
            {
                // 导入记录现在嵌在设置页里：窗口要真的显示出来，面板才会显示并读取（放在屏幕外）
                typeof(MainForm).GetField("scanRunning", Private).SetValue(form, true);
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-3000, -3000);
                form.Show();
                Application.DoEvents();
                typeof(MainForm).GetField("contentMode", Private).SetValue(form, Controls.SidebarControl.ContentMode.Website);
                typeof(MainForm).GetField("currentCategory", Private).SetValue(form, "文档");
                MethodInfo queue = typeof(MainForm).GetMethod("QueueImport", Private, null, new[]
                {
                    typeof(string[]), typeof(ImportOperation), typeof(string), typeof(string), typeof(bool), typeof(uint),
                    typeof(Action<ImportBatch>), typeof(int), typeof(bool), typeof(bool), typeof(ImportDuplicateDecision), typeof(bool)
                }, null);
                queue.Invoke(form, new object[] { new[] { source }, ImportOperation.Copy, "文档", null, true, (uint)0,
                    null, 0, false, false, null, false });
                FieldInfo running = typeof(MainForm).GetField("importRunning", Private);
                PumpUntil(() => !(bool)running.GetValue(form));
                Check(config.AddedItems.Count == 1 && File.Exists(source), "fixture import failed");

                // 把这条记录改成“移入、已授权、原件还没删”：相当于上次删源没有完成
                string journal = Path.Combine(Paths.DataDir, "Imports"), xml = null;
                ImportBatch saved = null;
                foreach (string path in Directory.GetFiles(journal, "*.xml"))
                {
                    ImportBatch candidate;
                    try { candidate = FileImport.Load(path); }
                    catch (Exception) { continue; }   // 其它用例留下的损坏日志
                    if (candidate.Items.Count == 1 && string.Equals(candidate.Items[0].Source, source, StringComparison.OrdinalIgnoreCase))
                    {
                        saved = candidate;
                        xml = path;
                    }
                }
                Check(saved != null && saved.Committed && saved.Items[0].State == FileImport.Copied, "fixture journal not found");
                string target = saved.Items[0].Target;
                saved.Operation = ImportOperation.Move;
                saved.DeleteAuthorized = true;
                FileImport.Save(saved);
                File.Delete(Path.ChangeExtension(xml, ".complete"));

                // 打开导入记录（现在是设置页里的一个目录，不再弹窗口）、选中这条记录，再按 clean 决定点清理还是直接返回
                bool found = false, offered = false;
                ImportHistoryDialog panel = (ImportHistoryDialog)typeof(MainForm).GetField("importHistoryPanel", Private).GetValue(form);
                ListBox batches = (ListBox)typeof(ImportHistoryDialog).GetField("batchList", Private).GetValue(panel);
                Control retryButton = (Control)typeof(ImportHistoryDialog).GetField("retry", Private).GetValue(panel);
                Controls.SettingsPage settings = (Controls.SettingsPage)typeof(MainForm).GetField("settingsPage", Private).GetValue(form);
                Action<bool> open = delegate(bool clean)
                {
                    found = false;
                    offered = false;
                    Invoke(form, "ShowImportHistory");
                    Check(settings.Visible && settings.Section == Controls.SettingsPage.SettingsSection.ImportHistory
                        && panel.Visible && !panel.TopLevel && panel.Parent == settings,
                        "import history did not open as a section of the settings page");
                    PumpUntil(delegate { return batches.Items.Count > 0; }, 15000);
                    for (int i = 0; i < batches.Items.Count; i++)
                        if (((ImportHistoryDialog.Record)batches.Items[i]).Batch.Id == saved.Id)
                        {
                            batches.SelectedIndex = i;
                            found = true;
                        }
                    offered = found && retryButton.Visible;
                    if (clean && offered)
                    {
                        AnswerMessageDialog("导入记录", 1, delegate { typeof(ImportHistoryDialog).GetMethod("RequestRetry", Private).Invoke(panel, null); });
                        Check(panel.Visible && !panel.IsDisposed && !retryButton.Visible, "requesting cleanup closed the embedded panel or left the button");
                    }
                    PumpUntil(() => !(bool)running.GetValue(form));
                    if (clean)
                    {
                        // 清理结束后面板自己重新读取：这条记录的清理按钮不再出现
                        PumpUntil(delegate { return batches.Items.Count > 0 && !retryButton.Visible; }, 15000);
                        for (int i = 0; i < batches.Items.Count; i++)
                            if (((ImportHistoryDialog.Record)batches.Items[i]).Batch.Id == saved.Id) batches.SelectedIndex = i;
                        Check(!retryButton.Visible, "the panel still offers cleanup after it finished");
                    }
                    Invoke(form, "CloseSettings");
                    Check(!settings.Visible, "settings page stayed open");
                };

                open(false);
                Check(found && offered, "pending record not listed with a cleanup button");
                Check(File.Exists(source) && File.Exists(target) && FileImport.Load(xml).Items[0].State == FileImport.Copied,
                    "closing the history window touched the source or the journal");

                open(true);
                Check(found && offered, "pending record not offered for cleanup");
                ImportBatch after = FileImport.Load(xml);
                Check(!File.Exists(source) && File.Exists(target), "cleanup did not delete the source or lost the library copy");
                Check(after.Items[0].State == FileImport.Moved && string.IsNullOrEmpty(after.Items[0].Error) && after.Finished,
                    "journal not updated after cleanup: " + after.Items[0].State + " / " + after.Items[0].Error);
                Check(File.ReadAllText(target) == "history cleanup content" && config.AddedItems.Count == 1, "library copy or its record changed");

                open(false);
                Check(found && !offered, "finished record still offers cleanup");
            }
        }

        private static void LocalScanProgressLifetime()
        {
            bool reportedShown = false, closeBlocked = false, workerSawCancel = false, probed = false;
            string finalStatus = null;
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            using (LocalScanProgressDialog dialog = new LocalScanProgressDialog("回归", "准备", "…", cancel))
            using (System.Windows.Forms.Timer probe = new System.Windows.Forms.Timer { Interval = 10 })
            {
                Label status = (Label)typeof(LocalScanProgressDialog).GetField("statusLabel", Private).GetValue(dialog);
                Label detail = (Label)typeof(LocalScanProgressDialog).GetField("detailLabel", Private).GetValue(dialog);
                Controls.IosProgressBar bar = (Controls.IosProgressBar)typeof(LocalScanProgressDialog).GetField("progress", Private).GetValue(dialog);
                dialog.Shown += delegate
                {
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try
                        {
                            dialog.Report("识别 1 / 4", "D:\\a&b.mp3", 1, 4);
                            workerSawCancel = cancel.Token.WaitHandle.WaitOne(5000);
                            dialog.Report("迟到的进度", "late", 2, 4);
                        }
                        finally { dialog.Finish(); }
                    });
                };
                probe.Tick += delegate
                {
                    if (!dialog.Visible || status.Text != "识别 1 / 4") return;   // 等后台报告的进度显示出来
                    probe.Stop();
                    probed = true;
                    dialog.Opacity = 0;
                    reportedShown = detail.Text == "D:\\a&b.mp3" && !detail.UseMnemonic
                        && !bar.Marquee && bar.Maximum == 4 && bar.Value == 1;
                    dialog.Close();
                    closeBlocked = dialog.Visible && cancel.IsCancellationRequested;
                };
                probe.Start();
                ConfirmDialog.ShowModal(dialog);
                finalStatus = status.Text;
                Check(bar.Maximum == 4 && bar.Value == 1, "progress reported after cancel was applied");
            }
            Check(probed, "progress dialog was never shown");
            Check(reportedShown, "reported progress was not shown");
            Check(closeBlocked, "closing the window did not cancel and wait for the worker");
            Check(workerSawCancel, "worker never saw the cancel request");
            Check(finalStatus == "正在取消扫描…", "late progress replaced the cancelling status: " + finalStatus);

            using (Controls.IosProgressBar bar = new Controls.IosProgressBar())
            {
                FieldInfo timer = typeof(Controls.IosProgressBar).GetField("_marqueeTimer", Private);
                bar.Size = new Size(180, 10);
                bar.Marquee = true;
                Check(timer.GetValue(bar) != null, "marquee did not start its timer");
                using (Bitmap bitmap = new Bitmap(bar.Width, bar.Height))
                    bar.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bar.Size));
                bar.Marquee = false;
                Check(timer.GetValue(bar) == null, "marquee timer survived being switched off");
                bar.Marquee = true;
                bar.Dispose();
                Check(timer.GetValue(bar) == null, "marquee timer survived dispose");
            }
        }

        // 结果窗口：移入并清理原件是默认选项时，回车不触发导入；没有移入选项时回车等同主按钮；Esc 始终取消。
        private static void LocalScanResultKeyboard()
        {
            List<LocalScanStat> stats = new List<LocalScanStat> { new LocalScanStat("新增", "3 张"), new LocalScanStat("读取失败", "1 项", true) };
            Func<bool, Keys, LocalScanResultDecision> press = delegate(bool hasNewItems, Keys key)
            {
                bool sent = false;
                LocalScanResultDecision decision;
                using (System.Windows.Forms.Timer probe = new System.Windows.Forms.Timer { Interval = 10 })
                {
                    probe.Tick += delegate
                    {
                        Form dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(open => open.Modal);
                        if (dialog == null) return;
                        probe.Stop();
                        dialog.Opacity = 0;
                        sent = true;
                        dialog.GetType().GetMethod("ProcessCmdKey", Private).Invoke(dialog, new object[] { new Message(), key });
                        if (!dialog.IsDisposed && dialog.Visible) dialog.Close();
                    };
                    probe.Start();
                    decision = LocalScanResultDialog.Show("回归", "发现 3 张新照片", stats, "第一行\n第二行", "开始收录", true, hasNewItems);
                }
                Check(sent, "result dialog did not receive " + key);
                return decision;
            };
            Check(press(true, Keys.Enter).Action == LocalScanResultAction.Cancel, "enter started the move that deletes originals");
            LocalScanResultDecision reuse = press(false, Keys.Enter);
            Check(reuse.Action == LocalScanResultAction.Import && reuse.Operation == ImportOperation.Copy, "enter did not accept the non-destructive import");
            Check(press(true, Keys.Escape).Action == LocalScanResultAction.Cancel && press(false, Keys.Escape).Action == LocalScanResultAction.Cancel,
                "escape did not cancel the result dialog");
        }

        private static void LocalScanResultSummaries()
        {
            LocalMediaScanResult media = new LocalMediaScanResult();
            Check(MainForm.LocalMediaScanHeadline(media, "照片", "张") == "没有发现可收录的照片。", "empty media headline wrong");
            media.Add(2024, "old.jpg", 10, true, false);
            Check(MainForm.LocalMediaScanHeadline(media, "照片", "张") == "没有新照片；1 张已收录，可更新年份集合归属。", "reuse-only media headline wrong");
            media.Add(2024, "new.jpg", 1024 * 1024, false, false);
            media.CloudFiles = 2;
            Check(MainForm.LocalMediaScanHeadline(media, "照片", "张") == "发现 1 张新照片，预计复制 1.0 MB。",
                "media headline wrong: " + MainForm.LocalMediaScanHeadline(media, "照片", "张"));
            Func<List<LocalScanStat>, string> text = delegate(List<LocalScanStat> stats)
            {
                return string.Join("|", stats.ConvertAll(delegate(LocalScanStat stat) { return stat.Label + "=" + stat.Value + (stat.Warn ? "!" : ""); }).ToArray());
            };
            Check(text(MainForm.LocalMediaScanStats(media, "张")) == "新增=1 张|已收录=1 张|云端未下载=2 张|读取失败=0 项",
                "media numbers wrong: " + text(MainForm.LocalMediaScanStats(media, "张")));
            media.ReadFailures = 2;
            media.UnreadableDirectories = 1;
            Check(text(MainForm.LocalMediaScanStats(media, "张")).EndsWith("读取失败=3 项!"), "media failures are not highlighted");

            LocalMusicScanResult music = new LocalMusicScanResult();
            Check(MainForm.MusicScanHeadline(music) == "没有发现可导入的音乐。", "empty music headline wrong");
            music.UnconfirmedFiles.Add("maybe.mp3");
            Check(MainForm.MusicScanHeadline(music) == "没有确认是歌曲的文件，另有 1 个无法确认。", "unconfirmed-only music headline wrong");
            music.Files.Add("song.mp3");
            music.AcceptedBytes = 2048;
            music.Count(MusicFilterReason.AlreadyIncluded, "a.mp3");
            music.Count(MusicFilterReason.AlreadyIncludedPendingCleanup, "b.mp3");
            music.AddError("拒绝访问");
            Check(MainForm.MusicScanHeadline(music) == "发现 1 首可导入的音乐，预计复制 2.0 KB。", "music headline wrong: " + MainForm.MusicScanHeadline(music));
            Check(text(MainForm.MusicScanStats(music)) == "可导入=1 首|无法确认=1 个|已收录=2 首|读取失败=1 项!",
                "music numbers wrong: " + text(MainForm.MusicScanStats(music)));
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

        private static void SelectionFooterText()
        {
            AppInfo navicat = new AppInfo { Name = "Navicat Premium 17", Source = "Desktop",
                TargetPath = "D:\\Program Files\\PremiumSoft\\Navicat Premium 17\\navicat.exe" };
            Check(MainForm.SelectionNameText(navicat) == "Navicat Premium 17", "selection name was changed");
            Check(MainForm.SelectionLocationText(navicat) == navicat.TargetPath, "selection target path was lost");
            Check(MainForm.SelectionSourceText(navicat) == "桌面快捷方式", "desktop source was not labelled");

            AppInfo file = new AppInfo { Name = "report", TargetPath = "C:\\Docs\\report.pdf", IsFile = true };
            Check(MainForm.SelectionNameText(file) == "report.pdf", "file display name lost its extension");

            string web = "http" + "s" + ":" + "//example.com/docs";
            AppInfo url = new AppInfo { Name = "Docs", TargetPath = web, Source = "Added" };
            Check(MainForm.SelectionLocationText(url) == web, "url was not shown verbatim");
            Check(MainForm.SelectionSourceText(url) == "手动添加", "manual source was not labelled");

            AppInfo packaged = new AppInfo { Name = "Calculator", IsUwp = true, Source = "AppsFolder",
                TargetPath = "shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" };
            Check(MainForm.SelectionLocationText(packaged) == "打包应用 · Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
                "packaged app id was not shown");
            Check(MainForm.SelectionSourceText(packaged) == "开始菜单", "apps-folder source was not labelled");

            AppInfo installed = new AppInfo { Name = "Installed", IsUwp = true,
                TargetPath = "shell:AppsFolder\\Installed.App_123!App", InstallLocation = "C:\\Program Files\\WindowsApps\\Installed" };
            Check(MainForm.SelectionLocationText(installed) == installed.InstallLocation,
                "packaged install location took priority over the app id");

            AppInfo shortcutUwp = new AppInfo { Name = "Shortcut Package", IsUwp = true,
                TargetPath = "C:\\Windows\\explorer.exe", LaunchArguments = "shell:AppsFolder\\Shortcut.Package_456!App" };
            Check(MainForm.SelectionLocationText(shortcutUwp) == "打包应用 · Shortcut.Package_456!App",
                "start-menu packaged shortcut did not recover the app id");

            AppInfo installOnly = new AppInfo { Name = "InstallOnly", InstallLocation = "D:\\Apps\\InstallOnly" };
            Check(MainForm.SelectionLocationText(installOnly) == installOnly.InstallLocation,
                "install location fallback was ignored");
            Check(MainForm.SelectionLocationText(new AppInfo { Name = "Empty" }) == "—",
                "empty location did not use the placeholder");

            AppInfo start = new AppInfo { Name = "Start", Source = "StartMenu" };
            Check(MainForm.SelectionSourceText(start) == "开始菜单", "start-menu source was not labelled");
            AppInfo desktopFallback = new AppInfo { Name = "Desk", IsDesktop = true };
            Check(MainForm.SelectionSourceText(desktopFallback) == "桌面快捷方式", "desktop flag fallback was ignored");
            Check(MainForm.SelectionCountText(3) == "已选 3 项", "multi-selection text changed");
        }

        private static void GridSelectionNotifications()
        {
            using (Controls.AppGridControl grid = new Controls.AppGridControl { Size = new Size(960, 420) })
            {
                List<AppInfo> apps = new List<AppInfo>();
                for (int i = 0; i < 10000; i++)
                    apps.Add(new AppInfo { Key = "selection:" + i, Name = "Selection " + i });

                int events = 0;
                grid.SelectionChanged += delegate { events++; };
                grid.SetApps(apps);
                Check(events == 1 && grid.SelectedCount == 0, "SetApps did not publish the empty selection");

                MethodInfo selectSingle = typeof(Controls.AppGridControl).GetMethod("SelectSingle", Private);
                MethodInfo selectRange = typeof(Controls.AppGridControl).GetMethod("SelectRange", Private);
                MethodInfo toggle = typeof(Controls.AppGridControl).GetMethod("ToggleSelected", Private);
                selectSingle.Invoke(grid, new object[] { 0 });
                Check(events == 2 && grid.SelectedCount == 1 && ReferenceEquals(grid.SingleSelectedApp, apps[0]),
                    "single selection was not exposed");

                events = 0;
                selectRange.Invoke(grid, new object[] { 9999 });
                Check(events == 1 && grid.SelectedCount == 10000 && grid.SingleSelectedApp == null,
                    "range selection did not fire exactly once or expose the wrong count");

                events = 0;
                grid.SetApps(apps);
                Check(events == 1 && grid.SelectedCount == 10000,
                    "large selection was not restored after reloading the list");

                events = 0;
                grid.ClearSelection();
                Check(events == 1 && grid.SelectedCount == 0, "clear selection did not fire exactly once");

                events = 0;
                toggle.Invoke(grid, new object[] { 2 });
                Check(events == 1 && grid.SelectedCount == 1 && ReferenceEquals(grid.SingleSelectedApp, apps[2]),
                    "toggle selection did not publish the changed item");
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

        private static void RestoreAliasShortcutScan()
        {
            string dir = Path.Combine(root, "restore-alias-scan");
            string canonical = Path.Combine(dir, "微信.lnk");
            string alias = Path.Combine(dir, "微信_restored2_1.lnk");
            string target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
            Directory.CreateDirectory(dir);
            try
            {
                CreateShortcutWithArguments(canonical, target, "--scene=desktop");
                CreateShortcutWithArguments(alias, target, "--scene=restored");
                DateTime now = DateTime.UtcNow;
                File.SetLastWriteTimeUtc(canonical, now.AddMinutes(-2));
                File.SetLastWriteTimeUtc(alias, now);

                AppInfo resolved = Scanner.ResolveShortcut(alias, "Desktop");
                Check(resolved != null && resolved.Name == "微信", "resolved shortcut kept the generated restore suffix");

                Scanner backupScanner = new Scanner(new AppConfig());
                typeof(Scanner).GetField("nameIndex", Private).SetValue(backupScanner, new Dictionary<string, AppInfo>(StringComparer.Ordinal));
                typeof(Scanner).GetField("launchIndex", Private).SetValue(backupScanner, new Dictionary<string, AppInfo>(StringComparer.Ordinal));
                Dictionary<string, AppInfo> backupDict = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
                MethodInfo addShortcut = typeof(Scanner).GetMethod("AddShortcut", Private);
                addShortcut.Invoke(backupScanner, new object[] { backupDict, canonical, "Desktop", false });
                addShortcut.Invoke(backupScanner, new object[] { backupDict, alias, "Desktop", false });
                AppInfo merged = backupDict.Values.Single();
                Check(backupDict.Count == 1 && merged.Name == "微信", "restored alias did not merge into one software entry");
                Check(merged.LaunchArguments == "--scene=restored", "newer backup shortcut arguments were not retained");

                Scanner desktopScanner = new Scanner(new AppConfig());
                typeof(Scanner).GetField("nameIndex", Private).SetValue(desktopScanner, new Dictionary<string, AppInfo>(StringComparer.Ordinal));
                typeof(Scanner).GetField("launchIndex", Private).SetValue(desktopScanner, new Dictionary<string, AppInfo>(StringComparer.Ordinal));
                Dictionary<string, AppInfo> desktopDict = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
                addShortcut.Invoke(desktopScanner, new object[] { desktopDict, canonical, "Desktop", true });
                addShortcut.Invoke(desktopScanner, new object[] { desktopDict, alias, "Desktop", false });
                AppInfo desktopMerged = desktopDict.Values.Single();
                Check(desktopMerged.Name == "微信" && desktopMerged.LaunchArguments == "--scene=desktop",
                    "newer backup alias overrode the current desktop shortcut");
            }
            finally
            {
                try { File.Delete(canonical); } catch { }
                try { File.Delete(alias); } catch { }
            }
        }

        private static void CreateShortcutWithArguments(string path, string target, string arguments)
        {
            object shell = null, shortcut = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) throw new InvalidOperationException("WScript.Shell unavailable");
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
                shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { arguments });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            finally
            {
                try { if (shortcut != null) Marshal.FinalReleaseComObject(shortcut); } catch { }
                try { if (shell != null) Marshal.FinalReleaseComObject(shell); } catch { }
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

        private static void WideLayoutSpreads()
        {
            using (Form host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000), Size = new Size(1700, 700) })
            using (Controls.AppGridControl grid = new Controls.AppGridControl { Location = Point.Empty, Size = new Size(1600, 600) })
            using (Bitmap bitmap = new Bitmap(8, 8))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                host.Controls.Add(grid);
                host.Show();
                int dpi = DpiHelper.ForControl(grid);

                // 网格：任何宽度下卡片都铺满整行（左右边距与列间距相等），命中判定与绘制位置一致
                grid.GridMode = true;
                List<AppInfo> apps = new List<AppInfo>();
                for (int i = 0; i < 60; i++) apps.Add(new AppInfo { Key = "af:wide-" + i, Name = "Wide " + i });
                grid.SetApps(apps);
                MethodInfo rect = typeof(Controls.AppGridControl).GetMethod("GridRect", Private);
                MethodInfo hit = typeof(Controls.AppGridControl).GetMethod("HitIndex", Private);
                MethodInfo columnCount = typeof(Controls.AppGridControl).GetMethod("Columns", Private);
                foreach (int width in new int[] { 1600, 1301, 777, 420 })
                {
                    grid.Size = new Size(width, 600);
                    int columns = (int)columnCount.Invoke(grid, null);
                    Rectangle first = (Rectangle)rect.Invoke(grid, new object[] { 0 });
                    Rectangle last = (Rectangle)rect.Invoke(grid, new object[] { columns - 1 });
                    int left = first.X, right = grid.ClientSize.Width - last.Right;
                    Check(right >= left && right - left <= columns,
                        "cards do not span the row at width " + width + ": left=" + left + " right=" + right);
                    Check(right < first.Width, "row leaves room for another card at width " + width);
                    for (int column = 0; column < columns; column++)
                    {
                        Rectangle card = (Rectangle)rect.Invoke(grid, new object[] { column });
                        if (column > 0)
                        {
                            Rectangle previous = (Rectangle)rect.Invoke(grid, new object[] { column - 1 });
                            Check(card.X - previous.Right == left, "uneven card gap at width " + width + " column " + column);
                        }
                        foreach (int x in new int[] { card.X, card.X + card.Width / 2, card.Right - 1 })
                            Check((int)hit.Invoke(grid, new object[] { new Point(x, card.Y + card.Height / 2) }) == column,
                                "hit test disagrees with card position at width " + width + " column " + column + " x=" + x);
                    }
                    Check((int)hit.Invoke(grid, new object[] { new Point(first.X - 1, first.Y + first.Height / 2) }) == -1,
                        "left margin was hit as a card at width " + width);
                }

                // 列表：宽行各列间距相等且大于基础间距，名称列不再独占余量；窄行保持原有紧凑排法
                grid.GridMode = false;
                grid.ShowTimeColumn = true;
                List<AppInfo> rows = new List<AppInfo>();
                for (int i = 0; i < 20; i++)
                    rows.Add(new AppInfo { Key = "add:wide-" + i, Name = "Row " + i, Category = "照片", IsFile = true, TargetPath = Path.Combine(root, "wide-" + i + ".jpg") });
                grid.SetApps(rows);
                MethodInfo listColumns = typeof(Controls.AppGridControl).GetMethod("ListColumns", Private);
                int baseGap = DpiHelper.Scale(18, dpi);
                foreach (int logicalWidth in new int[] { 1600, 700 })
                {
                    int width = DpiHelper.Scale(logicalWidth, dpi);   // 列宽常量随 DPI 放大，测试宽度同步放大
                    grid.Size = new Size(width, 600);
                    Rectangle row = new Rectangle(DpiHelper.Scale(14, dpi), 0, grid.ClientSize.Width - DpiHelper.Scale(28, dpi), DpiHelper.Scale(30, dpi));
                    object[] args = { graphics, row, 0, null, null, null, null, null };
                    listColumns.Invoke(grid, args);
                    Rectangle name = (Rectangle)args[3], category = (Rectangle)args[4], size = (Rectangle)args[5],
                        time = (Rectangle)args[6], location = (Rectangle)args[7];
                    int[] gaps = { category.X - name.Right, size.X - category.Right, time.X - size.Right, location.X - time.Right };
                    Check(gaps[1] == gaps[2] && gaps[2] == gaps[3],
                        "list column gaps differ at width " + width + ": " + string.Join(",", Array.ConvertAll(gaps, delegate(int gap) { return gap.ToString(); })));
                    // 名称文字始终可以写到分类列前：宽行里长名称不能在旁边留着空白时被截断
                    Check(gaps[0] == baseGap, "name column stops short of the category column at width " + width + ": " + gaps[0]);
                    Check(location.Right == row.Right - 12, "location column left the right edge at width " + width);
                    Check(name.Width >= DpiHelper.Scale(76, dpi), "name column too narrow at width " + width);
                    if (logicalWidth == 1600)
                    {
                        Check(gaps[1] > baseGap * 3, "wide row kept the compact gaps: " + gaps[1]);
                        Check(name.Width >= (int)(row.Width * 0.36f), "wide row gives the name column less than its comfortable width: " + name.Width + " of " + row.Width);
                    }
                    else Check(gaps[1] == baseGap, "narrow row changed its column gaps: " + gaps[1]);
                }
                host.Hide();
            }
        }

        private static void ToolbarCountAndPlayerWidth()
        {
            AppConfig config = RenameConfig("toolbar-count");
            config.AutoScan = false;
            using (MainForm form = new MainForm(config))
            {
                typeof(MainForm).GetField("scanRunning", Private).SetValue(form, true);
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-3000, -3000);
                form.Show();
                Application.DoEvents();
                int dpi = form.LayoutDpi;
                Label count = (Label)typeof(MainForm).GetField("countLabel", Private).GetValue(form);
                Control mini = (Control)typeof(MainForm).GetField("miniMusic", Private).GetValue(form);
                Control search = (Control)typeof(MainForm).GetField("search", Private).GetValue(form);
                int miniMin = DpiHelper.Scale(190, dpi), miniMax = DpiHelper.Scale(960, dpi);
                Invoke(form, "UpdateCount", 3, 9);
                bool fitsWithShortCount = mini.Visible;
                foreach (int[] numbers in new int[][] { new int[] { 200, 216 }, new int[] { 123456, 1234567 }, new int[] { 0, 0 } })
                {
                    Invoke(form, "UpdateCount", numbers[0], numbers[1]);
                    int needed = TextRenderer.MeasureText(count.Text, count.Font).Width;
                    // 计数优先完整显示；唯一例外是播放器已让到最小宽度（再让就会被隐藏并暂停音乐）
                    Check(count.Width >= needed || (mini.Visible && mini.Width == miniMin),
                        "count '" + count.Text + "' does not fit on one line: width=" + count.Width + " text=" + needed + " mini=" + mini.Width);
                    Check(!fitsWithShortCount || mini.Visible, "a longer count hid the mini player: '" + count.Text + "'");
                    Check(!mini.Visible || mini.Right <= count.Left, "mini player overlaps the count '" + count.Text + "'");
                    Check(search.Right <= mini.Left, "search box overlaps the mini player");
                }
                // 宽窗口：播放器要么到达上限，要么一直排到计数文字跟前，不在中间留空档
                form.Size = new Size(Math.Min(2400, Screen.PrimaryScreen.WorkingArea.Width), form.Height);
                Application.DoEvents();
                Check(mini.Visible, "mini player hidden in a wide window");
                Check(mini.Width <= miniMax, "mini player exceeds its maximum width");
                foreach (string text in new string[] { "200 / 216", "扫描失败，已保留当前列表", "0 / 0" })
                {
                    count.Text = text;   // 直接赋值（扫描失败提示走这条路径）也必须重排
                    int needed = TextRenderer.MeasureText(count.Text, count.Font).Width;
                    Check(count.Width >= needed, "'" + text + "' does not fit in a wide toolbar: width=" + count.Width + " text=" + needed);
                    int gap = count.Right - needed - mini.Right;
                    Check(mini.Width == miniMax || gap <= DpiHelper.Scale(68, dpi),
                        "wide toolbar leaves a gap of " + gap + " before '" + text + "': mini=" + mini.Bounds + " count=" + count.Bounds);
                }
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

        private static void GridPaintFailureFallback()
        {
            using (Controls.AppGridControl grid = new Controls.AppGridControl { Size = new Size(640, 360), GridMode = false })
            {
                grid.CreateControl();
                List<AppInfo> apps = (List<AppInfo>)typeof(Controls.AppGridControl)
                    .GetField("apps", Private).GetValue(grid);
                apps.Add(null);
                using (Bitmap bitmap = new Bitmap(grid.Width, grid.Height))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    try
                    {
                        typeof(Controls.AppGridControl).GetMethod("OnPaint", Private, null,
                            new Type[] { typeof(PaintEventArgs) }, null)
                            .Invoke(grid, new object[] { new PaintEventArgs(graphics, new Rectangle(Point.Empty, grid.Size)) });
                    }
                    catch (TargetInvocationException ex)
                    {
                        throw ex.InnerException ?? ex;
                    }
                }
                Check((bool)typeof(Controls.AppGridControl).GetField("paintFailureLogged", Private).GetValue(grid),
                    "paint failure was not handled by the fallback");
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

        private static void NewerShortcutKeepsIdentity()
        {
            Scanner scanner = new Scanner(new AppConfig());
            typeof(Scanner).GetField("nameIndex", Private).SetValue(scanner, new Dictionary<string, AppInfo>(StringComparer.Ordinal));
            typeof(Scanner).GetField("launchIndex", Private).SetValue(scanner, new Dictionary<string, AppInfo>(StringComparer.Ordinal));
            FieldInfo timestamp = typeof(AppInfo).GetField("ShortcutLastWriteTimeUtc", Private);
            FieldInfo currentDesktop = typeof(AppInfo).GetField("IsCurrentDesktopShortcut", Private);
            Action<AppInfo, DateTime, bool> mark = delegate(AppInfo app, DateTime time, bool onDesktop)
            {
                if (timestamp != null) timestamp.SetValue(app, time);
                if (currentDesktop != null) currentDesktop.SetValue(app, onDesktop);
            };
            Dictionary<string, AppInfo> dict = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
            MethodInfo byName = typeof(Scanner).GetMethod("MergeByName", Private);
            DateTime now = DateTime.UtcNow;
            AppInfo oldBackup = new AppInfo { Key = "lnk:old-backup", Name = "Update Tool", ShortcutPath = "D:\\Backup\\Update Tool.lnk",
                TargetPath = "D:\\App\\old.exe", IsDesktop = true };
            string oldShortcutPath = oldBackup.ShortcutPath;
            AppInfo newDesktop = new AppInfo { Key = "lnk:new-desktop", Name = "Update Tool", ShortcutPath = "D:\\Desktop\\Update Tool.lnk",
                TargetPath = "D:\\App\\new.exe", IsDesktop = true };
            mark(oldBackup, now.AddHours(-2), false);
            mark(newDesktop, now.AddHours(-1), true);
            byName.Invoke(scanner, new object[] { dict, oldBackup });
            byName.Invoke(scanner, new object[] { dict, newDesktop });
            AppInfo selected = dict.Values.Single();
            Check(dict.Count == 1 && selected.Key == oldBackup.Key, "new shortcut replaced the persistent identity");
            Check(selected.TargetPath == newDesktop.TargetPath && selected.ShortcutPath == newDesktop.ShortcutPath,
                "desktop shortcut did not replace the old launch target");
            Check(dict.ContainsKey(newDesktop.ShortcutPath) && !dict.ContainsKey(oldShortcutPath),
                "scanner index kept the old shortcut path: keys=" + string.Join(" | ", new List<string>(dict.Keys)));

            AppInfo staleDesktop = new AppInfo { Key = "lnk:stale-desktop", Name = "Update Tool", ShortcutPath = "D:\\Desktop2\\Update Tool.lnk",
                TargetPath = "D:\\App\\stale.exe", IsDesktop = true };
            mark(staleDesktop, now.AddHours(-3), true);
            byName.Invoke(scanner, new object[] { dict, staleDesktop });
            Check(selected.TargetPath == newDesktop.TargetPath, "older desktop shortcut replaced the newer current entry");

            AppInfo backupFirst = new AppInfo { Key = "lnk:backup-first", Name = "Backup Tool", ShortcutPath = "D:\\Backup\\Backup Tool.lnk",
                TargetPath = "D:\\App\\backup-old.exe", IsDesktop = true };
            AppInfo backupNew = new AppInfo { Key = "lnk:backup-new", Name = "Backup Tool", ShortcutPath = "D:\\Backup\\Backup Tool_1.lnk",
                TargetPath = "D:\\App\\backup-new.exe", IsDesktop = true };
            mark(backupFirst, now.AddHours(-2), false);
            mark(backupNew, now, false);
            byName.Invoke(scanner, new object[] { dict, backupFirst });
            byName.Invoke(scanner, new object[] { dict, backupNew });
            AppInfo backupSelected = dict.Values.Single(app => app.Name == "Backup Tool");
            Check(backupSelected.Key == backupFirst.Key && backupSelected.TargetPath == backupNew.TargetPath,
                "newer backup shortcut did not win while preserving identity");

            Scanner reverseScanner = new Scanner(new AppConfig());
            typeof(Scanner).GetField("nameIndex", Private).SetValue(reverseScanner, new Dictionary<string, AppInfo>(StringComparer.Ordinal));
            typeof(Scanner).GetField("launchIndex", Private).SetValue(reverseScanner, new Dictionary<string, AppInfo>(StringComparer.Ordinal));
            Dictionary<string, AppInfo> reverseDict = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
            AppInfo desktopFirst = new AppInfo { Key = "lnk:new-desktop", Name = "Update Tool", ShortcutPath = "D:\\Desktop\\Update Tool.lnk",
                TargetPath = "D:\\App\\new.exe", IsDesktop = true, IsCurrentDesktopShortcut = true, ShortcutLastWriteTimeUtc = now };
            AppInfo backupLater = new AppInfo { Key = "lnk:old-backup", Name = "Update Tool", ShortcutPath = "D:\\Backup\\Update Tool.lnk",
                TargetPath = "D:\\App\\old.exe", IsDesktop = true, ShortcutLastWriteTimeUtc = now.AddHours(-2) };
            byName.Invoke(reverseScanner, new object[] { reverseDict, desktopFirst });
            byName.Invoke(reverseScanner, new object[] { reverseDict, backupLater });
            AppInfo reverseSelected = reverseDict.Values.Single();
            Check(reverseSelected.Key == backupLater.Key && reverseSelected.TargetPath == desktopFirst.TargetPath
                && reverseSelected.ShortcutPath == desktopFirst.ShortcutPath,
                "desktop-first scan did not preserve the backup identity and desktop launch target");
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

        private static void MissingResourceDelete()
        {
            AppConfig config = RenameConfig("missing-resource-delete");
            AppInfo first = AddEntry(config, "文档",
                Path.Combine(config.ResourceStorePath, "文档", "gone-1.txt"), false, true, "gone-1");
            AppInfo second = AddEntry(config, "文档",
                Path.Combine(config.ResourceStorePath, "文档", "gone-2.txt"), false, true, "gone-2");
            File.Delete(first.TargetPath);
            File.Delete(second.TargetPath);

            using (MainForm form = ResourceForm(config, "文档", first, second))
            {
                List<MenuWindow.MenuItem> items = (List<MenuWindow.MenuItem>)Invoke(form, "BuildContextMenu", first,
                    new List<AppInfo> { first, second });
                MenuWindow.MenuItem delete = FindMenuItem(items, "删除");
                Check(delete != null && !delete.Disabled, "missing resources did not offer delete");
                ClickConfirmation(delegate { delete.Action(); }, true);
                Check(config.AddedItems.ByPath(first.TargetPath).Count == 0
                    && config.AddedItems.ByPath(second.TargetPath).Count == 0,
                    "missing resource delete kept the stale records");
            }
        }

        private static void SearchScopeFollowsCategory()
        {
            AppConfig config = RenameConfig("search-scope");
            config.AutoScan = false;
            AppInfo photo = AddEntry(config, "照片", Path.Combine(config.ResourceStorePath, "trip-photo.jpg"), false, true, "trip photo");
            AppInfo music = AddEntry(config, "音乐", Path.Combine(config.ResourceStorePath, "trip-music.mp3"), false, true, "trip music");
            AppInfo rootMember = AddEntry(config, "照片", Path.Combine(config.ResourceStorePath, "trip-root.jpg"), false, true, "trip root");
            AppInfo childMember = AddEntry(config, "照片", Path.Combine(config.ResourceStorePath, "trip-child.jpg"), false, true, "trip child");
            AppInfo hidden = AddEntry(config, "照片", Path.Combine(config.ResourceStorePath, "trip-hidden.jpg"), false, true, "trip hidden");
            AppInfo software = AddEntry(config, "开发工具", Path.Combine(config.ManageRootPath, "trip-tool.exe"), false, false, "trip tool");
            string rootKey = CollectionKeys.Make("照片", "旅行");
            string childKey = rootKey + "/2024";
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "旅行" });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "2024", ParentCollection = rootKey });
            config.ItemCategories[rootMember.Key] = new List<string> { rootKey };
            config.ItemCategories[childMember.Key] = new List<string> { childKey };
            config.Favorites.Add(photo.Key);
            config.Hidden.Add(hidden.Key);
            photo.IsFavorite = true;
            hidden.IsHidden = true;
            software.IsDesktop = true;
            Check(config.Save(), config.DatabaseError);
            AppInfo[] apps = { photo, music, rootMember, childMember, hidden, software };

            using (MainForm form = SearchForm(config, Controls.SidebarControl.ContentMode.Website, "照片", apps))
            {
                Controls.SearchField search = (Controls.SearchField)typeof(MainForm).GetField("search", Private).GetValue(form);
                Invoke(form, "RefreshGrid");
                Check(search.Placeholder == "在「照片」中搜索…", "category search placeholder missing: " + search.Placeholder);
                search.Text = "trip";
                Invoke(form, "RefreshGrid");
                List<AppInfo> shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                Check(shown.Count == 3 && shown.All(a => a.Category == "照片"),
                    "database category search leaked outside the selected category: " + shown.Count);
                Check(!shown.Any(a => a.Key == hidden.Key), "database category search ignored the hidden setting");

                search.Text = "照片";
                Invoke(form, "RefreshGrid");
                shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                Check(shown.Count == 3, "database category-name search missed scoped members: " + shown.Count);
                search.Text = "++";
                Invoke(form, "RefreshGrid");
                shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                int total = (int)typeof(MainForm).GetField("categoryTotal", Private).GetValue(form);
                Check(shown.Count == 0 && total == 0, "database punctuation-only search returned rows: " + shown.Count + "/" + total);
                search.Text = "trip";

                typeof(MainForm).GetField("databasePageNumber", Private).SetValue(form, 1);
                typeof(MainForm).GetField("currentCategory", Private).SetValue(form, "音乐");
                Invoke(form, "RefreshGrid");
                shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                Check(shown.Count == 1 && shown[0].Key == music.Key, "switching category kept the previous search scope");
                Check((int)typeof(MainForm).GetField("databasePageNumber", Private).GetValue(form) == 0,
                    "switching category reused a stale search cursor");

                typeof(MainForm).GetField("currentCategory", Private).SetValue(form, "照片");
                typeof(MainForm).GetField("currentCollection", Private).SetValue(form, rootKey);
                typeof(MainForm).GetField("databasePageNumber", Private).SetValue(form, 1);
                Invoke(form, "RefreshGrid");
                shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                Check(shown.Count == 1 && shown[0].Key == rootMember.Key, "database collection search included a descendant or sibling");
                Check((int)typeof(MainForm).GetField("databasePageNumber", Private).GetValue(form) == 0,
                    "switching collection reused a stale search cursor");
                search.Text = "旅行";
                Invoke(form, "RefreshGrid");
                shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                Check(shown.Count == 1 && shown[0].Key == rootMember.Key, "database collection-name search missed its direct member");

                typeof(MainForm).GetField("currentCollection", Private).SetValue(form, childKey);
                search.Text = "2024";
                Invoke(form, "RefreshGrid");
                shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                Check(shown.Count == 1 && shown[0].Key == childMember.Key, "database nested collection-name search lost its direct member");
                search.Text = "trip";

                typeof(MainForm).GetField("currentCollection", Private).SetValue(form, null);
                typeof(MainForm).GetField("currentCategory", Private).SetValue(form, "全部资源");
                Invoke(form, "RefreshGrid");
                shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                Check(shown.Count == 4, "database all-resources search changed: " + shown.Count);

                typeof(MainForm).GetField("currentCategory", Private).SetValue(form, "常用");
                Invoke(form, "RefreshGrid");
                shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                Check(shown.Count == 1 && shown[0].Key == photo.Key, "database favorites search did not use the favorite projection");

                typeof(MainForm).GetField("currentCategory", Private).SetValue(form, "开发工具");
                typeof(MainForm).GetField("contentMode", Private).SetValue(form, Controls.SidebarControl.ContentMode.Software);
                Invoke(form, "RefreshGrid");
                shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                Check(shown.Count == 1 && shown[0].Key == software.Key, "database software search mixed in resources");

                typeof(MainForm).GetField("currentCategory", Private).SetValue(form, "照片");
                typeof(MainForm).GetField("contentMode", Private).SetValue(form, Controls.SidebarControl.ContentMode.Website);
                typeof(MainForm).GetField("databasePageNumber", Private).SetValue(form, 1);
                config.ShowHidden = true;
                Invoke(form, "RefreshGrid");
                shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                Check(shown.Count == 4 && shown.Any(a => a.Key == hidden.Key), "showHidden did not affect scoped search");
                Check((int)typeof(MainForm).GetField("databasePageNumber", Private).GetValue(form) == 0,
                    "showHidden reused a stale search cursor");
                config.ShowHidden = false;
                search.Text = string.Empty;
                Invoke(form, "RefreshGrid");
                shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                Check(shown.Count == 3, "clearing search did not restore the full current category");
            }

            ConfigDatabase database = config.Database;
            config.Database = null;
            try
            {
                using (MainForm form = SearchForm(config, Controls.SidebarControl.ContentMode.Website, "照片", apps))
                {
                    Controls.SearchField search = (Controls.SearchField)typeof(MainForm).GetField("search", Private).GetValue(form);
                    search.Text = "trip";
                    Invoke(form, "RefreshGrid");
                    List<AppInfo> shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                    int total = (int)typeof(MainForm).GetField("categoryTotal", Private).GetValue(form);
                    Check(shown.Count == 3 && total == shown.Count, "fallback category search kept a global total: " + shown.Count + "/" + total);
                    Check(shown.All(a => a.Category == "照片") && !shown.Any(a => a.Key == hidden.Key),
                        "fallback category search leaked or ignored hidden state");

                    search.Text = "照片";
                    Invoke(form, "RefreshGrid");
                    shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                    Check(shown.Count == 3, "fallback category-name search missed scoped members: " + shown.Count);
                    search.Text = "++";
                    Invoke(form, "RefreshGrid");
                    shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                    total = (int)typeof(MainForm).GetField("categoryTotal", Private).GetValue(form);
                    Check(shown.Count == 0 && total == 0, "fallback punctuation-only search returned rows: " + shown.Count + "/" + total);
                    search.Text = "trip";

                    typeof(MainForm).GetField("currentCollection", Private).SetValue(form, rootKey);
                    Invoke(form, "RefreshGrid");
                    shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                    Check(shown.Count == 1 && shown[0].Key == rootMember.Key, "fallback collection search included a descendant");
                    search.Text = "旅行";
                    Invoke(form, "RefreshGrid");
                    shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                    Check(shown.Count == 1 && shown[0].Key == rootMember.Key, "fallback collection-name search missed its direct member");

                    typeof(MainForm).GetField("currentCollection", Private).SetValue(form, childKey);
                    search.Text = "2024";
                    Invoke(form, "RefreshGrid");
                    shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                    Check(shown.Count == 1 && shown[0].Key == childMember.Key, "fallback nested collection-name search lost its direct member");
                    search.Text = "trip";

                    typeof(MainForm).GetField("currentCollection", Private).SetValue(form, null);
                    typeof(MainForm).GetField("currentCategory", Private).SetValue(form, "全部资源");
                    Invoke(form, "RefreshGrid");
                    shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                    Check(shown.Count == 4, "fallback all-resources search changed: " + shown.Count);

                    typeof(MainForm).GetField("currentCategory", Private).SetValue(form, "常用");
                    Invoke(form, "RefreshGrid");
                    shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                    Check(shown.Count == 1 && shown[0].Key == photo.Key, "fallback favorites search changed");

                    typeof(MainForm).GetField("currentCategory", Private).SetValue(form, "快捷中心");
                    typeof(MainForm).GetField("contentMode", Private).SetValue(form, Controls.SidebarControl.ContentMode.Software);
                    Invoke(form, "RefreshGrid");
                    shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                    Check(shown.Count == 1 && shown[0].Key == software.Key, "fallback quick-center search changed");
                }
            }
            finally { config.Database = database; }
        }

        private static void SearchFieldClearButton()
        {
            using (DpiAwareForm host = new DpiAwareForm())
            using (Controls.SearchField search = new Controls.SearchField())
            using (TextBox other = new TextBox())
            {
                host.ShowInTaskbar = false;
                host.FormBorderStyle = FormBorderStyle.None;
                host.StartPosition = FormStartPosition.Manual;
                host.Location = new Point(-32000, -32000);
                host.Size = new Size(320, 80);
                search.SetBounds(12, 12, 240, 34);
                other.SetBounds(12, 50, 100, 23);
                host.Controls.Add(other);
                host.Controls.Add(search);
                Control clear = (Control)typeof(Controls.SearchField).GetField("clearButton", Private).GetValue(search);
                Control placeholder = (Control)typeof(Controls.SearchField).GetField("placeholderLabel", Private).GetValue(search);
                TextBox input = (TextBox)typeof(Controls.SearchField).GetField("box", Private).GetValue(search);
                Check(clear.AccessibleName == "清空搜索", "clear button accessible name changed");
                Check(clear.AccessibleRole == AccessibleRole.PushButton, "clear button accessible role changed");
                Check(!placeholder.AutoSize, "placeholder label must use explicit bounds for vertical alignment");
                Check(!clear.Visible, "clear button visible without input");
                host.Show();
                try
                {
                    host.Activate();
                    other.Focus();
                    Application.DoEvents();
                    foreach (int dpi in new int[] { 120, 144, 168, 96 })
                    {
                        SetDpi(host, dpi, host.Bounds);
                        Check(placeholder.Top == 0 && placeholder.Height == search.Height,
                            "placeholder height diverged at " + dpi + " DPI: " + placeholder.Bounds + " vs " + search.ClientSize);
                        Check(placeholder.Left == input.Left && placeholder.Width == input.Width,
                            "placeholder and input bounds diverged at " + dpi + " DPI");
                        Check(input.Top == (search.Height - input.PreferredHeight) / 2,
                            "input was not vertically centered at " + dpi + " DPI");
                        Check(clear.Left >= 0 && clear.Top >= 0 && clear.Right <= search.Width && clear.Bottom <= search.Height,
                            "clear button escaped the search field at " + dpi + " DPI: " + clear.Bounds);
                    }
                    // 最窄搜索框（200px）放不下完整占位文案时仍须单行垂直居中：原生 Label 会折成两行，首行被挤到上沿。
                    search.Width = 200;
                    search.Placeholder = "在「我的工具」中搜索…";
                    Application.DoEvents();
                    Check(TextRenderer.MeasureText(search.Placeholder, placeholder.Font).Width > placeholder.Width,
                        "fixture placeholder fits the narrow field: " + placeholder.Bounds);
                    CheckPlaceholderCentered(search, placeholder, "narrow field");
                    search.Width = 320;
                    Application.DoEvents();
                    CheckPlaceholderCentered(search, placeholder, "wide field");
                    Check(other.Focused, "test host did not receive focus");
                    search.Text = "idea";
                    Check(clear.Visible, "clear button hidden with input");
                    typeof(Control).GetMethod("OnClick", Private).Invoke(clear, new object[] { EventArgs.Empty });
                    Application.DoEvents();
                    Check(search.Text.Length == 0 && !clear.Visible, "clear button did not clear and hide");
                    Check(search.ContainsFocus, "search input did not regain focus after clear");
                }
                finally
                {
                    host.Close();
                }
            }
        }

        /// <summary>截取搜索框，检查占位文案字迹的上下边界相对输入框垂直居中。</summary>
        private static void CheckPlaceholderCentered(Control search, Control placeholder, string label)
        {
            Check(placeholder.Visible && placeholder.Top == 0 && placeholder.Height == search.Height,
                "placeholder hidden or not spanning the field (" + label + "): " + placeholder.Bounds);
            using (Bitmap shot = new Bitmap(placeholder.Width, placeholder.Height))
            {
                placeholder.DrawToBitmap(shot, new Rectangle(Point.Empty, shot.Size));
                int top = -1, bottom = -1;
                for (int y = 3; y < shot.Height - 3; y++)
                {
                    for (int x = 0; x < shot.Width; x++)
                    {
                        Color c = shot.GetPixel(x, y);
                        if (c.R + c.G + c.B >= 600) continue;   // 背景与边框都更亮，只统计灰色字迹
                        if (top < 0) top = y;
                        bottom = y;
                        break;
                    }
                }
                Check(top >= 0, "placeholder text not rendered (" + label + ")");
                // 折行时两行字迹合起来也近似居中，必须先确认只有一行
                Check(bottom - top + 1 <= placeholder.Font.Height, "placeholder text wrapped (" + label + "): ink rows "
                    + top + "-" + bottom + ", line height " + placeholder.Font.Height);
                int offset = (top + bottom + 1) - shot.Height;   // 字迹中心与框中心之差的两倍
                Check(Math.Abs(offset) <= 4, "placeholder text off-center (" + label + "): ink rows "
                    + top + "-" + bottom + " in height " + shot.Height);
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

        private static void DragGhostLifecycle()
        {
            using (DpiAwareForm owner = new DpiAwareForm())
            {
                Initialize(owner);
                using (Controls.DragGhostController controller = new Controls.DragGhostController(owner))
                {
                    Bitmap large = new Bitmap(56, 56);
                    Bitmap small = new Bitmap(16, 16);
                    using (Graphics g = Graphics.FromImage(large)) g.Clear(Color.RoyalBlue);
                    using (Graphics g = Graphics.FromImage(small)) g.Clear(Color.OrangeRed);
                    AppInfo app = new AppInfo { Name = "Alpha", IsFile = true, Icon = large, SmallIcon = small };
                    List<AppInfo> one = new List<AppInfo> { app };

                    Controls.DragGhostSession gridSession = controller.Begin(one, false, new Point(100, 100));
                    Check(gridSession != null, "grid drag ghost did not start");
                    Check(controller.HasVisibleSession, "grid drag ghost is not visible");
                    Check(!ReferenceEquals(gridSession.Icon, large), "drag ghost retained the source icon");
                    Check(gridSession.Icon.Width == 56, "grid drag ghost did not clone the large icon");
                    controller.Move(gridSession, new Point(140, 130));
                    controller.SetState(gridSession, Controls.DragDropIntent.Accept);
                    Check(gridSession.State == Controls.DragDropIntent.Accept, "valid state was not applied");

                    // 模拟后台图标刷新：源图释放后，拖影仍应使用自己的快照完成重绘。
                    large.Dispose();
                    small.Dispose();
                    app.Icon = null;
                    app.SmallIcon = null;
                    controller.Move(gridSession, new Point(180, 160));
                    controller.SetState(gridSession, Controls.DragDropIntent.Reject);
                    Check(gridSession.State == Controls.DragDropIntent.Reject, "invalid state was not applied");

                    int firstId = gridSession.Id;
                    List<AppInfo> many = new List<AppInfo>();
                    for (int i = 0; i < 10; i++) many.Add(new AppInfo { Name = "Item" + i, IsFile = true });
                    Controls.DragGhostSession listSession = controller.Begin(many, true, new Point(220, 180));
                    Check(listSession != null, "list drag ghost did not start");
                    Check(listSession.Id > firstId, "drag ghost session id did not advance");
                    Check(listSession.Count == 10, "drag ghost count is wrong");
                    Check(listSession.Icon.Width >= 16 && listSession.Icon.Width <= 32,
                        "list drag ghost forced the small icon to a large size");

                    controller.Move(gridSession, new Point(500, 500));
                    controller.SetState(gridSession, Controls.DragDropIntent.Accept);
                    controller.End(gridSession);
                    Check(ReferenceEquals(controller.CurrentSession, listSession),
                        "stale session ended the current drag ghost");
                    controller.End(listSession);
                    controller.End(listSession);
                    controller.Dispose();
                    controller.Move(listSession, new Point(300, 300));
                    controller.SetState(listSession, Controls.DragDropIntent.Accept);
                    controller.End(listSession);
                }
            }

            using (Controls.DragGhostForm ghost = new Controls.DragGhostForm())
            {
                PropertyInfo property = typeof(Controls.DragGhostForm).GetProperty("CreateParams", Private);
                CreateParams cp = (CreateParams)property.GetValue(ghost, null);
                Check((cp.ExStyle & 0x00080000) != 0, "drag ghost is not layered");
                Check((cp.ExStyle & 0x00000020) != 0, "drag ghost is not hit-test transparent");
                Check((cp.ExStyle & 0x00000080) != 0, "drag ghost is not a tool window");
                Check((cp.ExStyle & 0x08000000) != 0, "drag ghost can activate");

                MethodInfo wndProc = typeof(Controls.DragGhostForm).GetMethod("WndProc", Private);
                object[] hit = { Message.Create(ghost.Handle, 0x0084, IntPtr.Zero, IntPtr.Zero) };
                wndProc.Invoke(ghost, hit);
                Check(((Message)hit[0]).Result == (IntPtr)(-1), "drag ghost did not return HTTRANSPARENT");
            }
        }

        private static void GridReorderScopes()
        {
            AppConfig resourceConfig = RenameConfig("grid-reorder-resource");
            resourceConfig.AutoScan = false;
            AppInfo photo1 = AddEntry(resourceConfig, "照片",
                Path.Combine(resourceConfig.ResourceStorePath, "照片", "01-photo.jpg"), false, true, "01 photo");
            AppInfo photo2 = AddEntry(resourceConfig, "照片",
                Path.Combine(resourceConfig.ResourceStorePath, "照片", "02-photo.jpg"), false, true, "02 photo");
            AppInfo photo3 = AddEntry(resourceConfig, "照片",
                Path.Combine(resourceConfig.ResourceStorePath, "照片", "03-photo.jpg"), false, true, "03 photo");
            string trip = CollectionKeys.Make("照片", "旅行");
            resourceConfig.Collections.Add(new ResourceCollection { Parent = "照片", Name = "旅行" });
            CollectionOps.AddMember(resourceConfig, photo1.Key, trip);
            CollectionOps.AddMember(resourceConfig, photo2.Key, trip);
            Check(resourceConfig.Save(), resourceConfig.DatabaseError);

            using (MainForm form = ResourceForm(resourceConfig, "照片", photo1, photo2, photo3))
            {
                Invoke(form, "RefreshGrid");
                Controls.AppGridControl grid = (Controls.AppGridControl)typeof(MainForm).GetField("grid", Private).GetValue(form);
                Check(grid.AllowReorder, "resource category did not enable grid reorder");
                long revision = resourceConfig.DatabaseRevision;
                Invoke(form, "OnGridReorder", 0, 0);
                Check(resourceConfig.DatabaseRevision == revision && !resourceConfig.ItemOrder.ContainsKey("照片"),
                    "same-position drop changed the resource order or revision");

                Invoke(form, "OnGridReorder", 0, 2);
                List<AppInfo> shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                string expected = photo2.Key + "|" + photo3.Key + "|" + photo1.Key;
                Check(string.Join("|", shown.Select(a => a.Key).ToArray()) == expected,
                    "resource grid did not apply the dropped order");
                List<string> order;
                Check(resourceConfig.ItemOrder.TryGetValue("照片", out order)
                    && string.Join("|", order.ToArray()) == expected,
                    "resource order was not saved under the category key");
                CatalogPage page = resourceConfig.Database.Page("照片", true, false, "", 500, null, false, false, null, null, "照片");
                Check(string.Join("|", page.Items.Select(a => a.Key).ToArray()) == expected,
                    "resource database projection did not follow the dropped order");

                typeof(MainForm).GetField("currentCollection", Private).SetValue(form, trip);
                typeof(MainForm).GetField("collectionEntered", Private).SetValue(form, true);
                Invoke(form, "RefreshGrid");
                Check(grid.AllowReorder, "collection view did not enable grid reorder");
                string parentOrder = string.Join("|", resourceConfig.ItemOrder["照片"].ToArray());
                Invoke(form, "OnGridReorder", 0, 1);
                shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                expected = photo2.Key + "|" + photo1.Key;
                Check(string.Join("|", shown.Select(a => a.Key).ToArray()) == expected,
                    "collection grid did not apply the dropped order");
                Check(resourceConfig.ItemOrder.TryGetValue(trip, out order)
                    && string.Join("|", order.ToArray()) == expected,
                    "collection order was not saved under the collection key");
                Check(string.Join("|", resourceConfig.ItemOrder["照片"].ToArray()) == parentOrder,
                    "collection reorder changed the parent category order");
                CatalogPage collectionPage = resourceConfig.Database.Page(trip, true, false, "", 500, null, false, false, null, null, trip);
                Check(string.Join("|", collectionPage.Items.Select(a => a.Key).ToArray()) == expected,
                    "collection database projection did not follow the dropped order");
            }

            AppConfig softwareConfig = RenameConfig("grid-reorder-software");
            softwareConfig.AutoScan = false;
            AppInfo tool1 = AddEntry(softwareConfig, "开发工具",
                Path.Combine(softwareConfig.ManageRootPath, "01-tool.exe"), false, false, "01 tool");
            AppInfo tool2 = AddEntry(softwareConfig, "开发工具",
                Path.Combine(softwareConfig.ManageRootPath, "02-tool.exe"), false, false, "02 tool");
            AppInfo tool3 = AddEntry(softwareConfig, "开发工具",
                Path.Combine(softwareConfig.ManageRootPath, "03-tool.exe"), false, false, "03 tool");
            Check(softwareConfig.Save(), softwareConfig.DatabaseError);

            using (MainForm form = CategoryForm(softwareConfig, tool1, tool2, tool3))
            {
                Invoke(form, "RefreshGrid");
                Controls.AppGridControl grid = (Controls.AppGridControl)typeof(MainForm).GetField("grid", Private).GetValue(form);
                Check(grid.AllowReorder, "software category did not enable grid reorder");
                Invoke(form, "OnGridReorder", 0, 1);
                List<AppInfo> shown = (List<AppInfo>)typeof(MainForm).GetField("shownApps", Private).GetValue(form);
                string expected = tool2.Key + "|" + tool1.Key + "|" + tool3.Key;
                Check(string.Join("|", shown.Select(a => a.Key).ToArray()) == expected,
                    "software grid did not apply the dropped order");
                List<string> order;
                Check(softwareConfig.ItemOrder.TryGetValue("开发工具", out order)
                    && string.Join("|", order.ToArray()) == expected,
                    "software order was not saved under the category key");
                CatalogPage page = softwareConfig.Database.Page("开发工具", false, false, "", 500, null, false, false, null, null, "开发工具");
                Check(string.Join("|", page.Items.Select(a => a.Key).ToArray()) == expected,
                    "software database projection did not follow the dropped order");
            }
        }

        private static void GridDragVisualLifecycle()
        {
            using (Controls.AppGridControl grid = new Controls.AppGridControl
            {
                Size = new Size(640, 420),
                AllowReorder = true
            })
            {
                grid.SetApps(new List<AppInfo>
                {
                    new AppInfo { Name = "one" }, new AppInfo { Name = "two" }, new AppInfo { Name = "three" }
                });
                grid.CreateControl();
                int started = 0, moved = 0, ended = 0;
                bool usedSmallIcon = true;
                Controls.DragDropIntent lastState = Controls.DragDropIntent.Reject;
                grid.DragVisualStarted += delegate(List<AppInfo> apps, bool small, Point screen) { started++; usedSmallIcon = small; };
                grid.DragVisualMoved += delegate(Point screen, Controls.DragDropIntent state) { moved++; lastState = state; };
                grid.DragVisualEnded += delegate { ended++; };

                Type type = typeof(Controls.AppGridControl);
                MethodInfo down = type.GetMethod("OnMouseDown", Private);
                MethodInfo move = type.GetMethod("OnMouseMove", Private);
                MethodInfo processKey = type.GetMethod("ProcessCmdKey", Private);
                down.Invoke(grid, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 30, 30, 0) });
                Check(started == 0, "drag visual started on mouse down");
                move.Invoke(grid, new object[] { new MouseEventArgs(MouseButtons.Left, 0, 31, 31, 0) });
                Check(started == 0, "drag visual started before the system threshold");
                move.Invoke(grid, new object[] { new MouseEventArgs(MouseButtons.Left, 0, 220, 60, 0) });
                Check(started == 1, "drag visual did not start after the threshold");
                Check(moved > 0, "drag visual move was not reported");
                Check(!usedSmallIcon, "grid drag visual incorrectly requested the list icon");
                Check(lastState == Controls.DragDropIntent.Accept, "grid reorder did not report a valid state");

                object[] escape = { Message.Create(grid.Handle, 0x0100, (IntPtr)Keys.Escape, IntPtr.Zero), Keys.Escape };
                bool handled = (bool)processKey.Invoke(grid, escape);
                Check(handled && ended == 1, "escape did not cancel and end the drag visual");
            }

            using (Controls.AppGridControl list = new Controls.AppGridControl
            {
                Size = new Size(640, 420),
                GridMode = false,
                DragOutData = delegate(List<AppInfo> apps) { return null; }
            })
            {
                list.SetApps(new List<AppInfo> { new AppInfo { Name = "one" }, new AppInfo { Name = "two" } });
                list.CreateControl();
                int started = 0, moved = 0;
                bool usedSmallIcon = false;
                list.DragVisualStarted += delegate(List<AppInfo> apps, bool small, Point screen) { started++; usedSmallIcon = small; };
                list.DragVisualMoved += delegate(Point screen, Controls.DragDropIntent state) { moved++; };

                Type type = typeof(Controls.AppGridControl);
                type.GetMethod("OnMouseDown", Private).Invoke(list,
                    new object[] { new MouseEventArgs(MouseButtons.Left, 1, 30, 50, 0) });
                type.GetMethod("OnMouseMove", Private).Invoke(list,
                    new object[] { new MouseEventArgs(MouseButtons.Left, 0, 220, 80, 0) });
                Check(started == 1, "drag-out-only item did not start the visual");
                Check(moved > 0, "drag-out-only item did not report movement");
                Check(usedSmallIcon, "list drag visual did not request the small icon");
                type.GetMethod("OnMouseUp", Private).Invoke(list,
                    new object[] { new MouseEventArgs(MouseButtons.Left, 1, 220, 80, 0) });
            }
        }

        private static void DragGhostRapidLifecycle()
        {
            using (DpiAwareForm owner = new DpiAwareForm())
            {
                Initialize(owner);
                using (Controls.DragGhostController controller = new Controls.DragGhostController(owner))
                using (Bitmap icon = new Bitmap(56, 56))
                {
                    using (Graphics g = Graphics.FromImage(icon)) g.Clear(Color.CadetBlue);
                    List<AppInfo> items = new List<AppInfo>
                    {
                        new AppInfo { Name = "rapid", IsFile = true, Icon = icon }
                    };
                    Controls.DragGhostSession warm = controller.Begin(items, false, new Point(10, 10));
                    Check(warm != null, "rapid drag ghost did not start");
                    controller.Begin(items, false, new Point(10, 10));   // Finish the warm-up session and create a stable window
                    IntPtr window = controller.WindowHandle;
                    Check(window != IntPtr.Zero, "drag ghost window was not created");
                    uint before = GdiHandleCount();

                    for (int i = 0; i < 100; i++)
                    {
                        Controls.DragGhostSession session = controller.Begin(items, false,
                            new Point(20 + (i % 20), 20 + (i % 20)));
                        Check(session != null, "rapid drag ghost failed at iteration " + i);
                        controller.SetPayloadCount(session, 1 + (i % 3));
                        controller.Move(session, new Point(40, 40));
                        controller.SetState(session, (i & 1) == 0
                            ? Controls.DragDropIntent.Accept : Controls.DragDropIntent.Reject);
                        controller.End(session);
                    }

                    controller.Begin(items, false, new Point(10, 10));
                    Check(controller.WindowHandle == window, "layered drag window was recreated during rapid cycles");
                    controller.Dispose();
                    Check(!controller.TimerRunning, "animation timer remained running after dispose");
                    Check(controller.WindowHandle == IntPtr.Zero, "layered drag window remained after dispose");
                    uint after = GdiHandleCount();
                    if (before > 0 && after > before + 8)
                        Check(false, "GDI handles grew from " + before + " to " + after);
                }
            }
        }

        [DllImport("user32.dll")]
        private static extern uint GetGuiResources(IntPtr hProcess, uint flags);

        private static uint GdiHandleCount()
        {
            using (Process process = Process.GetCurrentProcess())
                return GetGuiResources(process.Handle, 0);
        }

        private static void GridDragCleanup()
        {
            foreach (string mode in new string[] { "capture", "cancel-mode", "refresh" })
            {
                using (Controls.AppGridControl grid = new Controls.AppGridControl
                {
                    Size = new Size(640, 420),
                    AllowReorder = true
                })
                {
                    grid.SetApps(new List<AppInfo>
                    {
                        new AppInfo { Name = "one" }, new AppInfo { Name = "two" }, new AppInfo { Name = "three" }
                    });
                    grid.CreateControl();
                    int ended = 0, drops = 0;
                    grid.DragVisualEnded += delegate { ended++; };
                    grid.ReorderRequested += delegate { drops++; };
                    grid.MoveIntoFolder += delegate { drops++; };
                    grid.DroppedOutside += delegate { drops++; };

                    StartGridDragForTest(grid, new Point(30, 30), new Point(220, 60));
                    Check(ended == 0 && drops == 0, "drag did not start cleanly: " + mode);
                    if (mode == "capture")
                    {
                        grid.Capture = false;
                        typeof(Controls.AppGridControl).GetMethod("OnMouseCaptureChanged", Private)
                            .Invoke(grid, new object[] { EventArgs.Empty });
                    }
                    else if (mode == "cancel-mode")
                    {
                        object[] message = { Message.Create(grid.Handle, 0x001F, IntPtr.Zero, IntPtr.Zero) };
                        typeof(Controls.AppGridControl).GetMethod("WndProc", Private).Invoke(grid, message);
                    }
                    else
                    {
                        grid.SetApps(new List<AppInfo> { new AppInfo { Name = "replacement" } });
                    }
                    Check(ended == 1, "cleanup did not end the drag visual: " + mode);
                    typeof(Controls.AppGridControl).GetMethod("OnMouseUp", Private)
                        .Invoke(grid, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 220, 60, 0) });
                    Check(drops == 0, "cleanup triggered a drop action: " + mode);
                    Check((int)typeof(Controls.AppGridControl).GetField("dragFrom", Private).GetValue(grid) == -1,
                        "cleanup left a stale drag source: " + mode);
                }
            }

            using (MainForm form = CategoryForm(new AppConfig(), new AppInfo { Key = "af:drag-minimize", Name = "minimize" }))
            {
                Controls.AppGridControl grid = (Controls.AppGridControl)typeof(MainForm).GetField("grid", Private).GetValue(form);
                Check(grid != null, "main form grid was not created");
                grid.AllowReorder = true;
                grid.SetApps(new List<AppInfo> { new AppInfo { Name = "one" }, new AppInfo { Name = "two" } });
                grid.CreateControl();
                int ended = 0;
                grid.DragVisualEnded += delegate { ended++; };
                StartGridDragForTest(grid, new Point(30, 30), new Point(220, 60));
                object[] message = { Message.Create(form.Handle, 0x0005, (IntPtr)1, IntPtr.Zero) };   // WM_SIZE / SIZE_MINIMIZED
                typeof(MainForm).GetMethod("WndProc", Private).Invoke(form, message);
                Check(ended == 1, "minimize did not clear the drag session");

                typeof(MainForm).GetField("dragGhostSession", Private).SetValue(form, null);
                Invoke(form, "OnGridDragVisualExternalStarting", new Point(0, 0), 1);
                Check((bool)typeof(MainForm).GetField("dragGhostExternal", Private).GetValue(form),
                    "OLE takeover without a visual did not mark the external session");
                QueryContinueDragEventArgs query = new QueryContinueDragEventArgs(1, true, DragAction.Continue);
                typeof(MainForm).GetMethod("OnGridQueryContinueDrag", Private)
                    .Invoke(form, new object[] { null, query });
                Check(query.Action == DragAction.Cancel, "OLE escape without a visual did not request cancellation");
                typeof(MainForm).GetField("dragCategoryTarget", Private).SetValue(form, "照片");
                Invoke(form, "CancelGridDragSession");
                Check((bool)typeof(MainForm).GetField("dragGhostExternal", Private).GetValue(form)
                    && (string)typeof(MainForm).GetField("dragCategoryTarget", Private).GetValue(form) == "照片",
                    "cancel mode ended an OLE-owned drag session early");
                typeof(MainForm).GetField("dragGhostExternal", Private).SetValue(form, false);
                Invoke(form, "CancelGridDragSession");
                Check(!(bool)typeof(MainForm).GetField("dragGhostExternal", Private).GetValue(form)
                    && (string)typeof(MainForm).GetField("dragCategoryTarget", Private).GetValue(form) == null,
                    "non-OLE cancel did not clear the drag session");
            }
        }

        private static void StartGridDragForTest(Controls.AppGridControl grid, Point start, Point end)
        {
            if (!grid.IsHandleCreated) grid.CreateControl();
            Type type = typeof(Controls.AppGridControl);
            type.GetMethod("OnMouseDown", Private).Invoke(grid,
                new object[] { new MouseEventArgs(MouseButtons.Left, 1, start.X, start.Y, 0) });
            type.GetMethod("OnMouseMove", Private).Invoke(grid,
                new object[] { new MouseEventArgs(MouseButtons.Left, 0, end.X, end.Y, 0) });
        }

        private static void DragIntentSemantics()
        {
            using (Controls.AppGridControl grid = new Controls.AppGridControl
            {
                Size = new Size(640, 420),
                AllowReorder = true
            })
            {
                grid.SetApps(new List<AppInfo>
                {
                    new AppInfo { Name = "one" }, new AppInfo { Name = "two" }, new AppInfo { Name = "three" }
                });
                grid.CreateControl();
                Controls.DragDropIntent state = Controls.DragDropIntent.Reject;
                grid.DragVisualMoved += delegate(Point screen, Controls.DragDropIntent value) { state = value; };
                StartGridDragForTest(grid, new Point(30, 30), new Point(1, 30));
                Check(state == Controls.DragDropIntent.Neutral, "dropping a row back at its original position was not neutral");
                grid.CancelActiveDrag();
            }

            string photoPendingPath = Path.Combine(root, "drag-pending.jpg");
            string photoMemberPath = Path.Combine(root, "drag-member.jpg");
            string videoPath = Path.Combine(root, "drag-video.mp4");
            File.WriteAllText(photoPendingPath, "photo");
            File.WriteAllText(photoMemberPath, "photo");
            File.WriteAllText(videoPath, "video");
            AppConfig config = new AppConfig();
            AppInfo pendingPhoto = new AppInfo { Key = "add:" + photoPendingPath, TargetPath = photoPendingPath,
                Name = "pending", Category = "其他", IsFile = true };
            AppInfo memberPhoto = new AppInfo { Key = "add:" + photoMemberPath, TargetPath = photoMemberPath,
                Name = "member", Category = "照片", IsFile = true };
            AppInfo video = new AppInfo { Key = "add:" + videoPath, TargetPath = videoPath,
                Name = "video", Category = "其他", IsFile = true };
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "旅行" });
            config.Collections.Add(new ResourceCollection { Parent = "照片", Name = "另一个" });
            string trip = CollectionKeys.Make("照片", "旅行");
            string other = CollectionKeys.Make("照片", "另一个");
            CollectionOps.AddMember(config, memberPhoto.Key, trip);

            using (MainForm form = CategoryForm(config, pendingPhoto, memberPhoto, video))
            {
                typeof(MainForm).GetField("contentMode", Private).SetValue(form, Controls.SidebarControl.ContentMode.Website);
                List<AppInfo> onePending = new List<AppInfo> { pendingPhoto };
                List<AppInfo> oneMember = new List<AppInfo> { memberPhoto };
                List<AppInfo> oneVideo = new List<AppInfo> { video };
                Check((Controls.DragDropIntent)Invoke(form, "ResolveAddIntent", onePending, "照片") == Controls.DragDropIntent.Accept,
                    "valid photo category target was not accepted");
                Check((Controls.DragDropIntent)Invoke(form, "ResolveAddIntent", oneMember, "照片") == Controls.DragDropIntent.Neutral,
                    "already assigned category target was not neutral");
                Check((Controls.DragDropIntent)Invoke(form, "ResolveAddIntent", oneVideo, "照片") == Controls.DragDropIntent.Reject,
                    "type mismatch category target was not rejected");
                Check((Controls.DragDropIntent)Invoke(form, "ResolveAddIntent", oneMember, trip) == Controls.DragDropIntent.Neutral,
                    "already assigned collection target was not neutral");
                Check((Controls.DragDropIntent)Invoke(form, "ResolveAddIntent", oneMember, other) == Controls.DragDropIntent.Accept,
                    "eligible collection target was not accepted");
                Check((Controls.DragDropIntent)Invoke(form, "ResolveAddIntent", onePending, trip) == Controls.DragDropIntent.Reject,
                    "collection target outside the parent category was not rejected");
            }

            string baseDir = Path.Combine(root, "drag-folder-rules");
            string sourceDir = Path.Combine(baseDir, "Source");
            string childDir = Path.Combine(sourceDir, "Child");
            string targetDir = Path.Combine(baseDir, "Target");
            string filesDir = Path.Combine(baseDir, "Files");
            Directory.CreateDirectory(childDir);
            Directory.CreateDirectory(targetDir);
            Directory.CreateDirectory(filesDir);
            string movablePath = Path.Combine(filesDir, "move.txt");
            string insidePath = Path.Combine(targetDir, "inside.txt");
            File.WriteAllText(movablePath, "move");
            File.WriteAllText(insidePath, "inside");
            AppConfig folderConfig = new AppConfig { ManageRootPath = baseDir };
            AppInfo sourceFile = new AppInfo { Key = "add:" + movablePath, TargetPath = movablePath,
                Name = "move", IsFile = true };
            AppInfo sourceFolder = new AppInfo { Key = "add:" + sourceDir, TargetPath = sourceDir,
                Name = "source", IsFile = true, IsFolder = true };
            AppInfo childFolder = new AppInfo { Key = "add:" + childDir, TargetPath = childDir,
                Name = "child", IsFile = true, IsFolder = true };
            AppInfo target = new AppInfo { Key = "add:" + targetDir, TargetPath = targetDir,
                Name = "target", IsFile = true, IsFolder = true };
            AppInfo insideFile = new AppInfo { Key = "add:" + insidePath, TargetPath = insidePath,
                Name = "inside", IsFile = true };
            AppInfo missingFile = new AppInfo { Key = "add:" + Path.Combine(baseDir, "missing.txt"),
                TargetPath = Path.Combine(baseDir, "missing.txt"), Name = "missing", IsFile = true };
            AppInfo missingTarget = new AppInfo { Key = "add:" + Path.Combine(baseDir, "missing-target"),
                TargetPath = Path.Combine(baseDir, "missing-target"), Name = "missing-target", IsFile = true, IsFolder = true };
            using (MainForm form = CategoryForm(folderConfig))
            {
                Check((Controls.DragDropIntent)Invoke(form, "ResolveFolderDropIntentForGrid", sourceFile, target) == Controls.DragDropIntent.Accept,
                    "valid file-to-folder move was not accepted");
                Check((Controls.DragDropIntent)Invoke(form, "ResolveFolderDropIntentForGrid", sourceFolder, target) == Controls.DragDropIntent.Accept,
                    "valid folder-to-folder move was not accepted");
                Check((Controls.DragDropIntent)Invoke(form, "ResolveFolderDropIntentForGrid", sourceFolder, sourceFolder) == Controls.DragDropIntent.Reject,
                    "folder dropped onto itself was not rejected");
                Check((Controls.DragDropIntent)Invoke(form, "ResolveFolderDropIntentForGrid", sourceFolder, childFolder) == Controls.DragDropIntent.Reject,
                    "folder dropped into its child was not rejected");
                Check((Controls.DragDropIntent)Invoke(form, "ResolveFolderDropIntentForGrid", insideFile, target) == Controls.DragDropIntent.Neutral,
                    "file already inside the target folder was not neutral");
                Check((Controls.DragDropIntent)Invoke(form, "ResolveFolderDropIntentForGrid", missingFile, target) == Controls.DragDropIntent.Reject,
                    "missing source was not rejected");
                Check((Controls.DragDropIntent)Invoke(form, "ResolveFolderDropIntentForGrid", sourceFile, missingTarget) == Controls.DragDropIntent.Reject,
                    "missing target was not rejected");
            }
        }

        private static void DragExportPayload()
        {
            string dir = Path.Combine(root, "drag-export");
            Directory.CreateDirectory(dir);
            string firstPath = Path.Combine(dir, "one.txt");
            string secondPath = Path.Combine(dir, "two.txt");
            File.WriteAllText(firstPath, "one");
            File.WriteAllText(secondPath, "two");
            AppConfig config = new AppConfig { ManageRootPath = dir };
            config.AddedItems.Add(new AddedEntry { Path = firstPath, Category = "文件" });
            config.AddedItems.Add(new AddedEntry { Path = secondPath, Category = "文件" });
            AppInfo first = new AppInfo { Key = "add:" + firstPath, TargetPath = firstPath,
                Name = "one", Category = "文件", IsFile = true };
            AppInfo second = new AppInfo { Key = "add:" + secondPath, TargetPath = secondPath,
                Name = "two", Category = "文件", IsFile = true };
            AppInfo nonExportable = new AppInfo { Key = "packaged:drag-export", TargetPath = "shell:AppsFolder\\drag-export",
                Name = "packaged", Category = "文件", IsFile = true };

            using (MainForm form = CategoryForm(config))
            {
                List<AppInfo> partial = new List<AppInfo> { first, second, first, nonExportable };
                object[] partialArgs = { partial, false };
                List<string> partialPaths = (List<string>)typeof(MainForm)
                    .GetMethod("ExportPaths", StaticPrivate).Invoke(null, partialArgs);
                Check(partialPaths.Count == 2 && !(bool)partialArgs[1],
                    "partial export count did not match the distinct existing files");

                Controls.ExternalDragRequest partialRequest = (Controls.ExternalDragRequest)Invoke(form, "ExportDragData", partial);
                Check(partialRequest != null, "partial export did not produce a drag request");
                Check(partialRequest.PayloadCount == partialPaths.Count,
                    "partial payload count diverged from the data object");
                Check(partialRequest.Data.GetFileDropList().Count == partialRequest.PayloadCount,
                    "partial file drop list length diverged from payload count");
                Check((partialRequest.Allowed & DragDropEffects.Move) == 0,
                    "partial export incorrectly advertised move");

                List<AppInfo> complete = new List<AppInfo> { first, second, first };
                object[] completeArgs = { complete, false };
                List<string> completePaths = (List<string>)typeof(MainForm)
                    .GetMethod("ExportPaths", StaticPrivate).Invoke(null, completeArgs);
                Check(completePaths.Count == 2 && (bool)completeArgs[1],
                    "complete export count did not match the distinct existing files");
                Controls.ExternalDragRequest completeRequest = (Controls.ExternalDragRequest)Invoke(form, "ExportDragData", complete);
                Check(completeRequest != null, "complete export did not produce a drag request");
                Check(completeRequest.PayloadCount == completePaths.Count,
                    "complete payload count diverged from the data object");
                Check(completeRequest.Data.GetFileDropList().Count == completeRequest.PayloadCount,
                    "complete file drop list length diverged from payload count");
                Check((completeRequest.Allowed & DragDropEffects.Move) != 0,
                    "complete managed export did not advertise move");
            }
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

        [DllImport("shcore.dll")]
        private static extern int GetProcessDpiAwareness(IntPtr process, out int awareness);

        /// <summary>
        /// 清单里的 DPI 设置一旦被系统忽略（例如命名空间写错），进程会以「不感知 DPI」启动，
        /// 之后 WPF 图片组件加载时再被改成「系统级感知」：排版停在 96 DPI、文字按屏幕 DPI 绘制，界面字大框小。
        /// 本测试程序与正式程序内嵌同一份清单，直接断言进程的感知级别。
        /// </summary>
        private static void ManifestDpiAwareness()
        {
            const int PerMonitorAware = 2;
            // 本方法引用了 WPF 图片组件（WicImageDecoder 的依赖），程序集在执行到这里之前就已加载，
            // 其模块初始化会尝试把进程改成系统级感知：清单生效时这一步无效，失效时查询结果是 1 而不是 2。
            GC.KeepAlive(typeof(System.Windows.Media.Imaging.BitmapDecoder).Assembly);
            int awareness;
            Check(GetProcessDpiAwareness(IntPtr.Zero, out awareness) == 0, "process DPI awareness query failed");
            Check(awareness == PerMonitorAware, "app.manifest DPI settings are ignored: process awareness is " + awareness + " instead of per-monitor");
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

        private static void ResizeOverGridScrollbar()
        {
            AppConfig config = RenameConfig("resize-scrollbar");
            config.AutoScan = false;
            List<AppInfo> apps = new List<AppInfo>();
            for (int i = 0; i < 120; i++)
                apps.Add(new AppInfo { Key = "af:resize-" + i, Name = "Resize " + i, Category = "开发工具" });
            Check(config.Save(), "database fixture save failed");
            config.Database.PublishCatalog(config, new List<AppInfo>(apps));

            Cursor original = Cursor.Current;
            try
            {
                using (MainForm form = new MainForm(config))
                {
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
                    // 主列表的滚动条已是自绘细条（在客户区内），这里只需确认内容确实溢出、滚动条会出现
                    Check(grid.AutoScrollMinSize.Height > grid.ClientSize.Height, "fixture does not overflow the grid");
                    int edge = DpiHelper.Scale(7, DpiHelper.ForControl(form));
                    int middleY = grid.Top + grid.Height / 2;
                    MethodInfo hitTest = typeof(MainForm).GetMethod("ResizeHitTest", Private);
                    Point rightClient = new Point(form.ClientSize.Width - 2, middleY);
                    Check((int)hitTest.Invoke(form, new object[] { rightClient }) == 11,
                        "right resize edge missing beside the grid scrollbar");

                    Cursor.Current = Cursors.Default;
                    Message message = Message.Create(grid.Handle, 0x00A0, IntPtr.Zero,
                        PackScreenPoint(form.PointToScreen(rightClient)));
                    Check(!form.PreFilterMessage(ref message), "non-client move was swallowed");
                    Check(Cursor.Current == Cursors.SizeWE,
                        "non-client scrollbar move did not show the horizontal resize cursor");

                    Point innerClient = new Point(form.ClientSize.Width - edge - 2, middleY);
                    Check((int)hitTest.Invoke(form, new object[] { innerClient }) == 0,
                        "scrollbar interior became a resize zone");
                    Cursor.Current = Cursors.Hand;
                    message = Message.Create(grid.Handle, 0x00A0, IntPtr.Zero,
                        PackScreenPoint(form.PointToScreen(innerClient)));
                    form.PreFilterMessage(ref message);
                    Check(Cursor.Current == Cursors.Hand,
                        "scrollbar interior move was turned into a resize cursor");
                }
            }
            finally
            {
                Cursor.Current = original;
            }
        }

        private static IntPtr PackScreenPoint(Point point)
        {
            long packed = (long)(uint)(point.X & 0xFFFF) | ((long)(uint)(point.Y & 0xFFFF) << 16);
            return new IntPtr(packed);
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
            using (DpiAwareForm form = new DpiAwareForm())
            using (LayoutBackupPanel panel = new LayoutBackupPanel())
            {
                form.ClientSize = new Size(640, 560);
                panel.Bounds = new Rectangle(0, 0, 640, 560);
                form.Controls.Add(panel);
                Initialize(form);
                Controls.BackupListControl list = (Controls.BackupListControl)typeof(LayoutBackupPanel).GetField("list", Private).GetValue(panel);
                // Same order as LayoutBackup.List: rollback folders sort before the user's own backups.
                List<LayoutBackupInfo> items = new List<LayoutBackupInfo>();
                items.Add(new LayoutBackupInfo { Id = "_prerestore_20260102-030405-aaaa", Name = "broken", IsPreRestore = true, Incomplete = true });
                items.Add(new LayoutBackupInfo { Id = "_prerestore_20260101-030405-bbbb", Name = "rollback", IsPreRestore = true });
                items.Add(new LayoutBackupInfo { Id = "20260103-000000", Name = "new", Created = "2026-01-03 00:00:00" });
                items.Add(new LayoutBackupInfo { Id = "20260101-000000", Name = "old", Created = "2026-01-01 00:00:00" });
                list.SetItems(items, "20260101-000000");
                Check(list.CardCount == 4 && list.Selected != null && list.Selected.Name == "old", "requested backup not selected");
                foreach (int dpi in new int[] { 96, 120, 144, 168 })
                {
                    SetDpi(form, dpi, new Rectangle(10, 10, DpiHelper.Scale(640, dpi), DpiHelper.Scale(560, dpi)));
                    Rectangle first = list.CardRect(0), second = list.CardRect(1), broken = list.CardRect(2);
                    Check(first.Height == DpiHelper.Scale(90, dpi), "card height incorrect");
                    Check(first.Left >= 0 && first.Right <= list.Width, "card wider than the list");
                    Check(second.Top == first.Bottom + DpiHelper.Scale(10, dpi), "card gap incorrect");
                    Check(broken.Top >= second.Bottom + DpiHelper.Scale(28, dpi), "rollback group has no heading of its own");
                    Rectangle restore = list.ActionRect(0, Controls.BackupListControl.CardAction.Restore);
                    Rectangle more = list.ActionRect(0, Controls.BackupListControl.CardAction.More);
                    Check(first.Contains(restore) && first.Contains(more) && restore.Right < more.Left, "inline buttons outside the card or overlapping");
                    Check(list.ActionRect(0, Controls.BackupListControl.CardAction.Delete).IsEmpty, "own backup shows delete outside the menu");
                    Check(list.ActionRect(2, Controls.BackupListControl.CardAction.Restore).IsEmpty
                        && !list.ActionRect(2, Controls.BackupListControl.CardAction.Delete).IsEmpty, "incomplete rollback offers restore");
                    Check(!list.ActionRect(3, Controls.BackupListControl.CardAction.Restore).IsEmpty
                        && list.ActionRect(3, Controls.BackupListControl.CardAction.More).IsEmpty, "rollback buttons incorrect");
                    using (Bitmap bitmap = new Bitmap(form.Width, form.Height)) form.DrawToBitmap(bitmap, form.ClientRectangle);
                }
            }
        }

        private static void BackupCards()
        {
            // 新建备份不再弹命名框：同一分钟内重名时依次加序号，恢复前留底的名字不占用
            List<LayoutBackupInfo> named = new List<LayoutBackupInfo>
            {
                new LayoutBackupInfo { Name = "布局备份 11-15 23:48" }, new LayoutBackupInfo { Name = "布局备份 11-15 23:48 (2)" },
                new LayoutBackupInfo { Name = "布局备份 11-16 08:00", IsPreRestore = true }
            };
            Check(LayoutBackupPanel.UniqueName("布局备份 11-15 23:48", named) == "布局备份 11-15 23:48 (3)"
                && LayoutBackupPanel.UniqueName("布局备份 11-16 08:00", named) == "布局备份 11-16 08:00"
                && LayoutBackupPanel.UniqueName("新的", null) == "新的", "duplicate default backup names were not numbered");

            DateTime now = new DateTime(2026, 11, 14, 12, 0, 0);
            Func<DateTime, string> relative = delegate(DateTime then) { return Controls.BackupListControl.RelativeTime(then, now); };
            Check(relative(now.AddSeconds(-20)) == "刚刚" && relative(now.AddMinutes(-5)) == "5 分钟前"
                && relative(now.AddHours(-3)) == "3 小时前", "recent relative time wrong");
            Check(relative(new DateTime(2026, 11, 13, 23, 59, 0)) == "昨天"
                && relative(new DateTime(2026, 10, 29, 14, 18, 40)) == "16 天前", "day relative time wrong");
            Check(relative(now.AddDays(-70)) == "2 个月前" && relative(now.AddDays(-800)) == "2 年前"
                && relative(now.AddHours(2)) == string.Empty, "far or future relative time wrong");

            LayoutBackupInfo own = new LayoutBackupInfo
            {
                Id = "20261029-141840", Name = "布局备份 10-29 14:18", Created = "2026-10-29 14:18:40",
                StashCount = 33, CustomCount = 121, CategoryCount = 3, SizeBytes = 81 * 1024
            };
            LayoutBackupInfo rollback = new LayoutBackupInfo
            {
                Id = "_prerestore_20261102-201500-0123456789abcdef", Name = "恢复前留底 20261102-201500-0123456789abcdef",
                Created = "bad", IsPreRestore = true, StashCount = 2, SizeBytes = -1
            };
            LayoutBackupInfo broken = new LayoutBackupInfo { Id = "_prerestore_x", Name = "恢复前留底 x", IsPreRestore = true, Incomplete = true };
            Check(Controls.BackupListControl.TimeText(own, now) == "16 天前 · 2026-10-29 14:18", "time line wrong");
            Check(Controls.BackupListControl.TimeText(rollback, now) == "bad"
                && Controls.BackupListControl.TimeText(broken, now) == "时间未知", "unparsable time not shown as is");
            Check(Controls.BackupListControl.DisplayName(own) == own.Name
                && Controls.BackupListControl.DisplayName(rollback) == "恢复前留底 11-02 20:15"
                && Controls.BackupListControl.DisplayName(broken) == "恢复前留底 x", "card title wrong");
            Check(string.Join("|", Controls.BackupListControl.Facts(own)) == "收纳 33|自定义 121|分类 3|81 KB"
                && string.Join("|", Controls.BackupListControl.Facts(rollback)) == "收纳 2|大小未统计", "fact chips wrong");

            using (Controls.BackupListControl list = new Controls.BackupListControl())
            {
                list.Size = new Size(608, 460);
                IntPtr handle = list.Handle;
                int restores = 0, renames = 0, deletes = 0, menus = 0;
                bool menuFromButton = false;
                list.RestoreRequested += delegate { restores++; };
                list.RenameRequested += delegate { renames++; };
                list.DeleteRequested += delegate { deletes++; };
                list.MenuRequested += delegate(LayoutBackupInfo info, Rectangle anchor, bool fromButton) { menus++; menuFromButton = fromButton; };
                MethodInfo keyDown = typeof(Controls.BackupListControl).GetMethod("OnKeyDown", Private);
                MethodInfo mouseDown = typeof(Controls.BackupListControl).GetMethod("OnMouseDown", Private);
                MethodInfo mouseUp = typeof(Controls.BackupListControl).GetMethod("OnMouseUp", Private);
                MethodInfo keyUp = typeof(Controls.BackupListControl).GetMethod("OnKeyUp", Private);
                Action<Keys> press = delegate(Keys key)
                {
                    keyDown.Invoke(list, new object[] { new KeyEventArgs(key) });
                    keyUp.Invoke(list, new object[] { new KeyEventArgs(key) });
                };
                Func<Rectangle, Point> center = delegate(Rectangle r) { return new Point(r.Left + r.Width / 2, r.Top + r.Height / 2); };
                Action<Point, Point, MouseButtons> drag = delegate(Point from, Point to, MouseButtons button)
                {
                    mouseDown.Invoke(list, new object[] { new MouseEventArgs(button, 1, from.X, from.Y, 0) });
                    mouseUp.Invoke(list, new object[] { new MouseEventArgs(button, 1, to.X, to.Y, 0) });
                };
                Action<Point, MouseButtons> click = delegate(Point p, MouseButtons button) { drag(p, p, button); };
                Func<int, Controls.BackupListControl.CardAction, Point> buttonAt = delegate(int card, Controls.BackupListControl.CardAction action)
                {
                    Rectangle r = list.ActionRect(card, action);
                    Check(!r.IsEmpty, "button missing: card " + card + " " + action);
                    return center(r);
                };
                Func<int, Point> cardBody = delegate(int card) { Rectangle r = list.CardRect(card); return new Point(r.Left + 100, r.Top + r.Height / 2); };

                // Empty list: nothing to act on, empty state renders.
                list.SetItems(new List<LayoutBackupInfo>(), null);
                press(Keys.Down); press(Keys.Enter); press(Keys.Delete);
                Check(list.CardCount == 0 && list.Selected == null && restores + deletes == 0, "empty list reacts to keys");
                using (Bitmap bitmap = new Bitmap(list.Width, list.Height)) list.DrawToBitmap(bitmap, list.ClientRectangle);

                // Own backups first, rollbacks after; nothing selected until the user interacts.
                list.SetItems(new List<LayoutBackupInfo> { broken, rollback, own }, null);
                Check(list.CardCount == 3 && list.Selected == null, "selection appears before any interaction");
                press(Keys.Enter); press(Keys.Delete);
                Check(restores + deletes == 0, "keys act without a selected backup");
                press(Keys.Down);
                Check(list.Selected == own, "own backup is not listed first");
                press(Keys.Enter); press(Keys.F2);
                Check(restores == 1 && renames == 1, "own backup: Enter/F2 ignored");
                press(Keys.Down);
                Check(list.Selected == broken, "incomplete rollback not second");
                press(Keys.Enter); press(Keys.F2);
                Check(restores == 1 && renames == 1, "incomplete rollback can be restored or renamed");
                press(Keys.Delete);
                Check(deletes == 1, "incomplete rollback cannot be deleted");
                press(Keys.End);
                Check(list.Selected == rollback, "End does not select the last card");
                press(Keys.Enter); press(Keys.F2);
                Check(restores == 2 && renames == 1, "rollback: restore missing or rename allowed");

                click(buttonAt(0, Controls.BackupListControl.CardAction.Restore), MouseButtons.Left);
                Check(restores == 3 && list.Selected == own, "inline restore button ignored");
                click(buttonAt(0, Controls.BackupListControl.CardAction.More), MouseButtons.Left);
                Check(menus == 1 && menuFromButton, "more button does not open the menu");
                click(cardBody(2), MouseButtons.Right);
                Check(menus == 2 && !menuFromButton && list.Selected == rollback, "right click does not open the menu for that card");
                click(buttonAt(1, Controls.BackupListControl.CardAction.Delete), MouseButtons.Left);
                Check(deletes == 2 && list.Selected == broken, "inline delete button ignored");
                drag(buttonAt(0, Controls.BackupListControl.CardAction.Restore), cardBody(0), MouseButtons.Left);
                Check(restores == 3, "releasing outside the button still triggers it");

                // Busy: every entry point is blocked, the busy card still renders.
                Point restoreOwn = buttonAt(0, Controls.BackupListControl.CardAction.Restore);
                list.SetBusy(own.Id, "正在恢复…");
                click(restoreOwn, MouseButtons.Left);
                click(cardBody(0), MouseButtons.Right);
                press(Keys.Enter); press(Keys.Delete); press(Keys.F2); press(Keys.Apps);
                Check(restores == 3 && deletes == 2 && renames == 1 && menus == 2, "actions run while an operation is in progress");
                using (Bitmap bitmap = new Bitmap(list.Width, list.Height)) list.DrawToBitmap(bitmap, list.ClientRectangle);
                list.ClearBusy();
                click(restoreOwn, MouseButtons.Left);
                Check(restores == 4, "buttons stay disabled after the operation ends");

                // Reload keeps the selection by id; a deleted backup clears it; an explicit id wins.
                list.SetItems(new List<LayoutBackupInfo> { rollback, own }, null);
                Check(list.Selected == own, "reload loses the selection");
                list.SetItems(new List<LayoutBackupInfo> { rollback }, null);
                Check(list.Selected == null, "selection points at a removed backup");
                list.SetItems(new List<LayoutBackupInfo> { rollback, own }, rollback.Id);
                Check(list.Selected == rollback, "explicit selection ignored");

                // Scrolling: keyboard navigation and a requested selection bring the card fully into view.
                List<LayoutBackupInfo> many = new List<LayoutBackupInfo>();
                for (int i = 0; i < 12; i++)
                    many.Add(new LayoutBackupInfo { Id = "20260101-0000" + i.ToString("00"), Name = "backup " + i, Created = "2026-01-01 00:00:00" });
                list.SetItems(many, null);
                Func<int, bool> visible = delegate(int card) { Rectangle r = list.CardRect(card); return r.Top >= 0 && r.Bottom <= list.Height; };
                Check(visible(0) && !visible(11), "long list does not need scrolling");
                press(Keys.End);
                Check(list.Selected == many[11] && visible(11) && !visible(0), "End does not scroll to the last card");
                click(buttonAt(11, Controls.BackupListControl.CardAction.Restore), MouseButtons.Left);
                Check(restores == 5 && list.Selected == many[11], "button hit testing ignores the scroll position");
                press(Keys.Home);
                Check(list.Selected == many[0] && visible(0), "Home does not scroll back to the top");
                list.SetItems(many, many[8].Id);
                Check(list.Selected == many[8] && visible(8), "newly selected backup is not scrolled into view");
                using (Bitmap bitmap = new Bitmap(list.Width, list.Height)) list.DrawToBitmap(bitmap, list.ClientRectangle);

                // Enter restores on release, once, and only for a press that started on this list:
                // a held key must not open the confirmation and then confirm it by auto-repeat.
                Action enterDown = delegate { keyDown.Invoke(list, new object[] { new KeyEventArgs(Keys.Enter) }); };
                Action enterUp = delegate { keyUp.Invoke(list, new object[] { new KeyEventArgs(Keys.Enter) }); };
                int baseline = restores;
                enterUp();
                Check(restores == baseline, "Enter release without a press on the list restores");
                enterDown(); enterDown();
                Check(restores == baseline, "Enter restores before the key is released");
                enterUp(); enterUp();
                Check(restores == baseline + 1, "Enter press and release does not restore exactly once");
                MethodInfo commandKey = typeof(Controls.BackupListControl).GetMethod("ProcessCmdKey", Private);
                Action<int> enterMessage = delegate(int flags)
                {
                    commandKey.Invoke(list, new object[] { Message.Create(list.Handle, 0x0100, (IntPtr)(int)Keys.Enter, (IntPtr)flags), Keys.Enter });
                };
                enterMessage(0x40000001); enterDown(); enterUp();
                Check(restores == baseline + 1, "auto-repeated Enter (key held since another dialog) restores");
                enterMessage(1); enterDown(); enterUp();
                Check(restores == baseline + 2, "fresh Enter press ignored after a repeated one");
                enterDown(); list.SetBusy(many[8].Id, "…"); enterUp(); list.ClearBusy(); enterUp();
                Check(restores == baseline + 2, "Enter pressed before an operation started restores afterwards");
                enterDown(); list.SetItems(many, null); enterUp();
                Check(restores == baseline + 2, "Enter pressed before the list reloaded restores afterwards");

                // Hover tip: only for a name that does not fit, and not while pointing at a button.
                LayoutBackupInfo longName = new LayoutBackupInfo { Id = "20260201-000000", Name = new string('长', 80), Created = "2026-02-01 00:00:00" };
                list.SetItems(new List<LayoutBackupInfo> { longName, own }, null);
                Check(list.TruncatedNameAt(cardBody(0)) == longName.Name, "truncated name has no hover tip");
                Check(list.TruncatedNameAt(cardBody(1)) == null, "short name shows a hover tip");
                Check(list.TruncatedNameAt(buttonAt(0, Controls.BackupListControl.CardAction.Restore)) == null, "hover tip shown over a button");
                Check(list.TruncatedNameAt(new Point(list.Width / 2, list.Height - 2)) == null, "hover tip shown outside the cards");
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static void BackupWheel()
        {
            using (DpiAwareForm form = new DpiAwareForm())
            using (LayoutBackupPanel panel = new LayoutBackupPanel())
            {
                form.ClientSize = new Size(640, 560);
                panel.Bounds = new Rectangle(0, 0, 640, 560);
                form.Controls.Add(panel);
                Initialize(form);
                Controls.BackupListControl list = (Controls.BackupListControl)typeof(LayoutBackupPanel).GetField("list", Private).GetValue(panel);
                Control create = (Control)typeof(LayoutBackupPanel).GetField("btnCreate", Private).GetValue(panel);
                IntPtr panelHandle = panel.Handle;   // 宿主未显示：先建面板句柄，否则按钮会挂在停放窗口上，滚轮转不到面板
                List<LayoutBackupInfo> many = new List<LayoutBackupInfo>();
                for (int i = 0; i < 200; i++)
                    many.Add(new LayoutBackupInfo { Id = "20260101-" + i.ToString("000000"), Name = "backup " + i, Created = "2026-01-01 00:00:00" });
                list.SetItems(many, null);
                int lines = SystemInformation.MouseWheelScrollLines;
                if (lines <= 0) lines = 3;
                int step = DpiHelper.Scale(32, form.LayoutDpi) * lines;
                IntPtr oneNotchDown = new IntPtr(-120 << 16);
                int top = list.CardRect(0).Top;
                SendMessage(list.Handle, 0x020A, oneNotchDown, IntPtr.Zero);
                Check(list.CardRect(0).Top == top - step, "wheel over the list does not scroll exactly one step: "
                    + (top - list.CardRect(0).Top) + " instead of " + step);
                SendMessage(create.Handle, 0x020A, oneNotchDown, IntPtr.Zero);
                Check(list.CardRect(0).Top == top - 2 * step, "wheel on a button does not scroll the list exactly one step");
                SendMessage(panel.Handle, 0x020A, oneNotchDown, IntPtr.Zero);
                Check(list.CardRect(0).Top == top - 3 * step, "wheel on the panel does not scroll the list exactly one step");
                Check(panel.ScrollByWheel(120) && list.CardRect(0).Top == top - 2 * step, "settings wheel routing does not scroll the backup list");
                panel.ScrollByWheel(-120);
                SendMessage(list.Handle, 0x020A, new IntPtr(120 << 16), IntPtr.Zero);
                Check(list.CardRect(0).Top == top - 2 * step, "wheel up does not scroll back one step");
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

        private static void RoundedPathNarrow()
        {
            foreach (int width in new int[] { 1, 2, 3, 4 })
            {
                using (System.Drawing.Drawing2D.GraphicsPath path = Draw.RoundedPath(new Rectangle(4, 4, width, 18), 9))
                using (Bitmap bitmap = new Bitmap(32, 32))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                using (SolidBrush brush = new SolidBrush(Color.Red))
                {
                    Check(path.PointCount > 0, "narrow path is empty at width " + width);
                    graphics.FillPath(brush, path);
                }
            }

            // 178px 内容宽度、最大值为 178 时 value=1 会产生 1px 填充，正是红叉的触发条件。
            using (Controls.IosProgressBar bar = new Controls.IosProgressBar())
            {
                bar.Size = new Size(180, 18);
                bar.Maximum = 178;
                bar.Value = 1;
                using (Bitmap bitmap = new Bitmap(bar.Width, bar.Height))
                    bar.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bar.Size));
            }
        }
    }
}
