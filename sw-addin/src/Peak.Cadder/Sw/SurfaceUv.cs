using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Peak.Cadder.Core.Model;
using SolidWorks.Interop.sldworks;

namespace Peak.Cadder.Sw
{
    /// <summary>
    /// Turns the surface parameters SolidWorks reports into a UV map a
    /// texture can go on.
    ///
    /// A tessellated vertex carries the parameters of the face it belongs
    /// to, and those are in the SURFACE's own units. On a plane they are
    /// metres. On a cylinder u is an ANGLE in radians and v is a length, so
    /// a 40 mm bore 10 mm deep measures 6.28 by 0.01. Written straight into
    /// a UV map that is an island six hundred times wider than it is tall,
    /// beside a plane that is 0.04 by 0.01. Some faces looked right and the
    /// rest looked stretched, which is exactly what one would expect
    /// (Oscar, 2026-09-17).
    ///
    /// The cure is to scale each direction by how much real length one unit
    /// of it covers, which puts both into metres. On a cylinder that turns u
    /// into arc length, so the island comes out circumference by height and
    /// a square texture stays square on the part. The STEP route has done
    /// this since it began, and this is the same rule for the live link.
    ///
    /// The scale comes from the TRIANGLES, not from the surface. For every
    /// triangle edge the parameter step and the real length are both
    /// already known, so a*du^2 + b*dv^2 = L^2 is one equation in two
    /// unknowns and a face gives hundreds of them. The least squares answer
    /// is exact on a plane, a cylinder and a cone, and a fair average on a
    /// sphere, a torus or a spline, where the true scale changes across the
    /// face. It also costs no COM calls, which asking the surface would.
    ///
    /// Faces that lie on ONE surface share a frame, so the two patches a
    /// cut leaves of a cylinder line up instead of landing on top of each
    /// other.
    /// </summary>
    internal static class SurfaceUv
    {
        /// <summary>
        /// How far each chart is moved off the UV origin, in metres.
        ///
        /// Every chart starts at the same place, so two faces of the same
        /// size write the same UVs. Where such faces meet, both sides of
        /// the shared edge carry one UV and Blender reads that as a single
        /// island folded on itself. The offset is the chart's own, so it is
        /// the same on every send, and small enough to be invisible.
        /// </summary>
        public const double ChartNudge = 0.001;

        /// <summary>
        /// Rewrites the UVs of the vertices this body just added. Returns
        /// how many it rewrote.
        ///
        /// faceOf says which face each vertex belongs to, as an index into
        /// faces, or -1 where nothing claimed it. A tessellated vertex
        /// belongs to exactly one face: facet topology matching shares
        /// vertices inside a face and never across one, which PlaneUvCheck
        /// measured over the whole corpus.
        /// </summary>
        public static int Apply(
            MeshDefinition mesh, int baseVertex, int vertexCount,
            int firstTriangle, IList<IFace2> faces, int[] faceOf,
            Action<string> log)
        {
            if (mesh == null || faces == null || faceOf == null) return 0;
            if (vertexCount <= 0 || faces.Count == 0) return 0;
            if (mesh.Uvs.Count < (baseVertex + vertexCount) * 2) return 0;
            if (faceOf.Length < vertexCount) return 0;

            var chartOf = new int[faces.Count];
            int charts = Charts(faces, chartOf);
            return Rewrite(mesh, baseVertex, vertexCount, firstTriangle,
                           chartOf, charts, faceOf, log);
        }

