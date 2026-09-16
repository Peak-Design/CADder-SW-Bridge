using System;
using System.Collections.Generic;
using System.Globalization;
using Peak.SwToBlender.Core;
using Peak.SwToBlender.Core.Model;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.SwToBlender.Sw
{
    /// <summary>
    /// Turns the walked assembly into a MeshScene: one definition per distinct
    /// part, one instance per occurrence.
    ///
    /// Sharing definitions is most of why the native path is fast: a fastener
    /// used two hundred times is tessellated once. The key is the referenced
    /// DOCUMENT plus its configuration, which is what decides a part's shape;
    /// two occurrences of the same document in the same configuration are the
    /// same geometry by construction.
    ///
    /// Body geometry comes back in the PART's own space, so the occurrence's
    /// total transform places it. That is the one assumption here that cannot
    /// be checked without SolidWorks running, so the first instance of each
    /// definition logs its bounding-box centre next to the transform it will
    /// be given: if the geometry were already in assembly space, those two
    /// would agree instead of the centre sitting near the part origin.
    /// </summary>
    public static class NativeSceneBuilder
    {
        /// <summary>
        /// <paramref name="only"/> restricts the scene to those component
        /// ids: how Blender asks for one part again at a finer tolerance
        /// without paying for the whole assembly.
        /// <paramref name="keepPaths"/> restricts it to those instance paths,
        /// which is what "only the selected components" means: the STEP route
        /// has honoured that option since the beginning, and the direct send
        /// tessellated the whole assembly whatever it said (2026-09-16).
        /// </summary>
        public static MeshScene Build(
            List<WalkedComponent> walked, double quality, Action<string> log,
            HashSet<string> only = null, bool separateSolids = false,
            HashSet<string> keepPaths = null)
        {
            var scene = new MeshScene();
            var definitions = new Dictionary<string, List<MeshDefinition>>(StringComparer.OrdinalIgnoreCase);
            var materials = new AppearanceTable(scene, log);
            int nextId = 0;
            double coarsest = 0.0;
            double totalSeconds = 0.0, slowest = 0.0;
            int totalTriangles = 0;

            foreach (var w in walked)
            {
                if (w == null || w.Comp == null) continue;
                if (only != null && !only.Contains(w.Id)) continue;
                if (keepPaths != null && (w.Graph == null || !keepPaths.Contains(w.Graph.Path)))
                    continue;
                // A FLEXIBLE subassembly's children are walked in their own
                // right and become their own instances; the node itself is
                // just their parent and owns nothing. A RIGID one's children
                // are not walked at all, so its geometry has to be gathered
                // from underneath it, see CollectBodies.
                if (w.Graph != null && w.Graph.Solving == "flexible") continue;
                // Hidden means hidden. A rigid subassembly's hidden children
                // were already being skipped underneath; a hidden component at
                // this level was not, so it paid full tessellation to arrive
                // in Blender as something SolidWorks does not draw.
                if (!IsVisible(w.Comp)) continue;

                // The occurrence's assembly-level appearance is part of
                // what the triangles carry, so it is part of the key.
                string key = DefinitionKey(w) + materials.OccurrenceKey(w.Comp);
                List<MeshDefinition> defs;
                if (!definitions.TryGetValue(key, out defs))
                {
                    var started = DateTime.UtcNow;
                    defs = new List<MeshDefinition>();
                    double tolerance = BuildDefinitions(
                        w, defs, quality, materials, log, separateSolids, ref nextId);
                    double seconds = (DateTime.UtcNow - started).TotalSeconds;
                    defs.RemoveAll(d => d.TriangleCount == 0);
                    if (defs.Count == 0)
                    {
                        if (log != null)
                            log("native export: no geometry for " + key);
                        continue;
                    }
                    coarsest = Math.Max(coarsest, tolerance);
                    slowest = Math.Max(slowest, seconds);
                    totalSeconds += seconds;
                    int triangles = 0;
                    foreach (var d in defs) triangles += d.TriangleCount;
                    totalTriangles += triangles;
                    // Tessellation is the whole cost of this path and it is
                    // one COM call per vertex and four per facet, so the log
                    // names anything that took long enough to be worth
                    // looking at. Nothing else says WHICH part is expensive.
                    if (log != null && seconds >= 0.5)
                        log(string.Format(CultureInfo.InvariantCulture,
                            "native export: {0} took {1:F1} s for {2} triangle(s)",
                            defs[0].Name, seconds, triangles));
                    definitions[key] = defs;
                    foreach (var d in defs)
                    {
                        scene.Definitions.Add(d);
                        LogPlacementCheck(d, w, log);
                    }
                }

                // Separate solids: one instance per body, all carrying the
                // component's id so the rig attaches every body of the part.
                foreach (var d in defs)
                {
                    string name = w.Graph != null ? w.Graph.Name : w.Id;
                    if (defs.Count > 1) name += " " + d.Name.Substring(d.Name.LastIndexOf(' ') + 1);
                    scene.Instances.Add(new MeshInstance
                    {
                        DefinitionId = d.Id,
                        ComponentId = w.Id,
                        Name = name,
                        Transform = TransformOf(w),
                    });
                }
            }

            scene.Tolerance = coarsest;
            if (log != null)
                log(string.Format(CultureInfo.InvariantCulture,
                    "native export: {0} definition(s), {1} instance(s), "
                    + "{2} triangle(s), tolerance {3:G4} m, {4:F1} s "
                    + "({5:F1} s in the slowest part)",
                    scene.Definitions.Count, scene.Instances.Count, totalTriangles,
                    coarsest, totalSeconds, slowest));
            return scene;
        }

        /// <summary>Tessellates every solid body under one occurrence: into
        /// one definition, or one per body with separateSolids (the STEP
        /// importer's "separate solids": a multibody part as one object per
        /// body). Returns the tolerance used.</summary>
        private static double BuildDefinitions(
            WalkedComponent w, List<MeshDefinition> defs, double quality,
            AppearanceTable materials, Action<string> log, bool separateSolids,
            ref int nextId)
        {
            double tolerance = 0.0;
            double[,] inverse;
            try { inverse = MathOps.InvertRigid(SwFrames.ComponentWorld(w.Comp)); }
            catch { inverse = MathOps.Identity4(); }
            string baseName = w.DocName ?? w.Id;
            int id = nextId;
            int bodies = 0;
            MeshDefinition shared = null;

            CollectBodies(w.Comp, inverse, log, (body, relative, owner) =>
            {
                bodies++;
                MeshDefinition def;
                if (separateSolids)
                {
                    def = new MeshDefinition { Id = id++, Name = baseName + " body " + bodies };
                    defs.Add(def);
                }
                else
                {
                    if (shared == null)
                    {
                        shared = new MeshDefinition { Id = id++, Name = baseName };
                        defs.Add(shared);
                    }
                    def = shared;
                }
                // The body's OWN component: under a rigid subassembly that
                // is a child the walker never visited, with its own part
                // document and its own appearance.
                var appearance = materials.For(owner ?? w.Comp);
                double diagonal = DiagonalOf(body);
                double tol = BodyTessellator.ToleranceFor(quality, diagonal);
                tolerance = Math.Max(tolerance, tol);
                int before = def.VertexCount;
                BodyTessellator.Append(
                    body, def, tol,
                    (face, b) => materials.Resolve(face, b, appearance, relative),
                    log);
                if (relative != null) Rebase(def, before, relative);
            });
            // A part with one body keeps the plain name whichever way.
            if (separateSolids && defs.Count == 1) defs[0].Name = baseName;
            nextId = id;
            return tolerance;
        }

        /// <summary>
        /// Every solid body under a component, each with the transform that
        /// takes it into that component's own space.
        ///
        /// An assembly component owns no bodies of its own: its geometry is
        /// its children's, so a rigid subassembly, whose children the walker
        /// never visits, is gathered by descending here. Parts stop the
        /// recursion immediately, which is the common case.
        /// </summary>
        private static void CollectBodies(
            IComponent2 comp, double[,] inverse, Action<string> log,
            Action<IBody2, double[,], IComponent2> visit)
        {
            if (comp == null) return;
            object[] children = null;
            try { children = comp.GetChildren() as object[]; } catch { }

            if (children == null || children.Length == 0)
            {
                object[] bodies = null;
                try { bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out _) as object[]; }
                catch (Exception ex)
                {
                    if (log != null) log("native export: GetBodies3 failed: " + ex.Message);
                }
                if (bodies == null) return;
                double[,] relative = null;
                try
                {
                    relative = MathOps.Multiply(inverse, SwFrames.ComponentWorld(comp));
                    if (IsIdentity(relative)) relative = null;
                }
                catch { }
                foreach (var o in bodies)
                {
                    var body = o as IBody2;
                    if (body != null) visit(body, relative, comp);
                }
                return;
            }

            foreach (var o in children)
            {
                var child = o as IComponent2;
                if (child == null) continue;
                bool suppressed = false;
                try
                {
                    suppressed = child.GetSuppression()
                        == (int)swComponentSuppressionState_e.swComponentSuppressed;
                }
                catch { }
                if (!suppressed && IsVisible(child))
                    CollectBodies(child, inverse, log, visit);
            }
        }

        /// <summary>Moves the vertices added since `from` into the walked
        /// component's space. Only a rigid subassembly's children need
        /// it.</summary>
        private static void Rebase(MeshDefinition def, int from, double[,] m)
        {
            for (int v = from; v < def.VertexCount; v++)
            {
                var p = MathOps.TransformPoint(m, new[]
                {
                    def.Positions[v * 3], def.Positions[v * 3 + 1], def.Positions[v * 3 + 2],
                });
                def.Positions[v * 3] = p[0];
                def.Positions[v * 3 + 1] = p[1];
                def.Positions[v * 3 + 2] = p[2];
                var n = MathOps.RotateVector(m, new[]
                {
                    def.Normals[v * 3], def.Normals[v * 3 + 1], def.Normals[v * 3 + 2],
                });
                def.Normals[v * 3] = n[0];
                def.Normals[v * 3 + 1] = n[1];
                def.Normals[v * 3 + 2] = n[2];
            }
        }

        /// <summary>Drawn in SolidWorks. Unreadable counts as visible: a
        /// component that cannot answer should still travel.</summary>
        private static bool IsVisible(IComponent2 comp)
        {
            if (comp == null) return false;
            try
            {
                return comp.Visible.Equals(
                    (int)swComponentVisibilityState_e.swComponentVisible);
            }
            catch { return true; }
        }

        private static bool IsIdentity(double[,] m)
        {
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    if (Math.Abs(m[r, c] - (r == c ? 1.0 : 0.0)) > 1e-12) return false;
            return true;
        }

        private static void LogPlacementCheck(MeshDefinition def, WalkedComponent w, Action<string> log)
        {
            if (log == null || def.VertexCount == 0) return;
            double cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < def.VertexCount; i++)
            {
                cx += def.Positions[i * 3];
                cy += def.Positions[i * 3 + 1];
                cz += def.Positions[i * 3 + 2];
            }
            cx /= def.VertexCount; cy /= def.VertexCount; cz /= def.VertexCount;
            var t = TransformOf(w);
            log(string.Format(CultureInfo.InvariantCulture,
                "native export: {0} centre [{1:G4},{2:G4},{3:G4}] placed at [{4:G4},{5:G4},{6:G4}]"
                + " ({7} tri)",
                def.Name, cx, cy, cz, t[3], t[7], t[11], def.TriangleCount));
        }

        private static string DefinitionKey(WalkedComponent w)
        {
            string path = null;
            try { path = w.Comp.GetPathName(); } catch { }
            if (string.IsNullOrEmpty(path)) path = w.Id;
            return path + "|" + (w.ReferencedConfiguration ?? "");
        }

        /// <summary>Row-major 4x4 in metres, matching the manifest's
        /// convention.</summary>
        private static double[] TransformOf(WalkedComponent w)
        {
            var m = w.Graph != null ? w.Graph.Transform : null;
            var flat = new double[16];
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    flat[r * 4 + c] = m != null ? m[r, c] : (r == c ? 1.0 : 0.0);
            return flat;
        }

        private static double DiagonalOf(IBody2 body)
        {
            try
            {
                var box = body.GetBodyBox() as double[];
                if (box != null && box.Length >= 6)
                {
                    double dx = box[3] - box[0], dy = box[4] - box[1], dz = box[5] - box[2];
                    double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (d > 1e-9) return d;
                }
            }
            catch { }
            return 0.1;
        }
    }
}
