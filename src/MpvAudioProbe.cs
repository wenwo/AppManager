using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AppManager.Controls;

namespace AppManager
{
    internal enum MusicProbeStatus
    {
        Success,
        Failed,
        TimedOut,
        Unavailable,
        Cancelled
    }

    internal sealed class MusicProbeResult
    {
        public MusicProbeStatus Status;
        public MusicProbeInfo Info = new MusicProbeInfo();

        internal static MusicProbeResult Success(MusicProbeInfo info)
        {
            return new MusicProbeResult { Status = MusicProbeStatus.Success, Info = info ?? new MusicProbeInfo() };
        }

        internal static MusicProbeResult Failed(MusicProbeInfo info)
        {
            return new MusicProbeResult { Status = MusicProbeStatus.Failed, Info = info ?? new MusicProbeInfo() };
        }

        internal static MusicProbeResult TimedOut(MusicProbeInfo info)
        {
            return new MusicProbeResult { Status = MusicProbeStatus.TimedOut, Info = info ?? new MusicProbeInfo() };
        }

        internal static MusicProbeResult Unavailable(string error)
        {
            return new MusicProbeResult
            {
                Status = MusicProbeStatus.Unavailable,
                Info = new MusicProbeInfo { Unavailable = true, Error = error ?? "" }
            };
        }

        internal static MusicProbeResult Cancelled()
        {
            return new MusicProbeResult
            {
                Status = MusicProbeStatus.Cancelled,
                Info = new MusicProbeInfo { Error = "已取消" }
            };
        }
    }

    internal interface IMusicProbeSession : IDisposable
    {
        MusicProbeResult Probe(string path, int timeoutMilliseconds, CancellationToken cancel);
    }

    /// <summary>
    /// libmpv 探测会话：实例和线程同生共死。超时只放弃当前会话，不等待卡住的原生调用；
    /// 下一次探测必须新建实例。这样坏盘阻塞不会拖住整个扫描线程。
    /// </summary>
    internal sealed class MpvAudioProbeSession : IMusicProbeSession
    {
        internal const int DefaultTimeoutMilliseconds = 3000;
        private const int ExternalTimeoutGraceMilliseconds = 500;

        private readonly object sync = new object();
        private readonly AutoResetEvent request = new AutoResetEvent(false);
        private readonly ManualResetEvent done = new ManualResetEvent(false);
        private readonly ManualResetEvent ready = new ManualResetEvent(false);
        private readonly List<AudioEngineEvent> events = new List<AudioEngineEvent>();
        private readonly Thread worker;
        private MpvAudioEngine engine;
        private string pendingPath;
        private int pendingTimeout;
        private MusicProbeInfo pendingInfo;
        private volatile bool stop;
        private volatile bool abandoned;
        private volatile string initError;
        private int disposed;

        internal MpvAudioProbeSession()
        {
            worker = new Thread(Run);
            worker.IsBackground = true;
            worker.Name = "AppManager.MusicProbe";
            worker.Start();
        }

        internal string InitError { get { return initError; } }

        public MusicProbeResult Probe(string path, int timeoutMilliseconds, CancellationToken cancel)
        {
            DateTime readyDeadline = DateTime.UtcNow.AddMilliseconds(5000);
            while (!ready.WaitOne(50))
            {
                if (cancel.IsCancellationRequested)
                {
                    Abandon();
                    return MusicProbeResult.Cancelled();
                }
                if (DateTime.UtcNow >= readyDeadline)
                {
                    Abandon();
                    return MusicProbeResult.Unavailable("播放组件启动超时");
                }
            }
            if (!string.IsNullOrEmpty(initError)) return MusicProbeResult.Unavailable(initError);
            if (stop || abandoned) return MusicProbeResult.Unavailable("播放组件已停止");

            int timeout = Math.Max(100, timeoutMilliseconds);
            lock (sync) { pendingPath = path; pendingTimeout = timeout; pendingInfo = null; }
            done.Reset();
            request.Set();

            int waitMilliseconds = timeout + ExternalTimeoutGraceMilliseconds;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(waitMilliseconds);
            while (true)
            {
                if (cancel.IsCancellationRequested)
                {
                    Abandon();
                    return MusicProbeResult.Cancelled();
                }
                int remaining = (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalMilliseconds);
                if (remaining <= 0)
                {
                    Abandon();
                    return MusicProbeResult.TimedOut(new MusicProbeInfo { Path = path, TimedOut = true, Error = "识别超时" });
                }
                if (done.WaitOne(Math.Min(50, remaining))) break;
            }

            MusicProbeInfo info;
            lock (sync) { info = pendingInfo; }
            if (info == null) return MusicProbeResult.Failed(new MusicProbeInfo { Path = path, Failed = true, Error = "探测无结果" });
            if (info.Unavailable)
            {
                Abandon();
                return MusicProbeResult.Unavailable(info.Error);
            }
            if (info.TimedOut)
            {
                Abandon();
                return MusicProbeResult.TimedOut(info);
            }
            if (info.Failed) return MusicProbeResult.Failed(info);
            return MusicProbeResult.Success(info);
        }

        private void Run()
        {
            try
            {
                lock (sync) { engine = new MpvAudioEngine(true); }
                ready.Set();
                while (!stop)
                {
                    request.WaitOne();
                    if (stop || abandoned) break;
                    string path;
                    int timeout;
                    lock (sync) { path = pendingPath; pendingPath = null; timeout = pendingTimeout; }
                    if (string.IsNullOrEmpty(path)) continue;
                    MusicProbeInfo info = ProbeFile(path, timeout);
                    lock (sync) { pendingInfo = info; }
                    if (!abandoned) done.Set();
                    if (info.TimedOut || info.Unavailable || abandoned) break;
                }
            }
            catch (Exception ex)
            {
                initError = DescribeProbeFailure(ex);
                ready.Set();
            }
            finally
            {
                ready.Set();
                ReleaseEngine();
            }
        }

