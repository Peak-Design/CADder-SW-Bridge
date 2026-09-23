using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Peak.Cadder.Appearance;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The split of a shared sub-assembly definition whose uses need
    /// different colors. A definition N is used at the top level and also
    /// inside a container P that is used twice. One occurrence entity inside
    /// P serves both uses of P, so N can only split after P has split.
    /// </summary>
    public class SharedDefinitionSplitTests : IDisposable
    {
        private readonly List<string> _tempFiles = new List<string>();

        public void Dispose()
        {
            foreach (var f in _tempFiles)
                try { File.Delete(f); } catch (IOException) { }
        }

        private static readonly Rgb Red = new Rgb(1, 0, 0);
        private static readonly Rgb Green = new Rgb(0, 1, 0);

        /// <summary>asm holds n-1 (N) at x=0, and p-1 and p-2 (P) at x=100
        /// and x=200. P holds N, and N holds one gray leaf part.</summary>
        private string WriteFixture(bool containerCanSplit, out Dictionary<string, int> nauo)
        {
            var f = new AppearanceStepFixture();
            int asm = f.Product("asm");
            int leaf = f.ColouredPart("leaf", 0.5, 0.5, 0.5);
            int n = f.Product("nested");
            int p = f.Product("holder");
            nauo = new Dictionary<string, int>
            {
                ["n-1"] = f.Use(asm, n, 0, 0, 0),
                ["p-1"] = f.Use(asm, p, 100, 0, 0),
                ["p-2"] = f.Use(asm, p, 200, 0, 0),
                ["n-in-p"] = f.Use(p, n, 0, 0, 0),
                ["leaf-in-n"] = f.Use(n, leaf, 0, 0, 0),
            };
            if (!containerCanSplit)
            {
                // A definition with no shape_definition_representation
                // cannot be copied.
                string path = f.Write(_tempFiles);
                string text = File.ReadAllText(path);
                int sr = f.ShapeRepresentationOf(p);
                var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None)
                    .Where(l => !(l.StartsWith("#") && l.Contains("SHAPE_DEFINITION_REPRESENTATION(")
                                  && l.Contains(",#" + sr + ")")));
                File.WriteAllText(path, string.Join("\r\n", lines));
                return path;
            }
            return f.Write(_tempFiles);
        }

        private static OccurrenceAppearance Node(string path, OccurrenceAppearance parent,
            Rgb? colour = null)
        {
            var n = new OccurrenceAppearance
            {
                Path = path,
                Parent = parent,
                Exported = true,
                OverridesPartInternals = colour.HasValue,
                Colour = colour ?? default(Rgb),
            };
            parent?.Children.Add(n);
            return n;
        }

        /// <summary>The matched pairs in the order the matcher makes them,
        /// with the uses of N first. N and P both have a use at the top
        /// level, so the order of their first uses cannot decide.</summary>
        private static List<KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>> Pairs(
            List<StepRewriter.OccurrenceRef> occs, Dictionary<string, int> nauo)
        {
            StepRewriter.OccurrenceRef Occ(string key) => occs.Single(o => o.NauoId == nauo[key]);

            var n1 = Node("n-1", null);
            var n1Leaf = Node("n-1/leaf-1", n1, Red);
            var p1 = Node("p-1", null);
            var p1N = Node("p-1/nested-1", p1);
            var p1Leaf = Node("p-1/nested-1/leaf-1", p1N, Red);
            var p2 = Node("p-2", null);
            var p2N = Node("p-2/nested-1", p2);
            var p2Leaf = Node("p-2/nested-1/leaf-1", p2N, Green);

            KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef> P(
                OccurrenceAppearance a, string key)
                => new KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>(a, Occ(key));

            return new List<KeyValuePair<OccurrenceAppearance, StepRewriter.OccurrenceRef>>
            {
                P(n1, "n-1"), P(n1Leaf, "leaf-in-n"),
                P(p1, "p-1"), P(p1N, "n-in-p"), P(p1Leaf, "leaf-in-n"),
                P(p2, "p-2"), P(p2N, "n-in-p"), P(p2Leaf, "leaf-in-n"),
            };
        }

        /// <summary>The color of the leaf at the end of a chain of uses, each
        /// picked by its x position inside the definition above it.</summary>
        private static string LeafColour(string path, params double[] xs)
        {
            var rw = new StepRewriter(path, null);
            rw.FindOccurrences();
            int pd = rw.RootPd;
            StepRewriter.OccurrenceRef use = null;
            foreach (var x in xs)
            {
                use = rw.ChildrenByParentPd[pd].Single(o => Math.Abs(o.Translation[0] - x) < 1e-6);
                pd = use.ChildPd;
            }
            var colours = AppearanceStepFixture.ColoursOn(rw.Document, use.TargetItems[0]);
            return Assert.Single(colours);
        }

        [Fact]
        public void TheContainerSplitsBeforeTheDefinitionNestedInIt()
        {
            string path = WriteFixture(true, out var nauo);
            var log = new List<string>();
            var rw = new StepRewriter(path, log.Add);
            var occs = rw.FindOccurrences();
            var pairs = Pairs(occs, nauo);

            rw.ApplyOccurrenceColours(pairs, deInstance: true);
            rw.Save(path);

            Assert.Equal("1,0,0", LeafColour(path, 0, 0));
            Assert.Equal("1,0,0", LeafColour(path, 100, 0, 0));
            Assert.Equal("0,1,0", LeafColour(path, 200, 0, 0));
        }

        [Fact]
        public void ASplitThatWouldRepointASharedEntityIsSkipped()
        {
            // P cannot be copied, so both uses of P keep one occurrence
            // entity for N. To split N for p-2 would repaint p-1 as well.
            string path = WriteFixture(false, out var nauo);
            var log = new List<string>();
            var rw = new StepRewriter(path, log.Add);
            var occs = rw.FindOccurrences();
            var pairs = Pairs(occs, nauo);

            rw.ApplyOccurrenceColours(pairs, deInstance: true);
            rw.Save(path);

            // A missing color is honest, a wrong one is not.
            Assert.NotEqual("0,1,0", LeafColour(path, 100, 0, 0));
            Assert.NotEqual("1,0,0", LeafColour(path, 200, 0, 0));
            Assert.Contains(log, l => l.Contains("CONFLICT"));
        }
    }
}
