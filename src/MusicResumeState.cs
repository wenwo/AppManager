using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using AppManager.Controls;

namespace AppManager
{
    /// <summary>
    /// 音乐播放器退出时的状态：播放列表、当前曲目、进度、播放模式、音量与静音，下次启动时恢复。
    /// 单独存 music-state.xml，不进配置库，避免每次退出都多出一条操作记录。
    /// 写入走 .tmp + 原子替换，失败保留上一次的文件；读取失败只记日志，按没有记录启动。
    /// </summary>
    internal sealed class MusicResumeState
    {
        /// <summary>最多记住的曲目数；超出时以当前曲目为中心截取，避免状态文件过大拖慢退出和启动。</summary>
        internal const int MaxTracks = 20000;
        private const int FormatVersion = 1;

        public List<string> Tracks = new List<string>();
        public int Index = -1;
        public double Position, Duration;
        public PlaybackMode Mode = PlaybackMode.Sequential;
        public int Volume = 60;
        public bool Muted;
        public string CollectionKey;

        internal static string FilePath { get { return Path.Combine(Paths.DataDir, "music-state.xml"); } }

        /// <summary>从当前会话取状态；播放失败或已播完的曲目从头开始，不记进度。</summary>
        internal static MusicResumeState Capture(MusicPlaybackSession session, string collectionKey)
        {
            MusicResumeState state = new MusicResumeState();
            if (session == null) return state;
            state.Mode = session.Mode;
            state.Volume = session.Volume;
            state.Muted = session.Muted;
            List<string> all = session.PathsSnapshot();
            int current = session.Index;
            if (all.Count == 0 || current < 0 || current >= all.Count) return state;

            int start = 0, count = all.Count;
            if (count > MaxTracks)
            {
                start = Math.Max(0, Math.Min(current - MaxTracks / 2, all.Count - MaxTracks));
                count = MaxTracks;
            }
            bool currentKept = false;
            for (int i = start; i < start + count; i++)
            {
                // 含 XML 不允许字符的路径无法写入，跳过它而不是让整份状态写入失败
                if (!IsWritable(all[i])) continue;
                if (i == current) { state.Index = state.Tracks.Count; currentKept = true; }
                else if (!currentKept && i > current && state.Index < 0) state.Index = state.Tracks.Count;
                state.Tracks.Add(all[i]);
            }
            if (state.Tracks.Count == 0) { state.Index = -1; return state; }
            if (state.Index < 0) state.Index = 0;
            bool resumable = currentKept && string.IsNullOrEmpty(session.Failure) && !session.Ended;
            state.Position = resumable ? Math.Max(0, session.ResumePosition) : 0;
            state.Duration = resumable ? Math.Max(0, session.Duration) : 0;
            state.CollectionKey = collectionKey;
            return state;
        }

