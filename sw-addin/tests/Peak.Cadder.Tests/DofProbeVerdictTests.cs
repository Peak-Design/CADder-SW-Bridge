using Peak.Cadder.Core.Model;
using Peak.Cadder.Sw;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A probe reading that the mapping cannot name ends as Free. Free here
    /// means "I do not know", so it must not stay Characterised: a
    /// characterised Free verdict against a revolute was reported as the
    /// solver disagreeing with the mates, and the joint lost its confidence
    /// on a reading that said nothing.
    /// </summary>
    public class DofProbeVerdictTests
    {
        private static ProbeVerdict Mapped(
            bool r1, bool r2, bool l1, bool l2,
            double[] r1Dir, double[] r1Pt, double[] r2Dir, double[] r2Pt,
            double[] l1Dir, double[] l2Dir)
        {
            var verdict = new ProbeVerdict { Characterised = true };
            DofProbe.Map(verdict, 0, r1, r2, l1, l2, r1Dir, r1Pt, r2Dir, r2Pt, l1Dir, l2Dir);
            return verdict;
        }

        [Fact]
        public void AStaticSlotWhoseVectorCameBackNullIsNotCharacterised()
        {
            // Rpoint1 and Rdir1 Static, but the COM vector came back null:
            // every flag is false and the mapping says Free.
            var verdict = Mapped(false, false, false, false,
                                 null, null, null, null, null, null);
            Assert.Equal(JointType.Free, verdict.Type);
            Assert.False(verdict.Characterised);
        }

        [Fact]
        public void ATurnAndASlideThatAreNotParallelAreNotCharacterised()
        {
            var verdict = Mapped(true, false, true, false,
                                 new[] { 0.0, 0, 1 }, new[] { 0.0, 0, 0 }, null, null,
                                 new[] { 1.0, 0, 0 }, null);
            Assert.Equal(JointType.Free, verdict.Type);
            Assert.False(verdict.Characterised);
        }

        [Fact]
        public void AHingeStaysCharacterised()
        {
            var verdict = Mapped(true, false, false, false,
                                 new[] { 0.0, 0, 1 }, new[] { 0.0, 0, 0 }, null, null,
                                 null, null);
            Assert.Equal(JointType.Revolute, verdict.Type);
            Assert.True(verdict.Characterised);
        }
    }
}
