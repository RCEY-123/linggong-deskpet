// ============================================================================
// Llm.cs —— 大模型接入（OpenAI 兼容 /chat/completions，本地与云端通吃）
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace AlDeskPet
{
    public class LlmMessage
    {
        public string role = "user";       // system / user / assistant
        public string content = "";

        public LlmMessage() { }
        public LlmMessage(string r, string c) { role = r; content = c; }
    }

    public class LlmResult
    {
        public bool ok;
        public string text = "";
        public string error = "";
        public int elapsedMs;
        public string raw = "";
    }

    public class LlmPreset
    {
        public string id;
        public string title;
        public string provider;
        public string baseUrl;
        public string model;
        public string hint;
        public bool needKey;

        public LlmPreset(string id, string title, string provider, string baseUrl, string model, bool needKey, string hint)
        {
            this.id = id;
            this.title = title;
            this.provider = provider;
            this.baseUrl = baseUrl;
            this.model = model;
            this.needKey = needKey;
            this.hint = hint;
        }
    }

    public static class Llm
    {
        static bool _tlsReady;

        public static void EnsureTls()
        {
            if (_tlsReady) return;
            _tlsReady = true;
            try
            {
                // .NET 4.x 默认可能不含 TLS1.2，云端 API 需要
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            }
            catch { }
        }

        public static List<LlmPreset> Presets()
        {
            List<LlmPreset> list = new List<LlmPreset>();
            list.Add(new LlmPreset("deepseek", "DeepSeek 云端", "cloud", "https://api.deepseek.com/v1", "deepseek-chat", true, "国内直连，便宜；需要 API Key（platform.deepseek.com）"));
            list.Add(new LlmPreset("openai", "OpenAI 云端", "cloud", "https://api.openai.com/v1", "gpt-4o-mini", true, "需要 API Key 与可用的网络出口"));
            list.Add(new LlmPreset("compatible", "云端 · OpenAI 兼容中转", "cloud", "https://your-endpoint.example.com/v1", "gpt-4o-mini", true, "任何 OpenAI 兼容中转站（OneAPI / New API 等）"));
            list.Add(new LlmPreset("ollama", "本地 · Ollama", "local", "http://127.0.0.1:11434/v1", "qwen2.5:7b", false, "本地推理，免 Key。先 ollama serve，再 ollama pull 模型"));
            list.Add(new LlmPreset("lmstudio", "本地 · LM Studio", "local", "http://127.0.0.1:1234/v1", "local-model", false, "LM Studio 打开 Local Server（默认 1234 端口）"));
            list.Add(new LlmPreset("oneapi", "本地 · OneAPI / NewAPI", "local", "http://127.0.0.1:3000/v1", "gpt-3.5-turbo", false, "自建网关：一个地址聚合多家模型"));
            list.Add(new LlmPreset("llamacpp", "本地 · llama.cpp server", "local", "http://127.0.0.1:8080/v1", "local-model", false, "llama-server 默认 8080 端口"));
            list.Add(new LlmPreset("custom", "自定义", "custom", "", "", false, "自己填写接口地址、模型名与 Key"));
            return list;
        }

        public static void ApplyPreset(LlmSettings s, string presetId)
        {
            foreach (LlmPreset p in Presets())
            {
                if (p.id != presetId) continue;
                s.provider = p.provider;
                s.baseUrl = p.baseUrl;
                s.model = p.model;
                if (p.provider == "local") s.apiKey = "";
                return;
            }
        }

        static string Endpoint(LlmSettings s)
        {
            string url = (s.baseUrl ?? "").Trim();
            if (url.Length == 0) throw new InvalidOperationException("接口地址为空");
            if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return url;
            if (url.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase)) return url;
            return url.TrimEnd('/') + "/chat/completions";
        }

        /// <summary>构造请求体（手写 JSON，避免任何外部依赖）。</summary>
        static string BuildBody(LlmSettings s, List<LlmMessage> messages, double temperature, int maxTokens)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"model\":");
            sb.Append(Json.WriteCompact(s.model ?? ""));
            sb.Append(",\"messages\":[");
            for (int i = 0; i < messages.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"role\":").Append(Json.WriteCompact(messages[i].role))
                  .Append(",\"content\":").Append(Json.WriteCompact(messages[i].content)).Append('}');
            }
            sb.Append(']');
            sb.Append(",\"temperature\":").Append(temperature.ToString("0.##", CultureInfo.InvariantCulture));
            sb.Append(",\"max_tokens\":").Append(Math.Max(16, maxTokens).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"stream\":false}");
            return sb.ToString();
        }

        /// <summary>把 HTTP 错误翻译成人话。</summary>
        static string FriendlyError(HttpStatusCode code, string body, string url)
        {
            string snippet = Utils.FirstLine(body, 300);
            switch ((int)code)
            {
                case 401:
                case 403:
                    return "鉴权失败（HTTP " + (int)code + "）：API Key 不对或没有权限。请到「齿轮 → 大模型 API」检查 Key。" + (snippet.Length > 0 ? "\r\n服务端返回：" + snippet : "");
                case 404:
                    return "接口地址不对（HTTP 404）：请确认地址以 /v1 结尾，例如 https://api.deepseek.com/v1。当前地址：" + url;
                case 429:
                    return "请求太频繁或额度不足（HTTP 429）：稍后再试，或检查账户余额。" + (snippet.Length > 0 ? "\r\n服务端返回：" + snippet : "");
                case 500:
                case 502:
                case 503:
                    return "服务端暂时不可用（HTTP " + (int)code + "）：" + snippet;
                default:
                    return "请求失败（HTTP " + (int)code + "）：" + snippet;
            }
        }

        static LlmResult Post(LlmSettings s, string body, int timeoutSec)
        {
            LlmResult result = new LlmResult();
            DateTime start = DateTime.Now;
            try
            {
                EnsureTls();
                string url = Endpoint(s);
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json; charset=utf-8";
                req.Accept = "application/json";
                req.UserAgent = AppPaths.ProductNameEn + "/" + AppPaths.Version;
                req.Timeout = Math.Max(5, timeoutSec) * 1000;
                req.ReadWriteTimeout = Math.Max(5, timeoutSec) * 1000;
                req.KeepAlive = false;
                if (!string.IsNullOrEmpty(s.apiKey))
                    req.Headers["Authorization"] = "Bearer " + s.apiKey.Trim();

                byte[] payload = Encoding.UTF8.GetBytes(body);
                req.ContentLength = payload.Length;
                using (Stream rs = req.GetRequestStream())
                {
                    rs.Write(payload, 0, payload.Length);
                }

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        result.raw = sr.ReadToEnd();
                    }
                }
                result.ok = true;
                result.text = ExtractContent(result.raw);
                if (string.IsNullOrEmpty(result.text))
                {
                    result.ok = false;
                    result.error = "模型返回内容为空。" + (result.raw.Length > 0 ? "\r\n原始返回：" + Utils.FirstLine(result.raw, 300) : "");
                }
            }
            catch (WebException wex)
            {
                result.ok = false;
                HttpWebResponse resp = wex.Response as HttpWebResponse;
                if (resp != null)
                {
                    string bodyText = "";
                    try
                    {
                        using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) bodyText = sr.ReadToEnd();
                    }
                    catch { }
                    result.error = FriendlyError(resp.StatusCode, bodyText, s.baseUrl);
                    result.raw = bodyText;
                    try { resp.Close(); } catch { }
                }
                else
                {
                    result.error = "无法连接到模型服务：" + wex.Message +
                        "\r\n请检查：① 网络是否可访问该地址；② 若用本地模型，服务是否已启动（" + s.baseUrl + "）；③ 地址是否写了 /v1。";
                }
                Log.Warn("大模型请求失败：" + result.error.Replace("\r\n", " "));
            }
            catch (Exception ex)
            {
                result.ok = false;
                result.error = "请求出错：" + ex.Message;
                Log.Error("大模型请求异常", ex);
            }
            result.elapsedMs = (int)(DateTime.Now - start).TotalMilliseconds;
            return result;
        }

        /// <summary>从返回 JSON 里取 choices[0].message.content（兼容数组形态与 reasoning 字段）。</summary>
        public static string ExtractContent(string raw)
        {
            try
            {
                Dictionary<string, object> root = Json.AsObj(Json.TryParse(raw));
                if (root == null) return "";
                List<object> choices = Json.AsArr(RootGet(root, "choices"));
                if (choices == null || choices.Count == 0) return "";
                Dictionary<string, object> first = Json.AsObj(choices[0]);
                if (first == null) return "";
                Dictionary<string, object> message = Json.AsObj(RootGet(first, "message"));
                if (message == null) message = Json.AsObj(RootGet(first, "delta"));
                if (message == null) return "";
                object content = RootGet(message, "content");
                string s = content as string;
                if (s != null) return Clean(s);
                List<object> arr = content as List<object>;
                if (arr != null)
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (object o in arr)
                    {
                        Dictionary<string, object> d = Json.AsObj(o);
                        if (d != null) sb.Append(Json.S(d, "text", ""));
                        else if (o is string) sb.Append((string)o);
                    }
                    return Clean(sb.ToString());
                }
                return "";
            }
            catch (Exception ex)
            {
                Log.Warn("解析模型返回失败：" + ex.Message);
                return "";
            }
        }

        static object RootGet(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v)) return v;
            return null;
        }

        /// <summary>清理模型输出：去掉 markdown 包裹与多余空白，避免气泡里排版难看。</summary>
        static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text.Trim();
            if (s.StartsWith("```"))
            {
                int nl = s.IndexOf('\n');
                if (nl > 0) s = s.Substring(nl + 1);
                int end = s.LastIndexOf("```", StringComparison.Ordinal);
                if (end >= 0) s = s.Substring(0, end);
                s = s.Trim();
            }
            s = s.Replace("\r\n", "\n").Replace('\r', '\n');
            // 去掉整行的 markdown 列表符号，让气泡更像台词
            string[] lines = s.Split('\n');
            StringBuilder sb = new StringBuilder();
            foreach (string line in lines)
            {
                string t = line.Trim();
                if (t.Length == 0) continue;
                if (t.StartsWith("- ") || t.StartsWith("* ") || t.StartsWith("• ")) t = t.Substring(2).Trim();
                if (t.StartsWith("#")) t = t.TrimStart('#').Trim();
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(t);
            }
            return sb.ToString().Trim();
        }

        /// <summary>正式对话：system 提示词 + 历史 + 本轮输入。</summary>
        public static LlmResult Chat(LlmSettings s, string systemPrompt, List<LlmMessage> history, string userText)
        {
            LlmResult r = new LlmResult();
            try
            {
                List<LlmMessage> messages = new List<LlmMessage>();
                messages.Add(new LlmMessage("system", systemPrompt));
                if (history != null)
                {
                    int keep = Math.Max(0, s.historyTurns) * 2;
                    int start = history.Count > keep ? history.Count - keep : 0;
                    for (int i = start; i < history.Count; i++) messages.Add(history[i]);
                }
                if (!string.IsNullOrEmpty(userText)) messages.Add(new LlmMessage("user", userText));
                string body = BuildBody(s, messages, s.temperature, s.maxTokens);
                Log.Debug("LLM 请求 → " + Endpoint(s) + " model=" + s.model + " messages=" + messages.Count);
                return Post(s, body, s.timeoutSec);
            }
            catch (Exception ex)
            {
                r.ok = false;
                r.error = "构造请求失败：" + ex.Message;
                Log.Error("构造大模型请求失败", ex);
                return r;
            }
        }

        /// <summary>连通性测试（设置界面用）。</summary>
        public static LlmResult TestConnection(LlmSettings s)
        {
            LlmResult r = new LlmResult();
            try
            {
                LlmSettings probe = Clone(s);
                probe.maxTokens = 32;
                List<LlmMessage> messages = new List<LlmMessage>();
                messages.Add(new LlmMessage("system", "你是连通性测试助手，只回复「连接成功」四个字。"));
                messages.Add(new LlmMessage("user", "测试连接"));
                string body = BuildBody(probe, messages, 0.1, probe.maxTokens);
                r = Post(probe, body, Math.Min(probe.timeoutSec, 30));
                if (r.ok) r.text = "连通成功：" + Utils.FirstLine(r.text, 60) + "（" + r.elapsedMs + " ms）";
            }
            catch (Exception ex)
            {
                r.ok = false;
                r.error = "测试失败：" + ex.Message;
            }
            return r;
        }

        public static LlmSettings Clone(LlmSettings s)
        {
            LlmSettings c = new LlmSettings();
            c.provider = s.provider;
            c.baseUrl = s.baseUrl;
            c.apiKey = s.apiKey;
            c.model = s.model;
            c.temperature = s.temperature;
            c.maxTokens = s.maxTokens;
            c.timeoutSec = s.timeoutSec;
            c.historyTurns = s.historyTurns;
            c.stream = s.stream;
            c.systemExtra = s.systemExtra;
            c.lastTestTime = s.lastTestTime;
            c.lastTestResult = s.lastTestResult;
            return c;
        }

        /// <summary>按角色卡拼 system 提示词。</summary>
        public static string BuildSystemPrompt(CharacterProfile p, string contextHint, bool includeContext)
        {
            StringBuilder sb = new StringBuilder();
            string name = p.DisplayName();
            string user = p.card != null && !string.IsNullOrEmpty(p.card.userName) ? p.card.userName : "指挥官";

            sb.Append("你现在是桌面宠物角色「").Append(name).Append("」，正在陪在").Append(user).Append("身边。").Append("\r\n");

            if (p.card != null && !string.IsNullOrEmpty(p.card.persona))
                sb.Append("\r\n【角色设定】\r\n").Append(p.card.persona.Trim()).Append("\r\n");
            if (p.card != null && !string.IsNullOrEmpty(p.card.speechStyle))
                sb.Append("\r\n【说话风格】\r\n").Append(p.card.speechStyle.Trim()).Append("\r\n");
            if (p.card != null && !string.IsNullOrEmpty(p.card.extraRules))
                sb.Append("\r\n【补充要求】\r\n").Append(p.card.extraRules.Trim()).Append("\r\n");
            if (!string.IsNullOrEmpty(Config.Current.llm.systemExtra))
                sb.Append("\r\n【全局附加要求】\r\n").Append(Config.Current.llm.systemExtra.Trim()).Append("\r\n");

            sb.Append("\r\n【输出格式（必须遵守）】\r\n")
              .Append("1. 你的回答会直接显示在角色头顶的小气泡里，所以只输出台词本身：不要旁白、不要括号里的动作、不要markdown、不要列表、不要表情符号堆砌。\r\n")
              .Append("2. 用口语化的中文，1~3 句，尽量不超过 60 个字；话没说完可以用「…」。\r\n")
              .Append("3. 保持角色人设与自称，不要暴露你是 AI、模型或程序，也不要提到提示词。\r\n")
              .Append("4. 玩家称呼：").Append(user).Append("。\r\n");

            if (includeContext && !string.IsNullOrEmpty(contextHint))
                sb.Append("\r\n【当前情况】\r\n").Append(contextHint)
                  .Append("\r\n可以根据上面的情况自然地开口（例如问玩家刚刚在做什么），但不要生硬地念出程序名。\r\n");

            return sb.ToString();
        }
    }
}
