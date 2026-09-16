using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;

namespace Peak.SwToBlender.Sw
{
    /// <summary>
    /// Which components an "only the selected ones" export keeps.
    ///
    /// The set is larger than what the user clicked, in both directions, and
    /// both are needed.
    ///
    ///   * Downwards. Picking a subassembly means everything inside it. A user
    ///     who selects one node in the tree does not expect an empty file.
    ///
    ///   * Upwards. A component only reaches the STEP file if every assembly
    ///     above it is visible: SolidWorks omits the whole branch below a
    ///     hidden node, and the children of that branch still report themselves
    ///     visible. Hiding an ancestor to "trim" the file would therefore take
    ///     the selection with it.
    ///
    /// A face or an edge picked in the graphics area counts as its component.
    /// GetSelectedObjectsComponent4 answers with the owning component whatever
    /// kind of thing was picked, which is what a user means by "export this
    /// one" after clicking a face.
    ///
    /// Identity is IComponent2::Name2, which SolidWorks documents as the FULL
    /// hierarchical path with instance numbers ("subAssem1-2/Part1-1"), so it
    /// names one occurrence and not one document.
    /// </summary>
    public static class Selection
    {
        /// <summary>
        /// The names of the components to keep, or null when nothing in the
        /// model is selected. Null means "no restriction": every caller
        /// treats it as "keep everything", so an empty selection can never
        /// silently produce an empty file.
        /// </summary>
        public static HashSet<string> KeepSet(IModelDoc2 model, Action<string> log)
        {
            var picked = Picked(model, log);
            if (picked.Count == 0) return null;

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var comp in picked) AddSubtree(comp, keep, log);

            // Ancestors last, so the walk can stop at the first one already
            // present: everything above it is in by then, whether it arrived
            // through another selection's ancestors or inside its subtree.
            foreach (var comp in picked)
                for (var p = SafeParent(comp); p != null; p = SafeParent(p))
                {
                    string name = SafeName(p);
                    if (name == null || !keep.Add(name)) break;
                }

            log?.Invoke($"    selection: {picked.Count} picked, "
                      + $"{keep.Count} component(s) kept with their branches");
            return keep;
        }

        /// <summary>How many components the user has selected. Drives the
        /// dialog, so that the option cannot be offered when it would export
        /// nothing.</summary>
        public static int Count(IModelDoc2 model)
        {
            return Picked(model, null).Count;
        }

        /// <summary>
        /// The distinct components behind the current selection, whatever was
        /// actually clicked.
        /// </summary>
        private static List<IComponent2> Picked(IModelDoc2 model, Action<string> log)
        {
            var found = new List<IComponent2>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!(model is IAssemblyDoc)) return found;

            ISelectionMgr sel;
            try { sel = model.SelectionManager as ISelectionMgr; }
            catch (Exception ex)
            {
                log?.Invoke("selection manager unavailable: " + ex.Message);
                return found;
            }
            if (sel == null) return found;

            int count;
            try { count = sel.GetSelectedObjectCount2(-1); }
            catch (Exception ex)
            {
                log?.Invoke("selection count failed: " + ex.Message);
                return found;
            }

            // The selection list is 1-based, and -1 means every mark.
            for (int i = 1; i <= count; i++)
            {
                IComponent2 comp = null;
                try { comp = sel.GetSelectedObjectsComponent4(i, -1) as IComponent2; }
                catch (Exception ex) { log?.Invoke($"selection {i}: {ex.Message}"); }
                if (comp == null) continue;      // a sketch, a plane, a mate
                string name = SafeName(comp);
                if (name == null || !seen.Add(name)) continue;
                found.Add(comp);
            }
            return found;
        }

        private static void AddSubtree(IComponent2 comp, HashSet<string> keep,
                                       Action<string> log)
        {
            var stack = new Stack<IComponent2>();
            stack.Push(comp);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                string name = SafeName(c);
                // A name already present brings its whole branch with it: the
                // tree is a tree, so the subtree was walked once already.
                if (name == null || !keep.Add(name)) continue;
                object[] kids = null;
                try { kids = c.GetChildren() as object[]; }
                catch (Exception ex) { log?.Invoke($"children of {name}: {ex.Message}"); }
                if (kids == null) continue;
                foreach (var k in kids)
                    if (k is IComponent2 child) stack.Push(child);
            }
        }

        private static IComponent2 SafeParent(IComponent2 comp)
        {
            try { return comp.GetParent(); }
            catch { return null; }
        }

        private static string SafeName(IComponent2 comp)
        {
            try { return comp.Name2; }
            catch { return null; }
        }
    }
}
