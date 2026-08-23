using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
// Vendored from NEXT-STEP-SW (Peak.NextStep) @ b081285 — the STEP appearance engine, merged into SW To Blender.

namespace Peak.SwToBlender.Appearance
{
    /// <summary>
    /// A small ISO 10303-21 reader and writer.
    ///
    /// This class works on the text and only adds to it. That is deliberate. The
    /// value of the exporter is that the geometry section of SolidWorks stays
    /// byte for byte the same. OCCT does not re-tolerance it, no PMI
    /// disappears, and the styling that SolidWorks writes for each face survives
    /// exactly as written. S0 proved that this styling is correct. This class
    /// therefore adds presentation entities, and changes only the styled items
    /// that are wrong.
    ///
    /// The parser records the position of every statement. A replacement swaps
    /// one recorded span, and every untouched statement is written back from
    /// the original text. A 12 MB assembly holds over a hundred thousand
    /// entities, and a replacement must not scan them all.
    /// </summary>
    public sealed class Part21
    {
        private static readonly Regex EntityRe =
            new Regex(@"^#(\d+)\s*=\s*([A-Z0-9_]+)?\s*\(", RegexOptions.Compiled);
        private static readonly Regex RefRe = new Regex(@"#(\d+)", RegexOptions.Compiled);

        public string Text { get; }
        public string Path { get; }

        /// <summary>Maps an id to a type and the argument text. The argument
        /// text includes the outer brackets.</summary>
        public Dictionary<int, KeyValuePair<string, string>> Entities { get; } =
            new Dictionary<int, KeyValuePair<string, string>>();

        /// <summary>The position of each original statement in Text, including
        /// the closing ';'.</summary>
        private readonly List<(int Id, int Start, int Length)> _spans =
            new List<(int, int, int)>();
        private readonly Dictionary<int, string> _patches = new Dictionary<int, string>();

        private readonly List<string> _appended = new List<string>();
        private readonly Dictionary<int, int> _appendedIndex = new Dictionary<int, int>();

        private Dictionary<string, List<int>> _byType;

        private int _nextId;

        public Part21(string path)
        {
            Path = path;
            Text = File.ReadAllText(path);
            Parse();
        }

        private void Parse()
        {
            int dataStart = Text.IndexOf("DATA;", StringComparison.Ordinal);
            if (dataStart < 0) throw new InvalidDataException("no DATA section");

            int i = dataStart + 5;
            int n = Text.Length;
            while (i < n)
            {
                while (i < n && char.IsWhiteSpace(Text[i])) i++;
                if (i >= n) break;

                int start = i;
                // Scan to the ';' that ends this statement. A quoted string
                // keeps its ';' characters, and two quote marks escape one.
                bool inStr = false;
                int end = -1;
                for (; i < n; i++)
                {
                    char c = Text[i];
                    if (inStr)
                    {
                        if (c == '\'')
                        {
                            if (i + 1 < n && Text[i + 1] == '\'') { i++; continue; }
                            inStr = false;
                        }
                    }
                    else if (c == '\'') inStr = true;
                    else if (c == ';') { end = i; break; }
                }
                if (end < 0) break;
                i = end + 1;

                if (Text[start] != '#') continue;
                string stmt = Text.Substring(start, end - start);   // without ';'
                var m = EntityRe.Match(stmt);
                if (!m.Success) continue;

                int id = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                string type = (m.Groups[2].Value ?? "").ToUpperInvariant();
                if (type.Length == 0)
                {
                    var inner = Regex.Match(stmt, @"\(\s*([A-Z0-9_]+)\s*\(");
                    type = "COMPLEX:" + (inner.Success ? inner.Groups[1].Value : "?");
                }
                Entities[id] = new KeyValuePair<string, string>(type, stmt.Substring(m.Length - 1));
                _spans.Add((id, start, end - start + 1));
                if (id >= _nextId) _nextId = id + 1;
            }
        }

        public int NextId() => _nextId++;

        public string TypeOf(int id)
            => Entities.TryGetValue(id, out var e) ? e.Key : null;

        public string ArgsOf(int id)
            => Entities.TryGetValue(id, out var e) ? e.Value : null;

        public List<int> ByType(string type)
        {
            if (_byType == null)
            {
                _byType = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in Entities)
                {
                    if (!_byType.TryGetValue(kv.Value.Key, out var list))
                        _byType[kv.Value.Key] = list = new List<int>();
                    list.Add(kv.Key);
                }
                foreach (var list in _byType.Values) list.Sort();
            }
            return _byType.TryGetValue(type, out var found) ? found : new List<int>();
        }

