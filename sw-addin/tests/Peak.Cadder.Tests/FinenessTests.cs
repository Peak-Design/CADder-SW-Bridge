using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Peak.Cadder;
using Peak.Cadder.Sw;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The quality names must cut a part the way the STEP import cuts it,
    /// so Draft over the bridge and Draft from a STEP file give the same
    /// mesh. The chord is a length and the angle keeps small holes round.
    /// </summary>
    public class FinenessTests
    {
        [Theory]
        [InlineData("DRAFT", 0.002, 0.6)]
        [InlineData("BALANCED", 0.0008, 0.5)]
        [InlineData("FINE", 0.0002, 0.25)]
        [InlineData("ULTRA", 0.00005, 0.1)]
        public void EachNameCutsLikeTheStepPresetOfThatName(
            string preset, double chord, double angle)
        {
            var f = BodyTessellator.FinenessFor(SendToBlenderCommand.QualityDial(preset));
            Assert.Equal(chord, f.Chord, 9);
            Assert.Equal(angle, f.Angle, 9);
        }

        [Fact]
        public void TheCoarseEndGoesPastDraft()
        {
            var coarsest = BodyTessellator.FinenessFor(0.0);
            var draft = BodyTessellator.FinenessFor(0.15);
            Assert.True(coarsest.Chord > draft.Chord);
            Assert.True(coarsest.Angle > draft.Angle);
        }

        [Fact]
        public void AFinerDialNeverCutsCoarser()
        {
            var last = BodyTessellator.FinenessFor(0.0);
            for (int i = 1; i <= 100; i++)
            {
                var f = BodyTessellator.FinenessFor(i / 100.0);
                Assert.True(f.Chord <= last.Chord, "chord at " + i);
                Assert.True(f.Angle <= last.Angle, "angle at " + i);
                last = f;
            }
        }

        [Theory]
        [InlineData(-1.0, 0.0)]
        [InlineData(2.0, 1.0)]
        public void ADialOutsideTheRangeIsHeldToIt(double asked, double held)
        {
            Assert.Equal(BodyTessellator.FinenessFor(held).Chord,
                         BodyTessellator.FinenessFor(asked).Chord, 12);
        }

        /// <summary>The same numbers as the STEP import's own table, read
        /// from CADder when it sits beside this repository.</summary>
        [Fact]
        public void TheTableMatchesTheStepImportsTable()
        {
            string path = Path.Combine("C:" + Path.DirectorySeparatorChar,
                                       "PeakDesign", "CADder", "import_ui.py");
            if (!File.Exists(path)) return;
            string source = File.ReadAllText(path);
            foreach (var preset in new[] { "DRAFT", "BALANCED", "FINE", "ULTRA" })
            {
                var m = Regex.Match(source,
                    "\"" + preset + @""":\s*\(([0-9.eE-]+),\s*([0-9.eE-]+)\)");
                Assert.True(m.Success, preset + " is not in QUALITY_PRESETS");
                var f = BodyTessellator.FinenessFor(SendToBlenderCommand.QualityDial(preset));
                Assert.Equal(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                             f.Chord, 9);
                Assert.Equal(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                             f.Angle, 9);
            }
        }
    }
}
