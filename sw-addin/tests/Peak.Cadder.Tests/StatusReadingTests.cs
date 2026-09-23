using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// When the status read with the limit mates out may weld a part.
    ///
    /// A limit mate counts as a fixed dimension, so a part behind one reads
    /// fully defined while it moves. The export takes every limit out,
    /// rebuilds, and welds what still reads fully defined. When a limit
    /// stayed in, or the rebuild failed, that reading is the one with the
    /// limits in, and a welded cutting head could not slide in Blender. The
    /// DOF probe cannot catch it: it counts a limit as fixed too. So the
    /// status is not read, and nothing is welded on it.
    /// </summary>
    public class StatusReadingTests
    {
        [Fact]
        public void EveryLimitOutAndRebuiltIsAReadingToUse()
        {
            Assert.Null(ExportCommand.StatusSkipReason(new string[0], true));
        }

        [Fact]
        public void ALimitThatStayedInStopsTheReading()
        {
            string why = ExportCommand.StatusSkipReason(
                new[] { "lead-screw.SLDASM: LimitDistance1" }, true);
            Assert.NotNull(why);
            Assert.Contains("LimitDistance1", why);
        }

        [Fact]
        public void AFailedRebuildStopsTheReading()
        {
            Assert.NotNull(ExportCommand.StatusSkipReason(new string[0], false));
        }
    }
}
