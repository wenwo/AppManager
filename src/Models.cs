using System;
using System.Drawing;
using System.IO;

namespace AppManager
{
    /// <summary>
    /// 应用信息数据模型：承载扫描收录后的全部条目信息。
    /// 公开字段便于跨层访问与持久化；Icon 仅为运行期缓存（标记 NonSerialized）。
    /// </summary>
    public class AppInfo
    {
        public string Name = string.Empty;
        public string TargetPath = string.Empty;   // 启动目标：exe/文件夹/网址；打包应用为 shell:AppsFolder\<AUMID>
        public string ShortcutPath = string.Empty; // 来源快捷方式路径，非快捷方式为空
        public string InstallLocation = string.Empty;
        public string UninstallString = string.Empty;
        public string DisplayIcon = string.Empty;  // 注册表 DisplayIcon 或快捷方式图标定位
        public string Category = string.Empty;     // 当前所属分类
        public bool IsFavorite;
        public bool IsHidden;
        public bool IsUwp;                         // 打包/UWP 应用（AppsFolder 或 shell 快捷方式判定）
        public bool IsDesktop;                     // 来自桌面快捷方式
        public bool IsFile;                        // 文件/文件夹/网址条目（用户添加或桌面文件快捷方式）
        public bool IsFolder;                      // 目标为文件夹
        public string Source = string.Empty;        // 来源：AppsFolder/StartMenu/Desktop/Added
        public string Key = string.Empty;           // 持久化唯一键（af:/lnk:/add:）
        internal int DatabaseKind = -1;             // 数据库分页结果的内容类型，避免展示时逐条查询归属

        [NonSerialized] public Bitmap Icon;         // 运行期图标缓存（网格大图标），不参与持久化
        [NonSerialized] public Bitmap SmallIcon;    // 运行期图标缓存（列表用小图标，对齐资源管理器），不参与持久化

        /// <summary>
        /// 显示与去重用唯一标识：优先快捷方式路径，其次目标路径，最后名称。
        /// </summary>
        public string DisplayKey()
        {
            if (!string.IsNullOrEmpty(ShortcutPath)) return ShortcutPath;
            if (!string.IsNullOrEmpty(TargetPath)) return TargetPath;
            return Name;
        }

        /// <summary>
        /// 列表/网格的显示名：文件条目补上磁盘后缀（对齐资源管理器「名称」列）。
        /// 快捷方式收录或用户改名（Name 与文件基本名不一致）的条目保持原名，避免被追加目标后缀。
        /// </summary>
        public string DisplayName()
        {
            if (!IsFile || IsFolder) return Name;
            string path = TargetPath;
            if (string.IsNullOrEmpty(path) || Scanner.IsUrl(path)) return Name;
            if (!string.Equals(Name, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase))
                return Name;
            string ext = Path.GetExtension(path);
            return string.IsNullOrEmpty(ext) ? Name : Name + ext;
        }
    }

    /// <summary>
    /// 分类定义：内置 9 类 + 常用 + 快捷中心 + 自定义 + 只读全集分类。
    /// IsAllApps 表示只读置底分类（全部软件/全部文件/全部资源）。
    /// </summary>
    public class Category
    {
        public string Name = string.Empty;
        public bool IsBuiltIn;   // 内置不可删
        public bool IsAllApps;   // 只读全集分类（只读置底）
        public bool IsCustom;   // 用户自定义可删
        public bool IsSpecial;   // 常用/快捷中心等特殊分类
    }
}
