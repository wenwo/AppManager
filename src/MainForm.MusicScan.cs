using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AppManager
{
    public partial class MainForm
    {
        private bool localMusicScanRunning;
        private CancellationTokenSource localMusicScanCancel;
        private bool missingMusicCleanupRunning;

        /// <summary>
        /// 检查「音乐」分类的失效记录。只移除数据库收录，不删除磁盘或回收站文件；
        /// 路径所在目录不可访问时视为磁盘离线并跳过，确认后还会再复核一次。
        /// </summary>
        private void CheckMissingMusicEntries()
        {
            if (missingMusicCleanupRunning) return;
            if (!CanEditCategory("音乐"))
            {
                MessageBox.Show(this, "当前无法编辑「音乐」分类。", "清理失效音乐记录",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            List<string> paths = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AddedEntry entry in config.AddedItems.InCategory("音乐"))
                if (!string.IsNullOrEmpty(entry.Path) && seen.Add(entry.Path)) paths.Add(entry.Path);
            if (paths.Count == 0)
            {
                ShowNotice("「音乐」分类中没有可检查的记录。");
                return;
            }

            missingMusicCleanupRunning = true;
            ShowNotice("正在检查「音乐」分类的失效记录…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> missing = null;
                Exception failure = null;
                try { missing = MissingResourceCleanup.FindMissingWithReachableParent(paths); }
                catch (Exception ex) { failure = ex; }
                RunOnUi(delegate
                {
                    if (IsDisposed) return;
                    if (failure != null)
                    {
                        missingMusicCleanupRunning = false;
                        Paths.LogError(failure);
                        MessageBox.Show(this, "检查失效音乐记录失败。\n" + failure.Message, "清理失效音乐记录",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (missing.Count == 0)
                    {
                        missingMusicCleanupRunning = false;
                        ShowNotice("「音乐」分类中没有失效记录。");
                        return;
                    }
                    string message = "在「音乐」分类中发现 " + missing.Count + " 条记录的原文件已不存在"
                        + "（所在目录仍可访问）。\n\n" + MissingMusicPreview(missing)
                        + "\n\n仅移除数据库记录，不会删除磁盘、回收站或任何文件。是否继续？";
                    if (!ConfirmDialog.Show("清理失效音乐记录", message))
                    {
                        missingMusicCleanupRunning = false;
                        return;
                    }
                    ConfirmMissingMusicCleanup(missing);
                });
            });
        }

        /// <summary>用户确认后在后台复核；文件已恢复或父目录不可访问时不清理。</summary>
        private void ConfirmMissingMusicCleanup(List<string> missing)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> confirmed = null;
                Exception failure = null;
                try { confirmed = MissingResourceCleanup.FindMissingWithReachableParent(missing); }
                catch (Exception ex) { failure = ex; }
                RunOnUi(delegate
                {
                    if (IsDisposed) return;
                    missingMusicCleanupRunning = false;
                    if (failure != null)
                    {
                        Paths.LogError(failure);
                        MessageBox.Show(this, "复核失效音乐记录失败，未做任何清理。\n" + failure.Message,
                            "清理失效音乐记录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (confirmed.Count == 0)
                    {
                        ShowNotice("文件已恢复，未清理任何记录。");
                        return;
                    }
                    int removed = RemoveMissingMusicEntries(confirmed);
                    if (removed == 0)
                    {
                        ShowNotice("记录已变化，未清理任何内容。");
                        return;
                    }
                    if (!config.Save("cleanup.missing.music"))
                    {
                        MessageBox.Show(this, "清理失效音乐记录未能保存。\n" + config.DatabaseError,
                            "清理失效音乐记录", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        StartScan(false);
                        return;
                    }
                    ShowNotice("已清理 " + removed + " 条失效音乐记录；磁盘文件未改动。");
                    StartScan(false);
                });
            });
        }

        private static string MissingMusicPreview(List<string> paths)
        {
            StringBuilder text = new StringBuilder();
            int count = Math.Min(8, paths.Count);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) text.Append('\n');
                string name = Path.GetFileName(paths[i]);
                text.Append(string.IsNullOrEmpty(name) ? paths[i] : name);
            }
            if (paths.Count > count) text.Append("\n……");
            return text.ToString();
        }

        /// <summary>只移除「音乐」分类归属；同路径若仍属于其它分类，保留其记录与配置。</summary>
        private int RemoveMissingMusicEntries(List<string> paths)
        {
            HashSet<string> target = new HashSet<string>(paths ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            int removed = 0;
            foreach (AddedEntry entry in new List<AddedEntry>(config.AddedItems.InCategory("音乐")))
                if (target.Contains(entry.Path) && config.AddedItems.Remove(entry)) removed++;
            if (removed > 0)
            {
                InvalidateAddedIndex();
                foreach (string path in target)
                    if (config.AddedItems.ByPath(path).Count == 0) PurgeEntryKeys(path, "add:" + path);
            }
            return removed;
        }

        /// <summary>
        /// 扫描本机本地磁盘上的音频文件，并移动到资源页「音乐」分类。
        /// 已登记的同一路径由现有导入流程转成引用并跳过重复入库，不会移动源文件。
        /// </summary>
        private void ScanLocalMusic()
        {
            if (localMusicScanRunning) return;
            if (!CanEditCategory("音乐"))
            {
                MessageBox.Show(this, "当前无法编辑「音乐」分类。", "扫描本机音乐",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (ActiveResourceStoreRoots().Count == 0)
            {
                MessageBox.Show(this, "未启用任何资源存储位置，请在设置 > 存储位置中启用至少一个位置。",
                    "扫描本机音乐", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string formats = string.Join("、", MediaRules.AudioExt).ToUpperInvariant();
            string prompt = "将扫描本机固定磁盘和可移动磁盘，查找以下音频文件：\n" + formats
                + "\n\n找到的歌曲会复制到资源存储位置的「音乐」分类，源文件保留；"
                + "已收录的同一路径会自动跳过，之前排除的项目保持排除。"
                + "\n按扩展名识别，可能包含提示音等其他音频。全盘扫描可能需要几分钟，可随时取消。是否开始？";
            if (MessageBox.Show(this, prompt, "扫描本机音乐", MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question) != DialogResult.OK) return;

            localMusicScanRunning = true;
            CancellationTokenSource cancel = new CancellationTokenSource();
            localMusicScanCancel = cancel;
            LocalMusicScanResult result = null;
            Exception failure = null;
            bool canceled = false;
            using (Form dialog = BuildMusicScanDialog())
            {
                Label status = (Label)dialog.Tag;
                Label detail = dialog.Controls["scanDetail"] as Label;
                Button cancelButton = dialog.Controls["scanCancel"] as Button;
                cancelButton.Click += delegate
                {
                    cancel.Cancel();
                    cancelButton.Enabled = false;
                    cancelButton.Text = "正在取消…";
                    status.Text = "正在停止扫描…";
                };
                dialog.Shown += delegate
                {
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try
                        {
                            result = LocalMusicScanner.Scan(cancel.Token, delegate(int count, string directory)
                            {
                                PostMusicScanProgress(dialog, status, detail, count, directory);
                            });
                        }
                        catch (OperationCanceledException) { canceled = true; }
                        catch (Exception ex) { failure = ex; Paths.LogError(ex); }
                        finally
                        {
                            RunOnUi(delegate
                            {
                                if (!dialog.IsDisposed) dialog.Close();
                            });
                        }
                    });
                };
                dialog.ShowDialog(this);
            }
            localMusicScanRunning = false;
            localMusicScanCancel = null;
            bool wasCanceled = canceled || cancel.IsCancellationRequested;
            cancel.Dispose();

            if (failure != null)
            {
                MessageBox.Show(this, "扫描本机音乐失败，未导入任何文件。\n" + failure.Message,
                    "扫描本机音乐", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (wasCanceled)
            {
                ShowNotice("已取消扫描本机音乐，未导入任何文件。");
                return;
            }
            if (result == null || result.Files.Count == 0)
            {
                string message = "扫描结束，未找到支持的音频文件。";
                if (result != null) message += MusicScanErrorSummary(result);
                MessageBox.Show(this, message, "扫描本机音乐",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            FinishLocalMusicScan(result);
        }

        private static Form BuildMusicScanDialog()
        {
            Form dialog = new Form
            {
                Text = "扫描本机音乐",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ControlBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(520, 142),
                BackColor = Theme.Background
            };
            Label status = new Label
            {
                Name = "scanStatus",
                Text = "正在扫描本机音乐…",
                AutoSize = false,
                Location = new Point(18, 18),
                Size = new Size(484, 24),
                Font = Theme.DialogTitleFont,
                ForeColor = Theme.TextPrimary
            };
            Label detail = new Label
            {
                Name = "scanDetail",
                Text = "准备扫描…",
                AutoSize = false,
                AutoEllipsis = true,
                Location = new Point(18, 50),
                Size = new Size(484, 22),
                ForeColor = Theme.TextSecondary
            };
            Button cancel = new Button
            {
                Name = "scanCancel",
                Text = "取消",
                Location = new Point(408, 94),
                Size = new Size(94, 30)
            };
            dialog.Controls.AddRange(new Control[] { status, detail, cancel });
            dialog.CancelButton = cancel;
            dialog.Tag = status;
            return dialog;
        }

        private static void PostMusicScanProgress(Form dialog, Label status, Label detail, int count, string directory)
        {
            try
            {
                if (dialog.IsDisposed || !dialog.IsHandleCreated) return;
                dialog.BeginInvoke((Action)delegate
                {
                    if (dialog.IsDisposed || status.IsDisposed || detail.IsDisposed) return;
                    status.Text = "正在扫描本机音乐… 已找到 " + count + " 个音频文件";
                    detail.Text = directory;
                });
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void FinishLocalMusicScan(LocalMusicScanResult result)
        {
            if (ActiveResourceStoreRoots().Count == 0)
            {
                MessageBox.Show(this, "未启用任何资源存储位置，本次扫描结果未导入。",
                    "扫描本机音乐", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string message = (result.Errors == 0 ? "扫描完成：" : "扫描结束（有遗漏）：") + "找到 " + result.Files.Count + " 个音频文件，"
                + "检查了 " + result.DirectoriesScanned + " 个目录。";
            message += MusicScanErrorSummary(result);
            if (result.CloudFiles > 0)
                message += "\n其中 " + result.CloudFiles + " 个云端按需文件在复制时可能需要下载。";
            message += "\n\n将复制到资源存储位置的「音乐」分类，源文件保留；"
                + "已收录的同一路径会自动跳过，之前排除的项目保持排除。是否继续导入？";
            if (MessageBox.Show(this, message, "扫描完成", MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question) != DialogResult.OK) return;
            QueueImport(result.Files.ToArray(), ImportOperation.Copy, "音乐", null, true, 0, null, 0, true);
        }

        private static string MusicScanErrorSummary(LocalMusicScanResult result)
        {
            if (result.Errors == 0) return "";
            string message = "\n有 " + result.Errors + " 个磁盘、目录或文件因权限、占用或路径变化未能读取。";
            if (result.ErrorSamples.Count > 0)
            {
                int count = Math.Min(3, result.ErrorSamples.Count);
                message += "\n示例：\n" + string.Join("\n", result.ErrorSamples.GetRange(0, count).ToArray());
            }
            return message;
        }
    }
}
