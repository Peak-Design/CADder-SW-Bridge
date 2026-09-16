using System;
using System.Collections.Generic;
using Peak.SwToBlender.Core;
using Peak.SwToBlender.Core.Model;
using Xunit;

namespace Peak.SwToBlender.Tests
{
    /// <summary>The table coupling RelationProbe hands the consumer, built
    /// from what a turn of the driver read off the model.</summary>
    public class RelationTableTests
    {
        private static List<double[]> Cam(int steps, double turn)
        {
            // A cam lift of 10 mm: v = 0.01 (1 - cos x), read at `steps`
            // points over `turn` radians, driver values unwrapped.
            var raw = new List<double[]>();
            for (int k = 0; k <= steps; k++)
            {
                double x = turn * k / steps;
                raw.Add(new[] { x, 0.01 * (1.0 - Math.Cos(x)) });
            }
            return raw;
        }

        [Fact]
        public void AFullTurnIsOnePeriodicCycleEndingWhereItStarted()
        {
            var c = RelationTable.Build("j001", Cam(72, 2.0 * Math.PI), 2.0 * Math.PI, true);
            Assert.NotNull(c);
            Assert.Equal("table", c.Kind);
            Assert.Equal("j001", c.DriverJoint);
            Assert.True(c.Periodic);
            Assert.Equal(2.0 * Math.PI, c.Period, 12);
            Assert.Equal(0.0, c.Samples[0][0], 12);
            Assert.Equal(0.0, c.Samples[0][1], 12);
            Assert.Equal(2.0 * Math.PI, c.Samples[c.Samples.Length - 1][0], 12);
            Assert.Equal(0.0, c.Samples[c.Samples.Length - 1][1], 12);
            for (int i = 1; i < c.Samples.Length; i++)
                Assert.True(c.Samples[i][0] > c.Samples[i - 1][0], "x must ascend");
            Assert.Equal(0.02, RelationTable.Evaluate(c, Math.PI), 6);
            // Wraps: a turn and a bit reads like the bit.
            Assert.Equal(RelationTable.Evaluate(c, 0.7), RelationTable.Evaluate(c, 0.7 + 2.0 * Math.PI), 12);
            Assert.Equal(RelationTable.Evaluate(c, 0.7), RelationTable.Evaluate(c, 0.7 - 2.0 * Math.PI), 12);
        }

        [Fact]
        public void AStoppedTurnIsNotPeriodicAndClampsAtItsEnds()
        {
            // The driver got a third of the way round and stopped.
            var c = RelationTable.Build("j001", Cam(24, 2.0 * Math.PI / 3.0), 2.0 * Math.PI, true);
            Assert.NotNull(c);
            Assert.False(c.Periodic);
            Assert.Equal(0.0, c.Period, 12);
            double end = c.Samples[c.Samples.Length - 1][1];
            Assert.Equal(end, RelationTable.Evaluate(c, 5.0), 12);
            Assert.Equal(0.0, RelationTable.Evaluate(c, -1.0), 12);
        }

        [Fact]
        public void ReadingsAreRelativeToTheRestPoseAndSortedAndDeduplicated()
        {
            var raw = new List<double[]>
            {
                new[] { 1.0, 5.0 },       // rest: not zero in either value
                new[] { 1.5, 5.2 },
                new[] { 1.2, 5.1 },       // out of order
                new[] { 1.5 + 1e-9, 9.9 }, // the same x again
                new[] { double.NaN, 1.0 }, // a failed read
            };
            var c = RelationTable.Build("j001", raw, 2.0 * Math.PI, true);
            Assert.NotNull(c);
            Assert.False(c.Periodic);
            Assert.Equal(3, c.Samples.Length);
            Assert.Equal(new[] { 0.0, 0.0 }, c.Samples[0]);
            Assert.Equal(0.2, c.Samples[1][0], 12);
            Assert.Equal(0.1, c.Samples[1][1], 12);
            Assert.Equal(0.5, c.Samples[2][0], 12);
            Assert.Equal(0.2, c.Samples[2][1], 12);
        }

        [Fact]
        public void FewerThanTwoPointsIsNoTable()
        {
            Assert.Null(RelationTable.Build("j001", new List<double[]> { new[] { 0.0, 0.0 } }, 1.0, true));
            Assert.Null(RelationTable.Build("j001", null, 1.0, true));
        }

        [Fact]
        public void TheWriterEmitsTheSamplesAndTheParserSideKeys()
        {
            var m = new RigManifest();
            m.StepExport.File = "x.step";
            var g0 = new RigidGroup { Id = "g000", Name = "ground", Grounded = true };
            var g1 = new RigidGroup { Id = "g001", Name = "cam" };
            var g2 = new RigidGroup { Id = "g002", Name = "follower" };
            m.RigidGroups.AddRange(new[] { g0, g1, g2 });
            var cam = new RigJoint
            {
                Id = "j001", Type = JointType.Revolute, ParentGroup = "g000", ChildGroup = "g001",
                Origin = new double[3], Axis = new double[] { 0, 0, 1 }, SecondaryAxis = new double[] { 1, 0, 0 },
            };
            var follower = new RigJoint
            {
                Id = "j002", Type = JointType.Prismatic, ParentGroup = "g000", ChildGroup = "g002",
                Origin = new double[] { 0.05, 0, 0 }, Axis = new double[] { 1, 0, 0 }, SecondaryAxis = new double[] { 0, 0, 1 },
                Coupling = RelationTable.Build("j001", Cam(12, 2.0 * Math.PI), 2.0 * Math.PI, true),
            };
            m.Joints.AddRange(new[] { cam, follower });

            var parsed = TestJson.Parse(ManifestWriter.Write(m));
            var coupling = parsed["joints"].Items[1]["coupling"];
            Assert.Equal(
                new[] { "kind", "driver_joint", "ratio", "meters_per_radian", "lead_m_per_rev",
                        "samples", "periodic", "period" },
                coupling.Keys);
            Assert.Equal(13, coupling["samples"].Items.Count);
            Assert.Equal(2, coupling["samples"].Items[0].Items.Count);
        }
    }
}
