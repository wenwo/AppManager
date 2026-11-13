using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>
    /// 设置页：作为独立页面覆盖在主内容区之上（标题行与窗口按钮保留），点击「返回」退出。
    /// 采用 iOS 分组卡片风格：左列「常规 / 扫描与收纳」，右列「存储位置 / 维护」。
    /// 每一行整行可点击：开关行切换开关；操作行执行动作，其中会打开窗口的行右侧画箭头，
    /// 立即执行的行右侧显示动作文字。
    /// </summary>
    public class SettingsPage : Control
    {
        /// <summary>「重新扫描」行的状态，由主窗体在扫描开始和结束时回传。</summary>
        public enum RescanState { Idle, Running, Done, Failed }

        private int RowH { get { return Dpi(60); } }         // 行高（标题 + 说明两行）
        private int GapY { get { return Dpi(20); } }         // 分组间距
        private int LabelH { get { return Dpi(18); } }       // 分组标题行高
        private int CardPadX { get { return Dpi(18); } }     // 卡片内左右留白
        private int CardRadius { get { return Dpi(10); } }

        private int Dpi(int value)
        {
            return DpiHelper.Scale(value, DpiHelper.ForControl(this));
        }

        /// <summary>一行内容：标题 + 说明，右侧为开关、箭头或动作文字。</summary>
        private sealed class Row
        {
            public Rectangle Bounds;
            public string Title = string.Empty;
            public string Subtitle;        // 标题下方的说明或当前值
            public ToggleSwitch Toggle;    // 开关行：整行点击即切换
            public Action Action;          // 操作行：整行点击即执行
            public bool Navigates;         // 操作会打开下一级窗口：右侧画箭头
            public string Trailing;        // 右侧文字：动作名或进行中的状态
            public bool Enabled = true;    // 为假时置灰且不响应点击
            public bool FirstInCard;
            public bool LastInCard;
        }

        /// <summary>一个分组：上方标题 + 下方白色圆角卡片。</summary>
        private sealed class Section
        {
            public string Title = string.Empty;
            public Rectangle LabelRect;
            public Rectangle CardRect;
        }

        private readonly RoundButton backBtn;
        private readonly ToggleSwitch showHiddenToggle;
        private readonly ToggleSwitch autoScanToggle;
        private readonly ToggleSwitch autoOrganizeDesktopToggle;
        private readonly ToggleSwitch closeToTrayToggle;

        private readonly List<Section> sections = new List<Section>();
        private readonly List<Row> rows = new List<Row>();
        private Rectangle headerRect;
        private string resourcePath = string.Empty;
        private RescanState rescanState = RescanState.Idle;
        private int hoverRow = -1;
        private int pressedRow = -1;
        /// <summary>绑定期间抑制开关事件，避免回填状态时触发「已改动」回调。</summary>
        private bool syncing;

        public event EventHandler BackRequested;
        public event EventHandler ShowHiddenChanged;
        public event EventHandler AutoScanChanged;
        public event EventHandler AutoOrganizeDesktopChanged;
        public event EventHandler CloseToTrayChanged;
        public event EventHandler StorageLocationRequested;
        public event EventHandler RescanRequested;
        public event EventHandler SelfTestRequested;
        public event EventHandler LayoutBackupRequested;
        public event EventHandler DatabaseRequested;

        public SettingsPage()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
                | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Theme.Background;

            backBtn = new RoundButton();
            backBtn.Text = "返回";
            backBtn.Icon = UiIconKind.Back;
            backBtn.Accent = false;
            backBtn.Click += delegate { Raise(BackRequested); };
            Controls.Add(backBtn);

            showHiddenToggle = MakeToggle(delegate { Raise(ShowHiddenChanged); });
            autoScanToggle = MakeToggle(delegate
            {
                if (!syncing) RefreshRows();   // 「自动收纳」依赖自动扫描，随之启用或置灰
                Raise(AutoScanChanged);
            });
            autoOrganizeDesktopToggle = MakeToggle(delegate { Raise(AutoOrganizeDesktopChanged); });
            closeToTrayToggle = MakeToggle(delegate { Raise(CloseToTrayChanged); });
        }

        /// <summary>子控件的位置和尺寸由 Relayout 按当前 DPI 统一计算，不再叠加框架缩放。</summary>
        protected override bool ScaleChildren { get { return false; } }

        // ===== 对外状态 =====

        public bool ShowHidden { get { return showHiddenToggle.Checked; } }
        public bool AutoScan { get { return autoScanToggle.Checked; } }
        public bool AutoOrganizeDesktop { get { return autoOrganizeDesktopToggle.Checked; } }
        public bool CloseToTray { get { return closeToTrayToggle.Checked; } }

        /// <summary>回填页面状态（开关与存储路径）。绑定过程中不触发变更事件。</summary>
        public void Bind(bool showHidden, bool autoScan, bool autoOrganizeDesktop, bool closeToTray, string resourceStorePath)
        {
            syncing = true;
            showHiddenToggle.Checked = showHidden;
            autoScanToggle.Checked = autoScan;
            autoOrganizeDesktopToggle.Checked = autoOrganizeDesktop;
            closeToTrayToggle.Checked = closeToTray;
            syncing = false;
            resourcePath = resourceStorePath ?? string.Empty;
            RefreshRows();   // 路径文本与开关依赖关系都参与行内容，重新生成行
        }

        /// <summary>更新「重新扫描」行：进行中置灰防止重复触发，结束后在说明里给出结果。</summary>
        public void SetRescanState(RescanState state)
        {
            if (rescanState == state) return;
            rescanState = state;
            RefreshRows();
        }

        private ToggleSwitch MakeToggle(EventHandler handler)
        {
            ToggleSwitch t = new ToggleSwitch();
            t.CheckedChanged += handler;
            // 指针进出开关时本页收不到移动消息，这里补一次悬停同步，让所在行保持高亮
            t.MouseEnter += delegate { SyncHover(); };
            t.MouseLeave += delegate { SyncHover(); };
            Controls.Add(t);
            return t;
        }

        private void Raise(EventHandler handler)
        {
            if (syncing) return;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        // ===== 布局 =====

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        /// <summary>行内容变化后重建行并重绘；指针仍停在页面内时恢复其所在行的高亮。</summary>
        private void RefreshRows()
        {
            Relayout();
            if (IsHandleCreated && Visible)
            {
                Point p = PointToClient(Cursor.Position);
                if (ClientRectangle.Contains(p)) hoverRow = RowAt(p);
            }
            Invalidate(true);
        }

        /// <summary>按可用宽高两列排布分组卡片；行矩形与开关位置一并计算。</summary>
        private void Relayout()
        {
            sections.Clear();
            rows.Clear();
            hoverRow = -1;
            pressedRow = -1;
            if (Width <= 0 || Height <= 0) return;

            int contentW = Math.Min(Dpi(760), Math.Max(Dpi(400), Width - Dpi(96)));
            int left = (Width - contentW) / 2;
            int top = Dpi(22);
            int headerH = Dpi(30);
            int backW = Dpi(74);
            // 标题靠左、返回按钮靠右，两端分别与卡片区左右边缘对齐
            headerRect = new Rectangle(left, top, contentW - backW - Dpi(14), headerH);
            backBtn.Bounds = new Rectangle(left + contentW - backW, top, backW, headerH);

            int colGap = Dpi(24);
            int colW = (contentW - colGap) / 2;
            int rightX = left + colW + colGap;
            int firstY = top + headerH + LabelH;

            // 左列：常规开关 + 扫描与收纳
            int y = AddSection("常规", left, colW, firstY, new Row[]
            {
                MakeToggleRow("显示隐藏项", "在列表中显示已设为隐藏的条目。", showHiddenToggle),
                MakeToggleRow("关闭时隐藏到托盘", "开启后关闭窗口隐藏到托盘；关闭则直接退出。", closeToTrayToggle)
            });
            bool autoScan = autoScanToggle.Checked;
            Row organize = MakeToggleRow("自动收纳桌面新快捷方式",
                autoScan ? "关闭时只更新列表，不移动桌面文件。" : "需要先开启上面的自动扫描。",
                autoOrganizeDesktopToggle);
            organize.Enabled = autoScan;   // 收纳由自动扫描的监听触发，扫描关闭时这个开关不起作用
            AddSection("扫描与收纳", left, colW, y + GapY, new Row[]
            {
                MakeToggleRow("自动扫描应用与桌面变化", "新装软件或桌面有变化时自动更新列表。", autoScanToggle),
                organize,
                MakeRescanRow()
            });

            // 右列：存储位置 + 维护操作
            int y2 = AddSection("存储位置", rightX, colW, firstY, new Row[]
            {
                MakeNavRow("资源存储位置", resourcePath, delegate { Raise(StorageLocationRequested); })
            });
            AddSection("维护", rightX, colW, y2 + GapY, new Row[]
            {
                MakeNavRow("布局备份与还原", "备份当前布局，或还原到之前的备份。", delegate { Raise(LayoutBackupRequested); }),
                MakeNavRow("数据维护与操作记录", "查看操作记录、撤销，完整备份与恢复数据库。", delegate { Raise(DatabaseRequested); }),
                MakeActionRow("运行自测", "检查核心功能，结果写入 selftest.log。", "运行",
                    delegate { Raise(SelfTestRequested); })
            });
        }

        /// <summary>追加一个分组，返回卡片底部 Y 坐标。</summary>
        private int AddSection(string title, int x, int width, int y, Row[] list)
        {
            Section s = new Section();
            s.Title = title;
            s.LabelRect = new Rectangle(x, y, width, LabelH);
            int cardTop = y + LabelH + Dpi(6);
            s.CardRect = new Rectangle(x, cardTop, width, list.Length * RowH);
            sections.Add(s);

            Size toggleSize = new Size(Dpi(46), Dpi(28));
            int rowTop = cardTop;
            for (int i = 0; i < list.Length; i++)
            {
                Row r = list[i];
                r.Bounds = new Rectangle(x, rowTop, width, RowH);
                r.FirstInCard = (i == 0);
                r.LastInCard = (i == list.Length - 1);
                if (r.Toggle != null)
                {
                    r.Toggle.Bounds = new Rectangle(r.Bounds.Right - CardPadX - toggleSize.Width,
                        r.Bounds.Top + (RowH - toggleSize.Height) / 2,
                        toggleSize.Width, toggleSize.Height);
                    r.Toggle.Enabled = r.Enabled;
                }
                rows.Add(r);
                rowTop += RowH;
            }
            return s.CardRect.Bottom;
        }

        private Row MakeToggleRow(string title, string subtitle, ToggleSwitch toggle)
        {
            Row r = new Row();
            r.Title = title;
            r.Subtitle = subtitle;
            r.Toggle = toggle;
            return r;
        }

        /// <summary>打开下一级窗口的操作行（右侧箭头）。</summary>
        private Row MakeNavRow(string title, string subtitle, Action action)
        {
            Row r = new Row();
            r.Title = title;
            r.Subtitle = subtitle;
            r.Action = action;
            r.Navigates = true;
            return r;
        }

        /// <summary>立即执行的操作行（右侧显示动作文字）。</summary>
        private Row MakeActionRow(string title, string subtitle, string trailing, Action action)
        {
            Row r = new Row();
            r.Title = title;
            r.Subtitle = subtitle;
            r.Trailing = trailing;
            r.Action = action;
            return r;
        }

        private Row MakeRescanRow()
        {
            Row r = MakeActionRow("重新扫描", "立即重新读取应用、桌面和已收录内容。", "扫描",
                delegate { Raise(RescanRequested); });
            if (rescanState == RescanState.Running)
            {
                r.Trailing = "正在扫描…";
                r.Enabled = false;   // 进行中不重复触发
            }
            else if (rescanState == RescanState.Done) r.Subtitle = "扫描完成，列表已更新。";
            else if (rescanState == RescanState.Failed) r.Subtitle = "扫描失败，已保留当前列表。";
            return r;
        }

        // ===== 绘制 =====

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush bg = new SolidBrush(Theme.Background))
                g.FillRectangle(bg, ClientRectangle);
            if (headerRect.Width <= 0) return;

            // 页面标题
            using (SolidBrush tb = new SolidBrush(Theme.TextPrimary))
                Draw.DrawStringLeft(g, "设置", Theme.PageTitleFont, tb, headerRect);

            // 分组标题与卡片
            using (SolidBrush lb = new SolidBrush(Theme.TextSecondary))
            {
                for (int i = 0; i < sections.Count; i++)
                {
                    Section s = sections[i];
                    Draw.DrawStringLeft(g, s.Title, Theme.SectionTitleFont, lb, s.LabelRect);
                    Draw.DrawShadow(g, s.CardRect, CardRadius, 0.18f);
                    using (SolidBrush cb = new SolidBrush(Theme.Card))
                        Draw.FillRoundedRect(g, cb, s.CardRect, CardRadius);
                }
            }

            PaintRows(g);
        }

        private void PaintRows(Graphics g)
        {
            Font titleFont = Theme.BodyFont;
            Font subFont = Theme.CaptionFont;
            int titleH = Dpi(22);
            int subH = Dpi(18);
            using (SolidBrush textBrush = new SolidBrush(Theme.TextPrimary))
            using (SolidBrush subBrush = new SolidBrush(Theme.TextSecondary))
            using (SolidBrush dimBrush = new SolidBrush(Theme.TextTertiary))
            using (SolidBrush accentBrush = new SolidBrush(Theme.AccentText))
            using (Pen sep = new Pen(Theme.Separator, 1))
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    Row r = rows[i];

                    // 悬停/按下高亮：按下后指针移出该行则不再高亮（松开即取消）
                    bool pressed = (i == pressedRow && i == hoverRow);
                    if (pressed || (i == hoverRow && pressedRow < 0))
                    {
                        using (SolidBrush hb = new SolidBrush(pressed ? Theme.RowHover : Theme.CardHover))
                        using (GraphicsPath hp = RowPath(r))
                            g.FillPath(hb, hp);
                    }
                    // 行分隔线（卡片内缩进）
                    if (!r.LastInCard)
                        g.DrawLine(sep, r.Bounds.Left + CardPadX, r.Bounds.Bottom - 1,
                            r.Bounds.Right - CardPadX, r.Bounds.Bottom - 1);

                    // 右侧：开关、箭头或动作文字，各自占掉一段文本宽度
                    int textX = r.Bounds.Left + CardPadX;
                    int textRight = r.Bounds.Right - CardPadX;
                    if (r.Toggle != null) textRight = r.Toggle.Left - Dpi(12);
                    else if (r.Navigates)
                    {
                        DrawRowChevron(g, r.Bounds);
                        textRight -= Dpi(20);
                    }
                    else if (!string.IsNullOrEmpty(r.Trailing))
                    {
                        int trailingW = (int)Math.Ceiling(g.MeasureString(r.Trailing, titleFont).Width);
                        Draw.DrawStringRight(g, r.Trailing, titleFont, r.Enabled ? accentBrush : subBrush,
                            new Rectangle(textRight - trailingW, r.Bounds.Top, trailingW, r.Bounds.Height));
                        textRight -= trailingW + Dpi(12);
                    }
                    int textW = Math.Max(0, textRight - textX);

                    // 标题与说明作为一个整体在行内垂直居中；没有说明时标题单独居中
                    Brush titleBrush = r.Enabled ? textBrush : dimBrush;
                    if (string.IsNullOrEmpty(r.Subtitle))
                    {
                        Draw.DrawStringLeft(g, r.Title, titleFont, titleBrush,
                            new Rectangle(textX, r.Bounds.Top, textW, r.Bounds.Height));
                    }
                    else
                    {
                        int blockTop = r.Bounds.Top + (r.Bounds.Height - titleH - subH) / 2;
                        Draw.DrawStringLeft(g, r.Title, titleFont, titleBrush,
                            new Rectangle(textX, blockTop, textW, titleH));
                        Draw.DrawStringLeft(g, r.Subtitle, subFont, subBrush,
                            new Rectangle(textX, blockTop + titleH, textW, subH));
                    }
                }
            }
        }

        /// <summary>行的高亮形状：卡片首行圆上角、末行圆下角、中间行直角，高亮不溢出卡片圆角。</summary>
        private GraphicsPath RowPath(Row r)
        {
            GraphicsPath p = new GraphicsPath();
            Rectangle b = r.Bounds;
            int d = CardRadius * 2;
            if (r.FirstInCard)
            {
                p.AddArc(b.X, b.Y, d, d, 180, 90);
                p.AddArc(b.Right - d, b.Y, d, d, 270, 90);
            }
            else p.AddLine(b.X, b.Y, b.Right, b.Y);
            if (r.LastInCard)
            {
                p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90);
                p.AddArc(b.X, b.Bottom - d, d, d, 90, 90);
            }
            else p.AddLine(b.Right, b.Bottom, b.X, b.Bottom);
            p.CloseFigure();
            return p;
        }

        /// <summary>操作行右侧的「›」箭头（线条绘制，与上下文菜单保持一致）；箭头尖与开关右缘对齐。</summary>
        private void DrawRowChevron(Graphics g, Rectangle r)
        {
            int arm = Dpi(4);
            int cx = r.Right - CardPadX - Dpi(2);
            int cy = r.Top + r.Height / 2;
            using (Pen p = new Pen(Theme.TextTertiary, Dpi(16) / 10F))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, new Point[]
                {
                    new Point(cx - arm, cy - arm),
                    new Point(cx, cy),
                    new Point(cx - arm, cy + arm)
                });
            }
        }

        // ===== 交互：整行点击 =====

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            SetHover(RowAt(e.Location));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            SyncHover();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            SetHover(RowAt(e.Location));
            SetPressed(hoverRow);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            int idx = pressedRow;
            SetPressed(-1);
            if (idx < 0 || idx != RowAt(e.Location)) return;   // 按下后移出该行再松开：视为取消
            Row r = rows[idx];
            if (r.Toggle != null) r.Toggle.Checked = !r.Toggle.Checked;
            else if (r.Action != null) r.Action();
        }

        /// <summary>
        /// 指针离开本页或进出开关时同步悬停行：停在某行的开关上时该行保持高亮，其余情况清除
        /// （回到页面空白处会紧接着收到移动消息，由 OnMouseMove 重新计算）。
        /// </summary>
        private void SyncHover()
        {
            if (!IsHandleCreated || !Visible)
            {
                SetHover(-1);
                return;
            }
            Point p = PointToClient(Cursor.Position);
            SetHover(GetChildAtPoint(p) is ToggleSwitch ? RowAt(p) : -1);
        }

        private void SetHover(int idx)
        {
            if (idx == hoverRow) return;
            int old = hoverRow;
            hoverRow = idx;
            InvalidateRow(old);
            InvalidateRow(idx);
        }

        private void SetPressed(int idx)
        {
            if (idx == pressedRow) return;
            int old = pressedRow;
            pressedRow = idx;
            InvalidateRow(old);
            InvalidateRow(idx);
        }

        /// <summary>重绘一行，连同行内开关（开关背景透明，高亮色要透过去）。</summary>
        private void InvalidateRow(int idx)
        {
            if (idx >= 0 && idx < rows.Count) Invalidate(rows[idx].Bounds, true);
        }

        /// <summary>命中可响应点击的行（未置灰）；未命中返回 -1。</summary>
        private int RowAt(Point p)
        {
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Enabled && rows[i].Bounds.Contains(p)) return i;
            return -1;
        }
    }
}
