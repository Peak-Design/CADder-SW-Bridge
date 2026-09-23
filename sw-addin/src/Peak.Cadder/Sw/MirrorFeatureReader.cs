using System;
using System.Collections.Generic;
using System.Globalization;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using SolidWorks.Interop.sldworks;

namespace Peak.Cadder.Sw
{
    /// <summary>
    /// Assembly mirror features, read as source/instance occurrence pairs.
    ///
    /// A mirrored instance carries no mate to its source, so nothing in the
    /// mate graph says the two move together, but SolidWorks keeps them
    /// reflections of one another, and a rig that ignores that lets the two
    /// halves of a mirrored mechanism drift apart.
    ///
    /// The feature names the SOURCE components and the mirror plane. It does
    /// not name the instances it created. So the feature supplies the
    /// candidates and the plane, and the GEOMETRY supplies the pairing.
    ///
    /// The instance is never a reflection of its source's placement:
    /// SolidWorks places every component by a proper rigid transform. A
    /// mirrored copy is the same part turned into one of four orientations,
    /// and an opposite-hand version is a new part that is already mirrored.
    /// So the instance B of source A is the reflection R across the plane
    /// composed with a mirror L of the part in its own frame: B = R x A x L.
    /// L = inverse(B) x R x A is then an involution: a reflection about a
    /// plane of the part, or an inversion through a point of it. Until
    /// 2026-09-22 the reader asked B x inverse(A) itself to be a reflection,
    /// which no two SolidWorks placements are, and no mirror feature ever
    /// paired (live engine sample: "1 source(s), 0 reflected instance(s)").
    ///
    /// Everything here is read-only. AccessSelections/ReleaseSelectionAccess
    /// is the documented way to READ a feature's selection properties; the
    /// release runs in a finally so an exception cannot leave the feature in
    /// selection-access mode.
    /// </summary>
    public static class MirrorFeatureReader
    {
        /// <summary>Two candidate planes are the same plane within this much
        /// (unit normal dot product, and metres of offset). Mirror instances
        /// created by one feature share their plane exactly; the tolerance is
        /// only there for float noise in the transforms.</summary>
        private const double PlaneTol = 1e-6;

        /// <summary>How far the local mirror may be from an involution, in
        /// its rotation entries and in meters. Placements are exact to float
        /// noise.</summary>
        private const double MirrorTol = 1e-6;

        /// <summary>One occurrence as the pairing sees it: plain data, so
        /// the pairing runs without SolidWorks.</summary>
        internal sealed class MirrorOccurrence
        {
            public string Id;
            public string File;              // the document's file name, null when unknown
            public double[,] Transform;      // world, as Transform2 gives it
            public double[] BoxMin, BoxMax;  // world box, null when unknown
            public bool OppositeHand;        // a source mirrored as a new, opposite-hand part
        }

        /// <summary>What one feature says: its sources and its plane.</summary>
        private sealed class FeatureRead
        {
            public readonly List<string> Sources = new List<string>();
            public readonly HashSet<string> OppositeHand =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>The plane as (point, unit normal), world frame, most
            /// likely reading first. Empty when the plane could not be read.</summary>
            public readonly List<double[][]> Planes = new List<double[][]>();
        }

