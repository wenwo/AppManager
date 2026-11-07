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
        private List<string> lastMusicScanRoots;

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
        /// 扫描本机本地磁盘上的音频文件，按内容识别歌曲后复制到资源页「音乐」分类。
        /// 已登记的同一路径由现有导入流程跳过重复入库；扫描只读，源文件保留。
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
            List<string> roots;
            using (LocalScanScopeDialog scope = new LocalScanScopeDialog("扫描本机音乐", LocalScanScopeKind.Music,
                lastMusicScanRoots, LocalScanExcludedRoots()))
            {
                if (scope.ShowDialog(this) != DialogResult.OK) return;
                roots = new List<string>(scope.ResultRoots);
            }
            if (roots.Count == 0) return;
            lastMusicScanRoots = new List<string>(roots);

            localMusicScanRunning = true;
            try
            {
                while (true)
                {
                    CancellationTokenSource cancel = new CancellationTokenSource();
                    localMusicScanCancel = cancel;
                    MusicScanRequest request = BuildLocalMusicScanRequest(roots);
                    LocalMusicScanResult result = null;
                    Exception failure = null;
                    bool canceled = false;
                    bool extensionOnly = false;
                    try
                    {
                        using (Form dialog = BuildMusicScanDialog())
                        {
                            Label status = (Label)dialog.Tag;
                            Label detail = dialog.Controls["scanDetail"] as Label;
                            ProgressBar progress = dialog.Controls["scanProgress"] as ProgressBar;
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
                                        string availabilityError;
                                        bool available = MpvAudioProbeSession.TryCheckAvailability(out availabilityError, cancel.Token);
                                        if (!available && !cancel.IsCancellationRequested)
                                        {
                                            bool fallback = false;
                                            using (ManualResetEvent answer = new ManualResetEvent(false))
                                            {
                                                try
                                                {
                                                    dialog.BeginInvoke((Action)delegate
                                                    {
                                                        if (dialog.IsDisposed || cancel.IsCancellationRequested) { answer.Set(); return; }
                                                        string message = "播放组件缺失或版本不兼容，无法识别音频内容。\n\n"
                                                            + (string.IsNullOrEmpty(availabilityError) ? "" : availabilityError + "\n\n")
                                                            + "是否按扩展名继续扫描？\n"
                                                            + "选择「是」将按扩展名收录，仍会跳过系统和程序目录、过小文件、云端占位文件和已排除项。";
                                                        fallback = MessageBox.Show(dialog, message, "播放组件缺失",
                                                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                                                        answer.Set();
                                                    });
                                                }
                                                catch (ObjectDisposedException) { answer.Set(); }
                                                catch (InvalidOperationException) { answer.Set(); }
                                                while (!answer.WaitOne(50))
                                                {
                                                    if (cancel.IsCancellationRequested) return;
                                                }
                                            }
                                            if (cancel.IsCancellationRequested) return;
                                            if (!fallback)
                                            {
                                                canceled = true;
                                                CloseMusicScanDialog(dialog);
                                                return;
                                            }
                                            extensionOnly = true;
                                            UpdateMusicScanStatus(dialog, status, detail, progress,
                                                "播放组件缺失，按扩展名扫描…", "准备扫描…", false, 0, 0);
                                        }
                                        if (cancel.IsCancellationRequested) return;
                                        request.ExtensionOnly = extensionOnly;
                                        request.ProbeFactory = extensionOnly ? null : new Func<IMusicProbeSession>(delegate { return new MpvAudioProbeSession(); });
                                        int lastUpdate = Environment.TickCount - 250;
                                        result = LocalMusicScanner.Scan(request, cancel.Token, delegate(MusicScanProgress value)
                                        {
                                            int now = Environment.TickCount;
                                            bool finalProbe = value.Probing && value.Total > 0 && value.Index >= value.Total;
                                            if (!value.Probing && unchecked(now - lastUpdate) < 200) return;
                                            if (value.Probing && !finalProbe && unchecked(now - lastUpdate) < 200) return;
                                            lastUpdate = now;
                                            if (value.Probing)
                                                UpdateMusicScanStatus(dialog, status, detail, progress,
                                                    "正在识别音乐：" + value.Index + " / " + value.Total + "，已通过 " + value.Accepted,
                                                    value.Path, true, value.Index, value.Total);
                                            else
                                                UpdateMusicScanStatus(dialog, status, detail, progress,
                                                    "正在遍历：已检查 " + value.DirectoriesScanned + " 个目录，发现 "
                                                    + value.AudioFilesFound + " 个音频文件，候选 " + value.Candidates,
                                                    value.Path, false, 0, 0);
                                        });
                                    }
                                    catch (OperationCanceledException) { canceled = true; }
                                    catch (Exception ex) { failure = ex; Paths.LogError(ex); }
                                    finally { CloseMusicScanDialog(dialog); }
                                });
                            };
                            dialog.ShowDialog(this);
                        }
                    }
                    finally
                    {
                        localMusicScanCancel = null;
                        canceled = canceled || cancel.IsCancellationRequested;
                        cancel.Dispose();
                    }
                    if (canceled)
                    {
                        ShowNotice("已取消扫描本机音乐，未导入任何文件。");
                        return;
                    }
                    if (failure != null)
                    {
                        LocalScanResultAction action = LocalScanResultDialog.Show(this, "扫描本机音乐",
                            "扫描失败，未导入任何文件，源文件未改动。\n\n" + failure.Message, null, false);
                        if (action == LocalScanResultAction.Retry) continue;
                        return;
                    }
                    if (result == null)
                    {
                        LocalScanResultAction action = LocalScanResultDialog.Show(this, "扫描本机音乐",
                            "扫描未返回结果，未导入任何文件。", null, false);
                        if (action == LocalScanResultAction.Retry) continue;
                        return;
                    }

                    bool canImport = result.Files.Count > 0 || result.UnconfirmedCount > 0;
                    LocalScanResultAction decision = LocalScanResultDialog.Show(this, "扫描本机音乐",
                        BuildMusicScanSummary(result), "继续", canImport);
                    if (decision == LocalScanResultAction.Retry) continue;
                    if (decision != LocalScanResultAction.Import || !canImport) return;
                    FinishLocalMusicScan(result);
                    return;
                }
            }
            finally
            {
                localMusicScanRunning = false;
                localMusicScanCancel = null;
            }
        }

        private MusicScanRequest BuildLocalMusicScanRequest(IEnumerable<string> roots)
        {
            MusicScanRequest request = new MusicScanRequest();
            request.Roots = roots == null ? null : new List<string>(roots);
            request.MusicRoots = LocalMusicScanner.UserMusicRoots();
            HashSet<string> seenIncluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenExcluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AddedEntry entry in config.AddedItems.InCategory("音乐"))
            {
                AddMusicPath(request.IncludedPaths, seenIncluded, entry.Path);
                AddMusicPath(request.IncludedPaths, seenIncluded, entry.OriginPath);
            }
            foreach (KeyValuePair<string, List<string>> pair in config.ExcludedCategories)
            {
                if (pair.Value == null || !pair.Value.Contains("音乐")) continue;
                if (pair.Key == null || !pair.Key.StartsWith("add:", StringComparison.OrdinalIgnoreCase)) continue;
                string path = pair.Key.Substring(4);
                AddMusicPath(request.ExcludedPaths, seenExcluded, path);
                foreach (AddedEntry entry in config.AddedItems.ByPath(path))
                    AddMusicPath(request.ExcludedPaths, seenExcluded, entry.OriginPath);
            }
            foreach (string root in KnownResourceStoreRoots())
                if (!string.IsNullOrEmpty(root)) request.ExcludedRoots.Add(root);
            request.ExcludedRoots.Add(Paths.DataDir);
            return request;
        }

        private static void AddMusicPath(List<string> paths, HashSet<string> seen, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !seen.Add(path)) return;
            paths.Add(path);
        }

        private static Form BuildMusicScanDialog()
        {
            Form dialog = new BufferedDialogForm
            {
                Text = "扫描本机音乐",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false,
                MaximizeBox = false,
                ControlBox = false,
                ShowInTaskbar = false,
                ClientSize = new Size(560, 166),
                BackColor = Theme.Background
            };
            Label status = new Label
            {
                Name = "scanStatus",
                Text = "正在检查播放组件…",
                AutoSize = false,
                Location = new Point(18, 18),
                Size = new Size(524, 26),
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
                Size = new Size(524, 30),
                ForeColor = Theme.TextSecondary
            };
            ProgressBar progress = new ProgressBar
            {
                Name = "scanProgress",
                Style = ProgressBarStyle.Marquee,
                Location = new Point(18, 86),
                Size = new Size(524, 12)
            };
            Button cancel = new Button
            {
                Name = "scanCancel",
                Text = "取消",
                Location = new Point(448, 116),
                Size = new Size(94, 30)
            };
            dialog.Controls.AddRange(new Control[] { status, detail, progress, cancel });
            dialog.CancelButton = cancel;
            dialog.Tag = status;
            return dialog;
        }

        private static void UpdateMusicScanStatus(Form dialog, Label status, Label detail, ProgressBar progress,
            string statusText, string detailText, bool probing, int index, int total)
        {
            try
            {
                if (dialog.IsDisposed || !dialog.IsHandleCreated) return;
                dialog.BeginInvoke((Action)delegate
                {
                    if (dialog.IsDisposed || status.IsDisposed || detail.IsDisposed) return;
                    status.Text = statusText;
                    detail.Text = detailText;
                    if (progress == null || progress.IsDisposed) return;
                    if (probing && total > 0)
                    {
                        progress.Style = ProgressBarStyle.Continuous;
                        progress.Maximum = total;
                        progress.Value = Math.Max(0, Math.Min(index, total));
                    }
                    else progress.Style = ProgressBarStyle.Marquee;
                });
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private static void CloseMusicScanDialog(Form dialog)
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

        private void FinishLocalMusicScan(LocalMusicScanResult result)
        {
            if (ActiveResourceStoreRoots().Count == 0)
            {
                MessageBox.Show(this, "未启用任何资源存储位置，本次扫描结果未导入。",
                    "扫描本机音乐", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            List<string> import = new List<string>();
            if (result.Files.Count > 0) import.AddRange(result.Files);
            if (result.UnconfirmedCount > 0)
            {
                string samples = MusicSamples(result, MusicFilterReason.Unconfirmed, 3);
                string message = "有 " + result.UnconfirmedCount + " 个文件无法确认是否为歌曲。"
                    + (string.IsNullOrEmpty(samples) ? "" : "\n例如：" + samples)
                    + "\n\n是否将无法确认的文件也一并导入？选择「否」会忽略这些文件。";
                if (MessageBox.Show(this, message, "无法确认的文件",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                    import.AddRange(result.UnconfirmedFiles);
            }
            if (import.Count == 0)
            {
                ShowNotice("未导入任何内容。");
                return;
            }
            QueueImport(import.ToArray(), ImportOperation.Copy, "音乐", null, true, 0, null, 0, true);
        }

        private static string BuildMusicScanSummary(LocalMusicScanResult result)
        {
            StringBuilder text = new StringBuilder();
            text.Append("扫描完成：发现 ").Append(result.AudioFilesFound).Append(" 个音频文件，进入识别 ")
                .Append(result.Candidates).Append(" 个；检查 ")
                .Append(result.DirectoriesScanned).Append(" 个目录，跳过 ")
                .Append(result.DirectoriesSkipped).Append(" 个目录。");
            text.Append("\n通过筛选 ").Append(result.Files.Count).Append(" 个，无法确认 ")
                .Append(result.UnconfirmedCount).Append(" 个。");
            text.Append("\n预计复制 ").Append(LocalScanScope.FormatBytes(result.AcceptedBytes));
            if (result.UnconfirmedBytes > 0)
                text.Append("；若一并导入无法确认文件，另约 ").Append(LocalScanScope.FormatBytes(result.UnconfirmedBytes));
            AppendMusicReason(text, result, MusicFilterReason.AlreadyIncluded, "已收录跳过");
            AppendMusicReason(text, result, MusicFilterReason.Excluded, "已排除跳过");
            AppendMusicReason(text, result, MusicFilterReason.TooSmall, "文件过小");
            AppendMusicReason(text, result, MusicFilterReason.CloudPlaceholder, "云端未下载");
            AppendMusicReason(text, result, MusicFilterReason.ProbeFailed, "无法解析");
            AppendMusicReason(text, result, MusicFilterReason.ProbeTimeout, "识别超时");
            AppendMusicReason(text, result, MusicFilterReason.ProbeUnavailable, "播放组件不可用");
            AppendMusicReason(text, result, MusicFilterReason.NoAudioStream, "没有音频轨");
            AppendMusicReason(text, result, MusicFilterReason.TooShort, "时长不足 30 秒");
            AppendMusicReason(text, result, MusicFilterReason.NonSongGenre, "播客/有声书/录音等标签");
            AppendMusicReason(text, result, MusicFilterReason.NonSongDirectory, "提示音/录音等目录");
            AppendMusicReason(text, result, MusicFilterReason.VoiceQuality, "低采样率单声道");
            AppendMusicReason(text, result, MusicFilterReason.Mono, "单声道");
            AppendMusicReason(text, result, MusicFilterReason.LongUntagged, "无标签且超过 20 分钟");
            AppendMusicReason(text, result, MusicFilterReason.Unconfirmed, "无法确认");
            AppendMusicReason(text, result, MusicFilterReason.NotProbed, "未探测");
            if (result.Probes > 0)
            {
                text.Append("\n探测 ").Append(result.Probes).Append(" 个，平均 ")
                    .Append(result.ProbeMilliseconds / result.Probes).Append(" ms/个；总耗时 ")
                    .Append(result.TotalMilliseconds).Append(" ms。");
            }
            else
            {
                text.Append("\n总耗时 ").Append(result.TotalMilliseconds).Append(" ms。");
            }
            if (result.CloudFiles > 0)
                text.Append("\n其中 ").Append(result.CloudFiles)
                    .Append(" 个是云端按需文件。请先在文件资源管理器中选择「始终保留在此设备上」，再重新扫描。");
            if (result.Errors > 0)
            {
                text.Append("\n有 ").Append(result.Errors).Append(" 个磁盘、目录或文件因权限、占用或路径变化未能读取。");
                if (result.ErrorSamples.Count > 0)
                {
                    text.Append("\n示例：");
                    int count = Math.Min(3, result.ErrorSamples.Count);
                    for (int i = 0; i < count; i++)
                        text.Append("\n").Append(result.ErrorSamples[i]);
                }
            }
            return text.ToString();
        }

        private static void AppendMusicReason(StringBuilder text, LocalMusicScanResult result,
            MusicFilterReason reason, string label)
        {
            int count = result.CountOf(reason);
            if (count == 0) return;
            text.Append("\n").Append(label).Append("：").Append(count);
            List<string> samples = result.SamplesOf(reason);
            int shown = Math.Min(2, samples.Count);
            for (int i = 0; i < shown; i++)
            {
                string name = Path.GetFileName(samples[i]);
                text.Append(i == 0 ? "（如 " : "、").Append(string.IsNullOrEmpty(name) ? samples[i] : name);
            }
            if (shown > 0) text.Append("）");
        }

        private static string MusicSamples(LocalMusicScanResult result, MusicFilterReason reason, int limit)
        {
            List<string> samples = reason == MusicFilterReason.Unconfirmed
                ? result.UnconfirmedFiles : result.SamplesOf(reason);
            StringBuilder text = new StringBuilder();
            int count = Math.Min(limit, samples.Count);
            for (int i = 0; i < count; i++)
            {
                string name = Path.GetFileName(samples[i]);
                if (i > 0) text.Append("、");
                text.Append(string.IsNullOrEmpty(name) ? samples[i] : name);
            }
            return text.ToString();
        }
    }
}
