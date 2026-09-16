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
    /// Three rules make it general and safe.
    ///
    /// The unit is a small INNER LOOP of a planar face, not a cylinder.
    /// That covers round holes, slots, keyways and small cutouts alike,
    /// with less code than a cylinder classifier.
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

        public sealed class Feature
        {
            public double Extent;            // metres, the widest marked loop
            public int Loops;                // marked loops on its boundary
            public int Faces;                // faces in the region
            public int Facets;               // triangles those faces cost now
            public string Declined;          // null when it would be removed
        }

        public sealed class Result
        {
            public int Faces;
            public int PlanarFaces;
            public int Facets;               // the body's triangles as it is
            public int FilledFaces;          // planar faces needing a new fill
            public int FilledFacetsBefore;   // what those faces cost now
            public int FilledFacetsAfter;    // what a polygon fill would cost
            /// <summary>What the point model says those same faces cost as
            /// they are, holes and all. Against FilledFacetsBefore, which is
            /// measured, it says whether the model can be trusted at all.
            /// The two should agree closely: a planar face needs no interior
            /// points, so the tessellator is triangulating the same polygon
            /// this model counts.</summary>
            public int FilledFacetsModelled;
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
                    return Facets - saved - FilledFacetsBefore + FilledFacetsAfter;
                }
            }
        }

        /// <summary>
        /// Surveys one body. maxExtent is the size under which an inner loop
        /// counts as small, in metres. tess may be null, in which case the
        /// triangle columns come back zero and only the counts are answered.
        /// </summary>
        public static Result Survey(
            IBody2 body, double maxExtent, ITessellation tess, Action<string> log,
            double tolerance = 0.0)
        {
            var result = new Result();
            if (body == null) return result;

            object[] faces = null;
            try { faces = body.GetFaces() as object[]; }
            catch (Exception ex)
            {
                if (log != null) log("small features: GetFaces failed: " + ex.Message);
                return result;
            }
            if (faces == null) return result;

            // ── Pass one: every small inner loop of a planar face ──────────
            var marked = new HashSet<string>();
            var owners = new List<KeyValuePair<IFace2, ILoop2>>();
            foreach (var o in faces)
            {
                var face = o as IFace2;
                if (face == null) continue;
                result.Faces++;
                if (!IsPlane(face)) continue;
                result.PlanarFaces++;
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
                    owners.Add(new KeyValuePair<IFace2, ILoop2>(face, loop));
                }
            }
            result.Facets = FacetsOfBody(tess, faces);
            if (owners.Count == 0) return result;

            // ── Pass two: what sits behind each loop ───────────────────────
            var claimed = new List<string>();        // loop keys already in a region
            var gone = new List<IFace2>();           // faces inside a removed region
            var fillFaces = new List<IFace2>();
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
                feature.Facets = FacetsOf(tess, region);
                feature.Declined = declined;
                result.Features.Add(feature);
                if (declined != null) continue;

                foreach (string b in bounds) if (!claimed.Contains(b)) claimed.Add(b);
                foreach (var f in region) if (!Contains(gone, f)) gone.Add(f);
                foreach (var owner in OwnersOf(owners, bounds))
                    if (!Contains(fillFaces, owner)) fillFaces.Add(owner);
            }

            // A face INSIDE a region is not filled, it is gone. Counting it
            // both ways took a part below zero triangles. It happens on a
            // counterbore, whose annulus is a planar face with a small inner
            // loop of its own: the walk stops at that loop, so the annulus is
            // in the first region AND owns the loop that bounds the second.
            // Dropping it from the fill list is also what makes the whole
            // counterbore go rather than half of it.
            for (int i = fillFaces.Count - 1; i >= 0; i--)
                if (Contains(gone, fillFaces[i])) fillFaces.RemoveAt(i);

            // ── What the faces that lose their holes cost, before and after ─
            //
            // A planar face needs no interior points, so the tessellator is
            // triangulating the same polygon this code can count, and a
            // polygon of P points with H holes is P + 2H - 2 triangles. That
            // reads backwards: the face's MEASURED triangle count and its
            // hole count give P as the tessellator actually built it, with no
            // model in the way.
            //
            // The model is then only asked which SHARE of those points
            // belongs to the holes that go. Counting a circle's chords
            // outright was wrong by 1.6x on this corpus and by 5.8x on one
            // part, because how finely SolidWorks walks a circle is its own
            // business. The share is far steadier than the scale: every
            // circle on a face is counted by the same rule, so the error
            // divides out.
            foreach (var face in fillFaces)
            {
                int before = FacetsOf(tess, new List<IFace2> { face });
                int loops = 0, holesLost = 0;
                double modelAll = 0.0, modelLost = 0.0;
                foreach (var loop in LoopsOf(face))
                {
                    bool outer = false;
                    try { outer = loop.IsOuter(); }
                    catch { }
                    if (!outer) loops++;
                    double extent;
                    string key = LoopKey(loop, out extent);
                    double points = 0.0;
                    foreach (var edge in EdgesOf(loop))
                        points += FillPoints(edge, tolerance);
                    modelAll += points;
                    if (key != null && claimed.Contains(key))
                    {
                        modelLost += points;
                        if (!outer) holesLost++;
                    }
                }
                // P as the tessellator built it, from its own triangle count.
                double pointsBuilt = Math.Max(3.0, before + 2 - 2.0 * loops);
                double share = modelAll > 0.0 ? modelLost / modelAll : 0.0;
                int lost = (int)Math.Round(pointsBuilt * share);
                result.FilledFaces++;
                result.FilledFacetsBefore += before;
                result.FilledFacetsAfter +=
                    Math.Max(1, before - lost - 2 * holesLost);
                result.FilledFacetsModelled += (int)Math.Round(
                    modelAll + 2.0 * loops - 2.0);
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
            double dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
            extent = Math.Sqrt(dx * dx + dy * dy + dz * dz);
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

        private static IEnumerable<IFace2> OwnersOf(
            List<KeyValuePair<IFace2, ILoop2>> owners, List<string> keys)
        {
            foreach (var pair in owners)
            {
                double extent;
                string key = LoopKey(pair.Value, out extent);
                if (key != null && keys.Contains(key)) yield return pair.Key;
            }
        }
    }
}
