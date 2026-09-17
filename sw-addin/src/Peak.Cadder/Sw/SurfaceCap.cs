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
    /// </summary>
    public static class SurfaceCap
    {
        /// <summary>
        /// The triangles to ADD to this face, three body vertex indices
        /// each, or null when there is nothing to cap. The face keeps its
        /// own triangles either way.
        /// </summary>
        public static List<int> Build(
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
            List<int> caps = null;
            foreach (int i in rims)
            {
                var lid = Lid(loops[i], point);
                if (lid == null)
                {
                    if (log != null)
                        log("small features: a rim on a curved face could not be "
                            + "capped, so the face keeps the hole it had");
                    continue;
                }
                if (caps == null) caps = new List<int>();
                caps.AddRange(lid);
            }
            return caps;
        }

        /// <summary>
        /// A lid for one rim: the rim laid flat in its own plane, cut into
        /// triangles, and given back as body vertex indices. Null when the
        /// rim encloses nothing that can be cut.
        /// </summary>
        private static List<int> Lid(List<int> ring, Dictionary<int, double[]> point)
        {
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
            var lid = new List<int>(fill.Count);
            foreach (int i in fill) lid.Add(ring[i]);
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
