using System;
using System.Collections.Generic;
using Peak.Cadder.Core;
using SolidWorks.Interop.sldworks;

namespace Peak.Cadder.Sw
{
    /// <summary>
    /// Rebuilds a planar face's triangles from its own boundary, with the
    /// small holes left out.
    ///
    /// The boundary comes from the tessellation SolidWorks already made,
    /// not from the edges' curves. That is not laziness, it is the only
    /// thing that works: the neighbouring faces keep their own triangles,
    /// so a fill whose boundary points differ from theirs by a micron
    /// leaves a crack all the way round the part. Reusing the very same
    /// vertices means the seam is exactly as tight as it was.
    ///
    /// It also means the fill introduces NO new vertices, so the question of
    /// what texture coordinates to give new points does not arise. Every
    /// point already has the coordinates, normal and position SolidWorks
    /// gave it. PlaneUvCheck still earned its place: it is what established
    /// that a vertex's parameters belong to one face only, which is what
    /// makes them usable here as the face's own flat coordinates.
    ///
    /// A face is either refilled or left exactly as it was. There is no
    /// repair.
    /// </summary>
    public static class PlaneRefill
    {
        /// <summary>A hole to leave out, as the survey found it.</summary>
        public sealed class Hole
        {
            public double[] Centre;      // global metres
            public double Extent;        // the width of its bounding box
            /// <summary>The model edges the rim is made of. A tessellated
            /// point of the rim sits ON one of them, which is what tells the
            /// right boundary loop from one that merely looks like it.</summary>
            public IList<IEdge> Edges;
        }

        /// <summary>
        /// What the fill was made from, so a caller can check it. The area
        /// of the triangles it gives back must equal the area of the ones it
        /// replaced PLUS the holes left out, because a face's own triangles
        /// cover the face without its holes and a hole left out is area the
        /// fill gains. That is a fact about real geometry rather than about
        /// a fixture.
        /// </summary>
        public sealed class Report
        {
            public double Before;        // the face's own triangles
            public double After;         // the fill's
            public double Dropped;       // the holes left out
            public int Loops;
        }

        /// <summary>
        /// The triangles that replace this face's own, three body vertex
        /// indices each, or null when the face has to be left alone.
        /// </summary>
        public static List<int> Build(
            IFace2 face, ITessellation tess, IList<Hole> gone, Action<string> log,
            Report report = null)
        {
            if (face == null || tess == null) return null;

            var tris = FaceTriangles(tess, face);
            if (tris == null || tris.Count < 3) return null;

            // Flat coordinates: the parameters of a plane ARE distances
            // along its two axes, so they serve as the plane's own x and y.
            var uv = new Dictionary<int, double[]>();
            foreach (int v in tris)
            {
                if (uv.ContainsKey(v)) continue;
                double[] p = null;
                try { p = tess.GetVertexParams(v) as double[]; }
                catch { }
                if (p == null || p.Length < 2) return null;
                uv[v] = new[] { p[0], p[1] };
            }

            // One winding for the lot. TryStitch gives a consistent loop but
            // not a consistent direction, and a boundary can only be found
            // when every inside edge is walked both ways.
            for (int i = 0; i < tris.Count; i += 3)
            {
                double twice = Cross(uv[tris[i]], uv[tris[i + 1]], uv[tris[i + 2]]);
                if (twice >= 0.0) continue;
                int swap = tris[i + 1];
                tris[i + 1] = tris[i + 2];
                tris[i + 2] = swap;
            }

            if (report != null)
            {
                report.Before = 0.0;
                for (int i = 0; i < tris.Count; i += 3)
                    report.Before += 0.5 * Cross(
                        uv[tris[i]], uv[tris[i + 1]], uv[tris[i + 2]]);
            }

            var loops = Boundary(tris);
            if (loops == null || loops.Count == 0) return null;

            // The biggest is the face, the rest are its holes.
            int outer = 0;
            double widest = -1.0;
            var area = new List<double>();
            for (int i = 0; i < loops.Count; i++)
            {
                double a = Math.Abs(Area(uv, loops[i]));
                area.Add(a);
                if (a <= widest) continue;
                widest = a;
                outer = i;
            }

            var drop = Matched(tess, loops, gone, outer);
            var holeRings = new List<List<int>>();
            for (int i = 0; i < loops.Count; i++)
            {
                if (i == outer) continue;
                if (drop.Contains(i))
                {
                    if (report != null) report.Dropped += area[i];
                    continue;
                }
                holeRings.Add(loops[i]);
            }
            if (report != null) report.Loops = loops.Count;

            // PolygonFill answers in the order the points were handed over.
            var order = new List<int>(loops[outer]);
            var outerPts = Points(uv, loops[outer]);
            var holePts = new List<IList<double[]>>();
            foreach (var ring in holeRings)
            {
                holePts.Add(Points(uv, ring));
                order.AddRange(ring);
            }

            var fill = PolygonFill.Triangulate(outerPts, holePts);
            if (fill == null)
            {
                if (log != null)
                    log("small features: a face's boundary could not be filled, "
                        + "so it keeps the triangles it had");
                return null;
            }
            var result = new List<int>(fill.Count);
            foreach (int i in fill) result.Add(order[i]);
            if (report != null)
            {
                report.After = 0.0;
                for (int i = 0; i < result.Count; i += 3)
                    report.After += 0.5 * Cross(
                        uv[result[i]], uv[result[i + 1]], uv[result[i + 2]]);
            }
            return result;
        }

