using Peak.Cadder.Core;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A screw mate's lead, from the two ways SolidWorks states one.
    /// </summary>
    public class ScrewLeadTests
    {
        [Fact]
        public void DistancePerRevolutionIsAlreadyInMetres()
        {
            // Corpus 09, built as 2 mm per revolution and read as 0.002.
            // The document's unit must not touch this form.
            foreach (double unit in new[] { 1.0, 1e-3, 0.0254 })
            {
                var lead = ScrewLead.Metres(0.002, true, true, unit);
                Assert.Equal(0.002, lead.Value, 9);
            }
        }

        [Fact]
        public void TurnsPerUnitLengthCountsTheDocumentsOwnUnit()
        {
            // The live wrench (2026-09-16): one turn per unit length. In an
            // inch document that is 25.4 mm of travel per turn.
            Assert.Equal(0.0254, ScrewLead.Metres(1.0, false, true, 0.0254).Value, 9);
            // The same mate in a millimetre document.
            Assert.Equal(0.001, ScrewLead.Metres(1.0, false, true, 1e-3).Value, 9);
            // And a real thread: 20 turns per inch.
            Assert.Equal(0.00127, ScrewLead.Metres(20.0, false, true, 0.0254).Value, 9);
        }

        [Fact]
        public void TurnsPerMetreWasTheOldReadingAndWasOutByTheUnit()
        {
            // What the wrench shipped as before the unit was read: one
            // metre of travel per turn, so a thousandth of a turn drove the
            // whole jaw. Kept as a test because the number is the symptom
            // Oscar saw.
            Assert.Equal(1.0, ScrewLead.Metres(1.0, false, true, 1.0).Value, 9);
        }

        [Fact]
        public void ReverseIsTheHandOfTheThread()
        {
            Assert.Equal(0.002, ScrewLead.Metres(0.002, true, true, 1.0).Value, 9);
            Assert.Equal(-0.002, ScrewLead.Metres(0.002, true, false, 1.0).Value, 9);
        }

        [Fact]
        public void AMateThatStatesNothingUsableGivesNoLead()
        {
            Assert.Null(ScrewLead.Metres(0.0, false, true, 0.0254));
            Assert.Null(ScrewLead.Metres(0.0, true, true, 0.0254));
        }

        [Fact]
        public void AnUnreadableUnitFallsBackToTheMetre()
        {
            Assert.Equal(1.0, ScrewLead.Metres(1.0, false, true, 0.0).Value, 9);
        }
    }
}
