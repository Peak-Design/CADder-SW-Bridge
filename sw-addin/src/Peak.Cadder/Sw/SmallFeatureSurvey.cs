using System;
using System.Collections.Generic;
using System.Globalization;
using SolidWorks.Interop.sldworks;

namespace Peak.Cadder.Sw
{
    /// <summary>
    /// Counts the small features a body could be sent without, and the
    /// triangles that would save. Reads only: nothing here touches the
    /// model, and no feature is written into anyone's part document.
    ///
    /// A plate with fifty bolt holes costs far more triangles for the holes
    /// than for the plate, and a model going to a game engine does not want
    /// them: the bolts are modelled and the holes are not visible (Oscar,
    /// 2026-09-16). SolidWorks patches a hole properly with Delete Face or
    /// the Defeature tool, and both are modelling operations that solve,
    /// fail on awkward geometry, and edit the user's files. This does not
    /// need them, because the add-in tessellates FACE BY FACE and so
    /// decides what each face contributes.
    ///
    /// Four rules make it general and safe.
    ///
    /// The unit is a small INNER LOOP of a face, not a cylinder. That
    /// covers round holes, slots, keyways and small cutouts alike, with
    /// less code than a cylinder classifier. Flat faces always count.
    /// Curved ones count when the caller asks, which is what reaches a hole
    /// drilled into a boss or a shaft.
    ///
    /// The feature behind a loop is found by walking INWARD: cross into the
    /// face on the other side, then keep crossing every edge that is not
    /// itself on a marked loop. A through hole gives one cylinder bounded
    /// by two marked loops; a blind hole gives the cylinder and its bottom;
    /// a counterbore gives cylinder, annulus, cylinder and bottom.
    ///
    /// The region is removed only when its WHOLE boundary is marked loops.
    /// A hole running into a fillet, a hole breaking the silhouette, a
    /// thread, anything odd: the walk escapes and the feature stays. There
    /// is no repair step and nothing to fail, only "simplified" and "left
    /// alone", and the counts of each are reported so the result can be
    /// trusted.
    ///
    /// Every rim that goes is dealt with on BOTH its faces. The one inside
    /// the feature goes with it; the one outside is rebuilt without the rim
    /// when it is flat, and keeps its own triangles with the rim capped when
    /// it is not. A rim left on a surviving face is a hole in the part, so
    /// BodyTessellator counts the open edges of what this plan produces and
    /// throws the whole plan away rather than send one.
    /// </summary>
    public static class SmallFeatureSurvey
    {
        /// <summary>A walk that reaches this many faces has not found a
        /// pocket, it has found its way out into the body. Abandon it, so a
        /// pathological shape cannot cost the export.</summary>
        private const int FaceBudget = 24;

        /// <summary>How near two loops must agree before they count as the
        /// same loop seen from its two faces. They share their edges, so
        /// they agree exactly bar floating point.</summary>
        private const double SameLoop = 1e-9;

        /// <summary>
        /// One small loop that a feature is taken out through: where it is,
        /// how wide, and the EDGES it is made of.
        ///
        /// The edges are what say which faces have to deal with it. A loop
        /// key cannot: the same rim can be a loop of its own on the flat face
        /// it breaks into and part of a longer loop on the cylinder beside
        /// it, so matching whole loops left a cylinder holding a rim nobody
        /// covered, and the part came back open (drum_pedal bolts, 68 edges).
        /// </summary>
        private sealed class Rim
        {
            public string Key;
            public double[] Centre;
            public double Extent;
            public List<IEdge> Edges = new List<IEdge>();
        }

        public sealed class Feature
        {
            public double Extent;            // metres, the widest marked loop
            public int Loops;                // marked loops on its boundary
            public int Faces;                // faces in the region
            public int Facets;               // triangles those faces cost now
            public string Declined;          // null when it would be removed
            /// <summary>The faces behind the loop. Empty when declined.</summary>
            public List<IFace2> Region = new List<IFace2>();
        }

