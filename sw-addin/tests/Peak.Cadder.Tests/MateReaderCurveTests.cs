using System;
using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Sw;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// How MateReader turns a path mate's curves into a polyline. The
    /// curves come from COM, so these tests feed the pure halves the same
    /// numbers SolidWorks gives: a curve as a function of its parameter,
    /// and an edge's span of it as ICurveParamData states it.
    /// </summary>
    public class MateReaderCurveTests
    {
        private const double Radius = 0.05;

        private static double[] Circle(double t)
        {
            return new[] { Radius * Math.Cos(t), Radius * Math.Sin(t), 0.0 };
        }

        [Fact]
        public void ArcEdgeIsSampledOverItsOwnSpan()
        {
            // An arc edge from t = 0.2 to t = 1.0 on its underlying circle.
            double s, e;
            bool matched;
            Assert.True(MateReader.EdgeSpan(0.2, 1.0, true, Circle(0.2), Circle(1.0), Circle,
                out s, out e, out matched));
            Assert.True(matched);
            Assert.Equal(0.2, s, 12);
            Assert.Equal(1.0, e, 12);

            var pts = MateReader.SampleSpan(Circle, s, e, null);
            Assert.NotNull(pts);
            foreach (var p in pts)
            {
                double angle = Math.Atan2(p[1], p[0]);
                Assert.InRange(angle, 0.2 - 1e-9, 1.0 + 1e-9);
            }
            Assert.Equal(Circle(0.2)[0], pts[0][0], 12);
            Assert.Equal(Circle(1.0)[1], pts[pts.Count - 1][1], 12);
        }

        [Fact]
        public void ReversedEdgeUsesTheNegatedSpan()
        {
            // The API help: an edge that runs against its curve from 10 to 5
            // reads UMin -10 and UMax -5. Here the edge runs from t = 1.0
            // back to t = 0.2. StartPoint is the curve-order start.
            double s, e;
            bool matched;
            Assert.True(MateReader.EdgeSpan(-1.0, -0.2, false, Circle(0.2), Circle(1.0), Circle,
                out s, out e, out matched));
            Assert.True(matched);
            Assert.Equal(0.2, s, 12);
            Assert.Equal(1.0, e, 12);
        }

        [Fact]
        public void ReversedEdgeWithPlainValuesIsCaughtByItsEndPoints()
        {
            // The same edge if SolidWorks did not negate: the end points
            // show which reading is the edge.
            double s, e;
            bool matched;
            Assert.True(MateReader.EdgeSpan(0.2, 1.0, false, Circle(1.0), Circle(0.2), Circle,
                out s, out e, out matched));
            Assert.True(matched);
            Assert.Equal(0.2, s, 12);
            Assert.Equal(1.0, e, 12);
        }

        [Fact]
        public void StraightEdgeHasABoundedSpan()
        {
            // A line's own range is unbounded, so a straight edge used to be
            // skipped. Its span is the edge.
            Func<double, double[]> line = t => new[] { 0.1 + t, 0.02, 0.0 };
            double s, e;
            bool matched;
            Assert.True(MateReader.EdgeSpan(0.0, 0.04, true, line(0.0), line(0.04), line,
                out s, out e, out matched));
            var pts = MateReader.SampleSpan(line, s, e, null);
            Assert.Equal(0.1, pts[0][0], 12);
            Assert.Equal(0.14, pts[pts.Count - 1][0], 12);
        }

        [Fact]
        public void MissingEndPointsTakeTheDocumentedSpan()
        {
            double s, e;
            bool matched;
            Assert.True(MateReader.EdgeSpan(-1.0, -0.2, false, null, null, Circle,
                out s, out e, out matched));
            Assert.False(matched);
            Assert.Equal(0.2, s, 12);
            Assert.Equal(1.0, e, 12);

            Assert.False(MateReader.EdgeSpan(double.NaN, 1.0, true, null, null, Circle,
                out s, out e, out matched));
            Assert.False(MateReader.EdgeSpan(1.0, 1.0, true, null, null, Circle,
                out s, out e, out matched));
        }

        [Fact]
        public void SampledArcHoldsTheChordTolerance()
        {
            var pts = MateReader.SampleSpan(Circle, 0.0, Math.PI, null);
            for (int i = 1; i < pts.Count; i++)
            {
                // The sagitta of each chord on a circle of this radius.
                double chord = Math.Sqrt(MathOps.Distance2(pts[i - 1], pts[i]));
                double sag = Radius - Math.Sqrt(Radius * Radius - chord * chord / 4.0);
                Assert.True(sag <= 1e-5 + 1e-12, "sag " + sag);
            }
        }
    }
}
