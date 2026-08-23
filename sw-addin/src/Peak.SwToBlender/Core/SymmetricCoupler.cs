using System;
using System.Collections.Generic;
using Peak.SwToBlender.Core.Model;

namespace Peak.SwToBlender.Core
{
    /// <summary>
    /// Three-body symmetric mates as motion couplings. A symmetric mate
    /// whose mirror plane and two mirrored entities live on THREE rigid
    /// groups makes two moving bodies mirror each other about the plane —
    /// a relation the pairwise mate graph cannot hold (corpus 14 sym3).
    /// When each mirrored body hangs on its own 1-DOF mount, the relation
    /// collapses to one number: reflections reverse orientation, so a
    /// rotation by θ about axis a mirrors into −θ about M(a), while a
    /// translation d·a mirrors into d·M(a). Measured about each mount's own
    /// oriented axis that is a gear coupling of ratio −dot(a_driven, M(a_driver))
    /// for revolute pairs and a linear coupler of +dot(a_driven, M(a_driver))
    /// for prismatic pairs — ±1 when the geometry is consistent, which the
    /// synthesis verifies rather than assumes. Anything that fails a gate
    /// falls back to the SYMMETRIC_COUPLING warning: the bodies pose
    /// independently and the user is told.
    /// </summary>
    public static class SymmetricCoupler
    {
        public static List<ManifestWarning> Resolve(
            MateGraph graph, RigidGroupingResult grouping, List<RigJoint> joints)
        {
            var warnings = new List<ManifestWarning>();
            string groundGroup = null;
            foreach (var g in grouping.Groups)
                if (g.Grounded) { groundGroup = g.Id; break; }

            foreach (var m in graph.Mates)
            {
                if (m.Suppressed || !MateFacts.Is(m, "SYMMETRIC")) continue;

                GraphMateEntity plane;
                GraphMateEntity ea, eb;
                if (!SplitEntities(m, grouping, groundGroup, out plane, out ea, out eb))
                    continue;    // two-body symmetric: the resolver already models it

                string ga = GroupOf(ea, grouping);
                string gb = GroupOf(eb, grouping);

                var driver = FindMount(joints, ga);
                var driven = FindMount(joints, gb);
                if (driver != null && driven != null && driven.Coupling != null
                    && driver.Coupling == null)
                {
                    var t = driver; driver = driven; driven = t;
                    var tg = ga; ga = gb; gb = tg;
                }

                string reason = null;
                double ratio = 0.0;
                string kind = null;
                if (driver == null && driven == null && groundGroup != null
                    && !TouchesAnyJoint(joints, ga) && !TouchesAnyJoint(joints, gb))
                {
                    // Neither body is mated to anything else: the symmetric
                    // mate is the WHOLE relation (live corpus 14 sym4,
                    // 2026-08-23 — both slides exported as free islands).
                    // Synthesize the pair: one free joint per body off the
                    // ground, the driven one carrying a full 6-DOF mirror
                    // coupling. Pose either body's mirror-driver bone and
                    // the twin follows exactly, like SolidWorks.
                    SynthesizeMirrorPair(joints, m, plane, groundGroup, ga, gb);
                    continue;
                }
                if (driver == null || driven == null || driver == driven)
                    reason = "each mirrored body needs its own revolute or prismatic mount";
                else if (driven.Coupling != null)
                    reason = "the driven side already carries a coupling";
                else if (driver.Type != driven.Type)
                    reason = "the two mounts are different joint types";
                else
                {
                    var n = MathOps.Normalized(plane.Direction);
                    var mirrored = Mirror(driver.Axis, n);
                    double align = MathOps.Dot(driven.Axis, mirrored);
                    if (Math.Abs(align) < 0.999)
                        reason = "the mount axes are not mirror images about the plane";
                    else
                    {
                        kind = driver.Type == JointType.Prismatic ? "linear_coupler" : "gear";
                        ratio = driver.Type == JointType.Prismatic
                            ? Math.Sign(align)
                            : -Math.Sign(align);
                    }
                }

                if (reason != null)
                {
                    var w = new ManifestWarning();
                    w.Code = "SYMMETRIC_COUPLING";
                    w.Message = "Symmetric mate " + (m.FeatureName ?? "?") + " spans three "
                        + "rigid groups (the mirror plane and two moving bodies), and the "
                        + "mirror relation could not become a coupling: " + reason
                        + ". The two bodies will pose independently in Blender.";
                    warnings.Add(w);
                    continue;
                }

                driven.Coupling = new JointCoupling
                {
                    Kind = kind,
                    DriverJoint = driver.Id,
                    Ratio = ratio,
                };
                driven.SourceMates.Add(new SourceMate
                {
                    SwFeature = m.FeatureName,
                    Type = m.TypeName,
                });
                driven.Notes = AppendNote(driven.Notes,
                    "mirrors " + driver.Id + " about the symmetric mate's plane.");
            }
            return warnings;
        }

