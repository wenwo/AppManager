using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Threading;

namespace AppManager
{
    internal sealed class LocalMusicScanResult
    {
        public readonly List<string> Files = new List<string>();
        public int DirectoriesScanned;
        public int DirectoriesSkipped;
        public int Errors;
        public int CloudFiles;
        public readonly List<string> ErrorSamples = new List<string>();
    }

    /// <summary>
    /// 本机音乐文件扫描：只遍历已就绪的本地固定/可移动磁盘，跳过链接/联接点目录、
    /// 回收站等系统目录，以及 AppManager 自己的数据目录。扫描只读，不改动任何文件。
    /// </summary>
    internal static class LocalMusicScanner
    {
        private static readonly HashSet<string> AudioExtensions =
            new HashSet<string>(MediaRules.AudioExt, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> SkippedDirectoryNames =
            new HashSet<string>(new string[]
            {
                "$Recycle.Bin", "System Volume Information", "Recovery", "Config.Msi"
            }, StringComparer.OrdinalIgnoreCase);

        internal static LocalMusicScanResult Scan(CancellationToken cancel, Action<int, string> progress)
        {
            List<string> failures = new List<string>();
            LocalMusicScanResult result = ScanRoots(LocalRoots(failures), cancel, progress);
            foreach (string failure in failures) AddError(result, failure);
            return result;
        }

        /// <summary>返回本机固定磁盘和已连接可移动磁盘的根目录；网络、光驱和未就绪卷不参与扫描。</summary>
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
                    catch (IOException ex) { AddRootError(failures, drive, ex); }
                    catch (UnauthorizedAccessException ex) { AddRootError(failures, drive, ex); }
                    catch (SecurityException ex) { AddRootError(failures, drive, ex); }
                }
            }
            catch (IOException ex) { if (failures != null) failures.Add("无法枚举本机磁盘：" + ex.Message); }
            catch (UnauthorizedAccessException ex) { if (failures != null) failures.Add("无法枚举本机磁盘：" + ex.Message); }
            catch (SecurityException ex) { if (failures != null) failures.Add("无法枚举本机磁盘：" + ex.Message); }
            return roots;
        }

        private static void AddRootError(List<string> failures, DriveInfo drive, Exception error)
        {
            if (failures != null) failures.Add("无法读取磁盘 " + drive.Name + "：" + error.Message);
        }

        internal static LocalMusicScanResult ScanRoots(IEnumerable<string> roots, CancellationToken cancel,
            Action<int, string> progress)
        {
            if (roots == null) throw new ArgumentNullException("roots");
            LocalMusicScanResult result = new LocalMusicScanResult();
            HashSet<string> seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> queuedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Stack<string> pending = new Stack<string>();
            string dataDir = Path.GetFullPath(Paths.DataDir);

            foreach (string value in roots)
            {
                cancel.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(value)) continue;
                try
                {
                    string root = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (root.Length == 2 && root[1] == ':') root += Path.DirectorySeparatorChar;
                    if (!Directory.Exists(root) || !queuedDirectories.Add(root)) continue;
                    if (IsSkippedDirectory(root, dataDir))
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
                catch (Exception ex) { AddError(result, value, ex); }
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
                                if (IsSkippedDirectory(entry, dataDir))
                                {
                                    result.DirectoriesSkipped++;
                                    continue;
                                }
                                if (queuedDirectories.Add(entry)) pending.Push(entry);
                                continue;
                            }
                            if (!AudioExtensions.Contains(Path.GetExtension(entry))) continue;
                            if (seenFiles.Add(entry))
                            {
                                result.Files.Add(entry);
                                if ((attributes & FileAttributes.Offline) != 0) result.CloudFiles++;
                            }
                        }
                        catch (FileNotFoundException ex) { AddError(result, entry, ex); }
                        catch (DirectoryNotFoundException ex) { AddError(result, entry, ex); }
                        catch (UnauthorizedAccessException ex) { AddError(result, entry, ex); }
                        catch (IOException ex) { AddError(result, entry, ex); }
                        catch (SecurityException ex) { AddError(result, entry, ex); }
                    }
                }
                catch (DirectoryNotFoundException ex) { AddError(result, directory, ex); }
                catch (UnauthorizedAccessException ex) { AddError(result, directory, ex); }
                catch (IOException ex) { AddError(result, directory, ex); }
                catch (SecurityException ex) { AddError(result, directory, ex); }

                if (progress != null && (result.DirectoriesScanned % 64 == 0 || pending.Count == 0))
                {
                    try { progress(result.Files.Count, directory); }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
            }
            return result;
        }

        private static void AddError(LocalMusicScanResult result, string path, Exception error)
        {
            AddError(result, path + "：" + error.Message);
        }

        private static void AddError(LocalMusicScanResult result, string message)
        {
            result.Errors++;
            if (result.ErrorSamples.Count < 5) result.ErrorSamples.Add(message);
        }

        private static bool IsSkippedDirectory(string path, string dataDir)
        {
            string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (SkippedDirectoryNames.Contains(name)) return true;
            return !string.IsNullOrEmpty(dataDir) && FileImport.Within(dataDir, path);
        }
    }
}
