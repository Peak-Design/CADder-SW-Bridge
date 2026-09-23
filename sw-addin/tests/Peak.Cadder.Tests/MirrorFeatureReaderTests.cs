using System;
using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Peak.Cadder.Sw;
using Xunit;
using Occ = Peak.Cadder.Sw.MirrorFeatureReader.MirrorOccurrence;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// Which walked occurrences a mirror component feature made. SolidWorks
    /// places every component by a PROPER rigid transform: a mirrored copy
    /// of a part is the same part turned into one of four orientations
    /// (IMirrorComponentFeatureData), and an opposite-hand version is a new,
    /// already mirrored part. So the instance is never the reflection of
    /// its source's placement. It is the reflection composed with a local
    /// mirror of the part: B = R x A x L. The transforms below are built
    /// that way, which is how SolidWorks gives them.
    /// </summary>
    public class MirrorFeatureReaderTests
    {
        private static readonly double[] Normal = { 1.0, 0.0, 0.0 };
        private static readonly double[] PlanePoint = { 0.05, 0.0, 0.0 };

        private static double[,] Placement(double[] xyz, double[] rpy)
        {
            return MathOps.GetTransformation(xyz, rpy);
        }

        private static double[,] Reflection(double[] n, double[] p)
        {
            var r = MathOps.Identity4();
            double along = MathOps.Dot(n, p);
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                    r[i, j] = (i == j ? 1.0 : 0.0) - 2.0 * n[i] * n[j];
                r[i, 3] = 2.0 * along * n[i];
            }
            return r;
        }

        /// <summary>A local mirror: a sign on each local axis, and a
        /// translation that keeps it an involution.</summary>
        private static double[,] Local(double sx, double sy, double sz, double[] t = null)
        {
            var l = MathOps.Identity4();
            l[0, 0] = sx; l[1, 1] = sy; l[2, 2] = sz;
            if (t != null) { l[0, 3] = t[0]; l[1, 3] = t[1]; l[2, 3] = t[2]; }
            return l;
        }

        private static double[,] Instance(double[,] source, double[,] local)
        {
            return MathOps.Multiply(Reflection(Normal, PlanePoint), MathOps.Multiply(source, local));
        }

        private static double Det(double[,] m)
        {
            return m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
                - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
                + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        }

        private static Occ Occurrence(string id, string file, double[,] t, bool oppositeHand = false)
        {
            return new Occ { Id = id, File = file, Transform = t, OppositeHand = oppositeHand };
        }

        private static List<double[][]> FeaturePlane()
        {
            return new List<double[][]> { new[] { PlanePoint, Normal } };
        }

        private static void AssertPlane(GraphMirrorPair pair)
        {
            double sign = MathOps.Dot(pair.PlaneNormal, Normal) < 0 ? -1.0 : 1.0;
            for (int i = 0; i < 3; i++) Assert.Equal(Normal[i], pair.PlaneNormal[i] * sign, 9);
            Assert.Equal(MathOps.Dot(PlanePoint, Normal), MathOps.Dot(pair.PlanePoint, Normal), 9);
        }

        public static IEnumerable<object[]> Orientations()
        {
            // swMirrorComponentOrientation2_e: mirrored X and Y (the local Z
            // flips), a flipped X, a flipped Y, and both flipped (the part
            // turns inside out through a point).
            yield return new object[] { 1.0, 1.0, -1.0 };
            yield return new object[] { -1.0, 1.0, 1.0 };
            yield return new object[] { 1.0, -1.0, 1.0 };
            yield return new object[] { -1.0, -1.0, -1.0 };
        }

        [Theory]
        [MemberData(nameof(Orientations))]
        public void CopyPlacedByAProperTransformPairs(double sx, double sy, double sz)
        {
            var a = Placement(new[] { 0.3, -0.2, 0.1 }, new[] { 0.4, 0.2, -0.1 });
            var b = Instance(a, Local(sx, sy, sz));
            Assert.True(Det(b) > 0.99);    // what SolidWorks gives

            var pairs = MirrorFeatureReader.Pair("MirrorComponent1",
                new[] { Occurrence("c001", "arm.sldprt", a) },
                new[] { Occurrence("c001", "arm.sldprt", a), Occurrence("c002", "arm.sldprt", b) },
                FeaturePlane());

            var pair = Assert.Single(pairs);
            Assert.Equal("c001", pair.SourceComponentId);
            Assert.Equal("c002", pair.MirroredComponentId);
            AssertPlane(pair);
        }

        [Fact]
        public void SymmetricPartPlacedByItsBoxCentrePairs()
        {
            // The part is symmetric about its local plane z = 0.02, and the
            // mirror type puts the box centres, not the origins, in mirror.
            var a = Placement(new[] { 0.3, -0.2, 0.1 }, new[] { 0.0, 0.0, 0.7 });
            var b = Instance(a, Local(1, 1, -1, new[] { 0.0, 0.0, 0.04 }));
            var pairs = MirrorFeatureReader.Pair("MirrorComponent1",
                new[] { Occurrence("c001", "bracket.sldprt", a) },
                new[] { Occurrence("c002", "bracket.sldprt", b) },
                FeaturePlane());
            AssertPlane(Assert.Single(pairs));
        }

        [Fact]
        public void OppositeHandVersionIsANewDocument()
        {
            var a = Placement(new[] { 0.3, 0.1, 0.0 }, new[] { 0.0, 0.3, 0.0 });
            var b = Instance(a, Local(-1, 1, 1));

            var pairs = MirrorFeatureReader.Pair("MirrorComponent1",
                new[] { Occurrence("c001", "lever-lh.sldprt", a, oppositeHand: true) },
                new[] { Occurrence("c002", "lever-rh.sldprt", b) },
                FeaturePlane());
            Assert.Equal("c002", Assert.Single(pairs).MirroredComponentId);

            // A copy is the same document: another part in mirror is not it.
            Assert.Empty(MirrorFeatureReader.Pair("MirrorComponent1",
                new[] { Occurrence("c001", "lever-lh.sldprt", a) },
                new[] { Occurrence("c002", "lever-rh.sldprt", b) },
                FeaturePlane()));
        }

        [Fact]
        public void DraggedInstanceDoesNotPair()
        {
            var a = Placement(new[] { 0.2, 0.1, 0.0 }, new[] { 0.3, 0.0, 0.0 });
            var b = Instance(a, Local(1, 1, -1));
            b[1, 3] += 0.02;    // 20 mm out of symmetry, along the plane
            Assert.Empty(MirrorFeatureReader.Pair("MirrorComponent1",
                new[] { Occurrence("c001", "arm.sldprt", a) },
                new[] { Occurrence("c002", "arm.sldprt", b) },
                FeaturePlane()));
        }

        [Fact]
        public void InsideOutCopyAwayFromTheMirrorDoesNotPair()
        {
            // A point inversion is an involution wherever it sits, so the
            // placement alone cannot place it. It pairs only where the
            // origins or the boxes are in mirror.
            var a = Placement(new[] { 0.2, 0.1, 0.0 }, new[] { 0.3, 0.0, 0.0 });
            var mirrored = Instance(a, Local(-1, -1, -1));
            var away = Instance(a, Local(-1, -1, -1, new[] { 0.1, 0.05, 0.0 }));

            Assert.Empty(MirrorFeatureReader.Pair("MirrorComponent1",
                new[] { Occurrence("c001", "nut.sldprt", a) },
                new[] { Occurrence("c002", "nut.sldprt", away) },
                FeaturePlane()));
            var pairs = MirrorFeatureReader.Pair("MirrorComponent1",
                new[] { Occurrence("c001", "nut.sldprt", a) },
                new[] { Occurrence("c002", "nut.sldprt", away), Occurrence("c003", "nut.sldprt", mirrored) },
                FeaturePlane());
            Assert.Equal("c003", Assert.Single(pairs).MirroredComponentId);
        }

        [Fact]
        public void EachInstanceBelongsToOneSource()
        {
            // Two sources of one document, each with its own instance.
            var a1 = Placement(new[] { 0.2, 0.1, 0.0 }, new[] { 0.3, 0.0, 0.0 });
            var a2 = Placement(new[] { 0.2, -0.3, 0.1 }, new[] { 0.0, 0.5, 0.0 });
            var b1 = Instance(a1, Local(1, 1, -1));
            var b2 = Instance(a2, Local(1, 1, -1));
            var pairs = MirrorFeatureReader.Pair("MirrorComponent1",
                new[] { Occurrence("c001", "pin.sldprt", a1), Occurrence("c002", "pin.sldprt", a2) },
                new[]
                {
                    Occurrence("c001", "pin.sldprt", a1), Occurrence("c002", "pin.sldprt", a2),
                    Occurrence("c003", "pin.sldprt", b1), Occurrence("c004", "pin.sldprt", b2),
                },
                FeaturePlane());
            Assert.Equal(2, pairs.Count);
            Assert.Contains(pairs, p => p.SourceComponentId == "c001" && p.MirroredComponentId == "c003");
            Assert.Contains(pairs, p => p.SourceComponentId == "c002" && p.MirroredComponentId == "c004");
        }

        [Fact]
        public void UnreadPlaneIsFoundFromOriginPlacements()
        {
            var a = Placement(new[] { 0.3, -0.2, 0.1 }, new[] { 0.4, 0.2, -0.1 });
            var b = Instance(a, Local(1, -1, 1));
            var pairs = MirrorFeatureReader.Pair("MirrorComponent1",
                new[] { Occurrence("c001", "arm.sldprt", a) },
                new[] { Occurrence("c002", "arm.sldprt", b) },
                null);
            AssertPlane(Assert.Single(pairs));
        }

        [Fact]
        public void ThePlaneReadingTheInstancesConfirmWins()
        {
            // A component's plane read in the wrong frame is another plane.
            // The one the placements agree with is the feature's.
            var a = Placement(new[] { 0.3, -0.2, 0.1 }, new[] { 0.4, 0.2, -0.1 });
            var b = Instance(a, Local(1, 1, -1));
            var readings = new List<double[][]>
            {
                new[] { new[] { -0.05, 0.0, 0.0 }, Normal },
                new[] { PlanePoint, Normal },
            };
            var pairs = MirrorFeatureReader.Pair("MirrorComponent1",
                new[] { Occurrence("c001", "arm.sldprt", a) },
                new[] { Occurrence("c002", "arm.sldprt", b) },
                readings);
            AssertPlane(Assert.Single(pairs));
        }
    }
}
