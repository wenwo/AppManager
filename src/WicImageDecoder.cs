using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AppManager
{
    /// <summary>使用 Windows Imaging Component 解码图片，再交给 WinForms/GDI+ 查看器绘制。</summary>
    internal static class WicImageDecoder
    {
        /// <summary>后台解码并限制工作位图的最长边；结果不持有原文件句柄。</summary>
        public static Bitmap Load(string path, int maxSide)
        {
            return Load(path, maxSide, CancellationToken.None);
        }

        public static Bitmap Load(string path, int maxSide, CancellationToken cancellation)
        {
            return Decode(path, delegate(int w, int h) { return maxSide; }, cancellation);
        }

        /// <summary>
        /// 按原图尺寸（已计入 EXIF 旋转）决定长边上限后解码：缩略图在解码阶段即缩小，
        /// 大照片无需整图解码，也不经 GDI+（GDI+ 跨线程串行，整图解码会拖住 UI 线程绘制）。
        /// </summary>
        public static Bitmap Load(string path, Func<int, int, int> maxSideFor)
        {
            return Decode(path, maxSideFor, CancellationToken.None);
        }

        private static readonly string[][] TakenDateQueries =
        {
            new string[] { "/ifd/exif/{ushort=36867}", "/app1/ifd/exif/{ushort=36867}" },
            new string[] { "/ifd/exif/{ushort=36868}", "/app1/ifd/exif/{ushort=36868}" },
            new string[] { "/ifd/{ushort=306}", "/app1/ifd/{ushort=306}" }
        };

        /// <summary>
        /// 只读取 RAW 的 EXIF 时间元数据，不解码像素、不生成缩略图；文件大小不受限制。
        /// 依次尝试 DateTimeOriginal、DateTimeDigitized、DateTime，单个字段非法时继续回退。
        /// </summary>
        internal static bool TryGetTakenDate(string path, out DateTime taken)
        {
            return ReadTakenDate(path, out taken) == TakenDateReadResult.Found;
        }

        internal static TakenDateReadResult ReadTakenDate(string path, out DateTime taken)
        {
            taken = DateTime.MinValue;
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    BitmapDecoder decoder = BitmapDecoder.Create(stream,
                        BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
                    if (decoder.Frames.Count == 0) return TakenDateReadResult.NoDate;
                    BitmapMetadata metadata = decoder.Frames[0].Metadata as BitmapMetadata;
                    if (metadata == null) return TakenDateReadResult.NoDate;
                    for (int i = 0; i < TakenDateQueries.Length; i++)
                    {
                        DateTime value;
                        if (TryReadExifDate(metadata, TakenDateQueries[i], out value))
                        {
                            taken = value;
                            return TakenDateReadResult.Found;
                        }
                    }
                    return TakenDateReadResult.NoDate;
                }
            }
            catch (IOException) { return TakenDateReadResult.Unavailable; }
            catch (UnauthorizedAccessException) { return TakenDateReadResult.Unavailable; }
            catch (Exception ex)
            {
                // 格式损坏/未安装解码器属于永久类，按更长退避重试；未知异常按暂时失败处理。
                return IsWicDecodeFailure(ex)
                    ? TakenDateReadResult.Permanent
                    : TakenDateReadResult.Unavailable;
            }
        }

        private static bool TryReadExifDate(BitmapMetadata metadata, string[] queries, out DateTime taken)
        {
            taken = DateTime.MinValue;
            for (int i = 0; i < queries.Length; i++)
            {
                string text = ReadExifText(metadata, queries[i]);
                if (MediaInfo.TryParseExifWallTime(text, out taken)) return true;
            }
            return false;
        }

        private static string ReadExifText(BitmapMetadata metadata, string query)
        {
            try
            {
                object value = metadata.GetQuery(query);
                if (value == null) return null;
                string text = value as string;
                if (text == null)
                {
                    byte[] bytes = value as byte[];
                    if (bytes != null) text = Encoding.ASCII.GetString(bytes);
                }
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch (Exception) { return null; }   // 不同容器的查询路径可能不支持，继续尝试下一个
        }

        private static Bitmap Decode(string path, Func<int, int, int> maxSideFor, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (!MediaRules.IsRawImage(path) && stream.Length > 256L * 1024 * 1024)
                    throw new InvalidDataException("图片超过 256 MB，请使用外部查看器打开。");
                BitmapDecoder probe;
                BitmapFrame frame;
                int orientation, width, height;
                try
                {
                    probe = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                        BitmapCacheOption.OnDemand);
                    if (probe.Frames.Count == 0) throw new FileFormatException("图片不包含可解码的帧。");
                    frame = probe.Frames[0];
                    orientation = ReadOrientation(frame);
                    width = frame.PixelWidth;
                    height = frame.PixelHeight;
                }
                catch (Exception ex)
                {
                    ThrowRawDecodeFailure(path, ex, cancellation);
                    throw;
                }
                if (width <= 0 || height <= 0 || (long)width * height > 64L * 1024 * 1024)
                    throw new InvalidDataException("图片超过内置查看器的 64M 像素限制。");
                bool rotated = orientation >= 5 && orientation <= 8;
                int maxSide = maxSideFor(rotated ? height : width, rotated ? width : height);
                cancellation.ThrowIfCancellationRequested();
                BitmapImage source = new BitmapImage();
                try
                {
                    stream.Position = 0;
                    source.BeginInit();
                    source.CacheOption = BitmapCacheOption.OnLoad;
                    source.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                    source.StreamSource = stream;
                    if (maxSide > 0 && Math.Max(width, height) > maxSide)
                    {
                        if (width >= height) source.DecodePixelWidth = maxSide;
                        else source.DecodePixelHeight = maxSide;
                    }
                    source.EndInit();
                    source.Freeze();
                }
                catch (Exception ex)
                {
                    ThrowRawDecodeFailure(path, ex, cancellation);
                    throw;
                }
                cancellation.ThrowIfCancellationRequested();

                FormatConvertedBitmap converted;
                try
                {
                    converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
                    converted.Freeze();
                }
                catch (Exception ex)
                {
                    ThrowRawDecodeFailure(path, ex, cancellation);
                    throw;
                }
                width = converted.PixelWidth;
                height = converted.PixelHeight;
                if (width <= 0 || height <= 0) throw new InvalidDataException("图片尺寸无效");
                Bitmap result = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                try
                {
                    BitmapData data = result.LockBits(new Rectangle(0, 0, width, height),
                        ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                    try
                    {
                        converted.CopyPixels(new Int32Rect(0, 0, width, height), data.Scan0,
                            checked(data.Stride * height), data.Stride);
                    }
                    finally { result.UnlockBits(data); }
                    cancellation.ThrowIfCancellationRequested();
                    ApplyOrientation(result, orientation);
                    cancellation.ThrowIfCancellationRequested();
                    return result;
                }
                catch (Exception ex)
                {
                    result.Dispose();
                    ThrowRawDecodeFailure(path, ex, cancellation);
                    throw;
                }
            }
        }

        private static void ThrowRawDecodeFailure(string path, Exception error, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!MediaRules.IsRawImage(path) || !IsWicDecodeFailure(error)) return;
            throw new InvalidDataException(RawDecodeFailureMessage(IsRw2WicDecoderRegistered()), error);
        }

        private static bool IsWicDecodeFailure(Exception error)
        {
            // WPF 常把 WIC 错误包一层；沿 InnerException 查找有限层数，避免误判外层 IOException 等。
            for (int depth = 0; error != null && depth < 4; depth++)
            {
                if (error is OperationCanceledException) return false;
                if (error is NotSupportedException || error is FileFormatException) return true;
                COMException com = error as COMException;
                if (com != null)
                {
                    uint code = unchecked((uint)com.HResult);
                    if (code >= 0x88982F00u && code <= 0x88982FFFu) return true;
                }
                error = error.InnerException;
            }
            return false;
        }

        internal static string RawDecodeFailureMessage(bool? decoderRegistered)
        {
            if (decoderRegistered == false)
                return "当前系统未安装 RW2 WIC 解码器。请安装 Microsoft Raw Image Extension 并重启应用。";
            if (decoderRegistered == true)
                return "RW2 解码失败，文件可能损坏或不受当前解码器支持。";
            return "RW2 解码失败，且无法确认系统是否注册了 RW2 解码器。";
        }

        private static readonly object RawDecoderGate = new object();
        private static bool rawDecoderProbed;
        private static bool? rawDecoderRegistered;

        internal static bool? IsRw2WicDecoderRegistered()
        {
            lock (RawDecoderGate)
            {
                if (rawDecoderProbed) return rawDecoderRegistered;
                bool? probed = ProbeRw2WicDecoder();
                if (probed.HasValue)
                {
                    // 只缓存确定结果；探测本身失败（如 WinRT 瞬时异常）下次错误时重试。
                    rawDecoderRegistered = probed;
                    rawDecoderProbed = true;
                }
                return probed;
            }
        }

        private static bool? ProbeRw2WicDecoder()
        {
            try
            {
                // WinRT 与 WPF 共用系统 WIC；按已注册解码器的文件扩展名判断 RAW 扩展是否可用。
                IReadOnlyList<Windows.Graphics.Imaging.BitmapCodecInformation> decoders =
                    Windows.Graphics.Imaging.BitmapDecoder.GetDecoderInformationEnumerator();
                bool queried = false;
                bool incomplete = false;
                foreach (Windows.Graphics.Imaging.BitmapCodecInformation info in decoders)
                {
                    try
                    {
                        IReadOnlyList<string> extensions = info.FileExtensions;
                        if (extensions == null) continue;
                        queried = true;
                        foreach (string extension in extensions)
                            if (string.Equals(extension, ".rw2", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    catch (Exception) { incomplete = true; }   // 信息不完整时不能断言“未安装”
                }
                return queried && !incomplete ? (bool?)false : null;
            }
            catch (Exception) { return null; }
        }

        private static int ReadOrientation(BitmapFrame frame)
        {
            BitmapMetadata metadata = frame.Metadata as BitmapMetadata;
            if (metadata == null) return 1;
            foreach (string query in new string[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
            {
                try
                {
                    object value = metadata.GetQuery(query);
                    if (value == null) continue;
                    int orientation = Convert.ToInt32(value);
                    if (orientation >= 1 && orientation <= 8) return orientation;
                }
                catch (Exception) { }   // 元数据缺失、类型异常或越界时回退为未旋转
            }
            return 1;
        }

        private static void ApplyOrientation(Bitmap bitmap, int orientation)
        {
            switch (orientation)
            {
                case 2: bitmap.RotateFlip(RotateFlipType.RotateNoneFlipX); break;
                case 3: bitmap.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
                case 4: bitmap.RotateFlip(RotateFlipType.Rotate180FlipX); break;
                case 5: bitmap.RotateFlip(RotateFlipType.Rotate90FlipX); break;
                case 6: bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
                case 7: bitmap.RotateFlip(RotateFlipType.Rotate270FlipX); break;
                case 8: bitmap.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
            }
        }
    }
}
