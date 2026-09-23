using Peak.Cadder.Appearance;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The color of an override in the STEP repair is the color that
    /// SolidWorks draws, the same one the direct send uses
    /// (AppearanceSpec.DisplayColourRef). Live usb_flash_drive2
    /// (2026-09-15): polished gold holds primary (255,206,127), which is the
    /// highlight tint of the metal, and secondary (247,224,153), which
    /// SolidWorks draws.
    /// </summary>
    public class AppearanceLadderColourTests
    {
        private static int Ref(int r, int g, int b) => b << 16 | g << 8 | r;

        [Fact]
        public void AMetalOverrideTakesTheColourSolidWorksDraws()
        {
            int primary = Ref(255, 206, 127), secondary = Ref(247, 224, 153);
            var c = AppearanceLadder.DisplayedColour(2, primary, secondary, secondary);
            Assert.True(c.ApproxEquals(new Rgb(247 / 255.0, 224 / 255.0, 153 / 255.0)), c.ToString());

            c = AppearanceLadder.DisplayedColour(1, primary, secondary, 0);
            Assert.True(c.ApproxEquals(new Rgb(247 / 255.0, 224 / 255.0, 153 / 255.0)), c.ToString());
        }

        [Fact]
        public void AThreeColourAppearanceTakesItsThirdAndAPlainOneItsFirst()
        {
            int p = Ref(10, 20, 30), s = Ref(40, 50, 60), t = Ref(70, 80, 90);
            Assert.True(AppearanceLadder.DisplayedColour(3, p, s, t)
                .ApproxEquals(new Rgb(70 / 255.0, 80 / 255.0, 90 / 255.0)));
            Assert.True(AppearanceLadder.DisplayedColour(0, p, s, t)
                .ApproxEquals(new Rgb(10 / 255.0, 20 / 255.0, 30 / 255.0)));
        }
    }
}
