// ============================================================================
// 灵工桌宠 · 安装程序（AzurLaneDeskPet Setup）
// ----------------------------------------------------------------------------
// · 单文件 winexe，负载 payload.zip 以 /resource:payload.zip,payload.zip 内嵌
// · 免管理员权限，默认装到 %LOCALAPPDATA%\Programs\AzurLaneDeskPet，可自定义目录
// · WinForms 纯代码绘制界面（无 .resx），配色：深蓝渐变 + 柔金描边（圆润简约）
// · 写入 install.json / 注册表卸载项 / 开始菜单与桌面快捷方式
// · 支持 /S 静默、/D= 指定目录、--no-shortcuts、--no-launch、--dry-run、--report=
//
// 编译（系统自带 csc，C# 5 语法，UTF-8 源码需 /codepage:65001）：
//   csc.exe /nologo /target:winexe /codepage:65001
//           /out:"灵工桌宠-安装程序.exe" /win32icon:icon.ico
//           /resource:payload.zip,payload.zip
//           /r:System.Windows.Forms.dll /r:System.Drawing.dll
//           /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll
//           DeskPetSetup.cs
//
// 退出码：0 成功 / 2 用户取消 / 3 失败
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AzurLaneDeskPet.Setup
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
        public const string MarkerText = "AzurLaneDeskPet install marker v" + Version;
        public const string DataFolderName = "AzurLaneDeskPet";

        /// <summary>
        /// 默认用户数据目录。环境变量 AZURLANEDESK_PET_DATA 可覆盖 ——
        /// 验收脚本靠它把"数据目录"整条链路（安装 → 运行 → 卸载）都指到沙箱里，
        /// 这样卸载器绝不会去动 %APPDATA%\AzurLaneDeskPet 这个真实目录。
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
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DataFolderName);
            }
        }
        public const string PayloadResource = "payload.zip";
        public const string RegUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AzurLaneDeskPet";
        public const string RegProductKey = @"Software\AzurLaneDeskPet";

        public const int ExitOk = 0;
        public const int ExitCancelled = 2;
        public const int ExitFailed = 3;

        public static string LogPath
        {
            get { return Path.Combine(Path.GetTempPath(), "AzurLaneDeskPet-setup.log"); }
        }

        // 免管理员权限的默认安装目录
        public static string DefaultInstallDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Programs\AzurLaneDeskPet");
        }

        // 运行时 exe 所在目录
        public static string ExeDir
        {
            get
            {
                try { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
                catch (Exception) { return Environment.CurrentDirectory; }
            }
        }
    }

    // ==================================================================== 入口
    internal static class Program
    {
        private static readonly StringBuilder Report = new StringBuilder();
        private static string _reportPath = "";

        [STAThread]
        private static int Main(string[] args)
        {
            try { Native.SetProcessDPIAware(); }
            catch (Exception) { }

            Options opt;
            try { opt = Options.Parse(args); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("参数解析失败：" + ex.Message);
                return Const.ExitFailed;
            }
            _reportPath = opt.Report;

            // ---- 静默模式：绝不弹窗，一切写日志 + 报告 ----
            if (opt.Silent)
            {
                Log.Write("==== " + Const.Product + " 静默安装开始 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ====");
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
                FlushReport(opt, code);
                return code;
            }

            // ---- 图形模式 ----
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Log.Write("==== " + Const.Product + " 图形安装开始 ====");
                SetupForm form = new SetupForm(opt);
                Application.Run(form);
                Report.Append(form.Steps);
                FlushReport(opt, form.ResultCode);
                return form.ResultCode;
            }
            catch (Exception ex)
            {
                Log.Write("图形模式异常：" + ex);
                Report.AppendLine("FATAL: " + ex.Message);
                FlushReport(opt, Const.ExitFailed);
                try
                {
                    MessageBox.Show("安装失败：" + ex.Message + Environment.NewLine + Environment.NewLine
                        + "日志：" + Const.LogPath, Const.Product + " 安装程序",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch (Exception) { }
                return Const.ExitFailed;
            }
        }

        private static void FlushReport(Options opt, int code)
        {
            if (opt == null || opt.Report == null || opt.Report.Length == 0) return;
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("== " + Const.Product + " 安装程序报告 ==");
                sb.AppendLine("产品版本 : " + Const.Version);
                sb.AppendLine("时间     : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("模式     : " + (opt.Silent ? "静默" : "图形") + (opt.DryRun ? " / dry-run（不写盘）" : ""));
                sb.AppendLine("安装目录 : " + (opt.InstallDir.Length > 0 ? opt.InstallDir : "(默认)")
                    + (opt.InstallDir.Length > 0 ? "" : " -> " + Const.DefaultInstallDir()));
                sb.AppendLine("快捷方式 : " + (opt.NoShortcuts ? "跳过" : (opt.DesktopShortcut ? "开始菜单 + 桌面" : "仅开始菜单")));
                sb.AppendLine("安装后启动: " + (opt.NoLaunch ? "否" : "是"));
                sb.AppendLine("日志     : " + Const.LogPath);
                sb.AppendLine();
                sb.AppendLine("---- 步骤 ----");
                sb.Append(Report.ToString());
                sb.AppendLine();
                sb.AppendLine("---- 退出码 ----");
                sb.AppendLine("exit=" + code + "  (" + ExitText(code) + ")");
                sb.AppendLine("0 = 安装成功");
                sb.AppendLine("2 = 用户取消");
                sb.AppendLine("3 = 安装失败（原因见上）");
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
        public bool NoShortcuts;
        public bool NoLaunch;
        public bool DesktopShortcut = true;
        public string InstallDir = "";
        public string Report = "";

        public static Options Parse(string[] args)
        {
            Options o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = (args[i] == null ? "" : args[i]).Trim();
                if (a.Length == 0) continue;
                string low = a.ToLowerInvariant();

                if (low == "/s" || low == "-s" || low == "--silent" || low == "/silent" || low == "/verysilent") { o.Silent = true; }
                else if (low == "/d" || low == "--dry-run" || low == "/dry-run" || low == "/dryrun") { o.DryRun = true; }
                else if (low == "--no-shortcuts" || low == "/no-shortcuts" || low == "/noshortcuts") { o.NoShortcuts = true; }
                else if (low == "--no-desktop-shortcut" || low == "/no-desktop-shortcut") { o.DesktopShortcut = false; }
                else if (low == "--no-launch" || low == "/no-launch" || low == "/nolaunch") { o.NoLaunch = true; }
                else if (low.StartsWith("/d=") || low.StartsWith("--dir=") || low.StartsWith("/dir="))
                {
                    // 兼容 Inno 习惯 /D=<目录>（末尾可带引号，也可不带）与本工具的 --dir="<目录>"
                    int eq = a.IndexOf('=');
                    string v = a.Substring(eq + 1);
                    while (v.Length > 0 && v[0] == '"') v = v.Substring(1);
                    while (v.Length > 0 && v[v.Length - 1] == '"') v = v.Substring(0, v.Length - 1);
                    // 目录里可能带空格而被拆成多个 argv，向后拼接；遇到下一个选项立即停止
                    while (i + 1 < args.Length && !Options.IsOption(args[i + 1]))
                    {
                        i++;
                        v = (v.Length == 0 ? "" : v + " ") + args[i];
                    }
                    v = v.Trim().Trim('"').Trim();
                    if (v.Length > 0) o.InstallDir = v;
                }
                else if (low.StartsWith("--report=") || low.StartsWith("/report="))
                {
                    int eq = a.IndexOf('=');
                    string v = a.Substring(eq + 1).Trim().Trim('"');
                    while (i + 1 < args.Length && !Options.IsOption(args[i + 1]))
                    {
                        i++;
                        v = (v.Length == 0 ? "" : v + " ") + args[i];
                    }
                    v = v.Trim().Trim('"').Trim();
                    if (v.Length > 0) o.Report = v;
                }
                else if (low == "--help" || low == "/?" || low == "-h") { o.Silent = true; o.Report = ""; }
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
            if (t.StartsWith("/"))
            {
                // Inno 的 /D=path 里也可能出现的路径片段形如 \foo，不以 / 开头；
                // 而 "/abc" 这种要么是选项要么是路径片段——按选项处理更安全（路径片段由引号包住即可）
                return true;
            }
            return false;
        }
    }

    // ================================================================== 引擎
    internal class Engine
    {
        private readonly Options _opt;
        private readonly Action<string> _log;
        private readonly StringBuilder _steps;
        public string PayloadRoot = "";
        public int CopiedFiles;
        public long CopiedBytes;
        public long CopiedSizeBytes;
        public string TrackedMarker = "";
        // 本次安装真正写进安装目录的条目（相对安装目录、反斜杠分隔、不以 \ 开头），供 files.json 与卸载器使用
        public readonly List<string> TrackedFiles = new List<string>();
        public readonly List<string> TrackedDirs = new List<string>();
        // 安装前目录里已有的条目数（!= 0 表示是共用文件夹，卸载时不得碰这些）
        public int PreexistingEntries;

        public Engine(Options opt, Action<string> log)
        {
            _opt = opt;
            _log = log;
            _steps = new StringBuilder();
        }

        private void Say(string s) { _log(s); _steps.AppendLine(s); }
        private void Step(string s)
        {
            _steps.AppendLine(s);
            _log(s);
        }
        public string Steps { get { return _steps.ToString(); } }

        // ------------------------------------------------------------ 主流程
        public int Run(bool interactive)
        {
            string dir = ResolveInstallDir();
            Step("[1/8] 安装目录：" + dir);

            // 安全预检（安全默认）：安装目录不得是重解析点，也不得落在系统关键目录
            string refuse = InstallDirRefusal(dir);
            if (refuse.Length > 0)
            {
                Step("[x] 拒绝安装到该目录：" + dir + "（" + refuse + "）");
                if (interactive && !_opt.Silent)
                {
                    MessageBox.Show("不能安装到该目录：" + Environment.NewLine + dir + Environment.NewLine + Environment.NewLine
                        + "原因：" + refuse + Environment.NewLine + Environment.NewLine
                        + "请改选一个普通目录（例如默认的 %LOCALAPPDATA%\\Programs\\AzurLaneDeskPet）。",
                        Const.Product + " 安装程序", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return Const.ExitFailed;
            }

            // 共用文件夹检测：安装前目录非空、且没有本产品标记 → 只写自己的文件，绝不清理别人的东西
            PreexistingEntries = CountEntries(dir);
            bool shared = PreexistingEntries > 0 && !File.Exists(Path.Combine(dir, Const.InstallMarker));
            if (shared)
            {
                Step("[i] 该目录已有 " + PreexistingEntries + " 个其它条目，属于共用文件夹：");
                Step("      安装不会改动它们；卸载时也只会删除本产品自己的文件（清单 files.json 记录）。");
                if (interactive && !_opt.Silent)
                {
                    MessageBox.Show(
                        "该目录已有其它文件（" + PreexistingEntries + " 个条目）：" + Environment.NewLine + dir + Environment.NewLine + Environment.NewLine
                        + "安装不会动它们；卸载时也只会删掉「" + Const.Product + "」自己的文件，其余一律保留。",
                        Const.Product + " 安装程序", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }

            // 已存在同名程序 → 覆盖升级（用户数据目录不动）
            string existing = Path.Combine(dir, Const.ExeName);
            if (File.Exists(existing))
            {
                Step("[i] 检测到已安装的 " + Const.Product + "（" + dir + "），将执行覆盖升级，用户数据目录保持不变");
                if (interactive && !_opt.Silent)
                {
                    DialogResult dr = MessageBox.Show(
                        "检测到该目录已安装「" + Const.Product + "」：" + Environment.NewLine + dir + Environment.NewLine + Environment.NewLine
                        + "是否覆盖升级？" + Environment.NewLine + "（程序文件会被替换，%APPDATA%\\AzurLaneDeskPet 里的角色素材/台词/设置全部保留）",
                        Const.Product + " 安装程序", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (dr != DialogResult.Yes)
                    {
                        Step("[x] 用户取消了覆盖升级");
                        return Const.ExitCancelled;
                    }
                }
            }

            // 负载
            Step("[2/8] 准备安装负载（内嵌 payload.zip）");
            if (!PreparePayload()) return Const.ExitFailed;

            if (_opt.DryRun)
            {
                Step("[3/8] （dry-run）跳过写盘：本应把 " + CountFiles(PayloadRoot) + " 个文件解包到 " + dir);
                Step("[4/8] （dry-run）跳过写盘：本应写入 " + Path.Combine(dir, Const.InstallManifest)
                    + "、" + Const.FileListManifest + " 与标记 " + Const.InstallMarker);
                Step("[5/8] （dry-run）跳过写盘：本应写注册表 HKCU\\" + Const.RegUninstallKey);
                Step("[6/8] （dry-run）跳过写盘：本应创建快捷方式（" + (_opt.NoShortcuts ? "已指定 --no-shortcuts" : "开始菜单" + (_opt.DesktopShortcut ? " + 桌面" : "")) + "）");
                Step("[7/8] （dry-run）跳过写盘：本应计算占用体积");
                Step("[8/8] （dry-run）跳过启动：" + (_opt.NoLaunch ? "已指定 --no-launch" : Const.ExeName));
                if (PreexistingEntries > 0) Step("      安装前已存在条目数：preexisting=" + PreexistingEntries);
                Step("RESULT=OK(dry-run)");
                return Const.ExitOk;
            }

            // 解包
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                Step("[x] 无法创建安装目录：" + ex.Message);
                return Const.ExitFailed;
            }
            Step("[3/8] 解包程序文件 → " + dir);
            try
            {
                CopyTree(PayloadRoot, dir);
                TrackedMarker = Const.InstallMarker;
                // 清单自身也是本产品写下的文件
                Track(Const.InstallManifest);
                Track(Const.FileListManifest);
                Track(Const.InstallMarker);
                Step("      已写入 " + CopiedFiles + " 个文件，" + Fmt(CopiedBytes));
                // 覆盖升级：按上一版清单清理本版已移除的残留
                // （只删旧清单里列出的、且不在本次负载里的文件；别的文件一律不碰）
                CleanupPreviousVersion(dir);
            }
            catch (Exception ex)
            {
                Step("[x] 解包失败：" + ex.Message);
                return Const.ExitFailed;
            }
            if (!File.Exists(Path.Combine(dir, Const.ExeName)))
            {
                Step("[x] 负载校验失败：缺少 " + Const.ExeName);
                return Const.ExitFailed;
            }

            // 体积
            long sizeKb = 0;
            try { sizeKb = CopiedSizeBytes / 1024; } catch (Exception) { }
            Step("[4/8] 安装体积约 " + sizeKb + " KB（按本产品写入的文件计算）");

            // 标记文件：卸载器据此确认「这确实是本产品的安装目录」
            try
            {
                File.WriteAllText(Path.Combine(dir, Const.InstallMarker), Const.MarkerText + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception ex) { Step("[!] 写安装标记失败（继续）：" + ex.Message); }

            // install.json
            // 注意：这里必须用 Const.DataDir（认 AZURLANEDESK_PET_DATA 覆盖），
            // 否则验收脚本装出来的 install.json 会把"真实用户数据目录"记进去，
            // 下一步卸载器就会把它当自定义数据目录**删掉**（v1.0.8 之前就是这样误删用户数据的）。
            string dataDir = Const.DataDir;
            string[] shortcuts = BuildShortcutPathList(dir);
            string manifestPath = Path.Combine(dir, Const.InstallManifest);
            try
            {
                WriteInstallManifest(manifestPath, dir, dataDir, shortcuts, sizeKb, PreexistingEntries);
                Step("      清单已写入：" + manifestPath);
            }
            catch (Exception ex)
            {
                Step("[!] 写 install.json 失败（继续）：" + ex.Message);
            }

            // files.json：卸载白名单（只删这里记录的东西）
            try
            {
                WriteFileList(dir);
                Step("      文件清单已写入：" + Path.Combine(dir, Const.FileListManifest)
                    + "（" + TrackedFiles.Count + " 个文件 / " + TrackedDirs.Count + " 个目录）");
            }
            catch (Exception ex)
            {
                Step("[!] 写 files.json 失败（继续）：" + ex.Message);
            }

            // 注册表
            Step("[5/8] 写注册表卸载项 HKCU\\" + Const.RegUninstallKey);
            try { WriteRegistry(dir, sizeKb); }
            catch (Exception ex) { Step("[!] 写注册表失败（继续）：" + ex.Message); }

            // 快捷方式
            if (_opt.NoShortcuts)
            {
                Step("[6/8] 按 --no-shortcuts 跳过快捷方式");
            }
            else
            {
                Step("[6/8] 创建快捷方式");
                string startMenu = StartMenuLink();
                string desktop = DesktopLink();
                bool ok1 = Shortcut.CreateSmart(startMenu, Path.Combine(dir, Const.ExeName), dir);
                Step((ok1 ? "      ✓ " : "      ✗ ") + startMenu);
                if (_opt.DesktopShortcut)
                {
                    bool ok2 = Shortcut.CreateSmart(desktop, Path.Combine(dir, Const.ExeName), dir);
                    Step((ok2 ? "      ✓ " : "      ✗ ") + desktop);
                }
                else
                {
                    Step("      · 已指定 --no-desktop-shortcut，跳过桌面快捷方式");
                }
            }

            // 启动
            if (_opt.NoLaunch)
            {
                Step("[7/8] 按 --no-launch 跳过启动");
            }
            else
            {
                Step("[7/8] 启动 " + Const.ExeName);
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(dir, Const.ExeName));
                    psi.WorkingDirectory = dir;
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                    Step("      ✓ 已启动");
                }
                catch (Exception ex) { Step("[!] 启动失败（不影响安装）：" + ex.Message); }
            }

            Step("[8/8] 安装完成，exit=0");
            return Const.ExitOk;
        }

        // ------------------------------------------------------- 目录与负载
        public string ResolveInstallDir()
        {
            string d = _opt.InstallDir;
            if (d == null) d = "";
            d = d.Trim().Trim('"').Trim();
            if (d.Length == 0) return Const.DefaultInstallDir();
            try { d = Path.GetFullPath(d); }
            catch (Exception) { }
            return d.TrimEnd('\\');
        }

        public bool PreparePayload()
        {
            try
            {
                // 允许 exe 同目录放一个 payload.zip 便于调试/替换负载
                string side = Path.Combine(Const.ExeDir, "payload.zip");
                string zipPath = "";
                if (File.Exists(side)) { zipPath = side; Say("使用 exe 同目录 payload.zip：" + side); }

                string stage = Path.Combine(Path.GetTempPath(), "AzurLaneDeskPet-payload-" + Process.GetCurrentProcess().Id);
                if (Directory.Exists(stage)) { try { Directory.Delete(stage, true); } catch (Exception) { } }
                Directory.CreateDirectory(stage);

                if (zipPath.Length > 0)
                {
                    ExtractZip(zipPath, stage);
                }
                else
                {
                    Assembly asm = Assembly.GetExecutingAssembly();
                    using (Stream s = asm.GetManifestResourceStream(Const.PayloadResource))
                    {
                        if (s == null)
                        {
                            Step("[x] 内嵌负载缺失：编译时未使用 /resource:payload.zip,payload.zip");
                            return false;
                        }
                        string tmpZip = Path.Combine(Path.GetTempPath(), "alp-embedded-" + Process.GetCurrentProcess().Id + ".zip");
                        using (FileStream fs = File.Create(tmpZip)) { s.CopyTo(fs); }
                        ExtractZip(tmpZip, stage);
                        try { File.Delete(tmpZip); } catch (Exception) { }
                    }
                }
                PayloadRoot = stage;
                int n = CountFiles(stage);
                Step("      负载已解出：" + n + " 个文件");
                if (!File.Exists(Path.Combine(stage, Const.ExeName)))
                {
                    Step("[x] 负载内缺少 " + Const.ExeName + "（payload.zip 内容应为 dist\\app 下全部文件）");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Step("[x] 准备负载失败：" + ex.Message);
                return false;
            }
        }

        public static void ExtractZip(string zipPath, string destDir)
        {
            using (ZipArchive z = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry e in z.Entries)
                {
                    string rel = e.FullName.Replace('/', Path.DirectorySeparatorChar);
                    string target = Path.Combine(destDir, rel);
                    if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\"))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    string parent = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    e.ExtractToFile(target, true);
                }
            }
        }

        // 把负载树复制进安装目录，并记录每一个写入的相对路径（供 files.json / 卸载白名单使用）。
        // 跳过负载里的重解析点（junction / symlink），且不跟随它们，避免把外部内容复制进来。
        public void CopyTree(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            CopyTreeWalk(src, dst, src);
        }

        private void CopyTreeWalk(string current, string dst, string srcRoot)
        {
            // 子目录
            string[] subDirs;
            try { subDirs = Directory.GetDirectories(current); }
            catch (Exception) { subDirs = new string[0]; }
            foreach (string d in subDirs)
            {
                if (IsReparsePoint(d))
                {
                    Step("      · 负载内的重解析点已跳过（不跟随）：" + RelOf(d, srcRoot));
                    continue;
                }
                string rel = RelOf(d, srcRoot);
                string target = Path.Combine(dst, rel);
                Directory.CreateDirectory(target);
                TrackDir(rel);
                CopyTreeWalk(d, dst, srcRoot);
            }
            // 文件
            string[] files;
            try { files = Directory.GetFiles(current); }
            catch (Exception) { files = new string[0]; }
            foreach (string f in files)
            {
                if (IsReparsePoint(f))
                {
                    Step("      · 负载内的重解析点已跳过（不跟随）：" + RelOf(f, srcRoot));
                    continue;
                }
                string rel = RelOf(f, srcRoot);
                string target = Path.Combine(dst, rel);
                string parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                File.Copy(f, target, true);
                Track(rel);
                CopiedFiles++;
                try
                {
                    long len = new FileInfo(f).Length;
                    CopiedBytes += len;
                    CopiedSizeBytes += len;
                }
                catch (Exception) { }
            }
        }

        private static string RelOf(string full, string root)
        {
            string rel = full.Substring(root.Length).TrimStart('\\', '/');
            return rel.Replace('/', '\\');
        }

        // 记录一个相对路径（文件）
        public void Track(string rel)
        {
            if (rel == null) return;
            string r = rel.Trim().TrimStart('\\', '/').Replace('/', '\\');
            // 防御：绝不记录逃逸路径
            if (r.Length == 0 || r.IndexOf("..") >= 0 || r.IndexOf(':') >= 0) return;
            if (!TrackedFiles.Contains(r)) TrackedFiles.Add(r);
        }

        public void TrackDir(string rel)
        {
            if (rel == null) return;
            string r = rel.Trim().TrimStart('\\', '/').Replace('/', '\\');
            if (r.Length == 0 || r.IndexOf("..") >= 0 || r.IndexOf(':') >= 0) return;
            if (!TrackedDirs.Contains(r)) TrackedDirs.Add(r);
        }

        public static bool IsReparsePoint(string path)
        {
            try
            {
                FileAttributes a = File.GetAttributes(path);
                return (a & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch (Exception) { return false; }
        }

        // ------------------------------------------------- 覆盖升级：清理上一版残留
        // 为什么需要它：
        //   files.json 是「本次安装写了什么」的白名单，卸载器只删白名单内的东西。
        //   直接覆盖升级时，上一版写过、这一版已经删掉的文件（例如早期内置的武藏 / 新泽西）
        //   会**留在安装目录里**：内置角色列表会多出已移除的角色，自检会因此失败，
        //   而且卸载也删不掉它们（不在新白名单里）。
        //
        // 因此：只在「目标目录确实是本产品的旧安装」（有 .azurlandeskpet-install 标记 +
        // files.json 里 product=AzurLaneDeskPet）时，按**旧清单**逐条清理：
        //   · 只处理旧清单里列出的相对路径，别的一律不碰；
        //   · 只删「不在本次负载里」的文件；本次负载里的文件刚被覆盖写好了，不能动；
        //   · 逐条校验：路径必须落在安装目录内、自身与祖先都不能是重解析点；
        //   · 删不掉的（被占用等）不报错，改为并进新白名单，留给卸载器以后再清；
        //   · 目录只在「已空」时才删，安装目录本身永不在此删除。
        public void CleanupPreviousVersion(string dir)
        {
            string marker = Path.Combine(dir, Const.InstallMarker);
            string listFile = Path.Combine(dir, Const.FileListManifest);
            if (!File.Exists(marker) || !File.Exists(listFile))
            {
                Step("      · 未发现上一版的安装标记 / 文件清单，跳过升级清理（不做任何删除）");
                return;
            }
            string json;
            try { json = File.ReadAllText(listFile, Encoding.UTF8); }
            catch (Exception ex) { Step("      · 读不到上一版 files.json，跳过升级清理：" + ex.Message); return; }

            string product = JsonStr(json, "product");
            if (!string.Equals(product, "AzurLaneDeskPet", StringComparison.OrdinalIgnoreCase))
            {
                Step("      · files.json 不是本产品的（product=" + product + "），跳过升级清理");
                return;
            }

            List<string> oldFiles = JsonArray(json, "files");
            List<string> oldDirs = JsonArray(json, "dirs");
            Step("      检测到上一版清单："
                + (oldFiles == null ? 0 : oldFiles.Count) + " 个文件 / "
                + (oldDirs == null ? 0 : oldDirs.Count) + " 个目录，开始清理本版已移除的残留");

            int removed = 0, kept = 0, skipped = 0;
            // 本次负载里的文件集合（大小写不敏感 —— Windows 路径大小写不敏感，
            // 用 List.Contains 的序数比较可能把「同一个文件」判成不同，从而误删刚写好的负载文件）
            HashSet<string> current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string t in TrackedFiles) current.Add(t);
            if (oldFiles != null)
            {
                foreach (string rel in oldFiles)
                {
                    string full = SafeJoin(dir, rel);
                    if (full == null) { skipped++; continue; }
                    if (!File.Exists(full)) continue;
                    if (IsReparsePoint(full) || HasReparseAncestor(dir, full)) { skipped++; continue; }
                    // 本次负载里还有这个文件 → 刚写好的，绝对不能删
                    if (current.Contains(rel)) continue;
                    try
                    {
                        File.Delete(full);
                        removed++;
                    }
                    catch (Exception)
                    {
                        kept++;
                        Track(rel);   // 删不掉就交给卸载器以后清理
                    }
                }
            }
            if (oldDirs != null)
            {
                List<string> dirs = new List<string>(oldDirs);
                dirs.Sort(delegate (string a, string b) { return b.Length.CompareTo(a.Length); });   // 深的先删
                foreach (string rel in dirs)
                {
                    string full = SafeJoin(dir, rel);
                    if (full == null) continue;
                    if (!Directory.Exists(full)) continue;
                    if (IsReparsePoint(full)) { skipped++; continue; }
                    if (IsSamePath(full, dir)) continue;                 // 安装目录本身绝不在这里删
                    try
                    {
                        if (Directory.GetFileSystemEntries(full).Length == 0)
                        {
                            Directory.Delete(full, false);
                            removed++;
                        }
                        else
                        {
                            Step("      · 目录非空，保留：" + rel + "（可能含有你自己的文件）");
                        }
                    }
                    catch (Exception) { }
                }
            }
            Step("      升级清理：删除上一版残留 " + removed + " 项，"
                + "保留（非本产品 / 非空目录）" + kept + " 项，跳过（重解析点 / 越界 / 缺失）" + skipped + " 项");
        }

        // 只允许落在 root 之内的相对路径；越界、含 .. / 盘符 / 绝对路径的一律返回 null
        private static string SafeJoin(string root, string rel)
        {
            if (string.IsNullOrEmpty(rel)) return null;
            string r = rel.Trim().TrimStart('\\', '/');
            if (r.Length == 0) return null;
            if (r.IndexOf("..", StringComparison.Ordinal) >= 0) return null;
            if (r.IndexOf(':') >= 0) return null;
            if (Path.IsPathRooted(r)) return null;
            string full;
            try { full = Path.GetFullPath(Path.Combine(root, r)); }
            catch (Exception) { return null; }
            string rootFull;
            try { rootFull = Path.GetFullPath(root).TrimEnd('\\'); }
            catch (Exception) { return null; }
            if (!full.StartsWith(rootFull + "\\", StringComparison.OrdinalIgnoreCase)) return null;
            return full;
        }

        // 自身或任意祖先目录是重解析点 → 不碰（不跟随 junction / symlink）
        private static bool HasReparseAncestor(string root, string full)
        {
            try
            {
                string cur = Path.GetDirectoryName(full);
                string rootFull = Path.GetFullPath(root).TrimEnd('\\');
                while (!string.IsNullOrEmpty(cur))
                {
                    string curFull = Path.GetFullPath(cur).TrimEnd('\\');
                    if (string.Equals(curFull, rootFull, StringComparison.OrdinalIgnoreCase)) return false;
                    if (IsReparsePoint(curFull)) return true;
                    if (curFull.Length <= rootFull.Length) return false;
                    cur = Path.GetDirectoryName(curFull);
                }
            }
            catch (Exception) { }
            return false;
        }

        private static bool IsSamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        // 极简 JSON 取值：够用于读自家 files.json 的 product / files[] / dirs[]
        internal static string JsonStr(string json, string key)
        {
            if (json == null) return "";
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
            if (!m.Success) return "";
            return m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\/", "/");
        }

        internal static List<string> JsonArray(string json, string key)
        {
            List<string> outp = new List<string>();
            if (json == null) return outp;
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
            if (!m.Success) return outp;
            foreach (Match s in Regex.Matches(m.Groups[1].Value, "\"((?:\\\\.|[^\"\\\\])*)\""))
            {
                string v = s.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\/", "/");
                v = v.Trim().TrimStart('\\', '/').Replace('/', '\\');
                if (v.Length > 0) outp.Add(v);
            }
            return outp;
        }

        // 安装目录直接子条目的数量（用于共用文件夹检测）
        public static int CountEntries(string dir)
        {
            int n = 0;
            try
            {
                if (!Directory.Exists(dir)) return 0;
                n += Directory.GetFiles(dir).Length;
                n += Directory.GetDirectories(dir).Length;
            }
            catch (Exception) { }
            return n;
        }

        // 安装目录安全预检：拒绝重解析点与系统关键目录；返回空字符串 = 通过
        public static string InstallDirRefusal(string dir)
        {
            try
            {
                if (dir == null || dir.Trim().Length == 0) return "路径为空";
                string full;
                try { full = Path.GetFullPath(dir).TrimEnd('\\'); }
                catch (Exception ex) { return "路径无效：" + ex.Message; }
                if (full.Length <= 3) return "盘符根目录";
                if (full.StartsWith("\\\\")) return "网络路径（UNC）不受支持";
                if (IsReparsePoint(full)) return "该目录是重解析点（junction / 符号链接）";

                string upper = full.ToUpperInvariant();
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\').ToUpperInvariant();
                if (upper == win || upper.StartsWith(win + "\\")) return "位于 Windows 目录内";

                string[] forbidden = new string[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Path.GetTempPath()
                };
                foreach (string f in forbidden)
                {
                    if (string.IsNullOrEmpty(f)) continue;
                    string ff = f.TrimEnd('\\').ToUpperInvariant();
                    if (upper == ff) return "系统/用户关键目录本身";
                }
                return "";
            }
            catch (Exception ex) { return "预检异常：" + ex.Message; }
        }

        public static int CountFiles(string dir)
        {
            try { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length; }
            catch (Exception) { return 0; }
        }

        public static long DirSize(string dir)
        {
            long total = 0;
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch (Exception) { }
                }
            }
            catch (Exception) { }
            return total;
        }

        public static string FmtSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("N1") + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("N2") + " MB";
        }

        private static string Fmt(long bytes) { return FmtSize(bytes); }

        // ------------------------------------------------------- 快捷方式路径
        public static string StartMenuLink()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs");
            return Path.Combine(dir, Const.ShortcutName);
        }

        public static string DesktopLink()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Const.ShortcutName);
        }

        private string[] BuildShortcutPathList(string installDir)
        {
            List<string> list = new List<string>();
            if (!_opt.NoShortcuts)
            {
                list.Add(StartMenuLink());
                if (_opt.DesktopShortcut) list.Add(DesktopLink());
            }
            return list.ToArray();
        }

        // ------------------------------------------------------------ 清单
        public void WriteInstallManifest(string path, string installDir, string dataDir, string[] shortcuts, long sizeKb, int preexisting)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"product\": " + Js(Const.Product) + ",");
            sb.AppendLine("  \"version\": " + Js(Const.Version) + ",");
            sb.AppendLine("  \"publisher\": " + Js(Const.Publisher) + ",");
            sb.AppendLine("  \"installDir\": " + Js(installDir) + ",");
            sb.AppendLine("  \"dataDir\": " + Js(dataDir) + ",");
            sb.Append("  \"shortcuts\": [");
            for (int i = 0; i < shortcuts.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Js(shortcuts[i]));
            }
            sb.AppendLine("],");
            sb.AppendLine("  \"registryKey\": " + Js(@"HKCU\" + Const.RegUninstallKey) + ",");
            sb.AppendLine("  \"uninstaller\": " + Js(Path.Combine(installDir, Const.UninstallerName)) + ",");
            sb.AppendLine("  \"mainExe\": " + Js(Path.Combine(installDir, Const.ExeName)) + ",");
            sb.AppendLine("  \"files\": " + Js(Const.FileListManifest) + ",");
            sb.AppendLine("  \"marker\": " + Js(Const.InstallMarker) + ",");
            sb.AppendLine("  \"preexisting\": " + preexisting + ",");
            sb.AppendLine("  \"estimatedSizeKb\": " + sizeKb + ",");
            sb.AppendLine("  \"installedAt\": " + Js(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")));
            sb.AppendLine("}");
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        // files.json：卸载白名单。卸载器只删这里记录的文件/目录，其余一律保留。
        public void WriteFileList(string installDir)
        {
            string path = Path.Combine(installDir, Const.FileListManifest);
            StringBuilder sb = new StringBuilder();
            List<string> files = new List<string>(TrackedFiles);
            files.Sort(StringComparer.OrdinalIgnoreCase);
            List<string> dirs = new List<string>(TrackedDirs);
            dirs.Sort(StringComparer.OrdinalIgnoreCase);

            sb.AppendLine("{");
            sb.AppendLine("  \"product\": " + Js("AzurLaneDeskPet") + ",");
            sb.AppendLine("  \"version\": " + Js(Const.Version) + ",");
            sb.AppendLine("  \"installDir\": " + Js(installDir) + ",");
            sb.AppendLine("  \"marker\": " + Js(Const.InstallMarker) + ",");
            sb.AppendLine("  \"preexisting\": " + PreexistingEntries + ",");
            sb.AppendLine("  \"created\": " + Js(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")) + ",");
            sb.Append("  \"files\": [");
            for (int i = 0; i < files.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Js(files[i]));
            }
            sb.AppendLine("],");
            sb.Append("  \"dirs\": [");
            for (int i = 0; i < dirs.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Js(dirs[i]));
            }
            sb.AppendLine("]");
            sb.AppendLine("}");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        // ------------------------------------------------------------ 注册表
        public void WriteRegistry(string installDir, long sizeKb)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Const.RegUninstallKey))
            {
                if (k == null) throw new Exception("CreateSubKey 返回 null");
                string exe = Path.Combine(installDir, Const.ExeName);
                string un = Path.Combine(installDir, Const.UninstallerName);
                k.SetValue("DisplayName", Const.Product, RegistryValueKind.String);
                k.SetValue("DisplayVersion", Const.Version, RegistryValueKind.String);
                k.SetValue("Publisher", Const.Publisher, RegistryValueKind.String);
                k.SetValue("InstallLocation", installDir, RegistryValueKind.String);
                k.SetValue("DisplayIcon", exe, RegistryValueKind.String);
                k.SetValue("UninstallString", "\"" + un + "\"", RegistryValueKind.String);
                k.SetValue("QuietUninstallString", "\"" + un + "\" /S", RegistryValueKind.String);
                k.SetValue("EstimatedSize", (int)sizeKb, RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            // 主程序可能用到的产品键（尽力而为）
            try
            {
                using (RegistryKey p = Registry.CurrentUser.CreateSubKey(Const.RegProductKey))
                {
                    if (p != null)
                    {
                        p.SetValue("InstallDir", installDir, RegistryValueKind.String);
                        p.SetValue("Version", Const.Version, RegistryValueKind.String);
                    }
                }
            }
            catch (Exception) { }
        }

        private static string Js(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append("\"");
            return sb.ToString();
        }
    }

    // ============================================================== 快捷方式
    internal static class Shortcut
    {
        // 主方案：WScript.Shell COM；失败降级为 .lnk 的“手写二进制”里最简可行方案——同目录 URL 式快捷方式不可靠，
        // 因此降级方案为调用 powershell 的 WScript.Shell（系统自带）。
        public static bool CreateSmart(string linkPath, string targetExe, string workDir)
        {
            if (CreateCom(linkPath, targetExe, workDir)) return true;
            if (CreateViaPowerShell(linkPath, targetExe, workDir)) return true;
            Log.Write("快捷方式创建失败：" + linkPath);
            return false;
        }

        private static bool CreateCom(string linkPath, string targetExe, string workDir)
        {
            object shell = null;
            object lnk = null;
            try
            {
                string dir = Path.GetDirectoryName(linkPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return false;
                shell = Activator.CreateInstance(t);
                lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
                if (lnk == null) return false;
                Type lt = lnk.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { targetExe });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { targetExe + ",0" });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { Const.Product + " · " + Const.Slogan });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                return File.Exists(linkPath);
            }
            catch (Exception ex)
            {
                Log.Write("WScript.Shell 建快捷方式失败：" + ex.Message);
                return false;
            }
            finally
            {
                try { if (lnk != null && Marshal.IsComObject(lnk)) Marshal.ReleaseComObject(lnk); } catch (Exception) { }
                try { if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell); } catch (Exception) { }
            }
        }

        private static bool CreateViaPowerShell(string linkPath, string targetExe, string workDir)
        {
            try
            {
                string dir = Path.GetDirectoryName(linkPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string q = "\"" + linkPath.Replace("'", "''") + "\"";
                string script =
                    "$w=New-Object -ComObject WScript.Shell;" +
                    "$s=$w.CreateShortcut('" + linkPath.Replace("'", "''") + "');" +
                    "$s.TargetPath='" + targetExe.Replace("'", "''") + "';" +
                    "$s.WorkingDirectory='" + workDir.Replace("'", "''") + "';" +
                    "$s.IconLocation='" + (targetExe + ",0").Replace("'", "''") + "';" +
                    "$s.Description='" + (Const.Product + " · " + Const.Slogan).Replace("'", "''") + "';" +
                    "$s.Save()";
                string tmp = Path.Combine(Path.GetTempPath(), "alp-mklnk-" + Guid.NewGuid().ToString("N") + ".ps1");
                File.WriteAllText(tmp, script, new UTF8Encoding(true));
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + tmp + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    string errOut = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode != 0) Log.Write("powershell 建快捷方式退出码 " + p.ExitCode + "：" + errOut);
                }
                try { File.Delete(tmp); } catch (Exception) { }
                return File.Exists(linkPath);
            }
            catch (Exception ex)
            {
                Log.Write("powershell 建快捷方式异常：" + ex.Message);
                return false;
            }
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
            using (Pen p = new Pen(Gold, thick ? 2f : 1f))
            {
                g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
            }
            using (Pen p2 = new Pen(Color.FromArgb(70, Gold), 1f))
            {
                g.DrawRectangle(p2, r.X + 2, r.Y + 2, Math.Max(0, r.Width - 5), Math.Max(0, r.Height - 5));
            }
        }

        public static void DrawAnchorGlyph(Graphics g, Rectangle r)
        {
            // 简化舰装/锚形徽记
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(Gold, 2f))
            {
                int cx = r.X + r.Width / 2;
                int cy = r.Y + r.Height / 2;
                int rr = Math.Min(r.Width, r.Height) / 2 - 3;
                g.DrawEllipse(p, cx - rr / 2, cy - rr, rr, rr);
                g.DrawLine(p, cx, cy - rr + 2, cx, cy + rr - 1);
                g.DrawLine(p, cx - rr, cy + 2, cx + rr, cy + 2);
                g.DrawArc(p, cx - rr, cy - rr / 2, rr * 2, rr, 20, 140);
            }
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

    // 渐变背景面板
    internal class GradientPanel : Panel
    {
        public GradientPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.NavyTop;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Theme.PaintGradient(e.Graphics, ClientRectangle);
        }
    }

    // 金色描边的取景框
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
                int tw = (int)Math.Ceiling(sz.Width) + 16;
                using (SolidBrush bg = new SolidBrush(Theme.PanelFill))
                {
                    g.FillRectangle(bg, 12, -1, tw, 16);
                }
                using (SolidBrush br = new SolidBrush(Theme.Gold))
                {
                    g.DrawString(_caption, f, br, 16, -2);
                }
            }
        }
    }

    // 金色主按钮
    internal class GoldButton : Button
    {
        private bool _hover;
        private bool _primary;

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
            Color fill = _primary
                ? (_hover ? Color.FromArgb(0xF6, 0xDD, 0x92) : Theme.Gold)
                : (_hover ? Color.FromArgb(0x1D, 0x3B, 0x6E) : Theme.PanelFill);
            using (SolidBrush b = new SolidBrush(fill)) g.FillRectangle(b, ClientRectangle);
            using (Pen p = new Pen(_primary ? Color.FromArgb(0xFF, 0xEE, 0xBB) : Theme.Gold, 1.6f))
            {
                g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            }
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

    // 透明文字标签
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
        public GhostCheck(string text, bool @checked, Color color, bool multiLine)
        {
            Text = text;
            Checked = @checked;
            ForeColor = color;
            BackColor = Color.Transparent;
            Font = Theme.UiFont(9f, FontStyle.Regular);
            AutoSize = false;
            if (multiLine)
            {
                // 用 Label 做自绘会把复选框画掉，改用自绘多行：这里保守处理 —— 允许换行但不改高度
                Height = 22;
            }
            else
            {
                Height = 22;
            }
        }

        public GhostCheck(string text, bool @checked, Color color)
            : this(text, @checked, color, false)
        {
        }
    }

    // ================================================================== 界面
    internal class SetupForm : Form
    {
        public int ResultCode = Const.ExitOk;
        private readonly StringBuilder _steps = new StringBuilder();
        public string Steps { get { return _steps.ToString(); } }

        private readonly Options _opt;
        private GradientPanel _root;
        private TextBox _txtDir;
        private GoldenTextBox _pathFrame;
        private GhostCheck _cbDesktop;
        private GhostCheck _cbLaunch;
        private RichTextBox _log;
        private Label _status;
        private GoldButton _btnInstall;
        private GoldButton _btnClose;

        public SetupForm(Options opt)
        {
            _opt = opt;
            BuildUi();
        }

        private void BuildUi()
        {
            Text = Const.Product + " 安装程序  v" + Const.Version;
            ClientSize = new Size(780, 612);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = true;
            Icon = LoadEmbeddedIcon();
            BackColor = Theme.NavyTop;
            Font = Theme.UiFont(9.5f, FontStyle.Regular);

            _root = new GradientPanel();
            _root.Dock = DockStyle.Fill;
            Controls.Add(_root);

            // 标题：宋体 金色
            Label title = new GhostLabel(Const.Slogan, Theme.Gold, 22f, FontStyle.Bold);
            title.Font = Theme.TitleFont(22f);
            title.SetBounds(28, 18, 724, 40);
            title.TextAlign = ContentAlignment.MiddleCenter;
            _root.Controls.Add(title);

            Label sub = new GhostLabel(Const.Product + "  ·  安装程序 v" + Const.Version, Theme.TextWhite, 11f, FontStyle.Regular);
            sub.SetBounds(28, 58, 724, 22);
            sub.TextAlign = ContentAlignment.MiddleCenter;
            _root.Controls.Add(sub);

            Label rule = new GhostLabel("——  " + Const.Publisher + "  ·  免管理员权限  ·  自定义安装目录  ——", Theme.TextDim, 9f, FontStyle.Regular);
            rule.SetBounds(28, 80, 724, 20);
            rule.TextAlign = ContentAlignment.MiddleCenter;
            _root.Controls.Add(rule);

            // 装饰
            Panel deco = new Panel();
            deco.SetBounds(28, 106, 724, 2);
            deco.BackColor = Theme.GoldDim;
            _root.Controls.Add(deco);

            // ① 安装位置
            FramedPanel g1 = new FramedPanel("① 安装位置");
            g1.SetBounds(24, 122, 732, 96);
            Label l1 = new GhostLabel("安装到：", Theme.TextWhite, 9.5f, FontStyle.Bold);
            l1.SetBounds(22, 30, 70, 24);
            g1.Controls.Add(l1);

            _pathFrame = new GoldenTextBox();
            _pathFrame.SetBounds(94, 27, 512, 28);
            _txtDir = _pathFrame.Inner;
            g1.Controls.Add(_pathFrame);

            GoldButton browse = new GoldButton(false);
            browse.Text = "浏览…";
            browse.SetBounds(614, 26, 96, 30);
            browse.Click += delegate { Browse(); };
            g1.Controls.Add(browse);

            GhostLabel lDef = new GhostLabel("默认目录：%LOCALAPPDATA%\\Programs\\AzurLaneDeskPet（留空即用默认，免管理员权限）",
                Theme.TextDim, 8.5f, FontStyle.Regular);
            lDef.SetBounds(22, 62, 706, 20);
            g1.Controls.Add(lDef);
            _root.Controls.Add(g1);

            // ② 选项
            FramedPanel g2 = new FramedPanel("② 安装选项");
            g2.SetBounds(24, 228, 732, 88);
            _cbDesktop = new GhostCheck("在桌面创建快捷方式", _opt.DesktopShortcut && !_opt.NoShortcuts, Theme.TextWhite);
            _cbDesktop.SetBounds(24, 26, 320, 22);
            if (!_opt.NoShortcuts)
            {
                _cbDesktop.Checked = true;
            }
            else
            {
                _cbDesktop.Checked = false;
                _cbDesktop.Enabled = false;
            }
            g2.Controls.Add(_cbDesktop);

            _cbLaunch = new GhostCheck("立即运行桌宠", !_opt.NoLaunch, Theme.TextWhite);
            _cbLaunch.SetBounds(24, 52, 320, 22);
            g2.Controls.Add(_cbLaunch);

            Label l2 = new GhostLabel("开始菜单快捷方式必建；用户数据（角色素材 / 台词 / 图片）保存在 %APPDATA%\\AzurLaneDeskPet，升级不会丢失。",
                Theme.TextDim, 8.5f, FontStyle.Regular);
            l2.SetBounds(350, 28, 370, 46);
            g2.Controls.Add(l2);
            _root.Controls.Add(g2);

            // 按钮
            _btnInstall = new GoldButton(true);
            _btnInstall.Text = "开始安装";
            _btnInstall.SetBounds(24, 328, 200, 38);
            _btnInstall.Click += delegate { DoInstall(); };
            _root.Controls.Add(_btnInstall);

            GoldButton btnFolder = new GoldButton(false);
            btnFolder.Text = "查看日志";
            btnFolder.SetBounds(238, 328, 150, 38);
            btnFolder.Click += delegate { OpenPath(Const.LogPath); };
            _root.Controls.Add(btnFolder);

            _btnClose = new GoldButton(false);
            _btnClose.Text = "关闭";
            _btnClose.SetBounds(606, 328, 150, 38);
            _btnClose.Click += delegate { Close(); };
            _root.Controls.Add(_btnClose);

            // 日志
            Label llog = new GhostLabel("安装日志", Theme.Gold, 9.5f, FontStyle.Bold);
            llog.SetBounds(24, 374, 200, 20);
            _root.Controls.Add(llog);

            _log = new RichTextBox();
            _log.SetBounds(24, 396, 732, 158);
            _log.ReadOnly = true;
            _log.BorderStyle = BorderStyle.FixedSingle;
            _log.BackColor = Color.FromArgb(0x0A, 0x14, 0x26);
            _log.ForeColor = Theme.TextWhite;
            _log.Font = new Font("Consolas", 9f);
            _log.WordWrap = false;
            _log.ScrollBars = RichTextBoxScrollBars.Both;
            _root.Controls.Add(_log);

            _status = new GhostLabel("就绪 · 点「开始安装」开始", Theme.TextDim, 9f, FontStyle.Regular);
            _status.SetBounds(24, 560, 732, 22);
            _root.Controls.Add(_status);

            Append("欢迎使用「" + Const.Product + "」安装程序。" , Color.FromArgb(0xC9, 0xD6, 0xEE));
            Append("安装目录：" + (_opt.InstallDir.Length > 0 ? _opt.InstallDir : Const.DefaultInstallDir()), Color.FromArgb(0xC9, 0xD6, 0xEE));
            if (_opt.InstallDir.Length > 0)
            {
                _txtDir.Text = _opt.InstallDir;
                _txtDir.Enabled = true;
            }
            else
            {
                _txtDir.Text = Const.DefaultInstallDir();
                _txtDir.Enabled = false;
            }
        }

        private static Icon LoadEmbeddedIcon()
        {
            try
            {
                string exe = Application.ExecutablePath;
                Icon ico = Icon.ExtractAssociatedIcon(exe);
                if (ico != null) return ico;
            }
            catch (Exception) { }
            return null;
        }

        private void Append(string text, Color c)
        {
            if (_log == null) return;
            _log.SelectionStart = _log.TextLength;
            _log.SelectionColor = c;
            _log.AppendText(text + Environment.NewLine);
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
            Application.DoEvents();
        }

        private void AppendLine(string text)
        {
            _steps.AppendLine(text);
            Color c = Theme.TextWhite;
            if (text.StartsWith("[x]")) c = Color.FromArgb(0xFF, 0x8A, 0x8A);
            else if (text.StartsWith("[!]")) c = Color.FromArgb(0xF2, 0xC9, 0x6B);
            else if (text.StartsWith("[i]")) c = Color.FromArgb(0x9E, 0xD8, 0xFF);
            else if (text.StartsWith("      ✓")) c = Color.FromArgb(0x8C, 0xE8, 0xA8);
            Append(text, c);
        }

        private void Browse()
        {
            try
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.Description = "选择「" + Const.Product + "」的安装目录（建议放在用户目录下，免管理员权限）";
                    d.ShowNewFolderButton = true;
                    string cur = Directory.Exists(_txtDir.Text) ? _txtDir.Text : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    d.SelectedPath = cur;
                    if (d.ShowDialog(this) == DialogResult.OK)
                    {
                        _txtDir.Text = Path.Combine(d.SelectedPath, Const.DataFolderName);
                        _txtDir.Enabled = true;
                    }
                }
            }
            catch (Exception ex) { Append("[!] 选择目录失败：" + ex.Message, Color.FromArgb(0xF2, 0xC9, 0x6B)); }
        }

        private void DoInstall()
        {
            _btnInstall.Enabled = false;
            _btnClose.Enabled = false;
            try
            {
                Options o = new Options();
                o.InstallDir = (_txtDir.Enabled && _txtDir.Text.Trim().Length > 0) ? _txtDir.Text : "";
                o.NoShortcuts = _opt.NoShortcuts || false;
                o.DesktopShortcut = _cbDesktop.Checked;
                o.NoLaunch = !_cbLaunch.Checked;
                o.DryRun = _opt.DryRun;
                o.Report = _opt.Report;

                Engine eng = new Engine(o, AppendLine);
                _status.Text = "正在安装…";
                int code = eng.Run(true);
                ResultCode = code;
                if (code == Const.ExitOk)
                {
                    _status.Text = "安装完成";
                    Append("=== 安装完成 ===", Color.FromArgb(0x8C, 0xE8, 0xA8));
                    MessageBox.Show(this,
                        "「" + Const.Product + "」安装完成！" + Environment.NewLine + Environment.NewLine
                        + "安装目录：" + (o.InstallDir.Length > 0 ? o.InstallDir : Const.DefaultInstallDir()) + Environment.NewLine
                        + (o.NoLaunch ? "请从开始菜单启动桌宠。" : "桌宠已启动，可在屏幕右下角托盘找到它。") + Environment.NewLine + Environment.NewLine
                        + "卸载：开始菜单 →「" + Const.Product + "」→ 卸载，或运行安装目录下的 " + Const.UninstallerName,
                        Const.Product + " 安装程序", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else if (code == Const.ExitCancelled)
                {
                    _status.Text = "已取消";
                }
                else
                {
                    _status.Text = "安装失败（详见日志）";
                    MessageBox.Show(this, "安装失败，请查看日志：" + Environment.NewLine + Const.LogPath,
                        Const.Product + " 安装程序", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                _btnInstall.Enabled = true;
                _btnClose.Enabled = true;
            }
            catch (Exception ex)
            {
                ResultCode = Const.ExitFailed;
                Log.Write("GUI 安装异常：" + ex);
                Append("[x] 安装失败：" + ex.Message, Color.FromArgb(0xFF, 0x8A, 0x8A));
                _status.Text = "安装失败";
                _btnInstall.Enabled = true;
                _btnClose.Enabled = true;
                MessageBox.Show(this, "安装失败：" + ex.Message + Environment.NewLine + "日志：" + Const.LogPath,
                    Const.Product + " 安装程序", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenPath(string path)
        {
            try
            {
                if (File.Exists(path)) Process.Start("notepad.exe", "\"" + path + "\"");
                else if (Directory.Exists(path)) Process.Start("explorer.exe", "\"" + path + "\"");
                else MessageBox.Show(this, "还不存在：" + path, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { MessageBox.Show(this, "打开失败：" + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }

    // 金框输入框（TextBox 不能自绘边框，外面套一层）
    internal class GoldenTextBox : Panel
    {
        public readonly TextBox Inner;

        public GoldenTextBox()
        {
            BackColor = Color.FromArgb(0x08, 0x12, 0x24);
            Padding = new Padding(4);
            Inner = new TextBox();
            Inner.BorderStyle = BorderStyle.None;
            Inner.BackColor = Color.FromArgb(0x08, 0x12, 0x24);
            Inner.ForeColor = Theme.TextWhite;
            Inner.Font = Theme.UiFont(9.5f, FontStyle.Regular);
            Inner.Dock = DockStyle.Fill;
            Controls.Add(Inner);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            using (Pen p = new Pen(Theme.GoldDim, 1.4f))
            {
                e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            }
        }
    }

    // ============================================================ P/Invoke
    internal static class Native
    {
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();
    }
}
