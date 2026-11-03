using System;
using System.Threading;

namespace AppManager.Controls
{
    /// <summary>
    /// mpv 内核在后台线程销毁：mpv_terminate_destroy 要等解码、音频设备和渲染线程全部收尾，
    /// 放在界面线程会让关闭播放器、切换视图时卡顿。libmpv 允许从任意线程销毁实例。
    /// 调用方先把字段置空再交给这里，界面线程之后不会再碰这个实例。
    /// </summary>
    internal static class MpvShutdown
    {
        private static readonly object Sync = new object();
        private static readonly ManualResetEvent Idle = new ManualResetEvent(true);
        private static int pending;

        /// <summary>正在后台销毁的实例数（供测试观察）。</summary>
        internal static int Pending { get { lock (Sync) return pending; } }

        /// <summary>
        /// 后台销毁 engine；destroyed 在销毁完成后于后台线程回调（例如回到界面线程释放渲染宿主窗口）。
        /// engine 为空时 destroyed 立即在调用线程执行。
        /// </summary>
        internal static void Destroy(IDisposable engine, Action destroyed)
        {
            if (engine == null) { if (destroyed != null) destroyed(); return; }
            lock (Sync) { if (pending++ == 0) Idle.Reset(); }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { engine.Dispose(); }
                catch (Exception ex) { Paths.LogError(new InvalidOperationException("后台销毁播放内核失败", ex)); }
                finally
                {
                    lock (Sync) { if (--pending == 0) Idle.Set(); }
                    if (destroyed != null)
                        try { destroyed(); }
                        catch (Exception ex) { Paths.LogError(ex); }
                }
            });
        }

        /// <summary>
        /// 等待全部后台销毁结束（控件释放、程序退出时调用），超时返回 false 并记录日志：
        /// 渲染宿主窗口必须活到内核销毁之后，退出也不能无限等待。
        /// </summary>
        internal static bool WaitAll(int milliseconds)
        {
            if (Idle.WaitOne(milliseconds)) return true;
            Paths.LogError(new TimeoutException("播放内核 " + milliseconds + "ms 内未销毁完毕，剩余 " + Pending + " 个"));
            return false;
        }
    }
}
