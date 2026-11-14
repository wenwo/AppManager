using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using AppManager.Controls;

namespace AppManager
{
    /// <summary>
    /// 照片/视频/音乐共用的扫描范围选择窗口：常用文件夹、磁盘和自己添加的文件夹列在同一个清单里，勾上即纳入扫描。
    /// 行左侧的箭头在原地展开下一级子文件夹；也可以在底部输入、粘贴路径或把文件夹拖进来。
    /// 整个选择过程不再弹出其它窗口：出错和提醒都显示在清单下方的提示行里。
    /// 磁盘和子文件夹在后台读取。只返回已校验、已去重并合并重叠项的根目录列表。
    /// </summary>
    internal sealed class LocalScanScopeDialog : BufferedDialogForm
    {
        private const int HeaderHeight = 30;
        private const int NoteHeight = 30;
        private const int RowHeight = 46;
        private const int FolderRowHeight = 34;
        private const int RemoveZone = 40;
        private const int IndentBase = 8;
        private const int IndentStep = 20;
        private const int ChevronWidth = 20;
        private const int MaxDroppedPaths = 50;
        private const int EM_SETCUEBANNER = 0x1501;

        private readonly List<string> excludedRoots;
        private readonly List<LocalScanScopeShortcut> shortcuts;
        private readonly List<string> selectedRoots = new List<string>();
        // 勾选过或手动加入的范围：在清单里看不到对应行时（上级没展开）列在「已添加的文件夹」
        private readonly List<string> knownRoots = new List<string>();
        private readonly HashSet<string> expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LocalScanScopeChildren> children =
            new Dictionary<string, LocalScanScopeChildren>(StringComparer.OrdinalIgnoreCase);
        private readonly ScopeList list = new ScopeList();
        private readonly Label summaryLabel = new Label();
        private readonly TextBox pathBox = new TextBox();
        private readonly RoundButton addPathButton = new RoundButton();
        private readonly RoundButton startButton = new RoundButton();
        private readonly RoundButton cancelButton = new RoundButton();
        private readonly Rectangle listFrame;   // 列表外框（96 DPI 设计坐标），绘制时按当前 DPI 换算
        private readonly Rectangle pathFrame;   // 路径输入框外框（96 DPI 设计坐标）
        private List<LocalScanScopeDrive> drives;   // null：仍在后台读取
        private string driveError;
        private string notice;       // 最近一次操作的提示；下一次操作时清掉，恢复显示范围汇总
        private bool noticeIsError;

        internal List<string> ResultRoots = new List<string>();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, string lParam);

        internal LocalScanScopeDialog(string title, LocalScanScopeKind kind, IEnumerable<string> initialRoots,
            IEnumerable<string> excludedRoots)
        {
            this.excludedRoots = excludedRoots == null
                ? new List<string>() : new List<string>(excludedRoots);
            shortcuts = LocalScanScope.Shortcuts(kind);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(600, 604);
            BackColor = Theme.Background;
            ShowInTaskbar = false;
            AllowDrop = true;
            Text = title;

            Label titleLabel = new Label();
            titleLabel.Text = title;
            titleLabel.Font = Theme.DialogTitleFont;
            titleLabel.ForeColor = Theme.TextPrimary;
            titleLabel.AutoSize = false;
            titleLabel.UseMnemonic = false;
            titleLabel.SetBounds(24, 18, 552, 28);
            Controls.Add(titleLabel);

            Label hint = new Label();
            hint.Text = "勾选要扫描的位置，点左侧箭头可展开子文件夹；资源存储位置和程序数据目录会自动跳过。";
            hint.Font = Theme.CaptionFont;
            hint.ForeColor = Theme.TextSecondary;
            hint.AutoSize = false;
            hint.AutoEllipsis = true;
            hint.SetBounds(24, 48, 552, 22);
            Controls.Add(hint);

            listFrame = new Rectangle(24, 80, 552, 372);
            list.SetBounds(listFrame.X + 4, listFrame.Y + 4, listFrame.Width - 8, listFrame.Height - 8);
            list.RemoveZoneWidth = RemoveZone;
            list.AccessibleName = "扫描范围";
            list.MeasureItem += MeasureRow;
            list.DrawItem += DrawRow;
            list.MouseUp += List_MouseUp;
            list.KeyDown += List_KeyDown;
            Controls.Add(list);

            // 两行高：平时是范围汇总，出错或需要提醒时换成提示文字
            summaryLabel.Font = Theme.CaptionFont;
            summaryLabel.AutoSize = false;
            summaryLabel.AutoEllipsis = true;
            summaryLabel.UseMnemonic = false;
            summaryLabel.TextAlign = ContentAlignment.MiddleLeft;
            summaryLabel.SetBounds(24, 456, 552, 38);
            Controls.Add(summaryLabel);

            pathFrame = new Rectangle(24, 500, 452, 34);
            pathBox.BorderStyle = BorderStyle.None;
            pathBox.BackColor = Theme.SearchBg;
            pathBox.ForeColor = Theme.TextPrimary;
            pathBox.Font = Theme.BodyFont;
            pathBox.AccessibleName = "文件夹路径";
            pathBox.HandleCreated += delegate
            {
                SendMessage(pathBox.Handle, EM_SETCUEBANNER, (IntPtr)1, "粘贴或输入文件夹路径，也可以把文件夹拖进来");
            };
            pathBox.GotFocus += delegate { pathBox.BackColor = Color.White; Invalidate(); };
            pathBox.LostFocus += delegate { pathBox.BackColor = Theme.SearchBg; Invalidate(); };
            Controls.Add(pathBox);
            LayoutPathBox();

            addPathButton.Text = "添加";
            addPathButton.SetBounds(484, 500, 92, 34);
            addPathButton.Click += delegate { AddTypedPath(); };
            Controls.Add(addPathButton);

            startButton.Text = "开始扫描";
            startButton.SetBounds(366, 548, 110, 34);
            startButton.Click += delegate { StartScan(); };
            Controls.Add(startButton);

            cancelButton.Text = "取消";
            cancelButton.SetBounds(486, 548, 90, 34);
            cancelButton.Click += delegate { CancelDialog(); };
            Controls.Add(cancelButton);

            ApplyInitialRoots(initialRoots);
            RefreshRows(true);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            list.Focus();
            LoadDrivesAsync();
        }

