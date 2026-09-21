using System;
using System.Collections.Generic;
using Peak.Cadder.Core.Model;

namespace Peak.Cadder.Core
{
    /// <summary>
    /// When a loop is cut and the consumer is not going to solve the cut, the
    /// constraint that cut carried would simply be lost, and SolidWorks does
    /// not lose it. This puts it back where it belongs: on the tree joints of
    /// the same ring, as freedom REMOVED.
    ///
    /// Live ClampRig (2026-08-24, Oscar): the two hydraulic rams are
    /// pinned to the machine by a concentric each. One is also held along its
    /// pin by a width mate between the lugs; the OTHER is held by a coincident
    /// between the two rams' front planes: it is located THROUGH the first
    /// ram, and nothing in that assembly is under-defined. Read pairwise, that
    /// second ram's own joint is a cylindrical: a concentric alone leaves it
    /// free to slide. The coincident is the mate that stops it, it lives on
    /// the ring's cut, and dropping the cut let the barrel slide along its pin
    /// in the rig when it cannot in SolidWorks.
    ///
    /// The algebra is screw theory, because "a revolute contributes no
    /// translation" is false the moment its axis does not pass through the
    /// point you are measuring at. Each joint contributes its twists: a
    /// revolute about the line (a, p) is (a, p x a), a slide along d is
    /// (0, d), and a freedom of the joint under test survives only if its
    /// own twist lies in the span of every OTHER twist around the ring. What
    /// the rest of the ring cannot reproduce, the ring forbids.
    ///
    /// This only ever removes freedom, which is the safe direction: a mate the
    /// pairwise reading could not see can make a joint stiffer, never looser.
    /// But only where the removal is POSE-INDEPENDENT. The span test is a
    /// first-order statement, and at a dead centre a joint that travels
    /// finitely has zero rate for every admissible velocity: read literally
    /// it would weld a live mechanism shut, which is far worse than leaving a
    /// freedom in. Every removal is therefore re-asked at a nudged pose
    /// before it is applied (LooseWhenNudged), ONE FREEDOM AT A TIME: a
    /// joint can hold one freedom the ring really forbids and another the
    /// saved pose merely hides.
    /// </summary>
    public static class CutTransfer
    {
        // The same tolerance the rest of the exporter calls two directions the
        // same by. The twists here are built from axes the DOF probe and the
        // mate reader produce independently. DofProbe accepts a rotation and
        // a slide direction as ONE line at 1e-6, so two axes that ARE the
        // same line can arrive a few 1e-7 apart. Read tighter than that, an
        // axis the pipeline has already accepted as parallel counts as an
        // independent direction, the ring's span comes out too large, and a
        // live freedom is removed: the one direction of error this must never
        // make. A larger tolerance can only ever remove less.
        private const double Tol = MateFacts.ParallelTol;

        private const string CoupledNote =
            "The loop cut at this ring removes freedom from this joint, but a "
            + "coupling mate drives one of its freedoms and this reading "
            + "cannot see that mate. It is exported unchanged and is therefore "
            + "freer here than in SolidWorks.";

        /// <summary>
        /// Narrows the tree joints of every loop the consumer will not solve.
        /// Runs after the loops are chosen and oriented, before the limits are
        /// derived, because a joint that loses its slide loses its stroke with
        /// it.
        /// </summary>
        public static void Apply(LoopAnalysisResult result)
        {
            if (result == null || result.Loops.Count == 0) return;

            var byId = new Dictionary<string, RigJoint>();
            foreach (var j in result.Joints) byId[j.Id] = j;

            // To a fixed point, because a joint can sit on more than one ring
            // (live ClampRig: a ram's own pin is in both its clamp's
            // loop and the loop that levels it against the other ram) and
            // what one ring takes away can let the next take more.
            //
            // Narrowing only ever removes freedom and no joint here holds
            // more than three, so one pass per freedom in the model is a real
            // termination bound. A fixed four was a schedule pretending to be
            // a backstop: a narrowing reaches only the loops AFTER this one in
            // result.Loops, so a cascade running against that order needs one
            // pass per step, and four would quietly leave the fifth undone,
            // freer than SolidWorks, and depending on the order the loops
            // happened to be discovered in.
            int budget = 3 * result.Joints.Count + 1;
            for (int pass = 0; pass < budget; pass++)
                if (!Sweep(result, byId)) return;
        }

