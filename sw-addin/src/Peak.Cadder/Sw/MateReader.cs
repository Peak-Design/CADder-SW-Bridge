using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.Cadder.Sw
{
    /// <summary>
    /// Reads every mate the WYSIWYG walk can see into one MateGraph. The
    /// output is plain data: the classifier and the unit tests never touch
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
                // 2026-08-22: the hinge joint vanished and the leaf exported
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
        /// document's own components: the only context where their entities
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
            if (type == (int)swMateType_e.swMateCAMFOLLOWER)
                ReadCamFaces(mate, feat, owner, byPath, gm, log);
            return gm;
        }

        /// <summary>Triangle budget for one cam path. Past it the cam is
        /// left to the probe's table or the user's hand.</summary>
        private const int MaxCamTriangles = 60000;

        /// <summary>
        /// Cam-follower mates: the cam path's faces, triangulated and lifted
        /// to assembly space, onto the mate. The relation probe tables a cam
        /// that turns about one fixed axis by dragging it. A cam free in its
        /// plane (cam-follower2, 2026-09-15) has no one-input table, so the
        /// faces themselves travel and the consumer holds the follower on
        /// them. The feature data lists the cam path's faces apart from the
        /// follower (swCamMateEntityType_e); the mate entities show only the
        /// first face.
        /// </summary>
        private static void ReadCamFaces(
            IMate2 mate, IFeature feat, WalkedComponent owner,
            Dictionary<string, WalkedComponent> byPath, GraphMate gm, Action<string> log)
        {
            // A lightweight component's faces have no tessellation to read
            // (live cam-follower2, 2026-09-15: the cam path came back as
            // one face with no triangles). Resolving it loads the part, as
            // the appearance pass does later anyway; the assembly is never
            // saved by this add-in.
            ResolveLightweight(mate, gm, log);
            var faces = CamPathFaces(feat, gm, log);
            if (faces == null || faces.Count == 0)
            {
                if (log != null)
                    log("cam mate " + gm.FeatureName + ": no cam path faces in the feature data");
                return;
            }
            faces = ExtrudedPath(faces, gm, log);

            var points = new List<double[]>();
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            var tris = new List<int[]>();
            string camComp = null;
            double[,] camLift = null;
            var pathFaces = new List<IFace2>();
            int read = 0, unplaced = 0;
            foreach (var o in faces)
            {
                var face = o as IFace2;
                if (face == null) continue;
                double[,] lift = null;
                string compId = null;
                try
                {
                    var entity = face as IEntity;
                    var comp = entity == null ? null : entity.GetComponent() as Component2;
                    var w = comp == null ? null : ResolveWalked(comp, owner, byPath);
                    if (w != null)
                    {
                        lift = w.Graph.Transform;
                        compId = w.Id;
                    }
                    else if (log != null)
                        log("cam mate " + gm.FeatureName + ": a cam face has "
                            + (entity == null ? "no IEntity" : comp == null ? "no component"
                               : "an unwalked component " + SafeName(comp)));
                }
                catch (Exception ex)
                {
                    if (log != null)
                        log("cam mate " + gm.FeatureName + ": placing a cam face failed: " + ex.Message);
                }
                if (compId == null)
                {
                    // A face nobody walked lifts nowhere: better one face
                    // short than one face in the wrong place.
                    unplaced++;
                    continue;
                }
                if (camComp == null) camComp = compId;
                if (camLift == null) camLift = lift;
                pathFaces.Add(face);
                double[] raw = null;
                raw = TessTriangles(face);
                if (raw == null || raw.Length < 9 || raw.Length % 9 != 0)
                {
                    if (log != null)
                        log("cam mate " + gm.FeatureName + ": a cam face @" + compId
                            + " has no tessellation (" + (raw == null ? "null" : raw.Length + " doubles") + ")");
                    continue;
                }
                if (tris.Count + raw.Length / 9 > MaxCamTriangles)
                {
                    if (log != null)
                        log("cam mate " + gm.FeatureName + ": the cam path exceeds "
                            + MaxCamTriangles + " triangles; not carried");
                    return;
                }
                AppendTessellation(raw, lift, points, index, tris);
                read++;
            }
            if (tris.Count == 0 || camComp == null)
            {
                if (log != null)
                    log("cam mate " + gm.FeatureName + ": no usable tessellation on the cam path ("
                        + unplaced + " face(s) on no walked component)");
                return;
            }
            gm.CamComponentId = camComp;
            gm.CamSurfacePoints = points.ToArray();
            gm.CamSurfaceTriangles = tris.ToArray();
            if (log != null)
                log("cam mate " + gm.FeatureName + ": " + read + " cam face(s) @" + camComp
                    + ", " + tris.Count + " display triangle(s), " + points.Count + " point(s)"
                    + (unplaced > 0 ? ", " + unplaced + " face(s) skipped" : ""));
            FineCamPath(pathFaces, camLift, gm, log);
        }

        /// <summary>The slot mate's constraint option (free / centered /
        /// distance / percent): the free one slides, the rest pin the
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
        /// is defensive and logged: corpus 17 pins what path selections
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
        /// the parameter range. GetTessPts needs trim endpoints this code
        /// does not always have. A line's range is the whole representable
        /// axis (its API doc says so verbatim), which no path is; such
        /// segments are skipped with a log line rather than sampled absurd.
        ///
        /// Sampling is ADAPTIVE: a fixed count cannot hold a tolerance across
        /// the range of paths a real assembly holds (48 uniform samples left
        /// a live 0.9 m spline 78 um off its own mate vertex, corpus 17,
        /// 2026-08-23, and would leave a cable run far worse), so intervals
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
        /// segments as needed. Gaps beyond tolerance are logged and bridged:
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
        /// warning in the tree) is one the solver is not honouring faithfully:
        /// with an over-defined set, SolidWorks itself picks which mate to
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
                // anything: dead weight left behind by suppressed or
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
        /// The trap: IMate2.MinimumVariation/MaximumVariation are RELATIVE,
        /// "Minimum_variation = minimum_value - dimension_value" (API help,
        /// IMate2~MinimumVariation.html), while the manifest contract wants
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
                // else is metres, and that is what this code assumes: a live
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
                // A lead's handedness is chirality: independent of which way
                // the joint axis points, so no axis-sense term belongs here.
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
        /// The "lock rotation" tick, which CONCENTRIC and PROFILECENTER both
        /// carry. It lives NOWHERE in the entity params: live corpus 05
        /// (2026-08-22): planar4 (unlocked) and planar5 (locked) export
        /// byte-identical raw entities, so the feature data is the only
        /// source.
        ///
        /// A locked concentric is not a pin: it kills the spin as well as the
        /// tilt, leaving only the axial slide, and with any face contact the
        /// pair is one body. Live ClampRig (2026-08-24): the ram's
        /// grease nipples, dowty seals and BSP adapters are each held by one
        /// locked concentric plus one coincident, are fully defined in
        /// SolidWorks, and spun in Blender because this flag was read for
        /// profile-centre mates only.
        /// </summary>
        private static void ReadLockRotation(IFeature feat, int type, GraphMate gm, Action<string> log)
        {
            string kind;
            if (type == (int)swMateType_e.swMatePROFILECENTER)
            {
                var pc = SafeDefinition(feat) as IProfileCenterMateFeatureData;
                if (pc == null) return;
                try { gm.LockRotation = pc.LockRotation; } catch { }
                kind = "profile-centre";
            }
            else if (type == (int)swMateType_e.swMateCONCENTRIC)
            {
                var cc = SafeDefinition(feat) as IConcentricMateFeatureData;
                if (cc == null) return;
                try { gm.LockRotation = cc.LockRotation; } catch { }
                kind = "concentric";
            }
            else return;

            if (log != null && gm.LockRotation)
                log(kind + " " + gm.FeatureName + " LockRotation=True");
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
            // invisible in the manifest, only this line can show it.
            var diag = new StringBuilder();
            diag.Append("mate ").Append(gm.FeatureName)
                .Append(" [").Append(gm.TypeName).Append("]");
            // Everything a replay needs that the entities do not carry. A
            // suppressed mate is logged like any other, and a limit's range
            // is what makes it a limit: without these on the line, a log
            // replayed through the engine has to GUESS which of a
            // configuration's alternative mates was live (live TongRig,
            // 2026-09-14: four contradictory mates on one hydraulic cylinder).
            if (gm.Suppressed) diag.Append(" suppressed");
            if (gm.MinimumVariation != gm.MaximumVariation || !double.IsNaN(gm.CurrentValue))
                diag.Append(" range=[")
                    .Append(gm.MinimumVariation.ToString("G6", CultureInfo.InvariantCulture)).Append(',')
                    .Append(gm.MaximumVariation.ToString("G6", CultureInfo.InvariantCulture)).Append(',')
                    .Append(gm.CurrentValue.ToString("G6", CultureInfo.InvariantCulture)).Append(']');

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
                    // get one recorded. Everything else carries FILLER there:
                    // live corpus 04 (2026-08-22): coordinate-system mate
                    // entities arrived kind-unknown with direction (1,0,0),
                    // which downstream code then trusted as a joint axis.
                    bool directional = kind == (int)swMateEntityTypes_e.swMateLine
                                    || (kind == (int)swMateEntityTypes_e.swMatePoint
                                        && sel == (int)swSelectType_e.swSelDATUMAXES)
                                    || kind == (int)swMateEntityTypes_e.swMatePlane
                                    || kind == (int)swMateEntityTypes_e.swMateCylinder
                                    || kind == (int)swMateEntityTypes_e.swMateCone
                                    || kind == (int)swMateEntityTypes_e.swMateCircle;
                    // Parallel and perpendicular mates are ABOUT directions:
                    // SolidWorks records the measured normal in the direction
                    // slots even when it types the entity as a point, live
                    // corpus 06 parallelogram3 (2026-08-22): plane-face
                    // parallel mates arrived as point(1) carrying the face
                    // normal. The corpus-04 filler trap does not apply here;
                    // for these mate types the direction IS the mate.
                    if (gm.TypeValue == (int)swMateType_e.swMatePARALLEL
                        || gm.TypeValue == (int)swMateType_e.swMatePERPENDICULAR)
                        directional = true;
                    // A rack-pinion mate is about a direction too: the rack
                    // side is the edge the rack travels along, and
                    // SolidWorks records that edge's direction even where it
                    // types the entity as a point (live "rack and pinion",
                    // the 2022 MechanicalMates sample, 2026-09-16:
                    // point(1/1) carrying a clean (0,-1,0)). Without it the
                    // rack has a coupling and no slide to apply it to.
                    if (gm.TypeValue == (int)swMateType_e.swMateRACKPINION)
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
                // face whose component this reader failed to map" (a bug):
                // live corpus 07 (2026-08-22) was undiagnosable without it.
                if (resolved[i] == null && i < refNames.Count && refNames[i] != null)
                    diag.Append("[!").Append(refNames[i]).Append(']');
                if (p != null)
                {
                    // Round-trip precision, not five figures: this line is
                    // what a replay rebuilds the mate graph from (LogReplay
                    // in the tests), and five figures put a mirrored plane
                    // 3 um off its reflection (live TongRig, 2026-09-14).
                    diag.Append(" raw=[");
                    for (int k = 0; k < p.Length; k++)
                    {
                        if (k > 0) diag.Append(',');
                        diag.Append(p[k].ToString("R", CultureInfo.InvariantCulture));
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
        /// corpus 16 pt2, 2026-08-23: the vertex-on-edge slide direction
        /// vanished and the pair became a ball at the edge's endpoint), and
        /// a 3D-sketch segment arrives kind-13 with all-zero params (pt4 /
        /// path1). The underlying curve is still reachable through
        /// IMateEntity2.Reference: the same route the path-mate sampler
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
        /// feature's own selection list: real faces, whose surfaces know
        /// what they are, can straighten it out:
        ///
        ///  * swMateEntityTypes_e declares a Sphere member but SolidWorks
        ///    never emits it: a live spherical-face concentric (corpus 04,
        ///    2026-08-22) arrived as cone(5), sphere centre in the point
        ///    slots and a FILLER direction the classifier trusted as an
        ///    axis. Sphere-surfaced cone entities retype "sphere",
        ///    direction dropped.
        ///  * A CONICAL face arrives as a CIRCLE entity (live corpus 15,
        ///    2026-08-23: every cone concentric/tangent came in edge(7/2))
        ///    with the HALF-ANGLE in the radius slot. Cone-surfaced
        ///    entities retype "cone", keep their axis, and carry the
        ///    surface's half-angle: the number a tangent-on-plane
        ///    decomposition needs for its tilted-axis gate.
        ///  * A face a POINT is coincident with is a case of its own: the
        ///    contact leaves five DOF whatever the surface is, and only the
        ///    plane and the cylinder have a carrier primitive for it. Every
        ///    other face (torus, sphere, cone, fillet, loft, B-surface)
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
                        + gm.TypeName + "]: surface retype skipped");
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
                            // angle, 8 doubles (API help, ISurface~
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
        /// is coarsened rather than dropped: the patch is a contact surface,
        /// not a render mesh, and the alternative is no joint at all.</summary>
        private const int MaxPatchTriangles = 20000;

        /// <summary>
        /// Reads a face's triangulation onto the entity, welded and lifted to
        /// assembly space. GetTessTriangles hands back nine loose floats per
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
            raw = TessTriangles(face);
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
            AppendTessellation(raw, lift, points, index, tris);
            if (tris.Count == 0) return;

            ge.EntityTypeName = "surface";
            ge.SurfacePoints = points.ToArray();
            ge.SurfaceTriangles = tris.ToArray();
            if (log != null)
                log("surface patch on " + gm.FeatureName + " @" + ge.ComponentId
                    + ": " + tris.Count + " triangle(s), " + points.Count + " point(s)");
        }

        /// <summary>The cam path's faces. First choice: the feature data's
        /// cam path list, which holds every face of the path. The interop
        /// hands it back as an array, or as one object for a single face,
        /// and the read is logged either way. Fallback: the mate entity on
        /// the cam side, which is the path's first face only.</summary>
        private static List<object> CamPathFaces(IFeature feat, GraphMate gm, Action<string> log)
        {
            var faces = new List<object>();
            object def = SafeDefinition(feat);
            var data = def as ICamFollowerMateFeatureData;
            if (data == null)
            {
                if (log != null)
                    log("cam mate " + gm.FeatureName + ": the feature definition is "
                        + (def == null ? "null" : "not cam-follower data"));
            }
            else
            {
                object raw = null;
                try
                {
                    raw = data.EntitiesToMate[(int)swCamMateEntityType_e.swCamMateEntityType_CamPath];
                }
                catch (Exception ex)
                {
                    if (log != null)
                        log("cam mate " + gm.FeatureName + ": EntitiesToMate threw " + ex.Message);
                }
                var items = raw as object[];
                if (items != null)
                {
                    foreach (var item in items) if (item != null) faces.Add(item);
                }
                else if (raw != null)
                {
                    faces.Add(raw);
                }
                if (log != null)
                    log("cam mate " + gm.FeatureName + ": feature data cam path = "
                        + (raw == null ? "null" : items != null ? items.Length + " item(s)" : "one object")
                        + ", " + faces.FindAll(f => f is IFace2).Count + " face(s)");
            }
            if (faces.Count > 0) return faces;

            // The mate entities' own references: one face of the path.
            IMate2 mate = null;
            try { mate = feat.GetSpecificFeature2() as IMate2; } catch { }
            if (mate == null) return faces;
            int count = 0;
            try { count = mate.GetMateEntityCount(); } catch { }
            for (int i = 0; i < count; i++)
            {
                IMateEntity2 me = null;
                try { me = mate.MateEntity(i) as IMateEntity2; } catch { }
                if (me == null) continue;
                object reference = null;
                try { reference = me.Reference; } catch { }
                if (reference is IFace2)
                {
                    faces.Add(reference);
                    if (log != null)
                        log("cam mate " + gm.FeatureName + ": using mate entity " + i
                            + "'s face as the cam path (the feature data listed none)");
                    break;
                }
            }
            return faces;
        }

        /// <summary>Chord tolerance for a cam path, as a fraction of the
        /// path's size. The display tessellation of the live cam-follower2
        /// cam (76 mm radius) was 62 triangles, a sag of about 0.3 mm, and a
        /// follower sat on it is off by the sag.</summary>
        private const double CamChordFraction = 1e-4;

        /// <summary>
        /// Replaces the display triangles of a cam path with the cam body
        /// tessellated at a chord tolerance chosen here, keeping only the path
        /// faces' facets. The body's frame is not documented for a face read
        /// in assembly context, so the finer mesh is placed whichever way
        /// (lifted by the component or not) puts its bounding box on the
        /// display triangles already placed, and is refused when neither
        /// does. The display triangles stay otherwise.
        /// </summary>
        private static void FineCamPath(
            List<IFace2> pathFaces, double[,] lift, GraphMate gm, Action<string> log)
        {
            if (pathFaces.Count == 0 || gm.CamSurfacePoints == null) return;
            IBody2 body = null;
            try { body = pathFaces[0].GetBody() as IBody2; } catch { }
            if (body == null)
            {
                if (log != null) log("cam mate " + gm.FeatureName + ": no body for a fine tessellation");
                return;
            }
            double[] lo, hi;
            Bounds(gm.CamSurfacePoints, out lo, out hi);
            double size = Math.Sqrt(MathOps.Distance2(lo, hi));
            double tolerance = Math.Max(size * CamChordFraction, 2e-6);

            var mesh = new MeshDefinition();
            int added;
            try
            {
                added = BodyTessellator.AppendFaces(body, mesh, tolerance, f =>
                {
                    foreach (var p in pathFaces)
                    {
                        try { if (ReferenceEquals(p, f) || p.IsSame(f)) return true; }
                        catch { }
                    }
                    return false;
                }, log);
            }
            catch (Exception ex)
            {
                if (log != null) log("cam mate " + gm.FeatureName + ": fine tessellation failed: " + ex.Message);
                return;
            }
            if (added == 0 || added > MaxCamTriangles)
            {
                if (log != null)
                    log("cam mate " + gm.FeatureName + ": fine tessellation gave " + added
                        + " facet(s); the display triangles stay");
                return;
            }

            // Only the vertices the kept facets use, in both placements.
            var remap = new Dictionary<int, int>();
            var raw = new List<double[]>();
            var tris = new List<int[]>();
            for (int t = 0; t + 2 < mesh.Triangles.Count; t += 3)
            {
                var tri = new int[3];
                for (int k = 0; k < 3; k++)
                {
                    int v = mesh.Triangles[t + k];
                    int at;
                    if (!remap.TryGetValue(v, out at))
                    {
                        at = raw.Count;
                        remap[v] = at;
                        raw.Add(new[] { mesh.Positions[v * 3], mesh.Positions[v * 3 + 1], mesh.Positions[v * 3 + 2] });
                    }
                    tri[k] = at;
                }
                tris.Add(tri);
            }
            var lifted = new List<double[]>(raw.Count);
            foreach (var p in raw) lifted.Add(lift == null ? p : SwFrames.LiftPoint(lift, p));

            double[] rlo, rhi, llo, lhi;
            Bounds(raw.ToArray(), out rlo, out rhi);
            Bounds(lifted.ToArray(), out llo, out lhi);
            double rawOff = Math.Sqrt(MathOps.Distance2(rlo, lo)) + Math.Sqrt(MathOps.Distance2(rhi, hi));
            double liftOff = Math.Sqrt(MathOps.Distance2(llo, lo)) + Math.Sqrt(MathOps.Distance2(lhi, hi));
            // The display mesh sags inside the true surface, so the boxes
            // differ by about the sag; anything past a percent of the size
            // is a wrong frame.
            double allowed = Math.Max(size * 0.01, 1e-4);
            List<double[]> chosen = liftOff <= rawOff ? lifted : raw;
            double off = Math.Min(liftOff, rawOff);
            if (off > allowed)
            {
                if (log != null)
                    log("cam mate " + gm.FeatureName + ": fine tessellation lands "
                        + (off * 1000).ToString("F2", CultureInfo.InvariantCulture)
                        + " mm off the display triangles either way; the display triangles stay");
                return;
            }
            gm.CamSurfacePoints = chosen.ToArray();
            gm.CamSurfaceTriangles = tris.ToArray();
            if (log != null)
                log("cam mate " + gm.FeatureName + ": fine cam path "
                    + tris.Count + " triangle(s) at "
                    + (tolerance * 1000).ToString("F3", CultureInfo.InvariantCulture) + " mm chord, "
                    + (ReferenceEquals(chosen, lifted) ? "lifted" : "already in assembly space")
                    + ", box within " + (off * 1000).ToString("F3", CultureInfo.InvariantCulture) + " mm");
        }

        private static void Bounds(double[][] pts, out double[] lo, out double[] hi)
        {
            lo = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
            hi = new[] { double.MinValue, double.MinValue, double.MinValue };
            foreach (var p in pts)
                for (int k = 0; k < 3; k++)
                {
                    lo[k] = Math.Min(lo[k], p[k]);
                    hi[k] = Math.Max(hi[k], p[k]);
                }
        }

        private static void ResolveLightweight(IMate2 mate, GraphMate gm, Action<string> log)
        {
            int count = 0;
            try { count = mate.GetMateEntityCount(); } catch { }
            for (int i = 0; i < count; i++)
            {
                Component2 comp = null;
                try
                {
                    var me = mate.MateEntity(i) as IMateEntity2;
                    comp = me == null ? null : me.ReferenceComponent as Component2;
                }
                catch { }
                if (comp == null) continue;
                int state = -1;
                try { state = comp.GetSuppression(); } catch { }
                if (state != (int)swComponentSuppressionState_e.swComponentLightweight
                    && state != (int)swComponentSuppressionState_e.swComponentFullyLightweight)
                    continue;
                bool ok = false;
                try
                {
                    comp.SetSuppression2((int)swComponentSuppressionState_e.swComponentFullyResolved);
                    ok = true;
                }
                catch { }
                if (log != null)
                    log("cam mate " + gm.FeatureName + ": resolved lightweight " + SafeName(comp)
                        + (ok ? "" : " (failed)"));
            }
        }

        /// <summary>A face's display triangles, nine coordinates per
        /// triangle. The interop hands GetTessTriangles back as a float
        /// array (BodyTessellator reads it that way), so a cast to double[]
        /// is always null: the cam path and every surface patch read
        /// nothing until 2026-09-15 (live cam-follower2).</summary>
        private static double[] TessTriangles(IFace2 face)
        {
            object o = null;
            try { o = face.GetTessTriangles(true); } catch { }
            var d = o as double[];
            if (d != null) return d;
            var f = o as float[];
            if (f == null) return null;
            var r = new double[f.Length];
            for (int i = 0; i < f.Length; i++) r[i] = f[i];
            return r;
        }

        /// <summary>Most faces one cam path may hold. A real profile has a
        /// handful to a few dozen.</summary>
        private const int MaxCamFaces = 400;

        /// <summary>
        /// The whole cam path from the face the feature data names. The cam
        /// mate stores only the face that was picked, and SolidWorks carries
        /// the contact on across the rest of the profile (live cam-follower2,
        /// 2026-09-15: every mate named one face of a profile of arcs and
        /// flats). A cam path is an extrusion, so the rest of it is every
        /// face reachable across edges whose normals are all perpendicular
        /// to the extrusion direction. The top and bottom faces are not (their
        /// normals lie along it), so the walk stops there and never reaches
        /// the bore.
        /// </summary>
        private static List<object> ExtrudedPath(List<object> start, GraphMate gm, Action<string> log)
        {
            var seed = start.Count > 0 ? start[0] as IFace2 : null;
            if (seed == null) return start;
            var dir = ExtrusionDirection(seed, gm, log);
            if (dir == null)
            {
                if (log != null)
                    log("cam mate " + gm.FeatureName + ": no extrusion direction from the picked face; "
                        + "the cam path is that face alone");
                return start;
            }
            var result = new List<object>();
            var seen = new List<IFace2>();
            var queue = new Queue<IFace2>();
            foreach (var o in start)
            {
                var f = o as IFace2;
                if (f == null || Seen(seen, f)) continue;
                seen.Add(f);
                queue.Enqueue(f);
                result.Add(f);
            }
            while (queue.Count > 0 && result.Count < MaxCamFaces)
            {
                foreach (var next in Neighbours(queue.Dequeue()))
                {
                    if (Seen(seen, next)) continue;
                    seen.Add(next);
                    if (!AllPerpendicular(next, dir)) continue;
                    result.Add(next);
                    queue.Enqueue(next);
                }
            }
            if (log != null)
                log("cam mate " + gm.FeatureName + ": cam path spread from " + start.Count
                    + " picked face(s) to " + result.Count + " along the extrusion");
            return result;
        }

        private static bool Seen(List<IFace2> seen, IFace2 f)
        {
            foreach (var s in seen)
                if (ReferenceEquals(s, f) || s.IsSame(f)) return true;
            return false;
        }

        private static IEnumerable<IFace2> Neighbours(IFace2 face)
        {
            object[] edges = null;
            try { edges = face.GetEdges() as object[]; } catch { }
            if (edges == null) yield break;
            foreach (var e in edges)
            {
                var edge = e as IEdge;
                if (edge == null) continue;
                object[] two = null;
                try { two = edge.GetTwoAdjacentFaces2() as object[]; } catch { }
                if (two == null) continue;
                foreach (var t in two)
                {
                    var f = t as IFace2;
                    if (f != null && !ReferenceEquals(f, face) && !f.IsSame(face)) yield return f;
                }
            }
        }

        /// <summary>A face's display normals as unit vectors, in its own
        /// part's frame (directions only: no lift needed to compare them).
        /// </summary>
        private static List<double[]> TessNormals(IFace2 face)
        {
            var list = new List<double[]>();
            object o = null;
            try { o = face.GetTessNorms(); } catch { }
            var f = o as float[];
            var d = o as double[];
            int n = f != null ? f.Length : d != null ? d.Length : 0;
            for (int i = 0; i + 2 < n; i += 3)
            {
                double x = f != null ? f[i] : d[i];
                double y = f != null ? f[i + 1] : d[i + 1];
                double z = f != null ? f[i + 2] : d[i + 2];
                double len = Math.Sqrt(x * x + y * y + z * z);
                if (len > 1e-9) list.Add(new[] { x / len, y / len, z / len });
            }
            return list;
        }

        private static bool AllParallel(IFace2 face, double[] dir)
        {
            var normals = TessNormals(face);
            if (normals.Count == 0) return false;
            foreach (var n in normals)
                if (Math.Abs(n[0] * dir[0] + n[1] * dir[1] + n[2] * dir[2]) < 0.9998) return false;
            return true;
        }

        private static bool AllPerpendicular(IFace2 face, double[] dir)
        {
            var normals = TessNormals(face);
            if (normals.Count == 0) return false;
            foreach (var n in normals)
                if (Math.Abs(n[0] * dir[0] + n[1] * dir[1] + n[2] * dir[2]) > 0.02) return false;
            return true;
        }

        /// <summary>The extrusion direction of a cam path face. A curved face
        /// gives it from its own normals (the cross product of the two that
        /// differ most). A flat face has one normal, so its neighbours vote:
        /// each neighbour's normal crossed with the flat's normal is a
        /// candidate, and the candidate the most neighbours are wholly
        /// perpendicular to wins (a profile neighbour's candidate is the
        /// extrusion direction, and the other profile neighbours agree; a
        /// top face's candidate lies in the profile plane and only the bottom
        /// face agrees).</summary>
        private static double[] ExtrusionDirection(IFace2 seed, GraphMate gm, Action<string> log)
        {
            var normals = TessNormals(seed);
            if (normals.Count == 0) return null;
            double[] a = normals[0], b = null;
            double least = 1.0;
            foreach (var n in normals)
            {
                double dot = Math.Abs(a[0] * n[0] + a[1] * n[1] + a[2] * n[2]);
                if (dot < least) { least = dot; b = n; }
            }
            if (b != null && least < Math.Cos(Math.PI / 90))
            {
                var curved = Unit(Cross(a, b));
                if (log != null)
                    log("cam mate " + gm.FeatureName + ": curved seed, " + normals.Count
                        + " normal(s), extrusion " + Fmt(curved));
                return curved;
            }

            double[] best = null;
            int bestVotes = 0;
            var neighbours = new List<IFace2>(Neighbours(seed));
            foreach (var nb in neighbours)
            {
                // The neighbour's normal that differs most from the flat's:
                // a tangent neighbour shares the flat's normal along their
                // common edge, and that one crosses to nothing.
                var nn = TessNormals(nb);
                double[] far = null;
                double farDot = 1.0;
                foreach (var n in nn)
                {
                    double dot = Math.Abs(a[0] * n[0] + a[1] * n[1] + a[2] * n[2]);
                    if (dot < farDot) { farDot = dot; far = n; }
                }
                if (far == null || farDot > 0.9995) continue;
                var c = Unit(Cross(a, far));
                if (c == null) continue;
                // Every neighbour of an extruded face lies either across
                // the extrusion (the profile) or along it (the end caps). A
                // direction in the profile plane has the end caps across it
                // too, but the profile's arcs lie neither way, which rules
                // it out (live cam-follower2, 2026-09-15: it tied the true
                // direction on votes alone and took the caps).
                int votes = 0;
                bool consistent = true;
                foreach (var other in neighbours)
                {
                    if (AllPerpendicular(other, c)) votes++;
                    else if (!AllParallel(other, c)) consistent = false;
                }
                if (log != null)
                    log("cam mate " + gm.FeatureName + ": flat seed, candidate " + Fmt(c)
                        + " has " + votes + " of " + neighbours.Count + " neighbour(s) across it"
                        + (consistent ? "" : ", and a neighbour lying neither way"));
                if (consistent && votes > bestVotes) { bestVotes = votes; best = c; }
            }
            return best;
        }

        private static string Fmt(double[] v)
        {
            return v == null ? "null" : "[" + v[0].ToString("F3", CultureInfo.InvariantCulture) + ","
                + v[1].ToString("F3", CultureInfo.InvariantCulture) + ","
                + v[2].ToString("F3", CultureInfo.InvariantCulture) + "]";
        }

        private static double[] Cross(double[] a, double[] b)
        {
            return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        }

        private static double[] Unit(double[] v)
        {
            double len = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            return len < 1e-6 ? null : new[] { v[0] / len, v[1] / len, v[2] / len };
        }

        private static string SafeName(Component2 comp)
        {
            try { return comp.Name2; } catch { return "?"; }
        }

        /// <summary>Welds one face's nine-doubles-per-triangle tessellation
        /// onto a shared point list, lifted to assembly space. Welding on the
        /// exact bits is enough: the duplicates are one tessellator writing
        /// the same corner repeatedly, not two computations that might
        /// disagree in the last place.</summary>
        private static void AppendTessellation(
            double[] raw, double[,] lift, List<double[]> points,
            Dictionary<string, int> index, List<int[]> tris)
        {
            int triangles = raw.Length / 9;
            for (int t = 0; t < triangles; t++)
            {
                var corner = new int[3];
                for (int c = 0; c < 3; c++)
                {
                    int at = t * 9 + c * 3;
                    var p = new[] { raw[at], raw[at + 1], raw[at + 2] };
                    if (lift != null) p = SwFrames.LiftPoint(lift, p);
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
        }

        /// <summary>
        /// Second chance for entities whose ReferenceComponent did not map to
        /// a walked component: the mate feature's own selection list still
        /// holds the real faces, and a face's IEntity knows its component.
        /// Live corpus 07 (2026-08-22): every entity of a flexible
        /// subassembly's internal mates came back unresolved, so the hinge
        /// joint vanished and the leaf exported as a disconnected island.
        /// Selection order is assumed to match mate-entity order: for the
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
        /// The mate's selection list: the faces and edges the user picked,
        /// which is where the retype passes read real surface geometry.
        ///
        /// EntitiesToMate is declared SEPARATELY on each *MateFeatureData
        /// interface; the base IMateFeatureData has no such member, so every
        /// type needs its own cast. A missing one fails SILENTLY (the retype
        /// passes just never run): live corpus 15 cone3 (2026-08-23) went out
        /// as a free joint for exactly that reason, TANGENT was absent, so
        /// the conical face kept its circle typing and lost the half-angle.
        /// All 17 interfaces that declare the member are listed here
        /// (reflected over the 2022 interop); the caller logs a miss.
        ///
        /// Three of them index the member by GROUP rather than returning one
        /// list: a hinge's concentric, coincident and angle picks; a cam's
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
        /// residence only when every resolved entity sits STRICTLY below it:
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
        /// IMateEntity2.ReferenceType names the geometry (swMateEntityTypes_e:
        /// the names the EntityParams table on its API help page keys on);
        /// ReferenceType2 (swSelectType_e) only refines a line into an axis
        /// or an edge. A circle entity is a circular edge and gets "edge":
        /// its direction and radius survive either way. A selected VERTEX
        /// arrives with ReferenceType 0 (live corpus 13/16, 2026-08-23:
        /// every vertex mate came in unknown(0/3)), so the selection type is
        /// what names it: the point slots are real, the direction slots are
        /// filler.
        /// </summary>
        private static string EntityKind(int mateEntityType, int selectType)
        {
            switch (mateEntityType)
            {
                case (int)swMateEntityTypes_e.swMatePoint:
                    // A datum AXIS arrives typed as a point with the axis
                    // direction in the direction slots (live cam-follower
                    // sample, 2026-09-15: the cam's axis coincident with two
                    // assembly planes came in as point(1/5)). The selection
                    // type is what tells it from a real point.
                    return selectType == (int)swSelectType_e.swSelDATUMAXES
                        ? "axis" : "point";
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
