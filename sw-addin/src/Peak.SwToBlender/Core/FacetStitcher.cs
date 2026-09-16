namespace Peak.SwToBlender.Core
{
    /// <summary>
    /// Turns SolidWorks' facet-and-fin tessellation into plain triangles.
    ///
    /// ITessellation does not hand out a facet's three corners. It hands out
    /// three FINS (half-edges) and each fin knows its two vertices, so the
    /// corners have to be recovered by walking the loop. The fins are ordered
    /// around the facet but their individual direction is not guaranteed,
    /// which is the whole difficulty: reading fin[0] forwards and assuming
    /// fin[1] starts where it ended is right most of the time and silently
    /// builds a bow-tie when it is not.
    /// </summary>
    public static class FacetStitcher
    {
        /// <summary>
        /// The three corners of one facet. Pairs are the three fins' vertex
        /// pairs, six ints. False means the fins do not form a closed
        /// triangle: a degenerate facet to skip, not to guess at.
        ///
        /// The corners come out in a consistent loop but not a guaranteed
        /// WINDING: when fin 0 itself is stored backwards the loop is walked
        /// the other way round. Nothing here can settle winding anyway, since
        /// a face may be reversed relative to its body. NeedsFlip does it
        /// afterwards, from the normals.
        /// </summary>
        public static bool TryStitch(int[] pairs, out int a, out int b, out int c)
        {
            a = b = c = -1;
            if (pairs == null || pairs.Length < 6) return false;

            int v0 = pairs[0], v1 = pairs[1];
            if (v0 == v1) return false;

            // The loop continues from wherever fin 0 ends...
            for (int f = 1; f < 3; f++)
            {
                int p = pairs[f * 2], q = pairs[f * 2 + 1];
                if (p == v1 && q != v0) { a = v0; b = v1; c = q; return true; }
                if (q == v1 && p != v0) { a = v0; b = v1; c = p; return true; }
            }
            // ...or, if fin 0 points the other way, from where it starts.
            for (int f = 1; f < 3; f++)
            {
                int p = pairs[f * 2], q = pairs[f * 2 + 1];
                if (p == v0 && q != v1) { a = v1; b = v0; c = q; return true; }
                if (q == v0 && p != v1) { a = v1; b = v0; c = p; return true; }
            }
            return false;
        }

        /// <summary>
        /// True when a triangle's winding disagrees with the surface it came
        /// from, judged against the vertex normals. A face can be reversed
        /// relative to its body, so facet order alone does not fix which way
        /// a triangle faces; the normals do, and they are the same normals
        /// the consumer will shade with.
        /// </summary>
        public static bool NeedsFlip(
            double[] p0, double[] p1, double[] p2, double[] normalSum)
        {
            double ux = p1[0] - p0[0], uy = p1[1] - p0[1], uz = p1[2] - p0[2];
            double vx = p2[0] - p0[0], vy = p2[1] - p0[1], vz = p2[2] - p0[2];
            double nx = uy * vz - uz * vy;
            double ny = uz * vx - ux * vz;
            double nz = ux * vy - uy * vx;
            double dot = nx * normalSum[0] + ny * normalSum[1] + nz * normalSum[2];
            // A sliver's own normal is noise; leaving it alone beats flipping
            // it on the strength of rounding.
            return dot < 0.0 && (nx * nx + ny * ny + nz * nz) > 1e-24;
        }
    }
}
