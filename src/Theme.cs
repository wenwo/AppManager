using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;

namespace AppManager
{
    /// <summary>
    /// iOS 风格主题常量：配色、圆角、尺寸、字体集中管理，避免散落硬编码。
    /// </summary>
    internal static class Theme
    {
        // 背景与卡片
        public static readonly Color Background = Color.FromArgb(242, 242, 247); // #F2F2F7
        public static readonly Color Card = Color.White;
        public static readonly Color CardHover = Color.FromArgb(245, 245, 250);
        public static readonly Color SearchBg = Color.FromArgb(230, 230, 235);   // iOS 搜索框灰底
        public static readonly Color RowHover = Color.FromArgb(240, 240, 243);   // 列表行悬停
        public static readonly Color SelectionBg = Color.FromArgb(20, 0, 122, 255); // 侧栏选中胶囊

        // 强调色
        public static readonly Color Accent = Color.FromArgb(0, 122, 255); // #007AFF
        public static readonly Color AccentRipple = Color.FromArgb(40, 0, 122, 255);

        // 文字
        public static readonly Color TextPrimary = Color.FromArgb(28, 28, 30);    // #1C1C1E
        public static readonly Color TextSecondary = Color.FromArgb(142, 142, 147); // #8E8E93
        public static readonly Color Separator = Color.FromArgb(229, 229, 234);   // #E5E5EA

        // 语义色：危险/成功/状态等，集中管理避免魔法值散落
        public static readonly Color DeleteRed = Color.FromArgb(255, 59, 48);    // iOS systemRed（删除/危险操作）
        public static readonly Color ToggleGreen = Color.FromArgb(52, 199, 89);  // iOS systemGreen（开关开态）
        public static readonly Color SegmentBg = Color.FromArgb(230, 230, 235);  // 分段控件灰底 / 开关关态轨道
        public static readonly Color ChipBg = Color.FromArgb(239, 239, 241);     // 列表分类胶囊底色
        public static readonly Color StatusBarBg = Color.FromArgb(248, 248, 250); // 底部状态栏底色
        public static readonly Color TooltipBg = Color.FromArgb(235, 30, 30, 32); // 悬浮信息卡深底

        // 菜单：白底 + 分隔线色描边（MenuWindow.OnPaint 内绘制），与底层内容形成清晰边界
        public static readonly Color MenuBg = Color.White;
        // iOS 上下文菜单悬停：浅灰高亮 + 保持深色文字（非 macOS 蓝底白字）
        public static readonly Color MenuHover = Color.FromArgb(230, 230, 235);

        // 侧栏：比窗口背景深一档，形成「侧栏 < 内容底 < 卡片」三级色阶层次
        public static readonly Color SidebarBg = Color.FromArgb(236, 236, 241);

        // 模态遮罩：弹窗外区域压暗程度（0~1），参考 Fluent ContentDialog 浅色主题的遮罩强度
        public const double ModalScrimOpacity = 0.32;

        // 尺寸
        public const int CornerRadius = 18;
        public const int IconSize = 56;
        public const int ListIconSize = 16;   // 列表小图标：与资源管理器一致，取 shell 16x16 原图不缩放
        public const int SidebarWidth = 200;
        // 顶栏双行：行1 标题+窗口按钮，行2 搜索/计数+视图切换+操作按钮
        public const int TitleRowHeight = 34;    // 行1 高度
        public const int ToolbarRowHeight = 50;  // 行2 高度
        public const int TopBarHeight = TitleRowHeight + ToolbarRowHeight; // 顶栏总高 84
        public const int CardPadding = 12;
        public const int GridItemGap = 14;
        public const int GridItemWidth = 110;
        public const int GridItemHeight = 116;
        public const int ListItemHeight = 64;

        // ===== 字体 =====

        /// <summary>
        /// 字体缓存：字号/样式/单位组合 -> Font 实例，应用生命周期内复用不释放。
        /// 绘制热路径（OnPaint/OnMouseMove 触发的重绘）高频取字体，重复 new 会造成
        /// GDI 句柄与 GC 压力、界面发卡，故集中缓存；调用方不得 Dispose 返回的字体。
        /// 注意：必须在 FontName 之前初始化（FontName 的 ResolveFamily 会调用 GetFont）。
        /// </summary>
        private static readonly Dictionary<string, Font> fontCache = new Dictionary<string, Font>();
        // 图标线程在后台构建占位图时也会取字体：缓存读写必须加锁，避免跨线程竞争
        private static readonly object fontCacheLock = new object();

        /// <summary>
        /// UI 字体族名：优先「微软雅黑 UI」（中文渲染清晰、与 Windows 中文系统一致），
        /// 依次回退雅黑 / Segoe UI / Tahoma，均不可用时用系统默认无衬线字体。
        /// 集中解析一次，避免各控件硬编码 "Segoe UI" 导致中文回退字体不一致、发虚。
        /// </summary>
        public static readonly string FontName = ResolveFamily(new string[]
        {
            "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Tahoma"
        });

        private static Font GetFont(float size, FontStyle style, GraphicsUnit unit)
        {
            string key = style.ToString() + "|" + size.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "|" + unit.ToString();
            lock (fontCacheLock)
            {
                Font f;
                if (!fontCache.TryGetValue(key, out f))
                {
                    f = new Font(FontName, size, style, unit);
                    fontCache[key] = f;
                }
                return f;
            }
        }

        /// <summary>按磅值取 UI 字体（常规）；返回缓存实例，调用方不得 Dispose。</summary>
        public static Font FontOf(float size)
        {
            return GetFont(size, FontStyle.Regular, GraphicsUnit.Point);
        }

        /// <summary>按磅值取 UI 字体（可指定字重/样式）；返回缓存实例，调用方不得 Dispose。</summary>
        public static Font FontOf(float size, FontStyle style)
        {
            return GetFont(size, style, GraphicsUnit.Point);
        }

        /// <summary>按像素值取 UI 字体（图标占位等按像素测量的场景）；返回缓存实例，调用方不得 Dispose。</summary>
        public static Font FontPx(float size, FontStyle style)
        {
            return GetFont(size, style, GraphicsUnit.Pixel);
        }

        private static string ResolveFamily(string[] candidates)
        {
            HashSet<string> set = InstalledFamilies;
            foreach (string name in candidates)
                if (set.Contains(name)) return name;
            return FontFamily.GenericSansSerif.Name;
        }

        private static readonly object familiesLock = new object();
        private static HashSet<string> installedFamilies;

        /// <summary>
        /// 已安装字体家族名集合：进程内只枚举一次 InstalledFontCollection（百毫秒级开销），
        /// Theme 与 IconButton 等字体探测共用，避免重复枚举。
        /// </summary>
        internal static HashSet<string> InstalledFamilies
        {
            get
            {
                lock (familiesLock)
                {
                    if (installedFamilies == null)
                    {
                        HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        try
                        {
                            using (InstalledFontCollection col = new InstalledFontCollection())
                                foreach (FontFamily f in col.Families) set.Add(f.Name);
                        }
                        catch { }
                        installedFamilies = set;
                    }
                    return installedFamilies;
                }
            }
        }
    }
}
