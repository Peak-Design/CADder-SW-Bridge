using System;
using System.IO;
using Peak.Cadder.Sw;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A STEP export to a network path is built on a local disk first.
    /// SolidWorks names the file's root PRODUCT after the file it writes,
    /// so the local file must have the target's exact name. Only its folder
    /// may differ.
    /// </summary>
    public class StepStagingTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "bf-staging-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        [Fact]
        public void TheScratchFileHasTheTargetsExactName()
        {
            string scratch = StepStaging.ScratchIn(@"Z:\Projects\Rig.step", _root);
            Assert.Equal("Rig.step", Path.GetFileName(scratch));
        }

        [Fact]
        public void TwoExportsOfOneTargetDoNotShareAFile()
        {
            string a = StepStaging.ScratchIn(@"Z:\Projects\Rig.step", _root);
            string b = StepStaging.ScratchIn(@"Z:\Projects\Rig.step", _root);
            Assert.NotEqual(a, b);
            Assert.True(Directory.Exists(Path.GetDirectoryName(a)));
        }

        [Fact]
        public void TheScratchFolderGoesWithTheFile()
        {
            string scratch = StepStaging.ScratchIn(@"Z:\Projects\Rig.step", _root);
            File.WriteAllText(scratch, "ISO-10303-21;");
            StepStaging.RemoveScratch(scratch);
            Assert.False(File.Exists(scratch));
            Assert.False(Directory.Exists(Path.GetDirectoryName(scratch)));
        }
    }
}
