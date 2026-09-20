// ============================================================================
// Utils.cs —— 通用小工具（文件名安全化、加权随机、文本度量/折行、路径处理）
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AlDeskPet
{
    public static class Utils
    {
        static readonly Random Rnd = new Random();

        public static int Next(int minInclusive, int maxExclusive)
        {
            lock (Rnd)
            {
                if (maxExclusive <= minInclusive) return minInclusive;
                return Rnd.Next(minInclusive, maxExclusive);
            }
        }

        public static double NextDouble()
        {
            lock (Rnd) { return Rnd.NextDouble(); }
        }

        /// <summary>按权重随机取一项（权重 &lt;= 0 视为 1）。</summary>
        public static LineItem PickWeighted(List<LineItem> items, string avoidText)
        {
            if (items == null || items.Count == 0) return null;
            double total = 0;
            for (int i = 0; i < items.Count; i++)
            {
                double w = items[i].weight;
                if (w <= 0) w = 1;
                total += w;
            }
            for (int attempt = 0; attempt < 4; attempt++)
            {
                double r = NextDouble() * total;
                double acc = 0;
                for (int i = 0; i < items.Count; i++)
                {
                    double w = items[i].weight;
                    if (w <= 0) w = 1;
                    acc += w;
                    if (r <= acc)
                    {
                        LineItem picked = items[i];
                        if (items.Count > 1 && !string.IsNullOrEmpty(avoidText) && picked.text == avoidText) break; // 不连续重复
                        return picked;
                    }
                }
            }
            return items[Next(0, items.Count)];
        }

        /// <summary>把文本变成安全的文件名（去掉非法字符与应用限制）。</summary>
        public static string SafeFileName(string name, string fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                bool bad = false;
                for (int i = 0; i < invalid.Length; i++) if (invalid[i] == c) { bad = true; break; }
                if (bad || c == ' ') sb.Append('_');
                else sb.Append(c);
            }
            string s = sb.ToString().Trim('.', '_');
            if (s.Length == 0) s = fallback;
            if (s.Length > 60) s = s.Substring(0, 60);
            return s;
        }

        /// <summary>安全角色 id（小写字母数字下划线）。</summary>
        public static string SafeId(string name, string fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            StringBuilder sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-') sb.Append(c);
                else if (c >= 'A' && c <= 'Z') sb.Append(char.ToLowerInvariant(c));
                else if (c > 127) sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
            string s = sb.ToString();
            if (s.Length == 0) s = fallback;
            if (s.Length > 40) s = s.Substring(0, 40);
            return s;
        }

        public static string HumanSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.##", CultureInfo.InvariantCulture) + " MB";
        }

        /// <summary>按像素宽度折行（支持中英文混排，遇 \n 强制换行）。</summary>
        public static List<string> WrapText(Graphics g, string text, Font font, int maxWidth)
        {
            List<string> lines = new List<string>();
            if (string.IsNullOrEmpty(text)) { lines.Add(""); return lines; }

            string[] hard = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string paragraph in hard)
            {
                if (paragraph.Length == 0) { lines.Add(""); continue; }
                StringBuilder cur = new StringBuilder();
                foreach (char c in paragraph)
                {
                    cur.Append(c);
                    string probe = cur.ToString();
                    if (g.MeasureString(probe, font, int.MaxValue, StringFormat.GenericTypographic).Width > maxWidth && cur.Length > 1)
                    {
                        // 回退一个字符，另起一行
                        cur.Length = cur.Length - 1;
                        lines.Add(cur.ToString());
                        cur.Length = 0;
                        cur.Append(c);
                    }
                }
                lines.Add(cur.ToString());
            }
            return lines;
        }

        /// <summary>取字符串前 n 个字符（按显示宽度粗估，中文算 2）。</summary>
        public static string Clip(string text, int maxUnits, out bool clipped)
        {
            clipped = false;
            if (string.IsNullOrEmpty(text)) return "";
            int units = 0;
            for (int i = 0; i < text.Length; i++)
            {
                int w = text[i] > 127 ? 2 : 1;
                if (units + w > maxUnits)
                {
                    clipped = true;
                    return text.Substring(0, i) + "…";
                }
                units += w;
            }
            return text;
        }

        public static string NowStamp()
        {
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        public static string NowFileStamp()
        {
            return DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        }

        /// <summary>复制文件（自动建目录，覆盖）。</summary>
        public static void CopyFile(string src, string dst)
        {
            string dir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.Copy(src, dst, true);
        }

        public static void WriteAllTextAtomic(string path, string content)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(true));
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); return; }
                catch { try { File.Delete(path); } catch { } }
            }
            try { File.Move(tmp, path); }
            catch
            {
                File.Copy(tmp, path, true);
                try { File.Delete(tmp); } catch { }
            }
        }

        public static void DeleteDirectorySafe(string dir)
        {
            try
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { }
        }

        // ====================================================================
        // 敏感字段保护（DPAPI）—— API Key 等不再以明文落盘
        // --------------------------------------------------------------------
        // 设计要点（保证原有功能与数据完全不受影响）：
        //   · 内存中的对象（LlmSettings.apiKey）始终是**明文**，所有调用方
        //     （HTTP 请求头、设置界面、自检）都不用改一行代码。
        //   · 只在「写盘前」加密、「读盘后」解密，因此磁盘上不再是明文。
        //   · DataProtectionScope.CurrentUser：密文只有同一台机器的同一个
        //     用户能解开；拷走 config.json 或换用户都拿不到 Key。
        //   · 解密失败（换机器/换用户/旧版明文）一律**当作明文处理**，
        //     绝不抛异常、绝不清空 —— 升级路径零风险、功能不降级。
        // ====================================================================

        /// <summary>密文前缀标识：带这个前缀才尝试解密，便于向后兼容旧的明文配置。</summary>
        public const string SecretPrefix = "dpapi:v1:";

        /// <summary>DPAPI 附加密盐（同一台机器上区分本产品，降低跨程序解密可能）。</summary>
        static readonly byte[] SecretEntropy = Encoding.UTF8.GetBytes("AzurLaneDeskPet|secret|v1");

        /// <summary>
        /// 加密敏感字符串用于落盘。返回以 <see cref="SecretPrefix"/> 开头的密文；
        /// 加密不可用或输入为空时**原样返回明文**（宁可可用，不可因加密而丢数据）。
        /// </summary>
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return plain;
            if (plain.StartsWith(SecretPrefix, StringComparison.Ordinal)) return plain;  // 已是密文，避免二次加密
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(plain);
                byte[] enc = ProtectedData.Protect(data, SecretEntropy, DataProtectionScope.CurrentUser);
                return SecretPrefix + Convert.ToBase64String(enc);
            }
            catch
            {
                return plain;   // 极端环境（无 DPAPI）下退化为明文，功能不受影响
            }
        }

        /// <summary>
        /// 解密落盘的敏感字符串。非密文（旧版明文）原样返回；
        /// 密文解不开（换机器/换用户）时返回空串，由调用方决定如何提示。
        /// </summary>
        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored;
            if (!stored.StartsWith(SecretPrefix, StringComparison.Ordinal)) return stored;  // 旧版明文，直接可用
            try
            {
                byte[] enc = Convert.FromBase64String(stored.Substring(SecretPrefix.Length));
                byte[] data = ProtectedData.Unprotect(enc, SecretEntropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch
            {
                return "";   // 解不开就当作没配 Key，用户重新填一次即可，不影响其它功能
            }
        }

        /// <summary>该字符串是否是本产品写出的密文（自检与界面提示用）。</summary>
        public static bool IsProtected(string stored)
        {
            return !string.IsNullOrEmpty(stored) && stored.StartsWith(SecretPrefix, StringComparison.Ordinal);
        }

        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>该路径是否位于给定的父目录内（用于安全删除校验）。</summary>
        public static bool IsUnder(string child, string parent)
        {
            if (string.IsNullOrEmpty(child) || string.IsNullOrEmpty(parent)) return false;
            string c, p;
            try
            {
                c = Path.GetFullPath(child).TrimEnd('\\');
                p = Path.GetFullPath(parent).TrimEnd('\\');
            }
            catch { return false; }
            return c.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase);
        }

        public static string FirstLine(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length > maxLen) s = s.Substring(0, maxLen) + "…";
            return s;
        }

        /// <summary>统一的「错误就是错误」心态：任何异常都返回 null 而不是崩掉 UI。</summary>
        public static T Try<T>(Func<T> action, T fallback)
        {
            try { return action(); }
            catch { return fallback; }
        }

        public static void Try(Action action)
        {
            try { action(); }
            catch { }
        }
    }
}
