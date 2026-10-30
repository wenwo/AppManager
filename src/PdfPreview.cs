using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AppManager
{
    // Windows 10/11 inbox PDF renderer. Runs only in the preview loader's background worker.
    internal static class PdfPreview
    {
        public static Bitmap Render(string path, int pageIndex, int maxSide, CancellationToken cancellation, out int pageCount)
        {
            pageCount = 0;
            cancellation.ThrowIfCancellationRequested();
            if (new FileInfo(path).Length > 128L * 1024 * 1024)
                throw new InvalidDataException("PDF 超过 128 MB，请使用外部查看器打开。");
            Stopwatch budget = Stopwatch.StartNew();
            StorageFile file = Result(StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)), cancellation, budget);
            using (IRandomAccessStream input = Result(file.OpenAsync(FileAccessMode.Read), cancellation, budget))
            {
                if (input.Size > 128UL * 1024 * 1024)
                    throw new InvalidDataException("PDF 超过 128 MB，请使用外部查看器打开。");
                PdfDocument document = Result(PdfDocument.LoadFromStreamAsync(input), cancellation, budget);
                pageCount = checked((int)document.PageCount);
                if (pageCount == 0 || pageIndex < 0 || pageIndex >= pageCount)
                    throw new InvalidDataException("PDF 页码无效。");
                using (PdfPage page = document.GetPage((uint)pageIndex))
                using (InMemoryRandomAccessStream output = new InMemoryRandomAccessStream())
                {
                    Windows.Foundation.Size size = page.Size;
                    double longest = Math.Max(size.Width, size.Height);
                    if (longest <= 0 || double.IsNaN(longest) || double.IsInfinity(longest))
                        throw new InvalidDataException("PDF 页面尺寸无效。");
                    double scale = Math.Min(2, maxSide / longest);
                    PdfPageRenderOptions options = new PdfPageRenderOptions
                    {
                        DestinationWidth = (uint)Math.Max(1, Math.Round(size.Width * scale)),
                        DestinationHeight = (uint)Math.Max(1, Math.Round(size.Height * scale))
                    };
                    IAsyncAction render = page.RenderToStreamAsync(output, options);
                    try { Wait(render, cancellation, budget); render.GetResults(); }
                    finally { Close(render); }
                    if (output.Size == 0 || output.Size > 40UL * 1024 * 1024)
                        throw new InvalidDataException("PDF 页面过大，请使用外部查看器打开。");
                    using (DataReader reader = new DataReader(output.GetInputStreamAt(0)))
                    {
                        uint length = (uint)output.Size;
                        if (Result(reader.LoadAsync(length), cancellation, budget) != length)
                            throw new EndOfStreamException("PDF 页面数据不完整。");
                        byte[] pixels = new byte[length];
                        reader.ReadBytes(pixels);
                        cancellation.ThrowIfCancellationRequested();
                        using (MemoryStream stream = new MemoryStream(pixels, false))
                        using (Image image = Image.FromStream(stream)) return new Bitmap(image);
                    }
                }
            }
        }

        private static T Result<T>(IAsyncOperation<T> operation, CancellationToken cancellation, Stopwatch budget)
        {
            try { Wait(operation, cancellation, budget); return operation.GetResults(); }
            finally { Close(operation); }
        }

        private static void Wait(IAsyncInfo operation, CancellationToken cancellation, Stopwatch budget)
        {
            while (operation.Status == AsyncStatus.Started)
            {
                if (cancellation.IsCancellationRequested || budget.ElapsedMilliseconds > 15000)
                {
                    operation.Cancel();
                    cancellation.ThrowIfCancellationRequested();
                    throw new TimeoutException("PDF 加载超过 15 秒，请使用外部查看器打开。");
                }
                cancellation.WaitHandle.WaitOne(10);
            }
            cancellation.ThrowIfCancellationRequested();
        }

        private static void Close(IAsyncInfo operation)
        {
            if (operation.Status != AsyncStatus.Started) operation.Close();
        }
    }
}
