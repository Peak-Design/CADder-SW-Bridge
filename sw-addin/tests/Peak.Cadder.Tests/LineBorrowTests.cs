using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;
using static Peak.Cadder.Tests.FixtureBuilder;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A pair that came out free can borrow a mate written against a third
    /// body, when that mate names a line the third body's own joint cannot
    /// move (live spurgear.sldasm, 2026-09-16). A body a rack-pinion mate
    /// names is left to that mate instead. These tests keep that rule off
    /// bodies it was not meant for.
    /// </summary>
    public class LineBorrowTests
    {
        private static ClassificationResult Run(MateGraph graph)
        {
            return JointClassifier.Classify(graph, RigidGrouper.Group(graph));
        }

        private static readonly double[] MinusZ = { 0, 0, -1 };

        /// <summary>
        /// The spurgear placement: the first gear sits on three assembly
        /// planes, the second on two, and a distance to the first gear's
        /// axis stops its slide. Elsewhere in the machine, a pinion runs on
        /// a FIXED rack. The fixed rack is part of the ground, so the rule
        /// for rack bodies took in the ground, and every free pair with the
        /// ground lost the borrow: the second gear went out free.
        /// </summary>
        [Fact]
        public void AFixedRackDoesNotStopTheGroundLendingALine()
        {
            var distance = Mate("Distance1", "swMateDISTANCE",
                AxisEnt("c001", MinusZ, P(0, 0, 0)),
                AxisEnt("c002", MinusZ, P(0.04318, 0, 0)));
            distance.CurrentValue = 0.04318;
            distance.MinimumVariation = 0.04318;
            distance.MaximumVariation = 0.04318;

            var rackPinion = Mate("RackPinionMate1", "swMateRACKPINION",
                EdgeEnt("c003", X, P(0.2, 0.01, 0)),
                Cylinder("c004", Z, P(0.2, 0, 0), radius: 0.01));
            rackPinion.MetersPerRadian = 0.01;

            var graph = Graph(
                new[]
                {
                    Comp("c001", "gear one"),
                    Comp("c002", "gear two"),
                    Comp("c003", "rack", isFixed: true),
                    Comp("c004", "pinion"),
                },
                Mate("Coincident1", "swMateCOINCIDENT",
                    AxisEnt("c001", MinusZ, P(0, 0, 0)), PlaneEnt(null, Y, P(0, 0, 0))),
                Mate("Coincident2", "swMateCOINCIDENT",
                    AxisEnt("c001", MinusZ, P(0, 0, 0)), PlaneEnt(null, X, P(0, 0, 0))),
                Mate("Coincident4", "swMateCOINCIDENT",
                    PlaneEnt("c001", Z, P(0, 0, 0)), PlaneEnt(null, Z, P(0, 0, 0))),
                distance,
                Mate("Coincident3", "swMateCOINCIDENT",
                    AxisEnt("c002", MinusZ, P(0.04318, 0, 0)), PlaneEnt(null, Y, P(0, 0, 0))),
                Mate("Coincident5", "swMateCOINCIDENT",
                    PlaneEnt("c002", Z, P(0.04318, 0, 0)), PlaneEnt(null, Z, P(0, 0, 0))),
                Concentric("ConcentricPinion", "c003", "c004", Z, P(0.2, 0, 0)),
                CoincidentPlanes("CoincidentPinion", "c003", "c004", Z, P(0.2, 0, 0)),
                rackPinion);

            var grouping = RigidGrouper.Group(graph);
            var result = JointClassifier.Classify(graph, grouping);

            string gearTwo = grouping.ComponentGroup["c002"];
            RigJoint joint = null;
            foreach (var j in result.Joints)
                if (j.ChildGroup == gearTwo) joint = j;
            Assert.NotNull(joint);
            Assert.Equal(JointType.Revolute, joint.Type);
            Assert.Equal(0.04318, joint.Origin[0], 9);
            Assert.DoesNotContain(result.Warnings, w => w.Code == "UNDER_DEFINED");
        }
    }
}
