using System;
using System.Collections.Generic;
using System.Globalization;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using SolidWorks.Interop.sldworks;

namespace Peak.Cadder.Sw
{
    /// <summary>
    /// The last rung of limit-sense resolution (JointClassifier's
    /// ILimitSignOracle): when a limit mate rests at 0/180 deg or with
    /// touching faces, no recorded geometry can say which way its dimension
    /// grows, but the live model can. The probe nudges one side of the pair
    /// a fraction of the limit span about/along the joint axis and reads two
    /// independent signals:
    ///
    ///   * the mate dimension's response (top-level mates only; SolidWorks
    ///     tracks a limit mate's dimension against the solved pose, proven
    ///     across three dragged exports reading 30°, 75°, 0°);
    ///   * the solver's asymmetry at a range endpoint: parked at Min, only
    ///     the dimension-increasing direction can move at all, so which nudge
    ///     survives IS the sign. This one needs no dimension and therefore
    ///     also works for pairs INSIDE a flexible subassembly, whose mate
    ///     features live in the sub document and report the document pose.
    ///
    /// MOVER HISTORY (all live 2026-08-23, corpus 01 at its stop): a bare
    /// Transform2 write plus EditRebuild3 reads back unmoved (the solver is
    /// never engaged); IComponent2.SetTransformAndSolve2 ALSO reads back
    /// unmoved for a mated component (it solves, but the previous solved
    /// state wins). IDragOperator is the interactive-drag pipeline: the one
    /// mover that demonstrably takes a mated component through its free DOF
    /// and stops at limit mates, so it is the primary mover, with
    /// SetTransformAndSolve2 kept only as a fallback when no drag operator
    /// exists. Every nudge is verified by transform read-back, so a mover
    /// that silently does nothing can only cost a signal, never invent one.
    ///
    /// Restoration: the nudge is reversed by the ACHIEVED amount (a blocked
    /// nudge must not be "reversed" into free territory), then every walked
    /// transform snapshot is re-written and the model rebuilt.
    /// </summary>
    public sealed class LimitSignProbe : ILimitSignOracle
    {
        private readonly IModelDoc2 _model;
        private readonly IAssemblyDoc _assembly;
        private readonly RigidGroupingResult _grouping;
        private readonly Action<string> _log;
        private readonly Dictionary<string, WalkedComponent> _byId
            = new Dictionary<string, WalkedComponent>();
        private readonly ComponentMover _mover;

        public LimitSignProbe(
            ISldWorks app, IModelDoc2 model, List<WalkedComponent> walked,
            RigidGroupingResult grouping, Action<string> log)
        {
            _model = model;
            _assembly = model as IAssemblyDoc;
            _grouping = grouping;
            _log = log ?? delegate { };
            _mover = new ComponentMover(app, model);
            foreach (var w in walked)
                if (w.Comp != null) _byId[w.Id] = w;
        }

