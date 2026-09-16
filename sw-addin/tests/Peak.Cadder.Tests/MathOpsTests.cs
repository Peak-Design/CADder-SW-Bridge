using System;
using Peak.Cadder.Core;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// Ported from sw-addin/vendor/sw2urdf/TestMathOps.cs for the functions
    /// that survived the adaptation (Max/Min/Envelope/PNorm and the
    /// MathTransform overloads did not). Upstream fed MathNet DenseMatrix with
    /// column-major arrays and indexed it [row, col]; the ports keep the same
    /// data by filling double[4,4] column-major, so the expected values stay
    /// byte-identical to upstream's.
    /// </summary>
    public class MathOpsTests
    {
        private const double Tol = 1e-10;

        private static double[,] FromColumnMajor(double[] data)
        {
            var m = new double[4, 4];
            for (int c = 0; c < 4; c++)
                for (int r = 0; r < 4; r++)
                    m[r, c] = data[c * 4 + r];
            return m;
        }

        private static double[] ToColumnMajor(double[,] m)
        {
            var data = new double[16];
            for (int c = 0; c < 4; c++)
                for (int r = 0; r < 4; r++)
                    data[c * 4 + r] = m[r, c];
            return data;
        }

        private static void AssertVector(double[] expected, double[] actual)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], actual[i], Tol);
        }

        [Theory]
        [InlineData(new double[] { 0.0, 0.0 }, new double[] { 1.0, 0.0 }, new double[] { 0.0, 0.0 }, new double[] { 0.0, 0.0 })]
        [InlineData(new double[] { 0.0, 0.0 }, new double[] { 0.0, 1.0 }, new double[] { 0.0, 0.0 }, new double[] { 0.0, 0.0 })]
        [InlineData(new double[] { 1.0, 0.0 }, new double[] { 1.0, 0.0 }, new double[] { 0.0, 0.0 }, new double[] { 1.0, 0.0 })]
        [InlineData(new double[] { 1.0, 0.0 }, new double[] { 0.0, 1.0 }, new double[] { 0.0, 0.0 }, new double[] { 0.0, 0.0 })]
        [InlineData(new double[] { 1.0, 1.0 }, new double[] { 1.0, 0.0 }, new double[] { 0.0, 0.0 }, new double[] { 1.0, 0.0 })]
        [InlineData(new double[] { 1.0, 1.0 }, new double[] { 0.0, 1.0 }, new double[] { 0.0, 0.0 }, new double[] { 0.0, 1.0 })]
        public void ClosestPointOnLineToPoint(double[] point, double[] line, double[] pointOnLine, double[] expected)
        {
            AssertVector(expected, MathOps.ClosestPointOnLineToPoint(point, line, pointOnLine));
        }

        [Theory]
        [InlineData(0.0, 1.0, 0.0, 1.0, 0.0, 1.0,
            new double[] { 1.0, 1.0, 1.0 }, new double[] { 0.0, 0.0, 0.0 }, new double[] { 0.0, 0.0, 0.0 })]
        [InlineData(0.0, 1.0, 0.0, 1.0, 0.0, 1.0,
            new double[] { 1.0, 1.0, 1.0 }, new double[] { 1.0, 1.0, 1.0 }, new double[] { 1.0, 1.0, 1.0 })]
        public void ClosestPointOnLineWithinBox(
            double xMin, double xMax, double yMin, double yMax, double zMin, double zMax,
            double[] line, double[] pointOnLine, double[] expected)
        {
            AssertVector(expected, MathOps.ClosestPointOnLineWithinBox(
                xMin, xMax, yMin, yMax, zMin, zMax, line, pointOnLine));
        }

        [Theory]
        [InlineData(
            new double[] { 0.0, 1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0, 11.0, 12.0, 13.0, 14.0, 15.0 },
            new double[] { 12.0, 13.0, 14.0 })]
        public void GetXYZ(double[] columnMajor, double[] expected)
        {
            AssertVector(expected, MathOps.GetXYZ(FromColumnMajor(columnMajor)));
        }

        [Theory]
        [InlineData(
            new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
            new double[] { 0, 0, 0 })]
        [InlineData(
            new double[] { 1, 0, 0, 0, 0, 0, 1, 0, -1, 0, 0, 0, 0, 0, 0, 1 },
            new double[] { Math.PI / 2.0, 0, 0 })]
        [InlineData(
            new double[] { 0, 0, -1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1 },
            new double[] { 0, Math.PI / 2.0, 0 })]
        [InlineData(
            new double[] { 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
            new double[] { 0, 0, Math.PI / 2.0 })]
        [InlineData(
            new double[] { 0, 0, 1, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 0, 1 },
            new double[] { 0, -Math.PI / 2.0, 0 })]
        [InlineData(
            new double[] { 0, 0, -1, 0, -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1 },
            new double[] { -Math.PI / 2.0, Math.PI / 2.0, 0 })]
        [InlineData(
            new double[] { 0, 0, 1, 0, 0.7071067811865476, 0.7071067811865476, 0, 0, -0.7071067811865476, 0.7071067811865476, 0, 0, 0, 0, 0, 1 },
            new double[] { -Math.PI / 4.0, -Math.PI / 2.0, 0 })]
        [InlineData(
            new double[] { 0, 0, 1, 0, -0.7071067811865476, -0.7071067811865476, 0, 0, 0.7071067811865476, -0.7071067811865476, 0, 0, 0, 0, 0, 1 },
            new double[] { 3 * Math.PI / 4.0, -Math.PI / 2.0, 0 })]
        public void GetRPY(double[] columnMajor, double[] expected)
        {
            AssertVector(expected, MathOps.GetRPY(FromColumnMajor(columnMajor)));
        }

        [Theory]
        [InlineData(
            new double[] { 0, 0, 0 },
            new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 })]
        [InlineData(
            new double[] { Math.PI / 2.0, 0, 0 },
            new double[] { 1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1 })]
        [InlineData(
            new double[] { 0, Math.PI / 2.0, 0 },
            new double[] { 0, 0, -1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1 })]
        [InlineData(
            new double[] { 0, 0, Math.PI / 2.0 },
            new double[] { 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 })]
        [InlineData(
            new double[] { 0, -Math.PI / 2.0, 0 },
            new double[] { 0, 0, 1, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 0, 1 })]
        [InlineData(
            new double[] { -Math.PI / 2.0, Math.PI / 2.0, 0 },
            new double[] { 0, 0, -1, 0, -1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1 })]
        [InlineData(
            new double[] { -Math.PI / 4.0, -Math.PI / 2.0, 0 },
            new double[] { 0, 0, 1, 0, 0.7071067811865476, 0.7071067811865476, 0, 0, -0.7071067811865476, 0.7071067811865476, 0, 0, 0, 0, 0, 1 })]
        [InlineData(
            new double[] { 3 * Math.PI / 4.0, -Math.PI / 2.0, 0 },
            new double[] { 0, 0, 1, 0, -0.7071067811865476, -0.7071067811865476, 0, 0, 0.7071067811865476, -0.7071067811865476, 0, 0, 0, 0, 0, 1 })]
        public void GetRotation(double[] rpy, double[] expectedColumnMajor)
        {
            AssertVector(expectedColumnMajor, ToColumnMajor(MathOps.GetRotation(rpy)));
        }

        /// <summary>The round trip both ways over the upstream case data. Yaw
        /// is zero in every gimbal-locked case, which is exactly the branch
        /// GetRPY collapses to, so the trip is exact for all of them.</summary>
        [Theory]
        [InlineData(new double[] { 0, 0, 0 })]
        [InlineData(new double[] { Math.PI / 2.0, 0, 0 })]
        [InlineData(new double[] { 0, Math.PI / 2.0, 0 })]
        [InlineData(new double[] { 0, 0, Math.PI / 2.0 })]
        [InlineData(new double[] { 0, -Math.PI / 2.0, 0 })]
        [InlineData(new double[] { -Math.PI / 2.0, Math.PI / 2.0, 0 })]
        [InlineData(new double[] { -Math.PI / 4.0, -Math.PI / 2.0, 0 })]
        [InlineData(new double[] { 3 * Math.PI / 4.0, -Math.PI / 2.0, 0 })]
        [InlineData(new double[] { 0.3, -0.4, 0.5 })]
        public void RpyRotationRoundTrip(double[] rpy)
        {
            var back = MathOps.GetRPY(MathOps.GetRotation(rpy));
            AssertVector(rpy, back);

            var m = MathOps.GetRotation(rpy);
            var again = MathOps.GetRotation(MathOps.GetRPY(m));
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    Assert.Equal(m[r, c], again[r, c], Tol);
        }

        [Theory]
        [InlineData(
            new double[] { 0, 0, 0 }, new double[] { 0.0, 0.0, 0.0 },
            new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 })]
        [InlineData(
            new double[] { 1, 2, 3 }, new double[] { 0.0, 0.0, 0.0 },
            new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 1, 2, 3, 1 })]
        [InlineData(
            new double[] { 0, 0, 0 }, new double[] { Math.PI / 2.0, 0, 0 },
            new double[] { 1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1 })]
        [InlineData(
            new double[] { 0, 0, 0 }, new double[] { 0.0, Math.PI / 2.0, 0 },
            new double[] { 0, 0, -1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1 })]
        [InlineData(
            new double[] { 0, 0, 0 }, new double[] { 0.0, 0.0, Math.PI / 2.0 },
            new double[] { 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 })]
        public void GetTransformation(double[] xyz, double[] rpy, double[] expectedColumnMajor)
        {
            AssertVector(expectedColumnMajor, ToColumnMajor(MathOps.GetTransformation(xyz, rpy)));
        }

        [Theory]
        [InlineData(new double[] { 1.0, 0 }, new double[] { 1.0, 0 }, 0)]
        [InlineData(new double[] { 1.0, 0 }, new double[] { 0, 1.0 }, 2)]
        [InlineData(new double[] { 0.0, 0 }, new double[] { 1.0, 1.0 }, 2)]
        [InlineData(new double[] { 1.0, 2.0, 3.0 }, new double[] { 3.0, 2.0, 1.0 }, 8)]
        public void Distance2(double[] a, double[] b, double expected)
        {
            Assert.Equal(expected, MathOps.Distance2(a, b));
        }

        [Theory]
        [InlineData(new double[] { 0.1, 1, 10.0 }, 0.5, new double[] { 0, 1, 10 })]
        [InlineData(new double[] { 0.1, 1, 10.0 }, 0.1, new double[] { 0.1, 1, 10 })]
        [InlineData(new double[] { 0.1, 0.01, 0.001 }, 0.5, new double[] { 0.0, 0.0, 0.0 })]
        [InlineData(new double[] { 0.1, 0.01, 0.001 }, 0.1, new double[] { 0.1, 0.0, 0.0 })]
        public void Threshold(double[] array, double minValue, double[] expected)
        {
            var result = MathOps.Threshold(array, minValue);
            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], result[i]);
        }

        [Fact]
        public void NormalizedZeroVectorStaysZero()
        {
            AssertVector(new double[] { 0, 0, 0 }, MathOps.Normalized(new double[] { 0, 0, 0 }));
        }

        [Fact]
        public void InvertRigidRoundTrip()
        {
            var m = MathOps.GetTransformation(new double[] { 1, 2, 3 }, new double[] { 0.3, -0.4, 0.5 });
            var product = MathOps.Multiply(MathOps.InvertRigid(m), m);
            var identity = MathOps.Identity4();
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    Assert.Equal(identity[r, c], product[r, c], Tol);
        }

        // ── Mirror-plane recovery ───────────────────────────────────────
        // Mirrored components carry no mate, so the symmetry has to be read
        // off the two placements. These pin the recovery and, just as
        // importantly, the refusals.

        private static double[,] Placement(double[] rpy, double[] xyz)
        {
            return MathOps.GetTransformation(xyz, rpy);
        }

        private static double[,] ReflectAcross(double[] n, double[] p, double[,] m)
        {
            var s = MathOps.Identity4();
            double along = MathOps.Dot(p, n);
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                    s[r, c] = (r == c ? 1.0 : 0.0) - 2.0 * n[r] * n[c];
                s[r, 3] = 2.0 * along * n[r];
            }
            return MathOps.Multiply(s, m);
        }

        [Theory]
        [InlineData(1.0, 0.0, 0.0, 0.05)]      // the sym4 plane
        [InlineData(0.0, 1.0, 0.0, -0.2)]
        [InlineData(0.6, 0.8, 0.0, 0.13)]      // oblique, through no axis
        [InlineData(0.0, 0.0, 1.0, 0.0)]       // through the origin
        public void RecoversThePlaneAMirroredPlacementWasMadeAcross(
            double nx, double ny, double nz, double offset)
        {
            var n = MathOps.Normalized(new[] { nx, ny, nz });
            var p = new[] { n[0] * offset, n[1] * offset, n[2] * offset };
            var source = Placement(new[] { 0.3, -0.7, 1.1 }, new[] { 0.4, 0.2, -0.1 });
            var mirrored = ReflectAcross(n, p, source);

            double[] gotPoint, gotNormal;
            Assert.True(MathOps.TryReflectionPlane(
                source, mirrored, out gotPoint, out gotNormal));

            // The normal is recovered up to sign; the PLANE is what matters,
            // so compare the plane both describe.
            double sign = MathOps.Dot(gotNormal, n) < 0 ? -1.0 : 1.0;
            for (int i = 0; i < 3; i++)
                Assert.Equal(n[i], gotNormal[i] * sign, 9);
            Assert.Equal(offset, MathOps.Dot(gotPoint, gotNormal) * sign, 9);
        }

        [Fact]
        public void RefusesTwoPlacementsThatAreNotMirrorImages()
        {
            var source = Placement(new[] { 0.3, -0.7, 1.1 }, new[] { 0.4, 0.2, -0.1 });
            double[] point, normal;

            // A plain rotation: proper, so no reflection plane exists.
            var rotated = Placement(new[] { 0.9, 0.1, 0.2 }, new[] { 0.4, 0.2, -0.1 });
            Assert.False(MathOps.TryReflectionPlane(source, rotated, out point, out normal));

            // The same placement: S is the identity, which fixes every plane
            // and therefore names none.
            Assert.False(MathOps.TryReflectionPlane(source, source, out point, out normal));

            // A HALF TURN is symmetric and squares to the identity like a
            // reflection does, and is told apart only by its determinant:
            // the case a looser test would wave through.
            var halfTurn = MathOps.Multiply(
                MathOps.GetTransformation(new double[] { 0, 0, 0 },
                                          new[] { 0.0, 0.0, Math.PI }),
                source);
            Assert.False(MathOps.TryReflectionPlane(source, halfTurn, out point, out normal));
        }

        [Fact]
        public void RefusesAMirroredPlacementThatWasThenDraggedAway()
        {
            // A mirrored instance is only a reflection of its source until
            // someone moves it. Inventing a symmetry for one that has been
            // dragged would hold a body SolidWorks leaves free.
            var n = new double[] { 1, 0, 0 };
            var source = Placement(new[] { 0.2, 0.1, 0.0 }, new[] { 0.3, 0.0, 0.0 });
            var mirrored = ReflectAcross(n, new double[] { 0.05, 0, 0 }, source);
            mirrored[1, 3] += 0.02;      // nudged 20 mm out of symmetry

            double[] point, normal;
            Assert.False(MathOps.TryReflectionPlane(
                source, mirrored, out point, out normal));
        }
    }
}
