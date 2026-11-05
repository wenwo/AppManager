using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace AppManager
{
    internal sealed class LocalPhotoScanResult
    {
        internal readonly SortedDictionary<int, List<string>> Years = new SortedDictionary<int, List<string>>();
        internal int UnreadableDirectories;
        internal int ReadFailures;
        internal int Reused;
        internal long Bytes;

        internal int Count { get { return Years.Values.Sum(paths => paths.Count); } }

        internal void Add(int year, string path, long bytes, bool reused)
        {
            List<string> paths;
            if (!Years.TryGetValue(year, out paths)) Years[year] = paths = new List<string>();
            paths.Add(path);
            Bytes += bytes;
            if (reused) Reused++;
        }
    }

    internal static class LocalPhotoScanner
    {
        private const int PhotosPerSecond = 100;

        internal static List<string> FixedRoots()
        {
            List<string> roots = new List<string>();
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (drive.IsReady && drive.DriveType == DriveType.Fixed) roots.Add(drive.RootDirectory.FullName);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return roots;
        }

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
                catch (Exception ex) { Paths.LogError(new IOException("读取照片导入记录失败：" + file, ex)); }
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

        internal static LocalPhotoScanResult Scan(IEnumerable<string> roots, IEnumerable<string> excludedRoots,
            IDictionary<string, string> importedTargets, Action<int, string> progress, CancellationToken cancel)
        {
            LocalPhotoScanResult result = new LocalPhotoScanResult();
            List<string> exclusions = (excludedRoots ?? Enumerable.Empty<string>()).Where(path => !string.IsNullOrEmpty(path))
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots ?? Enumerable.Empty<string>())
            {
                cancel.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(root) || IsExcluded(root, exclusions)) continue;
                Stack<string> pending = new Stack<string>();
                pending.Push(root);
                while (pending.Count > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    string directory = pending.Pop();
                    if (IsExcluded(directory, exclusions)) continue;
                    IEnumerable<string> entries;
                    try { entries = Directory.EnumerateFileSystemEntries(directory); }
                    catch (Exception ex)
                    {
                        if (!IsFileSystemError(ex)) throw;
                        result.UnreadableDirectories++;
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
                                continue;
                            }
                            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                string name = Path.GetFileName(path.TrimEnd('\\', '/'));
                                if (!IsDeletedOrSystemDirectory(name) && !IsExcluded(path, exclusions)) pending.Push(path);
                                continue;
                            }
                            if (!MediaRules.FitsCategory("照片", path, false) || !seen.Add(path)) continue;
                            AddPhoto(path, importedTargets, result);
                            Pace(cancel);
                            if (progress != null) progress(result.Count, path);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!IsFileSystemError(ex)) throw;
                        result.UnreadableDirectories++;
                    }
                }
            }
            return result;
        }

        private static void Pace(CancellationToken cancel)
        {
            if (cancel.WaitHandle.WaitOne(1000 / PhotosPerSecond)) cancel.ThrowIfCancellationRequested();
        }

        private static void AddPhoto(string source, IDictionary<string, string> importedTargets, LocalPhotoScanResult result)
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
