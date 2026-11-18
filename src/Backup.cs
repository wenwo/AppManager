using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using AppManager.Controls;

namespace AppManager
{
    /// <summary>一份布局备份的概要信息。</summary>
    internal class LayoutBackupInfo
    {
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string Created = string.Empty;
        public int StashCount;
        public int CustomCount;
        public int CategoryCount;
        public long SizeBytes;
        public bool IsPreRestore;
        public bool Incomplete;

        public string SizeText { get { return FormatSize(SizeBytes); } }

        private static string FormatSize(long bytes)
        {
            if (bytes < 0) return "大小未统计";
            if (bytes < 1024) { return bytes + " B"; }
            if (bytes < 1024 * 1024) { return (bytes / 1024) + " KB"; }
            return (bytes / 1024 / 1024) + " MB";
        }
    }

    /// <summary>
    /// 布局备份：把「配置 + 桌面收纳目录」整体快照成一份具名备份，可保存多份并随时恢复。
    /// 备份位于 %LOCALAPPDATA%\AppManager\Backups\&lt;id&gt;\（config.xml + 收纳目录 + meta.xml）。
    /// </summary>
    internal static class LayoutBackup
    {
        private static readonly object listLock = new object();
        public static string RootDir { get { return Paths.BackupsDir; } }

        /// <summary>默认名字：按时间起名，便于在列表里区分。</summary>
        public static string DefaultName()
        {
            return "布局备份 " + DateTime.Now.ToString("MM-dd HH:mm");
        }

        public static List<LayoutBackupInfo> List()
        {
            // 等上一次读取（例如刚关掉的窗口留下的后台统计）结束，不返回空列表：空列表会被显示成「还没有备份」。
            lock (listLock) return ListCore();
        }

