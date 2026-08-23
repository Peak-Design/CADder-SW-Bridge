using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Peak.SwToBlender.Sw
{
    /// <summary>
    /// A small ISO 10303-21 reader. Adapted from Peak Design's NEXT-STEP-SW
    /// (NEXT-STEP-SW/src/Peak.NextStep/Core/Part21.cs, same author/org): the
    /// parse logic and its traps are kept intact; the writer half (patches,
    /// appends, Save) is dropped because this add-in only READS the STEP file
    /// back to match occurrences — the geometry SolidWorks wrote is never
    /// touched.
    ///
    /// The parser records every statement of the DATA section into an
    /// id-to-(type, arguments) table. A 12 MB assembly holds over a hundred
    /// thousand entities, and every lookup after the single parse pass is a
    /// dictionary hit.
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

        private Dictionary<string, List<int>> _byType;

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
            }
        }

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

        /// <summary>The first quoted string in the arguments of an entity.
        /// For a PRODUCT that is its id/name pair's first half, which
        /// SolidWorks fills with the same name it shows.</summary>
        public string NameOf(int id) => QuotedStringAt(id, 0);

        /// <summary>
        /// The Nth quoted string of an entity, doubled quotes unescaped.
        ///
        /// Needed because NEXT_ASSEMBLY_USAGE_OCCURRENCE carries TWO strings:
        /// ('NAUO1','Jaw-1',...) — the first is the schema-level id, the
        /// SECOND is the occurrence name SolidWorks writes from the component
        /// instance name. Taking the first string as "the name" reports
        /// 'NAUO1' for every occurrence.
        /// </summary>
        public string QuotedStringAt(int id, int index)
        {
            var args = ArgsOf(id);
            if (args == null) return null;
            var matches = Regex.Matches(args, @"'((?:[^']|'')*)'");
            if (index < 0 || index >= matches.Count) return null;
            return matches[index].Groups[1].Value.Replace("''", "'");
        }
    }
}
