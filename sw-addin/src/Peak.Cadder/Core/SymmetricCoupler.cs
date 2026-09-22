using System;
using System.Collections.Generic;
using Peak.Cadder.Core.Model;

namespace Peak.Cadder.Core
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
    /// synthesis verifies rather than assumes. Both formulas read each body
    /// as its mount's child: a mount found on the parent side flips the
    /// sign. Anything that fails a gate falls back to the SYMMETRIC_COUPLING
    /// warning: the bodies pose independently and the user is told.
    ///
    /// When each mount sits in a closed loop of its own, the loop sets the
    /// mount, and the coupling goes on the two loops' drivers instead
    /// (see MirroredLoopInputs).
    /// </summary>
    public static class SymmetricCoupler
    {
        /// <param name="loops">The loops the joints close, or null. With
        /// them, a mirror between two bodies that each ride a loop of their
        /// own couples the two loops' drivers.</param>
        public static List<ManifestWarning> Resolve(
            MateGraph graph, RigidGroupingResult grouping, List<RigJoint> joints,
            List<RigLoop> loops = null)
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
                string unread;
                if (!SplitEntities(m, grouping, groundGroup,
                        out plane, out ea, out eb, out unread))
                {
                    // Two-body symmetric: the resolver already models it. A
                    // mate on three groups whose mirror could not be read is
                    // a relation the rig loses, so the user is told.
                    if (unread != null) warnings.Add(CouplingWarning(m, unread));
                    continue;
                }

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
                    MirrorScope.Plane, "the symmetric mate's plane", freePairRefusal,
                    loops, GroupOf(plane, grouping) ?? groundGroup);

                if (reason != null) warnings.Add(CouplingWarning(m, reason));
            }
            return warnings;
        }

        private static ManifestWarning CouplingWarning(GraphMate m, string reason)
        {
            var w = new ManifestWarning();
            w.Code = "SYMMETRIC_COUPLING";
            w.Message = "Symmetric mate " + (m.FeatureName ?? "?") + " spans three "
                + "rigid groups (the mirror plane and two moving bodies), and the "
                + "mirror relation could not become a coupling: " + reason
                + ". The two bodies will pose independently in Blender.";
            return w;
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
            string scope, string planeLabel, string freePairRefusal,
            List<RigLoop> loops = null, string planeGroup = null)
        {
            var driver = FindMount(joints, ga);
            var driven = FindMount(joints, gb);
            if (driver != null && driven != null && driven.Coupling != null
                && driver.Coupling == null)
            {
                var t = driver; driver = driven; driven = t;
                var tg = ga; ga = gb; gb = tg;
            }

            RigJoint inputA, inputB;
            string common;
            if (driver != null && driven != null && driver != driven
                && MirroredLoopInputs(loops, joints, driver, driven, planeGroup,
                                      planePoint, planeNormal,
                                      out inputA, out inputB, out common))
            {
                double along = MathOps.Dot(inputB.Axis, Mirror(inputA.Axis, planeNormal));
                double loopRatio = inputA.Type == JointType.Prismatic
                    ? Math.Sign(along)
                    : -Math.Sign(along);
                // Each input measures its moving side against the plane's
                // own body, which the mirror maps onto itself. An input with
                // that body as its CHILD measures the reverse.
                if (inputA.ChildGroup == common) loopRatio = -loopRatio;
                if (inputB.ChildGroup == common) loopRatio = -loopRatio;
                inputB.Coupling = new JointCoupling
                {
                    Kind = inputA.Type == JointType.Prismatic ? "linear_coupler" : "gear",
                    DriverJoint = inputA.Id,
                    Ratio = loopRatio,
                };
                inputB.SourceMates.Add(source);
                inputB.Notes = AppendNote(inputB.Notes,
                    "mirrors " + inputA.Id + " about " + planeLabel + ": the mirrored "
                    + "bodies each ride a loop, and these joints drive the two loops.");
                return null;
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
            // The mirror relates each body's motion against its mount
            // partner, which is what a mount measures only when the body is
            // its CHILD. A mount found on the parent side measures the
            // partner against the body, the reverse, so its sense flips.
            // Live CutterRig (2026-09-21): rod two's slide runs rod to
            // cylinder, the reverse of rod one's. Without the flip, the
            // coupling would pull one rod in as the other came out.
            if (driver.ChildGroup != ga) ratio = -ratio;
            if (driven.ChildGroup != gb) ratio = -ratio;

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
        /// The drivers of the two loops that carry two mirrored mounts.
        ///
        /// A mount inside a loop the rig closes does not hold its body's
        /// motion: the loop sets that joint from its driver, so a coupling
        /// on the mount does nothing. When each mount sits in exactly one
        /// such loop of its own (mobility 1, a closure the rig makes), and
        /// the two loops are mirror images joint for joint, the mirror of
        /// one body is the mirror of its whole loop, and the two drivers
        /// carry it. Both drivers must hang off the plane's own body, which
        /// the mirror maps onto itself.
        ///
        /// Live CutterRig (2026-09-22): one symmetric mate holds the two
        /// ram rods as mirror images. The rods hang on pins inside the two
        /// ram loops, which the two clamp hinges drive, so the coupling on
        /// the rod pins did nothing and the second clamp stood still while
        /// the first one swung.
        /// </summary>
        private static bool MirroredLoopInputs(
            List<RigLoop> loops, List<RigJoint> joints, RigJoint mountA, RigJoint mountB,
            string planeGroup, double[] planePoint, double[] planeNormal,
            out RigJoint inputA, out RigJoint inputB, out string common)
        {
            inputA = null;
            inputB = null;
            common = null;
            if (loops == null || planeGroup == null) return false;
            var loopA = OwnLoop(loops, mountA.Id, mountB.Id);
            var loopB = OwnLoop(loops, mountB.Id, mountA.Id);
            if (loopA == null || loopB == null || loopA == loopB) return false;

            inputA = JointById(joints, loopA.SuggestedDriverJoint);
            inputB = JointById(joints, loopB.SuggestedDriverJoint);
            if (inputA == null || inputB == null || inputA == inputB) return false;
            if (inputA.Coupling != null || inputB.Coupling != null) return false;
            if (inputA.Type != inputB.Type) return false;
            if (inputA.Type != JointType.Revolute && inputA.Type != JointType.Prismatic)
                return false;
            if (!LineMirrors(inputA, inputB, planePoint, planeNormal)) return false;

            if (inputA.ParentGroup == planeGroup || inputA.ChildGroup == planeGroup)
                common = planeGroup;
            if (common == null
                || (inputB.ParentGroup != common && inputB.ChildGroup != common))
                return false;

            // Every joint of one loop has its mirror image in the other.
            if (loopA.MemberJoints.Count != loopB.MemberJoints.Count) return false;
            var used = new HashSet<string>();
            foreach (string idA in loopA.MemberJoints)
            {
                var a = JointById(joints, idA);
                if (a == null) return false;
                RigJoint match = null;
                foreach (string idB in loopB.MemberJoints)
                {
                    if (used.Contains(idB)) continue;
                    var b = JointById(joints, idB);
                    if (b == null || b.Type != a.Type) continue;
                    if (a == inputA && b != inputB) continue;
                    if (!LineMirrors(a, b, planePoint, planeNormal)) continue;
                    match = b;
                    break;
                }
                if (match == null) return false;
                used.Add(match.Id);
            }
            return true;
        }

        /// <summary>The one loop the rig closes that holds `mount` and not
        /// `other`, or null when there is none or more than one.</summary>
        private static RigLoop OwnLoop(List<RigLoop> loops, string mount, string other)
        {
            RigLoop found = null;
            foreach (var lp in loops)
            {
                if (lp.ClosureKind == "none" || lp.Mobility != 1) continue;
                if (!lp.MemberJoints.Contains(mount) || lp.MemberJoints.Contains(other))
                    continue;
                if (found != null) return null;
                found = lp;
            }
            return found;
        }

        /// <summary>
        /// Whether joint `b` sits where the mirror puts joint `a`. A hinge
        /// is a line: the reflected direction is parallel to b's and the
        /// reflected origin lies on b's line. A slide or a planar has a
        /// direction only. A joint with no axis passes on its type alone.
        /// </summary>
        private static bool LineMirrors(
            RigJoint a, RigJoint b, double[] planePoint, double[] planeNormal)
        {
            if (a.Axis == null || b.Axis == null) return a.Axis == null && b.Axis == null;
            if (MathOps.Norm(a.Axis) < 1e-9 || MathOps.Norm(b.Axis) < 1e-9) return false;
            var nb = MathOps.Normalized(b.Axis);
            if (!MateFacts.IsParallel(Mirror(MathOps.Normalized(a.Axis), planeNormal), nb))
                return false;
            if (a.Type != JointType.Revolute && a.Type != JointType.Cylindrical) return true;
            if (a.Origin == null || b.Origin == null) return false;
            double away = MathOps.Dot(Minus(a.Origin, planePoint), planeNormal);
            var pa = new[] { a.Origin[0] - 2.0 * away * planeNormal[0],
                             a.Origin[1] - 2.0 * away * planeNormal[1],
                             a.Origin[2] - 2.0 * away * planeNormal[2] };
            return MateFacts.DistancePointToLine(pa, nb, b.Origin)
                   <= MateFacts.CollinearTol * Math.Max(1.0, MathOps.Norm(b.Origin));
        }

        private static RigJoint JointById(List<RigJoint> joints, string id)
        {
            if (id == null) return null;
            foreach (var j in joints) if (j.Id == id) return j;
            return null;
        }

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

        /// <summary>
        /// The two LINES of a symmetric mate, cylinder axes or datum axes,
        /// and the plane they mirror about. SolidWorks demands a planar
        /// mirror and two mirrored entities of one kind, so with one plane
        /// and two lines the plane is the mirror. No geometry test picks it,
        /// so none can pick a line by mistake.
        /// </summary>
        internal static bool SplitLines(
            List<GraphMateEntity> directed,
            out GraphMateEntity plane, out GraphMateEntity ea, out GraphMateEntity eb)
        {
            plane = null;
            ea = null;
            eb = null;
            foreach (var e in directed)
            {
                if (e.EntityTypeName == "plane")
                {
                    if (plane != null) return false;
                    plane = e;
                }
                else if (e.EntityTypeName == "cylinder" || e.EntityTypeName == "axis")
                {
                    if (ea == null) ea = e;
                    else if (eb == null) eb = e;
                    else return false;
                }
                else return false;
            }
            return plane != null && ea != null && eb != null;
        }

        /// <summary>
        /// Whether reflecting line `a` in plane `m` gives line `b`.
        ///
        /// A line is a direction and ANY point along it, so this is the
        /// line's own test: the reflected direction is parallel to b's, and
        /// the reflected point lies on b. Compared as planes, the points
        /// would have to match along the axis too, and they are wherever
        /// SolidWorks happened to name each cylinder. Live CutterRig
        /// (2026-09-21): one symmetric mate holds the two ram rod-end
        /// cylinders as mirror images about the assembly's Right plane.
        /// Their points sat 35 mm apart along the axes, the plane test
        /// failed, and the mate was dropped with no coupling and no warning,
        /// so the two clamps posed independently.
        /// </summary>
        internal static bool LinesMirror(
            GraphMateEntity m, GraphMateEntity a, GraphMateEntity b)
        {
            if (m.Point == null || m.Direction == null) return false;
            if (a.Point == null || a.Direction == null) return false;
            if (b.Point == null || b.Direction == null) return false;
            if (MathOps.Norm(m.Direction) < 1e-9) return false;
            if (MathOps.Norm(a.Direction) < 1e-9 || MathOps.Norm(b.Direction) < 1e-9)
                return false;

            var n = MathOps.Normalized(m.Direction);
            var ra = Reflect(MathOps.Normalized(a.Direction), n);
            var nb = MathOps.Normalized(b.Direction);
            if (!MateFacts.IsParallel(ra, nb)) return false;

            double away = MathOps.Dot(Minus(a.Point, m.Point), n);
            var pa = new[] { a.Point[0] - 2.0 * away * n[0],
                             a.Point[1] - 2.0 * away * n[1],
                             a.Point[2] - 2.0 * away * n[2] };
            return MateFacts.DistancePointToLine(pa, nb, b.Point)
                   <= MateFacts.CollinearTol * Math.Max(1.0, MathOps.Norm(b.Point));
        }

        /// <summary>True when the mate's entities sit on three distinct
        /// rigid groups (an assembly-level entity counts as the grounded
        /// group). Only this coupler models that shape, so a mate of that
        /// shape it cannot read is warned, never left to the resolver.</summary>
        private static bool SpansThreeGroups(
            GraphMate m, RigidGroupingResult grouping, string groundGroup)
        {
            var groups = new List<string>();
            foreach (var e in m.Entities)
            {
                string g = e.ComponentId == null ? groundGroup : GroupOf(e, grouping);
                if (g == null) return false;    // off the rig: the mate is inert
                if (!groups.Contains(g)) groups.Add(g);
            }
            return groups.Count >= 3;
        }

        /// <summary>
        /// Sorts a symmetric mate's recorded entities into the mirror plane
        /// and the two mirrored sides, and demands the THREE-body shape:
        /// two mirrored entities on two distinct groups, the plane on a
        /// third (an assembly-level plane counts as the grounded group).
        /// When all three entities are planes with one shared normal, the
        /// mirror is the one sitting midway between the other two: group
        /// membership cannot tell them apart, geometry can. When the mate
        /// does span three groups but its entities cannot be sorted,
        /// `unread` says why.
        /// </summary>
        private static bool SplitEntities(
            GraphMate m, RigidGroupingResult grouping, string groundGroup,
            out GraphMateEntity plane, out GraphMateEntity ea, out GraphMateEntity eb,
            out string unread)
        {
            plane = null;
            ea = null;
            eb = null;
            unread = null;

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
            else if (directed.Count == 3 && points.Count == 0
                     && SplitLines(directed, out plane, out ea, out eb))
            {
                if (!LinesMirror(plane, ea, eb))
                {
                    if (SpansThreeGroups(m, grouping, groundGroup))
                        unread = "its two " + MirroredKinds(m, plane)
                            + " entities are not mirror images about its plane";
                    return false;
                }
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
                if (mid < 0)
                {
                    if (SpansThreeGroups(m, grouping, groundGroup))
                        unread = "none of its " + MirroredKinds(m, null)
                            + " entities reflects the other two onto each other";
                    return false;
                }
                plane = directed[mid];
                ea = directed[(mid + 1) % 3];
                eb = directed[(mid + 2) % 3];
            }
            else
            {
                if (SpansThreeGroups(m, grouping, groundGroup))
                    unread = "its " + MirroredKinds(m, null) + " entities are not "
                        + "a mirror plane and two mirrored entities";
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
        /// why this symmetric mate was not modelled. With no plane known,
        /// every entity's kind.</summary>
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