        private static List<LayoutBackupInfo> ListCore()
        {
            List<LayoutBackupInfo> result = new List<LayoutBackupInfo>();
            try
            {
                if (!Directory.Exists(RootDir)) { return result; }
                foreach (string dir in Directory.EnumerateDirectories(RootDir))
                {
                    string id = Path.GetFileName(dir);
                    bool preRestore = id.StartsWith("_prerestore_", StringComparison.Ordinal);
                    if (id.StartsWith("_", StringComparison.Ordinal) && !preRestore) { continue; }
                    if (id.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) { continue; }
                    if (id.EndsWith(".old", StringComparison.OrdinalIgnoreCase)) { continue; }
                    try { SafeDirectory.EnsureNoLinks(dir); }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    if (!File.Exists(Path.Combine(dir, "config.xml"))) { continue; }

                    LayoutBackupInfo info = new LayoutBackupInfo();
                    info.Id = id;
                    info.Name = id;
                    info.IsPreRestore = preRestore;
                    if (preRestore)
                    {
                        // 恢复前留底同样可以恢复或删除，必须出现在列表里，不能只留在磁盘深处。
                        info.Name = "恢复前留底 " + id.Substring("_prerestore_".Length);
                        info.Incomplete = File.Exists(Path.Combine(dir, "incomplete"));
                        try { info.Created = Directory.GetLastWriteTime(dir).ToString("yyyy-MM-dd HH:mm:ss"); } catch { }
                        info.StashCount = CountTop(Path.Combine(dir, "DesktopBackup")) + CountTop(Path.Combine(dir, "DesktopStash"));
                    }
                    else
                    {
                        // 只认真正的布局备份：config.xml + meta.xml 缺一不可
                        // （部署时留的 deploy_before_* 快照只有 config.xml，不在列表里冒充备份）
                        if (!File.Exists(Path.Combine(dir, "meta.xml"))) { continue; }
                        ReadMeta(dir, info);
                    }
                    // 每份备份独立预算：单份过大只显示“大小未统计”，不能截断后续备份列表。
                    info.SizeBytes = DirSize(dir, LayoutBudget());
                    result.Add(info);
                }
            }
            catch (Exception ex)
            {
                Paths.LogError(ex);
            }

            // 新的排在前面
            result.Sort(delegate(LayoutBackupInfo a, LayoutBackupInfo b)
            {
                return string.Compare(b.Id, a.Id, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        private static int CountTop(string dir)
        {
            try { return Directory.Exists(dir) ? Directory.GetFileSystemEntries(dir).Length : 0; }
            catch { return 0; }
        }

        private static SafeDirectory.Budget LayoutBudget()
        {
            return new SafeDirectory.Budget(2000000, 100L * 1024 * 1024 * 1024, 600000);
        }

        private static void ReadMeta(string dir, LayoutBackupInfo info)
        {
            try
            {
                string metaFile = Path.Combine(dir, "meta.xml");
                if (!File.Exists(metaFile)) return;
                XmlDocument doc = SafeXml.Load(metaFile);
                XmlNode root = doc.DocumentElement;
                if (root == null) return;

                string name = Attr(root, "name");
                string created = Attr(root, "created");
                if (name.Length > 0) info.Name = name;
                if (created.Length > 0) info.Created = created;

                int n;
                if (int.TryParse(Attr(root, "stash"), out n)) info.StashCount = n;
                if (int.TryParse(Attr(root, "custom"), out n)) info.CustomCount = n;
                if (int.TryParse(Attr(root, "categories"), out n)) info.CategoryCount = n;
            }
            catch (Exception ex)
            {
                Paths.LogError(ex);
            }
        }

        private static string Attr(XmlNode node, string name)
        {
            return (node.Attributes != null && node.Attributes[name] != null)
                ? node.Attributes[name].Value : string.Empty;
        }

        private static long DirSize(string dir, SafeDirectory.Budget budget)
        {
            try { return SafeDirectory.Measure(dir, budget); }
            catch { return -1; } // 未完成的扫描不能显示成精确大小。
        }

        /// <summary>把当前布局另存为一份备份。</summary>
        public static bool Create(string name, out string id, out string error)
        {
            id = null;
            error = null;
            lock (DesktopOrganizer.OperationSync)
            {
                try
                {
                    Directory.CreateDirectory(RootDir);
                    CleanupTemporaryDirectories();

                    string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    string dirId = stamp;
                    int n = 2;
                    while (Directory.Exists(Path.Combine(RootDir, dirId)))
                    {
                        dirId = stamp + "-" + n;
                        n++;
                    }

                    string target = Path.Combine(RootDir, dirId);
                    string temp = target + ".tmp";
                    if (Directory.Exists(temp)) { Directory.Delete(temp, true); }
                    Directory.CreateDirectory(temp);

                    string showName = string.IsNullOrEmpty(name) ? DefaultName() : name.Trim();
                    try
                    {
                        WriteSnapshot(temp, showName, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        Directory.Move(temp, target);
                    }
                    catch
                    {
                        TryDeleteDirectory(temp);   // 失败的半成品不能留在备份目录里
                        throw;
                    }
                    id = dirId;
                    return true;
                }
                catch (Exception ex)
                {
                    Paths.LogError(ex);
                    error = ex.Message;
                    return false;
                }
            }
        }

        /// <summary>把当前布局（配置 + 桌面收纳）写进指定目录，并生成 meta.xml。</summary>
        private static void WriteSnapshot(string dir, string showName, string created)
        {
            // 先把内存配置落盘，保证快照与当前界面一致
            AppConfig cfg = AppConfig.Load();
            if (cfg.LoadFailed) throw new InvalidDataException("当前配置读取失败，已取消备份。");
            if (!cfg.SaveTo(Path.Combine(dir, "config.xml")))
                throw new IOException("无法写入备份配置。");
            if (cfg.Database != null) cfg.Database.Backup(Path.Combine(dir, "appmanager.db"));

            CopyDir(Paths.DesktopBackupDir, Path.Combine(dir, "DesktopBackup"));
            CopyDir(Paths.LegacyStashDir, Path.Combine(dir, "DesktopStash"));

            XmlWriterSettings s = new XmlWriterSettings();
            s.Indent = true;
            s.Encoding = new System.Text.UTF8Encoding(false);
            using (XmlWriter w = XmlWriter.Create(Path.Combine(dir, "meta.xml"), s))
            {
                w.WriteStartElement("backup");
                w.WriteAttributeString("name", showName ?? string.Empty);
                w.WriteAttributeString("created", created);
                w.WriteAttributeString("stash", cfg.DesktopBackup.Count.ToString());
                w.WriteAttributeString("custom", cfg.AddedItems.Count.ToString());
                w.WriteAttributeString("categories", cfg.CustomCategories.Count.ToString());
                w.WriteEndElement();
            }
        }

        /// <summary>读取备份的名称与创建时间，缺失时保持不变。</summary>
        private static void ReadNameCreated(string metaFile, ref string showName, ref string created)
        {
            try
            {
                if (!File.Exists(metaFile)) return;
                XmlDocument doc = SafeXml.Load(metaFile);
                XmlNode root = doc.DocumentElement;
                if (root == null) return;
                string name = Attr(root, "name");
                string when = Attr(root, "created");
                if (name.Length > 0) showName = name;
                if (when.Length > 0) created = when;
            }
            catch (Exception ex)
            {
                Paths.LogError(ex);
            }
        }

        /// <summary>
        /// 用当前布局刷新一份已有备份：名称与创建时间保持不变，只把内容替换成当前状态。
        /// 先在临时目录写完整份快照再整体换上去，中途失败不会破坏原备份。
        /// </summary>
        public static bool Update(string id, out string error)
        {
            error = null;
            lock (DesktopOrganizer.OperationSync)
            {
                try
                {
                    CleanupTemporaryDirectories();
                    string target = Path.Combine(RootDir, id);
                    string metaFile = Path.Combine(target, "meta.xml");
                    if (!File.Exists(metaFile))
                    {
                        error = "备份不完整（缺少 meta.xml）。";
                        return false;
                    }

                    string showName = id;
                    string created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    ReadNameCreated(metaFile, ref showName, ref created);

                    string temp = target + ".tmp";
                    if (Directory.Exists(temp)) { Directory.Delete(temp, true); }
                    Directory.CreateDirectory(temp);
                    try { WriteSnapshot(temp, showName, created); }
                    catch
                    {
                        TryDeleteDirectory(temp);
                        throw;
                    }

                    // 先把旧备份整体挪到 .old 留底，再换入新快照；换入失败则回滚，任何时刻都有一份完整备份
                    string old = target + ".old";
                    if (Directory.Exists(old)) { Directory.Delete(old, true); }
                    if (Directory.Exists(target)) { Directory.Move(target, old); }
                    try
                    {
                        Directory.Move(temp, target);
                    }
                    catch
                    {
                        if (!Directory.Exists(target) && Directory.Exists(old)) Directory.Move(old, target);
                        throw;
                    }
                    if (Directory.Exists(old)) { Directory.Delete(old, true); }
                    return true;
                }
                catch (Exception ex)
                {
                    Paths.LogError(ex);
                    error = ex.Message;
                    return false;
                }
            }
        }

        /// <summary>
        /// 恢复到指定备份：用备份里的配置与收纳目录覆盖当前状态。
        /// 当前收纳目录先整体挪到 Backups\_prerestore_&lt;时间&gt; 下留底，避免误删用户文件。
        /// </summary>
        /// <summary>
        /// 快照清单里没有、当前清单里有且文件仍在的收纳条目（快照之后新收纳的）：
        /// 所在目录被快照替换时，把文件从留底目录挪回新装的收纳目录（同名另取名）；目录未被替换时原地保留。
        /// 记录一并加入恢复后的清单。moves 记录（留底位置, 新位置），失败回滚时据此放回。
        /// </summary>
        private static void CarryNewerStash(AppConfig live, AppConfig restored, bool replaceDesktop, string oldDesktop,
            bool replaceStash, string oldStash, List<string[]> moves)
        {
            HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DesktopBackupEntry e in restored.DesktopBackup)
                if (e != null && !string.IsNullOrEmpty(e.CurrentPath)) known.Add(e.CurrentPath);
            foreach (DesktopBackupEntry e in live.DesktopBackup)
            {
                if (e == null || string.IsNullOrEmpty(e.CurrentPath) || known.Contains(e.CurrentPath)) continue;
                string target = e.CurrentPath;
                string aside = null;
                string rel = RelativeUnder(Paths.DesktopBackupDir, target);
                if (rel != null && replaceDesktop) aside = Path.Combine(oldDesktop, rel);
                else
                {
                    rel = RelativeUnder(Paths.LegacyStashDir, target);
                    if (rel != null && replaceStash) aside = Path.Combine(oldStash, rel);
                }
                if (aside != null)
                {
                    if (!(e.IsDir ? Directory.Exists(aside) : File.Exists(aside))) continue;
                    if (File.Exists(target) || Directory.Exists(target)) target = UniqueSibling(target);
                    string parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    if (e.IsDir) Directory.Move(aside, target); else File.Move(aside, target);
                    moves.Add(new string[] { aside, target });
                }
                else if (!(e.IsDir ? Directory.Exists(target) : File.Exists(target))) continue;
                restored.DesktopBackup.Add(new DesktopBackupEntry
                {
                    OriginalPath = e.OriginalPath, CurrentPath = target,
                    IsShortcut = e.IsShortcut, IsDir = e.IsDir, IsCopy = e.IsCopy
                });
                known.Add(target);
            }
        }

        /// <summary>path 位于 root 之内时返回相对路径，否则返回 null。</summary>
        private static string RelativeUnder(string root, string path)
        {
            try
            {
                string prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";
                string full = Path.GetFullPath(path);
                return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && full.Length > prefix.Length
                    ? full.Substring(prefix.Length) : null;
            }
            catch { return null; }
        }

        private static string UniqueSibling(string path)
        {
            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                string candidate = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
        }

        public static bool Restore(string id, out string error)
        {
            error = null;
            lock (DesktopOrganizer.OperationSync)
            {
                string transaction = null;
                try
                {
                    string dir = Path.Combine(RootDir, id);
                    string cfgFile = Path.Combine(dir, "config.xml");
                    if (!File.Exists(cfgFile))
                    {
                        error = "备份不完整（缺少 config.xml）。";
                        return false;
                    }
                    AppConfig restoredConfig = AppConfig.ImportFrom(cfgFile);
                    if (restoredConfig == null || restoredConfig.LoadFailed)
                    {
                        error = "备份配置已损坏，已取消恢复。";
                        return false;
                    }
                    string validation = restoredConfig.ValidateImportedContent(false);
                    if (validation != null)
                    {
                        error = "备份配置包含无效名称，已取消恢复：" + validation;
                        return false;
                    }

                    string snapStash = Path.Combine(dir, "DesktopStash");
                    if (!Directory.Exists(snapStash) && Directory.Exists(Path.Combine(dir, "stash")))
                        snapStash = Path.Combine(dir, "stash");
                    string snapDesktop = Path.Combine(dir, "DesktopBackup");
                    bool replaceDesktop = Directory.Exists(snapDesktop);
                    bool replaceStash = Directory.Exists(snapStash);

                    CheckRestoreCapacity();
                    transaction = Path.Combine(RootDir, "_prerestore_" + DateTime.Now.ToString("yyyyMMdd-HHmmss")
                        + "-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(transaction);
                    // 成功完成后移除；异常中断留下的残留会带此标记，不再永久占用 3 份额度。
                    File.WriteAllText(Path.Combine(transaction, "incomplete"), string.Empty);
                    string oldConfig = Path.Combine(transaction, "config.xml");
                    AppConfig liveConfig = AppConfig.Load();
                    if (liveConfig.LoadFailed || !liveConfig.SaveTo(oldConfig)) throw new IOException("无法保存恢复前配置。");
                    bool hadConfig = true;

                    string newDesktop = Path.Combine(transaction, "newDesktopBackup");
                    string newStash = Path.Combine(transaction, "newDesktopStash");
                    if (replaceDesktop) CopyDir(snapDesktop, newDesktop);
                    if (replaceStash) CopyDir(snapStash, newStash);

                    string oldDesktop = Path.Combine(transaction, "DesktopBackup");
                    string oldStash = Path.Combine(transaction, "DesktopStash");
                    bool desktopAside = false, desktopInstalled = false;
                    bool stashAside = false, stashInstalled = false;
                    List<string[]> carried = new List<string[]>();
                    try
                    {
                    if (replaceDesktop)
                    {
                        if (Directory.Exists(Paths.DesktopBackupDir))
                        {
                            Directory.Move(Paths.DesktopBackupDir, oldDesktop);
                            desktopAside = true;
                        }
                        Directory.Move(newDesktop, Paths.DesktopBackupDir);
                        desktopInstalled = true;
                    }
                    if (replaceStash)
                    {
                        if (Directory.Exists(Paths.LegacyStashDir))
                        {
                            Directory.Move(Paths.LegacyStashDir, oldStash);
                            stashAside = true;
                        }
                        Directory.Move(newStash, Paths.LegacyStashDir);
                        stashInstalled = true;
                    }
                    // 快照之后才收纳的条目：文件挪回新收纳目录、记录并入恢复后的清单，否则它们会失去还原入口
                    CarryNewerStash(liveConfig, restoredConfig, replaceDesktop, oldDesktop, replaceStash, oldStash, carried);
                    if (hadConfig)
                        File.Copy(oldConfig, Path.Combine(Paths.DataDir, "config.before-restore.xml"), true);
                        if (!restoredConfig.Save("physical.layout.restore")) throw new IOException("恢复配置写入失败：" + restoredConfig.DatabaseError);
                    }
                    catch
                    {
                    // 先把挪进新目录的较新收纳文件放回留底目录，再删除新装的目录，不能连带删掉
                    for (int m = carried.Count - 1; m >= 0; m--)
                    {
                        string from = carried[m][0], to = carried[m][1];
                        try
                        {
                            if (Directory.Exists(to) && !Directory.Exists(from)) Directory.Move(to, from);
                            else if (File.Exists(to) && !File.Exists(from)) File.Move(to, from);
                        }
                        catch (Exception moveBack) { Paths.LogError(moveBack); }
                    }
                    if (desktopInstalled && Directory.Exists(Paths.DesktopBackupDir))
                        Directory.Delete(Paths.DesktopBackupDir, true);
                    if (desktopAside && Directory.Exists(oldDesktop) && !Directory.Exists(Paths.DesktopBackupDir))
                        Directory.Move(oldDesktop, Paths.DesktopBackupDir);
                    if (stashInstalled && Directory.Exists(Paths.LegacyStashDir))
                        Directory.Delete(Paths.LegacyStashDir, true);
                    if (stashAside && Directory.Exists(oldStash) && !Directory.Exists(Paths.LegacyStashDir))
                        Directory.Move(oldStash, Paths.LegacyStashDir);
                        throw;
                    }
                    try { File.Delete(Path.Combine(transaction, "incomplete")); } catch { }
                    return true;
                }
                catch (Exception ex)
                {
                    Paths.LogError(ex);
                    error = ex.Message;
                    return false;
                }
            }
        }

        /// <summary>清理上次中断留下的 .tmp 半成品目录；调用方必须持有 OperationSync。</summary>
        private static void CleanupTemporaryDirectories()
        {
            try
            {
                if (!Directory.Exists(RootDir)) return;
                foreach (string dir in Directory.EnumerateDirectories(RootDir))
                {
                    string name = Path.GetFileName(dir);
                    if (!name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    TryDeleteDirectory(dir);
                }
            }
            catch (Exception ex) { Paths.LogError(ex); }
        }

        private static void TryDeleteDirectory(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (Exception ex) { Paths.LogError(ex); }
        }

        public static bool Delete(string id, out string error)
        {
            error = null;
            try
            {
                string dir = Path.Combine(RootDir, id);
                if (Directory.Exists(dir)) { Directory.Delete(dir, true); }
                return true;
            }
            catch (Exception ex)
            {
                Paths.LogError(ex);
                error = ex.Message;
                return false;
            }
        }

        public static bool Rename(string id, string name, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrEmpty(name) || name.Trim().Length == 0)
                {
                    error = "名称不能为空。";
                    return false;
                }
                string metaFile = Path.Combine(RootDir, id, "meta.xml");
                if (!File.Exists(metaFile))
                {
                    error = "备份不完整（缺少 meta.xml）。";
                    return false;
                }

                string showName = id;
                string created = string.Empty;
                ReadNameCreated(metaFile, ref showName, ref created);

                XmlWriterSettings s = new XmlWriterSettings();
                s.Indent = true;
                s.Encoding = new System.Text.UTF8Encoding(false);
                using (XmlWriter w = XmlWriter.Create(metaFile + ".tmp", s))
                {
                    w.WriteStartElement("backup");
                    w.WriteAttributeString("name", name.Trim());
                    w.WriteAttributeString("created", created);
                    LayoutBackupInfo info = new LayoutBackupInfo();
                    ReadMeta(Path.Combine(RootDir, id), info);
                    w.WriteAttributeString("stash", info.StashCount.ToString());
                    w.WriteAttributeString("custom", info.CustomCount.ToString());
                    w.WriteAttributeString("categories", info.CategoryCount.ToString());
                    w.WriteEndElement();
                }
                if (File.Exists(metaFile)) File.Delete(metaFile);
                File.Move(metaFile + ".tmp", metaFile);
                return true;
            }
            catch (Exception ex)
            {
                Paths.LogError(ex);
                error = ex.Message;
                return false;
            }
        }

        private static void CopyDir(string from, string to)
        {
            if (!Directory.Exists(from)) return;
            SafeDirectory.Copy(from, to, true, LayoutBudget());
        }

        // 留底可能是唯一副本；只统计完整留底，异常残留由界面列出供用户安全删除。
        private static void CheckRestoreCapacity()
        {
            int count = 0;
            foreach (string dir in Directory.EnumerateDirectories(RootDir, "_prerestore_*"))
            {
                if (File.Exists(Path.Combine(dir, "incomplete"))) continue;
                if (++count >= 3)
                    throw new IOException("恢复留底已达 3 份，请先在布局备份列表中检查并删除旧留底：" + RootDir);
            }
        }
    }

    /// <summary>
    /// 布局备份面板：嵌在设置页右侧（不再另开窗口）。上方一行是结果提示和「新建备份」，下面是卡片列表；
    /// 恢复、覆盖、重命名、删除都在卡片上发起，会改动数据的操作先确认。结果直接写在面板的提示行里，不另弹提示框。
    /// 恢复成功后触发 Restored，由主窗口重新加载；操作进行中 Busy 为真，设置页据此挡住返回和切换。
    /// </summary>
    internal sealed class LayoutBackupPanel : Control, ISettingsPanel
    {
        private const string CreateText = "新建备份";
        private const int MenuWidth = 176;
        private const int ToolbarH = 34;
        private const int Shadow = 8;   // 与设置页约定：左右各多留 8px 放卡片阴影，按钮和文字向内缩进同样距离

        private readonly BackupListControl list;
        private readonly RoundButton btnCreate;
        private bool operationRunning;
        private int reloadGeneration;
        private bool reloadRunning;
        private bool reloadPending;
        private string pendingSelection;
        /// <summary>操作成功后保持忙碌，直到重新读出的列表换上，避免在已删除或已改名的旧卡片上继续操作。</summary>
        private bool busyUntilReload;
        private string status = string.Empty;   // 上一次操作的结果
        private List<LayoutBackupInfo> shownItems = new List<LayoutBackupInfo>();   // 列表上当前显示的备份（新建时避开重名）
        private bool statusWarning;
        private volatile bool disposed;

        /// <summary>恢复成功：布局已换成备份里的样子，主窗口需要重新加载配置与列表。</summary>
        public event EventHandler Restored;
        public event EventHandler BusyChanged;

        public LayoutBackupPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Background;

            btnCreate = new RoundButton();
            btnCreate.Text = CreateText;
            btnCreate.Icon = UiIconKind.Add;
            btnCreate.Accent = true;
            btnCreate.Click += delegate { CreateBackup(); };
            Controls.Add(btnCreate);

            list = new BackupListControl();
            list.RestoreRequested += RestoreBackup;
            list.RenameRequested += RenameBackup;
            list.DeleteRequested += DeleteBackup;
            list.MenuRequested += ShowCardMenu;
            Controls.Add(list);
        }

        public bool Busy { get { return operationRunning || busyUntilReload; } }

        private int Dpi(int value)
        {
            return DpiHelper.Scale(value, DpiHelper.ForControl(this));
        }

        /// <summary>子控件位置由 OnResize 按当前 DPI 计算，不叠加框架缩放。</summary>
        protected override bool ScaleChildren { get { return false; } }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int w = Dpi(116);
            btnCreate.Bounds = new Rectangle(Math.Max(0, Width - Dpi(Shadow) - w), 0, w, Dpi(ToolbarH));
            int top = Dpi(ToolbarH) + Dpi(10);
            list.Bounds = new Rectangle(0, top, Width, Math.Max(0, Height - top));
            Invalidate();
        }

        /// <summary>每次显示时重新读取列表：备份目录可能在别处被改动过。</summary>
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && IsHandleCreated && !operationRunning) ReloadList(list.Selected == null ? null : list.Selected.Id);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (Visible) ReloadList();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (string.IsNullOrEmpty(status)) return;
            Rectangle rect = new Rectangle(Dpi(Shadow), 0, Math.Max(0, btnCreate.Left - Dpi(Shadow) - Dpi(12)), Dpi(ToolbarH));
            using (SolidBrush brush = new SolidBrush(statusWarning ? Theme.WarningText : Theme.SuccessText))
                Draw.DrawStringLeft(e.Graphics, status, Theme.CaptionFont, brush, rect);
        }

