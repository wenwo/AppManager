using System;
using System.Threading;
using System.Windows.Forms;

namespace AppManager.Controls
{
    // One running load and one replaceable pending request. UI notifications also coalesce.
    internal sealed class PreviewLoader<T> : IDisposable where T : class
    {
        private sealed class Request
        {
            public Func<CancellationToken, T> Load;
            public Action<T, Exception> Complete;
            public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            public int Version;
        }

        private readonly Control owner;
        private readonly object gate = new object();
        private Request active, pending;
        private T ready;
        private Action<T, Exception> readyCallback;
        private Exception readyError;
        private bool running, notificationQueued, disposed;
        private int version;

        public PreviewLoader(Control owner)
        {
            this.owner = owner;
            owner.HandleDestroyed += HandleDestroyed;
        }

        private void HandleDestroyed(object sender, EventArgs e)
        {
            lock (gate) { CancelLocked(); notificationQueued = false; }
        }

        public void Load(Func<CancellationToken, T> load, Action<T, Exception> complete)
        {
            // All callers run on the UI thread. Ensure BeginInvoke has a handle even in hidden tests.
            lock (gate)
            {
                if (disposed || owner.IsDisposed || owner.Disposing) return;
                IntPtr handle = owner.Handle;
                CancelLocked();
                pending = new Request { Load = load, Complete = complete, Version = version };
                if (running) return;
                running = true;
                ThreadPool.QueueUserWorkItem(delegate { Drain(); });
            }
        }

        public void Clear()
        {
            lock (gate) CancelLocked();
        }

        private void CancelLocked()
        {
            version++;
            if (active != null) active.Cancellation.Cancel();
            if (pending != null) { pending.Cancellation.Dispose(); pending = null; }
            Release(ready); ready = null; readyCallback = null; readyError = null;
        }

        private void Drain()
        {
            while (true)
            {
                Request request;
                lock (gate)
                {
                    request = pending; pending = null;
                    if (request == null || disposed) { running = false; return; }
                    active = request;
                }
                T result = null;
                Exception error = null;
                try { result = request.Load(request.Cancellation.Token); }
                catch (Exception ex) { error = ex; }
                bool post = false;
                lock (gate)
                {
                    active = null;
                    request.Cancellation.Dispose();
                    if (disposed || request.Version != version) Release(result);
                    else
                    {
                        Release(ready);
                        ready = result; readyError = error; readyCallback = request.Complete;
                        if (!notificationQueued) { notificationQueued = true; post = true; }
                    }
                }
                if (post)
                {
                    try { owner.BeginInvoke((Action)Publish); }
                    catch (InvalidOperationException)
                    {
                        lock (gate)
                        {
                            if (request.Version == version) { notificationQueued = false; CancelLocked(); }
                        }
                    }
                }
            }
        }

        private void Publish()
        {
            T result;
            Exception error;
            Action<T, Exception> callback;
            lock (gate)
            {
                notificationQueued = false;
                result = ready; error = readyError; callback = readyCallback;
                ready = null; readyCallback = null; readyError = null;
            }
            if (disposed || owner.IsDisposed || callback == null) { Release(result); return; }
            callback(result, error);
        }

        private static void Release(T value)
        {
            IDisposable disposable = value as IDisposable;
            if (disposable != null) disposable.Dispose();
        }

        public void Dispose()
        {
            lock (gate) { disposed = true; CancelLocked(); }
            owner.HandleDestroyed -= HandleDestroyed;
        }
    }
}
