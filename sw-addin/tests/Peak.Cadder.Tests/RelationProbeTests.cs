using System;
using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Peak.Cadder.Sw;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The pure parts of RelationProbe: which channel of the driven joint a
    /// table holds, how a turning output closes its cycle, and how long a
    /// read may go on. The drag itself needs a live SolidWorks.
    /// </summary>
    public class RelationProbeTests
    {
        [Fact]
        public void RevoluteTurnsAndPrismaticSlides()
        {
            bool turns;
            Assert.Null(RelationProbe.DrivenChannel(JointType.Revolute, false, 0.0, out turns));
            Assert.True(turns);
            Assert.Null(RelationProbe.DrivenChannel(JointType.Prismatic, false, 0.01, out turns));
            Assert.False(turns);
        }

        [Fact]
        public void PinInASlotThatSlidesIsNotReadAsATurn()
        {
            // The cam pushes the pin along its slot. A turn about the slide
            // direction is a channel a pin_slot locks, and a table on the
            // joint's turn would spin the pin instead of moving it.
            bool turns;
            var reason = RelationProbe.DrivenChannel(JointType.PinSlot, false, 0.008, out turns);
            Assert.NotNull(reason);
            Assert.Contains("slides", reason);
        }

        [Fact]
        public void PinInASlotThatOnlyTurnsIsReadAsItsTurn()
        {
            bool turns;
            Assert.Null(RelationProbe.DrivenChannel(JointType.PinSlot, false, 1e-9, out turns));
            Assert.True(turns);
        }

        [Fact]
        public void CamPlungerOnACylindricalJointIsNotTabledAsItsSpin()
        {
            // A round plunger mated concentric-only: the cam drives its
            // stroke, and its spin is whatever the drag gives it.
            bool turns;
            var reason = RelationProbe.DrivenChannel(JointType.Cylindrical, false, 0.01, out turns);
            Assert.NotNull(reason);
            Assert.Contains("slides", reason);
        }

        [Fact]
        public void CamRockerOnACylindricalJointIsTabledAsItsTurn()
        {
            bool turns;
            Assert.Null(RelationProbe.DrivenChannel(JointType.Cylindrical, false, 1e-8, out turns));
            Assert.True(turns);
        }

        [Fact]
        public void UniversalJointOutputOnACylindricalJointTurns()
        {
            bool turns;
            Assert.Null(RelationProbe.DrivenChannel(JointType.Cylindrical, true, 0.02, out turns));
            Assert.True(turns);
        }

        [Fact]
        public void NothingMovedLeavesTheTableFlatForARetry()
        {
            bool turns;
            Assert.Null(RelationProbe.DrivenChannel(JointType.Cylindrical, false, 1e-9, out turns));
            Assert.Null(RelationProbe.DrivenChannel(JointType.PinSlot, false, 0.0, out turns));
        }
    }
}
