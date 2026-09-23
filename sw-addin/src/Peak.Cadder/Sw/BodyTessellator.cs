using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
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
        /// <summary>How fine a body is cut: the largest distance between
        /// the mesh and the true surface, and the largest angle one facet
        /// may turn through.</summary>
        public struct Fineness
        {
            public double Chord;    // metres
            public double Angle;    // radians

            /// <summary>Cut each body to RelativeDistance of its own
            /// diagonal instead of to Chord. SolidWorks takes one tolerance
            /// for a whole body, so the body is what the share is of.</summary>
            public bool Relative;
            public double RelativeDistance;

            /// <summary>For the log: "0.8 mm, 28.6 deg" or "0.5% of each
            /// body, 28.6 deg".</summary>
            public override string ToString()
            {
                string angle = (Angle * 180.0 / Math.PI).ToString(
                    "0.#", CultureInfo.InvariantCulture) + " deg";
                if (Relative)
                    return (RelativeDistance * 100.0).ToString(
                        "0.###", CultureInfo.InvariantCulture)
                        + "% of each body, " + angle;
                return (Chord * 1000.0).ToString("0.####", CultureInfo.InvariantCulture)
                    + " mm, " + angle;
            }

            /// <summary>The chord this body is cut to.</summary>
            public double ChordFor(IBody2 body)
            {
                if (!Relative) return Chord;
                return Math.Max(RelativeDistance * BodyDiagonal(body), 1e-6);
            }
        }

        /// <summary>Custom: a distance in metres and an angle in radians.
        /// </summary>
        public static Fineness Custom(double chord, double angle)
        {
            return new Fineness
            {
                Chord = Math.Max(chord, 1e-6),
                Angle = Math.Max(angle, 0.002),
            };
        }

        /// <summary>Relative Tessellation: a share of each body's diagonal,
        /// and an angle in radians.</summary>
        public static Fineness RelativeTo(double share, double angle)
        {
            double s = Math.Max(share, 1e-5);
            return new Fineness
            {
                Chord = s * 0.1,
                Angle = Math.Max(angle, 0.002),
                Relative = true,
                RelativeDistance = s,
            };
        }

        /// <summary>The diagonal of a body's box, in metres. 0.1 m when the
        /// body cannot say.</summary>
        public static double BodyDiagonal(IBody2 body)
        {
            double[] box = null;
            try { box = body == null ? null : body.GetBodyBox() as double[]; }
            catch { }
            if (box == null || box.Length < 6) return 0.1;
            double dx = box[3] - box[0], dy = box[4] - box[1], dz = box[5] - box[2];
            double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            return d > 1e-9 ? d : 0.1;
        }

        /// <summary>The angle for contact geometry and anything else that
        /// asks for a chord only.</summary>
        public const double DefaultAngle = 0.35;

        // The dial at each named quality, and what it cuts to. The four
        // names are the STEP import's presets (import_ui.QUALITY_PRESETS in
        // CADder), so Draft over the bridge and Draft from a STEP file give
        // the same mesh. The chord is a length, as it is there, and not a
        // share of the body: that is what makes the two routes agree. The
        // angle keeps a small hole round when the chord alone would leave
        // it square. The coarse end goes past Draft, to the Detail 100 of
        // the STEP import's simple mode and further.
        private static readonly double[] Dials = { 0.0, 0.15, 0.45, 0.75, 1.0 };
        private static readonly double[] Chords = { 0.005, 0.002, 0.0008, 0.0002, 0.00005 };
        private static readonly double[] Angles = { 0.8, 0.6, 0.5, 0.25, 0.1 };

        /// <summary>
        /// The chord and angle for a 0..1 quality dial: 0 is the coarsest,
        /// 0.15 Draft, 0.45 Balanced, 0.75 Fine and 1 Ultra. Between two
        /// names the chord and the angle change by the same ratio for each
        /// step of the dial.
        /// </summary>
        public static Fineness FinenessFor(double quality)
        {
            double q = double.IsNaN(quality) ? 0.45 : Math.Max(0.0, Math.Min(1.0, quality));
            int i = 0;
            while (i < Dials.Length - 2 && q > Dials[i + 1]) i++;
            double t = (q - Dials[i]) / (Dials[i + 1] - Dials[i]);
            return new Fineness
            {
                Chord = Chords[i] * Math.Pow(Chords[i + 1] / Chords[i], t),
                Angle = Angles[i] * Math.Pow(Angles[i + 1] / Angles[i], t),
            };
        }

        /// <summary>
        /// Appends one body to a definition. False means nothing could be
        /// read, which the caller logs and carries on from: one unreadable
        /// body must not cost the assembly.
        /// </summary>
        public static bool Append(
            IBody2 body, MeshDefinition mesh, Fineness fineness,
            Func<IFace2, IBody2, int> materialOf, Action<string> log,
            SmallFeatureSurvey.Plan defeature = null)
        {
            if (body == null || mesh == null) return false;
            double tolerance = fineness.ChordFor(body);
            double angle = fineness.Angle;
            int vertexMark = mesh.VertexCount;
            int triangleMark = mesh.Triangles.Count;
            // Every road below appends this body's vertices from here, and a
            // road that fails puts the mesh back to here first.
            mesh.BodyStarts.Add(vertexMark);
            if (AppendTessellation(body, mesh, tolerance, angle, materialOf, log,
                                   defeature: defeature))
            {
                int amiss = defeature == null
                    ? 0 : NotClosed(mesh, vertexMark, triangleMark);
                if (amiss == 0) return true;
                // The last word on the contract. A body SolidWorks
                // tessellates is closed, so a body that comes out of this
                // open has had something taken out of it that was holding it
                // together, and a part with a hole in it is worse than a part
                // with its bolt holes still in. The plan is thrown away and
                // the body goes as it is.
                //
                // The count is off the triangles already in hand, so it costs
                // no call to SolidWorks and nothing at all for a body nobody
                // asked to defeature.
                if (log != null)
                    log("small features: this body would not close ("
                        + amiss + " edge(s) wrong), so it is sent as it is");
                Truncate(mesh, vertexMark, triangleMark);
                if (AppendTessellation(body, mesh, tolerance, angle, materialOf, log))
                    return true;
            }
            // A tessellation can fail after its vertices are in, when a
            // vertex read fails or no facet stitches. Those vertices belong
            // to no triangle, so they go before the display mesh starts, or
            // they travel as loose points.
            Truncate(mesh, vertexMark, triangleMark);
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
            AppendTessellation(body, mesh, tolerance, DefaultAngle,
                (face, b) => face != null && keep(face) ? 0 : -1, log, skipRejected: true);
            return mesh.Triangles.Count / 3 - before;
        }

        private static bool AppendTessellation(
            IBody2 body, MeshDefinition mesh, double tolerance, double angle,
            Func<IFace2, IBody2, int> materialOf, Action<string> log,
            bool skipRejected = false, SmallFeatureSurvey.Plan defeature = null)
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
                // Match by TOPOLOGY: facets either side of an edge inside
                // ONE FACE then share vertices, which is what makes a face a
                // mesh rather than a pile of loose triangles.
                //
                // It does not reach across faces. Measured over the corpus
                // (2026-09-17, the plane_uv command): of 152,333 vertices,
                // not one was claimed by two faces. Every face carries its
                // own copies along the edges it shares, which is why each
                // vertex can carry that face's surface parameters and why a
                // fill of our own can give new points coordinates SolidWorks
                // agrees with exactly.
                tess.MatchType = (int)swTesselationMatchType_e.swTesselationMatchFacetTopology;
                tess.SurfacePlaneTolerance = tolerance;
                tess.SurfacePlaneAngleTolerance = angle;
                tess.CurveChordTolerance = tolerance;
                tess.CurveChordAngleTolerance = angle;
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
            int firstTriangle = mesh.Triangles.Count;
            int dropped = 0, refilled = 0, refused = 0, capped = 0;

            // The faces are numbered first, because a vertex has to be able
            // to say which one it belongs to: its surface parameters mean
            // nothing without the surface they are on, and SurfaceUv turns
            // them into a UV map once the triangles are all in.
            object[] faces = null;
            try { faces = body.GetFaces() as object[]; } catch { }
            var numbered = new List<IFace2>();
            var ordinalOf = new Dictionary<IntPtr, int>();
            foreach (var o in faces ?? new object[0])
            {
                var found = o as IFace2;
                if (found == null) continue;
                ordinalOf[Identity(found)] = numbered.Count;
                numbered.Add(found);
            }
            state.FaceOf = new int[vertexCount];
            for (int i = 0; i < vertexCount; i++) state.FaceOf[i] = -1;

            if (numbered.Count > 0)
            {
                for (int ordinal = 0; ordinal < numbered.Count; ordinal++)
                {
                    var face = numbered[ordinal];
                    state.Face = ordinal;
                    int[] facets = null;
                    try { facets = tess.GetFaceFacets(face) as int[]; } catch { }
                    if (facets == null || facets.Length == 0) continue;
                    int material = materialOf == null ? 0 : materialOf(face, body);

                    // A face inside a removed feature never travels, and one
                    // that owned the feature's loops travels as a fill of its
                    // own boundary instead of as the triangles SolidWorks
                    // drew around the holes. Its facets are still marked
                    // covered, so the orphan sweep below does not put them
                    // back.
                    if (defeature != null && defeature.IsGone(face))
                    {
                        foreach (int facet in facets)
                            if (facet >= 0 && facet < facetCount) covered[facet] = true;
                        dropped++;
                        continue;
                    }
                    // The rims this face loses. A flat face is rebuilt
                    // without them, which is exact and leaves the face
                    // simpler as well. Anything else keeps its own triangles
                    // and has the rims capped, and so does a flat face whose
                    // fill was refused: a face that kept its rim while the
                    // feature behind it went would leave the body open.
                    IList<PlaneRefill.Hole> rims = null;
                    if (defeature != null)
                    {
                        int at = defeature.FillAt(face);
                        if (at >= 0)
                        {
                            rims = defeature.FillHoles[at];
                            var fill = PlaneRefill.Build(face, tess, rims, log);
                            if (fill != null)
                            {
                                foreach (int facet in facets)
                                    if (facet >= 0 && facet < facetCount)
                                        covered[facet] = true;
                                for (int t = 0; t + 2 < fill.Count; t += 3)
                                    AddTriangle(state, fill[t], fill[t + 1],
                                                fill[t + 2], material);
                                refilled++;
                                continue;
                            }
                            refused++;      // it keeps the triangles it had
                        }
                        else
                        {
                            int lid = defeature.CapAt(face);
                            if (lid >= 0) rims = defeature.CapHoles[lid];
                        }
                    }

                    foreach (int facet in facets)
                    {
                        if (facet < 0 || facet >= facetCount || covered[facet]) continue;
                        covered[facet] = true;
                        if (skipRejected && material < 0) continue;
                        AddFacet(state, facet, material);
                    }

                    if (rims == null) continue;
                    var cap = SurfaceCap.Build(face, tess, rims, log);
                    if (cap == null) continue;
                    // A lid is wound from its rim (SurfaceCap.Lid), which
                    // the normals at a cylinder's end circle cannot check.
                    for (int t = 0; t + 2 < cap.Count; t += 3)
                        AddTriangle(state, cap[t], cap[t + 1], cap[t + 2], material,
                                    asGiven: true);
                    capped++;
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
                int ordinal;
                state.Face = face != null
                    && ordinalOf.TryGetValue(Identity(face), out ordinal) ? ordinal : -1;
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

            // Last, because it reads the triangles: every one of them, the
            // fills and the caps included, so a face rebuilt by this add-in
            // gets the same UVs as one SolidWorks drew. It can ADD points,
            // where a closed face had to be cut open along its seam, so the
            // count it hands back is the one the body now has.
            vertexCount = SurfaceUv.Apply(mesh, baseVertex, vertexCount,
                                          firstTriangle, numbered,
                                          ref state.FaceOf, log);
            state.VertexCount = vertexCount;

            if (defeature != null && (dropped > 0 || refilled > 0 || capped > 0))
            {
                int before = vertexCount;
                int after = Compact(mesh, baseVertex, vertexCount, firstTriangle);
                if (log != null)
                    log("small features: " + defeature.Removed + " removed, "
                        + defeature.Declined + " left alone; " + dropped
                        + " face(s) dropped, " + refilled + " refilled"
                        + (capped > 0 ? ", " + capped + " capped" : "")
                        + (refused > 0 ? ", " + refused + " kept their triangles "
                           + "because the fill refused them" : "")
                        + "; " + before + " vertices to " + after);
            }
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

            /// <summary>The face being read, as an index into the numbered
            /// faces of the body, or -1 for a facet whose face is unknown.
            /// </summary>
            public int Face = -1;

            /// <summary>Which face each vertex came from. A tessellated
            /// vertex belongs to exactly one face, so a triangle writes the
            /// face of all three.</summary>
            public int[] FaceOf;

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

            Claim(s, a, b, c);
            s.Mesh.Triangles.Add(s.BaseVertex + a);
            s.Mesh.Triangles.Add(s.BaseVertex + b);
            s.Mesh.Triangles.Add(s.BaseVertex + c);
            s.Mesh.TriangleMaterials.Add(material);
            s.Stitched++;
        }

        /// <summary>
        /// Adds one triangle by vertex index, flipped to agree with the
        /// normals exactly as a facet would be. A fill's winding comes from
        /// the face's own parameters, and a face can be reversed relative to
        /// its body, so it needs the same check.
        /// </summary>
        private static void AddTriangle(
            FacetState s, int a, int b, int c, int material, bool asGiven = false)
        {
            if (a < 0 || b < 0 || c < 0
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
            if (!asGiven && FacetStitcher.NeedsFlip(s.P0, s.P1, s.P2, s.NormalSum))
            {
                int swap = b; b = c; c = swap;
            }
            Claim(s, a, b, c);
            s.Mesh.Triangles.Add(s.BaseVertex + a);
            s.Mesh.Triangles.Add(s.BaseVertex + b);
            s.Mesh.Triangles.Add(s.BaseVertex + c);
            s.Mesh.TriangleMaterials.Add(material);
            s.Stitched++;
        }

        /// <summary>Notes which face these three vertices came from.
        /// </summary>
        private static void Claim(FacetState s, int a, int b, int c)
        {
            if (s.FaceOf == null || s.Face < 0) return;
            if (a < s.FaceOf.Length) s.FaceOf[a] = s.Face;
            if (b < s.FaceOf.Length) s.FaceOf[b] = s.Face;
            if (c < s.FaceOf.Length) s.FaceOf[c] = s.Face;
        }

        /// <summary>
        /// The same COM object always gives the same pointer here, which is
        /// what lets a face read back from a facet be matched with the face
        /// it was numbered as. Comparing the wrappers would not do it: two
        /// reads of one face need not hand back one wrapper.
        /// </summary>
        private static IntPtr Identity(object com)
        {
            IntPtr found = Marshal.GetIUnknownForObject(com);
            Marshal.Release(found);
            return found;
        }

        /// <summary>
        /// Drops the vertices this body no longer uses and renumbers its
        /// triangles, returning how many are left.
        ///
        /// A removed feature takes whole faces with it, and the rims its
        /// holes left on the faces around it go too. Their vertices were read
        /// before any of that was decided, so without this they would travel
        /// as loose points: nothing to see in a render, but they are most of
        /// what a mesh weighs, and the transfer is where half the saving is
        /// meant to be.
        ///
        /// This body's vertices are the last block in the mesh, because a
        /// body is appended and then compacted before the next one starts.
        /// </summary>
        private static int Compact(
            MeshDefinition mesh, int baseVertex, int vertexCount, int firstTriangle)
        {
            if (vertexCount <= 0) return 0;
            var used = new bool[vertexCount];
            for (int i = firstTriangle; i < mesh.Triangles.Count; i++)
            {
                int v = mesh.Triangles[i] - baseVertex;
                if (v >= 0 && v < vertexCount) used[v] = true;
            }
            var map = new int[vertexCount];
            int kept = 0;
            for (int i = 0; i < vertexCount; i++) map[i] = used[i] ? kept++ : -1;
            if (kept == vertexCount) return kept;

            bool normals = mesh.Normals.Count >= (baseVertex + vertexCount) * 3;
            bool uvs = mesh.Uvs.Count >= (baseVertex + vertexCount) * 2;
            for (int i = 0; i < vertexCount; i++)
            {
                if (!used[i] || map[i] == i) continue;
                int to = baseVertex + map[i], from = baseVertex + i;
                for (int k = 0; k < 3; k++)
                {
                    mesh.Positions[to * 3 + k] = mesh.Positions[from * 3 + k];
                    if (normals) mesh.Normals[to * 3 + k] = mesh.Normals[from * 3 + k];
                }
                if (!uvs) continue;
                mesh.Uvs[to * 2] = mesh.Uvs[from * 2];
                mesh.Uvs[to * 2 + 1] = mesh.Uvs[from * 2 + 1];
            }
            int spare = vertexCount - kept;
            mesh.Positions.RemoveRange((baseVertex + kept) * 3, spare * 3);
            if (normals) mesh.Normals.RemoveRange((baseVertex + kept) * 3, spare * 3);
            if (uvs) mesh.Uvs.RemoveRange((baseVertex + kept) * 2, spare * 2);
            for (int i = firstTriangle; i < mesh.Triangles.Count; i++)
            {
                int v = mesh.Triangles[i] - baseVertex;
                if (v >= 0 && v < vertexCount) mesh.Triangles[i] = baseVertex + map[v];
            }
            return kept;
        }

        /// <summary>
        /// How many edges of the triangles just appended have anything but
        /// two faces on them. Zero is a closed solid, which is what
        /// SolidWorks gave us and what has to come back.
        ///
        /// Matched by POSITION. A tessellated vertex is shared inside one
        /// face and never across faces, so the two triangles either side of a
        /// model edge carry different indices for the same point. A tenth of
        /// a micron is far finer than any tessellation tolerance and far
        /// coarser than the rounding between two faces reading one point.
        /// </summary>
        private static int NotClosed(
            MeshDefinition mesh, int vertexMark, int triangleMark)
        {
            var used = new Dictionary<long, int>();
            var places = new Dictionary<int, long>();
            for (int i = triangleMark; i + 2 < mesh.Triangles.Count; i += 3)
            {
                long a = Place(mesh, mesh.Triangles[i], places);
                long b = Place(mesh, mesh.Triangles[i + 1], places);
                long c = Place(mesh, mesh.Triangles[i + 2], places);
                Seen(used, a, b);
                Seen(used, b, c);
                Seen(used, c, a);
            }
            int amiss = 0;
            foreach (var kv in used) if (kv.Value != 2) amiss++;
            return amiss;
        }

        private static void Seen(Dictionary<long, int> used, long a, long b)
        {
            long key = a < b ? a * 1000003L + b : b * 1000003L + a;
            int had;
            used[key] = used.TryGetValue(key, out had) ? had + 1 : 1;
        }

        private static long Place(
            MeshDefinition mesh, int vertex, Dictionary<int, long> places)
        {
            long place;
            if (places.TryGetValue(vertex, out place)) return place;
            unchecked
            {
                long hash = 17;
                for (int k = 0; k < 3; k++)
                    hash = hash * 1000003L
                        + (long)Math.Round(mesh.Positions[vertex * 3 + k] / 1e-7);
                places[vertex] = hash;
                return hash;
            }
        }

        /// <summary>Puts the mesh back as it was before this body.</summary>
        private static void Truncate(
            MeshDefinition mesh, int vertexMark, int triangleMark)
        {
            if (mesh.Positions.Count > vertexMark * 3)
                mesh.Positions.RemoveRange(
                    vertexMark * 3, mesh.Positions.Count - vertexMark * 3);
            if (mesh.Normals.Count > vertexMark * 3)
                mesh.Normals.RemoveRange(
                    vertexMark * 3, mesh.Normals.Count - vertexMark * 3);
            if (mesh.Uvs.Count > vertexMark * 2)
                mesh.Uvs.RemoveRange(
                    vertexMark * 2, mesh.Uvs.Count - vertexMark * 2);
            if (mesh.Triangles.Count > triangleMark)
                mesh.Triangles.RemoveRange(
                    triangleMark, mesh.Triangles.Count - triangleMark);
            if (mesh.TriangleMaterials.Count > triangleMark / 3)
                mesh.TriangleMaterials.RemoveRange(
                    triangleMark / 3, mesh.TriangleMaterials.Count - triangleMark / 3);
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
