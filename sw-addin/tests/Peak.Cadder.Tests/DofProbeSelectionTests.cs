using System;
using System.Collections.Generic;
using Peak.Cadder.Sw;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The DOF probe selects components to fix and unfix them, and that
    /// clears whatever the user had selected. The selection is read before
    /// the probe and put back after it, on every path out. With "only the
    /// selected components" on, an empty selection after an export made
    /// the next update from Blender export the whole assembly. Reading and
    /// selecting need a live SolidWorks. The order of the three steps does
    /// not, and is what these tests check.
    /// </summary>
    public class DofProbeSelectionTests
    {
        [Fact]
        public void TheSelectionComesBackAfterAReading()
        {
            var calls = new List<string>();
            int answer = DofProbe.KeepingSelection(
                () => { calls.Add("save"); return "picked"; },
                saved => calls.Add("restore " + saved),
                () => { calls.Add("probe"); return 7; });
            Assert.Equal(7, answer);
            Assert.Equal(new[] { "save", "probe", "restore picked" }, calls);
        }

        [Fact]
        public void TheSelectionComesBackWhenTheProbeFails()
        {
            var calls = new List<string>();
            Assert.Throws<InvalidOperationException>(() => DofProbe.KeepingSelection<int>(
                () => "picked",
                saved => calls.Add("restore " + saved),
                () => throw new InvalidOperationException("solver")));
            Assert.Equal(new[] { "restore picked" }, calls);
        }

        [Fact]
        public void ASelectionThatCannotBeReadDoesNotStopTheProbe()
        {
            var calls = new List<string>();
            int answer = DofProbe.KeepingSelection(
                () => throw new InvalidOperationException("no selection manager"),
                saved => calls.Add("restore"),
                () => 3);
            Assert.Equal(3, answer);
            Assert.Empty(calls);
        }
    }
}
