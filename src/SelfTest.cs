using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;

namespace AppManager
{
    /// <summary>
    /// 内置自测：覆盖目录/配置/分类/图标/扫描/去重/启动/收纳/网址/快捷中心/模糊匹配/改名/排序/备份导出导入/额外归属分类/全部应用过滤/AppsFolder 定位串/目录搬迁（同卷与跨卷）等关键路径。
    /// 结果写入 selftest.log，返回通过用例数。无 UI、无副作用（不实际收纳、不弹窗）。
    /// </summary>
    internal static class SelfTest
    {
        private static readonly string AppDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppManager");
        private static readonly string LogPath = Path.Combine(AppDir, "selftest.log");

        private static int _pass;
        private static int _total;
        private static StringBuilder _log = new StringBuilder();

        /// <summary>运行全部用例，返回通过数；同时落盘 selftest.log。</summary>
        private static readonly object RunLock = new object();
        private static bool _running;

        public static int Run()
        {
            lock (RunLock)
            {
                if (_running) return -1;   // 拒绝并发/重入：用例共享静态状态，互相污染会误判
                _running = true;
            }
            // 配置路径隔离：用例读写临时目录里的 config.xml，绝不触碰真实配置
            string prevOverride = Paths.ConfigFileOverride;
            string prevDataOverride = Paths.DataDirOverride;
            string isolatedDir = Path.Combine(Path.GetTempPath(), "AppManagerSelfTest_" + Guid.NewGuid().ToString("N"));
            Paths.DataDirOverride = isolatedDir;
            Paths.ConfigFileOverride = Path.Combine(isolatedDir, "config.xml");
            try
            {
                return RunCore();
            }
            finally
            {
                Paths.ConfigFileOverride = prevOverride;
                Paths.DataDirOverride = prevDataOverride;
                try { if (Directory.Exists(isolatedDir)) Directory.Delete(isolatedDir, true); } catch { }
                lock (RunLock) { _running = false; }
            }
        }

        private static int RunCore()
        {
            _pass = 0; _total = 0; _log = new StringBuilder();
            Log("==== AppManager 自测开始 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ====");

            Case01_DirWritable();
            Case02_ConfigDefault();
            Case03_ConfigRoundtrip();
            Case04_ClassifyDocker();
            Case05_ClassifyChrome();
            Case06_ClassifyWechat();
            Case07_ClassifyUnknown();
            Case08_IconExtractNotepad();
            Case09_TransparentInvalid();
            Case10_ScanNonEmpty();
            Case11_ScanDedup();
            Case12_IconPlaceholderFallback();
            Case13_LauncherNullGuard();
            Case14_DesktopBackupDir();
            Case15_BuiltInCount();
            Case16_NonAppEntryFilter();
            Case17_IconExtractSize();
            Case18_ClassifyPriority();
            Case19_AddedItemsRoundtrip();
            Case20_IsAliveTarget();
            Case21_StripIndexSuffix();
            Case22_NameDedupRule();
            Case23_IconUrlFile();
            Case24_IconFolder();
            Case25_IconWebsite();
            Case27_QuickCenterDir();
            Case28_FuzzyMatch();
            Case29_CategoryViewModeRoundtrip();
            Case30_AddedEntryNameRoundtrip();
            Case31_ItemOrderRoundtrip();
            Case32_DesktopBackupEntryRoundtrip();
            Case33_OrganizeFlagsRoundtrip();
            Case34_ExportImportRoundtrip();
            Case35_ImportInvalidGuard();
            Case36_AppsFolderTarget();
            Case37_MoveDirectory();
            Case38_MoveDirectoryCrossVolume();
            Case39_CloseToTrayRoundtrip();
            Case40_NormalizeRootAndAtomicSave();
            Case41_IconCacheClone();
            Case42_UniqueDestSuffix();
            Case43_MoveIntoFolderRules();
            Case44_DeletePath();
            Case45_ResourceCategoryType();
            Case46_CollectionKeysAndRoundtrip();
            Case47_CollectionOpsMembers();
            Case48_CollectionParentSync();
            Case49_MediaTypeRules();
            Case49_WicPngDecode();
            Case50_CorruptConfigProtection();
            Case51_LayoutRestoreRollback();
            Case52_SaveFailureKeepsOriginal();
            Case53_TakenDatePhotoExif();
            Case54_TakenDateVideoMvhd();
            Case55_MusicLyrics();
            Case56_MusicMetadata();
            Case57_MusicMenuGrouping();
            Case58_CategoryIconKeys();
            Case59_LyricsReadLimit();
            Case60_PathBoundaryNormalization();
            Case61_RestorePathBoundary();
            Case62_XmlDtdRejected();
            Case63_DpiScaling();

            Log("==== 用例通过 " + _pass + "/" + _total + " ====");
            Flush();
            return _pass;
        }

        /// <summary>最近一次 Run 的用例总数，供调用方判定是否全部通过（退出码约定）。</summary>
        public static int TotalCount { get { return _total; } }

        // ===== 用例 =====

        private static void Case01_DirWritable()
        {
            Begin("01 目录可创建可写");
            try
            {
                if (!Directory.Exists(AppDir)) Directory.CreateDirectory(AppDir);
                string probe = Path.Combine(AppDir, "probe.tmp");
                File.WriteAllText(probe, "x");
                bool ok = File.Exists(probe);
                File.Delete(probe);
                Assert(ok, "目录可写");
            }
            catch (Exception ex) { Assert(false, ex.Message); }
        }

