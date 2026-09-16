using System;
using System.Collections.Generic;
using System.Globalization;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using SolidWorks.Interop.sldworks;

namespace Peak.Cadder.Sw
{
    /// <summary>
    /// Reads a curve-valued relation off the live model. A cam-follower
    /// mate and a universal joint each tie two joints together by a
    /// function no mate records: the cam's profile, or the yokes' phase
    /// and bend. The solver knows the function, so the probe turns the
    /// driving joint through one revolution in steps, reads the driven
    /// joint at each step, and hands the consumer the table. Every step is
    /// verified by transform read-back, and everything is put back at the
    /// end.
    ///
    /// Until 2026-09-15 the cam relation was "the user's hand" (a warning
    /// and a medium-confidence follower joint) and the universal joint a
    /// 1:1 coupling with its fluctuation noted as missing. Both were the
    /// last mate types the exporter did not model.
    /// </summary>
    internal static class RelationProbe
    {
        /// <summary>Default steps per revolution: 5 degrees, fine enough for a cam
        /// lobe the consumer interpolates linearly between.</summary>
        public const int Steps = 72;

        /// <summary>Attaches a table coupling to the driven joint of every
        /// cam-follower and universal-joint mate the model lets the probe
        /// read. Returns the feature names of the mates now modelled, so
        /// the caller can retire their "not modelled" warnings.</summary>
        public static List<string> Resolve(
            ISldWorks app, IModelDoc2 model, List<WalkedComponent> walked,
            RigidGroupingResult grouping, MateGraph graph, List<RigJoint> joints,
            Action<string> log, int stepDegrees = 5, IDictionary<string, string> unread = null)
        {
            unread = unread ?? new Dictionary<string, string>();
            // Never coarser than 45 degrees, never finer than half a degree:
            // the drag lands short of small steps and stalls the read.
            int steps = Math.Max(8, Math.Min(720, (int)Math.Round(360.0 / Math.Max(1, stepDegrees))));
            var modelled = new List<string>();
            log = log ?? delegate { };
            var mover = new ComponentMover(app, model);
            if (!mover.Ready || graph == null || joints == null) return modelled;
            var byId = new Dictionary<string, WalkedComponent>();
            foreach (var w in walked) if (w.Comp != null) byId[w.Id] = w;

            foreach (var mate in graph.Mates)
            {
                bool cam = MateFacts.Is(mate, "CAMFOLLOWER");
                bool universal = MateFacts.Is(mate, "UNIVERSALJOINT");
                if ((!cam && !universal) || mate.Suppressed) continue;
                string name = mate.FeatureName ?? "?";

                string first, second;
                if (!EntityGroups(mate, grouping, out first, out second))
                {
                    unread[name] = "the mate does not span two rigid groups";
                    log("relation probe " + name + ": the mate does not span two groups");
                    continue;
                }
                string driverGroup = first, drivenGroup = second;
                if (cam)
                {
                    // The cam owns the profile: the side with more of the
                    // mate's entities (a cam is several faces, a follower
                    // one face or a point).
                    int nFirst = 0, nSecond = 0;
                    foreach (var e in mate.Entities)
                    {
                        string g;
                        if (e.ComponentId == null
                            || !grouping.ComponentGroup.TryGetValue(e.ComponentId, out g)) continue;
                        if (g == first) nFirst++; else if (g == second) nSecond++;
                    }
                    if (nSecond > nFirst) { driverGroup = second; drivenGroup = first; }
                }

                RigJoint edge = Between(joints, driverGroup, drivenGroup);
                var driver = JointClassifier.FindMountJoint(
                    joints, driverGroup, new[] { JointType.Revolute, JointType.Cylindrical }, edge);
                var driven = JointClassifier.FindMountJoint(
                    joints, drivenGroup,
                    new[] { JointType.Revolute, JointType.Prismatic, JointType.Cylindrical }, edge);
                if (universal && Hinge(driven) && !Hinge(driver))
                {
                    // A universal joint has no cam side: whichever shaft
                    // has the plain hinge drives. A cylindrical side can
                    // slide away under the drag instead of turning (live
                    // universal joint sample, 2026-09-15).
                    var t = driver; driver = driven; driven = t;
                    var g = driverGroup; driverGroup = drivenGroup; drivenGroup = g;
                }
                if (driver == null || driven == null || ReferenceEquals(driver, driven)
                    || !Turns(driver) || !Moves(driven))
                {
                    unread[name] = cam ? CamReason(driver, driven)
                        : "no driving hinge and driven joint to read ("
                          + Describe(driver) + " / " + Describe(driven) + ")";
                    log("relation probe " + name + ": no driving hinge and driven joint "
                        + "to read (" + Describe(driver) + " / " + Describe(driven) + ")");
                    continue;
                }
                if (driven.Coupling != null && driven.Coupling.Kind != "gear")
                {
                    unread[name] = driven.Id + " already carries a " + driven.Coupling.Kind + " coupling";
                    log("relation probe " + name + ": " + driven.Id + " already carries a "
                        + driven.Coupling.Kind + " coupling");
                    continue;
                }

                JointCoupling table = null;
                // A coupling mate is honoured by the drag solver, not by a
                // rebuild, and which drag mode honours it is not documented:
                // the universal joint sample (2026-09-15) turned its input
                // shaft a full revolution in mode 2 with the output shaft
                // never moving. So a flat reading is retried in the other
                // modes before the relation is given up as unread.
                foreach (int mode in new[] { 2, 0, 1 })
                {
                    mover.DragMode = mode;
                    try
                    {
                        table = Sample(mover, byId, grouping, driver, driven, name, log, steps);
                    }
                    catch (Exception ex)
                    {
                        unread[name] = "the drag failed: " + ex.Message;
                        log("relation probe " + name + " failed: " + ex.Message);
                        table = null;
                    }
                    if (table == null) break;
                    if (!Flat(table)) break;
                    unread[name] = driven.Id + " never moved while " + driver.Id + " was turned";
                    log("relation probe " + name + ": " + driven.Id + " never moved in drag mode "
                        + mode);
                    table = null;
                }
                mover.DragMode = 2;
                if (table == null)
                {
                    if (!unread.ContainsKey(name)) unread[name] = "the drag read nothing";
                    continue;
                }
                unread.Remove(name);

                driven.Coupling = table;
                driven.SourceMates.Add(new SourceMate { SwFeature = mate.FeatureName, Type = mate.TypeName });
                driven.Confidence = "high";
                driven.Notes = Strip(driven.Notes,
                    "universal joint approximated as a 1:1 coupling; the cyclic speed fluctuation is not modelled.");
                driven.Notes = Strip(driven.Notes,
                    "A cam-follower mate rides this pair; the cam relation is not rigged.");
                driven.Notes = Append(driven.Notes, (cam ? "Cam relation" : "Universal joint relation")
                    + " read off the model: " + table.Samples.Length + " point(s) over "
                    + (table.Periodic ? "one revolution of " : "the reachable turn of ")
                    + driver.Id + ".");
                modelled.Add(name);
                log("relation probe " + name + ": " + driven.Id + " follows " + driver.Id
                    + " through " + table.Samples.Length + " point(s)"
                    + (table.Periodic ? ", periodic" : ", not a full turn"));
            }
            return modelled;
        }

