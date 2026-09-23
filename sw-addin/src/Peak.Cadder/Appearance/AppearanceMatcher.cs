using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
// Vendored from NEXT-STEP-SW (Peak.NextStep) @ b081285: the STEP appearance engine, merged into CADder Bridge.

namespace Peak.Cadder.Appearance
{
    /// <summary>
    /// Matches the component tree of SolidWorks to the occurrence tree in the
    /// STEP output of SolidWorks.
    ///
    /// The match walks both trees together, level by level. At each level the
    /// candidates are the children of the matched parent, filtered by product
    /// name, and the position decides between instances of the same part. The
    /// positions compare in the PARENT frame on both sides: STEP placements are
    /// parent relative, and the root-relative transform of SolidWorks is
    /// converted. A flat, root-relative comparison matches nothing below the
    /// first level. This was measured: 7 matches out of 4564 on a four-level
    /// assembly.
    ///
    /// A wrong match with no message is the failure that matters, because it
    /// gives a wrong colour that looks correct. This code therefore reports an
    /// occurrence that it cannot match, and leaves it alone. It does not guess.
    /// </summary>
    public sealed class AppearanceMatcher
    {
        private const double ToleranceMm = 0.1;

        private readonly Action<string> _log;

        public AppearanceMatcher(IModelDoc2 model, Action<string> log)
            : this(log)
        {
        }

        /// <summary>The match reads nothing from the model. The tests use
        /// this form, because they cannot name the embedded interop
        /// type.</summary>
        internal AppearanceMatcher(Action<string> log)
        {
            _log = log;
        }

        public List<KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>> Match(
            List<OccurrenceAppearance> occurrences, StepRewriter rw)
        {
            var empty = new List<KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>>();
            var roots = occurrences.Where(o => o.Parent == null).ToList();
            if (roots.Count == 0 || rw.RootPd < 0) return empty;

            int exported = occurrences.Count(o => o.Exported);
            int excluded = occurrences.Count - exported;
            if (excluded > 0)
                _log?.Invoke($"  {excluded} occurrence(s) hidden, suppressed or enveloped, so " +
                             "absent from the file by design; not matched");

            // The rotation convention of IMathTransform.ArrayData is applied
            // blind in the parent-frame conversion. Both readings run, and the
            // one that matches more of the tree wins. On a correct reading the
            // distances are near zero. On the wrong one they are rotations of
            // the true offsets, and nothing lands inside the tolerance.
            var rowRun = Run(roots, rw, alt: false, quiet: true);
            var colRun = Run(roots, rw, alt: true, quiet: true);
            bool useAlt = colRun > rowRun;
            _log?.Invoke($"  transform convention: {(useAlt ? "column" : "row")} vector " +
                         $"({Math.Max(rowRun, colRun)} vs {Math.Min(rowRun, colRun)} matches)");

            var pairs = empty;
            RunInto(roots, rw, useAlt, quiet: false, pairs);
            _log?.Invoke($"  matched {pairs.Count(p => p.Value != null)} of {exported} " +
                         "exported occurrence(s)");
            return pairs;
        }

        private int Run(List<OccurrenceAppearance> roots, StepRewriter rw, bool alt, bool quiet)
        {
            var pairs = new List<KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>>();
            RunInto(roots, rw, alt, quiet, pairs);
            return pairs.Count(p => p.Value != null);
        }

        private void RunInto(List<OccurrenceAppearance> roots, StepRewriter rw, bool alt,
            bool quiet,
            List<KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>> pairs)
        {
            MatchChildren(roots, rw.RootPd, rw, alt, quiet, pairs);
        }

