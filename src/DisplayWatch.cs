// ============================================================================
// DisplayWatch.cs —— 桌宠显示方式（始终最上层 / 焦点冻结 / 全屏隐藏）
// ----------------------------------------------------------------------------
// 用户反馈：桌宠一直压在别的软件上面很影响使用，所以「怎么显示」交给用户选：
//   · always      始终保持在最上层（原行为，默认）
//   · focus       其他应用获得焦点时冻结：沉到窗口最底层 + 停动画 + 不主动搭话，
//                 一回到桌面（或点一下桌宠）立刻恢复
//   · fullscreen  其他应用全屏时隐藏：进后台运行（停动画、停搭话、收起气泡），
//                 退出全屏回到桌面再重新显示；非全屏时保持最上层
// 另外「其他程序全屏时自动隐藏」是任何模式下都能单独打开的兜底开关（默认开）。
//
// 设计要点：
//   · 只在「设置真的需要」时轮询（always + 不隐藏全屏 = 完全不轮询，零开销）；
//   · 一次采样 = GetForegroundWindow + GetWindowRect + GetMonitorInfo，纯本机 API，
//     不读窗口标题、不落盘、不外传（与 SystemWatch 那套需要授权的采集互不相干）；
//   · 判定逻辑写成纯函数（Decide / RectCovers），窗口行为可离线自检；
//   · 只有状态真的变化时才动窗口，避免 300ms 一次的无谓 SetWindowPos。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace AlDeskPet
{
    /// <summary>桌宠当前的显示状态。</summary>
    public enum PetDisplayState
    {
        /// <summary>正常显示（按设置决定是不是最上层）。</summary>
        Normal = 0,
        /// <summary>冻结：沉到窗口最底层，动画与主动搭话暂停，窗口仍在桌面上。</summary>
        Sunk = 1,
        /// <summary>进入后台：窗口隐藏，动画与主动搭话暂停，回到桌面再显示。</summary>
        Hidden = 2
    }

    /// <summary>桌宠显示方式（设置里的三选一）。</summary>
    public static class PetDisplayMode
    {
        public const string Always = "always";             // 始终保持在最上层
        public const string FocusFreeze = "focus";         // 其他应用获得焦点时冻结
        public const string FullscreenHide = "fullscreen"; // 其他应用全屏时隐藏

        public static string[] Ids()
        {
            return new string[] { Always, FocusFreeze, FullscreenHide };
        }

        /// <summary>界面上的短标题（顺序与 Ids 一致）。</summary>
        public static string[] Titles()
        {
            return new string[] { "始终保持在最上层", "其他应用获得焦点时冻结", "其他应用全屏时隐藏" };
        }

        /// <summary>界面上的详细说明（顺序与 Ids 一致）。</summary>
        public static string[] Details()
        {
            return new string[]
            {
                "桌宠永远浮在所有窗口上面（原来的行为）。开会投屏、全屏游戏时如果不想看见它，请打开下面的「其他程序全屏时自动隐藏」。",
                "你切到别的软件时，桌宠自动沉到窗口最底层并「冻结」：不再播放 Q 弹动画、不再主动说话，也不会挡住你正在用的程序。回到桌面或点一下桌宠马上恢复。",
                "平时仍然浮在最上面；一旦有别的程序全屏（游戏 / 视频 / 演示），桌宠就进入后台运行（隐藏 + 停动画 + 停搭话），退出全屏回到桌面后自动重新显示。"
            };
        }

        /// <summary>把任意字符串收敛成合法模式（非法值一律当 always）。</summary>
        public static string Normalize(string id)
        {
            if (string.IsNullOrEmpty(id)) return Always;
            string v = id.Trim().ToLowerInvariant();
            if (v == FocusFreeze) return FocusFreeze;
            if (v == FullscreenHide) return FullscreenHide;
            return Always;
        }

        public static int IndexOf(string id)
        {
            string v = Normalize(id);
            string[] ids = Ids();
            for (int i = 0; i < ids.Length; i++) if (ids[i] == v) return i;
            return 0;
        }

        public static string IdAt(int index)
        {
            string[] ids = Ids();
            if (index < 0 || index >= ids.Length) return Always;
            return ids[index];
        }

        public static string Title(string id) { return Titles()[IndexOf(id)]; }

        public static string Detail(string id) { return Details()[IndexOf(id)]; }

        /// <summary>当前设置需不需要起监听定时器（不需要就一点都不轮询）。</summary>
        public static bool NeedsWatch(string mode, bool hideOnFullscreen)
        {
            if (hideOnFullscreen) return true;
            return Normalize(mode) != Always;
        }
    }

    /// <summary>一次「当前前台窗口」的采样结果。</summary>
    public class ForegroundInfo
    {
        public IntPtr hwnd = IntPtr.Zero;
        public uint pid;
        public string cls = "";
        public string process = "";
        public bool isSelf;          // 桌宠自己（本体 / 气泡 / 输入条 / 设置界面，同进程）
        public bool isDesktop;       // 桌面、任务栏、开始菜单等系统外壳
        public bool isFullscreen;    // 全屏，且真的盖住了那块屏
        public string monitor = "";  // 全屏窗口所在显示器的设备名（如 \\.\DISPLAY1）
        public WinRect fullscreenRect; // 全屏窗口所在的显示器矩形（设备名拿不到时按它比对）
        public string describe = ""; // 给日志看的一句话

        /// <summary>前台是不是「别的程序」（桌面与自己都不算）。</summary>
        public bool isOtherApp
        {
            get { return hwnd != IntPtr.Zero && !isSelf && !isDesktop; }
        }
    }

    /// <summary>Win32 RECT（只用于全屏判定，做成公开结构体便于自检做纯计算）。</summary>
    public struct WinRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public WinRect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }

        public override string ToString()
        {
            return Left + "," + Top + " - " + Right + "," + Bottom;
        }
    }

    // ========================================================================
    // 前台窗口 / 全屏探测
    // ========================================================================
    public static class DisplayWatch
    {
        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public WinRect rcMonitor;
            public WinRect rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out WinRect rect);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
        static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong32(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        const int GWL_EXSTYLE = -20;
        const int WS_EX_TOPMOST = 0x00000008;
        const uint GW_HWNDNEXT = 2;
        const uint GA_ROOT = 2;
        const uint MONITOR_DEFAULTTONEAREST = 2;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOACTIVATE = 0x0010;

        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        static readonly int OwnPid = Process.GetCurrentProcess().Id;

        /// <summary>系统外壳的窗口类：不算「别的程序」。</summary>
        static readonly string[] ShellClasses = new string[]
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "SysShadow", "TaskListThumbnailWnd"
        };

        /// <summary>系统外壳的进程：开始菜单 / 搜索 / 通知中心这类浮层，不触发冻结或隐藏。</summary>
        static readonly string[] ShellProcesses = new string[]
        {
            "startmenuexperiencehost", "shellexperiencehost", "searchhost", "searchapp",
            "textinputhost", "applicationframehost", "lockapp"
        };

        // ---------------- 采样 ----------------

        /// <summary>
        /// 同一前台窗口的复用时间。前台窗口没变时不必反复探测（一次探测要动好几个 API），
        /// 但也不能一直复用：浏览器按 F11、播放器切全屏时窗口句柄可能不变、矩形会变，
        /// 所以最多复用 1 秒，最多晚 1 秒发现「刚刚全屏了」。
        /// </summary>
        const int ReuseMs = 1000;

        static IntPtr _lastHwnd = IntPtr.Zero;
        static DateTime _lastAt = DateTime.MinValue;
        static ForegroundInfo _lastInfo;

        /// <summary>
        /// 采一次当前前台窗口。selfWindows 是桌宠自己的窗口句柄（可为空：
        /// 同进程的窗口本来就按进程判为自己）。
        /// </summary>
        public static ForegroundInfo Sample(IntPtr[] selfWindows)
        {
            ForegroundInfo info = new ForegroundInfo();
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                // 前台窗口没变就直接复用上次结果（GetForegroundWindow 只花几微秒）
                if (hwnd != IntPtr.Zero && hwnd == _lastHwnd && _lastInfo != null
                    && (DateTime.Now - _lastAt).TotalMilliseconds < ReuseMs)
                    return _lastInfo;

                if (hwnd == IntPtr.Zero)
                {
                    info.describe = "没有前台窗口（桌面）";
                    _lastHwnd = hwnd; _lastAt = DateTime.Now; _lastInfo = info;
                    return info;
                }
                info.hwnd = hwnd;
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                info.pid = pid;
                info.cls = ClassName(hwnd);
                info.isDesktop = IsShellWindow(hwnd, info.cls, pid);
                info.isSelf = !info.isDesktop && IsSelfWindow(hwnd, pid, selfWindows);

                if (!info.isDesktop && !info.isSelf)
                {
                    info.process = ProcessName(pid);
                    if (IsWindowVisible(hwnd) && !IsIconic(hwnd))
                    {
                        string monitor;
                        WinRect rect;
                        if (IsFullscreen(hwnd, selfWindows, out monitor, out rect))
                        {
                            info.isFullscreen = true;
                            info.monitor = monitor;
                            info.fullscreenRect = rect;
                        }
                    }
                    info.describe = (string.IsNullOrEmpty(info.process) ? info.cls : info.process)
                        + (info.isFullscreen ? "（全屏）" : "");
                }
                else
                {
                    info.describe = info.isSelf ? "桌宠自己" : "桌面";
                }
                _lastHwnd = hwnd;
                _lastAt = DateTime.Now;
                _lastInfo = info;
                return info;
            }
            catch (Exception ex)
            {
                Log.Debug("前台窗口采样异常：" + ex.Message);
                return info;
            }
        }

        static string ClassName(IntPtr hwnd)
        {
            try
            {
                StringBuilder sb = new StringBuilder(256);
                GetClassName(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return ""; }
        }

        // ---------------- 进程名（带缓存，绝不能每条 tick 都去枚举系统进程） ----------------
        //
        // 注意：Process.GetProcessById(pid).ProcessName 看着很无辜，实际每次都会调
        // NtQuerySystemInformation(SystemProcessInformation) —— 把整机所有进程和线程
        // 全部枚举一遍，一次要几毫秒到十几毫秒。放在 300ms 的定时器里就是几个点的 CPU。
        // 所以这里改成 OpenProcess + QueryFullProcessImageName（几十微秒），再按 pid 缓存。

        class NameEntry
        {
            public string name = "";
            public DateTime at = DateTime.MinValue;
        }

        const int NameCacheMs = 30000;
        static readonly Dictionary<uint, NameEntry> NameCache = new Dictionary<uint, NameEntry>();

        static string ProcessName(uint pid)
        {
            if (pid == 0) return "";
            try
            {
                NameEntry e;
                if (NameCache.TryGetValue(pid, out e)
                    && (DateTime.Now - e.at).TotalMilliseconds < NameCacheMs)
                    return e.name;

                string name = FastProcessName(pid);
                if (string.IsNullOrEmpty(name)) name = SlowProcessName(pid);
                if (NameCache.Count > 256) NameCache.Clear();     // 简单封顶，别无限长
                NameEntry entry = new NameEntry();
                entry.name = name;
                entry.at = DateTime.Now;
                NameCache[pid] = entry;
                return name;
            }
            catch { return ""; }
        }

        /// <summary>快路径：只取映像文件名，几十微秒（不需要管理员权限，受限进程返回空）。</summary>
        static string FastProcessName(uint pid)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return "";
                int size = 1024;
                StringBuilder sb = new StringBuilder(size);
                if (!QueryFullProcessImageName(h, 0, sb, ref size)) return "";
                string path = sb.ToString();
                if (path.Length == 0) return "";
                return Path.GetFileNameWithoutExtension(path);
            }
            catch { return ""; }
            finally
            {
                if (h != IntPtr.Zero) { try { CloseHandle(h); } catch { } }
            }
        }

        /// <summary>兜底：快路径拿不到（权限不足等）时才走的慢路径。</summary>
        static string SlowProcessName(uint pid)
        {
            try
            {
                using (Process p = Process.GetProcessById((int)pid)) return p.ProcessName;
            }
            catch { return ""; }
        }

        /// <summary>桌面 / 任务栏 / 开始菜单等系统外壳（对这些窗口不冻结也不隐藏）。</summary>
        static bool IsShellWindow(IntPtr hwnd, string cls, uint pid)
        {
            try
            {
                if (hwnd == GetShellWindow() || hwnd == GetDesktopWindow()) return true;
                for (int i = 0; i < ShellClasses.Length; i++)
                    if (string.Equals(cls, ShellClasses[i], StringComparison.OrdinalIgnoreCase)) return true;
                string proc = ProcessName(pid);
                for (int i = 0; i < ShellProcesses.Length; i++)
                    if (string.Equals(proc, ShellProcesses[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch (Exception ex) { Log.Debug("判断系统外壳窗口异常：" + ex.Message); }
            return false;
        }

        /// <summary>是不是桌宠自己的窗口：同进程，或者就是传进来的那几个句柄。</summary>
        static bool IsSelfWindow(IntPtr hwnd, uint pid, IntPtr[] selfWindows)
        {
            if (hwnd == IntPtr.Zero) return false;
            if (selfWindows != null)
            {
                for (int i = 0; i < selfWindows.Length; i++)
                    if (selfWindows[i] != IntPtr.Zero && selfWindows[i] == hwnd) return true;
            }
            try
            {
                if (pid == 0) GetWindowThreadProcessId(hwnd, out pid);
                return pid != 0 && pid == (uint)OwnPid;
            }
            catch { return false; }
        }

        /// <summary>矩形是否盖满显示器（纯函数，便于自检）。</summary>
        public static bool RectCovers(WinRect window, WinRect monitor)
        {
            return window.Left <= monitor.Left && window.Top <= monitor.Top
                && window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;
        }

        /// <summary>
        /// 是否「真全屏」：窗口矩形盖满所在显示器，并且屏幕中心点的最上层窗口就是它
        /// （避免「窗口比屏幕大但其实被别的窗口压着」的误判）。
        /// </summary>
        public static bool IsFullscreen(IntPtr hwnd, IntPtr[] selfWindows, out string monitorDevice, out WinRect monitorRect)
        {
            monitorDevice = "";
            monitorRect = new WinRect();
            try
            {
                if (hwnd == IntPtr.Zero) return false;
                WinRect r;
                if (!GetWindowRect(hwnd, out r)) return false;
                IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (mon == IntPtr.Zero) return false;
                MONITORINFO mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (!GetMonitorInfo(mon, ref mi)) return false;
                if (!RectCovers(r, mi.rcMonitor)) return false;

                POINT center = new POINT();
                center.x = (mi.rcMonitor.Left + mi.rcMonitor.Right) / 2;
                center.y = (mi.rcMonitor.Top + mi.rcMonitor.Bottom) / 2;
                IntPtr top = WindowFromPoint(center);
                if (top != IntPtr.Zero)
                {
                    IntPtr root = GetAncestor(top, GA_ROOT);
                    // 中心点上是它自己、或者压在上面的只是桌宠自己 → 依然算全屏
                    if (root != hwnd && top != hwnd
                        && !IsSelfWindow(root, 0, selfWindows) && !IsSelfWindow(top, 0, selfWindows))
                        return false;
                }
                monitorRect = mi.rcMonitor;
                monitorDevice = MonitorDeviceName(mon);
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("全屏判定异常：" + ex.Message);
                return false;
            }
        }

        /// <summary>兼容写法：只关心「是不是全屏」时用。</summary>
        public static bool IsFullscreen(IntPtr hwnd, IntPtr[] selfWindows)
        {
            string dev;
            WinRect rect;
            return IsFullscreen(hwnd, selfWindows, out dev, out rect);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
        static extern bool GetMonitorInfoEx(IntPtr hMonitor, ref MONITORINFOEX info);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX
        {
            public int cbSize;
            public WinRect rcMonitor;
            public WinRect rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        static string MonitorDeviceName(IntPtr hMonitor)
        {
            try
            {
                MONITORINFOEX ex = new MONITORINFOEX();
                ex.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                if (!GetMonitorInfoEx(hMonitor, ref ex)) return "";
                return ex.szDevice == null ? "" : ex.szDevice.Trim();
            }
            catch { return ""; }
        }

        // ---------------- 显示状态的决策（纯函数） ----------------

        /// <summary>
        /// 决定桌宠此刻该处于什么状态。
        /// </summary>
        /// <param name="mode">设置里的显示方式</param>
        /// <param name="hideOnFullscreen">「其他程序全屏时自动隐藏」开关</param>
        /// <param name="fg">前台窗口采样</param>
        /// <param name="fullscreenOnThisScreen">前台的全屏窗口是否和这只桌宠在同一块屏幕上</param>
        public static PetDisplayState Decide(string mode, bool hideOnFullscreen, ForegroundInfo fg, bool fullscreenOnThisScreen)
        {
            string m = PetDisplayMode.Normalize(mode);
            // 桌面 / 桌宠自己在前台：一切照旧
            if (fg == null || fg.isSelf || fg.isDesktop || !fg.isOtherApp) return PetDisplayState.Normal;

            // 注意：fullscreenOnThisScreen 由调用方算好（前台全屏窗口是否就在这只桌宠的屏幕上），
            // 这里再确认一次 fg.isFullscreen，避免调用方传错参数时把普通窗口当成全屏。
            bool hideForFullscreen = fg.isFullscreen && fullscreenOnThisScreen
                && (m == PetDisplayMode.FullscreenHide || hideOnFullscreen);
            if (hideForFullscreen) return PetDisplayState.Hidden;
            if (m == PetDisplayMode.FocusFreeze) return PetDisplayState.Sunk;
            return PetDisplayState.Normal;
        }

        /// <summary>
        /// 前台的全屏窗口是不是和某块屏幕是同一块。
        /// deviceName / screenBounds 由调用方（桌宠）按自己的位置算出来。
        /// 优先比显示器设备名，退化到比显示器矩形；两者都拿不到时保守返回 true
        /// （宁可把桌宠收起来，也不要让它挡在全屏程序上面）。
        /// </summary>
        public static bool SameScreen(ForegroundInfo fg, string deviceName, WinRect screenBounds)
        {
            if (fg == null || !fg.isFullscreen) return false;
            if (!string.IsNullOrEmpty(fg.monitor) && !string.IsNullOrEmpty(deviceName))
                return string.Equals(fg.monitor, deviceName, StringComparison.OrdinalIgnoreCase);
            if (fg.fullscreenRect.Width > 0 && screenBounds.Width > 0)
                return fg.fullscreenRect.Left == screenBounds.Left && fg.fullscreenRect.Top == screenBounds.Top;
            return true;
        }

        // ---------------- 窗口层级 ----------------

        /// <summary>置顶 / 取消置顶（不动位置、不抢焦点）。</summary>
        public static bool SetTopMost(IntPtr hwnd, bool topMost)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                return SetWindowPos(hwnd, topMost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch (Exception ex)
            {
                Log.Debug("设置置顶失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>沉到窗口最底层（其余窗口都会盖住它；不会跑到桌面壁纸下面，见自检）。</summary>
        public static bool PushToBottom(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                return SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch (Exception ex)
            {
                Log.Debug("沉到最底层失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>窗口当前是否置顶（自检与日志用）。</summary>
        public static bool IsTopMost(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                int style = IntPtr.Size == 8
                    ? (int)GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64()
                    : GetWindowLong32(hwnd, GWL_EXSTYLE);
                return (style & WS_EX_TOPMOST) != 0;
            }
            catch { return false; }
        }

        public static bool IsVisible(IntPtr hwnd)
        {
            try { return hwnd != IntPtr.Zero && IsWindow(hwnd) && IsWindowVisible(hwnd); }
            catch { return false; }
        }

        /// <summary>窗口在 Z 序里的位置（0 = 最上面；-1 = 没找到）。自检用。</summary>
        public static int ZIndex(IntPtr hwnd)
        {
            try
            {
                if (hwnd == IntPtr.Zero) return -1;
                int i = 0;
                IntPtr h = GetTopWindow(IntPtr.Zero);
                while (h != IntPtr.Zero && i < 4000)
                {
                    if (h == hwnd) return i;
                    h = GetWindow(h, GW_HWNDNEXT);
                    i++;
                }
            }
            catch { }
            return -1;
        }

        /// <summary>桌面外壳窗口的 Z 序位置（桌宠必须比它更靠上，否则就被壁纸盖住了）。</summary>
        public static int ShellZIndex()
        {
            try { return ZIndex(GetShellWindow()); }
            catch { return -1; }
        }
    }

    // ========================================================================
    // 显示监听：按设置定时采样前台窗口，把状态推给每一只桌宠
    // ========================================================================
    public static class PetDisplayWatcher
    {
        /// <summary>采样间隔。300ms 足够跟手，单次开销只有几个本机 API 调用。</summary>
        public const int IntervalMs = 300;

        static Timer _timer;

        /// <summary>已经跑过的采样次数（自检用来看它是不是真的在跑）。</summary>
        public static int Ticks;

        public static bool Running { get { return _timer != null; } }

        public static void Start()
        {
            try
            {
                if (_timer != null) return;
                _timer = new Timer();
                _timer.Interval = IntervalMs;
                _timer.Tick += delegate { Tick(); };
                _timer.Start();
                Log.Info("桌宠显示监听已启动（间隔 " + IntervalMs + " ms，仅本机 API 判定前台窗口）");
                Tick();
            }
            catch (Exception ex)
            {
                Log.Error("启动桌宠显示监听失败", ex);
            }
        }

        public static void Stop()
        {
            try
            {
                if (_timer == null) return;
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
                Log.Info("桌宠显示监听已停止");
            }
            catch (Exception ex)
            {
                Log.Debug("停止桌宠显示监听异常：" + ex.Message);
            }
        }

        /// <summary>采一次并应用到所有桌宠（定时器与自检共用）。</summary>
        public static void Tick()
        {
            try
            {
                Ticks++;
                PetManager.RefreshDisplay();
            }
            catch (Exception ex)
            {
                Log.Debug("桌宠显示采样异常：" + ex.Message);
            }
        }
    }
}
