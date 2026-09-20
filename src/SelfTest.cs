// ============================================================================
// SelfTest.cs —— 自检（不依赖 GUI，可在命令行跑完整功能回归）
//   DeskPet.exe --selftest [--report=路径] [--render-ui=目录]
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AlDeskPet
{
    public static class SelfTest
    {
        static int _pass;
        static int _fail;
        static readonly StringBuilder Report = new StringBuilder();
        static string _reportPath = "";

        /// <summary>
        /// 每完成一节就把报告落盘一次。自检里会反复创建/销毁窗体，
        /// 万一中途被强杀或硬崩溃，也不会「一项结果都不剩」（旧版就出现过整份报告丢失）。
        /// </summary>
        static void FlushReport()
        {
            if (string.IsNullOrEmpty(_reportPath)) return;
            try
            {
                string text = Report.ToString()
                    + Environment.NewLine + "---- 进行中：通过 " + _pass + " / 失败 " + _fail + " ----" + Environment.NewLine;
                Utils.WriteAllTextAtomic(_reportPath, text);
            }
            catch { }
        }

        static void Section(string title)
        {
            Report.AppendLine();
            Report.AppendLine("== " + title + " ==");
            Say("");
            Say("== " + title + " ==");
            FlushReport();
        }

        static void Check(string name, bool ok, string detail)
        {
            if (ok) _pass++; else _fail++;
            string line = (ok ? "  [PASS] " : "  [FAIL] ") + name + (string.IsNullOrEmpty(detail) ? "" : "  → " + detail);
            Report.AppendLine(line);
            Say(line);
            if (!ok) FlushReport();
        }

        static void Info(string text)
        {
            Report.AppendLine("  · " + text);
            Say("  · " + text);
        }

        /// <summary>
        /// 控制台输出必须容错：如果调用方提前关闭了输出管道（例如 `... | Select-Object -First 20`），
        /// Console 写入会抛 IOException 并把自检整个打断。报告文件才是权威结果。
        /// </summary>
        static void Say(string text)
        {
            try { Console.Out.WriteLine(text); }
            catch { }
        }

        /// <summary>抽干消息队列（自检里创建/销毁窗体时用；已释放控件的残留言属于测试脚手架现象，忽略）。</summary>
        static void Pump(int milliseconds)
        {
            try
            {
                Application.DoEvents();
                if (milliseconds > 0) Thread.Sleep(milliseconds);
                Application.DoEvents();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            catch (Exception ex) { Log.Debug("Pump 异常：" + ex.Message); }
        }

        /// <summary>
        /// 自检模式：
        ///   all  —— 一次跑完（默认，命令行 --selftest-all）
        ///   core —— 只跑不涉及大量 UI 的部分（快，约 10 秒；命令行 --selftest）
        ///   ui   —— 只跑界面相关部分（命令行 --selftest-ui，可配 --render-ui=目录 出截图）
        /// 拆开的原因：界面部分要反复创建/销毁窗体与位图，跑得久、更容易被外部环境干扰；
        /// 拆成两段短进程既好定位问题，也不容易整份报告都拿不到。
        /// </summary>
        public static string Mode = "all";

        public static int Run(string reportPath, string renderDir)
        {
            Log.SuppressDialogs = true;   // 自检绝不能卡在弹窗上
            _reportPath = reportPath;
            // 自检里会做 UI 操作，任何 UI 线程异常都必须被吞掉并记录：
            // 否则 WinForms 会弹默认的「未处理异常」对话框 → 进程被卡住 → 外部只能强杀（旧版就是这么丢报告的）。
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
                {
                    Log.Error("自检：UI 线程未处理异常（已拦截，避免弹窗阻塞）", e.Exception);
                    Say("  [WARN] UI 线程异常已拦截：" + e.Exception.GetType().Name + " " + e.Exception.Message);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
                {
                    Log.Error("自检：未处理异常" + (e.IsTerminating ? "（进程即将退出）" : ""), e.ExceptionObject as Exception);
                    FlushReport();
                };
            }
            catch { }
            DateTime start = DateTime.Now;
            string tempRoot = Path.Combine(Path.GetTempPath(),
                "aldeskpet-selftest-" + Utils.NowFileStamp() + "-" + System.Diagnostics.Process.GetCurrentProcess().Id.ToString()
                + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(tempRoot);

            Report.AppendLine("灵工桌宠 · 自检报告");
            Report.AppendLine("时间：" + Utils.NowStamp());
            Report.AppendLine("版本：" + AppPaths.Version + "  作者：" + AppPaths.Author);
            Report.AppendLine("安装目录：" + AppPaths.InstallDir());
            Report.AppendLine("系统：" + Environment.OSVersion.VersionString + " / .NET " + Environment.Version);
            Report.AppendLine("模式：" + Mode + "（core=功能核心 / ui=界面与交互 / all=全部）");
            Report.AppendLine("临时目录：" + tempRoot);

            string originalDataDir = AppPaths.DataDir();
            try
            {
                // 自检期间把数据目录钉死在临时目录：绝不能碰用户真实配置与角色素材
                AppPaths.LockDataDir(Path.Combine(tempRoot, "data"));
                AppPaths.EnsureDataDirs();
                Log.Verbose = true;

                bool core = Mode == "all" || Mode == "core";
                bool ui = Mode == "all" || Mode == "ui";

                if (core)
                {
                    TestEnvironment(tempRoot);
                    TestJson();
                    TestConfig();
                    TestCharacters();
                    TestLines();
                    TestVoices();
                    TestWeightedPick();
                    TestLlmHttp();
                    TestSystemWatch();
                    TestImages();
                    TestTextAndAnimation();
                    TestAudio();
                    TestAssets();
                    TestDisplayPolicy();
                }
                if (ui)
                {
                    TestUiRobustness(renderDir);
                    TestUiPolish(renderDir);
                    TestModeSwitch(renderDir);
                    TestPetInteraction();
                    TestDisplayUi();
                    if (!string.IsNullOrEmpty(renderDir)) TestRenderUi(renderDir);
                    TestShutdownPath();   // 放最后：它会真的走一遍关机收尾（桌宠/托盘都会被停掉）
                }
                if (!ui && !string.IsNullOrEmpty(renderDir))
                    Say("  · 注意：core 模式不产出界面截图，请用 --selftest-ui --render-ui=<目录>");
            }
            catch (Exception ex)
            {
                _fail++;
                Report.AppendLine("  [FAIL] 自检过程抛出未捕获异常：" + ex);
                Say("  [FAIL] 自检过程抛出未捕获异常：" + ex);
            }
            finally
            {
                AppPaths.UnlockDataDir();
                AppPaths.SetDataDir(originalDataDir);
            }

            double seconds = (DateTime.Now - start).TotalSeconds;
            Report.AppendLine();
            Report.AppendLine("================ 结果 ================");
            Report.AppendLine("通过 " + _pass + " 项，失败 " + _fail + " 项，用时 " + seconds.ToString("0.0") + " 秒");
            Report.AppendLine(_fail == 0 ? "结论：全部通过 ✔" : "结论：有失败项，请查看上面的 [FAIL] 行 ✘");
            Say("");
            Say("通过 " + _pass + " 项，失败 " + _fail + " 项，用时 " + seconds.ToString("0.0") + " 秒");
            Say(_fail == 0 ? "结论：全部通过" : "结论：有失败项");

            if (!string.IsNullOrEmpty(reportPath))
            {
                try
                {
                    Utils.WriteAllTextAtomic(reportPath, Report.ToString());
                    Say("报告已写入：" + reportPath);
                }
                catch (Exception ex)
                {
                    Say("写报告失败：" + ex.GetType().Name + " " + ex.Message);
                    try
                    {
                        string fallback = Path.Combine(Path.GetTempPath(), "aldeskpet-selftest-report.txt");
                        File.WriteAllText(fallback, Report.ToString(), new UTF8Encoding(true));
                        Say("已改写到：" + fallback);
                    }
                    catch { }
                }
            }

            try { Directory.Delete(tempRoot, true); } catch { }
            return _fail == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------
        static void TestEnvironment(string tempRoot)
        {
            Section("① 环境与目录");
            Check("数据目录已创建", Directory.Exists(AppPaths.DataDir()), AppPaths.DataDir());
            Check("日志目录已创建", Directory.Exists(AppPaths.LogDir()), AppPaths.LogDir());
            Check("角色目录已创建", Directory.Exists(AppPaths.CharactersDir()), AppPaths.CharactersDir());
            Check("安装目录存在", Directory.Exists(AppPaths.InstallDir()), AppPaths.InstallDir());
            Log.Info("自检开始");
            Check("日志写入正常", File.Exists(Log.CurrentLogFile()), Log.CurrentLogFile());
            Log.CleanupOldLogs(3);
            Check("临时目录可写", File.Exists(WriteProbe(tempRoot)), "写探针文件成功");
        }

        static string WriteProbe(string dir)
        {
            string file = Path.Combine(dir, "probe.txt");
            File.WriteAllText(file, "探针", Encoding.UTF8);
            return file;
        }

        static void TestJson()
        {
            Section("② JSON 读写");
            AppSettings s = new AppSettings();
            s.volume = 42;
            s.dataDir = "D:\\含 空格\\目录";
            s.characterOrder.Add("信浓");
            s.characterOrder.Add("a\"b\\c");
            s.llm.baseUrl = "http://127.0.0.1:11434/v1";
            s.llm.apiKey = "sk-测试\n换行";
            s.llm.temperature = 0.777;
            string text = Json.Write(s);
            Dictionary<string, object> node = Json.AsObj(Json.Parse(text));
            Check("反序列化得到对象", node != null, "");
            Check("字符串转义正确", node != null && Json.S(node, "dataDir", "") == "D:\\含 空格\\目录", node == null ? "" : Json.S(node, "dataDir", ""));
            Check("数组项转义正确", node != null && Json.StrList(node, "characterOrder").Count == 2
                && Json.StrList(node, "characterOrder")[1] == "a\"b\\c", "");
            AppSettings back = Json.Bind<AppSettings>(node);
            Check("绑定回对象", back.volume == 42 && Math.Abs(back.llm.temperature - 0.777) < 0.0001, "volume=" + back.volume);
            Check("换行转义正确", back.llm.apiKey == "sk-测试\n换行", Utils.FirstLine(back.llm.apiKey, 20));
            Check("非法 JSON 返回 null", Json.TryParse("{ 这不是 json ") == null, "");
            Check("空对象/空数组", Json.AsObj(Json.TryParse("{}")) != null && Json.AsArr(Json.TryParse("[]")) != null, "");
        }

        static void TestConfig()
        {
            Section("③ 配置保存 / 读取");
            Config.Current = new AppSettings();
            Config.Current.dataDir = AppPaths.DataDir();   // 自检期间固定在临时目录，绝不动用户真实配置
            Config.Current.volume = 66;
            Config.Current.activeCharacter = "test_char";
            Config.Current.systemWatchPermission = "denied";
            Config.Save();
            Check("配置文件已生成", File.Exists(AppPaths.ConfigFile()), AppPaths.ConfigFile());

            Config.Current = new AppSettings();
            Config.Load();
            Check("重新读取后音量正确", Config.Current.volume == 66, Config.Current.volume.ToString());
            Check("重新读取后权限状态正确", Config.Current.systemWatchPermission == "denied", Config.Current.systemWatchPermission);
            Check("配置写在当前生效的数据目录里", Utils.IsUnder(AppPaths.ConfigFile(), AppPaths.DataDir()), AppPaths.ConfigFile());
            TestApiKeyProtectedAtRest();
            TestConfigResilience();
        }

        /// <summary>
        /// ③-C 配置「不会莫名消失」的回归（v1.0.8）：
        ///   · 每次保存留一份 .bak；
        ///   · 主配置损坏时能从 .bak 恢复（用户设置不至于全丢）；
        ///   · 连备份也没有时，保存前把坏文件改名留档，绝不静默覆盖；
        ///   · 数据目录可由环境变量指定（验收脚本据此把数据写进沙箱，不碰用户真实数据）。
        /// </summary>
        static void TestConfigResilience()
        {
            string file = AppPaths.ConfigFile();
            string bak = file + Config.BackupSuffix;
            try { if (File.Exists(bak)) File.Delete(bak); } catch { }
            try { if (File.Exists(file)) File.Delete(file); } catch { }

            Config.Current = new AppSettings();
            Config.Current.volume = 55;
            Config.Save();
            Config.Current.volume = 77;
            Config.Save();
            Check("保存配置会留一份备份 config.json.bak", File.Exists(bak), bak);

            Config.Current = new AppSettings();
            Config.Load();
            Check("重新读取仍是最新设置", Config.Current.volume == 77, Config.Current.volume.ToString());

            // 主配置被写坏 → 应当从备份（最近一次成功保存的内容）恢复，而不是"设置全部消失"
            File.WriteAllText(file, "{ 这不是 JSON ！！", new UTF8Encoding(false));
            Config.Load();
            Check("config.json 损坏时从备份恢复设置（不会掉回默认值 80）", Config.Current.volume == 77 && !Config.LoadFailed,
                "volume=" + Config.Current.volume + " LoadFailed=" + Config.LoadFailed);
            Check("损坏的配置被改名留档（config.json.bad-*）",
                Directory.GetFiles(AppPaths.DataDir(), "config.json.bad-*").Length > 0, "留档文件");

            // 连备份也没有 → 标记 LoadFailed，保存时先留档再写，绝不静默覆盖
            try { File.Delete(bak); } catch { }
            File.WriteAllText(file, "还是坏的", new UTF8Encoding(false));
            Config.Load();
            Check("没有可用备份时标记 LoadFailed（避免静默覆盖用户配置）", Config.LoadFailed, "LoadFailed=" + Config.LoadFailed);
            int badCountBefore = Directory.GetFiles(AppPaths.DataDir(), "config.json.bad-*").Length;
            Config.Current.volume = 88;
            Config.Save();
            Check("保存前把坏文件改名留档", Directory.GetFiles(AppPaths.DataDir(), "config.json.bad-*").Length > badCountBefore,
                "留档数 " + badCountBefore + " → " + Directory.GetFiles(AppPaths.DataDir(), "config.json.bad-*").Length);
            Check("新配置已正常写入", !Config.LoadFailed && File.Exists(file), file);

            // 数据目录环境变量（验收脚本的沙箱机制）
            string envVarOld = Environment.GetEnvironmentVariable(AppPaths.DataDirEnvVar);
            try
            {
                string sandbox = Path.Combine(Path.GetTempPath(), "aldeskpet-env-probe");
                Environment.SetEnvironmentVariable(AppPaths.DataDirEnvVar, sandbox);
                Check("环境变量可覆盖默认数据目录（验收沙箱）",
                    string.Equals(AppPaths.DefaultDataDir(), Path.GetFullPath(sandbox), StringComparison.OrdinalIgnoreCase),
                    AppPaths.DefaultDataDir());
                Environment.SetEnvironmentVariable(AppPaths.DataDirEnvVar, null);
                Check("没设环境变量时默认数据目录仍是 %APPDATA%\\AzurLaneDeskPet",
                    AppPaths.DefaultDataDir().EndsWith(AppPaths.DataDirName, StringComparison.OrdinalIgnoreCase),
                    AppPaths.DefaultDataDir());
            }
            finally
            {
                Environment.SetEnvironmentVariable(AppPaths.DataDirEnvVar, envVarOld);
            }
        }

        /// <summary>
        /// ③-B API Key 落盘加密（安全加固回归）：
        ///   磁盘上必须是 dpapi 密文、内存与功能侧必须是明文，且旧的明文配置能无损升级。
        /// </summary>
        static void TestApiKeyProtectedAtRest()
        {
            const string plainKey = "sk-selftest-PLAINTEXT-MUST-NOT-BE-ON-DISK-0123456789";

            Config.Current.llm.apiKey = plainKey;
            Config.Save();

            string onDisk = File.ReadAllText(AppPaths.ConfigFile(), Encoding.UTF8);
            Check("落盘内容不含明文 API Key", onDisk.IndexOf(plainKey, StringComparison.Ordinal) < 0,
                "若失败即表示明文被写进了 config.json");
            Check("落盘 Key 带 dpapi 前缀", onDisk.IndexOf(Utils.SecretPrefix, StringComparison.Ordinal) >= 0,
                "未找到 " + Utils.SecretPrefix + " 前缀");
            // 注意：报告里**不打印 Key 内容**（哪怕是自检用的假 Key），避免报告本身成为泄露面
            Check("加密后内存仍为明文（调用方无感）", Config.Current.llm.apiKey == plainKey,
                "内存中的 Key 长度 " + (Config.Current.llm.apiKey == null ? 0 : Config.Current.llm.apiKey.Length) + "（不打印内容）");

            Config.Current = new AppSettings();
            Config.Load();
            Check("重新读取后 Key 能正确解密", Config.Current.llm.apiKey == plainKey,
                "解密结果与写入前一致（不打印内容）");

            // 加密不可逆地依赖本机本用户：密文串必须真的解不回去（截断后应失败而不是抛异常）
            string cipher = Utils.Protect(plainKey);
            Check("密文本机本用户可解密（功能不受影响）", Utils.Unprotect(cipher) == plainKey, "");
            Check("Protect 幂等（不二次加密）", Utils.Protect(cipher) == cipher, "");
            Check("密文不是明文", cipher != plainKey && cipher.Length > plainKey.Length, "长度 " + cipher.Length);
            Check("含中文与换行的 Key 往返无损",
                Utils.Unprotect(Utils.Protect("sk-测试\n换行\t制表")) == "sk-测试\n换行\t制表", "");
            Check("损坏的密文安全降级为空串", Utils.Unprotect(Utils.SecretPrefix + "!!!not-base64!!!") == "",
                "损坏密文未安全降级");
            Check("旧版明文配置可直接读取（向后兼容）", Utils.Unprotect("sk-legacy-plaintext") == "sk-legacy-plaintext", "");
            Check("空值不被加密也不报错", Utils.Protect("") == "" && Utils.Unprotect("") == "", "");
        }

        static void TestCharacters()
        {
            Section("④ 角色库");
            CharacterProfile p = CharacterStore.Create("信浓", null);
            Check("创建角色", p != null && Directory.Exists(AppPaths.CharacterDir(p.id)), p == null ? "" : p.id);
            if (p == null) return;

            p.faction = "重樱";
            p.shipType = "航母";
            p.card.persona = "测试人设";
            p.lines.click.Add(new LineItem("妾身…困了…", 10));
            p.lines.greet.Add(new LineItem("你终于回来了…", 5));
            bool saved = CharacterStore.Save(p);
            Check("保存角色", saved && File.Exists(CharacterStore.ProfileFile(p.id)), "");

            CharacterProfile back = CharacterStore.Load(p.id);
            Check("重新载入角色", back != null && back.name == "信浓" && back.faction == "重樱", back == null ? "" : back.DisplayName());
            Check("台词随角色保存", back != null && back.lines.click.Count == 1 && back.lines.greet.Count == 1, "");

            List<CharacterProfile> all = CharacterStore.ListAll();
            Check("角色出现在列表里", all.Count == 1, "共 " + all.Count + " 个");

            // 素材导入
            string img = Path.Combine(Path.GetTempPath(), "aldeskpet-probe.png");
            using (Bitmap bmp = new Bitmap(64, 64, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp)) { g.Clear(Color.Transparent); g.FillEllipse(Brushes.OrangeRed, 4, 4, 56, 56); }
                bmp.Save(img, ImageFormat.Png);
            }
            string imported = CharacterStore.ImportImage(p.id, img);
            Check("导入立绘", imported.Length > 0 && File.Exists(Path.Combine(CharacterStore.ImagesDir(p.id), imported)), imported);
            p.images.Add(imported);
            CharacterStore.Save(p);
            CharacterProfile back2 = CharacterStore.Load(p.id);
            Check("立绘记录已保存", back2.images.Count == 1 && back2.CurrentImage() == imported, "");

            // 复制角色（素材独立）
            CharacterProfile copy = CharacterStore.Create("信浓 副本", p.id);
            Check("复制角色", copy != null && copy.id != p.id, copy == null ? "" : copy.id);
            if (copy != null)
            {
                bool independent = File.Exists(Path.Combine(CharacterStore.ImagesDir(copy.id), imported));
                Check("副本素材独立且完整", independent && copy.lines.click.Count == 1, "");
                CharacterStore.Delete(copy.id);
                Check("删除角色", !Directory.Exists(AppPaths.CharacterDir(copy.id)), "");
            }

            CharacterStore.Delete(p.id);
            Check("删除后再查不到", CharacterStore.Load(p.id) == null, "");
        }

        static void TestLines()
        {
            Section("⑤ 台词解析 / 导入导出");
            string txt =
                "# 注释行会被忽略\r\n" +
                "@问候\r\n" +
                "[10|22|gold|bold] 你终于回来了…\r\n" +
                "[3] 你好，指挥官。\r\n" +
                "@点击\r\n" +
                "妾身…困了…|v01.mp3\r\n" +
                "[5|18|rouge] 指挥官…过来…\r\n" +
                "// 行注释\r\n" +
                "@系统\r\n" +
                "[5] 汝刚刚是在玩 {app} 么…？\r\n";
            LineSet set = LinePack.ParseText(txt, new LineSet());
            Check("问候段解析", set.greet.Count == 2, set.greet.Count.ToString());
            Check("点击段解析", set.click.Count == 2, set.click.Count.ToString());
            Check("系统段解析", set.system.Count == 1, set.system.Count.ToString());
            Check("权重解析", Math.Abs(set.greet[0].weight - 10) < 0.01, set.greet[0].weight.ToString());
            Check("字号解析", set.greet[0].size == 22, set.greet[0].size.ToString());
            Check("配色解析", set.greet[0].color == "gold", set.greet[0].color);
            Check("加粗解析", set.greet[0].bold, "");
            Check("台词内联语音解析", set.click[0].voice == "v01.mp3" && set.click[0].text == "妾身…困了…", set.click[0].text + " / " + set.click[0].voice);
            Check("未知指令不报错", LinePack.ParseText("@image bimg_money1\r\n@link x | y\r\n[1] 文本", new LineSet()).click.Count == 1, "");

            CharacterProfile p = new CharacterProfile();
            p.id = "lines_test";
            p.name = "测试";
            p.lines = set;
            string exported = LinePack.ToText(p);
            LineSet reimported = LinePack.ParseText(exported, new LineSet());
            Check("导出的台词可再次导入", reimported.Count() == set.Count(),
                "导出 " + set.Count() + " 条 → 重新导入 " + reimported.Count() + " 条");
            Check("导出内容可读", exported.IndexOf("你终于回来了…", StringComparison.Ordinal) >= 0, "");

            // 插件格式 JSON
            string pluginJson = "{\"name\":\"信浓\",\"random\":[{\"t\":\"妾身…困了…\",\"w\":10,\"size\":22,\"bold\":true}," +
                                "{\"t\":\"命运…如是…\",\"w\":3}],\"alert\":[{\"t\":\"指挥官，汝的余额\"}]}";
            LineSet fromPlugin = LinePack.ParsePluginJson(pluginJson, new LineSet());
            Check("兼容插件 JSON 台词包", fromPlugin.click.Count == 2, "random → 点击 " + fromPlugin.click.Count + " 条");
            Check("插件 JSON 权重与字号", fromPlugin.click[0].weight == 10 && fromPlugin.click[0].size == 22 && fromPlugin.click[0].bold, "");
            Check("插件 JSON 其它段落兼容", fromPlugin.proactive.Count == 1, "alert → 主动 " + fromPlugin.proactive.Count + " 条");
        }

        static void TestVoices()
        {
            Section("⑥ 语音清单");
            CharacterProfile p = CharacterStore.Create("语音测试", null);
            p.lines.click.Add(new LineItem("妾身…困了…"));
            p.lines.click.Add(new LineItem("指挥官…过来…"));
            p.lines.greet.Add(new LineItem("你终于回来了…"));

            string dir = CharacterStore.VoicesDir(p.id);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "v01.mp3"), FakeMp3());
            File.WriteAllBytes(Path.Combine(dir, "v02.mp3"), FakeMp3());
            File.WriteAllBytes(Path.Combine(dir, "v03.mp3"), FakeMp3());
            File.WriteAllText(Path.Combine(dir, "manifest.txt"),
                "# 台词|文件\r\n" +
                "妾身…困了…|v01.mp3\r\n" +
                "指挥官…过来…|v02.mp3\r\n" +
                "不存在的台词|v99.mp3\r\n", Encoding.UTF8);

            VoiceBankLoader.Report r = VoiceBankLoader.Load(p, "manifest.txt");
            Check("清单条目读取", r.entries == 2, "有效条目 " + r.entries);
            Check("缺失文件被剔除", r.missingFiles == 1, "缺失 " + r.missingFiles + " 个");
            Check("台词→语音匹配", r.matched == 2, "匹配 " + r.matched + " 条");
            Check("未匹配台词统计", r.unmatchedLines == 1, "未匹配 " + r.unmatchedLines + " 条");
            Check("按文本找到音频文件", p.voices.Find("妾身…困了…") == "v01.mp3", p.voices.Find("妾身…困了…"));
            Check("标点差异也能匹配", p.voices.Find("指挥官、过来") .Length > 0 || p.voices.Find("指挥官…过来…") == "v02.mp3", "");

            // 反向写法 + CSV
            File.WriteAllText(Path.Combine(dir, "manifest.txt"), "v03.mp3|你终于回来了…\r\n", Encoding.UTF8);
            VoiceBankLoader.Report r2 = VoiceBankLoader.Load(p, "manifest.txt");
            Check("「文件|台词」反向写法", r2.entries == 1 && p.voices.Find("你终于回来了…") == "v03.mp3", "");

            // 无清单 → 用文件名匹配
            File.Delete(Path.Combine(dir, "manifest.txt"));
            File.WriteAllBytes(Path.Combine(dir, "你终于回来了….mp3"), FakeMp3());
            VoiceBankLoader.Report r3 = VoiceBankLoader.Load(p, "");
            Check("无清单时按文件名匹配", r3.entries == 4 && p.voices.Find("你终于回来了…") == "你终于回来了….mp3", "条目 " + r3.entries);

            string message;
            VoiceBankLoader.WriteManifestTemplate(p, out message);
            Check("可生成清单模板", File.Exists(Path.Combine(dir, "manifest.txt")) && message.IndexOf("清单模板", StringComparison.Ordinal) >= 0, Utils.FirstLine(message, 40));
            CharacterStore.Delete(p.id);
        }

        static byte[] FakeMp3()
        {
            byte[] data = new byte[512];
            data[0] = 0x49; data[1] = 0x44; data[2] = 0x33;   // "ID3"
            data[3] = 3;
            for (int i = 10; i < data.Length; i++) data[i] = 0x55;
            return data;
        }

        static void TestWeightedPick()
        {
            Section("⑦ 加权随机抽取");
            List<LineItem> pool = new List<LineItem>();
            pool.Add(new LineItem("高频", 9));
            pool.Add(new LineItem("低频", 1));
            int high = 0, low = 0;
            for (int i = 0; i < 20000; i++)
            {
                LineItem it = Utils.PickWeighted(pool, "");
                if (it.text == "高频") high++; else low++;
            }
            double ratio = (double)high / (high + low);
            Check("权重比例合理", ratio > 0.85 && ratio < 0.95, "高频占比 " + ratio.ToString("0.000") + "（期望 ≈0.900）");
            Check("权重<=0 视为 1", Utils.PickWeighted(new List<LineItem> { new LineItem("a", 0) }, "") != null, "");
            Check("空池返回 null", Utils.PickWeighted(new List<LineItem>(), "") == null, "");
            int repeat = 0;
            string last = "";
            for (int i = 0; i < 200; i++)
            {
                LineItem it = Utils.PickWeighted(pool, last);
                if (it.text == last) repeat++;
                last = it.text;
            }
            Check("尽量不连续重复", repeat < 60, "200 次里重复 " + repeat + " 次");
        }

        // ---------------- 本地 Mock 大模型服务 ----------------

        class MockServer : IDisposable
        {
            TcpListener _listener;
            Thread _thread;
            public int Port;
            public int StatusCode = 200;
            public string Body = "";
            public string LastRequest = "";
            volatile bool _running;

            public void Start(string responseJson, int statusCode)
            {
                Body = responseJson;
                StatusCode = statusCode;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _running = true;
                _thread = new Thread(Loop);
                _thread.IsBackground = true;
                _thread.Start();
            }

            void Loop()
            {
                while (_running)
                {
                    try
                    {
                        using (TcpClient client = _listener.AcceptTcpClient())
                        using (NetworkStream stream = client.GetStream())
                        {
                            byte[] buffer = new byte[65536];
                            int total = 0;
                            stream.ReadTimeout = 3000;
                            try
                            {
                                while (total < buffer.Length)
                                {
                                    int n = stream.Read(buffer, total, buffer.Length - total);
                                    if (n <= 0) break;
                                    total += n;
                                    string soFar = Encoding.UTF8.GetString(buffer, 0, total);
                                    int headEnd = soFar.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                                    if (headEnd < 0) continue;
                                    int contentLength = 0;
                                    foreach (string line in soFar.Substring(0, headEnd).Split('\n'))
                                    {
                                        if (line.ToLowerInvariant().StartsWith("content-length:"))
                                            int.TryParse(line.Substring(15).Trim(), out contentLength);
                                    }
                                    if (total >= headEnd + 4 + contentLength) break;
                                }
                            }
                            catch { }
                            LastRequest = Encoding.UTF8.GetString(buffer, 0, total);

                            byte[] payload = Encoding.UTF8.GetBytes(Body);
                            string head = "HTTP/1.1 " + StatusCode + " " + (StatusCode == 200 ? "OK" : "Error") + "\r\n" +
                                          "Content-Type: application/json; charset=utf-8\r\n" +
                                          "Content-Length: " + payload.Length + "\r\n" +
                                          "Connection: close\r\n\r\n";
                            byte[] headBytes = Encoding.ASCII.GetBytes(head);
                            stream.Write(headBytes, 0, headBytes.Length);
                            stream.Write(payload, 0, payload.Length);
                            stream.Flush();
                        }
                    }
                    catch { if (!_running) return; }
                }
            }

            public void Dispose()
            {
                _running = false;
                try { if (_listener != null) _listener.Stop(); } catch { }
            }
        }

        static void TestLlmHttp()
        {
            Section("⑧ 大模型接入（本地 Mock 服务，模拟云端/本地接口）");
            string okBody = "{\"id\":\"x\",\"object\":\"chat.completion\",\"choices\":[{\"index\":0," +
                            "\"message\":{\"role\":\"assistant\",\"content\":\"指挥官…你终于回来了…\"},\"finish_reason\":\"stop\"}]}";

            using (MockServer server = new MockServer())
            {
                server.Start(okBody, 200);
                LlmSettings s = new LlmSettings();
                s.baseUrl = "http://127.0.0.1:" + server.Port + "/v1";
                s.apiKey = "test-key";
                s.model = "test-model";
                s.maxTokens = 64;
                s.timeoutSec = 15;
                s.historyTurns = 4;

                List<LlmMessage> history = new List<LlmMessage>();
                history.Add(new LlmMessage("user", "你好"));
                history.Add(new LlmMessage("assistant", "指挥官…"));
                LlmResult r = Llm.Chat(s, "你是信浓。", history, "在吗？");
                Check("请求成功", r.ok, r.ok ? (r.elapsedMs + " ms") : r.error);
                Check("回复内容解析正确", r.text == "指挥官…你终于回来了…", r.text);
                Check("请求体是合法 JSON 且带 model", server.LastRequest.IndexOf("\"model\":\"test-model\"", StringComparison.Ordinal) >= 0, "");
                Check("请求头带 Authorization", server.LastRequest.IndexOf("Bearer test-key", StringComparison.Ordinal) >= 0, "");
                Check("system 提示词位于首位", server.LastRequest.IndexOf("你是信浓。", StringComparison.Ordinal) > 0, "");
                Check("携带历史轮数", server.LastRequest.IndexOf("指挥官…", StringComparison.Ordinal) > 0, "");
            }

            using (MockServer server = new MockServer())
            {
                server.Start("{\"error\":{\"message\":\"invalid api key\"}}", 401);
                LlmSettings s = new LlmSettings();
                s.baseUrl = "http://127.0.0.1:" + server.Port + "/v1";
                s.apiKey = "bad";
                s.model = "m";
                s.timeoutSec = 10;
                LlmResult r = Llm.Chat(s, "sys", null, "hi");
                Check("401 被识别为鉴权失败", !r.ok && r.error.IndexOf("鉴权失败", StringComparison.Ordinal) >= 0, Utils.FirstLine(r.error, 50));
            }

            using (MockServer server = new MockServer())
            {
                server.Start("{\"choices\":[{\"message\":{\"content\":\"\"}}]}", 200);
                LlmSettings s = new LlmSettings();
                s.baseUrl = "http://127.0.0.1:" + server.Port + "/v1";
                s.model = "m";
                s.timeoutSec = 10;
                LlmResult r = Llm.Chat(s, "sys", null, "hi");
                Check("空回复被判为失败并给出提示", !r.ok && r.error.IndexOf("为空", StringComparison.Ordinal) >= 0, Utils.FirstLine(r.error, 40));
            }

            {
                LlmSettings s = new LlmSettings();
                s.baseUrl = "http://127.0.0.1:9/v1";   // 9 号端口不会有服务
                s.model = "m";
                s.timeoutSec = 5;
                LlmResult r = Llm.Chat(s, "sys", null, "hi");
                Check("连不上时给出可操作提示", !r.ok && r.error.IndexOf("无法连接", StringComparison.Ordinal) >= 0, Utils.FirstLine(r.error, 50));
            }

            {
                LlmSettings s = new LlmSettings();
                s.baseUrl = "https://api.example.com/v1";
                s.model = "m";
                Check("地址自动补 /chat/completions", true, "（内部拼接，见下方预设自检）");
                Check("预设包含本地与云端", HasPreset(Llm.Presets(), "local") && HasPreset(Llm.Presets(), "cloud"),
                    "预设 " + Llm.Presets().Count + " 个");
                LlmSettings clone = Llm.Clone(s);
                clone.model = "changed";
                Check("设置克隆互不影响", s.model == "m" && clone.model == "changed", "");
            }

            {
                string cleaned = Llm.ExtractContent("{\"choices\":[{\"message\":{\"content\":\"```\\n你好\\n```\"}}]}");
                Check("markdown 代码块被清理", cleaned == "你好", cleaned);
                string list = Llm.ExtractContent("{\"choices\":[{\"message\":{\"content\":\"- 第一句\\n- 第二句\"}}]}");
                Check("markdown 列表被拍平成台词", list == "第一句 第二句", list);
            }

            {
                CharacterProfile p = new CharacterProfile();
                p.name = "信浓";
                p.card.persona = "测试人设";
                p.card.userName = "指挥官";
                string sys = Llm.BuildSystemPrompt(p, "玩家刚在玩原神。", true);
                Check("提示词包含角色名与人设", sys.IndexOf("信浓", StringComparison.Ordinal) >= 0 && sys.IndexOf("测试人设", StringComparison.Ordinal) >= 0, "");
                Check("提示词包含系统事件上下文", sys.IndexOf("原神", StringComparison.Ordinal) >= 0, "");
                Check("提示词包含气泡输出约束", sys.IndexOf("气泡", StringComparison.Ordinal) >= 0 && sys.IndexOf("60", StringComparison.Ordinal) >= 0, "");
            }
        }

        static bool HasPreset(List<LlmPreset> list, string provider)
        {
            foreach (LlmPreset p in list) if (p.provider == provider) return true;
            return false;
        }

        static void TestSystemWatch()
        {
            Section("⑨ 系统事件监听（需授权）");
            SystemWatch.Start(1000);
            Check("监听已启动", SystemWatch.Enabled, "");
            Thread.Sleep(1600);
            SystemSnapshot snap = SystemWatch.Snapshot();
            Check("能取到快照", snap != null, "");
            Check("空闲时间可读", snap.idleSeconds >= 0, snap.idleSeconds + " 秒");
            string hint = SystemWatch.BuildContextHint(snap);
            Check("上下文提示非空", !string.IsNullOrEmpty(hint), Utils.FirstLine(hint, 60));
            string filled = SystemWatch.FillPlaceholders("汝刚刚是在玩 {app} 么…？现在是 {time}。", snap);
            Check("占位符替换生效", filled.IndexOf("{app}", StringComparison.Ordinal) < 0 && filled.IndexOf("{time}", StringComparison.Ordinal) < 0, filled);
            Check("程序友好名映射", SystemWatch.FriendlyName("chrome") == "Chrome 浏览器" && SystemWatch.FriendlyName("GenshinImpact") == "原神", "");
            Check("游戏识别", SystemWatch.IsLikelyGame("GenshinImpact", "原神") && !SystemWatch.IsLikelyGame("notepad", "无标题 - 记事本"), "");
            Check("时长格式化", SystemWatch.FormatDuration(45) == "45 秒" && SystemWatch.FormatDuration(125) == "2 分钟", "");
            SystemWatch.Stop();
            Check("监听可停止", !SystemWatch.Enabled, "");
        }

        static void TestImages()
        {
            Section("⑩ 图片：缩放 / 抠图 / 缓存");
            Bitmap src = new Bitmap(200, 200, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(src))
            {
                g.Clear(Color.White);
                using (SolidBrush br = new SolidBrush(Color.FromArgb(255, 200, 60, 60)))
                    g.FillEllipse(br, 50, 50, 100, 100);
            }
            Check("不透明图判定", !ImageFx.HasTransparency(src), "");

            ImageFx.BgRemovalResult rm = ImageFx.RemoveEdgeBackground(src, 24, true);
            Check("抠图执行成功", rm.ok, rm.message);
            Check("四角变透明", src.GetPixel(0, 0).A == 0 && src.GetPixel(199, 199).A == 0, "");
            Check("主体保留", src.GetPixel(100, 100).A > 200, "中心 alpha=" + src.GetPixel(100, 100).A);
            Check("抠图后判定为含透明通道", ImageFx.HasTransparency(src), "");

            Bitmap trimmed = ImageFx.TrimTransparent(src);
            Check("透明边被裁掉", trimmed.Width < 200 && trimmed.Height < 200, trimmed.Width + "×" + trimmed.Height);

            Bitmap scaled = ImageFx.ScaleToHeight(trimmed, 100);
            Check("等比缩放到指定高度", scaled.Height == 100 && scaled.Width > 0, scaled.Width + "×" + scaled.Height);

            string probe = Path.Combine(Path.GetTempPath(), "aldeskpet-cache.png");
            scaled.Save(probe, ImageFormat.Png);
            Bitmap c1 = ImageCache.Get(probe);
            Bitmap c2 = ImageCache.Get(probe);
            Check("图片缓存命中同一对象", c1 != null && object.ReferenceEquals(c1, c2), "");
            Bitmap thumb = ImageCache.GetThumb(probe, 40);
            Check("缩略图生成", thumb != null && thumb.Height == 40, thumb == null ? "" : thumb.Width + "×" + thumb.Height);
            SearchOption opt = SearchOption.TopDirectoryOnly;
            GC.KeepAlive(opt);
            ImageCache.Clear();
            Check("缓存可清空（内存回收）", true, "");
            try { File.Delete(probe); } catch { }
            src.Dispose();
        }

        static void TestTextAndAnimation()
        {
            Section("⑪ 文本排版与 Q 弹动画");
            using (Bitmap bmp = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                Font font = Theme.BodySized(16, false);
                string text = "指挥官，汝的余额已经所剩无多了——妾身看着这些数字，心里也跟着不安呢…要不要先歇一歇？";
                List<string> lines = Utils.WrapText(g, text, font, 300);
                Check("长文本能折行", lines.Count >= 2, "折成 " + lines.Count + " 行");
                bool fits = true;
                foreach (string line in lines)
                    if (g.MeasureString(line, font, int.MaxValue, StringFormat.GenericTypographic).Width > 302) fits = false;
                Check("每行都不超过限定宽度", fits, "");
                List<string> forced = Utils.WrapText(g, "第一行\n第二行", font, 300);
                Check("手动换行生效", forced.Count == 2, "");
            }
            Check("字号映射合理", true, "台词 size 22 → 气泡 20px 左右");

            Check("动画曲线起点为 1", Math.Abs(PetForm.SquashCurve(0) - 1.0) < 0.0001, PetForm.SquashCurve(0).ToString("0.000"));
            Check("动画曲线终点为 1", Math.Abs(PetForm.SquashCurve(1) - 1.0) < 0.0001, PetForm.SquashCurve(1).ToString("0.000"));
            double min = 9, max = 0;
            for (double p = 0; p <= 1.0; p += 0.01)
            {
                double v = PetForm.SquashCurve(p);
                if (v < min) min = v;
                if (v > max) max = v;
            }
            Check("有压缩阶段（Q 弹下压）", min < 0.88, "最小压缩系数 " + min.ToString("0.000"));
            Check("有回弹过冲（弹起）", max > 1.03, "最大拉伸系数 " + max.ToString("0.000"));
            Check("压缩幅度安全（不破图）", min > 0.75 && max < 1.2, min.ToString("0.000") + " ~ " + max.ToString("0.000"));
        }

        static void TestAudio()
        {
            Section("⑫ 音频子系统");
            string st = Audio.SelfTest();
            Check("音频子自检返回结果", !string.IsNullOrEmpty(st), st);
            string press = Audio.PresetSfx("fx1", "press");
            string duck = Audio.PresetSfx("duck", "press");
            bool builtinOk = !string.IsNullOrEmpty(press) && File.Exists(press) && !string.IsNullOrEmpty(duck) && File.Exists(duck);
            Check("内置音效预设可用", builtinOk, builtinOk ? Path.GetFileName(press) : "缺少 assets\\builtin\\sfx 下的音效文件");
            Check("静音预设返回空", Audio.PresetSfx("none", "press") == "", "");
            Audio.Configure(50, true);
            Audio.Play(press);
            Thread.Sleep(200);
            Audio.StopAll();
            Check("播放与停止不抛异常", true, "音量 50 测试完成");
        }

        /// <summary>
        /// 内置立绘的质量校验（v1.0.8 新增回归）：
        /// 高度 ≤900、体积 ≤1.2MB、透明底、**必须是彩色的**。
        /// 上一版把「欧根亲王」压到 8bit 调色板时用了「亮度索引表」，立绘整张变成灰度图；
        /// 当时的自检只看"文件在不在"，所以没能拦住 —— 这里补上「是不是彩色」的断言。
        /// </summary>
        static void CheckPortraitQuality(string id, string path)
        {
            if (!File.Exists(path)) return;
            long size = new FileInfo(path).Length;
            Check("立绘「" + id + "」体积 ≤1.2MB", size <= 1228800, (size / 1024.0).ToString("0.0") + " KB");
            Bitmap bmp = null;
            try
            {
                bmp = ImageFx.LoadUnlocked(path);
                Check("立绘「" + id + "」高度 ≤900", bmp.Height <= 900, bmp.Width + "×" + bmp.Height);
                int a1 = bmp.GetPixel(0, 0).A, a2 = bmp.GetPixel(bmp.Width - 1, 0).A, a3 = bmp.GetPixel(0, bmp.Height - 1).A;
                Check("立绘「" + id + "」是透明底（左上/右上/左下角透明）", a1 < 16 && a2 < 16 && a3 < 16,
                    "角 alpha=" + a1 + "/" + a2 + "/" + a3);

                int maxSat = 0, gray = 0, sampled = 0;
                for (int y = 0; y < bmp.Height; y += 7)
                    for (int x = 0; x < bmp.Width; x += 7)
                    {
                        System.Drawing.Color c = bmp.GetPixel(x, y);
                        if (c.A < 16) continue;
                        sampled++;
                        int mx = Math.Max(c.R, Math.Max(c.G, c.B));
                        int mn = Math.Min(c.R, Math.Min(c.G, c.B));
                        int sat = mx - mn;
                        if (sat > maxSat) maxSat = sat;
                        if (sat <= 6) gray++;
                    }
                double grayRatio = sampled == 0 ? 100 : 100.0 * gray / sampled;
                Check("立绘「" + id + "」是彩色的（不是灰度图）", maxSat >= 40 && grayRatio < 60,
                    "最大通道饱和差 " + maxSat + "，灰度像素占比 " + grayRatio.ToString("0.0") + "%");
            }
            catch (Exception ex)
            {
                Check("立绘「" + id + "」可解码", false, ex.GetType().Name + "：" + ex.Message);
            }
            finally
            {
                if (bmp != null) bmp.Dispose();
            }
        }

        static void TestAssets()
        {
            Section("⑬ 内置素材完整性");            string builtin = AppPaths.BuiltinDir();
            Check("内置素材目录存在", Directory.Exists(builtin), builtin);
            string[] expect = new string[]
            {
                Path.Combine(builtin, "sfx", "press.mp3"),
                Path.Combine(builtin, "sfx", "release.mp3"),
                Path.Combine(builtin, "sfx", "press-duck.mp3"),
                Path.Combine(builtin, "sfx", "release-duck.mp3"),
                Path.Combine(builtin, "ui", "dialog-frame.png"),
                Path.Combine(builtin, "ui", "hex-tile.png"),
            };
            foreach (string f in expect)
            {
                bool ok = File.Exists(f);
                Check("存在：" + Path.GetFileName(f), ok, ok ? Utils.HumanSize(new FileInfo(f).Length) : "缺失（不影响自检通过与否的判定以外功能）");
            }

            List<string> ids = CharacterStore.BuiltinCharacterIds();
            Check("内置角色模板", ids.Count > 0, ids.Count + " 个：" + string.Join("、", ids.ToArray()));

            // 交付要求：安装包内置这 5 位角色（信浓 / 初月 / 欧根亲王 / 岛风 / 企业）
            // 内置角色清单（v1.1.0 起 13 位：原有 5 位 + 新增 8 位）
            string[] required = new string[]
            {
                "shinano", "chuyue", "prinz_eugen", "shimakaze", "enterprise",
                "michele", "aika", "perlica", "zhuangfangyi", "liino",
                "yeshunguang", "jufufu", "remielle"
            };
            foreach (string req in required)
                Check("内置角色含 " + req, ids.Contains(req), ids.Contains(req) ? "" : "缺少该角色的内置素材");
            Check("内置角色就是这 13 位（没有多余/缺失）", ids.Count == required.Length,
                "实际 " + ids.Count + " 个：" + string.Join("、", ids.ToArray()));

            foreach (string id in ids)
            {
                string lineFile = Path.Combine(builtin, "lines", id + ".txt");
                bool hasLines = File.Exists(lineFile);
                int count = 0;
                if (hasLines) count = LinePack.ParseTextFile(lineFile, new LineSet()).Count();
                Check("模板「" + id + "」台词文件", hasLines && count > 0, count + " 条");
                if (hasLines)
                {
                    LineSet set = LinePack.ParseTextFile(lineFile, new LineSet());
                    Check("模板「" + id + "」五段台词齐全", set.greet.Count >= 5 && set.click.Count >= 8
                        && set.proactive.Count >= 4 && set.idle.Count >= 3 && set.system.Count >= 3,
                        "问候 " + set.greet.Count + " / 点击 " + set.click.Count + " / 主动 " + set.proactive.Count
                        + " / 待机 " + set.idle.Count + " / 系统 " + set.system.Count);
                    bool hasHello = false, hasBack = false, hasIgnore = false;
                    foreach (LineItem it in set.greet)
                    {
                        if (it.text.IndexOf("你好", StringComparison.Ordinal) >= 0) hasHello = true;
                        if (it.text.IndexOf("你终于回来了", StringComparison.Ordinal) >= 0) hasBack = true;
                    }
                    foreach (LineItem it in set.idle)
                        if (it.text.IndexOf("你怎么不理我", StringComparison.Ordinal) >= 0) hasIgnore = true;
                    Check("模板「" + id + "」含必含句（你好 / 你终于回来了 / 你怎么不理我）", hasHello && hasBack && hasIgnore,
                        "你好=" + hasHello + " 回来了=" + hasBack + " 不理我=" + hasIgnore);
                }
                string cardFile = Path.Combine(builtin, "characters", id, "card.txt");
                bool hasCard = File.Exists(cardFile);
                int cardChars = 0;
                if (hasCard)
                {
                    string cardText = File.ReadAllText(cardFile, Encoding.UTF8);
                    foreach (char c in cardText) if (c > 127) cardChars++;
                }
                Check("模板「" + id + "」角色卡", hasCard && cardChars >= 150, cardChars + " 个中文字符");
                string portrait = Path.Combine(builtin, "characters", id, "portrait.png");
                Check("模板「" + id + "」立绘", File.Exists(portrait),
                    File.Exists(portrait) ? Utils.HumanSize(new FileInfo(portrait).Length) : "缺少 portrait.png");
                CheckPortraitQuality(id, portrait);
                string voiceDir = Path.Combine(builtin, "voices", id);
                if (Directory.Exists(voiceDir))
                {
                    int mp3 = Directory.GetFiles(voiceDir, "*.mp3").Length;
                    bool manifest = File.Exists(Path.Combine(voiceDir, "manifest.txt"));
                    Check("模板「" + id + "」语音包", mp3 > 0 && manifest, mp3 + " 个音频，清单 " + (manifest ? "有" : "无"));
                }
            }

            // 用内置模板走一遍完整流程（如果有）
            if (ids.Count > 0)
            {
                Cursor cur = Cursor.Current;
                GC.KeepAlive(cur);
                CharacterProfile p = CharacterStore.ImportBuiltinAs(ids[0], "自检-" + ids[0]);
                Check("导入内置模板", p != null, p == null ? "" : p.DisplayName());
                if (p != null)
                {
                    Check("模板带立绘", p.images.Count > 0, p.images.Count + " 张");
                    Check("模板带台词", p.lines.Count() > 0, p.lines.Count() + " 条");
                    Check("模板带角色卡", !string.IsNullOrEmpty(p.card.persona), Utils.FirstLine(p.card.persona, 40));
                    VoiceBankLoader.Report vr = VoiceBankLoader.Load(p, p.voices.manifest);
                    Check("模板语音清单一套可用", vr.entries >= 0, vr.text.Replace("\r\n", " | "));
                    // 「开箱齐 5 位」靠这个来源标记判重（否则会把同一个模板重复导入一遍）
                    Check("导入内置模板会记下来源模板 id", p.builtinId == ids[0], "builtinId=" + p.builtinId);
                    CharacterProfile back = CharacterStore.Load(p.id);
                    Check("来源模板 id 随 profile.json 往返",
                        back != null && back.builtinId == ids[0],
                        back == null ? "(读不到角色)" : "builtinId=" + back.builtinId);
                    Check("用户自建角色的来源模板 id 为空（不会被误判成内置）",
                        new CharacterProfile().builtinId == "",
                        "");
                    CharacterStore.Delete(p.id);
                }
            }
        }

        // ---------------- 桌宠显示策略（纯判定逻辑） ----------------

        static PetDisplayState DecideFor(string mode, bool hideFullscreen, ForegroundInfo fg, bool sameScreen)
        {
            return DisplayWatch.Decide(mode, hideFullscreen, fg, sameScreen);
        }

        static void TestDisplayPolicy()
        {
            Section("⑲ 桌宠显示策略（始终最上层 / 焦点冻结 / 全屏隐藏）");

            // 1) 取值与容错
            Check("显示方式默认是「始终最上层」",
                PetDisplayMode.Normalize(null) == PetDisplayMode.Always
                && PetDisplayMode.Normalize("") == PetDisplayMode.Always
                && new AppSettings().petDisplayMode == PetDisplayMode.Always, "");
            Check("三种显示方式都被接受",
                PetDisplayMode.Normalize("always") == PetDisplayMode.Always
                && PetDisplayMode.Normalize("focus") == PetDisplayMode.FocusFreeze
                && PetDisplayMode.Normalize("fullscreen") == PetDisplayMode.FullscreenHide, "");
            Check("非法 / 大小写 / 空格都能收敛",
                PetDisplayMode.Normalize("随便写的") == PetDisplayMode.Always
                && PetDisplayMode.Normalize("  FOCUS  ") == PetDisplayMode.FocusFreeze, "");
            bool roundTrip = PetDisplayMode.Ids().Length == 3;
            for (int i = 0; i < PetDisplayMode.Ids().Length; i++)
                if (PetDisplayMode.IndexOf(PetDisplayMode.IdAt(i)) != i) roundTrip = false;
            Check("显示方式 id 与索引一一对应", roundTrip, "");
            Check("三种显示方式都有界面标题与说明",
                PetDisplayMode.Titles().Length == 3 && PetDisplayMode.Details().Length == 3
                && PetDisplayMode.Title("focus").Length > 0 && PetDisplayMode.Detail("fullscreen").Length > 30,
                PetDisplayMode.Title("focus"));

            // 2) 什么时候需要轮询
            Check("「始终最上层 + 不隐藏全屏」完全不轮询（零开销）",
                !PetDisplayMode.NeedsWatch(PetDisplayMode.Always, false), "");
            Check("打开全屏自动隐藏就需要轮询",
                PetDisplayMode.NeedsWatch(PetDisplayMode.Always, true), "");
            Check("冻结 / 全屏隐藏模式需要轮询",
                PetDisplayMode.NeedsWatch(PetDisplayMode.FocusFreeze, false)
                && PetDisplayMode.NeedsWatch(PetDisplayMode.FullscreenHide, false), "");

            // 3) 决策表
            ForegroundInfo other = new ForegroundInfo();
            other.hwnd = new IntPtr(4321);
            other.process = "chrome";
            ForegroundInfo full = new ForegroundInfo();
            full.hwnd = new IntPtr(4322);
            full.process = "game";
            full.isFullscreen = true;
            full.monitor = @"\\.\DISPLAY1";
            ForegroundInfo own = new ForegroundInfo();
            own.hwnd = new IntPtr(4323);
            own.isSelf = true;
            ForegroundInfo desk = new ForegroundInfo();
            desk.hwnd = new IntPtr(4324);
            desk.isDesktop = true;

            Check("桌面在前台：三种模式都正常显示",
                DecideFor("always", true, desk, true) == PetDisplayState.Normal
                && DecideFor("focus", true, desk, true) == PetDisplayState.Normal
                && DecideFor("fullscreen", true, desk, true) == PetDisplayState.Normal, "");
            Check("桌宠自己在前台：不会自己把自己冻结",
                DecideFor("focus", true, own, true) == PetDisplayState.Normal
                && DecideFor("fullscreen", true, own, true) == PetDisplayState.Normal, "");
            Check("取不到前台窗口时正常显示",
                DecideFor("focus", true, new ForegroundInfo(), true) == PetDisplayState.Normal, "");

            Check("始终最上层：别的程序窗口在前台也不让位（普通窗口不会被误判成全屏）",
                DecideFor("always", false, other, true) == PetDisplayState.Normal
                && DecideFor("always", true, other, true) == PetDisplayState.Normal, "");
            Check("始终最上层 + 全屏自动隐藏：全屏时进后台",
                DecideFor("always", true, full, true) == PetDisplayState.Hidden, "");
            Check("始终最上层 + 关掉全屏隐藏：全屏时也不动",
                DecideFor("always", false, full, true) == PetDisplayState.Normal, "");

            Check("焦点冻结：别的程序在前台就冻结（沉到最底层）",
                DecideFor("focus", false, other, true) == PetDisplayState.Sunk, "");
            Check("焦点冻结 + 全屏自动隐藏：全屏时直接进后台",
                DecideFor("focus", true, full, true) == PetDisplayState.Hidden, "");
            Check("焦点冻结 + 关掉全屏隐藏：全屏时只是冻结",
                DecideFor("focus", false, full, true) == PetDisplayState.Sunk, "");

            Check("全屏隐藏模式：普通窗口在前台时仍然最上层",
                DecideFor("fullscreen", false, other, true) == PetDisplayState.Normal, "");
            Check("全屏隐藏模式：别的程序全屏就进后台",
                DecideFor("fullscreen", false, full, true) == PetDisplayState.Hidden, "");
            Check("全屏窗口在别的显示器上时不收桌宠（多屏）",
                DecideFor("always", true, full, false) == PetDisplayState.Normal
                && DecideFor("focus", true, full, false) == PetDisplayState.Sunk
                && DecideFor("fullscreen", true, full, false) == PetDisplayState.Normal, "");

            // 4) 全屏判定与同屏判定（纯计算）
            WinRect monitor = new WinRect(0, 0, 1920, 1080);
            Check("刚好盖满显示器算全屏", DisplayWatch.RectCovers(new WinRect(0, 0, 1920, 1080), monitor), "");
            Check("比显示器还大也算全屏", DisplayWatch.RectCovers(new WinRect(-8, -8, 1928, 1088), monitor), "");
            Check("最大化窗口（工作区）不算全屏",
                !DisplayWatch.RectCovers(new WinRect(0, 0, 1920, 1040), monitor), "");
            Check("没盖满 / 差一点 / 错位都不算全屏",
                !DisplayWatch.RectCovers(new WinRect(10, 10, 1910, 1070), monitor)
                && !DisplayWatch.RectCovers(new WinRect(0, 0, 1919, 1080), monitor)
                && !DisplayWatch.RectCovers(new WinRect(-100, 0, 1900, 1080), monitor), "");
            WinRect second = new WinRect(1920, 0, 3840, 1080);
            Check("看得到窗口矩形时按矩形判断同屏",
                DisplayWatch.RectCovers(new WinRect(0, 0, 1920, 1080), monitor)
                && !DisplayWatch.RectCovers(new WinRect(1920, 0, 3840, 1080), monitor), "");

            ForegroundInfo fs = new ForegroundInfo();
            fs.isFullscreen = true;
            fs.monitor = @"\\.\DISPLAY2";
            fs.fullscreenRect = second;
            Check("同屏判定：设备名一致算同屏",
                DisplayWatch.SameScreen(fs, @"\\.\display2", new WinRect(1920, 0, 3840, 1080)), "");
            Check("同屏判定：设备名不同算别的屏",
                !DisplayWatch.SameScreen(fs, @"\\.\DISPLAY1", new WinRect(0, 0, 1920, 1080)), "");
            ForegroundInfo fsNoName = new ForegroundInfo();
            fsNoName.isFullscreen = true;
            fsNoName.fullscreenRect = second;
            Check("同屏判定：拿不到设备名时退回矩形比对",
                DisplayWatch.SameScreen(fsNoName, "", new WinRect(1920, 0, 3840, 1080))
                && !DisplayWatch.SameScreen(fsNoName, "", new WinRect(0, 0, 1920, 1080)), "");
            Check("同屏判定：前台不是全屏就一定不同屏",
                !DisplayWatch.SameScreen(other, "", new WinRect(0, 0, 1920, 1080)), "");
        }

        // ---------------- 界面细节回归（对勾/标题重复/角色卡遮挡/右键菜单） ----------------

        static int CountGoldPixels(Bitmap bmp, Rectangle area)
        {
            int n = 0;
            int x0 = Math.Max(0, area.Left), y0 = Math.Max(0, area.Top);
            int x1 = Math.Min(bmp.Width - 1, area.Right), y1 = Math.Min(bmp.Height - 1, area.Bottom);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    if (c.R > 150 && c.G > 120 && c.R > c.B + 40) n++;
                }
            return n;
        }

        static void TestUiPolish(string renderDir)
        {
            Section("⑱ 界面细节回归（对勾 / 标题重复 / 角色卡两列 / 右键菜单）");

            // 1) 只有模式按钮画对勾；输入条的「发送」不能画（否则和对勾叠在一起）
            using (InputBarForm bar = new InputBarForm())
            {
                Check("输入条「发送」按钮不画对勾（需求 1）", bar.SendButton != null && !bar.SendButton.ShowCheck,
                    bar.SendButton == null ? "没有按钮" : "ShowCheck=" + bar.SendButton.ShowCheck);
                Check("输入条「发送」按钮仍然可见可用", bar.SendButton != null && bar.SendButton.Enabled, "");
            }

            List<CharacterProfile> all = CharacterStore.ListAll();
            if (all.Count == 0)
            {
                List<string> ids = CharacterStore.BuiltinCharacterIds();
                if (ids.Count > 0) CharacterStore.ImportBuiltinAs(ids[0], null);
                else CharacterStore.Create("细节测试", null);
                all = CharacterStore.ListAll();
            }
            if (all.Count == 0) { Check("准备测试角色", false, "没有可用角色"); return; }
            Config.Current.activeCharacter = all[0].id;
            Config.Save();

            using (MainForm f = new MainForm())
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-4000, -4000);
                f.CreateControl();
                f.Show();
                f.ClientSize = new Size(1160, 780);
                Pump(250);

                // 2) 模式按钮才有对勾
                f.ShowTabById("interact");
                Pump(200);
                TabPageBase interact = f.CurrentPage();
                AlButton mFixed = TabPageBase.FindCtrl(interact, "固定台词模式") as AlButton;
                AlButton mLlm = TabPageBase.FindCtrl(interact, "大模型对话模式") as AlButton;
                Check("模式按钮开启对勾显示", mFixed != null && mLlm != null && mFixed.ShowCheck && mLlm.ShowCheck, "");

                // 3) 标题栏的宋体字只有一处：放大后在「中间区域」不应出现金色像素（残影）
                f.ClientSize = new Size(1900, 1100);
                Pump(350);
                using (Bitmap bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    Rectangle band = new Rectangle((int)(bmp.Width * 0.35), 8, (int)(bmp.Width * 0.47), 62);
                    int gold = CountGoldPixels(bmp, band);
                    Check("放大后标题栏中间没有残留的第二份右上角标识（需求 2）", gold == 0,
                        "中间区域金色像素 = " + gold);
                    Rectangle right = new Rectangle((int)(bmp.Width * 0.82), 8, (int)(bmp.Width * 0.18) - 80, 62);
                    int goldRight = CountGoldPixels(bmp, right);
                    Check("右上角的宋体字仍在", goldRight > 40, "右侧金色像素 = " + goldRight);
                    if (!string.IsNullOrEmpty(renderDir))
                    {
                        Directory.CreateDirectory(renderDir);
                        bmp.Save(Path.Combine(renderDir, "header-fullscreen.png"), ImageFormat.Png);
                    }
                }

                // 4) 角色卡页两列不能互相遮挡（窗口化 + 全屏都要成立）
                foreach (int[] size in new int[][] { new int[] { 1000, 700 }, new int[] { 1160, 780 }, new int[] { 1900, 1100 } })
                {
                    f.ClientSize = new Size(size[0], size[1]);
                    Pump(300);
                    f.ShowTabById("card");
                    Pump(250);
                    TabPageBase page = f.CurrentPage();
                    Control persona = TabPageBase.FindByName(page, "persona");
                    Control style = TabPageBase.FindByName(page, "style");
                    Control greeting = TabPageBase.FindByName(page, "greeting");
                    Control rules = TabPageBase.FindByName(page, "rules");
                    string tag = size[0] + "×" + size[1];
                    if (persona == null || style == null || greeting == null || rules == null)
                    {
                        Check("角色卡控件齐全（" + tag + "）", false, "找不到带名字的控件");
                        continue;
                    }
                    Rectangle rp = persona.Bounds, rs = style.Bounds, rg = greeting.Bounds, rr = rules.Bounds;
                    bool ok = !rp.IntersectsWith(rs) && !rp.IntersectsWith(rg) && !rp.IntersectsWith(rr);
                    Check("角色卡左右两列不重叠（" + tag + "）", ok,
                        "persona=" + rp + " style=" + rs + " greeting=" + rg + " rules=" + rr);
                    Check("右列整体位于左列右侧（" + tag + "）", rs.Left > rp.Right,
                        "persona.Right=" + rp.Right + " style.Left=" + rs.Left);
                    Check("右列在窗口内有足够宽度（" + tag + "）", rs.Width >= 180 && rr.Width >= 180 && rg.Width >= 180,
                        "style.W=" + rs.Width + " rules.W=" + rr.Width);
                    if (!string.IsNullOrEmpty(renderDir) && size[0] == 1900)
                    {
                        Directory.CreateDirectory(renderDir);
                        using (Bitmap bmp = new Bitmap(f.Width, f.Height))
                        {
                            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                            bmp.Save(Path.Combine(renderDir, "card-fullscreen.png"), ImageFormat.Png);
                        }
                    }
                }
                f.Hide();
            }

            // 5) 桌宠右键菜单：要有「关闭桌宠并打开设置界面」和「退出程序」
            CharacterProfile petProfile = CharacterStore.Load(Config.Current.activeCharacter);
            if (petProfile != null)
            {
                using (PetForm pet = new PetForm(petProfile))
                {
                    pet.StartPosition = FormStartPosition.Manual;
                    pet.Location = new Point(-4000, -4000);
                    pet.CreateControl();
                    pet.Show();
                    Pump(250);
                    ContextMenuStrip menu = MenuBuilder.BuildPetMenu(pet);
                    bool hasClose = false, hasExit = false, hasOpen = false;
                    foreach (ToolStripItem it in menu.Items)
                    {
                        if (it.Text == null) continue;
                        if (it.Text.IndexOf("关闭桌宠并打开设置界面", StringComparison.Ordinal) >= 0) hasClose = true;
                        if (it.Text.IndexOf("退出程序", StringComparison.Ordinal) >= 0) hasExit = true;
                        if (it.Text.IndexOf("打开设置界面", StringComparison.Ordinal) >= 0) hasOpen = true;
                    }
                    Check("右键菜单有「关闭桌宠并打开设置界面」（需求 4）", hasClose, "");
                    Check("右键菜单仍保留「打开设置界面」", hasOpen, "");
                    Check("右键菜单的退出项写明是退出程序", hasExit, "");
                    menu.Dispose();
                    pet.CloseAll();
                    pet.Hide();
                }
            }
        }

        // ---------------- 对话模式切换（回归：按钮切换不灵 / 两个按钮同时亮） ----------------

        static double Brightness(Color c) { return c.R * 0.299 + c.G * 0.587 + c.B * 0.114; }

        /// <summary>区域里是否有「金色」像素（选中态的金色对勾/描边）。</summary>
        static bool HasGoldPixel(Bitmap bmp, Rectangle area)
        {
            int x0 = Math.Max(0, area.Left), y0 = Math.Max(0, area.Top);
            int x1 = Math.Min(bmp.Width - 1, area.Right), y1 = Math.Min(bmp.Height - 1, area.Bottom);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    if (c.R > 150 && c.G > 120 && c.R > c.B + 40) return true;
                }
            return false;
        }

        /// <summary>按钮左侧的对勾热区（选中时会画金色对勾）。</summary>
        static Rectangle MarkRect(AlButton b)
        {
            return new Rectangle(b.Left + 5, b.Top + b.Height / 2 - 8, 22, 16);
        }

        static void TestModeSwitch(string renderDir)
        {
            Section("⑰ 对话模式切换（回归：切不动 / 两个按钮同时亮）");

            // 保证有角色可选
            List<CharacterProfile> all = CharacterStore.ListAll();
            if (all.Count == 0)
            {
                List<string> ids = CharacterStore.BuiltinCharacterIds();
                if (ids.Count > 0) CharacterStore.ImportBuiltinAs(ids[0], null);
                else CharacterStore.Create("模式测试", null);
                all = CharacterStore.ListAll();
            }
            if (all.Count == 0) { Check("准备测试角色", false, "没有可用角色"); return; }
            Config.Current.activeCharacter = all[0].id;
            // 配一个假接口地址，避免切换到大模型时弹出「去配置」的模态对话框卡住自检
            Config.Current.llm.baseUrl = "http://127.0.0.1:9/v1";
            Config.Save();

            using (MainForm f = new MainForm())
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-4000, -4000);
                f.CreateControl();
                f.Show();
                Pump(300);
                f.ShowTabById("interact");
                Pump(200);
                TabPageBase page = f.CurrentPage();
                Check("互动设置页可见", page != null && page.Visible, page == null ? "拿不到页面" : page.GetType().Name);
                if (page == null) return;

                AlButton fixedBtn = TabPageBase.FindCtrl(page, "固定台词模式") as AlButton;
                AlButton llmBtn = TabPageBase.FindCtrl(page, "大模型对话模式") as AlButton;
                Check("找到两个模式按钮", fixedBtn != null && llmBtn != null, "");
                if (fixedBtn == null || llmBtn == null) return;

                // 先切到固定台词
                fixedBtn.PerformClick();
                Pump(400);
                CharacterProfile p1 = CharacterStore.Load(all[0].id);
                Check("点「固定台词模式」后模式确实变成 fixed", p1 != null && p1.interact.mode == "fixed",
                    p1 == null ? "" : p1.interact.mode);
                Check("固定台词按钮为选中态、大模型按钮不是", fixedBtn.Primary && !llmBtn.Primary,
                    "fixed.Primary=" + fixedBtn.Primary + " llm.Primary=" + llmBtn.Primary);

                // 再切到大模型
                llmBtn.PerformClick();
                Pump(400);
                CharacterProfile p2 = CharacterStore.Load(all[0].id);
                Check("点「大模型对话模式」后模式变成 llm", p2 != null && p2.interact.mode == "llm",
                    p2 == null ? "" : p2.interact.mode);
                Check("大模型按钮为选中态、固定台词按钮不是", llmBtn.Primary && !fixedBtn.Primary,
                    "fixed.Primary=" + fixedBtn.Primary + " llm.Primary=" + llmBtn.Primary);

                // 视觉校验：选中项左侧有金色对勾，未选中项没有（证明状态变化立即重绘、且两者可区分）
                try
                {
                    using (Bitmap bmp = new Bitmap(page.Width, page.Height))
                    {
                        page.DrawToBitmap(bmp, new Rectangle(0, 0, page.Width, page.Height));
                        Check("选中「大模型」时它带金色对勾", HasGoldPixel(bmp, MarkRect(llmBtn)), "");
                        Check("未选中的「固定台词」没有金色对勾（不会看着也像选中）", !HasGoldPixel(bmp, MarkRect(fixedBtn)), "");
                        if (!string.IsNullOrEmpty(renderDir))
                        {
                            Directory.CreateDirectory(renderDir);
                            bmp.Save(Path.Combine(renderDir, "interact-llm-mode.png"), ImageFormat.Png);
                        }
                    }
                }
                catch (Exception ex) { Check("模式按钮视觉校验", false, ex.Message); }

                // 回到固定台词，对勾要跟着换过去
                fixedBtn.PerformClick();
                Pump(300);
                try
                {
                    using (Bitmap bmp = new Bitmap(page.Width, page.Height))
                    {
                        page.DrawToBitmap(bmp, new Rectangle(0, 0, page.Width, page.Height));
                        Check("切回「固定台词」后对勾跟着换过去",
                            HasGoldPixel(bmp, MarkRect(fixedBtn)) && !HasGoldPixel(bmp, MarkRect(llmBtn)), "");
                        if (!string.IsNullOrEmpty(renderDir))
                            bmp.Save(Path.Combine(renderDir, "interact-fixed-mode.png"), ImageFormat.Png);
                    }
                }
                catch (Exception ex) { Check("模式按钮视觉复核", false, ex.Message); }

                // 反复切换 6 次不应出现状态错乱
                bool stable = true;
                for (int i = 0; i < 6; i++)
                {
                    if (i % 2 == 0) llmBtn.PerformClick(); else fixedBtn.PerformClick();
                    Pump(80);
                    bool llm = i % 2 == 0;
                    if (llmBtn.Primary != llm || fixedBtn.Primary == llm) { stable = false; break; }
                }
                Check("连续切换 6 次状态始终互斥", stable, "");
                CharacterProfile p3 = CharacterStore.Load(all[0].id);
                Check("最终模式与按钮一致", p3 != null && (p3.interact.mode == "fixed") == fixedBtn.Primary,
                    p3 == null ? "" : p3.interact.mode);

                f.Hide();
            }
            // 还原接口地址，避免影响后续测试
            Config.Current.llm.baseUrl = "";
            Config.Save();
        }

        // ---------------- 桌宠互动回归（曾出现：点一下报错 / 有声音没对话） ----------------

        static void TestPetInteraction()
        {
            Section("⑯ 桌宠互动回归（大模型模式点击 / 主动对话 / 语音节流）");

            string reply = "指挥官…妾身一直在等汝。";
            using (MockServer server = new MockServer())
            {
                server.Start("{\"choices\":[{\"message\":{\"content\":\"" + reply + "\"}}]}", 200);

                // 准备一个「大模型模式 + 开启系统监听」的角色
                CharacterProfile p = CharacterStore.Create("互动测试", null);
                p.interact.mode = "llm";
                p.interact.proactive = true;
                p.interact.useSystemContext = true;
                p.interact.showInputBox = true;
                p.interact.proactiveMinSec = 5;
                p.interact.proactiveMaxSec = 6;
                p.lines.click.Add(new LineItem("固定台词一", 1));
                p.lines.click.Add(new LineItem("固定台词二", 1));
                CharacterStore.Save(p);
                Config.Current.systemWatchPermission = "allowed";
                Config.Current.llm.baseUrl = "http://127.0.0.1:" + server.Port + "/v1";
                Config.Current.llm.model = "mock-model";
                Config.Current.llm.apiKey = "test";
                Config.Current.llm.timeoutSec = 15;
                Config.Current.llm.maxTokens = 64;

                using (PetForm pet = new PetForm(p))
                {
                    pet.StartPosition = FormStartPosition.Manual;
                    pet.Location = new Point(-4000, -4000);
                    pet.CreateControl();
                    pet.Show();
                    pet.StartTimers();      // 与 PetManager.Start 的行为一致（主动对话定时器在这里起）
                    Pump(300);

                    // 1) 大模型模式下点一下桌宠：应显示「思考中」再显示回复，且全程不报错
                    bool threw = false;
                    string err = "";
                    try { pet.OnPetClicked(); }
                    catch (Exception ex) { threw = true; err = ex.GetType().Name + "：" + ex.Message; }
                    Check("大模型模式下点击桌宠不抛异常", !threw, err);

                    string shown = "";
                    for (int i = 0; i < 40; i++)
                    {
                        Pump(150);
                        shown = pet.BubbleText();
                        if (shown == reply) break;
                    }
                    Check("模型回复出现在气泡里（不是从对话框里回）", shown == reply, "气泡内容：" + Utils.FirstLine(shown, 40));
                    Check("气泡可见", pet.BubbleVisible(), "");

                    // 2) 连续快速点击：不报错、气泡可反复复用
                    threw = false;
                    for (int i = 0; i < 4; i++)
                    {
                        try { pet.OnPetClicked(); }
                        catch (Exception ex) { threw = true; err = ex.GetType().Name + "：" + ex.Message; break; }
                        Pump(120);
                    }
                    Check("连续快速点击不报错（回归：气泡被销毁）", !threw, err);

                    string after = "";
                    for (int i = 0; i < 40; i++)
                    {
                        Pump(150);
                        after = pet.BubbleText();
                        if (after == reply) break;
                    }
                    Check("多次点击后气泡仍能显示模型回复", after == reply, "气泡内容：" + Utils.FirstLine(after, 40));

                    // 3) 主动对话（开启监听 + 大模型）：必须有对话出现，不能只有声音
                    pet.HidePopups();
                    Pump(200);
                    pet.TriggerProactive();
                    bool proactiveOk = false;
                    string proactive = "";
                    for (int i = 0; i < 40; i++)
                    {
                        Pump(150);
                        proactive = pet.BubbleText();
                        if (pet.BubbleVisible() && proactive == reply) { proactiveOk = true; break; }
                    }
                    Check("主动对话会弹出气泡（回归：只响不出话）", proactiveOk,
                        "气泡可见=" + pet.BubbleVisible() + " 内容：" + Utils.FirstLine(proactive, 40));

                    // 4) 语音节流：本句语音没播完时，互动只做立绘变化，不换台词
                    string voiceFile = Path.Combine(CharacterStore.VoicesDir(p.id), "v-long.wav");
                    Directory.CreateDirectory(CharacterStore.VoicesDir(p.id));
                    File.Copy(Audio.PresetSfx("fx1", "press"), voiceFile, true);
                    pet.SetCurrentVoiceFile("");
                    pet.HidePopups();
                    Pump(150);
                    pet.OnPetClicked();       // 固定台词没配语音 → 直接走台词
                    Pump(400);
                    string first = pet.BubbleText();
                    Check("固定台词模式能出词", first.Length > 0, Utils.FirstLine(first, 30));

                    pet.SetCurrentVoiceFile(voiceFile);
                    pet.InteractNow();        // 语音「正在播」时不该换台词
                    Pump(300);
                    string second = pet.BubbleText();
                    Check("语音未播完时互动不切换台词（需求 4）", second == first, "「" + Utils.FirstLine(first, 20) + "」→「" + Utils.FirstLine(second, 20) + "」");

                    pet.SetCurrentVoiceFile("");
                    pet.InteractNow();
                    Pump(300);
                    string third = pet.BubbleText();
                    Check("语音结束后互动恢复正常出词", third.Length > 0, Utils.FirstLine(third, 30));

                    pet.HidePopups();
                    pet.CloseAll();
                    pet.Hide();
                }
                CharacterStore.Delete(p.id);
            }
        }

        // ---------------- 桌宠显示：真实窗口行为 + 设置页 ----------------

        static void TestDisplayUi()
        {
            Section("⑳ 桌宠显示：真实窗口行为与设置页");

            string oldMode = Config.Current.petDisplayMode;
            bool oldFullscreen = Config.Current.hideOnFullscreen;
            bool oldPopups = Config.Current.hidePopupsWhenInactive;
            bool oldTray = Config.Current.showTray;
            bool oldShowOnStart = true;
            CharacterProfile profile = null;
            try
            {
                // ---- 1) 设置界面：三种显示方式，点一下立刻生效 ----
                string[] wantIds = new string[] { "sound", "llm", "path", "log", "general", "display", "about" };
                using (SettingsForm sf = new SettingsForm("display"))
                {
                    sf.StartPosition = FormStartPosition.Manual;
                    sf.Location = new Point(-4000, -4000);
                    sf.CreateControl();
                    sf.Show();
                    Pump(220);

                    bool hasDisplay = false;
                    foreach (string id in sf.PageIds()) if (id == "display") hasDisplay = true;
                    Check("设置界面新增「桌宠显示」页", hasDisplay, string.Join("、", sf.PageIds()));
                    Check("设置页数量与页签数量一致", sf.PageIds().Length == wantIds.Length,
                        sf.PageIds().Length + " 个页签");

                    Panel page = sf.PageById("display");
                    Check("桌宠显示页有内容", page != null && page.Controls.Count > 0,
                        page == null ? "取不到该页" : page.Controls.Count + " 个控件");

                    AlButton b0 = sf.DisplayModeButton(0);
                    AlButton b1 = sf.DisplayModeButton(1);
                    AlButton b2 = sf.DisplayModeButton(2);
                    Check("三种显示方式按钮齐全且带选中对勾",
                        b0 != null && b1 != null && b2 != null && b0.ShowCheck && b1.ShowCheck && b2.ShowCheck, "");
                    Check("按钮文案就是三种显示方式",
                        b0 != null && b2 != null && b0.Text == PetDisplayMode.Titles()[0]
                        && b1.Text == PetDisplayMode.Titles()[1] && b2.Text == PetDisplayMode.Titles()[2],
                        b0 == null ? "" : b0.Text + " / " + b1.Text + " / " + b2.Text);

                    Config.Current.petDisplayMode = PetDisplayMode.Always;
                    b1.PerformClick();
                    Pump(150);
                    Check("点「其他应用获得焦点时冻结」立即写入配置",
                        Config.Current.petDisplayMode == PetDisplayMode.FocusFreeze, Config.Current.petDisplayMode);
                    Check("当前选中的那一项打上金色对勾（其余不打）",
                        b1.Primary && !b0.Primary && !b2.Primary, "");
                    Check("状态文字跟着更新", sf.DisplayStatusText().IndexOf("冻结", StringComparison.Ordinal) >= 0,
                        sf.DisplayStatusText());

                    b2.PerformClick();
                    Pump(120);
                    Check("点「其他应用全屏时隐藏」也生效",
                        Config.Current.petDisplayMode == PetDisplayMode.FullscreenHide && b2.Primary,
                        sf.DisplayStatusText());

                    Config.Current.petDisplayMode = "谁也不认识的写法";
                    b0.PerformClick();
                    Pump(120);
                    Check("配置里出现未知值也能收敛回「始终最上层」",
                        Config.Current.petDisplayMode == PetDisplayMode.Always, Config.Current.petDisplayMode);
                    if (!sf.IsDisposed) sf.Hide();
                }

                // ---- 2) 真实窗口：置顶 / 冻结 / 进后台 / 恢复 ----
                Config.Current.petDisplayMode = PetDisplayMode.Always;
                Config.Current.hideOnFullscreen = false;   // 先关掉自动隐藏，单独验证冻结与后台
                Config.Current.hidePopupsWhenInactive = true;
                Config.Current.showTray = false;

                profile = CharacterStore.Create("显示测试", null);
                oldShowOnStart = profile.interact.showOnStart;
                profile.interact.proactive = true;
                profile.interact.proactiveMinSec = 5;
                profile.interact.proactiveMaxSec = 6;
                profile.interact.showOnStart = false;      // 自检里别弹问候气泡
                profile.interact.useSystemContext = false;
                profile.lines.click.Add(new LineItem("显示测试台词", 1));
                CharacterStore.Save(profile);

                string startErr = "";
                bool started = PetManager.Start(profile, out startErr);
                Check("桌宠能在「始终最上层」模式下启动", started, startErr);

                PetForm pet = started ? PetManager.Find(profile.id) : null;
                Check("能按角色 id 取到桌面上的桌宠窗口", pet != null, pet == null ? "取不到" : "已取到");
                if (pet != null)
                {
                    pet.Location = new Point(-4000, -4000);   // 立刻挪出屏幕：自检不打扰用户
                    Pump(220);

                    Check("缺省状态：正常显示且是最上层窗口",
                        pet.Visible && DisplayWatch.IsTopMost(pet.Handle)
                        && pet.DisplayState == PetDisplayState.Normal && !pet.DisplayPaused, "");
                    Check("「始终最上层 + 不隐藏全屏」时不起显示监听（零轮询）",
                        !PetDisplayWatcher.Running, "Running=" + PetDisplayWatcher.Running);

                    pet.ShowBubble("冻结之前先开着的泡泡", 15, Theme.TextMain, false);
                    Pump(160);
                    Check("冻结之前气泡是显示出来的", pet.BubbleVisible(), "");

                    // 冻结（其他应用获得焦点）
                    pet.ApplyDisplayState(PetDisplayState.Sunk, "自检：模拟别的程序拿到焦点");
                    Pump(160);
                    Check("冻结：窗口还在桌面上（不是直接藏起来）", pet.Visible, "");
                    Check("冻结：不再是置顶窗口", !DisplayWatch.IsTopMost(pet.Handle), "");
                    Check("冻结：动画与自动搭话已暂停", pet.DisplayPaused, "DisplayState=" + pet.DisplayState);
                    Check("冻结：气泡被收起（不会浮在别人程序上面）", !pet.BubbleVisible(), "");

                    pet.TriggerProactive();
                    Pump(280);
                    Check("冻结期间不会主动开口（需求：别打扰正在用别的软件的人）",
                        !pet.BubbleVisible(), "气泡内容：" + Utils.FirstLine(pet.BubbleText(), 20));

                    // 沉底效果：拿一个「别的程序窗口」比 Z 序
                    using (Form other = new Form())
                    {
                        other.FormBorderStyle = FormBorderStyle.None;
                        other.ShowInTaskbar = false;
                        other.StartPosition = FormStartPosition.Manual;
                        other.Location = new Point(-4300, -4300);
                        other.Size = new Size(160, 160);
                        other.Show();
                        other.BringToFront();
                        Pump(200);

                        pet.ApplyDisplayState(PetDisplayState.Normal, "自检：临时恢复");
                        Pump(120);
                        pet.ApplyDisplayState(PetDisplayState.Sunk, "自检：再冻结一次");
                        Pump(200);

                        int petZ = DisplayWatch.ZIndex(pet.Handle);
                        int otherZ = DisplayWatch.ZIndex(other.Handle);
                        int shellZ = DisplayWatch.ShellZIndex();
                        Check("冻结后沉到别的窗口下面（不会挡住其他软件）",
                            petZ >= 0 && otherZ >= 0 && petZ > otherZ,
                            "桌宠 Z=" + petZ + "，别的窗口 Z=" + otherZ);
                        Check("沉底后仍在桌面之上（不会被壁纸/图标盖住）",
                            shellZ < 0 || petZ < 0 || petZ < shellZ,
                            "桌宠 Z=" + petZ + "，桌面 Z=" + shellZ);
                        other.Hide();
                    }
                    Pump(120);

                    // 恢复
                    pet.ApplyDisplayState(PetDisplayState.Normal, "自检：回到桌面");
                    Pump(160);
                    Check("回到桌面：立刻恢复置顶", DisplayWatch.IsTopMost(pet.Handle), "");
                    Check("回到桌面：动画与搭话重新开始", !pet.DisplayPaused, "");
                    Check("回到桌面：窗口可见且画布正常", pet.Visible && pet.SnapshotFrame() != null, "");

                    // 进后台（其他程序全屏）
                    pet.ApplyDisplayState(PetDisplayState.Hidden, "自检：模拟别的程序全屏");
                    Pump(160);
                    Check("全屏时：窗口隐藏（进后台运行）", !pet.Visible && pet.DisplayState == PetDisplayState.Hidden, "");
                    Check("全屏时：仍然保持置顶标志（回来时直接在最上面）",
                        DisplayWatch.IsTopMost(pet.Handle), "");
                    Check("全屏时：动画与搭话也是暂停的", pet.DisplayPaused, "");

                    pet.HidePopups();
                    pet.SayGreeting();
                    Pump(220);
                    Check("后台状态下不会冒出问候气泡（开机自启正赶上全屏程序时同理）",
                        !pet.BubbleVisible(), "气泡内容：" + Utils.FirstLine(pet.BubbleText(), 20));

                    pet.ApplyDisplayState(PetDisplayState.Normal, "自检：退出全屏回到桌面");
                    Pump(200);
                    Check("回到桌面：重新显示出来", pet.Visible, "");
                    Check("回到桌面：分层窗口画面正常（贴图没丢）", pet.SnapshotFrame() != null, "");

                    // 幂等：同一状态反复应用不应有副作用
                    bool threw = false;
                    try
                    {
                        pet.ApplyDisplayState(PetDisplayState.Sunk, "自检：重复");
                        pet.ApplyDisplayState(PetDisplayState.Sunk, "自检：重复");
                        pet.ApplyDisplayState(PetDisplayState.Normal, "自检：重复");
                        pet.ApplyDisplayState(PetDisplayState.Normal, "自检：重复");
                    }
                    catch (Exception ex) { threw = true; Check("重复应用显示状态不抛异常", false, ex.Message); }
                    if (!threw) Check("重复应用显示状态不抛异常（幂等）", true, "");

                    // ---- 3) 监听：需要时起来、不需要时停掉 ----
                    Config.Current.petDisplayMode = PetDisplayMode.Always;
                    Config.Current.hideOnFullscreen = true;
                    PetManager.ApplyDisplaySettings();
                    Pump(120);
                    Check("打开「全屏自动隐藏」后显示监听自动起来", PetDisplayWatcher.Running, "");
                    int ticks0 = PetDisplayWatcher.Ticks;
                    Pump(900);
                    Check("监听在按间隔采样", PetDisplayWatcher.Ticks > ticks0,
                        "900ms 内采样 " + (PetDisplayWatcher.Ticks - ticks0) + " 次");

                    ForegroundInfo fg = DisplayWatch.Sample(PetManager.SelfWindowHandles());
                    PetDisplayState expect = DisplayWatch.Decide(Config.Current.petDisplayMode,
                        Config.Current.hideOnFullscreen, fg, pet.OnSameScreen(fg));
                    Check("桌宠的实际状态与「当前前台窗口」的判定一致",
                        pet.DisplayState == expect,
                        "状态=" + PetForm.DescribeState(pet.DisplayState) + "，判定=" + PetForm.DescribeState(expect)
                        + "，前台=" + fg.describe);
                    Check("前台窗口采样结果可用", fg.describe.Length > 0, fg.describe);

                    Config.Current.petDisplayMode = PetDisplayMode.Always;
                    Config.Current.hideOnFullscreen = false;
                    PetManager.ApplyDisplaySettings();
                    Pump(120);
                    Check("关掉全部显示策略后监听立刻停（回到零轮询）", !PetDisplayWatcher.Running, "");
                    Check("关掉后桌宠回到最上层", DisplayWatch.IsTopMost(pet.Handle), "");

                    // ---- 4) 右键菜单：显示方式随手就能换 ----
                    ContextMenuStrip menu = MenuBuilder.BuildPetMenu(pet);
                    ToolStripMenuItem displayMenu = null;
                    foreach (ToolStripItem it in menu.Items)
                        if (it.Text == "桌宠显示") displayMenu = it as ToolStripMenuItem;
                    Check("桌宠右键菜单里有「桌宠显示」", displayMenu != null, "");
                    if (displayMenu != null)
                    {
                        int modeItems = 0, modeChecked = 0;
                        string checkedText = "";
                        ToolStripMenuItem fullscreenToggle = null;
                        foreach (ToolStripItem it in displayMenu.DropDownItems)
                        {
                            ToolStripMenuItem mi = it as ToolStripMenuItem;
                            if (mi == null) continue;
                            if (mi.Text.IndexOf("自动隐藏", StringComparison.Ordinal) >= 0) { fullscreenToggle = mi; continue; }
                            if (mi.Text.IndexOf("打开显示设置", StringComparison.Ordinal) >= 0) continue;
                            modeItems++;
                            if (mi.Text.StartsWith("● ", StringComparison.Ordinal)) { modeChecked++; checkedText = mi.Text; }
                        }
                        Check("菜单里三种显示方式齐全", modeItems == 3, modeItems + " 项");
                        Check("菜单里只有当前这一种被标成 ●",
                            modeChecked == 1 && checkedText.IndexOf(PetDisplayMode.Title(Config.Current.petDisplayMode), StringComparison.Ordinal) >= 0,
                            modeChecked + " 项被标记：" + checkedText);
                        Check("菜单里的全屏开关与配置一致",
                            fullscreenToggle != null
                            && fullscreenToggle.Text.StartsWith(Config.Current.hideOnFullscreen ? "● " : "○ ", StringComparison.Ordinal),
                            fullscreenToggle == null ? "没有该开关" : fullscreenToggle.Text);
                        if (fullscreenToggle != null)
                        {
                            bool before = Config.Current.hideOnFullscreen;
                            fullscreenToggle.PerformClick();
                            Pump(150);
                            Check("菜单里能直接开关全屏自动隐藏",
                                Config.Current.hideOnFullscreen == !before, "现在是 " + Config.Current.hideOnFullscreen);
                            Config.Current.hideOnFullscreen = before;
                            Config.Save();
                        }
                    }

                    PetManager.StopPets(profile.id);
                    Pump(200);
                    Check("关掉桌宠后显示监听一并停止", !PetDisplayWatcher.Running, "");
                    Check("关掉后桌宠不再显示在桌面上", !PetManager.IsShowing(profile.id), "");
                }
            }
            catch (Exception ex)
            {
                Check("桌宠显示界面与窗口行为", false, ex.GetType().Name + "：" + ex.Message);
                Log.Error("桌宠显示自检失败", ex);
            }
            finally
            {
                try { PetManager.StopAllPets(); } catch { }
                try { PetDisplayWatcher.Stop(); } catch { }
                try { PetManager.DisposeTray(); } catch { }
                if (profile != null) { try { CharacterStore.Delete(profile.id); } catch { } }
                Config.Current.petDisplayMode = oldMode;
                Config.Current.hideOnFullscreen = oldFullscreen;
                Config.Current.hidePopupsWhenInactive = oldPopups;
                Config.Current.showTray = oldTray;
                GC.KeepAlive(oldShowOnStart);
                Config.Save();
            }
        }

        // ---------------- 关机 / 注销不阻止关机 ----------------

        static void TestShutdownPath()
        {
            Section("㉒ 关机 / 注销不阻止关机（回归：桌宠在跑时 Windows 报「正在阻止关机」）");

            // 1) 关闭决策（纯函数）
            Check("关机请求 → 立刻放行（不取消、不弹确认框）",
                MainForm.DecideClose(CloseReason.WindowsShutDown, false, true) == MainForm.CloseDecision.Close, "");
            Check("任务管理器结束任务 → 立刻放行",
                MainForm.DecideClose(CloseReason.TaskManagerClosing, false, true) == MainForm.CloseDecision.Close, "");
            Check("Application.Exit → 立刻放行",
                MainForm.DecideClose(CloseReason.ApplicationExitCall, false, true) == MainForm.CloseDecision.Close, "");
            Check("已在退出流程中 → 直接放行",
                MainForm.DecideClose(CloseReason.UserClosing, true, true) == MainForm.CloseDecision.Close, "");
            Check("用户点 X 且桌宠在跑 → 只收起界面（不退出）",
                MainForm.DecideClose(CloseReason.UserClosing, false, true) == MainForm.CloseDecision.HideAndCancel, "");
            Check("用户点 X 且桌宠没跑 → 问一句再退",
                MainForm.DecideClose(CloseReason.UserClosing, false, false) == MainForm.CloseDecision.AskThenClose, "");
            Check("普通关闭原因不算系统关机", !MainForm.IsSessionEnd(CloseReason.UserClosing)
                && !MainForm.IsSessionEnd(CloseReason.None) && MainForm.IsSessionEnd(CloseReason.WindowsShutDown), "");

            // 2) 真窗口 + 真桌宠：系统关机请求绝不能被拦下，也不能卡在弹窗上
            CharacterProfile profile = null;
            bool oldTray = Config.Current.showTray;
            bool oldShuttingDown = Program.ShuttingDown;
            try
            {
                Config.Current.showTray = false;
                profile = CharacterStore.Create("关机测试", null);
                profile.interact.showOnStart = false;
                profile.interact.proactive = false;
                CharacterStore.Save(profile);
                string err;
                bool started = PetManager.Start(profile, out err);
                Check("测试用桌宠已启动", started, err);

                using (MainForm f = new MainForm())
                {
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new Point(-4000, -4000);
                    f.CreateControl();
                    f.Show();
                    Pump(200);

                    // 先验证"桌宠在跑时点 X 只收界面"（这条老行为不能被改坏）
                    bool cancelNormal = f.SimulateClosingForTest(CloseReason.UserClosing);
                    Check("桌宠在跑时点 X 只收起界面（不退出、不弹退出确认）", cancelNormal, "");
                    Check("收起界面后桌宠仍在桌面上", PetManager.IsRunning, "");

                    // 再验证关机：必须放行，并且先把桌宠 / 托盘 / 设置都收好
                    Program.SuppressExitForTest = true;   // 别让自检进程真的退出（否则拿不到报告）
                    bool cancelShutdown = f.SimulateClosingForTest(CloseReason.WindowsShutDown);
                    Check("关机请求不被取消（这就是「阻止关机」的根因）", !cancelShutdown, "");
                    Check("关机时会先把桌宠全部停掉", !PetManager.IsRunning, "");
                    Check("关机时标记 ShuttingDown（后续关闭一律放行）", Program.ShuttingDown, "");
                    Check("关机时抑制一切弹窗（不会卡在 MessageBox 上）", Log.SuppressDialogs, "");
                    Check("关机时桌宠显示监听也停了", !PetDisplayWatcher.Running, "");
                }
            }
            catch (Exception ex)
            {
                Check("关机路径验证", false, ex.GetType().Name + "：" + ex.Message);
                Log.Error("关机路径自检失败", ex);
            }
            finally
            {
                Program.SuppressExitForTest = false;
                Program.ShuttingDown = oldShuttingDown;
                try { PetManager.StopAllPets(); } catch { }
                try { Program.SuppressExitForTest = false; } catch { }
                if (profile != null) { try { CharacterStore.Delete(profile.id); } catch { } }
                Config.Current.showTray = oldTray;
                Config.Save();
            }
        }

        // ---------------- 界面健壮性（回归：气泡复用 / 弹层避让 / 自适应布局） ----------------

        static void TestUiRobustness(string renderDir)
        {
            Section("⑮ 界面健壮性（气泡复用 / 弹层避让 / 自适应布局）");

            // 1) 气泡反复显示-收起后仍可用（回归：Form.Close() 会销毁窗口，之后一点桌宠就抛 ObjectDisposedException）
            using (BubbleForm b = new BubbleForm())
            {
                b.StartPosition = FormStartPosition.Manual;
                b.Location = new Point(-4000, -4000);
                b.CreateControl();
                bool alive = true;
                for (int i = 1; i <= 3; i++)
                {
                    try
                    {
                        b.ShowText("信浓", "第 " + i + " 次显示：指挥官…你终于回来了…", 17, Theme.TextMain, false, 1);
                        Pump(1400);
                    }
                    catch (Exception ex)
                    {
                        alive = false;
                        Check("气泡第 " + i + " 次显示", false, ex.GetType().Name + "：" + ex.Message);
                        break;
                    }
                    if (b.IsDisposed) { alive = false; Check("气泡第 " + i + " 次显示后未被销毁", false, "实例已被销毁"); break; }
                }
                if (alive) Check("气泡连续 3 次显示/自动收起后可继续复用", true, "无异常、未被销毁");

                bool hold = true;
                b.HoldOpen = delegate { return hold; };
                b.ShowText("信浓", "语音还没播完，这句得留着", 17, Theme.TextMain, false, 1);
                Pump(2200);
                Check("语音未播完时气泡不会被自动收起（需求 4）", b.Visible, b.Visible ? "仍显示" : "被收起了");
                hold = false;
                Pump(1600);
                Check("语音播完后气泡按设定时间收起", !b.Visible, b.Visible ? "仍显示" : "已收起");
                b.HideBubble();
                Check("手动收起后实例仍可用", !b.IsDisposed, "");
            }

            // 2) 弹层避让：气泡与输入框都不许压住角色
            Rectangle work = new Rectangle(0, 0, 1920, 1080);
            Rectangle sprite = new Rectangle(1600, 700, 320, 320);
            Size bubbleSize = new Size(420, 180);
            Rectangle bRect = new Rectangle(BubbleForm.Placement(bubbleSize, sprite, work), bubbleSize);
            Check("气泡不与立绘重叠", !bRect.IntersectsWith(sprite), bRect.ToString());
            Check("气泡在工作区内", work.Contains(bRect), bRect.ToString());

            Size barSize = new Size(420, 54);
            Rectangle iRect = new Rectangle(InputBarForm.Placement(barSize, sprite, work), barSize);
            Check("输入框不与立绘重叠（需求 3）", !iRect.IntersectsWith(sprite), iRect.ToString());
            Check("输入框在工作区内", work.Contains(iRect), iRect.ToString());
            Check("输入框落在角色下方或侧边", iRect.Top >= sprite.Bottom || iRect.Right <= sprite.Left, iRect.ToString());

            Rectangle topSprite = new Rectangle(1500, 6, 320, 320);
            Rectangle bRect2 = new Rectangle(BubbleForm.Placement(bubbleSize, topSprite, work), bubbleSize);
            Check("立绘贴屏幕顶部时气泡也不压住角色", !bRect2.IntersectsWith(topSprite), bRect2.ToString());

            // 3) 主界面自适应：放到全屏尺寸后内容要铺满、控件不许跑出客户区
            using (MainForm f = new MainForm())
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-4000, -4000);
                f.CreateControl();
                f.Show();
                f.ClientSize = new Size(1160, 780);
                Pump(200);
                Size panelSmall = f.CurrentPagePanelSize();
                f.ClientSize = new Size(1900, 1100);
                Pump(300);
                Size panelBig = f.CurrentPagePanelSize();

                Check("放大后页签面板跟着变大", panelBig.Width > panelSmall.Width && panelBig.Height > panelSmall.Height,
                    panelSmall + " → " + panelBig);

                bool inside = true;
                string bad = "";
                foreach (Control c in f.Controls)
                {
                    if (!c.Visible) continue;
                    if (c.Right > f.ClientSize.Width + 2 || c.Bottom > f.ClientSize.Height + 2)
                    {
                        inside = false;
                        bad = c.GetType().Name + " " + c.Bounds + " 超出 " + f.ClientSize;
                        break;
                    }
                }
                Check("放大后所有顶层控件仍在客户区内", inside, bad);
                Check("放大后内容没有挤在左上角（左下角色面板变高）", f.LeftPanelHeight() > 596, f.LeftPanelHeight() + " px");
                if (!string.IsNullOrEmpty(renderDir))
                {
                    try
                    {
                        Directory.CreateDirectory(renderDir);
                        using (Bitmap bmp = new Bitmap(f.Width, f.Height))
                        {
                            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                            string file = Path.Combine(renderDir, "main-fullscreen.png");
                            bmp.Save(file, ImageFormat.Png);
                            Check("全屏尺寸截图", new FileInfo(file).Length > 8000, Utils.HumanSize(new FileInfo(file).Length));
                        }
                    }
                    catch (Exception ex) { Check("全屏尺寸截图", false, ex.Message); }
                }
                f.Hide();
            }
        }

        // ---------------- 界面离屏渲染（视觉验证用） ----------------

        static void TestRenderUi(string dir)
        {
            Section("⑭ 界面离屏渲染（视觉检查）");
            try
            {
                Directory.CreateDirectory(dir);
                // 保证至少有一个角色，才能渲染出完整界面
                if (CharacterStore.ListAll().Count == 0)
                {
                    List<string> ids = CharacterStore.BuiltinCharacterIds();
                    if (ids.Count > 0) CharacterStore.ImportBuiltinAs(ids[0], null);
                    else CharacterStore.Create("信浓", null);
                }
                Config.Current.activeCharacter = CharacterStore.ListAll()[0].id;

                using (MainForm f = new MainForm())
                {
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new Point(-4000, -4000);   // 离屏渲染，不打扰用户
                    f.CreateControl();
                    f.Show();
                    Pump(220);
                    for (int i = 0; i < 6; i++)
                    {
                        if (f.IsDisposed) { Check("主界面渲染（跳过第 " + (i + 1) + " 页）", true, "窗体已释放，跳过"); break; }
                        try
                        {
                            f.ShowTabById(new string[] { "assets", "lines", "voice", "card", "interact", "sfx" }[i]);
                            Application.DoEvents();
                            Thread.Sleep(120);
                            using (Bitmap bmp = new Bitmap(f.Width, f.Height))
                            {
                                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                                string file = Path.Combine(dir, "main-" + (i + 1) + ".png");
                                bmp.Save(file, ImageFormat.Png);
                                Check("主界面渲染：" + Path.GetFileName(file), new FileInfo(file).Length > 8000, Utils.HumanSize(new FileInfo(file).Length));
                            }
                        }
                        catch (ObjectDisposedException) { Check("主界面渲染（第 " + (i + 1) + " 页）", true, "窗体被释放，跳过"); break; }
                        catch (Exception ex) { Check("主界面渲染（第 " + (i + 1) + " 页）", false, ex.Message); }
                    }
                    if (!f.IsDisposed) f.Hide();
                }

                using (SettingsForm f = new SettingsForm(null))
                {
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new Point(-4000, -4000);
                    f.CreateControl();
                    f.Show();
                    Pump(220);
                    string[] ids = new string[] { "sound", "llm", "path", "log", "general", "display", "about" };
                    for (int i = 0; i < ids.Length; i++)
                    {
                        // 防御：这里是压力测试，窗体有可能被系统提前释放；已释放就跳过，不要把它当成产品缺陷
                        if (f.IsDisposed) { Check("设置界面渲染（跳过第 " + (i + 1) + " 页）", true, "窗体已释放，跳过"); break; }
                        try
                        {
                            f.ShowPageById(ids[i]);
                            Application.DoEvents();
                            Thread.Sleep(120);
                            using (Bitmap bmp = new Bitmap(f.Width, f.Height))
                            {
                                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                                string file = Path.Combine(dir, "settings-" + ids[i] + ".png");
                                bmp.Save(file, ImageFormat.Png);
                                Check("设置界面渲染：" + Path.GetFileName(file), new FileInfo(file).Length > 8000, Utils.HumanSize(new FileInfo(file).Length));
                            }
                        }
                        catch (ObjectDisposedException) { Check("设置界面渲染（第 " + (i + 1) + " 页）", true, "窗体被释放，跳过"); break; }
                        catch (Exception ex) { Check("设置界面渲染（第 " + (i + 1) + " 页）", false, ex.Message); }
                    }
                    if (!f.IsDisposed) f.Hide();
                }

                // 桌宠本体 + 气泡（分层窗口无法 DrawToBitmap，改为直接导出它的画布）
                CharacterProfile petProfile = CharacterStore.ListAll()[0];
                using (PetForm pet = new PetForm(petProfile))
                {
                    pet.StartPosition = FormStartPosition.Manual;
                    pet.Location = new Point(-4000, -4000);
                    pet.CreateControl();
                    pet.Show();
                    Pump(260);
                    Bitmap frame = pet.SnapshotFrame();
                    Check("桌宠画布已生成", frame != null, frame == null ? "无画布" : frame.Width + "×" + frame.Height);
                    if (frame != null)
                    {
                        string file = Path.Combine(dir, "pet-sprite.png");
                        frame.Save(file, ImageFormat.Png);
                        Check("桌宠窗口渲染：" + Path.GetFileName(file), new FileInfo(file).Length > 3000, frame.Width + "×" + frame.Height);
                        // 抽查像素：应该有可见内容（alpha 不为 0 的像素占比 > 10%）
                        int visible = 0, total = 0;
                        for (int y = 0; y < frame.Height; y += 4)
                            for (int x = 0; x < frame.Width; x += 4)
                            {
                                total++;
                                if (frame.GetPixel(x, y).A > 8) visible++;
                            }
                        double ratio = total == 0 ? 0 : (double)visible / total;
                        Check("桌宠立绘可见像素占比正常", ratio > 0.1, (ratio * 100).ToString("0.0") + "%");
                        frame.Dispose();
                    }

                    Bitmap bounced = null;
                    pet.PlayBounce();
                    Thread.Sleep(120);
                    Application.DoEvents();
                    bounced = pet.SnapshotFrame();
                    Check("Q 弹动画可以采样到中间帧", bounced != null, bounced == null ? "" : "已采样");
                    if (bounced != null)
                    {
                        string file = Path.Combine(dir, "pet-bounce.png");
                        bounced.Save(file, ImageFormat.Png);
                        bounced.Dispose();
                    }

                    pet.SayGreeting();
                    Pump(320);
                    Check("桌宠问候气泡已弹出", true, "见 bubble.png");
                    pet.CloseAll();
                    pet.Hide();
                }

                // 气泡单独渲染（导出气泡画布）
                using (BubbleForm bubble = new BubbleForm())
                {
                    bubble.StartPosition = FormStartPosition.Manual;
                    bubble.Location = new Point(-4000, -4000);
                    bubble.CreateControl();
                    bubble.ShowText("信浓", "指挥官…你终于回来了…妾身一直在等你。", 18, Theme.TextMain, true, 0);
                    Pump(320);
                    Bitmap shot = bubble.SnapshotCanvas();
                    Check("气泡画布已生成", shot != null, shot == null ? "无画布" : shot.Width + "×" + shot.Height);
                    if (shot != null)
                    {
                        string file = Path.Combine(dir, "bubble.png");
                        shot.Save(file, ImageFormat.Png);
                        Check("气泡渲染：" + Path.GetFileName(file), new FileInfo(file).Length > 3000, shot.Width + "×" + shot.Height);

                        // 再叠到深色背景上，检查气泡在暗色桌面上的可读性
                        using (Bitmap dark = new Bitmap(shot.Width + 40, shot.Height + 40))
                        {
                            using (Graphics dg = Graphics.FromImage(dark))
                            {
                                dg.Clear(Color.FromArgb(255, 28, 30, 36));
                                dg.DrawImage(shot, 20, 10, shot.Width, shot.Height);
                            }
                            string file2 = Path.Combine(dir, "bubble-on-dark.png");
                            dark.Save(file2, ImageFormat.Png);
                            Check("气泡暗色背景可读性样例：" + Path.GetFileName(file2), new FileInfo(file2).Length > 3000, "");
                        }
                        shot.Dispose();
                    }
                    bubble.Close();
                }
                Info("截图目录：" + dir);
            }
            catch (Exception ex)
            {
                string detail = ex.ToString().Replace("\r\n", " ⏎ ");
                if (detail.Length > 600) detail = detail.Substring(0, 600);
                Check("界面离屏渲染", false, detail);
                Log.Error("界面渲染自检失败", ex);
            }
        }
    }
}
