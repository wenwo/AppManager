using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Threading;

namespace AppManager
{
    internal sealed class MusicScanProgress
    {
        public bool Probing;
        public int Index;
        public int Total;
        public int Accepted;
        public int Candidates;
        public string Path = "";
    }

    internal sealed class MusicScanRequest
    {
        public List<string> Roots;
        public List<string> ExcludedRoots = new List<string>();
        public List<string> IncludedPaths = new List<string>();
        public List<string> ExcludedPaths = new List<string>();
        public List<string> MusicRoots = LocalMusicScanner.UserMusicRoots();
        public Func<IMusicProbeSession> ProbeFactory;
        public bool ExtensionOnly;
        public int ProbeTimeoutMilliseconds = MpvAudioProbeSession.DefaultTimeoutMilliseconds;
        public int MaxAbandonedProbes = 3;
        public long MinimumFileBytes = 64 * 1024;
    }

    internal sealed class LocalMusicScanResult
    {
        public readonly List<string> Files = new List<string>();
        public readonly List<string> UnconfirmedFiles = new List<string>();
        public readonly List<string> ErrorSamples = new List<string>();
        internal readonly Dictionary<MusicFilterReason, int> ReasonCounts = new Dictionary<MusicFilterReason, int>();
        internal readonly Dictionary<MusicFilterReason, List<string>> ReasonSamples =
            new Dictionary<MusicFilterReason, List<string>>();
        public int DirectoriesScanned;
        public int DirectoriesSkipped;
        public int Errors;
        public int CloudFiles;
        public int AudioFilesFound;
        public int Candidates;
        public int Probes;
        public int AbandonedProbes;
        public long ProbeMilliseconds;
        public long TotalMilliseconds;

        internal int UnconfirmedCount { get { return UnconfirmedFiles.Count; } }

        internal void Count(MusicFilterReason reason, string path)
        {
            int count;
            ReasonCounts.TryGetValue(reason, out count);
            ReasonCounts[reason] = count + 1;
            if (string.IsNullOrEmpty(path)) return;
            List<string> samples;
            if (!ReasonSamples.TryGetValue(reason, out samples))
            {
                samples = new List<string>();
                ReasonSamples[reason] = samples;
            }
            if (samples.Count < 3) samples.Add(path);
        }

        internal int CountOf(MusicFilterReason reason)
        {
            int count;
            return ReasonCounts.TryGetValue(reason, out count) ? count : 0;
        }

        internal List<string> SamplesOf(MusicFilterReason reason)
        {
            List<string> samples;
            return ReasonSamples.TryGetValue(reason, out samples) ? samples : new List<string>();
        }

        internal void AddError(string message)
        {
            Errors++;
            if (ErrorSamples.Count < 5) ErrorSamples.Add(message);
        }
    }

    /// <summary>
    /// 本机音乐扫描：先只读遍历并做廉价过滤，再逐个用 libmpv 探测内容。
    /// 目录跳过按完整段名匹配，不对文件名或目录名做关键字猜测。
    /// </summary>
    internal static class LocalMusicScanner
    {
        private static readonly HashSet<string> AudioExtensions =
            new HashSet<string>(MediaRules.AudioExt, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> SkippedDirectoryNames =
            new HashSet<string>(new string[]
            {
                "$Recycle.Bin", "System Volume Information", "Recovery", "Config.Msi",
                "node_modules", ".git", "$WinREAgent", "Windows.old"
            }, StringComparer.OrdinalIgnoreCase);
        private static readonly string[] SystemRoots = BuildSystemRoots();

        private sealed class Candidate
        {
            public string Path;
        }

        internal static List<string> UserMusicRoots()
        {
            List<string> roots = new List<string>();
            AddExistingRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile)) AddExistingRoot(roots, Path.Combine(profile, "Music"));
            return roots;
        }

