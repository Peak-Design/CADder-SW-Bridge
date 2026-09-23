using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Peak.Cadder.Appearance;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The appearance repair on the leaves, in de-instance mode and in the
    /// mode that keeps the instancing. Each test builds a small file, applies
    /// the overrides through the real StepRewriter, saves, and reads the
    /// result back the way a consumer does.
    /// </summary>
    public class DeInstanceTests : IDisposable
    {
        private readonly List<string> _tempFiles = new List<string>();

        public void Dispose()
        {
            foreach (var f in _tempFiles)
                try { File.Delete(f); } catch (IOException) { }
        }

        private static OccurrenceAppearance Leaf(string path, Rgb? colour, double transparency = 0)
            => new OccurrenceAppearance
            {
                Path = path,
                Exported = true,
                OverridesPartInternals = colour.HasValue,
                Colour = colour ?? default(Rgb),
                Transparency = transparency,
            };

        private static KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef> Pair(
            OccurrenceAppearance a, StepRewriter.OccurrenceRef o)
            => new KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>(a, o);

        /// <summary>The transparency of every plain styled item on an
        /// entity. A chain with no SURFACE_STYLE_TRANSPARENT reads as 0.</summary>
        private static List<double> TransparenciesOn(Part21 step, int item)
            => AppearanceStepFixture.StyledItemsOn(step, item)
                .Select(s => AppearanceStepFixture.StyleEntitiesUnder(step, s, "SURFACE_STYLE_TRANSPARENT")
                    .Select(t => AppearanceStepFixture.Numbers(step, t)[0])
                    .DefaultIfEmpty(0.0).Max())
                .ToList();

        /// <summary>'asm' uses one part once. The part colour is grey, with
        /// the given transparency, on the solid and on its B-rep.</summary>
        private string SingleUse(double partTransparency, out int nauo)
        {
            var f = new AppearanceStepFixture();
            int asm = f.Product("asm");
            int part = f.ColouredPart("glass", 0.5, 0.5, 0.5, partTransparency);
            nauo = f.Use(asm, part, 0, 0, 0);
            return f.Write(_tempFiles);
        }

        /// <summary>Applies one override to the single use, saves, and
        /// returns the file read back with the part's solid and
        /// B-rep.</summary>
        private (Part21 Step, int Solid, int Brep) ApplySingle(string path, int nauo,
            Rgb colour, double transparency)
        {
            var rw = new StepRewriter(path, null);
            var occ = rw.FindOccurrences().Single(o => o.NauoId == nauo);
            rw.ApplyOccurrenceColours(
                new List<KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>>
                {
                    Pair(Leaf("glass-1", colour, transparency), occ),
                }, deInstance: true);
            rw.Save(path);

            var back = new StepRewriter(path, null);
            var leaf = back.FindOccurrences().Single(o => o.NauoId == nauo);
            int solid = leaf.TargetItems.Single();
            int brep = back.Document.ByType("ADVANCED_BREP_SHAPE_REPRESENTATION").Single();
            return (back.Document, solid, brep);
        }

        [Fact]
        public void ATransparentOverrideOnTheSharedGeometryStaysTransparent()
        {
            string path = SingleUse(0, out int nauo);
            var r = ApplySingle(path, nauo, new Rgb(0.2, 0.4, 0.9), 0.6);

            foreach (int item in new[] { r.Solid, r.Brep })
            {
                Assert.Equal(new[] { "0.2,0.4,0.9" }, AppearanceStepFixture.ColoursOn(r.Step, item));
                var t = TransparenciesOn(r.Step, item);
                Assert.NotEmpty(t);
                Assert.All(t, v => Assert.Equal(0.6, v, 9));
            }
        }

        [Fact]
        public void AnOpaqueOverrideOnTransparentGeometryComesOutOpaque()
        {
            string path = SingleUse(0.5, out int nauo);
            var r = ApplySingle(path, nauo, new Rgb(1, 0, 0), 0);

            foreach (int item in new[] { r.Solid, r.Brep })
            {
                Assert.Equal(new[] { "1,0,0" }, AppearanceStepFixture.ColoursOn(r.Step, item));
                var t = TransparenciesOn(r.Step, item);
                Assert.NotEmpty(t);
                Assert.All(t, v => Assert.Equal(0.0, v, 9));
            }
        }

        [Fact]
        public void AnOverrideWithTheSameTransparencyIsWrittenInPlace()
        {
            // The chain already holds the transparency, so the colour is
            // written again in place and no new chain is added.
            string path = SingleUse(0.5, out int nauo);
            int before = new Part21(path).ByType("COLOUR_RGB").Count;
            var r = ApplySingle(path, nauo, new Rgb(1, 0, 0), 0.5);

            Assert.Equal(before, r.Step.ByType("COLOUR_RGB").Count);
            Assert.Equal(new[] { "1,0,0" }, AppearanceStepFixture.ColoursOn(r.Step, r.Solid));
            Assert.All(TransparenciesOn(r.Step, r.Solid), v => Assert.Equal(0.5, v, 9));
        }
    }
}
