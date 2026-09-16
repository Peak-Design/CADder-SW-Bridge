using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Peak.Cadder.Sw
{
    /// <summary>
    /// A small ISO 10303-21 reader. Adapted from Peak Design's NEXT-STEP-SW
    /// (NEXT-STEP-SW/src/Peak.NextStep/Core/Part21.cs, same author/org): the
    /// parse logic and its traps are kept intact; the writer half (patches,
    /// appends, Save) is dropped because this add-in only READS the STEP file
    /// back to match occurrences: the geometry SolidWorks wrote is never
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

        public string NameOf(int id) => QuotedStringAt(id, 0);

        /// <summary>
        /// The Nth quoted string of an entity, doubled quotes unescaped.
        ///
        /// Needed because NEXT_ASSEMBLY_USAGE_OCCURRENCE carries TWO strings:
        /// ('NAUO1','Jaw-1',...): the first is the schema-level id, the
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
