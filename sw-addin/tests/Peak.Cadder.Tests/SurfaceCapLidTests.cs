using System;
using System.Collections.Generic;
using Peak.Cadder.Core.Model;
using Peak.Cadder.Sw;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A lid closes a rim that a face keeps after the feature behind it
    /// went. The rim comes from the face's own triangles, in the direction
    /// they run along it. For a closed mesh every edge is run once each way,
    /// so the lid must run every rim edge AGAINST that direction. Its
    /// winding cannot come from the normals: at the end circle of a
    /// cylinder they are radial, square to the lid, and say nothing.
    /// </summary>
    public class SurfaceCapLidTests
    {
        /// <summary>The top circle of a hole wall, 24 points, in the order
        /// the wall's triangles run along it.</summary>
        private static void Ring(bool clockwise, out List<int> ring,
                                 out Dictionary<int, double[]> point)
        {
            ring = new List<int>();
            point = new Dictionary<int, double[]>();
            for (int i = 0; i < 24; i++)
            {
                double a = (clockwise ? -1 : 1) * 2.0 * Math.PI * i / 24;
                int v = 100 + i;
                ring.Add(v);
                point[v] = new[] { 0.003 * Math.Cos(a), 0.003 * Math.Sin(a), 0.01 };
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheLidRunsEveryRimEdgeAgainstTheFace(bool clockwise)
        {
            List<int> ring;
            Dictionary<int, double[]> point;
            Ring(clockwise, out ring, out point);
            var lid = SurfaceCap.Lid(ring, point);
            Assert.NotNull(lid);

            var edges = new HashSet<long>();
            for (int t = 0; t + 2 < lid.Count; t += 3)
            {
                edges.Add(Key(lid[t], lid[t + 1]));
                edges.Add(Key(lid[t + 1], lid[t + 2]));
                edges.Add(Key(lid[t + 2], lid[t]));
            }
            for (int i = 0; i < ring.Count; i++)
            {
                int a = ring[i], b = ring[(i + 1) % ring.Count];
                Assert.False(edges.Contains(Key(a, b)), "rim edge " + i + " runs with the face");
                Assert.True(edges.Contains(Key(b, a)), "rim edge " + i + " is not in the lid");
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheLidFacesTheWayItsTrianglesAreWound(bool clockwise)
        {
            List<int> ring;
            Dictionary<int, double[]> point;
            Ring(clockwise, out ring, out point);
            double[] facing;
            var lid = SurfaceCap.Lid(ring, point, out facing);
            Assert.NotNull(lid);
            Assert.NotNull(facing);
            for (int t = 0; t + 2 < lid.Count; t += 3)
            {
                var n = Cross(point[lid[t]], point[lid[t + 1]], point[lid[t + 2]]);
                Assert.True(Dot(n, facing) > 0.0, "lid triangle " + t / 3 + " faces the other way");
            }
        }

        /// <summary>At the end circle of a hole wall every normal is radial,
        /// square to the lid, and a lid that used them would shade dark. On
        /// a small cross hole in a shaft the normals are near the lid's own,
        /// and the lid shades as the shaft does.</summary>
        [Theory]
        [InlineData(90.0, false)]
        [InlineData(70.0, false)]
        [InlineData(45.0, true)]
        [InlineData(0.0, true)]
        public void TheRimNormalsDescribeTheLidOnlyWhenTheyAreNearItsOwn(
            double degrees, bool describes)
        {
            List<int> ring;
            Dictionary<int, double[]> point;
            Ring(false, out ring, out point);
            double[] facing;
            SurfaceCap.Lid(ring, point, out facing);
            var normal = new Dictionary<int, double[]>();
            double tilt = degrees * Math.PI / 180.0;
            foreach (int v in ring)
            {
                var p = point[v];
                double r = Math.Sqrt(p[0] * p[0] + p[1] * p[1]);
                normal[v] = new[]
                {
                    Math.Sin(tilt) * p[0] / r + Math.Cos(tilt) * facing[0],
                    Math.Sin(tilt) * p[1] / r + Math.Cos(tilt) * facing[1],
                    Math.Cos(tilt) * facing[2],
                };
            }
            Assert.Equal(describes, SurfaceCap.RimDescribes(ring, normal, facing));
        }

        [Fact]
        public void AMissingRimNormalDoesNotDescribeTheLid()
        {
            List<int> ring;
            Dictionary<int, double[]> point;
            Ring(false, out ring, out point);
            double[] facing;
            SurfaceCap.Lid(ring, point, out facing);
            var normal = new Dictionary<int, double[]>();
            foreach (int v in ring) normal[v] = new[] { facing[0], facing[1], facing[2] };
            normal[ring[5]] = new double[3];
            Assert.False(SurfaceCap.RimDescribes(ring, normal, facing));
        }

        /// <summary>A flat lid goes on copies of the rim points: in the same
        /// place, with the same surface parameters, and with the lid's own
        /// normal. The wall keeps its points and its normals.</summary>
        [Fact]
        public void AFlatLidGetsCopiesOfTheRimPointsWithItsOwnNormal()
        {
            List<int> ring;
            Dictionary<int, double[]> point;
            Ring(false, out ring, out point);
            double[] facing;
            var lid = SurfaceCap.Lid(ring, point, out facing);

            // A body before this one, then this body: the rim points and
            // a few more, all with radial normals.
            var mesh = new MeshDefinition();
            for (int i = 0; i < 3; i++) AddPoint(mesh, new double[3], new[] { 0.0, 0.0, 1.0 }, 9, 9);
            int baseVertex = mesh.VertexCount;
            int count = 130;
            for (int v = 0; v < count; v++)
            {
                double[] p;
                if (!point.TryGetValue(v, out p)) p = new[] { v * 0.001, 0.0, 0.0 };
                double r = Math.Sqrt(p[0] * p[0] + p[1] * p[1]);
                AddPoint(mesh, p, r > 0 ? new[] { p[0] / r, p[1] / r, 0.0 } : new[] { 1.0, 0.0, 0.0 },
                         v, -v);
            }
            var faceOf = new int[count];
            for (int v = 0; v < count; v++) faceOf[v] = 4;
            var state = new BodyTessellator.FacetState
            {
                Mesh = mesh, BaseVertex = baseVertex, VertexCount = count, FaceOf = faceOf,
            };

            var moved = BodyTessellator.OwnPoints(state, lid, facing);

            Assert.Equal(count + ring.Count, state.VertexCount);
            Assert.Equal(baseVertex + state.VertexCount, mesh.VertexCount);
            Assert.Equal(mesh.Positions.Count, mesh.Normals.Count);
            Assert.Equal(mesh.VertexCount * 2, mesh.Uvs.Count);
            Assert.Equal(state.VertexCount, state.FaceOf.Length);
            Assert.Equal(lid.Count, moved.Count);
            for (int i = 0; i < lid.Count; i++)
            {
                int from = baseVertex + lid[i], to = baseVertex + moved[i];
                Assert.True(moved[i] >= count, "lid corner " + i + " is still on the rim point");
                for (int k = 0; k < 3; k++)
                {
                    Assert.Equal(mesh.Positions[from * 3 + k], mesh.Positions[to * 3 + k]);
                    Assert.Equal(facing[k], mesh.Normals[to * 3 + k]);
                }
                Assert.Equal(mesh.Uvs[from * 2], mesh.Uvs[to * 2]);
                Assert.Equal(mesh.Uvs[from * 2 + 1], mesh.Uvs[to * 2 + 1]);
                Assert.Equal(-1, state.FaceOf[moved[i]]);
            }
            // One copy per rim point, shared by the lid's own triangles.
            Assert.Equal(ring.Count, new HashSet<int>(moved).Count);
            // The wall's points are as they were.
            foreach (int v in ring)
                Assert.Equal(0.0, mesh.Normals[(baseVertex + v) * 3 + 2]);
        }

        [Fact]
        public void ALidKeepsTheRimPointsWhenTheBodyIsNotTheLastBlock()
        {
            List<int> ring;
            Dictionary<int, double[]> point;
            Ring(false, out ring, out point);
            double[] facing;
            var lid = SurfaceCap.Lid(ring, point, out facing);
            var mesh = new MeshDefinition();
            for (int v = 0; v < 140; v++)
                AddPoint(mesh, new double[3], new[] { 1.0, 0.0, 0.0 }, 0, 0);
            var state = new BodyTessellator.FacetState
            {
                Mesh = mesh, BaseVertex = 0, VertexCount = 130, FaceOf = new int[130],
            };
            var moved = BodyTessellator.OwnPoints(state, lid, facing);
            Assert.Same(lid, moved);
            Assert.Equal(140, mesh.VertexCount);
        }

        private static void AddPoint(MeshDefinition mesh, double[] p, double[] n, double u, double v)
        {
            mesh.Positions.AddRange(p);
            mesh.Normals.AddRange(n);
            mesh.Uvs.Add(u);
            mesh.Uvs.Add(v);
        }

        private static double[] Cross(double[] a, double[] b, double[] c)
        {
            double ux = b[0] - a[0], uy = b[1] - a[1], uz = b[2] - a[2];
            double vx = c[0] - a[0], vy = c[1] - a[1], vz = c[2] - a[2];
            return new[] { uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx };
        }

        private static double Dot(double[] a, double[] b)
        {
            return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        }

        private static long Key(int a, int b)
        {
            return ((long)a << 32) | (uint)b;
        }
    }
}
