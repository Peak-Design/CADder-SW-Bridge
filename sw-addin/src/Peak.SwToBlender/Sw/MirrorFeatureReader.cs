using System;
using System.Collections.Generic;
using Peak.SwToBlender.Core;
using Peak.SwToBlender.Core.Model;
using SolidWorks.Interop.sldworks;

namespace Peak.SwToBlender.Sw
{
    /// <summary>
    /// Assembly mirror features, read as source/instance occurrence pairs.
    ///
    /// A mirrored instance carries no mate to its source, so nothing in the
    /// mate graph says the two move together, but SolidWorks keeps them
    /// reflections of one another, and a rig that ignores that lets the two
    /// halves of a mirrored mechanism drift apart.
    ///
    /// The feature names the SOURCE components; it does not name the
    /// instances it created. So the feature supplies the candidate set and the
    /// GEOMETRY supplies the pairing: an instance is this source's mirror when
    /// the transform between them is an exact reflection. That test is worth
    /// more than a lookup would be, because it also answers the question the
    /// lookup cannot: whether the instance is STILL a mirror image. An
    /// instance somebody has since dragged is a glide reflection, which
    /// MathOps.TryReflectionPlane refuses, and it correctly gets no coupling.
    ///
    /// Everything here is read-only. AccessSelections/ReleaseSelectionAccess
    /// is the documented way to READ a feature's selection properties; the
    /// release runs in a finally so an exception cannot leave the feature in
    /// selection-access mode.
    /// </summary>
    public static class MirrorFeatureReader
    {
        /// <summary>Two candidate planes are the same plane within this much
        /// (unit normal dot product, and metres of offset). Mirror instances
        /// created by one feature share their plane exactly; the tolerance is
        /// only there for float noise in the transforms.</summary>
        private const double PlaneTol = 1e-6;

        public static void Read(
            IModelDoc2 model, List<WalkedComponent> walked, MateGraph graph,
            Action<string> log)
        {
            if (model == null || walked == null || graph == null) return;

            var byName = new Dictionary<string, WalkedComponent>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var w in walked)
                if (w.Graph != null && w.Graph.Path != null && !w.Graph.Suppressed)
                    byName[w.Graph.Path] = w;

            foreach (var feature in MirrorFeatures(model, log))
            {
                List<string> sources;
                try { sources = SourcePaths(model, feature, log); }
                catch (Exception ex)
                {
                    if (log != null)
                        log("mirror feature " + SafeName(feature) + ": " + ex.Message);
                    continue;
                }
                if (sources.Count == 0) continue;

                var pairs = PairByReflection(SafeName(feature), sources, byName, walked);
                foreach (var p in pairs) graph.MirrorPairs.Add(p);
                if (log != null)
                    log("mirror feature " + SafeName(feature) + ": " + sources.Count
                        + " source(s), " + pairs.Count + " reflected instance(s)");
            }
        }

        // ── Feature discovery ───────────────────────────────────────────────