        public int ResolveSign(RigJoint joint, GraphMate mate, bool rotational)
        {
            if (_assembly == null || joint == null || mate == null) return 0;
            var limit = rotational ? joint.RotationLimit : joint.TranslationLimit;
            if (limit == null) return 0;
            var axis = rotational
                ? joint.Axis
                : (joint.Type == JointType.PinSlot ? joint.SecondaryAxis : joint.Axis);
            if (axis == null) return 0;
            axis = MathOps.Normalized(axis);
            var origin = joint.Origin ?? new double[] { 0, 0, 0 };

            var parent = GroupRep(joint.ParentGroup);
            var child = GroupRep(joint.ChildGroup);
            if (parent == null || child == null) return 0;
            bool inSub = parent.Parent != null || child.Parent != null;

            // The recorded axis/origin describe the pose the mates were read
            // at; a flexed parent side carries them to the instance.
            var parentPoseDelta = parent.Graph.MatePoseDelta;
            if (parentPoseDelta != null)
            {
                axis = MathOps.Normalized(MathOps.RotateVector(parentPoseDelta, axis));
                origin = MathOps.TransformPoint(parentPoseDelta, origin);
            }

            var mover = child;
            if (IsFixed(child.Comp))
            {
                if (IsFixed(parent.Comp))
                {
                    _log("limit sign probe skipped " + joint.Id + ": both sides fixed");
                    return 0;
                }
                mover = parent;
            }
            bool moverIsChild = mover == child;

            IFeature feat = null;
            double a0 = double.NaN;
            if (!inSub)
            {
                feat = FindMateFeature(mate.FeatureName);
                if (feat != null) a0 = ReadDimension(feat, mate.TypeName);
            }

            // Where the INSTANCE's dimension currently sits: the recorded
            // rest shifted by the relative pose delta on the probe axis.
            double corrected = limit.ValueAtRest
                + RelativeDeltaOnAxis(joint, mate, axis, rotational);
            double span = Math.Abs(limit.Max - limit.Min);
            double endFloor = rotational ? 2e-3 : 1e-5;
            double endTol = Math.Max(endFloor, span * 0.02);
            bool atMin = Math.Abs(corrected - limit.Min) <= endTol;
            bool atMax = Math.Abs(corrected - limit.Max) <= endTol;

            double eps = rotational
                ? Math.Max(Math.Min(0.035, span / 4.0), 5e-4)    // ≤ ~2°
                : Math.Max(Math.Min(5e-4, span / 4.0), 1e-5);    // ≤ 0.5 mm

            var c0 = SwFrames.ToMatrix(child.Comp.Transform2);
            var p0 = SwFrames.ToMatrix(parent.Comp.Transform2);
            if (c0 == null || p0 == null) return 0;
            var m0 = moverIsChild ? c0 : p0;
            var snapshots = Snapshot();

            var achieved = new double[] { double.NaN, double.NaN };
            var dimDelta = new double[] { double.NaN, double.NaN };
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    int dir = i == 0 ? 1 : -1;
                    // Moving the parent by the inverse produces the same
                    // RELATIVE motion as moving the child forward.
                    double moverEps = (moverIsChild ? 1 : -1) * dir * eps;
                    if (!Nudge(mover.Comp, axis, origin, moverEps, rotational, m0))
                    {
                        _log("limit sign probe " + joint.Id + ": no mover accepted the "
                            + (dir > 0 ? "+" : "-") + "nudge on " + mover.Id);
                        continue;
                    }

                    var c1 = SwFrames.ToMatrix(child.Comp.Transform2);
                    var p1 = SwFrames.ToMatrix(parent.Comp.Transform2);
                    if (!inSub && feat != null)
                    {
                        // The dimension is solved lazily: reading it straight
                        // after a drag returned +0.0000 against a read-back
                        // proven motion (live corpus 01, 2026-08-23 18:04).
                        // A rebuild does not re-solve mates (round-11
                        // evidence), so it cannot undo the nudge: it only
                        // refreshes the feature data being read.
                        try { _model.EditRebuild3(); } catch { }
                        double a1 = ReadDimension(feat, mate.TypeName);
                        if (!double.IsNaN(a0) && !double.IsNaN(a1))
                            dimDelta[i] = a1 - a0;
                    }

                    if (c1 != null && p1 != null)
                    {
                        var dc = MathOps.Multiply(c1, MathOps.InvertRigid(c0));
                        var dp = MathOps.Multiply(p1, MathOps.InvertRigid(p0));
                        var rel = MathOps.Multiply(MathOps.InvertRigid(dp), dc);
                        achieved[i] = rotational
                            ? JointClassifier.RotationAbout(axis, rel)
                            : axis[0] * rel[0, 3] + axis[1] * rel[1, 3] + axis[2] * rel[2, 3];
                    }

                    // Undo by what actually happened: a blocked nudge must
                    // not be "reversed" into free territory.
                    if (!double.IsNaN(achieved[i]) && Math.Abs(achieved[i]) > 1e-6)
                        Nudge(mover.Comp, axis, origin,
                            -(moverIsChild ? 1 : -1) * achieved[i], rotational,
                            SwFrames.ToMatrix(mover.Comp.Transform2));

                    _log(string.Format(CultureInfo.InvariantCulture,
                        "limit sign probe {0} ({1}): nudge {2:+0.0000;-0.0000} -> "
                        + "moved {3}, dimension {4}",
                        joint.Id, mate.FeatureName, dir * eps,
                        double.IsNaN(achieved[i]) ? "?"
                            : achieved[i].ToString("+0.0000;-0.0000", CultureInfo.InvariantCulture)
                              + (rotational ? " rad" : " m"),
                        double.IsNaN(dimDelta[i])
                            ? (inSub ? "unreadable in-sub" : "n/a")
                            : dimDelta[i].ToString("+0.0000;-0.0000", CultureInfo.InvariantCulture)));
                }

