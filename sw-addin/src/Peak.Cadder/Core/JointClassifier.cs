using System;
using System.Collections.Generic;
using Peak.Cadder.Core.Model;

namespace Peak.Cadder.Core
{
    public sealed class ClassificationResult
    {
        public List<RigJoint> Joints = new List<RigJoint>();
        public List<ManifestWarning> Warnings = new List<ManifestWarning>();

        /// <summary>Component-less rigid groups synthesized during
        /// classification: the carrier links that split a tangent contact's
        /// residual motion into two primitive joints (live corpus 11,
        /// 2026-08-22). They join the manifest's rigid_groups and the loop
        /// analysis exactly like real groups; the Blender side gives them a
        /// bone and simply has no geometry to parent.</summary>
        public List<RigidGroup> VirtualGroups = new List<RigidGroup>();
    }

    /// <summary>
    /// Resolves the axis sign for a limit whose sense the recorded mate
    /// geometry cannot decide (the mate rests at 0/180 deg or with touching
    /// faces). The Sw layer implements this against the live model by
    /// perturbing the child a fraction of the limit span and reading how the
    /// mate dimension responded; headless consumers pass null and keep the
    /// honest "sign is a guess" note.
    /// </summary>
    public interface ILimitSignOracle
    {
        /// <summary>+1 when the mate's dimension grows with positive
        /// right-handed motion about (rotational) or along (translational)
        /// the joint's probe axis, −1 when it shrinks, 0 when the oracle
        /// could not resolve it either.</summary>
        int ResolveSign(RigJoint joint, GraphMate mate, bool rotational);
    }

    /// <summary>
    /// Turns each inter-group edge into the residual freedom its combined mate
    /// set leaves. The table is closed-world: every recognised pattern maps to
    /// one joint type, everything else is exported as "free" with a warning
    /// rather than silently fixed. Format-neutral; no SolidWorks required:
    /// the optional oracle is the one hook a live SolidWorks can plug in.
    /// </summary>
    public static class JointClassifier
    {
        public static ClassificationResult Classify(
            MateGraph graph, RigidGroupingResult grouping,
            ILimitSignOracle signOracle = null)
        {
            var result = new ClassificationResult();

            var groupBoxes = BuildGroupBoxes(graph, grouping);
            var groupAnchors = BuildGroupAnchors(graph, grouping);
            var poseDeltas = new Dictionary<string, double[,]>();
            foreach (var c in graph.Components)
                if (c.MatePoseDelta != null) poseDeltas[c.Id] = c.MatePoseDelta;
            var jointByEdge = new List<RigJoint>();
            int nextId = 1;
            int nextGroupId = NextGroupNumber(grouping.Groups);

            foreach (var edge in grouping.Edges)
            {
                if (TryClassifyContactPair(
                        edge, grouping, groupBoxes, result, ref nextId, ref nextGroupId))
                {
                    jointByEdge.Add(null);
                    continue;
                }
                var joint = ClassifyEdge(
                    edge, grouping, groupBoxes, groupAnchors, poseDeltas,
                    result.Warnings, ref nextId, signOracle);
                jointByEdge.Add(joint);
                if (joint != null) result.Joints.Add(joint);
            }

            BorrowFixedLines(
                grouping, groupBoxes, groupAnchors, poseDeltas, result,
                jointByEdge, signOracle);

            for (int i = 0; i < grouping.Edges.Count; i++)
                AttachCouplings(grouping, grouping.Edges[i], jointByEdge[i], result);

            return result;
        }

        // ── Constraints spread over more than one pair ──────────────────────

        /// <summary>
        /// Finishes a joint whose constraints are spread over more than one
        /// pair of bodies.
        ///
        /// Every joint above is read from the mates between ONE pair, which
        /// is right as long as the mates that hold a body are written against
        /// the body it is jointed to. They often are not. A part is commonly
        /// placed by a couple of mates to assembly planes and then spaced off
        /// some OTHER part's shaft, and then no pair has enough mates on its
        /// own while the two together have plenty.
        ///
        /// Live spurgear.sldasm (2026-09-16, Oscar): "one of the gears comes
        /// in as a revolute, the other is unrestrained". The second gear had
        /// its axis in one assembly plane and its face on another, which
        /// leaves it sliding along the line where those planes meet, and what
        /// stopped that slide was a distance mate to the FIRST GEAR'S axis.
        ///
        /// What makes it sound to move a mate from one pair to another is
        /// that some entities cannot move. A hinge's axis is a line, and
        /// turning the body on it leaves the line exactly where it was, so
        /// that line is as fixed in the body's parent as any assembly plane.
        /// A mate naming it therefore belongs to the parent's pair as much as
        /// to the pair it was written between. Only entities like that are
        /// borrowed, only into a pair that came out free, and only when the
        /// result is a better answer than free: this can add certainty, never
        /// take it away.
        /// </summary>
        private static void BorrowFixedLines(
            RigidGroupingResult grouping,
            Dictionary<string, double[][]> groupBoxes,
            Dictionary<string, double[]> groupAnchors,
            Dictionary<string, double[,]> poseDeltas,
            ClassificationResult result,
            List<RigJoint> jointByEdge,
            ILimitSignOracle signOracle)
        {
            var turning = new Dictionary<string, RigJoint>();
            for (int i = 0; i < grouping.Edges.Count; i++)
            {
                var j = jointByEdge[i];
                if (j == null || j.Axis == null || j.Origin == null) continue;
                if (j.Type != JointType.Revolute && j.Type != JointType.Cylindrical
                    && j.Type != JointType.Screw) continue;
                turning[PairKey(j.ParentGroup, j.ChildGroup)] = j;
            }
            if (turning.Count == 0) return;

            var railed = new HashSet<string>();
            foreach (var e in grouping.Edges)
                foreach (var m in e.Mates)
                {
                    if (!MateFacts.Is(m, "RACKPINION")
                        && !MateFacts.Is(m, "LINEARCOUPLER")) continue;
                    railed.Add(e.GroupA);
                    railed.Add(e.GroupB);
                    foreach (var ent in m.Entities)
                    {
                        string owner;
                        if (ent.ComponentId != null
                            && grouping.ComponentGroup.TryGetValue(
                                ent.ComponentId, out owner))
                            railed.Add(owner);
                    }
                }

            for (int i = 0; i < grouping.Edges.Count; i++)
            {
                var stuck = jointByEdge[i];
                if (stuck == null || stuck.Type != JointType.Free) continue;
                var edge = grouping.Edges[i];
                // A body a rack-and-pinion or linear-coupler mate names is
                // finished from that mate instead: it knows the travel per
                // revolution, and so the slide's direction as well as its
                // line, which beats anything general borrowed from a
                // neighbour. A gear mate says no such thing and does not
                // stand in the way.
                if (railed.Contains(edge.GroupA) || railed.Contains(edge.GroupB))
                    continue;

                // Either end of the stuck pair may be the one that is
                // already placed, so both are tried as the anchor.
                var borrowed = new List<GraphMate>();
                var donors = new List<int>();
                foreach (var ends in new[]
                         {
                             new[] { edge.GroupA, edge.GroupB },
                             new[] { edge.GroupB, edge.GroupA },
                         })
                {
                    string anchor = ends[0], loose = ends[1];
                    for (int d = 0; d < grouping.Edges.Count; d++)
                    {
                        var other = grouping.Edges[d];
                        if (ReferenceEquals(other, edge)) continue;
                        string third;
                        if (other.GroupA == loose) third = other.GroupB;
                        else if (other.GroupB == loose) third = other.GroupA;
                        else continue;
                        if (third == anchor) continue;
                        RigJoint held;
                        if (!turning.TryGetValue(PairKey(anchor, third), out held))
                            continue;

                        foreach (var m in other.Mates)
                        {
                            if (MateFacts.Is(m, "GEAR") || MateFacts.Is(m, "RACKPINION")
                                || MateFacts.Is(m, "LINEARCOUPLER")
                                || MateFacts.Is(m, "UNIVERSALJOINT")
                                || MateFacts.Is(m, "CAMFOLLOWER")
                                || MateFacts.IsLimitMate(m)) continue;
                            if (!NamesTheLine(m, grouping, third, held)) continue;
                            if (borrowed.Contains(m)) continue;
                            borrowed.Add(m);
                            if (!donors.Contains(d)) donors.Add(d);
                        }
                    }
                }
                if (borrowed.Count == 0) continue;

                var wider = new GroupEdge { GroupA = edge.GroupA, GroupB = edge.GroupB };
                wider.Mates.AddRange(edge.Mates);
                wider.Mates.AddRange(borrowed);

                int scratch = 1;
                var trial = new List<ManifestWarning>();
                var better = ClassifyEdge(
                    wider, grouping, groupBoxes, groupAnchors, poseDeltas,
                    trial, ref scratch, signOracle);
                if (better == null || better.Type == JointType.Free) continue;

                // The joint keeps its id: loops, couplings and the manifest
                // all name it, and only what it IS has changed.
                better.Id = stuck.Id;
                foreach (var w in trial)
                    for (int k = 0; k < w.Joints.Count; k++)
                        if (w.Joints[k] != stuck.Id) w.Joints[k] = stuck.Id;
                better.Notes = AppendNote(better.Notes,
                    "completed from " + borrowed.Count + " mate(s) written "
                    + "against another body, on a line that body's own joint "
                    + "cannot move.");
                result.Warnings.RemoveAll(w => w.Joints.Contains(stuck.Id));
                result.Warnings.AddRange(trial);
                int at = result.Joints.IndexOf(stuck);
                if (at >= 0) result.Joints[at] = better;
                else result.Joints.Add(better);
                jointByEdge[i] = better;
                DropSpentDonors(grouping, jointByEdge, result, donors, borrowed);
            }
        }

        /// <summary>
        /// A point on the line the mates name along <paramref name="dir"/>,
        /// or null when they name none. Planes are not lines, however their
        /// normal happens to lie: their point is a point on the plane and
        /// says nothing about where an axis runs.
        /// </summary>
        private static double[] AxisLineAmong(
            List<GraphMate> constraints, double[] dir, HashSet<string> owners)
        {
            var d = MathOps.Normalized(dir);
            if (d == null) return null;
            foreach (var m in constraints)
            {
                foreach (var e in m.Entities)
                {
                    if (e.Direction == null || e.Point == null) continue;
                    if (e.EntityTypeName == "plane") continue;
                    if (owners != null && e.ComponentId != null
                        && !owners.Contains(e.ComponentId)) continue;
                    if (!MateFacts.IsParallel(e.Direction, d)) continue;
                    return new[] { e.Point[0], e.Point[1], e.Point[2] };
                }
            }
            return null;
        }

        /// <summary>
        /// Takes away a joint that has nothing left to say.
        ///
        /// A pair whose every constraint mate has just been read as part of
        /// ANOTHER pair, and which could not be classified on its own, states
        /// nothing that is not stated better elsewhere. Live spurgear.sldasm
        /// (2026-09-16, Oscar): the only mates between the two gears are the
        /// distance that places the second one against the first one's shaft,
        /// now read where it belongs, and the gear mate, which is a coupling
        /// and was never a joint. Left standing, the pair is a free joint
        /// closing a ring that is not a mechanism, and an under-defined
        /// warning about an assembly that is fully defined.
        /// </summary>
        private static void DropSpentDonors(
            RigidGroupingResult grouping, List<RigJoint> jointByEdge,
            ClassificationResult result, List<int> donors, List<GraphMate> borrowed)
        {
            foreach (int d in donors)
            {
                var spent = jointByEdge[d];
                if (spent == null || spent.Type != JointType.Free) continue;
                bool everything = true;
                foreach (var m in grouping.Edges[d].Mates)
                {
                    if (MateFacts.Is(m, "GEAR") || MateFacts.Is(m, "RACKPINION")
                        || MateFacts.Is(m, "LINEARCOUPLER")
                        || MateFacts.Is(m, "UNIVERSALJOINT")
                        || MateFacts.Is(m, "CAMFOLLOWER")
                        || MateFacts.IsLimitMate(m)) continue;
                    if (borrowed.Contains(m)) continue;
                    everything = false;
                    break;
                }
                if (!everything) continue;
                result.Joints.Remove(spent);
                result.Warnings.RemoveAll(w => w.Joints.Contains(spent.Id));
                jointByEdge[d] = null;
            }
        }