        public static void Read(
            IModelDoc2 model, List<WalkedComponent> walked, MateGraph graph,
            Action<string> log)
        {
            if (model == null || walked == null || graph == null) return;

            var byName = new Dictionary<string, WalkedComponent>(
                StringComparer.OrdinalIgnoreCase);
            var all = new List<MirrorOccurrence>();
            var occurrenceOf = new Dictionary<WalkedComponent, MirrorOccurrence>();
            foreach (var w in walked)
            {
                if (w.Graph == null || w.Graph.Path == null || w.Graph.Suppressed) continue;
                byName[w.Graph.Path] = w;
                if (w.Graph.Transform == null) continue;
                var occ = new MirrorOccurrence
                {
                    Id = w.Graph.Id,
                    File = w.Graph.FileName,
                    Transform = w.Graph.Transform,
                    BoxMin = w.Graph.BboxMin,
                    BoxMax = w.Graph.BboxMax,
                };
                all.Add(occ);
                occurrenceOf[w] = occ;
            }

            foreach (var feature in MirrorFeatures(model, log))
            {
                string name = SafeName(feature);
                FeatureRead read;
                try { read = ReadFeature(model, feature, log); }
                catch (Exception ex)
                {
                    if (log != null) log("mirror feature " + name + ": " + ex.Message);
                    continue;
                }
                if (read.Sources.Count == 0) continue;

                var sources = new List<MirrorOccurrence>();
                foreach (string path in read.Sources)
                {
                    WalkedComponent w;
                    MirrorOccurrence occ;
                    if (!byName.TryGetValue(path, out w) || !occurrenceOf.TryGetValue(w, out occ)) continue;
                    sources.Add(new MirrorOccurrence
                    {
                        Id = occ.Id,
                        File = occ.File,
                        Transform = occ.Transform,
                        BoxMin = occ.BoxMin,
                        BoxMax = occ.BoxMax,
                        OppositeHand = read.OppositeHand.Contains(path),
                    });
                }

                if (read.Planes.Count == 0 && log != null)
                    log("mirror feature " + name + ": the mirror plane could not be read; "
                        + "the plane is taken from the placements");
                var pairs = Pair(name, sources, all, read.Planes);
                foreach (var p in pairs) graph.MirrorPairs.Add(p);
                if (log != null)
                    log("mirror feature " + name + ": " + read.Sources.Count
                        + " source(s), " + pairs.Count + " reflected instance(s)"
                        + (pairs.Count > 0
                            ? ", plane normal " + Fmt(pairs[0].PlaneNormal) + " through " + Fmt(pairs[0].PlanePoint)
                            : ""));
            }
        }

        // ── Feature discovery ───────────────────────────────────────────────