        public sealed class Result
        {
            public int Faces;
            public int PlanarFaces;
            public int Facets;               // the body's triangles as it is
            public int FilledFaces;          // planar faces needing a new fill
            public int FilledFacetsBefore;   // what those faces cost now
            public int FilledFacetsAfter;    // what the fill really costs
            /// <summary>Faces that would have been refilled and could not
            /// be. They keep the triangles they had, holes and all.</summary>
            public int FillRefused;
            /// <summary>Curved faces that keep their own triangles and gain a
            /// lid over the rim, and what those lids cost.</summary>
            public int CappedFaces;
            public int CapFacets;
            /// <summary>The worst a fill's area missed the area it replaced
            /// less the holes left out, as a fraction of the face. Zero bar
            /// rounding is the only right answer, and it is checked on every
            /// face of every part rather than on a fixture.</summary>
            public double WorstAreaSlip;
            public string WorstAreaWhere = "";
            public List<Feature> Features = new List<Feature>();

            public int Removed
            {
                get
                {
                    int n = 0;
                    foreach (var f in Features) if (f.Declined == null) n++;
                    return n;
                }
            }

            public int Declined { get { return Features.Count - Removed; } }

            /// <summary>Triangles after: everything except the removed
            /// regions, with the faces that owned their loops re-filled.</summary>
            public int FacetsAfter
            {
                get
                {
                    int saved = 0;
                    foreach (var f in Features) if (f.Declined == null) saved += f.Facets;
                    return Facets - saved - FilledFacetsBefore + FilledFacetsAfter
                        + CapFacets;
                }
            }
        }

        /// <summary>
        /// What a body would be sent without, as faces rather than as
        /// numbers: the ones inside a removed feature, which go, and the
        /// planar ones that owned their loops, which are rebuilt from their
        /// own boundary with those loops left out.
        /// </summary>
        public sealed class Plan
        {
            public List<IFace2> Gone = new List<IFace2>();
            public List<IFace2> Fill = new List<IFace2>();
            public List<List<PlaneRefill.Hole>> FillHoles =
                new List<List<PlaneRefill.Hole>>();
            /// <summary>Curved faces that owned a loop. These keep their own
            /// triangles and have the rim capped: see SurfaceCap.</summary>
            public List<IFace2> Cap = new List<IFace2>();
            public List<List<PlaneRefill.Hole>> CapHoles =
                new List<List<PlaneRefill.Hole>>();
            public List<Feature> Features = new List<Feature>();
            public int Faces;
            public int PlanarFaces;

            public int Removed
            {
                get
                {
                    int n = 0;
                    foreach (var f in Features) if (f.Declined == null) n++;
                    return n;
                }
            }

            public int Declined { get { return Features.Count - Removed; } }

            /// <summary>Whether the plan asks for anything at all.</summary>
            public bool Any
            {
                get { return Gone.Count > 0 || Fill.Count > 0 || Cap.Count > 0; }
            }

            public int FillAt(IFace2 face)
            {
                for (int i = 0; i < Fill.Count; i++)
                    if (Same(Fill[i], face)) return i;
                return -1;
            }

            public int CapAt(IFace2 face)
            {
                for (int i = 0; i < Cap.Count; i++)
                    if (Same(Cap[i], face)) return i;
                return -1;
            }

            public bool IsGone(IFace2 face) { return Contains(Gone, face); }
        }

