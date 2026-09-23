using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
// Vendored from NEXT-STEP-SW (Peak.NextStep) @ b081285: the STEP appearance engine, merged into CADder Bridge.

namespace Peak.Cadder.Appearance
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
            ScanRefs(args, (start, length, refId) => outIds.Add(refId));
            return outIds;
        }

        /// <summary>
        /// Calls back for each entity reference in an argument text, with
        /// its position, its length and its id. A quoted string is text and
        /// is skipped. SolidWorks writes the part file name into the product
        /// and representation names, and a name such as 'Screw #6' then made
        /// a reference to entity 6. A copy of the part walked into that
        /// entity and copied it, and the name of the copy changed to the id
        /// of the copied entity.
        /// </summary>
        private static void ScanRefs(string args, Action<int, int, int> onRef)
        {
            bool inStr = false;
            int n = args.Length;
            for (int i = 0; i < n; i++)
            {
                char c = args[i];
                if (inStr)
                {
                    // Two quote marks escape one, as in Parse.
                    if (c == '\'')
                    {
                        if (i + 1 < n && args[i + 1] == '\'') { i++; continue; }
                        inStr = false;
                    }
                    continue;
                }
                if (c == '\'') { inStr = true; continue; }
                if (c != '#') continue;

                int j = i + 1;
                int refId = 0;
                while (j < n && args[j] >= '0' && args[j] <= '9')
                    refId = checked(refId * 10 + (args[j++] - '0'));
                if (j == i + 1) continue;
                onRef(i, j - i, refId);
                i = j - 1;
            }
        }

        /// <summary>
        /// Rewrites the entity references in an argument text. text returns
        /// the replacement for a reference, or null to keep it. A quoted
        /// string stays as it is.
        /// </summary>
        public static string ReplaceRefs(string args, Func<int, string> text)
        {
            if (string.IsNullOrEmpty(args)) return args;
            StringBuilder sb = null;
            int pos = 0;
            ScanRefs(args, (start, length, refId) =>
            {
                string replacement = text(refId);
                if (replacement == null) return;
                if (sb == null) sb = new StringBuilder(args.Length + 16);
                sb.Append(args, pos, start - pos).Append(replacement);
                pos = start + length;
            });
            if (sb == null) return args;
            sb.Append(args, pos, args.Length - pos);
            return sb.ToString();
        }

        /// <summary>
        /// Millimetres per unit of the length unit a representation context
        /// declares, or 1.0 when it declares none that can be read.
        ///
        /// SolidWorks writes a STEP with one context per part, each in that
        /// part's own units, and the assembly's placements in the top
        /// document's units. The 2022 sample landing_gear.sldasm mixes inch
        /// parts under a metre assembly (2026-09-14): read as millimetres,
        /// every occurrence sat 25 times too close to the origin and none
        /// matched. A context is a complex entity such as
        ///   ( GEOMETRIC_REPRESENTATION_CONTEXT(3) ...
        ///     GLOBAL_UNIT_ASSIGNED_CONTEXT((#a,#b,#c)) ... )
        /// whose units are themselves complex:
        ///   ( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT(.MILLI.,.METRE.) )
        ///   ( CONVERSION_BASED_UNIT('INCH',#m) LENGTH_UNIT() NAMED_UNIT(#d) )
        /// with #m a LENGTH_MEASURE_WITH_UNIT(LENGTH_MEASURE(25.4), #mm).
        /// </summary>
        public double LengthUnitMm(int contextId)
        {
            foreach (int unit in Refs(contextId))
            {
                string args = ArgsOf(unit) ?? "";
                if (args.IndexOf("LENGTH_UNIT", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                double mm = UnitMm(unit, 0);
                if (mm > 0) return mm;
            }
            return 1.0;
        }

        private static readonly Regex SiPrefixRe = new Regex(
            @"SI_UNIT\s*\(\s*(\$|\.[A-Z]+\.)\s*,\s*\.METRE\.",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MeasureRe = new Regex(
            @"LENGTH_MEASURE\s*\(\s*(-?\d+\.?\d*(?:[eE][-+]?\d+)?)\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private double UnitMm(int unit, int depth)
        {
            if (depth > 4) return 0;
            string args = ArgsOf(unit) ?? "";
            var si = SiPrefixRe.Match(args);
            if (si.Success)
            {
                switch (si.Groups[1].Value.ToUpperInvariant())
                {
                    case "$": return 1000.0;
                    case ".MILLI.": return 1.0;
                    case ".CENTI.": return 10.0;
                    case ".DECI.": return 100.0;
                    case ".KILO.": return 1.0e6;
                    case ".MICRO.": return 1.0e-3;
                    default: return 0;
                }
            }
            if (args.IndexOf("CONVERSION_BASED_UNIT", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                foreach (int r in Refs(unit))
                {
                    string margs = ArgsOf(r) ?? "";
                    var m = MeasureRe.Match(margs);
                    if (!m.Success) continue;
                    double factor = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    foreach (int baseUnit in Refs(r))
                    {
                        double baseMm = UnitMm(baseUnit, depth + 1);
                        if (baseMm > 0) return factor * baseMm;
                    }
                }
            }
            return 0;
        }

        /// <summary>
        /// Millimetres per unit of the placement's own coordinates: the
        /// length unit of the shape representation that lists it among its
        /// items, found through the representation relationship that
        /// carries the transformation. 1.0 when nothing declares it.
        /// </summary>
        public double PlacementUnitMm(int relationship, int placement)
        {
            foreach (int rep in Refs(relationship))
            {
                string t = TypeOf(rep) ?? "";
                if (t.IndexOf("REPRESENTATION", StringComparison.OrdinalIgnoreCase) < 0
                    || t.IndexOf("RELATIONSHIP", StringComparison.OrdinalIgnoreCase) >= 0
                    || t.IndexOf("TRANSFORMATION", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                var items = Refs(rep);
                if (!items.Contains(placement)) continue;
                foreach (int ctx in items)
                {
                    string ct = TypeOf(ctx) ?? "";
                    if (ct.IndexOf("REPRESENTATION_CONTEXT", StringComparison.OrdinalIgnoreCase) >= 0)
                        return LengthUnitMm(ctx);
                }
            }
            return 1.0;
        }

        /// <summary>
        /// The representation, among those that a relationship joins, that
        /// lists this item among its items. 0 when none does. PlacementUnitMm
        /// reads the unit of this representation, so a new placement must
        /// go into its item list.
        /// </summary>
        public int RepresentationListing(int relationship, int item)
        {
            foreach (int rep in Refs(relationship))
            {
                string t = TypeOf(rep) ?? "";
                if (t.IndexOf("REPRESENTATION", StringComparison.OrdinalIgnoreCase) < 0
                    || t.IndexOf("RELATIONSHIP", StringComparison.OrdinalIgnoreCase) >= 0
                    || t.IndexOf("TRANSFORMATION", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (Refs(rep).Contains(item)) return rep;
            }
            return 0;
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

            WriteReplacing(path, sb.ToString());
        }

        /// <summary>
        /// Writes the new text beside the target and then swaps it in.
        /// File.WriteAllText on the target empties it first, so a write that
        /// failed half way (a full disk, an I/O error) left a part of a
        /// file. The export then reported the file as SolidWorks wrote it,
        /// with the hash of that file. Now a failed write leaves the target
        /// as it was.
        /// </summary>
        private static void WriteReplacing(string path, string text)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.WriteAllText(temp, text);
                try
                {
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                }
                catch (IOException)
                {
                    // A virus scanner can hold the new file for a moment, and
                    // some network drives cannot swap files. The text is
                    // complete and can be written, so write it directly, as
                    // before.
                    File.WriteAllText(path, text);
                }
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        public static string Str(string s)
            => "'" + (s ?? "").Replace("'", "''") + "'";

        public static string Num(double v)
            => v.ToString("0.################", CultureInfo.InvariantCulture);
    }
}
