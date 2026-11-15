using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>
    /// 自绘滚动列表的公共部分：按像素滚动、鼠标滚轮、细滚动条（悬停加深、可拖动、点轨道跳转）。
    /// 子类负责内容的排版、绘制和命中，排版后用 SetContentHeight 报告内容总高度。
    /// </summary>
    internal abstract class ScrollListBase : Control
    {
        private const int ScrollBarW = 4;
        private const int ScrollHitW = 12;

        private int contentHeight;
        private int scrollOffset;
        private int wheelRemainder;     // 不足一像素的滚轮累计量（单位：delta × 像素）
        private int scrollGrabDy;
        private bool scrollDragging;
        private bool scrollbarHot;

        protected ScrollListBase()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            TabStop = true;
        }

        protected int Dpi(int value)
        {
            return DpiHelper.Scale(value, DpiHelper.ForControl(this));
        }

        /// <summary>内容已向上滚过的像素数：内容坐标 = 客户区坐标 + ScrollOffset。</summary>
        protected int ScrollOffset { get { return scrollOffset; } }

        protected int MaxScroll { get { return Math.Max(0, contentHeight - Height); } }

        /// <summary>指针正在滚动条上或正在拖动滑块：此时子类不应再显示行悬停。</summary>
        protected bool OnScrollBar { get { return scrollDragging || scrollbarHot; } }

        protected void SetContentHeight(int height)
        {
            contentHeight = Math.Max(0, height);
            if (scrollOffset > MaxScroll) scrollOffset = MaxScroll;
        }

        /// <summary>滚动位置变化后调用。滚动不会产生鼠标移动消息，子类在这里按光标位置重算悬停。</summary>
        protected virtual void OnScrolled() { }

        protected void SetScroll(int value)
        {
            int max = MaxScroll;
            if (value < 0) value = 0;
            if (value > max) value = max;
            if (value == scrollOffset) return;
            scrollOffset = value;
            OnScrolled();
            Invalidate();
        }

        /// <summary>滚动到让内容坐标 [top, bottom) 这一段完整可见。</summary>
        protected void EnsureVisible(int top, int bottom)
        {
            if (top < scrollOffset) SetScroll(top);
            else if (bottom > scrollOffset + Height) SetScroll(bottom - Height);
        }

        /// <summary>鼠标滚轮滚动。delta 为 WM_MOUSEWHEEL 增量。</summary>
        internal void ScrollByWheel(int delta)
        {
            if (delta == 0 || MaxScroll <= 0) return;
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines <= 0) lines = 3;
            // 按像素换算并累计余数：精确式触控板每次 delta 小于 120，整除会得到 0 而完全滚不动
            wheelRemainder += delta * Dpi(32) * lines;
            int pixels = wheelRemainder / 120;
            wheelRemainder -= pixels * 120;
            if (pixels != 0) SetScroll(scrollOffset - pixels);
        }

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

        /// <summary>子类在 OnPaint 末尾调用：内容超出视口时画滚动条滑块。</summary>
        protected void PaintScrollBar(Graphics g)
        {
            if (MaxScroll <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush thumb = new SolidBrush(OnScrollBar ? Theme.ScrollThumbHot : Theme.ScrollThumb))
                Draw.FillRoundedRect(g, thumb, ScrollThumbRect(), Dpi(ScrollBarW) / 2);
        }

        private void DragTo(int y)
        {
            Rectangle track = ScrollTrackRect();
            int travel = track.Height - ScrollThumbRect().Height;
            if (travel > 0) SetScroll((int)((long)(y - scrollGrabDy - track.Y) * MaxScroll / travel));
        }

        /// <summary>左键按在滚动条上时开始拖动并返回 true，子类随即返回，不再当作点在内容上。</summary>
        protected bool ScrollMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || MaxScroll <= 0 || !ScrollHitRect().Contains(e.Location)) return false;
            Rectangle thumb = ScrollThumbRect();
            scrollDragging = true;
            if (e.Y >= thumb.Y && e.Y <= thumb.Bottom) scrollGrabDy = e.Y - thumb.Y;
            else
            {
                // 点在轨道空白处：滑块中心跳到点击处，并进入拖动
                scrollGrabDy = thumb.Height / 2;
                DragTo(e.Y);
            }
            Invalidate();
            return true;
        }

        /// <summary>指针在滚动条上（或正在拖动）时返回 true，子类随即清掉行悬停并返回。</summary>
        protected bool ScrollMouseMove(MouseEventArgs e)
        {
            if (scrollDragging)
            {
                DragTo(e.Y);
                return true;
            }
            bool hot = MaxScroll > 0 && ScrollHitRect().Contains(e.Location);
            if (hot != scrollbarHot)
            {
                scrollbarHot = hot;
                Invalidate();
            }
            return hot;
        }

        /// <summary>结束滑块拖动；本次松开属于滚动条时返回 true。</summary>
        protected bool ScrollMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !scrollDragging) return false;
            scrollDragging = false;
            Invalidate();
            return true;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (scrollbarHot)
            {
                scrollbarHot = false;
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        /// <summary>鼠标捕获丢失（弹出对话框、切换窗口等）时收不到 MouseUp：在这里结束滑块拖动。</summary>
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (Capture || !scrollDragging) return;
            scrollDragging = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollByWheel(e.Delta);
            // 标记已处理：否则默认窗口过程会把滚轮转给窗体，窗体再转回来造成滚两次
            HandledMouseEventArgs handled = e as HandledMouseEventArgs;
            if (handled != null) handled.Handled = true;
        }
    }
}
