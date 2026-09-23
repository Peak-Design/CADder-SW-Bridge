using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Peak.Cadder.Appearance;
using SwPart21 = Peak.Cadder.Sw.Part21;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The flexible-twin fix in a file whose assembly placements are not in
    /// millimeters. SolidWorks writes the assembly placements in the unit of
    /// the top document, and the 2022 sample landing_gear.sldasm has a meter
    /// assembly. The occurrence tables hold millimeters, but a new placement
    /// must go into the file in the unit of the representation that lists
    /// it, and that representation must list it, or a reader of the file
    /// cannot find its unit.
    /// </summary>
    public class FlexiblePoseUnitsTests : IDisposable
    {
        private readonly List<string> _tempFiles = new List<string>();

        public void Dispose()
        {
            foreach (var f in _tempFiles)
                try { File.Delete(f); } catch (IOException) { }
        }

        /// <summary>'asm' uses 'sub' at the origin and at subX, and 'sub'
        /// holds 'leaf' at leafX, both in the unit of the assembly context.
        /// The leaf part has its own millimeter context.</summary>
        private string WriteFixture(string unit, double subX, double leafX)
        {
            var f = new AppearanceStepFixture();
            int ctx = f.LengthContext(unit);
            int asm = f.Product("asm", ctx);
            int sub = f.Product("sub", ctx);
            int leaf = f.Product("leaf", f.LengthContext("mm"));
            f.Use(asm, sub, 0, 0, 0);
            f.Use(asm, sub, subX, 0, 0);
            f.Use(sub, leaf, leafX, 0, 0);
            return f.Write(_tempFiles);
        }

        private static double[,] Local(double xM)
            => new[,]
            {
                { 1.0, 0.0, 0.0, xM },
                { 0.0, 1.0, 0.0, 0.0 },
                { 0.0, 0.0, 1.0, 0.0 },
                { 0.0, 0.0, 0.0, 1.0 },
            };

        private static FlexInstanceLayout Instance(string path, double relXm, double leafXm)
            => new FlexInstanceLayout
            {
                Path = path,
                SubDocName = "sub",
                ParentRelTranslationM = new[] { relXm, 0.0, 0.0 },
                Children = new List<FlexChildPose>
                {
                    new FlexChildPose { Key = "leaf-1", ProductName = "leaf", LocalM = Local(leafXm) },
                },
            };

        private static FlexFixOutcome Run(StepRewriter rw, params FlexInstanceLayout[] instances)
        {
            var occs = rw.FindOccurrences();
            var request = new FlexFixRequest();
            request.Instances.AddRange(instances);
            return FlexiblePoseFixer.Fix(rw.Document, occs, rw.ChildrenByParentPd,
                new List<FlexFixRequest> { request }, null);
        }

        /// <summary>The leaf use under the sub use at subXmm, read back from
        /// the saved file: its position in millimeters, the raw coordinate
        /// in the file, and the unit that both parsers find for it.</summary>
        private static (double Mm, double Raw, double Unit, double SwUnit) LeafUnder(
            string path, double subXmm)
        {
            var rw = new StepRewriter(path, null);
            var occs = rw.FindOccurrences();
            var subUse = occs.Single(o => o.ProductName == "sub"
                && Math.Abs(o.Translation[0] - subXmm) < 1e-6);
            var leaf = Assert.Single(rw.ChildrenByParentPd[subUse.ChildPd]);
            var chain = AppearanceStepFixture.PlacementOf(rw.Document, leaf.NauoId);
            double raw = AppearanceStepFixture.Numbers(rw.Document, chain.Point)[0];
            var sw = new SwPart21(path);
            return (leaf.Translation[0], raw,
                rw.Document.PlacementUnitMm(chain.Rel, chain.Placement),
                sw.PlacementUnitMm(chain.Rel, chain.Placement));
        }

        [Fact]
        public void AMetreFileKeepsTheMatchingUseAndWritesTheCloneInMetres()
        {
            // The file holds the leaf at 10 mm, which is what sub-1 shows.
            // Only sub-2 needs its own definition, with the leaf at 20 mm.
            string path = WriteFixture("m", 0.1, 0.010);
            var rw = new StepRewriter(path, null);
            var outcome = Run(rw,
                Instance("asm/sub-1", 0.0, 0.010),
                Instance("asm/sub-2", 0.1, 0.020));

            Assert.Empty(outcome.FailedPaths);
            Assert.Equal(1, outcome.DefinitionsCloned);
            Assert.Equal(1, outcome.PlacementsRetargeted);
            rw.Save(path);

            var leaf1 = LeafUnder(path, 0.0);
            Assert.Equal(10.0, leaf1.Mm, 6);
            Assert.Equal(0.01, leaf1.Raw, 9);

            var leaf2 = LeafUnder(path, 100.0);
            Assert.Equal(20.0, leaf2.Mm, 6);
            Assert.Equal(0.02, leaf2.Raw, 9);
            Assert.Equal(1000.0, leaf2.Unit, 9);
            Assert.Equal(1000.0, leaf2.SwUnit, 9);
        }

        [Fact]
        public void AnInchFileWritesTheCloneInInches()
        {
            // sub-2 sits 4 in out. The file holds the leaf at 0.5 in, which
            // sub-1 shows. sub-2 wants the leaf at 1 in.
            string path = WriteFixture("in", 4.0, 0.5);
            var rw = new StepRewriter(path, null);
            var outcome = Run(rw,
                Instance("asm/sub-1", 0.0, 0.0127),
                Instance("asm/sub-2", 0.1016, 0.0254));

            Assert.Empty(outcome.FailedPaths);
            Assert.Equal(1, outcome.DefinitionsCloned);
            Assert.Equal(1, outcome.PlacementsRetargeted);
            rw.Save(path);

            var leaf2 = LeafUnder(path, 101.6);
            Assert.Equal(25.4, leaf2.Mm, 6);
            Assert.Equal(1.0, leaf2.Raw, 9);
            Assert.Equal(25.4, leaf2.Unit, 9);
            Assert.Equal(25.4, leaf2.SwUnit, 9);
        }

        [Fact]
        public void AMetreFileThatMatchesNobodyIsPatchedInMetres()
        {
            // The file says 99 mm. sub-1 wants 30 mm and keeps the original
            // definition, patched in place. sub-2 wants 40 mm and gets a
            // clone.
            string path = WriteFixture("m", 0.1, 0.099);
            var rw = new StepRewriter(path, null);
            var outcome = Run(rw,
                Instance("asm/sub-1", 0.0, 0.030),
                Instance("asm/sub-2", 0.1, 0.040));

            Assert.Empty(outcome.FailedPaths);
            Assert.Equal(1, outcome.DefinitionsCloned);
            Assert.Equal(2, outcome.PlacementsRetargeted);
            rw.Save(path);

            var leaf1 = LeafUnder(path, 0.0);
            Assert.Equal(30.0, leaf1.Mm, 6);
            Assert.Equal(0.03, leaf1.Raw, 9);
            Assert.Equal(1000.0, leaf1.SwUnit, 9);

            var leaf2 = LeafUnder(path, 100.0);
            Assert.Equal(40.0, leaf2.Mm, 6);
            Assert.Equal(0.04, leaf2.Raw, 9);
            Assert.Equal(1000.0, leaf2.SwUnit, 9);
        }
    }
}
