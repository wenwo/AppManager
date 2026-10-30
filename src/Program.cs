using System;
using System.Collections.Generic;
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
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs te) { Paths.LogError(te.Exception); };
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
                    using (OpenFileDialog recovery = new OpenFileDialog { Filter = "数据库备份 (*.db)|*.db" })
                    {
                        if (recovery.ShowDialog() != DialogResult.OK) return 1;
                        try { DatabaseRecovery.Restore(ConfigDatabase.DatabasePath(Paths.ConfigFile), recovery.FileName); }
                        catch (Exception ex) { Paths.LogError(ex); MessageBox.Show(ex.Message, "恢复失败", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
                    }
                    config = AppConfig.Load();
                    if (config.LoadFailed) return 1;
                }
                Application.Run(new MainForm(config));
            }
            return 0;
        }

        /// <summary>
        /// 命令行「整理桌面」：与 GUI 同一逻辑（收纳 + 保证桌面保留「应用中心」入口），
        /// 结果写入 tidy.log；成功返回 0，异常返回 1。
        /// </summary>
        private static int RunTidyDesktop()
        {
            int count = 0;
            string error = null;
            try
            {
                AppConfig config = AppConfig.Load();
                DesktopOrganizer organizer = new DesktopOrganizer(config);
                count = organizer.TidyDesktop(Application.ExecutablePath);
            }
            catch (Exception ex) { error = ex.Message; }
            WriteTidyLog(count, error);
            return error == null ? 0 : 1;
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
            try
            {
                IntPtr h = FindWindow(null, MainFormTitle);
                if (h == IntPtr.Zero) return;
                if (!IsWindowVisible(h)) ShowWindow(h, SW_RESTORE);
                SetForegroundWindow(h);
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
    }
}
