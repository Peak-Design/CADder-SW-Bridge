using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Peak.SwToBlender.Core;
using Peak.SwToBlender.Core.Model;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.SwToBlender.Sw
{
    /// <summary>
    /// Reads every mate the WYSIWYG walk can see into one MateGraph. The
    /// output is plain data — the classifier and the unit tests never touch
    /// SolidWorks, so everything a mate means must be captured here.
    /// </summary>
    public static class MateReader
    {
        public static MateGraph Read(List<WalkedComponent> walked, Action<string> log)
        {
            var graph = new MateGraph();
            var byPath = new Dictionary<string, WalkedComponent>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in walked)
            {
                graph.Components.Add(w.Graph);
                if (w.Graph.Path != null && !byPath.ContainsKey(w.Graph.Path))
                    byPath[w.Graph.Path] = w;
            }

            // Every mated component reports the same mate feature, so the
            // graph would hold each mate two or more times without a dedupe.
            // The key is the feature name plus the resolved component set,
            // not the name alone: mate names are unique per DOCUMENT, and a
            // flexible subassembly's internal "Concentric1" may share its
            // name with a top-level "Concentric1".
            var seen = new HashSet<string>();

            foreach (var w in walked)
            {
                if (w.Comp == null) continue;
                // A component INSIDE a flexible subassembly reports the sub
                // document's mates, but through the top-context occurrence
                // every entity's ReferenceComponent comes back null and so
                // does the feature's selection list (live corpus 07,
                // 2026-08-22 — the hinge joint vanished and the leaf exported
                // as an island). Those mates are read below through the sub
                // document's OWN components, where resolution behaves exactly
                // as it does at top level.
                if (w.Parent != null) continue;
                object[] mates = null;
                try { mates = w.Comp.GetMates() as object[]; }
                catch (Exception ex)
                {
                    if (log != null) log("GetMates failed for " + w.Graph.Path + ": " + ex.Message);
                }
                if (mates == null) continue;
                foreach (var o in mates)
                    ReadOne(o, w, byPath, graph, seen, log);
            }

            foreach (var w in walked)
            {
                if (w.Graph.Solving != "flexible" || w.Graph.Suppressed) continue;
                ReadSubDocumentMates(w, byPath, graph, seen, log);
            }
            return graph;
        }

        /// <summary>One object out of a GetMates array into the graph.
        /// GetMates returns IMate2 or IMateInPlace objects (API help,
        /// IComponent2~GetMates.html); an in-place mate has no entity
        /// geometry to record and no residual freedom to classify, so it is
        /// skipped.</summary>
        private static void ReadOne(
            object o, WalkedComponent owner,
            Dictionary<string, WalkedComponent> byPath, MateGraph graph,
            HashSet<string> seen, Action<string> log)
        {
            var mate = o as IMate2;
            if (mate == null) return;
            var feat = o as IFeature;
            if (feat == null) return;

            GraphMate gm;
            try { gm = ReadMate(mate, feat, owner, byPath, log); }
            catch (Exception ex)
            {
                if (log != null) log("mate read failed on " + owner.Graph.Path + ": " + ex.Message);
                return;
            }
            if (gm == null) return;
            if (!seen.Add(DedupeKey(gm))) return;
            graph.Mates.Add(gm);
        }

        /// <summary>
        /// The internal mates of a flexible subassembly, read from the sub
        /// document's own components — the only context where their entities
        /// resolve. The sub node is the owner: name resolution joins the
        /// sub-context names onto its path, and mate residence lifts the
        /// sub-local geometry by its transform. Entities on the sub's own
        /// reference geometry stay null and pin to the sub node itself, which
        /// the fixed-in-sub merge then welds to the right body.
        /// </summary>
        private static void ReadSubDocumentMates(
            WalkedComponent sub, Dictionary<string, WalkedComponent> byPath,
            MateGraph graph, HashSet<string> seen, Action<string> log)
        {
            object[] comps = null;
            try
            {
                var asm = sub.Comp.GetModelDoc2() as IAssemblyDoc;
                if (asm != null) comps = asm.GetComponents(true) as object[];
            }
            catch (Exception ex)
            {
                if (log != null)
                    log("sub-doc mates: GetComponents failed for " + sub.Graph.Path + ": " + ex.Message);
            }
            if (comps == null) return;

            foreach (var o in comps)
            {
                var comp = o as Component2;
                if (comp == null) continue;
                object[] mates = null;
                try { mates = comp.GetMates() as object[]; }
                catch (Exception ex)
                {
                    if (log != null)
                        log("sub-doc GetMates failed under " + sub.Graph.Path + ": " + ex.Message);
                }
                if (mates == null) continue;
                foreach (var m in mates)
                    ReadOne(m, sub, byPath, graph, seen, log);
            }
        }

        // ── One mate ────────────────────────────────────────────────────────

        private static GraphMate ReadMate(
            IMate2 mate, IFeature feat, WalkedComponent owner,
            Dictionary<string, WalkedComponent> byPath, Action<string> log)
        {
            var gm = new GraphMate();
            int type = mate.Type;
            gm.TypeValue = type;
            gm.TypeName = MateTypeName(type);
            gm.FeatureName = feat.Name;
            try { gm.Suppressed = feat.IsSuppressed(); } catch { }
            try { gm.Alignment = mate.Alignment; } catch { }
            try { gm.Flipped = mate.Flipped; } catch { }
            ReadErrorState(feat, gm, log);

            ReadDimensionAndLimits(mate, feat, type, gm);
            ReadCoupling(feat, type, gm, log);
            ReadLockRotation(feat, type, gm, log);
            ReadSlotConstraint(feat, type, gm, log);
            ReadEntities(mate, feat, owner, byPath, gm, log);
            RetypeFaceEntities(feat, owner, byPath, gm, log);
            RecoverCurveEntities(mate, owner, byPath, gm, log);
            if (type == (int)swMateType_e.swMatePATH)
                ReadPathCurve(mate, owner, byPath, gm, log);
            return gm;
        }

        /// <summary>The slot mate's constraint option (free / centered /
        /// distance / percent) — the free one slides, the rest pin the
        /// component along the slot and leave only the spin.</summary>
        private static void ReadSlotConstraint(IFeature feat, int type, GraphMate gm, Action<string> log)
        {
            if (type != (int)swMateType_e.swMateSLOT) return;
            try
            {
                var data = SafeDefinition(feat) as ISlotMateFeatureData;
                if (data == null) return;
                gm.SlotConstraint = data.Constraint;
                if (log != null)
                    log("slot mate " + gm.FeatureName + ": constraint=" + data.Constraint);
            }
            catch { }
        }

        /// <summary>
        /// Samples a path mate's curve into a polyline. SolidWorks exposes NO
        /// feature data for path mates (the *MateFeatureData family has no
        /// path member; GetDefinition returns nothing), so the only route is
        /// the mate entities' Reference objects: an edge or sketch segment
        /// exposes its underlying ICurve, an IReferenceCurve its segment
        /// list. Curves evaluate in the owning body's model space and lift to
        /// the assembly by the owning component's transform. Everything here
        /// is defensive and logged — corpus 17 pins what path selections
        /// actually arrive as; until then a failed sample leaves PathPoints
        /// null and the classifier warns instead of guessing.
        /// </summary>
        private static void ReadPathCurve(
            IMate2 mate, WalkedComponent owner,
            Dictionary<string, WalkedComponent> byPath, GraphMate gm, Action<string> log)
        {
            var polylines = new List<List<double[]>>();
            int count = 0;
            try { count = mate.GetMateEntityCount(); } catch { }
            for (int i = 0; i < count; i++)
            {
                IMateEntity2 me = null;
                try { me = mate.MateEntity(i) as IMateEntity2; } catch { }
                if (me == null) continue;

                object reference = null;
                try { reference = me.Reference; } catch { }
                if (reference == null) continue;

                double[,] lift = null;
                try
                {
                    var refComp = me.ReferenceComponent as Component2;
                    var w = refComp == null ? null : ResolveWalked(refComp, owner, byPath);
                    if (w != null) lift = w.Graph.Transform;
                }
                catch { }

                foreach (var curve in CurvesOf(reference, log))
                {
                    var pts = SampleCurve(curve, lift, log);
                    if (pts != null && pts.Count >= 2) polylines.Add(pts);
                }
            }

            if (polylines.Count == 0)
            {
                if (log != null)
                    log("path mate " + gm.FeatureName + ": no sampleable curve on any entity");
                return;
            }

            var chained = ChainPolylines(polylines);
            gm.PathPoints = chained.ToArray();
            double[] a = chained[0], b = chained[chained.Count - 1];
            gm.PathClosed = MathOps.Distance2(a, b) < 1e-10;
            if (log != null)
                log("path mate " + gm.FeatureName + ": sampled " + chained.Count
                    + " point(s) from " + polylines.Count + " segment(s)"
                    + (gm.PathClosed ? " (closed)" : "")
                    + ", first=[" + a[0].ToString("G6", CultureInfo.InvariantCulture)
                    + "," + a[1].ToString("G6", CultureInfo.InvariantCulture)
                    + "," + a[2].ToString("G6", CultureInfo.InvariantCulture) + "]");
        }

        /// <summary>The ICurves a mate-entity reference can yield: an edge's
        /// curve, a sketch segment's curve (line/arc/ellipse/spline/parabola
        /// only, per its API doc), a reference curve's segments, or the
        /// object already being a curve.</summary>
        private static IEnumerable<ICurve> CurvesOf(object reference, Action<string> log)
        {
            var edge = reference as IEdge;
            if (edge != null)
            {
                ICurve c = null;
                try { c = edge.GetCurve() as ICurve; } catch { }
                if (c != null) yield return c;
                yield break;
            }
            var seg = reference as ISketchSegment;
            if (seg != null)
            {
                ICurve c = null;
                try { c = seg.GetCurve() as ICurve; } catch { }
                if (c != null) yield return c;
                yield break;
            }
            var refCurve = reference as IReferenceCurve;
            if (refCurve != null)
            {
                object[] segs = null;
                try { segs = refCurve.GetSegments() as object[]; } catch { }
                if (segs != null)
                    foreach (var o in segs)
                    {
                        var c = o as ICurve;
                        if (c != null) yield return c;
                    }
                yield break;
            }
            var direct = reference as ICurve;
            if (direct != null) yield return direct;
        }

        /// <summary>
        /// The deviation a sampled polyline may have from its curve. The
        /// consumer rides the polyline, so this is a real positioning error
        /// on the driven part: 10 um is a decimal place below anything a
        /// modeller would notice and two below SolidWorks' own default mate
        /// tolerance.
        /// </summary>
        private const double PathChordToleranceM = 1e-5;

        /// <summary>Ceiling on one segment's samples. Reached only by a curve
        /// that is pathological or enormous, where a slightly coarser
        /// polyline beats a manifest nobody can load.</summary>
        private const int PathMaxSamples = 2048;

        /// <summary>One curve to one polyline, assembly space. Evaluate2 over
        /// the parameter range — GetTessPts needs trim endpoints this code
        /// does not always have. A line's range is the whole representable
        /// axis (its API doc says so verbatim), which no path is; such
        /// segments are skipped with a log line rather than sampled absurd.
        ///
        /// Sampling is ADAPTIVE: a fixed count cannot hold a tolerance across
        /// the range of paths a real assembly holds (48 uniform samples left
        /// a live 0.9 m spline 78 um off its own mate vertex — corpus 17,
        /// 2026-08-23 — and would leave a cable run far worse), so intervals
        /// bisect until the curve's midpoint sits within tolerance of the
        /// chord.</summary>
        private static List<double[]> SampleCurve(ICurve curve, double[,] lift, Action<string> log)
        {
            double s = 0, e = 0;
            bool closed = false, periodic = false;
            try
            {
                if (!curve.GetEndParams(out s, out e, out closed, out periodic)) return null;
            }
            catch { return null; }
            if (double.IsNaN(s) || double.IsNaN(e) || double.IsInfinity(s) || double.IsInfinity(e))
                return null;
            if (Math.Abs(e - s) > 1e8)
            {
                if (log != null)
                    log("path curve segment skipped: unbounded parameter range (an untrimmed line?)");
                return null;
            }

            // A seed coarse enough to be cheap on a straight edge and fine
            // enough that refinement never has to reach across a full period
            // of a wavy spline (whose chord midpoint can land back ON the
            // curve and stop refinement early).
            const int seed = 16;
            var pts = new List<double[]>();
            double[] prev = EvaluatePoint(curve, s, lift);
            if (prev == null) return null;
            pts.Add(prev);
            for (int i = 0; i < seed; i++)
            {
                double a = s + (e - s) * i / seed;
                double b = s + (e - s) * (i + 1) / seed;
                double[] pb = EvaluatePoint(curve, b, lift);
                if (pb == null) return null;
                if (!RefineSpan(curve, lift, a, prev, b, pb, 0, pts)) return null;
                prev = pb;
            }
            return pts;
        }

        /// <summary>Bisects one parameter span until the curve's midpoint is
        /// within tolerance of the chord, appending every point AFTER the
        /// span's start. False means the curve stopped evaluating.</summary>
        private static bool RefineSpan(
            ICurve curve, double[,] lift, double ta, double[] pa, double tb, double[] pb,
            int depth, List<double[]> pts)
        {
            if (depth < 7 && pts.Count < PathMaxSamples)
            {
                double tm = 0.5 * (ta + tb);
                double[] pm = EvaluatePoint(curve, tm, lift);
                if (pm == null) return false;
                double dx = pm[0] - 0.5 * (pa[0] + pb[0]);
                double dy = pm[1] - 0.5 * (pa[1] + pb[1]);
                double dz = pm[2] - 0.5 * (pa[2] + pb[2]);
                if (dx * dx + dy * dy + dz * dz
                        > PathChordToleranceM * PathChordToleranceM)
                {
                    return RefineSpan(curve, lift, ta, pa, tm, pm, depth + 1, pts)
                        && RefineSpan(curve, lift, tm, pm, tb, pb, depth + 1, pts);
                }
            }
            pts.Add(pb);
            return true;
        }

        private static double[] EvaluatePoint(ICurve curve, double t, double[,] lift)
        {
            double[] ev = null;
            try { ev = curve.Evaluate2(t, 0) as double[]; } catch { }
            if (ev == null || ev.Length < 3) return null;
            var p = new[] { ev[0], ev[1], ev[2] };
            return lift == null ? p : SwFrames.LiftPoint(lift, p);
        }

        /// <summary>Greedy end-to-end chaining of segment polylines, reversing
        /// segments as needed. Gaps beyond tolerance are logged and bridged —
        /// a broken chain that follows the path approximately still beats no
        /// path at all.</summary>
        private static List<double[]> ChainPolylines(List<List<double[]>> segments)
        {
            var chain = new List<double[]>(segments[0]);
            var remaining = new List<List<double[]>>(segments);
            remaining.RemoveAt(0);
            while (remaining.Count > 0)
            {
                var end = chain[chain.Count - 1];
                int bestIdx = 0;
                bool reverse = false;
                double best = double.MaxValue;
                for (int i = 0; i < remaining.Count; i++)
                {
                    double dStart = MathOps.Distance2(end, remaining[i][0]);
                    double dEnd = MathOps.Distance2(end, remaining[i][remaining[i].Count - 1]);
                    if (dStart < best) { best = dStart; bestIdx = i; reverse = false; }
                    if (dEnd < best) { best = dEnd; bestIdx = i; reverse = true; }
                }
                var next = remaining[bestIdx];
                remaining.RemoveAt(bestIdx);
                var pts = reverse ? Reversed(next) : next;
                // Drop the duplicated shared endpoint when the chain is tight.
                int from = best < 1e-10 ? 1 : 0;
                for (int i = from; i < pts.Count; i++) chain.Add(pts[i]);
            }
            return chain;
        }

        private static List<double[]> Reversed(List<double[]> pts)
        {
            var r = new List<double[]>(pts);
            r.Reverse();
            return r;
        }

        /// <summary>
        /// A mate flagged by SolidWorks (red error or yellow over-defined
        /// warning in the tree) is one the solver is not honouring faithfully
        /// — with an over-defined set, SolidWorks itself picks which mate to
        /// ignore, and this exporter cannot know which. The state is recorded
        /// here; ExportCommand refuses to export while any unsuppressed mate
        /// carries one, so the user fixes the assembly instead of getting a
        /// silently wrong rig.
        /// </summary>
        private static void ReadErrorState(IFeature feat, GraphMate gm, Action<string> log)
        {
            int code = 0;
            bool isWarning = false;
            try { code = feat.GetErrorCode2(out isWarning); }
            catch { return; }
            if (code == (int)swFeatureError_e.swFeatureErrorNone) return;
            if (code == (int)swFeatureError_e.swFeatureErrorMateBroken)
            {
                // Suppressed mate ENTITIES mean the mate is not solving
                // anything — dead weight left behind by suppressed or
                // deleted parts, not a modelling error the user must fix
                // (Oscar, 2026-08-23: a clean large assembly carried a pile
                // of these and the export refused). Treated exactly like an
                // explicitly suppressed mate: logged, skipped everywhere.
                // Over-defined and errored mates still abort the export.
                gm.Suppressed = true;
                if (log != null)
                    log("mate " + gm.FeatureName + " has suppressed mate entities; "
                        + "inactive, treated as suppressed");
                return;
            }
            gm.Error = MateErrorText(code) + (isWarning ? " (warning)" : " (error)");
            if (log != null)
                log("mate " + gm.FeatureName + " has " + gm.Error
                    + " [swFeatureError_e " + code.ToString(CultureInfo.InvariantCulture) + "]");
        }

        /// <summary>The swFeatureError_e members mates actually raise; any
        /// other value falls through with its number so a new SolidWorks
        /// error still names itself.</summary>
        private static string MateErrorText(int code)
        {
            switch (code)
            {
                case (int)swFeatureError_e.swFeatureErrorMateOverdefined:
                    return "over-defines the assembly";
                case (int)swFeatureError_e.swFeatureErrorMateIlldefined:
                    return "cannot be solved";
                case (int)swFeatureError_e.swFeatureErrorMateBroken:
                    return "has suppressed mate entities";
                case (int)swFeatureError_e.swFeatureErrorMateDanglingGeometry:
                    return "points to dangling geometry";
                case (int)swFeatureError_e.swFeatureErrorMateInvalidEdge:
                    return "references a suppressed or missing edge";
                case (int)swFeatureError_e.swFeatureErrorMateInvalidFace:
                    return "references a suppressed or missing face";
                case (int)swFeatureError_e.swFeatureErrorMateInvalidEntity:
                    return "references a suppressed or missing entity";
                default:
                    return "a feature error (swFeatureError_e "
                        + code.ToString(CultureInfo.InvariantCulture) + ")";
            }
        }

        /// <summary>
        /// Limit range and current value.
        ///
        /// The trap: IMate2.MinimumVariation/MaximumVariation are RELATIVE —
        /// "Minimum_variation = minimum_value - dimension_value" (API help,
        /// IMate2~MinimumVariation.html) — while the manifest contract wants
        /// ABSOLUTE limits plus value_at_rest. The mate feature data carries
        /// the absolute values (IDistanceMateFeatureData.MinimumDistance /
        /// MaximumDistance / Distance, all metres; the angle twin in radians),
        /// so that is the source here, and the IMate2 pair is only a fallback
        /// whose equal-values case still reads correctly as "not a limit
        /// mate".
        /// </summary>
        private static void ReadDimensionAndLimits(IMate2 mate, IFeature feat, int type, GraphMate gm)
        {
            if (type == (int)swMateType_e.swMateDISTANCE)
            {
                var data = SafeDefinition(feat) as IDistanceMateFeatureData;
                if (data != null)
                {
                    gm.CurrentValue = data.Distance;
                    gm.MinimumVariation = data.MinimumDistance;
                    gm.MaximumVariation = data.MaximumDistance;
                    try { gm.DimensionFlipped = data.FlipDimension; } catch { }
                    return;
                }
            }
            else if (type == (int)swMateType_e.swMateANGLE)
            {
                var data = SafeDefinition(feat) as IAngleMateFeatureData;
                if (data != null)
                {
                    gm.CurrentValue = data.Angle;
                    gm.MinimumVariation = data.MinimumAngle;
                    gm.MaximumVariation = data.MaximumAngle;
                    // The flip tick is the ONLY thing that distinguishes two
                    // mirrored limit mates parked at the same degenerate pose
                    // (live corpus 01 hinge vs hinge5, 2026-08-23).
                    try { gm.DimensionFlipped = data.FlipDimension; } catch { }
                    return;
                }
            }
            else if (type == (int)swMateType_e.swMateHINGE)
            {
                // A hinge carries its own optional angle range;
                // AngleSelection is the "specify angle limits" tick. Without
                // it the hinge is a plain revolute and the equal 0/0 pair
                // says so.
                var data = SafeDefinition(feat) as IHingeMateFeatureData;
                if (data != null && data.AngleSelection)
                {
                    gm.CurrentValue = data.Angle;
                    gm.MinimumVariation = data.MinVal;
                    gm.MaximumVariation = data.MaxVal;
                }
                return;
            }
            else
            {
                return;    // other mate types have no dimension to record
            }

            // Fallback for a distance/angle mate whose definition data was
            // unavailable: the relative pair. Equal values still classify as
            // "not a limit mate", which fails safe; unequal values reach the
            // manifest without their absolute anchor, which is why the
            // definition path above is the one that must normally run.
            try
            {
                gm.MinimumVariation = mate.MinimumVariation;
                gm.MaximumVariation = mate.MaximumVariation;
            }
            catch { }
        }

        /// <summary>
        /// Gear/rack/screw/coupler numbers, normalised to the manifest's
        /// units: gear and linear coupler as the raw numerator/denominator
        /// pair in entity order (the classifier signs and orients them
        /// against the mount joints), rack-pinion as metres of rack per
        /// radian of pinion, screw as metres of travel per revolution.
        /// Every raw value is logged: the sign conventions here are pinned
        /// on single live samples and the log is what settles the next
        /// disagreement.
        /// </summary>
        private static void ReadCoupling(IFeature feat, int type, GraphMate gm, Action<string> log)
        {
            if (type == (int)swMateType_e.swMateGEAR)
            {
                var data = SafeDefinition(feat) as IGearMateFeatureData;
                if (data == null) return;
                gm.CouplingNumerator = data.GearRatioNumerator;
                gm.CouplingDenominator = data.GearRatioDenominator;
                gm.CouplingReverse = data.Reverse;
                if (log != null)
                    log("gear mate " + gm.FeatureName + ": num=" + data.GearRatioNumerator
                        + " den=" + data.GearRatioDenominator + " reverse=" + data.Reverse);
            }
            else if (type == (int)swMateType_e.swMateRACKPINION)
            {
                var data = SafeDefinition(feat) as IRackPinionMateFeatureData;
                if (data == null) return;
                // DiameterVal is either the pinion pitch diameter or the rack
                // travel per revolution (API help, IRackPinionMateFeatureData~
                // DiameterType.html). Travel per radian is diameter/2 in the
                // first case and travel/2pi in the second.
                double v = data.DiameterVal;
                double perRadian =
                    data.DiameterType == (int)swRackPinionMateDistanceOptions_e.swRackTravelPerRevolution
                        ? v / (2.0 * Math.PI)
                        : v / 2.0;
                gm.MetersPerRadian = data.Reverse ? -perRadian : perRadian;
                if (log != null)
                    log("rack mate " + gm.FeatureName + ": diameterVal=" + v
                        + " type=" + data.DiameterType + " reverse=" + data.Reverse);
            }
            else if (type == (int)swMateType_e.swMateSCREW)
            {
                var data = SafeDefinition(feat) as IScrewMateFeatureData;
                if (data == null) return;
                // RevolutionVal is distance-per-revolution or its reciprocal,
                // by RevolutionType (API help, IScrewMateFeatureData~
                // RevolutionType.html). The doc mentions the user's linear
                // unit for the reciprocal form; the API convention everywhere
                // else is metres, and that is what this code assumes — a live
                // check is the only way to settle it.
                double v = data.RevolutionVal;
                double lead;
                if (data.RevolutionType == (int)swScrewMateDistanceOptions_e.swDistancePerRevolution)
                    lead = v;
                else if (Math.Abs(v) > 1e-12)
                    lead = 1.0 / v;
                else
                    return;
                // Sign convention pinned on live corpus 09 (2026-08-22): the
                // default screw mate behaved as a RIGHT-hand thread in
                // SolidWorks while Reverse read true, so Reverse=true means
                // +lead (advance along the rotation's right-hand direction).
                // A lead's handedness is chirality — independent of which way
                // the joint axis points — so no axis-sense term belongs here.
                // Single live sample; the log line is the tie-breaker if a
                // future left-hand or re-reversed screw disagrees.
                gm.LeadMPerRev = data.Reverse ? lead : -lead;
                if (log != null)
                    log("screw mate " + gm.FeatureName + ": revolutionVal=" + v
                        + " type=" + data.RevolutionType + " reverse=" + data.Reverse
                        + " -> lead=" + gm.LeadMPerRev);
            }
            else if (type == (int)swMateType_e.swMateLINEARCOUPLER)
            {
                var data = SafeDefinition(feat) as ILinearCouplerMateFeatureData;
                if (data == null) return;
                gm.CouplingNumerator = data.CouplerRatioNumerator;
                gm.CouplingDenominator = data.CouplerRatioDenominator;
                gm.CouplingReverse = data.Reverse;
                if (log != null)
                    log("linear coupler mate " + gm.FeatureName + ": num=" + data.CouplerRatioNumerator
                        + " den=" + data.CouplerRatioDenominator + " reverse=" + data.Reverse);
            }
        }

        /// <summary>
        /// The profile-centre "lock rotation" tick. It lives NOWHERE in the
        /// entity params — live corpus 05 (2026-08-22): planar4 (unlocked)
        /// and planar5 (locked) export byte-identical raw entities — so the
        /// feature data is the only source.
        /// </summary>
        private static void ReadLockRotation(IFeature feat, int type, GraphMate gm, Action<string> log)
        {
            if (type != (int)swMateType_e.swMatePROFILECENTER) return;
            var data = SafeDefinition(feat) as IProfileCenterMateFeatureData;
            if (data == null) return;
            try { gm.LockRotation = data.LockRotation; } catch { }
            if (log != null)
                log("profile-centre " + gm.FeatureName + " LockRotation=" + gm.LockRotation);
        }

        // ── Entities ────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the mate entities and lifts their geometry to the global
        /// frame.
        ///
        /// The frame question, settled by the API help page sldworksapi/
        /// SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.
        /// IMateEntity2~EntityParams.html: "All coordinate information is
        /// given in terms of the assembly coordinate system WHERE THE MATE
        /// RESIDES." A top-level mate is therefore already global. A mate
        /// that lives inside a flexible subassembly reports its geometry in
        /// that subassembly's own frame and is lifted here by the
        /// subassembly's root-relative transform. Residence is inferred: the
        /// deepest flexible ancestor of the reporting component under which
        /// every resolvable entity component sits is taken as the owning
        /// document; entities on that subassembly's own reference geometry
        /// come back with a null ReferenceComponent and are pinned to the
        /// subassembly's component id, not to the top assembly.
        /// </summary>
        private static void ReadEntities(
            IMate2 mate, IFeature feat, WalkedComponent owner,
            Dictionary<string, WalkedComponent> byPath, GraphMate gm,
            Action<string> log)
        {
            int count = 0;
            try { count = mate.GetMateEntityCount(); } catch { }

            var entities = new List<IMateEntity2>();
            var resolved = new List<WalkedComponent>();
            var refNames = new List<string>();
            for (int i = 0; i < count; i++)
            {
                IMateEntity2 e = null;
                try { e = mate.MateEntity(i); } catch { }
                if (e == null) continue;
                entities.Add(e);
                Component2 refComp = null;
                try { refComp = e.ReferenceComponent; } catch { }
                string refName = null;
                if (refComp != null)
                {
                    try { refName = refComp.Name2; } catch { }
                    if (refName == null) refName = "?";
                }
                refNames.Add(refName);
                resolved.Add(ResolveWalked(refComp, owner, byPath));
            }

            FallbackResolveFromSelections(feat, owner, byPath, resolved, gm, log);

            var residence = MateResidence(owner, resolved);
            double[,] lift = residence == null ? null : residence.Graph.Transform;

            // One raw line per mate: what SolidWorks ACTUALLY reported.
            // Pinned by the ball-joint hunt (2026-08-22): a spherical face
            // arriving under a wrong entity kind with a leftover direction is
            // invisible in the manifest — only this line can show it.
            var diag = new StringBuilder();
            diag.Append("mate ").Append(gm.FeatureName)
                .Append(" [").Append(gm.TypeName).Append("]");

            for (int i = 0; i < entities.Count; i++)
            {
                var e = entities[i];
                var ge = new GraphMateEntity();

                var comp = resolved[i];
                if (comp != null) ge.ComponentId = comp.Id;
                else if (residence != null) ge.ComponentId = residence.Id;
                else ge.ComponentId = null;

                int kind = 0, sel = 0;
                try { kind = e.ReferenceType; } catch { }
                try { sel = e.ReferenceType2; } catch { }
                ge.EntityTypeName = EntityKind(kind, sel);

                var p = null as double[];
                try { p = e.EntityParams as double[]; } catch { }
                if (p != null && p.Length >= 3)
                {
                    ge.Point = SwFrames.LiftPoint(lift, new[] { p[0], p[1], p[2] });
                    // Only kinds whose EntityParams table row DEFINES a vector
                    // get one recorded. Everything else carries FILLER there —
                    // live corpus 04 (2026-08-22): coordinate-system mate
                    // entities arrived kind-unknown with direction (1,0,0),
                    // which downstream code then trusted as a joint axis.
                    bool directional = kind == (int)swMateEntityTypes_e.swMateLine
                                    || kind == (int)swMateEntityTypes_e.swMatePlane
                                    || kind == (int)swMateEntityTypes_e.swMateCylinder
                                    || kind == (int)swMateEntityTypes_e.swMateCone
                                    || kind == (int)swMateEntityTypes_e.swMateCircle;
                    // Parallel and perpendicular mates are ABOUT directions:
                    // SolidWorks records the measured normal in the direction
                    // slots even when it types the entity as a point — live
                    // corpus 06 parallelogram3 (2026-08-22): plane-face
                    // parallel mates arrived as point(1) carrying the face
                    // normal. The corpus-04 filler trap does not apply here;
                    // for these mate types the direction IS the mate.
                    if (gm.TypeValue == (int)swMateType_e.swMatePARALLEL
                        || gm.TypeValue == (int)swMateType_e.swMatePERPENDICULAR)
                        directional = true;
                    if (directional && p.Length >= 6)
                    {
                        var dir = new[] { p[3], p[4], p[5] };
                        if (MathOps.Norm(dir) > MathOps.Epsilon)
                            ge.Direction = SwFrames.LiftDirection(lift, dir);
                    }
                    bool hasRadius = kind == (int)swMateEntityTypes_e.swMateCylinder
                                  || kind == (int)swMateEntityTypes_e.swMateCone
                                  || kind == (int)swMateEntityTypes_e.swMateSphere
                                  || kind == (int)swMateEntityTypes_e.swMateCircle;
                    if (hasRadius && p.Length >= 7) ge.Radius = p[6];
                }

                diag.Append(" | ").Append(ge.EntityTypeName)
                    .Append('(').Append(kind).Append('/').Append(sel).Append(')')
                    .Append('@').Append(ge.ComponentId ?? "asm");
                // An unresolved reference is the difference between "the
                // subassembly's own geometry" (null, legitimate) and "a part
                // face whose component this reader failed to map" (a bug) —
                // live corpus 07 (2026-08-22) was undiagnosable without it.
                if (resolved[i] == null && i < refNames.Count && refNames[i] != null)
                    diag.Append("[!").Append(refNames[i]).Append(']');
                if (p != null)
                {
                    diag.Append(" raw=[");
                    for (int k = 0; k < p.Length; k++)
                    {
                        if (k > 0) diag.Append(',');
                        diag.Append(p[k].ToString("G5", CultureInfo.InvariantCulture));
                    }
                    diag.Append(']');
                }
                else diag.Append(" raw=null");

                gm.Entities.Add(ge);
            }

            if (log != null) log(diag.ToString());
        }

        /// <summary>
        /// Coincident mates onto curves lose their geometry in EntityParams:
        /// an EDGE arrives typed point(1) with filler direction slots (live
        /// corpus 16 pt2, 2026-08-23 — the vertex-on-edge slide direction
        /// vanished and the pair became a ball at the edge's endpoint), and
        /// a 3D-sketch segment arrives kind-13 with all-zero params (pt4 /
        /// path1). The underlying curve is still reachable through
        /// IMateEntity2.Reference — the same route the path-mate sampler
        /// uses. A LINE re-types the entity to "edge" with the real
        /// direction; any other curve is sampled into gm.PathPoints so the
        /// classifier can build a path joint.
        /// </summary>
        private static void RecoverCurveEntities(
            IMate2 mate, WalkedComponent owner,
            Dictionary<string, WalkedComponent> byPath, GraphMate gm, Action<string> log)
        {
            if (gm.TypeValue != (int)swMateType_e.swMateCOINCIDENT) return;

            int count = 0;
            try { count = mate.GetMateEntityCount(); } catch { }
            var polylines = new List<List<double[]>>();
            for (int i = 0; i < count && i < gm.Entities.Count; i++)
            {
                var ge = gm.Entities[i];
                // Candidates: a directionless "point" that may really be an
                // edge, and a kind-unknown that may be a sketch segment.
                // Genuine points/vertices and unrelated unknowns yield no
                // curve below and pass through unchanged.
                bool pointish = ge.EntityTypeName == "point" && ge.Direction == null;
                if (!pointish && ge.EntityTypeName != "unknown") continue;

                IMateEntity2 me = null;
                try { me = mate.MateEntity(i); } catch { }
                if (me == null) continue;
                object reference = null;
                try { reference = me.Reference; } catch { }
                if (reference == null) continue;

                double[,] lift = null;
                try
                {
                    var refComp = me.ReferenceComponent as Component2;
                    var w = refComp == null ? null : ResolveWalked(refComp, owner, byPath);
                    if (w != null) lift = w.Graph.Transform;
                }
                catch { }

                foreach (var curve in CurvesOf(reference, log))
                {
                    bool isLine = false;
                    try { isLine = curve.IsLine(); } catch { }
                    if (isLine)
                    {
                        double[] lp = null;
                        try { lp = curve.LineParams as double[]; } catch { }
                        if (lp != null && lp.Length >= 6)
                        {
                            var dir = new[] { lp[3], lp[4], lp[5] };
                            if (MathOps.Norm(dir) > MathOps.Epsilon)
                            {
                                ge.EntityTypeName = "edge";
                                ge.Direction = SwFrames.LiftDirection(
                                    lift, MathOps.Normalized(dir));
                                if (ge.Point == null)
                                    ge.Point = SwFrames.LiftPoint(
                                        lift, new[] { lp[0], lp[1], lp[2] });
                                if (log != null)
                                    log("recovered edge direction on " + gm.FeatureName
                                        + " @" + (ge.ComponentId ?? "asm"));
                            }
                        }
                        continue;
                    }
                    var pts = SampleCurve(curve, lift, log);
                    if (pts != null && pts.Count >= 2)
                    {
                        polylines.Add(pts);
                        ge.EntityTypeName = "curve";
                    }
                }
            }

            if (polylines.Count > 0 && gm.PathPoints == null)
            {
                var chained = ChainPolylines(polylines);
                gm.PathPoints = chained.ToArray();
                double[] a = chained[0], b = chained[chained.Count - 1];
                gm.PathClosed = MathOps.Distance2(a, b) < 1e-10;
                if (log != null)
                    log("coincident-on-curve " + gm.FeatureName + ": sampled "
                        + chained.Count.ToString(CultureInfo.InvariantCulture)
                        + " point(s)");
            }
        }

        /// <summary>
        /// The entity kind lies about curved faces, and only the mate
        /// feature's own selection list — real faces, whose surfaces know
        /// what they are — can straighten it out:
        ///
        ///  * swMateEntityTypes_e declares a Sphere member but SolidWorks
        ///    never emits it: a live spherical-face concentric (corpus 04,
        ///    2026-08-22) arrived as cone(5) — sphere centre in the point
        ///    slots and a FILLER direction the classifier trusted as an
        ///    axis. Sphere-surfaced cone entities retype "sphere",
        ///    direction dropped.
        ///  * A CONICAL face arrives as a CIRCLE entity (live corpus 15,
        ///    2026-08-23: every cone concentric/tangent came in edge(7/2))
        ///    with the HALF-ANGLE in the radius slot. Cone-surfaced
        ///    entities retype "cone", keep their axis, and carry the
        ///    surface's half-angle — the number a tangent-on-plane
        ///    decomposition needs for its tilted-axis gate.
        ///  * A face a POINT is coincident with is a case of its own: the
        ///    contact leaves five DOF whatever the surface is, and only the
        ///    plane and the cylinder have a carrier primitive for it. Every
        ///    other face — torus, sphere, cone, fillet, loft, B-surface —
        ///    retypes "surface" and carries its own triangulation, which is
        ///    the only description of it that survives the trip.
        /// </summary>
        private static void RetypeFaceEntities(
            IFeature feat, WalkedComponent owner,
            Dictionary<string, WalkedComponent> byPath, GraphMate gm, Action<string> log)
        {
            // A patch is only worth carrying for a point ON the face; every
            // other pairing either has an analytic joint or is not modelled
            // at all, and a stray B-surface would cost megabytes.
            bool wantPatch = gm.TypeValue == (int)swMateType_e.swMateCOINCIDENT
                && HasPointEntity(gm);
            bool anyCandidate = wantPatch;
            foreach (var e in gm.Entities)
                if (e.EntityTypeName == "cone" || e.EntityTypeName == "edge")
                { anyCandidate = true; break; }
            if (!anyCandidate) return;

            var ents = MatedEntities(SafeDefinition(feat), false);
            if (ents == null)
            {
                if (log != null)
                    log("no selection list for " + gm.FeatureName + " ["
                        + gm.TypeName + "] — surface retype skipped");
                return;
            }

            foreach (var o in ents)
            {
                var face = o as IFace2;
                if (face == null) continue;
                bool isSphere = false;
                bool isCone = false;
                bool isFreeform = false;
                double halfAngle = 0.0;
                try
                {
                    var surf = face.GetSurface() as ISurface;
                    if (surf != null && wantPatch)
                    {
                        // A point ON a face leaves five DOF whatever the face
                        // is, and only the plane and the cylinder have a
                        // carrier primitive for that (planar+ball,
                        // cylindrical+ball). A sphere or a cone is no more
                        // analytic here than a torus: their retypes exist for
                        // CONCENTRIC and TANGENT, which describe surfaces
                        // meeting surfaces, not a point on one.
                        isFreeform = !surf.IsPlane() && !surf.IsCylinder();
                    }
                    else if (surf != null)
                    {
                        isSphere = surf.IsSphere();
                        if (!isSphere && surf.IsCone())
                        {
                            isCone = true;
                            // ConeParams: origin(3), axis(3), radius, half
                            // angle — 8 doubles (API help, ISurface~
                            // ConeParams.html).
                            var cp = surf.ConeParams as double[];
                            if (cp != null && cp.Length >= 8) halfAngle = cp[7];
                        }
                    }
                }
                catch { }
                if (!isSphere && !isCone && !isFreeform) continue;

                Component2 comp = null;
                try
                {
                    var ent = face as IEntity;
                    if (ent != null) comp = ent.GetComponent() as Component2;
                }
                catch { }
                var walked = ResolveWalked(comp, owner, byPath);
                if (walked == null) continue;

                foreach (var ge in gm.Entities)
                {
                    if (ge.ComponentId != walked.Id) continue;
                    if (isFreeform)
                    {
                        if (IsPointKind(ge.EntityTypeName)) continue;
                        AttachSurfacePatch(face, ge, walked.Graph.Transform, gm, log);
                    }
                    else if (isSphere && ge.EntityTypeName == "cone")
                    {
                        ge.EntityTypeName = "sphere";
                        ge.Direction = null;
                        if (log != null)
                            log("retyped cone->sphere on " + gm.FeatureName + " @" + ge.ComponentId);
                    }
                    else if (isCone && ge.Direction != null
                        && (ge.EntityTypeName == "cone" || ge.EntityTypeName == "edge"))
                    {
                        ge.EntityTypeName = "cone";
                        ge.HalfAngle = halfAngle;
                        if (log != null)
                            log("retyped ->cone (half-angle "
                                + halfAngle.ToString("0.####", CultureInfo.InvariantCulture)
                                + ") on " + gm.FeatureName + " @" + ge.ComponentId);
                    }
                }
            }
        }

        private static bool IsPointKind(string kind)
        {
            return kind == "point" || kind == "vertex" || kind == "origin";
        }

        private static bool HasPointEntity(GraphMate gm)
        {
            foreach (var e in gm.Entities)
                if (e.Point != null && IsPointKind(e.EntityTypeName)) return true;
            return false;
        }

        /// <summary>Triangle budget for one surface patch. A face past this
        /// is coarsened rather than dropped — the patch is a contact surface,
        /// not a render mesh, and the alternative is no joint at all.</summary>
        private const int MaxPatchTriangles = 20000;

        /// <summary>
        /// Reads a face's triangulation onto the entity, welded and lifted to
        /// assembly space. GetTessTriangles hands back nine loose doubles per
        /// triangle, in the part's own space, in metres when NoConversion is
        /// set (the document's unit setting would otherwise reach the
        /// manifest). Welding matters: the raw form repeats every interior
        /// vertex once per triangle touching it, which is most of the file
        /// size and all of the seams a nearest-surface query could fall into.
        /// </summary>
        private static void AttachSurfacePatch(
            IFace2 face, GraphMateEntity ge, double[,] lift, GraphMate gm, Action<string> log)
        {
            double[] raw = null;
            try { raw = face.GetTessTriangles(true) as double[]; } catch { }
            if (raw == null || raw.Length < 9 || raw.Length % 9 != 0)
            {
                if (log != null)
                    log("surface patch on " + gm.FeatureName + " @" + ge.ComponentId
                        + ": no usable tessellation");
                return;
            }
            int triangles = raw.Length / 9;
            if (triangles > MaxPatchTriangles)
            {
                if (log != null)
                    log("surface patch on " + gm.FeatureName + " @" + ge.ComponentId
                        + ": " + triangles + " triangles exceeds the budget");
                return;
            }

            var points = new List<double[]>();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            var tris = new List<int[]>(triangles);
            for (int t = 0; t < triangles; t++)
            {
                var corner = new int[3];
                for (int c = 0; c < 3; c++)
                {
                    int at = t * 9 + c * 3;
                    var p = new[] { raw[at], raw[at + 1], raw[at + 2] };
                    if (lift != null) p = SwFrames.LiftPoint(lift, p);
                    // Welding on the exact bits is enough: the duplicates are
                    // one tessellator writing the same corner repeatedly, not
                    // two computations that might disagree in the last place.
                    string key = p[0].ToString("R", CultureInfo.InvariantCulture) + "|"
                        + p[1].ToString("R", CultureInfo.InvariantCulture) + "|"
                        + p[2].ToString("R", CultureInfo.InvariantCulture);
                    int at_index;
                    if (!index.TryGetValue(key, out at_index))
                    {
                        at_index = points.Count;
                        index[key] = at_index;
                        points.Add(p);
                    }
                    corner[c] = at_index;
                }
                if (corner[0] == corner[1] || corner[1] == corner[2] || corner[0] == corner[2])
                    continue;    // degenerate: a sliver the tessellator collapsed
                tris.Add(corner);
            }
            if (tris.Count == 0) return;

            ge.EntityTypeName = "surface";
            ge.SurfacePoints = points.ToArray();
            ge.SurfaceTriangles = tris.ToArray();
            if (log != null)
                log("surface patch on " + gm.FeatureName + " @" + ge.ComponentId
                    + ": " + tris.Count + " triangle(s), " + points.Count + " point(s)");
        }

        /// <summary>
        /// Second chance for entities whose ReferenceComponent did not map to
        /// a walked component: the mate feature's own selection list still
        /// holds the real faces, and a face's IEntity knows its component.
        /// Live corpus 07 (2026-08-22): every entity of a flexible
        /// subassembly's internal mates came back unresolved, so the hinge
        /// joint vanished and the leaf exported as a disconnected island.
        /// Selection order is assumed to match mate-entity order — for the
        /// mates this rescues both sides are usually symmetric, and the
        /// diagnostic line records that the fallback ran.
        /// </summary>
        private static void FallbackResolveFromSelections(
            IFeature feat, WalkedComponent owner,
            Dictionary<string, WalkedComponent> byPath,
            List<WalkedComponent> resolved, GraphMate gm, Action<string> log)
        {
            bool anyUnresolved = false;
            foreach (var r in resolved)
                if (r == null) { anyUnresolved = true; break; }
            if (!anyUnresolved) return;

            var ents = MatedEntities(SafeDefinition(feat), true);
            if (ents == null || ents.Length != resolved.Count) return;

            for (int i = 0; i < resolved.Count; i++)
            {
                if (resolved[i] != null) continue;
                Component2 comp = null;
                try
                {
                    var ent = ents[i] as IEntity;
                    if (ent != null) comp = ent.GetComponent() as Component2;
                }
                catch { }
                if (comp == null) continue;
                var hit = ResolveWalked(comp, owner, byPath);
                if (hit == null) continue;
                resolved[i] = hit;
                if (log != null)
                    log("selection-resolved entity " + i + " of " + gm.FeatureName
                        + " -> " + hit.Id);
            }
        }

        /// <summary>
        /// The mate's selection list — the faces and edges the user picked,
        /// which is where the retype passes read real surface geometry.
        ///
        /// EntitiesToMate is declared SEPARATELY on each *MateFeatureData
        /// interface; the base IMateFeatureData has no such member, so every
        /// type needs its own cast. A missing one fails SILENTLY (the retype
        /// passes just never run): live corpus 15 cone3 (2026-08-23) went out
        /// as a free joint for exactly that reason — TANGENT was absent, so
        /// the conical face kept its circle typing and lost the half-angle.
        /// All 17 interfaces that declare the member are listed here
        /// (reflected over the 2022 interop); the caller logs a miss.
        ///
        /// Three of them index the member by GROUP rather than returning one
        /// list — a hinge's concentric, coincident and angle picks; a cam's
        /// path and its follower; a rack and its pinion. Their groups are
        /// concatenated for callers that only need the SET of selections,
        /// and withheld from callers that read the list positionally, where
        /// a group order that is not the mate-entity order would attribute
        /// geometry to the wrong component.
        /// </summary>
        private static object[] MatedEntities(object def, bool indexAligned)
        {
            // Reading a selection list is a COM call per mate and a failure
            // must cost only this mate's retype, never the export.
            try { return MatedEntitiesCore(def, indexAligned); }
            catch { return null; }
        }

        private static object[] MatedEntitiesCore(object def, bool indexAligned)
        {
            if (def == null) return null;
            var angle = def as IAngleMateFeatureData;
            if (angle != null) return angle.EntitiesToMate as object[];
            var camFollower = def as ICamFollowerMateFeatureData;
            if (camFollower != null)
                return indexAligned ? null : Flatten(
                    camFollower.EntitiesToMate[(int)swCamMateEntityType_e.swCamMateEntityType_CamPath],
                    camFollower.EntitiesToMate[(int)swCamMateEntityType_e.swCamMateEntityType_CamFollower]);
            var coincident = def as ICoincidentMateFeatureData;
            if (coincident != null) return coincident.EntitiesToMate as object[];
            var concentric = def as IConcentricMateFeatureData;
            if (concentric != null) return concentric.EntitiesToMate as object[];
            var distance = def as IDistanceMateFeatureData;
            if (distance != null) return distance.EntitiesToMate as object[];
            var gear = def as IGearMateFeatureData;
            if (gear != null) return gear.EntitiesToMate as object[];
            var hinge = def as IHingeMateFeatureData;
            if (hinge != null)
                return indexAligned ? null : Flatten(
                    hinge.EntitiesToMate[(int)swHingeMateEntityType_e.swHingeMateEntityType_Concentric],
                    hinge.EntitiesToMate[(int)swHingeMateEntityType_e.swHingeMateEntityType_Coincident],
                    hinge.EntitiesToMate[(int)swHingeMateEntityType_e.swHingeMateEntityType_Angle]);
            var lockMate = def as ILockMateFeatureData;
            if (lockMate != null) return lockMate.EntitiesToMate as object[];
            var parallel = def as IParallelMateFeatureData;
            if (parallel != null) return parallel.EntitiesToMate as object[];
            var perpendicular = def as IPerpendicularMateFeatureData;
            if (perpendicular != null) return perpendicular.EntitiesToMate as object[];
            var profileCenter = def as IProfileCenterMateFeatureData;
            if (profileCenter != null) return profileCenter.EntitiesToMate as object[];
            var rackPinion = def as IRackPinionMateFeatureData;
            if (rackPinion != null)
                return indexAligned ? null : Flatten(
                    rackPinion.EntitiesToMate[(int)swRackPinionMateEntityType_e.swRackPinionMateEntityType_Rack],
                    rackPinion.EntitiesToMate[(int)swRackPinionMateEntityType_e.swRackPinionMateEntityType_Pinion]);
            var screw = def as IScrewMateFeatureData;
            if (screw != null) return screw.EntitiesToMate as object[];
            var slot = def as ISlotMateFeatureData;
            if (slot != null) return slot.EntitiesToMate as object[];
            var symmetric = def as ISymmetricMateFeatureData;
            if (symmetric != null) return symmetric.EntitiesToMate as object[];
            var tangent = def as ITangentMateFeatureData;
            if (tangent != null) return tangent.EntitiesToMate as object[];
            var universal = def as IUniversalJointMateFeatureData;
            if (universal != null) return universal.EntitiesToMate as object[];
            return null;
        }

        /// <summary>One list from several grouped selection lists.</summary>
        private static object[] Flatten(params object[] groups)
        {
            var all = new List<object>();
            foreach (var g in groups)
            {
                var items = g as object[];
                if (items == null) continue;
                foreach (var item in items)
                    if (item != null) all.Add(item);
            }
            return all.Count > 0 ? all.ToArray() : null;
        }

        /// <summary>
        /// Maps a mate entity's ReferenceComponent to a walked component.
        /// Three steps, each covering a way SolidWorks names the component:
        /// the full instance path (top-document context); the path qualified
        /// by an ancestor subassembly (a component handed out in the
        /// subassembly's own context omits the prefix); and the GetParent()
        /// chain, which folds an entity deep inside a rigid subassembly onto
        /// the leaf that represents the whole subassembly in the graph.
        /// </summary>
        private static WalkedComponent ResolveWalked(
            Component2 refComp, WalkedComponent owner,
            Dictionary<string, WalkedComponent> byPath)
        {
            for (var c = refComp; c != null; c = SafeParent(c))
            {
                string name = null;
                try { name = c.Name2; } catch { }
                if (string.IsNullOrEmpty(name)) break;

                // Context joins BEFORE the bare name: a sub-document entity
                // named "hinge-base-1" must land inside its own subassembly,
                // not on a same-named top-level component. Top-level names
                // never match a join (their owner's path prefixes wrongly),
                // so they still resolve through the bare lookup.
                WalkedComponent hit;
                for (var anc = owner; anc != null; anc = anc.Parent)
                {
                    if (byPath.TryGetValue(anc.Graph.Path + "/" + name, out hit)) return hit;
                }
                if (byPath.TryGetValue(name, out hit)) return hit;
            }
            return null;
        }

        private static Component2 SafeParent(Component2 comp)
        {
            try { return comp.GetParent(); }
            catch { return null; }
        }

        /// <summary>
        /// The flexible subassembly whose document owns this mate, or null
        /// for the top assembly. Deepest candidate first; a candidate is the
        /// residence only when every resolved entity sits STRICTLY below it —
        /// an entity on the candidate itself means a mate made one level up,
        /// grabbing the subassembly from outside.
        /// </summary>
        private static WalkedComponent MateResidence(
            WalkedComponent owner, List<WalkedComponent> resolved)
        {
            var candidate = owner.Graph.Solving == "flexible" && HasStrictDescendant(owner, resolved)
                ? owner
                : owner.FlexibleAncestor;

            for (var f = candidate; f != null; f = f.FlexibleAncestor)
            {
                bool allBelow = true;
                bool anyBelow = false;
                foreach (var r in resolved)
                {
                    if (r == null) continue;
                    if (IsStrictlyBelow(r, f)) { anyBelow = true; continue; }
                    allBelow = false;
                    break;
                }
                if (allBelow && anyBelow) return f;
            }
            return null;
        }

        private static bool HasStrictDescendant(WalkedComponent f, List<WalkedComponent> resolved)
        {
            foreach (var r in resolved)
                if (r != null && IsStrictlyBelow(r, f)) return true;
            return false;
        }

        private static bool IsStrictlyBelow(WalkedComponent w, WalkedComponent ancestor)
        {
            for (var p = w.Parent; p != null; p = p.Parent)
                if (ReferenceEquals(p, ancestor)) return true;
            return false;
        }

        // ── Naming ──────────────────────────────────────────────────────────

        /// <summary>Every swMateType_e member the API help lists (swconst/
        /// SolidWorks.Interop.swconst~SolidWorks.Interop.swconst.
        /// swMateType_e.html). A value from a newer SolidWorks falls through
        /// to a named unknown instead of a wrong classification.</summary>
        private static string MateTypeName(int type)
        {
            switch (type)
            {
                case (int)swMateType_e.swMateCOINCIDENT: return "swMateCOINCIDENT";
                case (int)swMateType_e.swMateCONCENTRIC: return "swMateCONCENTRIC";
                case (int)swMateType_e.swMatePERPENDICULAR: return "swMatePERPENDICULAR";
                case (int)swMateType_e.swMatePARALLEL: return "swMatePARALLEL";
                case (int)swMateType_e.swMateTANGENT: return "swMateTANGENT";
                case (int)swMateType_e.swMateDISTANCE: return "swMateDISTANCE";
                case (int)swMateType_e.swMateANGLE: return "swMateANGLE";
                case (int)swMateType_e.swMateUNKNOWN: return "swMateUNKNOWN";
                case (int)swMateType_e.swMateSYMMETRIC: return "swMateSYMMETRIC";
                case (int)swMateType_e.swMateCAMFOLLOWER: return "swMateCAMFOLLOWER";
                case (int)swMateType_e.swMateGEAR: return "swMateGEAR";
                case (int)swMateType_e.swMateWIDTH: return "swMateWIDTH";
                case (int)swMateType_e.swMateLOCKTOSKETCH: return "swMateLOCKTOSKETCH";
                case (int)swMateType_e.swMateRACKPINION: return "swMateRACKPINION";
                case (int)swMateType_e.swMateMAXMATES: return "swMateMAXMATES";
                case (int)swMateType_e.swMatePATH: return "swMatePATH";
                case (int)swMateType_e.swMateLOCK: return "swMateLOCK";
                case (int)swMateType_e.swMateSCREW: return "swMateSCREW";
                case (int)swMateType_e.swMateLINEARCOUPLER: return "swMateLINEARCOUPLER";
                case (int)swMateType_e.swMateUNIVERSALJOINT: return "swMateUNIVERSALJOINT";
                case (int)swMateType_e.swMateCOORDINATE: return "swMateCOORDINATE";
                case (int)swMateType_e.swMateSLOT: return "swMateSLOT";
                case (int)swMateType_e.swMateHINGE: return "swMateHINGE";
                case (int)swMateType_e.swMateSLIDER: return "swMateSLIDER";
                case (int)swMateType_e.swMatePROFILECENTER: return "swMatePROFILECENTER";
                case (int)swMateType_e.swMateMAGNETIC: return "swMateMAGNETIC";
                default:
                    return "swMateUNKNOWN(" + type.ToString(CultureInfo.InvariantCulture) + ")";
            }
        }

        /// <summary>
        /// IMateEntity2.ReferenceType names the geometry (swMateEntityTypes_e
        /// — the names the EntityParams table on its API help page keys on);
        /// ReferenceType2 (swSelectType_e) only refines a line into an axis
        /// or an edge. A circle entity is a circular edge and gets "edge" —
        /// its direction and radius survive either way. A selected VERTEX
        /// arrives with ReferenceType 0 (live corpus 13/16, 2026-08-23:
        /// every vertex mate came in unknown(0/3)), so the selection type is
        /// what names it — the point slots are real, the direction slots are
        /// filler.
        /// </summary>
        private static string EntityKind(int mateEntityType, int selectType)
        {
            switch (mateEntityType)
            {
                case (int)swMateEntityTypes_e.swMatePoint: return "point";
                case (int)swMateEntityTypes_e.swMateLine:
                    return selectType == (int)swSelectType_e.swSelEDGES ? "edge" : "axis";
                case (int)swMateEntityTypes_e.swMatePlane: return "plane";
                case (int)swMateEntityTypes_e.swMateCylinder: return "cylinder";
                case (int)swMateEntityTypes_e.swMateCone: return "cone";
                case (int)swMateEntityTypes_e.swMateSphere: return "sphere";
                case (int)swMateEntityTypes_e.swMateCircle: return "edge";
                default:
                    return selectType == (int)swSelectType_e.swSelVERTICES
                        ? "vertex" : "unknown";
            }
        }

        private static object SafeDefinition(IFeature feat)
        {
            try { return feat.GetDefinition(); }
            catch { return null; }
        }

        private static string DedupeKey(GraphMate gm)
        {
            var sb = new StringBuilder();
            sb.Append(gm.FeatureName).Append('|').Append(gm.TypeValue);
            var ids = new List<string>();
            foreach (var e in gm.Entities) ids.Add(e.ComponentId ?? "-");
            ids.Sort(StringComparer.Ordinal);
            foreach (var id in ids) sb.Append('|').Append(id);
            return sb.ToString();
        }
    }
}
