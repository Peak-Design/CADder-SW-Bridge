using System;
using Peak.Cadder.Core;
using Peak.Cadder.Sw;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The frames MateReader lifts geometry through. SolidWorks gives an
    /// edge's curve, a sketch curve and a face's triangles in the frame of
    /// the part that owns them, but the walk only knows the components it
    /// walked, and a part inside a rigid subassembly is never walked. These
    /// tests pin the matrix side of that lift, which needs no SolidWorks.
    /// </summary>
    public class MateReaderFrameTests
    {
        private const double Tol = 1e-9;

        private static double[,] Pose(double x, double y, double z, double turnZDegrees)
        {
            var m = MathOps.GetTransformation(
                new[] { x, y, z }, new[] { 0.0, 0.0, turnZDegrees * Math.PI / 180.0 });
            return m;
        }

        private static void AssertPoint(double[] expected, double[] actual)
        {
            for (int i = 0; i < 3; i++) Assert.Equal(expected[i], actual[i], Tol);
        }

        [Fact]
        public void PartInsideARigidSubassemblyLiftsByItsOwnPlace()
        {
            // Rigid Slide-1 sits at x = 1 m, turned 30 degrees. Rail-1 sits
            // inside it, 200 mm along the sub's X and turned 90 degrees. The
            // walk reaches only Slide-1, so its transform is what
            // ResolveWalked hands back for Rail-1's edge.
            var slide = Pose(1.0, 0.0, 0.0, 30.0);
            var railInSlide = Pose(0.2, 0.0, 0.0, 90.0);
            var rail = MathOps.Multiply(slide, railInSlide);

            // Top context: every Transform2 is root-relative.
            var lift = MateReader.PartLift(slide, slide, rail);

            // The rail's own X axis, and a point 50 mm along it.
            var axis = SwFrames.LiftDirection(lift, new[] { 1.0, 0.0, 0.0 });
            AssertPoint(MathOps.RotateVector(rail, new[] { 1.0, 0.0, 0.0 }), axis);
            var point = SwFrames.LiftPoint(lift, new[] { 0.05, 0.0, 0.0 });
            AssertPoint(MathOps.TransformPoint(rail, new[] { 0.05, 0.0, 0.0 }), point);
        }

        [Fact]
        public void SubDocumentContextComposesWithTheWalkedPose()
        {
            // Read through flexible Arm-1's document: Transform2 is relative
            // to that document. Rigid Clamp-1 sits in it at (0.1, 0, 0), and
            // the walk placed Clamp-1 (Arm-1's child) in the world. The part
            // Jaw-1 sits inside Clamp-1.
            var clampInArm = Pose(0.1, 0.0, 0.0, 0.0);
            var jawInClamp = Pose(0.0, 0.03, 0.0, 90.0);
            var jawInArm = MathOps.Multiply(clampInArm, jawInClamp);
            var clampWorld = Pose(0.5, 0.2, 0.0, 45.0);

            var lift = MateReader.PartLift(clampWorld, clampInArm, jawInArm);

            var expected = MathOps.Multiply(clampWorld, jawInClamp);
            AssertPoint(MathOps.TransformPoint(expected, new[] { 0.01, 0.0, 0.0 }),
                SwFrames.LiftPoint(lift, new[] { 0.01, 0.0, 0.0 }));
        }

        [Fact]
        public void WalkedPartKeepsItsWalkedTransform()
        {
            var part = Pose(0.3, -0.1, 0.2, 60.0);
            var lift = MateReader.PartLift(part, part, part);
            AssertPoint(MathOps.TransformPoint(part, new[] { 0.02, 0.01, 0.0 }),
                SwFrames.LiftPoint(lift, new[] { 0.02, 0.01, 0.0 }));
        }

        [Fact]
        public void SubassemblyOwnCurveLiftsByTheSubassembly()
        {
            // Flexible Track-1 at (0.3, 0, 0), turned 90 degrees, holds an
            // assembly-level 3D sketch: its curve has no ReferenceComponent
            // and is in Track-1's document frame. The follower vertex of the
            // same mate lifts by Track-1, so the path must too.
            var track = Pose(0.3, 0.0, 0.0, 90.0);
            var lift = MateReader.UnresolvedLift(track, null);
            AssertPoint(new[] { 0.3, 0.1, 0.0 }, SwFrames.LiftPoint(lift, new[] { 0.1, 0.0, 0.0 }));
        }

        [Fact]
        public void UnwalkedPartCurveLiftsByItsPlaceInTheReadDocument()
        {
            var track = Pose(0.3, 0.0, 0.0, 90.0);
            var partInTrack = Pose(0.0, 0.05, 0.0, 0.0);
            var lift = MateReader.UnresolvedLift(track, partInTrack);
            AssertPoint(MathOps.TransformPoint(MathOps.Multiply(track, partInTrack), new[] { 0.1, 0.0, 0.0 }),
                SwFrames.LiftPoint(lift, new[] { 0.1, 0.0, 0.0 }));

            // At the top a part's place is already in the world.
            var top = MateReader.UnresolvedLift(null, partInTrack);
            AssertPoint(new[] { 0.1, 0.05, 0.0 }, SwFrames.LiftPoint(top, new[] { 0.1, 0.0, 0.0 }));
        }

        [Fact]
        public void TopDocumentOwnCurveStaysInTheWorld()
        {
            Assert.Null(MateReader.UnresolvedLift(null, null));
        }

        [Fact]
        public void OnlyChangeOkIsAResolvedSuppression()
        {
            // swSuppressionError_e: BadComponent 0, BadState 1, ChangeOk 2,
            // ChangeFailed 3. SetSuppression2 reports a refusal by value.
            Assert.True(MateReader.SuppressionChanged(2));
            Assert.False(MateReader.SuppressionChanged(3));
            Assert.False(MateReader.SuppressionChanged(0));
            Assert.False(MateReader.SuppressionChanged(1));
        }

        [Fact]
        public void UnreadablePlacesFallBackToTheWalkedTransform()
        {
            var walked = Pose(0.3, 0.0, 0.0, 10.0);
            Assert.Same(walked, MateReader.PartLift(walked, null, Pose(0, 0, 0, 0)));
            Assert.Same(walked, MateReader.PartLift(walked, Pose(0, 0, 0, 0), null));
            Assert.Null(MateReader.PartLift(null, Pose(0, 0, 0, 0), Pose(0, 0, 0, 0)));
        }
    }
}
