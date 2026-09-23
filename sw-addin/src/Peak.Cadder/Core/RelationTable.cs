using System;
using System.Collections.Generic;
using Peak.Cadder.Core.Model;

namespace Peak.Cadder.Core
{
    /// <summary>
    /// A sampled relation between two joints, as a table coupling. The
    /// samples come from the live model (RelationProbe turns the driver
    /// through a revolution and reads the driven joint), so this only has
    /// to make a clean table of them: sorted by driver value, one point per
    /// value, relative to the exported pose, and periodic when the driver
    /// came all the way round.
    ///
    /// Why a table and not a formula: a cam profile is arbitrary geometry,
    /// and a universal joint's output angle is atan2(sin x, cos x cos b)
    /// only once the yokes' phase is known, which no mate records. The
    /// solver knows both. The consumer maps the driver's channel through
    /// the samples (a driver F-curve with keyframes does exactly that) and
    /// never sees the difference.
    /// </summary>
    public static class RelationTable
    {
        /// <summary>Two samples closer than this in x are one sample.</summary>
        public const double SameX = 1e-6;

        /// <summary>
        /// Builds the coupling. `raw` holds (driverValue, drivenValue) pairs
        /// in the order they were read, driver values UNWRAPPED (a turn
        /// read past 2 pi keeps counting). The first pair is the rest pose
        /// and both values are taken relative to it. `drivenTurns` is true
        /// when the driven joint turns and its values are unwrapped too (a
        /// universal joint's output, a rocker): a periodic cycle then
        /// closes at the nearest whole turn, not at 0. Null when fewer than
        /// two distinct points came back.
        /// </summary>
        public static JointCoupling Build(
            string driverJoint, IList<double[]> raw, double period, bool driverTurns,
            bool drivenTurns = false)
        {
            if (raw == null || raw.Count < 2) return null;
            double x0 = raw[0][0], y0 = raw[0][1];
            var points = new List<double[]>();
            foreach (var p in raw)
            {
                if (p == null || double.IsNaN(p[0]) || double.IsNaN(p[1])) continue;
                points.Add(new[] { p[0] - x0, p[1] - y0 });
            }
            points.Sort((a, b) => a[0].CompareTo(b[0]));

            bool periodic = false;
            if (driverTurns && period > 0)
            {
                // Came round: the last driver value reaches a full period
                // (a little short is the last step's slack, a little over
                // is the same point again).
                double reach = points[points.Count - 1][0];
                periodic = reach >= period - period / 36.0;
                if (periodic)
                {
                    // The driven value one period on. A cam follower comes
                    // back where it started, so that is 0. The output yoke
                    // of a universal joint makes a whole turn with the
                    // driver: close its cycle at the whole turns it made.
                    // A close at 0 made the last segment run from a full
                    // turn back to 0, and the output spun a turn backwards
                    // in the last step below the rest pose. The consumer
                    // repeats the cycle with no offset, so at the seam the
                    // output jumps by whole turns, which a rotation does
                    // not show. The reading nearest one period tells the
                    // turns best.
                    double end = 0.0;
                    if (drivenTurns)
                    {
                        double turns = points[points.Count - 1][1] / (2.0 * Math.PI);
                        end = 2.0 * Math.PI * Math.Round(turns);
                    }
                    // Fold anything past one period onto [0, period) so the
                    // table is one cycle, and the cycle's end matches its
                    // start (in whole turns for a turning driven joint).
                    var folded = new List<double[]>();
                    foreach (var p in points)
                    {
                        double x = p[0];
                        if (x >= period - SameX) continue;
                        folded.Add(new[] { x, p[1] });
                    }
                    folded.Sort((a, b) => a[0].CompareTo(b[0]));
                    points = folded;
                    points.Add(new[] { period, end });
                }
            }

            var samples = new List<double[]>();
            foreach (var p in points)
            {
                if (samples.Count > 0 && Math.Abs(p[0] - samples[samples.Count - 1][0]) < SameX)
                    continue;
                samples.Add(p);
            }
            if (samples.Count < 2) return null;

            var c = new JointCoupling();
            c.Kind = "table";
            c.DriverJoint = driverJoint;
            c.Samples = samples.ToArray();
            c.Periodic = periodic;
            c.Period = periodic ? period : 0.0;
            return c;
        }

        /// <summary>Linear interpolation through the table, for tests and
        /// for a consumer that has no curve of its own. Periodic tables wrap
        /// with no offset, as the consumer's repeat does, so a turning
        /// driven joint loses its whole turns at the seam. Others clamp to
        /// their ends.</summary>
        public static double Evaluate(JointCoupling c, double x)
        {
            var s = c.Samples;
            if (c.Periodic && c.Period > 0)
            {
                x = x % c.Period;
                if (x < 0) x += c.Period;
            }
            if (x <= s[0][0]) return s[0][1];
            for (int i = 1; i < s.Length; i++)
            {
                if (x > s[i][0]) continue;
                double t = (x - s[i - 1][0]) / (s[i][0] - s[i - 1][0]);
                return s[i - 1][1] + t * (s[i][1] - s[i - 1][1]);
            }
            return s[s.Length - 1][1];
        }
    }
}
