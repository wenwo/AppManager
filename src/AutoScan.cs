using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace AppManager
{
    /// <summary>
    /// 自动扫描：监听开始菜单与桌面目录的新增 / 改名 / 删除，
    /// 静默片刻后合并成一次通知，由主窗口触发静默重扫。
    /// 安装程序通常连续创建多个快捷方式，防抖可以避免扫描被打断多次。
    /// </summary>
    internal sealed class AutoScanWatcher : IDisposable
    {
        /// <summary>最后一条变更之后等待的静默时长。</summary>
        private const int DebounceMs = 1200;

        private readonly Control _ui;
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private readonly Timer _debounce;
        private readonly List<string> _newDesktopFiles = new List<string>();
        private readonly Dictionary<string, DateTime> _ignoredDeletions =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly object _ignoreLock = new object();
        private bool _disposed;

        /// <summary>防抖后触发一次：表示「开始菜单或桌面发生了变化」。</summary>
        public event EventHandler ChangesDetected;

        public AutoScanWatcher(Control ui)
        {
            _ui = ui;

            _debounce = new Timer();
            _debounce.Interval = DebounceMs;
            _debounce.Tick += OnDebounceTick;

            Watch(DesktopOrganizer.UserDesktop);
            Watch(DesktopOrganizer.PublicDesktop);
            Watch(StartMenuPrograms(true));
            Watch(StartMenuPrograms(false));
        }

        private static string StartMenuPrograms(bool common)
        {
            string root = Environment.GetFolderPath(common
                ? Environment.SpecialFolder.CommonStartMenu
                : Environment.SpecialFolder.StartMenu);
            if (string.IsNullOrEmpty(root)) { return string.Empty; }
            return Path.Combine(root, "Programs");
        }

        private void Watch(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { return; }
            try
            {
                FileSystemWatcher w = new FileSystemWatcher(dir);
                w.IncludeSubdirectories = true;
                w.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName;
                // 默认 4KB 缓冲在批量安装时容易溢出（丢事件），适当放大
                w.InternalBufferSize = 16 * 1024;
                w.Created += OnCreated;
                w.Renamed += OnRenamed;
                w.Deleted += OnDeleted;
                w.Error += OnWatcherError;
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex)
            {
                Paths.LogError(ex);
            }
        }

        /// <summary>取走桌面上新出现、待自动收纳的快捷方式（取完即清空）。</summary>
        public List<string> TakeNewDesktopFiles()
        {
            List<string> result = new List<string>(_newDesktopFiles);
            _newDesktopFiles.Clear();
            return result;
        }

        /// <summary>
        /// 程序自己把桌面快捷方式搬进收纳目录后，随之而来的「删除」事件不必再触发重扫。
        /// 需在搬运之前调用，否则事件可能先到。
        /// </summary>
        public void IgnoreDeletions(List<string> paths)
        {
            if (paths == null || paths.Count == 0) { return; }
            DateTime until = DateTime.UtcNow.AddSeconds(10);
            lock (_ignoreLock)
            {
                foreach (string path in paths)
                {
                    if (!string.IsNullOrEmpty(path)) { _ignoredDeletions[path] = until; }
                }
            }
        }

        private bool ConsumeIgnoredDeletion(string path)
        {
            if (string.IsNullOrEmpty(path)) { return false; }
            lock (_ignoreLock)
            {
                DateTime until;
                if (!_ignoredDeletions.TryGetValue(path, out until)) { return false; }
                _ignoredDeletions.Remove(path);
                return DateTime.UtcNow <= until;
            }
        }

        // ---------- 文件系统事件（线程池线程）→ 统一切回界面线程 ----------

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            Post(e.FullPath, true);
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            Post(e.FullPath, true);
        }

        private void OnDeleted(object sender, FileSystemEventArgs e)
        {
            if (ConsumeIgnoredDeletion(e.FullPath)) { return; }
            Post(string.Empty, false);
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            // 缓冲区溢出会漏事件，直接安排一次重扫兜底
            Post(string.Empty, false);
        }

        private void Post(string path, bool maybeDesktopShortcut)
        {
            if (_disposed) { return; }
            try
            {
                // 窗口句柄没了（正在关闭）就不再排队，避免在非界面线程动 WinForms 计时器
                if (_ui == null || !_ui.IsHandleCreated) { return; }
                _ui.BeginInvoke((MethodInvoker)delegate { Enqueue(path, maybeDesktopShortcut); });
            }
            catch
            {
                // 窗口正在关闭：忽略即可
            }
        }

        private void Enqueue(string path, bool maybeDesktopShortcut)
        {
            if (_disposed) { return; }
            try
            {
                if (maybeDesktopShortcut && IsDesktopShortcut(path))
                {
                    if (!_newDesktopFiles.Contains(path)) { _newDesktopFiles.Add(path); }
                }
                _debounce.Stop();
                _debounce.Start();
            }
            catch
            {
                // 计时器已随窗口释放：忽略
            }
        }

        /// <summary>是否位于用户桌面 / 公共桌面下的快捷方式（本程序自身入口不算）。</summary>
        private static bool IsDesktopShortcut(string path)
        {
            if (string.IsNullOrEmpty(path)) { return false; }
            if (!DesktopOrganizer.IsShortcutFile(path)) { return false; }

            string[] roots = new string[] { DesktopOrganizer.UserDesktop, DesktopOrganizer.PublicDesktop };
            foreach (string root in roots)
            {
                if (!string.IsNullOrEmpty(root)
                    && path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    return !DesktopOrganizer.IsSelfShortcut(path);
                }
            }
            return false;
        }

        private void OnDebounceTick(object sender, EventArgs e)
        {
            _debounce.Stop();
            if (_disposed) { return; }
            EventHandler handler = ChangesDetected;
            if (handler != null) { handler(this, EventArgs.Empty); }
        }

        public void Dispose()
        {
            if (_disposed) { return; }
            _disposed = true;

            _debounce.Stop();
            _debounce.Tick -= OnDebounceTick;
            _debounce.Dispose();

            foreach (FileSystemWatcher w in _watchers)
            {
                try
                {
                    w.EnableRaisingEvents = false;
                    w.Dispose();
                }
                catch { }
            }
            _watchers.Clear();
            _newDesktopFiles.Clear();
            lock (_ignoreLock) { _ignoredDeletions.Clear(); }
        }
    }
}
