using System;
using System.Collections.Generic;
using Peak.SwToBlender.Core.Model;

namespace Peak.SwToBlender.Core
{
    /// <summary>How the child may still rotate after the constraints so far.</summary>
    public enum RotFreedom
    {
        Full,           // any axis, any position
        AboutPoint,     // any axis through RotPoint (spherical)
        AboutLine,      // only the line RotDir through RotPoint (pin)
        AboutDirection, // any line parallel to RotDir (planar spin)
        None,
    }

    /// <summary>The residual relative motion of a component pair.</summary>
    public sealed class MotionState
    {
        /// <summary>Orthonormal basis of the allowed translation subspace.</summary>
        public List<double[]> TransDirs = new List<double[]>
        {
            new double[] { 1, 0, 0 },
            new double[] { 0, 1, 0 },
            new double[] { 0, 0, 1 },
        };

        public RotFreedom Rot = RotFreedom.Full;
        public double[] RotDir;     // AboutLine / AboutDirection
        public double[] RotPoint;   // AboutLine / AboutPoint

        /// <summary>Constraint mates the resolver had no rule for (tangent,
        /// cam, ...). They can only REMOVE freedom, so a nonzero count means
        /// the state may overstate the mobility: the caller decides whether
        /// that demands a confidence downgrade.</summary>
        public int Unmodelled;

        public int TransDim { get { return TransDirs.Count; } }

        public bool IsRigid { get { return TransDim == 0 && Rot == RotFreedom.None; } }
    }

