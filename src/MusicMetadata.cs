using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;

namespace AppManager
{
    internal sealed class MusicTrackInfo
    {
        public string Title;
        public string Artist;
        public string Album;
        public string Lyrics;
        public Image Cover;

        public void Dispose()
        {
            if (Cover == null) return;
            Cover.Dispose();
            Cover = null;
        }
    }

    internal struct LyricLine
    {
        public double Time;
        public string Text;
    }

    internal static class MusicMetadata
    {
        private const int MaxMetadataBytes = 8 * 1024 * 1024;
        internal const int MaxLyricsChars = 1024 * 1024;

        public static MusicTrackInfo Read(string path)
        {
            MusicTrackInfo info = new MusicTrackInfo();
            info.Title = Path.GetFileNameWithoutExtension(path);
            info.Artist = "未知艺术家";
            info.Album = "本地音乐";
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".mp3") ReadMp3(path, info);
                else if (ext == ".flac") ReadFlac(path, info);
                string lyricPath = Path.ChangeExtension(path, ".lrc");
                if (File.Exists(lyricPath)) info.Lyrics = ReadText(lyricPath);
            }
            catch { info.Dispose(); }
            return info;
        }

        public static List<LyricLine> ParseLyrics(string text)
        {
            List<LyricLine> result = new List<LyricLine>();
            if (string.IsNullOrEmpty(text)) return result;
            string[] lines = text.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int cursor = 0;
                List<double> stamps = new List<double>();
                while (cursor < line.Length && line[cursor] == '[')
                {
                    int end = line.IndexOf(']', cursor + 1);
                    if (end < 0) break;
                    double time;
                    if (TryTimestamp(line.Substring(cursor + 1, end - cursor - 1), out time)) stamps.Add(time);
                    cursor = end + 1;
                }
                if (stamps.Count == 0) continue;
                string lyric = cursor < line.Length ? line.Substring(cursor).Trim() : "";
                for (int j = 0; j < stamps.Count; j++)
                    result.Add(new LyricLine { Time = stamps[j], Text = lyric });
            }
            result.Sort(delegate(LyricLine a, LyricLine b) { return a.Time.CompareTo(b.Time); });
            return result;
        }

        private static bool TryTimestamp(string text, out double seconds)
        {
            seconds = 0;
            string[] parts = text.Split(':');
            if (parts.Length != 2) return false;
            double minutes;
            double rest;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out minutes)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out rest)) return false;
            if (minutes < 0 || rest < 0) return false;
            seconds = minutes * 60 + rest;
            return true;
        }

        private static string ReadText(string path)
        {
            using (StreamReader reader = new StreamReader(path, Encoding.UTF8, true))
            {
                StringBuilder text = new StringBuilder();
                char[] buffer = new char[4096];
                while (text.Length < MaxLyricsChars)
                {
                    int count = reader.Read(buffer, 0, Math.Min(buffer.Length, MaxLyricsChars - text.Length));
                    if (count <= 0) break;
                    text.Append(buffer, 0, count);
                }
                return text.ToString();
            }
        }

        private static void ReadMp3(string path, MusicTrackInfo info)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (stream.Length >= 10)
                {
                    byte[] header = ReadBytes(stream, 10);
                    if (Ascii(header, 0, 3) == "ID3")
                    {
                        int version = header[3];
                        int size = SyncSafe(header, 6);
                        byte[] tag = ReadBytes(stream, Math.Min(size, MaxMetadataBytes));
                        ReadId3Frames(tag, version, info);
                    }
                }
                if (stream.Length >= 128)
                {
                    stream.Seek(-128, SeekOrigin.End);
                    byte[] tag = ReadBytes(stream, 128);
                    if (Ascii(tag, 0, 3) == "TAG")
                    {
                        if (info.Title == Path.GetFileNameWithoutExtension(path)) info.Title = CleanTag(Decode(Encoding.Default, tag, 3, 30));
                        if (info.Artist == "未知艺术家") info.Artist = CleanTag(Decode(Encoding.Default, tag, 33, 30));
                        if (info.Album == "本地音乐") info.Album = CleanTag(Decode(Encoding.Default, tag, 63, 30));
                    }
                }
            }
            Fallback(info, path);
        }

        private static void ReadId3Frames(byte[] tag, int version, MusicTrackInfo info)
        {
            int offset = 0;
            int headerSize = version == 2 ? 6 : 10;
            while (offset + headerSize <= tag.Length)
            {
                string id = Ascii(tag, offset, version == 2 ? 3 : 4);
                if (id.Trim('\0', ' ') == "") break;
                int size = version >= 4 ? SyncSafe(tag, offset + 4)
                    : version == 3 ? BE32(tag, offset + 4) : BE24(tag, offset + 3);
                int dataOffset = offset + headerSize;
                if (size <= 0 || dataOffset + size > tag.Length) break;
                if (id == "TIT2" || id == "TT2") info.Title = Id3Text(tag, dataOffset, size);
                else if (id == "TPE1" || id == "TP1") info.Artist = Id3Text(tag, dataOffset, size);
                else if (id == "TALB" || id == "TAL") info.Album = Id3Text(tag, dataOffset, size);
                else if ((id == "USLT" || id == "ULT") && string.IsNullOrEmpty(info.Lyrics)) info.Lyrics = Id3Lyrics(tag, dataOffset, size);
                else if ((id == "APIC" || id == "PIC") && info.Cover == null) info.Cover = Id3Cover(tag, dataOffset, size, version);
                offset = dataOffset + size;
            }
        }

        private static string Id3Text(byte[] data, int offset, int size)
        {
            if (size < 2) return "";
            return CleanTag(Decode(Id3Encoding(data[offset]), data, offset + 1, size - 1));
        }

        private static string Id3Lyrics(byte[] data, int offset, int size)
        {
            if (size < 6) return "";
            int cursor = SkipTerminated(data, offset + 4, offset + size, data[offset] == 1 || data[offset] == 2);
            return cursor < offset + size ? CleanTag(Decode(Id3Encoding(data[offset]), data, cursor, offset + size - cursor)) : "";
        }

        private static Image Id3Cover(byte[] data, int offset, int size, int version)
        {
            if (size < 5) return null;
            byte encodingByte = data[offset];
            int cursor = offset + 1;
            int end = offset + size;
            if (version == 2) cursor += 3;
            else cursor = SkipTerminated(data, cursor, end, false);
            if (cursor >= end) return null;
            cursor++;
            cursor = SkipTerminated(data, cursor, end, encodingByte == 1 || encodingByte == 2);
            return cursor < end ? DecodeImage(data, cursor, end - cursor) : null;
        }

        private static void ReadFlac(string path, MusicTrackInfo info)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (Ascii(ReadBytes(stream, 4), 0, 4) != "fLaC") return;
                bool last = false;
                while (!last && stream.Position + 4 <= stream.Length)
                {
                    byte[] header = ReadBytes(stream, 4);
                    if (header.Length < 4) break;
                    last = (header[0] & 128) != 0;
                    int type = header[0] & 127;
                    int length = BE24(header, 1);
                    if (stream.Position + length > stream.Length) break;
                    if (type != 4 && type != 6) { stream.Seek(length, SeekOrigin.Current); continue; }
                    if (length > MaxMetadataBytes) { stream.Seek(length, SeekOrigin.Current); continue; }
                    byte[] data = ReadBytes(stream, length);
                    if (type == 4) ReadVorbisComment(data, info);
                    else if (info.Cover == null) info.Cover = FlacCover(data);
                }
            }
            Fallback(info, path);
        }

        private static void ReadVorbisComment(byte[] data, MusicTrackInfo info)
        {
            int cursor = 0;
            int vendorLength = LE32(data, cursor);
            cursor += 4 + vendorLength;
            if (vendorLength < 0 || cursor < 0 || cursor + 4 > data.Length) return;
            int count = LE32(data, cursor);
            cursor += 4;
            for (int i = 0; i < count && cursor + 4 <= data.Length; i++)
            {
                int length = LE32(data, cursor);
                cursor += 4;
                if (length < 0 || cursor + length > data.Length) return;
                string pair = Encoding.UTF8.GetString(data, cursor, length);
                cursor += length;
                int split = pair.IndexOf('=');
                if (split < 1) continue;
                string key = pair.Substring(0, split).ToUpperInvariant();
                string value = CleanTag(pair.Substring(split + 1));
                if (value == "") continue;
                if (key == "TITLE") info.Title = value;
                else if (key == "ARTIST") info.Artist = value;
                else if (key == "ALBUM") info.Album = value;
                else if ((key == "LYRICS" || key == "UNSYNCEDLYRICS") && string.IsNullOrEmpty(info.Lyrics)) info.Lyrics = value;
            }
        }

        private static Image FlacCover(byte[] data)
        {
            int cursor = 4;
            int mimeLength = BE32(data, cursor);
            cursor += 4 + mimeLength;
            if (cursor + 4 > data.Length) return null;
            int descriptionLength = BE32(data, cursor);
            cursor += 4 + descriptionLength + 16;
            if (cursor < 0 || cursor + 4 > data.Length) return null;
            int imageLength = BE32(data, cursor);
            cursor += 4;
            return imageLength > 0 && cursor + imageLength <= data.Length ? DecodeImage(data, cursor, imageLength) : null;
        }

        private static Image DecodeImage(byte[] data, int offset, int length)
        {
            try
            {
                using (MemoryStream stream = new MemoryStream(data, offset, length, false))
                using (Image source = Image.FromStream(stream)) return new Bitmap(source);
            }
            catch { return null; }
        }

        private static void Fallback(MusicTrackInfo info, string path)
        {
            if (string.IsNullOrEmpty(info.Title)) info.Title = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(info.Artist)) info.Artist = "未知艺术家";
            if (string.IsNullOrEmpty(info.Album)) info.Album = "本地音乐";
        }

        private static Encoding Id3Encoding(byte value)
        {
            if (value == 1) return Encoding.Unicode;
            if (value == 2) return Encoding.BigEndianUnicode;
            if (value == 3) return Encoding.UTF8;
            return Encoding.Default;
        }

        private static int SkipTerminated(byte[] data, int cursor, int end, bool wide)
        {
            int step = wide ? 2 : 1;
            while (cursor + step <= end)
            {
                bool zero = data[cursor] == 0 && (!wide || data[cursor + 1] == 0);
                cursor += step;
                if (zero) break;
            }
            return cursor;
        }

        private static byte[] ReadBytes(Stream stream, int count)
        {
            byte[] data = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(data, offset, count - offset);
                if (read <= 0) break;
                offset += read;
            }
            if (offset == count) return data;
            byte[] shortData = new byte[offset];
            Buffer.BlockCopy(data, 0, shortData, 0, offset);
            return shortData;
        }

        private static string Decode(Encoding encoding, byte[] data, int offset, int count)
        {
            if (offset < 0 || count <= 0 || offset + count > data.Length) return "";
            return encoding.GetString(data, offset, count);
        }

        private static string CleanTag(string value)
        {
            return string.IsNullOrEmpty(value) ? "" : value.Trim('\0', ' ', '\r', '\n');
        }

        private static string Ascii(byte[] data, int offset, int count)
        {
            return offset >= 0 && count >= 0 && offset + count <= data.Length ? Encoding.ASCII.GetString(data, offset, count) : "";
        }

        private static int SyncSafe(byte[] data, int offset)
        {
            return ((data[offset] & 127) << 21) | ((data[offset + 1] & 127) << 14)
                | ((data[offset + 2] & 127) << 7) | (data[offset + 3] & 127);
        }

        private static int BE24(byte[] data, int offset)
        {
            return (data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2];
        }

        private static int BE32(byte[] data, int offset)
        {
            return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        }

        private static int LE32(byte[] data, int offset)
        {
            return data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24);
        }
    }
}
