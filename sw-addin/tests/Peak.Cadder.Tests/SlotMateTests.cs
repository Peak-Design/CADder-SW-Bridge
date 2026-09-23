using System;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;
using static Peak.Cadder.Tests.FixtureBuilder;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A pin in a slot. The motion resolver has no rule for a slot mate,
    /// so the classifier applies it. A pin in a slot usually also has its
    /// head on the plate, and that face coincidence alone reads as planar.
    /// The slot must narrow that planar freedom, not come after it.
    /// </summary>
    public class SlotMateTests
    {
        private static ClassificationResult Run(MateGraph graph)
        {
            return JointClassifier.Classify(graph, RigidGrouper.Group(graph));
        }

        private static void AssertAlong(double[] expected, double[] actual)
        {
            Assert.NotNull(actual);
            Assert.Equal(1.0, Math.Abs(MathOps.Dot(expected, MathOps.Normalized(actual))), 9);
        }

        /// <summary>The slot mate as a pin in a slot along X: the pin's
        /// cylinder about Z and one of the slot's side walls, whose normal
        /// is Y.</summary>
        private static GraphMate SlotAlongX(int constraint)
        {
            var slot = Mate("Slot1", "swMateSLOT",
                Cylinder("c002", Z, P(0.02, 0.01, 0)),
                PlaneEnt("c001", Y, P(0.02, 0.014, 0)));
            slot.SlotConstraint = constraint;
            return slot;
        }

        /// <summary>A slot mate whose entities do not show the travel: the
        /// pin and the floor of the slot.</summary>
        private static GraphMate SlotWithNoTravel(int constraint)
        {
            var slot = Mate("Slot1", "swMateSLOT",
                Cylinder("c002", Z, P(0.02, 0.01, 0)),
                PlaneEnt("c001", Z, P(0, 0, 0)));
            slot.SlotConstraint = constraint;
            return slot;
        }

        private static MateGraph PinInPlate(GraphMate slot, bool headOnPlate)
        {
            var components = new[]
            {
                Comp("c001", "plate", isFixed: true),
                Comp("c002", "pin"),
            };
            return headOnPlate
                ? Graph(components, slot,
                    CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)))
                : Graph(components, slot);
        }

        /// <summary>
        /// A pin held at the center of its slot, head on the plate: only the
        /// spin about the pin is left. The face coincidence read as planar
        /// and returned before the slot was looked at, so the pin could go
        /// anywhere on the plate.
        /// </summary>
        [Theory]
        [InlineData(1)]    // centered
        [InlineData(2)]    // distance along the slot
        [InlineData(3)]    // percent along the slot
        public void APinHeldInItsSlotWithItsHeadOnThePlateOnlySpins(int constraint)
        {
            var result = Run(PinInPlate(SlotAlongX(constraint), headOnPlate: true));

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Revolute, joint.Type);
            AssertAlong(Z, joint.Axis);
            Assert.Equal(0.02, joint.Origin[0], 9);
            Assert.Equal(0.01, joint.Origin[1], 9);
        }

        /// <summary>A free slot with the head on the plate: the pin spins
        /// and slides along the slot, which is a pin-slot joint.</summary>
        [Theory]
        [InlineData(0)]     // free
        [InlineData(-1)]    // not read, taken as free
        public void AFreePinWithItsHeadOnThePlateSlidesAlongTheSlot(int constraint)
        {
            var result = Run(PinInPlate(SlotAlongX(constraint), headOnPlate: true));

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.PinSlot, joint.Type);
            AssertAlong(Z, joint.Axis);
            AssertAlong(X, joint.SecondaryAxis);
        }

        /// <summary>
        /// A free slot and nothing else. The slot branch took the first
        /// direction in the mate, the pin's own axis, as a prismatic: the
        /// pin moved out of the plate and the slot travel was lost.
        /// </summary>
        [Fact]
        public void AFreeSlotAloneNeverSlidesAlongThePin()
        {
            var result = Run(PinInPlate(SlotAlongX(0), headOnPlate: false));

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.PinSlot, joint.Type);
            AssertAlong(Z, joint.Axis);
            AssertAlong(X, joint.SecondaryAxis);
        }

        /// <summary>When the slot mate does not show which way the slot
        /// runs, a free slot alone has no joint type, and the pin axis is
        /// still no slide.</summary>
        [Fact]
        public void AFreeSlotWithNoTravelIsFreeNotAPrismaticAlongThePin()
        {
            var result = Run(PinInPlate(SlotWithNoTravel(0), headOnPlate: false));

            var joint = Assert.Single(result.Joints);
            Assert.NotEqual(JointType.Prismatic, joint.Type);
            Assert.Equal(JointType.Free, joint.Type);
            Assert.Contains(result.Warnings, w => w.Code == "UNDER_DEFINED");
        }

        /// <summary>A free slot that does not show its travel, head on the
        /// plate: planar is the best the mates give, and the joint says it
        /// is freer than the assembly.</summary>
        [Fact]
        public void AFreeSlotWithNoTravelStaysPlanarAndSaysSo()
        {
            var result = Run(PinInPlate(SlotWithNoTravel(0), headOnPlate: true));

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Planar, joint.Type);
            Assert.Equal("medium", joint.Confidence);
            Assert.Contains("slot", joint.Notes ?? "");
        }
    }
}
