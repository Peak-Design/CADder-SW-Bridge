using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// Where setup registers the add-in for SolidWorks. It wrote a key for
    /// each SolidWorks year installed at that time, and nothing that a year
    /// installed later reads. The add-in was then missing from Tools >
    /// Add-Ins in the new year until the user ran setup again. SolidWorks
    /// also reads the key that no year owns, and that key covers every
    /// year, also a later one.
    /// </summary>
    public class RegistrationKeyTests
    {
        private const string Guid = "{5a19bed7-5bae-4520-a820-99c7466c42ac}";

        private static readonly string[] Installed =
        {
            "SOLIDWORKS 2024", "SolidWorks 2026", "SOLIDWORKS CAM", "AddIns", "Applications",
        };

        [Fact]
        public void EveryYearInstalledNowIsRegistered()
        {
            var keys = AddIn.RegistrationKeys(Installed, Guid);
            Assert.Contains(@"SOFTWARE\SolidWorks\SOLIDWORKS 2024\Addins\" + Guid, keys);
            Assert.Contains(@"SOFTWARE\SolidWorks\SolidWorks 2026\Addins\" + Guid, keys);
        }

        [Fact]
        public void AYearInstalledLaterFindsTheAddIn()
        {
            var keys = AddIn.RegistrationKeys(Installed, Guid);
            Assert.Contains(@"SOFTWARE\SolidWorks\AddIns\" + Guid, keys);
        }

        [Fact]
        public void TheKeyThatNoYearOwnsIsWrittenWithNoYearInstalled()
        {
            Assert.Equal(new[] { @"SOFTWARE\SolidWorks\AddIns\" + Guid },
                AddIn.RegistrationKeys(new string[0], Guid));
        }

        [Fact]
        public void NothingGoesUnderSolidWorksCam()
        {
            foreach (var key in AddIn.RegistrationKeys(Installed, Guid))
                Assert.DoesNotContain("CAM", key);
        }
    }
}
