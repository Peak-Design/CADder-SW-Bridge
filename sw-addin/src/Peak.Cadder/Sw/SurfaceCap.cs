using System;
using System.Collections.Generic;
using Peak.Cadder.Core;
using SolidWorks.Interop.sldworks;

namespace Peak.Cadder.Sw
{
    /// <summary>
    /// Caps the holes a CURVED face loses, leaving the rest of the face
    /// exactly as SolidWorks tessellated it.
    ///
    /// A planar face is rebuilt from its own boundary, which is exact and
    /// makes the face itself simpler as well. A curved face cannot be
    /// treated that way: its triangles are what give it its shape, and a
    /// cylinder rebuilt from its outline would come back as a flat sheet.
    ///
    /// So the curved face keeps every triangle it had, and the hole that
    /// went is covered by a lid cut from the hole's own rim. Every point of
    /// that lid is a point SolidWorks already put on the surface, so the
    /// shape is exact everywhere except across the hole, and across the hole
    /// there is nothing left to be exact about. No new vertex, no new
    /// texture coordinate, no crack at the seam: the same three properties
    /// that make the planar fill safe.
    ///
    /// The lid is cut in the plane of the rim rather than in the surface's
    /// parameters. A rim is small by definition, which is what makes it a
    /// small feature, and a small rim is flat enough to cut in its own
    /// plane. Parameters would also fail at a seam, where a closed cylinder
    /// gives one vertex two values and can only store one.
    ///
    /// One exception to "no new vertex": a rim whose normals do not
    /// describe the lid. At the end circle of a cylinder the normals are
    /// radial, square to the lid, and a lid that used them would shade
    /// dark and wrong. Such a lid is marked Flat, and the tessellator gives
    /// it its own copies of the rim points with the lid's normal, the same
    /// as every other face carries its own copies of the points on its
    /// edges. The copies sit exactly on the rim, so nothing cracks.
    /// </summary>
    public static class SurfaceCap
    {
        /// <summary>A rim normal further than this from the lid's own
        /// normal does not describe the lid (cos 60 degrees). A lid over a
        /// cross hole in a shaft keeps the shaft's normals, and shades as
        /// the shaft does.</summary>
        internal const double SameFacing = 0.5;

        /// <summary>One lid to add to a face.</summary>
        public sealed class Piece
        {
            /// <summary>Three body vertex indices per triangle, wound
            /// against the rim.</summary>
            public List<int> Triangles;

            /// <summary>The way the lid faces, as its triangles are wound.
            /// </summary>
            public double[] Normal;

            /// <summary>True when the lid needs its own copies of the rim
            /// points, with Normal, because the rim's normals do not
            /// describe it.</summary>
            public bool Flat;
        }

        /// <summary>
        /// The lids to ADD to this face, or null when there is nothing to
        /// cap. The face keeps its own triangles either way.
        /// </summary>
        public static List<Piece> Build(
            IFace2 face, ITessellation tess, IList<PlaneRefill.Hole> gone,
            Action<string> log)
        {
            if (face == null || tess == null || gone == null || gone.Count == 0)
                return null;

            var tris = PlaneRefill.FaceTriangles(tess, face);
            if (tris == null || tris.Count < 3) return null;

            // One winding for the lot, so that an edge with a triangle on
            // both sides is walked both ways and the rims stand out. The
            // planar fill settles this from the face parameters. A curved
            // face is settled from the normals SolidWorks gave the points,
            // which is the same test the tessellator itself uses.
            var point = new Dictionary<int, double[]>();
            var normal = new Dictionary<int, double[]>();
            foreach (int v in tris)
            {
                if (point.ContainsKey(v)) continue;
                double[] p = null, n = null;
                try { p = tess.GetVertexPoint(v) as double[]; }
                catch { }
                try { n = tess.GetVertexNormal(v) as double[]; }
                catch { }
                if (p == null || p.Length < 3) return null;
                point[v] = p;
                normal[v] = n != null && n.Length >= 3 ? n : new double[3];
            }
            for (int i = 0; i < tris.Count; i += 3)
            {
                var a = point[tris[i]];
                var b = point[tris[i + 1]];
                var c = point[tris[i + 2]];
                var n = new double[3];
                for (int k = 0; k < 3; k++)
                    n[k] = normal[tris[i]][k] + normal[tris[i + 1]][k]
                        + normal[tris[i + 2]][k];
                if (!FacetStitcher.NeedsFlip(a, b, c, n)) continue;
                int swap = tris[i + 1];
                tris[i + 1] = tris[i + 2];
                tris[i + 2] = swap;
            }

            var loops = PlaneRefill.Boundary(tris);
            if (loops == null || loops.Count == 0) return null;

            var rims = PlaneRefill.Matched(tess, loops, gone);
            List<Piece> caps = null;
            foreach (int i in rims)
            {
                double[] facing;
                var lid = Lid(loops[i], point, out facing);
                if (lid == null)
                {
                    if (log != null)
                        log("small features: a rim on a curved face could not be "
                            + "capped, so the face keeps the hole it had");
                    continue;
                }
                if (caps == null) caps = new List<Piece>();
                caps.Add(new Piece
                {
                    Triangles = lid,
                    Normal = facing,
                    Flat = !RimDescribes(loops[i], normal, facing),
                });
            }
            return caps;
        }

