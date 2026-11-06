using System;
using System.Collections.Generic;
using System.IO;

namespace AppManager
{
    internal enum MusicFilterReason
    {
        Accepted,
        AlreadyIncluded,
        Excluded,
        TooSmall,
        CloudPlaceholder,
        ProbeFailed,
        ProbeTimeout,
        ProbeUnavailable,
        NoAudioStream,
        TooShort,
        NonSongGenre,
        NonSongDirectory,
        VoiceQuality,
        Mono,
        LongUntagged,
        Unconfirmed,
        NotProbed
    }

    internal sealed class MusicProbeInfo
    {
        public string Path = "";
        public bool HasAudioStream;
        public double DurationSeconds;
        public int Channels;
        public int SampleRate;
        public string Artist = "";
        public string Album = "";
        public string Genre = "";
        public string Title = "";
        public bool Failed;
        public bool TimedOut;
        public bool Unavailable;
        public string Error = "";
    }

    internal sealed class MusicFilterDecision
    {
        public bool Accepted;
        public MusicFilterReason Reason;

        internal static MusicFilterDecision Accept()
        {
            return new MusicFilterDecision { Accepted = true, Reason = MusicFilterReason.Accepted };
        }

        internal static MusicFilterDecision Reject(MusicFilterReason reason)
        {
            return new MusicFilterDecision { Accepted = false, Reason = reason };
        }
    }

    /// <summary>
    /// 可解释的歌曲判定：只使用时长、音频轨、声道/采样率和标签，不按文件名猜测。
    /// 规则顺序固定在 Classify 中，命中即停，便于 UI 统计和回归测试。
    /// </summary>
    internal static class MusicFileClassifier
    {
        internal const double MinimumDurationSeconds = 30;
        internal const double LongUntaggedSeconds = 20 * 60;
        internal const int MinimumStereoSampleRate = 32000;
        internal const int VoiceSampleRateCeiling = 16000;

        private const int FileAttributeRecallOnOpen = 0x40000;
        private const int FileAttributeRecallOnDataAccess = 0x400000;

        private static readonly string[] NonSongGenreTokens =
        {
            "podcast", "podcasts", "audiobook", "audiobooks", "audio book", "audio books",
            "spoken word", "spoken words", "speech", "speeches",
            "sound effect", "sound effects", "sfx", "ringtone", "ringtones",
            "notification", "notifications", "alarm", "alarms", "voice memo", "voice memos",
            "播客", "有声书", "语音", "录音", "朗读", "提示音", "铃声", "音效"
        };

        private static readonly HashSet<string> NonSongDirectoryNames =
            new HashSet<string>(new string[]
            {
                "Ringtones", "Notifications", "Alarms", "Recordings",
                "Podcasts", "Audiobooks", "Voice Memos",
                "录音", "铃声", "提示音", "播客", "有声书"
            }, StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> PlaceholderTags =
            new HashSet<string>(new string[]
            {
                "unknown", "unknown artist", "unknown album", "unknown artist/unknown album",
                "<unknown>", "n/a", "none", "null", "未知", "未知艺术家", "未知专辑"
            }, StringComparer.OrdinalIgnoreCase);

        internal static MusicFilterDecision Classify(string path, MusicProbeInfo probe, IEnumerable<string> musicRoots)
        {
            if (probe == null || !probe.HasAudioStream)
                return MusicFilterDecision.Reject(MusicFilterReason.NoAudioStream);

            if (double.IsNaN(probe.DurationSeconds) || double.IsInfinity(probe.DurationSeconds)
                || probe.DurationSeconds < MinimumDurationSeconds)
                return MusicFilterDecision.Reject(MusicFilterReason.TooShort);

            if (ContainsNonSongGenre(probe.Genre))
                return MusicFilterDecision.Reject(MusicFilterReason.NonSongGenre);

            if (HasNonSongDirectory(path))
                return MusicFilterDecision.Reject(MusicFilterReason.NonSongDirectory);

            if (probe.Channels == 1 && probe.SampleRate > 0 && probe.SampleRate <= VoiceSampleRateCeiling)
                return MusicFilterDecision.Reject(MusicFilterReason.VoiceQuality);

            bool hasTag = HasMeaningfulTag(probe.Artist) || HasMeaningfulTag(probe.Album);
            if (hasTag) return MusicFilterDecision.Accept();

            if (probe.Channels == 1)
                return MusicFilterDecision.Reject(MusicFilterReason.Mono);

            if (probe.DurationSeconds > LongUntaggedSeconds)
                return MusicFilterDecision.Reject(MusicFilterReason.LongUntagged);

            if (probe.Channels >= 2 && probe.SampleRate >= MinimumStereoSampleRate)
                return MusicFilterDecision.Accept();

            if (IsUnderMusicRoot(path, musicRoots))
                return MusicFilterDecision.Accept();

            return MusicFilterDecision.Reject(MusicFilterReason.Unconfirmed);
        }

        internal static bool HasMeaningfulTag(string value)
        {
            string text = NormalizeTag(value);
            return text.Length > 0 && !PlaceholderTags.Contains(text);
        }

        /// <summary>
        /// 云端占位文件：Offline、RECALL_ON_OPEN、RECALL_ON_DATA_ACCESS 任一命中都可能在打开时触发下载。
        /// 只读属性/文件大小时不会触发下载，必须在 loadfile 前判断。
        /// </summary>
        internal static bool IsCloudPlaceholder(FileAttributes attributes)
        {
            int value = (int)attributes;
            return (value & ((int)FileAttributes.Offline | FileAttributeRecallOnOpen | FileAttributeRecallOnDataAccess)) != 0;
        }

        private static string NormalizeTag(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string text = value.Trim('\0', ' ', '\t', '\r', '\n', '\uFEFF');
            while (text.IndexOf("  ", StringComparison.Ordinal) >= 0)
                text = text.Replace("  ", " ");
            return text.ToLowerInvariant();
        }

        private static bool ContainsNonSongGenre(string genre)
        {
            string text = NormalizeTag(genre);
            if (text.Length == 0) return false;
            string[] parts = text.Split(new char[] { ';', ',', '|', '/', '+', '&' },
                StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < NonSongGenreTokens.Length; i++)
                for (int j = 0; j < parts.Length; j++)
                    if (TokenMatches(parts[j].Trim(), NonSongGenreTokens[i])) return true;
            return false;
        }

        private static bool TokenMatches(string value, string token)
        {
            if (string.Equals(value, token, StringComparison.OrdinalIgnoreCase)) return true;
            return value.Length > token.Length
                && value.StartsWith(token, StringComparison.OrdinalIgnoreCase)
                && char.IsWhiteSpace(value[token.Length]);
        }

        private static bool HasNonSongDirectory(string path)
        {
            string[] parts = PathParts(path);
            for (int i = 0; i < parts.Length; i++)
                if (NonSongDirectoryNames.Contains(parts[i])) return true;
            return false;
        }

        internal static string[] PathParts(string path)
        {
            if (string.IsNullOrEmpty(path)) return new string[0];
            try
            {
                string directory = Path.GetDirectoryName(path) ?? "";
                return directory.Split(new char[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries);
            }
            catch { return new string[0]; }
        }

        internal static bool IsUnderMusicRoot(string path, IEnumerable<string> roots)
        {
            if (string.IsNullOrEmpty(path) || roots == null) return false;
            foreach (string root in roots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                try { if (FileImport.Within(root, path)) return true; }
                catch { }
            }
            return false;
        }
    }
}
