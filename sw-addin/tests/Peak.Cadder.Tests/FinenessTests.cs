using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Peak.Cadder;
using Peak.Cadder.Core;
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
                                       "PeakDesign", "CADder", "quality.py");
            if (!File.Exists(path)) return;
            string source = File.ReadAllText(path);
            foreach (var preset in new[] { "DRAFT", "BALANCED", "FINE", "ULTRA" })
            {
                var m = Regex.Match(source,
                    "\"" + preset + @""":\s*\(([0-9.eE-]+),\s*([0-9.eE-]+)\)");
                Assert.True(m.Success, preset + " is not in PRESETS");
                var f = BodyTessellator.FinenessFor(SendToBlenderCommand.QualityDial(preset));
                Assert.Equal(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                             f.Chord, 9);
                Assert.Equal(double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                             f.Angle, 9);
            }
        }

        [Fact]
        public void CustomIsTheDistanceAndAngleAsSet()
        {
            var f = SendToBlenderCommand.FinenessOf(new AppSettings
            {
                QualityPreset = "CUSTOM", QualityDistance = 0.003, QualityAngle = 0.7,
            });
            Assert.Equal(0.003, f.Chord, 12);
            Assert.Equal(0.7, f.Angle, 12);
            Assert.False(f.Relative);
        }

        [Fact]
        public void ANameIgnoresTheCustomValues()
        {
            var f = SendToBlenderCommand.FinenessOf(new AppSettings
            {
                QualityPreset = "FINE", QualityDistance = 0.003, QualityAngle = 0.7,
            });
            Assert.Equal(0.0002, f.Chord, 12);
            Assert.Equal(0.25, f.Angle, 12);
        }

        [Fact]
        public void RelativeReplacesTheName()
        {
            var f = SendToBlenderCommand.FinenessOf(new AppSettings
            {
                QualityPreset = "DRAFT", QualityRelative = true,
                QualityRelativeDistance = 0.01, QualityAngle = 0.4,
            });
            Assert.True(f.Relative);
            Assert.Equal(0.01, f.RelativeDistance, 12);
            Assert.Equal(0.4, f.Angle, 12);
        }

        [Fact]
        public void ARequestFromBlenderCarriesTheNumbers()
        {
            var settings = new AppSettings { QualityPreset = "ULTRA" };
            var custom = Bridge.SwCommandHandler.FinenessFrom(
                new Dictionary<string, object>
                {
                    { "quality", 0.45 }, { "chord_m", 0.004 }, { "angle_rad", 0.3 },
                }, settings);
            Assert.Equal(0.004, custom.Chord, 12);
            Assert.Equal(0.3, custom.Angle, 12);

            var relative = Bridge.SwCommandHandler.FinenessFrom(
                new Dictionary<string, object>
                {
                    { "quality", 0.45 }, { "relative", true },
                    { "relative_distance", 0.02 }, { "angle_rad", 0.5 },
                }, settings);
            Assert.True(relative.Relative);
            Assert.Equal(0.02, relative.RelativeDistance, 12);
        }

        [Fact]
        public void ARequestFromCadder100CarriesOnlyTheDial()
        {
            var settings = new AppSettings { QualityPreset = "ULTRA" };
            var f = Bridge.SwCommandHandler.FinenessFrom(
                new Dictionary<string, object> { { "quality", 0.15 } }, settings);
            Assert.Equal(0.002, f.Chord, 12);
            // And a request with nothing takes the Export Options.
            var g = Bridge.SwCommandHandler.FinenessFrom(
                new Dictionary<string, object>(), settings);
            Assert.Equal(0.00005, g.Chord, 12);
        }

        [Fact]
        public void TheSettingsSurviveASaveAndALoad()
        {
            string path = Path.GetTempFileName();
            try
            {
                new AppSettings
                {
                    QualityPreset = "CUSTOM", QualityDistance = 0.0031,
                    QualityAngle = 0.61, QualityRelative = true,
                    QualityRelativeDistance = 0.0123,
                }.Save(null, path);
                var back = AppSettings.Load(null, path);
                Assert.Equal("CUSTOM", back.QualityPreset);
                Assert.Equal(0.0031, back.QualityDistance, 12);
                Assert.Equal(0.61, back.QualityAngle, 12);
                Assert.True(back.QualityRelative);
                Assert.Equal(0.0123, back.QualityRelativeDistance, 12);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void ASendTellsTheStepRouteTheSameSettings()
        {
            var payload = SendToBlenderCommand.BuildPayload(new AppSettings
            {
                QualityPreset = "CUSTOM", QualityDistance = 0.0031,
                QualityAngle = 0.61, QualityRelative = true,
                QualityRelativeDistance = 0.0123,
            }, "part.step", null, null);
            var o = (Dictionary<string, object>)payload["import_options"];
            Assert.Equal("CUSTOM", o["quality_preset"]);
            Assert.Equal(0.0031, (double)o["lin_deflection_len"], 12);
            Assert.Equal(0.61, (double)o["ang_deflection_rot"], 12);
            Assert.Equal(true, o["tessellation_relative"]);
            Assert.Equal(0.0123, (double)o["lin_deflection_rel"], 12);
        }
    }
}