        private static void AddExistingRoot(List<string> roots, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (full.Length == 0) return;
                foreach (string root in roots)
                    if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase)) return;
                roots.Add(full);
            }
            catch { }
        }

        internal static List<string> LocalRoots()
        {
            return LocalRoots(null);
        }

        private static List<string> LocalRoots(List<string> failures)
        {
            List<string> roots = new List<string>();
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!drive.IsReady) continue;
                        if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) continue;
                        string root = drive.RootDirectory.FullName;
                        if (!string.IsNullOrEmpty(root)) roots.Add(root);
                    }
                    catch (IOException ex) { if (failures != null) failures.Add("无法读取磁盘 " + drive.Name + "：" + ex.Message); }
                    catch (UnauthorizedAccessException ex) { if (failures != null) failures.Add("无法读取磁盘 " + drive.Name + "：" + ex.Message); }
                    catch (SecurityException ex) { if (failures != null) failures.Add("无法读取磁盘 " + drive.Name + "：" + ex.Message); }
                }
            }
            catch (IOException ex) { if (failures != null) failures.Add("无法枚举本机磁盘：" + ex.Message); }
            catch (UnauthorizedAccessException ex) { if (failures != null) failures.Add("无法枚举本机磁盘：" + ex.Message); }
            catch (SecurityException ex) { if (failures != null) failures.Add("无法枚举本机磁盘：" + ex.Message); }
            return roots;
        }

        internal static LocalMusicScanResult Scan(MusicScanRequest request, CancellationToken cancel,
            Action<MusicScanProgress> progress)
        {
            if (request == null) throw new ArgumentNullException("request");
            LocalMusicScanResult result = new LocalMusicScanResult();
            Stopwatch total = Stopwatch.StartNew();
            try
            {
                List<string> rootErrors = new List<string>();
                List<string> roots = request.Roots ?? LocalRoots(rootErrors);
                for (int i = 0; i < rootErrors.Count; i++) result.AddError(rootErrors[i]);
                List<Candidate> candidates = Collect(request, roots, result, cancel, progress);
                result.Candidates = candidates.Count;
                Probe(request, candidates, result, cancel, progress);
                return result;
            }
            finally { result.TotalMilliseconds = total.ElapsedMilliseconds; }
        }

        /// <summary>兼容旧的仅收集入口：不探测，按扩展名返回通过廉价过滤的文件。</summary>
        internal static LocalMusicScanResult ScanRoots(IEnumerable<string> roots, CancellationToken cancel,
            Action<int, string> progress)
        {
            MusicScanRequest request = new MusicScanRequest
            {
                Roots = roots == null ? null : new List<string>(roots),
                ExtensionOnly = true
            };
            Action<MusicScanProgress> adapter = null;
            if (progress != null) adapter = delegate(MusicScanProgress value)
            {
                progress(value.Candidates, value.Path);
            };
            return Scan(request, cancel, adapter);
        }

        private static List<Candidate> Collect(MusicScanRequest request, List<string> roots,
            LocalMusicScanResult result, CancellationToken cancel, Action<MusicScanProgress> progress)
        {
            List<Candidate> candidates = new List<Candidate>();
            HashSet<string> seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> queuedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> included = NormalizeSet(request.IncludedPaths);
            HashSet<string> excluded = NormalizeSet(request.ExcludedPaths);
            List<string> excludedRoots = NormalizeList(request.ExcludedRoots);
            string dataDir = null;
            try { dataDir = Path.GetFullPath(Paths.DataDir); }
            catch { }
            Stack<string> pending = new Stack<string>();

            foreach (string value in roots)
            {
                cancel.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(value)) continue;
                try
                {
                    string root = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (root.Length == 2 && root[1] == ':') root += Path.DirectorySeparatorChar;
                    if (!Directory.Exists(root) || !queuedDirectories.Add(root)) continue;
                    if (IsSkippedDirectory(root, dataDir, excludedRoots))
                    {
                        result.DirectoriesSkipped++;
                        continue;
                    }
                    FileAttributes attributes = File.GetAttributes(root);
                    if ((attributes & FileAttributes.Directory) == 0
                        || FileImport.IsNameSurrogateReparsePoint(root, attributes))
                    {
                        result.DirectoriesSkipped++;
                        continue;
                    }
                    pending.Push(root);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { result.AddError(value + "：" + ex.Message); }
            }

            while (pending.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                string directory = pending.Pop();
                result.DirectoriesScanned++;
                try
                {
                    foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        cancel.ThrowIfCancellationRequested();
                        try
                        {
                            FileAttributes attributes = File.GetAttributes(entry);
                            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                            if (isDirectory && FileImport.IsNameSurrogateReparsePoint(entry, attributes)) continue;
                            if (isDirectory)
                            {
                                if (IsSkippedDirectory(entry, dataDir, excludedRoots))
                                {
                                    result.DirectoriesSkipped++;
                                    continue;
                                }
                                if (queuedDirectories.Add(entry)) pending.Push(entry);
                                continue;
                            }
                            if (!AudioExtensions.Contains(Path.GetExtension(entry))) continue;
                            if (!seenFiles.Add(entry)) continue;
                            result.AudioFilesFound++;
                            if (included.Contains(entry))
                            {
                                result.Count(MusicFilterReason.AlreadyIncluded, entry);
                                continue;
                            }
                            if (excluded.Contains(entry))
                            {
                                result.Count(MusicFilterReason.Excluded, entry);
                                continue;
                            }
                            if (MusicFileClassifier.IsCloudPlaceholder(attributes))
                            {
                                result.CloudFiles++;
                                result.Count(MusicFilterReason.CloudPlaceholder, entry);
                                continue;
                            }
                            long size;
                            try { size = new FileInfo(entry).Length; }
                            catch (Exception ex)
                            {
                                if (!IsFileSystemError(ex) && !(ex is ArgumentException) && !(ex is NotSupportedException)) throw;
                                result.AddError(entry + "：" + ex.Message);
                                continue;
                            }
                            if (size < request.MinimumFileBytes)
                            {
                                result.Count(MusicFilterReason.TooSmall, entry);
                                continue;
                            }
                            candidates.Add(new Candidate { Path = entry });
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            if (!IsFileSystemError(ex) && !(ex is ArgumentException) && !(ex is NotSupportedException)) throw;
                            result.AddError(entry + "：" + ex.Message);
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (!IsFileSystemError(ex) && !(ex is ArgumentException) && !(ex is NotSupportedException)) throw;
                    result.AddError(directory + "：" + ex.Message);
                }

                if (progress != null)
                {
                    try
                    {
                        progress(new MusicScanProgress
                        {
                            Probing = false,
                            Candidates = candidates.Count,
                            Path = directory
                        });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
            }
            return candidates;
        }

        private static void Probe(MusicScanRequest request, List<Candidate> candidates,
            LocalMusicScanResult result, CancellationToken cancel, Action<MusicScanProgress> progress)
        {
            if (request.ExtensionOnly || request.ProbeFactory == null)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    result.Files.Add(candidates[i].Path);
                    result.Count(MusicFilterReason.Accepted, candidates[i].Path);
                    ReportProbe(progress, i, candidates.Count, result, candidates[i].Path);
                }
                return;
            }

            IMusicProbeSession session = null;
            int maxAbandoned = Math.Max(1, request.MaxAbandonedProbes);
            try
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    string path = candidates[i].Path;
                    if (session == null)
                    {
                        try { session = request.ProbeFactory(); }
                        catch (Exception ex)
                        {
                            result.Count(MusicFilterReason.ProbeUnavailable, path);
                            result.UnconfirmedFiles.Add(path);
                            result.AddError("播放组件不可用：" + ex.Message);
                            AddRemainingUnconfirmed(candidates, i + 1, result);
                            break;
                        }
                        if (session == null)
                        {
                            result.Count(MusicFilterReason.ProbeUnavailable, path);
                            result.UnconfirmedFiles.Add(path);
                            result.AddError("播放组件不可用：探测会话创建失败");
                            AddRemainingUnconfirmed(candidates, i + 1, result);
                            break;
                        }
                    }

                    MusicProbeResult probe;
                    Stopwatch probeWatch = Stopwatch.StartNew();
                    try { probe = session.Probe(path, request.ProbeTimeoutMilliseconds, cancel); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        probe = MusicProbeResult.Failed(new MusicProbeInfo { Path = path, Failed = true, Error = ex.Message });
                    }
                    result.ProbeMilliseconds += probeWatch.ElapsedMilliseconds;
                    result.Probes++;

                    if (probe == null) probe = MusicProbeResult.Failed(new MusicProbeInfo { Path = path, Failed = true });
                    if (probe.Status == MusicProbeStatus.Cancelled)
                    {
                        cancel.ThrowIfCancellationRequested();
                        result.Count(MusicFilterReason.ProbeFailed, path);
                        ReportProbe(progress, i, candidates.Count, result, path);
                        continue;
                    }
                    if (probe.Status == MusicProbeStatus.TimedOut || probe.Status == MusicProbeStatus.Unavailable)
                    {
                        result.AbandonedProbes++;
                        MusicFilterReason reason = probe.Status == MusicProbeStatus.Unavailable
                            ? MusicFilterReason.ProbeUnavailable : MusicFilterReason.ProbeTimeout;
                        result.Count(reason, path);
                        result.UnconfirmedFiles.Add(path);
                        session.Dispose();
                        session = null;
                        ReportProbe(progress, i, candidates.Count, result, path);
                        bool stop = probe.Status == MusicProbeStatus.Unavailable || result.AbandonedProbes >= maxAbandoned;
                        if (stop)
                        {
                            AddRemainingUnconfirmed(candidates, i + 1, result);
                            break;
                        }
                        continue;
                    }
                    if (probe.Status == MusicProbeStatus.Failed)
                    {
                        result.Count(MusicFilterReason.ProbeFailed, path);
                        ReportProbe(progress, i, candidates.Count, result, path);
                        continue;
                    }

                    MusicFilterDecision decision = MusicFileClassifier.Classify(path, probe.Info, request.MusicRoots);
                    if (decision.Accepted)
                    {
                        result.Files.Add(path);
                        result.Count(MusicFilterReason.Accepted, path);
                    }
                    else if (decision.Reason == MusicFilterReason.Unconfirmed)
                    {
                        result.Count(decision.Reason, path);
                        result.UnconfirmedFiles.Add(path);
                    }
                    else result.Count(decision.Reason, path);
                    ReportProbe(progress, i, candidates.Count, result, path);
                }
            }
            finally
            {
                if (session != null) session.Dispose();
            }
        }

        private static void AddRemainingUnconfirmed(List<Candidate> candidates, int start,
            LocalMusicScanResult result)
        {
            for (int i = start; i < candidates.Count; i++)
            {
                result.Count(MusicFilterReason.NotProbed, candidates[i].Path);
                result.UnconfirmedFiles.Add(candidates[i].Path);
            }
        }

        private static void ReportProbe(Action<MusicScanProgress> progress, int index, int total,
            LocalMusicScanResult result, string path)
        {
            if (progress == null) return;
            try
            {
                progress(new MusicScanProgress
                {
                    Probing = true,
                    Index = index + 1,
                    Total = total,
                    Accepted = result.Files.Count,
                    Candidates = result.Candidates,
                    Path = path
                });
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        private static HashSet<string> NormalizeSet(IEnumerable<string> paths)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (paths == null) return result;
            foreach (string path in paths)
            {
                string full = NormalizePath(path);
                if (!string.IsNullOrEmpty(full)) result.Add(full);
            }
            return result;
        }

        private static List<string> NormalizeList(IEnumerable<string> paths)
        {
            List<string> result = new List<string>();
            if (paths == null) return result;
            foreach (string path in paths)
            {
                string full = NormalizePath(path);
                if (!string.IsNullOrEmpty(full)) result.Add(full);
            }
            return result;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { return null; }
        }

        private static bool IsSkippedDirectory(string path, string dataDir, List<string> excludedRoots)
        {
            string full = NormalizePath(path);
            if (string.IsNullOrEmpty(full)) return true;
            if (!string.IsNullOrEmpty(dataDir) && FileImport.Within(dataDir, full)) return true;
            for (int i = 0; i < excludedRoots.Count; i++)
                if (FileImport.Within(excludedRoots[i], full)) return true;
            for (int i = 0; i < SystemRoots.Length; i++)
                if (FileImport.Within(SystemRoots[i], full)) return true;

            string[] parts = full.Split(new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (SkippedDirectoryNames.Contains(part)) return true;
                if (string.Equals(part, "AppData", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(part, "ProgramData", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(part, "Program Files", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(part, "Program Files (x86)", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(part, "steamapps", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length
                    && (string.Equals(parts[i + 1], "common", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(parts[i + 1], "workshop", StringComparison.OrdinalIgnoreCase))) return true;
            }
            return false;
        }

        private static string[] BuildSystemRoots()
        {
            List<string> roots = new List<string>();
            AddSystemRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            AddSystemRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            AddSystemRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            AddSystemRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
            string system = Environment.SystemDirectory;
            if (!string.IsNullOrEmpty(system)) AddSystemRoot(roots, Path.GetDirectoryName(system));
            return roots.ToArray();
        }

        private static void AddSystemRoot(List<string> roots, string path)
        {
            string full = NormalizePath(path);
            if (!string.IsNullOrEmpty(full)) roots.Add(full);
        }

        private static bool IsFileSystemError(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException;
        }
    }
}