    /// <summary>
    /// Intersects the allowed relative motion of every constraint mate
    /// between a component pair. The predecessor pattern-matched mate PAIRS
    /// and returned on the first recognised shape, so a fully-defined hinge
    /// (concentric + face coincident + a side-face coincident that kills the
    /// spin) came back "revolute": found live on corpus assembly 01
    /// variants, 2026-08-22. Intersection cannot make that mistake: every
    /// mate narrows the state and the joint type is whatever survives all
    /// of them. RigidGrouper and JointClassifier both consume this, so
    /// "rigid enough to merge" and "classified fixed" can never disagree.
    /// </summary>
    public static class MotionResolver
    {
        public static MotionState Resolve(List<GraphMate> mates)
        {
            var s = new MotionState();
            // An angle-family mate allows rotation about EITHER measured
            // direction (a union, not a subspace), so it is only decidable
            // once the other mates have narrowed the rotation. Deferred,
            // and kept as one entry PER MATE: the union is between one
            // mate's own directions.
            var anglePairs = new List<double[][]>();
            // A line lying in a plane: {normal, line direction, line point}.
            // Deferred for the same reason: see ApplyLinesInPlanes.
            var linesInPlanes = new List<double[][]>();
            bool widthNoDirection = false;

            // Widths go last: a width with a cylindrical tab removes exactly
            // ONE rotation direction, and the rotation lattice can only apply
            // that exactly to a state another mate has already narrowed to a
            // single direction. Order in the feature tree is not a contract.
            var ordered = new List<GraphMate>(mates.Count);
            foreach (var m in mates)
                if (m != null && !MateFacts.Is(m, "WIDTH")) ordered.Add(m);
            foreach (var m in mates)
                if (m != null && MateFacts.Is(m, "WIDTH")) ordered.Add(m);

            foreach (var m in ordered)
            {
                if (m == null || MateFacts.IsLimitMate(m)) continue;

                if (MateFacts.IsLock(m))
                {
                    s.TransDirs.Clear();
                    s.Rot = RotFreedom.None;
                    s.RotDir = null;
                    continue;
                }
                if (MateFacts.Is(m, "GEAR") || MateFacts.Is(m, "RACKPINION")
                    || MateFacts.Is(m, "LINEARCOUPLER") || MateFacts.Is(m, "SCREW")
                    || MateFacts.Is(m, "HINGE") || MateFacts.Is(m, "SLOT")
                    || MateFacts.Is(m, "UNIVERSALJOINT"))
                {
                    // Couplings annotate other joints (a universal joint
                    // relates two shafts' spins exactly like a gear); hinge
                    // and slot are handled by the classifier's special cases
                    // before and after this resolver.
                    continue;
                }
                if (MateFacts.Is(m, "CONCENTRIC"))
                {
                    ApplyLineCoincidence(s, m);
                    // "Lock rotation" on a concentric kills the spin too, so
                    // what is left is the axial slide alone, and with any
                    // face contact, nothing (live ClampRig, 2026-08-24:
                    // the ram's seals and grease nipples).
                    if (m.LockRotation) RotNone(s);
                    continue;
                }
                if (MateFacts.Is(m, "COORDINATE"))
                {
                    // Pinned on live corpus 04 (2026-08-22): an origin mate
                    // WITHOUT "align axes" exports as swMateCOINCIDENT, and
                    // the align-axes variant exports swMateCOORDINATE: the
                    // API exposes no align flag anywhere (2024 interop and
                    // docs both checked), the mate TYPE is the flag. Aligned
                    // origins are a full lock.
                    s.TransDirs.Clear();
                    RotNone(s);
                    continue;
                }
                if (MateFacts.Is(m, "COINCIDENT"))
                {
                    var planes = MateFacts.Planes(m);
                    var line = FirstLineEntity(m);
                    var point = FindTypedPointEntity(m);
                    if (planes.Count > 1)
                    {
                        foreach (var pl in planes) ApplyPlane(s, pl[0]);
                    }
                    else if (planes.Count == 1 && point != null)
                    {
                        // A vertex on a face pins ONE translation and no
                        // rotation at all: treating it like a plane pair
                        // (the old rule) invented two dead rotations.
                        RestrictTransToPlane(s, planes[0][0]);
                    }
                    else if (planes.Count == 1 && line != null)
                    {
                        // An edge or axis lying in a plane. Applied after the
                        // other mates, and in pairs where two of them share
                        // the line: see ApplyLinesInPlanes.
                        linesInPlanes.Add(new[]
                        {
                            MathOps.Normalized(planes[0][0]),
                            MathOps.Normalized(line.Direction),
                            line.Point,
                        });
                    }
                    else if (planes.Count == 1)
                    {
                        ApplyPlane(s, planes[0][0]);
                    }
                    else if (line != null && point != null
                        && (line.EntityTypeName == "axis" || line.EntityTypeName == "edge"))
                    {
                        // A vertex ON a line keeps its slide along the line
                        // and every rotation; only the two cross-line
                        // translations die. The old route through the
                        // concentric rule killed two rotations that exist.
                        RestrictTransToLine(s, MathOps.Normalized(line.Direction));
                    }
                    else if (line != null && point != null)
                    {
                        // A vertex on a curved FACE (cylinder, cone): only
                        // the radial translation dies: the same kill as the
                        // equivalent tangency. The old fall-through hit the
                        // concentric rule and killed four DOF that exist
                        // (live corpus 16 pt3, 2026-08-23).
                        if (!ApplyContactKill(s, m)) s.Unmodelled++;
                    }
                    else if (HasLineEntity(m))
                    {
                        // Coincident temporary axes / edges are a concentric
                        // in different clothes: rotate about + slide along
                        // the shared line. Found live: hinge4, 2026-08-22.
                        ApplyLineCoincidence(s, m);
                    }
                    else
                    {
                        // Typed points first; then ANY point-carrying entity:
                        // an origin coincidence (no align axes) arrives with
                        // kind-unknown entities holding only points (live
                        // corpus 04 variant ball2, 2026-08-22), and it pins
                        // the origins leaving rotation free: a ball.
                        var pt = FindPoint(m);
                        if (pt == null) pt = FindAnyPoint(m);
                        if (pt != null) ApplyPointCoincidence(s, pt);
                        else s.Unmodelled++;
                    }
                    continue;
                }
                if (MateFacts.Is(m, "DISTANCE"))
                {
                    // A fixed distance between planes is a coincident plane
                    // at an offset; between curved entities it is a contact
                    // at an offset: the same kill set as the tangency it
                    // generalises.
                    var planes = MateFacts.Planes(m);
                    if (planes.Count > 1)
                        foreach (var pl in planes) ApplyPlane(s, pl[0]);
                    else if (!ApplyContactKill(s, m))
                        s.Unmodelled++;
                    continue;
                }
                if (MateFacts.Is(m, "TANGENT"))
                {
                    if (!ApplyContactKill(s, m)) s.Unmodelled++;
                    continue;
                }
                if (MateFacts.Is(m, "SYMMETRIC"))
                {
                    ApplySymmetric(s, m);
                    continue;
                }
                if (MateFacts.Is(m, "PARALLEL"))
                {
                    // Orientations locked, translation untouched.
                    double[] dir, pt;
                    if (MateFacts.TryGetAxis(m, out dir, out pt)) RestrictRotToDirection(s, dir);
                    else s.Unmodelled++;
                    continue;
                }
                if (MateFacts.Is(m, "ANGLE") || MateFacts.Is(m, "PERPENDICULAR"))
                {
                    var dirs = new List<double[]>();
                    foreach (var e in m.Entities)
                        if (e.Direction != null) dirs.Add(MathOps.Normalized(e.Direction));
                    if (dirs.Count > 0) anglePairs.Add(dirs.ToArray());
                    else s.Unmodelled++;
                    continue;
                }
                if (MateFacts.Is(m, "WIDTH"))
                {
                    if (!ApplyWidth(s, m)) widthNoDirection = true;
                    continue;
                }
                if (MateFacts.Is(m, "PROFILECENTER"))
                {
                    // Profile centring pins all three translations at the
                    // profile centre. The "lock rotation" tick arrives ONLY on
                    // the feature data. Live corpus 05 (2026-08-22): planar4
                    // (unlocked) and planar5 (locked) export byte-identical
                    // entity params. Unlocked, the child spins about the mated
                    // faces' shared normal through the centre (the entity
                    // point IS the centre: confirmed live).
                    s.TransDirs.Clear();
                    if (m.LockRotation)
                    {
                        RotNone(s);
                        continue;
                    }
                    var pcPlanes = MateFacts.Planes(m);
                    if (pcPlanes.Count > 0) RestrictRotToLine(s, pcPlanes[0][0], pcPlanes[0][1]);
                    else s.Unmodelled++;
                    continue;
                }
                s.Unmodelled++;
            }

            ApplyLinesInPlanes(s, linesInPlanes);

            if (widthNoDirection && s.Rot == RotFreedom.AboutLine && s.RotDir != null)
            {
                // The recorded width mate carried no direction; the one thing
                // it reliably does on a pin joint is kill the axial slide.
                RestrictTransToPlane(s, s.RotDir);
            }

            foreach (var dirs in anglePairs)
            {
                if (s.Rot == RotFreedom.AboutLine || s.Rot == RotFreedom.AboutDirection)
                {
                    // The angle survives rotation about EITHER measured
                    // direction (spin keeps one normal fixed, precession
                    // keeps the cone angle): a union over the MATE's own
                    // directions. Applying each direction as an independent
                    // kill welded every perpendicular-carrying pair rigid:
                    // a perpendicular's two normals are mutually
                    // perpendicular by construction, so the rotation can be
                    // parallel to at most one of them (live corpus 12
                    // perp1, 2026-08-23: a redundant perpendicular fused
                    // the hinge with no joint and no warning).
                    bool survives = false;
                    foreach (var n in dirs)
                        if (MateFacts.IsParallel(s.RotDir, n)) { survives = true; break; }
                    if (!survives)
                    {
                        s.Rot = RotFreedom.None;
                        s.RotDir = null;
                    }
                }
                else if (s.Rot != RotFreedom.None)
                {
                    s.Unmodelled++;
                }
            }

            return s;
        }

