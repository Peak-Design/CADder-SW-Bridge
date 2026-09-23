using System;
using System.Collections.Generic;
using System.Linq;
using Peak.Cadder.Sw;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The points that bound one edge of a loop, which decide how wide the
    /// loop is and where its middle is. The width decides whether a feature
    /// counts as small, and the middle is how the rim is found again in the
    /// tessellated face. The edge here is a model: its curve, its own two
    /// ends, and whether a point lies on it, which is what SolidWorks tells
    /// the live code.
    /// </summary>
    public class SmallFeatureEdgeTests
    {
        private const double Tol = 1e-9;

        /// <summary>A straight edge. Its curve is the whole infinite line,
        /// as IEdge.GetCurve gives it.</summary>
        private static SmallFeatureSurvey.EdgeShape Line(double[] a, double[] b)
        {
            var d = new[] { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
            double length = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
            for (int k = 0; k < 3; k++) d[k] /= length;
            return new SmallFeatureSurvey.EdgeShape
            {
                IsLine = true,
                Start = a,
                End = b,
                CurveMin = -1e11,
                CurveMax = 1e11,
                Evaluate = t => new[] { a[0] + t * d[0], a[1] + t * d[1], a[2] + t * d[2] },
                OnEdge = q =>
                {
                    double t = (q[0] - a[0]) * d[0] + (q[1] - a[1]) * d[1] + (q[2] - a[2]) * d[2];
                    return t >= -Tol && t <= length + Tol;
                },
            };
        }

        /// <summary>An arc of the circle c + r (cos t u + sin t v), from t0
        /// to t1. A full circle is t0 = 0, t1 = 2 pi.</summary>
        private static SmallFeatureSurvey.EdgeShape Arc(
            double[] c, double[] u, double[] v, double r, double t0, double t1)
        {
            Func<double, double[]> at = t => new[]
            {
                c[0] + r * (Math.Cos(t) * u[0] + Math.Sin(t) * v[0]),
                c[1] + r * (Math.Cos(t) * u[1] + Math.Sin(t) * v[1]),
                c[2] + r * (Math.Cos(t) * u[2] + Math.Sin(t) * v[2]),
            };
            var n = new[]
            {
                u[1] * v[2] - u[2] * v[1],
                u[2] * v[0] - u[0] * v[2],
                u[0] * v[1] - u[1] * v[0],
            };
            return new SmallFeatureSurvey.EdgeShape
            {
                IsCircle = true,
                Circle = new[] { c[0], c[1], c[2], n[0], n[1], n[2], r },
                Start = at(t0),
                End = at(t1),
                CurveMin = 0.0,
                CurveMax = 2.0 * Math.PI,
                Evaluate = at,
                OnEdge = q =>
                {
                    double x = 0, y = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        x += (q[k] - c[k]) * u[k];
                        y += (q[k] - c[k]) * v[k];
                    }
                    double t = Math.Atan2(y, x);
                    while (t < t0 - 1e-9) t += 2.0 * Math.PI;
                    while (t > t0 + 2.0 * Math.PI) t -= 2.0 * Math.PI;
                    return t <= t1 + 1e-9;
                },
            };
        }

        private static readonly double[] X = { 1, 0, 0 };
        private static readonly double[] Y = { 0, 1, 0 };
        private static readonly double[] Z = { 0, 0, 1 };

        private static double[] Span(IEnumerable<SmallFeatureSurvey.EdgeShape> edges)
        {
            var points = edges.SelectMany(SmallFeatureSurvey.EdgePoints).ToList();
            Assert.NotEmpty(points);
            return new[]
            {
                points.Max(p => p[0]) - points.Min(p => p[0]),
                points.Max(p => p[1]) - points.Min(p => p[1]),
                points.Max(p => p[2]) - points.Min(p => p[2]),
            };
        }

        [Fact]
        public void AStraightEdgeIsBoundedByItsOwnEnds()
        {
            var span = Span(new[] { Line(new[] { 0.0, 0, 0 }, new[] { 0.008, 0, 0 }) });
            Assert.Equal(0.008, span[0], 9);
        }

        [Fact]
        public void ASmallRectangularCutoutCountsAsSmall()
        {
            var edges = new[]
            {
                Line(new[] { 0.0, 0, 0 }, new[] { 0.008, 0, 0 }),
                Line(new[] { 0.008, 0, 0 }, new[] { 0.008, 0.004, 0 }),
                Line(new[] { 0.008, 0.004, 0 }, new[] { 0.0, 0.004, 0 }),
                Line(new[] { 0.0, 0.004, 0 }, new[] { 0.0, 0, 0 }),
            };
            double extent;
            string key = SmallFeatureSurvey.LoopKey(
                edges.Length, edges.SelectMany(SmallFeatureSurvey.EdgePoints), out extent);
            Assert.NotNull(key);
            Assert.Equal(0.008, extent, 9);
            Assert.Contains("0.0040000,0.0020000,0.0000000", key);
        }

        [Fact]
        public void ACircleIsBoundedInItsOwnPlane()
        {
            // A hole drilled along X: the circle lies in the YZ plane.
            var span = Span(new[] { Arc(new[] { 0.1, 0.2, 0.3 }, Y, Z, 0.003, 0, 2 * Math.PI) });
            Assert.Equal(0.0, span[0], 9);
            Assert.Equal(0.006, span[1], 9);
            Assert.Equal(0.006, span[2], 9);
        }

        [Fact]
        public void ATiltedCircleHasItsTrueBox()
        {
            double s = Math.Sqrt(0.5);
            var v = new[] { 0.0, s, -s };
            var span = Span(new[] { Arc(new[] { 0.0, 0, 0 }, X, v, 0.003, 0, 2 * Math.PI) });
            Assert.Equal(0.006, span[0], 9);
            Assert.Equal(0.006 * s, span[1], 9);
            Assert.Equal(0.006 * s, span[2], 9);
        }

        [Fact]
        public void AnArcIsBoundedByItsOwnPartOfTheCircle()
        {
            // The upper half of a circle about Z.
            var span = Span(new[] { Arc(new[] { 0.0, 0, 0 }, X, Y, 0.003, 0, Math.PI) });
            Assert.Equal(0.006, span[0], 9);
            Assert.Equal(0.003, span[1], 9);
            Assert.Equal(0.0, span[2], 9);
        }

        [Fact]
        public void ASlotAlongZHasItsTrueLength()
        {
            // A slot on a face normal to Y, arc centres 10 mm apart, r 3 mm:
            // 16 mm long and 6 mm wide, its middle at z = 5 mm.
            var edges = new[]
            {
                Arc(new[] { 0.0, 0, 0.010 }, X, Z, 0.003, 0, Math.PI),
                Line(new[] { -0.003, 0, 0.010 }, new[] { -0.003, 0, 0.0 }),
                Arc(new[] { 0.0, 0, 0.0 }, X, Z, 0.003, Math.PI, 2 * Math.PI),
                Line(new[] { 0.003, 0, 0.0 }, new[] { 0.003, 0, 0.010 }),
            };
            double extent;
            string key = SmallFeatureSurvey.LoopKey(
                edges.Length, edges.SelectMany(SmallFeatureSurvey.EdgePoints), out extent);
            Assert.Equal(0.016, extent, 9);
            Assert.Contains("0.0000000,0.0000000,0.0050000", key);
            var span = Span(edges);
            Assert.Equal(0.006, span[0], 9);
            Assert.Equal(0.0, span[1], 9);
        }
    }
}