        private void SetStatus(string text, bool warning)
        {
            status = text ?? string.Empty;
            statusWarning = warning;
            Invalidate();
        }

        public bool ScrollByWheel(int delta)
        {
            list.ScrollByWheel(delta);
            return true;
        }

        /// <summary>指针停在按钮或提示行上时，滚轮同样滚动列表。</summary>
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            list.ScrollByWheel(e.Delta);
        }

        protected override void Dispose(bool disposing)
        {
            disposed = true;
            base.Dispose(disposing);
        }

        /// <summary>后台线程把结果交回界面线程；面板已释放或句柄未建时丢弃。</summary>
        private void Post(Action action)
        {
            if (disposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)delegate { if (!disposed && !IsDisposed) action(); }); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void ReloadList()
        {
            ReloadList(null);
        }

        private void ReloadList(string selectId)
        {
            if (reloadRunning)
            {
                reloadPending = true;
                pendingSelection = selectId;
                return;
            }
            reloadRunning = true;
            int generation = ++reloadGeneration;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<LayoutBackupInfo> loaded;
                try { loaded = LayoutBackup.List(); }
                catch (Exception ex) { Paths.LogError(ex); loaded = new List<LayoutBackupInfo>(); }
                Post(delegate
                {
                    if (generation != reloadGeneration) return;
                    reloadRunning = false;
                    list.SetItems(loaded, selectId);
                    shownItems = loaded;
                    if (reloadPending)
                    {
                        reloadPending = false;
                        ReloadList(pendingSelection);
                    }
                    else if (busyUntilReload)
                    {
                        busyUntilReload = false;
                        ShowBusy(false, null, null);
                    }
                });
            });
        }

        // ---------- 菜单 ----------

        /// <summary>
        /// 卡片的操作菜单。来自「更多」按钮时贴着按钮右下方展开，只列次要操作；
        /// 右键或菜单键时在指针处展开，并把「恢复」放在最前面。
        /// </summary>
        private void ShowCardMenu(LayoutBackupInfo info, Rectangle anchor, bool fromButton)
        {
            if (Busy) return;
            List<MenuWindow.MenuItem> entries = new List<MenuWindow.MenuItem>();
            if (!fromButton && !info.Incomplete) entries.Add(MenuEntry("恢复到这份备份", RestoreBackup, info));
            if (!info.IsPreRestore)
            {
                if (entries.Count > 0) entries.Add(new MenuWindow.MenuItem { Separator = true });
                entries.Add(MenuEntry("用当前布局覆盖", UpdateBackup, info));
                entries.Add(MenuEntry("重命名", RenameBackup, info));
            }
            if (entries.Count > 0) entries.Add(new MenuWindow.MenuItem { Separator = true });
            entries.Add(MenuEntry("删除", DeleteBackup, info));

            MenuWindow menu = new MenuWindow(entries, MenuWidth);
            // 菜单显示时才按 DPI 放大：这里先按放大后的宽度算位置，右边缘才能与按钮对齐
            int width = Dpi(MenuWidth);
            Point point = fromButton ? new Point(anchor.Right - width, anchor.Bottom + Dpi(4)) : anchor.Location;
            Rectangle work = Screen.GetWorkingArea(point);
            menu.Location = new Point(Math.Max(work.Left, Math.Min(point.X, work.Right - width)),
                Math.Max(work.Top, Math.Min(point.Y, work.Bottom - menu.Height)));
            menu.Show(FindForm());
        }

        /// <summary>菜单项的动作延后到菜单窗口关闭之后执行：那时主窗口已重新成为活动窗口，确认框才会以它为宿主居中。</summary>
        private MenuWindow.MenuItem MenuEntry(string text, Action<LayoutBackupInfo> action, LayoutBackupInfo info)
        {
            MenuWindow.MenuItem entry = new MenuWindow.MenuItem();
            entry.Text = text;
            entry.Action = delegate { Post(delegate { action(info); }); };
            return entry;
        }

        /// <summary>确认框和提示里引用的备份名：过长时截断，保证一行放得下。</summary>
        private static string Quote(LayoutBackupInfo info)
        {
            string name = BackupListControl.DisplayName(info);
            if (name.Length > 18) name = name.Substring(0, 17) + "…";
            return "「" + name + "」";
        }

        // ---------- 操作 ----------

        /// <summary>直接按时间命名新建（不再弹出命名框）；需要改名时在卡片菜单里重命名。</summary>
        private void CreateBackup()
        {
            if (Busy) return;
            string name = UniqueName(LayoutBackup.DefaultName(), shownItems);
            string id = null;
            RunBackupOperation(null, "正在备份…", "新建备份失败：", delegate
            {
                string error;
                return LayoutBackup.Create(name, out id, out error) ? null : error;
            }, delegate
            {
                SetStatus("已新建备份「" + name + "」。需要换个名字，可以在卡片的「更多」菜单里重命名。", false);
                ReloadList(id);
            });
        }

        /// <summary>
        /// 默认名按分钟取时间，同一分钟内连续新建会重名：与列表里已有的名字相同时依次加「(2)」「(3)」…，
        /// 卡片上能分清是哪一份。只比较用户自己的备份，恢复前留底另有名称。
        /// </summary>
        internal static string UniqueName(string name, IList<LayoutBackupInfo> existing)
        {
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (existing != null)
                foreach (LayoutBackupInfo info in existing)
                    if (info != null && !info.IsPreRestore && !string.IsNullOrEmpty(info.Name)) taken.Add(info.Name.Trim());
            if (!taken.Contains(name)) return name;
            for (int n = 2; ; n++)
            {
                string candidate = name + " (" + n + ")";
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        private void UpdateBackup(LayoutBackupInfo info)
        {
            if (Busy || info == null || info.IsPreRestore) return;
            if (!ConfirmDialog.Show("覆盖备份", "用当前布局覆盖" + Quote(info) + "？\r\n" +
                "备份里原来的内容会被替换，名称和创建时间不变。", "覆盖", "取消")) return;

            RunBackupOperation(info.Id, "正在覆盖…", "覆盖备份失败：", delegate
            {
                string error;
                return LayoutBackup.Update(info.Id, out error) ? null : error;
            }, delegate
            {
                SetStatus("已用当前布局覆盖" + Quote(info) + "。", false);
                ReloadList(info.Id);
            });
        }

        private void RestoreBackup(LayoutBackupInfo info)
        {
            if (Busy || info == null) return;
            if (info.Incomplete)
            {
                SetStatus("这份恢复前残留可能不完整，只能删除，不能恢复。", true);
                return;
            }
            if (!ConfirmDialog.Show("恢复备份", "恢复到" + Quote(info) + "？\r\n" +
                "当前布局会先自动留底，再换成这份备份的样子。", "恢复", "取消")) return;

            RunBackupOperation(info.Id, "正在恢复…", "恢复失败：", delegate
            {
                string error;
                return LayoutBackup.Restore(info.Id, out error) ? null : error;
            }, delegate
            {
                SetStatus("已恢复到" + Quote(info) + "。恢复前的布局已自动留底，在下面的列表里可以再恢复回去。", false);
                ReloadList(info.Id);   // 列出新生成的恢复前留底
                EventHandler handler = Restored;
                if (handler != null) handler(this, EventArgs.Empty);
            });
        }

        private void RenameBackup(LayoutBackupInfo info)
        {
            if (Busy || info == null || info.IsPreRestore) return;
            string name = InputDialog.Show("重命名备份", "输入新的备份名称。", info.Name, -1, delegate(string value)
            {
                return (value == null || value.Trim().Length == 0) ? "名称不能为空。" : null;
            });
            if (name == null || name.Trim() == info.Name) return;

            RunBackupOperation(info.Id, "正在重命名…", "重命名失败：", delegate
            {
                string error;
                return LayoutBackup.Rename(info.Id, name, out error) ? null : error;
            }, delegate
            {
                SetStatus("已重命名为「" + name.Trim() + "」。", false);
                ReloadList(info.Id);
            });
        }

        private void DeleteBackup(LayoutBackupInfo info)
        {
            if (Busy || info == null) return;
            string consequence = info.IsPreRestore
                ? "这是那次恢复前自动保存的布局，删除后无法找回。"
                : "备份文件会被永久删除，当前布局不受影响。";
            if (!ConfirmDialog.Show("删除备份", "删除" + Quote(info) + "？\r\n" + consequence, "删除", "取消")) return;

            RunBackupOperation(info.Id, "正在删除…", "删除失败：", delegate
            {
                string error;
                return LayoutBackup.Delete(info.Id, out error) ? null : error;
            }, delegate
            {
                SetStatus("已删除" + Quote(info) + "。", false);
                ReloadList();
            });
        }

        /// <summary>操作进行中：停用所有入口；busyId 对应的卡片显示进度文字，为空表示正在新建（文字显示在「新建备份」按钮上）。</summary>
        private void ShowBusy(bool running, string busyId, string busyText)
        {
            bool creating = running && busyId == null;
            UseWaitCursor = running;
            btnCreate.Enabled = !running;
            btnCreate.Text = creating ? busyText : CreateText;
            btnCreate.Icon = creating ? (UiIconKind?)null : UiIconKind.Add;
            btnCreate.Accent = !creating;
            if (running) list.SetBusy(busyId, busyText);
            else
            {
                list.ClearBusy();
                if (Visible && list.CanFocus) list.Focus();   // 焦点回到列表：新选中的卡片可以直接用键盘操作
            }
            EventHandler handler = BusyChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RunBackupOperation(string busyId, string busyText, string failPrefix, Func<string> operation, Action onSuccess)
        {
            if (Busy) return;
            operationRunning = true;
            SetStatus(string.Empty, false);
            ShowBusy(true, busyId, busyText);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                try { error = operation(); }
                catch (Exception ex) { error = ex.Message; Paths.LogError(ex); }
                Post(delegate
                {
                    operationRunning = false;
                    if (error != null)
                    {
                        ShowBusy(false, null, null);
                        // 失败也可能改动了磁盘（恢复中断会留下不完整的留底、删除可能只删了一半）：先按实际情况刷新列表
                        SetStatus(failPrefix + error, true);
                        ReloadList();
                        return;
                    }
                    // 成功后的刷新在后台统计目录大小，可能要几秒：期间保持停用，新列表换上后再解除
                    busyUntilReload = true;
                    onSuccess();
                    if (!reloadRunning && busyUntilReload)
                    {
                        busyUntilReload = false;
                        ShowBusy(false, null, null);
                    }
                });
            });
        }
    }
}