        protected override void OnDpiScaleChanged()
        {
            base.OnDpiScaleChanged();
            list.RemoveZoneWidth = Dpi(RemoveZone);
            LayoutPathBox();
            RefreshRows(true);   // 行高不随窗体缩放自动变化，重新加入条目才会重新测量
            Invalidate(true);
        }

        /// <summary>单行输入框的高度由字体决定，不随窗体缩放：按当前 DPI 重新放到外框中间。</summary>
        private void LayoutPathBox()
        {
            pathBox.SetBounds(Dpi(pathFrame.X + 12), Dpi(pathFrame.Y) + (Dpi(pathFrame.Height) - pathBox.Height) / 2,
                Dpi(pathFrame.Width - 24), pathBox.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle frame = new Rectangle(Dpi(listFrame.X), Dpi(listFrame.Y), Dpi(listFrame.Width), Dpi(listFrame.Height));
            using (SolidBrush fill = new SolidBrush(Theme.Card)) Draw.FillRoundedRect(g, fill, frame, Dpi(8));
            using (Pen border = new Pen(Theme.ControlBorder, 1)) Draw.DrawRoundedRect(g, border, frame, Dpi(8));

            Rectangle input = new Rectangle(Dpi(pathFrame.X), Dpi(pathFrame.Y), Dpi(pathFrame.Width), Dpi(pathFrame.Height));
            bool focused = pathBox.Focused;
            using (SolidBrush fill = new SolidBrush(focused ? Color.White : Theme.SearchBg)) Draw.FillRoundedRect(g, fill, input, Dpi(6));
            using (Pen border = new Pen(focused ? Theme.Accent : Theme.ControlBorder, focused ? 1.4F : 1F))
                Draw.DrawRoundedRect(g, border, input, Dpi(6));
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { CancelDialog(); return true; }
            if (keyData == Keys.Enter)
            {
                // 自绘按钮不是系统默认按钮：焦点在哪个按钮上，回车就执行哪个；
                // 输入框里有内容时回车是添加路径；其余情况等同「开始扫描」
                if (addPathButton.Focused || (pathBox.Focused && pathBox.Text.Trim().Length > 0)) AddTypedPath();
                else if (cancelButton.Focused) CancelDialog();
                else StartScan();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            string[] dropped;
            try { dropped = e.Data.GetData(DataFormats.FileDrop) as string[]; }
            catch (ExternalException ex)
            {
                SetNotice("无法读取拖入的内容：" + ex.Message, true);
                RefreshSummary();
                return;
            }
            if (dropped == null || dropped.Length == 0) return;
            List<string> paths = new List<string>(dropped);
            int skipped = Math.Max(0, paths.Count - MaxDroppedPaths);
            if (skipped > 0) paths.RemoveRange(MaxDroppedPaths, skipped);
            bool added = AddPaths(paths);
            if (skipped > 0 && added)
            {
                SetNotice("一次最多拖入 " + MaxDroppedPaths + " 个文件夹，其余 " + skipped + " 个没有添加。", true);
                RefreshSummary();
            }
        }

        private void ApplyInitialRoots(IEnumerable<string> initialRoots)
        {
            if (initialRoots == null)
            {
                for (int i = 0; i < shortcuts.Count; i++)
                {
                    if (!shortcuts[i].Primary) continue;
                    AddRoot(shortcuts[i].Path, false);
                    break;
                }
                return;
            }
            foreach (string root in initialRoots) AddRoot(root, false);
        }

        private void LoadDrivesAsync()
        {
            try
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    List<LocalScanScopeDrive> loaded = null;
                    Exception failure = null;
                    try { loaded = LocalScanScope.Drives(); }
                    catch (Exception ex) { failure = ex; }
                    try
                    {
                        BeginInvoke((Action)delegate
                        {
                            if (IsDisposed || Disposing) return;
                            drives = loaded ?? new List<LocalScanScopeDrive>();
                            driveError = failure == null ? null : failure.Message;
                            RefreshRows(false);
                        });
                    }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                });
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        /// <summary>
        /// 在后台读取一个文件夹的下一级子文件夹。每次展开都重新读取，保证看到的是磁盘上的当前内容；
        /// 窗口已关闭或该行已收起时结果只存不显示。
        /// </summary>
        private void LoadChildren(string path)
        {
            LocalScanScopeChildren current;
            if (children.TryGetValue(path, out current) && current.Loading) return;
            children[path] = new LocalScanScopeChildren { Loading = true };
            try
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    LocalScanScopeChildren loaded;
                    try { loaded = LocalScanScope.ListSubfolders(path, excludedRoots); }
                    catch (Exception ex) { loaded = new LocalScanScopeChildren { Error = ex.Message }; }
                    try
                    {
                        BeginInvoke((Action)delegate
                        {
                            if (IsDisposed || Disposing) return;
                            children[path] = loaded;
                            if (expanded.Contains(path)) RefreshRows(false);
                        });
                    }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                });
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void SetNotice(string text, bool isError)
        {
            notice = text;
            noticeIsError = isError;
        }

