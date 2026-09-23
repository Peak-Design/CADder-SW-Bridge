using Peak.Cadder.Sw;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The limit-sign probe's second signal needs to know which stop the
    /// joint rests at. For a pair inside a flexed subassembly, the rest
    /// value is shifted by the flexed motion, and that shift adds or
    /// subtracts according to the very sign the probe is looking for. When
    /// the two answers name different stops, the signal would only confirm
    /// the sign it assumed, so it must say nothing.
    /// </summary>
    public class LimitSignEndpointTests
    {
        private const double Tol = 0.02;

        [Fact]
        public void NoFlexIsTheRestValueItself()
        {
            Assert.Equal(-1, LimitSignProbe.RestEndpoint(0.0, 0.0, 0.0, 1.0, Tol));
            Assert.Equal(1, LimitSignProbe.RestEndpoint(1.0, 0.0, 0.0, 1.0, Tol));
            Assert.Equal(0, LimitSignProbe.RestEndpoint(0.5, 0.0, 0.0, 1.0, Tol));
        }

        [Fact]
        public void ASmallFlexNamesTheSameStopEitherWay()
        {
            Assert.Equal(-1, LimitSignProbe.RestEndpoint(0.0, 0.005, 0.0, 1.0, Tol));
        }

        [Fact]
        public void TwoSignsNamingTwoStopsSayNothing()
        {
            // Rest at mid range, flexed half the span: with sign +1 the pair
            // sits at Max, with sign -1 at Min.
            Assert.Equal(0, LimitSignProbe.RestEndpoint(0.5, 0.5, 0.0, 1.0, Tol));
        }

        [Fact]
        public void AStopOnlyOneSignCanReachSaysNothing()
        {
            // Sign +1 puts the pair at Max, sign -1 puts it inside the
            // range. Either can be true.
            Assert.Equal(0, LimitSignProbe.RestEndpoint(0.7, 0.3, 0.0, 1.0, Tol));
        }

        [Fact]
        public void ASignThatLeavesTheRangeIsImpossible()
        {
            // Sign -1 would put the pair at 1.4, past Max: only sign +1 is
            // possible, and it rests at Min.
            Assert.Equal(-1, LimitSignProbe.RestEndpoint(0.7, -0.7, 0.0, 1.0, Tol));
        }
    }
}
