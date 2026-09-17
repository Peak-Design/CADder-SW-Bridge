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
    /// Three surfaces can be FLATTENED with no error at all, because they
    /// are developable: a plane is already flat, a cylinder unrolls into a
    /// rectangle, and a cone unrolls into a fan. Each of those is done
    /// exactly, from the surface's own numbers.
    ///
    /// Nothing else can be. A sphere, a torus and a spline have no flat
    /// counterpart, so there the best one frame can do is one scale in u
    /// and one in v, found from the triangles: an edge knows both its
    /// parameter step and its real length, so a*du^2 + b*dv^2 = L^2 is one
    /// equation in two unknowns and a face gives hundreds of them. Each
    /// direction is then put back over the middle of the face by AREA,
    /// because least squares works on squared lengths and would otherwise
    /// answer a worm thread for its coarsest corner.
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

        /// <summary>How far the triangles may disagree with the surface
        /// before its own numbers are not believed. A quarter is enormous:
        /// the check is there to catch a parameterisation that is not what
        /// this assumes at all, not to grade the fit.</summary>
        private const double Slack = 0.25;

        internal const string Plane = "plane";
        internal const string Cylinder = "cylinder";
        internal const string Cone = "cone";
        internal const string Sphere = "sphere";
        internal const string Torus = "torus";

        /// <summary>
        /// Writes the UV map of the vertices this body just added. Returns
        /// how many vertices the body now has, which is MORE than it had
        /// when a closed face had to be cut open along its seam.
        ///
        /// faceOf says which face each vertex belongs to, as an index into
        /// faces, or -1 where nothing claimed it. A tessellated vertex
        /// belongs to exactly one face: facet topology matching shares
        /// vertices inside a face and never across one, which PlaneUvCheck
        /// measured over the whole corpus. It grows with the body.
        /// </summary>
        public static int Apply(
            MeshDefinition mesh, int baseVertex, int vertexCount,
            int firstTriangle, IList<IFace2> faces, ref int[] faceOf,
            Action<string> log)
        {
            if (mesh == null || faces == null || faceOf == null) return vertexCount;
            if (vertexCount <= 0 || faces.Count == 0) return vertexCount;
            if (mesh.Uvs.Count < (baseVertex + vertexCount) * 2) return vertexCount;
            if (faceOf.Length < vertexCount) return vertexCount;

            var chartOf = new int[faces.Count];
            var charts = Charts(faces, chartOf);
            return Rewrite(mesh, baseVertex, vertexCount, firstTriangle,
                           chartOf, charts, ref faceOf, log);
        }

        /// <summary>
        /// Puts a face's angle back on ONE branch, and cuts it open where
        /// it goes the whole way round.
        ///
        /// u is an angle on a cylinder, a cone, a sphere and a torus, and
        /// SolidWorks reports it modulo a turn. Two points a millimetre
        /// apart on the part can therefore come back a whole turn apart in
        /// u, and written into a UV map that edge runs backwards over the
        /// entire texture. It happens down one line of every closed hole
        /// and shaft. It happens to MOST of a worm wheel's tooth faces
        /// (Oscar, 2026-09-17).
        ///
        /// So the face is walked triangle by triangle, and each point is
        /// given the branch of the angle that puts it beside the point it
        /// was reached from. A face that does not close comes out in one
        /// piece. A face that does close comes back to its start a turn
        /// out, and the ring of triangles that closes it gets its own
        /// copies of those points, a turn further round: the seam, cut
        /// exactly once, which is what a cylinder needs to lie flat.
        ///
        /// Nothing moves. A copy sits exactly where the point it came from
        /// does, which is what the closure count needs, and carries the
        /// same normal. Returns how many vertices the body has after it.
        /// </summary>
        internal static int Unwrap(
            MeshDefinition mesh, int baseVertex, int vertexCount,
            int firstTriangle, int[] chartOf, IList<Chart> charts,
            ref int[] faceOf, Action<string> log)
        {
            var mine = Sorted(mesh, baseVertex, vertexCount, firstTriangle,
                              chartOf, charts, faceOf);
            if (mine == null) return vertexCount;

            var turnOf = new Dictionary<int, int>();       // vertex -> branch
            var copyOf = new Dictionary<long, int>();      // (vertex, branch)
            int cut = 0, turned = 0;

            foreach (var face in mine)
            {
                turnOf.Clear();
                if (!Continuous(mesh, baseVertex, face, turnOf, copyOf,
                                ref cut)) continue;
                foreach (var pair in turnOf)
                {
                    if (pair.Value == 0) continue;
                    mesh.Uvs[(baseVertex + pair.Key) * 2] += Turn * pair.Value;
                    turned++;
                }
            }
            if (copyOf.Count == 0 && turned == 0) return vertexCount;

            int after = mesh.VertexCount - baseVertex;
            if (after > vertexCount)
            {
                var grown = new int[after];
                for (int v = 0; v < after; v++)
                    grown[v] = v < vertexCount ? faceOf[v] : -1;
                foreach (var pair in copyOf)
                    grown[pair.Value] = faceOf[(int)(pair.Key >> 8)];
                faceOf = grown;
            }

            if (log != null && (cut > 0 || turned > 0))
                log("surface uv: " + turned + " point(s) put back on one "
                    + "branch of the angle, " + cut + " triangle(s) cut along "
                    + "a seam with " + copyOf.Count + " copied point(s)");
            return after;
        }

        /// <summary>A whole turn of the angle.</summary>
        private const double Turn = 2.0 * Math.PI;

        /// <summary>
        /// The triangles of each face whose u is an angle, or null when no
        /// face of this body has one.
        ///
        /// A face is walked whatever its u range, because a face that
        /// goes all the way round needs the same cut whether its angle was
        /// reported in one run or in pieces. What IS checked is that the
        /// steps look like steps of an angle: a face whose middling step is
        /// wider than a radian is not tessellated in radians at all, and
        /// nothing here would be an improvement.
        /// </summary>
        private static List<List<int>> Sorted(
            MeshDefinition mesh, int baseVertex, int vertexCount,
            int firstTriangle, int[] chartOf, IList<Chart> charts, int[] faceOf)
        {
            var order = new Dictionary<int, int>();
            var found = new List<List<int>>();
            for (int v = 0; v < vertexCount; v++)
            {
                int face = faceOf[v];
                if (face < 0 || face >= chartOf.Length) continue;
                if (!charts[chartOf[face]].Angular || order.ContainsKey(face))
                    continue;
                order[face] = found.Count;
                found.Add(new List<int>());
            }
            if (found.Count == 0) return null;

            for (int t = firstTriangle; t + 2 < mesh.Triangles.Count; t += 3)
            {
                int a = mesh.Triangles[t] - baseVertex;
                if (a < 0 || a >= vertexCount) continue;
                int face = faceOf[a];
                int at;
                if (face < 0 || !order.TryGetValue(face, out at)) continue;
                found[at].Add(t);
            }

            var steps = new List<double>();
            for (int at = 0; at < found.Count; at++)
            {
                steps.Clear();
                foreach (int t in found[at])
                    for (int k = 0; k < 3; k++)
                    {
                        int a = mesh.Triangles[t + k] - baseVertex;
                        int b = mesh.Triangles[t + (k + 1) % 3] - baseVertex;
                        if (a < 0 || b < 0 || a >= vertexCount
                            || b >= vertexCount) continue;
                        steps.Add(Math.Abs(mesh.Uvs[(baseVertex + b) * 2]
                                           - mesh.Uvs[(baseVertex + a) * 2]));
                    }
                if (steps.Count == 0) continue;
                steps.Sort();
                if (steps[steps.Count / 2] > 1.0) found[at].Clear();
            }
            return found;
        }

        /// <summary>
        /// Walks one face and gives each of its points the branch of the
        /// angle that puts it beside its neighbours. Returns false when the
        /// face has nothing this can start from.
        /// </summary>
        private static bool Continuous(
            MeshDefinition mesh, int baseVertex, List<int> triangles,
            Dictionary<int, int> turnOf, Dictionary<long, int> copyOf,
            ref int cut)
        {
            if (triangles.Count == 0) return false;
            var waiting = new List<int>(triangles);
            var held = new List<int>();
            bool any = false;

            // The walk reaches a triangle when one of its corners is
            // already placed. A pass that places nothing means what is left
            // does not touch what is done, so the next piece starts afresh.
            while (waiting.Count > 0)
            {
                bool moved = false;
                held.Clear();
                foreach (int t in waiting)
                {
                    int seed = -1;
                    for (int k = 0; k < 3 && seed < 0; k++)
                        if (turnOf.ContainsKey(mesh.Triangles[t + k] - baseVertex))
                            seed = mesh.Triangles[t + k] - baseVertex;
                    if (seed < 0 && any) { held.Add(t); continue; }
                    if (seed < 0)
                    {
                        seed = mesh.Triangles[t] - baseVertex;
                        turnOf[seed] = 0;
                        any = true;
                    }
                    Place(mesh, baseVertex, t, seed, turnOf, copyOf, ref cut);
                    moved = true;
                }
                if (held.Count == 0) break;
                if (!moved) any = false;        // start a new piece
                var swap = waiting;
                waiting = held;
                held = swap;
            }
            return true;
        }

        /// <summary>Puts the other two corners of one triangle on the same
        /// branch as the corner it was reached from.</summary>
        private static void Place(
            MeshDefinition mesh, int baseVertex, int t, int seed,
            Dictionary<int, int> turnOf, Dictionary<long, int> copyOf,
            ref int cut)
        {
            double anchor = mesh.Uvs[(baseVertex + seed) * 2] + Turn * turnOf[seed];
            bool cutHere = false;
            for (int k = 0; k < 3; k++)
            {
                int v = mesh.Triangles[t + k] - baseVertex;
                if (v == seed) continue;
                double u = mesh.Uvs[(baseVertex + v) * 2];
                int want = (int)Math.Round((anchor - u) / Turn);
                int has;
                if (!turnOf.TryGetValue(v, out has))
                {
                    turnOf[v] = want;
                    continue;
                }
                if (has == want) continue;

                // The face has come back to its start a turn out: this is
                // the seam, and the triangles that cross it take copies.
                cutHere = true;
                long key = ((long)v << 8) | (uint)(want - has + 128);
                int copy;
                if (!copyOf.TryGetValue(key, out copy))
                {
                    copy = mesh.VertexCount - baseVertex;
                    int at = (baseVertex + v) * 3;
                    mesh.Positions.Add(mesh.Positions[at]);
                    mesh.Positions.Add(mesh.Positions[at + 1]);
                    mesh.Positions.Add(mesh.Positions[at + 2]);
                    if (mesh.Normals.Count >= at + 3)
                    {
                        mesh.Normals.Add(mesh.Normals[at]);
                        mesh.Normals.Add(mesh.Normals[at + 1]);
                        mesh.Normals.Add(mesh.Normals[at + 2]);
                    }
                    mesh.Uvs.Add(u + Turn * want);
                    mesh.Uvs.Add(mesh.Uvs[(baseVertex + v) * 2 + 1]);
                    copyOf[key] = copy;
                }
                mesh.Triangles[t + k] = baseVertex + copy;
            }
            if (cutHere) cut++;
        }

        /// <summary>
        /// The arithmetic on its own: the faces have already been sorted
        /// into charts, and nothing below here asks SolidWorks anything.
        /// chartOf gives the chart of each face, faceOf the face of each
        /// vertex.
        /// </summary>
        internal static int Rewrite(
            MeshDefinition mesh, int baseVertex, int vertexCount,
            int firstTriangle, int[] chartOf, IList<Chart> charts,
            ref int[] faceOf, Action<string> log)
        {
            if (mesh == null || chartOf == null || faceOf == null) return vertexCount;
            if (charts == null || charts.Count == 0) return vertexCount;
            if (vertexCount <= 0) return vertexCount;
            if (mesh.Uvs.Count < (baseVertex + vertexCount) * 2) return vertexCount;
            if (mesh.Positions.Count < (baseVertex + vertexCount) * 3) return vertexCount;
            if (faceOf.Length < vertexCount) return vertexCount;

            // 1. The angle, put back on one branch. SolidWorks reports it
            //    modulo a turn, and a step of a whole turn between two
            //    neighbours would spoil both the fit and the map.
            vertexCount = Unwrap(mesh, baseVertex, vertexCount, firstTriangle,
                                 chartOf, charts, ref faceOf, log);

            // 2. The fit, from the triangles. Every chart gets one, because
            //    it is also what says whether the surface's own numbers can
            //    be believed.
            EachTriangle(mesh, baseVertex, vertexCount, firstTriangle,
                         chartOf, charts, faceOf,
                         (chart, a, b, c) => chart.Measure(mesh, baseVertex, a, b, c));
            foreach (var chart in charts) chart.Solve();
            EachTriangle(mesh, baseVertex, vertexCount, firstTriangle,
                         chartOf, charts, faceOf,
                         (chart, a, b, c) => chart.Sample(mesh, baseVertex, a, b, c));
            foreach (var chart in charts) chart.Centre();

            // 3. A cone needs the apex it fans out from, which is where its
            //    radius reaches zero along its own axis.
            EachVertex(mesh, baseVertex, vertexCount, chartOf, charts, faceOf,
                       (chart, v) => chart.Profile(mesh, baseVertex, v));
            foreach (var chart in charts) chart.Choose();

            // 4. The mapping, written in place, and where each chart landed.
            EachVertex(mesh, baseVertex, vertexCount, chartOf, charts, faceOf,
                       (chart, v) => chart.Map(mesh, baseVertex, v));
            for (int i = 0; i < charts.Count; i++) charts[i].Place(i);

            int orphans = 0;
            for (int v = 0; v < vertexCount; v++)
            {
                int face = faceOf[v];
                if (face < 0 || face >= chartOf.Length) { orphans++; continue; }
                var chart = charts[chartOf[face]];
                int at = (baseVertex + v) * 2;
                mesh.Uvs[at] = mesh.Uvs[at] - chart.UMin + chart.NudgeU;
                mesh.Uvs[at + 1] = mesh.Uvs[at + 1] - chart.VMin + chart.NudgeV;
            }

            if (log != null)
            {
                int flat = 0;
                double whole = 0.0, flatArea = 0.0;
                foreach (var chart in charts)
                {
                    whole += chart.Covers;
                    if (!chart.Unrolled) continue;
                    flat++;
                    flatArea += chart.Covers;
                }
                log("surface uv: " + charts.Count + " chart(s), " + flat
                    + " unrolled exactly"
                    + (whole > 0.0 ? " (" + (flatArea / whole * 100.0)
                        .ToString("F1", CultureInfo.InvariantCulture)
                        + "% of the surface)" : "")
                    + ", " + (charts.Count - flat) + " fitted"
                    + (orphans > 0 ? ", " + orphans + " vertex(es) belonged to "
                       + "no face and keep the parameters SolidWorks gave them"
                       : ""));
            }
            return vertexCount;
        }

        private static void EachTriangle(
            MeshDefinition mesh, int baseVertex, int vertexCount,
            int firstTriangle, int[] chartOf, IList<Chart> charts,
            int[] faceOf, Action<Chart, int, int, int> run)
        {
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
                run(charts[chartOf[face]], a, b, c);
            }
        }

        private static void EachVertex(
            MeshDefinition mesh, int baseVertex, int vertexCount,
            int[] chartOf, IList<Chart> charts, int[] faceOf,
            Action<Chart, int> run)
        {
            for (int v = 0; v < vertexCount; v++)
            {
                int face = faceOf[v];
                if (face < 0 || face >= chartOf.Length) continue;
                run(charts[chartOf[face]], v);
            }
        }

        /// <summary>Sorts the faces into charts: one per surface, so patches
        /// of one cylinder share a frame. A surface this cannot name, such
        /// as a spline, gets a chart to itself, which is a chart per face
        /// and the safe answer.</summary>
        private static List<Chart> Charts(IList<IFace2> faces, int[] chartOf)
        {
            var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
            var charts = new List<Chart>();
            for (int i = 0; i < faces.Count; i++)
            {
                string kind;
                double[] values;
                string key = KeyOf(faces[i], out kind, out values);
                int found;
                if (key == null || !byKey.TryGetValue(key, out found))
                {
                    found = charts.Count;
                    charts.Add(new Chart(kind, values));
                    if (key != null) byKey[key] = found;
                }
                chartOf[i] = found;
            }
            return charts;
        }

        /// <summary>What surface this face lies on: its kind, its numbers,
        /// and a key that is the same for two faces on one surface. The key
        /// is null for a surface this cannot compare.</summary>
        private static string KeyOf(IFace2 face, out string kind, out double[] values)
        {
            kind = null;
            values = null;
            ISurface surface = null;
            try { surface = face == null ? null : face.GetSurface() as ISurface; }
            catch { }
            if (surface == null) return null;

            try
            {
                if (surface.IsPlane())
                {
                    kind = Plane;
                    values = surface.PlaneParams as double[];
                }
                else if (surface.IsCylinder())
                {
                    kind = Cylinder;
                    values = surface.CylinderParams as double[];
                }
                else if (surface.IsCone())
                {
                    kind = Cone;
                    values = surface.ConeParams as double[];
                }
                else if (surface.IsSphere())
                {
                    kind = Sphere;
                    values = surface.SphereParams as double[];
                }
                else if (surface.IsTorus())
                {
                    kind = Torus;
                    values = surface.TorusParams as double[];
                }
            }
            catch
            {
                kind = null;
                values = null;
                return null;
            }
            if (kind == null || values == null || values.Length == 0)
            {
                kind = null;
                values = null;
                return null;
            }

            var text = new StringBuilder(kind);
            foreach (double value in values)
                text.Append('|').Append(Math.Round(value, 9)
                    .ToString("G9", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        /// <summary>
        /// One frame: the surface it is on, how its parameters turn into
        /// lengths, and where it sits in the map.
        /// </summary>
        internal sealed class Chart
        {
            public readonly string Kind;
            public readonly double[] Params;

            /// <summary>Whether the surface was flattened exactly rather
            /// than scaled by a fit.</summary>
            public bool Unrolled;

            /// <summary>Whether u goes round: it is an angle, and the face
            /// can therefore be closed and have a seam.</summary>
            public bool Angular
            {
                get
                {
                    return Kind == Cylinder || Kind == Cone
                        || Kind == Sphere || Kind == Torus;
                }
            }

            /// <summary>How much of the part this chart covers, in square
            /// metres. What the log reports, because a chart count says
            /// nothing about how much of a part a texture sits right on.
            /// </summary>
            public double Covers;

            public double Du = 1.0, Dv = 1.0;
            public double UMin, VMin;
            public double NudgeU, NudgeV;

            public Chart(string kind, double[] values)
            {
                Kind = kind;
                Params = values;
            }

            // The fit.
            private double _sxx, _sxy, _syy, _sxz, _syz;
            private int _edges;
            private List<Off> _alongU, _alongV;

            // The axis profile, for a cone.
            private double _n, _st, _stt, _sr, _srt;
            private double _rLow = double.MaxValue, _rHigh = double.MinValue;

            // The fan a cone unrolls into.
            private double[] _apex;
            private double _sine;

            // Where the chart landed.
            private bool _seen;
            private double _uMax, _vMax;

            // ── The fit ──────────────────────────────────────────────────

            /// <summary>One triangle's three edges: the parameter step in
            /// each direction against the real length it spans.</summary>
            public void Measure(MeshDefinition mesh, int baseVertex,
                                int a, int b, int c)
            {
                Pair(mesh, baseVertex, a, b);
                Pair(mesh, baseVertex, b, c);
                Pair(mesh, baseVertex, c, a);
            }

            private void Pair(MeshDefinition mesh, int baseVertex, int a, int b)
            {
                int ua = (baseVertex + a) * 2, ub = (baseVertex + b) * 2;
                double du = mesh.Uvs[ub] - mesh.Uvs[ua];
                double dv = mesh.Uvs[ub + 1] - mesh.Uvs[ua + 1];
                double x = du * du, y = dv * dv;
                double z = Square(mesh, baseVertex, a, b);
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
                double share = Area(mesh, baseVertex, a, b, c) / 3.0;
                if (!(share > 0.0)) return;
                Covers += share * 3.0;
                Weigh(mesh, baseVertex, a, b, share);
                Weigh(mesh, baseVertex, b, c, share);
                Weigh(mesh, baseVertex, c, a, share);
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
            private void Weigh(MeshDefinition mesh, int baseVertex,
                               int a, int b, double share)
            {
                int ua = (baseVertex + a) * 2, ub = (baseVertex + b) * 2;
                double du = Math.Abs(mesh.Uvs[ub] - mesh.Uvs[ua]) * Du;
                double dv = Math.Abs(mesh.Uvs[ub + 1] - mesh.Uvs[ua + 1]) * Dv;
                bool along = du > 3.0 * dv, across = dv > 3.0 * du;
                if (!along && !across) return;
                double said = along ? du : dv;
                if (said <= 0.0) return;
                double real = Math.Sqrt(Square(mesh, baseVertex, a, b));
                if (real <= 0.0) return;
                var into = along
                    ? (_alongU ?? (_alongU = new List<Off>()))
                    : (_alongV ?? (_alongV = new List<Off>()));
                into.Add(new Off { Ratio = real / said, Share = share });
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

            private static double Middle(List<Off> found)
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

            // ── Unrolling ────────────────────────────────────────────────

            /// <summary>Where this vertex sits along the axis and how far it
            /// is from it. A cone's radius runs straight in the first, which
            /// is what gives away the apex.</summary>
            public void Profile(MeshDefinition mesh, int baseVertex, int v)
            {
                if (Kind != Cone || Params == null || Params.Length < 7) return;
                int at = (baseVertex + v) * 3;
                double x = mesh.Positions[at] - Params[0];
                double y = mesh.Positions[at + 1] - Params[1];
                double z = mesh.Positions[at + 2] - Params[2];
                double t = x * Params[3] + y * Params[4] + z * Params[5];
                double px = x - t * Params[3];
                double py = y - t * Params[4];
                double pz = z - t * Params[5];
                double r = Math.Sqrt(px * px + py * py + pz * pz);
                _n += 1.0;
                _st += t;
                _stt += t * t;
                _sr += r;
                _srt += r * t;
                if (r < _rLow) _rLow = r;
                if (r > _rHigh) _rHigh = r;
            }

            /// <summary>
            /// Decides how this chart is written: unrolled from the
            /// surface's own numbers, or scaled by the fit.
            ///
            /// The surface is believed only where the triangles agree with
            /// it. A parameterisation this does not expect (u not an angle,
            /// v not a length) shows up at once as a fit far from what the
            /// surface says it should be, and the fit is then kept, because
            /// the fit was measured and this was assumed.
            /// </summary>
            public void Choose()
            {
                if (Kind == Plane)
                {
                    // A plane's parameters are already metres, measured over
                    // the corpus to a micron (PlaneUvCheck).
                    if (Near(Du, 1.0) && Near(Dv, 1.0))
                    {
                        Du = 1.0;
                        Dv = 1.0;
                        Unrolled = true;
                    }
                    return;
                }
                if (Kind == Cylinder && Params != null && Params.Length >= 7)
                {
                    // It unrolls into a rectangle: the angle becomes arc
                    // length at the true radius, and v is already a length.
                    double radius = Params[6];
                    if (radius > 0.0 && Near(Du, radius) && Near(Dv, 1.0))
                    {
                        Du = radius;
                        Dv = 1.0;
                        Unrolled = true;
                    }
                    return;
                }
                if (Kind != Cone || Params == null || Params.Length < 7) return;
                if (!(_n > 3.0)) return;

                // A cone unrolls into a fan. Its radius runs straight along
                // its axis, so a line through (distance along the axis,
                // distance from it) hands back the apex, where the radius
                // reaches zero, and the half angle with it.
                double det = _n * _stt - _st * _st;
                if (!(Math.Abs(det) > 1e-18)) return;
                double slope = (_n * _srt - _st * _sr) / det;
                double start = (_stt * _sr - _st * _srt) / det;
                if (!(Math.Abs(slope) > 1e-9)) return;      // a cylinder
                double tangent = Math.Abs(slope);
                _sine = tangent / Math.Sqrt(1.0 + tangent * tangent);
                if (!(_sine > 0.01) || !(_sine < 0.9999)) return;

                // u has to be the angle for the fan to be right, and what
                // one unit of it covers is the radius, halfway up the face.
                double middle = (_rLow + _rHigh) * 0.5;
                if (!(middle > 0.0) || !Near(Du, middle)) return;

                double apexAt = -start / slope;
                _apex = new[] { Params[0] + apexAt * Params[3],
                                Params[1] + apexAt * Params[4],
                                Params[2] + apexAt * Params[5] };
                Unrolled = true;
            }

            /// <summary>Writes this vertex where the mapping puts it, and
            /// notes how far the chart reaches.</summary>
            public void Map(MeshDefinition mesh, int baseVertex, int v)
            {
                int at = (baseVertex + v) * 2;
                double u, w;
                if (_apex != null)
                {
                    // The fan: the slant distance from the apex, turned
                    // through the angle the cone opens by. Distances across
                    // it and around it both come out exact.
                    int p = (baseVertex + v) * 3;
                    double dx = mesh.Positions[p] - _apex[0];
                    double dy = mesh.Positions[p + 1] - _apex[1];
                    double dz = mesh.Positions[p + 2] - _apex[2];
                    double slant = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    double turn = mesh.Uvs[at] * _sine;
                    u = slant * Math.Cos(turn);
                    w = slant * Math.Sin(turn);
                }
                else
                {
                    u = mesh.Uvs[at] * Du;
                    w = mesh.Uvs[at + 1] * Dv;
                }
                mesh.Uvs[at] = u;
                mesh.Uvs[at + 1] = w;
                if (!_seen)
                {
                    UMin = _uMax = u;
                    VMin = _vMax = w;
                    _seen = true;
                    return;
                }
                if (u < UMin) UMin = u;
                if (u > _uMax) _uMax = u;
                if (w < VMin) VMin = w;
                if (w > _vMax) _vMax = w;
            }

            /// <summary>Gives the chart an offset of its own, from its own
            /// box and its number, so it is the same on every send and two
            /// charts practically never land on one value.</summary>
            public void Place(int index)
            {
                double x = UMin + _uMax, y = VMin + _vMax, z = index + UMin * 0.5;
                double hu = x * 0.7548776662 + y * 0.5698402909 + z * 0.4301597090;
                double hv = x * 0.3247179572 + y * 0.8191725134 + z * 0.6180339887;
                hu -= Math.Floor(hu);
                hv -= Math.Floor(hv);
                NudgeU = hu * ChartNudge;
                NudgeV = hv * ChartNudge;
            }

            // ── Small sums ───────────────────────────────────────────────

            private static double Square(MeshDefinition mesh, int baseVertex,
                                         int a, int b)
            {
                int pa = (baseVertex + a) * 3, pb = (baseVertex + b) * 3;
                double dx = mesh.Positions[pb] - mesh.Positions[pa];
                double dy = mesh.Positions[pb + 1] - mesh.Positions[pa + 1];
                double dz = mesh.Positions[pb + 2] - mesh.Positions[pa + 2];
                return dx * dx + dy * dy + dz * dz;
            }

            private static double Area(MeshDefinition mesh, int baseVertex,
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
                return Math.Sqrt(nx * nx + ny * ny + nz * nz) * 0.5;
            }

            private static bool Near(double got, double want)
            {
                return want > 0.0 && Math.Abs(got - want) <= Slack * want;
            }

            private static bool Fine(double value)
            {
                return value > 1e-18 && !double.IsNaN(value)
                    && !double.IsInfinity(value);
            }

            /// <summary>One edge, and how much of the face it
            /// speaks for.</summary>
            private struct Off
            {
                public double Ratio;
                public double Share;
            }
        }
    }
}
