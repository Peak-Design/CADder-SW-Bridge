using System;
using System.Collections.Generic;
using Peak.Cadder.Core.Model;

namespace Peak.Cadder.Core
{
    public sealed class LoopAnalysisResult
    {
        public List<RigLoop> Loops = new List<RigLoop>();

        /// <summary>Loops grouped by shared joints, each with every input
        /// it can take as a complete alternative (see RigMechanism).</summary>
        public List<RigMechanism> Mechanisms = new List<RigMechanism>();

        /// <summary>Why an input a loop listed is not offered on its
        /// mechanism. For the log, never serialised.</summary>
        public List<string> Notes = new List<string>();

        /// <summary>Joints whose rotation limit was DERIVED from a
        /// slider-crank's stroke (DeriveSliderDriverLimits), not read from
        /// a mate. It belongs to the configuration that made the joint the
        /// driver, so another input's option re-derives instead of
        /// inheriting it. Never serialised.</summary>
        public HashSet<string> DerivedLimitJoints = new HashSet<string>();

        /// <summary>The input joints, same instances and order, with
        /// ParentGroup/ChildGroup oriented so the parent is nearer the
        /// grounded root. The Blender side parents bones exactly as given.</summary>
        public List<RigJoint> Joints = new List<RigJoint>();

        /// <summary>Free joints whose rotation lock became a gear coupling on
        /// the tree joints it constrains: their UNDER_DEFINED warning is
        /// obsolete: the mate IS modelled.</summary>
        public List<string> CoupledFreeJointIds = new List<string>();

        /// <summary>Free joints whose constraint is already enforced by a
        /// declared loop (redundant mates over a closed ring), harmless, and
        /// their warning should say so instead of crying under-defined.</summary>
        public List<string> RedundantFreeJointIds = new List<string>();
    }

    /// <summary>
    /// Bone hierarchies cannot hold cycles, so every non-tree edge of the
    /// joint graph becomes a recorded loop with a chosen cut. The consumer
    /// parents along the tree and closes each loop at the cut with IK; it must
    /// not re-derive any of this.
    /// </summary>
    public static class LoopAnalyzer
    {
        public static LoopAnalysisResult Analyze(IList<RigidGroup> groups, IList<RigJoint> joints)
        {
            // The closures are chosen from the joint types, and the
            // narrowing that follows (CutTransfer) can change those types:
            // what the rest of a ring cannot reproduce, a joint loses. When
            // that takes a chosen DRIVER's freedom away, the choice was made
            // on a reading SolidWorks does not permit, so it is made again
            // with the narrowed types. Live weldingrobot.sldasm (2026-09-15):
            // the four-bar's driver was the support's prismatic, read
            // pairwise; the ring narrowed it to fixed, and the robot arm
            // shipped with no control at all. Narrowing only ever removes
            // freedom, so the rounds are bounded by the freedoms there are.
            LoopAnalysisResult result = null;
            int budget = 3 * joints.Count + 1;
            for (int round = 0; round < budget; round++)
            {
                result = Choose(groups, joints);
                var before = new List<string>();
                foreach (var j in joints) before.Add(j.Type);
                // A cut the consumer will not solve would otherwise throw
                // its constraint away; this puts it back on the ring's tree
                // joints as freedom removed. Before the limits, because a
                // joint that loses its slide loses its stroke with it.
                CutTransfer.Apply(result);
                bool changed = false;
                for (int i = 0; i < joints.Count && !changed; i++)
                    changed = joints[i].Type != before[i];
                if (!changed) break;
            }
            DeriveSliderDriverLimits(result);
            result.Mechanisms = ComputeMechanisms(groups, joints, result);
            MoveCutsOffWelds(result);
            SeatClosureOrigins(result);
            AddCouplingMechanisms(result);
            DropStrayCandidates(result);
            return result;
        }

        /// <summary>
        /// Drops a driver candidate that names a joint outside its own loop,
        /// with a note. The consumer refuses a whole manifest over one such
        /// candidate, and a candidate is only an offer: the loop's own
        /// driver and closure are untouched.
        /// </summary>
        private static void DropStrayCandidates(LoopAnalysisResult result)
        {
            var seen = new HashSet<RigLoop>();
            var all = new List<RigLoop>(result.Loops);
            foreach (var mech in result.Mechanisms)
                foreach (var option in mech.Inputs) all.AddRange(option.Loops);
            foreach (var loop in all)
            {
                if (!seen.Add(loop)) continue;
                for (int i = loop.DriverCandidates.Count - 1; i >= 0; i--)
                {
                    var c = loop.DriverCandidates[i];
                    if (loop.MemberJoints.Contains(c.DriverJoint)
                        && loop.MemberJoints.Contains(c.ClosureJoint)) continue;
                    result.Notes.Add(loop.Id + ": candidate " + c.DriverJoint + " cut at "
                        + c.ClosureJoint + " dropped, it is not a member of the loop");
                    loop.DriverCandidates.RemoveAt(i);
                }
            }
        }

        /// <summary>One pass of closure selection, orientation and seating
        /// from the joint types as they stand. <paramref name="seedDrivers"/>
        /// count as already chosen before the first loop is met, which is
        /// how a mechanism is re-chosen from another input.</summary>
        private static LoopAnalysisResult Choose(
            IList<RigidGroup> groups, IList<RigJoint> joints, ISet<string> seedDrivers = null,
            bool explore = true)
        {
            var result = new LoopAnalysisResult();
            foreach (var j in joints) result.Joints.Add(j);

            var groupIndex = new Dictionary<string, int>();
            for (int i = 0; i < groups.Count; i++) groupIndex[groups[i].Id] = i;

            var adjacency = new List<RigJoint>[groups.Count];
            for (int i = 0; i < adjacency.Length; i++) adjacency[i] = new List<RigJoint>();
            var usable = new List<RigJoint>(joints.Count);
            foreach (var j in joints)
            {
                // Free joints are under-mated pairs the consumer never
                // parents (SCHEMA.md), so they are not graph edges here
                // either. Counting them made the exporter's spanning tree
                // disagree with the consumer's: live corpus 06
                // parallelogram2 (2026-08-22): a redundant parallel mate
                // exported free but held a tree slot, every real ring joint
                // became a loop closure, and the Blender side rightly refused
                // the manifest as disconnected.
                if (j.Type == JointType.Free) continue;
                int a, b;
                if (!groupIndex.TryGetValue(j.ParentGroup, out a)) continue;
                if (!groupIndex.TryGetValue(j.ChildGroup, out b)) continue;
                if (a == b) continue;
                adjacency[a].Add(j);
                adjacency[b].Add(j);
                usable.Add(j);
            }
            foreach (var list in adjacency)
                list.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));

            // The first grounded group roots the main tree; further grounded
            // groups and stranded islands root their own trees so every joint
            // still gets an orientation.
            var roots = new List<int>();
            for (int i = 0; i < groups.Count; i++) if (groups[i].Grounded) roots.Add(i);
            for (int i = 0; i < groups.Count; i++) if (!groups[i].Grounded) roots.Add(i);

            var tree0 = new HashSet<string>();
            Bfs(groups.Count, adjacency, groupIndex, roots, null, tree0);
            var tree = new HashSet<string>(tree0);
            var loops = SelectLoops(groups, usable, adjacency, groupIndex, roots, seedDrivers, tree);
            if (explore)
            {
                // The configuration with the FEWEST controls wins. A loop
                // met before the one that moves its body takes an input of
                // its own, and the mechanism ships with more controls than
                // it has degrees of freedom: live plunger.sldasm
                // (2026-09-15) met its three link-and-nut rings before the
                // slider ring, drove them from the link's pin, and the rig
                // had three controls where one degree of freedom moves
                // everything. Re-chosen from the slider, the same rings
                // drive from the pin the slider's chain solves, one
                // control. Every input the loops name is tried; the
                // choice above stands unless one of them does better.
                var inputs = new List<string>();
                foreach (var lp in loops)
                    foreach (var c in lp.DriverCandidates)
                        if (!inputs.Contains(c.DriverJoint)) inputs.Add(c.DriverJoint);
                int best = Controls(loops).Count;
                foreach (var alt in inputs)
                {
                    var altTree = new HashSet<string>(tree0);
                    var altLoops = SelectLoops(groups, usable, adjacency, groupIndex, roots,
                                               new HashSet<string> { alt }, altTree);
                    int n = Controls(altLoops).Count;
                    if (n < best)
                    {
                        best = n;
                        loops = altLoops;
                        tree = altTree;
                    }
                }
            }
            result.Loops.AddRange(loops);

