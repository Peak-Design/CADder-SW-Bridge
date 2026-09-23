using System;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;
using static Peak.Cadder.Tests.FixtureBuilder;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// Contact mates split through a carrier link: one primitive joint for
    /// each side of the contact. These cover the sides the split must read
    /// correctly for the joint pair to keep every freedom SolidWorks gives.
    /// </summary>
    public class ContactSplitTests
    {
        private static ClassificationResult Run(MateGraph graph)
        {
            return JointClassifier.Classify(graph, RigidGrouper.Group(graph));
        }

        private static void AssertVector(double[] expected, double[] actual, double tol = 1e-9)
        {
            Assert.NotNull(actual);
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], actual[i], tol);
        }

        private static void AssertAlong(double[] expected, double[] actual)
        {
            Assert.NotNull(actual);
            Assert.Equal(1.0, Math.Abs(MathOps.Dot(expected, MathOps.Normalized(actual))), 9);
        }

        // ── A ball against a line ───────────────────────────────────────────

        /// <summary>
        /// A ball tangent to the inside of a tube rolls along the tube as
        /// well as around it. The tube side was split as a revolute, so in
        /// Blender the ball could only orbit at its export position. It must
        /// be a cylindrical joint along the tube, at the ball's own station.
        /// </summary>
        [Fact]
        public void ABallInATubeSlidesAlongTheTube()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "tube", isFixed: true),
                    Comp("c002", "ball"),
                },
                Mate("Tangent1", "swMateTANGENT",
                    Cylinder("c001", X, P(0, 0, 0), radius: 0.01),
                    SphereEnt("c002", P(0.05, 0, -0.005), radius: 0.005)));

            var result = Run(graph);

            Assert.Equal(2, result.Joints.Count);
            var tube = result.Joints[0];
            var ball = result.Joints[1];
            Assert.Equal(JointType.Cylindrical, tube.Type);
            AssertAlong(X, tube.Axis);
            AssertVector(new[] { 0.05, 0.0, 0.0 }, tube.Origin);
            Assert.Equal(JointType.Ball, ball.Type);
            AssertVector(new[] { 0.05, 0.0, -0.005 }, ball.Origin);
            Assert.Empty(result.Warnings);
        }

        /// <summary>A vertex held at a distance from a datum axis moves
        /// along the axis and around it, with the ball's three turns on
        /// top.</summary>
        [Fact]
        public void AVertexAtADistanceFromAnAxisSlidesAlongIt()
        {
            var distance = Mate("Distance1", "swMateDISTANCE",
                AxisEnt("c001", Z, P(0, 0, 0)),
                VertexEnt("c002", P(0.02, 0, 0.03)));
            distance.CurrentValue = 0.02;

            var graph = Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    Comp("c002", "pointer"),
                },
                distance);

            var result = Run(graph);

            Assert.Equal(2, result.Joints.Count);
            var line = result.Joints[0];
            var ball = result.Joints[1];
            Assert.Equal(JointType.Cylindrical, line.Type);
            AssertAlong(Z, line.Axis);
            AssertVector(new[] { 0.0, 0.0, 0.03 }, line.Origin);
            Assert.Equal(JointType.Ball, ball.Type);
            AssertVector(new[] { 0.02, 0.0, 0.03 }, ball.Origin);
        }

        /// <summary>The same contact with the ball on the parent side: the
        /// ball comes first in the chain and the tube's cylindrical second.
        /// </summary>
        [Fact]
        public void ATubeOnAFixedBallSlidesAlongTheTube()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "ball", isFixed: true),
                    Comp("c002", "tube"),
                },
                Mate("Tangent1", "swMateTANGENT",
                    SphereEnt("c001", P(0.05, 0, -0.005), radius: 0.005),
                    Cylinder("c002", X, P(0, 0, 0), radius: 0.01)));

            var result = Run(graph);

            Assert.Equal(2, result.Joints.Count);
            Assert.Equal(JointType.Ball, result.Joints[0].Type);
            Assert.Equal(JointType.Cylindrical, result.Joints[1].Type);
            AssertAlong(X, result.Joints[1].Axis);
            AssertVector(new[] { 0.05, 0.0, 0.0 }, result.Joints[1].Origin);
        }
    }
}
