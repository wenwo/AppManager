using System;
using System.IO;
using System.Text;
using System.Threading;

namespace AppManager
{
    internal sealed class TextPreview
    {
        internal const int MaxCharacters = 256 * 1024;
        internal const int MaxFileBytes = 16 * 1024 * 1024;
        public string Content;
        public bool Truncated;
        /// <summary>读取时识别出的编码（含 BOM 信息），保存时按原编码写回。</summary>
        public Encoding Encoding;
        /// <summary>读取前的修改时间，保存前用于发现外部修改。</summary>
        public DateTime LastWriteUtc;
        /// <summary>Markdown 预览：后台线程预先生成的 RTF，界面线程装入后即释放。</summary>
        public string RenderedRtf;

        public static TextPreview Read(string path, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            DateTime lastWrite = File.GetLastWriteTimeUtc(path);
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (stream.Length > MaxFileBytes)
                    throw new InvalidDataException("文本超过 16 MB，请使用外部查看器打开。");
                // Detect UTF-8/16/32 BOMs; BOM-less legacy text falls back to the Windows code page.
                byte[] prefix = new byte[4];
                int prefixLength = stream.Read(prefix, 0, prefix.Length);
                bool hasBom = (prefixLength >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
                    || (prefixLength >= 2 && ((prefix[0] == 0xFF && prefix[1] == 0xFE)
                        || (prefix[0] == 0xFE && prefix[1] == 0xFF)))
                    || (prefixLength >= 4 && prefix[0] == 0 && prefix[1] == 0 && prefix[2] == 0xFE && prefix[3] == 0xFF);
                stream.Position = 0;
                TextPreview preview;
                try { preview = Read(stream, new UTF8Encoding(false, true), cancellation); }
                catch (DecoderFallbackException)
                {
                    if (hasBom) throw;
                    stream.Position = 0;
                    preview = Read(stream, Encoding.Default, cancellation);
                }
                preview.LastWriteUtc = lastWrite;
                return preview;
            }
        }

        private static TextPreview Read(Stream stream, Encoding encoding, CancellationToken cancellation)
        {
            char[] buffer = new char[4096];
            StringBuilder text = new StringBuilder();
            Encoding detected;
            using (StreamReader reader = new StreamReader(stream, encoding, true, 4096, true))
            {
                while (text.Length <= MaxCharacters)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int count = reader.Read(buffer, 0, Math.Min(buffer.Length, MaxCharacters + 1 - text.Length));
                    if (count == 0) break;
                    for (int i = 0; i < count; i++)
                        if (buffer[i] == '\0') throw new InvalidDataException("文件包含非文本内容，请使用外部查看器打开。");
                    text.Append(buffer, 0, count);
                }
                detected = reader.CurrentEncoding;
            }
            cancellation.ThrowIfCancellationRequested();
            bool truncated = text.Length > MaxCharacters;
            if (truncated) text.Length = MaxCharacters;
            return new TextPreview { Content = text.ToString(), Truncated = truncated, Encoding = detected };
        }

        /// <summary>
        /// 按指定编码（含其 BOM）写回文本：先写同目录临时文件再原子替换，写入中断不会损坏原文件。
        /// 编码无法表示的字符抛出 EncoderFallbackException，由调用方决定是否改用 UTF-8。
        /// </summary>
        public static void Write(string path, string content, Encoding encoding)
        {
            Encoding strict = (Encoding)encoding.Clone();
            strict.EncoderFallback = EncoderFallback.ExceptionFallback;
            byte[] preamble = encoding.GetPreamble();
            byte[] body = strict.GetBytes(content ?? string.Empty);
            string temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)),
                "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(preamble, 0, preamble.Length);
                    stream.Write(body, 0, body.Length);
                    stream.Flush(true);
                }
                File.Replace(temp, path, null, true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
