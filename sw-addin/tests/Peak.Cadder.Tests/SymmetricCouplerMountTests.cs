using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;
using static Peak.Cadder.Tests.FixtureBuilder;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// Which joints a mirror between two bodies is put on: each body's own
    /// mount, or the drivers of the two loops the bodies ride.
    /// </summary>
    public class SymmetricCouplerMountTests
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
                Axis = (double[])axis.Clone(),
                SecondaryAxis = (double[])X.Clone(),
                Origin = origin,
            };
        }

        /// <summary>
        /// Two collars on cylindrical mounts, mirrored about the plate's mid
        /// plane, each with a bolt that turns in it. A cylindrical mount is
        /// no mount a single number can couple, so the mate is a warning.
        /// The bolts' hinges were taken as the collars' mounts instead:
        /// the bolts were geared, the collars posed independently, and no
        /// warning said so.
        /// </summary>
        [Fact]
        public void AJointHangingOffTheBodyIsNotItsMount()
        {
            var grouping = new RigidGroupingResult();
            string[] names = { "plate", "collar one", "collar two", "bolt one", "bolt two" };
            var components = new List<GraphComponent>();
            for (int i = 0; i < names.Length; i++)
            {
                string gid = "g" + i.ToString("000"), cid = "c" + (i + 1).ToString("000");
                grouping.Groups.Add(new RigidGroup { Id = gid, Name = names[i], Grounded = i == 0, Components = { cid } });
                grouping.ComponentGroup[cid] = gid;
                components.Add(Comp(cid, names[i], isFixed: i == 0));
            }
            var graph = Graph(components.ToArray(),
                Mate("Symmetric1", "swMateSYMMETRIC",
                    PlaneEnt("c002", Y, P(0, 0.03, 0)),
                    PlaneEnt("c003", Y, P(0, -0.03, 0)),
                    PlaneEnt("c001", Y, P(0, 0, 0))));
            var joints = new List<RigJoint>
            {
                Joint("j001", JointType.Cylindrical, "g000", "g001", X, P(0, 0.03, 0)),
                Joint("j002", JointType.Cylindrical, "g000", "g002", X, P(0, -0.03, 0)),
                Joint("j003", JointType.Revolute, "g001", "g003", Z, P(0.1, 0.03, 0)),
                Joint("j004", JointType.Revolute, "g002", "g004", Z, P(0.1, -0.03, 0)),
            };

            var warnings = SymmetricCoupler.Resolve(graph, grouping, joints);

            foreach (var j in joints) Assert.Null(j.Coupling);
            var w = Assert.Single(warnings);
            Assert.Equal("SYMMETRIC_COUPLING", w.Code);
            Assert.Contains("revolute or prismatic mount", w.Message);
        }

        private static RigLoop Loop(string id, string driver, string cut, params string[] members)
        {
            var lp = new RigLoop
            {
                Id = id,
                SuggestedDriverJoint = driver,
                ClosureJoint = cut,
                ClosureKind = "ik",
                Mobility = 1,
            };
            lp.MemberJoints.AddRange(members);
            return lp;
        }

        /// <summary>
        /// Two mirrored loops, each driven by a slide on the frame, and each
        /// with a second slide parallel to it. Loop A's second slide has the
        /// lower id, loop B's has the higher one. Loop A's members were
        /// matched to loop B's in id order, the second slide took loop B's
        /// driver, loop A's driver then found no match, and the mirror went
        /// on the bodies' mounts inside the loops, where the loops set them.
        /// </summary>
        [Fact]
        public void EveryMemberFindsItsMirrorWhateverTheIdOrder()
        {
            var joints = new List<RigJoint>
            {
                Joint("j001", JointType.Prismatic, "g002", "g003", Y, P(0.4, 0.3, 0)),
                Joint("j002", JointType.Revolute, "g001", "g002", Z, P(0.3, 0.2, 0)),
                Joint("j003", JointType.Revolute, "g000", "g003", Z, P(0.5, 0.0, 0)),
                Joint("j004", JointType.Prismatic, "g000", "g001", Y, P(0.3, 0.0, 0)),
                Joint("j011", JointType.Prismatic, "g000", "g004", Y, P(-0.3, 0.0, 0)),
                Joint("j012", JointType.Prismatic, "g005", "g006", Y, P(-0.4, 0.3, 0)),
                Joint("j013", JointType.Revolute, "g004", "g005", Z, P(-0.3, 0.2, 0)),
                Joint("j014", JointType.Revolute, "g000", "g006", Z, P(-0.5, 0.0, 0)),
            };
            var loops = new List<RigLoop>
            {
                Loop("loop001", "j004", "j002", "j001", "j002", "j003", "j004"),
                Loop("loop002", "j011", "j013", "j011", "j012", "j013", "j014"),
            };

            string reason = SymmetricCoupler.TryCouple(
                joints, "g000", "g003", "g006", new double[3], (double[])X.Clone(),
                new SourceMate { SwFeature = "Symmetric1", Type = "swMateSYMMETRIC" },
                MirrorScope.Plane, "the symmetric mate's plane", null, loops, "g000");

            Assert.Null(reason);
            var inputB = joints.Find(j => j.Id == "j011");
            Assert.NotNull(inputB.Coupling);
            Assert.Equal("linear_coupler", inputB.Coupling.Kind);
            Assert.Equal("j004", inputB.Coupling.DriverJoint);
            Assert.Equal(1.0, inputB.Coupling.Ratio ?? 0.0, 9);
            Assert.Null(joints.Find(j => j.Id == "j012").Coupling);
        }
    }
}