        // ── Per-mate rules ──────────────────────────────────────────────────

        /// <summary>
        /// A line lying in a plane (a datum axis or an edge coincident with
        /// a face) leaves rotation about the plane's normal AND about the
        /// line: a two-direction freedom the rotation lattice cannot hold,
        /// so on an unnarrowed state the single mate can only count as
        /// unmodelled. Two such mates on the SAME line with different
        /// normals pin the line outright: that is a line coincidence,
        /// exactly what a concentric does. Live cam-follower sample
        /// (2026-09-15): the cam's datum axis lies in two perpendicular
        /// assembly planes, and with a face on the third the cam is a
        /// hinge, which the per-mate reading called free. The singles run
        /// after every other mate so the tilt kill lands on a state the
        /// others have narrowed, the way widths go last.
        /// </summary>
        private static void ApplyLinesInPlanes(MotionState s, List<double[][]> entries)
        {
            var pinned = new bool[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                if (pinned[i]) continue;
                for (int j = i + 1; j < entries.Count; j++)
                {
                    if (pinned[j] || !SameLine(entries[i], entries[j])) continue;
                    if (MateFacts.IsParallel(entries[i][0], entries[j][0])) continue;
                    RestrictTransToLine(s, entries[i][1]);
                    RestrictRotToLine(s, entries[i][1], entries[i][2]);
                    pinned[i] = pinned[j] = true;
                    break;
                }
            }
            for (int i = 0; i < entries.Count; i++)
            {
                double[] n = entries[i][0], l = entries[i][1];
                // The normal translation dies, and so does the one tilt that
                // would lift the line out of the plane (the same
                // single-direction kill a width's cylindrical tab uses).
                RestrictTransToPlane(s, n);
                if (pinned[i]) continue;    // the line cannot tilt at all
                if (!MateFacts.IsParallel(n, l)) KillRotDirection(s, MathOps.Cross(n, l));
            }
        }

