using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A closure's origin slides along the closure's own axis to the point
    /// its bodies cannot carry away. That is right only for a joint whose
    /// origin is free along a line.
    /// </summary>
    public class ClosureSeatTests
    {
        private static RigJoint Joint(string id, string type, string parent, string child,
                                      double[] axis, double[] origin)
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

        /// <summary>
        /// A spatial crank and rocker joined by a link with a ball at each
        /// end. Each ball has an angle limit, so it carries the axis of its
        /// cone. A ball is the loop's cut, and its origin is its center, the
        /// one point that defines the joint. Slid along the cone's axis to
        /// the point nearest the crank's hinge, it moved 400 mm off the
        /// point where the bodies meet.
        /// </summary>
        [Fact]
        public void ABallCutKeepsItsCentre()
        {
            var groups = new List<RigidGroup>
            {
                new RigidGroup { Id = "g000", Name = "frame", Grounded = true },
                new RigidGroup { Id = "g001", Name = "crank" },
                new RigidGroup { Id = "g002", Name = "link" },
                new RigidGroup { Id = "g003", Name = "rocker" },
            };
            var y = new double[] { 0, 1, 0 };
            var joints = new List<RigJoint>
            {
                Joint("j001", JointType.Revolute, "g000", "g001", new double[] { 0, 0, 1 },
                      new double[] { 0, 0.4, 0 }),
                Joint("j002", JointType.Ball, "g001", "g002", (double[])y.Clone(),
                      new double[] { 1, 0, 0.3 }),
                Joint("j003", JointType.Ball, "g002", "g003", (double[])y.Clone(),
                      new double[] { 2, 0.5, 0.6 }),
                Joint("j004", JointType.Revolute, "g003", "g000", new double[] { 1, 0, 0 },
                      new double[] { 2, 0.2, 0 }),
            };
            var before = new Dictionary<string, double[]>();
            foreach (var j in joints) before[j.Id] = (double[])j.Origin.Clone();

            var result = LoopAnalyzer.Analyze(groups, joints);

            var loop = Assert.Single(result.Loops);
            var cut = result.Joints.Find(j => j.Id == loop.ClosureJoint);
            Assert.Equal(JointType.Ball, cut.Type);
            Assert.Equal("ik", loop.ClosureKind);
            for (int k = 0; k < 3; k++) Assert.Equal(before[cut.Id][k], cut.Origin[k], 12);
        }
    }
}
