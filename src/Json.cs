// ============================================================================
// Json.cs —— 极简 JSON 读写（零依赖，C# 5 / .NET Framework 4.x）
// ----------------------------------------------------------------------------
// 不引入任何第三方库：自己实现 写入（反射遍历公开字段）+ 解析 + 对象绑定，
// 保证「灵工桌宠」单 exe 运行、不挑环境。
// ============================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace AlDeskPet
{
    /// <summary>JSON 序列化 / 反序列化工具。</summary>
    public static class Json
    {
        // ==================== 写入 ====================

        public static string Write(object value)
        {
            StringBuilder sb = new StringBuilder(512);
            WriteValue(sb, value, 0, true);
            return sb.ToString();
        }

        public static string WriteCompact(object value)
        {
            StringBuilder sb = new StringBuilder(256);
            WriteValue(sb, value, 0, false);
            return sb.ToString();
        }

        static void Indent(StringBuilder sb, int level)
        {
            sb.Append('\n');
            for (int i = 0; i < level; i++) sb.Append("  ");
        }

        static void WriteValue(StringBuilder sb, object v, int level, bool pretty)
        {
            if (v == null) { sb.Append("null"); return; }

            if (v is string) { WriteString(sb, (string)v); return; }
            if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }
            if (v is char) { WriteString(sb, v.ToString()); return; }
            if (v is Enum) { WriteString(sb, v.ToString()); return; }

            if (v is float) { sb.Append(((float)v).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (v is double) { sb.Append(((double)v).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (v is decimal) { sb.Append(((decimal)v).ToString(CultureInfo.InvariantCulture)); return; }
            if (v is byte || v is sbyte || v is short || v is ushort || v is int || v is uint || v is long || v is ulong)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }
            if (v is DateTime)
            {
                WriteString(sb, ((DateTime)v).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                return;
            }

            IDictionary dict = v as IDictionary;
            if (dict != null)
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    if (pretty) Indent(sb, level + 1);
                    WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                    sb.Append(pretty ? ": " : ":");
                    WriteValue(sb, e.Value, level + 1, pretty);
                }
                if (pretty) Indent(sb, level);
                sb.Append('}');
                return;
            }

            IEnumerable list = v as IEnumerable;
            if (list != null && !(v is string))
            {
                bool any = false;
                StringBuilder inner = new StringBuilder();
                foreach (object item in list)
                {
                    if (any) inner.Append(',');
                    any = true;
                    if (pretty) Indent(inner, level + 1);
                    WriteValue(inner, item, level + 1, pretty);
                }
                if (!any) { sb.Append("[]"); return; }
                sb.Append('[').Append(inner);
                if (pretty) Indent(sb, level);
                sb.Append(']');
                return;
            }

            // 普通对象：按公开字段写入
            Type t = v.GetType();
            FieldInfo[] fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
            sb.Append('{');
            bool firstF = true;
            for (int i = 0; i < fields.Length; i++)
            {
                object fv;
                try { fv = fields[i].GetValue(v); }
                catch { continue; }
                if (!firstF) sb.Append(',');
                firstF = false;
                if (pretty) Indent(sb, level + 1);
                WriteString(sb, fields[i].Name);
                sb.Append(pretty ? ": " : ":");
                WriteValue(sb, fv, level + 1, pretty);
            }
            if (pretty && !firstF) Indent(sb, level);
            sb.Append('}');
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ==================== 解析 ====================

        /// <summary>解析 JSON 文本；对象 → Dictionary&lt;string,object&gt;，数组 → List&lt;object&gt;。</summary>
        public static object Parse(string text)
        {
            if (text == null) return null;
            int i = 0;
            object v = ParseValue(text, ref i);
            return v;
        }

        /// <summary>容错解析：失败时返回 null，不抛异常。</summary>
        public static object TryParse(string text)
        {
            try { return Parse(text); }
            catch { return null; }
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON 意外结束");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't')
            {
                Expect(s, ref i, "true");
                return true;
            }
            if (c == 'f')
            {
                Expect(s, ref i, "false");
                return false;
            }
            if (c == 'n')
            {
                Expect(s, ref i, "null");
                return null;
            }
            return ParseNumber(s, ref i);
        }

        static void Expect(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                throw new FormatException("JSON 期望 " + word + "（位置 " + i + "）");
            i += word.Length;
        }

        static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("JSON 对象未闭合");
                if (s[i] != '"') throw new FormatException("JSON 键必须是字符串（位置 " + i + "）");
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("JSON 缺少冒号（位置 " + i + "）");
                i++;
                object val = ParseValue(s, ref i);
                d[key] = val;
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("JSON 对象未闭合");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("JSON 对象出现非法字符（位置 " + i + "）");
            }
        }

        static List<object> ParseArray(string s, ref int i)
        {
            List<object> list = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (true)
            {
                object val = ParseValue(s, ref i);
                list.Add(val);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("JSON 数组未闭合");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw new FormatException("JSON 数组出现非法字符（位置 " + i + "）");
            }
        }

        static string ParseString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("JSON 字符串必须以引号开始（位置 " + i + "）");
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 <= s.Length)
                        {
                            int code = int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                            sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("JSON 字符串未闭合");
        }

        static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && ("+-0123456789.eE".IndexOf(s[i]) >= 0)) i++;
            if (i == start) throw new FormatException("JSON 出现非法字符 '" + s[start] + "'（位置 " + start + "）");
            string num = s.Substring(start, i - start);
            double d;
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new FormatException("JSON 数字格式错误：" + num);
            return d;
        }

        // ==================== 取值助手 ====================

        public static Dictionary<string, object> AsObj(object o)
        {
            return o as Dictionary<string, object>;
        }

        public static List<object> AsArr(object o)
        {
            return o as List<object>;
        }

        public static string S(Dictionary<string, object> d, string key, string def)
        {
            if (d == null) return def;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            string s = v as string;
            if (s != null) return s;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static double N(Dictionary<string, object> d, string key, double def)
        {
            if (d == null) return def;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            if (v is double) return (double)v;
            if (v is bool) return ((bool)v) ? 1 : 0;
            double r;
            if (double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return r;
            return def;
        }

        public static int I(Dictionary<string, object> d, string key, int def)
        {
            return (int)Math.Round(N(d, key, def));
        }

        public static bool B(Dictionary<string, object> d, string key, bool def)
        {
            if (d == null) return def;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            if (v is bool) return (bool)v;
            if (v is double) return ((double)v) != 0;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(s)) return def;
            if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
            if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase) || s.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
            return def;
        }

        public static List<string> StrList(Dictionary<string, object> d, string key)
        {
            List<string> result = new List<string>();
            if (d == null) return result;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return result;
            List<object> arr = v as List<object>;
            if (arr != null)
            {
                foreach (object o in arr) if (o != null) result.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
                return result;
            }
            string s = v as string;
            if (!string.IsNullOrEmpty(s)) result.Add(s);
            return result;
        }

        // ==================== 绑定到对象 ====================

        /// <summary>把解析出来的 Dictionary 绑定到 T 的公开字段上（按字段名匹配，大小写不敏感）。</summary>
        public static T Bind<T>(object node) where T : new()
        {
            T target = new T();
            BindInto(target, node);
            return target;
        }

        public static void BindInto(object target, object node)
        {
            if (target == null) return;
            Dictionary<string, object> d = node as Dictionary<string, object>;
            if (d == null) return;
            FieldInfo[] fields = target.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (FieldInfo f in fields)
            {
                object raw;
                if (!d.TryGetValue(f.Name, out raw)) continue;
                object conv = ConvertValue(raw, f.FieldType);
                if (conv == null && raw != null && !IsNullable(f.FieldType)) continue;
                try { f.SetValue(target, conv); }
                catch { /* 忽略类型不匹配的陈旧字段 */ }
            }
        }

        static bool IsNullable(Type t)
        {
            return !t.IsValueType || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>));
        }

        static object ConvertValue(object raw, Type target)
        {
            if (target == typeof(string)) return raw == null ? "" : Convert.ToString(raw, CultureInfo.InvariantCulture);
            if (raw == null) return null;

            if (target == typeof(bool)) return ToBool(raw, false);
            if (target == typeof(int)) return (int)Math.Round(ToNum(raw, 0));
            if (target == typeof(long)) return (long)Math.Round(ToNum(raw, 0));
            if (target == typeof(double)) return ToNum(raw, 0);
            if (target == typeof(float)) return (float)ToNum(raw, 0);
            if (target == typeof(decimal)) return (decimal)ToNum(raw, 0);
            if (target.IsEnum)
            {
                string s = Convert.ToString(raw, CultureInfo.InvariantCulture);
                try { return Enum.Parse(target, s, true); }
                catch { return null; }
            }

            if (target == typeof(string[]))
            {
                List<string> l = ToStrList(raw);
                return l.ToArray();
            }
            if (target == typeof(List<string>)) return ToStrList(raw);
            if (target == typeof(Dictionary<string, string>)) return ToStrDict(raw);
            if (target == typeof(Dictionary<string, object>))
            {
                Dictionary<string, object> dd = raw as Dictionary<string, object>;
                return dd ?? new Dictionary<string, object>();
            }

            if (target.IsGenericType && target.GetGenericTypeDefinition() == typeof(List<>))
            {
                Type item = target.GetGenericArguments()[0];
                List<object> arr = raw as List<object>;
                IList outList = (IList)Activator.CreateInstance(target);
                if (arr == null) return outList;
                foreach (object o in arr)
                {
                    object itemVal = ConvertValue(o, item);
                    if (itemVal != null) outList.Add(itemVal);
                }
                return outList;
            }

            // 嵌套对象
            if (target.IsClass && raw is Dictionary<string, object>)
            {
                object inst = Activator.CreateInstance(target);
                BindInto(inst, raw);
                return inst;
            }
            return null;
        }

        static double ToNum(object raw, double def)
        {
            if (raw is double) return (double)raw;
            if (raw is bool) return ((bool)raw) ? 1 : 0;
            double r;
            if (double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return r;
            return def;
        }

        static bool ToBool(object raw, bool def)
        {
            if (raw is bool) return (bool)raw;
            if (raw is double) return ((double)raw) != 0;
            string s = Convert.ToString(raw, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(s)) return def;
            if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return def;
        }

        static List<string> ToStrList(object raw)
        {
            List<string> list = new List<string>();
            List<object> arr = raw as List<object>;
            if (arr != null)
            {
                foreach (object o in arr) if (o != null) list.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
                return list;
            }
            string s = raw as string;
            if (!string.IsNullOrEmpty(s)) list.Add(s);
            return list;
        }

        static Dictionary<string, string> ToStrDict(object raw)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> d = raw as Dictionary<string, object>;
            if (d == null) return result;
            foreach (KeyValuePair<string, object> kv in d)
                result[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
            return result;
        }
    }
}
