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
        private static readonly string AppDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppManager");
        private static readonly string BackupDir = Path.Combine(AppDir, "DesktopBackup");
        private static readonly string QuickCenterDirPath = Path.Combine(AppDir, "QuickCenter");

        private const string FilesSub = "Files";
        private const string FoldersSub = "Folders";

        public DesktopOrganizer(AppConfig cfg) { config = cfg; }

        /// <summary>备份目录路径（供外部判断/重扫使用）。</summary>
        public static string BackupPath { get { return BackupDir; } }

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
            if (cfg == null || paths == null) return 0;
            int moved = 0;
            foreach (string path in paths)
            {
                try
                {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                    if (!IsShortcutFile(path) || IsSelfShortcut(path)) continue;
                    if (!Directory.Exists(ShortcutDestDir)) Directory.CreateDirectory(ShortcutDestDir);
                    string dest = Path.Combine(ShortcutDestDir, Path.GetFileName(path));
                    if (File.Exists(dest)) dest = UniquePath(ShortcutDestDir, Path.GetFileName(path), false);
                    File.Copy(path, dest, false);
                    File.Delete(path);
                    DesktopBackupEntry entry = new DesktopBackupEntry();
                    entry.OriginalPath = path;
                    entry.CurrentPath = dest;
                    entry.IsShortcut = true;
                    entry.IsDir = false;
                    entry.IsCopy = false;
                    cfg.DesktopBackup.Add(entry);
                    moved++;
                }
                catch (Exception ex)
                {
                    Paths.LogError(ex);
                    failed++;
                }
            }
            if (moved > 0) cfg.Save();
            return moved;
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
            int count = Organize();
            EnsureLauncherShortcutOnDesktop(exePath);
            return count;
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

        /// <summary>收纳桌面条目（按配置的范围与模式）；返回收纳条目数。</summary>
        public int Organize()
        {
            List<string> desktops = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            };
            if (!Directory.Exists(BackupDir)) Directory.CreateDirectory(BackupDir);
            config.DesktopBackup.Clear();
            int count = 0;
            foreach (string desk in desktops)
            {
                if (!Directory.Exists(desk)) continue;
                count += ScanAndMove(desk, 0);
            }
            config.Save();
            return count;
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
                if (IsSkippable(name, s)) continue;
                if (config.OrganizeDir)
                    count += BackupItem(s, FolderDestDir, name, true, false);   // 整目录收纳
                else
                    count += ScanAndMove(s, depth + 1);                          // 仅深入找快捷方式/文件
            }
            return count;
        }

        /// <summary>
        /// 备份单个桌面条目：搬家=移动（默认），副本=拷贝（保留原件）。
        /// 副本模式下目标已存在视为已备份，仅登记既有副本，避免重复拷贝堆积。返回 1 表示已纳入备份。
        /// </summary>
        private int BackupItem(string source, string destDir, string name, bool isDir, bool isShortcut)
        {
            try
            {
                if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                string dest = Path.Combine(destDir, name);
                bool destExists = isDir ? Directory.Exists(dest) : File.Exists(dest);

                if (config.OrganizeCopy)
                {
                    if (destExists)
                    {
                        Record(source, dest, isDir, isShortcut, true);
                        return 1;
                    }
                    if (isDir) CopyDir(source, dest);
                    else File.Copy(source, dest, false);
                }
                else
                {
                    if (destExists) dest = UniquePath(destDir, name, isDir);
                    if (isDir) Directory.Move(source, dest);
                    else File.Move(source, dest);
                }
                Record(source, dest, isDir, isShortcut, config.OrganizeCopy);
                return 1;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("收纳失败 " + source + ": " + ex.Message);
                return 0;
            }
        }

        private void Record(string original, string current, bool isDir, bool isShortcut, bool isCopy)
        {
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
        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            string[] files;
            try { files = Directory.GetFiles(src); } catch { files = new string[0]; }
            foreach (string f in files)
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), false);
            string[] subs;
            try { subs = Directory.GetDirectories(src); } catch { subs = new string[0]; }
            foreach (string d in subs)
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
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
        internal static void MoveDirectory(string source, string dest)
        {
            string srcRoot = Path.GetPathRoot(Path.GetFullPath(source));
            string dstRoot = Path.GetPathRoot(Path.GetFullPath(dest));
            if (string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(source, dest);
                return;
            }
            try
            {
                CopyDir(source, dest);
            }
            catch
            {
                TryDeleteDir(dest);   // 复制中断：清理残留，源保持原样
                throw;
            }
            try { Directory.Delete(source, true); }
            catch (Exception ex)
            {
                Debug.WriteLine("跨卷移动：复制已完成但删除源目录失败 " + source + ": " + ex.Message);
            }
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
            int count = 0;
            List<DesktopBackupEntry> remaining = new List<DesktopBackupEntry>();
            List<DesktopBackupEntry> snapshot = new List<DesktopBackupEntry>(config.DesktopBackup);
            foreach (DesktopBackupEntry e in snapshot)
            {
                if (RestoreOne(e)) count++;
                else remaining.Add(e);   // 失败项保留，避免丢失还原线索
            }
            config.DesktopBackup.Clear();
            config.DesktopBackup.AddRange(remaining);
            config.Save();
            return count;
        }

        /// <summary>
        /// 还原单条：副本模式删除备份副本（原件仍在）；搬家模式把备份项移回原位置。
        /// 返回是否已处理（搬家模式下备份缺失视为失败，保留清单项）。
        /// </summary>
        public bool RestoreOne(DesktopBackupEntry e)
        {
            if (e == null) return false;
            try
            {
                bool backupExists = e.IsDir ? Directory.Exists(e.CurrentPath) : File.Exists(e.CurrentPath);
                if (e.IsCopy)
                {
                    // 副本模式：桌面原件仍在，还原即清理备份副本
                    if (backupExists)
                    {
                        if (e.IsDir) Directory.Delete(e.CurrentPath, true);
                        else File.Delete(e.CurrentPath);
                    }
                    return true;
                }
                if (!backupExists) return false;
                string dir = Path.GetDirectoryName(e.OriginalPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string dest = UniqueRestoreTarget(e);
                if (e.IsDir) Directory.Move(e.CurrentPath, dest);
                else File.Move(e.CurrentPath, dest);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("还原失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>还原落点：原位置已有同名项时追加 _restored，避免覆盖。</summary>
        private static string UniqueRestoreTarget(DesktopBackupEntry e)
        {
            string dest = e.OriginalPath;
            bool exists = e.IsDir ? Directory.Exists(dest) : File.Exists(dest);
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

        /// <summary>从清单中移除某条记录（不触碰磁盘文件）。</summary>
        public void RemoveEntry(DesktopBackupEntry e)
        {
            if (e == null) return;
            config.DesktopBackup.Remove(e);
            config.Save();
        }

        /// <summary>是否存在已收纳备份。</summary>
        public bool HasBackup() { return config.DesktopBackup.Count > 0; }

        /// <summary>
        /// 更新已有备份清单：重新扫描备份目录，以当前实际存在的内容刷新 DesktopBackup
        /// （保留原路径信息，剔除已被手动删除的备份项，补回新落入的条目）。返回更新后的条目数。
        /// </summary>
        public int UpdateBackup()
        {
            if (!Directory.Exists(BackupDir))
            {
                config.DesktopBackup.Clear();
                config.Save();
                return 0;
            }
            Dictionary<string, DesktopBackupEntry> existing =
                new Dictionary<string, DesktopBackupEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (DesktopBackupEntry e in config.DesktopBackup)
                if (!string.IsNullOrEmpty(e.CurrentPath)) existing[e.CurrentPath] = e;

            List<DesktopBackupEntry> fresh = new List<DesktopBackupEntry>();

            // 快捷方式（备份根，历史扁平布局）与普通文件（Files 子目录）
            foreach (string f in SafeFiles(BackupDir))
                fresh.Add(MatchOrNew(existing, f, IsShortcutFile(f), false));
            foreach (string f in SafeFiles(FileDestDir))
                fresh.Add(MatchOrNew(existing, f, IsShortcutFile(f), false));
            // 目录（Folders 子目录，仅顶层目录各成一条）
            foreach (string d in SafeDirs(FolderDestDir))
                fresh.Add(MatchOrNew(existing, d, false, true));

            config.DesktopBackup.Clear();
            config.DesktopBackup.AddRange(fresh);
            config.Save();
            return fresh.Count;
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
            ne.IsCopy = config.OrganizeCopy;
            string desk = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            ne.OriginalPath = Path.Combine(desk, Path.GetFileName(path));
            return ne;
        }
    }
}