        /// <summary>Every top-level feature whose definition is mirror
        /// component data. The type name is not the gate (the cast is), so a
        /// SolidWorks version that renames the feature type still works; the
        /// name only narrows which features get a GetDefinition call.</summary>
        private static List<IFeature> MirrorFeatures(IModelDoc2 model, Action<string> log)
        {
            var found = new List<IFeature>();
            try
            {
                for (var f = model.FirstFeature() as IFeature; f != null;
                     f = f.GetNextFeature() as IFeature)
                {
                    string type = null;
                    try { type = f.GetTypeName2(); } catch { }
                    if (type == null
                        || type.IndexOf("Mirror", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    object def = null;
                    try { def = f.GetDefinition(); } catch { }
                    if (def is IMirrorComponentFeatureData) found.Add(f);
                    else if (log != null)
                        log("mirror-like feature " + SafeName(f) + " (" + type
                            + ") has no mirror component data; skipped");
                }
            }
            catch (Exception ex)
            {
                if (log != null) log("mirror feature scan: " + ex.Message);
            }
            return found;
        }

        /// <summary>The instance paths of the components this feature
        /// mirrors (both alignment modes and the opposite-hand list), and its
        /// plane. Selection properties need selection access, and the release
        /// is unconditional.</summary>
        private static FeatureRead ReadFeature(
            IModelDoc2 model, IFeature feature, Action<string> log)
        {
            var read = new FeatureRead();
            var def = feature.GetDefinition() as IMirrorComponentFeatureData;
            if (def == null) return read;

            bool opened = false;
            try { opened = def.AccessSelections(model, null); }
            catch (Exception ex)
            {
                if (log != null)
                    log("mirror feature " + SafeName(feature)
                        + ": selection access refused (" + ex.Message + ")");
                return read;
            }
            if (!opened)
            {
                if (log != null)
                    log("mirror feature " + SafeName(feature)
                        + ": selection access refused");
                return read;
            }

            try
            {
                Collect(def.ComponentsToInstanceAlignToComponentOrigin, read.Sources, null);
                Collect(def.ComponentsToInstanceAlignToSelection, read.Sources, null);
                // The sources that became a NEW, mirrored part. Their
                // instance is another document.
                object opposite = null;
                try { opposite = def.OppositeHandComponents; } catch { }
                Collect(opposite, read.Sources, read.OppositeHand);

                object plane = null;
                try { plane = def.MirrorPlane; } catch { }
                PlaneReadings(plane, read.Planes, SafeName(feature), log);
            }
            finally
            {
                try { def.ReleaseSelectionAccess(); } catch { }
            }
            return read;
        }

        private static void Collect(object components, List<string> paths, HashSet<string> mark)
        {
            var array = components as object[];
            if (array == null) return;
            foreach (var o in array)
            {
                var c = o as Component2;
                if (c == null) continue;
                string name = null;
                try { name = c.Name2; } catch { }
                if (name == null) continue;
                if (!paths.Contains(name)) paths.Add(name);
                if (mark != null) mark.Add(name);
            }
        }

        /// <summary>
        /// The mirror plane, IFace2 or IRefPlane (API help,
        /// IMirrorComponentFeatureData~MirrorPlane), as world (point, normal)
        /// readings. A reference plane's Transform takes the Front plane (its
        /// normal is Z) to the plane, "with respect to the world". A face's
        /// PlaneParams are in its part's frame. Two things are not
        /// documented: the frame of a COMPONENT's reference plane read in the
        /// assembly, and the sign of the Transform's translation (the two
        /// API examples read it with opposite signs). Each open reading is
        /// kept, and the pairing keeps the one the placements confirm.
        /// </summary>
        private static void PlaneReadings(
            object plane, List<double[][]> readings, string feature, Action<string> log)
        {
            if (plane == null)
            {
                if (log != null) log("mirror feature " + feature + ": no mirror plane in the definition");
                return;
            }
            try
            {
                var asFeature = plane as IFeature;
                var refPlane = plane as IRefPlane
                    ?? (asFeature == null ? null : asFeature.GetSpecificFeature2() as IRefPlane);
                double[,] owner = OwnerWorld(plane) ?? OwnerWorld(refPlane);
                if (refPlane != null)
                {
                    var m = SwFrames.ToMatrix(refPlane.Transform);
                    if (m == null) return;
                    var normal = MathOps.RotateVector(m, new[] { 0.0, 0.0, 1.0 });
                    var point = new[] { m[0, 3], m[1, 3], m[2, 3] };
                    var negated = new[] { -m[0, 3], -m[1, 3], -m[2, 3] };
                    AddPlane(readings, point, normal, null);
                    AddPlane(readings, negated, normal, null);
                    if (owner != null)
                    {
                        AddPlane(readings, point, normal, owner);
                        AddPlane(readings, negated, normal, owner);
                    }
                    return;
                }
                var face = plane as IFace2;
                if (face != null)
                {
                    var surf = face.GetSurface() as ISurface;
                    var pp = surf == null || !surf.IsPlane() ? null : surf.PlaneParams as double[];
                    if (pp == null || pp.Length < 6)
                    {
                        if (log != null) log("mirror feature " + feature + ": the mirror face is not planar");
                        return;
                    }
                    var normal = new[] { pp[0], pp[1], pp[2] };
                    var point = new[] { pp[3], pp[4], pp[5] };
                    if (owner != null) AddPlane(readings, point, normal, owner);
                    AddPlane(readings, point, normal, null);
                    return;
                }
                if (log != null)
                    log("mirror feature " + feature + ": the mirror plane is neither a face nor a plane");
            }
            catch (Exception ex)
            {
                if (log != null) log("mirror feature " + feature + ": mirror plane unreadable: " + ex.Message);
            }
        }

        /// <summary>The world transform of the component an entity belongs
        /// to, or null for the assembly's own entity.</summary>
        private static double[,] OwnerWorld(object entity)
        {
            var e = entity as IEntity;
            if (e == null) return null;
            try { return SwFrames.ComponentWorld(e.GetComponent() as Component2); }
            catch { return null; }
        }

        private static void AddPlane(
            List<double[][]> readings, double[] point, double[] normal, double[,] lift)
        {
            var n = MathOps.Normalized(lift == null ? normal : MathOps.RotateVector(lift, normal));
            if (n == null) return;
            var p = lift == null ? point : MathOps.TransformPoint(lift, point);
            foreach (var r in readings)
                if (SamePlane(r[0], r[1], p, n)) return;
            readings.Add(new[] { p, n });
        }

        // ── Pairing ─────────────────────────────────────────────────────────

        /// <summary>
        /// The instance of each source, across the feature's plane. With
        /// several plane readings, the one that pairs the most sources wins
        /// (the first on a tie). With none, the plane is taken from the
        /// placements: a copy placed by its origin gives it exactly.
        /// </summary>
        internal static List<GraphMirrorPair> Pair(
            string featureName, IList<MirrorOccurrence> sources,
            IList<MirrorOccurrence> others, IList<double[][]> planes)
        {
            var best = new List<GraphMirrorPair>();
            if (sources == null || others == null) return best;
            var readings = planes != null && planes.Count > 0
                ? planes : PlanesFromPlacements(sources, others);
            foreach (var plane in readings)
            {
                var pairs = PairAcross(featureName, sources, others, plane[0], plane[1]);
                if (pairs.Count > best.Count) best = pairs;
            }
            return best;
        }

        /// <summary>
        /// For each source, the occurrence whose placement is the source's
        /// mirrored across this plane: B = R x A x L with L an involution of
        /// the part. A copy is the same document and an opposite-hand version
        /// is another. Each occurrence is one source's instance at most, and
        /// a source is never an instance.
        ///
        /// A point inversion (both axes flipped) is an involution wherever it
        /// sits, so for one the placement does not fix the place. It is
        /// taken only where the origins are in mirror or the boxes are, and
        /// the nearest box wins among several.
        /// </summary>
        private static List<GraphMirrorPair> PairAcross(
            string featureName, IList<MirrorOccurrence> sources,
            IList<MirrorOccurrence> others, double[] planePoint, double[] planeNormal)
        {
            var pairs = new List<GraphMirrorPair>();
            var n = MathOps.Normalized(planeNormal);
            if (n == null || planePoint == null) return pairs;
            var r = Reflection(n, planePoint);

            var sourceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in sources) if (s != null && s.Id != null) sourceIds.Add(s.Id);
            var claimed = new HashSet<string>(StringComparer.Ordinal);

            foreach (var src in sources)
            {
                if (src == null || src.Transform == null) continue;
                var ra = MathOps.Multiply(r, src.Transform);
                MirrorOccurrence pick = null;
                double pickScore = double.MaxValue;
                foreach (var other in others)
                {
                    if (other == null || other.Transform == null || other.Id == null) continue;
                    if (sourceIds.Contains(other.Id) || claimed.Contains(other.Id)) continue;
                    if (!DocumentFits(src, other)) continue;

                    var local = MathOps.Multiply(MathOps.InvertRigid(other.Transform), ra);
                    bool inversion;
                    if (!IsLocalMirror(local, out inversion)) continue;
                    double score = BoxMismatch(r, src, other);
                    if (inversion)
                    {
                        double shift = Math.Sqrt(local[0, 3] * local[0, 3]
                            + local[1, 3] * local[1, 3] + local[2, 3] * local[2, 3]);
                        if (shift > MirrorTol && !(score <= BoxTolerance(src))) continue;
                    }
                    if (double.IsNaN(score)) score = double.MaxValue / 2;
                    if (pick == null || score < pickScore)
                    {
                        pick = other;
                        pickScore = score;
                    }
                }
                if (pick == null) continue;
                claimed.Add(pick.Id);
                pairs.Add(new GraphMirrorPair
                {
                    FeatureName = featureName,
                    SourceComponentId = src.Id,
                    MirroredComponentId = pick.Id,
                    PlanePoint = (double[])planePoint.Clone(),
                    PlaneNormal = n,
                });
            }
            return pairs;
        }

        /// <summary>A copy is an instance of the source's own document, an
        /// opposite-hand version an instance of another. An unknown file
        /// name decides nothing.</summary>
        private static bool DocumentFits(MirrorOccurrence src, MirrorOccurrence other)
        {
            if (string.IsNullOrEmpty(src.File) || string.IsNullOrEmpty(other.File)) return true;
            bool same = string.Equals(src.File, other.File, StringComparison.OrdinalIgnoreCase);
            return src.OppositeHand ? !same : same;
        }

        /// <summary>
        /// True when <paramref name="l"/> mirrors a part onto itself: its
        /// rotation part is symmetric, squares to the identity and has
        /// determinant -1 (a reflection, or an inversion when
        /// <paramref name="inversion"/>), and applied twice it is the
        /// identity, so its translation has no part along the mirror plane.
        /// A dragged instance fails that last test.
        /// </summary>
        internal static bool IsLocalMirror(double[,] l, out bool inversion)
        {
            inversion = false;
            double det =
                l[0, 0] * (l[1, 1] * l[2, 2] - l[1, 2] * l[2, 1])
                - l[0, 1] * (l[1, 0] * l[2, 2] - l[1, 2] * l[2, 0])
                + l[0, 2] * (l[1, 0] * l[2, 1] - l[1, 1] * l[2, 0]);
            if (det > -0.99 || det < -1.01) return false;
            var twice = MathOps.Multiply(l, l);
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    if (Math.Abs(l[i, j] - l[j, i]) > MirrorTol) return false;
                    if (Math.Abs(twice[i, j] - (i == j ? 1.0 : 0.0)) > MirrorTol) return false;
                }
                if (Math.Abs(twice[i, 3]) > MirrorTol) return false;
            }
            inversion = l[0, 0] + l[1, 1] + l[2, 2] < -2.5;
            return true;
        }

