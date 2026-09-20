// ============================================================================
// Store.cs —— 路径 / 日志 / 配置 / 角色库 / 台词解析 / 语音清单
// ----------------------------------------------------------------------------
// 目录约定（与安装器、卸载器一致）：
//   安装目录\AzurLaneDeskPet.exe、assets\builtin\**、卸载-灵工桌宠.exe
//   用户数据目录（默认 %APPDATA%\AzurLaneDeskPet，可在设置里改）：
//       config.json
//       logs\pet-YYYYMMDD.log
//       characters\<id>\profile.json、images\、voices\、sfx\、lines\
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace AlDeskPet
{
    // ========================================================================
    // 路径
    // ========================================================================
    public static class AppPaths
    {
        public const string ProductName = "灵工桌宠";
        public const string ProductNameEn = "AzurLaneDeskPet";
        public const string Version = "1.1.0";
        public const string Author = "睡不着のHATSUZUKI";
        public const string DataDirName = "AzurLaneDeskPet";

        /// <summary>
        /// 数据目录覆盖用的环境变量。
        /// 自动化测试 / 验收脚本靠它把数据写进沙箱目录，**绝不碰用户的真实数据**
        /// （v1.0.8 之前验收脚本会直接删掉 %APPDATA% 下的真实数据目录，导致用户设置与角色"全部消失"）。
        /// </summary>
        public const string DataDirEnvVar = "AZURLANEDESK_PET_DATA";

        /// <summary>数据目录里的配置文件名（卸载器也认这个名字）。</summary>
        public const string ConfigFileName = "config.json";

        public static string InstallDir()
        {
            return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        }

        public static string BuiltinDir()
        {
            return Path.Combine(InstallDir(), "assets", "builtin");
        }

        /// <summary>默认数据目录：环境变量 AZURLANEDESK_PET_DATA → %APPDATA%\AzurLaneDeskPet。</summary>
        public static string DefaultDataDir()
        {
            try
            {
                string env = Environment.GetEnvironmentVariable(DataDirEnvVar);
                if (!string.IsNullOrEmpty(env))
                {
                    env = Environment.ExpandEnvironmentVariables(env.Trim());
                    if (env.Length > 0) return Path.GetFullPath(env);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取数据目录环境变量失败（改用默认目录）：" + ex.Message);
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DataDirName);
        }

        static string _dataDir = "";
        static bool _locked;

        /// <summary>当前生效的用户数据目录（可由设置覆盖）。</summary>
        public static string DataDir()
        {
            if (!string.IsNullOrEmpty(_dataDir)) return _dataDir;
            return DefaultDataDir();
        }

        public static void SetDataDir(string dir)
        {
            if (_locked) return;   // 自检等场景下锁定，避免被 Config.Load 的 Normalize 改回默认目录
            if (string.IsNullOrEmpty(dir)) { _dataDir = ""; return; }
            string full;
            try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dir)); }
            catch { full = DefaultDataDir(); }
            _dataDir = full;
        }

        /// <summary>钉死数据目录（自检专用）：之后任何 SetDataDir 都会被忽略。</summary>
        public static void LockDataDir(string dir)
        {
            _dataDir = "";
            _locked = false;
            SetDataDir(dir);
            _locked = true;
        }

        public static void UnlockDataDir()
        {
            _locked = false;
        }

        /// <summary>数据目录是否被钉死（自检等场景）。</summary>
        public static bool IsDataDirLocked() { return _locked; }

        public static string ConfigFile() { return Path.Combine(DataDir(), ConfigFileName); }
        public static string LogDir() { return Path.Combine(DataDir(), "logs"); }
        public static string CharactersDir() { return Path.Combine(DataDir(), "characters"); }
        public static string CharacterDir(string id) { return Path.Combine(CharactersDir(), id); }

        /// <summary>数据目录标记文件：卸载器靠它确认「这确实是桌宠的数据目录」，避免误删别人的目录。</summary>
        public const string DataMarkerName = ".azurlandeskpet-data";

        public static string DataMarkerFile() { return Path.Combine(DataDir(), DataMarkerName); }

        public static void EnsureDataDirs()
        {
            Directory.CreateDirectory(DataDir());
            Directory.CreateDirectory(LogDir());
            Directory.CreateDirectory(CharactersDir());
            WriteDataMarker();
        }

        /// <summary>写数据目录标记（内容只是标识与版本，便于卸载器安全校验）。</summary>
        public static void WriteDataMarker()
        {
            try
            {
                string file = DataMarkerFile();
                string want = ProductNameEn + " data dir v" + Version + Environment.NewLine
                            + "本文件用于卸载程序确认该目录属于「" + ProductName + "」，请勿删除。" + Environment.NewLine;
                if (!File.Exists(file)) File.WriteAllText(file, want, new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                Log.Warn("写数据目录标记失败：" + ex.Message);
            }
        }
    }

    // ========================================================================
    // 日志
    // ========================================================================
    public static class Log
    {
        static readonly object Gate = new object();
        public static bool Verbose = false;
        /// <summary>自检 / 静默模式下不发弹窗（否则会卡在模态对话框上）。</summary>
        public static bool SuppressDialogs = false;

        public static string CurrentLogFile()
        {
            return Path.Combine(AppPaths.LogDir(), "pet-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");
        }

        public static void Info(string message) { Write("INFO ", message, null); }
        public static void Warn(string message) { Write("WARN ", message, null); }
        public static void Debug(string message) { if (Verbose) Write("DEBUG", message, null); }

        public static void Error(string message) { Write("ERROR", message, null); }
        public static void Error(string message, Exception ex) { Write("ERROR", message, ex); }

        static void Write(string level, string message, Exception ex)
        {
            string line = string.Format(CultureInfo.InvariantCulture, "[{0}] [{1}] {2}{3}",
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                level,
                message,
                ex == null ? "" : (" | " + ex.GetType().Name + ": " + ex.Message + "\r\n" + ex.StackTrace));
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.LogDir());
                    File.AppendAllText(CurrentLogFile(), line + Environment.NewLine, new UTF8Encoding(true));
                }
                catch { /* 日志写不进去时不再递归报错 */ }
            }
        }

        /// <summary>启动时清理过期日志（默认保留 14 天）。</summary>
        public static void CleanupOldLogs(int keepDays)
        {
            if (keepDays <= 0) keepDays = 14;
            try
            {
                string dir = AppPaths.LogDir();
                if (!Directory.Exists(dir)) return;
                DateTime limit = DateTime.Now.AddDays(-keepDays);
                foreach (string f in Directory.GetFiles(dir, "pet-*.log"))
                {
                    try { if (File.GetLastWriteTime(f) < limit) File.Delete(f); }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// 统一的错误弹窗：按需求，明确告诉用户「日志已生成、路径在哪、
        /// 请把日志上传给 AI 或技术人员，而不是只截这张图」。
        /// </summary>
        public static void ErrorDialog(string context, Exception ex)
        {
            Error(context, ex);
            string path = CurrentLogFile();
            string text =
                "软件遇到错误：" + context + "\r\n\r\n" +
                "错误日志已生成，路径：\r\n" + path + "\r\n\r\n" +
                "如果需要帮助，请把这个日志文件直接上传给 AI 或相关技术人员——" +
                "日志里有完整的错误原因和调用位置，比只截这张弹窗有用得多。";
            if (SuppressDialogs)
            {
                try { Console.WriteLine("  [ERROR] " + context + " → " + (ex == null ? "" : ex.Message) + "（已写入日志：" + path + "）"); } catch { }
                return;
            }
            try
            {
                using (ErrorDialogForm dlg = new ErrorDialogForm(context, ex, path, text))
                {
                    dlg.ShowDialog();
                }
            }
            catch
            {
                try { MessageBox.Show(text, AppPaths.ProductName + " · 错误", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                catch { }
            }
        }
    }

    // ========================================================================
    // 配置
    // ========================================================================
    public static class Config
    {
        public static AppSettings Current = new AppSettings();

        /// <summary>配置备份文件名（每次成功保存前留一份旧配置，损坏/误删时可回滚）。</summary>
        public const string BackupSuffix = ".bak";

        /// <summary>
        /// 本次启动**没能读出**原有 config.json（文件在但解析不了，且备份也不可用）。
        /// 此时绝不能静默覆盖：第一次 Save 会把坏文件改名为 .bad-* 留档，再写新配置。
        /// </summary>
        public static bool LoadFailed;

        static AppSettings ParseSettings(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            object node = Json.TryParse(text);
            if (node == null) return null;
            return Json.Bind<AppSettings>(node);
        }

        public static void Load()
        {
            LoadFailed = false;
            AppSettings s = null;
            string file = AppPaths.ConfigFile();
            string backup = file + BackupSuffix;
            bool fileExists = false;
            try
            {
                fileExists = File.Exists(file);
                if (fileExists) s = ParseSettings(File.ReadAllText(file, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                Log.Error("读取配置失败，将尝试备份", ex);
            }

            if (s == null && File.Exists(backup))
            {
                // 主配置读不出来（损坏 / 被写坏）：先用备份把用户的设置恢复回来
                try
                {
                    if (fileExists)
                    {
                        string bad = file + ".bad-" + Utils.NowFileStamp() + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
                        try { File.Copy(file, bad, true); } catch { }
                    }
                    s = ParseSettings(File.ReadAllText(backup, Encoding.UTF8));
                    if (s != null) Log.Warn("config.json 读取失败，已从备份 " + Path.GetFileName(backup) + " 恢复设置");
                }
                catch (Exception ex)
                {
                    Log.Warn("读取配置备份失败：" + ex.Message);
                }
            }

            if (s == null && fileExists)
            {
                // 文件在但读不出来、也没有可用备份：标记一下，避免后面的保存把用户数据直接盖掉
                LoadFailed = true;
                Log.Error("config.json 无法解析，且没有可用备份；为避免覆盖，保存时会先把它改名留档");
            }

            if (s == null) s = new AppSettings();
            Current = s;
            Normalize();
            // 解密落盘的敏感字段：内存中始终是明文，旧版明文配置也能直接读进来。
            Current.llm.apiKey = Utils.Unprotect(Current.llm.apiKey);
        }

        /// <summary>写盘前的敏感字段加密（只影响磁盘内容，内存对象保持明文）。</summary>
        static string ProtectedConfigJson()
        {
            string plain = Current.llm != null ? Current.llm.apiKey : "";
            if (Current.llm != null) Current.llm.apiKey = Utils.Protect(plain);
            try
            {
                return Json.Write(Current);
            }
            finally
            {
                if (Current.llm != null) Current.llm.apiKey = plain;   // 还原内存明文，调用方无感
            }
        }

        static void Normalize()
        {
            AppSettings s = Current;
            if (s.llm == null) s.llm = new LlmSettings();
            if (s.characterOrder == null) s.characterOrder = new List<string>();
            if (s.volume < 0) s.volume = 0;
            if (s.volume > 100) s.volume = 100;
            if (s.llm.timeoutSec < 5) s.llm.timeoutSec = 5;
            if (s.llm.timeoutSec > 600) s.llm.timeoutSec = 600;
            if (s.llm.historyTurns < 0) s.llm.historyTurns = 0;
            if (s.llm.historyTurns > 50) s.llm.historyTurns = 50;
            if (s.logKeepDays <= 0) s.logKeepDays = 14;
            s.petDisplayMode = PetDisplayMode.Normalize(s.petDisplayMode);
            AppPaths.SetDataDir(s.dataDir);
        }

        public static void Save()
        {
            try
            {
                AppPaths.EnsureDataDirs();
                WriteConfig();
            }
            catch (Exception ex)
            {
                Log.Error("保存配置失败", ex);
            }
        }

        public static void SaveOrDialog(string context)
        {
            try
            {
                AppPaths.EnsureDataDirs();
                WriteConfig();
            }
            catch (Exception ex)
            {
                Log.ErrorDialog(context + "（保存配置失败）", ex);
            }
        }

        /// <summary>
        /// 原子写配置，并留一份可用的备份：
        /// · 正常情况：写成功后把**刚写好的这份**（能解析才留）同步成 config.json.bak，
        ///   这样下次 config.json 损坏 / 被写坏时能恢复到最近一次设置，而不是"设置全没了"；
        /// · 本次启动就没读出原配置（LoadFailed）：把坏文件改名成 config.json.bad-* 留档，绝不静默覆盖。
        /// </summary>
        static void WriteConfig()
        {
            string file = AppPaths.ConfigFile();
            if (LoadFailed)
            {
                try { File.Move(file, UniqueBadName(file)); }
                catch (Exception ex) { Log.Warn("留档坏配置失败：" + ex.Message); }
                LoadFailed = false;
                Log.Warn("原 config.json 无法解析，已改名留档（.bad-*）后写入新配置");
            }

            string json = ProtectedConfigJson();
            Utils.WriteAllTextAtomic(file, json);

            // 备份只在"这份内容确实是合法配置"时才更新，避免把坏内容也备份进去
            try
            {
                if (ParseSettings(json) != null) File.WriteAllText(file + BackupSuffix, json, new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                Log.Debug("更新配置备份失败（不影响保存）：" + ex.Message);
            }
        }

        /// <summary>坏配置留档名（同一秒内多次留档也不会撞名）。</summary>
        static string UniqueBadName(string file)
        {
            return file + ".bad-" + Utils.NowFileStamp() + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
        }

        /// <summary>迁移数据目录：把旧目录整体搬到新目录（同盘 move / 跨盘 copy+delete）。</summary>
        public static bool MoveDataDir(string newDir, out string message)
        {
            message = "";
            string oldDir = AppPaths.DataDir();
            try
            {
                string target = Path.GetFullPath(Environment.ExpandEnvironmentVariables(newDir));
                if (Utils.SamePath(target, oldDir)) { message = "目录未变化。"; return true; }
                if (Utils.IsUnder(target, oldDir)) { message = "新目录不能位于旧目录内部。"; return false; }

                Directory.CreateDirectory(target);
                int moved = 0;
                foreach (string sub in new string[] { "characters", "logs" })
                {
                    string src = Path.Combine(oldDir, sub);
                    if (!Directory.Exists(src)) continue;
                    string dst = Path.Combine(target, sub);
                    moved += CopyTree(src, dst);
                }
                // config.json 连同自定义 dataDir 一起写过去
                string cfgSrc = Path.Combine(oldDir, "config.json");
                if (File.Exists(cfgSrc))
                {
                    File.Copy(cfgSrc, Path.Combine(target, "config.json"), true);
                    moved++;
                }

                Current.dataDir = target;
                AppPaths.SetDataDir(target);
                Save();

                // 清掉旧目录（只删我们自己的子目录与配置文件，保守处理）
                try
                {
                    foreach (string sub in new string[] { "characters", "logs" })
                    {
                        string src = Path.Combine(oldDir, sub);
                        if (Directory.Exists(src)) Directory.Delete(src, true);
                    }
                    string cfg = Path.Combine(oldDir, "config.json");
                    if (File.Exists(cfg)) File.Delete(cfg);
                }
                catch (Exception ex) { Log.Warn("旧数据目录清理不完整：" + ex.Message); }

                message = "已迁移 " + moved + " 个文件到：\r\n" + target;
                Log.Info("数据目录迁移完成 → " + target);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("迁移数据目录失败", ex);
                message = "迁移失败：" + ex.Message;
                return false;
            }
        }

        static int CopyTree(string src, string dst)
        {
            int count = 0;
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
            {
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
                count++;
            }
            foreach (string d in Directory.GetDirectories(src))
                count += CopyTree(d, Path.Combine(dst, Path.GetFileName(d)));
            return count;
        }
    }

    // ========================================================================
    // 角色库
    // ========================================================================
    public static class CharacterStore
    {
        public static string ProfileFile(string id) { return Path.Combine(AppPaths.CharacterDir(id), "profile.json"); }
        public static string ImagesDir(string id) { return Path.Combine(AppPaths.CharacterDir(id), "images"); }
        public static string VoicesDir(string id) { return Path.Combine(AppPaths.CharacterDir(id), "voices"); }
        public static string SfxDir(string id) { return Path.Combine(AppPaths.CharacterDir(id), "sfx"); }
        public static string LinesDir(string id) { return Path.Combine(AppPaths.CharacterDir(id), "lines"); }

        public static void EnsureCharacterDirs(string id)
        {
            Directory.CreateDirectory(AppPaths.CharacterDir(id));
            Directory.CreateDirectory(ImagesDir(id));
            Directory.CreateDirectory(VoicesDir(id));
            Directory.CreateDirectory(SfxDir(id));
            Directory.CreateDirectory(LinesDir(id));
        }

        /// <summary>列出全部角色（按配置里的顺序，未知的排后面）。</summary>
        public static List<CharacterProfile> ListAll()
        {
            List<CharacterProfile> result = new List<CharacterProfile>();
            List<string> ids = new List<string>();
            if (Directory.Exists(AppPaths.CharactersDir()))
            {
                foreach (string d in Directory.GetDirectories(AppPaths.CharactersDir()))
                    ids.Add(Path.GetFileName(d));
            }
            List<string> ordered = new List<string>();
            foreach (string id in Config.Current.characterOrder)
                if (ids.Contains(id) && !ordered.Contains(id)) ordered.Add(id);
            ordered.Sort(delegate (string a, string b) { return string.Compare(a, b, StringComparison.OrdinalIgnoreCase); });
            List<string> final = new List<string>();
            foreach (string id in Config.Current.characterOrder) if (ids.Contains(id) && !final.Contains(id)) final.Add(id);
            foreach (string id in ordered) if (!final.Contains(id)) final.Add(id);

            foreach (string id in final)
            {
                CharacterProfile p = Load(id);
                if (p != null) result.Add(p);
            }
            return result;
        }

        public static CharacterProfile Load(string id)
        {
            try
            {
                string file = ProfileFile(id);
                if (!File.Exists(file)) return null;
                object node = Json.TryParse(File.ReadAllText(file, Encoding.UTF8));
                if (node == null) return null;
                CharacterProfile p = Json.Bind<CharacterProfile>(node);
                if (string.IsNullOrEmpty(p.id)) p.id = id;
                Normalize(p);
                return p;
            }
            catch (Exception ex)
            {
                Log.Error("读取角色失败：" + id, ex);
                return null;
            }
        }

        public static void Normalize(CharacterProfile p)
        {
            if (p.images == null) p.images = new List<string>();
            if (p.lines == null) p.lines = new LineSet();
            if (p.lines.greet == null) p.lines.greet = new List<LineItem>();
            if (p.lines.click == null) p.lines.click = new List<LineItem>();
            if (p.lines.proactive == null) p.lines.proactive = new List<LineItem>();
            if (p.lines.idle == null) p.lines.idle = new List<LineItem>();
            if (p.lines.system == null) p.lines.system = new List<LineItem>();
            if (p.voices == null) p.voices = new VoiceBank();
            if (p.voices.entries == null) p.voices.entries = new List<VoiceEntry>();
            if (p.card == null) p.card = new CharacterCard();
            if (p.interact == null) p.interact = new InteractionSettings();
            if (p.sfx == null) p.sfx = new SfxSettings();
            InteractionClamp(p.interact);
        }

        public static void InteractionClamp(InteractionSettings s)
        {
            if (s.proactiveMinSec < 5) s.proactiveMinSec = 5;
            if (s.proactiveMaxSec < s.proactiveMinSec) s.proactiveMaxSec = s.proactiveMinSec;
            if (s.proactiveMaxSec > 24 * 3600) s.proactiveMaxSec = 24 * 3600;
            if (s.idleMinSec < 30) s.idleMinSec = 30;
            if (s.bubbleSeconds < 0) s.bubbleSeconds = 0;
            if (s.bubbleSeconds > 600) s.bubbleSeconds = 600;
            if (s.scale < 0.2) s.scale = 0.2;
            if (s.scale > 4.0) s.scale = 4.0;
            if (s.animDurationMs < 120) s.animDurationMs = 120;
            if (s.animDurationMs > 2000) s.animDurationMs = 2000;
            if (s.mode != "llm") s.mode = "fixed";
            if (s.fixedVoiceMode != "off" && s.fixedVoiceMode != "always") s.fixedVoiceMode = "match";
        }

        public static bool Save(CharacterProfile p)
        {
            try
            {
                p.updatedAt = Utils.NowStamp();
                EnsureCharacterDirs(p.id);
                Utils.WriteAllTextAtomic(ProfileFile(p.id), Json.Write(p));
                return true;
            }
            catch (Exception ex)
            {
                Log.ErrorDialog("保存角色「" + p.DisplayName() + "」失败", ex);
                return false;
            }
        }

        public static CharacterProfile Create(string name, string sourceCharacterId)
        {
            string baseId = Utils.SafeId(name, "character");
            string id = baseId;
            int n = 2;
            while (Directory.Exists(AppPaths.CharacterDir(id)))
            {
                id = baseId + "_" + n.ToString(CultureInfo.InvariantCulture);
                n++;
            }

            CharacterProfile p = new CharacterProfile();
            p.id = id;
            p.name = name;
            p.createdAt = Utils.NowStamp();
            EnsureCharacterDirs(id);

            if (!string.IsNullOrEmpty(sourceCharacterId))
            {
                // 从内置素材 / 已有角色复制
                CharacterProfile src = Load(sourceCharacterId);
                if (src != null)
                {
                    p.faction = src.faction;
                    p.shipType = src.shipType;
                    p.note = src.note;
                    p.card.persona = src.card.persona;
                    p.card.speechStyle = src.card.speechStyle;
                    p.card.greeting = src.card.greeting;
                    p.card.extraRules = src.card.extraRules;
                    p.lines = CloneLines(src.lines);
                    foreach (string img in src.images) ImportImage(id, Path.Combine(ImagesDir(src.id), img));
                    foreach (string f in Directory.Exists(VoicesDir(src.id)) ? Directory.GetFiles(VoicesDir(src.id)) : new string[0])
                        Utils.CopyFile(f, Path.Combine(VoicesDir(id), Path.GetFileName(f)));
                    foreach (string f in Directory.Exists(SfxDir(src.id)) ? Directory.GetFiles(SfxDir(src.id)) : new string[0])
                        Utils.CopyFile(f, Path.Combine(SfxDir(id), Path.GetFileName(f)));
                    p.voices = CloneVoices(src.voices);
                }
            }

            Save(p);
            Config.Current.characterOrder.Add(id);
            Config.Save();
            Log.Info("新建角色：" + id + "（" + name + "）");
            return p;
        }

        public static LineSet CloneLines(LineSet src)
        {
            LineSet d = new LineSet();
            foreach (string sec in LineSet.SectionNames())
            {
                List<LineItem> from = src.Section(sec);
                List<LineItem> to = d.Section(sec);
                foreach (LineItem it in from) to.Add(CloneLine(it));
            }
            return d;
        }

        public static LineItem CloneLine(LineItem it)
        {
            LineItem c = new LineItem();
            c.text = it.text;
            c.weight = it.weight;
            c.size = it.size;
            c.color = it.color;
            c.bold = it.bold;
            c.voice = it.voice;
            return c;
        }

        public static VoiceBank CloneVoices(VoiceBank src)
        {
            VoiceBank v = new VoiceBank();
            v.manifest = src.manifest;
            v.note = src.note;
            foreach (VoiceEntry e in src.entries)
            {
                VoiceEntry c = new VoiceEntry();
                c.text = e.text;
                c.file = e.file;
                v.entries.Add(c);
            }
            return v;
        }

        /// <summary>导入一张立绘（复制进角色目录，自动重名处理）。</summary>
        public static string ImportImage(string id, string srcFile)
        {
            try
            {
                EnsureCharacterDirs(id);
                string name = Path.GetFileName(srcFile);
                string dst = Path.Combine(ImagesDir(id), name);
                int n = 2;
                while (File.Exists(dst))
                {
                    string stem = Path.GetFileNameWithoutExtension(name);
                    string ext = Path.GetExtension(name);
                    dst = Path.Combine(ImagesDir(id), stem + "_" + n.ToString(CultureInfo.InvariantCulture) + ext);
                    n++;
                }
                Utils.CopyFile(srcFile, dst);
                return Path.GetFileName(dst);
            }
            catch (Exception ex)
            {
                Log.ErrorDialog("导入图片失败", ex);
                return "";
            }
        }

        public static void Delete(string id)
        {
            try
            {
                string dir = AppPaths.CharacterDir(id);
                if (Utils.IsUnder(dir, AppPaths.CharactersDir()) && Directory.Exists(dir)) Directory.Delete(dir, true);
                Config.Current.characterOrder.Remove(id);
                if (Config.Current.activeCharacter == id) Config.Current.activeCharacter = "";
                Config.Save();
                Log.Info("删除角色：" + id);
            }
            catch (Exception ex)
            {
                Log.ErrorDialog("删除角色失败", ex);
            }
        }

        /// <summary>从安装目录的 assets\builtin 导入一个内置角色模板。</summary>
        public static CharacterProfile ImportBuiltin(string builtinId)
        {
            return ImportBuiltinAs(builtinId, null);
        }

        /// <summary>从安装目录的 assets\builtin 导入一个内置角色模板（可自定义显示名）。</summary>
        public static CharacterProfile ImportBuiltinAs(string builtinId, string customName)
        {
            try
            {
                string src = Path.Combine(AppPaths.BuiltinDir(), "characters", builtinId);
                if (!Directory.Exists(src)) return null;

                string displayName = builtinId;
                string infoFile = Path.Combine(src, "info.json");
                if (File.Exists(infoFile))
                {
                    Dictionary<string, object> info = Json.AsObj(Json.TryParse(File.ReadAllText(infoFile, Encoding.UTF8)));
                    if (info != null) displayName = Json.S(info, "name", builtinId);
                }
                if (!string.IsNullOrEmpty(customName)) displayName = customName;

                CharacterProfile p = Create(displayName, null);
                // 立绘
                foreach (string img in Directory.GetFiles(src, "*.png"))
                {
                    string name = ImportImage(p.id, img);
                    if (name.Length > 0 && !p.images.Contains(name)) p.images.Add(name);
                }

                // info
                string infoTxt = Path.Combine(src, "info.json");
                if (File.Exists(infoTxt))
                {
                    Dictionary<string, object> info = Json.AsObj(Json.TryParse(File.ReadAllText(infoTxt, Encoding.UTF8)));
                    if (info != null)
                    {
                        if (string.IsNullOrEmpty(customName)) p.name = Json.S(info, "name", p.name);
                        p.faction = Json.S(info, "faction", "");
                        p.shipType = Json.S(info, "type", "");
                        p.note = Json.S(info, "artist", "");
                    }
                }

                // 角色卡
                string cardFile = Path.Combine(src, "card.txt");
                if (File.Exists(cardFile)) p.card.persona = File.ReadAllText(cardFile, Encoding.UTF8).Trim();

                // 台词
                string lineFile = Path.Combine(AppPaths.BuiltinDir(), "lines", builtinId + ".txt");
                if (File.Exists(lineFile)) p.lines = LinePack.ParseTextFile(lineFile, p.lines);

                // 语音（把清单里的文本对齐到台词；音频整包复制）
                string voiceSrc = Path.Combine(AppPaths.BuiltinDir(), "voices", builtinId);
                if (Directory.Exists(voiceSrc))
                {
                    foreach (string f in Directory.GetFiles(voiceSrc))
                    {
                        if (Path.GetExtension(f).ToLowerInvariant() == ".txt" && Path.GetFileName(f) == "manifest.txt") continue;
                        Utils.CopyFile(f, Path.Combine(VoicesDir(p.id), Path.GetFileName(f)));
                    }
                    string manifestSrc = Path.Combine(voiceSrc, "manifest.txt");
                    if (File.Exists(manifestSrc))
                    {
                        Utils.CopyFile(manifestSrc, Path.Combine(VoicesDir(p.id), "manifest.txt"));
                        VoiceBankLoader.Load(p, "manifest.txt");
                    }
                }

                // 头像对齐
                p.imageIndex = 0;
                p.builtinId = builtinId;    // 记下来源模板，便于「开箱补齐 5 位」时判重（不重复导入同一个模板）
                Save(p);
                Log.Info("导入内置角色：" + builtinId + " → " + p.id);
                return p;
            }
            catch (Exception ex)
            {
                Log.ErrorDialog("导入内置角色失败", ex);
                return null;
            }
        }

        /// <summary>内置角色模板列表（assets\builtin\characters 下的目录名）。</summary>
        public static List<string> BuiltinCharacterIds()
        {
            List<string> ids = new List<string>();
            try
            {
                string dir = Path.Combine(AppPaths.BuiltinDir(), "characters");
                if (!Directory.Exists(dir)) return ids;
                foreach (string d in Directory.GetDirectories(dir)) ids.Add(Path.GetFileName(d));
            }
            catch { }
            return ids;
        }

        public static string BuiltinDisplayName(string builtinId)
        {
            try
            {
                string infoFile = Path.Combine(AppPaths.BuiltinDir(), "characters", builtinId, "info.json");
                if (File.Exists(infoFile))
                {
                    Dictionary<string, object> info = Json.AsObj(Json.TryParse(File.ReadAllText(infoFile, Encoding.UTF8)));
                    if (info != null) return Json.S(info, "name", builtinId);
                }
            }
            catch { }
            return builtinId;
        }
    }

    // ========================================================================
    // 台词解析 / 导出
    // ========================================================================
    public static class LinePack
    {
        /// <summary>
        /// 解析 txt 台词文件。支持：
        ///   # 注释 / // 注释
        ///   @问候 @点击 @主动 @待机 @系统（兼容插件的 @随机 → 点击）
        ///   [权重|字号|配色|加粗] 台词文本 | 音频文件名
        /// </summary>
        public static LineSet ParseText(string text, LineSet baseSet)
        {
            LineSet result = baseSet != null ? baseSet : new LineSet();
            string current = "click";
            string[] rawLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string raw in rawLines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#") || line.StartsWith("//")) continue;
                if (line.StartsWith("=") || line.StartsWith("【") || line.StartsWith("——")) continue;

                if (line.StartsWith("@"))
                {
                    string sec = MapSection(line.Substring(1).Trim());
                    if (sec != null) { current = sec; continue; }
                    continue; // 未知 @ 指令忽略（例如插件的 @image / @link）
                }

                LineItem item = ParseLine(line);
                if (item == null || item.text.Length == 0) continue;
                List<LineItem> target = result.Section(current);
                if (target == null) target = result.click;
                target.Add(item);
            }
            return result;
        }

        public static LineSet ParseTextFile(string file, LineSet baseSet)
        {
            try
            {
                if (!File.Exists(file)) return baseSet;
                string text = File.ReadAllText(file, Encoding.UTF8);
                if (text.IndexOf('\uFFFD') >= 0)
                {
                    // 编码不是 UTF-8 时退化为 GBK 读取（老台词文件的常见情况）
                    try { text = File.ReadAllText(file, Encoding.GetEncoding("GB18030")); } catch { }
                }
                return ParseText(text, baseSet);
            }
            catch (Exception ex)
            {
                Log.Error("解析台词文件失败：" + file, ex);
                return baseSet;
            }
        }

        public static string MapSection(string name)
        {
            switch (name)
            {
                case "问候": case "greet": case "hello": case "开场": return "greet";
                case "点击": case "click": case "随机": case "random": case "互动": return "click";
                case "主动": case "proactive": case "主动对话": return "proactive";
                case "待机": case "idle": case "挂机": return "idle";
                case "系统": case "system": case "事件": return "system";
                default: return null;
            }
        }

        /// <summary>解析单条台词行：[权重|字号|配色|加粗] 文本 | 音频</summary>
        public static LineItem ParseLine(string line)
        {
            LineItem item = new LineItem();
            string rest = line;

            if (rest.StartsWith("["))
            {
                int end = rest.IndexOf(']');
                if (end > 0)
                {
                    string meta = rest.Substring(1, end - 1);
                    rest = rest.Substring(end + 1).Trim();
                    string[] parts = meta.Split('|');
                    for (int i = 0; i < parts.Length; i++)
                    {
                        string p = parts[i].Trim();
                        if (p.Length == 0) continue;
                        if (p == "bold" || p == "加粗" || p == "b") { item.bold = true; continue; }
                        double num;
                        if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out num))
                        {
                            if (i == 0) item.weight = num;
                            else if (i == 1) item.size = (int)Math.Round(num);
                            else if (i == 2) item.color = "";
                            continue;
                        }
                        if (i == 2) item.color = p;
                        else item.color = p;
                    }
                }
            }

            // 「台词 | 音频」形式
            int bar = rest.LastIndexOf('|');
            if (bar > 0)
            {
                string tail = rest.Substring(bar + 1).Trim();
                if (tail.Length > 0 && (tail.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || tail.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                    || tail.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) || tail.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)))
                {
                    item.voice = tail;
                    rest = rest.Substring(0, bar).Trim();
                }
            }

            // 去掉包裹的引号/书名号
            rest = rest.Trim();
            if (rest.Length >= 2)
            {
                char a = rest[0], b = rest[rest.Length - 1];
                if ((a == '"' && b == '"') || (a == '「' && b == '」') || (a == '“' && b == '”')) rest = rest.Substring(1, rest.Length - 2);
            }
            item.text = rest.Trim();
            if (item.text.Length == 0) return null;
            return item;
        }

        /// <summary>导出为 txt（与导入格式一致，可再次导入）。</summary>
        public static string ToText(CharacterProfile p)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("# ").Append(p.DisplayName()).Append(" · 台词（灵工桌宠）").Append(Environment.NewLine);
            sb.Append("# 段落：@问候（打开桌宠） / @点击（点击互动） / @主动（主动对话） / @待机（长时间没互动） / @系统（{app} 为前台程序名）").Append(Environment.NewLine);
            sb.Append("# 行首可选 [权重|字号|配色|加粗]，例：[10|22|gold|bold] 台词… | 音频文件.mp3").Append(Environment.NewLine);
            foreach (string sec in LineSet.SectionNames())
            {
                sb.Append(Environment.NewLine).Append("@").Append(SectionTag(sec)).Append(Environment.NewLine);
                foreach (LineItem it in p.lines.Section(sec))
                {
                    sb.Append("[")
                      .Append(it.weight.ToString("0.##", CultureInfo.InvariantCulture)).Append("|")
                      .Append(it.size.ToString(CultureInfo.InvariantCulture)).Append("|")
                      .Append(it.color).Append(it.bold ? "|bold" : "")
                      .Append("] ").Append(it.text);
                    if (!string.IsNullOrEmpty(it.voice)) sb.Append(" | ").Append(it.voice);
                    sb.Append(Environment.NewLine);
                }
            }
            return sb.ToString();
        }

        public static string SectionTag(string sec)
        {
            if (sec == "greet") return "问候";
            if (sec == "click") return "点击";
            if (sec == "proactive") return "主动";
            if (sec == "idle") return "待机";
            if (sec == "system") return "系统";
            return sec;
        }

        /// <summary>导入插件格式的 JSON 台词包（{name, random:[{t,w,size,bold,rgb}]}）。</summary>
        public static LineSet ParsePluginJson(string text, LineSet baseSet)
        {
            LineSet result = baseSet != null ? baseSet : new LineSet();
            Dictionary<string, object> root = Json.AsObj(Json.TryParse(text));
            if (root == null) return result;

            string[][] map = new string[][]
            {
                new string[] { "random", "click" },
                new string[] { "greet", "greet" },
                new string[] { "click", "click" },
                new string[] { "proactive", "proactive" },
                new string[] { "idle", "idle" },
                new string[] { "system", "system" },
                new string[] { "alert", "proactive" },
                new string[] { "budget", "proactive" },
                new string[] { "turnCost", "click" }
            };
            foreach (string[] pair in map)
            {
                object node;
                if (!root.TryGetValue(pair[0], out node)) continue;
                List<object> arr = Json.AsArr(node);
                if (arr == null) continue;
                List<LineItem> target = result.Section(pair[1]);
                foreach (object o in arr)
                {
                    Dictionary<string, object> d = Json.AsObj(o);
                    if (d == null)
                    {
                        string plain = Convert.ToString(o, CultureInfo.InvariantCulture);
                        if (!string.IsNullOrEmpty(plain)) target.Add(new LineItem(plain));
                        continue;
                    }
                    LineItem it = new LineItem();
                    it.text = Json.S(d, "t", Json.S(d, "text", ""));
                    if (it.text.Length == 0) continue;
                    it.weight = Json.N(d, "w", Json.N(d, "weight", 3));
                    it.size = Json.I(d, "size", 0);
                    it.bold = Json.B(d, "bold", false);
                    it.color = Json.S(d, "rgb", Json.S(d, "color", ""));
                    target.Add(it);
                }
            }
            return result;
        }

        /// <summary>智能导入：自动识别 txt / json（含插件格式）。</summary>
        public static LineSet ImportFile(string file, LineSet baseSet, out string report)
        {
            report = "";
            try
            {
                string text = File.ReadAllText(file, Encoding.UTF8);
                if (text.IndexOf('\uFFFD') >= 0)
                {
                    try { text = File.ReadAllText(file, Encoding.GetEncoding("GB18030")); } catch { }
                }
                string ext = Path.GetExtension(file).ToLowerInvariant();
                LineSet set;
                if (ext == ".json")
                {
                    set = ParsePluginJson(text, baseSet != null ? baseSet : new LineSet());
                    if (set.Count() == 0) set = ParseText(text, baseSet);
                }
                else
                {
                    set = ParseText(text, baseSet != null ? baseSet : new LineSet());
                }
                report = "导入成功：问候 " + set.greet.Count + " 条 / 点击 " + set.click.Count + " 条 / 主动 " +
                         set.proactive.Count + " 条 / 待机 " + set.idle.Count + " 条 / 系统 " + set.system.Count + " 条";
                return set;
            }
            catch (Exception ex)
            {
                report = "导入失败：" + ex.Message;
                Log.Error("导入台词失败：" + file, ex);
                return baseSet;
            }
        }
    }

    // ========================================================================
    // 语音清单
    // ========================================================================
    public static class VoiceBankLoader
    {
        public class Report
        {
            public int entries;
            public int matched;
            public int missingFiles;
            public int unmatchedLines;
            public string text = "";
        }

        /// <summary>
        /// 载入语音目录：优先读清单（manifest），没有清单则按「文件名 = 台词文本」自动匹配。
        /// 需求 3 明确要求提示用户「音频应当有对应的清单」，这里把提示与校验结果写进 Report。
        /// </summary>
        public static Report Load(CharacterProfile p, string manifestFile)
        {
            Report r = new Report();
            try
            {
                string dir = CharacterStore.VoicesDir(p.id);
                if (!Directory.Exists(dir)) { r.text = "未导入语音。"; return r; }

                List<string> audio = new List<string>();
                foreach (string f in Directory.GetFiles(dir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".mp3" || ext == ".wav" || ext == ".ogg" || ext == ".m4a" || ext == ".wma" || ext == ".flac")
                        audio.Add(Path.GetFileName(f));
                }

                string manifestPath = "";
                if (!string.IsNullOrEmpty(manifestFile))
                {
                    manifestPath = Path.Combine(dir, manifestFile);
                    if (!File.Exists(manifestPath)) manifestPath = "";
                }
                if (manifestPath.Length == 0)
                {
                    foreach (string cand in new string[] { "manifest.txt", "清单.txt", "语音清单.txt", "manifest.csv", "manifest.json" })
                    {
                        string path = Path.Combine(dir, cand);
                        if (File.Exists(path)) { manifestPath = path; break; }
                    }
                }

                VoiceBank bank = new VoiceBank();
                if (manifestPath.Length > 0)
                {
                    bank.manifest = Path.GetFileName(manifestPath);
                    string ext = Path.GetExtension(manifestPath).ToLowerInvariant();
                    string content = File.ReadAllText(manifestPath, Encoding.UTF8);
                    if (ext == ".json")
                    {
                        object node = Json.TryParse(content);
                        List<object> arr = Json.AsArr(node);
                        Dictionary<string, object> obj = Json.AsObj(node);
                        if (arr != null)
                        {
                            foreach (object o in arr)
                            {
                                Dictionary<string, object> d = Json.AsObj(o);
                                if (d == null) continue;
                                VoiceEntry e = new VoiceEntry();
                                e.text = Json.S(d, "text", Json.S(d, "t", ""));
                                e.file = Json.S(d, "file", Json.S(d, "audio", ""));
                                if (e.file.Length > 0) bank.entries.Add(e);
                            }
                        }
                        else if (obj != null)
                        {
                            foreach (KeyValuePair<string, object> kv in obj)
                            {
                                VoiceEntry e = new VoiceEntry();
                                e.text = kv.Key;
                                e.file = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
                                if (e.file.Length > 0) bank.entries.Add(e);
                            }
                        }
                    }
                    else
                    {
                        foreach (string raw in content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                        {
                            string line = raw.Trim();
                            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                            char sep = line.IndexOf('\t') >= 0 ? '\t' : (line.IndexOf('|') >= 0 ? '|' : ',');
                            int idx = line.IndexOf(sep);
                            string a, b;
                            if (idx <= 0)
                            {
                                a = ""; b = line;
                            }
                            else
                            {
                                a = line.Substring(0, idx).Trim().Trim('"');
                                b = line.Substring(idx + 1).Trim().Trim('"');
                            }
                            if (b.Length == 0) continue;
                            VoiceEntry e = new VoiceEntry();
                            // 兼容「文件|文本」与「文本|文件」两种写法
                            if (IsAudio(b) && !IsAudio(a)) { e.text = a; e.file = b; }
                            else if (IsAudio(a)) { e.text = b; e.file = a; }
                            else { e.text = a; e.file = b; }
                            bank.entries.Add(e);
                        }
                    }
                }

                // 没有清单：用文件名当台词文本
                if (bank.entries.Count == 0)
                {
                    foreach (string f in audio)
                    {
                        VoiceEntry e = new VoiceEntry();
                        e.text = Path.GetFileNameWithoutExtension(f);
                        e.file = f;
                        bank.entries.Add(e);
                    }
                }

                // 校验：清单里的文件是否存在
                foreach (VoiceEntry e in bank.entries)
                {
                    string path = Path.Combine(dir, e.file);
                    if (!File.Exists(path)) r.missingFiles++;
                }
                bank.entries.RemoveAll(delegate (VoiceEntry e) { return !File.Exists(Path.Combine(dir, e.file)); });
                r.entries = bank.entries.Count;

                p.voices = bank;

                // 统计台词能对上多少条语音
                int totalLines = 0;
                foreach (string sec in LineSet.SectionNames())
                    foreach (LineItem it in p.lines.Section(sec))
                    {
                        totalLines++;
                        string f = bank.Find(it.text);
                        if (!string.IsNullOrEmpty(f)) r.matched++;
                        else r.unmatchedLines++;
                    }

                StringBuilder sb = new StringBuilder();
                sb.Append("语音清单：").Append(bank.manifest.Length > 0 ? bank.manifest : "（无清单，按文件名匹配）");
                sb.Append(" · 条目 ").Append(r.entries);
                sb.Append(" · 已匹配台词 ").Append(r.matched).Append("/").Append(totalLines);
                if (r.missingFiles > 0) sb.Append(" · 清单中 ").Append(r.missingFiles).Append(" 个音频文件缺失已忽略");
                if (bank.manifest.Length == 0 && audio.Count > 0)
                    sb.Append("\r\n提示：建议编写清单文件（每行「台词文本|音频文件名」），否则台词与音频容易串位。");
                r.text = sb.ToString();
                Log.Info("载入语音：角色 " + p.id + "，" + r.text.Replace("\r\n", " "));
                return r;
            }
            catch (Exception ex)
            {
                Log.Error("载入语音清单失败：" + p.id, ex);
                r.text = "载入语音失败：" + ex.Message;
                return r;
            }
        }

        public static bool IsAudio(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string e = Path.GetExtension(s).ToLowerInvariant();
            return e == ".mp3" || e == ".wav" || e == ".ogg" || e == ".m4a" || e == ".wma" || e == ".flac";
        }

        /// <summary>生成一份清单模板（把当前台词与已导入音频配对写入 manifest.txt）。</summary>
        public static string WriteManifestTemplate(CharacterProfile p, out string message)
        {
            message = "";
            try
            {
                string dir = CharacterStore.VoicesDir(p.id);
                Directory.CreateDirectory(dir);
                List<string> audio = new List<string>();
                foreach (string f in Directory.GetFiles(dir))
                    if (IsAudio(f)) audio.Add(Path.GetFileName(f));

                List<LineItem> all = new List<LineItem>();
                foreach (string sec in LineSet.SectionNames())
                    foreach (LineItem it in p.lines.Section(sec)) if (sec != "system") all.Add(it);

                StringBuilder sb = new StringBuilder();
                sb.Append("# 语音清单：每行「台词文本|音频文件名」").Append(Environment.NewLine);
                sb.Append("# 音频文件请放在本目录（voices）下；清单与台词文本完全一致时自动挂接。").Append(Environment.NewLine);
                sb.Append("# 也可以写「音频文件名|台词文本」，或写成 manifest.json。").Append(Environment.NewLine);

                int used = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    string file = i < audio.Count ? audio[i] : "";
                    if (file.Length > 0) used++;
                    sb.Append(all[i].text).Append("|").Append(file).Append(Environment.NewLine);
                }
                // 多余的音频补在后面
                for (int i = all.Count; i < audio.Count; i++)
                {
                    sb.Append(Path.GetFileNameWithoutExtension(audio[i])).Append("|").Append(audio[i]).Append(Environment.NewLine);
                }

                string path = Path.Combine(dir, "manifest.txt");
                Utils.WriteAllTextAtomic(path, sb.ToString());
                message = "已生成清单模板：\r\n" + path + "\r\n\r\n共 " + all.Count + " 条台词、" + audio.Count + " 个音频文件，已按顺序预填 " + used + " 条。\r\n请核对后保存（可能需要手工调整配对）。";
                return path;
            }
            catch (Exception ex)
            {
                Log.ErrorDialog("生成语音清单模板失败", ex);
                message = "生成失败：" + ex.Message;
                return "";
            }
        }
    }
}
