using System;
using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A ball the ring leaves one rotation is a revolute about that line,
    /// wherever the line points. The ball's three turns were read about the
    /// world axes, so a line on a world axis was named and a tilted line was
    /// not: the same mechanism turned in the world came out a revolute one
    /// way and a full ball with a note the other way.
    /// </summary>
    public class CutTransferBallTests
    {
        /// <summary>A turn of 30 degrees about x, then 20 degrees about z:
        /// no world axis goes to a world axis.</summary>
        private static double[] Tilt(double[] v)
        {
            double a = 30.0 * Math.PI / 180.0, b = 20.0 * Math.PI / 180.0;
            var x = new[] { v[0], Math.Cos(a) * v[1] - Math.Sin(a) * v[2], Math.Sin(a) * v[1] + Math.Cos(a) * v[2] };
            return new[] { Math.Cos(b) * x[0] - Math.Sin(b) * x[1], Math.Sin(b) * x[0] + Math.Cos(b) * x[1], x[2] };
        }

        private static RigJoint Joint(
            string id, string type, string parent, string child, double[] axis, double[] origin)
        {
            return new RigJoint
            {
                Id = id,
                Type = type,
                ParentGroup = parent,
                ChildGroup = child,
                Axis = axis,
                SecondaryAxis = axis == null ? null : new double[] { 1, 0, 0 },
                Origin = origin,
            };
        }

        /// <summary>The ring of the existing ball test (a ball, a revolute
        /// about z and a plane whose normal is z), placed by `place`.
        /// </summary>
        private static RigJoint NarrowedBall(Func<double[], double[]> place)
        {
            var groups = new List<RigidGroup>
            {
                new RigidGroup { Id = "g000", Name = "g000", Grounded = true },
                new RigidGroup { Id = "g001", Name = "g001" },
                new RigidGroup { Id = "g002", Name = "g002" },
            };
            var z = place(new double[] { 0, 0, 1 });
            var ball = Joint("j001", JointType.Ball, "g000", "g001", null, place(new double[3]));
            ball.RotationLimit = new JointLimit { Min = -0.4, Max = 0.4, ValueAtRest = 0.0 };
            var pin = Joint("j002", JointType.Revolute, "g000", "g002", z, place(new double[] { 1, 0, 0 }));
            var level = Joint("j003", JointType.Planar, "g001", "g002", z, place(new double[] { 0.5, 0, 0 }));

            var result = LoopAnalyzer.Analyze(groups, new List<RigJoint> { ball, pin, level });

            var loop = Assert.Single(result.Loops);
            Assert.Equal("none", loop.ClosureKind);
            return ball;
        }

        [Fact]
        public void ATiltedLineIsNamedAsARevoluteAsAWorldAxisIs()
        {
            foreach (var place in new Func<double[], double[]>[] { v => v, Tilt })
            {
                var ball = NarrowedBall(place);

                Assert.Equal(JointType.Revolute, ball.Type);
                Assert.Equal(1.0, Math.Abs(MathOps.Dot(
                    MathOps.Normalized(ball.Axis), place(new double[] { 0, 0, 1 }))), 9);
                Assert.Null(ball.RotationLimit);
                Assert.Contains("two of its three rotations", ball.Notes);
                Assert.DoesNotContain("coupled motion", ball.Notes);
            }
        }
    }
}
