using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace AppManager
{
    /// <summary>
    /// 集合右键「导出…」：选一个文件夹，在其中新建与集合同名的文件夹并复制集合（含子集合）成员的实体文件。
    /// 只复制，不改动原文件和资源库；不能导出到资源存储位置、管理目录或程序数据目录。
    /// 导出排在导入队列里串行执行（不与导入同时写盘，退出时一并询问取消）；底部状态条显示进度，点击可取消。
    /// </summary>
    public partial class MainForm
    {
        private const int ExportPageSize = 500;   // 数据库分页上限
        private const int ExportListLimit = 8;    // 提示里最多列出的条目数

        private CancellationTokenSource exportCancel;   // 非 null：有集合正在导出或排队等待导出
        private string exportName;
        private string exportStatusText;                // 状态条显示这段文字时，点击才是取消导出
        private string exportStatusLine;                // 导出最近一次写到状态条上的文字（含取消中），收尾时据此收起
        private bool exportStatusBound;
        private bool exportStarted;                     // 导出任务已出队开始执行（未开始时前面还有导入在跑）

        private string ExportJournal { get { return Path.Combine(Paths.DataDir, "Exports"); } }

        /// <summary>
        /// 不能作为导出位置的目录：资源存储位置（含已停用的）、管理根及旧版文件/文件夹存储位置、AppManager 数据目录。
        /// 只按配置推算，不建目录。
        /// </summary>
        private List<CollectionExportForbidden> ExportForbiddenRoots()
        {
            List<string> resourceRoots = new List<string>(), legacy = new List<string>(), structural = new List<string>();
            StoreRoots(resourceRoots, legacy, structural);
            List<CollectionExportForbidden> result = new List<CollectionExportForbidden>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in resourceRoots) AddForbidden(result, seen, path, "资源存储位置");
            foreach (string path in structural) AddForbidden(result, seen, path, "资源目录");
            AddForbidden(result, seen, Paths.DataDir, "AppManager 数据目录");
            return result;
        }

        private static void AddForbidden(List<CollectionExportForbidden> list, HashSet<string> seen, string path, string label)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string key = path.TrimEnd('\\', '/');
            if (key.Length == 2 && key[1] == ':') key += "\\";
            if (seen.Add(key)) list.Add(new CollectionExportForbidden { Path = path, Label = label });
        }

        /// <summary>集合右键「导出…」：先选位置（选择窗口里即时拦截禁止的目录），再开始导出。</summary>
        private void ExportCollection(ResourceCollection c)
        {
            if (c == null || ExportBusy() || ExitingRefused()) return;
            string key = CollectionKeys.GetKey(c);
            List<CollectionExportForbidden> forbidden = ExportForbiddenRoots();
            string folderName = CollectionExport.SafeFolderName(c.Name);
            string parent = PathPickerDialog.Pick(new PathPickerOptions
            {
                Title = "导出集合「" + c.Name + "」",
                Hint = "将在所选文件夹中新建「" + folderName + "」，复制集合里的资源，子集合导出为子文件夹。原文件和集合不会改动。",
                Mode = PathPickerMode.Folder,
                ConfirmText = "导出",
                // 不在选择窗口里建文件夹：它只按路径校验，换个写法（联接点、subst 盘）的禁止目录要到后台才查得出，
                // 先建会在禁止目录里留下空文件夹。导出本身会新建「集合名」文件夹。
                AllowCreateFolder = false,
                Validate = delegate(string path) { return CollectionExport.TargetError(path, forbidden); }
            });
            if (parent == null) return;
            StartCollectionExport(key, parent);
        }

        /// <summary>已确认退出、正在等当前文件处理完：不再接新的导出（与导入入口一致）。</summary>
        private bool ExitingRefused()
        {
            if (!importExitPending) return false;
            MessageDialog.Show(this, "正在退出，请等当前操作结束后再试。", "导出集合", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }

        private bool ExportBusy()
        {
            if (exportCancel == null) return false;
            MessageDialog.Show(this, "集合「" + exportName + "」正在导出，请等它完成后再导出其它集合。", "导出集合",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }

        /// <summary>
        /// 选定位置后开始导出（测试直接调用）：排进导入队列，轮到时才按当时的集合与配置取成员
        /// （排在同集合的导入后面时能带上刚导入的文件）；磁盘检查与复制都在后台进行。
        /// </summary>
        private void StartCollectionExport(string key, string parent)
        {
            if (ExportBusy() || ExitingRefused()) return;
            ResourceCollection c = FindCollectionByKey(key);
            if (c == null)
            {
                MessageDialog.Show(this, "这个集合已不存在，未导出。", "导出集合", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string journal = ExportJournal;
            // 与导入共用退出取消：退出或关机时一并取消，已复制的副本撤回后再退出。
            exportCancel = CancellationTokenSource.CreateLinkedTokenSource(importCancel.Token);
            exportName = c.Name;
            exportStarted = false;
            CancellationToken cancel = exportCancel.Token;
            string name = c.Name;
            // 记住集合对象本身：改名、移动都是原地修改这个对象，排队期间改了名也能认出它；只看集合键会认错或认不出。
            ResourceCollection target = c;
            AppConfig owner = config;
            BindExportStatus();
            if (importRunning) ShowNotice("当前导入完成后开始导出「" + name + "」。");
            EnqueueImport(delegate { RunCollectionExport(target, owner, key, name, parent, journal, cancel); });
        }

        /// <summary>
        /// 导出任务出队时（界面线程）按当前状态取计划和禁止目录。按入队时的集合对象找：仍在当前配置里就用它（含改名、移动后的）；
        /// 配置整体换过（还原备份等）才退回按入队时的集合键找。集合已被删除时返回 null。
        /// </summary>
        private ExportStart BeginExportOnUi(ResourceCollection target, AppConfig owner, string key)
        {
            exportStarted = true;
            ResourceCollection c = ReferenceEquals(config, owner)
                ? (config.Collections.Contains(target) ? target : null)
                : FindCollectionByKey(key);
            if (c == null) return null;
            exportName = c.Name;
            return new ExportStart
            {
                Name = c.Name,
                Plan = BuildExportPlan(c),
                Forbidden = ExportForbiddenRoots(),
                Database = config.Database,
                ShowHidden = config.ShowHidden
            };
        }

        private sealed class ExportStart
        {
            internal string Name;
            internal CollectionExportNode Plan;
            internal List<CollectionExportForbidden> Forbidden;
            internal ConfigDatabase Database;
            internal bool ShowHidden;
        }

        /// <summary>导出计划：集合与各级子集合按层级建节点；非数据库模式在这里一次遍历可见资源池填好成员。</summary>
        private CollectionExportNode BuildExportPlan(ResourceCollection c)
        {
            CollectionIndex index = new CollectionIndex(config.Collections);
            Dictionary<string, CollectionExportNode> byKey = new Dictionary<string, CollectionExportNode>(StringComparer.Ordinal);
            CollectionExportNode root = BuildExportNode(c, index, byKey, 0);
            if (config.Database == null)
            {
                // 与网格同口径：父分类里当前可见的资源条目，归属列表含该集合键即为直接成员。
                foreach (AppInfo app in VisibleResourcePool(c.Parent))
                {
                    List<string> categories;
                    if (app == null || string.IsNullOrEmpty(app.Key) || !config.ItemCategories.TryGetValue(app.Key, out categories)) continue;
                    foreach (string category in categories)
                    {
                        CollectionExportNode node;
                        if (category != null && byKey.TryGetValue(category, out node)) AddExportMember(node, app);
                    }
                }
            }
            return root;
        }

        private static CollectionExportNode BuildExportNode(ResourceCollection c, CollectionIndex index,
            Dictionary<string, CollectionExportNode> byKey, int depth)
        {
            string key = CollectionKeys.GetKey(c);
            CollectionExportNode node = new CollectionExportNode { Name = c.Name, Key = key };
            byKey[key] = node;
            if (depth >= CollectionKeys.MaxDepth) return node;   // 层级上限之外的异常数据不再展开，防止环
            foreach (ResourceCollection child in index.Children(key))
            {
                string childKey = CollectionKeys.GetKey(child);
                if (byKey.ContainsKey(childKey) || !string.Equals(child.Parent, c.Parent, StringComparison.Ordinal)) continue;
                node.Children.Add(BuildExportNode(child, index, byKey, depth + 1));
            }
            return node;
        }

        private static void AddExportMember(CollectionExportNode node, AppInfo app)
        {
            string path = ExportCandidate(app);
            if (path == null) node.Unavailable++; else node.Sources.Add(path);
        }

        /// <summary>数据库模式（后台线程）：按集合键用游标分页读出全部直接成员，与网格同样的隐藏项口径。</summary>
        private static void FillExportPlanFromDatabase(CollectionExportNode node, ConfigDatabase database, bool showHidden,
            CancellationToken cancel)
        {
            CatalogPage after = null;
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                CatalogPage page = database.Page(node.Key, true, showHidden, string.Empty, ExportPageSize, after, false, false);
                foreach (AppInfo app in page.Items) AddExportMember(node, app);
                if (page.Items.Count < ExportPageSize) break;
                after = page;
            }
            foreach (CollectionExportNode child in node.Children) FillExportPlanFromDatabase(child, database, showHidden, cancel);
        }

        /// <summary>导入队列里的导出任务（后台线程）。结果统一交给界面线程收尾，窗口已关闭时不再回调。</summary>
        private void RunCollectionExport(ResourceCollection target, AppConfig owner, string key, string name, string parent,
            string journal, CancellationToken cancel)
        {
            CollectionExportResult result = null;
            string error = null;
            bool canceled = false;
            try
            {
                cancel.ThrowIfCancellationRequested();
                ExportStart start;
                try { start = (ExportStart)Invoke(new Func<ExportStart>(delegate { return BeginExportOnUi(target, owner, key); })); }
                catch (ObjectDisposedException) { throw new OperationCanceledException(); }
                catch (InvalidOperationException) { throw new OperationCanceledException(); }
                if (start == null) { error = "集合「" + name + "」已不存在，未导出。"; return; }
                name = start.Name;   // 排队期间改过名时，提示和导出文件夹都用现在的名字
                CollectionExportNode plan = start.Plan;
                RunOnUi(delegate { ShowExportStatus("正在准备导出「" + name + "」…", 0); });
                if (start.Database != null) FillExportPlanFromDatabase(plan, start.Database, start.ShowHidden, cancel);
                int unavailable;
                ImportBatch batch = CollectionExport.Prepare(plan, parent, start.Forbidden, journal, cancel, out unavailable, out error);
                if (batch != null)
                {
                    int total = batch.Items.Count;
                    RunOnUi(delegate { ShowExportStatus("正在导出「" + name + "」…", total); });
                    result = CollectionExport.Execute(batch, KeepPartialExport,
                        delegate(int index, string current) { UpdateStatus(index, total, current); }, cancel);
                    result.Unavailable = unavailable;
                }
            }
            catch (OperationCanceledException) { canceled = true; }
            catch (Exception ex)
            {
                Paths.LogError(ex);
                error = "导出失败：" + ex.Message;
            }
            finally
            {
                RunOnUi(delegate { FinishCollectionExport(name, result, error, canceled); });
            }
        }

        /// <summary>部分失败时（后台线程）问用户：保留已复制的，还是全部撤回。窗口已关闭或无法询问时撤回。</summary>
        private bool KeepPartialExport(ImportBatch batch)
        {
            int copied = batch.Items.Count(i => i.State == FileImport.Copied);
            List<string> failures = batch.Items.Where(i => i.State != FileImport.Copied)
                .Select(i => Path.GetFileName(i.Source.TrimEnd('\\', '/')) + "：" + (i.Error ?? "未知原因")).ToList();
            string text = "保留已导出的 " + copied + " 项吗？\n有 " + failures.Count + " 项未能复制：\n" + ExportList(failures)
                + "\n\n选择「全部撤回」会删除本次导出的副本，原文件不受影响。";
            try
            {
                return (bool)Invoke(new Func<bool>(delegate
                {
                    if (IsDisposed || importExitPending) return false;   // 已确认退出：按撤回处理，不再问
                    // 窗口在托盘里时先恢复：否则确认框没有归属窗口、可能压在别的程序下面，导入队列一直等着
                    if (!Visible) ShowFromTray();
                    return ConfirmDialog.Show("导出集合「" + batch.Category + "」", text, "保留", "全部撤回");
                }));
            }
            catch (ObjectDisposedException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private void ShowExportStatus(string text, int total)
        {
            if (exportCancel == null || exportCancel.IsCancellationRequested) return;
            exportStatusText = text + "  点此取消";
            exportStatusLine = exportStatusText;
            ShowStatusBar(exportStatusText, 0, total);
            statusLabel.Cursor = statusDetail.Cursor = Cursors.Hand;
        }

        private void BindExportStatus()
        {
            if (exportStatusBound) return;
            exportStatusBound = true;
            statusLabel.Click += delegate { OnExportStatusClick(); };
            statusDetail.Click += delegate { OnExportStatusClick(); };
        }

        /// <summary>点击导出进度：确认后取消，已复制的副本撤回。状态条已被别的提示占用时不响应。</summary>
        private void OnExportStatusClick()
        {
            if (exportCancel == null || exportCancel.IsCancellationRequested
                || exportStatusText == null || statusLabel.Text != exportStatusText) return;
            if (!ConfirmDialog.Show("取消导出", "取消导出「" + exportName + "」吗？\n已复制的文件会撤回，原文件不受影响。",
                "取消导出", "继续导出")) return;
            CancelCollectionExport();
        }

        private void CancelCollectionExport()
        {
            if (exportCancel == null || exportCancel.IsCancellationRequested) return;
            exportCancel.Cancel();
            exportStatusText = null;
            exportStatusLine = "正在取消导出，撤回已复制的文件…";
            ShowStatusBar(exportStatusLine, 0, 0);
            statusLabel.Cursor = statusDetail.Cursor = Cursors.Default;
        }

        /// <summary>界面线程收尾：释放导出状态，结果提示交给导入队列收尾时显示。</summary>
        private void FinishCollectionExport(string name, CollectionExportResult result, string error, bool canceled)
        {
            if (exportCancel != null) { exportCancel.Dispose(); exportCancel = null; }
            // 状态条仍是导出自己的进度/取消提示时才收起，别的操作此时写上的提示不动。
            if (exportStatusLine != null && statusBar.Visible && statusLabel.Text == exportStatusLine)
            {
                statusLabel.Cursor = statusDetail.Cursor = Cursors.Default;
                HideStatusBar();
            }
            exportStatusText = exportStatusLine = null;
            exportStarted = false;
            // 结果交给紧随其后的 StartNextImport 显示（同一后台线程先投递本回调、后投递它，顺序有保证）：
            // 队列空了就在 importRunning 复位后用底部提示条，还有任务就在下一项开始后浮出提示。
            pendingQueueReport = delegate { ReportCollectionExport(name, result, error, canceled); };
        }

        private void ReportCollectionExport(string name, CollectionExportResult result, string error, bool canceled)
        {
            const string title = "导出集合";
            if (result == null)
            {
                if (error != null) MessageDialog.Show(this, error, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else if (canceled) ShowNotice("已取消导出「" + name + "」。");
                return;
            }
            string leftovers = result.Leftovers.Count == 0 ? string.Empty
                : "\n\n以下文件或文件夹未能撤回，请手动核对：\n" + ExportList(result.Leftovers);
            string skipped = result.Unavailable > 0 ? "；" + result.Unavailable + " 项已不在原位置，已跳过" : string.Empty;
            if (result.Kept)
            {
                if (result.Failures.Count == 0)
                {
                    ShowNotice("已导出「" + name + "」到「" + result.Root + "」，共 " + result.Copied + " 项" + skipped + "。");
                    return;
                }
                MessageDialog.Show(this, "已导出 " + result.Copied + " 项到「" + result.Root + "」" + skipped + "。\n以下 "
                    + result.Failures.Count + " 项未能导出：\n" + ExportList(result.Failures), title,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // 撤回结果按实际写：有没撤回的就不说「已撤回」，免得用户以为不用再管
            string undone = leftovers.Length == 0 ? "\n已复制的文件已撤回，原文件不受影响。" : "\n原文件不受影响，但有文件没能撤回，见下方清单。";
            string message;
            if (result.Error != null) message = result.Error + (result.Refused ? "\n没有复制任何文件，原文件不受影响。" : undone);
            else if (result.SpaceLimited) message = "磁盘空间不足，导出已停止。" + undone;
            else if (result.Canceled)
            {
                if (leftovers.Length == 0) { ShowNotice("已取消导出「" + name + "」，已复制的文件已撤回。"); return; }
                message = "已取消导出「" + name + "」。";
            }
            else if (result.Declined)
            {
                if (leftovers.Length == 0) { ShowNotice("已撤回导出「" + name + "」，原文件不受影响。"); return; }
                message = "已撤回导出「" + name + "」。";
            }
            else message = "没有文件导出成功：\n" + ExportList(result.Failures);
            MessageDialog.Show(this, message + leftovers, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>
        /// 退出时导入队列里有导出：按是否还有导入一起说明并确认，status 返回取消期间状态条的文字。
        /// 没有导出时返回 null，由调用方按导入处理；用户选择继续时返回 false。
        /// </summary>
        private bool? ConfirmExitWithExport(out string status)
        {
            status = null;
            if (exportCancel == null) return null;
            // 导出还没开始 = 前面有导入在跑；导出已开始而队列不空 = 后面排着导入。
            bool importing = !exportStarted || importQueue.Count > 0;
            bool confirmed = importing
                ? ConfirmDialog.Show("退出", "取消文件导入和集合「" + exportName + "」的导出并退出吗？\n"
                    + "已入库的项目会保留，未完成项目的源文件不会被删除；已导出的副本会撤回，原文件不受影响。", "全部取消并退出", "继续")
                : ConfirmDialog.Show("导出集合", "取消导出「" + exportName + "」并退出吗？\n已复制的文件会撤回，原文件不受影响。",
                    "取消导出并退出", "继续导出");
            status = importing ? "正在取消导入和导出，完成当前文件后退出…" : "正在取消导出，撤回已复制的文件后退出…";
            return confirmed;
        }

        private static string ExportList(List<string> items)
        {
            IEnumerable<string> shown = items.Take(ExportListLimit);
            string text = string.Join("\n", shown);
            if (items.Count > ExportListLimit) text += "\n…等共 " + items.Count + " 项";
            return text;
        }
    }
}
