using System;
using System.Drawing;
using System.Windows.Forms;

namespace AppManager
{
    /// <summary>内容区底栏：选中项详情。只读取 AppInfo 字段，不访问磁盘。</summary>
    public partial class MainForm
    {
        private static readonly StringFormat SelectionPathFormat = CreateSelectionPathFormat();

        private static StringFormat CreateSelectionPathFormat()
        {
            StringFormat format = new StringFormat();
            format.Alignment = StringAlignment.Near;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisPath;
            format.FormatFlags = StringFormatFlags.NoWrap;
            return format;
        }

        private void OnGridSelectionChanged()
        {
            if (IsDisposed || Disposing) return;
            InvalidateContentFooter();
        }

        /// <summary>绘制选中项详情；没有可见选中项时返回 false，由原有分页/集合文案接管。</summary>
        private bool PaintSelectionFooter(Graphics graphics, Rectangle footer)
        {
            if (grid == null || settingsOpen || !grid.Visible) return false;
            int count = grid.SelectedCount;
            if (count <= 0) return false;

            using (Pen separator = new Pen(Theme.Separator, 1))
                graphics.DrawLine(separator, footer.X + Dpi(14), footer.Y, footer.Right - Dpi(14), footer.Y);

            int left = footer.X + Dpi(16);
            int right = footer.Right - Dpi(16);
            if (!databasePrevious.IsEmpty) right = databasePrevious.Left - Dpi(12);
            if (right < left) right = left;

            using (SolidBrush primary = new SolidBrush(Theme.TextPrimary))
            using (SolidBrush secondary = new SolidBrush(Theme.TextSecondary))
            {
                if (count != 1)
                {
                    Draw.DrawStringLeft(graphics, SelectionCountText(count), Theme.BodyFont, primary,
                        new Rectangle(left, footer.Top, right - left, footer.Height));
                    return true;
                }

                AppInfo app = grid.SingleSelectedApp;
                if (app == null)
                {
                    Draw.DrawStringLeft(graphics, SelectionCountText(1), Theme.BodyFont, primary,
                        new Rectangle(left, footer.Top, right - left, footer.Height));
                    return true;
                }

                string name = SelectionNameText(app);
                string location = SelectionLocationText(app);
                string source = SelectionSourceText(app);
                int available = Math.Max(0, right - left);
                int nameGap = Dpi(12);
                int sourceGap = Dpi(12);
                int sourceWidth = string.IsNullOrEmpty(source)
                    ? 0 : MeasureTextWidth(source, Theme.CaptionFont) + Dpi(16);
                bool showSource = sourceWidth > 0 && available >= Dpi(300);
                if (!showSource) sourceWidth = 0;

                int sourceReserve = showSource ? sourceWidth + sourceGap : 0;
                int nameCap = (int)(available * 0.4f);
                int nameWidth = Math.Min(MeasureTextWidth(name, Theme.BodyFont), nameCap);
                int nameGapWidth = nameWidth > 0 ? nameGap : 0;
                int minPathWidth = Math.Min(Dpi(80), Math.Max(0, available - sourceReserve - nameGapWidth));
                int pathWidth = available - nameWidth - nameGapWidth - sourceReserve;
                if (pathWidth < minPathWidth)
                {
                    int reduce = Math.Min(minPathWidth - pathWidth, nameWidth);
                    nameWidth -= reduce;
                    nameGapWidth = nameWidth > 0 ? nameGap : 0;
                    pathWidth = available - nameWidth - nameGapWidth - sourceReserve;
                }
                pathWidth = Math.Max(0, pathWidth);

                if (nameWidth > 0)
                    Draw.DrawStringLeft(graphics, name, Theme.BodyFont, primary,
                        new Rectangle(left, footer.Top, nameWidth, footer.Height));

                int pathLeft = left + nameWidth + nameGapWidth;
                if (pathWidth > 0)
                {
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    graphics.DrawString(location, Theme.CaptionFont, secondary,
                        new Rectangle(pathLeft, footer.Top, pathWidth, footer.Height), SelectionPathFormat);
                }

                if (showSource)
                {
                    int sourceHeight = Dpi(24);
                    Rectangle sourceRect = new Rectangle(right - sourceWidth,
                        footer.Top + Math.Max(0, (footer.Height - sourceHeight) / 2), sourceWidth, sourceHeight);
                    using (SolidBrush chip = new SolidBrush(Theme.ChipBg))
                        Draw.FillRoundedRect(graphics, chip, sourceRect, Dpi(12));
                    Draw.DrawStringCenter(graphics, source, Theme.CaptionFont, secondary, sourceRect);
                }
            }
            return true;
        }

