using System;
using System.Collections.Generic;

namespace Peak.Cadder.Core
{
    /// <summary>
    /// Triangulates a flat polygon that may have holes. Pure arithmetic: no
    /// SolidWorks, no Blender, no floating dependency, and therefore the one
    /// part of removing small features that can be tested outright.
    ///
    /// This is what lets a planar face be sent as its own boundary rather
    /// than as the triangles SolidWorks drew round its holes. The plate with
    /// fifty bolt holes becomes two triangles a side.
    ///
    /// The method is the usual one. Each hole is bridged into the outer ring
    /// by a pair of coincident edges, which turns a polygon with holes into
    /// one simple ring, and the ring is then cut ear by ear. Ear clipping is
    /// O(n squared), which is the right trade here: a face's boundary is
    /// tens of points, not thousands, and a simple algorithm that can be
    /// read and tested beats a fast one that cannot.
    ///
    /// Nothing here guesses. Given a boundary it cannot cut, it returns null
    /// rather than a broken fill, and the caller leaves that face exactly as
    /// SolidWorks tessellated it. "Simplified" or "left alone", never
    /// "repaired".
    /// </summary>
    public static class PolygonFill
    {
        /// <summary>
        /// Triangles for a polygon with holes, as indices into the points
        /// laid end to end: the outer ring first, then each hole in the
        /// order given. Three indices per triangle, wound the same way as
        /// the outer ring. Null when the boundary cannot be cut.
        ///
        /// Rings are open: do not repeat the first point at the end.
        /// </summary>
        public static List<int> Triangulate(
            IList<double[]> outer, IList<IList<double[]>> holes)
        {
            if (outer == null || outer.Count < 3) return null;

            var points = new List<double[]>();
            foreach (var p in outer)
            {
                if (p == null || p.Length < 2) return null;
                points.Add(p);
            }
            var rings = new List<List<int>>();
            var outerRing = new List<int>();
            for (int i = 0; i < outer.Count; i++) outerRing.Add(i);
            double outerArea = SignedArea(points, outerRing);
            // Points in a line enclose nothing. There is no fill to give,
            // and saying so beats handing back a triangle of no area.
            if (Math.Abs(outerArea) <= 0.0) return null;
            if (outerArea < 0.0) outerRing.Reverse();

            if (holes != null)
            {
                foreach (var hole in holes)
                {
                    if (hole == null || hole.Count < 3) continue;
                    var ring = new List<int>();
                    foreach (var p in hole)
                    {
                        if (p == null || p.Length < 2) return null;
                        ring.Add(points.Count);
                        points.Add(p);
                    }
                    // A hole runs the other way round, so that the bridge
                    // into the outer ring folds back on itself and leaves
                    // one simple ring behind.
                    if (SignedArea(points, ring) > 0.0) ring.Reverse();
                    rings.Add(ring);
                }
            }

            // LEFTMOST hole first, because a bridge reaches left. The
            // first one reaches the outer ring, and every one after it can
            // reach a hole already spliced in as well, which is what makes a
            // row of holes across a plate work: the third hole in a line has
            // no clear run to the outer ring at all, and does not need one.
            rings.Sort((a, b) => Leftmost(points, a).CompareTo(Leftmost(points, b)));

            var ring2 = outerRing;
            for (int h = 0; h < rings.Count; h++)
            {
                ring2 = Bridge(points, ring2, rings[h], rings, h + 1);
                if (ring2 == null) return null;
            }
            return Clip(points, ring2);
        }

        /// <summary>The x of a ring's leftmost point.</summary>
        private static double Leftmost(List<double[]> points, List<int> ring)
        {
            double x = double.MaxValue;
            foreach (int i in ring) if (points[i][0] < x) x = points[i][0];
            return x;
        }

