using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AppManager
{
    /// <summary>
    /// 图标提取：三级择优（256/48/32）+ HICON 兜底，有效像素占比低于 2% 视为空图。
    /// 优先 PrivateExtractIcons 取高分辨率，其次 SHGetFileInfo 取系统大图标，
    /// 全部失败时返回自绘占位图标（力求占位图数量降到 0）。
    /// 说明：原规格采用 JUMBO/EXTRALARGE/LARGE 系统图像列表（IImageList COM 接口），
    /// 该 COM 接口 vtable 顺序在不同 .NET 版本下存在调用风险，为保障单文件零依赖
    /// 稳定编译运行，此处以 PrivateExtractIcons(256/48) 等价实现 JUMBO/EXTRALARGE，
    /// SHGetFileInfo(LARGE) 实现 LARGE 级，目标一致：取得高质量图标并避免空图。
    /// </summary>
    internal static class Icons
    {
        private const uint SHGFI_ICON = 0x00000100u;
        private const uint SHGFI_LARGEICON = 0x00000000u;
        private const uint SHGFI_SMALLICON = 0x00000001u;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x00000010u;
        private const uint SHGFI_PIDL = 0x00000008u;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80u;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10u;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(IntPtr pidl, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string pszName, IntPtr pbc,
            out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint PrivateExtractIcons(string lpszFile, int nIconIndex,
            int cxIcon, int cyIcon, [Out] IntPtr[] phicon, [Out] uint[] piconid, uint nIcons, uint flags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int ExtractIconEx(string lpszFile, int nIconIndex,
            [Out] IntPtr[] phiconLarge, [Out] IntPtr[] phiconSmall, int nIcons);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        // ===== 模块级图标缓存 =====
        // 重扫时条目列表整体重建，但多数条目的图标来源不变；缓存可免去重复的
        // PrivateExtractIcons/SHGetFileInfo 抽取（较慢）。缓存持有 master 位图，对外一律返回副本，
        // 调用方照常 Dispose，二者互不影响。
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, Bitmap> Cache =
            new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        private const int CacheCap = 2000;

        /// <summary>缓存键：涵盖影响提取结果的全部字段（含 Name，用于占位图首字母区分），并追加图标来源时间戳。</summary>
        private static string CacheKey(AppInfo app, int size, bool small)
        {
            return (small ? "S" : "L") + size
                + "|" + (app.DisplayIcon ?? string.Empty)
                + "|" + (app.ShortcutPath ?? string.Empty)
                + "|" + (app.TargetPath ?? string.Empty)
                + "|" + (app.Name ?? string.Empty)
                + "|" + SourceStamp(app);
        }

        /// <summary>
        /// 图标来源时间戳：对参与图标解析的各文件取「最后写入时间」。
        /// 任一来源文件内容变化（时间戳随之变化）都会改变缓存键，从而仅让该条目的缓存失效，
        /// 重扫后即刷新为新图标；未变化的条目仍命中缓存，不影响整体性能。
        /// 目录图标恒定，不参与时间戳，避免目录内容变化导致无谓失效。
        /// </summary>
        private static string SourceStamp(AppInfo app)
        {
            StringBuilder sb = new StringBuilder(64);
            try
            {
                // .url 声明的图标文件（如 favicon 图片），其内容变化需同步失效
                string urlIcon;
                int urlIconIndex;
                if (TryResolveUrlIcon(app, out urlIcon, out urlIconIndex))
                    AppendFileStamp(sb, urlIcon);

                // 图标宿主：DisplayIcon（去掉 ,index 后）或目标文件
                string host;
                int index;
                ResolveIconHost(app, out host, out index);
                AppendFileStamp(sb, host);

                // .lnk/.url 自身（其 IconLocation/IconFile 声明可能变化）
                AppendFileStamp(sb, app.ShortcutPath);
                AppendFileStamp(sb, app.TargetPath);
            }
            catch { }
            return sb.ToString();
        }

        /// <summary>仅对真实文件追加「路径=最后写入时间」；目录按恒定图标跳过。</summary>
        private static void AppendFileStamp(StringBuilder sb, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (!File.Exists(path)) return;
                sb.Append(path).Append('=').Append(File.GetLastWriteTimeUtc(path).Ticks).Append(';');
            }
            catch { }
        }

        /// <summary>命中则返回 master 的副本（在锁内复制，避免并发清理时 master 被释放）。</summary>
        private static Bitmap CacheTakeClone(string key)
        {
            lock (CacheLock)
            {
                Bitmap b;
                if (!Cache.TryGetValue(key, out b)) return null;
                return SafeClone(b);
            }
        }

        /// <summary>把抽取结果作为 master 入缓存，并返回给调用方的副本（副本在锁内生成）。</summary>
        private static Bitmap CacheStore(string key, Bitmap bmp)
        {
            if (bmp == null) return null;
            lock (CacheLock)
            {
                Bitmap exist;
                if (Cache.TryGetValue(key, out exist)) { bmp.Dispose(); return SafeClone(exist); }
                if (Cache.Count >= CacheCap)
                {
                    foreach (Bitmap v in Cache.Values) { try { v.Dispose(); } catch { } }
                    Cache.Clear();
                }
                Cache[key] = bmp;
                return SafeClone(bmp);
            }
        }

        /// <summary>复制位图作为返回值；失败时返回 null（由调用方按占位处理）。</summary>
        private static Bitmap SafeClone(Bitmap src)
        {
            if (src == null) return null;
            try { return new Bitmap(src); }
            catch { return null; }
        }

        /// <summary>
        /// 为单个应用提取最佳图标，返回指定尺寸的 Bitmap（调用方拥有，可安全 Dispose）。
        /// 命中模块级缓存时返回缓存的副本，免去重复抽取；未命中则抽取后入缓存。
        /// </summary>
        public static Bitmap Extract(AppInfo app, int displaySize)
        {
            string key = CacheKey(app, displaySize, false);
            Bitmap hit = CacheTakeClone(key);
            if (hit != null) return hit;
            return CacheStore(key, ExtractCore(app, displaySize));
        }

        private static Bitmap ExtractCore(AppInfo app, int displaySize)
        {
            // 本地图片条目：直接生成内容缩略图（照片等资源分类展示图片本身而非文件类型图标）
            Bitmap thumb = TryLoadImageThumbnail(app.TargetPath, displaySize);
            if (thumb != null) return thumb;

            string host;
            int iconIndex;
            ResolveIconHost(app, out host, out iconIndex);

            // .url 专用取图：优先使用 IconFile=/IconIndex= 声明（.ico 取最佳帧，其余资源按索引提取）
            Bitmap bmp = null;
            string urlIcon;
            int urlIconIndex;
            if (TryResolveUrlIcon(app, out urlIcon, out urlIconIndex))
                bmp = TryLoadFromHost(urlIcon, urlIconIndex);

            if (!IsValid(bmp)) { Dispose(bmp); bmp = TryPrivateExtract(host, iconIndex, 256); }
            if (!IsValid(bmp)) { Dispose(bmp); bmp = TryPrivateExtract(host, iconIndex, 48); }
            if (!IsValid(bmp)) { Dispose(bmp); bmp = TryShGetInfo(host, false); }
            if (!IsValid(bmp) && !string.IsNullOrEmpty(app.ShortcutPath))
            {
                Dispose(bmp);
                bmp = TryShGetInfo(app.ShortcutPath, false);
            }
            // 打包/UWP 应用：按 shell:AppsFolder\<AUMID> 解析名取系统图标
            if (!IsValid(bmp) && Scanner.IsAppsFolderTarget(app.TargetPath))
            {
                Dispose(bmp);
                bmp = TryShGetInfoByParsingName(app.TargetPath, false);
            }
            // 纯网址条目无专属图标时，取系统 Internet 快捷方式图标兜底
            if (!IsValid(bmp) && Scanner.IsUrl(app.TargetPath))
            {
                Dispose(bmp);
                bmp = TryShGetInfoCore("site.url", FILE_ATTRIBUTE_NORMAL, true, false);
            }
            if (!IsValid(bmp)) { Dispose(bmp); bmp = Placeholder(displaySize, app); }

            return ScaleTo(bmp, displaySize);
        }

        /// <summary>
        /// 列表专用小图标：对齐 Windows 资源管理器列表视图——优先直接取 shell 的 16x16 小图标，
        /// 避免把大图标缩放到小尺寸导致发虚/怪异；取不到时回退到常规大图流程再缩放。
        /// </summary>
        public static Bitmap ExtractSmall(AppInfo app, int displaySize)
        {
            string key = CacheKey(app, displaySize, true);
            Bitmap hit = CacheTakeClone(key);
            if (hit != null) return hit;
            return CacheStore(key, ExtractSmallCore(app, displaySize));
        }

        private static Bitmap ExtractSmallCore(AppInfo app, int displaySize)
        {
            Bitmap bmp = null;

            // 本地图片条目：小尺寸同样使用内容缩略图，列表视图与网格视图一致
            Bitmap thumb = TryLoadImageThumbnail(app.TargetPath, displaySize);
            if (thumb != null) return thumb;

            // 打包/UWP 应用：按 shell:AppsFolder\<AUMID> 解析名取小图标
            if (Scanner.IsAppsFolderTarget(app.TargetPath))
                bmp = TryShGetInfoByParsingName(app.TargetPath, true);

            // 真实存在的文件/文件夹/exe：按路径直接取 shell 小图标（目录自动取文件夹图标）
            if (!IsValid(bmp) && !string.IsNullOrEmpty(app.TargetPath)
                && (File.Exists(app.TargetPath) || Directory.Exists(app.TargetPath)))
            {
                Dispose(bmp);
                bmp = TryShGetInfo(app.TargetPath, true);
            }

            // 快捷方式宿主：.lnk/.url 取小图标
            if (!IsValid(bmp) && !string.IsNullOrEmpty(app.ShortcutPath))
            {
                Dispose(bmp);
                bmp = TryShGetInfo(app.ShortcutPath, true);
            }

            if (!IsValid(bmp))
            {
                Dispose(bmp);
                return Extract(app, displaySize);   // 兜底：沿用大图标流程
            }
            return ScaleTo(bmp, displaySize);
        }

        /// <summary>
        /// 本地图片文件取内容缩略图：解码原图后等比缩放到 displaySize 方形画布并居中（透明留白），
        /// 与图标网格布局一致。仅处理图片文件（MediaRules.IsImage），损坏/不支持的格式返回 null 走图标兜底。
        /// 加载期间用独占读共享打开，读完即释放，不长期占用文件句柄。
        /// </summary>
        private static Bitmap TryLoadImageThumbnail(string path, int displaySize)
        {
            try
            {
                if (!MediaRules.IsImage(path) || !File.Exists(path)) return null;
                using (Bitmap src = new Bitmap(path))
                {
                    if (src.Width <= 0 || src.Height <= 0) return null;
                    Bitmap dst = new Bitmap(displaySize, displaySize, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(dst))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.Clear(Color.Transparent);
                        double scale = Math.Min((double)displaySize / src.Width,
                            (double)displaySize / src.Height);
                        int w = Math.Max(1, (int)Math.Round(src.Width * scale));
                        int h = Math.Max(1, (int)Math.Round(src.Height * scale));
                        g.DrawImage(src, new Rectangle((displaySize - w) / 2,
                            (displaySize - h) / 2, w, h));
                    }
                    return dst;
                }
            }
            catch { return null; }   // 损坏文件/非图片内容：回退到常规图标流程
        }

        /// <summary>
        /// 封面缩略图：等比裁剪填满目标尺寸（cover 语义，无留白），带模块级缓存。
        /// 供集合封面（侧边栏缩略图/信息页大图）使用：只解码一次并缓存小图，避免每次重绘都全量解码原图。
        /// 非图片或解码失败返回 null，由调用方走占位绘制。
        /// </summary>
        public static Bitmap LoadCoverThumb(string path, int width, int height)
        {
            return LoadCoverThumb(path, width, height, 0.5F, 0.5F);
        }

        /// <summary>
        /// 带取景焦点的封面缩略图：focusX/focusY 为 0~1 的取景中心（0.5 居中），
        /// 用于集合封面拖动调整展示区域后的稳定渲染。
        /// </summary>
        public static Bitmap LoadCoverThumb(string path, int width, int height, float focusX, float focusY)
        {
            if (string.IsNullOrEmpty(path) || width <= 0 || height <= 0) return null;
            focusX = FocusClamp(focusX);
            focusY = FocusClamp(focusY);
            StringBuilder stamp = new StringBuilder(64);
            AppendFileStamp(stamp, path);
            string key = "C" + width + "x" + height
                + "f" + (int)Math.Round(focusX * 100) + "x" + (int)Math.Round(focusY * 100)
                + "|" + path + "|" + stamp;
            Bitmap hit = CacheTakeClone(key);
            if (hit != null) return hit;
            return CacheStore(key, TryLoadCoverThumbCore(path, width, height, focusX, focusY));
        }

        /// <summary>
        /// 只查缓存不解码：与 LoadCoverThumb 相同的键规则，命中返回副本，未命中返回 null。
        /// 供 UI 线程判断封面是否已就绪；未就绪时由调用方走后台解码，避免在绘制路径同步解码原图。
        /// </summary>
        public static Bitmap TryGetCachedCoverThumb(string path, int width, int height, float focusX, float focusY)
        {
            if (string.IsNullOrEmpty(path) || width <= 0 || height <= 0) return null;
            focusX = FocusClamp(focusX);
            focusY = FocusClamp(focusY);
            StringBuilder stamp = new StringBuilder(64);
            AppendFileStamp(stamp, path);
            string key = "C" + width + "x" + height
                + "f" + (int)Math.Round(focusX * 100) + "x" + (int)Math.Round(focusY * 100)
                + "|" + path + "|" + stamp;
            return CacheTakeClone(key);
        }

        /// <summary>取景焦点夹紧到 0~1（NaN 回退居中）。</summary>
        private static float FocusClamp(float v)
        {
            if (float.IsNaN(v)) return 0.5F;
            if (v < 0F) return 0F;
            if (v > 1F) return 1F;
            return v;
        }

        /// <summary>解码并等比裁剪填满目标尺寸（按取景焦点偏移，默认居中，类似 CSS background-position）。</summary>
        private static Bitmap TryLoadCoverThumbCore(string path, int width, int height, float focusX, float focusY)
        {
            try
            {
                if (!MediaRules.IsImage(path) || !File.Exists(path)) return null;
                using (Bitmap src = new Bitmap(path))
                {
                    if (src.Width <= 0 || src.Height <= 0) return null;
                    double scale = Math.Max((double)width / src.Width, (double)height / src.Height);
                    int dw = Math.Max(1, (int)Math.Round(src.Width * scale));
                    int dh = Math.Max(1, (int)Math.Round(src.Height * scale));
                    int ox = (int)Math.Round((dw - width) * focusX);
                    int oy = (int)Math.Round((dh - height) * focusY);
                    Bitmap dst = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(dst))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(src, new Rectangle(-ox, -oy, dw, dh));
                    }
                    return dst;
                }
            }
            catch { return null; }   // 损坏文件/非图片内容：回退到占位绘制
        }

        /// <summary>
        /// 封面取景原图：按长边上限等比缩放但不裁剪，供编辑态拖动取景使用，带模块级缓存。
        /// 非图片或解码失败返回 null。
        /// </summary>
        public static Bitmap LoadCoverSource(string path, int maxLongEdge)
        {
            if (string.IsNullOrEmpty(path) || maxLongEdge <= 0) return null;
            StringBuilder stamp = new StringBuilder(64);
            AppendFileStamp(stamp, path);
            string key = "S" + maxLongEdge + "|" + path + "|" + stamp;
            Bitmap hit = CacheTakeClone(key);
            if (hit != null) return hit;
            return CacheStore(key, TryLoadCoverSourceCore(path, maxLongEdge));
        }

        /// <summary>解码整图并按长边上限缩放（不裁剪）。</summary>
        private static Bitmap TryLoadCoverSourceCore(string path, int maxLongEdge)
        {
            try
            {
                if (!MediaRules.IsImage(path) || !File.Exists(path)) return null;
                using (Bitmap src = new Bitmap(path))
                {
                    if (src.Width <= 0 || src.Height <= 0) return null;
                    double scale = Math.Min(1.0, (double)maxLongEdge / Math.Max(src.Width, src.Height));
                    int dw = Math.Max(1, (int)Math.Round(src.Width * scale));
                    int dh = Math.Max(1, (int)Math.Round(src.Height * scale));
                    Bitmap dst = new Bitmap(dw, dh, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(dst))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(src, new Rectangle(0, 0, dw, dh));
                    }
                    return dst;
                }
            }
            catch { return null; }
        }

        /// <summary>检测一个 Bitmap 是否为有效图标（非空、有效像素占比 >= 2%）。</summary>
        public static bool IsValid(Bitmap b)
        {
            if (b == null) return false;
            try
            {
                int w = b.Width, h = b.Height;
                if (w <= 0 || h <= 0) return false;
                // 大图按 4 像素网格采样（>=64 按 2），且只把采样行逐行拷入托管数组：
                // 256px 图标不再整图拷贝，热点路径（逐条目校验）的 CPU/内存占用大幅下降
                int step = (w >= 128 || h >= 128) ? 4 : ((w >= 64 || h >= 64) ? 2 : 1);
                long total = 0, valid = 0;
                BitmapData d = b.LockBits(new Rectangle(0, 0, w, h),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int stride = d.Stride;
                    byte[] buf = new byte[w * 4];
                    for (int y = 0; y < h; y += step)
                    {
                        Marshal.Copy(IntPtr.Add(d.Scan0, y * stride), buf, 0, buf.Length);
                        for (int x = 0; x < w; x += step)
                        {
                            int o = x * 4;
                            byte a = buf[o + 3];
                            byte r = buf[o + 2];
                            byte g = buf[o + 1];
                            byte bl = buf[o + 0];
                            total++;
                            if (a < 32) continue;                        // 透明
                            if (r > 245 && g > 245 && bl > 245) continue; // 近白背景
                            valid++;
                        }
                    }
                }
                finally { b.UnlockBits(d); }
                if (total == 0) return false;
                double ratio = (double)valid / (double)total;
                return ratio >= 0.02;
            }
            catch
            {
                return true; // 无法判定时保守认为有效，避免误剔除
            }
        }

        // ===== 私有辅助 =====

        private static void Dispose(Bitmap b) { if (b != null) b.Dispose(); }

        /// <summary>
        /// 解析图标宿主路径与索引：优先注册表 DisplayIcon（可带 ,index），退而取目标 exe。
        /// </summary>
        private static void ResolveIconHost(AppInfo app, out string host, out int index)
        {
            host = string.Empty;
            index = 0;
            if (!string.IsNullOrEmpty(app.DisplayIcon))
            {
                string di = app.DisplayIcon.Trim('"', ' ');
                int comma = di.LastIndexOf(',');
                if (comma >= 0 && comma < di.Length - 1)
                {
                    string idxStr = di.Substring(comma + 1).Trim();
                    int parsed;
                    if (int.TryParse(idxStr, out parsed))
                    {
                        index = parsed;
                        host = di.Substring(0, comma).Trim();
                    }
                    else
                    {
                        host = di;
                    }
                }
                else
                {
                    host = di;
                }
                if (!string.IsNullOrEmpty(host) && File.Exists(host)) return;
                host = string.Empty;
                index = 0;
            }
            if (!string.IsNullOrEmpty(app.TargetPath)
                && (File.Exists(app.TargetPath) || Directory.Exists(app.TargetPath)))
            {
                host = app.TargetPath;
                return;
            }
            host = string.Empty;
        }

        private static Bitmap TryPrivateExtract(string host, int index, int size)
        {
            if (string.IsNullOrEmpty(host) || !File.Exists(host)) return null;
            try
            {
                IntPtr[] hicons = new IntPtr[1];
                uint[] ids = new uint[1];
                uint r = PrivateExtractIcons(host, index, size, size, hicons, ids, 1, 0);
                if (r != 1 || hicons[0] == IntPtr.Zero) return null;
                try { return HIconToBitmap(hicons[0]); }
                finally { DestroyIcon(hicons[0]); }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 用 SHGetFileInfo 取系统大图标：真实存在的路径按其类型取图标（目录取文件夹图标），
        /// 不存在的路径按扩展名兜底。
        /// </summary>
        private static Bitmap TryShGetInfo(string path, bool small)
        {
            if (string.IsNullOrEmpty(path)) return null;
            bool exists = File.Exists(path) || Directory.Exists(path);
            uint attr = Directory.Exists(path) ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
            return TryShGetInfoCore(path, attr, !exists, small);
        }

        private static Bitmap TryShGetInfoCore(string path, uint attributes, bool useFileAttributes, bool small)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                SHFILEINFO fi = new SHFILEINFO();
                uint flags = SHGFI_ICON | (small ? SHGFI_SMALLICON : SHGFI_LARGEICON);
                if (useFileAttributes) flags |= SHGFI_USEFILEATTRIBUTES;
                SHGetFileInfo(path, attributes, ref fi,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags);
                if (fi.hIcon == IntPtr.Zero) return null;
                try { return HIconToBitmap(fi.hIcon); }
                finally { DestroyIcon(fi.hIcon); }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 按 Shell 解析名取图标：用于打包/UWP 应用（target 形如 shell:AppsFolder\&lt;AUMID&gt;）。
        /// SHParseDisplayName 将其解析为 PIDL，再经 SHGetFileInfo(SHGFI_PIDL) 取系统图标（大/小可选）。
        /// </summary>
        private static Bitmap TryShGetInfoByParsingName(string parsingName, bool small)
        {
            if (string.IsNullOrEmpty(parsingName)) return null;
            IntPtr pidl = IntPtr.Zero;
            try
            {
                uint attrs;
                if (SHParseDisplayName(parsingName, IntPtr.Zero, out pidl, 0, out attrs) != 0
                    || pidl == IntPtr.Zero) return null;
                SHFILEINFO fi = new SHFILEINFO();
                SHGetFileInfo(pidl, 0, ref fi,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                    SHGFI_ICON | (small ? SHGFI_SMALLICON : SHGFI_LARGEICON) | SHGFI_PIDL);
                if (fi.hIcon == IntPtr.Zero) return null;
                try { return HIconToBitmap(fi.hIcon); }
                finally { DestroyIcon(fi.hIcon); }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
            }
        }

        /// <summary>解析 .url 文件的 IconFile=/IconIndex= 声明；无有效声明返回 false。</summary>
        private static bool TryResolveUrlIcon(AppInfo app, out string iconFile, out int iconIndex)
        {
            iconFile = string.Empty;
            iconIndex = 0;
            string url = null;
            if (!string.IsNullOrEmpty(app.ShortcutPath)
                && app.ShortcutPath.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                url = app.ShortcutPath;
            else if (!string.IsNullOrEmpty(app.DisplayIcon)
                && app.DisplayIcon.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                url = app.DisplayIcon;
            if (url == null || !File.Exists(url)) return false;

            try
            {
                foreach (string line in File.ReadAllLines(url))
                {
                    string l = line.Trim();
                    if (l.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = l.Substring(9).Trim().Trim('"');
                        int comma = v.LastIndexOf(',');
                        if (comma >= 0 && comma < v.Length - 1)
                        {
                            int idx;
                            if (int.TryParse(v.Substring(comma + 1).Trim(), out idx))
                            {
                                iconIndex = idx;
                                v = v.Substring(0, comma).Trim().Trim('"');
                            }
                        }
                        iconFile = Environment.ExpandEnvironmentVariables(v);
                    }
                    else if (l.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase))
                    {
                        int idx2;
                        if (int.TryParse(l.Substring(10).Trim(), out idx2)) iconIndex = idx2;
                    }
                }
            }
            catch { }
            return !string.IsNullOrEmpty(iconFile) && File.Exists(iconFile);
        }

        /// <summary>从宿主文件加载图标：.ico 取最佳 64px 帧，其余资源用 ExtractIconEx 按索引提取。</summary>
        private static Bitmap TryLoadFromHost(string host, int index)
        {
            if (string.IsNullOrEmpty(host) || !File.Exists(host)) return null;
            if (host.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) return TryLoadIco(host);
            Bitmap b = TryExtractIconEx(host, index);
            if (!IsValid(b)) { Dispose(b); b = TryPrivateExtract(host, index, 256); }
            return b;
        }

        /// <summary>加载 .ico：优先取最接近 64px 的最佳帧，失败回退系统默认帧。</summary>
        private static Bitmap TryLoadIco(string path)
        {
            try
            {
                using (Icon ic = new Icon(path, 64, 64))
                    return FromIcon(ic);
            }
            catch { }
            try
            {
                using (Icon ic = new Icon(path))
                    return FromIcon(ic);
            }
            catch { return null; }
        }

        /// <summary>用 ExtractIconEx 按索引提取大图标（index 为负时按资源 ID 解析）。</summary>
        private static Bitmap TryExtractIconEx(string file, int index)
        {
            try
            {
                IntPtr[] large = new IntPtr[1];
                int n = ExtractIconEx(file, index, large, null, 1);
                if (n <= 0 || large[0] == IntPtr.Zero) return null;
                try { return HIconToBitmap(large[0]); }
                finally { DestroyIcon(large[0]); }
            }
            catch
            {
                return null;
            }
        }

        private static Bitmap HIconToBitmap(IntPtr hicon)
        {
            if (hicon == IntPtr.Zero) return null;
            try
            {
                using (Icon ic = Icon.FromHandle(hicon))
                    return FromIcon(ic);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Icon 转 32bpp ARGB 位图，保留透明通道。
        /// Bitmap.FromHicon / Icon.ToBitmap 在 .NET Framework 下会丢弃 alpha，
        /// 使透明区域渲染为黑色，表现为图标带黑底/黑框；改为绘制到透明位图可避免。
        /// </summary>
        private static Bitmap FromIcon(Icon ic)
        {
            if (ic == null) return null;
            try
            {
                int w = ic.Width, h = ic.Height;
                if (w <= 0 || h <= 0) return null;
                Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.DrawIcon(ic, new Rectangle(0, 0, w, h));
                }
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static Bitmap ScaleTo(Bitmap src, int size)
        {
            if (src == null) return null;
            if (src.Width == size && src.Height == size) return src;
            try
            {
                double scale = (double)size / (double)Math.Max(src.Width, src.Height);
                int dw = (int)Math.Round(src.Width * scale);
                int dh = (int)Math.Round(src.Height * scale);
                if (dw < 1) dw = 1;
                if (dh < 1) dh = 1;
                Bitmap r = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(r))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    int ox = (size - dw) / 2, oy = (size - dh) / 2;
                    g.DrawImage(src, new Rectangle(ox, oy, dw, dh));
                }
                src.Dispose();
                return r;
            }
            catch
            {
                return src;
            }
        }

        /// <summary>占位图标：强调色圆角方块 + 名称首字母。</summary>
        private static Bitmap Placeholder(int size, AppInfo app)
        {
            Bitmap b = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (GraphicsPath p = Draw.RoundedPath(new Rectangle(2, 2, size - 4, size - 4), 12))
                    using (Brush br = new SolidBrush(Theme.Accent))
                    {
                        g.FillPath(br, p);
                    }
                    string ch = string.IsNullOrEmpty(app.Name) ? "?" : app.Name.Substring(0, 1);
                    try { ch = ch.ToUpperInvariant(); } catch { }
                    // 字体来自 Theme 缓存（应用生命周期复用），不得释放
                    Font f = Theme.FontPx(size * 0.45f, FontStyle.Bold);
                    using (StringFormat sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        g.DrawString(ch, f, Brushes.White, new Rectangle(0, 0, size, size), sf);
                    }
                }
            }
            catch
            {
                // 占位绘制失败时返回空白图，不抛
            }
            return b;
        }
    }
}
