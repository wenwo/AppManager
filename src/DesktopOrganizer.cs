using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace AppManager
{
    /// <summary>
    /// 桌面一键收纳/还原：递归扫描桌面（最多 2 层），按「收纳范围」（快捷方式/文件/文件夹）
    /// 与「收纳模式」（搬家=移动 / 副本=拷贝）处理，并清理桌面、保留「应用中心」入口。
    /// 生成备份清单（config.DesktopBackup），支持单条还原与一键全部还原。
    /// </summary>
    internal class DesktopOrganizer
    {
        private readonly AppConfig config;
        private static string BackupDir { get { return Paths.DesktopBackupDir; } }
        private static string QuickCenterDirPath { get { return Path.Combine(Paths.DataDir, "QuickCenter"); } }

        private const string FilesSub = "Files";
        private const string FoldersSub = "Folders";

        // 自动收纳、手动整理、还原共用一把锁，避免后台监听与“还原全部”同时改写同一份清单。
        private static readonly object OperationLock = new object();
        private static readonly Dictionary<string, DateTime> AutoStashIgnore =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static int restoreDepth;   // 进行中的还原层数（RestoreAll 内嵌 RestoreOne），只在持有 OperationLock 时读写

        public DesktopOrganizer(AppConfig cfg) { config = cfg; }

        /// <summary>最近一次 RestoreOne 的失败原因，供界面显示，避免统一误报“备份已不存在”。</summary>
        public string LastRestoreError { get; private set; }

        /// <summary>备份目录路径（供外部判断/重扫使用）。</summary>
        public static string BackupPath { get { return BackupDir; } }

        /// <summary>布局备份等需要与桌面搬动串行化的操作共用此入口。</summary>
        internal static void RunExclusive(Action operation)
        {
            lock (OperationLock) operation();
        }

        internal static T RunExclusive<T>(Func<T> operation)
        {
            lock (OperationLock) return operation();
        }

        internal static object OperationSync { get { return OperationLock; } }

        /// <summary>
        /// 是否有整理/还原/自动收纳/布局恢复正在搬动文件（另一线程持有操作锁）。
        /// 退出前检查：搬到一半被结束会留下已移动但未登记的文件。
        /// </summary>
        internal static bool IsOperationRunning
        {
            get
            {
                if (!System.Threading.Monitor.TryEnter(OperationLock)) return true;
                System.Threading.Monitor.Exit(OperationLock);
                return false;
            }
        }

        /// <summary>快捷中心收纳主目录：文件夹收进此目录，文件收进其 Files 子目录。</summary>
        public static string QuickCenterDir { get { return QuickCenterDirPath; } }

        /// <summary>当前用户桌面目录（自动监听用）。</summary>
        public static string UserDesktop
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); }
        }

        /// <summary>公共桌面目录（所有用户共享）。</summary>
        public static string PublicDesktop
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory); }
        }

        /// <summary>本程序入口名称：桌面上的这个快捷方式不收纳（避免把唯一入口收走）。</summary>
        private const string AppDisplayName = "应用中心";

        /// <summary>判断是否为本程序自身的快捷方式。</summary>
        public static bool IsSelfShortcut(string shortcutPath)
        {
            string name = Path.GetFileNameWithoutExtension(shortcutPath);
            return string.Equals(name, AppDisplayName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 自动收纳一批桌面快捷方式到收纳目录根并登记还原清单（自动监听触发时使用）。
        /// 已消失、非快捷方式、本程序自身的入口都会被跳过，返回成功收纳数。
        /// </summary>
        public static int AutoStashShortcuts(AppConfig cfg, List<string> paths, out int failed)
        {
            failed = 0;
            if (cfg == null || paths == null || cfg.LoadFailed) return 0;
            lock (OperationLock)
            {
                int moved = 0;
                foreach (string path in paths)
                {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path) || IsAutoStashSuppressed(path)) continue;
                    if (!IsShortcutFile(path) || IsSelfShortcut(path)) continue;
                    string dest = null;
                    bool sourceRemoved = false;
                    try
                    {
                        if (!Directory.Exists(ShortcutDestDir)) Directory.CreateDirectory(ShortcutDestDir);
                        dest = Path.Combine(ShortcutDestDir, Path.GetFileName(path));
                        if (File.Exists(dest)) dest = UniquePath(ShortcutDestDir, Path.GetFileName(path), false);
                        File.Copy(path, dest, false);
                        try { File.Delete(path); sourceRemoved = true; }
                        catch (Exception ex) { Paths.LogError(new IOException("自动收纳已复制但源快捷方式未删除: " + path, ex)); }

                        DesktopBackupEntry entry = new DesktopBackupEntry();
                        entry.OriginalPath = path;
                        entry.CurrentPath = dest;
                        entry.IsShortcut = true;
                        entry.IsDir = false;
                        entry.IsCopy = !sourceRemoved;
                        cfg.DesktopBackup.Add(entry);
                        if (!cfg.Save("physical.desktop.autostash"))
                        {
                            cfg.DesktopBackup.Remove(entry);
                            RollbackBackedItem(path, dest, false, sourceRemoved);
                            failed++;
                            continue;
                        }
                        moved++;
                    }
                    catch (Exception ex)
                    {
                        Paths.LogError(ex);
                        failed++;
                        if (dest != null)
                            try { if (File.Exists(dest)) File.Delete(dest); } catch { }
                    }
                }
                return moved;
            }
        }

        /// <summary>还原后抑制自动收纳的时长，从整个还原操作结束时起算。</summary>
        internal static readonly TimeSpan AutoStashQuiet = TimeSpan.FromSeconds(30);

        /// <summary>
        /// 还原后短暂抑制自动收纳，避免刚回到桌面的快捷方式又被监听器收走。
        /// 还原操作进行中先登记为“挂起”（不过期），整个操作结束时再统一开始计时：
        /// “还原全部”耗时超过抑制时长时，早先还原的项不会在补扫时已经过期。
        /// </summary>
        private static void SuppressAutoStash(params string[] paths)
        {
            lock (AutoStashIgnore)
            {
                DateTime until = restoreDepth > 0 ? DateTime.MaxValue : DateTime.UtcNow + AutoStashQuiet;
                foreach (string path in paths)
                    if (!string.IsNullOrEmpty(path)) AutoStashIgnore[path] = until;
            }
        }

        /// <summary>结束一层还原操作（持有 OperationLock 时调用）；最外层结束时挂起的抑制项开始计时。</summary>
        private static void EndRestore()
        {
            if (--restoreDepth > 0) return;
            lock (AutoStashIgnore)
            {
                DateTime until = DateTime.UtcNow + AutoStashQuiet;
                foreach (string path in new List<string>(AutoStashIgnore.Keys))
                    if (AutoStashIgnore[path] == DateTime.MaxValue) AutoStashIgnore[path] = until;
            }
        }

        private static bool IsAutoStashSuppressed(string path)
        {
            lock (AutoStashIgnore)
            {
                DateTime until;
                if (!AutoStashIgnore.TryGetValue(path, out until)) return false;
                if (DateTime.UtcNow <= until) return true;
                AutoStashIgnore.Remove(path);
                return false;
            }
        }

        /// <summary>保存失败时撤回本次收纳：搬家移回原位，副本删除本次新建的目标。</summary>
        private static void RollbackBackedItem(string source, string dest, bool isDir, bool sourceRemoved)
        {
            try
            {
                bool exists = isDir ? Directory.Exists(dest) : File.Exists(dest);
                if (!exists) return;
                if (sourceRemoved)
                {
                    if (isDir) MoveDirectory(dest, source);
                    else MoveFile(dest, source);
                }
                else if (isDir) Directory.Delete(dest, true);
                else File.Delete(dest);
            }
            catch (Exception ex) { Paths.LogError(new IOException("收纳保存失败后撤回失败: " + dest, ex)); }
        }
        // 各类目标的备份落点：快捷方式沿用历史扁平布局，文件/文件夹各归子目录
        private static string ShortcutDestDir { get { return BackupDir; } }
        private static string FileDestDir { get { return Path.Combine(BackupDir, FilesSub); } }
        private static string FolderDestDir { get { return Path.Combine(BackupDir, FoldersSub); } }

        /// <summary>
        /// 一键整理桌面：收纳桌面条目，并确保桌面保留「应用中心」入口。
        /// GUI「整理桌面」与命令行 --tidy-desktop 共用此逻辑，返回收纳条目数。
        /// </summary>
        public int TidyDesktop(string exePath)
        {
            lock (OperationLock)
            {
                int count = Organize();
                EnsureLauncherShortcutOnDesktop(exePath);
                return count;
            }
        }

        /// <summary>在桌面生成/刷新「应用中心」快捷方式（收纳后桌面仅留它）。</summary>
        public static void EnsureLauncherShortcutOnDesktop(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return;
            try
            {
                string desk = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                if (string.IsNullOrEmpty(desk)) return;
                string lnk = Path.Combine(desk, "应用中心.lnk");
                CreateShortcut(lnk, exePath, "应用中心");
                // 通知 shell 刷新图标缓存，避免因图标变更后仍显示旧的兜底图标
                try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); }
                catch { }
            }
            catch { }
        }

        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;
        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        /// <summary>用 PowerShell 创建快捷方式，避免引入 WSH COM 依赖；显式写入 IconLocation。</summary>
        private static void CreateShortcut(string lnkPath, string target, string desc)
        {
            try
            {
                string script = "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('" + lnkPath.Replace("'", "''") + "');" +
                    "$s.TargetPath='" + target.Replace("'", "''") + "';" +
                    "$s.IconLocation='" + target.Replace("'", "''") + ",0';" +
                    "$s.Description='" + desc.Replace("'", "''") + "';" +
                    "$s.WindowStyle=1;$s.Save();";
                Process p = new Process();
                p.StartInfo.FileName = "powershell.exe";
                p.StartInfo.Arguments = "-NoProfile -ExecutionPolicy Bypass -Command " + EscapeArg(script);
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.CreateNoWindow = true;
                p.Start();
                p.WaitForExit(4000);
            }
            catch { }
        }

        private static string EscapeArg(string a)
        {
            if (string.IsNullOrEmpty(a)) return "\"\"";
            if (a.IndexOf(' ') >= 0 || a.IndexOf('"') >= 0)
                return "\"" + a.Replace("\"", "\"\"") + "\"";
            return a;
        }

        /// <summary>收纳桌面条目（按配置的范围与模式）；返回收纳条目数。配置读取失败时拒绝执行。</summary>
        public int Organize()
        {
            lock (OperationLock)
            {
                if (config.LoadFailed) throw new IOException("配置读取失败，已停止整理桌面：" + config.DatabaseError);
                List<string> desktops = new List<string>
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
                };
                if (!Directory.Exists(BackupDir)) Directory.CreateDirectory(BackupDir);
                // 合并而非清空：保留历史收纳记录（二次收纳不丢上一批的还原信息），
                // 只剔除备份文件已被手动删除/还原的旧记录。
                int removed = config.DesktopBackup.RemoveAll(delegate(DesktopBackupEntry e)
                {
                    if (e == null || string.IsNullOrEmpty(e.CurrentPath)) return true;
                    return e.IsDir ? !Directory.Exists(e.CurrentPath) : !File.Exists(e.CurrentPath);
                });
                if (removed > 0 && !config.Save("physical.desktop.organize")) throw new IOException("整理清单未能保存：" + config.DatabaseError);
                int count = 0;
                foreach (string desk in desktops)
                {
                    if (!Directory.Exists(desk)) continue;
                    count += ScanAndMove(desk, 0);
                }
                return count;
            }
        }
        private int ScanAndMove(string dir, int depth)
        {
            if (depth > 2) return 0;
            int count = 0;

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { files = new string[0]; }
            foreach (string f in files)
            {
                string name = Path.GetFileName(f);
                if (IsSkippable(name, f)) continue;
                if (IsShortcutFile(f))
                {
                    if (!config.OrganizeShortcut) continue;
                    count += BackupItem(f, ShortcutDestDir, name, false, true);
                }
                else
                {
                    if (!config.OrganizeFile) continue;
                    count += BackupItem(f, FileDestDir, name, false, false);
                }
            }

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { subs = new string[0]; }
            foreach (string s in subs)
            {
                string name = Path.GetFileName(s);
                if (IsSkippable(name, s) || IsReparsePoint(s)) continue;
                if (config.OrganizeDir)
                    count += BackupItem(s, FolderDestDir, name, true, false);   // 整目录收纳
                // 未勾选“收纳文件夹”时不得进入桌面文件夹内部抽取文件，否则会破坏用户目录结构。
            }
            return count;
        }

        /// <summary>
        /// 备份单个桌面条目：搬家=移动（默认），副本=拷贝（保留原件）。
        /// 同名目标始终另存新名字；只有源确实已移除才登记为搬家，保存失败则撤回本次搬运。
        /// </summary>
        private int BackupItem(string source, string destDir, string name, bool isDir, bool isShortcut)
        {
            string dest = null;
            bool sourceRemoved = false;
            try
            {
                if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                dest = Path.Combine(destDir, name);
                bool destExists = isDir ? Directory.Exists(dest) : File.Exists(dest);
                if (destExists) dest = UniquePath(destDir, name, isDir);

                if (config.OrganizeCopy)
                {
                    if (isDir) CopyDir(source, dest);
                    else File.Copy(source, dest, false);
                }
                else
                {
                    sourceRemoved = isDir ? MoveDirectory(source, dest) : MoveFile(source, dest);
                }

                Record(source, dest, isDir, isShortcut, config.OrganizeCopy || !sourceRemoved);
                if (!config.Save("physical.desktop.organize"))
                {
                    DesktopBackupEntry added = config.DesktopBackup.Count == 0 ? null : config.DesktopBackup[config.DesktopBackup.Count - 1];
                    if (added != null && string.Equals(added.CurrentPath, dest, StringComparison.OrdinalIgnoreCase))
                        config.DesktopBackup.Remove(added);
                    RollbackBackedItem(source, dest, isDir, sourceRemoved);
                    throw new IOException("收纳清单未能保存：" + config.DatabaseError);
                }
                return 1;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("收纳失败 " + source + ": " + ex.Message);
                Paths.LogError(ex);
                return 0;
            }
        }
        private void Record(string original, string current, bool isDir, bool isShortcut, bool isCopy)
        {
            // 同一当前路径只保留最新记录，避免合并模式下重复登记
            for (int i = config.DesktopBackup.Count - 1; i >= 0; i--)
            {
                DesktopBackupEntry ex = config.DesktopBackup[i];
                if (ex != null && string.Equals(ex.CurrentPath, current, StringComparison.OrdinalIgnoreCase))
                    config.DesktopBackup.RemoveAt(i);
            }
            DesktopBackupEntry e = new DesktopBackupEntry();
            e.OriginalPath = original;
            e.CurrentPath = current;
            e.IsDir = isDir;
            e.IsShortcut = isShortcut;
            e.IsCopy = isCopy;
            config.DesktopBackup.Add(e);
        }

        public static bool IsShortcutFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".lnk" || ext == ".url";
        }

        /// <summary>是否跳过：应用中心入口、desktop.ini/缩略图缓存、隐藏或系统属性项。</summary>
        private static bool IsSkippable(string name, string fullPath)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (name.IndexOf("应用中心", StringComparison.Ordinal) >= 0) return true;
            string lower = name.ToLowerInvariant();
            if (lower == "desktop.ini" || lower == "thumbs.db") return true;
            try
            {
                FileAttributes at = File.GetAttributes(fullPath);
                if ((at & FileAttributes.ReparsePoint) != 0) return true;
                if ((at & FileAttributes.Hidden) != 0 || (at & FileAttributes.System) != 0) return true;
            }
            catch { }
            return false;
        }

        /// <summary>在目标目录取不冲突的唯一路径（重名追加 _1/_2…）。</summary>
        private static string UniquePath(string dir, string name, bool isDir)
        {
            string baseName = isDir ? name : Path.GetFileNameWithoutExtension(name);
            string ext = isDir ? string.Empty : Path.GetExtension(name);
            int i = 1;
            string cand;
            do { cand = Path.Combine(dir, baseName + "_" + i + ext); i++; }
            while (File.Exists(cand) || Directory.Exists(cand));
            return cand;
        }

        /// <summary>递归拷贝目录（副本模式用）。</summary>
        private static List<string> CopyDir(string src, string dst)
        {
            if (Directory.Exists(dst) || File.Exists(dst)) throw new IOException("复制目标已存在。");
            string staging = dst + ".copy-" + Guid.NewGuid().ToString("N");
            try
            {
                List<string> copied = SafeDirectory.Copy(src, staging, false, new SafeDirectory.Budget());
                Directory.Move(staging, dst);
                return copied;
            }
            finally { TryDeleteDir(staging); }
        }

        private static bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return true; }
        }

        /// <summary>
        /// 规范化用户配置的存储根目录：盘符根（H:\）去掉末尾分隔符后只剩「H:」，
        /// 会被系统当成「H 盘的当前目录」，使 H:\Files 退化为 H:Files 这种盘相对路径。
        /// 故对盘符根补回分隔符；普通路径仅去除末尾多余分隔符；空值原样返回。
        /// </summary>
        internal static string NormalizeRoot(string root)
        {
            if (string.IsNullOrEmpty(root)) return root;
            string r = root.Trim().TrimEnd('\\', '/');
            if (r.Length == 2 && r[1] == ':') r = r + "\\";
            return r;
        }

        /// <summary>
        /// 搬迁目录到目标路径：同卷直接 Move（快且原子）；跨卷时 .NET Framework 的 Directory.Move
        /// 会抛「源路径和目标路径必须具有相同的根」，改为「递归复制 + 删除源」实现。复制中断时
        /// 清理残留目标并抛出，源目录保持原样，避免数据丢失。
        /// </summary>
        /// <returns>
        /// 源目录是否已完全移除。跨卷时复制期间新增/修改的文件、被占用的文件会保留在源位置（不丢数据），
        /// 此时返回 false，目标已是完整副本。同卷移动恒为 true。
        /// </returns>
        internal static bool MoveDirectory(string source, string dest)
        {
            if (SameVolumeAs(source, dest))
            {
                Directory.Move(source, dest);
                return true;
            }
            List<string> copied = CopyDir(source, dest); // 完整复制后才安装目标；失败只清理本次临时目录。
            DeleteCopiedSource(NormalizeRoot(Path.GetFullPath(source)), Path.GetFullPath(dest), copied);
            return !Directory.Exists(source);
        }

        /// <summary>
        /// 跨卷移动的删源阶段：只删除复制快照里、且与目标副本大小和修改时间一致的文件；
        /// 目录自深向浅非递归删除，保留下来的文件或复制期间新出现的内容使目录非空而保留。
        /// </summary>
        private static void DeleteCopiedSource(string source, string dest, List<string> copied)
        {
            for (int i = copied.Count - 1; i >= 0; i--)
            {
                string path = copied[i];
                string copy = Path.Combine(dest, path.Substring(source.Length).TrimStart('\\', '/'));
                if (Directory.Exists(path))
                {
                    try { Directory.Delete(path, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    continue;
                }
                try
                {
                    FileInfo original = new FileInfo(path), target = new FileInfo(copy);
                    if (!original.Exists) continue;
                    if (!target.Exists || original.Length != target.Length || original.LastWriteTimeUtc != target.LastWriteTimeUtc)
                        continue;   // 复制后被修改：保留源文件，由用户决定取舍
                    if ((original.Attributes & FileAttributes.ReadOnly) != 0)
                        original.Attributes &= ~FileAttributes.ReadOnly;
                    original.Delete();
                }
                catch (Exception ex)
                {
                    Paths.LogError(new IOException("跨卷移动已复制但源文件未删除: " + path, ex));
                }
            }
        }

        /// <summary>
        /// 移动文件：同卷直接 Move；跨卷先复制到目标目录内的临时名再改名到位（不覆盖他人文件），
        /// 最后删除源文件。返回 false 表示副本已就位但源文件未能删除（被占用/无权限）。
        /// </summary>
        internal static bool MoveFile(string source, string dest)
        {
            if (SameVolumeAs(source, dest))
            {
                File.Move(source, dest);
                return true;
            }
            string staging = dest + ".copy-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(source, staging, false);
                File.Move(staging, dest);   // 目标若在此期间出现则失败，不覆盖
            }
            finally
            {
                try { if (File.Exists(staging)) File.Delete(staging); } catch { }
            }
            try
            {
                FileInfo original = new FileInfo(source);
                if ((original.Attributes & FileAttributes.ReadOnly) != 0)
                    original.Attributes &= ~FileAttributes.ReadOnly;
                original.Delete();
                return true;
            }
            catch (Exception ex)
            {
                Paths.LogError(new IOException("跨卷移动已复制但源文件删除失败: " + source, ex));
                return false;
            }
        }

        /// <summary>源与目标（尚不存在，按其所在目录判断）是否同一卷；挂载点按实际卷区分，无法确认时按跨卷处理。</summary>
        private static bool SameVolumeAs(string source, string dest)
        {
            string parent = Path.GetDirectoryName(Path.GetFullPath(dest).TrimEnd('\\', '/'));
            return !string.IsNullOrEmpty(parent) && FileImport.SameVolume(Path.GetFullPath(source), parent);
        }

        /// <summary>尽力删除目录，失败静默（用于清理搬迁残留）。</summary>
        private static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { }
        }

        /// <summary>一键还原全部收纳项；返回成功还原条目数（失败项保留在清单中）。</summary>
        public int RestoreAll()
        {
            lock (OperationLock)
            {
                int count = 0;
                restoreDepth++;   // 整批结束后才开始计抑制时间，早还原的项不会在批量还原途中过期
                try
                {
                    List<DesktopBackupEntry> snapshot = new List<DesktopBackupEntry>(config.DesktopBackup);
                    foreach (DesktopBackupEntry e in snapshot)
                    {
                        if (!RestoreOne(e)) continue;   // 失败项保留，避免丢失还原线索
                        count++;
                        config.DesktopBackup.Remove(e);
                        if (!config.Save("physical.desktop.restore"))
                        {
                            // Save 失败会从数据库重载配置；仅在重载也失败、记录确实缺失时补回线索。
                            bool present = config.DesktopBackup.Exists(delegate(DesktopBackupEntry value)
                            { return value != null && string.Equals(value.CurrentPath, e.CurrentPath, StringComparison.OrdinalIgnoreCase); });
                            if (!present) config.DesktopBackup.Add(e);
                            break;
                        }
                    }
                }
                finally { EndRestore(); }
                return count;
            }
        }

        /// <summary>
        /// 还原单条：副本模式先核对原件，原件仍在且内容一致才清理备份副本（进回收站）；
        /// 原件已丢失则按搬家逻辑移回。搬家模式把备份项移回原位置。
        /// 返回是否已处理（备份缺失或移动不完整时保留清单项）。
        /// </summary>
        public bool RestoreOne(DesktopBackupEntry e)
        {
            LastRestoreError = null;
            if (e == null) { LastRestoreError = "还原记录为空。"; return false; }
            lock (OperationLock)
            {
                restoreDepth++;
                try
                {
                    if (!IsManagedBackupPath(e.CurrentPath))
                    {
                        Debug.WriteLine("还原失败: 当前路径不在收纳目录内 " + e.CurrentPath);
                        LastRestoreError = "备份路径不在当前收纳目录内：" + e.CurrentPath;
                        return false;
                    }
                    bool backupExists = e.IsDir ? Directory.Exists(e.CurrentPath) : File.Exists(e.CurrentPath);
                    bool originalExists = IsSafeRestoreTarget(e.OriginalPath)
                        && (e.IsDir ? Directory.Exists(e.OriginalPath) : File.Exists(e.OriginalPath));
                    if (e.IsCopy)
                    {
                        // 副本模式且原件仍在：备份副本是多余数据，还原即清理；没有回收站时拒绝永久删除。
                        if (originalExists)
                        {
                            if (!backupExists) return true;
                            if (!SameCopyContent(e))
                            {
                                Debug.WriteLine("还原失败: 原件与备份内容不一致，未删除副本 " + e.CurrentPath);
                                LastRestoreError = "原件与备份内容不一致，已保留备份：" + e.CurrentPath;
                                return false;
                            }
                            if (!Launcher.CanUseRecycleBin(e.CurrentPath))
                            {
                                Debug.WriteLine("还原失败: 该位置没有可用回收站，未永久删除副本 " + e.CurrentPath);
                                LastRestoreError = "该位置没有可用回收站，未永久删除副本：" + e.CurrentPath;
                                return false;
                            }
                            bool deleted = Launcher.DeletePath(e.CurrentPath, true);
                            if (!deleted) LastRestoreError = "无法把副本移入回收站，已保留：" + e.CurrentPath;
                            return deleted;
                        }
                        // 原件已不在：不能直接删副本，按搬家项移回原位置。
                    }
                    if (!IsSafeRestoreTarget(e.OriginalPath))
                    {
                        Debug.WriteLine("还原失败: 原始路径不在桌面目录内 " + e.OriginalPath);
                        LastRestoreError = "原始路径不在当前桌面目录内：" + e.OriginalPath;
                        return false;
                    }
                    if (!backupExists) { LastRestoreError = "备份文件已不存在：" + e.CurrentPath; return false; }
                    string dir = Path.GetDirectoryName(e.OriginalPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string dest = UniqueRestoreTarget(e);
                    if (!IsSafeRestoreTarget(dest)) { LastRestoreError = "还原落点不安全：" + dest; return false; }
                    bool sourceRemoved = e.IsDir ? MoveDirectory(e.CurrentPath, dest) : MoveFile(e.CurrentPath, dest);
                    if (!sourceRemoved)
                    {
                        LastRestoreError = "已复制到桌面，但原备份未能删除（可能被占用）：" + e.CurrentPath;
                        return false;
                    }
                    SuppressAutoStash(dest, e.OriginalPath);
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("还原失败: " + ex.Message);
                    LastRestoreError = "还原失败：" + ex.Message;
                    Paths.LogError(ex);
                    return false;
                }
                finally { EndRestore(); }
            }
        }

        /// <summary>副本还原前核对两份内容；任何无法完整读取或安全比较的情况都返回 false，保留备份。</summary>
        private static bool SameCopyContent(DesktopBackupEntry entry)
        {
            try
            {
                return entry.IsDir
                    ? SameDirectoryContent(entry.OriginalPath, entry.CurrentPath, 0)
                    : SameFileContent(entry.OriginalPath, entry.CurrentPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("副本内容核对失败，保留备份: " + ex.Message);
                return false;
            }
        }

        private static bool SameDirectoryContent(string original, string backup, int depth)
        {
            if (depth > 64 || !Directory.Exists(original) || !Directory.Exists(backup)) return false;
            if (IsReparsePoint(original) || IsReparsePoint(backup)) return false;

            string[] originalFiles = Directory.GetFiles(original);
            string[] backupFiles = Directory.GetFiles(backup);
            if (originalFiles.Length != backupFiles.Length) return false;
            Array.Sort(originalFiles, StringComparer.OrdinalIgnoreCase);
            Array.Sort(backupFiles, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < originalFiles.Length; i++)
            {
                if (!string.Equals(Path.GetFileName(originalFiles[i]), Path.GetFileName(backupFiles[i]), StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!SameFileContent(originalFiles[i], backupFiles[i])) return false;
            }

            string[] originalDirs = Directory.GetDirectories(original);
            string[] backupDirs = Directory.GetDirectories(backup);
            if (originalDirs.Length != backupDirs.Length) return false;
            Array.Sort(originalDirs, StringComparer.OrdinalIgnoreCase);
            Array.Sort(backupDirs, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < originalDirs.Length; i++)
            {
                if (!string.Equals(Path.GetFileName(originalDirs[i]), Path.GetFileName(backupDirs[i]), StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!SameDirectoryContent(originalDirs[i], backupDirs[i], depth + 1)) return false;
            }
            return true;
        }

        private static bool SameFileContent(string original, string backup)
        {
            if (!File.Exists(original) || !File.Exists(backup)) return false;
            if (IsReparsePoint(original) || IsReparsePoint(backup)) return false;
            if (new FileInfo(original).Length != new FileInfo(backup).Length) return false;

            const int bufferSize = 64 * 1024;
            byte[] originalBuffer = new byte[bufferSize];
            byte[] backupBuffer = new byte[bufferSize];
            using (FileStream left = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan))
            using (FileStream right = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan))
            {
                for (;;)
                {
                    int leftRead = ReadBlock(left, originalBuffer);
                    int rightRead = ReadBlock(right, backupBuffer);
                    if (leftRead != rightRead) return false;
                    if (leftRead == 0) return true;
                    for (int i = 0; i < leftRead; i++)
                        if (originalBuffer[i] != backupBuffer[i]) return false;
                }
            }
        }

        private static int ReadBlock(FileStream stream, byte[] buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }

        internal static bool IsManagedBackupPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string fullPath = Path.GetFullPath(path);
                return IsStrictChildOf(Paths.DesktopBackupDir, fullPath)
                    || IsStrictChildOf(Paths.LegacyStashDir, fullPath);
            }
            catch { return false; }
        }

        private static bool IsStrictChildOf(string dir, string path)
        {
            string root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase)) return false;
            if (!(full + Path.DirectorySeparatorChar).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            return !HasReparsePointBelow(root, full);
        }

        /// <summary>检查 root 之下（不含 root 自身）是否有重解析点；root 本身是 junction 时仍视为可信起点。</summary>
        private static bool HasReparsePointBelow(string root, string path)
        {
            string current = path;
            while (!string.IsNullOrEmpty(current))
            {
                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) return false;
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                string parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) return false;
                current = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            return false;
        }

        /// <summary>还原落点：原位置已有同名项时追加 _restored，避免覆盖。</summary>
        private static string UniqueRestoreTarget(DesktopBackupEntry e)
        {
            string dest = e.OriginalPath;
            bool exists = Directory.Exists(dest) || File.Exists(dest);
            if (!exists) return dest;
            string dir = Path.GetDirectoryName(dest);
            string baseName = e.IsDir ? Path.GetFileName(dest) : Path.GetFileNameWithoutExtension(dest);
            string ext = e.IsDir ? string.Empty : Path.GetExtension(dest);
            int i = 1;
            string cand;
            do
            {
                cand = Path.Combine(dir, baseName + "_restored" + (i > 1 ? i.ToString() : "") + ext);
                i++;
            }
            while (File.Exists(cand) || Directory.Exists(cand));
            return cand;
        }

        internal static bool IsSafeRestoreTarget(string path)
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) return false;
            try
            {
                string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                bool drive = full.Length >= 3 && full[1] == ':' && (full[2] == '\\' || full[2] == '/');
                bool unc = full.StartsWith("\\\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal);
                if (!drive && !unc) return false;
                // 拒绝 NTFS 备用数据流；UNC 路径中任何冒号都属于非法文件名
                if (full.IndexOf(':', drive ? 2 : 0) >= 0) return false;
                string user = Path.GetFullPath(UserDesktop).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string shared = Path.GetFullPath(PublicDesktop).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string root = IsStrictChildOf(user, full) ? user : (IsStrictChildOf(shared, full) ? shared : null);
                return root != null && !HasReparsePointBelow(root, full);
            }
            catch { return false; }
        }

        /// <summary>从清单中移除某条记录（不触碰磁盘文件）。</summary>
        public bool RemoveEntry(DesktopBackupEntry e, out string error)
        {
            error = null;
            if (e == null) return true;
            config.DesktopBackup.Remove(e);
            if (!config.Save("physical.desktop.remove"))
            {
                error = config.DatabaseError;
                return false;
            }
            return true;
        }

        /// <summary>是否存在已收纳备份。</summary>
        public bool HasBackup() { return config.DesktopBackup.Count > 0; }

        /// <summary>
        /// 更新已有备份清单：重新扫描备份目录，以当前实际存在的内容刷新 DesktopBackup
        /// （保留原路径信息，剔除已被手动删除的备份项，补回新落入的条目）。返回更新后的条目数。
        /// </summary>
        public int UpdateBackup()
        {
            lock (OperationLock)
            {
                Dictionary<string, DesktopBackupEntry> existing =
                    new Dictionary<string, DesktopBackupEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (DesktopBackupEntry e in config.DesktopBackup)
                    if (!string.IsNullOrEmpty(e.CurrentPath)) existing[e.CurrentPath] = e;

                List<DesktopBackupEntry> fresh = new List<DesktopBackupEntry>();
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 快捷方式（备份根，历史扁平布局）与普通文件（Files 子目录）
                foreach (string f in SafeFiles(BackupDir))
                    if (!IsTemporaryBackupName(f) && seen.Add(f)) fresh.Add(MatchOrNew(existing, f, IsShortcutFile(f), false));
                foreach (string f in SafeFiles(FileDestDir))
                    if (!IsTemporaryBackupName(f) && seen.Add(f)) fresh.Add(MatchOrNew(existing, f, IsShortcutFile(f), false));
                // 目录（Folders 子目录，仅顶层目录各成一条）
                foreach (string d in SafeDirs(FolderDestDir))
                    if (!IsTemporaryBackupName(d) && seen.Add(d)) fresh.Add(MatchOrNew(existing, d, false, true));

                // 旧版 DesktopStash 或用户自定义旧目录里的现有记录不能因本次重扫而消失。
                foreach (DesktopBackupEntry e in config.DesktopBackup)
                {
                    if (e == null || string.IsNullOrEmpty(e.CurrentPath) || seen.Contains(e.CurrentPath)) continue;
                    bool exists = e.IsDir ? Directory.Exists(e.CurrentPath) : File.Exists(e.CurrentPath);
                    if (exists && !IsTemporaryBackupName(e.CurrentPath)) { fresh.Add(e); seen.Add(e.CurrentPath); }
                }

                config.DesktopBackup.Clear();
                config.DesktopBackup.AddRange(fresh);
                if (!config.Save("physical.desktop.update")) throw new IOException("备份清单未能保存：" + config.DatabaseError);
                return fresh.Count;
            }
        }

        private static bool IsTemporaryBackupName(string path)
        {
            string name = Path.GetFileName(path) ?? string.Empty;
            return name.IndexOf(".copy-", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf(".restore-", StringComparison.OrdinalIgnoreCase) >= 0
                || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
        }
        private static string[] SafeFiles(string dir)
        {
            try { return Directory.Exists(dir) ? Directory.GetFiles(dir) : new string[0]; }
            catch { return new string[0]; }
        }

        private static string[] SafeDirs(string dir)
        {
            try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : new string[0]; }
            catch { return new string[0]; }
        }

        /// <summary>优先复用已有记录；否则按「桌面\文件名」推断原路径新建，模式和当前配置一致。</summary>
        private DesktopBackupEntry MatchOrNew(
            Dictionary<string, DesktopBackupEntry> existing, string path, bool isShortcut, bool isDir)
        {
            DesktopBackupEntry e;
            if (existing.TryGetValue(path, out e)) return e;
            DesktopBackupEntry ne = new DesktopBackupEntry();
            ne.CurrentPath = path;
            ne.IsShortcut = isShortcut;
            ne.IsDir = isDir;
            ne.IsCopy = false;   // 补登的历史条目按搬家处理，避免“还原”误删唯一副本
            // 原路径只能靠猜：公共桌面存在同名项时优先猜公共桌面，否则猜用户桌面
            string name = Path.GetFileName(path);
            string userGuess = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), name);
            string pubGuess = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), name);
            ne.OriginalPath = (File.Exists(pubGuess) || Directory.Exists(pubGuess)) ? pubGuess : userGuess;
            return ne;
        }
    }
}
