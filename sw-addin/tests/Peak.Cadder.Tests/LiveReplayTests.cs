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
    /// Live exports replayed through the current engine from their logs.
    /// See LogReplay for what a log carries and what has to be assumed.
    /// </summary>
    public class LiveReplayTests
    {
        private readonly ITestOutputHelper _out;

        public LiveReplayTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static MateGraph TongRig(LogReplay.Options options)
        {
            return Fixture("tongrig", options);
        }

        private static MateGraph Fixture(string name, LogReplay.Options options)
        {
            return LogReplay.FromLog(
                File.ReadAllText(LogReplay.FixturePath(name, "manifest.rig.json")),
                File.ReadAllLines(LogReplay.FixturePath(name, "mates.log")),
                options);
        }

        /// <summary>
        /// The SolidWorks 2022 sample landing_gear.sldasm (2026-09-14): an
        /// oleo strut held to the assembly, a piston sliding in it (the
        /// wheel assembly and hub welded on by lock-rotation concentrics),
        /// and two sway links: one pinned to the side of the strut, one to
        /// the side of the piston, pinned to each other. Extend the strut
        /// and the links flatten. One degree of freedom, one loop with one
        /// slide whose neighbouring pins sit 60 mm OFF the stroke axis.
        ///
        /// The live export read that ring as a ram, aimed pin to pin, and
        /// the Blender side (rightly) refused the aim, fell back to IK and
        /// ended with every bone driven and no control. A slide is only a
        /// ram when its pins are on its line; this one closes with IK and
        /// keeps one input.
        /// </summary>
        [Fact]
        public void LandingGearIsASliderCrankNotARam()
        {
            var graph = Fixture("landing_gear", new LogReplay.Options());
            // The strut is the fixed component in SolidWorks; the log does
            // not record fixed flags, so the replay is told.
            foreach (var c in graph.Components)
                if (c.Path == "oleostrut-1") c.IsFixed = true;
            var outcome = LogReplay.Run(graph);
            _out.WriteLine(LogReplay.Report(graph, outcome));
            try
            {
                var dir = Environment.GetEnvironmentVariable("SWTB_REPLAY_OUT");
                if (!string.IsNullOrEmpty(dir))
                    ManifestWriter.WriteFile(
                        LogReplay.ToManifest(graph, outcome, "landing_gear.step"),
                        Path.Combine(dir, "landing_gear.rig.json"));
            }
            catch (Exception) { }

            Assert.Equal(4, outcome.Grouping.Groups.Count);
            var loop = Assert.Single(outcome.Loops.Loops);
            Assert.Equal("ik", loop.ClosureKind);
            // The driver touches the ground: the stroke or the strut pin.
            RigJoint driver = null;
            foreach (var j in outcome.Loops.Joints)
                if (j.Id == loop.SuggestedDriverJoint) driver = j;
            Assert.NotNull(driver);
            Assert.Equal("g000", driver.ParentGroup);
            // The slide is the INPUT: a rotational solver cannot change a
            // sliding joint, so the slide must not sit inside the solved
            // chain, and the only other place for it is the driver. The cut
            // is the pin between the piston and its sway link, so the two
            // links close on the piston's pin with rotations alone.
            Assert.Equal(JointType.Prismatic, driver.Type);
            var cut = FindJoint(outcome, loop.ClosureJoint);
            // A concentric alone, so cylindrical as mated; the cut is never
            // narrowed by the ring, only the tree joints are.
            Assert.Contains(cut.Type, new[] { JointType.Revolute, JointType.Cylindrical });
            Assert.Contains("g001", new[] { cut.ParentGroup, cut.ChildGroup });
        }

        /// <summary>
        /// The same sample, re-exported on 2026-09-16 with the wheel hub
        /// and the wheel assembly in one group. The ring then narrows the
        /// pin between the oleo piston and that group to FIXED: the wheel
        /// rides the piston and nothing turns it.
        ///
        /// Oscar: "the wheel_hub part rotates as I move the slider up and
        /// down, it should not, it should only slide. Switching Mechanism
        /// to upprsway_link - hinge and rotating the hinge produces the
        /// correct motion without the hub rotating."
        ///
        /// The cut had landed on that weld. A closure re-joins one point
        /// and the solver only rotates, so the hub arrived in the right
        /// place at the wrong angle, and only the input whose cut fell
        /// elsewhere posed correctly. A weld belongs in the tree.
        /// </summary>
        [Fact]
        public void TheLandingGearNeverClosesItsLoopOnAWeld()
        {
            var graph = Fixture("landing_gear_welded_hub", new LogReplay.Options());
            foreach (var c in graph.Components)
                if (c.Path == "oleostrut-1") c.IsFixed = true;
            var outcome = LogReplay.Run(graph);
            _out.WriteLine(LogReplay.Report(graph, outcome));

            var loop = Assert.Single(outcome.Loops.Loops);
            var welds = new List<string>();
            foreach (string id in loop.MemberJoints)
            {
                var j = FindJoint(outcome, id);
                if (j != null && j.Type == JointType.Fixed) welds.Add(id);
            }
            // The weld is the point of the fixture: without it the ring
            // never had the fault and the test proves nothing.
            Assert.NotEmpty(welds);

            Assert.DoesNotContain(loop.ClosureJoint, welds);
            foreach (var candidate in loop.DriverCandidates)
                Assert.DoesNotContain(candidate.ClosureJoint, welds);
            foreach (var mechanism in outcome.Loops.Mechanisms)
                foreach (var input in mechanism.Inputs)
                    foreach (var l in input.Loops)
                        Assert.DoesNotContain(l.ClosureJoint, welds);

            // And the slide is still the offered input: the cut moved, the
            // choice of driver did not.
            var driver = FindJoint(outcome, loop.SuggestedDriverJoint);
            Assert.Equal(JointType.Prismatic, driver.Type);
        }

        /// <summary>
        /// The SolidWorks 2022 sample wrench.sldasm, from the live export
        /// of 2026-09-16. The screw threads into the main grip and pushes
        /// the centerlink; the centerlink pins to arm2, arm2 to clamp2, and
        /// clamp2 back to the grip. Four pins and a screw around one ring
        /// is a FIVE-BAR: five freedoms, three spent closing the ring, so
        /// it takes two inputs.
        ///
        /// Oscar: "the arm2 assembly can rotate around a pin on clamp2, but
        /// the only control bone I am getting in Blender is the screw which
        /// IS correct, but there should be an additional revolute joint".
        /// The ring read as one input, the whole driven side was solved
        /// from the screw, and the second freedom had nowhere to go.
        ///
        /// Replayed from the manifest, not the log: one of the wrench's
        /// mates is a point on an edge the reader rebuilds from the
        /// selection, and the log of that export recorded that it had done
        /// so without recording the edge. The reader now writes the
        /// direction too, so a later export of this assembly replays whole.
        /// </summary>
        [Fact]
        public void TheWrenchIsAFiveBarAndTakesTwoInputs()
        {
            var inputs = ManifestReplay.Load(
                File.ReadAllText(LogReplay.FixturePath("wrench", "manifest.rig.json")));
            var loops = LoopAnalyzer.Analyze(inputs.Groups, inputs.Joints);
            foreach (var note in loops.Notes) _out.WriteLine("NOTE " + note);
            foreach (var j in loops.Joints)
                _out.WriteLine(j.Id + " " + j.Type + " " + j.ParentGroup + " -> " + j.ChildGroup);

            try
            {
                var dir = Environment.GetEnvironmentVariable("SWTB_REPLAY_OUT");
                if (!string.IsNullOrEmpty(dir))
                    ManifestWriter.WriteFile(
                        ManifestReplay.ToManifest(inputs, loops, "wrench.step"),
                        Path.Combine(dir, "wrench.rig.json"));
            }
            catch (Exception) { }

            var loop = Assert.Single(loops.Loops);
            _out.WriteLine(loop.Id + " members " + string.Join(",", loop.MemberJoints.ToArray())
                           + " cut " + loop.ClosureJoint + " driver " + loop.SuggestedDriverJoint
                           + " mobility " + loop.Mobility);
            Assert.True(loop.Planar);
            Assert.Equal(5, loop.MemberJoints.Count);
            Assert.Equal(2, loop.Mobility);

            // The screw is the input, and the ring leaves it alone: a five-bar
            // spends three of its five freedoms, and narrowing a member here
            // would spend a fourth.
            var driver = FindJoint(loops.Joints, loop.SuggestedDriverJoint);
            Assert.Equal(JointType.Screw, driver.Type);
            AssertLoopsMatchTheTree(loops.Loops, loops.Joints);
        }

        /// <summary>
        /// The SolidWorks 2022 sample actuator.sldasm (2026-09-14): a ram on
        /// the bracket works link1 (the crank); link1 drives the plunger
        /// along the bracket through two parallel links (link2-1, link2-2).
        /// One degree of freedom, three loops: the ram (aim pair) and the
        /// two slider-cranks. Every loop must name the SAME driver, the
        /// crank pin the ram works on, and it must not matter in which
        /// order the components were walked: the live re-export with its
        /// components in another order met a slider loop before the ram
        /// loop, took the slide as the input, and Blender refused the rig
        /// as a dependency cycle between the two loops.
        /// </summary>
        [Fact]
        public void ActuatorLoopsShareTheCrankPinInAnyComponentOrder()
        {
            var graph = Fixture("actuator", new LogReplay.Options());
            foreach (var c in graph.Components)
                if (c.Path == "mounting_bracket-1") c.IsFixed = true;
            var first = DriverOf(graph);
            _out.WriteLine("as exported: " + first);

            // Reverse the walk order: the list AND the ids, so neither the
            // engine's list order nor its id order can tell the two apart.
            var reversed = Fixture("actuator", new LogReplay.Options());
            foreach (var c in reversed.Components)
                if (c.Path == "mounting_bracket-1") c.IsFixed = true;
            var rename = new Dictionary<string, string>();
            int n = reversed.Components.Count;
            for (int i = 0; i < n; i++)
                rename[reversed.Components[i].Id] = reversed.Components[n - 1 - i].Id;
            foreach (var c in reversed.Components)
            {
                c.Id = rename[c.Id];
                if (c.ParentId != null && rename.ContainsKey(c.ParentId))
                    c.ParentId = rename[c.ParentId];
            }
            foreach (var m in reversed.Mates)
                foreach (var e in m.Entities)
                    if (e.ComponentId != null && rename.ContainsKey(e.ComponentId))
                        e.ComponentId = rename[e.ComponentId];
            reversed.Components.Reverse();
            var second = DriverOf(reversed);
            _out.WriteLine("reversed:    " + second);

            Assert.Equal("mounting_bracket-1 -> link1-1 (revolute)", first);
            Assert.Equal(first, second);

            // The slider-crank loops offer the slide as the other input,
            // and taking it moves the cut to the pin between the plunger
            // and the link, so the crank side is what gets solved.
            var outcome = LogReplay.Run(graph);
            var byId = new Dictionary<string, RigJoint>();
            foreach (var j in outcome.Loops.Joints) byId[j.Id] = j;
            int sliderLoops = 0;
            foreach (var lp in outcome.Loops.Loops)
            {
                Assert.True(lp.DriverCandidates.Count >= 1, lp.Id + " has no candidates");
                Assert.Equal(lp.SuggestedDriverJoint, lp.DriverCandidates[0].DriverJoint);
                Assert.Equal(lp.ClosureJoint, lp.DriverCandidates[0].ClosureJoint);
                Assert.Equal(lp.ClosureKind, lp.DriverCandidates[0].ClosureKind);
                if (lp.ClosureKind != "ik") continue;
                sliderLoops++;
                Assert.Equal(2, lp.DriverCandidates.Count);
                var other = lp.DriverCandidates[1];
                Assert.Equal(JointType.Prismatic, byId[other.DriverJoint].Type);
                var cut = byId[other.ClosureJoint];
                Assert.Equal(byId[other.DriverJoint].ChildGroup, cut.ParentGroup);
                Assert.Equal("ik", other.ClosureKind);
            }
            Assert.Equal(2, sliderLoops);

            // The two links are identical parts on identical pins. One pin
            // is the cut, the other is not, and both are revolute: the ring
            // forbids the axial slide of each, whichever one is cut.
            var pathOf = new Dictionary<string, string>();
            foreach (var g in outcome.Grouping.Groups)
                foreach (var cid in g.Components)
                    foreach (var c in graph.Components)
                        if (c.Id == cid) pathOf[g.Id] = c.Path;
            var linkPins = new List<RigJoint>();
            foreach (var j in outcome.Loops.Joints)
                if (pathOf[j.ParentGroup] == "plunger-1" && pathOf[j.ChildGroup].StartsWith("link2-"))
                    linkPins.Add(j);
            Assert.Equal(2, linkPins.Count);
            foreach (var j in linkPins) Assert.Equal(JointType.Revolute, j.Type);
        }

        /// <summary>The one driver every loop of the mechanism names, as
        /// "parent -> child (type)" by component path, or a description of
        /// the disagreement.</summary>
        private static string DriverOf(MateGraph graph)
        {
            var outcome = LogReplay.Run(graph);
            var drivers = new HashSet<string>();
            foreach (var lp in outcome.Loops.Loops) drivers.Add(lp.SuggestedDriverJoint);
            Assert.Equal(3, outcome.Loops.Loops.Count);
            if (drivers.Count != 1)
                return "disagree: " + string.Join(", ", drivers);
            RigJoint driver = null;
            foreach (var j in outcome.Loops.Joints)
                if (drivers.Contains(j.Id)) driver = j;
            Assert.NotNull(driver);
            var pathOf = new Dictionary<string, string>();
            foreach (var g in outcome.Grouping.Groups)
                foreach (var cid in g.Components)
                    foreach (var c in graph.Components)
                        if (c.Id == cid) pathOf[g.Id] = c.Path;
            return pathOf[driver.ParentGroup] + " -> " + pathOf[driver.ChildGroup]
                + " (" + driver.Type.ToString().ToLowerInvariant() + ")";
        }

        /// <summary>
        /// The SolidWorks 2022 tutorial weldingrobot.sldasm (2026-09-15): a
        /// four-bar (support, link1, link2, weld arm) on a base. The
        /// support's joint read pairwise as a prismatic, the ring narrowed it
        /// to fixed AFTER the closures were chosen, and the loop shipped with
        /// a fixed joint as its driver: no control at all. The choice is
        /// made again with the narrowed types.
        /// </summary>
        [Fact]
        public void WeldingRobotDriverSurvivesNarrowing()
        {
            var graph = Fixture("weldingrobot", new LogReplay.Options());
            foreach (var c in graph.Components)
                if (c.Path == "baseplate-1") c.IsFixed = true;
            var outcome = LogReplay.Run(graph);
            _out.WriteLine(LogReplay.Report(graph, outcome));
            AssertLoopsMatchTheTree(outcome);
            foreach (var lp in outcome.Loops.Loops)
            {
                var driver = FindJoint(outcome, lp.SuggestedDriverJoint);
                Assert.NotNull(driver);
                Assert.NotEqual(JointType.Fixed, driver.Type);
                foreach (var c in lp.DriverCandidates)
                    Assert.NotEqual(JointType.Fixed, FindJoint(outcome, c.DriverJoint).Type);
            }
        }

        /// <summary>
        /// The SolidWorks 2022 tutorials lens_mount.sldasm and
        /// plunger.sldasm (2026-09-15): loops whose rings share edges. A
        /// later loop's cut swap moved an edge of an earlier ring into the
        /// tree, and the earlier loop's member list still described the old
        /// ring, so Blender refused both manifests: "member_joints do not
        /// match the tree path plus closure". Members are read off the
        /// final tree.
        /// </summary>
        [Theory]
        [InlineData("lens_mount", "table-1")]
        [InlineData("plunger", "base_plunger-1")]
        public void LoopMembersDescribeTheFinalTree(string fixture, string ground)
        {
            var graph = Fixture(fixture, new LogReplay.Options());
            foreach (var c in graph.Components)
                if (c.Path == ground) c.IsFixed = true;
            var outcome = LogReplay.Run(graph);
            _out.WriteLine(LogReplay.Report(graph, outcome));
            Assert.NotEmpty(outcome.Loops.Loops);
            AssertLoopsMatchTheTree(outcome);
        }

        /// <summary>
        /// Plunger and actuator, live 2026-09-15: picking another input in
        /// the Blender panel applied one loop's candidate on its own, the
        /// other loops of the mechanism kept the old driver, and the rig
        /// came back with three controls, then refused the next choice
        /// ("member_joints do not match the tree path plus closure"). Every
        /// input a mechanism offers is a complete alternative: the same
        /// loop ids, the chosen joint driving, and a tree the consumer's
        /// own check accepts once the option's flips are applied.
        /// </summary>
        [Theory]
        [InlineData("plunger", "base_plunger-1")]
        [InlineData("actuator", "mounting_bracket-1")]
        public void MechanismInputsAreCompleteAlternatives(string fixture, string ground)
        {
            var graph = Fixture(fixture, new LogReplay.Options());
            foreach (var c in graph.Components)
                if (c.Path == ground) c.IsFixed = true;
            var outcome = LogReplay.Run(graph);
            _out.WriteLine(LogReplay.Report(graph, outcome));
            foreach (var note in outcome.Loops.Notes) _out.WriteLine("NOTE " + note);
            try
            {
                var dir = Environment.GetEnvironmentVariable("SWTB_REPLAY_OUT");
                if (!string.IsNullOrEmpty(dir))
                    ManifestWriter.WriteFile(
                        LogReplay.ToManifest(graph, outcome, fixture + ".step"),
                        Path.Combine(dir, fixture + ".rig.json"));
            }
            catch (Exception) { }
            foreach (var lp in outcome.Loops.Loops)
            {
                var cands = new List<string>();
                foreach (var c in lp.DriverCandidates)
                    cands.Add(c.DriverJoint + "/" + c.ClosureJoint + "/" + c.ClosureKind);
                _out.WriteLine(lp.Id + " driver " + lp.SuggestedDriverJoint + " cut "
                               + lp.ClosureJoint + " " + lp.ClosureKind
                               + " members " + string.Join(",", lp.MemberJoints)
                               + " candidates " + string.Join(" ", cands));
            }

            Assert.NotEmpty(outcome.Loops.Mechanisms);
            var loopIds = new HashSet<string>();
            foreach (var lp in outcome.Loops.Loops) Assert.True(loopIds.Add(lp.Id));
            var covered = new HashSet<string>();
            foreach (var mech in outcome.Loops.Mechanisms)
                foreach (var id in mech.LoopIds) Assert.True(covered.Add(id), id + " in two mechanisms");
            Assert.True(covered.SetEquals(loopIds), "every loop belongs to one mechanism");

            bool anyChoice = false;
            foreach (var mech in outcome.Loops.Mechanisms)
            {
                Assert.NotEmpty(mech.Inputs);
                var first = mech.Inputs[0];
                Assert.Empty(first.FlippedJoints);
                Assert.Equal(mech.LoopIds, ToIds(first.Loops));
                foreach (var opt in mech.Inputs)
                {
                    _out.WriteLine(mech.Id + " input " + opt.Joint + ": "
                                   + string.Join(", ", ToIds(opt.Loops))
                                   + (opt.FlippedJoints.Count > 0
                                      ? " flips " + string.Join(",", opt.FlippedJoints) : ""));
                    Assert.Equal(mech.LoopIds, ToIds(opt.Loops));
                    Assert.Contains(opt.Loops, lp => lp.SuggestedDriverJoint == opt.Joint);
                    // Solving order: the loop the option's input drives comes
                    // first, so the consumer's chain truncation never
                    // strands the input's own chain behind a dependent loop.
                    Assert.Equal(opt.Joint, opt.Loops[0].SuggestedDriverJoint);

                    // Apply the option the way the consumer does.
                    var joints = new List<RigJoint>();
                    foreach (var j in outcome.Loops.Joints)
                    {
                        var c = new RigJoint();
                        c.Id = j.Id;
                        c.Type = j.Type;
                        c.ParentGroup = j.ParentGroup;
                        c.ChildGroup = j.ChildGroup;
                        if (opt.FlippedJoints.Contains(j.Id))
                        {
                            c.ParentGroup = j.ChildGroup;
                            c.ChildGroup = j.ParentGroup;
                        }
                        joints.Add(c);
                    }
                    var loops = new List<RigLoop>();
                    foreach (var lp in outcome.Loops.Loops)
                        if (!mech.LoopIds.Contains(lp.Id)) loops.Add(lp);
                    loops.AddRange(opt.Loops);
                    AssertLoopsMatchTheTree(loops, joints);
                }
                if (mech.Inputs.Count > 1) anyChoice = true;
            }
            Assert.True(anyChoice, "a slider-crank offers more than one input");
        }

        /// <summary>
        /// The live plunger export of 2026-09-15 (DOF probe and all), from
        /// its manifest. Each nut sits on a bolt through two link plates:
        /// welded to one plate, planar on the other, and the bolt's two
        /// pins (one per plate, on the arm) are coaxial. The probe could
        /// not weld the nuts (its readings were loop-locked), and the cut
        /// narrowing refused the planar as "a coupled motion this schema
        /// cannot say": the one motion the ring leaves the nut is the turn
        /// about the bolt, which is not the plane's own spin line. It is a
        /// hinge about the bolt, and the manifest can say that.
        /// </summary>
        [Fact]
        public void LivePlungerNutsHingeOnTheirBolts()
        {
            var inputs = ManifestReplay.Load(
                File.ReadAllText(LogReplay.FixturePath("plunger_live", "manifest.rig.json")));
            var before = new Dictionary<string, string>();
            foreach (var j in inputs.Joints) before[j.Id] = j.Type;
            Assert.Equal(JointType.Planar, before["j008"]);
            Assert.Equal(JointType.Planar, before["j009"]);

            var loops = LoopAnalyzer.Analyze(inputs.Groups, inputs.Joints);
            foreach (var note in loops.Notes) _out.WriteLine("NOTE " + note);
            foreach (var j in loops.Joints)
                _out.WriteLine(j.Id + " " + before[j.Id] + " -> " + j.Type + " origin "
                               + string.Join(",", Array.ConvertAll(j.Origin ?? new double[0],
                                                                    v => v.ToString("0.####"))));
            try
            {
                var dir = Environment.GetEnvironmentVariable("SWTB_REPLAY_OUT");
                if (!string.IsNullOrEmpty(dir))
                    ManifestWriter.WriteFile(
                        ManifestReplay.ToManifest(inputs, loops, "plunger.step"),
                        Path.Combine(dir, "plunger_live.rig.json"));
            }
            catch (Exception) { }

            foreach (var id in new[] { "j008", "j009" })
            {
                var nut = FindJoint(loops.Joints, id);
                Assert.Equal(JointType.Revolute, nut.Type);
                // About the bolt: the line of the arm's coaxial pins j003
                // and j007, not the plane's own origin.
                var pin = FindJoint(loops.Joints, "j003");
                Assert.Equal(1.0, Math.Abs(MathOps.Dot(nut.Axis, pin.Axis)), 6);
                var d = new[] { nut.Origin[0] - pin.Origin[0], nut.Origin[1] - pin.Origin[1],
                                nut.Origin[2] - pin.Origin[2] };
                Assert.True(MathOps.Norm(MathOps.Cross(d, pin.Axis)) < 1e-6,
                            id + " turns about a line off the bolt");
            }
            AssertLoopsMatchTheTree(loops.Loops, loops.Joints);
        }

        private static RigJoint FindJoint(IList<RigJoint> joints, string id)
        {
            foreach (var j in joints) if (j.Id == id) return j;
            return null;
        }

        private static List<string> ToIds(List<RigLoop> loops)
        {
            var ids = new List<string>();
            foreach (var lp in loops) ids.Add(lp.Id);
            return ids;
        }

        /// <summary>The consumer's own check (rig/graph.py): the tree is every
        /// non-free joint that is no loop's closure, and a loop's members
        /// must be exactly the tree path between its closure's ends plus
        /// the closure.</summary>
        private static void AssertLoopsMatchTheTree(LogReplay.Outcome outcome)
        {
            AssertLoopsMatchTheTree(outcome.Loops.Loops, outcome.Loops.Joints);
        }

        private static void AssertLoopsMatchTheTree(IList<RigLoop> loops, IList<RigJoint> joints)
        {
            var closures = new HashSet<string>();
            foreach (var lp in loops) closures.Add(lp.ClosureJoint);
            var adjacency = new Dictionary<string, List<RigJoint>>();
            foreach (var j in joints)
            {
                if (j.Type == JointType.Free || closures.Contains(j.Id)) continue;
                if (!adjacency.ContainsKey(j.ParentGroup)) adjacency[j.ParentGroup] = new List<RigJoint>();
                if (!adjacency.ContainsKey(j.ChildGroup)) adjacency[j.ChildGroup] = new List<RigJoint>();
                adjacency[j.ParentGroup].Add(j);
                adjacency[j.ChildGroup].Add(j);
            }
            foreach (var lp in loops)
            {
                RigJoint cj = null;
                foreach (var j in joints) if (j.Id == lp.ClosureJoint) cj = j;
                Assert.NotNull(cj);
                // Breadth-first from the closure's parent group to its child.
                var via = new Dictionary<string, RigJoint>();
                var queue = new Queue<string>();
                queue.Enqueue(cj.ParentGroup);
                via[cj.ParentGroup] = null;
                while (queue.Count > 0 && !via.ContainsKey(cj.ChildGroup))
                {
                    string g = queue.Dequeue();
                    if (!adjacency.ContainsKey(g)) continue;
                    foreach (var j in adjacency[g])
                    {
                        string other = j.ParentGroup == g ? j.ChildGroup : j.ParentGroup;
                        if (via.ContainsKey(other)) continue;
                        via[other] = j;
                        queue.Enqueue(other);
                    }
                }
                Assert.True(via.ContainsKey(cj.ChildGroup),
                            lp.Id + ": no tree path between the closure's ends");
                var path = new HashSet<string> { cj.Id };
                for (string g = cj.ChildGroup; via[g] != null; )
                {
                    var j = via[g];
                    path.Add(j.Id);
                    g = j.ParentGroup == g ? j.ChildGroup : j.ParentGroup;
                }
                var members = new HashSet<string>(lp.MemberJoints);
                Assert.True(members.SetEquals(path),
                            lp.Id + ": members " + string.Join(",", lp.MemberJoints)
                            + " but the tree path plus closure is " + string.Join(",", path));
                foreach (var c in lp.DriverCandidates)
                {
                    Assert.Contains(c.DriverJoint, members);
                    Assert.Contains(c.ClosureJoint, members);
                }
            }
        }

        private static RigJoint FindJoint(LogReplay.Outcome outcome, string id)
        {
            foreach (var j in outcome.Loops.Joints)
                if (j.Id == id) return j;
            return null;
        }

        /// <summary>
        /// TongRig (2026-09-14): a hydraulic tong. Base section c001 is
        /// held on the assembly's own planes (two of them through a
        /// symmetric mate with angled faces); two arms hinge on X-axis pins
        /// either side of it; two links tie the arms together; one cylinder
        /// sits between the arms, its body on a cone bore over the rod with
        /// a point-to-point stroke limit. One degree of freedom, three loops
        /// that share joints.
        ///
        /// The first live export ran on a stale DLL and welded all 31
        /// components into one body. The second, on the current DLL, welded
        /// 29: the stroke limit on the cylinder was left active while the
        /// base-to-arm pairs were probed, and a limit anywhere in a loop
        /// reads every pair of the loop rigid. That is a probe fault the
        /// replay cannot see (it has no solver), so what the replay pins is
        /// the ENGINE's answer given honest readings: the mates alone make
        /// this a mechanism.
        ///
        /// The fixture log is from the current add-in, so suppression and
        /// the limit's range come from the log itself: the cylinder's
        /// Open/Closed/Distance1 configuration mates are suppressed,
        /// LimitDistance1 runs 0.5873 to 0.9473 m and rests at its minimum.
        /// </summary>
        [Fact]
        public void TongRigIsNotOneWeldedBody()
        {
            var graph = TongRig(new LogReplay.Options());
            var outcome = LogReplay.Run(graph);
            string report = LogReplay.Report(graph, outcome);
            _out.WriteLine(report);
            try
            {
                var dir = Environment.GetEnvironmentVariable("SWTB_REPLAY_OUT");
                if (!string.IsNullOrEmpty(dir))
                {
                    string stem = "tongrig";
                    File.WriteAllText(Path.Combine(dir, stem + ".txt"), report);
                    ManifestWriter.WriteFile(
                        LogReplay.ToManifest(graph, outcome, "TongRig_1.step"),
                        Path.Combine(dir, stem + ".rig.json"));
                }
            }
            catch (Exception) { }

            // The one thing that must never happen again: everything in one
            // group. A tong with a hydraulic cylinder in it MOVES.
            Assert.True(outcome.Grouping.Groups.Count > 2,
                        "welded into " + outcome.Grouping.Groups.Count + " group(s)");

            // The base is held on three assembly planes, so it IS the ground.
            var ground = outcome.Grouping.Groups[0];
            Assert.True(ground.Grounded);
            // By name: component ids follow walk order, which differs
            // between exports of the same assembly.
            string baseId = null;
            foreach (var c in graph.Components)
                if (c.Path == "TongRig.01S-1") baseId = c.Id;
            Assert.NotNull(baseId);
            Assert.Contains(baseId, ground.Components);

            // One degree of freedom: every loop is driven from the same
            // input.
            var drivers = new HashSet<string>();
            foreach (var lp in outcome.Loops.Loops)
                drivers.Add(lp.SuggestedDriverJoint);
            Assert.Single(drivers);

            // ...and the ram closes as an aim pair on its own stroke, which
            // keeps the limit it was given: the log's range, 0.36 m wide.
            // The cut is never retyped by the loop narrowing, so it stays
            // the cylindrical the mates make it: SolidWorks lets the rod
            // spin in the barrel, only the ring stops it, and the aim
            // closure pins roll regardless.
            RigJoint stroke = null;
            foreach (var lp in outcome.Loops.Loops)
                if (lp.ClosureKind == "aim_pair")
                    foreach (var j in outcome.Loops.Joints)
                        if (j.Id == lp.ClosureJoint) stroke = j;
            Assert.NotNull(stroke);
            Assert.NotNull(stroke.TranslationLimit);
            Assert.Contains(stroke.Type, new[] { JointType.Prismatic, JointType.Cylindrical });
            Assert.InRange(stroke.TranslationLimit.Max - stroke.TranslationLimit.Min,
                           0.3599, 0.3601);
        }
    }
}
