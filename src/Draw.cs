using System.Drawing;
using System.Drawing.Drawing2D;

namespace AppManager
{
    /// <summary>
    /// 自绘工具集：圆角矩形、柔和阴影、文本渲染等 GDI+ 原语。
    /// 集中提供绘制函数，保证全站 UI 风格一致。
    /// </summary>
    internal static class Draw
    {
        /// <summary>
        /// 构造圆角矩形路径；半径自动夹到短边一半，防止越界。
        /// </summary>
        public static GraphicsPath RoundedPath(Rectangle rect, int radius)
        {
            int d = radius * 2;
            if (d > rect.Width) { radius = rect.Width / 2; d = radius * 2; }
            if (d > rect.Height) { radius = rect.Height / 2; d = radius * 2; }
            if (radius < 1) radius = 1;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(rect.X, rect.Y, d, d, 180, 90);
            p.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            p.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            p.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>填充圆角矩形。</summary>
        public static void FillRoundedRect(Graphics g, Brush brush, Rectangle rect, int radius)
        {
            using (GraphicsPath p = RoundedPath(rect, radius))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(brush, p);
            }
        }

        /// <summary>描边圆角矩形。</summary>
        public static void DrawRoundedRect(Graphics g, Pen pen, Rectangle rect, int radius)
        {
            using (GraphicsPath p = RoundedPath(rect, radius))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawPath(pen, p);
            }
        }

        /// <summary>绘制柔和阴影（多层半透明圆角，由外向内加深）。</summary>
        public static void DrawShadow(Graphics g, Rectangle rect, int radius)
        {
            DrawShadow(g, rect, radius, 1f);
        }

        /// <summary>绘制柔和阴影；strength 为整体强度系数（0~1），iOS 质感用低强度。</summary>
        public static void DrawShadow(Graphics g, Rectangle rect, int radius, float strength)
        {
            int layers = 5;
            for (int i = layers; i > 0; i--)
            {
                int off = i;
                Rectangle r = Rectangle.Inflate(rect, off, off);
                if (r.Width <= 0 || r.Height <= 0) continue;
                int alpha = (int)((3 + (layers - i) * 2) * strength);
                if (alpha <= 0) continue;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0)))
                {
                    FillRoundedRect(g, b, r, radius + off);
                }
            }
        }

        /// <summary>居中绘制文本（带省略号截断，单行）。</summary>
        public static void DrawStringCenter(Graphics g, string text, Font font, Brush brush, Rectangle rect)
        {
            if (string.IsNullOrEmpty(text)) return;
            StringFormat sf = new StringFormat();
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            sf.Trimming = StringTrimming.EllipsisCharacter;
            sf.FormatFlags = StringFormatFlags.NoWrap;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.DrawString(text, font, brush, rect, sf);
        }

        /// <summary>左对齐垂直居中绘制文本（带省略号）。</summary>
        public static void DrawStringLeft(Graphics g, string text, Font font, Brush brush, Rectangle rect)
        {
            if (string.IsNullOrEmpty(text)) return;
            StringFormat sf = new StringFormat();
            sf.LineAlignment = StringAlignment.Center;
            sf.Trimming = StringTrimming.EllipsisCharacter;
            sf.FormatFlags = StringFormatFlags.NoWrap;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.DrawString(text, font, brush, rect, sf);
        }

        /// <summary>右对齐垂直居中绘制文本。</summary>
        public static void DrawStringRight(Graphics g, string text, Font font, Brush brush, Rectangle rect)
        {
            if (string.IsNullOrEmpty(text)) return;
            StringFormat sf = new StringFormat();
            sf.Alignment = StringAlignment.Far;
            sf.LineAlignment = StringAlignment.Center;
            sf.Trimming = StringTrimming.EllipsisCharacter;
            sf.FormatFlags = StringFormatFlags.NoWrap;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.DrawString(text, font, brush, rect, sf);
        }
    }
}
