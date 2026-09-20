// ============================================================================
// 灵工桌宠 · 卸载程序（AzurLaneDeskPet Uninstaller）
// ----------------------------------------------------------------------------
// 本 exe 位于安装目录内。为了让安装目录能被完整清空，卸载器会：
//   1) 把自己复制到 %TEMP%\AzurLaneDeskPet-uninstall-<随机>.exe
//   2) 用 --from-temp --dir="<安装目录>" 重新启动临时副本，原进程立即退出
//   3) 由临时副本执行全部清理，并在结束时自删除
//
// 清理范围（要求 8：本地所有桌宠相关文件全部清除）：
//   · 结束正在运行的 AzurLaneDeskPet.exe
//   · 安装目录下所有文件与目录（含安装目录本身；删不掉则写延迟删除 cmd）
//   · %APPDATA%\AzurLaneDeskPet（日志 / 角色素材 / 导入的图片语音台词）
//   · config.json 里 dataDir 指向的自定义数据目录（带安全校验）
//   · install.json 记录的快捷方式 + 开始菜单 / 桌面 / 公共桌面 的默认快捷方式
//   · 注册表 HKCU\...\Uninstall\AzurLaneDeskPet 与 HKCU\Software\AzurLaneDeskPet
//   · %TEMP% 下自己产生的日志与临时文件（含本次报告以外的残留）
//
// 编译（系统自带 csc，C# 5 语法，UTF-8 源码需 /codepage:65001）：
//   csc.exe /nologo /target:winexe /codepage:65001
//           /out:"卸载-灵工桌宠.exe" /win32icon:icon.ico
//           /r:System.Windows.Forms.dll /r:System.Drawing.dll
//           Uninstaller.cs
//
// 命令行：/S | --silent | --dir=<目录> | --purge-data | --dry-run | --report=<文件>
//         --from-temp（内部使用）
// 退出码：0 成功 / 2 用户取消 / 3 失败
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AzurLaneDeskPet.Uninstall
{
    // ==================================================================== 常量
    internal static class Const
    {
        public const string Product = "灵工桌宠";
        public const string Version = "1.1.0";
        public const string Publisher = "睡不着のHATSUZUKI";
        public const string Slogan = "灵工巧物 天祈智临";
        public const string ExeName = "AzurLaneDeskPet.exe";
        public const string UninstallerName = "卸载-灵工桌宠.exe";
        public const string ShortcutName = "灵工桌宠.lnk";
        public const string InstallManifest = "install.json";
        public const string FileListManifest = "files.json";
        public const string InstallMarker = ".azurlandeskpet-install";
        public const string DataMarker = ".azurlandeskpet-data";
        public const string DataFolderName = "AzurLaneDeskPet";
        public const string RegUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AzurLaneDeskPet";
        public const string RegProductKey = @"Software\AzurLaneDeskPet";

        public const int ExitOk = 0;
        public const int ExitCancelled = 2;
        public const int ExitFailed = 3;

        public static string LogPath
        {
            get { return Path.Combine(Path.GetTempPath(), "AzurLaneDeskPet-uninstall.log"); }
        }

        /// <summary>
        /// 默认用户数据目录。环境变量 AZURLANEDESK_PET_DATA 可覆盖 ——
        /// 自动化验收脚本靠它把"要删的数据目录"指到沙箱里，绝不碰用户的真实数据。
        /// </summary>
        public static string DataDir
        {
            get
            {
                try
                {
                    string env = Environment.GetEnvironmentVariable("AZURLANEDESK_PET_DATA");
                    if (!string.IsNullOrEmpty(env))
                    {
                        env = Environment.ExpandEnvironmentVariables(env.Trim());
                        if (env.Length > 0) return Path.GetFullPath(env);
                    }
                }
                catch (Exception) { }
                return RealUserDataDir;
            }
        }

        /// <summary>用户真实数据目录（始终是 %APPDATA%\AzurLaneDeskPet，不受环境变量影响）。</summary>
        public static string RealUserDataDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DataFolderName);
            }
        }
    }

    // ==================================================================== 入口
    internal static class Program
    {
        private static readonly StringBuilder Report = new StringBuilder();
        public static bool SilentMode;
        public static string ReportPath = "";

        [STAThread]
        private static int Main(string[] args)
        {
            try { Native.SetProcessDPIAware(); }
            catch (Exception) { }

            Options opt = Options.Parse(args);
            SilentMode = opt.Silent;
            ReportPath = opt.Report;
            Log.Write("Main 进入：fromTemp=" + opt.FromTemp + " silent=" + opt.Silent + " pid=" + Process.GetCurrentProcess().Id);

            if (!opt.FromTemp)
            {
                // ---- 阶段一：把自己搬到 %TEMP% 再执行，保证安装目录能被清空 ----
                int relaunch = Launcher.RelaunchFromTemp(opt);
                Log.Write("阶段一结束：relaunch=" + relaunch + " pid=" + Process.GetCurrentProcess().Id + "（即将返回 Main）");
                return relaunch;
            }
            Log.Write("阶段二开始（临时副本）：pid=" + Process.GetCurrentProcess().Id);

            if (opt.Silent)
            {
                Log.Write("==== 静默卸载开始 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ====");
                Engine eng = new Engine(opt, Log.Write);
                int code = Const.ExitFailed;
                try { code = eng.Run(false); }
                catch (Exception ex)
                {
                    Log.Write("致命错误：" + ex);
                    Report.AppendLine("FATAL: " + ex.Message);
                    code = Const.ExitFailed;
                }
                Report.Append(eng.Steps);
                FlushReport(opt, eng, code);
                return code;
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Log.Write("==== 图形卸载开始 ====");
                UninstallForm form = new UninstallForm(opt);
                Application.Run(form);
                Report.Append(form.Steps);
                FlushReport(opt, form.LastEngine, form.ResultCode);
                return form.ResultCode;
            }
            catch (Exception ex)
            {
                Log.Write("图形模式异常：" + ex);
                Report.AppendLine("FATAL: " + ex.Message);
                FlushReport(opt, null, Const.ExitFailed);
                try
                {
                    MessageBox.Show("卸载失败：" + ex.Message + Environment.NewLine + Environment.NewLine
                        + "日志：" + Const.LogPath, Const.Product + " 卸载程序",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch (Exception) { }
                return Const.ExitFailed;
            }
        }

        private static void FlushReport(Options opt, Engine eng, int code)
        {
            if (opt == null || opt.Report == null || opt.Report.Length == 0) return;
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("== " + Const.Product + " 卸载程序报告 ==");
                sb.AppendLine("时间     : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("模式     : " + (opt.Silent ? "静默" : "图形") + (opt.DryRun ? " / dry-run（不写盘）" : ""));
                sb.AppendLine("安装目录 : " + (eng != null ? eng.InstallDir : opt.InstallDir));
                sb.AppendLine("临时副本 : " + (opt.FromTemp ? Application.ExecutablePath : "(未搬迁)"));
                sb.AppendLine("日志     : " + Const.LogPath);
                sb.AppendLine();
                sb.AppendLine("---- 步骤 ----");
                sb.Append(Report.ToString());
                if (eng != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("---- 统计 ----");
                    sb.AppendLine("删除文件 : " + eng.DeletedFiles);
                    sb.AppendLine("删除目录 : " + eng.DeletedDirs);
                    sb.AppendLine("删除快捷方式 : " + eng.DeletedLinks);
                    sb.AppendLine("清理注册表项 : " + eng.DeletedRegKeys);
                    sb.AppendLine("删除模式 : " + (eng.Manifest != null
                        ? "清单模式（files.json：" + eng.Manifest.Files.Count + " 文件 / " + eng.Manifest.Dirs.Count + " 目录）"
                        : "保守模式（无 files.json，只删本产品已知文件）"));
                    sb.AppendLine("未能删除 : " + eng.Failed.Count + " 项");
                    foreach (string f in eng.Failed) sb.AppendLine("   ! " + f);

                    sb.AppendLine();
                    sb.AppendLine("---- 已保留（非本产品文件） ----");
                    if (eng.KeptItems.Count == 0) sb.AppendLine("(无，本产品文件已全部清除)");
                    else foreach (string k in eng.KeptItems) sb.AppendLine("   · " + k);

                    sb.AppendLine();
                    sb.AppendLine("---- 安全校验拒绝 ----");
                    if (eng.BlockedItems.Count == 0) sb.AppendLine("(无)");
                    else foreach (string b in eng.BlockedItems) sb.AppendLine("   ✗ " + b);
                    if (eng.InstallDirRefused)
                    {
                        sb.AppendLine();
                        sb.AppendLine("注意：安装目录未通过安全校验，本次未删除任何文件（包括用户数据）。");
                    }
                    if (eng.KeptItems.Count > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine("注意：安装目录内存在非本产品文件，已按安全策略全部保留；如需彻底清空请手动确认后删除。");
                    }
                }
                sb.AppendLine();
                sb.AppendLine("---- 退出码 ----");
                sb.AppendLine("exit=" + code + "  (" + ExitText(code) + ")");
                sb.AppendLine("0 = 卸载成功");
                sb.AppendLine("2 = 用户取消");
                sb.AppendLine("3 = 卸载失败（原因见上）");
                string dir = Path.GetDirectoryName(opt.Report);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(opt.Report, sb.ToString(), new UTF8Encoding(false));
                Log.Write("报告已写入：" + opt.Report);
            }
            catch (Exception ex) { Log.Write("写报告失败：" + ex.Message); }
        }

        private static string ExitText(int code)
        {
            if (code == Const.ExitOk) return "成功";
            if (code == Const.ExitCancelled) return "用户取消";
            return "失败";
        }
    }

    // ================================================================== 日志
    internal static class Log
    {
        private static readonly object Gate = new object();

        public static void Write(string text)
        {
            lock (Gate)
            {
                try
                {
                    File.AppendAllText(Const.LogPath,
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text + Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch (Exception) { }
            }
        }
    }

    // ================================================================== 参数
    internal class Options
    {
        public bool Silent;
        public bool DryRun;
        public bool PurgeData = true;
        public bool FromTemp;
        public string InstallDir = "";
        public string Report = "";
        public bool NoTempClean;   // 内部：清理临时文件时跳过指定文件
        public string KeepPaths = "";

        public static Options Parse(string[] args)
        {
            Options o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string raw = args[i] == null ? "" : args[i];
                string a = raw.Trim();
                if (a.Length == 0) continue;
                string low = a.ToLowerInvariant();

                if (low == "/s" || low == "-s" || low == "--silent" || low == "/silent" || low == "/verysilent") o.Silent = true;
                else if (low == "/d" || low == "--dry-run" || low == "/dry-run" || low == "/dryrun") o.DryRun = true;
                else if (low == "--purge-data" || low == "/purge-data" || low == "/purgedata") o.PurgeData = true;
                else if (low == "--keep-data" || low == "/keep-data") o.PurgeData = false;
                else if (low == "--from-temp" || low == "/from-temp") o.FromTemp = true;
                else if (low.StartsWith("/d=") || low.StartsWith("--dir=") || low.StartsWith("/dir="))
                {
                    int eq = a.IndexOf('=');
                    string v = Strip(a.Substring(eq + 1));
                    // 目录含空格会被拆成多个 argv；遇到下一个选项立即停止拼接
                    while (i + 1 < args.Length && !Options.IsOption(args[i + 1]))
                    {
                        i++;
                        v = (v.Length == 0 ? "" : v + " ") + Strip(args[i]);
                    }
                    v = v.Trim().Trim('"').Trim();
                    if (v.Length > 0) o.InstallDir = v;
                }
                else if (low.StartsWith("--report=") || low.StartsWith("/report="))
                {
                    int eq = a.IndexOf('=');
                    string v = Strip(a.Substring(eq + 1));
                    while (i + 1 < args.Length && !Options.IsOption(args[i + 1]))
                    {
                        i++;
                        v = (v.Length == 0 ? "" : v + " ") + Strip(args[i]);
                    }
                    v = v.Trim().Trim('"').Trim();
                    if (v.Length > 0) o.Report = v;
                }
            }
            return o;
        }

        // 判断一个 argv 是否是“选项”（而不是被空格拆开的路径片段）
        public static bool IsOption(string s)
        {
            if (s == null) return false;
            string t = s.Trim();
            if (t.Length == 0) return false;
            if (t.StartsWith("-")) return true;
            if (t.StartsWith("/")) return true;
            return false;
        }

        private static string Strip(string s)
        {
            if (s == null) return "";
            while (s.StartsWith("\"")) s = s.Substring(1);
            while (s.EndsWith("\"")) s = s.Substring(0, s.Length - 1);
            return s;
        }
    }

    // ================================================================== 搬迁
    internal static class Launcher
    {
        // 把自身复制到 %TEMP%\AzurLaneDeskPet-uninstall-<随机>.exe 后重启，
        // 让临时副本去删除安装目录（含卸载器自身文件）。
        // 用 Win32 CreateProcess(DETACHED_PROCESS) 启动，完全脱离父进程；
        // 比 Process.Start(UseShellExecute=true) 更可靠（后者会把 Shell 的
        // SEE_MASK_NOCLOSEPROCESS 句柄带回来，在部分环境下原进程会滞留不退出）。
        public static int RelaunchFromTemp(Options opt)
        {
            try
            {
                string self = Application.ExecutablePath;
                string stamp = Guid.NewGuid().ToString("N").Substring(0, 8);
                string tempExe = Path.Combine(Path.GetTempPath(), "AzurLaneDeskPet-uninstall-" + stamp + ".exe");
                File.Copy(self, tempExe, true);

                string dir = opt.InstallDir;
                if (dir == null || dir.Trim().Length == 0) dir = Path.GetDirectoryName(self);

                StringBuilder sb = new StringBuilder();
                sb.Append("--from-temp");
                sb.Append(opt.Silent ? " /S" : "");
                sb.Append(opt.DryRun ? " --dry-run" : "");
                sb.Append(opt.PurgeData ? " --purge-data" : " --keep-data");
                sb.Append(" \"--dir=").Append(dir.TrimEnd('\\')).Append("\"");
                if (opt.Report.Length > 0) sb.Append(" \"--report=").Append(opt.Report).Append("\"");

                int childPid = Native.SpawnDetached(tempExe, sb.ToString(), Path.GetDirectoryName(tempExe));
                if (childPid == 0) throw new Exception("CreateProcess 失败，错误码 " + Native.LastError());
                Log.Write("已搬迁到临时副本：" + tempExe + "（pid " + childPid + "），原进程即将退出");

                // 在临时副本完成扫描/确认之前，原进程必须已经释放映像锁；
                // 这里稍等片刻让子进程站稳，然后返回，由 Main 正常退出。
                System.Threading.Thread.Sleep(350);
                Log.Write("原进程退出（退出码 0）");
                return Const.ExitOk;
            }
            catch (Exception ex)
            {
                Log.Write("搬迁失败：" + ex.Message);
                if (!opt.Silent)
                {
                    try
                    {
                        MessageBox.Show("卸载程序无法搬迁到临时目录：" + ex.Message + Environment.NewLine
                            + "将继续就地卸载（安装目录可能残留本程序自身的文件）。",
                            Const.Product + " 卸载程序", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    catch (Exception) { }
                    return Const.ExitOk;  // 就地继续
                }
                return Const.ExitFailed;
            }
        }
    }

    // ================================================================== 引擎
    // files.json 解析结果（卸载白名单）
    internal class FileList
    {
        public string Path = "";
        public readonly List<string> Files = new List<string>();
        public readonly List<string> Dirs = new List<string>();
        public bool HasMarkerEntry;
    }

    internal class Engine
    {
        private readonly Options _opt;
        private readonly Action<string> _log;
        private readonly StringBuilder _steps = new StringBuilder();

        public string InstallDir = "";
        public string DataDir = Const.DataDir;
        public string CustomDataDir = "";

        /// <summary>数据目录是否被环境变量（沙箱）覆盖 —— 用于禁止顺手删除用户真实数据目录。</summary>
        public static bool DataDirOverridden
        {
            get
            {
                try
                {
                    string env = Environment.GetEnvironmentVariable("AZURLANEDESK_PET_DATA");
                    return !string.IsNullOrEmpty(env);
                }
                catch (Exception) { return false; }
            }
        }
        public List<string> Links = new List<string>();
        public List<string> Targets = new List<string>();
        public List<string> Failed = new List<string>();
        // 安全模型：保留（非本产品）与安全校验拒绝两类，都要在报告里列清楚
        public List<string> KeptItems = new List<string>();
        public List<string> BlockedItems = new List<string>();
        public FileList Manifest;              // null = 保守模式
        public bool InstallDirRefused;         // 安装目录被安全校验拒绝
        public bool SelfCleanupPending;        // 卸载器自身副本需在退出后自删
        public string SelfCleanupPath = "";
        public int DeletedFiles;
        public int DeletedDirs;
        public int DeletedLinks;
        public int DeletedRegKeys;

        public Engine(Options opt, Action<string> log)
        {
            _opt = opt;
            _log = log;
        }

        public string Steps { get { return _steps.ToString(); } }

        private void Step(string s) { _steps.AppendLine(s); _log(s); }

        // --------------------------------------------------------- 探测目标
        public void Scan()
        {
            InstallDir = ResolveInstallDir();
            Links.Clear();
            Targets.Clear();

            Step("[1/7] 安装目录：" + InstallDir);
            Step("      用户数据目录：" + DataDir);

            // install.json 清单
            string manifest = Path.Combine(InstallDir, Const.InstallManifest);
            if (File.Exists(manifest))
            {
                try
                {
                    string json = File.ReadAllText(manifest, Encoding.UTF8);
                    string d = JsonReadString(json, "dataDir");
                    if (d.Length > 0)
                    {
                        d = Environment.ExpandEnvironmentVariables(d);
                        if (!Same(d, DataDir)) CustomDataDir = d;
                    }
                    foreach (string s in JsonReadArray(json, "shortcuts")) AddLink(s);
                    string u = JsonReadString(json, "uninstaller");
                    if (u.Length > 0) Step("      清单记录的卸载器：" + u);
                    string flName = JsonReadString(json, "files");
                    string mk = JsonReadString(json, "marker");
                    string pre = JsonReadString(json, "preexisting");
                    Step("      已读取清单：" + manifest);
                    if (flName.Length > 0) Step("      文件清单字段：" + flName);
                    if (mk.Length > 0) Step("      安装标记字段：" + mk);
                    if (pre.Length > 0) Step("      安装前已存在条目数：" + pre);
                }
                catch (Exception ex) { Step("[!] 读取 install.json 失败（继续）：" + ex.Message); }
            }
            else
            {
                Step("      · 未发现 install.json（可能是手动删除或非本安装器安装）");
            }

            // files.json：卸载白名单
            Manifest = LoadFileList(InstallDir);
            if (Manifest != null)
            {
                Step("      ✓ 已读取文件清单：" + Manifest.Path
                    + "（" + Manifest.Files.Count + " 个文件 / " + Manifest.Dirs.Count + " 个目录）");
                Step("        → 清单模式：只删清单内的文件，其余一律保留");
            }
            else
            {
                Step("      · 没有可用的 files.json → 保守模式：只删本产品已知文件，绝不整目录递归删除");
                Blocked("files.json", "缺失，已退化为保守模式（不会删除未知文件）");
            }

            // 安装标记
            if (File.Exists(Path.Combine(InstallDir, Const.InstallMarker)))
                Step("      ✓ 安装标记存在：" + Const.InstallMarker);
            else
                Step("      · 未发现安装标记：" + Const.InstallMarker + "（可能是旧版本安装或共用目录）");

            // 安装目录本身的安全性
            string dirWhy = SafetyCheck(InstallDir);
            if (dirWhy.Length > 0)
            {
                Step("      ✗ 安装目录未通过安全校验，将拒绝任何删除：" + InstallDir + "（" + dirWhy + "）");
                Blocked(InstallDir, "安全校验：" + dirWhy);
            }
            else if (IsReparsePoint(InstallDir))
            {
                Step("      ✗ 安装目录是重解析点（junction / 符号链接），将拒绝删除：" + InstallDir);
                Blocked(InstallDir, "安装目录是重解析点，不跟随也不删除");
            }

            // 开始菜单快捷方式
            AddLink(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs\" + Const.ShortcutName));
            AddLink(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Const.ShortcutName));
            AddLink(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Const.ShortcutName));
            string pub = Environment.GetEnvironmentVariable("PUBLIC");
            if (string.IsNullOrEmpty(pub)) pub = @"C:\Users\Public";
            AddLink(Path.Combine(pub, @"Desktop\" + Const.ShortcutName));

            // 自定义数据目录（从 config.json 里再抓一次，防止 install.json 丢失）
            try
            {
                string cfg = Path.Combine(DataDir, "config.json");
                if (File.Exists(cfg))
                {
                    string json = File.ReadAllText(cfg, Encoding.UTF8);
                    string d = JsonReadString(json, "dataDir");
                    if (d.Length > 0)
                    {
                        d = Environment.ExpandEnvironmentVariables(d);
                        if (!Same(d, DataDir) && !Same(d, CustomDataDir)) CustomDataDir = d;
                    }
                }
            }
            catch (Exception ex) { Step("[!] 解析 config.json 失败（继续）：" + ex.Message); }
            if (CustomDataDir.Length > 0) Step("      自定义数据目录（config.json dataDir）：" + CustomDataDir);

            // 目标清单
            Targets.Add("DIR " + InstallDir + (Manifest != null ? "（清单模式）" : "（保守模式）"));
            Targets.Add("DIR " + DataDir + "（仅已知条目，需通过数据目录身份校验）");
            if (CustomDataDir.Length > 0)
            {
                string why = SafetyCheck(CustomDataDir);
                if (why.Length == 0) Targets.Add("DIR " + CustomDataDir + "（仅已知条目）");
                else
                {
                    Step("[!] 自定义数据目录被安全校验拒绝，不会删除：" + CustomDataDir + "（" + why + "）");
                    Blocked(CustomDataDir, "安全校验：" + why);
                }
            }
            foreach (string l in Links) Targets.Add("LNK " + l);
            Targets.Add("REG HKCU\\" + Const.RegUninstallKey);
            Targets.Add("REG HKCU\\" + Const.RegProductKey);
            if (KeptItems.Count > 0)
            {
                Step("      以下条目不属于本产品，卸载时会保留：");
                foreach (string k in KeptItems) Step("        · " + k);
            }
        }

        // ------------------------------------------------------------- 主流程
        public int Run(bool interactive)
        {
            Scan();

            // ② 结束主程序
            Step("[2/7] 结束正在运行的 " + Const.ExeName);
            string taskkillNote = KillPet();

            if (_opt.DryRun)
            {
                Step("[3/7] （dry-run）不写盘：本应删除以下内容");
                foreach (string t in Targets) Step("      - " + t);
                Step("[4/7] （dry-run）本应"
                    + (_opt.PurgeData ? "删除用户数据目录：" + DataDir + (CustomDataDir.Length > 0 ? " 以及自定义目录 " + CustomDataDir : "") : "保留用户数据目录（--keep-data）"));
                Step("[5/7] （dry-run）本应删除注册表项 HKCU\\" + Const.RegUninstallKey + " 与 HKCU\\" + Const.RegProductKey);
                Step("[6/7] （dry-run）本应删除自身临时副本");
                Step("[7/7] （dry-run）本应清理 %TEMP% 下桌宠相关日志与临时文件，完成，exit=0");
                Step("RESULT=OK(dry-run)");
                return Const.ExitOk;
            }

            // ③ 安装目录（清单模式：只删清单内条目；保守模式：只删已知自家文件）
            Step("[3/7] 清理安装目录（只删本产品清单内的文件，其余一律保留）");
            if (Directory.Exists(InstallDir))
            {
                string whyDir = SafetyCheck(InstallDir);
                InstallDirRefused = (whyDir.Length > 0);
                if (InstallDirRefused)
                {
                    Step("      ✗ 安全校验拒绝处理该安装目录，已放弃全部删除动作：" + InstallDir);
                    Step("        原因：" + whyDir);
                    Blocked(InstallDir, "安全校验：" + whyDir);
                }
                else
                {
                    bool removed = RemoveProductTree(InstallDir, Manifest);
                    if (!removed && Directory.Exists(InstallDir))
                    {
                        Step("      · 安装目录未被删除（含保留文件或文件被占用），详见下方清单");
                    }
                }
            }
            else Step("      · 安装目录不存在，跳过");

            // ④ 用户数据目录（必须通过身份校验；只删已知条目）
            if (_opt.PurgeData && !InstallDirRefused)
            {
                Step("[4/7] 清理用户数据目录（角色素材 / 台词 / 图片 / 语音 / 设置）");
                RemoveDataDir(DataDir, "默认数据目录");
                if (CustomDataDir.Length > 0)
                {
                    if (Same(CustomDataDir, DataDir))
                    {
                        Step("      自定义数据目录与默认一致，跳过重复处理");
                    }
                    else if (DataDirOverridden && Same(CustomDataDir, Const.RealUserDataDir))
                    {
                        // 沙箱保护（v1.0.8）：脚本用 AZURLANEDESK_PET_DATA 指定数据目录时，
                        // 绝不因为清单里残留的旧路径去删**用户真实数据目录**。
                        Step("      ✗ 清单里的自定义数据目录是用户真实数据目录，沙箱模式下拒绝删除：" + CustomDataDir);
                        Blocked(CustomDataDir, "沙箱模式（AZURLANEDESK_PET_DATA）下不允许删除真实用户数据目录");
                    }
                    else if (SafetyCheck(CustomDataDir).Length > 0)
                    {
                        string cw = SafetyCheck(CustomDataDir);
                        Step("      ✗ 自定义数据目录未通过安全校验，拒绝删除：" + CustomDataDir + "（" + cw + "）");
                        Blocked(CustomDataDir, "安全校验：" + cw);
                    }
                    else
                    {
                        Step("      自定义数据目录：" + CustomDataDir);
                        RemoveDataDir(CustomDataDir, "自定义数据目录");
                    }
                }
            }
            else if (!_opt.PurgeData)
            {
                Step("[4/7] 按 --keep-data 保留用户数据目录：" + DataDir);
            }

            // ⑤ 快捷方式
            Step("[5/7] 删除快捷方式");
            foreach (string l in Links)
            {
                if (DeleteFile(l)) { DeletedLinks++; Step("      ✓ " + l); }
            }

            // ⑥ 注册表
            Step("[6/7] 删除注册表项");
            if (DeleteRegKey(Const.RegUninstallKey)) Step("      ✓ HKCU\\" + Const.RegUninstallKey);
            if (DeleteRegKey(Const.RegProductKey)) Step("      ✓ HKCU\\" + Const.RegProductKey);

            // ⑦ 自身与临时文件
            Step("[7/7] 清理临时文件与自身");
            CleanupTemp(interactive);

            // 安装目录若残留「清单内文件」（多为被占用），安排安全延迟删除；
            // 安全校验拒绝 / 保守模式 / 目录本身是重解析点 → 一律不安排。
            if (Directory.Exists(InstallDir) && !InstallDirRefused && !IsReparsePoint(InstallDir))
            {
                List<string> stillListed = new List<string>();
                if (Manifest != null)
                {
                    foreach (string rel in Manifest.Files)
                    {
                        string full = ResolveInside(InstallDir, rel);
                        if (full != null && File.Exists(full)) stillListed.Add(rel);
                    }
                }
                if (stillListed.Count > 0)
                {
                    Step("      · 安装目录仍有 " + stillListed.Count + " 个清单内的文件未删除（可能被占用），安排安全延迟删除");
                    ScheduleDelayedDelete(InstallDir, Manifest);
                }
                else if (!IsEmptyDir(InstallDir))
                {
                    Step("      · 安装目录保留（内含非本产品文件），不做任何延迟删除：" + InstallDir);
                }
            }
            Step("");
            Step("RESULT: 删除文件 " + DeletedFiles + " 个 / 删除目录 " + DeletedDirs
                + " 个 / 快捷方式 " + DeletedLinks + " 个 / 注册表项 " + DeletedRegKeys + " 个；未能删除 " + Failed.Count + " 项");
            if (taskkillNote.Length > 0) Step(taskkillNote);
            Step("Kept=" + KeptItems.Count + " Blocked=" + BlockedItems.Count);
            Step(Failed.Count == 0 ? "CLEAN=OK" : "CLEAN=PARTIAL");
            return Const.ExitOk;
        }

        private string ResolveInstallDir()
        {
            string d = _opt.InstallDir;
            if (d == null) d = "";
            d = d.Trim().Trim('"').Trim();
            if (d.Length == 0) d = Path.GetDirectoryName(Application.ExecutablePath);
            try { d = Path.GetFullPath(d); } catch (Exception) { }
            return d.TrimEnd('\\');
        }

        // --------------------------------------------------------- 进程处理
        public static bool PetRunning()
        {
            try { return Process.GetProcessesByName("AzurLaneDeskPet").Length > 0; }
            catch (Exception) { return false; }
        }

        public string KillPet()
        {
            if (!PetRunning())
            {
                Step("      · 主程序未在运行");
                return "";
            }
            // 先温和关闭（CloseMainWindow），2 秒后再强杀
            try
            {
                foreach (Process p in Process.GetProcessesByName("AzurLaneDeskPet"))
                {
                    try { p.CloseMainWindow(); } catch (Exception) { }
                }
            }
            catch (Exception) { }
            System.Threading.Thread.Sleep(1200);
            if (!PetRunning())
            {
                Step("      ✓ 主程序已温和退出");
                return "主程序为温和关闭";
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("taskkill.exe", "/IM " + Const.ExeName + " /F /T");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string outp = p.StandardOutput.ReadToEnd();
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    Log.Write("taskkill 退出码 " + p.ExitCode + " " + outp + err);
                }
            }
            catch (Exception ex) { Step("[!] taskkill 调用失败：" + ex.Message); }
            System.Threading.Thread.Sleep(400);
            if (PetRunning())
            {
                Step("[!] 主程序仍在运行，个别文件可能删不掉");
                Failed.Add("仍然在运行：" + Const.ExeName);
                return "主程序可能仍在运行";
            }
            Step("      ✓ 已强制结束主程序");
            return "主程序被强制结束";
        }

        // --------------------------------------------------------- 删除工具
        // 安全模型：只删「清单里记录的、确实位于安装目录内、且不是重解析点」的文件；
        // 目录只在「清单内且已空」时才删；任何情况下都不做整目录递归删除。
        private bool IsReparsePoint(string path)
        {
            try
            {
                FileAttributes a = File.GetAttributes(path);
                return (a & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch (Exception) { return false; }
        }

        // 从 root 到 full 的**任一祖先部件**（不含 full 自身）是否为重解析点。
        // 这是防穿透的关键：即使清单里点名了 "linked\x.txt"，只要 linked 是 junction，
        // 该条目就绝不能删（否则会删到 junction 指向的外部数据）。
        public static bool HasReparseAncestor(string root, string full)
        {
            try
            {
                string r = Path.GetFullPath(root).TrimEnd('\\');
                string f = Path.GetFullPath(full).TrimEnd('\\');
                if (f.Length <= r.Length + 1) return false;
                string rel = f.Substring(r.Length).TrimStart('\\');
                string[] parts = rel.Split('\\');
                string cur = r;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    if (parts[i].Length == 0) continue;
                    cur = cur + "\\" + parts[i];
                    try
                    {
                        if (!Directory.Exists(cur) && !File.Exists(cur)) return false;
                        if ((File.GetAttributes(cur) & FileAttributes.ReparsePoint) != 0) return true;
                    }
                    catch (Exception) { return true; }  // 读不到属性 → 保守拒绝
                }
                return false;
            }
            catch (Exception) { return true; }          // 解析失败 → 保守拒绝
        }

        // 相对路径解析 + 越界校验：任何 .. 逃逸或落到根目录外的路径一律拒绝
        private string ResolveInside(string root, string rel)
        {
            try
            {
                if (rel == null) return null;
                string r = rel.Trim().Trim('"').TrimStart('\\', '/');
                if (r.Length == 0) return null;
                string full = Path.GetFullPath(Path.Combine(root, r));
                if (!PathInside(root, full)) return null;
                if (Same(full, root)) return null;
                return full;
            }
            catch (Exception) { return null; }
        }

        public static bool PathInside(string root, string full)
        {
            try
            {
                string a = Path.GetFullPath(root).TrimEnd('\\');
                string b = Path.GetFullPath(full).TrimEnd('\\');
                if (b.Length <= a.Length) return false;
                if (string.Compare(b, 0, a, 0, a.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
                return b[a.Length] == '\\';
            }
            catch (Exception) { return false; }
        }

        private bool IsEmptyDir(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return false;
                return Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir).Length == 0;
            }
            catch (Exception) { return false; }
        }

        // 清单内、已空 → 删除目录（不带递归）
        private bool DeleteEmptyDirIfListed(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return false;
                if (!IsEmptyDir(dir)) return false;
                Directory.Delete(dir, false);
                DeletedDirs++;
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("删除空目录失败 " + dir + "：" + ex.Message);
                return false;
            }
        }

        private void Keep(string path, string why)
        {
            string line = path + (why.Length > 0 ? "（" + why + "）" : "");
            if (!KeptItems.Contains(line)) KeptItems.Add(line);
        }

        private void Blocked(string path, string why)
        {
            string line = path + "（" + why + "）";
            if (!BlockedItems.Contains(line)) BlockedItems.Add(line);
            Log.Write("安全校验拒绝：" + line);
        }

        // 读 files.json（卸载白名单）。返回 null 表示没有清单 → 保守模式。
        public FileList LoadFileList(string installDir)
        {
            try
            {
                string p = Path.Combine(installDir, Const.FileListManifest);
                if (!File.Exists(p)) return null;
                string json = File.ReadAllText(p, Encoding.UTF8);
                FileList fl = new FileList();
                fl.Path = p;
                foreach (string s in JsonReadArray(json, "files")) fl.Files.Add(NormalizeRel(s));
                foreach (string s in JsonReadArray(json, "dirs")) fl.Dirs.Add(NormalizeRel(s));
                fl.HasMarkerEntry = fl.Files.Contains(Const.InstallMarker);
                if (fl.Files.Count == 0 && fl.Dirs.Count == 0) return null;
                return fl;
            }
            catch (Exception ex)
            {
                Log.Write("读取 files.json 失败：" + ex.Message);
                return null;
            }
        }

        private static string NormalizeRel(string s)
        {
            if (s == null) return "";
            return s.Trim().Trim('"').TrimStart('\\', '/').Replace('/', '\\');
        }

        // 保守模式下的「自家顶层文件」白名单
        private static readonly string[] OwnTopFiles = new string[]
        {
            "AzurLaneDeskPet.exe", "卸载-灵工桌宠.exe", "使用说明.txt",
            "install.json", "files.json", ".azurlandeskpet-install"
        };

        // 只按清单删安装目录里的本产品文件；返回 true = 安装目录最终被删除
        private bool RemoveProductTree(string dir, FileList fl)
        {
            if (dir == null || dir.Trim().Length == 0) { Step("      · 安装目录为空，跳过"); return false; }
            if (!Directory.Exists(dir)) { Step("      · 不存在，跳过：" + dir); return false; }

            // ① 安全检查：拒绝 → 立即返回，绝不安排任何延迟删除
            string why = SafetyCheck(dir);
            if (why.Length > 0)
            {
                Step("      ✗ 安全校验拒绝删除：" + dir + "（" + why + "）");
                Step("        已放弃全部删除动作，不会安排任何延迟删除。");
                Blocked(dir, "安全校验：" + why);
                return false;
            }
            if (IsReparsePoint(dir))
            {
                Step("      ✗ 安装目录本身是重解析点（junction / 符号链接），拒绝删除：" + dir);
                Blocked(dir, "安装目录是重解析点，不跟随也不删除");
                return false;
            }

            bool manifestMode = (fl != null && fl.Files.Count > 0);
            Step("      删除模式：" + (manifestMode
                ? "清单模式（files.json，" + fl.Files.Count + " 个文件 / " + fl.Dirs.Count + " 个目录）"
                : "保守模式（无 files.json，只删本产品已知文件）"));

            List<string> filesToDelete = new List<string>();
            List<string> dirsToDelete = new List<string>();
            List<string> reparseHits = new List<string>();

            if (manifestMode)
            {
                foreach (string rel in fl.Files)
                {
                    string full = ResolveInside(dir, rel);
                    if (full == null)
                    {
                        Blocked(rel, "清单条目越界或路径非法，已忽略");
                        continue;
                    }
                    // 祖先里有重解析点 → 一律拒绝（防穿透到外部目录）
                    if (HasReparseAncestor(dir, full))
                    {
                        reparseHits.Add(full);
                        continue;
                    }
                    if (!File.Exists(full))
                    {
                        if (IsReparsePoint(full)) reparseHits.Add(full);
                        continue;
                    }
                    if (IsReparsePoint(full)) { reparseHits.Add(full); continue; }
                    filesToDelete.Add(full);
                }
                foreach (string rel in fl.Dirs)
                {
                    string full = ResolveInside(dir, rel);
                    if (full == null) { Blocked(rel, "清单目录越界或路径非法，已忽略"); continue; }
                    if (HasReparseAncestor(dir, full)) { reparseHits.Add(full); continue; }
                    if (Directory.Exists(full)) dirsToDelete.Add(full);
                }
            }
            else
            {
                foreach (string name in OwnTopFiles) filesToDelete.Add(Path.Combine(dir, name));
                // assets 子目录：仅当含安装标记 / 自身标记 / builtin\ui\dialog-frame.png 时才按自家目录处理
                string assets = Path.Combine(dir, "assets");
                bool assetsOwned = Directory.Exists(assets) && (
                    File.Exists(Path.Combine(assets, Const.InstallMarker))
                    || File.Exists(Path.Combine(assets, "assets.json"))
                    || File.Exists(Path.Combine(assets, @"builtin\ui\dialog-frame.png"))
                    || File.Exists(Path.Combine(assets, @"builtin\素材清单.md")));
                if (assetsOwned)
                {
                    CollectOwnedTree(assets, dir, filesToDelete, dirsToDelete, reparseHits);
                }
                else if (Directory.Exists(assets))
                {
                    Keep("assets\\", "保守模式下无法确认是本产品目录，整块保留");
                }
            }

            foreach (string r in reparseHits)
            {
                Step("      · 跳过重解析点（不跟随、不删除）：" + r);
                Blocked(r, "路径含重解析点（junction / 符号链接），已跳过不穿透");
            }
            if (reparseHits.Count > 0)
                Step("      共跳过 " + reparseHits.Count + " 个重解析点相关条目（外部数据不会被触及）");

            // ② 逐个删文件（先清只读位；文件被占用时留给延迟删除处理）
            List<string> notDeleted = new List<string>();
            foreach (string f in filesToDelete)
            {
                if (DeleteFile(f)) { DeletedFiles++; Step("      ✓ " + f); }
                else if (File.Exists(f)) notDeleted.Add(f);
            }

            // ②b 卸载器自身（本进程映像，Windows 上必然删不掉）：尝试删，失败则安排退出后自删
            try
            {
                string self = Application.ExecutablePath;
                string selfInDir = Path.Combine(dir, Const.UninstallerName);
                if (Same(self, selfInDir) && File.Exists(selfInDir))
                {
                    if (DeleteFile(selfInDir)) { DeletedFiles++; Step("      ✓ " + selfInDir); }
                    else
                    {
                        SelfCleanupPending = true;
                        SelfCleanupPath = selfInDir;
                        Step("      · 卸载器正在运行，安装目录内的副本将在退出后自删：" + selfInDir);
                    }
                }
            }
            catch (Exception) { }
            if (notDeleted.Count > 0) Step("      · 暂时删不掉（占用中）：" + notDeleted.Count + " 个文件，将安排延迟删除");

            // ③ 只删「清单内且已空」的目录，由深到浅
            dirsToDelete.Sort(delegate (string a, string b) { return b.Length.CompareTo(a.Length); });
            foreach (string d in dirsToDelete)
            {
                if (!Directory.Exists(d)) continue;
                if (IsReparsePoint(d)) { Step("      · 跳过重解析点目录：" + d); continue; }
                if (DeleteEmptyDirIfListed(d)) Step("      ✓ " + d);
            }

            // ④ 安装目录本身：只在已空时才删
            bool removed = false;
            if (!Directory.Exists(dir))
            {
                removed = true;
            }
            else if (IsEmptyDir(dir))
            {
                removed = DeleteEmptyDirIfListed(dir);
                if (removed) Step("      ✓ " + dir + "（已空，删除）");
            }

            // ⑤ 清点保留项（非本产品文件），并报告
            if (Directory.Exists(dir))
            {
                List<string> lf = new List<string>(), ld = new List<string>();
                Walk(dir, lf, ld, 0);
                int kept = 0;
                foreach (string f in lf)
                {
                    if (!File.Exists(f)) continue;
                    string rel = f.Substring(dir.Length).TrimStart('\\');
                    bool ours = manifestMode ? ContainsRel(fl.Files, rel) : false;
                    if (!ours) { Keep(rel, "非本产品文件，保留"); kept++; }
                }
                foreach (string d in ld)
                {
                    if (!Directory.Exists(d)) continue;
                    string rel = d.Substring(dir.Length).TrimStart('\\');
                    bool ours = manifestMode ? ContainsRel(fl.Dirs, rel) : false;
                    if (!ours) Keep(rel + "\\", "非本产品目录，保留");
                }
                if (kept > 0) Step("      · 目录非空，保留 " + kept + " 个非本产品文件（见「已保留」清单），安装目录保留");
                Step("      " + (removed ? "✓" : "!") + " 安装目录未删除：" + dir);
            }
            return removed;
        }

        private static bool ContainsRel(List<string> list, string rel)
        {
            string r = NormalizeRel(rel);
            foreach (string x in list) if (string.Compare(x, r, StringComparison.OrdinalIgnoreCase) == 0) return true;
            return false;
        }

        // 保守模式下遍历「自家目录」（跳过重解析点）
        private void CollectOwnedTree(string dir, string installRoot, List<string> files, List<string> dirs, List<string> reparse)
        {
            string[] fs;
            try { fs = Directory.GetFiles(dir); } catch (Exception) { fs = new string[0]; }
            foreach (string f in fs)
            {
                if (IsReparsePoint(f)) { reparse.Add(f); continue; }
                files.Add(f);
            }
            string[] ds;
            try { ds = Directory.GetDirectories(dir); } catch (Exception) { ds = new string[0]; }
            foreach (string d in ds)
            {
                if (IsReparsePoint(d)) { reparse.Add(d); continue; }
                dirs.Add(d);
                CollectOwnedTree(d, installRoot, files, dirs, reparse);
            }
        }

        // Walk：跳过重解析点，不穿透（供残留清点使用）
        private static void Walk(string dir, List<string> files, List<string> dirs, int depth)
        {
            if (depth > 64) return;
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) == 0) files.Add(f);
                }
            }
            catch (Exception) { }
            try
            {
                foreach (string d in Directory.GetDirectories(dir))
                {
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue; // 不穿透
                    dirs.Add(d);
                    Walk(d, files, dirs, depth + 1);
                }
            }
            catch (Exception) { }
        }

        // --------------------------------------------- 用户数据目录（仅删已知条目）
        // 数据目录身份校验：必须有 .azurlandeskpet-data 标记，或 config.json 里含 configVersion
        public static string DataDirRefusal(string dir)
        {
            try
            {
                if (dir == null || dir.Trim().Length == 0) return "路径为空";
                if (!Directory.Exists(dir)) return "";
                if (IsReparsePointStatic(dir)) return "该目录是重解析点（junction / 符号链接）";
                string marker = Path.Combine(dir, ".azurlandeskpet-data");
                if (File.Exists(marker)) return "";
                string cfg = Path.Combine(dir, "config.json");
                if (File.Exists(cfg))
                {
                    try
                    {
                        string json = File.ReadAllText(cfg, Encoding.UTF8);
                        if (json.IndexOf("configVersion", StringComparison.OrdinalIgnoreCase) >= 0) return "";
                    }
                    catch (Exception) { }
                }
                if (File.Exists(Path.Combine(dir, "files.json"))) return "";
                return "没有 .azurlandeskpet-data 标记，config.json 里也没有 configVersion —— 无法确认是本产品数据目录";
            }
            catch (Exception ex) { return "校验异常：" + ex.Message; }
        }

        private static bool IsReparsePointStatic(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (Exception) { return false; }
        }

        // 数据目录：只删已知条目，其它顶层条目保留
        private void RemoveDataDir(string dir, string label)
        {
            if (dir == null || dir.Trim().Length == 0) return;
            if (!Directory.Exists(dir)) { Step("      · " + label + " 不存在，跳过：" + dir); return; }

            string why = SafetyCheck(dir);
            if (why.Length > 0)
            {
                Step("      ✗ 安全校验拒绝删除 " + label + "：" + dir + "（" + why + "）");
                Blocked(dir, "安全校验：" + why);
                return;
            }
            string dataWhy = DataDirRefusal(dir);
            if (dataWhy.Length > 0)
            {
                Step("      ✗ 无法确认是本产品数据目录，拒绝删除：" + dir);
                Step("        原因：" + dataWhy);
                Step("        请确认后手动删除（本程序不会碰它）。");
                Blocked(dir, "数据目录身份校验未通过：" + dataWhy);
                return;
            }

            Step("      ✓ 数据目录身份校验通过（" + label + "）：" + dir);

            // 已知文件
            string[] knownFiles = new string[] { "config.json", "config.json.bak", ".azurlandeskpet-data", "files.json", "使用说明.txt" };
            foreach (string name in knownFiles)
            {
                string f = Path.Combine(dir, name);
                if (!File.Exists(f)) continue;
                if (IsReparsePoint(f)) { Blocked(f, "重解析点，已跳过不穿透"); continue; }
                if (DeleteFile(f)) { DeletedFiles++; Step("      ✓ " + f); }
            }
            // 坏配置留档（config.json.bad-时间戳）同样是本产品文件
            try
            {
                foreach (string f in Directory.GetFiles(dir, "config.json.bad-*"))
                {
                    if (IsReparsePoint(f)) { Blocked(f, "重解析点，已跳过不穿透"); continue; }
                    if (DeleteFile(f)) { DeletedFiles++; Step("      ✓ " + f); }
                }
            }
            catch (Exception) { }

            // 已知子目录
            string[] knownDirs = new string[] { "logs", "characters" };
            foreach (string name in knownDirs)
            {
                string d = Path.Combine(dir, name);
                if (!Directory.Exists(d)) continue;
                if (!PathInside(dir, d)) { Blocked(d, "越界"); continue; }
                if (IsReparsePoint(d)) { Step("      · 跳过重解析点目录（不跟随、不删除）：" + d); Blocked(d, "重解析点，已跳过不穿透"); continue; }
                if (HasReparseAncestor(dir, d)) { Step("      · 跳过（路径含重解析点）：" + d); Blocked(d, "路径含重解析点"); continue; }
                RemoveOwnedTree(d);
            }
            // files.json 若在数据目录里也一并清理（仅当它确实是清单文件）
            try
            {
                string df = Path.Combine(dir, Const.FileListManifest);
                if (File.Exists(df) && !IsReparsePoint(df) && !HasReparseAncestor(dir, df))
                {
                    string txt = File.ReadAllText(df, Encoding.UTF8);
                    if (txt.IndexOf("AzurLaneDeskPet", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (DeleteFile(df)) { DeletedFiles++; Step("      ✓ " + df); }
                    }
                    else Keep(df, "内容不像本产品清单，保留");
                }
            }
            catch (Exception) { }

            // 顶层其它条目：保留并报告
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    string rel = Path.GetFileName(f);
                    bool known = false;
                    foreach (string k in knownFiles) if (string.Compare(k, rel, StringComparison.OrdinalIgnoreCase) == 0) known = true;
                    if (!known) Keep(dir + "\\" + rel, "非本产品已知数据，保留");
                }
                foreach (string d in Directory.GetDirectories(dir))
                {
                    string rel = Path.GetFileName(d);
                    bool known = false;
                    foreach (string k in knownDirs) if (string.Compare(k, rel, StringComparison.OrdinalIgnoreCase) == 0) known = true;
                    if (!known) Keep(d + "\\", "非本产品已知数据目录，保留");
                }
            }
            catch (Exception) { }

            // 数据目录本身：只在空时删
            if (Directory.Exists(dir))
            {
                if (IsEmptyDir(dir))
                {
                    if (DeleteEmptyDirIfListed(dir)) Step("      ✓ " + dir + "（已空，删除）");
                }
                else Step("      · 数据目录仍有保留内容，未删除：" + dir);
            }
        }

        // 删一棵「自家」子树：跳过重解析点，逐个文件删，最后只删空目录
        private void RemoveOwnedTree(string dir)
        {
            if (!Directory.Exists(dir)) return;
            if (IsReparsePoint(dir)) { Blocked(dir, "重解析点，已跳过不穿透"); return; }
            List<string> files = new List<string>(), dirs = new List<string>(), reparse = new List<string>();
            CollectOwnedTree(dir, dir, files, dirs, reparse);
            foreach (string r in reparse) { Step("      · 跳过重解析点（不跟随、不删除）：" + r); Blocked(r, "重解析点，已跳过不穿透"); }
            foreach (string f in files)
            {
                if (DeleteFile(f)) { DeletedFiles++; Step("      ✓ " + f); }
            }
            dirs.Sort(delegate (string a, string b) { return b.Length.CompareTo(a.Length); });
            foreach (string d in dirs)
            {
                if (Directory.Exists(d) && DeleteEmptyDirIfListed(d)) Step("      ✓ " + d);
            }
            if (Directory.Exists(dir) && DeleteEmptyDirIfListed(dir)) Step("      ✓ " + dir);
        }

        // 临时目录的整棵删除（仅用于 %TEMP% 下本产品自己的解包目录，路径已由调用方限定）
        private void RemoveTempTree(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                string temp = Path.GetTempPath().TrimEnd('\\');
                if (!PathInside(temp, dir)) { Blocked(dir, "不在 %TEMP% 内，拒绝整树删除"); return; }
                if (IsReparsePoint(dir)) { Blocked(dir, "重解析点，已跳过不穿透"); return; }
                List<string> files = new List<string>(), dirs = new List<string>(), reparse = new List<string>();
                CollectOwnedTree(dir, dir, files, dirs, reparse);
                foreach (string r in reparse) Blocked(r, "重解析点，已跳过不穿透");
                foreach (string f in files) { if (DeleteFile(f)) DeletedFiles++; }
                dirs.Sort(delegate (string a, string b) { return b.Length.CompareTo(a.Length); });
                foreach (string d in dirs) { if (Directory.Exists(d) && DeleteEmptyDirIfListed(d)) { } }
                if (Directory.Exists(dir) && IsEmptyDir(dir) && DeleteEmptyDirIfListed(dir)) { }
            }
            catch (Exception ex) { Log.Write("清理临时目录失败 " + dir + "：" + ex.Message); }
        }

        private bool DeleteFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                FileAttributes attr = File.GetAttributes(path);
                if ((attr & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                    File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
                File.Delete(path);
                return !File.Exists(path);
            }
            catch (Exception ex)
            {
                Log.Write("删除失败 " + path + "：" + ex.Message);
                return false;
            }
        }

        private bool DeleteRegKey(string subKey)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(subKey, false))
                {
                    if (k == null) return false;
                }
                Registry.CurrentUser.DeleteSubKeyTree(subKey, false);
                DeletedRegKeys++;
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("删除注册表项失败 " + subKey + "：" + ex.Message);
                Failed.Add("注册表 HKCU\\" + subKey + "（" + ex.Message + "）");
                return false;
            }
        }

        private void TryDeleteEmptyDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir) && Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir).Length == 0)
                {
                    Directory.Delete(dir, false);
                    DeletedDirs++;
                }
            }
            catch (Exception) { }
        }

        private void AddLink(string path)
        {
            if (path == null || path.Trim().Length == 0) return;
            string p = path.Trim().Trim('"');
            foreach (string x in Links) if (Same(x, p)) return;
            Links.Add(p);
        }

        // --------------------------------------------------------- 安全校验
        // 只保护「关键目录本身」与 Windows 目录（及其内部路径）。
        // 注意：关键目录的「子目录」不再一律拒绝 —— 用户完全可能把程序装进
        // %TEMP%\xxx、%LOCALAPPDATA%\Programs\xxx 这类位置；这些位置是否属于本产品，
        // 由「安装标记 + files.json 清单 + 逐条越界校验」来判断（见 RemoveProductTree）。
        // 返回空字符串 = 允许；否则返回拒绝原因
        public static string SafetyCheck(string path)
        {
            try
            {
                if (path == null) return "空路径";
                string p = path.Trim().Trim('"');
                if (p.Length == 0) return "空路径";
                p = Path.GetFullPath(p);
                string full = p.TrimEnd('\\');
                if (full.Length <= 3) return "盘符根目录";
                if (full.StartsWith("\\\\")) return "网络路径（UNC）";

                string upper = full.ToUpperInvariant();
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\').ToUpperInvariant();
                if (win.Length > 0 && (upper == win || upper.StartsWith(win + "\\"))) return "Windows 目录";

                string[] critical = new string[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    Path.GetTempPath(),
                    @"C:\Users",
                    @"C:\ProgramData"
                };
                foreach (string c in critical)
                {
                    if (string.IsNullOrEmpty(c)) continue;
                    string cc = c.TrimEnd('\\').ToUpperInvariant();
                    if (cc.Length == 0) continue;
                    if (upper == cc) return "系统/用户关键目录本身（" + c.TrimEnd('\\') + "）";
                    // C:\Users / C:\ProgramData 的直接子目录也保护（用户根目录/profile 本身）
                    if ((cc == @"C:\USERS" || cc == @"C:\PROGRAMDATA")
                        && upper.StartsWith(cc + "\\") && upper.Substring(cc.Length + 1).IndexOf('\\') < 0)
                        return "系统关键目录的直接子目录";
                }
                return "";
            }
            catch (Exception ex) { return "路径解析异常：" + ex.Message; }
        }

        // --------------------------------------------------------- 临时文件
        // 只清理「明确属于本产品」的临时项：
        //   · %TEMP%\AzurLaneDeskPet-*.log / .txt / .cmd 等本产品自建文件
        //   · %TEMP%\AzurLaneDeskPet-payload-<pid>（安装器解包目录，内含本产品文件）
        //   · %TEMP%\AzurLaneDeskPet-cleanup-*.cmd（安全延迟删除脚本）
        //   · %TEMP% 下「空的」alp-<...> 目录（仅当它已空时才删，绝不递归、绝不删有内容的）
        // 绝不按 alp-* 通配递归删除 —— 用户/测试可能用同名目录。
        private void CleanupTemp(bool interactive)
        {
            string temp = Path.GetTempPath();
            try
            {
                foreach (string f in Directory.GetFiles(temp, "AzurLaneDeskPet-*"))
                {
                    if (Same(f, Program.ReportPath)) { Step("      · 保留报告文件：" + f); continue; }
                    if (Same(f, Const.LogPath)) { Step("      · 保留本次日志：" + f); continue; }
                    if (DeleteFile(f)) { DeletedFiles++; Step("      ✓ " + f); }
                }
                foreach (string d in Directory.GetDirectories(temp, "AzurLaneDeskPet-payload-*"))
                {
                    RemoveTempTree(d);
                }
                // alp-* 只删「已空」的目录，绝不递归
                foreach (string d in Directory.GetDirectories(temp, "alp-*"))
                {
                    try
                    {
                        if (IsReparsePoint(d)) { Step("      · 跳过重解析点目录（不跟随）：" + d); continue; }
                        if (IsEmptyDir(d))
                        {
                            Directory.Delete(d, false);
                            DeletedDirs++;
                            Step("      ✓ " + d + "（空目录）");
                        }
                        else
                        {
                            Keep(d + "\\", "非本产品目录（%TEMP% 下同名但非本产品自建），保留");
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception ex) { Log.Write("清理临时文件异常：" + ex.Message); }

            // 自删除：先尝试直接删，失败则安排 cmd 延迟删除
            string self = Application.ExecutablePath;
            if (_opt.FromTemp && self.ToUpperInvariant().StartsWith(temp.ToUpperInvariant()))
            {
                if (!DeleteFile(self))
                {
                    Step("      · 卸载器临时副本正在运行，已安排退出后自删除：" + self);
                    ScheduleSelfDelete(self);
                }
                else Step("      ✓ 已删除临时副本：" + self);
            }
            // 安装目录里的卸载器副本同样需要等本进程退出后才能删
            if (SelfCleanupPending && SelfCleanupPath.Length > 0)
            {
                Step("      · 安装目录内的卸载器副本将在退出后自删：" + SelfCleanupPath);
                ScheduleSelfDelete(SelfCleanupPath);
            }
        }

        private void ScheduleSelfDelete(string self)
        {
            try
            {
                string bat = Path.Combine(Path.GetTempPath(), "AzurLaneDeskPet-selfdel-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".cmd");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("ping -n 3 127.0.0.1 > nul");
                sb.AppendLine("del /f /q \"" + self + "\" > nul 2>&1");
                sb.AppendLine("del /f /q \"" + Const.LogPath + "\" > nul 2>&1");
                sb.AppendLine("del /f /q \"%~f0\" > nul 2>&1");
                File.WriteAllText(bat, sb.ToString(), Encoding.Default);
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch (Exception ex) { Log.Write("安排自删除失败：" + ex.Message); }
        }

        // 安全版延迟删除：只对「清单内的文件」逐条 del，只对「清单内且已空」的目录 rd（不带 /s）。
        // 严禁 rd /s /q、del /s、rmdir /s；没有清单（保守模式）时完全不安排。
        private void ScheduleDelayedDelete(string dir, FileList fl)
        {
            try
            {
                if (fl == null || fl.Files.Count == 0)
                {
                    Log.Write("无 files.json 清单，保守模式下不安排任何延迟删除：" + dir);
                    return;
                }
                if (SafetyCheck(dir).Length > 0 || IsReparsePoint(dir)) return;

                List<string> pending = new List<string>();
                foreach (string rel in fl.Files)
                {
                    string full = ResolveInside(dir, rel);
                    if (full == null) continue;
                    if (HasReparseAncestor(dir, full)) continue;   // 防穿透
                    if (File.Exists(full)) pending.Add(full);
                }
                if (pending.Count == 0)
                {
                    Log.Write("没有需要延迟删除的清单内文件：" + dir);
                    return;
                }

                List<string> dirList = new List<string>();
                foreach (string rel in fl.Dirs)
                {
                    string full = ResolveInside(dir, rel);
                    if (full != null && Directory.Exists(full)) dirList.Add(full);
                }
                dirList.Sort(delegate (string a, string b) { return b.Length.CompareTo(a.Length); });
                if (!ContainsSame(dirList, dir)) dirList.Add(dir);

                string bat = Path.Combine(Path.GetTempPath(), "AzurLaneDeskPet-cleanup-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".cmd");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("ping -n 4 127.0.0.1 > nul");
                foreach (string f in pending) sb.AppendLine("del /f /q \"" + f + "\" > nul 2>&1");
                foreach (string d in dirList) sb.AppendLine("rd \"" + d + "\" > nul 2>&1");
                sb.AppendLine("del /f /q \"%~f0\" > nul 2>&1");
                File.WriteAllText(bat, sb.ToString(), Encoding.Default);
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
                Step("      已安排安全延迟删除（逐条 del + 仅删空目录 rd，共 " + pending.Count + " 个文件）");
                Log.Write("安全延迟删除脚本：" + bat + "（文件 " + pending.Count + " 个）");
            }
            catch (Exception ex) { Log.Write("安排延迟删除失败：" + ex.Message); }
        }

        private static bool ContainsSame(List<string> list, string path)
        {
            foreach (string x in list) if (Same(x, path)) return true;
            return false;
        }

        // --------------------------------------------------------- 极简 JSON
        public static string JsonReadString(string json, string key)
        {
            if (json == null) return "";
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
            if (!m.Success) return "";
            return JsUnescape(m.Groups[1].Value);
        }

        public static List<string> JsonReadArray(string json, string key)
        {
            List<string> list = new List<string>();
            if (json == null) return list;
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
            if (!m.Success) return list;
            foreach (Match it in Regex.Matches(m.Groups[1].Value, "\"((?:\\\\.|[^\"\\\\])*)\""))
            {
                string v = JsUnescape(it.Groups[1].Value);
                if (v.Length > 0) list.Add(v);
            }
            return list;
        }

        private static string JsUnescape(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char n = s[++i];
                if (n == 'n') sb.Append('\n');
                else if (n == 'r') sb.Append('\r');
                else if (n == 't') sb.Append('\t');
                else if (n == 'b') sb.Append('\b');
                else if (n == 'f') sb.Append('\f');
                else if (n == 'u' && i + 4 < s.Length)
                {
                    try
                    {
                        int code = Convert.ToInt32(s.Substring(i + 1, 4), 16);
                        sb.Append((char)code);
                        i += 4;
                    }
                    catch (Exception) { sb.Append(n); }
                }
                else sb.Append(n);
            }
            return sb.ToString();
        }

        public static bool Same(string a, string b)
        {
            if (a == null || b == null) return false;
            return string.Compare(a.Trim().TrimEnd('\\'), b.Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == 0;
        }
    }

    // ============================================================== 主题绘制
    internal static class Theme
    {
        public static readonly Color NavyTop = Color.FromArgb(0x0E, 0x1B, 0x33);
        public static readonly Color NavyBottom = Color.FromArgb(0x16, 0x30, 0x5C);
        public static readonly Color Gold = Color.FromArgb(0xE8, 0xC8, 0x6A);
        public static readonly Color GoldDim = Color.FromArgb(0x9A, 0x86, 0x46);
        public static readonly Color TextWhite = Color.FromArgb(0xF2, 0xF6, 0xFF);
        public static readonly Color TextDim = Color.FromArgb(0xA8, 0xBA, 0xD8);
        public static readonly Color PanelFill = Color.FromArgb(0x12, 0x25, 0x45);

        public static void PaintGradient(Graphics g, Rectangle r)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (LinearGradientBrush b = new LinearGradientBrush(
                new Rectangle(r.X, r.Y, Math.Max(1, r.Width), Math.Max(2, r.Height)), NavyTop, NavyBottom, 90f))
            {
                g.FillRectangle(b, r);
            }
        }

        public static void DrawGoldFrame(Graphics g, Rectangle r, bool thick)
        {
            using (Pen p = new Pen(Gold, thick ? 2f : 1f)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
            using (Pen p2 = new Pen(Color.FromArgb(70, Gold), 1f))
                g.DrawRectangle(p2, r.X + 2, r.Y + 2, Math.Max(0, r.Width - 5), Math.Max(0, r.Height - 5));
        }

        public static Font TitleFont(float size)
        {
            try { return new Font("宋体", size, FontStyle.Bold, GraphicsUnit.Point); }
            catch (Exception) { return new Font(FontFamily.GenericSerif, size, FontStyle.Bold); }
        }

        public static Font UiFont(float size, FontStyle style)
        {
            string[] names = new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimSun" };
            foreach (string n in names)
            {
                try
                {
                    Font f = new Font(n, size, style, GraphicsUnit.Point);
                    if (string.Compare(f.Name, n, StringComparison.OrdinalIgnoreCase) == 0) return f;
                    f.Dispose();
                }
                catch (Exception) { }
            }
            return new Font(FontFamily.GenericSansSerif, size, style);
        }
    }

    internal class GradientPanel : Panel
    {
        public GradientPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.NavyTop;
        }
        protected override void OnPaintBackground(PaintEventArgs e) { Theme.PaintGradient(e.Graphics, ClientRectangle); }
    }

    internal class FramedPanel : Panel
    {
        private readonly string _caption;
        public FramedPanel(string caption)
        {
            _caption = caption;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.PanelFill;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.PanelFill);
            Theme.DrawGoldFrame(g, new Rectangle(0, 0, Width, Height), false);
            if (_caption != null && _caption.Length > 0)
            {
                Font f = Theme.UiFont(9.5f, FontStyle.Bold);
                SizeF sz = g.MeasureString(_caption, f);
                using (SolidBrush bg = new SolidBrush(Theme.PanelFill)) g.FillRectangle(bg, 12, -1, (int)sz.Width + 16, 16);
                using (SolidBrush br = new SolidBrush(Theme.Gold)) g.DrawString(_caption, f, br, 16, -2);
            }
        }
    }

    internal class GoldButton : Button
    {
        private bool _hover;
        private readonly bool _primary;

        public GoldButton(bool primary)
        {
            _primary = primary;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Font = Theme.UiFont(10f, FontStyle.Bold);
            ForeColor = _primary ? Theme.NavyTop : Theme.Gold;
            BackColor = _primary ? Theme.Gold : Theme.PanelFill;
            Cursor = Cursors.Hand;
            Height = 34;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            Graphics g = pevent.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = _primary ? (_hover ? Color.FromArgb(0xF6, 0xDD, 0x92) : Theme.Gold)
                                  : (_hover ? Color.FromArgb(0x1D, 0x3B, 0x6E) : Theme.PanelFill);
            using (SolidBrush b = new SolidBrush(fill)) g.FillRectangle(b, ClientRectangle);
            using (Pen p = new Pen(_primary ? Color.FromArgb(0xFF, 0xEE, 0xBB) : Theme.Gold, 1.6f))
                g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            using (SolidBrush tb = new SolidBrush(_primary ? Theme.NavyTop : (_hover ? Color.White : Theme.Gold)))
            {
                StringFormat sf = new StringFormat();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(Text, Font, tb, new RectangleF(0, 0, Width, Height), sf);
                sf.Dispose();
            }
        }
    }

    internal class GhostLabel : Label
    {
        public GhostLabel(string text, Color color, float size, FontStyle style)
        {
            Text = text;
            ForeColor = color;
            BackColor = Color.Transparent;
            Font = Theme.UiFont(size, style);
            AutoSize = false;
        }
    }

    internal class GhostCheck : CheckBox
    {
        public GhostCheck(string text, bool @checked, Color color)
        {
            Text = text;
            Checked = @checked;
            ForeColor = color;
            BackColor = Color.Transparent;
            Font = Theme.UiFont(9.5f, FontStyle.Regular);
            AutoSize = false;
            Height = 22;
        }
    }

    // ================================================================== 界面
    internal class UninstallForm : Form
    {
        public int ResultCode = Const.ExitOk;
        public Engine LastEngine;
        private readonly Options _opt;
        private readonly StringBuilder _steps = new StringBuilder();
        public string Steps { get { return _steps.ToString(); } }

        private TextBox _log;
        private Label _status;
        private GoldButton _btnGo;
        private GoldButton _btnCancel;
        private GhostCheck _cbData;

        public UninstallForm(Options opt)
        {
            _opt = opt;
            BuildUi();
            Shown += delegate { PreScan(); };
        }

        private void BuildUi()
        {
            Text = Const.Product + " 卸载程序  v" + Const.Version;
            ClientSize = new Size(720, 520);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Icon = LoadEmbeddedIcon();
            BackColor = Theme.NavyTop;
            Font = Theme.UiFont(9.5f, FontStyle.Regular);

            GradientPanel root = new GradientPanel();
            root.Dock = DockStyle.Fill;
            Controls.Add(root);

            Label title = new GhostLabel(Const.Slogan, Theme.Gold, 20f, FontStyle.Bold);
            title.Font = Theme.TitleFont(20f);
            title.SetBounds(24, 14, 672, 36);
            title.TextAlign = ContentAlignment.MiddleCenter;
            root.Controls.Add(title);

            Label sub = new GhostLabel(Const.Product + "  ·  卸载向导 —— 将清除本机全部桌宠文件与设置", Theme.TextWhite, 10.5f, FontStyle.Regular);
            sub.SetBounds(24, 50, 672, 22);
            sub.TextAlign = ContentAlignment.MiddleCenter;
            root.Controls.Add(sub);

            Panel deco = new Panel();
            deco.SetBounds(24, 78, 672, 2);
            deco.BackColor = Theme.GoldDim;
            root.Controls.Add(deco);

            FramedPanel g1 = new FramedPanel("① 清理范围");
            g1.SetBounds(16, 94, 688, 118);

            GhostLabel l1 = new GhostLabel("安装目录：", Theme.TextWhite, 9.5f, FontStyle.Bold);
            l1.SetBounds(16, 26, 84, 22);
            g1.Controls.Add(l1);

            GhostLabel v1 = new GhostLabel("", Theme.TextDim, 9f, FontStyle.Regular);
            v1.SetBounds(104, 26, 566, 22);
            v1.Name = "lblDir";
            g1.Controls.Add(v1);

            GhostLabel l2 = new GhostLabel("用户数据：", Theme.TextWhite, 9.5f, FontStyle.Bold);
            l2.SetBounds(16, 50, 84, 22);
            g1.Controls.Add(l2);

            GhostLabel v2 = new GhostLabel("", Theme.TextDim, 9f, FontStyle.Regular);
            v2.SetBounds(104, 50, 566, 22);
            v2.Name = "lblData";
            g1.Controls.Add(v2);

            _cbData = new GhostCheck("同时删除 %APPDATA%\\AzurLaneDeskPet 与自定义 dataDir（素材/台词/图片/设置）", true, Theme.TextWhite);
            _cbData.SetBounds(18, 76, 654, 22);
            g1.Controls.Add(_cbData);

            GhostLabel l3 = new GhostLabel("还会删除：开始菜单 / 桌面快捷方式、注册表卸载项 HKCU\\...\\Uninstall\\AzurLaneDeskPet、%TEMP% 下桌宠日志。",
                Theme.TextDim, 8.5f, FontStyle.Regular);
            l3.SetBounds(18, 96, 654, 18);
            g1.Controls.Add(l3);
            root.Controls.Add(g1);

            _btnGo = new GoldButton(true);
            _btnGo.Text = "开始卸载";
            _btnGo.SetBounds(20, 224, 200, 38);
            _btnGo.Click += delegate { DoUninstall(); };
            root.Controls.Add(_btnGo);

            GoldButton bLog = new GoldButton(false);
            bLog.Text = "查看日志";
            bLog.SetBounds(234, 224, 150, 38);
            bLog.Click += delegate { OpenPath(Const.LogPath); };
            root.Controls.Add(bLog);

            _btnCancel = new GoldButton(false);
            _btnCancel.Text = "取消";
            _btnCancel.SetBounds(550, 224, 150, 38);
            _btnCancel.Click += delegate { ResultCode = Const.ExitCancelled; Close(); };
            root.Controls.Add(_btnCancel);

            GhostLabel llog = new GhostLabel("卸载日志", Theme.Gold, 9.5f, FontStyle.Bold);
            llog.SetBounds(20, 270, 200, 20);
            root.Controls.Add(llog);

            _log = new TextBox();
            _log.SetBounds(20, 292, 680, 168);
            _log.ReadOnly = true;
            _log.Multiline = true;
            _log.ScrollBars = ScrollBars.Both;
            _log.WordWrap = false;
            _log.BorderStyle = BorderStyle.FixedSingle;
            _log.BackColor = Color.FromArgb(0x0A, 0x14, 0x26);
            _log.ForeColor = Theme.TextWhite;
            _log.Font = new Font("Consolas", 9f);
            root.Controls.Add(_log);

            _status = new GhostLabel("正在检查本机安装情况…", Theme.TextDim, 9f, FontStyle.Regular);
            _status.SetBounds(20, 468, 680, 22);
            root.Controls.Add(_status);
        }

        private static Icon LoadEmbeddedIcon()
        {
            try
            {
                Icon ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ico != null) return ico;
            }
            catch (Exception) { }
            return null;
        }

        private void AppendLine(string text)
        {
            _steps.AppendLine(text);
            if (_log == null) return;
            _log.AppendText(text + Environment.NewLine);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
            Application.DoEvents();
        }

        private void PreScan()
        {
            try
            {
                Engine eng = new Engine(_opt, AppendLine);
                eng.Scan();
                LastEngine = eng;
                _opt.InstallDir = eng.InstallDir;
                Control[] c1 = Controls.Find("lblDir", true);
                if (c1.Length > 0) c1[0].Text = eng.InstallDir + (Directory.Exists(eng.InstallDir) ? "" : "（不存在）");
                Control[] c2 = Controls.Find("lblData", true);
                if (c2.Length > 0) c2[0].Text = eng.DataDir + (Directory.Exists(eng.DataDir) ? "" : "（不存在）")
                    + (eng.CustomDataDir.Length > 0 ? "  + 自定义：" + eng.CustomDataDir : "");
                _status.Text = Engine.PetRunning() ? "检测到桌宠正在运行，卸载时会先结束它。" : "就绪 · 点「开始卸载」执行清理";
            }
            catch (Exception ex)
            {
                AppendLine("[!] 预扫描失败：" + ex.Message);
                _status.Text = "就绪";
            }
        }

        private void DoUninstall()
        {
            DialogResult dr = MessageBox.Show(this,
                "确定要卸载「" + Const.Product + "」并清除本机全部相关文件吗？" + Environment.NewLine + Environment.NewLine
                + "· 程序目录：" + _opt.InstallDir + Environment.NewLine
                + "· 用户数据：" + Const.DataDir + (_cbData.Checked ? "（将删除）" : "（保留）") + Environment.NewLine
                + "· 快捷方式与注册表卸载项（将删除）" + Environment.NewLine + Environment.NewLine
                + "此操作不可撤销。",
                "确认卸载", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (dr != DialogResult.Yes)
            {
                ResultCode = Const.ExitCancelled;
                AppendLine("[x] 用户取消了卸载");
                return;
            }

            _btnGo.Enabled = false;
            _btnCancel.Enabled = false;
            try
            {
                Options o = new Options();
                o.InstallDir = _opt.InstallDir;
                o.Silent = false;
                o.DryRun = _opt.DryRun;
                o.PurgeData = _cbData.Checked;
                o.Report = _opt.Report;
                o.FromTemp = true;   // 图形模式已是副本（或就地），不再搬迁
                o.NoTempClean = true;
                Engine eng = new Engine(o, AppendLine);
                LastEngine = eng;
                _status.Text = "正在卸载…";
                int code = eng.Run(true);
                ResultCode = code;

                StringBuilder msg = new StringBuilder();
                msg.AppendLine("已清除：" + Const.Product);
                msg.AppendLine();
                msg.AppendLine("共删除 " + eng.DeletedFiles + " 个文件 / " + eng.DeletedDirs + " 个目录");
                msg.AppendLine("快捷方式 " + eng.DeletedLinks + " 个，注册表项 " + eng.DeletedRegKeys + " 个");
                if (eng.Failed.Count > 0)
                {
                    msg.AppendLine();
                    msg.AppendLine("以下 " + eng.Failed.Count + " 项未能删除：");
                    for (int i = 0; i < eng.Failed.Count && i < 12; i++) msg.AppendLine("  · " + eng.Failed[i]);
                    if (eng.Failed.Count > 12) msg.AppendLine("  · …其余见日志");
                }
                else
                {
                    msg.AppendLine();
                    msg.AppendLine("本机已无残留。");
                }
                msg.AppendLine();
                msg.AppendLine("日志：" + Const.LogPath);
                _status.Text = eng.Failed.Count == 0 ? "卸载完成" : "卸载完成（有残留）";

                if (Program.ReportPath.Length > 0)
                {
                    try
                    {
                        StringBuilder sb = new StringBuilder();
                        sb.AppendLine("== " + Const.Product + " 卸载程序报告（图形模式） ==");
                        sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                        sb.AppendLine(Steps);
                        sb.AppendLine("exit=" + code);
                        File.WriteAllText(Program.ReportPath, sb.ToString(), new UTF8Encoding(false));
                    }
                    catch (Exception) { }
                }

                using (ResultDialog dlg = new ResultDialog(msg.ToString(), Const.LogPath))
                {
                    dlg.ShowDialog(this);
                }
                Close();
            }
            catch (Exception ex)
            {
                ResultCode = Const.ExitFailed;
                Log.Write("GUI 卸载异常：" + ex);
                AppendLine("[x] 卸载失败：" + ex.Message);
                _status.Text = "卸载失败";
                _btnGo.Enabled = true;
                _btnCancel.Enabled = true;
                MessageBox.Show(this, "卸载失败：" + ex.Message + Environment.NewLine + "日志：" + Const.LogPath,
                    Const.Product + " 卸载程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenPath(string path)
        {
            try
            {
                if (File.Exists(path)) Process.Start("notepad.exe", "\"" + path + "\"");
                else MessageBox.Show(this, "还不存在：" + path, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show(this, "打开失败：" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }

    // 卸载结果弹窗（自带「查看日志」按钮）
    internal class ResultDialog : Form
    {
        private readonly string _logPath;

        public ResultDialog(string message, string logPath)
        {
            _logPath = logPath;
            Text = Const.Product + " 卸载程序";
            ClientSize = new Size(560, 400);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Theme.NavyTop;
            Font = Theme.UiFont(9.5f, FontStyle.Regular);

            GradientPanel root = new GradientPanel();
            root.Dock = DockStyle.Fill;
            Controls.Add(root);

            GhostLabel t = new GhostLabel("卸载完成", Theme.Gold, 15f, FontStyle.Bold);
            t.Font = Theme.TitleFont(15f);
            t.SetBounds(20, 16, 520, 30);
            t.TextAlign = ContentAlignment.MiddleCenter;
            root.Controls.Add(t);

            TextBox box = new TextBox();
            box.SetBounds(20, 56, 520, 262);
            box.Multiline = true;
            box.ReadOnly = true;
            box.ScrollBars = ScrollBars.Vertical;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.BackColor = Color.FromArgb(0x0A, 0x14, 0x26);
            box.ForeColor = Theme.TextWhite;
            box.Font = Theme.UiFont(9.5f, FontStyle.Regular);
            box.Text = message.Replace("\n", Environment.NewLine);
            root.Controls.Add(box);

            GoldButton bLog = new GoldButton(false);
            bLog.Text = "查看日志";
            bLog.SetBounds(20, 332, 160, 38);
            bLog.Click += delegate { OpenLog(); };
            root.Controls.Add(bLog);

            GoldButton bOk = new GoldButton(true);
            bOk.Text = "确定";
            bOk.SetBounds(380, 332, 160, 38);
            bOk.Click += delegate { Close(); };
            root.Controls.Add(bOk);
        }

        private void OpenLog()
        {
            try
            {
                if (File.Exists(_logPath)) Process.Start("notepad.exe", "\"" + _logPath + "\"");
                else MessageBox.Show(this, "日志不存在：" + _logPath, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show(this, "打开失败：" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }

    // ============================================================ P/Invoke
    internal static class Native
    {
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        // ---------------------------------------------------------- 脱离式启动
        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(
            string lpApplicationName, string lpCommandLine,
            IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
            uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetLastError();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint DETACHED_PROCESS = 0x00000008;
        private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
        private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private const int STARTF_USESHOWWINDOW = 0x00000001;
        private const short SW_SHOWNORMAL = 1;

        // 启动一个与本进程完全脱离的新进程（不继承句柄、独立进程组），返回其 pid；失败返回 0
        public static int SpawnDetached(string exePath, string arguments, string workingDir)
        {
            try
            {
                STARTUPINFO si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
                si.dwFlags = STARTF_USESHOWWINDOW;
                si.wShowWindow = SW_SHOWNORMAL;
                PROCESS_INFORMATION pi = new PROCESS_INFORMATION();
                string cmd = "\"" + exePath + "\"" + (arguments.Length > 0 ? " " + arguments : "");
                bool ok = CreateProcess(exePath, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_UNICODE_ENVIRONMENT,
                    IntPtr.Zero, workingDir, ref si, out pi);
                if (!ok) return 0;
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                return pi.dwProcessId;
            }
            catch (Exception ex)
            {
                Log.Write("SpawnDetached 异常：" + ex.Message);
                return 0;
            }
        }

        public static uint LastError() { return GetLastError(); }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        // 带“不弹 UI / 不询问”的强力递归删除（可处理长路径）
        public static void SHDelete(string path)
        {
            try
            {
                SHFILEOPSTRUCT op = new SHFILEOPSTRUCT();
                op.wFunc = 3;         // FO_DELETE
                op.pFrom = path + "\0\0";
                op.fFlags = 0x0004 | 0x0010 | 0x0400; // NOCONFIRMATION | NOPROGRESS | NOERRORUI
                SHFileOperation(ref op);
            }
            catch (Exception) { }
        }
    }
}
