using System;
using System.Collections.Generic;
using System.Text;
using Peak.Cadder.Core.Model;

namespace Peak.Cadder.Core
{
    /// <summary>
    /// One inter-group connection: the ordered group pair plus every surviving
    /// mate whose entities span the two groups. JointClassifier consumes these
    /// instead of rebuilding the pair index from the raw graph.
    /// </summary>
    public sealed class GroupEdge
    {
        public string GroupA;             // the group with the lower list index
        public string GroupB;
        public List<GraphMate> Mates = new List<GraphMate>();
    }

    public sealed class RigidGroupingResult
    {
        public List<RigidGroup> Groups = new List<RigidGroup>();

        /// <summary>Component id -> rigid group id, active components only.
        /// Suppressed components appear in no group.</summary>
        public Dictionary<string, string> ComponentGroup = new Dictionary<string, string>();

        public List<GroupEdge> Edges = new List<GroupEdge>();

        /// <summary>Top-level components SolidWorks calls UNDER-defined that
        /// ended up in the grounded group. SolidWorks says they can move, so
        /// each is a freedom the rig has lost.</summary>
        public List<string> MergedAwayDofs = new List<string>();

        /// <summary>Components welded to the ground because SolidWorks says
        /// they cannot move (GraphComponent.StatusFree).</summary>
        public List<string> StatusWelds = new List<string>();
    }

    /// <summary>
    /// Union-find merge of components that cannot move relative to each
    /// other: what SolidWorks reports immobile, and what the combined mate set
    /// between a pair leaves with zero relative DOF. Kinematics only: nothing
    /// here looks at a name, a file, a description or a part library.
    /// </summary>
    public static class RigidGrouper
    {
        /// <summary>The phantom component standing in for the assembly
        /// itself when no component is fixed but a mate references
        /// assembly-owned geometry (a 3D sketch, a datum plane). It grounds
        /// a virtual, component-less rigid group; the id never reaches the
        /// manifest.</summary>
        public const string AssemblyGroundId = "__assembly__";

