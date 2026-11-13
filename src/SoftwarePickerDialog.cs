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
    /// 「添加软件」对话框：从已安装的软件里勾选，加入当前分类。
    /// 默认只列用户安装的软件，Windows 自带的系统工具需打开「显示系统工具」才列出；支持按名称搜索、全选。
    /// 图标在后台线程提取，位图归对话框所有，关闭时统一释放（不动主列表条目上的图标缓存）。
    /// </summary>
    internal sealed class SoftwarePickerDialog : BufferedDialogForm
    {
        private const int RowHeight = 46;
        private const int IconPx = 28;

        private readonly List<AppInfo> candidates;
        private readonly HashSet<AppInfo> chosen = new HashSet<AppInfo>();
        private readonly int systemCount;

        private readonly SearchField search = new SearchField();
        private readonly PickerList list = new PickerList();
        private readonly Label summary = new Label();
        private readonly Label empty = new Label();
        private readonly ToggleSwitch systemToggle = new ToggleSwitch();
        private readonly RoundButton selectAll = new RoundButton();
        private readonly RoundButton ok = new RoundButton();
        private Rectangle listFrame;   // 列表外框（96 DPI 设计坐标），绘制时按当前 DPI 换算

        // 图标：后台线程把提取结果放进 loaded，界面线程取走放入 icons；closed 之后的结果由后台线程自行释放
        private readonly object iconLock = new object();
        private readonly List<KeyValuePair<AppInfo, Bitmap>> loaded = new List<KeyValuePair<AppInfo, Bitmap>>();
        private readonly Dictionary<AppInfo, Bitmap> icons = new Dictionary<AppInfo, Bitmap>();
        private bool closed;
        private bool iconLoadStarted;
        private int drainPending;

        /// <summary>用户点了「浏览其它程序…」：由调用方改用文件浏览器选择 exe。</summary>
        internal bool BrowseRequested { get; private set; }

        /// <summary>确认添加的条目（按列表顺序）；取消或改用文件浏览器时为空。</summary>
        internal List<AppInfo> Picked { get; private set; }

        internal SoftwarePickerDialog(string category, List<AppInfo> candidates, bool showSystem)
        {
            this.candidates = candidates ?? new List<AppInfo>();
            Picked = new List<AppInfo>();
            foreach (AppInfo a in this.candidates)
                if (Scanner.IsSystemComponent(a)) systemCount++;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 640);
            BackColor = Theme.Background;
            ShowInTaskbar = false;
            Text = "添加软件";

            Label title = new Label();
            title.Text = "添加软件到「" + category + "」";
            title.Font = Theme.DialogTitleFont;
            title.ForeColor = Theme.TextPrimary;
            title.AutoSize = false;
            title.AutoEllipsis = true;
            title.UseMnemonic = false;
            title.SetBounds(24, 18, 512, 28);
            Controls.Add(title);

            Label hint = new Label();
            hint.Text = "勾选已安装的软件加入此分类；只调整归属，不移动程序文件。";
            hint.Font = Theme.CaptionFont;
            hint.ForeColor = Theme.TextSecondary;
            hint.AutoSize = false;
            hint.SetBounds(24, 48, 512, 22);
            Controls.Add(hint);

            search.Placeholder = "搜索软件名称…";
            search.SetBounds(24, 78, 512, 34);
            search.TextChanged += delegate { RefreshList(); };
            Controls.Add(search);

            summary.Font = Theme.CaptionFont;
            summary.ForeColor = Theme.TextSecondary;
            summary.AutoSize = false;
            summary.AutoEllipsis = true;
            summary.TextAlign = ContentAlignment.MiddleLeft;
            summary.SetBounds(24, 122, 286, 28);
            Controls.Add(summary);

            Label toggleLabel = new Label();
            toggleLabel.Text = "显示系统工具";
            toggleLabel.Font = Theme.CaptionFont;
            toggleLabel.ForeColor = Theme.TextSecondary;
            toggleLabel.AutoSize = false;
            toggleLabel.TextAlign = ContentAlignment.MiddleRight;
            toggleLabel.SetBounds(314, 122, 92, 28);
            toggleLabel.Click += delegate { systemToggle.Checked = !systemToggle.Checked; };
            Controls.Add(toggleLabel);

            systemToggle.SetBounds(412, 124, 40, 24);
            systemToggle.Checked = showSystem;
            systemToggle.AccessibleName = "显示系统工具";
            systemToggle.CheckedChanged += delegate { RefreshList(); };
            Controls.Add(systemToggle);

            selectAll.Text = "全选";
            selectAll.SetBounds(464, 122, 72, 28);
            selectAll.Click += delegate { ToggleAllVisible(); };
            Controls.Add(selectAll);

            listFrame = new Rectangle(24, 160, 512, 406);
            list.SetBounds(listFrame.X + 4, listFrame.Y + 4, listFrame.Width - 8, listFrame.Height - 8);
            list.ItemHeight = RowHeight;
            list.AccessibleName = "可添加的软件";
            list.DrawItem += DrawRow;
            list.MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) Toggle(list.HitRow(e.Location));
            };
            list.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Space) return;
                Toggle(list.SelectedIndex);
                e.Handled = true;
                e.SuppressKeyPress = true;
            };
            Controls.Add(list);

            empty.Font = Theme.BodyFont;
            empty.ForeColor = Theme.TextTertiary;
            empty.BackColor = Theme.Card;
            empty.AutoSize = false;
            empty.TextAlign = ContentAlignment.MiddleCenter;
            empty.Bounds = list.Bounds;
            empty.Visible = false;
            Controls.Add(empty);
            empty.BringToFront();

            RoundButton browse = new RoundButton();
            browse.Text = "浏览其它程序…";
            browse.SetBounds(24, 582, 136, 34);
            browse.Click += delegate { BrowseRequested = true; Close(); };
            Controls.Add(browse);

            ok.SetBounds(326, 582, 110, 34);
            ok.Click += delegate { Confirm(); };
            Controls.Add(ok);

            RoundButton cancel = new RoundButton();
            cancel.Text = "取消";
            cancel.SetBounds(446, 582, 90, 34);
            cancel.Click += delegate { Close(); };
            Controls.Add(cancel);

            RefreshList();
        }

        /// <summary>
        /// 按「是否显示系统工具」和搜索词筛选候选，保持候选原有顺序；名称直接包含搜索词的排在模糊匹配之前。
        /// internal 供回归测试。
        /// </summary>
        internal static List<AppInfo> Filter(IList<AppInfo> candidates, string keyword, bool includeSystem)
        {
            List<AppInfo> direct = new List<AppInfo>();
            List<AppInfo> fuzzy = new List<AppInfo>();
            if (candidates == null) return direct;
            string kw = (keyword ?? string.Empty).Trim();
            for (int i = 0; i < candidates.Count; i++)
            {
                AppInfo a = candidates[i];
                if (a == null) continue;
                if (!includeSystem && Scanner.IsSystemComponent(a)) continue;
                string name = a.Name ?? string.Empty;
                if (kw.Length == 0 || name.IndexOf(kw, StringComparison.CurrentCultureIgnoreCase) >= 0) direct.Add(a);
                else if (Scanner.FuzzyMatch(name, kw)) fuzzy.Add(a);
            }
            direct.AddRange(fuzzy);
            return direct;
        }

        /// <summary>列表第二行的说明：所在分类 + 程序位置（应用商店应用没有可显示的路径）。internal 供回归测试。</summary>
        internal static string Describe(AppInfo app)
        {
            if (app == null) return string.Empty;
            string where;
            if (!string.IsNullOrEmpty(app.LinkTarget) && !app.LinkTarget.StartsWith("::", StringComparison.Ordinal))
                where = app.LinkTarget;
            else if (app.IsUwp || Scanner.IsAppsFolderTarget(app.TargetPath))
                where = Scanner.IsSystemComponent(app) ? "系统组件" : "应用商店应用";
            else
                where = app.TargetPath ?? string.Empty;
            if (string.IsNullOrEmpty(app.Category)) return where;
            return string.IsNullOrEmpty(where) ? app.Category : app.Category + "  ·  " + where;
        }

        private void RefreshList()
        {
            string keyword = search.Text;
            bool includeSystem = systemToggle.Checked;
            List<AppInfo> visible = Filter(candidates, keyword, includeSystem);
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                list.Items.AddRange(visible.ToArray());
            }
            finally { list.EndUpdate(); }
            list.ResetHover();

            bool searching = (keyword ?? string.Empty).Trim().Length > 0;
            // 关着「显示系统工具」时，说明还有多少项没列出来，避免以为软件丢了
            int hidden = includeSystem ? 0 : Filter(candidates, keyword, true).Count - visible.Count;
            summary.Text = (searching ? "找到 " : "共 ") + visible.Count + " 个"
                + (hidden > 0 ? " · 另有 " + hidden + " 个系统工具未显示" : string.Empty);
            if (visible.Count == 0)
            {
                if (hidden > 0)
                    empty.Text = "没有" + (searching ? "匹配的" : "可添加的") + "已安装软件。\n打开「显示系统工具」还有 " + hidden + " 项。";
                else if (candidates.Count == 0)
                    empty.Text = "没有可添加的已安装软件。\n便携程序可点「浏览其它程序…」选择 .exe。";
                else
                    empty.Text = "没有匹配的软件。\n便携程序可点「浏览其它程序…」选择 .exe。";
            }
            empty.Visible = visible.Count == 0;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            ok.Text = "添加 (" + chosen.Count + ")";
            ok.Accent = chosen.Count > 0;
            ok.Enabled = chosen.Count > 0;
            selectAll.Enabled = list.Items.Count > 0;
            selectAll.Text = list.Items.Count > 0 && AllVisibleChosen() ? "取消全选" : "全选";
        }

        private bool AllVisibleChosen()
        {
            for (int i = 0; i < list.Items.Count; i++)
                if (!chosen.Contains((AppInfo)list.Items[i])) return false;
            return true;
        }

        /// <summary>全选只作用于当前列出的条目（受搜索和「显示系统工具」影响）；已全选时改为取消这些条目。</summary>
        private void ToggleAllVisible()
        {
            if (list.Items.Count == 0) return;
            bool clear = AllVisibleChosen();
            for (int i = 0; i < list.Items.Count; i++)
            {
                AppInfo app = (AppInfo)list.Items[i];
                if (clear) chosen.Remove(app);
                else chosen.Add(app);
            }
            list.Invalidate();
            UpdateButtons();
        }

        private void Toggle(int index)
        {
            if (index < 0 || index >= list.Items.Count) return;
            AppInfo app = (AppInfo)list.Items[index];
            if (!chosen.Remove(app)) chosen.Add(app);
            list.InvalidateRow(index);
            UpdateButtons();
        }

        private void Confirm()
        {
            if (chosen.Count == 0) return;
            List<AppInfo> picked = new List<AppInfo>();
            foreach (AppInfo app in candidates)
                if (chosen.Contains(app)) picked.Add(app);
            Picked = picked;
            Close();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == Keys.Enter) { Confirm(); return true; }
            if (keyData == (Keys.Control | Keys.A) && list.Focused) { ToggleAllVisible(); return true; }
            if (keyData == Keys.Down && search.ContainsFocus && list.Items.Count > 0)
            {
                list.Focus();
                if (list.SelectedIndex < 0) list.SelectedIndex = 0;
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            search.FocusInput();
            StartIconLoad();
        }

        protected override void OnDpiScaleChanged()
        {
            base.OnDpiScaleChanged();
            list.ItemHeight = Dpi(RowHeight);   // 行高不随窗体缩放自动变化
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

        private void DrawRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= list.Items.Count) return;
            AppInfo app = (AppInfo)list.Items[e.Index];
            Rectangle row = e.Bounds;
            if (row.Width <= 0 || row.Height <= 0) return;
            // 整行先画到缓冲再贴上去：悬停、勾选、图标到位时的重绘都不闪
            using (BufferedGraphics buffer = BufferedGraphicsManager.Current.Allocate(e.Graphics, row))
            {
                Graphics g = buffer.Graphics;
                // 底色先按整像素铺满，再开抗锯齿：否则行的上、左边缘会半透明地透出缓冲区的黑底
                using (SolidBrush back = new SolidBrush(Theme.Card)) g.FillRectangle(back, row);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                bool isChosen = chosen.Contains(app);
                bool active = e.Index == list.HoverIndex || (list.KeyboardActive && e.Index == list.SelectedIndex);
                if (isChosen || active)
                {
                    Rectangle highlight = new Rectangle(row.X + Dpi(2), row.Y + 1, row.Width - Dpi(4), row.Height - 2);
                    using (SolidBrush b = new SolidBrush(isChosen ? Theme.SelectionBg : Theme.RowHover))
                        Draw.FillRoundedRect(g, b, highlight, Dpi(6));
                }

                int box = Dpi(18);
                Rectangle check = new Rectangle(row.X + Dpi(12), row.Y + (row.Height - box) / 2, box, box);
                DrawCheck(g, check, isChosen);

                int iconSize = Dpi(IconPx);
                Rectangle iconRect = new Rectangle(check.Right + Dpi(12), row.Y + (row.Height - iconSize) / 2, iconSize, iconSize);
                Bitmap icon;
                if (icons.TryGetValue(app, out icon) && icon != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(icon, iconRect);
                }
                else
                {
                    using (SolidBrush placeholder = new SolidBrush(Theme.ChipBg))
                        Draw.FillRoundedRect(g, placeholder, iconRect, Dpi(6));
                }

                int textX = iconRect.Right + Dpi(12);
                int textWidth = Math.Max(0, row.Right - textX - Dpi(10));
                using (SolidBrush nameBrush = new SolidBrush(Theme.TextPrimary))
                    Draw.DrawStringLeft(g, app.Name, Theme.BodyFont, nameBrush,
                        new Rectangle(textX, row.Y + Dpi(4), textWidth, Dpi(21)));
                using (SolidBrush detailBrush = new SolidBrush(Theme.TextTertiary))
                    Draw.DrawStringLeft(g, Describe(app), Theme.CaptionFont, detailBrush,
                        new Rectangle(textX, row.Y + Dpi(24), textWidth, Dpi(18)));
                buffer.Render(e.Graphics);
            }
        }

        private void DrawCheck(Graphics g, Rectangle r, bool isChecked)
        {
            if (!isChecked)
            {
                using (SolidBrush fill = new SolidBrush(Theme.Card)) Draw.FillRoundedRect(g, fill, r, Dpi(4));
                using (Pen border = new Pen(Theme.ControlBorder, Math.Max(1F, Dpi(3) / 2F)))
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

        // ===== 图标后台加载 =====

        private void StartIconLoad()
        {
            if (iconLoadStarted || candidates.Count == 0) return;
            iconLoadStarted = true;
            int size = Dpi(IconPx);
            // 先取当前列出的条目，再取被筛掉的（之后搜索或打开系统工具时才会看到）
            List<AppInfo> pending = new List<AppInfo>();
            HashSet<AppInfo> queued = new HashSet<AppInfo>();
            for (int i = 0; i < list.Items.Count; i++)
                if (queued.Add((AppInfo)list.Items[i])) pending.Add((AppInfo)list.Items[i]);
            foreach (AppInfo app in candidates)
                if (app != null && queued.Add(app)) pending.Add(app);

            ThreadPool.QueueUserWorkItem(delegate
            {
                foreach (AppInfo app in pending)
                {
                    lock (iconLock) { if (closed) return; }
                    Bitmap bmp;
                    try { bmp = Icons.Extract(app, size); }
                    catch (Exception) { bmp = null; }   // 取不到图标时这一行保留占位块，不影响勾选添加
                    if (bmp == null) continue;
                    lock (iconLock)
                    {
                        if (closed) { bmp.Dispose(); return; }
                        loaded.Add(new KeyValuePair<AppInfo, Bitmap>(app, bmp));
                    }
                    RequestIconDrain();
                }
            });
        }

        /// <summary>合并图标回传：同一时刻至多挂起一次界面线程回调。</summary>
        private void RequestIconDrain()
        {
            if (Interlocked.CompareExchange(ref drainPending, 1, 0) != 0) return;
            try { BeginInvoke((Action)DrainIcons); }
            catch (InvalidOperationException)
            {
                // 窗口已关闭（句柄销毁）：loaded 里的位图由 Dispose 释放
                Interlocked.Exchange(ref drainPending, 0);
            }
        }

        private void DrainIcons()
        {
            Interlocked.Exchange(ref drainPending, 0);
            List<KeyValuePair<AppInfo, Bitmap>> batch;
            lock (iconLock)
            {
                if (closed) return;
                batch = new List<KeyValuePair<AppInfo, Bitmap>>(loaded);
                loaded.Clear();
            }
            foreach (KeyValuePair<AppInfo, Bitmap> pair in batch)
            {
                Bitmap old;
                if (icons.TryGetValue(pair.Key, out old) && old != null) old.Dispose();
                icons[pair.Key] = pair.Value;
                list.InvalidateRow(list.Items.IndexOf(pair.Key));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (iconLock)
                {
                    closed = true;
                    foreach (KeyValuePair<AppInfo, Bitmap> pair in loaded) pair.Value.Dispose();
                    loaded.Clear();
                }
                foreach (Bitmap bmp in icons.Values)
                    if (bmp != null) bmp.Dispose();
                icons.Clear();
            }
            base.Dispose(disposing);
        }

        /// <summary>自绘行的列表：记录鼠标悬停行，并且不整块擦除背景（行由 DrawItem 整行重绘）。</summary>
        private sealed class PickerList : ListBox
        {
            private const int WM_ERASEBKGND = 0x0014;

            internal int HoverIndex = -1;
            internal bool KeyboardActive;   // 最近一次操作来自键盘：此时才高亮键盘选中的行

            internal PickerList()
            {
                DrawMode = DrawMode.OwnerDrawFixed;
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

            internal void InvalidateRow(int index)
            {
                if (index < 0 || index >= Items.Count || !IsHandleCreated) return;
                Invalidate(GetItemRectangle(index));
            }

            internal void ResetHover()
            {
                HoverIndex = -1;
                KeyboardActive = false;
            }

            private void SetHover(int index)
            {
                if (index == HoverIndex) return;
                int old = HoverIndex;
                HoverIndex = index;
                InvalidateRow(old);
                InvalidateRow(index);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                if (KeyboardActive)
                {
                    KeyboardActive = false;
                    InvalidateRow(SelectedIndex);
                }
                SetHover(HitRow(e.Location));
                base.OnMouseMove(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                SetHover(-1);
                base.OnMouseLeave(e);
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                SetHover(HitRow(PointToClient(MousePosition)));   // 滚动后鼠标下换了一行
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
