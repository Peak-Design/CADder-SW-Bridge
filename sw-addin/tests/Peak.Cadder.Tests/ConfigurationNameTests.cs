using System.Collections.Generic;
using Peak.Cadder.Core;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The names of the files of a send, from its configuration.
    ///
    /// Blender names the import after the same stem, so two configurations
    /// of one assembly stand side by side, and a send replaces only the
    /// import with its own stem. The rule is the contract between the two
    /// halves, so these pin it.
    /// </summary>
    public class ConfigurationNameTests
    {
        [Fact]
        public void TheDefaultConfigurationIsNamedLikeAnyOther()
        {
            // Every send uses the stem, also a send of the one configuration
            // a document has.
            Assert.Equal("lift_Default", ConfigurationNames.Stem("lift", "Default"));
        }

        [Fact]
        public void AnOrdinaryNameIsKeptAsItIs()
        {
            Assert.Equal("Extended 250mm", ConfigurationNames.SafeName("Extended 250mm"));
            Assert.Equal("Arm-Up (v2)", ConfigurationNames.SafeName("Arm-Up (v2)"));
            Assert.Equal("Größe", ConfigurationNames.SafeName("Größe"));
        }

        [Fact]
        public void EveryCharacterWindowsRefusesBecomesAnUnderscore()
        {
            Assert.Equal("a_b_c_d_e_f_g_h_i_j",
                ConfigurationNames.SafeName("a\\b/c:d*e?f\"g<h>i|j"));
        }

        [Fact]
        public void AControlCharacterBecomesAnUnderscore()
        {
            Assert.Equal("a_b_c", ConfigurationNames.SafeName("a\tb\u0001c"));
        }

        [Fact]
        public void TrailingDotsAndSpacesGo()
        {
            // Windows drops them from a file name, and the two halves must
            // agree on the name the file really has.
            Assert.Equal("Open", ConfigurationNames.SafeName("Open. . "));
            Assert.Equal(" Open", ConfigurationNames.SafeName(" Open"));
            Assert.Equal("v1.2", ConfigurationNames.SafeName("v1.2"));
        }

        [Fact]
        public void ANameWithNothingLeftIsOneUnderscore()
        {
            Assert.Equal("_", ConfigurationNames.SafeName(""));
            Assert.Equal("_", ConfigurationNames.SafeName(null));
            Assert.Equal("_", ConfigurationNames.SafeName(". ."));
            Assert.Equal("lift__", ConfigurationNames.Stem("lift", "..."));
        }

        [Fact]
        public void TheStemKeepsTheRefusedCharactersOutOfTheFileName()
        {
            Assert.Equal("lift_A_B", ConfigurationNames.Stem("lift", "A/B"));
        }

        [Fact]
        public void TheExactNameIsFoundFirst()
        {
            var names = new List<string> { "default", "Default" };
            Assert.Equal("Default", ConfigurationNames.Find(names, "Default"));
            Assert.Equal("default", ConfigurationNames.Find(names, "default"));
        }

        [Fact]
        public void ANameInAnotherCaseIsStillFound()
        {
            Assert.Equal("Default",
                ConfigurationNames.Find(new List<string> { "Open", "Default" }, "DEFAULT"));
        }

        [Fact]
        public void ANameTheDocumentDoesNotHaveIsNotFound()
        {
            Assert.Null(ConfigurationNames.Find(new List<string> { "Default" }, "Open"));
            Assert.Null(ConfigurationNames.Find(new List<string> { "Default" }, ""));
            Assert.Null(ConfigurationNames.Find(null, "Default"));
        }
    }

    /// <summary>
    /// What the configuration list of Send to Blender ticks when it opens.
    /// </summary>
    public class ConfigurationPickerTests
    {
        private static readonly List<string> Names =
            new List<string> { "Default", "Open", "Closed", "Folded" };

        [Fact]
        public void TheFirstTimeOnlyTheActiveConfigurationIsTicked()
        {
            Assert.Equal(new[] { "Open" },
                ConfigurationPickerDialog.DefaultTicks(Names, "Open", null));
        }

        [Fact]
        public void TheLastChoiceIsTickedAgainInTheOrderOfTheDocument()
        {
            var remembered = new List<string> { "Folded", "Default" };
            Assert.Equal(new[] { "Default", "Folded" },
                ConfigurationPickerDialog.DefaultTicks(Names, "Open", remembered));
        }

        [Fact]
        public void ARememberedNameThatIsGoneIsDropped()
        {
            var remembered = new List<string> { "Closed", "Renamed" };
            Assert.Equal(new[] { "Closed" },
                ConfigurationPickerDialog.DefaultTicks(Names, "Open", remembered));
        }

        [Fact]
        public void WhenNothingRememberedIsLeftTheActiveOneIsTicked()
        {
            var remembered = new List<string> { "Renamed", "Deleted" };
            Assert.Equal(new[] { "Open" },
                ConfigurationPickerDialog.DefaultTicks(Names, "Open", remembered));
        }

        [Fact]
        public void AnActiveNameTheListDoesNotHoldTicksNothing()
        {
            Assert.Empty(ConfigurationPickerDialog.DefaultTicks(Names, null, null));
            Assert.Empty(ConfigurationPickerDialog.DefaultTicks(Names, "Other", null));
        }

        [Fact]
        public void TheListGivesTheTicksInTheOrderOfTheDocument()
        {
            Assert.Equal(new[] { "Open", "Folded" }, RunOnStaThread(() =>
            {
                using (var list = new ConfigurationChecklist(
                    Names, "Open", new List<string> { "Folded", "Open" }))
                    return list.Ticked;
            }));
        }

        [Fact]
        public void SelectAllAndClearSayWhatChanged()
        {
            var seen = RunOnStaThread(() =>
            {
                var counts = new List<int>();
                using (var list = new ConfigurationChecklist(Names, "Open", null))
                {
                    list.TickedChanged += (s, e) => counts.Add(list.Ticked.Count);
                    list.TickAll(true);
                    list.TickAll(false);
                }
                return counts;
            });
            Assert.Equal(new[] { 4, 0 }, seen);
        }

        /// <summary>Windows Forms controls want a single threaded
        /// apartment, which the test runner does not give.</summary>
        private static T RunOnStaThread<T>(System.Func<T> work)
        {
            T result = default(T);
            System.Exception error = null;
            var thread = new System.Threading.Thread(() =>
            {
                try { result = work(); }
                catch (System.Exception ex) { error = ex; }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error != null) throw error;
            return result;
        }
    }
}
