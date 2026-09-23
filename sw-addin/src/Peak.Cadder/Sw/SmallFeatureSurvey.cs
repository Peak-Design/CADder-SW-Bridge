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
    /// drilled into a boss or a shaft. The loop must open INTO the material:
    /// the foot of a pin or a boss is an inner loop too, and it stays.
    ///
    /// The feature behind a loop is found by walking INWARD: cross into the
    /// face on the other side, then keep crossing every edge that is not
    /// itself on a marked loop. A through hole gives one cylinder bounded
    /// by two marked loops. A blind hole gives the cylinder and its bottom.
    /// A counterbore gives two regions: the counterbore (cylinder and
    /// annulus), and the hole in its floor (cylinder and bottom), which is
    /// walked from the annulus. The planner does this in any face order.
    ///
    /// The region is removed only when its WHOLE boundary is marked loops.
    /// A hole running into a fillet, a hole breaking the silhouette, a
    /// thread, anything odd: the walk escapes and the feature stays. There
    /// is no repair step and nothing to fail, only "defeatured" and "left
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

            var list = new List<IFace2>();
            foreach (var o in faces)
            {
                var face = o as IFace2;
                if (face != null) list.Add(face);
            }
            var found = new FeaturePlanner<IFace2, ILoop2, IEdge>(new SwTopology())
                .Choose(list, maxExtent, curved);

            plan.Faces = found.Faces;
            plan.PlanarFaces = found.PlanarFaces;
            plan.Gone.AddRange(found.Gone);
            plan.Fill.AddRange(found.Fill);
            foreach (var rims in found.FillHoles) plan.FillHoles.Add(Holes(rims));
            plan.Cap.AddRange(found.Cap);
            foreach (var rims in found.CapHoles) plan.CapHoles.Add(Holes(rims));
            foreach (var f in found.Features)
                plan.Features.Add(new Feature
                {
                    Extent = f.Extent,
                    Loops = f.Loops,
                    Faces = f.Faces,
                    Declined = f.Declined,
                    Region = f.Region,
                });
            return plan;
        }

        private static List<PlaneRefill.Hole> Holes(
            List<FeaturePlanner<IFace2, ILoop2, IEdge>.Rim> rims)
        {
            var holes = new List<PlaneRefill.Hole>(rims.Count);
            foreach (var rim in rims)
                holes.Add(new PlaneRefill.Hole
                {
                    Centre = rim.Centre,
                    Extent = rim.Extent,
                    Edges = rim.Edges,
                });
            return holes;
        }

        /// <summary>The body's topology as the planner reads it, answered
        /// from SolidWorks.</summary>
        private sealed class SwTopology : IFeatureTopology<IFace2, ILoop2, IEdge>
        {
            public bool IsPlane(IFace2 face) { return SmallFeatureSurvey.IsPlane(face); }

            public IEnumerable<ILoop2> LoopsOf(IFace2 face) { return SmallFeatureSurvey.LoopsOf(face); }

            public IEnumerable<IEdge> EdgesOf(ILoop2 loop) { return SmallFeatureSurvey.EdgesOf(loop); }

            public IList<IFace2> FacesOf(IEdge edge)
            {
                object[] pair = null;
                try { pair = edge.GetTwoAdjacentFaces2() as object[]; }
                catch { }
                var faces = new List<IFace2>();
                foreach (var o in pair ?? new object[0]) faces.Add(o as IFace2);
                return faces;
            }

            public bool Same(IFace2 a, IFace2 b) { return SmallFeatureSurvey.Same(a, b); }

            public bool TryIsOuter(ILoop2 loop, out bool outer)
            {
                outer = false;
                try { outer = loop.IsOuter(); return true; }
                catch { return false; }
            }

            public string LoopKey(ILoop2 loop, out double extent)
            {
                return SmallFeatureSurvey.LoopKey(loop, out extent);
            }

            public double[] LoopCentre(ILoop2 loop) { return SmallFeatureSurvey.LoopCentre(loop); }

            /// <summary>
            /// Reads the side from the faces across the rim: the points on
            /// their edges, measured from the owner's plane at the rim. The
            /// owner's normal is the FACE normal, which points out of the
            /// material: ISurface.EvaluateAtPoint gives the surface normal,
            /// and FaceInSurfaceSense says when the face runs the other way.
            /// </summary>
            public int FeatureSide(IFace2 owner, ILoop2 loop, double extent)
            {
                double[] at = null;
                var rim = new List<IEdge>();
                foreach (var edge in SmallFeatureSurvey.EdgesOf(loop))
                {
                    rim.Add(edge);
                    if (at == null)
                        foreach (var p in SamplePoints(edge)) { at = p; break; }
                }
                if (at == null) return 0;
                var outward = OutwardNormal(owner, at);
                if (outward == null) return 0;

                var beyond = new List<IFace2>();
                foreach (var edge in rim)
                    foreach (var face in FacesOf(edge))
                        if (face != null && !Same(face, owner) && !Contains(beyond, face))
                            beyond.Add(face);
                var points = new List<double[]>();
                foreach (var face in beyond)
                    foreach (var other in SmallFeatureSurvey.LoopsOf(face))
                        foreach (var edge in SmallFeatureSurvey.EdgesOf(other))
                            points.AddRange(SamplePoints(edge));
                return SideOf(at, outward, points, extent);
            }

            private static double[] OutwardNormal(IFace2 face, double[] at)
            {
                try
                {
                    var surface = face.GetSurface() as ISurface;
                    if (surface == null) return null;
                    var raw = surface.EvaluateAtPoint(at[0], at[1], at[2]) as double[];
                    if (raw == null || raw.Length < 3) return null;
                    double sign = face.FaceInSurfaceSense() ? -1.0 : 1.0;
                    double length = Math.Sqrt(raw[0] * raw[0] + raw[1] * raw[1] + raw[2] * raw[2]);
                    if (!(length > 0.0)) return null;
                    return new[]
                    {
                        sign * raw[0] / length, sign * raw[1] / length, sign * raw[2] / length,
                    };
                }
                catch { return null; }
            }
        }

        /// <summary>
        /// Which way a feature goes from the owner face: +1 into the
        /// material, -1 out of it, 0 when the points do not say.
        ///
        /// An inner loop of a face can be a hole or the foot of a boss, a
        /// pin or a standoff. Both walk closed, so before this the walk took
        /// a pin away and refilled the face over its foot, and the closure
        /// check passed: a visible part of the outline went with no warning.
        /// SolidWorks' own example (Determine Type of Face) tells the two
        /// apart at the rim. This reads the whole faces beyond the rim
        /// instead, which also works where they meet the owner at a tangent,
        /// as a filleted rim does.
        ///
        /// <paramref name="at"/> is a point on the rim and
        /// <paramref name="outward"/> the owner's unit normal there, out of
        /// the material. The deepest point beyond the rim on each side of
        /// that plane decides. A curved owner lets the rim itself rise or
        /// fall a little, so the other side only has to be much smaller.
        /// </summary>
        internal static int SideOf(
            double[] at, double[] outward, IEnumerable<double[]> beyond, double extent)
        {
            double into = 0.0, outOf = 0.0;
            foreach (var p in beyond)
            {
                double d = (p[0] - at[0]) * outward[0] + (p[1] - at[1]) * outward[1]
                    + (p[2] - at[2]) * outward[2];
                if (-d > into) into = -d;
                if (d > outOf) outOf = d;
            }
            double floor = Math.Max(1e-7, extent * 1e-3);
            if (into > floor && outOf <= into * 0.25) return 1;
            if (outOf > floor && into <= outOf * 0.25) return -1;
            return 0;
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
            // contract, defeatured or left alone.
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
                    result.CapFacets += SurfaceCap.TriangleCount(lid);
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
                result.CapFacets += SurfaceCap.TriangleCount(lid);
            }
            return result;
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
            var points = new List<double[]>();
            int edges = 0;
            foreach (var edge in EdgesOf(loop))
            {
                edges++;
                points.AddRange(SamplePoints(edge));
            }
            return LoopKey(edges, points, out extent);
        }

        /// <summary>The key of a loop of this many edges through these
        /// points.</summary>
        internal static string LoopKey(int edges, IEnumerable<double[]> points, out double extent)
        {
            double[] centre;
            if (edges == 0 || !Box(points, out centre, out extent))
            {
                extent = 0.0;
                return null;
            }
            return string.Format(
                CultureInfo.InvariantCulture, "{0}|{1:F7},{2:F7},{3:F7}|{4:F7}",
                edges, centre[0], centre[1], centre[2], extent);
        }

        /// <summary>
        /// Enough points to bound an edge, all of them on the edge. A line
        /// answers with its two ends. A circle answers from its own
        /// parameters, which is most bolt holes, and an arc keeps the
        /// extremes that are on it. Anything else is evaluated at a few
        /// places, which bounds a spline without a refinement loop.
        /// </summary>
        private static IEnumerable<double[]> SamplePoints(IEdge edge)
        {
            return EdgePoints(ShapeOf(edge));
        }

        /// <summary>What SamplePoints reads from one edge, so that the rules
        /// that turn it into points can run without SolidWorks.</summary>
        internal sealed class EdgeShape
        {
            public bool IsLine;
            public bool IsCircle;
            /// <summary>Center (0..2), axis (3..5) and radius (6), as
            /// ICurve.CircleParams gives them.</summary>
            public double[] Circle;
            /// <summary>The ends of the EDGE. A closed edge has one point
            /// for both.</summary>
            public double[] Start, End;
            /// <summary>The parameter range of the whole CURVE. For a line
            /// that is the whole infinite line.</summary>
            public double CurveMin = double.NaN, CurveMax = double.NaN;
            public Func<double, double[]> Evaluate;
            /// <summary>Whether a point of the curve lies on the edge. True
            /// when it cannot tell.</summary>
            public Func<double[], bool> OnEdge;
        }

        private static EdgeShape ShapeOf(IEdge edge)
        {
            var shape = new EdgeShape();
            ICurve curve = null;
            try { curve = edge.GetCurve() as ICurve; }
            catch { }
            if (curve == null) return shape;
            try { shape.IsLine = curve.IsLine(); }
            catch { }
            if (!shape.IsLine)
            {
                try { shape.IsCircle = curve.IsCircle(); }
                catch { }
            }
            if (shape.IsCircle)
            {
                try { shape.Circle = curve.CircleParams as double[]; }
                catch { }
            }
            double s = 0, e = 0;
            bool closed = false, periodic = false;
            try
            {
                if (curve.GetEndParams(out s, out e, out closed, out periodic))
                {
                    shape.CurveMin = s;
                    shape.CurveMax = e;
                }
            }
            catch { }
            shape.Evaluate = t =>
            {
                double[] p = null;
                try { p = curve.Evaluate(t) as double[]; }
                catch { }
                return p != null && p.Length >= 3 ? new[] { p[0], p[1], p[2] } : null;
            };
            ReadEnds(edge, shape);
            // The edge knows its own extent, whichever way its curve runs:
            // a point of the curve is on the edge when the edge's nearest
            // point to it is the point itself. When the edge cannot answer,
            // the point counts, so the box can only come out too big, and
            // a feature that is not small is never taken for one.
            double reach = shape.Circle != null && shape.Circle.Length >= 7
                ? Math.Abs(shape.Circle[6]) : 0.0;
            double tolerance = Math.Max(1e-8, reach * 1e-6);
            shape.OnEdge = q =>
            {
                double[] on = null;
                try { on = edge.GetClosestPointOn(q[0], q[1], q[2]) as double[]; }
                catch { }
                if (on == null || on.Length < 3) return true;
                return Near(on, q, tolerance);
            };
            return shape;
        }

        /// <summary>The two ends of the edge itself, which for a closed
        /// edge are one point. Both the calls that give them need
        /// IEdge.GetCurve first, which ShapeOf has made.</summary>
        private static void ReadEnds(IEdge edge, EdgeShape shape)
        {
            try
            {
                var data = edge.GetCurveParams3();
                if (data != null)
                {
                    shape.Start = Point(data.StartPoint as double[], 0);
                    shape.End = Point(data.EndPoint as double[], 0);
                }
            }
            catch { }
            if (shape.Start != null && shape.End != null) return;
            try
            {
                var raw = edge.GetCurveParams2() as double[];
                shape.Start = Point(raw, 0);
                shape.End = Point(raw, 3);
            }
            catch { }
            if (shape.Start == null || shape.End == null) shape.Start = shape.End = null;
        }

        private static double[] Point(double[] raw, int at)
        {
            if (raw == null || raw.Length < at + 3) return null;
            return new[] { raw[at], raw[at + 1], raw[at + 2] };
        }

        /// <summary>
        /// The points SamplePoints gives for one edge. Every point lies on
        /// the edge, so the same points bound the loop and show which way
        /// the faces beyond a rim go.
        ///
        /// The edge's own two ends come first. IEdge.GetCurve gives the
        /// UNDERLYING curve, and for a line that is the whole infinite line:
        /// its end parameters are the largest the parameter space allows.
        /// Evaluated there, every loop with a straight edge came out
        /// enormous, so a slot, a keyway or a small cutout never counted as
        /// small, whatever the dial said.
        /// </summary>
        internal static List<double[]> EdgePoints(EdgeShape shape)
        {
            var points = new List<double[]>();
            if (shape == null) return points;
            bool closed = shape.Start == null || shape.End == null
                || Near(shape.Start, shape.End, 1e-9);
            if (shape.Start != null) points.Add(shape.Start);
            if (shape.End != null && !closed) points.Add(shape.End);
            // Two points bound a straight edge exactly, and two points is
            // also what a fill needs from it. Sampling nine made a
            // rectangular plate look like a 34 triangle fill instead of the
            // two it really is.
            if (shape.IsLine) return points;

            var cp = shape.Circle;
            if (shape.IsCircle && cp != null && cp.Length >= 7)
            {
                // The circle's own extremes along each world axis, where the
                // arc reaches them. The box of (cx +/- r, cy +/- r, cz) is
                // the box of a circle about Z only. A circle in any other
                // plane got a width it does not have in one direction and
                // none in another.
                foreach (var q in CircleExtremes(cp))
                    if (closed || shape.OnEdge == null || shape.OnEdge(q)) points.Add(q);
                return points;
            }

            // Anything else: the curve at a few places, where they are on
            // the edge. A curve with an unbounded range gives no samples.
            double s = shape.CurveMin, e = shape.CurveMax;
            if (double.IsNaN(s) || double.IsNaN(e) || double.IsInfinity(s)
                || double.IsInfinity(e) || Math.Abs(e - s) > 1e8
                || shape.Evaluate == null)
                return points;
            const int steps = 8;
            for (int i = 0; i <= steps; i++)
            {
                var p = shape.Evaluate(s + (e - s) * i / steps);
                if (p == null) continue;
                if (closed || shape.OnEdge == null || shape.OnEdge(p)) points.Add(p);
            }
            return points;
        }

        /// <summary>
        /// The points of a circle furthest along each world axis, both
        /// ways: the center plus r times that axis laid into the circle's
        /// plane. The box of these six points is the box of the circle.
        /// An axis along the circle's normal has no extreme, and is left out.
        /// </summary>
        internal static IEnumerable<double[]> CircleExtremes(double[] cp)
        {
            double nx = cp[3], ny = cp[4], nz = cp[5];
            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (!(length > 0.0)) yield break;
            var n = new[] { nx / length, ny / length, nz / length };
            double r = Math.Abs(cp[6]);
            for (int k = 0; k < 3; k++)
            {
                var u = new[] { -n[k] * n[0], -n[k] * n[1], -n[k] * n[2] };
                u[k] += 1.0;
                double size = Math.Sqrt(u[0] * u[0] + u[1] * u[1] + u[2] * u[2]);
                if (size < 1e-12) continue;
                for (int sign = -1; sign <= 1; sign += 2)
                    yield return new[]
                    {
                        cp[0] + sign * r * u[0] / size,
                        cp[1] + sign * r * u[1] / size,
                        cp[2] + sign * r * u[2] / size,
                    };
            }
        }

        private static bool Near(double[] a, double[] b, double tolerance)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1], dz = a[2] - b[2];
            return dx * dx + dy * dy + dz * dz <= tolerance * tolerance;
        }

        /// <summary>The box of some points: its middle, and its widest
        /// side. False when there are no points.</summary>
        internal static bool Box(
            IEnumerable<double[]> points, out double[] centre, out double extent)
        {
            centre = null;
            extent = 0.0;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool any = false;
            foreach (var p in points)
            {
                any = true;
                if (p[0] < minX) minX = p[0];
                if (p[1] < minY) minY = p[1];
                if (p[2] < minZ) minZ = p[2];
                if (p[0] > maxX) maxX = p[0];
                if (p[1] > maxY) maxY = p[1];
                if (p[2] > maxZ) maxZ = p[2];
            }
            if (!any) return false;
            // The WIDEST SIDE of the box, not its diagonal. A dial that says
            // 12 mm has to mean a 12 mm hole, and the diagonal of a circle's
            // box is 1.414 times its diameter whichever way the circle
            // faces, so the diagonal made the dial mean 8.5 mm instead.
            // The widest side is the diameter exactly for a hole drilled
            // along an axis, never less than 0.82 of it for one drilled at
            // an angle, and the length of a slot rather than its diagonal.
            extent = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
            centre = new[] { (minX + maxX) * 0.5, (minY + maxY) * 0.5, (minZ + maxZ) * 0.5 };
            return true;
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
            var points = new List<double[]>();
            foreach (var edge in EdgesOf(loop)) points.AddRange(SamplePoints(edge));
            double[] centre;
            double extent;
            return Box(points, out centre, out extent) ? centre : null;
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

    /// <summary>
    /// What the planner reads from a body. SmallFeatureSurvey answers from
    /// SolidWorks. The tests answer from a model of a part, so the rules of
    /// the walk can be checked without SolidWorks.
    /// </summary>
    internal interface IFeatureTopology<TFace, TLoop, TEdge>
        where TFace : class where TLoop : class where TEdge : class
    {
        bool IsPlane(TFace face);
        IEnumerable<TLoop> LoopsOf(TFace face);
        IEnumerable<TEdge> EdgesOf(TLoop loop);

        /// <summary>The faces of an edge as SolidWorks gives them: two for
        /// an edge inside a solid. An entry can be null.</summary>
        IList<TFace> FacesOf(TEdge edge);

        bool Same(TFace a, TFace b);

        /// <summary>False when the loop cannot say.</summary>
        bool TryIsOuter(TLoop loop, out bool outer);

        /// <summary>A name that both faces of a loop arrive at, and the
        /// width of the loop. Null when the loop gives no points.</summary>
        string LoopKey(TLoop loop, out double extent);

        double[] LoopCentre(TLoop loop);

        /// <summary>Which way the feature behind an inner loop of this face
        /// goes: +1 into the material (a hole or a pocket), -1 out of it (a
        /// boss, a pin or a standoff), 0 when it cannot tell.</summary>
        int FeatureSide(TFace owner, TLoop loop, double extent);
    }

    /// <summary>
    /// The rules that decide what a body is sent without, apart from the
    /// SolidWorks calls that feed them. SmallFeatureSurvey.Choose runs them
    /// on a live body, and the tests run them on a model of a part.
    /// </summary>
    internal sealed class FeaturePlanner<TFace, TLoop, TEdge>
        where TFace : class where TLoop : class where TEdge : class
    {
        /// <summary>A walk that reaches this many faces has not found a
        /// pocket, it has found its way out into the body. Abandon it, so a
        /// pathological shape cannot cost the export.</summary>
        internal const int FaceBudget = 24;

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
        internal sealed class Rim
        {
            public string Key;
            public double[] Centre;
            public double Extent;
            public List<TEdge> Edges = new List<TEdge>();
        }

        internal sealed class Feature
        {
            public double Extent;
            public int Loops;
            public int Faces;
            public string Declined;
            public List<TFace> Region = new List<TFace>();
        }

        internal sealed class Result
        {
            public List<TFace> Gone = new List<TFace>();
            public List<TFace> Fill = new List<TFace>();
            public List<List<Rim>> FillHoles = new List<List<Rim>>();
            public List<TFace> Cap = new List<TFace>();
            public List<List<Rim>> CapHoles = new List<List<Rim>>();
            public List<Feature> Features = new List<Feature>();
            public int Faces;
            public int PlanarFaces;
        }

        private readonly IFeatureTopology<TFace, TLoop, TEdge> _topo;

        public FeaturePlanner(IFeatureTopology<TFace, TLoop, TEdge> topology)
        {
            _topo = topology;
        }

        /// <summary>See SmallFeatureSurvey.Choose.</summary>
        public Result Choose(IEnumerable<TFace> faces, double maxExtent, bool curved)
        {
            var plan = new Result();
            var marked = new HashSet<string>();
            var rims = new Dictionary<string, Rim>();
            var owners = new List<KeyValuePair<TFace, TLoop>>();
            foreach (var face in faces)
            {
                if (face == null) continue;
                plan.Faces++;
                bool plane = _topo.IsPlane(face);
                if (plane) plan.PlanarFaces++;
                if (!plane && !curved) continue;
                foreach (var loop in _topo.LoopsOf(face))
                {
                    bool outer;
                    if (!_topo.TryIsOuter(loop, out outer) || outer) continue;
                    double extent;
                    string key = _topo.LoopKey(loop, out extent);
                    if (key == null || extent > maxExtent) continue;
                    // Only a hole or a pocket is a feature to leave out. A
                    // pin or a boss standing on the face is part of the
                    // outline, and it is not a candidate at all.
                    int side = _topo.FeatureSide(face, loop, extent);
                    if (side < 0) continue;
                    if (side == 0)
                    {
                        plan.Features.Add(new Feature
                        {
                            Extent = extent,
                            Declined = "the faces beyond the loop show no hole",
                        });
                        continue;
                    }
                    marked.Add(key);
                    if (!rims.ContainsKey(key))
                    {
                        var rim = new Rim
                        {
                            Key = key,
                            Extent = extent,
                            Centre = _topo.LoopCentre(loop),
                        };
                        foreach (var edge in _topo.EdgesOf(loop)) rim.Edges.Add(edge);
                        if (rim.Centre != null) rims[key] = rim;
                    }
                    owners.Add(new KeyValuePair<TFace, TLoop>(face, loop));
                }
            }
            if (owners.Count == 0) return plan;

            // Two lists, because a loop can be a wall of one region and the
            // way into another. A walk stops at every marked loop it meets,
            // so the walk from a counterbore's rim ends at the rim of the
            // hole in its floor. That rim is a wall of the counterbore, and
            // it is also where the hole behind it starts. When every wall
            // counted as walked, the hole was never walked on its own when
            // the top face came first: its wall and bottom stayed inside the
            // part, capped, as a closed shell turned inside out.
            //
            // So only the loop a walk STARTED from counts as walked, as on
            // the STEP route (defeature_brep.plan). Every wall of a region
            // that goes is claimed, which is what the covering below needs.
            // A loop is not walked when everything behind it has already
            // gone, which is the far rim of a through hole, and a region
            // found twice counts once.
            var walked = new HashSet<string>();
            var claimed = new List<string>();
            var removed = new List<List<TFace>>();
            foreach (var pair in owners)
            {
                double extent;
                string key = _topo.LoopKey(pair.Value, out extent);
                if (key == null || walked.Contains(key)) continue;
                if (BehindIsGone(pair.Key, pair.Value, plan.Gone)) continue;

                var region = new List<TFace>();
                var bounds = new List<string>();
                string declined = Walk(pair.Key, pair.Value, marked, region, bounds);
                if (declined == null && FoundBefore(removed, region)) continue;
                plan.Features.Add(new Feature
                {
                    Extent = extent,
                    Faces = region.Count,
                    Loops = bounds.Count,
                    Declined = declined,
                    Region = region,
                });
                if (declined != null) continue;

                walked.Add(key);
                removed.Add(region);
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
        /// Collects the faces behind one marked loop. Returns null when the
        /// whole boundary of what it found is marked loops, and the reason
        /// it gave up otherwise.
        /// </summary>
        private string Walk(
            TFace from, TLoop seed, HashSet<string> marked,
            List<TFace> region, List<string> bounds)
        {
            var queue = new Queue<TFace>();
            foreach (var edge in _topo.EdgesOf(seed))
            {
                var next = Across(edge, from);
                if (next == null) return "an edge of the loop has no face behind it";
                if (_topo.Same(next, from)) return "the loop has the same face on both sides";
                if (!Contains(region, next)) { region.Add(next); queue.Enqueue(next); }
            }
            double _;
            string seedKey = _topo.LoopKey(seed, out _);
            if (seedKey != null) bounds.Add(seedKey);

            while (queue.Count > 0)
            {
                if (region.Count > FaceBudget)
                    return "the walk passed " + FaceBudget + " faces without closing";
                var face = queue.Dequeue();
                foreach (var loop in _topo.LoopsOf(face))
                {
                    double extent;
                    string key = _topo.LoopKey(loop, out extent);
                    if (key != null && marked.Contains(key))
                    {
                        if (!bounds.Contains(key)) bounds.Add(key);
                        continue;                       // a wall of the pocket
                    }
                    foreach (var edge in _topo.EdgesOf(loop))
                    {
                        var next = Across(edge, face);
                        if (next == null) return "an edge inside the feature has no face behind it";
                        // Back at the face the loop is on means the walk has
                        // gone round the OUTSIDE of the body, not into a
                        // pocket. Without this a nut block with too few faces
                        // to trip the budget came back as one feature holding
                        // every face it had, and the body defeatured to
                        // nothing at all.
                        if (_topo.Same(next, from))
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

        /// <summary>Whether every face across this loop has gone already:
        /// the region behind it was taken out from its other end.</summary>
        private bool BehindIsGone(TFace owner, TLoop loop, List<TFace> gone)
        {
            if (gone.Count == 0) return false;
            bool any = false;
            foreach (var edge in _topo.EdgesOf(loop))
            {
                var next = Across(edge, owner);
                if (next == null || !Contains(gone, next)) return false;
                any = true;
            }
            return any;
        }

        /// <summary>Whether a region that goes already holds exactly these
        /// faces.</summary>
        private bool FoundBefore(List<List<TFace>> removed, List<TFace> region)
        {
            foreach (var had in removed)
            {
                if (had.Count != region.Count) continue;
                bool same = true;
                foreach (var f in region)
                    if (!Contains(had, f)) { same = false; break; }
                if (same) return true;
            }
            return false;
        }

        /// <summary>Both faces of an edge, however many it can answer.</summary>
        private IEnumerable<TFace> BothSides(TEdge edge)
        {
            foreach (var face in _topo.FacesOf(edge) ?? new List<TFace>())
                if (face != null) yield return face;
        }

        /// <summary>Notes that a surviving face has to lose this rim: rebuilt
        /// without it when it is flat, capped over it when it is not.</summary>
        private void Cover(Result plan, TFace face, Rim rim)
        {
            bool plane = _topo.IsPlane(face);
            var into = plane ? plan.Fill : plan.Cap;
            var holes = plane ? plan.FillHoles : plan.CapHoles;
            int at = -1;
            for (int i = 0; i < into.Count && at < 0; i++)
                if (_topo.Same(into[i], face)) at = i;
            if (at < 0)
            {
                into.Add(face);
                holes.Add(new List<Rim>());
                at = into.Count - 1;
            }
            foreach (var had in holes[at])
                if (had.Extent == rim.Extent && had.Centre[0] == rim.Centre[0]
                    && had.Centre[1] == rim.Centre[1]
                    && had.Centre[2] == rim.Centre[2]) return;
            holes[at].Add(rim);
        }

        /// <summary>The face on the other side of an edge, or null.</summary>
        private TFace Across(TEdge edge, TFace from)
        {
            var pair = _topo.FacesOf(edge);
            if (pair == null || pair.Count < 2) return null;
            var a = pair[0];
            var b = pair[1];
            if (a == null || b == null) return a ?? b;
            return _topo.Same(a, from) ? b : a;
        }

        private bool Contains(List<TFace> faces, TFace face)
        {
            foreach (var f in faces) if (_topo.Same(f, face)) return true;
            return false;
        }
    }
}
