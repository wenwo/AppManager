using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AppManager
{
    internal enum LocalScanScopeKind
    {
        Photos,
        Videos,
        Music
    }

    internal sealed class LocalScanScopeDrive
    {
        internal string Path;
        internal string Display;
        internal bool Supported;
        internal string UnsupportedReason;
        internal bool Removable;

        public override string ToString()
        {
            return string.IsNullOrEmpty(Display) ? (Path ?? string.Empty) : Display;
        }
    }

    internal sealed class LocalScanScopeShortcut
    {
        internal string Label;
        internal string Path;
        internal bool Primary;
    }

    internal sealed class LocalScanScopeResolution
    {
        internal readonly List<string> Roots = new List<string>();
        internal readonly List<string> Invalid = new List<string>();
        internal int Merged;

        internal int DriveCount
        {
            get { return Roots.Count(LocalScanScope.IsDriveRoot); }
        }
    }

    /// <summary>
    /// 本机扫描范围：磁盘枚举、快捷目录、路径校验和重叠范围合并。
    /// 只允许本地固定磁盘/可移动磁盘，不接受网络路径、光驱和未就绪磁盘。
    /// </summary>
    internal static class LocalScanScope
    {
        private static readonly Guid DownloadsFolder = new Guid("374DE290-123F-4565-9164-39C4925E467B");

        internal static List<LocalScanScopeDrive> Drives()
        {
            List<LocalScanScopeDrive> result = new List<LocalScanScopeDrive>();
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        string name = drive.Name;
                        string root = drive.RootDirectory.FullName;
                        DriveType type = drive.DriveType;
                        bool ready = drive.IsReady;
                        bool supported = ready && (type == DriveType.Fixed || type == DriveType.Removable);
                        string label = string.Empty;
                        string space = string.Empty;
                        if (supported)
                        {
                            try { label = drive.VolumeLabel; } catch { }
                            try { space = "，可用 " + FormatBytes(drive.AvailableFreeSpace) + " / " + FormatBytes(drive.TotalSize); } catch { }
                        }
                        LocalScanScopeDrive item = new LocalScanScopeDrive
                        {
                            Path = root,
                            Supported = supported,
                            Removable = type == DriveType.Removable,
                            UnsupportedReason = supported ? null : UnsupportedDriveReason(type, ready)
                        };
                        item.Display = name + (string.IsNullOrWhiteSpace(label) ? string.Empty : " " + label.Trim())
                            + "（" + DriveTypeName(type, ready) + space + "）";
                        result.Add(item);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (System.Security.SecurityException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
            return result;
        }

        internal static List<LocalScanScopeShortcut> Shortcuts(LocalScanScopeKind kind)
        {
            List<LocalScanScopeShortcut> result = new List<LocalScanScopeShortcut>();
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string primary, fallback, label;
            switch (kind)
            {
                case LocalScanScopeKind.Photos:
                    primary = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                    fallback = "Pictures";
                    label = "图片";
                    break;
                case LocalScanScopeKind.Videos:
                    primary = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                    fallback = "Videos";
                    label = "视频";
                    break;
                case LocalScanScopeKind.Music:
                    primary = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                    fallback = "Music";
                    label = "音乐";
                    break;
                default:
                    throw new ArgumentOutOfRangeException("kind");
            }
            if (string.IsNullOrWhiteSpace(primary) && !string.IsNullOrWhiteSpace(profile))
                primary = Path.Combine(profile, fallback);
            AddShortcut(result, label, primary, true);
            AddShortcut(result, "桌面", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false);
            string downloads = Scanner.KnownFolderPath(DownloadsFolder);
            if (string.IsNullOrWhiteSpace(downloads) && !string.IsNullOrWhiteSpace(profile))
                downloads = Path.Combine(profile, "Downloads");
            AddShortcut(result, "下载", downloads, false);
            return result;
        }

        internal static bool TryValidate(string path, IEnumerable<string> excludedRoots,
            out string normalized, out string reason)
        {
            normalized = null;
            reason = null;
            string full = NormalizePath(path);
            if (full == null)
            {
                reason = "路径为空或格式不正确。";
                return false;
            }
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
            {
                reason = "不支持网络路径。";
                return false;
            }
            string root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root) || root.Length < 3 || root[1] != ':')
            {
                reason = "仅支持本地固定磁盘或可移动磁盘。";
                return false;
            }
            try
            {
                DriveInfo drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    reason = "磁盘未就绪。";
                    return false;
                }
                if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable)
                {
                    reason = UnsupportedDriveReason(drive.DriveType, drive.IsReady);
                    return false;
                }
                if (!Directory.Exists(full))
                {
                    reason = "目录不存在或当前不可访问。";
                    return false;
                }
                FileAttributes attributes = File.GetAttributes(full);
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    reason = "所选位置不是文件夹。";
                    return false;
                }
                if (FileImport.IsNameSurrogateReparsePoint(full, attributes))
                {
                    reason = "不支持链接或联接点目录。";
                    return false;
                }
            }
            catch (IOException ex) { reason = ex.Message; return false; }
            catch (UnauthorizedAccessException ex) { reason = ex.Message; return false; }
            catch (System.Security.SecurityException ex) { reason = ex.Message; return false; }

            if (excludedRoots != null)
            {
                foreach (string excluded in excludedRoots)
                {
                    string excludedFull = NormalizePath(excluded);
                    if (string.IsNullOrEmpty(excludedFull)) continue;
                    try
                    {
                        if (FileImport.Within(excludedFull, full))
                        {
                            reason = "属于资源存储位置或 AppManager 数据目录，扫描时会自动跳过。";
                            return false;
                        }
                    }
                    catch { }
                }
            }
            normalized = full;
            return true;
        }

        internal static LocalScanScopeResolution Resolve(IEnumerable<string> roots, IEnumerable<string> excludedRoots)
        {
            LocalScanScopeResolution result = new LocalScanScopeResolution();
            List<string> exclusions = new List<string>();
            if (excludedRoots != null)
            {
                foreach (string excluded in excludedRoots)
                {
                    string full = NormalizePath(excluded);
                    if (!string.IsNullOrEmpty(full) && !exclusions.Contains(full, StringComparer.OrdinalIgnoreCase))
                        exclusions.Add(full);
                }
            }

            List<string> candidates = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (roots != null)
            {
                foreach (string value in roots)
                {
                    string normalized;
                    string reason;
                    if (!TryValidate(value, exclusions, out normalized, out reason))
                    {
                        result.Invalid.Add((string.IsNullOrWhiteSpace(value) ? "空路径" : value) + "：" + reason);
                        continue;
                    }
                    if (!seen.Add(normalized))
                    {
                        result.Merged++;
                        continue;
                    }
                    candidates.Add(normalized);
                }
            }

            candidates.Sort(delegate(string a, string b)
            {
                int length = a.Length.CompareTo(b.Length);
                return length != 0 ? length : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });
            foreach (string candidate in candidates)
            {
                bool contained = false;
                for (int i = 0; i < result.Roots.Count; i++)
                {
                    if (Within(result.Roots[i], candidate)) { contained = true; break; }
                }
                if (contained)
                {
                    result.Merged++;
                    continue;
                }
                for (int i = result.Roots.Count - 1; i >= 0; i--)
                {
                    if (Within(candidate, result.Roots[i]))
                    {
                        result.Roots.RemoveAt(i);
                        result.Merged++;
                    }
                }
                result.Roots.Add(candidate);
            }
            return result;
        }

        internal static bool IsDriveRoot(string path)
        {
            string full = NormalizePath(path);
            if (full == null) return false;
            string root = Path.GetPathRoot(full);
            string normalizedRoot = NormalizePath(root);
            return !string.IsNullOrEmpty(normalizedRoot)
                && string.Equals(normalizedRoot, full, StringComparison.OrdinalIgnoreCase);
        }

        internal static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                string full = Path.GetFullPath(path);
                string root = Path.GetPathRoot(full);
                if (!string.IsNullOrEmpty(root) && string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                    return root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                        ? root : root + Path.DirectorySeparatorChar;
                return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { return null; }
        }

        internal static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return value.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
        }

        private static void AddShortcut(List<LocalScanScopeShortcut> result, string label, string path, bool primary)
        {
            string full = NormalizePath(path);
            if (string.IsNullOrEmpty(full)) return;
            try { if (!Directory.Exists(full)) return; } catch { return; }
            for (int i = 0; i < result.Count; i++)
            {
                if (!string.Equals(result[i].Path, full, StringComparison.OrdinalIgnoreCase)) continue;
                if (primary) result[i].Primary = true;
                return;
            }
            result.Add(new LocalScanScopeShortcut { Label = label, Path = full, Primary = primary });
        }

        private static bool Within(string parent, string child)
        {
            try { return FileImport.Within(parent, child); }
            catch { return false; }
        }

        private static string DriveTypeName(DriveType type, bool ready)
        {
            if (!ready) return "未就绪";
            switch (type)
            {
                case DriveType.Fixed: return "固定磁盘";
                case DriveType.Removable: return "可移动磁盘";
                case DriveType.Network: return "网络位置";
                case DriveType.CDRom: return "光驱";
                case DriveType.Ram: return "内存盘";
                default: return "不支持的磁盘";
            }
        }

        private static string UnsupportedDriveReason(DriveType type, bool ready)
        {
            if (!ready) return "磁盘未就绪。";
            switch (type)
            {
                case DriveType.Network: return "不支持网络位置。";
                case DriveType.CDRom: return "不支持光驱。";
                case DriveType.Ram: return "不支持内存盘。";
                default: return "仅支持本地固定磁盘或可移动磁盘。";
            }
        }
    }
}
