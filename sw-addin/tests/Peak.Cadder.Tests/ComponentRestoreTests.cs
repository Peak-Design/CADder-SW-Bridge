using System;
using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Sw;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The probes that move the user's model put every component back where
    /// it was. A bare Transform2 write does not move a mated component, so
    /// the restore drags each one that is still off back by the difference,
    /// in steps small enough for the drag to follow. These tests check the
    /// two pieces of arithmetic that decide it: how far a component is off,
    /// and how the move back is cut into steps. The drags themselves need a
    /// live SolidWorks.
    /// </summary>
    public class ComponentRestoreTests
    {
        private static double[,] Screw(double[] axis, double[] point, double angle, double slide)
        {
            var n = MathOps.Normalized(axis);
            var m = ComponentMover.RotationAboutAxis(n, point, angle);
            for (int i = 0; i < 3; i++) m[i, 3] += n[i] * slide;
            return m;
        }

        private static void Same(double[,] a, double[,] b, double tolerance)
        {
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    Assert.True(Math.Abs(a[r, c] - b[r, c]) <= tolerance,
                        "[" + r + "," + c + "] " + a[r, c] + " vs " + b[r, c]);
        }

        private static double[,] Product(List<double[,]> steps)
        {
            var m = MathOps.Identity4();
            foreach (var step in steps) m = MathOps.Multiply(step, m);
            return m;
        }

        [Fact]
        public void ATurnAboutThePartsOwnOriginStillCountsAsOff()
        {
            // A hinge leaf whose origin is on the pin, left turned by one
            // nudge. Its origin has not moved at all.
            var want = MathOps.Identity4();
            var now = Screw(new[] { 0.0, 0, 1 }, new[] { 0.0, 0, 0 }, 0.035, 0.0);
            double angle, distance;
            ComponentMover.Residue(want, now, out angle, out distance);
            Assert.Equal(0.0, distance, 12);
            Assert.Equal(0.035, angle, 9);
        }

        [Fact]
        public void TheStepsAddUpToTheWholeMoveBack()
        {
            var delta = Screw(new[] { 0.3, -0.5, 0.8 }, new[] { 0.1, 0.02, -0.05 }, 2.9, 0.004);
            var steps = ComponentMover.Steps(delta, 0.1, 0.002, longWay: false);
            Assert.True(steps.Count >= 29, "only " + steps.Count + " step(s)");
            Same(Product(steps), delta, 1e-12);
            foreach (var step in steps)
            {
                double angle, distance;
                ComponentMover.Residue(MathOps.Identity4(), step, out angle, out distance);
                Assert.True(angle <= 0.1 + 1e-12, "a step of " + angle + " rad");
            }
        }

        [Fact]
        public void TheLongWayRoundEndsInTheSamePlace()
        {
            // A driver stopped three quarters of the way round can go back
            // the way it came, the long way, when the short way is blocked.
            var delta = Screw(new[] { 0.0, 0, 1 }, new[] { 0.02, 0, 0 }, Math.PI / 2, 0.0);
            var shortWay = ComponentMover.Steps(delta, 0.1, 0.002, longWay: false);
            var longWay = ComponentMover.Steps(delta, 0.1, 0.002, longWay: true);
            Assert.True(longWay.Count > 2 * shortWay.Count);
            Same(Product(longWay), delta, 1e-12);
        }

        [Fact]
        public void APureSlideIsCutAlongItsOwnLine()
        {
            var delta = MathOps.Identity4();
            delta[0, 3] = 0.01;
            delta[2, 3] = -0.004;
            var steps = ComponentMover.Steps(delta, 0.1, 0.002, longWay: false);
            Assert.True(steps.Count >= 6);
            Same(Product(steps), delta, 1e-15);
        }

        [Theory]
        [InlineData(Math.PI)]
        [InlineData(Math.PI - 1e-8)]
        public void AHalfTurnIsCutToo(double angle)
        {
            var delta = Screw(new[] { 0.6, 0.0, 0.8 }, new[] { 0.0, 0.03, 0.0 }, angle, 0.001);
            var steps = ComponentMover.Steps(delta, 0.1, 0.002, longWay: false);
            Same(Product(steps), delta, 1e-9);
        }

        [Fact]
        public void NothingToDoIsNoStepAtAll()
        {
            Assert.Empty(ComponentMover.Steps(MathOps.Identity4(), 0.1, 0.002, longWay: false));
        }
    }
}
