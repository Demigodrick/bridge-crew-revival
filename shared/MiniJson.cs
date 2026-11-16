using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StbcRevival
{
    /// <summary>
    /// A small JSON reader and writer for the mod's own files (bridge layouts, galaxy files): objects become Dictionary&lt;string, object&gt;,
    /// arrays List&lt;object&gt;, numbers double, and strings, true/false and null themselves. Unity 5.4's JsonUtility
    /// only fills [Serializable] classes it's happy with, and says nothing when it isn't; this says what's wrong.
    /// No Unity in here, so it's tested outside the game. Public so the galaxy designer tool can use it too.
    /// </summary>
    public static class MiniJson
    {
        public static object Parse(string text)
        {
            int i = 0;
            var value = Value(text, ref i);
            Space(text, ref i);
            if (i != text.Length) throw new FormatException($"JSON: something after the end, at {i}");
            return value;
        }

        /// <summary>
        /// The inverse of Parse: Dictionary&lt;string, object&gt; (keys in insertion order), lists and arrays, strings,
        /// bools, null and numbers. Indented two spaces. Numbers use the invariant culture and "R", so a value reads
        /// back as the same number; floats are written from their own digits (0.1f stays 0.1, not 0.10000000149).
        /// </summary>
        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        private static void Indent(StringBuilder sb, int depth)
        {
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        private static void WriteValue(StringBuilder sb, object v, int depth)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string) { WriteStr(sb, (string)v); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is float) { WriteNum(sb, ((float)v).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (v is double) { WriteNum(sb, ((double)v).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (v is int || v is long || v is short || v is byte || v is uint || v is ulong || v is ushort || v is sbyte || v is decimal)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }
            var dict = v as System.Collections.IDictionary;
            if (dict != null)
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                bool first = true;
                foreach (System.Collections.DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Indent(sb, depth + 1);
                    WriteStr(sb, (string)e.Key);
                    sb.Append(": ");
                    WriteValue(sb, e.Value, depth + 1);
                }
                Indent(sb, depth);
                sb.Append('}');
                return;
            }
            var list = v as System.Collections.IEnumerable;
            if (list != null)
            {
                var items = new List<object>();
                foreach (var item in list) items.Add(item);
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (int k = 0; k < items.Count; k++)
                {
                    if (k > 0) sb.Append(',');
                    Indent(sb, depth + 1);
                    WriteValue(sb, items[k], depth + 1);
                }
                Indent(sb, depth);
                sb.Append(']');
                return;
            }
            throw new FormatException("JSON: can't write a " + v.GetType().Name);
        }

        private static void WriteNum(StringBuilder sb, string text)
        {
            // JSON has no NaN or Infinity; writing one would make a file this reader can't read back.
            if (text == "NaN" || text.EndsWith("Infinity", StringComparison.Ordinal)) throw new FormatException("JSON: can't write " + text);
            sb.Append(text);
        }

        private static void WriteStr(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static void Space(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static object Value(string s, ref int i)
        {
            Space(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON: ends early");
            char c = s[i];
            if (c == '{')
            {
                var o = new Dictionary<string, object>();
                i++;
                Space(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return o; }
                while (true)
                {
                    Space(s, ref i);
                    if (i >= s.Length || s[i] != '"') throw new FormatException($"JSON: a name expected at {i}");
                    string key = Str(s, ref i);
                    Space(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException($"JSON: ':' expected at {i}");
                    i++;
                    o[key] = Value(s, ref i);
                    Space(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return o; }
                    throw new FormatException($"JSON: ',' or '}}' expected at {i}");
                }
            }
            if (c == '[')
            {
                var a = new List<object>();
                i++;
                Space(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return a; }
                while (true)
                {
                    a.Add(Value(s, ref i));
                    Space(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return a; }
                    throw new FormatException($"JSON: ',' or ']' expected at {i}");
                }
            }
            if (c == '"') return Str(s, ref i);
            if (Word(s, ref i, "true")) return true;
            if (Word(s, ref i, "false")) return false;
            if (Word(s, ref i, "null")) return null;
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E')) i++;
            if (i == start) throw new FormatException($"JSON: unexpected '{c}' at {i}");
            string text = s.Substring(start, i - start);
            // net35 throws on a number past double's range where net8 gives infinity: infinity on both, for the
            // reader of the value to reject saying where it is.
            try { return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture); }
            catch (OverflowException) { return text.StartsWith("-", StringComparison.Ordinal) ? double.NegativeInfinity : double.PositiveInfinity; }
        }

        private static bool Word(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        private static string Str(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // the opening quote
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("JSON: short \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break; // \" \\ \/
                }
            }
            throw new FormatException("JSON: a string doesn't end");
        }
    }
}
