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
            SetStyle(ControlStyles.Selectable, false);   // 不抢网格键盘焦点
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
            ClampScroll();
            Invalidate();
        }

        /// <summary>设置选中集合键（空串 = 「全部」）。</summary>
        public void SetActive(string key)
        {
            activeKey = key ?? string.Empty;
            Invalidate();
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
        public void ScrollByWheel(int delta)
        {
            if (collapsed || delta == 0 || MaxScroll <= 0) return;
            SetScroll(scrollOffset - (delta / 120) * RowH);
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

            int thumbSize = Dpi(ThumbSize);
            Rectangle cover = new Rectangle(r.X + Dpi(8), r.Y + (r.Height - thumbSize) / 2, thumbSize, thumbSize);
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
            Draw.DrawStringLeft(g, "新建集合", Theme.ButtonFont, textBrush,
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
                Cursor = Cursors.Default;
                return;
            }
            if (collapsed)
            {
                hoverIndex = -1;
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
                hoverFooter = false;
                Cursor = Cursors.Default;
                return;
            }
            if (scrollbarHot) { scrollbarHot = false; Invalidate(); }

            if (e.Y >= ClientSize.Height - FooterH)
            {
                if (!hoverFooter) { hoverFooter = true; Invalidate(); }
                if (hoverIndex != -1) { hoverIndex = -1; Invalidate(); }
                Cursor = Cursors.Default;
                return;
            }
            if (hoverFooter) { hoverFooter = false; Invalidate(); }

            int idx = IndexAt(e.Y);
            if (idx != hoverIndex)
            {
                hoverIndex = idx;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            bool dirty = hoverIndex != -1 || hoverFooter || hoverCollapse || scrollbarHot;
            hoverIndex = -1;
            hoverFooter = false;
            hoverCollapse = false;
            scrollbarHot = false;
            if (dirty) Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (collapsed || CollapseRect.Contains(e.Location)) return;
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
            if (e.Button == MouseButtons.Right)
            {
                if (it.IsAll) return;
                Action<string, Point> h = ContextRequested;
                if (h != null) h(it.Key, PointToScreen(e.Location));
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            Action<string> sel = ItemSelected;
            if (sel != null) sel(it.Key);
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
            Action<string> h = ItemActivated;
            if (h != null) h(items[idx].Key);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollByWheel(e.Delta);
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
