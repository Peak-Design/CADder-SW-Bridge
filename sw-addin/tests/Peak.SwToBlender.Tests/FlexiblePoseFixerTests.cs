using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Peak.SwToBlender.Appearance;
using Xunit;

namespace Peak.SwToBlender.Tests
{
    /// <summary>
    /// The flexible-twin STEP fix, against a synthetic Part-21 file: an
    /// assembly using one sub twice, the sub holding one leaf. The layouts
    /// mirror live corpus 07 flexible-sub2 (2026-08-22): the file carries the
    /// FLEXED layout, so the rigid twin needs its own definition posed at the
    /// document layout. Everything runs through the real StepRewriter parse
    /// and a real save/re-parse round trip — the assertions read the output
    /// file the way any consumer would.
    /// </summary>
    public class FlexiblePoseFixerTests : IDisposable
    {
        private readonly List<string> _tempFiles = new List<string>();

        public void Dispose()
        {
            foreach (var f in _tempFiles)
                try { File.Delete(f); } catch (IOException) { }
        }

        /// <summary>Root 'asm' uses 'sub' at (0,0,0) and (100,0,0) mm; 'sub'
        /// holds 'leaf' at leafX/leafY/leafZ mm, axes Z/X.</summary>
        private string WriteFixture(double leafX, double leafY, double leafZ)
        {
            var lines = new[]
            {
                "ISO-10303-21;",
                "HEADER;",
                "FILE_DESCRIPTION((''),'2;1');",
                "FILE_NAME('t','2026-08-23',(''),(''),'','','');",
                "FILE_SCHEMA(('AUTOMOTIVE_DESIGN'));",
                "ENDSEC;",
                "DATA;",
                "#90=REPRESENTATION_CONTEXT('','');",
                "#91=PRODUCT_DEFINITION_CONTEXT('part definition',#93,'design');",
                "#92=PRODUCT_CONTEXT('',#93,'mechanical');",
                "#93=APPLICATION_CONTEXT('automotive design');",
                "#1=PRODUCT('asm','asm','',(#92));",
                "#2=PRODUCT_DEFINITION_FORMATION('','',#1);",
                "#3=PRODUCT_DEFINITION('design','',#2,#91);",
                "#4=PRODUCT('sub','sub','',(#92));",
                "#5=PRODUCT_DEFINITION_FORMATION('','',#4);",
                "#6=PRODUCT_DEFINITION('design','',#5,#91);",
                "#7=PRODUCT('leaf','leaf','',(#92));",
                "#8=PRODUCT_DEFINITION_FORMATION('','',#7);",
                "#9=PRODUCT_DEFINITION('design','',#8,#91);",
                // Assembly shape representations list their children's
                // placement axes — that is what the definition clone walks.
                "#11=AXIS2_PLACEMENT_3D('',#50,#51,#52);",
                "#50=CARTESIAN_POINT('',(0.,0.,0.));",
                "#51=DIRECTION('',(0.,0.,1.));",
                "#52=DIRECTION('',(1.,0.,0.));",
                "#10=SHAPE_REPRESENTATION('',(#11,#26,#34),#90);",
                "#13=AXIS2_PLACEMENT_3D('',#53,#54,#55);",
                "#53=CARTESIAN_POINT('',(0.,0.,0.));",
                "#54=DIRECTION('',(0.,0.,1.));",
                "#55=DIRECTION('',(1.,0.,0.));",
                "#12=SHAPE_REPRESENTATION('',(#13,#42),#90);",
                "#15=AXIS2_PLACEMENT_3D('',#56,#57,#58);",
                "#56=CARTESIAN_POINT('',(0.,0.,0.));",
                "#57=DIRECTION('',(0.,0.,1.));",
                "#58=DIRECTION('',(1.,0.,0.));",
                "#14=SHAPE_REPRESENTATION('',(#15),#90);",
                "#16=PRODUCT_DEFINITION_SHAPE('','',#3);",
                "#17=SHAPE_DEFINITION_REPRESENTATION(#16,#10);",
                "#18=PRODUCT_DEFINITION_SHAPE('','',#6);",
                "#19=SHAPE_DEFINITION_REPRESENTATION(#18,#12);",
                "#20=PRODUCT_DEFINITION_SHAPE('','',#9);",
                "#21=SHAPE_DEFINITION_REPRESENTATION(#20,#14);",
                // sub-1 at origin
                "#22=NEXT_ASSEMBLY_USAGE_OCCURRENCE('NAUO1','sub-1','',#3,#6,$);",
                "#25=PRODUCT_DEFINITION_SHAPE('Placement','',#22);",
                "#27=CARTESIAN_POINT('',(0.,0.,0.));",
                "#28=DIRECTION('',(0.,0.,1.));",
                "#29=DIRECTION('',(1.,0.,0.));",
                "#26=AXIS2_PLACEMENT_3D('',#27,#28,#29);",
                "#30=ITEM_DEFINED_TRANSFORMATION('','',#26,#13);",
                "#31=( REPRESENTATION_RELATIONSHIP('','',#12,#10) "
                    + "REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION(#30) "
                    + "SHAPE_REPRESENTATION_RELATIONSHIP() );",
                "#32=CONTEXT_DEPENDENT_SHAPE_REPRESENTATION(#31,#25);",
                // sub-2 at (100,0,0)
                "#23=NEXT_ASSEMBLY_USAGE_OCCURRENCE('NAUO2','sub-2','',#3,#6,$);",
                "#33=PRODUCT_DEFINITION_SHAPE('Placement','',#23);",
                "#35=CARTESIAN_POINT('',(100.,0.,0.));",
                "#36=DIRECTION('',(0.,0.,1.));",
                "#37=DIRECTION('',(1.,0.,0.));",
                "#34=AXIS2_PLACEMENT_3D('',#35,#36,#37);",
                "#38=ITEM_DEFINED_TRANSFORMATION('','',#34,#13);",
                "#39=( REPRESENTATION_RELATIONSHIP('','',#12,#10) "
                    + "REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION(#38) "
                    + "SHAPE_REPRESENTATION_RELATIONSHIP() );",
                "#40=CONTEXT_DEPENDENT_SHAPE_REPRESENTATION(#39,#33);",
                // leaf inside sub
                "#24=NEXT_ASSEMBLY_USAGE_OCCURRENCE('NAUO3','leaf-1','',#6,#9,$);",
                "#41=PRODUCT_DEFINITION_SHAPE('Placement','',#24);",
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "#43=CARTESIAN_POINT('',({0},{1},{2}));", leafX, leafY, leafZ),
                "#44=DIRECTION('',(0.,0.,1.));",
                "#45=DIRECTION('',(1.,0.,0.));",
                "#42=AXIS2_PLACEMENT_3D('',#43,#44,#45);",
                "#46=ITEM_DEFINED_TRANSFORMATION('','',#42,#15);",
                "#47=( REPRESENTATION_RELATIONSHIP('','',#14,#12) "
                    + "REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION(#46) "
                    + "SHAPE_REPRESENTATION_RELATIONSHIP() );",
                "#48=CONTEXT_DEPENDENT_SHAPE_REPRESENTATION(#47,#41);",
                "ENDSEC;",
                "END-ISO-10303-21;",
            };
            string path = Path.Combine(Path.GetTempPath(),
                "swtb-flexfix-" + Guid.NewGuid().ToString("N") + ".step");
            File.WriteAllText(path, string.Join("\r\n", lines));
            _tempFiles.Add(path);
            return path;
        }

