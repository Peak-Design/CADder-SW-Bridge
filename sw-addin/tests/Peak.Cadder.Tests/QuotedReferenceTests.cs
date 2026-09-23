using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Peak.Cadder.Appearance;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A '#' and digits inside a quoted string is text, not a reference.
    /// SolidWorks writes the part file name into the product and the
    /// representation names, and a name such as 'Screw #6' or '#10-32 SHCS'
    /// is common. Its ids are dense, so such a name always hits a real
    /// entity.
    /// </summary>
    public class QuotedReferenceTests : IDisposable
    {
        private readonly List<string> _tempFiles = new List<string>();

        public void Dispose()
        {
            foreach (var f in _tempFiles)
                try { File.Delete(f); } catch (IOException) { }
        }

        [Fact]
        public void ReferencesInsideAQuotedStringAreText()
        {
            string path = Path.GetTempFileName();
            _tempFiles.Add(path);
            File.WriteAllText(path, "ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\n"
                + "#1=PRODUCT_CONTEXT('',#2,'mechanical');\n"
                + "#2=APPLICATION_CONTEXT('automotive design');\n"
                + "#5=PRODUCT('Screw #1','#2-32 SHCS, it''s #1','',(#1));\n"
                + "ENDSEC;\nEND-ISO-10303-21;\n");
            var step = new Part21(path);

            Assert.Equal(new[] { 1 }, step.Refs(5));
            Assert.Equal("'Screw #1','#2-32 SHCS, it''s #1','',(#9)",
                Part21.ReplaceRefs("'Screw #1','#2-32 SHCS, it''s #1','',(#1)",
                    id => id == 1 ? "#9" : null));
        }

        [Fact]
        public void ACopyOfAPartKeepsItsNameAndCopiesNothingElse()
        {
            // The part's name points at the shape representation of the
            // assembly. The part is used twice with two colors, so one use
            // gets a copy of the part.
            var f = new AppearanceStepFixture();
            int asm = f.Product("asm");
            string name = "Screw #" + f.ShapeRepresentationOf(asm);
            int part = f.ColouredPart(name, 0.5, 0.5, 0.5);
            int n1 = f.Use(asm, part, 0, 0, 0);
            int n2 = f.Use(asm, part, 10, 0, 0);
            string path = f.Write(_tempFiles);

            var rw = new StepRewriter(path, null);
            var occs = rw.FindOccurrences();
            var pairs = new List<KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>>
            {
                Pair("screw-1", new Rgb(1, 0, 0), occs.Single(o => o.NauoId == n1)),
                Pair("screw-2", new Rgb(0, 1, 0), occs.Single(o => o.NauoId == n2)),
            };
            rw.ApplyOccurrenceColours(pairs, deInstance: true);
            int withMaterial = new MaterialWriter(rw.Document, null).Apply(
                new[] { new PartMaterial { ProductName = name, Name = "Steel", Density = 7850 } },
                rw.BucketIndexByProduct);
            rw.Save(path);

            Assert.Equal(2, withMaterial);

            var back = new Part21(path);
            var products = back.ByType("PRODUCT").Select(back.NameOf).OrderBy(s => s).ToList();
            Assert.Equal(new[] { name, name, "asm" }.OrderBy(s => s), products);
            var reps = back.ByType("SHAPE_REPRESENTATION").Select(back.NameOf).ToList();
            Assert.Single(reps, r => r == "asm");
            Assert.Equal(2, reps.Count(r => r == name));
        }

        private static KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef> Pair(
            string path, Rgb colour, StepRewriter.OccurrenceRef occ)
            => new KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>(
                new OccurrenceAppearance
                {
                    Path = path,
                    Exported = true,
                    OverridesPartInternals = true,
                    Colour = colour,
                }, occ);
    }
}