        /// <summary>
        /// The arithmetic on its own: the faces have already been sorted
        /// into charts, and nothing below here asks SolidWorks anything.
        /// chartOf gives the chart of each face, faceOf the face of each
        /// vertex.
        /// </summary>
        internal static int Rewrite(
            MeshDefinition mesh, int baseVertex, int vertexCount,
            int firstTriangle, int[] chartOf, int charts, int[] faceOf,
            Action<string> log)
        {
            if (mesh == null || chartOf == null || faceOf == null) return 0;
            if (charts <= 0 || vertexCount <= 0) return 0;
            if (mesh.Uvs.Count < (baseVertex + vertexCount) * 2) return 0;
            if (mesh.Positions.Count < (baseVertex + vertexCount) * 3) return 0;
            if (faceOf.Length < vertexCount) return 0;

            var fits = new Fit[charts];
            for (int i = 0; i < charts; i++) fits[i] = new Fit();

            for (int t = firstTriangle; t + 2 < mesh.Triangles.Count; t += 3)
            {
                int a = mesh.Triangles[t] - baseVertex;
                int b = mesh.Triangles[t + 1] - baseVertex;
                int c = mesh.Triangles[t + 2] - baseVertex;
                if (a < 0 || b < 0 || c < 0
                    || a >= vertexCount || b >= vertexCount || c >= vertexCount)
                    continue;
                int face = faceOf[a];
                if (face < 0 || face >= chartOf.Length) continue;
                var fit = fits[chartOf[face]];
                fit.Edge(mesh, baseVertex, a, b);
                fit.Edge(mesh, baseVertex, b, c);
                fit.Edge(mesh, baseVertex, c, a);
            }

            foreach (var fit in fits) fit.Solve();

            // The fit again, over the MIDDLE of the face rather than its
            // longest edges. Least squares works on squared lengths, so a
            // surface whose scale changes across it (a worm thread, a
            // spline) is answered for its coarsest corner and the rest of
            // it comes out many times too small. The aspect ratio the fit
            // found is kept; only the size is put back where most of the
            // triangles are.
            for (int t = firstTriangle; t + 2 < mesh.Triangles.Count; t += 3)
            {
                int a = mesh.Triangles[t] - baseVertex;
                int b = mesh.Triangles[t + 1] - baseVertex;
                int c = mesh.Triangles[t + 2] - baseVertex;
                if (a < 0 || b < 0 || c < 0
                    || a >= vertexCount || b >= vertexCount || c >= vertexCount)
                    continue;
                int face = faceOf[a];
                if (face < 0 || face >= chartOf.Length) continue;
                fits[chartOf[face]].Sample(mesh, baseVertex, a, b, c);
            }
            foreach (var fit in fits) fit.Centre();

            // Where each chart starts, in the metric the fit just found.
            for (int v = 0; v < vertexCount; v++)
            {
                int face = faceOf[v];
                if (face < 0 || face >= chartOf.Length) continue;
                var fit = fits[chartOf[face]];
                fit.See(mesh.Uvs[(baseVertex + v) * 2] * fit.Du,
                        mesh.Uvs[(baseVertex + v) * 2 + 1] * fit.Dv);
            }
            for (int i = 0; i < charts; i++) fits[i].Place(i);

            int written = 0, orphans = 0;
            for (int v = 0; v < vertexCount; v++)
            {
                int face = faceOf[v];
                if (face < 0 || face >= chartOf.Length) { orphans++; continue; }
                var fit = fits[chartOf[face]];
                int at = (baseVertex + v) * 2;
                mesh.Uvs[at] = mesh.Uvs[at] * fit.Du - fit.UMin + fit.NudgeU;
                mesh.Uvs[at + 1] = mesh.Uvs[at + 1] * fit.Dv - fit.VMin + fit.NudgeV;
                written++;
            }

            if (log != null && orphans > 0)
                log("surface uv: " + orphans + " vertex(es) belonged to no face "
                    + "and keep the parameters SolidWorks gave them");
            return written;
        }

        /// <summary>Numbers the charts: one per surface, so patches of one
        /// cylinder share a frame. A surface this cannot name, such as a
        /// spline, gets a chart to itself, which is a chart per face and the
        /// safe answer.</summary>
        private static int Charts(IList<IFace2> faces, int[] chartOf)
        {
            var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
            int charts = 0;
            for (int i = 0; i < faces.Count; i++)
            {
                string key = KeyOf(faces[i]);
                if (key == null) { chartOf[i] = charts++; continue; }
                int found;
                if (!byKey.TryGetValue(key, out found))
                {
                    found = charts++;
                    byKey[key] = found;
                }
                chartOf[i] = found;
            }
            return charts;
        }

