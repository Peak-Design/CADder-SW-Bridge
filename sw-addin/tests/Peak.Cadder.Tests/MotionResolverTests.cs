using System;
using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;
using static Peak.Cadder.Tests.FixtureBuilder;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A countersunk bolt is held by a coincident between its head's cone
    /// and the countersink's cone, plus a concentric on the shank. The
    /// coincident used to read like a concentric, which keeps the slide
    /// along the axis. So every bolt with a locked concentric exported as a
    /// slide (live CutterRig, 2026-09-21). SolidWorks read those bolts
    /// rigid, and read the one whose concentric was broken as revolute.
    /// The geometry here is the shape of that case, not its numbers. Both
    /// cone points sit on the axis a little apart, because the entity point
    /// is not the apex.
    /// </summary>
    public class MotionResolverTests
    {
        private const double Countersink = Math.PI / 4.0;
        private static readonly double[] PlateCone = { 0.04, 0.0, 0.02 };
        private static readonly double[] BoltCone = { 0.04, 0.0007, 0.02 };

        private static ClassificationResult Run(MateGraph graph)
        {
            return JointClassifier.Classify(graph, RigidGrouper.Group(graph));
        }

        /// <summary>A circular edge as a circle entity delivers it: the
        /// circle's axis in the direction slots and its radius, which only
        /// the circle kind gives an edge.</summary>
        private static GraphMateEntity CircleEdgeEnt(
            string compId, double[] axis, double[] center, double radius)
        {
            var e = EdgeEnt(compId, axis, center);
            e.Radius = radius;
            return e;
        }

        private static GraphMate ConeSeat(double plateHalfAngle, double boltHalfAngle)
        {
            return Mate("Coincident1", "swMateCOINCIDENT",
                ConeEnt("c001", Y, PlateCone, plateHalfAngle),
                ConeEnt("c002", Y, BoltCone, boltHalfAngle));
        }

        private static GraphMate LockedShank()
        {
            var m = Concentric("Concentric1", "c001", "c002", Y, P(0.04, -0.01, 0.02));
            m.LockRotation = true;
            return m;
        }

        private static void AssertSpinAboutTheBoltAxis(MotionState s)
        {
            Assert.Equal(0, s.TransDim);
            Assert.Equal(RotFreedom.AboutLine, s.Rot);
            Assert.True(MateFacts.IsParallel(s.RotDir, Y), "the spin is not about the bolt axis");
            Assert.True(MateFacts.DistancePointToLine(s.RotPoint, Y, PlateCone) <= 1e-9,
                        "the spin line is off the bolt axis");
            Assert.Equal(0, s.Unmodelled);
        }

        [Fact]
        public void AConeSeatedInAConeKeepsOnlyTheSpin()
        {
            var s = MotionResolver.Resolve(new List<GraphMate> { ConeSeat(Countersink, Countersink) });

            AssertSpinAboutTheBoltAxis(s);
        }

        /// <summary>Two parts modelled apart never agree on the angle to the
        /// last digit. The live pair differed at 1e-14.</summary>
        [Fact]
        public void HalfAnglesThatDifferInTheLastDigitsStillSeat()
        {
            var s = MotionResolver.Resolve(new List<GraphMate>
            {
                ConeSeat(0.7853981633974485, 0.78539816339745),
            });

            AssertSpinAboutTheBoltAxis(s);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ASeatedConeWithALockedConcentricIsRigid(bool seatFirst)
        {
            var mates = seatFirst
                ? new List<GraphMate> { ConeSeat(Countersink, Countersink), LockedShank() }
                : new List<GraphMate> { LockedShank(), ConeSeat(Countersink, Countersink) };

            var s = MotionResolver.Resolve(mates);

            Assert.True(s.IsRigid);
            Assert.Equal(0, s.Unmodelled);
        }

        /// <summary>SolidWorks refuses cone faces of different angles, so the
        /// seat rule does not know a mismatch. The pair keeps the old
        /// concentric reading: spin about the axis and slide along it.</summary>
        [Fact]
        public void MismatchedHalfAnglesKeepTheConcentricReading()
        {
            // 90 and 82 degree countersinks: half-angles of 45 and 41 degrees.
            double ninety = Math.PI / 4.0, eightyTwo = 41.0 * Math.PI / 180.0;
            var s = MotionResolver.Resolve(new List<GraphMate> { ConeSeat(ninety, eightyTwo) });

            Assert.Equal(1, s.TransDim);
            Assert.True(MateFacts.IsParallel(s.TransDirs[0], Y));
            Assert.Equal(RotFreedom.AboutLine, s.Rot);

            var locked = MotionResolver.Resolve(new List<GraphMate>
            {
                ConeSeat(ninety, eightyTwo), LockedShank(),
            });
            Assert.False(locked.IsRigid);
            Assert.Equal(1, locked.TransDim);
        }

        /// <summary>A cone entity with no half-angle was never read off a
        /// conical surface, so the rule cannot tell what it is.</summary>
        [Fact]
        public void ConesWithNoHalfAngleKeepTheConcentricReading()
        {
            var s = MotionResolver.Resolve(new List<GraphMate> { ConeSeat(0.0, 0.0) });

            Assert.Equal(1, s.TransDim);
            Assert.Equal(RotFreedom.AboutLine, s.Rot);
        }

        /// <summary>The other usual countersink mate: the head's cone on the
        /// countersink's circular top edge. A circle lies on a cone only as
        /// one of its parallels, so the height is pinned just the same.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AConeSeatedOnACircularEdgeKeepsOnlyTheSpin(bool coneFirst)
        {
            var cone = ConeEnt("c002", Y, BoltCone, Countersink);
            var edge = CircleEdgeEnt("c001", Y, PlateCone, 0.008);
            var seat = coneFirst
                ? Mate("Coincident1", "swMateCOINCIDENT", cone, edge)
                : Mate("Coincident1", "swMateCOINCIDENT", edge, cone);

            AssertSpinAboutTheBoltAxis(MotionResolver.Resolve(new List<GraphMate> { seat }));
            Assert.True(MotionResolver.Resolve(new List<GraphMate> { seat, LockedShank() }).IsRigid);
        }

        /// <summary>An edge with no radius is a straight edge, and a
        /// straight edge on a cone is no seat. The rule leaves it alone even
        /// where its line runs along the cone's axis.</summary>
        [Fact]
        public void AStraightEdgeOnAConeIsNotASeat()
        {
            var seat = Mate("Coincident1", "swMateCOINCIDENT",
                ConeEnt("c002", Y, BoltCone, Countersink),
                EdgeEnt("c001", Y, PlateCone));

            var s = MotionResolver.Resolve(new List<GraphMate> { seat });

            Assert.Equal(1, s.TransDim);
        }

        /// <summary>A solved seat shares one axis line. Two cones side by
        /// side are some other recording, and keep the old reading.</summary>
        [Fact]
        public void ConesOnDifferentAxisLinesAreNotASeat()
        {
            var seat = Mate("Coincident1", "swMateCOINCIDENT",
                ConeEnt("c001", Y, PlateCone, Countersink),
                ConeEnt("c002", Y, P(0.05, 0.0007, 0.02), Countersink));

            var s = MotionResolver.Resolve(new List<GraphMate> { seat });

            Assert.Equal(1, s.TransDim);
        }

        /// <summary>
        /// The whole pipeline agrees with the resolver, because the grouper
        /// and the classifier both ask it. The locked bolt joins its plate.
        /// The bolt whose concentric was broken turns about its own axis,
        /// which is what SolidWorks read for it.
        /// </summary>
        [Fact]
        public void TheLockedBoltJoinsItsPlateAndTheLooseBoltTurns()
        {
            var locked = Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    Comp("c002", "countersunk bolt"),
                },
                ConeSeat(Countersink, Countersink),
                LockedShank());

            var grouping = RigidGrouper.Group(locked);
            Assert.Single(grouping.Groups);
            var weld = JointClassifier.Classify(locked, grouping);
            Assert.Empty(weld.Joints);
            Assert.Empty(weld.Warnings);

            var loose = Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    Comp("c002", "countersunk bolt"),
                },
                ConeSeat(Countersink, Countersink));

            var result = Run(loose);
            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Revolute, joint.Type);
            Assert.True(MateFacts.IsParallel(joint.Axis, Y));
            Assert.True(MateFacts.DistancePointToLine(joint.Origin, Y, PlateCone) <= 1e-9);
            Assert.Equal("high", joint.Confidence);
            Assert.Empty(result.Warnings);
        }
    }
}