        private static string PairKey(string a, string b)
        {
            return string.CompareOrdinal(a, b) <= 0 ? a + "\u0000" + b : b + "\u0000" + a;
        }

        /// <summary>
        /// True when one of the mate's entities belongs to <paramref name="group"/>
        /// and lies exactly on that body's own turning axis, which is the one
        /// line about it that the body's freedom cannot move.
        /// </summary>
        private static bool NamesTheLine(
            GraphMate mate, RigidGroupingResult grouping, string group, RigJoint held)
        {
            var axis = MathOps.Normalized(held.Axis);
            if (axis == null) return false;
            foreach (var e in mate.Entities)
            {
                if (e.ComponentId == null || e.Direction == null || e.Point == null)
                    continue;
                string owner;
                if (!grouping.ComponentGroup.TryGetValue(e.ComponentId, out owner)) continue;
                if (owner != group) continue;
                if (!MateFacts.IsParallel(e.Direction, axis)) continue;
                var onAxis = MathOps.ClosestPointOnLineToPoint(e.Point, axis, held.Origin);
                if (MathOps.Distance2(onAxis, e.Point) > 1e-12) continue;
                return true;
            }
            return false;
        }

        // ── Per-edge classification ─────────────────────────────────────────

        private static RigJoint ClassifyEdge(
            GroupEdge edge, RigidGroupingResult grouping,
            Dictionary<string, double[][]> groupBoxes,
            Dictionary<string, double[]> groupAnchors,
            Dictionary<string, double[,]> poseDeltas,
            List<ManifestWarning> warnings, ref int nextId,
            ILimitSignOracle signOracle)
        {
            var constraints = new List<GraphMate>();
            var limits = new List<GraphMate>();
            var camMates = new List<GraphMate>();
            GraphMate screwMate = null;
            GraphMate pathMate = null;
            GraphMate surfaceMate = null;
            bool couplingOnly = edge.Mates.Count > 0;

            foreach (var m in edge.Mates)
            {
                if (MateFacts.Is(m, "GEAR") || MateFacts.Is(m, "RACKPINION")
                    || MateFacts.Is(m, "LINEARCOUPLER") || MateFacts.Is(m, "UNIVERSALJOINT"))
                    continue;    // annotations on other joints, not constraints here
                couplingOnly = false;
                if (MateFacts.Is(m, "SCREW")) { screwMate = m; continue; }
                if (MateFacts.Is(m, "PATH")) { if (pathMate == null) pathMate = m; continue; }
                if (MateFacts.Is(m, "COINCIDENT") && m.PathPoints != null
                    && m.PathPoints.Length >= 2 && HasFollowerPoint(m))
                {
                    // A vertex coincident WITH A CURVE is a path mate in all
                    // but name: slide along the sampled polyline, rotation
                    // free. MateReader recovered the curve from the entity
                    // reference (live corpus 16 pt4, 2026-08-23: a corner on
                    // an assembly 3D-sketch spline).
                    if (pathMate == null) pathMate = m;
                    continue;
                }
                if (MateFacts.Is(m, "COINCIDENT") && SurfaceEntity(m) != null
                    && HasFollowerPoint(m))
                {
                    // A vertex coincident with a face no joint type models:
                    // a torus, a fillet, a loft. MateReader carried the face's
                    // triangulation, so the consumer can hold the point on it.
                    if (surfaceMate == null) surfaceMate = m;
                    continue;
                }
                if (MateFacts.Is(m, "CAMFOLLOWER")) { camMates.Add(m); continue; }
                if (MateFacts.IsLimitMate(m)) { limits.Add(m); continue; }
                constraints.Add(m);
            }

            // An edge carrying only gear/rack/coupler mates is not a joint at
            // all: the coupled motion lives on the joints that mount the two
            // groups, and AttachCouplings finds those.
            if (couplingOnly) return null;

            var joint = new RigJoint();
            joint.Id = "j" + nextId.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
            nextId++;
            joint.ParentGroup = edge.GroupA;
            joint.ChildGroup = edge.GroupB;
            joint.Confidence = "high";
            foreach (var m in edge.Mates)
                joint.SourceMates.Add(new SourceMate { SwFeature = m.FeatureName, Type = m.TypeName });

            // RigidGrouper must already have merged zero-DOF pairs; a LOCK here
            // means it did not, and the honest output is a fixed joint plus a
            // warning, not a crash in the exporter.
            foreach (var m in constraints)
            {
                if (!MateFacts.IsLock(m)) continue;
                joint.Type = JointType.Fixed;
                joint.Notes = "zero-DOF pair reached the classifier";
                Warn(warnings, "UNCLASSIFIED_PAIR", joint,
                    "Locked pair was not merged by the rigid grouper; exported as a fixed joint.");
                return joint;
            }

            // A path mate IS the joint, the way a hinge is: one slide along
            // an arbitrary curve. SolidWorks exposes NO feature data for it
            // (the *MateFeatureData family simply has no path member), so the
            // curve arrives pre-sampled by MateReader through the mate
            // entities' underlying edges/sketch segments. Without a sampled
            // curve there is nothing a consumer could follow: free plus a
            // warning beats a joint that pretends.
            if (pathMate != null)
            {
                if (pathMate.PathPoints != null && pathMate.PathPoints.Length >= 2)
                {
                    joint.Type = JointType.Path;
                    BuildPathJoint(joint, pathMate);
                    if (constraints.Count > 0)
                    {
                        joint.Confidence = "medium";
                        joint.Notes = AppendNote(joint.Notes,
                            constraints.Count + " other mate(s) on this pair are not combined with the path.");
                    }
                    return joint;
                }
                joint.Type = JointType.Free;
                Warn(warnings, "PATH_UNSAMPLED", joint,
                    "Path mate " + (pathMate.FeatureName ?? "?") + " could not be sampled into a "
                    + "curve (SolidWorks exposes no feature data for path mates and the selected "
                    + "path yielded no usable geometry); the pair is exported free.");
                return joint;
            }

            // Point on a free-form face: two DOF over the surface and all
            // three rotations, which is what SolidWorks gives and what no
            // analytic joint captures. The patch travels with the joint.
            if (surfaceMate != null)
            {
                joint.Type = JointType.Surface;
                BuildSurfaceJoint(joint, surfaceMate);
                if (constraints.Count > 0)
                {
                    joint.Confidence = "medium";
                    joint.Notes = AppendNote(joint.Notes,
                        constraints.Count + " other mate(s) on this pair are not combined "
                        + "with the surface contact.");
                }
                return joint;
            }

            double[] axis, origin, slideDir;
            GraphMate hingeMate;
            int unmodelled;
            // Only the two bodies of this pair (or the assembly itself) may
            // say where their shared axis runs. A mate borrowed from a third
            // body names a line that stands still, which is what made it
            // worth borrowing, and never the line these two turn on.
            var axisOwners = new HashSet<string>();
            foreach (var kv in grouping.ComponentGroup)
                if (kv.Value == edge.GroupA || kv.Value == edge.GroupB)
                    axisOwners.Add(kv.Key);
            string type = ResolveType(
                constraints, screwMate, out axis, out origin, out slideDir,
                out hingeMate, out unmodelled, axisOwners);
            joint.Type = type;

            if (camMates.Count > 0)
            {
                // A cam profile is a curve-valued coupling: the follower's
                // pose is a nonlinear function of the cam angle, which no
                // mate records. The relation probe reads it off the model
                // later, when the cam turns about one fixed axis, and takes
                // this warning away (or fills in why it could not). The
                // joint the OTHER mates make is still right (the follower's
                // pivot or slide).
                joint.Confidence = "medium";
                joint.Notes = AppendNote(joint.Notes,
                    "A cam-follower mate rides this pair; the cam relation is not rigged.");
                Warn(warnings, "CAM_FOLLOWER", joint,
                    "Cam-follower mate " + (camMates[0].FeatureName ?? "?") + " is not rigged: "
                    + "the cam relation was not read off the model. The follower's own joint is "
                    + "exported; pose it by hand to match the cam.");
            }

            if (unmodelled > 0 && joint.Confidence == "high")
            {
                joint.Confidence = "medium";
                joint.Notes = AppendNote(joint.Notes,
                    unmodelled + " mate(s) with no motion model were ignored; the joint may be freer than the assembly.");
            }

            if (type == JointType.Fixed)
            {
                // RigidGrouper runs the same resolver, so this cannot happen
                // unless the two fall out of step; the honest output is a
                // fixed joint plus a loud warning, not a crash.
                Warn(warnings, "UNCLASSIFIED_PAIR", joint,
                    "Zero-DOF pair reached the classifier; RigidGrouper should have merged it. Exported as a fixed joint.");
                return joint;
            }

            if (type == JointType.Free)
            {
                // The residual state travels with the joint so LoopAnalyzer
                // can still model a rotation-locking mate that forms no joint
                // (a parallel mate between two moving links) as a coupling.
                var residual = MotionResolver.Resolve(constraints);
                joint.ResidualKnown = true;
                joint.ResidualRot = residual.Rot;
                joint.ResidualRotDir =
                    residual.Rot == RotFreedom.AboutLine
                    || residual.Rot == RotFreedom.AboutDirection
                        ? residual.RotDir : null;
                Warn(warnings, "UNDER_DEFINED", joint,
                    "Mates between " + edge.GroupA + " and " + edge.GroupB
                    + " leave 3 or more DOF with no recognised pattern; joint exported as free.");
                return joint;
            }

            if (axis != null)
            {
                // Canonical sign: the mate geometry fixes the axis LINE, but
                // its direction along that line depended on entity order and
                // the pose at export, live 2026-08-23, the hinge bone
                // flipped with the export pose. The sign along the line is
                // now a pure function of the line itself; the limit VALUES
                // carry the dimension's sense instead (ReconcileLimitSigns).
                axis = Canonical(MathOps.Threshold(MathOps.Normalized(axis), 1e-11));
                joint.Axis = axis;
                // Pin-slot is the one type whose secondary axis carries
                // meaning: it is the slide direction (SCHEMA.md), not a roll
                // reference the consumer may pick freely.
                joint.SecondaryAxis = slideDir != null
                    ? Canonical(MathOps.Threshold(MathOps.Normalized(slideDir), 1e-11))
                    : SecondaryAxis(axis);
            }
            if (origin != null)
            {
                double[] anchor;
                if ((type == JointType.Prismatic || type == JointType.Planar)
                    && groupAnchors.TryGetValue(edge.GroupB, out anchor))
                    // A prismatic/planar origin is kinematically arbitrary
                    // (no rotation axis line to stay on), and the mate
                    // entities put it wherever the touching faces happen to
                    // sit. Live corpus 02 variants: a free slider's bone
                    // landed on the rail's far corner. The child part's own
                    // origin is the WYSIWYG spot.
                    origin = anchor;
                else
                    origin = SlideTowardChild(origin, axis, groupBoxes, edge.GroupB);
                joint.Origin = MathOps.Threshold(origin, 1e-11);
            }

            GraphMate rotationLimitSource = null;
            GraphMate translationLimitSource = null;

            if (hingeMate != null && hingeMate.MinimumVariation != hingeMate.MaximumVariation)
            {
                joint.RotationLimit = MakeLimit(hingeMate);
                rotationLimitSource = hingeMate;
            }

            foreach (var lm in limits)
            {
                switch (AttachLimit(joint, lm, warnings))
                {
                    case LimitRole.Rotation: rotationLimitSource = lm; break;
                    case LimitRole.Translation: translationLimitSource = lm; break;
                }
            }

            if (type == JointType.Ball && joint.RotationLimit != null
                && rotationLimitSource != null)
                BallConeAxes(joint, rotationLimitSource, grouping, poseDeltas);

            ReconcileLimitSigns(joint, rotationLimitSource, translationLimitSource,
                grouping, poseDeltas, signOracle);
            CorrectLimitsForFlexedPose(
                joint, rotationLimitSource, translationLimitSource, grouping, poseDeltas);

            if (screwMate != null && type == JointType.Screw)
            {
                joint.Coupling = new JointCoupling
                {
                    Kind = "screw",
                    DriverJoint = null,
                    LeadMPerRev = screwMate.LeadMPerRev,
                };
            }

            return joint;
        }

