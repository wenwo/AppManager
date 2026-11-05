using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>
    /// 资源分类的第二级内嵌侧边栏：集合列表（固定「全部」+ 自定义集合 + 底部「新建集合」）。
    /// 单击选中集合（由外部决定展示信息页或内容），双击直接进入；自定义集合可右键编辑/删除，
    /// 并作为网格条目拖出与外部文件拖入的投放目标。封面位图由外部注入，所有权转移给本控件。
    /// </summary>
    public class CollectionSidebar : ScrollableControl
    {
        /// <summary>集合条目数据（含封面位图，所有权归本控件）。</summary>
        public class Item
        {
            public string Key = string.Empty;       // 集合键；空串表示「全部」
            public string Name = string.Empty;
            public Bitmap Cover;                    // 可为 null：绘制占位图形
            public bool IsAll;                      // 固定「全部」项
            public int Depth;                       // 树形缩进层级；「全部」为 0
            public string Path = string.Empty;      // 完整可读路径，供辅助信息/菜单复用
            public string ParentKey = string.Empty; // 直接父集合键；根集合为空
            public bool HasChildren;
            public bool Expanded;
        }

        /// <summary>侧边栏固定宽度（外部布局按此预留）。</summary>
        public const int PreferredWidth = 176;
        public const int CollapsedWidth = 44;
        /// <summary>集合封面缩略图边长（外部构建封面位图时按此尺寸）。</summary>
        public const int ThumbSize = 26;

        private int HeaderH { get { return Dpi(36); } }   // 与列表表头/左侧分类行同高，三栏文字中心对齐
        private int RowH { get { return Dpi(38); } }
        private int ItemH { get { return Dpi(36); } }
        private int PadX { get { return Dpi(10); } }
        private int FooterH { get { return Dpi(44); } }
        private int CoverGap { get { return Dpi(8); } }
        private int Radius { get { return Dpi(8); } }
        private int ScrollBarW { get { return Dpi(4); } }
        private int ScrollHitW { get { return Dpi(12); } }

        private int Dpi(int value)
        {
            return DpiHelper.Scale(value, DpiHelper.ForControl(this));
        }

        private readonly List<Item> items = new List<Item>();
        private string activeKey = string.Empty;
        private int hoverIndex = -1;
        private bool hoverFooter;
        private int scrollOffset;
        private bool scrollbarHot;
        private bool scrollDragging;
        private int scrollGrabDy;
        private int externalDragIndex = -1;    // 主内容区拖动时的投放高亮行
        private bool collapsed;
        private bool hoverCollapse;
        private bool createSubfolder;
        private int arrowHoverIndex = -1;
        private int lastArrowToggleTick;
        private int lastArrowToggleIndex = -1;

        private readonly SolidBrush activeBrush = new SolidBrush(Theme.SelectionBg);
        private readonly SolidBrush hoverBrush = new SolidBrush(Color.FromArgb(140, 255, 255, 255));
        private readonly SolidBrush cardBrush = new SolidBrush(Theme.Card);
        private readonly SolidBrush textBrush = new SolidBrush(Theme.TextPrimary);
        private readonly SolidBrush accentBrush = new SolidBrush(Theme.AccentText);
        private readonly SolidBrush subBrush = new SolidBrush(Theme.TextSecondary);
        private readonly SolidBrush chipBrush = new SolidBrush(Theme.ChipBg);
        private readonly Pen dropPen = new Pen(Theme.Accent, 2f);
        private readonly Pen placeholderPen = new Pen(Theme.TextSecondary, 1.2f);

        /// <summary>单击集合：选中（参数为集合键，空串表示「全部」）。</summary>
        public event Action<string> ItemSelected;
        /// <summary>双击集合：直接进入内容（参数为集合键，空串表示「全部」）。</summary>
        public event Action<string> ItemActivated;
        /// <summary>右键自定义集合：编辑/删除菜单（集合键 + 屏幕坐标）。</summary>
        public event Action<string, Point> ContextRequested;
        /// <summary>展开/收起树节点：集合键 + 目标展开状态。</summary>
        public event Action<string, bool> ToggleRequested;
        /// <summary>点击底部「+ 新建集合」。</summary>
        public event Action CreateRequested;
        /// <summary>集合行的外部拖放：统一计算效果，drop=true 时接收文件。</summary>
        public Func<string, DragEventArgs, bool, DragDropEffects> ExternalDrop;
        /// <summary>集合栏收起或展开后触发，供主窗体重新布局。</summary>
        public event Action CollapsedChanged;

        public bool Collapsed { get { return collapsed; } }
        public int CurrentWidth { get { return collapsed ? CollapsedWidth : PreferredWidth; } }

        public CollectionSidebar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, true);    // 点击侧栏后可键盘导航
            TabStop = false;                             // 不参与 Tab 顺序，避免抢网格焦点
            BackColor = Theme.SidebarBg;
            AutoScroll = false;
            AllowDrop = true;
        }

        // ===== 数据注入（位图所有权随之转移） =====

        /// <summary>设置集合列表；旧封面位图由本控件释放。</summary>
        public void SetItems(List<Item> list)
        {
            SetItems(list, null);
        }

        /// <summary>设置集合列表并保留指定键的旧封面（避免编辑中当前集合封面被释放重建）。</summary>
        public void SetItems(List<Item> list, string preserveCoverKey)
        {
            string anchorKey = null;
            int anchorOffset = 0;
            List<string> anchorChain = null;
            CaptureScrollAnchor(out anchorKey, out anchorOffset, out anchorChain);
            Bitmap preserved = null;
            if (!string.IsNullOrEmpty(preserveCoverKey))
            {
                for (int i = 0; i < items.Count; i++)
                {
                    Item it = items[i];
                    if (it.Key == preserveCoverKey && it.Cover != null)
                    {
                        preserved = it.Cover;
                        it.Cover = null;   // 摘出，避免被下方 DisposeCovers 释放
                        break;
                    }
                }
            }
            DisposeCovers();
            items.Clear();
            if (list != null)
            {
                items.AddRange(list);
                if (preserved != null)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (items[i].Key == preserveCoverKey && items[i].Cover == null)
                        {
                            items[i].Cover = preserved;
                            preserved = null;
                            break;
                        }
                    }
                    if (preserved != null) preserved.Dispose();   // 列表中已无该键，避免泄漏
                }
            }
            externalDragIndex = -1;
            hoverIndex = -1;
            arrowHoverIndex = -1;
            RestoreScrollAnchor(anchorKey, anchorOffset, anchorChain);
            Invalidate();
        }

        /// <summary>设置选中集合键（空串 = 「全部」）。</summary>
        public void SetActive(string key)
        {
            activeKey = key ?? string.Empty;
            Invalidate();
        }

        /// <summary>底栏新建入口是否针对当前文件夹；仅影响文案，创建目标由主窗体按有效上下文决定。</summary>
        public void SetCreateSubfolder(bool value)
        {
            if (createSubfolder == value) return;
            createSubfolder = value;
            Invalidate();
        }

        /// <summary>显式导航后把指定集合滚入可视区；后台刷新不得调用，以免抢用户滚动位置。</summary>
        public void ScrollToKey(string key)
        {
            int index = IndexOfKey(key);
            if (index < 0 || ViewportHeight <= 0) return;
            EnsureVisible(index);
        }

        private int IndexOfKey(string key)
        {
            key = key ?? string.Empty;
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                if (key.Length == 0 ? item.IsAll : string.Equals(item.Key, key, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        private void CaptureScrollAnchor(out string key, out int offset, out List<string> chain)
        {
            key = null;
            offset = 0;
            chain = null;
            if (items.Count == 0 || ViewportHeight <= 0) return;
            int index = Math.Max(0, Math.Min(items.Count - 1, scrollOffset / RowH));
            key = items[index].Key;
            offset = scrollOffset - index * RowH;
            chain = new List<string>();
            Item current = items[index];
            for (int guard = 0; current != null && guard < 64; guard++)
            {
                chain.Add(current.Key);
                if (current.IsAll || string.IsNullOrEmpty(current.ParentKey)) break;
                int parentIndex = IndexOfKey(current.ParentKey);
                current = parentIndex < 0 ? null : items[parentIndex];
            }
            if (chain.Count == 0 || chain[chain.Count - 1].Length != 0) chain.Add(string.Empty);
        }

        private void RestoreScrollAnchor(string key, int offset, List<string> chain)
        {
            if (items.Count == 0)
            {
                scrollOffset = 0;
                return;
            }
            int index = IndexOfKey(key);
            bool exact = index >= 0;
            if (!exact && chain != null)
            {
                for (int i = 1; i < chain.Count && index < 0; i++) index = IndexOfKey(chain[i]);
            }
            if (index < 0) index = 0;
            scrollOffset = index * RowH + (exact ? Math.Max(0, offset) : 0);
            ClampScroll();
        }

        private void EnsureVisible(int index)
        {
            if (index < 0 || index >= items.Count || ViewportHeight <= 0) return;
            int top = index * RowH;
            int bottom = top + RowH;
            int newOffset = scrollOffset;
            if (top < scrollOffset) newOffset = top;
            else if (bottom > scrollOffset + ViewportHeight) newOffset = bottom - ViewportHeight;
            SetScroll(newOffset);
        }

        private void DisposeCovers()
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i].Cover != null) { items[i].Cover.Dispose(); items[i].Cover = null; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCovers();
                activeBrush.Dispose();
                hoverBrush.Dispose();
                cardBrush.Dispose();
                textBrush.Dispose();
                accentBrush.Dispose();
                subBrush.Dispose();
                chipBrush.Dispose();
                dropPen.Dispose();
                placeholderPen.Dispose();
            }
            base.Dispose(disposing);
        }

        // ===== 落点命中（主内容区条目拖出） =====

        /// <summary>按屏幕坐标返回命中的自定义集合键；未命中或命中「全部」返回 null。</summary>
        public string DropCollectionAtScreen(Point screen)
        {
            if (!Visible || collapsed) return null;
            Point p = PointToClient(screen);
            if (p.X < 0 || p.X > ClientSize.Width) return null;
            int idx = IndexAt(p.Y);
            if (idx < 0 || idx >= items.Count) return null;
            return items[idx].IsAll ? null : items[idx].Key;
        }

        /// <summary>设置外部拖动时的投放高亮（传 null/空清除）。</summary>
        public void SetExternalDropHighlight(string key)
        {
            int idx = -1;
            if (!string.IsNullOrEmpty(key))
            {
                for (int i = 0; i < items.Count; i++)
                    if (!items[i].IsAll && items[i].Key == key) { idx = i; break; }
            }
            if (idx != externalDragIndex) { externalDragIndex = idx; Invalidate(); }
        }

        // ===== 外部文件拖入（OLE） =====

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            UpdateExternalDropFeedback(e);
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            UpdateExternalDropFeedback(e);
        }

        protected override void OnDragLeave(EventArgs e)
        {
            SetExternalDropHighlight(null);
            base.OnDragLeave(e);
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            string key = DropCollectionAtScreen(new Point(e.X, e.Y));
            SetExternalDropHighlight(null);
            if (key == null || ExternalDrop == null) return;
            e.Effect = ExternalDrop(key, e, true);
        }

        /// <summary>外部拖入反馈：仅自定义集合行接受文件拖放。</summary>
        private void UpdateExternalDropFeedback(DragEventArgs e)
        {
            string key = DropCollectionAtScreen(new Point(e.X, e.Y));
            e.Effect = key != null && ExternalDrop != null ? ExternalDrop(key, e, false) : DragDropEffects.None;
            SetExternalDropHighlight(key);
        }

        // ===== 布局 =====

        private int ListTop { get { return HeaderH; } }
        private int ListBottom { get { return ClientSize.Height - FooterH; } }
        private int ViewportHeight
        {
            get { int h = ListBottom - ListTop; return h > 0 ? h : 0; }
        }

        private Rectangle ItemRect(int index)
        {
            return new Rectangle(PadX, ListTop + index * RowH - scrollOffset, ClientSize.Width - PadX * 2, ItemH);
        }

        private int MaxScroll
        {
            get
            {
                int m = items.Count * RowH - ViewportHeight;
                return m > 0 ? m : 0;
            }
        }

        private void ClampScroll()
        {
            if (scrollOffset < 0) scrollOffset = 0;
            int m = MaxScroll;
            if (scrollOffset > m) scrollOffset = m;
        }

        private int IndexAt(int y)
        {
            if (y < ListTop || y >= ListBottom) return -1;
            int idx = (y - ListTop + scrollOffset) / RowH;
            return idx >= 0 && idx < items.Count ? idx : -1;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ClampScroll();
        }

        /// <summary>滚轮滚动（供主窗体滚轮路由调用）：每次滚动一行，避免集合行高 38px × 系统行数造成跳动过大。</summary>
        private int wheelRemainder;   // 不足一像素的滚轮累计量（单位：delta × 像素）

        public void ScrollByWheel(int delta)
        {
            if (collapsed || delta == 0 || MaxScroll <= 0) return;
            // 按像素换算并累计余数：精确式触控板每次 delta 小于 120，整除会得到 0 而完全滚不动
            wheelRemainder += delta * RowH;
            int pixels = wheelRemainder / 120;
            wheelRemainder -= pixels * 120;
            if (pixels != 0) SetScroll(scrollOffset - pixels);
        }

        private void SetScroll(int offset)
        {
            int m = MaxScroll;
            if (offset < 0) offset = 0;
            if (offset > m) offset = m;
            if (offset != scrollOffset) { scrollOffset = offset; Invalidate(); }
        }

        // ===== 绘制 =====

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.SidebarBg);

            // 标题 + 右侧/底部分隔线：表头文字与列表表头、左侧首个分类垂直居中于同一水平线
            if (!collapsed)
                Draw.DrawStringLeft(g, "集合", Theme.ButtonFont, textBrush,
                    new Rectangle(PadX + 4, 0, Math.Max(0, CollapseRect.Left - PadX - 10), HeaderH));
            PaintCollapseButton(g);
            using (Pen sep = new Pen(Theme.Separator, 1))
            {
                g.DrawLine(sep, ClientSize.Width - 1, 0, ClientSize.Width - 1, ClientSize.Height);
                g.DrawLine(sep, 0, HeaderH - 1, ClientSize.Width, HeaderH - 1);
            }
            if (collapsed) return;

            // 集合行（裁剪到列表视口，随滚动位移）
            GraphicsState st = g.Save();
            g.SetClip(new Rectangle(0, ListTop, ClientSize.Width, ViewportHeight), CombineMode.Intersect);
            for (int i = 0; i < items.Count; i++)
            {
                Rectangle r = ItemRect(i);
                if (r.Bottom <= ListTop || r.Y >= ListBottom) continue;
                PaintItem(g, items[i], i, r);
            }
            g.Restore(st);

            // 滚动条：条目超出可视区时显示（iOS 细条）
            if (MaxScroll > 0) PaintScrollBar(g);

            // 底部「+ 新建集合」
            PaintFooter(g);
        }

        private Rectangle CollapseRect
        {
            get
            {
                int x = collapsed ? (ClientSize.Width - 26) / 2 : ClientSize.Width - 34;
                return new Rectangle(x, 5, 26, 26);
            }
        }

        private void PaintCollapseButton(Graphics g)
        {
            if (hoverCollapse)
            {
                using (SolidBrush hover = new SolidBrush(Theme.CardHover))
                    Draw.FillRoundedRect(g, hover, CollapseRect, 6);
            }
            Draw.DrawIcon(g, collapsed ? UiIconKind.PanelLeftOpen : UiIconKind.PanelLeftClose,
                CollapseRect, Theme.IconColor);
        }

        private void ToggleCollapsed()
        {
            collapsed = !collapsed;
            hoverIndex = -1;
            hoverFooter = false;
            arrowHoverIndex = -1;
            scrollbarHot = false;
            SetExternalDropHighlight(null);
            Invalidate();
            Action handler = CollapsedChanged;
            if (handler != null) handler();
        }

        private void PaintItem(Graphics g, Item item, int index, Rectangle r)
        {
            bool active = item.Key == activeKey || (item.IsAll && activeKey.Length == 0);
            if (active) Draw.FillRoundedRect(g, activeBrush, r, Radius);
            else if (index == hoverIndex) Draw.FillRoundedRect(g, hoverBrush, r, Radius);
            if (index == externalDragIndex)
                Draw.DrawRoundedRect(g, dropPen, r, Radius);

            Rectangle arrow = ArrowRect(item, r);
            if (item.HasChildren)
            {
                if (index == arrowHoverIndex)
                    Draw.FillRoundedRect(g, hoverBrush, arrow, 4);
                DrawTreeChevron(g, arrow, item.Expanded, active ? Theme.AccentText : Theme.TextSecondary);
            }

            int thumbSize = Dpi(ThumbSize);
            int coverX = item.IsAll ? r.X + Dpi(8) : arrow.Right + Dpi(2);
            Rectangle cover = new Rectangle(coverX, r.Y + (r.Height - thumbSize) / 2, thumbSize, thumbSize);
            if (item.IsAll)
            {
                PaintAllGlyph(g, cover, active);
            }
            else
            {
                using (GraphicsPath cp = Draw.RoundedPath(cover, 7))
                {
                    g.FillPath(cardBrush, cp);
                    GraphicsState cs = g.Save();
                    g.SetClip(cp);
                    if (item.Cover != null) DrawCoverFilled(g, item.Cover, cover);
                    else PaintCoverPlaceholder(g, cover);
                    g.Restore(cs);
                }
            }

            Rectangle textRect = new Rectangle(cover.Right + CoverGap, r.Y, r.Right - cover.Right - CoverGap - 8, r.Height);
            Draw.DrawStringLeft(g, item.Name, Theme.NavigationFont,
                active ? accentBrush : textBrush, textRect);
            if (Focused && active)
                ControlPaint.DrawFocusRectangle(g, new Rectangle(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4),
                    Theme.TextPrimary, Color.Empty);
        }

        /// <summary>树形缩进：深层的后续层级不再继续右移，避免 16 层时文字完全消失。</summary>
        private int TreeIndent(Item item)
        {
            int depth = item == null ? 0 : Math.Max(0, Math.Min(4, item.Depth));
            return depth * Dpi(12);
        }

        private Rectangle ArrowRect(Item item, Rectangle row)
        {
            int size = Dpi(14);
            int x = row.X + Dpi(2) + TreeIndent(item);
            return new Rectangle(x, row.Y + (row.Height - size) / 2, size, size);
        }

        private static void DrawTreeChevron(Graphics g, Rectangle r, bool expanded, Color color)
        {
            int cx = r.X + r.Width / 2;
            int cy = r.Y + r.Height / 2;
            int s = Math.Max(3, r.Width / 4);
            using (Pen p = new Pen(color, 1.6f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                if (expanded)
                {
                    g.DrawLines(p, new Point[]
                    {
                        new Point(cx - s, cy - s / 2),
                        new Point(cx, cy + s / 2),
                        new Point(cx + s, cy - s / 2)
                    });
                }
                else
                {
                    g.DrawLines(p, new Point[]
                    {
                        new Point(cx - s / 2, cy - s),
                        new Point(cx + s / 2, cy),
                        new Point(cx - s / 2, cy + s)
                    });
                }
            }
        }

        /// <summary>「全部」图标：浅底圆角 + 2x2 网格点，选中时强调色。</summary>
        private void PaintAllGlyph(Graphics g, Rectangle r, bool active)
        {
            Draw.FillRoundedRect(g, cardBrush, r, 7);
            SolidBrush b = active ? accentBrush : subBrush;
            int s = 6, gap = 3;
            int left = r.X + (r.Width - (s * 2 + gap)) / 2;
            int top = r.Y + (r.Height - (s * 2 + gap)) / 2;
            Draw.FillRoundedRect(g, b, new Rectangle(left, top, s, s), 2);
            Draw.FillRoundedRect(g, b, new Rectangle(left + s + gap, top, s, s), 2);
            Draw.FillRoundedRect(g, b, new Rectangle(left, top + s + gap, s, s), 2);
            Draw.FillRoundedRect(g, b, new Rectangle(left + s + gap, top + s + gap, s, s), 2);
        }

        /// <summary>等比裁剪填满缩略图（居中裁剪，类似 CSS cover）。</summary>
        private static void DrawCoverFilled(Graphics g, Bitmap bmp, Rectangle dest)
        {
            int bw = bmp.Width, bh = bmp.Height;
            if (bw <= 0 || bh <= 0) return;
            Rectangle src;
            if ((double)bw / bh > (double)dest.Width / dest.Height)
            {
                int w = (int)(bh * dest.Width / (double)dest.Height);
                src = new Rectangle((bw - w) / 2, 0, w, bh);
            }
            else
            {
                int h = (int)(bw * dest.Height / (double)dest.Width);
                src = new Rectangle(0, (bh - h) / 2, bw, h);
            }
            g.DrawImage(bmp, dest, src, GraphicsUnit.Pixel);
        }

        /// <summary>无封面占位：浅灰底 + 简约风景图形（缩略图尺寸）。</summary>
        private void PaintCoverPlaceholder(Graphics g, Rectangle r)
        {
            g.FillRectangle(chipBrush, r);
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            g.DrawEllipse(placeholderPen, cx - 7, cy - 6, 4, 4);
            using (GraphicsPath m = new GraphicsPath())
            {
                m.AddLine(cx - 9, cy + 5, cx - 3, cy - 3);
                m.AddLine(cx - 3, cy - 3, cx + 1, cy + 1);
                m.AddLine(cx + 1, cy + 1, cx + 4, cy - 2);
                m.AddLine(cx + 4, cy - 2, cx + 9, cy + 5);
                g.DrawPath(placeholderPen, m);
            }
        }

        private void PaintScrollBar(Graphics g)
        {
            Rectangle track = new Rectangle(ClientSize.Width - ScrollBarW - 3, ListTop, ScrollBarW, ViewportHeight);
            int thumbH = Math.Max(18, (int)((long)ViewportHeight * ViewportHeight / (items.Count * RowH)));
            int travel = track.Height - thumbH;
            int y = travel > 0 ? track.Y + (int)((long)scrollOffset * travel / MaxScroll) : track.Y;
            int alpha = (scrollbarHot || scrollDragging) ? 150 : 80;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, 60, 60, 67)))
                Draw.FillRoundedRect(g, b, new Rectangle(track.X, y, ScrollBarW, thumbH), ScrollBarW / 2);
        }

        private void PaintFooter(Graphics g)
        {
            // 底部「+ 新建集合」：平铺底栏，不做卡片
            int footY = ClientSize.Height - FooterH;
            using (Pen p = new Pen(Theme.Separator, 1))
                g.DrawLine(p, PadX, footY, ClientSize.Width - PadX, footY);
            Rectangle footRect = new Rectangle(PadX, footY + 4, ClientSize.Width - PadX * 2, FooterH - 8);
            if (hoverFooter)
            {
                using (SolidBrush b = new SolidBrush(Theme.CardHover))
                    Draw.FillRoundedRect(g, b, footRect, Radius);
            }
            Draw.DrawIcon(g, UiIconKind.Add,
                new Rectangle(footRect.X + 7, footRect.Y + (footRect.Height - 18) / 2, 18, 18), Theme.IconColor);
            Draw.DrawStringLeft(g, createSubfolder ? "新建子文件夹" : "新建集合", Theme.ButtonFont, textBrush,
                new Rectangle(footRect.X + 32, footRect.Y, footRect.Width - 36, footRect.Height));
        }

        // ===== 交互 =====

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            bool overCollapse = CollapseRect.Contains(e.Location);
            if (hoverCollapse != overCollapse) { hoverCollapse = overCollapse; Invalidate(); }
            if (overCollapse)
            {
                hoverIndex = -1;
                hoverFooter = false;
                if (arrowHoverIndex != -1) { arrowHoverIndex = -1; Invalidate(); }
                Cursor = Cursors.Default;
                return;
            }
            if (collapsed)
            {
                hoverIndex = -1;
                arrowHoverIndex = -1;
                Cursor = Cursors.Default;
                return;
            }

            if (scrollDragging)
            {
                Rectangle track = ScrollTrackRect();
                int thumbH = ScrollThumbHeight();
                int travel = track.Height - thumbH;
                if (travel > 0)
                {
                    int off = (int)((long)(e.Y - scrollGrabDy - track.Y) * MaxScroll / travel);
                    SetScroll(off);
                }
                Cursor = Cursors.Default;
                return;
            }
            if (MaxScroll > 0 && ScrollHitRect().Contains(e.Location))
            {
                if (!scrollbarHot) { scrollbarHot = true; Invalidate(); }
                if (hoverIndex != -1) { hoverIndex = -1; Invalidate(); }
                if (arrowHoverIndex != -1) { arrowHoverIndex = -1; Invalidate(); }
                hoverFooter = false;
                Cursor = Cursors.Default;
                return;
            }
            if (scrollbarHot) { scrollbarHot = false; Invalidate(); }

            if (e.Y >= ClientSize.Height - FooterH)
            {
                if (!hoverFooter) { hoverFooter = true; Invalidate(); }
                if (hoverIndex != -1) { hoverIndex = -1; Invalidate(); }
                if (arrowHoverIndex != -1) { arrowHoverIndex = -1; Invalidate(); }
                Cursor = Cursors.Default;
                return;
            }
            if (hoverFooter) { hoverFooter = false; Invalidate(); }

            int idx = IndexAt(e.Y);
            int arrow = ArrowIndexAt(e.Location);
            if (arrow != arrowHoverIndex) { arrowHoverIndex = arrow; Invalidate(); }
            if (idx != hoverIndex)
            {
                hoverIndex = idx;
                Invalidate();
            }
            Cursor = arrow >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            bool dirty = hoverIndex != -1 || hoverFooter || hoverCollapse || scrollbarHot || arrowHoverIndex != -1;
            hoverIndex = -1;
            hoverFooter = false;
            hoverCollapse = false;
            scrollbarHot = false;
            arrowHoverIndex = -1;
            if (dirty) Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (collapsed || CollapseRect.Contains(e.Location)) return;
            if (IndexAt(e.Y) >= 0) Focus();
            if (MaxScroll > 0 && ScrollHitRect().Contains(e.Location))
            {
                Rectangle thumb = ScrollThumbRect();
                if (thumb.Contains(e.Location))
                {
                    scrollDragging = true;
                    scrollGrabDy = e.Y - thumb.Y;
                    Capture = true;
                }
                else
                {
                    // 点击滑块外的轨道：按页滚动到点击位置
                    SetScroll((int)((long)(e.Y - ListTop) * MaxScroll / Math.Max(1, ViewportHeight - ScrollThumbHeight())));
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (scrollDragging)
            {
                scrollDragging = false;
                Capture = false;
                Invalidate();
            }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (CollapseRect.Contains(e.Location))
            {
                if (e.Button == MouseButtons.Left) ToggleCollapsed();
                return;
            }
            if (collapsed) return;
            if (MaxScroll > 0 && ScrollHitRect().Contains(e.Location)) return;   // 滚动条区域不触发选中
            if (e.Y >= ClientSize.Height - FooterH)
            {
                if (e.Button == MouseButtons.Left)
                {
                    Action h = CreateRequested;
                    if (h != null) h();
                }
                return;
            }
            int idx = IndexAt(e.Y);
            if (idx < 0 || idx >= items.Count) return;
            Item it = items[idx];
            if (e.Button == MouseButtons.Left && IsArrowAt(e.Location, idx))
            {
                ToggleIndex(idx, true);
                return;
            }
            if (e.Button == MouseButtons.Right)
            {
                if (it.IsAll) return;
                Action<string, Point> h = ContextRequested;
                if (h != null) h(it.Key, PointToScreen(e.Location));
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            SelectIndex(idx);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;
            if (collapsed || CollapseRect.Contains(e.Location)) return;
            if (MaxScroll > 0 && ScrollHitRect().Contains(e.Location)) return;
            if (e.Y >= ClientSize.Height - FooterH) return;
            int idx = IndexAt(e.Y);
            if (idx < 0 || idx >= items.Count) return;
            if (IsArrowAt(e.Location, idx)) return;   // 箭头只负责展开/收起
            Action<string> h = ItemActivated;
            if (h != null) h(items[idx].Key);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollByWheel(e.Delta);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            arrowHoverIndex = -1;
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.Up || key == Keys.Down || key == Keys.Left || key == Keys.Right
                || key == Keys.Home || key == Keys.End || key == Keys.Enter || key == Keys.Space)
                return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (collapsed || items.Count == 0) return;
            int index = CurrentIndex();
            if (index < 0) index = 0;
            switch (e.KeyCode)
            {
                case Keys.Up:
                    SelectIndex(Math.Max(0, index - 1));
                    e.Handled = true;
                    break;
                case Keys.Down:
                    SelectIndex(Math.Min(items.Count - 1, index + 1));
                    e.Handled = true;
                    break;
                case Keys.Home:
                    SelectIndex(0);
                    e.Handled = true;
                    break;
                case Keys.End:
                    SelectIndex(items.Count - 1);
                    e.Handled = true;
                    break;
                case Keys.Right:
                    Item rightItem = items[index];
                    if (rightItem.HasChildren)
                    {
                        if (!rightItem.Expanded) ToggleIndex(index, false);
                        else if (index + 1 < items.Count
                            && string.Equals(items[index + 1].ParentKey, rightItem.Key, StringComparison.Ordinal))
                            SelectIndex(index + 1);
                    }
                    e.Handled = true;
                    break;
                case Keys.Left:
                    Item leftItem = items[index];
                    if (leftItem.HasChildren && leftItem.Expanded)
                    {
                        ToggleIndex(index, false);
                    }
                    else if (leftItem.Depth > 0)
                    {
                        int parent = IndexOfKey(leftItem.ParentKey);
                        SelectIndex(parent >= 0 ? parent : IndexOfKey(string.Empty));
                    }
                    e.Handled = true;
                    break;
                case Keys.Space:
                    ToggleIndex(index, false);
                    e.Handled = true;
                    break;
                case Keys.Enter:
                    Action<string> activate = ItemActivated;
                    if (activate != null) activate(items[index].Key);
                    e.Handled = true;
                    break;
            }
        }

        private int CurrentIndex()
        {
            int index = IndexOfKey(activeKey);
            return index >= 0 ? index : (items.Count > 0 ? 0 : -1);
        }

        private void SelectIndex(int index)
        {
            if (index < 0 || index >= items.Count) return;
            Item item = items[index];
            SetActive(item.Key);
            EnsureVisible(index);
            Action<string> handler = ItemSelected;
            if (handler != null) handler(item.Key);
        }

        private int ArrowIndexAt(Point p)
        {
            int index = IndexAt(p.Y);
            return IsArrowAt(p, index) ? index : -1;
        }

        private bool IsArrowAt(Point p, int index)
        {
            if (index < 0 || index >= items.Count) return false;
            Item item = items[index];
            return item.HasChildren && ArrowRect(item, ItemRect(index)).Contains(p);
        }

        private void ToggleIndex(int index, bool fromMouse)
        {
            if (index < 0 || index >= items.Count) return;
            Item item = items[index];
            if (!item.HasChildren) return;
            if (fromMouse)
            {
                int now = Environment.TickCount;
                if (index == lastArrowToggleIndex && unchecked(now - lastArrowToggleTick) < SystemInformation.DoubleClickTime)
                    return;
                lastArrowToggleTick = now;
                lastArrowToggleIndex = index;
            }
            Action<string, bool> handler = ToggleRequested;
            if (handler != null) handler(item.Key, !item.Expanded);
        }

        /// <summary>是否为空白区（非行/非底部按钮/非滚动条），供窗体拖动判定。</summary>
        public bool IsBlankArea(Point p)
        {
            if (CollapseRect.Contains(p)) return false;
            if (collapsed) return true;
            if (p.Y >= ClientSize.Height - FooterH) return false;
            if (MaxScroll > 0 && ScrollHitRect().Contains(p)) return false;
            return IndexAt(p.Y) < 0;
        }

        private Rectangle ScrollTrackRect()
        {
            return new Rectangle(ClientSize.Width - ScrollBarW - 3, ListTop, ScrollBarW, ViewportHeight);
        }

        private Rectangle ScrollHitRect()
        {
            return new Rectangle(ClientSize.Width - ScrollHitW, ListTop, ScrollHitW, ViewportHeight);
        }

        private int ScrollThumbHeight()
        {
            return Math.Max(18, (int)((long)ViewportHeight * ViewportHeight / Math.Max(1, items.Count * RowH)));
        }

        private Rectangle ScrollThumbRect()
        {
            Rectangle track = ScrollTrackRect();
            int thumbH = ScrollThumbHeight();
            int travel = track.Height - thumbH;
            int y = travel > 0 ? track.Y + (int)((long)scrollOffset * travel / Math.Max(1, MaxScroll)) : track.Y;
            return new Rectangle(track.X, y, track.Width, thumbH);
        }
    }
}