        private MusicProbeInfo ProbeFile(string path, int timeoutMilliseconds)
        {
            MusicProbeInfo info = new MusicProbeInfo { Path = path };
            MpvAudioEngine current;
            lock (sync) { current = engine; }
            if (current == null)
            {
                info.Unavailable = true;
                info.Error = "播放组件已停止";
                return info;
            }
            try
            {
                current.DiscardEvents();
                if (current.LoadFile(path) < 0)
                {
                    info.Failed = true;
                    info.Error = "加载命令失败";
                    return info;
                }

                bool loaded = false, failed = false;
                Stopwatch watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < timeoutMilliseconds)
                {
                    if (stop || abandoned)
                    {
                        info.Failed = true;
                        info.Error = "已放弃";
                        return info;
                    }
                    current.WaitEvents(events, 0.05);
                    if (stop || abandoned)
                    {
                        info.Failed = true;
                        info.Error = "已放弃";
                        return info;
                    }
                    for (int i = 0; i < events.Count; i++)
                    {
                        AudioEngineEvent item = events[i];
                        if (item.Kind == AudioEngineEventKind.FileLoaded) loaded = true;
                        else if (item.Kind == AudioEngineEventKind.EndFile && item.EndReason == MpvAudioEngine.EndFileReasonError)
                            failed = true;
                    }
                    if (loaded || failed) break;
                }

                if (!loaded)
                {
                    if (failed)
                    {
                        info.Failed = true;
                        info.Error = "无法解析音频";
                    }
                    else
                    {
                        info.TimedOut = true;
                        info.Error = "识别超时";
                    }
                    return info;
                }
                if (stop || abandoned)
                {
                    info.Failed = true;
                    info.Error = "已放弃";
                    return info;
                }
                ReadInfo(current, info);
            }
            catch (DllNotFoundException ex) { info.Unavailable = true; info.Error = DescribeProbeFailure(ex); }
            catch (BadImageFormatException ex) { info.Unavailable = true; info.Error = DescribeProbeFailure(ex); }
            catch (EntryPointNotFoundException ex) { info.Unavailable = true; info.Error = DescribeProbeFailure(ex); }
            catch (Exception ex) { info.Failed = true; info.Error = ex.Message; }
            return info;
        }

        private static void ReadInfo(MpvAudioEngine current, MusicProbeInfo info)
        {
            double duration;
            if (current.TryGetDouble("duration", out duration) && !double.IsNaN(duration)
                && !double.IsInfinity(duration) && duration >= 0) info.DurationSeconds = duration;

            long count;
            if (current.TryGetLong("track-list/count", out count))
            {
                for (int i = 0; i < count && i < 64; i++)
                {
                    string prefix = "track-list/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/";
                    string type;
                    if (!current.TryGetString(prefix + "type", out type) || string.IsNullOrEmpty(type)
                        || !string.Equals(type, "audio", StringComparison.OrdinalIgnoreCase)) continue;
                    info.HasAudioStream = true;
                    long value, channels = 0, sampleRate = 0;
                    if (current.TryGetLong(prefix + "demux-channel-count", out value) && value > 0)
                        channels = Math.Max(channels, value);
                    if (current.TryGetLong(prefix + "demux-samplerate", out value) && value > 0)
                        sampleRate = Math.Max(sampleRate, value);
                    if (channels > 0) info.Channels = (int)Math.Min(channels, int.MaxValue);
                    if (sampleRate > 0) info.SampleRate = (int)Math.Min(sampleRate, int.MaxValue);
                }
            }

            string text;
            if (current.TryGetString("metadata/by-key/artist", out text)) info.Artist = text ?? "";
            if (current.TryGetString("metadata/by-key/album", out text)) info.Album = text ?? "";
            if (current.TryGetString("metadata/by-key/genre", out text)) info.Genre = text ?? "";
            if (current.TryGetString("metadata/by-key/title", out text)) info.Title = text ?? "";
        }

        private static string DescribeProbeFailure(Exception ex)
        {
            if (ex is DllNotFoundException || ex is BadImageFormatException || ex is EntryPointNotFoundException)
                return "播放组件缺失或版本不兼容";
            return string.IsNullOrEmpty(ex.Message) ? "播放组件不可用" : ex.Message;
        }

        internal static bool TryCheckAvailability(out string error)
        {
            return TryCheckAvailability(out error, CancellationToken.None);
        }

        internal static bool TryCheckAvailability(out string error, CancellationToken cancel)
        {
            error = null;
            using (MpvAudioProbeSession session = new MpvAudioProbeSession())
            {
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(5000);
                while (!session.ready.WaitOne(50))
                {
                    if (cancel.IsCancellationRequested) return false;
                    if (DateTime.UtcNow >= deadline)
                    {
                        error = "播放组件启动超时";
                        return false;
                    }
                }
                error = session.initError;
                return string.IsNullOrEmpty(error);
            }
        }

        private void Abandon()
        {
            abandoned = true;
            stop = true;
            request.Set();
            ReleaseEngine();
        }

        private void ReleaseEngine()
        {
            MpvAudioEngine old;
            lock (sync) { old = engine; engine = null; }
            if (old != null) MpvShutdown.Destroy(old, null);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Abandon();
        }
    }
}