        /// <summary>Every top-level feature whose definition is mirror
        /// component data. The type name is not the gate (the cast is), so a
        /// SolidWorks version that renames the feature type still works; the
        /// name only narrows which features get a GetDefinition call.</summary>
        private static List<IFeature> MirrorFeatures(IModelDoc2 model, Action<string> log)
        {
            var found = new List<IFeature>();
            try
            {
                for (var f = model.FirstFeature() as IFeature; f != null;
                     f = f.GetNextFeature() as IFeature)
                {
                    string type = null;
                    try { type = f.GetTypeName2(); } catch { }
                    if (type == null
                        || type.IndexOf("Mirror", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    object def = null;
                    try { def = f.GetDefinition(); } catch { }
                    if (def is IMirrorComponentFeatureData) found.Add(f);
                    else if (log != null)
                        log("mirror-like feature " + SafeName(f) + " (" + type
                            + ") has no mirror component data; skipped");
                }
            }
            catch (Exception ex)
            {
                if (log != null) log("mirror feature scan: " + ex.Message);
            }
            return found;
        }

        /// <summary>The instance paths of the components this feature
        /// mirrors, from both alignment modes. Selection properties need
        /// selection access; the release is unconditional.</summary>
        private static List<string> SourcePaths(
            IModelDoc2 model, IFeature feature, Action<string> log)
        {
            var paths = new List<string>();
            var def = feature.GetDefinition() as IMirrorComponentFeatureData;
            if (def == null) return paths;

            bool opened = false;
            try { opened = def.AccessSelections(model, null); }
            catch (Exception ex)
            {
                if (log != null)
                    log("mirror feature " + SafeName(feature)
                        + ": selection access refused (" + ex.Message + ")");
                return paths;
            }
            if (!opened)
            {
                if (log != null)
                    log("mirror feature " + SafeName(feature)
                        + ": selection access refused");
                return paths;
            }

            try
            {
                Collect(def.ComponentsToInstanceAlignToComponentOrigin, paths);
                Collect(def.ComponentsToInstanceAlignToSelection, paths);
            }
            finally
            {
                try { def.ReleaseSelectionAccess(); } catch { }
            }
            return paths;
        }

        private static void Collect(object components, List<string> paths)
        {
            var array = components as object[];
            if (array == null) return;
            foreach (var o in array)
            {
                var c = o as Component2;
                if (c == null) continue;
                string name = null;
                try { name = c.Name2; } catch { }
                if (name != null && !paths.Contains(name)) paths.Add(name);
            }
        }

        // ── Pairing ─────────────────────────────────────────────────────────

        /// <summary>
        /// For each source, every walked occurrence whose placement is an
        /// exact reflection of it. Candidates from one feature must share one
        /// plane, so when several planes appear the largest consistent set
        /// wins and the rest are dropped: a part that happens to sit mirrored
        /// about some OTHER plane is a coincidence, not this feature's work.
        /// </summary>
        private static List<GraphMirrorPair> PairByReflection(
            string featureName, List<string> sources,
            Dictionary<string, WalkedComponent> byName, List<WalkedComponent> walked)
        {
            var candidates = new List<GraphMirrorPair>();
            foreach (string path in sources)
            {
                WalkedComponent src;
                if (!byName.TryGetValue(path, out src)) continue;
                if (src.Graph == null || src.Graph.Transform == null) continue;

                foreach (var other in walked)
                {
                    if (other == src || other.Graph == null
                        || other.Graph.Suppressed || other.Graph.Transform == null)
                        continue;
                    double[] point, normal;
                    if (!MathOps.TryReflectionPlane(
                            src.Graph.Transform, other.Graph.Transform,
                            out point, out normal))
                        continue;
                    candidates.Add(new GraphMirrorPair
                    {
                        FeatureName = featureName,
                        SourceComponentId = src.Graph.Id,
                        MirroredComponentId = other.Graph.Id,
                        PlanePoint = point,
                        PlaneNormal = normal,
                    });
                }
            }
            return LargestCoplanarSet(candidates);
        }

        private static List<GraphMirrorPair> LargestCoplanarSet(
            List<GraphMirrorPair> candidates)
        {
            if (candidates.Count < 2) return candidates;

            List<GraphMirrorPair> best = null;
            foreach (var seed in candidates)
            {
                var set = new List<GraphMirrorPair>();
                foreach (var c in candidates)
                    if (SamePlane(seed, c)) set.Add(c);
                if (best == null || set.Count > best.Count) best = set;
            }
            return best;
        }

        private static bool SamePlane(GraphMirrorPair a, GraphMirrorPair b)
        {
            double dot = MathOps.Dot(a.PlaneNormal, b.PlaneNormal);
            if (Math.Abs(Math.Abs(dot) - 1.0) > PlaneTol) return false;
            // Same plane, either normal sense: the offset of one point from
            // the other along the shared normal must vanish.
            double gap = 0.0;
            for (int i = 0; i < 3; i++)
                gap += (b.PlanePoint[i] - a.PlanePoint[i]) * a.PlaneNormal[i];
            return Math.Abs(gap) <= PlaneTol;
        }

        private static string SafeName(IFeature f)
        {
            try { return f.Name; } catch { return "?"; }
        }
    }
}
