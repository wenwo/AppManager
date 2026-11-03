using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
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
        private static int Main(string[] args)
        {
            // 外部过滤器测试：本程序被复制成 lz4.exe，若被 libarchive 以「lz4 -d」启动，只留下标记文件后退出
            if (args.Length > 0 && args[0] == "-d")
            {
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "lz4-was-run.txt"), "ran");
                return 0;
            }
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
                Run("music players advance to the next track when a song ends", MusicAutoAdvance);
                Run("music stops on a missing track and the hidden mini player pauses", MusicSilencedWhenUnreachable);
                Run("markdown renders common syntax and keeps unsupported text", MarkdownRendering);
                Run("markdown hostile nesting and unclosed markers stay bounded and cancellable", MarkdownHostile);
                Run("markdown/text view switch renders live edits", MarkdownViewSwitch);
                Run("text edits save with original encoding, BOM and newlines", TextSaving);
                Run("lossy decoding opens read-only; mixed newlines detected; failed save keeps original", TextSafety);
                Run("PDF page navigation and image pan in both directions", ViewerNavigation);
                Run("resource routing preserves software scripts; credentials accept text", Routing);
                Run("archive zip names (UTF-8 flag, unflagged UTF-8, GBK), tree, sizes and path normalization", ArchiveZip);
                Run("archive tar.gz/xz/bz2/zst/7z and raw gz through system libarchive; corrupt and truncated input", ArchiveLibArchive);
                Run("archive reading shares the file, honours cancellation and releases handles", ArchiveSharing);
                Run("archive viewer browse, natural/size sort, breadcrumbs, hint and switching", ArchiveViewer);
                Run("archive hostile input: odd names, raw GNU tar names, deep paths, no external filters, damaged tar.gz", ArchiveHostile);
                Run("archive viewer sorts 60k rows by type/size/name without stalling the UI", ArchiveSortSpeed);
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
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool SetDllDirectory(string path);

        private static string WriteSilence(string name, double seconds)
        {
            string path = Path.Combine(root, name);
            int rate = 8000, samples = (int)(rate * seconds);
            using (BinaryWriter w = new BinaryWriter(File.Create(path)))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + samples * 2); w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(samples * 2); w.Write(new byte[samples * 2]);
            }
            return path;
        }

        private static bool PumpUntil(Func<bool> done, int milliseconds)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (!done() && watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(15); }
            return done();
        }

        private static void MusicAutoAdvance()
        {
            // 播放器依赖运行目录里的 libmpv-2.dll；未安装时跳过（构建脚本会自动下载）。
            string runtime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppManager");
            if (!File.Exists(Path.Combine(runtime, "libmpv-2.dll"))) { Console.WriteLine("  SKIP: libmpv-2.dll not installed"); return; }
            SetDllDirectory(runtime);
            string first = WriteSilence("auto-first.wav", 0.4), second = WriteSilence("auto-second.wav", 0.4);
            using (Form host = new Form { ShowInTaskbar = false, Size = new Size(900, 640), StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) })
            using (MusicPlayerControl player = new MusicPlayerControl { Dock = DockStyle.Fill })
            using (MiniMusicPlayerControl mini = new MiniMusicPlayerControl { Size = new Size(320, 48) })
            {
                host.Controls.Add(mini);
                host.Controls.Add(player);
                host.Show();
                player.Open(new List<string> { first, second }, 0);
                Check(PumpUntil(delegate { return string.Equals(player.CurrentPath, second, StringComparison.OrdinalIgnoreCase); }, 15000),
                    "full player did not advance after the song ended (current: " + player.CurrentPath + ")");
                player.Visible = false;
                mini.LoadPlaylist(new List<string> { first, second }, "回归", "regression", 0);
                Check(PumpUntil(delegate { return mini.CurrentIndex == 1; }, 15000), "mini player did not advance after the song ended");
                mini.Stop();
                host.Hide();
            }
        }

        private static bool EngineFlag(object control, string property)
        {
            object engine = control.GetType().GetField("engine", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(control);
            if (engine == null) return false;
            object[] args = { property, false };
            bool ok = (bool)engine.GetType().GetMethod("TryGetFlag").Invoke(engine, args);
            return ok && (bool)args[1];
        }

        private static void MusicSilencedWhenUnreachable()
        {
            string runtime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppManager");
            if (!File.Exists(Path.Combine(runtime, "libmpv-2.dll"))) { Console.WriteLine("  SKIP: libmpv-2.dll not installed"); return; }
            SetDllDirectory(runtime);
            string song = WriteSilence("long-song.wav", 8);
            string missing = Path.Combine(root, "deleted-song.wav");
            using (Form host = new Form { ShowInTaskbar = false, Size = new Size(900, 640), StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) })
            using (MusicPlayerControl player = new MusicPlayerControl { Dock = DockStyle.Fill })
            using (MiniMusicPlayerControl mini = new MiniMusicPlayerControl { Size = new Size(320, 48) })
            {
                host.Controls.Add(mini);
                host.Controls.Add(player);
                host.Show();
                player.Open(new List<string> { song, missing }, 0);
                Check(PumpUntil(delegate { return !EngineFlag(player, "idle-active") && !EngineFlag(player, "pause"); }, 10000), "full player never started");
                player.StepFromMiniPlayer(1);   // 下一首已被删除
                Check(PumpUntil(delegate { return EngineFlag(player, "idle-active"); }, 5000), "previous song kept playing after switching to a missing file");
                player.Visible = false;
                mini.LoadPlaylist(new List<string> { song }, "回归", "regression", 0);
                Check(PumpUntil(delegate { return !EngineFlag(mini, "pause") && !EngineFlag(mini, "idle-active"); }, 10000), "mini player never started");
                mini.PauseOwnPlayback();
                Check(PumpUntil(delegate { return EngineFlag(mini, "pause"); }, 5000), "hidden mini player kept playing");
                mini.Stop();
                host.Hide();
            }
        }

        private static void TextSafety()
        {
            // 合法 UTF-8、带 BOM 的 UTF-8、UTF-16 与合法的系统编码文本都能无损往返，可以编辑。
            foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true), Encoding.Unicode })
            {
                string ok = WriteText("roundtrip.txt", "中文 abc\r\n第二行", encoding);
                Check(TextPreview.Read(ok, CancellationToken.None).ReadOnlyReason == null, "lossless " + encoding.WebName + " opened read-only");
            }
            string ansi = WriteText("roundtrip-ansi.txt", Encoding.Default.GetString(Encoding.Default.GetBytes("中文ABC")), Encoding.Default);
            Check(TextPreview.Read(ansi, CancellationToken.None).ReadOnlyReason == null, "lossless code page text opened read-only");
            // 带 UTF-8 BOM 但内容含非法字节：解码会替换字符，必须只读。
            string bomBroken = Path.Combine(root, "bom-broken.txt");
            File.WriteAllBytes(bomBroken, new byte[] { 0xEF, 0xBB, 0xBF, 0x61, 0xFF, 0xFE, 0x62 });
            Check(TextPreview.Read(bomBroken, CancellationToken.None).ReadOnlyReason != null, "lossy BOM text is editable");
            // 不是 UTF-8、系统编码也无法表示的字节序列（如 GBK 下的 0x80/0xFF）：必须只读。
            string legacyBroken = Path.Combine(root, "legacy-broken.txt");
            File.WriteAllBytes(legacyBroken, new byte[] { 0x41, 0xFF, 0x80, 0x42, 0x81 });
            TextPreview broken = TextPreview.Read(legacyBroken, CancellationToken.None);
            bool exact = TextPreview.RoundTrips(File.ReadAllBytes(legacyBroken), broken.Content, broken.Encoding);
            Check(exact == (broken.ReadOnlyReason == null), "read-only flag disagrees with round trip");
            if (Encoding.Default.CodePage == 936) Check(broken.ReadOnlyReason != null, "lossy GBK text is editable");
            // 换行：混用才提示。
            Check(TextPreview.HasMixedNewlines("a\r\nb\nc") && TextPreview.HasMixedNewlines("a\rb\nc")
                && !TextPreview.HasMixedNewlines("a\r\nb\r\nc") && !TextPreview.HasMixedNewlines("a\nb\n"), "mixed newline detection");
            string mixed = WriteText("mixed.csv", "h1,h2\r\n\"x\ny\",z\r\n", new UTF8Encoding(false));
            Check(TextPreview.Read(mixed, CancellationToken.None).MixedNewlines, "mixed newline file not flagged");
            // 写入失败（只读目标）：原文件内容不变，不留临时文件或备份。
            string locked = WriteText("locked.txt", "original", new UTF8Encoding(false));
            File.SetAttributes(locked, FileAttributes.ReadOnly);
            try
            {
                bool failed = false;
                try { TextPreview.Write(locked, "changed", new UTF8Encoding(false)); }
                catch (UnauthorizedAccessException) { failed = true; }
                catch (IOException) { failed = true; }
                Check(failed && File.ReadAllText(locked) == "original", "read-only target was modified");
            }
            finally { File.SetAttributes(locked, FileAttributes.Normal); }
            TextPreview.Write(locked, "saved", new UTF8Encoding(false));
            Check(File.ReadAllText(locked) == "saved", "save after unlock failed");
            Check(Directory.GetFiles(root, ".locked.txt.*").Length == 0, "temporary or backup file left behind");
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
        // 窗体未显示时 Control.Visible 恒为 false，读取控件自身的可见标志
        private static bool Shown(Control control)
        { return (bool)typeof(Control).GetMethod("GetState", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(control, new object[] { 2 }); }
        private static string RenderedText(string markdown)
        {
            using (RichTextBox box = new RichTextBox())
            {
                box.Rtf = MarkdownRtf.Render(markdown, 9000);
                return box.Text;
            }
        }
        private static void MarkdownHostile()
        {
            Stopwatch watch = Stopwatch.StartNew();
            // 每层引用/链接都会递归：没有上限时这些输入会把调用栈压穿，进程直接退出。
            string quotes = new string('>', 100000) + " deep";
            Check(MarkdownRtf.Render(quotes, 9000).Contains("deep"), "deep quote lost its text");
            StringBuilder links = new StringBuilder();
            for (int i = 0; i < 20000; i++) links.Append('[');
            links.Append("core");
            for (int i = 0; i < 20000; i++) links.Append("](u)");
            Check(MarkdownRtf.Render(links.ToString(), 9000).Contains("core"), "deep link lost its text");
            StringBuilder nested = new StringBuilder();   // 不超过长度上限，专门走嵌套深度上限
            for (int i = 0; i < 1000; i++) nested.Append('[');
            nested.Append("mid");
            for (int i = 0; i < 1000; i++) nested.Append("](u)");
            Check(nested.Length < MarkdownRtf.MaxInlineLength && MarkdownRtf.Render(nested.ToString(), 9000).Contains("mid"), "nested link lost its text");
            StringBuilder lines = new StringBuilder();
            for (int depth = 1; depth <= MarkdownRtf.MaxBlockDepth + 8; depth++)
                lines.Append(new string('>', depth)).Append(" level").Append(depth).Append('\n');
            Check(MarkdownRtf.Render(lines.ToString(), 9000).Contains("level" + (MarkdownRtf.MaxBlockDepth + 8)), "text beyond depth limit dropped");
            // 未闭合的起始符：每个都会扫描到行尾，超长段落必须跳过行内解析。
            StringBuilder emphasis = new StringBuilder();
            for (int i = 0; i < 30000; i++) emphasis.Append("*a [b ");
            MarkdownRtf.Render(emphasis.ToString(), 9000);
            Check(watch.ElapsedMilliseconds < 8000, "hostile markdown took " + watch.ElapsedMilliseconds + " ms");
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                bool canceled = false;
                try { MarkdownRtf.Render("# a\n\nb\n", 9000, cancel.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Check(canceled, "markdown render ignored cancellation");
            }
        }

        private static void MarkdownRendering()
        {
            string text = RenderedText("# 标题 #\n\n**粗体** *斜体* ~~删除~~ `a{b}\\c` [链接](http://x) ![图](a.png)\n"
                + "- 项目\n  - [x] 完成\n1. 第一\n2. 第二\n\n> 引用\n\n| 列A | 列B |\n|:--|--:|\n| 1 | `x|y` |\n\n"
                + "```\ncode {block}\n```\n\n---\nsnake_case_name \\*星号\\* <!-- 注释 -->\n");
            foreach (string expected in new string[] { "标题", "粗体", "斜体", "删除", "a{b}\\c", "链接", "[图片：图]", "项目", "\u2611",
                "1.", "2.", "第二", "引用", "列A", "列B", "x|y", "code {block}", "snake_case_name", "*星号*" })
                Check(text.Contains(expected), "missing rendered text: " + expected);
            foreach (string syntax in new string[] { "# ", "**", "~~", "`", "](", "```", "<!--", "|:--" })
                Check(!text.Contains(syntax), "markdown syntax leaked: " + syntax);
            Check(RenderedText("plain <b>html</b> 1 < 2 * 3").Contains("plain <b>html</b> 1 < 2 * 3"), "unsupported text altered");
            Check(RenderedText(string.Empty) != null && RenderedText("\uD83D\uDE00 emoji").Contains("\uD83D\uDE00"), "surrogates");
            Stopwatch elapsed = Stopwatch.StartNew();
            StringBuilder large = new StringBuilder();
            while (large.Length < TextPreview.MaxCharacters)
                large.Append("## 小节\n\n段落 **加粗** `代码` [链接](u) <https://a.b> 文本 *强调* _下划线_\n- 列表项\n\n");
            string rtf = MarkdownRtf.Render(large.ToString(), 9000);
            Console.WriteLine("  256K markdown -> RTF: " + elapsed.ElapsedMilliseconds + " ms, " + rtf.Length / 1024 + " KiB");
            Check(elapsed.ElapsedMilliseconds < 3000, "markdown render too slow");
        }
        private static void MarkdownViewSwitch()
        {
            using (Form form = new Form())
            using (TextViewerControl viewer = new TextViewerControl())
            {
                form.Controls.Add(viewer); viewer.Size = new Size(800, 600);
                IntPtr handle = form.Handle; handle = viewer.Handle;
                RichTextBox text = (RichTextBox)Field(viewer, "textBox"), markdown = (RichTextBox)Field(viewer, "markdownBox");
                string md = WriteText("note.md", "# 标题\n\n正文", new UTF8Encoding(false));
                viewer.Open(new List<string> { md }, 0);
                Pump(delegate { return !((bool)Field(viewer, "loading")); });
                Check(Shown(markdown) && !Shown(text) && markdown.Text.Contains("标题") && !markdown.Text.Contains("#"), "markdown preview default");
                Call(viewer, "SetMarkdownView", false);
                Check(Shown(text) && !Shown(markdown) && !text.ReadOnly && text.Text.StartsWith("# 标题"), "plain text view");
                text.Select(text.TextLength, 0); text.SelectedText = "\n\n**新增**";
                Check((bool)Field(viewer, "dirty") && Shown((Control)Field(viewer, "saveButton")), "edit not tracked");
                Call(viewer, "SetMarkdownView", true);
                Check(markdown.Text.Contains("新增") && !markdown.Text.Contains("**"), "preview missed live edit");
                Call(viewer, "SetMarkdownView", false);
                Check(text.Text.EndsWith("**新增**") && (bool)Field(viewer, "dirty"), "edit lost across view switch");
                Check((bool)Call(viewer, "Save"), "save failed");
                Check(File.ReadAllText(md) == "# 标题\n\n正文\n\n**新增**", "markdown saved content");
                string txt = WriteText("plain.txt", "abc", Encoding.UTF8);
                viewer.Open(new List<string> { txt }, 0);
                Pump(delegate { return !((bool)Field(viewer, "loading")); });
                Check(Shown(text) && !Shown(markdown) && !text.ReadOnly && ((Rectangle)Field(viewer, "markdownTabRect")).IsEmpty, "plain text has md switch");
                // 程序加载与重新打开不应产生未保存状态（否则关闭时会弹窗阻塞）
                Check(!(bool)Field(viewer, "dirty") && viewer.ConfirmLeave(), "load marked dirty");
                StringBuilder large = new StringBuilder();
                while (large.Length < TextPreview.MaxCharacters)
                    large.Append("## 小节\n\n段落 **加粗** `代码`\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n```\ncode\n```\n\n");
                string big = WriteText("big.md", large.ToString(), new UTF8Encoding(false));
                Call(viewer, "SetMarkdownView", true);
                Check(!(bool)Field(viewer, "showMarkdown"), "view switch applied without markdown file");
                Set(viewer, "showMarkdown", true);
                Stopwatch elapsed = Stopwatch.StartNew();
                viewer.Open(new List<string> { big }, 0);
                Pump(delegate { return !((bool)Field(viewer, "loading")); });
                Console.WriteLine("  256K markdown preview load/display: " + elapsed.ElapsedMilliseconds + " ms");
                Check(markdown.TextLength > 0, "large markdown preview empty");
                viewer.ClosePreview();
            }
        }
        private static void TextSaving()
        {
            using (Form form = new Form())
            using (TextViewerControl viewer = new TextViewerControl())
            {
                form.Controls.Add(viewer); viewer.Size = new Size(800, 600);
                IntPtr handle = form.Handle; handle = viewer.Handle;
                RichTextBox text = (RichTextBox)Field(viewer, "textBox");
                Encoding[] encodings = { new UTF8Encoding(false), new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.Default };
                foreach (Encoding encoding in encodings)
                    foreach (string newline in new string[] { "\r\n", "\n" })
                    {
                        string path = WriteText("save.txt", "第一行" + newline + "second", encoding);
                        viewer.Open(new List<string> { path }, 0);
                        Pump(delegate { return !((bool)Field(viewer, "loading")); });
                        text.Select(text.TextLength, 0); text.SelectedText = "\nthird";
                        Check((bool)Call(viewer, "Save"), "save failed: " + encoding.WebName);
                        byte[] expected = Concat(encoding.GetPreamble(), encoding.GetBytes("第一行" + newline + "second" + newline + "third"));
                        Check(Convert.ToBase64String(File.ReadAllBytes(path)) == Convert.ToBase64String(expected),
                            "bytes differ: " + encoding.WebName + " " + (newline == "\n" ? "LF" : "CRLF"));
                        Check(!(bool)Field(viewer, "dirty") && !Shown((Control)Field(viewer, "saveButton")), "saved state");
                    }
                Check(Directory.GetFiles(root, ".save.txt.*").Length == 0, "temporary file left behind");
                string truncatedPath = WriteText("huge.txt", new string('x', TextPreview.MaxCharacters + 1), new UTF8Encoding(false));
                viewer.Open(new List<string> { truncatedPath }, 0);
                Pump(delegate { return !((bool)Field(viewer, "loading")); });
                Check(text.ReadOnly, "truncated text editable");
                viewer.ClosePreview();
            }
            if (Encoding.Default.IsSingleByte || Encoding.Default.CodePage == 936)
            {
                string legacy = WriteText("legacy.txt", "abc", Encoding.Default);
                Expect<EncoderFallbackException>(delegate { TextPreview.Write(legacy, "\uD83D\uDE00", Encoding.Default); });
                Check(File.ReadAllText(legacy, Encoding.Default) == "abc", "failed write changed file");
            }
        }
        private static byte[] Concat(byte[] a, byte[] b)
        {
            byte[] result = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, result, 0, a.Length); Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
            return result;
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
            AppInfo archive = new AppInfo { TargetPath = @"C:\not-real\pack.tar.gz", IsFile = true };
            Check(!(bool)Call(form, "IsEmbeddedViewerItem", archive), "software archive captured");
            Set(form, "contentMode", SidebarControl.ContentMode.Website);
            Check((bool)Call(form, "IsEmbeddedViewerItem", script), "resource script not previewed");
            Check((bool)Call(form, "IsEmbeddedViewerItem", archive), "resource archive not previewed");
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

        // ===== 压缩包 =====

        private static readonly string[][] ArchiveSample =
        {
            new string[] { "说明 文件.txt", "hello" },
            new string[] { "子目录 a/图片.png", new string('x', 5000) },
            new string[] { "子目录 a/深层/e.md", "# t" },
            new string[] { "空目录/", null },
            new string[] { "./点开头.txt", "1" },
            new string[] { "a\\b.txt", "22" },
            new string[] { "file10.txt", "0123456789" },
            new string[] { "file2.txt", "01" },
        };

        /// <summary>names 为 null 时由 ZipArchive 自行决定（非 ASCII 名设置 UTF-8 标记）。</summary>
        private static string MakeZip(string name, Encoding names, string[][] entries)
        {
            string path = Path.Combine(root, name);
            if (File.Exists(path)) File.Delete(path);
            using (FileStream stream = new FileStream(path, FileMode.CreateNew))
            using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Create, false, names))
                foreach (string[] e in entries)
                {
                    ZipArchiveEntry entry = zip.CreateEntry(e[0]);
                    entry.LastWriteTime = new DateTimeOffset(2024, 5, 6, 7, 8, 0, TimeSpan.Zero);
                    if (e[1] != null)
                        using (Stream body = entry.Open()) { byte[] data = Encoding.UTF8.GetBytes(e[1]); body.Write(data, 0, data.Length); }
                }
            return path;
        }

        private static ArchiveEntryInfo Find(ArchivePreview preview, string path)
        {
            ArchiveEntryInfo node = preview.Root;
            foreach (string part in path.Split('/'))
            {
                string name = part;
                node = node.Children == null ? null : node.Children.Find(delegate(ArchiveEntryInfo c) { return c.Name == name; });
                if (node == null) return null;
            }
            return node;
        }

        private static void ArchiveZip()
        {
            List<Encoding> encodings = new List<Encoding> { null, new UTF8Encoding(false) };
            if (Encoding.Default.CodePage == 936) encodings.Add(Encoding.Default);
            foreach (Encoding names in encodings)
            {
                string label = names == null ? "flagged UTF-8" : names is UTF8Encoding ? "unflagged UTF-8" : "GBK";
                string path = MakeZip("names.zip", names, ArchiveSample);
                // 测试前提：后两种文件名确实没有 UTF-8 标记（通用标志位 bit 11）
                byte[] head = File.ReadAllBytes(path);
                Check(((head[7] & 0x08) != 0) == (names == null), label + ": unexpected UTF-8 flag");
                ArchivePreview p = ArchivePreview.Read(path, CancellationToken.None);
                Check(p.FileCount == 7 && p.FolderCount == 4, label + ": counts " + p.FileCount + "/" + p.FolderCount);
                Check(p.TotalSize == 5 + 5000 + 3 + 1 + 2 + 10 + 2 && !p.SizeUnknown, label + ": total " + p.TotalSize);
                Check(p.ArchiveBytes == new FileInfo(path).Length, label + ": archive bytes");
                ArchiveEntryInfo sub = Find(p, "子目录 a");
                Check(sub != null && sub.IsFolder && sub.Size == 5003 && sub.FileCount == 2 && sub.FolderCount == 1, label + ": folder aggregate");
                ArchiveEntryInfo md = Find(p, "子目录 a/深层/e.md");
                // zip 记录的是本地时钟时间（DOS 时间，无时区）
                Check(md != null && md.Size == 3 && md.Modified == new DateTime(2024, 5, 6, 7, 8, 0),
                    label + ": nested file " + (md == null ? "missing" : md.Size + " " + md.Modified.ToString("o")));
                Check(Find(p, "说明 文件.txt") != null && Find(p, "点开头.txt") != null && Find(p, "a/b.txt") != null, label + ": names / normalization");
                Check(Find(p, "空目录") != null && Find(p, "空目录").IsFolder && Find(p, "空目录").Children.Count == 0, label + ": explicit empty folder");
                Check(p.Root.Children.TrueForAll(delegate(ArchiveEntryInfo c) { return c.Name.Length > 0 && c.Name != "."; }), label + ": empty segment node");
            }
        }

        private static string SystemTar()
        {
            string tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
            return File.Exists(tar) && File.Exists(Path.Combine(Environment.SystemDirectory, "archiveint.dll")) ? tar : null;
        }

        private static void RunTar(string tar, string arguments)
        {
            ProcessStartInfo info = new ProcessStartInfo(tar, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            using (Process process = Process.Start(info))
            {
                string error = process.StandardError.ReadToEnd();
                Check(process.WaitForExit(60000) && process.ExitCode == 0, "tar " + arguments + " failed: " + error);
            }
        }

        private static void ArchiveLibArchive()
        {
            string tar = SystemTar();
            if (tar == null) { Console.WriteLine("  skipped: system libarchive (tar.exe / archiveint.dll) not present"); return; }
            string src = Path.Combine(root, "arc-src");
            Directory.CreateDirectory(Path.Combine(src, "子目录 a", "深层"));
            File.WriteAllText(Path.Combine(src, "说明 文件.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "子目录 a", "图片.png"), new string('x', 5000));
            File.WriteAllText(Path.Combine(src, "子目录 a", "深层", "e.md"), "# t");
            File.WriteAllText(Path.Combine(src, "数据 2024.csv"), "a,b");
            string[][] formats =
            {
                new string[] { "t.tar.gz", "-czf" }, new string[] { "t.tar.xz", "-cJf" }, new string[] { "t.tar.bz2", "-cjf" },
                new string[] { "t.tar.zst", "--zstd -cf" }, new string[] { "t.7z", "--format 7zip -cf" }, new string[] { "t.tar", "-cf" },
            };
            foreach (string[] format in formats)
            {
                string path = Path.Combine(root, format[0]);
                RunTar(tar, format[1] + " \"" + path + "\" -C \"" + src + "\" .");
                ArchivePreview p = ArchivePreview.Read(path, CancellationToken.None);
                Check(p.FileCount == 4 && p.FolderCount == 2, format[0] + ": counts " + p.FileCount + "/" + p.FolderCount);
                Check(Find(p, "数据 2024.csv") != null && Find(p, "说明 文件.txt") != null, format[0] + ": unicode names");
                ArchiveEntryInfo png = Find(p, "子目录 a/图片.png");
                Check(png != null && png.Size == 5000 && png.Modified != DateTime.MinValue, format[0] + ": nested entry");
                Check(p.TotalSize == 5 + 5000 + 3 + 3 && p.Warning == null && !p.Truncated, format[0] + ": totals");
            }

            // ZipArchive 读不了的 zip 走 libarchive：emoji（增补平面）名与 GBK 名都要正确
            // （系统 tar.exe 打包含 emoji 的文件名会崩溃，这里用 .NET 生成 zip 后直接调用 libarchive 路径）
            MethodInfo viaLibArchive = typeof(ArchivePreview).GetNestedType("LibArchive", BindingFlags.NonPublic)
                .GetMethod("Read", BindingFlags.Public | BindingFlags.Static);
            string[][] emojiSample = { new string[] { "😀 表情.txt", "emoji" }, new string[] { "目录/说明.txt", "x" } };
            List<Encoding> zipNames = new List<Encoding> { null };
            if (Encoding.Default.CodePage == 936) zipNames.Add(Encoding.Default);
            foreach (Encoding names in zipNames)
            {
                string[][] sample = names == null ? emojiSample : new string[][] { emojiSample[1], new string[] { "中文 名.txt", "y" } };
                string path = MakeZip("libarchive.zip", names, sample);
                ArchivePreview p = (ArchivePreview)viaLibArchive.Invoke(null, new object[] { path, CancellationToken.None });
                foreach (string[] e in sample)
                    Check(Find(p, e[0]) != null, (names == null ? "UTF-8" : "GBK") + " zip via libarchive lost name: " + e[0]);
            }

            // 单文件压缩流：列出去掉 .gz 后的文件名，大小未知
            string gz = Path.Combine(root, "log.txt.gz");
            using (FileStream stream = File.Create(gz))
            using (GZipStream zip = new GZipStream(stream, CompressionMode.Compress))
            { byte[] data = Encoding.UTF8.GetBytes("hello world"); zip.Write(data, 0, data.Length); }
            ArchivePreview raw = ArchivePreview.Read(gz, CancellationToken.None);
            Check(raw.FileCount == 1 && raw.Root.Children[0].Name == "log.txt" && raw.Root.Children[0].Size == -1 && raw.SizeUnknown, "raw gz");

            // 扩展名是压缩包、内容不是：raw 兜底不能把任意文件当成单文件压缩流
            string fake = WriteText("fake.gz", "not a gzip stream", Encoding.ASCII);
            Expect<InvalidDataException>(delegate { ArchivePreview.Read(fake, CancellationToken.None); });
            string junk = Path.Combine(root, "junk.rar");
            byte[] noise = new byte[4096]; new Random(7).NextBytes(noise); File.WriteAllBytes(junk, noise);
            try { ArchivePreview.Read(junk, CancellationToken.None); throw new Exception("junk rar accepted"); }
            catch (InvalidDataException ex) { Check(ex.Message == "无法识别的压缩包格式", "junk message: " + ex.Message); }
            // mtree 清单伪装成 .tar：不能被当成压缩包解析（mtree 读取器会按 contents= 访问外部/网络路径）
            string mtreeTarget = WriteText("mtree-target.txt", "x", Encoding.ASCII);
            string mtree = WriteText("mtree.tar", "#mtree\n./probe.txt type=file size=1 contents=" + mtreeTarget.Replace('\\', '/') + "\n", Encoding.ASCII);
            Expect<InvalidDataException>(delegate { ArchivePreview.Read(mtree, CancellationToken.None); });
            // 0 字节的 .zip：libarchive 视为空归档，查看器显示「压缩包为空」
            string emptyZip = WriteText("empty.zip", "", Encoding.ASCII);
            ArchivePreview empty = ArchivePreview.Read(emptyZip, CancellationToken.None);
            Check(empty.FileCount == 0 && empty.Root.Children.Count == 0, "empty zip");

            // 下载不完整的 tar.gz：列出已读到的部分并给出提示
            string many = Path.Combine(root, "arc-many");
            Directory.CreateDirectory(many);
            Random random = new Random(11);
            for (int i = 0; i < 20; i++) { byte[] data = new byte[20000]; random.NextBytes(data); File.WriteAllBytes(Path.Combine(many, "f" + i + ".bin"), data); }
            string full = Path.Combine(root, "many.tar.gz");
            RunTar(tar, "-czf \"" + full + "\" -C \"" + many + "\" .");
            byte[] bytes = File.ReadAllBytes(full);
            string cut = Path.Combine(root, "cut.tar.gz");
            byte[] half = new byte[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, half, 0, half.Length);
            File.WriteAllBytes(cut, half);
            ArchivePreview partial = ArchivePreview.Read(cut, CancellationToken.None);
            Check(partial.Warning != null && partial.FileCount > 0 && partial.FileCount < 20, "truncated: " + partial.FileCount + " " + partial.Warning);
        }

        private static void ArchiveSharing()
        {
            string zip = MakeZip("shared.zip", null, ArchiveSample);
            List<string> targets = new List<string> { zip };
            string tar = SystemTar();
            if (tar != null)
            {
                string src = Path.Combine(root, "arc-share");
                Directory.CreateDirectory(src);
                File.WriteAllText(Path.Combine(src, "a.txt"), "a");
                string tgz = Path.Combine(root, "shared.tar.gz");
                RunTar(tar, "-czf \"" + tgz + "\" -C \"" + src + "\" .");
                targets.Add(tgz);
            }
            foreach (string path in targets)
            {
                // 其他程序正以读写方式打开文件时仍能列出内容
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                    Check(ArchivePreview.Read(path, CancellationToken.None).FileCount > 0, "shared read: " + path);
                Expect<OperationCanceledException>(delegate { ArchivePreview.Read(path, new CancellationToken(true)); });
                ArchivePreview.Read(path, CancellationToken.None);
                string moved = path + ".moved";
                File.Move(path, moved); File.Delete(moved);   // 读取结束后不残留句柄
            }
        }

        private static void ArchiveViewer()
        {
            using (Form form = new Form())
            using (ArchiveViewerControl viewer = new ArchiveViewerControl())
            {
                form.Controls.Add(viewer); viewer.Size = new Size(900, 600);
                IntPtr handle = form.Handle; handle = viewer.Handle;
                string first = MakeZip("view1.zip", null, ArchiveSample);
                string second = MakeZip("view2.zip", null, new string[][] { new string[] { "only.txt", "x" } });
                string broken = WriteText("broken.zip", "PK not really", Encoding.ASCII);
                for (int i = 0; i < 30; i++) { viewer.Open(new List<string> { first, second }, i % 2); viewer.ClosePreview(); }
                viewer.Open(new List<string> { first, second }, 0);
                Pump(delegate { return !(bool)Field(viewer, "loading"); });
                Check(Field(viewer, "current") != null, "zip not loaded: " + Field(viewer, "failureText"));
                List<ArchiveEntryInfo> rows = (List<ArchiveEntryInfo>)Field(viewer, "rows");
                ListView list = (ListView)Field(viewer, "list");
                // 窗体未显示时子控件不建句柄；虚拟列表的选中状态依赖句柄
                handle = list.Handle;
                Check(rows.Count == 7 && rows[0] != null && list.VirtualListSize == rows.Count && Shown(list), "root rows " + rows.Count);
                int lastFolder = rows.FindLastIndex(delegate(ArchiveEntryInfo r) { return r.IsFolder; });
                int firstFile = rows.FindIndex(delegate(ArchiveEntryInfo r) { return !r.IsFolder; });
                Check(lastFolder < firstFile, "folders not first");
                Check(rows.FindIndex(delegate(ArchiveEntryInfo r) { return r.Name == "file2.txt"; })
                    < rows.FindIndex(delegate(ArchiveEntryInfo r) { return r.Name == "file10.txt"; }), "natural order");
                Check(((string)Call(viewer, "Summary")).Contains("7 个文件"), "summary: " + Call(viewer, "Summary"));
                for (int i = 0; i < rows.Count; i++) list.Items[i].ToString();   // 每一行都能生成列表项

                ArchiveEntryInfo sub = rows.Find(delegate(ArchiveEntryInfo r) { return r.Name == "子目录 a"; });
                Call(viewer, "ShowFolder", sub, null);
                Check(rows.Count == 3 && rows[0] == null && rows[1].Name == "深层" && rows[2].Name == "图片.png", "sub rows");
                Check(list.SelectedIndices.Count == 1 && list.SelectedIndices[0] == 1, "entering a folder should select its first entry, not the up row");
                list.SelectedIndices.Clear(); list.SelectedIndices.Add(0);
                Call(viewer, "SortBy", 1);
                Check(list.SelectedIndices.Count == 1 && list.SelectedIndices[0] == 0 && rows[0] == null, "up row selection lost by sort");
                Check(HeaderFormat(list, 1) == 0x0200 && HeaderFormat(list, 0) == 0, "sort arrow: name=" + HeaderFormat(list, 0) + " size=" + HeaderFormat(list, 1));
                Call(viewer, "SortBy", 0);
                Check(HeaderFormat(list, 0) == 0x0400 && HeaderFormat(list, 1) == 0, "sort arrow reset");
                System.Collections.IList crumbs = (System.Collections.IList)Field(viewer, "crumbs");
                FieldInfo target = crumbs.Count > 0 ? crumbs[0].GetType().GetField("Target") : null;
                Check(crumbs.Count == 2 && target.GetValue(crumbs[0]) != null && target.GetValue(crumbs[1]) == null, "crumbs " + crumbs.Count);
                Call(viewer, "GoUp");
                Check(Field(viewer, "folder") == Field(viewer, "current").GetType().GetField("Root").GetValue(Field(viewer, "current"))
                    && list.SelectedIndices.Count == 1 && rows[list.SelectedIndices[0]] == sub, "up did not reselect origin folder");

                Call(viewer, "SortBy", 1);   // 大小：默认从大到小，文件夹仍在前
                List<ArchiveEntryInfo> files = rows.FindAll(delegate(ArchiveEntryInfo r) { return !r.IsFolder; });
                for (int i = 1; i < files.Count; i++) Check(files[i - 1].Size >= files[i].Size, "size order");
                Check(rows.FindLastIndex(delegate(ArchiveEntryInfo r) { return r.IsFolder; }) < rows.FindIndex(delegate(ArchiveEntryInfo r) { return !r.IsFolder; }), "folders first after sort");
                Call(viewer, "SortBy", 0);
                Check(rows.FindIndex(delegate(ArchiveEntryInfo r) { return r.Name == "file2.txt"; })
                    < rows.FindIndex(delegate(ArchiveEntryInfo r) { return r.Name == "file10.txt"; }), "name order restored");

                list.SelectedIndices.Clear(); list.SelectedIndices.Add(firstFile);
                Call(viewer, "ActivateSelected");
                Check(Field(viewer, "hint") != null && Field(viewer, "folder") != null, "file activation hint");

                Call(viewer, "Step", 1);
                Pump(delegate { return !(bool)Field(viewer, "loading"); });
                Check((int)Field(viewer, "index") == 1 && rows.Count == 1 && rows[0].Name == "only.txt", "step to next archive");
                Check(Field(viewer, "hint") == null, "hint carried over to another archive");

                viewer.Open(new List<string> { broken }, 0);
                Pump(delegate { return !(bool)Field(viewer, "loading"); });
                Check((bool)Field(viewer, "loadFailed") && ((string)Field(viewer, "failureText")).StartsWith("无法读取此压缩包：") && !Shown(list), "broken zip");
                viewer.ClosePreview();
                Check(rows.Count == 0 && list.VirtualListSize == 0 && !Shown(viewer), "closed viewer retained rows");
            }
            Application.DoEvents();
        }

        // ===== 手工构造 tar（GNU 风格原始字节名称、pax 超长路径）=====

        private static void Octal(byte[] header, int offset, int length, long value)
        {
            Encoding.ASCII.GetBytes(Convert.ToString(value, 8).PadLeft(length - 1, '0')).CopyTo(header, offset);
            header[offset + length - 1] = 0;
        }

        private static byte[] TarHeader(byte[] name, long size, char type)
        {
            byte[] h = new byte[512];
            Array.Copy(name, h, Math.Min(100, name.Length));
            Octal(h, 100, 8, 420); Octal(h, 108, 8, 0); Octal(h, 116, 8, 0);
            Octal(h, 124, 12, size); Octal(h, 136, 12, 1700000000);
            h[156] = (byte)type;
            Encoding.ASCII.GetBytes("ustar").CopyTo(h, 257); h[263] = (byte)'0'; h[264] = (byte)'0';
            for (int i = 148; i < 156; i++) h[i] = 32;
            int sum = 0; foreach (byte b in h) sum += b;
            Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0')).CopyTo(h, 148); h[154] = 0; h[155] = 32;
            return h;
        }

        private static void TarBlock(Stream output, byte[] header, byte[] data)
        {
            output.Write(header, 0, 512);
            output.Write(data, 0, data.Length);
            if (data.Length % 512 != 0) output.Write(new byte[512 - data.Length % 512], 0, 512 - data.Length % 512);
        }

        /// <summary>names 为原始字节（不经 pax 转码，同 GNU tar 默认格式）；paxPath 非空时追加一个用 pax 记录写长路径的文件。</summary>
        private static string MakeTar(string fileName, List<byte[]> names, string paxPath)
        {
            string path = Path.Combine(root, fileName);
            using (FileStream output = File.Create(path))
            {
                foreach (byte[] name in names) TarBlock(output, TarHeader(name, 1, '0'), new byte[] { 65 });
                if (paxPath != null)
                {
                    byte[] value = Encoding.UTF8.GetBytes(paxPath);
                    int body = 1 + "path=".Length + value.Length + 1;   // 空格 + 键= + 值 + 换行
                    int length = body + body.ToString().Length;
                    if (length.ToString().Length != body.ToString().Length) length++;
                    byte[] record = Concat(Concat(Encoding.ASCII.GetBytes(length + " path="), value), new byte[] { 10 });
                    TarBlock(output, TarHeader(Encoding.ASCII.GetBytes("PaxHeader/deep"), record.Length, 'x'), record);
                    TarBlock(output, TarHeader(Encoding.ASCII.GetBytes("deep-placeholder"), 1, '0'), new byte[] { 66 });
                }
                output.Write(new byte[1024], 0, 1024);
            }
            return path;
        }

        private static void ArchiveHostile()
        {
            if (SystemTar() == null) { Console.WriteLine("  skipped: system libarchive not present"); return; }
            // GNU 风格原始字节名称：UTF-8、GBK，以及 .NET Framework 路径函数会拒绝的字符
            string[] odd = { "utf8-中文.txt", "bad|name.txt", "quote\"x.md", "angle<>.log", "ctl\u0001.bin", "dir|x/inner.txt" };
            List<byte[]> names = new List<byte[]>();
            foreach (string name in odd) names.Add(Encoding.UTF8.GetBytes(name));
            if (Encoding.Default.CodePage == 936) names.Add(Encoding.Default.GetBytes("gbk-中文.txt"));
            string tar = MakeTar("odd.tar", names, null);
            ArchivePreview p = ArchivePreview.Read(tar, CancellationToken.None);
            foreach (string expected in odd)
                Check(Find(p, expected) != null, "raw tar name lost or garbled: " + expected);
            if (Encoding.Default.CodePage == 936) Check(Find(p, "gbk-中文.txt") != null, "GBK tar name garbled");
            using (Form form = new Form())
            using (ArchiveViewerControl viewer = new ArchiveViewerControl())
            {
                form.Controls.Add(viewer); viewer.Size = new Size(900, 600);
                IntPtr handle = form.Handle; handle = viewer.Handle;
                viewer.Open(new List<string> { tar }, 0);
                Pump(delegate { return !(bool)Field(viewer, "loading"); });
                Check(Field(viewer, "current") != null, "odd names: " + Field(viewer, "failureText"));
                ListView list = (ListView)Field(viewer, "list");
                foreach (int column in new int[] { 2, 1, 3, 0 })
                {
                    Call(viewer, "SortBy", column);
                    for (int i = 0; i < list.VirtualListSize; i++) list.Items[i].ToString();   // 每一行都能生成（不因名称抛异常）
                }
                viewer.ClosePreview();
            }

            // zip 条目名含「|」：ZipArchive 拒绝整个压缩包，应改由 libarchive 列出
            string zip = MakeZip("pipe.zip", null, new string[][] { new string[] { "a#b.txt", "x" }, new string[] { "ok.txt", "y" } });
            byte[] bytes = File.ReadAllBytes(zip);
            for (int i = 0; i + 2 < bytes.Length; i++) if (bytes[i] == 'a' && bytes[i + 1] == '#' && bytes[i + 2] == 'b') bytes[i + 1] = (byte)'|';
            File.WriteAllBytes(zip, bytes);
            ArchivePreview pipe = ArchivePreview.Read(zip, CancellationToken.None);
            Check(Find(pipe, "a|b.txt") != null && Find(pipe, "ok.txt") != null, "zip with | in a name not listed");

            // 超深路径：30 万层的 pax 路径不能拖死读取，也不生成海量文件夹
            StringBuilder deep = new StringBuilder();
            for (int i = 0; i < 300000; i++) deep.Append("a/");
            deep.Append("x.txt");
            string deepTar = MakeTar("deep.tar", new List<byte[]> { Encoding.ASCII.GetBytes("ok.txt") }, deep.ToString());
            Stopwatch elapsed = Stopwatch.StartNew();
            ArchivePreview deepPreview = ArchivePreview.Read(deepTar, CancellationToken.None);
            Console.WriteLine("  300k-level path: " + elapsed.ElapsedMilliseconds + " ms");
            Check(elapsed.ElapsedMilliseconds < 5000 && Find(deepPreview, "ok.txt") != null && deepPreview.FolderCount == 0
                && deepPreview.Warning != null && deepPreview.Warning.Contains("层级过深"),
                "deep path handling: files=" + deepPreview.FileCount + " folders=" + deepPreview.FolderCount + " warning=" + deepPreview.Warning);
            StringBuilder within = new StringBuilder();
            for (int i = 0; i < ArchivePreview.MaxDepth - 1; i++) within.Append("d/");
            within.Append("leaf.txt");
            ArchivePreview withinPreview = ArchivePreview.Read(MakeTar("within.tar", new List<byte[]>(), within.ToString()), CancellationToken.None);
            Check(withinPreview.FolderCount == ArchivePreview.MaxDepth - 1 && withinPreview.Warning == null,
                "path within depth limit: folders=" + withinPreview.FolderCount + " warning=" + withinPreview.Warning);

            // lz4 魔数：libarchive 的 lz4 过滤器没有内置实现，注册后会去运行外部 lz4.exe
            string sandbox = Path.Combine(root, "lz4-sandbox");
            Directory.CreateDirectory(sandbox);
            File.Copy(Assembly.GetEntryAssembly().Location, Path.Combine(sandbox, "lz4.exe"));
            string evil = Path.Combine(sandbox, "evil.rar");
            File.WriteAllBytes(evil, new byte[] { 0x04, 0x22, 0x4D, 0x18, 0x64, 0x40, 0xA7, 0, 0, 0, 0, 0, 0, 0 });
            string previous = Environment.CurrentDirectory;
            Environment.CurrentDirectory = sandbox;
            try { Expect<InvalidDataException>(delegate { ArchivePreview.Read(evil, CancellationToken.None); }); }
            finally { Environment.CurrentDirectory = previous; }
            Thread.Sleep(500);
            Check(!File.Exists(Path.Combine(sandbox, "lz4-was-run.txt")), "an external decompressor program was started");

            // 损坏的 tar.gz（解压后不是 tar）：报告无法读取，而不是显示成一个名为 x.tar 的文件
            string broken = Path.Combine(root, "broken.tar.gz");
            using (FileStream stream = File.Create(broken))
            using (GZipStream gzip = new GZipStream(stream, CompressionMode.Compress))
            { byte[] noise = new byte[3000]; new Random(3).NextBytes(noise); gzip.Write(noise, 0, noise.Length); }
            Expect<InvalidDataException>(delegate { ArchivePreview.Read(broken, CancellationToken.None); });
        }

        private static void ArchiveSortSpeed()
        {
            string path = Path.Combine(root, "sort-speed.zip");
            string[] extensions = { ".txt", ".jpg", ".pdf", ".docx", ".md", ".zip", ".mp3", ".png" };
            using (FileStream stream = File.Create(path))
            using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Create))
                for (int i = 0; i < 60000; i++) zip.CreateEntry("file" + i + extensions[i % 8], CompressionLevel.NoCompression);
            using (Form form = new Form())
            using (ArchiveViewerControl viewer = new ArchiveViewerControl())
            {
                form.Controls.Add(viewer); viewer.Size = new Size(900, 600);
                IntPtr handle = form.Handle; handle = viewer.Handle;
                viewer.Open(new List<string> { path }, 0);
                Pump(delegate { return !(bool)Field(viewer, "loading"); });
                List<ArchiveEntryInfo> rows = (List<ArchiveEntryInfo>)Field(viewer, "rows");
                foreach (int column in new int[] { 2, 2, 1, 3, 0, 0 })
                {
                    Stopwatch elapsed = Stopwatch.StartNew();
                    Call(viewer, "SortBy", column);
                    long ms = elapsed.ElapsedMilliseconds;
                    Console.WriteLine("  SortBy(" + column + ") 60k rows: " + ms + " ms");
                    Check(ms < 1000, "sorting 60k rows stalled the UI for " + ms + " ms");
                    Check(rows.Count == 60000, "rows lost by sort");
                }
                viewer.ClosePreview();
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct HDITEM
        {
            public uint mask; public int cxy; public IntPtr pszText; public IntPtr hbm; public int cchTextMax; public int fmt;
            public IntPtr lParam; public int iImage; public int iOrder; public uint type; public IntPtr pvFilter; public uint state;
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref HDITEM lParam);
        /// <summary>列头的排序箭头标志：0x400 升序，0x200 降序，0 无。</summary>
        private static int HeaderFormat(ListView list, int column)
        {
            IntPtr header = SendMessage(list.Handle, 0x101F, IntPtr.Zero, IntPtr.Zero);   // LVM_GETHEADER
            HDITEM item = new HDITEM { mask = 0x4 };                                        // HDI_FORMAT
            Check(SendMessage(header, 0x120B, (IntPtr)column, ref item) != IntPtr.Zero, "HDM_GETITEM failed");
            return item.fmt & 0x0600;
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
