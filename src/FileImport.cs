using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.Win32.SafeHandles;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace AppManager
{
    public enum ImportOperation { Copy, Move, Auto }

    public sealed class ImportFileStamp
    {
        public string Relative, Hash, TargetId, DetachedPath;
        public long Length, Modified;
        public List<ImportStreamStamp> Streams = new List<ImportStreamStamp>();
        // Linked：同卷移动时以硬链接落地，入库路径与源路径是同一文件，无哈希。
        public bool Deleted, Linked;
    }

    public sealed class ImportStreamStamp
    {
        public string Name, Hash;
        public long Length;
    }

    public sealed class ImportDirectoryStamp
    {
        public string Relative, TargetId;
        public List<ImportStreamStamp> Streams = new List<ImportStreamStamp>();
    }

    public sealed class ImportItem
    {
        public string Source, Target, Error, TargetId, StagingId, State = FileImport.Waiting;
        public bool Folder, Reference;
        public List<string> Directories = new List<string>();
        public List<ImportFileStamp> Files = new List<ImportFileStamp>();
        public List<ImportDirectoryStamp> TargetDirectories = new List<ImportDirectoryStamp>();
    }

    public sealed class ImportBatch
    {
        public string Id = Guid.NewGuid().ToString("N"), Category, Collection, Error;
        public bool Resource, Committed, DeleteAuthorized, Finished;
        public ImportOperation Operation;
        public List<ImportItem> Items = new List<ImportItem>();
        [XmlIgnore] public string JournalDirectory;
        [XmlIgnore] public uint ClipboardSequence;
        internal int SavedAt;
    }

    // 文件先完整落地，数据库提交成功后才允许删源；提交失败撤回入库副本。
    // 日志用于中断恢复：启动时清理临时落点、撤回未入库副本，不自动重试删源。
    internal static class FileImport
    {
        internal const string Waiting = "等待", Copied = "已复制", Linked = "已收录", Skipped = "已跳过", Failed = "失败",
            Moved = "已移动", DeleteFailed = "源文件删除失败", RolledBack = "已撤回";

        internal static DragDropEffects ResolveEffect(bool sameVolume, Keys modifiers, DragDropEffects allowed)
        {
            DragDropEffects wanted = (modifiers & Keys.Control) != 0 ? DragDropEffects.Copy
                : (modifiers & Keys.Shift) != 0 ? DragDropEffects.Move
                : sameVolume ? DragDropEffects.Move : DragDropEffects.Copy;
            if ((allowed & wanted) != 0) return wanted;
            // 不把复制意图升级为移动；仅允许 Move 的来源需要用户明确请求移动。
            return (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
        }

        internal static ImportOperation ClipboardOperation(object value)
        {
            MemoryStream stream = value as MemoryStream;
            byte[] bytes = stream != null ? stream.ToArray() : value as byte[];
            return bytes != null && bytes.Length >= 4 && BitConverter.ToUInt32(bytes, 0) == 2
                ? ImportOperation.Move : ImportOperation.Copy;
        }

        internal static bool Within(string parent, string child)
        {
            parent = Path.GetFullPath(parent).TrimEnd('\\', '/');
            child = Path.GetFullPath(child).TrimEnd('\\', '/');
            return string.Equals(parent, child, StringComparison.OrdinalIgnoreCase)
                || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        // ===== Win32 =====

        private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, DeleteAccess = 0x00010000, ReadAttributes = 0x80;
        private const uint ShareRead = 1, ShareAll = 7, CreateNewFile = 1, OpenExisting = 3;
        private const uint SequentialScan = 0x08000000, BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
        private const uint NameSurrogate = 0x20000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct FileIdInfo { public ulong Volume, IdLow, IdHigh; }

        [StructLayout(LayoutKind.Sequential)]
        private struct AttributeTagInfo { public uint Attributes, ReparseTag; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleInfo
        {
            public uint Attributes;
            public FILETIME Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StreamData
        {
            public long Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumePathName(string path, StringBuilder volume, int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeNameForVolumeMountPoint(string path, StringBuilder volume, int size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string link, string existing, IntPtr security);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref int info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileIdInfo info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out AttributeTagInfo info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleInfo info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstStreamW(string path, int level, out StreamData data, int flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool FindNextStreamW(IntPtr find, out StreamData data);
        [DllImport("kernel32.dll")]
        private static extern bool FindClose(IntPtr find);

        private static SafeFileHandle Open(string path, uint access, uint share, uint flags)
        {
            SafeFileHandle handle = CreateFile(path, access, share, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, new Win32Exception(error).Message + "：" + path);
        }

        internal static string Volume(string path)
        {
            path = Path.GetFullPath(path);
            while (!File.Exists(path) && !Directory.Exists(path))
            {
                string parent = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(parent)) break;
                path = parent;
            }
            StringBuilder root = new StringBuilder(1024), id = new StringBuilder(1024);
            if (GetVolumePathName(path, root, root.Capacity))
            {
                if (GetVolumeNameForVolumeMountPoint(root.ToString(), id, id.Capacity)) return id.ToString();
                if (root.ToString().StartsWith(@"\\")) return root.ToString();
            }
            return null; // 无法确认所在卷时默认复制。
        }

        internal static bool SameVolume(string source, string target)
        {
            string a = Volume(source), b = Volume(target);
            return a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameFile(SafeFileHandle a, SafeFileHandle b)
        {
            FileIdInfo x, y;
            if (GetFileInformationByHandleEx(a, 18, out x, 24) && GetFileInformationByHandleEx(b, 18, out y, 24))
                return x.Volume == y.Volume && x.IdLow == y.IdLow && x.IdHigh == y.IdHigh;
            ByHandleInfo p, q;
            if (!GetFileInformationByHandle(a, out p) || !GetFileInformationByHandle(b, out q))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return p.Volume == q.Volume && p.IndexHigh == q.IndexHigh && p.IndexLow == q.IndexLow;
        }

        private static string Identity(SafeFileHandle handle)
        {
            FileIdInfo id;
            if (GetFileInformationByHandleEx(handle, 18, out id, 24))
                return id.Volume.ToString("X") + ":" + id.IdHigh.ToString("X") + ":" + id.IdLow.ToString("X");
            ByHandleInfo info;
            if (!GetFileInformationByHandle(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return info.Volume.ToString("X") + ":" + info.IndexHigh.ToString("X") + ":" + info.IndexLow.ToString("X");
        }

        internal static string Identity(string path)
        {
            using (SafeFileHandle handle = Open(path, ReadAttributes, ShareAll, BackupSemantics | OpenReparsePoint)) return Identity(handle);
        }

        private static void RequireIdentity(string path, string expected)
        {
            EnsureNoLinks(path);
            if (string.IsNullOrEmpty(expected) || Identity(path) != expected) throw new IOException("无法确认文件属于本次导入，已保留：" + path);
        }

        private static bool Matches(SafeFileHandle handle, ImportFileStamp stamp)
        {
            ByHandleInfo info;
            if (!GetFileInformationByHandle(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            long length = ((long)info.SizeHigh << 32) | info.SizeLow;
            long write = ((long)(uint)info.Write.dwHighDateTime << 32) | (uint)info.Write.dwLowDateTime;
            return length == stamp.Length && DateTime.FromFileTimeUtc(write).Ticks == stamp.Modified;
        }

        // 句柄删除：忽略只读属性（Win10 1809+）；旧系统或不支持的文件系统回退普通删除标记。
        private static void MarkDelete(SafeFileHandle handle)
        {
            int flags = 0x11;
            if (SetFileInformationByHandle(handle, 21, ref flags, 4)) return;
            int delete = 1;
            if (!SetFileInformationByHandle(handle, 4, ref delete, 4)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private static void DeleteFile(string path)
        {
            using (SafeFileHandle handle = Open(path, DeleteAccess, ShareAll, OpenReparsePoint)) MarkDelete(handle);
        }

        // ===== 链接检查 =====

        // 云文件（OneDrive 按需文件）、去重等数据型重解析点按普通文件处理；符号链接、联接点等名称代理一律拒绝。
        private static void CheckEntry(string path, FileAttributes attributes)
        {
            if ((attributes & FileAttributes.ReparsePoint) == 0) return;
            AttributeTagInfo info;
            using (SafeFileHandle handle = Open(path, ReadAttributes, ShareAll, OpenReparsePoint | BackupSemantics))
                if (!GetFileInformationByHandleEx(handle, 9, out info, 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.ReparseTag & NameSurrogate) != 0) throw new IOException("路径包含链接或联接点，操作已取消：" + path);
        }

        internal static void EnsureNoLinks(string path)
        {
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                try { CheckEntry(current, File.GetAttributes(current)); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                current = Path.GetDirectoryName(current);
            }
        }

        // ===== 日志 =====

        internal static void Save(ImportBatch batch)
        {
            Directory.CreateDirectory(batch.JournalDirectory);
            string path = Path.Combine(batch.JournalDirectory, batch.Id + ".xml"), temp = path + ".tmp";
            using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                new XmlSerializer(typeof(ImportBatch)).Serialize(stream, batch);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            batch.SavedAt = Environment.TickCount;
        }

        // 逐项进度最多每秒落盘一次；阶段边界（开始、提交前后、结束）始终落盘，恢复不依赖逐项状态。
        private static void SaveSoon(ImportBatch batch)
        {
            if (unchecked(Environment.TickCount - batch.SavedAt) >= 1000) Save(batch);
        }

        private static string Marker(ImportBatch batch) { return Path.Combine(batch.JournalDirectory, batch.Id + ".pending"); }

        internal static ImportBatch Load(string path)
        {
            using (XmlReader reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            {
                ImportBatch batch = (ImportBatch)new XmlSerializer(typeof(ImportBatch)).Deserialize(reader);
                if (batch.Id != Path.GetFileNameWithoutExtension(path)) throw new IOException("导入记录标识不匹配。");
                batch.JournalDirectory = Path.GetDirectoryName(path);
                return batch;
            }
        }

        internal static void Finish(ImportBatch batch)
        {
            batch.Finished = true;
            Save(batch);
            string complete = Path.Combine(batch.JournalDirectory, batch.Id + ".complete");
            bool settled = batch.Committed && string.IsNullOrEmpty(batch.Error) && batch.Items.All(i => string.IsNullOrEmpty(i.Error)
                && (i.State == Moved || i.State == Linked || i.State == Skipped || (batch.Operation == ImportOperation.Copy && i.State == Copied)));
            if (settled) File.WriteAllText(complete, ""); else DropMarker(complete);
            try { File.Delete(Marker(batch)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        internal static void Prune(string directory, int keep)
        {
            if (!Directory.Exists(directory)) return;
            DirectoryInfo dir = new DirectoryInfo(directory);
            IEnumerable<FileInfo> stale = dir.GetFiles("*.xml").OrderByDescending(f => f.LastWriteTimeUtc).Skip(keep)
                .Where(f => !File.Exists(Path.ChangeExtension(f.FullName, ".pending")) && File.Exists(Path.ChangeExtension(f.FullName, ".complete")))
                .Concat(dir.GetFiles("*.tmp").Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)));
            foreach (FileInfo file in stale)
            {
                try { file.Delete(); if (file.Extension == ".xml") DropMarker(Path.ChangeExtension(file.FullName, ".complete")); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        // ===== 快照与校验 =====

        private static string Digest(Stream stream)
        {
            return Digest(stream, CancellationToken.None);
        }

        private static string Digest(Stream stream, CancellationToken cancel)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buffer = new byte[1 << 16]; int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                { cancel.ThrowIfCancellationRequested(); sha.TransformBlock(buffer, 0, read, null, 0); }
                sha.TransformFinalBlock(buffer, 0, 0);
                return Convert.ToBase64String(sha.Hash);
            }
        }

        private static string At(ImportItem item, string root, string relative)
        {
            string path = item.Folder ? Path.GetFullPath(Path.Combine(root, relative)) : Path.GetFullPath(root);
            if (!Within(root, path)) throw new IOException("导入记录中的路径越界。");
            return path;
        }

        private static void Snapshot(ImportItem item, FileSystemInfo entry, string relative, int depth, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            if (depth > 64 || item.Files.Count + item.Directories.Count >= 200000) throw new IOException("目录层级或条目数量超过导入上限。");
            CheckEntry(entry.FullName, entry.Attributes);
            DirectoryInfo dir = entry as DirectoryInfo;
            if (dir != null)
            {
                item.Directories.Add(relative);
                foreach (FileSystemInfo child in dir.EnumerateFileSystemInfos())
                    Snapshot(item, child, Path.Combine(relative, child.Name), depth + 1, cancel);
            }
            else
            {
                FileInfo file = (FileInfo)entry;
                item.Files.Add(new ImportFileStamp { Relative = relative, Length = file.Length, Modified = file.LastWriteTimeUtc.Ticks });
            }
        }

        private static void Collect(DirectoryInfo dir, string relative, List<string> files, List<string> dirs, int depth)
        {
            if (depth > 64 || files.Count + dirs.Count >= 200000) throw new IOException("目录层级或条目数量超过上限。");
            CheckEntry(dir.FullName, dir.Attributes);
            dirs.Add(relative);
            foreach (FileSystemInfo child in dir.EnumerateFileSystemInfos())
            {
                string rel = Path.Combine(relative, child.Name);
                DirectoryInfo sub = child as DirectoryInfo;
                if (sub != null) Collect(sub, rel, files, dirs, depth + 1);
                else { CheckEntry(child.FullName, child.Attributes); files.Add(rel); }
            }
        }

        // 删除前的廉价预检：长度/时间不符即停止；内容哈希在持有删除句柄时再比对。
        private static void CheckFile(ImportItem item, string root, ImportFileStamp stamp, bool source)
        {
            string path = At(item, root, stamp.Relative);
            EnsureNoLinks(path);
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length != stamp.Length || ((source || stamp.Linked) && info.LastWriteTimeUtc.Ticks != stamp.Modified))
                throw new IOException("文件已变化，保留源文件：" + path);
        }

        private static void CheckTree(ImportItem item, bool allowDeleted)
        {
            if (!item.Folder) return;
            EnsureNoLinks(item.Source);
            HashSet<string> files = new HashSet<string>(item.Files.Where(f => !allowDeleted || !f.Deleted).Select(f => f.Relative), StringComparer.OrdinalIgnoreCase);
            HashSet<string> dirs = new HashSet<string>(item.Directories, StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(item.Source))
            {
                if (allowDeleted && files.Count == 0) return;
                throw new IOException("源目录不存在。");
            }
            CheckTreeEntries(new DirectoryInfo(item.Source), "", files, dirs);
            if (files.Count != 0) throw new IOException("源目录文件列表已变化，停止删除。");
        }

        private static void CheckTreeEntries(DirectoryInfo dir, string relative, HashSet<string> files, HashSet<string> dirs)
        {
            CheckEntry(dir.FullName, dir.Attributes);
            if (!dirs.Contains(relative)) throw new IOException("源目录新增了子目录，停止删除。");
            foreach (FileSystemInfo child in dir.EnumerateFileSystemInfos())
            {
                string rel = Path.Combine(relative, child.Name);
                DirectoryInfo sub = child as DirectoryInfo;
                if (sub != null) CheckTreeEntries(sub, rel, files, dirs);
                else
                {
                    CheckEntry(child.FullName, child.Attributes);
                    if (!files.Remove(rel)) throw new IOException("源目录文件列表已变化，停止删除。");
                }
            }
        }

        // ===== 落地 =====

        // 落地单个文件，返回后续文件是否继续尝试硬链接。
        private static bool Place(string source, string target, ImportFileStamp stamp, bool link, CancellationToken cancel)
        {
            // 共享读打开：其他进程正在写入的文件直接失败，避免搬走写了一半的数据。
            using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
            {
                if (input.Length != stamp.Length || File.GetLastWriteTimeUtc(source).Ticks != stamp.Modified) throw new IOException("复制前源文件已变化。");
                if (link)
                {
                    // 同卷移动：硬链接不复制数据、不占额外空间，且保留属性、ACL 与备用数据流。
                    if (CreateHardLink(target, source, IntPtr.Zero)) { stamp.Linked = true; stamp.TargetId = Identity(target); return true; }
                    link = false; // 跨卷、FAT/exFAT、链接数上限等：本项其余文件改为复制。
                }
                using (FileStream output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                using (SHA256 sha = SHA256.Create())
                {
                    stamp.TargetId = Identity(output.SafeFileHandle);
                    // 边复制边计算哈希，源文件只读一遍。
                    byte[] buffer = new byte[1 << 16];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        sha.TransformBlock(buffer, 0, read, null, 0);
                    }
                    sha.TransformFinalBlock(buffer, 0, 0);
                    output.Flush(true);
                    if (output.Length != stamp.Length) throw new IOException("目标文件长度校验失败。");
                    stamp.Hash = Convert.ToBase64String(sha.Hash);
                }
            }
            stamp.Streams = CopyStreams(source, target, cancel);
            FileInfo from = new FileInfo(source);
            File.SetCreationTimeUtc(target, from.CreationTimeUtc);
            File.SetLastWriteTimeUtc(target, new DateTime(stamp.Modified, DateTimeKind.Utc));
            FileAttributes keep = from.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System
                | FileAttributes.Archive | FileAttributes.NotContentIndexed);
            if (keep != 0) File.SetAttributes(target, keep);
            return link;
        }

        // 流枚举失败与流复制失败均向上传递，不能把缺失的数据作为成功移动。
        private static List<ImportStreamStamp> Streams(string path)
        {
            List<ImportStreamStamp> result = new List<ImportStreamStamp>();
            StreamData data;
            IntPtr find = FindFirstStreamW(path, 0, out data, 0);
            if (find == new IntPtr(-1))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 38 || error == 1 || error == 50) return result;
                throw new Win32Exception(error, "无法枚举文件数据流：" + path);
            }
            try
            {
                do
                {
                    if (data.Name != "::$DATA") result.Add(new ImportStreamStamp { Name = data.Name, Length = data.Size });
                } while (FindNextStreamW(find, out data));
                int error = Marshal.GetLastWin32Error();
                if (error != 38) throw new Win32Exception(error, "数据流枚举未完成：" + path);
            }
            finally { FindClose(find); }
            return result;
        }

        private static List<ImportStreamStamp> CopyStreams(string source, string target, CancellationToken cancel)
        {
            List<ImportStreamStamp> streams = Streams(source);
            if (streams.Count == 0) return streams;
            byte[] buffer = streams.Count == 0 ? null : new byte[1 << 16];
            foreach (ImportStreamStamp stream in streams)
            {
                cancel.ThrowIfCancellationRequested();
                using (FileStream input = new FileStream(Open(source + stream.Name, GenericRead, ShareRead | 4, SequentialScan), FileAccess.Read))
                using (SafeFileHandle handle = CreateFile(target + stream.Name, GenericWrite, 0, IntPtr.Zero, CreateNewFile, 0, IntPtr.Zero))
                {
                    if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保留数据流：" + stream.Name);
                    using (FileStream output = new FileStream(handle, FileAccess.Write))
                    using (SHA256 sha = SHA256.Create())
                    {
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            cancel.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); sha.TransformBlock(buffer, 0, read, null, 0);
                        }
                        sha.TransformFinalBlock(buffer, 0, 0); output.Flush(true);
                        if (output.Length != stream.Length) throw new IOException("数据流长度已变化：" + stream.Name);
                        stream.Hash = Convert.ToBase64String(sha.Hash);
                    }
                }
            }
            return streams;
        }

        private static List<FileStream> HoldStreams(string source, string target, List<ImportStreamStamp> expected, CancellationToken cancel)
        {
            List<FileStream> held = new List<FileStream>();
            try
            {
                List<ImportStreamStamp> left = Streams(source), right = Streams(target);
                if (left.Count != right.Count || (expected != null && expected.Count != left.Count)) throw new IOException("文件数据流清单已变化，停止删除。");
                foreach (ImportStreamStamp stream in left)
                {
                    cancel.ThrowIfCancellationRequested();
                    ImportStreamStamp other = right.Find(s => s.Name.Equals(stream.Name, StringComparison.OrdinalIgnoreCase));
                    ImportStreamStamp saved = expected == null ? null : expected.Find(s => s.Name.Equals(stream.Name, StringComparison.OrdinalIgnoreCase));
                    if (other == null || stream.Length != other.Length || (expected != null && (saved == null || saved.Length != stream.Length)))
                        throw new IOException("文件数据流已变化，停止删除。");
                    FileStream a = new FileStream(Open(source + stream.Name, GenericRead, ShareRead | 4, SequentialScan), FileAccess.Read); held.Add(a);
                    FileStream b = new FileStream(Open(target + stream.Name, GenericRead, ShareRead | 4, SequentialScan), FileAccess.Read); held.Add(b);
                    string hash = Digest(a, cancel);
                    if (hash != Digest(b, cancel) || (saved != null && hash != saved.Hash)) throw new IOException("文件数据流校验失败，停止删除。");
                }
                return held;
            }
            catch { foreach (FileStream stream in held) stream.Dispose(); throw; }
        }

        private static void CopyDirectoryInfo(ImportItem item, string staging, CancellationToken cancel)
        {
            foreach (string dir in item.Directories.OrderByDescending(d => d.Length))
            {
                cancel.ThrowIfCancellationRequested();
                DirectoryInfo from = new DirectoryInfo(At(item, item.Source, dir)), to = new DirectoryInfo(At(item, staging, dir));
                item.TargetDirectories.Add(new ImportDirectoryStamp { Relative = dir, TargetId = Identity(to.FullName),
                    Streams = CopyStreams(from.FullName, to.FullName, cancel) });
                to.CreationTimeUtc = from.CreationTimeUtc;
                to.LastWriteTimeUtc = from.LastWriteTimeUtc;
                FileAttributes keep = from.Attributes & (FileAttributes.Hidden | FileAttributes.System);
                if (keep != 0) to.Attributes |= keep;
            }
        }

        // 临时落点归本批所有，整体删除（不触碰源文件；硬链接只移除名字）。
        private static void RemoveStaging(string staging)
        {
            try
            {
                if (File.Exists(staging)) { DeleteFile(staging); return; }
                if (!Directory.Exists(staging)) return;
                List<string> files = new List<string>(), dirs = new List<string>();
                Collect(new DirectoryInfo(staging), "", files, dirs, 0);
                foreach (string file in files) DeleteFile(Path.Combine(staging, file));
                foreach (string dir in dirs.OrderByDescending(d => d.Length)) Directory.Delete(Path.Combine(staging, dir), false);
            }
            catch { /* 本次临时落地残留不影响保源，下次启动恢复时再清理。 */ }
        }

        private static void CopyAll(ImportBatch batch, Action<int, string> progress, CancellationToken cancel)
        {
            for (int i = 0; i < batch.Items.Count; i++)
            {
                ImportItem item = batch.Items[i];
                if (progress != null) progress(i, Path.GetFileName(item.Source));
                if (item.State == Failed || item.State == Skipped) continue;
                string staging = null;
                bool stagingOwned = false;
                try
                {
                    cancel.ThrowIfCancellationRequested();
                    if (!File.Exists(item.Source) && !Directory.Exists(item.Source)) throw new IOException("源文件不存在。");
                    if (item.Reference) { item.Target = item.Source; item.State = Linked; SaveSoon(batch); continue; }
                    EnsureNoLinks(item.Source);
                    if (item.Folder && Within(item.Source, item.Target)) throw new IOException("不能将文件夹导入自身内部。");
                    EnsureNoLinks(item.Target);
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Target));
                    item.Files.Clear(); item.Directories.Clear(); item.TargetDirectories.Clear();
                    Snapshot(item, item.Folder ? (FileSystemInfo)new DirectoryInfo(item.Source) : new FileInfo(item.Source), "", 0, cancel);
                    staging = item.Target + ".import-" + batch.Id;
                    if (File.Exists(staging) || Directory.Exists(staging)) throw new IOException("临时目标已存在。");
                    stagingOwned = true;
                    if (item.Folder)
                    {
                        foreach (string dir in item.Directories) Directory.CreateDirectory(At(item, staging, dir));
                        item.StagingId = Identity(staging);
                    }
                    bool link = batch.Operation == ImportOperation.Move;
                    foreach (ImportFileStamp stamp in item.Files)
                    {
                        cancel.ThrowIfCancellationRequested();
                        link = Place(At(item, item.Source, stamp.Relative), At(item, staging, stamp.Relative), stamp, link, cancel);
                    }
                    if (!item.Folder) item.StagingId = item.Files[0].TargetId;
                    if (item.Folder) CopyDirectoryInfo(item, staging, cancel);
                    CheckTree(item, false);
                    // CreateNew/Move 均不覆盖；其他进程占用目标名时，此项失败并保源。
                    if (item.Folder) Directory.Move(staging, item.Target); else File.Move(staging, item.Target);
                    item.TargetId = item.StagingId;
                    staging = null; item.State = Copied;
                }
                catch (OperationCanceledException) { item.State = Failed; item.Error = "已取消"; }
                catch (Exception ex) { item.State = Failed; item.Error = ex.Message; }
                finally
                {
                    if (staging != null && stagingOwned) RemoveStaging(staging);
                }
                SaveSoon(batch);
            }
            Save(batch); // 提交前必须持久化全部校验信息。
        }

        // ===== 删源 =====

        private static void DeleteVerified(ImportItem item, ImportFileStamp stamp, CancellationToken cancel)
        {
            string source = At(item, item.Source, stamp.Relative), target = At(item, item.Target, stamp.Relative);
            EnsureNoLinks(source); EnsureNoLinks(target);
            if (stamp.Linked)
            {
                // 硬链接：两个路径仍是同一文件时，删除源路径只移除一个名字，数据保留在入库路径。
                using (SafeFileHandle original = Open(source, GenericRead | DeleteAccess, ShareRead, 0))
                using (SafeFileHandle copy = Open(target, ReadAttributes, ShareRead, 0))
                {
                    if (Identity(copy) != stamp.TargetId || !SameFile(original, copy) || !Matches(original, stamp)) throw new IOException("源文件或入库文件已变化，停止删除。");
                    MarkDelete(original);
                }
                return;
            }
            // 同一已校验句柄删除，禁止并发写入/替换；不按旧路径盲删文件。
            using (FileStream copy = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
            using (SafeFileHandle handle = Open(source, GenericRead | DeleteAccess, ShareRead, SequentialScan))
            using (FileStream original = new FileStream(handle, FileAccess.Read, 1 << 16))
            {
                if (original.Length != stamp.Length || copy.Length != stamp.Length || File.GetLastWriteTimeUtc(source).Ticks != stamp.Modified
                    || Identity(copy.SafeFileHandle) != stamp.TargetId || Digest(original, cancel) != stamp.Hash || Digest(copy, cancel) != stamp.Hash)
                    throw new IOException("源文件或入库文件已变化，停止删除。");
                List<FileStream> streams = HoldStreams(source, target, stamp.Streams, cancel);
                try { cancel.ThrowIfCancellationRequested(); MarkDelete(handle); }
                finally { foreach (FileStream stream in streams) stream.Dispose(); }
            }
        }

        internal static void DeleteSources(ImportBatch batch, CancellationToken cancel = default(CancellationToken))
        {
            if (!batch.Committed || !batch.DeleteAuthorized || batch.Operation != ImportOperation.Move) return;
            foreach (ImportItem item in batch.Items.Where(i => !i.Reference && (i.State == Copied || i.State == DeleteFailed)))
            {
                if (cancel.IsCancellationRequested) break; // 未处理项保持「已复制」，可在导入记录中继续。
                List<IDisposable> held = new List<IDisposable>();
                try
                {
                    RequireIdentity(item.Target, item.TargetId);
                    // 包括空目录；持有目标目录句柄直到删源结束，防止校验后被移走。
                    foreach (ImportDirectoryStamp dir in item.TargetDirectories)
                    {
                        cancel.ThrowIfCancellationRequested();
                        string target = At(item, item.Target, dir.Relative);
                        SafeFileHandle handle = Open(target, ReadAttributes, ShareRead | 2, BackupSemantics);
                        held.Add(handle);
                        if (Identity(handle) != dir.TargetId) throw new IOException("目标目录已变化，保留源目录。");
                        string source = At(item, item.Source, dir.Relative);
                        if (Directory.Exists(source)) held.AddRange(HoldStreams(source, target, dir.Streams, cancel));
                    }
                    if (item.Folder && item.TargetDirectories.Count != item.Directories.Count)
                        throw new IOException("旧记录缺少目录校验信息，保留源目录。");
                    foreach (ImportFileStamp file in item.Files)
                    {
                        cancel.ThrowIfCancellationRequested();
                        CheckFile(item, item.Target, file, false);
                        // 删除后、日志落盘前崩溃：目标仍完整且源路径确实不存在时，可恢复检查点。
                        if (!file.Deleted)
                        {
                            try { File.GetAttributes(At(item, item.Source, file.Relative)); }
                            catch (FileNotFoundException) { file.Deleted = true; }
                            catch (DirectoryNotFoundException) { file.Deleted = true; }
                        }
                        if (!file.Deleted) CheckFile(item, item.Source, file, true);
                    }
                    CheckTree(item, true);
                    foreach (ImportFileStamp file in item.Files.Where(f => !f.Deleted))
                    {
                        cancel.ThrowIfCancellationRequested();
                        DeleteVerified(item, file, cancel); file.Deleted = true;
                    }
                    // 非递归删除：复制后新产生的文件永远不会被随目录一起删除。
                    if (item.Folder) foreach (string dir in item.Directories.OrderByDescending(d => d.Length))
                    {
                        string path = At(item, item.Source, dir);
                        cancel.ThrowIfCancellationRequested();
                        EnsureNoLinks(path);
                        if (Directory.Exists(path))
                            using (SafeFileHandle handle = Open(path, DeleteAccess, ShareAll, BackupSemantics)) MarkDelete(handle);
                    }
                    item.State = Moved; item.Error = null;
                }
                catch (Exception ex) { item.State = DeleteFailed; item.Error = ex.Message; }
                finally { foreach (IDisposable handle in held) handle.Dispose(); }
                SaveSoon(batch);
            }
            DetachRetained(batch);
            Save(batch);
        }

        // ===== 撤回与恢复 =====

        // 仅保源异常路径复制数据；正常同卷移动保持零数据复制。
        private static void DetachRetained(ImportBatch batch)
        {
            foreach (ImportItem item in batch.Items.Where(i => !i.Reference && (i.State == Copied || i.State == DeleteFailed)))
            {
                foreach (ImportFileStamp stamp in item.Files.Where(f => f.Linked && !f.Deleted))
                {
                    string target = At(item, item.Target, stamp.Relative);
                    string temporary = target + ".detach-" + batch.Id;
                    try
                    {
                        stamp.DetachedPath = temporary;
                        SaveSoon(batch); // 分离过程也节流记账；中断时可按固定临时名找回副本。
                        ImportFileStamp independent;
                        using (SafeFileHandle handle = Open(target, GenericRead | DeleteAccess, ShareRead, SequentialScan))
                        {
                            if (Identity(handle) != stamp.TargetId) throw new IOException("目标文件身份已变化。");
                            independent = new ImportFileStamp { Relative = stamp.Relative, Length = new FileInfo(target).Length,
                                Modified = File.GetLastWriteTimeUtc(target).Ticks };
                            Place(target, temporary, independent, false, CancellationToken.None);
                            MarkDelete(handle);
                        }
                        File.Move(temporary, target);
                        stamp.Linked = false; stamp.TargetId = independent.TargetId; stamp.Hash = independent.Hash;
                        stamp.Length = independent.Length; stamp.Modified = independent.Modified; stamp.Streams = independent.Streams;
                        stamp.DetachedPath = null;
                        if (!item.Folder) item.TargetId = stamp.TargetId;
                    }
                    catch (Exception ex)
                    {
                        item.Error = (item.Error ?? "") + "；共享链接未能分离，请勿编辑源或目标文件；保留副本：" + temporary + "；" + ex.Message;
                        if (!batch.Committed)
                        {
                            try
                            {
                                if (RemoveDuplicate(item, item.Target, item.TargetId)) item.State = RolledBack;
                            }
                            catch (Exception rollback) { item.Error += "；" + rollback.Message; }
                        }
                        if (item.State != RolledBack) item.State = DeleteFailed;
                        break;
                    }
                }
            }
        }

        private static bool SameContent(string a, string b)
        {
            using (SafeFileHandle x = Open(a, GenericRead, ShareRead, SequentialScan))
            using (SafeFileHandle y = Open(b, GenericRead, ShareRead, SequentialScan))
            {
                if (SameFile(x, y)) return true;
                using (FileStream p = new FileStream(x, FileAccess.Read, 1 << 16))
                using (FileStream q = new FileStream(y, FileAccess.Read, 1 << 16))
                    return p.Length == q.Length && Digest(p) == Digest(q);
            }
        }

        // 仅当入库路径与源路径内容完全一致（或为同一文件）时删除入库路径；任何差异都保留，不依赖日志中的逐项状态。
        private static bool RemoveDuplicate(ImportItem item, string target, string identity)
        {
            string source = item.Source;
            if (!File.Exists(target) && !Directory.Exists(target)) return true;
            EnsureNoLinks(source); RequireIdentity(target, identity);
            if (File.Exists(target))
            {
                RemoveDuplicateFile(source, target, identity);
                return true;
            }
            if (!Directory.Exists(target) || !Directory.Exists(source)) return false;
            List<string> files = new List<string>(), dirs = new List<string>();
            Collect(new DirectoryInfo(target), "", files, dirs, 0);
            if (files.Count != item.Files.Count || dirs.Count != item.TargetDirectories.Count) return false;
            foreach (string file in files)
            {
                ImportFileStamp stamp = item.Files.Find(f => f.Relative.Equals(file, StringComparison.OrdinalIgnoreCase));
                if (stamp == null) return false;
                string original = Path.Combine(source, file);
                RequireIdentity(Path.Combine(target, file), stamp.TargetId);
                if (!File.Exists(original) || !SameContent(original, Path.Combine(target, file))) return false;
            }
            foreach (string dir in dirs)
            {
                ImportDirectoryStamp stamp = item.TargetDirectories.Find(d => d.Relative.Equals(dir, StringComparison.OrdinalIgnoreCase));
                if (stamp == null || !Directory.Exists(Path.Combine(source, dir))) return false;
                RequireIdentity(Path.Combine(target, dir), stamp.TargetId);
            }
            foreach (string file in files) RemoveDuplicateFile(Path.Combine(source, file), Path.Combine(target, file), item.Files.Find(f => f.Relative == file).TargetId);
            foreach (string dir in dirs.OrderByDescending(d => d.Length))
            {
                string path = Path.Combine(target, dir);
                using (SafeFileHandle handle = Open(path, DeleteAccess, ShareRead, BackupSemantics))
                {
                    if (Identity(handle) != item.TargetDirectories.Find(d => d.Relative == dir).TargetId) return false;
                    List<FileStream> streams = HoldStreams(Path.Combine(source, dir), path, null, CancellationToken.None);
                    try { MarkDelete(handle); } finally { foreach (FileStream stream in streams) stream.Dispose(); }
                }
            }
            return true;
        }

        private static void RemoveDuplicateFile(string source, string target, string identity)
        {
            using (SafeFileHandle handle = Open(target, GenericRead | DeleteAccess, ShareRead, SequentialScan))
            using (SafeFileHandle original = Open(source, GenericRead, ShareRead | 4, SequentialScan))
            {
                if (string.IsNullOrEmpty(identity) || Identity(handle) != identity) throw new IOException("目标文件身份已变化，已保留。");
                if (SameFile(handle, original)) { MarkDelete(handle); return; }
                using (FileStream a = new FileStream(handle, FileAccess.Read))
                using (FileStream b = new FileStream(original, FileAccess.Read))
                {
                    if (a.Length != b.Length || Digest(a) != Digest(b)) throw new IOException("副本内容已变化，已保留。");
                    List<FileStream> streams = HoldStreams(source, target, null, CancellationToken.None);
                    try { MarkDelete(handle); } finally { foreach (FileStream stream in streams) stream.Dispose(); }
                }
            }
        }

        private static void Rollback(ImportBatch batch)
        {
            foreach (ImportItem item in batch.Items.Where(i => !i.Reference && i.State == Copied))
            {
                try
                {
                    if (RemoveDuplicate(item, item.Target, item.TargetId)) item.State = RolledBack;
                    else item.Error = "入库副本与源文件不一致，已保留：" + item.Target;
                }
                catch (Exception ex) { item.Error = "入库副本未能撤回：" + ex.Message; }
            }
        }

        internal static string[] PendingJournals(string directory)
        {
            return Directory.Exists(directory) ? Directory.GetFiles(directory, "*.pending") : new string[0];
        }

        internal static void DropMarker(string marker)
        {
            try { File.Delete(marker); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // 上次运行中断：清理本批临时落点；未入库时撤回与源一致的副本。已入库的不自动删源，留给导入记录确认。
        internal static void Recover(ImportBatch batch, bool registered)
        {
            foreach (ImportItem item in batch.Items.Where(i => !i.Reference && i.State != Skipped && !string.IsNullOrEmpty(i.Target)))
            {
                string staging = item.Target + ".import-" + batch.Id;
                try
                {
                    // 身份缺失、复制未完整落地或副本已修改时保留现场，不能按路径猜测归属。
                    if (!RemoveDuplicate(item, staging, item.StagingId)) item.Error = "临时副本需核对：" + staging;
                }
                catch (Exception ex) { item.Error = "临时副本已保留：" + ex.Message; }
                if (batch.Committed || registered) continue;
                try { if (RemoveDuplicate(item, item.Target, item.TargetId ?? item.StagingId)) item.State = RolledBack; }
                catch (Exception ex) { item.Error = "入库副本未能撤回：" + ex.Message; }
            }
            if (!batch.Committed && registered) { batch.Committed = true; batch.Error = "上次导入在入库后中断，源文件已保留，可在导入记录中确认删除。"; }
            else if (!batch.Committed) batch.Error = "上次导入被中断；可核对的副本已撤回，其余文件保留并记录，源文件保留。";
            else if (batch.Operation == ImportOperation.Move && batch.Items.Any(i => i.State == Copied || i.State == DeleteFailed))
                batch.Error = "上次删除源文件时中断，可在导入记录中重试。";
            foreach (ImportItem item in batch.Items.Where(i => !i.Reference && !string.IsNullOrEmpty(i.Target)))
            {
                foreach (ImportFileStamp stamp in item.Files)
                {
                    string temporary = At(item, item.Target, stamp.Relative) + ".detach-" + batch.Id;
                    if (File.Exists(temporary)) stamp.DetachedPath = temporary;
                }
                if (item.Files.Any(f => !string.IsNullOrEmpty(f.DetachedPath)))
                    item.Error = "上次分离共享链接时中断；请核对保留副本：" + string.Join("、", item.Files.Where(f => !string.IsNullOrEmpty(f.DetachedPath)).Select(f => f.DetachedPath));
            }
            if (batch.Committed) DetachRetained(batch);
            Finish(batch);
        }

        internal static void Run(ImportBatch batch, Func<ImportBatch, bool> commit, Action<int, string> progress,
            CancellationToken cancel = default(CancellationToken))
        {
            Save(batch); // 不能持久化任务时，不开始文件 IO。
            File.WriteAllText(Marker(batch), string.Empty);
            CopyAll(batch, progress, cancel);
            if (batch.Operation == ImportOperation.Move && (cancel.IsCancellationRequested || batch.Items.Any(i => i.State == Failed)))
                DetachRetained(batch);
            try { batch.Committed = commit(batch); }
            catch (Exception ex) { batch.Committed = false; batch.Error = "入库记录保存失败：" + ex.Message; }
            batch.DeleteAuthorized = batch.Committed && batch.Items.All(i => i.State == Copied || i.State == Linked || i.State == Skipped);
            if (!batch.Committed)
            {
                Rollback(batch);
                batch.Error = (batch.Error ?? "入库记录保存失败") + "；已撤回入库副本，全部源文件保留。";
            }
            else if (!batch.DeleteAuthorized && batch.Operation == ImportOperation.Move)
                batch.Error = "部分失败，全部源文件保留；可在导入记录中确认删除已入库项的源文件。";
            Save(batch);
            DeleteSources(batch, cancel);
            Finish(batch);
        }

        internal static string Summary(ImportBatch batch)
        {
            int rolledBack = batch.Items.Count(i => i.State == RolledBack);
            return "已移动 " + batch.Items.Count(i => i.State == Moved) + "，已复制 " + batch.Items.Count(i => i.State == Copied)
                + "，已收录/跳过 " + batch.Items.Count(i => i.State == Linked || i.State == Skipped) + "，失败 " + batch.Items.Count(i => i.State == Failed)
                + "，源文件删除失败 " + batch.Items.Count(i => i.State == DeleteFailed)
                + (rolledBack > 0 ? "，已撤回 " + rolledBack : "");
        }
    }
}
