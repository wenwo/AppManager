using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
        internal int CleanupPending;
        internal long Bytes;
        internal long CopyBytes;

        internal int Count { get { return Years.Values.Sum(paths => paths.Count); } }

        internal void Add(int year, string path, long bytes, bool reused, bool cleanupPending)
        {
            List<string> paths;
            if (!Years.TryGetValue(year, out paths)) Years[year] = paths = new List<string>();
            paths.Add(path);
            Bytes += bytes;
            if (reused) Reused++;
            else CopyBytes += bytes;
            if (cleanupPending) CleanupPending++;
        }

        internal void AddFailureSample(string path)
        {
            if (FailureSamples.Count < 5 && !string.IsNullOrEmpty(path)) FailureSamples.Add(path);
        }
    }

    internal sealed class LocalMediaImportTarget
    {
        internal readonly string Target;
        internal readonly bool PendingCleanup;

        internal LocalMediaImportTarget(string target, bool pendingCleanup)
        {
            Target = target;
            PendingCleanup = pendingCleanup;
        }
    }

    internal sealed class LocalMediaImportPlan
    {
        internal readonly string[] Paths;
        internal readonly ImportOperation Operation;
        internal readonly bool DeleteSuccessfulSourcesOnPartial;

        internal LocalMediaImportPlan(string[] paths, ImportOperation operation, bool deleteSuccessfulSourcesOnPartial)
        {
            Paths = paths;
            Operation = operation;
            DeleteSuccessfulSourcesOnPartial = deleteSuccessfulSourcesOnPartial;
        }
    }

    /// <summary>
    /// 照片/视频共用的本机媒体扫描：按分类扩展名筛选、按拍摄或创建时间归年，
    /// 并复用已完成导入记录避免重复复制。扫描本身只读，源文件保持不变。
    /// </summary>
    internal static class LocalMediaScanner
    {
        private const int FilesPerSecond = 100;

        private sealed class ImportCandidate
        {
            internal readonly string Target;
            internal readonly ImportFileStamp Stamp;
            internal readonly bool Move;

            internal ImportCandidate(string target, ImportFileStamp stamp, bool move)
            {
                Target = target;
                Stamp = stamp;
                Move = move;
            }
        }

        internal static Dictionary<string, LocalMediaImportTarget> CompletedImportTargets(string journalDirectory,
            Func<List<string>, Dictionary<string, string>> registeredPaths, CancellationToken cancel)
        {
            Dictionary<string, LocalMediaImportTarget> result = new Dictionary<string, LocalMediaImportTarget>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(journalDirectory)) return result;
            Dictionary<string, ImportCandidate> candidates = new Dictionary<string, ImportCandidate>(StringComparer.OrdinalIgnoreCase);
            foreach (FileInfo journal in new DirectoryInfo(journalDirectory).GetFiles("*.xml").OrderBy(file => file.LastWriteTimeUtc))
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    ImportBatch batch = FileImport.Load(journal.FullName);
                    if (!batch.Committed || !batch.Resource) continue;
                    bool copy = batch.Operation == ImportOperation.Copy;
                    bool move = batch.Operation == ImportOperation.Move;
                    if (!copy && !move) continue;
                    foreach (ImportItem item in batch.Items)
                    {
                        if (move && item.State == FileImport.Moved)
                        {
                            if (!string.IsNullOrEmpty(item.Source)) candidates.Remove(item.Source);
                            continue;
                        }
                        bool copied = item.State == FileImport.Copied;
                        bool deleteFailed = move && item.State == FileImport.DeleteFailed;
                        if (!(copy && copied) && !deleteFailed && !(move && copied)) continue;
                        if (item.Reference || item.Folder || string.IsNullOrEmpty(item.Source) || string.IsNullOrEmpty(item.Target)
                            || item.Files.Count != 1) continue;
                        ImportFileStamp stamp = item.Files[0];
                        if (string.IsNullOrEmpty(stamp.TargetId)) continue;
                        candidates[item.Source] = new ImportCandidate(item.Target, stamp, move);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Paths.LogError(new IOException("读取媒体导入记录失败：" + journal.FullName, ex)); }
            }
            if (candidates.Count == 0) return result;
            List<string> lookupPaths = new List<string>();
            HashSet<string> lookupSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, ImportCandidate> pair in candidates)
            {
                if (lookupSeen.Add(pair.Value.Target)) lookupPaths.Add(pair.Value.Target);
                // 目标改名后数据库可按 OriginPath 把原来源路径映射到新目标。
                if (lookupSeen.Add(pair.Key)) lookupPaths.Add(pair.Key);
            }
            Dictionary<string, string> registered = registeredPaths(lookupPaths);
            if (registered == null) return result;
            foreach (KeyValuePair<string, ImportCandidate> pair in candidates)
            {
                cancel.ThrowIfCancellationRequested();
                ImportCandidate candidate = pair.Value;
                string target = ResolveTarget(pair.Key, candidate, registered);
                if (target == null || !SourceMatches(pair.Key, candidate.Stamp, candidate.Move, cancel)) continue;
                result[pair.Key] = new LocalMediaImportTarget(target, candidate.Move);
            }
            return result;
        }

        private static string ResolveTarget(string source, ImportCandidate candidate, IDictionary<string, string> registered)
        {
            string target;
            if (registered.TryGetValue(candidate.Target, out target) && !string.IsNullOrEmpty(target)
                && TargetMatches(target, candidate.Stamp)) return target;
            if (!string.Equals(candidate.Target, source, StringComparison.OrdinalIgnoreCase)
                && registered.TryGetValue(source, out target) && !string.IsNullOrEmpty(target)
                && TargetMatches(target, candidate.Stamp)) return target;
            return null;
        }

        internal static List<LocalMediaImportPlan> PlanImports(IList<string> paths, ImportOperation operation, int batchSize)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            if (batchSize <= 0) throw new ArgumentOutOfRangeException("batchSize");
            bool deleteSuccessfulSources = operation == ImportOperation.Move;
            List<LocalMediaImportPlan> result = new List<LocalMediaImportPlan>();
            for (int offset = 0; offset < paths.Count; offset += batchSize)
            {
                string[] batch = new string[Math.Min(batchSize, paths.Count - offset)];
                for (int i = 0; i < batch.Length; i++) batch[i] = paths[offset + i];
                result.Add(new LocalMediaImportPlan(batch, operation, deleteSuccessfulSources));
            }
            return result;
        }

        private static bool SourceMatches(string source, ImportFileStamp stamp, bool verifyHash, CancellationToken cancel)
        {
            try
            {
                FileInfo info = new FileInfo(source);
                if (!info.Exists || info.Length != stamp.Length || info.LastWriteTimeUtc.Ticks != stamp.Modified) return false;
                // 同卷移动的硬链接没有哈希；仅靠长度和时间无法识别同路径替换的新文件。
                // 此时源路径必须仍与日志记录的目标身份是同一文件，之后才能按“已收录”复用。
                if (stamp.Linked)
                    return !string.IsNullOrEmpty(stamp.TargetId) && FileImport.Identity(source) == stamp.TargetId;
                if (!verifyHash) return true;
                return !string.IsNullOrEmpty(stamp.Hash) && Digest(source, cancel) == stamp.Hash;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (!IsFileSystemError(ex) && !(ex is ArgumentException) && !(ex is NotSupportedException)
                    && !(ex is CryptographicException)) throw;
                return false;
            }
        }

        private static bool TargetMatches(string target, ImportFileStamp stamp)
        {
            try
            {
                FileInfo info = new FileInfo(target);
                return info.Exists && info.Length == stamp.Length && info.LastWriteTimeUtc.Ticks == stamp.Modified
                    && FileImport.Identity(target) == stamp.TargetId;
            }
            catch (Exception ex)
            {
                if (!IsFileSystemError(ex) && !(ex is ArgumentException) && !(ex is NotSupportedException)) throw;
                return false;
            }
        }

        private static string Digest(string path, CancellationToken cancel)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buffer = new byte[1 << 16];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, read, null, 0);
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                return Convert.ToBase64String(sha.Hash);
            }
        }

        internal static LocalMediaScanResult Scan(string category, IEnumerable<string> roots, IEnumerable<string> excludedRoots,
            IDictionary<string, LocalMediaImportTarget> importedTargets, Action<LocalMediaScanProgress> progress, CancellationToken cancel)
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

        private static void AddMedia(string source, IDictionary<string, LocalMediaImportTarget> importedTargets, LocalMediaScanResult result)
        {
            try
            {
                DateTime taken;
                if (!MediaInfo.TryGetTakenDate(source, out taken)) taken = File.GetLastWriteTime(source);
                long bytes = new FileInfo(source).Length;
                LocalMediaImportTarget target = null;
                bool reused = importedTargets != null && importedTargets.TryGetValue(source, out target);
                result.Add(taken.Year, reused ? target.Target : source, bytes, reused, reused && target.PendingCleanup);
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
            return ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException
                || ex is Win32Exception;
        }
    }
}