                // Signal 1: the dimension moved with a real motion.
                double dimTol = Math.Max(1e-7, eps / 20.0);
                for (int i = 0; i < 2; i++)
                {
                    if (double.IsNaN(achieved[i]) || double.IsNaN(dimDelta[i])) continue;
                    if (Math.Abs(achieved[i]) < eps / 2.0) continue;
                    if (Math.Abs(dimDelta[i]) < dimTol) continue;
                    int sign = Math.Sign(dimDelta[i]) * Math.Sign(achieved[i]);
                    _log("limit sign probe " + joint.Id + ": dimension response says sign "
                        + sign.ToString(CultureInfo.InvariantCulture));
                    return sign;
                }

                // Signal 2: at an endpoint, exactly one direction can move,
                // from Min the pair can only take the dimension UP.
                if (atMin ^ atMax)
                {
                    int movedIdx = -1;
                    bool oneBlocked = false;
                    for (int i = 0; i < 2; i++)
                    {
                        if (double.IsNaN(achieved[i]) || Math.Abs(achieved[i]) < eps / 4.0)
                            oneBlocked = true;
                        else if (movedIdx < 0) movedIdx = i;
                        else movedIdx = -2;    // both moved: interior after all
                    }
                    if (movedIdx >= 0 && oneBlocked)
                    {
                        int sign = Math.Sign(achieved[movedIdx]) * (atMin ? 1 : -1);
                        _log("limit sign probe " + joint.Id + ": endpoint asymmetry at "
                            + (atMin ? "Min" : "Max") + " says sign "
                            + sign.ToString(CultureInfo.InvariantCulture));
                        return sign;
                    }
                }

