using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

namespace AppManager
{
    internal static class FileImportTests
    {
        private static string root;
        private static int passed, failed;
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static void Test(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
        }
        private static ImportBatch Batch(ImportOperation operation, params string[] names)
        {
            string dir = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            ImportBatch batch = new ImportBatch { JournalDirectory = Path.Combine(dir, "journal"), Operation = operation, Category = "files" };
            Directory.CreateDirectory(Path.Combine(dir, "source"));
            foreach (string name in names)
            {
                string source = Path.Combine(dir, "source", name); File.WriteAllText(source, "content-" + name);
                batch.Items.Add(new ImportItem { Source = source, Target = Path.Combine(dir, "target", name) });
            }
            return batch;
        }
        private static ImportBatch Reload(ImportBatch batch) { return FileImport.Load(Path.Combine(batch.JournalDirectory, batch.Id + ".xml")); }
        private static string HashFile(string path)
        {
            using (SHA256 sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path)));
        }
        private static ImportItem StampedItem(string source, string target, bool linked)
        {
            File.WriteAllText(source, "original-content");
            File.WriteAllText(target, "original-content");
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
            ImportItem item = new ImportItem { Source = source, Target = target, TargetId = FileImport.Identity(target) };
            item.Files.Add(new ImportFileStamp { Relative = string.Empty, Length = new FileInfo(source).Length,
                Modified = File.GetLastWriteTimeUtc(source).Ticks, Hash = HashFile(source), TargetId = item.TargetId, Linked = linked });
            return item;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);
        private static ImportItem LinkedStampedItem(string source, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(source));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.WriteAllText(source, "original-content");
            if (!CreateHardLink(target, source, IntPtr.Zero))
                throw new IOException("hard link setup failed: " + Marshal.GetLastWin32Error());
            ImportItem item = new ImportItem { Source = source, Target = target, TargetId = FileImport.Identity(target) };
            item.Files.Add(new ImportFileStamp { Relative = string.Empty, Length = new FileInfo(source).Length,
                Modified = File.GetLastWriteTimeUtc(source).Ticks, TargetId = item.TargetId, Linked = true });
            return item;
        }
        private static ImportBatch JournalBatch(string id, ImportOperation operation, params ImportItem[] items)
        {
            ImportBatch batch = new ImportBatch { Id = id, JournalDirectory = Path.Combine(root, "journal-" + id),
                Committed = true, Resource = true, Operation = operation, Category = "照片" };
            batch.Items.AddRange(items);
            FileImport.Save(batch);
            return batch;
        }
        private static Dictionary<string, string> RegisteredExisting(List<string> paths)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths) if (File.Exists(path)) result[path] = path;
            return result;
        }
        private sealed class FailingProbeSession : IMusicProbeSession
        {
            private readonly MusicProbeStatus status;
            private readonly CancellationTokenSource cancel;
            internal FailingProbeSession(MusicProbeStatus status) { this.status = status; }
            internal FailingProbeSession(MusicProbeStatus status, CancellationTokenSource cancel) { this.status = status; this.cancel = cancel; }
            public MusicProbeResult Probe(string path, int timeoutMilliseconds, CancellationToken token)
            {
                if (cancel != null) cancel.Cancel();
                if (status == MusicProbeStatus.TimedOut) return MusicProbeResult.TimedOut(new MusicProbeInfo { Path = path, TimedOut = true });
                if (status == MusicProbeStatus.Unavailable) return MusicProbeResult.Unavailable("unavailable");
                if (status == MusicProbeStatus.Cancelled) return MusicProbeResult.Cancelled();
                return MusicProbeResult.Failed(new MusicProbeInfo { Path = path, Failed = true });
            }
            public void Dispose() { }
        }
        private sealed class AsyncSource : DataObject, ShellImportDrop.IAsyncOperation
        {
            internal int Starts, Ends, Result;
            internal uint Effect;
            public void SetAsyncMode(bool value) { }
            public void GetAsyncMode(out bool value) { value = true; }
            public void StartOperation(IntPtr reserved) { Starts++; }
            public void InOperation(out bool value) { value = Starts > Ends; }
            public void EndOperation(int result, IntPtr reserved, uint effects) { Ends++; Result = result; Effect = effects; }
        }
        private static void Run(ImportBatch batch) { FileImport.Run(batch, delegate { return true; }, null); }
        private static void WriteSized(string path, int bytes)
        {
            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write)) stream.SetLength(bytes);
        }
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint code,
            IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
        private static void MakeSparseLargerThanFreeSpace(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                uint returned;
                if (!DeviceIoControl(stream.SafeFileHandle, 0x900c4, IntPtr.Zero, 0, IntPtr.Zero, 0, out returned, IntPtr.Zero))
                    throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                stream.SetLength(new DriveInfo(Path.GetPathRoot(path)).AvailableFreeSpace + (1L << 30));
            }
        }
        private static string MusicTestRoot(string prefix)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), prefix + Guid.NewGuid().ToString("N"));
        }

        private static void WriteStream(string path, string text)
        {
            using (FileStream stream = new FileStream(CreateFile(path, 0x40000000, 0, IntPtr.Zero, 2, 0, IntPtr.Zero), FileAccess.Write))
            using (StreamWriter writer = new StreamWriter(stream)) writer.Write(text);
        }
        private static string ReadStream(string path)
        {
            Microsoft.Win32.SafeHandles.SafeFileHandle handle = CreateFile(path, 0x80000000, 1, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) return "";
            using (FileStream stream = new FileStream(handle, FileAccess.Read)) using (StreamReader reader = new StreamReader(stream)) return reader.ReadToEnd();
        }

        [STAThread]
        private static int Main()
        {
            root = Path.Combine(Path.GetTempPath(), "AppManagerImportTests_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            Paths.DataDirOverride = root;   // 错误日志等运行时文件只写临时目录
            try
            {
                Test("native effects and modifier precedence", delegate
                {
                    DragDropEffects both = DragDropEffects.Copy | DragDropEffects.Move;
                    Check(FileImport.ResolveEffect(true, Keys.None, both) == DragDropEffects.Move, "same volume");
                    Check(FileImport.ResolveEffect(false, Keys.None, both) == DragDropEffects.Copy, "cross volume");
                    Check(FileImport.ResolveEffect(true, Keys.Control, both) == DragDropEffects.Copy, "ctrl");
                    Check(FileImport.ResolveEffect(false, Keys.Shift, both) == DragDropEffects.Move, "shift");
                    Check(FileImport.ResolveEffect(true, Keys.Control | Keys.Shift, both) == DragDropEffects.Copy, "ctrl+shift safe copy");
                    Check(FileImport.ResolveEffect(true, Keys.Shift, DragDropEffects.Copy) == DragDropEffects.Copy, "copy-only fallback");
                    Check(FileImport.ResolveEffect(true, Keys.Control, DragDropEffects.Move) == DragDropEffects.None, "never promote copy to delete");
                    Check(FileImport.ResolveEffect(true, Keys.None, DragDropEffects.Link) == DragDropEffects.None, "unsupported allowed effects");
                });
                Test("disk-full errors distinguish capacity from permissions", delegate
                {
                    Check(FileImport.IsDiskFull(new IOException("disk full", unchecked((int)0x80070070))), "IO disk full");
                    Check(FileImport.IsDiskFull(new System.ComponentModel.Win32Exception(39)), "Win32 disk full");
                    Check(!FileImport.IsDiskFull(new UnauthorizedAccessException()) && !FileImport.IsDiskFull(new IOException("other")), "unrelated error misclassified");
                });
                Test("RW2 enters photo and credential categories with uppercase extensions", delegate
                {
                    foreach (string path in new[] { "photo.rw2", "photo.RW2", @"C:\album\photo.Rw2" })
                    {
                        Check(MediaRules.IsImage(path) && MediaRules.IsRawImage(path), "RW2 not classified as image");
                        Check(MediaRules.MatchCategoryExt("照片", path) && MediaRules.FitsCategory("照片", path, false), "RW2 rejected by photos");
                        Check(MediaRules.SuggestResourceCategory(path, false) == "照片", "RW2 not suggested as photo");
                        Check(MediaRules.MatchCategoryExt("证件", path), "RW2 not accepted by credentials");
                    }
                    Check(Array.IndexOf(MediaRules.ResourceCategoryExt("照片"), ".rw2") >= 0, "photo filter missing RW2");
                });
                Test("local music scan is recursive, case-insensitive and skips metadata folders", delegate
                {
                    string scanRoot = MusicTestRoot("AppManagerMusicScan_");
                    string junction = Path.Combine(scanRoot, "linked-album");
                    try
                    {
                        string nested = Path.Combine(scanRoot, "album", "disc 1");
                        string recycle = Path.Combine(scanRoot, "$Recycle.Bin");
                        Directory.CreateDirectory(nested);
                        Directory.CreateDirectory(recycle);
                        foreach (string name in new[] { "System Volume Information", "Recovery", "Config.Msi" })
                        {
                            string skipped = Path.Combine(scanRoot, name); Directory.CreateDirectory(skipped);
                            WriteSized(Path.Combine(skipped, "skipped.mp3"), 70000);
                        }
                        WriteSized(Path.Combine(scanRoot, "one.mp3"), 70000);
                        WriteSized(Path.Combine(scanRoot, "album", "two.FLAC"), 70000);
                        WriteSized(Path.Combine(nested, "three.ogg"), 70000);
                        File.WriteAllText(Path.Combine(nested, "notes.txt"), "not audio");
                        WriteSized(Path.Combine(recycle, "deleted.mp3"), 70000);
                        using (System.Diagnostics.Process mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                            "/c mklink /J \"" + junction + "\" \"" + nested + "\"") { CreateNoWindow = true, UseShellExecute = false }))
                        { mk.WaitForExit(); Check(mk.ExitCode == 0, "music scan junction setup"); }
                        string ordinary = Path.Combine(scanRoot, "one.mp3");
                        Check(!FileImport.IsNameSurrogateReparsePoint(ordinary, File.GetAttributes(ordinary)),
                            "ordinary file treated as a link");
                        int progressCalls = 0;
                        LocalMusicScanResult result = LocalMusicScanner.ScanRoots(new[] { scanRoot, scanRoot },
                            CancellationToken.None, delegate(int count, string path) { progressCalls++; });
                        Check(result.Files.Count == 3, "audio count wrong: " + result.Files.Count);
                        Check(result.Files.All(p => File.Exists(p)), "scanner returned a missing file");
                        Check(!result.Files.Any(p => p.Contains("$Recycle.Bin")), "recycle bin was scanned");
                        Check(!result.Files.Any(p => p.Contains("System Volume Information") || p.Contains("Recovery")
                            || p.Contains("Config.Msi") || p.Contains("linked-album")), "metadata folder or junction was scanned");
                        Check(result.DirectoriesSkipped >= 1, "skipped directory not counted");
                        Check(progressCalls > 0, "scan progress was not reported");
                        string appAudio = Path.Combine(root, "must-not-scan.mp3");
                        WriteSized(appAudio, 70000);
                        try
                        {
                            LocalMusicScanResult appResult = LocalMusicScanner.ScanRoots(new[] { root },
                                CancellationToken.None, null);
                            Check(!appResult.Files.Contains(appAudio), "app data directory was scanned");
                        }
                        finally { File.Delete(appAudio); }
                        LocalMusicScanResult invalid = LocalMusicScanner.ScanRoots(new[] { "bad\0root" }, CancellationToken.None, null);
                        Check(invalid.Errors == 1 && invalid.ErrorSamples.Count == 1, "scan failure has no actionable sample");
                        string missing = Path.Combine(Path.GetTempPath(), "AppManagerMissingMusic_" + Guid.NewGuid().ToString("N"));
                        LocalMusicScanResult missingResult = LocalMusicScanner.ScanRoots(new[] { missing }, CancellationToken.None, null);
                        Check(missingResult.Errors == 1 && missingResult.ErrorSamples.Count == 1
                            && missingResult.Files.Count == 0, "missing scan root was treated as empty");
                    }
                    finally
                    {
                        if (Directory.Exists(junction)) Directory.Delete(junction);
                        if (Directory.Exists(scanRoot)) Directory.Delete(scanRoot, true);
                    }
                });
                Test("local music scan honours cancellation", delegate
                {
                    using (CancellationTokenSource cancel = new CancellationTokenSource())
                    {
                        cancel.Cancel();
                        bool cancelled = false;
                        try { LocalMusicScanner.ScanRoots(new[] { root }, cancel.Token, null); }
                        catch (OperationCanceledException) { cancelled = true; }
                        Check(cancelled, "cancelled scan continued");
                    }
                    string cancelRoot = MusicTestRoot("AppManagerMusicCancel_");
                    try
                    {
                        for (int i = 0; i < 70; i++)
                        {
                            string dir = Path.Combine(cancelRoot, "d" + i);
                            Directory.CreateDirectory(dir);
                            WriteSized(Path.Combine(dir, "song.mp3"), 70000);
                        }
                        using (CancellationTokenSource cancel = new CancellationTokenSource())
                        {
                            bool cancelled = false;
                            try
                            {
                                LocalMusicScanner.ScanRoots(new[] { cancelRoot }, cancel.Token,
                                    delegate(int count, string path) { cancel.Cancel(); });
                            }
                            catch (OperationCanceledException) { cancelled = true; }
                            Check(cancelled, "in-progress scan ignored cancellation");
                        }
                    }
                    finally { if (Directory.Exists(cancelRoot)) Directory.Delete(cancelRoot, true); }
                });
                Test("music classifier follows fixed priority without filename keyword matching", delegate
                {
                    string[] music = new[] { Path.Combine(root, "Music") };
                    MusicFilterDecision decision = MusicFileClassifier.Classify(Path.Combine(root, "Music", "normal.mp3"),
                        new MusicProbeInfo { HasAudioStream = true, DurationSeconds = 31, Channels = 1, SampleRate = 44100, Artist = "Artist" }, music);
                    Check(decision.Accepted, "meaningful artist tag was rejected before mono rule");
                    decision = MusicFileClassifier.Classify(Path.Combine(root, "Music", "normal.mp3"),
                        new MusicProbeInfo { HasAudioStream = true, DurationSeconds = 31, Channels = 1, SampleRate = 44100 }, music);
                    Check(decision.Reason == MusicFilterReason.Mono, "untagged mono was not rejected");
                    decision = MusicFileClassifier.Classify(Path.Combine(root, "Music", "normal.mp3"),
                        new MusicProbeInfo { HasAudioStream = true, DurationSeconds = 31, Channels = 2, SampleRate = 44100 }, music);
                    Check(decision.Accepted, "untagged stereo was not accepted");
                    decision = MusicFileClassifier.Classify(Path.Combine(root, "Music", "normal.mp3"),
                        new MusicProbeInfo { HasAudioStream = true, DurationSeconds = 31, Channels = 2, SampleRate = 8000 }, music);
                    Check(decision.Accepted, "file under user music root was not accepted");
                    decision = MusicFileClassifier.Classify(Path.Combine(root, "elsewhere", "normal.mp3"),
                        new MusicProbeInfo { HasAudioStream = true, DurationSeconds = 31, Channels = 2, SampleRate = 8000 }, music);
                    Check(decision.Reason == MusicFilterReason.Unconfirmed, "ambiguous stereo was not unconfirmed");
                    decision = MusicFileClassifier.Classify(Path.Combine(root, "Music", "normal.mp3"),
                        new MusicProbeInfo { HasAudioStream = true, DurationSeconds = 31, Channels = 2, SampleRate = 44100, Genre = "Podcast" }, music);
                    Check(decision.Reason == MusicFilterReason.NonSongGenre, "non-song genre did not win over tags");
                    decision = MusicFileClassifier.Classify(Path.Combine(root, "Music", "normal.mp3"),
                        new MusicProbeInfo { HasAudioStream = true, DurationSeconds = 31, Channels = 2, SampleRate = 44100, Genre = "Podcasts" }, music);
                    Check(decision.Reason == MusicFilterReason.NonSongGenre, "plural podcast genre was accepted");
                    decision = MusicFileClassifier.Classify(Path.Combine(root, "Music", "normal.mp3"),
                        new MusicProbeInfo { HasAudioStream = true, DurationSeconds = 31, Channels = 2, SampleRate = 44100, Genre = "Audiobooks" }, music);
                    Check(decision.Reason == MusicFilterReason.NonSongGenre, "plural audiobook genre was accepted");
                    decision = MusicFileClassifier.Classify(Path.Combine(root, "Music", "Ringtones", "normal.mp3"),
                        new MusicProbeInfo { HasAudioStream = true, DurationSeconds = 31, Channels = 2, SampleRate = 44100, Artist = "Artist" }, music);
                    Check(decision.Reason == MusicFilterReason.NonSongDirectory, "ringtone directory was accepted");
                    decision = MusicFileClassifier.Classify(Path.Combine(root, "Music", "Live Recordings", "normal.mp3"),
                        new MusicProbeInfo { HasAudioStream = true, DurationSeconds = 31, Channels = 2, SampleRate = 44100, Artist = "Artist" }, music);
                    Check(decision.Accepted, "directory segment partial match rejected a real song");
                    decision = MusicFileClassifier.Classify(Path.Combine(root, "Music", "normal.mp3"),
                        new MusicProbeInfo { HasAudioStream = true, DurationSeconds = 29, Channels = 2, SampleRate = 44100, Artist = "Artist" }, music);
                    Check(decision.Reason == MusicFilterReason.TooShort, "short song passed");
                    decision = MusicFileClassifier.Classify(Path.Combine(root, "Music", "normal.mp3"),
                        new MusicProbeInfo { HasAudioStream = true, DurationSeconds = 1201, Channels = 2, SampleRate = 44100 }, music);
                    Check(decision.Reason == MusicFilterReason.LongUntagged, "long untagged recording passed");
                    decision = MusicFileClassifier.Classify(Path.Combine(root, "Music", "normal.mp3"),
                        new MusicProbeInfo { HasAudioStream = false, DurationSeconds = 60 }, music);
                    Check(decision.Reason == MusicFilterReason.NoAudioStream, "missing audio stream passed");
                    Check(MusicFileClassifier.IsCloudPlaceholder(FileAttributes.Offline), "Offline bit not detected");
                    Check(MusicFileClassifier.IsCloudPlaceholder((FileAttributes)0x40000), "RECALL_ON_OPEN bit not detected");
                    Check(MusicFileClassifier.IsCloudPlaceholder((FileAttributes)0x400000), "RECALL_ON_DATA_ACCESS bit not detected");
                    Check(!MusicFileClassifier.IsCloudPlaceholder(FileAttributes.Normal), "normal file treated as cloud placeholder");
                });
                Test("music scan prefilter skips cheap rejects and keeps exact safe directories", delegate
                {
                    string scanRoot = MusicTestRoot("AppManagerMusicFilter_");
                    try
                    {
                        string live = Path.Combine(scanRoot, "Music", "Live Recordings");
                        string music = Path.Combine(scanRoot, "steamapps", "music");
                        string steamCommon = Path.Combine(scanRoot, "steamapps", "common");
                        string programData = Path.Combine(scanRoot, "ProgramData");
                        Directory.CreateDirectory(live); Directory.CreateDirectory(music);
                        Directory.CreateDirectory(steamCommon); Directory.CreateDirectory(programData);
                        string keep1 = Path.Combine(live, "a.mp3");
                        string keep2 = Path.Combine(music, "b.flac");
                        string included = Path.Combine(scanRoot, "included.mp3");
                        string pending = Path.Combine(scanRoot, "pending.mp3");
                        string excluded = Path.Combine(scanRoot, "excluded.mp3");
                        string tiny = Path.Combine(scanRoot, "tiny.mp3");
                        string cloud = Path.Combine(scanRoot, "cloud.mp3");
                        string common = Path.Combine(steamCommon, "game.mp3");
                        string program = Path.Combine(programData, "prompt.mp3");
                        WriteSized(keep1, 70000); WriteSized(keep2, 70000);
                        WriteSized(included, 70000); WriteSized(pending, 70000); WriteSized(excluded, 70000);
                        WriteSized(tiny, 1024); WriteSized(cloud, 70000);
                        WriteSized(common, 70000); WriteSized(program, 70000);
                        File.SetAttributes(cloud, FileAttributes.Offline);
                        MusicScanRequest request = new MusicScanRequest
                        {
                            Roots = new List<string> { scanRoot },
                            IncludedPaths = new List<string> { included, pending },
                            PendingCleanupPaths = new List<string> { pending },
                            ExcludedPaths = new List<string> { excluded },
                            ExtensionOnly = true
                        };
                        LocalMusicScanResult result = LocalMusicScanner.Scan(request, CancellationToken.None, null);
                        Check(result.Files.Contains(keep1) && result.Files.Contains(keep2), "safe music directory was skipped");
                        Check(result.AcceptedBytes == 140000, "accepted bytes: " + result.AcceptedBytes);
                        Check(!result.Files.Contains(included) && result.CountOf(MusicFilterReason.AlreadyIncluded) == 1, "included source was not skipped");
                        Check(!result.Files.Contains(pending) && result.CountOf(MusicFilterReason.AlreadyIncludedPendingCleanup) == 1,
                            "pending cleanup source was not reported separately");
                        Check(!result.Files.Contains(excluded) && result.CountOf(MusicFilterReason.Excluded) == 1, "excluded source was not skipped");
                        Check(!result.Files.Contains(tiny) && result.CountOf(MusicFilterReason.TooSmall) == 1, "small file was not skipped");
                        Check(!result.Files.Contains(cloud) && result.CloudFiles == 1, "cloud placeholder was not skipped");
                        Check(!result.Files.Contains(common), "steamapps/common was scanned");
                        Check(!result.Files.Contains(program), "ProgramData was scanned");
                    }
                    finally { if (Directory.Exists(scanRoot)) Directory.Delete(scanRoot, true); }
                });
                Test("music scan abandons timed-out sessions and marks the rest unconfirmed", delegate
                {
                    string scanRoot = MusicTestRoot("AppManagerMusicTimeout_");
                    try
                    {
                        Directory.CreateDirectory(scanRoot);
                        for (int i = 0; i < 5; i++) WriteSized(Path.Combine(scanRoot, "song" + i + ".mp3"), 70000);
                        int sessions = 0;
                        MusicScanRequest request = new MusicScanRequest
                        {
                            Roots = new List<string> { scanRoot },
                            MaxAbandonedProbes = 3,
                            ProbeTimeoutMilliseconds = 100,
                            ProbeFactory = delegate
                            {
                                sessions++;
                                return new FailingProbeSession(MusicProbeStatus.TimedOut);
                            }
                        };
                        LocalMusicScanResult result = LocalMusicScanner.Scan(request, CancellationToken.None, null);
                        Check(result.Files.Count == 0, "timed-out candidates were accepted");
                        Check(result.UnconfirmedCount == 5, "unconfirmed count: " + result.UnconfirmedCount);
                        Check(result.UnconfirmedBytes == 350000, "unconfirmed bytes: " + result.UnconfirmedBytes);
                        Check(result.CountOf(MusicFilterReason.ProbeTimeout) == 3, "timeout count: " + result.CountOf(MusicFilterReason.ProbeTimeout));
                        Check(result.CountOf(MusicFilterReason.NotProbed) == 2, "not-probed count: " + result.CountOf(MusicFilterReason.NotProbed));
                        Check(sessions == 3, "probe sessions rebuilt: " + sessions);
                    }
                    finally { if (Directory.Exists(scanRoot)) Directory.Delete(scanRoot, true); }
                });
                Test("music scan marks the current candidate when probe session creation fails", delegate
                {
                    string scanRoot = MusicTestRoot("AppManagerMusicUnavailable_");
                    try
                    {
                        Directory.CreateDirectory(scanRoot);
                        WriteSized(Path.Combine(scanRoot, "song0.mp3"), 70000);
                        WriteSized(Path.Combine(scanRoot, "song1.mp3"), 70000);
                        MusicScanRequest request = new MusicScanRequest
                        {
                            Roots = new List<string> { scanRoot },
                            ProbeFactory = delegate { throw new InvalidOperationException("missing probe"); }
                        };
                        LocalMusicScanResult result = LocalMusicScanner.Scan(request, CancellationToken.None, null);
                        Check(result.UnconfirmedCount == 2, "unconfirmed count: " + result.UnconfirmedCount);
                        Check(result.CountOf(MusicFilterReason.ProbeUnavailable) == 1, "current unavailable count");
                        Check(result.CountOf(MusicFilterReason.NotProbed) == 1, "remaining not-probed count");
                    }
                    finally { if (Directory.Exists(scanRoot)) Directory.Delete(scanRoot, true); }
                });
                Test("music scan aborts when an in-flight probe reports cancellation", delegate
                {
                    string scanRoot = MusicTestRoot("AppManagerMusicCancelProbe_");
                    try
                    {
                        Directory.CreateDirectory(scanRoot);
                        WriteSized(Path.Combine(scanRoot, "song.mp3"), 70000);
                        using (CancellationTokenSource cancel = new CancellationTokenSource())
                        {
                            bool cancelled = false;
                            MusicScanRequest request = new MusicScanRequest
                            {
                                Roots = new List<string> { scanRoot },
                                ProbeFactory = delegate { return new FailingProbeSession(MusicProbeStatus.Cancelled, cancel); }
                            };
                            try { LocalMusicScanner.Scan(request, cancel.Token, null); }
                            catch (OperationCanceledException) { cancelled = true; }
                            Check(cancelled, "cancelled probe was not propagated");
                        }
                    }
                    finally { if (Directory.Exists(scanRoot)) Directory.Delete(scanRoot, true); }
                });                Test("resource store selection skips an unavailable location and keeps a folder together", delegate
                {
                    string dir = Path.Combine(root, "multi-store-" + Guid.NewGuid().ToString("N"));
                    string blocked = Path.Combine(dir, "blocked"), usable = Path.Combine(dir, "usable");
                    string source = Path.Combine(dir, "source", "album");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(blocked, "not a directory");
                    Directory.CreateDirectory(usable);
                    Directory.CreateDirectory(source);
                    File.WriteAllText(Path.Combine(source, "inside.txt"), "inside");

                    ImportBatch batch = new ImportBatch { JournalDirectory = Path.Combine(dir, "journal"), Operation = ImportOperation.Copy, Category = "docs" };
                    ImportItem item = new ImportItem { Source = source, Folder = true };
                    batch.Items.Add(item);
                    batch.ResolveTarget = delegate(ImportItem current)
                    {
                        long size; string error, categoryDir;
                        if (!ResourceStore.TryGetSize(current.Source, current.Folder, CancellationToken.None, out size, out error))
                            throw new IOException(error);
                        string targetRoot = ResourceStore.ChooseRoot(new[] { blocked, usable }, current.Source, current.Folder,
                            batch.Category, batch.Operation, size, null, out categoryDir, out error);
                        if (targetRoot == null) throw new IOException(error);
                        return Path.Combine(categoryDir, Path.GetFileName(current.Source.TrimEnd('\\', '/')));
                    };
                    Run(batch);
                    Check(item.State == FileImport.Copied && item.Target.StartsWith(Path.Combine(usable, "docs"), StringComparison.OrdinalIgnoreCase),
                        "folder was not moved to the next usable location");
                    Check(File.ReadAllText(Path.Combine(item.Target, "inside.txt")) == "inside", "folder content was split or lost");
                    Check(File.Exists(Path.Combine(source, "inside.txt")), "copy removed the source folder");
                });
                Test("insufficient space stops separation without copying or deleting sources", delegate
                {
                    foreach (bool committed in new[] { false, true })
                    {
                        ImportBatch batch = Batch(ImportOperation.Move, committed ? new[] { "large.bin" } : new[] { "large.bin", "missing.txt" });
                        ImportItem item = batch.Items[0]; MakeSparseLargerThanFreeSpace(item.Source);
                        if (!committed) { File.Delete(batch.Items[1].Source); Run(batch); }
                        else using (FileStream locked = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.Read)) Run(batch);
                        Check(batch.SpaceLimited && batch.Error == FileImport.SpaceMessage, "missing capacity warning");
                        Check(File.Exists(item.Source) && File.Exists(item.Target) && item.Files[0].Linked && item.Files[0].Hash == null, "source changed or full copy attempted");
                        Check(!File.Exists(item.Target + ".detach-" + batch.Id), "unnecessary separation copy created");
                        Check(item.State == (committed ? FileImport.DeleteFailed : FileImport.Failed), "reported successful import");
                        ImportBatch restored = Reload(batch);
                        Check(restored.SpaceLimited, "capacity warning not persisted");
                        if (committed)
                        {
                            FileImport.Recover(restored, true);
                            Check(File.Exists(item.Source) && !File.Exists(item.Target + ".detach-" + batch.Id), "recovery retried separation");
                        }
                    }
                });
                Test("OLE async negotiation holds source until completion and reports optimized move", delegate
                {
                    AsyncSource source = new AsyncSource();
                    ShellImportDrop transfer = new ShellImportDrop(new DataObject(source));
                    Check(transfer.Asynchronous && source.Starts == 1 && source.Ends == 0, "async lifetime not started");
                    ImportBatch batch = new ImportBatch { Committed = true, Operation = ImportOperation.Move };
                    batch.Items.Add(new ImportItem { State = FileImport.Moved });
                    transfer.Complete(batch); transfer.Complete(null);
                    Check(source.Ends == 1 && source.Result == 0 && source.Effect == 0, "optimized move completed twice or requested source delete");
                    Check(new ShellImportDrop(new DataObject()).Asynchronous == false, "non-async source accepted asynchronously");
                });
                Test("clipboard copy and move independent of volumes", delegate
                {
                    Check(FileImport.ClipboardOperation(null) == ImportOperation.Copy, "missing");
                    Check(FileImport.ClipboardOperation(new byte[] { 2 }) == ImportOperation.Copy, "truncated");
                    Check(FileImport.ClipboardOperation(BitConverter.GetBytes(1)) == ImportOperation.Copy, "copy");
                    Check(FileImport.ClipboardOperation(new MemoryStream(BitConverter.GetBytes(2))) == ImportOperation.Move, "cut");
                    Check(FileImport.ClipboardOperation(BitConverter.GetBytes(3)) == ImportOperation.Copy, "ambiguous");
                });
                Test("same actual volume resolves before target exists", delegate
                {
                    Check(FileImport.SameVolume(root, Path.Combine(root, "not-created", "nested")), "actual volume");
                });
                Test("copy retains all sources and survives journal reload", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Copy, "a.txt", "b.txt"); Run(batch);
                    Check(batch.Items.All(i => File.Exists(i.Source) && File.ReadAllText(i.Source) == File.ReadAllText(i.Target) && i.State == FileImport.Copied), "copy");
                    Check(Reload(batch).Committed, "journal");
                });
                Test("optional copy rate limit preserves data and cancellation", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Copy, "large-photo.bin");
                    File.WriteAllBytes(batch.Items[0].Source, new byte[256 * 1024]);
                    batch.CopyBytesPerSecond = 1024 * 1024;
                    System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
                    Run(batch);
                    elapsed.Stop();
                    Check(elapsed.ElapsedMilliseconds >= 180, "copy was not rate limited: " + elapsed.ElapsedMilliseconds);
                    Check(batch.Items[0].State == FileImport.Copied && File.Exists(batch.Items[0].Source)
                        && new FileInfo(batch.Items[0].Target).Length == 256 * 1024, "rate-limited copy changed data or source");
                    Check(Reload(batch).CopyBytesPerSecond == batch.CopyBytesPerSecond, "rate limit not persisted for recovery");
                    FileImport.CopyRateLimiter limiter = new FileImport.CopyRateLimiter(1);
                    using (CancellationTokenSource cancel = new CancellationTokenSource())
                    {
                        cancel.Cancel();
                        bool canceled = false;
                        try { limiter.Throttle(1, cancel.Token); }
                        catch (OperationCanceledException) { canceled = true; }
                        Check(canceled, "rate limit wait ignored cancellation");
                    }
                });
                Test("batch move commits while every source is still present", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move, "a.txt", "b.txt");
                    FileImport.Run(batch, delegate(ImportBatch value)
                    {
                        Check(value.Items.All(i => File.Exists(i.Source) && File.Exists(i.Target)), "source deleted before commit"); return true;
                    }, null);
                    Check(batch.Items.All(i => !File.Exists(i.Source) && File.Exists(i.Target) && i.State == FileImport.Moved), "move");
                });
                Test("partial move cleanup deletes successful sources without detaching links", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move, "a.txt", "b.txt");
                    batch.DeleteSuccessfulSourcesOnPartial = true;
                    File.Delete(batch.Items[1].Source);
                    Run(batch);
                    Check(batch.Committed && batch.DeleteAuthorized, "partial cleanup not authorized");
                    Check(batch.Items[0].State == FileImport.Moved && !File.Exists(batch.Items[0].Source) && File.Exists(batch.Items[0].Target),
                        "successful item was not moved");
                    Check(batch.Items[0].Files[0].Linked, "successful hard link was detached");
                    Check(batch.Items[1].State == FileImport.Failed, "failed item state changed");
                    Check(!Directory.GetFiles(Path.GetDirectoryName(batch.Items[0].Target), "*.detach-*", SearchOption.AllDirectories).Any(),
                        "partial cleanup created a detached copy");
                    Check(Reload(batch).DeleteSuccessfulSourcesOnPartial, "new option was not persisted");
                });
                Test("partial cleanup recovery keeps successful links and detaches retained failures", delegate
                {
                    string dir = Path.Combine(root, "recover-partial-cleanup");
                    Directory.CreateDirectory(dir);
                    ImportItem copied = LinkedStampedItem(Path.Combine(dir, "copied-source.jpg"), Path.Combine(dir, "copied-target.jpg"));
                    copied.State = FileImport.Copied;
                    ImportItem deleteFailedItem = LinkedStampedItem(Path.Combine(dir, "failed-source.jpg"), Path.Combine(dir, "failed-target.jpg"));
                    deleteFailedItem.State = FileImport.DeleteFailed;
                    ImportBatch batch = JournalBatch("recover-partial-cleanup", ImportOperation.Move, copied, deleteFailedItem);
                    batch.DeleteSuccessfulSourcesOnPartial = true;
                    batch.DeleteAuthorized = true;
                    FileImport.Save(batch);

                    ImportBatch restored = Reload(batch);
                    FileImport.Recover(restored, true);
                    Check(FileImport.Identity(copied.Source) == FileImport.Identity(copied.Target),
                        "successful hard link was detached during recovery");
                    Check(FileImport.Identity(deleteFailedItem.Source) != FileImport.Identity(deleteFailedItem.Target),
                        "retained failure was left sharing a hard link");
                    Check(restored.Items[0].State == FileImport.Copied && File.Exists(copied.Source),
                        "successful source was removed during recovery");

                    FileImport.DeleteSources(restored);
                    Check(!File.Exists(copied.Source) && !File.Exists(deleteFailedItem.Source), "source deletion retry did not finish");
                    Check(File.Exists(copied.Target) && File.Exists(deleteFailedItem.Target) && restored.Items.All(i => i.State == FileImport.Moved),
                        "target or state changed after deletion retry");
                });
                Test("cleanup option defaults false and persists when selected", delegate
                {
                    ImportBatch defaults = Batch(ImportOperation.Copy, "a.txt");
                    Check(!defaults.DeleteSuccessfulSourcesOnPartial, "cleanup option defaulted true");
                    FileImport.Save(defaults);
                    Check(!Reload(defaults).DeleteSuccessfulSourcesOnPartial, "cleanup option default was not preserved");
                    ImportBatch selected = Batch(ImportOperation.Move, "b.txt");
                    selected.DeleteSuccessfulSourcesOnPartial = true;
                    FileImport.Save(selected);
                    Check(Reload(selected).DeleteSuccessfulSourcesOnPartial, "cleanup option selection was not persisted");
                });
                Test("scan result defaults to cancel before the user chooses", delegate
                {
                    LocalScanResultDecision decision = new LocalScanResultDecision();
                    Check(decision.Action == LocalScanResultAction.Cancel, "scan result defaulted to import");
                    Check(decision.Operation == ImportOperation.Copy, "scan result defaulted to a destructive operation");
                });
                Test("copy never deletes sources even with partial cleanup flag", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Copy, "a.txt");
                    batch.DeleteSuccessfulSourcesOnPartial = true;
                    Run(batch);
                    Check(File.Exists(batch.Items[0].Source) && File.Exists(batch.Items[0].Target), "copy lost a source or target");
                    Check(batch.Items[0].State == FileImport.Copied && !batch.DeleteAuthorized, "copy authorized source deletion");
                });
                Test("cancelled partial cleanup keeps every source", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move, "a.txt", "b.txt");
                    batch.DeleteSuccessfulSourcesOnPartial = true;
                    using (CancellationTokenSource cancel = new CancellationTokenSource())
                        FileImport.Run(batch, delegate { return true; }, delegate(int index, string name) { if (index == 1) cancel.Cancel(); }, cancel.Token);
                    Check(batch.Items.All(i => File.Exists(i.Source)) && !batch.DeleteAuthorized, "cancelled batch deleted a source");
                });
                Test("one missing source prevents deletion of whole batch", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move, "a.txt", "b.txt"); File.Delete(batch.Items[1].Source); Run(batch);
                    Check(File.Exists(batch.Items[0].Source) && File.Exists(batch.Items[0].Target) && !batch.DeleteAuthorized, "whole batch preservation");
                });
                Test("one locked source preserves every source", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move, "a.txt", "b.txt");
                    using (FileStream locked = new FileStream(batch.Items[1].Source, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Run(batch);
                    Check(batch.Items.All(i => File.Exists(i.Source)) && !batch.DeleteAuthorized, "locked source preservation");
                });
                Test("target conflict never overwritten and keeps entire batch", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move, "a.txt", "b.txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(batch.Items[1].Target)); File.WriteAllText(batch.Items[1].Target, "original"); Run(batch);
                    Check(batch.Items.All(i => File.Exists(i.Source)), "preserve sources");
                    Check(File.ReadAllText(batch.Items[1].Target) == "original", "no overwrite");
                });
                Test("database rejection preserves sources and disables retries", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move, "a.txt", "b.txt");
                    FileImport.Run(batch, delegate { return false; }, null); FileImport.DeleteSources(Reload(batch));
                    Check(batch.Items.All(i => File.Exists(i.Source) && !File.Exists(i.Target) && i.State == FileImport.RolledBack), "database failure");
                    Check(!File.Exists(Path.Combine(batch.JournalDirectory, batch.Id + ".pending")) && Reload(batch).Finished, "journal not finished");
                });
                Test("journal reuse validates source and status before skipping a file", delegate
                {
                    string copyDir = Path.Combine(root, "reuse-copy"); Directory.CreateDirectory(copyDir);
                    string copySource = Path.Combine(copyDir, "source.jpg"), copyTarget = Path.Combine(copyDir, "target.jpg");
                    ImportItem copyItem = StampedItem(copySource, copyTarget, false);
                    copyItem.State = FileImport.Copied;
                    ImportBatch copyBatch = JournalBatch("reuse-copy", ImportOperation.Copy, copyItem);
                    Dictionary<string, LocalMediaImportTarget> copied = LocalMediaScanner.CompletedImportTargets(
                        copyBatch.JournalDirectory, RegisteredExisting, CancellationToken.None);
                    Check(copied.ContainsKey(copySource) && copied[copySource].Target == copyTarget && !copied[copySource].PendingCleanup,
                        "unchanged copied source was not reused");

                    string failedDir = Path.Combine(root, "reuse-failed"); Directory.CreateDirectory(failedDir);
                    string failedSource = Path.Combine(failedDir, "source.jpg"), failedTarget = Path.Combine(failedDir, "target.jpg");
                    ImportItem failedItem = StampedItem(failedSource, failedTarget, false);
                    failedItem.State = FileImport.DeleteFailed;
                    ImportBatch failedBatch = JournalBatch("reuse-failed", ImportOperation.Move, failedItem);
                    Dictionary<string, LocalMediaImportTarget> failedMap = LocalMediaScanner.CompletedImportTargets(
                        failedBatch.JournalDirectory, RegisteredExisting, CancellationToken.None);
                    Check(failedMap.ContainsKey(failedSource) && failedMap[failedSource].PendingCleanup, "delete-failed residue was not reported");
                    LocalMediaScanResult pendingResult = LocalMediaScanner.Scan("照片", new[] { failedDir }, null, failedMap, null,
                        CancellationToken.None);
                    Check(pendingResult.Reused == 1 && pendingResult.CleanupPending == 1,
                        "media scan falsely reported a pending source as cleaned");

                    DateTime modified = File.GetLastWriteTimeUtc(failedSource);
                    File.WriteAllText(failedSource, "changed-content!");
                    File.SetLastWriteTimeUtc(failedSource, modified);
                    failedMap = LocalMediaScanner.CompletedImportTargets(failedBatch.JournalDirectory, RegisteredExisting, CancellationToken.None);
                    Check(!failedMap.ContainsKey(failedSource), "modified delete-failed source was reused");

                    string movedDir = Path.Combine(root, "reuse-moved"); Directory.CreateDirectory(movedDir);
                    string movedSource = Path.Combine(movedDir, "source.jpg"), movedTarget = Path.Combine(movedDir, "target.jpg");
                    ImportItem movedItem = StampedItem(movedSource, movedTarget, false);
                    movedItem.State = FileImport.Moved;
                    ImportBatch movedBatch = JournalBatch("reuse-moved", ImportOperation.Move, movedItem);
                    Dictionary<string, LocalMediaImportTarget> moved = LocalMediaScanner.CompletedImportTargets(
                        movedBatch.JournalDirectory, RegisteredExisting, CancellationToken.None);
                    Check(!moved.ContainsKey(movedSource), "new file at a moved source path was reused");

                    string legacyDir = Path.Combine(root, "reuse-legacy"); Directory.CreateDirectory(legacyDir);
                    string legacySource = Path.Combine(legacyDir, "source.jpg"), legacyTarget = Path.Combine(legacyDir, "target.jpg");
                    File.WriteAllText(legacySource, "legacy"); File.WriteAllText(legacyTarget, "legacy");
                    ImportBatch legacyBatch = JournalBatch("reuse-legacy", ImportOperation.Copy,
                        new ImportItem { Source = legacySource, Target = legacyTarget, State = FileImport.Copied });
                    Dictionary<string, LocalMediaImportTarget> legacy = LocalMediaScanner.CompletedImportTargets(
                        legacyBatch.JournalDirectory, RegisteredExisting, CancellationToken.None);
                    Check(!legacy.ContainsKey(legacySource), "record without verification stamps was reused");

                    string combinedDir = Path.Combine(root, "journal-combined"); Directory.CreateDirectory(combinedDir);
                    string combinedSource = Path.Combine(combinedDir, "source.jpg"), combinedTarget = Path.Combine(combinedDir, "target.jpg");
                    ImportItem oldCopyItem = StampedItem(combinedSource, combinedTarget, false);
                    oldCopyItem.State = FileImport.Copied;
                    ImportBatch oldCopyBatch = new ImportBatch { Id = "combined-old", JournalDirectory = combinedDir,
                        Committed = true, Resource = true, Operation = ImportOperation.Copy, Category = "照片" };
                    oldCopyBatch.Items.Add(oldCopyItem); FileImport.Save(oldCopyBatch);
                    File.SetLastWriteTimeUtc(Path.Combine(combinedDir, "combined-old.xml"), DateTime.UtcNow.AddMinutes(-1));
                    ImportBatch laterMovedBatch = new ImportBatch { Id = "combined-new", JournalDirectory = combinedDir,
                        Committed = true, Resource = true, Operation = ImportOperation.Move, Category = "照片" };
                    laterMovedBatch.Items.Add(new ImportItem { Source = combinedSource, Target = combinedTarget, State = FileImport.Moved });
                    FileImport.Save(laterMovedBatch);
                    Dictionary<string, LocalMediaImportTarget> combined = LocalMediaScanner.CompletedImportTargets(
                        combinedDir, RegisteredExisting, CancellationToken.None);
                    Check(!combined.ContainsKey(combinedSource), "older copy record survived a later completed move");
                });
                Test("completed hard-link move reuse rejects a replacement source", delegate
                {
                    string dir = Path.Combine(root, "reuse-linked-identity"); Directory.CreateDirectory(dir);
                    string source = Path.Combine(dir, "source.jpg"), target = Path.Combine(dir, "target.jpg");
                    ImportItem item = LinkedStampedItem(source, target);
                    item.State = FileImport.DeleteFailed;
                    ImportBatch batch = JournalBatch("reuse-linked-identity", ImportOperation.Move, item);
                    Dictionary<string, LocalMediaImportTarget> imported = LocalMediaScanner.CompletedImportTargets(
                        batch.JournalDirectory, RegisteredExisting, CancellationToken.None);
                    Check(imported.ContainsKey(source) && imported[source].PendingCleanup,
                        "unchanged hard-link residue was not reused");

                    DateTime modified = File.GetLastWriteTimeUtc(source);
                    string content = File.ReadAllText(source);
                    File.Delete(source);
                    File.WriteAllText(source, content);
                    File.SetLastWriteTimeUtc(source, modified);
                    imported = LocalMediaScanner.CompletedImportTargets(batch.JournalDirectory, RegisteredExisting, CancellationToken.None);
                    Check(!imported.ContainsKey(source), "replacement source at a hard-link move path was reused");
                });
                Test("completed copy reuse follows a renamed target before validation", delegate
                {
                    string dir = Path.Combine(root, "reuse-renamed-target"); Directory.CreateDirectory(dir);
                    string source = Path.Combine(dir, "source.jpg"), target = Path.Combine(dir, "target.jpg");
                    string renamed = Path.Combine(dir, "renamed.jpg");
                    ImportItem item = StampedItem(source, target, false);
                    item.State = FileImport.Copied;
                    ImportBatch batch = JournalBatch("reuse-renamed-target", ImportOperation.Copy, item);
                    File.Move(target, renamed);
                    Func<List<string>, Dictionary<string, string>> registered = delegate(List<string> paths)
                    {
                        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (string path in paths)
                            if (string.Equals(path, source, StringComparison.OrdinalIgnoreCase)) result[path] = renamed;
                        return result;
                    };
                    Dictionary<string, LocalMediaImportTarget> imported = LocalMediaScanner.CompletedImportTargets(
                        batch.JournalDirectory, registered, CancellationToken.None);
                    Check(imported.ContainsKey(source) && imported[source].Target == renamed && !imported[source].PendingCleanup,
                        "renamed copy target was not reused");
                });
                Test("file system access failures are ignored instead of aborting reuse", delegate
                {
                    string dir = Path.Combine(root, "reuse-locked-source"); Directory.CreateDirectory(dir);
                    string source = Path.Combine(dir, "source.jpg"), target = Path.Combine(dir, "target.jpg");
                    ImportItem item = StampedItem(source, target, false);
                    item.State = FileImport.DeleteFailed;
                    ImportBatch batch = JournalBatch("reuse-locked-source", ImportOperation.Move, item);
                    using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        Dictionary<string, LocalMediaImportTarget> imported = LocalMediaScanner.CompletedImportTargets(
                            batch.JournalDirectory, RegisteredExisting, CancellationToken.None);
                        Check(imported.Count == 0, "locked source was reused or aborted the scan");
                    }

                    System.Reflection.MethodInfo classify = typeof(LocalMediaScanner).GetMethod("IsFileSystemError",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    Check(classify != null, "file system error classifier is missing");
                    Check((bool)classify.Invoke(null, new object[] { new System.ComponentModel.Win32Exception(5) }),
                        "Win32 target access failure must be treated as unreusable");
                    Check((bool)classify.Invoke(null, new object[] { new IOException("sharing violation") }),
                        "I/O target access failure must be treated as unreusable");
                    Check((bool)classify.Invoke(null, new object[] { new UnauthorizedAccessException("denied") }),
                        "access denied must be treated as unreusable");
                    Check(!(bool)classify.Invoke(null, new object[] { new InvalidOperationException("bug") }),
                        "unexpected failures must still abort the scan");
                });
                Test("move residue without a source hash is not reused", delegate
                {
                    string dir = Path.Combine(root, "reuse-missing-hash"); Directory.CreateDirectory(dir);
                    string source = Path.Combine(dir, "source.jpg"), target = Path.Combine(dir, "target.jpg");
                    ImportItem item = StampedItem(source, target, false);
                    item.State = FileImport.DeleteFailed;
                    item.Files[0].Hash = null;
                    ImportBatch batch = JournalBatch("reuse-missing-hash", ImportOperation.Move, item);
                    Dictionary<string, LocalMediaImportTarget> imported = LocalMediaScanner.CompletedImportTargets(
                        batch.JournalDirectory, RegisteredExisting, CancellationToken.None);
                    Check(!imported.ContainsKey(source), "move residue without a hash was reused");
                });
                Test("every 500-item media import plan keeps the selected operation and cleanup flag", delegate
                {
                    List<string> paths = Enumerable.Range(0, 1001).Select(i => "item-" + i).ToList();
                    List<LocalMediaImportPlan> movePlans = LocalMediaScanner.PlanImports(paths, ImportOperation.Move, 500);
                    List<LocalMediaImportPlan> copyPlans = LocalMediaScanner.PlanImports(paths, ImportOperation.Copy, 500);
                    Check(movePlans.Count == 3 && movePlans[0].Paths.Length == 500 && movePlans[1].Paths.Length == 500
                        && movePlans[2].Paths.Length == 1, "media batch boundary changed");
                    Check(movePlans.All(p => p.Operation == ImportOperation.Move && p.DeleteSuccessfulSourcesOnPartial),
                        "move cleanup flag was not carried by every batch");
                    Check(copyPlans.All(p => p.Operation == ImportOperation.Copy && !p.DeleteSuccessfulSourcesOnPartial),
                        "copy plan requested source deletion");
                });
                Test("source changed after copy is never deleted", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move, "a.txt");
                    FileImport.Run(batch, delegate { File.WriteAllText(batch.Items[0].Source, "new content"); return true; }, null);
                    Check(batch.Items[0].State == FileImport.DeleteFailed && File.ReadAllText(batch.Items[0].Source) == "new content", "changed source");
                });
                Test("target changed after copy prevents deletion", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move, "a.txt");
                    FileImport.Run(batch, delegate { File.WriteAllText(batch.Items[0].Target, "damaged"); return true; }, null);
                    Check(File.Exists(batch.Items[0].Source) && batch.Items[0].State == FileImport.DeleteFailed, "changed target");
                });
                Test("delete denied is persisted and retry succeeds after unlock", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move, "a.txt");
                    using (FileStream locked = new FileStream(batch.Items[0].Source, FileMode.Open, FileAccess.Read, FileShare.Read)) Run(batch);
                    Check(batch.Items[0].State == FileImport.DeleteFailed && File.Exists(batch.Items[0].Source), "denial state");
                    ImportBatch restored = Reload(batch); FileImport.DeleteSources(restored);
                    Check(restored.Items[0].State == FileImport.Moved && !File.Exists(restored.Items[0].Source), "retry");
                });
                Test("retry refuses source edited since initial deletion failure", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move, "a.txt");
                    using (FileStream locked = new FileStream(batch.Items[0].Source, FileMode.Open, FileAccess.Read, FileShare.Read)) Run(batch);
                    File.WriteAllText(batch.Items[0].Source, "user edit"); FileImport.DeleteSources(Reload(batch));
                    Check(File.ReadAllText(batch.Items[0].Source) == "user edit", "retry deleted edits");
                });
                Test("journal failure prevents copy and source deletion", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move, "a.txt"); File.WriteAllText(batch.JournalDirectory, "blocked");
                    bool rejected = false; try { Run(batch); } catch (IOException) { rejected = true; }
                    Check(rejected && File.Exists(batch.Items[0].Source) && !File.Exists(batch.Items[0].Target), "unrecorded IO");
                });
                Test("retry resumes partially deleted directory without deleting new files", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move); string dir = Path.GetDirectoryName(batch.JournalDirectory), source = Path.Combine(dir, "source", "folder");
                    Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "a.txt"), "a"); File.WriteAllText(Path.Combine(source, "b.txt"), "b");
                    batch.Items.Add(new ImportItem { Source = source, Target = Path.Combine(dir, "target", "folder"), Folder = true });
                    using (FileStream locked = new FileStream(Path.Combine(source, "b.txt"), FileMode.Open, FileAccess.Read, FileShare.Read)) Run(batch);
                    ImportBatch restored = Reload(batch); FileImport.DeleteSources(restored);
                    Check(restored.Items[0].State == FileImport.Moved && !Directory.Exists(source), "partial retry");
                });
                Test("directory move preserves nested structure and empty folders", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move); string dir = Path.GetDirectoryName(batch.JournalDirectory);
                    string source = Path.Combine(dir, "source", "folder"); Directory.CreateDirectory(Path.Combine(source, "empty"));
                    Directory.CreateDirectory(Path.Combine(source, "nested")); File.WriteAllText(Path.Combine(source, "nested", "x.txt"), "nested");
                    batch.Items.Add(new ImportItem { Source = source, Target = Path.Combine(dir, "target", "folder"), Folder = true }); Run(batch);
                    Check(!Directory.Exists(source) && Directory.Exists(Path.Combine(batch.Items[0].Target, "empty"))
                        && File.ReadAllText(Path.Combine(batch.Items[0].Target, "nested", "x.txt")) == "nested", "directory move");
                });
                Test("new source child after copy prevents recursive deletion", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move); string dir = Path.GetDirectoryName(batch.JournalDirectory), source = Path.Combine(dir, "source", "folder");
                    Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "old.txt"), "old");
                    batch.Items.Add(new ImportItem { Source = source, Target = Path.Combine(dir, "target", "folder"), Folder = true });
                    FileImport.Run(batch, delegate { File.WriteAllText(Path.Combine(source, "new.txt"), "new"); return true; }, null);
                    Check(File.Exists(Path.Combine(source, "old.txt")) && File.Exists(Path.Combine(source, "new.txt")), "new child protection");
                });
                Test("folder cannot be copied into itself", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move); string source = Path.Combine(Path.GetDirectoryName(batch.JournalDirectory), "source");
                    batch.Items.Add(new ImportItem { Source = source, Target = Path.Combine(source, "nested"), Folder = true }); Run(batch);
                    Check(batch.Items[0].State == FileImport.Failed && Directory.Exists(source), "recursive target");
                });
                Test("entry-only import performs no file IO or deletion", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move, "app.exe", "existing.txt"); foreach (ImportItem i in batch.Items) i.Reference = true;
                    Run(batch); Check(batch.Items.All(i => i.Source == i.Target && File.Exists(i.Source) && i.State == FileImport.Linked), "references");
                });
                Test("preexisting temporary path is never removed", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Copy, "a.txt"); Directory.CreateDirectory(Path.GetDirectoryName(batch.Items[0].Target));
                    string temporary = batch.Items[0].Target + ".import-" + batch.Id; File.WriteAllText(temporary, "keep"); Run(batch);
                    Check(File.ReadAllText(temporary) == "keep" && batch.Items[0].State == FileImport.Failed, "foreign temp removed");
                });
                Test("malformed paths and DTD journals rejected", delegate
                {
                    Check(!FileImport.Within(Path.Combine(root, "a"), Path.Combine(root, "ab")), "prefix");
                    Check(FileImport.Within(root, Path.Combine(root, "a", "..", "b")), "canonical");
                    string path = Path.Combine(root, "bad.xml"); File.WriteAllText(path, "<!DOCTYPE ImportBatch [<!ENTITY x 'bad'>]><ImportBatch />");
                    bool rejected = false; try { FileImport.Load(path); } catch { rejected = true; } Check(rejected, "DTD");
                });
                Test("journal round-trips XML-invalid characters", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Copy, "a.txt");
                    string invalid = "bad\u0001\u000B\uD800end";
                    batch.Error = invalid; batch.Items[0].Error = invalid; batch.Items[0].Source = invalid;
                    FileImport.Save(batch);
                    ImportBatch restored = Reload(batch);
                    Check(restored.Error == invalid && restored.Items[0].Error == invalid
                        && restored.Items[0].Source == invalid, "invalid XML characters were not preserved");
                });
                Test("journal save retries a transient replace lock", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Copy, "retry.txt");
                    FileImport.Save(batch);
                    string journal = Path.Combine(batch.JournalDirectory, batch.Id + ".xml");
                    FileStream locked = new FileStream(journal, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    Thread release = new Thread(delegate() { Thread.Sleep(150); locked.Dispose(); });
                    release.Start();
                    try { FileImport.Save(batch); }
                    finally { locked.Dispose(); }
                    Check(release.Join(2000) && Reload(batch).Items.Count == 1, "transient replace lock was not retried");
                });
                Test("same-volume move links instead of copying and keeps metadata", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move, "a.txt"); File.SetAttributes(batch.Items[0].Source, FileAttributes.ReadOnly | FileAttributes.Hidden);
                    FileImport.Run(batch, delegate(ImportBatch value)
                    {
                        Check(value.Items[0].Files.All(f => f.Linked && f.Hash == null), "not hard linked"); return true;
                    }, null);
                    string target = batch.Items[0].Target;
                    Check(batch.Items[0].State == FileImport.Moved && !File.Exists(batch.Items[0].Source), "read-only source not deleted: " + batch.Items[0].Error);
                    Check((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0 && File.ReadAllText(target) == "content-a.txt", "target metadata/content");
                    File.SetAttributes(target, FileAttributes.Normal);
                });
                Test("copy hashes in one pass and keeps alternate streams", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Copy, "zone.txt");
                    string source = batch.Items[0].Source; WriteStream(source + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3");
                    Run(batch);
                    Check(batch.Items[0].Files[0].Hash != null && !batch.Items[0].Files[0].Linked, "copy stamp");
                    Check(ReadStream(batch.Items[0].Target + ":Zone.Identifier").Contains("ZoneId=3"), "mark of the web lost");
                });
                Test("partial failure keeps sources until explicitly confirmed", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move, "a.txt", "b.txt"); File.Delete(batch.Items[1].Source); Run(batch);
                    ImportBatch restored = Reload(batch); FileImport.DeleteSources(restored);
                    Check(File.Exists(restored.Items[0].Source), "deleted without confirmation");
                    restored.DeleteAuthorized = true; FileImport.DeleteSources(restored);
                    Check(restored.Items[0].State == FileImport.Moved && !File.Exists(restored.Items[0].Source) && File.Exists(restored.Items[0].Target), "confirmed delete");
                });
                Test("cancellation keeps every source and withdraws same-volume links instead of sharing data", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move, "a.txt", "b.txt");
                    using (CancellationTokenSource cancel = new CancellationTokenSource())
                        FileImport.Run(batch, delegate { return true; }, delegate(int index, string name) { if (index == 1) cancel.Cancel(); }, cancel.Token);
                    // 同卷移动在取消时撤回已建立的硬链接：不提交、不复制，也不留下与源文件共用数据的入库副本
                    Check(batch.Items[0].State == FileImport.RolledBack && batch.Items[1].State == FileImport.Failed && !batch.DeleteAuthorized, "cancel states: " + batch.Items[0].State);
                    Check(batch.Items.All(i => File.Exists(i.Source)) && File.ReadAllText(batch.Items[0].Source) == "content-a.txt", "cancel lost or changed a source");
                    Check(!File.Exists(batch.Items[0].Target), "cancelled link is still shared with the retained source");
                    string targetDir = Path.GetDirectoryName(batch.Items[0].Target);
                    Check(!Directory.Exists(targetDir) || !Directory.GetFiles(targetDir, "*.detach-*", SearchOption.AllDirectories).Any(),
                        "cancelled detach left a temporary copy");
                });
                Test("recovery removes identified duplicates but preserves incomplete staging", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Copy, "a.txt", "b.txt");
                    string targetDir = Path.GetDirectoryName(batch.Items[0].Target); Directory.CreateDirectory(targetDir);
                    File.Copy(batch.Items[0].Source, batch.Items[0].Target);   // interrupted after placement, before commit
                    batch.Items[0].TargetId = FileImport.Identity(batch.Items[0].Target);
                    File.WriteAllText(batch.Items[1].Target, "different");     // differs from source: must be kept
                    string staging = batch.Items[0].Target + ".import-" + batch.Id; File.WriteAllText(staging, "partial");
                    FileImport.Save(batch); File.WriteAllText(Path.Combine(batch.JournalDirectory, batch.Id + ".pending"), "");
                    Check(FileImport.PendingJournals(batch.JournalDirectory).Length == 1, "pending marker");
                    ImportBatch pending = Reload(batch); FileImport.Recover(pending, false);
                    Check(File.Exists(staging) && !File.Exists(batch.Items[0].Target) && File.ReadAllText(batch.Items[1].Target) == "different", "recovery cleanup");
                    Check(batch.Items.All(i => File.Exists(i.Source)) && FileImport.PendingJournals(batch.JournalDirectory).Length == 0 && Reload(batch).Finished, "recovery state");
                });
                Test("junction in source path is rejected", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move); string dir = Path.GetDirectoryName(batch.JournalDirectory);
                    string real = Path.Combine(dir, "real"), junction = Path.Combine(dir, "junction"); Directory.CreateDirectory(real);
                    File.WriteAllText(Path.Combine(real, "x.txt"), "x");
                    using (System.Diagnostics.Process mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                        "/c mklink /J \"" + junction + "\" \"" + real + "\"") { CreateNoWindow = true, UseShellExecute = false })) mk.WaitForExit();
                    Check(Directory.Exists(junction), "junction setup");
                    batch.Items.Add(new ImportItem { Source = Path.Combine(junction, "x.txt"), Target = Path.Combine(dir, "target", "x.txt") }); Run(batch);
                    Check(batch.Items[0].State == FileImport.Failed && File.Exists(Path.Combine(real, "x.txt")), "junction accepted");
                    Directory.Delete(junction);
                });
                Test("journal pruning keeps newest and pending records", delegate
                {
                    string dir = Path.Combine(root, "prune"); Directory.CreateDirectory(dir);
                    for (int i = 0; i < 5; i++)
                    {
                        string path = Path.Combine(dir, i + ".xml"); File.WriteAllText(path, "x"); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(i));
                        File.WriteAllText(Path.ChangeExtension(path, ".complete"), "");
                    }
                    File.WriteAllText(Path.Combine(dir, "0.pending"), "");
                    FileImport.Prune(dir, 2);
                    Check(File.Exists(Path.Combine(dir, "4.xml")) && File.Exists(Path.Combine(dir, "3.xml")) && File.Exists(Path.Combine(dir, "0.xml"))
                        && !File.Exists(Path.Combine(dir, "1.xml")) && !File.Exists(Path.Combine(dir, "2.xml")), "prune");
                });
                Test("recovery never removes unowned same-content files with unique streams", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Copy, "foreign.txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(batch.Items[0].Target));
                    File.Copy(batch.Items[0].Source, batch.Items[0].Target);
                    WriteStream(batch.Items[0].Target + ":private", "unique");
                    FileImport.Save(batch); FileImport.Recover(Reload(batch), false);
                    Check(File.Exists(batch.Items[0].Target) && ReadStream(batch.Items[0].Target + ":private") == "unique", "foreign file removed");
                });
                Test("missing empty destination directory prevents source deletion", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move); string dir = Path.GetDirectoryName(batch.JournalDirectory);
                    string source = Path.Combine(dir, "source", "folder"); Directory.CreateDirectory(Path.Combine(source, "empty"));
                    batch.Items.Add(new ImportItem { Source = source, Target = Path.Combine(dir, "target", "folder"), Folder = true });
                    FileImport.Run(batch, delegate { Directory.Delete(Path.Combine(batch.Items[0].Target, "empty")); return true; }, null);
                    Check(Directory.Exists(Path.Combine(source, "empty")) && batch.Items[0].State == FileImport.DeleteFailed, "empty directory lost");
                });
                Test("retained source is independent after partial failure and delete failure", delegate
                {
                    ImportBatch partial = Batch(ImportOperation.Move, "a.txt", "missing.txt"); File.Delete(partial.Items[1].Source); Run(partial);
                    File.WriteAllText(partial.Items[0].Target, "library edit");
                    Check(File.ReadAllText(partial.Items[0].Source) == "content-a.txt", "partial alias");
                    ImportBatch denied = Batch(ImportOperation.Move, "b.txt");
                    using (FileStream locked = new FileStream(denied.Items[0].Source, FileMode.Open, FileAccess.Read, FileShare.Read)) Run(denied);
                    File.WriteAllText(denied.Items[0].Target, "another edit");
                    Check(File.ReadAllText(denied.Items[0].Source) == "content-b.txt", "delete failure alias");
                });
                Test("pruning preserves unresolved and legacy journals", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move, "a.txt", "missing.txt"); File.Delete(batch.Items[1].Source); Run(batch);
                    string path = Path.Combine(batch.JournalDirectory, batch.Id + ".xml");
                    string legacy = Path.Combine(batch.JournalDirectory, "legacy.xml"); File.WriteAllText(legacy, "legacy");
                    FileImport.Prune(batch.JournalDirectory, 0);
                    Check(File.Exists(path) && File.Exists(legacy), "unresolved journal pruned");
                });
                Test("actual cross-volume copy and move", delegate
                {
                    string other = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed
                        && !FileImport.SameVolume(root, d.RootDirectory.FullName)).Select(d => d.RootDirectory.FullName).FirstOrDefault();
                    if (other == null) throw new Exception("No second local volume available for verification");
                    string cross = Path.Combine(other, "AppManagerImportTests_" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        Directory.CreateDirectory(cross);
                        foreach (ImportOperation operation in new[] { ImportOperation.Copy, ImportOperation.Move })
                        {
                            ImportBatch batch = Batch(operation, operation + ".txt"); batch.Items[0].Target = Path.Combine(cross, operation + ".txt");
                            WriteStream(batch.Items[0].Source + ":Zone.Identifier", "ZoneId=3");
                            using (FileStream large = new FileStream(CreateFile(batch.Items[0].Source + ":large", 0x40000000, 0, IntPtr.Zero, 2, 0, IntPtr.Zero), FileAccess.Write))
                            { large.SetLength((64L << 20) + 1); large.WriteByte(42); }
                            Run(batch);
                            Check(File.Exists(batch.Items[0].Target) && File.Exists(batch.Items[0].Source) == (operation == ImportOperation.Copy), "cross-volume outcome");
                            Check(ReadStream(batch.Items[0].Target + ":Zone.Identifier") == "ZoneId=3", "cross-volume ADS lost");
                            using (FileStream large = new FileStream(CreateFile(batch.Items[0].Target + ":large", 0x80000000, 1, IntPtr.Zero, 3, 0, IntPtr.Zero), FileAccess.Read))
                                Check(large.Length == (64L << 20) + 1 && large.ReadByte() == 42, "large ADS lost");
                        }
                    }
                    finally { if (Directory.Exists(cross)) Directory.Delete(cross, true); }
                });
                Test("managed DataObject writes performed drop effect", delegate
                {
                    AsyncSource source = new AsyncSource();
                    DataObject dropped = new DataObject(source);
                    ShellImportDrop transfer = new ShellImportDrop(dropped);
                    ImportBatch batch = new ImportBatch {
                        Committed = true, Operation = ImportOperation.Move };
                    batch.Items.Add(new ImportItem { State = FileImport.Moved });
                    transfer.Complete(batch);
                    object performed = dropped.GetData("Performed DropEffect");
                    MemoryStream stream = performed as MemoryStream;
                    Check(stream != null, "drop effect missing");
                    Check(BitConverter.ToUInt32(stream.ToArray(), 0) == 0,
                        "wrong effect");
                });
                Test("duplicate inspector skips later same-batch content and keeps sources", delegate
                {
                    string dir = Path.Combine(root, "dup-batch-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);
                    string first = Path.Combine(dir, "first.bin"), second = Path.Combine(dir, "second.bin");
                    File.WriteAllBytes(first, new byte[] { 1, 2, 3, 4, 5 });
                    File.Copy(first, second);
                    ImportBatch batch = new ImportBatch { Category = "照片", Resource = true, Operation = ImportOperation.Move };
                    batch.Items.Add(new ImportItem { Source = first, Origin = first, Target = Path.Combine(dir, "target", "first.bin") });
                    batch.Items.Add(new ImportItem { Source = second, Origin = second, Target = Path.Combine(dir, "target", "second.bin") });
                    Check(ImportDuplicateInspector.InspectAndApply(batch, new[] { first, second }, "照片", true,
                        new ImportDuplicateContext(), CancellationToken.None), "inspection cancelled");
                    Check(batch.DuplicateReport.BatchContentDuplicate == 1, "same-batch duplicate not counted");
                    Check(batch.Items[0].State != FileImport.Skipped && batch.Items[1].State == FileImport.Skipped, "later duplicate not skipped");
                    Check(!string.IsNullOrEmpty(batch.Items[0].ExpectedHash), "first item hash missing");
                    Check(File.Exists(first) && File.Exists(second), "duplicate inspection deleted a source");
                });
                Test("duplicate paths and nested selections are reported only as path overlap", delegate
                {
                    string dir = Path.Combine(root, "dup-selection-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);
                    string folder = Path.Combine(dir, "album");
                    Directory.CreateDirectory(folder);
                    string image = Path.Combine(folder, "one.bin");
                    File.WriteAllBytes(image, new byte[] { 91, 92, 93 });
                    ImportDuplicateReport duplicate = ImportDuplicateInspector.InspectPaths(new[] { image, image }, "照片", true,
                        new ImportDuplicateContext(), CancellationToken.None);
                    Check(duplicate.BatchPathOverlap == 1 && duplicate.BatchContentDuplicate == 0,
                        "duplicate selection was counted as a content duplicate");
                    ImportDuplicateReport nested = ImportDuplicateInspector.InspectPaths(new[] { folder, image }, "照片", true,
                        new ImportDuplicateContext(), CancellationToken.None);
                    Check(nested.BatchPathOverlap == 1 && nested.SameCategoryContent == 0,
                        "nested selection was inspected as a separate content item");
                });
                Test("same-category content reuses target and can be kept explicitly", delegate
                {
                    string dir = Path.Combine(root, "dup-reuse-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);
                    string target = Path.Combine(dir, "existing.bin"), source = Path.Combine(dir, "incoming.bin");
                    File.WriteAllBytes(target, new byte[] { 7, 8, 9, 10 });
                    File.Copy(target, source);
                    List<AddedEntry> entries = new List<AddedEntry> { new AddedEntry { RecordId = 1, Path = target, Category = "照片", IsResource = true } };
                    ImportBatch reuse = new ImportBatch { Category = "照片", Resource = true, Operation = ImportOperation.Move };
                    reuse.Items.Add(new ImportItem { Source = source, Origin = source, Target = Path.Combine(dir, "dest", "incoming.bin") });
                    ImportDuplicateContext context = new ImportDuplicateContext { MemoryEntries = entries };
                    ImportDuplicateReport report = ImportDuplicateInspector.InspectPaths(new[] { source }, "照片", true, context, CancellationToken.None);
                    Check(report.SameCategoryContent == 1 && report.Findings[0].CanKeep, "same-category content not found");
                    Check(ImportDuplicateInspector.InspectAndApply(reuse, new[] { source }, "照片", true, context, CancellationToken.None),
                        "reuse inspection cancelled");
                    Check(reuse.Items[0].Reference && reuse.Items[0].Source == target, "duplicate did not reuse existing target");
                    Check(File.Exists(source), "reuse deleted the source");
                    reuse.JournalDirectory = Path.Combine(dir, "journal");
                    FileImport.Run(reuse, delegate { return true; }, null);
                    Check(reuse.Items[0].State == FileImport.Linked && File.Exists(source) && File.Exists(target),
                        "reuse move did not preserve the selected source");

                    ImportDuplicateFinding finding = report.Findings.First(value => value.Kind == ImportDuplicateKind.SameCategoryContent);
                    finding.Keep = true;
                    ImportDuplicateDecision keep = ImportDuplicateDecision.FromReport(report);
                    ImportBatch kept = new ImportBatch { Category = "照片", Resource = true, Operation = ImportOperation.Move };
                    kept.Items.Add(new ImportItem { Source = source, Origin = source, Target = Path.Combine(dir, "dest", "incoming.bin") });
                    Check(ImportDuplicateInspector.InspectAndApply(kept, new[] { source }, "照片", true,
                        new ImportDuplicateContext { MemoryEntries = entries, Decision = keep }, CancellationToken.None), "keep inspection cancelled");
                    Check(!kept.Items[0].Reference && !string.IsNullOrEmpty(kept.Items[0].ExpectedHash), "explicit keep was ignored");
                    Check(File.Exists(source), "explicit keep deleted the source");
                });
                Test("cross-category content reuses target without keep option", delegate
                {
                    string dir = Path.Combine(root, "dup-cross-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);
                    string target = Path.Combine(dir, "video.bin"), source = Path.Combine(dir, "photo.bin");
                    File.WriteAllBytes(target, new byte[] { 11, 12, 13 });
                    File.Copy(target, source);
                    List<AddedEntry> entries = new List<AddedEntry> { new AddedEntry { Path = target, Category = "视频", IsResource = true } };
                    ImportDuplicateReport report = ImportDuplicateInspector.InspectPaths(new[] { source }, "照片", true,
                        new ImportDuplicateContext { MemoryEntries = entries }, CancellationToken.None);
                    Check(report.OtherCategoryContent == 1 && !report.Findings[0].CanKeep, "cross-category reuse/keep mismatch");
                    ImportBatch batch = new ImportBatch { Category = "照片", Resource = true };
                    batch.Items.Add(new ImportItem { Source = source, Origin = source, Target = Path.Combine(dir, "dest", "photo.bin") });
                    Check(ImportDuplicateInspector.InspectAndApply(batch, new[] { source }, "照片", true,
                        new ImportDuplicateContext { MemoryEntries = entries }, CancellationToken.None), "cross-category inspection cancelled");
                    Check(batch.Items[0].Reference && batch.Items[0].Source == target, "cross-category target not reused");
                    Check(File.Exists(source), "cross-category reuse deleted the source");
                });
                Test("duplicate inspector keeps resource and software records isolated", delegate
                {
                    string dir = Path.Combine(root, "dup-mode-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);
                    string target = Path.Combine(dir, "software.bin"), source = Path.Combine(dir, "resource.bin");
                    File.WriteAllBytes(target, new byte[] { 21, 22, 23, 24 });
                    File.Copy(target, source);
                    List<AddedEntry> entries = new List<AddedEntry> { new AddedEntry { Path = target, Category = "照片", IsResource = false } };
                    ImportDuplicateReport report = ImportDuplicateInspector.InspectPaths(new[] { source }, "照片", true,
                        new ImportDuplicateContext { MemoryEntries = entries }, CancellationToken.None);
                    Check(!report.NeedsPrompt, "software record was reused by a resource import");
                });
                Test("alternate data streams are reported unchecked instead of content duplicates", delegate
                {
                    string dir = Path.Combine(root, "dup-ads-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);
                    string target = Path.Combine(dir, "existing.bin"), source = Path.Combine(dir, "incoming.bin");
                    File.WriteAllBytes(target, new byte[] { 31, 32, 33 });
                    File.Copy(target, source);
                    WriteStream(source + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3");
                    List<AddedEntry> entries = new List<AddedEntry> { new AddedEntry { Path = target, Category = "照片", IsResource = true } };
                    ImportDuplicateReport report = ImportDuplicateInspector.InspectPaths(new[] { source }, "照片", true,
                        new ImportDuplicateContext { MemoryEntries = entries }, CancellationToken.None);
                    Check(report.Unchecked == 1 && report.SameCategoryContent == 0, "ADS file was treated as a main-stream duplicate");
                });
                Test("changed source invalidates a precheck skip decision", delegate
                {
                    string dir = Path.Combine(root, "dup-stale-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);
                    string first = Path.Combine(dir, "first.bin"), second = Path.Combine(dir, "second.bin");
                    File.WriteAllBytes(first, new byte[] { 41, 42, 43 });
                    File.Copy(first, second);
                    ImportDuplicateReport report = ImportDuplicateInspector.InspectPaths(new[] { first, second }, "照片", true,
                        new ImportDuplicateContext(), CancellationToken.None);
                    ImportDuplicateDecision decision = ImportDuplicateDecision.FromReport(report);
                    Check(decision.SkipSources.Contains(second) && decision.SkipHashes.ContainsKey(second), "skip decision/hash not captured");
                    File.WriteAllBytes(second, new byte[] { 51, 52, 53, 54, 55 });
                    ImportBatch batch = new ImportBatch { Category = "照片", Resource = true };
                    batch.Items.Add(new ImportItem { Source = second, Origin = second, Target = Path.Combine(dir, "dest", "second.bin") });
                    Check(ImportDuplicateInspector.InspectAndApply(batch, new[] { second }, "照片", true,
                        new ImportDuplicateContext { Decision = decision }, CancellationToken.None), "stale decision inspection cancelled");
                    Check(batch.Items[0].State != FileImport.Skipped, "changed source was still skipped");
                    Check(!string.IsNullOrEmpty(batch.Items[0].ExpectedHash), "changed source was not re-hashed");
                });
                Test("missing existing target is never reused", delegate
                {
                    string dir = Path.Combine(root, "dup-missing-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);
                    string missing = Path.Combine(dir, "missing.bin"), source = Path.Combine(dir, "incoming.bin");
                    File.WriteAllBytes(missing, new byte[] { 61, 62, 63 });
                    File.Copy(missing, source);
                    File.Delete(missing);
                    List<AddedEntry> entries = new List<AddedEntry> { new AddedEntry { Path = missing, Category = "照片", IsResource = true } };
                    ImportDuplicateReport report = ImportDuplicateInspector.InspectPaths(new[] { source }, "照片", true,
                        new ImportDuplicateContext { MemoryEntries = entries }, CancellationToken.None);
                    Check(report.ExistingPathCurrent == 0 && report.SameCategoryContent == 0, "missing target was reused");
                });
                Test("same path prefers the record already in the current category", delegate
                {
                    string dir = Path.Combine(root, "dup-priority-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, "shared.bin");
                    File.WriteAllBytes(path, new byte[] { 71, 72, 73 });
                    List<AddedEntry> entries = new List<AddedEntry>
                    {
                        new AddedEntry { RecordId = 1, Path = path, Category = "视频", IsResource = true },
                        new AddedEntry { RecordId = 2, Path = path, Category = "照片", IsResource = true }
                    };
                    ImportDuplicateReport report = ImportDuplicateInspector.InspectPaths(new[] { path }, "照片", true,
                        new ImportDuplicateContext { MemoryEntries = entries }, CancellationToken.None);
                    Check(report.ExistingPathCurrent == 1 && report.ExistingPathOther == 0, "current-category record was not preferred");
                });
                Test("final review prompts only for findings not already confirmed", delegate
                {
                    string dir = Path.Combine(root, "dup-prompt-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);
                    string target = Path.Combine(dir, "existing.bin"), source = Path.Combine(dir, "incoming.bin");
                    File.WriteAllBytes(target, new byte[] { 81, 82, 83 });
                    File.Copy(target, source);
                    List<AddedEntry> entries = new List<AddedEntry> { new AddedEntry { Path = target, Category = "照片", IsResource = true } };
                    int prompts = 0;
                    ImportBatch unreviewed = new ImportBatch { Category = "照片", Resource = true };
                    unreviewed.Items.Add(new ImportItem { Source = source, Origin = source, Target = Path.Combine(dir, "dest", "incoming.bin") });
                    Check(ImportDuplicateInspector.InspectAndApply(unreviewed, new[] { source }, "照片", true,
                        new ImportDuplicateContext { MemoryEntries = entries, Confirm = delegate { prompts++; return new string[0]; } },
                        CancellationToken.None), "unreviewed inspection cancelled");
                    Check(prompts == 1, "new duplicate finding did not prompt");

                    ImportDuplicateReport precheck = ImportDuplicateInspector.InspectPaths(new[] { source }, "照片", true,
                        new ImportDuplicateContext { MemoryEntries = entries }, CancellationToken.None);
                    ImportDuplicateDecision decision = ImportDuplicateDecision.FromReport(precheck);
                    ImportBatch reviewed = new ImportBatch { Category = "照片", Resource = true };
                    reviewed.Items.Add(new ImportItem { Source = source, Origin = source, Target = Path.Combine(dir, "dest", "incoming.bin") });
                    Check(ImportDuplicateInspector.InspectAndApply(reviewed, new[] { source }, "照片", true,
                        new ImportDuplicateContext { MemoryEntries = entries, Decision = decision, Confirm = delegate { prompts++; return new string[0]; } },
                        CancellationToken.None), "reviewed inspection cancelled");
                    Check(prompts == 1, "already confirmed finding prompted again");
                });
                Test("reviewed unchecked source prompts when final hash finds a duplicate", delegate
                {
                    string dir = Path.Combine(root, "dup-unchecked-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);
                    string target = Path.Combine(dir, "existing.bin"), source = Path.Combine(dir, "incoming.bin");
                    File.WriteAllBytes(target, new byte[] { 61, 62, 63 });
                    File.WriteAllBytes(source, new byte[0]);
                    List<AddedEntry> entries = new List<AddedEntry> { new AddedEntry { Path = target, Category = "照片", IsResource = true } };
                    ImportDuplicateReport precheck = ImportDuplicateInspector.InspectPaths(new[] { source }, "照片", true,
                        new ImportDuplicateContext { MemoryEntries = entries }, CancellationToken.None);
                    Check(precheck.Unchecked == 1, "empty source was not treated as unchecked");
                    ImportDuplicateDecision decision = ImportDuplicateDecision.FromReport(precheck);

                    File.WriteAllBytes(source, new byte[] { 61, 62, 63 });
                    int prompts = 0;
                    ImportBatch batch = new ImportBatch { Category = "照片", Resource = true };
                    batch.Items.Add(new ImportItem { Source = source, Origin = source, Target = Path.Combine(dir, "dest", "incoming.bin") });
                    Check(ImportDuplicateInspector.InspectAndApply(batch, new[] { source }, "照片", true,
                        new ImportDuplicateContext { MemoryEntries = entries, Decision = decision,
                            Confirm = delegate { prompts++; return new string[0]; } }, CancellationToken.None), "inspection cancelled");
                    Check(prompts == 1, "new duplicate after an unchecked precheck was silently accepted");
                    Check(batch.Items[0].Reference, "new duplicate was not reused after confirmation");
                });
                Test("quick fingerprint collision still requires full sha256", delegate
                {
                    string dir = Path.Combine(root, "dup-quick-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);
                    string target = Path.Combine(dir, "existing.bin"), source = Path.Combine(dir, "incoming.bin");
                    int size = 200 * 1024;
                    byte[] first = new byte[size], second = new byte[size];
                    first[100000] = 1; second[100000] = 2;
                    File.WriteAllBytes(target, first);
                    File.WriteAllBytes(source, second);
                    List<AddedEntry> entries = new List<AddedEntry> { new AddedEntry { Path = target, Category = "照片", IsResource = true } };
                    ImportDuplicateReport report = ImportDuplicateInspector.InspectPaths(new[] { source }, "照片", true,
                        new ImportDuplicateContext { MemoryEntries = entries }, CancellationToken.None);
                    Check(report.SameCategoryContent == 0 && report.OtherCategoryContent == 0 && report.BatchContentDuplicate == 0,
                        "quick fingerprint collision was treated as a full duplicate");
                    Check(report.ExpectedHashes.Count == 1, "different full hash was not retained for copy verification");
                });
                Test("same-volume move checks expected hash before hard link", delegate
                {
                    string dir = Path.Combine(root, "dup-hardlink-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);
                    string source = Path.Combine(dir, "source.bin"), target = Path.Combine(dir, "target", "moved.bin");
                    File.WriteAllText(source, "actual-content");
                    ImportBatch batch = new ImportBatch { JournalDirectory = Path.Combine(dir, "journal"),
                        Operation = ImportOperation.Move, Category = "照片", Resource = true };
                    batch.Items.Add(new ImportItem { Source = source, Target = target,
                        ExpectedHash = Convert.ToBase64String(SHA256.Create().ComputeHash(System.Text.Encoding.UTF8.GetBytes("different"))) });
                    Run(batch);
                    Check(batch.Items[0].State == FileImport.Failed, "changed hard-link source was imported");
                    Check(File.Exists(source) && !File.Exists(target), "hard-link verification did not preserve source");
                });
                Test("staging residue is logged and reported instead of silently left behind", delegate
                {
                    System.Reflection.MethodInfo remove = typeof(FileImport).GetMethod("RemoveStaging",
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    string staging = Path.Combine(root, "staging-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(Path.Combine(staging, "sub"));
                    string held = Path.Combine(staging, "sub", "held.bin"); File.WriteAllText(held, "x");
                    using (FileStream hold = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
                        Check(!(bool)remove.Invoke(null, new object[] { staging }) && File.Exists(held), "locked staging reported as cleaned");
                    Check(File.Exists(Path.Combine(root, "error.log")) && File.ReadAllText(Path.Combine(root, "error.log")).Contains(staging),
                        "staging residue was not logged");
                    Check((bool)remove.Invoke(null, new object[] { staging }) && !Directory.Exists(staging), "staging not removed once released");
                    // 目标名被占用导致失败：本批临时副本被清理，不留 .import- 残留，也不覆盖占用者
                    ImportBatch batch = Batch(ImportOperation.Copy, "occupied.txt");
                    Directory.CreateDirectory(Path.GetDirectoryName(batch.Items[0].Target));
                    File.WriteAllText(batch.Items[0].Target, "someone else");
                    Run(batch);
                    Check(batch.Items[0].State == FileImport.Failed && File.Exists(batch.Items[0].Source), "occupied target did not fail safely");
                    Check(!Directory.GetFileSystemEntries(Path.GetDirectoryName(batch.Items[0].Target)).Any(p => p.Contains(".import-")),
                        "staging residue left behind");
                    Check(File.ReadAllText(batch.Items[0].Target) == "someone else", "occupied target overwritten");
                });
                Test("collection export folder names: invalid characters, device names, dots and duplicates", delegate
                {
                    Check(CollectionExport.SafeFolderName("旅行:2024/上*半?年") == "旅行_2024_上_半_年", "invalid characters kept");
                    Check(CollectionExport.SafeFolderName("CON") == "CON_" && CollectionExport.SafeFolderName("nul.txt") == "nul_.txt"
                        && CollectionExport.SafeFolderName("com1 ") == "com1_", "device name not escaped");
                    Check(CollectionExport.SafeFolderName("..") == CollectionExport.FallbackName && CollectionExport.SafeFolderName("  ") == CollectionExport.FallbackName
                        && CollectionExport.SafeFolderName(null) == CollectionExport.FallbackName, "empty name not replaced");
                    // 与分类存储目录同一套规则：首尾的点都去掉
                    Check(CollectionExport.SafeFolderName("相册. ") == "相册" && CollectionExport.SafeFolderName(".隐藏") == "隐藏", "leading/trailing dots or spaces");
                    Check(CollectionExport.SafeFolderName("a\tb") == "a_b", "control character kept");
                    HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Check(FileNames.Claim(used, "a.jpg", true) == "a.jpg" && FileNames.Claim(used, "A.JPG", true) == "A (2).JPG"
                        && FileNames.Claim(used, "a.jpg", true) == "a (3).jpg", "file duplicates not numbered");
                    Check(FileNames.Claim(used, "x.y", false) == "x.y" && FileNames.Claim(used, "x.y", false) == "x.y (2)", "folder duplicate numbered like a file");
                    // 合并后的旧调用方行为不变：分类存储目录只多了设备名处理，桌面还原的同名编号照旧保留扩展名
                    Check(ResourceStore.SafeCategoryDirectory("照片") == "照片" && ResourceStore.SafeCategoryDirectory("..a.b..") == "a.b"
                        && ResourceStore.SafeCategoryDirectory("") == "其他" && ResourceStore.SafeCategoryDirectory("a:b") == "a_b"
                        && ResourceStore.SafeCategoryDirectory("aux") == "aux_", "category directory rule changed");
                    string occupied = Path.Combine(root, "names-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(occupied); File.WriteAllText(Path.Combine(occupied, "a.txt"), "x");
                    Check(FileNames.UniquePath(Path.Combine(occupied, "a.txt"), true) == Path.Combine(occupied, "a (2).txt")
                        && FileNames.UniquePath(Path.Combine(occupied, "b.txt"), true) == Path.Combine(occupied, "b.txt"), "unique path numbering");
                    int missing;
                    List<KeyValuePair<string, bool>> existing = FileNames.Existing(new[] { Path.Combine(occupied, "a.txt"), occupied, null,
                        Path.Combine(occupied, "A.TXT"), Path.Combine(occupied, "gone.txt") }, CancellationToken.None, out missing);
                    Check(existing.Count == 2 && !existing[0].Value && existing[1].Value && missing == 2, "existing paths: " + existing.Count + "/" + missing);
                });
                Test("collection export copies members and sub-collections into a new same-name folder and keeps sources", delegate
                {
                    string dir = Path.Combine(root, "export-" + Guid.NewGuid().ToString("N"));
                    string src = Path.Combine(dir, "src"), other = Path.Combine(dir, "other"), dest = Path.Combine(dir, "dest"), journal = Path.Combine(dir, "journal");
                    Directory.CreateDirectory(Path.Combine(src, "album", "deep")); Directory.CreateDirectory(other); Directory.CreateDirectory(dest);
                    File.WriteAllText(Path.Combine(src, "a.jpg"), "A1"); File.WriteAllText(Path.Combine(other, "a.jpg"), "A2");
                    File.WriteAllText(Path.Combine(src, "b.txt"), "B"); File.WriteAllText(Path.Combine(src, "c.png"), "C");
                    File.WriteAllText(Path.Combine(src, "album", "x.txt"), "X"); File.WriteAllText(Path.Combine(src, "album", "deep", "y.txt"), "Y");
                    WriteStream(Path.Combine(src, "b.txt") + ":note", "kept");
                    Directory.CreateDirectory(Path.Combine(dest, "旅行_2024")); File.WriteAllText(Path.Combine(dest, "旅行_2024", "mine.txt"), "M");
                    CollectionExportNode plan = new CollectionExportNode { Name = "旅行:2024", Key = "k", Unavailable = 1 };
                    plan.Sources.AddRange(new[] { Path.Combine(src, "a.jpg"), Path.Combine(other, "a.jpg"), Path.Combine(src, "b.txt"),
                        Path.Combine(src, "album"), Path.Combine(src, "a.jpg"), Path.Combine(src, "missing.jpg") });
                    CollectionExportNode child = new CollectionExportNode { Name = "b.txt" }; child.Sources.Add(Path.Combine(src, "c.png"));
                    plan.Children.Add(child); plan.Children.Add(new CollectionExportNode { Name = "空的" });
                    int unavailable; string error;
                    ImportBatch batch = CollectionExport.Prepare(plan, dest, new CollectionExportForbidden[0], journal, CancellationToken.None, out unavailable, out error);
                    Check(batch != null && error == null, "prepare failed: " + error);
                    Check(unavailable == 2 && batch.Items.Count == 5, "members not filtered: " + unavailable + "/" + batch.Items.Count);
                    Check(!Directory.Exists(batch.ExportRoot), "prepare touched the disk");
                    List<int> seen = new List<int>();
                    CollectionExportResult result = CollectionExport.Execute(batch, null, delegate(int i, string n) { seen.Add(i); }, CancellationToken.None);
                    string top = Path.Combine(dest, "旅行_2024 (2)");
                    Check(result.Kept && result.Copied == 5 && result.Failures.Count == 0 && result.Root == top, "export not kept: " + string.Join(";", result.Failures));
                    Check(File.ReadAllText(Path.Combine(top, "a.jpg")) == "A1" && File.ReadAllText(Path.Combine(top, "a (2).jpg")) == "A2", "same-name members");
                    Check(File.ReadAllText(Path.Combine(top, "b (2).txt")) == "B" && ReadStream(Path.Combine(top, "b (2).txt") + ":note") == "kept", "file vs sub-collection name or ADS");
                    Check(File.ReadAllText(Path.Combine(top, "album", "deep", "y.txt")) == "Y" && File.ReadAllText(Path.Combine(top, "album", "x.txt")) == "X", "folder member");
                    Check(File.ReadAllText(Path.Combine(top, "b.txt", "c.png")) == "C" && Directory.Exists(Path.Combine(top, "空的"))
                        && Directory.GetFileSystemEntries(Path.Combine(top, "空的")).Length == 0, "sub-collection folders");
                    Check(Directory.GetFileSystemEntries(top, "*", SearchOption.AllDirectories).Length == 10, "unexpected extra entries in export");
                    Check(File.ReadAllText(Path.Combine(dest, "旅行_2024", "mine.txt")) == "M" && Directory.GetFileSystemEntries(Path.Combine(dest, "旅行_2024")).Length == 1,
                        "existing same-name folder was merged or changed");
                    Check(File.ReadAllText(Path.Combine(src, "a.jpg")) == "A1" && File.Exists(Path.Combine(src, "album", "deep", "y.txt"))
                        && ReadStream(Path.Combine(src, "b.txt") + ":note") == "kept", "sources changed");
                    Check(!Directory.GetFileSystemEntries(top, "*.import-*", SearchOption.AllDirectories).Any(), "staging residue");
                    Check(FileImport.PendingJournals(journal).Length == 0 && File.Exists(Path.Combine(journal, batch.Id + ".complete")), "journal not settled");
                    Check(seen.Count == 5, "progress not reported per item");
                });
                Test("collection export refuses forbidden targets by path, by disk identity and inside member folders", delegate
                {
                    string dir = Path.Combine(root, "export-guard-" + Guid.NewGuid().ToString("N"));
                    string store = Path.Combine(dir, "store"), src = Path.Combine(dir, "src"), alias = Path.Combine(dir, "alias");
                    Directory.CreateDirectory(store); Directory.CreateDirectory(Path.Combine(src, "album"));
                    File.WriteAllText(Path.Combine(src, "a.jpg"), "A");
                    List<CollectionExportForbidden> forbidden = new List<CollectionExportForbidden> {
                        new CollectionExportForbidden { Path = store, Label = "资源存储位置" },
                        new CollectionExportForbidden { Path = Path.Combine(dir, "planned"), Label = "资源目录" } };
                    Check(CollectionExport.TargetError(store, forbidden) != null && CollectionExport.TargetError(store + "\\", forbidden) != null
                        && CollectionExport.TargetError(Path.Combine(store, "sub").ToUpperInvariant(), forbidden) != null, "store or its child accepted");
                    Check(CollectionExport.TargetError(dir, forbidden) == null && CollectionExport.TargetError(store + "-other", forbidden) == null, "sibling/parent rejected");
                    Check(CollectionExport.TargetError("relative", forbidden) != null && CollectionExport.TargetError("C:relative", forbidden) != null
                        && CollectionExport.TargetError(@"\rooted", forbidden) != null && CollectionExport.TargetError("", forbidden) != null, "partial path accepted");
                    Check(CollectionExport.TargetError(@"\\server\share\folder", forbidden) == null, "network share rejected by text check");
                    CollectionExportNode plan = new CollectionExportNode { Name = "planned" }; plan.Sources.Add(Path.Combine(src, "a.jpg"));
                    int unavailable; string error;
                    // 选的是禁止目录的上级、但新文件夹恰好就是还没建出来的禁止目录
                    Check(CollectionExport.Prepare(plan, dir, forbidden, Path.Combine(dir, "journal"), CancellationToken.None, out unavailable, out error) == null
                        && error != null && error.Contains("资源目录"), "final folder equal to a forbidden root accepted");
                    // 联接点指向存储位置：字符串检查拦不住，按磁盘身份要拦住
                    using (System.Diagnostics.Process mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                        "/c mklink /J \"" + alias + "\" \"" + store + "\"") { CreateNoWindow = true, UseShellExecute = false })) mk.WaitForExit();
                    Check(Directory.Exists(alias), "junction setup");
                    Check(CollectionExport.TargetError(Path.Combine(alias, "out"), forbidden) == null, "fixture: text check already rejects the alias");
                    Check(CollectionExport.RealLocationError(Path.Combine(alias, "out"), forbidden, CancellationToken.None) != null, "alias of the store accepted by real-location check");
                    Check(CollectionExport.RealLocationError(Path.Combine(src, "out"), forbidden, CancellationToken.None) == null, "unrelated folder rejected by real-location check");
                    bool stopped = false;
                    using (CancellationTokenSource canceled = new CancellationTokenSource())
                    {
                        canceled.Cancel();
                        try { CollectionExport.RealLocationError(Path.Combine(src, "out"), forbidden, canceled.Token); }
                        catch (OperationCanceledException) { stopped = true; }
                    }
                    Check(stopped, "real-location check ignored cancellation");
                    plan.Name = "x";
                    Check(CollectionExport.Prepare(plan, alias, forbidden, Path.Combine(dir, "journal"), CancellationToken.None, out unavailable, out error) == null
                        && error != null && error.Contains("资源存储位置"), "export through a junction into the store accepted: " + error);
                    Directory.Delete(alias);
                    // 指向禁止目录里某个子文件夹的别名（subst 盘、映射盘同理）：卷号+文件 ID 对不上，要靠真实路径判断
                    string inner = Path.Combine(store, "分类A"), innerAlias = Path.Combine(dir, "inner-alias");
                    Directory.CreateDirectory(inner);
                    using (System.Diagnostics.Process mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                        "/c mklink /J \"" + innerAlias + "\" \"" + inner + "\"") { CreateNoWindow = true, UseShellExecute = false })) mk.WaitForExit();
                    Check(Directory.Exists(innerAlias), "inner junction setup");
                    Check(CollectionExport.TargetError(Path.Combine(innerAlias, "out"), forbidden) == null, "fixture: text check already rejects the inner alias");
                    error = CollectionExport.RealLocationError(Path.Combine(innerAlias, "out"), forbidden, CancellationToken.None);
                    Check(error != null && error.Contains("资源存储位置"), "alias of a folder inside the store accepted: " + error);
                    Directory.Delete(innerAlias);
                    // 所选位置已不存在：不补建，提示重选
                    Check(CollectionExport.Prepare(plan, Path.Combine(dir, "gone", "deeper"), forbidden, Path.Combine(dir, "journal"), CancellationToken.None, out unavailable, out error) == null
                        && error != null && error.Contains("已不存在") && !Directory.Exists(Path.Combine(dir, "gone")), "missing parent was recreated");
                    // 导出位置在成员文件夹里面：整批拒绝，不复制
                    plan.Sources.Add(Path.Combine(src, "album"));
                    Check(CollectionExport.Prepare(plan, Path.Combine(src, "album"), forbidden, Path.Combine(dir, "journal"), CancellationToken.None, out unavailable, out error) == null
                        && error != null && error.Contains("集合成员文件夹"), "export into a member folder accepted");
                    Check(!Directory.Exists(Path.Combine(src, "album", "x")) && !Directory.Exists(Path.Combine(dir, "journal")), "refused export left files behind");
                    // 一个实体文件都没有：不建文件夹
                    CollectionExportNode empty = new CollectionExportNode { Name = "空", Unavailable = 2 };
                    empty.Sources.Add(Path.Combine(src, "gone.jpg"));
                    Check(CollectionExport.Prepare(empty, dir, forbidden, Path.Combine(dir, "journal"), CancellationToken.None, out unavailable, out error) == null
                        && unavailable == 3 && error.Contains("没有可导出的文件"), "empty collection export prepared");
                });
                Test("collection export cancel, decline and failure roll back copies and new folders; keep retains the rest", delegate
                {
                    string dir = Path.Combine(root, "export-roll-" + Guid.NewGuid().ToString("N"));
                    string src = Path.Combine(dir, "src"), dest = Path.Combine(dir, "dest"), journal = Path.Combine(dir, "journal");
                    Directory.CreateDirectory(src); Directory.CreateDirectory(dest);
                    CollectionExportNode plan = new CollectionExportNode { Name = "集" };
                    foreach (string name in new[] { "1.txt", "2.txt", "3.txt" })
                    {
                        File.WriteAllText(Path.Combine(src, name), "data-" + name); plan.Sources.Add(Path.Combine(src, name));
                    }
                    CollectionExportNode child = new CollectionExportNode { Name = "子" }; child.Sources.Add(Path.Combine(src, "1.txt"));
                    plan.Children.Add(child);
                    int unavailable; string error;
                    Func<ImportBatch> prepare = delegate
                    {
                        ImportBatch prepared = CollectionExport.Prepare(plan, dest, null, journal, CancellationToken.None, out unavailable, out error);
                        Check(prepared != null, "prepare failed: " + error);
                        return prepared;
                    };
                    Action sourcesIntact = delegate
                    {
                        foreach (string name in new[] { "1.txt", "2.txt", "3.txt" })
                            Check(File.ReadAllText(Path.Combine(src, name)) == "data-" + name, "source changed: " + name);
                    };

                    // 取消：已复制的两项撤回，新建的导出文件夹和子文件夹删除
                    using (CancellationTokenSource cancel = new CancellationTokenSource())
                    {
                        ImportBatch batch = prepare();
                        int asked = 0;
                        CollectionExportResult result = CollectionExport.Execute(batch, delegate(ImportBatch b) { asked++; return true; },
                            delegate(int i, string n) { if (i == 2) cancel.Cancel(); }, cancel.Token);
                        Check(asked == 0 && result.Canceled && !result.Kept && result.Leftovers.Count == 0, "cancel result");
                        Check(!Directory.Exists(batch.ExportRoot) && Directory.GetFileSystemEntries(dest).Length == 0, "cancel left copies or folders");
                        Check(FileImport.PendingJournals(journal).Length == 0, "cancel left journal pending");
                    }
                    sourcesIntact();

                    // 部分失败：一项被占用。拒绝保留时整批撤回；保留时其余项留下，并补建子集合文件夹
                    using (FileStream hold = new FileStream(Path.Combine(src, "2.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        int asked = 0;
                        ImportBatch batch = prepare();
                        CollectionExportResult result = CollectionExport.Execute(batch, delegate(ImportBatch b) { asked++; return false; }, null, CancellationToken.None);
                        Check(asked == 1 && result.Declined && !result.Kept && result.Failures.Count == 1 && result.Failures[0].StartsWith("2.txt"), "decline result");
                        Check(!Directory.Exists(batch.ExportRoot), "declined export left files");

                        batch = prepare();
                        result = CollectionExport.Execute(batch, delegate(ImportBatch b) { asked++; return true; }, null, CancellationToken.None);
                        Check(asked == 2 && result.Kept && result.Copied == 3 && result.Failures.Count == 1, "keep result");
                        Check(File.Exists(Path.Combine(batch.ExportRoot, "1.txt")) && File.Exists(Path.Combine(batch.ExportRoot, "子", "1.txt"))
                            && !File.Exists(Path.Combine(batch.ExportRoot, "2.txt")), "kept export content");
                        Directory.Delete(batch.ExportRoot, true);
                    }
                    sourcesIntact();

                    // 全部失败：不问，直接撤回
                    using (new FileStream(Path.Combine(src, "1.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
                    using (new FileStream(Path.Combine(src, "2.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
                    using (new FileStream(Path.Combine(src, "3.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        ImportBatch batch = prepare();
                        int asked = 0;
                        CollectionExportResult result = CollectionExport.Execute(batch, delegate(ImportBatch b) { asked++; return true; }, null, CancellationToken.None);
                        Check(asked == 0 && !result.Kept && !result.Declined && result.Failures.Count == 4 && !Directory.Exists(batch.ExportRoot), "all-failed result");
                    }

                    // 复制到一半磁盘满：不询问，整批撤回
                    {
                        ImportBatch batch = prepare();
                        int asked = 0;
                        CollectionExportResult result = CollectionExport.Execute(batch, delegate(ImportBatch b) { asked++; return true; },
                            delegate(int i, string n) { if (i == 2) batch.SpaceLimited = true; }, CancellationToken.None);
                        Check(asked == 0 && result.SpaceLimited && !result.Kept && !result.Declined, "space-limited export asked or kept");
                        Check(!Directory.Exists(batch.ExportRoot) && Directory.GetFileSystemEntries(dest).Length == 0 && result.Leftovers.Count == 0,
                            "space-limited export left copies or folders");
                        Check(FileImport.PendingJournals(journal).Length == 0, "space-limited export left journal pending");
                    }
                    sourcesIntact();

                    // 询问保留期间用户选了取消或退出：即使回答「保留」也按撤回处理
                    using (new FileStream(Path.Combine(src, "2.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
                    using (CancellationTokenSource cancel = new CancellationTokenSource())
                    {
                        ImportBatch batch = prepare();
                        CollectionExportResult result = CollectionExport.Execute(batch, delegate(ImportBatch b) { cancel.Cancel(); return true; }, null, cancel.Token);
                        Check(!result.Kept && result.Canceled && !result.Declined && !Directory.Exists(batch.ExportRoot), "keep answered after cancel was honoured");
                    }
                    sourcesIntact();

                    // 选了保留之后日志才写不进去（日志目录被换成同名文件）：副本保留，不撤回，并说明记录没保存
                    using (new FileStream(Path.Combine(src, "2.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        ImportBatch batch = prepare();
                        CollectionExportResult result = CollectionExport.Execute(batch, delegate(ImportBatch b)
                        {
                            Directory.Delete(journal, true); File.WriteAllText(journal, "not a folder");
                            return true;
                        }, null, CancellationToken.None);
                        File.Delete(journal);
                        Check(result.Kept && result.Copied == 3 && File.Exists(Path.Combine(batch.ExportRoot, "1.txt"))
                            && Directory.Exists(Path.Combine(batch.ExportRoot, "子")), "kept export rolled back after a journal failure");
                        Check(result.Failures.Any(f => f.StartsWith("2.txt")) && result.Failures.Any(f => f.Contains("导出记录未能保存")),
                            "journal failure or item failure not reported: " + string.Join(" | ", result.Failures));
                        Directory.Delete(batch.ExportRoot, true);
                    }
                    sourcesIntact();

                    // 复制引擎中途抛出（日志写不进去之类）：当场撤回
                    {
                        ImportBatch batch = prepare();
                        CollectionExportResult result = CollectionExport.Execute(batch, null,
                            delegate(int i, string n) { if (i == 2) throw new IOException("journal disk gone"); }, CancellationToken.None);
                        Check(!result.Kept && result.Error != null && result.Error.Contains("journal disk gone"), "abort result");
                        Check(!Directory.Exists(batch.ExportRoot) && result.Leftovers.Count == 0, "abort left copies");
                    }
                    sourcesIntact();
                });
                Test("interrupted collection export is rolled back at next start; finished exports are left alone", delegate
                {
                    string dir = Path.Combine(root, "export-recover-" + Guid.NewGuid().ToString("N"));
                    string src = Path.Combine(dir, "src"), dest = Path.Combine(dir, "dest"), journal = Path.Combine(dir, "journal");
                    Directory.CreateDirectory(src); Directory.CreateDirectory(dest);
                    CollectionExportNode plan = new CollectionExportNode { Name = "中断" };
                    foreach (string name in new[] { "1.txt", "2.txt", "3.txt" })
                    {
                        File.WriteAllText(Path.Combine(src, name), "data-" + name); plan.Sources.Add(Path.Combine(src, name));
                    }
                    plan.Children.Add(new CollectionExportNode { Name = "子" });
                    int unavailable; string error;
                    ImportBatch batch = CollectionExport.Prepare(plan, dest, null, journal, CancellationToken.None, out unavailable, out error);
                    try
                    {
                        // 进程在第三项开始前崩溃：日志每秒最多落盘一次，这两项在日志里还没有身份记录，
                        // 另留一个本批的临时副本，模拟正在复制的那一项（走 Execute：先建导出文件夹、记身份标记）
                        CollectionExport.Execute(batch, null, delegate(int i, string n)
                        {
                            if (i != 2) return;
                            File.WriteAllText(batch.Items[2].Target + ".import-" + batch.Id, "partial");
                            throw new InvalidProgramException("simulated crash");
                        }, CancellationToken.None);
                    }
                    catch (InvalidProgramException) { }
                    Check(FileImport.PendingJournals(journal).Length == 1 && File.Exists(Path.Combine(batch.ExportRoot, "2.txt"))
                        && string.IsNullOrEmpty(Reload(batch).Items[1].TargetId), "crash fixture: journal already had the copy identity");
                    CollectionExport.Recover(journal);
                    Check(!Directory.Exists(batch.ExportRoot), "interrupted export not rolled back");
                    Check(FileImport.PendingJournals(journal).Length == 0 && FileImport.Load(Path.Combine(journal, batch.Id + ".xml")).Finished, "journal not finished");
                    Check(File.ReadAllText(Path.Combine(src, "2.txt")) == "data-2.txt", "recovery touched sources");

                    ImportBatch done = CollectionExport.Prepare(plan, dest, null, journal, CancellationToken.None, out unavailable, out error);
                    Check(CollectionExport.Execute(done, null, null, CancellationToken.None).Kept, "fixture export failed");
                    File.WriteAllText(Path.Combine(journal, done.Id + ".pending"), "");   // 收尾后、删标记前断电
                    CollectionExport.Recover(journal);
                    Check(File.Exists(Path.Combine(done.ExportRoot, "3.txt")) && Directory.Exists(Path.Combine(done.ExportRoot, "子")), "finished export was rolled back");
                    Check(FileImport.PendingJournals(journal).Length == 0, "finished export left pending");

                    // 日志上限：撤回、取消的批次也按时间裁剪，仍待恢复的保留
                    string prune = Path.Combine(dir, "prune"); Directory.CreateDirectory(prune);
                    for (int i = 0; i < CollectionExport.JournalKeep + 5; i++)
                    {
                        string xml = Path.Combine(prune, "b" + i + ".xml"); File.WriteAllText(xml, "x");
                        File.SetLastWriteTimeUtc(xml, DateTime.UtcNow.AddMinutes(i));
                    }
                    File.WriteAllText(Path.Combine(prune, "b0.pending"), "");
                    CollectionExport.PruneJournal(prune, CollectionExport.JournalKeep);
                    Check(Directory.GetFiles(prune, "*.xml").Length == CollectionExport.JournalKeep + 1 && File.Exists(Path.Combine(prune, "b0.xml"))
                        && !File.Exists(Path.Combine(prune, "b1.xml")) && File.Exists(Path.Combine(prune, "b" + (CollectionExport.JournalKeep + 4) + ".xml")),
                        "export journal not capped or pending journal dropped");
                });
                Test("export recovery only touches the folder it created: stale journals, tampered paths and finished-but-interrupted batches", delegate
                {
                    string dir = Path.Combine(root, "export-own-" + Guid.NewGuid().ToString("N"));
                    string src = Path.Combine(dir, "src"), dest = Path.Combine(dir, "dest"), journal = Path.Combine(dir, "journal");
                    Directory.CreateDirectory(src); Directory.CreateDirectory(dest);
                    CollectionExportNode plan = new CollectionExportNode { Name = "旅行" };
                    foreach (string name in new[] { "1.txt", "2.txt" })
                    {
                        File.WriteAllText(Path.Combine(src, name), "data-" + name); plan.Sources.Add(Path.Combine(src, name));
                    }
                    plan.Children.Add(new CollectionExportNode { Name = "空" });
                    int unavailable; string error;

                    // 一次被撤回、但未完成标记残留的导出；之后同一位置又成功导出了一次
                    ImportBatch stale = CollectionExport.Prepare(plan, dest, null, journal, CancellationToken.None, out unavailable, out error);
                    CollectionExportResult declined = CollectionExport.Execute(stale, delegate { return false; },
                        delegate(int i, string n) { if (i == 1) throw new IOException("disk gone"); }, CancellationToken.None);
                    Check(!declined.Kept && !Directory.Exists(stale.ExportRoot) && !string.IsNullOrEmpty(stale.ExportRootStamp), "stale fixture");
                    File.WriteAllText(Path.Combine(journal, stale.Id + ".pending"), "");
                    ImportBatch fresh = CollectionExport.Prepare(plan, dest, null, journal, CancellationToken.None, out unavailable, out error);
                    Check(fresh.ExportRoot == stale.ExportRoot, "fixture: second export should reuse the same folder name");
                    Check(CollectionExport.Execute(fresh, null, null, CancellationToken.None).Kept, "second export failed");
                    CollectionExport.Recover(journal);
                    Check(File.Exists(Path.Combine(fresh.ExportRoot, "1.txt")) && File.Exists(Path.Combine(fresh.ExportRoot, "2.txt"))
                        && Directory.Exists(Path.Combine(fresh.ExportRoot, "空")), "stale journal deleted a later export at the same path");

                    // 日志被改写成别处的路径：恢复不删导出文件夹以外的空目录
                    string outside = Path.Combine(dir, "outside-empty"); Directory.CreateDirectory(outside);
                    ImportBatch tampered = CollectionExport.Prepare(plan, dest, null, journal, CancellationToken.None, out unavailable, out error);
                    try
                    {
                        // 走 Execute（先建导出文件夹、记身份标记），复制一项后进程崩溃；崩溃前日志里混进了外面的路径
                        CollectionExport.Execute(tampered, null, delegate(int i, string n)
                        {
                            if (i != 1) return;
                            tampered.ExportDirectories.Add(outside);
                            FileImport.Save(tampered);
                            throw new InvalidProgramException("simulated crash");
                        }, CancellationToken.None);
                    }
                    catch (InvalidProgramException) { }
                    Check(File.Exists(Path.Combine(tampered.ExportRoot, "1.txt")) && FileImport.PendingJournals(journal).Length == 1, "tampered fixture");
                    CollectionExport.Recover(journal);
                    Check(!Directory.Exists(tampered.ExportRoot), "recovery did not roll back the interrupted export");
                    Check(Directory.Exists(outside), "recovery deleted an empty folder outside the export");

                    // 已决定保留、收尾前中断：恢复补建空的子集合文件夹，副本保留
                    ImportBatch kept = CollectionExport.Prepare(plan, dest, null, journal, CancellationToken.None, out unavailable, out error);
                    Check(CollectionExport.Execute(kept, null, null, CancellationToken.None).Kept, "fixture export failed");
                    Directory.Delete(Path.Combine(kept.ExportRoot, "空"));
                    File.WriteAllText(Path.Combine(journal, kept.Id + ".pending"), "");
                    CollectionExport.Recover(journal);
                    Check(Directory.Exists(Path.Combine(kept.ExportRoot, "空")) && File.Exists(Path.Combine(kept.ExportRoot, "1.txt")),
                        "interrupted kept export not completed");

                    // 准备之后导出位置被别的程序占了：不往里写，提示重试
                    ImportBatch raced = CollectionExport.Prepare(plan, dest, null, journal, CancellationToken.None, out unavailable, out error);
                    Directory.CreateDirectory(raced.ExportRoot); File.WriteAllText(Path.Combine(raced.ExportRoot, "theirs.txt"), "t");
                    CollectionExportResult refused = CollectionExport.Execute(raced, null, null, CancellationToken.None);
                    Check(refused.Refused && !refused.Kept && refused.Error.Contains("刚被占用")
                        && Directory.GetFileSystemEntries(raced.ExportRoot).Length == 1, "export wrote into a folder created by someone else");
                });
                Test("journal saves back off for large batches and import journals carry no export fields", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Copy, "a.txt");
                    FileImport.Save(batch);
                    Check(!File.ReadAllText(Path.Combine(batch.JournalDirectory, batch.Id + ".xml")).Contains("Export"), "import journal serialized export fields");
                    batch.SaveCost = 50;
                    Check(FileImport.SaveInterval(batch) == 1000, "small batches should keep the one-second interval");
                    batch.SaveCost = 400;
                    Check(FileImport.SaveInterval(batch) == 4000, "large batches should save less often");
                    batch.SaveCost = int.MaxValue;
                    Check(FileImport.SaveInterval(batch) == int.MaxValue, "interval overflowed");
                    // 1 万项的批次：落盘耗时，用来说明放宽间隔的必要
                    ImportBatch big = new ImportBatch { JournalDirectory = batch.JournalDirectory, Operation = ImportOperation.Copy };
                    for (int i = 0; i < 10000; i++)
                        big.Items.Add(new ImportItem { Source = @"C:\source\folder\file-" + i + ".jpg", Target = @"D:\export\集合\file-" + i + ".jpg" });
                    FileImport.Save(big);
                    Console.WriteLine("INFO journal save of 10000 items took " + big.SaveCost + " ms, next progress save after " + FileImport.SaveInterval(big) + " ms");
                });
            }
            finally { Paths.DataDirOverride = null; Directory.Delete(root, true); }
            Console.WriteLine("RESULT " + passed + " passed, " + failed + " failed"); return failed == 0 ? 0 : 1;
        }
    }
}