        /// <summary>
        /// Works out what a body could be sent without. maxExtent is the size
        /// under which an inner loop counts as small, in metres. Reads the
        /// body's topology only: no tessellation, and nothing is changed.
        ///
        /// curved also marks a loop on a face that is not flat, which is how
        /// a hole drilled into a boss or a shaft is reached. The owner face
        /// then keeps its own triangles and the rim is capped, because a
        /// curved face rebuilt from its outline would come back flat.
        /// </summary>
        public static Plan Choose(
            IBody2 body, double maxExtent, Action<string> log, bool curved = false)
        {
            var plan = new Plan();
            if (body == null) return plan;

            object[] faces = null;
            try { faces = body.GetFaces() as object[]; }
            catch (Exception ex)
            {
                if (log != null) log("small features: GetFaces failed: " + ex.Message);
                return plan;
            }
            if (faces == null) return plan;

            var marked = new HashSet<string>();
            var rims = new Dictionary<string, Rim>();
            var owners = new List<KeyValuePair<IFace2, ILoop2>>();
            foreach (var o in faces)
            {
                var face = o as IFace2;
                if (face == null) continue;
                plan.Faces++;
                bool plane = IsPlane(face);
                if (plane) plan.PlanarFaces++;
                if (!plane && !curved) continue;
                foreach (var loop in LoopsOf(face))
                {
                    bool outer;
                    try { outer = loop.IsOuter(); }
                    catch { continue; }
                    if (outer) continue;
                    double extent;
                    string key = LoopKey(loop, out extent);
                    if (key == null || extent > maxExtent) continue;
                    marked.Add(key);
                    if (!rims.ContainsKey(key))
                    {
                        var rim = new Rim
                        {
                            Key = key,
                            Extent = extent,
                            Centre = LoopCentre(loop),
                        };
                        foreach (var edge in EdgesOf(loop)) rim.Edges.Add(edge);
                        if (rim.Centre != null) rims[key] = rim;
                    }
                    owners.Add(new KeyValuePair<IFace2, ILoop2>(face, loop));
                }
            }
            if (owners.Count == 0) return plan;

            var claimed = new List<string>();
            foreach (var pair in owners)
            {
                double extent;
                string key = LoopKey(pair.Value, out extent);
                if (key == null || claimed.Contains(key)) continue;

                var feature = new Feature { Extent = extent };
                var region = new List<IFace2>();
                var bounds = new List<string>();
                string declined = Walk(pair.Key, pair.Value, marked, region, bounds, log);
                feature.Faces = region.Count;
                feature.Loops = bounds.Count;
                feature.Declined = declined;
                feature.Region = region;
                plan.Features.Add(feature);
                if (declined != null) continue;

                foreach (string b in bounds) if (!claimed.Contains(b)) claimed.Add(b);
                foreach (var f in region) if (!Contains(plan.Gone, f)) plan.Gone.Add(f);
            }

            // Every rim that goes has two faces. The one inside the feature
            // goes with it. The one OUTSIDE has to lose the rim as well, or
            // the body is left open where the feature was: the faces around
            // the hole still have triangles that reach its edge and now have
            // nothing on the other side.
            //
            // The faces are taken from the rim's EDGES, which is the only
            // thing that names them exactly. Two other ways were tried and
            // both left parts open. Taking the face the rim was found on
            // misses the case where a rim found from a flat face has a
            // cylinder on the other side. Sweeping the body for faces holding
            // a matching LOOP misses the case where the rim is a loop of its
            // own on one face and part of a longer loop on the next.
            //
            // It runs when every region is settled, so a face that is inside
            // one feature and outside another is seen for what it is. That is
            // a counterbore: its annulus is in the first region and carries
            // the rim of the second.
            //
            // A flat face is rebuilt from its own boundary, which is exact
            // and leaves the face simpler as well. A curved one keeps its
            // triangles, because they are what give it its shape, and has the
            // rim capped. Capping is not what the curved switch turns on: it
            // is what closing the body needs, whichever way the switch is set.
            foreach (string key in claimed)
            {
                Rim rim;
                if (!rims.TryGetValue(key, out rim)) continue;
                foreach (var edge in rim.Edges)
                    foreach (var face in BothSides(edge))
                    {
                        if (Contains(plan.Gone, face)) continue;
                        Cover(plan, face, rim);
                    }
            }
            return plan;
        }

