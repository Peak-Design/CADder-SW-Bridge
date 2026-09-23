using System;
using System.Collections.Generic;
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

        private static long Key(int a, int b)
        {
            return ((long)a << 32) | (uint)b;
        }
    }
}
