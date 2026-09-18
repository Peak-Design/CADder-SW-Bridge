using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The half of the native geometry path that does not need SolidWorks:
    /// recovering triangles from facet fins, and the .swmesh wire format.
    /// The COM half cannot be tested here at all, which is exactly why these
    /// two are worth pinning: a fin walked the wrong way and a field written
    /// in the wrong order both produce a file that loads and looks wrong.
    /// </summary>
    public class FacetStitcherTests
    {
        [Fact]
        public void StitchesAFacetWhoseFinsRunHeadToTail()
        {
            // 7 -> 8 -> 9 -> 7, every fin pointing the way the loop goes.
            int a, b, c;
            Assert.True(FacetStitcher.TryStitch(
                new[] { 7, 8, 8, 9, 9, 7 }, out a, out b, out c));
            Assert.Equal(7, a);
            Assert.Equal(8, b);
            Assert.Equal(9, c);
        }

        [Fact]
        public void StitchesAFacetWhoseLaterFinsAreReversed()
        {
            // Same triangle, but fins 1 and 2 are stored back to front: the
            // case that quietly builds a bow tie if the loop is assumed.
            int a, b, c;
            Assert.True(FacetStitcher.TryStitch(
                new[] { 7, 8, 9, 8, 7, 9 }, out a, out b, out c));
            Assert.Equal(7, a);
            Assert.Equal(8, b);
            Assert.Equal(9, c);
        }

        [Fact]
        public void StitchesAFacetWhoseFirstFinIsReversed()
        {
            // Fin 0 runs against the loop. The three corners are what matter,
            // which way round they come out does not, because NeedsFlip
            // settles winding from the vertex normals afterwards, and it is
            // the only thing that can: facet order alone cannot tell whether
            // the face is reversed relative to its body.
            int a, b, c;
            Assert.True(FacetStitcher.TryStitch(
                new[] { 8, 7, 8, 9, 9, 7 }, out a, out b, out c));
            Assert.Equal(new[] { 7, 8, 9 }, new[] { a, b, c }.OrderBy(v => v).ToArray());
        }

        [Theory]
        [InlineData(new[] { 7, 7, 8, 9, 9, 7 })]      // degenerate fin
        [InlineData(new[] { 7, 8, 1, 2, 3, 4 })]      // fins do not meet
        [InlineData(new[] { 7, 8 })]                  // truncated
        public void RefusesFinsThatAreNotATriangle(int[] pairs)
        {
            int a, b, c;
            Assert.False(FacetStitcher.TryStitch(pairs, out a, out b, out c));
        }

        [Fact]
        public void FlipsOnlyWhenTheWindingFightsTheNormals()
        {
            var p0 = new double[] { 0, 0, 0 };
            var p1 = new double[] { 1, 0, 0 };
            var p2 = new double[] { 0, 1, 0 };
            // This winding's own normal is +Z.
            Assert.False(FacetStitcher.NeedsFlip(p0, p1, p2, new double[] { 0, 0, 3 }));
            Assert.True(FacetStitcher.NeedsFlip(p0, p1, p2, new double[] { 0, 0, -3 }));
            // A sliver has no trustworthy normal of its own; leave it be.
            Assert.False(FacetStitcher.NeedsFlip(
                p0, new double[] { 1e-9, 0, 0 }, new double[] { 2e-9, 0, 0 },
                new double[] { 0, 0, -3 }));
        }
    }

    public class MeshWriterTests
    {
        /// <summary>A two-triangle scene with everything the format carries,
        /// chosen so every field is distinguishable if it lands in the wrong
        /// slot.</summary>
        internal static MeshScene Sample()
        {
            var scene = new MeshScene { Tolerance = 0.000125 };
            scene.Materials.Add(new MeshMaterial { Name = "grey" });
            scene.Materials.Add(new MeshMaterial
            {
                Name = "red",
                R = 1.0, G = 0.25, B = 0.125, A = 0.5,
                Roughness = 0.75, Metallic = 0.0,
                Texture = "red.png",
                Appearance = "{\"name\":\"red\",\"file\":\"red.p2m\",\"blender\":{\"metallic\":0.0}}",
            });
            var def = new MeshDefinition { Id = 3, Name = "bracket" };
            def.Positions.AddRange(new[]
            {
                0.0, 0.0, 0.0,
                1.0, 0.0, 0.0,
                0.0, 2.0, 0.0,
                1.0, 2.0, 0.0,
            });
            for (int i = 0; i < 4; i++) def.Normals.AddRange(new[] { 0.0, 0.0, 1.0 });
            def.Uvs.AddRange(new[] { 0.0, 0.0, 1.0, 0.0, 0.0, 1.0, 1.0, 1.0 });
            def.Triangles.AddRange(new[] { 0, 1, 2, 1, 3, 2 });
            def.TriangleMaterials.AddRange(new[] { 1, 0 });
            // Two bodies, so the file carries the section that says where
            // each starts. Only the numbers travel: nothing here checks that
            // the triangles keep to their own body.
            def.BodyStarts.AddRange(new[] { 0, 2 });
            scene.Definitions.Add(def);
            scene.Instances.Add(new MeshInstance
            {
                DefinitionId = 3,
                ComponentId = "c007",
                Name = "bracket",
                Path = "lifter-2/bracket-1",
                Transform = new double[]
                {
                    1, 0, 0, 0.5,
                    0, 1, 0, 1.5,
                    0, 0, 1, 2.5,
                    0, 0, 0, 1,
                },
                // A part of a rigid subassembly: it carries the
                // subassembly's component id and its own place inside it.
                Local = new double[]
                {
                    1, 0, 0, 0.25,
                    0, 1, 0, 1.0,
                    0, 0, 1, 1.75,
                    0, 0, 0, 1,
                },
            });
            // The branch the instance hangs under: a rigid subassembly,
            // which is one component and holds parts of its own.
            scene.Nodes.Add(new MeshNode
            {
                Path = "lifter-2",
                Name = "lifter",
                ComponentId = "c007",
                Transform = new double[]
                {
                    1, 0, 0, 0.25,
                    0, 1, 0, 0.5,
                    0, 0, 1, 0.75,
                    0, 0, 0, 1,
                },
            });
            return scene;
        }

        [Fact]
        public void WritesTheHeaderAndFlagsThatDescribeTheScene()
        {
            using (var ms = new MemoryStream())
            {
                MeshWriter.Write(ms, Sample());
                var bytes = ms.ToArray();
                Assert.Equal(MeshWriter.Magic, BitConverter.ToUInt32(bytes, 0));
                Assert.Equal(MeshWriter.Version, BitConverter.ToUInt32(bytes, 4));
                var flags = (MeshWriter.SceneFlags)BitConverter.ToUInt32(bytes, 8);
                Assert.True(flags.HasFlag(MeshWriter.SceneFlags.Normals));
                Assert.True(flags.HasFlag(MeshWriter.SceneFlags.Uvs));
                Assert.Equal(0.000125, BitConverter.ToDouble(bytes, 12), 12);
            }
        }

        [Fact]
        public void CountsTheNodeTableInTheHeader()
        {
            // The node count is the last field of the header, after the
            // instance count: a consumer that reads it from the wrong
            // offset gets a plausible number and a scene full of branches
            // that are not there.
            using (var ms = new MemoryStream())
            {
                MeshWriter.Write(ms, Sample());
                var bytes = ms.ToArray();
                Assert.Equal(2u, BitConverter.ToUInt32(bytes, 20));   // materials
                Assert.Equal(1u, BitConverter.ToUInt32(bytes, 24));   // definitions
                Assert.Equal(1u, BitConverter.ToUInt32(bytes, 28));   // instances
                Assert.Equal(1u, BitConverter.ToUInt32(bytes, 32));   // nodes
            }
        }

        [Fact]
        public void DropsTheNormalFlagWhenAnyDefinitionLacksThem()
        {
            var scene = Sample();
            scene.Definitions[0].Normals.Clear();
            using (var ms = new MemoryStream())
            {
                MeshWriter.Write(ms, scene);
                var flags = (MeshWriter.SceneFlags)BitConverter.ToUInt32(ms.ToArray(), 8);
                Assert.False(flags.HasFlag(MeshWriter.SceneFlags.Normals));
            }
        }

        [Fact]
        public void EndsWithTheNodeTableWhenEveryPartIsOneBody()
        {
            // No section at all for the usual case, so the file is exactly
            // what the version 3 writer made.
            var scene = Sample();
            scene.Definitions[0].BodyStarts.Clear();
            scene.Definitions[0].BodyStarts.Add(0);
            using (var plain = new MemoryStream())
            using (var sectioned = new MemoryStream())
            {
                MeshWriter.Write(plain, scene);
                MeshWriter.Write(sectioned, Sample());
                Assert.Equal(plain.Length + 4 + 4 + 4 + 2 * 4, sectioned.Length);
            }
        }

        [Fact]
        public void WritesWhereEachBodyStartsAfterTheNodes()
        {
            using (var ms = new MemoryStream())
            {
                MeshWriter.Write(ms, Sample());
                var bytes = ms.ToArray();
                int at = bytes.Length - (4 + 4 + 4 + 2 * 4);
                Assert.Equal("BODY", System.Text.Encoding.ASCII.GetString(bytes, at, 4));
                Assert.Equal(12u, BitConverter.ToUInt32(bytes, at + 4));   // length
                Assert.Equal(2u, BitConverter.ToUInt32(bytes, at + 8));    // count
                Assert.Equal(0, BitConverter.ToInt32(bytes, at + 12));
                Assert.Equal(2, BitConverter.ToInt32(bytes, at + 16));
            }
        }

        /// <summary>
        /// The golden file the Blender-side reader is tested against. The
        /// format crosses a language boundary, so the only test that means
        /// anything is one written by this code and read by that one; this
        /// half writes it where ci/rig/test_swmesh.py looks.
        ///
        /// Located from the SOURCE path, not the assembly's: the test runner
        /// shadow-copies the assembly and then deletes the copy, so a file
        /// written beside it exists only for as long as the test does.
        /// </summary>
        [Fact]
        public void WritesTheGoldenFileTheConsumerTestReads()
        {
            string dir = Path.Combine(SourceDirectory(), "golden");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "sample.swmesh");
            MeshWriter.Write(path, Sample());
            Assert.True(new FileInfo(path).Length > 100);
        }

        private static string SourceDirectory([CallerFilePath] string here = null)
        {
            return Path.GetDirectoryName(here);
        }
    }
}
