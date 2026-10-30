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
    /// 采用 iOS 分组卡片风格：左列「常规 / 关闭主窗口」，右列「存储位置 / 维护」；
    /// 开关项使用 ToggleSwitch，操作项为整行可点击行（悬停高亮 + 右侧箭头）。
    /// </summary>
    public class SettingsPage : Control
    {
        private int RowH { get { return Dpi(48); } }         // 普通开关/操作行高
        private int InfoRowH { get { return Dpi(56); } }     // 标题 + 路径两行行高
        private int ActionRowH { get { return Dpi(44); } }   // 纯操作行高
        private int GapY { get { return Dpi(20); } }         // 分组间距
        private int LabelH { get { return Dpi(18); } }       // 分组标题行高
        private int CardPadX { get { return Dpi(18); } }     // 卡片内左右留白
        private int CardRadius { get { return Dpi(10); } }

        private int Dpi(int value)
        {
            return DpiHelper.Scale(value, DpiHelper.ForControl(this));
        }

        /// <summary>一行内容：标题（可带副标题/右侧值）+ 可选开关或点击动作。</summary>
        private sealed class Row
        {
            public Rectangle Bounds;
            public string Title = string.Empty;
            public string Subtitle;
            public string Value;
            public ToggleSwitch Toggle;
            public Action Action;
            public bool LastInCard;
            public int Height;
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
        private string folderPath = string.Empty;
        private string filePath = string.Empty;
        private string resourcePath = string.Empty;
        private int hoverRow = -1;
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
            backBtn.Size = new Size(74, 30);
            backBtn.Click += delegate { Raise(BackRequested); };
            Controls.Add(backBtn);

            showHiddenToggle = MakeToggle(delegate { Raise(ShowHiddenChanged); });
            autoScanToggle = MakeToggle(delegate { Raise(AutoScanChanged); });
            autoOrganizeDesktopToggle = MakeToggle(delegate { Raise(AutoOrganizeDesktopChanged); });
            closeToTrayToggle = MakeToggle(delegate { Raise(CloseToTrayChanged); });
        }

        // ===== 对外状态 =====

        public bool ShowHidden { get { return showHiddenToggle.Checked; } }
        public bool AutoScan { get { return autoScanToggle.Checked; } }
        public bool AutoOrganizeDesktop { get { return autoOrganizeDesktopToggle.Checked; } }
        public bool CloseToTray { get { return closeToTrayToggle.Checked; } }

        /// <summary>回填页面状态（开关与存储路径）。绑定过程中不触发变更事件。</summary>
        public void Bind(bool showHidden, bool autoScan, bool autoOrganizeDesktop, bool closeToTray,
            string folderStorePath, string fileStorePath, string resourceStorePath)
        {
            syncing = true;
            showHiddenToggle.Checked = showHidden;
            autoScanToggle.Checked = autoScan;
            autoOrganizeDesktopToggle.Checked = autoOrganizeDesktop;
            closeToTrayToggle.Checked = closeToTray;
            syncing = false;
            folderPath = folderStorePath ?? string.Empty;
            filePath = fileStorePath ?? string.Empty;
            resourcePath = resourceStorePath ?? string.Empty;
            Relayout();   // 路径文本参与行布局，重新生成行以刷新展示内容
            Invalidate();
        }

        private ToggleSwitch MakeToggle(EventHandler handler)
        {
            ToggleSwitch t = new ToggleSwitch();
            t.Size = new Size(46, 28);
            if (handler != null) t.CheckedChanged += handler;
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

        /// <summary>按可用宽高两列排布分组卡片；行矩形与开关位置一并计算。</summary>
        private void Relayout()
        {
            sections.Clear();
            rows.Clear();
            hoverRow = -1;
            if (Width <= 0 || Height <= 0) return;

            int contentW = Math.Min(760, Math.Max(400, Width - 96));
            int left = (Width - contentW) / 2;
            int top = 22;
            // 标题靠左、返回按钮靠右，两端分别与卡片区左右边缘对齐
            headerRect = new Rectangle(left, top, contentW - 88, 30);
            backBtn.Bounds = new Rectangle(left + contentW - 74, top, 74, 30);

            int colGap = 24;
            int colW = (contentW - colGap) / 2;
            int rightX = left + colW + colGap;
            int y = top + 30 + LabelH;

            // 左列：常规开关 + 关闭主窗口行为
            y = AddSection("常规", left, colW, y, new Row[]
            {
                MakeToggleRow("显示隐藏项", showHiddenToggle),
                MakeToggleRow("自动扫描应用与桌面变化", autoScanToggle),
                MakeSubtitleRow("自动收纳桌面新快捷方式", "关闭时只更新列表，不移动桌面文件。", autoOrganizeDesktopToggle)
            });
            y += GapY;
            y = AddSection("关闭主窗口", left, colW, y, new Row[]
            {
                MakeSubtitleRow("最小化到托盘", "开启后关闭窗口隐藏到托盘；关闭则直接退出。", closeToTrayToggle)
            });

            // 右列：存储位置 + 维护操作
            int y2 = top + 30 + LabelH;
            y2 = AddSection("存储位置", rightX, colW, y2, new Row[]
            {
                MakeInfoRow("文件夹存储位置", folderPath),
                MakeInfoRow("文件存储位置", filePath),
                MakeInfoRow("资源存储位置", resourcePath),
                MakeActionRow("修改存储位置…", delegate { Raise(StorageLocationRequested); }, ActionRowH)
            });
            y2 += GapY;
            y2 = AddSection("维护", rightX, colW, y2, new Row[]
            {
                MakeActionRow("重新扫描", delegate { Raise(RescanRequested); }, ActionRowH),
                MakeActionRow("布局备份与还原…", delegate { Raise(LayoutBackupRequested); }, ActionRowH),
                MakeActionRow("数据维护与操作记录…", delegate { Raise(DatabaseRequested); }, ActionRowH),
                MakeActionRow("运行自测（--selftest 日志）", delegate { Raise(SelfTestRequested); }, ActionRowH)
            });
        }

        /// <summary>追加一个分组，返回卡片底部 Y 坐标。</summary>
        private int AddSection(string title, int x, int width, int y, Row[] list)
        {
            Section s = new Section();
            s.Title = title;
            s.LabelRect = new Rectangle(x, y, width, LabelH);
            int cardTop = y + LabelH + 6;
            int cardH = 0;
            for (int i = 0; i < list.Length; i++) cardH += list[i].Height;
            s.CardRect = new Rectangle(x, cardTop, width, cardH);
            sections.Add(s);

            int rowTop = cardTop;
            for (int i = 0; i < list.Length; i++)
            {
                Row r = list[i];
                r.Bounds = new Rectangle(x, rowTop, width, r.Height);
                r.LastInCard = (i == list.Length - 1);
                if (r.Toggle != null)
                {
                    r.Toggle.Bounds = new Rectangle(r.Bounds.Right - CardPadX - r.Toggle.Width,
                        r.Bounds.Top + (r.Height - r.Toggle.Height) / 2,
                        r.Toggle.Width, r.Toggle.Height);
                }
                rows.Add(r);
                rowTop += r.Height;
            }
            return s.CardRect.Bottom;
        }

        private Row MakeToggleRow(string title, ToggleSwitch toggle)
        {
            Row r = new Row();
            r.Title = title;
            r.Toggle = toggle;
            r.Height = RowH;
            return r;
        }

        private Row MakeSubtitleRow(string title, string subtitle, ToggleSwitch toggle)
        {
            Row r = MakeToggleRow(title, toggle);
            r.Subtitle = subtitle;
            r.Height = Dpi(64);
            return r;
        }

        private Row MakeInfoRow(string title, string value)
        {
            Row r = new Row();
            r.Title = title;
            r.Value = value ?? string.Empty;
            r.Height = InfoRowH;
            return r;
        }

        private Row MakeActionRow(string title, Action action, int height)
        {
            Row r = new Row();
            r.Title = title;
            r.Action = action;
            r.Height = height;
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
            using (SolidBrush textBrush = new SolidBrush(Theme.TextPrimary))
            using (SolidBrush subBrush = new SolidBrush(Theme.TextSecondary))
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    Row r = rows[i];

                    // 操作行悬停高亮
                    if (r.Action != null && i == hoverRow)
                    {
                        using (SolidBrush hb = new SolidBrush(Theme.RowHover))
                            Draw.FillRoundedRect(g, hb, r.Bounds, CardRadius);
                    }
                    // 行分隔线（卡片内缩进）
                    if (!r.LastInCard)
                    {
                        using (Pen sep = new Pen(Theme.Separator, 1))
                            g.DrawLine(sep, r.Bounds.Left + CardPadX, r.Bounds.Bottom - 1,
                                r.Bounds.Right - CardPadX, r.Bounds.Bottom - 1);
                    }

                    int textX = r.Bounds.Left + CardPadX;
                    int textRight = r.Bounds.Right - CardPadX;
                    if (r.Action != null) textRight -= 18;
                    if (r.Toggle != null) textRight = r.Toggle.Left - 12;
                    int textW = Math.Max(0, textRight - textX);

                    if (r.Value != null)
                    {
                        Draw.DrawStringLeft(g, r.Title, titleFont, textBrush,
                            new Rectangle(textX, r.Bounds.Top + 7, textW, 20));
                        Draw.DrawStringLeft(g, r.Value, subFont, subBrush,
                            new Rectangle(textX, r.Bounds.Top + 28, textW, 18));
                    }
                    else if (r.Subtitle != null)
                    {
                        Draw.DrawStringLeft(g, r.Title, titleFont, textBrush,
                            new Rectangle(textX, r.Bounds.Top + 8, textW, 20));
                        Draw.DrawStringLeft(g, r.Subtitle, subFont, subBrush,
                            new Rectangle(textX, r.Bounds.Top + 29, textW, 18));
                    }
                    else
                    {
                        Draw.DrawStringLeft(g, r.Title, titleFont, textBrush,
                            new Rectangle(textX, r.Bounds.Top, textW, r.Bounds.Height));
                    }

                    if (r.Action != null) DrawRowChevron(g, r.Bounds);
                }
            }
        }

        /// <summary>操作行右侧的「›」箭头（线条绘制，与上下文菜单保持一致）。</summary>
        private void DrawRowChevron(Graphics g, Rectangle r)
        {
            int cx = r.Right - CardPadX - 7;
            int cy = r.Top + r.Height / 2;
            using (Pen p = new Pen(Theme.TextSecondary, 1.6F))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, new Point[]
                {
                    new Point(cx - 4, cy - 4),
                    new Point(cx, cy),
                    new Point(cx - 4, cy + 4)
                });
            }
        }

        // ===== 交互：操作行点击 =====

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int idx = ActionRowAt(e.Location);
            if (idx != hoverRow)
            {
                hoverRow = idx;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hoverRow = -1;
            Cursor = Cursors.Default;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int idx = ActionRowAt(e.Location);
            if (idx < 0) return;
            Action action = rows[idx].Action;
            if (action != null) action();
        }

        /// <summary>命中可点击操作行；未命中返回 -1。</summary>
        private int ActionRowAt(Point p)
        {
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Action != null && rows[i].Bounds.Contains(p)) return i;
            return -1;
        }
    }
}
