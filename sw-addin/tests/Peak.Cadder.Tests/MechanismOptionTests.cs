using System.Collections.Generic;
using System.IO;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A mechanism option is a complete alternative for ITS mechanism, and
    /// only for its mechanism: the consumer applies it and keeps every
    /// other loop as the manifest has it.
    /// </summary>
    public class MechanismOptionTests
    {
        private static readonly double[] Z = { 0, 0, 1 };

        private static RigJoint Pin(string id, string parent, string child, double x, double y)
        {
            return new RigJoint
            {
                Id = id,
                Type = JointType.Revolute,
                ParentGroup = parent,
                ChildGroup = child,
                Axis = (double[])Z.Clone(),
                SecondaryAxis = new double[] { 1, 0, 0 },
                Origin = new[] { x, y, 0.0 },
            };
        }

        private static List<RigidGroup> Groups(int count)
        {
            var groups = new List<RigidGroup>();
            for (int i = 0; i < count; i++)
                groups.Add(new RigidGroup
                {
                    Id = "g" + i.ToString("000"),
                    Name = "g" + i.ToString("000"),
                    Grounded = i == 0,
                });
            return groups;
        }

        /// <summary>
        /// A ram mechanism whose first choice needs two controls. Chosen
        /// from j009 it needs one, and the ram's stroke then stops j009.
        /// Beside it, on the same ground, a four-bar that takes either of
        /// its ground pins (j101 or j104).
        /// </summary>
        private static void RamMechanismBesideAFourBar(
            out List<RigidGroup> groups, out List<RigJoint> joints)
        {
            groups = Groups(11);
            var stroke = new RigJoint
            {
                Id = "j007",
                Type = JointType.Prismatic,
                ParentGroup = "g007",
                ChildGroup = "g006",
                Axis = MathOps.Normalized(new[] { 0.8716, -0.4903, 0.0 }),
                SecondaryAxis = new double[] { 0, 0, 1 },
                Origin = new[] { -0.236, -0.3495, 0.0 },
                TranslationLimit = new JointLimit { Min = -0.05, Max = 0.1, ValueAtRest = 0 },
            };
            joints = new List<RigJoint>
            {
                Pin("j001", "g002", "g003", 0.25, 0.891),
                Pin("j002", "g003", "g004", -0.427, -0.625),
                Pin("j003", "g005", "g004", 0.881, -0.989),
                Pin("j004", "g000", "g001", -0.418, 0.973),
                Pin("j005", "g001", "g002", -0.539, -0.065),
                Pin("j006", "g000", "g006", -0.564, -0.165),
                stroke,
                Pin("j008", "g007", "g005", 0.092, -0.534),
                Pin("j009", "g001", "g005", -0.507, -0.547),
                Pin("j101", "g000", "g008", 5, 0),
                Pin("j102", "g008", "g009", 5, 1),
                Pin("j103", "g009", "g010", 7, 1.2),
                Pin("j104", "g010", "g000", 7, 0),
            };
        }

        /// <summary>Every joint the mechanism's loops name, in any of its
        /// options.</summary>
        private static HashSet<string> Members(LoopAnalysisResult result, RigMechanism mech)
        {
            var members = new HashSet<string>();
            foreach (var lp in result.Loops)
                if (mech.LoopIds.Contains(lp.Id)) members.UnionWith(lp.MemberJoints);
            foreach (var option in mech.Inputs)
                foreach (var lp in option.Loops) members.UnionWith(lp.MemberJoints);
            return members;
        }

        /// <summary>
        /// An option re-chooses the whole model and keeps only its own
        /// mechanism's loops. The ram mechanism beside the four-bar came
        /// back from that re-choice with its first choice, not the one the
        /// manifest has. Its stroke-derived stop on j009 then showed as
        /// cleared in the four-bar's option, and taking j104 in Blender
        /// took the ram's stop away.
        /// </summary>
        [Fact]
        public void AnOptionChangesOnlyItsOwnMechanism()
        {
            RamMechanismBesideAFourBar(out var groups, out var joints);

            var result = LoopAnalyzer.Analyze(groups, joints);

            var j009 = result.Joints.Find(j => j.Id == "j009");
            Assert.NotNull(j009.RotationLimit);
            RigMechanism fourBar = null;
            foreach (var mech in result.Mechanisms)
            {
                var members = Members(result, mech);
                if (members.Contains("j101")) fourBar = mech;
                foreach (var option in mech.Inputs)
                {
                    foreach (string id in option.FlippedJoints)
                        Assert.True(members.Contains(id),
                                    mech.Id + " input " + option.Joint + " flips " + id);
                    foreach (var limit in option.JointLimits)
                        Assert.True(members.Contains(limit.Joint),
                                    mech.Id + " input " + option.Joint + " limits " + limit.Joint);
                }
            }
            Assert.NotNull(fourBar);
            var other = fourBar.Inputs.Find(o => o.Joint == "j104");
            Assert.NotNull(other);

            ConsumerRigCheck.Apply(result, fourBar, other, out var applied, out var loops);
            var stop = applied.Find(j => j.Id == "j009").RotationLimit;
            Assert.NotNull(stop);
            Assert.Equal(j009.RotationLimit.Min, stop.Min, 12);
            Assert.Equal(j009.RotationLimit.Max, stop.Max, 12);
            Assert.Empty(ConsumerRigCheck.EveryOption(groups, result));
        }

        /// <summary>
        /// Two copies of the SolidWorks 2022 tutorial plunger.sldasm on one
        /// ground. Met as they come, each copy's slider mechanism needs more
        /// controls than it has freedoms, and re-chosen from one input it
        /// needs one (live plunger.sldasm, 2026-09-15). The choice took the
        /// better input for the first copy only. For the second, the same
        /// input gave the same total count, because the first copy then went
        /// back to its first choice, and "not fewer" was refused. Two copies
        /// of one mechanism must be rigged the same way.
        /// </summary>
        [Fact]
        public void EveryMechanismTakesTheInputThatServesItBest()
        {
            var graph = LogReplay.FromLog(
                File.ReadAllText(LogReplay.FixturePath("plunger", "manifest.rig.json")),
                File.ReadAllLines(LogReplay.FixturePath("plunger", "mates.log")),
                new LogReplay.Options());
            foreach (var c in graph.Components) if (c.Path == "base_plunger-1") c.IsFixed = true;
            var grouping = RigidGrouper.Group(graph);
            var classification = JointClassifier.Classify(graph, grouping);
            var groups = new List<RigidGroup>(grouping.Groups);
            groups.AddRange(classification.VirtualGroups);
            string ground = groups.Find(g => g.Grounded).Id;

            // The second copy: every moving group and joint again, on the
            // same ground, with ids that sort after the first copy's.
            string Copy(string gid) => gid == ground ? gid : gid + "b";
            string CopyId(string jid) => "j1" + jid.Substring(1);
            var all = new List<RigidGroup>(groups);
            foreach (var g in groups)
                if (!g.Grounded) all.Add(new RigidGroup { Id = Copy(g.Id), Name = g.Name + " copy" });
            var joints = new List<RigJoint>(classification.Joints);
            foreach (var j in classification.Joints)
                joints.Add(new RigJoint
                {
                    Id = CopyId(j.Id),
                    Type = j.Type,
                    ParentGroup = Copy(j.ParentGroup),
                    ChildGroup = Copy(j.ChildGroup),
                    Origin = (double[])j.Origin?.Clone(),
                    Axis = (double[])j.Axis?.Clone(),
                    SecondaryAxis = (double[])j.SecondaryAxis?.Clone(),
                    RotationLimit = j.RotationLimit,
                    TranslationLimit = j.TranslationLimit,
                    ResidualKnown = j.ResidualKnown,
                    ResidualRot = j.ResidualRot,
                    ResidualRotDir = j.ResidualRotDir,
                });
            var firstCopy = new HashSet<string>();
            foreach (var j in classification.Joints) firstCopy.Add(j.Id);

            var result = LoopAnalyzer.Analyze(all, joints);

            var first = new List<string>();
            var second = new List<string>();
            foreach (var lp in result.Loops)
            {
                if (firstCopy.Contains(lp.ClosureJoint))
                    first.Add(CopyId(lp.SuggestedDriverJoint) + "/" + CopyId(lp.ClosureJoint)
                              + "/" + lp.ClosureKind);
                else
                    second.Add(lp.SuggestedDriverJoint + "/" + lp.ClosureJoint + "/" + lp.ClosureKind);
            }
            first.Sort(System.StringComparer.Ordinal);
            second.Sort(System.StringComparer.Ordinal);
            Assert.NotEmpty(first);
            Assert.Equal(first, second);
            foreach (var j in classification.Joints)
                Assert.Equal(j.Type, result.Joints.Find(k => k.Id == CopyId(j.Id)).Type);
            Assert.Empty(ConsumerRigCheck.EveryOption(all, result));
        }
    }
}