        // ── Contact mates: carrier-link decomposition ───────────────────────

        /// <summary>
        /// A contact mate's residual motion does not fit ONE joint: a
        /// cylinder lying on a plane keeps two slides plus two independent
        /// spins, rim-tangent discs orbit AND spin, a vertex on a face keeps
        /// two slides and a whole ball's rotation. Live corpus 11
        /// (2026-08-22) pinned the first two; the family generalises. The
        /// residual factors exactly into two primitive joints through a
        /// synthesized zero-size carrier link, one primitive per SIDE of the
        /// contact:
        ///   plane        →  planar (its normal)
        ///   cylinder     →  revolute (its axis, the other side orbits it)
        ///   axis/edge under a COINCIDENT  →  prismatic (a point ON a line
        ///                   slides along it; only an offset contact orbits)
        ///   sphere/vertex→  ball (its centre)
        /// chained parent side → carrier → child side. Contacts here are one
        /// TANGENT, one non-limit DISTANCE between curved entities (offset
        /// changes the dimension, never the freedom), or one COINCIDENT of a
        /// point with a face/line. Gates stay strict: no limit mates, and
        /// the REST of the mate set must leave exactly the freedom the
        /// pattern expects. Everything else falls through to the ordinary
        /// path, where the resolver's contact kills narrow what they can and
        /// the rest stays honestly unmodelled.
        /// </summary>
        private static bool TryClassifyContactPair(
            GroupEdge edge, RigidGroupingResult grouping,
            Dictionary<string, double[][]> groupBoxes,
            ClassificationResult result, ref int nextId, ref int nextGroupId)
        {
            GraphMate contact = null;
            var rest = new List<GraphMate>();
            foreach (var m in edge.Mates)
            {
                if (MateFacts.Is(m, "GEAR") || MateFacts.Is(m, "RACKPINION")
                    || MateFacts.Is(m, "LINEARCOUPLER") || MateFacts.Is(m, "UNIVERSALJOINT"))
                    continue;
                if (MateFacts.IsLimitMate(m)) return false;
                if (IsContactMate(m))
                {
                    if (contact != null) return false;
                    contact = m;
                    continue;
                }
                if (MateFacts.Is(m, "SCREW") || MateFacts.Is(m, "HINGE")
                    || MateFacts.Is(m, "SLOT") || MateFacts.IsLock(m)
                    || MateFacts.Is(m, "PATH") || MateFacts.Is(m, "CAMFOLLOWER"))
                    return false;
                rest.Add(m);
            }
            if (contact == null) return false;

            GraphMateEntity ea = null, eb = null;
            foreach (var e in contact.Entities)
            {
                string g = GroupOf(e, grouping);
                if (g == edge.GroupA) { if (ea == null) ea = e; }
                else if (g == edge.GroupB) { if (eb == null) eb = e; }
            }
            if (ea == null || eb == null) return false;

            string kindA = ContactSideKind(ea);
            string kindB = ContactSideKind(eb);
            if (kindA == null || kindB == null) return false;
            if (kindA == "plane" && kindB == "plane") return false;
            // A ball on a ball would need a second spin the chain cannot
            // carry without a redundant DOF; those stay free until a real
            // assembly demands them.
            bool ballA = kindA == "ball", ballB = kindB == "ball";
            if (ballA && ballB) return false;

            var state = MotionResolver.Resolve(rest);
            if (state.Unmodelled > 0) return false;

            if (kindA == "line" && kindB == "line")
            {
                // Orbit + spin needs the face-coincident freedom around it:
                // slide in the discs' plane, spin about the shared direction.
                var a1 = MathOps.Normalized(ea.Direction);
                var a2 = MathOps.Normalized(eb.Direction);
                if (!MateFacts.IsParallel(a1, a2)) return false;
                if (state.TransDim != 2 || state.Rot != RotFreedom.AboutDirection
                    || !MateFacts.IsParallel(state.RotDir, a1)) return false;
            }
            else
            {
                // Every other pattern is the whole story by itself.
                if (rest.Count != 0) return false;
                if ((kindA == "plane" && kindB == "line")
                    || (kindA == "line" && kindB == "plane"))
                {
                    var lineEnt = kindA == "plane" ? eb : ea;
                    var n = MathOps.Normalized((kindA == "plane" ? ea : eb).Direction);
                    var a = MathOps.Normalized(lineEnt.Direction);
                    // Only a side contact splits into planar + spin; a
                    // cylinder standing on its end is a planar joint at
                    // most. A CONE lying tangent holds its axis tilted out
                    // of the plane by exactly the half-angle (live corpus 15
                    // cone3, 2026-08-23), so that is where its gate sits.
                    double tilt = lineEnt.EntityTypeName == "cone" && lineEnt.HalfAngle > 0
                        ? Math.Sin(lineEnt.HalfAngle) : 0.0;
                    if (Math.Abs(Math.Abs(MathOps.Dot(n, a)) - tilt) > 0.02) return false;
                }
            }

            var first = SidePrimitive(contact, ea, eb, ref nextId, out bool okA);
            var second = SidePrimitive(contact, eb, ea, ref nextId, out bool okB);
            if (!okA || !okB) return false;

            string carrier = "g" + nextGroupId.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
            first.ParentGroup = edge.GroupA;
            first.ChildGroup = carrier;
            second.ParentGroup = carrier;
            second.ChildGroup = edge.GroupB;
            AddCarrierGroup(result, carrier, edge, grouping, groupBoxes, second.Origin);

            nextGroupId++;
            result.Joints.Add(first);
            result.Joints.Add(second);
            return true;
        }

        /// <summary>Mates whose whole meaning is a surface/point contact:
        /// tangent; a fixed distance held between curved entities (plane
        /// pairs are ordinary offset planes); a point coincident with a face
        /// or a line.</summary>
        private static bool IsContactMate(GraphMate m)
        {
            if (MateFacts.Is(m, "TANGENT")) return true;
            if (MateFacts.Is(m, "DISTANCE"))
                return !MateFacts.IsLimitMate(m) && MateFacts.Planes(m).Count < 2;
            if (MateFacts.Is(m, "COINCIDENT"))
            {
                bool hasPoint = false, hasSurface = false;
                foreach (var e in m.Entities)
                {
                    if (e.Direction == null && e.Point != null
                        && (e.EntityTypeName == "point" || e.EntityTypeName == "vertex"
                            || e.EntityTypeName == "origin" || e.EntityTypeName == "sphere"))
                        hasPoint = true;
                    else if (e.Direction != null)
                        hasSurface = true;
                }
                return hasPoint && hasSurface;
            }
            return false;
        }

        /// <summary>"plane", "line" (direction-carrying non-plane), "ball"
        /// (sphere or typed point), or null for no rule.</summary>
        private static string ContactSideKind(GraphMateEntity e)
        {
            if (e.Direction != null)
                return e.EntityTypeName == "plane" ? "plane" : "line";
            if (e.Point == null) return null;
            if (e.EntityTypeName == "sphere" || e.EntityTypeName == "point"
                || e.EntityTypeName == "vertex" || e.EntityTypeName == "origin")
                return "ball";
            return null;
        }

        /// <summary>The primitive joint one side of a contact contributes,
        /// parent/child groups left for the caller to wire. `other` supplies
        /// the anchor a planar origin projects.</summary>
        private static RigJoint SidePrimitive(
            GraphMate contact, GraphMateEntity side, GraphMateEntity other,
            ref int nextId, out bool ok)
        {
            ok = true;
            string kind = ContactSideKind(side);
            if (kind == "plane")
            {
                var n = MathOps.Normalized(side.Direction);
                // A cone resting on a plane pivots about its APEX, so both
                // halves of the split anchor there rather than on whatever
                // point the mate entity carried (live corpus 15 cone3: the
                // base-circle centre, 27.5 mm away and 9.4 mm off the plate).
                var anchor = ConeApex(other, side) ?? other.Point ?? side.Point;
                double d = MathOps.Dot(n, new[]
                {
                    anchor[0] - side.Point[0],
                    anchor[1] - side.Point[1],
                    anchor[2] - side.Point[2],
                });
                var origin = new[]
                {
                    anchor[0] - d * n[0],
                    anchor[1] - d * n[1],
                    anchor[2] - d * n[2],
                };
                return MakeCarrierJoint(ref nextId, JointType.Planar, n, origin, contact);
            }
            if (kind == "line")
            {
                if (side.Point == null) { ok = false; return null; }
                if (MateFacts.Is(contact, "COINCIDENT")
                    && side.EntityTypeName == "cylinder" && other.Point != null)
                {
                    // A vertex ON a cylindrical FACE rides the whole
                    // surface: spin about the axis AND slide along it stay
                    // (live corpus 16 pt3, 2026-08-23, the revolute side
                    // pinned the slide SolidWorks allows). Origin at the
                    // vertex's foot on the axis, so the bone sits at the
                    // contact height.
                    var a = MathOps.Normalized(side.Direction);
                    double along = a[0] * (other.Point[0] - side.Point[0])
                                 + a[1] * (other.Point[1] - side.Point[1])
                                 + a[2] * (other.Point[2] - side.Point[2]);
                    var foot = new[]
                    {
                        side.Point[0] + a[0] * along,
                        side.Point[1] + a[1] * along,
                        side.Point[2] + a[2] * along,
                    };
                    return MakeCarrierJoint(
                        ref nextId, JointType.Cylindrical, a, foot, contact);
                }
                var apex = ConeApex(side, other);
                if (apex != null)
                {
                    // A cone lying on a plane turns about its own axis and
                    // precesses about the vertical, and BOTH of those pass
                    // through the apex: where the axis meets the plane. The
                    // axis is negated so it runs from the apex INTO the cone
                    // (SolidWorks reports it pointing the other way), which
                    // puts the bone along the body rather than through the
                    // plate.
                    var into = MathOps.Normalized(side.Direction);
                    return MakeCarrierJoint(
                        ref nextId, JointType.Revolute,
                        new[] { -into[0], -into[1], -into[2] }, apex, contact);
                }
                // A point ON a line (coincident with an axis or edge) slides
                // along it; any offset contact orbits the axis instead.
                bool slides = MateFacts.Is(contact, "COINCIDENT")
                    && (side.EntityTypeName == "axis" || side.EntityTypeName == "edge");
                var origin = slides && other.Point != null ? other.Point : side.Point;
                return MakeCarrierJoint(ref nextId,
                    slides ? JointType.Prismatic : JointType.Revolute,
                    MathOps.Normalized(side.Direction), origin, contact);
            }
            // ball
            return MakeCarrierJoint(ref nextId, JointType.Ball, null, side.Point, contact);
        }

