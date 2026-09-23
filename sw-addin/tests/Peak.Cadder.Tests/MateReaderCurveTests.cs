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
        public void TwoDSketchCurveIsLiftedThroughItsSketchFrame()
        {
            // A 2D sketch on a plane 20 mm off the part's YZ plane: sketch X
            // runs along model -Z, sketch Y along model Y, and the sketch
            // origin sits at x = 0.02. Its curve evaluates in sketch space.
            var sketchToModel = MathOps.GetTransformation(
                new[] { 0.02, 0.0, 0.0 }, new[] { 0.0, Math.PI / 2.0, 0.0 });
            // The part sits 1 m along the assembly's X.
            var part = MathOps.GetTransformation(new[] { 1.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 0.0 });

            var frame = MateReader.SketchLift(part, sketchToModel);
            var p = SwFrames.LiftPoint(frame, new[] { 0.1, 0.03, 0.0 });
            var expected = MathOps.TransformPoint(part,
                MathOps.TransformPoint(sketchToModel, new[] { 0.1, 0.03, 0.0 }));
            for (int i = 0; i < 3; i++) Assert.Equal(expected[i], p[i], 12);
            // The sketch point lies on the sketch plane, not on the XY plane.
            Assert.Equal(1.02, p[0], 12);

            // A recovered line direction turns with the sketch too.
            var dir = SwFrames.LiftDirection(frame, new[] { 1.0, 0.0, 0.0 });
            Assert.Equal(-1.0, dir[2], 12);
        }

        [Fact]
        public void ModelSpaceCurveKeepsTheComponentLift()
        {
            var part = MathOps.GetTransformation(new[] { 1.0, 0.0, 0.0 }, new[] { 0.0, 0.0, 0.3 });
            Assert.Same(part, MateReader.SketchLift(part, null));
            var sketch = MathOps.GetTransformation(new[] { 0.0, 0.5, 0.0 }, new[] { 0.0, 0.0, 0.0 });
            // An assembly-level 2D sketch at the top has no component lift.
            Assert.Same(sketch, MateReader.SketchLift(null, sketch));
            Assert.Null(MateReader.SketchLift(null, null));
        }

        private static List<double[]> Segment(params double[] xs)
        {
            var pts = new List<double[]>();
            foreach (var x in xs) pts.Add(new[] { x, 0.0, 0.0 });
            return pts;
        }

        private static double Length(List<double[]> chain)
        {
            double sum = 0;
            for (int i = 1; i < chain.Count; i++)
                sum += Math.Sqrt(MathOps.Distance2(chain[i - 1], chain[i]));
            return sum;
        }

        [Fact]
        public void ChainTurnsTheFirstSegmentWhenItsStartIsTheJoint()
        {
            // A runs 0 to 1, B runs 0 to -1: they meet at A's START. The
            // chain is B reversed then A, 2 m long, with no jump back.
            var chain = MateReader.ChainPolylines(new List<List<double[]>>
            {
                Segment(0.0, 0.5, 1.0),
                Segment(0.0, -0.5, -1.0),
            });
            Assert.Equal(5, chain.Count);
            Assert.Equal(2.0, Length(chain), 12);
            Assert.Equal(1.0, Math.Abs(chain[0][0]), 12);
            Assert.Equal(1.0, Math.Abs(chain[chain.Count - 1][0]), 12);
        }

        [Fact]
        public void ChainGrowsAtBothEndsFromAMiddleSegment()
        {
            // The middle segment comes first, and one piece joins each end.
            var chain = MateReader.ChainPolylines(new List<List<double[]>>
            {
                Segment(1.0, 2.0),
                Segment(3.0, 2.0),
                Segment(0.0, 1.0),
            });
            Assert.Equal(4, chain.Count);
            Assert.Equal(3.0, Length(chain), 12);
        }

        [Fact]
        public void ClosedLoopStartedMidPathClosesOnItself()
        {
            // A square of four edges, listed out of order and senses mixed.
            var a = new List<double[]> { new[] { 0.0, 0.0, 0.0 }, new[] { 1.0, 0.0, 0.0 } };
            var b = new List<double[]> { new[] { 1.0, 1.0, 0.0 }, new[] { 1.0, 0.0, 0.0 } };
            var c = new List<double[]> { new[] { 0.0, 1.0, 0.0 }, new[] { 1.0, 1.0, 0.0 } };
            var d = new List<double[]> { new[] { 0.0, 1.0, 0.0 }, new[] { 0.0, 0.0, 0.0 } };
            var chain = MateReader.ChainPolylines(new List<List<double[]>> { b, d, a, c });
            Assert.Equal(4.0, Length(chain), 12);
            Assert.True(MathOps.Distance2(chain[0], chain[chain.Count - 1]) < 1e-20);
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
