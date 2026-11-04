using System;
using System.Collections.Generic;

namespace AppManager
{
    /// <summary>
    /// 本地媒体文件类型识别：资源模式下的照片/视频/音乐等分类可收纳对应类型的本地文件，
    /// 入库时移动到资源存储位置（原文件不保留）。
    /// </summary>
    internal static class MediaRules
    {
        /// <summary>普通图片扩展名（小写、含点）。</summary>
        private static readonly string[] PlainImageExt =
        {
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"
        };

        /// <summary>需要系统 WIC RAW 解码器的图片扩展名（小写、含点）。</summary>
        public static readonly string[] RawImageExt = { ".rw2" };

        /// <summary>图片扩展名（小写、含点），包含依赖可选 WIC 解码器的 RAW 格式。</summary>
        public static readonly string[] ImageExt = Merge(PlainImageExt, RawImageExt);

        /// <summary>视频扩展名（小写、含点）。</summary>
        public static readonly string[] VideoExt =
        {
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v"
        };

        /// <summary>音频扩展名（小写、含点）。</summary>
        public static readonly string[] AudioExt =
        {
            ".mp3", ".wav", ".flac", ".m4a", ".ogg", ".wma", ".aac", ".ape",
            ".opus", ".aiff", ".aif", ".alac", ".dsf", ".dff"
        };

        /// <summary>文档扩展名（小写、含点）。</summary>
        public static readonly string[] DocExt =
        {
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".markdown",
            ".rtf", ".csv", ".json", ".xml", ".html", ".htm", ".epub"
        };

        /// <summary>可在内置文本查看器中直接读取的纯文本扩展名。</summary>
        public static readonly string[] TextExt =
        {
            ".txt", ".md", ".markdown", ".log", ".ini", ".cfg", ".conf", ".csv", ".json", ".xml",
            ".yaml", ".yml", ".toml", ".sql", ".html", ".htm", ".css", ".js", ".ts",
            ".cs", ".java", ".py", ".sh", ".bat", ".cmd", ".ps1", ".properties"
        };

        private static readonly string[] PdfExt = { ".pdf" };

        private static readonly string[] MarkdownExt = { ".md", ".markdown" };

        /// <summary>压缩包扩展名（小写、含点）。</summary>
        public static readonly string[] ArchiveExt =
        {
            ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".tgz", ".zst", ".cab", ".lzh"
        };

        // ===== 资源分类接收的类型 =====
        // 分类可以收下内置查看器打不开的格式（双击交给系统默认程序），因此与查看器使用的
        // ImageExt / VideoExt / DocExt 分开维护：这里加格式不会让查看器去解码它不支持的文件。
        // 视频不收 .ts：它同时是 TypeScript 文本扩展名，会被文本查看器打开。

        /// <summary>「照片」接收：查看器图片格式 + iPhone 照片（HEIC/HEIF）、扫描件（TIFF）、AVIF、SVG。</summary>
        private static readonly string[] PhotoCategoryExt =
            Merge(ImageExt, new[] { ".heic", ".heif", ".tif", ".tiff", ".avif", ".svg" });

        /// <summary>「视频」接收：播放器常见格式 + 蓝光/DV 流、MPEG、手机 3GP、RMVB。</summary>
        private static readonly string[] VideoCategoryExt =
            Merge(VideoExt, new[] { ".m2ts", ".mts", ".mpg", ".mpeg", ".3gp", ".rmvb" });

        /// <summary>「文档」接收：常见文档 + WPS（wps/et/dps）、OFD 电子发票/证照、OpenDocument、XPS。</summary>
        private static readonly string[] DocCategoryExt =
            Merge(DocExt, new[] { ".wps", ".et", ".dps", ".ofd", ".odt", ".ods", ".odp", ".xps" });

        /// <summary>「证件」接收：证件照与证件文档（不含脚本、代码等纯文本类型）。</summary>
        private static readonly string[] CredentialExt = Merge(PhotoCategoryExt, DocCategoryExt);

        private static string[] Merge(string[] first, string[] second)
        {
            List<string> list = new List<string>(first);
            foreach (string ext in second) if (!list.Contains(ext)) list.Add(ext);
            return list.ToArray();
        }

        /// <summary>是否为本地图片文件（按扩展名判定）。</summary>
        public static bool IsImage(string path)
        {
            return MatchExt(path, ImageExt);
        }

        /// <summary>是否为依赖系统 WIC RAW 解码器的图片；这些文件不走 GDI+ 解码路径。</summary>
        public static bool IsRawImage(string path)
        {
            return MatchExt(path, RawImageExt);
        }

        /// <summary>是否为 PDF 文件；PDF 在资源查看器中按文档画布模式打开。</summary>
        public static bool IsPdf(string path)
        {
            return MatchExt(path, PdfExt);
        }

        /// <summary>是否为可在内置文本查看器中读取的文本文件。</summary>
        public static bool IsText(string path)
        {
            return MatchExt(path, TextExt);
        }

        /// <summary>是否为 Markdown 文本；文本查看器为其提供渲染预览与纯文本两种视图。</summary>
        public static bool IsMarkdown(string path)
        {
            return MatchExt(path, MarkdownExt);
        }

