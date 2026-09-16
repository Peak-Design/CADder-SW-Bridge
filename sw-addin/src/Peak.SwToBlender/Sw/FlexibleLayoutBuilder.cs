using System;
using System.Collections.Generic;
using System.Linq;
using Peak.SwToBlender.Appearance;
using Peak.SwToBlender.Core;

namespace Peak.SwToBlender.Sw
{
    /// <summary>
    /// Builds the FlexiblePoseFixer's input from the walked tree: for every
    /// subassembly document that is inserted more than once AND has at least
    /// one flexed flexible instance, one request describing each instance's
    /// correct internal layout: the flexed instance's from its walked
    /// children's actual world transforms, every other instance's from the
    /// document's saved layout (captured on the flexible instance's walk,
    /// because a rigid twin's interior is never walked).
    /// </summary>
    public static class FlexibleLayoutBuilder
    {
        public static List<FlexFixRequest> Build(
            List<WalkedComponent> walked, Action<string> log)
        {
            var requests = new List<FlexFixRequest>();
            var doneFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var flexed in walked)
            {
                var g = flexed.Graph;
                if (g.Solving != "flexible" || g.Suppressed || g.FileName == null)
                    continue;
                if (!flexed.Children.Any(c => c.Graph.MatePoseDelta != null))
                    continue;
                if (!doneFiles.Add(g.FileName)) continue;

                var instances = walked
                    .Where(w => !w.Graph.Suppressed && string.Equals(
                        w.Graph.FileName, g.FileName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (instances.Count < 2) continue;

                var request = new FlexFixRequest();
                bool ok = true;
                foreach (var inst in instances)
                {
                    var layout = BuildInstanceLayout(inst, flexed);
                    if (layout == null) { ok = false; break; }
                    request.Instances.Add(layout);
                }
                if (ok)
                {
                    requests.Add(request);
                }
                else if (log != null)
                {
                    log("flexible fix: could not describe every instance of "
                        + g.FileName + "; the STEP keeps the shared layout");
                }
            }
            return requests;
        }

        private static FlexInstanceLayout BuildInstanceLayout(
            WalkedComponent inst, WalkedComponent flexedReference)
        {
            var subWorld = inst.Graph.Transform;
            if (subWorld == null) return null;
            var parentWorld = inst.Parent != null ? inst.Parent.Graph.Transform : null;
            var rel = parentWorld == null
                ? subWorld
                : MathOps.Multiply(MathOps.InvertRigid(parentWorld), subWorld);

            var layout = new FlexInstanceLayout
            {
                Path = inst.Graph.Path,
                SubDocName = inst.DocName,
                ParentRelTranslationM = new[] { rel[0, 3], rel[1, 3], rel[2, 3] },
            };

            bool useActual = inst.Graph.Solving == "flexible"
                && inst.Children.Count > 0;
            if (useActual)
            {
                var invSub = MathOps.InvertRigid(subWorld);
                foreach (var child in inst.Children)
                {
                    if (child.Graph.Suppressed) continue;
                    if (child.Graph.Transform == null) return null;
                    layout.Children.Add(new FlexChildPose
                    {
                        Key = LastSegment(child.Graph.Path),
                        ProductName = child.DocName,
                        LocalM = MathOps.Multiply(invSub, child.Graph.Transform),
                    });
                }
            }
            else
            {
                var doc = flexedReference.SubDocLayout;
                if (doc == null || doc.Count == 0) return null;
                foreach (var c in doc)
                {
                    if (c.Suppressed) continue;
                    layout.Children.Add(new FlexChildPose
                    {
                        Key = c.LeafName,
                        ProductName = c.DocName,
                        LocalM = c.Local,
                    });
                }
            }
            return layout.Children.Count > 0 ? layout : null;
        }

        private static string LastSegment(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            int slash = path.LastIndexOf('/');
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }
    }
}
