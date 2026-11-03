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
        /// <summary>非空表示只能查看不能编辑（截断显示、编码无法无损写回），内容为提示原因。</summary>
        public string ReadOnlyReason;
        /// <summary>文件混用了 CRLF/LF/CR：保存会统一成一种换行，需要用户确认。</summary>
        public bool MixedNewlines;

        public static TextPreview Read(string path, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            DateTime lastWrite = File.GetLastWriteTimeUtc(path);
            byte[] raw;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (stream.Length > MaxFileBytes)
                    throw new InvalidDataException("文本超过 16 MB，请使用外部查看器打开。");
                // 整体读入（上限 16 MB）：解码后要和原始字节做往返校验，判断能否无损写回。
                raw = new byte[stream.Length];
                int read = 0;
                while (read < raw.Length)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int n = stream.Read(raw, read, raw.Length - read);
                    if (n == 0) break;
                    read += n;
                }
                if (read != raw.Length) Array.Resize(ref raw, read);
            }
            // Detect UTF-8/16/32 BOMs; BOM-less legacy text falls back to the Windows code page.
            bool hasBom = (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
                || (raw.Length >= 2 && ((raw[0] == 0xFF && raw[1] == 0xFE) || (raw[0] == 0xFE && raw[1] == 0xFF)))
                || (raw.Length >= 4 && raw[0] == 0 && raw[1] == 0 && raw[2] == 0xFE && raw[3] == 0xFF);
            TextPreview preview;
            try
            {
                using (MemoryStream stream = new MemoryStream(raw, false))
                    preview = Read(stream, new UTF8Encoding(false, true), cancellation);
            }
            catch (DecoderFallbackException)
            {
                if (hasBom) throw;
                using (MemoryStream stream = new MemoryStream(raw, false))
                    preview = Read(stream, Encoding.Default, cancellation);
            }
            preview.LastWriteUtc = lastWrite;
            if (preview.Truncated)
                preview.ReadOnlyReason = "仅预览前 256K 字符，完整内容请外部打开";
            else if (!RoundTrips(raw, preview.Content, preview.Encoding))
                // 系统编码回退或带 BOM 的文件解码时会把非法字节悄悄换成 ?/U+FFFD；
                // 这种内容一旦保存，原始字节就永久丢了，只能只读查看。
                preview.ReadOnlyReason = "编码无法无损识别，已按只读打开，避免保存时改坏原内容";
            preview.MixedNewlines = HasMixedNewlines(preview.Content);
            return preview;
        }

        /// <summary>按读取时的编码（含 BOM）重新编码，必须与原始字节完全一致才允许编辑写回。</summary>
        internal static bool RoundTrips(byte[] raw, string content, Encoding encoding)
        {
            if (encoding == null) return false;
            try
            {
                Encoding strict = (Encoding)encoding.Clone();
                strict.EncoderFallback = EncoderFallback.ExceptionFallback;
                byte[] preamble = encoding.GetPreamble();
                byte[] body = strict.GetBytes(content ?? string.Empty);
                if (preamble.Length + body.Length != raw.Length) return false;
                for (int i = 0; i < preamble.Length; i++) if (raw[i] != preamble[i]) return false;
                for (int i = 0; i < body.Length; i++) if (raw[preamble.Length + i] != body[i]) return false;
                return true;
            }
            catch (EncoderFallbackException) { return false; }
        }

        /// <summary>是否同时出现两种及以上换行（CRLF、单独 LF、单独 CR）。</summary>
        internal static bool HasMixedNewlines(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            bool crlf = false, lf = false, cr = false;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n') { crlf = true; i++; }
                    else cr = true;
                }
                else if (text[i] == '\n') lf = true;
                if ((crlf ? 1 : 0) + (lf ? 1 : 0) + (cr ? 1 : 0) > 1) return true;
            }
            return false;
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
            string backup = temp + ".bak";
            try
            {
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(preamble, 0, preamble.Length);
                    stream.Write(body, 0, body.Length);
                    stream.Flush(true);
                }
                // 指定备份名：ReplaceFile 中途失败（1176/1177）时原件会留在备份名下，而不是凭空消失。
                File.Replace(temp, path, backup, true);
            }
            catch
            {
                RestoreAfterFailedReplace(path, temp, backup);
                throw;
            }
            finally
            {
                // 只有原路径上确实有文件时才清理；否则临时文件/备份可能是唯一的副本。
                if (File.Exists(path))
                {
                    TryDelete(temp);
                    TryDelete(backup);
                }
            }
        }

        /// <summary>替换失败且原路径已空：先放回原件，没有原件时放回新内容，保证文件不会消失。</summary>
        private static void RestoreAfterFailedReplace(string path, string temp, string backup)
        {
            try
            {
                if (File.Exists(path)) return;
                if (File.Exists(backup)) File.Move(backup, path);
                else if (File.Exists(temp)) File.Move(temp, path);
            }
            catch (Exception ex)
            {
                Paths.LogError(new IOException("文本保存失败后放回原文件失败，请手动检查：" + path + "（" + backup + " / " + temp + "）", ex));
            }
        }

        private static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
