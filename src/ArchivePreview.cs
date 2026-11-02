using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AppManager
{
    /// <summary>压缩包内的一个文件或文件夹；文件夹的大小与计数为全部子孙之和。</summary>
    internal sealed class ArchiveEntryInfo
    {
        public string Name;
        public bool IsFolder;
        /// <summary>文件：解压后大小，-1 表示未知；文件夹：子孙文件已知大小之和。</summary>
        public long Size;
        /// <summary>修改时间（本地时间）；DateTime.MinValue 表示未知。</summary>
        public DateTime Modified;
        public bool Encrypted;
        public ArchiveEntryInfo Parent;
        /// <summary>直接子项（仅文件夹）。</summary>
        public List<ArchiveEntryInfo> Children;
        /// <summary>子孙中的文件 / 文件夹数（仅文件夹）。</summary>
        public int FileCount, FolderCount;
        // 构建期按名称查找子项，构建完成后释放
        internal Dictionary<string, ArchiveEntryInfo> Index;
    }

    /// <summary>
    /// 压缩包内容列表（只读文件头，不解压数据）。
    /// zip 用 .NET 自带的 ZipArchive，文件名先按 UTF-8 严格解码、失败再按系统 ANSI 代码页（中文系统为 GBK）；
    /// 其余格式（rar/7z/tar/gz/xz/zst/cab/lzh…）及 ZipArchive 无法解析的 zip 交给系统自带的 libarchive（archiveint.dll）。
    /// </summary>
    internal sealed class ArchivePreview
    {
        /// <summary>最多列出的条目数；超出部分不再读取，避免超大压缩包耗尽内存。</summary>
        internal const int MaxEntries = 200000;

        public ArchiveEntryInfo Root;
        public int FileCount, FolderCount;
        /// <summary>全部文件解压后已知大小之和。</summary>
        public long TotalSize;
        /// <summary>存在大小未知的文件（单文件压缩流等）。</summary>
        public bool SizeUnknown;
        /// <summary>压缩包文件本身的大小。</summary>
        public long ArchiveBytes;
        public bool HasEncrypted;
        /// <summary>条目超过 MaxEntries，只列出了前一部分。</summary>
        public bool Truncated;
        /// <summary>读取中途出错（压缩包损坏或不完整），只列出了可读取的部分。</summary>
        public string Warning;

        public static ArchivePreview Read(string path, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            long bytes = new FileInfo(path).Length;
            ArchivePreview preview;
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                try { preview = ReadZip(path, cancellation); }
                catch (InvalidDataException zipError)
                {
                    // 非常规结构的 zip 交给 libarchive 再试；系统没有该组件时报告 zip 自身的错误
                    try { preview = LibArchive.Read(path, true, cancellation); }
                    catch (NotSupportedException) { throw zipError; }
                }
            }
            else preview = LibArchive.Read(path, false, cancellation);
            preview.ArchiveBytes = bytes;
            return preview;
        }

        // ===== zip =====

        private static ArchivePreview ReadZip(string path, CancellationToken cancellation)
        {
            Builder builder = new Builder();
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Read, false, new ZipNameEncoding()))
            {
                int count = 0;
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    if ((count++ & 255) == 0) cancellation.ThrowIfCancellationRequested();
                    string name = entry.FullName;
                    bool folder = name.EndsWith("/", StringComparison.Ordinal) || name.EndsWith("\\", StringComparison.Ordinal);
                    DateTime modified;
                    try { modified = entry.LastWriteTime.LocalDateTime; }
                    catch (ArgumentOutOfRangeException) { modified = DateTime.MinValue; }
                    if (!builder.Add(name, folder, folder ? 0 : entry.Length, modified, false)) break;
                }
            }
            return builder.Finish();
        }

        /// <summary>
        /// zip 文件名解码：未设置 UTF-8 标记的条目由 ZipArchive 交给此编码。合法 UTF-8 按 UTF-8 解
        /// （macOS 等打包时常不设标记），否则按系统 ANSI 代码页解（Windows 资源管理器打包的中文 zip）。
        /// </summary>
        internal sealed class ZipNameEncoding : Encoding
        {
            private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
            private static readonly Encoding Legacy = Encoding.Default;

            private static Encoding Pick(byte[] bytes, int index, int count)
            {
                try { Utf8.GetCharCount(bytes, index, count); return Utf8; }
                catch (DecoderFallbackException) { return Legacy; }
            }

            public override int GetByteCount(char[] chars, int index, int count) { return Legacy.GetByteCount(chars, index, count); }
            public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
            { return Legacy.GetBytes(chars, charIndex, charCount, bytes, byteIndex); }
            public override int GetCharCount(byte[] bytes, int index, int count) { return Pick(bytes, index, count).GetCharCount(bytes, index, count); }
            public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
            { return Pick(bytes, byteIndex, byteCount).GetChars(bytes, byteIndex, byteCount, chars, charIndex); }
            public override string GetString(byte[] bytes, int index, int count) { return Pick(bytes, index, count).GetString(bytes, index, count); }
            public override int GetMaxByteCount(int charCount) { return Math.Max(Legacy.GetMaxByteCount(charCount), Utf8.GetMaxByteCount(charCount)); }
            public override int GetMaxCharCount(int byteCount) { return Math.Max(Legacy.GetMaxCharCount(byteCount), Utf8.GetMaxCharCount(byteCount)); }
        }

        // ===== 目录树构建 =====

        private sealed class Builder
        {
            private readonly ArchivePreview preview = new ArchivePreview();
            private readonly ArchiveEntryInfo root = NewFolder("", null);
            private int entries;

            private static ArchiveEntryInfo NewFolder(string name, ArchiveEntryInfo parent)
            {
                return new ArchiveEntryInfo
                {
                    Name = name, IsFolder = true, Parent = parent, Modified = DateTime.MinValue,
                    Children = new List<ArchiveEntryInfo>(), Index = new Dictionary<string, ArchiveEntryInfo>(StringComparer.Ordinal)
                };
            }

            public int Count { get { return entries; } }

            /// <summary>加入一个条目；达到上限时返回 false，调用方应停止读取。</summary>
            public bool Add(string path, bool folder, long size, DateTime modified, bool encrypted)
            {
                if (entries >= MaxEntries) { preview.Truncated = true; return false; }
                entries++;
                List<string> parts = Split(path);
                if (parts.Count == 0) return true;   // 「./」等根目录条目
                ArchiveEntryInfo parent = root;
                int last = folder ? parts.Count : parts.Count - 1;
                for (int i = 0; i < last; i++)
                {
                    ArchiveEntryInfo child;
                    if (!parent.Index.TryGetValue("d:" + parts[i], out child))
                    {
                        child = NewFolder(parts[i], parent);
                        parent.Index["d:" + parts[i]] = child;
                        parent.Children.Add(child);
                        for (ArchiveEntryInfo a = parent; a != null; a = a.Parent) a.FolderCount++;
                    }
                    parent = child;
                }
                if (folder)
                {
                    if (modified != DateTime.MinValue) parent.Modified = modified;
                    return true;
                }
                string name = parts[parts.Count - 1];
                ArchiveEntryInfo file;
                if (parent.Index.TryGetValue("f:" + name, out file))
                {
                    // 同名条目（tar 追加更新）以后出现的为准
                    long delta = Math.Max(0, size) - Math.Max(0, file.Size);
                    for (ArchiveEntryInfo a = parent; a != null; a = a.Parent) a.Size += delta;
                }
                else
                {
                    file = new ArchiveEntryInfo { Name = name, Parent = parent };
                    parent.Index["f:" + name] = file;
                    parent.Children.Add(file);
                    for (ArchiveEntryInfo a = parent; a != null; a = a.Parent) { a.FileCount++; a.Size += Math.Max(0, size); }
                }
                file.Size = size;
                file.Modified = modified;
                file.Encrypted = encrypted;
                return true;
            }

            private static List<string> Split(string path)
            {
                List<string> parts = new List<string>();
                foreach (string part in (path ?? string.Empty).Replace('\\', '/').Split('/'))
                    if (part.Length > 0 && part != ".") parts.Add(part);
                return parts;
            }

            public void Fail(string warning)
            {
                preview.Warning = warning;
            }

            public ArchivePreview Finish()
            {
                bool unknown = false, encrypted = false;
                Stack<ArchiveEntryInfo> stack = new Stack<ArchiveEntryInfo>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    ArchiveEntryInfo folder = stack.Pop();
                    folder.Index = null;
                    foreach (ArchiveEntryInfo child in folder.Children)
                    {
                        if (child.IsFolder) stack.Push(child);
                        else
                        {
                            if (child.Size < 0) unknown = true;
                            if (child.Encrypted) encrypted = true;
                        }
                    }
                }
                preview.Root = root;
                preview.FileCount = root.FileCount;
                preview.FolderCount = root.FolderCount;
                preview.TotalSize = root.Size;
                preview.SizeUnknown = unknown;
                preview.HasEncrypted = encrypted;
                return preview;
            }
        }

        // ===== libarchive（Windows 自带 archiveint.dll）=====

        private static class LibArchive
        {
            private const string Dll = "archiveint.dll";
            private const int OK = 0, EOF = 1, RETRY = -10, WARN = -20, FAILED = -25;
            private const ushort IFMT = 0xF000, IFDIR = 0x4000;

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate IntPtr ReadCallback(IntPtr archive, IntPtr data, out IntPtr buffer);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate long SkipCallback(IntPtr archive, IntPtr data, long request);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate long SeekCallback(IntPtr archive, IntPtr data, long offset, int whence);

            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr archive_read_new();
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_free(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_filter_all(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_format_all(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_format_raw(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
            private static extern int archive_read_set_options(IntPtr a, string options);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_set_read_callback(IntPtr a, ReadCallback callback);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_set_skip_callback(IntPtr a, SkipCallback callback);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_set_seek_callback(IntPtr a, SeekCallback callback);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_set_callback_data(IntPtr a, IntPtr data);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_open1(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_next_header(IntPtr a, out IntPtr entry);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_filter_count(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr archive_error_string(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr archive_entry_pathname_w(IntPtr entry);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr archive_entry_pathname(IntPtr entry);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern ushort archive_entry_filetype(IntPtr entry);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern long archive_entry_size(IntPtr entry);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_entry_size_is_set(IntPtr entry);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern long archive_entry_mtime(IntPtr entry);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_entry_mtime_is_set(IntPtr entry);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_entry_is_encrypted(IntPtr entry);

            public static ArchivePreview Read(string path, bool zip, CancellationToken cancellation)
            {
                try
                {
                    try { return ReadPass(path, false, zip, cancellation); }
                    catch (InvalidDataException first)
                    {
                        // 不是归档格式时，可能是单个文件的压缩流（log.txt.gz）：按原始数据格式再读一次
                        ArchivePreview raw = null;
                        try { raw = ReadPass(path, true, zip, cancellation); }
                        catch (InvalidDataException) { }
                        if (raw == null) throw first;
                        return raw;
                    }
                }
                catch (DllNotFoundException) { throw Unsupported(); }
                catch (EntryPointNotFoundException) { throw Unsupported(); }
                catch (BadImageFormatException) { throw Unsupported(); }
            }

            private static NotSupportedException Unsupported()
            {
                return new NotSupportedException("当前系统缺少压缩包组件（archiveint.dll），无法查看此格式");
            }

            private static ArchivePreview ReadPass(string path, bool raw, bool zip, CancellationToken cancellation)
            {
                Builder builder = new Builder();
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
                using (Source source = new Source(stream, cancellation))
                {
                    IntPtr a = archive_read_new();
                    if (a == IntPtr.Zero) throw new OutOfMemoryException();
                    try
                    {
                        archive_read_support_filter_all(a);
                        if (raw) archive_read_support_format_raw(a);
                        else archive_read_support_format_all(a);
                        // ZipArchive 没能读的 zip：无 UTF-8 标记的文件名按系统代码页解码，避免中文乱码
                        if (zip && !raw) archive_read_set_options(a, "zip:hdrcharset=CP" + Encoding.Default.CodePage);
                        archive_read_set_read_callback(a, source.ReadDelegate);
                        archive_read_set_skip_callback(a, source.SkipDelegate);
                        archive_read_set_seek_callback(a, source.SeekDelegate);
                        archive_read_set_callback_data(a, IntPtr.Zero);
                        int r = archive_read_open1(a);
                        if (r < WARN) throw Failure(a, source, cancellation);
                        // raw 能「读」任何文件：只有确实经过解压过滤器（gzip/xz…）才算单文件压缩流
                        if (raw && archive_filter_count(a) < 2) throw new InvalidDataException("不是压缩包");
                        int failures = 0;
                        while (true)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            IntPtr entry;
                            r = archive_read_next_header(a, out entry);
                            if (r == EOF) break;
                            if (r == RETRY) continue;
                            if (r == FAILED && entry != IntPtr.Zero && ++failures < 100) continue;   // 单个条目损坏，跳过
                            if (r < WARN || entry == IntPtr.Zero)
                            {
                                Exception error = Failure(a, source, cancellation);
                                // 已读到部分内容（下载不完整等）时列出可读部分，否则视为无法读取
                                if (builder.Count == 0) throw error;
                                builder.Fail("压缩包不完整或已损坏，只列出了可读取的部分");
                                break;
                            }
                            if (raw)
                            {
                                builder.Add(RawName(path), false, -1, DateTime.MinValue, false);
                                break;
                            }
                            string name = EntryName(entry);
                            if (name == null) continue;
                            bool folder = (archive_entry_filetype(entry) & IFMT) == IFDIR
                                || name.EndsWith("/", StringComparison.Ordinal) || name.EndsWith("\\", StringComparison.Ordinal);
                            long size = archive_entry_size_is_set(entry) != 0 ? archive_entry_size(entry) : -1;
                            DateTime modified = archive_entry_mtime_is_set(entry) != 0 ? FromUnix(archive_entry_mtime(entry)) : DateTime.MinValue;
                            if (!builder.Add(name, folder, folder ? 0 : size, modified, archive_entry_is_encrypted(entry) != 0)) break;
                        }
                    }
                    finally { archive_read_free(a); }
                }
                return builder.Finish();
            }

            private static Exception Failure(IntPtr a, Source source, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                if (source.Error != null) return source.Error;
                IntPtr message = archive_error_string(a);
                return new InvalidDataException(Describe(message == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(message)));
            }

            /// <summary>libarchive 的错误信息为英文：常见情形换成中文说明，其余保留原文便于排查。</summary>
            private static string Describe(string text)
            {
                if (string.IsNullOrEmpty(text) || text.IndexOf("Unrecognized archive format", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "无法识别的压缩包格式";
                if (text.IndexOf("ncrypt", StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("passphrase", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "压缩包已加密（连文件名一起加密），需要密码才能查看";
                if (text.IndexOf("runcated", StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("Premature end", StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("Damaged", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "压缩包不完整或已损坏（" + text + "）";
                return text;
            }

            private static string EntryName(IntPtr entry)
            {
                IntPtr wide = archive_entry_pathname_w(entry);
                if (wide != IntPtr.Zero) return Marshal.PtrToStringUni(wide);
                IntPtr narrow = archive_entry_pathname(entry);
                return narrow == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(narrow);
            }

            /// <summary>单文件压缩流的内部文件名：去掉最后一层扩展名（log.txt.gz → log.txt）。</summary>
            private static string RawName(string path)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                return string.IsNullOrEmpty(name) ? "data" : name;
            }

            private static DateTime FromUnix(long seconds)
            {
                // 超出 DateTime 范围的时间戳（损坏或刻意构造）视为未知
                if (seconds < -62135596800L || seconds > 253402300799L) return DateTime.MinValue;
                return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).ToLocalTime();
            }

            /// <summary>
            /// 以 .NET FileStream 为 libarchive 提供数据：允许他人同时移动/删除文件，取消时读回调立即报错返回。
            /// 回调委托与固定缓冲区在 archive_read_free 之前保持存活。
            /// </summary>
            private sealed class Source : IDisposable
            {
                private readonly FileStream stream;
                private readonly CancellationToken cancellation;
                private readonly byte[] buffer = new byte[64 * 1024];
                private GCHandle pin;
                public readonly ReadCallback ReadDelegate;
                public readonly SkipCallback SkipDelegate;
                public readonly SeekCallback SeekDelegate;
                public Exception Error;

                public Source(FileStream stream, CancellationToken cancellation)
                {
                    this.stream = stream;
                    this.cancellation = cancellation;
                    pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                    ReadDelegate = Read;
                    SkipDelegate = Skip;
                    SeekDelegate = Seek;
                }

                // 回调内的异常不能抛进本机代码：记录后返回错误码，由调用方在 libarchive 返回后重新抛出
                private IntPtr Read(IntPtr archive, IntPtr data, out IntPtr result)
                {
                    result = pin.AddrOfPinnedObject();
                    try
                    {
                        if (cancellation.IsCancellationRequested) return (IntPtr)(-1);
                        return (IntPtr)stream.Read(buffer, 0, buffer.Length);
                    }
                    catch (Exception ex) { if (Error == null) Error = ex; return (IntPtr)(-1); }
                }

                private long Skip(IntPtr archive, IntPtr data, long request)
                {
                    try
                    {
                        if (cancellation.IsCancellationRequested || request <= 0) return 0;
                        long step = Math.Min(request, stream.Length - stream.Position);
                        if (step <= 0) return 0;
                        stream.Seek(step, SeekOrigin.Current);
                        return step;
                    }
                    catch (Exception ex) { if (Error == null) Error = ex; return -30; }
                }

                private long Seek(IntPtr archive, IntPtr data, long offset, int whence)
                {
                    try
                    {
                        if (cancellation.IsCancellationRequested) return -30;
                        SeekOrigin origin = whence == 1 ? SeekOrigin.Current : whence == 2 ? SeekOrigin.End : SeekOrigin.Begin;
                        return stream.Seek(offset, origin);
                    }
                    catch (Exception ex) { if (Error == null) Error = ex; return -30; }
                }

                public void Dispose()
                {
                    if (pin.IsAllocated) pin.Free();
                }
            }
        }
    }
}
