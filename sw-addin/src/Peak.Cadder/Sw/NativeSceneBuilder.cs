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
            List<WalkedComponent> walked, BodyTessellator.Fineness fineness, Action<string> log,
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

            var filter = PathFilter.For(keepPaths);
            var shown = new Dictionary<WalkedComponent, bool>();
            Func<WalkedComponent, bool> visible = p =>
            {
                bool drawn;
                if (!shown.TryGetValue(p, out drawn)) shown[p] = drawn = IsVisible(p.Comp);
                return drawn;
            };

            int placed = 0;
            foreach (var w in walked)
            {
                // Tessellation is most of the cost of a direct send and it
                // only reads the model, so it can stop between any two
                // components. The bar recorded an Escape here and nothing
                // read it: the send ran to the end and went to Blender.
                progress.StopIfCancelled();
                progress.Step(placed++);
                if (w == null || w.Comp == null) continue;
                // A subassembly is a branch of the tree whether or not its
                // own geometry travels, so it is recorded before any filter.
                if (w.Graph != null && w.Graph.Solving != null)
                    Remember(nodes, w.Graph.Path, w.DocName, w.Id, TransformOf(w));
                if (only != null && !only.Contains(w.Id)) continue;
                // A path names one PLACEMENT. It can be this component's
                // own, and then everything under it travels, or it can be a
                // part inside a rigid subassembly, and then only that part
                // does. Every part of such a subassembly carries the
                // SUBASSEMBLY's component id, so the id alone cannot say
                // which: asking for one part re-tessellated the 78 parts of
                // the branch it sits on (Conveyor12k-A00, Oscar, 2026-09-17).
                // See PathFilter for why a path in the set does not always
                // bring its whole branch.
                bool wholeBranch = filter == null
                    || (w.Graph != null && filter.Wants(w.Graph.Path));
                if (!wholeBranch && (w.Graph == null || !filter.KeepsBelow(w.Graph.Path)))
                    continue;
                // A FLEXIBLE subassembly's children are walked in their own
                // right and become their own instances; the node itself is
                // just their parent and owns nothing.
                if (w.Graph != null && w.Graph.Solving == "flexible") continue;
                // Hidden means hidden. A rigid subassembly's hidden children
                // were already being skipped underneath; a hidden component at
                // this level was not, so it paid full tessellation to arrive
                // in Blender as something SolidWorks does not draw.
                if (!visible(w) || HiddenAbove(w, visible)) continue;
                // Suppressed has no geometry to read. The walker keeps such
                // a component for the manifest, and each one wrote its own
                // "no geometry" line here: 69 of the 71 in one send (live
                // CutterRig, 2026-09-21).
                if (w.Graph != null && w.Graph.Suppressed) continue;

                foreach (var leaf in Leaves(w, nodes, log))
                {
                    if (!wholeBranch && !filter.Wants(leaf.Path)) continue;
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
                            leaf, defs, fineness, materials, log, separateSolids,
                            ref nextId, spec);
                        double seconds = (DateTime.UtcNow - started).TotalSeconds;
                        defs.RemoveAll(d => d.TriangleCount == 0);
                        if (defs.Count == 0)
                        {
                            if (log != null)
                                log("native export: no geometry for " + key);
                            // Remembered empty, so the next occurrence of
                            // this document is not read again: a fitting
                            // used four times was tried four times (live
                            // CutterRig, 2026-09-21).
                            definitions[key] = defs;
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

            // An Escape during the last component is read here, before the
            // scene is written and sent.
            progress.StopIfCancelled();
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

        /// <summary>
        /// Which placements a keep set asks for.
        ///
        /// A keep set from Selection.KeepSet holds the picked components,
        /// everything under each of them, and every ANCESTOR of each, which
        /// the STEP route needs to keep a pick visible. An ancestor is in
        /// the set for that reason only, so a path in the set brings its
        /// whole branch only when nothing below it is in the set too.
        /// Before, a pick of one part inside a rigid subassembly matched the
        /// subassembly itself as an ancestor, and all of its parts were
        /// tessellated and sent. The STEP route of the same export sent the
        /// one part. A path that names a branch with nothing below it, as a
        /// Retessellate request does, still brings the whole branch.
        /// </summary>
        internal sealed class PathFilter
        {
            private readonly HashSet<string> _keep;

            /// <summary>Every branch that something in the set lies under.
            /// </summary>
            private readonly HashSet<string> _above =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            private PathFilter(HashSet<string> keep)
            {
                _keep = keep;
                foreach (var path in keep)
                    for (string p = Parent(path); p != null; p = Parent(p))
                        if (!_above.Add(p)) break;
            }

            /// <summary>Null for no keep set, which keeps everything.</summary>
            public static PathFilter For(HashSet<string> keep)
            {
                return keep == null ? null : new PathFilter(keep);
            }

            /// <summary>Whether this placement travels whole: it, or a
            /// branch above it, is in the set with nothing of its own below
            /// it in the set.</summary>
            public bool Wants(string path)
            {
                for (string p = path; p != null; p = Parent(p))
                    if (_keep.Contains(p) && !_above.Contains(p)) return true;
                return false;
            }

            /// <summary>Whether something below this branch is kept.</summary>
            public bool KeepsBelow(string branch)
            {
                return branch != null && _above.Contains(branch);
            }

            private static string Parent(string path)
            {
                if (string.IsNullOrEmpty(path)) return null;
                int slash = path.LastIndexOf('/');
                return slash > 0 ? path.Substring(0, slash) : null;
            }
        }

        /// <summary>
        /// Whether a walked component sits under a walked subassembly that
        /// SolidWorks does not draw. The walk goes into every flexible
        /// subassembly, hidden or not, and SolidWorks leaves out the whole
        /// branch below a hidden node while its children still report
        /// themselves visible (Selection). So the parts of a hidden flexible
        /// subassembly were tessellated and placed in Blender, where the
        /// STEP route leaves them out.
        /// </summary>
        internal static bool HiddenAbove(WalkedComponent w, Func<WalkedComponent, bool> visible)
        {
            for (var p = w == null ? null : w.Parent; p != null; p = p.Parent)
                if (!visible(p)) return true;
            return false;
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

        /// <summary>Tessellates every solid body and every shown surface
        /// body of one part occurrence (NativeExport.BodiesToSend): into
        /// one definition, or one per body with separateSolids (the STEP
        /// importer's "separate solids": a multibody part as one object per
        /// body). Returns the tolerance used.</summary>
        private static double BuildDefinitions(
            Leaf leaf, List<MeshDefinition> defs, BodyTessellator.Fineness fineness,
            AppearanceTable materials, Action<string> log, bool separateSolids,
            ref int nextId, DefeatureSpec spec)
        {
            double tolerance = 0.0;
            string baseName = leaf.Name ?? "part";
            int id = nextId;
            int count = 0;
            MeshDefinition shared = null;
            var appearance = materials.For(leaf.Comp);

            object[] solids = null, sheets = null;
            try { solids = leaf.Comp.GetBodies3((int)swBodyType_e.swSolidBody, out _) as object[]; }
            catch (Exception ex)
            {
                if (log != null) log("native export: GetBodies3 failed: " + ex.Message);
            }
            try { sheets = leaf.Comp.GetBodies3((int)swBodyType_e.swSheetBody, out _) as object[]; }
            catch (Exception ex)
            {
                if (log != null) log("native export: GetBodies3 failed for surfaces: " + ex.Message);
            }
            foreach (var body in NativeExport.BodiesToSend(solids, sheets))
            {
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
                tolerance = Math.Max(tolerance, fineness.ChordFor(body));
                if (!BodyTessellator.Append(
                        body, def, fineness,
                        (face, b) => materials.Resolve(face, b, appearance, null),
                        log, NativeExport.Defeature(body, spec, log))
                    && log != null)
                    log(NativeExport.NoTriangles(baseName, count));
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
    }
}
