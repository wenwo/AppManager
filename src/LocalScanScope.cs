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
        internal string Title;
        internal string Detail;
        internal bool Supported;
        internal string UnsupportedReason;
        internal bool Removable;
    }

    internal enum LocalScanScopeRowKind
    {
        Header,
        Note,
        Shortcut,
        Drive,
        Custom
    }

    /// <summary>范围清单中的一行。标题行和提示行不可勾选，Path 为空。</summary>
    internal sealed class LocalScanScopeRow
    {
        internal LocalScanScopeRowKind Kind;
        internal string Title;
        internal string Detail;
        internal string Path;
        internal bool Enabled;
        internal bool Checked;
        internal bool Warning;
        /// <summary>已被另一个勾选的上级范围包含时，为该上级路径；否则为空。</summary>
        internal string CoveredBy;

        public override string ToString()
        {
            return Title ?? string.Empty;
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
                            try { space = "  ·  可用 " + FormatBytes(drive.AvailableFreeSpace) + " / " + FormatBytes(drive.TotalSize); } catch { }
                        }
                        LocalScanScopeDrive item = new LocalScanScopeDrive
                        {
                            Path = root,
                            Supported = supported,
                            Removable = type == DriveType.Removable,
                            UnsupportedReason = supported ? null : UnsupportedDriveReason(type, ready)
                        };
                        item.Title = name + (string.IsNullOrWhiteSpace(label) ? string.Empty : "  " + label.Trim());
                        item.Detail = supported ? DriveTypeName(type, ready) + space : item.UnsupportedReason;
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

        /// <summary>
        /// 生成范围清单的行：常用文件夹、磁盘、已添加的文件夹。drives 为 null 表示磁盘仍在读取。
        /// knownRoots 是本次打开后出现过的全部范围；其中没有对应常用文件夹或可用磁盘的，列在「已添加的文件夹」。
        /// 勾选状态只来自 selected；被另一个已勾选的上级范围包含的行会标出 CoveredBy。不做磁盘访问。
        /// </summary>
        internal static List<LocalScanScopeRow> BuildRows(IList<LocalScanScopeShortcut> shortcuts,
            IList<LocalScanScopeDrive> drives, string driveError, IList<string> knownRoots, IList<string> selected)
        {
            List<LocalScanScopeRow> rows = new List<LocalScanScopeRow>();
            HashSet<string> listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (shortcuts != null && shortcuts.Count > 0)
            {
                rows.Add(new LocalScanScopeRow { Kind = LocalScanScopeRowKind.Header, Title = "常用文件夹" });
                foreach (LocalScanScopeShortcut shortcut in shortcuts)
                {
                    string path = NormalizePath(shortcut.Path);
                    if (path == null || !listed.Add(path)) continue;
                    rows.Add(new LocalScanScopeRow
                    {
                        Kind = LocalScanScopeRowKind.Shortcut, Title = shortcut.Label, Detail = path, Path = path, Enabled = true
                    });
                }
            }

            rows.Add(new LocalScanScopeRow { Kind = LocalScanScopeRowKind.Header, Title = "磁盘" });
            if (drives == null)
                rows.Add(new LocalScanScopeRow { Kind = LocalScanScopeRowKind.Note, Title = "正在读取磁盘…" });
            else
            {
                if (!string.IsNullOrEmpty(driveError))
                    rows.Add(new LocalScanScopeRow { Kind = LocalScanScopeRowKind.Note, Title = "读取磁盘失败：" + driveError, Warning = true });
                int before = rows.Count;
                for (int pass = 0; pass < 2; pass++)
                {
                    // 可扫描的磁盘在前，不可用的磁盘排在后面并写明原因
                    foreach (LocalScanScopeDrive drive in drives)
                    {
                        if (drive == null || drive.Supported != (pass == 0)) continue;
                        string path = NormalizePath(drive.Path);
                        if (path == null) continue;
                        if (drive.Supported && !listed.Add(path)) continue;
                        rows.Add(new LocalScanScopeRow
                        {
                            Kind = LocalScanScopeRowKind.Drive,
                            Title = string.IsNullOrEmpty(drive.Title) ? path : drive.Title,
                            Detail = drive.Supported ? drive.Detail : drive.UnsupportedReason,
                            Path = path,
                            Enabled = drive.Supported
                        });
                    }
                }
                if (rows.Count == before && string.IsNullOrEmpty(driveError))
                    rows.Add(new LocalScanScopeRow { Kind = LocalScanScopeRowKind.Note, Title = "没有可扫描的磁盘。" });
            }

            List<LocalScanScopeRow> custom = new List<LocalScanScopeRow>();
            if (knownRoots != null)
            {
                foreach (string root in knownRoots)
                {
                    string path = NormalizePath(root);
                    if (path == null || listed.Contains(path)) continue;
                    // 磁盘读完之前先不列盘符，避免它先出现在这里、随后又跳到「磁盘」分组
                    if (drives == null && IsDriveRoot(path)) continue;
                    listed.Add(path);
                    string name = Path.GetFileName(path);
                    custom.Add(new LocalScanScopeRow
                    {
                        Kind = LocalScanScopeRowKind.Custom, Title = string.IsNullOrEmpty(name) ? path : name,
                        Detail = path, Path = path, Enabled = true
                    });
                }
            }
            if (custom.Count > 0)
            {
                rows.Add(new LocalScanScopeRow { Kind = LocalScanScopeRowKind.Header, Title = "已添加的文件夹" });
                rows.AddRange(custom);
            }

            List<string> chosen = new List<string>();
            if (selected != null)
            {
                foreach (string value in selected)
                {
                    string path = NormalizePath(value);
                    if (path != null && !chosen.Contains(path, StringComparer.OrdinalIgnoreCase)) chosen.Add(path);
                }
            }
            foreach (LocalScanScopeRow row in rows)
            {
                if (!row.Enabled || !chosen.Contains(row.Path, StringComparer.OrdinalIgnoreCase)) continue;
                row.Checked = true;
                foreach (string other in chosen)
                {
                    if (string.Equals(other, row.Path, StringComparison.OrdinalIgnoreCase) || !Within(other, row.Path)) continue;
                    if (row.CoveredBy == null || other.Length < row.CoveredBy.Length) row.CoveredBy = other;
                }
            }
            return rows;
        }

        /// <summary>范围清单下方的一句话汇总：扫描几个磁盘、几个文件夹，以及合并、整盘和不可用提示。</summary>
        internal static string Summary(LocalScanScopeResolution resolution)
        {
            if (resolution.Roots.Count == 0)
                return resolution.Invalid.Count == 0 ? "请至少勾选一个位置。" : "没有可用范围：" + resolution.Invalid[0];
            int drives = resolution.DriveCount;
            int folders = resolution.Roots.Count - drives;
            string text = "将扫描 " + (drives > 0 ? drives + " 个磁盘" : string.Empty)
                + (drives > 0 && folders > 0 ? "、" : string.Empty)
                + (folders > 0 ? folders + " 个文件夹" : string.Empty) + "。";
            if (resolution.Merged > 0) text += "已合并 " + resolution.Merged + " 个重复或重叠范围。";
            if (drives > 0) text += "整盘扫描可能耗时较长。";
            if (resolution.Invalid.Count > 0) text += "另有 " + resolution.Invalid.Count + " 个范围不可用。";
            return text;
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
