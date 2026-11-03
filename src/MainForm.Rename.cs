using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AppManager
{
    /// <summary>路径改写工具：条目改名/移动后，把引用旧路径（或旧文件夹内路径）的值换成新路径。</summary>
    internal static class EntryPaths
    {
        /// <summary>value 等于 oldPath 或位于其下时换成 newPath 下的对应路径，否则原样返回。</summary>
        internal static string Rebase(string value, string oldPath, string newPath)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(oldPath)) return value;
            string from = oldPath.TrimEnd('\\', '/');
            if (string.Equals(value.TrimEnd('\\', '/'), from, StringComparison.OrdinalIgnoreCase)) return newPath;
            if (value.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase))
                return newPath.TrimEnd('\\', '/') + value.Substring(from.Length);
            return value;
        }
    }

    /// <summary>
    /// 条目改名。位于本应用存储目录内的文件/文件夹（导入时搬进来、归应用管理）改的是磁盘上的真实名称；
    /// 软件（exe、快捷方式等启动文件）与存储目录之外的条目（引用用户原位置的文件）只改显示名。
    /// </summary>
    public partial class MainForm
    {
        /// <summary>软件启动文件：改名只改显示名，改磁盘名会让程序或快捷方式失效。</summary>
        private static readonly string[] LauncherExt = { ".exe", ".lnk", ".url", ".appref-ms" };

        private static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>资源库保存失败、而且把文件改回原名也失败：内存记录保持新名称（与磁盘一致），不重扫以免读回旧路径。</summary>
        private bool renameUnsaved;

        /// <summary>改名入口（右键菜单与列表行按钮）。</summary>
        private void RenameAddedEntry(AppInfo app)
        {
            List<AddedEntry> entries = EntriesOf(app);
            if (entries.Count == 0) return;
            string path = entries[0].Path;
            if (RenamesOnDisk(app, path)) RenameOnDisk(app, path);
            else RenameDisplayName(app);
        }

        private List<AddedEntry> EntriesOf(AppInfo app)
        {
            return app == null ? new List<AddedEntry>() : EntriesAt(app.TargetPath);
        }

        /// <summary>
        /// 按路径取收录记录。每次修改前都要重新取：保存成功会清空记录缓存，
        /// 对话框打开期间（后台导入完成等）发生的保存会让之前取到的对象脱离列表，再改它会把旧值写回。
        /// </summary>
        private List<AddedEntry> EntriesAt(string path)
        {
            List<AddedEntry> result = new List<AddedEntry>();
            if (string.IsNullOrEmpty(path)) return result;
            foreach (AddedEntry e in config.AddedItems.ByPath(path))
                if (string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)) result.Add(e);
            return result;
        }

        /// <summary>
        /// 是否修改磁盘名：仅限位于本应用存储目录之内的收录文件/文件夹。资源记录也不例外——
        /// 已收录的原位置路径再导入资源分类时只登记引用，文件仍是用户自己的。
        /// 软件启动文件、扫描到的程序与快捷方式、网址、已失效的条目一律只改显示名。
        /// </summary>
        private bool RenamesOnDisk(AppInfo app, string path)
        {
            if (app == null || !app.IsFile) return false;
            if (string.IsNullOrEmpty(app.Key) || !app.Key.StartsWith("add:", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrEmpty(path) || Scanner.IsUrl(path)) return false;
            bool folder = Directory.Exists(path);
            if (!folder && !File.Exists(path)) return false;
            if (!folder && IsLauncherFile(path)) return false;
            return IsManagedPath(path);
        }

        internal static bool IsLauncherFile(string path)
        {
            string ext = Path.GetExtension(path ?? string.Empty);
            foreach (string launcher in LauncherExt)
                if (string.Equals(ext, launcher, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 路径位于存储目录之内，且不是存储结构本身（存储根、管理根下的 Files/Resources、资源分类目录）。
        /// 按配置推算存储目录，不创建目录、不改配置（ResolveManageRoot 等会建目录并可能写回默认根）。
        /// </summary>
        private bool IsManagedPath(string path)
        {
            string full;
            try { full = Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch (Exception ex)
            {
                if (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) return false;
                throw;
            }
            List<string> manageRoots = new List<string>();
            if (!string.IsNullOrEmpty(config.ManageRootPath)) manageRoots.Add(DesktopOrganizer.NormalizeRoot(config.ManageRootPath));
            else { manageRoots.Add(@"H:\Manage"); manageRoots.Add(DesktopOrganizer.QuickCenterDir); }   // 与 ResolveManageRoot 的回退顺序一致

            List<string> resourceRoots = new List<string>(), stores = new List<string>(), structural = new List<string>();
            if (!string.IsNullOrEmpty(config.ResourceStorePath)) resourceRoots.Add(DesktopOrganizer.NormalizeRoot(config.ResourceStorePath));
            if (!string.IsNullOrEmpty(config.FileStorePath)) stores.Add(DesktopOrganizer.NormalizeRoot(config.FileStorePath));
            if (!string.IsNullOrEmpty(config.FolderStorePath)) stores.Add(DesktopOrganizer.NormalizeRoot(config.FolderStorePath));
            foreach (string manage in manageRoots)
            {
                if (string.IsNullOrEmpty(config.ResourceStorePath)) resourceRoots.Add(Path.Combine(manage, "Resources"));
                if (string.IsNullOrEmpty(config.FileStorePath)) stores.Add(Path.Combine(manage, "Files"));
                if (string.IsNullOrEmpty(config.FolderStorePath)) stores.Add(manage);   // 文件夹默认直接存放在管理根下
                structural.Add(manage);
                structural.Add(Path.Combine(manage, "Files"));
                structural.Add(Path.Combine(manage, "Resources"));
            }
            stores.AddRange(resourceRoots);
            structural.AddRange(stores);

            foreach (string dir in structural)
                if (SamePath(dir, full)) return false;
            // 资源存储根下的直接子目录是各分类的存储目录
            string parent = Path.GetDirectoryName(full);
            foreach (string resourceRoot in resourceRoots)
                if (SamePath(resourceRoot, parent) && Directory.Exists(full)) return false;
            foreach (string store in stores)
                if (IsUnder(store, full)) return true;
            return false;
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                if (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) return false;
                throw;
            }
        }

        // ===== 只改显示名（软件与存储目录之外的条目）=====

        private void RenameDisplayName(AppInfo app)
        {
            string newName = InputDialog.Show("改名", "显示名（不修改磁盘上的文件）：", app.Name);
            if (newName == null) return;
            newName = newName.Trim();
            if (newName.Length == 0) return;
            List<AddedEntry> entries = EntriesOf(app);   // 对话框关闭后再取，见 EntriesAt
            if (entries.Count == 0) return;
            foreach (AddedEntry e in entries) e.Name = newName;
            app.Name = newName;
            config.Save();
            StartScan(false);
        }

        // ===== 修改磁盘名（存储目录内的文件/文件夹）=====

        private void RenameOnDisk(AppInfo app, string path)
        {
            string oldPath = path.TrimEnd('\\', '/');
            bool folder = Directory.Exists(oldPath);
            string blocker = RenameBlocker(oldPath, folder);
            if (blocker != null)
            {
                MessageBox.Show(this, blocker, "改名", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string initial = InitialRenameText(app, oldPath, folder);
            renameUnsaved = false;
            string accepted = InputDialog.Show("改名", "新名称（同时修改磁盘上的文件名）：", initial,
                folder ? -1 : StemLength(initial),
                delegate(string input) { return TryRenameOnDisk(app, oldPath, folder, input); });
            if (accepted == null) return;
            grid.Invalidate();
            if (!renameUnsaved) StartScan(false);   // 后台重扫：刷新列表、图标与目录缓存
        }

        /// <summary>改名前的阻断条件；文件夹内还有单独收录的条目时，改名会让它们指向失效路径。</summary>
        private string RenameBlocker(string path, bool folder)
        {
            if (folder && config.AddedItems.EntriesUnder(path).Count > 0)
                return "「" + Path.GetFileName(path) + "」内包含其它已收录的项目，改名后这些项目会失效。\n请先移除这些项目后再改名。";
            return null;
        }

        /// <summary>
        /// 预填文本：磁盘上的完整名称。旧版改名只改了显示名的条目以显示名为准（文件补上原扩展名），
        /// 确认即把磁盘名改成与列表一致。
        /// </summary>
        private static string InitialRenameText(AppInfo app, string path, bool folder)
        {
            string fileName = Path.GetFileName(path);
            string alias = app.Name ?? string.Empty;
            if (alias.Length == 0 || alias.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return fileName;
            if (folder) return alias;
            string stem = Path.GetFileNameWithoutExtension(fileName);
            if (stem.Length == 0 || string.Equals(alias, stem, StringComparison.OrdinalIgnoreCase)) return fileName;
            return alias + Path.GetExtension(fileName);
        }

        /// <summary>初始选中的主名长度（不含扩展名，同资源管理器）；「.gitignore」这类无主名的整体选中。</summary>
        internal static int StemLength(string name)
        {
            int stem = Path.GetFileNameWithoutExtension(name ?? string.Empty).Length;
            return stem == 0 ? -1 : stem;
        }

        /// <summary>文件/文件夹名合法性：非法字符、保留名、结尾句点/空格、长度。合法返回 null。</summary>
        internal static string ValidateEntryName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "名称不能为空";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "名称不能包含下列字符：\\ / : * ? \" < > |";
            if (name == "." || name == "..") return "名称不能是「" + name + "」";
            if (name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal))
                return "名称不能以句点或空格结尾";
            string device = name;
            int dot = device.IndexOf('.');
            if (dot >= 0) device = device.Substring(0, dot);
            device = device.TrimEnd(' ');
            foreach (string reserved in ReservedNames)
                if (string.Equals(device, reserved, StringComparison.OrdinalIgnoreCase))
                    return "「" + reserved + "」是 Windows 保留名称，不能用作文件名";
            if (name.Length > 255) return "名称过长（最多 255 个字符）";
            return null;
        }

        /// <summary>
        /// 对话框「确定」：校验并执行改名。返回 null 表示成功（关闭对话框），
        /// 返回错误信息则对话框保持打开并显示，返回空串表示用户在二次确认中取消。
        /// 先改磁盘再保存资源库；保存失败时把文件改回原名并撤回记录。
        /// </summary>
        private string TryRenameOnDisk(AppInfo app, string oldPath, bool folder, string input)
        {
            string name = (input ?? string.Empty).Trim();
            string error = ValidateEntryName(name);
            if (error != null) return error;
            string dir = Path.GetDirectoryName(oldPath);
            string oldName = Path.GetFileName(oldPath);
            string newPath = Path.Combine(dir, name);
            string display = DisplayNameFor(name, folder);

            if (string.Equals(name, oldName, StringComparison.Ordinal))
            {
                // 磁盘名不变：旧版留下的显示名与文件名不一致时，让显示名回到文件名
                return SyncDisplayName(app, oldPath, display);
            }
            bool caseOnly = string.Equals(name, oldName, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly)
            {
                if (File.Exists(newPath) || Directory.Exists(newPath)) return "此位置已有名为「" + name + "」的文件或文件夹";
                if (config.AddedItems.ByPath(newPath).Count > 0) return "「" + name + "」已被资源库中的其它条目使用";
            }
            if (!folder)
            {
                string oldExt = Path.GetExtension(oldName), newExt = Path.GetExtension(name);
                if (!string.Equals(oldExt, newExt, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string category in TypedCategoriesOf(app, EntriesAt(oldPath)))
                        if (!MediaRules.FitsCategory(category, newPath, false))
                            return MediaRules.CategoryTypeHint(category) + "，不能改为" + (newExt.Length > 0 ? "「" + newExt + "」" : "无扩展名");
                    if (!ConfirmDialog.Show("改名", "如果改变扩展名（" + ExtText(oldExt) + " → " + ExtText(newExt) + "），可能会导致文件不可用。\n确实要更改吗？"))
                        return string.Empty;
                }
            }

            try { RenamePath(oldPath, newPath); }
            catch (Exception ex)
            {
                if (!(ex is Win32Exception || ex is IOException || ex is UnauthorizedAccessException
                    || ex is ArgumentException || ex is NotSupportedException)) throw;
                return "无法改名：" + ex.Message.TrimEnd('。', '.', '\r', '\n');
            }
            ForgetMoveOutAfterRename(oldPath);

            // 从这里到保存之间没有模态窗口，RekeyEntry 取到的记录对象在保存前一直有效
            string oldKey = app.Key, newKey = "add:" + newPath;
            string oldAppName = app.Name, oldDisplayIcon = app.DisplayIcon;
            List<AddedEntry> moved = RekeyEntry(oldKey, newKey, oldPath, newPath, folder);
            Dictionary<AddedEntry, string> oldNames = new Dictionary<AddedEntry, string>();
            foreach (AddedEntry e in moved) { oldNames[e] = e.Name; e.Name = display; }
            app.TargetPath = newPath;
            app.Key = newKey;
            app.Name = display;
            if (string.Equals(app.DisplayIcon, oldPath, StringComparison.OrdinalIgnoreCase)) app.DisplayIcon = newPath;
            if (config.Save("physical.files.rename")) return null;

            // 资源库保存失败（具体原因由数据库错误提示另行说明）：把文件改回原名并撤回全部记录
            try { RenamePath(newPath, oldPath); }
            catch (Exception ex)
            {
                if (!(ex is Win32Exception || ex is IOException || ex is UnauthorizedAccessException)) throw;
                renameUnsaved = true;
                ShowStatusBar("已改名为「" + name + "」，但资源库未能保存，改回原名也失败。下次保存成功前请不要退出程序。", 0, 0);
                return null;
            }
            RekeyEntry(newKey, oldKey, newPath, oldPath, folder);
            foreach (KeyValuePair<AddedEntry, string> pair in oldNames) pair.Key.Name = pair.Value;
            app.TargetPath = oldPath;
            app.Key = oldKey;
            app.Name = oldAppName;
            app.DisplayIcon = oldDisplayIcon;
            return "资源库未能保存，已撤销改名";
        }

        /// <summary>显示名：文件为主名（列表补上扩展名展示），文件夹为完整名称。</summary>
        private static string DisplayNameFor(string name, bool folder)
        {
            if (folder) return name;
            string stem = Path.GetFileNameWithoutExtension(name);
            return stem.Length == 0 ? name : stem;
        }

        private static string ExtText(string ext)
        {
            return string.IsNullOrEmpty(ext) ? "无扩展名" : ext;
        }

        private string SyncDisplayName(AppInfo app, string path, string display)
        {
            List<AddedEntry> entries = EntriesAt(path);
            Dictionary<AddedEntry, string> oldNames = new Dictionary<AddedEntry, string>();
            foreach (AddedEntry e in entries)
                if (e.Name != display) { oldNames[e] = e.Name; e.Name = display; }
            if (oldNames.Count == 0) return null;
            string oldAppName = app.Name;
            app.Name = display;
            if (config.Save()) return null;
            foreach (KeyValuePair<AddedEntry, string> pair in oldNames) pair.Key.Name = pair.Value;
            app.Name = oldAppName;
            return "资源库未能保存";
        }

        /// <summary>条目当前所属、带类型约束的资源分类（照片/视频/…）；改扩展名后必须仍符合这些分类。</summary>
        private List<string> TypedCategoriesOf(AppInfo app, List<AddedEntry> entries)
        {
            List<string> result = new List<string>();
            bool resource = false;
            foreach (AddedEntry e in entries) if (e.IsResource) resource = true;
            if (!resource) return result;   // 类型约束只作用于资源页
            List<string> candidates = new List<string>();
            if (!string.IsNullOrEmpty(app.Category)) candidates.Add(app.Category);
            foreach (AddedEntry e in entries) if (!string.IsNullOrEmpty(e.Category)) candidates.Add(e.Category);
            string overrideCategory;
            if (config.CategoryOverride.TryGetValue(app.Key, out overrideCategory) && !string.IsNullOrEmpty(overrideCategory))
                candidates.Add(overrideCategory);
            List<string> extra;
            if (config.ItemCategories.TryGetValue(app.Key, out extra))
                foreach (string c in extra) if (!CollectionKeys.IsCollectionKey(c)) candidates.Add(c);
            foreach (string c in candidates)
                if (!result.Contains(c) && MediaRules.ResourceCategoryExt(c) != null && InCategory(app, c)) result.Add(c);
            return result;
        }

        // ===== 路径变化后的记录迁移（改名与拖入文件夹共用）=====

        /// <summary>
        /// 条目路径变化后迁移全部以「add:路径」为键的记录：收录记录、数据库条目与常用/隐藏/排序
        /// （由 QueueEntryRename 一并处理）、分类覆盖、额外归属与集合成员、分类排除，
        /// 以及按路径引用的集合封面和迷你播放器播放列表。返回被改写路径的收录记录（随后修改它们才安全）。
        /// </summary>
        private List<AddedEntry> RekeyEntry(string oldKey, string newKey, string oldPath, string newPath, bool folder)
        {
            List<AddedEntry> moved = EntriesAt(oldPath);
            foreach (AddedEntry e in moved)
            {
                e.Path = newPath;
                e.IsFolder = folder;
            }
            if (!string.IsNullOrEmpty(oldKey) && !string.Equals(oldKey, newKey, StringComparison.Ordinal))
            {
                config.QueueEntryRename(oldKey, newKey);
                MoveKey(config.CategoryOverride, oldKey, newKey);
                MoveKey(config.ItemCategories, oldKey, newKey);
                MoveKey(config.ExcludedCategories, oldKey, newKey);
            }
            // 集合封面与播放列表存的是文件路径而不是条目键，需要单独跟随
            foreach (ResourceCollection collection in config.Collections)
                collection.Cover = EntryPaths.Rebase(collection.Cover, oldPath, newPath);
            if (miniMusic != null && !miniMusic.IsDisposed) miniMusic.RebasePath(oldPath, newPath);
            InvalidateAddedIndex();   // 上文改写了 AddedEntry.Path，按旧路径建的索引必须失效
            return moved;
        }

        private static void MoveKey<T>(Dictionary<string, T> map, string oldKey, string newKey)
        {
            T value;
            if (!map.TryGetValue(oldKey, out value)) return;
            map.Remove(oldKey);
            map[newKey] = value;
        }

        // ===== 磁盘改名 =====

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileExW(string existingFileName, string newFileName, int flags);

        /// <summary>
        /// 同目录改名：MoveFileEx 不覆盖已有文件，文件与文件夹通用，
        /// 也支持只改大小写（Directory.Move 会把仅大小写不同视为同一路径而拒绝）。
        /// </summary>
        internal static void RenamePath(string from, string to)
        {
            if (!MoveFileExW(LongPath(from), LongPath(to), 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private static string LongPath(string path)
        {
            string full = Path.GetFullPath(path);
            if (full.Length < 248 || full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
            return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full.Substring(2) : @"\\?\" + full;
        }
    }
}
