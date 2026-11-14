using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace AppManager
{
    /// <summary>一个资源存储位置。停用后不再接收新文件，但仍用于识别库内已有文件。</summary>
    public sealed class ResourceStoreLocation
    {
        public string Path = string.Empty;
        public bool Enabled = true;

        public ResourceStoreLocation() { }
        public ResourceStoreLocation(string path, bool enabled)
        {
            Path = path ?? string.Empty;
            Enabled = enabled;
        }

        internal ResourceStoreLocation Clone()
        {
            return new ResourceStoreLocation(Path, Enabled);
        }
    }

    /// <summary>
    /// 资源存储位置池：按配置顺序选择位置，复制前按卷可用空间预检，同一卷只计算一次可用空间。
    /// 同卷移动使用硬链接，不要求预留 1 GB；只有真正复制或跨卷移动才检查容量。
    /// </summary>
    internal static class ResourceStore
    {
        internal const long ReserveBytes = 1024L * 1024L * 1024L;
        internal const string CapacityMessage = "所有资源存储位置均不可用，或可用空间不足（每个位置保留 1 GB）。源文件未移动。";

        internal static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            return DesktopOrganizer.NormalizeRoot(path.Trim());
        }

        /// <summary>设置界面可编辑的位置；旧版单一路径在首次编辑时转成第一项。</summary>
        internal static List<ResourceStoreLocation> EditableLocations(AppConfig config)
        {
            List<ResourceStoreLocation> result = new List<ResourceStoreLocation>();
            if (config != null)
            {
                foreach (ResourceStoreLocation location in config.ResourceStoreLocations)
                {
                    if (location == null || string.IsNullOrWhiteSpace(location.Path)) continue;
                    result.Add(new ResourceStoreLocation(Normalize(location.Path), location.Enabled));
                }
                if (result.Count == 0 && !string.IsNullOrWhiteSpace(config.ResourceStorePath))
                    result.Add(new ResourceStoreLocation(Normalize(config.ResourceStorePath), true));
            }
            return result;
        }

        /// <summary>新导入可用的位置。显式配置全部停用时返回空，不再偷偷回退到默认目录。</summary>
        internal static List<string> ActiveRoots(AppConfig config, string defaultRoot)
        {
            List<string> result = new List<string>();
            List<ResourceStoreLocation> explicitLocations = EditableLocations(config);
            if (explicitLocations.Count > 0)
            {
                foreach (ResourceStoreLocation location in explicitLocations)
                    if (location.Enabled) AddUnique(result, location.Path);
                return result;
            }
            if (config != null && !string.IsNullOrWhiteSpace(config.ResourceStorePath))
                AddUnique(result, Normalize(config.ResourceStorePath));
            else
                AddUnique(result, Normalize(defaultRoot));
            return result;
        }

        /// <summary>所有已知位置，含已停用位置。用于识别库内文件，避免被当成外部文件。</summary>
        internal static List<string> KnownRoots(AppConfig config, string defaultRoot)
        {
            List<string> result = new List<string>();
            List<ResourceStoreLocation> explicitLocations = EditableLocations(config);
            foreach (ResourceStoreLocation location in explicitLocations) AddUnique(result, location.Path);
            if (config != null && !string.IsNullOrWhiteSpace(config.ResourceStorePath))
                AddUnique(result, Normalize(config.ResourceStorePath));
            if (result.Count == 0) AddUnique(result, Normalize(defaultRoot));
            return result;
        }

        internal static string PrimaryRoot(AppConfig config, string defaultRoot)
        {
            List<string> active = ActiveRoots(config, defaultRoot);
            if (active.Count > 0) return active[0];
            List<ResourceStoreLocation> explicitLocations = EditableLocations(config);
            if (explicitLocations.Count > 0) return explicitLocations[0].Path;
            return Normalize(defaultRoot);
        }

        internal static bool IsUnderAny(IEnumerable<string> roots, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (roots == null) return false;
            foreach (string root in roots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                try
                {
                    if (FileImport.Within(root, path)) return true;
                }
                catch (Exception ex)
                {
                    if (!(ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)) throw;
                }
            }
            return false;
        }

        internal static string CategoryDirectory(string root, string category)
        {
            return Path.GetFullPath(Path.Combine(Normalize(root), SafeCategoryDirectory(category)));
        }

        internal static string SafeCategoryDirectory(string name)
        {
            if (string.IsNullOrEmpty(name)) return "其他";
            char[] invalid = Path.GetInvalidFileNameChars();
            char[] chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                for (int j = 0; j < invalid.Length; j++)
                    if (chars[i] == invalid[j]) { chars[i] = '_'; break; }
            }
            string value = new string(chars).Trim().Trim('.');
            return value.Length == 0 ? "其他" : value;
        }

        /// <summary>拖放提示用：来源都在同一卷且有同卷资源位置时，移动不复制数据。</summary>
        internal static bool CanMoveWithinSourceVolume(IEnumerable<string> roots, IList<string> sources, IList<bool> folders, string category)
        {
            if (sources == null || sources.Count == 0 || folders == null || sources.Count != folders.Count) return false;
            string volume = null;
            for (int i = 0; i < sources.Count; i++)
            {
                string current = FileImport.Volume(sources[i]);
                if (current == null) return false;
                if (volume == null) volume = current;
                else if (!string.Equals(volume, current, StringComparison.OrdinalIgnoreCase)) return false;
            }
            for (int i = 0; i < sources.Count; i++)
            {
                bool found = false;
                foreach (string root in roots)
                {
                    if (!string.Equals(volume, FileImport.Volume(root), StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        string dir = CategoryDirectory(root, category);
                        if (folders[i] && FileImport.Within(sources[i], dir)) continue;
                        found = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (!(ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)) throw;
                    }
                }
                if (!found) return false;
            }
            return true;
        }

        /// <summary>为单个文件/文件夹选位置。Move 优先同卷；其他情况按顺序检查容量。</summary>
        internal static string ChooseRoot(IEnumerable<string> roots, string source, bool folder, string category,
            ImportOperation operation, long requiredBytes, ICollection<string> excluded, out string directory, out string error,
            bool create = true)
        {
            directory = null; error = null;
            string sourceVolume = string.IsNullOrWhiteSpace(source) ? null : FileImport.Volume(source);
            HashSet<string> attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (excluded != null) foreach (string value in excluded) attempted.Add(value);
            List<string> ordered = new List<string>();
            foreach (string root in roots) if (!string.IsNullOrWhiteSpace(root)) AddUnique(ordered, Normalize(root));

            if (operation == ImportOperation.Move && sourceVolume != null)
            {
                foreach (string root in ordered)
                {
                    if (attempted.Contains(root)) continue;
                    if (!string.Equals(sourceVolume, FileImport.Volume(root), StringComparison.OrdinalIgnoreCase)) continue;
                    if (TryTargetDirectory(root, source, folder, category, create, out directory, out error)) return root;
                    attempted.Add(root);
                }
            }

            foreach (string root in ordered)
            {
                if (attempted.Contains(root)) continue;
                attempted.Add(root);
                bool sameVolumeMove = operation == ImportOperation.Move && sourceVolume != null
                    && string.Equals(sourceVolume, FileImport.Volume(root), StringComparison.OrdinalIgnoreCase);
                if (!sameVolumeMove)
                {
                    long available;
                    if (!TryGetFreeBytes(root, out available)) continue;
                    if (requiredBytes < 0 || available < requiredBytes || available - requiredBytes < ReserveBytes) continue;
                }
                if (TryTargetDirectory(root, source, folder, category, create, out directory, out error)) return root;
            }
            directory = null;
            error = CapacityMessage;
            return null;
        }

        private static bool TryTargetDirectory(string root, string source, bool folder, string category, bool create,
            out string directory, out string error)
        {
            directory = null; error = null;
            try
            {
                directory = CategoryDirectory(root, category);
                if (folder && !string.IsNullOrWhiteSpace(source) && FileImport.Within(source, directory)) return false;
                if (create) Directory.CreateDirectory(directory);
                return true;
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException
                    || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)) throw;
                error = root + " 不可用：" + ex.Message;
                directory = null;
                return false;
            }
        }

        internal static bool TryGetFreeBytes(string path, out long available)
        {
            ulong bytes;
            if (FileImport.TryGetAvailableBytes(path, out bytes) && bytes <= long.MaxValue)
            {
                available = (long)bytes;
                return true;
            }
            available = 0;
            return false;
        }

        /// <summary>复制前计算文件或整个文件夹的字节数；文件被占用、含链接或无法读取时返回失败。</summary>
        internal static bool TryGetSize(string path, bool folder, CancellationToken cancel, out long bytes, out string error)
        {
            bytes = 0; error = null;
            try
            {
                if (!folder)
                {
                    FileInfo file = new FileInfo(path);
                    if (!file.Exists) { error = "源文件不存在：" + path; return false; }
                    bytes = file.Length;
                    return true;
                }
                Stack<string> pending = new Stack<string>();
                pending.Push(path);
                long total = 0;
                while (pending.Count > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    string current = pending.Pop();
                    DirectoryInfo dir = new DirectoryInfo(current);
                    if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        error = "文件夹包含链接，不能导入：" + current;
                        return false;
                    }
                    foreach (FileSystemInfo child in dir.EnumerateFileSystemInfos())
                    {
                        cancel.ThrowIfCancellationRequested();
                        if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            error = "文件夹包含链接，不能导入：" + child.FullName;
                            return false;
                        }
                        DirectoryInfo sub = child as DirectoryInfo;
                        if (sub != null) pending.Push(sub.FullName);
                        else total = checked(total + ((FileInfo)child).Length);
                    }
                }
                bytes = total;
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException
                    || ex is NotSupportedException || ex is PathTooLongException || ex is OverflowException)) throw;
                error = "无法计算大小：" + ex.Message;
                return false;
            }
        }

        internal static void AddUnique(List<string> list, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            foreach (string existing in list)
                if (string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(value);
        }
    }
}