        /// <summary>添加输入框里的路径；成功后清空输入框，失败时保留内容并在提示行说明原因。</summary>
        private void AddTypedPath()
        {
            if (pathBox.Text.Trim().Length == 0)
            {
                pathBox.Focus();
                return;
            }
            if (AddPaths(new string[] { pathBox.Text })) pathBox.Clear();
        }

        /// <summary>
        /// 加入手动输入或拖入的路径并勾选；逐个校验，通过的加入，没通过的在提示行汇总原因。
        /// 全部通过时返回 true。
        /// </summary>
        private bool AddPaths(IEnumerable<string> paths)
        {
            notice = null;
            string firstAdded = null, firstError = null;
            int failed = 0;
            foreach (string raw in paths)
            {
                string reason;
                string cleaned = LocalScanScope.CleanTypedPath(raw, out reason);
                if (cleaned == null && reason == null) continue;
                string normalized = null;
                if (cleaned != null && LocalScanScope.TryValidate(cleaned, excludedRoots, out normalized, out reason))
                {
                    if (IndexOfRoot(knownRoots, normalized) < 0) knownRoots.Add(normalized);
                    if (IndexOfRoot(selectedRoots, normalized) < 0) selectedRoots.Add(normalized);
                    if (firstAdded == null) firstAdded = normalized;
                    continue;
                }
                failed++;
                if (firstError == null) firstError = (cleaned ?? raw.Trim()) + "：" + reason;
            }
            if (failed == 1) SetNotice("没有添加 " + firstError, true);
            else if (failed > 1) SetNotice("有 " + failed + " 项没有添加，例如 " + firstError, true);
            RefreshRows(false);
            SelectRow(firstAdded);
            return failed == 0;
        }

        /// <summary>校验后把一个范围加入并勾选。校验失败时不改动任何状态；report 为 true 时在提示行说明原因。</summary>
        private bool AddRoot(string path, bool report)
        {
            string normalized;
            string reason;
            if (!LocalScanScope.TryValidate(path, excludedRoots, out normalized, out reason))
            {
                if (report) SetNotice("无法勾选 " + path + "：" + reason, true);
                return false;
            }
            if (IndexOfRoot(knownRoots, normalized) < 0) knownRoots.Add(normalized);
            if (IndexOfRoot(selectedRoots, normalized) < 0) selectedRoots.Add(normalized);
            return true;
        }

