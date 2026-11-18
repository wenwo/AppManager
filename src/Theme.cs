using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;

namespace AppManager
{
    /// <summary>
    /// 桌面效率工具主题：中性灰明暗分层、蓝色强调、轻量圆角与语义化字体。
    /// </summary>
    internal static class Theme
    {
        // 背景与卡片
        public static readonly Color Background = Color.FromArgb(247, 248, 250); // #F7F8FA 内容区底色
        public static readonly Color Card = Color.White;
        public static readonly Color TopBarBg = Color.FromArgb(242, 244, 247);
        public static readonly Color CardHover = Color.FromArgb(245, 247, 250);
        public static readonly Color SearchBg = Color.FromArgb(240, 242, 245);
        public static readonly Color RowHover = Color.FromArgb(237, 240, 244);
        public static readonly Color SelectionBg = Color.FromArgb(230, 240, 255);
        public static readonly Color SelectedCard = Color.FromArgb(246, 250, 255); // 选中的卡片底色（配强调色描边），比 SelectionBg 更浅

        // 强调色
        public static readonly Color Accent = Color.FromArgb(22, 119, 255); // #1677FF，图标/描边/实底按钮
        public static readonly Color AccentText = Color.FromArgb(9, 88, 217); // #0958D9，浅底小字号文字专用高对比蓝
        public static readonly Color AccentRipple = Color.FromArgb(32, 22, 119, 255);
        public static readonly Color AccentDisabled = Color.FromArgb(150, 194, 255);
        // 实底主按钮的悬停 / 按下色：所有自绘主按钮共用，悬停略亮、按下略暗
        public static readonly Color AccentHover = Color.FromArgb(45, 132, 255);
        public static readonly Color AccentPressed = Color.FromArgb(20, 105, 225); // 停用状态下的强调色（已勾选但不可改的勾选框等）

        // 文字：主文字近黑；次文字保证小字号可读；第三级仅用于占位与禁用
        public static readonly Color TextPrimary = Color.FromArgb(32, 33, 36);
        public static readonly Color TextSecondary = Color.FromArgb(95, 99, 104);
        public static readonly Color TextTertiary = Color.FromArgb(143, 149, 158);
        public static readonly Color IconColor = Color.FromArgb(70, 74, 80);
        public static readonly Color ControlBorder = Color.FromArgb(218, 220, 224);
        public static readonly Color Separator = Color.FromArgb(229, 230, 235);

        // 语义色：危险/成功/状态等，集中管理避免魔法值散落
        public static readonly Color DeleteRed = Color.FromArgb(245, 74, 69);
        // 危险操作实底按钮（删除等）的悬停 / 按下色：与强调色按钮同样的「悬停略亮、按下略暗」
        public static readonly Color DangerHover = Color.FromArgb(248, 99, 94);
        public static readonly Color DangerPressed = Color.FromArgb(221, 62, 57);
        // 状态文字与浅底：成功 / 警示 / 失败，导入记录、布局备份、设置页等共用同一组
        public static readonly Color SuccessText = Color.FromArgb(24, 128, 66);
        public static readonly Color SuccessBg = Color.FromArgb(228, 245, 234);
        public static readonly Color WarningText = Color.FromArgb(196, 98, 0);
        public static readonly Color WarningBg = Color.FromArgb(255, 241, 222);
        public static readonly Color DangerText = Color.FromArgb(207, 48, 42);
        public static readonly Color DangerBg = Color.FromArgb(255, 235, 233);
        public static readonly Color NeutralTint = Color.FromArgb(110, 116, 125);   // 图标块的中性灰（停用项、恢复前留底等）
        public static readonly Color ToggleGreen = Color.FromArgb(52, 199, 89);
        public static readonly Color ToggleOn = Accent;
        public static readonly Color SegmentBg = Color.FromArgb(233, 236, 241);
        public static readonly Color ChipBg = Color.FromArgb(239, 241, 245);
        public static readonly Color StatusBarBg = Color.FromArgb(250, 251, 252);
        public static readonly Color TooltipBg = Color.FromArgb(235, 30, 30, 32); // 悬浮信息卡深底
        // 自绘细滚动条的滑块：平时浅，指针移上去或拖动时加深
        public static readonly Color ScrollThumb = Color.FromArgb(80, 60, 60, 67);
        public static readonly Color ScrollThumbHot = Color.FromArgb(150, 60, 60, 67);

        // 菜单：白底 + 分隔线色描边（MenuWindow.OnPaint 内绘制），与底层内容形成清晰边界
        public static readonly Color MenuBg = Color.White;
        // iOS 上下文菜单悬停：浅灰高亮 + 保持深色文字（非 macOS 蓝底白字）
        public static readonly Color MenuHover = Color.FromArgb(240, 242, 245);

        // 侧栏：比窗口背景深一档，形成「侧栏 < 内容底 < 卡片」三级色阶层次
        public static readonly Color SidebarBg = Color.FromArgb(240, 241, 243);

        // 模态遮罩：弹窗外区域压暗程度（0~1），参考 Fluent ContentDialog 浅色主题的遮罩强度
        public const double ModalScrimOpacity = 0.32;

        // 圆角规格（96 DPI 下的设计值，使用处用 DpiHelper.Scale 按当前 DPI 换算）。界面上的圆角只用这三档：
        // 新写的控件不要再写死别的数值，也不要漏掉换算（不换算的圆角在高缩放下会显得偏方）。
        public const int RadiusCard = 8;      // 卡片、列表和面板的外框、分段开关的底
        public const int RadiusControl = 6;   // 按钮、输入框、搜索框、行高亮、悬停底色
        public const int RadiusChip = 4;      // 小标签、勾选框、行内的小悬停块

        // 尺寸
        public const int CornerRadius = 12;
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

        // 字体家族缓存与其锁：必须在 FontName 之前初始化——
        // FontName 的 ResolveFamily 会调用 InstalledFamilies，内部要 lock(familiesLock)，
        // 若锁对象尚为 null 会抛 ArgumentNullException，导致 Theme 类型初始化失败、启动即崩。
        private static readonly object familiesLock = new object();
        private static HashSet<string> installedFamilies;

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

        public static Font WindowTitleFont { get { return FontOf(12.5F, FontStyle.Bold); } }
        public static Font PageTitleFont { get { return FontOf(15F, FontStyle.Bold); } }
        public static Font DialogTitleFont { get { return FontOf(13F, FontStyle.Bold); } }
        public static Font SectionTitleFont { get { return FontOf(10F, FontStyle.Bold); } }
        public static Font NavigationFont { get { return FontOf(10F, FontStyle.Bold); } }
        public static Font BodyFont { get { return FontOf(9.5F); } }
        public static Font CaptionFont { get { return FontOf(9F); } }
        public static Font ButtonFont { get { return FontOf(9.5F, FontStyle.Bold); } }
        public static Font MenuFont { get { return FontOf(9F, FontStyle.Regular); } }

        private static string ResolveFamily(string[] candidates)
        {
            HashSet<string> set = InstalledFamilies;
            foreach (string name in candidates)
                if (set.Contains(name)) return name;
            return FontFamily.GenericSansSerif.Name;
        }

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
