using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace AppManager.Controls
{
    /// <summary>
    /// 应用内图片查看器：双击照片后替换列表区域展示大图（窗口其余部分不变）。
    /// 滚轮以光标为中心缩放、拖拽平移、双击在「适应窗口/实际大小」间切换；
    /// 底部按钮或 ←/→ 切换上一张/下一张，Esc/返回按钮退出；顶栏显示文件名与序号。
    /// 整体深色底 + 白色图形元素，与系统照片应用的大图浏览体验一致。
    /// </summary>
    public class ImageViewerControl : Control
    {
        /// <summary>请求退出查看器（Esc / 返回按钮），由主窗体恢复列表。</summary>
        public event Action CloseRequested;

        // 浏览状态
        private List<string> paths = new List<string>();
        private int index;
        private Image image;            // 当前大图；切换/退出必须 Dispose，否则文件句柄不释放
        private bool loadFailed;        // 当前图片解码失败（展示占位文案）

        // 缩放/平移：DisplayScale = fitScale * zoomFactor，zoomFactor=1 表示「适应窗口」
        private float fitScale = 1F;
        private float zoomFactor = 1F;
        private PointF pan;
        private bool panning;
        private Point panStart;
        private PointF panOrigin;

        // 界面按钮悬停/按下态：-1 无；0 上一张 1 缩小 2 比例 3 放大 4 下一张 5 返回
        private int hover = -1;
        private int press = -1;

        // 界面按钮几何：OnPaint 布局后缓存，命中测试与绘制共用
        private Rectangle backRect;
        private Rectangle nameRect;
        private Rectangle countRect;
        private Rectangle pillRect;
        private Rectangle prevRect, zoomOutRect, zoomLabelRect, zoomInRect, nextRect;

        private static readonly Color Bg = Color.FromArgb(20, 20, 22);
        private static readonly Color TextWhite = Color.White;
        private static readonly Color TextDim = Color.FromArgb(160, 255, 255, 255);
        private const int TopBarH = 44;
        private const int PillBtnW = 44;
        private const int PillH = 40;

        public ImageViewerControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Bg;
            Cursor = Cursors.Default;
            TabStop = true;
            Visible = false;
        }

        /// <summary>当前图片展示倍率（相对实际像素）。</summary>
        private float DisplayScale { get { return fitScale * zoomFactor; } }

        /// <summary>图片绘制区：顶栏以下、底部按钮以上的全部空间。</summary>
        private Rectangle ImageArea
        {
            get { return new Rectangle(0, TopBarH, Width, Math.Max(0, Height - TopBarH - PillH - 24)); }
        }

        /// <summary>打开一组图片并定位到指定下标；外部负责显隐切换。</summary>
        public void Open(List<string> imagePaths, int startIndex)
        {
            paths = imagePaths ?? new List<string>();
            if (paths.Count == 0) { RequestClose(); return; }
            index = Math.Min(Math.Max(startIndex, 0), paths.Count - 1);
            Visible = true;
            Focus();
            LoadCurrent();
        }

        /// <summary>请求关闭：统一走事件，由主窗体恢复列表布局。</summary>
        private void RequestClose()
        {
            Action h = CloseRequested;
            if (h != null) h();
        }

        // ===== 图片加载 =====

        private void LoadCurrent()
        {
            ReleaseImage();
            loadFailed = false;
            string path = paths[index];
            try
            {
                if (!File.Exists(path)) loadFailed = true;
                else image = Image.FromFile(path);
            }
            catch (Exception ex)
            {
                DebugWrite("图片加载失败 " + path + ": " + ex.Message);
                loadFailed = true;
            }
            // 换图后缩放/平移复位，回到「适应窗口」
            zoomFactor = 1F;
            pan = PointF.Empty;
            RecomputeFit();
            Invalidate();
        }

        private void ReleaseImage()
        {
            if (image != null) { image.Dispose(); image = null; }
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private static void DebugWrite(string msg)
        {
            System.Diagnostics.Debug.WriteLine(msg);
        }

        // ===== 缩放与平移 =====

        /// <summary>按控件尺寸重算「适应窗口」基准倍率。</summary>
        private void RecomputeFit()
        {
            if (image == null) { fitScale = 1F; return; }
            Rectangle area = ImageArea;
            if (area.Width <= 0 || area.Height <= 0) { fitScale = 1F; return; }
            fitScale = Math.Min(area.Width / (float)image.Width, area.Height / (float)image.Height);
        }

        /// <summary>平移钳制：图片小于视口时保持居中，大于视口时不拖出空白超过半屏。</summary>
        private void ClampPan()
        {
            if (image == null) { pan = PointF.Empty; return; }
            Rectangle area = ImageArea;
            float w = image.Width * DisplayScale;
            float h = image.Height * DisplayScale;
            pan.X = w <= area.Width ? 0F : Clamp(pan.X, area.Width - w, 0F);
            pan.Y = h <= area.Height ? 0F : Clamp(pan.Y, area.Height - h, 0F);
        }

        private static float Clamp(float v, float min, float max)
        {
            return v < min ? min : (v > max ? max : v);
        }

        /// <summary>图片在控件上的目标绘制矩形（居中 + 平移偏移）。</summary>
        private RectangleF ImageDestRect()
        {
            Rectangle area = ImageArea;
            float s = DisplayScale;
            float w = image.Width * s;
            float h = image.Height * s;
            float x = area.X + (area.Width - w) / 2F + pan.X;
            float y = area.Y + (area.Height - h) / 2F + pan.Y;
            return new RectangleF(x, y, w, h);
        }

        /// <summary>滚轮缩放：以光标为不动点推拉图片。</summary>
        private void ZoomAt(Point pt, int steps)
        {
            if (image == null || loadFailed) return;
            float oldS = DisplayScale;
            zoomFactor = Clamp(zoomFactor * (float)Math.Pow(1.15, steps), 0.05F, 40F);
            float newS = DisplayScale;
            if (Math.Abs(newS - oldS) < 0.0001F) return;
            Rectangle area = ImageArea;
            RectangleF oldRect = ImageDestRect();
            // 光标处图像坐标在新倍率下保持不动
            float ix = (pt.X - oldRect.X) / oldS;
            float iy = (pt.Y - oldRect.Y) / oldS;
            pan.X = pt.X - (area.X + (area.Width - image.Width * newS) / 2F) - ix * newS;
            pan.Y = pt.Y - (area.Y + (area.Height - image.Height * newS) / 2F) - iy * newS;
            ClampPan();
            Invalidate();
        }

        /// <summary>键盘/按钮缩放：以视口中心为不动点。</summary>
        private void ZoomCenter(int steps)
        {
            Rectangle area = ImageArea;
            ZoomAt(new Point(area.X + area.Width / 2, area.Y + area.Height / 2), steps);
        }

        /// <summary>双击/比例按钮：在「适应窗口」与「实际大小」间切换。</summary>
        private void ToggleFit()
        {
            if (image == null || loadFailed) return;
            if (zoomFactor > 1.01F || zoomFactor < 0.99F)
            {
                zoomFactor = 1F;
                pan = PointF.Empty;
            }
            else if (fitScale > 0.0001F)
            {
                zoomFactor = 1F / fitScale;   // 100% 实际像素
            }
            ClampPan();
            Invalidate();
        }

        // ===== 切换 =====

        private void Prev()
        {
            if (paths.Count < 2) return;
            index = (index - 1 + paths.Count) % paths.Count;
            LoadCurrent();
        }

        private void Next()
        {
            if (paths.Count < 2) return;
            index = (index + 1) % paths.Count;
            LoadCurrent();
        }

        // ===== 命中测试 =====

        /// <summary>界面按钮命中：0 上一张 1 缩小 2 比例 3 放大 4 下一张 5 返回，未命中 -1。</summary>
        private int HitButton(Point p)
        {
            if (backRect.Contains(p)) return 5;
            if (prevRect.Contains(p)) return 0;
            if (zoomOutRect.Contains(p)) return 1;
            if (zoomLabelRect.Contains(p)) return 2;
            if (zoomInRect.Contains(p)) return 3;
            if (nextRect.Contains(p)) return 4;
            return -1;
        }

        private void RunButton(int btn)
        {
            switch (btn)
            {
                case 0: Prev(); break;
                case 1: ZoomCenter(-1); break;
                case 2: ToggleFit(); break;
                case 3: ZoomCenter(1); break;
                case 4: Next(); break;
                case 5: RequestClose(); break;
            }
        }

        // ===== 交互 =====

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;
            int btn = HitButton(e.Location);
            // 捕获鼠标：拖出控件外松开也能正常结束平移/按下态
            Capture = true;
            if (btn >= 0) { press = btn; Invalidate(); return; }
            if (image != null && !loadFailed)
            {
                panning = true;
                panStart = e.Location;
                panOrigin = pan;
                Cursor = Cursors.SizeAll;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (panning)
            {
                pan.X = panOrigin.X + (e.X - panStart.X);
                pan.Y = panOrigin.Y + (e.Y - panStart.Y);
                ClampPan();
                Invalidate();
                return;
            }
            int h = HitButton(e.Location);
            if (h != hover) { hover = h; Invalidate(); }
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Capture = false;
            if (panning)
            {
                panning = false;
                Cursor = Cursors.Default;
                return;
            }
            if (e.Button == MouseButtons.Left && press >= 0)
            {
                int btn = press;
                press = -1;
                Invalidate();
                if (HitButton(e.Location) == btn) RunButton(btn);
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover >= 0) { hover = -1; Invalidate(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int steps = e.Delta / SystemInformation.MouseWheelScrollDelta;
            if (steps != 0) ZoomAt(e.Location, steps);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (HitButton(e.Location) < 0) ToggleFit();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Escape: RequestClose(); return true;
                case Keys.Left: Prev(); return true;
                case Keys.Right: Next(); return true;
                case Keys.Add:
                case Keys.Oemplus: ZoomCenter(1); return true;
                case Keys.Subtract:
                case Keys.OemMinus: ZoomCenter(-1); return true;
                case Keys.D0:
                case Keys.NumPad0: ToggleFit(); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            RecomputeFit();
            ClampPan();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ReleaseImage();
            base.Dispose(disposing);
        }

        // ===== 绘制 =====

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Bg);

            LayoutChrome();
            DrawImage(g);
            DrawTopBar(g);
            DrawPill(g);
        }

        private void LayoutChrome()
        {
            // 顶栏：返回在左，文件名居中，序号在右
            backRect = new Rectangle(8, 6, 76, TopBarH - 12);
            countRect = new Rectangle(Width - 96, 6, 88, TopBarH - 12);
            nameRect = new Rectangle(backRect.Right + 8, 0,
                Math.Max(0, countRect.Left - backRect.Right - 16), TopBarH);

            // 底部居中悬浮操作条
            int pillW = PillBtnW * 5;
            pillRect = new Rectangle((Width - pillW) / 2, Height - PillH - 16, pillW, PillH);
            prevRect = new Rectangle(pillRect.X, pillRect.Y, PillBtnW, PillH);
            zoomOutRect = new Rectangle(prevRect.Right, pillRect.Y, PillBtnW, PillH);
            zoomLabelRect = new Rectangle(zoomOutRect.Right, pillRect.Y, PillBtnW, PillH);
            zoomInRect = new Rectangle(zoomLabelRect.Right, pillRect.Y, PillBtnW, PillH);
            nextRect = new Rectangle(zoomInRect.Right, pillRect.Y, PillBtnW, PillH);
        }

        private void DrawImage(Graphics g)
        {
            if (image == null)
            {
                if (loadFailed)
                {
                    using (SolidBrush b = new SolidBrush(TextDim))
                        Draw.DrawStringCenter(g, "无法加载此图片", Theme.FontOf(10F), b, ImageArea);
                }
                return;
            }
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(image, ImageDestRect());
            g.InterpolationMode = InterpolationMode.Default;
            g.PixelOffsetMode = PixelOffsetMode.Default;
        }

        private void DrawTopBar(Graphics g)
        {
            // 返回：chevron + 文案，悬停/按下给浅色圆角底
            DrawButtonBg(g, 5, backRect);
            float cx = backRect.X + 16;
            float cy = backRect.Y + backRect.Height / 2F;
            using (Pen pen = new Pen(TextWhite, 1.8F))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, cx + 3, cy - 5, cx - 3, cy);
                g.DrawLine(pen, cx - 3, cy, cx + 3, cy + 5);
            }
            Rectangle textR = new Rectangle(backRect.X + 24, backRect.Y, backRect.Width - 24, backRect.Height);
            TextRenderer.DrawText(g, "返回", Theme.FontOf(9.5F), textR, TextWhite,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            // 文件名（超长省略）与「当前/总数」
            string name = paths.Count > 0 && index < paths.Count ? Path.GetFileName(paths[index]) : string.Empty;
            TextRenderer.DrawText(g, name, Theme.FontOf(9.5F), nameRect, TextWhite,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            string count = (index + 1) + " / " + paths.Count;
            TextRenderer.DrawText(g, count, Theme.FontOf(9F), countRect, TextDim,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        private void DrawPill(Graphics g)
        {
            // 深色悬浮底：与背景拉开一点层次，圆角与 iOS 工具条一致
            using (SolidBrush b = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
                Draw.FillRoundedRect(g, b, pillRect, PillH / 2);

            bool single = paths.Count < 2;
            DrawPillBtn(g, prevRect, 0, single);
            DrawPillBtn(g, zoomOutRect, 1, false);
            DrawZoomLabel(g);
            DrawPillBtn(g, zoomInRect, 3, false);
            DrawPillBtn(g, nextRect, 4, single);
        }

        /// <summary>悬浮条图标按钮：chevron 前后切换、± 缩放；single 时前后切换弱化。</summary>
        private void DrawPillBtn(Graphics g, Rectangle r, int id, bool dim)
        {
            DrawButtonBg(g, id, r);
            Color c = dim ? Color.FromArgb(90, 255, 255, 255) : TextWhite;
            float cx = r.X + r.Width / 2F;
            float cy = r.Y + r.Height / 2F;
            using (Pen pen = new Pen(c, 1.8F))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                switch (id)
                {
                    case 0:   // ‹
                        g.DrawLine(pen, cx + 3, cy - 6, cx - 3, cy);
                        g.DrawLine(pen, cx - 3, cy, cx + 3, cy + 6);
                        break;
                    case 1:   // −
                        g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
                        break;
                    case 3:   // ＋
                        g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
                        g.DrawLine(pen, cx, cy - 5, cx, cy + 5);
                        break;
                    case 4:   // ›
                        g.DrawLine(pen, cx - 3, cy - 6, cx + 3, cy);
                        g.DrawLine(pen, cx + 3, cy, cx - 3, cy + 6);
                        break;
                }
            }
        }

        /// <summary>当前缩放比例文本，点击在「适应窗口/实际大小」间切换。</summary>
        private void DrawZoomLabel(Graphics g)
        {
            DrawButtonBg(g, 2, zoomLabelRect);
            string text = Math.Round(DisplayScale * 100F) + "%";
            TextRenderer.DrawText(g, text, Theme.FontOf(9F), zoomLabelRect, TextWhite,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        /// <summary>按钮悬停/按下底色：仅叠加白色透明度，无描边（与标题栏按钮一致）。</summary>
        private void DrawButtonBg(Graphics g, int id, Rectangle r)
        {
            if (press != id && hover != id) return;
            Color fill = press == id ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(34, 255, 255, 255);
            using (SolidBrush b = new SolidBrush(fill))
                Draw.FillRoundedRect(g, b, r, 10);
        }
    }
}