        /// <summary>A face's facets as vertex triples.</summary>
        internal static List<int> FaceTriangles(ITessellation tess, IFace2 face)
        {
            int[] facets = null;
            try { facets = tess.GetFaceFacets(face) as int[]; }
            catch { }
            if (facets == null) return null;
            var tris = new List<int>(facets.Length * 3);
            var pairs = new int[6];
            foreach (int facet in facets)
            {
                int[] fins = null;
                try { fins = tess.GetFacetFins(facet) as int[]; }
                catch { }
                if (fins == null || fins.Length < 3) continue;
                bool ok = true;
                for (int k = 0; k < 3 && ok; k++)
                {
                    int[] fv = null;
                    try { fv = tess.GetFinVertices(fins[k]) as int[]; }
                    catch { }
                    if (fv == null || fv.Length < 2) { ok = false; break; }
                    pairs[k * 2] = fv[0];
                    pairs[k * 2 + 1] = fv[1];
                }
                int a, b, c;
                if (!ok || !FacetStitcher.TryStitch(pairs, out a, out b, out c))
                    continue;
                tris.Add(a);
                tris.Add(b);
                tris.Add(c);
            }
            return tris;
        }

        /// <summary>
        /// The closed loops around a triangulated patch: an edge walked one
        /// way only has nothing on its other side, so it is on the boundary,
        /// and following those edges end to end gives the outline and every
        /// hole in it.
        /// </summary>
        internal static List<List<int>> Boundary(List<int> tris)
        {
            var walked = new HashSet<long>();
            for (int i = 0; i < tris.Count; i += 3)
            {
                walked.Add(Key(tris[i], tris[i + 1]));
                walked.Add(Key(tris[i + 1], tris[i + 2]));
                walked.Add(Key(tris[i + 2], tris[i]));
            }
            var next = new Dictionary<int, List<int>>();
            for (int i = 0; i < tris.Count; i += 3)
            {
                Edge(next, walked, tris[i], tris[i + 1]);
                Edge(next, walked, tris[i + 1], tris[i + 2]);
                Edge(next, walked, tris[i + 2], tris[i]);
            }
            if (next.Count == 0) return null;

            var loops = new List<List<int>>();
            while (next.Count > 0)
            {
                var start = default(KeyValuePair<int, List<int>>);
                foreach (var kv in next) { start = kv; break; }
                int from = start.Key;
                var loop = new List<int>();
                int at = from;
                int guard = 0;
                while (guard++ < 100000)
                {
                    List<int> outs;
                    if (!next.TryGetValue(at, out outs) || outs.Count == 0) break;
                    int to = outs[0];
                    outs.RemoveAt(0);
                    if (outs.Count == 0) next.Remove(at);
                    loop.Add(at);
                    at = to;
                    if (at == from) break;
                }
                if (loop.Count >= 3) loops.Add(loop);
            }
            return loops;
        }

        private static void Edge(
            Dictionary<int, List<int>> next, HashSet<long> walked, int a, int b)
        {
            if (walked.Contains(Key(b, a))) return;      // a face on both sides
            List<int> outs;
            if (!next.TryGetValue(a, out outs)) next[a] = outs = new List<int>();
            outs.Add(b);
        }

        private static long Key(int a, int b)
        {
            return ((long)a << 32) | (uint)b;
        }

