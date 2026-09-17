using System;
using System.Collections.Generic;
using Peak.Cadder.Core.Model;
using Peak.Cadder.Sw;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The UV map a part gets over the live link.
    ///
    /// SolidWorks hands out the surface's own parameters, and those are in
    /// the surface's own units: metres on a plane, radians by metres on a
    /// cylinder. Written straight into a UV map, the cylinder comes out
    /// hundreds of times too wide and the plane beside it looks right,
    /// which is what a stretched island IS. These hold the cure: each
    /// direction scaled by how much length one unit of it covers.
    /// </summary>
    public class SurfaceUvTests
    {
        private const double Radius = 0.02;      // 20 mm
        private const double Height = 0.01;      // 10 mm

        [Fact]
        public void ACylinderComesBackAsArcLengthByHeight()
        {
            var faceOf = new List<int>();
            var mesh = new MeshDefinition();
            Cylinder(mesh, faceOf, 0, 0.0, Math.PI, 24);

            // As it arrives: u runs over half a turn, which is 3.14 against
            // a height of 0.01. That is the island that looks like spaghetti.
            Assert.InRange(Span(mesh, 0), Math.PI - 1e-9, Math.PI + 1e-9);

            int written = SurfaceUv.Rewrite(
                mesh, 0, mesh.VertexCount, 0, new[] { 0 }, 1, faceOf.ToArray(), null);

            Assert.Equal(mesh.VertexCount, written);
            // Half the circumference by the height, both in metres.
            Near(Radius * Math.PI, Span(mesh, 0), 0.005);
            Near(Height, Span(mesh, 1), 0.005);
        }

        [Fact]
        public void APlaneIsAlreadyInMetresAndIsLeftAtItsOwnSize()
        {
            var faceOf = new List<int>();
            var mesh = new MeshDefinition();
            Plane(mesh, faceOf, 0, 0.05, 0.03);

            SurfaceUv.Rewrite(mesh, 0, mesh.VertexCount, 0,
                              new[] { 0 }, 1, faceOf.ToArray(), null);

            Near(0.05, Span(mesh, 0), 1e-9);
            Near(0.03, Span(mesh, 1), 1e-9);
        }

        [Fact]
        public void EveryChartStartsAtItsOwnCornerOfTheOrigin()
        {
            var faceOf = new List<int>();
            var mesh = new MeshDefinition();
            Plane(mesh, faceOf, 0, 0.05, 0.03);
            Plane(mesh, faceOf, 1, 0.05, 0.03);

            SurfaceUv.Rewrite(mesh, 0, mesh.VertexCount, 0,
                              new[] { 0, 1 }, 2, faceOf.ToArray(), null);

            // Two faces of one size would otherwise write exactly the same
            // UVs, and Blender reads that as one island folded on itself.
            double first = Low(mesh, faceOf, 0, 0);
            double second = Low(mesh, faceOf, 1, 0);
            Assert.NotEqual(first, second);
            Assert.InRange(first, 0.0, SurfaceUv.ChartNudge);
            Assert.InRange(second, 0.0, SurfaceUv.ChartNudge);
        }

        [Fact]
        public void TwoPatchesOfOneCylinderLineUpInsteadOfOverlapping()
        {
            var faceOf = new List<int>();
            var mesh = new MeshDefinition();
            Cylinder(mesh, faceOf, 0, 0.0, Math.PI, 24);
            Cylinder(mesh, faceOf, 1, Math.PI, 2.0 * Math.PI, 24);

            // One chart, because the two faces lie on one cylinder.
            SurfaceUv.Rewrite(mesh, 0, mesh.VertexCount, 0,
                              new[] { 0, 0 }, 1, faceOf.ToArray(), null);

            double halfWay = Radius * Math.PI;
            Near(halfWay, High(mesh, faceOf, 0, 0) - Low(mesh, faceOf, 0, 0), 0.005);
            // The second patch starts where the first ends, so the seam of
            // the hole is a seam and not a fold.
            Near(High(mesh, faceOf, 0, 0), Low(mesh, faceOf, 1, 0), 0.01);
        }

        [Fact]
        public void TwoPatchesInTwoChartsLandOnTopOfEachOther()
        {
            var faceOf = new List<int>();
            var mesh = new MeshDefinition();
            Cylinder(mesh, faceOf, 0, 0.0, Math.PI, 24);
            Cylinder(mesh, faceOf, 1, Math.PI, 2.0 * Math.PI, 24);

            SurfaceUv.Rewrite(mesh, 0, mesh.VertexCount, 0,
                              new[] { 0, 1 }, 2, faceOf.ToArray(), null);

            // Which is what the chart is FOR: apart, they both start at the
            // origin and cover the same ground.
            Near(Low(mesh, faceOf, 0, 0), Low(mesh, faceOf, 1, 0),
                 SurfaceUv.ChartNudge * 2.0);
        }

        [Fact]
        public void AFaceWhoseScaleChangesIsSizedWhereMostOfItIs()
        {
            var faceOf = new List<int>();
            var mesh = new MeshDefinition();
            Ramp(mesh, faceOf, 0);

            SurfaceUv.Rewrite(mesh, 0, mesh.VertexCount, 0,
                              new[] { 0 }, 1, faceOf.ToArray(), null);

            // A worm thread or a spline runs at one scale over most of
            // its area and at another over a corner of it. Least squares
            // answers for the corner, because it works on squared lengths,
            // and the rest of the face then comes out many times too small.
            // What a texture is looked at on is the rest of the face.
            Near(1.0, MedianRatio(mesh, faceOf), 0.4);
        }

        [Fact]
        public void AVertexNoFaceClaimedKeepsWhatSolidWorksGaveIt()
        {
            var faceOf = new List<int>();
            var mesh = new MeshDefinition();
            Cylinder(mesh, faceOf, 0, 0.0, Math.PI, 8);
            var marks = faceOf.ToArray();
            marks[marks.Length - 1] = -1;
            double was = mesh.Uvs[(marks.Length - 1) * 2];

            var said = new List<string>();
            int written = SurfaceUv.Rewrite(
                mesh, 0, mesh.VertexCount, 0, new[] { 0 }, 1, marks, said.Add);

            Assert.Equal(mesh.VertexCount - 1, written);
            Assert.Equal(was, mesh.Uvs[(marks.Length - 1) * 2]);
            Assert.Single(said);
        }

        [Fact]
        public void ABodyThisCannotReadIsLeftExactlyAsItWas()
        {
            var mesh = new MeshDefinition();
            Assert.Equal(0, SurfaceUv.Rewrite(mesh, 0, 0, 0, new[] { 0 }, 1,
                                              new int[0], null));
            Assert.Equal(0, SurfaceUv.Rewrite(mesh, 0, 4, 0, null, 1,
                                              new[] { 0, 0, 0, 0 }, null));
        }

        // ── Fixtures ──────────────────────────────────────────────────────

        /// <summary>A band of a cylinder about Z, tessellated the way
        /// SolidWorks does: u is the angle in radians, v the height in
        /// metres.</summary>
        private static void Cylinder(
            MeshDefinition mesh, List<int> faceOf, int face,
            double from, double to, int steps)
        {
            int first = mesh.VertexCount;
            for (int i = 0; i <= steps; i++)
            {
                double angle = from + (to - from) * i / steps;
                for (int row = 0; row < 2; row++)
                {
                    double z = row * Height;
                    mesh.Positions.Add(Radius * Math.Cos(angle));
                    mesh.Positions.Add(Radius * Math.Sin(angle));
                    mesh.Positions.Add(z);
                    mesh.Normals.Add(Math.Cos(angle));
                    mesh.Normals.Add(Math.Sin(angle));
                    mesh.Normals.Add(0.0);
                    mesh.Uvs.Add(angle);
                    mesh.Uvs.Add(z);
                    faceOf.Add(face);
                }
            }
            for (int i = 0; i < steps; i++)
            {
                int a = first + i * 2;
                Triangle(mesh, a, a + 1, a + 2);
                Triangle(mesh, a + 1, a + 3, a + 2);
            }
        }

        /// <summary>A flat quad, where the parameters are already metres.
        /// </summary>
        private static void Plane(
            MeshDefinition mesh, List<int> faceOf, int face,
            double wide, double tall)
        {
            int first = mesh.VertexCount;
            double[,] corners = { { 0, 0 }, { wide, 0 }, { wide, tall }, { 0, tall } };
            for (int i = 0; i < 4; i++)
            {
                mesh.Positions.Add(corners[i, 0]);
                mesh.Positions.Add(corners[i, 1]);
                mesh.Positions.Add(0.0);
                mesh.Normals.Add(0.0);
                mesh.Normals.Add(0.0);
                mesh.Normals.Add(1.0);
                mesh.Uvs.Add(corners[i, 0]);
                mesh.Uvs.Add(corners[i, 1]);
                faceOf.Add(face);
            }
            Triangle(mesh, first, first + 1, first + 2);
            Triangle(mesh, first, first + 2, first + 3);
        }

        /// <summary>A strip that steps one unit of u at a time, where two
        /// hundred steps span half a millimetre each and one spans fifty.
        /// What a surface whose parameters run at two scales looks like to
        /// the tessellation: most of the face at one, a corner of it at the
        /// other.</summary>
        private static void Ramp(MeshDefinition mesh, List<int> faceOf, int face)
        {
            const int Steps = 201;
            int first = mesh.VertexCount;
            double x = 0.0;
            for (int i = 0; i <= Steps; i++)
            {
                for (int row = 0; row < 2; row++)
                {
                    mesh.Positions.Add(x);
                    mesh.Positions.Add(row * 0.002);
                    mesh.Positions.Add(0.0);
                    mesh.Normals.Add(0.0);
                    mesh.Normals.Add(0.0);
                    mesh.Normals.Add(1.0);
                    mesh.Uvs.Add(i);
                    mesh.Uvs.Add(row);
                    faceOf.Add(face);
                }
                x += i == Steps - 1 ? 0.05 : 0.0005;
            }
            for (int i = 0; i < Steps; i++)
            {
                int a = first + i * 2;
                Triangle(mesh, a, a + 1, a + 2);
                Triangle(mesh, a + 1, a + 3, a + 2);
            }
        }

        /// <summary>The middle of how far the UV map is from the part: one
        /// is a texture that does not stretch.</summary>
        private static double MedianRatio(MeshDefinition mesh, List<int> faceOf)
        {
            var found = new List<double>();
            for (int t = 0; t + 2 < mesh.Triangles.Count; t += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = mesh.Triangles[t + k];
                    int b = mesh.Triangles[t + (k + 1) % 3];
                    double du = mesh.Uvs[b * 2] - mesh.Uvs[a * 2];
                    double dv = mesh.Uvs[b * 2 + 1] - mesh.Uvs[a * 2 + 1];
                    double dx = mesh.Positions[b * 3] - mesh.Positions[a * 3];
                    double dy = mesh.Positions[b * 3 + 1] - mesh.Positions[a * 3 + 1];
                    double dz = mesh.Positions[b * 3 + 2] - mesh.Positions[a * 3 + 2];
                    double real = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (real <= 1e-12) continue;
                    found.Add(Math.Sqrt(du * du + dv * dv) / real);
                }
            found.Sort();
            return found[found.Count / 2];
        }

        private static void Triangle(MeshDefinition mesh, int a, int b, int c)
        {
            mesh.Triangles.Add(a);
            mesh.Triangles.Add(b);
            mesh.Triangles.Add(c);
            mesh.TriangleMaterials.Add(0);
        }

        private static double Span(MeshDefinition mesh, int channel)
        {
            double low = double.MaxValue, high = double.MinValue;
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                double value = mesh.Uvs[v * 2 + channel];
                if (value < low) low = value;
                if (value > high) high = value;
            }
            return high - low;
        }

        private static double Low(
            MeshDefinition mesh, List<int> faceOf, int face, int channel)
        {
            double low = double.MaxValue;
            for (int v = 0; v < mesh.VertexCount; v++)
                if (faceOf[v] == face && mesh.Uvs[v * 2 + channel] < low)
                    low = mesh.Uvs[v * 2 + channel];
            return low;
        }

        private static double High(
            MeshDefinition mesh, List<int> faceOf, int face, int channel)
        {
            double high = double.MinValue;
            for (int v = 0; v < mesh.VertexCount; v++)
                if (faceOf[v] == face && mesh.Uvs[v * 2 + channel] > high)
                    high = mesh.Uvs[v * 2 + channel];
            return high;
        }

        private static void Near(double want, double got, double slack)
        {
            Assert.True(Math.Abs(want - got) <= slack,
                        "wanted " + want + ", got " + got);
        }
    }
}