        private static bool SameLine(double[][] a, double[][] b)
        {
            if (a[2] == null || b[2] == null) return false;
            if (!MateFacts.IsParallel(a[1], b[1])) return false;
            return MateFacts.DistancePointToLine(b[2], a[1], a[2]) <= MateFacts.CollinearTol;
        }

        private static void ApplyLineCoincidence(MotionState s, GraphMate m)
        {
            // Sphere before axis: a concentric with a spherical side pins the
            // CENTRES (a ball, not a pin) and must win even when the other
            // entity carries a direction. EntityParams direction slots are
            // undefined for point-like geometry, so a direction next to a
            // sphere is noise, not an axis.
            var sphere = FindEntity(m, "sphere");
            if (sphere != null && sphere.Point != null)
            {
                ApplyPointCoincidence(s, sphere.Point);
                return;
            }
            double[] dir, pt;
            if (MateFacts.TryGetAxis(m, out dir, out pt))
            {
                RestrictTransToLine(s, dir);
                RestrictRotToLine(s, dir, pt);
                // Cone-cone concentric: PINNED live (corpus 15 cone1,
                // 2026-08-23). SolidWorks leaves the axial slide alive,
                // exactly like a cylinder pair, so the cylindrical reading
                // needs no honesty flag.
                return;
            }
            // No direction anywhere: point-like entities on a concentric are
            // spheres in different clothes (centres pinned): the honest
            // reading is a ball, not an unmodelled shrug.
            var point = FindPoint(m);
            if (point != null)
            {
                ApplyPointCoincidence(s, point);
                return;
            }
            s.Unmodelled++;
        }

        private static void ApplyPlane(MotionState s, double[] normal)
        {
            RestrictTransToPlane(s, normal);
            RestrictRotToDirection(s, normal);
        }

        /// <summary>
        /// A width mate's plane entities are the width pair (normal w); the
        /// tab may be planes, or a cylindrical/conical face or axis. A plane
        /// tab behaves like a coincident plane pair. A line-like tab centred
        /// between the faces keeps BOTH its own spin and the tilt about w:
        /// the mate kills exactly one rotation direction, w × axis. Live
        /// corpus 05 (2026-08-22): treating the cylinder tab like a plane tab
        /// cost planar2 its spin and merged planar3 rigid. Returns false when
        /// no entity carries a direction (a recorded fixture).
        /// </summary>
        private static bool ApplyWidth(MotionState s, GraphMate m)
        {
            double[] w = null;
            foreach (var e in m.Entities)
            {
                if (e.EntityTypeName != "plane" || e.Direction == null) continue;
                w = MathOps.Normalized(e.Direction);
                break;
            }
            GraphMateEntity tab = null;
            foreach (var e in m.Entities)
            {
                if (e.Direction == null) continue;
                if (e.EntityTypeName == "plane" || e.EntityTypeName == "sphere") continue;
                tab = e;
                break;
            }
            if (w == null && tab != null) w = MathOps.Normalized(tab.Direction);
            if (w == null) return false;

            RestrictTransToPlane(s, w);

            var tabAxis = tab == null ? null : MathOps.Normalized(tab.Direction);
            if (tabAxis != null && !MateFacts.IsParallel(tabAxis, w))
            {
                KillRotDirection(s, MathOps.Cross(w, tabAxis));
                // The surviving spin's line is the tab's own axis: the width
                // pins that axis to the mid-plane, so the rest-pose rotation
                // line is known even though the plane pair alone gave only a
                // direction.
                if (s.Rot == RotFreedom.AboutDirection && tab.Point != null
                    && MateFacts.IsParallel(s.RotDir, tabAxis))
                {
                    s.Rot = RotFreedom.AboutLine;
                    s.RotPoint = tab.Point;
                }
            }
            else
            {
                RestrictRotToDirection(s, w);
            }
            return true;
        }