        /// <summary>
        /// Which boundary loops of a face are the rims that go: ONE loop per
        /// rim, the nearest that fits, and no loop taken twice.
        ///
        /// One rim per loop matters. A bolt has a chamfer at each end of its
        /// shank, and the two circles of a chamfer are close together and
        /// nearly the same size, so a rule that took every loop within reach
        /// of a rim capped the circle the shank still needs as well: the part
        /// came back with that circle covered twice over rather than open,
        /// which is just as wrong and harder to see.
        ///
        /// skip is the outer loop of the face, which is never a rim.
        /// </summary>
        internal static HashSet<int> Matched(
            ITessellation tess, List<List<int>> loops, IList<Hole> gone,
            int skip = -1)
        {
            var chosen = new HashSet<int>();
            if (gone == null || gone.Count == 0 || loops == null) return chosen;
            var centre = new double[loops.Count][];
            var extent = new double[loops.Count];
            for (int i = 0; i < loops.Count; i++)
                centre[i] = Measure(tess, loops[i], out extent[i]);

            // Every rim that could be every loop, nearest pair first. Rim
            // by rim in the order they were found is not good enough: the
            // first rim can take a loop that belongs to the second, and the
            // second is then left with none and keeps its hole while the
            // feature behind it goes.
            var pairs = new List<int[]>();
            var away = new List<double>();
            for (int h = 0; h < gone.Count; h++)
            {
                var hole = gone[h];
                if (hole == null || hole.Centre == null || hole.Centre.Length < 3)
                    continue;
                // A tessellated loop sits inside the curve it approximates,
                // so it is a little smaller and its middle a little off. A
                // tenth of the rim is far more than that and far less than
                // the gap to the next rim along.
                double slack = Math.Max(hole.Extent * 0.1, 1e-7);
                for (int i = 0; i < loops.Count; i++)
                {
                    if (i == skip || centre[i] == null) continue;
                    double dx = centre[i][0] - hole.Centre[0];
                    double dy = centre[i][1] - hole.Centre[1];
                    double dz = centre[i][2] - hole.Centre[2];
                    if (Math.Abs(dx) > slack || Math.Abs(dy) > slack
                        || Math.Abs(dz) > slack) continue;
                    if (Math.Abs(extent[i] - hole.Extent) > hole.Extent * 0.25)
                        continue;
                    if (!OnRim(tess, loops[i], hole)) continue;
                    pairs.Add(new[] { h, i });
                    away.Add(dx * dx + dy * dy + dz * dz);
                }
            }
            var order = new List<int>();
            for (int i = 0; i < pairs.Count; i++) order.Add(i);
            order.Sort((a, b) => away[a].CompareTo(away[b]));
            var taken = new HashSet<int>();
            foreach (int i in order)
            {
                if (taken.Contains(pairs[i][0]) || chosen.Contains(pairs[i][1]))
                    continue;
                taken.Add(pairs[i][0]);
                chosen.Add(pairs[i][1]);
            }
            return chosen;
        }

        /// <summary>
        /// Whether a boundary loop really is this rim, rather than another
        /// loop of about the same size in about the same place.
        ///
        /// A tessellated point on the boundary of a face sits exactly on the
        /// model edge it came from, so asking the rim's own curves how far
        /// away a few of the loop's points are settles it. Where and how big
        /// alone does not: a part with several holes near each other had one
        /// rim take the loop of the next, which left one hole covered twice
        /// over and the other open.
        /// </summary>
        private static bool OnRim(ITessellation tess, List<int> loop, Hole hole)
        {
            if (hole.Edges == null || hole.Edges.Count == 0) return true;
            double slack = Math.Max(hole.Extent * 0.01, 1e-6);
            int tried = 0;
            int step = Math.Max(1, loop.Count / 4);
            for (int i = 0; i < loop.Count && tried < 4; i += step)
            {
                double[] p = null;
                try { p = tess.GetVertexPoint(loop[i]) as double[]; }
                catch { }
                if (p == null || p.Length < 3) continue;
                tried++;
                bool on = false;
                foreach (var edge in hole.Edges)
                {
                    ICurve curve = null;
                    try { curve = edge.GetCurve() as ICurve; }
                    catch { }
                    if (curve == null) continue;
                    double[] near = null;
                    try { near = curve.GetClosestPointOn(p[0], p[1], p[2]) as double[]; }
                    catch { }
                    if (near == null || near.Length < 3) continue;
                    double dx = near[0] - p[0], dy = near[1] - p[1], dz = near[2] - p[2];
                    if (dx * dx + dy * dy + dz * dz > slack * slack) continue;
                    on = true;
                    break;
                }
                if (!on) return false;
            }
            return tried > 0;
        }

        /// <summary>The middle of a loop and how wide it is, or null when a
        /// point of it cannot be read.</summary>
        private static double[] Measure(
            ITessellation tess, List<int> loop, out double extent)
        {
            extent = 0.0;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (int v in loop)
            {
                double[] p = null;
                try { p = tess.GetVertexPoint(v) as double[]; }
                catch { }
                if (p == null || p.Length < 3) return null;
                if (p[0] < minX) minX = p[0];
                if (p[1] < minY) minY = p[1];
                if (p[2] < minZ) minZ = p[2];
                if (p[0] > maxX) maxX = p[0];
                if (p[1] > maxY) maxY = p[1];
                if (p[2] > maxZ) maxZ = p[2];
            }
            if (minX > maxX) return null;
            double dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
            extent = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            return new[]
            {
                (minX + maxX) * 0.5, (minY + maxY) * 0.5, (minZ + maxZ) * 0.5,
            };
        }

        private static IList<double[]> Points(
            Dictionary<int, double[]> uv, List<int> ring)
        {
            var pts = new List<double[]>(ring.Count);
            foreach (int v in ring) pts.Add(uv[v]);
            return pts;
        }

        private static double Area(Dictionary<int, double[]> uv, List<int> ring)
        {
            double sum = 0.0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = uv[ring[i]];
                var b = uv[ring[(i + 1) % ring.Count]];
                sum += a[0] * b[1] - b[0] * a[1];
            }
            return sum * 0.5;
        }

        private static double Cross(double[] a, double[] b, double[] c)
        {
            return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);
        }
    }
}
