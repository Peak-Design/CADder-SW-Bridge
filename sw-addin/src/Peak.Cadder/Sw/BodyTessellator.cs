using System;
using System.Collections.Generic;
using System.Globalization;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.Cadder.Sw
{
    /// <summary>
    /// Reads a body's geometry as a mesh, at a tolerance THIS code chooses.
    ///
    /// That last part is the point. The obvious route. IFace2.GetTessTriangles,
    /// which is what KeyShot's SolidWorks plugin used: returns the DISPLAY
    /// tessellation, so its quality is whatever the document's image-quality
    /// slider happens to be set to, and the only way to change it is to reach
    /// into the user's document settings. IBody2.GetTessellation takes an
    /// explicit chord tolerance instead, which is what makes "send this one
    /// part again, finer" possible without touching anything the user owns.
    ///
    /// GetTessTriangles stays as the fallback: it is a different code path
    /// inside SolidWorks and it answers for bodies the tessellator refuses.
    /// </summary>
    public static class BodyTessellator
    {
        /// <summary>
        /// Chord tolerance for a body, from a 0..1 quality dial. Relative to
        /// the body's own size because an absolute tolerance either wastes
        /// triangles on a washer or ruins a chassis. SolidWorks' own image
        /// quality is relative for the same reason.
        /// </summary>
        public static double ToleranceFor(double quality, double diagonal)
        {
            double q = Math.Max(0.0, Math.Min(1.0, quality));
            if (diagonal <= 0.0 || double.IsNaN(diagonal)) diagonal = 0.1;
            // 1/100th of the diagonal at the coarse end down to 1/50000th at
            // the fine end, geometrically.
            double fraction = 0.01 * Math.Pow(0.002, q);
            return Math.Max(diagonal * fraction, 1e-6);
        }

        /// <summary>
        /// Appends one body to a definition. False means nothing could be
        /// read, which the caller logs and carries on from: one unreadable
        /// body must not cost the assembly.
        /// </summary>
        public static bool Append(
            IBody2 body, MeshDefinition mesh, double tolerance,
            Func<IFace2, IBody2, int> materialOf, Action<string> log)
        {
            if (body == null || mesh == null) return false;
            if (AppendTessellation(body, mesh, tolerance, materialOf, log)) return true;
            if (log != null)
                log("tessellation refused this body at "
                    + tolerance.ToString("G4", CultureInfo.InvariantCulture)
                    + " m; falling back to the display mesh");
            return AppendDisplayMesh(body, mesh, materialOf, log);
        }

        /// <summary>
        /// Appends only the facets of the faces `keep` accepts, at an explicit
        /// chord tolerance, with no display-mesh fallback. For contact
        /// geometry (a cam path), where the triangles are a position, not a
        /// picture, and the display tessellation is too coarse to hold a
        /// follower to a tenth of a millimetre. Returns the facets added.
        /// </summary>
        public static int AppendFaces(
            IBody2 body, MeshDefinition mesh, double tolerance,
            Func<IFace2, bool> keep, Action<string> log)
        {
            if (body == null || mesh == null || keep == null) return 0;
            int before = mesh.Triangles.Count / 3;
            AppendTessellation(body, mesh, tolerance,
                (face, b) => face != null && keep(face) ? 0 : -1, log, skipRejected: true);
            return mesh.Triangles.Count / 3 - before;
        }

        private static bool AppendTessellation(
            IBody2 body, MeshDefinition mesh, double tolerance,
            Func<IFace2, IBody2, int> materialOf, Action<string> log,
            bool skipRejected = false)
        {
            ITessellation tess;
            try
            {
                tess = body.GetTessellation(null) as ITessellation;
                if (tess == null) return false;
                tess.NeedFaceFacetMap = true;
                tess.NeedVertexNormal = true;
                tess.NeedVertexParams = true;
                tess.ImprovedQuality = true;
                // Match by TOPOLOGY: facets either side of an edge then share
                // vertices, which is what makes the result a mesh rather than
                // a pile of disconnected faces.
                tess.MatchType = (int)swTesselationMatchType_e.swTesselationMatchFacetTopology;
                tess.SurfacePlaneTolerance = tolerance;
                tess.SurfacePlaneAngleTolerance = 0.35;   // ~20 degrees
                tess.CurveChordTolerance = tolerance;
                tess.CurveChordAngleTolerance = 0.35;
                if (!tess.Tessellate()) return false;
            }
            catch (Exception ex)
            {
                if (log != null) log("tessellation failed: " + ex.Message);
                return false;
            }

            int baseVertex = mesh.VertexCount;
            int vertexCount, facetCount;
            try
            {
                vertexCount = tess.GetVertexCount();
                facetCount = tess.GetFacetCount();
            }
            catch { return false; }
            if (vertexCount <= 0 || facetCount <= 0) return false;

            // ITessellation has no bulk reader: every vertex and every facet
            // is its own cross-apartment COM call, so the count of those
            // calls IS the cost of this method, and the lists they fill are
            // sized up front rather than doubled a hundred times on the way.
            mesh.Positions.Capacity = Math.Max(
                mesh.Positions.Capacity, mesh.Positions.Count + vertexCount * 3);
            mesh.Normals.Capacity = mesh.Positions.Capacity;
            mesh.Uvs.Capacity = Math.Max(
                mesh.Uvs.Capacity, mesh.Uvs.Count + vertexCount * 2);
            mesh.Triangles.Capacity = Math.Max(
                mesh.Triangles.Capacity, mesh.Triangles.Count + facetCount * 3);
            mesh.TriangleMaterials.Capacity = Math.Max(
                mesh.TriangleMaterials.Capacity,
                mesh.TriangleMaterials.Count + facetCount);

            for (int i = 0; i < vertexCount; i++)
            {
                double[] p;
                try { p = tess.GetVertexPoint(i) as double[]; }
                catch { return false; }
                if (p == null || p.Length < 3) return false;
                mesh.Positions.Add(p[0]);
                mesh.Positions.Add(p[1]);
                mesh.Positions.Add(p[2]);

                double[] n = null;
                try { n = tess.GetVertexNormal(i) as double[]; }
                catch { }
                if (n == null || n.Length < 3) n = UpNormal;
                mesh.Normals.Add(n[0]);
                mesh.Normals.Add(n[1]);
                mesh.Normals.Add(n[2]);

                double[] uv = null;
                try { uv = tess.GetVertexParams(i) as double[]; }
                catch { }
                bool haveUv = uv != null && uv.Length >= 2;
                mesh.Uvs.Add(haveUv ? uv[0] : 0.0);
                mesh.Uvs.Add(haveUv ? uv[1] : 0.0);
            }

            // Facets are walked FACE BY FACE. NeedFaceFacetMap lets one call
            // return a whole face's facets, which retires GetFacetFace: a
            // COM call and a COM object per triangle, and lets the
            // appearance resolve once per face instead of once per triangle.
            // MaterialPropertyValues is itself a COM property, so that second
            // saving is the larger one: a 2000-triangle face went from 2000
            // appearance lookups to one.
            var state = new FacetState { Tess = tess, Body = body, Mesh = mesh,
                                         BaseVertex = baseVertex, VertexCount = vertexCount };
            var covered = new bool[facetCount];

            object[] faces = null;
            try { faces = body.GetFaces() as object[]; } catch { }
            if (faces != null)
            {
                foreach (var o in faces)
                {
                    var face = o as IFace2;
                    if (face == null) continue;
                    int[] facets = null;
                    try { facets = tess.GetFaceFacets(face) as int[]; } catch { }
                    if (facets == null || facets.Length == 0) continue;
                    int material = materialOf == null ? 0 : materialOf(face, body);
                    foreach (int facet in facets)
                    {
                        if (facet < 0 || facet >= facetCount || covered[facet]) continue;
                        covered[facet] = true;
                        if (skipRejected && material < 0) continue;
                        AddFacet(state, facet, material);
                    }
                }
            }

            // Anything the face map did not account for still has to travel,
            // so it goes the slow way rather than going missing.
            int orphans = 0;
            for (int f = 0; f < facetCount; f++)
            {
                if (covered[f]) continue;
                orphans++;
                IFace2 face = null;
                try { face = tess.GetFacetFace(f) as IFace2; } catch { }
                int orphanMaterial = materialOf == null ? 0 : materialOf(face, body);
                if (skipRejected && orphanMaterial < 0) continue;
                AddFacet(state, f, orphanMaterial);
            }

            if (log != null && orphans > 0)
                log("tessellation: " + orphans + " facet(s) had no face in the "
                    + "face-facet map and were read one at a time");
            if (log != null && state.Skipped > 0)
                log("tessellation: " + state.Stitched + " facet(s) kept, "
                    + state.Skipped + " skipped");
            return state.Stitched > 0;
        }

        /// <summary>What AddFacet needs, gathered once instead of passed as
        /// seven arguments per triangle. The scratch buffers exist so a
        /// million-facet body does not allocate four small arrays per
        /// triangle purely to ask which way it faces.</summary>
        private sealed class FacetState
        {
            public ITessellation Tess;
            public IBody2 Body;
            public MeshDefinition Mesh;
            public int BaseVertex;
            public int VertexCount;
            public int Stitched;
            public int Skipped;

            public readonly int[] Pairs = new int[6];
            public readonly double[] P0 = new double[3];
            public readonly double[] P1 = new double[3];
            public readonly double[] P2 = new double[3];
            public readonly double[] NormalSum = new double[3];
        }

        private static void AddFacet(FacetState s, int facet, int material)
        {
            int[] fins = null;
            try { fins = s.Tess.GetFacetFins(facet) as int[]; } catch { }
            if (fins == null || fins.Length < 3) { s.Skipped++; return; }

            for (int k = 0; k < 3; k++)
            {
                int[] fv = null;
                try { fv = s.Tess.GetFinVertices(fins[k]) as int[]; } catch { }
                if (fv == null || fv.Length < 2) { s.Skipped++; return; }
                s.Pairs[k * 2] = fv[0];
                s.Pairs[k * 2 + 1] = fv[1];
            }

            int a, b, c;
            if (!FacetStitcher.TryStitch(s.Pairs, out a, out b, out c)
                || a < 0 || b < 0 || c < 0
                || a >= s.VertexCount || b >= s.VertexCount || c >= s.VertexCount)
            {
                s.Skipped++;
                return;
            }

            Fill(s.Mesh.Positions, s.BaseVertex + a, s.P0);
            Fill(s.Mesh.Positions, s.BaseVertex + b, s.P1);
            Fill(s.Mesh.Positions, s.BaseVertex + c, s.P2);
            for (int k = 0; k < 3; k++)
                s.NormalSum[k] = s.Mesh.Normals[(s.BaseVertex + a) * 3 + k]
                    + s.Mesh.Normals[(s.BaseVertex + b) * 3 + k]
                    + s.Mesh.Normals[(s.BaseVertex + c) * 3 + k];

            if (FacetStitcher.NeedsFlip(s.P0, s.P1, s.P2, s.NormalSum))
            {
                int swap = b; b = c; c = swap;
            }

            s.Mesh.Triangles.Add(s.BaseVertex + a);
            s.Mesh.Triangles.Add(s.BaseVertex + b);
            s.Mesh.Triangles.Add(s.BaseVertex + c);
            s.Mesh.TriangleMaterials.Add(material);
            s.Stitched++;
        }

        private static void Fill(List<double> source, int vertex, double[] into)
        {
            into[0] = source[vertex * 3];
            into[1] = source[vertex * 3 + 1];
            into[2] = source[vertex * 3 + 2];
        }

        private static readonly double[] UpNormal = { 0, 0, 1 };

        /// <summary>
        /// The display tessellation, per face. Its quality is the document's
        /// image-quality setting and cannot be asked for, which is why this is
        /// the fallback and not the main road. Vertices are NOT shared between
        /// faces here. SolidWorks hands each face its own copies.
        /// </summary>
        private static bool AppendDisplayMesh(
            IBody2 body, MeshDefinition mesh, Func<IFace2, IBody2, int> materialOf,
            Action<string> log)
        {
            object[] faces = null;
            try { faces = body.GetFaces() as object[]; } catch { }
            if (faces == null) return false;

            int added = 0;
            foreach (var o in faces)
            {
                var face = o as IFace2;
                if (face == null) continue;
                int count;
                float[] tris, norms = null;
                try
                {
                    count = face.GetTessTriangleCount();
                    if (count <= 0) continue;
                    tris = face.GetTessTriangles(true) as float[];
                    norms = face.GetTessNorms() as float[];
                }
                catch { continue; }
                if (tris == null || tris.Length < count * 9) continue;
                bool haveNormals = norms != null && norms.Length >= count * 9;

                int material = materialOf == null ? 0 : materialOf(face, body);
                for (int t = 0; t < count; t++)
                {
                    int at = mesh.VertexCount;
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int p = t * 9 + corner * 3;
                        mesh.Positions.Add(tris[p]);
                        mesh.Positions.Add(tris[p + 1]);
                        mesh.Positions.Add(tris[p + 2]);
                        mesh.Normals.Add(haveNormals ? norms[p] : 0.0);
                        mesh.Normals.Add(haveNormals ? norms[p + 1] : 0.0);
                        mesh.Normals.Add(haveNormals ? norms[p + 2] : 1.0);
                        mesh.Uvs.Add(0);
                        mesh.Uvs.Add(0);
                    }
                    mesh.Triangles.Add(at);
                    mesh.Triangles.Add(at + 1);
                    mesh.Triangles.Add(at + 2);
                    mesh.TriangleMaterials.Add(material);
                    added++;
                }
            }
            if (log != null) log("display mesh: " + added + " triangle(s)");
            return added > 0;
        }

    }
}
