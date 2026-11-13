using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AppManager
{
    public partial class MainForm
    {
        private const int LocalMediaImportBatchSize = 500;
        private const int LocalMediaCopyBytesPerSecond = 32 * 1024 * 1024;
        private bool localMediaScanRunning;
        private CancellationTokenSource localMediaScanCancel;
        private List<string> lastPhotoScanRoots;
        private List<string> lastVideoScanRoots;

        private void OrganizeLocalPhotos()
        {
            OrganizeLocalMedia("照片", LocalScanScopeKind.Photos, "照片", "张",
                "拍摄时间优先读取 EXIF，无拍摄时间时按文件修改时间归入年份集合。", ref lastPhotoScanRoots);
        }

        private void OrganizeLocalVideos()
        {
            OrganizeLocalMedia("视频", LocalScanScopeKind.Videos, "视频", "个",
                "创建时间优先读取 MP4/MOV/M4V 元数据，无创建时间时按文件修改时间归入年份集合。", ref lastVideoScanRoots);
        }

        private void OrganizeLocalMedia(string category, LocalScanScopeKind kind, string noun, string unit,
            string metadataHint, ref List<string> lastRoots)
        {
            if (localMediaScanRunning) return;
            string title = "整理本机" + noun, scanTitle = "扫描本机" + noun;
            if (!CanEditCategory(category))
            {
                MessageBox.Show(this, "当前无法编辑「" + category + "」分类。", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (ActiveResourceStoreRoots().Count == 0)
            {
                MessageBox.Show(this, "请先在设置 > 存储位置中启用至少一个现有资源存储位置。\n本功能不会新增或修改存储位置。",
                    title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            localMediaScanRunning = true;
            try
            {
                Func<List<string>, Dictionary<string, string>> registered = RegisteredMediaPathsForBackground();
                List<string> exclusions = LocalScanExcludedRoots();
                List<string> roots = null;   // null：需要（重新）选择范围
                while (true)
                {
                    if (roots == null)
                    {
                        using (LocalScanScopeDialog scope = new LocalScanScopeDialog(title, kind, lastRoots, exclusions))
                        {
                            ConfirmDialog.ShowModal(scope);   // 模态 + 遮罩层
                            if (scope.DialogResult != DialogResult.OK) return;
                            roots = new List<string>(scope.ResultRoots);
                        }
                        if (roots.Count == 0) return;
                        lastRoots = new List<string>(roots);
                    }
                    List<string> scanRoots = roots;
                    LocalMediaScanResult result = null;
                    Exception failure = null;
                    bool canceled = false;
                    CancellationTokenSource cancel = new CancellationTokenSource();
                    localMediaScanCancel = cancel;
                    try
                    {
                        using (LocalScanProgressDialog dialog = new LocalScanProgressDialog(scanTitle, "正在准备扫描…",
                            "扫描所选磁盘/文件夹；现有资源存储位置会跳过。", cancel))
                        {
                            dialog.Shown += delegate
                            {
                                ThreadPool.QueueUserWorkItem(delegate
                                {
                                    try
                                    {
                                        Dictionary<string, LocalMediaImportTarget> previous =
                                            LocalMediaScanner.CompletedImportTargets(ImportJournal, registered, cancel.Token);
                                        int lastUpdate = Environment.TickCount;
                                        result = LocalMediaScanner.Scan(category, scanRoots, exclusions, previous, delegate(LocalMediaScanProgress value)
                                        {
                                            int now = Environment.TickCount;
                                            if (unchecked(now - lastUpdate) < 200) return;
                                            lastUpdate = now;
                                            string text = "正在遍历：已检查 " + value.DirectoriesScanned + " 个目录，发现 "
                                                + value.FilesFound + " " + unit + noun + "；预计复制 " + FormatLocalMediaBytes(value.CopyBytes);
                                            dialog.Report(text, value.Path, 0, 0);
                                        }, cancel.Token);
                                    }
                                    catch (OperationCanceledException) { canceled = true; }
                                    catch (Exception ex) { failure = ex; }
                                    finally { dialog.Finish(); }
                                });
                            };
                            ConfirmDialog.ShowModal(dialog);
                        }
                    }
                    finally
                    {
                        localMediaScanCancel = null;
                        canceled = canceled || cancel.IsCancellationRequested;
                        cancel.Dispose();
                    }
                    if (canceled)
                    {
                        ShowNotice("已取消扫描本机" + noun + "，未导入任何文件。");
                        return;
                    }
                    if (failure != null)
                    {
                        Paths.LogError(new IOException("扫描本机" + noun + "失败。", failure));
                        LocalScanResultDecision decision = LocalScanResultDialog.Show(scanTitle,
                            "扫描失败，尚未收录或创建年份集合，源文件未改动。", null, failure.Message, null, false);
                        if (decision.Action == LocalScanResultAction.ChangeScope) { roots = null; continue; }
                        if (decision.Action == LocalScanResultAction.Retry) continue;
                        return;
                    }
                    if (result == null)
                    {
                        LocalScanResultDecision decision = LocalScanResultDialog.Show(scanTitle,
                            "扫描未返回结果，尚未收录或创建年份集合。", null, null, null, false);
                        if (decision.Action == LocalScanResultAction.ChangeScope) { roots = null; continue; }
                        if (decision.Action == LocalScanResultAction.Retry) continue;
                        return;
                    }

                    LocalScanResultDecision importDecision = LocalScanResultDialog.Show(scanTitle,
                        LocalMediaScanHeadline(result, noun, unit), LocalMediaScanStats(result, unit),
                        BuildLocalMediaScanSummary(result, unit, metadataHint),
                        "开始收录", result.Count > 0, result.Count > result.Reused);
                    if (importDecision.Action == LocalScanResultAction.ChangeScope) { roots = null; continue; }
                    if (importDecision.Action == LocalScanResultAction.Retry) continue;
                    if (importDecision.Action != LocalScanResultAction.Import || result.Count == 0) return;
                    List<string> importPaths = result.Years.Values.SelectMany(paths => paths).ToList();
                    ImportDuplicateDecision duplicateDecision = PrecheckImportDuplicates(importPaths, category, true, scanTitle, true);
                    if (duplicateDecision == null) { ShowNotice("已取消本次导入，源文件未改动。"); return; }

                    if (!EnsureLocalMediaCollections(category, noun, result.Years.Keys)) return;

                    foreach (KeyValuePair<int, List<string>> year in result.Years.OrderByDescending(pair => pair.Key))
                    {
                        string collection = CollectionKeys.Make(category, year.Key + "年");
                        foreach (LocalMediaImportPlan plan in LocalMediaScanner.PlanImports(year.Value, importDecision.Operation,
                            LocalMediaImportBatchSize))
                        {
                            QueueImport(plan.Paths, plan.Operation, category, collection, true, 0, null,
                                LocalMediaCopyBytesPerSecond, false, plan.DeleteSuccessfulSourcesOnPartial, duplicateDecision, true);
                        }
                    }
                    RefreshGrid();
                    return;
                }
            }
            finally
            {
                localMediaScanRunning = false;
                localMediaScanCancel = null;
            }
        }

        private List<string> LocalScanExcludedRoots()
        {
            List<string> roots = new List<string>();
            foreach (string root in KnownResourceStoreRoots())
                if (!string.IsNullOrWhiteSpace(root)) roots.Add(root);
            roots.Add(Paths.DataDir);
            return roots;
        }

        /// <summary>结果窗口的一句话结论：有没有新内容、要复制多少。</summary>
        internal static string LocalMediaScanHeadline(LocalMediaScanResult result, string noun, string unit)
        {
            int added = result.Count - result.Reused;
            if (added > 0)
                return "发现 " + added + " " + unit + "新" + noun + "，预计复制 " + FormatLocalMediaBytes(result.CopyBytes) + "。";
            if (result.Reused > 0)
                return "没有新" + noun + "；" + result.Reused + " " + unit + "已收录，可更新年份集合归属。";
            return "没有发现可收录的" + noun + "。";
        }

        /// <summary>结果窗口顶部的关键数字：新增、已收录、云端未下载、读取失败。</summary>
        internal static List<LocalScanStat> LocalMediaScanStats(LocalMediaScanResult result, string unit)
        {
            int failed = result.UnreadableDirectories + result.ReadFailures;
            return new List<LocalScanStat>
            {
                new LocalScanStat("新增", (result.Count - result.Reused) + " " + unit),
                new LocalScanStat("已收录", result.Reused + " " + unit),
                new LocalScanStat("云端未下载", result.CloudFiles + " " + unit),
                new LocalScanStat("读取失败", failed + " 项", failed > 0)
            };
        }

        private static string BuildLocalMediaScanSummary(LocalMediaScanResult result, string unit, string metadataHint)
        {
            StringBuilder text = new StringBuilder();
            text.Append("检查 ").Append(result.DirectoriesScanned).Append(" 个目录，跳过 ")
                .Append(result.DirectoriesSkipped).Append(" 个目录。");
            text.Append("\n预计复制 ").Append(FormatLocalMediaBytes(result.CopyBytes));
            if (result.Reused > 0) text.Append("；另复用 ").Append(FormatLocalMediaBytes(result.Bytes - result.CopyBytes));
            if (result.CleanupPending > 0)
                text.Append("\n已收录复用中有 ").Append(result.CleanupPending)
                    .Append(" 个原文件仍存在（上次移入未完成删源）；本次扫描不会删除，请在导入记录中重试“删除已入库项的源文件”。");
            if (result.UnreadableDirectories > 0 || result.ReadFailures > 0)
            {
                text.Append("\n失败：").Append(result.UnreadableDirectories).Append(" 个不可读目录，")
                    .Append(result.ReadFailures).Append(" 个无法读取的文件。");
                if (result.FailureSamples.Count > 0)
                {
                    text.Append("\n示例：");
                    int count = Math.Min(5, result.FailureSamples.Count);
                    for (int i = 0; i < count; i++) text.Append("\n").Append(result.FailureSamples[i]);
                }
                text.Append("\n可先处理失败项，再选择“重新扫描”。");
            }
            text.Append("\n\n导入时可选择移入资源库（成功项删源、失败项保留）或仅复制保留原件；不会新增或修改存储位置。");
            text.Append("\n").Append(metadataHint);
            text.Append("\n扫描最多约 100 ").Append(unit).Append("/秒，复制限速约 32 MB/s。");
            return text.ToString();
        }

        private Func<List<string>, Dictionary<string, string>> RegisteredMediaPathsForBackground()
        {
            if (config.Database != null)
            {
                string databaseFile = config.Database.FilePath;
                return paths => AddedEntryList.RegisteredPaths(databaseFile, paths, true);
            }
            Dictionary<string, string> byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> byOrigin = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (AddedEntry entry in config.AddedItems)
            {
                if (!entry.IsResource || string.IsNullOrEmpty(entry.Path)) continue;
                byPath[entry.Path] = entry.Path;
                if (!string.IsNullOrEmpty(entry.OriginPath) && !byOrigin.ContainsKey(entry.OriginPath))
                    byOrigin[entry.OriginPath] = entry.Path;
            }
            return delegate(List<string> paths)
            {
                Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in paths)
                {
                    string target;
                    if (byPath.TryGetValue(path, out target) || byOrigin.TryGetValue(path, out target)) result[path] = target;
                }
                return result;
            };
        }

        private bool EnsureLocalMediaCollections(string category, string noun, IEnumerable<int> years)
        {
            List<int> missing = years.Distinct().Where(year => CollectionOps.Find(config, category, year + "年") == null)
                .OrderByDescending(value => value).ToList();
            if (config.Collections.Count + missing.Count > CollectionKeys.MaxCollections)
            {
                MessageBox.Show(this, "集合数量已达上限，尚未开始收录" + noun + "。", "整理本机" + noun,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            foreach (int year in missing)
                config.Collections.Add(new ResourceCollection { Parent = category, Name = year + "年" });
            if (missing.Count == 0) return true;
            if (config.Save(category == "照片" ? "photos.years" : "videos.years")) return true;
            MessageBox.Show(this, "年份集合未能保存，尚未开始收录" + noun + "。\n" + config.DatabaseError,
                "整理本机" + noun, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        private static string FormatLocalMediaBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return value.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
        }
    }
}
