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

        /// <summary>'asm' uses one part once. The part color is gray, with
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
        public void ACopyKeepsTheBodiesOfEveryRepresentation()
        {
            // A part with a surface body and a solid body carries them in
            // two representations, behind two relationships. The surface
            // one has the lower id. The part is used twice with two colors,
            // so one use gets a copy.
            var f = new AppearanceStepFixture();
            int asm = f.Product("asm");
            int part = f.Product("shell_and_solid");
            var surface = f.Bodies(part, "MANIFOLD_SURFACE_SHAPE_REPRESENTATION", "SHELL_BASED_SURFACE_MODEL");
            var solid = f.Bodies(part, "ADVANCED_BREP_SHAPE_REPRESENTATION", "MANIFOLD_SOLID_BREP");
            f.Style(surface[0], 0.5, 0.5, 0.5);
            f.Style(solid[0], 0.5, 0.5, 0.5);
            int n1 = f.Use(asm, part, 0, 0, 0);
            int n2 = f.Use(asm, part, 10, 0, 0);
            string path = f.Write(_tempFiles);

            var rw = new StepRewriter(path, null);
            var occs = rw.FindOccurrences();
            rw.ApplyOccurrenceColours(
                new List<KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>>
                {
                    Pair(Leaf("part-1", new Rgb(1, 0, 0)), occs.Single(o => o.NauoId == n1)),
                    Pair(Leaf("part-2", new Rgb(0, 1, 0)), occs.Single(o => o.NauoId == n2)),
                }, deInstance: true);
            rw.Save(path);

            var back = new StepRewriter(path, null);
            var leaves = back.FindOccurrences();
            var pd1 = leaves.Single(o => o.NauoId == n1);
            var pd2 = leaves.Single(o => o.NauoId == n2);
            Assert.NotEqual(pd1.ChildPd, pd2.ChildPd);
            foreach (var leaf in new[] { pd1, pd2 })
            {
                var types = leaf.TargetItems.Select(back.Document.TypeOf).OrderBy(t => t).ToList();
                Assert.Equal(new[] { "MANIFOLD_SOLID_BREP", "SHELL_BASED_SURFACE_MODEL" }, types);
            }
            Assert.Equal(new[] { "0,1,0" }, AppearanceStepFixture.ColoursOn(back.Document,
                pd2.TargetItems.Single(i => back.Document.TypeOf(i) == "MANIFOLD_SOLID_BREP")));
        }

        [Fact]
        public void AnUnmatchedUseKeepsTheSolidWorksColour()
        {
            // p-1 is matched and red. p-2 is not matched, so the log says it
            // keeps the SolidWorks color. It still uses the original part
            // in the file, so the red must go on a copy.
            var f = new AppearanceStepFixture();
            int asm = f.Product("asm");
            int part = f.ColouredPart("p", 0.5, 0.5, 0.5);
            int n1 = f.Use(asm, part, 0, 0, 0);
            int n2 = f.Use(asm, part, 10, 0, 0);
            string path = f.Write(_tempFiles);

            var rw = new StepRewriter(path, null);
            var occs = rw.FindOccurrences();
            rw.ApplyOccurrenceColours(
                new List<KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>>
                {
                    Pair(Leaf("p-1", new Rgb(1, 0, 0)), occs.Single(o => o.NauoId == n1)),
                    Pair(Leaf("p-2", null), null),
                }, deInstance: true);
            rw.Save(path);

            var back = new StepRewriter(path, null);
            var leaves = back.FindOccurrences();
            var p1 = leaves.Single(o => o.NauoId == n1);
            var p2 = leaves.Single(o => o.NauoId == n2);
            Assert.Equal(new[] { "1,0,0" }, AppearanceStepFixture.ColoursOn(back.Document, p1.TargetItems[0]));
            Assert.Equal(new[] { "0.5,0.5,0.5" }, AppearanceStepFixture.ColoursOn(back.Document, p2.TargetItems[0]));
        }

        /// <summary>No entity in the file may reference the id 0, which
        /// does not exist.</summary>
        private static void AssertNoReferenceToZero(Part21 step)
            => Assert.DoesNotContain(step.Entities.Keys, id => step.Refs(id).Contains(0));

        [Fact]
        public void APartWithNoSolidsGetsNoOccurrenceStyle()
        {
            // A skeleton or layout part: a product with no bodies. The
            // override cascades onto it from a sub-assembly.
            var f = new AppearanceStepFixture();
            int asm = f.Product("asm");
            int skeleton = f.Product("skeleton");
            int n1 = f.Use(asm, skeleton, 0, 0, 0);
            string path = f.Write(_tempFiles);

            var rw = new StepRewriter(path, null);
            var occs = rw.FindOccurrences();
            rw.ApplyOccurrenceColours(
                new List<KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>>
                {
                    Pair(Leaf("skeleton-1", new Rgb(1, 0, 0)), occs.Single(o => o.NauoId == n1)),
                }, deInstance: false);
            rw.Save(path);

            AssertNoReferenceToZero(new Part21(path));
        }

        [Fact]
        public void AFailedCopyLeavesNoOrphansAndNoStyleOnNothing()
        {
            // The same part used twice with two colors: the second group
            // needs a copy, and a part with no solids cannot give one.
            var f = new AppearanceStepFixture();
            int asm = f.Product("asm");
            int skeleton = f.Product("skeleton");
            int n1 = f.Use(asm, skeleton, 0, 0, 0);
            int n2 = f.Use(asm, skeleton, 10, 0, 0);
            string path = f.Write(_tempFiles);

            var rw = new StepRewriter(path, null);
            var occs = rw.FindOccurrences();
            rw.ApplyOccurrenceColours(
                new List<KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>>
                {
                    Pair(Leaf("skeleton-1", new Rgb(1, 0, 0)), occs.Single(o => o.NauoId == n1)),
                    Pair(Leaf("skeleton-2", new Rgb(0, 1, 0)), occs.Single(o => o.NauoId == n2)),
                }, deInstance: true);
            rw.Save(path);

            var back = new Part21(path);
            AssertNoReferenceToZero(back);
            Assert.Equal(2, back.ByType("PRODUCT").Count);
            Assert.Equal(2, back.ByType("PRODUCT_DEFINITION").Count);
        }

        [Fact]
        public void EveryBodyOfAPartGetsTheOccurrenceStyle()
        {
            // Two bodies. SolidWorks styled only the second one, so the
            // first solid with a styled item is not the first solid.
            var f = new AppearanceStepFixture();
            int asm = f.Product("asm");
            int part = f.Product("two_bodies");
            var solids = f.Bodies(part, "ADVANCED_BREP_SHAPE_REPRESENTATION",
                "MANIFOLD_SOLID_BREP", "MANIFOLD_SOLID_BREP");
            int styledSecond = f.Style(solids[1], 0.5, 0.5, 0.5);
            int n1 = f.Use(asm, part, 0, 0, 0);
            string path = f.Write(_tempFiles);

            var rw = new StepRewriter(path, null);
            var occs = rw.FindOccurrences();
            rw.ApplyOccurrenceColours(
                new List<KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>>
                {
                    Pair(Leaf("two_bodies-1", new Rgb(1, 0, 0)), occs.Single(o => o.NauoId == n1)),
                }, deInstance: false);
            rw.Save(path);

            var back = new Part21(path);
            AssertNoReferenceToZero(back);
            var plain = back.ByType("STYLED_ITEM")
                .Where(s => back.NameOf(s) == "occurrence colour").ToList();
            var over = back.ByType("CONTEXT_DEPENDENT_OVER_RIDING_STYLED_ITEM").ToList();

            // The first solid has no styled item to override: a plain one.
            int first = Assert.Single(plain);
            Assert.Equal(solids[0], back.Refs(first).Last());
            // The second solid overrides the styled item that styles it,
            // in the context of the occurrence.
            int second = Assert.Single(over);
            var refs = back.Refs(second);
            Assert.Equal(new[] { solids[1], styledSecond, n1 }, refs.Skip(1).ToArray());
        }

        [Fact]
        public void AnOverrideWithTheSameTransparencyIsWrittenInPlace()
        {
            // The chain already holds the transparency, so the color is
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
