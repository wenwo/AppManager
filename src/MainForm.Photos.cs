using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
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
            List<string> roots = LocalPhotoScanner.FixedRoots();
            if (roots.Count == 0)
            {
                MessageBox.Show(this, "未找到可读取的本地固定磁盘。", "整理本机照片", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string prompt = "将只读扫描所有本地固定磁盘中的照片文件，现有资源存储位置会跳过。\n"
                + "扫描后可确认数量，再复制到当前已启用的存储位置；源照片保留，存储设置不会改变。\n\n"
                + "全盘扫描可能需要几分钟，可随时取消。是否开始？";
            if (MessageBox.Show(this, prompt, "整理本机照片", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            localPhotoScanRunning = true;
            Func<List<string>, Dictionary<string, string>> registered = RegisteredPhotoPathsForBackground();
            LocalPhotoScanResult result = null;
            Exception failure = null;
            CancellationTokenSource cancel = new CancellationTokenSource();
            localPhotoScanCancel = cancel;
            Label status, detail;
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
                            result = LocalPhotoScanner.Scan(roots, KnownResourceStoreRoots().Concat(new[] { Paths.DataDir }), previous, delegate(int count, string path)
                            {
                                int now = Environment.TickCount;
                                if (unchecked(now - lastUpdate) < 200) return;
                                lastUpdate = now;
                                UpdateLocalPhotoProgress(dialog, status, detail, "已发现 " + count + " 张照片，正在扫描…", path);
                            }, cancel.Token);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { failure = ex; }
                        try
                        {
                            dialog.BeginInvoke((Action)delegate
                            {
                                if (dialog.IsDisposed || dialog.Disposing) return;
                                dialog.DialogResult = result != null ? DialogResult.OK : DialogResult.Cancel;
                            });
                        }
                        catch (ObjectDisposedException) { }
                        catch (InvalidOperationException) { }
                    });
                };
                dialog.ShowDialog(this);
            }
            localPhotoScanRunning = false;
            localPhotoScanCancel = null;
            bool wasCanceled = cancel.IsCancellationRequested;
            cancel.Dispose();
            if (failure != null)
            {
                Paths.LogError(new IOException("扫描本机照片失败。", failure));
                MessageBox.Show(this, "扫描本机照片失败，尚未收录或创建年份集合。\n" + failure.Message,
                    "整理本机照片", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (result == null || wasCanceled) return;
            if (result.Count == 0)
            {
                MessageBox.Show(this, "未发现可收录的照片。" + LocalPhotoScanWarnings(result),
                    "整理本机照片", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string confirmation = "发现 " + result.Count + " 张照片（约 " + FormatLocalPhotoBytes(result.Bytes) + "）。\n\n"
                + "将保留原照片，并复制到当前已启用的资源存储位置；不会新增或修改存储位置。\n"
                + "拍摄时间优先读取 EXIF，无拍摄时间时按文件修改时间归入对应年份集合。\n"
                + "为减少卡顿，扫描最多约 100 张/秒，复制限速约 32 MB/s，始终单个后台任务。";
            if (result.Reused > 0) confirmation += "\n其中 " + result.Reused + " 张使用已收录副本，不会重复复制。";
            confirmation += LocalPhotoScanWarnings(result) + "\n\n是否开始收录？";
            if (MessageBox.Show(this, confirmation, "整理本机照片", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
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
            Label statusLabel = new Label { Text = "正在准备扫描…", AutoSize = true, Location = new Point(20, 18), Font = Theme.BodyFont };
            Label detailLabel = new Label { Text = "扫描所有本地固定磁盘；现有资源存储位置会跳过。", Location = new Point(20, 50), Size = new Size(480, 48), AutoEllipsis = true };
            status = statusLabel;
            detail = detailLabel;
            ProgressBar progress = new ProgressBar { Style = ProgressBarStyle.Marquee, Location = new Point(20, 106), Size = new Size(480, 12) };
            Button stop = new Button { Text = "取消", Location = new Point(425, 130), Size = new Size(75, 28) };
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

        private static string LocalPhotoScanWarnings(LocalPhotoScanResult result)
        {
            if (result.UnreadableDirectories == 0 && result.ReadFailures == 0) return string.Empty;
            return "\n跳过：" + result.UnreadableDirectories + " 个不可读目录，" + result.ReadFailures + " 个无法读取的文件。";
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