        private static bool Sweep(
            LoopAnalysisResult result, Dictionary<string, RigJoint> byId)
        {
            bool changed = false;
            foreach (var loop in result.Loops)
            {
                // EVERY ring, not only the ones the consumer leaves open.
                // "The rest of the ring cannot reproduce this freedom" is a
                // fact about the ASSEMBLY, not about the rig: SolidWorks
                // forbids it whoever closes the loop, so removing it can
                // never be wrong here and gating on the closure only leaves
                // freedoms in.
                //
                // A solved closure does not put the constraint back either.
                // An IK closure re-joins ONE POINT, and the solver only
                // ROTATES: a translation channel left on a tree joint of
                // that ring is a channel nothing in the rig ever moves back.
                //
                // Live corpus 06 (2026-08-25, Oscar): in fourbar and in
                // parallelogram every pin is a concentric PLUS a coincident,
                // so every pin is a revolute, but for one pair the only
                // mate between those two bodies is the concentric, the
                // coincident that pins their height being between different
                // pairs. Read pairwise that pin is a cylindrical and its
                // height is held THROUGH THE LOOP. Both rings cut to an IK
                // closure, were gated out here, and shipped that pin
                // cylindrical: in Blender the driven bone slid straight up
                // and down its pin, which SolidWorks does not allow.
                var ring = new List<RigJoint>();
                foreach (string id in loop.MemberJoints)
                {
                    RigJoint j;
                    if (byId.TryGetValue(id, out j)) ring.Add(j);
                }
                if (ring.Count < 2) continue;

                foreach (var joint in ring)
                {
                    // The cut is narrowed like any other member. It used to
                    // be skipped ("it carries no bone of its own"), but a
                    // consumer may take another driver candidate, and then
                    // the cut becomes a tree edge with a bone: the two
                    // identical links of the SolidWorks 2022 sample
                    // actuator.sldasm shipped as one revolute and one
                    // cylindrical pin, the difference being only which of
                    // them the analyzer had cut (2026-09-15). What the ring
                    // forbids, it forbids whichever member is cut. The
                    // closure kind is unaffected: a ram's slide survives
                    // (the pins reproduce a translation), and a pin that
                    // loses its slide still shares a point.
                    var peers = new List<RigJoint>();
                    var others = new List<double[]>();
                    bool complete = true;
                    foreach (var peer in ring)
                    {
                        if (ReferenceEquals(peer, joint)) continue;
                        peers.Add(peer);
                        if (!AppendTwists(peer, others)) { complete = false; break; }
                    }
                    // A member this cannot describe (a path, a face contact)
                    // could permit anything, so the ring forbids nothing.
                    if (!complete) continue;
                    if (Narrow(joint, others, peers)) changed = true;
                }
            }
            return changed;
        }

        /// <summary>The twists a joint permits, in the global frame. False for
        /// a type whose freedom is not a fixed screw system.</summary>
        private static bool AppendTwists(RigJoint j, List<double[]> into,
                                         int seed = 0)
        {
            return AppendTwistsAt(j, into, j.Origin == null ? null : Where(j, seed));
        }

        /// <summary>The same twists, read with the joint's origin at
        /// <paramref name="point"/> (a nudged pose the caller has chosen).</summary>
        private static bool AppendTwistsAt(RigJoint j, List<double[]> into, double[] point)
        {
            switch (j.Type)
            {
                case JointType.Fixed:
                    return true;
                case JointType.Revolute:
                    if (!Framed(j)) return false;
                    into.Add(Turn(j.Axis, point));
                    return true;
                case JointType.Prismatic:
                    if (j.Axis == null) return false;
                    into.Add(Slide(j.Axis));
                    return true;
                case JointType.Cylindrical:
                    if (!Framed(j)) return false;
                    into.Add(Turn(j.Axis, point));
                    into.Add(Slide(j.Axis));
                    return true;
                case JointType.Screw:
                    // A screw couples its rotation and slide, so it is ONE
                    // twist, and its pitch is not in the manifest. Treating it
                    // as a cylindrical is the honest over-estimate: it can
                    // only make the ring look freer, never tighter.
                    if (!Framed(j)) return false;
                    into.Add(Turn(j.Axis, point));
                    into.Add(Slide(j.Axis));
                    return true;
                case JointType.Planar:
                    if (!Framed(j)) return false;
                    into.Add(Turn(j.Axis, point));
                    foreach (var d in InPlane(j.Axis)) into.Add(Slide(d));
                    return true;
                case JointType.PinSlot:
                    if (!Framed(j) || j.SecondaryAxis == null) return false;
                    into.Add(Turn(j.Axis, point));
                    into.Add(Slide(j.SecondaryAxis));
                    return true;
                case JointType.Ball:
                    if (j.Origin == null) return false;
                    foreach (var d in Basis()) into.Add(Turn(d, point));
                    return true;
                default:
                    return false;    // free, path, surface: unbounded
            }
        }

