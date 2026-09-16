using System.Collections.Generic;
using Peak.Cadder.Core.Model;

namespace Peak.Cadder.Core
{
    /// <summary>How much of the pose a mirror coupling carries.</summary>
    public static class MirrorScope
    {
        /// <summary>A symmetric MATE between two planar faces: a
        /// plane-to-plane relation, three coupled freedoms, three
        /// independent.</summary>
        public const string Plane = "plane";

        /// <summary>An assembly MIRROR FEATURE: the instance is a full
        /// reflection of its source, so every freedom follows.</summary>
        public const string Rigid = "rigid";
    }

    /// <summary>
    /// Assembly mirror features as motion couplings. A mirrored instance
    /// carries no mate to its source, but SolidWorks keeps it a reflection:
    /// move the source and the mirror follows. Nothing in the mate graph
    /// records that, so without this the two halves of every mirrored
    /// mechanism pose independently.
    ///
    /// The relation is the same one a three-body symmetric mate declares, so
    /// the synthesis is shared with SymmetricCoupler: only the SCOPE differs.
    /// A mirror feature reflects the whole placement, position and
    /// orientation, whether it built an opposite-hand part or merely
    /// repositioned the original; the manifest carries kinematics and not
    /// geometry, so those two cases are one case here.
    ///
    /// Components SolidWorks reports fully defined never reach this code:
    /// RigidGrouper has already welded them to ground, so their group is
    /// grounded and the pair is skipped. That is Oscar's rule: do not
    /// constrain what is already pinned, falling out of the grounding pass
    /// rather than being restated here.
    /// </summary>
    public static class MirrorFeatureCoupler
    {
        public static List<ManifestWarning> Resolve(
            MateGraph graph, RigidGroupingResult grouping, List<RigJoint> joints)
        {
            var warnings = new List<ManifestWarning>();
            if (graph.MirrorPairs.Count == 0) return warnings;

            string groundGroup = null;
            var grounded = new HashSet<string>();
            foreach (var g in grouping.Groups)
            {
                if (!g.Grounded) continue;
                grounded.Add(g.Id);
                if (groundGroup == null) groundGroup = g.Id;
            }

            foreach (var pair in graph.MirrorPairs)
            {
                if (pair.PlaneNormal == null) continue;
                string ga, gb;
                if (!grouping.ComponentGroup.TryGetValue(
                        pair.SourceComponentId ?? "", out ga)
                    || !grouping.ComponentGroup.TryGetValue(
                        pair.MirroredComponentId ?? "", out gb))
                    continue;    // suppressed, or inside a rigid subassembly

                // Already immobile, or already the same body: nothing to
                // couple, and no warning either. This is the ordinary case
                // in a production assembly, where most mirrored parts are
                // fully mated.
                if (ga == gb || grounded.Contains(ga) || grounded.Contains(gb))
                    continue;

                string reason = SymmetricCoupler.TryCouple(
                    joints, groundGroup, ga, gb,
                    pair.PlanePoint ?? new double[3],
                    MathOps.Normalized(pair.PlaneNormal),
                    new SourceMate { SwFeature = pair.FeatureName, Type = "MirrorComponent" },
                    MirrorScope.Rigid, "the mirror feature's plane",
                    freePairRefusal: null);
                if (reason == null) continue;

                var w = new ManifestWarning();
                w.Code = "MIRROR_COUPLING";
                w.Message = "Mirror feature " + (pair.FeatureName ?? "?")
                    + " mirrors two bodies that can still move, and the relation "
                    + "could not become a coupling: " + reason
                    + ". The two bodies will pose independently in Blender.";
                warnings.Add(w);
            }
            return warnings;
        }
    }
}
