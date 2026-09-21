using System;
using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;
using static Peak.Cadder.Tests.FixtureBuilder;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The three defects live ClampRig exposed in its hydraulic rams
    /// (2026-08-24). Every number here is off that assembly's own mate table
    /// as the debug log reported it, so these pin the actual failures rather
    /// than a reconstruction of them.
    ///
    /// The machine is a triangle per side: the ram's BORE end is pinned to the
    /// body, the clamp is pinned to the body further along, and the ram's ROD
    /// end drives the clamp's lug. Extending the ram therefore swings it, so
    /// the bore pivot is a joint, and losing it leaves a rig whose rods extend
    /// straight through the clamp.
    /// </summary>
    public class HydraulicRamTests
    {
        private static readonly double[] Yn = { 0, -1, 0 };
        private static readonly double[] Yp = { 0, 1, 0 };

        private static GraphComponent Flexible(GraphComponent c)
        {
            c.Solving = "flexible";
            return c;
        }

        /// <summary>
        /// The live ram linkage: a fixed machine body, two flexible ram
        /// subassemblies (each a node, a nested flexible node, an internally
        /// fixed barrel and a rod), and the top-level mates between them.
        /// </summary>
        private static MateGraph RamGraph(bool nodeReportsFixed)
        {
            var body = Comp("c054", "body", isFixed: true);
            var sub2 = Flexible(Comp("c057", "ram sub 2", isFixed: nodeReportsFixed));
            var inner2 = InSubFixed(Inside(Comp("c063", "ram 2"), "c057"));
            var barrel2 = InSubFixed(Inside(Comp("c065", "barrel 2"), "c063"));
            var rod2 = Inside(Comp("c064", "rod 2"), "c063");
            var sub1 = Flexible(Comp("c110", "ram sub 1", isFixed: nodeReportsFixed));
            var inner1 = InSubFixed(Inside(Comp("c117", "ram 1"), "c110"));
            var barrel1 = InSubFixed(Inside(Comp("c118", "barrel 1"), "c117"));
            var rod1 = Inside(Comp("c119", "rod 1"), "c117");

            return Graph(
                new[] { body, sub2, inner2, barrel2, rod2, sub1, inner1, barrel1, rod1 },
                // The two bore pivots. One concentric each, plus a width on
                // the left that centres the barrel between the clevis cheeks.
                Mate("Concentric328085765", "swMateCONCENTRIC",
                    Cylinder("c054", Yn, P(0.525, 0.046, -0.31568), 0.013),
                    Cylinder("c065", Yn, P(0.525, 0.045, -0.31568), 0.013)),
                Mate("Concentric328085764", "swMateCONCENTRIC",
                    Cylinder("c054", Yn, P(-0.525, 0.046, -0.31568), 0.013),
                    Cylinder("c118", Yp, P(-0.525, -0.045, -0.31568), 0.013)),
                Mate("Width53", "swMateWIDTH",
                    PlaneEnt("c054", Yp, P(-0.525, 0.066, -0.31568)),
                    PlaneEnt("c054", Yn, P(-0.525, -0.066, -0.31568)),
                    Cylinder("c118", new[] { 0.24581, 0.0, 0.96932 },
                             P(-0.5309, 0, -0.33895), 0.045)),
                // Both rams' own planes held on the machine's centre plane.
                Mate("Coincident558996035", "swMateCOINCIDENT",
                    PlaneEnt("c117", Yp, P(-0.61398, 0, -0.66658)),
                    PlaneEnt("c063", Yn, P(0.61398, 0, -0.66658))));
        }

        /// <summary>
        /// A FLEXIBLE subassembly node is not a rigid body. SolidWorks
        /// dissolves it and solves its children against the top assembly,
        /// so its Fix/Float flag cannot ground anything. Live ClampRig reported
        /// both rams (flexible) fixed while both clamps (rigid) were not; the
        /// flag pulled each barrel into ground through its node and the bore
        /// pivots vanished with it.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AFlexibleSubassemblyNodeNeverGroundsItsContents(bool nodeReportsFixed)
        {
            var grouping = RigidGrouper.Group(RamGraph(nodeReportsFixed));

            string ground = grouping.ComponentGroup["c054"];
            Assert.NotEqual(ground, grouping.ComponentGroup["c065"]);
            Assert.NotEqual(ground, grouping.ComponentGroup["c118"]);

            // Only the body's group is grounded, whatever the node claims.
            var grounded = new List<string>();
            foreach (var g in grouping.Groups) if (g.Grounded) grounded.Add(g.Id);
            Assert.Equal(new[] { ground }, grounded);

            // Each barrel stays welded to its own sub's nodes: the sub IS
            // rigid with whatever is fixed inside it, and keeps a live edge
            // to the body, which is the bore pivot.
            Assert.Equal(grouping.ComponentGroup["c065"], grouping.ComponentGroup["c063"]);
            Assert.Equal(grouping.ComponentGroup["c065"], grouping.ComponentGroup["c057"]);
            Assert.True(HasEdge(grouping, ground, grouping.ComponentGroup["c065"]));
            Assert.True(HasEdge(grouping, ground, grouping.ComponentGroup["c118"]));
        }

        /// <summary>But a ground there must be: when the ONLY fixed component
        /// in the assembly is a flexible subassembly node, it grounds after
        /// all, because the alternative is a rig of nothing but islands.</summary>
        [Fact]
        public void AFlexibleNodeStillGroundsWhenNothingElseCan()
        {
            var graph = Graph(
                new[]
                {
                    Flexible(Comp("c001", "rig sub", isFixed: true)),
                    InSubFixed(Inside(Comp("c002", "sub base"), "c001")),
                    Inside(Comp("c003", "arm"), "c001"),
                },
                Concentric("Concentric1", "c002", "c003", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c002", "c003", Z, P(0, 0, 0.01)));

            var grouping = RigidGrouper.Group(graph);

            string baseGroup = grouping.ComponentGroup["c002"];
            Assert.Equal(grouping.ComponentGroup["c001"], baseGroup);
            foreach (var g in grouping.Groups)
                Assert.Equal(g.Id == baseGroup, g.Grounded);
        }

        private static bool HasEdge(RigidGroupingResult g, string a, string b)
        {
            foreach (var e in g.Edges)
                if ((e.GroupA == a && e.GroupB == b) || (e.GroupA == b && e.GroupB == a))
                    return true;
            return false;
        }

        /// <summary>
        /// A concentric carries SolidWorks' own "lock rotation" tick
        /// (IConcentricMateFeatureData.LockRotation), and it kills the spin as
        /// well as the tilt. The live ram's grease nipples, dowty seals and
        /// BSP adapters are each held by one locked concentric plus one face
        /// coincident: fully defined in SolidWorks, and free to spin in
        /// Blender for as long as only PROFILECENTER mates were asked for the
        /// flag.
        /// </summary>
        [Fact]
        public void ALockedConcentricPlusAFaceContactIsOneBody()
        {
            var seal = Mate("Concentric91", "swMateCONCENTRIC",
                Cylinder("c065", Yp, P(0, 0.065, -0.231), 0.007475),
                Cylinder("c058", Yp, P(0, 0.065, -0.231), 0.007475));
            var face = CoincidentPlanes("Coincident71", "c065", "c058", Yp, P(0, 0.065, -0.231));

            var free = MotionResolver.Resolve(new List<GraphMate> { seal, face });
            Assert.Equal(RotFreedom.AboutLine, free.Rot);
            Assert.False(free.IsRigid);

            seal.LockRotation = true;
            var locked = MotionResolver.Resolve(new List<GraphMate> { seal, face });
            Assert.True(locked.IsRigid);
        }

        /// <summary>A locked concentric ALONE still slides: locking the
        /// rotation does not close the bore.</summary>
        [Fact]
        public void ALockedConcentricAloneStillSlidesAlongItsAxis()
        {
            var m = Mate("Concentric91", "swMateCONCENTRIC",
                Cylinder("c065", Yp, P(0, 0.065, -0.231), 0.007475),
                Cylinder("c058", Yp, P(0, 0.065, -0.231), 0.007475));
            m.LockRotation = true;

            var s = MotionResolver.Resolve(new List<GraphMate> { m });
            Assert.Equal(RotFreedom.None, s.Rot);
            Assert.Equal(1, s.TransDim);
        }

        /// <summary>
        /// The ram's stroke is limited by a distance mate between two
        /// VERTICES. Such a mate measures no normal: the direction slots are
        /// filler and MateReader rightly drops them, so every geometric rung
        /// of the sign ladder used to fall through to the probe, which cannot
        /// read a dimension inside a flexible subassembly. Both live rams
        /// shipped confidence "medium" with their limits as read, which is
        /// right for one and mirrored for the other.
        ///
        /// The separation between the two points IS the direction the
        /// dimension grows in, and that is a fact about the geometry alone.
        /// </summary>
        [Theory]
        [InlineData(1.0, 0.0, 0.30, 0.12)]      // rod on the +axis side: as read
        [InlineData(-1.0, -0.30, 0.0, -0.12)]   // rod on the −axis side: mirrored
        public void APointToPointStrokeLimitResolvesFromTheSeparationAlone(
            double side, double expectMin, double expectMax, double expectRest)
        {
            var barrelPoint = P(0, 0, 0);
            var rodPoint = P(0.4 * side, 0, 0);
            var graph = Graph(
                new[]
                {
                    Comp("c065", "barrel", isFixed: true),
                    Comp("c064", "rod"),
                },
                Mate("Concentric1", "swMateCONCENTRIC",
                    Cylinder("c065", X, P(0, 0, 0), 0.0225),
                    Cylinder("c064", X, P(0, 0, 0), 0.0225)),
                // The anti-rotation mate: the eye bores at the two ends run
                // across the stroke, and holding them parallel is what stops
                // the rod spinning in the barrel.
                Mate("Parallel1", "swMatePARALLEL",
                    Cylinder("c065", Yp, P(-0.045, 0, 0), 0.013),
                    Cylinder("c064", Yp, P(0.4 * side, 0, 0), 0.0225)),
                WithRange(
                    Mate("LimitDistance1", "swMateDISTANCE",
                        VertexEnt("c065", barrelPoint), VertexEnt("c064", rodPoint)),
                    min: 0.0, max: 0.30, current: 0.12));

            var grouping = RigidGrouper.Group(graph);
            var result = JointClassifier.Classify(graph, grouping);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Prismatic, joint.Type);
            // The bone never flips: the axis is a pure function of the line.
            Assert.Equal(new double[] { 1, 0, 0 }, joint.Axis);
            Assert.NotNull(joint.TranslationLimit);
            Assert.Equal(expectMin, joint.TranslationLimit.Min, 12);
            Assert.Equal(expectMax, joint.TranslationLimit.Max, 12);
            Assert.Equal(expectRest, joint.TranslationLimit.ValueAtRest, 12);
            // Resolved from geometry, so no "the sign is a guess" downgrade.
            Assert.Equal("high", joint.Confidence);
        }

        /// <summary>
        /// A ram with a stroke limit between two LINES: the barrel's pin axis
        /// and the rod eye's pin axis. Both lines run across the stroke and
        /// point opposite ways, as live CutterRig reports them (2026-09-21).
        /// A parallel mate between the eye bores stops the spin, and the
        /// limit sits at its short stop, 0.33 apart. <paramref name="side"/>
        /// puts the rod eye on the +X or the −X side of the barrel pin. Each
        /// pin also sits off centre along its own line, as the live ones do.
        /// That moves nothing along the stroke.
        /// </summary>
        private static MateGraph LineStrokeRam(
            double side, bool rodFixed,
            Func<string, double[], double[], GraphMateEntity> pin)
        {
            var barrelPin = P(0, -0.02, 0);
            var rodPin = P(0.33 * side, 0.015, 0);
            return Graph(
                new[]
                {
                    Comp("c001", "barrel", isFixed: !rodFixed),
                    Comp("c002", "rod", isFixed: rodFixed),
                },
                Mate("Concentric1", "swMateCONCENTRIC",
                    Cylinder("c001", X, P(0, 0, 0), 0.01),
                    Cylinder("c002", X, P(0, 0, 0), 0.01)),
                Mate("Parallel1", "swMatePARALLEL",
                    Cylinder("c001", Yp, barrelPin, 0.008),
                    Cylinder("c002", Yn, rodPin, 0.008)),
                WithRange(
                    Mate("LimitDistance1", "swMateDISTANCE",
                        pin("c001", Yp, barrelPin), pin("c002", Yn, rodPin)),
                    min: 0.33, max: 0.53, current: 0.33));
        }

        /// <summary>
        /// Both live CutterRig rams limit their stroke with a distance mate
        /// between two datum axes across the slide (2026-09-21). The
        /// classifier read the first axis as a plane normal. A normal across
        /// the slide measures nothing along it, so neither rod slide got its
        /// stroke limit, and both carried LIMIT_AXIS_MISMATCH. The gap
        /// between the two lines along the slide is what the dimension
        /// measures, and the side the child sits on gives its sense. The two
        /// cases with the barrel as the child are the live second ram, whose
        /// slide runs from the rod to the barrel.
        /// </summary>
        [Theory]
        [InlineData(1.0, false, 0.33, 0.53, 0.33)]      // rod child on the +axis side: as read
        [InlineData(-1.0, false, -0.53, -0.33, -0.33)]  // rod child on the −axis side: mirrored
        [InlineData(1.0, true, -0.53, -0.33, -0.33)]    // barrel child on the −axis side: mirrored
        [InlineData(-1.0, true, 0.33, 0.53, 0.33)]      // barrel child on the +axis side: as read
        public void AnAxisToAxisStrokeLimitAttachesToTheSlide(
            double side, bool rodFixed, double expectMin, double expectMax, double expectRest)
        {
            var graph = LineStrokeRam(side, rodFixed, AxisEnt);

            var grouping = RigidGrouper.Group(graph);
            var result = JointClassifier.Classify(graph, grouping);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Prismatic, joint.Type);
            Assert.Equal(new double[] { 1, 0, 0 }, joint.Axis);
            Assert.Equal(grouping.ComponentGroup[rodFixed ? "c001" : "c002"], joint.ChildGroup);
            Assert.NotNull(joint.TranslationLimit);
            Assert.Equal(expectMin, joint.TranslationLimit.Min, 12);
            Assert.Equal(expectMax, joint.TranslationLimit.Max, 12);
            Assert.Equal(expectRest, joint.TranslationLimit.ValueAtRest, 12);
            Assert.Null(joint.RotationLimit);
            Assert.DoesNotContain(result.Warnings, w => w.Code == "LIMIT_AXIS_MISMATCH");
            // Resolved from geometry, so no "the sign is a guess" downgrade.
            Assert.Equal("high", joint.Confidence);
        }

        /// <summary>A straight model edge is a line just as a datum axis is.
        /// A circular edge is also typed "edge", but a distance to it runs
        /// to its centre. Its direction is the circle's axis, so this rule
        /// gives it no stroke limit.</summary>
        [Fact]
        public void StraightEdgesAcrossTheSlideLimitTheStrokeButCircularEdgesDoNot()
        {
            var straight = LineStrokeRam(1.0, false, EdgeEnt);
            var joint = Assert.Single(
                JointClassifier.Classify(straight, RigidGrouper.Group(straight)).Joints);
            Assert.NotNull(joint.TranslationLimit);
            Assert.Equal(0.33, joint.TranslationLimit.ValueAtRest, 12);

            var circular = LineStrokeRam(1.0, false, (c, d, p) =>
            {
                var e = EdgeEnt(c, d, p);
                e.Radius = 0.008;
                return e;
            });
            var result = JointClassifier.Classify(circular, RigidGrouper.Group(circular));
            Assert.Null(Assert.Single(result.Joints).TranslationLimit);
            Assert.Equal("LIMIT_AXIS_MISMATCH", Assert.Single(result.Warnings).Code);
        }
    }
}