            RefreshLoopMembers(result, groups.Count, adjacency, groupIndex, roots, tree);
            OrderLoops(result.Loops);
            Orient(result.Joints, groups.Count, adjacency, groupIndex, roots, tree);
            SynthesizeParallelCouplings(
                result, groups.Count, adjacency, groupIndex, roots, tree);
            // Both halves of an aim pair have to sit ON the ram they close,
            // and the origin SolidWorks hands back is only one arbitrary
            // point of the pin. Before everything downstream, so the pivots
            // the limits and the consumer read are the seated ones.
            SeatAimPairMounts(result);
            return result;
        }

        /// <summary>The loops of one configuration: every non-tree edge
        /// of <paramref name="tree"/> met in id order, rams first, its
        /// ring read off the tree as it stands, its driver and cut chosen,
        /// the cut swapped into the tree. <paramref name="tree"/> comes in
        /// as the spanning tree to start from and goes out as the final
        /// one.</summary>
        private static List<RigLoop> SelectLoops(
            IList<RigidGroup> groups, List<RigJoint> usable, List<RigJoint>[] adjacency,
            Dictionary<string, int> groupIndex, List<int> roots, ISet<string> seedDrivers,
            HashSet<string> tree)
        {
            var loops = new List<RigLoop>();
            Walk walk = null;

            // Rounds to a fixed point of the tree. A loop's ring is read
            // off the tree as it stands when the loop is met, and a later
            // loop's cut swap changes that tree, so an earlier loop's
            // driver and cut could end up off its own ring (live
            // plunger.sldasm re-chosen from the arm's hinge, 2026-09-15:
            // the link loop's driver was no longer a member). Choosing
            // again from the swapped tree, until no loop swaps, leaves
            // every ring, driver, cut and member list read off the one
            // final tree.
            int rounds = usable.Count + 2;
            for (int round = 0; round < rounds; round++)
            {
            var startTree = new HashSet<string>(tree);
            loops.Clear();
            var nonTree = new List<RigJoint>();
            foreach (var j in usable)
                if (!tree.Contains(j.Id)) nonTree.Add(j);
            nonTree.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));

            int loopNumber = 1;
            // Drivers already chosen for earlier loops. A mechanism with one
            // degree of freedom has loops that share joints, and every one of
            // them must be driven from the SAME input: two loops that each
            // make the other's driver a driven bone are a dependency cycle
            // the consumer refuses (live TongRig, 2026-09-14: two link
            // loops said "drive arm A", the ram loop said "drive arm B").
            var chosenDrivers = seedDrivers == null
                ? new HashSet<string>() : new HashSet<string>(seedDrivers);
            // The seed is the mechanism's INPUT and outranks a driver an
            // earlier loop chose for itself: a ram loop always names the
            // pin it works from, and that pin must not win the tie against
            // the input the user asked for on the loops that could take it
            // (actuator.sldasm, 2026-09-15: seeded with the plunger's slide,
            // every loop still fell to the ram's pin).
            var seed = seedDrivers == null ? null : new HashSet<string>(seedDrivers);
            // Two passes, aimed rams first. Which loop is met first decides
            // the mechanism's driver, and the candidates come in id order,
            // which is component walk order: the same assembly re-exported
            // with its components in another order had its slider loop
            // met before its ram loop, took the slide as the input, and the
            // ram loop then chose the crank pin, two loops driving against
            // each other and a dependency cycle in Blender (SolidWorks 2022
            // sample actuator.sldasm, 2026-09-14). A ram can only be driven
            // from the pin it works on; a slider-crank closes from either
            // end. So the rams decide, in any order, and every other loop
            // follows them.
            var done = new HashSet<string>();
            for (int pass = 0; pass < 2; pass++)
            foreach (var closureCandidate in nonTree)
            {
                if (done.Contains(closureCandidate.Id)) continue;
                // A previous swap may have pulled this edge into the tree; it
                // was that loop's business, not a loop of its own.
                if (tree.Contains(closureCandidate.Id)) continue;

                walk = Bfs(groups.Count, adjacency, groupIndex, roots, tree, null);
                var ring = CycleRing(closureCandidate, walk, groupIndex);
                if (ring == null) continue;

                // The consumer's IK solves ONLY the driven side of the cut, so
                // every loop joint except the driver's own edge must land on
                // that side: the cut goes just past the driver's moving group.
                // The driver itself must be an edge at the ring's anchor:
                // posing anything deeper drags IK-solved groups along. Found
                // live on corpus 06 (2026-08-22): the old "first revolute
                // member" cut removed the crank's own edge, the driver side
                // came out empty and the four-bar froze solid.
                RigJoint cut, driver;
                bool aimPair = false;
                bool chained = false;
                bool holdsNothing = false;
                RigJoint redundant = null;
                int n = ring.Edges.Count;
                int slide = SoleSlideIndex(ring);
                RigJoint slideDriver = slide < 0 ? null
                    : DriverClearOfSlide(ring, slide, chosenDrivers);
                // Both halves of an aimed ram hang off a mount pin of their
                // own, so a half that is a tree ROOT (the ground) cannot be
                // one: it hangs off nothing. The SolidWorks 2022 sample
                // landing_gear.sldasm (2026-09-14) slides its piston in the
                // oleo strut, which is the ground; read as a ram, the Blender
                // side refused the aim, fell back to IK with the sides
                // swapped, and every bone ended driven with no control. A
                // slide on the ground is a slider-crank: the slide stays in
                // the tree and the ring closes with IK like any other.
                if (slide >= 0 && slideDriver != null
                    && walk.ParentGroup[ring.Groups[slide]] < 0)
                {
                    slideDriver = null;
                }
                if (slide >= 0 && slideDriver != null
                    && walk.ParentGroup[ring.Groups[(slide + 1) % n]] < 0)
                {
                    slideDriver = null;
                }
                // The first pass takes only the rams (see chosenDrivers).
                if (pass == 0 && !(slide >= 0 && slideDriver != null)) continue;
                done.Add(closureCandidate.Id);
                // Every input this ring could take, for the consumer to
                // offer: the choice made below goes first.
                var candidates = new List<RigLoopCandidate>();
                if (slide >= 0 && slideDriver != null)
                {
                    // Slider-crank. Cut the SLIDE: no rotational solver can
                    // change a sliding joint, so leaving it in the solved
                    // chain freezes the mechanism, and cutting a pin instead
                    // leaves the slide locked inside the tree with the same
                    // result. Cut here and both halves of the ram hang off
                    // their own pins, each free to aim at the other: the way
                    // a ram is rigged by hand. The driver is the pin that
                    // touches neither sliding body: posing it moves the two
                    // pin points apart and the ram follows.
                    cut = ring.Edges[slide];
                    driver = slideDriver;
                    aimPair = true;
                    candidates.Add(new RigLoopCandidate(driver.Id, cut.Id, "aim_pair"));
                }
                else if (n > 2 && IsSlide(ring.Edges[0]) && IsSlide(ring.Edges[n - 1]))
                {
                    // Both edges at the anchor are SLIDES: two bodies each
                    // sliding on the same ground, tied to each other. Cutting
                    // between them leaves them siblings, and no solver can
                    // then make one carry the other: a rotational solve has
                    // nothing to rotate. Cut one of the anchor edges instead
                    // and the loop becomes a chain: pose the driver and the
                    // far body simply comes along, which is what the mates
                    // said all along.
                    //
                    // Live ClampRig (2026-08-24): the cutting head and
                    // the lead screw rod both slide along the machine, and
                    // the head is mated to the rod. Cut between them and both
                    // hung off ground as siblings, so driving the lead screw
                    // left the cutting head behind.
                    if (PreferFirst(ring.Edges[0], ring.Edges[n - 1],
                                    ring.Edges[n - 1], ring.Edges[0], chosenDrivers, seed))
                    {
                        driver = ring.Edges[0];
                        cut = ring.Edges[n - 1];
                    }
                    else
                    {
                        driver = ring.Edges[n - 1];
                        cut = ring.Edges[0];
                    }
                    chained = true;
                    candidates.Add(new RigLoopCandidate(driver.Id, cut.Id, "none"));
                    candidates.Add(new RigLoopCandidate(cut.Id, driver.Id, "none"));
                }
                else if (n == 2)
                {
                    // Two joints between one pair of groups: no second moving
                    // body, so no driver side exists: the closure only
                    // re-constrains the same pair. The non-tree edge stays cut.
                    cut = closureCandidate;
                    driver = ReferenceEquals(ring.Edges[0], closureCandidate)
                        ? ring.Edges[1] : ring.Edges[0];
                    candidates.Add(new RigLoopCandidate(
                        driver.Id, cut.Id, CutSharesAPoint(cut) ? "ik" : "none"));
                }
                else if (n > 2 && IsSlide(ring.Edges[0]) != IsSlide(ring.Edges[n - 1])
                         && !chosenDrivers.Contains(
                             (IsSlide(ring.Edges[0]) ? ring.Edges[n - 1] : ring.Edges[0]).Id)
                         // ... unless a coupling writes the slide: a cam's
                         // follower cannot drive the cam (live cam-follower,
                         // 2026-09-15), so the choice below decides.
                         && !Driven(IsSlide(ring.Edges[0]) ? ring.Edges[0] : ring.Edges[n - 1]))
                {
                    // ONE anchor edge is a slide that could not be aimed (its
                    // ground half hangs off nothing). It drives: the ring's
                    // other side then closes with rotations only, which is
                    // all a rotational solver can do. Preferred the other
                    // way round, the slide sits inside the solved chain and
                    // freezes it (SolidWorks 2022 sample landing_gear.sldasm,
                    // 2026-09-14: the sway link pin drove, the links swung,
                    // and the piston never moved).
                    if (IsSlide(ring.Edges[0]))
                    {
                        driver = ring.Edges[0];
                        cut = ring.Edges[1];
                    }
                    else
                    {
                        driver = ring.Edges[n - 1];
                        cut = ring.Edges[n - 2];
                    }
                }
                else if (n > 2 && (redundant = RedundantPlanar(ring.Edges)) != null)
                {
                    // A planar joint whose normal every hinge of the ring
                    // shares holds nothing: every body of the ring already
                    // moves in that plane. It is the cut, and the ring needs
                    // no closure at all. Cut anywhere else, it takes a real
                    // joint's place in the tree, and every ring met after
                    // that runs through it (live CutterRig, 2026-09-21: two
                    // rams' rods held coplanar by a mate between them; the
                    // rod pin was cut instead, one rod came to hang off the
                    // other, and each ram's loop ran through the other ram).
                    cut = redundant;
                    RigJoint first = ring.Edges[0], last = ring.Edges[n - 1];
                    if (ReferenceEquals(first, redundant)) driver = last;
                    else if (ReferenceEquals(last, redundant)) driver = first;
                    else driver = PreferFirst(first, ring.Edges[1], last, ring.Edges[n - 2],
                                              chosenDrivers, seed) ? first : last;
                    holdsNothing = true;
                    candidates.Add(new RigLoopCandidate(driver.Id, cut.Id, "none"));
                }
                else if (PreferFirst(ring.Edges[0], ring.Edges[1],
                                     ring.Edges[n - 1], ring.Edges[n - 2], chosenDrivers, seed))
                {
                    driver = ring.Edges[0];
                    cut = ring.Edges[1];
                }
                else
                {
                    driver = ring.Edges[n - 1];
                    cut = ring.Edges[n - 2];
                }
                if (candidates.Count == 0)
                {
                    // An anchor edge drives, its neighbour is the cut: the
                    // choice above and the other anchor's, in that order.
                    RigJoint otherDriver = ReferenceEquals(driver, ring.Edges[0])
                        ? ring.Edges[n - 1] : ring.Edges[0];
                    RigJoint otherCut = ReferenceEquals(driver, ring.Edges[0])
                        ? ring.Edges[n - 2] : ring.Edges[1];
                    candidates.Add(new RigLoopCandidate(
                        driver.Id, cut.Id, CutSharesAPoint(cut) ? "ik" : "none"));
                    if (!ReferenceEquals(otherDriver, driver))
                        candidates.Add(new RigLoopCandidate(
                            otherDriver.Id, otherCut.Id,
                            CutSharesAPoint(otherCut) ? "ik" : "none"));
                }
                if (!ReferenceEquals(cut, closureCandidate))
                {
                    tree.Remove(cut.Id);
                    tree.Add(closureCandidate.Id);
                }

                var loop = new RigLoop();
                loop.Id = "loop" + loopNumber.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
                loopNumber++;
                var members = new List<RigJoint>(ring.Edges);
                members.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));
                foreach (var j in members) loop.MemberJoints.Add(j.Id);
                loop.ClosureJoint = cut.Id;
                // An IK closure re-joins a cut by making one POINT meet
                // again, so a cut whose two bodies share no point is a
                // closure the consumer cannot honour: solving it would drag
                // a body to pin a face against a face. Say so rather than
                // shipping a solve that fights the rest of the rig.
                //
                // Live ClampRig (2026-08-24): the two hydraulic rams
                // are held level by a coincidence between their subassembly
                // mid-planes, which reads as a planar joint BETWEEN the two
                // barrels. Cut there and IK-closed, it pulled one barrel off
                // the Damped Track that aims it at its own rod, and that ram
                // stopped following its clamp.
                loop.ClosureKind = aimPair ? "aim_pair"
                    : chained || holdsNothing || !CutSharesAPoint(cut) ? "none" : "ik";
                loop.SuggestedDriverJoint = driver.Id;
                loop.DriverCandidates = candidates;
                // A weld drives nothing, so it has no claim on the loops met
                // after it. Only a ring of welds names one, because it has
                // nothing else to name. Live CutterRig (2026-09-22): a ring
                // of three welds in the lead screw was met first, its weld
                // then won every later loop of that mechanism, and the
                // cutting head's slide was never the input.
                if (driver.Type != JointType.Fixed) chosenDrivers.Add(driver.Id);
                SetPlanarity(loop, members);
                loop.Mobility = Mobility(loop, members);
                // The ring's groups and the driver's moving body, for the
                // control rule in ComputeMechanisms. Never serialised.
                loop.RingGroups = new List<string>();
                foreach (int g in ring.Groups) loop.RingGroups.Add(groups[g].Id);
                loop.AnchorGroup = groups[ring.Groups[0]].Id;
                int dp = groupIndex[driver.ParentGroup], dc = groupIndex[driver.ChildGroup];
                loop.DriverChildGroup = walk.Depth[dc] > walk.Depth[dp]
                    ? driver.ChildGroup : driver.ParentGroup;
                loops.Add(loop);
            }
            if (tree.SetEquals(startTree)) break;
            }
            return loops;
        }

        /// <summary>
        /// Puts every closure's origin where its own bodies cannot carry
        /// it away.
        ///
        /// A rotation axis is a LINE, so any point on it does for the joint
        /// itself, and the classifier slides the origin along it to sit
        /// inside the child part. That is cosmetic until the joint becomes
        /// a loop's CLOSURE: the consumer re-joins the two bodies AT the
        /// origin, so the origin has to be a point that stands still when
        /// each body moves on its own joint. A point off the parent's own
        /// rotation axis does not: it orbits it.
        ///
        /// Live wrench.sldasm (2026-09-16, Oscar): "everything rotates with
        /// the screw but it shouldn't". The centerlink rests on a point on
        /// the end of the screw, which is ON the screw's axis and stands
        /// still while the screw turns. The origin had been slid 8.5 mm
        /// down the centerlink's own edge, clear of that axis, so the
        /// closure point swung round the screw once per turn and took the
        /// whole linkage with it. Half a turn and the centerlink was 12 mm
        /// adrift of the screw it is supposed to rest on.
        ///
        /// So the origin slides back along its OWN axis to the point
        /// nearest the body's other axis. An axis parallel to the closure's
        /// own cannot move a point on it, so it asks nothing. Two bodies
        /// that ask for different points get neither: this only ever makes
        /// a closure stand still, never guesses.
        /// </summary>
        private static void SeatClosureOrigins(LoopAnalysisResult result)
        {
            if (result == null || result.Loops.Count == 0) return;

            var byId = new Dictionary<string, RigJoint>();
            foreach (var j in result.Joints) byId[j.Id] = j;

            var cuts = new HashSet<string>();
            foreach (var lp in result.Loops)
                if (lp.ClosureKind == "ik") cuts.Add(lp.ClosureJoint);
            foreach (var mech in result.Mechanisms)
                foreach (var option in mech.Inputs)
                    foreach (var lp in option.Loops)
                        if (lp.ClosureKind == "ik") cuts.Add(lp.ClosureJoint);

            foreach (string id in cuts)
            {
                RigJoint cut;
                if (!byId.TryGetValue(id, out cut)) continue;
                if (cut.Axis == null || cut.Origin == null) continue;

                double[] seat = null;
                bool disagree = false;
                foreach (string group in new[] { cut.ParentGroup, cut.ChildGroup })
                {
                    foreach (var other in result.Joints)
                    {
                        if (ReferenceEquals(other, cut)) continue;
                        if (other.ParentGroup != group && other.ChildGroup != group) continue;
                        if (other.Axis == null || other.Origin == null) continue;
                        if (other.Type != JointType.Revolute
                            && other.Type != JointType.Cylindrical
                            && other.Type != JointType.Screw) continue;
                        var found = NearestOnAxis(cut.Origin, cut.Axis,
                                                  other.Origin, other.Axis);
                        if (found == null) continue;
                        if (seat == null) seat = found;
                        else if (MathOps.Distance2(seat, found) > 1e-12) disagree = true;
                    }
                }
                if (seat != null && !disagree)
                    cut.Origin = MathOps.Threshold(seat, 1e-11);
            }
        }

        /// <summary>
        /// The point on the line (origin, axis) nearest the line
        /// (otherOrigin, otherAxis), or null when the two are parallel: a
        /// point on the closure's axis is then already as near the other
        /// as any point on it can be, and nothing needs moving.
        /// </summary>
        private static double[] NearestOnAxis(
            double[] origin, double[] axis, double[] otherOrigin, double[] otherAxis)
        {
            var a = MathOps.Normalized(axis);
            var b = MathOps.Normalized(otherAxis);
            if (a == null || b == null) return null;
            double ab = MathOps.Dot(a, b);
            double denom = 1.0 - ab * ab;
            if (denom < 1e-9) return null;      // parallel, or near enough
            var w = new[]
            {
                origin[0] - otherOrigin[0],
                origin[1] - otherOrigin[1],
                origin[2] - otherOrigin[2],
            };
            double t = (ab * MathOps.Dot(b, w) - MathOps.Dot(a, w)) / denom;
            return new[] { origin[0] + t * a[0], origin[1] + t * a[1], origin[2] + t * a[2] };
        }

        /// <summary>
        /// A coupled pair is a mechanism too, and it takes either half as
        /// its input.
        ///
        /// A rack and pinion is one degree of freedom with two ways to hold
        /// it: turn the pinion and the rack runs, or push the rack and the
        /// pinion turns. The exporter names one of them the driver, and
        /// that used to be the end of it: the other half's channel is
        /// written by a driver expression and cannot be posed. Oscar,
        /// 2026-09-16: "can we ensure that the rack and pinion example
        /// gives us an option of either driver? Slider on the rack OR
        /// revolute on the pinion?"
        ///
        /// So the pair is offered like any other mechanism, and taking the
        /// other input turns the coupling round. Only the couplings that
        /// invert: a ratio of turns, of travel, or of travel per turn. A
        /// cam, a table and a mirror are shapes, not ratios, and a cam's
        /// follower never turns its cam.
        ///
        /// A joint that sits in a loop is left alone. Its input is the
        /// loop's business, and the two rules would fight over it.
        /// </summary>
        private static void AddCouplingMechanisms(LoopAnalysisResult result)
        {
            if (result == null) return;
            var inLoop = new HashSet<string>();
            foreach (var lp in result.Loops)
                foreach (string id in lp.MemberJoints) inLoop.Add(id);

            var byId = new Dictionary<string, RigJoint>();
            foreach (var j in result.Joints) byId[j.Id] = j;

            int number = result.Mechanisms.Count + 1;
            foreach (var driven in result.Joints)
            {
                var c = driven.Coupling;
                if (c == null || string.IsNullOrEmpty(c.DriverJoint)) continue;
                if (c.Kind != "gear" && c.Kind != "rack_pinion"
                    && c.Kind != "linear_coupler") continue;
                RigJoint driver;
                if (!byId.TryGetValue(c.DriverJoint, out driver)) continue;
                if (inLoop.Contains(driven.Id) || inLoop.Contains(driver.Id)) continue;
                if (driven.Type == JointType.Fixed || driver.Type == JointType.Fixed) continue;
                if (driven.Type == JointType.Free || driver.Type == JointType.Free) continue;

                var mech = new RigMechanism();
                mech.Id = "mech" + number.ToString(
                    "000", System.Globalization.CultureInfo.InvariantCulture);
                number++;
                mech.CouplingPair = true;
                // The exporter's own choice first, as everywhere else.
                foreach (var jid in new[] { driver.Id, driven.Id })
                {
                    var option = new RigInputOption();
                    option.Joint = jid;
                    mech.Inputs.Add(option);
                }
                result.Mechanisms.Add(mech);
            }
        }

        /// <summary>
        /// Moves a closure off a WELD, where its ring gives it somewhere
        /// else to go.
        ///
        /// A closure re-joins one POINT and the solver only rotates, so a
        /// cut at a fixed joint keeps its two bodies meeting at that point
        /// and leaves the far one free to turn about it. That is the one
        /// thing the weld forbids. A weld belongs in the tree, where it
        /// carries the angle as well as the place.
        ///
        /// Live landing_gear.sldasm (2026-09-16, Oscar): "the wheel_hub
        /// part rotates as I move the slider up and down, it should not, it
        /// should only slide". The ring narrows the pin between the oleo
        /// piston and the wheel assembly to fixed, the cut landed on that
        /// pin, and the wheel was then reached the long way round through
        /// the two sway links: IK put it in the right place at the wrong
        /// angle. The hinge input posed correctly because its cut fell
        /// elsewhere.
        ///
        /// This runs LAST, after the narrowing has settled, and it changes
        /// nothing but the cut. Moved any earlier it moves the spanning
        /// tree with it, and the rings that tree finds are what the
        /// narrowing reads: tried inside the choice, the live plunger came
        /// back with both nuts welded to their plates, which SolidWorks
        /// lets turn.
        /// </summary>
        private static void MoveCutsOffWelds(LoopAnalysisResult result)
        {
            if (result == null || result.Loops.Count == 0) return;
            var byId = new Dictionary<string, RigJoint>();
            foreach (var j in result.Joints) byId[j.Id] = j;

            Recut(result.Loops, result.Loops, byId);
            foreach (var mech in result.Mechanisms)
                foreach (var option in mech.Inputs)
                {
                    // An input re-cuts only the mechanism's own loops; the
                    // rest of the model keeps the closures it has, and one
                    // tree has to hold for all of them at once.
                    var all = new List<RigLoop>(option.Loops);
                    var mine = new HashSet<string>();
                    foreach (var lp in option.Loops) mine.Add(lp.Id);
                    foreach (var lp in result.Loops)
                        if (!mine.Contains(lp.Id)) all.Add(lp);
                    Recut(option.Loops, all, byId);
                }

            // The per-loop candidate list is the same choice said again,
            // for a consumer that reads no mechanisms. Bring it with the
            // input it stands for, and drop what has nowhere to go: an
            // option that cannot hold a weld is not an option to offer.
            foreach (var loop in result.Loops)
            {
                var kept = new List<RigLoopCandidate>();
                foreach (var candidate in loop.DriverCandidates)
                {
                    RigJoint stood;
                    if (candidate.ClosureKind != "ik"
                        || !byId.TryGetValue(candidate.ClosureJoint, out stood)
                        || stood.Type != JointType.Fixed)
                    {
                        kept.Add(candidate);      // it was never on a weld
                        continue;
                    }
                    foreach (var mech in result.Mechanisms)
                        foreach (var option in mech.Inputs)
                        {
                            if (option.Joint != candidate.DriverJoint) continue;
                            var same = SameRing(option.Loops, loop);
                            if (same == null) continue;
                            candidate.ClosureJoint = same.ClosureJoint;
                            candidate.ClosureKind = same.ClosureKind;
                        }
                    RigJoint cut;
                    if (candidate.ClosureKind == "ik"
                        && byId.TryGetValue(candidate.ClosureJoint, out cut)
                        && cut.Type == JointType.Fixed)
                        continue;
                    kept.Add(candidate);
                }
                loop.DriverCandidates = kept;
            }
        }

        /// <summary>
        /// The loop of an input that is the same ring as `loop`: the same
        /// members, else the most members in common, the first on a tie.
        /// Never by id. An input's loops carry the mechanism's ids in
        /// solving order, and the re-choice can meet the rings in another
        /// order, so one id can name two rings (live CutterRig, 2026-09-22:
        /// read by id, a washer ring got the cut of the ring beside it, and
        /// the consumer refused the manifest).
        /// </summary>
        private static RigLoop SameRing(IList<RigLoop> loops, RigLoop loop)
        {
            RigLoop best = null;
            int bestScore = 0;
            foreach (var lp in loops)
            {
                int shared = 0;
                foreach (var jid in lp.MemberJoints)
                    if (loop.MemberJoints.Contains(jid)) shared++;
                bool same = shared == lp.MemberJoints.Count
                            && shared == loop.MemberJoints.Count;
                int score = same ? int.MaxValue : shared;
                if (score > bestScore)
                {
                    best = lp;
                    bestScore = score;
                }
            }
            return best;
        }

        private static void Recut(
            IList<RigLoop> editable, IList<RigLoop> all,
            Dictionary<string, RigJoint> byId)
        {
            foreach (var loop in editable)
            {
                RigJoint cut;
                if (loop.ClosureKind != "ik") continue;
                if (!byId.TryGetValue(loop.ClosureJoint, out cut)) continue;
                if (cut.Type != JointType.Fixed) continue;

                string was = loop.ClosureJoint;
                foreach (var next in AwayFromTheDriver(loop, cut, byId))
                {
                    if (next.Id == loop.SuggestedDriverJoint) continue;
                    if (next.Type == JointType.Fixed || !IsPinCut(next)) continue;
                    loop.ClosureJoint = next.Id;
                    if (EveryClosureStillHasItsPath(all, byId)) break;
                    loop.ClosureJoint = was;
                }
            }
        }

        /// <summary>
        /// The ring's other members, nearest the cut first, and of two at
        /// the same distance the one further from the driver. The bodies
        /// nearest the driver then stay in the tree and the solver gets the
        /// far end, which is where every other cut here is placed.
        ///
        /// The ring is a cycle through the groups, so it is walked out from
        /// one end of the cut round to the other, without using the cut.
        /// </summary>
        private static List<RigJoint> AwayFromTheDriver(
            RigLoop loop, RigJoint cut, Dictionary<string, RigJoint> byId)
        {
            var order = new List<RigJoint> { cut };
            var adjacency = new Dictionary<string, List<RigJoint>>();
            foreach (string id in loop.MemberJoints)
            {
                RigJoint j;
                if (!byId.TryGetValue(id, out j) || ReferenceEquals(j, cut)) continue;
                if (!adjacency.ContainsKey(j.ParentGroup))
                    adjacency[j.ParentGroup] = new List<RigJoint>();
                if (!adjacency.ContainsKey(j.ChildGroup))
                    adjacency[j.ChildGroup] = new List<RigJoint>();
                adjacency[j.ParentGroup].Add(j);
                adjacency[j.ChildGroup].Add(j);
            }
            var walked = new HashSet<string>();
            string at = cut.ParentGroup;
            while (at != cut.ChildGroup)
            {
                List<RigJoint> next;
                if (!adjacency.TryGetValue(at, out next)) break;
                RigJoint step = null;
                foreach (var j in next)
                    if (!walked.Contains(j.Id)) { step = j; break; }
                if (step == null) break;
                walked.Add(step.Id);
                order.Add(step);
                at = step.ParentGroup == at ? step.ChildGroup : step.ParentGroup;
            }
            if (order.Count != loop.MemberJoints.Count) return new List<RigJoint>();

            int n = order.Count, driver = -1;
            for (int i = 0; i < n; i++)
                if (order[i].Id == loop.SuggestedDriverJoint) driver = i;
            var places = new Dictionary<string, int>();
            for (int i = 0; i < n; i++) places[order[i].Id] = i;
            var ranked = order.GetRange(1, n - 1);
            int driverAt = driver;
            ranked.Sort(delegate (RigJoint a, RigJoint b)
            {
                int ia = places[a.Id], ib = places[b.Id];
                int c = Round(ia, n).CompareTo(Round(ib, n));
                if (c != 0) return c;
                if (driverAt >= 0)
                {
                    c = Round(ib - driverAt, n).CompareTo(Round(ia - driverAt, n));
                    if (c != 0) return c;
                }
                return string.CompareOrdinal(a.Id, b.Id);
            });
            return ranked;
        }

        /// <summary>Steps between two places on a ring of n, the short way
        /// round.</summary>
        private static int Round(int steps, int n)
        {
            int k = ((steps % n) + n) % n;
            return Math.Min(k, n - k);
        }

        /// <summary>
        /// Whether the tree that is left when every closure is taken out
        /// still joins each closure's two ends, by exactly that loop's own
        /// members. The consumer checks this before it builds a rig, so a
        /// cut that breaks it is not a cut worth making.
        /// </summary>
        private static bool EveryClosureStillHasItsPath(
            IList<RigLoop> loops, Dictionary<string, RigJoint> byId)
        {
            var closures = new HashSet<string>();
            foreach (var lp in loops)
                if (!closures.Add(lp.ClosureJoint)) return false;

            var adjacency = new Dictionary<string, List<RigJoint>>();
            foreach (var pair in byId)
            {
                var j = pair.Value;
                if (j.Type == JointType.Free || closures.Contains(j.Id)) continue;
                if (!adjacency.ContainsKey(j.ParentGroup))
                    adjacency[j.ParentGroup] = new List<RigJoint>();
                if (!adjacency.ContainsKey(j.ChildGroup))
                    adjacency[j.ChildGroup] = new List<RigJoint>();
                adjacency[j.ParentGroup].Add(j);
                adjacency[j.ChildGroup].Add(j);
            }

            foreach (var lp in loops)
            {
                RigJoint cj;
                if (!byId.TryGetValue(lp.ClosureJoint, out cj)) return false;
                var via = new Dictionary<string, RigJoint>();
                var queue = new Queue<string>();
                queue.Enqueue(cj.ParentGroup);
                via[cj.ParentGroup] = null;
                while (queue.Count > 0 && !via.ContainsKey(cj.ChildGroup))
                {
                    string g = queue.Dequeue();
                    List<RigJoint> next;
                    if (!adjacency.TryGetValue(g, out next)) continue;
                    foreach (var j in next)
                    {
                        string other = j.ParentGroup == g ? j.ChildGroup : j.ParentGroup;
                        if (via.ContainsKey(other)) continue;
                        via[other] = j;
                        queue.Enqueue(other);
                    }
                }
                if (!via.ContainsKey(cj.ChildGroup)) return false;
                var path = new HashSet<string> { cj.Id };
                for (string g = cj.ChildGroup; via[g] != null; )
                {
                    var j = via[g];
                    path.Add(j.Id);
                    g = j.ParentGroup == g ? j.ChildGroup : j.ParentGroup;
                }
                if (!path.SetEquals(lp.MemberJoints)) return false;
            }
            return true;
        }

        /// <summary>
        /// The mechanisms (loops sharing joints) and every input each can
        /// take, as complete alternatives. The exporter's choice is input
        /// 0; each other input re-runs the choice on a copy of the joints
        /// with that joint counted as already chosen, so every loop of the
        /// mechanism follows it exactly as it would have, had that loop
        /// been met first. Inputs the re-choice does not actually take
        /// (a slide that the ram rule turns into an aim pair from its pin)
        /// are not offered: a consumer must only ever see what the
        /// analyzer can produce.
        ///
        /// Live plunger.sldasm (2026-09-15): the panel applied a per-loop
        /// candidate on its own, the other three loops stayed on the old
        /// driver, and the rig came back with three controls on one
        /// degree of freedom, then refused the next choice outright.
        /// </summary>
        private static List<RigMechanism> ComputeMechanisms(
            IList<RigidGroup> groups, IList<RigJoint> joints, LoopAnalysisResult result)
        {
            var mechanisms = new List<RigMechanism>();
            int n = result.Loops.Count;
            if (n == 0) return mechanisms;

            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            Func<int, int> find = null;
            find = x => parent[x] == x ? x : (parent[x] = find(parent[x]));
            var owner = new Dictionary<string, int>();
            for (int i = 0; i < n; i++)
                foreach (var jid in result.Loops[i].MemberJoints)
                {
                    int o;
                    if (owner.TryGetValue(jid, out o))
                    {
                        int a = find(o), b = find(i);
                        if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
                    }
                    else owner[jid] = i;
                }
            var byRoot = new Dictionary<int, List<RigLoop>>();
            var rootOrder = new List<int>();
            for (int i = 0; i < n; i++)
            {
                int r = find(i);
                if (!byRoot.ContainsKey(r)) { byRoot[r] = new List<RigLoop>(); rootOrder.Add(r); }
                byRoot[r].Add(result.Loops[i]);
            }

            int number = 1;
            foreach (int r in rootOrder)
            {
                var loops = byRoot[r];
                var mech = new RigMechanism();
                mech.Id = "mech" + number.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
                number++;
                foreach (var lp in loops) mech.LoopIds.Add(lp.Id);

                var inputs = new List<string>();
                foreach (var lp in loops)
                    foreach (var c in lp.DriverCandidates)
                        if (!inputs.Contains(c.DriverJoint)) inputs.Add(c.DriverJoint);
                if (inputs.Count == 0) { mechanisms.Add(mech); continue; }

                // An option is named by the joint the user will pose: the
                // driver no other loop's chain solves. Two loops of one
                // mechanism can name two drivers (a bolt loop hung off the
                // link the slider loop solves), and the votes would then
                // name a joint that ends up no control at all (live
                // plunger.sldasm: three loops said the link pin, the rig
                // had one control, the plunger).
                var primary = new RigInputOption();
                var controls = Controls(loops);
                primary.Joint = controls.Count > 0 ? controls[0] : CurrentDriver(loops);
                primary.Loops.AddRange(loops);
                mech.Inputs.Add(primary);
                var signatures = new HashSet<string> { Signature(loops) };

                var memberSet = new HashSet<string>();
                foreach (var lp in loops) foreach (var jid in lp.MemberJoints) memberSet.Add(jid);

                foreach (string alt in inputs)
                {
                    // A weld cannot be posed, so it is no input to offer.
                    // Only a ring of welds names one as its candidate.
                    RigJoint altJoint = null;
                    foreach (var j in joints) if (j.Id == alt) { altJoint = j; break; }
                    if (altJoint != null && altJoint.Type == JointType.Fixed)
                    {
                        result.Notes.Add(mech.Id + ": input " + alt + " not offered, it is a weld");
                        continue;
                    }
                    var clones = new List<RigJoint>(joints.Count);
                    foreach (var j in joints)
                    {
                        var c = Clone(j);
                        // A limit derived from a stroke belongs to the
                        // configuration that made this joint the driver;
                        // the option derives its own below.
                        if (result.DerivedLimitJoints.Contains(j.Id)) c.RotationLimit = null;
                        clones.Add(c);
                    }
                    var altResult = Choose(groups, clones, new HashSet<string> { alt }, false);
                    DeriveSliderDriverLimits(altResult);
                    var altLoops = new List<RigLoop>();
                    foreach (var lp in altResult.Loops)
                    {
                        bool shares = false;
                        foreach (var jid in lp.MemberJoints) if (memberSet.Contains(jid)) { shares = true; break; }
                        if (shares) altLoops.Add(lp);
                    }
                    // The cycle rank of a mechanism does not change with the
                    // input; a different count means the re-choice found a
                    // different mechanism, which is nothing to offer.
                    if (altLoops.Count != loops.Count)
                    {
                        result.Notes.Add(mech.Id + ": input " + alt + " not offered, the re-choice"
                                         + " found " + altLoops.Count + " loop(s) where the choice has "
                                         + loops.Count);
                        continue;
                    }
                    bool taken = false;
                    foreach (var lp in altLoops) if (lp.SuggestedDriverJoint == alt) taken = true;
                    if (!taken)
                    {
                        var drivers = new List<string>();
                        foreach (var lp in altLoops) drivers.Add(lp.Id + "<-" + lp.SuggestedDriverJoint);
                        result.Notes.Add(mech.Id + ": input " + alt + " not offered, the re-choice"
                                         + " drove no loop from it (" + string.Join(", ", drivers) + ")");
                        continue;
                    }
                    // Same ids as the choice, so bones keep their names when
                    // the consumer switches. The ids name slots in solving
                    // order, not rings: the re-choice can meet the rings in
                    // another order (see SameRing).
                    for (int i = 0; i < altLoops.Count; i++) altLoops[i].Id = loops[i].Id;

                    if (!signatures.Add(Signature(altLoops)))
                    {
                        result.Notes.Add(mech.Id + ": input " + alt + " is the configuration"
                                         + " already offered");
                        continue;
                    }
                    var option = new RigInputOption();
                    var altControls = Controls(altLoops);
                    // More controls than the choice has is more inputs than
                    // the mechanism has freedoms: a rig where posing one
                    // leaves the others behind. The live plunger's arm
                    // hinge came out that way (its nut rings then drove
                    // from a nut's planar joint), and it is not offered.
                    if (altControls.Count > controls.Count)
                    {
                        var shape = new List<string>();
                        foreach (var lp in altLoops)
                            shape.Add(lp.Id + "<-" + lp.SuggestedDriverJoint + "/" + lp.ClosureJoint
                                      + "/" + lp.ClosureKind);
                        result.Notes.Add(mech.Id + ": input " + alt + " not offered, it needs "
                                         + altControls.Count + " control(s) ("
                                         + string.Join(",", altControls) + ") where the choice needs "
                                         + controls.Count + "; " + string.Join(" ", shape));
                        continue;
                    }
                    option.Joint = altControls.Count > 0 ? altControls[0] : alt;
                    if (NamesAnOption(mech, option.Joint)) option.Joint = alt;
                    if (NamesAnOption(mech, option.Joint))
                    {
                        result.Notes.Add(mech.Id + ": input " + alt + " not offered, its control "
                                         + option.Joint + " already names an option");
                        continue;
                    }
                    option.Loops.AddRange(altLoops);
                    for (int i = 0; i < joints.Count; i++)
                        if (clones[i].ParentGroup == joints[i].ChildGroup
                            && clones[i].ChildGroup == joints[i].ParentGroup)
                            option.FlippedJoints.Add(joints[i].Id);
                    // Limits that differ under this input: a stroke limit
                    // derived onto the crank of a slider-crank belongs to the
                    // crank-driven configuration, and the slider-driven one
                    // has the slide's own limit instead (live
                    // actuator.sldasm, 2026-09-15: switching input lost the
                    // stop).
                    for (int i = 0; i < joints.Count; i++)
                    {
                        if (SameLimit(clones[i].RotationLimit, joints[i].RotationLimit)
                            && SameLimit(clones[i].TranslationLimit, joints[i].TranslationLimit))
                            continue;
                        option.JointLimits.Add(new RigOptionLimit
                        {
                            Joint = joints[i].Id,
                            RotationLimit = clones[i].RotationLimit,
                            TranslationLimit = clones[i].TranslationLimit,
                        });
                    }
                    mech.Inputs.Add(option);
                }
                mechanisms.Add(mech);
            }
            return mechanisms;
        }

        /// <summary>The joint most of the loops name as their driver, the
        /// first named on a tie. The consumer's inputs.current does the
        /// same, so both sides agree on which option is the standing one.</summary>
        private static string CurrentDriver(List<RigLoop> loops)
        {
            var votes = new Dictionary<string, int>();
            var order = new List<string>();
            foreach (var lp in loops)
            {
                string jid = lp.SuggestedDriverJoint;
                if (string.IsNullOrEmpty(jid)) continue;
                if (!votes.ContainsKey(jid)) { votes[jid] = 0; order.Add(jid); }
                votes[jid]++;
            }
            string best = null;
            foreach (var jid in order)
                if (best == null || votes[jid] > votes[best]) best = jid;
            return best;
        }

        /// <summary>
        /// Loops in the order the consumer must solve them: a loop whose
        /// driver's body another loop's chain solves comes AFTER that loop.
        /// The consumer takes the manifest order as the solving order and
        /// truncates a later chain at the first body an earlier one solved,
        /// so with a bolt loop ahead of the slider loop that moves its
        /// link, the bolt loop claimed the link, the slider loop's chain
        /// stopped short of the arm, and the arm's hinge came out a third
        /// control (live plunger.sldasm, 2026-09-15: one control in one
        /// component order, three in another). Ids are renumbered to the
        /// new order.
        /// </summary>
        private static void OrderLoops(List<RigLoop> loops)
        {
            if (loops.Count < 2) return;
            var solvedBy = new Dictionary<string, RigLoop>();
            foreach (var lp in loops)
            {
                if (lp.RingGroups == null) continue;
                foreach (var g in lp.RingGroups)
                    if (g != lp.AnchorGroup && g != lp.DriverChildGroup && !solvedBy.ContainsKey(g))
                        solvedBy[g] = lp;
            }
            var ordered = new List<RigLoop>();
            var placed = new HashSet<string>();
            while (ordered.Count < loops.Count)
            {
                RigLoop next = null;
                foreach (var lp in loops)
                {
                    if (placed.Contains(lp.Id)) continue;
                    RigLoop owner;
                    bool waits = lp.DriverChildGroup != null
                        && solvedBy.TryGetValue(lp.DriverChildGroup, out owner)
                        && owner != lp && !placed.Contains(owner.Id);
                    if (!waits) { next = lp; break; }
                }
                if (next == null)
                {
                    // A cycle of loops each solving the other's driver: keep
                    // the met order for the rest.
                    foreach (var lp in loops) if (!placed.Contains(lp.Id)) { next = lp; break; }
                }
                ordered.Add(next);
                placed.Add(next.Id);
            }
            loops.Clear();
            loops.AddRange(ordered);
            for (int i = 0; i < loops.Count; i++)
                loops[i].Id = "loop" + (i + 1).ToString("000", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>The drivers the user will pose, in loop order: a driver
        /// whose posed body lies inside another loop's solved chain is no
        /// control, the chain moves it. This replays the consumer's rule
        /// (rig/graph.py, solved_by) from the rings recorded at choice.</summary>
        private static List<string> Controls(List<RigLoop> loops)
        {
            var solvedBy = new Dictionary<string, string>();
            foreach (var lp in loops)
            {
                if (lp.RingGroups == null) continue;
                foreach (var g in lp.RingGroups)
                    if (g != lp.AnchorGroup && g != lp.DriverChildGroup && !solvedBy.ContainsKey(g))
                        solvedBy[g] = lp.Id;
            }
            var controls = new List<string>();
            foreach (var lp in loops)
            {
                if (string.IsNullOrEmpty(lp.SuggestedDriverJoint)) continue;
                string owner;
                if (lp.DriverChildGroup != null && solvedBy.TryGetValue(lp.DriverChildGroup, out owner)
                    && owner != lp.Id)
                    continue;
                if (!controls.Contains(lp.SuggestedDriverJoint)) controls.Add(lp.SuggestedDriverJoint);
            }
            return controls;
        }

        /// <summary>What a configuration IS, ids aside: every loop's driver,
        /// cut, closure kind and members, as a set.</summary>
        private static string Signature(List<RigLoop> loops)
        {
            var parts = new List<string>();
            foreach (var lp in loops)
                parts.Add(lp.SuggestedDriverJoint + ">" + lp.ClosureJoint + ">" + lp.ClosureKind
                          + ">" + string.Join(",", lp.MemberJoints));
            parts.Sort(string.CompareOrdinal);
            return string.Join("|", parts);
        }

        private static bool SameLimit(JointLimit a, JointLimit b)
        {
            if (a == null || b == null) return a == null && b == null;
            return Math.Abs(a.Min - b.Min) < 1e-9 && Math.Abs(a.Max - b.Max) < 1e-9
                && Math.Abs(a.ValueAtRest - b.ValueAtRest) < 1e-9;
        }

        /// <summary>
        /// Drops every mechanism input whose joint a coupling writes. A
        /// driver is one-way: pushing a cam's follower never turns the cam
        /// (live cam-follower, 2026-09-15: the lifter's slide was offered
        /// beside the cam's hinge and did nothing). The couplers and the
        /// relation probe run after the inputs were chosen, so this is a
        /// pass of its own. The chosen configuration (the first input) is
        /// left as it is, with a note: the rig is built from it.
        /// </summary>
        public static void PruneDrivenInputs(LoopAnalysisResult loops)
        {
            foreach (var mech in loops.Mechanisms)
            {
                // A coupled pair offers its DRIVEN half on purpose: taking
                // it turns the coupling round rather than posing a channel
                // something else writes (AddCouplingMechanisms).
                if (mech.CouplingPair) continue;
                for (int k = mech.Inputs.Count - 1; k >= 0; k--)
                {
                    var joint = loops.Joints.Find(j => j.Id == mech.Inputs[k].Joint);
                    if (joint == null || joint.Coupling == null
                        || string.IsNullOrEmpty(joint.Coupling.DriverJoint)) continue;
                    if (k == 0)
                    {
                        loops.Notes.Add(mech.Id + ": the chosen input " + joint.Id
                            + " is driven by " + joint.Coupling.DriverJoint + " through a "
                            + joint.Coupling.Kind + " coupling; the rig will not move from it");
                        continue;
                    }
                    loops.Notes.Add(mech.Id + ": input " + joint.Id + " dropped, "
                        + joint.Coupling.DriverJoint + " drives it through a "
                        + joint.Coupling.Kind + " coupling");
                    mech.Inputs.RemoveAt(k);
                }
            }
        }

        private static bool NamesAnOption(RigMechanism mech, string joint)
        {
            foreach (var o in mech.Inputs) if (o.Joint == joint) return true;
            return false;
        }

        /// <summary>A joint the re-choice may orient, seat and couple
        /// without touching the manifest's own. Geometry arrays are copied
        /// because seating writes into them.</summary>
        private static RigJoint Clone(RigJoint j)
        {
            var c = new RigJoint();
            c.Id = j.Id;
            c.Type = j.Type;
            c.ParentGroup = j.ParentGroup;
            c.ChildGroup = j.ChildGroup;
            c.Origin = j.Origin == null ? null : (double[])j.Origin.Clone();
            c.Axis = j.Axis == null ? null : (double[])j.Axis.Clone();
            c.SecondaryAxis = j.SecondaryAxis == null ? null : (double[])j.SecondaryAxis.Clone();
            c.RotationLimit = j.RotationLimit;
            c.TranslationLimit = j.TranslationLimit;
            c.Coupling = j.Coupling;
            c.SourceMates = j.SourceMates;
            c.Confidence = j.Confidence;
            c.Notes = j.Notes;
            c.PathPoints = j.PathPoints;
            c.PathClosed = j.PathClosed;
            c.SurfacePoints = j.SurfacePoints;
            c.SurfaceTriangles = j.SurfaceTriangles;
            c.ResidualKnown = j.ResidualKnown;
            c.ResidualRot = j.ResidualRot;
            c.ResidualRotDir = j.ResidualRotDir;
            return c;
        }

        /// <summary>
        /// Every loop's members, read off the FINAL tree. A loop's ring is
        /// recorded when it is met, and a later loop's cut swap moves an
        /// edge of that ring into or out of the tree, so the members no
        /// longer describe the path the consumer will find between the
        /// closure's ends (live lens_mount.sldasm and plunger.sldasm,
        /// 2026-09-15: Blender refused both, "member_joints do not match the
        /// tree path plus closure"). Candidates that name an edge no longer
        /// on the ring go too, except the choice itself.
        /// </summary>
        private static void RefreshLoopMembers(
            LoopAnalysisResult result, int groupCount, List<RigJoint>[] adjacency,
            Dictionary<string, int> groupIndex, List<int> roots, HashSet<string> tree)
        {
            var byId = new Dictionary<string, RigJoint>();
            foreach (var j in result.Joints) byId[j.Id] = j;
            var walk = Bfs(groupCount, adjacency, groupIndex, roots, tree, null);
            foreach (var loop in result.Loops)
            {
                RigJoint closure;
                if (!byId.TryGetValue(loop.ClosureJoint ?? "", out closure)) continue;
                var ring = CycleRing(closure, walk, groupIndex);
                if (ring == null) continue;
                var members = new List<RigJoint>(ring.Edges);
                members.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));
                var ids = new HashSet<string>();
                foreach (var j in members) ids.Add(j.Id);
                loop.MemberJoints.Clear();
                foreach (var j in members) loop.MemberJoints.Add(j.Id);
                var kept = new List<RigLoopCandidate>();
                for (int i = 0; i < loop.DriverCandidates.Count; i++)
                {
                    var c = loop.DriverCandidates[i];
                    RigJoint cd;
                    bool weld = byId.TryGetValue(c.DriverJoint, out cd) && cd.Type == JointType.Fixed;
                    if (i == 0 || (!weld && ids.Contains(c.DriverJoint) && ids.Contains(c.ClosureJoint)))
                        kept.Add(c);
                }
                loop.DriverCandidates = kept;
                SetPlanarity(loop, members);
            }
        }

        // ── Parallel-mate couplings ─────────────────────────────────────────

        /// <summary>
        /// A parallel mate between two moving links forms no joint (it kills
        /// two rotations and no translation), but when every tree joint on
        /// the paths between the pair rotates about one common direction z,
        /// locking their relative orientation about z means the SIGNED JOINT
        /// ANGLES on the two paths must stay equal: a 1:1 gear relation.
        /// Live corpus 06 parallelogram3 (2026-08-22): a corner pin replaced
        /// by parallel mates left three independent revolutes; SolidWorks
        /// still moved as a parallelogram, the rig did not. Only two-term
        /// relations are expressible (one coupling = one driver), and a
        /// relation already enforced by a declared loop's IK closure must NOT
        /// also become a driver: the two would fight.
        /// </summary>
        private static void SynthesizeParallelCouplings(
            LoopAnalysisResult result, int groupCount, List<RigJoint>[] adjacency,
            Dictionary<string, int> groupIndex, List<int> roots, HashSet<string> tree)
        {
            var walk = Bfs(groupCount, adjacency, groupIndex, roots, tree, null);

            var loopMembers = new HashSet<string>();
            foreach (var lp in result.Loops)
                foreach (var id in lp.MemberJoints) loopMembers.Add(id);

            var childDepth = new Dictionary<string, int>();   // tree joint id -> child group depth
            foreach (var j in result.Joints)
            {
                if (!tree.Contains(j.Id)) continue;
                int c;
                if (groupIndex.TryGetValue(j.ChildGroup, out c)) childDepth[j.Id] = walk.Depth[c];
            }

            foreach (var free in result.Joints)
            {
                if (free.Type != JointType.Free || !free.ResidualKnown) continue;
                if (free.ResidualRot == RotFreedom.Full
                    || free.ResidualRot == RotFreedom.AboutPoint) continue;

                int u, v;
                if (!groupIndex.TryGetValue(free.ParentGroup, out u)) continue;
                if (!groupIndex.TryGetValue(free.ChildGroup, out v)) continue;
                if (u == v || walk.Depth[u] < 0 || walk.Depth[v] < 0) continue;

                // Tree-path joints from each end up to the common ancestor.
                var pathU = new List<RigJoint>();
                var pathV = new List<RigJoint>();
                int uu = u, vv = v;
                bool broken = false;
                while (walk.Depth[uu] > walk.Depth[vv]) { pathU.Add(walk.ParentJoint[uu]); uu = walk.ParentGroup[uu]; }
                while (walk.Depth[vv] > walk.Depth[uu]) { pathV.Add(walk.ParentJoint[vv]); vv = walk.ParentGroup[vv]; }
                while (uu != vv)
                {
                    if (walk.ParentGroup[uu] < 0 || walk.ParentGroup[vv] < 0) { broken = true; break; }
                    pathU.Add(walk.ParentJoint[uu]);
                    uu = walk.ParentGroup[uu];
                    pathV.Add(walk.ParentJoint[vv]);
                    vv = walk.ParentGroup[vv];
                }
                if (broken || pathU.Count + pathV.Count != 2) continue;

                // Every path joint must spin about one common direction z for
                // "orientation locked about z" to mean "signed angles equal".
                var terms = new List<RigJoint>();
                var signs = new List<int>();     // +1 on the parent-side path, -1 on the child side, times the axis sense
                double[] z = null;
                bool eligible = true;
                for (int side = 0; side < 2; side++)
                {
                    var path = side == 0 ? pathU : pathV;
                    int pathSign = side == 0 ? 1 : -1;
                    foreach (var t in path)
                    {
                        bool rotates = t.Type == JointType.Revolute || t.Type == JointType.Cylindrical;
                        if (!rotates || t.Axis == null) { eligible = false; break; }
                        var axis = MathOps.Normalized(t.Axis);
                        if (z == null) z = axis;
                        else if (!MateFacts.IsParallel(axis, z)) { eligible = false; break; }
                        terms.Add(t);
                        signs.Add(MathOps.Dot(axis, z) > 0 ? pathSign : -pathSign);
                    }
                    if (!eligible) break;
                }
                if (!eligible || z == null) continue;

                // The mate must actually kill relative rotation about z: any
                // surviving rotation direction parallel to z leaves the
                // angles unrelated.
                if (free.ResidualRot != RotFreedom.None)
                {
                    if (free.ResidualRotDir == null) continue;
                    if (MateFacts.IsParallel(free.ResidualRotDir, z)) continue;
                }

                if (loopMembers.Contains(terms[0].Id) || loopMembers.Contains(terms[1].Id))
                {
                    result.RedundantFreeJointIds.Add(free.Id);
                    continue;
                }

                // Driven = the joint deeper in the tree (its bone hangs below
                // the driver's in the rig); equal depths fall to the higher
                // id, so the first-created ground joint stays the input.
                int d0, d1;
                childDepth.TryGetValue(terms[0].Id, out d0);
                childDepth.TryGetValue(terms[1].Id, out d1);
                int drivenIdx;
                if (d0 != d1) drivenIdx = d0 > d1 ? 0 : 1;
                else drivenIdx = string.CompareOrdinal(terms[0].Id, terms[1].Id) > 0 ? 0 : 1;
                if (terms[drivenIdx].Coupling != null) drivenIdx = 1 - drivenIdx;
                var driven = terms[drivenIdx];
                var driver = terms[1 - drivenIdx];
                if (driven.Coupling != null || driven == driver) continue;

                // Chained couplings are fine; a mutual pair takes the whole
                // manifest down in the consumer's dependency pre-flight.
                bool cycles = false;
                var visited = new HashSet<string>();
                for (var d = driver; d != null && d.Coupling != null
                     && d.Coupling.DriverJoint != null;)
                {
                    if (!visited.Add(d.Id) || d.Coupling.DriverJoint == driven.Id)
                    {
                        cycles = true;
                        break;
                    }
                    d = FindJoint(result.Joints, d.Coupling.DriverJoint);
                }
                if (cycles) continue;

                // signs[p]*theta_p + signs[q]*theta_q = 0, both in {-1, +1},
                // so theta_driven = -(sign_driver * sign_driven) * theta_driver.
                driven.Coupling = new JointCoupling
                {
                    Kind = "gear",
                    DriverJoint = driver.Id,
                    Ratio = -(double)(signs[drivenIdx] * signs[1 - drivenIdx]),
                };
                foreach (var sm in free.SourceMates)
                    driven.SourceMates.Add(new SourceMate { SwFeature = sm.SwFeature, Type = sm.Type });
                result.CoupledFreeJointIds.Add(free.Id);
            }
        }

        private static RigJoint FindJoint(List<RigJoint> joints, string id)
        {
            foreach (var j in joints)
                if (j.Id == id) return j;
            return null;
        }

        // ── Spanning tree ───────────────────────────────────────────────────

        private sealed class Walk
        {
            public int[] Depth;
            public int[] ParentGroup;          // -1 at a root
            public RigJoint[] ParentJoint;
        }

        /// <summary>
        /// BFS over the group graph. With restrictTo null it builds the
        /// initial spanning tree and records it into recordTree; with a tree
        /// set given it only walks those edges, recomputing depths and parents
        /// after a cut swap.
        /// </summary>
        private static Walk Bfs(
            int groupCount, List<RigJoint>[] adjacency, Dictionary<string, int> groupIndex,
            List<int> roots, HashSet<string> restrictTo, HashSet<string> recordTree)
        {
            var walk = new Walk();
            walk.Depth = new int[groupCount];
            walk.ParentGroup = new int[groupCount];
            walk.ParentJoint = new RigJoint[groupCount];
            var visited = new bool[groupCount];
            for (int i = 0; i < groupCount; i++) { walk.Depth[i] = -1; walk.ParentGroup[i] = -1; }

            var queue = new Queue<int>();
            foreach (int root in roots)
            {
                if (visited[root]) continue;
                visited[root] = true;
                walk.Depth[root] = 0;
                queue.Enqueue(root);
                while (queue.Count > 0)
                {
                    int g = queue.Dequeue();
                    foreach (var j in adjacency[g])
                    {
                        if (restrictTo != null && !restrictTo.Contains(j.Id)) continue;
                        int other = OtherEnd(j, g, groupIndex);
                        if (visited[other]) continue;
                        visited[other] = true;
                        walk.Depth[other] = walk.Depth[g] + 1;
                        walk.ParentGroup[other] = g;
                        walk.ParentJoint[other] = j;
                        if (recordTree != null) recordTree.Add(j.Id);
                        queue.Enqueue(other);
                    }
                }
            }
            return walk;
        }

        private static int OtherEnd(RigJoint j, int g, Dictionary<string, int> groupIndex)
        {
            int a = groupIndex[j.ParentGroup];
            int b = groupIndex[j.ChildGroup];
            return a == g ? b : a;
        }

        private sealed class Ring
        {
            public List<int> Groups = new List<int>();       // Groups[0] is the anchor, nearest its root
            public List<RigJoint> Edges = new List<RigJoint>(); // Edges[i] joins Groups[i] to Groups[(i+1) % n]
        }

        /// <summary>The fundamental cycle of a non-tree edge as an ordered
        /// ring starting at the anchor (the cycle group nearest the root):
        /// both tree paths up to the common ancestor plus the edge itself.
        /// Driver and cut selection need adjacency, which a sorted member
        /// list cannot give.</summary>
        private static Ring CycleRing(
            RigJoint closure, Walk walk, Dictionary<string, int> groupIndex)
        {
            int u = groupIndex[closure.ParentGroup];
            int v = groupIndex[closure.ChildGroup];
            if (walk.Depth[u] < 0 || walk.Depth[v] < 0) return null;

            var groupsU = new List<int>();
            var edgesU = new List<RigJoint>();
            var groupsV = new List<int>();
            var edgesV = new List<RigJoint>();
            while (walk.Depth[u] > walk.Depth[v])
            {
                groupsU.Add(u);
                edgesU.Add(walk.ParentJoint[u]);
                u = walk.ParentGroup[u];
            }
            while (walk.Depth[v] > walk.Depth[u])
            {
                groupsV.Add(v);
                edgesV.Add(walk.ParentJoint[v]);
                v = walk.ParentGroup[v];
            }
            while (u != v)
            {
                if (walk.ParentGroup[u] < 0 || walk.ParentGroup[v] < 0) return null;
                groupsU.Add(u);
                edgesU.Add(walk.ParentJoint[u]);
                u = walk.ParentGroup[u];
                groupsV.Add(v);
                edgesV.Add(walk.ParentJoint[v]);
                v = walk.ParentGroup[v];
            }

            // u == v is the anchor; the ring runs down the v side, across the
            // closure, and back up the u side.
            var ring = new Ring();
            ring.Groups.Add(u);
            for (int i = edgesV.Count - 1; i >= 0; i--)
            {
                ring.Edges.Add(edgesV[i]);
                ring.Groups.Add(groupsV[i]);
            }
            ring.Edges.Add(closure);
            for (int i = 0; i < edgesU.Count; i++)
            {
                ring.Groups.Add(groupsU[i]);
                ring.Edges.Add(edgesU[i]);
            }
            return ring;
        }

        // ── Loop annotations ────────────────────────────────────────────────

        /// <summary>Chooses between the two anchor-incident driver candidates
        /// (each paired with the cut its choice implies). A limited joint is
        /// the modelled input; a revolute drives best under hand-posing; a
        /// revolute cut is the only closure an IK point constraint models
        /// faithfully; ids break the tie so re-exports are stable.</summary>
        private static bool Driven(RigJoint j)
        {
            return j.Coupling != null && !string.IsNullOrEmpty(j.Coupling.DriverJoint);
        }

        private static bool PreferFirst(
            RigJoint driverA, RigJoint cutA, RigJoint driverB, RigJoint cutB,
            HashSet<string> chosen = null, HashSet<string> seed = null)
        {
            // The input asked for outranks everything: see Choose's seed.
            if (seed != null)
            {
                bool sa = seed.Contains(driverA.Id), sb = seed.Contains(driverB.Id);
                if (sa != sb) return sa;
            }
            // Consistency across loops outranks everything below: a joint
            // that already drives another loop of this mechanism drives this
            // one too, or the two loops pull against each other.
            if (chosen != null)
            {
                bool ka = chosen.Contains(driverA.Id), kb = chosen.Contains(driverB.Id);
                if (ka != kb) return ka;
            }
            // A weld cannot be posed: it drives nothing, whatever else it
            // has going for it (the narrowing can leave one at a ring's
            // anchor, live weldingrobot.sldasm 2026-09-15). Nor can a joint
            // a coupling writes: pushing a cam's follower never turns the
            // cam (live cam-follower, 2026-09-15: the lifter's slide was
            // chosen over the cam's hinge and the rig would not move).
            bool fa = driverA.Type == JointType.Fixed || Driven(driverA);
            bool fb = driverB.Type == JointType.Fixed || Driven(driverB);
            if (fa != fb) return !fa;
            bool la = HasLimits(driverA), lb = HasLimits(driverB);
            if (la != lb) return la;
            bool ra = driverA.Type == JointType.Revolute;
            bool rb = driverB.Type == JointType.Revolute;
            if (ra != rb) return ra;
            bool pa = IsPinCut(cutA), pb = IsPinCut(cutB);
            if (pa != pb) return pa;
            bool ca = cutA.Type == JointType.Revolute;
            bool cb = cutB.Type == JointType.Revolute;
            if (ca != cb) return ca;
            return string.CompareOrdinal(driverA.Id, driverB.Id) <= 0;
        }

        // ── Slider-crank limits ─────────────────────────────────────────────

        /// <summary>
        /// Carries a slider-crank's stroke limit round to its driver.
        ///
        /// Oscar's question, 2026-08-24: the hydraulic ram's stroke is what
        /// stops the clamp in SolidWorks: the ram bottoms out and the clamp
        /// can go no further. Cut the loop at the slide and the clamp becomes
        /// a free input again, so it would swing straight past the stop and
        /// leave the ram behind. But the two limits are the SAME constraint
        /// seen from two corners of one triangle, and the triangle is fixed:
        /// A is the ram's mount, B the driver's pivot, C the rod's mount, and
        /// |AB| and |BC| are rigid because each pair sits on one body. Only
        /// |AC| (the ram), changes. So
        ///
        ///     cos(ABC) = (|AB|² + |BC|² − |AC|²) / (2·|AB|·|BC|)
        ///
        /// converts the stroke range into the driver's angle range exactly,
        /// and the clamp stops where SolidWorks stops it. The rod keeps its
        /// own stroke limit as well; the two now agree instead of one being
        /// free to break the other.
        /// </summary>
        /// <summary>
        /// Puts the two mounts of an aim-pair closure ON the ram they close.
        ///
        /// A pin's origin is only defined up to sliding along its own axis:
        /// every point of the pin line is the same joint, and which one comes
        /// out is whichever entity the mate happened to name: the top of a
        /// lug, the centre of one circular edge. So the two ends of a
        /// perfectly straight ram arrive at different heights on their pins.
        ///
        /// The aim closure cannot live with that. It stands in for the slide
        /// by making each half point AT the other half's pivot, which
        /// reproduces the slide only when both pivots lie on the slide's own
        /// axis. Off it, the halves aim ACROSS the ram instead of along it,
        /// and the rod comes at the bore at an angle (live ClampRig,
        /// 2026-08-24: the barrel pin arrived 45 mm above the ram axis and the
        /// rod pin 31 mm below it, and both pin lines cross the ram axis
        /// exactly at the ram's own centre plane).
        ///
        /// Sliding each mount to the point of its pin closest to the ram axis
        /// is free (it is the same joint, with the same twist) and it is
        /// where the pin really crosses the ram.
        /// </summary>
        private static void SeatAimPairMounts(LoopAnalysisResult result)
        {
            if (result.Loops.Count == 0) return;

            var byId = new Dictionary<string, RigJoint>();
            foreach (var j in result.Joints) byId[j.Id] = j;

            var closures = new HashSet<string>();
            foreach (var lp in result.Loops) closures.Add(lp.ClosureJoint);

            // Each group's TREE parent joint: the mount whose origin is the
            // point that body swings about, and the point the other half of
            // the pair will be aimed at.
            var mountOf = new Dictionary<string, RigJoint>();
            foreach (var j in result.Joints)
                if (!closures.Contains(j.Id) && !mountOf.ContainsKey(j.ChildGroup))
                    mountOf[j.ChildGroup] = j;

            var seated = new HashSet<string>();
            foreach (var lp in result.Loops)
            {
                if (lp.ClosureKind != "aim_pair") continue;
                RigJoint slide;
                if (!byId.TryGetValue(lp.ClosureJoint ?? "", out slide)) continue;
                if (slide.Axis == null || slide.Origin == null) continue;

                foreach (string gid in new[] { slide.ParentGroup, slide.ChildGroup })
                {
                    RigJoint mount;
                    if (gid == null) continue;
                    if (!mountOf.TryGetValue(gid, out mount)) continue;
                    // A pin shared by two rams would otherwise be dragged onto
                    // whichever ram was seated last.
                    if (!seated.Add(mount.Id)) continue;
                    SeatOnSlide(mount, slide);
                }
            }
        }

        /// <summary>Slides a pin's origin along its OWN axis to the point
        /// closest to the ram's axis line.</summary>
        private static void SeatOnSlide(RigJoint pin, RigJoint slide)
        {
            // Only a pin's origin is free along its axis in a way that leaves
            // the joint untouched AND means something here. Anything else is
            // left exactly where the exporter put it.
            if (pin.Type != JointType.Revolute
                && pin.Type != JointType.Cylindrical) return;
            if (pin.Axis == null || pin.Origin == null) return;
            if (MathOps.Norm(pin.Axis) < 1e-9) return;

            var d1 = MathOps.Normalized(pin.Axis);
            var d2 = MathOps.Normalized(slide.Axis);
            double b = MathOps.Dot(d1, d2);
            if (1.0 - b * b < 1e-9)
            {
                // The pin runs along the ram, so every point of it is the same
                // distance from the ram's axis and nothing here can choose.
                pin.Notes = AppendLoopNote(pin.Notes,
                    "This pin is parallel to the slide " + slide.Id
                    + " it is aimed along, so no point of it is nearer the "
                    + "ram's axis than any other: the bone rests where the "
                    + "mate entity sat.");
                return;
            }

            var w0 = Minus(pin.Origin, slide.Origin);
            double t = (b * MathOps.Dot(d2, w0) - MathOps.Dot(d1, w0))
                       / (1.0 - b * b);
            var at = new[] { pin.Origin[0] + t * d1[0],
                             pin.Origin[1] + t * d1[1],
                             pin.Origin[2] + t * d1[2] };

            // How far that point still is from the ram's axis. Zero on a real
            // ram, because the pin crosses it; anything else and the pins are
            // genuinely off the ram, so aiming one half at the other is an
            // approximation and the manifest should say so.
            double miss = MathOps.Norm(FlattenOnto(Minus(at, slide.Origin), d2));
            if (miss > 1e-6)
                pin.Notes = AppendLoopNote(pin.Notes,
                    "This pin passes "
                    + (miss * 1000.0).ToString("0.###",
                        System.Globalization.CultureInfo.InvariantCulture)
                    + " mm clear of the axis of the slide " + slide.Id
                    + " it is aimed along, so the two halves aim slightly "
                    + "across that slide rather than along it.");

            pin.Origin = at;
        }

        private static void DeriveSliderDriverLimits(LoopAnalysisResult result)
        {
            if (result.Loops.Count == 0) return;

            var byId = new Dictionary<string, RigJoint>();
            foreach (var j in result.Joints) byId[j.Id] = j;

            var closures = new HashSet<string>();
            foreach (var lp in result.Loops) closures.Add(lp.ClosureJoint);

            // Each group's TREE parent joint: the mount whose origin is the
            // point that body swings about.
            var mountOf = new Dictionary<string, RigJoint>();
            foreach (var j in result.Joints)
                if (!closures.Contains(j.Id) && !mountOf.ContainsKey(j.ChildGroup))
                    mountOf[j.ChildGroup] = j;

            foreach (var lp in result.Loops)
            {
                if (!lp.Planar || lp.PlaneNormal == null) continue;

                RigJoint slide, driver;
                if (!byId.TryGetValue(lp.ClosureJoint ?? "", out slide)) continue;
                if (!byId.TryGetValue(lp.SuggestedDriverJoint ?? "", out driver)) continue;
                // A ram's stroke may read CYLINDRICAL or PRISMATIC: a rod
                // that can spin in its barrel keeps that spin unless the
                // ring's narrowing takes it. Its stroke limit is a limit on
                // the slide all the same, and it is the one thing that
                // stops the driver (live TongRig, 2026-09-14).
                if (slide.Type != JointType.Prismatic && slide.Type != JointType.Screw
                    && slide.Type != JointType.Cylindrical)
                    continue;
                if (slide.TranslationLimit == null) continue;
                if (driver.Type != JointType.Revolute) continue;
                if (driver.RotationLimit != null) continue;   // its own limit wins
                if (driver.Axis == null || driver.Origin == null) continue;
                // The closed form is a PLANAR one: the driver has to turn in
                // the mechanism's own plane for the triangle to stay a triangle.
                if (Math.Abs(MathOps.Dot(MathOps.Normalized(driver.Axis),
                                         MathOps.Normalized(lp.PlaneNormal))) < 0.999)
                    continue;

                RigJoint mountP, mountC;
                if (!mountOf.TryGetValue(slide.ParentGroup, out mountP)) continue;
                if (!mountOf.TryGetValue(slide.ChildGroup, out mountC)) continue;
                if (mountP.Origin == null || mountC.Origin == null) continue;

                // Of the slide's two mounts, the one hanging off the driver's
                // own body is C: the corner that swings when the driver is
                // posed. Without that the triangle has no fixed side.
                double[] a, c;
                // ...and whether the corner that swings is the slide's own
                // CHILD, which is the end the stroke measures.
                bool childAtC;
                if (mountC.ParentGroup == driver.ChildGroup)
                { a = mountP.Origin; c = mountC.Origin; childAtC = true; }
                else if (mountP.ParentGroup == driver.ChildGroup)
                { a = mountC.Origin; c = mountP.Origin; childAtC = false; }
                else continue;

                var derived = SliderDriverLimit(
                    driver, a, c, slide.Axis, childAtC, slide.TranslationLimit);
                if (derived == null) continue;
                driver.RotationLimit = derived;
                result.DerivedLimitJoints.Add(driver.Id);
                driver.Notes = AppendLoopNote(driver.Notes,
                    "Rotation limit derived from the stroke limit of " + slide.Id
                    + " through the loop triangle: in SolidWorks this joint is "
                    + "stopped by that slide reaching its end, not by a limit "
                    + "mate of its own.");
            }
        }

        private static JointLimit SliderDriverLimit(
            RigJoint driver, double[] aPoint, double[] cPoint,
            double[] slideAxis, bool childAtC, JointLimit stroke)
        {
            var n = MathOps.Normalized(driver.Axis);
            var u = FlattenOnto(Minus(aPoint, driver.Origin), n);   // B -> A
            var v = FlattenOnto(Minus(cPoint, driver.Origin), n);   // B -> C
            double sideA = MathOps.Norm(u), sideC = MathOps.Norm(v);
            if (sideA < 1e-9 || sideC < 1e-9) return null;

            var span = Minus(v, u);                                 // A -> C
            double rest = MathOps.Norm(span);                       // |AC| now
            if (rest < 1e-9) return null;

            // A stroke is a SIGNED displacement of the slide's child along the
            // slide's own axis; the ram's LENGTH is |AC|. The two agree only
            // when that axis points from A towards C. On the mirrored hand of
            // a machine it points the other way, and a stroke that lengthens
            // the ram reads as a falling coordinate. Without this the two
            // hands of live ClampRig derived clamp limits of +90 and
            // +38 degrees from the same triangle and the same 500 mm stroke
            // (2026-08-24).
            double sign = 1.0;
            if (slideAxis != null)
            {
                double along = MathOps.Dot(
                    MathOps.Normalized(FlattenOnto(slideAxis, n)),
                    MathOps.Normalized(span));
                if (!childAtC) along = -along;
                if (along < 0.0) sign = -1.0;
            }

            double lo = rest + sign * (stroke.Min - stroke.ValueAtRest);
            double hi = rest + sign * (stroke.Max - stroke.ValueAtRest);
            if (hi < lo) { double swap = lo; lo = hi; hi = swap; }

            // A triangle only closes between |a−c| and a+c. A stroke reaching
            // past either is a stroke the linkage stops first.
            double shortest = Math.Abs(sideA - sideC) + 1e-9;
            double longest = sideA + sideC - 1e-9;
            if (longest <= shortest) return null;
            lo = Math.Min(Math.Max(lo, shortest), longest);
            hi = Math.Min(Math.Max(hi, shortest), longest);
            if (hi - lo < 1e-9) return null;

            double atRest = Corner(sideA, sideC, rest);
            double atLo = Corner(sideA, sideC, lo);
            double atHi = Corner(sideA, sideC, hi);

            // Which way the driver turns as the slide lengthens: the SIGNED
            // angle from B->A to B->C about the axis grows with the driver's
            // own rotation, and the corner angle grows with |AC|.
            double signed = Math.Atan2(
                MathOps.Dot(n, MathOps.Cross(u, v)), MathOps.Dot(u, v));
            double sense = signed < 0 ? -1.0 : 1.0;

            double d1 = sense * (atLo - atRest);
            double d2 = sense * (atHi - atRest);
            var limit = new JointLimit();
            limit.Min = Math.Min(d1, d2);
            limit.Max = Math.Max(d1, d2);
            // The values ARE displacements from rest: there is no SolidWorks
            // dimension behind this one.
            limit.ValueAtRest = 0.0;
            return limit;
        }

        /// <summary>The corner angle opposite the side named, in a triangle
        /// whose other two sides are given.</summary>
        private static double Corner(double sideA, double sideC, double opposite)
        {
            double cos = (sideA * sideA + sideC * sideC - opposite * opposite)
                       / (2.0 * sideA * sideC);
            if (cos > 1.0) cos = 1.0;
            else if (cos < -1.0) cos = -1.0;
            return Math.Acos(cos);
        }

        private static double[] Minus(double[] p, double[] q)
        {
            return new[] { p[0] - q[0], p[1] - q[1], p[2] - q[2] };
        }

        /// <summary>Drops the component along the axis: the mechanism moves in
        /// the plane, and the two mounts need not sit at the same height on
        /// their pins.</summary>
        private static double[] FlattenOnto(double[] v, double[] axis)
        {
            double along = MathOps.Dot(v, axis);
            return new[] { v[0] - along * axis[0],
                           v[1] - along * axis[1],
                           v[2] - along * axis[2] };
        }

        private static string AppendLoopNote(string notes, string add)
        {
            return string.IsNullOrEmpty(notes) ? add : notes + " " + add;
        }

        /// <summary>
        /// The ring's only sliding edge, or −1 when it has none or more than
        /// one. One slide is a slider-crank and has an exact closed form; two
        /// or more is a mechanism this analyzer does not claim to plan, and
        /// the general rule takes it.
        /// </summary>
        private static int SoleSlideIndex(Ring ring)
        {
            int found = -1;
            for (int i = 0; i < ring.Edges.Count; i++)
            {
                if (!IsSlide(ring.Edges[i])) continue;
                if (found >= 0) return -1;
                found = i;
            }
            if (found >= 0) return found;

            // No prismatic in the ring. A ram whose rod may still SPIN in
            // its barrel arrives cylindrical, because the loop narrowing
            // that kills the spin runs after closures are chosen: live
            // TongRig (2026-09-14): a cone bore on a rod with a
            // point-to-point stroke limit, in a ring of hinge pins. Read as
            // "no slide", the ram was closed with IK on one arm, its own
            // two joints stayed free inputs, and its length never changed.
            //
            // A cylindrical is only a ram's stroke when its axis passes
            // THROUGH both of its mounts: the rod eye is centred on the
            // mount pin, so the stroke line and the pin line meet. A loose
            // pin in a four-bar is cylindrical too, but its axis runs
            // parallel to the neighbouring pins a bar length away, and the
            // ClampRig's rod eye on its clamp pin is a cylindrical whose axis is
            // the pin, square to the stroke. Neither meets both neighbours.
            //
            // Only a second cylindrical that ALSO meets both its neighbours
            // makes the ring ambiguous. The rod eye's own mount pin is a
            // cylindrical too when it is mated by a concentric alone (live
            // TongRig on the current DLL, 2026-09-14), and it fails the
            // test, parallel to the arm pin beside it; met before the
            // stroke it must not hide the stroke, met after it must not
            // veto it.
            int n = ring.Edges.Count;
            if (n < 4) return -1;
            for (int i = 0; i < n; i++)
            {
                var j = ring.Edges[i];
                if (j.Type != JointType.Cylindrical) continue;
                var before = ring.Edges[(i + n - 1) % n];
                var after = ring.Edges[(i + 1) % n];
                if (!AxisMeets(j, before) || !AxisMeets(j, after)) continue;
                if (found >= 0) return -1;
                found = i;
            }
            return found;
        }

        /// <summary>Whether two joints' axis lines cross: not parallel, and
        /// within a tenth of a millimetre of meeting. Both need an axis and
        /// a point on it.</summary>
        private static bool AxisMeets(RigJoint a, RigJoint b)
        {
            if (a.Axis == null || a.Origin == null || b.Axis == null || b.Origin == null)
                return false;
            var da = MathOps.Normalized(a.Axis);
            var db = MathOps.Normalized(b.Axis);
            var normal = MathOps.Cross(da, db);
            double len = MathOps.Norm(normal);
            if (len < 1e-6) return false;                 // parallel: never meet
            var between = new[] { b.Origin[0] - a.Origin[0],
                                  b.Origin[1] - a.Origin[1],
                                  b.Origin[2] - a.Origin[2] };
            double gap = Math.Abs(MathOps.Dot(between, normal)) / len;
            return gap < 1e-4;
        }

        private static bool IsSlide(RigJoint j)
        {
            return j.Type == JointType.Prismatic || j.Type == JointType.Screw;
        }

        /// <summary>
        /// The pin a hand would pose to work a slider-crank: an edge touching
        /// NEITHER of the sliding bodies, so that posing it changes the
        /// distance the slide has to make up. An edge on a sliding body is no
        /// good: it is one of the two the aim-pair drives. Anchor-incident
        /// first (the operator poses against ground), then a limited joint,
        /// then a revolute, then id order so a re-export reads the same.
        /// Null when the ring is too tight for such an edge to exist.
        /// </summary>
        private static RigJoint DriverClearOfSlide(
            Ring ring, int slide, HashSet<string> chosen = null)
        {
            int n = ring.Edges.Count;
            int a = ring.Groups[slide], b = ring.Groups[(slide + 1) % n];
            int anchor = ring.Groups[0];
            RigJoint best = null;
            bool bestAnchored = false, bestLimited = false, bestRevolute = false;
            bool bestChosen = false;
            for (int i = 0; i < n; i++)
            {
                if (i == slide) continue;
                int u = ring.Groups[i], v = ring.Groups[(i + 1) % n];
                if (u == a || u == b || v == a || v == b) continue;
                var j = ring.Edges[i];
                bool already = chosen != null && chosen.Contains(j.Id);
                bool anchored = u == anchor || v == anchor;
                bool limited = HasLimits(j);
                bool revolute = j.Type == JointType.Revolute;
                if (best != null)
                {
                    if (already != bestChosen) { if (!already) continue; }
                    else if (anchored != bestAnchored) { if (!anchored) continue; }
                    else if (limited != bestLimited) { if (!limited) continue; }
                    else if (revolute != bestRevolute) { if (!revolute) continue; }
                    else if (string.CompareOrdinal(j.Id, best.Id) > 0) continue;
                }
                best = j;
                bestChosen = already;
                bestAnchored = anchored;
                bestLimited = limited;
                bestRevolute = revolute;
            }
            return best;
        }

        /// <summary>
        /// A cut the consumer can actually close. The closure is a point
        /// coincidence (two bodies pinned back together at one spot), so the
        /// cut joint has to be a joint whose bodies SHARE a point. A prismatic
        /// shares none: cutting there asks the consumer to hold two bodies
        /// together at a point that slides, and it leaves the slide itself
        /// outside the solved chain, where nothing can change it.
        ///
        /// Live ClampRig (2026-08-24) had one of each. The machine's two
        /// hydraulic rams are mirror images, but on one side the tie broke to
        /// a pin cut (clamp drives, ram swings) and on the other to the ram's
        /// own stroke, which froze that clamp solid, because the clamp then
        /// sat inside the driven chain instead of being the input. Nothing but
        /// joint-id order separated them.
        /// </summary>
        private static bool IsPinCut(RigJoint j)
        {
            return j.Type == JointType.Revolute
                || j.Type == JointType.Cylindrical
                || j.Type == JointType.Ball;
        }

        /// <summary>Whether the two bodies of this cut share a point, which
        /// is the only thing an IK closure can re-join them by.</summary>
        private static bool CutSharesAPoint(RigJoint j)
        {
            return IsPinCut(j) || j.Type == JointType.Fixed;
        }

        private static bool HasLimits(RigJoint j)
        {
            return j.RotationLimit != null || j.TranslationLimit != null;
        }

        /// <summary>A loop is planar when every revolute member spins about
        /// the same direction (within 1e-6); the Blender side can then keep
        /// its IK in one plane.</summary>
        /// <summary>
        /// How many inputs the ring takes: Gruebler on one planar loop, the
        /// members' freedom less the three the closure spends.
        ///
        /// Counted only where each member's contribution is plain, which is
        /// a PLANAR ring built from pins about its normal and slides in its
        /// plane. Anything else answers 1, the answer this gave everywhere
        /// before: a ring is far better short of a control than short of a
        /// constraint.
        ///
        /// Live wrench.sldasm (2026-09-16, Oscar): "the arm2 assembly can
        /// rotate around a pin on clamp2, but the only control bone I am
        /// getting in Blender is the screw". Four pins and a screw around
        /// one ring is a five-bar: five freedoms, three spent, two inputs.
        /// The rig solved all three of the driven bodies from the screw
        /// alone and the second freedom had nowhere to go.
        /// </summary>
        private static int Mobility(RigLoop loop, List<RigJoint> members)
        {
            if (!loop.Planar || loop.PlaneNormal == null) return 1;
            var n = MathOps.Normalized(loop.PlaneNormal);
            int freedom = 0;
            foreach (var j in members)
            {
                if (j.Type == JointType.Fixed) continue;
                if (j.Axis == null) return 1;
                var a = MathOps.Normalized(j.Axis);
                double along = Math.Abs(MathOps.Dot(a, n));
                if (j.Type == JointType.Revolute)
                {
                    // A pin about the ring's normal turns in the plane.
                    if (along < 1.0 - 1e-6) return 1;
                    freedom++;
                }
                else if (j.Type == JointType.Prismatic || j.Type == JointType.Screw)
                {
                    // A slide in the plane travels in the plane. A screw
                    // brings its spin with it, about that same in-plane
                    // line, and a spin about an in-plane line moves nothing
                    // in the plane: one freedom either way.
                    if (along > 1e-6) return 1;
                    freedom++;
                }
                else
                {
                    return 1;   // a freedom this count cannot read plainly
                }
            }
            return Math.Max(1, freedom - 3);
        }

        /// <summary>
        /// A planar joint in a ring that moves in that plane anyway: every
        /// hinge of the ring turns about the planar's normal, every slide
        /// runs square to it, and nothing else is in the ring but welds and
        /// other such planars. Null when there is none, or when a joint of
        /// the ring could leave the plane. The lowest id wins when there are
        /// several.
        /// </summary>
        private static RigJoint RedundantPlanar(List<RigJoint> edges)
        {
            double[] normal = null;
            foreach (var j in edges)
            {
                if (j.Type != JointType.Revolute) continue;
                if (j.Axis == null) return null;
                if (normal == null) { normal = MathOps.Normalized(j.Axis); continue; }
                if (MathOps.Norm(MathOps.Cross(normal, MathOps.Normalized(j.Axis))) >= 1e-6)
                    return null;
            }
            if (normal == null) return null;
            RigJoint found = null;
            foreach (var j in edges)
            {
                switch (j.Type)
                {
                    case JointType.Revolute:
                    case JointType.Fixed:
                        break;
                    case JointType.Prismatic:
                        if (j.Axis == null
                            || Math.Abs(MathOps.Dot(normal, MathOps.Normalized(j.Axis))) >= 1e-6)
                            return null;
                        break;
                    case JointType.Planar:
                        if (j.Axis == null
                            || MathOps.Norm(MathOps.Cross(normal, MathOps.Normalized(j.Axis))) >= 1e-6)
                            return null;
                        if (found == null || string.CompareOrdinal(j.Id, found.Id) < 0) found = j;
                        break;
                    default:
                        return null;
                }
            }
            return found;
        }

        private static void SetPlanarity(RigLoop loop, List<RigJoint> members)
        {
            double[] normal = null;
            foreach (var j in members)
            {
                if (j.Type != JointType.Revolute || j.Axis == null) continue;
                if (normal == null) { normal = j.Axis; continue; }
                if (MathOps.Norm(MathOps.Cross(
                        MathOps.Normalized(normal), MathOps.Normalized(j.Axis))) >= 1e-6)
                {
                    loop.Planar = false;
                    loop.PlaneNormal = null;
                    return;
                }
            }
            loop.Planar = normal != null;
            loop.PlaneNormal = normal;
        }

        // ── Orientation ─────────────────────────────────────────────────────

        /// <summary>Parent is the end nearer the root. Tree joints follow the
        /// BFS discovery direction; closure joints orient by depth so their
        /// parent side is also rootward.</summary>
        private static void Orient(
            List<RigJoint> joints, int groupCount, List<RigJoint>[] adjacency,
            Dictionary<string, int> groupIndex, List<int> roots, HashSet<string> tree)
        {
            var walk = Bfs(groupCount, adjacency, groupIndex, roots, tree, null);
            foreach (var j in joints)
            {
                int a, b;
                if (!groupIndex.TryGetValue(j.ParentGroup, out a)) continue;
                if (!groupIndex.TryGetValue(j.ChildGroup, out b)) continue;
                if (a == b) continue;

                bool swap;
                if (tree.Contains(j.Id))
                    swap = ReferenceEquals(walk.ParentJoint[a], j);   // discovery entered at a
                else
                    swap = walk.Depth[a] >= 0 && walk.Depth[b] >= 0 && walk.Depth[b] < walk.Depth[a];

                if (swap)
                {
                    string t = j.ParentGroup;
                    j.ParentGroup = j.ChildGroup;
                    j.ChildGroup = t;
                }
            }
        }
    }
}
