using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AppManager
{
    public partial class MainForm
    {
        private const int LocalPhotoImportBatchSize = 500;
        private const int LocalPhotoCopyBytesPerSecond = 32 * 1024 * 1024;
        private bool localPhotoScanRunning;
        private CancellationTokenSource localPhotoScanCancel;
        private List<string> lastPhotoScanRoots;

        private void OrganizeLocalPhotos()
        {
            if (localPhotoScanRunning) return;
            if (!CanEditCategory("照片"))
            {
                MessageBox.Show(this, "当前无法编辑「照片」分类。", "整理本机照片", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (ActiveResourceStoreRoots().Count == 0)
            {
                MessageBox.Show(this, "请先在设置 > 存储位置中启用至少一个现有资源存储位置。\n本功能不会新增或修改存储位置。",
                    "整理本机照片", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            List<string> roots;
            using (LocalScanScopeDialog scope = new LocalScanScopeDialog("整理本机照片", LocalScanScopeKind.Photos,
                lastPhotoScanRoots, LocalScanExcludedRoots()))
            {
                if (scope.ShowDialog(this) != DialogResult.OK) return;
                roots = new List<string>(scope.ResultRoots);
            }
            if (roots.Count == 0) return;
            lastPhotoScanRoots = new List<string>(roots);

            localPhotoScanRunning = true;
            try
            {
                Func<List<string>, Dictionary<string, string>> registered = RegisteredPhotoPathsForBackground();
                List<string> exclusions = LocalScanExcludedRoots();
                while (true)
                {
                    LocalPhotoScanResult result = null;
                    Exception failure = null;
                    bool canceled = false;
                    CancellationTokenSource cancel = new CancellationTokenSource();
                    localPhotoScanCancel = cancel;
                    Label status, detail;
                    try
                    {
                        using (Form dialog = LocalPhotoProgressDialog(cancel, out status, out detail))
                        {
                            dialog.Shown += delegate
                            {
                                ThreadPool.QueueUserWorkItem(delegate
                                {
                                    try
                                    {
                                        Dictionary<string, string> previous = LocalPhotoScanner.CompletedImportTargets(ImportJournal, registered, cancel.Token);
                                        int lastUpdate = Environment.TickCount;
                                        result = LocalPhotoScanner.Scan(roots, exclusions, previous, delegate(LocalPhotoScanProgress value)
                                        {
                                            int now = Environment.TickCount;
                                            if (unchecked(now - lastUpdate) < 200) return;
                                            lastUpdate = now;
                                            string text = "正在遍历：已检查 " + value.DirectoriesScanned + " 个目录，发现 "
                                                + value.PhotosFound + " 张照片；预计复制 " + FormatLocalPhotoBytes(value.CopyBytes);
                                            UpdateLocalPhotoProgress(dialog, status, detail, text, value.Path);
                                        }, cancel.Token);
                                    }
                                    catch (OperationCanceledException) { canceled = true; }
                                    catch (Exception ex) { failure = ex; }
                                    CloseLocalScanProgressDialog(dialog);
                                });
                            };
                            dialog.ShowDialog(this);
                        }
                    }
                    finally
                    {
                        localPhotoScanCancel = null;
                        canceled = canceled || cancel.IsCancellationRequested;
                        cancel.Dispose();
                    }
                    if (canceled)
                    {
                        ShowNotice("已取消扫描本机照片，未导入任何文件。");
                        return;
                    }
                    if (failure != null)
                    {
                        Paths.LogError(new IOException("扫描本机照片失败。", failure));
                        LocalScanResultAction action = LocalScanResultDialog.Show(this, "扫描本机照片",
                            "扫描失败，尚未收录或创建年份集合，源文件未改动。\n\n" + failure.Message, null, false);
                        if (action == LocalScanResultAction.Retry) continue;
                        return;
                    }
                    if (result == null)
                    {
                        LocalScanResultAction action = LocalScanResultDialog.Show(this, "扫描本机照片",
                            "扫描未返回结果，尚未收录或创建年份集合。", null, false);
                        if (action == LocalScanResultAction.Retry) continue;
                        return;
                    }

                    string summary = BuildLocalPhotoScanSummary(result);
                    LocalScanResultAction decision = LocalScanResultDialog.Show(this, "扫描本机照片", summary,
                        "开始收录", result.Count > 0);
                    if (decision == LocalScanResultAction.Retry) continue;
                    if (decision != LocalScanResultAction.Import || result.Count == 0) return;
                    if (!EnsureLocalPhotoCollections(result.Years.Keys)) return;

                    foreach (KeyValuePair<int, List<string>> year in result.Years.OrderByDescending(pair => pair.Key))
                    {
                        string collection = CollectionKeys.Make("照片", year.Key + "年");
                        for (int offset = 0; offset < year.Value.Count; offset += LocalPhotoImportBatchSize)
                        {
                            string[] paths = year.Value.GetRange(offset, Math.Min(LocalPhotoImportBatchSize, year.Value.Count - offset)).ToArray();
                            QueueImport(paths, ImportOperation.Copy, "照片", collection, true, 0, null, LocalPhotoCopyBytesPerSecond);
                        }
                    }
                    RefreshGrid();
                    return;
                }
            }
            finally
            {
                localPhotoScanRunning = false;
                localPhotoScanCancel = null;
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

        private static string BuildLocalPhotoScanSummary(LocalPhotoScanResult result)
        {
            StringBuilder text = new StringBuilder();
            int added = result.Count - result.Reused;
            text.Append("扫描完成：检查 ").Append(result.DirectoriesScanned).Append(" 个目录，跳过 ")
                .Append(result.DirectoriesSkipped).Append(" 个目录。");
            text.Append("\n新增 ").Append(added).Append(" 张，已收录复用 ").Append(result.Reused)
                .Append(" 张，云端未下载 ").Append(result.CloudFiles).Append(" 张。");
            text.Append("\n预计复制 ").Append(FormatLocalPhotoBytes(result.CopyBytes));
            if (result.Reused > 0) text.Append("；另复用 ").Append(FormatLocalPhotoBytes(result.Bytes - result.CopyBytes));
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
            text.Append("\n\n将保留原照片，复制到当前已启用的资源存储位置；不会新增或修改存储位置。");
            text.Append("\n拍摄时间优先读取 EXIF，无拍摄时间时按文件修改时间归入年份集合。");
            text.Append("\n扫描最多约 100 张/秒，复制限速约 32 MB/s。");
            return text.ToString();
        }

        private static void CloseLocalScanProgressDialog(Form dialog)
        {
            try
            {
                if (dialog.IsDisposed || dialog.Disposing) return;
                if (!dialog.IsHandleCreated) { dialog.Close(); return; }
                dialog.BeginInvoke((Action)delegate { if (!dialog.IsDisposed) dialog.Close(); });
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private Func<List<string>, Dictionary<string, string>> RegisteredPhotoPathsForBackground()
        {
            if (config.Database != null)
            {
                string databaseFile = config.Database.FilePath;
                return paths => AddedEntryList.RegisteredPaths(databaseFile, paths);
            }
            HashSet<string> snapshot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AddedEntry entry in config.AddedItems)
                if (entry.IsResource && !string.IsNullOrEmpty(entry.Path)) snapshot.Add(entry.Path);
            return delegate(List<string> paths)
            {
                Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in paths) if (snapshot.Contains(path)) result[path] = path;
                return result;
            };
        }

        private bool EnsureLocalPhotoCollections(IEnumerable<int> years)
        {
            List<int> missing = years.Distinct().Where(year => CollectionOps.Find(config, "照片", year + "年") == null)
                .OrderByDescending(value => value).ToList();
            if (config.Collections.Count + missing.Count > CollectionKeys.MaxCollections)
            {
                MessageBox.Show(this, "集合数量已达上限，尚未开始收录照片。", "整理本机照片", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            foreach (int year in missing)
                config.Collections.Add(new ResourceCollection { Parent = "照片", Name = year + "年" });
            if (missing.Count == 0) return true;
            if (config.Save("photos.years")) return true;
            MessageBox.Show(this, "年份集合未能保存，尚未开始收录照片。\n" + config.DatabaseError,
                "整理本机照片", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        private static Form LocalPhotoProgressDialog(CancellationTokenSource cancel, out Label status, out Label detail)
        {
            Form dialog = new BufferedDialogForm
            {
                Text = "扫描本机照片", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(520, 170),
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ControlBox = false
            };
            Label statusLabel = new Label { Text = "正在准备扫描…", AutoSize = false, Location = new Point(20, 18), Size = new Size(480, 38), Font = Theme.BodyFont };
            Label detailLabel = new Label { Text = "扫描所选磁盘/文件夹；现有资源存储位置会跳过。", Location = new Point(20, 58), Size = new Size(480, 36), AutoEllipsis = true };
            status = statusLabel;
            detail = detailLabel;
            ProgressBar progress = new ProgressBar { Style = ProgressBarStyle.Marquee, Location = new Point(20, 100), Size = new Size(480, 12) };
            Button stop = new Button { Text = "取消", Location = new Point(425, 128), Size = new Size(75, 28) };
            stop.Click += delegate
            {
                cancel.Cancel();
                stop.Enabled = false;
                statusLabel.Text = "正在取消扫描…";
            };
            dialog.Controls.AddRange(new Control[] { status, detail, progress, stop });
            return dialog;
        }

        private static void UpdateLocalPhotoProgress(Form dialog, Label status, Label detail, string text, string path)
        {
            try
            {
                dialog.BeginInvoke((Action)delegate
                {
                    if (dialog.IsDisposed || dialog.Disposing) return;
                    status.Text = text;
                    detail.Text = path;
                });
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private static string FormatLocalPhotoBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return value.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
        }
    }
}
