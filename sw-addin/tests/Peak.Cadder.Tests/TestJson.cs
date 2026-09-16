using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Peak.Cadder.Tests
{
    internal enum JsonKind
    {
        Object,
        Array,
        String,
        Number,
        Bool,
        Null,
    }

    /// <summary>
    /// One parsed JSON value. Object members keep document order because the
    /// writer promises a fixed key order and the tests assert it.
    /// </summary>
    internal sealed class JsonValue
    {
        public JsonKind Kind;
        public List<KeyValuePair<string, JsonValue>> Members;   // Object only
        public List<JsonValue> Items;                           // Array only
        public string Str;
        public double Num;
        public bool Flag;

        public JsonValue this[string key]
        {
            get
            {
                foreach (var kv in Members)
                    if (kv.Key == key) return kv.Value;
                throw new KeyNotFoundException(key);
            }
        }

        public List<string> Keys
        {
            get
            {
                var keys = new List<string>();
                foreach (var kv in Members) keys.Add(kv.Key);
                return keys;
            }
        }

        public void Remove(string key)
        {
            for (int i = Members.Count - 1; i >= 0; i--)
                if (Members[i].Key == key) Members.RemoveAt(i);
        }
    }

    /// <summary>
    /// Strict recursive-descent JSON reader for the writer tests. Hand-rolled
    /// on purpose: the test project takes no JSON package dependency, and the
    /// parser's strictness is itself part of the assertion, anything the
    /// writer emits that this rejects (duplicate keys, bare words, comma-form
    /// decimals) is a writer bug.
    /// </summary>
    internal static class TestJson
    {
        public static JsonValue Parse(string text)
        {
            int pos = 0;
            var value = ParseValue(text, ref pos);
            SkipWs(text, ref pos);
            if (pos != text.Length)
                throw new FormatException("Trailing content at offset " + pos);
            return value;
        }

        /// <summary>Structural equality with exact number comparison. Object
        /// keys must match in order as well as name: the writer's key order
        /// is fixed by the schema and part of the contract under test.</summary>
        public static bool DeepEquals(JsonValue a, JsonValue b, out string diff)
        {
            return DeepEquals(a, b, "$", out diff);
        }

        private static bool DeepEquals(JsonValue a, JsonValue b, string path, out string diff)
        {
            diff = null;
            if (a.Kind != b.Kind)
            {
                diff = path + ": kind " + a.Kind + " vs " + b.Kind;
                return false;
            }
            switch (a.Kind)
            {
                case JsonKind.Object:
                    if (a.Members.Count != b.Members.Count)
                    {
                        diff = path + ": " + a.Members.Count + " vs " + b.Members.Count + " members";
                        return false;
                    }
                    for (int i = 0; i < a.Members.Count; i++)
                    {
                        if (a.Members[i].Key != b.Members[i].Key)
                        {
                            diff = path + ": key[" + i + "] '" + a.Members[i].Key + "' vs '" + b.Members[i].Key + "'";
                            return false;
                        }
                        if (!DeepEquals(a.Members[i].Value, b.Members[i].Value,
                                path + "." + a.Members[i].Key, out diff))
                            return false;
                    }
                    return true;
                case JsonKind.Array:
                    if (a.Items.Count != b.Items.Count)
                    {
                        diff = path + ": " + a.Items.Count + " vs " + b.Items.Count + " items";
                        return false;
                    }
                    for (int i = 0; i < a.Items.Count; i++)
                        if (!DeepEquals(a.Items[i], b.Items[i], path + "[" + i + "]", out diff))
                            return false;
                    return true;
                case JsonKind.String:
                    if (a.Str != b.Str) { diff = path + ": '" + a.Str + "' vs '" + b.Str + "'"; return false; }
                    return true;
                case JsonKind.Number:
                    // Exact: both sides came through double.Parse of decimal
                    // text, and the writer's format is round-trip exact.
                    if (a.Num != b.Num)
                    {
                        diff = path + ": " + a.Num.ToString("R", CultureInfo.InvariantCulture)
                            + " vs " + b.Num.ToString("R", CultureInfo.InvariantCulture);
                        return false;
                    }
                    return true;
                case JsonKind.Bool:
                    if (a.Flag != b.Flag) { diff = path + ": " + a.Flag + " vs " + b.Flag; return false; }
                    return true;
                default:
                    return true;    // null == null
            }
        }

        // ── Parser ──────────────────────────────────────────────────────────

        private static JsonValue ParseValue(string s, ref int pos)
        {
            SkipWs(s, ref pos);
            if (pos >= s.Length) throw new FormatException("Unexpected end of input");
            char ch = s[pos];
            if (ch == '{') return ParseObject(s, ref pos);
            if (ch == '[') return ParseArray(s, ref pos);
            if (ch == '"') return new JsonValue { Kind = JsonKind.String, Str = ParseString(s, ref pos) };
            if (ch == 't' || ch == 'f') return ParseBool(s, ref pos);
            if (ch == 'n') { Expect(s, ref pos, "null"); return new JsonValue { Kind = JsonKind.Null }; }
            if (ch == '-' || (ch >= '0' && ch <= '9')) return ParseNumber(s, ref pos);
            throw new FormatException("Unexpected character '" + ch + "' at offset " + pos);
        }

        private static JsonValue ParseObject(string s, ref int pos)
        {
            pos++;    // '{'
            var v = new JsonValue { Kind = JsonKind.Object, Members = new List<KeyValuePair<string, JsonValue>>() };
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return v; }
            while (true)
            {
                SkipWs(s, ref pos);
                if (pos >= s.Length || s[pos] != '"')
                    throw new FormatException("Expected object key at offset " + pos);
                string key = ParseString(s, ref pos);
                foreach (var kv in v.Members)
                    if (kv.Key == key)
                        throw new FormatException("Duplicate key '" + key + "' at offset " + pos);
                SkipWs(s, ref pos);
                if (pos >= s.Length || s[pos] != ':')
                    throw new FormatException("Expected ':' at offset " + pos);
                pos++;
                v.Members.Add(new KeyValuePair<string, JsonValue>(key, ParseValue(s, ref pos)));
                SkipWs(s, ref pos);
                if (pos >= s.Length) throw new FormatException("Unterminated object");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == '}') { pos++; return v; }
                throw new FormatException("Expected ',' or '}' at offset " + pos);
            }
        }

        private static JsonValue ParseArray(string s, ref int pos)
        {
            pos++;    // '['
            var v = new JsonValue { Kind = JsonKind.Array, Items = new List<JsonValue>() };
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return v; }
            while (true)
            {
                v.Items.Add(ParseValue(s, ref pos));
                SkipWs(s, ref pos);
                if (pos >= s.Length) throw new FormatException("Unterminated array");
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ']') { pos++; return v; }
                throw new FormatException("Expected ',' or ']' at offset " + pos);
            }
        }

        private static string ParseString(string s, ref int pos)
        {
            pos++;    // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (pos >= s.Length) throw new FormatException("Unterminated string");
                char ch = s[pos++];
                if (ch == '"') return sb.ToString();
                if (ch < 0x20) throw new FormatException("Raw control character in string at offset " + (pos - 1));
                if (ch != '\\') { sb.Append(ch); continue; }
                if (pos >= s.Length) throw new FormatException("Unterminated escape");
                char esc = s[pos++];
                switch (esc)
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
                        if (pos + 4 > s.Length) throw new FormatException("Truncated \\u escape");
                        sb.Append((char)ushort.Parse(s.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        pos += 4;
                        break;
                    default:
                        throw new FormatException("Bad escape '\\" + esc + "' at offset " + (pos - 1));
                }
            }
        }

        private static JsonValue ParseBool(string s, ref int pos)
        {
            if (s[pos] == 't') { Expect(s, ref pos, "true"); return new JsonValue { Kind = JsonKind.Bool, Flag = true }; }
            Expect(s, ref pos, "false");
            return new JsonValue { Kind = JsonKind.Bool, Flag = false };
        }

        /// <summary>JSON number grammar exactly: -? int frac? exp?. The decimal
        /// separator is a dot by grammar, so a culture-leaked comma splits the
        /// number and fails the surrounding container parse.</summary>
        private static JsonValue ParseNumber(string s, ref int pos)
        {
            int start = pos;
            if (pos < s.Length && s[pos] == '-') pos++;
            if (pos >= s.Length || s[pos] < '0' || s[pos] > '9')
                throw new FormatException("Malformed number at offset " + start);
            if (s[pos] == '0') pos++;
            else while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') pos++;
            if (pos < s.Length && s[pos] == '.')
            {
                pos++;
                if (pos >= s.Length || s[pos] < '0' || s[pos] > '9')
                    throw new FormatException("Digits required after '.' at offset " + pos);
                while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') pos++;
            }
            if (pos < s.Length && (s[pos] == 'e' || s[pos] == 'E'))
            {
                pos++;
                if (pos < s.Length && (s[pos] == '+' || s[pos] == '-')) pos++;
                if (pos >= s.Length || s[pos] < '0' || s[pos] > '9')
                    throw new FormatException("Digits required in exponent at offset " + pos);
                while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') pos++;
            }
            double value = double.Parse(s.Substring(start, pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            return new JsonValue { Kind = JsonKind.Number, Num = value };
        }

        private static void Expect(string s, ref int pos, string word)
        {
            if (pos + word.Length > s.Length || string.CompareOrdinal(s, pos, word, 0, word.Length) != 0)
                throw new FormatException("Expected '" + word + "' at offset " + pos);
            pos += word.Length;
        }

        private static void SkipWs(string s, ref int pos)
        {
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\n' || s[pos] == '\r'))
                pos++;
        }
    }
}
