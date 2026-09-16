using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Peak.Cadder.Core
{
    /// <summary>
    /// Minimal JSON reader/writer for the bridge protocol and the settings
    /// file. House style bans Newtonsoft/System.Text.Json on net48 (see
    /// PLAN.md), and ManifestWriter only writes: the bridge must also READ
    /// what Blender answers. Objects are Dictionary&lt;string, object&gt;,
    /// arrays List&lt;object&gt;, numbers double, plus string/bool/null.
    /// Invariant culture throughout: a comma decimal separator in a payload
    /// would corrupt every transform.
    /// </summary>
    public static class MiniJson
    {
        // ── Reading ─────────────────────────────────────────────────────────

        public static object Parse(string text)
        {
            if (text == null) throw new FormatException("null JSON text");
            int pos = 0;
            object value = ParseValue(text, ref pos);
            SkipWhitespace(text, ref pos);
            if (pos != text.Length)
                throw new FormatException("trailing content at offset " + pos);
            return value;
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            var obj = Parse(text) as Dictionary<string, object>;
            if (obj == null) throw new FormatException("expected a JSON object");
            return obj;
        }

        private static object ParseValue(string s, ref int pos)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length) throw new FormatException("unexpected end of JSON");
            char c = s[pos];
            switch (c)
            {
                case '{': return ParseDict(s, ref pos);
                case '[': return ParseList(s, ref pos);
                case '"': return ParseString(s, ref pos);
                case 't': Expect(s, ref pos, "true"); return true;
                case 'f': Expect(s, ref pos, "false"); return false;
                case 'n': Expect(s, ref pos, "null"); return null;
                default: return ParseNumber(s, ref pos);
            }
        }

        private static Dictionary<string, object> ParseDict(string s, ref int pos)
        {
            var result = new Dictionary<string, object>();
            pos++;  // '{'
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return result; }
            while (true)
            {
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != '"')
                    throw new FormatException("expected object key at offset " + pos);
                string key = ParseString(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length || s[pos] != ':')
                    throw new FormatException("expected ':' at offset " + pos);
                pos++;
                result[key] = ParseValue(s, ref pos);
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("unterminated object");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == '}') { pos++; return result; }
                throw new FormatException("expected ',' or '}' at offset " + pos);
            }
        }

        private static List<object> ParseList(string s, ref int pos)
        {
            var result = new List<object>();
            pos++;  // '['
            SkipWhitespace(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return result; }
            while (true)
            {
                result.Add(ParseValue(s, ref pos));
                SkipWhitespace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("unterminated array");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ']') { pos++; return result; }
                throw new FormatException("expected ',' or ']' at offset " + pos);
            }
        }

        private static string ParseString(string s, ref int pos)
        {
            pos++;  // opening quote
            var sb = new StringBuilder();
            while (pos < s.Length)
            {
                char c = s[pos++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (pos >= s.Length) break;
                char e = s[pos++];
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
                        if (pos + 4 > s.Length) throw new FormatException("bad \\u escape");
                        sb.Append((char)int.Parse(s.Substring(pos, 4),
                            NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        pos += 4;
                        break;
                    default: throw new FormatException("bad escape '\\" + e + "'");
                }
            }
            throw new FormatException("unterminated string");
        }

        private static object ParseNumber(string s, ref int pos)
        {
            int start = pos;
            while (pos < s.Length && ("+-0123456789.eE".IndexOf(s[pos]) >= 0)) pos++;
            string token = s.Substring(start, pos - start);
            double d;
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new FormatException("bad number '" + token + "' at offset " + start);
            return d;
        }

        private static void Expect(string s, ref int pos, string token)
        {
            if (pos + token.Length > s.Length || s.Substring(pos, token.Length) != token)
                throw new FormatException("expected '" + token + "' at offset " + pos);
            pos += token.Length;
        }

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t'
                   || s[pos] == '\r' || s[pos] == '\n')) pos++;
        }

        // ── Writing ─────────────────────────────────────────────────────────

        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
            var s = value as string;
            if (s != null) { WriteString(sb, s); return; }
            var dict = value as IDictionary<string, object>;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    WriteValue(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }
            var list = value as System.Collections.IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            }
            if (value is int) { sb.Append(((int)value).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is long) { sb.Append(((long)value).ToString(CultureInfo.InvariantCulture)); return; }
            if (value is double)
            {
                double d = (double)value;
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (value is float)
            {
                WriteValue(sb, (double)(float)value);
                return;
            }
            throw new NotSupportedException(
                "MiniJson cannot write " + value.GetType().FullName);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
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
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString(
                                "x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ── Typed accessors for bridge/settings payloads ────────────────────

        public static string Str(Dictionary<string, object> obj, string key, string fallback = null)
        {
            object v;
            return obj != null && obj.TryGetValue(key, out v) && v is string
                ? (string)v : fallback;
        }

        public static bool Flag(Dictionary<string, object> obj, string key, bool fallback = false)
        {
            object v;
            return obj != null && obj.TryGetValue(key, out v) && v is bool
                ? (bool)v : fallback;
        }

        public static int Int(Dictionary<string, object> obj, string key, int fallback = 0)
        {
            object v;
            if (obj != null && obj.TryGetValue(key, out v) && v is double)
                return (int)Math.Round((double)v);
            return fallback;
        }

        public static double Num(Dictionary<string, object> obj, string key, double fallback = 0.0)
        {
            object v;
            if (obj != null && obj.TryGetValue(key, out v) && v is double)
                return (double)v;
            return fallback;
        }

        public static Dictionary<string, object> Obj(Dictionary<string, object> obj, string key)
        {
            object v;
            return obj != null && obj.TryGetValue(key, out v)
                ? v as Dictionary<string, object> : null;
        }

        public static List<object> Arr(Dictionary<string, object> obj, string key)
        {
            object v;
            return obj != null && obj.TryGetValue(key, out v) ? v as List<object> : null;
        }

        /// <summary>A JSON array of numbers as doubles, or null when the key
        /// is missing or any item is not a number.</summary>
        public static double[] NumArray(Dictionary<string, object> obj, string key)
        {
            var list = Arr(obj, key);
            if (list == null) return null;
            var result = new double[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                if (!(list[i] is double)) return null;
                result[i] = (double)list[i];
            }
            return result;
        }
    }
}
