using System;
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
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
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
                Test("cancellation keeps every source and commits finished items only", delegate
                {
                    ImportBatch batch = Batch(ImportOperation.Move, "a.txt", "b.txt");
                    using (CancellationTokenSource cancel = new CancellationTokenSource())
                        FileImport.Run(batch, delegate { return true; }, delegate(int index, string name) { if (index == 1) cancel.Cancel(); }, cancel.Token);
                    Check(batch.Items[0].State == FileImport.Copied && batch.Items[1].State == FileImport.Failed && !batch.DeleteAuthorized, "cancel states");
                    Check(batch.Items.All(i => File.Exists(i.Source)), "cancel deleted source");
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
            }
            finally { Directory.Delete(root, true); }
            Console.WriteLine("RESULT " + passed + " passed, " + failed + " failed"); return failed == 0 ? 0 : 1;
        }
    }
}