        /// <summary>原子写入状态文件；失败抛出，由调用方记日志，原文件保持不变。</summary>
        internal void Save(string path)
        {
            string tmp = path + ".tmp";
            XmlWriterSettings settings = new XmlWriterSettings();
            settings.Indent = false;
            settings.Encoding = new UTF8Encoding(false);
            try
            {
                using (XmlWriter w = XmlWriter.Create(tmp, settings))
                {
                    w.WriteStartDocument();
                    w.WriteStartElement("music");
                    w.WriteAttributeString("version", FormatVersion.ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("mode", Mode.ToString());
                    w.WriteAttributeString("volume", Volume.ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("muted", Muted ? "1" : "0");
                    if (Tracks.Count > 0)
                    {
                        w.WriteAttributeString("index", Index.ToString(CultureInfo.InvariantCulture));
                        w.WriteAttributeString("position", Position.ToString("R", CultureInfo.InvariantCulture));
                        w.WriteAttributeString("duration", Duration.ToString("R", CultureInfo.InvariantCulture));
                        // 空字符串是「全部音乐」，也要写，否则读回来分不清「全部音乐」和「没有来源集合」
                        if (CollectionKey != null && (CollectionKey.Length == 0 || IsWritable(CollectionKey)))
                            w.WriteAttributeString("collection", CollectionKey);
                    }
                    foreach (string track in Tracks)
                    {
                        w.WriteStartElement("track");
                        w.WriteAttributeString("path", track);
                        w.WriteEndElement();
                    }
                    w.WriteEndElement();
                    w.WriteEndDocument();
                }
                if (File.Exists(path)) Paths.ReplaceFile(tmp, path);
                else File.Move(tmp, path);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception cleanup) { Paths.LogError(cleanup); }
                throw;
            }
        }

        /// <summary>当前曲目不在时最多往后找几首；全都找不到多半是所在的盘没连上，原样保留。</summary>
        private const int MaxMissingProbe = 50;

        /// <summary>
        /// 读取并校验状态文件。列表只按路径格式过滤，不因文件暂时不在而删掉曲目（移动盘、网络盘没连上时，
        /// 删掉后退出会把列表永久写短）。只检查当前曲目：不在了就往后找第一首还在的、进度归零；
        /// 往后都找不到就原样保留。会访问磁盘，必须在后台线程调用。文件不存在返回 null；格式错误抛出。
        /// </summary>
        internal static MusicResumeState Load(string path)
        {
            if (!File.Exists(path)) return null;
            XmlElement root = SafeXml.Load(path).DocumentElement;
            if (root == null || root.Name != "music") throw new InvalidDataException("音乐播放状态格式不正确：" + path);
            int version;
            if (!int.TryParse(root.GetAttribute("version"), NumberStyles.Integer, CultureInfo.InvariantCulture, out version)
                || version != FormatVersion)
                throw new InvalidDataException("音乐播放状态版本不受支持：" + path);

            MusicResumeState state = new MusicResumeState();
            PlaybackMode mode;
            if (Enum.TryParse(root.GetAttribute("mode"), false, out mode) && Enum.IsDefined(typeof(PlaybackMode), mode))
                state.Mode = mode;
            int volume;
            if (int.TryParse(root.GetAttribute("volume"), NumberStyles.Integer, CultureInfo.InvariantCulture, out volume))
                state.Volume = Math.Max(0, Math.Min(100, volume));
            state.Muted = root.GetAttribute("muted") == "1";

            int savedIndex;
            if (!int.TryParse(root.GetAttribute("index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out savedIndex))
                savedIndex = 0;
            double position = ReadSeconds(root, "position"), duration = ReadSeconds(root, "duration");
            int ordinal = 0;
            bool currentFound = false;
            foreach (XmlNode node in root.SelectNodes("track"))
            {
                XmlElement el = node as XmlElement;
                if (el == null) continue;
                int at = ordinal++;
                if (state.Tracks.Count >= MaxTracks) break;
                string track = el.GetAttribute("path");
                if (!IsValidTrackPath(track)) continue;
                if (at == savedIndex) { state.Index = state.Tracks.Count; currentFound = true; }
                else if (!currentFound && at > savedIndex && state.Index < 0) state.Index = state.Tracks.Count;
                state.Tracks.Add(track);
            }
            if (state.Tracks.Count == 0) { state.Index = -1; return state; }
            if (state.Index < 0) state.Index = 0;
            if (currentFound)
            {
                state.Position = position;
                state.Duration = duration;
            }
            if (!Exists(state.Tracks[state.Index]))
            {
                int limit = Math.Min(state.Tracks.Count, state.Index + 1 + MaxMissingProbe);
                for (int i = state.Index + 1; i < limit; i++)
                {
                    if (!Exists(state.Tracks[i])) continue;
                    state.Index = i;
                    state.Position = state.Duration = 0;
                    break;
                }
            }
            state.CollectionKey = root.HasAttribute("collection") ? root.GetAttribute("collection") : null;
            return state;
        }

        private static double ReadSeconds(XmlElement root, string name)
        {
            double value;
            if (!double.TryParse(root.GetAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return 0;
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) return 0;
            return value;
        }

        /// <summary>只接受完整路径的音频文件：状态文件可能被改动，不能把网址、设备路径或其它类型交给播放内核。</summary>
        private static bool IsValidTrackPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.IndexOf('\0') >= 0) return false;
            // 设备命名空间（\\.\）和备用数据流（盘符之后再出现冒号）都不是普通音乐文件
            if (path.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;
            string body = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.Substring(4) : path;
            if (body.Length > 2 && body.IndexOf(':', 2) >= 0) return false;
            // 只认完整路径：盘符加分隔符或 UNC；C:a.mp3、\a.mp3 这类随当前目录变化的路径不接受
            bool driveRooted = body.Length >= 3 && char.IsLetter(body[0]) && body[1] == ':' && (body[2] == '\\' || body[2] == '/');
            if (!driveRooted && !path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            try
            {
                if (path.IndexOf("://", StringComparison.Ordinal) >= 0) return false;
                return MediaRules.IsAudio(path);
            }
            catch (ArgumentException) { return false; }
        }

        private static bool Exists(string path)
        {
            try { return File.Exists(path); }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
        }

        private static bool IsWritable(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            try { XmlConvert.VerifyXmlChars(value); return true; }
            catch (XmlException) { return false; }
        }
    }
}
