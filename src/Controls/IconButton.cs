using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using AppManager;

namespace AppManager.Controls
{
    /// <summary>标题栏图标类型。</summary>
    public enum IconKind
    {
        None,
        Settings,
        Minimize,
        Maximize,
        Restore,
        Close
    }

    /// <summary>
    /// 标题栏图标按钮：无边框、透明底，悬停/按下时显示浅色圆角底，与标题栏融为一体。
    /// 设置图标取自系统 Segoe 图标字体并转为路径加粗绘制；最小化/最大化/还原/关闭为自绘图形。
    /// 齿轮墨迹归一化到统一外形尺寸后居中，四个图标共用统一线宽，大小与笔画保持一致。
    /// </summary>
    public class IconButton : Control
    {
        private const string SettingsGlyph = "\uE713";
        private const float StrokeWidth = 1.8F;   // 线宽：贴近 Win11 标题栏图标的纤细笔画
        private const float IconBox = 12F;        // 自绘图标外形尺寸（像素）
        private const float IconRadius = 2F;      // 矩形图标圆角半径
        private const float GlyphEmSize = 14F;    // 齿轮字形按像素计算的 em 尺寸
        private const float GlyphStroke = 0.3F;   // 齿轮字形描边加粗量，抑制字形本身的单薄感
        private const float GearBox = 15F;      // 齿轮墨迹归一化尺寸：略小于线条图标，抵消密集填充的视觉放大

        private static readonly Font GlyphFont = CreateGlyphFont();
        /// <summary>图标色：比正文略浅的深灰，弱化标题栏按钮的存在感。</summary>
        private static readonly Color GlyphColor = Color.FromArgb(82, 82, 86);

        private IconKind _kind = IconKind.None;
        private bool _hover;
        private bool _pressed;

        public IconButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint
                | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Size = new Size(34, 30);
        }

        /// <summary>图标类型，切换类型不会改变按钮尺寸。</summary>
        public IconKind Kind
        {
            get { return _kind; }
            set { _kind = value; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 悬停/按下：仅显示浅色圆角底，不画描边（Windows 11 标题栏风格）
            if (_hover || _pressed)
            {
                Rectangle r = ClientRectangle;
                r.Inflate(-4, -2);
                Color fill = _pressed ? Color.FromArgb(44, 0, 0, 0) : Color.FromArgb(22, 0, 0, 0);
                using (SolidBrush b = new SolidBrush(fill))
                    Draw.FillRoundedRect(g, b, r, 6);
            }
            if (_kind == IconKind.None) return;

            if (_kind == IconKind.Settings)
            {
                DrawSettings(g);
                return;
            }

            float cx = Width / 2F;
            float cy = Height / 2F;
            using (Pen pen = new Pen(GlyphColor, StrokeWidth))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                switch (_kind)
                {
                    case IconKind.Minimize:
                        g.DrawLine(pen, cx - IconBox / 2F, cy, cx + IconBox / 2F, cy);
                        break;
                    case IconKind.Maximize:
                        DrawRoundRect(g, pen, cx - IconBox / 2F, cy - IconBox / 2F, IconBox, IconBox);
                        break;
                    case IconKind.Restore:
                        DrawRestore(g, pen, cx, cy);
                        break;
                    case IconKind.Close:
                        float h = IconBox / 2F;
                        g.DrawLine(pen, cx - h, cy - h, cx + h, cy + h);
                        g.DrawLine(pen, cx + h, cy - h, cx - h, cy + h);
                        break;
                }
            }
        }

        /// <summary>
        /// 设置齿轮：把字形转为路径后「填充 + 细描边」，既保留齿轮造型，又让笔画粗细
        /// 与自绘窗口图标统一（纯字体填充的笔画偏细，与矢量线条视觉重量不一致）。
        /// 墨迹归一化到 GearBox 后居中：避免字体行盒留白偏移，外形不随字体/字号漂移。
        /// </summary>
        private void DrawSettings(Graphics g)
        {
            float cx = Width / 2F;
            float cy = Height / 2F;
            using (GraphicsPath path = new GraphicsPath())
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                path.AddString(SettingsGlyph, GlyphFont.FontFamily, (int)FontStyle.Regular,
                    GlyphEmSize, new PointF(0, 0), sf);
                RectangleF b = path.GetBounds();
                if (b.Width > 0F && b.Height > 0F)
                {
                    // 墨迹中心移到原点 → 等比缩放到 GearBox → 移到按钮中心：
                    // 齿轮外形不再随字体/字号漂移，与自绘图标的 13px 外形精确对齐
                    float scale = GearBox / Math.Max(b.Width, b.Height);
                    using (Matrix m = new Matrix())
                    {
                        m.Translate(-(b.X + b.Width / 2F), -(b.Y + b.Height / 2F));
                        m.Scale(scale, scale);
                        m.Translate(cx, cy);
                        path.Transform(m);
                    }
                }
                using (SolidBrush br = new SolidBrush(GlyphColor))
                    g.FillPath(br, path);
                using (Pen pen = new Pen(GlyphColor, GlyphStroke))
                {
                    pen.LineJoin = LineJoin.Round;
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawPath(pen, path);
                }
            }
        }

        /// <summary>还原图标：后层方框只画左上两条边，前层方框完整绘制。</summary>
        private void DrawRestore(Graphics g, Pen pen, float cx, float cy)
        {
            const float size = IconBox - 3F;
            const float offset = 3F;
            float backLeft = cx - size / 2F - offset / 2F;
            float backTop = cy - size / 2F - offset / 2F;
            float frontLeft = cx - size / 2F + offset / 2F;
            float frontTop = cy - size / 2F + offset / 2F;

            using (GraphicsPath path = new GraphicsPath())
            {
                float r = IconRadius;
                // 后层：左下角向上到左上圆角，再向右延伸
                path.AddLine(backLeft, backTop + size, backLeft, backTop + r);
                path.AddArc(backLeft, backTop, r * 2, r * 2, 180, 90);
                path.AddLine(backLeft + r, backTop, backLeft + size, backTop);
                g.DrawPath(pen, path);
            }
            DrawRoundRect(g, pen, frontLeft, frontTop, size, size);
        }

        private void DrawRoundRect(Graphics g, Pen pen, float x, float y, float w, float h)
        {
            float r = IconRadius * 2;
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddArc(x, y, r, r, 180, 90);
                path.AddArc(x + w - r, y, r, r, 270, 90);
                path.AddArc(x + w - r, y + h - r, r, r, 0, 90);
                path.AddArc(x, y + h - r, r, r, 90, 90);
                path.CloseFigure();
                g.DrawPath(pen, path);
            }
        }

        /// <summary>解析系统图标字体：Win11 优先 Fluent Icons，其次 MDL2，最后回退 UI 符号字体。</summary>
        private static Font CreateGlyphFont()
        {
            string[] candidates = { "Segoe Fluent Icons", "Segoe MDL2 Assets", "Segoe UI Symbol" };
            string name = null;
            try
            {
                using (InstalledFontCollection col = new InstalledFontCollection())
                {
                    foreach (string candidate in candidates)
                    {
                        foreach (FontFamily f in col.Families)
                        {
                            if (string.Equals(f.Name, candidate, StringComparison.OrdinalIgnoreCase))
                            {
                                name = candidate;
                                break;
                            }
                        }
                        if (name != null) break;
                    }
                }
            }
            catch { }
            if (name == null) name = Theme.FontName;
            return new Font(name, GlyphEmSize, FontStyle.Regular, GraphicsUnit.Pixel);
        }
    }
}
