using System;
using System.Collections.Generic;
using System.IO;
using Peak.Cadder.Bridge;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// Which CADder works with this add-in, and the question before a send
    /// to one that does not.
    ///
    /// The two halves work together when the first two numbers of their
    /// versions are the same. Oscar, 2026-09-23: "add a dialoge warning
    /// before anything is even sent with a download link to the new version
    /// and give them an option to abort or continue".
    /// </summary>
    public class VersionMatchTests : IDisposable
    {
        private readonly Func<System.Windows.Forms.IWin32Window, VersionMatch.Mismatch, bool> _ask;

        public VersionMatchTests()
        {
            _ask = VersionGate.Ask;
            VersionGate.Reset();
        }

        public void Dispose()
        {
            VersionGate.Ask = _ask;
            VersionGate.Reset();
        }

        [Theory]
        [InlineData("1.1.0", "1.1.9", true)]
        [InlineData("1.1.1", "1.1.0", true)]
        [InlineData("1.2.0", "1.1.9", false)]
        [InlineData("2.1.0", "1.1.0", false)]
        [InlineData("v1.1.0", "1.1.3", true)]     // this add-in says "v1.1.0"
        [InlineData("v1.2.0", "1.1.3", false)]
        [InlineData("1.1.0.0", "1.1.3", true)]
        public void TheFirstTwoNumbersDecide(string a, string b, bool match)
        {
            Assert.Equal(match, VersionMatch.Match(a, b));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("unknown")]
        public void AVersionThatIsNotKnownDecidesNothing(string version)
        {
            Assert.Null(VersionMatch.Match(version, "1.1.0"));
            Assert.Null(VersionMatch.For("v1.1.0", version, "CADder"));
        }

        [Fact]
        public void AnOlderAddInIsTheOneToUpdate()
        {
            var m = VersionMatch.For("v1.1.1", "1.2.0", "CADder");
            Assert.NotNull(m);
            Assert.Equal("CADder Bridge", m.Update);
            Assert.Equal(VersionMatch.BridgeReleases, m.Url);
            Assert.Equal("Download CADder Bridge 1.2", m.LinkText);
            Assert.Contains("CADder Bridge: 1.1.1", m.Text);
            Assert.Contains("CADder: 1.2.0", m.Text);
            Assert.Contains("Update CADder Bridge to 1.2.", m.Text);
        }

        [Fact]
        public void AnOlderCadderIsTheOneToUpdate()
        {
            var m = VersionMatch.For("v1.3.0", "1.2.4", "CADder");
            Assert.Equal("CADder", m.Update);
            Assert.Equal(VersionMatch.CadderReleases, m.Url);
            Assert.Equal("Download CADder 1.3", m.LinkText);
        }

        [Fact]
        public void CadderProHasItsOwnLastNumber()
        {
            Assert.Null(VersionMatch.For("v1.2.0", "1.2.5", "CADder Pro"));
            var m = VersionMatch.For("v1.3.0", "1.2.5", "CADder Pro");
            Assert.Equal("CADder Pro", m.Update);
            // CADder Pro has no public download to send the user to.
            Assert.Equal("", m.Url);
            Assert.Contains("from where you got it", m.Advice);
        }

        [Fact]
        public void ABridgeBeforeOnePointOnePointOneIsCadder()
        {
            var m = VersionMatch.For("v1.2.0", "1.1.0", null);
            Assert.Equal("CADder", m.AddonName);
            Assert.Equal(VersionMatch.CadderReleases, m.Url);
        }

        private static BlenderInstance Blender(string version, string name = "CADder")
        {
            return new BlenderInstance { Pid = 1, Port = 1, Token = "t",
                                         AddonVersion = version, AddonName = name };
        }

        /// <summary>The first two numbers of this add-in, one higher: a
        /// CADder that does not match it.</summary>
        private static string Newer()
        {
            var mine = VersionMatch.MajorMinor(AddIn.AddInVersion);
            return mine[0] + "." + (mine[1] + 1) + ".0";
        }

        [Fact]
        public void AMatchingCadderIsNotAsked()
        {
            int asked = 0;
            VersionGate.Ask = (o, m) => { asked++; return false; };
            var mine = VersionMatch.Plain(AddIn.AddInVersion);
            Assert.True(VersionGate.Confirm(null, Blender(mine)));
            Assert.True(VersionGate.Confirm(null, Blender(null)));
            Assert.True(VersionGate.Confirm(null, null));
            Assert.Equal(0, asked);
        }

        [Fact]
        public void AbortStopsTheSendAndAsksAgainNextTime()
        {
            var answers = new List<VersionMatch.Mismatch>();
            VersionGate.Ask = (o, m) => { answers.Add(m); return false; };
            Assert.False(VersionGate.Confirm(null, Blender(Newer())));
            Assert.False(VersionGate.Confirm(null, Blender(Newer())));
            Assert.Equal(2, answers.Count);
            Assert.Equal("CADder Bridge", answers[0].Update);
        }

        [Fact]
        public void ContinueHoldsForThatPairUntilSolidWorksCloses()
        {
            int asked = 0;
            VersionGate.Ask = (o, m) => { asked++; return true; };
            Assert.True(VersionGate.Confirm(null, Blender(Newer())));
            Assert.True(VersionGate.Confirm(null, Blender(Newer())));
            Assert.Equal(1, asked);
            // another CADder is another question
            Assert.True(VersionGate.Confirm(null, Blender(Newer(), "CADder Pro")));
            Assert.Equal(2, asked);
        }

        [Fact]
        public void TheDialogDraws()
        {
            var m = VersionMatch.For("v1.1.1", "1.2.0", "CADder");
            using (var bmp = VersionMismatchDialog.Render(m))
            {
                Assert.True(bmp.Width > 200 && bmp.Height > 120,
                    "the dialog is " + bmp.Width + " x " + bmp.Height);
                // The text and the buttons are drawn: dark pixels in the
                // body, below the title bar.
                int dark = 0;
                for (int y = bmp.Height / 4; y < bmp.Height; y += 2)
                    for (int x = 0; x < bmp.Width; x += 2)
                        if (bmp.GetPixel(x, y).GetBrightness() < 0.35f) dark++;
                Assert.True(dark > 200, "the body has " + dark + " dark pixels");
                string shot = Environment.GetEnvironmentVariable("CADDER_DIALOG_SHOT");
                if (!string.IsNullOrEmpty(shot))
                    bmp.Save(shot, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
    }
}