        private static JointCoupling Sample(
            ComponentMover mover, Dictionary<string, WalkedComponent> byId,
            RigidGroupingResult grouping, RigJoint driver, RigJoint driven,
            string name, Action<string> log, int steps)
        {
            // A parent with no component is the assembly's own geometry
            // (live cam-follower sample, 2026-09-15: no part is fixed, every
            // part hangs off assembly planes). That ground is the world
            // frame, so a null parent reads as the identity below.
            var dParent = GroupRep(byId, grouping, driver.ParentGroup);
            var dChild = GroupRep(byId, grouping, driver.ChildGroup);
            var fParent = GroupRep(byId, grouping, driven.ParentGroup);
            var fChild = GroupRep(byId, grouping, driven.ChildGroup);
            if (dChild == null || fChild == null)
            {
                log("relation probe " + name + ": a moving group has no component to read");
                return null;
            }
            if (IsFixed(dChild.Comp))
            {
                log("relation probe " + name + ": " + driver.Id + "'s moving body is fixed");
                return null;
            }
            if (driver.Axis == null || driver.Origin == null || driven.Axis == null)
            {
                log("relation probe " + name + ": a joint has no axis to read along");
                return null;
            }

            var axis = MathOps.Normalized(driver.Axis);
            var origin = (double[])driver.Origin.Clone();
            var parentPoseDelta = dParent == null ? null : dParent.Graph.MatePoseDelta;
            if (parentPoseDelta != null)
            {
                axis = MathOps.Normalized(MathOps.RotateVector(parentPoseDelta, axis));
                origin = MathOps.TransformPoint(parentPoseDelta, origin);
            }
            var drivenAxis = MathOps.Normalized(
                driven.Type == JointType.PinSlot && driven.SecondaryAxis != null
                    ? driven.SecondaryAxis : driven.Axis);
            var fPoseDelta = fParent == null ? null : fParent.Graph.MatePoseDelta;
            if (fPoseDelta != null)
                drivenAxis = MathOps.Normalized(MathOps.RotateVector(fPoseDelta, drivenAxis));
            bool drivenTurns = driven.Type != JointType.Prismatic;

            var dP0 = Pose(dParent);
            var dC0 = Pose(dChild);
            var fP0 = Pose(fParent);
            var fC0 = Pose(fChild);
            if (dP0 == null || dC0 == null || fP0 == null || fC0 == null) return null;

            var snapshots = ComponentMover.Snapshot(byId.Values);
            var raw = new List<double[]> { new[] { 0.0, 0.0 } };
            double step = 2.0 * Math.PI / steps;
            double driverTotal = 0.0, driverLast = 0.0;
            double drivenTotal = 0.0, drivenLast = 0.0;
            try
            {
                // Until the driver has come all the way round, not a fixed
                // count of steps: a drag lands short of the asked step more
                // often than not (live cam-follower, 2026-09-15: 72 steps
                // of five degrees reached 302 degrees, so the table missed
                // the lobe on one follower and was not periodic).
                double full = 2.0 * Math.PI - step / 2.0;
                for (int k = 1; k <= 3 * steps && driverTotal < full; k++)
                {
                    var from = SwFrames.ToMatrix(dChild.Comp.Transform2);
                    if (!mover.Nudge(dChild.Comp, axis, origin, step, true, from))
                    {
                        log("relation probe " + name + ": no mover accepted step " + k);
                        break;
                    }
                    double d = Relative(axis, dP0, dC0, dParent, dChild, true);
                    double f = Relative(drivenAxis, fP0, fC0, fParent, fChild, drivenTurns);
                    if (double.IsNaN(d) || double.IsNaN(f)) break;
                    double dInc = Wrap(d - driverLast);
                    driverLast = d;
                    if (Math.Abs(dInc) < step / 4.0)
                    {
                        log(string.Format(CultureInfo.InvariantCulture,
                            "relation probe {0}: {1} stopped after {2:0.000} rad", name, driver.Id, driverTotal));
                        break;
                    }
                    driverTotal += dInc;
                    if (drivenTurns)
                    {
                        drivenTotal += Wrap(f - drivenLast);
                        drivenLast = f;
                    }
                    else drivenTotal = f;
                    raw.Add(new[] { driverTotal, drivenTotal });
                }
            }
            finally
            {
                mover.RestoreAll(snapshots);
                var back = SwFrames.ToMatrix(dChild.Comp.Transform2);
                if (back != null)
                {
                    double drift = Math.Sqrt(
                        (back[0, 3] - dC0[0, 3]) * (back[0, 3] - dC0[0, 3])
                        + (back[1, 3] - dC0[1, 3]) * (back[1, 3] - dC0[1, 3])
                        + (back[2, 3] - dC0[2, 3]) * (back[2, 3] - dC0[2, 3]));
                    if (drift > 1e-6)
                        log("relation probe " + name + ": " + dChild.Id + " rests "
                            + drift.ToString("0.0e0", CultureInfo.InvariantCulture)
                            + " m from where it started");
                }
            }
            if (raw.Count < 3)
            {
                log("relation probe " + name + ": too few readings (" + raw.Count + ")");
                return null;
            }
            return RelationTable.Build(driver.Id, raw, 2.0 * Math.PI, true);
        }