        private void MatchChildren(List<OccurrenceAppearance> swKids, int stepParentPd,
            StepRewriter rw, bool alt, bool quiet,
            List<KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>> pairs)
        {
            rw.ChildrenByParentPd.TryGetValue(stepParentPd, out var stepKids);
            stepKids = stepKids ?? new List<StepRewriter.OccurrenceRef>();

            // One use of a shared sub-assembly walks the same STEP children as
            // every other use. The used set is therefore local to this walk of
            // this parent, never global.
            var used = new HashSet<StepRewriter.OccurrenceRef>();

            foreach (var sw in swKids)
            {
                if (!sw.Exported) continue;

                double[] want = RelMm(sw, alt);
                StepRewriter.OccurrenceRef best = null;
                double bestDist = double.MaxValue, secondDist = double.MaxValue;
                double nearest = double.MaxValue;

                // The candidates go in tiers of name match, strictest
                // first. A looser tier counts only when no stricter one has
                // a candidate inside the tolerance. With one ranking by
                // distance, 'base' and 'base_plate' at the same position
                // tied, the first NAUO won, and base took the occurrence of
                // base_plate. base_plate then had no match, and the override
                // of base painted base_plate.
                if (want != null)
                {
                    for (int tier = 0; tier <= LoosestTier && best == null; tier++)
                    {
                        StepRewriter.OccurrenceRef tierBest = null;
                        double tierDist = double.MaxValue, tierSecond = double.MaxValue;
                        foreach (var k in stepKids)
                        {
                            if (used.Contains(k) || k.Translation == null) continue;
                            if (NameTier(sw, k) != tier) continue;
                            double d = Distance(k.Translation, want);
                            if (d < nearest) nearest = d;
                            if (d < tierDist) { tierSecond = tierDist; tierDist = d; tierBest = k; }
                            else if (d < tierSecond) tierSecond = d;
                        }
                        if (tierBest != null && tierDist <= ToleranceMm)
                        {
                            best = tierBest;
                            bestDist = tierDist;
                            secondDist = tierSecond;
                        }
                    }
                }

                if (best != null)
                {
                    // Two occurrences at the same position make the match
                    // unclear. Report this instead of a choice.
                    if (secondDist <= ToleranceMm && !quiet)
                        _log?.Invoke($"  WARNING: {sw.Path} is ambiguous -- two occurrences " +
                                     $"within {ToleranceMm} mm; taking NAUO #{best.NauoId}");
                    used.Add(best);
                    pairs.Add(Pair(sw, best));
                    if (sw.Children.Count > 0)
                        MatchChildren(sw.Children, best.ChildPd, rw, alt, quiet, pairs);
                }
                else
                {
                    if (!quiet)
                        _log?.Invoke($"  UNMATCHED {sw.Path} ({CountExported(sw)} occurrence(s) " +
                                     $"under it; nearest candidate " +
                                     $"{(nearest == double.MaxValue ? -1 : nearest):F3} mm) -- " +
                                     "left with SolidWorks' colour");
                    AddUnmatchedSubtree(sw, pairs);
                }
            }
        }

        private static void AddUnmatchedSubtree(OccurrenceAppearance n,
            List<KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>> pairs)
        {
            pairs.Add(Pair(n, null));
            foreach (var c in n.Children)
                if (c.Exported) AddUnmatchedSubtree(c, pairs);
        }

        private static int CountExported(OccurrenceAppearance n)
            => 1 + n.Children.Where(c => c.Exported).Sum(CountExported);

        private const int LoosestTier = 3;

        /// <summary>
        /// How well this STEP product name fits this component, from 0 (the
        /// strictest) to LoosestTier, or -1 when it cannot be this component.
        /// SolidWorks names the product after the file, and appends the
        /// configuration name for a non-default configuration, joined with
        /// an underscore.
        ///
        ///   0  the document name
        ///   1  the document name and the referenced configuration
        ///   2  the component name without the instance number, for a
        ///      virtual or renamed component
        ///   3  any name that starts with the document name and '_'
        /// </summary>
        private static int NameTier(OccurrenceAppearance sw, StepRewriter.OccurrenceRef k)
        {
            string n = k.ProductName ?? "";
            string d = sw.DocName ?? "";
            if (d.Length > 0)
            {
                if (n.Equals(d, StringComparison.OrdinalIgnoreCase)) return 0;
                var cfg = sw.ReferencedConfiguration;
                if (!string.IsNullOrEmpty(cfg)
                    && n.Equals(d + "_" + cfg, StringComparison.OrdinalIgnoreCase)) return 1;
            }
            string seg = AppearanceLadder.LastSegmentWithoutInstance(sw.Path);
            if (seg.Length > 0 && n.Equals(seg, StringComparison.OrdinalIgnoreCase)) return 2;
            if (d.Length > 0 && n.StartsWith(d + "_", StringComparison.OrdinalIgnoreCase))
                return LoosestTier;
            return -1;
        }

        /// <summary>
        /// The position of this component in the frame of its PARENT, in
        /// millimetres. IComponent2.Transform2 is root relative and in metres,
        /// so the difference of the translations rotates back through the
        /// rotation of the parent.
        /// </summary>
        private static double[] RelMm(OccurrenceAppearance n, bool alt)
        {
            var c = n.Transform;
            if (c == null || c.Length < 13) return null;
            var p = n.Parent?.Transform;
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

        private static KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>
            Pair(OccurrenceAppearance a, StepRewriter.OccurrenceRef b)
            => new KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>(a, b);

        private static double Distance(double[] a, double[] b)
            => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0])
                       + (a[1] - b[1]) * (a[1] - b[1])
                       + (a[2] - b[2]) * (a[2] - b[2]));
    }
}