        /// <summary>
        /// Where a cone's axis meets the plane it is lying on: its APEX,
        /// or null when this is not a cone resting on a plane.
        ///
        /// It really is the apex, not a coincidence. The tangency gate has
        /// already established |dot(n, a)| = sin(half-angle), which says the
        /// axis dips by exactly the half-angle; that is the one attitude in
        /// which the lowest generator lies IN the plane, and a cone can only
        /// touch a plane along a generator when its apex is on it. Verified
        /// on live corpus 15 cone3: the intersection lands on the cone
        /// part's own origin to 1e-16 m.
        ///
        /// Everything about the contact turns about that point: the spin
        /// about the cone's own axis and the precession about the vertical
        /// both pass through it, so it is where the joint belongs. The
        /// alternative, the mate entity's own point, is the base-circle
        /// centre: 27.5 mm away and 9.4 mm above the plate.
        /// </summary>
        private static double[] ConeApex(GraphMateEntity cone, GraphMateEntity plane)
        {
            if (cone == null || plane == null) return null;
            if (cone.EntityTypeName != "cone" || cone.HalfAngle <= 0.0) return null;
            if (ContactSideKind(plane) != "plane") return null;
            if (cone.Point == null || cone.Direction == null
                || plane.Point == null || plane.Direction == null) return null;

            var a = MathOps.Normalized(cone.Direction);
            var n = MathOps.Normalized(plane.Direction);
            double denom = MathOps.Dot(n, a);
            // The gate that got us here fixed |denom| at sin(half-angle) > 0,
            // so this is never a grazing intersection, but a cone standing
            // on its base would divide by nothing, and it must fall through
            // to the old anchor instead.
            if (Math.Abs(denom) < 1e-9) return null;

            double t = (n[0] * (plane.Point[0] - cone.Point[0])
                      + n[1] * (plane.Point[1] - cone.Point[1])
                      + n[2] * (plane.Point[2] - cone.Point[2])) / denom;
            return new[]
            {
                cone.Point[0] + t * a[0],
                cone.Point[1] + t * a[1],
                cone.Point[2] + t * a[2],
            };
        }

        private static RigJoint MakeCarrierJoint(
            ref int nextId, string type, double[] axis, double[] origin, GraphMate contact)
        {
            var joint = new RigJoint();
            joint.Id = "j" + nextId.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
            nextId++;
            joint.Type = type;
            if (axis != null)
            {
                joint.Axis = MathOps.Threshold(MathOps.Normalized(axis), 1e-11);
                joint.SecondaryAxis = SecondaryAxis(joint.Axis);
            }
            joint.Origin = MathOps.Threshold((double[])origin.Clone(), 1e-11);
            joint.Confidence = "high";
            joint.Notes = "half of a contact mate, split through a synthesized carrier link";
            joint.SourceMates.Add(new SourceMate { SwFeature = contact.FeatureName, Type = contact.TypeName });
            return joint;
        }

