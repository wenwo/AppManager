using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace AppManager.Controls
{
    public enum PlaybackMode { Sequential, RepeatAll, RepeatOne, Shuffle }

    /// <summary>
    /// 唯一的音频播放会话：持有列表、当前/预排索引、播放模式、进度和 mpv 内核。
    /// 预排下一首交给 mpv 播放列表自动切换，界面线程只负责显示状态。
    /// </summary>
    public sealed class MusicPlaybackSession : IDisposable
    {
        private const int PlayingPollMs = 150;
        private const int PausedPollMs = 600;
        private readonly List<string> paths = new List<string>();
        private readonly List<AudioEngineEvent> events = new List<AudioEngineEvent>();
        private readonly Random random = new Random();
        private readonly Timer pollTimer = new Timer { Interval = PlayingPollMs };
        private MpvAudioEngine engine;
        private const int MaxAutoSkips = 3;
        private int index = -1, queuedIndex = -1, volume = 60, autoSkipBudget;
        private bool muted, playing, ended, loaded, loading, engineUnavailable;
        private bool queuedAppended, waitingAutoStart, autoStartFallbackIssued, pendingPause, awaitingManualStart;
        private double position, duration, pendingSeek = -1;
        private string failure = "";
        private DateTime loadDeadline, autoStartDeadline;
        private long currentEntryId = -1;
        private PlaybackMode mode = PlaybackMode.Sequential;

        public event Action StateChanged;
        public event Action TrackChanged;
        public event Action PlaybackStarting;

        public MusicPlaybackSession()
        {
            pollTimer.Tick += PollTimer_Tick;
        }
        public int PathCount { get { return paths.Count; } }
        public int Index { get { return index; } }
        public string CurrentPath { get { return index >= 0 && index < paths.Count ? paths[index] : null; } }
        public string CurrentTitle
        {
            get
            {
                string path = CurrentPath;
                return string.IsNullOrEmpty(path) ? "" : Path.GetFileNameWithoutExtension(path);
            }
        }
        public bool HasSession { get { return engine != null && paths.Count > 0; } }
        public bool HasTracks { get { return paths.Count > 0; } }
        public bool IsPlaying { get { return playing; } }
        public bool IsLoaded { get { return loaded; } }
        public bool Ended { get { return ended; } }
        public double Position { get { return position; } }
        public double Duration { get { return duration; } }
        public int Volume { get { return volume; } }
        public bool Muted { get { return muted; } }
        public string Failure { get { return failure; } }
        public PlaybackMode Mode { get { return mode; } }
        internal MpvAudioEngine Engine { get { return engine; } }
        internal bool HasQueuedNext { get { return queuedAppended; } }

        public string PathAt(int value)
        {
            return value >= 0 && value < paths.Count ? paths[value] : null;
        }

        public List<string> PathsSnapshot() { return new List<string>(paths); }
        public void Open(List<string> audioPaths, int startIndex)
        {
            Open(audioPaths, startIndex, 0, false, null);
        }

        public void Open(List<string> audioPaths, int startIndex, double startPosition, bool startPaused)
        {
            Open(audioPaths, startIndex, startPosition, startPaused, null);
        }

        public void Open(List<string> audioPaths, int startIndex, double startPosition, bool startPaused,
            PlaybackMode? modeOverride)
        {
            if (audioPaths == null || audioPaths.Count == 0)
            {
                Stop();
                paths.Clear();
                index = -1;
                RaiseTrackChanged();
                RaiseStateChanged();
                return;
            }
            paths.Clear();
            paths.AddRange(audioPaths);
            if (modeOverride.HasValue) mode = modeOverride.Value;
            index = Math.Max(0, Math.Min(startIndex, paths.Count - 1));
            queuedIndex = -1;
            queuedAppended = false;
            waitingAutoStart = false;
            autoSkipBudget = 0;
            currentEntryId = -1;
            LoadAt(index, startPosition, startPaused);
        }

        public void SetMode(PlaybackMode value)
        {
            if (mode == value) return;
            mode = value;
            if (engine != null) engine.SetLoopFile(mode == PlaybackMode.RepeatOne);
            // 已收到当前曲 EOF、等 mpv 触发下一首 START_FILE 时，播放列表不能被重排，
            // 否则会把正在切换的项当成旧预排项，导致下标错位或一次跳过两首。
            if (engine != null && paths.Count > 0 && !ended && !waitingAutoStart)
            {
                ClearQueued();
                QueueNext();
            }
            RaiseStateChanged();
        }

        public void CycleMode()
        {
            SetMode((PlaybackMode)(((int)mode + 1) % 4));
        }
        public void PlayAt(int value)
        {
            if (value < 0 || value >= paths.Count) return;
            ClearQueued();
            autoSkipBudget = 0;
            LoadAt(value, 0, false);
        }

        public void Next()
        {
            if (paths.Count == 0) return;
            int target = ComputeNext(index, false);
            if (target < 0) return;
            if (queuedAppended && queuedIndex == target && !waitingAutoStart)
            {
                if (engine != null) engine.PlaylistNext();
                return;
            }
            ClearQueued();
            autoSkipBudget = 0;
            LoadAt(target, 0, false);
        }

        public void Previous()
        {
            if (paths.Count == 0) return;
            if (position > 4) { Seek(0); return; }
            ClearQueued();
            autoSkipBudget = 0;
            int target = index > 0 ? index - 1 : paths.Count - 1;
            LoadAt(target, 0, false);
        }
        public void TogglePause()
        {
            if (engine == null || paths.Count == 0) return;
            if (failure != "") { RestartCurrent(); return; }
            if (ended) { RestartCurrent(); return; }
            if (!loaded) return;
            playing = !playing;
            pendingPause = !playing;
            engine.SetPause(!playing);
            if (playing) RaisePlaybackStarting();
            pollTimer.Interval = playing ? PlayingPollMs : PausedPollMs;
            pollTimer.Start();
            RaiseStateChanged();
        }

        public void Pause()
        {
            if (engine == null || !playing) return;
            playing = false;
            pendingPause = true;
            engine.SetPause(true);
            pollTimer.Interval = PausedPollMs;
            pollTimer.Start();
            RaiseStateChanged();
        }

        public void RestartCurrent()
        {
            if (index >= 0 && index < paths.Count) LoadAt(index, 0, false);
        }

        public void Seek(double value)
        {
            if (engine == null || duration <= 0) return;
            position = Math.Max(0, Math.Min(value, duration));
            engine.Seek(position);
            ended = false;
            RaiseStateChanged();
        }

        public void SetVolume(int value)
        {
            volume = Math.Max(0, Math.Min(100, value));
            if (volume > 0) muted = false;
            if (engine != null) { engine.SetVolume(volume); engine.SetMute(muted); }
            RaiseStateChanged();
        }

        public void ToggleMute()
        {
            muted = !muted;
            if (engine != null) engine.SetMute(muted);
            RaiseStateChanged();
        }

        public void SetMute(bool value)
        {
            muted = value;
            if (engine != null) engine.SetMute(muted);
            RaiseStateChanged();
        }
        private bool EnsureEngine()
        {
            if (engine != null) return true;
            if (engineUnavailable) return false;
            try
            {
                engine = new MpvAudioEngine();
                engine.SetVolume(volume);
                engine.SetMute(muted);
                engine.SetLoopFile(mode == PlaybackMode.RepeatOne);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("音乐播放内核初始化失败: " + ex.Message);
                engineUnavailable = true;
                engine = null;
                return false;
            }
        }

        private void LoadAt(int value, double startPosition, bool startPaused)
        {
            if (value < 0 || value >= paths.Count) return;
            pollTimer.Stop();
            index = value;
            queuedIndex = -1;
            queuedAppended = false;
            waitingAutoStart = false;
            autoStartFallbackIssued = false;
            duration = position = 0;
            loaded = false;
            loading = true;
            ended = false;
            failure = "";
            playing = true;
            pendingSeek = startPosition > 0 ? startPosition : -1;
            pendingPause = startPaused;
            RaiseTrackChanged();
            if (!EnsureEngine())
            {
                failure = "播放组件不可用（缺少 libmpv-2.dll）";
                loading = false;
                playing = false;
                RaiseStateChanged();
                return;
            }
            engine.DiscardEvents();
            awaitingManualStart = true;
            engine.SetVolume(volume);
            engine.SetMute(muted);
            engine.SetLoopFile(mode == PlaybackMode.RepeatOne);
            if (startPaused) engine.SetPause(true);
            engine.LoadFile(paths[index]);
            if (!startPaused) engine.SetPause(false);
            loadDeadline = DateTime.UtcNow.AddSeconds(12);
            pollTimer.Interval = PlayingPollMs;
            pollTimer.Start();
            RaiseStateChanged();
        }
        private void PollTimer_Tick(object sender, EventArgs e)
        {
            if (engine == null) return;
            engine.DrainEvents(events);
            for (int i = 0; i < events.Count && engine != null; i++) HandleEvent(events[i]);
            if (engine == null) return;

            if (loading && !loaded && !waitingAutoStart && DateTime.UtcNow > loadDeadline)
            {
                Fail("加载音乐超时");
                return;
            }
            if (waitingAutoStart && !autoStartFallbackIssued && DateTime.UtcNow > autoStartDeadline)
            {
                autoStartFallbackIssued = true;
                autoStartDeadline = DateTime.UtcNow.AddSeconds(2);
                engine.PlaylistNext();
            }
            else if (waitingAutoStart && autoStartFallbackIssued && DateTime.UtcNow > autoStartDeadline)
            {
                HandleQueuedFailure();
                return;
            }
            if (!ended && loaded && !queuedAppended && !waitingAutoStart)
            {
                bool eof;
                if (engine.TryGetFlag("eof-reached", out eof) && eof)
                {
                    HandleEnded();
                    return;
                }
            }

            double value;
            if (engine.TryGetDouble("duration", out value) && value >= 0) duration = value;
            if (loaded && engine.TryGetDouble("time-pos", out value) && value >= 0) position = value;
            bool paused;
            if (engine.TryGetFlag("pause", out paused)) playing = !paused;
            pollTimer.Interval = playing ? PlayingPollMs : PausedPollMs;
            RaiseStateChanged();
        }

        private void HandleEvent(AudioEngineEvent item)
        {
            // loadfile 之后、新曲 START_FILE 之前到达的事件属于上一首；旧代码的 awaitingStart 同样作用。
            if (awaitingManualStart)
            {
                if (item.Kind != AudioEngineEventKind.StartFile) return;
                awaitingManualStart = false;
            }
            if (item.Kind == AudioEngineEventKind.StartFile)
            {
                currentEntryId = item.PlaylistEntryId;
                bool wasQueued = queuedAppended;
                if (wasQueued)
                {
                    int started = queuedIndex;
                    queuedIndex = -1;
                    queuedAppended = false;
                    if (started >= 0 && started < paths.Count)
                    {
                        index = started;
                        RaiseTrackChanged();
                    }
                    engine.RemovePlaylistIndex(0);
                }
                waitingAutoStart = false;
                autoStartFallbackIssued = false;
                loaded = false;
                loading = true;
                ended = false;
                failure = "";
                duration = position = 0;
                playing = true;
                if (pendingPause && engine != null) engine.SetPause(true);
                loadDeadline = DateTime.UtcNow.AddSeconds(12);
                pollTimer.Interval = PlayingPollMs;
                pollTimer.Start();
                RaisePlaybackStarting();
                RaiseStateChanged();
            }
            else if (item.Kind == AudioEngineEventKind.FileLoaded)
            {
                loaded = true;
                loading = false;
                autoSkipBudget = 0;
                double value;
                if (engine.TryGetDouble("duration", out value) && value >= 0) duration = value;
                if (pendingSeek > 0)
                {
                    engine.Seek(pendingSeek);
                    position = pendingSeek;
                }
                pendingSeek = -1;
                if (pendingPause)
                {
                    engine.SetPause(true);
                    playing = false;
                }
                else
                {
                    engine.SetPause(false);
                    playing = true;
                }
                pendingPause = false;
                if (!queuedAppended && mode != PlaybackMode.RepeatOne) QueueNext();
                RaiseStateChanged();
            }
            else if (item.Kind == AudioEngineEventKind.EndFile)
            {
                HandleEndFile(item);
            }
        }
        private void HandleEndFile(AudioEngineEvent item)
        {
            if (item.EndReason == 0)
            {
                // 单曲循环由 loop-file=inf 在 mpv 内部完成，不在这里再 seek 一次。
                if (mode == PlaybackMode.RepeatOne) return;
                if (currentEntryId >= 0 && item.PlaylistEntryId != currentEntryId)
                {
                    if (queuedAppended) HandleQueuedFailure();
                    return;
                }
                if (queuedAppended)
                {
                    waitingAutoStart = true;
                    autoStartFallbackIssued = false;
                    autoStartDeadline = DateTime.UtcNow.AddSeconds(1);
                    return;
                }
                HandleEnded();
                return;
            }
            if (item.EndReason == 4)
            {
                if (queuedAppended && (currentEntryId < 0 || item.PlaylistEntryId != currentEntryId))
                {
                    HandleQueuedFailure();
                    return;
                }
                if (TryAutoSkip(index)) return;
                Fail("无法播放此音乐");
            }
        }

        private void HandleEnded()
        {
            if (mode == PlaybackMode.RepeatOne)
            {
                position = 0;
                ended = false;
                engine.Seek(0);
                engine.SetPause(false);
                playing = true;
                RaiseStateChanged();
                return;
            }
            int next = ComputeNext(index, true);
            if (next >= 0)
            {
                LoadAt(next, 0, false);
                return;
            }
            playing = false;
            ended = true;
            position = duration;
            pollTimer.Stop();
            RaiseStateChanged();
        }

        private int ComputeNext(int from, bool automatic)
        {
            if (paths.Count == 0) return -1;
            if (mode == PlaybackMode.Shuffle)
            {
                if (paths.Count == 1) return from;
                int next = from;
                while (next == from) next = random.Next(paths.Count);
                return next;
            }
            if (from + 1 < paths.Count) return from + 1;
            if (mode == PlaybackMode.RepeatAll) return 0;
            return automatic ? -1 : 0;
        }
        private void QueueNext()
        {
            if (engine == null || paths.Count == 0 || index < 0) return;
            if (mode == PlaybackMode.RepeatOne)
            {
                engine.SetLoopFile(true);
                return;
            }
            engine.SetLoopFile(false);
            if (queuedAppended) return;
            int next = ComputeNext(index, true);
            if (next < 0) return;
            queuedIndex = next;
            queuedAppended = true;
            engine.AppendFile(paths[next]);
        }

        private void ClearQueued()
        {
            if (engine != null && queuedAppended && !waitingAutoStart) engine.RemovePlaylistIndex(1);
            queuedIndex = -1;
            queuedAppended = false;
            waitingAutoStart = false;
            autoStartFallbackIssued = false;
        }

        private bool TryAutoSkip(int failedIndex)
        {
            if (autoSkipBudget <= 0) return false;
            autoSkipBudget--;
            int next = ComputeNext(failedIndex, true);
            if (next < 0 || next == failedIndex) return false;
            ClearQueued();
            LoadAt(next, 0, false);
            return true;
        }

        private void HandleQueuedFailure()
        {
            int failed = queuedIndex >= 0 ? queuedIndex : index;
            ClearQueued();
            autoSkipBudget = Math.Max(autoSkipBudget, MaxAutoSkips);
            if (TryAutoSkip(failed)) return;
            Fail("无法播放队列中的音乐");
        }

        private void Fail(string message)
        {
            failure = message;
            playing = false;
            loading = false;
            ended = false;
            queuedIndex = -1;
            queuedAppended = false;
            waitingAutoStart = false;
            awaitingManualStart = false;
            pollTimer.Stop();
            if (engine != null) engine.Stop();
            RaiseStateChanged();
        }
        /// <summary>文件改名或移动后同步当前列表；已预排进 mpv 的下一首重新排队。</summary>
        public void RebasePath(string oldPath, string newPath)
        {
            bool currentChanged = false;
            for (int i = 0; i < paths.Count; i++)
            {
                string updated = EntryPaths.Rebase(paths[i], oldPath, newPath);
                if (!string.Equals(updated, paths[i], StringComparison.OrdinalIgnoreCase))
                {
                    paths[i] = updated;
                    if (i == index) currentChanged = true;
                }
            }
            if (queuedAppended && engine != null && !waitingAutoStart)
            {
                engine.RemovePlaylistIndex(1);
                queuedAppended = false;
                queuedIndex = -1;
                QueueNext();
            }
            if (currentChanged) RaiseTrackChanged();
        }

        public void Stop()
        {
            pollTimer.Stop();
            playing = false;
            ended = false;
            loaded = false;
            loading = false;
            failure = "";
            queuedIndex = -1;
            queuedAppended = false;
            waitingAutoStart = false;
            autoStartFallbackIssued = false;
            awaitingManualStart = false;
            pendingSeek = -1;
            pendingPause = false;
            currentEntryId = -1;
            position = duration = 0;
            paths.Clear();
            index = -1;
            if (engine != null)
            {
                MpvAudioEngine old = engine;
                engine = null;
                try { old.SetPause(true); } catch { }
                MpvShutdown.Destroy(old, null);
            }
            RaiseTrackChanged();
            RaiseStateChanged();
        }

        private void RaiseStateChanged()
        {
            Action handler = StateChanged;
            if (handler != null) handler();
        }

        private void RaiseTrackChanged()
        {
            Action handler = TrackChanged;
            if (handler != null) handler();
        }

        private void RaisePlaybackStarting()
        {
            Action handler = PlaybackStarting;
            if (handler != null) handler();
        }

        public void Dispose()
        {
            Stop();
            pollTimer.Dispose();
            MpvShutdown.WaitAll(3000);
        }
    }
}
