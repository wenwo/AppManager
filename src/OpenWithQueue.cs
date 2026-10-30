using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace AppManager
{
    // A separate STA thread keeps the modal Windows picker off the main UI thread.
    // Cancelling a picker cancels all pending files; repeated clicks do not duplicate them.
    internal sealed class OpenWithQueue : IDisposable
    {
        private readonly object gate = new object();
        private readonly Queue<string> pending = new Queue<string>();
        private readonly HashSet<string> queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<string, bool> show;
        private readonly Action<Exception> reportError;
        private bool running, disposed;

        public OpenWithQueue(Func<string, bool> show, Action<Exception> reportError)
        {
            this.show = show;
            this.reportError = reportError;
        }

        public bool Enqueue(string path)
        {
            lock (gate)
            {
                if (disposed) return false;
                if (queued.Contains(path)) return true;
                if (queued.Count >= 256) return false;
                queued.Add(path);
                pending.Enqueue(path);
                if (running) return true;
                try
                {
                    Thread worker = new Thread(Drain) { IsBackground = true, Name = "External viewer picker" };
                    worker.SetApartmentState(ApartmentState.STA);
                    running = true;
                    worker.Start();
                }
                catch
                {
                    running = false;
                    pending.Clear(); queued.Clear();
                    throw;
                }
                return true;
            }
        }

        private void Drain()
        {
            while (true)
            {
                string path;
                lock (gate)
                {
                    if (disposed || pending.Count == 0) { running = false; return; }
                    path = pending.Dequeue();
                }
                bool opened = false;
                try { opened = show(path); }
                catch (Exception ex)
                {
                    Debug.WriteLine("External viewer picker failed: " + ex.Message);
                    try { if (reportError != null) reportError(ex); }
                    catch (Exception reportFailure) { Debug.WriteLine(reportFailure.Message); }
                }
                lock (gate)
                {
                    queued.Remove(path);
                    if (!opened) { pending.Clear(); queued.Clear(); }
                }
            }
        }

        public void Dispose()
        {
            lock (gate) { disposed = true; pending.Clear(); queued.Clear(); }
        }
    }
}
