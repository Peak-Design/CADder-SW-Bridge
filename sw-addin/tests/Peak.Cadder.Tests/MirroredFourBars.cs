using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using static Peak.Cadder.Tests.FixtureBuilder;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// Two four-bars hinged on one frame, one the mirror image of the other
    /// about the frame's X plane, with a symmetric mate between their
    /// couplers about that plane. Each is a crank and rocker: the 100 mm
    /// crank turns a full circle on the 400 mm base, the 300 mm rocker
    /// swings. Side B is side A with X negated.
    ///
    /// Groups: g000 frame, g001 crank A, g002 coupler A, g003 rocker A,
    /// g004 crank B, g005 coupler B, g006 rocker B.
    /// Joints: j001 to j004 on side A, j005 to j008 on side B, in the same
    /// order: crank hinge, crank pin, rocker pin, rocker hinge.
    /// </summary>
    internal static class MirroredFourBars
    {
        private const double RockerPinX = 0.49872189505317316;
        private const double RockerPinY = 0.2823875802126927;

        /// <param name="mates">How many symmetric mates hold the couplers.
        /// Two is the same relation declared twice.</param>
        public static void Build(
            out List<RigidGroup> groups, out List<RigJoint> joints, out MateGraph graph,
            out RigidGroupingResult grouping, int mates = 1)
        {
            groups = new List<RigidGroup>();
            grouping = new RigidGroupingResult();
            string[] names = { "frame", "crank A", "coupler A", "rocker A", "crank B", "coupler B", "rocker B" };
            var components = new List<GraphComponent>();
            for (int i = 0; i < names.Length; i++)
            {
                string gid = "g" + i.ToString("000");
                string cid = "c" + (i + 1).ToString("000");
                var g = new RigidGroup { Id = gid, Name = names[i], Grounded = i == 0, Components = { cid } };
                groups.Add(g);
                grouping.Groups.Add(g);
                grouping.ComponentGroup[cid] = gid;
                components.Add(Comp(cid, names[i], isFixed: i == 0));
            }

            joints = new List<RigJoint>();
            foreach (double side in new[] { 1.0, -1.0 })
            {
                int first = side > 0 ? 1 : 5;
                string frame = "g000";
                string crank = "g" + (side > 0 ? 1 : 4).ToString("000");
                string coupler = "g" + (side > 0 ? 2 : 5).ToString("000");
                string rocker = "g" + (side > 0 ? 3 : 6).ToString("000");
                joints.Add(Pin(first, frame, crank, P(side * 0.2, 0, 0)));
                joints.Add(Pin(first + 1, crank, coupler, P(side * 0.2, 0.1, 0)));
                joints.Add(Pin(first + 2, coupler, rocker, P(side * RockerPinX, RockerPinY, 0)));
                joints.Add(Pin(first + 3, frame, rocker, P(side * 0.6, 0, 0)));
            }

            var symmetric = new List<GraphMate>();
            for (int k = 1; k <= mates; k++)
                symmetric.Add(Mate("Symmetric" + k, "swMateSYMMETRIC",
                    PlaneEnt("c003", X, P(0.35, 0.2, 0)),
                    PlaneEnt("c006", X, P(-0.35, 0.2, 0)),
                    PlaneEnt(null, X, P(0, 0, 0))));
            graph = Graph(components.ToArray(), symmetric.ToArray());
        }

        private static RigJoint Pin(int number, string parent, string child, double[] origin)
        {
            return new RigJoint
            {
                Id = "j" + number.ToString("000"),
                Type = JointType.Revolute,
                ParentGroup = parent,
                ChildGroup = child,
                Axis = (double[])Z.Clone(),
                SecondaryAxis = (double[])X.Clone(),
                Origin = origin,
            };
        }
    }
}