        /// <summary>
        /// Drops the freedoms of this joint the rest of the ring cannot
        /// reproduce, and re-types it to what is left.
        ///
        /// The count comes first, from the DIMENSION of the intersection
        /// between the joint's own screw system and the ring's, because a
        /// two-freedom joint can be pinned down to a coupled motion neither
        /// of its named freedoms performs on its own: a cylindrical reduced
        /// to a screw. Only once the count agrees with what the individual
        /// freedoms say is anything renamed; where they disagree the joint is
        /// left exactly as it was, with a note. Removing a freedom that is
        /// really there would weld a mechanism shut, which is far worse than
        /// leaving one that is not.
        /// </summary>
        private static bool Narrow(RigJoint j, List<double[]> ring,
                                  List<RigJoint> peers)
        {
            // Not Framed: a ball has no axis and still has a twist system.
            // Every axis-bearing type is gated by AppendTwists just below,
            // which refuses a null axis; what this body needs of its own is
            // the point everything is measured from.
            if (j.Origin == null) return false;

            List<double[]> own = new List<double[]>();
            if (!AppendTwists(j, own) || own.Count == 0) return false;

            int survives = IntersectionRank(ring, own, j.Origin);
            if (survives >= own.Count) return false;   // nothing was removed

            // A coupling is an explicit mate DRIVING one of this joint's
            // freedoms, and this analysis cannot see it: it reads every joint
            // as if its freedoms were independent. Re-typing here would leave
            // the manifest declaring a driver for a channel the type no
            // longer has, and a revolute carries no translation limit to
            // clamp it with. Same refusal as the DOF probe's.
            if (j.Coupling != null) { Note(j, CoupledNote); return false; }

            // Which freedoms a nudged pose gives back, asked BEFORE
            // AlignSlides renames a plane's pair so the answers line up with
            // this joint's own freedoms one for one.
            AlignSlides(own, ring, j.Origin);
            bool[] loose = LooseWhenNudged(j, peers, own);

            var live = new bool[own.Count];
            var kept = new List<double[]>();
            for (int i = 0; i < own.Count; i++)
                if (Spans(ring, own[i], j.Origin))
                {
                    live[i] = true;
                    kept.Add(own[i]);
                }
            if (kept.Count != survives)
            {
                // One motion left, and none of the joint's own axes performs
                // it alone: a rotation about a line PARALLEL to a planar's
                // normal is its spin plus a slide, and a plane pinned that
                // way is a hinge about that line. Live plunger.sldasm
                // (2026-09-15): each nut sits on a bolt through two link
                // plates, mated planar to one plate and welded to the other,
                // and the ring of the bolt's two coaxial pins left the nut
                // exactly the turn about the bolt. Refused as "a coupled
                // motion this schema cannot say", the nut kept a plane's
                // three freedoms and drove the arm-hinge rig from a nut.
                if (survives == 1 && kept.Count == 0 && TurnsAboutOneLine(j, ring, own, peers))
                    return true;
                Note(j, "The loop cut at this ring removes freedom from this "
                        + "joint, but what is left is a coupled motion none of "
                        + "its own axes performs alone, which this schema has "
                        + "no way to say. It is exported unchanged and is "
                        + "therefore freer here than in SolidWorks.");
                return false;
            }

            // Everything above is the velocity loop equation at the ONE pose
            // this assembly was saved in, and "cannot move now" is not "cannot
            // move". At a dead centre: end of stroke, over centre, a crank in
            // line with its slide: a joint that travels finitely has zero
            // rate for every admissible velocity, and the span test cannot
            // tell that from a joint SolidWorks really holds. A real hold
            // comes from how the axes are ORIENTED, which the mates fix; a
            // dead centre comes from where the parts happen to SIT, which the
            // saved pose fixes. So nudge the ring's origins off their exact
            // positions, leave every axis alone, and re-ask: a freedom that
            // comes back was never held.
            //
            // FREEDOM BY FREEDOM, because a joint can hold one of each. Live
            // corpus 06 fourbar (2026-08-25, Oscar) is saved with its crank in
            // line with the ground bar, which puts three of its four pin
            // points on one line: to first order the coupler and the rocker
            // cannot turn relative to each other at that instant, and the
            // nudge rightly gives that turn back. Their SLIDE along the pin no
            // nudge ever gives back: every twist in that ring is a turn about
            // z or a slide across it, so nothing the ring can do moves
            // anything along z, at any pose. Vetoing the whole narrowing
            // because ONE freedom was pose-hidden shipped that pin
            // cylindrical, and the rocker slid straight off it in Blender.
            int hidden = 0;
            for (int i = 0; i < own.Count; i++) if (!live[i] && loose[i]) hidden++;
            if (hidden > 0)
            {
                // Nothing the ring appeared to take survives the nudge:
                // say so and leave the joint alone. (A plane's two slides
                // used to force this exit too, because the nudged reading
                // was taken before AlignSlides renamed the pair and so
                // named different freedoms; it now reads the aligned pair.
                // Live cam-follower sample, 2026-09-15: the lifter, planar
                // on an assembly plane and planar on the cam's end face,
                // whose normal is the cam's own axis, is a slide, and the
                // veto shipped it planar with a dead-centre note.)
                if (kept.Count + hidden >= own.Count)
                {
                    Note(j, "The loop cut at this ring appears to remove "
                            + "freedom from this joint, but only at the pose "
                            + "this assembly was saved in, a dead centre, "
                            + "where the freedom is momentarily still and not "
                            + "forbidden. It is exported unchanged. Re-export "
                            + "away from the dead centre to see what the ring "
                            + "really holds.");
                    return false;
                }
                for (int i = 0; i < own.Count; i++)
                    if (!live[i] && loose[i]) { live[i] = true; kept.Add(own[i]); }
                survives = kept.Count;
            }

            switch (j.Type)
            {
                case JointType.Cylindrical:
                    // Off `kept`, not off a fresh reading at the saved pose:
                    // a freedom the nudge gave back is in `kept` and is NOT in
                    // the ring's span where the assembly happens to sit.
                    bool turns = HasSpin(kept);
                    if (survives == 0) Retype(j, JointType.Fixed, "both freedoms");
                    else if (turns) Retype(j, JointType.Revolute, "the slide");
                    else Retype(j, JointType.Prismatic, "the rotation");
                    return true;

                case JointType.Revolute:
                    Retype(j, JointType.Fixed, "the rotation");
                    return true;

                case JointType.Prismatic:
                    Retype(j, JointType.Fixed, "the slide");
                    return true;

                case JointType.PinSlot:
                    if (survives == 0) { Retype(j, JointType.Fixed, "both freedoms"); return true; }
                    if (HasSpin(kept))
                        Retype(j, JointType.Revolute, "the slot");
                    else
                    {
                        j.Axis = MathOps.Normalized(j.SecondaryAxis);
                        j.SecondaryAxis = AnyPerpendicular(j.Axis);
                        Retype(j, JointType.Prismatic, "the pin's rotation");
                    }
                    return true;

                case JointType.Planar:
                    var slides = new List<double[]>();
                    foreach (var t in kept)
                        if (IsSlide(t)) slides.Add(new[] { t[3], t[4], t[5] });
                    bool spins = kept.Count > slides.Count;
                    if (survives == 0) { Retype(j, JointType.Fixed, "every freedom"); return true; }
                    if (slides.Count == 0) { Retype(j, JointType.Revolute, "both slides"); return true; }
                    if (slides.Count == 1 && spins)
                    {
                        j.SecondaryAxis = MathOps.Normalized(slides[0]);
                        Retype(j, JointType.PinSlot, "one of its slides");
                        return true;
                    }
                    if (slides.Count == 1)
                    {
                        j.Axis = MathOps.Normalized(slides[0]);
                        j.SecondaryAxis = AnyPerpendicular(j.Axis);
                        Retype(j, JointType.Prismatic,
                               "its spin and one of its slides");
                        return true;
                    }
                    // Two slides and no spin: a plane that cannot turn in
                    // itself, which no type here names.
                    Note(j, "The loop cut at this ring also forbids the spin "
                            + "about this plane's normal, which this schema "
                            + "has no way to say; the spin is exported.");
                    return false;

                case JointType.Ball:
                    if (survives == 0)
                    {
                        Retype(j, JointType.Fixed, "every rotation");
                        return true;
                    }
                    if (survives == 1)
                    {
                        // kept.Count == survives here, and every twist in a
                        // ball's span turns about its own centre, so the
                        // survivor is a named axis through the origin, which
                        // is exactly a revolute.
                        if (j.RotationLimit != null)
                            Note(j, "Its swing-cone limit measured rotations "
                                    + "the ring has removed, so it is not "
                                    + "exported.");
                        j.RotationLimit = null;
                        j.Axis = MathOps.Normalized(
                            new[] { kept[0][0], kept[0][1], kept[0][2] });
                        j.SecondaryAxis = AnyPerpendicular(j.Axis);
                        Retype(j, JointType.Revolute,
                               "two of its three rotations");
                        return true;
                    }
                    // Two rotations and no third: a universal joint, which no
                    // type here names.
                    Note(j, "The loop cut at this ring leaves this ball only "
                            + "two of its three rotations, which this schema "
                            + "has no way to say. It is exported as a full "
                            + "ball and is therefore freer here than in "
                            + "SolidWorks.");
                    return false;

                case JointType.Screw:
                    // Deliberately NOT narrowed. AppendTwists reads a screw as
                    // a cylindrical because the pitch is not in the manifest,
                    // and the classifier also types a pair `screw` on the
                    // fallback where the DOF state matched no analytic case,
                    // so this joint's real freedom is not guaranteed to lie
                    // inside the span just measured, and welding it shut on
                    // that evidence is the error this file calls the worse one.
                    Note(j, "The loop cut at this ring removes freedom from "
                            + "this joint, but a screw's turn and slide are "
                            + "one coupled motion whose pitch this analysis "
                            + "does not carry, so what is left cannot be named "
                            + "safely. It is exported unchanged and may be "
                            + "freer here than in SolidWorks.");
                    return false;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Which of this joint's freedoms the ring permits once its origins
        /// are nudged off their exact positions: the signature of a dead
        /// centre, where the pose and not the mates is what stills that
        /// freedom. Axes are left exactly as they are, because their
        /// directions are what the mates hold and a genuine removal must
        /// survive this untouched.
        ///
        /// One flag per freedom, in the order of `own` (the joint's twists
        /// after AlignSlides), so the caller can give back the freedom the
        /// pose was hiding without giving back the one the ring really
        /// holds. Slides are pure translations and do not depend on where
        /// the joint sits, so the aligned pair stands in for the seed pair
        /// at every nudged pose and the flags name the same freedoms.
        /// </summary>
        private static bool[] LooseWhenNudged(
            RigJoint j, List<RigJoint> peers, List<double[]> own)
        {
            int count = own.Count;
            var loose = new bool[count];
            for (int seed = 1; seed <= 2; seed++)
            {
                var nudged = new List<double[]>();
                foreach (var p in peers)
                    if (!AppendTwists(p, nudged, seed)) return loose;
                var mine = new List<double[]>();
                if (!AppendTwists(j, mine, seed)) return loose;
                for (int i = 0; i < count && i < mine.Count; i++)
                    if (IsSlide(mine[i]) && IsSlide(own[i])) mine[i] = own[i];
                var at = Where(j, seed);
                for (int i = 0; i < count && i < mine.Count; i++)
                    if (Spans(nudged, mine[i], at)) loose[i] = true;
            }
            return loose;
        }

        /// <summary>
        /// The one motion a ring leaves a joint when it is a pure rotation:
        /// re-types the joint to a revolute about that line. Only where the
        /// same line comes back at nudged poses, because a rotation that is
        /// left at one pose alone is a dead centre, not a hinge.
        /// </summary>
        private static bool TurnsAboutOneLine(
            RigJoint j, List<double[]> ring, List<double[]> own, List<RigJoint> peers)
        {
            if (j.Type != JointType.Planar && j.Type != JointType.Cylindrical
                && j.Type != JointType.PinSlot)
                return false;
            // The line has to be a PEER'S hinge line: a line the mates fix,
            // not one the pose happens to make. A scotch yoke at its dead
            // centre also leaves its pin-slot exactly one rotation, about
            // the crank's own axis, and that is where the parts sit, not
            // how they are mated. The peer's line is asked for again at
            // nudged poses; the nudge moves coaxial pins TOGETHER, because
            // two pins on one bolt are one line by their mates, and a
            // nudge that pulled them apart would invent a slide.
            RigJoint peer = null;
            double[] axis = null, origin = null;
            for (int seed = 0; seed <= 2 && (seed == 0 || peer != null); seed++)
            {
                var points = NudgedPoints(peers, j, seed);
                var ringHere = new List<double[]>();
                foreach (var p in peers)
                    if (!AppendTwistsAt(p, ringHere, points[p.Id])) return false;
                var mine = new List<double[]>();
                if (!AppendTwistsAt(j, mine, points[j.Id])) return false;
                double[] at = points[j.Id];
                if (IntersectionRank(ringHere, mine, at) != 1) return false;
                double[] line = LineOf(IntersectionTwist(ringHere, mine, at), at);
                if (line == null) return false;
                var dir = new[] { line[0], line[1], line[2] };
                var pt = new[] { line[3], line[4], line[5] };
                RigJoint found = null;
                foreach (var p in peers)
                {
                    if (p.Type != JointType.Revolute && p.Type != JointType.Cylindrical
                        && p.Type != JointType.Screw && p.Type != JointType.PinSlot)
                        continue;
                    if (!Framed(p)) continue;
                    if (Math.Abs(MathOps.Dot(MathOps.Normalized(p.Axis), dir)) < 1.0 - Tol) continue;
                    if (DistanceToLine(points[p.Id], dir, pt) > LineTol) continue;
                    found = p;
                    break;
                }
                if (found == null) return false;
                if (seed == 0) { peer = found; axis = dir; origin = pt; }
                else if (!ReferenceEquals(found, peer)) return false;
            }

            j.Axis = axis;
            j.SecondaryAxis = AnyPerpendicular(axis);
            j.Origin = origin;
            Retype(j, JointType.Revolute, "every freedom but the turn about "
                   + peer.Id + "'s own axis, which is not one of its own");
            return true;
        }

        /// <summary>Two points closer than this sit on one line.</summary>
        private const double LineTol = 1e-6;

        /// <summary>Where every joint of the ring is read at a nudged pose:
        /// each origin moved by its own id-hashed offset, except that joints
        /// on ONE line (parallel axes, origins on each other's line) share
        /// the first one's offset and stay on one line.</summary>
        private static Dictionary<string, double[]> NudgedPoints(
            List<RigJoint> peers, RigJoint j, int seed)
        {
            var all = new List<RigJoint>(peers) { j };
            var points = new Dictionary<string, double[]>();
            var offsets = new List<KeyValuePair<RigJoint, double[]>>();
            foreach (var x in all)
            {
                // A weld the classifier returned early carries no point, and
                // a slide's twist needs none: AppendTwistsAt reads neither
                // from a point, and Framed() keeps every type that does away
                // from a null here. Live 825 (2026-09-21): a planar joined a
                // ring through such a weld and the send failed on its origin.
                if (x.Origin == null) { points[x.Id] = null; continue; }
                double[] offset = null;
                if (seed > 0)
                {
                    foreach (var earlier in offsets)
                        if (Coaxial(earlier.Key, x)) { offset = earlier.Value; break; }
                    if (offset == null)
                    {
                        var moved = Where(x, seed);
                        offset = new double[3];
                        for (int i = 0; i < 3; i++) offset[i] = moved[i] - x.Origin[i];
                    }
                    offsets.Add(new KeyValuePair<RigJoint, double[]>(x, offset));
                }
                var p = new double[3];
                for (int i = 0; i < 3; i++) p[i] = x.Origin[i] + (offset == null ? 0.0 : offset[i]);
                points[x.Id] = p;
            }
            return points;
        }

        private static bool Coaxial(RigJoint a, RigJoint b)
        {
            if (!Framed(a) || !Framed(b)) return false;
            var da = MathOps.Normalized(a.Axis);
            if (Math.Abs(MathOps.Dot(da, MathOps.Normalized(b.Axis))) < 1.0 - Tol) return false;
            return DistanceToLine(b.Origin, da, a.Origin) <= LineTol;
        }

        private static double DistanceToLine(double[] p, double[] dir, double[] through)
        {
            var d = new[] { p[0] - through[0], p[1] - through[1], p[2] - through[2] };
            return MathOps.Norm(MathOps.Cross(d, dir));
        }

        /// <summary>The line of a pure-rotation twist expressed at `at`:
        /// unit direction in [0..2], the point nearest `at` in [3..5]. Null
        /// for a slide or a screw.</summary>
        private static double[] LineOf(double[] t, double[] at)
        {
            double[] axis = PureRotationAxis(t);
            if (axis == null) return null;
            double[] w = { t[0], t[1], t[2] };
            double[] v = { t[3], t[4], t[5] };
            double ww = MathOps.Dot(w, w);
            double[] wxv = MathOps.Cross(w, v);
            return new[] { axis[0], axis[1], axis[2],
                           at[0] + wxv[0] / ww, at[1] + wxv[1] / ww, at[2] + wxv[2] / ww };
        }

        /// <summary>The unit direction of a twist that is a pure rotation
        /// (no pitch), else null.</summary>
        private static double[] PureRotationAxis(double[] t)
        {
            if (t == null) return null;
            double[] w = { t[0], t[1], t[2] };
            double[] v = { t[3], t[4], t[5] };
            double wn = MathOps.Norm(w);
            if (wn <= Tol) return null;
            double pitch = MathOps.Dot(w, v) / (wn * wn);
            if (Math.Abs(pitch) > Tol) return null;
            return MathOps.Normalized(w);
        }

        /// <summary>
        /// A twist in both spans, expressed at `at`, when the intersection
        /// is one-dimensional: the null space of [R | -O] over the two
        /// independent bases has one direction, and its O-part is the twist.
        /// Null when the intersection is not exactly one-dimensional.
        /// </summary>
        private static double[] IntersectionTwist(
            List<double[]> ring, List<double[]> own, double[] at)
        {
            var r = new List<double[]>();
            foreach (var t in ring) Extend(r, Shift(t, at));
            var o = new List<double[]>();
            foreach (var t in own) Extend(o, Shift(t, at));
            int n = r.Count + o.Count;
            if (r.Count == 0 || o.Count == 0) return null;

            // Row-reduce the 6 x n system A x = 0, A = [R | -O].
            var a = new double[6][];
            for (int i = 0; i < 6; i++)
            {
                a[i] = new double[n];
                for (int k = 0; k < r.Count; k++) a[i][k] = r[k][i];
                for (int k = 0; k < o.Count; k++) a[i][r.Count + k] = -o[k][i];
            }
            var pivotCol = new int[6];
            int rank = 0;
            for (int col = 0; col < n && rank < 6; col++)
            {
                int best = -1;
                double bestAbs = Tol;
                for (int row = rank; row < 6; row++)
                    if (Math.Abs(a[row][col]) > bestAbs) { bestAbs = Math.Abs(a[row][col]); best = row; }
                if (best < 0) continue;
                var tmp = a[best]; a[best] = a[rank]; a[rank] = tmp;
                double p = a[rank][col];
                for (int k = 0; k < n; k++) a[rank][k] /= p;
                for (int row = 0; row < 6; row++)
                {
                    if (row == rank) continue;
                    double f = a[row][col];
                    if (f == 0.0) continue;
                    for (int k = 0; k < n; k++) a[row][k] -= f * a[rank][k];
                }
                pivotCol[rank] = col;
                rank++;
            }
            if (n - rank != 1) return null;
            // The one free column: set it to 1, read the pivots off.
            var isPivot = new bool[n];
            for (int i = 0; i < rank; i++) isPivot[pivotCol[i]] = true;
            int free = -1;
            for (int col = 0; col < n; col++) if (!isPivot[col]) { free = col; break; }
            var x = new double[n];
            x[free] = 1.0;
            for (int i = 0; i < rank; i++) x[pivotCol[i]] = -a[i][free];

            var twist = new double[6];
            for (int k = 0; k < o.Count; k++)
                for (int i = 0; i < 6; i++) twist[i] += x[r.Count + k] * o[k][i];
            double norm = Norm6(twist);
            if (norm <= Tol) return null;
            return ScaleTo(twist, 1.0 / norm);
        }

        /// <summary>A twist with no angular part: a pure slide.</summary>
        private static bool IsSlide(double[] t)
        {
            return t[0] == 0.0 && t[1] == 0.0 && t[2] == 0.0;
        }

        /// <summary>Whether any of these twists turns.</summary>
        private static bool HasSpin(List<double[]> twists)
        {
            foreach (var t in twists) if (!IsSlide(t)) return true;
            return false;
        }

        /// <summary>The joint's origin, or (for seed &gt; 0), that origin
        /// moved a fixed fraction of a millimetre in a direction that depends
        /// only on the joint id, so a re-export reads exactly the same.
        /// </summary>
        private static double[] Where(RigJoint j, int seed)
        {
            if (seed == 0 || j.Origin == null) return j.Origin;
            var o = new double[3];
            for (int i = 0; i < 3; i++)
            {
                unchecked
                {
                    int h = (int)2166136261;
                    foreach (char c in j.Id ?? "") h = (h ^ c) * 16777619;
                    h = (h ^ (seed * 31 + i)) * 16777619;
                    o[i] = j.Origin[i] + 1e-3 * ((h & 0xFFFF) / 32768.0 - 1.0);
                }
            }
            return o;
        }

        /// <summary>How many dimensions of `own` survive inside the span of
        /// `ring`: dim(A) + dim(B) - dim(A u B).</summary>
        private static int IntersectionRank(
            List<double[]> ring, List<double[]> own, double[] at)
        {
            var basis = new List<double[]>();
            int ringRank = 0, ownRank = 0;
            foreach (var t in ring) if (Extend(basis, Shift(t, at))) ringRank++;
            int unionRank = basis.Count;

            var alone = new List<double[]>();
            foreach (var t in own) if (Extend(alone, Shift(t, at))) ownRank++;
            foreach (var t in own) if (Extend(basis, Shift(t, at))) unionRank++;

            int rank = ringRank + ownRank - unionRank;
            return rank < 0 ? 0 : rank;
        }

        /// <summary>Gram-Schmidt: adds v to the orthonormal basis if it is
        /// independent of it, and says whether it did.</summary>
        private static bool Extend(List<double[]> basis, double[] v)
        {
            var r = (double[])v.Clone();
            Reduce(basis, r);
            double n = Norm6(r);
            if (n <= Tol * Math.Max(1.0, Norm6(v))) return false;
            basis.Add(ScaleTo(r, 1.0 / n));
            return true;
        }

        private static void Retype(RigJoint j, string type, string lost)
        {
            if (j.Type == type) return;
            string was = j.Type;
            j.Type = type;
            if (type == JointType.Fixed || type == JointType.Revolute)
                j.TranslationLimit = null;
            if (type == JointType.Fixed || type == JointType.Prismatic)
                j.RotationLimit = null;
            Note(j, "Read pairwise this is a " + was + ", but the loop cut at "
                    + "this ring removes " + lost + ": no combination of the "
                    + "ring's other joints can reproduce it, so SolidWorks "
                    + "does not permit it either.");
        }

        private static void Note(RigJoint j, string add)
        {
            // Apply sweeps to a fixed point, and a joint this declines to
            // narrow is reached again on every later pass. Without this the
            // same sentence lands in the manifest up to four times.
            if (!string.IsNullOrEmpty(j.Notes) && j.Notes.Contains(add)) return;
            j.Notes = string.IsNullOrEmpty(j.Notes) ? add : j.Notes + " " + add;
            if (j.Confidence == "high") j.Confidence = "medium";
        }

        // ── Screw algebra ───────────────────────────────────────────────────

        private static bool Framed(RigJoint j)
        {
            return j.Axis != null && j.Origin != null
                   && MathOps.Norm(j.Axis) > 1e-9;
        }

        /// <summary>The twist of a rotation about the line through p along
        /// a: angular part a, linear part p x a.</summary>
        private static double[] Turn(double[] axis, double[] p)
        {
            var a = MathOps.Normalized(axis);
            var m = MathOps.Cross(p, a);
            return new[] { a[0], a[1], a[2], m[0], m[1], m[2] };
        }

        private static double[] Slide(double[] dir)
        {
            var d = MathOps.Normalized(dir);
            return new[] { 0.0, 0.0, 0.0, d[0], d[1], d[2] };
        }

        private static double[] AnyPerpendicular(double[] axis)
        {
            var a = MathOps.Normalized(axis);
            var seed = Math.Abs(a[0]) < 0.9
                ? new[] { 1.0, 0.0, 0.0 } : new[] { 0.0, 1.0, 0.0 };
            return MathOps.Normalized(MathOps.Cross(a, seed));
        }

        /// <summary>
        /// A planar joint's two slides come from AnyPerpendicular's seed, so
        /// they are a frame fixed to the WORLD and not to the mechanism: the
        /// same linkage mounted at an angle gets a different pair. `survives`
        /// is a dimension and does not care, but the one-at-a-time test does:
        /// a permitted slide diagonal to that frame spans neither axis, the
        /// two disagree, and a joint this schema can name is exported
        /// unnarrowed. So turn the frame toward the ring first: u' is the
        /// in-plane direction the ring comes closest to permitting, v' its
        /// perpendicular. Same plane, same intersection; only the names
        /// change. A no-op for every other type: they have at most one slide
        /// and theirs is a real axis, not a seed.
        /// </summary>
        private static void AlignSlides(
            List<double[]> own, List<double[]> ring, double[] at)
        {
            int a = -1, b = -1;
            for (int i = 0; i < own.Count; i++)
                if (own[i][0] == 0.0 && own[i][1] == 0.0 && own[i][2] == 0.0)
                {
                    if (a < 0) a = i;
                    else if (b < 0) b = i;
                    else return;                 // more than two: not a plane
                }
            if (b < 0) return;

            var basis = new List<double[]>();
            foreach (var t in ring)
            {
                var v = Shift(t, at);
                Reduce(basis, v);
                double n = Norm6(v);
                if (n > 1e-9) basis.Add(ScaleTo(v, 1.0 / n));
            }

            // What the ring cannot reproduce of x*u + y*v is x*ra + y*rb, so
            // the direction it best permits is the smallest eigenvector of the
            // 2x2 Gram matrix [[p, q], [q, r]].
            var ra = (double[])own[a].Clone(); Reduce(basis, ra);
            var rb = (double[])own[b].Clone(); Reduce(basis, rb);
            double p = Dot6(ra, ra), q = Dot6(ra, rb), r = Dot6(rb, rb);
            double lo = 0.5 * (p + r - Math.Sqrt((p - r) * (p - r) + 4.0 * q * q));

            double x, y;
            if (Math.Abs(q) > Tol) { x = q; y = lo - p; }
            else if (p <= r) { x = 1.0; y = 0.0; }   // already aligned
            else { x = 0.0; y = 1.0; }
            double k = Math.Sqrt(x * x + y * y);
            if (k < 1e-12) return;
            x /= k; y /= k;

            var du = new[] { own[a][3], own[a][4], own[a][5] };
            var dv = new[] { own[b][3], own[b][4], own[b][5] };
            own[a] = Slide(new[] { x * du[0] + y * dv[0],
                                   x * du[1] + y * dv[1],
                                   x * du[2] + y * dv[2] });
            own[b] = Slide(new[] { -y * du[0] + x * dv[0],
                                   -y * du[1] + x * dv[1],
                                   -y * du[2] + x * dv[2] });
        }

        private static IEnumerable<double[]> InPlane(double[] normal)
        {
            var u = AnyPerpendicular(normal);
            yield return u;
            yield return MathOps.Normalized(MathOps.Cross(normal, u));
        }

        private static IEnumerable<double[]> Basis()
        {
            yield return new[] { 1.0, 0.0, 0.0 };
            yield return new[] { 0.0, 1.0, 0.0 };
            yield return new[] { 0.0, 0.0, 1.0 };
        }

        /// <summary>Whether a twist lies in the span of the given twists, by
        /// Gram-Schmidt: project the candidate onto an orthonormal basis of
        /// the span and see whether anything is left over.
        ///
        /// Everything is first re-referenced to `at`, which is the origin of
        /// the joint under test. A twist's linear part is a moment arm, so
        /// measuring it from the world origin would let a mechanism a hundred
        /// metres from it swamp the angular part; measured from the joint's
        /// own pivot the arms are the mechanism's own dimensions.</summary>
        private static bool Spans(List<double[]> twists, double[] want, double[] at)
        {
            // Extend, not a gate of its own: `kept` is counted against what
            // IntersectionRank produces, and two different notions of
            // independence cannot be made to agree. A ring twist Extend calls
            // dependent must not become a basis direction here.
            var basis = new List<double[]>();
            foreach (var t in twists) Extend(basis, Shift(t, at));
            var residual = Shift(want, at);
            double scale = Norm6(residual);
            Reduce(basis, residual);
            // The candidates are unit twists about `at`, so `scale` is 1 and
            // the residual is directly comparable to it.
            return Norm6(residual) <= Tol * Math.Max(1.0, scale);
        }

        /// <summary>The same twist read at another point: (w, v) becomes
        /// (w, v - r x w).</summary>
        private static double[] Shift(double[] t, double[] r)
        {
            var w = new[] { t[0], t[1], t[2] };
            var m = MathOps.Cross(r, w);
            return new[] { t[0], t[1], t[2],
                           t[3] - m[0], t[4] - m[1], t[5] - m[2] };
        }

        private static void Reduce(List<double[]> basis, double[] v)
        {
            foreach (var b in basis)
            {
                double d = Dot6(b, v);
                for (int i = 0; i < 6; i++) v[i] -= d * b[i];
            }
        }

        private static double Dot6(double[] a, double[] b)
        {
            double s = 0.0;
            for (int i = 0; i < 6; i++) s += a[i] * b[i];
            return s;
        }

        private static double Norm6(double[] a) => Math.Sqrt(Dot6(a, a));

        private static double[] ScaleTo(double[] a, double k)
        {
            var o = new double[6];
            for (int i = 0; i < 6; i++) o[i] = a[i] * k;
            return o;
        }
    }
}
