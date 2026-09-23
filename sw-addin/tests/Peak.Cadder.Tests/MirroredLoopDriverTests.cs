using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A mirror between two bodies that each ride a loop of their own is a
    /// coupling between the two loops' drivers. The coupling gives the
    /// second driver a driver of its own, and ExportCommand then analyzes
    /// the loops again. That second analysis must keep the coupled joint as
    /// its loop's driver: inside the loop's solved chain, the loop sets the
    /// joint and the coupling does nothing.
    /// </summary>
    public class MirroredLoopDriverTests
    {
        private static RigLoop LoopWith(LoopAnalysisResult result, string joint)
        {
            return result.Loops.Find(lp => lp.MemberJoints.Contains(joint));
        }

        /// <summary>
        /// Two mirror-image four-bars on one frame, with a symmetric mate
        /// between their couplers. Each crank drives its own four-bar, and
        /// crank B follows crank A. The second analysis read crank B as a
        /// joint a coupling writes, gave four-bar B to its rocker, and left
        /// crank B inside the solved chain: posing crank A left four-bar B
        /// still.
        /// </summary>
        [Fact]
        public void TheSecondAnalysisKeepsTheCoupledCrankAsItsLoopsDriver()
        {
            MirroredFourBars.Build(out var groups, out var joints, out var graph, out var grouping);
            var loops = LoopAnalyzer.Analyze(groups, joints);
            Assert.Equal("j001", LoopWith(loops, "j001").SuggestedDriverJoint);
            Assert.Equal("j005", LoopWith(loops, "j005").SuggestedDriverJoint);

            var warnings = SymmetricCoupler.Resolve(graph, grouping, loops.Joints, loops.Loops);
            Assert.Empty(warnings);
            var crankB = loops.Joints.Find(j => j.Id == "j005");
            Assert.Equal("gear", crankB.Coupling.Kind);
            Assert.Equal("j001", crankB.Coupling.DriverJoint);

            // As ExportCommand does once a coupling has a driver.
            var again = LoopAnalyzer.Analyze(groups, loops.Joints);
            LoopAnalyzer.PruneDrivenInputs(again);

            var loopA = LoopWith(again, "j001");
            var loopB = LoopWith(again, "j005");
            Assert.Equal("j001", loopA.SuggestedDriverJoint);
            Assert.Equal("j005", loopB.SuggestedDriverJoint);
            Assert.NotEqual("j005", loopB.ClosureJoint);
            Assert.Equal("j001", crankB.Coupling.DriverJoint);
            Assert.Empty(ConsumerRigCheck.EveryOption(groups, again));

            // Four-bar B follows crank A, so it offers no input of its own:
            // taken, its rocker would leave crank B in the solved chain.
            var mechB = again.Mechanisms.Find(m => m.LoopIds.Contains(loopB.Id));
            var only = Assert.Single(mechB.Inputs);
            Assert.Equal("j005", only.Joint);
        }
    }
}
