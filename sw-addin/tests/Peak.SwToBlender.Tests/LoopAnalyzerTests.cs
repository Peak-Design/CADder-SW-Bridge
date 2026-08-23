using System.Collections.Generic;
using Peak.SwToBlender.Core;
using Peak.SwToBlender.Core.Model;
using Xunit;

namespace Peak.SwToBlender.Tests
{
    public class LoopAnalyzerTests
    {
        private static RigidGroup Group(string id, bool grounded = false)
        {
            var g = new RigidGroup();
            g.Id = id;
            g.Name = id;
            g.Grounded = grounded;
            return g;
        }

        private static RigJoint Joint(string id, string type, string parent, string child, double[] axis = null)
        {
            var j = new RigJoint();
            j.Id = id;
            j.Type = type;
            j.ParentGroup = parent;
            j.ChildGroup = child;
            j.Axis = axis;
            return j;
        }

        /// <summary>A free joint from a parallel mate: rotation locked except
        /// about the mated faces' normal, translations untouched.</summary>
        private static RigJoint ParallelFree(string id, string parent, string child, double[] normal)
        {
            var j = Joint(id, JointType.Free, parent, child);
            j.ResidualKnown = true;
            j.ResidualRot = RotFreedom.AboutDirection;
            j.ResidualRotDir = normal;
            return j;
        }

        /// <summary>Classic four-bar: four groups in a ring, four revolutes
        /// about the same direction. Some joints are stated backwards on
        /// purpose — the analyzer must re-orient them rootward.</summary>
        [Fact]
        public void FourBarRingYieldsOnePlanarLoop()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"),
                Group("g002"),
                Group("g003"),
            };
            var axis = new double[] { 0, 0, 1 };
            var joints = new List<RigJoint>
            {
                Joint("j001", JointType.Revolute, "g000", "g001", axis),
                Joint("j002", JointType.Revolute, "g002", "g001", axis),   // backwards
                Joint("j003", JointType.Revolute, "g002", "g003", axis),
                Joint("j004", JointType.Revolute, "g003", "g000", axis),   // backwards
            };

            var result = LoopAnalyzer.Analyze(groups, joints);

            var loop = Assert.Single(result.Loops);
            Assert.Equal("loop001", loop.Id);
            Assert.Equal(new[] { "j001", "j002", "j003", "j004" }, loop.MemberJoints);
            // j001 drives (ground-incident revolute, lowest id) and the cut
            // sits just past its moving group, so the rest of the ring is
            // IK-solved.
            Assert.Equal("j002", loop.ClosureJoint);
            Assert.True(loop.Planar);
            Assert.NotNull(loop.PlaneNormal);
            Assert.Equal(axis, loop.PlaneNormal);
            Assert.Equal("j001", loop.SuggestedDriverJoint);

