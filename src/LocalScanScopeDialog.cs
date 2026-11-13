using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;
using AppManager.Controls;

namespace AppManager
{
    /// <summary>
    /// 照片/视频/音乐共用的扫描范围选择窗口：常用文件夹、磁盘和自己添加的文件夹列在同一个清单里，勾上即纳入扫描。
    /// 磁盘列表在后台读取，文件夹选择留在 UI 线程。只返回已校验、已去重并合并重叠项的根目录列表。
    /// </summary>
    internal sealed class LocalScanScopeDialog : BufferedDialogForm
    {
        private const int HeaderHeight = 30;
        private const int NoteHeight = 34;
        private const int RowHeight = 46;
        private const int RemoveZone = 40;

        private readonly string dialogTitle;
        private readonly List<string> excludedRoots;
        private readonly List<LocalScanScopeShortcut> shortcuts;
        private readonly List<string> selectedRoots = new List<string>();
        // 本次打开后出现过的全部范围；取消勾选后仍留在清单里，只有点「移除」才去掉
        private readonly List<string> knownRoots = new List<string>();
        private readonly ScopeList list = new ScopeList();
        private readonly Label summaryLabel = new Label();
        private readonly RoundButton addFolderButton = new RoundButton();
        private readonly RoundButton startButton = new RoundButton();
        private readonly RoundButton cancelButton = new RoundButton();
        private readonly Rectangle listFrame;   // 列表外框（96 DPI 设计坐标），绘制时按当前 DPI 换算
        private List<LocalScanScopeDrive> drives;   // null：仍在后台读取
        private string driveError;

        internal List<string> ResultRoots = new List<string>();

        internal LocalScanScopeDialog(string title, LocalScanScopeKind kind, IEnumerable<string> initialRoots,
            IEnumerable<string> excludedRoots)
        {
            dialogTitle = title;
            this.excludedRoots = excludedRoots == null
                ? new List<string>() : new List<string>(excludedRoots);
            shortcuts = LocalScanScope.Shortcuts(kind);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(600, 590);
            BackColor = Theme.Background;
            ShowInTaskbar = false;
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
            hint.Text = "勾选要扫描的位置，默认包含子文件夹；资源存储位置和程序数据目录会自动跳过。";
            hint.Font = Theme.CaptionFont;
            hint.ForeColor = Theme.TextSecondary;
            hint.AutoSize = false;
            hint.AutoEllipsis = true;
            hint.SetBounds(24, 48, 552, 22);
            Controls.Add(hint);

            listFrame = new Rectangle(24, 80, 552, 406);
            list.SetBounds(listFrame.X + 4, listFrame.Y + 4, listFrame.Width - 8, listFrame.Height - 8);
            list.RemoveZoneWidth = RemoveZone;
            list.AccessibleName = "扫描范围";
            list.MeasureItem += MeasureRow;
            list.DrawItem += DrawRow;
            list.MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                int index = list.HitRow(e.Location);
                if (list.InRemoveZone(index, e.Location)) RemoveCustom(index);
                else Toggle(index);
            };
            list.KeyDown += List_KeyDown;
            Controls.Add(list);

            summaryLabel.Font = Theme.CaptionFont;
            summaryLabel.AutoSize = false;
            summaryLabel.AutoEllipsis = true;
            summaryLabel.UseMnemonic = false;
            summaryLabel.TextAlign = ContentAlignment.MiddleLeft;
            summaryLabel.SetBounds(24, 494, 552, 24);
            Controls.Add(summaryLabel);

            addFolderButton.Text = "添加文件夹…";
            addFolderButton.SetBounds(24, 532, 124, 34);
            addFolderButton.Click += delegate { AddFolder(); };
            Controls.Add(addFolderButton);

            startButton.Text = "开始扫描";
            startButton.SetBounds(366, 532, 110, 34);
            startButton.Click += delegate { StartScan(); };
            Controls.Add(startButton);

