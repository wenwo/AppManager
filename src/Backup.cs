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
            if (!Monitor.TryEnter(listLock)) return new List<LayoutBackupInfo>();
            try { return ListCore(); }
            finally { Monitor.Exit(listLock); }
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
    /// 布局备份管理对话框：卡片列表 + 右上角「新建备份」。恢复、覆盖、重命名、删除都在卡片上发起，
    /// 会改动数据的操作先确认；恢复成功后窗口自动关闭，由主窗口重新加载。
    /// </summary>
    internal class BackupForm : BufferedDialogForm
    {
        private const string CreateText = "新建备份";
        private const int MenuWidth = 176;

        private readonly BackupListControl list;
        private readonly RoundButton btnCreate;
        private bool operationRunning;
        private int reloadGeneration;
        private bool reloadRunning;
        private bool reloadPending;
        private string pendingSelection;

        /// <summary>是否执行过恢复（主窗口据此重载配置与重扫）。</summary>
        public bool Restored;

        public BackupForm()
        {
            Text = "布局备份";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            BackColor = Theme.Background;
            ClientSize = new Size(640, 560);
            try { using (GraphicsPath path = Draw.RoundedPath(ClientRectangle, Theme.CornerRadius)) Region = new Region(path); } catch { }

            IconButton btnClose = new IconButton();
            btnClose.Kind = IconKind.Close;
            btnClose.Bounds = new Rectangle(ClientSize.Width - 14 - 34, 18, 34, 30);
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);

            btnCreate = new RoundButton();
            btnCreate.Text = CreateText;
            btnCreate.Icon = UiIconKind.Add;
            btnCreate.Accent = true;
            btnCreate.Bounds = new Rectangle(btnClose.Left - 8 - 116, 16, 116, 34);
            btnCreate.Click += delegate { CreateBackup(); };
            Controls.Add(btnCreate);

            // 列表比标题左右各宽 8px：卡片自带 8px 留白放阴影，卡片边缘正好与标题对齐
            list = new BackupListControl();
            list.Bounds = new Rectangle(16, 84, ClientSize.Width - 32, ClientSize.Height - 84 - 16);
            list.RestoreRequested += RestoreBackup;
            list.RenameRequested += RenameBackup;
            list.DeleteRequested += DeleteBackup;
            list.MenuRequested += ShowCardMenu;
            Controls.Add(list);
            ActiveControl = list;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ReloadList();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            using (SolidBrush tb = new SolidBrush(Theme.TextPrimary))
                Draw.DrawStringLeft(g, "布局备份", Theme.DialogTitleFont, tb, new Rectangle(Dpi(24), Dpi(18), Dpi(300), Dpi(30)));
            using (SolidBrush db = new SolidBrush(Theme.TextSecondary))
                Draw.DrawStringLeft(g, "把分类、顺序、常用、隐藏与桌面收纳整体存一份，之后可以随时恢复。", Theme.CaptionFont, db,
                    new Rectangle(Dpi(24), Dpi(52), ClientSize.Width - Dpi(48), Dpi(20)));
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>焦点不在列表上（例如停在按钮上）时，滚轮同样滚动列表。</summary>
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            list.ScrollByWheel(e.Delta);
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
                List<LayoutBackupInfo> loaded = LayoutBackup.List();
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        if (IsDisposed || Disposing || generation != reloadGeneration) return;
                        reloadRunning = false;
                        list.SetItems(loaded, selectId);
                        if (reloadPending)
                        {
                            reloadPending = false;
                            ReloadList(pendingSelection);
                        }
                    });
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (operationRunning) e.Cancel = true;
            base.OnFormClosing(e);
        }

        // ---------- 菜单 ----------

        /// <summary>
        /// 卡片的操作菜单。来自「更多」按钮时贴着按钮右下方展开，只列次要操作；
        /// 右键或菜单键时在指针处展开，并把「恢复」放在最前面。
        /// </summary>
        private void ShowCardMenu(LayoutBackupInfo info, Rectangle anchor, bool fromButton)
        {
            if (operationRunning) return;
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
            menu.Show(this);
        }

        /// <summary>菜单项的动作延后到菜单窗口关闭之后执行：那时本窗口已重新成为活动窗口，确认框才会以它为宿主居中。</summary>
        private MenuWindow.MenuItem MenuEntry(string text, Action<LayoutBackupInfo> action, LayoutBackupInfo info)
        {
            MenuWindow.MenuItem entry = new MenuWindow.MenuItem();
            entry.Text = text;
            entry.Action = delegate
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate { if (!IsDisposed && !Disposing) action(info); });
            };
            return entry;
        }

        /// <summary>确认框里引用的备份名：过长时截断，保证一行放得下。</summary>
        private static string Quote(LayoutBackupInfo info)
        {
            string name = BackupListControl.DisplayName(info);
            if (name.Length > 18) name = name.Substring(0, 17) + "…";
            return "「" + name + "」";
        }

        // ---------- 操作 ----------

        private void CreateBackup()
        {
            if (operationRunning) return;
            string name = InputDialog.Show("新建备份", "给这份布局起个名字（留空则按时间命名）。", LayoutBackup.DefaultName());
            if (name == null) return;   // 取消
            name = name.Trim();         // 只输入了空格等同留空，按时间命名

            string id = null;
            RunBackupOperation(null, "正在备份…", "新建备份失败：", delegate
            {
                string error;
                return LayoutBackup.Create(name, out id, out error) ? null : error;
            }, delegate
            {
                ReloadList(id);
            });
        }

        private void UpdateBackup(LayoutBackupInfo info)
        {
            if (operationRunning || info == null || info.IsPreRestore) return;
            if (!ConfirmDialog.Show("覆盖备份", "用当前布局覆盖" + Quote(info) + "？\r\n" +
                "备份里原来的内容会被替换，名称和创建时间不变。", "覆盖", "取消")) return;

            RunBackupOperation(info.Id, "正在覆盖…", "覆盖备份失败：", delegate
            {
                string error;
                return LayoutBackup.Update(info.Id, out error) ? null : error;
            }, ReloadList);
        }

        private void RestoreBackup(LayoutBackupInfo info)
        {
            if (operationRunning || info == null) return;
            if (info.Incomplete)
            {
                MessageBox.Show(this, "这份恢复前残留可能不完整，只能删除，不能恢复。", "布局备份",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                Restored = true;
                MessageBox.Show(this, "已恢复到" + Quote(info) + "。\r\n恢复前的布局已自动留底，需要时可以回到这里再恢复回去。",
                    "布局备份", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();   // 主窗口在本窗口关闭后重新加载，不必再手动关一次
            });
        }

        private void RenameBackup(LayoutBackupInfo info)
        {
            if (operationRunning || info == null || info.IsPreRestore) return;
            string name = InputDialog.Show("重命名备份", "输入新的备份名称。", info.Name, -1, delegate(string value)
            {
                return (value == null || value.Trim().Length == 0) ? "名称不能为空。" : null;
            });
            if (name == null || name.Trim() == info.Name) return;

            RunBackupOperation(info.Id, "正在重命名…", "重命名失败：", delegate
            {
                string error;
                return LayoutBackup.Rename(info.Id, name, out error) ? null : error;
            }, ReloadList);
        }

        private void DeleteBackup(LayoutBackupInfo info)
        {
            if (operationRunning || info == null) return;
            string consequence = info.IsPreRestore
                ? "这是那次恢复前自动保存的布局，删除后无法找回。"
                : "备份文件会被永久删除，当前布局不受影响。";
            if (!ConfirmDialog.Show("删除备份", "删除" + Quote(info) + "？\r\n" + consequence, "删除", "取消")) return;

            RunBackupOperation(info.Id, "正在删除…", "删除失败：", delegate
            {
                string error;
                return LayoutBackup.Delete(info.Id, out error) ? null : error;
            }, ReloadList);
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
                ActiveControl = list;   // 焦点回到列表：新选中的卡片可以直接用键盘操作
            }
        }

        private void RunBackupOperation(string busyId, string busyText, string failPrefix, Func<string> operation, Action onSuccess)
        {
            if (operationRunning) return;
            operationRunning = true;
            ShowBusy(true, busyId, busyText);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                try { error = operation(); }
                catch (Exception ex) { error = ex.Message; Paths.LogError(ex); }
                if (IsDisposed) return;
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        if (IsDisposed || Disposing) return;
                        operationRunning = false;
                        ShowBusy(false, null, null);
                        if (error != null)
                        {
                            // 失败也可能改动了磁盘（恢复中断会留下不完整的留底、删除可能只删了一半）：先按实际情况刷新列表
                            ReloadList();
                            MessageBox.Show(this, failPrefix + error, "布局备份",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                        onSuccess();
                    });
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            });
        }
    }
}