        /// <summary>
        /// The kill set of a surface contact: tangency, or a distance held
        /// between curved entities (the offset changes the dimension, never
        /// the freedom). Contacts remove translations along the contact
        /// normal; only the plane-against-line-like case also removes a
        /// rotation (the tilt that would lift the line off the plane:
        /// surface tangency in SolidWorks keeps the axis parallel to the
        /// plane). Point contacts (spheres, vertices) kill no rotation.
        /// Returns false when the entity combination has no rule.
        /// </summary>
        private static bool ApplyContactKill(MotionState s, GraphMate m)
        {
            GraphMateEntity plane = null, lineA = null, lineB = null, pointA = null, pointB = null;
            foreach (var e in m.Entities)
            {
                if (e.EntityTypeName == "plane" && e.Direction != null)
                {
                    if (plane == null) plane = e;
                }
                else if (e.Direction != null)
                {
                    if (lineA == null) lineA = e;
                    else if (lineB == null) lineB = e;
                }
                else if (e.Point != null)
                {
                    if (pointA == null) pointA = e;
                    else if (pointB == null) pointB = e;
                }
            }

            if (plane != null)
            {
                var n = MathOps.Normalized(plane.Direction);
                if (lineA != null)
                {
                    RestrictTransToPlane(s, n);
                    var a = MathOps.Normalized(lineA.Direction);
                    if (!MateFacts.IsParallel(n, a))
                        KillRotDirection(s, MathOps.Cross(n, a));
                    return true;
                }
                if (pointA != null)
                {
                    RestrictTransToPlane(s, n);
                    return true;
                }
                return false;
            }
            if (lineA != null && lineB != null)
            {
                var a1 = MathOps.Normalized(lineA.Direction);
                var a2 = MathOps.Normalized(lineB.Direction);
                if (MateFacts.IsParallel(a1, a2))
                {
                    // Parallel cylinders held apart: the radial direction dies.
                    if (lineA.Point == null || lineB.Point == null) return false;
                    var radial = PerpComponent(Delta(lineB.Point, lineA.Point), a1);
                    if (MathOps.Norm(radial) < 1e-9) return false;
                    RestrictTransToPlane(s, MathOps.Normalized(radial));
                }
                else
                {
                    // Crossed cylinders touch at a point along their common
                    // normal.
                    RestrictTransToPlane(s, MathOps.Normalized(MathOps.Cross(a1, a2)));
                }
                return true;
            }
            if (lineA != null && pointA != null)
            {
                if (lineA.Point == null) return false;
                var foot = MathOps.ClosestPointOnLineToPoint(
                    pointA.Point, MathOps.Normalized(lineA.Direction), lineA.Point);
                var radial = Delta(pointA.Point, foot);
                if (MathOps.Norm(radial) < 1e-9) return false;
                RestrictTransToPlane(s, MathOps.Normalized(radial));
                return true;
            }
            if (pointA != null && pointB != null)
            {
                // Two centres a fixed distance apart (sphere-sphere, or a
                // point-to-point distance): the line between them dies.
                var radial = Delta(pointB.Point, pointA.Point);
                if (MathOps.Norm(radial) < 1e-9) return false;
                RestrictTransToPlane(s, MathOps.Normalized(radial));
                return true;
            }
            return false;
        }

