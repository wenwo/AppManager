using System;

namespace AppManager
{
    /// <summary>
    /// 自动分类规则：9 大内置分类 + 关键词匹配，未命中归为"其他"。
    /// 关键词采用小写包含匹配，前序分类优先级高于后序。
    /// </summary>
    internal static class CategoryRules
    {
        /// <summary>内置 9 大分类，末位为默认兜底"其他"。</summary>
        public static readonly string[] BuiltIn = new string[]
        {
            "开发工具", "系统工具", "办公软件", "浏览器", "通讯社交",
            "媒体娱乐", "图像设计", "网络工具", "其他"
        };

        /// <summary>资源模式内置分类（照片/视频/音乐/文档/证件/压缩包/文件）。</summary>
        public static readonly string[] ResourceBuiltIn = new string[]
        {
            "照片", "视频", "音乐", "文档", "证件", "压缩包", "文件"
        };

        /// <summary>各分类关键词，与 BuiltIn 一一对应（"其他"无关键词，仅兜底）。</summary>
        private static readonly string[][] Keywords = new string[][]
        {
            new string[] { "visual studio", "vscode", "vs code", "intellij", "pycharm", "webstorm", "rider", "goland", "clion", "phpstorm", "eclipse", "netbeans", "codeblocks", "dev c++", "devc", "git", "github", "docker", "postman", "jetbrains", "notepad++", "sublime", "vim", "emacs", "maven", "gradle", "jdk", "android studio", "unity", "unreal", "terminal", "powershell", "wsl", "mysql", "navicat", "dbeaver", "redis", "mongodb", "node", "python", "jupyter", "anaconda", "conda", " rust", "c++", ".net", " idea", "cmake", "nasm", "fiddler", "wireshark", "make" },
            new string[] { "任务管理", "注册表", "资源管理", "控制面板", "设备管理", "磁盘", "清理", "卸载", "优化", "驱动", "ccleaner", "everything", "powertoys", "7-zip", "7zip", "winrar", "压缩", "regedit", "msconfig", "服务", "services" },
            new string[] { "office", "word", "excel", "powerpoint", "ppt", "outlook", "wps", "access", "onenote", "visio", "project", "pdf", "acrobat", "福昕", "foxit", "cajviewer", "nitro", "libreoffice", "openoffice", "typora", "markdown", "xmind", "思维导图", "幕布" },
            new string[] { "chrome", "谷歌浏览器", "edge", "firefox", "火狐", "opera", "brave", "vivaldi", "safari", "浏览器", "browser", "360极速", "360安全浏览器", "qq浏览器", "搜狗", "uc浏览器", "maxthon", "chromium" },
            new string[] { "微信", "qq", "tim", "钉钉", "dingtalk", "telegram", "电报", "discord", "skype", "飞书", "lark", "slack", "企业微信", "wxwork", "陌陌", "yy", "kakao", "line", "whatsapp" },
            new string[] { "音乐", "网易云", "netease", "qq音乐", "酷狗", "酷我", "kugou", "spotify", "foobar", "aimp", "player", "播放器", "vlc", "potplayer", "哔哩哔哩", "bilibili", "obs", "直播", "抖音", "tiktok", "剪映", "premiere", "after effect" },
            new string[] { "photoshop", "ps ", "illustrator", "figma", "sketch", "indesign", "coreldraw", "cdr", "paint", "gimp", "affinity", "3ds max", "maya", "blender", "autocad", "solidworks", "snipaste", "截图", "snagit" },
            new string[] { "vpn", "proxy", "代理", "ssr", "v2ray", "clash", "shadowsocks", "trojan", "wireguard", "openvpn", "ssh", "putty", "xshell", "winscp", "filezilla", "ftp", "迅雷", "thunder", "idm", "internet download" },
            new string[] { } // "其他"：兜底，无关键词
        };

        /// <summary>按名称归类；未命中任何规则返回"其他"。</summary>
        public static string Classify(string name)
        {
            if (string.IsNullOrEmpty(name)) return "其他";
            string lower = name.ToLowerInvariant();
            for (int i = 0; i < BuiltIn.Length; i++)
            {
                string[] kws = Keywords[i];
                if (kws == null) continue;
                for (int j = 0; j < kws.Length; j++)
                {
                    if (lower.IndexOf(kws[j], StringComparison.Ordinal) >= 0)
                        return BuiltIn[i];
                }
            }
            return "其他";
        }
    }
}
