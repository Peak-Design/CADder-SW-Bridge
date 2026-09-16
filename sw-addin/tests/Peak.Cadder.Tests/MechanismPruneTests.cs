using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A joint a coupling writes is not an input (live cam-follower,
    /// 2026-09-15: the lifter's slide, driven from the cam's hinge by a
    /// table, was offered beside the hinge and moved nothing).
    /// </summary>
    public class MechanismPruneTests
    {
        private static LoopAnalysisResult Tong(string chosen, string other)
        {
            var loops = new LoopAnalysisResult();
            loops.Joints.Add(new RigJoint { Id = "j001", Type = JointType.Revolute });
            loops.Joints.Add(new RigJoint
            {
                Id = "j002",
                Type = JointType.Prismatic,
                Coupling = new JointCoupling { Kind = "table", DriverJoint = "j001" },
            });
            var mech = new RigMechanism { Id = "mech001" };
            mech.Inputs.Add(new RigInputOption { Joint = chosen });
            mech.Inputs.Add(new RigInputOption { Joint = other });
            loops.Mechanisms.Add(mech);
            return loops;
        }

        [Fact]
        public void ADrivenAlternativeIsDropped()
        {
            var loops = Tong("j001", "j002");
            LoopAnalyzer.PruneDrivenInputs(loops);
            Assert.Single(loops.Mechanisms[0].Inputs);
            Assert.Equal("j001", loops.Mechanisms[0].Inputs[0].Joint);
            Assert.Single(loops.Notes);
            Assert.Contains("j002 dropped", loops.Notes[0]);
        }

        [Fact]
        public void ADrivenChosenInputStaysWithANote()
        {
            var loops = Tong("j002", "j001");
            LoopAnalyzer.PruneDrivenInputs(loops);
            Assert.Equal(2, loops.Mechanisms[0].Inputs.Count);
            Assert.Single(loops.Notes);
            Assert.Contains("chosen input j002", loops.Notes[0]);
        }
    }
}
