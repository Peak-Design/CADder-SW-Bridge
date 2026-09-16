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
    }
}
