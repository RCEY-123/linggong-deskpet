// ============================================================================
// SystemWatch.cs —— 系统事件 / 用户操作监听（前台窗口 + 空闲时间）
// ----------------------------------------------------------------------------
// 需要用户授权（需求 6）：授权信息存放在 Config.Current.systemWatchPermission，
// 未授权时本模块不启动，不会采集任何数据。
// 只记录「前台程序名 + 窗口标题 + 停留时长」，全部在内存里，不落盘、不外传，
// 仅用于在开启大模型对话时给模型一段「刚刚在做什么」的上下文。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AlDeskPet
{
    public class AppSample
    {
        public DateTime time;
        public string process = "";
        public string title = "";
    }

    public class AppUsage
    {
        public string process = "";
        public string friendly = "";
        public string title = "";
        public int seconds;
    }

    public class SystemSnapshot
    {
        public string currentApp = "";          // 友好名
        public string currentProcess = "";
        public string currentTitle = "";
        public int idleSeconds;
        public List<AppUsage> recent = new List<AppUsage>();
        public bool watchEnabled;
    }

    public static class SystemWatch
    {
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        const int WindowMinutes = 30;
        static readonly List<AppSample> Samples = new List<AppSample>();
        static readonly object Gate = new object();
        static Timer _timer;
        static int _intervalMs = 5000;
        static bool _enabled;

        public static bool Enabled { get { return _enabled; } }

        public static void Start(int intervalMs)
        {
            try
            {
                _intervalMs = Math.Max(1000, intervalMs);
                if (_timer != null) { _timer.Change(0, _intervalMs); _enabled = true; return; }
                _timer = new Timer(delegate { Tick(); }, null, 0, _intervalMs);
                _enabled = true;
                Log.Info("系统事件监听已启动（间隔 " + _intervalMs + " ms，仅记录前台程序名与窗口标题）");
            }
            catch (Exception ex)
            {
                Log.Error("启动系统事件监听失败", ex);
            }
        }

        public static void Stop()
        {
            try
            {
                if (_timer != null)
                {
                    _timer.Dispose();
                    _timer = null;
                }
                _enabled = false;
                lock (Gate) Samples.Clear();
                Log.Info("系统事件监听已停止");
            }
            catch (Exception ex)
            {
                Log.Warn("停止系统事件监听异常：" + ex.Message);
            }
        }

        static void Tick()
        {
            try
            {
                string process = "", title = "";
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd != IntPtr.Zero)
                {
                    uint pid;
                    GetWindowThreadProcessId(hwnd, out pid);
                    StringBuilder sb = new StringBuilder(512);
                    GetWindowText(hwnd, sb, sb.Capacity);
                    title = sb.ToString();
                    if (pid != 0)
                    {
                        try
                        {
                            using (Process p = Process.GetProcessById((int)pid)) process = p.ProcessName;
                        }
                        catch { process = ""; }
                    }
                }

                // 不记录自己，避免「桌宠看着桌宠」
                if (!string.IsNullOrEmpty(process) &&
                    process.Equals(AppPaths.ProductNameEn, StringComparison.OrdinalIgnoreCase)) { process = ""; title = ""; }

                if (string.IsNullOrEmpty(process)) return;

                lock (Gate)
                {
                    Samples.Add(new AppSample { time = DateTime.Now, process = process, title = title });
                    DateTime limit = DateTime.Now.AddMinutes(-WindowMinutes);
                    int remove = 0;
                    while (remove < Samples.Count && Samples[remove].time < limit) remove++;
                    if (remove > 0) Samples.RemoveRange(0, remove);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("系统事件采样异常：" + ex.Message);
            }
        }

        public static int IdleSeconds()
        {
            try
            {
                LASTINPUTINFO info = new LASTINPUTINFO();
                info.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
                if (!GetLastInputInfo(ref info)) return 0;
                uint elapsed = (uint)Environment.TickCount - info.dwTime;
                if (elapsed > int.MaxValue) return 0;
                return (int)(elapsed / 1000);
            }
            catch { return 0; }
        }

        /// <summary>取当前快照（最近 30 分钟的前台使用情况 + 空闲时间）。</summary>
        public static SystemSnapshot Snapshot()
        {
            SystemSnapshot snap = new SystemSnapshot();
            snap.watchEnabled = _enabled;
            snap.idleSeconds = IdleSeconds();
            try
            {
                List<AppSample> copy;
                lock (Gate)
                {
                    copy = new List<AppSample>(Samples);
                }

                Dictionary<string, AppUsage> agg = new Dictionary<string, AppUsage>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < copy.Count; i++)
                {
                    int span = _intervalMs / 1000;
                    if (i == copy.Count - 1) span = Math.Min(span, Math.Max(1, _intervalMs / 1000));
                    AppUsage u;
                    if (!agg.TryGetValue(copy[i].process, out u))
                    {
                        u = new AppUsage();
                        u.process = copy[i].process;
                        u.friendly = FriendlyName(copy[i].process);
                        agg[copy[i].process] = u;
                    }
                    u.seconds += span;
                    if (i == copy.Count - 1)
                    {
                        u.title = copy[i].title;
                        snap.currentProcess = copy[i].process;
                        snap.currentTitle = copy[i].title;
                        snap.currentApp = u.friendly;
                    }
                }

                List<AppUsage> list = new List<AppUsage>(agg.Values);
                list.Sort(delegate (AppUsage a, AppUsage b) { return b.seconds.CompareTo(a.seconds); });
                snap.recent = list;
                return snap;
            }
            catch (Exception ex)
            {
                Log.Debug("生成系统快照失败：" + ex.Message);
                return snap;
            }
        }

        /// <summary>生成给大模型看的自然语言上下文。</summary>
        public static string BuildContextHint(SystemSnapshot snap)
        {
            if (snap == null) return "";
            StringBuilder sb = new StringBuilder();
            if (snap.recent.Count == 0)
            {
                sb.Append("（最近 30 分钟没有采集到前台程序信息）");
            }
            else
            {
                sb.Append("最近 30 分钟，玩家在前台使用过的程序：");
                int count = 0;
                foreach (AppUsage u in snap.recent)
                {
                    if (u.seconds < 10 && count > 0) continue;
                    if (count > 0) sb.Append("、");
                    sb.Append(u.friendly).Append("（").Append(FormatDuration(u.seconds)).Append("）");
                    count++;
                    if (count >= 5) break;
                }
                sb.Append("；");
                if (!string.IsNullOrEmpty(snap.currentApp))
                {
                    sb.Append("当前正在用：").Append(snap.currentApp);
                    if (!string.IsNullOrEmpty(snap.currentTitle))
                    {
                        bool clipped;
                        sb.Append("（窗口标题：").Append(Utils.Clip(snap.currentTitle, 40, out clipped)).Append("）");
                    }
                    sb.Append("；");
                }
                if (snap.idleSeconds > 120)
                    sb.Append("玩家已经 ").Append(FormatDuration(snap.idleSeconds)).Append("没有动键鼠了；");
                else
                    sb.Append("玩家刚刚还在操作键鼠；");
            }
            sb.Append("现在是 ").Append(DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture)).Append("。");
            return sb.ToString();
        }

        /// <summary>固定台词模式下的占位符替换：{app} {title} {idle} {time}。</summary>
        public static string FillPlaceholders(string text, SystemSnapshot snap)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string app = snap != null && !string.IsNullOrEmpty(snap.currentApp) ? snap.currentApp : "电脑";
            string title = snap != null ? snap.currentTitle : "";
            string idle = snap != null ? FormatDuration(snap.idleSeconds) : "0 秒";
            string mainApp = app;
            if (snap != null && snap.recent.Count > 0) mainApp = snap.recent[0].friendly;

            return text
                .Replace("{app}", app)
                .Replace("{main}", mainApp)
                .Replace("{title}", title)
                .Replace("{idle}", idle)
                .Replace("{time}", DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture));
        }

        public static string FormatDuration(int seconds)
        {
            if (seconds < 60) return seconds + " 秒";
            if (seconds < 3600) return (seconds / 60) + " 分钟";
            return (seconds / 3600) + " 小时 " + ((seconds % 3600) / 60) + " 分钟";
        }

        /// <summary>常见程序 → 中文友好名。</summary>
        public static string FriendlyName(string process)
        {
            if (string.IsNullOrEmpty(process)) return "未知程序";
            switch (process.ToLowerInvariant())
            {
                case "chrome": return "Chrome 浏览器";
                case "msedge": return "Edge 浏览器";
                case "firefox": return "Firefox 浏览器";
                case "explorer": return "文件资源管理器";
                case "code": return "VS Code";
                case "devenv": return "Visual Studio";
                case "idea64": return "IntelliJ IDEA";
                case "pycharm64": return "PyCharm";
                case "photoshop": return "Photoshop";
                case "steam": case "steamwebhelper": return "Steam";
                case "genshinimpact": case "yuanshen": return "原神";
                case "starrail": return "崩坏：星穹铁道";
                case "bh3": return "崩坏3";
                case "leagueclient": case "league of legends": return "英雄联盟";
                case "javaw": case "java": return "Java 程序（常见于 Minecraft）";
                case "minecraft.windows": return "Minecraft";
                case "potplayermini64": case "potplayer": return "PotPlayer 播放器";
                case "bilibili": return "哔哩哔哩";
                case "qq": case "tim": return "QQ";
                case "wechat": return "微信";
                case "dingtalk": return "钉钉";
                case "windowsterminal": case "wt": return "Windows 终端";
                case "powershell": case "pwsh": return "PowerShell";
                case "cmd": return "命令提示符";
                case "notepad": return "记事本";
                case "wps": return "WPS";
                case "winword": return "Word";
                case "excel": return "Excel";
                case "powerpnt": return "PowerPoint";
                case "acrobat": case "acrord32": return "PDF 阅读器";
                case "obs64": return "OBS";
                case "clash": case "clash-verge": case "clash for windows": return "代理工具";
                case "vlc": return "VLC 播放器";
                case "azurlanedeskpet": return "桌宠";
                default:
                    if (process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return process;
                    return process;
            }
        }

        /// <summary>粗略判断是不是游戏（用于 @系统 台词里问「你刚刚是在打游戏吗」）。</summary>
        public static bool IsLikelyGame(string process, string title)
        {
            string p = (process ?? "").ToLowerInvariant();
            string t = (title ?? "").ToLowerInvariant();
            string[] keys = new string[] { "game", "genshin", "yuanshen", "starrail", "steam", "league", "dota", "csgo", "cs2", "valorant", "pubg", "minecraft", "bh3", "honkai", "wuthering", "arknights", "azurlane", "blhx", "unityplayer", "unreal" };
            foreach (string k in keys)
            {
                if (p.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (t.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }
    }
}
