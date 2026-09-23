using System;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;
using static Peak.Cadder.Tests.FixtureBuilder;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// Limits on a joint whose child sits in a flexed subassembly. The
    /// mates inside a flexible subassembly describe the document pose, and
    /// the child's MatePoseDelta carries them to the instance. The limit's
    /// value at rest moves by the part of that delta the joint measures,
    /// and by nothing else.
    /// </summary>
    public class FlexedLimitTests
    {
        private static readonly double[] MinusZ = { 0, 0, -1 };
        private static readonly double[] MinusY = { 0, -1, 0 };

        private static double[,] RotationAboutZThrough(double angle, double px, double py)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle);
            var m = MathOps.Identity4();
            m[0, 0] = c; m[0, 1] = -s;
            m[1, 0] = s; m[1, 1] = c;
            // t = (I - R) p for a turn about the line through p along Z.
            m[0, 3] = px - (c * px - s * py);
            m[1, 3] = py - (s * px + c * py);
            return m;
        }

        /// <summary>
        /// A puck in a slot along X, spinning about its own axis at x = 0.1,
        /// with a travel limit along the slot. The instance is flexed by a
        /// quarter turn of spin and no slide at all. The slide change was
        /// read as the translation column of the delta, which for a turn
        /// about a line off the origin is (I - R) p: 0.1 m along the slot
        /// here. The rest value moved from 0.05 to 0.15, outside its own
        /// range. The slide is the travel of a point on the joint.
        /// </summary>
        [Fact]
        public void ASpinOfAFlexedPinSlotDoesNotMoveItsTravel()
        {
            var puck = Comp("c002", "puck");
            puck.MatePoseDelta = RotationAboutZThrough(Math.PI / 2.0, 0.1, 0.0);

            var graph = Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    puck,
                },
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0.01)),
                Mate("Width1", "swMateWIDTH",
                    PlaneEnt("c001", Y, P(0, 0.05, 0.01)),
                    PlaneEnt("c001", MinusY, P(0, -0.05, 0.01)),
                    Cylinder("c002", MinusZ, P(0.1, 0, 0.02), 0.015)),
                DistanceLimit("LimitDistance1", "c001", "c002", X, P(0.05, 0, 0.01),
                    min: 0.0, max: 0.1, current: 0.05));

            var result = JointClassifier.Classify(graph, RigidGrouper.Group(graph));

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.PinSlot, joint.Type);
            Assert.NotNull(joint.TranslationLimit);
            Assert.Equal(0.05, joint.TranslationLimit.ValueAtRest, 9);
        }
    }
}