        /// <summary>How many triangles the lids add. 0 for none.</summary>
        public static int TriangleCount(List<Piece> pieces)
        {
            int count = 0;
            if (pieces == null) return count;
            foreach (var piece in pieces) count += piece.Triangles.Count / 3;
            return count;
        }

        /// <summary>
        /// Whether every normal on the rim is near enough to the lid's own
        /// normal for the lid to shade with them. A missing normal does
        /// not describe anything.
        /// </summary>
        internal static bool RimDescribes(
            List<int> ring, Dictionary<int, double[]> normal, double[] facing)
        {
            foreach (int v in ring)
            {
                double[] n;
                if (!normal.TryGetValue(v, out n) || n == null || n.Length < 3)
                    return false;
                double length = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
                if (!(length > 0.0)) return false;
                double dot = (n[0] * facing[0] + n[1] * facing[1] + n[2] * facing[2])
                    / length;
                if (dot < SameFacing) return false;
            }
            return true;
        }

        /// <summary>
        /// A lid for one rim: the rim laid flat in its own plane, cut into
        /// triangles, and given back as body vertex indices. Null when the
        /// rim encloses nothing that can be cut.
        /// </summary>
        internal static List<int> Lid(List<int> ring, Dictionary<int, double[]> point)
        {
            double[] facing;
            return Lid(ring, point, out facing);
        }

        /// <summary>
        /// The lid, and the way it faces as its triangles are wound: against
        /// the ring, so opposite to the ring's own Newell normal.
        /// </summary>
        internal static List<int> Lid(
            List<int> ring, Dictionary<int, double[]> point, out double[] facing)
        {
            facing = null;
            if (ring == null || ring.Count < 3) return null;

            // Newell: the area-weighted normal of a ring, which is right for
            // a rim that is not quite flat and needs no point to be picked
            // out as special.
            double nx = 0.0, ny = 0.0, nz = 0.0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = point[ring[i]];
                var b = point[ring[(i + 1) % ring.Count]];
                nx += (a[1] - b[1]) * (a[2] + b[2]);
                ny += (a[2] - b[2]) * (a[0] + b[0]);
                nz += (a[0] - b[0]) * (a[1] + b[1]);
            }
            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (!(length > 0.0)) return null;
            var n = new[] { nx / length, ny / length, nz / length };

            var e1 = Across(n);
            if (e1 == null) return null;
            var e2 = new[]
            {
                n[1] * e1[2] - n[2] * e1[1],
                n[2] * e1[0] - n[0] * e1[2],
                n[0] * e1[1] - n[1] * e1[0],
            };

            var flat = new List<double[]>(ring.Count);
            var origin = point[ring[0]];
            foreach (int v in ring)
            {
                var p = point[v];
                double dx = p[0] - origin[0], dy = p[1] - origin[1], dz = p[2] - origin[2];
                flat.Add(new[]
                {
                    dx * e1[0] + dy * e1[1] + dz * e1[2],
                    dx * e2[0] + dy * e2[1] + dz * e2[2],
                });
            }

            var fill = PolygonFill.Triangulate(flat, null);
            if (fill == null) return null;

            // The winding comes from the rim, not from the normals. The
            // ring runs the way the face's own triangles run along it, and
            // a closed mesh needs the lid to run every rim edge the other
            // way. The normals cannot say which way that is at the end
            // circle of a cylinder: they are radial, square to the lid, and
            // the flip test read nothing from them. So every lid triangle
            // is turned against the ring, and BodyTessellator adds it as it
            // is.
            double ringArea = 0.0;
            for (int i = 0; i < flat.Count; i++)
            {
                var a = flat[i];
                var b = flat[(i + 1) % flat.Count];
                ringArea += a[0] * b[1] - b[0] * a[1];
            }
            var lid = new List<int>(fill.Count);
            for (int t = 0; t + 2 < fill.Count; t += 3)
            {
                var p = flat[fill[t]];
                var q = flat[fill[t + 1]];
                var r = flat[fill[t + 2]];
                double area = (q[0] - p[0]) * (r[1] - p[1]) - (r[0] - p[0]) * (q[1] - p[1]);
                bool withRing = area * ringArea > 0.0;
                lid.Add(ring[fill[t]]);
                lid.Add(ring[fill[withRing ? t + 2 : t + 1]]);
                lid.Add(ring[fill[withRing ? t + 1 : t + 2]]);
            }
            // The ring runs counterclockwise about n (e1, e2 and n are a
            // right-handed frame, and ringArea is its signed area), so the
            // lid, wound against it, faces the other way.
            facing = ringArea > 0.0 ? new[] { -n[0], -n[1], -n[2] } : n;
            return lid;
        }

        /// <summary>A unit vector across the given one.</summary>
        private static double[] Across(double[] n)
        {
            var axis = Math.Abs(n[0]) < 0.9 ? new[] { 1.0, 0.0, 0.0 }
                                            : new[] { 0.0, 1.0, 0.0 };
            var v = new[]
            {
                axis[1] * n[2] - axis[2] * n[1],
                axis[2] * n[0] - axis[0] * n[2],
                axis[0] * n[1] - axis[1] * n[0],
            };
            double length = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            if (!(length > 0.0)) return null;
            return new[] { v[0] / length, v[1] / length, v[2] / length };
        }
    }
}