        /// <summary>
        /// Surveys one body: the plan, plus what it would cost in triangles.
        /// tess may be null, in which case the triangle columns come back
        /// zero and only the counts are answered.
        /// </summary>
        public static Result Survey(
            IBody2 body, double maxExtent, ITessellation tess, Action<string> log,
            double tolerance = 0.0, bool curved = false)
        {
            var result = new Result();
            if (body == null) return result;

            object[] faces = null;
            try { faces = body.GetFaces() as object[]; }
            catch { return result; }
            if (faces == null) return result;

            var plan = Choose(body, maxExtent, log, curved);
            result.Faces = plan.Faces;
            result.PlanarFaces = plan.PlanarFaces;
            result.Facets = FacetsOfBody(tess, faces);
            foreach (var f in plan.Features)
            {
                f.Facets = FacetsOf(tess, f.Region);
                result.Features.Add(f);
            }
            var fillFaces = plan.Fill;

            // ── What the faces that lose their holes cost, before and after ─
            //
            // Not modelled: BUILT. The fill is made for real from the face's
            // own tessellated boundary and its triangles are counted, so the
            // saving reported is the saving there would be. Counting instead
            // of modelling also puts every face of the corpus through the
            // fill, which is a harder test of it than any fixture.
            //
            // A face the fill refuses keeps every triangle it had. That is
            // not a failure to report as a number missed: it is the whole
            // contract, simplified or left alone.
            foreach (var face in fillFaces)
            {
                int before = FacetsOf(tess, new List<IFace2> { face });
                result.FilledFaces++;
                result.FilledFacetsBefore += before;
                var report = new PlaneRefill.Report();
                var refill = PlaneRefill.Build(
                    face, tess, plan.FillHoles[plan.Fill.IndexOf(face)], log, report);
                if (refill == null)
                {
                    // It keeps its own triangles and the rims are capped
                    // instead, which is what closes the body.
                    result.FillRefused++;
                    result.FilledFacetsAfter += before;
                    var lid = SurfaceCap.Build(
                        face, tess, plan.FillHoles[plan.Fill.IndexOf(face)], log);
                    if (lid != null) result.CapFacets += lid.Count / 3;
                    continue;
                }
                result.FilledFacetsAfter += refill.Count / 3;
                // PLUS, not minus. A face's own triangles cover the face
                // WITHOUT its holes, so a hole left out is area the fill
                // gains. Written the other way round it accused every
                // correct fill on the corpus of being wrong by exactly
                // twice the hole.
                double want = Math.Abs(report.Before) + Math.Abs(report.Dropped);
                double slip = Math.Abs(Math.Abs(report.After) - want);
                if (Math.Abs(report.Before) > 0.0)
                    slip /= Math.Abs(report.Before);
                if (slip > result.WorstAreaSlip)
                {
                    result.WorstAreaSlip = slip;
                    result.WorstAreaWhere = string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "before {0:G6} after {1:G6} dropped {2:G6} loops {3} "
                        + "facets {4} -> {5}",
                        report.Before, report.After, report.Dropped, report.Loops,
                        before, refill.Count / 3);
                }
            }

            // A curved owner keeps every triangle it had and gains a lid, so
            // its cost is what the lid costs and nothing else.
            for (int i = 0; i < plan.Cap.Count; i++)
            {
                result.CappedFaces++;
                var lid = SurfaceCap.Build(plan.Cap[i], tess, plan.CapHoles[i], log);
                if (lid != null) result.CapFacets += lid.Count / 3;
            }
            return result;
        }

        // ── The inward walk ────────────────────────────────────────────────

        /// <summary>
        /// Collects the faces behind one marked loop. Returns null when the
        /// whole boundary of what it found is marked loops, and the reason
        /// it gave up otherwise.
        /// </summary>
        private static string Walk(
            IFace2 from, ILoop2 seed, HashSet<string> marked,
            List<IFace2> region, List<string> bounds, Action<string> log)
        {
            var queue = new Queue<IFace2>();
            foreach (var edge in EdgesOf(seed))
            {
                var next = Across(edge, from);
                if (next == null) return "an edge of the loop has no face behind it";
                if (Same(next, from)) return "the loop has the same face on both sides";
                if (!Contains(region, next)) { region.Add(next); queue.Enqueue(next); }
            }
            double _;
            string seedKey = LoopKey(seed, out _);
            if (seedKey != null) bounds.Add(seedKey);

            while (queue.Count > 0)
            {
                if (region.Count > FaceBudget)
                    return "the walk passed " + FaceBudget + " faces without closing";
                var face = queue.Dequeue();
                foreach (var loop in LoopsOf(face))
                {
                    double extent;
                    string key = LoopKey(loop, out extent);
                    if (key != null && marked.Contains(key))
                    {
                        if (!bounds.Contains(key)) bounds.Add(key);
                        continue;                       // a wall of the pocket
                    }
                    foreach (var edge in EdgesOf(loop))
                    {
                        var next = Across(edge, face);
                        if (next == null) return "an edge inside the feature has no face behind it";
                        // Back at the face the loop is on means the walk has
                        // gone round the OUTSIDE of the body, not into a
                        // pocket. Without this a nut block with too few faces
                        // to trip the budget came back as one feature holding
                        // every face it had, and the body simplified to
                        // nothing at all.
                        if (Same(next, from))
                            return "the walk came back to the face it started from";
                        if (!Contains(region, next))
                        {
                            if (region.Count >= FaceBudget)
                                return "the walk passed " + FaceBudget + " faces without closing";
                            region.Add(next);
                            queue.Enqueue(next);
                        }
                    }
                }
            }
            if (bounds.Count == 0) return "nothing bounded the region";
            return null;
        }

