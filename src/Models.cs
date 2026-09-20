// ============================================================================
// Models.cs —— 数据模型（配置 / 角色 / 台词 / 语音 / 角色卡 / 互动 / 音效 / 大模型）
// ----------------------------------------------------------------------------
// 全部使用公开字段，便于 Json 反射序列化；默认值即「开箱可用」的状态。
// ============================================================================
using System;
using System.Collections.Generic;

namespace AlDeskPet
{
    /// <summary>一条台词。</summary>
    public class LineItem
    {
        public string text = "";
        public double weight = 3;      // 抽取权重
        public int size = 0;           // 字号（0 = 跟随全局）
        public string color = "";      // 配色名（gold / white / rouge / indigo / candy …）
        public bool bold = false;
        public string voice = "";      // 指定音频文件名（空 = 由语音清单决定）

        public LineItem() { }

        public LineItem(string t) { text = t; }

        public LineItem(string t, double w)
        {
            text = t;
            weight = w;
        }

        public override string ToString() { return text; }
    }

    /// <summary>按场景分段的台词集合。</summary>
    public class LineSet
    {
        public List<LineItem> greet = new List<LineItem>();       // 打开桌宠时的问候
        public List<LineItem> click = new List<LineItem>();       // 点击互动的随机台词
        public List<LineItem> proactive = new List<LineItem>();   // 主动对话
        public List<LineItem> idle = new List<LineItem>();        // 长时间无互动
        public List<LineItem> system = new List<LineItem>();      // 系统事件（可用 {app} {title} 占位）

        public int Count()
        {
            return greet.Count + click.Count + proactive.Count + idle.Count + system.Count;
        }

        public List<LineItem> Section(string name)
        {
            if (name == "greet") return greet;
            if (name == "click") return click;
            if (name == "proactive") return proactive;
            if (name == "idle") return idle;
            if (name == "system") return system;
            return null;
        }

        public static string[] SectionNames()
        {
            return new string[] { "greet", "click", "proactive", "idle", "system" };
        }

        public static string SectionTitle(string name)
        {
            if (name == "greet") return "问候（打开桌宠时）";
            if (name == "click") return "点击互动";
            if (name == "proactive") return "主动对话";
            if (name == "idle") return "待机（长时间无互动）";
            if (name == "system") return "系统事件（{app} / {title}）";
            return name;
        }
    }

    /// <summary>语音清单中的一条：台词文本 ↔ 音频文件。</summary>
    public class VoiceEntry
    {
        public string text = "";
        public string file = "";     // 相对角色 voices 目录（或绝对路径）
    }

    /// <summary>语音包（含清单）。</summary>
    public class VoiceBank
    {
        public List<VoiceEntry> entries = new List<VoiceEntry>();
        public string manifest = "";       // 清单文件名（相对角色 voices 目录）
        public string note = "";

        public string Find(string lineText)
        {
            if (string.IsNullOrEmpty(lineText)) return "";
            string key = Normalize(lineText);
            // 1) 完全相同
            for (int i = 0; i < entries.Count; i++)
                if (Normalize(entries[i].text) == key) return entries[i].file;
            // 2) 去掉书名号 / 引号 / 空白 / 省略号后相同
            for (int i = 0; i < entries.Count; i++)
            {
                string a = Loose(entries[i].text);
                if (a.Length > 0 && a == Loose(lineText)) return entries[i].file;
            }
            // 3) 前缀包含（台词被截断的情况）
            for (int i = 0; i < entries.Count; i++)
            {
                string a = Loose(entries[i].text);
                string b = Loose(lineText);
                if (a.Length >= 4 && b.Length >= 4 && (a.StartsWith(b) || b.StartsWith(a))) return entries[i].file;
            }
            return "";
        }

        static string Normalize(string s)
        {
            if (s == null) return "";
            return s.Trim();
        }

