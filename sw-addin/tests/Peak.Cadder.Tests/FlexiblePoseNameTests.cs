using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Peak.Cadder.Appearance;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The flexible-twin fix finds the uses of a sub-assembly document by
    /// product name. A product named 'doc_config' is a configuration of the
    /// document, but another document can also have a name that starts with
    /// 'doc_', such as 'hinge_assy' next to 'hinge'.
    /// </summary>
    public class FlexiblePoseNameTests : IDisposable
    {
        private readonly List<string> _tempFiles = new List<string>();

        public void Dispose()
        {
            foreach (var f in _tempFiles)
                try { File.Delete(f); } catch (IOException) { }
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
                SubDocName = "hinge",
                ParentRelTranslationM = new[] { relXm, 0.0, 0.0 },
                Children = new List<FlexChildPose>
                {
                    new FlexChildPose { Key = "leaf-1", ProductName = "leaf", LocalM = Local(leafXm) },
                },
            };

        [Fact]
        public void AnotherDocumentWithTheSamePrefixIsNotAUse()
        {
            // 'hinge' is used twice, and one use is flexed. 'hinge_assy' is
            // another sub-assembly document, used once.
            var f = new AppearanceStepFixture();
            int asm = f.Product("asm");
            int hinge = f.Product("hinge");
            int other = f.Product("hinge_assy");
            int leaf = f.Product("leaf");
            f.Use(asm, hinge, 0, 0, 0);
            f.Use(asm, hinge, 100, 0, 0);
            f.Use(asm, other, 200, 0, 0);
            f.Use(hinge, leaf, 10, 0, 0);
            f.Use(other, leaf, 0, 0, 0);
            string path = f.Write(_tempFiles);

            var rw = new StepRewriter(path, null);
            var occs = rw.FindOccurrences();
            var request = new FlexFixRequest();
            request.Instances.Add(Instance("asm/hinge-1", 0.0, 0.010));
            request.Instances.Add(Instance("asm/hinge-2", 0.1, 0.020));
            var outcome = FlexiblePoseFixer.Fix(rw.Document, occs, rw.ChildrenByParentPd,
                new List<FlexFixRequest> { request }, null);

            Assert.Empty(outcome.FailedPaths);
            Assert.Equal(1, outcome.DefinitionsCloned);
            Assert.Equal(1, outcome.PlacementsRetargeted);
        }

        [Fact]
        public void TwoConfigurationsOfTheDocumentAreStillBothUses()
        {
            // One instance uses the default configuration ('hinge'), the
            // other the 'open' one ('hinge_open'). Both are uses of the
            // document, as before.
            var f = new AppearanceStepFixture();
            int asm = f.Product("asm");
            int hinge = f.Product("hinge");
            int open = f.Product("hinge_open");
            int leaf = f.Product("leaf");
            f.Use(asm, hinge, 0, 0, 0);
            f.Use(asm, open, 100, 0, 0);
            f.Use(hinge, leaf, 10, 0, 0);
            f.Use(open, leaf, 10, 0, 0);
            string path = f.Write(_tempFiles);

            var rw = new StepRewriter(path, null);
            var occs = rw.FindOccurrences();
            var request = new FlexFixRequest();
            request.Instances.Add(Instance("asm/hinge-1", 0.0, 0.010));
            request.Instances.Add(Instance("asm/hinge-2", 0.1, 0.010));
            var outcome = FlexiblePoseFixer.Fix(rw.Document, occs, rw.ChildrenByParentPd,
                new List<FlexFixRequest> { request }, null);

            Assert.Empty(outcome.FailedPaths);
            Assert.Equal(0, outcome.PlacementsRetargeted);
        }
    }
}
