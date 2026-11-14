using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using AppManager.Controls;

namespace AppManager
{
    public partial class MainForm
    {
        private readonly Queue<Action> importQueue = new Queue<Action>();
        private bool importRunning, importExitPending, importStatusShown;
        private CancellationTokenSource importCancel = new CancellationTokenSource();
        private readonly ToolTip importTip = new ToolTip();
        private System.Windows.Forms.Timer importStatusTimer;
        private string importTipText;
        private DragProbe dragProbe;
        private string ImportJournal { get { return Path.Combine(Paths.DataDir, "Imports"); } }

        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] private static extern bool EmptyClipboard();
        [DllImport("user32.dll")] private static extern bool CloseClipboard();

        // 一次拖放会话内不变的来源信息；DragOver 高频触发，只在来源变化时重新访问文件系统。
        private sealed class DragProbe
        {
            public object Data;
            public string[] Paths, Volumes;
            public bool[] Folders;
            public bool Mixed;
            public readonly Dictionary<string, bool> Same = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> Mismatched = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        private bool CanImportHere
        {
            get { return !importExitPending && !settingsOpen && !imageViewer.Visible && !textViewer.Visible && !archiveViewer.Visible && !videoPlayer.Visible
                && !musicPlayer.Visible && !collectionInfo.IsEditing; }
        }

        private bool ImportTextFocus
        {
            get
            {
                Control focus = ActiveControl;
                while (focus is ContainerControl && ((ContainerControl)focus).ActiveControl != null)
                    focus = ((ContainerControl)focus).ActiveControl;
                return focus is TextBoxBase || focus is ComboBox || search.ContainsFocus || collectionInfo.IsTextInputFocused;
            }
        }

        private string ImportCollectionKey
        {
            get { return collectionInfo.Visible && !collectionInfo.IsEditing ? currentCollection : ActiveCollectionKey; }
        }

        // 导入专用目标快照；无法创建时明确失败，禁止静默换卷后沿用原移动判定。
        private string ImportDirectory(string category, bool resource, bool folder)
        {
            string root = DesktopOrganizer.NormalizeRoot(config.ManageRootPath);
            if (string.IsNullOrWhiteSpace(root)) root = Directory.Exists(@"H:\") ? @"H:\Manage" : DesktopOrganizer.QuickCenterDir;
            string dir = DesktopOrganizer.NormalizeRoot(resource ? config.ResourceStorePath : folder ? config.FolderStorePath : config.FileStorePath);
            if (string.IsNullOrWhiteSpace(dir)) dir = resource ? Path.Combine(root, "Resources") : folder ? root : Path.Combine(root, "Files");
            return Path.GetFullPath(resource ? Path.Combine(dir, SafeDirName(category)) : dir);
        }

        private string StoreDirectory(string category, bool resource, bool folder)
        {
            return ImportDirectory(category, resource, folder);
        }

        private void BindImportSurface(Control control)
        {
            control.AllowDrop = true;
            control.DragEnter += delegate(object s, DragEventArgs e) { e.Effect = HandleExternalImport(null, null, e, false); };
            control.DragOver += delegate(object s, DragEventArgs e) { e.Effect = HandleExternalImport(null, null, e, false); };
            control.DragDrop += delegate(object s, DragEventArgs e) { e.Effect = HandleExternalImport(null, null, e, true); };
            control.DragLeave += delegate { HideImportTip(); };
        }

        private void HideImportTip() { importTip.Hide(this); importTipText = null; dragProbe = null; }

        private void ShowImportTip(string text, DragEventArgs e)
        {
            if (importTipText == text) return;
            importTipText = text;
            Point point = PointToClient(new Point(e.X, e.Y));
            importTip.Show(text, this, point.X + 18, point.Y + 22, 30000);
        }

        private DragProbe Probe(DragEventArgs e)
        {
            if (dragProbe != null && ReferenceEquals(dragProbe.Data, e.Data)) return dragProbe;
            string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0) return null;
            if (dragProbe != null && dragProbe.Paths.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase))
            {
                dragProbe.Data = e.Data;
                return dragProbe;
            }
            DragProbe probe = new DragProbe { Data = e.Data, Paths = paths, Folders = new bool[paths.Length], Volumes = new string[paths.Length] };
            for (int i = 0; i < paths.Length; i++)
            {
                probe.Folders[i] = Directory.Exists(paths[i]);
                probe.Volumes[i] = FileImport.Volume(paths[i]);
            }
            probe.Mixed = probe.Volumes.Any(v => v == null || !string.Equals(v, probe.Volumes[0], StringComparison.OrdinalIgnoreCase));
            return dragProbe = probe;
        }

        /// <summary>拖入的文件中不符合资源分类类型的数量（按分类缓存，DragOver 高频触发时不重复计算）。</summary>
        private static int MismatchedFiles(DragProbe probe, string category)
        {
            int count;
            if (probe.Mismatched.TryGetValue(category, out count)) return count;
            for (int i = 0; i < probe.Paths.Length; i++)
                if (!MediaRules.FitsCategory(category, probe.Paths[i], probe.Folders[i])) count++;
            probe.Mismatched[category] = count;
            return count;
        }

        private bool ProbeSameVolume(DragProbe probe, string category, bool resource)
        {
            if (probe.Mixed) return false;
            string key = (resource ? "r|" : "s|") + category;
            bool same;
            if (probe.Same.TryGetValue(key, out same)) return same;
            same = resource
                ? ResourceStore.CanMoveWithinSourceVolume(ActiveResourceStoreRoots(), probe.Paths, probe.Folders, category)
                : probe.Folders.Select(f => FileImport.Volume(ImportDirectory(category, false, f)))
                    .All(v => v != null && string.Equals(v, probe.Volumes[0], StringComparison.OrdinalIgnoreCase));
            probe.Same[key] = same;
            return same;
        }

