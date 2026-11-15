using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>
    /// 自绘行的列表框换上细滚动条：去掉系统滚动条，在右侧放一条自绘的滑块（悬停加深、可拖动、点轨道跳转）。
    /// 滚动仍由列表框自己完成（按行），这里只负责显示位置和把拖动换算成首行序号；
    /// 需要滚动时行的绘制区域在右侧让出滑块的宽度。
    /// </summary>
    internal class ThinScrollListBox : ListBox
    {
        private const int WS_VSCROLL = 0x00200000;
        private const int WS_CLIPCHILDREN = 0x02000000;
        private const int WM_SIZE = 0x0005;
        private const int WM_SETREDRAW = 0x000B;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_TIMER = 0x0113;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_MOUSEWHEEL = 0x020A;
        private const int WM_VSCROLL = 0x0115;
        // 会改变行数、首行或行高的列表框消息；查询类消息（取行矩形、取首行等）不必重算
        private static readonly int[] ChangingMessages =
        {
            0x0180, 0x0181, 0x0182, 0x0184, 0x0185, 0x0186,   // 增、插、删、清空、多选、单选
            0x0197, 0x01A0, 0x01A7, 0x01A8                      // 设首行、设行高、设行数、预分配
        };
        private const int StripWidth = 10;
        private const int ThumbWidth = 4;

        private readonly Strip strip;
        private int wheelRemainder;     // 不足一行的滚轮累计量（单位：delta × 行）
        private bool syncing;
        // 内容是否超出、滑块条是否该显示。不用 strip.Visible 判断：父窗口还没显示时它总是 false
        private bool overflowing;

        internal ThinScrollListBox()
        {
            strip = new Strip(this);
            strip.Visible = false;
            Controls.Add(strip);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style &= ~WS_VSCROLL;       // 不要系统滚动条
                cp.Style |= WS_CLIPCHILDREN;   // 滚动时不把滑块所在的那一条像素一起搬走
                return cp;
            }
        }

        private int Scale(int value)
        {
            return DpiHelper.Scale(value, DpiHelper.ForControl(this));
        }

        /// <summary>行的绘制区域（客户区坐标）：需要滚动时右侧让出滑块的宽度。</summary>
        internal Rectangle RowBounds(int index)
        {
            Rectangle r = GetItemRectangle(index);
            if (overflowing && r.Width > strip.Width) r.Width -= strip.Width;
            return r;
        }

        /// <summary>行内容可用的宽度。</summary>
        internal int RowWidth
        {
            get { return Math.Max(0, ClientSize.Width - (overflowing ? strip.Width : 0)); }
        }

        /// <summary>从首行起完整露出的行数（至少 1）。</summary>
        private int VisibleRows(int top)
        {
            int rows = 0;
            for (int i = top; i < Items.Count; i++)
            {
                if (GetItemRectangle(i).Bottom > ClientSize.Height) break;
                rows++;
            }
            return Math.Max(1, rows);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (overflowing && e.Bounds.Width > strip.Width)
            {
                Rectangle bounds = e.Bounds;
                bounds.Width -= strip.Width;
                e = new DrawItemEventArgs(e.Graphics, e.Font, bounds, e.Index, e.State, e.ForeColor, e.BackColor);
            }
            base.OnDrawItem(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            HandledMouseEventArgs handled = e as HandledMouseEventArgs;
            if (handled != null && handled.Handled) return;
            // 自己按行滚动并标记已处理：不依赖没有系统滚动条时列表框的默认滚轮行为，也避免滚两次
            ScrollRows(e.Delta);
            if (handled != null) handled.Handled = true;
        }

        /// <summary>按滚轮增量逐行滚动；指针停在右侧滑块上时滑块条也转到这里。</summary>
        private void ScrollRows(int delta)
        {
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines <= 0) lines = 3;
            wheelRemainder += delta * lines;
            int rows = wheelRemainder / 120;
            wheelRemainder -= rows * 120;
            if (rows != 0 && Items.Count > 0) TopIndex = Math.Max(0, Math.Min(Items.Count - 1, TopIndex - rows));
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // 没有系统滚动条的列表框不发滚动通知：在可能改变行数、首行或高度的消息处理完之后比对一次
            int msg = m.Msg;
            bool relevant = Array.IndexOf(ChangingMessages, msg) >= 0 || msg == WM_VSCROLL || msg == WM_SIZE || msg == WM_SETREDRAW || msg == WM_KEYDOWN
                || msg == WM_MOUSEWHEEL || msg == WM_LBUTTONDOWN || msg == WM_TIMER
                || (msg == WM_MOUSEMOVE && (MouseButtons & MouseButtons.Left) != 0);
            if (relevant) SyncStrip();
        }

        private void SyncStrip()
        {
            if (syncing || !IsHandleCreated || IsDisposed || Disposing || RecreatingHandle) return;
            syncing = true;
            try
            {
                int count = Items.Count;
                int top = count == 0 ? 0 : TopIndex;
                bool overflow = count > 0 && (top > 0 || GetItemRectangle(count - 1).Bottom > ClientSize.Height);
                int width = Scale(StripWidth);
                Rectangle bounds = new Rectangle(ClientSize.Width - width, 0, width, ClientSize.Height);
                if (strip.Bounds != bounds) strip.Bounds = bounds;
                if (overflowing != overflow)
                {
                    overflowing = overflow;
                    strip.Visible = overflow;
                    Invalidate();   // 行的可用宽度变了
                }
                if (overflow) strip.Show(top, count, VisibleRows(top));
            }
            finally { syncing = false; }
        }

        /// <summary>右侧的滑块条：只显示和拖动，不接收焦点。</summary>
        private sealed class Strip : Control
        {
            private readonly ThinScrollListBox owner;
            private int top, count = 1, visible = 1;
            private bool hot, dragging;
            private int grabDy;

            internal Strip(ThinScrollListBox owner)
            {
                this.owner = owner;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
                SetStyle(ControlStyles.Selectable, false);
                TabStop = false;
            }

            internal void Show(int top, int count, int visible)
            {
                if (top == this.top && count == this.count && visible == this.visible) return;
                this.top = top;
                this.count = count;
                this.visible = visible;
                Invalidate();
            }

            private int MaxTop { get { return Math.Max(1, count - visible); } }

            private Rectangle Track()
            {
                int w = owner.Scale(ThumbWidth);
                return new Rectangle(Width - owner.Scale(2) - w, owner.Scale(4), w, Math.Max(0, Height - owner.Scale(8)));
            }

            private Rectangle Thumb()
            {
                Rectangle t = Track();
                int h = (int)((long)t.Height * visible / Math.Max(1, count));
                if (h < owner.Scale(30)) h = owner.Scale(30);
                if (h > t.Height) h = t.Height;
                int travel = t.Height - h;
                int y = t.Y + (int)((long)travel * Math.Min(top, MaxTop) / MaxTop);
                return new Rectangle(t.X, y, t.Width, h);
            }

            private void DragTo(int y)
            {
                Rectangle t = Track();
                int travel = t.Height - Thumb().Height;
                if (travel <= 0 || owner.Items.Count == 0) return;
                long target = (long)(y - grabDy - t.Y) * MaxTop / travel;
                owner.TopIndex = (int)Math.Max(0, Math.Min(owner.Items.Count - 1, target));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                using (SolidBrush back = new SolidBrush(owner.BackColor)) g.FillRectangle(back, ClientRectangle);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush thumb = new SolidBrush(hot || dragging ? Theme.ScrollThumbHot : Theme.ScrollThumb))
                    Draw.FillRoundedRect(g, thumb, Thumb(), owner.Scale(ThumbWidth) / 2);
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                owner.ScrollRows(e.Delta);   // 指针在滑块上滚动滚轮：滚的是旁边的列表
                HandledMouseEventArgs handled = e as HandledMouseEventArgs;
                if (handled != null) handled.Handled = true;
            }

            protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                Rectangle thumb = Thumb();
                dragging = true;
                if (e.Y >= thumb.Y && e.Y <= thumb.Bottom) grabDy = e.Y - thumb.Y;
                else
                {
                    // 点在轨道空白处：滑块中心跳到点击处，并进入拖动
                    grabDy = thumb.Height / 2;
                    DragTo(e.Y);
                }
                Invalidate();
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (dragging) DragTo(e.Y);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (!dragging) return;
                dragging = false;
                Invalidate();
            }

            /// <summary>鼠标捕获丢失时收不到 MouseUp：在这里结束拖动。</summary>
            protected override void OnMouseCaptureChanged(EventArgs e)
            {
                base.OnMouseCaptureChanged(e);
                if (Capture || !dragging) return;
                dragging = false;
                Invalidate();
            }
        }
    }
}
