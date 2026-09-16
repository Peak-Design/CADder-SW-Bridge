using System;
using System.Collections.Generic;
using Peak.SwToBlender.Core.Model;

namespace Peak.SwToBlender.Core
{
    /// <summary>
    /// Three-body symmetric mates as motion couplings. A symmetric mate
    /// whose mirror plane and two mirrored entities live on THREE rigid
    /// groups makes two moving bodies mirror each other about the plane:
    /// a relation the pairwise mate graph cannot hold (corpus 14 sym3).
    /// When each mirrored body hangs on its own 1-DOF mount, the relation
    /// collapses to one number: reflections reverse orientation, so a
    /// rotation by θ about axis a mirrors into −θ about M(a), while a
    /// translation d·a mirrors into d·M(a). Measured about each mount's own
    /// oriented axis that is a gear coupling of ratio −dot(a_driven, M(a_driver))
    /// for revolute pairs and a linear coupler of +dot(a_driven, M(a_driver))
    /// for prismatic pairs: ±1 when the geometry is consistent, which the
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

                // Only PLANAR mirrored entities may become a free-standing
                // mirror pair. The coupling means what a plane-to-plane
                // relation constrains: the translation along the normal and
                // the two tilts, the rest left independent (live corpus 14
                // sym4, 2026-08-24: SolidWorks lets one block be raised
                // without the other). A symmetric mate on points or axes
                // constrains a DIFFERENT set, and reading it as a plane would
                // silently over- or under-constrain.
                string freePairRefusal = BothPlanar(m, plane)
                    ? null
                    : "only symmetric mates between planar faces are modelled; "
                      + "this one mirrors " + MirroredKinds(m, plane) + " entities";

                string reason = TryCouple(
                    joints, groundGroup, ga, gb,
                    plane.Point ?? new double[3], MathOps.Normalized(plane.Direction),
                    new SourceMate { SwFeature = m.FeatureName, Type = m.TypeName },
                    MirrorScope.Plane, "the symmetric mate's plane", freePairRefusal);

