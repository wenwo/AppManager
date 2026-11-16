using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AppManager
{
    /// <summary>
    /// 程序入口：--selftest 走自测并返回通过数；--tidy-desktop 命令行整理桌面；
    /// 否则单实例校验后加载配置并启动主窗体。
    /// </summary>
    internal static class Program
    {
        private const string MutexName = "AppManager.SingleInstance.9F2E1C70";
        private const string MainFormTitle = "应用中心";
        private static string InstancePidFile { get { return Path.Combine(Path.GetTempPath(), MutexName + ".pid"); } }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_RESTORE = 9;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
                AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            }
            catch { }

            if (args != null && args.Length > 0 && args[0] == "--selftest")
            {
                int passed = SelfTest.Run();
                // 退出码约定：全部用例通过返回 0，存在失败返回 1
                return passed == SelfTest.TotalCount ? 0 : 1;
            }

            if (args != null && args.Length > 0 && args[0] == "--tidy-desktop")
            {
                return RunTidyDesktop();
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    ActivateExistingInstance();
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // 全局异常兜底：UI 线程与后台线程异常都写入 error.log，避免无声崩溃
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs te)
                {
                    Paths.LogError(te.Exception);
                    Action hook = uiErrorHook;
                    if (hook != null) hook();
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs ue)
                {
                    Exception err = ue.ExceptionObject as Exception;
                    if (err != null) Paths.LogError(err);
                };
                AppConfig config = AppConfig.Load();
                if (config.LoadFailed)
                {
                    if (MessageBox.Show("数据读取失败，原数据已保留。\n" + config.DatabaseError + "\n\n是否选择数据库完整备份恢复？", "应用中心",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return 1;
                    string backupFile = PickRecoveryBackup();
                    if (backupFile == null) return 1;
                    try { DatabaseRecovery.Restore(ConfigDatabase.DatabasePath(Paths.ConfigFile), backupFile); }
                    catch (Exception ex) { Paths.LogError(ex); MessageBox.Show(ex.Message, "恢复失败", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
                    config = AppConfig.Load();
                    if (config.LoadFailed) return 1;
                }
                WriteInstancePid();
                try { Application.Run(new MainForm(config)); }
                finally { RemoveInstancePid(); }
            }
            return 0;
        }

        /// <summary>
        /// 界面线程未处理异常的附加通知（全局处理已记日志后调用）。Application.ThreadException 再次订阅会替换掉
        /// 原有处理而不是叠加，所以需要临时感知异常的地方挂在这里。
        /// </summary>
        private static Action uiErrorHook;

        /// <summary>
        /// 启动时数据库读不出来：选一个完整备份来恢复，取消返回 null。主窗口还没打开，先用程序内的选择窗口；
        /// 它打开失败、或运行中出现未处理异常时，记日志、关掉它并退回系统的打开窗口，保证这条最后的恢复入口一定能用。
        /// </summary>
        internal static string PickRecoveryBackup()
        {
            bool failed = false;
            PathPickerDialog dialog = null;
            uiErrorHook = delegate
            {
                failed = true;
                if (dialog != null && !dialog.IsDisposed) dialog.Close();
            };
            try
            {
                using (dialog = new PathPickerDialog(new PathPickerOptions
                {
                    Title = "选择数据库备份",
                    Hint = "展开文件夹，选中之前「完整备份」得到的 .db 文件；恢复前会保留一份当前数据。",
                    Mode = PathPickerMode.OpenFile,
                    ConfirmText = "恢复",
                    Extensions = new string[] { ".db" }
                }))
                {
                    // 没有主窗口可依附：给它任务栏按钮并在屏幕居中，切到别的程序后还能找回来
                    dialog.ShowInTaskbar = true;
                    dialog.StartPosition = FormStartPosition.CenterScreen;
                    dialog.ShowDialog();
                    if (!failed) return dialog.DialogResult == DialogResult.OK ? dialog.ResultPath : null;
                }
            }
            catch (Exception ex) { Paths.LogError(ex); }
            finally { uiErrorHook = null; }
            using (OpenFileDialog recovery = new OpenFileDialog { Filter = "数据库备份 (*.db)|*.db" })
                return recovery.ShowDialog() == DialogResult.OK ? recovery.FileName : null;
        }

        /// <summary>
        /// 命令行「整理桌面」：与 GUI 同一逻辑（收纳 + 保证桌面保留「应用中心」入口），
        /// 结果写入 tidy.log；成功返回 0，异常返回 1。
        /// </summary>
        private static int RunTidyDesktop()
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // 与 GUI 共用单实例锁：两边同时写数据库会丢收纳记录，或让界面后续保存持续失败。
                    WriteTidyLog(0, "主程序正在运行，已跳过命令行整理，避免同时修改数据库。");
                    return 1;
                }
                try
                {
                    int count = 0;
                    string error = null;
                    try
                    {
                        AppConfig config = AppConfig.Load();
                        if (config.LoadFailed) throw new IOException("配置读取失败，已停止整理：" + config.DatabaseError);
                        DesktopOrganizer organizer = new DesktopOrganizer(config);
                        count = organizer.TidyDesktop(Application.ExecutablePath);
                    }
                    catch (Exception ex) { error = ex.Message; }
                    WriteTidyLog(count, error);
                    return error == null ? 0 : 1;
                }
                finally
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }

        /// <summary>把整理结果写入 %LocalAppData%\AppManager\tidy.log（静默，不抛异常）。</summary>
        private static void WriteTidyLog(int count, string error)
        {
            try
            {
                string dir = Paths.DataDir;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string line = error == null
                    ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " 整理完成，收纳 " + count + " 项"
                    : DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " 整理失败：" + error;
                File.WriteAllText(Path.Combine(dir, "tidy.log"), line, new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>重复启动时把已有窗口恢复到前台，不再新开进程。</summary>
        private static void ActivateExistingInstance()
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                // 通过 PID 文件定位首个实例，避免窗口标题变化（本地化/用户改名）后失去激活入口。
                int pid;
                if (File.Exists(InstancePidFile)
                    && int.TryParse(File.ReadAllText(InstancePidFile).Trim(), out pid))
                {
                    string processName = Process.GetCurrentProcess().ProcessName;
                    for (int attempt = 0; attempt < 20 && h == IntPtr.Zero; attempt++)
                    {
                        try
                        {
                            using (Process process = Process.GetProcessById(pid))
                            {
                                if (!string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) break;
                                process.Refresh();
                                h = process.MainWindowHandle;
                            }
                        }
                        catch (ArgumentException) { break; }   // 进程已退出
                        if (h == IntPtr.Zero) Thread.Sleep(100);   // 首个实例可能还在创建主窗口
                    }
                }
            }
            catch { }
            if (h == IntPtr.Zero) h = FindWindow(null, MainFormTitle);   // 旧版本没有 PID 文件
            if (h == IntPtr.Zero) return;
            if (!IsWindowVisible(h)) ShowWindow(h, SW_RESTORE);
            SetForegroundWindow(h);
        }

        private static void WriteInstancePid()
        {
            try { File.WriteAllText(InstancePidFile, Process.GetCurrentProcess().Id.ToString()); }
            catch { }
        }

        private static void RemoveInstancePid()
        {
            try
            {
                if (!File.Exists(InstancePidFile)) return;
                if (File.ReadAllText(InstancePidFile).Trim() == Process.GetCurrentProcess().Id.ToString())
                    File.Delete(InstancePidFile);
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
    }
}
