using System;
using System.Collections.Generic;
using System.Globalization;
using Peak.Cadder.Core;
using SolidWorks.Interop.sldworks;

namespace Peak.Cadder.Sw
{
    /// <summary>
    /// Moves a mated component through its free motion and puts everything
    /// back. The mover is the interactive drag pipeline (IDragOperator in
    /// relaxation mode): a bare Transform2 write plus a rebuild reads back
    /// unmoved, and SetTransformAndSolve2 also reads back unmoved for a
    /// mated component; the drag operator is the one mover that
    /// demonstrably takes a mated component through its degree of freedom
    /// and stops at limit mates (LimitSignProbe, live 2026-08-23).
    ///
    /// One mover serves both probes: the limit-sign probe, which nudges a
    /// joint towards each of its stops to read which way its dimension
    /// grows, and the relation probe, which turns a cam or a universal
    /// joint's input through a revolution. The limit-sign probe had its own
    /// copy of these calls until 2026-09-16, when the two were merged in a
    /// live session and the cam tables were read again to check it.
    /// </summary>
    internal sealed class ComponentMover
    {
        private readonly ISldWorks _app;
        private readonly IModelDoc2 _model;
        private readonly IAssemblyDoc _assembly;
        private readonly Action<string> _log;
        private IMathUtility _mathUtil;

        /// <summary>A component counts as back in place within these.</summary>
        private const double BackAngle = 1e-6, BackDistance = 1e-6;

        /// <summary>The largest single drag of a restore: near the 5 degree
        /// step the relation probe turns a cam by, and four of the largest
        /// slides the limit-sign probe makes.</summary>
        private const double StepAngle = 0.1, StepDistance = 0.002;

        private const int RestorePasses = 3;

        /// <summary><paramref name="log"/> defaults to the add-in log, so a
        /// component the restore could not put back is always named.</summary>
        public ComponentMover(ISldWorks app, IModelDoc2 model, Action<string> log = null)
        {
            _app = app;
            _model = model;
            _assembly = model as IAssemblyDoc;
            _log = log ?? (Action<string>)AddIn.Log;
        }

        /// <summary>A component the restore could not put back, and how
        /// far off it was left.</summary>
        public sealed class LeftMoved
        {
            public string Name;
            public double Angle;
            public double Distance;
        }

        public bool Ready { get { return _assembly != null; } }

        /// <summary>Turns (rotational) or slides the component by `amount`
        /// about/along the world axis through `origin`. `from` is the
        /// component's transform before the move, for the solve fallback.</summary>
        /// <summary>The drag mode the mates are solved in. 2 (relaxation)
        /// is what the limit-sign probe has always used; the relation
        /// probe tries the others when a coupling mate does not carry the
        /// driven body along.</summary>
        public int DragMode = 2;

        public bool Nudge(
            Component2 comp, double[] axis, double[] origin, double amount,
            bool rotational, double[,] from)
        {
            var motion = rotational
                ? RotationAboutAxis(axis, origin, amount)
                : TranslationAlong(axis, amount);
            if (DragBy(comp, motion)) return true;
            return from != null && SolveTo(comp, MathOps.Multiply(motion, from));
        }

        public bool DragBy(Component2 comp, double[,] deltaWorld)
        {
            IDragOperator drag = null;
            try
            {
                drag = _assembly.GetDragOperator() as IDragOperator;
                if (drag == null) return false;
                try { drag.GraphicsRedrawEnabled = false; } catch { }
                try { drag.CollisionDetectionEnabled = false; } catch { }
                try { drag.DynamicClearanceEnabled = false; } catch { }
                drag.TransformType = 2;         // general
                drag.DragMode = (short)DragMode;    // 2 = relaxation: solve the mates
                try { drag.UseAbsoluteTransform = false; } catch { }
                if (!drag.AddComponent(comp, false)) return false;
                if (!drag.BeginDrag()) return false;
                var mt = ToMathTransform(deltaWorld);
                if (mt == null) { drag.EndDrag(); return false; }
                drag.Drag(mt);
                drag.EndDrag();
                return true;
            }
            catch
            {
                try { if (drag != null) drag.EndDrag(); } catch { }
                return false;
            }
        }

        public bool SolveTo(Component2 comp, double[,] m)
        {
            var mt = ToMathTransform(m);
            if (mt == null) return false;
            try { return comp.SetTransformAndSolve2(mt); }
            catch { return false; }
        }

        public MathTransform ToMathTransform(double[,] m)
        {
            if (_mathUtil == null)
            {
                try { _mathUtil = _app.GetMathUtility() as IMathUtility; } catch { }
                if (_mathUtil == null) return null;
            }
            return SwFrames.FromMatrix(_mathUtil, m);
        }

        public static List<KeyValuePair<Component2, MathTransform>> Snapshot(
            IEnumerable<WalkedComponent> walked)
        {
            var snaps = new List<KeyValuePair<Component2, MathTransform>>();
            foreach (var w in walked)
            {
                if (w.Comp == null) continue;
                try
                {
                    var t = w.Comp.Transform2;
                    if (t != null)
                        snaps.Add(new KeyValuePair<Component2, MathTransform>(w.Comp, t));
                }
                catch { }
            }
            return snaps;
        }

