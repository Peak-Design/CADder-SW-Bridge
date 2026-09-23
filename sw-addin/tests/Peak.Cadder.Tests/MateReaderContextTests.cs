using System;
using System.Collections.Generic;
using Peak.Cadder.Core.Model;
using Peak.Cadder.Sw;
using Xunit;
using MateContext = Peak.Cadder.Sw.MateReader.MateContext;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A mate is read either in the top document or in the document of one
    /// flexible subassembly. The two give component names and entity
    /// coordinates in different frames, so the reader must know which one
    /// it reads. It used to infer that from the component that reported
    /// the mate and from where the entities resolved, and a top-level
    /// flexible subassembly made both guesses wrong.
    /// </summary>
    public class MateReaderContextTests
    {
        private static WalkedComponent Walked(
            string id, string path, string solving = null, WalkedComponent parent = null)
        {
            var w = new WalkedComponent { Id = id, Parent = parent };
            w.Graph.Id = id;
            w.Graph.Path = path;
            w.Graph.Solving = solving;
            if (parent != null) parent.Children.Add(w);
            return w;
        }

        private static Dictionary<string, WalkedComponent> ByPath(params WalkedComponent[] all)
        {
            var d = new Dictionary<string, WalkedComponent>(StringComparer.OrdinalIgnoreCase);
            foreach (var w in all) d[w.Graph.Path] = w;
            return d;
        }

        [Fact]
        public void TopLevelNameIsNotJoinedOntoTheFlexibleSubThatReportedIt()
        {
            // Pin-1 at top level and Pin-1 inside flexible Arm-1: instance
            // numbers restart in each document. Arm-1 reports a top-level
            // mate between the top Pin-1 and Arm-1's own plane.
            var arm = Walked("c001", "Arm-1", "flexible");
            var inner = Walked("c002", "Arm-1/Pin-1", null, arm);
            var pin = Walked("c003", "Pin-1");
            var byPath = ByPath(arm, inner, pin);

            Assert.Same(pin, MateContext.ForTop(arm).Match("Pin-1", byPath));
            // A top-context name is the full instance path.
            Assert.Same(inner, MateContext.ForTop(pin).Match("Arm-1/Pin-1", byPath));
        }

        [Fact]
        public void SubDocumentNameResolvesOnlyInsideItsSub()
        {
            var arm = Walked("c001", "Arm-1", "flexible");
            var inner = Walked("c002", "Arm-1/Pin-1", null, arm);
            var bolt = Walked("c003", "Bolt-1");
            var byPath = ByPath(arm, inner, bolt);

            var ctx = MateContext.ForSub(arm);
            Assert.Same(inner, ctx.Match("Pin-1", byPath));
            // Bolt-1 is not one of Arm-1's walked children. The top-level
            // Bolt-1 is a different component in a different document.
            Assert.Null(ctx.Match("Bolt-1", byPath));
        }

        [Fact]
        public void NestedSubDocumentNameDoesNotLandInItsParentSub()
        {
            var a = Walked("c001", "A-1", "flexible");
            var b = Walked("c002", "A-1/B-1", "flexible", a);
            var aPin = Walked("c003", "A-1/Pin-1", null, a);
            var byPath = ByPath(a, b, aPin);

            // B's document names its own Pin-1, which is not walked here:
            // A-1/Pin-1 belongs to A's document.
            Assert.Null(MateContext.ForSub(b).Match("Pin-1", byPath));

            var bPin = Walked("c004", "A-1/B-1/Pin-1", null, b);
            byPath = ByPath(a, b, aPin, bPin);
            Assert.Same(bPin, MateContext.ForSub(b).Match("Pin-1", byPath));
        }

        [Fact]
        public void TopLevelMateKeepsTheTopFrameWhenAFlexibleSubReportsIt()
        {
            // Link-1 inside flexible Arm-1, mated to the top assembly's own
            // Top Plane. Its EntityParams are already in top coordinates, and
            // the plane entity is ground, not Arm-1.
            var arm = Walked("c001", "Arm-1", "flexible");
            Walked("c002", "Arm-1/Link-1", null, arm);

            Assert.Null(MateContext.ForTop(arm).Residence);
            Assert.Same(arm, MateContext.ForSub(arm).Residence);
        }

        [Fact]
        public void DedupeKeySeparatesTheTopDocumentFromASubDocument()
        {
            var arm = Walked("c001", "Arm-1", "flexible");
            var pin = Walked("c009", "Pin-1");
            var gm = new GraphMate { FeatureName = "Coincident1", TypeValue = 0 };
            gm.Entities.Add(new GraphMateEntity { ComponentId = "c002" });
            gm.Entities.Add(new GraphMateEntity { ComponentId = "c003" });

            // One top-level mate reported by two of its components is one mate.
            Assert.Equal(MateReader.DedupeKey(gm, MateContext.ForTop(arm)),
                MateReader.DedupeKey(gm, MateContext.ForTop(pin)));
            // A mate of the same name in Arm-1's own document is another mate.
            Assert.NotEqual(MateReader.DedupeKey(gm, MateContext.ForTop(pin)),
                MateReader.DedupeKey(gm, MateContext.ForSub(arm)));
        }
    }
}