        public List<int> Refs(int id)
        {
            var outIds = new List<int>();
            var args = ArgsOf(id);
            if (args == null) return outIds;
            foreach (Match m in RefRe.Matches(args))
                outIds.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            return outIds;
        }

        /// <summary>The first quoted string in the arguments of an entity. This
        /// is its name.</summary>
        public string NameOf(int id)
        {
            var args = ArgsOf(id);
            if (args == null) return null;
            var m = Regex.Match(args, @"'((?:[^']|'')*)'");
            return m.Success ? m.Groups[1].Value.Replace("''", "'") : null;
        }

        /// <summary>
        /// Adds a new entity, and records it so that a later pass can find it.
        ///
        /// The record matters. De-instancing adds copies of PRODUCT entities,
        /// and the material pass afterwards finds the products by type. Without
        /// the record, the material pass cannot see the copies, and only the
        /// original part gets a material.
        /// </summary>
        public void Append(string entityLine)
        {
            var m = EntityRe.Match(entityLine.TrimStart());
            if (!m.Success) { _appended.Add(entityLine); return; }

            int id = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            _appendedIndex[id] = _appended.Count;
            _appended.Add(entityLine);
            Index(id, entityLine);
            if (id >= _nextId) _nextId = id + 1;
        }

        /// <summary>Parses one statement into the entity table and the type
        /// index.</summary>
        private void Index(int id, string statement)
        {
            string body = statement.Trim();
            if (body.EndsWith(";", StringComparison.Ordinal))
                body = body.Substring(0, body.Length - 1);
            var m = EntityRe.Match(body);
            if (!m.Success) return;

            string oldType = TypeOf(id);
            string type = (m.Groups[2].Value ?? "").ToUpperInvariant();
            if (type.Length == 0)
            {
                var inner = Regex.Match(body, @"\(\s*([A-Z0-9_]+)\s*\(");
                type = "COMPLEX:" + (inner.Success ? inner.Groups[1].Value : "?");
                Entities[id] = new KeyValuePair<string, string>(
                    type, body.Substring(body.IndexOf('=') + 1));
            }
            else
            {
                Entities[id] = new KeyValuePair<string, string>(type, body.Substring(m.Length - 1));
            }

            if (_byType == null) return;
            if (oldType != null && oldType != type
                && _byType.TryGetValue(oldType, out var oldList)) oldList.Remove(id);
            if (oldType == type && oldType != null) return;
            if (!_byType.TryGetValue(type, out var list)) _byType[type] = list = new List<int>();
            if (!list.Contains(id)) list.Add(id);
        }

        /// <summary>
        /// Replaces the whole statement of one entity. The entity table sees
        /// the new arguments at once. The text change lands when Save runs.
        /// </summary>
        public void Replace(int id, string newStatement)
        {
            if (_appendedIndex.TryGetValue(id, out var idx))
                _appended[idx] = newStatement;
            else
                _patches[id] = newStatement;
            Index(id, newStatement);
        }

        public void Save(string path)
        {
            var sb = new StringBuilder(Text.Length + _appended.Count * 64);

            if (_patches.Count == 0)
            {
                sb.Append(Text);
            }
            else
            {
                int pos = 0;
                foreach (var span in _spans)
                {
                    if (_patches.TryGetValue(span.Id, out var patched))
                    {
                        sb.Append(Text, pos, span.Start - pos);
                        sb.Append(patched);
                        pos = span.Start + span.Length;
                    }
                }
                sb.Append(Text, pos, Text.Length - pos);
            }

            if (_appended.Count > 0)
            {
                string built = sb.ToString();
                int idx = built.LastIndexOf("ENDSEC;", StringComparison.Ordinal);
                if (idx < 0) throw new InvalidDataException("no ENDSEC to append before");
                sb.Clear();
                sb.Append(built, 0, idx);
                sb.Append(string.Join(Environment.NewLine, _appended)).Append(Environment.NewLine);
                sb.Append(built, idx, built.Length - idx);
            }

            File.WriteAllText(path, sb.ToString());
        }

        public static string Str(string s)
            => "'" + (s ?? "").Replace("'", "''") + "'";

        public static string Num(double v)
            => v.ToString("0.################", CultureInfo.InvariantCulture);
    }
}
