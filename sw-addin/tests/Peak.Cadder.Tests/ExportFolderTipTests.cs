using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The tooltip of "Export files to" in Export Options. It said that a
    /// direct send writes neither file and is not affected. Every Send to
    /// Blender and Refresh Model writes its mesh and its manifest into
    /// that folder, so a user who chose "Next to the assembly" for STEP
    /// files got a folder in the project on every send.
    /// </summary>
    public class ExportFolderTipTests
    {
        [Fact]
        public void TheTipSaysEverySendWritesThere()
        {
            string tip = BlenderOptionsDialog.ExportFolderTip;
            Assert.DoesNotContain("neither file", tip);
            Assert.Contains("Send to Blender", tip);
            Assert.Contains("Refresh Model", tip);
            Assert.Contains("mesh", tip);
        }
    }
}
