using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace AppManager
{
    internal enum LocalScanResultAction
    {
        Import,
        Retry,
        Cancel
    }

    /// <summary>
    /// 照片/音乐共用的扫描范围选择窗口。磁盘列表在后台读取，文件夹选择留在 UI 线程。
    /// 只返回已校验、已去重并合并重叠项的根目录列表。
    /// </summary>
    internal sealed class LocalScanScopeDialog : BufferedDialogForm
    {
        private readonly string dialogTitle;
        private readonly LocalScanScopeKind kind;
        private readonly List<string> excludedRoots;
        private readonly List<string> selectedRoots = new List<string>();
        private readonly CheckedListBox driveList;
        private readonly ListBox selectedList;
        private readonly Label unavailableLabel;
        private readonly Label summaryLabel;
        private readonly Button startButton;
        private readonly Control shortcutsPanel;
        private bool populating;

        internal List<string> ResultRoots = new List<string>();

        internal LocalScanScopeDialog(string title, LocalScanScopeKind kind, IEnumerable<string> initialRoots,
            IEnumerable<string> excludedRoots)
        {
            this.dialogTitle = title;
            this.kind = kind;
            this.excludedRoots = excludedRoots == null
                ? new List<string>() : new List<string>(excludedRoots);
            Text = title;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ControlBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(760, 540);
            BackColor = Theme.Background;

            Label titleLabel = new Label
            {
                Text = title,
                AutoSize = false,
                Location = new Point(20, 14),
                Size = new Size(720, 28),
                Font = Theme.DialogTitleFont,
                ForeColor = Theme.TextPrimary
            };
            Label description = new Label
            {
                Text = "选择要扫描的磁盘或文件夹。默认包含子文件夹；现有资源存储位置和 AppManager 数据目录会自动跳过。",
                AutoSize = false,
                Location = new Point(20, 44),
                Size = new Size(720, 24),
                ForeColor = Theme.TextSecondary
            };

            Label drivesTitle = new Label
            {
                Text = "磁盘",
                AutoSize = true,
                Location = new Point(20, 78),
                Font = Theme.SectionTitleFont,
                ForeColor = Theme.TextPrimary
            };
            driveList = new CheckedListBox
            {
                Location = new Point(20, 100),
                Size = new Size(330, 174),
                CheckOnClick = true,
                IntegralHeight = false,
                HorizontalScrollbar = true,
                Font = Theme.CaptionFont
            };
            driveList.ItemCheck += DriveList_ItemCheck;

            Label shortcutsTitle = new Label
            {
                Text = "快捷范围",
                AutoSize = true,
                Location = new Point(20, 284),
                Font = Theme.SectionTitleFont,
                ForeColor = Theme.TextPrimary
            };
            shortcutsPanel = new Panel
            {
                Location = new Point(20, 306),
                Size = new Size(330, 36),
                BackColor = Theme.Background
            };
            BuildShortcuts();

            Label unavailableTitle = new Label
            {
                Text = "不可用位置",
                AutoSize = true,
                Location = new Point(20, 350),
                Font = Theme.SectionTitleFont,
                ForeColor = Theme.TextPrimary
            };
            unavailableLabel = new Label
            {
                Text = "正在读取磁盘…",
                AutoSize = false,
                Location = new Point(20, 372),
                Size = new Size(330, 48),
                ForeColor = Theme.TextSecondary
            };

            Button addFolder = MakeButton("添加文件夹…", false);
            addFolder.Location = new Point(20, 426);
            addFolder.Size = new Size(150, 30);
            addFolder.Click += delegate { AddFolder(); };

            Label selectedTitle = new Label
            {
                Text = "已选范围（完整路径）",
                AutoSize = true,
                Location = new Point(370, 78),
                Font = Theme.SectionTitleFont,
                ForeColor = Theme.TextPrimary
            };
            selectedList = new ListBox
            {
                Location = new Point(370, 100),
                Size = new Size(370, 286),
                SelectionMode = SelectionMode.MultiExtended,
                HorizontalScrollbar = true,
                IntegralHeight = false,
                Font = Theme.CaptionFont
            };
            selectedList.SelectedIndexChanged += delegate { RefreshSummary(); };

            Button removeButton = MakeButton("移除所选", false);
            removeButton.Location = new Point(370, 394);
            removeButton.Size = new Size(110, 30);
            removeButton.Click += delegate { RemoveSelected(); };
            Button clearButton = MakeButton("清空", false);
            clearButton.Location = new Point(490, 394);
            clearButton.Size = new Size(90, 30);
            clearButton.Click += delegate { ClearSelected(); };

            summaryLabel = new Label
            {
                Text = "请选择至少一个磁盘或文件夹。",
                AutoSize = false,
                Location = new Point(20, 454),
                Size = new Size(720, 38),
                ForeColor = Theme.TextSecondary
            };

            startButton = MakeButton("开始扫描", true);
            startButton.Location = new Point(640, 498);
            startButton.Size = new Size(100, 30);
            startButton.Click += delegate { StartScan(); };
            Button cancelButton = MakeButton("取消", false);
            cancelButton.Location = new Point(530, 498);
            cancelButton.Size = new Size(94, 30);
            cancelButton.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            AcceptButton = startButton;
            CancelButton = cancelButton;

            Controls.AddRange(new Control[] {
                titleLabel, description, drivesTitle, driveList, shortcutsTitle, shortcutsPanel,
                unavailableTitle, unavailableLabel, addFolder, selectedTitle, selectedList,
                removeButton, clearButton, summaryLabel, startButton, cancelButton
            });

            ApplyInitialRoots(initialRoots);
            RefreshSummary();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LoadDrivesAsync();
        }

        private void BuildShortcuts()
        {
            List<LocalScanScopeShortcut> shortcuts = LocalScanScope.Shortcuts(kind);
            int x = 0;
            for (int i = 0; i < shortcuts.Count; i++)
            {
                LocalScanScopeShortcut shortcut = shortcuts[i];
                Button button = MakeButton(shortcut.Label, false);
                button.Location = new Point(x, 0);
                button.Size = new Size(Math.Max(84, 84 + (shortcut.Label.Length - 2) * 8), 30);
                button.Tag = shortcut;
                button.Click += delegate(object sender, EventArgs args)
                {
                    Button source = sender as Button;
                    LocalScanScopeShortcut value = source == null ? null : source.Tag as LocalScanScopeShortcut;
                    if (value == null) return;
                    AddSelectedRoot(value.Path, true);
                };
                shortcutsPanel.Controls.Add(button);
                x += button.Width + 8;
            }
        }

        private void ApplyInitialRoots(IEnumerable<string> initialRoots)
        {
            if (initialRoots == null)
            {
                List<LocalScanScopeShortcut> shortcuts = LocalScanScope.Shortcuts(kind);
                for (int i = 0; i < shortcuts.Count; i++)
                {
                    if (!shortcuts[i].Primary) continue;
                    AddSelectedRoot(shortcuts[i].Path, false);
                    break;
                }
                return;
            }
            foreach (string root in initialRoots) AddSelectedRoot(root, false);
        }

        private void LoadDrivesAsync()
        {
            try
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    List<LocalScanScopeDrive> drives = null;
                    Exception failure = null;
                    try { drives = LocalScanScope.Drives(); }
                    catch (Exception ex) { failure = ex; }
                    try
                    {
                        BeginInvoke((Action)delegate
                        {
                            if (IsDisposed || Disposing) return;
                            PopulateDrives(drives, failure);
                        });
                    }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                });
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void PopulateDrives(List<LocalScanScopeDrive> drives, Exception failure)
        {
            populating = true;
            driveList.Items.Clear();
            List<string> unavailable = new List<string>();
            if (failure != null) unavailable.Add("读取磁盘失败：" + failure.Message);
            if (drives != null)
            {
                foreach (LocalScanScopeDrive drive in drives)
                {
                    if (!drive.Supported)
                    {
                        if (!string.IsNullOrEmpty(drive.UnsupportedReason))
                            unavailable.Add(drive.Path + "：" + drive.UnsupportedReason);
                        continue;
                    }
                    driveList.Items.Add(drive);
                }
                foreach (string root in selectedRoots)
                {
                    int index = FindDriveIndex(root);
                    if (index >= 0) driveList.SetItemChecked(index, true);
                }
            }
            populating = false;
            unavailableLabel.Text = unavailable.Count == 0
                ? "仅支持已就绪的本地固定磁盘和可移动磁盘；网络位置、光驱和未就绪磁盘不会显示为可扫描项。"
                : JoinLines(unavailable);
            unavailableLabel.ForeColor = unavailable.Count == 0 ? Theme.TextSecondary : Theme.DeleteRed;
            RefreshSummary();
        }

        private void DriveList_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (populating || e.Index < 0 || e.Index >= driveList.Items.Count) return;
            LocalScanScopeDrive drive = driveList.Items[e.Index] as LocalScanScopeDrive;
            if (drive == null || !drive.Supported) return;
            if (e.NewValue == CheckState.Checked)
            {
                if (!ContainsRoot(drive.Path) && !AddSelectedRoot(drive.Path, true))
                    e.NewValue = CheckState.Unchecked;
            }
            else RemoveSelectedRoot(drive.Path);
        }

        private void AddFolder()
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择要扫描的文件夹";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                AddSelectedRoot(dialog.SelectedPath, true);
            }
        }

        private bool AddSelectedRoot(string path, bool showError)
        {
            string normalized;
            string reason;
            if (!LocalScanScope.TryValidate(path, excludedRoots, out normalized, out reason))
            {
                if (showError) MessageBox.Show(this, reason, dialogTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            for (int i = 0; i < selectedRoots.Count; i++)
            {
                if (!string.Equals(selectedRoots[i], normalized, StringComparison.OrdinalIgnoreCase)) continue;
                return false;
            }
            selectedRoots.Add(normalized);
            RefreshSelectedList();
            RefreshSummary();
            return true;
        }

        private void RemoveSelectedRoot(string path)
        {
            string normalized = LocalScanScope.NormalizePath(path);
            if (normalized == null) return;
            for (int i = selectedRoots.Count - 1; i >= 0; i--)
                if (string.Equals(selectedRoots[i], normalized, StringComparison.OrdinalIgnoreCase))
                    selectedRoots.RemoveAt(i);
            RefreshSelectedList();
            RefreshSummary();
        }

        private void RemoveSelected()
        {
            List<int> indices = new List<int>();
            foreach (int index in selectedList.SelectedIndices) indices.Add(index);
            indices.Sort(delegate(int a, int b) { return b.CompareTo(a); });
            for (int i = 0; i < indices.Count; i++)
            {
                if (indices[i] >= 0 && indices[i] < selectedRoots.Count)
                    selectedRoots.RemoveAt(indices[i]);
            }
            SyncDriveChecks();
            RefreshSelectedList();
            RefreshSummary();
        }

        private void ClearSelected()
        {
            selectedRoots.Clear();
            SyncDriveChecks();
            RefreshSelectedList();
            RefreshSummary();
        }

        private void SyncDriveChecks()
        {
            if (driveList.Items.Count == 0) return;
            populating = true;
            for (int i = 0; i < driveList.Items.Count; i++)
            {
                LocalScanScopeDrive drive = driveList.Items[i] as LocalScanScopeDrive;
                bool selected = drive != null && ContainsRoot(drive.Path);
                driveList.SetItemChecked(i, selected);
            }
            populating = false;
        }

        private bool ContainsRoot(string path)
        {
            string normalized = LocalScanScope.NormalizePath(path);
            if (normalized == null) return false;
            for (int i = 0; i < selectedRoots.Count; i++)
                if (string.Equals(selectedRoots[i], normalized, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private int FindDriveIndex(string path)
        {
            string normalized = LocalScanScope.NormalizePath(path);
            if (normalized == null) return -1;
            for (int i = 0; i < driveList.Items.Count; i++)
            {
                LocalScanScopeDrive drive = driveList.Items[i] as LocalScanScopeDrive;
                if (drive != null && string.Equals(LocalScanScope.NormalizePath(drive.Path), normalized,
                    StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        private void RefreshSelectedList()
        {
            object[] selected = new object[selectedRoots.Count];
            for (int i = 0; i < selectedRoots.Count; i++)
                selected[i] = (LocalScanScope.IsDriveRoot(selectedRoots[i]) ? "[磁盘] " : "[文件夹] ") + selectedRoots[i];
            selectedList.BeginUpdate();
            try
            {
                selectedList.Items.Clear();
                selectedList.Items.AddRange(selected);
            }
            finally { selectedList.EndUpdate(); }
        }

        private void RefreshSummary()
        {
            LocalScanScopeResolution resolution = LocalScanScope.Resolve(selectedRoots, excludedRoots);
            int folders = resolution.Roots.Count - resolution.DriveCount;
            string text;
            if (resolution.Roots.Count == 0)
            {
                text = resolution.Invalid.Count == 0
                    ? "请选择至少一个磁盘或文件夹，选择后才会开始扫描。"
                    : "没有可用范围：" + resolution.Invalid[0];
            }
            else
            {
                text = "将扫描 " + resolution.DriveCount + " 个磁盘、" + folders + " 个文件夹。";
                if (resolution.Merged > 0) text += "已合并 " + resolution.Merged + " 个重复或重叠范围。";
                if (resolution.DriveCount > 0) text += "包含整盘扫描，可能耗时较长。";
                if (resolution.Invalid.Count > 0) text += "另有 " + resolution.Invalid.Count + " 个范围不可用。";
            }
            summaryLabel.Text = text;
            summaryLabel.ForeColor = resolution.Invalid.Count > 0 ? Theme.DeleteRed
                : resolution.DriveCount > 0 ? Theme.AccentText : Theme.TextSecondary;
            startButton.Enabled = resolution.Roots.Count > 0;
        }

        private void StartScan()
        {
            LocalScanScopeResolution resolution = LocalScanScope.Resolve(selectedRoots, excludedRoots);
            if (resolution.Invalid.Count > 0)
            {
                string message = "以下范围当前不可用：\n\n" + JoinLines(resolution.Invalid)
                    + "\n\n是否排除这些范围并继续扫描可用部分？";
                if (MessageBox.Show(this, message, dialogTitle, MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes) return;
                selectedRoots.Clear();
                selectedRoots.AddRange(resolution.Roots);
                SyncDriveChecks();
                RefreshSelectedList();
            }
            if (resolution.Roots.Count == 0)
            {
                MessageBox.Show(this, "没有可用的扫描范围。请选择磁盘或添加文件夹。", dialogTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            ResultRoots = new List<string>(resolution.Roots);
            DialogResult = DialogResult.OK;
            Close();
        }

        private static string JoinLines(List<string> lines)
        {
            int count = Math.Min(5, lines.Count);
            string text = string.Empty;
            for (int i = 0; i < count; i++) text += (i == 0 ? "" : "\n") + lines[i];
            if (lines.Count > count) text += "\n…另有 " + (lines.Count - count) + " 项";
            return text;
        }

        private static Button MakeButton(string text, bool accent)
        {
            Button button = new Button
            {
                Text = text,
                Font = Theme.ButtonFont,
                UseVisualStyleBackColor = true,
                FlatStyle = FlatStyle.System
            };
            if (accent) button.Font = Theme.ButtonFont;
            return button;
        }
    }

    /// <summary>扫描结果确认窗口：汇总、失败样例、重试和继续导入。</summary>
    internal static class LocalScanResultDialog
    {
        internal static LocalScanResultAction Show(IWin32Window owner, string title, string summary,
            string importText, bool canImport)
        {
            LocalScanResultAction action = LocalScanResultAction.Cancel;
            using (Form form = new BufferedDialogForm())
            {
                form.Text = title;
                form.StartPosition = FormStartPosition.CenterParent;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MinimizeBox = false;
                form.MaximizeBox = false;
                form.ControlBox = false;
                form.ShowInTaskbar = false;
                form.ClientSize = new Size(680, 470);
                form.BackColor = Theme.Background;

                Label heading = new Label
                {
                    Text = title,
                    AutoSize = false,
                    Location = new Point(20, 14),
                    Size = new Size(640, 28),
                    Font = Theme.DialogTitleFont,
                    ForeColor = Theme.TextPrimary
                };
                TextBox details = new TextBox
                {
                    Text = summary ?? string.Empty,
                    Location = new Point(20, 50),
                    Size = new Size(640, 350),
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Both,
                    WordWrap = false,
                    BackColor = Theme.Card,
                    BorderStyle = BorderStyle.FixedSingle,
                    Font = Theme.BodyFont
                };

                Button cancel = StandardButton("取消");
                cancel.Location = new Point(556, 418);
                cancel.Size = new Size(100, 30);
                cancel.DialogResult = DialogResult.Cancel;
                Button retry = StandardButton("重新扫描");
                retry.Location = new Point(430, 418);
                retry.Size = new Size(112, 30);
                retry.Click += delegate { action = LocalScanResultAction.Retry; form.DialogResult = DialogResult.OK; form.Close(); };
                Button import = StandardButton(string.IsNullOrEmpty(importText) ? "继续" : importText);
                import.Location = new Point(292, 418);
                import.Size = new Size(124, 30);
                import.Click += delegate { action = LocalScanResultAction.Import; form.DialogResult = DialogResult.OK; form.Close(); };
                import.Visible = canImport;
                form.AcceptButton = canImport ? (IButtonControl)import : (IButtonControl)retry;
                form.CancelButton = cancel;

                form.Controls.AddRange(new Control[] { heading, details, cancel, retry, import });
                form.ShowDialog(owner);
            }
            return action;
        }

        private static Button StandardButton(string text)
        {
            return new Button
            {
                Text = text,
                Font = Theme.ButtonFont,
                UseVisualStyleBackColor = true,
                FlatStyle = FlatStyle.System
            };
        }
    }
}
