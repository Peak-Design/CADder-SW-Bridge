using System.Collections.Generic;
using Peak.Cadder;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Peak.Cadder.Sw;
using Xunit;
using static Peak.Cadder.Tests.FixtureBuilder;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The division of labour between the mate analysis and the SolidWorks
    /// solver. The mates say who hangs off whom: the one thing the API will
    /// not answer, because "how much freedom does this have" is always
    /// relative to ground and never to a parent you have yet to choose. The
    /// solver says what each connection actually is, having solved the whole
    /// assembly at once instead of one pair at a time.
    /// </summary>
    public class SolverVerdictTests
    {
        private static RigJoint Prismatic()
        {
            return new RigJoint
            {
                Id = "j001",
                Type = JointType.Prismatic,
                ParentGroup = "g000",
                ChildGroup = "g001",
                Axis = new double[] { 1, 0, 0 },
                SecondaryAxis = new double[] { 0, 0, 1 },
                Origin = new double[] { 0.1, 0, 0 },
                TranslationLimit = new JointLimit { Min = 0, Max = 0.05, ValueAtRest = 0.02 },
            };
        }

        /// <summary>The whole point: when both name a primitive and they
        /// differ, the solver's freedom replaces the inferred one.</summary>
        [Fact]
        public void ASolverPrimitiveReplacesTheInferredOne()
        {
            var j = Prismatic();
            JointClassifier.AdoptSolverVerdict(
                j, JointType.Revolute, new double[] { 0, 0, 1 }, new double[] { 0.2, 0, 0 });

            Assert.Equal(JointType.Revolute, j.Type);
            Assert.Equal(new double[] { 0, 0, 1 }, j.Axis);
            Assert.Equal(new double[] { 0.2, 0, 0 }, j.Origin);
            Assert.NotNull(j.SecondaryAxis);
            Assert.Equal(0.0, MathOps.Dot(j.Axis, j.SecondaryAxis), 12);
        }

        /// <summary>A limit is measured about a specific axis. Moved onto a
        /// freedom the new type does not have, it is no longer about anything,
        /// so it goes, and the note says so rather than leaving a number in
        /// the file that means nothing.</summary>
        [Fact]
        public void ALimitOnAFreedomTheNewTypeLacksIsDropped()
        {
            var j = Prismatic();
            string note = JointClassifier.AdoptSolverVerdict(
                j, JointType.Revolute, new double[] { 1, 0, 0 }, null);

            Assert.Null(j.TranslationLimit);
            Assert.Contains("translation limit", note);
        }

        /// <summary>Same line, and the new type still has the freedom: the
        /// limit survives. A revolute that was really a cylindrical keeps the
        /// angle range it was always about.</summary>
        [Fact]
        public void ALimitOnTheSameAxisSurvives()
        {
            var j = new RigJoint
            {
                Id = "j001",
                Type = JointType.Revolute,
                Axis = new double[] { 0, 0, 1 },
                SecondaryAxis = new double[] { 1, 0, 0 },
                Origin = new double[3],
                RotationLimit = new JointLimit { Min = -1, Max = 1, ValueAtRest = 0 },
            };
            JointClassifier.AdoptSolverVerdict(
                j, JointType.Cylindrical, new double[] { 0, 0, 1 }, new double[3]);

            Assert.Equal(JointType.Cylindrical, j.Type);
            Assert.NotNull(j.RotationLimit);
        }

        /// <summary>The probe reports a ball as a point with no single axis,
        /// and the schema says an unlimited ball carries none.</summary>
        [Fact]
        public void ABallVerdictClearsTheAxis()
        {
            var j = Prismatic();
            JointClassifier.AdoptSolverVerdict(
                j, JointType.Ball, null, new double[] { 0, 0, 0.05 });

            Assert.Equal(JointType.Ball, j.Type);
            Assert.Null(j.Axis);
            Assert.Null(j.SecondaryAxis);
            Assert.Equal(new double[] { 0, 0, 0.05 }, j.Origin);
        }

        /// <summary>The axis sign stays canonical after an adoption. The probe
        /// reports whichever sense the solver happened to hand back, and a
        /// consumer's bone must not flip because of that (SCHEMA.md: the sign
        /// is a pure function of the LINE).</summary>
        [Fact]
        public void TheAdoptedAxisIsStillCanonical()
        {
            var a = Prismatic();
            var b = Prismatic();
            JointClassifier.AdoptSolverVerdict(a, JointType.Revolute,
                new double[] { 0.6, 0.8, 0 }, null);
            JointClassifier.AdoptSolverVerdict(b, JointType.Revolute,
                new double[] { -0.6, -0.8, 0 }, null);

            Assert.Equal(a.Axis[0], b.Axis[0], 12);
            Assert.Equal(a.Axis[1], b.Axis[1], 12);
            Assert.Equal(a.Axis[2], b.Axis[2], 12);
        }

        /// <summary>Only the primitives the probe has a vocabulary for. Path,
        /// surface, pin_slot and screw are things it reads freedoms past: it
        /// cannot see a curve, a mesh, or the coupling between two DOFs.</summary>
        [Theory]
        [InlineData(JointType.Revolute, true)]
        [InlineData(JointType.Prismatic, true)]
        [InlineData(JointType.Cylindrical, true)]
        [InlineData(JointType.Planar, true)]
        [InlineData(JointType.Ball, true)]
        [InlineData(JointType.Screw, false)]
        [InlineData(JointType.PinSlot, false)]
        [InlineData(JointType.Path, false)]
        [InlineData(JointType.Surface, false)]
        [InlineData(JointType.Free, false)]
        [InlineData(JointType.Fixed, false)]
        public void OnlyProbeVocabularyIsAdoptable(string type, bool adoptable)
        {
            Assert.Equal(adoptable, JointClassifier.IsSolverPrimitive(type));
        }

        /// <summary>
        /// Oscar's question, 2026-08-24, and the live answer. SolidWorks
        /// counts a limit mate's RANGE as a constraint, so a hydraulic ram
        /// free to extend 300 mm reports fully defined: confirmed on
        /// ClampRig, where the lead screw rod and four slot-mated
        /// blocks all read fully defined while moving.
        ///
        /// Nothing is welded on that status any more (it means "no freedom of
        /// its OWN", not "cannot move"), but the mate types still matter: they
        /// are what tells the DOF probe when its own reading is unusable,
        /// since the probe suppresses limit mates and nothing else.
        /// </summary>
        [Fact]
        public void ALimitMateLeavesTheProbeUnableToSeePastIt()
        {
            var limit = DistanceLimit("LimitDistance1", "c001", "c002", Z, P(0, 0, 0),
                min: 0.0, max: 0.3, current: 0.1);
            Assert.True(MateFacts.PermitsMotion(limit));
            Assert.True(MateFacts.IsLimitMate(limit));
        }

        /// <summary>
        /// The follower. The live cutting head is mated to the machine body
        /// AND to the lead screw rod: it has no freedom of its own, so the
        /// probe reads it fixed, and it slides half a metre because the rod
        /// does. The rod link was never read fixed, so the verdict on the
        /// body/carriage pair cannot be acted on.
        /// </summary>
        [Fact]
        public void AChildFollowingABodyTheProbeDidNotWeldIsNotBelievable()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "body", isFixed: true),
                    Comp("c002", "rod"),
                    Comp("c003", "carriage"),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c003", X, P(0, 0, 0)),
                CoincidentPlanes("Coincident2", "c002", "c003", Z, P(0, 0, 0.4)));
            var grouping = RigidGrouper.Group(graph);
            string body = grouping.ComponentGroup["c001"];
            string carriage = grouping.ComponentGroup["c003"];
            string rod = grouping.ComponentGroup["c002"];

            // The probe read the carriage fixed against the body, and said
            // nothing rigid about the rod.
            var welds = new SolverWelds(
                graph, grouping, new[] { new[] { body, carriage } });

            Assert.False(welds.Believable(body, carriage));
            Assert.False(welds.Believable(body, rod));
        }

        /// <summary>
        /// The bolted cluster, and the reason a per-pair test cannot do this
        /// job. Live ClampRig bolts its buoyancy module on with nine
        /// M16s: every bolt is mated to the machine body AND to the module,
        /// so no single pair is rigid by its mates, but the probe reads
        /// every pair of the cluster fixed, and the cluster is one body. The
        /// third body here is inside the same run of fixed verdicts, which is
        /// exactly what the cutting head's is not.
        /// </summary>
        [Fact]
        public void AClusterTheProbeReadsRigidThroughoutIsBelievable()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "body", isFixed: true),
                    Comp("c002", "bolt"),
                    Comp("c003", "module"),
                },
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident2", "c001", "c003", X, P(0, 0, 0)),
                CoincidentPlanes("Coincident3", "c002", "c003", Z, P(0, 0, 0.02)));
            var grouping = RigidGrouper.Group(graph);
            string body = grouping.ComponentGroup["c001"];
            string bolt = grouping.ComponentGroup["c002"];
            string module = grouping.ComponentGroup["c003"];

            var welds = new SolverWelds(graph, grouping, new[]
            {
                new[] { body, bolt },
                new[] { body, module },
                new[] { bolt, module },
            });

            Assert.True(welds.Believable(body, bolt));
            Assert.True(welds.Believable(body, module));
            Assert.True(welds.Believable(bolt, module));
        }

        /// <summary>And when there IS nothing else mated to the child, the
        /// reading means what it appears to.</summary>
        [Fact]
        public void APairWithNoOutsideMateIsBelievable()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "body", isFixed: true),
                    Comp("c002", "cover"),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)));
            var grouping = RigidGrouper.Group(graph);
            string body = grouping.ComponentGroup["c001"];
            string cover = grouping.ComponentGroup["c002"];

            var welds = new SolverWelds(
                graph, grouping, new[] { new[] { body, cover } });

            Assert.True(welds.Believable(body, cover));
        }

        /// <summary>Mates that permit motion whatever the solver's accounting
        /// says. Types and ranges only: no names, no file paths.</summary>
        [Theory]
        [InlineData("swMatePATH", true)]
        [InlineData("swMateGEAR", true)]
        [InlineData("swMateSCREW", true)]
        [InlineData("swMateRACKPINION", true)]
        [InlineData("swMateCAMFOLLOWER", true)]
        [InlineData("swMateLINEARCOUPLER", true)]
        [InlineData("swMateUNIVERSALJOINT", true)]
        [InlineData("swMateTANGENT", true)]
        [InlineData("swMateHINGE", true)]
        [InlineData("swMateSLIDER", true)]
        [InlineData("swMateCONCENTRIC", false)]
        [InlineData("swMateCOINCIDENT", false)]
        [InlineData("swMateLOCK", false)]
        [InlineData("swMateWIDTH", false)]
        public void MotionPermittingMateTypes(string typeName, bool permits)
        {
            var m = Mate("M1", typeName, PlaneEnt("c001", Z, P(0, 0, 0)));
            Assert.Equal(permits, MateFacts.PermitsMotion(m));
        }

        /// <summary>Only the FREE option leaves the pin sliding. "Centered in
        /// slot" pins it at the midpoint just as firmly as a distance does:
        /// live ClampRigPart (2026-08-24) reported constraint=1 and is fully
        /// defined in SolidWorks.</summary>
        [Theory]
        [InlineData(0, true)]
        [InlineData(1, false)]
        [InlineData(2, false)]
        [InlineData(3, false)]
        [InlineData(-1, true)]
        public void SlotMatesPermitMotionUnlessPinnedAlongTheSlot(
            int constraint, bool permits)
        {
            var m = Mate("Slot1", "swMateSLOT", PlaneEnt("c001", Z, P(0, 0, 0)));
            m.SlotConstraint = constraint;
            Assert.Equal(permits, MateFacts.PermitsMotion(m));
        }

        /// <summary>The solver can also see rigidity the mates cannot: three
        /// bodies can pin each other in a way no single PAIR reveals. When the
        /// probe reports no relative freedom, the pair is one body.</summary>
        [Fact]
        public void ASolverRigidPairMergesOnTheSecondPass()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "base", isFixed: true),
                    Comp("c002", "link"),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0.01)));

            Assert.Equal(2, RigidGrouper.Group(graph).Groups.Count);

            var merged = RigidGrouper.Group(
                graph, new[] { new[] { "c001", "c002" } });

            Assert.Single(merged.Groups);
            Assert.Equal(new[] { "c001", "c002" }, merged.Groups[0].Components);
            Assert.Empty(merged.Edges);
        }

        // ── What a status value means ───────────────────────────────────────
        //
        // GetRemainingDOFs fills four slots and returns a status. swconst
        // declares the vocabulary (swDofStatus_e) and nothing in the interop
        // is typed with it, so it is easy to mistake for a boolean:
        //
        //   0 Unused  1 Static  2 StaticNormal  3 Free  5 Instantaneous
        //
        // Only Unused means "nothing here". Reading 2, 3 and 5 as absence is
        // what welded ten corpus assemblies solid on 2026-08-25: every one
        // of them a pair SolidWorks itself calls under-defined.

        [Theory]
        // A puck lying flat on a plate: a rotation with no unique centre, and
        // two translations described by the normal of the plane they span.
        [InlineData(3, 0, 2, 0, 0, false, "planar1/slider2/sym2/pt1/dist3/dist4")]
        // A vertex on a cylinder, and a disc orbiting at a fixed distance:
        // translation along a curve, true at this pose only.
        [InlineData(3, 0, 5, 0, 0, false, "pt3/dist1/tangent2")]
        // One perpendicular mate: two rotations, neither with a fixed point.
        [InlineData(3, 3, 0, 0, 0, false, "perp3")]
        // A parallelogram coupler: it translates along an arc without
        // turning. Described exactly, then welded.
        [InlineData(0, 0, 5, 0, 0, false, "parallelogram2 g000/g001")]
        // Genuinely nothing: every slot empty and the call satisfied. This is
        // what all 33 of live ClampRig's rigid verdicts look like.
        [InlineData(0, 0, 0, 0, 0, true, "a real weld")]
        // Empty slots but the call could not answer (Unavailable): the
        // reading itself is unusable.
        [InlineData(0, 0, 0, 0, 2, false, "ball2, and the ClampRig's adrift batch")]
        public void OnlyAnEmptySlotMeansNoFreedom(
            int rPoint1, int rPoint2, int tDir1, int tDir2, int remaining,
            bool weldable, string seenOn)
        {
            Assert.Equal(weldable, DofProbe.IsWeldable(
                rPoint1, rPoint2, tDir1, tDir2, remaining));
            Assert.False(string.IsNullOrEmpty(seenOn));
        }

        /// <summary>
        /// The probe cannot tell a ball from a hinge, and must not try.
        ///
        /// A ball has three rotations about a fixed centre. The call has two
        /// rotation slots, and it reports the CENTRE as Static with an
        /// arbitrary direction, which is indistinguishable from a hinge
        /// unless the direction status is read. Live corpus 04 (2026-08-25):
        /// all three ball assemblies came back "revolute [Rpoint1=Static]"
        /// and were adopted, and the studs stopped tumbling.
        /// </summary>
        [Fact]
        public void ARotationWhoseDirectionIsNotStaticIsNotNamed()
        {
            const int Unused = 0, Static = 1, Free = 3;

            // A hinge: the point AND the direction are both pinned.
            Assert.True(DofProbe.IsCharacterised(
                rPoint1: Static, rDir1: Static, rPoint2: Unused, rDir2: Unused,
                tDir1: Unused, tDir2: Unused, remaining: 0));

            // A ball: a fixed centre, and no particular axis. Whatever vector
            // came back in that slot is not an axis to build a hinge on.
            Assert.False(DofProbe.IsCharacterised(
                rPoint1: Static, rDir1: Free, rPoint2: Unused, rDir2: Unused,
                tDir1: Unused, tDir2: Unused, remaining: 0));
            Assert.False(DofProbe.IsCharacterised(
                rPoint1: Static, rDir1: Unused, rPoint2: Unused, rDir2: Unused,
                tDir1: Unused, tDir2: Unused, remaining: 0));

            // A slide has no point status to disagree with.
            Assert.True(DofProbe.IsCharacterised(
                rPoint1: Unused, rDir1: Unused, rPoint2: Unused, rDir2: Unused,
                tDir1: Static, tDir2: Unused, remaining: 0));
            // ...but a translation the solver would not pin down is not named.
            Assert.False(DofProbe.IsCharacterised(
                rPoint1: Unused, rDir1: Unused, rPoint2: Unused, rDir2: Unused,
                tDir1: 2, tDir2: Unused, remaining: 0));
        }

        /// <summary>
        /// A rigid verdict that was REFUSED as a weld must not come back as a
        /// warning against the joint that was right all along.
        ///
        /// It can only reach the reporting stage by having been refused: a
        /// trusted weld merges the pair, and then no joint spans it to
        /// disagree with. So every rigid verdict that arrives is the artefact
        /// the ground-parent rule exists to discount: the probe pinned a
        /// moving body and the mechanism froze. Live corpus 06 (2026-08-25):
        /// with the four-bar's grouping finally correct, two of its four
        /// correct revolutes would have shipped at "low" confidence carrying
        /// "the SolidWorks DOF probe reports fixed", against a README whose
        /// acceptance criterion is no warnings at all.
        /// </summary>
        [Fact]
        public void ARefusedWeldIsNotReportedAgainstTheJoint()
        {
            var pin = new RigJoint
            {
                Id = "j003",
                Type = JointType.Revolute,
                ParentGroup = "g001",
                ChildGroup = "g002",
                Axis = new double[] { 0, 0, 1 },
                SecondaryAxis = new double[] { 1, 0, 0 },
                Origin = new double[] { 0.08, 0, 0.0075 },
            };

            Assert.False(ExportCommand.DisagreementIsWorthReporting(
                pin, JointType.Fixed, characterised: true));
            // A real disagreement between two things it CAN see still carries.
            Assert.True(ExportCommand.DisagreementIsWorthReporting(
                pin, JointType.Prismatic, characterised: true));
            // ...and an unnamed reading never does.
            Assert.False(ExportCommand.DisagreementIsWorthReporting(
                pin, JointType.Prismatic, characterised: false));
            // A ball is invisible to this API, so it is never argued with.
            var ball = new RigJoint { Id = "j001", Type = JointType.Ball };
            Assert.False(ExportCommand.DisagreementIsWorthReporting(
                ball, JointType.Revolute, characterised: true));
        }

        /// <summary>
        /// `Fixed` MEANS weldable, and the two are decided in one place.
        ///
        /// The gap was a Static status whose COM vector came back null: every
        /// freedom flag then reads false, and the mapper's "nothing here"
        /// branch used to answer Fixed whenever the call had returned
        /// Restricted: welding a hinge on a reading IsWeldable had already
        /// refused. The mapper is only entered once IsWeldable has said no,
        /// so it may never say Fixed.
        /// </summary>
        [Fact]
        public void OnlyAnEmptyReadingCanBeRigid()
        {
            // The shape that used to slip through: a rotation the solver DID
            // describe, so nothing about it is weldable.
            Assert.False(DofProbe.IsWeldable(
                rPoint1: DofProbe.StatusStatic, rPoint2: 0,
                tDir1: 0, tDir2: 0, remaining: 0));
            Assert.True(DofProbe.IsCharacterised(
                rPoint1: DofProbe.StatusStatic, rDir1: DofProbe.StatusStatic,
                rPoint2: 0, rDir2: 0, tDir1: 0, tDir2: 0, remaining: 0));
        }

        /// <summary>
        /// The SolidWorks 2022 tutorial claw-mechanism.sldasm (2026-09-15):
        /// a collar sliding on the centre, a claw hinged on the centre, a
        /// con-rod pinned to both. The probe read the con-rod's pins with
        /// the collar pinned, which holds the whole loop rigid, so the only
        /// freedom left was the con-rod's slop along its parallel pins: two
        /// "prismatic" readings, adopted, and the ring then welded the
        /// collar's slide. A reading taken with a moving body pinned may not
        /// narrow a pair on a loop; a pair off every loop (a bridge) keeps
        /// its own freedom whatever is pinned, and the ground may always be
        /// pinned.
        /// </summary>
        [Fact]
        public void APinnedReadingOfALoopPairDoesNotNarrowIt()
        {
            RigJoint J(string id, string type, string p, string c)
            {
                return new RigJoint
                {
                    Id = id, Type = type, ParentGroup = p, ChildGroup = c,
                    Axis = new double[] { 0, 0, 1 }, Origin = new double[3],
                };
            }
            var joints = new List<RigJoint>
            {
                J("j001", JointType.Prismatic, "g000", "g001"),    // centre -> collar
                J("j002", JointType.Revolute, "g000", "g002"),     // centre -> claw
                J("j003", JointType.Cylindrical, "g001", "g003"),  // collar -> con-rod
                J("j004", JointType.Cylindrical, "g002", "g003"),  // claw -> con-rod
                J("j005", JointType.Cylindrical, "g003", "g004"),  // con-rod -> a pin on it
            };
            // On the loop, read with the collar pinned: locked.
            Assert.True(ExportCommand.PinnedReadingIsLoopLocked(joints, joints[2], "g001", "g000"));
            Assert.True(ExportCommand.PinnedReadingIsLoopLocked(joints, joints[3], "g002", "g000"));
            // On the loop, read with the ground pinned: the reading stands.
            Assert.False(ExportCommand.PinnedReadingIsLoopLocked(joints, joints[0], "g000", "g000"));
            // Off every loop: its own freedom, whatever is pinned.
            Assert.False(ExportCommand.PinnedReadingIsLoopLocked(joints, joints[4], "g003", "g000"));
        }

        /// <summary>Belt and braces on the same fact, at the level where the
        /// verdict is applied: a ball is never overruled, whatever the probe
        /// thinks it saw, because two rotation slots cannot hold three
        /// rotations.</summary>
        [Fact]
        public void ABallIsNeverOverruledByTheSolver()
        {
            var ball = new RigJoint
            {
                Id = "j001",
                Type = JointType.Ball,
                ParentGroup = "g000",
                ChildGroup = "g001",
                Origin = new double[] { 0, 0, 0.02 },
            };

            Assert.False(ExportCommand.VerdictMayOverrule(
                ball, JointType.Revolute, characterised: true, blind: false));
            // ...while an ordinary disagreement still carries.
            var cyl = new RigJoint
            {
                Id = "j002",
                Type = JointType.Cylindrical,
                ParentGroup = "g000",
                ChildGroup = "g001",
                Axis = new double[] { 0, 0, 1 },
                SecondaryAxis = new double[] { 1, 0, 0 },
                Origin = new double[3],
            };
            Assert.True(ExportCommand.VerdictMayOverrule(
                cyl, JointType.Revolute, characterised: true, blind: false));
            // An unnamed reading never overrules anything.
            Assert.False(ExportCommand.VerdictMayOverrule(
                cyl, JointType.Revolute, characterised: false, blind: false));
        }
    }
}