        internal static string SelectionNameText(AppInfo app)
        {
            return app == null ? string.Empty : CleanSelectionText(app.DisplayName());
        }

        /// <summary>实际位置：网址原样，打包应用优先安装目录，普通条目优先启动目标。</summary>
        internal static string SelectionLocationText(AppInfo app)
        {
            if (app == null) return "—";
            string target = TrimOuterQuotes(app.TargetPath);
            if (Scanner.IsAppsFolderTarget(target) || app.IsUwp)
            {
                string install = TrimOuterQuotes(app.InstallLocation);
                if (install.Length > 0) return install;
                string appId = PackagedAppId(app);
                return appId.Length > 0 ? "打包应用 · " + appId : "打包应用";
            }
            if (Scanner.IsUrl(target)) return target;
            if (target.Length > 0) return target;

            string installLocation = TrimOuterQuotes(app.InstallLocation);
            if (installLocation.Length > 0) return installLocation;
            string shortcut = TrimOuterQuotes(app.ShortcutPath);
            return shortcut.Length > 0 ? shortcut : "—";
        }

        internal static string SelectionSourceText(AppInfo app)
        {
            if (app == null) return string.Empty;
            string source = CleanSelectionText(app.Source);
            if (string.Equals(source, "Desktop", StringComparison.OrdinalIgnoreCase) || app.IsDesktop)
                return "桌面快捷方式";
            if (string.Equals(source, "StartMenu", StringComparison.OrdinalIgnoreCase)
                || string.Equals(source, "AppsFolder", StringComparison.OrdinalIgnoreCase))
                return "开始菜单";
            if (string.Equals(source, "Added", StringComparison.OrdinalIgnoreCase))
                return "手动添加";
            return source.Length == 0 ? string.Empty : "其他来源";
        }

        internal static string SelectionCountText(int count)
        {
            return "已选 " + count + " 项";
        }

        private static string PackagedAppId(AppInfo app)
        {
            string target = TrimOuterQuotes(app == null ? null : app.TargetPath);
            if (target.StartsWith(Scanner.AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
                return TrimPackagedId(target.Substring(Scanner.AppsFolderPrefix.Length));

            string arguments = CleanSelectionText(app == null ? null : app.LaunchArguments);
            int start = arguments.IndexOf(Scanner.AppsFolderPrefix, StringComparison.OrdinalIgnoreCase);
            return start < 0 ? string.Empty
                : TrimPackagedId(arguments.Substring(start + Scanner.AppsFolderPrefix.Length));
        }

        private static string TrimPackagedId(string value)
        {
            string id = CleanSelectionText(value).Trim('"');
            int end = id.IndexOfAny(new char[] { '"', ' ' });
            if (end >= 0) id = id.Substring(0, end);
            return id.Trim('"');
        }

        private static string TrimOuterQuotes(string value)
        {
            string text = CleanSelectionText(value);
            if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
                text = text.Substring(1, text.Length - 2).Trim();
            return text;
        }

        private static string CleanSelectionText(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        }

        private static int MeasureTextWidth(string text, Font font)
        {
            if (string.IsNullOrEmpty(text) || font == null) return 0;
            return TextRenderer.MeasureText(text, font, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        }
    }
}