        /// <summary>Both faces of an edge, however many it can answer.</summary>
        private static IEnumerable<IFace2> BothSides(IEdge edge)
        {
            object[] pair = null;
            try { pair = edge.GetTwoAdjacentFaces2() as object[]; }
            catch { }
            foreach (var o in pair ?? new object[0])
            {
                var face = o as IFace2;
                if (face != null) yield return face;
            }
        }

        /// <summary>Notes that a surviving face has to lose this rim: rebuilt
        /// without it when it is flat, capped over it when it is not.</summary>
        private static void Cover(Plan plan, IFace2 face, Rim rim)
        {
            bool plane = IsPlane(face);
            var into = plane ? plan.Fill : plan.Cap;
            var holes = plane ? plan.FillHoles : plan.CapHoles;
            int at = -1;
            for (int i = 0; i < into.Count && at < 0; i++)
                if (Same(into[i], face)) at = i;
            if (at < 0)
            {
                into.Add(face);
                holes.Add(new List<PlaneRefill.Hole>());
                at = into.Count - 1;
            }
            foreach (var had in holes[at])
                if (had.Extent == rim.Extent && had.Centre[0] == rim.Centre[0]
                    && had.Centre[1] == rim.Centre[1]
                    && had.Centre[2] == rim.Centre[2]) return;
            holes[at].Add(new PlaneRefill.Hole
            {
                Centre = rim.Centre,
                Extent = rim.Extent,
                Edges = rim.Edges,
            });
        }

        /// <summary>The face on the other side of an edge, or null.</summary>
        private static IFace2 Across(IEdge edge, IFace2 from)
        {
            object[] pair = null;
            try { pair = edge.GetTwoAdjacentFaces2() as object[]; }
            catch { }
            if (pair == null || pair.Length < 2) return null;
            var a = pair[0] as IFace2;
            var b = pair[1] as IFace2;
            if (a == null || b == null) return a ?? b;
            return Same(a, from) ? b : a;
        }

        private static bool Same(IFace2 a, IFace2 b)
        {
            if (a == null || b == null) return false;
            if (ReferenceEquals(a, b)) return true;
            try { return a.IsSame(b); }
            catch { return false; }
        }

        private static bool Contains(List<IFace2> faces, IFace2 face)
        {
            foreach (var f in faces) if (Same(f, face)) return true;
            return false;
        }

        // ── Loops, edges and their extent ──────────────────────────────────

        private static IEnumerable<ILoop2> LoopsOf(IFace2 face)
        {
            object[] loops = null;
            try { loops = face.GetLoops() as object[]; }
            catch { }
            if (loops == null) yield break;
            foreach (var o in loops)
            {
                var loop = o as ILoop2;
                if (loop != null) yield return loop;
            }
        }

        private static IEnumerable<IEdge> EdgesOf(ILoop2 loop)
        {
            object[] edges = null;
            try { edges = loop.GetEdges() as object[]; }
            catch { }
            if (edges == null) yield break;
            foreach (var o in edges)
            {
                var edge = o as IEdge;
                if (edge != null) yield return edge;
            }
        }

        private static bool HasOuterLoopOnly(IFace2 face)
        {
            int n = 0;
            foreach (var loop in LoopsOf(face)) n++;
            return n == 1;
        }