        /// <summary>
        /// Groups the components. <paramref name="solverRigidPairs"/> is the
        /// SolidWorks solver's own verdict, arriving on a second pass: each
        /// entry is a component-id pair the DOF probe read as having no
        /// relative freedom once the parent side was pinned. The mates alone
        /// cannot always see that: three bodies can pin each other in a way
        /// no single pair reveals, so when the solver says two things are one
        /// body, they are.
        /// </summary>
        public static RigidGroupingResult Group(
            MateGraph graph, IEnumerable<string[]> solverRigidPairs = null)
        {
            var comps = new List<GraphComponent>();
            var indexById = new Dictionary<string, int>();
            foreach (var c in graph.Components)
            {
                if (c.Suppressed) continue;
                indexById[c.Id] = comps.Count;
                comps.Add(c);
            }

            // A mate entity with a null ComponentId sits on the assembly
            // itself, which is rigid with the fixed components. It is treated
            // as an entity on the first fixed component. With no fixed
            // component at all, the assembly frame itself becomes the ground:
            // a phantom fixed component backs a virtual, component-less
            // grounded group (live corpus 16/17 pt4 + path1, 2026-08-23, a
            // part mated to an assembly 3D sketch exported as a groundless
            // island and the sampled path was thrown away).
            int assemblyProxy = -1;
            for (int i = 0; i < comps.Count; i++)
            {
                if (Grounds(comps[i], false)) { assemblyProxy = i; break; }
            }
            // Last resort: an assembly whose only fixed component IS a
            // flexible subassembly node still needs a ground, or every group
            // it holds becomes an island. Preferring a real body keeps the
            // ClampRig ram case (a fixed flexible node beside a fixed machine
            // body) on the rule above.
            bool flexibleGrounds = assemblyProxy < 0;
            if (flexibleGrounds)
                for (int i = 0; i < comps.Count; i++)
                    if (comps[i].IsFixed) { assemblyProxy = i; break; }
            if (assemblyProxy < 0 && NeedsAssemblyGround(graph, indexById))
            {
                var ground = new GraphComponent();
                ground.Id = AssemblyGroundId;
                ground.Name = "assembly";
                ground.Transform = MathOps.Identity4();
                ground.IsFixed = true;
                assemblyProxy = comps.Count;
                indexById[ground.Id] = comps.Count;
                comps.Add(ground);
            }

            var pairMates = new Dictionary<long, List<GraphMate>>();
            foreach (var mate in graph.Mates)
            {
                if (mate.Suppressed) continue;
                int a, b;
                if (!TrySpan(mate, indexById, assemblyProxy, out a, out b)) continue;
                long key = PairKey(a, b);
                List<GraphMate> list;
                if (!pairMates.TryGetValue(key, out list))
                {
                    list = new List<GraphMate>();
                    pairMates[key] = list;
                }
                list.Add(mate);
            }

            var parent = new int[comps.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;

            // A child fixed INSIDE a flexible subassembly is rigid to the
            // subassembly's own frame, not to the world: it merges with the
            // subassembly node (live corpus 07, 2026-08-22, the hinge's
            // fixed base floated as its own group, and every mate grabbing
            // the sub's reference geometry attached one body away from the
            // part it actually pins).
            //
            for (int i = 0; i < comps.Count; i++)
            {
                var c = comps[i];
                if (!c.FixedInSubassembly || c.ParentId == null) continue;
                int p;
                if (indexById.TryGetValue(c.ParentId, out p)) Union(parent, i, p);
            }

            // Every fixed component belongs to the SAME ground: two things
            // fixed to the assembly have no freedom between them whether or
            // not a mate happens to span them. Without this the ground
            // arrives as many separate grounded groups, and any joint landing
            // on a later one makes a grounded group somebody's child, which
            // no bone hierarchy can root (live ClampRig, 2026-08-24:
            // 18 grounded groups, and Blender refused the manifest at j005).
            if (assemblyProxy >= 0)
                for (int i = 0; i < comps.Count; i++)
                    if (Grounds(comps[i], flexibleGrounds)) Union(parent, i, assemblyProxy);

            // What SolidWorks itself says cannot move joins the ground before
            // the mates are asked anything. The mate analysis sees one pair
            // at a time and misses what it has no model for (a width between
            // two plates, a cone seated in a countersink), and every miss
            // was a part that moved in Blender and not in SolidWorks (live
            // CutterRig, 2026-09-21: 24 countersunk bolts, four plates and a
            // frame). The solver has already solved the whole assembly.
            //
            // Only the status read with the limit mates OUT: with them in, a
            // part behind a limit reads fully defined while it moves (live
            // ClampRig, 2026-08-24: welding on that reading took the lead
            // screw and cutting head out of the rig). Only top-level
            // components: inside a flexible subassembly the status does not
            // follow the motion. And never a component with no active mate:
            // a pattern or mirror instance follows its seed, mated or not.
            var statusWelded = new List<string>();
            if (assemblyProxy >= 0)
            {
                var mated = MatedComponents(graph);
                for (int i = 0; i < comps.Count; i++)
                {
                    var c = comps[i];
                    if (i == assemblyProxy || c.ParentId != null) continue;
                    if (c.StatusFree != SwFullyConstrained) continue;
                    if (!mated.Contains(c.Id)) continue;
                    if (Find(parent, i) == Find(parent, assemblyProxy)) continue;
                    Union(parent, i, assemblyProxy);
                    statusWelded.Add(c.Path ?? c.Id);
                }
            }

            // The solver's own verdict, arriving on a second pass: pairs the
            // DOF probe read as having no relative freedom and that survived
            // SolverWelds, which separates a weld from a follower by what the
            // probe said about the child's OTHER partners.
            if (solverRigidPairs != null)
            {
                foreach (var pair in solverRigidPairs)
                {
                    int a, b;
                    if (pair == null || pair.Length != 2) continue;
                    if (!indexById.TryGetValue(pair[0], out a)) continue;
                    if (!indexById.TryGetValue(pair[1], out b)) continue;
                    Union(parent, a, b);
                }
            }

            // Pair keys are visited in a deterministic order so that repeated
            // runs on the same graph produce the same unions.
            var keys = new List<long>(pairMates.Keys);
            keys.Sort();

            foreach (long key in keys)
            {
                int a = (int)(key >> 32);
                int b = (int)(key & 0xFFFFFFFF);
                var mates = pairMates[key];

                if (PairIsRigid(mates, comps[a], comps[b])) Union(parent, a, b);
            }

            // Rigidity can hide at the GROUP level: one body's mates may land
            // on different members of another group, each pair non-rigid
            // alone, the union welded solid. Live corpus 07 (2026-08-22): the
            // baseplate's coincidents grabbed the hinge sub's fixed base
            // while its distance mate grabbed the sub's own reference plane,
            // the pair-level pass left them split and the classifier hit a
            // zero-DOF edge it refuses to output. Aggregate every pair's
            // mates by current group and merge to a fixpoint.
            bool changed = true;
            while (changed)
            {
                changed = false;
                var byRootPair = new Dictionary<long, List<GraphMate>>();
                foreach (long key in keys)
                {
                    int ra = Find(parent, (int)(key >> 32));
                    int rb = Find(parent, (int)(key & 0xFFFFFFFF));
                    if (ra == rb) continue;
                    long rootKey = PairKey(Math.Min(ra, rb), Math.Max(ra, rb));
                    List<GraphMate> list;
                    if (!byRootPair.TryGetValue(rootKey, out list))
                    {
                        list = new List<GraphMate>();
                        byRootPair[rootKey] = list;
                    }
                    list.AddRange(pairMates[key]);
                }
                var rootKeys = new List<long>(byRootPair.Keys);
                rootKeys.Sort();
                foreach (long rootKey in rootKeys)
                {
                    int ra = Find(parent, (int)(rootKey >> 32));
                    int rb = Find(parent, (int)(rootKey & 0xFFFFFFFF));
                    if (ra == rb) continue;    // united earlier this sweep
                    if (!MotionResolver.Resolve(byRootPair[rootKey]).IsRigid) continue;
                    Union(parent, ra, rb);
                    changed = true;
                }
            }

            var result = BuildResult(comps, parent, pairMates, keys, flexibleGrounds);
            result.MergedAwayDofs = UnderDefinedButMerged(comps, parent, result);
            result.StatusWelds = statusWelded;
            return result;
        }

        /// <summary>
        /// Whether this component's fixed flag may ground the rig.
        ///
        /// A FLEXIBLE subassembly is not a rigid body: SolidWorks dissolves
        /// the node and solves its children against the top assembly, which
        /// is exactly why the walker descends into it. Its Fix/Float flag
        /// describes a body the solver no longer has, so it cannot ground
        /// anything: whether the sub's contents are pinned is decided by
        /// their own mates, like any other component.
        ///
        /// Live ClampRig (2026-08-24): both hydraulic rams
        /// (RamSub-1 and -2, flexible) reported IsFixed while both
        /// clamps (ClampSub, rigid) did not, and the ram barrels went
        /// into ground through the node, taking the bore pivot with them, so
        /// the rods extended in Blender without the ram swinging. The only
        /// mates from either barrel to a grounded body are the bore
        /// concentrics (plus one width), which is a revolute; nothing in the
        /// mate table welds them.
        /// </summary>
        private static bool Grounds(GraphComponent c, bool flexibleGrounds)
        {
            return c.IsFixed && (flexibleGrounds || c.Solving != "flexible");
        }

        // ── What the constrained status is good for ─────────────────────────

        /// <summary>swConstrainedStatus_e values.</summary>
        private const int SwUnderConstrained = 2;
        private const int SwFullyConstrained = 3;

        /// <summary>
        /// Top-level components SolidWorks calls under-defined that sit in
        /// the grounded group. Read from StatusFree when it was read, else
        /// from the status with the limits in, which can only be wrong in
        /// the other direction (a limit makes a moving part read fully
        /// defined, never a still one under-defined). A component in a
        /// moving group is not reported: a bolt on a swinging clamp is
        /// under-defined because the clamp swings, and it belongs with the
        /// clamp.
        /// </summary>
        private static List<string> UnderDefinedButMerged(
            List<GraphComponent> comps, int[] parent, RigidGroupingResult result)
        {
            var lost = new List<string>();
            var grounded = new HashSet<string>();
            foreach (var g in result.Groups) if (g.Grounded) grounded.Add(g.Id);
            for (int i = 0; i < comps.Count; i++)
            {
                var c = comps[i];
                if (c.ParentId != null || c.IsFixed || c.Id == AssemblyGroundId) continue;
                int status = c.StatusFree != 0 ? c.StatusFree : c.ConstrainedStatus;
                if (status != SwUnderConstrained) continue;
                string group;
                if (!result.ComponentGroup.TryGetValue(c.Id, out group)) continue;
                if (grounded.Contains(group)) lost.Add(c.Path ?? c.Id);
            }
            return lost;
        }

        /// <summary>Components some active mate touches.</summary>
        private static HashSet<string> MatedComponents(MateGraph graph)
        {
            var mated = new HashSet<string>();
            foreach (var m in graph.Mates)
            {
                if (m.Suppressed) continue;
                foreach (var e in m.Entities)
                    if (e != null && e.ComponentId != null) mated.Add(e.ComponentId);
            }
            return mated;
        }

        /// <summary>True when some surviving mate reaches assembly-owned
        /// geometry (a datum plane, a 3D sketch) as well as a component: that
        /// geometry is rigid with the assembly and needs a body to live on.</summary>
        private static bool NeedsAssemblyGround(
            MateGraph graph, Dictionary<string, int> indexById)
        {
            foreach (var mate in graph.Mates)
            {
                if (mate.Suppressed) continue;
                bool onAssembly = false, onComponent = false;
                foreach (var e in mate.Entities)
                {
                    if (e.ComponentId == null) onAssembly = true;
                    else if (indexById.ContainsKey(e.ComponentId)) onComponent = true;
                }
                if (onAssembly && onComponent) return true;
            }
            return false;
        }

        // ── Zero-DOF detection ──────────────────────────────────────────────

        /// <summary>
        /// True when the combined mate set between exactly this component pair
        /// leaves no relative freedom. MotionResolver is the single source of
        /// that truth: the classifier consumes the same resolver, so a pair
        /// this method keeps separate can never classify as fixed. (The
        /// predecessor mirrored a few zero-DOF patterns by hand and missed
        /// concentric + face + side-face coincidents: the live fully-defined
        /// hinge stayed unmerged and rotated freely in Blender.)
        /// </summary>
        private static bool PairIsRigid(List<GraphMate> mates, GraphComponent a, GraphComponent b)
        {
            // Two fixed components cannot move relative to each other no
            // matter what the mates between them say.
            if (Grounds(a, false) && Grounds(b, false)) return true;

            return MotionResolver.Resolve(mates).IsRigid;
        }

        // ── Result assembly ─────────────────────────────────────────────────

        private static RigidGroupingResult BuildResult(
            List<GraphComponent> comps, int[] parent,
            Dictionary<long, List<GraphMate>> pairMates, List<long> keys,
            bool flexibleGrounds)
        {
            var membersByRoot = new Dictionary<int, List<int>>();
            var rootOrder = new List<int>();
            for (int i = 0; i < comps.Count; i++)
            {
                int root = Find(parent, i);
                List<int> members;
                if (!membersByRoot.TryGetValue(root, out members))
                {
                    members = new List<int>();
                    membersByRoot[root] = members;
                    rootOrder.Add(root);
                }
                members.Add(i);
            }

            // g000 is the grounded group that contains the earliest fixed
            // component. Other grounded islands keep Grounded=true under later
            // ids; the caller is responsible for the DISCONNECTED_ISLAND
            // warning.
            int groundedFirst = -1;
            foreach (int root in rootOrder)
            {
                bool grounded = false;
                foreach (int i in membersByRoot[root])
                    if (Grounds(comps[i], flexibleGrounds)) { grounded = true; break; }
                if (grounded) { groundedFirst = root; break; }
            }
            var ordered = new List<int>();
            if (groundedFirst >= 0) ordered.Add(groundedFirst);
            foreach (int root in rootOrder)
                if (root != groundedFirst) ordered.Add(root);

            var result = new RigidGroupingResult();
            var groupIndexByRoot = new Dictionary<int, int>();
            for (int g = 0; g < ordered.Count; g++)
            {
                int root = ordered[g];
                var members = membersByRoot[root];
                groupIndexByRoot[root] = g;

                var group = new RigidGroup();
                group.Id = "g" + g.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
                bool grounded = false;
                int largest = members[0];
                double largestDiag = -1.0;
                double[] boxMin = null, boxMax = null;
                foreach (int i in members)
                {
                    var c = comps[i];
                    if (Grounds(c, flexibleGrounds)) grounded = true;
                    // The phantom assembly ground stays out of the member
                    // list: the group is virtual (schema allows empty
                    // components: same as carriers) and the id must never
                    // reach the manifest.
                    if (c.Id == AssemblyGroundId) continue;
                    group.Components.Add(c.Id);
                    result.ComponentGroup[c.Id] = group.Id;
                    if (c.BboxMin != null && c.BboxMax != null)
                    {
                        double diag = Math.Sqrt(MathOps.Distance2(c.BboxMax, c.BboxMin));
                        if (diag > largestDiag) { largestDiag = diag; largest = i; }
                        if (boxMin == null)
                        {
                            boxMin = (double[])c.BboxMin.Clone();
                            boxMax = (double[])c.BboxMax.Clone();
                        }
                        else
                        {
                            for (int k = 0; k < 3; k++)
                            {
                                if (c.BboxMin[k] < boxMin[k]) boxMin[k] = c.BboxMin[k];
                                if (c.BboxMax[k] > boxMax[k]) boxMax[k] = c.BboxMax[k];
                            }
                        }
                    }
                }
                group.Grounded = grounded;
                group.Name = Sanitise(comps[largest].Name, group.Id);
                group.Frame = comps[members[0]].Transform;
                group.BboxDiag = boxMin == null
                    ? (double?)null
                    : Math.Sqrt(MathOps.Distance2(boxMax, boxMin));
                result.Groups.Add(group);
            }

            var edgeByKey = new Dictionary<long, GroupEdge>();
            var edgeOrder = new List<long>();
            foreach (long key in keys)
            {
                int a = (int)(key >> 32);
                int b = (int)(key & 0xFFFFFFFF);
                int ga = groupIndexByRoot[Find(parent, a)];
                int gb = groupIndexByRoot[Find(parent, b)];
                if (ga == gb) continue;    // consumed by the merge
                long groupKey = PairKey(Math.Min(ga, gb), Math.Max(ga, gb));
                GroupEdge edge;
                if (!edgeByKey.TryGetValue(groupKey, out edge))
                {
                    edge = new GroupEdge();
                    edge.GroupA = result.Groups[Math.Min(ga, gb)].Id;
                    edge.GroupB = result.Groups[Math.Max(ga, gb)].Id;
                    edgeByKey[groupKey] = edge;
                    edgeOrder.Add(groupKey);
                }
                edge.Mates.AddRange(pairMates[key]);
            }
            edgeOrder.Sort();
            foreach (long groupKey in edgeOrder)
                result.Edges.Add(edgeByKey[groupKey]);

            return result;
        }

        /// <summary>Bone-name slug: lowercase letters, digits and underscores
        /// only. An all-symbol name falls back to the group id.</summary>
        private static string Sanitise(string name, string fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            var sb = new StringBuilder(name.Length);
            bool lastUnderscore = false;
            foreach (char ch in name)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                    lastUnderscore = false;
                }
                else if (!lastUnderscore && sb.Length > 0)
                {
                    sb.Append('_');
                    lastUnderscore = true;
                }
            }
            while (sb.Length > 0 && sb[sb.Length - 1] == '_') sb.Length--;
            return sb.Length == 0 ? fallback : sb.ToString();
        }

