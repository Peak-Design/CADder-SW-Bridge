using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A closure moved off a weld moves the weld into the tree, and the
    /// tree then reaches some bodies from the other side. The joints have
    /// to turn with it, or a body ends up with two tree parents and the
    /// consumer refuses the whole manifest.
    /// </summary>
    public class WeldRecutTests
    {
        private static RigidGroup Group(string id, bool grounded = false)
        {
            return new RigidGroup { Id = id, Name = id, Grounded = grounded };
        }

        private static RigJoint Pin(string id, string parent, string child, double x)
        {
            return new RigJoint
            {
                Id = id,
                Type = JointType.Revolute,
                ParentGroup = parent,
                ChildGroup = child,
                Axis = new double[] { 0, 0, 1 },
                SecondaryAxis = new double[] { 1, 0, 0 },
                Origin = new double[] { x, 0, 0 },
            };
        }

        /// <summary>
        /// Two brackets hinged on one axis of a base, and one bolt through
        /// both brackets off that axis. The bolt holds the brackets
        /// together, so the ring welds it, and the closure then moves off
        /// the weld to the second hinge. The bolt can be classified either
        /// way round, and the manifest and every option must build both
        /// ways.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AClosureMovedOffAWeldLeavesOneParentPerBody(bool boltReversed)
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"),
                Group("g002"),
            };
            var joints = new List<RigJoint>
            {
                Pin("j001", "g000", "g001", 0.0),
                Pin("j002", "g000", "g002", 0.0),
                boltReversed ? Pin("j003", "g001", "g002", 0.1) : Pin("j003", "g002", "g001", 0.1),
            };

            var result = LoopAnalyzer.Analyze(groups, joints);

            var loop = Assert.Single(result.Loops);
            Assert.Equal(JointType.Fixed, joints[2].Type);
            Assert.NotEqual("j003", loop.ClosureJoint);
            Assert.Empty(ConsumerRigCheck.EveryOption(groups, result));
        }
    }
}
