using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace AppManager.Controls
{
    /// <summary>
    /// 应用内视频播放器：双击视频后替换列表区域播放（窗口其余部分不变）。
    /// 外观沿用主界面浅色卡片风格：顶栏仅「返回」，中间为黑色圆角视频卡片 + 深色悬浮操作条，
    /// 底部为平铺信息栏（文件名 + 当前/总数），与侧边栏底栏一致；
    /// 操作条为 上一张/播放暂停/进度条/时间/下一张/静音，Esc/返回退出，←/→ 切换视频，空格播放暂停，
    /// 滚轮调音量，单击视频区暂停/播放，双击视频区请求全屏（由主窗体切换窗体状态）。
    /// 播放内核为 libmpv（mpv 官方 C 客户端库，内置 FFmpeg）：格式/字幕/音轨/硬件解码均由 mpv 处理，
    /// 画面经 wid 选项嵌入本控件的子窗口渲染；组件缺失或解码失败时只展示占位文案，不再转外部播放器。
    /// </summary>
    public class VideoPlayerControl : Control
    {
        public event Action PlaybackStarting;
        /// <summary>请求退出播放器（Esc / 返回按钮），由主窗体恢复列表。</summary>
        public event Action CloseRequested;
        /// <summary>双击视频区：请求切换全屏（主窗体负责窗体状态与布局）。</summary>
        public event Action FullscreenRequested;

        // 播放序列
        private List<string> paths = new List<string>();
        private int index;
        private bool loadFailed;        // 当前视频无法播放（展示占位文案）
        private string failText = "无法播放此视频";

        // libmpv 播放内核与渲染宿主
        private VideoHost host;         // 画面宿主子窗口：mpv 通过 wid 直接渲染到该窗口
        private MpvEngine mpv;          // 首次播放时惰性创建，随控件一起销毁
        private bool mpvUnavailable;    // 初始化失败（缺 DLL/初始化报错），后续不再重试

        // 播放状态
        private bool playing;
        private bool seeking;           // 拖动进度条中，暂停轮询回写位置
        private bool fileLoaded;        // 已收到 MPV_EVENT_FILE_LOADED
        private bool eofReached;        // 播放到结尾（keep-open 停在最后一帧）
        private double duration;        // 当前媒体时长（秒），0 表示未知
        private double position;        // 当前播放位置（秒）
        private DateTime loadDeadline;  // 打开后超过该时刻仍未加载成功视为失败
        private readonly Timer pollTimer = new Timer { Interval = 400 };

        // 界面按钮悬停/按下态：-1 无；0 上一张 1 播放/暂停 2 下一张 3 返回 4 静音
        private int hover = -1;
        private int press = -1;

        // 界面按钮几何：OnPaint 布局后缓存，命中测试与绘制共用
        private Rectangle backRect;
        private Rectangle videoCardRect;
        private Rectangle infoBarRect;
        private Rectangle hintRect;       // 顶栏右侧的音量提示位（文件名/序号已移至底部信息栏）
        private Rectangle pillRect;
        private Rectangle prevRect, playRect, seekRect, timeRect, nextRect, volRect;
        private Rectangle volSliderRect;   // 音量滑块：静止显示音量等级，按住拖动调节
        private bool volDrag;              // 正在拖动音量滑块

        // 音量：本地缓存（0-100），滚轮/静音按钮变更并同步到 mpv；volUntil 控制音量提示条的显示时限
        private int volume = 50;
        private bool muted;
        private DateTime volUntil = DateTime.MinValue;

        // 单击视频区暂停/双击全屏：单击先挂起，超过系统双击间隔仍无第二击才触发暂停，
        // 第二击在间隔内则由 OnMouseDoubleClick 接管为全屏（避免双击时误触发一次暂停）
        private readonly Timer clickTimer = new Timer();

        private static readonly Color Bg = Theme.Background;                    // 与主界面一致的浅色底
        private static readonly Color VideoBg = Color.FromArgb(20, 20, 22);    // 视频卡片内部黑底
        private static readonly Color TextWhite = Color.White;
        private static readonly Color TextDim = Color.FromArgb(160, 255, 255, 255);
        private const int TopBarH = 44;
        private const int PillH = 40;
        private const int PillBtnW = 44;
        private const int TimeColW = 96;
        private const int VolSliderW = 72;
        private const int ChromePad = 16;       // 视频卡片与内容区之间的留白
        private const int CardGap = 12;         // 视频卡片 / 操作条 / 信息栏之间的间距
        private const int InfoBarH = 44;        // 底部信息栏高度（与侧边栏底栏一致）
        private const int VideoRadius = 14;     // 视频卡片圆角

        public VideoPlayerControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Bg;
            TabStop = true;
            Visible = false;
            pollTimer.Tick += OnPoll;
            clickTimer.Interval = SystemInformation.DoubleClickTime;
            clickTimer.Tick += delegate { clickTimer.Stop(); TogglePlay(); };   // 间隔内无第二击：按单击处理（暂停/播放）
        }

        /// <summary>视频卡片区：顶栏以下、操作条以上的全部空间（同时是 mpv 渲染宿主的边界）。</summary>
        private Rectangle VideoArea
        {
            get
            {
                int bottom = Height - InfoBarH - CardGap - PillH - CardGap;
                return new Rectangle(ChromePad, TopBarH, Math.Max(0, Width - ChromePad * 2),
                    Math.Max(0, bottom - TopBarH));
            }
        }

        /// <summary>打开一组视频并定位到指定下标；外部负责显隐切换。</summary>
        public void Open(List<string> videoPaths, int startIndex)
        {
            paths = videoPaths ?? new List<string>();
            if (paths.Count == 0) { RequestClose(); return; }
            index = Math.Min(Math.Max(startIndex, 0), paths.Count - 1);
            Visible = true;
            EnsureEngine();
            Focus();
            LoadCurrent();
        }

        /// <summary>请求关闭：统一走事件，由主窗体恢复列表布局。</summary>
        private void RequestClose()
        {
            Action h = CloseRequested;
            if (h != null) h();
        }

        // ===== 播放内核 =====

        /// <summary>
        /// 惰性创建渲染宿主与 libmpv 实例：避免未使用播放器时也常驻内存。
        /// 初始化失败（如缺少 libmpv-2.dll）置 mpvUnavailable，打开时只显示占位文案。
        /// </summary>
        private bool EnsureEngine()
        {
            if (mpv != null) return true;
            if (mpvUnavailable) return false;
            try
            {
                if (host == null)
                {
                    host = new VideoHost();
                    Controls.Add(host);
                    IntPtr h = host.Handle;   // 先建句柄，供 mpv 以 wid 方式嵌入渲染
                }
                host.Bounds = VideoArea;
                ApplyHostRegion();
                host.Visible = true;
                mpv = new MpvEngine(host.Handle);
                mpv.SetVolume(volume);
                mpv.SetMute(muted);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("libmpv 初始化失败: " + ex.Message);
                mpvUnavailable = true;
                if (host != null) { Controls.Remove(host); host.Dispose(); host = null; }
                mpv = null;
                return false;
            }
        }

        /// <summary>视频宿主按卡片圆角裁切：宿主是子窗口，不裁角会用黑色矩形盖住卡片圆角。</summary>
        private void ApplyHostRegion()
        {
            if (host == null || host.Width <= 0 || host.Height <= 0) return;
            Region old = host.Region;
            using (GraphicsPath p = Draw.RoundedPath(new Rectangle(0, 0, host.Width, host.Height), VideoRadius))
                host.Region = new Region(p);
            if (old != null) old.Dispose();
        }

        /// <summary>加载当前视频并自动播放；失败进入占位态（不再回退外部播放器）。</summary>
        private void LoadCurrent()
        {
            loadFailed = false;
            fileLoaded = false;
            eofReached = false;
            duration = 0;
            position = 0;
            playing = false;
            seeking = false;
            string path = paths[index];
            if (mpv == null || !File.Exists(path))
            {
                MarkFailed(mpv == null ? "播放组件不可用（缺少 libmpv-2.dll）" : "文件不存在");
                return;
            }
            try
            {
                Action starting = PlaybackStarting;
                if (starting != null) starting();
                mpv.LoadFile(path);
                mpv.SetPause(false);
                loadDeadline = DateTime.UtcNow.AddSeconds(12);
                pollTimer.Start();
                if (host != null) host.Visible = true;
                Invalidate();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("加载视频失败: " + ex.Message);
                MarkFailed("无法播放此视频");
            }
        }

        /// <summary>进入失败占位态：停止轮询并遮住画面宿主，与图片查看器的占位处理一致。</summary>
        private void MarkFailed(string text)
        {
            loadFailed = true;
            failText = text;
            pollTimer.Stop();
            seeking = false;
            playing = false;
            eofReached = false;
            clickTimer.Stop();
            if (mpv != null) { try { mpv.Stop(); } catch { } }
            if (host != null) host.Visible = false;
            Invalidate();
        }

        /// <summary>轮询播放状态/进度；mpv 事件（加载完成/解码失败）也在同一轮询中排空。</summary>
        private void OnPoll(object sender, EventArgs e)
        {
            MpvEngine p = mpv;
            if (p == null) return;
            try
            {
                bool loaded = false;
                bool endError = false;
                p.DrainEvents(ref loaded, ref endError);
                if (loaded) fileLoaded = true;
                if (endError) { MarkFailed("无法播放此视频（解码失败）"); return; }
                if (!fileLoaded)
                {
                    if (DateTime.UtcNow > loadDeadline) { MarkFailed("无法播放此视频（解码超时）"); return; }
                }
                else
                {
                    double d;
                    if (p.TryGetDouble("duration", out d) && d > 0 && !double.IsNaN(d)) duration = d;
                    bool eof;
                    if (p.TryGetFlag("eof-reached", out eof)) eofReached = eof;
                    if (!seeking)
                    {
                        double t;
                        if (p.TryGetDouble("time-pos", out t) && t >= 0) position = t;
                    }
                    bool paused;
                    if (!p.TryGetFlag("pause", out paused)) paused = true;
                    playing = !paused && !eofReached;
                    if (eofReached && duration > 0) position = duration;   // 停在终点，按钮回到「播放」
                }
            }
            catch { }
            DisableHostInput();
            Invalidate();
        }

        private void TogglePlay()
        {
            if (loadFailed) return;
            MpvEngine p = mpv;
            if (p == null) return;
            try
            {
                if (!playing || eofReached)
                {
                    Action starting = PlaybackStarting;
                    if (starting != null) starting();
                }
                if (eofReached)
                {
                    // 播放结束停在终点：重新播放从头开始
                    p.Seek(0);
                    position = 0;
                    eofReached = false;
                    p.SetPause(false);
                }
                else if (playing) p.SetPause(true);
                else p.SetPause(false);
            }
            catch { }
            Invalidate();
        }

        /// <summary>按 X 坐标定位进度条并跳转。</summary>
        private void SeekTo(int x)
        {
            if (duration <= 0) return;
            double t = (x - seekRect.X) / (double)Math.Max(1, seekRect.Width) * duration;
            if (t < 0) t = 0;
            if (t > duration) t = duration;
            position = t;
            MpvEngine p = mpv;
            if (p != null) { try { p.Seek(t); } catch { } }
            Invalidate();
        }

        private void Prev()
        {
            if (paths.Count < 2) return;
            index = (index - 1 + paths.Count) % paths.Count;
            LoadCurrent();
        }

        private void Next()
        {
            if (paths.Count < 2) return;
            index = (index + 1) % paths.Count;
            LoadCurrent();
        }

        // ===== 音量控制 =====

        /// <summary>静音/取消静音；取消时音量为 0 则恢复到可听值，并显示提示条。</summary>
        private void ToggleMute()
        {
            if (muted)
            {
                muted = false;
                if (volume <= 0) volume = 40;
            }
            else muted = true;
            MpvEngine p = mpv;
            if (p != null) { try { p.SetMute(muted); p.SetVolume(volume); } catch { } }
            volUntil = DateTime.UtcNow.AddMilliseconds(900);
            Invalidate();
        }

        /// <summary>滚轮调节音量（±5，夹在 0-100），归零视为静音，并显示提示条。</summary>
        private void ChangeVolume(int delta)
        {
            volume = Math.Max(0, Math.Min(100, volume + delta));
            muted = volume <= 0;
            MpvEngine p = mpv;
            if (p != null) { try { p.SetMute(muted); p.SetVolume(volume); } catch { } }
            volUntil = DateTime.UtcNow.AddMilliseconds(900);
            Invalidate();
        }

        /// <summary>音量滑块拖动：按 X 位置换算 0-100，实时同步 mpv 并短提示。</summary>
        private void SetVolumeByX(int x)
        {
            int w = Math.Max(1, volSliderRect.Width);
            int v = (int)Math.Round((x - volSliderRect.X) / (double)w * 100);
            if (v < 0) v = 0;
            if (v > 100) v = 100;
            volume = v;
            muted = volume <= 0;
            MpvEngine p = mpv;
            if (p != null) { try { p.SetMute(muted); p.SetVolume(volume); } catch { } }
            volUntil = DateTime.UtcNow.AddMilliseconds(900);
            Invalidate();
        }

        /// <summary>停止播放（隐藏时自动调用，避免后台继续出声）。</summary>
        private void StopPlayback()
        {
            pollTimer.Stop();
            clickTimer.Stop();   // 取消挂起的单击，避免隐藏后延迟触发暂停/播放
            seeking = false;
            playing = false;
            eofReached = false;
            fileLoaded = false;
            MpvEngine p = mpv;
            if (p != null) { try { p.Stop(); } catch { } }
        }

        /// <summary>
        /// mpv 若在宿主内自建子窗口，则逐个置为禁用，让鼠标消息回传给本控件；
        /// 宿主本身也已禁用输入，两者配合保证单击/双击/滚轮统一由本控件处理。
        /// </summary>
        private void DisableHostInput()
        {
            if (host == null || !host.IsHandleCreated) return;
            IntPtr child = GetWindow(host.Handle, GW_CHILD);
            while (child != IntPtr.Zero)
            {
                int style = GetWindowLong(child, GWL_STYLE);
                if ((style & WS_DISABLED) == 0)
                    SetWindowLong(child, GWL_STYLE, style | WS_DISABLED);
                child = GetWindow(child, GW_HWNDNEXT);
            }
        }

        // ===== 命中测试 =====

        /// <summary>界面按钮命中：0 上一张 1 播放/暂停 2 下一张 3 返回 4 静音，未命中 -1（进度条区域单独判定）。</summary>
        private int HitButton(Point p)
        {
            if (backRect.Contains(p)) return 3;
            if (prevRect.Contains(p)) return 0;
            if (playRect.Contains(p)) return 1;
            if (nextRect.Contains(p)) return 2;
            if (volRect.Contains(p)) return 4;
            return -1;
        }

        /// <summary>进度条命中：操作条内、按钮与时间列之外的横向条带。</summary>
        private bool HitSeek(Point p)
        {
            return pillRect.Contains(p)
                && p.X >= seekRect.X - 6 && p.X <= seekRect.Right + 6;
        }

        // ===== 交互 =====

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;
            clickTimer.Stop();   // 任何新的按下都取消挂起的单击动作（改按按钮/拖动则不触发暂停）
            Capture = true;
            int btn = HitButton(e.Location);
            if (btn >= 0) { press = btn; Invalidate(); return; }
            if (HitVolumeSlider(e.Location))
            {
                volDrag = true;
                SetVolumeByX(e.X);
                return;
            }
            if (HitSeek(e.Location))
            {
                seeking = true;
                SeekTo(e.X);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (volDrag) { SetVolumeByX(e.X); return; }
            if (seeking) { SeekTo(e.X); return; }
            int h = HitButton(e.Location);
            if (h != hover) { hover = h; Invalidate(); }
            Cursor = (h >= 0 || HitSeek(e.Location) || HitVolumeSlider(e.Location))
                ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Capture = false;
            if (volDrag) { volDrag = false; return; }
            if (seeking) { seeking = false; return; }
            if (e.Button == MouseButtons.Left && press >= 0)
            {
                int btn = press;
                press = -1;
                Invalidate();
                if (HitButton(e.Location) == btn)
                {
                    switch (btn)
                    {
                        case 0: Prev(); break;
                        case 1: TogglePlay(); break;
                        case 2: Next(); break;
                        case 3: RequestClose(); break;
                        case 4: ToggleMute(); break;
                    }
                }
            }
        }

        /// <summary>视频区单击：挂起到双击间隔，仍无第二击才暂停/播放（双击由 OnMouseDoubleClick 接管全屏）。</summary>
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left || loadFailed) return;
            if (!VideoArea.Contains(e.Location)) return;
            clickTimer.Start();
        }

        /// <summary>视频区双击：取消挂起的单击暂停，请求切换全屏。</summary>
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left || loadFailed) return;
            if (!VideoArea.Contains(e.Location)) return;
            clickTimer.Stop();
            Action h = FullscreenRequested;
            if (h != null) h();
        }

        /// <summary>滚轮调音量：播放器获得焦点即可响应（打开与任意按下都会 Focus）。</summary>
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (loadFailed) return;
            ChangeVolume(e.Delta > 0 ? 5 : -5);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover >= 0) { hover = -1; Invalidate(); }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Escape: RequestClose(); return true;
                // 快捷键优先于挂起的单击：先取消挂起，避免延迟再触发一次暂停/播放
                case Keys.Left: clickTimer.Stop(); Prev(); return true;
                case Keys.Right: clickTimer.Stop(); Next(); return true;
                case Keys.Space: clickTimer.Stop(); TogglePlay(); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (host != null)
            {
                host.Bounds = VideoArea;
                ApplyHostRegion();
            }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) { StopPlayback(); ReleaseEngine(); }   // 关闭播放器即释放 mpv 内核，不常驻到进程退出
        }

        /// <summary>释放 mpv 内核与宿主窗口；下次播放由 EnsureEngine 惰性重建（mpvUnavailable 语义不变）。</summary>
        private void ReleaseEngine()
        {
            if (mpv != null) { try { mpv.Dispose(); } catch { } mpv = null; }
            if (host != null) { Controls.Remove(host); host.Dispose(); host = null; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopPlayback();
                clickTimer.Stop();
                pollTimer.Dispose();
                clickTimer.Dispose();
                if (mpv != null) { mpv.Dispose(); mpv = null; }
            }
            base.Dispose(disposing);
        }

        // ===== 绘制 =====

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Bg);

            LayoutChrome();
            DrawVideoCard(g);
            DrawTopBar(g);
            DrawPill(g);
            DrawInfoBar(g);
        }

        private void LayoutChrome()
        {
            // 顶栏：左侧返回，右侧为音量提示位（文件名/序号在底部信息栏）
            backRect = new Rectangle(ChromePad, 6, 76, TopBarH - 12);
            hintRect = new Rectangle(Math.Max(0, Width - ChromePad - 180), 0, 180, TopBarH);

            // 底部信息栏：平铺底栏，高度与侧边栏「新建」底栏一致
            int infoTop = Height - InfoBarH;
            infoBarRect = new Rectangle(0, infoTop, Width, InfoBarH);

            // 底部居中悬浮操作条：上一张/播放暂停/进度条/时间/下一张/音量滑块/静音
            int avail = Math.Max(0, Width - ChromePad * 2);
            int pillW = Math.Min(Math.Min(640, avail), Math.Max(360, avail - 24));
            pillRect = new Rectangle((Width - pillW) / 2, infoTop - CardGap - PillH, pillW, PillH);
            prevRect = new Rectangle(pillRect.X + 8, pillRect.Y, PillBtnW, PillH);
            playRect = new Rectangle(prevRect.Right, pillRect.Y, PillBtnW, PillH);
            volRect = new Rectangle(pillRect.Right - 8 - PillBtnW, pillRect.Y, PillBtnW, PillH);
            volSliderRect = new Rectangle(volRect.Left - 8 - VolSliderW, pillRect.Y + (PillH - 20) / 2,
                VolSliderW, 20);
            nextRect = new Rectangle(volSliderRect.Left - 8 - PillBtnW, pillRect.Y, PillBtnW, PillH);
            timeRect = new Rectangle(nextRect.Left - TimeColW, pillRect.Y, TimeColW, PillH);
            int seekX = playRect.Right + 10;
            seekRect = new Rectangle(seekX, pillRect.Y + PillH / 2 - 2,
                Math.Max(0, timeRect.Left - 10 - seekX), 4);

            // 视频卡片区与宿主边界共用同一 Geometry
            videoCardRect = VideoArea;
        }

        /// <summary>音量滑块命中：纵向放宽便于按住拖动，横向不含两侧按钮。</summary>
        private bool HitVolumeSlider(Point p)
        {
            if (volSliderRect.Width <= 0) return false;
            Rectangle r = volSliderRect;
            r.Inflate(0, 8);
            return r.Contains(p);
        }

        /// <summary>视频卡片：黑色圆角底 + 柔和投影；加载失败时在卡片内居中显示占位文案。</summary>
        private void DrawVideoCard(Graphics g)
        {
            if (videoCardRect.Width <= 0 || videoCardRect.Height <= 0) return;
            Draw.DrawShadow(g, videoCardRect, VideoRadius, 0.6f);
            using (SolidBrush b = new SolidBrush(VideoBg))
                Draw.FillRoundedRect(g, b, videoCardRect, VideoRadius);
            if (!loadFailed) return;
            using (SolidBrush b = new SolidBrush(TextDim))
                Draw.DrawStringCenter(g, failText, Theme.BodyFont, b, videoCardRect);
        }

        private void DrawTopBar(Graphics g)
        {
            // 返回：chevron + 文案，悬停/按下给浅色圆角底
            DrawButtonBg(g, 3, backRect);
            Draw.DrawIcon(g, UiIconKind.Back,
                new Rectangle(backRect.X + 6, backRect.Y + (backRect.Height - 20) / 2, 20, 20), Theme.IconColor);
            Rectangle textR = new Rectangle(backRect.X + 24, backRect.Y, backRect.Width - 24, backRect.Height);
            TextRenderer.DrawText(g, "返回", Theme.ButtonFont, textR, Theme.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            // 音量提示：原悬浮条上方的提示条会被视频宿主窗口遮挡，改在顶栏右侧短暂显示
            if (!muted && DateTime.UtcNow >= volUntil) return;
            string hint = muted ? "已静音" : "音量 " + volume + "%";
            TextRenderer.DrawText(g, hint, Theme.CaptionFont, hintRect, Theme.TextSecondary,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        /// <summary>底部信息栏：平铺底栏 + 顶部分隔线，左侧文件名、右侧当前/总数，与侧边栏底栏同风格。</summary>
        private void DrawInfoBar(Graphics g)
        {
            if (infoBarRect.Width <= 0 || infoBarRect.Height <= 0) return;
            using (Pen p = new Pen(Theme.Separator, 1))
                g.DrawLine(p, 12, infoBarRect.Y, infoBarRect.Width - 12, infoBarRect.Y);

            const int pad = 16;
            const int countW = 80;
            Rectangle countR = new Rectangle(infoBarRect.Right - pad - countW, infoBarRect.Y,
                countW, infoBarRect.Height);
            Rectangle nameR = new Rectangle(infoBarRect.X + pad, infoBarRect.Y,
                Math.Max(0, countR.Left - 12 - (infoBarRect.X + pad)), infoBarRect.Height);

            string name = paths.Count > 0 && index < paths.Count ? Path.GetFileName(paths[index]) : string.Empty;
            TextRenderer.DrawText(g, name, Theme.BodyFont, nameR, Theme.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                | TextFormatFlags.SingleLine);
            string count = (index + 1) + " / " + paths.Count;
            TextRenderer.DrawText(g, count, Theme.CaptionFont, countR, Theme.TextSecondary,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        private void DrawPill(Graphics g)
        {
            // 深色悬浮底：浅色背景上保持播放控件的对比度，投影拉开层次
            Draw.DrawShadow(g, pillRect, PillH / 2, 0.8f);
            using (SolidBrush b = new SolidBrush(Theme.TooltipBg))
                Draw.FillRoundedRect(g, b, pillRect, PillH / 2);

            bool single = paths.Count < 2;
            DrawPillBtn(g, prevRect, 0, single);
            DrawPlayPause(g);
            DrawSeek(g);
            DrawTime(g);
            DrawPillBtn(g, nextRect, 2, single);
            DrawVolumeSlider(g);
            DrawVolumeBtn(g);
        }

        /// <summary>音量滑块：细轨道 + 当前音量填充 + 白色圆钮；静音时填充归零，拖动或滚轮后实时反映。</summary>
        private void DrawVolumeSlider(Graphics g)
        {
            Rectangle r = volSliderRect;
            if (r.Width <= 0 || r.Height <= 0) return;
            int cy = r.Y + r.Height / 2;
            Rectangle track = new Rectangle(r.X, cy - 1, r.Width, 3);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
                Draw.FillRoundedRect(g, b, track, 2);
            int lvl = muted ? 0 : Math.Max(0, Math.Min(100, volume));
            int w = (int)Math.Round(r.Width * lvl / 100.0);
            if (w > 0)
            {
                Rectangle fill = new Rectangle(r.X, cy - 1, w, 3);
                using (SolidBrush b = new SolidBrush(TextWhite))
                    Draw.FillRoundedRect(g, b, fill, 2);
            }
            float kx = r.X + (float)Math.Round(r.Width * lvl / 100.0);
            using (SolidBrush b = new SolidBrush(TextWhite))
                g.FillEllipse(b, kx - 4F, cy - 4F, 8F, 8F);
        }

        /// <summary>静音按钮：喇叭主体 + 声波（静音时画 ×），与操作条其他按钮同风格。</summary>
        private void DrawVolumeBtn(Graphics g)
        {
            DrawPillButtonBg(g, 4, volRect);
            float cx = volRect.X + volRect.Width / 2F - 2;
            float cy = volRect.Y + volRect.Height / 2F;
            using (GraphicsPath sp = new GraphicsPath())
            {
                sp.AddLine(cx - 8, cy - 2.5F, cx - 5, cy - 2.5F);
                sp.AddLine(cx - 5, cy - 2.5F, cx - 1, cy - 6);
                sp.AddLine(cx - 1, cy - 6, cx - 1, cy + 6);
                sp.AddLine(cx - 1, cy + 6, cx - 5, cy + 2.5F);
                sp.AddLine(cx - 5, cy + 2.5F, cx - 8, cy + 2.5F);
                sp.CloseFigure();
                using (SolidBrush b = new SolidBrush(TextWhite)) g.FillPath(b, sp);
            }
            using (Pen pen = new Pen(TextWhite, 1.5F))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                if (muted)
                {
                    g.DrawLine(pen, cx + 3, cy - 4, cx + 8, cy + 4);
                    g.DrawLine(pen, cx + 8, cy - 4, cx + 3, cy + 4);
                }
                else
                {
                    g.DrawArc(pen, cx + 1, cy - 3, 5, 6, -55, 110);
                    g.DrawArc(pen, cx + 1, cy - 5, 8, 10, -55, 110);
                }
            }
        }

        /// <summary>悬浮条图标按钮：‹ › 前后切换；single 时前后切换弱化。</summary>
        private void DrawPillBtn(Graphics g, Rectangle r, int id, bool dim)
        {
            DrawPillButtonBg(g, id, r);
            Color c = dim ? Color.FromArgb(90, 255, 255, 255) : TextWhite;
            float cx = r.X + r.Width / 2F;
            float cy = r.Y + r.Height / 2F;
            using (Pen pen = new Pen(c, 1.8F))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                if (id == 0)   // ‹
                {
                    g.DrawLine(pen, cx + 3, cy - 6, cx - 3, cy);
                    g.DrawLine(pen, cx - 3, cy, cx + 3, cy + 6);
                }
                else            // ›
                {
                    g.DrawLine(pen, cx - 3, cy - 6, cx + 3, cy);
                    g.DrawLine(pen, cx + 3, cy, cx - 3, cy + 6);
                }
            }
        }

        /// <summary>播放/暂停：播放中双竖线，其余三角（播放结束回到三角，可重播）。</summary>
        private void DrawPlayPause(Graphics g)
        {
            DrawPillButtonBg(g, 1, playRect);
            float cx = playRect.X + playRect.Width / 2F;
            float cy = playRect.Y + playRect.Height / 2F;
            using (SolidBrush b = new SolidBrush(TextWhite))
            {
                if (playing)
                {
                    g.FillRectangle(b, cx - 5F, cy - 6F, 3.5F, 12F);
                    g.FillRectangle(b, cx + 1.5F, cy - 6F, 3.5F, 12F);
                }
                else
                {
                    using (GraphicsPath tri = new GraphicsPath())
                    {
                        tri.AddLine(cx - 4F, cy - 6F, cx - 4F, cy + 6F);
                        tri.AddLine(cx - 4F, cy + 6F, cx + 6F, cy);
                        tri.CloseFigure();
                        g.FillPath(b, tri);
                    }
                }
            }
        }

        /// <summary>进度条：浅色轨道 + 强调色播放进度 + 白色圆钮（时长未知时只画轨道）。</summary>
        private void DrawSeek(Graphics g)
        {
            using (SolidBrush track = new SolidBrush(Color.FromArgb(60, 255, 255, 255)))
                Draw.FillRoundedRect(g, track, seekRect, 2);
            if (duration > 0)
            {
                int w = (int)Math.Round(seekRect.Width * (position / duration));
                Rectangle fill = new Rectangle(seekRect.X, seekRect.Y, Math.Max(2, Math.Min(w, seekRect.Width)), seekRect.Height);
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    Draw.FillRoundedRect(g, b, fill, 2);
                float kx = fill.Right;
                float ky = seekRect.Y + seekRect.Height / 2F;
                using (SolidBrush b = new SolidBrush(TextWhite))
                    g.FillEllipse(b, kx - 5F, ky - 5F, 10F, 10F);
            }
        }

        /// <summary>时间列：当前 / 总时长，右侧对齐。</summary>
        private void DrawTime(Graphics g)
        {
            string text = FmtTime(position) + " / " + FmtTime(duration);
            TextRenderer.DrawText(g, text, Theme.CaptionFont, timeRect, TextWhite,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        private static string FmtTime(double sec)
        {
            if (sec < 0 || double.IsNaN(sec) || double.IsInfinity(sec)) sec = 0;
            int s = (int)Math.Floor(sec);
            int h = s / 3600;
            int m = (s % 3600) / 60;
            int ss = s % 60;
            return h > 0
                ? h + ":" + m.ToString("00") + ":" + ss.ToString("00")
                : m + ":" + ss.ToString("00");
        }

        /// <summary>浅色顶栏按钮悬停/按下底色：叠加黑色透明度，无描边（与侧边栏悬停一致）。</summary>
        private void DrawButtonBg(Graphics g, int id, Rectangle r)
        {
            if (press != id && hover != id) return;
            Color fill = press == id ? Color.FromArgb(26, 0, 0, 0) : Color.FromArgb(12, 0, 0, 0);
            using (SolidBrush b = new SolidBrush(fill))
                Draw.FillRoundedRect(g, b, r, 6);
        }

        /// <summary>深色悬浮条按钮悬停/按下底色：叠加白色透明度，无描边。</summary>
        private void DrawPillButtonBg(Graphics g, int id, Rectangle r)
        {
            if (press != id && hover != id) return;
            Color fill = press == id ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(34, 255, 255, 255);
            using (SolidBrush b = new SolidBrush(fill))
                Draw.FillRoundedRect(g, b, r, 10);
        }

        // ===== Win32 辅助 =====

        private const int GWL_STYLE = -16;
        private const int GW_CHILD = 5;
        private const int GW_HWNDNEXT = 2;
        private const int WS_DISABLED = 0x08000000;

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        /// <summary>
        /// mpv 渲染宿主：画面由 mpv 的 win32 VO 直接绘制，本控件不参与内容绘制；
        /// 禁用输入让鼠标消息穿透给父控件，保证播放器的单击/双击/滚轮逻辑统一。
        /// </summary>
        private sealed class VideoHost : Control
        {
            public VideoHost()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.Opaque
                    | ControlStyles.AllPaintingInWmPaint, true);
                SetStyle(ControlStyles.Selectable, false);
                BackColor = Color.Black;
                TabStop = false;
                Enabled = false;
            }

            /// <summary>mpv 要求宿主带 WS_CLIPCHILDREN，避免自绘与视频画面互相覆盖。</summary>
            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.Style |= 0x02000000;   // WS_CLIPCHILDREN
                    return cp;
                }
            }

            protected override void OnPaintBackground(PaintEventArgs e) { }
            protected override void OnPaint(PaintEventArgs e) { }
        }

        /// <summary>
        /// libmpv 最小封装：覆盖播放所需能力（加载文件、暂停、跳转、音量、时长/位置、加载/失败事件）。
        /// 属性与命令名均为 ASCII；文件路径等文本手工转 UTF-8，避免系统代码页对中文路径的破坏。
        /// </summary>
        private sealed class MpvEngine : IDisposable
        {
            private const string Dll = "libmpv-2.dll";
            private const int FormatFlag = 3;
            private const int FormatDouble = 5;
            private const int EventEndFile = 7;
            private const int EventFileLoaded = 8;
            private const int EndFileReasonError = 4;

            private IntPtr ctx;

            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
            private static extern IntPtr mpv_create();
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            private static extern int mpv_initialize(IntPtr ctx);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            private static extern int mpv_set_option_string(IntPtr ctx, string name, string value);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            private static extern int mpv_set_property_string(IntPtr ctx, string name, string value);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property", CharSet = CharSet.Ansi)]
            private static extern int mpv_get_property_double(IntPtr ctx, string name, int format, ref double data);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property", CharSet = CharSet.Ansi)]
            private static extern int mpv_get_property_flag(IntPtr ctx, string name, int format, ref int data);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
            private static extern int mpv_command(IntPtr ctx, IntPtr args);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
            private static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
            private static extern int mpv_request_log_messages(IntPtr ctx, string minLevel);
            [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
            private static extern void mpv_terminate_destroy(IntPtr ctx);

            [StructLayout(LayoutKind.Sequential)]
            private struct MpvEvent
            {
                public int EventId;
                public int Error;
                public ulong ReplyUserdata;
                public IntPtr Data;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct MpvEventEndFile
            {
                public int Reason;
                public int Error;
                public long PlaylistEntryId;
                public long PlaylistInsertId;
                public int PlaylistInsertNumEntries;
            }

            /// <summary>创建并初始化内核；wid 为渲染宿主窗口句柄。失败时抛异常（DLL 缺失/选项或初始化失败）。</summary>
            public MpvEngine(IntPtr wid)
            {
                // vo=gpu 在部分显卡驱动/远程桌面下初始化失败：按候选顺序回退渲染后端
                string[] voCandidates = { "gpu", "win" };
                Exception last = null;
                for (int vi = 0; vi < voCandidates.Length; vi++)
                {
                    ctx = mpv_create();
                    if (ctx == IntPtr.Zero) { last = new InvalidOperationException("mpv_create 失败"); continue; }
                    try
                    {
                        // 绑定渲染窗口（必须在 initialize 之前设置），并关闭所有自带交互/界面：
                        // 鼠标键盘输入全部由本控件接管，OSC/OSD 不显示
                        if (SetOption("wid", wid.ToInt64().ToString(CultureInfo.InvariantCulture)) < 0)
                            throw new InvalidOperationException("mpv 绑定渲染窗口失败");
                        SetOption("vo", voCandidates[vi]);
                        SetOption("hwdec", "auto-safe");
                        SetOption("keep-open", "yes");
                        SetOption("idle", "yes");
                        SetOption("terminal", "no");
                        SetOption("config", "no");
                        SetOption("osc", "no");
                        SetOption("osd-level", "0");
                        SetOption("input-default-bindings", "no");
                        SetOption("input-vo-keyboard", "no");
                        SetOption("input-cursor", "no");
                        SetOption("input-media-keys", "no");
                        int r = mpv_initialize(ctx);
                        if (r < 0) throw new InvalidOperationException("mpv_initialize 失败(vo=" + voCandidates[vi] + "): " + r);
                        mpv_request_log_messages(ctx, "no");
                        return;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        mpv_terminate_destroy(ctx);
                        ctx = IntPtr.Zero;
                    }
                }
                throw last ?? new InvalidOperationException("mpv 初始化失败");
            }

            public void LoadFile(string path) { Command("loadfile", path); }
            public void SetPause(bool paused) { SetString("pause", paused ? "yes" : "no"); }
            public void SetVolume(int volume)
            {
                SetString("volume", volume.ToString(CultureInfo.InvariantCulture));
            }
            public void SetMute(bool mute) { SetString("mute", mute ? "yes" : "no"); }
            public void Seek(double seconds)
            {
                Command("seek", seconds.ToString("0.###", CultureInfo.InvariantCulture), "absolute");
            }
            public void Stop() { Command("stop"); }

            public bool TryGetDouble(string name, out double value)
            {
                value = 0;
                return ctx != IntPtr.Zero && mpv_get_property_double(ctx, name, FormatDouble, ref value) >= 0;
            }

            public bool TryGetFlag(string name, out bool value)
            {
                value = false;
                if (ctx == IntPtr.Zero) return false;
                int raw = 0;
                if (mpv_get_property_flag(ctx, name, FormatFlag, ref raw) < 0) return false;
                value = raw != 0;
                return true;
            }

            /// <summary>排空事件队列：识别文件加载完成与解码失败（END_FILE reason=ERROR）。</summary>
            public void DrainEvents(ref bool fileLoaded, ref bool error)
            {
                if (ctx == IntPtr.Zero) return;
                for (int i = 0; i < 128; i++)
                {
                    IntPtr evp = mpv_wait_event(ctx, 0);
                    if (evp == IntPtr.Zero) return;
                    MpvEvent ev = (MpvEvent)Marshal.PtrToStructure(evp, typeof(MpvEvent));
                    if (ev.EventId == 0) return;   // MPV_EVENT_NONE：队列已空
                    if (ev.EventId == EventFileLoaded) fileLoaded = true;
                    else if (ev.EventId == EventEndFile && ev.Data != IntPtr.Zero)
                    {
                        MpvEventEndFile ef = (MpvEventEndFile)Marshal.PtrToStructure(ev.Data, typeof(MpvEventEndFile));
                        if (ef.Reason == EndFileReasonError) error = true;
                    }
                }
            }

            public void Dispose()
            {
                if (ctx == IntPtr.Zero) return;
                mpv_terminate_destroy(ctx);
                ctx = IntPtr.Zero;
            }

            private void SetString(string name, string value)
            {
                if (ctx == IntPtr.Zero) return;
                mpv_set_property_string(ctx, name, value);
            }

            private int SetOption(string name, string value)
            {
                return mpv_set_option_string(ctx, name, value);
            }

            /// <summary>执行命令；参数按 UTF-8 编组（mpv 在 Windows 上要求 UTF-8 路径）。</summary>
            private int Command(params string[] args)
            {
                if (ctx == IntPtr.Zero) return -1;
                IntPtr[] ptrs = new IntPtr[args.Length + 1];
                try
                {
                    for (int i = 0; i < args.Length; i++) ptrs[i] = AllocUtf8(args[i]);
                    IntPtr argv = Marshal.AllocHGlobal(IntPtr.Size * ptrs.Length);
                    try
                    {
                        Marshal.Copy(ptrs, 0, argv, ptrs.Length);
                        return mpv_command(ctx, argv);
                    }
                    finally { Marshal.FreeHGlobal(argv); }
                }
                finally
                {
                    for (int i = 0; i < args.Length; i++)
                        if (ptrs[i] != IntPtr.Zero) Marshal.FreeHGlobal(ptrs[i]);
                }
            }

            private static IntPtr AllocUtf8(string text)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                IntPtr p = Marshal.AllocHGlobal(bytes.Length + 1);
                Marshal.Copy(bytes, 0, p, bytes.Length);
                Marshal.WriteByte(p, bytes.Length, 0);
                return p;
            }
        }
    }
}
