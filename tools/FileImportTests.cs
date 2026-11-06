using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                    Check(MediaRules.ResourceCategoryFilter("照片").Contains("*.rw2"), "photo filter missing RW2");
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
                        string excluded = Path.Combine(scanRoot, "excluded.mp3");
                        string tiny = Path.Combine(scanRoot, "tiny.mp3");
                        string cloud = Path.Combine(scanRoot, "cloud.mp3");
                        string common = Path.Combine(steamCommon, "game.mp3");
                        string program = Path.Combine(programData, "prompt.mp3");
                        WriteSized(keep1, 70000); WriteSized(keep2, 70000);
                        WriteSized(included, 70000); WriteSized(excluded, 70000);
                        WriteSized(tiny, 1024); WriteSized(cloud, 70000);
                        WriteSized(common, 70000); WriteSized(program, 70000);
                        File.SetAttributes(cloud, FileAttributes.Offline);
                        MusicScanRequest request = new MusicScanRequest
                        {
                            Roots = new List<string> { scanRoot },
                            IncludedPaths = new List<string> { included },
                            ExcludedPaths = new List<string> { excluded },
                            ExtensionOnly = true
                        };
                        LocalMusicScanResult result = LocalMusicScanner.Scan(request, CancellationToken.None, null);
                        Check(result.Files.Contains(keep1) && result.Files.Contains(keep2), "safe music directory was skipped");
                        Check(!result.Files.Contains(included) && result.CountOf(MusicFilterReason.AlreadyIncluded) == 1, "included source was not skipped");
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
            }
            finally { Paths.DataDirOverride = null; Directory.Delete(root, true); }
            Console.WriteLine("RESULT " + passed + " passed, " + failed + " failed"); return failed == 0 ? 0 : 1;
        }
    }
}
