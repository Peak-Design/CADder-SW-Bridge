using System.Collections.Generic;
using Peak.SwToBlender.Core;
using Peak.SwToBlender.Core.Model;
using Xunit;
using static Peak.SwToBlender.Tests.FixtureBuilder;

namespace Peak.SwToBlender.Tests
{
    /// <summary>
    /// Assembly mirror features as couplings. A mirrored instance carries no
    /// mate to its source, so the mate graph says nothing about the relation,
    /// but SolidWorks keeps the two reflections of each other, and unlike a
    /// symmetric mate the reflection is the WHOLE placement.
    /// </summary>
    public class MirrorFeatureCouplerTests
    {
        private static RigidGroupingResult Grouping(bool mirroredIsGrounded = false)
        {
            var g = new RigidGroupingResult();
            g.Groups.Add(new RigidGroup { Id = "g000", Name = "frame", Grounded = true, Components = { "c001" } });
            g.Groups.Add(new RigidGroup { Id = "g001", Name = "arm_lh", Components = { "c002" } });
            g.Groups.Add(new RigidGroup
            {
                Id = "g002",
                Name = "arm_rh",
                Grounded = mirroredIsGrounded,
                Components = { "c003" },
            });
            g.ComponentGroup["c001"] = "g000";
            g.ComponentGroup["c002"] = "g001";
            g.ComponentGroup["c003"] = "g002";
            return g;
        }

        private static MateGraph MirrorGraph()
        {
            var graph = Graph(new[]
            {
                Comp("c001", "frame", isFixed: true),
                Comp("c002", "arm lh"),
                Comp("c003", "arm rh"),
            });
            graph.MirrorPairs.Add(new GraphMirrorPair
            {
                FeatureName = "MirrorComponent1",
                SourceComponentId = "c002",
                MirroredComponentId = "c003",
                PlanePoint = new double[3],
                PlaneNormal = new double[] { 0, 1, 0 },
            });
            return graph;
        }

        private static RigJoint Mount(string id, string type, string child, double[] axis)
        {
            return new RigJoint
            {
                Id = id,
                Type = type,
                ParentGroup = "g000",
                ChildGroup = child,
                Axis = (double[])axis.Clone(),
                SecondaryAxis = new double[] { 0, 0, 1 },
                Origin = new double[3],
            };
        }

        /// <summary>Two free bodies mirrored by a feature: the mirror IS the
        /// whole relation, so the pair becomes two ground-rooted free joints
        /// and the driven one reflects the driver ENTIRELY, unlike a
        /// symmetric mate between planar faces, which couples three freedoms
        /// and leaves three independent.</summary>
        [Fact]
        public void FreePairBecomesARigidMirrorPair()
        {
            var joints = new List<RigJoint>();
            var warnings = MirrorFeatureCoupler.Resolve(MirrorGraph(), Grouping(), joints);

            Assert.Empty(warnings);
            Assert.Equal(2, joints.Count);
            Assert.All(joints, j => Assert.Equal(JointType.Free, j.Type));
            Assert.All(joints, j => Assert.Equal("g000", j.ParentGroup));

            var driven = joints[1];
            Assert.Equal("mirror", driven.Coupling.Kind);
            Assert.Equal(MirrorScope.Rigid, driven.Coupling.MirrorScope);
            Assert.Equal(joints[0].Id, driven.Coupling.DriverJoint);
            Assert.Equal(new double[] { 0, 1, 0 }, driven.Coupling.MirrorPlaneNormal);
            Assert.Null(joints[0].Coupling);
        }

        /// <summary>When each mirrored body already hangs on its own mount the
        /// relation collapses to one number, exactly as it does for a
        /// symmetric mate: a reflection reverses orientation, so mirrored
        /// revolutes turn opposite ways.</summary>
        [Fact]
        public void MountedPairBecomesAGearOfMinusOne()
        {
            var joints = new List<RigJoint>
            {
                Mount("j001", JointType.Revolute, "g001", X),
                Mount("j002", JointType.Revolute, "g002", X),
            };
            var warnings = MirrorFeatureCoupler.Resolve(MirrorGraph(), Grouping(), joints);

            Assert.Empty(warnings);
            Assert.Equal(2, joints.Count);        // nothing synthesized
            Assert.Null(joints[0].Coupling);
            Assert.Equal("gear", joints[1].Coupling.Kind);
            Assert.Equal(-1.0, joints[1].Coupling.Ratio);
        }

        /// <summary>Oscar's rule, and it needs no code of its own: a component
        /// SolidWorks reports fully defined is already welded to ground by
        /// RigidGrouper, so its group is grounded and the pair is skipped,
        /// no obsolete constraint on something that cannot move, and no
        /// warning either, because in a production assembly this is the
        /// ordinary case.</summary>
        [Fact]
        public void MirroredComponentThatCannotMoveIsSkippedSilently()
        {
            var joints = new List<RigJoint>();
            var warnings = MirrorFeatureCoupler.Resolve(
                MirrorGraph(), Grouping(mirroredIsGrounded: true), joints);

            Assert.Empty(warnings);
            Assert.Empty(joints);
        }

        /// <summary>Mounts that are not mirror images of each other cannot be
        /// coupled by one number, and the pair is NOT silently welded: the
        /// user is told the two bodies will pose independently.</summary>
        [Fact]
        public void MismatchedMountsWarnInsteadOfCoupling()
        {
            var joints = new List<RigJoint>
            {
                Mount("j001", JointType.Revolute, "g001", X),
                Mount("j002", JointType.Revolute, "g002", Z),
            };
            var warnings = MirrorFeatureCoupler.Resolve(MirrorGraph(), Grouping(), joints);

            var w = Assert.Single(warnings);
            Assert.Equal("MIRROR_COUPLING", w.Code);
            Assert.Contains("not mirror images", w.Message);
            Assert.Null(joints[1].Coupling);
        }
    }
}