                _log("limit sign probe " + joint.Id + " (" + mate.FeatureName
                    + "): no perturbation produced a readable response");
                return 0;
            }
            catch (Exception ex)
            {
                _log("limit sign probe failed on " + joint.Id + ": " + ex.Message);
                return 0;
            }
            finally
            {
                try
                {
                    RestoreAll(snapshots);
                    var back = SwFrames.ToMatrix(mover.Comp.Transform2);
                    if (back != null)
                    {
                        double drift = Math.Sqrt(
                            (back[0, 3] - m0[0, 3]) * (back[0, 3] - m0[0, 3])
                            + (back[1, 3] - m0[1, 3]) * (back[1, 3] - m0[1, 3])
                            + (back[2, 3] - m0[2, 3]) * (back[2, 3] - m0[2, 3]));
                        if (drift > 1e-6)
                            _log("limit sign probe " + joint.Id + ": " + mover.Id
                                + " rests " + drift.ToString("0.0e0", CultureInfo.InvariantCulture)
                                + " m from where it started");
                    }
                }
                catch (Exception ex)
                {
                    _log("limit sign probe restore failed: " + ex.Message);
                }
            }
        }

        // ── Movers ────────────────────────────────────────────────────────

        /// <summary>One nudge of `amount` about/along the axis, through the
        /// shared mover. Success here only means "a mover ran": the caller
        /// reads the transforms back to see what truly happened.</summary>
        private bool Nudge(
            Component2 comp, double[] axis, double[] origin, double amount,
            bool rotational, double[,] from)
        {
            return _mover.Nudge(comp, axis, origin, amount, rotational, from);
        }

        // ── Model access ────────────────────────────────────────────────────

        private WalkedComponent GroupRep(string groupId)
        {
            foreach (var g in _grouping.Groups)
            {
                if (g.Id != groupId) continue;
                foreach (var cid in g.Components)
                {
                    WalkedComponent w;
                    if (_byId.TryGetValue(cid, out w)) return w;
                }
                return null;
            }
            return null;
        }

        /// <summary>The relative pose delta between the mate's two sides
        /// (flexed instances only), projected on the probe axis: how far
        /// the instance's dimension sits from the recorded rest value.</summary>
        private double RelativeDeltaOnAxis(
            RigJoint joint, GraphMate mate, double[] axis, bool rotational)
        {
            double[,] parentDelta = null, childDelta = null;
            foreach (var e in mate.Entities)
            {
                if (e.ComponentId == null) continue;
                WalkedComponent w;
                if (!_byId.TryGetValue(e.ComponentId, out w)) continue;
                var d = w.Graph.MatePoseDelta;
                if (d == null) continue;
                string g;
                bool childSide = _grouping.ComponentGroup.TryGetValue(e.ComponentId, out g)
                    && g == joint.ChildGroup;
                if (childSide) { if (childDelta == null) childDelta = d; }
                else if (parentDelta == null) parentDelta = d;
            }
            if (parentDelta == null && childDelta == null) return 0.0;
            var rel = childDelta ?? MathOps.Identity4();
            if (parentDelta != null)
                rel = MathOps.Multiply(MathOps.InvertRigid(parentDelta), rel);
            if (rotational) return JointClassifier.RotationAbout(axis, rel);
            return axis[0] * rel[0, 3] + axis[1] * rel[1, 3] + axis[2] * rel[2, 3];
        }

        private static bool IsFixed(Component2 comp)
        {
            try { return comp.IsFixed(); } catch { return false; }
        }

        private IFeature FindMateFeature(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                var f = _assembly.FeatureByName(name) as IFeature;
                if (f != null) return f;
            }
            catch { }
            // Mates live under the MateGroup folder; FeatureByName misses
            // them on some SolidWorks builds, so walk that folder directly.
            try
            {
                for (var f = _model.FirstFeature() as IFeature; f != null;
                     f = f.GetNextFeature() as IFeature)
                {
                    if (f.GetTypeName2() != "MateGroup") continue;
                    for (var sub = f.GetFirstSubFeature() as IFeature; sub != null;
                         sub = sub.GetNextSubFeature() as IFeature)
                        if (string.Equals(sub.Name, name, StringComparison.Ordinal))
                            return sub;
                }
            }
            catch { }
            return null;
        }

        /// <summary>The mate's live dimension. SolidWorks keeps a limit
        /// mate's dimension solved against the current pose, so this is the
        /// measurement the probe compares before/after the nudge.</summary>
        private static double ReadDimension(IFeature feat, string typeName)
        {
            object def = null;
            try { def = feat.GetDefinition(); } catch { }
            if (def == null) return double.NaN;
            try
            {
                if (typeName == "swMateANGLE")
                {
                    var d = def as IAngleMateFeatureData;
                    return d != null ? d.Angle : double.NaN;
                }
                if (typeName == "swMateDISTANCE")
                {
                    var d = def as IDistanceMateFeatureData;
                    return d != null ? d.Distance : double.NaN;
                }
                if (typeName == "swMateHINGE")
                {
                    var d = def as IHingeMateFeatureData;
                    return d != null && d.AngleSelection ? d.Angle : double.NaN;
                }
            }
            catch { }
            return double.NaN;
        }

        private List<KeyValuePair<Component2, MathTransform>> Snapshot()
        {
            return ComponentMover.Snapshot(_byId.Values);
        }

        private void RestoreAll(List<KeyValuePair<Component2, MathTransform>> snaps)
        {
            _mover.RestoreAll(snaps);
        }

    }
}
