using System;
using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.Cadder.Sw
{
    /// <summary>What one probe found: a joint type name from JointType.*,
    /// the axis and origin when the type has them (global frame), and the
    /// raw status quad for the log.</summary>
    public sealed class ProbeVerdict
    {
        public string Type = JointType.Free;
        public double[] Axis;
        public double[] Origin;

        /// <summary>The call's return value, which is a swRemainingDofs_e
        /// (Restricted 0, Unrestricted 1, Unavailable 2, Failed 3,
        /// RootComponent 4) and NOT a count of freedoms: a plain hinge has
        /// one degree of freedom and returns Restricted. Anything but 0
        /// means the reading itself is not usable.</summary>
        public int RemainingStatus;

        /// <summary>Every slot came back Unused: the solver found nothing
        /// this body can do. The only reading that may merge a pair.</summary>
        public bool Weldable;

        /// <summary>
        /// Every freedom SolidWorks reported, it also NAMED: each slot was
        /// either Unused or Static, and a Static rotation came with a Static
        /// direction as well as a Static point.
        ///
        /// That last clause is the one that matters. A ball has a fixed
        /// CENTRE and no particular axis, so it reports a Static point with
        /// a direction that is not Static: indistinguishable from a hinge
        /// unless the direction status is read, which it never was (live
        /// corpus 04, 2026-08-25: three ball joints shipped as revolutes
        /// about [0,0,1]). A verdict that is not characterised may be
        /// logged, but it may never override the mate analysis.
        /// </summary>
        public bool Characterised;

        public string RawStatuses;
    }

    /// <summary>
    /// The DOF probe, transcribed from SW2URDF's
    /// EstimateGlobalJointFromComponents / FixComponents / SuppressLimitMates
    /// / UnFixComponents sequence (vendor/sw2urdf/ExportHelperExtension.cs,
    /// "Joint methods" region) and hardened where the original was known to
    /// misbehave.
    ///
    /// The mate-table classifier is the shipping path; the probe cross-checks
    /// it (welds a pair the mates left open, or names a joint the mates could
    /// not). It mutates model state, fix flags and mate suppression, which is
    /// why everything it changes is restored in a finally block, fixed state
    /// only for components this probe itself fixed.
    ///
    /// Hardening over the original:
    ///   * Limit mates. GetRemainingDOFs counts a limit mate as a FIXED
    ///     distance or angle at every position, which is why SW2URDF
    ///     suppressed them at all. The original suppressed every ranged mate
    ///     on the child; a first version of this probe narrowed that to the
    ///     limits spanning the probed pair, so that "an unrelated limit
    ///     elsewhere keeps constraining". That was the wrong way round. A
    ///     limit is never a constraint on freedom, only a bound on it, and
    ///     one anywhere in a closed loop through the child locks the whole
    ///     loop: live TongRig (2026-09-14), a hydraulic tong whose
    ///     cylinder carries the stroke limit, read EVERY pair rigid, the two
    ///     arms hinged on the base included, and the export welded 29 of 31
    ///     components into the ground. Every genuine limit mate in the
    ///     assembly is now suppressed once, for the whole probe session
    ///     (SuppressLimitMates / RestoreLimitMates), and a reading is taken
    ///     against the mates' topology alone.
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

        /// <summary>
        /// swDofStatus_e, which swconst declares and which no method in the
        /// interop is typed with, so it is easy to mistake for a boolean:
        ///
        ///   0 Unused          nothing in this slot
        ///   1 Static          a fixed point / a fixed direction
        ///   2 StaticNormal    TWO translations, in the plane whose normal
        ///                     came back in the slot
        ///   3 Free            real, direction usable, point not unique
        ///   5 Instantaneous   true at this pose only (a coupler travelling
        ///                     along an arc)
        /// </summary>
        public const int StatusUnused = 0;
        public const int StatusStatic = 1;

        /// <summary>
        /// Whether a reading may merge the pair into one body: every slot
        /// empty, and the call itself satisfied (swRemainingDofs_e
        /// Restricted). Anything else in any slot is a freedom SolidWorks
        /// DESCRIBED, and reading it as absence is what welded ten corpus
        /// assemblies solid on 2026-08-25: a puck lying flat on a plate
        /// comes back Rpoint1=Free Tdir1=StaticNormal, which says "spins,
        /// and slides in this plane" as clearly as the API can.
        ///
        /// The two DIRECTION statuses are deliberately not part of this. The
        /// four here are the ones observed across a live production export
        /// (ClampRig: all 33 rigid verdicts empty on all four); the
        /// direction slots have never been recorded, so gating a weld on
        /// them would risk losing one on an assumption.
        /// </summary>
        public static bool IsWeldable(
            int rPoint1, int rPoint2, int tDir1, int tDir2, int remaining)
        {
            return remaining == 0
                && rPoint1 == StatusUnused && rPoint2 == StatusUnused
                && tDir1 == StatusUnused && tDir2 == StatusUnused;
        }

        /// <summary>
        /// Whether every freedom the solver reported, it also NAMED: each
        /// slot either empty or Static, and a rotation Static in its
        /// DIRECTION as well as in its point.
        ///
        /// That last clause is the whole difference between a hinge and a
        /// ball. A ball turns about a fixed CENTRE with no particular axis,
        /// so it reports a Static point and a direction that is not, and
        /// with the direction status thrown away, as it was until
        /// 2026-08-25, the two are indistinguishable. Corpus 04's three ball
        /// joints all shipped as revolutes about [0,0,1].
        /// </summary>
        public static bool IsCharacterised(
            int rPoint1, int rDir1, int rPoint2, int rDir2,
            int tDir1, int tDir2, int remaining)
        {
            if (remaining != 0) return false;
            if (rPoint1 != StatusUnused
                && !(rPoint1 == StatusStatic && rDir1 == StatusStatic)) return false;
            if (rPoint2 != StatusUnused
                && !(rPoint2 == StatusStatic && rDir2 == StatusStatic)) return false;
            if (tDir1 != StatusUnused && tDir1 != StatusStatic) return false;
            if (tDir2 != StatusUnused && tDir2 != StatusStatic) return false;
            return true;
        }

        public ProbeVerdict Probe(Component2 parent, Component2 child)
        {
            var limitsOff = SuppressLimitMates();
            try
            {
                var one = ProbeAgainst(
                    new List<Component2> { parent }, new List<Component2> { child });
                return one.Count > 0 ? one[0] : new ProbeVerdict();
            }
            finally
            {
                RestoreLimitMates(limitsOff);
            }
        }

        /// <summary>
        /// Probes several children against one parent BODY, fixing that body
        /// ONCE for the whole batch. Fixing and unfixing forces a solve of the
        /// entire assembly, and on a production model that solve is the probe's
        /// whole cost (live ClampRig, 2026-08-24: 88 probes, twelve
        /// seconds each, twelve minutes of a nineteen-minute export). One
        /// parent with a dozen children now pays for one solve, not a dozen.
        /// Verdicts come back positionally, one per child.
        ///
        /// The parent arrives as EVERY component of its rigid group, not one
        /// of them. A rigid group is one body by the mate analysis, and
        /// SolidWorks knows nothing of that grouping: fix a single member and
        /// the rest of the body is still free to drift, so what comes back is
        /// the child's freedom relative to a floating parent, which is the
        /// child's freedom, full stop. (Live ClampRig, 2026-08-24: the
        /// ground group's proxy was one bracket out of seventy-four, and all
        /// twenty-five children of that batch came back with the same
        /// uncharacterised remaining=2: the machine body itself was adrift.
        /// Every weld the probe could have found was then rejected, and the
        /// manifest shipped nine bolts with joints of their own.)
        /// </summary>
        public List<ProbeVerdict> ProbeAgainst(
            IList<Component2> parentBody, IList<Component2> children)
        {
            var verdicts = new List<ProbeVerdict>();
            if (children == null) return verdicts;
            if (_assembly == null || parentBody == null || parentBody.Count == 0)
            {
                foreach (var c in children) verdicts.Add(new ProbeVerdict());
                return verdicts;
            }

            List<Component2> fixedByProbe = null;
            try
            {
                // Fix the parent and its assembly-tree ancestors so the only
                // freedom the solver can report is the child's freedom
                // relative to the parent. The original fixed the URDF-parent
                // chain; with a plain component pair the assembly ancestors
                // play that role: an unfixed ancestor would let the whole
                // branch drift and the probe would read the branch's freedom
                // instead of the joint's.
                fixedByProbe = FixParentSide(parentBody);
                foreach (var child in children)
                    verdicts.Add(child == null
                        ? new ProbeVerdict()
                        : ReadPinned(parentBody, child));
            }
            catch (Exception ex)
            {
                if (_log != null) _log("DOF probe batch failed: " + ex.Message);
                while (verdicts.Count < children.Count) verdicts.Add(new ProbeVerdict());
            }
            finally
            {
                Restore(null, fixedByProbe);
            }
            return verdicts;
        }

        /// <summary>One reading, with the parent side already fixed and the
        /// assembly's limit mates already suppressed by the caller.</summary>
        private ProbeVerdict ReadPinned(IList<Component2> parentBody, Component2 child)
        {
            var verdict = new ProbeVerdict();
            try
            {
                int r1Status, r1DirStatus, r2Status, r2DirStatus;
                int l1Status, l2Status;
                MathPoint rPoint1, rPoint2;
                MathVector rDir1, rDir2, lDir1, lDir2;

                // Undocumented API, found by SW2URDF via
                // https://forum.solidworks.com/thread/57414: the parameter
                // list here is the one the SolidWorks 2022 interop assembly
                // declares (checked by reflection, IComponent2).
                int remaining = child.GetRemainingDOFs(
                    out r1Status, out rPoint1, out r1DirStatus, out rDir1,
                    out r2Status, out rPoint2, out r2DirStatus, out rDir2,
                    out l1Status, out lDir1,
                    out l2Status, out lDir2);

                verdict.RemainingStatus = remaining;
                // Every status, under the name the interop gives it. What
                // this used to log as "R1" is Rpoint1_status: the status of
                // the rotation's POINT, not of the rotation, and the two
                // DIRECTION statuses were read into locals and then never
                // used at all. They are the whole difference between a hinge
                // and a ball (2026-08-25).
                verdict.RawStatuses =
                    "Rpoint1=" + r1Status + " Rdir1=" + r1DirStatus
                    + " Rpoint2=" + r2Status + " Rdir2=" + r2DirStatus
                    + " Tdir1=" + l1Status + " Tdir2=" + l2Status
                    + " remaining=" + remaining;

                // swDofStatus_e, which swconst declares and no method in the
                // interop is typed with:
                //
                //   0 Unused          nothing in this slot
                //   1 Static          a fixed point / a fixed direction
                //   2 StaticNormal    TWO translations, in the plane whose
                //                     normal came back in the slot
                //   3 Free            real, direction usable, point not unique
                //   5 Instantaneous   true at this pose only (a coupler
                //                     translating along an arc)
                //
                // So only Unused means "no freedom of this kind". Reading
                // anything else as absence is what welded ten corpus
                // assemblies solid: a puck lying flat on a plate comes back
                // Rpoint1=Free Tdir1=StaticNormal remaining=0, which
                // describes its planar freedom exactly, and it was welded.
                const int Unused = StatusUnused, Static = StatusStatic;

                verdict.Weldable = IsWeldable(
                    r1Status, r2Status, l1Status, l2Status, remaining);
                if (verdict.Weldable
                    && (r1DirStatus != Unused || r2DirStatus != Unused)
                    && _log != null)
                    _log("DOF probe: every point and translation slot is "
                        + "empty but a DIRECTION slot is not (" + verdict.RawStatuses
                        + "); treating it as rigid, which is the first time "
                        + "this combination has been seen");

                // A freedom is NAMED when its point and its direction are
                // both Static: that is a real axis in a real place. A ball
                // has a Static CENTRE and no particular axis, so it comes
                // back Rpoint1=Static with a direction that is not, which
                // is exactly how it was mistaken for a hinge, three times
                // over, in corpus 04.
                bool r1 = r1Status == Static && r1DirStatus == Static && rDir1 != null;
                bool r2 = r2Status == Static && r2DirStatus == Static && rDir2 != null;
                bool l1 = l1Status == Static && lDir1 != null;
                bool l2 = l2Status == Static && lDir2 != null;

                verdict.Characterised = IsCharacterised(
                    r1Status, r1DirStatus, r2Status, r2DirStatus,
                    l1Status, l2Status, remaining);
                // The direction statuses have never appeared in a log: the
                // old code read them into locals and dropped them. So when
                // one is the ONLY reason a reading goes unnamed, say so:
                // that line is the evidence that settles whether a real
                // hinge reports Static here, and it fails in the safe
                // direction meanwhile (the mate analysis stands).
                if (!verdict.Characterised && _log != null
                    && IsCharacterised(r1Status, StatusStatic, r2Status,
                                       StatusStatic, l1Status, l2Status,
                                       remaining))
                    _log("DOF probe: a freedom went unnamed only because its "
                        + "DIRECTION status is not Static (" + verdict.RawStatuses
                        + "); the mate analysis stands. If a plain hinge ever "
                        + "logs this, the direction clause is too strict");

                if (verdict.Weldable)
                {
                    verdict.Type = JointType.Fixed;
                    return verdict;
                }
                if (!verdict.Characterised)
                {
                    // Freedom the probe saw and could not name. The honest
                    // answer is "I do not know", which is what Free means
                    // here: never Fixed, and never adopted over the mates.
                    verdict.Type = JointType.Free;
                    return verdict;
                }

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
                // NOT Fixed. Map is only reached when IsWeldable has already
                // said no, so deciding "fixed" here would contradict the rule
                // applied moments ago, and there is a reading that does
                // exactly that: a Static status whose COM vector came back
                // null leaves every flag false with remaining == 0, which
                // used to weld a hinge. Weldable is the only route to Fixed.
                verdict.Type = JointType.Free;
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

        /// <summary>Fixes every component of the parent body and their
        /// assembly-tree ancestors, returning only the ones that were NOT
        /// already fixed: the restore must not unfix a component the user
        /// had fixed.</summary>
        private List<Component2> FixParentSide(IList<Component2> parentBody)
        {
            var chain = new List<Component2>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in parentBody)
                for (var c = member; c != null; c = SafeParent(c))
                {
                    string name = null;
                    try { name = c.Name2; } catch { }
                    // A name already seen brings its ancestors with it.
                    if (name != null && !seen.Add(name)) break;
                    chain.Add(c);
                }

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
        /// Suppresses every genuine limit mate at the top level of the
        /// assembly, for the whole probe session, and returns the ones it
        /// suppressed so RestoreLimitMates can put them back. Genuine means
        /// a distance or angle mate with a real range; a mate the user has
        /// suppressed is left alone, since there is nothing to undo.
        ///
        /// GetRemainingDOFs counts a limit mate as a fixed dimension at every
        /// position. A limit bounds a freedom, it never removes one, so no
        /// reading taken with a limit active is about the mates' topology,
        /// and the limit does not have to be on the probed pair to spoil the
        /// reading: one anywhere in a closed loop through the child locks
        /// the loop (live TongRig, 2026-09-14: the stroke limit on the
        /// cylinder read both arms rigid against the base they hinge on).
        ///
        /// Top level only, for Probe on one pair. The export takes the limits
        /// out itself before it probes (SolveState), inside flexible
        /// subassemblies too, through each subassembly's own document: a
        /// top-context handle cannot suppress a mate there (live corpus 07).
        /// </summary>
        public List<IFeature> SuppressLimitMates()
        {
            var suppressed = new List<IFeature>();
            if (_assembly == null) return suppressed;
            object[] comps = null;
            try { comps = _assembly.GetComponents(true) as object[]; } catch { }
            if (comps == null) return suppressed;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var co in comps)
            {
                var comp = co as Component2;
                if (comp == null) continue;
                object[] mates = null;
                try { mates = comp.GetMates() as object[]; } catch { }
                if (mates == null) continue;

                foreach (var o in mates)
                {
                    var mate = o as IMate2;
                    var feat = o as IFeature;
                    if (mate == null || feat == null) continue;
                    string name = null;
                    try { name = feat.Name; } catch { }
                    // A mate is reported by every component it touches.
                    if (name != null && !seen.Add(name)) continue;

                    int type = 0;
                    try { type = mate.Type; } catch { }
                    if (type != (int)swMateType_e.swMateDISTANCE
                        && type != (int)swMateType_e.swMateANGLE) continue;

                    double min = 0, max = 0;
                    try { min = mate.MinimumVariation; max = mate.MaximumVariation; } catch { }
                    if (min == max) continue;           // a value, not a range

                    bool alreadySuppressed = false;
                    try { alreadySuppressed = feat.IsSuppressed(); } catch { }
                    if (alreadySuppressed) continue;    // nothing to undo later

                    bool ok = false;
                    try
                    {
                        ok = feat.SetSuppression2(
                            (int)swFeatureSuppressionAction_e.swSuppressFeature,
                            (int)swInConfigurationOpts_e.swThisConfiguration, null);
                    }
                    catch (Exception ex)
                    {
                        if (_log != null) _log("suppress " + name + ": " + ex.Message);
                    }
                    if (ok) suppressed.Add(feat);
                    else if (_log != null)
                        _log("DOF probe: limit mate " + name + " could not be "
                            + "suppressed; every reading in a loop through it "
                            + "will say rigid");
                }
            }
            if (_log != null && suppressed.Count > 0)
                _log("DOF probe: " + suppressed.Count + " limit mate(s) suppressed "
                    + "for the session");
            return suppressed;
        }

        /// <summary>Puts back the limit mates SuppressLimitMates took out.</summary>
        public void RestoreLimitMates(List<IFeature> suppressedByProbe)
        {
            Restore(suppressedByProbe, null);
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