        /// <summary>
        /// Splices one hole into a ring along a bridge, leaving a single
        /// simple ring.
        ///
        /// A bridge is a segment from the hole out to the ring that crosses
        /// nothing and stays inside the material. Rather than reason about
        /// which one that will be, this tries them, nearest first, and takes
        /// the first that passes. The nearest almost always passes, so the
        /// cost is a few tests per hole, and what makes it right is a test
        /// that can be read rather than a rule that has to be trusted.
        /// </summary>
        private static List<int> Bridge(
            List<double[]> points, List<int> ring, List<int> hole,
            List<List<int>> rings, int firstUnbridged)
        {
            int at = 0;
            for (int k = 1; k < hole.Count; k++)
            {
                var p = points[hole[k]];
                var best = points[hole[at]];
                if (p[0] < best[0] || (p[0] == best[0] && p[1] < best[1])) at = k;
            }
            int from = hole[at];

            var order = new List<int>(ring.Count);
            for (int i = 0; i < ring.Count; i++) order.Add(i);
            order.Sort((a, b) => Distance2(points[from], points[ring[a]])
                          .CompareTo(Distance2(points[from], points[ring[b]])));

            foreach (int mi in order)
            {
                if (!Clear(points, from, ring[mi], ring, hole, rings, firstUnbridged))
                    continue;
                var spliced = new List<int>(ring.Count + hole.Count + 2);
                for (int i = 0; i <= mi; i++) spliced.Add(ring[i]);
                for (int k = 0; k <= hole.Count; k++)
                    spliced.Add(hole[(at + k) % hole.Count]);
                spliced.Add(ring[mi]);
                for (int i = mi + 1; i < ring.Count; i++) spliced.Add(ring[i]);
                return spliced;
            }
            return null;
        }

        /// <summary>
        /// Whether the segment from a to b can serve as a bridge: it crosses
        /// no edge of the ring, of the hole it comes from, or of a hole still
        /// waiting, and its middle lies in the material rather than outside
        /// the ring or down a hole.
        ///
        /// The hole it comes FROM has to be tested like any other. At the
        /// leftmost point of a circle the boundary runs straight up and down,
        /// so every direction with any rightward lean sets off INTO the hole,
        /// and a bridge that starts there and aims right is inside the hole
        /// from its first millimetre.
        /// </summary>
        private static bool Clear(
            List<double[]> points, int a, int b, List<int> ring, List<int> hole,
            List<List<int>> rings, int firstUnbridged)
        {
            if (a == b) return false;
            if (Crosses(points, a, b, ring)) return false;
            if (Crosses(points, a, b, hole)) return false;
            for (int h = firstUnbridged; h < rings.Count; h++)
                if (Crosses(points, a, b, rings[h])) return false;

            var mid = new[]
            {
                (points[a][0] + points[b][0]) * 0.5,
                (points[a][1] + points[b][1]) * 0.5,
            };
            if (!Inside(points, ring, mid)) return false;
            if (Inside(points, hole, mid)) return false;
            for (int h = firstUnbridged; h < rings.Count; h++)
                if (Inside(points, rings[h], mid)) return false;
            return true;
        }

        private static bool Crosses(
            List<double[]> points, int a, int b, List<int> ring)
        {
            for (int i = 0; i < ring.Count; i++)
            {
                int c = ring[i], d = ring[(i + 1) % ring.Count];
                if (c == a || c == b || d == a || d == b) continue;
                if (SegmentsCross(points[a], points[b], points[c], points[d]))
                    return true;
            }
            return false;
        }

        /// <summary>A proper crossing, or an end of one segment lying in the
        /// middle of the other. Two segments merely meeting at a shared point
        /// are the caller's business and never reach here.</summary>
        private static bool SegmentsCross(
            double[] a, double[] b, double[] c, double[] d)
        {
            double d1 = Cross(c, d, a), d2 = Cross(c, d, b);
            double d3 = Cross(a, b, c), d4 = Cross(a, b, d);
            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
                && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
            if (d1 == 0 && Between(c, d, a)) return true;
            if (d2 == 0 && Between(c, d, b)) return true;
            if (d3 == 0 && Between(a, b, c)) return true;
            if (d4 == 0 && Between(a, b, d)) return true;
            return false;
        }

        private static bool Between(double[] a, double[] b, double[] p)
        {
            return Math.Min(a[0], b[0]) <= p[0] && p[0] <= Math.Max(a[0], b[0])
                && Math.Min(a[1], b[1]) <= p[1] && p[1] <= Math.Max(a[1], b[1]);
        }

        /// <summary>Ray crossing, to the right.</summary>
        private static bool Inside(
            List<double[]> points, List<int> ring, double[] p)
        {
            bool inside = false;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = points[ring[i]];
                var b = points[ring[(i + 1) % ring.Count]];
                if ((a[1] > p[1]) == (b[1] > p[1])) continue;
                double x = a[0] + (p[1] - a[1]) * (b[0] - a[0]) / (b[1] - a[1]);
                if (x > p[0]) inside = !inside;
            }
            return inside;
        }

        private static double Distance2(double[] a, double[] b)
        {
            double dx = a[0] - b[0], dy = a[1] - b[1];
            return dx * dx + dy * dy;
        }

