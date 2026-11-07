using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AppManager
{
    internal sealed class LocalMediaScanProgress
    {
        internal int DirectoriesScanned;
        internal int DirectoriesSkipped;
        internal int FilesFound;
        internal long CopyBytes;
        internal string Path = string.Empty;
    }

    internal sealed class LocalMediaScanResult
    {
        internal readonly SortedDictionary<int, List<string>> Years = new SortedDictionary<int, List<string>>();
        internal readonly List<string> FailureSamples = new List<string>();
        internal int UnreadableDirectories;
        internal int ReadFailures;
        internal int Reused;
        internal int DirectoriesScanned;
        internal int DirectoriesSkipped;
        internal int CloudFiles;
        internal long Bytes;
        internal long CopyBytes;

        internal int Count { get { return Years.Values.Sum(paths => paths.Count); } }

        internal void Add(int year, string path, long bytes, bool reused)
        {
            List<string> paths;
            if (!Years.TryGetValue(year, out paths)) Years[year] = paths = new List<string>();
            paths.Add(path);
            Bytes += bytes;
            if (reused) Reused++;
            else CopyBytes += bytes;
        }

        internal void AddFailureSample(string path)
        {
            if (FailureSamples.Count < 5 && !string.IsNullOrEmpty(path)) FailureSamples.Add(path);
        }
    }

    /// <summary>
    /// 照片/视频共用的本机媒体扫描：按分类扩展名筛选、按拍摄或创建时间归年，
    /// 并复用已完成导入记录避免重复复制。扫描本身只读，源文件保持不变。
    /// </summary>
    internal static class LocalMediaScanner
    {
        private const int FilesPerSecond = 100;

        internal static Dictionary<string, string> CompletedImportTargets(string journalDirectory,
            Func<List<string>, Dictionary<string, string>> registeredPaths, CancellationToken cancel)
        {
            Dictionary<string, string> candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(journalDirectory)) return candidates;
            foreach (string file in Directory.EnumerateFiles(journalDirectory, "*.xml"))
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    ImportBatch batch = FileImport.Load(file);
                    if (!batch.Committed || !batch.Resource || batch.Operation != ImportOperation.Copy) continue;
                    foreach (ImportItem item in batch.Items)
                    {
                        if (item.State != FileImport.Copied || string.IsNullOrEmpty(item.Source) || string.IsNullOrEmpty(item.Target)) continue;
                        candidates[item.Source] = item.Target;
                    }
                }
                catch (Exception ex) { Paths.LogError(new IOException("读取媒体导入记录失败：" + file, ex)); }
            }
            if (candidates.Count == 0) return candidates;
            Dictionary<string, string> registered = registeredPaths(candidates.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in candidates)
            {
                cancel.ThrowIfCancellationRequested();
                string target = null;
                if (registered.TryGetValue(pair.Value, out target) && File.Exists(target)) result[pair.Key] = target;
            }
            return result;
        }

        internal static LocalMediaScanResult Scan(string category, IEnumerable<string> roots, IEnumerable<string> excludedRoots,
            IDictionary<string, string> importedTargets, Action<LocalMediaScanProgress> progress, CancellationToken cancel)
        {
            if (string.IsNullOrEmpty(category)) throw new ArgumentException("扫描分类不能为空。", "category");
            LocalMediaScanResult result = new LocalMediaScanResult();
            LocalMediaScanProgress scanProgress = new LocalMediaScanProgress();
            List<string> exclusions = (excludedRoots ?? Enumerable.Empty<string>()).Where(path => !string.IsNullOrEmpty(path))
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots ?? Enumerable.Empty<string>())
            {
                cancel.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(root) || IsExcluded(root, exclusions))
                {
                    result.DirectoriesSkipped++;
                    continue;
                }
                Stack<string> pending = new Stack<string>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    string directory = pending.Pop();
                    if (IsExcluded(directory, exclusions))
                    {
                        result.DirectoriesSkipped++;
                        continue;
                    }
                    result.DirectoriesScanned++;
                    IEnumerable<string> entries;
                    try { entries = Directory.EnumerateFileSystemEntries(directory); }
                    catch (Exception ex)
                    {
                        if (!IsFileSystemError(ex)) throw;
                        result.UnreadableDirectories++;
                        result.AddFailureSample(directory);
                        continue;
                    }
                    try
                    {
                        foreach (string path in entries)
                        {
                            cancel.ThrowIfCancellationRequested();
                            FileAttributes attributes;
                            try { attributes = File.GetAttributes(path); }
                            catch (Exception ex)
                            {
                                if (!IsFileSystemError(ex)) throw;
                                result.ReadFailures++;
                                result.AddFailureSample(path);
                                continue;
                            }
                            if ((attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                if ((attributes & FileAttributes.Directory) != 0) result.DirectoriesSkipped++;
                                continue;
                            }
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                string name = Path.GetFileName(path.TrimEnd('\\', '/'));
                                if (IsDeletedOrSystemDirectory(name) || IsExcluded(path, exclusions)) result.DirectoriesSkipped++;
                                else pending.Push(path);
                                continue;
                            }
                            if (!MediaRules.FitsCategory(category, path, false) || !seen.Add(path)) continue;
                            if (MusicFileClassifier.IsCloudPlaceholder(attributes))
                            {
                                result.CloudFiles++;
                                continue;
                            }
                            AddMedia(path, importedTargets, result);
                            Pace(cancel);
                            if (progress != null)
                            {
                                scanProgress.DirectoriesScanned = result.DirectoriesScanned;
                                scanProgress.DirectoriesSkipped = result.DirectoriesSkipped;
                                scanProgress.FilesFound = result.Count;
                                scanProgress.CopyBytes = result.CopyBytes;
                                scanProgress.Path = path;
                                progress(scanProgress);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!IsFileSystemError(ex)) throw;
                        result.UnreadableDirectories++;
                        result.AddFailureSample(directory);
                    }
                    if (progress != null)
                    {
                        scanProgress.DirectoriesScanned = result.DirectoriesScanned;
                        scanProgress.DirectoriesSkipped = result.DirectoriesSkipped;
                        scanProgress.FilesFound = result.Count;
                        scanProgress.CopyBytes = result.CopyBytes;
                        scanProgress.Path = directory;
                        progress(scanProgress);
                    }
                }
            }
            return result;
        }

        private static void Pace(CancellationToken cancel)
        {
            if (cancel.WaitHandle.WaitOne(1000 / FilesPerSecond)) cancel.ThrowIfCancellationRequested();
        }

        private static void AddMedia(string source, IDictionary<string, string> importedTargets, LocalMediaScanResult result)
        {
            try
            {
                DateTime taken;
                if (!MediaInfo.TryGetTakenDate(source, out taken)) taken = File.GetLastWriteTime(source);
                long bytes = new FileInfo(source).Length;
                string target = null;
                bool reused = importedTargets != null && importedTargets.TryGetValue(source, out target);
                result.Add(taken.Year, reused ? target : source, bytes, reused);
            }
            catch (Exception ex)
            {
                if (!IsFileSystemError(ex) && !(ex is ArgumentException) && !(ex is NotSupportedException)) throw;
                result.ReadFailures++;
                result.AddFailureSample(source);
            }
        }

        private static bool IsExcluded(string path, List<string> exclusions)
        {
            try
            {
                string full = Path.GetFullPath(path);
                foreach (string excluded in exclusions)
                    if (FileImport.Within(excluded, full)) return true;
            }
            catch (Exception ex)
            {
                if (!IsFileSystemError(ex) && !(ex is ArgumentException) && !(ex is NotSupportedException)) throw;
                return true;
            }
            return false;
        }

        private static bool IsDeletedOrSystemDirectory(string name)
        {
            return string.Equals(name, "$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "System Volume Information", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Recovery", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Config.Msi", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFileSystemError(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException;
        }
    }
}