        /// <summary>图片查看器画布支持的图片类资源（图片与 PDF）。</summary>
        public static bool IsImageDocument(string path)
        {
            return IsImage(path) || IsPdf(path);
        }

        /// <summary>是否为本地视频文件（按扩展名判定）。</summary>
        public static bool IsVideo(string path)
        {
            return MatchExt(path, VideoExt);
        }

        /// <summary>是否为本地音频文件（按扩展名判定）。</summary>
        public static bool IsAudio(string path)
        {
            return MatchExt(path, AudioExt);
        }

        /// <summary>是否为压缩包；资源中的压缩包由内置查看器列出内容。</summary>
        public static bool IsArchive(string path)
        {
            return MatchExt(path, ArchiveExt);
        }

        /// <summary>是否为任一类型的本地媒体文件（图片/视频/音频）。</summary>
        public static bool IsMedia(string path)
        {
            return IsImage(path) || IsVideo(path) || IsAudio(path);
        }

        private static bool MatchExt(string path, string[] exts)
        {
            if (string.IsNullOrEmpty(path)) return false;
            for (int i = 0; i < exts.Length; i++)
                if (path.EndsWith(exts[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ===== 资源分类的类型约束 =====

        /// <summary>
        /// 资源内置分类允许的文件扩展名；返回 null 表示不限制类型
        /// （「文件」分类与用户自建资源分类）。
        /// </summary>
        public static string[] ResourceCategoryExt(string category)
        {
            switch (category)
            {
                case "照片": return PhotoCategoryExt;
                case "视频": return VideoCategoryExt;
                case "音乐": return AudioExt;
                case "文档": return DocCategoryExt;
                case "证件": return CredentialExt;
                case "压缩包": return ArchiveExt;
                default: return null;
            }
        }

        /// <summary>资源分类的类型名称：用于添加菜单文案与对话框标题。</summary>
        public static string ResourceCategoryTypeName(string category)
        {
            switch (category)
            {
                case "照片": return "照片";
                case "视频": return "视频";
                case "音乐": return "音乐";
                case "文档": return "文档";
                case "证件": return "证件";
                case "压缩包": return "压缩包";
                case "文件": return "文件";
                default: return "文件";
            }
        }

        /// <summary>文件是否属于该资源分类允许的类型；分类不限制类型时恒为 true。</summary>
        public static bool MatchCategoryExt(string category, string path)
        {
            string[] exts = ResourceCategoryExt(category);
            if (exts == null) return true;
            return MatchExt(path, exts);
        }

        /// <summary>
        /// 条目能否放进该资源分类：文件按分类类型约束；文件夹整体收纳，不按扩展名判断。
        /// 导入、粘贴、拖放、移动到分类、加入集合都以此为准，避免各入口规则不一致。
        /// </summary>
        public static bool FitsCategory(string category, string path, bool folder)
        {
            return folder || MatchCategoryExt(category, path);
        }

        /// <summary>可按文件类型自动推荐的资源分类：「证件」同时收图片和文档、「文件」不限类型，都不作为推荐。</summary>
        public static readonly string[] SuggestedResourceCategories = { "照片", "视频", "音乐", "文档", "压缩包" };

        /// <summary>单个条目按类型推荐的资源分类：文件匹配第一个类型分类，文件夹与其余文件归「文件」。</summary>
        public static string SuggestResourceCategory(string path, bool folder)
        {
            if (!folder)
                foreach (string category in SuggestedResourceCategories)
                    if (MatchCategoryExt(category, path)) return category;
            return "文件";
        }

        /// <summary>类型不符时的统一提示：「照片」只接收照片类型的文件。</summary>
        public static string CategoryTypeHint(string category)
        {
            return "「" + category + "」只接收" + ResourceCategoryTypeName(category) + "类型的文件";
        }

        /// <summary>分类支持的文件扩展名列表，用于格式不符时明确提示。</summary>
        public static string CategorySupportedFormats(string category)
        {
            string[] exts = ResourceCategoryExt(category);
            if (exts == null) return "所有文件";
            return string.Join("、", exts);
        }

        /// <summary>只含内置解码器能打开的图片（集合封面等需要应用自己绘制的场景），不含 HEIC/SVG 等分类可收但无法解码的格式。</summary>
        public static string DecodableImageFilter()
        {
            string patterns = string.Join(";", Array.ConvertAll(ImageExt, e => "*" + e));
            return "图片 (" + patterns + ")|" + patterns;
        }

        /// <summary>选文件对话框的过滤器：按资源分类限定类型，避免选中不符类型的文件。
        /// 模式必须带 * 通配符（*.mp4），缺少则对话框匹配不到任何文件。</summary>
        public static string ResourceCategoryFilter(string category)
        {
            string[] exts = ResourceCategoryExt(category);
            if (exts == null) return "所有文件 (*.*)|*.*";
            string[] wildcards = new string[exts.Length];
            for (int i = 0; i < exts.Length; i++) wildcards[i] = "*" + exts[i];
            string patterns = string.Join(";", wildcards);
            return ResourceCategoryTypeName(category) + " (" + patterns + ")|" + patterns;
        }

    }
}
