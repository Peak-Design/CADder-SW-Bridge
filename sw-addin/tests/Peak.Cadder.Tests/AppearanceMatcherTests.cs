using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Peak.Cadder.Appearance;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The match of SolidWorks components to STEP occurrences by name and
    /// position. SolidWorks names a product after the file, with the
    /// configuration name added for a non-default configuration. A product
    /// whose name only starts with the document name is the loosest match,
    /// and it must not win over an exact one.
    /// </summary>
    public class AppearanceMatcherTests : IDisposable
    {
        private readonly List<string> _tempFiles = new List<string>();

        public void Dispose()
        {
            foreach (var f in _tempFiles)
                try { File.Delete(f); } catch (IOException) { }
        }

        private static OccurrenceAppearance Component(string path, string doc,
            double x, Rgb? colour = null, string config = null)
            => new OccurrenceAppearance
            {
                Path = path,
                ComponentName = path,
                DocName = doc,
                ReferencedConfiguration = config,
                Exported = true,
                OverridesPartInternals = colour.HasValue,
                Colour = colour ?? default(Rgb),
                Transform = new[] { 1.0, 0, 0, 0, 1, 0, 0, 0, 1, x, 0, 0, 1, 0, 0, 0 },
            };

        [Fact]
        public void AnExactNameWinsOverAPrefixAtTheSamePosition()
        {
            // Two parts modelled in place at the assembly origin. The
            // product of base_plate is written first.
            var f = new AppearanceStepFixture();
            int asm = f.Product("asm");
            int plate = f.ColouredPart("base_plate", 0.5, 0.5, 0.5);
            int base_ = f.ColouredPart("base", 0.5, 0.5, 0.5);
            int plateUse = f.Use(asm, plate, 0, 0, 0);
            int baseUse = f.Use(asm, base_, 0, 0, 0);
            string path = f.Write(_tempFiles);

            var rw = new StepRewriter(path, null);
            rw.FindOccurrences();
            var components = new List<OccurrenceAppearance>
            {
                Component("base-1", "base", 0, new Rgb(1, 0, 0)),
                Component("base_plate-1", "base_plate", 0),
            };
            var log = new List<string>();
            var pairs = new AppearanceMatcher(log.Add).Match(components, rw);

            Assert.Equal(baseUse, pairs.Single(p => p.Key.Path == "base-1").Value?.NauoId);
            Assert.Equal(plateUse, pairs.Single(p => p.Key.Path == "base_plate-1").Value?.NauoId);
            Assert.DoesNotContain(log, l => l.Contains("UNMATCHED"));
        }

        [Fact]
        public void TheConfigurationNameWinsOverAnotherPrefix()
        {
            // bracket in its 'long' configuration is written as
            // 'bracket_long'. 'bracket_assy' sits at the same position and
            // is written first.
            var f = new AppearanceStepFixture();
            int asm = f.Product("asm");
            int other = f.ColouredPart("bracket_assy", 0.5, 0.5, 0.5);
            int longCfg = f.ColouredPart("bracket_long", 0.5, 0.5, 0.5);
            f.Use(asm, other, 0, 0, 0);
            int longUse = f.Use(asm, longCfg, 0, 0, 0);
            string path = f.Write(_tempFiles);

            var rw = new StepRewriter(path, null);
            rw.FindOccurrences();
            var components = new List<OccurrenceAppearance>
            {
                Component("bracket-1", "bracket", 0, new Rgb(1, 0, 0), "long"),
            };
            var pairs = new AppearanceMatcher(null).Match(components, rw);

            Assert.Equal(longUse, pairs.Single().Value?.NauoId);
        }

        [Fact]
        public void APrefixStillMatchesWhenNothingStricterIsThere()
        {
            // A configuration product whose name the component does not
            // give: the generic prefix is the only candidate.
            var f = new AppearanceStepFixture();
            int asm = f.Product("asm");
            int cfg = f.ColouredPart("bracket_short", 0.5, 0.5, 0.5);
            int use = f.Use(asm, cfg, 0, 0, 0);
            string path = f.Write(_tempFiles);

            var rw = new StepRewriter(path, null);
            rw.FindOccurrences();
            var pairs = new AppearanceMatcher(null).Match(
                new List<OccurrenceAppearance> { Component("bracket-1", "bracket", 0, new Rgb(1, 0, 0)) }, rw);

            Assert.Equal(use, pairs.Single().Value?.NauoId);
        }
    }
}
