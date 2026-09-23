using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Peak.Cadder.Appearance
{
    /// <summary>One internal child of a subassembly instance, at the pose the
    /// instance actually shows in SolidWorks. Key is the child's instance name
    /// in the sub document ("hinge-leaf-1"): the stable correlator between
    /// the layouts of different instances of one document.</summary>
    public sealed class FlexChildPose
    {
        public string Key;
        /// <summary>The STEP PRODUCT name base (document file name without
        /// extension).</summary>
        public string ProductName;
        /// <summary>Row-major 4x4, child pose in the SUB's frame, metres.</summary>
        public double[,] LocalM;
    }

    /// <summary>One instance of a shared subassembly document with the
    /// internal layout it should show.</summary>
    public sealed class FlexInstanceLayout
    {
        /// <summary>SW instance path, for messages.</summary>
        public string Path;
        public string SubDocName;
        /// <summary>The instance's own position relative to its parent, metres:
        /// how the instance is told apart from its twins in the file.</summary>
        public double[] ParentRelTranslationM;
        public List<FlexChildPose> Children = new List<FlexChildPose>();
    }

    /// <summary>All instances of ONE shared subassembly document.</summary>
    public sealed class FlexFixRequest
    {
        public List<FlexInstanceLayout> Instances = new List<FlexInstanceLayout>();
    }

    public sealed class FlexFixOutcome
    {
        public int DefinitionsCloned;
        public int PlacementsRetargeted;
        public List<string> FixedPaths = new List<string>();
        public List<string> FailedPaths = new List<string>();
        public List<string> Notes = new List<string>();
    }

    /// <summary>
    /// Fixes the shared-flexible-geometry defect INSIDE the STEP file. STEP
    /// stores one internal layout per subassembly product definition, and
    /// SolidWorks writes the resolved (flexed) layout, so a rigid twin of a
    /// flexed flexible subassembly imports at the flexible instance's pose in
    /// every consumer. This class de-instances the definition (DeInstancer's
    /// assembly-structure clone, geometry stays shared) and retargets the
    /// cloned child placements to each instance's actual SolidWorks layout.
    ///
    /// Runs BEFORE the appearance passes, and keeps the rewriter's occurrence
    /// tables consistent (use ChildPd, ChildrenByParentPd, Translations), so
    /// the colour matcher sees the post-fix tree.
    /// </summary>
    public static class FlexiblePoseFixer
    {
        private const double PosTolM = 5e-4;    // matches NEXT-STEP's 0.1mm order
        private const double RotTolRad = 1e-3;

        public static FlexFixOutcome Fix(
            Part21 step,
            List<StepRewriter.OccurrenceRef> occurrences,
            Dictionary<int, List<StepRewriter.OccurrenceRef>> childrenByParentPd,
            List<FlexFixRequest> requests,
            Action<string> log)
        {
            var outcome = new FlexFixOutcome();
            var di = new DeInstancer(step, log);

            foreach (var request in requests)
                FixOne(step, di, occurrences, childrenByParentPd, request, outcome, log);
            return outcome;
        }

        private static void FixOne(
            Part21 step, DeInstancer di,
            List<StepRewriter.OccurrenceRef> occurrences,
            Dictionary<int, List<StepRewriter.OccurrenceRef>> childrenByParentPd,
            FlexFixRequest request, FlexFixOutcome outcome, Action<string> log)
        {
            var instances = request.Instances;
            if (instances.Count < 2) return;
            string docName = instances[0].SubDocName;

            // Every use of a matching sub-assembly definition in the file.
            // The exact document name comes first. The 'doc_config' form
            // also matches another document such as 'hinge_assy' next to
            // 'hinge', and its uses made the count wrong, so the prefix form
            // counts only when the exact name alone does not account for
            // every instance.
            var subUses = occurrences
                .Where(o => childrenByParentPd.ContainsKey(o.ChildPd)
                            && NameMatches(o.ProductName, docName))
                .ToList();
            var uses = subUses
                .Where(o => string.Equals(o.ProductName, docName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (uses.Count != instances.Count) uses = subUses;
            if (uses.Count != instances.Count)
            {
                Fail(outcome, instances, log, string.Format(CultureInfo.InvariantCulture,
                    "{0}: {1} SolidWorks instance(s) but {2} matching use(s) in the "
                    + "STEP file; layouts left as exported", docName, instances.Count,
                    uses.Count));
                return;
            }

            var byInstance = MatchInstancesToUses(instances, uses);
            if (byInstance == null)
            {
                Fail(outcome, instances, log, docName + ": could not match the "
                    + "SolidWorks instances to the STEP uses by position; "
                    + "layouts left as exported");
                return;
            }

            foreach (var defGroup in byInstance.GroupBy(kv => kv.Value.ChildPd))
            {
                int defPd = defGroup.Key;
                var members = defGroup.ToList();

                var fileChildren = ReadChildPlacements(step, childrenByParentPd[defPd]);
                if (fileChildren == null)
                {
                    Fail(outcome, members.Select(m => m.Key), log, docName
                        + ": a child placement chain could not be read; "
                        + "layouts left as exported");
                    continue;
                }

                // Layout-equality classes: rigid twins of one document share
                // one layout and therefore one clone.
                var classes = new List<List<FlexInstanceLayout>>();
                foreach (var m in members)
                {
                    var cls = classes.FirstOrDefault(
                        c => SameLayout(c[0], m.Key));
                    if (cls == null) classes.Add(cls = new List<FlexInstanceLayout>());
                    cls.Add(m.Key);
                }

                // Which class already matches what the file holds? That class
                // keeps the original definition untouched. When none does, the
                // largest class takes the original and gets patched in place.
                Assignment best = null;
                List<FlexInstanceLayout> fileClass = null;
                foreach (var cls in classes)
                {
                    var a = AssignChildren(cls[0], fileChildren);
                    if (a == null) continue;
                    if (best == null || a.Cost < best.Cost) best = a;
                    // Assignment errors are already in metres, whatever the
                    // file unit.
                    if (fileClass == null && a.MaxPosErr < PosTolM
                        && a.MaxRotErr < RotTolRad)
                        fileClass = cls;
                }
                if (best == null)
                {
                    Fail(outcome, members.Select(m => m.Key), log, docName
                        + ": the internal children of the STEP definition do not "
                        + "correspond to the SolidWorks sub-document; layouts "
                        + "left as exported");
                    continue;
                }

                var keeperClass = fileClass ?? classes.OrderByDescending(c => c.Count).First();

                // The canonical mapping file-child -> layout key comes from the
                // best assignment; all layouts of one document share the keys.
                var keyOf = best.KeyByChild;
                foreach (var inst in keeperClass) outcome.FixedPaths.Add(inst.Path);

                foreach (var cls in classes)
                {
                    if (cls == keeperClass) continue;
                    var map = di.CloneAssemblyStructure(defPd,
                        childrenByParentPd[defPd].Select(c => c.NauoId).ToList());
                    if (map == null)
                    {
                        Fail(outcome, cls, log, docName + ": definition clone "
                            + "failed; " + Describe(cls) + " keep the shared layout");
                        continue;
                    }
                    outcome.DefinitionsCloned++;

                    // Point this class's uses at the clone and mirror the
                    // occurrence records, exactly as the appearance split does.
                    var clonedKids = new List<StepRewriter.OccurrenceRef>();
                    foreach (var c in childrenByParentPd[defPd])
                        clonedKids.Add(new StepRewriter.OccurrenceRef
                        {
                            NauoId = map[c.NauoId],
                            ParentPd = map[defPd],
                            ChildPd = c.ChildPd,
                            ProductName = c.ProductName,
                            Translation = c.Translation,
                            TargetItems = c.TargetItems,
                            BaseStyledItemId = c.BaseStyledItemId,
                        });
                    childrenByParentPd[map[defPd]] = clonedKids;

                    foreach (var inst in cls)
                    {
                        var use = byInstance[inst];
                        di.PointOccurrenceAt(use, new DeInstancer.PartCopy { Map = map });
                        use.ChildPd = map[defPd];
                        outcome.FixedPaths.Add(inst.Path);
                    }

                    RetargetAll(step, fileChildren, cls[0], keyOf, map, outcome,
                        clonedKids);
                    log?.Invoke("    " + docName + ": cloned the definition ("
                        + map.Count + " entities, geometry shared) and reposed it "
                        + "for " + Describe(cls));
                }

                // The in-place patch runs LAST: Part21.Replace rewrites the
                // entity args the cloner reads, so patching before cloning
                // would leak the keeper's placements into every clone.
                if (fileClass == null)
                {
                    RetargetAll(step, fileChildren, keeperClass[0], keyOf,
                        null, outcome);
                    log?.Invoke("    " + docName + ": original definition reposed "
                        + "in place for " + Describe(keeperClass));
                }
            }
        }

        // ── Instance ↔ use matching ─────────────────────────────────────────

        /// <summary>
        /// Every length in this class is in millimetres, and SolidWorks gives
        /// metres. The occurrence tables are already in millimetres whatever
        /// the unit of the file (StepRewriter.ReadOccurrencePlacement), and
        /// FindPlacement converts the child placements the same way. Only
        /// AppendPlacement goes back to the unit of the file.
        ///
        /// An earlier version tried a scale of 1000 and then 1 on the raw
        /// file values. After the tables changed to millimetres, the scale
        /// always came out as 1000. In a metre or inch file the fix then
        /// compared raw metres with millimetres, and wrote millimetres into a
        /// metre context: the leaves came out 1000 times too far out.
        /// </summary>
        private const double MmPerM = 1000.0;

        private static Dictionary<FlexInstanceLayout, StepRewriter.OccurrenceRef>
            MatchInstancesToUses(
                List<FlexInstanceLayout> instances,
                List<StepRewriter.OccurrenceRef> uses)
        {
            const double scale = MmPerM;
            var candidates = new List<KeyValuePair<double,
                KeyValuePair<FlexInstanceLayout, StepRewriter.OccurrenceRef>>>();
            foreach (var inst in instances)
            {
                if (inst.ParentRelTranslationM == null) return null;
                foreach (var use in uses)
                {
                    if (use.Translation == null) return null;
                    double d = 0;
                    for (int i = 0; i < 3; i++)
                    {
                        double diff = use.Translation[i]
                            - inst.ParentRelTranslationM[i] * scale;
                        d += diff * diff;
                    }
                    candidates.Add(new KeyValuePair<double,
                        KeyValuePair<FlexInstanceLayout, StepRewriter.OccurrenceRef>>(
                        Math.Sqrt(d),
                        new KeyValuePair<FlexInstanceLayout, StepRewriter.OccurrenceRef>(
                            inst, use)));
                }
            }
            candidates.Sort((a, b) => a.Key.CompareTo(b.Key));

            double tol = PosTolM * scale;
            var result = new Dictionary<FlexInstanceLayout, StepRewriter.OccurrenceRef>();
            var taken = new HashSet<StepRewriter.OccurrenceRef>();
            foreach (var c in candidates)
            {
                if (result.ContainsKey(c.Value.Key) || taken.Contains(c.Value.Value))
                    continue;
                if (c.Key > tol) break;
                result[c.Value.Key] = c.Value.Value;
                taken.Add(c.Value.Value);
            }
            return result.Count == instances.Count ? result : null;
        }

        // ── File child placements ───────────────────────────────────────────

        private sealed class FileChild
        {
            public StepRewriter.OccurrenceRef Occ;
            public int IdtId;
            public int MovingId;
            public int RelId;
            /// <summary>Millimetres per unit of the representation that
            /// lists the placement. A new placement is written in this
            /// unit.</summary>
            public double UnitMm;
            public double[] Loc;         // millimetres
            public double[,] Rot;        // 3x3
        }

        private static List<FileChild> ReadChildPlacements(
            Part21 step, List<StepRewriter.OccurrenceRef> children)
        {
            var result = new List<FileChild>();
            foreach (var c in children)
            {
                var fc = FindPlacement(step, c);
                if (fc == null) return null;
                result.Add(fc);
            }
            return result;
        }

        /// <summary>NAUO → PDS → CDSR → *REPRESENTATION_RELATIONSHIP* → IDT;
        /// the IDT's first AXIS2_PLACEMENT_3D is the child's pose in the
        /// parent (the second is the shared child origin, see
        /// StepRewriter.ReadOccurrencePlacement).</summary>
        private static FileChild FindPlacement(Part21 step, StepRewriter.OccurrenceRef c)
        {
            int pds = step.ByType("PRODUCT_DEFINITION_SHAPE")
                .FirstOrDefault(p => step.Refs(p).Contains(c.NauoId));
            if (pds == 0) return null;
            int cdsr = step.ByType("CONTEXT_DEPENDENT_SHAPE_REPRESENTATION")
                .FirstOrDefault(cs => step.Refs(cs).Contains(pds));
            if (cdsr == 0) return null;
            foreach (var rel in step.Refs(cdsr))
            {
                var t = step.TypeOf(rel) ?? "";
                if (t.IndexOf("REPRESENTATION_RELATIONSHIP", StringComparison.Ordinal) < 0)
                    continue;
                foreach (var idt in step.Refs(rel))
                {
                    if (step.TypeOf(idt) != "ITEM_DEFINED_TRANSFORMATION") continue;
                    var items = step.Refs(idt);
                    if (items.Count < 1) continue;
                    int placement = items[0];
                    if (step.TypeOf(placement) != "AXIS2_PLACEMENT_3D") continue;
                    var refs = step.Refs(placement);
                    if (refs.Count < 1) continue;
                    var loc = ReadTriple(step, refs[0]);
                    if (loc == null) continue;
                    double[] axis = refs.Count > 1 ? ReadTriple(step, refs[1]) : null;
                    double[] rd = refs.Count > 2 ? ReadTriple(step, refs[2]) : null;
                    // The same unit that StepRewriter applies to the
                    // occurrence tables: the one of the parent
                    // representation.
                    double mm = step.PlacementUnitMm(rel, placement);
                    return new FileChild
                    {
                        Occ = c,
                        IdtId = idt,
                        MovingId = placement,
                        RelId = rel,
                        UnitMm = mm,
                        Loc = new[] { loc[0] * mm, loc[1] * mm, loc[2] * mm },
                        Rot = RotFromAxes(axis, rd),
                    };
                }
            }
            return null;
        }

        // ── Layout ↔ file assignment ────────────────────────────────────────

        private sealed class Assignment
        {
            public Dictionary<FileChild, string> KeyByChild
                = new Dictionary<FileChild, string>();
            public double Cost;
            public double MaxPosErr;    // metres
            public double MaxRotErr;    // radians
        }

        private static Assignment AssignChildren(
            FlexInstanceLayout layout, List<FileChild> fileChildren)
        {
            const double scale = MmPerM;
            if (layout.Children.Count != fileChildren.Count) return null;
            var pairs = new List<KeyValuePair<double,
                KeyValuePair<FlexChildPose, FileChild>>>();
            foreach (var pose in layout.Children)
                foreach (var fc in fileChildren)
                {
                    if (!NameMatches(fc.Occ.ProductName, pose.ProductName)) continue;
                    double d = 0;
                    for (int i = 0; i < 3; i++)
                    {
                        double diff = fc.Loc[i] - pose.LocalM[i, 3] * scale;
                        d += diff * diff;
                    }
                    pairs.Add(new KeyValuePair<double,
                        KeyValuePair<FlexChildPose, FileChild>>(Math.Sqrt(d),
                        new KeyValuePair<FlexChildPose, FileChild>(pose, fc)));
                }
            pairs.Sort((a, b) => a.Key.CompareTo(b.Key));

            var a2 = new Assignment();
            var posesTaken = new HashSet<FlexChildPose>();
            foreach (var p in pairs)
            {
                if (posesTaken.Contains(p.Value.Key)
                    || a2.KeyByChild.ContainsKey(p.Value.Value)) continue;
                posesTaken.Add(p.Value.Key);
                a2.KeyByChild[p.Value.Value] = p.Value.Key.Key;
                double posErrM = p.Key / scale;
                double rotErr = RotAngle(p.Value.Value.Rot, p.Value.Key.LocalM);
                a2.Cost += posErrM + 0.05 * rotErr;
                if (posErrM > a2.MaxPosErr) a2.MaxPosErr = posErrM;
                if (rotErr > a2.MaxRotErr) a2.MaxRotErr = rotErr;
            }
            return a2.KeyByChild.Count == fileChildren.Count ? a2 : null;
        }

        private static bool SameLayout(FlexInstanceLayout a, FlexInstanceLayout b)
        {
            if (a.Children.Count != b.Children.Count) return false;
            foreach (var pa in a.Children)
            {
                var pb = b.Children.FirstOrDefault(p => p.Key == pa.Key);
                if (pb == null) return false;
                for (int i = 0; i < 3; i++)
                    if (Math.Abs(pa.LocalM[i, 3] - pb.LocalM[i, 3]) > 1e-5) return false;
                if (RotAngle3(pa.LocalM, pb.LocalM) > 1e-4) return false;
            }
            return true;
        }

        // ── Placement retargeting ───────────────────────────────────────────

        /// <summary>Rewrites every child placement (original when map is null,
        /// otherwise the cloned copies) to the given layout. Appends a fresh
        /// CARTESIAN_POINT + DIRECTIONs + AXIS2_PLACEMENT_3D per child and
        /// swaps the IDT's moving reference: original placements can be
        /// shared entities and are never edited.</summary>
        private static void RetargetAll(
            Part21 step, List<FileChild> fileChildren, FlexInstanceLayout layout,
            Dictionary<FileChild, string> keyOf,
            Dictionary<int, int> map, FlexFixOutcome outcome,
            List<StepRewriter.OccurrenceRef> clonedKids = null)
        {
            for (int i = 0; i < fileChildren.Count; i++)
            {
                var fc = fileChildren[i];
                string key;
                if (!keyOf.TryGetValue(fc, out key)) continue;
                var pose = layout.Children.FirstOrDefault(p => p.Key == key);
                if (pose == null) continue;

                int idt = map == null ? fc.IdtId : map[fc.IdtId];
                int moving = map == null ? fc.MovingId : map[fc.MovingId];
                int rel = fc.RelId;
                if (map != null && !map.TryGetValue(fc.RelId, out rel)) rel = fc.RelId;
                int rep = step.RepresentationListing(rel, moving);

                int placement = AppendPlacement(step, pose.LocalM, fc.UnitMm);
                SwapIdtReference(step, idt, moving, placement);
                if (rep != 0) ListInRepresentation(step, rep, moving, placement);
                outcome.PlacementsRetargeted++;

                var newLoc = new[]
                {
                    pose.LocalM[0, 3] * MmPerM,
                    pose.LocalM[1, 3] * MmPerM,
                    pose.LocalM[2, 3] * MmPerM,
                };
                if (map == null)
                {
                    fc.Occ.Translation = newLoc;
                    fc.Loc = newLoc;
                    fc.Rot = Rot3(pose.LocalM);
                }
                else if (clonedKids != null && i < clonedKids.Count)
                {
                    clonedKids[i].Translation = newLoc;
                }
            }
        }

        /// <summary>A new placement in the unit of the representation that
        /// lists it: unitMm is millimetres per unit of that
        /// representation.</summary>
        private static int AppendPlacement(Part21 step, double[,] localM, double unitMm)
        {
            double scale = MmPerM / (unitMm > 0 ? unitMm : 1.0);
            int pt = step.NextId();
            step.Append(string.Format(CultureInfo.InvariantCulture,
                "#{0}=CARTESIAN_POINT('',({1},{2},{3}));", pt,
                Part21.Num(localM[0, 3] * scale),
                Part21.Num(localM[1, 3] * scale),
                Part21.Num(localM[2, 3] * scale)));
            int ax = step.NextId();
            step.Append(string.Format(CultureInfo.InvariantCulture,
                "#{0}=DIRECTION('',({1},{2},{3}));", ax,
                Part21.Num(localM[0, 2]), Part21.Num(localM[1, 2]),
                Part21.Num(localM[2, 2])));
            int rd = step.NextId();
            step.Append(string.Format(CultureInfo.InvariantCulture,
                "#{0}=DIRECTION('',({1},{2},{3}));", rd,
                Part21.Num(localM[0, 0]), Part21.Num(localM[1, 0]),
                Part21.Num(localM[2, 0])));
            int a2p = step.NextId();
            step.Append(string.Format(CultureInfo.InvariantCulture,
                "#{0}=AXIS2_PLACEMENT_3D('',#{1},#{2},#{3});", a2p, pt, ax, rd));
            return a2p;
        }

        private static void SwapIdtReference(Part21 step, int idt, int oldRef, int newRef)
        {
            string args = step.ArgsOf(idt) ?? "";
            // A whole reference only: never "#12" inside "#123", never a
            // '#' and digits inside a quoted name.
            string swapped = Part21.ReplaceRefs(args,
                id => id == oldRef ? "#" + newRef : null);
            step.Replace(idt, "#" + idt + "=ITEM_DEFINED_TRANSFORMATION" + swapped + ";");
        }

        /// <summary>
        /// Lists the new placement in the representation that listed the old
        /// one. A reader finds the unit of a placement through this list
        /// (Part21.PlacementUnitMm, and the copy in the rig matcher). A
        /// placement that no representation lists falls back to
        /// millimetres, and in a metre file the reader then takes a wrong
        /// value as correct. The old placement stays in the list while
        /// another transformation still uses it.
        /// </summary>
        private static void ListInRepresentation(Part21 step, int rep, int oldRef, int newRef)
        {
            bool stillUsed = step.ByType("ITEM_DEFINED_TRANSFORMATION")
                .Any(t => step.Refs(t).Contains(oldRef));
            string type = step.TypeOf(rep) ?? "";
            string args = step.ArgsOf(rep) ?? "";
            string listed = Part21.ReplaceRefs(args, id => id != oldRef ? null
                : stillUsed ? "#" + oldRef + ",#" + newRef : "#" + newRef);
            step.Replace(rep, type.StartsWith("COMPLEX:", StringComparison.Ordinal)
                ? "#" + rep + "=" + listed + ";"
                : "#" + rep + "=" + type + listed + ";");
        }

        // ── Small math / parsing helpers ────────────────────────────────────

        private static bool NameMatches(string productName, string docName)
        {
            if (string.IsNullOrEmpty(productName) || string.IsNullOrEmpty(docName))
                return false;
            if (string.Equals(productName, docName, StringComparison.OrdinalIgnoreCase))
                return true;
            return productName.StartsWith(docName + "_", StringComparison.OrdinalIgnoreCase);
        }

        private static double[] ReadTriple(Part21 step, int id)
        {
            var args = step.ArgsOf(id) ?? "";
            var nums = Regex.Matches(args, @"-?\d+\.?\d*(?:[eE][-+]?\d+)?")
                .Cast<Match>()
                .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))
                .ToArray();
            return nums.Length >= 3 ? new[] { nums[0], nums[1], nums[2] } : null;
        }

        private static double[,] RotFromAxes(double[] z, double[] x)
        {
            if (z == null) z = new[] { 0.0, 0.0, 1.0 };
            if (x == null) x = new[] { 1.0, 0.0, 0.0 };
            z = Unit(z);
            // Orthogonalize x against z the way STEP readers do.
            double dot = x[0] * z[0] + x[1] * z[1] + x[2] * z[2];
            x = Unit(new[] { x[0] - dot * z[0], x[1] - dot * z[1], x[2] - dot * z[2] });
            var y = new[]
            {
                z[1] * x[2] - z[2] * x[1],
                z[2] * x[0] - z[0] * x[2],
                z[0] * x[1] - z[1] * x[0],
            };
            return new[,]
            {
                { x[0], y[0], z[0] },
                { x[1], y[1], z[1] },
                { x[2], y[2], z[2] },
            };
        }

        private static double[] Unit(double[] v)
        {
            double n = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            if (n < 1e-12) return new[] { 0.0, 0.0, 1.0 };
            return new[] { v[0] / n, v[1] / n, v[2] / n };
        }

        private static double[,] Rot3(double[,] m4)
            => new[,]
            {
                { m4[0, 0], m4[0, 1], m4[0, 2] },
                { m4[1, 0], m4[1, 1], m4[1, 2] },
                { m4[2, 0], m4[2, 1], m4[2, 2] },
            };

        private static double RotAngle(double[,] rot3, double[,] m4)
        {
            double trace = 0;
            for (int i = 0; i < 3; i++)
                for (int k = 0; k < 3; k++)
                    trace += rot3[k, i] * m4[k, i];
            return Math.Acos(Math.Max(-1.0, Math.Min(1.0, (trace - 1.0) / 2.0)));
        }

        private static double RotAngle3(double[,] a4, double[,] b4)
            => RotAngle(Rot3(a4), b4);

        private static void Fail(FlexFixOutcome outcome,
            IEnumerable<FlexInstanceLayout> instances, Action<string> log,
            string message)
        {
            foreach (var i in instances) outcome.FailedPaths.Add(i.Path);
            outcome.Notes.Add(message);
            log?.Invoke("    flexible fix: " + message);
        }

        private static string Describe(List<FlexInstanceLayout> cls)
            => string.Join(", ", cls.Select(i => i.Path).ToArray());
    }
}