        private static void AddCarrierGroup(
            ClassificationResult result, string id, GroupEdge edge,
            RigidGroupingResult grouping, Dictionary<string, double[][]> groupBoxes,
            double[] at)
        {
            var group = new RigidGroup();
            group.Id = id;
            group.Name = (FindGroupName(grouping, edge.GroupB) ?? edge.GroupB) + "_carrier";
            group.Grounded = false;
            group.Frame = MathOps.Identity4();
            group.Frame[0, 3] = at[0];
            group.Frame[1, 3] = at[1];
            group.Frame[2, 3] = at[2];
            double[][] box;
            if (groupBoxes.TryGetValue(edge.GroupB, out box) && box != null)
            {
                double dx = box[1][0] - box[0][0];
                double dy = box[1][1] - box[0][1];
                double dz = box[1][2] - box[0][2];
                // Half the moving side's diagonal: visibly smaller than the
                // bones it sits between, still proportionate to the mechanism.
                group.BboxDiag = 0.5 * Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            result.VirtualGroups.Add(group);
        }

        private static string FindGroupName(RigidGroupingResult grouping, string groupId)
        {
            foreach (var g in grouping.Groups)
                if (g.Id == groupId) return g.Name;
            return null;
        }

        private static string GroupOf(GraphMateEntity e, RigidGroupingResult grouping)
        {
            if (e.ComponentId == null) return null;
            string g;
            return grouping.ComponentGroup.TryGetValue(e.ComponentId, out g) ? g : null;
        }

        private static int NextGroupNumber(List<RigidGroup> groups)
        {
            int max = -1;
            foreach (var g in groups)
            {
                if (g.Id == null || g.Id.Length < 2 || g.Id[0] != 'g') continue;
                int n;
                if (int.TryParse(g.Id.Substring(1), System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out n) && n > max)
                    max = n;
            }
            return max + 1;
        }

        /// <summary>
        /// Classification by residual motion: MotionResolver intersects the
        /// allowed motion of EVERY constraint mate, and the joint type is
        /// whatever survives: the predecessor's pattern table returned on the
        /// first recognised mate pair and mis-typed any pair with more mates
        /// than the pattern (the live fully-defined hinge came back revolute).
        /// Hinge and slot mates stay special-cased: a hinge IS the joint, and
        /// a slot's travel direction is not derivable from residual motion
        /// alone. Limit mates shape the range, never the type, and coupling
        /// mates never reach here.
        /// </summary>
        private static string ResolveType(
            List<GraphMate> constraints, GraphMate screwMate,
            out double[] axis, out double[] origin, out double[] slideDir,
            out GraphMate hingeMate, out int unmodelled,
            HashSet<string> axisOwners = null)
        {
            axis = null;
            origin = null;
            slideDir = null;
            hingeMate = null;
            unmodelled = 0;

            GraphMate slot = null;
            foreach (var m in constraints)
            {
                if (MateFacts.Is(m, "HINGE") && hingeMate == null) hingeMate = m;
                if (MateFacts.Is(m, "SLOT") && slot == null) slot = m;
            }

            if (hingeMate != null)
            {
                double[] hdir, hpt;
                if (MateFacts.TryGetAxis(hingeMate, out hdir, out hpt))
                {
                    axis = hdir;
                    origin = hpt;
                }
                return JointType.Revolute;
            }

            var state = MotionResolver.Resolve(constraints);
            unmodelled = state.Unmodelled;

            // Plane primitives are kept for origin refinement only: the
            // TYPE decision is the resolver's alone. Symmetric counts: its
            // net effect is a coincidence with the mid-plane, and the plane
            // entities carry the same normal.
            var planes = new List<double[][]>();
            foreach (var m in constraints)
                if (MateFacts.Is(m, "COINCIDENT") || MateFacts.Is(m, "DISTANCE")
                    || MateFacts.Is(m, "SYMMETRIC"))
                    planes.AddRange(MateFacts.Planes(m));

            if (state.IsRigid)
                return JointType.Fixed;

            if (state.TransDim == 0 && state.Rot == RotFreedom.AboutLine)
            {
                axis = state.RotDir;
                origin = state.RotPoint;
                foreach (var pl in planes)
                {
                    if (MateFacts.IsParallel(pl[0], axis))
                    {
                        origin = IntersectPlaneAxis(pl, axis, origin);
                        break;
                    }
                }
                return JointType.Revolute;
            }

            if (state.TransDim == 1 && state.Rot == RotFreedom.None)
            {
                axis = state.TransDirs[0];
                origin = PrismaticOrigin(constraints, planes);
                return JointType.Prismatic;
            }

            if (state.TransDim == 1 && state.Rot == RotFreedom.AboutLine
                && MateFacts.IsParallel(state.RotDir, state.TransDirs[0]))
            {
                axis = state.RotDir;
                origin = state.RotPoint;
                return screwMate != null ? JointType.Screw : JointType.Cylindrical;
            }

            if (state.TransDim == 1 && state.Rot == RotFreedom.AboutLine
                && MateFacts.IsPerpendicular(state.RotDir, state.TransDirs[0]))
            {
                // Spin about one line plus a slide perpendicular to it: a pin
                // in a slot. Live corpus 05 planar2 (2026-08-22): a puck on a
                // plate centred by one width mate slides along the plate and
                // spins about its own cylinder axis.
                axis = state.RotDir;
                origin = state.RotPoint;
                slideDir = state.TransDirs[0];
                return JointType.PinSlot;
            }

            if (state.TransDim == 0 && state.Rot == RotFreedom.AboutPoint)
            {
                origin = state.RotPoint;
                return JointType.Ball;
            }

            if (state.TransDim == 0 && state.Rot == RotFreedom.AboutDirection)
            {
                // Nothing may move and one direction may turn: that is a
                // hinge. The resolver knows the DIRECTION of the turn but
                // not which line it is on, because the mates that killed
                // the last of the translation need not have said. The
                // mates themselves do say: one of them names a line along
                // that direction, and a line is where a body turns.
                //
                // Only a line the PAIR owns counts. Live spurgear.sldasm
                // (2026-09-16, Oscar): the mate that finishes the second
                // gear is a distance to the FIRST gear's axis, and that
                // axis is a line along the same direction sitting 43.18 mm
                // away. Reading the origin off it would hinge the gear
                // about its neighbour.
                var line = AxisLineAmong(constraints, state.RotDir, axisOwners);
                if (line != null)
                {
                    axis = state.RotDir;
                    origin = line;
                    return JointType.Revolute;
                }
            }

            if (state.TransDim == 2 && state.Rot == RotFreedom.AboutDirection
                && planes.Count > 0)
            {
                axis = state.RotDir;
                origin = planes[0][1];
                return JointType.Planar;
            }

            if (slot != null)
            {
                double[] sdir, spt;
                if (MateFacts.TryGetAxis(slot, out sdir, out spt))
                {
                    axis = sdir;
                    origin = spt;
                    // Centered / distance-along / percent-along pin the
                    // component at a spot in the slot (ISlotMateFeatureData.
                    // Constraint): the spin about the pin axis is all that
                    // remains. Only the free slot still slides.
                    return slot.SlotConstraint >= 1 && slot.SlotConstraint <= 3
                        ? JointType.Revolute : JointType.Prismatic;
                }
            }

            if (screwMate != null)
            {
                double[] scdir, scpt;
                if (MateFacts.TryGetAxis(screwMate, out scdir, out scpt))
                {
                    axis = scdir;
                    origin = scpt;
                    return JointType.Screw;
                }
            }

            return JointType.Free;
        }

        /// <summary>A point on the slide line: the first line-like mate's
        /// point when one exists (pin in a slot of planes), else the
        /// intersection of the first non-parallel plane pair, else the first
        /// plane's point.</summary>
        private static double[] PrismaticOrigin(List<GraphMate> constraints, List<double[][]> planes)
        {
            foreach (var m in constraints)
            {
                if (!MateFacts.Is(m, "CONCENTRIC") && !MateFacts.Is(m, "COINCIDENT")) continue;
                if (MateFacts.Planes(m).Count > 0) continue;
                double[] dir, pt;
                if (MateFacts.TryGetAxis(m, out dir, out pt)) return pt;
            }
            for (int i = 1; i < planes.Count; i++)
            {
                if (MateFacts.IsParallel(planes[0][0], planes[i][0])) continue;
                return IntersectPlanes(planes[0], planes[i]);
            }
            return planes.Count > 0 ? planes[0][1] : new double[3];
        }

        // ── Limits ──────────────────────────────────────────────────────────

        private enum LimitRole { None, Rotation, Translation }

        /// <summary>
        /// SW2URDF's admitted bug was attaching a limit mate to whatever DOF
        /// the joint happened to have. A limit only lands here when the joint
        /// has the matching DOF and the mate measures along (distance) or
        /// around (angle) the joint axis; otherwise LIMIT_AXIS_MISMATCH.
        /// Returns which DOF the limit landed on so the caller can orient the
        /// axis for it afterwards.
        /// </summary>
        private static LimitRole AttachLimit(RigJoint joint, GraphMate mate, List<ManifestWarning> warnings)
        {
            double[] dir = null;
            foreach (var e in mate.Entities)
                if (e.Direction != null) { dir = MathOps.Normalized(e.Direction); break; }

            bool isDistance = MateFacts.Is(mate, "DISTANCE");
            var attached = LimitRole.None;

            if (isDistance)
            {
                bool typeOk = joint.Type == JointType.Prismatic
                    || joint.Type == JointType.Cylindrical
                    || joint.Type == JointType.Screw
                    || joint.Type == JointType.PinSlot;
                // The measured direction is the plane normal; along the slide
                // it limits the travel. Plainly-off means closer to
                // perpendicular. A pin-slot slides along its SECONDARY axis.
                var slideAxis = joint.Type == JointType.PinSlot
                    ? joint.SecondaryAxis : joint.Axis;
                bool axisOk = dir == null || slideAxis == null
                    || Math.Abs(MathOps.Dot(dir, slideAxis)) > 0.5;
                if (typeOk && axisOk && joint.TranslationLimit == null)
                {
                    joint.TranslationLimit = MakeLimit(mate);
                    attached = LimitRole.Translation;
                }
            }
            else
            {
                // Ball included: the consumer applies a rotation limit on a
                // ball as a swing cone about the rest pose.
                bool typeOk = joint.Type == JointType.Revolute
                    || joint.Type == JointType.Cylindrical
                    || joint.Type == JointType.Screw
                    || joint.Type == JointType.Ball
                    || joint.Type == JointType.PinSlot;
                // An angle between faces only changes when the faces rotate
                // about the joint axis, which needs their normals off the axis.
                bool axisOk = dir == null || joint.Axis == null
                    || Math.Abs(MathOps.Dot(dir, joint.Axis)) < 0.5;
                if (typeOk && axisOk && joint.RotationLimit == null)
                {
                    joint.RotationLimit = MakeLimit(mate);
                    attached = LimitRole.Rotation;
                }
            }

            if (attached == LimitRole.None)
            {
                Warn(warnings, "LIMIT_AXIS_MISMATCH", joint,
                    "Limit mate " + (mate.FeatureName ?? "?") + " does not fit the "
                    + joint.Type + " DOF of " + joint.Id + "; no limit attached.");
            }
            return attached;
        }

        private static JointLimit MakeLimit(GraphMate mate)
        {
            var limit = new JointLimit();
            limit.Min = mate.MinimumVariation;
            limit.Max = mate.MaximumVariation;
            limit.ValueAtRest = double.IsNaN(mate.CurrentValue) ? 0.0 : mate.CurrentValue;
            return limit;
        }

        /// <summary>
        /// The axis NEVER flips for a limit (it is canonical: a pure
        /// function of the geometry, so the bone direction is identical
        /// across exports no matter the pose; Oscar's invariant,
        /// 2026-08-23). What the mate's dimension sense decides is the limit
        /// VALUES: the manifest expresses limits as signed displacement
        /// about/along the exported axis, so when the dimension grows the
        /// left-handed way the values are mirrored (min,max,rest →
        /// −max,−min,−rest: same physical range, axis-frame numbers).
        /// Each DOF resolves and mirrors independently, which also retires
        /// the old rotation-vs-translation axis conflict. An unresolved
        /// sense (degenerate pose, every rung failed) leaves the values
        /// as read plus the honest note: at worst the limits mirror,
        /// never the bone.
        /// </summary>
        private static void ReconcileLimitSigns(
            RigJoint joint, GraphMate rotationSource, GraphMate translationSource,
            RigidGroupingResult grouping,
            Dictionary<string, double[,]> poseDeltas, ILimitSignOracle oracle)
        {
            if (joint.Axis == null) return;
            // A ball's limit is an UNSIGNED swing band about the cone axis
            // (BallConeAxes): there is no sign to resolve.
            if (joint.Type == JointType.Ball) return;

            bool unresolved = false;
            if (rotationSource != null && joint.RotationLimit != null)
            {
                int s = ResolveSignLadder(joint, rotationSource, grouping,
                                          poseDeltas, oracle, rotational: true);
                if (s < 0) MirrorLimit(joint.RotationLimit);
                else if (s == 0)
                {
                    // Every rung failed: the values ship on the as-read
                    // branch, unless the mate's dimension is FLIPPED, which
                    // is exactly the other branch. The geometric rungs never
                    // see the tick (the flip moves the solved entities, not
                    // the recorded math), and at a readable pose they already
                    // report the flipped sense from the entities themselves,
                    // so the tick applies ONLY here, never as a multiplier
                    // (live corpus 01 hinge5, 2026-08-23: parked at the stop,
                    // flip tick the only difference from hinge).
                    if (rotationSource.DimensionFlipped) MirrorLimit(joint.RotationLimit);
                    unresolved = true;
                }
            }
            if (translationSource != null && joint.TranslationLimit != null)
            {
                int s = ResolveSignLadder(joint, translationSource, grouping,
                                          poseDeltas, oracle, rotational: false);
                if (s < 0) MirrorLimit(joint.TranslationLimit);
                else if (s == 0)
                {
                    if (translationSource.DimensionFlipped) MirrorLimit(joint.TranslationLimit);
                    unresolved = true;
                }
            }
            if (unresolved)
            {
                joint.Confidence = "medium";
                joint.Notes = AppendNote(joint.Notes,
                    "Limit direction could not be resolved from the mate geometry; the sign of the limits is a guess.");
            }
        }

        /// <summary>
        /// A limited ball is a swing cone, and the cone's frame is the limit
        /// mate's own measurement geometry: the parent-side direction is the
        /// cone axis (`axis`), the child-side direction is the vector that
        /// must stay within [Min, Max] of it (`secondary_axis`), and the
        /// limit values are the UNSIGNED angle band between the two. Neither
        /// vector is canonicalized: the sign says which way the cone opens.
        /// Without this the consumer could only fake the cone about the
        /// child's REST pose, which broke the moment the stud exported
        /// tilted (live corpus 04, 2026-08-23: limits rode the tilted rest
        /// instead of the socket's vertical).
        /// </summary>
        private static void BallConeAxes(
            RigJoint joint, GraphMate mate, RigidGroupingResult grouping,
            Dictionary<string, double[,]> poseDeltas)
        {
            double[] coneAxis = null, childDir = null;
            bool transported = false;
            foreach (var e in mate.Entities)
            {
                if (e.Direction == null) continue;
                var d = Transported(MathOps.Normalized(e.Direction), e.ComponentId, poseDeltas);
                transported |= DeltaOf(e.ComponentId, poseDeltas) != null;
                if (SideOf(e, joint, grouping) == joint.ChildGroup)
                {
                    if (childDir == null) childDir = d;
                }
                else if (coneAxis == null) coneAxis = d;
            }
            if (coneAxis == null || childDir == null) return;
            joint.Axis = MathOps.Threshold(coneAxis, 1e-11);
            joint.SecondaryAxis = MathOps.Threshold(childDir, 1e-11);
            // In-sub geometry describes the sub DOCUMENT's pose; when the
            // deltas moved either side, the recorded dimension does not
            // match the instance and the actual angle between the
            // transported directions is the true rest.
            if (transported)
            {
                double c = MathOps.Dot(coneAxis, childDir);
                if (c > 1.0) c = 1.0;
                else if (c < -1.0) c = -1.0;
                joint.RotationLimit.ValueAtRest = Math.Acos(c);
            }
        }

        /// <summary>Same physical range re-expressed for a dimension that
        /// grows the left-handed way about the canonical axis.</summary>
        private static void MirrorLimit(JointLimit limit)
        {
            double min = limit.Min;
            limit.Min = -limit.Max;
            limit.Max = -min;
            limit.ValueAtRest = -limit.ValueAtRest;
        }

        /// <summary>
        /// The deterministic sign for an axis line: a fixed weighted sum
        /// (dominated by X, then Y, then Z) must come out positive. A plain
        /// largest-component rule has cliffs at the 45° diagonals where
        /// export noise could still flip a tilted axis; the weighted sum is
        /// only unstable on a measure-zero great circle no real mate axis
        /// sits on. Pure axis-aligned axes resolve by the lower-order terms
        /// ([0,0,−1] → +Z).
        /// </summary>
        private static double[] Canonical(double[] v)
        {
            double s = v[0] + 1e-4 * v[1] + 1e-8 * v[2];
            if (Math.Abs(s) < 1e-12) return v;
            return s < 0 ? Negate(v) : v;
        }

        /// <summary>
        /// Limit dimensions read through a flexible subassembly's document
        /// report the DOCUMENT pose, not the flexed instance: live corpus 07
        /// flexible-sub2 (2026-08-22): a hinge flexed from 30° to 75° still
        /// exported value_at_rest = 30°, so Blender allowed +45° past the
        /// limit and stopped 30° short of the other end. Each component's
        /// MatePoseDelta says how far its actual pose is from the pose the
        /// mates describe; the relative delta between the limit mate's two
        /// sides, projected on the (already limit-oriented) joint axis, is
        /// exactly the dimension change. Runs after OrientAxisToLimits
        /// because the projection needs the final axis sense.
        /// </summary>
        private static void CorrectLimitsForFlexedPose(
            RigJoint joint, GraphMate rotationSource, GraphMate translationSource,
            RigidGroupingResult grouping, Dictionary<string, double[,]> poseDeltas)
        {
            if (poseDeltas.Count == 0) return;

            if (joint.RotationLimit != null && rotationSource != null && joint.Axis != null
                && joint.Type != JointType.Ball)    // a ball's rest is the swing
            {                                       // angle, set by BallConeAxes
                var rel = RelativePoseDelta(rotationSource, joint, grouping, poseDeltas);
                if (rel != null)
                    joint.RotationLimit.ValueAtRest += RotationAbout(joint.Axis, rel);
            }
            if (joint.TranslationLimit != null && translationSource != null)
            {
                var slideAxis = joint.Type == JointType.PinSlot
                    ? joint.SecondaryAxis : joint.Axis;
                var rel = RelativePoseDelta(translationSource, joint, grouping, poseDeltas);
                if (rel != null && slideAxis != null)
                    // The rotation part of a delta about the slide axis moves
                    // nothing along it, so the translation row is the whole
                    // dimension change.
                    joint.TranslationLimit.ValueAtRest +=
                        slideAxis[0] * rel[0, 3] + slideAxis[1] * rel[1, 3] + slideAxis[2] * rel[2, 3];
            }
        }

        /// <summary>D(parent side)⁻¹ × D(child side) for the mate's entity
        /// components, world frame: the motion the child ACTUALLY has beyond
        /// what the mate geometry describes. Null when both sides sit at
        /// their described poses (the delta would be identity).</summary>
        private static double[,] RelativePoseDelta(
            GraphMate mate, RigJoint joint, RigidGroupingResult grouping,
            Dictionary<string, double[,]> poseDeltas)
        {
            double[,] parentDelta = null, childDelta = null;
            foreach (var e in mate.Entities)
            {
                if (e.ComponentId == null) continue;
                double[,] d;
                if (!poseDeltas.TryGetValue(e.ComponentId, out d)) continue;
                if (SideOf(e, joint, grouping) == joint.ChildGroup)
                {
                    if (childDelta == null) childDelta = d;
                }
                else if (parentDelta == null) parentDelta = d;
            }
            if (parentDelta == null && childDelta == null) return null;
            if (parentDelta == null) return childDelta;
            var inv = MathOps.InvertRigid(parentDelta);
            return childDelta == null ? inv : MathOps.Multiply(inv, childDelta);
        }

        /// <summary>The signed right-handed rotation of a rigid delta about
        /// the given unit axis: transport any perpendicular vector and read
        /// the turn. Only the component about the axis is reported, which is
        /// the joint's own DOF: off-axis parts belong to other joints.
        /// Internal: the Sw limit-sign probe reads its perturbation results
        /// with the same convention.</summary>
        internal static double RotationAbout(double[] axis, double[,] delta)
        {
            var a = MathOps.Normalized(axis);
            // Any unit vector well off the axis seeds the perpendicular.
            double[] seed = Math.Abs(a[0]) < 0.9
                ? new double[] { 1, 0, 0 } : new double[] { 0, 1, 0 };
            double along = MathOps.Dot(seed, a);
            var v = new[] { seed[0] - along * a[0], seed[1] - along * a[1], seed[2] - along * a[2] };
            v = MathOps.Normalized(v);
            var rv = MathOps.RotateVector(delta, v);
            double raLong = MathOps.Dot(rv, a);
            var rp = new[] { rv[0] - raLong * a[0], rv[1] - raLong * a[1], rv[2] - raLong * a[2] };
            if (MathOps.Norm(rp) < 1e-12) return 0.0;
            rp = MathOps.Normalized(rp);
            double cos = MathOps.Dot(v, rp);
            double sin = MathOps.Dot(a, MathOps.Cross(v, rp));
            return Math.Atan2(sin, cos);
        }

        /// <summary>
        /// The rungs of limit-sense resolution, cheapest first: the recorded
        /// mate geometry at the pose the mates describe, the same geometry
        /// transported to the ACTUAL flexed pose (a flexed instance moves the
        /// measurement faces off the degenerate alignment, so the sign
        /// becomes readable, live 2026-08-23: the flexible hinge's sense
        /// depended on where the leaf happened to sit), the flexed-instance
        /// range check, and finally the live oracle. Each returns +1/−1 in
        /// the same convention: the dimension grows with positive
        /// right-handed motion about/along the probe axis, and 0 passes to
        /// the next rung.
        /// </summary>
        private static int ResolveSignLadder(
            RigJoint joint, GraphMate mate, RigidGroupingResult grouping,
            Dictionary<string, double[,]> poseDeltas, ILimitSignOracle oracle,
            bool rotational)
        {
            int sign = rotational
                ? RotationDimensionSign(joint, mate, grouping)
                : TranslationDimensionSign(joint, mate, grouping);
            if (sign == 0 && poseDeltas.Count > 0)
                sign = rotational
                    ? RotationDimensionSign(joint, mate, grouping, poseDeltas)
                    : TranslationDimensionSign(joint, mate, grouping, poseDeltas);
            if (sign == 0)
                sign = FlexedRangeSign(joint, mate, grouping, poseDeltas, rotational);
            if (sign == 0 && oracle != null)
                sign = oracle.ResolveSign(joint, mate, rotational);
            return sign;
        }

        /// <summary>
        /// Sense from a flexed instance, when the doc-pose geometry is
        /// degenerate: the limit dimension at the ACTUAL pose is the recorded
        /// doc-pose value shifted by the pose delta projected on the axis,
        /// and that actual value must lie inside [Min, Max]: the assembly
        /// solver holds it there. When only one axis sense puts it in range,
        /// that sense is proven (live corpus 07, 2026-08-23: doc pose at the
        /// 0° stop, instance flexed +40° in a 0..75° range: the wrong sense
        /// lands at −40°). Both-in-range (a tiny delta, a wide range) stays
        /// unresolved.
        /// </summary>
        private static int FlexedRangeSign(
            RigJoint joint, GraphMate mate, RigidGroupingResult grouping,
            Dictionary<string, double[,]> poseDeltas, bool rotational)
        {
            var limit = rotational ? joint.RotationLimit : joint.TranslationLimit;
            if (limit == null || joint.Axis == null || poseDeltas.Count == 0) return 0;
            var rel = RelativePoseDelta(mate, joint, grouping, poseDeltas);
            if (rel == null) return 0;

            double delta;
            if (rotational)
            {
                delta = RotationAbout(joint.Axis, rel);
            }
            else
            {
                var slideAxis = joint.Type == JointType.PinSlot
                    ? joint.SecondaryAxis : joint.Axis;
                if (slideAxis == null) return 0;
                delta = slideAxis[0] * rel[0, 3] + slideAxis[1] * rel[1, 3]
                      + slideAxis[2] * rel[2, 3];
            }

            double span = Math.Abs(limit.Max - limit.Min);
            double floor = rotational ? 1e-4 : 1e-6;
            if (Math.Abs(delta) < Math.Max(floor, span * 0.01)) return 0;

            double tol = Math.Max(floor, span * 0.005);
            bool keepIn = limit.ValueAtRest + delta >= limit.Min - tol
                       && limit.ValueAtRest + delta <= limit.Max + tol;
            bool flipIn = limit.ValueAtRest - delta >= limit.Min - tol
                       && limit.ValueAtRest - delta <= limit.Max + tol;
            if (keepIn == flipIn) return 0;
            return keepIn ? 1 : -1;
        }

        /// <summary>
        /// +1 when the mate's angle dimension grows with positive right-handed
        /// rotation of the child group about the joint axis, −1 when it
        /// shrinks, 0 when the geometry is degenerate. For measurement-face
        /// normals n1 (parent side) and n2 (child side), the unsigned angle's
        /// derivative with respect to child rotation about axis a has the sign
        /// of a · (n1 × n2): zero exactly when the normals are parallel (the
        /// mate rests at 0/180 deg) or lie on the axis.
        /// </summary>
        private static int RotationDimensionSign(
            RigJoint joint, GraphMate mate, RigidGroupingResult grouping,
            Dictionary<string, double[,]> poseDeltas = null)
        {
            double[] n1 = null, n2 = null;
            double[,] parentDelta = null;
            foreach (var e in mate.Entities)
            {
                if (e.Direction == null) continue;
                var d = MathOps.Normalized(e.Direction);
                // With pose deltas, evaluate at the ACTUAL pose: entity
                // geometry describes the pose the mates were read at (the sub
                // document's, for in-sub mates), and the delta carries each
                // side to where its instance really sits.
                d = Transported(d, e.ComponentId, poseDeltas);
                // A hinge mate's concentric legs run along the axis; only the
                // off-axis angle-reference faces measure the dimension.
                if (Math.Abs(MathOps.Dot(d, joint.Axis)) > 0.5) continue;
                if (SideOf(e, joint, grouping) == joint.ChildGroup)
                {
                    if (n2 == null) n2 = d;
                }
                else if (n1 == null)
                {
                    n1 = d;
                    parentDelta = DeltaOf(e.ComponentId, poseDeltas);
                }
            }
            if (n1 == null || n2 == null) return 0;

            // The axis is parent-side geometry and moves with the parent.
            var axis = parentDelta == null
                ? joint.Axis
                : MathOps.Normalized(MathOps.RotateVector(parentDelta, joint.Axis));
            double s = MathOps.Dot(axis, MathOps.Cross(n1, n2));
            if (Math.Abs(s) < 1e-9) return 0;
            return s > 0 ? 1 : -1;
        }

        private static double[,] DeltaOf(
            string componentId, Dictionary<string, double[,]> poseDeltas)
        {
            if (poseDeltas == null || componentId == null) return null;
            double[,] d;
            return poseDeltas.TryGetValue(componentId, out d) ? d : null;
        }

        private static double[] Transported(
            double[] direction, string componentId,
            Dictionary<string, double[,]> poseDeltas)
        {
            var d = DeltaOf(componentId, poseDeltas);
            if (d == null) return direction;
            return MathOps.Normalized(MathOps.RotateVector(d, direction));
        }

        /// <summary>
        /// +1 when the distance dimension grows as the child group moves along
        /// +axis. D = |(p2 − p1) · n| for parallel measurement faces with
        /// normal n, so the derivative's sign is sign((p2 − p1) · n) · (a · n).
        /// 0 when the faces touch in the rest pose (D = 0: the sense is
        /// unknowable) or the normal is off the axis.
        /// </summary>
        private static int TranslationDimensionSign(
            RigJoint joint, GraphMate mate, RigidGroupingResult grouping,
            Dictionary<string, double[,]> poseDeltas = null)
        {
            double[] n = null;
            double[] p1 = null, p2 = null;
            foreach (var e in mate.Entities)
            {
                if (e.Direction != null && n == null)
                    n = Transported(MathOps.Normalized(e.Direction),
                                    e.ComponentId, poseDeltas);
                if (e.Point == null) continue;
                var delta = DeltaOf(e.ComponentId, poseDeltas);
                var p = delta == null ? e.Point : MathOps.TransformPoint(delta, e.Point);
                if (SideOf(e, joint, grouping) == joint.ChildGroup)
                {
                    if (p2 == null) p2 = p;
                }
                else if (p1 == null) p1 = p;
            }
            if (p1 == null || p2 == null) return 0;
            if (n == null)
            {
                // A point-to-point distance measures no NORMAL: its entities
                // are bare vertices, and the direction slots are filler that
                // MateReader correctly refuses to record. What the dimension
                // grows along is the separation itself: move the child away
                // from the parent point and the number goes up. Live
                // ClampRig (2026-08-24): both hydraulic rams are limited
                // by exactly such a mate, every geometric rung fell through to
                // the probe, and the probe cannot read a dimension inside a
                // flexible subassembly, so both shipped their limits as read,
                // which is right for one ram and mirrored for the other.
                var apart = new[] { p2[0] - p1[0], p2[1] - p1[1], p2[2] - p1[2] };
                if (MathOps.Norm(apart) < 1e-9) return 0;
                n = MathOps.Normalized(apart);
            }

            // A pin-slot's slide is its secondary axis; every other limited
            // type slides along the axis itself.
            var slideAxis = joint.Type == JointType.PinSlot
                ? joint.SecondaryAxis : joint.Axis;
            double along = MathOps.Dot(slideAxis, n);
            if (Math.Abs(along) < 0.5) return 0;
            double sep = (p2[0] - p1[0]) * n[0] + (p2[1] - p1[1]) * n[1] + (p2[2] - p1[2]) * n[2];
            if (Math.Abs(sep) < 1e-9) return 0;
            return Math.Sign(sep) * along > 0 ? 1 : -1;
        }

        /// <summary>Assembly-frame references (null or unknown component)
        /// act as the parent side: they are ground to the moving child.</summary>
        private static string SideOf(GraphMateEntity e, RigJoint joint, RigidGroupingResult grouping)
        {
            if (e.ComponentId == null) return joint.ParentGroup;
            string g;
            return grouping.ComponentGroup.TryGetValue(e.ComponentId, out g) ? g : joint.ParentGroup;
        }

        private static double[] Negate(double[] v)
        {
            return new[] { -v[0], -v[1], -v[2] };
        }

        private static string AppendNote(string notes, string add)
        {
            return string.IsNullOrEmpty(notes) ? add : notes + " " + add;
        }

        // ── Couplings ───────────────────────────────────────────────────────

        private static void AttachCouplings(
            RigidGroupingResult grouping, GroupEdge edge, RigJoint edgeJoint,
            ClassificationResult result)
        {
            foreach (var mate in edge.Mates)
            {
                bool gear = MateFacts.Is(mate, "GEAR");
                bool rack = MateFacts.Is(mate, "RACKPINION");
                bool coupler = MateFacts.Is(mate, "LINEARCOUPLER");
                bool universal = MateFacts.Is(mate, "UNIVERSALJOINT");
                if (rack) gear = false;    // "RACKPINION" would otherwise be read again below
                if (!gear && !rack && !coupler && !universal) continue;

                string firstGroup, secondGroup;
                if (!EntityGroups(mate, grouping, out firstGroup, out secondGroup))
                {
                    WarnCoupling(result, edgeJoint, mate);
                    continue;
                }

                string driverGroup = firstGroup;
                string drivenGroup = secondGroup;
                string[] driverPrefer = new[] { JointType.Revolute };
                string[] drivenPrefer = new[] { JointType.Revolute };
                if (rack)
                {
                    // The pinion side owns the cylindrical entity; the rack is
                    // the driven, translating side.
                    string pinionGroup = CylinderEntityGroup(mate, grouping);
                    if (pinionGroup != null && pinionGroup == secondGroup)
                    {
                        driverGroup = secondGroup;
                        drivenGroup = firstGroup;
                    }
                    drivenPrefer = new[] { JointType.Prismatic };
                }
                else if (coupler)
                {
                    driverPrefer = new[] { JointType.Prismatic, JointType.Cylindrical };
                    drivenPrefer = new[] { JointType.Prismatic, JointType.Cylindrical };
                }

                var driver = FindMountJoint(result.Joints, driverGroup, driverPrefer, edgeJoint);
                var driven = FindMountJoint(result.Joints, drivenGroup, drivenPrefer, edgeJoint);
                if (driver == null || driven == null || driver == driven)
                {
                    WarnCoupling(result, edgeJoint, mate);
                    continue;
                }

                var coupling = new JointCoupling();
                coupling.DriverJoint = driver.Id;
                if (gear)
                {
                    coupling.Kind = "gear";
                    coupling.Ratio = SignedPairRatio(
                        mate, driverGroup == firstGroup, driver, driven, gear: true);
                }
                else if (universal)
                {
                    // A universal joint averages to a 1:1 rotation transfer;
                    // the within-revolution fluctuation (cos of the bend
                    // angle) has no bone-driver expression, and posing does
                    // not miss it. Sign convention unpinned live: the axes'
                    // dot decides, straight-shaft-transfers-same-way.
                    coupling.Kind = "gear";
                    double s = driver.Axis != null && driven.Axis != null
                        && MathOps.Dot(driver.Axis, driven.Axis) < 0 ? -1.0 : 1.0;
                    coupling.Ratio = s;
                    driven.Confidence = "medium";
                    driven.Notes = AppendNote(driven.Notes,
                        "universal joint approximated as a 1:1 coupling; the cyclic speed fluctuation is not modelled.");
                }
                else if (rack)
                {
                    // The slide the rack-pinion mate itself states, where
                    // nothing else stated it. See RackSlide.
                    RackSlide(result, mate, driver, driven);
                    coupling.Kind = "rack_pinion";
                    coupling.MetersPerRadian = SignedRackRatio(mate, driver, driven);
                }
                else
                {
                    coupling.Kind = "linear_coupler";
                    coupling.Ratio = SignedPairRatio(
                        mate, driverGroup == firstGroup, driver, driven, gear: false);
                }
                driven.Coupling = coupling;
                driven.SourceMates.Add(new SourceMate { SwFeature = mate.FeatureName, Type = mate.TypeName });
            }
        }

        /// <summary>
        /// The signed driven-per-driver ratio the Blender driver applies to
        /// bone-local channels. Everything here was pinned on live corpus 08
        /// (2026-08-22, gear-pair): the mate's numerator:denominator is the
        /// ANGULAR ratio θ(entity1):θ(entity2): the large gear (entity 1,
        /// r=20 mm) carried num/den = 0.5 against the small one, so the
        /// driven side follows at den/num when the driver is entity 1 and at
        /// num/den when the sides are swapped. An UN-reversed gear mate
        /// counter-rotates (external mesh; the live pair rotated the same way
        /// in Blender until this sign), and the world-frame sign converts to
        /// bone-local through the two mount joints' axis senses. The linear
        /// coupler shares the number layout; its default direction is taken
        /// as same-way (no live sample yet: the read-time log settles it
        /// when one arrives).
        /// </summary>
        private static double? SignedPairRatio(
            GraphMate mate, bool driverIsFirstEntity, RigJoint driver, RigJoint driven, bool gear)
        {
            double magnitude;
            if (mate.CouplingNumerator.HasValue && mate.CouplingDenominator.HasValue
                && Math.Abs(mate.CouplingNumerator.Value) > 1e-12
                && Math.Abs(mate.CouplingDenominator.Value) > 1e-12)
            {
                magnitude = driverIsFirstEntity
                    ? Math.Abs(mate.CouplingDenominator.Value / mate.CouplingNumerator.Value)
                    : Math.Abs(mate.CouplingNumerator.Value / mate.CouplingDenominator.Value);
            }
            else if (mate.CouplingRatio.HasValue)
            {
                // Legacy single-number recordings (old fixtures): keep them
                // usable, magnitude only: the sign logic below still applies.
                magnitude = Math.Abs(mate.CouplingRatio.Value);
            }
            else
            {
                return null;
            }

            double worldSign = gear
                ? (mate.CouplingReverse ? 1.0 : -1.0)
                : (mate.CouplingReverse ? -1.0 : 1.0);

            // Bone-local channels measure motion about each joint's OWN axis;
            // anti-parallel axes flip the world-frame relation once more.
            double axisSign = 1.0;
            if (driver.Axis != null && driven.Axis != null
                && MathOps.Dot(driver.Axis, driven.Axis) < 0)
                axisSign = -1.0;

            return worldSign * axisSign * magnitude;
        }

        /// <summary>
        /// Bone-local signed metres of rack per radian of pinion.
        ///
        /// The mate states the relation in its own two entities' senses:
        /// the rack travels along its selected edge as the pinion turns
        /// about its selected axis, and the Reverse tick picks which way.
        /// The reader folds Reverse into MetersPerRadian, so the only work
        /// left is to carry that number from the mate's two senses into the
        /// two bones' own senses.
        ///
        /// Pinned live on the SolidWorks sample "rack and pinion.sldasm"
        /// (2026-09-16): the pinion entity points down -Z and its bone up
        /// +Z, the rack entity and its bone both run -Y, and Reverse is
        /// ticked. One flip, so +12.7 mm per radian, and the rack then
        /// runs the way it runs in SolidWorks. This code read the sign off
        /// the rolling contact before that (the pinion axis crossed into
        /// the offset to the rack) and fought the tick: the two answers
        /// differ whenever the tick is set, because SolidWorks does not use
        /// the rolling sense as its unticked default.
        /// </summary>
        private static double? SignedRackRatio(
            GraphMate mate, RigJoint driver, RigJoint driven)
        {
            if (!mate.MetersPerRadian.HasValue) return null;
            double value = mate.MetersPerRadian.Value;

            // The reader writes the rack first and the pinion second, as
            // the mate lists them. A cylinder in the first slot means the
            // pair arrived the other way round.
            GraphMateEntity rackSide = null, pinion = null;
            if (mate.Entities.Count >= 2)
            {
                rackSide = mate.Entities[0];
                pinion = mate.Entities[1];
                if (rackSide.EntityTypeName == "cylinder"
                    && pinion.EntityTypeName != "cylinder")
                {
                    var swap = rackSide; rackSide = pinion; pinion = swap;
                }
            }

            if (pinion != null && pinion.Direction != null && driver.Axis != null
                && MathOps.Dot(driver.Axis, MathOps.Normalized(pinion.Direction)) < 0)
                value = -value;
            if (rackSide != null && rackSide.Direction != null && driven.Axis != null
                && MathOps.Dot(driven.Axis, MathOps.Normalized(rackSide.Direction)) < 0)
                value = -value;
            return value;
        }

        /// <summary>Fills a path joint from the sampled curve: origin at the
        /// follower's rest point, axis along the local tangent, so at rest
        /// the bone behaves like the prismatic it locally is.</summary>
        private static void BuildPathJoint(RigJoint joint, GraphMate pathMate)
        {
            var pts = pathMate.PathPoints;
            joint.PathPoints = pts;
            joint.PathClosed = pathMate.PathClosed;

            double[] follower = null;
            foreach (var e in pathMate.Entities)
                if (e.Direction == null && e.Point != null
                    && e.EntityTypeName != "curve")    // the sampled side is
                { follower = e.Point; break; }         // the track, not the rider

            int seg = 0;
            if (follower != null)
            {
                double best = double.MaxValue;
                for (int i = 0; i + 1 < pts.Length; i++)
                {
                    double d = MathOps.Distance2(follower, pts[i]);
                    if (d < best) { best = d; seg = i; }
                }
            }
            double[] tangent = null;
            for (int i = seg; i + 1 < pts.Length && tangent == null; i++)
            {
                var t = new[]
                {
                    pts[i + 1][0] - pts[i][0],
                    pts[i + 1][1] - pts[i][1],
                    pts[i + 1][2] - pts[i][2],
                };
                if (MathOps.Norm(t) > 1e-9) tangent = MathOps.Normalized(t);
            }
            joint.Axis = tangent ?? new double[] { 1, 0, 0 };
            joint.Axis = MathOps.Threshold(joint.Axis, 1e-11);
            joint.SecondaryAxis = SecondaryAxis(joint.Axis);
            joint.Origin = MathOps.Threshold(
                (double[])(follower ?? pts[0]).Clone(), 1e-11);
            joint.Notes = AppendNote(joint.Notes,
                "path mate: the child slides along the sampled curve; pitch/yaw/roll "
                + "options are not readable (SolidWorks exposes no path mate feature data).");
        }

        /// <summary>Fills a surface joint from the carried patch: origin at
        /// the follower's rest point on the face, axis along the local
        /// surface normal, so the rest frame reads like the planar contact
        /// it locally is.</summary>
        private static void BuildSurfaceJoint(RigJoint joint, GraphMate surfaceMate)
        {
            var patch = SurfaceEntity(surfaceMate);
            joint.SurfacePoints = patch.SurfacePoints;
            joint.SurfaceTriangles = patch.SurfaceTriangles;

            double[] follower = null;
            foreach (var e in surfaceMate.Entities)
                if (e.Direction == null && e.Point != null
                    && e.EntityTypeName != "surface")
                { follower = e.Point; break; }

            joint.Origin = MathOps.Threshold(
                (double[])(follower ?? patch.SurfacePoints[0]).Clone(), 1e-11);
            joint.Axis = MathOps.Threshold(
                NearestTriangleNormal(patch, joint.Origin), 1e-11);
            joint.SecondaryAxis = SecondaryAxis(joint.Axis);
            joint.Notes = AppendNote(joint.Notes,
                "surface contact: the child's point stays on the triangulated face, "
                + "free to slide across it and to rotate; the patch approximates the "
                + "real surface to SolidWorks' tessellation tolerance.");
        }

        /// <summary>The normal of the patch triangle nearest a point: the
        /// rest frame's up. Nearest by CENTROID: a contact point sits on a
        /// triangle it is nearly coplanar with, so plane distance cannot
        /// separate the candidates but centroid distance can.</summary>
        private static double[] NearestTriangleNormal(GraphMateEntity patch, double[] p)
        {
            var pts = patch.SurfacePoints;
            double best = double.MaxValue;
            double[] normal = null;
            foreach (var t in patch.SurfaceTriangles)
            {
                double[] a = pts[t[0]], b = pts[t[1]], c = pts[t[2]];
                var centroid = new[]
                {
                    (a[0] + b[0] + c[0]) / 3.0,
                    (a[1] + b[1] + c[1]) / 3.0,
                    (a[2] + b[2] + c[2]) / 3.0,
                };
                double d = MathOps.Distance2(p, centroid);
                if (d >= best) continue;
                var n = MathOps.Cross(
                    new[] { b[0] - a[0], b[1] - a[1], b[2] - a[2] },
                    new[] { c[0] - a[0], c[1] - a[1], c[2] - a[2] });
                if (MathOps.Norm(n) < 1e-15) continue;
                best = d;
                normal = MathOps.Normalized(n);
            }
            return normal ?? new double[] { 0, 0, 1 };
        }

        /// <summary>The side carrying a triangulated face, if any.</summary>
        private static GraphMateEntity SurfaceEntity(GraphMate m)
        {
            foreach (var e in m.Entities)
                if (e.SurfaceTriangles != null && e.SurfaceTriangles.Length > 0
                    && e.SurfacePoints != null && e.SurfacePoints.Length >= 3)
                    return e;
            return null;
        }

        /// <summary>A direction-less point-carrying entity: the follower a
        /// point-on-curve coincidence moves along the sampled path.</summary>
        private static bool HasFollowerPoint(GraphMate m)
        {
            foreach (var e in m.Entities)
                if (e.Direction == null && e.Point != null
                    && e.EntityTypeName != "curve" && e.EntityTypeName != "surface")
                    return true;
            return false;
        }

        /// <summary>First-entity component's group, then the first group that
        /// differs. The mate author picked the driver first; that convention
        /// is all the recorded graph carries.</summary>
        internal static bool EntityGroups(
            GraphMate mate, RigidGroupingResult grouping, out string first, out string second)
        {
            first = null;
            second = null;
            foreach (var e in mate.Entities)
            {
                if (e.ComponentId == null) continue;
                string g;
                if (!grouping.ComponentGroup.TryGetValue(e.ComponentId, out g)) continue;
                if (first == null) { first = g; continue; }
                if (g != first) { second = g; return true; }
            }
            return false;
        }

        private static string CylinderEntityGroup(GraphMate mate, RigidGroupingResult grouping)
        {
            foreach (var e in mate.Entities)
            {
                if (e.EntityTypeName != "cylinder" || e.ComponentId == null) continue;
                string g;
                if (grouping.ComponentGroup.TryGetValue(e.ComponentId, out g)) return g;
            }
            return null;
        }

        /// <summary>The joint that mounts a group to the rest of the rig:
        /// prefer joints where the group is the child, prefer the wanted
        /// types, then lowest id. The coupling edge's own joint (if any) is
        /// excluded: a gear mate never drives itself.</summary>
        internal static RigJoint FindMountJoint(
            List<RigJoint> joints, string groupId, string[] preferTypes, RigJoint exclude)
        {
            RigJoint best = null;
            int bestScore = int.MaxValue;
            foreach (var j in joints)
            {
                if (j == exclude) continue;
                bool child = j.ChildGroup == groupId;
                bool parent = j.ParentGroup == groupId;
                if (!child && !parent) continue;
                int score = (child ? 0 : 2) + (Array.IndexOf(preferTypes, j.Type) >= 0 ? 0 : 1);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = j;
                }
            }
            return best;
        }