        /// <summary>
        /// Sorts a symmetric mate's recorded entities into the mirror plane
        /// and the two mirrored sides, and demands the THREE-body shape:
        /// two mirrored entities on two distinct groups, the plane on a
        /// third (an assembly-level plane counts as the grounded group).
        /// When all three entities are planes with one shared normal, the
        /// mirror is the one sitting midway between the other two — group
        /// membership cannot tell them apart, geometry can.
        /// </summary>
        private static bool SplitEntities(
            GraphMate m, RigidGroupingResult grouping, string groundGroup,
            out GraphMateEntity plane, out GraphMateEntity ea, out GraphMateEntity eb)
        {
            plane = null;
            ea = null;
            eb = null;

            var directed = new List<GraphMateEntity>();
            var points = new List<GraphMateEntity>();
            foreach (var e in m.Entities)
            {
                if (e.Direction != null) directed.Add(e);
                else if (e.Point != null) points.Add(e);
            }

            if (directed.Count == 1 && points.Count == 2)
            {
                plane = directed[0];
                ea = points[0];
                eb = points[1];
            }
            else if (directed.Count == 3 && points.Count == 0)
            {
                // Pick the geometric bisector among three parallel planes.
                var n = MathOps.Normalized(directed[0].Direction);
                foreach (var e in directed)
                    if (!MateFacts.IsParallel(n, e.Direction)) return false;
                int mid = -1;
                for (int k = 0; k < 3 && mid < 0; k++)
                {
                    int i = (k + 1) % 3, j = (k + 2) % 3;
                    if (directed[k].Point == null || directed[i].Point == null
                        || directed[j].Point == null) return false;
                    double dk = MathOps.Dot(n, directed[k].Point);
                    double di = MathOps.Dot(n, directed[i].Point);
                    double dj = MathOps.Dot(n, directed[j].Point);
                    if (Math.Abs(dk - (di + dj) / 2.0) < 1e-6) mid = k;
                }
                if (mid < 0) return false;
                plane = directed[mid];
                ea = directed[(mid + 1) % 3];
                eb = directed[(mid + 2) % 3];
            }
            else
            {
                return false;
            }

            string gp = GroupOf(plane, grouping) ?? groundGroup;
            string ga = GroupOf(ea, grouping);
            string gb = GroupOf(eb, grouping);
            if (ga == null || gb == null || gp == null) return false;
            if (ga == gb || ga == gp || gb == gp) return false;
            return true;
        }

        private static bool TouchesAnyJoint(List<RigJoint> joints, string groupId)
        {
            foreach (var j in joints)
                if (j.ParentGroup == groupId || j.ChildGroup == groupId) return true;
            return false;
        }

        /// <summary>Two ground-rooted free joints, the driven one mirroring
        /// the driver across the mate's plane. The lower group id drives —
        /// deterministic, and the consumer lets the user pose the driver
        /// while the driven bone is locked to its drivers.</summary>
        private static void SynthesizeMirrorPair(
            List<RigJoint> joints, GraphMate m, GraphMateEntity plane,
            string groundGroup, string ga, string gb)
        {
            string driverGid = string.CompareOrdinal(ga, gb) <= 0 ? ga : gb;
            string drivenGid = driverGid == ga ? gb : ga;

            int max = 0;
            foreach (var j in joints)
            {
                int n;
                if (j.Id != null && j.Id.Length > 1 && j.Id[0] == 'j'
                    && int.TryParse(j.Id.Substring(1),
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out n)
                    && n > max)
                    max = n;
            }
            string drvId = "j" + (max + 1).ToString("000",
                System.Globalization.CultureInfo.InvariantCulture);
            string dvnId = "j" + (max + 2).ToString("000",
                System.Globalization.CultureInfo.InvariantCulture);

            var source = new SourceMate { SwFeature = m.FeatureName, Type = m.TypeName };
            var normal = MathOps.Normalized(plane.Direction);

            var jDriver = new RigJoint();
            jDriver.Id = drvId;
            jDriver.Type = JointType.Free;
            jDriver.ParentGroup = groundGroup;
            jDriver.ChildGroup = driverGid;
            jDriver.Confidence = "high";
            jDriver.Notes = "mirror pair: pose this body; " + dvnId
                + " follows as its mirror image.";
            jDriver.SourceMates.Add(source);
            joints.Add(jDriver);

            var jDriven = new RigJoint();
            jDriven.Id = dvnId;
            jDriven.Type = JointType.Free;
            jDriven.ParentGroup = groundGroup;
            jDriven.ChildGroup = drivenGid;
            jDriven.Confidence = "high";
            jDriven.Notes = "mirror pair: poses as the mirror image of " + drvId
                + " about the symmetric mate's plane.";
            jDriven.SourceMates.Add(source);
            jDriven.Coupling = new JointCoupling
            {
                Kind = "mirror",
                DriverJoint = drvId,
                MirrorPlanePoint = MathOps.Threshold(
                    (double[])(plane.Point ?? new double[3]).Clone(), 1e-11),
                MirrorPlaneNormal = MathOps.Threshold(normal, 1e-11),
            };
            joints.Add(jDriven);
        }

        private static RigJoint FindMount(List<RigJoint> joints, string groupId)
        {
            RigJoint best = null;
            int bestScore = int.MaxValue;
            foreach (var j in joints)
            {
                if (j.Type != JointType.Revolute && j.Type != JointType.Prismatic) continue;
                if (j.Axis == null) continue;
                int score;
                if (j.ChildGroup == groupId) score = 0;
                else if (j.ParentGroup == groupId) score = 1;
                else continue;
                if (score < bestScore
                    || (score == bestScore && best != null
                        && string.CompareOrdinal(j.Id, best.Id) < 0))
                {
                    best = j;
                    bestScore = score;
                }
            }
            return best;
        }

        private static string GroupOf(GraphMateEntity e, RigidGroupingResult grouping)
        {
            if (e == null || e.ComponentId == null) return null;
            string g;
            return grouping.ComponentGroup.TryGetValue(e.ComponentId, out g) ? g : null;
        }

        private static double[] Mirror(double[] v, double[] unitNormal)
        {
            double along = MathOps.Dot(v, unitNormal);
            return new[]
            {
                v[0] - 2.0 * along * unitNormal[0],
                v[1] - 2.0 * along * unitNormal[1],
                v[2] - 2.0 * along * unitNormal[2],
            };
        }

        private static string AppendNote(string notes, string add)
        {
            return string.IsNullOrEmpty(notes) ? add : notes + " " + add;
        }
    }
}
