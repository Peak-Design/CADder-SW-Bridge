using System.Collections.Generic;
using System.IO;
using Peak.SwToBlender.Core;
using Peak.SwToBlender.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Peak.SwToBlender.Tests
{
    /// <summary>
    /// The SolidWorks 2022 API sample cam-follower (live 2026-09-15). No
    /// component is fixed: every part hangs off assembly planes, so the
    /// ground is the assembly's own geometry. The cam is held by its datum
    /// axis lying in two perpendicular assembly planes plus a face on the
    /// third: a hinge about the axis. That hinge is what the relation
    /// probe drives to read the cam table, so a free verdict here costs the
    /// whole mechanism.
    /// </summary>
    public class CamFollowerReplayTests
    {
        private readonly ITestOutputHelper _out;

        public CamFollowerReplayTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static LogReplay.Outcome Replay()
        {
            var graph = LogReplay.FromLog(
                File.ReadAllText(LogReplay.FixturePath("cam_follower", "manifest.rig.json")),
                File.ReadAllLines(LogReplay.FixturePath("cam_follower", "mates.log")),
                new LogReplay.Options());
            return LogReplay.Run(graph);
        }

        [Fact]
        public void TheCamHingesAboutItsDatumAxis()
        {
            var o = Replay();
            foreach (var j in o.Loops.Joints)
                _out.WriteLine(j.Id + " " + j.Type + " " + j.ParentGroup + "->" + j.ChildGroup
                               + " " + (j.Notes ?? ""));
            var cam = ToGround(o, "c003");
            Assert.NotNull(cam);
            Assert.Equal(JointType.Revolute, cam.Type);
            Assert.InRange(System.Math.Abs(cam.Axis[2]), 0.999, 1.0);
        }

        [Fact]
        public void TheFollowerSlidesAndTheLifterIsPlanarToGround()
        {
            var o = Replay();
            // The vertex follower (c002): two perpendicular assembly planes
            // hold its face, so it slides along their intersection.
            Assert.Equal(JointType.Prismatic, ToGround(o, "c002").Type);
            // The lifter (c001): one assembly plane, and a plane on the cam
            // whose normal is the cam's own axis, so the cam's turn never
            // moves it. Read against ground alone it is planar; the ring
            // through the cam leaves it one slide.
            var lifter = ToGround(o, "c001");
            Assert.Equal(JointType.Prismatic, lifter.Type);
            Assert.InRange(System.Math.Abs(lifter.Axis[0]), 0.999, 1.0);
        }

        [Fact]
        public void TheAnalysisIsAFixedPointOfItself()
        {
            var o = Replay();
            var all = new List<RigidGroup>(o.Grouping.Groups);
            all.AddRange(o.Classification.VirtualGroups);
            var again = LoopAnalyzer.Analyze(all, o.Loops.Joints);
            Assert.Equal(o.Loops.Joints.Count, again.Joints.Count);
            for (int i = 0; i < again.Joints.Count; i++)
            {
                Assert.Equal(o.Loops.Joints[i].Id, again.Joints[i].Id);
                Assert.Equal(o.Loops.Joints[i].Type, again.Joints[i].Type);
            }
            Assert.Equal(o.Loops.Loops.Count, again.Loops.Count);
            for (int i = 0; i < again.Loops.Count; i++)
                Assert.Equal(o.Loops.Loops[i].ClosureJoint, again.Loops[i].ClosureJoint);
        }

        [Fact]
        public void WithTheTablesKnownTheCamIsTheInput()
        {
            var o = Replay();
            var cam = ToGround(o, "c003");
            // What the relation probe adds after the first analysis: the
            // followers' own joints follow the cam through tables.
            foreach (string follower in new[] { "c001", "c002" })
            {
                var j = ToGround(o, follower);
                j.Coupling = new JointCoupling
                {
                    Kind = "table", DriverJoint = cam.Id,
                    Samples = new[] { new[] { 0.0, 0.0 }, new[] { 1.0, 0.01 } },
                };
            }
            var all = new List<RigidGroup>(o.Grouping.Groups);
            all.AddRange(o.Classification.VirtualGroups);
            var again = LoopAnalyzer.Analyze(all, o.Loops.Joints);
            LoopAnalyzer.PruneDrivenInputs(again);
            Assert.NotEmpty(again.Mechanisms);
            foreach (var mech in again.Mechanisms)
            {
                Assert.Equal(cam.Id, mech.Inputs[0].Joint);
                Assert.Single(mech.Inputs);
            }
            foreach (var lp in again.Loops)
                Assert.Equal(cam.Id, lp.SuggestedDriverJoint);
        }

        /// <summary>The joint between the grounded group and the group that
        /// owns the component.</summary>
        private static RigJoint ToGround(LogReplay.Outcome o, string componentId)
        {
            string group = o.Grouping.ComponentGroup[componentId];
            var grounded = new HashSet<string>();
            foreach (var g in o.Grouping.Groups) if (g.Grounded) grounded.Add(g.Id);
            foreach (var g in o.Classification.VirtualGroups) if (g.Grounded) grounded.Add(g.Id);
            foreach (var j in o.Loops.Joints)
            {
                if (j.ChildGroup == group && grounded.Contains(j.ParentGroup)) return j;
                if (j.ParentGroup == group && grounded.Contains(j.ChildGroup)) return j;
            }
            return null;
        }
    }
}