        /// <summary>
        /// Gives the rack the slide the rack-pinion mate states, when the
        /// rack's own mates did not state one.
        ///
        /// A rack is usually held by nothing but the pinion it runs on: in
        /// the SolidWorks sample (live "rack and pinion", 2026-09-16) its
        /// mates are one plane coincident and one distance to the PINION,
        /// plus a parallel to an assembly plane. Against ground that leaves
        /// four freedoms and against the pinion two, so neither pair is a
        /// pattern the resolver knows, and both came out free: a rack that
        /// could be dragged anywhere, with a coupling hanging off it doing
        /// nothing (Oscar: "they do not interact").
        ///
        /// Nothing pairwise can see the slide, because the evidence is
        /// split across two pairs. The MATE can: a rack-pinion mate says
        /// the rack translates along its own entity's direction and nothing
        /// else. So where the rack's joint is free, this makes it the
        /// prismatic the mate describes, along that direction.
        ///
        /// Only ever an upgrade FROM free: a rack whose own mates define a
        /// slide keeps the joint they defined, geometry and all.
        /// </summary>
        private static void RackSlide(
            ClassificationResult result, GraphMate mate, RigJoint driver, RigJoint driven)
        {
            if (driven == null || driven.Type != JointType.Free) return;

            GraphMateEntity rackSide = null;
            foreach (var e in mate.Entities)
            {
                if (e.EntityTypeName == "cylinder") continue;
                if (e.Direction == null || e.Point == null) continue;
                rackSide = e;
                break;
            }
            if (rackSide == null) return;
            var axis = MathOps.Normalized(rackSide.Direction);
            if (axis == null || MathOps.Norm(axis) < 0.5) return;

            driven.Type = JointType.Prismatic;
            driven.Axis = axis;
            driven.Origin = new[] { rackSide.Point[0], rackSide.Point[1], rackSide.Point[2] };
            driven.SecondaryAxis = SecondaryAxis(axis);
            driven.SourceMates.Add(new SourceMate
            {
                SwFeature = mate.FeatureName,
                Type = mate.TypeName,
            });
            driven.Notes = AppendNote(driven.Notes,
                "the slide comes from the rack-pinion mate: the rack's own "
                + "mates leave more than one freedom, and the mate says the "
                + "rack travels along its own edge.");
            // The under-defined warning was about the joint as it was.
            result.Warnings.RemoveAll(w =>
                w.Code == "UNDER_DEFINED" && w.Joints.Contains(driven.Id));
        }