        /// <summary>
        /// A name for a loop that its two faces both arrive at.
        ///
        /// The loop on the plate's face and the loop on the hole's wall are
        /// different topology objects holding the SAME edges, and there is
        /// no cheap way to ask SolidWorks whether two loops are the same
        /// one. Their geometry is the same to rounding, so the geometry is
        /// the name: how many edges, where the middle is, and how wide.
        /// </summary>
        private static string LoopKey(ILoop2 loop, out double extent)
        {
            extent = 0.0;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            int edges = 0;
            foreach (var edge in EdgesOf(loop))
            {
                edges++;
                foreach (var p in SamplePoints(edge))
                {
                    if (p[0] < minX) minX = p[0];
                    if (p[1] < minY) minY = p[1];
                    if (p[2] < minZ) minZ = p[2];
                    if (p[0] > maxX) maxX = p[0];
                    if (p[1] > maxY) maxY = p[1];
                    if (p[2] > maxZ) maxZ = p[2];
                }
            }
            if (edges == 0 || minX > maxX) return null;
            // The WIDEST SIDE of the box, not its diagonal. A dial that says
            // 12 mm has to mean a 12 mm hole, and the diagonal of a circle's
            // box is 1.414 times its diameter whichever way the circle
            // faces, so the diagonal made the dial mean 8.5 mm instead.
            // The widest side is the diameter exactly for a hole drilled
            // along an axis, never less than 0.82 of it for one drilled at
            // an angle, and the length of a slot rather than its diagonal.
            double dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
            extent = Math.Max(dx, Math.Max(dy, dz));
            return string.Format(
                CultureInfo.InvariantCulture, "{0}|{1:F7},{2:F7},{3:F7}|{4:F7}",
                edges, (minX + maxX) * 0.5, (minY + maxY) * 0.5, (minZ + maxZ) * 0.5,
                extent);
        }

        /// <summary>
        /// Enough points to bound an edge. A circle answers from its own
        /// parameters, which is most bolt holes and costs one call; anything
        /// else is evaluated at a few places, which bounds a slot's arcs and
        /// a spline alike without a refinement loop.
        /// </summary>
        private static IEnumerable<double[]> SamplePoints(IEdge edge)
        {
            ICurve curve = null;
            try { curve = edge.GetCurve() as ICurve; }
            catch { }
            if (curve == null) yield break;

            bool isLine = false;
            try { isLine = curve.IsLine(); }
            catch { }
            if (isLine)
            {
                // Two points bound a straight edge exactly, and two points
                // is also what a fill needs from it. Sampling nine made a
                // rectangular plate look like a 34 triangle fill instead of
                // the two it really is.
                foreach (var p in Ends(curve)) yield return p;
                yield break;
            }

            bool isCircle = false;
            try { isCircle = curve.IsCircle(); }
            catch { }
            if (isCircle)
            {
                double[] cp = null;
                try { cp = curve.CircleParams as double[]; }
                catch { }
                // centre (0..2), axis (3..5), radius (6). The box of the
                // whole circle bounds any arc of it, which is all this needs.
                if (cp != null && cp.Length >= 7)
                {
                    double r = cp[6];
                    for (int sx = -1; sx <= 1; sx += 2)
                        for (int sy = -1; sy <= 1; sy += 2)
                            yield return new[] { cp[0] + sx * r, cp[1] + sy * r, cp[2] };
                    yield break;
                }
            }

            double s = 0, e = 0;
            bool closed = false, periodic = false;
            bool ok = false;
            try { ok = curve.GetEndParams(out s, out e, out closed, out periodic); }
            catch { }
            if (!ok || double.IsNaN(s) || double.IsNaN(e)) yield break;
            const int steps = 8;
            for (int i = 0; i <= steps; i++)
            {
                double t = s + (e - s) * i / steps;
                double[] p = null;
                try { p = curve.Evaluate(t) as double[]; }
                catch { }
                if (p != null && p.Length >= 3) yield return p;
            }
        }

        private static IEnumerable<double[]> Ends(ICurve curve)
        {
            double s = 0, e = 0;
            bool closed = false, periodic = false;
            bool ok = false;
            try { ok = curve.GetEndParams(out s, out e, out closed, out periodic); }
            catch { }
            if (!ok) yield break;
            foreach (double t in new[] { s, e })
            {
                double[] p = null;
                try { p = curve.Evaluate(t) as double[]; }
                catch { }
                if (p != null && p.Length >= 3) yield return p;
            }
        }

