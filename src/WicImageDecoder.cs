using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
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
            cancellation.ThrowIfCancellationRequested();
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > 256L * 1024 * 1024)
                    throw new InvalidDataException("图片超过 256 MB，请使用外部查看器打开。");
                BitmapDecoder probe = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnDemand);
                BitmapFrame frame = probe.Frames[0];
                int orientation = ReadOrientation(frame);
                int width = frame.PixelWidth;
                int height = frame.PixelHeight;
                if (width <= 0 || height <= 0 || (long)width * height > 64L * 1024 * 1024)
                    throw new InvalidDataException("图片超过内置查看器的 64M 像素限制。");
                cancellation.ThrowIfCancellationRequested();
                stream.Position = 0;
                BitmapImage source = new BitmapImage();
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
                cancellation.ThrowIfCancellationRequested();

                FormatConvertedBitmap converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
                converted.Freeze();
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
                    return result;
                }
                catch { result.Dispose(); throw; }
            }
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
                    if (value != null) return Convert.ToInt32(value);
                }
                catch (NotSupportedException) { }
                catch (System.Runtime.InteropServices.COMException) { }
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
