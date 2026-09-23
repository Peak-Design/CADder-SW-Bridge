using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;
using static Peak.Cadder.Tests.FixtureBuilder;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// Pinned points and sphere centers. A body with two of its points
    /// pinned to another body still turns about the line through them. A
    /// sphere center on an axis still slides along the axis. Both used to
    /// read as less motion than SolidWorks gives: two pins welded the pair
    /// shut, and a ball in a bore could not travel.
    /// </summary>
    public class PointPinTests
    {
        private static GraphMate VertexPin(string feature, double[] at)
        {
            return Mate(feature, "swMateCOINCIDENT", VertexEnt("c001", at), VertexEnt("c002", at));
        }

        private static GraphMate BallStud(string feature, double[] at)
        {
            return Mate(feature, "swMateCONCENTRIC", SphereEnt("c001", at), SphereEnt("c002", at));
        }

        private static void AssertLine(MotionState s, double[] dir, double[] through)
        {
            Assert.Equal(0, s.TransDim);
            Assert.Equal(RotFreedom.AboutLine, s.Rot);
            Assert.True(MateFacts.IsParallel(s.RotDir, dir), "the turn is about the pins' line");
            Assert.True(MateFacts.DistancePointToLine(through, s.RotDir, s.RotPoint) < 1e-9);
            Assert.False(s.IsRigid);
        }

        // ── Two pinned points ───────────────────────────────────────────────

        [Fact]
        public void TwoPinnedPointsLeaveTheTurnAboutTheLineThroughThem()
        {
            var s = MotionResolver.Resolve(new List<GraphMate>
            {
                VertexPin("Coincident1", P(0, 0, 0.05)),
                VertexPin("Coincident2", P(0.2, 0, 0.05)),
            });
            AssertLine(s, X, P(0.1, 0, 0.05));
        }

        [Fact]
        public void TwoBallStudsLeaveTheTurnAboutTheLineThroughTheirCenters()
        {
            var s = MotionResolver.Resolve(new List<GraphMate>
            {
                BallStud("Concentric1", P(0, 0.1, 0)),
                BallStud("Concentric2", P(0, 0.1, 0.3)),
            });
            AssertLine(s, Z, P(0, 0.1, 0));
        }

        [Fact]
        public void APinOnTheLineAddsNothing()
        {
            var s = MotionResolver.Resolve(new List<GraphMate>
            {
                VertexPin("Coincident1", P(0, 0, 0.05)),
                VertexPin("Coincident2", P(0.2, 0, 0.05)),
                VertexPin("Coincident3", P(0.1, 0, 0.05)),
            });
            AssertLine(s, X, P(0, 0, 0.05));
        }

        [Fact]
        public void APinOffTheLineHoldsTheLastTurn()
        {
            var s = MotionResolver.Resolve(new List<GraphMate>
            {
                VertexPin("Coincident1", P(0, 0, 0.05)),
                VertexPin("Coincident2", P(0.2, 0, 0.05)),
                VertexPin("Coincident3", P(0.1, 0.1, 0.05)),
            });
            Assert.True(s.IsRigid);
        }

        /// <summary>A lid pinned to its frame at the two ends of its hinge
        /// line opens: it stays its own body on a revolute.</summary>
        [Fact]
        public void ALidPinnedAtBothEndsOfItsHingeLineOpens()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    Comp("c002", "lid"),
                },
                VertexPin("Coincident1", P(0, 0, 0.05)),
                VertexPin("Coincident2", P(0.2, 0, 0.05)));

            var grouping = RigidGrouper.Group(graph);
            var result = JointClassifier.Classify(graph, grouping);

            Assert.Equal(2, grouping.Groups.Count);
            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Revolute, joint.Type);
            Assert.True(MateFacts.IsParallel(joint.Axis, X));
            Assert.True(MateFacts.DistancePointToLine(joint.Origin, X, P(0, 0, 0.05)) < 1e-9);
        }

        // ── A sphere on an axis ─────────────────────────────────────────────

        [Theory]
        [InlineData("cylinder")]
        [InlineData("axis")]
        [InlineData("edge")]
        public void ASphereOnAnAxisKeepsTheSlideAlongIt(string kind)
        {
            GraphMateEntity line;
            if (kind == "cylinder") line = Cylinder("c001", Z, P(0, 0, 0), 0.01);
            else if (kind == "axis") line = AxisEnt("c001", Z, P(0, 0, 0));
            else line = EdgeEnt("c001", Z, P(0, 0, 0));
            foreach (bool sphereFirst in new[] { true, false })
            {
                var ball = SphereEnt("c002", P(0, 0, 0.05), 0.01);
                var m = sphereFirst
                    ? Mate("Concentric1", "swMateCONCENTRIC", ball, line)
                    : Mate("Concentric1", "swMateCONCENTRIC", line, ball);

                var s = MotionResolver.Resolve(new List<GraphMate> { m });

                Assert.Equal(1, s.TransDim);
                Assert.True(MateFacts.IsParallel(s.TransDirs[0], Z), "the slide is along the axis");
                Assert.Equal(RotFreedom.Full, s.Rot);
            }
        }

        /// <summary>A plunger with a spherical tip in a bore, kept square by
        /// a parallel: before, the tip pinned the center and the plunger
        /// turned on a revolute with no stroke at all.</summary>
        [Fact]
        public void ABallInABoreIsNeverAPinOrAWeld()
        {
            var s = MotionResolver.Resolve(new List<GraphMate>
            {
                Mate("Concentric1", "swMateCONCENTRIC",
                    SphereEnt("c002", P(0, 0, 0.05), 0.01), Cylinder("c001", Z, P(0, 0, 0), 0.01)),
                ParallelPlanes("Parallel1", "c001", "c002", Z, P(0, 0, 0)),
            });

            Assert.Equal(1, s.TransDim);
            Assert.True(MateFacts.IsParallel(s.TransDirs[0], Z));
            Assert.False(s.IsRigid);
        }
    }
}
