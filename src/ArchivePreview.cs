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
        // 构建期按名称查找子项（首个子项加入时才创建），构建完成后释放
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
        /// <summary>路径层级与文件夹总数上限：恶意构造的超深路径会生成大量文件夹节点。</summary>
        internal const int MaxDepth = 256, MaxFolders = 50000;

        public ArchiveEntryInfo Root;
        public int FileCount, FolderCount;
        /// <summary>全部文件解压后已知大小之和。</summary>
        public long TotalSize;
        /// <summary>存在大小未知的文件（单文件压缩流等）。</summary>
        public bool SizeUnknown;
        /// <summary>压缩包文件本身的大小。</summary>
        public long ArchiveBytes;
        public bool HasEncrypted;
        /// <summary>条目或文件夹超过上限，只列出了前一部分。</summary>
        public bool Truncated;
        /// <summary>读取中途出错（压缩包损坏或不完整）或有条目未能列出时的说明。</summary>
        public string Warning;

        public static ArchivePreview Read(string path, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            long bytes = new FileInfo(path).Length;
            ArchivePreview preview;
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                Exception zipError = null;
                preview = null;
                try { preview = ReadZip(path, cancellation); }
                catch (InvalidDataException ex) { zipError = ex; }   // 分卷、非常规结构
                catch (ArgumentException ex) { zipError = ex; }      // 条目名含 " < > | 等字符，ZipArchive 拒绝整个压缩包
                if (preview == null)
                {
                    // 交给 libarchive 再试；系统没有该组件时报告 zip 自身的错误
                    try { preview = LibArchive.Read(path, cancellation); }
                    catch (NotSupportedException) { throw new InvalidDataException(zipError.Message, zipError); }
                }
            }
            else preview = LibArchive.Read(path, cancellation);
            preview.ArchiveBytes = bytes;
            return preview;
        }

        // ===== 文件名解码 =====

        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        /// <summary>没有 Unicode 标记的文件名：合法 UTF-8 按 UTF-8 解，否则按系统 ANSI 代码页（GBK）解。</summary>
        internal static Encoding PickNameEncoding(byte[] bytes, int index, int count)
        {
            try { StrictUtf8.GetCharCount(bytes, index, count); return StrictUtf8; }
            catch (DecoderFallbackException) { return Encoding.Default; }
        }

        // ===== zip =====

        private static ArchivePreview ReadZip(string path, CancellationToken cancellation)
        {
            Builder builder = new Builder(cancellation);
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Read, false, new ZipNameEncoding()))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
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
            private static readonly Encoding Legacy = Encoding.Default;

            public override int GetByteCount(char[] chars, int index, int count) { return Legacy.GetByteCount(chars, index, count); }
            public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
            { return Legacy.GetBytes(chars, charIndex, charCount, bytes, byteIndex); }
            public override int GetCharCount(byte[] bytes, int index, int count) { return PickNameEncoding(bytes, index, count).GetCharCount(bytes, index, count); }
            public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
            { return PickNameEncoding(bytes, byteIndex, byteCount).GetChars(bytes, byteIndex, byteCount, chars, charIndex); }
            public override string GetString(byte[] bytes, int index, int count) { return PickNameEncoding(bytes, index, count).GetString(bytes, index, count); }
            public override int GetMaxByteCount(int charCount) { return Math.Max(Legacy.GetMaxByteCount(charCount), StrictUtf8.GetMaxByteCount(charCount)); }
            public override int GetMaxCharCount(int byteCount) { return Math.Max(Legacy.GetMaxCharCount(byteCount), StrictUtf8.GetMaxCharCount(byteCount)); }
        }

        // ===== 目录树构建 =====

        /// <summary>
        /// 构建目录树：加入条目只做查找与挂接（与路径深度成正比），数量与大小在 Finish 中一次性自底向上汇总，
        /// 避免超深路径下每新建一层都遍历全部祖先造成的平方级开销。
        /// </summary>
        private sealed class Builder
        {
            private readonly ArchivePreview preview = new ArchivePreview();
            private readonly ArchiveEntryInfo root = NewFolder("", null);
            private readonly CancellationToken cancellation;
            private int entries, folders;
            private bool tooDeep;

            public Builder(CancellationToken cancellation)
            {
                this.cancellation = cancellation;
            }

            private static ArchiveEntryInfo NewFolder(string name, ArchiveEntryInfo parent)
            {
                return new ArchiveEntryInfo { Name = name, IsFolder = true, Parent = parent, Modified = DateTime.MinValue, Children = new List<ArchiveEntryInfo>() };
            }

            public int Count { get { return entries; } }

            private static ArchiveEntryInfo Find(ArchiveEntryInfo folder, string key)
            {
                ArchiveEntryInfo found;
                return folder.Index != null && folder.Index.TryGetValue(key, out found) ? found : null;
            }

            private static void Attach(ArchiveEntryInfo folder, string key, ArchiveEntryInfo child)
            {
                if (folder.Index == null) folder.Index = new Dictionary<string, ArchiveEntryInfo>(StringComparer.Ordinal);
                folder.Index[key] = child;
                folder.Children.Add(child);
            }

            /// <summary>加入一个条目；达到上限时返回 false，调用方应停止读取。</summary>
            public bool Add(string path, bool folder, long size, DateTime modified, bool encrypted)
            {
                if ((entries & 255) == 0) cancellation.ThrowIfCancellationRequested();
                if (entries >= MaxEntries) { preview.Truncated = true; return false; }
                entries++;
                List<string> parts = Split(path);
                if (parts == null) { tooDeep = true; return true; }
                if (parts.Count == 0) return true;   // 「./」等根目录条目
                ArchiveEntryInfo parent = root;
                int last = folder ? parts.Count : parts.Count - 1;
                for (int i = 0; i < last; i++)
                {
                    ArchiveEntryInfo child = Find(parent, "d:" + parts[i]);
                    if (child == null)
                    {
                        if (folders >= MaxFolders) { preview.Truncated = true; return false; }
                        folders++;
                        child = NewFolder(parts[i], parent);
                        Attach(parent, "d:" + parts[i], child);
                    }
                    parent = child;
                }
                if (folder)
                {
                    if (modified != DateTime.MinValue) parent.Modified = modified;
                    return true;
                }
                string name = parts[parts.Count - 1];
                ArchiveEntryInfo file = Find(parent, "f:" + name);   // 同名条目（tar 追加更新）以后出现的为准
                if (file == null)
                {
                    file = new ArchiveEntryInfo { Name = name, Parent = parent };
                    Attach(parent, "f:" + name, file);
                }
                file.Size = size < 0 ? -1 : size;
                file.Modified = modified;
                file.Encrypted = encrypted;
                return true;
            }

            /// <summary>按 / 与 \ 拆分路径，丢弃空段与「.」；层级超过 MaxDepth 时返回 null（不为其生成节点）。</summary>
            private static List<string> Split(string path)
            {
                List<string> parts = new List<string>();
                if (string.IsNullOrEmpty(path)) return parts;
                int start = 0;
                for (int i = 0; i <= path.Length; i++)
                {
                    if (i < path.Length && path[i] != '/' && path[i] != '\\') continue;
                    int length = i - start;
                    if (length > 0 && !(length == 1 && path[start] == '.'))
                    {
                        if (parts.Count >= MaxDepth) return null;
                        parts.Add(path.Substring(start, length));
                    }
                    start = i + 1;
                }
                return parts;
            }

            public void Fail(string warning)
            {
                preview.Warning = warning;
            }

            private static long SaturatingAdd(long total, long value)
            {
                return total > long.MaxValue - value ? long.MaxValue : total + value;
            }

            public ArchivePreview Finish()
            {
                // 先序收集全部文件夹，再逆序汇总：子文件夹总在父文件夹之前完成
                List<ArchiveEntryInfo> order = new List<ArchiveEntryInfo>();
                Stack<ArchiveEntryInfo> stack = new Stack<ArchiveEntryInfo>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    ArchiveEntryInfo folder = stack.Pop();
                    order.Add(folder);
                    foreach (ArchiveEntryInfo child in folder.Children) if (child.IsFolder) stack.Push(child);
                }
                bool unknown = false, encrypted = false;
                for (int i = order.Count - 1; i >= 0; i--)
                {
                    if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                    ArchiveEntryInfo folder = order[i];
                    folder.Index = null;
                    long size = 0;
                    int files = 0, subfolders = 0;
                    foreach (ArchiveEntryInfo child in folder.Children)
                    {
                        if (child.IsFolder)
                        {
                            size = SaturatingAdd(size, child.Size);
                            files += child.FileCount;
                            subfolders += child.FolderCount + 1;
                        }
                        else
                        {
                            files++;
                            if (child.Size >= 0) size = SaturatingAdd(size, child.Size);
                            else unknown = true;
                            if (child.Encrypted) encrypted = true;
                        }
                    }
                    folder.Size = size;
                    folder.FileCount = files;
                    folder.FolderCount = subfolders;
                }
                if (tooDeep)
                    preview.Warning = (preview.Warning == null ? string.Empty : preview.Warning + "；") + "部分条目路径层级过深，未列出";
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

        /// <summary>libarchive 无法识别格式（区别于已识别但损坏）：只有这种情况才按单文件压缩流再试。</summary>
        private static InvalidDataException Unrecognized()
        {
            InvalidDataException error = new InvalidDataException("无法识别的压缩包格式");
            error.Data["unrecognized"] = true;
            return error;
        }

        private static bool IsUnrecognized(Exception error)
        {
            return error.Data.Contains("unrecognized");
        }

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
            // 只注册进程内实现的解压过滤器：filter_all 还会启用 lz4/lzop/lrzip/grzip 等，
            // 它们在库内没有实现时会启动外部程序（按当前目录与 PATH 查找），构造的文件即可借此运行任意程序。
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_filter_gzip(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_filter_bzip2(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_filter_xz(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_filter_lzma(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_filter_lzip(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_filter_zstd(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_filter_compress(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_format_7zip(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_format_ar(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_format_cab(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_format_cpio(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_format_empty(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_format_iso9660(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_format_lha(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_format_rar(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_format_rar5(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_format_tar(IntPtr a);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_format_zip(IntPtr a);

            /// <summary>
            /// 逐个注册需要的格式，不用 format_all：其中的 mtree 读取器在列目录时会按清单里的路径
            /// （contents=\\server\share\x 或本机任意路径）打开/查询外部文件，一个伪装成 .tar 的文本文件
            /// 就能让程序访问任意路径（可能泄露 NTLM 凭据）。warc/xar 与查看器无关，一并不注册。
            /// 旧系统缺少某个导出时跳过该格式。
            /// </summary>
            private static void SupportArchiveFormats(IntPtr a)
            {
                Func<IntPtr, int>[] formats =
                {
                    archive_read_support_format_7zip, archive_read_support_format_ar, archive_read_support_format_cab,
                    archive_read_support_format_cpio, archive_read_support_format_empty, archive_read_support_format_iso9660,
                    archive_read_support_format_lha, archive_read_support_format_rar, archive_read_support_format_rar5,
                    archive_read_support_format_tar, archive_read_support_format_zip
                };
                foreach (Func<IntPtr, int> register in formats)
                {
                    try { register(a); }
                    catch (EntryPointNotFoundException) { }
                }
            }
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int archive_read_support_format_raw(IntPtr a);
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

            public static ArchivePreview Read(string path, CancellationToken cancellation)
            {
                try
                {
                    try { return ReadPass(path, false, cancellation); }
                    catch (InvalidDataException first)
                    {
                        // 不是归档格式时，可能是单个文件的压缩流（log.txt.gz）：按原始数据格式再读一次。
                        // 已识别但损坏的压缩包、tar 类（x.tar.gz、x.tgz）识别失败，都不能当成单个文件显示。
                        if (!IsUnrecognized(first) || !RawCandidate(path)) throw;
                        ArchivePreview raw = null;
                        try { raw = ReadPass(path, true, cancellation); }
                        catch (InvalidDataException) { }
                        if (raw == null) throw first;
                        return raw;
                    }
                }
                catch (DllNotFoundException) { throw Unsupported(); }
                catch (EntryPointNotFoundException) { throw Unsupported(); }
                catch (BadImageFormatException) { throw Unsupported(); }
            }

            private static bool RawCandidate(string path)
            {
                if (path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)) return false;
                return !RawName(path).EndsWith(".tar", StringComparison.OrdinalIgnoreCase);
            }

            private static NotSupportedException Unsupported()
            {
                return new NotSupportedException("当前系统缺少压缩包组件（archiveint.dll），无法查看此格式");
            }

            private static ArchivePreview ReadPass(string path, bool raw, CancellationToken cancellation)
            {
                Builder builder = new Builder(cancellation);
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
                using (Source source = new Source(stream, cancellation))
                {
                    IntPtr a = archive_read_new();
                    if (a == IntPtr.Zero) throw new OutOfMemoryException();
                    try
                    {
                        archive_read_support_filter_gzip(a);
                        archive_read_support_filter_bzip2(a);
                        archive_read_support_filter_xz(a);
                        archive_read_support_filter_lzma(a);
                        archive_read_support_filter_lzip(a);
                        archive_read_support_filter_zstd(a);
                        archive_read_support_filter_compress(a);
                        if (raw) archive_read_support_format_raw(a);
                        else SupportArchiveFormats(a);
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
                string text = message == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(message);
                if (string.IsNullOrEmpty(text) || text.IndexOf("Unrecognized archive format", StringComparison.OrdinalIgnoreCase) >= 0)
                    return Unrecognized();
                return new InvalidDataException(Describe(text));
            }

            /// <summary>libarchive 的错误信息为英文：常见情形换成中文说明，其余保留原文便于排查。</summary>
            private static string Describe(string text)
            {
                if (text.IndexOf("ncrypt", StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("passphrase", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "压缩包已加密（连文件名一起加密），需要密码才能查看";
                if (text.IndexOf("runcated", StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("Premature end", StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf("Damaged", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "压缩包不完整或已损坏（" + text + "）";
                return text;
            }

            /// <summary>
            /// 条目名。先取原始字节：没有 Unicode 信息的名称（GNU tar、RAR4、无标记 zip 等）只能从这里拿到原样字节，
            /// 按「合法 UTF-8 优先、否则系统代码页」解码；宽字符形式会把每个字节当 Latin-1 而出现乱码。
            /// 带 Unicode 信息的名称（pax、7z、rar5、有标记 zip）在 C 区域设置下取不到多字节形式，改用宽字符。
            /// </summary>
            private static string EntryName(IntPtr entry)
            {
                IntPtr narrow = archive_entry_pathname(entry);
                if (narrow != IntPtr.Zero)
                {
                    byte[] bytes = ReadBytes(narrow);
                    return PickNameEncoding(bytes, 0, bytes.Length).GetString(bytes);
                }
                IntPtr wide = archive_entry_pathname_w(entry);
                return wide == IntPtr.Zero ? null : Marshal.PtrToStringUni(wide);
            }

            /// <summary>读取以 0 结尾的字节串（最多 1 MB，超出部分截断；如此长的路径本身也会因层级过深被忽略）。</summary>
            private static byte[] ReadBytes(IntPtr text)
            {
                int length = 0;
                while (length < (1 << 20) && Marshal.ReadByte(text, length) != 0) length++;
                byte[] bytes = new byte[length];
                Marshal.Copy(text, bytes, 0, length);
                return bytes;
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

                /// <summary>
                /// 跳过：返回实际跳过的字节数。出错或取消时必须返回 0——libarchive 把其它值都当作「已前进」，
                /// 返回负数会被反复重试形成死循环；返回 0 时它改用 read，由 read 回调报告错误。
                /// </summary>
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
                    catch (Exception ex) { if (Error == null) Error = ex; return 0; }
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
