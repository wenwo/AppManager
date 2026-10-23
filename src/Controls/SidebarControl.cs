using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>
    /// 侧栏：顶部 iOS 分段控件（软件/网站），下方分类列表；"全部软件"置底并加分隔线；
    /// 标记为 DropTarget 的条目（可编辑分类）可接收文件/文件夹拖放，回调 FilesDropped。
    /// </summary>
    public class SidebarControl : ScrollableControl
    {
        /// <summary>侧栏条目。</summary>
        public class SidebarItem
        {
            public string Text = string.Empty;
            public string Key = string.Empty;
            public bool Active;
            public bool Separator;
            public bool DropTarget;   // 可接收拖放（文件/文件夹）
            public bool Orderable;    // 参与拖动排序
            public int OrderGroup;    // 排序分组，阻止跨组拖动
        }

        /// <summary>内容模式：软件 / 网站。</summary>
        public enum ContentMode { Software, Website }

        private readonly List<SidebarItem> items = new List<SidebarItem>();
        private int hoverIndex = -1;
        private int dragIndex = -1;
        private int externalDragIndex = -1;   // 外部（主内容区）拖动时的高亮目标行
        private const int ItemHeight = 36;
        private const int SegHeight = 28;      // 顶部分段控件（软件/网站）高度
        private static readonly int HeaderHeight = Theme.TopBarHeight;   // 顶栏总高：分类列表从其下开始
        private const int FooterHeight = 44;   // 底部「+ 新建分类」按钮高度
        private const int ListPad = 8;         // 分类列表上下留白
        private const int ScrollBarW = 4;      // 滚动条绘制宽度（iOS 细条）
        private const int ScrollHitW = 12;     // 滚动条命中区宽度（比可视条宽，便于抓取）
        private ContentMode mode = ContentMode.Software;
        private int hoverSegment = -1;         // 分段悬停：0=软件 1=网站
        private int hoverFooter = -1;          // 底部按钮悬停：0=新建分类

        // 分类列表滚动（顶部分段控件与底部按钮固定，仅中间列表区滚动）
        private int scrollOffset;              // 列表滚动偏移（>=0）
        private bool scrollDragging;           // 正在拖动滚动条
        private int scrollGrabDy;              // 抓取点相对滑块顶部的偏移
        private bool scrollbarHot;             // 滚动条悬停（加深显示）

        // 区块划分（按 Orderable 标记）：中间区块（自定义/内置分类）滚动，其前的常用/快捷中心固定顶部，
        // 其后的全部软件/全部文件/全部网站固定底部
        private int scrollStart = -1;          // 中间区块起始下标
        private int scrollEnd = -1;            // 中间区块结束下标（含）
        private bool hasScrollBlock;           // 是否存在中间滚动区块

        // 拖动排序状态（仅中间区块：内置分类 + 自定义分类）
        private bool dragPending;
        private bool dragging;
        private bool suppressClick;
        private int dragFrom = -1;
        private int dragGap = -1;
        private Point dragStart;

        public event Action<string> SelectedChanged;
        /// <summary>文件/文件夹拖放到可投放分类时触发，参数为（目标分类 key, 被拖入的路径）。</summary>
        public event Action<string, string[]> FilesDropped;
        /// <summary>内容模式切换时触发（软件/网站）。</summary>
        public event Action<ContentMode> ModeChanged;
        /// <summary>点击底部「+ 新建分类」时触发。</summary>
        public event Action AddCategoryRequested;
        /// <summary>右键某个分类时触发，参数为分类名 + 屏幕坐标。</summary>
        public event Action<string, Point> CategoryContextRequested;
        /// <summary>分类拖动排序完成：from=源索引，to=目标索引（均为 items 下标）。</summary>
        public event Action<int, int> ReorderRequested;

        public ContentMode Mode
        {
            get { return mode; }
            set { mode = value; Invalidate(); }
        }

        public SidebarControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Theme.SidebarBg;
            AutoScroll = false;
            Font = Theme.FontOf(10F);
            DoubleBuffered = true;
            AllowDrop = true;
        }

        public void SetItems(List<SidebarItem> list)
        {
            items.Clear();
            items.AddRange(list);
            externalDragIndex = -1;
            RebuildZones();
            ClampScroll();
            Invalidate();
        }

        /// <summary>按 Orderable 标记重新划分区块：连续的可排序条目为中间滚动区，其余前后分别为顶部/底部固定区。</summary>
        private void RebuildZones()
        {
            int start = -1, end = -1;
            for (int i = 0; i < items.Count; i++)
            {
                if (!items[i].Orderable) continue;
                if (start < 0) start = i;
                end = i;
            }
            hasScrollBlock = start >= 0;
            scrollStart = start;
            scrollEnd = end;
        }

        /// <summary>顶部固定条目数（中间区块之前：常用/快捷中心及分隔线）。</summary>
        private int TopCount
        {
            get { return hasScrollBlock ? scrollStart : items.Count; }
        }

        /// <summary>底部固定条目数（中间区块之后：全部软件/全部文件/全部网站及分隔线）。</summary>
        private int BottomCount
        {
            get { return hasScrollBlock ? items.Count - 1 - scrollEnd : 0; }
        }

        public void SetActive(string key)
        {
            for (int i = 0; i < items.Count; i++)
                items[i].Active = (items[i].Key == key);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // ===== 顶部分段控件（软件 | 网站）：位于顶栏行2 =====
            Rectangle segRect = SegRect;
            using (SolidBrush bg = new SolidBrush(Theme.SegmentBg))
                Draw.FillRoundedRect(g, bg, segRect, 9);
            int segW = segRect.Width / 2;
            Rectangle activeRect = (mode == ContentMode.Software)
                ? new Rectangle(segRect.X, segRect.Y, segW, segRect.Height)
                : new Rectangle(segRect.X + segW, segRect.Y, segRect.Width - segW, segRect.Height);
            using (SolidBrush active = new SolidBrush(Color.White))
                Draw.FillRoundedRect(g, active, activeRect, 8);
            string[] labels = { "软件", "网站" };
            for (int i = 0; i < 2; i++)
            {
                Rectangle r = new Rectangle(segRect.X + i * segW, segRect.Y, segW, segRect.Height);
                Color tc = (i == (int)mode) ? Theme.Accent : Theme.TextSecondary;
                using (SolidBrush br = new SolidBrush(tc))
                    Draw.DrawStringCenter(g, labels[i], Font, br, r);
            }

            // ===== 分类列表（顶部分段/底部按钮固定；列表内顶部/底部区块亦固定，仅中间区块随 scrollOffset 位移并裁剪）=====
            if (ListBottom > ListTop)
            {
                // 顶部固定区块（常用/快捷中心）与底部固定区块（全部*）：不裁剪、不随滚动位移
                for (int i = 0; i < TopCount; i++) PaintItem(g, i);
                for (int i = items.Count - BottomCount; i < items.Count; i++) PaintItem(g, i);

                // 中间滚动区块：裁剪到视口，条目随滚动位移
                if (hasScrollBlock)
                {
                    GraphicsState ls = g.Save();
                    g.SetClip(new Rectangle(0, ScrollTop, ClientSize.Width, ScrollViewportHeight), CombineMode.Intersect);
                    for (int i = scrollStart; i <= scrollEnd; i++)
                    {
                        int itemTop = ItemTop(i);
                        if (itemTop + ItemHeight <= ScrollTop) continue;   // 已滚出上方
                        if (itemTop >= ScrollBottom) break;                // 已滚出下方（其后条目更靠下）
                        PaintItem(g, i);
                    }

                    // ===== 拖动排序插入指示线 =====
                    if (dragging && dragGap >= 0)
                    {
                        int end = OrderableEnd();
                        int y = (dragGap <= end) ? ItemTop(dragGap) : ItemTop(end) + ItemHeight;
                        using (Pen p = new Pen(Theme.Accent, 2))
                            g.DrawLine(p, 14, y, ClientSize.Width - 14, y);
                    }
                    g.Restore(ls);
                }
            }

            // ===== 滚动条（仅分类超出可视区时显示，iOS 细条风格）=====
            if (MaxScroll > 0)
            {
                Rectangle thumb = ScrollThumbRect();
                int alpha = (scrollbarHot || scrollDragging) ? 150 : 80;
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(alpha, 60, 60, 67)))
                    Draw.FillRoundedRect(g, tb, thumb, ScrollBarW / 2);
            }

            // ===== 底部「+ 新建分类」按钮 =====
            int footY = ClientSize.Height - FooterHeight;
            using (Pen p = new Pen(Theme.Separator, 1))
                g.DrawLine(p, 12, footY, ClientSize.Width - 12, footY);
            Rectangle footRect = new Rectangle(10, footY + 4, ClientSize.Width - 20, FooterHeight - 8);
            if (hoverFooter == 0)
            {
                using (SolidBrush b = new SolidBrush(Theme.CardHover))
                    Draw.FillRoundedRect(g, b, footRect, 8);
            }
            using (SolidBrush plus = new SolidBrush(Theme.Accent))
            using (SolidBrush txt = new SolidBrush(Theme.Accent))
            using (Font ff = Theme.FontOf(11F))
            {
                // 加号图标
                int cx = footRect.X + 18, cy = footRect.Y + footRect.Height / 2;
                g.FillRectangle(plus, cx - 1, cy - 6, 2, 12);
                g.FillRectangle(plus, cx - 6, cy - 1, 12, 2);
                Draw.DrawStringLeft(g, "新建分类", ff, txt,
                    new Rectangle(footRect.X + 34, footRect.Y, footRect.Width - 40, footRect.Height));
            }
        }

        /// <summary>绘制单个分类项（分隔线 / 选中 / 悬停 / 投放高亮 + 文本），位置由 ItemTop 决定。</summary>
        private void PaintItem(Graphics g, int index)
        {
            SidebarItem it = items[index];
            Rectangle r = new Rectangle(10, ItemTop(index), ClientSize.Width - 20, ItemHeight);
            if (it.Separator)
            {
                int y = r.Y + ItemHeight / 2;
                using (Pen p = new Pen(Theme.Separator, 1)) g.DrawLine(p, r.X, y, r.Right, y);
                return;
            }
            if (it.Active)
            {
                using (SolidBrush b = new SolidBrush(Theme.SelectionBg))
                    Draw.FillRoundedRect(g, b, r, 8);
            }
            else if (index == hoverIndex)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(120, 255, 255, 255)))
                    Draw.FillRoundedRect(g, b, r, 8);
            }
            if ((index == dragIndex || index == externalDragIndex) && it.DropTarget)
            {
                using (Pen p = new Pen(Theme.Accent, 2))
                    Draw.DrawRoundedRect(g, p, r, 8);
            }
            Color tc = it.Active ? Theme.Accent : Theme.TextPrimary;
            using (SolidBrush br = new SolidBrush(tc))
                Draw.DrawStringLeft(g, it.Text, Font, br, new Rectangle(r.X + 12, r.Y, r.Width - 16, r.Height));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            // 拖动排序：越过系统阈值才开始，避免与单击冲突
            if (dragPending && (e.Button & MouseButtons.Left) == MouseButtons.Left)
            {
                if (!dragging)
                {
                    Size d = SystemInformation.DragSize;
                    if (Math.Abs(e.X - dragStart.X) > d.Width || Math.Abs(e.Y - dragStart.Y) > d.Height)
                        dragging = true;
                }
                if (dragging)
                {
                    int gap = DropGap(e.Y);
                    if (gap != dragGap) { dragGap = gap; Invalidate(); }
                    Cursor = Cursors.SizeAll;
                    return;
                }
            }

            // 滚动条：拖动中按位移换算滚动偏移
            if (scrollDragging)
            {
                Rectangle track = ScrollTrackRect();
                Rectangle thumb = ScrollThumbRect();
                int travel = track.Height - thumb.Height;
                if (travel > 0)
                {
                    int off = (int)((long)(e.Y - scrollGrabDy - track.Y) * MaxScroll / travel);
                    SetScroll(off);
                }
                Cursor = Cursors.Hand;
                return;
            }
            // 滚动条：悬停时加深显示并让出条目悬停
            if (MaxScroll > 0 && ScrollHitRect().Contains(e.Location))
            {
                if (!scrollbarHot) { scrollbarHot = true; Invalidate(); }
                if (hoverIndex != -1) { hoverIndex = -1; Invalidate(); }
                hoverSegment = -1;
                hoverFooter = -1;
                Cursor = Cursors.Hand;
                return;
            }
            if (scrollbarHot) { scrollbarHot = false; Invalidate(); }

            if (SegRect.Contains(e.Location))
            {
                int seg = e.X < ClientSize.Width / 2 ? 0 : 1;
                if (seg != hoverSegment) { hoverSegment = seg; Invalidate(); }
                Cursor = Cursors.Hand;
                hoverIndex = -1;
                hoverFooter = -1;
                return;
            }
            // 底部「+ 新建分类」按钮
            if (e.Y >= ClientSize.Height - FooterHeight)
            {
                if (hoverFooter != 0) { hoverFooter = 0; Invalidate(); }
                Cursor = Cursors.Hand;
                hoverIndex = -1;
                return;
            }
            hoverSegment = -1;
            hoverFooter = -1;
            int idx = IndexAt(e.Y);
            if (idx != hoverIndex)
            {
                hoverIndex = idx;
                Invalidate();
                Cursor = (idx >= 0 && !items[idx].Separator) ? Cursors.Hand : Cursors.Default;
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hoverIndex = -1;
            hoverSegment = -1;
            hoverFooter = -1;
            scrollbarHot = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (suppressClick) { suppressClick = false; return; }
            // 底部「+ 新建分类」
            if (e.Y >= ClientSize.Height - FooterHeight)
            {
                if (e.Button == MouseButtons.Left)
                {
                    Action h = AddCategoryRequested;
                    if (h != null) h();
                }
                return;
            }
            if (SegRect.Contains(e.Location))
            {
                int seg = e.X < ClientSize.Width / 2 ? 0 : 1;
                ContentMode newMode = seg == 0 ? ContentMode.Software : ContentMode.Website;
                if (newMode != mode)
                {
                    mode = newMode;
                    Invalidate();
                    Action<ContentMode> h = ModeChanged;
                    if (h != null) h(mode);
                }
                return;
            }
            int idx = IndexAt(e.Y);
            if (idx < 0 || idx >= items.Count) return;
            SidebarItem it = items[idx];
            if (it.Separator) return;
            if (e.Button == MouseButtons.Right)
            {
                Action<string, Point> h = CategoryContextRequested;
                if (h != null) h(it.Key, PointToScreen(e.Location));
                return;
            }
            Action<string> h2 = SelectedChanged;
            if (h2 != null) h2(it.Key);
        }

        /// <summary>顶部分段控件（软件 | 网站）矩形：置于顶栏行2（工具行）垂直居中。</summary>
        private Rectangle SegRect
        {
            get
            {
                int segY = Theme.TitleRowHeight + (Theme.ToolbarRowHeight - SegHeight) / 2;
                return new Rectangle(12, segY, ClientSize.Width - 24, SegHeight);
            }
        }

        /// <summary>分类列表可视区上边界（顶部分段控件之下）。</summary>
        private int ListTop
        {
            get { return HeaderHeight; }
        }

        /// <summary>分类列表可视区下边界（底部「+ 新建分类」按钮之上）。</summary>
        private int ListBottom
        {
            get { return ClientSize.Height - FooterHeight; }
        }

        /// <summary>中间滚动区上边界（紧随顶部固定区块）。</summary>
        private int ScrollTop
        {
            get { return ListTop + ListPad + TopCount * ItemHeight; }
        }

        /// <summary>中间滚动区下边界（底部固定区块的上沿）。</summary>
        private int ScrollBottom
        {
            get { return ListBottom - ListPad - BottomCount * ItemHeight; }
        }

        /// <summary>条目顶端 Y：顶部/底部固定区块不随滚动位移，中间区块随 scrollOffset 位移。</summary>
        private int ItemTop(int index)
        {
            if (index < TopCount) return ListTop + ListPad + index * ItemHeight;
            if (hasScrollBlock && index >= scrollStart && index <= scrollEnd)
                return ScrollTop + (index - scrollStart) * ItemHeight - scrollOffset;
            return ScrollBottom + (index - scrollEnd - 1) * ItemHeight;
        }

        /// <summary>按 Y 坐标命中条目下标（顶部/底部固定区块与中间滚动区块分别换算）；未命中返回 -1。</summary>
        private int IndexAt(int y)
        {
            if (y < ListTop || y >= ListBottom) return -1;
            // 顶部固定区块
            if (y < ScrollTop)
            {
                int idx = (y - ListTop - ListPad) / ItemHeight;
                return idx >= 0 ? idx : -1;
            }
            // 底部固定区块
            if (hasScrollBlock && y >= ScrollBottom)
            {
                int idx = scrollEnd + 1 + (y - ScrollBottom) / ItemHeight;
                return (idx > scrollEnd && idx < items.Count) ? idx : -1;
            }
            // 中间滚动区块
            if (!hasScrollBlock) return -1;
            int adj = y - ScrollTop + scrollOffset;
            if (adj < 0) return -1;
            int i = scrollStart + adj / ItemHeight;
            return i <= scrollEnd ? i : -1;
        }

        // ===== 中间区块滚动 =====

        /// <summary>中间滚动区可视高度。</summary>
        private int ScrollViewportHeight
        {
            get { int h = ScrollBottom - ScrollTop; return h > 0 ? h : 0; }
        }

        /// <summary>中间滚动区内容高度。</summary>
        private int ScrollContentHeight
        {
            get { return hasScrollBlock ? (scrollEnd - scrollStart + 1) * ItemHeight : 0; }
        }

        /// <summary>最大滚动偏移；中间区块不超出可视区时为 0（不显示滚动条）。</summary>
        private int MaxScroll
        {
            get { int m = ScrollContentHeight - ScrollViewportHeight; return m > 0 ? m : 0; }
        }

        /// <summary>滚动条轨道矩形（贴中间滚动区右边缘，位于条目右侧留白内）。</summary>
        private Rectangle ScrollTrackRect()
        {
            return new Rectangle(ClientSize.Width - ScrollBarW - 3, ScrollTop, ScrollBarW, ScrollViewportHeight);
        }

        /// <summary>滚动条命中区矩形（比可视条更宽，便于鼠标抓取；绘制仍用细条）。</summary>
        private Rectangle ScrollHitRect()
        {
            return new Rectangle(ClientSize.Width - ScrollHitW, ScrollTop, ScrollHitW, ScrollViewportHeight);
        }

        /// <summary>滚动条滑块矩形；无需滚动时为空。</summary>
        private Rectangle ScrollThumbRect()
        {
            if (MaxScroll <= 0) return Rectangle.Empty;
            Rectangle t = ScrollTrackRect();
            int thumbH = (int)((long)t.Height * ScrollViewportHeight / ScrollContentHeight);
            if (thumbH < 30) thumbH = 30;
            if (thumbH > t.Height) thumbH = t.Height;
            int travel = t.Height - thumbH;
            int y = t.Y + (travel <= 0 ? 0 : (int)((long)travel * scrollOffset / MaxScroll));
            return new Rectangle(t.X, y, t.Width, thumbH);
        }

        /// <summary>夹紧滚动偏移（条目数或尺寸变化后调用），越界时复位并重绘。</summary>
        private void ClampScroll()
        {
            int m = MaxScroll;
            int v = scrollOffset;
            if (v < 0) v = 0;
            if (v > m) v = m;
            if (v != scrollOffset)
            {
                scrollOffset = v;
                SyncHoverToCursor();
            }
        }

        private void SetScroll(int value)
        {
            int m = MaxScroll;
            if (value < 0) value = 0;
            if (value > m) value = m;
            if (value == scrollOffset) return;
            scrollOffset = value;
            SyncHoverToCursor();   // 滚动不触发 MouseMove，需按当前光标重算悬停项
            Invalidate();
        }

        /// <summary>按当前光标位置重算悬停条目（滚动后调用，避免高亮停留在错误条目）。</summary>
        private void SyncHoverToCursor()
        {
            Point p = PointToClient(Cursor.Position);
            int idx = ClientRectangle.Contains(p) ? IndexAt(p.Y) : -1;
            if (idx != hoverIndex)
            {
                hoverIndex = idx;
                Cursor = (idx >= 0 && idx < items.Count && !items[idx].Separator) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        /// <summary>鼠标滚轮滚动分类列表（由窗体消息过滤器按光标位置路由到此）。delta 为 WM_MOUSEWHEEL 增量。</summary>
        public void ScrollByWheel(int delta)
        {
            if (delta == 0 || MaxScroll <= 0) return;
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines <= 0) lines = 3;
            SetScroll(scrollOffset - (delta / 120) * ItemHeight * lines);
        }

        /// <summary>中间区块（可排序）起始下标，无则 -1。</summary>
        private int OrderableStart()
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i].Orderable) return i;
            return -1;
        }

        /// <summary>中间区块（可排序）结束下标（含），无则 -1。</summary>
        private int OrderableEnd()
        {
            for (int i = items.Count - 1; i >= 0; i--)
                if (items[i].Orderable) return i;
            return -1;
        }

        /// <summary>按鼠标 Y 计算插入缝隙（限定在可排序区块内）。</summary>
        private int DropGap(int y)
        {
            int s = OrderableStart(), e = OrderableEnd();
            if (s < 0) return -1;
            if (dragFrom < s || dragFrom > e) return -1;
            int fromGroup = items[dragFrom].OrderGroup;
            int groupStart = dragFrom;
            int groupEnd = dragFrom;
            while (groupStart > s && items[groupStart - 1].Orderable && items[groupStart - 1].OrderGroup == fromGroup) groupStart--;
            while (groupEnd < e && items[groupEnd + 1].Orderable && items[groupEnd + 1].OrderGroup == fromGroup) groupEnd++;
            int gap = groupStart;
            for (int i = groupStart; i <= groupEnd; i++)
                if (y >= ItemTop(i) + ItemHeight / 2) gap = i + 1;
            return gap;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            suppressClick = false;
            if (e.Button != MouseButtons.Left) return;

            // 滚动条：命中滑块纵向范围则抓取拖动；否则滑块跳到点击处并进入拖动
            if (MaxScroll > 0 && ScrollHitRect().Contains(e.Location))
            {
                Rectangle track = ScrollTrackRect();
                Rectangle thumb = ScrollThumbRect();
                scrollDragging = true;
                if (e.Y >= thumb.Y && e.Y <= thumb.Bottom)
                {
                    scrollGrabDy = e.Y - thumb.Y;
                }
                else
                {
                    scrollGrabDy = thumb.Height / 2;
                    int travel = track.Height - thumb.Height;
                    if (travel > 0)
                        SetScroll((int)((long)(e.Y - scrollGrabDy - track.Y) * MaxScroll / travel));
                }
                Cursor = Cursors.Hand;
                return;
            }

            if (e.Y < HeaderHeight || e.Y >= ClientSize.Height - FooterHeight) return;
            int idx = IndexAt(e.Y);
            if (idx < 0 || idx >= items.Count || !items[idx].Orderable) return;
            dragPending = true;
            dragging = false;
            dragFrom = idx;
            dragGap = -1;
            dragStart = e.Location;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (scrollDragging)
            {
                scrollDragging = false;
                suppressClick = true;   // 抑制释放时误触发分类选中
                Invalidate();
                return;
            }
            bool wasDrag = dragging && dragFrom >= 0 && dragGap >= 0;
            if (wasDrag)
            {
                int to = dragGap > dragFrom ? dragGap - 1 : dragGap;
                if (to != dragFrom)
                {
                    Action<int, int> h = ReorderRequested;
                    if (h != null) h(dragFrom, to);
                }
                suppressClick = true;
            }
            dragPending = false;
            dragging = false;
            dragFrom = -1;
            dragGap = -1;
            Cursor = Cursors.Default;
            if (wasDrag) Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ClampScroll();
        }

        /// <summary>是否为空白区（非分段头/非条目/非底部按钮/非滚动条），供窗体拖动判定。</summary>
        public bool IsBlankArea(Point p)
        {
            // 顶栏区域内：分段控件不可拖动，其余空白（行1标题区等）可拖动窗体
            if (p.Y < HeaderHeight) return !SegRect.Contains(p);
            if (p.Y >= ClientSize.Height - FooterHeight) return false;
            // 滚动条区域不参与窗体拖动，避免与滚动条拖动冲突
            if (MaxScroll > 0 && ScrollHitRect().Contains(p)) return false;
            int idx = IndexAt(p.Y);
            if (idx < 0) return true;
            return items[idx].Separator;
        }

        // ===== 外部拖动（主内容区条目拖到侧栏分类） =====

        /// <summary>按屏幕坐标返回命中的可投放分类 key；未命中（非分类/分隔/只读/常用）返回 null。</summary>
        public string DropCategoryAtScreen(Point screen)
        {
            Point p = PointToClient(screen);
            if (p.X < 0 || p.X > ClientSize.Width) return null;
            int idx = IndexAt(p.Y);
            if (idx < 0 || idx >= items.Count) return null;
            SidebarItem it = items[idx];
            return it.DropTarget ? it.Key : null;
        }

        /// <summary>设置外部拖动时的高亮目标分类（传 null/空清除）。</summary>
        public void SetExternalDropHighlight(string categoryKey)
        {
            int idx = -1;
            if (!string.IsNullOrEmpty(categoryKey))
            {
                for (int i = 0; i < items.Count; i++)
                    if (items[i].Key == categoryKey && items[i].DropTarget) { idx = i; break; }
            }
            if (idx != externalDragIndex) { externalDragIndex = idx; Invalidate(); }
        }

        // ===== 拖放（文件/文件夹拖入「快捷中心」） =====

        protected override void OnDragEnter(DragEventArgs e)
        {
            base.OnDragEnter(e);
            UpdateDragTarget(e);
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            UpdateDragTarget(e);
        }

        protected override void OnDragLeave(EventArgs e)
        {
            dragIndex = -1;
            Invalidate();
            base.OnDragLeave(e);
        }

        protected override void OnDragDrop(DragEventArgs e)
        {
            base.OnDragDrop(e);
            int idx = IndexAt(PointToClient(new Point(e.X, e.Y)).Y);
            dragIndex = -1;
            Invalidate();
            if (idx < 0 || idx >= items.Count || !items[idx].DropTarget) return;
            string[] files = e.Data == null ? null : e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            Action<string, string[]> h = FilesDropped;
            if (h != null) h(items[idx].Key, files);
        }

        /// <summary>计算拖放目标行：仅 DropTarget 分类（可编辑分类）可接收文件/文件夹，并给出拖放光标效果。</summary>
        private void UpdateDragTarget(DragEventArgs e)
        {
            int idx = IndexAt(PointToClient(new Point(e.X, e.Y)).Y);
            bool accept = idx >= 0 && idx < items.Count && items[idx].DropTarget
                && e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop);
            if (dragIndex != idx) { dragIndex = idx; Invalidate(); }
            e.Effect = accept ? DragDropEffects.Copy : DragDropEffects.None;
        }
    }
}