        private static void WarnCoupling(ClassificationResult result, RigJoint edgeJoint, GraphMate mate)
        {
            var w = new ManifestWarning();
            w.Code = "COUPLING_UNRESOLVED";
            w.Message = "Coupling mate " + (mate.FeatureName ?? "?")
                + " could not be resolved to a driver/driven joint pair; annotation dropped.";
            if (edgeJoint != null) w.Joints.Add(edgeJoint.Id);
            result.Warnings.Add(w);
        }

        // ── Geometry helpers ────────────────────────────────────────────────

        private static double[] IntersectPlaneAxis(double[][] plane, double[] axis, double[] axisPoint)
        {
            double denom = MathOps.Dot(axis, plane[0]);
            if (Math.Abs(denom) < MathOps.Epsilon) return axisPoint;
            double t = (MathOps.Dot(plane[1], plane[0]) - MathOps.Dot(axisPoint, plane[0])) / denom;
            return new[]
            {
                axisPoint[0] + t * axis[0],
                axisPoint[1] + t * axis[1],
                axisPoint[2] + t * axis[2],
            };
        }

        private static double[] IntersectPlanes(double[][] p1, double[][] p2)
        {
            var d = MathOps.Cross(p1[0], p2[0]);
            double dd = MathOps.Dot(d, d);
            if (dd < MathOps.Epsilon) return p1[1];
            double c1 = MathOps.Dot(p1[0], p1[1]);
            double c2 = MathOps.Dot(p2[0], p2[1]);
            var mix = new[]
            {
                c1 * p2[0][0] - c2 * p1[0][0],
                c1 * p2[0][1] - c2 * p1[0][1],
                c1 * p2[0][2] - c2 * p1[0][2],
            };
            var num = MathOps.Cross(mix, d);
            return new[] { num[0] / dd, num[1] / dd, num[2] / dd };
        }