        /// <summary>
        /// How many points a fill needs along one edge, at the tolerance the
        /// body is tessellated to. A straight edge needs its two ends. A
        /// circle needs enough chords to stay within the tolerance of it, and
        /// never fewer than the angle tolerance allows, which is the same
        /// pair of rules the tessellator itself works to.
        /// </summary>
        private static int FillPoints(IEdge edge, double tolerance)
        {
            ICurve curve = null;
            try { curve = edge.GetCurve() as ICurve; }
            catch { }
            if (curve == null) return 2;
            try { if (curve.IsLine()) return 2; }
            catch { }
            double radius = 0.0;
            try
            {
                if (curve.IsCircle())
                {
                    var cp = curve.CircleParams as double[];
                    if (cp != null && cp.Length >= 7) radius = Math.Abs(cp[6]);
                }
            }
            catch { }
            if (radius > 0.0 && tolerance > 0.0)
            {
                double ratio = 1.0 - Math.Min(1.0, tolerance / radius);
                double byChord = ratio <= -1.0
                    ? 3.0 : Math.PI / Math.Max(1e-9, Math.Acos(ratio));
                const double angleTolerance = 0.35;      // the tessellator's
                double byAngle = 2.0 * Math.PI / angleTolerance;
                double chords = Math.Max(byChord, byAngle);
                return (int)Math.Ceiling(Math.Max(3.0, Math.Min(chords, 4096.0)));
            }
            int n = 0;
            foreach (var p in SamplePoints(edge)) n++;
            return Math.Max(2, n);
        }

        private static bool IsPlane(IFace2 face)
        {
            try
            {
                var surface = face.GetSurface() as ISurface;
                return surface != null && surface.IsPlane();
            }
            catch { return false; }
        }

        // ── Triangles ──────────────────────────────────────────────────────

        private static int FacetsOfBody(ITessellation tess, object[] faces)
        {
            if (tess == null) return 0;
            int n = 0;
            foreach (var o in faces)
            {
                var face = o as IFace2;
                if (face == null) continue;
                n += FacetCount(tess, face);
            }
            return n;
        }

        private static int FacetsOf(ITessellation tess, List<IFace2> faces)
        {
            if (tess == null) return 0;
            int n = 0;
            foreach (var face in faces) n += FacetCount(tess, face);
            return n;
        }

        private static int FacetCount(ITessellation tess, IFace2 face)
        {
            int[] facets = null;
            try { facets = tess.GetFaceFacets(face) as int[]; }
            catch { }
            return facets == null ? 0 : facets.Length;
        }

        /// <summary>
        /// The triangles a polygon fill would need for a face once its
        /// marked loops are gone: a polygon of n points with h holes
        /// triangulates to n + 2h - 2 triangles, and the holes that stay
        /// are the ones this survey did not claim.
        /// </summary>
        private static double[] LoopCentre(ILoop2 loop)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool any = false;
            foreach (var edge in EdgesOf(loop))
                foreach (var p in SamplePoints(edge))
                {
                    any = true;
                    if (p[0] < minX) minX = p[0];
                    if (p[1] < minY) minY = p[1];
                    if (p[2] < minZ) minZ = p[2];
                    if (p[0] > maxX) maxX = p[0];
                    if (p[1] > maxY) maxY = p[1];
                    if (p[2] > maxZ) maxZ = p[2];
                }
            if (!any) return null;
            return new[]
            {
                (minX + maxX) * 0.5, (minY + maxY) * 0.5, (minZ + maxZ) * 0.5,
            };
        }

        /// <summary>What a face's whole polygon costs by the point model,
        /// holes and all when claimed is null.</summary>
        private static int FillCost(
            IFace2 face, List<string> claimed, double tolerance)
        {
            int points = 0, holes = 0;
            foreach (var loop in LoopsOf(face))
            {
                double extent;
                string key = LoopKey(loop, out extent);
                if (claimed != null && key != null && claimed.Contains(key))
                    continue;                                          // gone
                bool outer = false;
                try { outer = loop.IsOuter(); }
                catch { }
                if (!outer) holes++;
                foreach (var edge in EdgesOf(loop))
                    points += FillPoints(edge, tolerance);
            }
            return Math.Max(1, points + 2 * holes - 2);
        }

    }
}
