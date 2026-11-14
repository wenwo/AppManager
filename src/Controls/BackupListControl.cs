using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>
    /// 布局备份窗口的卡片列表：每份备份一张卡片（图标、名称、时间、数量标签），操作按钮直接放在卡片右侧。
    /// 「我的备份」与「恢复前自动留底」分组显示；整体自绘、按像素滚动，行内按钮由 Zone 统一命中。
    /// 控件只负责展示与发起请求，确认、执行和失败提示由窗体处理。
    /// </summary>
    internal sealed class BackupListControl : Control
    {
        /// <summary>卡片右侧的行内操作。</summary>
        internal enum CardAction { Restore, More, Delete }

        // 尺寸均为 96 DPI 下的逻辑像素，使用处统一经 Dpi() 缩放
        private const int SidePad = 8;         // 卡片左右留白（容纳阴影与滚动条）
        private const int SectionH = 28;       // 分组标题高度
        private const int SectionGap = 14;     // 两个分组之间的额外间距
        private const int CardH = 90;
        private const int CardGap = 10;
        private const int CardRadius = 10;
        private const int IconTile = 36;
        private const int ButtonH = 30;
        private const int RestoreW = 64;
        private const int MoreW = 34;
        private const int DeleteW = 56;
        private const int ScrollBarW = 4;
        private const int ScrollHitW = 12;

        // 图标字形（Segoe Fluent Icons / MDL2 Assets 共有的码位）；系统缺少图标字体时图标块留空
        private const string GlyphBackup = "";
        private const string GlyphUndo = "";

        private static readonly Color TintGray = Color.FromArgb(110, 116, 125);
        private static readonly Color WarningText = Color.FromArgb(176, 96, 0);
        private static readonly Color DangerText = Color.FromArgb(207, 34, 46);
        private static readonly Color SelectedFill = Color.FromArgb(246, 250, 255);

        private sealed class Card
        {
            public LayoutBackupInfo Info;
            public Rectangle Bounds;     // 内容坐标
            public int TextRight;        // 文字可用的右边界
            public int GroupTop;         // 分组首张卡片：分组标题的顶部；其余等于卡片顶部
        }

        private sealed class Header
        {
            public string Title;
            public string Note;
            public int Count;
            public Rectangle Bounds;
        }

        private sealed class Zone
        {
            public int Card;
            public CardAction Kind;
            public Rectangle Bounds;
        }

        private readonly List<LayoutBackupInfo> items = new List<LayoutBackupInfo>();
        private readonly List<Card> cards = new List<Card>();
        private readonly List<Header> headers = new List<Header>();
        private readonly List<Zone> zones = new List<Zone>();

        private bool layoutDirty = true;
        private int layoutWidth = -1;
        private int layoutDpi = -1;
        private int contentHeight;
        private int scrollOffset;
        private int wheelRemainder;         // 不足一像素的滚轮累计量（单位：delta × 像素）
        private bool scrollDragging;
        private int scrollGrabDy;
        private bool scrollbarHot;
        private int hoverCard = -1;
        private int hoverZone = -1;
        private int pressedZone = -1;
        private int selected = -1;
        private bool loading = true;
        private bool busy;
        private string busyId;
        private string busyText;
        private bool enterRepeat;           // 最近一次回车按下是按住不放产生的重复
        private bool enterArmed;            // 回车在本控件上按下过：松开时才发起恢复
        private readonly ToolTip nameTip = new ToolTip();
        private string nameTipText = string.Empty;

        public event Action<LayoutBackupInfo> RestoreRequested;
        public event Action<LayoutBackupInfo> RenameRequested;
        public event Action<LayoutBackupInfo> DeleteRequested;
        /// <summary>请求弹出操作菜单：参数为备份、屏幕坐标的锚点、是否来自卡片上的「更多」按钮（否则为右键或菜单键）。</summary>
        public event Action<LayoutBackupInfo, Rectangle, bool> MenuRequested;

        public BackupListControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            BackColor = Theme.Background;
            TabStop = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) nameTip.Dispose();
            base.Dispose(disposing);
        }

        private static Font TitleFont { get { return Theme.FontOf(10F, FontStyle.Bold); } }

        // ===== 数据与状态 =====

        /// <summary>当前选中的备份；未选中时为 null。</summary>
        public LayoutBackupInfo Selected
        {
            get { return (selected >= 0 && selected < items.Count) ? items[selected] : null; }
        }

        /// <summary>
        /// 换入新的备份列表：自己建的备份在前、恢复前留底在后，组内保持传入顺序。
        /// selectId 非空时选中并滚动到该备份；否则沿用原来选中的那一份（已不存在则取消选中）。
        /// </summary>
        public void SetItems(List<LayoutBackupInfo> source, string selectId)
        {
            string keep = selectId;
            if (string.IsNullOrEmpty(keep) && Selected != null) keep = Selected.Id;

            items.Clear();
            if (source != null)
            {
                foreach (LayoutBackupInfo info in source) if (info != null && !info.IsPreRestore) items.Add(info);
                foreach (LayoutBackupInfo info in source) if (info != null && info.IsPreRestore) items.Add(info);
            }
            selected = string.IsNullOrEmpty(keep) ? -1 : items.FindIndex(delegate(LayoutBackupInfo info)
            {
                return string.Equals(info.Id, keep, StringComparison.OrdinalIgnoreCase);
            });
            loading = false;
            pressedZone = -1;
            enterArmed = false;
            layoutDirty = true;
            EnsureLayout();
            if (selected >= 0 && !string.IsNullOrEmpty(selectId)) EnsureVisible(selected);
            UpdateHover();
            Invalidate();
        }

        /// <summary>进入忙碌状态：所有行内操作停用；id 对应的卡片在按钮位置显示 text（id 为空则只停用）。</summary>
        public void SetBusy(string id, string text)
        {
            busy = true;
            busyId = id;
            busyText = text;
            pressedZone = -1;
            hoverZone = -1;
            enterArmed = false;
            Invalidate();
        }

        public void ClearBusy()
        {
            busy = false;
            busyId = null;
            busyText = null;
            UpdateHover();
            Invalidate();
        }

        // ===== 显示文字（纯函数，便于测试）=====

        /// <summary>卡片标题：恢复前留底用留底时间命名，不直接显示目录名里的随机串。</summary>
        internal static string DisplayName(LayoutBackupInfo info)
        {
            if (!info.IsPreRestore) return info.Name;
            const string prefix = "_prerestore_";
            const string stamp = "yyyyMMdd-HHmmss";
            DateTime at;
            if (info.Id.StartsWith(prefix, StringComparison.Ordinal) && info.Id.Length >= prefix.Length + stamp.Length
                && DateTime.TryParseExact(info.Id.Substring(prefix.Length, stamp.Length), stamp,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out at))
                return "恢复前留底 " + at.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            return info.Name;
        }

        /// <summary>相对时间；时间在未来（系统时钟被调过）时返回空串，由调用方只显示具体时间。</summary>
        internal static string RelativeTime(DateTime then, DateTime now)
        {
            TimeSpan span = now - then;
            if (span < TimeSpan.FromMinutes(-1)) return string.Empty;
            if (span < TimeSpan.FromMinutes(1)) return "刚刚";
            if (span < TimeSpan.FromHours(1)) return (int)span.TotalMinutes + " 分钟前";
            int days = (now.Date - then.Date).Days;
            if (days <= 0) return (int)span.TotalHours + " 小时前";
            if (days == 1) return "昨天";
            if (days < 30) return days + " 天前";
            if (days < 365) return (days / 30) + " 个月前";
            return (days / 365) + " 年前";
        }

        /// <summary>卡片第二行：「16 天前 · 2026-10-29 14:18」；时间无法解析时原样显示。</summary>
        internal static string TimeText(LayoutBackupInfo info, DateTime now)
        {
            DateTime at;
            if (!DateTime.TryParseExact(info.Created, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out at))
                return string.IsNullOrEmpty(info.Created) ? "时间未知" : info.Created;
            string absolute = at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            string relative = RelativeTime(at, now);
            return relative.Length == 0 ? absolute : relative + " · " + absolute;
        }

        /// <summary>卡片第三行的数量标签。恢复前留底没有清单，只显示收纳数量和大小。</summary>
        internal static List<string> Facts(LayoutBackupInfo info)
        {
            List<string> facts = new List<string>();
            facts.Add("收纳 " + info.StashCount);
            if (!info.IsPreRestore)
            {
                facts.Add("自定义 " + info.CustomCount);
                facts.Add("分类 " + info.CategoryCount);
            }
            facts.Add(info.SizeText);
            return facts;
        }

        // ===== 布局 =====

        private int Dpi(int value)
        {
            return DpiHelper.Scale(value, DpiHelper.ForControl(this));
        }

        /// <summary>按当前宽度和 DPI 重排卡片、分组标题和行内按钮；数据、宽度、DPI 都没变时直接返回。</summary>
        private void EnsureLayout()
        {
            int dpi = DpiHelper.ForControl(this);
            if (!layoutDirty && layoutWidth == Width && layoutDpi == dpi) return;
            layoutDirty = false;
            layoutWidth = Width;
            layoutDpi = dpi;
            cards.Clear();
            headers.Clear();
            zones.Clear();

            int left = Dpi(SidePad);
            int width = Math.Max(0, Width - Dpi(SidePad) * 2);
            int y = 0;
            Header current = null;
            for (int i = 0; i < items.Count; i++)
            {
                LayoutBackupInfo info = items[i];
                int groupTop = y;
                if (current == null || (i > 0 && items[i - 1].IsPreRestore != info.IsPreRestore))
                {
                    if (current != null) { y += Dpi(SectionGap); groupTop = y; }
                    current = new Header();
                    current.Title = info.IsPreRestore ? "恢复前自动留底" : "我的备份";
                    current.Note = info.IsPreRestore ? "每次恢复前自动保存当时的布局，最多 3 份，存满需先删除旧的" : null;
                    current.Bounds = new Rectangle(left + Dpi(2), y, Math.Max(0, width - Dpi(4)), Dpi(SectionH));
                    headers.Add(current);
                    y += Dpi(SectionH);
                }
                current.Count++;

                Card card = new Card();
                card.Info = info;
                card.Bounds = new Rectangle(left, y, width, Dpi(CardH));
                card.GroupTop = groupTop;

                // 行内按钮从右向左排：自己建的备份是「恢复 + 更多」，留底是「恢复 + 删除」，不完整的留底只能删除
                int buttonTop = card.Bounds.Top + (card.Bounds.Height - Dpi(ButtonH)) / 2;
                int x = card.Bounds.Right - Dpi(16);
                x = AddZone(i, info.IsPreRestore ? CardAction.Delete : CardAction.More, x,
                    Dpi(info.IsPreRestore ? DeleteW : MoreW), buttonTop);
                if (!info.Incomplete) x = AddZone(i, CardAction.Restore, x - Dpi(8), Dpi(RestoreW), buttonTop);
                card.TextRight = x - Dpi(16);
                cards.Add(card);
                y += Dpi(CardH) + Dpi(CardGap);
            }
            contentHeight = items.Count == 0 ? 0 : y - Dpi(CardGap) + Dpi(8);
            if (scrollOffset > MaxScroll) scrollOffset = MaxScroll;
        }

        private int AddZone(int card, CardAction kind, int right, int width, int top)
        {
            Zone zone = new Zone();
            zone.Card = card;
            zone.Kind = kind;
            zone.Bounds = new Rectangle(right - width, top, width, Dpi(ButtonH));
            zones.Add(zone);
            return zone.Bounds.Left;
        }

        private int MaxScroll { get { return Math.Max(0, contentHeight - Height); } }

        private Rectangle ScrollTrackRect()
        {
            int w = Dpi(ScrollBarW);
            return new Rectangle(Width - Dpi(2) - w, Dpi(4), w, Math.Max(0, Height - Dpi(8)));
        }

        private Rectangle ScrollHitRect()
        {
            int w = Dpi(ScrollHitW);
            return new Rectangle(Width - w, 0, w, Height);
        }

        /// <summary>滚动条滑块矩形；无需滚动时为空。</summary>
        private Rectangle ScrollThumbRect()
        {
            if (MaxScroll <= 0) return Rectangle.Empty;
            Rectangle t = ScrollTrackRect();
            int thumbH = (int)((long)t.Height * Height / contentHeight);
            if (thumbH < Dpi(30)) thumbH = Dpi(30);
            if (thumbH > t.Height) thumbH = t.Height;
            int travel = t.Height - thumbH;
            int y = t.Y + (travel <= 0 ? 0 : (int)((long)travel * scrollOffset / MaxScroll));
            return new Rectangle(t.X, y, t.Width, thumbH);
        }

        private void SetScroll(int value)
        {
            EnsureLayout();
            int max = MaxScroll;
            if (value < 0) value = 0;
            if (value > max) value = max;
            if (value == scrollOffset) return;
            scrollOffset = value;
            UpdateHover();   // 滚动不触发 MouseMove，需按当前光标重算悬停
            Invalidate();
        }

        /// <summary>鼠标滚轮滚动列表。delta 为 WM_MOUSEWHEEL 增量。</summary>
        public void ScrollByWheel(int delta)
        {
            EnsureLayout();
            if (delta == 0 || MaxScroll <= 0) return;
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines <= 0) lines = 3;
            // 按像素换算并累计余数：精确式触控板每次 delta 小于 120，整除会得到 0 而完全滚不动
            wheelRemainder += delta * Dpi(32) * lines;
            int pixels = wheelRemainder / 120;
            wheelRemainder -= pixels * 120;
            if (pixels != 0) SetScroll(scrollOffset - pixels);
        }

        /// <summary>滚动到让指定卡片完整可见；分组的第一张卡片连同分组标题一起露出。</summary>
        private void EnsureVisible(int index)
        {
            EnsureLayout();
            if (index < 0 || index >= cards.Count) return;
            Card card = cards[index];
            if (card.GroupTop < scrollOffset) SetScroll(card.GroupTop);
            else if (card.Bounds.Bottom + Dpi(8) > scrollOffset + Height) SetScroll(card.Bounds.Bottom + Dpi(8) - Height);
        }

        private void SetSelected(int index, bool reveal)
        {
            if (index < 0 || index >= items.Count) return;
            if (reveal) EnsureVisible(index);
            if (index == selected) return;
            selected = index;
            Invalidate();
        }

        // ===== 测试与窗体定位用的几何查询（客户区坐标）=====

        internal int CardCount { get { EnsureLayout(); return cards.Count; } }

        internal Rectangle CardRect(int index)
        {
            EnsureLayout();
            if (index < 0 || index >= cards.Count) return Rectangle.Empty;
            Rectangle r = cards[index].Bounds;
            r.Offset(0, -scrollOffset);
            return r;
        }

        /// <summary>卡片上某个行内按钮的位置；该卡片没有这个按钮时为空矩形。</summary>
        internal Rectangle ActionRect(int index, CardAction action)
        {
            EnsureLayout();
            foreach (Zone zone in zones)
            {
                if (zone.Card != index || zone.Kind != action) continue;
                Rectangle r = zone.Bounds;
                r.Offset(0, -scrollOffset);
                return r;
            }
            return Rectangle.Empty;
        }

        // ===== 绘制 =====

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush bg = new SolidBrush(Theme.Background))
                g.FillRectangle(bg, ClientRectangle);
            EnsureLayout();

            if (items.Count == 0)
            {
                if (loading)
                {
                    using (SolidBrush tb = new SolidBrush(Theme.TextTertiary))
                        Draw.DrawStringCenter(g, "正在读取备份…", Theme.BodyFont, tb, ClientRectangle);
                }
                else PaintEmpty(g);
                return;
            }

            GraphicsState state = g.Save();
            try
            {
                g.TranslateTransform(0, -scrollOffset);
                PaintHeaders(g);
                DateTime now = DateTime.Now;
                int top = scrollOffset - Dpi(8), bottom = scrollOffset + Height + Dpi(8);
                for (int i = 0; i < cards.Count; i++)
                {
                    if (cards[i].Bounds.Bottom < top || cards[i].Bounds.Top > bottom) continue;   // 只画视口内的卡片
                    PaintCard(g, i, now);
                }
            }
            finally { g.Restore(state); }

            // 内容滚上去后在顶部画一条分隔线，卡片不会像被标题区生硬切断
            if (scrollOffset > 0)
            {
                using (Pen line = new Pen(Theme.Separator, 1))
                    g.DrawLine(line, Dpi(SidePad), 0, Width - Dpi(SidePad) - 1, 0);
            }

            // 滚动条（仅内容超出视口时显示，细条风格，与设置页一致）
            if (MaxScroll > 0)
            {
                int alpha = (scrollbarHot || scrollDragging) ? 150 : 80;
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(alpha, 60, 60, 67)))
                    Draw.FillRoundedRect(g, tb, ScrollThumbRect(), Dpi(ScrollBarW) / 2);
            }
        }

        private void PaintEmpty(Graphics g)
        {
            string[] lines =
            {
                "新建一份备份，把现在的分类、排列和桌面收纳存下来；",
                "以后弄乱了，可以整体恢复到这一刻。",
                "点右上角的「新建备份」开始。"
            };
            int tile = Dpi(56);
            int blockH = tile + Dpi(16) + Dpi(26) + Dpi(8) + lines.Length * Dpi(20);
            int y = Math.Max(Dpi(8), (Height - blockH) / 2 - Dpi(10));
            Rectangle tileRect = new Rectangle((Width - tile) / 2, y, tile, tile);
            using (SolidBrush tb = new SolidBrush(Mix(Theme.Accent, Theme.Background, 0.87f)))
                Draw.FillRoundedRect(g, tb, tileRect, Dpi(14));
            Draw.DrawGlyph(g, GlyphBackup, Rectangle.Inflate(tileRect, -Dpi(15), -Dpi(15)), Theme.Accent);
            y += tile + Dpi(16);
            using (SolidBrush title = new SolidBrush(Theme.TextPrimary))
                Draw.DrawStringCenter(g, "还没有备份", Theme.FontOf(11F, FontStyle.Bold), title, new Rectangle(0, y, Width, Dpi(26)));
            y += Dpi(26) + Dpi(8);
            using (SolidBrush text = new SolidBrush(Theme.TextSecondary))
            {
                foreach (string line in lines)
                {
                    Draw.DrawStringCenter(g, line, Theme.CaptionFont, text, new Rectangle(0, y, Width, Dpi(20)));
                    y += Dpi(20);
                }
            }
        }

        private void PaintHeaders(Graphics g)
        {
            using (SolidBrush titleBrush = new SolidBrush(Theme.TextSecondary))
            using (SolidBrush noteBrush = new SolidBrush(Theme.TextTertiary))
            {
                foreach (Header h in headers)
                {
                    Draw.DrawStringLeft(g, h.Title, Theme.SectionTitleFont, titleBrush, h.Bounds);
                    int titleW = (int)Math.Ceiling(g.MeasureString(h.Title, Theme.SectionTitleFont).Width);
                    string count = h.Count + " 份";
                    int countW = (int)Math.Ceiling(g.MeasureString(count, Theme.CaptionFont).Width) + Dpi(4);
                    Rectangle countRect = new Rectangle(h.Bounds.Left + titleW + Dpi(2), h.Bounds.Top, countW, h.Bounds.Height);
                    Draw.DrawStringLeft(g, count, Theme.CaptionFont, noteBrush, countRect);
                    if (string.IsNullOrEmpty(h.Note)) continue;
                    int noteLeft = countRect.Right + Dpi(12);
                    if (h.Bounds.Right - noteLeft > Dpi(60))
                        Draw.DrawStringRight(g, h.Note, Theme.CaptionFont, noteBrush,
                            new Rectangle(noteLeft, h.Bounds.Top, h.Bounds.Right - noteLeft, h.Bounds.Height));
                }
            }
        }

        private void PaintCard(Graphics g, int index, DateTime now)
        {
            Card card = cards[index];
            LayoutBackupInfo info = card.Info;
            Rectangle b = card.Bounds;
            bool isSelected = index == selected;
            bool hot = index == hoverCard && !busy;

            Draw.DrawShadow(g, b, Dpi(CardRadius), 0.18f);
            using (SolidBrush fill = new SolidBrush(isSelected ? SelectedFill : Theme.Card))
                Draw.FillRoundedRect(g, fill, b, Dpi(CardRadius));
            if (isSelected || hot)
            {
                Color border = isSelected ? Theme.Accent : Mix(Theme.Accent, Theme.ControlBorder, 0.55f);
                using (Pen pen = new Pen(border, isSelected ? Dpi(15) / 10F : 1F))
                    Draw.DrawRoundedRect(g, pen, new Rectangle(b.X, b.Y, b.Width - 1, b.Height - 1), Dpi(CardRadius));
            }

            // 图标块：自己建的备份用强调色，恢复前留底用灰色，不完整的留底用警示色
            Color tint = info.Incomplete ? WarningText : (info.IsPreRestore ? TintGray : Theme.Accent);
            Rectangle tile = new Rectangle(b.Left + Dpi(16), b.Top + (b.Height - Dpi(IconTile)) / 2, Dpi(IconTile), Dpi(IconTile));
            using (SolidBrush tb = new SolidBrush(Mix(tint, Theme.Card, 0.87f)))
                Draw.FillRoundedRect(g, tb, tile, Dpi(9));
            Draw.DrawGlyph(g, info.IsPreRestore ? GlyphUndo : GlyphBackup, Rectangle.Inflate(tile, -Dpi(9), -Dpi(9)), tint);

            int textX = tile.Right + Dpi(14);
            int textW = Math.Max(0, card.TextRight - textX);
            using (SolidBrush titleBrush = new SolidBrush(Theme.TextPrimary))
                Draw.DrawStringLeft(g, DisplayName(info), TitleFont, titleBrush,
                    new Rectangle(textX, b.Top + Dpi(12), textW, Dpi(22)));
            using (SolidBrush timeBrush = new SolidBrush(Theme.TextSecondary))
                Draw.DrawStringLeft(g, TimeText(info, now), Theme.CaptionFont, timeBrush,
                    new Rectangle(textX, b.Top + Dpi(34), textW, Dpi(18)));
            PaintFacts(g, info, textX, b.Top + Dpi(58), card.TextRight);

            if (busy && busyId != null && string.Equals(busyId, info.Id, StringComparison.OrdinalIgnoreCase))
            {
                // 正在处理的卡片：按钮位置换成进度文字
                using (SolidBrush busyBrush = new SolidBrush(Theme.AccentText))
                    Draw.DrawStringRight(g, busyText, Theme.ButtonFont, busyBrush,
                        new Rectangle(b.Right - Dpi(16) - Dpi(130), b.Top, Dpi(130), b.Height));
                return;
            }
            for (int i = 0; i < zones.Count; i++)
                if (zones[i].Card == index) PaintButton(g, i);
        }

        /// <summary>数量标签：浅灰圆角小块，从左向右排，放不下的不画；不完整的留底多一个警示标签。</summary>
        private void PaintFacts(Graphics g, LayoutBackupInfo info, int x, int y, int right)
        {
            Font font = Theme.FontOf(8.5F);
            int h = Dpi(20);
            List<string> facts = Facts(info);
            if (info.Incomplete) facts.Insert(0, "可能不完整，只能删除");
            for (int i = 0; i < facts.Count; i++)
            {
                bool warn = info.Incomplete && i == 0;
                int w = (int)Math.Ceiling(g.MeasureString(facts[i], font).Width) + Dpi(12);
                if (x + w > right) break;
                Rectangle chip = new Rectangle(x, y, w, h);
                using (SolidBrush bg = new SolidBrush(warn ? Mix(WarningText, Theme.Card, 0.88f) : Theme.ChipBg))
                    Draw.FillRoundedRect(g, bg, chip, h / 2);
                using (SolidBrush tb = new SolidBrush(warn ? WarningText : Theme.TextSecondary))
                    Draw.DrawStringCenter(g, facts[i], font, tb, chip);
                x += w + Dpi(6);
            }
        }

        private void PaintButton(Graphics g, int zoneIndex)
        {
            Zone zone = zones[zoneIndex];
            Rectangle r = zone.Bounds;
            bool enabled = !busy;
            bool hot = enabled && zoneIndex == hoverZone && (pressedZone < 0 || pressedZone == zoneIndex);
            bool pressed = hot && pressedZone == zoneIndex;
            Color fill, border, text;
            switch (zone.Kind)
            {
                case CardAction.Restore:
                    fill = pressed ? Mix(Theme.Accent, Theme.Card, 0.82f) : (hot ? Mix(Theme.Accent, Theme.Card, 0.91f) : Theme.Card);
                    border = Mix(Theme.Accent, Theme.Card, hot ? 0.3f : 0.55f);
                    text = Theme.AccentText;
                    break;
                case CardAction.Delete:
                    fill = pressed ? Mix(Theme.DeleteRed, Theme.Card, 0.84f) : (hot ? Mix(Theme.DeleteRed, Theme.Card, 0.92f) : Theme.Card);
                    border = hot ? Mix(Theme.DeleteRed, Theme.Card, 0.45f) : Theme.ControlBorder;
                    text = hot ? DangerText : Theme.TextPrimary;
                    break;
                default:
                    fill = pressed ? Theme.RowHover : (hot ? Theme.CardHover : Theme.Card);
                    border = hot ? Mix(Theme.Accent, Theme.ControlBorder, 0.5f) : Theme.ControlBorder;
                    text = Theme.IconColor;
                    break;
            }
            if (!enabled)
            {
                fill = Theme.Background;
                border = Theme.Separator;
                text = Theme.TextTertiary;
            }
            using (SolidBrush fb = new SolidBrush(fill)) Draw.FillRoundedRect(g, fb, r, Dpi(6));
            using (Pen pen = new Pen(border, 1F))
                Draw.DrawRoundedRect(g, pen, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), Dpi(6));
            using (SolidBrush tb = new SolidBrush(text))
            {
                if (zone.Kind != CardAction.More)
                {
                    Draw.DrawStringCenter(g, zone.Kind == CardAction.Restore ? "恢复" : "删除", Theme.ButtonFont, tb, r);
                    return;
                }
                // 「更多」：三个圆点，用图形绘制，不依赖图标字体
                float dot = Dpi(30) / 10F;
                float cx = r.X + r.Width / 2F, cy = r.Y + r.Height / 2F;
                for (int i = -1; i <= 1; i++)
                    g.FillEllipse(tb, cx + i * Dpi(6) - dot / 2F, cy - dot / 2F, dot, dot);
            }
        }

        /// <summary>两色线性混合：amount 为 b 所占比例（0~1）。</summary>
        private static Color Mix(Color a, Color b, float amount)
        {
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * amount),
                (int)Math.Round(a.G + (b.G - a.G) * amount),
                (int)Math.Round(a.B + (b.B - a.B) * amount));
        }

        // ===== 交互 =====

        /// <summary>命中未停用的行内按钮（客户区坐标）；未命中返回 -1。</summary>
        private int ZoneAt(Point client)
        {
            EnsureLayout();
            if (busy || !ClientRectangle.Contains(client)) return -1;
            Point p = new Point(client.X, client.Y + scrollOffset);
            for (int i = 0; i < zones.Count; i++)
                if (zones[i].Bounds.Contains(p)) return i;
            return -1;
        }

        private int CardAt(Point client)
        {
            EnsureLayout();
            if (!ClientRectangle.Contains(client)) return -1;
            Point p = new Point(client.X, client.Y + scrollOffset);
            for (int i = 0; i < cards.Count; i++)
                if (cards[i].Bounds.Contains(p)) return i;
            return -1;
        }

        /// <summary>按指针当前位置重算悬停：数据重排或滚动后没有鼠标移动消息，需要主动重算。</summary>
        private void UpdateHover()
        {
            if (!IsHandleCreated || !Visible)
            {
                hoverCard = -1;
                hoverZone = -1;
                return;
            }
            Point p = PointToClient(Cursor.Position);
            hoverCard = CardAt(p);
            hoverZone = ZoneAt(p);
        }

        /// <summary>
        /// 指针停在卡片文字区、且该卡片的名称在界面上放不下（已用省略号截断）时，返回完整名称作悬停提示；否则返回 null。
        /// </summary>
        internal string TruncatedNameAt(Point client)
        {
            int index = CardAt(client);
            if (index < 0 || ZoneAt(client) >= 0) return null;
            Card card = cards[index];
            string name = DisplayName(card.Info);
            int room = card.TextRight - (card.Bounds.Left + Dpi(16) + Dpi(IconTile) + Dpi(14));
            int width = TextRenderer.MeasureText(name, TitleFont, Size.Empty,
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
            return width > room ? name : null;
        }

        /// <summary>更新悬停提示：只在文本变化时设置，避免鼠标每移动一次就重置提示计时。</summary>
        private void SetNameTip(string text)
        {
            text = text ?? string.Empty;
            if (text == nameTipText) return;
            nameTipText = text;
            nameTip.SetToolTip(this, text);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            layoutDirty = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (scrollDragging)
            {
                Rectangle track = ScrollTrackRect();
                int travel = track.Height - ScrollThumbRect().Height;
                if (travel > 0) SetScroll((int)((long)(e.Y - scrollGrabDy - track.Y) * MaxScroll / travel));
                return;
            }
            bool hot = MaxScroll > 0 && ScrollHitRect().Contains(e.Location);
            int zone = hot ? -1 : ZoneAt(e.Location);
            int card = hot ? -1 : CardAt(e.Location);
            SetNameTip(hot ? null : TruncatedNameAt(e.Location));
            if (hot == scrollbarHot && zone == hoverZone && card == hoverCard) return;
            scrollbarHot = hot;
            hoverZone = zone;
            hoverCard = card;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            SetNameTip(null);
            if (hoverZone >= 0 || hoverCard >= 0 || scrollbarHot)
            {
                hoverZone = -1;
                hoverCard = -1;
                scrollbarHot = false;
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        /// <summary>鼠标捕获丢失（弹出对话框、切换窗口等）时收不到 MouseUp：在这里结束滚动条拖动和按下状态。</summary>
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (Capture || (!scrollDragging && pressedZone < 0)) return;
            scrollDragging = false;
            pressedZone = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Right)
            {
                SetSelected(CardAt(e.Location), false);
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            // 滚动条：命中滑块则抓取拖动；点在轨道空白处则滑块跳到点击处并进入拖动
            if (MaxScroll > 0 && ScrollHitRect().Contains(e.Location))
            {
                Rectangle track = ScrollTrackRect();
                Rectangle thumb = ScrollThumbRect();
                scrollDragging = true;
                if (e.Y >= thumb.Y && e.Y <= thumb.Bottom) scrollGrabDy = e.Y - thumb.Y;
                else
                {
                    scrollGrabDy = thumb.Height / 2;
                    int travel = track.Height - thumb.Height;
                    if (travel > 0) SetScroll((int)((long)(e.Y - scrollGrabDy - track.Y) * MaxScroll / travel));
                }
                Invalidate();
                return;
            }

            hoverZone = ZoneAt(e.Location);
            pressedZone = hoverZone;
            SetSelected(CardAt(e.Location), false);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right)
            {
                int card = CardAt(e.Location);
                if (card >= 0 && !busy) RaiseMenu(items[card], new Rectangle(PointToScreen(e.Location), Size.Empty), false);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (scrollDragging)
            {
                scrollDragging = false;
                Invalidate();
                return;
            }
            int zone = pressedZone;
            pressedZone = -1;
            Invalidate();
            if (zone < 0 || zone != ZoneAt(e.Location)) return;   // 按下后移出按钮再松开：视为取消
            Activate(zones[zone]);
        }

        private void Activate(Zone zone)
        {
            LayoutBackupInfo info = items[zone.Card];
            switch (zone.Kind)
            {
                case CardAction.Restore:
                    if (RestoreRequested != null) RestoreRequested(info);
                    break;
                case CardAction.Delete:
                    if (DeleteRequested != null) DeleteRequested(info);
                    break;
                default:
                    Rectangle r = zone.Bounds;
                    r.Offset(0, -scrollOffset);
                    RaiseMenu(info, RectangleToScreen(r), true);
                    break;
            }
        }

        private void RaiseMenu(LayoutBackupInfo info, Rectangle anchor, bool fromButton)
        {
            if (MenuRequested != null) MenuRequested(info, anchor, fromButton);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollByWheel(e.Delta);
            // 标记已处理：否则默认窗口过程会把滚轮转给窗体，窗体再转回来造成滚两次
            HandledMouseEventArgs handled = e as HandledMouseEventArgs;
            if (handled != null) handled.Handled = true;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.Home:
                case Keys.End:
                case Keys.PageUp:
                case Keys.PageDown:
                case Keys.Enter:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || e.Modifiers != Keys.None || items.Count == 0) return;
            LayoutBackupInfo info = Selected;
            switch (e.KeyCode)
            {
                case Keys.Down:
                    SetSelected(selected < 0 ? 0 : Math.Min(selected + 1, items.Count - 1), true);
                    break;
                case Keys.Up:
                    SetSelected(selected < 0 ? 0 : Math.Max(selected - 1, 0), true);
                    break;
                case Keys.Home:
                    SetSelected(0, true);
                    break;
                case Keys.End:
                    SetSelected(items.Count - 1, true);
                    break;
                case Keys.PageDown:
                    SetScroll(scrollOffset + Height);
                    break;
                case Keys.PageUp:
                    SetScroll(scrollOffset - Height);
                    break;
                case Keys.Enter:
                    // 恢复在松开回车时才发起（见 OnKeyUp），按住不放的重复按键不算：
                    // 否则重复的回车会落到随后弹出的确认框上，等于没看就确认了恢复
                    if (!enterRepeat) enterArmed = info != null && !busy && !info.Incomplete;
                    break;
                case Keys.F2:
                    if (info != null && !busy && !info.IsPreRestore && RenameRequested != null) RenameRequested(info);
                    break;
                case Keys.Delete:
                    if (info != null && !busy && DeleteRequested != null) DeleteRequested(info);
                    break;
                case Keys.Apps:
                    if (info != null && !busy)
                    {
                        EnsureVisible(selected);
                        Rectangle card = CardRect(selected);
                        RaiseMenu(info, new Rectangle(PointToScreen(new Point(card.Left + Dpi(66), card.Top + Dpi(40))), Size.Empty), false);
                    }
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        /// <summary>记下这次回车是不是按住不放产生的重复（消息 lParam 第 30 位：按键此前已处于按下状态）。</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Enter) enterRepeat = (msg.LParam.ToInt64() & 0x40000000L) != 0;
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode != Keys.Enter || !enterArmed) return;
            enterArmed = false;
            LayoutBackupInfo info = Selected;
            if (info != null && !busy && !info.Incomplete && RestoreRequested != null) RestoreRequested(info);
            e.Handled = true;
        }

        protected override void OnLostFocus(EventArgs e)
        {
            enterArmed = false;
            base.OnLostFocus(e);
        }
    }
}