        private static void Case02_ConfigDefault()
        {
            Begin("02 配置默认值（无文件回退）");
            string bak = BackupConfig();
            try
            {
                if (File.Exists(ConfigFilePath())) File.Delete(ConfigFilePath());
                AppConfig c = AppConfig.Load();
                Assert(c.ViewGrid && !c.ShowHidden && c.AutoScan, "默认值正确");
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { RestoreConfig(bak); }
        }

        private static void Case03_ConfigRoundtrip()
        {
            Begin("03 配置保存/重读一致");
            string bak = BackupConfig();
            try
            {
                if (File.Exists(ConfigFilePath())) File.Delete(ConfigFilePath());
                AppConfig c = new AppConfig();
                c.Favorites.Add("k1");
                c.Hidden.Add("h1");
                c.CustomCategories.Add("自定义1");
                c.CategoryOverride["k1"] = "浏览器";
                c.ItemCategories["k1"] = new List<string> { "开发工具", "常用" };
                c.ResourceStorePath = @"D:\Resources";
                c.Save();
                AppConfig r = AppConfig.Load();
                Assert(r.Favorites.Contains("k1") && r.Hidden.Contains("h1")
                    && r.CustomCategories.Contains("自定义1")
                    && r.CategoryOverride.ContainsKey("k1") && r.CategoryOverride["k1"] == "浏览器"
                    && r.ItemCategories.ContainsKey("k1") && r.ItemCategories["k1"].Count == 2
                    && r.ItemCategories["k1"].Contains("开发工具")
                    && r.ResourceStorePath == @"D:\Resources",
                    "字段往返一致");
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { RestoreConfig(bak); }
        }

        private static void Case04_ClassifyDocker()
        {
            Begin("04 分类 docker desktop -> 开发工具");
            Assert(CategoryRules.Classify("Docker Desktop") == "开发工具", "归类开发工具");
        }

        private static void Case05_ClassifyChrome()
        {
            Begin("05 分类 Google Chrome -> 浏览器");
            Assert(CategoryRules.Classify("Google Chrome") == "浏览器", "归类浏览器");
        }

        private static void Case06_ClassifyWechat()
        {
            Begin("06 分类 微信 -> 通讯社交");
            Assert(CategoryRules.Classify("微信") == "通讯社交", "归类通讯社交");
        }

        private static void Case07_ClassifyUnknown()
        {
            Begin("07 分类 未知名称 -> 其他");
            Assert(CategoryRules.Classify("一个不认识的软件XYZ") == "其他", "兜底其他");
        }

        private static void Case08_IconExtractNotepad()
        {
            Begin("08 图标提取 notepad.exe 有效");
            try
            {
                AppInfo a = new AppInfo();
                a.Name = "notepad";
                a.TargetPath = @"C:\Windows\System32\notepad.exe";
                Bitmap b = Icons.Extract(a, 56);
                Assert(Icons.IsValid(b), "取得有效图标");
                if (b != null) b.Dispose();
            }
            catch (Exception ex) { Assert(false, ex.Message); }
        }

        private static void Case09_TransparentInvalid()
        {
            Begin("09 有效像素阈值：全透明图判无效");
            try
            {
                using (Bitmap t = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
                {
                    for (int y = 0; y < 32; y++)
                        for (int x = 0; x < 32; x++)
                            t.SetPixel(x, y, Color.Transparent);
                    Assert(!Icons.IsValid(t), "全透明图被识别为空图");
                }
            }
            catch (Exception ex) { Assert(false, ex.Message); }
        }

        private static void Case10_ScanNonEmpty()
        {
            Begin("10 扫描器返回非空列表");
            try
            {
                AppConfig cfg = AppConfig.Load();
                Scanner s = new Scanner(cfg);
                List<AppInfo> list = s.ScanAll();
                Assert(list.Count > 0, "扫描到 " + list.Count + " 项");
            }
            catch (Exception ex) { Assert(false, ex.Message); }
        }

        private static void Case11_ScanDedup()
        {
            Begin("11 扫描结果 DisplayKey 去重");
            try
            {
                AppConfig cfg = AppConfig.Load();
                Scanner s = new Scanner(cfg);
                List<AppInfo> list = s.ScanAll();
                HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int dup = 0;
                foreach (AppInfo a in list)
                {
                    if (!keys.Add(a.DisplayKey())) dup++;
                }
                Assert(dup == 0, "无重复键，共 " + list.Count + " 项");
            }
            catch (Exception ex) { Assert(false, ex.Message); }
        }

        private static void Case12_IconPlaceholderFallback()
        {
            Begin("12 图标占位兜底：空宿主返回占位非空");
            try
            {
                AppInfo a = new AppInfo();
                a.Name = "X";
                a.TargetPath = string.Empty;
                a.DisplayIcon = string.Empty;
                using (Bitmap b = Icons.Extract(a, 56))
                {
                    Assert(b != null, "占位图非空");
                }
            }
            catch (Exception ex) { Assert(false, ex.Message); }
        }

        private static void Case13_LauncherNullGuard()
        {
            Begin("13 Launcher 参数守卫：null 不抛异常");
            try
            {
                bool l = Launcher.Launch(null);
                bool u = Launcher.Uninstall(null);
                Assert(!l && !u, "null 返回 false");
            }
            catch (Exception ex) { Assert(false, ex.Message); }
        }

        private static void Case14_DesktopBackupDir()
        {
            Begin("14 DesktopOrganizer 备份目录可建");
            try
            {
                string bp = DesktopOrganizer.BackupPath;
                if (!Directory.Exists(bp)) Directory.CreateDirectory(bp);
                Assert(Directory.Exists(bp), "备份目录存在 " + bp);
            }
            catch (Exception ex) { Assert(false, ex.Message); }
        }

        private static void Case15_BuiltInCount()
        {
            Begin("15 内置分类数量为 9（并含新增资源分类）");
            bool resource = Array.IndexOf(CategoryRules.ResourceBuiltIn, "文档") >= 0
                && Array.IndexOf(CategoryRules.ResourceBuiltIn, "证件") >= 0
                && Array.IndexOf(CategoryRules.ResourceBuiltIn, "压缩包") >= 0
                && Array.IndexOf(CategoryRules.ResourceBuiltIn, "文件") >= 0;
            Assert(CategoryRules.BuiltIn.Length == 9 && resource,
                "数量=" + CategoryRules.BuiltIn.Length + " 资源=" + string.Join(",", CategoryRules.ResourceBuiltIn));
        }

        private static void Case16_NonAppEntryFilter()
        {
            Begin("16 全部应用过滤：网址/文档/卸载器/虚拟项剔除，正常应用保留");
            bool doc = Scanner.IsNonAppEntry("Git Release Notes", @"C:\Program Files\Git\ReleaseNotes.html");
            bool website = Scanner.IsNonAppEntry("Node.js website", string.Empty);
            bool uninstall = Scanner.IsNonAppEntry("卸载微信", string.Empty);
            bool virtualItem = Scanner.IsNonAppEntry("此电脑", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
            bool url = Scanner.IsNonAppEntry("主页", "https://example.com");
            bool keepApp = !Scanner.IsNonAppEntry("Apifox", "cn.apifox.app");
            bool keepExe = !Scanner.IsNonAppEntry("微信", @"D:\APP\WeChat\WeChat.exe");
            bool keepNamed = !Scanner.IsNonAppEntry("计算器", string.Empty);
            Assert(doc && website && uninstall && virtualItem && url && keepApp && keepExe && keepNamed,
                "doc=" + doc + " website=" + website + " uninstall=" + uninstall + " virtual=" + virtualItem
                + " url=" + url + " app=" + keepApp + " exe=" + keepExe + " named=" + keepNamed);
        }

        private static void Case17_IconExtractSize()
        {
            Begin("17 图标提取输出尺寸为 56");
            try
            {
                AppInfo a = new AppInfo();
                a.Name = "notepad";
                a.TargetPath = @"C:\Windows\System32\notepad.exe";
                using (Bitmap b = Icons.Extract(a, 56))
                {
                    Assert(b != null && b.Width == 56 && b.Height == 56,
                        "尺寸=" + (b == null ? "null" : b.Width + "x" + b.Height));
                }
            }
            catch (Exception ex) { Assert(false, ex.Message); }
        }

        private static void Case18_ClassifyPriority()
        {
            Begin("18 分类关键词覆盖：VSCode/Edge");
            string dev = CategoryRules.Classify("Visual Studio Code");
            string browser = CategoryRules.Classify("Microsoft Edge");
            Assert(dev == "开发工具" && browser == "浏览器", dev + " / " + browser);
        }

        private static void Case19_AddedItemsRoundtrip()
        {
            Begin("19 用户添加项配置往返一致");
            string bak = BackupConfig();
            try
            {
                if (File.Exists(ConfigFilePath())) File.Delete(ConfigFilePath());
                AppConfig c = new AppConfig();
                AddedEntry e = new AddedEntry();
                e.Path = @"C:\Windows\System32\notepad.exe";
                e.Category = "开发工具";
                e.IsFolder = false;
                c.AddedItems.Add(e);
                c.Save();
                AppConfig r = AppConfig.Load();
                bool ok = r.AddedItems.Count == 1
                    && r.AddedItems[0].Path == e.Path
                    && r.AddedItems[0].Category == "开发工具"
                    && !r.AddedItems[0].IsFolder;
                Assert(ok, "条目数=" + r.AddedItems.Count);
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { RestoreConfig(bak); }
        }

        private static void Case20_IsAliveTarget()
        {
            Begin("20 失效判定：UWP/网址/协议串恒有效，缺失路径剔除");
            bool uwp = Scanner.IsAliveTarget(string.Empty, true);
            bool url = Scanner.IsAliveTarget("https://example.com", false);
            // 协议定位串（非磁盘路径）不得被误判失效，否则 steam:// 等快捷方式会被剔除
            bool proto = Scanner.IsAliveTarget("steam://rungameid/730", false);
            bool missing = Scanner.IsAliveTarget(@"C:\__no_such__\x.exe", false);
            bool sys = Scanner.IsAliveTarget(@"C:\Windows", false);
            Assert(uwp && url && proto && !missing && sys,
                "uwp=" + uwp + " url=" + url + " proto=" + proto + " missing=" + missing + " dir=" + sys);
        }

        private static void Case21_StripIndexSuffix()
        {
            Begin("21 重名后缀剔除（(2)/_1/-1；空格+数字视为版本号不裁）");
            bool a = Scanner.StripIndexSuffix("微信 (2)") == "微信";
            bool b = Scanner.StripIndexSuffix("Chrome_1") == "Chrome";
            bool c = Scanner.StripIndexSuffix("工具-2") == "工具";
            bool d = Scanner.StripIndexSuffix("Visual Studio Code") == "Visual Studio Code";
            bool e = Scanner.StripIndexSuffix("App (Beta)") == "App (Beta)";
            // 版本号形如「名称 16」，不得被当作重名序号裁剪，否则不同版本会被合并
            bool f = Scanner.StripIndexSuffix("Navicat Premium 16") == "Navicat Premium 16";
            bool g = Scanner.StripIndexSuffix("App 7") == "App 7";
            Assert(a && b && c && d && e && f && g,
                "a=" + a + " b=" + b + " c=" + c + " d=" + d + " e=" + e + " f=" + f + " g=" + g);
        }

        private static void Case22_NameDedupRule()
        {
            Begin("22 收纳去重：同名条目视为重复（条目名称不重复）");
            bool same = Scanner.IsSameEntryName("微信 (2)", "微信");
            bool suffix = Scanner.IsSameEntryName("Chrome_1", "Chrome (3)");
            bool diff = !Scanner.IsSameEntryName("微信", "QQ");
            Assert(same && suffix && diff, "same=" + same + " suffix=" + suffix + " diff=" + diff);
        }

        private static void Case23_IconUrlFile()
        {
            Begin("23 .url 专用取图：IconFile 指向 exe 可提取");
            string tmp = Path.Combine(Path.GetTempPath(), "AppManagerUrl_" + Guid.NewGuid().ToString("N") + ".url");
            try
            {
                File.WriteAllText(tmp,
                    "[InternetShortcut]\r\nURL=https://example.com\r\n" +
                    "IconFile=C:\\Windows\\System32\\notepad.exe\r\nIconIndex=0\r\n");
                AppInfo a = new AppInfo();
                a.Name = "example";
                a.ShortcutPath = tmp;
                a.TargetPath = "https://example.com";
                a.DisplayIcon = tmp;
                a.IsFile = true;
                using (Bitmap b = Icons.Extract(a, 56))
                    Assert(Icons.IsValid(b), "取得有效图标");
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        private static void Case24_IconFolder()
        {
            Begin("24 文件夹条目取系统文件夹图标");
            string dir = Path.Combine(Path.GetTempPath(), "AppManagerDir_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                AppInfo a = new AppInfo();
                a.Name = "测试文件夹";
                a.TargetPath = dir;
                a.DisplayIcon = dir;
                a.IsFile = true;
                a.IsFolder = true;
                using (Bitmap b = Icons.Extract(a, 56))
                    Assert(Icons.IsValid(b), "取得有效图标");
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }
        }

        private static void Case25_IconWebsite()
        {
            Begin("25 网址目标取 Internet 快捷方式图标");
            try
            {
                AppInfo a = new AppInfo();
                a.Name = "example.com";
                a.TargetPath = "https://example.com";
                a.IsFile = true;
                using (Bitmap b = Icons.Extract(a, 56))
                    Assert(Icons.IsValid(b), "取得有效图标");
            }
            catch (Exception ex) { Assert(false, ex.Message); }
        }

        private static void Case27_QuickCenterDir()
        {
            Begin("27 快捷中心收纳目录可建");
            try
            {
                string qc = DesktopOrganizer.QuickCenterDir;
                if (!Directory.Exists(qc)) Directory.CreateDirectory(qc);
                Assert(Directory.Exists(qc), "收纳目录存在 " + qc);
            }
            catch (Exception ex) { Assert(false, ex.Message); }
        }

        private static void Case28_FuzzyMatch()
        {
            Begin("28 模糊匹配：归一化包含 + 字符序列容错");
            bool sub = Scanner.FuzzyMatch("Google Chrome", "chrome");
            bool seq = Scanner.FuzzyMatch("Visual Studio Code", "vsc");
            // 归一化：忽略空格/连字符，故 vscode 可命中「VS Code」「Visual Studio Code」
            bool norm1 = Scanner.FuzzyMatch("VS Code", "vscode");
            bool norm2 = Scanner.FuzzyMatch("Visual Studio Code", "vscode");
            bool miss = !Scanner.FuzzyMatch("微信", "qq");
            bool empty = Scanner.FuzzyMatch("anything", "");
            Assert(sub && seq && norm1 && norm2 && miss && empty,
                "sub=" + sub + " seq=" + seq + " norm1=" + norm1 + " norm2=" + norm2
                + " miss=" + miss + " empty=" + empty);
        }

        private static void Case29_CategoryViewModeRoundtrip()
        {
            Begin("29 分类视图模式往返一致（每分类独立网格/列表）");
            string bak = BackupConfig();
            try
            {
                if (File.Exists(ConfigFilePath())) File.Delete(ConfigFilePath());
                AppConfig c = new AppConfig();
                c.CategoryViewMode["浏览器"] = false;
                c.CategoryViewMode["开发工具"] = true;
                c.ManageRootPath = @"H:\Manage";
                c.Save();
                AppConfig r = AppConfig.Load();
                bool ok = r.CategoryViewMode.ContainsKey("浏览器") && !r.CategoryViewMode["浏览器"]
                    && r.CategoryViewMode.ContainsKey("开发工具") && r.CategoryViewMode["开发工具"]
                    && r.ManageRootPath == @"H:\Manage";
                Assert(ok, "浏览器=" + (r.CategoryViewMode.ContainsKey("浏览器") ? r.CategoryViewMode["浏览器"].ToString() : "-")
                    + " ManageRoot=" + r.ManageRootPath);
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { RestoreConfig(bak); }
        }

        private static void Case30_AddedEntryNameRoundtrip()
        {
            Begin("30 用户添加条目改名（Name 字段）往返一致");
            string bak = BackupConfig();
            try
            {
                if (File.Exists(ConfigFilePath())) File.Delete(ConfigFilePath());
                AppConfig c = new AppConfig();
                AddedEntry e = new AddedEntry();
                e.Path = @"C:\apps\tool.exe";
                e.Category = "快捷中心";
                e.Name = "我的示例工具";
                c.AddedItems.Add(e);
                c.Save();
                AppConfig r = AppConfig.Load();
                bool ok = r.AddedItems.Count == 1
                    && r.AddedItems[0].Name == "我的示例工具";
                Assert(ok, "name=" + (r.AddedItems.Count > 0 ? r.AddedItems[0].Name : "-"));
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { RestoreConfig(bak); }
        }

        private static void Case31_ItemOrderRoundtrip()
        {
            Begin("31 拖动排序记录往返一致（条目顺序 + 侧栏分类顺序）");
            string tmp = TempFile(".ambak");
            try
            {
                AppConfig c = new AppConfig();
                c.ItemOrder["开发工具"] = new List<string> { "lnk:a", "reg:b", "add:c" };
                c.SidebarOrder = new List<string> { "游戏", "开发工具", "浏览器" };
                c.SaveTo(tmp);
                AppConfig r = AppConfig.LoadFrom(tmp);
                List<string> order;
                bool ok = r.ItemOrder.TryGetValue("开发工具", out order)
                    && order.Count == 3 && order[1] == "reg:b"
                    && r.SidebarOrder.Count == 3 && r.SidebarOrder[0] == "游戏" && r.SidebarOrder[2] == "浏览器";
                Assert(ok, "条目=" + (order == null ? "-" : order.Count.ToString())
                    + " 侧栏=" + r.SidebarOrder.Count);
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        private static void Case32_DesktopBackupEntryRoundtrip()
        {
            Begin("32 桌面备份条目新字段往返（目录 / 副本模式）");
            string tmp = TempFile(".ambak");
            try
            {
                AppConfig c = new AppConfig();
                DesktopBackupEntry e = new DesktopBackupEntry();
                e.OriginalPath = @"C:\Users\x\Desktop\项目";
                e.CurrentPath = @"C:\Users\x\AppData\Local\AppManager\DesktopBackup\Folders\项目";
                e.IsShortcut = false;
                e.IsDir = true;
                e.IsCopy = true;
                c.DesktopBackup.Add(e);
                c.SaveTo(tmp);
                AppConfig r = AppConfig.LoadFrom(tmp);
                bool ok = r.DesktopBackup.Count == 1
                    && r.DesktopBackup[0].IsDir
                    && r.DesktopBackup[0].IsCopy
                    && !r.DesktopBackup[0].IsShortcut
                    && r.DesktopBackup[0].CurrentPath == e.CurrentPath;
                Assert(ok, "条目数=" + r.DesktopBackup.Count
                    + " dir=" + (r.DesktopBackup.Count > 0 ? r.DesktopBackup[0].IsDir.ToString() : "-")
                    + " copy=" + (r.DesktopBackup.Count > 0 ? r.DesktopBackup[0].IsCopy.ToString() : "-"));
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        private static void Case33_OrganizeFlagsRoundtrip()
        {
            Begin("33 收纳模式与范围开关往返一致");
            string tmp = TempFile(".ambak");
            try
            {
                AppConfig c = new AppConfig();
                c.OrganizeCopy = true;
                c.OrganizeShortcut = false;
                c.OrganizeFile = true;
                c.OrganizeDir = false;
                c.AutoOrganizeDesktop = true;
                c.SaveTo(tmp);
                AppConfig r = AppConfig.LoadFrom(tmp);
                bool ok = r.OrganizeCopy && !r.OrganizeShortcut && r.OrganizeFile && !r.OrganizeDir
                    && r.AutoOrganizeDesktop && !new AppConfig().AutoOrganizeDesktop;
                Assert(ok, "copy=" + r.OrganizeCopy + " sc=" + r.OrganizeShortcut
                    + " file=" + r.OrganizeFile + " dir=" + r.OrganizeDir);
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        private static void Case34_ExportImportRoundtrip()
        {
            Begin("34 备份包导出/导入往返一致（.ambak）");
            string tmp = TempFile(".ambak");
            try
            {
                AppConfig c = new AppConfig();
                c.CustomCategories.Add("我的分类");
                c.ItemOrder["我的分类"] = new List<string> { "add:1", "add:2" };
                c.SidebarOrder = new List<string> { "我的分类", "游戏" };
                c.ExportTo(tmp);
                AppConfig r = AppConfig.ImportFrom(tmp);
                bool ok = r != null
                    && r.CustomCategories.Contains("我的分类")
                    && r.ItemOrder.ContainsKey("我的分类")
                    && r.ItemOrder["我的分类"].Count == 2
                    && r.SidebarOrder.Count == 2;
                Assert(ok, "分类=" + (r == null ? "null" : r.CustomCategories.Count.ToString()));
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        private static void Case35_ImportInvalidGuard()
        {
            Begin("35 非法备份包导入守卫：损坏/缺失均返回 null");
            string bad = TempFile(".ambak");
            try
            {
                File.WriteAllText(bad, "<NotAppManager/>");
                AppConfig broken = AppConfig.ImportFrom(bad);
                AppConfig missing = AppConfig.ImportFrom(Path.Combine(Path.GetTempPath(), "__no_such__.ambak"));
                Assert(broken == null && missing == null,
                    "broken=" + (broken == null) + " missing=" + (missing == null));
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { try { if (File.Exists(bad)) File.Delete(bad); } catch { } }
        }

        private static void Case36_AppsFolderTarget()
        {
            Begin("36 AppsFolder 定位串识别：打包应用 target 判定");
            bool yes = Scanner.IsAppsFolderTarget("shell:AppsFolder\\Chrome");
            bool yesCn = Scanner.IsAppsFolderTarget("SHELL:APPSFOLDER\\cn.apifox.app");
            bool noExe = !Scanner.IsAppsFolderTarget(@"C:\Windows\notepad.exe");
            bool noEmpty = !Scanner.IsAppsFolderTarget(string.Empty);
            bool noUrl = !Scanner.IsAppsFolderTarget("https://example.com");
            Assert(yes && yesCn && noExe && noEmpty && noUrl,
                "yes=" + yes + " cn=" + yesCn + " exe=" + noExe + " empty=" + noEmpty + " url=" + noUrl);
        }

        private static void Case37_MoveDirectory()
        {
            Begin("37 目录搬迁-同卷：直接 Move，源消失且目标结构一致");
            string baseTmp = Path.Combine(Path.GetTempPath(), "AppManagerMove_" + Guid.NewGuid().ToString("N"));
            try
            {
                string src = Path.Combine(baseTmp, "src");
                string dest = Path.Combine(baseTmp, "dest");
                Directory.CreateDirectory(Path.Combine(src, "sub"));
                File.WriteAllText(Path.Combine(src, "a.txt"), "x");
                File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "y");
                DesktopOrganizer.MoveDirectory(src, dest);
                bool ok = !Directory.Exists(src)
                    && File.Exists(Path.Combine(dest, "a.txt"))
                    && File.Exists(Path.Combine(dest, "sub", "b.txt"));
                Assert(ok, "源残留=" + Directory.Exists(src) + " 目标=" + Directory.Exists(dest));
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { TryDeleteTree(baseTmp); }
        }

        /// <summary>跨卷搬迁：仅在存在可用第二卷时执行，避免无第二卷环境下误报失败。</summary>
        private static void Case38_MoveDirectoryCrossVolume()
        {
            string other = OtherFixedVolume(Path.GetPathRoot(Path.GetTempPath()));
            if (other == null) return;
            Begin("38 目录搬迁-跨卷：复制后删除源，源消失且目标结构一致");
            string baseTmp = Path.Combine(Path.GetTempPath(), "AppManagerMove_" + Guid.NewGuid().ToString("N"));
            string xdest = Path.Combine(other, "AppManagerMoveTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                string xsrc = Path.Combine(baseTmp, "xsrc");
                Directory.CreateDirectory(Path.Combine(xsrc, "deep"));
                File.WriteAllText(Path.Combine(xsrc, "deep", "c.txt"), "z");
                DesktopOrganizer.MoveDirectory(xsrc, xdest);
                string sourceFile = Path.Combine(baseTmp, "move-file.txt");
                string destFile = xdest + ".txt";
                File.WriteAllText(sourceFile, "cross-volume");
                DesktopOrganizer.MoveFile(sourceFile, destFile);
                bool ok = !Directory.Exists(xsrc)
                    && File.Exists(Path.Combine(xdest, "deep", "c.txt"))
                    && !File.Exists(sourceFile) && File.ReadAllText(destFile) == "cross-volume";
                Assert(ok, "源残留=" + Directory.Exists(xsrc) + " 目标=" + Directory.Exists(xdest) + " 卷=" + other);
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally
            {
                TryDeleteTree(baseTmp); TryDeleteTree(xdest);
                try { if (File.Exists(xdest + ".txt")) File.Delete(xdest + ".txt"); } catch { }
            }
        }

        private static void Case39_CloseToTrayRoundtrip()
        {
            Begin("39 关闭行为开关往返一致（退出/最小化到托盘）");
            string tmp = TempFile(".ambak");
            try
            {
                AppConfig c = new AppConfig();
                c.CloseToTray = true;
                c.SaveTo(tmp);
                AppConfig r = AppConfig.LoadFrom(tmp);
                bool on = r.CloseToTray;
                c.CloseToTray = false;
                c.SaveTo(tmp);
                AppConfig r2 = AppConfig.LoadFrom(tmp);
                bool off = !r2.CloseToTray;
                Assert(on && off, "toTray=" + on + " exit=" + off);
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        private static void Case40_NormalizeRootAndAtomicSave()
        {
            Begin("40 盘符根规范化 + 配置原子写");
            bool root = DesktopOrganizer.NormalizeRoot("H:") == "H:\\"
                && DesktopOrganizer.NormalizeRoot("H:\\Manage\\") == "H:\\Manage"
                && DesktopOrganizer.NormalizeRoot(string.Empty) == string.Empty;
            string tmp = TempFile(".ambak");
            bool atomic;
            try
            {
                AppConfig c = new AppConfig();
                c.Favorites.Add("x");
                c.SaveTo(tmp);              // 首次：无目标文件，临时文件整体移动
                c.Favorites.Add("y");
                c.SaveTo(tmp);              // 再次：File.Replace 原子替换，原文件不被中途写坏
                AppConfig r = AppConfig.LoadFrom(tmp);
                atomic = r.Favorites.Count == 2 && !File.Exists(tmp + ".tmp");
            }
            catch (Exception ex) { Assert(false, ex.Message); return; }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
            Assert(root && atomic, "root=" + root + " atomic=" + atomic);
        }

        private static void Case41_IconCacheClone()
        {
            Begin("41 图标缓存：重复取得为独立副本，释放其一不影响缓存复用");
            AppInfo a = new AppInfo();
            a.Name = "notepad";
            a.TargetPath = @"C:\Windows\System32\notepad.exe";
            a.DisplayIcon = a.TargetPath;
            bool independent = false, reusable = false;
            try
            {
                Bitmap first = Icons.Extract(a, 48);
                Bitmap second = Icons.Extract(a, 48);
                // 两次取得必须是不同实例，避免调用方释放副本时污染缓存
                independent = first != null && second != null && !ReferenceEquals(first, second);
                if (first != null) first.Dispose();
                Bitmap third = Icons.Extract(a, 48);      // 释放副本后仍应从缓存取到有效图标
                reusable = Icons.IsValid(third);
                if (second != null) second.Dispose();
                if (third != null) third.Dispose();
            }
            catch (Exception ex) { Assert(false, ex.Message); return; }
            Assert(independent && reusable, "独立=" + independent + " 可复用=" + reusable);
        }

        /// <summary>用例42：落点唯一化规则——重名追加 _1/_2 后缀，无冲突时原样返回（拖入目录与新建共用）。</summary>
        private static void Case42_UniqueDestSuffix()
        {
            Begin("42 落点唯一化：重名追加 _1/_2，无冲突原样返回");
            string dir = Path.Combine(Path.GetTempPath(), "AppManagerUD_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                // 无冲突：原样返回
                string first = MainForm.UniqueDest(dir, "新建文本文档.txt");
                File.WriteAllText(first, "x");
                // 同名文件冲突：追加 _1
                string second = MainForm.UniqueDest(dir, "新建文本文档.txt");
                File.WriteAllText(second, "y");
                // 再冲突：追加 _2
                string third = MainForm.UniqueDest(dir, "新建文本文档.txt");
                // 同名目录冲突：同样走 _1 规则
                string sub = Path.Combine(dir, "新建文件夹");
                Directory.CreateDirectory(sub);
                string dirDest = MainForm.UniqueDest(dir, "新建文件夹");
                bool ok = Path.GetFileName(first) == "新建文本文档.txt"
                       && Path.GetFileName(second) == "新建文本文档_1.txt"
                       && Path.GetFileName(third) == "新建文本文档_2.txt"
                       && Path.GetFileName(dirDest) == "新建文件夹_1";
                Assert(ok, first + " | " + second + " | " + third + " | " + dirDest);
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { TryDeleteTree(dir); }
        }

        /// <summary>用例43：拖入目录搬迁规则——IsUnder 防自嵌套边界 + 文件经 UniqueDest 搬入后源消失、目标存在。</summary>
        private static void Case43_MoveIntoFolderRules()
        {
            Begin("43 拖入目录：防自嵌套边界正确，搬迁落点与源清理一致");
            string baseTmp = Path.Combine(Path.GetTempPath(), "AppManagerMF_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(baseTmp);
                string folder = Path.Combine(baseTmp, "目录");
                string child = Path.Combine(folder, "子");
                Directory.CreateDirectory(folder);
                Directory.CreateDirectory(child);
                // 防自嵌套边界：拖入自身 / 拖入子目录 / 子目录拖出 均须拦截
                bool selfBlocked = MainForm.IsUnder(folder, folder);        // 含自身 → 拖到自身被拦
                bool intoChildBlocked = MainForm.IsUnder(folder, child);    // 目录拖入自己的子目录被拦
                bool childOutAllowed = !MainForm.IsUnder(child, folder);    // 子目录拖回父目录放行
                // 文件搬迁：UniqueDest 落点 + File.Move，源消失、目标存在
                string file = Path.Combine(baseTmp, "笔记.txt");
                File.WriteAllText(file, "data");
                string dest = MainForm.UniqueDest(folder, Path.GetFileName(file));
                File.Move(file, dest);
                bool moved = !File.Exists(file) && File.Exists(dest)
                          && File.ReadAllText(dest) == "data";
                Assert(selfBlocked && intoChildBlocked && childOutAllowed && moved,
                    "自身=" + selfBlocked + " 入子=" + intoChildBlocked
                    + " 子出=" + childOutAllowed + " 落点=" + dest);
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { TryDeleteTree(baseTmp); }
        }

        /// <summary>用例44：删除能力——永久删除目录树/单文件生效，空参与已消失路径守卫返回 false（回收站模式不污染测试）。</summary>
        private static void Case44_DeletePath()
        {
            Begin("44 删除：目录树与单文件删除生效，无效路径守卫拦截");
            string dir = Path.Combine(Path.GetTempPath(), "AppManagerDel_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(dir);
                string sub = Path.Combine(dir, "子目录");
                Directory.CreateDirectory(sub);
                File.WriteAllText(Path.Combine(sub, "inner.txt"), "i");
                File.WriteAllText(Path.Combine(dir, "笔记.txt"), "d");
                bool dirOk = Launcher.DeletePath(dir, false);        // 永久删除目录树（测试不进回收站）
                string f2 = Path.Combine(Path.GetTempPath(), "AppManagerDelF_" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(f2, "f");
                bool fileOk = Launcher.DeletePath(f2, false);        // 永久删除单文件
                bool guard = !Launcher.DeletePath(null, false)       // 空参守卫
                          && !Launcher.DeletePath(dir, false);       // 已消失路径守卫
                Assert(dirOk && fileOk && guard && !Directory.Exists(dir) && !File.Exists(f2),
                    "目录=" + dirOk + " 文件=" + fileOk + " 守卫=" + guard);
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { TryDeleteTree(dir); }
        }

        /// <summary>资源分类的类型约束：照片只收图片、视频只收视频，文件与自建分类不限类型。</summary>
        private static void Case45_ResourceCategoryType()
        {
            Begin("45 资源分类类型约束：内置分类按类型收，文件/自建分类不限");
            bool photoOk = MediaRules.MatchCategoryExt("照片", @"C:\a\MVIMG_0001.JPG")
                && !MediaRules.MatchCategoryExt("照片", @"C:\a\movie.mp4")
                && !MediaRules.MatchCategoryExt("照片", @"C:\a\doc.pdf");
            bool videoOk = MediaRules.MatchCategoryExt("视频", @"C:\a\movie.mp4")
                && !MediaRules.MatchCategoryExt("视频", @"C:\a\song.mp3");
            bool musicOk = MediaRules.MatchCategoryExt("音乐", @"C:\a\song.flac")
                && !MediaRules.MatchCategoryExt("音乐", @"C:\a\movie.mkv");
            bool docOk = MediaRules.MatchCategoryExt("文档", @"C:\a\readme.pdf")
                && !MediaRules.MatchCategoryExt("文档", @"C:\a\pack.zip");
            bool zipOk = MediaRules.MatchCategoryExt("压缩包", @"C:\a\pack.zip")
                && !MediaRules.MatchCategoryExt("压缩包", @"C:\a\a.png");
            bool idOk = MediaRules.MatchCategoryExt("证件", @"C:\a\id.jpg")
                && MediaRules.MatchCategoryExt("证件", @"C:\a\id.pdf")
                && !MediaRules.MatchCategoryExt("证件", @"C:\a\a.mp4");
            bool freeOk = MediaRules.MatchCategoryExt("文件", @"C:\a\a.txt")
                && MediaRules.MatchCategoryExt("我的照片", @"C:\a\a.txt");
            string photoFilter = MediaRules.ResourceCategoryFilter("照片");
            bool filterOk = photoFilter.StartsWith("照片 (*.")
                && photoFilter.Contains("*.jpg") && photoFilter.Contains("|*.jpg")
                && MediaRules.ResourceCategoryFilter("文件") == "所有文件 (*.*)|*.*";
            bool nameOk = MediaRules.ResourceCategoryTypeName("视频") == "视频"
                && MediaRules.ResourceCategoryTypeName("文件") == "文件"
                && MediaRules.ResourceCategoryTypeName("我的照片") == "文件";
            Assert(photoOk && videoOk && musicOk && docOk && zipOk && idOk && freeOk && filterOk && nameOk,
                "照片=" + photoOk + " 视频=" + videoOk + " 音乐=" + musicOk + " 文档=" + docOk
                + " 压缩包=" + zipOk + " 证件=" + idOk + " 不限=" + freeOk + " 过滤=" + filterOk + " 名称=" + nameOk);
        }

        /// <summary>集合键规则与集合定义的配置往返（XML 保存/重读一致）。</summary>
        private static void Case46_CollectionKeysAndRoundtrip()
        {
            Begin("46 集合键规则与 Collections 配置往返");
            string bak = BackupConfig();
            try
            {
                if (File.Exists(ConfigFilePath())) File.Delete(ConfigFilePath());
                AppConfig c = new AppConfig();
                ResourceCollection col = new ResourceCollection();
                col.Name = "旅行";
                col.Parent = "照片";
                col.Description = "海边与山野";
                col.Cover = @"C:\pics\cover.jpg";
                col.CoverFocusX = 0.25F;
                col.CoverFocusY = 0.75F;
                c.Collections.Add(col);
                c.Save();
                AppConfig r = AppConfig.Load();
                float fx = r.Collections.Count == 1 ? r.Collections[0].CoverFocusX : -1F;
                float fy = r.Collections.Count == 1 ? r.Collections[0].CoverFocusY : -1F;
                bool roundtrip = r.Collections.Count == 1
                    && r.Collections[0].Name == "旅行"
                    && r.Collections[0].Parent == "照片"
                    && r.Collections[0].Description == "海边与山野"
                    && r.Collections[0].Cover == @"C:\pics\cover.jpg"
                    && Math.Abs(fx - 0.25F) < 0.001F
                    && Math.Abs(fy - 0.75F) < 0.001F;
                bool keyRule = CollectionKeys.Make("照片", "旅行") == "col:照片/旅行"
                    && CollectionKeys.IsCollectionKey("col:照片/旅行")
                    && !CollectionKeys.IsCollectionKey("照片")
                    && !CollectionKeys.IsCollectionKey(null);
                Assert(roundtrip && keyRule, "往返=" + roundtrip + " 键规则=" + keyRule);
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { RestoreConfig(bak); }
        }

        /// <summary>集合成员操作：加入/移出/重命名迁移/删除清理（多归属不搬文件）。</summary>
        private static void Case47_CollectionOpsMembers()
        {
            Begin("47 集合成员操作与归属键迁移（加入/移出/改名/删除）");
            string bak = BackupConfig();
            try
            {
                if (File.Exists(ConfigFilePath())) File.Delete(ConfigFilePath());
                AppConfig c = new AppConfig();
                ResourceCollection col = new ResourceCollection();
                col.Name = "旅行";
                col.Parent = "照片";
                c.Collections.Add(col);
                string key = CollectionKeys.Make(col.Parent, col.Name);
                string item = "add:C:\\photos\\a.jpg";

                // 首次加入返回 true（产生变化），重复加入返回 false（供调用方决定是否落盘/刷新）
                bool addChanged = CollectionOps.AddMember(c, item, key);
                bool addAgain = CollectionOps.AddMember(c, item, key);
                bool added = addChanged && !addAgain
                    && !CollectionOps.AddMember(c, null, key)       // 空条目键：无变化
                    && c.ItemCategories.ContainsKey(item)
                    && c.ItemCategories[item].Count == 1
                    && c.ItemCategories[item][0] == key;

                bool removed = CollectionOps.RemoveMember(c, item, key)
                    && !c.ItemCategories.ContainsKey(item)          // 归属清空后条目键移除
                    && !CollectionOps.RemoveMember(c, item, key);   // 再移出返回 false

                CollectionOps.AddMember(c, item, key);
                CollectionOps.RenameMemberKey(c, key, "col:照片/远行");
                bool renamed = c.ItemCategories[item][0] == "col:照片/远行"
                    && CollectionOps.Find(c, "照片", "远行") == null;   // 定义未随键改名，由调用方同步

                CollectionOps.RemoveCollectionKey(c, "col:照片/远行");
                bool cleaned = !c.ItemCategories.ContainsKey(item);

                // 定义查找：同父分类名唯一，跨父分类不冲突
                ResourceCollection other = new ResourceCollection();
                other.Name = "旅行"; other.Parent = "证件";
                c.Collections.Add(other);
                bool findOk = CollectionOps.NameExists(c, "照片", "旅行")
                    && CollectionOps.NameExists(c, "证件", "旅行")
                    && !CollectionOps.NameExists(c, "照片", "远行");
                Assert(added && removed && renamed && cleaned && findOk,
                    "加入=" + added + " 移出=" + removed + " 改名迁移=" + renamed
                    + " 删除清理=" + cleaned + " 查找=" + findOk);
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { RestoreConfig(bak); }
        }

        /// <summary>资源分类改名/删除时集合的联动迁移：父分类与成员键同步、孤儿集合清理。</summary>
        private static void Case48_CollectionParentSync()
        {
            Begin("48 资源分类改名/删除时集合同步迁移");
            string bak = BackupConfig();
            try
            {
                if (File.Exists(ConfigFilePath())) File.Delete(ConfigFilePath());
                AppConfig c = new AppConfig();
                ResourceCollection photo = new ResourceCollection();
                photo.Name = "旅行"; photo.Parent = "照片";
                c.Collections.Add(photo);
                ResourceCollection doc = new ResourceCollection();
                doc.Name = "合同"; doc.Parent = "文档";
                c.Collections.Add(doc);
                string photoKey = CollectionKeys.Make("照片", "旅行");
                string docKey = CollectionKeys.Make("文档", "合同");
                string photoItem = "add:C:\\photos\\a.jpg";
                string docItem = "add:C:\\docs\\b.pdf";
                CollectionOps.AddMember(c, photoItem, photoKey);
                CollectionOps.AddMember(c, docItem, docKey);

                // 改名：父分类与成员键一并迁移，其它分类的集合与成员不受影响
                CollectionOps.RenameParent(c, "照片", "图片");
                string newKey = CollectionKeys.Make("图片", "旅行");
                bool renameOk = photo.Parent == "图片"
                    && CollectionOps.Find(c, "图片", "旅行") != null
                    && c.ItemCategories[photoItem].Contains(newKey)
                    && !c.ItemCategories[photoItem].Contains(photoKey)
                    && doc.Parent == "文档"
                    && c.ItemCategories[docItem].Contains(docKey);

                // 删除：该分类下的集合定义与成员键清理，其它分类保留
                CollectionOps.RemoveByParent(c, "图片");
                bool removeOk = c.Collections.Count == 1
                    && c.Collections[0].Name == "合同"
                    && !c.ItemCategories.ContainsKey(photoItem)
                    && c.ItemCategories.ContainsKey(docItem);
                Assert(renameOk && removeOk, "改名迁移=" + renameOk + " 删除清理=" + removeOk);
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { RestoreConfig(bak); }
        }

        /// <summary>媒体类型识别：视频全格式覆盖、大小写不敏感、非同类型排除；「视频」分类过滤器只含视频扩展名。</summary>
        private static void Case49_MediaTypeRules()
        {
            Begin("49 媒体类型识别与视频分类过滤器");
            string[] videos = { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v" };
            string filter = MediaRules.ResourceCategoryFilter("视频");
            bool allVideo = true;
            bool filterAll = true;
            for (int i = 0; i < videos.Length; i++)
            {
                // 大写扩展名同样命中（播放器按扩展名判定，不受大小写影响）
                allVideo = allVideo && MediaRules.IsVideo(@"C:\a\clip" + videos[i].ToUpperInvariant());
                filterAll = filterAll && filter.Contains("*" + videos[i]);
            }
            bool videoOk = allVideo
                && !MediaRules.IsVideo(@"C:\a\song.mp3")
                && !MediaRules.IsVideo(@"C:\a\pic.jpg")
                && !MediaRules.IsVideo(@"C:\a\plain")
                && !MediaRules.IsVideo(null);
            bool imageOk = MediaRules.IsImage(@"C:\a\Photo.JPG")
                && !MediaRules.IsImage(@"C:\a\clip.mp4");
            bool audioOk = MediaRules.IsAudio(@"C:\a\Song.FLAC")
                && MediaRules.IsAudio(@"C:\a\Song.APE")
                && MediaRules.IsAudio(@"C:\a\Song.DSF")
                && !MediaRules.IsAudio(@"C:\a\clip.mkv");
            bool mediaOk = MediaRules.IsMedia(@"C:\a\x.webm")
                && MediaRules.IsMedia(@"C:\a\x.png")
                && MediaRules.IsMedia(@"C:\a\x.m4a")
                && !MediaRules.IsMedia(@"C:\a\x.txt");
            bool documentOk = MediaRules.IsPdf(@"C:\a\id.PDF")
                && MediaRules.IsImageDocument(@"C:\a\id.pdf")
                && MediaRules.IsImageDocument(@"C:\a\id.png")
                && MediaRules.IsText(@"C:\a\notes.TXT")
                && MediaRules.IsText(@"C:\a\config.json")
                && !MediaRules.IsText(@"C:\a\archive.zip");
            bool filterOk = filter.StartsWith("视频 (*.")
                && filter.Contains("|*.")
                && !filter.Contains("*.jpg")
                && !filter.Contains("*.mp3");
            Assert(videoOk && imageOk && audioOk && mediaOk && documentOk && filterOk && filterAll,
                "视频=" + videoOk + " 图片=" + imageOk + " 音频=" + audioOk
                + " 媒体=" + mediaOk + " 文档=" + documentOk + " 过滤=" + filterOk + " 全覆盖=" + filterAll);
        }

        private static void Case49_WicPngDecode()
        {
            Begin("49B WIC 解码：透明、小图、横竖图限幅、EXIF 和句柄释放");
            string path = TempFile(".png");
            string jpeg = TempFile(".jpg");
            try
            {
                using (Bitmap source = new Bitmap(3, 2, PixelFormat.Format32bppArgb))
                {
                    source.SetPixel(1, 1, Color.FromArgb(96, 12, 34, 56));
                    source.Save(path, ImageFormat.Png);
                }
                bool alpha;
                using (Bitmap decoded = WicImageDecoder.Load(path, 3200))
                {
                    Color pixel = decoded.GetPixel(1, 1);
                    alpha = decoded.Width == 3 && decoded.Height == 2 && pixel.A == 96;
                    using (FileStream exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                }
                bool bounds = true;
                foreach (Size size in new Size[] { new Size(400, 100), new Size(100, 400) })
                {
                    using (Bitmap source = new Bitmap(size.Width, size.Height)) source.Save(path, ImageFormat.Png);
                    using (Bitmap decoded = WicImageDecoder.Load(path, 32))
                        bounds = bounds && Math.Max(decoded.Width, decoded.Height) == 32
                            && Math.Min(decoded.Width, decoded.Height) == 8;
                }
                using (Bitmap source = new Bitmap(4, 2))
                {
                    PropertyItem item = (PropertyItem)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(PropertyItem));
                    item.Id = 0x0112; item.Type = 3; item.Value = new byte[] { 6, 0 }; item.Len = 2;
                    source.SetPropertyItem(item);
                    source.Save(jpeg, ImageFormat.Jpeg);
                }
                bool orientation;
                using (Bitmap decoded = WicImageDecoder.Load(jpeg, 3200))
                    orientation = decoded.Width == 2 && decoded.Height == 4;
                Assert(alpha && bounds && orientation, "透明/小图=" + alpha + " 横竖限幅=" + bounds + " EXIF=" + orientation);
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                try { if (File.Exists(jpeg)) File.Delete(jpeg); } catch { }
            }
        }

        private static void Case50_CorruptConfigProtection()
        {
            Begin("50 配置损坏：禁止默认对象覆盖原文件");
            string path = ConfigFilePath();
            const string brokenXml = "<AppManagerConfig><broken>";
            try
            {
                File.WriteAllText(path, brokenXml);
                AppConfig broken = AppConfig.LoadFrom(path);
                broken.CustomCategories.Add("不应保存");
                bool saved = broken.Save();
                Assert(broken.LoadFailed && !saved && File.ReadAllText(path) == brokenXml,
                    "loadFailed=" + broken.LoadFailed + " saved=" + saved);
            }
            catch (Exception ex) { Assert(false, ex.Message); }
        }

        private static void Case51_LayoutRestoreRollback()
        {
            Begin("51 布局恢复：预复制失败不改现场，成功后完整替换");
            string id = "restore-test";
            string snapshot = Path.Combine(LayoutBackup.RootDir, id);
            try
            {
                Directory.CreateDirectory(Path.Combine(snapshot, "DesktopBackup"));
                AppConfig backup = new AppConfig();
                backup.CustomCategories.Add("备份分类");
                backup.SaveTo(Path.Combine(snapshot, "config.xml"));
                string snapshotFile = Path.Combine(snapshot, "DesktopBackup", "backup.txt");
                File.WriteAllText(snapshotFile, "backup-data");
                Directory.CreateDirectory(Paths.DesktopBackupDir);
                string liveFile = Path.Combine(Paths.DesktopBackupDir, "live.txt");
                File.WriteAllText(liveFile, "live-data");
                AppConfig live = new AppConfig();
                live.CustomCategories.Add("现场分类");
                live.Save();

                string error;
                bool rollbackOk;
                using (FileStream locked = new FileStream(snapshotFile, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    bool restored = LayoutBackup.Restore(id, out error);
                    bool unchanged = !restored && File.ReadAllText(liveFile) == "live-data"
                        && AppConfig.Load().CustomCategories.Contains("现场分类");
                    rollbackOk = unchanged;
                }
                bool success = LayoutBackup.Restore(id, out error);
                bool replaced = success && File.ReadAllText(Path.Combine(Paths.DesktopBackupDir, "backup.txt")) == "backup-data"
                    && !File.Exists(liveFile) && AppConfig.Load().CustomCategories.Contains("备份分类");
                Assert(rollbackOk && replaced, "失败回滚=" + rollbackOk + " 成功提交=" + replaced + " error=" + error);
            }
            catch (Exception ex) { Assert(false, ex.Message); }
        }

        private static void Case52_SaveFailureKeepsOriginal()
        {
            Begin("52 配置保存失败：原文件保持完整");
            string path = TempFile(".xml");
            const string original = "original-config";
            try
            {
                File.WriteAllText(path, original);
                bool saved;
                using (FileStream locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    AppConfig config = new AppConfig();
                    saved = config.SaveTo(path);
                }
                Assert(!saved && File.ReadAllText(path) == original, "saved=" + saved);
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                try { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); } catch { }
                try { if (File.Exists(path + ".previous")) File.Delete(path + ".previous"); } catch { }
            }
        }

        /// <summary>照片拍摄时间：EXIF DateTimeOriginal 优先于文件系统时间；损坏文件回退 false。</summary>
        private static void Case53_TakenDatePhotoExif()
        {
            Begin("53 拍摄时间：照片读 EXIF DateTimeOriginal");
            string path = TempFile(".jpg");
            try
            {
                // 造一张最小 JPEG：1x1 像素，EXIF 写入 DateTimeOriginal=2020-01-02 03:04:05
                using (Bitmap bmp = new Bitmap(1, 1))
                {
                    // 0x9003 PropertyItem 不能直接 new，借现有项改造
                    PropertyItem pi = (PropertyItem)System.Runtime.Serialization.FormatterServices
                        .GetUninitializedObject(typeof(PropertyItem));
                    pi.Id = 0x9003;
                    pi.Type = 2; // ASCII
                    string text = "2020:01:02 03:04:05\0";
                    pi.Value = System.Text.Encoding.ASCII.GetBytes(text);
                    pi.Len = pi.Value.Length;
                    bmp.SetPropertyItem(pi);
                    bmp.Save(path, ImageFormat.Jpeg);
                }
                DateTime taken;
                bool ok = MediaInfo.TryGetTakenDate(path, out taken)
                    && taken.Year == 2020 && taken.Month == 1 && taken.Day == 2
                    && taken.Hour == 3 && taken.Minute == 4 && taken.Second == 5;
                // 非媒体文件返回 false
                string txt = TempFile(".txt");
                File.WriteAllText(txt, "x");
                DateTime dt;
                bool notMedia = !MediaInfo.TryGetTakenDate(txt, out dt);
                try { File.Delete(txt); } catch { }
                Assert(ok && notMedia, "exif=" + ok + " 非媒体=" + notMedia);
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        }

        /// <summary>视频拍摄时间：mp4 mvhd creation_time 解析；损坏文件返回 false。</summary>
        private static void Case54_TakenDateVideoMvhd()
        {
            Begin("54 拍摄时间：视频解析 mp4 mvhd creation_time");
            string path = TempFile(".mp4");
            try
            {
                // 手工构造最小 mp4：ftyp + moov(mvhd)
                // mvhd version=0，creation_time = (2024-03-22 10:30:00 UTC) - 1904-01-01 的秒数
                DateTime want = new DateTime(2024, 3, 22, 10, 30, 0, DateTimeKind.Utc);
                uint secs = (uint)(want - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    WriteBox(fs, "ftyp", new byte[] { 0x69, 0x73, 0x6F, 0x6D, 0, 0, 0, 1 });  // "isom" + version
                    byte[] mvhdPayload = new byte[100];
                    mvhdPayload[0] = 0; // version
                    // flags[1..3] = 0
                    WriteU32BE(mvhdPayload, 4, secs);
                    WriteU32BE(mvhdPayload, 8, secs);   // modification_time
                    WriteU32BE(mvhdPayload, 12, 1000);  // timescale
                    WriteU32BE(mvhdPayload, 16, 5000);  // duration
                    WriteBox(fs, "moov", Box("mvhd", mvhdPayload));
                }
                DateTime taken;
                bool ok = MediaInfo.TryGetTakenDate(path, out taken);
                // 转成本地时区后比较：本地时间可能因时区有偏移，换算回 UTC 再比
                DateTime backUtc = taken.ToUniversalTime();
                bool timeOk = ok && Math.Abs((backUtc - want).TotalSeconds) < 1;
                // 空文件应失败
                string bad = TempFile(".mp4");
                File.WriteAllBytes(bad, new byte[0]);
                DateTime dt;
                bool badOk = !MediaInfo.TryGetTakenDate(bad, out dt);
                try { File.Delete(bad); } catch { }
                Assert(timeOk && badOk, "mvhd=" + timeOk + " 损坏=" + badOk);
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        }

        private static void Case55_MusicLyrics()
        {
            Begin("55 音乐同步歌词解析与排序");
            List<LyricLine> lines = MusicMetadata.ParseLyrics(
                "[ti:示例]\r\n[00:12.50][00:02.25]重复时间戳\r\n[bad]忽略\r\n[01:03.750]下一行\r\n普通文本");
            bool ok = lines.Count == 3
                && Math.Abs(lines[0].Time - 2.25) < 0.001
                && lines[0].Text == "重复时间戳"
                && Math.Abs(lines[1].Time - 12.5) < 0.001
                && Math.Abs(lines[2].Time - 63.75) < 0.001
                && lines[2].Text == "下一行"
                && MusicMetadata.ParseLyrics("").Count == 0;
            Assert(ok, "行数=" + lines.Count + " 多时间戳=True 排序=True 无效行忽略=True");
        }

        private static void Case56_MusicMetadata()
        {
            Begin("56 音乐 ID3 标题、内嵌封面与同名歌词");
            string path = TempFile(".mp3");
            try
            {
                byte[] title = Id3Frame("TIT2", new byte[] { 3 }.Concat(Encoding.UTF8.GetBytes("测试歌曲")).ToArray());
                List<byte> picture = new List<byte>();
                picture.Add(3);
                picture.AddRange(Encoding.ASCII.GetBytes("image/png\0"));
                picture.Add(3);
                picture.Add(0);
                picture.AddRange(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
                byte[] cover = Id3Frame("APIC", picture.ToArray());
                int tagSize = title.Length + cover.Length;
                using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    stream.Write(Encoding.ASCII.GetBytes("ID3"), 0, 3);
                    stream.Write(new byte[] { 3, 0, 0, (byte)(tagSize >> 21), (byte)(tagSize >> 14), (byte)(tagSize >> 7), (byte)tagSize }, 0, 7);
                    stream.Write(title, 0, title.Length);
                    stream.Write(cover, 0, cover.Length);
                }
                File.WriteAllText(Path.ChangeExtension(path, ".lrc"), "[00:01.00]测试歌词", new UTF8Encoding(false));
                MusicTrackInfo info = MusicMetadata.Read(path);
                bool ok = info.Title == "测试歌曲" && info.Cover != null
                    && MusicMetadata.ParseLyrics(info.Lyrics).Count == 1;
                info.Dispose();
                Assert(ok, "标题=True 封面=True 同名LRC=True");
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally
            {
                try { File.Delete(path); } catch { }
                try { File.Delete(Path.ChangeExtension(path, ".lrc")); } catch { }
            }
        }

        private static byte[] Id3Frame(string id, byte[] payload)
        {
            byte[] result = new byte[10 + payload.Length];
            Array.Copy(Encoding.ASCII.GetBytes(id), result, 4);
            WriteU32BE(result, 4, (uint)payload.Length);
            Array.Copy(payload, 0, result, 10, payload.Length);
            return result;
        }

        private static void Case57_MusicMenuGrouping()
        {
            Begin("57 音乐菜单分组有界且歌曲索引完整");
            List<string> songs = new List<string>();
            for (int i = 0; i < 300; i++) songs.Add(@"C:\music\" + i.ToString("000") + ".mp3");
            int selected = -1;
            List<MenuWindow.MenuItem> root = MainForm.MiniMusicSongItems("", "全部音乐", songs,
                delegate(string key, string name, int index) { selected = index; });
            int maxItems = 0;
            List<MenuWindow.MenuItem> leaves = new List<MenuWindow.MenuItem>();
            CollectMenuLeaves(root, leaves, ref maxItems);
            if (leaves.Count > 0 && leaves[leaves.Count - 1].Action != null) leaves[leaves.Count - 1].Action();
            bool ok = root.Count <= 16 && maxItems <= 16 && leaves.Count == songs.Count && selected == 299;
            Assert(ok, "根分组=" + root.Count + " 最大菜单=" + maxItems + " 歌曲=" + leaves.Count + " 索引=" + selected);
        }

        private static void CollectMenuLeaves(List<MenuWindow.MenuItem> items,
            List<MenuWindow.MenuItem> leaves, ref int maxItems)
        {
            maxItems = Math.Max(maxItems, items.Count);
            foreach (MenuWindow.MenuItem item in items)
            {
                if (item.SubItems != null && item.SubItems.Count > 0) CollectMenuLeaves(item.SubItems, leaves, ref maxItems);
                else leaves.Add(item);
            }
        }

        private static void Case58_CategoryIconKeys()
        {
            Begin("58 软件与资源同名分类图标独立持久化");
            string tmp = TempFile(".xml");
            try
            {
                string softwareKey = CategoryIcons.ConfigKey(false, "同名");
                string resourceKey = CategoryIcons.ConfigKey(true, "同名");
                AppConfig config = new AppConfig();
                config.CategoryIconNames[softwareKey] = CategoryIconKind.Code.ToString();
                config.CategoryIconNames[resourceKey] = CategoryIconKind.Photo.ToString();
                config.SaveTo(tmp);
                AppConfig loaded = AppConfig.LoadFrom(tmp);
                bool ok = softwareKey != resourceKey
                    && loaded.CategoryIconNames[softwareKey] == CategoryIconKind.Code.ToString()
                    && loaded.CategoryIconNames[resourceKey] == CategoryIconKind.Photo.ToString();
                Assert(ok, "软件=" + loaded.CategoryIconNames[softwareKey] + " 资源=" + loaded.CategoryIconNames[resourceKey]);
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }

        private static void Case59_LyricsReadLimit()
        {
            Begin("59 超大歌词文件按上限读取");
            string path = TempFile(".mp3");
            string lyricPath = Path.ChangeExtension(path, ".lrc");
            try
            {
                File.WriteAllBytes(path, new byte[0]);
                File.WriteAllText(lyricPath, new string('a', MusicMetadata.MaxLyricsChars + 4096), Encoding.UTF8);
                MusicTrackInfo info = MusicMetadata.Read(path);
                bool ok = info.Lyrics != null && info.Lyrics.Length == MusicMetadata.MaxLyricsChars;
                info.Dispose();
                Assert(ok, "读取字符=" + (info.Lyrics == null ? -1 : info.Lyrics.Length));
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                try { if (File.Exists(lyricPath)) File.Delete(lyricPath); } catch { }
            }
        }

        private static void Case60_PathBoundaryNormalization()
        {
            Begin("60 路径边界规范化相对路径与父目录片段");
            string root = Path.Combine(Paths.DataDir, "Boundary");
            string inside = Path.Combine(root, "child", "..", "target");
            string outside = Path.Combine(root, "..", "outside");
            bool ok = MainForm.IsUnder(root, inside)
                && MainForm.IsUnder(root.ToUpperInvariant(), inside)
                && !MainForm.IsUnder(root, outside);
            Assert(ok, "规范化内部=True 大小写=True 越界拦截=True");
        }

        private static void Case61_RestorePathBoundary()
        {
            Begin("61 收纳还原拒绝删除管理目录外路径");
            string outside = Path.Combine(Paths.DataDir, "outside-restore");
            string managed = Path.Combine(Paths.DesktopBackupDir, "restore-copy");
            try
            {
                Directory.CreateDirectory(outside);
                Directory.CreateDirectory(managed);
                DesktopOrganizer organizer = new DesktopOrganizer(new AppConfig());
                DesktopBackupEntry outsideEntry = new DesktopBackupEntry();
                outsideEntry.CurrentPath = outside;
                outsideEntry.IsDir = true;
                outsideEntry.IsCopy = true;
                DesktopBackupEntry managedEntry = new DesktopBackupEntry();
                managedEntry.CurrentPath = managed;
                managedEntry.IsDir = true;
                managedEntry.IsCopy = true;
                bool ok = !organizer.RestoreOne(outsideEntry)
                    && Directory.Exists(outside)
                    && organizer.RestoreOne(managedEntry)
                    && !Directory.Exists(managed)
                    && !DesktopOrganizer.IsManagedBackupPath(Paths.DesktopBackupDir);
                Assert(ok, "外部目录保留=True 管理副本清理=True 根目录拦截=True");
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally
            {
                try { if (Directory.Exists(outside)) Directory.Delete(outside, true); } catch { }
                try { if (Directory.Exists(managed)) Directory.Delete(managed, true); } catch { }
            }
        }

        private static void Case62_XmlDtdRejected()
        {
            Begin("62 XML 配置禁止 DTD 且保留普通文档读取");
            string path = TempFile(".xml");
            try
            {
                File.WriteAllText(path, "<!DOCTYPE config [<!ENTITY test 'blocked'>]><config>&test;</config>", Encoding.UTF8);
                bool rejected = false;
                try { SafeXml.Load(path); }
                catch (System.Xml.XmlException) { rejected = true; }
                File.WriteAllText(path, "<config><value>ok</value></config>", Encoding.UTF8);
                System.Xml.XmlDocument doc = SafeXml.Load(path);
                bool ok = rejected && doc.DocumentElement != null && doc.DocumentElement.Name == "config";
                Assert(ok, "DTD拒绝=" + rejected + " 普通XML=True");
            }
            catch (Exception ex) { Assert(false, ex.Message); }
            finally { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        }

        private static void Case63_DpiScaling()
        {
            Begin("63 DPI 逻辑像素按 100/125/150/175% 缩放");
            bool ok = DpiHelper.Scale(240, 96) == 240
                && DpiHelper.Scale(240, 120) == 300
                && DpiHelper.Scale(240, 144) == 360
                && DpiHelper.Scale(240, 168) == 420
                && DpiHelper.Scale(34, 144) == 51;
            Assert(ok, "100%=240 125%=300 150%=360 175%=420");
        }

        private static void WriteBox(Stream s, string type, byte[] payload)
        {
            byte[] header = new byte[8];
            WriteU32BE(header, 0, (uint)(8 + payload.Length));
            header[4] = (byte)type[0]; header[5] = (byte)type[1];
            header[6] = (byte)type[2]; header[7] = (byte)type[3];
            s.Write(header, 0, 8);
            s.Write(payload, 0, payload.Length);
        }

        private static byte[] Box(string type, byte[] payload)
        {
            byte[] buf = new byte[8 + payload.Length];
            WriteU32BE(buf, 0, (uint)buf.Length);
            buf[4] = (byte)type[0]; buf[5] = (byte)type[1];
            buf[6] = (byte)type[2]; buf[7] = (byte)type[3];
            Array.Copy(payload, 0, buf, 8, payload.Length);
            return buf;
        }

        private static void WriteU32BE(byte[] buf, int offset, uint v)
        {
            buf[offset] = (byte)(v >> 24);
            buf[offset + 1] = (byte)(v >> 16);
            buf[offset + 2] = (byte)(v >> 8);
            buf[offset + 3] = (byte)v;
        }

        /// <summary>查找与给定根不同的、就绪的本地固定卷（供跨卷搬迁用例使用），无则返回 null。</summary>
        private static string OtherFixedVolume(string currentRoot)
        {
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                    if (string.Equals(d.Name, currentRoot, StringComparison.OrdinalIgnoreCase)) continue;
                    return d.Name;
                }
            }
            catch { }
            return null;
        }

        private static void TryDeleteTree(string dir)
        {
            try { if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { }
        }

        // ===== 日志辅助 =====

        private static void Begin(string title)
        {
            _total++;
            Log("[用例] " + title);
        }

        private static void Assert(bool ok, string detail)
        {
            if (ok) { _pass++; Log("  -> 通过: " + detail); }
            else { Log("  -> 失败: " + detail); }
        }

        /// <summary>追加一行并立即落盘：个别用例卡住时，可从日志末尾定位最后执行到的位置。</summary>
        private static void Log(string s)
        {
            _log.AppendLine(s);
            Flush();
        }

        private static void Flush()
        {
            try
            {
                if (!Directory.Exists(AppDir)) Directory.CreateDirectory(AppDir);
                File.WriteAllText(LogPath, _log.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        private static string ConfigFilePath()
        {
            return Paths.ConfigFile;   // 跟随隔离注入：自测期间指向临时目录
        }

        /// <summary>临时文件路径（供导出/导入等用例使用，用后即删，避免副作用）。</summary>
        private static string TempFile(string ext)
        {
            return Path.Combine(Path.GetTempPath(), "AppManagerTest_" + Guid.NewGuid().ToString("N") + ext);
        }

        private static string BackupConfig()
        {
            string p = ConfigFilePath();
            if (!File.Exists(p)) return null;
            string bak = p + ".selftestbak";
            try { File.Copy(p, bak, true); } catch { }
            return bak;
        }

        private static void RestoreConfig(string bak)
        {
            string p = ConfigFilePath();
            try
            {
                if (bak != null && File.Exists(bak))
                {
                    File.Copy(bak, p, true);
                    File.Delete(bak);
                }
                else if (File.Exists(p))
                {
                    File.Delete(p);
                }
            }
            catch { }
        }
    }
}
