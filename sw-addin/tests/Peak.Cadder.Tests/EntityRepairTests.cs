using System;
using System.Collections.Generic;
using System.IO;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// SolidWorks 2022 types the line of a point-on-line coincident as a
    /// point, and the held point as nothing. Read that way the coincident
    /// took every freedom and the straight slots of slot_slot welded to the
    /// base (2026-09-18). SolidWorks 2024 typed the same mates correctly
    /// and the slots slid (2026-09-16).
    /// </summary>
    public class EntityRepairTests
    {
        private readonly ITestOutputHelper _out;

        public EntityRepairTests(ITestOutputHelper output)
        {
            _out = output;
        }

        [Fact]
        public void SlotSlotFromSolidWorks2022SlidesItsStraightSlots()
        {
            var options = new LogReplay.Options();
            options.FixedComponents.Add("c003");     // slot_base
            var graph = LogReplay.FromLog(
                File.ReadAllText(LogReplay.FixturePath("slot_slot_2022", "manifest.rig.json")),
                File.ReadAllLines(LogReplay.FixturePath("slot_slot_2022", "mates.log")),
                options);
            var outcome = LogReplay.Run(graph);
            _out.WriteLine(LogReplay.Report(graph, outcome));

            Assert.Equal(5, outcome.Grouping.Groups.Count);
            var revolute = new List<RigJoint>();
            var prismatic = new List<RigJoint>();
            foreach (var j in outcome.Loops.Joints)
            {
                if (j.Type == JointType.Revolute) revolute.Add(j);
                else if (j.Type == JointType.Prismatic) prismatic.Add(j);
            }
            Assert.Equal(2, revolute.Count);
            Assert.Equal(2, prismatic.Count);

            // The axes SolidWorks 2024 gave: one slot along X, the other at
            // 45 degrees in the base plane.
            var axes = new List<double[]>();
            foreach (var j in prismatic) axes.Add(j.Axis);
            Assert.Contains(axes, a => Along(a, new[] { 1.0, 0.0, 0.0 }));
            Assert.Contains(axes, a => Along(a, new[] { Math.Sqrt(0.5), Math.Sqrt(0.5), 0.0 }));
        }

        [Fact]
        public void TwoPointsAtOnePlaceStayPoints()
        {
            var mate = Coincident(
                Entity("point", new[] { 0.1, 0.2, 0.0 }, new[] { 1.0, 0.0, 0.0 }),
                Entity("unknown", new[] { 0.1, 0.2, 0.0 }, new[] { 1.0, 0.0, 0.0 }));
            Assert.Equal(0, EntityRepair.Apply(Graph(mate), null));
            Assert.Equal("point", mate.Entities[0].EntityTypeName);
            Assert.Equal("unknown", mate.Entities[1].EntityTypeName);
        }

        [Fact]
        public void APointOffTheLineIsNotReadAsHeldOnIt()
        {
            var mate = Coincident(
                Entity("point", new[] { 0.0, 0.0, 0.0 }, new[] { 1.0, 0.0, 0.0 }),
                Entity("unknown", new[] { 0.05, 0.001, 0.0 }, new[] { 1.0, 0.0, 0.0 }));
            Assert.Equal(0, EntityRepair.Apply(Graph(mate), null));
            Assert.Equal("point", mate.Entities[0].EntityTypeName);
        }

        [Fact]
        public void APointHeldOnALineMakesTheLine()
        {
            var mate = Coincident(
                Entity("unknown", new[] { 0.03, 0.03, 0.01 }, new[] { 1.0, 0.0, 0.0 }),
                Entity("point", new[] { 0.0, 0.0, 0.01 }, new[] { Math.Sqrt(0.5), Math.Sqrt(0.5), 0.0 }));
            Assert.Equal(1, EntityRepair.Apply(Graph(mate), null));
            Assert.Equal("edge", mate.Entities[1].EntityTypeName);
            Assert.True(Along(mate.Entities[1].Direction, new[] { Math.Sqrt(0.5), Math.Sqrt(0.5), 0.0 }));
            Assert.Equal("vertex", mate.Entities[0].EntityTypeName);
            Assert.Null(mate.Entities[0].Direction);
        }

        [Fact]
        public void OnlyACoincidentIsRepaired()
        {
            var mate = Coincident(
                Entity("point", new[] { 0.0, 0.0, 0.0 }, new[] { 1.0, 0.0, 0.0 }),
                Entity("unknown", new[] { 0.05, 0.0, 0.0 }, new[] { 1.0, 0.0, 0.0 }));
            mate.TypeName = "swMateDISTANCE";
            mate.TypeValue = 5;
            Assert.Equal(0, EntityRepair.Apply(Graph(mate), null));
        }

        private static bool Along(double[] a, double[] b)
        {
            return a != null && Math.Abs(Math.Abs(MathOps.Dot(MathOps.Normalized(a), b)) - 1.0) < 1e-6;
        }

        private static GraphMateEntity Entity(string kind, double[] point, double[] raw)
        {
            return new GraphMateEntity
            {
                ComponentId = "c001",
                EntityTypeName = kind,
                Point = point,
                RawDirection = raw,
            };
        }

        private static GraphMate Coincident(GraphMateEntity a, GraphMateEntity b)
        {
            var m = new GraphMate { FeatureName = "Coincident1", TypeName = "swMateCOINCIDENT", TypeValue = 0 };
            m.Entities.Add(a);
            m.Entities.Add(b);
            return m;
        }

        private static MateGraph Graph(GraphMate mate)
        {
            var g = new MateGraph();
            g.Mates.Add(mate);
            return g;
        }
    }
}
