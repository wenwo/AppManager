using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;
using System.Xml;

namespace AppManager
{
    /// <summary>
    /// 扫描收录：系统「全部应用」列表(shell:AppsFolder，权威来源) + 桌面快捷方式 + 已收纳备份 + 用户手动添加。
    /// 注册表不再作为来源，仅按名称补充卸载/安装/图标信息；开始菜单仅在 AppsFolder 枚举失败时兜底。
    /// 结果按名称去重（同名合并且保留有效启动目标），并应用分类规则与配置覆盖。
    /// </summary>
    internal class Scanner
    {
        private readonly AppConfig config;

        // COM 快捷方式解析：IShellLinkW 接口。
        // 注意：COM 接口按 vtable 槽位顺序调用，方法声明顺序必须与 shobjidl_core.h 中
        // IShellLinkW 完全一致（含 Get/Set 的方向与 GetHotkey/SetHotkey/GetShowCmd/SetShowCmd），
        // 顺序错位会导致调用到相邻槽位、参数不匹配进而破坏调用栈（图标路径取回垃圾数据）。
        [ComImport]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        [ClassInterface(ClassInterfaceType.None)]
        private class CShellLink { }

        public Scanner(AppConfig cfg) { config = cfg; }

        // 按名称去重的索引（NormalizeEntryName → 条目）：MergeByName 由 O(n²) 遍历改为 O(1) 查找
        private Dictionary<string, AppInfo> nameIndex;

        /// <summary>执行全量扫描，返回去重后的应用列表。</summary>
        public List<AppInfo> ScanAll()
        {
            nameIndex = new Dictionary<string, AppInfo>(StringComparer.Ordinal);
            Dictionary<string, AppInfo> dict = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
            ScanDesktop(dict);          // 桌面快捷方式（快捷中心来源）
            ScanDesktopBackup(dict);    // 已收纳备份（快捷中心来源）
            ScanAdded(dict);            // 用户手动添加
            ScanManagedFiles(dict);     // 收纳主目录里实际存在的文件/文件夹（「全部文件」视图）
            int appsFolder = ScanAppsFolder(dict);      // 系统「全部应用」列表（权威来源）
            if (appsFolder == 0) ScanStartMenu(dict);   // 枚举失败时兜底开始菜单
            EnrichFromRegistry(dict);   // 注册表仅补充卸载/安装/图标信息，不新增条目

            List<AppInfo> list = new List<AppInfo>(dict.Values);
            ApplyConfig(list);
            return list;
        }

        /// <summary>
        /// 扫描「收纳主目录」（ManageRoot，默认 H:\Manage）里实际存在的文件夹与文件，供「全部文件」视图使用。
        /// 只做登记展示，不写配置、不移动磁盘内容；结构与入库规则一致：文件夹在根下，文件在文件存储目录。
        /// </summary>
        private void ScanManagedFiles(Dictionary<string, AppInfo> dict)
        {
            string root = ResolveManageRoot();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;

            string foldersDir = string.IsNullOrEmpty(config.FolderStorePath)
                ? root : SafeFullPath(config.FolderStorePath);
            string filesDir = string.IsNullOrEmpty(config.FileStorePath)
                ? Path.Combine(root, "Files") : SafeFullPath(config.FileStorePath);
            // 资源模式的存储目录不参与「全部文件」展示，避免把资源库本身当成一个文件夹条目
            string resourceDir = string.IsNullOrEmpty(config.ResourceStorePath)
                ? Path.Combine(root, "Resources") : SafeFullPath(config.ResourceStorePath);

            try
            {
                if (Directory.Exists(foldersDir))
                {
                    foreach (string dir in Directory.GetDirectories(foldersDir))
                    {
                        if (IsSameDirectory(dir, filesDir)) continue;
                        if (IsSameDirectory(dir, resourceDir)) continue;
                        AddManaged(dict, dir, true);
                    }
                }
            }
            catch (Exception ex) { Paths.LogError(ex); }

            try
            {
                if (Directory.Exists(filesDir))
                {
                    foreach (string file in Directory.GetFiles(filesDir))
                    {
                        if (IsStoreMetadata(file)) continue;
                        AddManaged(dict, file, false);
                    }
                }
            }
            catch (Exception ex) { Paths.LogError(ex); }
        }

