using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.VisualBasic.FileIO;

namespace AppManager
{
    /// <summary>
    /// 应用启动与卸载入口：封装进程启动、目录打开、卸载调用。
    /// 所有方法吞并异常并返回布尔，调用方据此提示用户。
    /// </summary>
    internal static class Launcher
    {
        private static readonly OpenWithQueue openWithQueue = new OpenWithQueue(ShowOpenWithDialog, delegate
        {
            MessageBox.Show("无法打开文件，请检查文件是否存在或重新选择查看器。", "外部打开", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        });

        static Launcher()
        {
            Application.ApplicationExit += delegate { openWithQueue.Dispose(); };
        }

        /// <summary>启动应用；UWP 走快捷方式 shell 解析，其余走目标/快捷方式（浏览器网址等由系统关联程序打开）。</summary>
        public static bool Launch(AppInfo app)
        {
            if (app == null) return false;
            try
            {
                if (app.IsUwp)
                {
                    if (!string.IsNullOrEmpty(app.ShortcutPath))
                    {
                        Process.Start(new ProcessStartInfo(app.ShortcutPath) { UseShellExecute = true });
                        return true;
                    }
                    // 打包/UWP 应用：以 shell:AppsFolder\<AUMID> 定位串经 explorer 启动
                    if (Scanner.IsAppsFolderTarget(app.TargetPath))
                    {
                        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + app.TargetPath + "\"")
                        { UseShellExecute = true });
                        return true;
                    }
                    return false;
                }
                string start = !string.IsNullOrEmpty(app.ShortcutPath) ? app.ShortcutPath : app.TargetPath;
                if (string.IsNullOrEmpty(start)) return false;
                ProcessStartInfo psi = new ProcessStartInfo(start);
                psi.UseShellExecute = true;
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("启动失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>打开安装目录（或目标所在目录）。</summary>
        public static bool OpenInstallDirectory(AppInfo app)
        {
            string dir = ResolveDirectory(app);
            return OpenFolder(dir);
        }

        public static bool OpenFolder(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            try
            {
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("打开目录失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>打开目标文件本身（资源管理器）。</summary>
        public static bool OpenFile(AppInfo app)
        {
            string file = ResolveTargetFile(app);
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return false;
            try
            {
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("打开文件失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>排队显示 Windows“打开方式”；同时仅一个，取消时停止后续文件。</summary>
        public static bool OpenWith(string file)
        {
            if (string.IsNullOrEmpty(file)) return false;
            try
            {
                return openWithQueue.Enqueue(Path.GetFullPath(file));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("打开方式选择器启动失败: " + ex.Message);
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenAsInfo
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string File;
            [MarshalAs(UnmanagedType.LPWStr)] public string Class;
            public uint Flags;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHOpenWithDialog(IntPtr owner, ref OpenAsInfo info);
        [DllImport("ole32.dll")]
        private static extern int OleInitialize(IntPtr reserved);
        [DllImport("ole32.dll")]
        private static extern void OleUninitialize();

        private static bool ShowOpenWithDialog(string path)
        {
            Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero));
            try
            {
                OpenAsInfo info = new OpenAsInfo { File = path, Flags = 0x4 }; // OAIF_EXEC
                // No main-window owner: the shell dialog must not disable the application's UI.
                int result = SHOpenWithDialog(IntPtr.Zero, ref info);
                if (result == unchecked((int)0x800704C7) || result == unchecked((int)0x80004004) || result == 1)
                    return false;
                Marshal.ThrowExceptionForHR(result);
                return result == 0;
            }
            finally { OleUninitialize(); }
        }

        /// <summary>打开目标文件所在位置（资源管理器选中）。</summary>
        public static bool OpenFileLocation(AppInfo app)
        {
            string file = ResolveTargetFile(app);
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return OpenInstallDirectory(app);
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + file + "\""));
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("打开所在位置失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>调用卸载入口；解析常见 UninstallString 形态。</summary>
        public static bool Uninstall(AppInfo app)
        {
            if (app == null || string.IsNullOrEmpty(app.UninstallString)) return false;
            try
            {
                string u = app.UninstallString.Trim();
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.UseShellExecute = true;
                if (u.StartsWith("msiexec", StringComparison.OrdinalIgnoreCase))
                {
                    int sp = u.IndexOf(' ');
                    psi.FileName = "msiexec.exe";
                    psi.Arguments = sp >= 0 ? u.Substring(sp + 1) : string.Empty;
                }
                else if (u.StartsWith("\""))
                {
                    int end = u.IndexOf('"', 1);
                    if (end > 0)
                    {
                        psi.FileName = u.Substring(1, end - 1);
                        psi.Arguments = u.Substring(end + 1).Trim();
                    }
                    else
                    {
                        psi.FileName = u.Trim('"');
                    }
                }
                else
                {
                    // 未加引号且路径含空格时，优先按 .exe 边界切分，避免把路径在空格处截断
                    int exeEnd = ExeBoundaryEnd(u);
                    if (exeEnd > 0)
                    {
                        psi.FileName = u.Substring(0, exeEnd);
                        psi.Arguments = u.Substring(exeEnd).Trim();
                    }
                    else
                    {
                        int sp = u.IndexOf(' ');
                        if (sp > 0)
                        {
                            psi.FileName = u.Substring(0, sp);
                            psi.Arguments = u.Substring(sp + 1);
                        }
                        else
                        {
                            psi.FileName = u;
                        }
                    }
                }
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("卸载失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>未加引号命令行中 .exe 的结束下标（其后为空白或结尾）；未找到返回 -1。</summary>
        private static int ExeBoundaryEnd(string s)
        {
            int at = 0;
            for (;;)
            {
                int hit = s.IndexOf(".exe", at, StringComparison.OrdinalIgnoreCase);
                if (hit < 0) return -1;
                int end = hit + 4;
                if (end >= s.Length || s[end] == ' ' || s[end] == '\t') return end;
                at = end;
            }
        }

        // ===== 删除 =====

        /// <summary>删除文件/目录：toRecycle 时免确认进回收站（可恢复），否则永久删除。失败返回 false。
        /// 采用 FileIO 官方封装，正常路径无弹层；出错时弹系统错误框，异常同时上抛给调用方提示。</summary>
        public static bool DeletePath(string path, bool toRecycle)
        {
            if (string.IsNullOrEmpty(path)) return false;
            bool isDir = Directory.Exists(path);
            if (!isDir && !File.Exists(path)) return false;
            try
            {
                RecycleOption mode = toRecycle ? RecycleOption.SendToRecycleBin : RecycleOption.DeletePermanently;
                if (isDir) FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, mode);
                else FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, mode);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("删除失败 " + path + ": " + ex.Message);
                return false;
            }
        }

        // ===== 解析辅助 =====

        private static string ResolveTargetFile(AppInfo app)
        {
            if (app == null) return string.Empty;
            if (!string.IsNullOrEmpty(app.TargetPath) && File.Exists(app.TargetPath)) return app.TargetPath;
            if (!string.IsNullOrEmpty(app.ShortcutPath) && File.Exists(app.ShortcutPath)) return app.ShortcutPath;
            return string.Empty;
        }

        private static string ResolveDirectory(AppInfo app)
        {
            if (app == null) return string.Empty;
            if (!string.IsNullOrEmpty(app.InstallLocation) && Directory.Exists(app.InstallLocation))
                return app.InstallLocation;
            string t = ResolveTargetFile(app);
            if (!string.IsNullOrEmpty(t) && File.Exists(t)) return Path.GetDirectoryName(t);
            return string.Empty;
        }
    }
}
