using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A coupled pair is offered as a mechanism with two inputs, and the
    /// consumer takes the second input by turning the coupling round: the
    /// old driver gets the coupling, whatever coupling it had before. That
    /// is safe only when nothing else drives the old driver, and when the
    /// old driver drives nothing else that is offered the same way.
    /// </summary>
    public class CoupledPairTests
    {
        private static readonly double[] Down = { 0, 0, -1 };

        private static RigidGroup Group(string id, bool grounded = false)
        {
            return new RigidGroup { Id = id, Name = id, Grounded = grounded };
        }

        private static RigJoint Joint(string id, string type, string parent, string child)
        {
            return new RigJoint
            {
                Id = id,
                Type = type,
                ParentGroup = parent,
                ChildGroup = child,
                Axis = type == JointType.Free ? null : (double[])Down.Clone(),
            };
        }

        private static RigJoint ParallelFree(string id, string parent, string child, double[] normal)
        {
            var j = Joint(id, JointType.Free, parent, child);
            j.ResidualKnown = true;
            j.ResidualRot = RotFreedom.AboutDirection;
            j.ResidualRotDir = normal;
            return j;
        }

        private static RigJoint Gear(string id, string child, string driver)
        {
            var j = Joint(id, JointType.Revolute, "g000", child);
            if (driver != null)
                j.Coupling = new JointCoupling { Kind = "gear", DriverJoint = driver, Ratio = -1 };
            return j;
        }

        private static List<string> Pairs(LoopAnalysisResult result)
        {
            var pairs = new List<string>();
            foreach (var mech in result.Mechanisms)
                if (mech.CouplingPair)
                    pairs.Add(mech.Inputs[0].Joint + "+" + mech.Inputs[1].Joint);
            return pairs;
        }

        /// <summary>
        /// Live corpus 06 parallelogram3: the parallel mates become two
        /// gears in a chain. j003 follows j001, and j005 follows j003. The
        /// pair (j003, j005) was offered too. Taken in Blender, it gave j003
        /// a coupling from j005 in place of its coupling from j001, so crank
        /// one then turned alone and the parallelogram fell apart.
        /// </summary>
        [Fact]
        public void APairWhoseDriverIsDrivenIsNotOffered()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true), Group("g001"), Group("g002"), Group("g003"),
            };
            var joints = new List<RigJoint>
            {
                Joint("j001", JointType.Revolute, "g000", "g001"),
                ParallelFree("j002", "g000", "g002", new double[] { 1, 0, 0 }),
                Joint("j003", JointType.Revolute, "g000", "g003"),
                ParallelFree("j004", "g001", "g003", new double[] { 0, -1, 0 }),
                Joint("j005", JointType.Revolute, "g002", "g003"),
            };

            var result = LoopAnalyzer.Analyze(groups, joints);

            Assert.Equal("j001", joints[2].Coupling.DriverJoint);
            Assert.Equal("j003", joints[4].Coupling.DriverJoint);
            Assert.Equal(new[] { "j001+j003" }, Pairs(result));
        }

        /// <summary>A gear train, one gear driving the next. Only the first
        /// pair can turn round without cutting the train.</summary>
        [Fact]
        public void OnlyTheHeadOfAGearTrainIsOffered()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true), Group("g001"), Group("g002"), Group("g003"),
            };
            var joints = new List<RigJoint>
            {
                Gear("j001", "g001", null),
                Gear("j002", "g002", "j001"),
                Gear("j003", "g003", "j002"),
            };

            var result = LoopAnalyzer.Analyze(groups, joints);

            Assert.Equal(new[] { "j001+j002" }, Pairs(result));
        }

        /// <summary>One gear driving two. Turned round once, the pair is
        /// safe, but turned round a second time the other pair takes the
        /// first input's coupling away. Neither is offered.</summary>
        [Fact]
        public void AGearThatDrivesTwoOffersNoPair()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true), Group("g001"), Group("g002"), Group("g003"),
            };
            var joints = new List<RigJoint>
            {
                Gear("j001", "g001", null),
                Gear("j002", "g002", "j001"),
                Gear("j003", "g003", "j001"),
            };

            var result = LoopAnalyzer.Analyze(groups, joints);

            Assert.Empty(Pairs(result));
        }
    }
}