        /// <summary>Deterministic roll reference: the global axis least
        /// parallel to the joint axis, orthogonalised and normalised. Ties
        /// resolve X before Y before Z.</summary>
        // ── The solver's verdict ────────────────────────────────────────────

        /// <summary>
        /// The joint types the DOF probe has a vocabulary for. Everything else
        /// the classifier can produce (path, surface, pin_slot, screw), is
        /// invisible to the probe, which reads freedoms and cannot see a
        /// curve, a mesh, or the coupling between two of them.
        /// </summary>
        public static bool IsSolverPrimitive(string type)
        {
            return type == JointType.Revolute || type == JointType.Prismatic
                || type == JointType.Cylindrical || type == JointType.Planar
                || type == JointType.Ball;
        }

        /// <summary>
        /// Replaces a joint's kinematics with what SolidWorks' own solver
        /// reported for the pair. The mate analysis infers the freedom from
        /// mate geometry one pair at a time; the solver has actually solved
        /// the assembly, so where the two disagree on a primitive the solver
        /// is right.
        ///
        /// Only the freedom itself is adopted: type, axis, origin. Limits,
        /// couplings and sampled geometry stay with the mate analysis, because
        /// the probe cannot see them: it has to SUPPRESS limit mates to read
        /// any freedom at all, and a screw reads as a plain cylindrical once
        /// its coupling is invisible. Returns a note describing anything that
        /// had to be dropped, or null.
        /// </summary>
        public static string AdoptSolverVerdict(
            RigJoint joint, string type, double[] axis, double[] origin)
        {
            string previous = joint.Type;
            bool sameLine = joint.Axis != null && axis != null
                && Math.Abs(MathOps.Dot(MathOps.Normalized(joint.Axis),
                                        MathOps.Normalized(axis))) > 0.999;

            joint.Type = type;
            if (axis != null)
            {
                joint.Axis = Canonical(MathOps.Threshold(MathOps.Normalized(axis), 1e-11));
                joint.SecondaryAxis = SecondaryAxis(joint.Axis);
            }
            else if (type == JointType.Ball)
            {
                joint.Axis = null;
                joint.SecondaryAxis = null;
            }
            // A prismatic or planar verdict carries no origin: the probe
            // reports a direction, not a point, and the classifier already
            // anchored the origin at the child's own reference frame, which is
            // where a consumer wants the bone. Keep it.
            if (origin != null) joint.Origin = MathOps.Threshold(origin, 1e-11);

            // A limit is measured about a specific axis. Moved to a different
            // line, or onto a freedom the new type does not have, it is no
            // longer about anything.
            var dropped = new List<string>();
            if (joint.RotationLimit != null && (!sameLine || !HasRotation(type)))
            {
                joint.RotationLimit = null;
                dropped.Add("its rotation limit");
            }
            if (joint.TranslationLimit != null && (!sameLine || !HasTranslation(type)))
            {
                joint.TranslationLimit = null;
                dropped.Add("its translation limit");
            }

            string note = "The SolidWorks solver reads this pair as " + type
                + "; the mate analysis said " + previous
                + ", and the solver is authoritative.";
            if (dropped.Count > 0)
                note += " Dropped " + string.Join(" and ", dropped.ToArray())
                    + ": the freedom it measured is not this joint's.";
            return note;
        }

        private static bool HasRotation(string type)
        {
            return type == JointType.Revolute || type == JointType.Cylindrical
                || type == JointType.Ball;
        }

        private static bool HasTranslation(string type)
        {
            return type == JointType.Prismatic || type == JointType.Cylindrical
                || type == JointType.Planar;
        }

        private static double[] SecondaryAxis(double[] axis)
        {
            var candidates = new[]
            {
                new double[] { 1, 0, 0 },
                new double[] { 0, 1, 0 },
                new double[] { 0, 0, 1 },
            };
            double[] pick = candidates[0];
            double best = double.MaxValue;
            foreach (var c in candidates)
            {
                double d = Math.Abs(MathOps.Dot(axis, c));
                if (d < best) { best = d; pick = c; }
            }
            double along = MathOps.Dot(axis, pick);
            var ortho = new[]
            {
                pick[0] - along * axis[0],
                pick[1] - along * axis[1],
                pick[2] - along * axis[2],
            };
            return MathOps.Threshold(MathOps.Normalized(ortho), 1e-11);
        }

        private static Dictionary<string, double[][]> BuildGroupBoxes(
            MateGraph graph, RigidGroupingResult grouping)
        {
            var boxes = new Dictionary<string, double[][]>();
            foreach (var c in graph.Components)
            {
                if (c.BboxMin == null || c.BboxMax == null) continue;
                string g;
                if (!grouping.ComponentGroup.TryGetValue(c.Id, out g)) continue;
                double[][] box;
                if (!boxes.TryGetValue(g, out box))
                {
                    boxes[g] = new[] { (double[])c.BboxMin.Clone(), (double[])c.BboxMax.Clone() };
                    continue;
                }
                for (int k = 0; k < 3; k++)
                {
                    if (c.BboxMin[k] < box[0][k]) box[0][k] = c.BboxMin[k];
                    if (c.BboxMax[k] > box[1][k]) box[1][k] = c.BboxMax[k];
                }
            }
            return boxes;
        }

        /// <summary>Each group's reference point: the first listed component's
        /// transform translation: the same fallback the Blender consumer uses
        /// for group placement, so the two sides agree on where a group "is".</summary>
        private static Dictionary<string, double[]> BuildGroupAnchors(
            MateGraph graph, RigidGroupingResult grouping)
        {
            var byId = new Dictionary<string, GraphComponent>();
            foreach (var c in graph.Components)
                if (c.Id != null) byId[c.Id] = c;

            var anchors = new Dictionary<string, double[]>();
            foreach (var g in grouping.Groups)
            {
                foreach (var cid in g.Components)
                {
                    GraphComponent c;
                    if (!byId.TryGetValue(cid, out c) || c.Transform == null) continue;
                    anchors[g.Id] = new[] { c.Transform[0, 3], c.Transform[1, 3], c.Transform[2, 3] };
                    break;
                }
            }
            return anchors;
        }

        /// <summary>Mate entities can sit far from the moving part (a long
        /// shaft mated at its base). The origin slides along the axis into the
        /// child group's bounding box so the bone lands on the geometry.</summary>
        private static double[] SlideTowardChild(
            double[] origin, double[] axis, Dictionary<string, double[][]> groupBoxes, string childGroup)
        {
            if (axis == null) return origin;
            double[][] box;
            if (!groupBoxes.TryGetValue(childGroup, out box)) return origin;
            return MathOps.ClosestPointOnLineWithinBox(
                box[0][0], box[1][0], box[0][1], box[1][1], box[0][2], box[1][2],
                axis, origin);
        }

        private static void Warn(List<ManifestWarning> warnings, string code, RigJoint joint, string message)
        {
            var w = new ManifestWarning();
            w.Code = code;
            w.Message = message;
            if (joint != null) w.Joints.Add(joint.Id);
            warnings.Add(w);
        }
    }
}