        private static double[,] Local(double xM, double yM, double zM,
            double zRotDeg = 0)
        {
            double c = Math.Cos(zRotDeg * Math.PI / 180.0);
            double s = Math.Sin(zRotDeg * Math.PI / 180.0);
            // Snap the fp dust of exact-degree rotations so the written
            // DIRECTION triples are exact literals the assertions can grep.
            if (Math.Abs(c) < 1e-12) c = 0;
            if (Math.Abs(s) < 1e-12) s = 0;
            return new[,]
            {
                { c, -s, 0.0, xM },
                { s, c, 0.0, yM },
                { 0.0, 0.0, 1.0, zM },
                { 0.0, 0.0, 0.0, 1.0 },
            };
        }

        private static FlexInstanceLayout Instance(
            string path, double relXm, params FlexChildPose[] children)
            => new FlexInstanceLayout
            {
                Path = path,
                SubDocName = "sub",
                ParentRelTranslationM = new[] { relXm, 0.0, 0.0 },
                Children = children.ToList(),
            };

        private static FlexChildPose Leaf(double[,] local)
            => new FlexChildPose { Key = "leaf-1", ProductName = "leaf", LocalM = local };

        [Fact]
        public void RigidTwinGetsItsOwnDefinitionAtTheDocumentLayout()
        {
            // File holds the FLEXED layout (leaf at 10mm — the flexible
            // instance's pose). The rigid twin wants the document layout
            // (leaf at 20mm, rotated 90°).
            string path = WriteFixture(10, 0, 0);
            var rw = new StepRewriter(path, null);
            var occs = rw.FindOccurrences();

            var request = new FlexFixRequest();
            request.Instances.Add(Instance("asm/sub-1", 0.0, Leaf(Local(0.010, 0, 0))));
            request.Instances.Add(Instance("asm/sub-2", 0.1, Leaf(Local(0.020, 0, 0.005, 90))));

            var outcome = FlexiblePoseFixer.Fix(rw.Document, occs,
                rw.ChildrenByParentPd,
                new List<FlexFixRequest> { request }, null);

            Assert.Empty(outcome.FailedPaths);
            Assert.Equal(1, outcome.DefinitionsCloned);
            Assert.Equal(1, outcome.PlacementsRetargeted);
            Assert.Contains("asm/sub-1", outcome.FixedPaths);
            Assert.Contains("asm/sub-2", outcome.FixedPaths);

            rw.Save(path);

            // Read the output like any consumer: two DIFFERENT sub
            // definitions now, each with its own leaf placement.
            var rw2 = new StepRewriter(path, null);
            var occs2 = rw2.FindOccurrences();
            var subUses = occs2.Where(o => o.ProductName == "sub").ToList();
            Assert.Equal(2, subUses.Count);
            var use1 = subUses.Single(o => Math.Abs(o.Translation[0]) < 1e-9);
            var use2 = subUses.Single(o => Math.Abs(o.Translation[0] - 100.0) < 1e-9);
            Assert.NotEqual(use1.ChildPd, use2.ChildPd);

            var leaf1 = Assert.Single(rw2.ChildrenByParentPd[use1.ChildPd]);
            Assert.Equal(10.0, leaf1.Translation[0], 6);

            var leaf2 = Assert.Single(rw2.ChildrenByParentPd[use2.ChildPd]);
            Assert.Equal(20.0, leaf2.Translation[0], 6);
            Assert.Equal(5.0, leaf2.Translation[2], 6);

            // The rotation went into the clone's fresh placement axes.
            string text = File.ReadAllText(path);
            Assert.Contains("DIRECTION('',(0,1,0))", text.Replace(" ", ""));
        }