            // Every joint's parent end is nearer the grounded root:
            // g000 at depth 0, g001/g003 at 1, g002 at 2.
            Assert.Equal(4, result.Joints.Count);
            AssertOriented(result.Joints[0], "g000", "g001");
            AssertOriented(result.Joints[1], "g001", "g002");
            AssertOriented(result.Joints[2], "g003", "g002");
            AssertOriented(result.Joints[3], "g000", "g003");
        }

        /// <summary>An IK point constraint faithfully closes a revolute but
        /// over-constrains a sliding interface, so between the two possible
        /// driver/cut pairings the analyzer takes the one whose cut is a
        /// revolute — the prismatic becomes a tree edge instead.</summary>
        [Fact]
        public void DriverSideChosenSoTheCutIsRevolute()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"),
                Group("g002"),
                Group("g003"),
            };
            var axis = new double[] { 0, 0, 1 };
            var joints = new List<RigJoint>
            {
                Joint("j001", JointType.Revolute, "g000", "g001", axis),
                Joint("j002", JointType.Revolute, "g001", "g002", axis),
                Joint("j003", JointType.Prismatic, "g002", "g003", new double[] { 1, 0, 0 }),
                Joint("j004", JointType.Revolute, "g003", "g000", axis),   // backwards
            };

            var result = LoopAnalyzer.Analyze(groups, joints);

            var loop = Assert.Single(result.Loops);
            Assert.Equal(new[] { "j001", "j002", "j003", "j004" }, loop.MemberJoints);

            // Driving j004 would put the cut on the prismatic j003; driving
            // j001 puts it on the revolute j002, so j001 drives.
            Assert.Equal("j002", loop.ClosureJoint);
            Assert.Equal("j001", loop.SuggestedDriverJoint);
            Assert.True(loop.Planar);

            // Tree j001/j003/j004: g001 and g003 at depth 1, g002 at 2
            // under g003 through the prismatic.
            AssertOriented(result.Joints[0], "g000", "g001");
            AssertOriented(result.Joints[1], "g001", "g002");   // closure: rootward end is parent
            AssertOriented(result.Joints[2], "g003", "g002");
            AssertOriented(result.Joints[3], "g000", "g003");
        }

        /// <summary>Live corpus 06 (2026-08-22): the ground–crank revolute
        /// must stay a tree edge and drive; the cut goes just past the crank
        /// so coupler and rocker are IK-solved. The old rule cut the driver's
        /// own edge and the four-bar froze solid. The rocker–coupler joint is
        /// cylindrical here because a lone concentric classifies that way —
        /// the driver preference must still avoid cutting it.</summary>
        [Fact]
        public void FourBarCutsJustPastTheDriver()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),   // ground
                Group("g001"),                   // coupler
                Group("g002"),                   // crank
                Group("g003"),                   // rocker
            };
            var axis = new double[] { 0, 0, -1 };
            var joints = new List<RigJoint>
            {
                Joint("j001", JointType.Revolute, "g000", "g002", axis),
                Joint("j002", JointType.Revolute, "g000", "g003", axis),
                Joint("j003", JointType.Revolute, "g001", "g002", axis),
                Joint("j004", JointType.Cylindrical, "g003", "g001", axis),
            };

            var result = LoopAnalyzer.Analyze(groups, joints);

            var loop = Assert.Single(result.Loops);
            Assert.Equal(new[] { "j001", "j002", "j003", "j004" }, loop.MemberJoints);
            Assert.Equal("j003", loop.ClosureJoint);
            Assert.Equal("j001", loop.SuggestedDriverJoint);
            Assert.True(loop.Planar);

            AssertOriented(result.Joints[0], "g000", "g002");
            AssertOriented(result.Joints[1], "g000", "g003");
            AssertOriented(result.Joints[2], "g002", "g001");   // closure re-oriented rootward
            AssertOriented(result.Joints[3], "g003", "g001");
        }

        /// <summary>Live corpus 06 parallelogram2 (2026-08-22): redundant
        /// parallel mates between opposing links export as free joints. Free
        /// joints must not be graph edges — the consumer never parents them,
        /// so a free "tree edge" here made every real ring joint a loop
        /// closure and the Blender side refused the manifest as
        /// disconnected. The ring must come out as exactly one loop with the
        /// free joints in no loop at all.</summary>
        [Fact]
        public void FreeJointsAreNotGraphEdges()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),   // bottom bar
                Group("g001"),                   // crank
                Group("g002"),                   // top bar
                Group("g003"),                   // second crank
            };
            var axis = new double[] { 0, 0, -1 };
            var joints = new List<RigJoint>
            {
                Joint("j001", JointType.Revolute, "g000", "g001", axis),
                ParallelFree("j002", "g000", "g002", new double[] { 1, 0, 0 }),
                Joint("j003", JointType.Revolute, "g000", "g003", axis),
                Joint("j004", JointType.Cylindrical, "g001", "g002", axis),
                ParallelFree("j005", "g001", "g003", new double[] { 0, -1, 0 }),
                Joint("j006", JointType.Revolute, "g002", "g003", axis),
            };

            var result = LoopAnalyzer.Analyze(groups, joints);

            var loop = Assert.Single(result.Loops);
            Assert.Equal(new[] { "j001", "j003", "j004", "j006" }, loop.MemberJoints);
            // Driving j001 would cut the cylindrical j004; driving j003 cuts
            // the revolute j006, so j003 drives.
            Assert.Equal("j006", loop.ClosureJoint);
            Assert.Equal("j003", loop.SuggestedDriverJoint);
            Assert.True(loop.Planar);

            // The parallel mates duplicate what the loop's IK closure already
            // enforces: no coupling may be synthesized on the ring joints (a
            // driver would fight the IK), and the free joints are reported
            // redundant rather than under-defined.
            foreach (var j in result.Joints) Assert.Null(j.Coupling);
            Assert.Empty(result.CoupledFreeJointIds);
            Assert.Equal(new[] { "j002", "j005" }, result.RedundantFreeJointIds);
        }

        /// <summary>Live corpus 06 parallelogram3 (2026-08-22): a corner pin
        /// deleted, parallel mates between opposing links instead. Pairwise
        /// that is three independent revolutes plus two free joints, but the
        /// parallel mates lock relative orientation about the common hinge
        /// direction, which makes the signed joint angles equal: gear
        /// couplings with geometric signs. crank2 follows crank1 1:1; the
        /// top bar counter-rotates on crank2's pin to stay level.</summary>
        [Fact]
        public void ParallelMatesBecomeGearCouplings()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),   // bottom bar
                Group("g001"),                   // crank 1
                Group("g002"),                   // top bar
                Group("g003"),                   // crank 2
            };
            var axis = new double[] { 0, 0, -1 };
            var joints = new List<RigJoint>
            {
                Joint("j001", JointType.Revolute, "g000", "g001", axis),
                ParallelFree("j002", "g000", "g002", new double[] { 1, 0, 0 }),
                Joint("j003", JointType.Revolute, "g000", "g003", axis),
                ParallelFree("j004", "g001", "g003", new double[] { 0, -1, 0 }),
                Joint("j005", JointType.Revolute, "g002", "g003", axis),   // backwards
            };

            var result = LoopAnalyzer.Analyze(groups, joints);

            Assert.Empty(result.Loops);
            Assert.Equal(new[] { "j002", "j004" }, result.CoupledFreeJointIds);
            Assert.Empty(result.RedundantFreeJointIds);

            var j003 = result.Joints[2];
            Assert.NotNull(j003.Coupling);
            Assert.Equal("gear", j003.Coupling.Kind);
            Assert.Equal("j001", j003.Coupling.DriverJoint);
            Assert.Equal(1.0, j003.Coupling.Ratio);

            var j005 = result.Joints[4];
            AssertOriented(j005, "g003", "g002");
            Assert.NotNull(j005.Coupling);
            Assert.Equal("gear", j005.Coupling.Kind);
            Assert.Equal("j003", j005.Coupling.DriverJoint);
            Assert.Equal(-1.0, j005.Coupling.Ratio);

            // The free joints stay free — the coupling models them, the tree
            // never parents them.
            Assert.Null(result.Joints[1].Coupling);
            Assert.Null(result.Joints[3].Coupling);
        }

        /// <summary>A pure chain has no non-tree edge and therefore no loop,
        /// whatever the joint count.</summary>
        [Fact]
        public void TreeOnlyAssemblyHasNoLoops()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"),
                Group("g002"),
            };
            var joints = new List<RigJoint>
            {
                Joint("j001", JointType.Revolute, "g000", "g001", new double[] { 0, 0, 1 }),
                Joint("j002", JointType.Prismatic, "g002", "g001", new double[] { 1, 0, 0 }),   // backwards
            };

            var result = LoopAnalyzer.Analyze(groups, joints);

            Assert.Empty(result.Loops);
            Assert.Equal(2, result.Joints.Count);
            AssertOriented(result.Joints[0], "g000", "g001");
            AssertOriented(result.Joints[1], "g001", "g002");
        }

        private static void AssertOriented(RigJoint joint, string parent, string child)
        {
            Assert.Equal(parent, joint.ParentGroup);
            Assert.Equal(child, joint.ChildGroup);
        }
    }
}
