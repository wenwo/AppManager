using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AppManager.Controls;

namespace AppManager
{
    internal static class ViewerTests
    {
        private static int passed, failed;
        private static string root;
        private sealed class Value : IDisposable
        {
            public int Number;
            public bool Disposed;
            public void Dispose() { Disposed = true; }
        }
        private sealed class Owner : Control
        {
            public void Recreate() { RecreateHandle(); }
        }

        [STAThread]
        private static int Main()
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            root = Path.Combine(Path.GetTempPath(), "AppManagerViewerTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Run("loader coalesces 100 switches, disposes stale result, keeps UI responsive", LoaderCoalescing);
                Run("loader clear/dispose prevents late callbacks and releases results", LoaderClose);
                Run("loader recovers after error and handle recreation", LoaderRecovery);
                Run("text UTF8, UTF16/32 BOM and legacy ANSI", TextEncodings);
                Run("text bounds, binary rejection, cancellation and file release", TextBounds);
                Run("bounded long-line text display latency", TextDisplay);
                Run("WIC transparency, aspect ratio, cancellation and file release", Images);
                Run("PDF renders both pages through Windows API and releases file", PdfPages);
                Run("PDF corrupt/missing/oversize/cancelled inputs recover", PdfFailures);
                Run("PDF repeated rendering releases GDI handles", PdfRepeated);
                Run("viewer rapid switch/close and text selection keys", ViewerLifecycle);
                Run("PDF page navigation and image pan in both directions", ViewerNavigation);
                Run("resource routing preserves software scripts; credentials accept text", Routing);
                Run("external pickers serialize on STA, deduplicate and keep UI responsive", PickerSerial);
                Run("external picker cancellation clears pending files and permits retry", PickerCancellation);
                Run("external picker queue bounds, failure and disposal", PickerBounds);
                Console.WriteLine("RESULT " + passed + " passed, " + failed + " failed");
                return failed == 0 ? 0 : 1;
            }
            finally { Directory.Delete(root, true); }
        }

