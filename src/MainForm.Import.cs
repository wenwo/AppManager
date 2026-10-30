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
        private readonly CancellationTokenSource importCancel = new CancellationTokenSource();
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
            public bool Mixed, Executables;
            public readonly Dictionary<string, bool> Same = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        private bool CanImportHere
        {
            get { return !importExitPending && !settingsOpen && !imageViewer.Visible && !textViewer.Visible && !videoPlayer.Visible
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
            probe.Executables = !probe.Folders.Any(f => f) && paths.All(IsSoftwareFile);
            return dragProbe = probe;
        }

        private bool ProbeSameVolume(DragProbe probe, string category, bool resource)
        {
            if (probe.Mixed) return false;
            string key = (resource ? "r|" : "s|") + category;
            bool same;
            if (probe.Same.TryGetValue(key, out same)) return same;
            same = true;
            foreach (bool folder in probe.Folders.Distinct())
            {
                string volume = FileImport.Volume(ImportDirectory(category, resource, folder));
                if (volume == null || !string.Equals(volume, probe.Volumes[0], StringComparison.OrdinalIgnoreCase)) { same = false; break; }
            }
            probe.Same[key] = same;
            return same;
        }

        private DragDropEffects HandleExternalImport(string category, string collection, DragEventArgs e, bool drop)
        {
            try
            {
                if (!CanImportHere || e.Data == null) { HideImportTip(); return DragDropEffects.None; }
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
                bool references = !resource && probe.Executables;
                bool same = !choose && ProbeSameVolume(probe, category, resource);
                DragDropEffects effect = FileImport.ResolveEffect(same, modifiers, e.AllowedEffect);
                if (references) effect = (e.AllowedEffect & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
                if (effect == DragDropEffects.None) { ShowImportTip("来源不允许此操作", e); return effect; }
                string text = choose ? "选择导入位置…" : references ? "添加入口到「" + category + "」"
                    : (effect == DragDropEffects.Move ? "移动到「" : "复制到「") + category + "」";
                if (!references && (e.AllowedEffect & DragDropEffects.Move) == 0) text += "（来源仅可复制）";
                else if (probe.Mixed && (modifiers & (Keys.Control | Keys.Shift)) == 0) text += "（多来源文件，默认复制）";
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
                            CompleteDrop(paths, target, targetCollection, choose, navigate, references, mixed, modifiers, allowed, operation, resource, transfer.Complete);
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
                            CompleteDrop(paths, target, targetCollection, choose, navigate, references, mixed, modifiers, allowed, operation, resource,
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

        private void CompleteDrop(string[] paths, string category, string collection, bool choose, bool navigate, bool references,
            bool mixed, Keys modifiers, DragDropEffects allowed, ImportOperation operation, bool resource, Action<ImportBatch> completed,
            CancellationToken? cancel = null)
        {
            bool queued = false;
            try
            {
                if (choose)
                {
                    category = PickImportCategory(resource);
                    if (category == null) return;
                    collection = null;
                    if (!references)
                    {
                        bool same = !mixed && paths.All(p => FileImport.SameVolume(p, ImportDirectory(category, resource, Directory.Exists(p))));
                        DragDropEffects effect = FileImport.ResolveEffect(same, modifiers, allowed);
                        if (effect == DragDropEffects.None) return;
                        operation = effect == DragDropEffects.Move ? ImportOperation.Move : ImportOperation.Copy;
                    }
                    string action = references ? "添加入口" : operation == ImportOperation.Move ? "移动" : "复制";
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

        private string PickImportCategory(bool resource)
        {
            using (Form dialog = new Form { Text = "导入到…", StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(360, 140) })
            {
                Label label = new Label { Text = "选择目标分类", AutoSize = true, Location = new Point(18, 18) };
                ComboBox choices = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(18, 45), Width = 322 };
                IEnumerable<string> categories = resource ? CategoryRules.ResourceBuiltIn.Concat(config.ResourceCategories)
                    : CategoryRules.BuiltIn.Concat(config.CustomCategories).Concat(new string[] { QuickCenter });
                foreach (string category in categories.Distinct()) choices.Items.Add(category);
                choices.SelectedItem = resource ? "文件" : "其他";
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
                IDataObject data = Clipboard.GetDataObject();
                if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return false;
                string[] paths = data.GetData(DataFormats.FileDrop) as string[];
                if (paths == null || paths.Length == 0) return false;
                ImportOperation operation = FileImport.ClipboardOperation(data.GetData("Preferred DropEffect"));
                if (sequence != GetClipboardSequenceNumber()) throw new IOException("剪贴板已变化，请重新粘贴。");
                bool resource = contentMode == SidebarControl.ContentMode.Website;
                bool choose = SearchActive || !CanEditCategory(currentCategory);
                string category = choose ? PickImportCategory(resource) : currentCategory;
                if (category != null) QueueImport(paths, operation, category, choose ? null : ImportCollectionKey, resource, sequence);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法读取剪贴板，请重试。\n" + ex.Message, "粘贴文件", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return true;
            }
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
                if (importExitPending) ExitApp();
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
            Action<ImportBatch> completed = null)
        {
            QueueImportWithCancellation(paths, operation, category, collection, resource, sequence, completed, importCancel.Token);
        }

        private void QueueImportWithCancellation(string[] paths, ImportOperation operation, string category, string collection, bool resource,
            uint sequence, Action<ImportBatch> completed, CancellationToken cancel)
        {
            // 目标与动作均在投放/粘贴时冻结；队列执行时只重新检查文件和分类是否仍存在。
            ImportBatch batch = new ImportBatch { Category = category, Collection = collection, Resource = resource,
                Operation = operation, ClipboardSequence = sequence, JournalDirectory = ImportJournal };
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                try
                {
                    string source = Path.GetFullPath(path);
                    if (!seen.Add(source)) continue;
                    List<AddedEntry> known = AddedIndexLookup(source);
                    if (known != null && known.Count > 0) source = known[0].Path;
                    bool folder = Directory.Exists(source);
                    string dir = StoreDirectory(category, resource, folder);
                    bool reference = (!resource && !folder && IsSoftwareFile(source)) || known != null || FileImport.Within(dir, source);
                    ImportItem item = new ImportItem { Source = source, Target = dir, Folder = folder, Reference = reference };
                    if (folder && FileImport.Within(source, dir)) { item.State = FileImport.Failed; item.Error = "不能将文件夹导入自身内部。"; }
                    else if (resource && !folder && !MediaRules.MatchCategoryExt(category, source))
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
            if (batch.Items.Count == 0) { if (completed != null) completed(batch); return; }
            EnqueueImport(delegate
            {
                try
                {
                    // 文件名在串行任务开始时预留，避免排队任务使用过期的同名判断。
                    HashSet<string> reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (ImportItem item in batch.Items.Where(i => i.State != FileImport.Failed && i.State != FileImport.Skipped && !i.Reference))
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
                catch (Exception ex) { batch.Error = "导入中断：" + ex.Message; }
                finally
                {
                    RunOnUi(delegate
                    {
                        if (completed != null) completed(batch);
                        FinishImport(batch);
                    });
                }
            });
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
            foreach (ImportItem item in batch.Items.Where(i => i.State == FileImport.Copied || i.State == FileImport.Linked))
            {
                if (SetCategoryExcluded(new AppInfo { Key = "add:" + item.Target }, batch.Category, false)) exclusions.Add(item.Target);
                if (FindAddedEntry(item.Target, batch.Category) < 0)
                {
                    List<AddedEntry> known = AddedIndexLookup(item.Target);
                    if (known != null && known.Any(entry => entry.IsResource == batch.Resource))
                    {
                        if (CollectionOps.AddMember(config, "add:" + item.Target, batch.Category)) categories.Add("add:" + item.Target);
                    }
                    else
                    {
                        AddedEntry entry = new AddedEntry { Path = item.Target, Category = batch.Category, IsFolder = item.Folder, IsResource = batch.Resource };
                        config.AddedItems.Add(entry); added.Add(entry);
                    }
                }
                if (batch.Collection != null && MediaRules.MatchCategoryExt(batch.Category, item.Target)
                    && CollectionOps.AddMember(config, "add:" + item.Target, batch.Collection)) memberships.Add("add:" + item.Target);
            }
            InvalidateAddedIndex();
            bool saved = config.Save();
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

        private void FinishImport(ImportBatch batch)
        {
            bool partial = batch.Error != null || batch.Items.Any(i => i.State == FileImport.Failed || i.State == FileImport.Skipped
                || i.State == FileImport.DeleteFailed || i.State == FileImport.RolledBack);
            if (!importExitPending)
            {
                ShowStatusBar((!batch.Committed ? "入库未保存" : partial ? "导入部分完成" : "导入完成") + " ·「" + batch.Category + "」", batch.Items.Count, batch.Items.Count);
                statusDetail.Text = FileImport.Summary(batch) + " · 点击查看详情";
                MarkImportStatus(partial || !batch.Committed ? 15000 : 4000);
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

        private static bool CanRetryDelete(ImportBatch batch)
        {
            return batch.Committed && batch.Operation == ImportOperation.Move
                && batch.Items.Any(i => !i.Reference && (i.State == FileImport.DeleteFailed || i.State == FileImport.Copied));
        }

        private void ShowImportHistory()
        {
            using (Form dialog = new Form { Text = "导入记录", StartPosition = FormStartPosition.CenterParent, Size = new Size(860, 560) })
            {
                ListBox list = new ListBox { Dock = DockStyle.Top, Height = 120, Enabled = false };
                TextBox details = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
                Button retry = new Button { Dock = DockStyle.Bottom, Height = 38, Text = "删除已入库项的源文件", Enabled = false };
                List<ImportBatch> batches = new List<ImportBatch>();
                string journal = ImportJournal;
                list.Items.Add("正在读取导入记录…");
                // 日志可能很大，后台读取，避免打开记录时卡住界面。
                dialog.Shown += delegate
                {
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        List<ImportBatch> loaded = new List<ImportBatch>();
                        try
                        {
                            if (Directory.Exists(journal))
                                foreach (FileInfo file in new DirectoryInfo(journal).GetFiles("*.xml").OrderByDescending(f => f.LastWriteTimeUtc)
                                    .Where((f, index) => index < 100 || !File.Exists(Path.ChangeExtension(f.FullName, ".complete"))))
                                {
                                    try { loaded.Add(FileImport.Load(file.FullName)); }
                                    catch (Exception ex) { Paths.LogError(ex); }
                                }
                        }
                        catch (Exception ex) { Paths.LogError(ex); }
                        try
                        {
                            dialog.BeginInvoke((Action)delegate
                            {
                                list.Items.Clear();
                                batches.AddRange(loaded);
                                foreach (ImportBatch batch in loaded) list.Items.Add(batch.Category + " · " + FileImport.Summary(batch));
                                if (batches.Count == 0) { list.Items.Add("暂无导入记录"); return; }
                                list.Enabled = true;
                                list.SelectedIndex = 0;
                            });
                        }
                        catch (InvalidOperationException) { }   // 对话框已关闭
                    });
                };
                list.SelectedIndexChanged += delegate
                {
                    if (list.SelectedIndex < 0 || list.SelectedIndex >= batches.Count) { retry.Enabled = false; return; }
                    ImportBatch batch = batches[list.SelectedIndex];
                    details.Text = (batch.Error ?? "") + "\r\n" + string.Join("\r\n\r\n", batch.Items.Select(i => i.State + "：" + i.Source + "\r\n目标：" + i.Target + (string.IsNullOrEmpty(i.Error) ? "" : "\r\n" + i.Error)));
                    retry.Enabled = !importRunning && CanRetryDelete(batch);
                };
                retry.Click += delegate
                {
                    if (list.SelectedIndex < 0 || list.SelectedIndex >= batches.Count) return;
                    ImportBatch batch = batches[list.SelectedIndex];
                    if (!batch.DeleteAuthorized)
                    {
                        // 部分失败的批次当时保留了全部源文件；需用户明确确认后才删除已入库项的源文件。
                        if (MessageBox.Show(dialog, "该批次当时有失败项，因此保留了全部源文件。\n确定删除已成功入库项目的源文件吗？删除前会再次校验内容。",
                            "导入记录", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                        batch.DeleteAuthorized = true;
                    }
                    CancellationToken cancel = importCancel.Token;
                    EnqueueImport(delegate
                    {
                        // 数据库已被恢复/条目已删除时，不按过期日志删源。
                        bool registered = (bool)Invoke(new Func<bool>(delegate
                        {
                            return batch.Items.Where(i => !i.Reference && (i.State == FileImport.Copied || i.State == FileImport.DeleteFailed))
                                .All(i => FindAddedEntry(i.Target, batch.Category) >= 0);
                        }));
                        if (!registered) throw new IOException("入库记录已变化，保留源文件。");
                        FileImport.DeleteSources(batch, cancel); FileImport.Finish(batch); RunOnUi(delegate { FinishImport(batch); });
                    });
                    dialog.Close();
                };
                dialog.Controls.Add(details); dialog.Controls.Add(list); dialog.Controls.Add(retry);
                dialog.ShowDialog(this);
            }
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
                                && !string.IsNullOrEmpty(i.Target) && FindAddedEntry(i.Target, batch.Category) >= 0);
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
