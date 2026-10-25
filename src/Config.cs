using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace AppManager
{
    /// <summary>统一管理运行期路径（数据目录 / 配置 / 收纳目录 / 备份目录 / 日志）。</summary>
    internal static class Paths
    {
        public static string DataDir
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppManager");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string ConfigFile { get { return Path.Combine(DataDir, "config.xml"); } }

        /// <summary>新版桌面收纳目录。</summary>
        public static string DesktopBackupDir { get { return Path.Combine(DataDir, "DesktopBackup"); } }

        /// <summary>旧版桌面收纳目录（迁移前的 DesktopStash），备份时一并快照。</summary>
        public static string LegacyStashDir { get { return Path.Combine(DataDir, "DesktopStash"); } }

        /// <summary>布局备份根目录。</summary>
        public static string BackupsDir
        {
            get
            {
                string dir = Path.Combine(DataDir, "Backups");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string LogFile { get { return Path.Combine(DataDir, "error.log"); } }

        /// <summary>异常写入 error.log，绝不抛出。</summary>
        public static void LogError(Exception ex)
        {
            try
            {
                string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}\r\n{2}\r\n\r\n",
                    DateTime.Now, ex.Message, ex);
                File.AppendAllText(LogFile, line, System.Text.Encoding.UTF8);
            }
            catch { }
        }
    }

    /// <summary>
    /// 桌面收纳备份条目：记录原路径、当前路径、是否快捷方式，用于一键还原。
    /// </summary>
    public class DesktopBackupEntry
    {
        public string OriginalPath = string.Empty;
        public string CurrentPath = string.Empty;
        public bool IsShortcut = true;
        // 收纳对象类型与处理方式：目录条目、副本模式（false=搬家移动，true=副本拷贝）
        public bool IsDir = false;
        public bool IsCopy = false;
    }

    /// <summary>
    /// 用户手动添加的条目：记录路径（或网址）、归属分类、是否为文件夹/网址，
    /// 以及可选的显示名（改名）。用于分类/自定义/快捷中心下增删。
    /// </summary>
    public class AddedEntry
    {
        public string Path = string.Empty;
        public string Category = string.Empty;
        public bool IsFolder;
        public bool IsUrl;      // 网址条目（Path 存 URL，不做磁盘校验）
        public string Name = string.Empty;   // 用户改过的显示名，空则按路径/主机名推导
    }

    /// <summary>
    /// 配置持久化：收藏置顶、隐藏项、自定义分类、开关、用户添加条目、桌面收纳备份清单。
    /// 采用 XML 单文件存储，读取容错（缺失字段回退默认值），异常仅写日志不抛出。
    /// </summary>
    public class AppConfig
    {
        // 开关：视图模式 + 显示隐藏 + 显示全部已安装 + 启动扫描
        public bool ViewGrid = true;
        public bool ShowHidden = false;
        public bool ShowAllInstalled = false;
        public bool AutoScan = true;
        // 关闭主窗口行为：false=彻底退出（默认），true=最小化到系统托盘
        public bool CloseToTray = false;

        public List<string> Favorites = new List<string>();
        public List<string> Hidden = new List<string>();
        public List<string> CustomCategories = new List<string>();
        // 资源模式自定义分类（照片/视频/音乐/书签之外用户自建）
        public List<string> ResourceCategories = new List<string>();
        public Dictionary<string, string> CategoryOverride = new Dictionary<string, string>();
        public List<AddedEntry> AddedItems = new List<AddedEntry>();
        public List<DesktopBackupEntry> DesktopBackup = new List<DesktopBackupEntry>();

        // 每分类独立视图模式：分类名 -> true(网格)/false(列表)，缺失则用 ViewGrid 兜底
        public Dictionary<string, bool> CategoryViewMode = new Dictionary<string, bool>();
        // 管理根路径：文件夹/文件搬入时的目标根目录（默认 H:\Manage，不可用时回退本地收纳目录）
        public string ManageRootPath = string.Empty;
        // 文件/文件夹各自的存储位置（留空时按 ManageRootPath 与 ManageRootPath\Files 兜底）
        public string FolderStorePath = string.Empty;
        public string FileStorePath = string.Empty;

        // 拖动排序持久化：分类名 -> 有序稳定键列表（AppInfo.Key），各分类独立
        public Dictionary<string, List<string>> ItemOrder = new Dictionary<string, List<string>>();
        // 额外归属分类（拖放「加入分类」）：条目 Key -> 额外加入的分类名集合；原分类与存储位置均不变
        public Dictionary<string, List<string>> ItemCategories = new Dictionary<string, List<string>>();
        // 侧栏中间区块分类顺序（内置分类 + 自定义分类名）；与默认顺序不一致时按此渲染并自愈回写
        public List<string> SidebarOrder = new List<string>();
        // 桌面整理模式：true=副本（保留原件），false=搬家（移动，默认）
        public bool OrganizeCopy = false;
        // 桌面整理范围：快捷方式 / 文件 / 文件夹（默认全选）
        public bool OrganizeShortcut = true;
        public bool OrganizeFile = true;
        public bool OrganizeDir = true;

        // ===== 旧版 config.xml（<appmanager> 属性式）导入标记 =====
        // 旧版以「名称归一化 id」标识条目，导入时统一写成 af:<id>，扫描时再按名称回退匹配。
        internal bool LoadedLegacy;

        private static string Dir { get { return Paths.DataDir; } }
        private static string FilePath { get { return Paths.ConfigFile; } }

        /// <summary>加载配置；文件不存在或损坏时返回默认实例，绝不抛出。</summary>
        public static AppConfig Load()
        {
            return LoadFrom(FilePath);
        }

        /// <summary>从指定文件加载配置；文件不存在或损坏时返回默认实例，绝不抛出。</summary>
        public static AppConfig LoadFrom(string filePath)
        {
            AppConfig cfg = new AppConfig();
            try
            {
                if (!File.Exists(filePath)) return cfg;
                XmlDocument doc = new XmlDocument();
                doc.Load(filePath);
                XmlNode root = doc.DocumentElement;
                if (root == null) return cfg;

                // 旧版配置（<appmanager version="1">，属性式）：导入到新版模型，避免升级后分类/收藏丢失
                if (string.Equals(root.Name, "appmanager", StringComparison.OrdinalIgnoreCase))
                {
                    LoadLegacy(root, cfg);
                    cfg.LoadedLegacy = true;
                    BackupLegacyFile(filePath);
                    return cfg;
                }

                cfg.ViewGrid = XmlElementBool(root, "ViewGrid", true);
                cfg.ShowHidden = XmlElementBool(root, "ShowHidden", false);
                cfg.ShowAllInstalled = XmlElementBool(root, "ShowAllInstalled", false);
                cfg.AutoScan = XmlElementBool(root, "AutoScan", true);
                cfg.CloseToTray = XmlElementBool(root, "CloseToTray", false);

                foreach (XmlNode n in root.SelectNodes("Favorites/Item"))
                    AddUnique(cfg.Favorites, n.InnerText);
                foreach (XmlNode n in root.SelectNodes("Hidden/Item"))
                    AddUnique(cfg.Hidden, n.InnerText);
                foreach (XmlNode n in root.SelectNodes("CustomCategories/Item"))
                    AddUnique(cfg.CustomCategories, n.InnerText);
                foreach (XmlNode n in root.SelectNodes("ResourceCategories/Item"))
                    AddUnique(cfg.ResourceCategories, n.InnerText);
                foreach (XmlNode n in root.SelectNodes("CategoryOverrides/Item"))
                {
                    string k = XmlAttr(n, "key");
                    if (!string.IsNullOrEmpty(k)) cfg.CategoryOverride[k] = n.InnerText;
                }
                foreach (XmlNode n in root.SelectNodes("CategoryViewModes/Item"))
                {
                    string k = XmlAttr(n, "cat");
                    bool v;
                    if (!string.IsNullOrEmpty(k) && bool.TryParse(n.InnerText, out v))
                        cfg.CategoryViewMode[k] = v;
                }
                cfg.ManageRootPath = XmlElementText(root, "ManageRootPath", string.Empty);
                cfg.FolderStorePath = XmlElementText(root, "FolderStorePath", string.Empty);
                cfg.FileStorePath = XmlElementText(root, "FileStorePath", string.Empty);
                foreach (XmlNode n in root.SelectNodes("AddedItems/Item"))
                {
                    AddedEntry e = new AddedEntry();
                    e.Path = XmlAttr(n, "path");
                    e.Category = XmlAttr(n, "category");
                    e.IsFolder = XmlAttributeBool(n, "folder", false);
                    e.IsUrl = XmlAttributeBool(n, "url", false);
                    e.Name = XmlAttr(n, "name");
                    if (!string.IsNullOrEmpty(e.Path)) cfg.AddedItems.Add(e);
                }
                foreach (XmlNode n in root.SelectNodes("DesktopBackup/Item"))
                {
                    DesktopBackupEntry e = new DesktopBackupEntry();
                    e.OriginalPath = XmlAttr(n, "path");
                    e.CurrentPath = XmlAttr(n, "current");
                    e.IsShortcut = XmlAttributeBool(n, "shortcut", true);
                    e.IsDir = XmlAttributeBool(n, "dir", false);
                    e.IsCopy = XmlAttributeBool(n, "copy", false);
                    cfg.DesktopBackup.Add(e);
                }
                foreach (XmlNode n in root.SelectNodes("ItemOrders/Item"))
                {
                    string cat = XmlAttr(n, "cat");
                    if (string.IsNullOrEmpty(cat)) continue;
                    List<string> keys = new List<string>();
                    foreach (XmlNode k in n.SelectNodes("Key"))
                        if (!string.IsNullOrEmpty(k.InnerText)) keys.Add(k.InnerText);
                    cfg.ItemOrder[cat] = keys;
                }
                foreach (XmlNode n in root.SelectNodes("ItemCategories/Item"))
                {
                    string k = XmlAttr(n, "key");
                    if (string.IsNullOrEmpty(k)) continue;
                    List<string> cats = new List<string>();
                    foreach (XmlNode c in n.SelectNodes("Category"))
                        if (!string.IsNullOrEmpty(c.InnerText)) cats.Add(c.InnerText);
                    if (cats.Count > 0) cfg.ItemCategories[k] = cats;
                }
                foreach (XmlNode n in root.SelectNodes("SidebarOrder/Item"))
                    AddUnique(cfg.SidebarOrder, n.InnerText);
                cfg.OrganizeCopy = XmlElementBool(root, "OrganizeCopy", false);
                cfg.OrganizeShortcut = XmlElementBool(root, "OrganizeShortcut", true);
                cfg.OrganizeFile = XmlElementBool(root, "OrganizeFile", true);
                cfg.OrganizeDir = XmlElementBool(root, "OrganizeDir", true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("配置读取失败: " + ex.Message);
            }
            return cfg;
        }

        /// <summary>保存配置；目录不存在自动创建，异常仅写日志不抛出。</summary>
        public void Save()
        {
            SaveTo(FilePath);
        }

        /// <summary>
        /// 保存配置到指定文件；目录不存在自动创建，异常仅写日志不抛出。
        /// 采用「先写临时文件再原子替换」，避免写入中途崩溃/断电损坏原配置。
        /// </summary>
        public void SaveTo(string filePath)
        {
            try
            {
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string tmp = filePath + ".tmp";
                XmlWriterSettings s = new XmlWriterSettings();
                s.Indent = true;
                s.Encoding = new System.Text.UTF8Encoding(false);
                using (XmlWriter w = XmlWriter.Create(tmp, s))
                {
                    w.WriteStartElement("AppManagerConfig");
                    w.WriteElementString("ViewGrid", ViewGrid ? "true" : "false");
                    w.WriteElementString("ShowHidden", ShowHidden ? "true" : "false");
                    w.WriteElementString("ShowAllInstalled", ShowAllInstalled ? "true" : "false");
                    w.WriteElementString("AutoScan", AutoScan ? "true" : "false");
                    w.WriteElementString("CloseToTray", CloseToTray ? "true" : "false");
                    w.WriteElementString("ManageRootPath", ManageRootPath ?? string.Empty);
                    w.WriteElementString("FolderStorePath", FolderStorePath ?? string.Empty);
                    w.WriteElementString("FileStorePath", FileStorePath ?? string.Empty);
                    WriteList(w, "Favorites", Favorites);
                    WriteList(w, "Hidden", Hidden);
                    WriteList(w, "CustomCategories", CustomCategories);
                    WriteList(w, "ResourceCategories", ResourceCategories);
                    w.WriteStartElement("CategoryOverrides");
                    foreach (KeyValuePair<string, string> kv in CategoryOverride)
                    {
                        w.WriteStartElement("Item");
                        w.WriteAttributeString("key", kv.Key);
                        w.WriteString(kv.Value ?? string.Empty);
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                    w.WriteStartElement("CategoryViewModes");
                    foreach (KeyValuePair<string, bool> kv in CategoryViewMode)
                    {
                        w.WriteStartElement("Item");
                        w.WriteAttributeString("cat", kv.Key);
                        w.WriteString(kv.Value ? "true" : "false");
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                    w.WriteStartElement("AddedItems");
                    foreach (AddedEntry e in AddedItems)
                    {
                        w.WriteStartElement("Item");
                        w.WriteAttributeString("path", e.Path ?? string.Empty);
                        w.WriteAttributeString("category", e.Category ?? string.Empty);
                        w.WriteAttributeString("folder", e.IsFolder ? "true" : "false");
                        w.WriteAttributeString("url", e.IsUrl ? "true" : "false");
                        w.WriteAttributeString("name", e.Name ?? string.Empty);
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                    w.WriteStartElement("DesktopBackup");
                    foreach (DesktopBackupEntry e in DesktopBackup)
                    {
                        w.WriteStartElement("Item");
                        w.WriteAttributeString("path", e.OriginalPath ?? string.Empty);
                        w.WriteAttributeString("current", e.CurrentPath ?? string.Empty);
                        w.WriteAttributeString("shortcut", e.IsShortcut ? "true" : "false");
                        w.WriteAttributeString("dir", e.IsDir ? "true" : "false");
                        w.WriteAttributeString("copy", e.IsCopy ? "true" : "false");
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                    w.WriteStartElement("ItemOrders");
                    foreach (KeyValuePair<string, List<string>> kv in ItemOrder)
                    {
                        w.WriteStartElement("Item");
                        w.WriteAttributeString("cat", kv.Key);
                        foreach (string key in kv.Value)
                            w.WriteElementString("Key", key ?? string.Empty);
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                    w.WriteStartElement("ItemCategories");
                    foreach (KeyValuePair<string, List<string>> kv in ItemCategories)
                    {
                        w.WriteStartElement("Item");
                        w.WriteAttributeString("key", kv.Key);
                        foreach (string c in kv.Value)
                            w.WriteElementString("Category", c ?? string.Empty);
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                    WriteList(w, "SidebarOrder", SidebarOrder);
                    w.WriteElementString("OrganizeCopy", OrganizeCopy ? "true" : "false");
                    w.WriteElementString("OrganizeShortcut", OrganizeShortcut ? "true" : "false");
                    w.WriteElementString("OrganizeFile", OrganizeFile ? "true" : "false");
                    w.WriteElementString("OrganizeDir", OrganizeDir ? "true" : "false");
                    w.WriteEndElement();
                }
                ReplaceAtomic(tmp, filePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("配置保存失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 用临时文件原子替换目标文件：同卷优先 File.Replace（保留原文件属性并支持回滚），
        /// 目标不存在或替换失败时退化为移动。
        /// </summary>
        private static void ReplaceAtomic(string tmp, string target)
        {
            try
            {
                if (File.Exists(target)) File.Replace(tmp, target, null);
                else File.Move(tmp, target);
            }
            catch
            {
                try { if (File.Exists(target)) File.Delete(target); }
                catch { }
                File.Move(tmp, target);
            }
        }

        /// <summary>导出为备份包（.ambak，仅记录与设置，不含物理文件）。</summary>
        public void ExportTo(string filePath)
        {
            SaveTo(filePath);
        }

        /// <summary>把当前配置另存为带时间戳的快照副本，返回副本路径；失败返回 null。</summary>
        public string BackupSnapshot()
        {
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
                string bak = Path.Combine(Dir, "config.xml.bak-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                SaveTo(bak);
                return bak;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("配置快照失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 从备份包导入配置；根节点校验失败返回 null（调用方据此中止覆盖，避免脏数据）。
        /// </summary>
        public static AppConfig ImportFrom(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.Load(filePath);
                if (doc.DocumentElement == null || doc.DocumentElement.Name != "AppManagerConfig")
                    return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("备份包校验失败: " + ex.Message);
                return null;
            }
            return LoadFrom(filePath);
        }

        // ===== 旧版配置导入 =====

        /// <summary>
        /// 导入旧版 &lt;appmanager&gt; 属性式配置：设置、分类、收藏/隐藏、视图、排序、
        /// 自定义条目、桌面收纳清单与网站统一换成新版字段；旧版 id 统一加 af: 前缀，
        /// 扫描时若路径键对不上，再按名称归一化回退匹配（见 Scanner.ApplyConfig）。
        /// </summary>
        private static void LoadLegacy(XmlNode root, AppConfig cfg)
        {
            XmlNode settings = root.SelectSingleNode("settings");
            if (settings != null)
            {
                cfg.ShowAllInstalled = XmlAttributeBool(settings, "displayAll", false);
                cfg.ShowHidden = XmlAttributeBool(settings, "showHidden", false);
                cfg.AutoScan = XmlAttributeBool(settings, "autoscan", true);
                cfg.CloseToTray = XmlAttributeBool(settings, "closetotray", true);
                cfg.OrganizeCopy = XmlAttributeBool(settings, "organizecopy", false);
                cfg.OrganizeShortcut = XmlAttributeBool(settings, "organizescut", true);
                cfg.OrganizeFile = XmlAttributeBool(settings, "organizefile", true);
                cfg.OrganizeDir = XmlAttributeBool(settings, "organizedir", true);
                cfg.ViewGrid = !string.Equals(XmlAttr(settings, "view"), "list", StringComparison.OrdinalIgnoreCase);
                string manageRoot = XmlAttr(settings, "manageroot");
                if (manageRoot.Length > 0) cfg.ManageRootPath = manageRoot;
            }

            foreach (XmlNode n in root.SelectNodes("sidebarorder/item"))
            {
                string name = XmlAttr(n, "name");
                if (name.Length > 0) AddUnique(cfg.SidebarOrder, name);
            }

            foreach (XmlNode n in root.SelectNodes("categories/item"))
            {
                string id = XmlAttr(n, "id");
                string cat = XmlAttr(n, "category");
                if (id.Length > 0 && cat.Length > 0) cfg.CategoryOverride["af:" + id] = cat;
            }

            foreach (XmlNode n in root.SelectNodes("usercategories/item"))
            {
                string name = XmlAttr(n, "name");
                if (name.Length > 0) AddUnique(cfg.CustomCategories, name);
            }

            foreach (XmlNode n in root.SelectNodes("pinned/id"))
            {
                string id = (n.InnerText ?? string.Empty).Trim();
                if (id.Length > 0) AddUnique(cfg.Favorites, "af:" + id);
            }

            foreach (XmlNode n in root.SelectNodes("hidden/id"))
            {
                string id = (n.InnerText ?? string.Empty).Trim();
                if (id.Length > 0) AddUnique(cfg.Hidden, "af:" + id);
            }

            // 旧版「已移除」= 不再出现在列表里，对应新版的隐藏项
            foreach (XmlNode n in root.SelectNodes("removed/id"))
            {
                string id = (n.InnerText ?? string.Empty).Trim();
                if (id.Length > 0) AddUnique(cfg.Hidden, "af:" + id);
            }

            foreach (XmlNode n in root.SelectNodes("views/item"))
            {
                string scope = XmlAttr(n, "scope");
                if (scope.Length > 0 && !string.Equals(scope, "soft", StringComparison.OrdinalIgnoreCase)) continue;
                string cat = XmlAttr(n, "category");
                if (cat.Length == 0) continue;
                cfg.CategoryViewMode[cat] =
                    !string.Equals(XmlAttr(n, "mode"), "list", StringComparison.OrdinalIgnoreCase);
            }

            foreach (XmlNode v in root.SelectNodes("orders/view"))
            {
                string key = XmlAttr(v, "key");
                int bar = key.IndexOf('|');
                string scope = bar > 0 ? key.Substring(0, bar) : string.Empty;
                string cat = bar > 0 ? key.Substring(bar + 1) : key;
                if (cat.Length == 0) continue;
                if (scope.Length > 0 && !string.Equals(scope, "soft", StringComparison.OrdinalIgnoreCase)) continue;
                List<string> ids = new List<string>();
                foreach (XmlNode e in v.SelectNodes("id"))
                {
                    string id = (e.InnerText ?? string.Empty).Trim();
                    if (id.Length > 0) ids.Add("af:" + id);
                }
                if (ids.Count > 0) cfg.ItemOrder[cat] = ids;
            }

            // 旧版自定义条目（软件/文件/文件夹/网址）→ 新版「已添加条目」
            foreach (XmlNode e in root.SelectNodes("custom/entry"))
            {
                AddedEntry item = new AddedEntry();
                item.Name = XmlAttr(e, "name");
                item.Path = XmlAttr(e, "target");
                string kind = XmlAttr(e, "kind");
                item.IsFolder = string.Equals(kind, "folder", StringComparison.OrdinalIgnoreCase);
                item.IsUrl = string.Equals(kind, "url", StringComparison.OrdinalIgnoreCase);
                item.Category = XmlAttr(e, "category");
                if (item.Path.Length == 0) continue;
                if (item.Name.Length == 0) item.Name = Path.GetFileNameWithoutExtension(item.Path);
                cfg.AddedItems.Add(item);
            }

            // 旧版桌面收纳清单（文件相对 DesktopStash 目录）→ 新版还原清单
            foreach (XmlNode e in root.SelectNodes("stash/entry"))
            {
                DesktopBackupEntry en = new DesktopBackupEntry();
                string file = XmlAttr(e, "file");
                en.OriginalPath = XmlAttr(e, "original");
                en.IsDir = XmlAttributeBool(e, "dir", false);
                en.IsCopy = XmlAttributeBool(e, "copy", false);
                en.IsShortcut = !en.IsDir;
                if (file.Length == 0 || en.OriginalPath.Length == 0) continue;
                en.CurrentPath = Path.Combine(Paths.LegacyStashDir, file.Replace('/', '\\'));
                cfg.DesktopBackup.Add(en);
            }

            // 旧版网站条目 → 新版网址条目（全部网站分类）
            foreach (XmlNode e in root.SelectNodes("websites/entry"))
            {
                string url = XmlAttr(e, "url");
                if (url.Length == 0) continue;
                AddedEntry item = new AddedEntry();
                item.Path = url;
                item.IsUrl = true;
                item.Category = "全部网站";
                item.Name = XmlAttr(e, "name");
                if (item.Name.Length == 0) item.Name = DisplayHost(url);
                cfg.AddedItems.Add(item);
            }
        }

        /// <summary>首次导入旧版配置时，把原文件另存一份 config.legacy.xml 供回查。</summary>
        private static void BackupLegacyFile(string filePath)
        {
            try
            {
                string bak = Path.Combine(Paths.DataDir, "config.legacy.xml");
                if (!File.Exists(bak) && File.Exists(filePath)) File.Copy(filePath, bak, false);
            }
            catch { }
        }

        /// <summary>网址域名（去掉 www.），用于缺省显示名。</summary>
        private static string DisplayHost(string url)
        {
            try
            {
                Uri uri = new Uri(url);
                string host = uri.Host;
                if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host.Substring(4);
                return host.Length > 0 ? host : url;
            }
            catch { return url ?? string.Empty; }
        }

        // ===== 私有辅助 =====

        private static void AddUnique(List<string> list, string value)
        {
            if (!string.IsNullOrEmpty(value) && !list.Contains(value)) list.Add(value);
        }

        private static bool XmlElementBool(XmlNode parent, string name, bool def)
        {
            XmlNode n = parent.SelectSingleNode(name);
            if (n == null) return def;
            bool v;
            return bool.TryParse(n.InnerText, out v) ? v : def;
        }

        private static string XmlElementText(XmlNode parent, string name, string def)
        {
            XmlNode n = parent.SelectSingleNode(name);
            return n == null ? def : (n.InnerText ?? def);
        }

        private static bool XmlAttributeBool(XmlNode node, string attr, bool def)
        {
            string v = (node.Attributes != null && node.Attributes[attr] != null)
                ? node.Attributes[attr].Value : null;
            bool b;
            return v != null && bool.TryParse(v, out b) ? b : def;
        }

        private static string XmlAttr(XmlNode node, string attr)
        {
            return (node.Attributes != null && node.Attributes[attr] != null)
                ? node.Attributes[attr].Value : string.Empty;
        }

        private static void WriteList(XmlWriter w, string name, List<string> list)
        {
            w.WriteStartElement(name);
            foreach (string item in list)
            {
                w.WriteElementString("Item", item ?? string.Empty);
            }
            w.WriteEndElement();
        }
    }
}