        /// <summary>The child's motion relative to the parent since the
        /// rest read, as a turn about `axis` or a slide along it.</summary>
        private static double Relative(
            double[] axis, double[,] p0, double[,] c0,
            WalkedComponent parent, WalkedComponent child, bool rotational)
        {
            var p1 = Pose(parent);
            var c1 = Pose(child);
            if (p1 == null || c1 == null) return double.NaN;
            var dc = MathOps.Multiply(c1, MathOps.InvertRigid(c0));
            var dp = MathOps.Multiply(p1, MathOps.InvertRigid(p0));
            var rel = MathOps.Multiply(MathOps.InvertRigid(dp), dc);
            if (rotational) return JointClassifier.RotationAbout(axis, rel);
            return axis[0] * rel[0, 3] + axis[1] * rel[1, 3] + axis[2] * rel[2, 3];
        }

        /// <summary>The component's current world pose, or the identity for
        /// the assembly's own geometry (no component).</summary>
        private static double[,] Pose(WalkedComponent w)
        {
            if (w == null) return MathOps.Identity4();
            return SwFrames.ToMatrix(w.Comp.Transform2);
        }

        private static double Wrap(double a)
        {
            while (a > Math.PI) a -= 2.0 * Math.PI;
            while (a <= -Math.PI) a += 2.0 * Math.PI;
            return a;
        }

