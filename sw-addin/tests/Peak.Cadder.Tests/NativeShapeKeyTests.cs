using Peak.Cadder.Sw;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// When two occurrences of one part share a mesh in the direct send.
    /// A part with external references (a hose of SolidWorks Routing) or a
    /// flexible part can have another shape in each place, and a shared
    /// mesh then shows the first shape everywhere.
    /// </summary>
    public class NativeShapeKeyTests
    {
        private static readonly double[] Hose = { -0.01, -0.01, 0.0, 0.01, 0.2, 0.35 };

        [Fact]
        public void TwoReadsOfOneBodyAreTheSameShape()
        {
            Assert.Equal(NativeSceneBuilder.ShapeOf(Hose, 12),
                         NativeSceneBuilder.ShapeOf((double[])Hose.Clone(), 12));
        }

        [Fact]
        public void NoiseBelowAMicrometreIsTheSameShape()
        {
            var noisy = (double[])Hose.Clone();
            noisy[4] += 1e-10;
            Assert.Equal(NativeSceneBuilder.ShapeOf(Hose, 12),
                         NativeSceneBuilder.ShapeOf(noisy, 12));
        }

        [Fact]
        public void AHoseRoutedToAnotherFittingIsAnotherShape()
        {
            var longer = (double[])Hose.Clone();
            longer[5] += 0.04;
            Assert.NotEqual(NativeSceneBuilder.ShapeOf(Hose, 12),
                            NativeSceneBuilder.ShapeOf(longer, 12));
        }

        [Fact]
        public void AnotherFaceCountIsAnotherShape()
        {
            Assert.NotEqual(NativeSceneBuilder.ShapeOf(Hose, 12),
                            NativeSceneBuilder.ShapeOf(Hose, 14));
        }

        [Fact]
        public void ABodyWithNoBoxStillHasAKey()
        {
            Assert.Equal("7", NativeSceneBuilder.ShapeOf(null, 7));
        }
    }
}