            cancelButton.Text = "取消";
            cancelButton.SetBounds(486, 532, 90, 34);
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
            RefreshRows(true);   // 行高不随窗体缩放自动变化，重新加入条目才会重新测量
            Invalidate(true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle frame = new Rectangle(Dpi(listFrame.X), Dpi(listFrame.Y), Dpi(listFrame.Width), Dpi(listFrame.Height));
            using (SolidBrush fill = new SolidBrush(Theme.Card)) Draw.FillRoundedRect(g, fill, frame, Dpi(8));
            using (Pen border = new Pen(Theme.ControlBorder, 1)) Draw.DrawRoundedRect(g, border, frame, Dpi(8));
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { CancelDialog(); return true; }
            if (keyData == Keys.Enter)
            {
                // 自绘按钮不是系统默认按钮：焦点在哪个按钮上，回车就执行哪个；其余情况等同「开始扫描」
                if (addFolderButton.Focused) AddFolder();
                else if (cancelButton.Focused) CancelDialog();
                else StartScan();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
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

        private void AddFolder()
        {
            string picked;
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择要扫描的文件夹";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                picked = dialog.SelectedPath;
            }
            if (!AddRoot(picked, true)) return;
            RefreshRows(false);
            SelectRow(LocalScanScope.NormalizePath(picked));
        }

        /// <summary>校验后把一个范围加入清单并勾选；已在清单里的只补上勾选。校验失败时不改动任何状态。</summary>
        private bool AddRoot(string path, bool showError)
        {
            string normalized;
            string reason;
            if (!LocalScanScope.TryValidate(path, excludedRoots, out normalized, out reason))
            {
                if (showError)
                    MessageBox.Show(this, (string.IsNullOrWhiteSpace(path) ? string.Empty : path + "\n\n") + reason,
                        dialogTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

        private LocalScanScopeRow RowAt(int index)
        {
            return index < 0 || index >= list.Items.Count ? null : list.Items[index] as LocalScanScopeRow;
        }

        private void Toggle(int index)
        {
            LocalScanScopeRow row = RowAt(index);
            if (row == null || !row.Enabled) return;
            if (row.Checked)
            {
                int selected = IndexOfRoot(selectedRoots, row.Path);
                if (selected >= 0) selectedRoots.RemoveAt(selected);
            }
            else if (!AddRoot(row.Path, true)) return;
            RefreshRows(false);
        }

        /// <summary>把自己添加的文件夹从清单里去掉；常用文件夹和磁盘不能移除，只能取消勾选。</summary>
        private void RemoveCustom(int index)
        {
            LocalScanScopeRow row = RowAt(index);
            if (row == null || row.Kind != LocalScanScopeRowKind.Custom) return;
            int known = IndexOfRoot(knownRoots, row.Path);
            if (known >= 0) knownRoots.RemoveAt(known);
            int selected = IndexOfRoot(selectedRoots, row.Path);
            if (selected >= 0) selectedRoots.RemoveAt(selected);
            RefreshRows(false);
        }

        private void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) Toggle(list.SelectedIndex);
            else if (e.KeyCode == Keys.Delete) RemoveCustom(list.SelectedIndex);
            else if (e.KeyCode == Keys.Down) MoveSelection(1);
            else if (e.KeyCode == Keys.Up) MoveSelection(-1);
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
        /// 按当前状态重算清单。分组和条目没变时只更新勾选等状态并重绘，避免整表重建时滚动位置跳动；
        /// rebuild 为 true 时强制重建（行高要重新测量）。
        /// </summary>
        private void RefreshRows(bool rebuild)
        {
            List<LocalScanScopeRow> rows = LocalScanScope.BuildRows(shortcuts, drives, driveError, knownRoots, selectedRoots);
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
                    current.CoveredBy = rows[i].CoveredBy;
                }
                list.Invalidate();
            }
            else
            {
                LocalScanScopeRow selected = RowAt(list.SelectedIndex);
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
                for (int i = 0; selectedPath != null && i < rows.Count; i++)
                {
                    if (!rows[i].Enabled || !string.Equals(rows[i].Path, selectedPath, StringComparison.OrdinalIgnoreCase)) continue;
                    list.SelectedIndex = i;
                    break;
                }
                if (top > 0 && top < list.Items.Count) list.TopIndex = top;
            }
            RefreshSummary();
        }

        private bool SameLayout(List<LocalScanScopeRow> rows)
        {
            if (rows.Count != list.Items.Count) return false;
            for (int i = 0; i < rows.Count; i++)
            {
                LocalScanScopeRow current = RowAt(i);
                if (current == null || current.Kind != rows[i].Kind
                    || !string.Equals(current.Path, rows[i].Path, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        private void RefreshSummary()
        {
            LocalScanScopeResolution resolution = LocalScanScope.Resolve(selectedRoots, excludedRoots);
            summaryLabel.Text = LocalScanScope.Summary(resolution);
            summaryLabel.ForeColor = resolution.Invalid.Count > 0 ? Theme.DeleteRed
                : resolution.DriveCount > 0 ? Theme.AccentText : Theme.TextSecondary;
            bool ready = resolution.Roots.Count > 0;
            startButton.Enabled = ready;
            startButton.Accent = ready;
        }

        private void StartScan()
        {
            if (selectedRoots.Count == 0) return;   // 没有勾选：按钮已禁用，汇总行已提示
            LocalScanScopeResolution resolution = LocalScanScope.Resolve(selectedRoots, excludedRoots);
            if (resolution.Invalid.Count > 0)
            {
                string message = "以下范围当前不可用：\n\n" + JoinLines(resolution.Invalid)
                    + "\n\n是否排除这些范围并继续扫描可用部分？";
                if (MessageBox.Show(this, message, dialogTitle, MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes) return;
                selectedRoots.Clear();
                selectedRoots.AddRange(resolution.Roots);
                RefreshRows(false);
            }
            if (resolution.Roots.Count == 0)
            {
                MessageBox.Show(this, "没有可用的扫描范围。请勾选磁盘或添加文件夹。", dialogTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
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

        private static string JoinLines(List<string> lines)
        {
            int count = Math.Min(5, lines.Count);
            string text = string.Empty;
            for (int i = 0; i < count; i++) text += (i == 0 ? "" : "\n") + lines[i];
            if (lines.Count > count) text += "\n…另有 " + (lines.Count - count) + " 项";
            return text;
        }

        // ===== 行绘制 =====

        private void MeasureRow(object sender, MeasureItemEventArgs e)
        {
            LocalScanScopeRow row = RowAt(e.Index);
            int height = row == null || row.Kind == LocalScanScopeRowKind.Header ? HeaderHeight
                : row.Kind == LocalScanScopeRowKind.Note ? NoteHeight : RowHeight;
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
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush back = new SolidBrush(Theme.Card)) g.FillRectangle(back, row);

                if (item.Kind == LocalScanScopeRowKind.Header)
                {
                    using (SolidBrush brush = new SolidBrush(Theme.TextSecondary))
                        Draw.DrawStringLeft(g, item.Title, Theme.FontOf(9F, FontStyle.Bold), brush,
                            new Rectangle(row.X + Dpi(12), row.Y + Dpi(6), row.Width - Dpi(24), row.Height - Dpi(6)));
                }
                else if (item.Kind == LocalScanScopeRowKind.Note)
                {
                    using (SolidBrush brush = new SolidBrush(item.Warning ? Theme.DeleteRed : Theme.TextTertiary))
                        Draw.DrawStringLeft(g, item.Title, Theme.CaptionFont, brush,
                            new Rectangle(row.X + Dpi(12), row.Y, row.Width - Dpi(24), row.Height));
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
            Rectangle check = new Rectangle(row.X + Dpi(12), row.Y + (row.Height - box) / 2, box, box);
            DrawCheck(g, check, item.Checked, item.Enabled);

            bool removable = item.Kind == LocalScanScopeRowKind.Custom;
            int textX = check.Right + Dpi(12);
            int textWidth = Math.Max(0, row.Right - textX - (removable ? list.RemoveZoneWidth : Dpi(10)));
            using (SolidBrush titleBrush = new SolidBrush(item.Enabled ? Theme.TextPrimary : Theme.TextTertiary))
                Draw.DrawStringLeft(g, item.Title, Theme.BodyFont, titleBrush,
                    new Rectangle(textX, row.Y + Dpi(4), textWidth, Dpi(21)));
            string detail = item.Detail ?? string.Empty;
            if (item.CoveredBy != null) detail += "  ·  已包含在 " + item.CoveredBy + " 中";
            using (SolidBrush detailBrush = new SolidBrush(Theme.TextTertiary))
                Draw.DrawStringLeft(g, detail, Theme.CaptionFont, detailBrush,
                    new Rectangle(textX, row.Y + Dpi(24), textWidth, Dpi(18)));

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

        private void DrawCheck(Graphics g, Rectangle r, bool isChecked, bool enabled)
        {
            if (!isChecked)
            {
                using (SolidBrush fill = new SolidBrush(enabled ? Theme.Card : Theme.SearchBg)) Draw.FillRoundedRect(g, fill, r, Dpi(4));
                using (Pen border = new Pen(enabled ? Theme.ControlBorder : Theme.Separator, Math.Max(1F, Dpi(3) / 2F)))
                    Draw.DrawRoundedRect(g, border, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), Dpi(4));
                return;
            }
            using (SolidBrush fill = new SolidBrush(Theme.Accent)) Draw.FillRoundedRect(g, fill, r, Dpi(4));
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