        [Fact]
        public void WhenTheFileMatchesNobodyTheOriginalIsPatchedInPlace()
        {
            // File says 99mm; instance A wants 30mm, instance B wants 40mm.
            // A (first of the equally-sized classes) takes the original,
            // patched in place; B gets a clone.
            string path = WriteFixture(99, 0, 0);
            var rw = new StepRewriter(path, null);
            var occs = rw.FindOccurrences();

            var request = new FlexFixRequest();
            request.Instances.Add(Instance("asm/sub-1", 0.0, Leaf(Local(0.030, 0, 0))));
            request.Instances.Add(Instance("asm/sub-2", 0.1, Leaf(Local(0.040, 0, 0))));

            var outcome = FlexiblePoseFixer.Fix(rw.Document, occs,
                rw.ChildrenByParentPd,
                new List<FlexFixRequest> { request }, null);

            Assert.Empty(outcome.FailedPaths);
            Assert.Equal(1, outcome.DefinitionsCloned);
            Assert.Equal(2, outcome.PlacementsRetargeted);

            rw.Save(path);
            var rw2 = new StepRewriter(path, null);
            var occs2 = rw2.FindOccurrences();
            var subUses = occs2.Where(o => o.ProductName == "sub").ToList();
            var use1 = subUses.Single(o => Math.Abs(o.Translation[0]) < 1e-9);
            var use2 = subUses.Single(o => Math.Abs(o.Translation[0] - 100.0) < 1e-9);
            Assert.NotEqual(use1.ChildPd, use2.ChildPd);

            var leaf1 = Assert.Single(rw2.ChildrenByParentPd[use1.ChildPd]);
            var leaf2 = Assert.Single(rw2.ChildrenByParentPd[use2.ChildPd]);
            Assert.Equal(30.0, leaf1.Translation[0], 6);
            Assert.Equal(40.0, leaf2.Translation[0], 6);
        }

        [Fact]
        public void InstanceCountMismatchFailsHonestlyAndChangesNothing()
        {
            string path = WriteFixture(10, 0, 0);
            string before = File.ReadAllText(path);
            var rw = new StepRewriter(path, null);
            var occs = rw.FindOccurrences();

            // Three SW instances claimed, two uses in the file.
            var request = new FlexFixRequest();
            request.Instances.Add(Instance("asm/sub-1", 0.0, Leaf(Local(0.010, 0, 0))));
            request.Instances.Add(Instance("asm/sub-2", 0.1, Leaf(Local(0.020, 0, 0))));
            request.Instances.Add(Instance("asm/sub-3", 0.2, Leaf(Local(0.030, 0, 0))));

            var outcome = FlexiblePoseFixer.Fix(rw.Document, occs,
                rw.ChildrenByParentPd,
                new List<FlexFixRequest> { request }, null);

            Assert.Equal(3, outcome.FailedPaths.Count);
            Assert.Equal(0, outcome.DefinitionsCloned);
            Assert.Equal(0, outcome.PlacementsRetargeted);
            Assert.Single(outcome.Notes);

            rw.Save(path);
            Assert.Equal(before, File.ReadAllText(path));
        }

        [Fact]
        public void MatchingInstancesLeaveTheFileByteIdentical()
        {
            // Both instances already agree with the file: nothing to clone,
            // nothing to retarget, no accidental writes.
            string path = WriteFixture(10, 0, 0);
            string before = File.ReadAllText(path);
            var rw = new StepRewriter(path, null);
            var occs = rw.FindOccurrences();

            var request = new FlexFixRequest();
            request.Instances.Add(Instance("asm/sub-1", 0.0, Leaf(Local(0.010, 0, 0))));
            request.Instances.Add(Instance("asm/sub-2", 0.1, Leaf(Local(0.010, 0, 0))));

            var outcome = FlexiblePoseFixer.Fix(rw.Document, occs,
                rw.ChildrenByParentPd,
                new List<FlexFixRequest> { request }, null);

            Assert.Equal(0, outcome.DefinitionsCloned);
            Assert.Equal(0, outcome.PlacementsRetargeted);
            Assert.Empty(outcome.FailedPaths);

            rw.Save(path);
            Assert.Equal(before, File.ReadAllText(path));
        }
    }
}
