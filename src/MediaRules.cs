using System;

namespace AppManager
{
    /// <summary>
    /// 本地媒体文件类型识别：资源模式下「照片/视频/音乐」分类只收对应类型的本地文件
    /// （登记引用、不搬运原文件），网址条目归「书签」与「全部资源」。
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
            ".mp3", ".wav", ".flac", ".m4a", ".ogg", ".wma", ".aac"
        };

        /// <summary>是否为本地图片文件（按扩展名判定）。</summary>
        public static bool IsImage(string path)
        {
            return MatchExt(path, ImageExt);
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
    }
}
