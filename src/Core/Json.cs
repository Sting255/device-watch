// ============================================================================
//  Json.cs —— 极简 JSON 读写
//
//  .NET Framework 4.7.2 没有 System.Text.Json，也不想引入第三方依赖，
//  所以自己写一个够用的：写出去的是固定形状的对象，读进来的是设置项和请求体。
//
//  只支持 JSON 的标准子集，够本项目用：
//    写出：object / IDictionary<string,object> / IEnumerable / string / 数字 / bool / null
//    读入：完整 JSON（对象 / 数组 / 字符串 / 数字 / true / false / null）
// ============================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DeviceWatch
{
    /// <summary>JSON 输出。按写入顺序生成，中文不转义。</summary>
    public sealed class JsonWriter
    {
        private readonly StringBuilder _sb = new StringBuilder(4096);
        private readonly bool _pretty;
        private int _depth;

        public JsonWriter(bool pretty = false) { _pretty = pretty; }

        public override string ToString() { return _sb.ToString(); }

        private void Indent()
        {
            if (!_pretty) return;
            _sb.Append('\n');
            for (int i = 0; i < _depth; i++) _sb.Append("  ");
        }

        public void WriteValue(object v)
        {
            if (v == null) { _sb.Append("null"); return; }

            if (v is string) { WriteString((string)v); return; }
            if (v is bool) { _sb.Append(((bool)v) ? "true" : "false"); return; }

            if (v is float) { WriteNumber(((float)v).ToString("0.###", CultureInfo.InvariantCulture)); return; }
            if (v is double)
            {
                double d = (double)v;
                if (double.IsNaN(d) || double.IsInfinity(d)) { _sb.Append("null"); return; }
                // 整数就不带小数点，前端 parseInt 更省事
                if (Math.Abs(d - Math.Round(d)) < 1e-9 && Math.Abs(d) < 1e15)
                    _sb.Append(((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture));
                else
                    _sb.Append(d.ToString("0.###", CultureInfo.InvariantCulture));
                return;
            }
            if (v is decimal) { WriteNumber(((decimal)v).ToString("0.###", CultureInfo.InvariantCulture)); return; }
            if (v is byte || v is sbyte || v is short || v is ushort || v is int || v is uint || v is long || v is ulong)
            { _sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return; }

            var dict = v as IDictionary<string, object>;
            if (dict != null) { WriteObject(dict); return; }

            var pairs = v as IEnumerable<KeyValuePair<string, object>>;
            if (pairs != null)
            {
                var d2 = new Dictionary<string, object>();
                foreach (var kv in pairs) d2[kv.Key] = kv.Value;
                WriteObject(d2);
                return;
            }

            var map = v as IDictionary;
            if (map != null)
            {
                var d3 = new Dictionary<string, object>();
                foreach (DictionaryEntry de in map) d3[Convert.ToString(de.Key)] = de.Value;
                WriteObject(d3);
                return;
            }

            var list = v as IEnumerable;
            if (list != null) { WriteArray(list); return; }

            WriteString(Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        public void WriteObject(IDictionary<string, object> o)
        {
            _sb.Append('{');
            _depth++;
            bool first = true;
            foreach (var kv in o)
            {
                if (!first) _sb.Append(',');
                first = false;
                Indent();
                WriteString(kv.Key);
                _sb.Append(':');
                if (_pretty) _sb.Append(' ');
                WriteValue(kv.Value);
            }
            _depth--;
            if (!first) Indent();
            _sb.Append('}');
        }

        public void WriteArray(IEnumerable items)
        {
            _sb.Append('[');
            _depth++;
            bool first = true;
            foreach (var it in items)
            {
                if (!first) _sb.Append(',');
                first = false;
                Indent();
                WriteValue(it);
            }
            _depth--;
            if (!first) Indent();
            _sb.Append(']');
        }

        private void WriteNumber(string s) { _sb.Append(s); }

        public void WriteString(string s)
        {
            _sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\b': _sb.Append("\\b"); break;
                    case '\f': _sb.Append("\\f"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) _sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else _sb.Append(c);      // 中文直接输出，不转义
                        break;
                }
            }
            _sb.Append('"');
        }
    }

    /// <summary>JSON 解析。返回 Dictionary / List / string / double / bool / null。</summary>
    public static class JsonParser
    {
        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int i = 0;
            object v = ParseValue(text, ref i);
            SkipWs(text, ref i);
            return v;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't') { Expect(s, ref i, "true"); return true; }
            if (c == 'f') { Expect(s, ref i, "false"); return false; }
            if (c == 'n') { Expect(s, ref i, "null"); return null; }
            return ParseNumber(s, ref i);
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                throw new FormatException("JSON 解析失败：位置 " + i + " 处期望 " + word);
            i += word.Length;
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var o = new Dictionary<string, object>();
            i++;   // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return o; }
            while (true)
            {
                SkipWs(s, ref i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("JSON 解析失败：缺少 ':'");
                i++;
                object val = ParseValue(s, ref i);
                o[key] = val;
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("JSON 解析失败：对象未闭合");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; break; }
                throw new FormatException("JSON 解析失败：对象里出现意外字符");
            }
            return o;
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var a = new List<object>();
            i++;   // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return a; }
            while (true)
            {
                a.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("JSON 解析失败：数组未闭合");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; break; }
                throw new FormatException("JSON 解析失败：数组里出现意外字符");
            }
            return a;
        }

        private static string ParseString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("JSON 解析失败：期望字符串");
            i++;
            var sb = new StringBuilder();
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
                            sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                            i += 4;
                        }
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("JSON 解析失败：字符串未闭合");
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && ("+-0123456789.eE".IndexOf(s[i]) >= 0)) i++;
            string num = s.Substring(start, i - start);
            double d;
            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            throw new FormatException("JSON 解析失败：'" + num + "' 不是数字");
        }

        // ------------------------------------------------------------ 取值辅助
        public static Dictionary<string, object> AsObject(object o) { return o as Dictionary<string, object>; }

        public static string GetString(Dictionary<string, object> o, string key, string def = null)
        {
            object v;
            if (o != null && o.TryGetValue(key, out v) && v != null) return Convert.ToString(v, CultureInfo.InvariantCulture);
            return def;
        }

        public static double GetDouble(Dictionary<string, object> o, string key, double def)
        {
            object v;
            if (o != null && o.TryGetValue(key, out v) && v != null)
            {
                if (v is double) return (double)v;
                double d;
                if (double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            }
            return def;
        }

        public static int GetInt(Dictionary<string, object> o, string key, int def)
        {
            return (int)Math.Round(GetDouble(o, key, def));
        }

        public static bool GetBool(Dictionary<string, object> o, string key, bool def)
        {
            object v;
            if (o != null && o.TryGetValue(key, out v) && v != null)
            {
                if (v is bool) return (bool)v;
                string s = Convert.ToString(v, CultureInfo.InvariantCulture);
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
                double d;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return Math.Abs(d) > 0.0001;
            }
            return def;
        }

        public static List<object> GetArray(Dictionary<string, object> o, string key)
        {
            object v;
            if (o != null && o.TryGetValue(key, out v)) return v as List<object>;
            return null;
        }
    }
}