        private void AddManaged(Dictionary<string, AppInfo> dict, string path, bool isFolder)
        {
            AppInfo a = new AppInfo();
            a.Name = isFolder ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(a.Name)) a.Name = path;
            a.TargetPath = path;
            a.DisplayIcon = path;
            a.Source = "Managed";
            a.Key = "add:" + path;
            a.IsFile = true;
            a.IsFolder = isFolder;
            // 收纳库条目只属于「全部文件」视图，不参与软件分类
            a.Category = "全部文件";
            Merge(dict, a);
        }

        /// <summary>收纳库里的元数据文件（网址记录等）不作为条目展示。</summary>
        private static bool IsStoreMetadata(string path)
        {
            string name = Path.GetFileName(path);
            return string.Equals(name, "nets.md", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "thumbs.db", StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeFullPath(string path)
        {
            try { return DesktopOrganizer.NormalizeRoot(path); }
            catch { return path; }
        }

        private static bool IsSameDirectory(string a, string b)
        {
            try
            {
                string fa = Path.GetFullPath(a).TrimEnd('\\');
                string fb = Path.GetFullPath(b).TrimEnd('\\');
                return string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>收纳根路径：配置值可用则用，其次默认 H:\Manage，都不可用时返回空（不主动建目录）。</summary>
        private string ResolveManageRoot()
        {
            string configured = SafeFullPath(config.ManageRootPath);
            if (!string.IsNullOrEmpty(configured) && Directory.Exists(configured)) return configured;
            string fallback = @"H:\Manage";
            if (Directory.Exists(fallback)) return fallback;
            return string.Empty;
        }

        /// <summary>注入用户手动添加的软件/文件/文件夹条目（来源 AddedItems）；路径失效则跳过。</summary>
        private void ScanAdded(Dictionary<string, AppInfo> dict)
        {
            foreach (AddedEntry e in config.AddedItems)
            {
                if (string.IsNullOrEmpty(e.Path)) continue;
                bool isFolder = e.IsFolder || Directory.Exists(e.Path);
                if (!File.Exists(e.Path) && !Directory.Exists(e.Path)) continue;
                AppInfo a = new AppInfo();
                a.Name = !string.IsNullOrEmpty(e.Name) ? e.Name
                    : (isFolder ? Path.GetFileName(e.Path) : Path.GetFileNameWithoutExtension(e.Path));
                if (string.IsNullOrEmpty(a.Name)) a.Name = e.Path;
                a.TargetPath = e.Path;
                a.Source = "Added";
                a.Key = "add:" + e.Path;
                a.IsFile = true;
                a.IsFolder = isFolder;
                a.DisplayIcon = e.Path;
                a.Category = string.IsNullOrEmpty(e.Category)
                    ? CategoryRules.Classify(a.Name)
                    : e.Category;
                Merge(dict, a);
            }
        }

        // ===== 系统「全部应用」列表（shell:AppsFolder） =====

        /// <summary>AppsFolder 启动目标前缀：打包/UWP 应用以「explorer.exe shell:AppsFolder\&lt;AUMID&gt;」启动。</summary>
        internal const string AppsFolderPrefix = "shell:AppsFolder\\";

        /// <summary>判断启动目标是否为 AppsFolder 定位串（打包/UWP 应用）。</summary>
        internal static bool IsAppsFolderTarget(string target)
        {
            return !string.IsNullOrEmpty(target)
                && target.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 收录系统「全部应用」列表（shell:AppsFolder，权威且已去重）作为软件来源。
        /// 桌面应用的解析路径为可执行文件路径；打包/UWP 应用无文件路径，以 shell:AppsFolder\&lt;AUMID&gt; 作为启动目标。
        /// 返回枚举到的条目数（0 表示枚举失败，调用方回退开始菜单扫描）。
        /// </summary>
        private int ScanAppsFolder(Dictionary<string, AppInfo> dict)
        {
            List<string[]> items = EnumerateAppsFolder();
            foreach (string[] it in items)
            {
                string name = it[0];
                string id = it[1];
                if (IsNonAppEntry(name, id)) continue;

                AppInfo app = new AppInfo();
                app.Name = name;
                app.Source = "AppsFolder";
                app.Key = "af:" + NormalizeEntryName(name);
                app.Category = CategoryRules.Classify(name);

                if (!string.IsNullOrEmpty(id) && Directory.Exists(id))
                {
                    app.TargetPath = id;
                    app.DisplayIcon = id;
                    app.IsFile = true;
                    app.IsFolder = true;
                }
                else if (!string.IsNullOrEmpty(id) && File.Exists(id))
                {
                    app.TargetPath = id;
                    app.DisplayIcon = id;
                    // exe/lnk 视为软件；其余文件（非文档类，已过滤）按文件条目处理
                    string ext = Path.GetExtension(id).ToLowerInvariant();
                    if (ext != ".exe" && ext != ".lnk") app.IsFile = true;
                }
                else if (!string.IsNullOrEmpty(id))
                {
                    app.IsUwp = true;
                    app.TargetPath = AppsFolderPrefix + id;
                }
                else
                {
                    continue;   // 无解析路径，无法启动
                }
                MergeByName(dict, app);
            }
            return items.Count;
        }

        /// <summary>
        /// 枚举系统「全部应用」列表，返回 (显示名, 解析路径) 数组。
        /// 打包/UWP 应用的解析路径为 AppUserModelID（如 Chrome、cn.apifox.app）；
        /// 传统桌面应用的解析路径为可执行文件路径。失败时返回空列表。
        /// </summary>
        private static List<string[]> EnumerateAppsFolder()
        {
            List<string[]> result = new List<string[]>();
            object shell = null;
            object folder = null;
            object items = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return result;
                shell = Activator.CreateInstance(shellType);
                folder = shellType.InvokeMember("NameSpace", BindingFlags.InvokeMethod, null, shell,
                    new object[] { "shell:AppsFolder" });
                if (folder == null) return result;
                Type folderType = folder.GetType();
                items = folderType.InvokeMember("Items", BindingFlags.InvokeMethod, null, folder, null);
                if (items == null) return result;
                Type itemsType = items.GetType();
                int count = Convert.ToInt32(itemsType.InvokeMember("Count", BindingFlags.GetProperty, null, items, null),
                    CultureInfo.InvariantCulture);
                for (int i = 0; i < count; i++)
                {
                    object item = null;
                    try
                    {
                        item = itemsType.InvokeMember("Item", BindingFlags.InvokeMethod, null, items, new object[] { i });
                        if (item == null) continue;
                        Type itemType = item.GetType();
                        string name = Convert.ToString(itemType.InvokeMember("Name", BindingFlags.GetProperty, null, item, null),
                            CultureInfo.InvariantCulture);
                        string id = Convert.ToString(itemType.InvokeMember("Path", BindingFlags.GetProperty, null, item, null),
                            CultureInfo.InvariantCulture);
                        if (!string.IsNullOrEmpty(name)) result.Add(new string[] { name, id ?? string.Empty });
                    }
                    finally
                    {
                        ReleaseCom(item);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("枚举 AppsFolder 失败: " + ex.Message);
            }
            finally
            {
                // 逐个释放 COM 引用，避免枚举数百条目时 RCW 堆积（原实现仅释放 shell）
                ReleaseCom(items);
                ReleaseCom(folder);
                ReleaseCom(shell);
            }
            return result;
        }

        /// <summary>释放单个 COM 引用；非 COM 对象或释放失败时静默忽略。</summary>
        private static void ReleaseCom(object o)
        {
            if (o == null) return;
            try { Marshal.ReleaseComObject(o); }
            catch { }
        }

        // 明显非应用项（文档/帮助/网址/卸载器等）名称片段
        private static readonly string[] NonAppNameFragments = new string[]
        {
            "release note", "faq", "manual", "documentation", "readme", "website",
            "getting started", "install additional", "uninstall", "卸载", "帮助", "使用说明", "说明文档"
        };

        /// <summary>
        /// 判断「全部应用」条目是否为明显非应用项：虚拟项（此电脑等）、网址、文档、卸载器等。
        /// 这些虽出现在系统列表中，但不属于可启动的软件，应在软件列表中剔除。
        /// </summary>
        internal static bool IsNonAppEntry(string name, string path)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (!string.IsNullOrEmpty(path) && path.StartsWith("::", StringComparison.Ordinal)) return true;
            if (IsUrl(name) || IsUrl(path)) return true;
            string n = name.ToLowerInvariant();
            if (n.StartsWith("卸载") || n.StartsWith("uninstall")) return true;
            if (IsDocumentPath(path)) return true;
            for (int i = 0; i < NonAppNameFragments.Length; i++)
                if (n.IndexOf(NonAppNameFragments[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>是否为文档/说明类文件路径（.html/.txt 等），供非应用项判定使用。</summary>
        private static bool IsDocumentPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".html": case ".htm": case ".txt": case ".pdf": case ".chm":
                case ".doc": case ".docx": case ".rtf": case ".md": case ".log":
                    return true;
            }
            return false;
        }

        // ===== 注册表（仅作信息补充） =====

        /// <summary>
        /// 从注册表卸载键读取卸载/安装/图标信息，按名称合并到已有条目；注册表不再作为独立来源新增条目。
        /// </summary>
        private void EnrichFromRegistry(Dictionary<string, AppInfo> dict)
        {
            if (dict.Count == 0) return;
            Dictionary<string, AppInfo> byName = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
            RegistryHive[] hives = new RegistryHive[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser };
            // 64 位系统：Registry64 视图下两个路径已分别覆盖 64/32 位卸载键，
            // 再开 Registry32 视图只是重复枚举同一批键（32 位视图 + 普通路径 ≡ 64 位视图 + WOW6432Node）
            RegistryView[] views = Environment.Is64BitOperatingSystem
                ? new RegistryView[] { RegistryView.Registry64 }
                : new RegistryView[] { RegistryView.Registry32 };
            string[] paths = Environment.Is64BitOperatingSystem
                ? new string[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                }
                : new string[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" };
            foreach (RegistryHive hive in hives)
            {
                foreach (RegistryView view in views)
                {
                    using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                    {
                        foreach (string p in paths)
                            CollectUninstallKey(baseKey, p, byName);
                    }
                }
            }
            foreach (AppInfo exist in dict.Values)
            {
                string norm = NormalizeEntryName(exist.Name);
                if (norm.Length == 0) continue;
                AppInfo reg;
                if (!byName.TryGetValue(norm, out reg)) continue;
                if (string.IsNullOrEmpty(exist.UninstallString)) exist.UninstallString = reg.UninstallString;
                if (string.IsNullOrEmpty(exist.InstallLocation)) exist.InstallLocation = reg.InstallLocation;
                if (string.IsNullOrEmpty(exist.DisplayIcon)) exist.DisplayIcon = reg.DisplayIcon;
            }
        }

        /// <summary>读取卸载键，按归一化名称收集卸载/安装/图标信息（同名以先到者为准）。</summary>
        private void CollectUninstallKey(RegistryKey root, string sub, Dictionary<string, AppInfo> byName)
        {
            RegistryKey key;
            try { key = root.OpenSubKey(sub); }
            catch { return; }
            if (key == null) return;
            using (key)
            {
                string[] names;
                try { names = key.GetSubKeyNames(); }
                catch { return; }
                foreach (string childName in names)
                {
                    RegistryKey child;
                    try { child = key.OpenSubKey(childName); }
                    catch { continue; }
                    if (child == null) continue;
                    using (child)
                    {
                        string name = SafeStr(child.GetValue("DisplayName"));
                        if (string.IsNullOrEmpty(name)) continue;
                        // 跳过 Windows 更新/补丁与系统组件
                        if (childName.StartsWith("KB", StringComparison.OrdinalIgnoreCase) && IsUpdate(name)) continue;
                        if (SafeStr(child.GetValue("SystemComponent")) == "1") continue;
                        string norm = NormalizeEntryName(name);
                        if (norm.Length == 0 || byName.ContainsKey(norm)) continue;

                        AppInfo app = new AppInfo();
                        app.Name = name;
                        app.Source = "Registry";
                        app.DisplayIcon = SafeStr(child.GetValue("DisplayIcon"));
                        app.InstallLocation = SafeStr(child.GetValue("InstallLocation"));
                        app.UninstallString = SafeStr(child.GetValue("UninstallString"));
                        byName[norm] = app;
                    }
                }
            }
        }

        /// <summary>归一化名称：转小写并剔除非字母数字字符，便于宽松匹配。</summary>
        private static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            StringBuilder sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = char.ToLowerInvariant(name[i]);
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c > 127)
                    sb.Append(c);
            }
            return sb.ToString();
        }

        // ===== 开始菜单 =====

        private void ScanStartMenu(Dictionary<string, AppInfo> dict)
        {
            string user = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
            ScanShortcuts(user, dict, "StartMenu", true);
            ScanShortcuts(common, dict, "StartMenu", true);
        }

        /// <summary>
        /// 扫描目录内快捷方式并收录，按名称去重（同名合并且保留有效启动目标），
        /// 避免同一软件经桌面/开始菜单/收纳目录等多来源重复出现。
        /// </summary>
        private void ScanShortcuts(string folder, Dictionary<string, AppInfo> dict, string source, bool recursive)
        {
            if (!Directory.Exists(folder)) return;
            string[] files;
            try { files = Directory.GetFiles(folder, "*.*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly); }
            catch { return; }
            foreach (string f in files)
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".lnk") continue;
                AppInfo app = ResolveShortcut(f, source);
                if (app == null) continue;
                // 失效剔除：目标不存在则剔除；UWP 恒有效，协议定位串（如 steam://）恒有效
                if (!IsAliveTarget(app.TargetPath, app.IsUwp)) continue;
                if (string.IsNullOrEmpty(app.Category)) app.Category = CategoryRules.Classify(app.Name);
                MergeByName(dict, app);
            }
        }

        private AppInfo ResolveShortcut(string path, string source)
        {
            try
            {
                IShellLinkW link = (IShellLinkW)new CShellLink();
                IPersistFile pf = (IPersistFile)link;
                pf.Load(path, 0);

                StringBuilder sb = new StringBuilder(260);
                link.GetPath(sb, 260, IntPtr.Zero, 0);
                string target = sb.ToString().Trim();

                StringBuilder args = new StringBuilder(260);
                link.GetArguments(args, 260);
                string a = args.ToString();

                StringBuilder iconLoc = new StringBuilder(260);
                int iconIdx;
                link.GetIconLocation(iconLoc, 260, out iconIdx);
                string iconPath = iconLoc.ToString();

                AppInfo app = new AppInfo();
                app.ShortcutPath = path;
                app.Name = Path.GetFileNameWithoutExtension(path);
                app.Source = source;
                app.IsDesktop = (source == "Desktop");
                app.Key = "lnk:" + path;

                if (string.IsNullOrEmpty(target))
                {
                    // AppsFolder/UWP：目标为空，视为 UWP，不剔除
                    app.IsUwp = true;
                    app.TargetPath = string.Empty;
                    app.DisplayIcon = path;
                }
                else
                {
                    app.TargetPath = target;
                    string tlow = target.ToLowerInvariant();
                    if (tlow.EndsWith("explorer.exe") && a.IndexOf("shell:appsfolder", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        app.IsUwp = true;
                        app.DisplayIcon = path;
                    }
                    else
                    {
                        // 区分软件 / 文件 / 文件夹，供右键菜单与失效判定使用
                        if (Directory.Exists(target)) { app.IsFile = true; app.IsFolder = true; }
                        else if (!tlow.EndsWith(".exe")) { app.IsFile = true; }

                        if (!string.IsNullOrEmpty(iconPath)) app.DisplayIcon = iconPath + "," + iconIdx;
                        else app.DisplayIcon = target;
                    }
                }
                return app;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("解析快捷方式失败 " + path + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 判断条目目标是否有效：UWP 恒有效；协议定位串（含 "://"，如 http://、steam://）恒有效；
        /// 文件/文件夹需真实存在。
        /// 说明：协议快捷方式的目标不是磁盘路径，若按 File.Exists 判定会被误判失效而从列表剔除，
        /// 故对任何含 "://" 的目标一律放行。
        /// </summary>
        internal static bool IsAliveTarget(string target, bool isUwp)
        {
            if (isUwp) return true;
            if (string.IsNullOrEmpty(target)) return false;
            if (target.IndexOf("://", StringComparison.Ordinal) >= 0) return true;
            return File.Exists(target) || Directory.Exists(target);
        }

        /// <summary>是否为 http/https/ftp 网址字符串（供非应用项过滤与显示兜底使用）。</summary>
        internal static bool IsUrl(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 模糊匹配：先「归一化包含」再「子序列跳字」（容错拼写不完整）。
        /// 归一化会剔除非字母数字字符（保留中日韩等），使「vscode」「vs code」「vs-code」等价，
        /// 因此「vscode」可搜到「Visual Studio Code」。空关键词恒命中，不区分大小写。
        /// </summary>
        internal static bool FuzzyMatch(string text, string keyword)
        {
            if (string.IsNullOrEmpty(keyword)) return true;
            if (string.IsNullOrEmpty(text)) return false;
            string k = NormalizeName(keyword);
            if (k.Length == 0) return true;      // 关键词仅由标点/空白组成，视为无约束
            string t = NormalizeName(text);
            if (t.Length == 0) return false;
            if (t.IndexOf(k, StringComparison.Ordinal) >= 0) return true;
            int ti = 0;
            for (int ki = 0; ki < k.Length; ki++)
            {
                int found = t.IndexOf(k[ki], ti);
                if (found < 0) return false;
                ti = found + 1;
            }
            return true;
        }

        // ===== 桌面 =====

        private void ScanDesktop(Dictionary<string, AppInfo> dict)
        {
            string desk = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            // 桌面只扫顶层：递归遍历会把用户放在桌面的工程目录整棵扫进来，耗时且产生噪音条目
            ScanShortcuts(desk, dict, "Desktop", false);
            ScanShortcuts(common, dict, "Desktop", false);
        }

        private void ScanDesktopBackup(Dictionary<string, AppInfo> dict)
        {
            // 新版收纳目录 + 旧版 DesktopStash（升级后旧收纳的快捷方式也要能在快捷中心看到）
            if (Directory.Exists(Paths.DesktopBackupDir))
                ScanShortcuts(Paths.DesktopBackupDir, dict, "Desktop", true);
            if (Directory.Exists(Paths.LegacyStashDir))
                ScanShortcuts(Paths.LegacyStashDir, dict, "Desktop", true);
        }

        // ===== 去重与配置 =====

        private void Merge(Dictionary<string, AppInfo> dict, AppInfo app)
        {
            string k = app.DisplayKey();
            AppInfo exist;
            if (dict.TryGetValue(k, out exist))
            {
                if (string.IsNullOrEmpty(exist.UninstallString) && !string.IsNullOrEmpty(app.UninstallString))
                    exist.UninstallString = app.UninstallString;
                if (string.IsNullOrEmpty(exist.InstallLocation) && !string.IsNullOrEmpty(app.InstallLocation))
                    exist.InstallLocation = app.InstallLocation;
                if (string.IsNullOrEmpty(exist.DisplayIcon) && !string.IsNullOrEmpty(app.DisplayIcon))
                    exist.DisplayIcon = app.DisplayIcon;
            }
            else
            {
                dict[k] = app;
                RegisterNameIndex(app);
            }
        }

        /// <summary>
        /// 按名称收录（收纳目录专用）：与已有条目同名（忽略大小写与重名后缀）时不新增条目，
        /// 仅把缺失的卸载/安装信息与桌面来源标记合并到已有条目，避免同一软件出现两条。
        /// </summary>
        private void MergeByName(Dictionary<string, AppInfo> dict, AppInfo app)
        {
            string name = NormalizeEntryName(app.Name);
            if (name.Length > 0)
            {
                AppInfo exist;
                if (nameIndex != null && nameIndex.TryGetValue(name, out exist))
                {
                    if (string.IsNullOrEmpty(exist.UninstallString) && !string.IsNullOrEmpty(app.UninstallString))
                        exist.UninstallString = app.UninstallString;
                    if (string.IsNullOrEmpty(exist.InstallLocation) && !string.IsNullOrEmpty(app.InstallLocation))
                        exist.InstallLocation = app.InstallLocation;
                    if (string.IsNullOrEmpty(exist.DisplayIcon) && !string.IsNullOrEmpty(app.DisplayIcon))
                        exist.DisplayIcon = app.DisplayIcon;
                    // 收纳项仍应能在「快捷中心」分类中看到
                    if (app.IsDesktop) exist.IsDesktop = true;
                    return;
                }
            }
            dict[app.DisplayKey()] = app;
            RegisterNameIndex(app);
        }

        /// <summary>把条目登记到名称去重索引（首个同名条目优先，与 MergeByName 的顺序语义一致）。</summary>
        private void RegisterNameIndex(AppInfo app)
        {
            if (nameIndex == null || app == null) return;
            string name = NormalizeEntryName(app.Name);
            if (name.Length > 0 && !nameIndex.ContainsKey(name)) nameIndex[name] = app;
        }

        /// <summary>条目名称归一化（去重比较用）：先剔除重名后缀，再转小写去符号。</summary>
        internal static string NormalizeEntryName(string name)
        {
            return NormalizeName(StripIndexSuffix(name));
        }

        /// <summary>两个名称按收纳去重规则是否视为同一条目。</summary>
        internal static bool IsSameEntryName(string a, string b)
        {
            string na = NormalizeEntryName(a);
            return na.Length > 0 && na == NormalizeEntryName(b);
        }

        /// <summary>
        /// 去除重名时系统追加的序号后缀：如「名称 (2)」「名称_1」「名称-1」。
        /// 注意：不处理「名称 1」（空格+数字）——该形式基本是软件版本号（如 Navicat Premium 16），
        /// 裁剪会把不同版本误判为同一条目并合并，导致软件丢失。
        /// 仅用于显示与去重比较，不改变备份清单与还原逻辑；无后缀时原样返回。
        /// </summary>
        internal static string StripIndexSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            string s = name.Trim();

            // 形如 "名称 (2)" / "名称(2)"
            if (s.EndsWith(")", StringComparison.Ordinal))
            {
                int open = s.LastIndexOf('(');
                if (open > 0)
                {
                    string inner = s.Substring(open + 1, s.Length - open - 2).Trim();
                    if (IsDigits(inner))
                    {
                        string stem = s.Substring(0, open).TrimEnd();
                        if (stem.Length > 0) return stem;
                    }
                }
                return s;
            }

            // 形如 "名称_1" / "名称-1"
            int i = s.Length;
            while (i > 0 && s[i - 1] >= '0' && s[i - 1] <= '9') i--;
            if (i > 0 && i < s.Length)
            {
                char sep = s[i - 1];
                if (sep == '_' || sep == '-')
                {
                    string stem = s.Substring(0, i - 1).TrimEnd();
                    if (stem.Length > 0) return stem;
                }
            }
            return s;
        }

        private static bool IsDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
                if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }

        internal void ApplyConfig(List<AppInfo> list)
        {
            foreach (AppInfo a in list)
            {
                // 旧版配置用「名称归一化 id」记录，导入时写成 af:<id>；路径键对不上时按名称回退匹配
                string legacyKey = "af:" + NormalizeEntryName(a.Name);
                if (config.Favorites.Contains(a.Key) || config.Favorites.Contains(legacyKey)) a.IsFavorite = true;
                if (config.Hidden.Contains(a.Key) || config.Hidden.Contains(legacyKey)) a.IsHidden = true;
                string ov;
                if (config.CategoryOverride.TryGetValue(a.Key, out ov) && !string.IsNullOrEmpty(ov))
                    a.Category = ov;
                else if (config.CategoryOverride.TryGetValue(legacyKey, out ov) && !string.IsNullOrEmpty(ov))
                    a.Category = ov;
            }
        }

        private static string SafeStr(object v)
        {
            return v == null ? string.Empty : v.ToString().Trim();
        }

        private static bool IsUpdate(string name)
        {
            string n = name.ToLowerInvariant();
            return n.Contains("update") || n.Contains("security") || n.Contains("修补") || n.Contains("更新");
        }
    }

    /// <summary>
    /// 扫描结果磁盘缓存：把最近一次全量扫描的条目写到 scan.cache.xml，
    /// 启动时先展示缓存数据再后台重扫，避免首屏长时间空等。
    /// 写入走 .tmp + File.Replace 原子替换；读取失败直接放弃，不影响正常启动。
    /// 收藏/隐藏/分类覆盖等配置不在这里持久化，加载后由 Scanner.ApplyConfig 重新套用。
    /// </summary>
    internal static class ScanCache
    {
        private static string CachePath { get { return Path.Combine(Paths.DataDir, "scan.cache.xml"); } }

        public static List<AppInfo> Load()
        {
            List<AppInfo> list = new List<AppInfo>();
            try
            {
                string path = CachePath;
                if (!File.Exists(path)) return list;
                XmlDocument doc = new XmlDocument();
                doc.Load(path);
                XmlNodeList nodes = doc.SelectNodes("/scan/app");
                if (nodes == null) return list;
                foreach (XmlNode node in nodes)
                {
                    XmlElement el = node as XmlElement;
                    if (el == null) continue;
                    AppInfo a = new AppInfo();
                    a.Name = el.GetAttribute("name");
                    a.TargetPath = el.GetAttribute("target");
                    a.ShortcutPath = el.GetAttribute("shortcut");
                    a.InstallLocation = el.GetAttribute("install");
                    a.UninstallString = el.GetAttribute("uninstall");
                    a.DisplayIcon = el.GetAttribute("icon");
                    a.Category = el.GetAttribute("category");
                    a.Source = el.GetAttribute("source");
                    a.Key = el.GetAttribute("key");
                    a.IsUwp = el.GetAttribute("uwp") == "1";
                    a.IsDesktop = el.GetAttribute("desktop") == "1";
                    a.IsFile = el.GetAttribute("file") == "1";
                    a.IsFolder = el.GetAttribute("folder") == "1";
                    if (!string.IsNullOrEmpty(a.Name)) list.Add(a);
                }
            }
            catch (Exception ex) { Paths.LogError(ex); list.Clear(); }
            return list;
        }

        public static void Save(List<AppInfo> list)
        {
            if (list == null) return;
            try
            {
                string path = CachePath;
                string tmp = path + ".tmp";
                XmlWriterSettings settings = new XmlWriterSettings();
                settings.Indent = false;
                settings.Encoding = new UTF8Encoding(false);
                using (XmlWriter w = XmlWriter.Create(tmp, settings))
                {
                    w.WriteStartDocument();
                    w.WriteStartElement("scan");
                    foreach (AppInfo a in list)
                    {
                        if (a == null || string.IsNullOrEmpty(a.Name)) continue;
                        w.WriteStartElement("app");
                        w.WriteAttributeString("name", a.Name);
                        if (!string.IsNullOrEmpty(a.TargetPath)) w.WriteAttributeString("target", a.TargetPath);
                        if (!string.IsNullOrEmpty(a.ShortcutPath)) w.WriteAttributeString("shortcut", a.ShortcutPath);
                        if (!string.IsNullOrEmpty(a.InstallLocation)) w.WriteAttributeString("install", a.InstallLocation);
                        if (!string.IsNullOrEmpty(a.UninstallString)) w.WriteAttributeString("uninstall", a.UninstallString);
                        if (!string.IsNullOrEmpty(a.DisplayIcon)) w.WriteAttributeString("icon", a.DisplayIcon);
                        if (!string.IsNullOrEmpty(a.Category)) w.WriteAttributeString("category", a.Category);
                        if (!string.IsNullOrEmpty(a.Source)) w.WriteAttributeString("source", a.Source);
                        if (!string.IsNullOrEmpty(a.Key)) w.WriteAttributeString("key", a.Key);
                        if (a.IsUwp) w.WriteAttributeString("uwp", "1");
                        if (a.IsDesktop) w.WriteAttributeString("desktop", "1");
                        if (a.IsFile) w.WriteAttributeString("file", "1");
                        if (a.IsFolder) w.WriteAttributeString("folder", "1");
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                    w.WriteEndDocument();
                }
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception ex) { Paths.LogError(ex); }
        }
    }
}