        private static void Run(string name, Action action)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try { action(); passed++; Console.WriteLine("PASS " + name + " (" + watch.ElapsedMilliseconds + " ms)"); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
        }
        private static void Check(bool condition, string message)
        { if (!condition) throw new Exception(message); }
        private static void Pump(Func<bool> done)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (!done())
            {
                Application.DoEvents();
                if (watch.ElapsedMilliseconds > 25000) throw new TimeoutException("UI wait");
                Thread.Sleep(2);
            }
            Application.DoEvents();
        }
        private static void Expect<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new Exception("Expected " + typeof(T).Name);
        }
        private static object Field(object value, string name)
        { return value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value); }
        private static void Set(object value, string name, object item)
        { value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(value, item); }
        private static object Call(object value, string name, params object[] args)
        { return value.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(value, args); }

        private static void LoaderCoalescing()
        {
            using (Owner owner = new Owner())
            using (PreviewLoader<Value> loader = new PreviewLoader<Value>(owner))
            using (ManualResetEvent started = new ManualResetEvent(false))
            using (ManualResetEvent release = new ManualResetEvent(false))
            {
                int loads = 0, callbacks = 0, heartbeat = 0;
                Value stale = new Value();
                loader.Load(delegate(CancellationToken c) { Interlocked.Increment(ref loads); started.Set(); release.WaitOne(); return stale; },
                    delegate { throw new Exception("stale callback"); });
                Check(started.WaitOne(5000), "worker start");
                for (int i = 0; i < 100; i++)
                {
                    int number = i;
                    loader.Load(delegate(CancellationToken c) { Interlocked.Increment(ref loads); return new Value { Number = number }; },
                        delegate(Value v, Exception e) { Check(e == null && v.Number == 99, "latest value"); callbacks++; v.Dispose(); });
                }
                owner.BeginInvoke((Action)delegate { heartbeat++; });
                Pump(delegate { return heartbeat == 1; });
                Check(loads == 1, "work piled up while first blocked");
                release.Set();
                Pump(delegate { return callbacks == 1; });
                Check(loads == 2 && stale.Disposed, "bounded loads / release");
            }
        }

        private static void LoaderClose()
        {
            using (Owner owner = new Owner())
            using (ManualResetEvent started = new ManualResetEvent(false))
            using (ManualResetEvent release = new ManualResetEvent(false))
            {
                PreviewLoader<Value> loader = new PreviewLoader<Value>(owner);
                Value stale = new Value(); int callbacks = 0;
                loader.Load(delegate(CancellationToken c) { started.Set(); release.WaitOne(); return stale; }, delegate { callbacks++; });
                Check(started.WaitOne(5000), "worker start");
                loader.Clear(); loader.Dispose(); release.Set();
                Pump(delegate { return stale.Disposed; });
                loader.Load(delegate { throw new Exception("disposed load"); }, delegate { callbacks++; });
                Check(callbacks == 0, "closed callback");
            }
        }

        private static void LoaderRecovery()
        {
            using (Owner owner = new Owner())
            using (PreviewLoader<Value> loader = new PreviewLoader<Value>(owner))
            {
                bool error = false, success = false;
                loader.Load(delegate { throw new InvalidDataException(); }, delegate(Value v, Exception e) { error = e is InvalidDataException; });
                Pump(delegate { return error; });
                Value stale = new Value();
                loader.Load(delegate { return stale; }, delegate { throw new Exception("destroyed handle callback"); });
                Check(SpinWait.SpinUntil(delegate { return Field(loader, "ready") != null; }, 5000), "queued result");
                owner.Recreate();
                Check(stale.Disposed, "handle recreation retained stale result");
                loader.Load(delegate { return new Value(); }, delegate(Value v, Exception e) { success = e == null; v.Dispose(); });
                Pump(delegate { return success; });
            }
        }

        private static string WriteText(string name, string content, Encoding encoding)
        {
            string path = Path.Combine(root, name); File.WriteAllText(path, content, encoding); return path;
        }
        private static void TextEncodings()
        {
            string content = "中文证件\r\nabc 😀";
            foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true),
                Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF32, new UTF32Encoding(true, true) })
            {
                string path = WriteText("encoding.txt", content, encoding);
                Check(TextPreview.Read(path, CancellationToken.None).Content == content, encoding.WebName);
            }
            string ansi = Encoding.Default.GetString(Encoding.Default.GetBytes("中文ABC"));
            string legacy = WriteText("ansi.txt", ansi, Encoding.Default);
            Check(TextPreview.Read(legacy, CancellationToken.None).Content == ansi, "Windows code page");
            if (Encoding.Default.CodePage == 936)
            {
                File.WriteAllBytes(legacy, new byte[] { 0xEF, 0xA1, 0x41 });
                Check(TextPreview.Read(legacy, CancellationToken.None).Content == Encoding.Default.GetString(File.ReadAllBytes(legacy)), "BOM prefix false positive");
            }
        }
        private static void TextBounds()
        {
            string path = WriteText("large.txt", new string('x', TextPreview.MaxCharacters + 1), new UTF8Encoding(false));
            TextPreview preview = TextPreview.Read(path, CancellationToken.None);
            Check(preview.Truncated && preview.Content.Length == TextPreview.MaxCharacters, "truncate");
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                stream.SetLength(TextPreview.MaxFileBytes + 1L);
            Expect<InvalidDataException>(delegate { TextPreview.Read(path, CancellationToken.None); });
            File.WriteAllBytes(path, new byte[] { 65, 0, 66 });
            Expect<InvalidDataException>(delegate { TextPreview.Read(path, CancellationToken.None); });
            Expect<OperationCanceledException>(delegate { TextPreview.Read(path, new CancellationToken(true)); });
            File.Delete(path);
        }

        private static string MakeImage()
        {
            string path = Path.Combine(root, "alpha.png");
            using (Bitmap image = new Bitmap(128, 64, PixelFormat.Format32bppArgb))
            {
                image.SetPixel(0, 0, Color.FromArgb(128, 200, 100, 50)); image.Save(path, ImageFormat.Png);
            }
            return path;
        }
        private static void Images()
        {
            string path = MakeImage();
            using (Bitmap image = WicImageDecoder.Load(path, 128))
                Check(image.Width == 128 && image.Height == 64 && Math.Abs(image.GetPixel(0, 0).A - 128) <= 1, "alpha / dimensions");
            using (Bitmap image = WicImageDecoder.Load(path, 32)) Check(image.Width == 32 && image.Height == 16, "downsampling");
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Expect<OperationCanceledException>(delegate { WicImageDecoder.Load(path, 128, new CancellationToken(true)); });
        }

        private static void TextDisplay()
        {
            using (Form form = new Form())
            using (TextViewerControl viewer = new TextViewerControl())
            {
                form.Controls.Add(viewer); viewer.Size = new Size(800, 600);
                RichTextBox box = (RichTextBox)Field(viewer, "textBox");
                IntPtr handle = form.Handle; handle = viewer.Handle; handle = box.Handle;
                string path = WriteText("long-line.txt", new string('x', TextPreview.MaxCharacters + 4096), Encoding.UTF8);
                Stopwatch elapsed = Stopwatch.StartNew();
                viewer.Open(new List<string> { path }, 0);
                Pump(delegate { return !((bool)Field(viewer, "loading")); });
                Console.WriteLine("  256K single-line RichTextBox load/display: " + elapsed.ElapsedMilliseconds + " ms");
                Check(box.TextLength == TextPreview.MaxCharacters && (bool)Field(viewer, "truncated"), "display bound");
                viewer.ClosePreview();
            }
        }

        private static string MakePdf()
        {
            string path = Path.Combine(root, "two-pages.pdf");
            if (File.Exists(path)) return path;
            string red = "1 0 0 rg 0 0 200 100 re f\n", blue = "0 0 1 rg 0 0 200 100 re f\n";
            string[] objects = { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R >>",
                "<< /Length " + red.Length + " >>\nstream\n" + red + "endstream",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 6 0 R >>",
                "<< /Length " + blue.Length + " >>\nstream\n" + blue + "endstream" };
            StringBuilder pdf = new StringBuilder("%PDF-1.4\n"); List<int> offsets = new List<int>();
            for (int i = 0; i < objects.Length; i++)
            { offsets.Add(pdf.Length); pdf.Append((i + 1) + " 0 obj\n" + objects[i] + "\nendobj\n"); }
            int xref = pdf.Length;
            pdf.Append("xref\n0 7\n0000000000 65535 f \n");
            foreach (int offset in offsets) pdf.Append(offset.ToString("D10") + " 00000 n \n");
            pdf.Append("trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
            File.WriteAllText(path, pdf.ToString(), Encoding.ASCII); return path;
        }
        private static Bitmap Render(string path, int page)
        {
            Task<Bitmap> task = Task.Run(delegate
            {
                int count; Bitmap image = PdfPreview.Render(path, page, 2400, CancellationToken.None, out count);
                Check(count == 2, "page count"); return image;
            });
            if (!task.Wait(25000)) throw new TimeoutException("PDF worker");
            return task.GetAwaiter().GetResult();
        }
        private static void PdfPages()
        {
            string path = MakePdf();
            using (Bitmap first = Render(path, 0))
            {
                Color pixel = first.GetPixel(first.Width / 2, first.Height / 2);
                Console.WriteLine("  PDF first page: " + first.Width + "x" + first.Height + ", center=" + pixel);
                Check(Math.Abs(first.Width / (double)first.Height - 2) < 0.02 && first.Width <= 2400 && pixel.R > 240 && pixel.B < 20, "red page");
            }
            using (Bitmap second = Render(path, 1))
            {
                Color pixel = second.GetPixel(second.Width / 2, second.Height / 2);
                Check(pixel.B > 240 && pixel.R < 20, "blue page");
            }
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        private static void PdfFailures()
        {
            string corrupt = WriteText("corrupt.pdf", "broken", Encoding.ASCII);
            foreach (string path in new string[] { corrupt, Path.Combine(root, "missing.pdf") })
            {
                bool rejected = false;
                try { using (Bitmap image = Render(path, 0)) { } }
                catch (Exception) { rejected = true; }
                Check(rejected, "invalid PDF accepted");
            }
            using (FileStream stream = File.Create(corrupt)) stream.SetLength(128L * 1024 * 1024 + 1);
            int count;
            Expect<InvalidDataException>(delegate { PdfPreview.Render(corrupt, 0, 2400, CancellationToken.None, out count); });
            Expect<OperationCanceledException>(delegate { PdfPreview.Render(MakePdf(), 0, 2400, new CancellationToken(true), out count); });
            using (Bitmap image = Render(MakePdf(), 0)) { }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetGuiResources(IntPtr process, int flags);
        private static void PdfRepeated()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long memoryBefore = Process.GetCurrentProcess().PrivateMemorySize64;
            int before = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
            for (int i = 0; i < 30; i++) using (Bitmap image = Render(MakePdf(), i % 2)) { }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            int after = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
            long memoryAfter = Process.GetCurrentProcess().PrivateMemorySize64;
            Check(after <= before + 4, "GDI growth " + before + " -> " + after);
            Console.WriteLine("  GDI handles: " + before + " -> " + after);
            Console.WriteLine("  Private memory after GC: " + memoryBefore / 1024 + " -> " + memoryAfter / 1024 + " KiB");
            for (int batch = 0; batch < 3; batch++)
            {
                for (int i = 0; i < 30; i++) using (Bitmap image = Render(MakePdf(), i % 2)) { }
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Console.WriteLine("  Private memory batch " + (batch + 2) + ": " + Process.GetCurrentProcess().PrivateMemorySize64 / 1024 + " KiB");
            }
            Check(Process.GetCurrentProcess().PrivateMemorySize64 <= memoryAfter + 16L * 1024 * 1024, "PDF memory continues growing after warmup");
            Check(GetGuiResources(Process.GetCurrentProcess().Handle, 0) <= before + 4, "GDI growth after 120 pages");
        }

        private static void ViewerLifecycle()
        {
            using (Form form = new Form())
            using (ImageViewerControl image = new ImageViewerControl())
            using (TextViewerControl text = new TextViewerControl())
            {
                form.Controls.Add(image); form.Controls.Add(text); image.Size = text.Size = new Size(800, 600);
                string png = MakeImage(), txt = WriteText("view.txt", "abc", Encoding.UTF8);
                for (int i = 0; i < 50; i++) { image.Open(new List<string> { png }, 0); image.ClosePreview(); text.Open(new List<string> { txt }, 0); text.ClosePreview(); }
                image.Open(new List<string> { png }, 0);
                Pump(delegate { return Field(image, "image") != null; });
                image.Open(new List<string>(), 0);
                Check(Field(image, "image") == null, "empty open retained image");
                text.Open(new List<string> { txt }, 0);
                Pump(delegate { return !((bool)Field(text, "loading")); });
                RichTextBox box = (RichTextBox)Field(text, "textBox");
                Check(box.Text == "abc", "text content");
                object[] args = { new Message(), Keys.Left };
                Check(!(bool)Call(text, "ProcessCmdKey", args), "text cursor key intercepted");
                text.ClosePreview(); Check(box.Text.Length == 0, "closed text retained");
                image.ClosePreview();
            }
            Application.DoEvents();
        }
        private static void ViewerNavigation()
        {
            using (ImageViewerControl viewer = new ImageViewerControl())
            {
                viewer.Size = new Size(800, 600);
                viewer.Open(new List<string> { MakePdf() }, 0);
                Pump(delegate { return Field(viewer, "image") != null || (bool)Field(viewer, "loadFailed"); });
                Check((int)Field(viewer, "pdfPages") == 2, "PDF not loaded");
                Call(viewer, "Next"); Pump(delegate { return Field(viewer, "image") != null; });
                Check((int)Field(viewer, "pdfPage") == 1, "next PDF page");
                Call(viewer, "Prev"); Pump(delegate { return Field(viewer, "image") != null; });
                Check((int)Field(viewer, "pdfPage") == 0, "previous PDF page");
                Set(viewer, "zoomFactor", 4F);
                Set(viewer, "pan", new PointF(10000, -10000)); Call(viewer, "ClampPan");
                PointF pan = (PointF)Field(viewer, "pan"); Check(pan.X > 0 && pan.Y < 0, "pan constrained to one side");
                viewer.ClosePreview();
            }
        }
        private static void Routing()
        {
            MainForm form = (MainForm)FormatterServices.GetUninitializedObject(typeof(MainForm));
            AppInfo script = new AppInfo { TargetPath = @"C:\not-real\script.ps1", IsFile = true };
            Set(form, "contentMode", SidebarControl.ContentMode.Software);
            Check(!(bool)Call(form, "IsEmbeddedViewerItem", script), "software script captured");
            Set(form, "contentMode", SidebarControl.ContentMode.Website);
            Check((bool)Call(form, "IsEmbeddedViewerItem", script), "resource script not previewed");
            foreach (string url in new string[] { "https://example.test/picture.png", "https://example.test/file.pdf", "https://example.test/file.txt" })
                Check(!(bool)Call(form, "IsEmbeddedViewerItem", new AppInfo { TargetPath = url, IsFile = true }), "URL captured: " + url);
            Check(MediaRules.MatchCategoryExt("证件", "note.txt") && MediaRules.MatchCategoryExt("证件", "scan.pdf")
                && MediaRules.MatchCategoryExt("证件", "note.docx") && !MediaRules.MatchCategoryExt("证件", "movie.mp4"), "credential formats");
            Set(form, "shownApps", new List<AppInfo> { new AppInfo { TargetPath = @"\\offline-test\share\missing.png", IsFile = true } });
            List<string> sequence = (List<string>)Call(form, "MediaSequence", (Func<string, bool>)MediaRules.IsImageDocument, false);
            Check(sequence.Count == 1, "background existence checks");
            // The uninitialized form is only used for methods that inspect these fields; never show/dispose it.
            GC.SuppressFinalize(form);
        }

        private static void PickerSerial()
        {
            using (ManualResetEvent started = new ManualResetEvent(false))
            using (ManualResetEvent release = new ManualResetEvent(false))
            using (Owner owner = new Owner())
            {
                List<string> shown = new List<string>(); int active = 0, errors = 0, heartbeat = 0;
                using (OpenWithQueue queue = new OpenWithQueue(delegate(string path)
                {
                    Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "picker thread is not STA");
                    Check(Interlocked.Increment(ref active) == 1, "overlapping pickers");
                    lock (shown) shown.Add(path);
                    if (path == "first") { started.Set(); release.WaitOne(); }
                    Interlocked.Decrement(ref active);
                    return true;
                }, delegate { Interlocked.Increment(ref errors); }))
                {
                    Check(queue.Enqueue("first") && started.WaitOne(5000), "first picker did not start");
                    Check(queue.Enqueue("FIRST") && queue.Enqueue("second") && queue.Enqueue("third"), "enqueue failed");
                    IntPtr handle = owner.Handle;
                    owner.BeginInvoke((Action)delegate { heartbeat++; });
                    Pump(delegate { return heartbeat == 1; });
                    lock (shown) Check(shown.Count == 1, "pending picker opened too soon");
                    release.Set();
                    Pump(delegate { return !(bool)Field(queue, "running"); });
                    lock (shown) Check(string.Join(",", shown.ToArray()) == "first,second,third", "order or duplicate");
                    Check(errors == 0 && active == 0, "picker worker failed");
                }
            }
        }

        private static void PickerCancellation()
        {
            using (ManualResetEvent started = new ManualResetEvent(false))
            using (ManualResetEvent release = new ManualResetEvent(false))
            {
                int calls = 0;
                using (OpenWithQueue queue = new OpenWithQueue(delegate(string path)
                {
                    Interlocked.Increment(ref calls);
                    if (path == "cancel") { started.Set(); release.WaitOne(); return false; }
                    return true;
                }, null))
                {
                    Check(queue.Enqueue("cancel") && started.WaitOne(5000), "picker did not start");
                    queue.Enqueue("skipped1"); queue.Enqueue("skipped2");
                    release.Set(); Pump(delegate { return !(bool)Field(queue, "running"); });
                    Check(calls == 1, "cancelled batch continued");
                    Check(queue.Enqueue("retry"), "retry rejected");
                    Pump(delegate { return !(bool)Field(queue, "running"); });
                    Check(calls == 2, "retry did not run");
                }
            }
        }

        private static void PickerBounds()
        {
            using (ManualResetEvent started = new ManualResetEvent(false))
            using (ManualResetEvent release = new ManualResetEvent(false))
            {
                int calls = 0;
                OpenWithQueue queue = new OpenWithQueue(delegate
                {
                    Interlocked.Increment(ref calls); started.Set(); release.WaitOne(); return true;
                }, null);
                Check(queue.Enqueue("first") && started.WaitOne(5000), "picker did not start");
                for (int i = 0; i < 255; i++) Check(queue.Enqueue("pending" + i), "queue filled prematurely");
                Check(!queue.Enqueue("overflow") && queue.Enqueue("first"), "queue bound or dedup");
                queue.Dispose(); release.Set();
                Pump(delegate { return !(bool)Field(queue, "running"); });
                Check(calls == 1 && !queue.Enqueue("after disposal"), "disposed queue continued");
            }
            int errors = 0, attempts = 0;
            using (OpenWithQueue queue = new OpenWithQueue(delegate
            {
                if (Interlocked.Increment(ref attempts) == 1) throw new IOException("fixture");
                return true;
            }, delegate { Interlocked.Increment(ref errors); }))
            {
                queue.Enqueue("error"); Pump(delegate { return !(bool)Field(queue, "running"); });
                Check(errors == 1, "error not reported");
                queue.Enqueue("retry"); Pump(delegate { return !(bool)Field(queue, "running"); });
                Check(attempts == 2 && errors == 1, "failed picker could not retry");
            }
        }
    }
}