        /// <summary>World reflection across the plane through
        /// <paramref name="p"/> with unit normal <paramref name="n"/>.</summary>
        private static double[,] Reflection(double[] n, double[] p)
        {
            var r = MathOps.Identity4();
            double along = MathOps.Dot(n, p);
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                    r[i, j] = (i == j ? 1.0 : 0.0) - 2.0 * n[i] * n[j];
                r[i, 3] = 2.0 * along * n[i];
            }
            return r;
        }

        /// <summary>How far the other box's center is from the mirror of
        /// the source box's center, or NaN when a box is unknown. GetBox is
        /// not the tightest box, so this ranks candidates and is never the
        /// proof of a mirror.</summary>
        private static double BoxMismatch(double[,] r, MirrorOccurrence src, MirrorOccurrence other)
        {
            var a = Centre(src);
            var b = Centre(other);
            if (a == null || b == null) return double.NaN;
            return Math.Sqrt(MathOps.Distance2(MathOps.TransformPoint(r, a), b));
        }

        private static double BoxTolerance(MirrorOccurrence src)
        {
            if (src.BoxMin == null || src.BoxMax == null) return 1e-4;
            return Math.Max(1e-4, 0.01 * Math.Sqrt(MathOps.Distance2(src.BoxMin, src.BoxMax)));
        }

        private static double[] Centre(MirrorOccurrence o)
        {
            if (o.BoxMin == null || o.BoxMax == null || o.BoxMin.Length < 3 || o.BoxMax.Length < 3)
                return null;
            return new[]
            {
                0.5 * (o.BoxMin[0] + o.BoxMax[0]),
                0.5 * (o.BoxMin[1] + o.BoxMax[1]),
                0.5 * (o.BoxMin[2] + o.BoxMax[2]),
            };
        }

        /// <summary>
        /// Planes read off the placements when the feature's plane could not
        /// be: for each source and each document-fitting occurrence, the
        /// world reflection that takes the source, turned into each of the
        /// four mirror orientations, onto the occurrence. Only a copy placed
        /// by its origin gives one exactly, so this is the fallback and not
        /// the rule. Every plane found is tried, and the one that pairs the
        /// most wins, as a set that shares one plane.
        /// </summary>
        private static List<double[][]> PlanesFromPlacements(
            IList<MirrorOccurrence> sources, IList<MirrorOccurrence> others)
        {
            var planes = new List<double[][]>();
            var orientations = new[]
            {
                new[] { 1.0, 1.0, -1.0 }, new[] { -1.0, 1.0, 1.0 },
                new[] { 1.0, -1.0, 1.0 }, new[] { -1.0, -1.0, -1.0 },
            };
            foreach (var src in sources)
            {
                if (src == null || src.Transform == null) continue;
                foreach (var other in others)
                {
                    if (other == null || other.Transform == null || other.Id == src.Id) continue;
                    if (!DocumentFits(src, other)) continue;
                    foreach (var o in orientations)
                    {
                        var flip = MathOps.Identity4();
                        flip[0, 0] = o[0]; flip[1, 1] = o[1]; flip[2, 2] = o[2];
                        double[] point, normal;
                        if (!MathOps.TryReflectionPlane(
                                MathOps.Multiply(src.Transform, flip), other.Transform,
                                out point, out normal))
                            continue;
                        AddPlane(planes, point, normal, null);
                    }
                }
            }
            return planes;
        }

        private static bool SamePlane(double[] pa, double[] na, double[] pb, double[] nb)
        {
            double dot = MathOps.Dot(na, nb);
            if (Math.Abs(Math.Abs(dot) - 1.0) > PlaneTol) return false;
            // Same plane, either normal sense: the offset of one point from
            // the other along the shared normal must vanish.
            double gap = 0.0;
            for (int i = 0; i < 3; i++)
                gap += (pb[i] - pa[i]) * na[i];
            return Math.Abs(gap) <= PlaneTol;
        }

        private static string Fmt(double[] v)
        {
            if (v == null) return "null";
            return "[" + v[0].ToString("0.####", CultureInfo.InvariantCulture) + ","
                + v[1].ToString("0.####", CultureInfo.InvariantCulture) + ","
                + v[2].ToString("0.####", CultureInfo.InvariantCulture) + "]";
        }

        private static string SafeName(IFeature f)
        {
            try { return f.Name; } catch { return "?"; }
        }
    }
}