        // ── Pair index plumbing ─────────────────────────────────────────────

        /// <summary>
        /// Resolves which two components a mate spans. Mates touching one
        /// component only, or three or more, take no part in grouping or
        /// classification. MateReader does not produce them for the mate
        /// types this pipeline handles.
        /// </summary>
        private static bool TrySpan(
            GraphMate mate, Dictionary<string, int> indexById, int assemblyProxy,
            out int a, out int b)
        {
            a = -1; b = -1;
            foreach (var e in mate.Entities)
            {
                int idx;
                if (e.ComponentId == null)
                {
                    if (assemblyProxy < 0) continue;
                    idx = assemblyProxy;
                }
                else if (!indexById.TryGetValue(e.ComponentId, out idx))
                {
                    return false;   // entity on a suppressed component: mate is inert
                }
                if (a < 0) { a = idx; continue; }
                if (idx == a) continue;
                if (b < 0) { b = idx; continue; }
                if (idx != b) return false;
            }
            if (a < 0 || b < 0) return false;
            if (a > b) { int t = a; a = b; b = t; }
            return true;
        }

        private static long PairKey(int a, int b) => ((long)a << 32) | (uint)b;

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a);
            int rb = Find(parent, b);
            if (ra == rb) return;
            // The smaller root wins so group membership order is stable.
            if (ra < rb) parent[rb] = ra; else parent[ra] = rb;
        }
    }

    /// <summary>
    /// Mate-set predicates shared by RigidGrouper and JointClassifier. Keyed
    /// on TypeName substrings because the swMateType_e numeric values have
    /// shifted between SolidWorks releases.
    /// </summary>
    internal static class MateFacts
    {
        /// <summary>Directions closer than this (norm of the unit cross
        /// product) count as parallel. Matches the loop-planarity tolerance.</summary>
        public const double ParallelTol = 1e-6;

        /// <summary>Axes offset by less than this (metres) count as collinear.</summary>
        public const double CollinearTol = 1e-6;

        public static bool Is(GraphMate m, string kind)
        {
            return m.TypeName != null
                && m.TypeName.IndexOf(kind, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>The LOCK mate exactly. Is(m, "LOCK") also matches
        /// swMateLOCKTOSKETCH: a sketch-driven positioner, nothing like a
        /// rigid weld, so every lock test must come through here.</summary>
        public static bool IsLock(GraphMate m)
        {
            return Is(m, "LOCK") && !Is(m, "LOCKTOSKETCH");
        }

        /// <summary>A limit mate constrains a range, not a value. Only
        /// distance and angle mates can be limit mates; a hinge carries its
        /// range itself and stays a constraint.</summary>
        public static bool IsLimitMate(GraphMate m)
        {
            if (m.MinimumVariation == m.MaximumVariation) return false;
            return Is(m, "DISTANCE") || Is(m, "ANGLE");
        }

        /// <summary>
        /// A mate that leaves motion by its own definition, whatever the
        /// solver's DOF accounting makes of it: a limit range, a path, a slot
        /// that is not pinned along its length, a tangent contact, and the
        /// mechanical mates that TRANSMIT motion rather than remove it.
        ///
        /// This reads mate TYPES and ranges (kinematics), never names.
        /// </summary>
        /// <summary>A mate that ties this body's motion to another body's:
        /// gears, racks, couplers, screws, cams, universal joints.</summary>
        public static bool IsCoupling(GraphMate m)
        {
            return Is(m, "SCREW")
                || Is(m, "GEAR")
                || Is(m, "RACKPINION")
                || Is(m, "LINEARCOUPLER")
                || Is(m, "CAMFOLLOWER")
                || Is(m, "UNIVERSALJOINT");
        }

        public static bool PermitsMotion(GraphMate m)
        {
            if (IsLimitMate(m)) return true;
            if (Is(m, "PATH")) return true;
            // swSlotMateConstraintOptions_e: 0 free, 1 centered, 2 distance,
            // 3 percent. Only FREE leaves the pin sliding: centered pins it
            // at the slot's midpoint just as firmly as a distance does (live
            // ClampRigPart, 2026-08-24, which reported constraint=1 and is
            // fully defined in SolidWorks). Unread (−1) is treated as free.
            if (Is(m, "SLOT"))
                return m.SlotConstraint == 0 || m.SlotConstraint < 0;
            return Is(m, "SCREW")
                || Is(m, "GEAR")
                || Is(m, "RACKPINION")
                || Is(m, "LINEARCOUPLER")
                || Is(m, "CAMFOLLOWER")
                || Is(m, "UNIVERSALJOINT")
                || Is(m, "HINGE")
                || Is(m, "SLIDER")
                || Is(m, "TANGENT")
                || Is(m, "MAGNETIC");
        }

        /// <summary>Axis of a concentric-like mate: the first entity carrying
        /// a direction that is not a sphere. Both sides are parallel once the
        /// assembly is solved, so the first is as good as the second.</summary>
        public static bool TryGetAxis(GraphMate m, out double[] direction, out double[] point)
        {
            foreach (var e in m.Entities)
            {
                if (e.Direction == null) continue;
                if (e.EntityTypeName == "sphere") continue;
                direction = MathOps.Normalized(e.Direction);
                point = e.Point ?? new double[3];
                return true;
            }
            direction = null;
            point = null;
            return false;
        }

        public static List<double[][]> Planes(GraphMate m)
        {
            var result = new List<double[][]>();
            foreach (var e in m.Entities)
            {
                if (e.EntityTypeName != "plane" || e.Direction == null) continue;
                result.Add(new[] { MathOps.Normalized(e.Direction), e.Point ?? new double[3] });
            }
            return result;
        }

        public static bool IsParallel(double[] a, double[] b)
        {
            var na = MathOps.Normalized(a);
            var nb = MathOps.Normalized(b);
            return MathOps.Norm(MathOps.Cross(na, nb)) < ParallelTol;
        }

        public static bool IsPerpendicular(double[] a, double[] b)
        {
            var na = MathOps.Normalized(a);
            var nb = MathOps.Normalized(b);
            return Math.Abs(MathOps.Dot(na, nb)) < ParallelTol;
        }

        public static double DistancePointToLine(double[] p, double[] dir, double[] pointOnLine)
        {
            var foot = MathOps.ClosestPointOnLineToPoint(p, dir, pointOnLine);
            return Math.Sqrt(MathOps.Distance2(p, foot));
        }
    }
}
