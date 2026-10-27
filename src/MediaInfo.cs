using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;

namespace AppManager
{
    /// <summary>
    /// 媒体文件「拍摄时间」读取：列表时间列与排序共用同一口径。
    /// 照片取 EXIF DateTimeOriginal（0x9003，不存在则依次回退 0x9004/0x0132）；
    /// 视频解析 mp4/mov/m4v 的 mvhd atom 中的 creation_time（ISO-BMFF，自 1904-01-01 起的秒数）；
    /// 其他类型或解析失败返回 false，调用方回退到 LastWriteTime。
    /// 所有 IO/解码异常都在内部吞掉，绝不影响列表加载或排序。
    /// </summary>
    internal static class MediaInfo
    {
        /// <summary>尝试读取拍摄/创建时间；成功返回 true，否则 false。</summary>
        public static bool TryGetTakenDate(string path, out DateTime taken)
        {
            taken = DateTime.MinValue;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            try
            {
                if (MediaRules.IsImage(path)) return TryReadImageTaken(path, out taken);
                if (MediaRules.IsVideo(path)) return TryReadVideoTaken(path, out taken);
            }
            catch { }
            return false;
        }

        // ===== 照片：EXIF =====

        private static bool TryReadImageTaken(string path, out DateTime taken)
        {
            taken = DateTime.MinValue;
            try
            {
                using (Image img = Image.FromFile(path))
                {
                    string dto = null, dtd = null, dt = null;
                    PropertyItem[] props = img.PropertyItems;
                    for (int i = 0; i < props.Length; i++)
                    {
                        PropertyItem p = props[i];
                        switch (p.Id)
                        {
                            case 0x9003: dto = ExifDate(p.Value); break;
                            case 0x9004: dtd = ExifDate(p.Value); break;
                            case 0x0132: dt = ExifDate(p.Value); break;
                        }
                    }
                    string s = !string.IsNullOrEmpty(dto) ? dto
                             : !string.IsNullOrEmpty(dtd) ? dtd : dt;
                    if (string.IsNullOrEmpty(s)) return false;
                    DateTime t;
                    if (DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                    {
                        taken = t;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static string ExifDate(byte[] value)
        {
            if (value == null || value.Length == 0) return null;
            string s = Encoding.ASCII.GetString(value).Trim('\0', ' ');
            DateTime t;
            if (DateTime.TryParseExact(s, "yyyy:MM:dd HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                return t.ToString("yyyy-MM-dd HH:mm:ss");
            return s;
        }

        // ===== 视频：mp4/mov/m4v 的 mvhd =====

        private static readonly string[] MovExt = { ".mp4", ".mov", ".m4v" };

        private static bool TryReadVideoTaken(string path, out DateTime taken)
        {
            taken = DateTime.MinValue;
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return false;
            ext = ext.ToLowerInvariant();
            bool supported = false;
            for (int i = 0; i < MovExt.Length; i++)
                if (ext == MovExt[i]) { supported = true; break; }
            if (!supported) return false;

            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    DateTime t;
                    if (TryFindMvhd(fs, 0, fs.Length, 0, out t))
                    {
                        // mvhd 的 creation_time 是 UTC 秒数（1904 起算），转本地时区与文件系统口径一致
                        taken = t.ToLocalTime();
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 在 [start, end) 区间内递归找 moov/mvhd；depth 防止恶意/损坏文件造成无限递归。
        /// 找到的 creation_time 以 UTC 返回（未转本地）。
        /// </summary>
        private static bool TryFindMvhd(Stream s, long start, long end, int depth, out DateTime taken)
        {
            taken = DateTime.MinValue;
            if (depth > 8 || end - start < 8) return false;
            s.Seek(start, SeekOrigin.Begin);
            long pos = start;
            byte[] header = new byte[16];
            while (pos + 8 <= end)
            {
                s.Seek(pos, SeekOrigin.Begin);
                int read = ReadFull(s, header, 0, 16);
                if (read < 8) return false;
                uint size = ReadU32BE(header, 0);
                string type = Encoding.ASCII.GetString(header, 4, 4);
                long boxHeaderSize = 8;
                long boxSize;
                if (size == 1)
                {
                    if (read < 16) return false;
                    boxSize = (long)ReadU64BE(header, 8);
                    boxHeaderSize = 16;
                }
                else if (size == 0)
                {
                    boxSize = end - pos;   // 延伸到父容器末尾
                }
                else
                {
                    boxSize = size;
                }
                if (boxSize < boxHeaderSize) return false;

                if (type == "mvhd")
                {
                    // box 数据段：version(1) + flags(3) + creation_time(4 或 8)
                    long dataStart = pos + boxHeaderSize;
                    byte[] buf = new byte[8];
                    s.Seek(dataStart, SeekOrigin.Begin);
                    if (ReadFull(s, buf, 0, 4) < 4) return false;
                    int version = buf[0];
                    if (version == 1)
                    {
                        if (ReadFull(s, buf, 0, 8) < 8) return false;
                        taken = MvhdEpoch.AddSeconds(ReadU64BE(buf, 0));
                        return true;
                    }
                    else
                    {
                        if (ReadFull(s, buf, 0, 4) < 4) return false;
                        taken = MvhdEpoch.AddSeconds(ReadU32BE(buf, 0));
                        return true;
                    }
                }

                // 递归进容器 atom：moov（最常见）；traf/moof 等忽略
                if (type == "moov")
                {
                    DateTime inner;
                    if (TryFindMvhd(s, pos + boxHeaderSize, pos + boxSize, depth + 1, out inner))
                    {
                        taken = inner;
                        return true;
                    }
                }

                pos += boxSize;
            }
            return false;
        }

        private static readonly DateTime MvhdEpoch = new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static int ReadFull(Stream s, byte[] buf, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buf, offset + total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        private static uint ReadU32BE(byte[] buf, int offset)
        {
            return ((uint)buf[offset] << 24) | ((uint)buf[offset + 1] << 16)
                 | ((uint)buf[offset + 2] << 8) | buf[offset + 3];
        }

        private static ulong ReadU64BE(byte[] buf, int offset)
        {
            return ((ulong)ReadU32BE(buf, offset) << 32) | ReadU32BE(buf, offset + 4);
        }
    }
}