        /// <summary>
        /// A symmetric mate makes one side's geometry the mirror of the
        /// other about the symmetry plane. Between TWO bodies (the plane on
        /// one, both mirrored entities on the other: the common "centre
        /// this part" use) the net effect is a plane coincidence with the
        /// mid-plane: one translation and two tilts die. Every recorded
        /// direction agrees on that normal once solved; when none is
        /// recorded, two mirrored points define it. Mixed directions (line
        /// pairs mirrored about an off-axis plane) have no rule yet and stay
        /// unmodelled. A symmetric mate spanning THREE bodies is a motion
        /// coupling this resolver cannot see at all. ExportCommand warns.
        /// </summary>
        private static void ApplySymmetric(MotionState s, GraphMate m)
        {
            double[] n = null;
            bool mixed = false;
            foreach (var e in m.Entities)
            {
                if (e.Direction == null) continue;
                var d = MathOps.Normalized(e.Direction);
                if (n == null) n = d;
                else if (!MateFacts.IsParallel(n, d)) mixed = true;
            }
            if (n != null && !mixed)
            {
                ApplyPlane(s, n);
                return;
            }
            if (mixed)
            {
                // The mirrored planes are NOT parallel to each other: two
                // faces of one body meeting at an angle, held symmetric about
                // a plane. That says exactly one thing about the body: its
                // own bisector of those two faces lies IN the mirror plane.
                // So it is a plane coincidence on the mirror's normal, and
                // the mirror is whichever entity reflects the other two onto
                // each other. Live TongRig (2026-09-14): the base section's
                // two side faces sit 15 degrees either side of X, mirrored
                // about the assembly's own Right plane. Unmodelled, the base
                // kept a slide along X it does not have, and the whole tong
                // exported as sliding relative to its ground.
                var mirror = MirrorOf(m);
                if (mirror != null)
                {
                    ApplyPlane(s, MathOps.Normalized(mirror.Direction));
                    return;
                }
            }
            if (n == null)
            {
                var pts = new List<double[]>();
                foreach (var e in m.Entities)
                    if (e.Point != null) pts.Add(e.Point);
                if (pts.Count >= 2)
                {
                    var axis = Delta(pts[1], pts[0]);
                    if (MathOps.Norm(axis) > 1e-9)
                    {
                        ApplyPlane(s, MathOps.Normalized(axis));
                        return;
                    }
                }
            }
            s.Unmodelled++;
        }

        /// <summary>The one plane of a symmetric mate that reflects the
        /// other two onto each other, or null. Compared as plane equations
        /// (unit normal plus signed offset, sign allowed to flip), never as
        /// entity points: the mirrored entities ride the moving bodies and
        /// only their PLANES are related to the mirror.</summary>
        private static GraphMateEntity MirrorOf(GraphMate m)
        {
            var planes = new List<GraphMateEntity>();
            foreach (var e in m.Entities)
                if (e.Direction != null && e.Point != null
                    && MathOps.Norm(e.Direction) > 1e-9)
                    planes.Add(e);
            if (planes.Count != 3) return null;
            for (int k = 0; k < 3; k++)
            {
                var mirror = planes[k];
                var a = planes[(k + 1) % 3];
                var b = planes[(k + 2) % 3];
                var n = MathOps.Normalized(mirror.Direction);
                var na = MathOps.Normalized(a.Direction);
                var nb = MathOps.Normalized(b.Direction);
                double d = MathOps.Dot(na, n);
                var ra = new[] { na[0] - 2.0 * d * n[0],
                                 na[1] - 2.0 * d * n[1],
                                 na[2] - 2.0 * d * n[2] };
                if (!MateFacts.IsParallel(ra, nb)) continue;
                // The plane of `a`, carried through the mirror, must be the
                // plane of `b`: same signed offset along the shared normal.
                double away = MathOps.Dot(Delta(a.Point, mirror.Point), n);
                var pa = new[] { a.Point[0] - 2.0 * away * n[0],
                                 a.Point[1] - 2.0 * away * n[1],
                                 a.Point[2] - 2.0 * away * n[2] };
                double da = MathOps.Dot(ra, pa);
                double db = MathOps.Dot(nb, b.Point);
                if (MathOps.Dot(ra, nb) < 0.0) da = -da;
                // The planes of a SOLVED symmetric agree to SolidWorks' own
                // mate tolerance, around 1e-8 m. Ten microns is a hundred
                // times that and still a thousand times finer than any real
                // clearance, so it admits a logged value rounded to five
                // figures without admitting a plane that is somewhere else.
                double tol = 1e-5 * Math.Max(1.0, Math.Abs(db));
                if (Math.Abs(da - db) <= tol) return mirror;
            }
            return null;
        }