        /// <summary>
        /// Puts every snapshotted component back and returns the ones it
        /// could not, each logged with how far off it was left.
        ///
        /// The Transform2 write puts back a component that no mate holds.
        /// It does NOT move a mated one: the mover history in
        /// LimitSignProbe records that a bare write plus a rebuild reads
        /// back unmoved. That write was the whole restore until 2026-09-23,
        /// and a live hinge sample was left turned by one nudge in the
        /// user's document (the leaf rested 0.61 mm off), with the STEP
        /// written later from the moved pose. So every component is then
        /// compared with its snapshot, turn and origin both, and one that
        /// is still off is dragged back by the difference, in short steps,
        /// through the same drag pipeline that moved it. A few passes
        /// catch a component that the drag of another carried away.
        /// </summary>
        public List<LeftMoved> RestoreAll(
            List<KeyValuePair<Component2, MathTransform>> snaps, string who = null)
        {
            foreach (var kv in snaps)
            {
                try { kv.Key.Transform2 = kv.Value; } catch { }
            }
            try { _model.EditRebuild3(); } catch { }

            for (int pass = 0; pass < RestorePasses; pass++)
            {
                int dragged = 0;
                foreach (var kv in snaps)
                {
                    var want = SwFrames.ToMatrix(kv.Value);
                    var now = SwFrames.ComponentWorld(kv.Key);
                    if (want == null || now == null) continue;
                    double angle, distance;
                    Residue(want, now, out angle, out distance);
                    if (angle <= BackAngle && distance <= BackDistance) continue;
                    dragged++;
                    // The last pass may go round the other way. A driver
                    // left part of the way round a turn can be blocked the
                    // short way back and free the way it came.
                    bool longWay = pass == RestorePasses - 1 && angle > 0.5;
                    var delta = MathOps.Multiply(want, MathOps.InvertRigid(now));
                    foreach (var step in Steps(delta, StepAngle, StepDistance, longWay))
                        if (!DragBy(kv.Key, step))
                        {
                            SolveTo(kv.Key, want);
                            break;
                        }
                }
                if (dragged == 0) break;
                try { _model.EditRebuild3(); } catch { }
            }

            var left = new List<LeftMoved>();
            foreach (var kv in snaps)
            {
                var want = SwFrames.ToMatrix(kv.Value);
                var now = SwFrames.ComponentWorld(kv.Key);
                if (want == null || now == null) continue;
                double angle, distance;
                Residue(want, now, out angle, out distance);
                if (angle <= BackAngle && distance <= BackDistance) continue;
                string name = null;
                try { name = kv.Key.Name2; } catch { }
                left.Add(new LeftMoved { Name = name ?? "?", Angle = angle, Distance = distance });
            }
            if (_log != null)
                foreach (var moved in left)
                    _log(string.Format(CultureInfo.InvariantCulture,
                        "{0}: {1} could not be put back and stays {2:0.0e0} rad and "
                        + "{3:0.0e0} m from where it was. Check its position in SolidWorks "
                        + "before you save",
                        who ?? "restore", moved.Name, moved.Angle, moved.Distance));
            return left;
        }

        /// <summary>
        /// How far a component is from where it should be: the angle it is
        /// turned by, and how far its origin is. Both count. A part whose
        /// origin sits on its own axis (a cam, a shaft, a hinge leaf drawn
        /// on its pin) turns without moving its origin at all, and a check
        /// of the origin alone read such a part as back in place.
        /// </summary>
        internal static void Residue(
            double[,] want, double[,] now, out double angle, out double distance)
        {
            // The rotation from now to want: want times now transposed.
            var r = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    for (int k = 0; k < 3; k++)
                        r[i, j] += want[i, k] * now[j, k];
            angle = AngleOf(r);
            distance = Math.Sqrt(
                (want[0, 3] - now[0, 3]) * (want[0, 3] - now[0, 3])
                + (want[1, 3] - now[1, 3]) * (want[1, 3] - now[1, 3])
                + (want[2, 3] - now[2, 3]) * (want[2, 3] - now[2, 3]));
        }

        /// <summary>The angle of a rotation, 0 to pi. atan2, not acos, so
        /// that a small angle is read to full precision.</summary>
        private static double AngleOf(double[,] r)
        {
            double wx = r[2, 1] - r[1, 2], wy = r[0, 2] - r[2, 0], wz = r[1, 0] - r[0, 1];
            double sin = 0.5 * Math.Sqrt(wx * wx + wy * wy + wz * wz);
            double cos = 0.5 * (r[0, 0] + r[1, 1] + r[2, 2] - 1.0);
            return Math.Atan2(sin, cos);
        }