        static string Loose(string s)
        {
            if (s == null) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) continue;
                if (c == '「' || c == '」' || c == '『' || c == '』' || c == '"' || c == '\'' || c == '“' || c == '”') continue;
                if (c == '…' || c == '.' || c == '。' || c == '!' || c == '！' || c == '?' || c == '？' || c == '，' || c == ',' || c == '、' || c == '—' || c == '-' || c == '~' || c == '～') continue;
                sb.Append(c);
            }
            return sb.ToString();
        }
    }

    /// <summary>角色卡（仅在接入大模型时可用）。</summary>
    public class CharacterCard
    {
        public bool enabled = false;          // 是否在接入大模型时启用本角色卡
        public string persona = "";           // 角色设定（system prompt 主体）
        public string speechStyle = "";       // 说话风格补充
        public string greeting = "";          // 大模型模式下的开场白
        public string extraRules = "";        // 附加约束
        public double temperature = 0.85;
        public int maxTokens = 220;
        public string userName = "指挥官";     // 用户称呼
    }

    /// <summary>互动设置（每个角色独立）。</summary>
    public class InteractionSettings
    {
        public string mode = "fixed";          // fixed = 基础互动固定台词；llm = 大模型对话
        public bool proactive = false;         // 是否允许主动对话
        public int proactiveMinSec = 120;      // 主动对话间隔下限（秒）
        public int proactiveMaxSec = 300;      // 主动对话间隔上限（秒）
        public int idleMinSec = 600;           // 多久没互动触发「待机」台词
        public int bubbleSeconds = 6;          // 气泡自动关闭秒数（0 = 不自动关闭）
        public bool showInputBox = true;       // 大模型模式下显示输入框
        public bool useSystemContext = false;  // 使用系统事件/用户操作作为对话依据（需授权）
        public bool clickAnimation = true;     // 点击 Q 弹动画
        public bool showOnStart = true;        // 启动时显示
        public double scale = 1.0;             // 立绘缩放
        public int animDurationMs = 420;       // Q 弹动画时长
        public string fixedVoiceMode = "match"; // match = 台词有语音就播；off = 不播语音；always = 播随机语音
    }

    /// <summary>互动音效（每个角色独立）。</summary>
    public class SfxSettings
    {
        public bool enabled = true;
        public string press = "";       // 相对角色 sfx 目录或绝对路径（空 = 用内置预设）
        public string release = "";
        public string bubble = "";      // 气泡出现音
        public string preset = "fx1";   // 内置预设：fx1（音效1） / duck（小黄鸭） / none
    }

    /// <summary>一个完整角色。</summary>
    public class CharacterProfile
    {
        public string id = "";
        public string name = "";
        public string faction = "";           // 阵营（重樱 / 白鹰 / 皇家 …）
        public string shipType = "";          // 舰种
        public string note = "";              // 备注（作者 / 素材来源）
        public string builtinId = "";         // 来自哪个内置模板（空 = 用户自建 / 自 v1.0.6 前的旧角色）

        public List<string> images = new List<string>();   // 相对角色目录的图片文件名
        public int imageIndex = 0;

        public LineSet lines = new LineSet();
        public VoiceBank voices = new VoiceBank();
        public CharacterCard card = new CharacterCard();
        public InteractionSettings interact = new InteractionSettings();
        public SfxSettings sfx = new SfxSettings();

        public string createdAt = "";
        public string updatedAt = "";

        public string DisplayName()
        {
            if (!string.IsNullOrEmpty(name)) return name;
            return string.IsNullOrEmpty(id) ? "未命名角色" : id;
        }

        public string CurrentImage()
        {
            if (images == null || images.Count == 0) return "";
            int idx = imageIndex;
            if (idx < 0 || idx >= images.Count) idx = 0;
            return images[idx];
        }
    }

    /// <summary>大模型接入设置（兼容本地与云端：OpenAI 兼容 /chat/completions）。</summary>
    public class LlmSettings
    {
        public string provider = "cloud";                         // cloud / local / custom
        public string baseUrl = "https://api.deepseek.com/v1";
        public string apiKey = "";
        public string model = "deepseek-chat";
        public double temperature = 0.85;
        public int maxTokens = 220;
        public int timeoutSec = 60;
        public int historyTurns = 8;                              // 携带的历史轮数
        public bool stream = false;                               // 预留：流式（当前非流式实现）
        public string systemExtra = "";                           // 全局附加提示词
        public string lastTestTime = "";
        public string lastTestResult = "";                        // ok / 失败原因
    }

    /// <summary>全局设置。</summary>
    public class AppSettings
    {
        public int configVersion = 1;
        public string dataDir = "";               // 自定义用户数据目录（空 = %APPDATA%\AzurLaneDeskPet）
        public string activeCharacter = "";       // 当前选中的角色 id
        public List<string> characterOrder = new List<string>();
        public LlmSettings llm = new LlmSettings();

        public int volume = 80;                   // 语音/音效音量 0-100
        public bool sfxEnabled = true;
        public bool voiceEnabled = true;

        public string systemWatchPermission = "unasked";  // unasked / allowed / denied
        public bool firstRunDone = false;
        public int builtinSeed = 0;               // 内置角色"开箱即用"的一次性补齐标记（0 = 还没补过；老配置兼容）
        public string seededBuiltin = "";         // 已经向本数据目录提供过哪些内置模板 id（逗号分隔）—— 用户删掉的不再补，新版本新增的会补一次
        public bool autoStart = false;            // 开机自启
        public string petPosition = "";           // 桌宠位置 "x,y"

        // ---- 桌宠显示方式（详见 DisplayWatch.cs）----
        public string petDisplayMode = "always";   // always = 始终最上层 / focus = 焦点冻结 / fullscreen = 全屏隐藏
        public bool hideOnFullscreen = true;       // 任何模式下都生效：有别的程序全屏就把桌宠收进后台
        public bool hidePopupsWhenInactive = true; // 冻结 / 进后台时一并收起气泡与输入框

        public bool showTray = true;
        public bool logVerbose = false;
        public int logKeepDays = 14;
        public string lastVersion = "";
    }
}
