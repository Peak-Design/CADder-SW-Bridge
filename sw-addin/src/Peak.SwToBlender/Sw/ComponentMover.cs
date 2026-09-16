using System;
using System.Collections.Generic;
using Peak.SwToBlender.Core;
using SolidWorks.Interop.sldworks;

namespace Peak.SwToBlender.Sw
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
        private IMathUtility _mathUtil;

        public ComponentMover(ISldWorks app, IModelDoc2 model)
        {
            _app = app;
            _model = model;
            _assembly = model as IAssemblyDoc;
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

        public void RestoreAll(List<KeyValuePair<Component2, MathTransform>> snaps)
        {
            foreach (var kv in snaps)
            {
                try { kv.Key.Transform2 = kv.Value; } catch { }
            }
            try { _model.EditRebuild3(); } catch { }
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
