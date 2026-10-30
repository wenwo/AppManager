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
        /// <summary>图片扩展名（小写、含点）。</summary>
        public static readonly string[] ImageExt =
        {
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"
        };

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
            ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md",
            ".rtf", ".csv", ".json", ".xml", ".html", ".htm", ".epub"
        };

        /// <summary>可在内置文本查看器中直接读取的纯文本扩展名。</summary>
        public static readonly string[] TextExt =
        {
            ".txt", ".md", ".log", ".ini", ".cfg", ".conf", ".csv", ".json", ".xml",
            ".yaml", ".yml", ".toml", ".sql", ".html", ".htm", ".css", ".js", ".ts",
            ".cs", ".java", ".py", ".sh", ".bat", ".cmd", ".ps1", ".properties"
        };

        private static readonly string[] PdfExt = { ".pdf" };

        /// <summary>压缩包扩展名（小写、含点）。</summary>
        public static readonly string[] ArchiveExt =
        {
            ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".tgz"
        };

        /// <summary>证件扩展名：证件照、证件文档与文本。</summary>
        private static readonly string[] CredentialExt = BuildCredentialExt();

        private static string[] BuildCredentialExt()
        {
            List<string> list = new List<string>(ImageExt);
            foreach (string ext in DocExt) if (!list.Contains(ext)) list.Add(ext);
            foreach (string ext in TextExt) if (!list.Contains(ext)) list.Add(ext);
            return list.ToArray();
        }

        /// <summary>是否为本地图片文件（按扩展名判定）。</summary>
        public static bool IsImage(string path)
        {
            return MatchExt(path, ImageExt);
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

        /// <summary>是否为任一类型的本地媒体文件（图片/视频/音频）。</summary>
        public static bool IsMedia(string path)
        {
            return IsImage(path) || IsVideo(path) || IsAudio(path);
        }

        private static bool MatchExt(string path, string[] exts)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = path.ToLowerInvariant();
            for (int i = 0; i < exts.Length; i++)
                if (p.EndsWith(exts[i], StringComparison.Ordinal)) return true;
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
                case "照片": return ImageExt;
                case "视频": return VideoExt;
                case "音乐": return AudioExt;
                case "文档": return DocExt;
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
