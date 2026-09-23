using System;
using System.Collections.Generic;
using System.IO;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.Cadder.Sw
{
    /// <summary>
    /// One occurrence that survived the WYSIWYG walk: the live component, its
    /// recorded GraphComponent, and the tree links the occurrence matcher
    /// needs. Parent is null for top-level components; Children is filled only
    /// under flexible subassemblies, because a rigid subassembly is one leaf
    /// and its interior is nobody's business here.
    /// </summary>
    /// <summary>One child of a flexible subassembly's OWN document: its
    /// instance name there, the referenced document's base name (which is the
    /// STEP PRODUCT name), and its transform in the document frame. This is
    /// the layout a rigid twin of the same document should show.</summary>
    public sealed class SubDocChild
    {
        public string LeafName;
        public string DocName;
        public double[,] Local;
        public bool Suppressed;
    }

    public sealed class WalkedComponent
    {
        public string Id;
        public Component2 Comp;
        public GraphComponent Graph = new GraphComponent();
        public WalkedComponent Parent;
        public List<WalkedComponent> Children = new List<WalkedComponent>();

        /// <summary>The sub DOCUMENT's saved child layout: filled only for
        /// flexible subassemblies (their document is guaranteed in memory).
        /// The flexible-twin STEP fix reads a rigid twin's correct layout from
        /// here, because a rigid sub's interior is never walked.</summary>
        public List<SubDocChild> SubDocLayout;

        /// <summary>The 16 doubles of Transform2.ArrayData, root-relative, in
        /// metres. The occurrence matcher consumes the raw array because its
        /// parent-frame conversion runs both rotation conventions and keeps
        /// the one that matches. Null when the transform is unavailable.</summary>
        public double[] RawTransform;

        /// <summary>Referenced document file name without the extension.
        /// SolidWorks builds the STEP PRODUCT name from this.</summary>
        public string DocName;

        public string ReferencedConfiguration;

        /// <summary>The nearest walked ancestor that is a FLEXIBLE
        /// subassembly, or null. MateReader uses it to decide which frame a
        /// mate's entity parameters live in.</summary>
        public WalkedComponent FlexibleAncestor
        {
            get
            {
                for (var p = Parent; p != null; p = p.Parent)
                    if (p.Graph.Solving == "flexible") return p;
                return null;
            }
        }
    }

    /// <summary>
    /// The WYSIWYG walk. Top-level components always; a subassembly recurses
    /// only when its solving mode is flexible, because only then do its
    /// internal mates still move anything in the open assembly. Component
    /// documents are never opened: everything here reads through the
    /// occurrence, so a large assembly does not page in every part file.
    /// </summary>
    public static class AssemblyWalker
    {
        public static List<WalkedComponent> Walk(IAssemblyDoc assembly, Action<string> log)
        {
            var result = new List<WalkedComponent>();
            var top = assembly.GetComponents(true) as object[];
            if (top == null) return result;

            // Ids follow the INSTANCE NAME, sorted, at every level. The
            // order GetComponents returns is not stable: two exports of one
            // unchanged assembly in two SolidWorks sessions numbered the
            // same occurrences differently (live cam-follower2, 2026-09-15:
            // c001 was the cam in one export and the rod in the next), which
            // renumbers every group and joint with them and breaks anything
            // saved against an id (a mechanism choice, an update from CAD).
            // Sorting by name keeps a parent before its children, because a
            // child's name starts with the parent's.
            int next = 0;
            foreach (var comp in Sorted(top))
                Add(comp, null, result, ref next, log, fixedInSub: false);
            return result;
        }

        /// <summary>Components by instance name, so the ids do not depend
        /// on the order SolidWorks happens to return.</summary>
        private static List<Component2> Sorted(object[] raw)
        {
            var list = new List<Component2>();
            foreach (var o in raw ?? new object[0])
            {
                var comp = o as Component2;
                if (comp != null) list.Add(comp);
            }
            list.Sort((a, b) => string.Compare(SafeName2(a), SafeName2(b), StringComparison.Ordinal));
            return list;
        }

        /// <summary>The same order, as the objects the caller already has.</summary>
        private static object[] SortedObjects(object[] raw)
        {
            var sorted = Sorted(raw);
            var result = new object[sorted.Count];
            for (int i = 0; i < sorted.Count; i++) result[i] = sorted[i];
            return result;
        }

        private static void Add(
            Component2 comp, WalkedComponent parent,
            List<WalkedComponent> result, ref int next, Action<string> log,
            bool fixedInSub, double[,] subDocLocal = null)
        {
            var w = new WalkedComponent();
            next++;
            w.Id = "c" + next.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
            w.Comp = comp;
            w.Parent = parent;

            var g = w.Graph;
            g.Id = w.Id;
            // Name2 is the instance path from the root ("Sub-1/Jaw-2") at
            // every depth, so it needs no accumulation and carries no
            // file-system prefix.
            g.Path = comp.Name2;
            g.Name = LastSegmentWithoutInstance(comp.Name2);

            string pathName = null;
            try { pathName = comp.GetPathName(); } catch { }
            g.FileName = string.IsNullOrEmpty(pathName) ? null : Path.GetFileName(pathName);
            w.DocName = string.IsNullOrEmpty(pathName)
                ? LastSegmentWithoutInstance(comp.Name2)
                : Path.GetFileNameWithoutExtension(pathName);
            try { w.ReferencedConfiguration = comp.ReferencedConfiguration; } catch { }

            int suppression = (int)swComponentSuppressionState_e.swComponentResolved;
            try { suppression = comp.GetSuppression2(); } catch { }
            g.Suppressed = suppression == (int)swComponentSuppressionState_e.swComponentSuppressed;

            try { g.IsFixed = comp.IsFixed(); } catch { }
            try { g.ConstrainedStatus = comp.GetConstrainedStatus(); } catch { }

            // Fixed inside a flexible subassembly means rigid to the SUB's
            // frame, never to the world: it must not ground a group. The
            // fixedInSub flag comes from the subassembly's own tree (see the
            // recursion below), and only from there. IsFixed on the
            // top-context handle is true for EVERY child of a flexible
            // subassembly that is fixed itself (live 2026-09-23: the
            // hydraulic assembly and a hinge, each placed fixed and
            // flexible, came out as one rigid body with no joints).
            if (parent != null)
            {
                g.ParentId = parent.Id;
                g.IsFixed = false;
                g.FixedInSubassembly = fixedInSub;
            }

            // Solving returns -1 for part components (API help,
            // IComponent2~Solving.html), which is the cleanest part-vs-
            // subassembly test that works on lightweight components too.
            int solving = -1;
            try { solving = comp.Solving; } catch { }
            if (solving == (int)swComponentSolvingOption_e.swComponentRigidSolving) g.Solving = "rigid";
            else if (solving == (int)swComponentSolvingOption_e.swComponentFlexibleSolving) g.Solving = "flexible";
            else g.Solving = null;

            try
            {
                var t = comp.Transform2;
                g.Transform = SwFrames.ToMatrix(t);
                w.RawTransform = t == null ? null : t.ArrayData as double[];
            }
            catch { }
            // The schema requires a transform on every component; a suppressed
            // occurrence has none, and identity is the honest stand-in because
            // the component is also flagged suppressed.
            if (g.Transform == null) g.Transform = MathOps.Identity4();

            ReadBox(comp, g, log);

            // A flexible instance can be posed away from the sub DOCUMENT's
            // saved positions, and the internal mates are read through the
            // document: their geometry and dimension values describe the
            // document pose. The delta (actual = delta × document pose, world
            // frame) lets the classifier shift value_at_rest to the flexed
            // pose (live corpus 07 flexible-sub2, 2026-08-22).
            if (subDocLocal != null && parent != null && parent.Graph.Transform != null)
            {
                var matePose = MathOps.Multiply(parent.Graph.Transform, subDocLocal);
                var delta = MathOps.Multiply(g.Transform, MathOps.InvertRigid(matePose));
                if (!IsNearIdentity(delta)) g.MatePoseDelta = delta;
            }

            result.Add(w);
            if (parent != null) parent.Children.Add(w);

            // Flexible subassembly: the leaf above stays in the list (top
            // level mates grab its planes) AND the children join the graph.
            // Their Transform2 is root-relative already, see SwFrames.
            // A suppressed subassembly has no live children to walk.
            if (g.Solving == "flexible" && !g.Suppressed)
            {
                // Fixed state lives in the SUB's own tree: the top-context
                // child handles report IsFixed()=false even for a child fixed
                // in the subassembly document (live corpus 07, 2026-08-22,
                // the hinge's fixed base floated as its own group). A
                // flexible sub is always resolved, so its document is in
                // memory; this reads it, never opens it.
                HashSet<string> fixedNames;
                Dictionary<string, double[,]> subLocals;
                ReadSubDocTree(comp, log, out fixedNames, out subLocals,
                    out w.SubDocLayout);
                var children = comp.GetChildren() as object[];
                if (children != null)
                {
                    foreach (var o in SortedObjects(children))
                    {
                        var child = o as Component2;
                        if (child == null) continue;
                        string leaf = LastPathSegment(SafeName2(child));
                        double[,] local = null;
                        if (leaf != null) subLocals.TryGetValue(leaf, out local);
                        Add(child, w, result, ref next, log,
                            fixedInSub: leaf != null && fixedNames.Contains(leaf),
                            subDocLocal: local);
                    }
                }
            }
        }

        /// <summary>Reads a flexible subassembly's OWN document tree once:
        /// which top-level children are fixed there (instance names,
        /// "hinge-base-1"), and each child's transform in the document's
        /// frame: the pose the sub's internal mates describe.</summary>
        private static void ReadSubDocTree(
            Component2 subComp, Action<string> log,
            out HashSet<string> fixedNames, out Dictionary<string, double[,]> localTransforms,
            out List<SubDocChild> layout)
        {
            fixedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            localTransforms = new Dictionary<string, double[,]>(StringComparer.OrdinalIgnoreCase);
            layout = new List<SubDocChild>();
            try
            {
                var asm = subComp.GetModelDoc2() as IAssemblyDoc;
                if (asm == null) return;
                var comps = asm.GetComponents(true) as object[];
                if (comps == null) return;
                foreach (var o in comps)
                {
                    var c = o as Component2;
                    if (c == null) continue;
                    string leaf = LastPathSegment(SafeName2(c));
                    if (leaf == null) continue;
                    bool isFixed = false;
                    try { isFixed = c.IsFixed(); } catch { }
                    if (isFixed) fixedNames.Add(leaf);

                    var child = new SubDocChild { LeafName = leaf };
                    try
                    {
                        child.Suppressed = c.GetSuppression2()
                            == (int)swComponentSuppressionState_e.swComponentSuppressed;
                    }
                    catch { }
                    try
                    {
                        string p = c.GetPathName();
                        child.DocName = string.IsNullOrEmpty(p)
                            ? LastSegmentWithoutInstance(leaf)
                            : Path.GetFileNameWithoutExtension(p);
                    }
                    catch { child.DocName = LastSegmentWithoutInstance(leaf); }
                    try
                    {
                        var m = SwFrames.ToMatrix(c.Transform2);
                        if (m != null)
                        {
                            localTransforms[leaf] = m;
                            child.Local = m;
                        }
                    }
                    catch { }
                    if (child.Local != null) layout.Add(child);
                }
            }
            catch (Exception ex)
            {
                if (log != null) log("sub-doc tree query failed: " + ex.Message);
            }
        }

        /// <summary>Rigid-transform identity check at the tolerances that
        /// matter for limit correction: a micrometre of drift is noise, a
        /// flexed hinge is degrees and millimetres.</summary>
        private static bool IsNearIdentity(double[,] m)
        {
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                {
                    double want = r == c ? 1.0 : 0.0;
                    if (Math.Abs(m[r, c] - want) > 1e-7) return false;
                }
            return Math.Abs(m[0, 3]) <= 1e-7
                && Math.Abs(m[1, 3]) <= 1e-7
                && Math.Abs(m[2, 3]) <= 1e-7;
        }

        private static string SafeName2(Component2 comp)
        {
            try { return comp.Name2; } catch { return null; }
        }

        /// <summary>"hinge-1/hinge-base-1" gives "hinge-base-1"; a bare name
        /// passes through. Instance suffixes stay: they distinguish
        /// instances.</summary>
        private static string LastPathSegment(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int slash = path.LastIndexOf('/');
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }

        /// <summary>
        /// Global-frame bounding box. GetBox returns two diagonal corners with
        /// no min/max ordering promise, so each axis sorts its own pair. The
        /// call returns null for unloaded subassemblies, and the API help
        /// marks the values approximate: good enough for the bone-length
        /// heuristic they feed, nothing else.
        /// </summary>
        private static void ReadBox(Component2 comp, GraphComponent g, Action<string> log)
        {
            try
            {
                var box = comp.GetBox(false, false) as double[];
                if (box == null || box.Length < 6) return;
                g.BboxMin = new[]
                {
                    Math.Min(box[0], box[3]),
                    Math.Min(box[1], box[4]),
                    Math.Min(box[2], box[5]),
                };
                g.BboxMax = new[]
                {
                    Math.Max(box[0], box[3]),
                    Math.Max(box[1], box[4]),
                    Math.Max(box[2], box[5]),
                };
            }
            catch (Exception ex)
            {
                if (log != null) log("GetBox failed for " + g.Path + ": " + ex.Message);
            }
        }


        /// <summary>"Sub-1/Jaw-2" gives "Jaw". Same rule as NEXT-STEP's
        /// AppearanceLadder, because the STEP name fallback must behave the
        /// same way in both add-ins.</summary>
        public static string LastSegmentWithoutInstance(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int slash = path.LastIndexOf('/');
            string seg = slash >= 0 ? path.Substring(slash + 1) : path;
            int dash = seg.LastIndexOf('-');
            return dash > 0 ? seg.Substring(0, dash) : seg;
        }
    }
}
