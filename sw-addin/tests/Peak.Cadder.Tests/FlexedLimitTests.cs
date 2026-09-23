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
            var sub = Comp("c000", "slide", isFixed: true);
            sub.Solving = "flexible";
            var plate = Comp("c001", "plate");
            plate.ParentId = "c000";
            plate.FixedInSubassembly = true;
            var puck = Comp("c002", "puck");
            puck.ParentId = "c000";
            puck.MatePoseDelta = RotationAboutZThrough(Math.PI / 2.0, 0.1, 0.0);

            var graph = Graph(
                new[]
                {
                    sub,
                    plate,
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

        /// <summary>
        /// A two-link arm in a flexible subassembly: the first link turns on
        /// the fixed base about Z at the origin, and the second on the first
        /// at x = 0.1 in the document. The instance has the first link turned
        /// a quarter turn, carrying the second with it. The elbow is then at
        /// y = 0.1, not at x = 0.1 where the document's mates put it, and
        /// the shoulder, on the part that did not move, stays where it is
        /// (live 2026-09-23: a four-bar posed flexible had its coupler pins
        /// 58 and 29 mm off).
        /// </summary>
        [Fact]
        public void AJointOnAFlexedLinkIsWhereTheLinkIs()
        {
            var sub = Comp("c000", "arm", isFixed: true);
            sub.Solving = "flexible";
            var fixedBase = Comp("c001", "base");
            fixedBase.ParentId = "c000";
            fixedBase.FixedInSubassembly = true;
            var upper = Comp("c002", "upper");
            upper.ParentId = "c000";
            upper.MatePoseDelta = RotationAboutZThrough(Math.PI / 2.0, 0.0, 0.0);
            var lower = Comp("c003", "lower");
            lower.ParentId = "c000";
            lower.MatePoseDelta = RotationAboutZThrough(Math.PI / 2.0, 0.0, 0.0);

            var graph = Graph(
                new[] { sub, fixedBase, upper, lower },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0.01)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0.01)),
                Concentric("Concentric2", "c002", "c003", Z, P(0.1, 0, 0.02)),
                CoincidentPlanes("Coincident2", "c002", "c003", Z, P(0.1, 0, 0.02)));

            var grouping = RigidGrouper.Group(graph);
            var result = JointClassifier.Classify(graph, grouping);

            Assert.Equal(2, result.Joints.Count);
            RigJoint shoulder = null, elbow = null;
            foreach (var j in result.Joints)
            {
                if (j.ChildGroup == grouping.ComponentGroup["c002"]) shoulder = j;
                if (j.ChildGroup == grouping.ComponentGroup["c003"]) elbow = j;
            }
            Assert.NotNull(shoulder);
            Assert.NotNull(elbow);
            Assert.Equal(0.0, shoulder.Origin[0], 9);
            Assert.Equal(0.0, shoulder.Origin[1], 9);
            Assert.Equal(0.0, elbow.Origin[0], 9);
            Assert.Equal(0.1, elbow.Origin[1], 9);
            Assert.Equal(1.0, Math.Abs(elbow.Axis[2]), 9);
        }

        /// <summary>
        /// The flexible hinge of live corpus 07, with its leaf flexed 45
        /// degrees from the document pose, and an angle limit written in the
        /// TOP assembly between the leaf and the baseplate. The reader lifts
        /// only a mate that lives in the subassembly's document. A top-level
        /// mate is read at the instance pose already, so its rest value must
        /// not move by the flex a second time.
        /// </summary>
        [Fact]
        public void ATopLevelLimitOnAFlexedLeafIsNotShiftedAgain()
        {
            var subFrame = Comp("c001", "hinge");
            subFrame.Solving = "flexible";
            var fixedBase = Comp("c002", "hinge base");
            fixedBase.ParentId = "c001";
            fixedBase.FixedInSubassembly = true;
            var leaf = Comp("c003", "hinge leaf");
            leaf.ParentId = "c001";
            leaf.MatePoseDelta = RotationAboutZThrough(Math.PI / 4.0, 0.04, 0.02);

            // The angle the SolidWorks top assembly reports: the instance's.
            double current = 0.5236 + Math.PI / 4.0;
            var graph = Graph(
                new[]
                {
                    subFrame,
                    fixedBase,
                    leaf,
                    Comp("c004", "baseplate", isFixed: true),
                },
                CoincidentPlanes("Coincident1", "c004", "c002", Z, P(0.04, 0.02, 0.01)),
                CoincidentPlanes("Coincident4", "c004", "c002", Y, P(0.01, 0, 0.01)),
                Mate("Distance2", "swMateDISTANCE",
                    PlaneEnt("c001", X, P(0.04, 0.02, 0.02)),
                    PlaneEnt("c004", X, P(0, 0, 0.01))),
                Concentric("Concentric1", "c002", "c003", Z, P(0.04, 0.02, 0.028)),
                CoincidentPlanes("CoincidentSub", "c001", "c003", Z, P(0.04, 0.02, 0.02)),
                AngleLimit("LimitAngleTop", "c004", "c003",
                    X, P(Math.Cos(current), Math.Sin(current), 0), P(0.04, 0.02, 0.02),
                    min: 0.0, max: 1.5708, current: current));

            var result = JointClassifier.Classify(graph, RigidGrouper.Group(graph));

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Revolute, joint.Type);
            Assert.NotNull(joint.RotationLimit);
            Assert.Equal(current, joint.RotationLimit.ValueAtRest, 9);
        }
    }
}
