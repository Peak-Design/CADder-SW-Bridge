using System;
using System.Collections.Generic;
using System.Globalization;
using SolidWorks.Interop.sldworks;

namespace Peak.Cadder.Sw
{
    /// <summary>
    /// Does the body still close after the small features come out?
    ///
    /// A body SolidWorks tessellates is a closed solid: every edge of it has
    /// a triangle on each side. Take a feature out and the body is open
    /// unless the face that owned the rim is dealt with, so the count of
    /// edges with only one triangle on them is the whole contract in one
    /// number. Zero, or the part has a hole in it.
    ///
    /// The count is by POSITION, not by vertex index. SolidWorks shares a
    /// tessellated vertex within one face and never across faces, so two
    /// triangles either side of a model edge carry different indices for the
    /// same point. Rounding to a tenth of a micron is far finer than any
    /// tessellation tolerance and far coarser than the rounding between two
    /// faces reading one point.
    /// </summary>
    public static class ClosureCheck
    {
        /// <summary>A hundred nanometres: the grid positions are matched on.</summary>
        private const double Grid = 1e-7;

        public sealed class Report
        {
            public int Open;                 // edges with one triangle on them
            /// <summary>Edges with MORE than two, which is a face covered
            /// twice over: not a hole, and just as wrong.</summary>
            public int Doubled;
            public int Triangles;
            /// <summary>The faces an open edge runs along, at most a few, as
            /// the surface type and how the plan treated the face.</summary>
            public List<string> Where = new List<string>();
        }

        /// <summary>
        /// Counts the open edges of what a plan would send. plan may be null,
        /// which measures the body as SolidWorks tessellated it and should
        /// always answer zero.
        /// </summary>
        public static Report Run(
            IBody2 body, ITessellation tess, SmallFeatureSurvey.Plan plan,
            Action<string> log)
        {
            var report = new Report();
            if (body == null || tess == null) return report;
            object[] faces = null;
            try { faces = body.GetFaces() as object[]; }
            catch { return report; }
            if (faces == null) return report;

            var used = new Dictionary<long, int>();
            var owner = new Dictionary<long, string>();
            var places = new Dictionary<int, long>();

            foreach (var o in faces)
            {
                var face = o as IFace2;
                if (face == null) continue;
                if (plan != null && plan.IsGone(face)) continue;

                List<int> tris = null;
                string how = Kind(face);
                IList<PlaneRefill.Hole> rims = null;
                if (plan != null)
                {
                    int at = plan.FillAt(face);
                    if (at >= 0)
                    {
                        rims = plan.FillHoles[at];
                        tris = PlaneRefill.Build(face, tess, rims, log);
                        how += tris == null ? ", fill refused" : ", refilled";
                    }
                    else
                    {
                        int lid = plan.CapAt(face);
                        if (lid >= 0) rims = plan.CapHoles[lid];
                    }
                }
                if (tris != null) rims = null;          // the fill covered them
                if (tris == null) tris = PlaneRefill.FaceTriangles(tess, face);
                if (tris == null) continue;
                if (rims != null)
                {
                    var cap = SurfaceCap.Build(face, tess, rims, log);
                    if (cap != null)
                    {
                        tris = new List<int>(tris);
                        tris.AddRange(cap);
                        how += ", capped";
                    }
                    else how += ", cap refused";
                }

                for (int i = 0; i + 2 < tris.Count; i += 3)
                {
                    report.Triangles++;
                    long a = Place(tess, tris[i], places);
                    long b = Place(tess, tris[i + 1], places);
                    long c = Place(tess, tris[i + 2], places);
                    Count(used, owner, a, b, how);
                    Count(used, owner, b, c, how);
                    Count(used, owner, c, a, how);
                }
            }

            var named = new List<string>();
            foreach (var kv in used)
            {
                if (kv.Value == 2) continue;
                if (kv.Value > 2) report.Doubled++;
                else report.Open++;
                string where;
                if (!owner.TryGetValue(kv.Key, out where)) continue;
                if (!named.Contains(where) && named.Count < 8) named.Add(where);
            }
            report.Where = named;
            return report;
        }

        private static void Count(
            Dictionary<long, int> used, Dictionary<long, string> owner,
            long a, long b, string how)
        {
            long key = a < b ? a * 1000003L + b : b * 1000003L + a;
            int had;
            used[key] = used.TryGetValue(key, out had) ? had + 1 : 1;
            string was;
            owner[key] = owner.TryGetValue(key, out was) && was != how
                ? was + " | " + how : how;
        }

        /// <summary>A number for a point, the same for two faces reading one
        /// corner of the model.</summary>
        private static long Place(
            ITessellation tess, int vertex, Dictionary<int, long> places)
        {
            long place;
            if (places.TryGetValue(vertex, out place)) return place;
            double[] p = null;
            try { p = tess.GetVertexPoint(vertex) as double[]; }
            catch { }
            if (p == null || p.Length < 3) { places[vertex] = 0; return 0; }
            unchecked
            {
                long hash = 17;
                for (int k = 0; k < 3; k++)
                    hash = hash * 1000003L
                        + (long)Math.Round(p[k] / Grid);
                places[vertex] = hash;
                return hash;
            }
        }

        private static string Kind(IFace2 face)
        {
            try
            {
                var surface = face.GetSurface() as ISurface;
                if (surface == null) return "?";
                if (surface.IsPlane()) return "plane";
                if (surface.IsCylinder()) return "cylinder";
                if (surface.IsCone()) return "cone";
                if (surface.IsSphere()) return "sphere";
                if (surface.IsTorus()) return "torus";
                return "surface";
            }
            catch { return "?"; }
        }

        public static string Describe(Report report)
        {
            if (report == null) return "";
            return string.Format(CultureInfo.InvariantCulture,
                "{0} open edge(s) of {1} triangle(s)", report.Open, report.Triangles);
        }
    }
}