        private static bool EntityGroups(
            GraphMate mate, RigidGroupingResult grouping, out string first, out string second)
        {
            return JointClassifier.EntityGroups(mate, grouping, out first, out second);
        }

        private static RigJoint Between(List<RigJoint> joints, string a, string b)
        {
            foreach (var j in joints)
                if ((j.ParentGroup == a && j.ChildGroup == b) || (j.ParentGroup == b && j.ChildGroup == a))
                    return j;
            return null;
        }

        /// <summary>Every driven reading is zero: the drag turned the driver
        /// and nothing followed.</summary>
        private static bool Flat(JointCoupling table)
        {
            foreach (var p in table.Samples)
                if (Math.Abs(p[1]) > 1e-7) return false;
            return true;
        }

        /// <summary>Why a cam mate's relation cannot be tabled from these
        /// two joints, for the user: the table is one input to one output,
        /// so the cam must turn about one fixed axis against the follower's
        /// base. A cam left free in its plane (cam-follower2, 2026-09-15)
        /// has three inputs and no table holds it.</summary>
        private static string CamReason(RigJoint driver, RigJoint driven)
        {
            if (driver == null || driven == null || ReferenceEquals(driver, driven))
                return "no cam joint and follower joint to read between";
            if (!Turns(driver))
            {
                if (driver.Type == JointType.Prismatic)
                    return "the cam only slides (" + Describe(driver) + ") and a table is read "
                        + "from a cam that turns about one fixed axis";
                return "the cam is free in more than one direction against the follower's base ("
                    + Describe(driver) + ") and a table holds only a cam that turns about one "
                    + "fixed axis. Mate the cam to a hinge to read it";
            }
            return "the follower's joint (" + Describe(driven) + ") has no single channel to table";
        }

        private static bool Hinge(RigJoint j)
        {
            return j != null && j.Type == JointType.Revolute;
        }

        private static bool Turns(RigJoint j)
        {
            return j != null && (j.Type == JointType.Revolute || j.Type == JointType.Cylindrical);
        }

        private static bool Moves(RigJoint j)
        {
            return j != null && (j.Type == JointType.Revolute || j.Type == JointType.Prismatic
                                 || j.Type == JointType.Cylindrical || j.Type == JointType.PinSlot);
        }

        private static string Describe(RigJoint j)
        {
            return j == null ? "none" : j.Id + " " + j.Type;
        }

        private static WalkedComponent GroupRep(
            Dictionary<string, WalkedComponent> byId, RigidGroupingResult grouping, string groupId)
        {
            foreach (var g in grouping.Groups)
            {
                if (g.Id != groupId) continue;
                foreach (var cid in g.Components)
                {
                    WalkedComponent w;
                    if (byId.TryGetValue(cid, out w)) return w;
                }
                return null;
            }
            return null;
        }

        private static bool IsFixed(Component2 comp)
        {
            try { return comp.IsFixed(); } catch { return false; }
        }

        private static string Append(string notes, string add)
        {
            return string.IsNullOrEmpty(notes) ? add : notes + " " + add;
        }

        private static string Strip(string notes, string remove)
        {
            if (string.IsNullOrEmpty(notes)) return notes;
            string s = notes.Replace(remove, "").Replace("  ", " ").Trim();
            return s.Length == 0 ? null : s;
        }
    }
}