        /// <summary>Cuts a simple ring ear by ear.</summary>
        private static List<int> Clip(List<double[]> points, List<int> ring)
        {
            var left = new List<int>(ring);
            var result = new List<int>();
            int stall = 0;
            while (left.Count > 3)
            {
                int n = left.Count;
                bool cut = false;
                for (int i = 0; i < n; i++)
                {
                    int ia = left[(i + n - 1) % n], ib = left[i], ic = left[(i + 1) % n];
                    if (Cross(points[ia], points[ib], points[ic]) <= 0.0) continue;
                    // A point in the ear blocks it, edges included: a
                    // reflex corner sitting exactly on the line of an ear
                    // would be cut off by it, and the notch of an L would
                    // fill in.
                    //
                    // Unless it is in the same PLACE as one of the ear's own
                    // corners, which is not another point at all. A bridge
                    // lays two edges on top of each other and puts a second
                    // copy of its endpoints into the ring, and two samples of
                    // a curve can land on one spot; testing by index alone
                    // called those blockers and gave up on any face with
                    // three holes or more.
                    bool blocked = false;
                    for (int k = 0; k < n && !blocked; k++)
                    {
                        int id = left[k];
                        if (id == ia || id == ib || id == ic) continue;
                        var p = points[id];
                        if (Samey(p, points[ia]) || Samey(p, points[ib])
                            || Samey(p, points[ic])) continue;
                        if (InTriangle(points[ia][0], points[ia][1],
                                       points[ib][0], points[ib][1],
                                       points[ic][0], points[ic][1], p[0], p[1]))
                            blocked = true;
                    }
                    if (blocked) continue;
                    result.Add(ia);
                    result.Add(ib);
                    result.Add(ic);
                    left.RemoveAt(i);
                    cut = true;
                    break;
                }
                if (cut) { stall = 0; continue; }

                // No ear anywhere. A bridge lays two edges over each
                // other and leaves a second copy of its endpoints in the
                // ring, and a ring like that can have no ear to cut. Dropping
                // one of those copies changes nothing, because the point it
                // stood on is still in the ring.
                //
                // Dropping a REAL corner is a different thing and this used
                // to do it. The fill then stopped short of the boundary while
                // the face next door still had triangles reaching it, and the
                // part came back with a crack a few edges long. A fill either
                // covers its ring or there is no fill: the face keeps the
                // triangles it had.
                if (++stall > 1) return null;
                int spare = Duplicate(points, left);
                if (spare < 0) return null;
                left.RemoveAt(spare);
            }
            // The last three. THREE REAL POINTS in a line have no triangle
            // left to give: emitting one of no area puts a black facet in the
            // mesh, and leaving it out stops the fill short of its ring,
            // which cracks the part along the boundary. Neither is a fill, so
            // it is refused and the face keeps the triangles it had.
            //
            // Three points where two are in the same PLACE are a different
            // thing. The ring has already closed on itself there, its two
            // remaining edges run the same line in opposite directions, and
            // dropping the lot leaves nothing uncovered.
            if (left.Count == 3
                && Cross(points[left[0]], points[left[1]], points[left[2]]) > 0.0)
            {
                result.Add(left[0]);
                result.Add(left[1]);
                result.Add(left[2]);
            }
            else if (left.Count != 3 || Duplicate(points, left) < 0)
                return null;
            return result.Count >= 3 ? result : null;
        }

        /// <summary>The vertex whose corner is nearest to straight.</summary>
        /// <summary>
        /// A point in the ring that sits exactly where its neighbour does,
        /// and can therefore be dropped without changing what the ring
        /// covers. Minus one when there is none.
        /// </summary>
        private static int Duplicate(List<double[]> points, List<int> ring)
        {
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var here = points[ring[i]];
                if (Samey(here, points[ring[(i + 1) % n]])
                    || Samey(here, points[ring[(i + n - 1) % n]]))
                    return i;
            }
            return -1;
        }

        private static double Cross(double[] a, double[] b, double[] c)
        {
            return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);
        }

        private static double SignedArea(List<double[]> points, List<int> ring)
        {
            double sum = 0.0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = points[ring[i]];
                var b = points[ring[(i + 1) % ring.Count]];
                sum += a[0] * b[1] - b[0] * a[1];
            }
            return sum * 0.5;
        }

        /// <summary>Two points in the same place.</summary>
        private static bool Samey(double[] a, double[] b)
        {
            return a[0] == b[0] && a[1] == b[1];
        }

        private static bool InTriangle(
            double ax, double ay, double bx, double by, double cx, double cy,
            double px, double py)
        {
            double d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
            double d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
            double d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0;
            bool pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }
    }
}
