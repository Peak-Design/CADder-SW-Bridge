using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Peak.Cadder.Sw
{
    /// <summary>What the matcher found for one walked component: the exact
    /// PRODUCT name in the STEP file, and the slash-joined occurrence path
    /// (root product name, then the NAUO occurrence names down the tree) the
    /// Blender side matches against.</summary>
    public sealed class OccurrenceMatch
    {
        public string StepName;
        public string StepPath;
    }

    public sealed class MatchResult
    {
        /// <summary>Walked component id to its match. A component with no
        /// entry could not be matched; the caller keeps its
        /// step_occurrence_path null and warns.</summary>
        public Dictionary<string, OccurrenceMatch> ByComponentId =
            new Dictionary<string, OccurrenceMatch>();

        public int ExportedCount;
        public int MatchedCount;
    }

    /// <summary>
    /// Matches the walked SolidWorks component tree to the occurrence tree in
    /// the STEP output. Adapted from Peak Design's NEXT-STEP-SW: the
    /// level-by-level matcher from NEXT-STEP-SW/src/Peak.NextStep/Core/
    /// OccurrenceMatcher.cs and the NAUO/placement discovery from
    /// StepRewriter.FindOccurrences in the same directory. The styling and
    /// geometry-target halves of the originals are not needed here and were
    /// dropped; every documented trap below is theirs.
    ///
    /// The match walks both trees together, level by level. At each level the
    /// candidates are the children of the matched parent, filtered by product
    /// name, and the position decides between instances of the same part. The
    /// positions compare in the PARENT frame on both sides: STEP placements
    /// are parent relative, and the root-relative transform of SolidWorks is
    /// converted. A flat, root-relative comparison matches nothing below the
    /// first level. This was measured in NEXT-STEP: 7 matches out of 4564 on
    /// a four-level assembly.
    ///
    /// A wrong match with no message is the failure that matters, because it
    /// pairs a bone with the wrong body and the rig looks plausible while
    /// moving the wrong part. This code therefore reports an occurrence that
    /// it cannot match, and leaves it alone. It does not guess.
    /// </summary>
    public sealed class OccurrenceMatcher
    {
        /// <summary>Manifest step_export.occurrence_matching id for this
        /// matcher's output.</summary>
        public const string MatcherId = "peak-nauo-tree/1";

        /// <summary>SolidWorks writes STEP files in millimetres; the
        /// SolidWorks transforms arrive in metres and convert on the way in.</summary>
        private const double ToleranceMm = 0.1;

        private readonly Part21 _step;
        private readonly Action<string> _log;

        private sealed class StepOccurrence
        {
            public int NauoId;
            /// <summary>The occurrence name: the SECOND quoted string of the
            /// NAUO, which SolidWorks fills with the component instance name
            /// ("Jaw-1"). The first string is the schema id ("NAUO1").</summary>
            public string NauoName;
            public int ParentPd;
            public int ChildPd;
            public string ProductName;
            /// <summary>The position of this occurrence in the frame of its
            /// PARENT assembly, in file units. STEP placements are parent
            /// relative; the root-relative position of a nested occurrence
            /// appears nowhere in the file.</summary>
            public double[] Translation;
        }

        private readonly Dictionary<int, List<StepOccurrence>> _childrenByParentPd =
            new Dictionary<int, List<StepOccurrence>>();
        private int _rootPd = -1;
        private string _rootProductName;

        public OccurrenceMatcher(Part21 step, Action<string> log)
        {
            _step = step;
            _log = log;
            FindOccurrences();
        }

        // ── STEP-side tree ──────────────────────────────────────────────────

        private void FindOccurrences()
        {
            var byNauo = new Dictionary<int, StepOccurrence>();

            foreach (var nauo in _step.ByType("NEXT_ASSEMBLY_USAGE_OCCURRENCE"))
            {
                var pds = _step.Refs(nauo)
                    .Where(r => _step.TypeOf(r) == "PRODUCT_DEFINITION").ToList();
                if (pds.Count < 2) continue;

                // NAUO(id, name, description, relating, related): the relating
                // product definition is the assembly, the related one the child.
                var occ = new StepOccurrence
                {
                    NauoId = nauo,
                    ParentPd = pds[0],
                    ChildPd = pds[pds.Count - 1],
                };
                occ.ProductName = _step.NameOf(ProductOf(occ.ChildPd));
                occ.NauoName = _step.QuotedStringAt(nauo, 1);
                // SolidWorks writes the NAUO name field as a single SPACE, not
                // an empty string (live corpus 07, 2026-08-22: every nested
                // path came out "flexible-sub1/ / "), so whitespace-only falls
                // back to the product name too, which is what the Blender
                // importer rebuilds its occurrence paths from.
                if (string.IsNullOrWhiteSpace(occ.NauoName)) occ.NauoName = occ.ProductName;
                byNauo[nauo] = occ;

                if (!_childrenByParentPd.TryGetValue(occ.ParentPd, out var list))
                    _childrenByParentPd[occ.ParentPd] = list = new List<StepOccurrence>();
                list.Add(occ);
            }

            foreach (var cdsr in _step.ByType("CONTEXT_DEPENDENT_SHAPE_REPRESENTATION"))
            {
                int pds = _step.Refs(cdsr).FirstOrDefault(
                    r => _step.TypeOf(r) == "PRODUCT_DEFINITION_SHAPE");
                int nauo = pds == 0 ? 0 : _step.Refs(pds).FirstOrDefault(
                    r => _step.TypeOf(r) == "NEXT_ASSEMBLY_USAGE_OCCURRENCE");
                if (nauo != 0 && byNauo.TryGetValue(nauo, out var occ))
                    occ.Translation = ReadOccurrencePlacement(cdsr);
            }

            // The root is the assembly that no occurrence uses as a child.
            var children = new HashSet<int>(byNauo.Values.Select(o => o.ChildPd));
            _rootPd = _childrenByParentPd.Keys.FirstOrDefault(p => !children.Contains(p));
            if (_rootPd == 0) _rootPd = -1;
            _rootProductName = _rootPd < 0 ? null : _step.NameOf(ProductOf(_rootPd));
        }

        /// <summary>
        /// Reads the position of this occurrence in the frame of its parent.
        ///
        /// This code follows an exact chain. A breadth-first search does not
        /// work here:
        ///     CDSR -> (representation_relationship_with_transformation)
        ///          -> ITEM_DEFINED_TRANSFORMATION(name, desc, item_1, item_2)
        /// Here item_1 is the position in PARENT space, which identifies the
        /// occurrence. item_2 is the origin of the part itself, and every
        /// occurrence SHARES it. A breadth-first search finds the point of
        /// item_2 just as easily, and then reports every occurrence at the
        /// origin.
        /// </summary>
        private double[] ReadOccurrencePlacement(int cdsr)
        {
            foreach (var rel in _step.Refs(cdsr))
            {
                var relType = _step.TypeOf(rel) ?? "";
                if (relType.IndexOf("REPRESENTATION_RELATIONSHIP", StringComparison.Ordinal) < 0)
                    continue;

                foreach (var idt in _step.Refs(rel))
                {
                    if (!string.Equals(_step.TypeOf(idt), "ITEM_DEFINED_TRANSFORMATION",
                                       StringComparison.OrdinalIgnoreCase))
                        continue;

                    var items = _step.Refs(idt);           // [transform_item_1, transform_item_2]
                    if (items.Count < 1) continue;

                    var placement = items[0];
                    if (!string.Equals(_step.TypeOf(placement), "AXIS2_PLACEMENT_3D",
                                       StringComparison.OrdinalIgnoreCase))
                        continue;

                    var placementRefs = _step.Refs(placement);   // [location, axis, refDirection]
                    if (placementRefs.Count < 1) continue;

                    int location = placementRefs[0];
                    if (string.Equals(_step.TypeOf(location), "CARTESIAN_POINT",
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        // In the unit of the parent's representation, which
                        // is whatever the top document was modelled in; the
                        // matcher compares millimetres.
                        var xyz = ReadTriple(location);
                        if (xyz == null) return null;
                        double mm = _step.PlacementUnitMm(rel, placement);
                        return new[] { xyz[0] * mm, xyz[1] * mm, xyz[2] * mm };
                    }
                }
            }
            return null;
        }

        private double[] ReadTriple(int cartesianPointId)
        {
            var args = _step.ArgsOf(cartesianPointId) ?? "";
            var nums = System.Text.RegularExpressions.Regex
                .Matches(args, @"-?\d+\.?\d*(?:[eE][-+]?\d+)?")
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))
                .ToArray();
            return nums.Length >= 3 ? new[] { nums[0], nums[1], nums[2] } : null;
        }

        /// <summary>Walks PRODUCT_DEFINITION to formation to PRODUCT.</summary>
        private int ProductOf(int productDefinition)
        {
            if (productDefinition <= 0) return -1;
            foreach (var f in _step.Refs(productDefinition))
            {
                var t = _step.TypeOf(f) ?? "";
                if (!t.StartsWith("PRODUCT_DEFINITION_FORMATION", StringComparison.Ordinal)) continue;
                foreach (var p in _step.Refs(f))
                    if (_step.TypeOf(p) == "PRODUCT") return p;
            }
            return -1;
        }

        // ── Matching ────────────────────────────────────────────────────────

        public MatchResult Match(List<WalkedComponent> walked)
        {
            var result = new MatchResult();
            var roots = walked.Where(w => w.Parent == null).ToList();
            result.ExportedCount = walked.Count(IsExported);
            if (roots.Count == 0 || _rootPd < 0) return result;

            // The rotation convention of IMathTransform.ArrayData is applied
            // blind in the parent-frame conversion. Both readings run, and the
            // one that matches more of the tree wins. On a correct reading the
            // distances are near zero. On the wrong one they are rotations of
            // the true offsets, and nothing lands inside the tolerance.
            int rowRun = CountMatches(roots, alt: false);
            int colRun = CountMatches(roots, alt: true);
            bool useAlt = colRun > rowRun;
            if (_log != null)
                _log("occurrence match convention: " + (useAlt ? "column" : "row")
                    + " vector (" + Math.Max(rowRun, colRun) + " vs "
                    + Math.Min(rowRun, colRun) + " matches)");

            string rootSegment = string.IsNullOrEmpty(_rootProductName) ? "" : _rootProductName;
            MatchChildren(roots, _rootPd, rootSegment, useAlt, quiet: false, result);
            result.MatchedCount = result.ByComponentId.Count;
            if (_log != null)
                _log("matched " + result.MatchedCount + " of "
                    + result.ExportedCount + " exported occurrence(s)");
            return result;
        }

        private int CountMatches(List<WalkedComponent> roots, bool alt)
        {
            var probe = new MatchResult();
            MatchChildren(roots, _rootPd, "", alt, quiet: true, probe);
            return probe.ByComponentId.Count;
        }

        private void MatchChildren(
            List<WalkedComponent> swKids, int stepParentPd, string parentPath,
            bool alt, bool quiet, MatchResult result)
        {
            _childrenByParentPd.TryGetValue(stepParentPd, out var stepKids);
            stepKids = stepKids ?? new List<StepOccurrence>();

            // One use of a shared sub-assembly walks the same STEP children as
            // every other use. The used set is therefore local to this walk of
            // this parent, never global.
            var used = new HashSet<StepOccurrence>();

            foreach (var sw in swKids)
            {
                // A suppressed component is absent from the STEP file by
                // design; matching it would only claim someone else's slot.
                if (!IsExported(sw)) continue;

                double[] want = RelMm(sw, alt);
                StepOccurrence best = null;
                double bestDist = double.MaxValue, secondDist = double.MaxValue;

                if (want != null)
                {
                    foreach (var k in stepKids)
                    {
                        if (used.Contains(k) || k.Translation == null) continue;
                        if (!NameMatches(sw, k)) continue;
                        double d = Distance(k.Translation, want);
                        if (d < bestDist) { secondDist = bestDist; bestDist = d; best = k; }
                        else if (d < secondDist) secondDist = d;
                    }
                }

                if (best != null && bestDist <= ToleranceMm)
                {
                    // Two occurrences at the same position make the match
                    // unclear. Report this instead of a choice.
                    if (secondDist <= ToleranceMm && !quiet && _log != null)
                        _log("WARNING: " + sw.Graph.Path + " is ambiguous -- two occurrences "
                            + "within " + ToleranceMm + " mm; taking NAUO #" + best.NauoId);
                    used.Add(best);

                    string path = string.IsNullOrEmpty(parentPath)
                        ? best.NauoName
                        : parentPath + "/" + best.NauoName;
                    result.ByComponentId[sw.Id] = new OccurrenceMatch
                    {
                        StepName = best.ProductName,
                        StepPath = path,
                    };
                    if (sw.Children.Count > 0)
                        MatchChildren(sw.Children, best.ChildPd, path, alt, quiet, result);
                }
                else
                {
                    if (!quiet && _log != null)
                        _log("UNMATCHED " + sw.Graph.Path + " (nearest candidate "
                            + (bestDist == double.MaxValue
                                ? "-1"
                                : bestDist.ToString("F3", CultureInfo.InvariantCulture))
                            + " mm) -- step_occurrence_path stays null");
                    // The children of an unmatched subassembly cannot anchor
                    // to a STEP parent either; they simply get no entry and
                    // the caller warns per component.
                }
            }
        }

        private static bool IsExported(WalkedComponent w)
            => w.Graph != null && !w.Graph.Suppressed;

        /// <summary>
        /// True when this STEP product can be this component. SolidWorks names
        /// the product after the file, and appends the configuration name for a
        /// non-default configuration, joined with an underscore.
        /// </summary>
        private static bool NameMatches(WalkedComponent sw, StepOccurrence k)
        {
            string n = k.ProductName ?? "";
            string d = sw.DocName ?? "";
            if (d.Length > 0)
            {
                if (n.Equals(d, StringComparison.OrdinalIgnoreCase)) return true;
                var cfg = sw.ReferencedConfiguration;
                if (!string.IsNullOrEmpty(cfg)
                    && n.Equals(d + "_" + cfg, StringComparison.OrdinalIgnoreCase)) return true;
                if (n.StartsWith(d + "_", StringComparison.OrdinalIgnoreCase)) return true;
            }
            // A virtual or renamed component: fall back to the component name
            // itself, without the instance number.
            string seg = AssemblyWalker.LastSegmentWithoutInstance(sw.Graph.Path);
            return seg.Length > 0 && n.Equals(seg, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The position of this component in the frame of its PARENT, in
        /// millimetres. IComponent2.Transform2 is root relative and in metres,
        /// so the difference of the translations rotates back through the
        /// rotation of the parent.
        /// </summary>
        private static double[] RelMm(WalkedComponent n, bool alt)
        {
            var c = n.RawTransform;
            if (c == null || c.Length < 13) return null;
            var p = n.Parent == null ? null : n.Parent.RawTransform;
            if (p == null || p.Length < 13)
                return new[] { c[9] * 1000.0, c[10] * 1000.0, c[11] * 1000.0 };

            double dx = c[9] - p[9], dy = c[10] - p[10], dz = c[11] - p[11];
            double scale = Math.Abs(p[12]) > 1e-12 ? p[12] : 1.0;

            if (!alt)
                return new[]
                {
                    (dx * p[0] + dy * p[1] + dz * p[2]) / scale * 1000.0,
                    (dx * p[3] + dy * p[4] + dz * p[5]) / scale * 1000.0,
                    (dx * p[6] + dy * p[7] + dz * p[8]) / scale * 1000.0,
                };
            return new[]
            {
                (dx * p[0] + dy * p[3] + dz * p[6]) / scale * 1000.0,
                (dx * p[1] + dy * p[4] + dz * p[7]) / scale * 1000.0,
                (dx * p[2] + dy * p[5] + dz * p[8]) / scale * 1000.0,
            };
        }

        private static double Distance(double[] a, double[] b)
            => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0])
                       + (a[1] - b[1]) * (a[1] - b[1])
                       + (a[2] - b[2]) * (a[2] - b[2]));
    }
}
