using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;

namespace Peak.Cadder.Sw
{
    /// <summary>
    /// Asks whether a planar face's texture coordinates can be rebuilt from
    /// its surface, rather than read off the tessellation.
    ///
    /// This has to be answered before small features are removed. Replacing
    /// a face's triangles with a fill of this code's own means giving the
    /// new points texture coordinates, and if those do not agree with the
    /// ones SolidWorks would have given, a textured part shifts where it
    /// was defeatured and nowhere else, which is worse than not defeaturing
    /// at all.
    ///
    /// The recovery is the obvious one: a plane's surface is
    /// Evaluate(u, v) = P0 + u*U + v*V, so evaluating at (0,0), (1,0) and
    /// (0,1) hands back the origin and the two axes, and any point on the
    /// plane projects onto them. This measures that against the parameters
    /// the tessellator itself reports, which ARE SolidWorks' answer.
    ///
    /// One caveat was built into the measurement, and the measurement
    /// answered it. A planar face needs no interior points, so every vertex
    /// it has sits on an edge it shares with another face, and the worry was
    /// that such a vertex would be ONE vertex carrying one pair of
    /// parameters belonging to whichever face SolidWorks chose. It is not:
    /// over the corpus, of 152,333 vertices not one was claimed by two
    /// faces. Facet topology matching shares vertices inside a face and
    /// never across one. The count of vertices touched by a single face is
    /// still reported, because it is what says so.
    /// </summary>
    public static class PlaneUvCheck
    {
        public sealed class Result
        {
            public int PlanarFaces;
            public int Vertices;            // vertices on planar faces
            public int Sole;                // ... of those, touched by one face
            public int Agree;               // within Tolerance
            public int SoleAgree;
            public double Worst;            // metres, over sole vertices
            public double WorstAny;
            public int NoFrame;             // faces whose surface would not evaluate
            public int BodyVertices;        // every vertex of the body
            public int BodyShared;          // ... claimed by more than one face
        }

        /// <summary>How near is near enough. Parameters on a plane are in
        /// metres, so this is a micron: far below anything a texture shows,
        /// and far above single-precision rounding.</summary>
        public const double Tolerance = 1e-6;

        public static Result Check(IBody2 body, ITessellation tess, Action<string> log)
        {
            var result = new Result();
            if (body == null || tess == null) return result;

            object[] faces = null;
            try { faces = body.GetFaces() as object[]; }
            catch { }
            if (faces == null) return result;

            int vertexCount;
            try { vertexCount = tess.GetVertexCount(); }
            catch { return result; }
            if (vertexCount <= 0) return result;

            // Which face each vertex belongs to, and whether a second one
            // ever claimed it.
            var owner = new int[vertexCount];
            var shared = new bool[vertexCount];
            for (int i = 0; i < vertexCount; i++) owner[i] = -1;

            var perFace = new List<List<int>>();
            int index = 0;
            foreach (var o in faces)
            {
                var face = o as IFace2;
                if (face == null) { perFace.Add(null); index++; continue; }
                var mine = new List<int>();
                foreach (int facet in FacetsOf(tess, face))
                    foreach (int v in VerticesOf(tess, facet))
                    {
                        if (v < 0 || v >= vertexCount) continue;
                        mine.Add(v);
                        if (owner[v] == -1) owner[v] = index;
                        else if (owner[v] != index) shared[v] = true;
                    }
                perFace.Add(mine);
                index++;
            }

            result.BodyVertices = vertexCount;
            for (int i = 0; i < vertexCount; i++) if (shared[i]) result.BodyShared++;

            index = 0;
            foreach (var o in faces)
            {
                var face = o as IFace2;
                var mine = perFace[index++];
                if (face == null || mine == null) continue;
                ISurface surface = null;
                try { surface = face.GetSurface() as ISurface; }
                catch { }
                bool plane = false;
                try { plane = surface != null && surface.IsPlane(); }
                catch { }
                if (!plane) continue;
                result.PlanarFaces++;

                double[] p0 = At(surface, 0.0, 0.0);
                double[] pu = At(surface, 1.0, 0.0);
                double[] pv = At(surface, 0.0, 1.0);
                if (p0 == null || pu == null || pv == null)
                {
                    result.NoFrame++;
                    continue;
                }
                var u = Sub(pu, p0);
                var v = Sub(pv, p0);
                double uu = Dot(u, u), vv = Dot(v, v);
                if (uu < 1e-18 || vv < 1e-18) { result.NoFrame++; continue; }

                var counted = new HashSet<int>();
                foreach (int vertex in mine)
                {
                    if (!counted.Add(vertex)) continue;
                    double[] point = null, param = null;
                    try { point = tess.GetVertexPoint(vertex) as double[]; }
                    catch { }
                    try { param = tess.GetVertexParams(vertex) as double[]; }
                    catch { }
                    if (point == null || point.Length < 3
                        || param == null || param.Length < 2) continue;

                    var d = Sub(point, p0);
                    double gotU = Dot(d, u) / uu;
                    double gotV = Dot(d, v) / vv;
                    double off = Math.Max(Math.Abs(gotU - param[0]),
                                          Math.Abs(gotV - param[1]));

                    result.Vertices++;
                    if (off > result.WorstAny) result.WorstAny = off;
                    if (off <= Tolerance) result.Agree++;
                    if (!shared[vertex])
                    {
                        result.Sole++;
                        if (off > result.Worst) result.Worst = off;
                        if (off <= Tolerance) result.SoleAgree++;
                    }
                }
            }
            return result;
        }

        private static double[] At(ISurface surface, double u, double v)
        {
            double[] r = null;
            try { r = surface.Evaluate(u, v, 0, 0) as double[]; }
            catch { }
            return r != null && r.Length >= 3 ? new[] { r[0], r[1], r[2] } : null;
        }

        private static double[] Sub(double[] a, double[] b)
        {
            return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
        }

        private static double Dot(double[] a, double[] b)
        {
            return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        }

        private static IEnumerable<int> FacetsOf(ITessellation tess, IFace2 face)
        {
            int[] facets = null;
            try { facets = tess.GetFaceFacets(face) as int[]; }
            catch { }
            if (facets == null) yield break;
            foreach (int f in facets) yield return f;
        }

        /// <summary>The three vertices of a facet, by the same fin walk the
        /// tessellator's own reader uses.</summary>
        private static IEnumerable<int> VerticesOf(ITessellation tess, int facet)
        {
            int[] fins = null;
            try { fins = tess.GetFacetFins(facet) as int[]; }
            catch { }
            if (fins == null || fins.Length < 3) yield break;
            foreach (int fin in fins)
            {
                int[] pair = null;
                try { pair = tess.GetFinVertices(fin) as int[]; }
                catch { }
                if (pair == null || pair.Length < 2) continue;
                yield return pair[0];
                yield return pair[1];
            }
        }
    }
}