                if (reason != null)
                {
                    var w = new ManifestWarning();
                    w.Code = "SYMMETRIC_COUPLING";
                    w.Message = "Symmetric mate " + (m.FeatureName ?? "?") + " spans three "
                        + "rigid groups (the mirror plane and two moving bodies), and the "
                        + "mirror relation could not become a coupling: " + reason
                        + ". The two bodies will pose independently in Blender.";
                    warnings.Add(w);
                }
            }
            return warnings;
        }

        /// <summary>
        /// Couples two bodies that mirror each other about a plane, whatever
        /// declared the relation: a three-body symmetric mate or an assembly
        /// mirror feature. Returns null when a coupling was made, otherwise
        /// the reason it could not be.
        ///
        /// Two shapes work. When each body hangs on its own 1-DOF mount the
        /// relation collapses to one number, because a reflection reverses
        /// orientation: a rotation by θ about axis a mirrors into −θ about
        /// M(a), a translation d·a into d·M(a). When neither body is mated to
        /// anything at all, the mirror IS the whole relation, and the pair
        /// becomes two ground-rooted free joints with the driven one carrying
        /// the reflection.
        /// </summary>
        internal static string TryCouple(
            List<RigJoint> joints, string groundGroup, string ga, string gb,
            double[] planePoint, double[] planeNormal, SourceMate source,
            string scope, string planeLabel, string freePairRefusal)
        {
            var driver = FindMount(joints, ga);
            var driven = FindMount(joints, gb);
            if (driver != null && driven != null && driven.Coupling != null
                && driver.Coupling == null)
            {
                var t = driver; driver = driven; driven = t;
                var tg = ga; ga = gb; gb = tg;
            }

            double ratio = 0.0;
            string kind = null;
            if (driver == null && driven == null && groundGroup != null
                && !TouchesAnyJoint(joints, ga) && !TouchesAnyJoint(joints, gb))
            {
                if (freePairRefusal != null) return freePairRefusal;
                SynthesizeMirrorPair(joints, groundGroup, ga, gb,
                    planePoint, planeNormal, source, scope, planeLabel);
                return null;
            }
            if (driver == null || driven == null || driver == driven)
                return "each mirrored body needs its own revolute or prismatic mount";
            if (driven.Coupling != null)
                return "the driven side already carries a coupling";
            if (driver.Type != driven.Type)
                return "the two mounts are different joint types";

            var mirrored = Mirror(driver.Axis, planeNormal);
            double align = MathOps.Dot(driven.Axis, mirrored);
            if (Math.Abs(align) < 0.999)
                return "the mount axes are not mirror images about the plane";

            kind = driver.Type == JointType.Prismatic ? "linear_coupler" : "gear";
            ratio = driver.Type == JointType.Prismatic
                ? Math.Sign(align)
                : -Math.Sign(align);

            driven.Coupling = new JointCoupling
            {
                Kind = kind,
                DriverJoint = driver.Id,
                Ratio = ratio,
            };
            driven.SourceMates.Add(source);
            driven.Notes = AppendNote(driven.Notes,
                "mirrors " + driver.Id + " about " + planeLabel + ".");
            return null;
        }

        /// <summary>
        /// Sorts a symmetric mate's recorded entities into the mirror plane
        /// and the two mirrored sides, and demands the THREE-body shape:
        /// two mirrored entities on two distinct groups, the plane on a
        /// third (an assembly-level plane counts as the grounded group).
        /// When all three entities are planes with one shared normal, the
        /// mirror is the one sitting midway between the other two: group
        /// membership cannot tell them apart, geometry can.
        /// </summary>
        /// <summary>
        /// Whether reflecting plane `a` in plane `m` gives plane `b`.
        ///
        /// A plane is a unit normal and a signed offset along it, so that is
        /// what is compared: never the entities' points, which are wherever
        /// SolidWorks happened to name and differ between two faces of the
        /// same plane. The reflected normal may come back pointing either
        /// way, so the offset is compared with the matching sign.
        /// </summary>
        private static bool Mirrors(
            GraphMateEntity m, GraphMateEntity a, GraphMateEntity b)
        {
            if (m.Point == null || m.Direction == null) return false;
            if (a.Point == null || a.Direction == null) return false;
            if (b.Point == null || b.Direction == null) return false;
            if (MathOps.Norm(m.Direction) < 1e-9) return false;

            var n = MathOps.Normalized(m.Direction);
            var ra = Reflect(MathOps.Normalized(a.Direction), n);
            var nb = MathOps.Normalized(b.Direction);
            if (!MateFacts.IsParallel(ra, nb)) return false;

            // A point of `a` carried through the mirror, then read as an
            // offset along the reflected normal.
            double away = MathOps.Dot(Minus(a.Point, m.Point), n);
            var pa = new[] { a.Point[0] - 2.0 * away * n[0],
                             a.Point[1] - 2.0 * away * n[1],
                             a.Point[2] - 2.0 * away * n[2] };
            double da = MathOps.Dot(ra, pa);
            double db = MathOps.Dot(nb, b.Point);
            if (MathOps.Dot(ra, nb) < 0.0) da = -da;

            return Math.Abs(da - db)
                   <= MateFacts.CollinearTol * Math.Max(1.0, Math.Abs(db));
        }

        private static double[] Reflect(double[] v, double[] n)
        {
            double d = MathOps.Dot(v, n);
            return new[] { v[0] - 2.0 * d * n[0],
                           v[1] - 2.0 * d * n[1],
                           v[2] - 2.0 * d * n[2] };
        }

        private static double[] Minus(double[] p, double[] q)
        {
            return new[] { p[0] - q[0], p[1] - q[1], p[2] - q[2] };
        }

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
                // The mirror is the plane that REFLECTS the other two
                // onto each other. Mirrored planes need not be parallel to it
                // (only to each other's reflection in it) and on a real
                // machine they are not, because the mirrored entities RIDE
                // THE MOVING BODIES: they line up with the mirror only when
                // the mechanism happens to sit at its symmetric zero, and a
                // saved assembly sits wherever it was left.
                //
                // Live ClampRig (2026-08-25): one symmetric mate opens
                // and closes the two clamps together, and their planes are
                // tilted 0.0822 degrees out of the machine's centre plane,
                // a thousand times the parallel tolerance, and the very angle
                // the clamps are open by. Read as three parallel planes, that
                // mate was dropped with no coupling and no warning, and the
                // clamps posed independently.
                int mid = -1;
                for (int k = 0; k < 3 && mid < 0; k++)
                    if (Mirrors(directed[k], directed[(k + 1) % 3],
                                directed[(k + 2) % 3])) mid = k;
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
        /// the driver across the mate's plane. The lower group id drives:
        /// deterministic, and the consumer lets the user pose the driver
        /// while the driven bone is locked to its drivers.</summary>
        /// <summary>True when both mirrored entities are planar faces: the
        /// only shape the mirror coupling's semantics describe.</summary>
        private static bool BothPlanar(GraphMate m, GraphMateEntity plane)
        {
            int planar = 0, mirrored = 0;
            foreach (var e in m.Entities)
            {
                if (e == plane) continue;
                mirrored++;
                if (e.EntityTypeName == "plane") planar++;
            }
            return mirrored >= 2 && planar == mirrored;
        }

        /// <summary>The mirrored entities' kinds, for the warning that says
        /// why this symmetric mate was not modelled.</summary>
        private static string MirroredKinds(GraphMate m, GraphMateEntity plane)
        {
            var kinds = new List<string>();
            foreach (var e in m.Entities)
            {
                if (e == plane) continue;
                string kind = e.EntityTypeName ?? "unknown";
                if (!kinds.Contains(kind)) kinds.Add(kind);
            }
            kinds.Sort(StringComparer.Ordinal);
            return kinds.Count == 0 ? "unknown" : string.Join("/", kinds.ToArray());
        }

        private static void SynthesizeMirrorPair(
            List<RigJoint> joints, string groundGroup, string ga, string gb,
            double[] planePoint, double[] planeNormal, SourceMate source,
            string scope, string planeLabel)
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
                + " about " + planeLabel + ".";
            jDriven.SourceMates.Add(source);
            jDriven.Coupling = new JointCoupling
            {
                Kind = "mirror",
                DriverJoint = drvId,
                MirrorScope = scope,
                MirrorPlanePoint = MathOps.Threshold(
                    (double[])planePoint.Clone(), 1e-11),
                MirrorPlaneNormal = MathOps.Threshold(planeNormal, 1e-11),
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
