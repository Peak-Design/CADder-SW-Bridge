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

        /// <summary>A read as Sample makes it: the driver unwrapped over one
        /// turn in five-degree steps, the output from `output`.</summary>
        private static List<double[]> Read(Func<double, double> output)
        {
            var raw = new List<double[]>();
            for (int k = 0; k <= 71; k++)
            {
                double x = 2.0 * Math.PI * k / 72;
                raw.Add(new[] { x, output(x) });
            }
            return raw;
        }

        [Fact]
        public void OneToOneTurningOutputEndsItsCycleAFullTurnOn()
        {
            // A universal joint read 1:1: the unwrapped output reaches
            // about 2 pi as the input comes round. Ending the cycle at 0
            // swept the output back a whole turn over the last step.
            var c = RelationTable.Build("j001", Read(x => x), 2.0 * Math.PI, true, true);
            Assert.True(c.Periodic);
            var last = c.Samples[c.Samples.Length - 1];
            Assert.Equal(2.0 * Math.PI, last[0], 12);
            Assert.Equal(2.0 * Math.PI, last[1], 9);
            // No step anywhere sweeps back.
            for (int i = 1; i < c.Samples.Length; i++)
                Assert.True(c.Samples[i][1] - c.Samples[i - 1][1] > 0, "output must keep turning");

            var reversed = RelationTable.Build("j001", Read(x => -x), 2.0 * Math.PI, true, true);
            Assert.Equal(-2.0 * Math.PI, reversed.Samples[reversed.Samples.Length - 1][1], 9);
        }

        [Fact]
        public void RockerThatComesBackEndsItsCycleAtRest()
        {
            var c = RelationTable.Build("j001", Read(x => 0.3 * Math.Sin(x)), 2.0 * Math.PI, true, true);
            Assert.True(c.Periodic);
            Assert.Equal(0.0, c.Samples[c.Samples.Length - 1][1], 12);
        }

        [Fact]
        public void SlidingOutputEndsItsCycleAtRest()
        {
            // A slide never winds: a cam's stroke comes back to its start.
            var c = RelationTable.Build("j001", Read(x => 0.01 * (1.0 - Math.Cos(x))), 2.0 * Math.PI, true, false);
            Assert.Equal(0.0, c.Samples[c.Samples.Length - 1][1], 12);
        }

        /// <summary>Drags the read takes to come round when each one lands
        /// at `fraction` of the asked step, as Sample counts them.</summary>
        private static int DragsToComeRound(int steps, double fraction)
        {
            double step = 2.0 * Math.PI / steps;
            double full = 2.0 * Math.PI - step / 2.0;
            double total = 0.0;
            int k = 0;
            while (total < full)
            {
                k++;
                total += step * fraction;
            }
            return k;
        }

        [Theory]
        [InlineData(72, 0.35)]   // the live cam sample landed at about 0.35
        [InlineData(72, 0.30)]   // a slightly slower drag
        [InlineData(72, 0.26)]   // just above the stall test
        [InlineData(360, 0.26)]
        [InlineData(8, 0.26)]
        public void EveryDragThatIsNotAStallComesRoundWithinTheBudget(int steps, double fraction)
        {
            // Sample stops at a drag under a quarter of the step. Any slower
            // drag must reach the full turn before the budget ends, or the
            // table misses the end of the profile and is not periodic.
            Assert.True(DragsToComeRound(steps, fraction) <= RelationProbe.MaxDrags(steps),
                DragsToComeRound(steps, fraction) + " drags needed, budget "
                + RelationProbe.MaxDrags(steps));
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
