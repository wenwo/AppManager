using System;
using System.Collections.Generic;
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
        // 共享 StringFormat（绘制参数固定，仅用于绘制调用；避免每行每帧重复创建/泄漏）
        private static readonly StringFormat FmtCenter = MakeFormat(StringAlignment.Center);
        private static readonly StringFormat FmtLeft = MakeFormat(StringAlignment.Near);
        private static readonly StringFormat FmtRight = MakeFormat(StringAlignment.Far);

        private static StringFormat MakeFormat(StringAlignment align)
        {
            StringFormat sf = new StringFormat();
            sf.Alignment = align;
            sf.LineAlignment = StringAlignment.Center;
            sf.Trimming = StringTrimming.EllipsisCharacter;
            sf.FormatFlags = StringFormatFlags.NoWrap;
            return sf;
        }
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

        /// <summary>绘制柔和阴影；strength 为整体强度系数（0~1），iOS 质感用低强度。
        /// 画刷按强度静态缓存、路径按尺寸静态缓存（网格卡片尺寸固定，命中率高），
        /// 避免每张卡片每帧创建 5 组 Brush/Path。</summary>
        public static void DrawShadow(Graphics g, Rectangle rect, int radius, float strength)
        {
            const int layers = 5;
            SolidBrush[] brushes = ShadowBrushes(strength);
            for (int i = layers; i > 0; i--)
            {
                int off = i;
                Rectangle r = Rectangle.Inflate(rect, off, off);
                if (r.Width <= 0 || r.Height <= 0) continue;
                SolidBrush b = brushes[i - 1];
                if (b == null) continue;
                GraphicsPath p = ShadowPath(r.Width, r.Height, radius + off);
                g.TranslateTransform(r.X, r.Y);
                g.SmoothingMode = SmoothingMode.AntiAlias;   // 与原实现一致：AA 状态留给后续绘制
                g.FillPath(b, p);
                g.TranslateTransform(-r.X, -r.Y);
            }
        }

        private static readonly object shadowLock = new object();
        private static readonly Dictionary<int, SolidBrush[]> shadowBrushes = new Dictionary<int, SolidBrush[]>();
        private static readonly Dictionary<string, GraphicsPath> shadowPaths = new Dictionary<string, GraphicsPath>(StringComparer.Ordinal);

        /// <summary>按强度取 5 层阴影画刷（强度量化为百分比做键）；alpha 为 0 的层为 null（调用方跳过）。</summary>
        private static SolidBrush[] ShadowBrushes(float strength)
        {
            int key = (int)Math.Round(strength * 100);
            lock (shadowLock)
            {
                SolidBrush[] arr;
                if (shadowBrushes.TryGetValue(key, out arr)) return arr;
                arr = new SolidBrush[5];
                for (int i = 5; i > 0; i--)
                {
                    int alpha = (int)((3 + (5 - i) * 2) * (key / 100f));
                    if (alpha > 0) arr[i - 1] = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
                }
                shadowBrushes[key] = arr;
                return arr;
            }
        }

        /// <summary>按尺寸取原点圆角路径（调用方平移到目标位置）；缓存有界，超限时清空重建。</summary>
        private static GraphicsPath ShadowPath(int w, int h, int radius)
        {
            string key = w + "x" + h + "r" + radius;
            lock (shadowLock)
            {
                GraphicsPath p;
                if (shadowPaths.TryGetValue(key, out p)) return p;
                if (shadowPaths.Count > 64) shadowPaths.Clear();   // 尺寸漂移（窗口宽度变化）时防无限增长
                p = RoundedPath(new Rectangle(0, 0, w, h), radius);
                shadowPaths[key] = p;
                return p;
            }
        }

        /// <summary>居中绘制文本（带省略号截断，单行）。</summary>
        public static void DrawStringCenter(Graphics g, string text, Font font, Brush brush, Rectangle rect)
        {
            if (string.IsNullOrEmpty(text)) return;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.DrawString(text, font, brush, rect, FmtCenter);
        }

        /// <summary>左对齐垂直居中绘制文本（带省略号）。</summary>
        public static void DrawStringLeft(Graphics g, string text, Font font, Brush brush, Rectangle rect)
        {
            if (string.IsNullOrEmpty(text)) return;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.DrawString(text, font, brush, rect, FmtLeft);
        }

        /// <summary>右对齐垂直居中绘制文本。</summary>
        public static void DrawStringRight(Graphics g, string text, Font font, Brush brush, Rectangle rect)
        {
            if (string.IsNullOrEmpty(text)) return;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.DrawString(text, font, brush, rect, FmtRight);
        }
    }
}
