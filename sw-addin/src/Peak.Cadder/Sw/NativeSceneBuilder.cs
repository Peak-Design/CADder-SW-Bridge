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
    /// Turns the walked assembly into a MeshScene: one definition per distinct
    /// part, one instance per PART occurrence, and one node per subassembly
    /// occurrence above them.
    ///
    /// Sharing definitions is most of why the native path is fast: a fastener
    /// used two hundred times is tessellated once. The key is the referenced
    /// DOCUMENT plus its configuration, which is what decides a part's shape;
    /// two occurrences of the same document in the same configuration are the
    /// same geometry by construction.
    ///
    /// The tree is the STEP file's tree. A rigid subassembly is ONE body to
    /// the rig, and the manifest says so, but it is still an assembly of parts
    /// to look at: SolidWorks writes its parts as their own STEP products,
    /// nested under the subassembly's product, and this writes them as their
    /// own instances, nested under the subassembly's node. Before 2026-09-16
    /// the geometry under a rigid subassembly was welded into one definition
    /// named after the subassembly, so the same part inside and outside a
    /// subassembly could not share a mesh, and its own name was lost
    /// (Oscar, cam-follower: three lifters arrived as one blob each).
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
            HashSet<string> keepPaths = null, ExportProgress progress = null,
            AppearanceOptions appearance = null, DefeatureOptions defeature = null)
        {
            progress = progress ?? ExportProgress.None;
            var scene = new MeshScene();
            var definitions = new Dictionary<string, List<MeshDefinition>>(StringComparer.OrdinalIgnoreCase);
            var materials = new AppearanceTable(scene, log, appearance);
            // Every subassembly occurrence seen, walked or descended into.
            // Only the ones an instance actually hangs under are written.
            var nodes = new Dictionary<string, MeshNode>(StringComparer.OrdinalIgnoreCase);
            int nextId = 0;
            double coarsest = 0.0;
            double totalSeconds = 0.0, slowest = 0.0;
            int totalTriangles = 0;

            int placed = 0;
            foreach (var w in walked)
            {
                progress.Step(placed++);
                if (w == null || w.Comp == null) continue;
                // A subassembly is a branch of the tree whether or not its
                // own geometry travels, so it is recorded before any filter.
                if (w.Graph != null && w.Graph.Solving != null)
                    Remember(nodes, w.Graph.Path, w.DocName, w.Id, TransformOf(w));
                if (only != null && !only.Contains(w.Id)) continue;
                if (keepPaths != null && (w.Graph == null || !keepPaths.Contains(w.Graph.Path)))
                    continue;
                // A FLEXIBLE subassembly's children are walked in their own
                // right and become their own instances; the node itself is
                // just their parent and owns nothing.
                if (w.Graph != null && w.Graph.Solving == "flexible") continue;
                // Hidden means hidden. A rigid subassembly's hidden children
                // were already being skipped underneath; a hidden component at
                // this level was not, so it paid full tessellation to arrive
                // in Blender as something SolidWorks does not draw.
                if (!IsVisible(w.Comp)) continue;

                foreach (var leaf in Leaves(w, nodes, log))
                {
                    // The occurrence's assembly-level appearance is part of
                    // what the triangles carry, so it is part of the key.
                    // Two occurrences of one document share one mesh, which
                    // is most of why this path is fast. Two occurrences the
                    // consumer wants defeatured DIFFERENTLY are no longer the
                    // same geometry, so the spec is part of the key.
                    var spec = defeature == null ? null : defeature.For(w.Id);
                    string key = DefinitionKey(leaf.Comp)
                        + materials.OccurrenceKey(leaf.Comp)
                        + (spec == null ? "" : spec.Key);
                    List<MeshDefinition> defs;
                    if (!definitions.TryGetValue(key, out defs))
                    {
                        var started = DateTime.UtcNow;
                        defs = new List<MeshDefinition>();
                        double tolerance = BuildDefinitions(
                            leaf, defs, quality, materials, log, separateSolids,
                            ref nextId, spec);
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
                            LogPlacementCheck(d, leaf, log);
                        }
                    }

                    // Separate solids: one instance per body, all carrying the
                    // component's id so the rig attaches every body of the part.
                    string basename = leaf.Name ?? "part";
                    foreach (var d in defs)
                    {
                        // ".body002" and the like, so several bodies of one
                        // part stay apart in the tree without making a branch
                        // of their own, exactly as the STEP route names them.
                        string suffix = d.Name != null
                            && d.Name.Length > basename.Length
                            && d.Name.StartsWith(basename, StringComparison.Ordinal)
                            ? d.Name.Substring(basename.Length) : "";
                        scene.Instances.Add(new MeshInstance
                        {
                            DefinitionId = d.Id,
                            ComponentId = w.Id,
                            Name = basename + suffix,
                            Path = leaf.Path + suffix,
                            Transform = leaf.Transform,
                            Local = leaf.Local,
                        });
                    }
                }
            }

            WriteNodes(scene, nodes);
            scene.Tolerance = coarsest;
            if (log != null)
                log(string.Format(CultureInfo.InvariantCulture,
                    "native export: {0} definition(s), {1} instance(s), "
                    + "{2} node(s), {3} triangle(s), tolerance {4:G4} m, {5:F1} s "
                    + "({6:F1} s in the slowest part)",
                    scene.Definitions.Count, scene.Instances.Count, scene.Nodes.Count,
                    totalTriangles, coarsest, totalSeconds, slowest));
            return scene;
        }

        /// <summary>One part occurrence: the live component, what to call it,
        /// where it sits in the tree, and where it sits in the world.</summary>
        private sealed class Leaf
        {
            public IComponent2 Comp;
            public string Name;
            public string Path;
            public double[] Transform;

            /// <summary>Its place inside the walked component, or null when
            /// it IS the walked component.</summary>
            public double[] Local;
        }

        /// <summary>
        /// Every PART occurrence whose geometry belongs to this walked
        /// component: itself when it is a part, and otherwise everything
        /// inside it, however deep.
        ///
        /// An assembly component owns no bodies of its own: its geometry is
        /// its children's. A rigid subassembly's children are never walked,
        /// so this is the only place they are seen, and every subassembly met
        /// on the way down is remembered as a node.
        /// </summary>
        private static IEnumerable<Leaf> Leaves(
            WalkedComponent w, Dictionary<string, MeshNode> nodes, Action<string> log)
        {
            var found = new List<Leaf>();
            var world = TransformOf(w);
            Descend(w.Comp, w.Graph != null ? w.Graph.Path : null, w.DocName,
                    world, nodes, found, log);
            // Every part of a rigid subassembly travels under the
            // subassembly's component id, so each one carries where it sits
            // inside it. The occurrence's own placement carries nothing.
            double[,] inverse = null;
            try { inverse = MathOps.InvertRigid(Square(world)); }
            catch { }
            foreach (var leaf in found)
                if (!ReferenceEquals(leaf.Comp, w.Comp) && inverse != null
                        && leaf.Transform != null)
                    leaf.Local = Flatten(MathOps.Multiply(inverse, Square(leaf.Transform)));
            return found;
        }

        private static void Descend(
            IComponent2 comp, string path, string docName, double[] transform,
            Dictionary<string, MeshNode> nodes, List<Leaf> found, Action<string> log)
        {
            if (comp == null) return;
            object[] children = null;
            try { children = comp.GetChildren() as object[]; } catch { }

            if (children == null || children.Length == 0)
            {
                found.Add(new Leaf
                {
                    Comp = comp,
                    Name = docName,
                    Path = path,
                    Transform = transform,
                });
                return;
            }

            Remember(nodes, path, docName, null, transform);
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
                if (suppressed || !IsVisible(child)) continue;
                // Name2 is the occurrence path from the ROOT at every depth
                // and Transform2 is root-relative at every depth, so a child
                // needs no accumulation: neither its path nor its place
                // depends on how deep the walk went to reach it.
                Descend(child, SafeName2(child) ?? path, DocNameOf(child),
                        WorldOf(child), nodes, found, log);
            }
        }

        private static void Remember(
            Dictionary<string, MeshNode> nodes, string path, string name,
            string componentId, double[] transform)
        {
            if (string.IsNullOrEmpty(path)) return;
            MeshNode node;
            if (!nodes.TryGetValue(path, out node))
            {
                node = new MeshNode { Path = path };
                nodes[path] = node;
            }
            if (!string.IsNullOrEmpty(name)) node.Name = name;
            if (!string.IsNullOrEmpty(componentId)) node.ComponentId = componentId;
            if (transform != null) node.Transform = transform;
        }

        /// <summary>The nodes an instance actually hangs under, parents
        /// first. A node nothing was placed under is left out: it would draw
        /// an empty collection for a subassembly that was filtered away.</summary>
        private static void WriteNodes(MeshScene scene, Dictionary<string, MeshNode> nodes)
        {
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var inst in scene.Instances)
            {
                string path = inst.Path;
                while (!string.IsNullOrEmpty(path))
                {
                    int slash = path.LastIndexOf('/');
                    if (slash < 0) break;
                    path = path.Substring(0, slash);
                    wanted.Add(path);
                }
            }
            var kept = new List<MeshNode>();
            foreach (var node in nodes.Values)
                if (wanted.Contains(node.Path)) kept.Add(node);
            kept.Sort((a, b) =>
            {
                int depth = Depth(a.Path).CompareTo(Depth(b.Path));
                return depth != 0 ? depth
                    : string.Compare(a.Path, b.Path, StringComparison.Ordinal);
            });
            scene.Nodes.AddRange(kept);
        }

        private static int Depth(string path)
        {
            int n = 0;
            foreach (var c in path ?? "") if (c == '/') n++;
            return n;
        }

        /// <summary>Tessellates every solid body of one part occurrence: into
        /// one definition, or one per body with separateSolids (the STEP
        /// importer's "separate solids": a multibody part as one object per
        /// body). Returns the tolerance used.</summary>
        private static double BuildDefinitions(
            Leaf leaf, List<MeshDefinition> defs, double quality,
            AppearanceTable materials, Action<string> log, bool separateSolids,
            ref int nextId, DefeatureSpec spec)
        {
            double tolerance = 0.0;
            string baseName = leaf.Name ?? "part";
            int id = nextId;
            int count = 0;
            MeshDefinition shared = null;
            var appearance = materials.For(leaf.Comp);

            object[] bodies = null;
            try { bodies = leaf.Comp.GetBodies3((int)swBodyType_e.swSolidBody, out _) as object[]; }
            catch (Exception ex)
            {
                if (log != null) log("native export: GetBodies3 failed: " + ex.Message);
            }
            foreach (var o in bodies ?? new object[0])
            {
                var body = o as IBody2;
                if (body == null) continue;
                count++;
                MeshDefinition def;
                if (separateSolids)
                {
                    // The STEP importer's own spelling for a body of a
                    // multibody part, so the two routes name it alike.
                    def = new MeshDefinition
                    {
                        Id = id++,
                        Name = baseName + ".body" + count.ToString(
                            "000", CultureInfo.InvariantCulture),
                    };
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
                double diagonal = DiagonalOf(body);
                double tol = BodyTessellator.ToleranceFor(quality, diagonal);
                tolerance = Math.Max(tolerance, tol);
                BodyTessellator.Append(
                    body, def, tol,
                    (face, b) => materials.Resolve(face, b, appearance, null),
                    log, NativeExport.Defeature(body, spec, log));
            }
            // A part with one body keeps the plain name whichever way.
            if (separateSolids && defs.Count == 1) defs[0].Name = baseName;
            nextId = id;
            return tolerance;
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

        private static void LogPlacementCheck(MeshDefinition def, Leaf leaf, Action<string> log)
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
            var t = leaf.Transform ?? Identity();
            log(string.Format(CultureInfo.InvariantCulture,
                "native export: {0} centre [{1:G4},{2:G4},{3:G4}] placed at [{4:G4},{5:G4},{6:G4}]"
                + " ({7} tri)",
                def.Name, cx, cy, cz, t[3], t[7], t[11], def.TriangleCount));
        }

        private static string DefinitionKey(IComponent2 comp)
        {
            string path = null;
            try { path = comp.GetPathName(); } catch { }
            if (string.IsNullOrEmpty(path)) path = SafeName2(comp) ?? "?";
            string configuration = null;
            try { configuration = comp.ReferencedConfiguration; } catch { }
            return path + "|" + (configuration ?? "");
        }

        /// <summary>Row-major 4x4 in metres, matching the manifest's
        /// convention.</summary>
        private static double[] TransformOf(WalkedComponent w)
        {
            var m = w.Graph != null ? w.Graph.Transform : null;
            if (m == null) return WorldOf(w.Comp);
            return Flatten(m);
        }

        private static double[] WorldOf(IComponent2 comp)
        {
            var m = SwFrames.ComponentWorld(comp);
            return m == null ? Identity() : Flatten(m);
        }

        private static double[,] Square(double[] flat)
        {
            var m = new double[4, 4];
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    m[r, c] = flat[r * 4 + c];
            return m;
        }

        private static double[] Flatten(double[,] m)
        {
            var flat = new double[16];
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    flat[r * 4 + c] = m[r, c];
            return flat;
        }

        private static double[] Identity()
        {
            var m = new double[16];
            m[0] = m[5] = m[10] = m[15] = 1.0;
            return m;
        }

        private static string SafeName2(IComponent2 comp)
        {
            try { return comp.Name2; } catch { return null; }
        }

        /// <summary>The referenced document's name, which SolidWorks also
        /// gives the STEP product. Falls back to the instance name without
        /// its number, which is what the document is usually called.</summary>
        private static string DocNameOf(IComponent2 comp)
        {
            string path = null;
            try { path = comp.GetPathName(); } catch { }
            if (!string.IsNullOrEmpty(path))
                return System.IO.Path.GetFileNameWithoutExtension(path);
            return AssemblyWalker.LastSegmentWithoutInstance(SafeName2(comp));
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
