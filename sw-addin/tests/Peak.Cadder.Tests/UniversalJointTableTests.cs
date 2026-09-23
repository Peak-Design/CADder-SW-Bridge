using System;
using System.Collections.Generic;
using Peak.Cadder.Core;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>A table whose driven joint turns with the driver: the output
    /// yoke of a universal joint. After one input turn the output has also
    /// made a whole turn, so the cycle must close at that turn and not at 0.
    /// When it closed at 0, the output spun a full turn backwards in the
    /// last probe step, right below the rest pose.</summary>
    public class UniversalJointTableTests
    {
        private const double Deg = Math.PI / 180.0;

        private static double Wrap(double a)
        {
            while (a > Math.PI) a -= 2.0 * Math.PI;
            while (a <= -Math.PI) a += 2.0 * Math.PI;
            return a;
        }

        /// <summary>The output angle of a universal joint at input x, bent
        /// by b. `sign` -1 is the same joint read about the other axis
        /// direction, so the output counts down.</summary>
        private static double Output(double x, double bend, double sign)
        {
            return sign * Math.Atan2(Math.Sin(x), Math.Cos(x) * Math.Cos(bend));
        }

        /// <summary>What RelationProbe reads: the rest pair, then one pair
        /// per step until the input comes round. It unwraps the output the
        /// way the probe does for a turning driven joint.</summary>
        private static List<double[]> Probe(int steps, double reach, double bend, double sign)
        {
            var raw = new List<double[]> { new[] { 0.0, 0.0 } };
            double total = 0.0, last = 0.0;
            for (int k = 1; k <= steps; k++)
            {
                double x = reach * k / steps;
                double f = Wrap(Output(x, bend, sign));
                total += Wrap(f - last);
                last = f;
                raw.Add(new[] { x, total });
            }
            return raw;
        }

        private static double AngleError(double a, double b)
        {
            return Math.Abs(Wrap(a - b));
        }

        [Theory]
        [InlineData(1.0)]
        [InlineData(-1.0)]
        public void TheCycleClosesAtTheWholeTurnTheOutputMade(double sign)
        {
            var c = RelationTable.Build(
                "j001", Probe(72, 2.0 * Math.PI, 40.0 * Deg, sign), 2.0 * Math.PI, true, true);
            Assert.NotNull(c);
            Assert.True(c.Periodic);
            var end = c.Samples[c.Samples.Length - 1];
            Assert.Equal(2.0 * Math.PI, end[0], 12);
            Assert.Equal(sign * 2.0 * Math.PI, end[1], 12);
        }

        [Theory]
        [InlineData(1.0)]
        [InlineData(-1.0)]
        public void TheOutputFollowsTheInputThroughTheSeam(double sign)
        {
            double bend = 40.0 * Deg;
            var c = RelationTable.Build(
                "j001", Probe(72, 2.0 * Math.PI, bend, sign), 2.0 * Math.PI, true, true);
            // The last step of a turn, just below the rest pose, and the
            // same place one turn on. Linear between samples five degrees
            // apart is good to well under a degree. Before the fix, -2.5
            // degrees read 176.7 degrees and -1 degree read 70.7 degrees.
            foreach (double x in new[] { -2.5, -1.0, 357.5, 359.0, 717.5, -362.5 })
            {
                double want = Output(x * Deg, bend, sign);
                double got = RelationTable.Evaluate(c, x * Deg);
                Assert.True(AngleError(got, want) < 0.5 * Deg,
                    "input " + x + " deg: output " + got / Deg + " deg, want " + want / Deg + " deg");
            }
        }

        [Fact]
        public void AProbeThatStopsALittleShortStillClosesAtTheWholeTurn()
        {
            // The last drag landed 4 degrees short of a full turn. That is
            // inside the slack Build accepts as having come round.
            double reach = 2.0 * Math.PI - 4.0 * Deg;
            var c = RelationTable.Build(
                "j001", Probe(71, reach, 30.0 * Deg, 1.0), 2.0 * Math.PI, true, true);
            Assert.True(c.Periodic);
            Assert.Equal(2.0 * Math.PI, c.Samples[c.Samples.Length - 1][1], 12);
            double want = Output(-2.0 * Deg, 30.0 * Deg, 1.0);
            Assert.True(AngleError(RelationTable.Evaluate(c, -2.0 * Deg), want) < 0.5 * Deg);
        }

        [Fact]
        public void ARockerThatSwingsBackStillClosesAtZero()
        {
            // A rocker on a cam turns, but it comes back where it started:
            // no whole turn to keep.
            var raw = new List<double[]>();
            for (int k = 0; k <= 72; k++)
            {
                double x = 2.0 * Math.PI * k / 72;
                raw.Add(new[] { x, 0.3 * (1.0 - Math.Cos(x)) });
            }
            var c = RelationTable.Build("j001", raw, 2.0 * Math.PI, true, true);
            Assert.True(c.Periodic);
            Assert.Equal(0.0, c.Samples[c.Samples.Length - 1][1], 12);
        }

        [Fact]
        public void ASlideStillClosesAtZero()
        {
            // A slide is not a turn, so its end is never rounded to a turn,
            // even when its last reading is near 2 pi (metres here).
            var raw = new List<double[]>();
            for (int k = 0; k <= 72; k++)
            {
                double x = 2.0 * Math.PI * k / 72;
                raw.Add(new[] { x, 0.99 * x });
            }
            var c = RelationTable.Build("j001", raw, 2.0 * Math.PI, true);
            Assert.True(c.Periodic);
            Assert.Equal(0.0, c.Samples[c.Samples.Length - 1][1], 12);
        }
    }
}