        /// <summary>What surface this face lies on, as something hashable,
        /// or null for one this cannot compare.</summary>
        private static string KeyOf(IFace2 face)
        {
            ISurface surface = null;
            try { surface = face == null ? null : face.GetSurface() as ISurface; }
            catch { }
            if (surface == null) return null;

            string kind = null;
            double[] values = null;
            try
            {
                if (surface.IsPlane())
                {
                    kind = "plane";
                    values = surface.PlaneParams as double[];
                }
                else if (surface.IsCylinder())
                {
                    kind = "cylinder";
                    values = surface.CylinderParams as double[];
                }
                else if (surface.IsCone())
                {
                    kind = "cone";
                    values = surface.ConeParams as double[];
                }
                else if (surface.IsSphere())
                {
                    kind = "sphere";
                    values = surface.SphereParams as double[];
                }
                else if (surface.IsTorus())
                {
                    kind = "torus";
                    values = surface.TorusParams as double[];
                }
            }
            catch { return null; }
            if (kind == null || values == null || values.Length == 0) return null;

            var text = new StringBuilder(kind);
            foreach (double value in values)
                text.Append('|').Append(Math.Round(value, 9)
                    .ToString("G9", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        /// <summary>
        /// One chart: how much length a unit of u and of v covers, where the
        /// chart starts, and where it sits.
        /// </summary>
        private sealed class Fit
        {
            public double Du = 1.0, Dv = 1.0;
            public double UMin, VMin;
            public double NudgeU, NudgeV;

            private double _sxx, _sxy, _syy, _sxz, _syz;
            private int _edges;
            private bool _seen;
            private List<Sample2> _alongU, _alongV;

            /// <summary>One triangle edge: the parameter step in each
            /// direction against the real length it spans.</summary>
            public void Edge(MeshDefinition mesh, int baseVertex, int a, int b)
            {
                int ua = (baseVertex + a) * 2, ub = (baseVertex + b) * 2;
                double du = mesh.Uvs[ub] - mesh.Uvs[ua];
                double dv = mesh.Uvs[ub + 1] - mesh.Uvs[ua + 1];
                int pa = (baseVertex + a) * 3, pb = (baseVertex + b) * 3;
                double dx = mesh.Positions[pb] - mesh.Positions[pa];
                double dy = mesh.Positions[pb + 1] - mesh.Positions[pa + 1];
                double dz = mesh.Positions[pb + 2] - mesh.Positions[pa + 2];

                double x = du * du, y = dv * dv, z = dx * dx + dy * dy + dz * dz;
                if (z <= 0.0 || (x <= 0.0 && y <= 0.0)) return;
                _sxx += x * x;
                _sxy += x * y;
                _syy += y * y;
                _sxz += x * z;
                _syz += y * z;
                _edges++;
            }

            public void Solve()
            {
                if (_edges == 0) return;
                double det = _sxx * _syy - _sxy * _sxy;
                double size = _sxx * _syy;
                if (size > 0.0 && Math.Abs(det) > 1e-12 * size)
                {
                    double a = (_sxz * _syy - _syz * _sxy) / det;
                    double b = (_sxx * _syz - _sxy * _sxz) / det;
                    if (Fine(a) && Fine(b))
                    {
                        Du = Math.Sqrt(a);
                        Dv = Math.Sqrt(b);
                        return;
                    }
                }
                // The two directions cannot be told apart: every edge of the
                // chart runs the same way in parameter space. Each is then
                // fitted on its own, which overstates both and still beats
                // mixing radians with metres.
                double alone = _sxx > 0.0 ? _sxz / _sxx : 0.0;
                if (Fine(alone)) Du = Math.Sqrt(alone);
                alone = _syy > 0.0 ? _syz / _syy : 0.0;
                if (Fine(alone)) Dv = Math.Sqrt(alone);
            }

            private static bool Fine(double value)
            {
                return value > 1e-18 && !double.IsNaN(value)
                    && !double.IsInfinity(value);
            }

            /// <summary>
            /// How far each edge of one triangle is from the size the fit
            /// gave it, with the AREA the triangle covers.
            ///
            /// One is a perfect answer. The area is what the samples are
            /// weighed by: a cone apex or a blend corner carries a great
            /// many tiny triangles and hardly any of the face, and counting
            /// them one each would size the whole face for its corner.
            /// </summary>
            public void Sample(MeshDefinition mesh, int baseVertex,
                               int a, int b, int c)
            {
                int pa = (baseVertex + a) * 3;
                int pb = (baseVertex + b) * 3;
                int pc = (baseVertex + c) * 3;
                double abx = mesh.Positions[pb] - mesh.Positions[pa];
                double aby = mesh.Positions[pb + 1] - mesh.Positions[pa + 1];
                double abz = mesh.Positions[pb + 2] - mesh.Positions[pa + 2];
                double acx = mesh.Positions[pc] - mesh.Positions[pa];
                double acy = mesh.Positions[pc + 1] - mesh.Positions[pa + 1];
                double acz = mesh.Positions[pc + 2] - mesh.Positions[pa + 2];
                double nx = aby * acz - abz * acy;
                double ny = abz * acx - abx * acz;
                double nz = abx * acy - aby * acx;
                double share = Math.Sqrt(nx * nx + ny * ny + nz * nz) / 6.0;
                if (!(share > 0.0)) return;
                Edge(mesh, baseVertex, a, b, share);
                Edge(mesh, baseVertex, b, c, share);
                Edge(mesh, baseVertex, c, a, share);
            }

            /// <summary>
            /// One edge, counted toward the direction it mostly runs in.
            ///
            /// Each direction is put right on its own. Scaling both by one
            /// number would move whichever of them the fit already had
            /// right, and on a face where only u is wrong that trades one
            /// error for two. An edge that runs across both says nothing
            /// about either and is left out.
            /// </summary>
            private void Edge(MeshDefinition mesh, int baseVertex,
                              int a, int b, double share)
            {
                int ua = (baseVertex + a) * 2, ub = (baseVertex + b) * 2;
                double du = Math.Abs(mesh.Uvs[ub] - mesh.Uvs[ua]) * Du;
                double dv = Math.Abs(mesh.Uvs[ub + 1] - mesh.Uvs[ua + 1]) * Dv;
                bool along = du > 3.0 * dv, across = dv > 3.0 * du;
                if (!along && !across) return;
                double said = along ? du : dv;
                if (said <= 0.0) return;
                int pa = (baseVertex + a) * 3, pb = (baseVertex + b) * 3;
                double dx = mesh.Positions[pb] - mesh.Positions[pa];
                double dy = mesh.Positions[pb + 1] - mesh.Positions[pa + 1];
                double dz = mesh.Positions[pb + 2] - mesh.Positions[pa + 2];
                double real = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (real <= 0.0) return;
                var into = along
                    ? (_alongU ?? (_alongU = new List<Sample2>()))
                    : (_alongV ?? (_alongV = new List<Sample2>()));
                into.Add(new Sample2 { Ratio = real / said, Share = share });
            }

            /// <summary>Puts each size where most of the FACE is: the ratio
            /// that half its area sits below.</summary>
            public void Centre()
            {
                double middle = Middle(_alongU);
                if (Fine(middle)) Du *= middle;
                middle = Middle(_alongV);
                if (Fine(middle)) Dv *= middle;
                _alongU = null;
                _alongV = null;
            }

            private static double Middle(List<Sample2> found)
            {
                if (found == null || found.Count == 0) return 0.0;
                found.Sort((x, y) => x.Ratio.CompareTo(y.Ratio));
                double whole = 0.0;
                foreach (var one in found) whole += one.Share;
                double half = whole * 0.5, walked = 0.0;
                foreach (var one in found)
                {
                    walked += one.Share;
                    if (walked >= half) return one.Ratio;
                }
                return found[found.Count - 1].Ratio;
            }

            private struct Sample2
            {
                public double Ratio;
                public double Share;
            }

            public void See(double u, double v)
            {
                if (!_seen)
                {
                    UMin = u;
                    VMin = v;
                    _umax = u;
                    _vmax = v;
                    _seen = true;
                    return;
                }
                if (u < UMin) UMin = u;
                if (v < VMin) VMin = v;
                if (u > _umax) _umax = u;
                if (v > _vmax) _vmax = v;
            }

            private double _umax, _vmax;

            /// <summary>Gives the chart an offset of its own, from its own
            /// box and its number, so it is the same on every send and two
            /// charts practically never land on one value.</summary>
            public void Place(int index)
            {
                double x = UMin + _umax, y = VMin + _vmax, z = index + UMin * 0.5;
                double hu = x * 0.7548776662 + y * 0.5698402909 + z * 0.4301597090;
                double hv = x * 0.3247179572 + y * 0.8191725134 + z * 0.6180339887;
                hu -= Math.Floor(hu);
                hv -= Math.Floor(hv);
                NudgeU = hu * ChartNudge;
                NudgeV = hv * ChartNudge;
            }
        }
    }
}
