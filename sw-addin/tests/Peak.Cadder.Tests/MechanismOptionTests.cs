using System;
using System.Collections.Generic;
using System.IO;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Peak.Cadder.Tests
{
    /// <summary>What a mechanism's inputs carry besides their loops.</summary>
    public class MechanismOptionTests
    {
        private readonly ITestOutputHelper _out;

        public MechanismOptionTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>
        /// TongRig (live 2026-09-14): a hydraulic tong whose cylinder has
        /// a point-to-point stroke limit. Driven from an arm's hinge, the
        /// exporter derives that hinge's angle range from the stroke (the
        /// closed form in LoopAnalyzer) and puts it on the hinge. That limit
        /// belongs to the configuration that made the hinge the ram's
        /// driver: an input under which the ram is driven from elsewhere
        /// has to take it off, or switching input in Blender keeps a stop
        /// that no longer means anything (and the actuator, 2026-09-15,
        /// lost its stop the other way round).
        /// </summary>
        [Fact]
        public void AnInputThatDoesNotDriveTheRamDropsTheDerivedStop()
        {
            var graph = LogReplay.FromLog(
                File.ReadAllText(LogReplay.FixturePath("tongrig", "manifest.rig.json")),
                File.ReadAllLines(LogReplay.FixturePath("tongrig", "mates.log")),
                new LogReplay.Options());
            var outcome = LogReplay.Run(graph);
            var loops = outcome.Loops;
            Assert.NotEmpty(loops.DerivedLimitJoints);
            foreach (var note in loops.Notes) _out.WriteLine("NOTE " + note);

            int checkedOptions = 0;
            foreach (var mech in loops.Mechanisms)
            {
                Assert.Empty(mech.Inputs[0].JointLimits);
                for (int k = 1; k < mech.Inputs.Count; k++)
                {
                    var opt = mech.Inputs[k];
                    _out.WriteLine(mech.Id + " input " + opt.Joint + " limits: "
                                   + string.Join(", ", opt.JointLimits.ConvertAll(
                                       l => l.Joint + (l.RotationLimit == null ? " free" : " limited"))));
                    foreach (string derived in loops.DerivedLimitJoints)
                    {
                        if (!mech.LoopIds.Exists(id => LoopUses(loops, id, derived))) continue;
                        bool stillDrivesTheRam = opt.Loops.Exists(
                            lp => lp.ClosureKind == "aim_pair" && lp.SuggestedDriverJoint == derived);
                        RigOptionLimit entry = opt.JointLimits.Find(l => l.Joint == derived);
                        if (stillDrivesTheRam)
                        {
                            // Same ram, same driver, same derived stop: no
                            // difference to carry.
                            Assert.True(entry == null || entry.RotationLimit != null,
                                        opt.Joint + " drops a stop the ram still needs");
                        }
                        else
                        {
                            Assert.NotNull(entry);
                            Assert.Null(entry.RotationLimit);
                        }
                        checkedOptions++;
                    }
                }
            }
            Assert.True(checkedOptions > 0, "the tong offers another input to check");
        }

        private static bool LoopUses(LoopAnalysisResult loops, string loopId, string jointId)
        {
            foreach (var lp in loops.Loops)
                if (lp.Id == loopId) return lp.MemberJoints.Contains(jointId);
            return false;
        }
    }
}
