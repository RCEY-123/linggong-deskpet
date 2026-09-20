// ============================================================================
// Program.cs —— 入口：启动流程、全局错误处理、单实例、命令行参数
//   常用参数：
//     --selftest [--report=路径] [--render-ui=目录]   自检（不弹主界面）
//     --autostart                                     开机自启时使用（直接显示桌宠）
//     --ui                                            强制打开设置主界面
//     --version / --help
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AlDeskPet
{
    public static class Program
    {
        public static MainForm MainUi;
        public static bool ShuttingDown;
        /// <summary>首次运行要显示在主界面状态栏里的一句提示（用完清空）。</summary>
        public static string StartupTip = "";

        static Mutex _single;
        static EventWaitHandle _showSignal;
        static Thread _signalThread;

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll")]
        static extern bool FreeConsole();

        /// <summary>winexe 没有自己的控制台：自检时挂到父进程控制台上，让输出可见。</summary>
        static void AttachParentConsole()
        {
            try
            {
                if (!AttachConsole(-1)) return;
                StreamWriter outWriter = new StreamWriter(Console.OpenStandardOutput());
                outWriter.AutoFlush = true;
                Console.SetOut(outWriter);
                StreamWriter errWriter = new StreamWriter(Console.OpenStandardError());
                errWriter.AutoFlush = true;
                Console.SetError(errWriter);
            }
            catch { }
        }

        [STAThread]
        static int Main(string[] args)
        {
            bool selfTestCore = HasFlag(args, "--selftest");
            bool selfTestUi = HasFlag(args, "--selftest-ui");
            bool selfTestAll = HasFlag(args, "--selftest-all");
            bool selfTest = selfTestCore || selfTestUi || selfTestAll;
            SelfTest.Mode = selfTestUi ? "ui" : (selfTestCore ? "core" : "all");
            string report = ArgValue(args, "--report");
            string renderDir = ArgValue(args, "--render-ui");
            if (renderDir == null && HasFlag(args, "--render-ui")) renderDir = Path.Combine(Path.GetTempPath(), "aldeskpet-ui");

            // ---- 数据目录覆盖：--datadir=路径（测试 / 验收脚本用，绝不碰用户真实数据） ----
            // 与 Selftest 的 LockDataDir 同一套机制：锁定后 Config.Load 的 Normalize 也改不回去。
            string dataDirArg = ArgValue(args, "--datadir");
            if (!string.IsNullOrEmpty(dataDirArg))
            {
                try
                {
                    AppPaths.LockDataDir(dataDirArg);
                    Console.WriteLine("数据目录（命令行指定）：" + AppPaths.DataDir());
                }
                catch (Exception ex) { Log.Warn("命令行数据目录无效：" + ex.Message); }
            }

            if (HasFlag(args, "--version") || HasFlag(args, "-v"))
            {
                Console.WriteLine(AppPaths.ProductName + " v" + AppPaths.Version + " · " + AppPaths.Author);
                return 0;
            }
            if (HasFlag(args, "--help") || HasFlag(args, "-h") || HasFlag(args, "/?"))
            {
                Console.WriteLine(AppPaths.ProductName + " v" + AppPaths.Version + " · " + AppPaths.Author);
                Console.WriteLine("用法：");
                Console.WriteLine("  AzurLaneDeskPet.exe                  正常启动（首次会弹出桌宠设置界面）");
                Console.WriteLine("  AzurLaneDeskPet.exe --ui             强制打开设置主界面");
                Console.WriteLine("  AzurLaneDeskPet.exe --autostart      开机自启：直接显示桌宠，不弹设置界面");
                Console.WriteLine("  AzurLaneDeskPet.exe --selftest [--report=文件]");
                Console.WriteLine("                                       自检：功能核心回归（约 10 秒）");
                Console.WriteLine("  AzurLaneDeskPet.exe --selftest-ui [--report=文件] [--render-ui=目录]");
                Console.WriteLine("                                       自检：界面与交互回归（可顺带出截图）");
                Console.WriteLine("  AzurLaneDeskPet.exe --selftest-all [--report=文件]");
                Console.WriteLine("                                       自检：一次跑完上面两段");
                Console.WriteLine("  AzurLaneDeskPet.exe --import-builtin=<id> [--name=显示名] [--no-activate]");
                Console.WriteLine("                                       把内置角色导入到数据目录并设为当前角色");
                Console.WriteLine("  AzurLaneDeskPet.exe --datadir=<目录>  指定数据目录（测试用，不会碰真实数据）");
                Console.WriteLine("  AzurLaneDeskPet.exe --version");
                Console.WriteLine();
                Console.WriteLine("数据目录：" + AppPaths.DataDir());
                Console.WriteLine("日志目录：" + AppPaths.LogDir());
                return 0;
            }

            try { SetProcessDPIAware(); }
            catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // ---- 命令行导入内置角色：--import-builtin=chuyue[ --name=初月][ --no-activate] ----
            string importId = ArgValue(args, "--import-builtin");
            if (!string.IsNullOrEmpty(importId))
            {
                AttachParentConsole();
                AppPaths.EnsureDataDirs();
                Config.Load();
                AppPaths.EnsureDataDirs();
                List<string> ids = CharacterStore.BuiltinCharacterIds();
                if (!ids.Contains(importId))
                {
                    Console.WriteLine("没有这个内置角色：" + importId);
                    Console.WriteLine("可用： " + string.Join("、", ids.ToArray()));
                    return 3;
                }
                string wantName = ArgValue(args, "--name");
                CharacterProfile imported = CharacterStore.ImportBuiltinAs(importId, wantName);
                if (imported == null)
                {
                    Console.WriteLine("导入失败，详见日志：" + Log.CurrentLogFile());
                    return 3;
                }
                if (!HasFlag(args, "--no-activate"))
                {
                    Config.Current.activeCharacter = imported.id;
                    Config.Save();
                }
                Console.WriteLine("已导入内置角色：" + imported.DisplayName() + "（id=" + imported.id + "）");
                Console.WriteLine("立绘 " + imported.images.Count + " 张 / 台词 " + imported.lines.Count() + " 条 / 语音清单条目 "
                    + imported.voices.entries.Count);
                Console.WriteLine("数据目录：" + AppPaths.DataDir());
                return 0;
            }

            if (selfTest)
            {
                AttachParentConsole();
                if (string.IsNullOrEmpty(report))
                    report = Path.Combine(Path.GetTempPath(), "aldeskpet-selftest-report.txt");
                int code = SelfTest.Run(report, renderDir);
                // 用 Environment.Exit 固定退出码：自检会创建/销毁窗体，
                // 收尾阶段的残留消息可能影响隐式返回码，这里显式收口。
                try { Utils.WriteAllTextAtomic(report + ".exit", "exit=" + code + " at " + Utils.NowStamp()); }
                catch { }
                Console.Out.Flush();
                Environment.Exit(code);
                return code;
            }

            // ---------------- 正常运行 ----------------
            if (HasFlag(args, "--autostart") && !HasFlag(args, "--ui"))
            {
                // 开机自启：如果已经有一份在跑，就直接退出（并把已有实例的界面叫出来）
                EventWaitHandle existing = null;
                try { EventWaitHandle.TryOpenExisting("AzurLaneDeskPet.ShowUi", out existing); }
                catch { }
                if (existing != null)
                {
                    try { existing.Set(); } catch { }
                    existing.Close();
                    return 0;
                }
            }

            bool createdNew;
            _single = new Mutex(true, "AzurLaneDeskPet.SingleInstance", out createdNew);
            if (!createdNew)
            {
                // 已有实例：请求它把界面显示出来，然后退出
                try
                {
                    EventWaitHandle signal = null;
                    if (EventWaitHandle.TryOpenExisting("AzurLaneDeskPet.ShowUi", out signal))
                    {
                        signal.Set();
                        signal.Close();
                    }
                    else
                    {
                        MessageBox.Show("「" + AppPaths.ProductName + "」已经在运行了。\r\n\r\n请查看系统托盘图标，或右键桌面上的桌宠选择「打开设置界面」。",
                            AppPaths.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                catch
                {
                    MessageBox.Show("「" + AppPaths.ProductName + "」已经在运行了。", AppPaths.ProductName);
                }
                return 0;
            }

            AppPaths.EnsureDataDirs();
            Config.Load();
            AppPaths.EnsureDataDirs();   // 配置里可能指定了自定义数据目录
            Log.Verbose = Config.Current.logVerbose;
            Log.CleanupOldLogs(Config.Current.logKeepDays);
            Log.Info("=== " + AppPaths.ProductName + " v" + AppPaths.Version + " 启动 ===");
            Log.Info("安装目录：" + AppPaths.InstallDir());
            Log.Info("数据目录：" + AppPaths.DataDir());
            Audio.Configure(Config.Current.volume, Config.Current.sfxEnabled);

            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
            {
                Log.ErrorDialog("界面线程异常", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                Log.Error("未处理异常" + (e.IsTerminating ? "（进程即将退出）" : ""), ex);
                if (e.IsTerminating) FlushOnCrash();
            };

            // ---- 关机 / 注销：必须立刻放行，绝不弹窗、绝不取消 ----
            // 否则 Windows 会判定「此应用正在阻止关机」（桌宠在跑时尤其容易触发：主界面被请求关闭 →
            // 旧逻辑会 e.Cancel = true 把这条关机请求顶回去）。
            // 注意：WinForms 的 Application 没有 SessionEnding，系统级的在 Microsoft.Win32.SystemEvents。
            Microsoft.Win32.SystemEvents.SessionEnding += delegate (object s, Microsoft.Win32.SessionEndingEventArgs e)
            {
                try
                {
                    // 这个回调在专用线程上跑：收尾要碰窗口与托盘，必须回到 UI 线程做。
                    if (MainUi != null && !MainUi.IsDisposed && MainUi.IsHandleCreated)
                        MainUi.BeginInvoke((MethodInvoker)delegate { BeginSessionShutdown(CloseReason.WindowsShutDown); });
                    else
                        BeginSessionShutdown(CloseReason.WindowsShutDown);
                }
                catch
                {
                    try { ShuttingDown = true; } catch { }   // 兜底：至少别再拦着系统关机
                }
            };

            try
            {
                Bootstrap();
                StartSignalListener();

                bool autostart = HasFlag(args, "--autostart");
                bool forceUi = HasFlag(args, "--ui");
                bool hasCharacter = CharacterStore.ListAll().Count > 0;
                bool wantUi = forceUi || !Config.Current.firstRunDone || !hasCharacter;

                MainUi = new MainForm();

                if (wantUi && !autostart)
                {
                    Application.Run(MainUi);
                }
                else
                {
                    MainUi.Show();
                    MainUi.Hide();
                    PetManager.EnsureTray();
                    PetManager.ShowActivePet();
                    if (PetManager.IsRunning && autostart)
                        PetManager.Balloon(AppPaths.ProductName, "桌宠已经在桌面上了。双击托盘图标可以打开设置界面。");
                    Application.Run(new ApplicationContext());
                }
            }
            catch (Exception ex)
            {
                Log.ErrorDialog("启动失败", ex);
                return 1;
            }
            finally
            {
                Cleanup();
            }
            return 0;
        }

        static void FlushOnCrash()
        {
            try
            {
                Config.Save();
                Audio.StopAll();
                SystemWatch.Stop();
            }
            catch { }
        }

        static void Cleanup()
        {
            ShuttingDown = true;
            try { Config.Save(); } catch { }
            try { SystemWatch.Stop(); } catch { }
            try { PetManager.StopAllPets(); } catch { }
            try { PetManager.DisposeTray(); } catch { }
            try { Audio.StopAll(); } catch { }
            try { ImageCache.Clear(); } catch { }
            try { if (_showSignal != null) _showSignal.Set(); } catch { }
            Log.Info("=== 退出 ===");
        }

        /// <summary>
        /// 自检用：置 true 时关机收尾照常跑，但**不真的结束进程**
        /// （否则自检会把自己杀掉，拿不到报告）。
        /// </summary>
        public static bool SuppressExitForTest;

        /// <summary>
        /// 关机 / 注销 / 任务管理器结束任务时的收尾：先把该保存的保存好、把桌宠与托盘收掉，
        /// 再立刻退出进程。**不弹任何对话框、不取消系统请求**，否则就是「阻止关机」。
        /// </summary>
        public static void BeginSessionShutdown(CloseReason reason)
        {
            bool already = ShuttingDown;
            try
            {
                Log.SuppressDialogs = true;   // 收尾期间任何错误都只写日志，绝不弹窗卡住关机
                if (!already) Log.Info("系统要求关机 / 注销（" + reason + "）：保存设置并立刻退出");
                if (!already) Cleanup();
            }
            catch (Exception ex)
            {
                try { Log.Error("关机收尾异常", ex); } catch { }
            }
            if (already) return;
            if (SuppressExitForTest) return;
            try { Environment.Exit(0); } catch { }
        }

        /// <summary>供托盘 / 右键菜单调用：干净退出。</summary>
        public static void ShutdownApp()
        {
            ShuttingDown = true;
            try
            {
                if (MainUi != null && !MainUi.IsDisposed) MainUi.Hide();
            }
            catch { }
            Application.ExitThread();
            try { Application.Exit(); } catch { }
        }

        static void StartSignalListener()
        {
            try
            {
                _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "AzurLaneDeskPet.ShowUi");
                _signalThread = new Thread(delegate ()
                {
                    while (!ShuttingDown)
                    {
                        try
                        {
                            if (!_showSignal.WaitOne(1500)) continue;
                            if (ShuttingDown) return;
                            PetManager.ShowMainUi();
                        }
                        catch { return; }
                    }
                });
                _signalThread.IsBackground = true;
                _signalThread.Start();
            }
            catch (Exception ex)
            {
                Log.Warn("单实例信号通道创建失败（不影响使用）：" + ex.Message);
            }
        }

        /// <summary>首次运行：准备一个可用的默认角色，保证「打开就能用」。</summary>
        static void Bootstrap()
        {
            try
            {
                // 0) 内置角色「开箱齐 5 位」：装完不需要自己一个个导入，
                //    角色列表里直接就有 信浓 / 初月 / 欧根亲王 / 岛风 / 企业，点一下就能换。
                int seeded = SeedBuiltinCharacters();

                List<CharacterProfile> all = CharacterStore.ListAll();
                if (all.Count > 0)
                {
                    bool found = false;
                    foreach (CharacterProfile p in all) if (p.id == Config.Current.activeCharacter) found = true;
                    if (!found)
                    {
                        // 优先把默认角色设为信浓（内置的第一位）
                        CharacterProfile prefer = null;
                        foreach (CharacterProfile p in all)
                            if (string.Equals(p.builtinId, "shinano", StringComparison.OrdinalIgnoreCase)) prefer = p;
                        Config.Current.activeCharacter = prefer != null ? prefer.id : all[0].id;
                        Config.Save();
                    }
                    if (seeded > 0)
                        StartupTip = "已为你准备好内置角色：" + BuiltinSummary() + "。左侧列表里点一下就能换人。";
                    return;
                }

                CharacterProfile created = null;
                List<string> ids = CharacterStore.BuiltinCharacterIds();
                if (ids.Contains("shinano")) created = CharacterStore.ImportBuiltinAs("shinano", null);
                else if (ids.Count > 0) created = CharacterStore.ImportBuiltinAs(ids[0], null);

                if (created == null)
                {
                    // 没有内置素材（例如只拷了 exe）：造一个空白角色并填上基础台词
                    created = CharacterStore.Create("信浓", null);
                    if (created != null)
                    {
                        FillDefaultLines(created);
                        CharacterStore.Save(created);
                    }
                    Log.Warn("没有找到内置素材，已创建空白默认角色。");
                }
                if (created != null)
                {
                    Config.Current.activeCharacter = created.id;
                    Config.Save();
                    Log.Info("首次运行：已准备默认角色 " + created.DisplayName());
                    StartupTip = "首次使用：默认角色「" + created.DisplayName() + "」用的是内置示例素材（含 16 条语音示范）。"
                        + "想要真实立绘可以点左侧「用内置模板新建…」选岛风 / 新泽西 / 武藏 / 企业，或直接「导入图片…」换成你自己的立绘。";
                }
            }
            catch (Exception ex)
            {
                Log.Error("初始化默认角色失败", ex);
            }
        }

        /// <summary>角色列表里"内置那几位"的显示名汇总，用于状态栏提示。</summary>
        static string BuiltinSummary()
        {
            List<string> names = new List<string>();
            foreach (CharacterProfile p in CharacterStore.ListAll())
                if (!string.IsNullOrEmpty(p.builtinId) && !names.Contains(p.DisplayName()))
                    names.Add(p.DisplayName());
            return names.Count == 0 ? "内置角色" : string.Join(" / ", names.ToArray());
        }

        /// <summary>
        /// 把内置角色补齐到角色列表里（幂等、非破坏性）：
        ///   · 只"添加还没给过这个数据目录的那几位"，绝不改动、绝不删除用户已有的任何角色；
        ///   · 判重优先看 profile 里的 builtinId，老版本（没有这个字段）按显示名比对；
        ///   · **已提供过的模板记在 config 的 seededBuiltin 里**：用户自己删掉某位之后不会再被补回来；
        ///     但**新版本新增的内置角色**（名单里没有的）会在升级后再补一次 —— v1.1.0 加 8 位新角色靠这个；
        ///   · 老配置（只有 builtinSeed=1、没有 seededBuiltin）视为"已提供过当时那批"：
        ///     用 builtinSeed 记下的老名单兜底，避免把用户删掉的老角色又补回来；
        ///   · 自检场景钉死了数据目录，直接跳过。
        /// </summary>
        static int SeedBuiltinCharacters()
        {
            try
            {
                if (AppPaths.IsDataDirLocked()) return 0;

                List<string> ids = CharacterStore.BuiltinCharacterIds();
                if (ids.Count == 0) return 0;                    // 没有内置素材（只拷了 exe）

                // 已经"提供过"的模板 id：新字段优先；老配置（builtinSeed>=1 但没有名单）按 v1.0.x 的老五位兜底
                List<string> offered = ParseIdList(Config.Current.seededBuiltin);
                bool legacySeeded = offered.Count == 0 && Config.Current.builtinSeed >= 1;
                if (legacySeeded) offered = new List<string>(LegacyBuiltinIds);

                // 还没提供过的模板 → 这次要补的
                List<string> pending = new List<string>();
                foreach (string id in ids)
                    if (!HasId(offered, id)) pending.Add(id);
                if (pending.Count == 0)
                {
                    if (Config.Current.seededBuiltin != string.Join(",", ids.ToArray()))
                    {
                        Config.Current.seededBuiltin = string.Join(",", ids.ToArray());
                        Config.Current.builtinSeed = 1;
                        Config.Save();
                    }
                    return 0;
                }

                // 已有的角色：先按 builtinId 认，老角色按显示名认
                List<CharacterProfile> all = CharacterStore.ListAll();
                List<string> haveIds = new List<string>();
                List<string> haveNames = new List<string>();
                foreach (CharacterProfile p in all)
                {
                    if (!string.IsNullOrEmpty(p.builtinId)) haveIds.Add(p.builtinId.ToLowerInvariant());
                    haveNames.Add(p.DisplayName());
                }

                // 期望顺序：信浓在最前，其余按内置目录名排
                List<string> order = new List<string>();
                if (ids.Contains("shinano")) order.Add("shinano");
                foreach (string id in ids) if (!order.Contains(id)) order.Add(id);

                int added = 0;
                foreach (string id in order)
                {
                    if (HasId(offered, id)) continue;   // 已经提供过（含用户删掉的）
                    if (haveIds.Contains(id.ToLowerInvariant())) continue;
                    if (haveNames.Contains(CharacterStore.BuiltinDisplayName(id))) continue;   // 老版本导入的，按名字认出来
                    CharacterProfile p = CharacterStore.ImportBuiltinAs(id, null);
                    if (p != null)
                    {
                        added++;
                        haveIds.Add(id.ToLowerInvariant());
                        haveNames.Add(p.DisplayName());
                    }
                }

                // 记录"已经提供过全部内置模板"（包括用户删掉的：下次不再补）
                Config.Current.seededBuiltin = string.Join(",", ids.ToArray());
                Config.Current.builtinSeed = 1;
                Config.Save();
                if (added > 0) Log.Info("已补齐内置角色 " + added + " 位（开箱即用）");
                return added;
            }
            catch (Exception ex)
            {
                Log.Error("补齐内置角色失败（不影响使用）", ex);
                return 0;
            }
        }

        /// <summary>v1.0.x 的内置角色名单：老配置没有 seededBuiltin 字段时用它兜底，避免复活用户删掉的角色。</summary>
        static readonly string[] LegacyBuiltinIds = new string[]
        {
            "shinano", "chuyue", "prinz_eugen", "shimakaze", "enterprise"
        };

        /// <summary>忽略大小写的 id 命中判断（不引 LINQ，保持零依赖风格）。</summary>
        static bool HasId(List<string> list, string id)
        {
            if (list == null || string.IsNullOrEmpty(id)) return false;
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }        static List<string> ParseIdList(string csv)
        {
            List<string> list = new List<string>();
            if (string.IsNullOrEmpty(csv)) return list;
            foreach (string s in csv.Split(','))
            {
                string t = s.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        static void FillDefaultLines(CharacterProfile p)
        {
            string[] greet = new string[] { "你好，指挥官。", "你终于回来了…", "妾身…等汝许久了…", "今天也一起加油吧。" };
            string[] click = new string[]
            {
                "妾身…困了…", "命运…如是…", "指挥官…过来…", "梦里…亦有汝在…",
                "累了便枕于妾身怀中罢…", "莫要熬夜，妾身陪汝到梦尽处…", "有汝在，命运亦不足为惧…"
            };
            string[] proactive = new string[] { "你还在么…？", "偶尔也歇一歇吧…", "妾身在看着汝呢…" };
            string[] idle = new string[] { "你怎么不理我…", "妾身…又睡着了…", "呼姆…汝还在忙么…？" };
            string[] system = new string[] { "汝刚刚是在玩 {app} 么…？好玩么…？", "原来是 {app}…妾身记下了。" };

            foreach (string s in greet) p.lines.greet.Add(new LineItem(s, 6));
            foreach (string s in click) p.lines.click.Add(new LineItem(s, 3));
            foreach (string s in proactive) p.lines.proactive.Add(new LineItem(s, 4));
            foreach (string s in idle) p.lines.idle.Add(new LineItem(s, 3));
            foreach (string s in system) p.lines.system.Add(new LineItem(s, 4));
            p.lines.greet[0].size = 20;
            p.lines.greet[0].bold = true;
            p.lines.greet[0].color = "gold";
            p.lines.click[0].size = 22;
            p.lines.click[0].bold = true;
            p.card.persona = "桌面宠物角色，性格温柔、带着一点宿命感，把玩家当作并肩的指挥官。";
            p.card.speechStyle = "说话慢而软，自称「妾身」，偶尔用「呼姆」「唔」；句尾常带省略号。";
            p.card.greeting = "指挥官…你终于回来了。";
            p.card.userName = "指挥官";
        }

        // ---------------- 命令行工具 ----------------

        public static bool HasFlag(string[] args, string flag)
        {
            foreach (string a in args)
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static string ArgValue(string[] args, string name)
        {
            foreach (string a in args)
            {
                if (a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return a.Substring(name.Length + 1).Trim('"');
            }
            return null;
        }
    }
}
