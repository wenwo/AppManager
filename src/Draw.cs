using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace AppManager
{
    public enum UiIconKind
    {
        Add,
        Organize,
        Back,
        Search,
        List,
        PanelLeftClose,
        PanelLeftOpen
    }

    /// <summary>
    /// 自绘工具集：圆角矩形、柔和阴影、文本渲染等 GDI+ 原语。
    /// 集中提供绘制函数，保证全站 UI 风格一致。
    /// </summary>
    internal static class Draw
    {
        public static void DrawIcon(Graphics g, UiIconKind kind, Rectangle rect, Color color)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(color, 1.8F))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                float cx = rect.X + rect.Width / 2F;
                float cy = rect.Y + rect.Height / 2F;
                switch (kind)
                {
                    case UiIconKind.Add:
                        g.DrawLine(pen, cx - 5F, cy, cx + 5F, cy);
                        g.DrawLine(pen, cx, cy - 5F, cx, cy + 5F);
                        break;
                    case UiIconKind.Organize:
                        g.DrawRectangle(pen, cx - 7F, cy - 7F, 5F, 5F);
                        g.DrawRectangle(pen, cx + 2F, cy - 7F, 5F, 5F);
                        g.DrawRectangle(pen, cx - 7F, cy + 2F, 5F, 5F);
                        g.DrawRectangle(pen, cx + 2F, cy + 2F, 5F, 5F);
                        break;
                    case UiIconKind.Back:
                        g.DrawLine(pen, cx + 3F, cy - 5F, cx - 3F, cy);
                        g.DrawLine(pen, cx - 3F, cy, cx + 3F, cy + 5F);
                        break;
                    case UiIconKind.Search:
                        g.DrawEllipse(pen, cx - 6F, cy - 6F, 10F, 10F);
                        g.DrawLine(pen, cx + 3F, cy + 3F, cx + 7F, cy + 7F);
                        break;
                    case UiIconKind.List:
                        for (int row = -1; row <= 1; row++)
                        {
                            float y = cy + row * 5F;
                            g.DrawLine(pen, cx - 6F, y, cx - 5F, y);
                            g.DrawLine(pen, cx - 1F, y, cx + 6F, y);
                        }
                        break;
                    case UiIconKind.PanelLeftClose:
                    case UiIconKind.PanelLeftOpen:
                        Rectangle panel = new Rectangle((int)(cx - 7F), (int)(cy - 8F), 14, 16);
                        DrawRoundedRect(g, pen, panel, 3);
                        g.DrawLine(pen, panel.X + 4, panel.Y + 4, panel.X + 4, panel.Bottom - 4);
                        break;
                }
            }
        }

        private static readonly object glyphLock = new object();
        private static readonly Dictionary<string, GraphicsPath> glyphPaths = new Dictionary<string, GraphicsPath>(StringComparer.Ordinal);
        private static FontFamily glyphFamily;

        /// <summary>
        /// 在 box 内居中绘制系统图标字体（Segoe Fluent Icons / MDL2）里的一个字形：墨迹等比缩放到 box 的短边，
        /// 不受字体行盒留白影响。字形路径按单位尺寸缓存；系统没有图标字体或字形为空时不绘制。
        /// </summary>
        public static void DrawGlyph(Graphics g, string glyph, RectangleF box, Color color)
        {
            GraphicsPath unit = GlyphPath(glyph);
            float size = Math.Min(box.Width, box.Height);
            if (unit == null || size <= 0F) return;
            GraphicsState state = g.Save();
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform(box.X + box.Width / 2F, box.Y + box.Height / 2F);
                g.ScaleTransform(size, size);
                using (SolidBrush brush = new SolidBrush(color)) g.FillPath(brush, unit);
            }
            finally { g.Restore(state); }
        }

        /// <summary>取字形的单位路径（墨迹中心在原点、长边为 1）；缓存有界，取不到时返回 null。</summary>
        private static GraphicsPath GlyphPath(string glyph)
        {
            if (string.IsNullOrEmpty(glyph)) return null;
            lock (glyphLock)
            {
                GraphicsPath path;
                if (glyphPaths.TryGetValue(glyph, out path)) return path;
                if (glyphFamily == null)
                {
                    foreach (string candidate in new string[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
                    {
                        if (!Theme.InstalledFamilies.Contains(candidate)) continue;
                        glyphFamily = new FontFamily(candidate);
                        break;
                    }
                }
                if (glyphFamily != null)
                {
                    path = new GraphicsPath();
                    using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
                        path.AddString(glyph, glyphFamily, (int)FontStyle.Regular, 100F, PointF.Empty, format);
                    RectangleF ink = path.GetBounds();
                    float extent = Math.Max(ink.Width, ink.Height);
                    if (extent > 0F)
                    {
                        using (Matrix normalize = new Matrix())
                        {
                            normalize.Scale(1F / extent, 1F / extent);
                            normalize.Translate(-(ink.X + ink.Width / 2F), -(ink.Y + ink.Height / 2F));
                            path.Transform(normalize);
                        }
                    }
                    else
                    {
                        path.Dispose();
                        path = null;
                    }
                }
                if (glyphPaths.Count > 64)
                {
                    foreach (GraphicsPath cached in glyphPaths.Values) if (cached != null) cached.Dispose();
                    glyphPaths.Clear();
                }
                glyphPaths[glyph] = path;
                return path;
            }
        }

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
        /// 构造圆角矩形路径；半径自动夹到短边一半，防止越界。宽度或高度不足 2 像素时
        /// 无法形成圆角，回退为直角矩形，避免 AddArc 收到 0 尺寸。
        /// </summary>
        public static GraphicsPath RoundedPath(Rectangle rect, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (rect.Width <= 0 || rect.Height <= 0) return p;
            int limit = Math.Min(rect.Width, rect.Height) / 2;
            if (radius > limit) radius = limit;
            if (radius < 1)
            {
                p.AddRectangle(rect);
                return p;
            }
            int d = radius * 2;
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

        /// <summary>两色线性混合：amount 为 b 所占比例（0~1）。</summary>
        public static Color Mix(Color a, Color b, float amount)
        {
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * amount),
                (int)Math.Round(a.G + (b.G - a.G) * amount),
                (int)Math.Round(a.B + (b.B - a.B) * amount));
        }

        /// <summary>
        /// 自绘勾选框：未勾选是白底描边，勾选后是强调色实底加白色对勾；停用时整体变浅。
        /// r 为已按 DPI 换算好的方框，dpi 用来换算圆角和线宽。
        /// </summary>
        public static void DrawCheckBox(Graphics g, Rectangle r, bool isChecked, bool enabled, int dpi)
        {
            int radius = DpiHelper.Scale(4, dpi);
            if (!isChecked)
            {
                using (SolidBrush fill = new SolidBrush(enabled ? Theme.Card : Theme.SearchBg)) FillRoundedRect(g, fill, r, radius);
                using (Pen border = new Pen(Theme.ControlBorder, Math.Max(1F, DpiHelper.Scale(3, dpi) / 2F)))
                    DrawRoundedRect(g, border, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), radius);
                return;
            }
            using (SolidBrush fill = new SolidBrush(enabled ? Theme.Accent : Theme.AccentDisabled)) FillRoundedRect(g, fill, r, radius);
            using (Pen mark = new Pen(Color.White, Math.Max(1.6F, DpiHelper.Scale(2, dpi))))
            {
                mark.StartCap = LineCap.Round;
                mark.EndCap = LineCap.Round;
                mark.LineJoin = LineJoin.Round;
                float w = r.Width, h = r.Height;
                g.DrawLines(mark, new PointF[]
                {
                    new PointF(r.X + w * 0.26F, r.Y + h * 0.52F),
                    new PointF(r.X + w * 0.44F, r.Y + h * 0.70F),
                    new PointF(r.X + w * 0.76F, r.Y + h * 0.34F)
                });
            }
        }

        /// <summary>
        /// 自绘单选圆点：未选中是白底描边的圆，选中后是强调色实底加白色圆心；停用时整体变浅。
        /// </summary>
        public static void DrawRadio(Graphics g, Rectangle r, bool isChecked, bool enabled, int dpi)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle circle = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
            if (!isChecked)
            {
                using (SolidBrush fill = new SolidBrush(enabled ? Theme.Card : Theme.SearchBg)) g.FillEllipse(fill, circle);
                using (Pen border = new Pen(Theme.ControlBorder, Math.Max(1F, DpiHelper.Scale(3, dpi) / 2F))) g.DrawEllipse(border, circle);
                return;
            }
            using (SolidBrush fill = new SolidBrush(enabled ? Theme.Accent : Theme.AccentDisabled)) g.FillEllipse(fill, circle);
            float dot = r.Width * 0.38F;
            using (SolidBrush center = new SolidBrush(Color.White))
                g.FillEllipse(center, r.X + (r.Width - 1 - dot) / 2F, r.Y + (r.Height - 1 - dot) / 2F, dot, dot);
        }

        /// <summary>
        /// 统一的空状态：居中的浅色图标块 + 一行标题 + 若干行说明，说清「这里是空的」和「接下来可以做什么」。
        /// 主列表、布局备份、操作记录、导入记录、桌面备份清单共用。glyph 是系统图标字体里的字形，取不到时只是不画图标；
        /// 区域太矮放不下图标块时，退回只画一行标题。
        /// </summary>
        public static void DrawEmptyState(Graphics g, Rectangle area, string glyph, string title, string[] lines, int dpi)
        {
            if (area.Width <= 0 || area.Height <= 0) return;
            int count = lines == null ? 0 : lines.Length;
            int tile = DpiHelper.Scale(56, dpi), titleH = DpiHelper.Scale(26, dpi), lineH = DpiHelper.Scale(20, dpi);
            int blockH = tile + DpiHelper.Scale(16, dpi) + titleH + (count > 0 ? DpiHelper.Scale(8, dpi) + count * lineH : 0);
            if (area.Height < blockH + DpiHelper.Scale(16, dpi))
            {
                using (SolidBrush plain = new SolidBrush(Theme.TextSecondary))
                    DrawStringCenter(g, title, Theme.BodyFont, plain, area);
                return;
            }
            int y = area.Y + Math.Max(DpiHelper.Scale(8, dpi), (area.Height - blockH) / 2 - DpiHelper.Scale(10, dpi));
            Rectangle tileRect = new Rectangle(area.X + (area.Width - tile) / 2, y, tile, tile);
            using (SolidBrush tb = new SolidBrush(Mix(Theme.Accent, Theme.Background, 0.87f)))
                FillRoundedRect(g, tb, tileRect, DpiHelper.Scale(14, dpi));
            int inset = DpiHelper.Scale(15, dpi);
            DrawGlyph(g, glyph, Rectangle.Inflate(tileRect, -inset, -inset), Theme.Accent);
            y += tile + DpiHelper.Scale(16, dpi);
            using (SolidBrush titleBrush = new SolidBrush(Theme.TextPrimary))
                DrawStringCenter(g, title, Theme.FontOf(11F, FontStyle.Bold), titleBrush, new Rectangle(area.X, y, area.Width, titleH));
            if (count == 0) return;
            y += titleH + DpiHelper.Scale(8, dpi);
            using (SolidBrush text = new SolidBrush(Theme.TextSecondary))
            {
                foreach (string line in lines)
                {
                    DrawStringCenter(g, line, Theme.CaptionFont, text, new Rectangle(area.X, y, area.Width, lineH));
                    y += lineH;
                }
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
                if (shadowPaths.Count > 64)
                {
                    foreach (GraphicsPath cached in shadowPaths.Values) cached.Dispose();
                    shadowPaths.Clear();
                }
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
