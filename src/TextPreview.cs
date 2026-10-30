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

        public static TextPreview Read(string path, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
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
                try { return Read(stream, new UTF8Encoding(false, true), cancellation); }
                catch (DecoderFallbackException)
                {
                    if (hasBom) throw;
                    stream.Position = 0;
                    return Read(stream, Encoding.Default, cancellation);
                }
            }
        }

        private static TextPreview Read(Stream stream, Encoding encoding, CancellationToken cancellation)
        {
            char[] buffer = new char[4096];
            StringBuilder text = new StringBuilder();
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
            }
            cancellation.ThrowIfCancellationRequested();
            bool truncated = text.Length > MaxCharacters;
            if (truncated) text.Length = MaxCharacters;
            return new TextPreview { Content = text.ToString(), Truncated = truncated };
        }
    }
}