        /// <summary>
        /// A rigid move cut into equal steps: turns of at most maxAngle
        /// about one screw axis, and slides of at most maxDistance along it.
        /// Applied one after the other, each on the left, they make the
        /// whole move. The drag solves the mates at every step, so it
        /// follows a long move in short steps the way the relation probe
        /// turns a cam.
        ///
        /// longWay turns the other way round the same axis, through the
        /// rest of the circle, to the same place. A driver that stopped part
        /// of the way round can go back the way it came when the short way
        /// is blocked.
        /// </summary>
        internal static List<double[,]> Steps(
            double[,] delta, double maxAngle, double maxDistance, bool longWay)
        {
            var steps = new List<double[,]>();
            var r = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[i, j] = delta[i, j];
            var t = new[] { delta[0, 3], delta[1, 3], delta[2, 3] };
            double theta = AngleOf(r);

            if (theta < 1e-12)
            {
                double length = MathOps.Norm(t);
                if (length < 1e-15) return steps;
                int count = StepCount(0.0, length, maxAngle, maxDistance);
                for (int s = 0; s < count; s++)
                    steps.Add(ComponentMover.TranslationAlong(
                        new[] { t[0] / length, t[1] / length, t[2] / length }, length / count));
                return steps;
            }

            var n = AxisOf(r, theta);
            double slide = MathOps.Dot(t, n);
            var across = new[] { t[0] - slide * n[0], t[1] - slide * n[1], t[2] - slide * n[2] };
            // The point on the screw axis nearest the origin: with a the
            // part of t across the axis, p = (a + cot(theta/2) n x a) / 2.
            var turn = MathOps.Cross(n, across);
            double cot = 1.0 / Math.Tan(theta / 2.0);
            var point = new[]
            {
                0.5 * (across[0] + cot * turn[0]),
                0.5 * (across[1] + cot * turn[1]),
                0.5 * (across[2] + cot * turn[2]),
            };
            double angle = longWay ? theta - 2.0 * Math.PI : theta;
            int n2 = StepCount(Math.Abs(angle), Math.Abs(slide), maxAngle, maxDistance);
            for (int s = 0; s < n2; s++)
            {
                var step = RotationAboutAxis(n, point, angle / n2);
                for (int i = 0; i < 3; i++) step[i, 3] += n[i] * slide / n2;
                steps.Add(step);
            }
            return steps;
        }

        private static int StepCount(
            double angle, double distance, double maxAngle, double maxDistance)
        {
            double count = 1.0;
            if (maxAngle > 0.0) count = Math.Max(count, Math.Ceiling(angle / maxAngle));
            if (maxDistance > 0.0) count = Math.Max(count, Math.Ceiling(distance / maxDistance));
            return (int)Math.Min(count, 400.0);
        }

        /// <summary>The unit axis of a rotation by theta, in the sense
        /// that turns by +theta about it.</summary>
        private static double[] AxisOf(double[,] r, double theta)
        {
            var w = new[] { r[2, 1] - r[1, 2], r[0, 2] - r[2, 0], r[1, 0] - r[0, 1] };
            double size = MathOps.Norm(w);
            if (Math.Sin(theta) > 1e-6 && size > 0.0)
                return new[] { w[0] / size, w[1] / size, w[2] / size };
            // Near half a turn the skew part says little. The symmetric part
            // is cos I + (1 - cos) n n^T, so n n^T comes out exactly, and
            // its largest column is the axis.
            double cos = Math.Cos(theta);
            int j = 0;
            for (int k = 1; k < 3; k++) if (r[k, k] > r[j, j]) j = k;
            var n = new double[3];
            for (int i = 0; i < 3; i++)
                n[i] = (0.5 * (r[i, j] + r[j, i]) - (i == j ? cos : 0.0)) / (1.0 - cos);
            var unit = MathOps.Normalized(n);
            if (MathOps.Dot(unit, w) < 0.0) unit = new[] { -unit[0], -unit[1], -unit[2] };
            return unit;
        }

        public static double[,] RotationAboutAxis(double[] a, double[] o, double angle)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle), t = 1.0 - c;
            var r = new double[3, 3]
            {
                { t * a[0] * a[0] + c,        t * a[0] * a[1] - s * a[2], t * a[0] * a[2] + s * a[1] },
                { t * a[0] * a[1] + s * a[2], t * a[1] * a[1] + c,        t * a[1] * a[2] - s * a[0] },
                { t * a[0] * a[2] - s * a[1], t * a[1] * a[2] + s * a[0], t * a[2] * a[2] + c        },
            };
            var m = MathOps.Identity4();
            for (int i = 0; i < 3; i++)
            {
                double ro = 0;
                for (int j = 0; j < 3; j++)
                {
                    m[i, j] = r[i, j];
                    ro += r[i, j] * o[j];
                }
                m[i, 3] = o[i] - ro;    // p' = R(p - o) + o
            }
            return m;
        }

        public static double[,] TranslationAlong(double[] a, double dist)
        {
            var m = MathOps.Identity4();
            m[0, 3] = a[0] * dist;
            m[1, 3] = a[1] * dist;
            m[2, 3] = a[2] * dist;
            return m;
        }
    }
}