        private DragDropEffects HandleExternalImport(string category, string collection, DragEventArgs e, bool drop)
        {
            try
            {
                if (!CanImportHere || e.Data == null) { HideImportTip(); return DragDropEffects.None; }
                // 本应用拖出的条目又拖回窗口：OLE 拖放仍在网格的鼠标事件内，不在此重入导入流程。
                if (e.Data.GetDataPresent(DragOutFormat))
                {
                    if (drop) HideImportTip(); else ShowImportTip("已在库中，请在窗口内直接拖到分类", e);
                    return DragDropEffects.None;
                }
                // 软件页面只收录软件：外部拖入（含 exe）一律不接收，文件导入只在资源页面进行。
                if (contentMode != SidebarControl.ContentMode.Website)
                {
                    if (drop) HideImportTip(); else ShowImportTip("软件页面不支持拖入，请到「资源」页面导入文件", e);
                    return DragDropEffects.None;
                }
                if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    if (e.Data.GetDataPresent("FileGroupDescriptorW") || e.Data.GetDataPresent("FileGroupDescriptor"))
                        ShowImportTip("请先保存到本地磁盘再导入", e);
                    return DragDropEffects.None;
                }
                DragProbe probe = Probe(e);
                if (probe == null) return DragDropEffects.None;
                bool targeted = !string.IsNullOrEmpty(category);   // 侧栏分类行 / 集合行
                if (category == null && !SearchActive && CanEditCategory(currentCategory))
                { category = currentCategory; collection = ImportCollectionKey; }
                bool resource = contentMode == SidebarControl.ContentMode.Website;
                // e.KeyState 是本次 OLE 事件的键盘快照（8=Ctrl，4=Shift）。
                Keys modifiers = ((e.KeyState & 8) != 0 ? Keys.Control : Keys.None) | ((e.KeyState & 4) != 0 ? Keys.Shift : Keys.None);
                bool choose = string.IsNullOrEmpty(category) || !CanEditCategory(category);
                // 有类型限制的资源分类：拖动经过时就提示类型不符，而不是松手后才跳过（对齐资源管理器的禁止光标）。
                int mismatched = resource && !choose ? MismatchedFiles(probe, category) : 0;
                if (mismatched == probe.Paths.Length)
                {
                    if (drop) HideImportTip();
                    else ShowImportTip(MediaRules.CategoryTypeHint(category), e);
                    return DragDropEffects.None;
                }
                bool same = !choose && ProbeSameVolume(probe, category, resource);
                DragDropEffects effect = FileImport.ResolveEffect(same, modifiers, e.AllowedEffect);
                if (effect == DragDropEffects.None) { ShowImportTip("来源不允许此操作", e); return effect; }
                string text = choose ? "选择导入位置…" : (effect == DragDropEffects.Move ? "移动到「" : "复制到「") + category + "」";
                if ((e.AllowedEffect & DragDropEffects.Move) == 0) text += "（来源仅可复制）";
                else if (probe.Mixed && (modifiers & (Keys.Control | Keys.Shift)) == 0) text += "（多来源文件，默认复制）";
                if (mismatched > 0) text += "（" + mismatched + " 个类型不符的文件将跳过）";
                if (!drop) { ShowImportTip(text, e); return effect; }
                HideImportTip();
                dragProbe = null;
                string[] paths = probe.Paths;
                string target = category, targetCollection = collection;
                bool mixed = probe.Mixed, navigate = targeted && !choose && category != currentCategory;
                DragDropEffects allowed = e.AllowedEffect;
                ImportOperation operation = effect == DragDropEffects.Move ? ImportOperation.Move : ImportOperation.Copy;
                ShellImportDrop transfer = new ShellImportDrop(e.Data);
                if (transfer.Asynchronous)
                {
                    try
                    {
                        BeginInvoke((Action)delegate
                        {
                            CompleteDrop(paths, target, targetCollection, choose, navigate, mixed, modifiers, allowed, operation, resource, transfer.Complete);
                        });
                    }
                    catch { transfer.Complete(null); throw; }
                    return operation == ImportOperation.Copy ? DragDropEffects.Copy : DragDropEffects.None;
                }
                else
                {
                    // 没有异步契约时来源可能在 Drop 返回后立即释放临时文件，必须完成接收再返回。
                    using (CancellationTokenSource receiveCancel = CancellationTokenSource.CreateLinkedTokenSource(importCancel.Token))
                    using (Form waiting = new Form { Text = "正在接收文件…", StartPosition = FormStartPosition.CenterParent,
                        ClientSize = new Size(380, 110), ControlBox = false, FormBorderStyle = FormBorderStyle.FixedDialog })
                    {
                        waiting.Controls.Add(new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                            Text = "请等待文件接收完成，再关闭来源窗口。" });
                        Button cancelReceive = new Button { Dock = DockStyle.Bottom, Height = 32, Text = "取消接收" };
                        cancelReceive.Click += delegate { receiveCancel.Cancel(); cancelReceive.Enabled = false; cancelReceive.Text = "正在取消…"; };
                        waiting.Controls.Add(cancelReceive);
                        waiting.Shown += delegate
                        {
                            CompleteDrop(paths, target, targetCollection, choose, navigate, mixed, modifiers, allowed, operation, resource,
                                delegate(ImportBatch batch) { transfer.Complete(batch); waiting.Close(); }, receiveCancel.Token);
                        };
                        try { waiting.ShowDialog(this); } finally { transfer.Complete(null); }
                    }
                }
                // 不允许来源根据返回 Move 再次删除；实际结果通过 Performed DropEffect / EndOperation 通知。
                return transfer.Result;
            }
            catch (Exception ex)
            {
                if (drop) MessageBox.Show(this, ex.Message, "无法导入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else ShowImportTip("无法读取来源或目标目录", e);
                return DragDropEffects.None;
            }
        }

        private void CompleteDrop(string[] paths, string category, string collection, bool choose, bool navigate,
            bool mixed, Keys modifiers, DragDropEffects allowed, ImportOperation operation, bool resource, Action<ImportBatch> completed,
            CancellationToken? cancel = null)
        {
            bool queued = false;
            try
            {
                if (choose)
                {
                    category = PickImportCategory(resource, paths);
                    if (category == null) return;
                    collection = null;
                    bool same = !mixed && (resource
                        ? ResourceStore.CanMoveWithinSourceVolume(ActiveResourceStoreRoots(), paths,
                            paths.Select(p => Directory.Exists(p)).ToList(), category)
                        : paths.All(p => FileImport.SameVolume(p, ImportDirectory(category, false, Directory.Exists(p)))));
                    DragDropEffects effect = FileImport.ResolveEffect(same, modifiers, allowed);
                    if (effect == DragDropEffects.None) return;
                    operation = effect == DragDropEffects.Move ? ImportOperation.Move : ImportOperation.Copy;
                    string action = operation == ImportOperation.Move ? "移动" : "复制";
                    if (MessageBox.Show(this, action + " " + paths.Length + " 项到「" + category + "」？", "导入文件",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                }
                else if (navigate && CanEditCategory(category)
                    && contentMode == (resource ? SidebarControl.ContentMode.Website : SidebarControl.ContentMode.Software))
                {
                    OnSidebarSelect(category);   // 拖到侧栏其他分类：与原行为一致，投放时切到该分类
                }
                QueueImportWithCancellation(paths, operation, category, collection, resource, 0, completed, cancel ?? importCancel.Token);
                queued = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "无法导入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { if (!queued) completed(null); }
        }

        /// <summary>按待导入内容过滤资源分类：只保留至少能收下一项的分类；全部为同一类型的文件时推荐对应分类。</summary>
        internal static List<string> ResourceImportChoices(IEnumerable<string> categories, IList<string> paths, bool[] folders, ref string preferred)
        {
            Func<string, int, bool> fits = (c, i) => MediaRules.FitsCategory(c, paths[i], folders[i]);
            List<string> result = categories.Where(c => Enumerable.Range(0, paths.Count).Any(i => fits(c, i))).ToList();
            if (folders.Any(f => f)) return result;
            foreach (string suggested in MediaRules.SuggestedResourceCategories)
                if (Enumerable.Range(0, paths.Count).All(i => fits(suggested, i))) { preferred = suggested; break; }
            return result;
        }

        /// <summary>
        /// 选择导入分类。资源模式下按待导入内容过滤：只列出至少能收下一项的分类，
        /// 全部文件都属于同一类型时自动选中对应分类（如全是图片则选「照片」），否则默认「文件」。
        /// </summary>
        private string PickImportCategory(bool resource, IList<string> paths = null, bool[] folders = null)
        {
            using (Form dialog = new Form { Text = "导入到…", StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(360, 140) })
            {
                Label label = new Label { Text = "选择目标分类", AutoSize = true, Location = new Point(18, 18) };
                ComboBox choices = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(18, 45), Width = 322 };
                IEnumerable<string> categories = resource ? CategoryRules.ResourceBuiltIn.Concat(config.ResourceCategories)
                    : CategoryRules.BuiltIn.Concat(config.CustomCategories).Concat(new string[] { QuickCenter });
                string preferred = resource ? "文件" : "其他";
                if (resource && paths != null && paths.Count > 0)
                    categories = ResourceImportChoices(categories, paths, folders ?? paths.Select(p => Directory.Exists(p)).ToArray(), ref preferred);
                foreach (string category in categories.Distinct()) choices.Items.Add(category);
                choices.SelectedItem = preferred;
                if (choices.SelectedIndex < 0 && choices.Items.Count > 0) choices.SelectedIndex = 0;
                Button ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(180, 94) };
                Button cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(266, 94) };
                dialog.Controls.AddRange(new Control[] { label, choices, ok, cancel }); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
                return dialog.ShowDialog(this) == DialogResult.OK ? choices.SelectedItem as string : null;
            }
        }

        private bool ClipboardHasFiles()
        {
            try { return Clipboard.ContainsFileDropList(); } catch (ExternalException) { return false; }
        }

        private bool PasteImportFiles()
        {
            if (!CanImportHere || ImportTextFocus) return false;
            try
            {
                uint sequence = GetClipboardSequenceNumber();
                // 本应用剪切的条目粘贴回应用：按「移动到分类」处理，不再当作外部文件导入。
                if (cutState != null && sequence == cutState.Sequence) { PasteCutItems(); return true; }
                IDataObject data = Clipboard.GetDataObject();
                if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return false;
                string[] paths = data.GetData(DataFormats.FileDrop) as string[];
                if (paths == null || paths.Length == 0) return false;
                bool resource = contentMode == SidebarControl.ContentMode.Website;
                // 软件页面只收录软件：剪贴板里的文件（含 exe）不导入，提示到资源页面粘贴。
                if (!resource) { ShowNotice("软件页面不支持粘贴文件，请到「资源」页面粘贴导入"); return true; }
                ImportOperation operation = FileImport.ClipboardOperation(data.GetData("Preferred DropEffect"));
                if (sequence != GetClipboardSequenceNumber()) throw new IOException("剪贴板已变化，请重新粘贴。");
                bool choose = SearchActive || !CanEditCategory(currentCategory);
                bool[] folders = resource ? paths.Select(p => Directory.Exists(p)).ToArray() : null;
                string category = choose ? PickImportCategory(resource, paths, folders) : currentCategory;
                if (category == null) return true;
                // 与拖放一致：全部类型不符时直接提示，不排队一个注定全部跳过的导入批次
                if (resource && Enumerable.Range(0, paths.Length).All(i => !MediaRules.FitsCategory(category, paths[i], folders[i])))
                {
                    ShowUnsupportedFormats(category, paths);
                    return true;
                }
                ImportDuplicateDecision duplicateDecision = PrecheckImportDuplicates(paths, category, resource, "粘贴文件");
                if (duplicateDecision == null) { ShowNotice("已取消本次导入，源文件未改动。"); return true; }
                QueueImport(paths, operation, category, choose ? null : ImportCollectionKey, resource, sequence,
                    duplicateDecision: duplicateDecision, promptDuplicates: true);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法读取剪贴板，请重试。\n" + ex.Message, "粘贴文件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return true;
            }
        }

        private void ShowUnsupportedFormats(string category, string[] paths)
        {
            string formats = string.Join("、", paths
                .Select(Path.GetExtension)
                .Select(ext => string.IsNullOrEmpty(ext) ? "无扩展名" : ext.ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase));
            string lead = paths.Length == 1 ? "未导入：该文件" : "未导入：" + paths.Length + " 个文件";
            string message = lead + "的格式不符合「" + category + "」分类。\n\n"
                + "不支持：" + formats + "\n\n"
                + "支持格式（不区分大小写）：\n" + MediaRules.CategorySupportedFormats(category);
            MessageBox.Show(this, message, "格式不支持", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void EnqueueImport(Action action)
        {
            importQueue.Enqueue(action);
            if (!importRunning) StartNextImport();
        }

        private void StartNextImport()
        {
            if (importQueue.Count == 0)
            {
                importRunning = false;
                if (importExitPending)
                {
                    // Cancelling imports must not poison this session if the subsequent exit is cancelled.
                    importExitPending = false;
                    importCancel.Dispose();
                    importCancel = new CancellationTokenSource();
                    ExitApp();
                }
                return;
            }
            importRunning = true;
            Action action = importQueue.Dequeue();
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { action(); }
                catch (Exception ex)
                {
                    RunOnUi(delegate { MessageBox.Show(this, "导入中断，未完成的源删除不会自动重试。\n" + ex.Message, "导入文件", MessageBoxButtons.OK, MessageBoxIcon.Warning); });
                }
                finally { RunOnUi(StartNextImport); }
            });
        }

        // 用户退出时导入仍在进行：确认后取消剩余项，当前文件处理完即退出；未完成项的源文件不会被删除。
        private void RequestImportExit()
        {
            if (importExitPending) return;
            if (MessageBox.Show(this, "文件导入尚未完成。\n是否取消剩余项目，并在当前文件处理完后退出？\n已入库的项目会保留，未完成项目的源文件不会被删除。",
                "导入文件", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            importExitPending = true;
            // 已协商异步的来源必须收到结束通知；排队任务用已取消 token 收尾，不能直接丢弃回调。
            importCancel.Cancel();
            ShowStatusBar("正在取消导入，完成当前文件后退出…", 0, 0);
        }

        private void QueueImport(string[] paths, ImportOperation operation, string category, string collection, bool resource, uint sequence,
            Action<ImportBatch> completed = null, int copyBytesPerSecond = 0, bool preserveCategoryExclusions = false,
            bool deleteSuccessfulSourcesOnPartial = false, ImportDuplicateDecision duplicateDecision = null, bool promptDuplicates = true)
        {
            QueueImportWithCancellation(paths, operation, category, collection, resource, sequence, completed, importCancel.Token,
                copyBytesPerSecond, preserveCategoryExclusions, deleteSuccessfulSourcesOnPartial, duplicateDecision, promptDuplicates);
        }

        private void QueueImportWithCancellation(string[] paths, ImportOperation operation, string category, string collection, bool resource,
            uint sequence, Action<ImportBatch> completed, CancellationToken cancel, int copyBytesPerSecond = 0,
            bool preserveCategoryExclusions = false, bool deleteSuccessfulSourcesOnPartial = false,
            ImportDuplicateDecision duplicateDecision = null, bool promptDuplicates = true)
        {
            // 目标与动作均在投放/粘贴时冻结；存储目录也在此算好（只读配置、不建目录）。
            // 路径归属、文件夹内收录检查等预处理放进导入队列在后台执行，批量拖入几千个文件时不卡界面；
            // 后台直接读库（AddedEntryList 实例不是线程安全的）。没有数据库的会话（读取失败/测试）仍在界面线程预处理。
            string folderDir = StoreDirectory(category, resource, true), fileDir = StoreDirectory(category, resource, false);
            List<string> activeRoots = resource ? ActiveResourceStoreRoots() : null;
            List<string> knownRoots = resource ? KnownResourceStoreRoots() : null;
            string databaseFile = config.Database != null ? config.Database.FilePath : null;
            if (databaseFile == null)
            {
                ImportBatch prepared = resource
                    ? PrepareImportBatchMultiStore(paths, operation, category, collection, resource, sequence, folderDir, fileDir,
                        delegate(List<string> list) { return RegisteredPathsInMemory(list, resource); },
                        delegate(string folder) { return config.AddedItems.EntriesUnder(folder).Count > 0; }, knownRoots)
                    : PrepareImportBatch(paths, operation, category, collection, resource, sequence, folderDir, fileDir,
                        delegate(List<string> list) { return RegisteredPathsInMemory(list, resource); },
                        delegate(string folder) { return config.AddedItems.EntriesUnder(folder).Count > 0; });
                prepared.CopyBytesPerSecond = copyBytesPerSecond;
                prepared.PreserveCategoryExclusions = preserveCategoryExclusions;
                prepared.DeleteSuccessfulSourcesOnPartial = deleteSuccessfulSourcesOnPartial && operation == ImportOperation.Move;
                if (prepared.Items.Count == 0) { if (completed != null) completed(prepared); return; }
                EnqueueImport(delegate
                {
                    bool inspected;
                    try
                    {
                        // 无数据库会话的批次可能连续排队：在真正执行时重新取快照，才能看见前一批刚提交的条目。
                        List<AddedEntry> snapshot = (List<AddedEntry>)Invoke(new Func<List<AddedEntry>>(SnapshotAddedEntriesForImport));
                        inspected = InspectPreparedImport(prepared, paths, null, snapshot, duplicateDecision, promptDuplicates, cancel);
                    }
                    catch (OperationCanceledException)
                    {
                        RunOnUi(delegate { if (completed != null) completed(prepared); ShowNotice("已取消本次导入。"); });
                        return;
                    }
                    catch (Exception ex)
                    {
                        FailPreparedImport(prepared, completed, ex);
                        return;
                    }
                    if (!inspected)
                    {
                        RunOnUi(delegate { if (completed != null) completed(prepared); ShowNotice("已取消本次导入。"); });
                        return;
                    }
                    RunPreparedImport(prepared, activeRoots, completed, cancel);
                });
                return;
            }
            EnqueueImport(delegate
            {
                ImportBatch prepared;
                try
                {
                    prepared = resource
                        ? PrepareImportBatchMultiStore(paths, operation, category, collection, resource, sequence, folderDir, fileDir,
                            delegate(List<string> list) { return AddedEntryList.RegisteredPaths(databaseFile, list, resource); },
                            delegate(string folder) { return AddedEntryList.HasEntriesUnder(databaseFile, folder); }, knownRoots)
                        : PrepareImportBatch(paths, operation, category, collection, resource, sequence, folderDir, fileDir,
                            delegate(List<string> list) { return AddedEntryList.RegisteredPaths(databaseFile, list, resource); },
                            delegate(string folder) { return AddedEntryList.HasEntriesUnder(databaseFile, folder); });
                }
                catch (Exception ex)
                {
                    // 读库失败时整批不动任何文件：源全部保留，回调照常送达（异步拖放来源需要收到结束通知）。
                    Paths.LogError(new IOException("导入预处理失败（" + paths.Length + " 项）", ex));
                    ImportBatch failed = new ImportBatch { Category = category, Collection = collection, Resource = resource,
                        Operation = operation, ClipboardSequence = sequence, JournalDirectory = ImportJournal, Error = "导入预处理失败：" + ex.Message };
                    foreach (string path in paths) failed.Items.Add(new ImportItem { Source = path, State = FileImport.Failed, Error = failed.Error });
                    RunOnUi(delegate
                    {
                        if (completed != null) completed(failed);
                        MessageBox.Show(this, "读取收录记录失败，本次导入未处理任何文件，源文件均已保留。\n" + ex.Message, "导入文件",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    });
                    return;
                }
                prepared.CopyBytesPerSecond = copyBytesPerSecond;
                prepared.PreserveCategoryExclusions = preserveCategoryExclusions;
                prepared.DeleteSuccessfulSourcesOnPartial = deleteSuccessfulSourcesOnPartial && operation == ImportOperation.Move;
                bool inspected;
                try
                {
                    inspected = InspectPreparedImport(prepared, paths, databaseFile, null, duplicateDecision, promptDuplicates, cancel);
                }
                catch (OperationCanceledException)
                {
                    RunOnUi(delegate { if (completed != null) completed(prepared); ShowNotice("已取消本次导入。"); });
                    return;
                }
                catch (Exception ex)
                {
                    FailPreparedImport(prepared, completed, ex);
                    return;
                }
                if (!inspected)
                {
                    RunOnUi(delegate { if (completed != null) completed(prepared); ShowNotice("已取消本次导入。"); });
                    return;
                }
                if (prepared.Items.Count == 0) { RunOnUi(delegate { if (completed != null) completed(prepared); }); return; }
                RunPreparedImport(prepared, activeRoots, completed, cancel);
            });
        }

        private bool InspectPreparedImport(ImportBatch batch, string[] paths, string databaseFile, IList<AddedEntry> entries,
            ImportDuplicateDecision decision, bool prompt, CancellationToken cancel)
        {
            ImportDuplicateContext context = new ImportDuplicateContext
            {
                DatabaseFile = databaseFile,
                MemoryEntries = entries,
                Decision = decision,
                Confirm = prompt ? new Func<ImportDuplicateReport, ICollection<string>>(ConfirmImportDuplicates) : null
            };
            return ImportDuplicateInspector.InspectAndApply(batch, paths, batch.Category, batch.Resource, context, cancel);
        }

        private void FailPreparedImport(ImportBatch batch, Action<ImportBatch> completed, Exception ex)
        {
            string message = "重复检查失败，本次未导入任何文件，源文件均已保留。\n" + ex.Message;
            Paths.LogError(new IOException("导入重复检查失败（" + batch.Items.Count + " 项）", ex));
            batch.Error = message;
            foreach (ImportItem item in batch.Items)
                if (item != null && item.State == FileImport.Waiting)
                {
                    item.State = FileImport.Failed;
                    item.Error = message;
                }
            RunOnUi(delegate
            {
                if (completed != null) completed(batch);
                MessageBox.Show(this, message, "导入文件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            });
        }

        private ICollection<string> ConfirmImportDuplicates(ImportDuplicateReport report)
        {
            if (IsDisposed || !IsHandleCreated) return null;
            try
            {
                return (ICollection<string>)Invoke(new Func<ICollection<string>>(delegate
                { return ImportDuplicateDialog.Show(this, report); }));
            }
            catch (ObjectDisposedException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        private List<AddedEntry> SnapshotAddedEntriesForImport()
        {
            List<AddedEntry> result = new List<AddedEntry>();
            foreach (AddedEntry entry in config.AddedItems)
            {
                if (entry == null) continue;
                result.Add(new AddedEntry { Path = entry.Path, Category = entry.Category, Name = entry.Name,
                    OriginPath = entry.OriginPath, IsFolder = entry.IsFolder, IsResource = entry.IsResource });
            }
            return result;
        }

        /// <summary>扫描结果批量导入前只提示一次；最终每个批次仍会重新复核，并沿用这里的覆盖/跳过决定。</summary>
        private ImportDuplicateDecision PrecheckImportDuplicates(IEnumerable<string> paths, string category, bool resource, string title,
            bool quietKnownCurrent = false)
        {
            if (paths == null) return new ImportDuplicateDecision();
            List<string> snapshot = paths.Where(path => !string.IsNullOrEmpty(path)).ToList();
            if (snapshot.Count == 0) return new ImportDuplicateDecision();
            try
            {
                ImportDuplicateContext context = new ImportDuplicateContext
                {
                    DatabaseFile = config.Database != null ? config.Database.FilePath : null,
                    MemoryEntries = config.Database == null ? SnapshotAddedEntriesForImport() : null
                };
                ImportDuplicateReport report = ImportDuplicateDialog.InspectWithProgress(this, title, snapshot, category, resource, context);
                if (report == null) return null;
                if (!report.NeedsPrompt) return ImportDuplicateDecision.FromReport(report);
                // 扫描结果里的“已收录复用”按原样回到库中，不把它当成用户本次新选的重复项反复弹窗。
                if (quietKnownCurrent && report.Findings.All(finding => finding.Kind == ImportDuplicateKind.ExistingPathCurrent))
                    return ImportDuplicateDecision.FromReport(report);
                ICollection<string> keep = ImportDuplicateDialog.Show(this, report);
                if (keep == null) return null;
                HashSet<string> set = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
                foreach (ImportDuplicateFinding finding in report.Findings)
                    if (finding.CanKeep && !string.IsNullOrEmpty(finding.Source)) finding.Keep = set.Contains(finding.Source);
                return ImportDuplicateDecision.FromReport(report);
            }
            catch (Exception ex)
            {
                Paths.LogError(new IOException("导入重复检查失败（" + snapshot.Count + " 项）", ex));
                MessageBox.Show(this, "重复检查失败，本次未导入任何文件，源文件均已保留。\n" + ex.Message,
                    title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
        }

        /// <summary>无数据库会话：用内存索引查已登记路径（只能在界面线程调用）。</summary>
        private Dictionary<string, string> RegisteredPathsInMemory(List<string> paths, bool resource)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, List<AddedEntry>> kv in AddedIndexLookups(paths, true))
            {
                AddedEntry match = kv.Value == null ? null : kv.Value.FirstOrDefault(entry => entry.IsResource == resource);
                if (match != null) result[kv.Key] = match.Path;
            }
            return result;
        }

        /// <summary>
        /// 生成导入批次：规范化路径、识别已登记的引用项、检查文件夹自身嵌套与内部已收录项、资源类型过滤，
        /// 并去掉被同批外层目录包含的条目。不访问界面控件与 AddedEntryList 实例，查库方式由调用方注入。
        /// </summary>
        private ImportBatch PrepareImportBatch(string[] paths, ImportOperation operation, string category, string collection, bool resource,
            uint sequence, string folderDir, string fileDir, Func<List<string>, Dictionary<string, string>> registeredPaths, Func<string, bool> hasEntriesUnder)
        {
            return PrepareImportBatchCore(paths, operation, category, collection, resource, sequence, folderDir, fileDir,
                registeredPaths, hasEntriesUnder, null);
        }

        private ImportBatch PrepareImportBatchMultiStore(string[] paths, ImportOperation operation, string category, string collection, bool resource,
            uint sequence, string folderDir, string fileDir, Func<List<string>, Dictionary<string, string>> registeredPaths,
            Func<string, bool> hasEntriesUnder, List<string> knownRoots)
        {
            return PrepareImportBatchCore(paths, operation, category, collection, resource, sequence, folderDir, fileDir,
                registeredPaths, hasEntriesUnder, knownRoots);
        }

        private ImportBatch PrepareImportBatchCore(string[] paths, ImportOperation operation, string category, string collection, bool resource,
            uint sequence, string folderDir, string fileDir, Func<List<string>, Dictionary<string, string>> registeredPaths,
            Func<string, bool> hasEntriesUnder, List<string> knownRoots)
        {
            ImportBatch batch = new ImportBatch { Category = category, Collection = collection, Resource = resource,
                Operation = operation, ClipboardSequence = sequence, JournalDirectory = ImportJournal };
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> lookupPaths = new List<string>();
            HashSet<string> lookupSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                try
                {
                    string source = Path.GetFullPath(path);
                    if (lookupSeen.Add(source)) lookupPaths.Add(source);
                }
                catch { }
            }
            Dictionary<string, string> knownPaths = registeredPaths(lookupPaths);
            foreach (string path in paths)
            {
                try
                {
                    string source = Path.GetFullPath(path), origin = source;
                    if (!seen.Add(source)) continue;
                    string registered;
                    bool known = knownPaths.TryGetValue(source, out registered);
                    if (known) source = registered;
                    if (known && !File.Exists(source) && !Directory.Exists(source))
                    { known = false; source = origin; }
                    bool folder = Directory.Exists(source);
                    string dir = folder ? folderDir : fileDir;
                    bool reference = (!resource && !folder && IsSoftwareFile(source)) || known
                        || (resource ? ResourceStore.IsUnderAny(knownRoots, source) : FileImport.Within(dir, source));
                    ImportItem item = new ImportItem { Source = source, Target = dir, Origin = origin, Folder = folder, Reference = reference };
                    // 软件页面只登记程序（原位引用），不复制、不移动任何文件或文件夹。
                    if (!resource && (folder || !IsSoftwareFile(source)))
                    { item.State = FileImport.Skipped; item.Error = "软件页面只登记程序（.exe），文件和文件夹请到资源页面导入。"; }
                    else if (!resource && folder && FileImport.Within(source, dir)) { item.State = FileImport.Failed; item.Error = "不能将文件夹导入自身内部。"; }
                    // 移动会让文件夹内已收录的项目全部指向旧路径而失效；复制不影响它们。
                    else if (folder && !reference && operation == ImportOperation.Move && hasEntriesUnder(source))
                    { item.State = FileImport.Failed; item.Error = "文件夹内包含已收录的项目，不能移动；请改用复制（拖放时按住 Ctrl）。"; }
                    else if (resource && !MediaRules.FitsCategory(category, source, folder))
                    { item.State = FileImport.Skipped; item.Error = "文件类型不符合目标分类。"; }
                    batch.Items.Add(item);
                }
                catch (Exception ex) { batch.Items.Add(new ImportItem { Source = path, State = FileImport.Failed, Error = ex.Message }); }
            }
            // 同一批同时选择目录及其内部文件时，只处理外层目录。
            HashSet<string> folders = new HashSet<string>(batch.Items.Where(i => i.Folder && i.State != FileImport.Failed)
                .Select(i => i.Source.TrimEnd('\\', '/')), StringComparer.OrdinalIgnoreCase);
            batch.Items = batch.Items.Where(i =>
            {
                if (i.State == FileImport.Failed) return true;
                string parent = Path.GetDirectoryName(i.Source.TrimEnd('\\', '/'));
                while (!string.IsNullOrEmpty(parent))
                { if (folders.Contains(parent.TrimEnd('\\', '/'))) return false; parent = Path.GetDirectoryName(parent); }
                return true;
            }).ToList();
            return batch;
        }

        /// <summary>每个文件/文件夹在真正复制前选盘；磁盘满换盘时也用这个入口，文件夹不会被拆开。</summary>
        private Func<ImportItem, string> CreateResourceTargetResolver(ImportBatch batch, List<string> roots, CancellationToken cancel)
        {
            Dictionary<ImportItem, HashSet<string>> triedByItem = new Dictionary<ImportItem, HashSet<string>>();
            HashSet<string> reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return delegate(ImportItem item)
            {
                if (roots == null || roots.Count == 0)
                {
                    item.Error = "未启用任何资源存储位置，请在设置 > 存储位置中启用至少一个位置。";
                    return null;
                }
                HashSet<string> tried;
                if (!triedByItem.TryGetValue(item, out tried))
                    triedByItem[item] = tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(item.TargetRoot)) tried.Add(item.TargetRoot);

                string root = null, dir = null, error = null;
                // 同卷移动不占新空间，先只尝试同卷位置；成功时无需扫描文件夹大小。
                if (batch.Operation == ImportOperation.Move)
                    root = ResourceStore.ChooseRoot(roots, item.Source, item.Folder, batch.Category,
                        batch.Operation, long.MaxValue, tried, out dir, out error);

                if (root == null)
                {
                    if (!item.SizeKnown)
                    {
                        long size;
                        if (!ResourceStore.TryGetSize(item.Source, item.Folder, cancel, out size, out error))
                        {
                            item.Error = error;
                            return null;
                        }
                        item.RequiredBytes = size;
                        item.SizeKnown = true;
                    }
                    root = ResourceStore.ChooseRoot(roots, item.Source, item.Folder, batch.Category,
                        batch.Operation, item.RequiredBytes, tried, out dir, out error);
                }
                if (root == null)
                {
                    item.Error = string.IsNullOrEmpty(error) ? ResourceStore.CapacityMessage : error;
                    return null;
                }

                item.TargetRoot = root;
                string name = Path.GetFileName(item.Source.TrimEnd('\\', '/'));
                string target = Path.Combine(dir, name);
                int suffix = 1;
                while (reserved.Contains(target) || File.Exists(target) || Directory.Exists(target))
                    target = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + "_" + suffix++ + Path.GetExtension(name));
                reserved.Add(target);
                item.Error = null;
                return target;
            };
        }

        /// <summary>在导入队列（后台线程）中执行已生成的批次：预留目标名、复制/移动、提交入库，结束后回到界面线程收尾。</summary>
        private void RunPreparedImport(ImportBatch batch, List<string> resourceRoots, Action<ImportBatch> completed, CancellationToken cancel)
        {
            try
            {
                if (batch.Resource)
                    batch.ResolveTarget = CreateResourceTargetResolver(batch, resourceRoots, cancel);
                // 文件名在串行任务开始时预留，避免排队任务使用过期的同名判断。
                HashSet<string> reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ImportItem item in batch.Items.Where(i => i.State != FileImport.Failed && i.State != FileImport.Skipped && !i.Reference
                    && batch.ResolveTarget == null))
                {
                    string dir = item.Target, name = Path.GetFileName(item.Source.TrimEnd('\\', '/'));
                    string target = Path.Combine(dir, name); int suffix = 1;
                    while (reserved.Contains(target) || File.Exists(target) || Directory.Exists(target))
                        target = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + "_" + suffix++ + Path.GetExtension(name));
                    reserved.Add(target); item.Target = target;
                }
                RunOnUi(delegate { ShowStatusBar("正在导入「" + batch.Category + "」…", 0, batch.Items.Count); });
                FileImport.Run(batch, delegate(ImportBatch value)
                { return (bool)Invoke(new Func<bool>(delegate { return CommitImport(value); })); },
                    delegate(int index, string name) { UpdateStatus(index, batch.Items.Count, name); }, cancel);
            }
            catch (Exception ex)
            {
                batch.Error = "导入中断：" + ex.Message;
                FileImport.RecordError(batch, ex);
            }
            finally
            {
                RunOnUi(delegate
                {
                    if (completed != null) completed(batch);
                    FinishImport(batch);
                });
            }
        }

        private bool CommitImport(ImportBatch batch)
        {
            bool valid = batch.Resource ? CategoryRules.ResourceBuiltIn.Contains(batch.Category) || config.ResourceCategories.Contains(batch.Category)
                : CategoryRules.BuiltIn.Contains(batch.Category) || config.CustomCategories.Contains(batch.Category) || batch.Category == QuickCenter;
            if (!valid) return false;
            if (batch.Collection != null && FindCollectionByKey(batch.Collection) == null)
            {
                // 排队期间集合被删除：仍入库到分类，只放弃集合归属，避免整批副本无人登记。
                batch.Collection = null;
                batch.Error = "目标集合已不存在，仅加入分类。";
            }
            List<AddedEntry> added = new List<AddedEntry>();
            List<string> memberships = new List<string>();
            List<string> categories = new List<string>();
            List<string> exclusions = new List<string>();
            List<ImportItem> committed = batch.Items.Where(i => i.State == FileImport.Copied || i.State == FileImport.Linked).ToList();
            Dictionary<string, List<AddedEntry>> knownTargets = AddedIndexLookups(committed.Select(i => i.Target));
            foreach (ImportItem item in committed)
            {
                if (!batch.PreserveCategoryExclusions
                    && SetCategoryExcluded(new AppInfo { Key = "add:" + item.Target }, batch.Category, false)) exclusions.Add(item.Target);
                List<AddedEntry> known;
                knownTargets.TryGetValue(item.Target, out known);
                if (known != null && known.Count == 0) known = null;
                bool alreadyInCategory = AddedTargetInCategory(item.Target, batch.Category, batch.Resource, known);
                if (!alreadyInCategory)
                {
                    if (known != null && known.Any(entry => entry.IsResource == batch.Resource))
                    {
                        if (CollectionOps.AddMember(config, "add:" + item.Target, batch.Category)) categories.Add("add:" + item.Target);
                    }
                    else
                    {
                        AddedEntry entry = new AddedEntry { Path = item.Target, Category = batch.Category, IsFolder = item.Folder,
                            IsResource = batch.Resource, OriginPath = batch.Resource && batch.Operation == ImportOperation.Copy && !item.Reference
                                ? item.Origin ?? item.Source : null };
                        config.AddedItems.Add(entry); added.Add(entry);
                    }
                }
                if (batch.Collection != null && MediaRules.FitsCategory(batch.Category, item.Target, item.Folder)
                    && CollectionOps.AddMember(config, "add:" + item.Target, batch.Collection)) memberships.Add("add:" + item.Target);
            }
            InvalidateAddedIndex();
            bool saved = config.Save("physical.files.import");
            if (!saved)
            {
                foreach (AddedEntry entry in added) config.AddedItems.Remove(entry);
                foreach (string key in memberships) CollectionOps.RemoveMember(config, key, batch.Collection);
                foreach (string key in categories) CollectionOps.RemoveMember(config, key, batch.Category);
                foreach (string path in exclusions) SetCategoryExcluded(new AppInfo { Key = "add:" + path }, batch.Category, true);
                InvalidateAddedIndex();
            }
            return saved;
        }

        /// <summary>目标在指定分类中是否有效：同时考虑附加归属与分类覆盖，不能只看 added_items 的原始分类。</summary>
        private bool AddedTargetInCategory(string target, string category, bool resource, List<AddedEntry> known)
        {
            string key = "add:" + target;
            List<string> extras;
            if (config.ItemCategories.TryGetValue(key, out extras) && extras.Contains(category)) return true;
            string overridden;
            if (config.CategoryOverride.TryGetValue(key, out overridden) && !string.IsNullOrEmpty(overridden))
            {
                if (overridden == category) return true;
                if (IsResourceCategoryName(overridden) == IsResourceCategoryName(category)) return false;
            }
            return known != null && known.Any(entry => entry.IsResource == resource && entry.Category == category);
        }

        private void FinishImport(ImportBatch batch)
        {
            bool partial = batch.Error != null || batch.Items.Any(i => i.State == FileImport.Failed || i.State == FileImport.Skipped
                || i.State == FileImport.DeleteFailed || i.State == FileImport.RolledBack);
            if (!importExitPending)
            {
                ShowStatusBar((!batch.Committed ? "入库未保存" : partial ? "导入部分完成" : "导入完成") + " ·「" + batch.Category + "」", batch.Items.Count, batch.Items.Count);
                statusDetail.Text = FileImport.Summary(batch) + " · 点击查看详情";
                MarkImportStatus(partial || !batch.Committed ? 15000 : 4000);
                if (batch.SpaceLimited)
                {
                    bool shared = batch.Items.Any(i => i.Files.Any(f => f.Linked && !f.Deleted));
                    MessageBox.Show(this, FileImport.SpaceMessage + (shared
                        ? "\n\n部分源路径和库路径仍可能指向同一份文件，编辑一边会影响另一边，请暂勿编辑。详情见导入记录。" : ""),
                        "磁盘空间不足", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            // 不切换用户当前视图：只做静默重扫，正在查看的图片/视频不被打断。
            if (batch.Committed) StartScan(false, true);
            if (batch.Operation == ImportOperation.Move && batch.ClipboardSequence != 0
                && batch.Items.All(i => i.State == FileImport.Moved) && batch.Committed && OpenClipboard(Handle))
            {
                // 持有剪贴板锁后再比较序号，避免检查与清空之间覆盖用户刚复制的内容。
                try { if (GetClipboardSequenceNumber() == batch.ClipboardSequence) EmptyClipboard(); }
                finally { CloseClipboard(); }
            }
        }

        // 导入结果在状态栏停留片刻后自动收起；仅此期间点击状态栏打开导入记录。
        private void MarkImportStatus(int milliseconds)
        {
            importStatusShown = true;
            statusLabel.Cursor = statusDetail.Cursor = Cursors.Hand;
            if (importStatusTimer == null)
            {
                importStatusTimer = new System.Windows.Forms.Timer();
                importStatusTimer.Tick += delegate { importStatusTimer.Stop(); if (importStatusShown) HideStatusBar(); };
            }
            importStatusTimer.Stop();
            importStatusTimer.Interval = milliseconds;
            importStatusTimer.Start();
        }

        /// <summary>状态栏被其他操作占用或收起时调用：结束导入结果的停留与点击行为。</summary>
        private void ResetImportStatus()
        {
            if (!importStatusShown) return;
            importStatusShown = false;
            if (importStatusTimer != null) importStatusTimer.Stop();
            statusLabel.Cursor = statusDetail.Cursor = Cursors.Default;
        }

        /// <summary>该项已入库、但按「移入」本应删除的原件还在（删除失败，或整批因部分失败而保留了原件）。</summary>
        internal static bool AwaitsSourceDelete(ImportBatch batch, ImportItem item)
        {
            return batch.Committed && batch.Operation == ImportOperation.Move
                && !item.Reference && (item.State == FileImport.DeleteFailed || item.State == FileImport.Copied);
        }

        internal static bool CanRetryDelete(ImportBatch batch)
        {
            return batch.Items.Any(i => AwaitsSourceDelete(batch, i));
        }

        private void ShowImportHistory()
        {
            ImportBatch batch;
            using (ImportHistoryDialog dialog = new ImportHistoryDialog(ImportJournal, delegate { return importRunning; }))
            {
                ConfirmDialog.ShowModal(dialog);   // 模态 + 遮罩层
                batch = dialog.RetryBatch;
            }
            if (batch == null) return;
            CancellationToken cancel = importCancel.Token;
            EnqueueImport(delegate
            {
                // 数据库已被恢复/条目已删除时，不按过期日志删源。
                bool registered = (bool)Invoke(new Func<bool>(delegate
                {
                    return batch.Items.Where(i => !i.Reference && (i.State == FileImport.Copied || i.State == FileImport.DeleteFailed))
                        .All(i => IsBatchTargetRegistered(batch, i.Target));
                }));
                if (!registered) throw new IOException("入库记录已变化，保留源文件。");
                FileImport.DeleteSources(batch, cancel); FileImport.Finish(batch); RunOnUi(delegate { FinishImport(batch); });
            });
        }

        /// <summary>
        /// 批次的入库路径是否已登记。软件页面的旧批次条目已迁到资源页面、分类已变，改按路径是否仍有收录判断
        /// （非引用项的入库路径是本批次新预留的，不会与其它条目重合）；否则会把已入库的副本当成未入库撤回。
        /// </summary>
        private bool IsBatchTargetRegistered(ImportBatch batch, string target)
        {
            return FindAddedEntry(target, batch.Category) >= 0 || (!batch.Resource && IsAddedAnywhere(target));
        }

        // 启动时处理上次中断的导入：清理临时落点、撤回未入库副本；同时限制日志数量。
        private void RecoverImports()
        {
            string journal = ImportJournal;
            EnqueueImport(delegate
            {
                foreach (string marker in FileImport.PendingJournals(journal))
                {
                    ImportBatch batch;
                    try { batch = FileImport.Load(Path.ChangeExtension(marker, ".xml")); }
                    catch (Exception ex) { Paths.LogError(ex); FileImport.DropMarker(marker); continue; }
                    try
                    {
                        // 新预留的入库路径已登记 = 中断发生在提交之后，不能撤回。
                        bool registered = !batch.Committed && (bool)Invoke(new Func<bool>(delegate
                        {
                            return batch.Items.Any(i => !i.Reference && i.State != FileImport.Failed && i.State != FileImport.Skipped
                                && !string.IsNullOrEmpty(i.Target) && IsBatchTargetRegistered(batch, i.Target));
                        }));
                        FileImport.Recover(batch, registered);
                    }
                    catch (Exception ex) { Paths.LogError(ex); }
                }
                try { FileImport.Prune(journal, 200); }
                catch (Exception ex) { Paths.LogError(ex); }
            });
        }

        private void InitializeImportUi()
        {
            BindImportSurface(headerStrip); BindImportSurface(collectionInfo);
            grid.DragLeave += delegate { HideImportTip(); };
            sidebar.DragLeave += delegate { HideImportTip(); };
            collectionSidebar.DragLeave += delegate { HideImportTip(); };
            statusLabel.Click += delegate { if (importStatusShown) ShowImportHistory(); };
            statusDetail.Click += delegate { if (importStatusShown) ShowImportHistory(); };
            if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                importTip.SetToolTip(addBtn, "当前以管理员身份运行，资源管理器可能无法拖入；请以普通权限启动。仍可使用粘贴文件。");
        }
    }
}