        private static int IndexOfRoot(List<string> roots, string path)
        {
            for (int i = 0; i < roots.Count; i++)
                if (string.Equals(roots[i], path, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static void RemoveRoot(List<string> roots, string path)
        {
            int index = IndexOfRoot(roots, path);
            if (index >= 0) roots.RemoveAt(index);
        }

        private LocalScanScopeRow RowAt(int index)
        {
            return index < 0 || index >= list.Items.Count ? null : list.Items[index] as LocalScanScopeRow;
        }

        private void Toggle(int index)
        {
            LocalScanScopeRow row = RowAt(index);
            if (row == null || !row.Enabled) return;
            notice = null;
            if (row.Checked)
            {
                RemoveRoot(selectedRoots, row.Path);
                // 自己添加的文件夹取消勾选后仍留在清单里；其余行本来就有固定位置，不用再记
                if (row.Kind != LocalScanScopeRowKind.Custom) RemoveRoot(knownRoots, row.Path);
            }
            else if (row.CoveredBy != null)
                SetNotice("「" + row.Title + "」已包含在 " + row.CoveredBy + " 中，会一并扫描；只想扫描它时，请先取消勾选 "
                    + row.CoveredBy + "。", false);
            else AddRoot(row.Path, true);
            RefreshRows(false);
        }

        /// <summary>展开或收起一行的子文件夹。</summary>
        private void ToggleExpand(int index)
        {
            LocalScanScopeRow row = RowAt(index);
            if (row == null || !row.Expandable) return;
            notice = null;
            if (!expanded.Remove(row.Path))
            {
                expanded.Add(row.Path);
                LoadChildren(row.Path);
            }
            RefreshRows(false);
        }

        /// <summary>把自己添加的文件夹从清单里去掉；常用文件夹和磁盘不能移除，只能取消勾选。</summary>
        private void RemoveCustom(int index)
        {
            LocalScanScopeRow row = RowAt(index);
            if (row == null || row.Kind != LocalScanScopeRowKind.Custom) return;
            notice = null;
            RemoveRoot(knownRoots, row.Path);
            RemoveRoot(selectedRoots, row.Path);
            RefreshRows(false);
        }

        /// <summary>勾选框左边缘相对行左边缘的位置；它左侧是缩进和展开箭头。</summary>
        private int CheckLeft(LocalScanScopeRow row)
        {
            return Dpi(IndentBase + row.Depth * IndentStep + ChevronWidth + 2);
        }

        private void List_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            int index = list.HitRow(e.Location);
            LocalScanScopeRow row = RowAt(index);
            if (row == null) return;
            if (list.InRemoveZone(index, e.Location)) RemoveCustom(index);
            else if (row.Expandable && e.X < CheckLeft(row)) ToggleExpand(index);
            else Toggle(index);
        }

        private void List_KeyDown(object sender, KeyEventArgs e)
        {
            LocalScanScopeRow row = RowAt(list.SelectedIndex);
            if (e.KeyCode == Keys.Space) Toggle(list.SelectedIndex);
            else if (e.KeyCode == Keys.Delete) RemoveCustom(list.SelectedIndex);
            else if (e.KeyCode == Keys.Down) MoveSelection(1);
            else if (e.KeyCode == Keys.Up) MoveSelection(-1);
            else if (e.KeyCode == Keys.Right)
            {
                if (row != null && row.Expandable && !row.Expanded) ToggleExpand(list.SelectedIndex);
            }
            else if (e.KeyCode == Keys.Left)
            {
                if (row != null && row.Expanded) ToggleExpand(list.SelectedIndex);
                else SelectParent();
            }
            else return;
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        /// <summary>上下键只在可勾选的行之间移动，跳过分组标题、提示行和不可用的磁盘。</summary>
        private void MoveSelection(int step)
        {
            int index = list.SelectedIndex;
            if (index < 0 && step < 0) index = list.Items.Count;
            for (index += step; index >= 0 && index < list.Items.Count; index += step)
            {
                LocalScanScopeRow row = RowAt(index);
                if (row == null || !row.Enabled) continue;
                list.SelectedIndex = index;
                return;
            }
        }

        /// <summary>左键在已收起的行上：跳到它的上一级文件夹。</summary>
        private void SelectParent()
        {
            LocalScanScopeRow current = RowAt(list.SelectedIndex);
            if (current == null || current.Depth == 0) return;
            for (int index = list.SelectedIndex - 1; index >= 0; index--)
            {
                LocalScanScopeRow row = RowAt(index);
                if (row == null || !row.Enabled || row.Depth >= current.Depth) continue;
                list.SelectedIndex = index;
                return;
            }
        }

        private void SelectRow(string path)
        {
            if (path == null) return;
            for (int i = 0; i < list.Items.Count; i++)
            {
                LocalScanScopeRow row = RowAt(i);
                if (row == null || !row.Enabled || !string.Equals(row.Path, path, StringComparison.OrdinalIgnoreCase)) continue;
                list.KeyboardActive = true;   // 高亮刚加入的一行，滚动到可见位置
                list.SelectedIndex = i;
                list.Invalidate();
                return;
            }
        }

        /// <summary>
        /// 按当前状态重算清单。行的种类、路径和层级都没变时只更新勾选等状态并重绘，避免整表重建时滚动位置跳动；
        /// rebuild 为 true 时强制重建（行高要重新测量）。
        /// </summary>
        private void RefreshRows(bool rebuild)
        {
            List<LocalScanScopeRow> rows = LocalScanScope.BuildRows(shortcuts, drives, driveError, knownRoots, selectedRoots,
                expanded, children);
            if (!rebuild && SameLayout(rows))
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    LocalScanScopeRow current = RowAt(i);
                    current.Title = rows[i].Title;
                    current.Detail = rows[i].Detail;
                    current.Enabled = rows[i].Enabled;
                    current.Checked = rows[i].Checked;
                    current.Warning = rows[i].Warning;
                    current.Expandable = rows[i].Expandable;
                    current.Expanded = rows[i].Expanded;
                    current.CoveredBy = rows[i].CoveredBy;
                }
                list.Invalidate();
            }
            else
            {
                int selectedIndex = list.SelectedIndex;
                LocalScanScopeRow selected = RowAt(selectedIndex);
                string selectedPath = selected == null ? null : selected.Path;
                int top = list.TopIndex;
                list.BeginUpdate();
                try
                {
                    list.Items.Clear();
                    list.Items.AddRange(rows.ToArray());
                }
                finally { list.EndUpdate(); }
                list.ResetHover();
                if (selectedPath != null)
                {
                    // 同一个文件夹可能同时出现在常用文件夹和磁盘的展开结果里：优先留在原来的位置
                    int target = selectedIndex < rows.Count && SamePath(rows[selectedIndex], selectedPath) ? selectedIndex : -1;
                    for (int i = 0; target < 0 && i < rows.Count; i++)
                        if (SamePath(rows[i], selectedPath)) target = i;
                    if (target >= 0) list.SelectedIndex = target;
                }
                if (top > 0 && top < list.Items.Count) list.TopIndex = top;
            }
            RefreshSummary();
        }

        private static bool SamePath(LocalScanScopeRow row, string path)
        {
            return row.Enabled && string.Equals(row.Path, path, StringComparison.OrdinalIgnoreCase);
        }

        private bool SameLayout(List<LocalScanScopeRow> rows)
        {
            if (rows.Count != list.Items.Count) return false;
            for (int i = 0; i < rows.Count; i++)
            {
                LocalScanScopeRow current = RowAt(i);
                if (current == null || current.Kind != rows[i].Kind || current.Depth != rows[i].Depth
                    || !string.Equals(current.Path, rows[i].Path, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        private void RefreshSummary()
        {
            LocalScanScopeResolution resolution = LocalScanScope.Resolve(selectedRoots, excludedRoots);
            if (notice != null)
            {
                summaryLabel.Text = notice;
                summaryLabel.ForeColor = noticeIsError ? Theme.DeleteRed : Theme.TextSecondary;
            }
            else
            {
                summaryLabel.Text = LocalScanScope.Summary(resolution);
                summaryLabel.ForeColor = resolution.Invalid.Count > 0 ? Theme.DeleteRed
                    : resolution.DriveCount > 0 ? Theme.AccentText : Theme.TextSecondary;
            }
            bool ready = resolution.Roots.Count > 0;
            startButton.Enabled = ready;
            startButton.Accent = ready;
        }

        /// <summary>
        /// 开始前再校验一次每个勾选的范围（磁盘可能已拔出、文件夹可能已删除）。
        /// 有不可用的就取消它们的勾选并在提示行说明，不开始扫描；用户确认剩下的范围后再点一次。
        /// </summary>
        private void StartScan()
        {
            if (selectedRoots.Count == 0) return;   // 没有勾选：按钮已禁用，汇总行已提示
            notice = null;
            List<string> dropped = new List<string>();
            for (int i = selectedRoots.Count - 1; i >= 0; i--)
            {
                string normalized;
                string reason;
                if (LocalScanScope.TryValidate(selectedRoots[i], excludedRoots, out normalized, out reason)) continue;
                dropped.Insert(0, selectedRoots[i] + "（" + reason.TrimEnd('。') + "）");
                selectedRoots.RemoveAt(i);
            }
            if (dropped.Count > 0)
            {
                string shown = dropped[0] + (dropped.Count > 1 ? "、" + dropped[1] : string.Empty)
                    + (dropped.Count > 2 ? " 等 " + dropped.Count + " 项" : string.Empty);
                SetNotice("已取消勾选当前不可用的范围：" + shown + "。"
                    + (selectedRoots.Count > 0 ? "确认其余范围后再点「开始扫描」。" : string.Empty), true);
                RefreshRows(false);
                return;
            }
            LocalScanScopeResolution resolution = LocalScanScope.Resolve(selectedRoots, excludedRoots);
            if (resolution.Roots.Count == 0)
            {
                RefreshRows(false);
                return;
            }
            ResultRoots = new List<string>(resolution.Roots);
            DialogResult = DialogResult.OK;
            Close();
        }

        private void CancelDialog()
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        // ===== 行绘制 =====

        private void MeasureRow(object sender, MeasureItemEventArgs e)
        {
            LocalScanScopeRow row = RowAt(e.Index);
            int height = row == null || row.Kind == LocalScanScopeRowKind.Header ? HeaderHeight
                : row.Kind == LocalScanScopeRowKind.Note ? NoteHeight
                : row.Kind == LocalScanScopeRowKind.Folder ? FolderRowHeight : RowHeight;
            e.ItemHeight = Dpi(height);
        }

        private void DrawRow(object sender, DrawItemEventArgs e)
        {
            LocalScanScopeRow item = RowAt(e.Index);
            Rectangle row = e.Bounds;
            if (item == null || row.Width <= 0 || row.Height <= 0) return;
            // 整行先画到缓冲再贴上去：悬停、勾选的重绘都不闪
            using (BufferedGraphics buffer = BufferedGraphicsManager.Current.Allocate(e.Graphics, row))
            {
                Graphics g = buffer.Graphics;
                // 底色先按整像素铺满，再开抗锯齿：否则行的上、左边缘会半透明地透出缓冲区的黑底
                using (SolidBrush back = new SolidBrush(Theme.Card)) g.FillRectangle(back, row);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                if (item.Kind == LocalScanScopeRowKind.Header)
                {
                    using (SolidBrush brush = new SolidBrush(Theme.TextSecondary))
                        Draw.DrawStringLeft(g, item.Title, Theme.FontOf(9F, FontStyle.Bold), brush,
                            new Rectangle(row.X + Dpi(12), row.Y + Dpi(6), row.Width - Dpi(24), row.Height - Dpi(6)));
                }
                else if (item.Kind == LocalScanScopeRowKind.Note)
                {
                    // 展开出来的提示行与同级文件夹的勾选框左对齐
                    int left = item.Depth == 0 ? Dpi(12) : CheckLeft(item);
                    using (SolidBrush brush = new SolidBrush(item.Warning ? Theme.DeleteRed : Theme.TextTertiary))
                        Draw.DrawStringLeft(g, item.Title, Theme.CaptionFont, brush,
                            new Rectangle(row.X + left, row.Y, Math.Max(0, row.Width - left - Dpi(12)), row.Height));
                }
                else DrawScopeRow(g, row, item, e.Index);
                buffer.Render(e.Graphics);
            }
        }

        private void DrawScopeRow(Graphics g, Rectangle row, LocalScanScopeRow item, int index)
        {
            bool active = item.Enabled && (index == list.HoverIndex || (list.KeyboardActive && index == list.SelectedIndex));
            if (item.Checked || active)
            {
                Rectangle highlight = new Rectangle(row.X + Dpi(2), row.Y + 1, row.Width - Dpi(4), row.Height - 2);
                using (SolidBrush b = new SolidBrush(item.Checked ? Theme.SelectionBg : Theme.RowHover))
                    Draw.FillRoundedRect(g, b, highlight, Dpi(6));
            }

            int box = Dpi(18);
            Rectangle check = new Rectangle(row.X + CheckLeft(item), row.Y + (row.Height - box) / 2, box, box);
            if (item.Expandable)
                DrawChevron(g, new Rectangle(check.X - Dpi(ChevronWidth + 2), row.Y, Dpi(ChevronWidth), row.Height), item.Expanded);
            DrawCheck(g, check, item);

            bool removable = item.Kind == LocalScanScopeRowKind.Custom;
            int textX = check.Right + Dpi(10);
            int textWidth = Math.Max(0, row.Right - textX - (removable ? list.RemoveZoneWidth : Dpi(10)));
            using (SolidBrush titleBrush = new SolidBrush(item.Enabled ? Theme.TextPrimary : Theme.TextTertiary))
            {
                // 展开出来的子文件夹只有一行名称；分组下的行是名称 + 说明两行
                Rectangle title = item.Kind == LocalScanScopeRowKind.Folder
                    ? new Rectangle(textX, row.Y, textWidth, row.Height)
                    : new Rectangle(textX, row.Y + Dpi(4), textWidth, Dpi(21));
                Draw.DrawStringLeft(g, item.Title, Theme.BodyFont, titleBrush, title);
            }
            if (item.Kind != LocalScanScopeRowKind.Folder)
            {
                string detail = item.Detail ?? string.Empty;
                if (item.CoveredBy != null) detail += "  ·  已包含在 " + item.CoveredBy + " 中";
                using (SolidBrush detailBrush = new SolidBrush(Theme.TextTertiary))
                    Draw.DrawStringLeft(g, detail, Theme.CaptionFont, detailBrush,
                        new Rectangle(textX, row.Y + Dpi(24), textWidth, Dpi(18)));
            }

            if (!removable) return;
            int size = Dpi(24);
            Rectangle remove = new Rectangle(row.Right - Dpi(8) - size, row.Y + (row.Height - size) / 2, size, size);
            bool hot = index == list.HoverIndex && list.HoverRemove;
            if (hot)
                using (SolidBrush b = new SolidBrush(Theme.ChipBg)) Draw.FillRoundedRect(g, b, remove, Dpi(6));
            using (Pen cross = new Pen(hot ? Theme.TextPrimary : Theme.TextTertiary, Math.Max(1.4F, Dpi(3) / 2F)))
            {
                cross.StartCap = LineCap.Round;
                cross.EndCap = LineCap.Round;
                float cx = remove.X + remove.Width / 2F, cy = remove.Y + remove.Height / 2F, arm = Dpi(4);
                g.DrawLine(cross, cx - arm, cy - arm, cx + arm, cy + arm);
                g.DrawLine(cross, cx - arm, cy + arm, cx + arm, cy - arm);
            }
        }

        /// <summary>展开箭头：收起时朝右，展开后朝下。</summary>
        private void DrawChevron(Graphics g, Rectangle box, bool isExpanded)
        {
            using (Pen pen = new Pen(Theme.TextSecondary, Math.Max(1.4F, Dpi(3) / 2F)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                float cx = box.X + box.Width / 2F, cy = box.Y + box.Height / 2F, arm = Dpi(4), half = arm / 2F;
                PointF[] points = isExpanded
                    ? new PointF[] { new PointF(cx - arm, cy - half), new PointF(cx, cy + half), new PointF(cx + arm, cy - half) }
                    : new PointF[] { new PointF(cx - half, cy - arm), new PointF(cx + half, cy), new PointF(cx - half, cy + arm) };
                g.DrawLines(pen, points);
            }
        }

        private void DrawCheck(Graphics g, Rectangle r, LocalScanScopeRow item)
        {
            if (!item.Checked && !item.Implied)
            {
                using (SolidBrush fill = new SolidBrush(item.Enabled ? Theme.Card : Theme.SearchBg)) Draw.FillRoundedRect(g, fill, r, Dpi(4));
                using (Pen border = new Pen(item.Enabled ? Theme.ControlBorder : Theme.Separator, Math.Max(1F, Dpi(3) / 2F)))
                    Draw.DrawRoundedRect(g, border, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), Dpi(4));
                return;
            }
            // 上级已勾选、自己没勾选：用浅色勾表示“随上级一并扫描”
            Color color = item.Checked ? Theme.Accent : Color.FromArgb(150, 194, 255);
            using (SolidBrush fill = new SolidBrush(color)) Draw.FillRoundedRect(g, fill, r, Dpi(4));
            using (Pen mark = new Pen(Color.White, Math.Max(1.6F, Dpi(2))))
            {
                mark.StartCap = LineCap.Round;
                mark.EndCap = LineCap.Round;
                mark.LineJoin = LineJoin.Round;
                float w = r.Width, h = r.Height;
                g.DrawLines(mark, new PointF[]
                {
                    new PointF(r.X + w * 0.26F, r.Y + h * 0.52F),
                    new PointF(r.X + w * 0.44F, r.Y + h * 0.70F),
                    new PointF(r.X + w * 0.76F, r.Y + h * 0.34F)
                });
            }
        }

        /// <summary>
        /// 自绘行的清单：行高随行类型变化，记录鼠标悬停行和是否悬停在「移除」上，
        /// 并且不整块擦除背景（行由 DrawItem 整行重绘）。
        /// </summary>
        private sealed class ScopeList : ListBox
        {
            private const int WM_ERASEBKGND = 0x0014;

            internal int HoverIndex = -1;
            internal bool HoverRemove;
            internal bool KeyboardActive;   // 最近一次操作来自键盘：此时才高亮键盘选中的行
            internal int RemoveZoneWidth;

            internal ScopeList()
            {
                DrawMode = DrawMode.OwnerDrawVariable;
                BorderStyle = BorderStyle.None;
                IntegralHeight = false;
                SelectionMode = SelectionMode.One;
                BackColor = Theme.Card;
                ForeColor = Theme.TextPrimary;
                Font = Theme.BodyFont;
            }

            /// <summary>坐标所在的行；落在最后一行以下的空白处返回 -1。</summary>
            internal int HitRow(Point p)
            {
                int index = IndexFromPoint(p);
                if (index < 0 || index >= Items.Count) return -1;
                return GetItemRectangle(index).Contains(p) ? index : -1;
            }

            /// <summary>坐标是否落在自己添加的文件夹行右侧的「移除」区域。</summary>
            internal bool InRemoveZone(int index, Point p)
            {
                if (index < 0 || index >= Items.Count) return false;
                LocalScanScopeRow row = Items[index] as LocalScanScopeRow;
                return row != null && row.Kind == LocalScanScopeRowKind.Custom
                    && p.X >= GetItemRectangle(index).Right - RemoveZoneWidth;
            }

            internal void InvalidateRow(int index)
            {
                if (index < 0 || index >= Items.Count || !IsHandleCreated) return;
                Invalidate(GetItemRectangle(index));
            }

            internal void ResetHover()
            {
                HoverIndex = -1;
                HoverRemove = false;
                KeyboardActive = false;
            }

            private void SetHover(int index, bool remove)
            {
                if (index == HoverIndex && remove == HoverRemove) return;
                int old = HoverIndex;
                HoverIndex = index;
                HoverRemove = remove;
                InvalidateRow(old);
                InvalidateRow(index);
            }

            private void SetHover(Point p)
            {
                int index = HitRow(p);
                SetHover(index, InRemoveZone(index, p));
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                if (KeyboardActive)
                {
                    KeyboardActive = false;
                    InvalidateRow(SelectedIndex);
                }
                SetHover(e.Location);
                base.OnMouseMove(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                SetHover(-1, false);
                base.OnMouseLeave(e);
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                SetHover(PointToClient(MousePosition));   // 滚动后鼠标下换了一行
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (!KeyboardActive)
                {
                    KeyboardActive = true;
                    InvalidateRow(SelectedIndex);
                }
                base.OnKeyDown(e);
            }

            protected override void OnKeyPress(KeyPressEventArgs e)
            {
                // 条目是对象而非文本：屏蔽列表自带的“按首字母跳转”，否则按键会让选中行乱跳
                e.Handled = true;
                base.OnKeyPress(e);
            }

            protected override void OnSelectedIndexChanged(EventArgs e)
            {
                base.OnSelectedIndexChanged(e);
                Invalidate();   // 自绘行不随选中变化自动重绘
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg != WM_ERASEBKGND || m.WParam == IntPtr.Zero)
                {
                    base.WndProc(ref m);
                    return;
                }
                // 只擦最后一行以下的空白：行区域交给 DrawItem，避免先擦后画造成闪烁
                int bottom = Items.Count == 0 ? 0 : GetItemRectangle(Items.Count - 1).Bottom;
                if (bottom < ClientSize.Height)
                {
                    using (Graphics g = Graphics.FromHdc(m.WParam))
                    using (SolidBrush back = new SolidBrush(BackColor))
                        g.FillRectangle(back, 0, Math.Max(0, bottom), ClientSize.Width, ClientSize.Height - Math.Max(0, bottom));
                }
                m.Result = (IntPtr)1;
            }
        }
    }
}