        private static double[] Delta(double[] to, double[] from)
        {
            return new[] { to[0] - from[0], to[1] - from[1], to[2] - from[2] };
        }

        private static double[] PerpComponent(double[] v, double[] unitAxis)
        {
            double along = MathOps.Dot(v, unitAxis);
            return new[]
            {
                v[0] - along * unitAxis[0],
                v[1] - along * unitAxis[1],
                v[2] - along * unitAxis[2],
            };
        }

        /// <summary>First direction-carrying entity that is not a plane or a
        /// sphere, that is the line-likes: axis, edge, cylinder, cone.</summary>
        private static GraphMateEntity FirstLineEntity(GraphMate m)
        {
            foreach (var e in m.Entities)
            {
                if (e.Direction == null) continue;
                if (e.EntityTypeName == "plane" || e.EntityTypeName == "sphere") continue;
                return e;
            }
            return null;
        }

        /// <summary>An entity that is DEFINITELY a point: typed as one, with
        /// no direction. Kind-unknown point-carriers stay out: they may be a
        /// plane that lost its normal, and weakening a plane rule on their
        /// account would free motion that does not exist.</summary>
        private static GraphMateEntity FindTypedPointEntity(GraphMate m)
        {
            foreach (var e in m.Entities)
            {
                if (e.Direction != null || e.Point == null) continue;
                if (e.EntityTypeName == "point" || e.EntityTypeName == "origin"
                    || e.EntityTypeName == "vertex")
                    return e;
            }
            return null;
        }

        /// <summary>Removes one direction from the allowed rotation set. Exact
        /// only against a state already narrowed to a single direction: the
        /// lattice cannot hold a two-direction span, so Full/AboutPoint stay
        /// as they are and flag Unmodelled (overstating mobility, which is the
        /// flag's contract; those pairs have no recognised joint type anyway).</summary>
        private static void KillRotDirection(MotionState s, double[] killed)
        {
            if (MathOps.Norm(killed) < MateFacts.ParallelTol) { s.Unmodelled++; return; }
            var k = MathOps.Normalized(killed);
            switch (s.Rot)
            {
                case RotFreedom.None:
                    return;
                case RotFreedom.AboutLine:
                case RotFreedom.AboutDirection:
                    if (Math.Abs(MathOps.Dot(MathOps.Normalized(s.RotDir), k)) > 1e-6)
                        RotNone(s);
                    return;
                default:
                    s.Unmodelled++;
                    return;
            }
        }

        private static void ApplyPointCoincidence(MotionState s, double[] point)
        {
            s.TransDirs.Clear();
            switch (s.Rot)
            {
                case RotFreedom.Full:
                    s.Rot = RotFreedom.AboutPoint;
                    s.RotPoint = point;
                    break;
                case RotFreedom.AboutPoint:
                    if (MathOps.Distance2(s.RotPoint, point) > MateFacts.CollinearTol * MateFacts.CollinearTol)
                    {
                        s.Rot = RotFreedom.None;
                        s.RotDir = null;
                    }
                    break;
                case RotFreedom.AboutLine:
                    if (MateFacts.DistancePointToLine(point, s.RotDir, s.RotPoint) > MateFacts.CollinearTol)
                    {
                        s.Rot = RotFreedom.None;
                        s.RotDir = null;
                    }
                    break;
                case RotFreedom.AboutDirection:
                    s.Rot = RotFreedom.AboutLine;
                    s.RotPoint = point;
                    break;
            }
        }

        // ── Rotation intersection ───────────────────────────────────────────

        private static void RestrictRotToLine(MotionState s, double[] dir, double[] point)
        {
            switch (s.Rot)
            {
                case RotFreedom.Full:
                    s.Rot = RotFreedom.AboutLine;
                    s.RotDir = dir;
                    s.RotPoint = point;
                    return;
                case RotFreedom.AboutPoint:
                    if (MateFacts.DistancePointToLine(s.RotPoint, dir, point) <= MateFacts.CollinearTol)
                    {
                        s.Rot = RotFreedom.AboutLine;
                        s.RotDir = dir;
                        s.RotPoint = point;
                    }
                    else RotNone(s);
                    return;
                case RotFreedom.AboutLine:
                    if (!MateFacts.IsParallel(s.RotDir, dir)
                        || MateFacts.DistancePointToLine(point, s.RotDir, s.RotPoint) > MateFacts.CollinearTol)
                        RotNone(s);
                    return;
                case RotFreedom.AboutDirection:
                    if (MateFacts.IsParallel(s.RotDir, dir))
                    {
                        s.Rot = RotFreedom.AboutLine;
                        s.RotDir = dir;
                        s.RotPoint = point;
                    }
                    else RotNone(s);
                    return;
            }
        }

