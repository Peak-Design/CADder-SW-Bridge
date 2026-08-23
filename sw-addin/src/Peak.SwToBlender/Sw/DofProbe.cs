using System;
using System.Collections.Generic;
using Peak.SwToBlender.Core;
using Peak.SwToBlender.Core.Model;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.SwToBlender.Sw
{
    /// <summary>What one probe found: a joint type name from JointType.*,
    /// the axis and origin when the type has them (global frame), and the
    /// raw status quad for the log.</summary>
    public sealed class ProbeVerdict
    {
        public string Type = JointType.Free;
        public double[] Axis;
        public double[] Origin;

        /// <summary>GetRemainingDOFs return value: the freedoms the solver
        /// could not characterise. Anything above zero downgrades trust.</summary>
        public int UncharacterisedDofs;

        public string RawStatuses;
    }

    /// <summary>
    /// The DOF probe, transcribed from SW2URDF's
    /// EstimateGlobalJointFromComponents / FixComponents / SuppressLimitMates
    /// / UnFixComponents sequence (vendor/sw2urdf/ExportHelperExtension.cs,
    /// "Joint methods" region) and hardened where the original was known to
    /// misbehave.
    ///
    /// NOT CALLED FROM ExportCommand IN M1. The mate-table classifier is the
    /// shipping path; this probe exists so a later milestone can cross-check
    /// the table's verdict (SCHEMA.md: confidence drops to "low" when they
    /// disagree). It mutates model state — fix flags and mate suppression —
    /// which is why everything it changes is restored in a finally block,
    /// fixed state only for components this probe itself fixed.
    ///
    /// Hardening over the original:
    ///   * The original suppressed EVERY mate with MinimumVariation !=
    ///     MaximumVariation anywhere on the child. This probe suppresses only
    ///     genuine limit mates — distance or angle, an actual range, and
    ///     entities spanning the probed pair — so an unrelated limit
    ///     elsewhere in the assembly keeps constraining.
    ///   * The original only recognised one rotation or one translation. This
    ///     probe maps the full status quad to cylindrical, planar and ball as
    ///     well.
    /// </summary>
    public sealed class DofProbe
    {
        private readonly IModelDoc2 _model;
        private readonly IAssemblyDoc _assembly;
        private readonly Action<string> _log;

        public DofProbe(IModelDoc2 model, Action<string> log)
        {
            _model = model;
            _assembly = model as IAssemblyDoc;
            _log = log;
        }

        public ProbeVerdict Probe(Component2 parent, Component2 child)
        {
            var verdict = new ProbeVerdict();
            if (_assembly == null || parent == null || child == null) return verdict;

            List<Component2> fixedByProbe = null;
            List<IFeature> suppressedByProbe = null;
            try
            {
                // Fix the parent and its assembly-tree ancestors so the only
                // freedom the solver can report is the child's freedom
                // relative to the parent. The original fixed the URDF-parent
                // chain; with a plain component pair the assembly ancestors
                // play that role — an unfixed ancestor would let the whole
                // branch drift and the probe would read the branch's freedom
                // instead of the joint's.
                fixedByProbe = FixParentSide(parent);
                suppressedByProbe = SuppressPairLimitMates(parent, child);

                int r1Status, r1DirStatus, r2Status, r2DirStatus;
                int l1Status, l2Status;
                MathPoint rPoint1, rPoint2;
                MathVector rDir1, rDir2, lDir1, lDir2;

                // Undocumented API, found by SW2URDF via
                // https://forum.solidworks.com/thread/57414 — the parameter
                // list here is the one the SolidWorks 2022 interop assembly
                // declares (checked by reflection, IComponent2).
                int remaining = child.GetRemainingDOFs(
                    out r1Status, out rPoint1, out r1DirStatus, out rDir1,
                    out r2Status, out rPoint2, out r2DirStatus, out rDir2,
                    out l1Status, out lDir1,
                    out l2Status, out lDir2);

                verdict.UncharacterisedDofs = remaining;
                verdict.RawStatuses = "R1=" + r1Status + " R2=" + r2Status
                                    + " L1=" + l1Status + " L2=" + l2Status
                                    + " remaining=" + remaining;

                bool r1 = r1Status == 1 && rDir1 != null;
                bool r2 = r2Status == 1 && rDir2 != null;
                bool l1 = l1Status == 1 && lDir1 != null;
                bool l2 = l2Status == 1 && lDir2 != null;

                double[] r1Dir = r1 ? Vec(rDir1) : null;
                double[] r1Pt = r1 && rPoint1 != null ? Vec(rPoint1) : null;
                double[] r2Dir = r2 ? Vec(rDir2) : null;
                double[] r2Pt = r2 && rPoint2 != null ? Vec(rPoint2) : null;
                double[] l1Dir = l1 ? Vec(lDir1) : null;
                double[] l2Dir = l2 ? Vec(lDir2) : null;

                Map(verdict, remaining, r1, r2, l1, l2, r1Dir, r1Pt, r2Dir, r2Pt, l1Dir, l2Dir);
            }
            catch (Exception ex)
            {
                if (_log != null) _log("DOF probe failed: " + ex.Message);
                verdict.Type = JointType.Free;
            }
            finally
            {
                // Restoration order is the reverse of mutation. A restore
                // failure is logged and swallowed: the probe must never leave
                // an exception in flight while the model still carries its
                // temporary state changes.
                Restore(suppressedByProbe, fixedByProbe);
            }
            return verdict;
        }

        // ── Verdict mapping ─────────────────────────────────────────────────

        private static void Map(
            ProbeVerdict verdict, int remaining,
            bool r1, bool r2, bool l1, bool l2,
            double[] r1Dir, double[] r1Pt, double[] r2Dir, double[] r2Pt,
            double[] l1Dir, double[] l2Dir)
        {
            const double parallelTol = 1e-6;

            if (!r1 && !r2 && !l1 && !l2)
            {
                // No characterised freedom. remaining == 0 means genuinely
                // fixed; remaining > 0 means the solver saw freedom it could
                // not describe, and calling that "fixed" would weld a moving
                // pair — the honest answer is free.
                verdict.Type = remaining == 0 ? JointType.Fixed : JointType.Free;
                return;
            }

            if (r1 && !r2 && !l1 && !l2)
            {
                verdict.Type = JointType.Revolute;
                verdict.Axis = MathOps.Normalized(r1Dir);
                verdict.Origin = r1Pt;
                return;
            }

            if (l1 && !l2 && !r1 && !r2)
            {
                verdict.Type = JointType.Prismatic;
                verdict.Axis = MathOps.Normalized(l1Dir);
                return;
            }

            if (r1 && l1 && !r2 && !l2)
            {
                var a = MathOps.Normalized(r1Dir);
                var b = MathOps.Normalized(l1Dir);
                if (MathOps.Norm(MathOps.Cross(a, b)) < parallelTol)
                {
                    verdict.Type = JointType.Cylindrical;
                    verdict.Axis = a;
                    verdict.Origin = r1Pt;
                    return;
                }
                verdict.Type = JointType.Free;
                return;
            }

            if (l1 && l2 && !r2)
            {
                // Two slide directions define a plane; a single rotation is
                // only planar motion when it spins about the plane normal.
                var normal = MathOps.Normalized(MathOps.Cross(l1Dir, l2Dir));
                if (MathOps.Norm(normal) < 0.5)
                {
                    verdict.Type = JointType.Free;    // degenerate directions
                    return;
                }
                if (r1)
                {
                    var a = MathOps.Normalized(r1Dir);
                    if (MathOps.Norm(MathOps.Cross(a, normal)) >= parallelTol)
                    {
                        verdict.Type = JointType.Free;
                        return;
                    }
                }
                verdict.Type = JointType.Planar;
                verdict.Axis = normal;
                verdict.Origin = r1 ? r1Pt : null;
                return;
            }

            if (r1 && r2 && !l1 && !l2)
            {
                // Two rotation axes through one point: a ball. Axes through
                // different points would be a linkage this probe cannot name.
                if (r1Pt != null && r2Pt != null && r2Dir != null)
                {
                    double offAxis = DistancePointToLine(r2Pt, MathOps.Normalized(r1Dir), r1Pt);
                    double offAxis2 = DistancePointToLine(r1Pt, MathOps.Normalized(r2Dir), r2Pt);
                    if (offAxis < 1e-6 || offAxis2 < 1e-6)
                    {
                        verdict.Type = JointType.Ball;
                        verdict.Origin = r1Pt;
                        return;
                    }
                }
                verdict.Type = JointType.Free;
                return;
            }

            verdict.Type = JointType.Free;
        }

        private static double DistancePointToLine(double[] p, double[] dir, double[] pointOnLine)
        {
            var foot = MathOps.ClosestPointOnLineToPoint(p, dir, pointOnLine);
            return Math.Sqrt(MathOps.Distance2(p, foot));
        }

        // ── Fix / suppress / restore ────────────────────────────────────────

        /// <summary>Fixes the parent component and its assembly-tree
        /// ancestors, returning only the ones that were NOT already fixed —
        /// the restore must not unfix a component the user had fixed.</summary>
        private List<Component2> FixParentSide(Component2 parent)
        {
            var chain = new List<Component2>();
            for (var c = parent; c != null; c = SafeParent(c)) chain.Add(c);

            var toUnfix = new List<Component2>();
            foreach (var c in chain)
            {
                bool already = false;
                try { already = c.IsFixed(); } catch { }
                if (!already) toUnfix.Add(c);
            }

            SelectComponents(chain);
            _assembly.FixComponent();
            _model.ClearSelection2(true);
            return toUnfix;
        }

        /// <summary>
        /// Suppresses the limit mates that would stop GetRemainingDOFs from
        /// seeing the pair's freedom — the API call reports a limited joint
        /// as fixed while its limits are active, which is why SW2URDF
        /// suppressed them at all. Only genuine limit mates on THIS pair go:
        /// distance or angle, a real range, and entities on both sides of
        /// the probed pair.
        /// </summary>
        private List<IFeature> SuppressPairLimitMates(Component2 parent, Component2 child)
        {
            var suppressed = new List<IFeature>();
            object[] mates = null;
            try { mates = child.GetMates() as object[]; } catch { }
            if (mates == null) return suppressed;

            foreach (var o in mates)
            {
                var mate = o as IMate2;
                var feat = o as IFeature;
                if (mate == null || feat == null) continue;

                int type = 0;
                try { type = mate.Type; } catch { }
                if (type != (int)swMateType_e.swMateDISTANCE
                    && type != (int)swMateType_e.swMateANGLE) continue;

                double min = 0, max = 0;
                try { min = mate.MinimumVariation; max = mate.MaximumVariation; } catch { }
                if (min == max) continue;               // a value, not a range

                bool alreadySuppressed = false;
                try { alreadySuppressed = feat.IsSuppressed(); } catch { }
                if (alreadySuppressed) continue;        // nothing to undo later

                if (!SpansPair(mate, parent, child)) continue;

                bool ok = false;
                try
                {
                    ok = feat.SetSuppression2(
                        (int)swFeatureSuppressionAction_e.swSuppressFeature,
                        (int)swInConfigurationOpts_e.swThisConfiguration, null);
                }
                catch (Exception ex)
                {
                    if (_log != null) _log("suppress " + feat.Name + ": " + ex.Message);
                }
                if (ok) suppressed.Add(feat);
            }
            return suppressed;
        }

        /// <summary>True when every entity of the mate belongs to the parent
        /// or child side (self or assembly-tree descendant) and both sides
        /// appear — the definition of "this mate limits the probed pair".</summary>
        private static bool SpansPair(IMate2 mate, Component2 parent, Component2 child)
        {
            int count = 0;
            try { count = mate.GetMateEntityCount(); } catch { }
            bool onParent = false, onChild = false;
            for (int i = 0; i < count; i++)
            {
                IMateEntity2 e = null;
                try { e = mate.MateEntity(i); } catch { }
                if (e == null) continue;
                Component2 rc = null;
                try { rc = e.ReferenceComponent; } catch { }
                if (rc == null) continue;    // assembly-level entity: neither side

                if (IsSelfOrDescendant(rc, parent)) { onParent = true; continue; }
                if (IsSelfOrDescendant(rc, child)) { onChild = true; continue; }
                return false;                // a third component: not this pair's limit
            }
            return onParent && onChild;
        }

        private static bool IsSelfOrDescendant(Component2 comp, Component2 ancestor)
        {
            string ancestorName = null;
            try { ancestorName = ancestor.Name2; } catch { }
            if (ancestorName == null) return false;
            for (var c = comp; c != null; c = SafeParent(c))
            {
                string name = null;
                try { name = c.Name2; } catch { }
                if (name != null && string.Equals(name, ancestorName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private void Restore(List<IFeature> suppressedByProbe, List<Component2> fixedByProbe)
        {
            if (suppressedByProbe != null)
            {
                foreach (var feat in suppressedByProbe)
                {
                    try
                    {
                        feat.SetSuppression2(
                            (int)swFeatureSuppressionAction_e.swUnSuppressFeature,
                            (int)swInConfigurationOpts_e.swThisConfiguration, null);
                    }
                    catch (Exception ex)
                    {
                        if (_log != null) _log("unsuppress failed: " + ex.Message);
                    }
                }
            }

            if (fixedByProbe != null && fixedByProbe.Count > 0)
            {
                try
                {
                    SelectComponents(fixedByProbe);
                    _assembly.UnfixComponent();
                    _model.ClearSelection2(true);
                }
                catch (Exception ex)
                {
                    if (_log != null) _log("unfix failed: " + ex.Message);
                }
            }
        }

        /// <summary>Selection helper, from SW2URDF's CommonSwOperations.
        /// SelectComponents: clear, then Select4 with append so the Fix/Unfix
        /// command acts on the whole set at once.</summary>
        private void SelectComponents(List<Component2> components)
        {
            _model.ClearSelection2(true);
            var manager = _model.SelectionManager as ISelectionMgr;
            SelectData data = manager == null ? null : manager.CreateSelectData();
            if (data != null) data.Mark = -1;
            foreach (var c in components)
            {
                try { c.Select4(true, data, false); }
                catch (Exception ex)
                {
                    if (_log != null) _log("select failed: " + ex.Message);
                }
            }
        }

        private static Component2 SafeParent(Component2 comp)
        {
            try { return comp.GetParent(); }
            catch { return null; }
        }

        private static double[] Vec(MathVector v)
        {
            var d = v.ArrayData as double[];
            return d != null && d.Length >= 3 ? new[] { d[0], d[1], d[2] } : null;
        }

        private static double[] Vec(MathPoint p)
        {
            var d = p.ArrayData as double[];
            return d != null && d.Length >= 3 ? new[] { d[0], d[1], d[2] } : null;
        }
    }
}
