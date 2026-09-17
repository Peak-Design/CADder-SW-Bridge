using System;
using System.Collections.Generic;
using Peak.Cadder.Core;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The fill that replaces a planar face's triangles once its small holes
    /// are gone. Every case here checks the two things that make a fill
    /// usable rather than merely present: the triangles cover the polygon
    /// and nothing else, which the AREA says, and they all wind the same
    /// way, which the sign of each one says. A fill that covers the right
    /// area with a triangle folded back on itself renders black.
    /// </summary>
    public class PolygonFillTests
    {
        private static IList<double[]> Ring(params double[] xy)
        {
            var ring = new List<double[]>();
            for (int i = 0; i + 1 < xy.Length; i += 2)
                ring.Add(new[] { xy[i], xy[i + 1] });
            return ring;
        }

        private static IList<double[]> Circle(
            double cx, double cy, double r, int n, bool clockwise = false)
        {
            var ring = new List<double[]>();
            for (int i = 0; i < n; i++)
            {
                double t = 2.0 * Math.PI * i / n * (clockwise ? -1.0 : 1.0);
                ring.Add(new[] { cx + r * Math.Cos(t), cy + r * Math.Sin(t) });
            }
            return ring;
        }

        private static List<double[]> All(
            IList<double[]> outer, IList<IList<double[]>> holes)
        {
            var points = new List<double[]>(outer);
            foreach (var hole in holes ?? new List<IList<double[]>>())
                points.AddRange(hole);
            return points;
        }

        /// <summary>Total area of the triangles, and the smallest one's sign.
        /// A fill is right when the first equals the polygon's own area and
        /// the second is positive throughout.</summary>
        private static void Measure(
            List<int> tris, List<double[]> points, out double area, out double worstSign)
        {
            area = 0.0;
            worstSign = double.MaxValue;
            for (int i = 0; i < tris.Count; i += 3)
            {
                var a = points[tris[i]];
                var b = points[tris[i + 1]];
                var c = points[tris[i + 2]];
                double twice = (b[0] - a[0]) * (c[1] - a[1])
                             - (b[1] - a[1]) * (c[0] - a[0]);
                area += twice * 0.5;
                if (twice < worstSign) worstSign = twice;
            }
        }

        private static void Check(
            IList<double[]> outer, IList<IList<double[]>> holes, double expectedArea,
            int expectedTriangles = -1)
        {
            var tris = PolygonFill.Triangulate(outer, holes);
            Assert.NotNull(tris);
            Assert.Equal(0, tris.Count % 3);
            var points = All(outer, holes);
            double area, worstSign;
            Measure(tris, points, out area, out worstSign);
            Assert.Equal(expectedArea, area, 9);
            Assert.True(worstSign > 0.0,
                        "a triangle is wound the other way or has no area");
            if (expectedTriangles >= 0)
                Assert.Equal(expectedTriangles, tris.Count / 3);
        }

        [Fact]
        public void ASquareIsTwoTriangles()
        {
            Check(Ring(0, 0, 1, 0, 1, 1, 0, 1), null, 1.0, 2);
        }

        [Fact]
        public void ASquareWoundTheOtherWayIsStillTwoTrianglesTheRightWayRound()
        {
            // SolidWorks hands loops back in whichever order it likes.
            Check(Ring(0, 1, 1, 1, 1, 0, 0, 0), null, 1.0, 2);
        }

        [Fact]
        public void AnLShapeKeepsItsNotch()
        {
            // Concave: an ear clipper that ignores reflex corners fills the
            // notch in and comes out with 3 instead of 2.
            Check(Ring(0, 0, 2, 0, 2, 1, 1, 1, 1, 2, 0, 2), null, 3.0, 4);
        }

        [Fact]
        public void APlateWithOneSquareHoleLosesExactlyTheHole()
        {
            var holes = new List<IList<double[]>>
            {
                Ring(0.25, 0.25, 0.75, 0.25, 0.75, 0.75, 0.25, 0.75),
            };
            Check(Ring(0, 0, 2, 0, 2, 1, 0, 1), holes, 2.0 - 0.25);
        }

        [Fact]
        public void AHoleWoundEitherWayGivesTheSameArea()
        {
            var clockwise = new List<IList<double[]>>
            {
                Ring(0.75, 0.25, 0.25, 0.25, 0.25, 0.75, 0.75, 0.75),
            };
            Check(Ring(0, 0, 2, 0, 2, 1, 0, 1), clockwise, 2.0 - 0.25);
        }

        [Fact]
        public void APlateWithFourBoltHolesLosesFourBoltHoles()
        {
            var holes = new List<IList<double[]>>();
            double r = 0.1;
            foreach (var c in new[]
                     {
                         new[] { 0.5, 0.5 }, new[] { 1.5, 0.5 },
                         new[] { 2.5, 0.5 }, new[] { 3.5, 0.5 },
                     })
                holes.Add(Circle(c[0], c[1], r, 24));
            double hole = 4.0 * 24 * 0.5 * r * r * Math.Sin(2.0 * Math.PI / 24);
            Check(Ring(0, 0, 4, 0, 4, 1, 0, 1), holes, 4.0 - hole);
        }

        /// <summary>
        /// The count a polygon of P points with H holes comes to is
        /// P + 2H - 2. The survey reads that identity BACKWARDS, off a
        /// face's measured triangle count, to work out how many points the
        /// tessellator used. If the identity is wrong the survey is wrong,
        /// so it is checked here where it can be.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(3)]
        public void TheTriangleCountIsPointsPlusTwiceHolesLessTwo(int holeCount)
        {
            var holes = new List<IList<double[]>>();
            for (int i = 0; i < holeCount; i++)
                holes.Add(Circle(0.6 + i * 1.0, 0.5, 0.2, 8));
            var outer = Ring(0, 0, 4, 0, 4, 1, 0, 1);
            var tris = PolygonFill.Triangulate(outer, holes);
            Assert.NotNull(tris);
            int points = outer.Count + holeCount * 8;
            Assert.Equal(points + 2 * holeCount - 2, tris.Count / 3);
        }

        [Fact]
        public void AHoleTouchingNothingElseStillBridgesFromTheLeft()
        {
            // The bridge is cast LEFT from the hole's leftmost point, so a
            // hole hard against the right hand edge is the awkward one.
            var holes = new List<IList<double[]>> { Circle(3.7, 0.5, 0.25, 16) };
            double hole = 16 * 0.5 * 0.25 * 0.25 * Math.Sin(2.0 * Math.PI / 16);
            Check(Ring(0, 0, 4, 0, 4, 1, 0, 1), holes, 4.0 - hole);
        }

        [Fact]
        public void TwoHolesInLineWithEachOtherBothGetBridged()
        {
            // A bridge reaches LEFT, so the left hole reaches the outer
            // ring and the right one reaches the left hole. Taken the other
            // way round, the right hole has no clear run anywhere.
            var holes = new List<IList<double[]>>
            {
                Circle(1.0, 0.5, 0.2, 12),
                Circle(3.0, 0.5, 0.2, 12),
            };
            double hole = 2.0 * 12 * 0.5 * 0.2 * 0.2 * Math.Sin(2.0 * Math.PI / 12);
            Check(Ring(0, 0, 4, 0, 4, 1, 0, 1), holes, 4.0 - hole);
        }

        [Fact]
        public void ACollinearRunIsNotAPolygon()
        {
            Assert.Null(PolygonFill.Triangulate(Ring(0, 0, 1, 0, 2, 0), null));
        }

        [Fact]
        public void TooFewPointsIsRefusedRatherThanGuessed()
        {
            Assert.Null(PolygonFill.Triangulate(Ring(0, 0, 1, 0), null));
            Assert.Null(PolygonFill.Triangulate(null, null));
        }

        [Fact]
        public void APointRepeatedDoesNotStopTheFill()
        {
            // Two curve samples landing on the same spot is ordinary, and it
            // must not cost the face its fill.
            Check(Ring(0, 0, 1, 0, 1, 0, 1, 1, 0, 1), null, 1.0);
        }

        [Fact]
        public void AHoleTooSmallToBeARingIsIgnoredRatherThanRefused()
        {
            var holes = new List<IList<double[]>> { Ring(0.5, 0.5, 0.6, 0.5) };
            Check(Ring(0, 0, 1, 0, 1, 1, 0, 1), holes, 1.0, 2);
        }

        [Fact]
        public void AHoleInAConcaveFaceIsStillJustAHole()
        {
            // A bracket: an L with a bolt hole in the long arm.
            var holes = new List<IList<double[]>> { Circle(0.5, 0.5, 0.2, 16) };
            double hole = 16 * 0.5 * 0.2 * 0.2 * Math.Sin(2.0 * Math.PI / 16);
            Check(Ring(0, 0, 3, 0, 3, 1, 1, 1, 1, 3, 0, 3), holes, 5.0 - hole);
        }

        [Fact]
        public void TheSameFaceFillsTheSameWayTwice()
        {
            // An export that changes between runs makes every diff useless.
            var holes = new List<IList<double[]>>
            {
                Circle(1.0, 0.5, 0.2, 9),
                Circle(2.0, 0.5, 0.2, 9),
                Circle(3.0, 0.5, 0.2, 9),
            };
            var outer = Ring(0, 0, 4, 0, 4, 1, 0, 1);
            var first = PolygonFill.Triangulate(outer, holes);
            var again = PolygonFill.Triangulate(outer, holes);
            Assert.NotNull(first);
            Assert.Equal(first, again);
        }

        [Fact]
        public void AFaceTheSizeOfARealPlateFillsInOnePass()
        {
            // 50 bolt holes, the case the whole feature is for: the fill has
            // to come out right and it has to come out at all.
            var holes = new List<IList<double[]>>();
            for (int i = 0; i < 50; i++)
                holes.Add(Circle(0.05 + i * 0.02, 0.05, 0.005, 12));
            var outer = Ring(0, 0, 1.05, 0, 1.05, 0.1, 0, 0.1);
            var tris = PolygonFill.Triangulate(outer, holes);
            Assert.NotNull(tris);
            var points = All(outer, holes);
            double area, worstSign;
            Measure(tris, points, out area, out worstSign);
            double hole = 50 * 12 * 0.5 * 0.005 * 0.005 * Math.Sin(2.0 * Math.PI / 12);
            Assert.Equal(1.05 * 0.1 - hole, area, 9);
            Assert.True(worstSign > 0.0, "a triangle is wound the other way");
        }
    }
}