        private static void RestrictRotToDirection(MotionState s, double[] dir)
        {
            switch (s.Rot)
            {
                case RotFreedom.Full:
                    s.Rot = RotFreedom.AboutDirection;
                    s.RotDir = MathOps.Normalized(dir);
                    return;
                case RotFreedom.AboutPoint:
                    s.Rot = RotFreedom.AboutLine;
                    s.RotDir = MathOps.Normalized(dir);
                    return;
                case RotFreedom.AboutLine:
                case RotFreedom.AboutDirection:
                    if (!MateFacts.IsParallel(s.RotDir, dir)) RotNone(s);
                    return;
            }
        }

        private static void RotNone(MotionState s)
        {
            s.Rot = RotFreedom.None;
            s.RotDir = null;
        }

        // ── Translation intersection ────────────────────────────────────────

        private static void RestrictTransToLine(MotionState s, double[] dir)
        {
            var d = MathOps.Normalized(dir);
            // span{d} ∩ subspace is {d} when d lies in the subspace, else {0}.
            var residual = (double[])d.Clone();
            foreach (var b in s.TransDirs)
            {
                double along = MathOps.Dot(residual, b);
                for (int i = 0; i < 3; i++) residual[i] -= along * b[i];
            }
            s.TransDirs.Clear();
            if (MathOps.Norm(residual) < 1e-6) s.TransDirs.Add(d);
        }

        private static void RestrictTransToPlane(MotionState s, double[] normal)
        {
            // Remove the subspace's component along the normal; directions
            // already perpendicular to it survive untouched.
            var n = MathOps.Normalized(normal);
            var inSubspace = new double[3];
            foreach (var b in s.TransDirs)
            {
                double along = MathOps.Dot(n, b);
                for (int i = 0; i < 3; i++) inSubspace[i] += along * b[i];
            }
            if (MathOps.Norm(inSubspace) < 1e-6) return;    // normal ⊥ subspace
            var kill = MathOps.Normalized(inSubspace);

            var survivors = new List<double[]>();
            foreach (var b in s.TransDirs)
            {
                var r = (double[])b.Clone();
                double along = MathOps.Dot(r, kill);
                for (int i = 0; i < 3; i++) r[i] -= along * kill[i];
                foreach (var kept in survivors)
                {
                    double d2 = MathOps.Dot(r, kept);
                    for (int i = 0; i < 3; i++) r[i] -= d2 * kept[i];
                }
                if (MathOps.Norm(r) > 1e-6) survivors.Add(MathOps.Normalized(r));
            }
            s.TransDirs = survivors;
        }

        // ── Entity helpers ──────────────────────────────────────────────────

        private static GraphMateEntity FindEntity(GraphMate m, string kind)
        {
            foreach (var e in m.Entities)
                if (e.EntityTypeName == kind) return e;
            return null;
        }

        private static bool HasLineEntity(GraphMate m)
        {
            foreach (var e in m.Entities)
            {
                if (e.Direction == null) continue;
                if (e.EntityTypeName == "plane" || e.EntityTypeName == "sphere") continue;
                return true;    // axis, edge, cylinder, cone, unknown-with-direction
            }
            return false;
        }

        private static double[] FindPoint(GraphMate m)
        {
            foreach (var e in m.Entities)
            {
                if (e.Point == null) continue;
                if (e.EntityTypeName == "point" || e.EntityTypeName == "origin"
                    || e.EntityTypeName == "vertex")
                    return e.Point;
            }
            return null;
        }

        /// <summary>First entity carrying a point, whatever its kind: for
        /// mate types whose entities are always point-like (COORDINATE).</summary>
        private static double[] FindAnyPoint(GraphMate m)
        {
            foreach (var e in m.Entities)
                if (e.Point != null) return e.Point;
            return null;
        }
    }
}
