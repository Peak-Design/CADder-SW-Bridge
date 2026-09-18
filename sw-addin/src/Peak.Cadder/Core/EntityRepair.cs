using System;
using Peak.Cadder.Core.Model;

namespace Peak.Cadder.Core
{
    /// <summary>
    /// Types a mate entity by what the solved assembly shows it must be,
    /// where SolidWorks reported it as something it cannot be.
    ///
    /// A coincident mate is solved: its two entities touch. SolidWorks 2022
    /// reports a point held on a sketch line with the LINE typed as a point
    /// (its direction still in the direction slots) and the held point
    /// untyped (live slot_slot, 2026-09-18: point(1/0) on the base,
    /// unknown(0/0) on the straight slot). Two points that coincide are at
    /// one place. These two are 74 mm apart, and the untyped one lies on
    /// the line through the "point" along its direction to 1e-16 m. So the
    /// "point" is the line and the other is the point that rides on it.
    /// Read as two points, the coincident took every freedom and the slot
    /// welded to the base. SolidWorks 2024 typed the same mate correctly,
    /// and the slot slid (2026-09-16).
    ///
    /// MateReader.RecoverCurveEntities does the same from the live
    /// selection, by reading the curve behind it. For these sketch lines
    /// SolidWorks 2022 gave it nothing to read, so the solved positions are
    /// what is left, and they are exact.
    ///
    /// Only this shape is repaired: one entity typed a point, the other
    /// untyped or a vertex, apart, and the second exactly on the first's
    /// line. Anything else is left as SolidWorks reported it.
    /// </summary>
    internal static class EntityRepair
    {
        /// <summary>How far a point may sit off the line, and how far apart
        /// the two must be, in metres. SolidWorks solves to far below this,
        /// and a real coincident of two points is at one place.</summary>
        public const double Tolerance = 1e-6;

        public static int Apply(MateGraph graph, Action<string> log)
        {
            int repaired = 0;
            if (graph == null) return 0;
            foreach (var mate in graph.Mates)
            {
                if (!MateFacts.Is(mate, "COINCIDENT") || mate.Entities.Count != 2)
                    continue;
                for (int i = 0; i < 2; i++)
                {
                    var line = mate.Entities[i];
                    var held = mate.Entities[1 - i];
                    if (!Repairs(line, held)) continue;
                    var along = MathOps.Normalized(line.RawDirection);
                    // The same kind RecoverCurveEntities gives a line it
                    // reads off the selection, so both routes rig alike.
                    line.EntityTypeName = "edge";
                    line.Direction = along;
                    held.EntityTypeName = "vertex";
                    held.Direction = null;
                    repaired++;
                    if (log != null)
                        log("line read from a solved coincident: " + mate.FeatureName
                            + ", the point on " + (line.ComponentId ?? "asm")
                            + " is a line along [" + Fmt(along) + "] and the entity on "
                            + (held.ComponentId ?? "asm") + " rides on it "
                            + Fmt1(Distance(line.Point, held.Point) * 1000.0) + " mm along");
                    break;
                }
            }
            return repaired;
        }

        private static bool Repairs(GraphMateEntity line, GraphMateEntity held)
        {
            if (line.EntityTypeName != "point" || line.Point == null
                || line.RawDirection == null) return false;
            if (held.EntityTypeName != "unknown" && held.EntityTypeName != "vertex")
                return false;
            if (held.Point == null) return false;
            if (MathOps.Norm(line.RawDirection) < MathOps.Epsilon) return false;
            var along = MathOps.Normalized(line.RawDirection);
            var d = new[]
            {
                held.Point[0] - line.Point[0],
                held.Point[1] - line.Point[1],
                held.Point[2] - line.Point[2],
            };
            double apart = MathOps.Norm(d);
            if (apart <= Tolerance) return false;
            double t = MathOps.Dot(d, along);
            double off = Math.Sqrt(Math.Max(0.0, apart * apart - t * t));
            return off <= Tolerance;
        }

        private static double Distance(double[] a, double[] b)
        {
            double x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2];
            return Math.Sqrt(x * x + y * y + z * z);
        }

        private static string Fmt(double[] v)
        {
            return Fmt1(v[0], "0.####") + "," + Fmt1(v[1], "0.####") + "," + Fmt1(v[2], "0.####");
        }

        private static string Fmt1(double v, string format = "0.#")
        {
            return v.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
