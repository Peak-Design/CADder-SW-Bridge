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
    }
}
