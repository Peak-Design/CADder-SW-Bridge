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

        /// <summary>The component ids behind StatusWelds, in the same order.
        /// </summary>
        public List<string> StatusWeldIds = new List<string>();

        /// <summary>Parts that read fully defined but are held by a cam or
        /// path mate, and so were not welded on their status.</summary>
        public List<string> PoseHeldSkips = new List<string>();

        /// <summary>Children of flexible subassemblies welded to their
        /// subassembly's frame because SolidWorks says they cannot move in
        /// the subassembly's own document (GraphComponent.SubStatusFree).
        /// </summary>
        public List<string> SubStatusWelds = new List<string>();

        /// <summary>Mates that touch three or more components and did not
        /// come down to two rigid groups, so the rig cannot use them.
        /// </summary>
        public List<string> UnreadMultiMates = new List<string>();
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
        /// <param name="statusVetoed">Components the status pass must not
        /// weld: the DOF probe found them free against the ground although
        /// SolidWorks' status calls them fully defined.</param>
        /// <param name="statusWelds">False skips the top-level status pass,
        /// for the grouping the mates alone give.</param>
        public static RigidGroupingResult Group(
            MateGraph graph, IEnumerable<string[]> solverRigidPairs = null,
            ISet<string> statusVetoed = null, bool statusWelds = true,
            bool subStatusWelds = true)
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
            var multiMates = new List<MultiMate>();
            foreach (var mate in graph.Mates)
            {
                if (mate.Suppressed) continue;
                int a, b;
                if (!TrySpan(mate, indexById, assemblyProxy, out a, out b))
                {
                    var multi = MultiSpan(mate, indexById, assemblyProxy);
                    if (multi != null) multiMates.Add(multi);
                    continue;
                }
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

            // A child SolidWorks calls fully defined in its subassembly's own
            // document, with the limits out, is rigid to the subassembly's
            // frame in the same way: mates in a parent can only add to what
            // holds it (live CutterRig, 2026-09-22: the cutting head's nuts
            // and washers read fully defined in its own document and slid in
            // Blender). A child no active mate touches follows its seed, as
            // for the top-level status below.
            var mated = MatedComponents(graph);
            // A cam or path mate holds a part differently at every pose, and
            // the status is read at one pose. At a cam's dwell the follower
            // cannot move to first order, so SolidWorks calls it fully
            // defined, and the cam moves it 79 mm further round (corpus
            // cam-follower, 2026-09-22: the lifter was welded and the roller
            // ran through the lobe). The DOF probe reads the same pose, so it
            // cannot tell either. Such a part is never welded on its status,
            // nor is anything the mates alone make one body with it.
            var poseTouched = PoseHeldComponents(graph);
            HashSet<string> poseHeld = null;
            if (poseTouched.Count > 0 && (statusWelds || subStatusWelds))
            {
                // What the mates alone make one body, with neither status
                // pass: both passes keep their hands off that whole body.
                var mateOnly = Group(graph, solverRigidPairs, null,
                    statusWelds: false, subStatusWelds: false);
                var heldGroups = new HashSet<string>();
                foreach (string id in poseTouched)
                {
                    string g;
                    if (mateOnly.ComponentGroup.TryGetValue(id, out g)) heldGroups.Add(g);
                }
                poseHeld = new HashSet<string>();
                foreach (var kv in mateOnly.ComponentGroup)
                    if (heldGroups.Contains(kv.Value)) poseHeld.Add(kv.Key);
            }
            var poseSkips = new List<string>();
            var subStatusWelded = new List<string>();
            for (int i = 0; i < comps.Count && subStatusWelds; i++)
            {
                var c = comps[i];
                if (c.ParentId == null || c.SubStatusFree != SwFullyConstrained) continue;
                if (!mated.Contains(c.Id)) continue;
                // Not only the part the cam touches: a part bolted to the
                // follower reads fully defined at a dwell too, and welding it
                // to the subassembly takes the follower with it.
                if (poseHeld != null ? poseHeld.Contains(c.Id) : poseTouched.Contains(c.Id))
                {
                    poseSkips.Add(c.Path ?? c.Id);
                    continue;
                }
                int p;
                if (!indexById.TryGetValue(c.ParentId, out p)) continue;
                if (Find(parent, i) == Find(parent, p)) continue;
                Union(parent, i, p);
                subStatusWelded.Add(c.Path ?? c.Id);
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
            var statusWeldedIds = new List<string>();
            if (assemblyProxy >= 0 && statusWelds)
            {
                for (int i = 0; i < comps.Count; i++)
                {
                    var c = comps[i];
                    if (i == assemblyProxy || c.ParentId != null) continue;
                    if (c.StatusFree != SwFullyConstrained) continue;
                    // A fixed component always reads fully defined. A fixed
                    // FLEXIBLE node that Grounds() will not ground is not
                    // grounded on its status either: it is not a body, and
                    // the barrel fixed inside it would go with it (the
                    // ClampRig rams, 2026-08-24).
                    if (c.IsFixed && !Grounds(c, flexibleGrounds)) continue;
                    if (!mated.Contains(c.Id)) continue;
                    if (statusVetoed != null && statusVetoed.Contains(c.Id)) continue;
                    if (poseHeld != null && poseHeld.Contains(c.Id))
                    {
                        poseSkips.Add(c.Path ?? c.Id);
                        continue;
                    }
                    if (Find(parent, i) == Find(parent, assemblyProxy)) continue;
                    Union(parent, i, assemblyProxy);
                    statusWelded.Add(c.Path ?? c.Id);
                    statusWeldedIds.Add(c.Id);
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
                // A mate on three or more components joins in as soon as
                // they fall into two groups: a tab centred between faces on
                // two plates welded together is a width between two bodies.
                foreach (var multi in multiMates)
                {
                    int ra, rb;
                    if (!TwoBodies(multi, i => Find(parent, i), out ra, out rb)) continue;
                    long rootKey = PairKey(Math.Min(ra, rb), Math.Max(ra, rb));
                    List<GraphMate> list;
                    if (!byRootPair.TryGetValue(rootKey, out list))
                    {
                        list = new List<GraphMate>();
                        byRootPair[rootKey] = list;
                    }
                    list.Add(multi.Mate);
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

            var result = BuildResult(comps, parent, pairMates, keys, flexibleGrounds, multiMates);
            result.MergedAwayDofs = UnderDefinedButMerged(comps, parent, result);
            result.StatusWelds = statusWelded;
            result.StatusWeldIds = statusWeldedIds;
            result.PoseHeldSkips = poseSkips;
            result.SubStatusWelds = subStatusWelded;
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
        /// <summary>Components an active cam-follower or path mate touches.
        /// </summary>
        private static HashSet<string> PoseHeldComponents(MateGraph graph)
        {
            var touched = new HashSet<string>();
            foreach (var m in graph.Mates)
            {
                if (m.Suppressed) continue;
                if (!MateFacts.Is(m, "CAMFOLLOWER") && !MateFacts.Is(m, "PATH")) continue;
                foreach (var e in m.Entities)
                    if (e != null && e.ComponentId != null) touched.Add(e.ComponentId);
            }
            return touched;
        }

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
            bool flexibleGrounds, List<MultiMate> multiMates)
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
            foreach (var multi in multiMates)
            {
                int ga, gb;
                if (!TwoBodies(multi, i => groupIndexByRoot[Find(parent, i)], out ga, out gb))
                {
                    // Consumed by a merge (every part in one group) or still
                    // spread over three bodies: only the second is lost.
                    var groupsTouched = new HashSet<int>();
                    foreach (int i in multi.Components)
                        groupsTouched.Add(groupIndexByRoot[Find(parent, i)]);
                    if (groupsTouched.Count > 1)
                        result.UnreadMultiMates.Add(multi.Mate.FeatureName ?? "?");
                    continue;
                }
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
                edge.Mates.Add(multi.Mate);
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
        /// Resolves which two components a mate spans. A mate touching one
        /// component only takes no part. One touching three or more is
        /// handled by MultiSpan, once its components fall into two groups.
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

        /// <summary>A mate on three or more components: the component
        /// index of each entity, in entity order.</summary>
        private sealed class MultiMate
        {
            public GraphMate Mate;
            public int[] EntityComponents;
            public List<int> Components;
        }

        /// <summary>
        /// A mate whose entities sit on three or more components, or null.
        /// A mate with an entity on a suppressed component is inert, as for
        /// a pair. A symmetric mate stays out: across three bodies it is a
        /// coupling, which SymmetricCoupler reads, and never a pair mate.
        /// </summary>
        private static MultiMate MultiSpan(
            GraphMate mate, Dictionary<string, int> indexById, int assemblyProxy)
        {
            if (MateFacts.Is(mate, "SYMMETRIC")) return null;
            var entityComponents = new int[mate.Entities.Count];
            var distinct = new List<int>();
            for (int k = 0; k < mate.Entities.Count; k++)
            {
                var e = mate.Entities[k];
                int idx;
                if (e.ComponentId == null)
                {
                    if (assemblyProxy < 0) return null;
                    idx = assemblyProxy;
                }
                else if (!indexById.TryGetValue(e.ComponentId, out idx))
                {
                    return null;    // entity on a suppressed component
                }
                entityComponents[k] = idx;
                if (!distinct.Contains(idx)) distinct.Add(idx);
            }
            if (distinct.Count < 3) return null;
            return new MultiMate
            {
                Mate = mate, EntityComponents = entityComponents, Components = distinct,
            };
        }

        /// <summary>
        /// Whether a mate on three or more components comes down to two
        /// bodies, and which, given where each component now sits. A width
        /// needs more than two bodies: its width faces (the first two
        /// entities, as SolidWorks lists them) on one body and its tab on
        /// the other. A width face and a tab face on each of two bodies ties
        /// nothing between them, so that split is refused (live CutterRig,
        /// 2026-09-21: every plate held in a groove between two other plates
        /// slid, because such widths were dropped whole).
        /// </summary>
        private static bool TwoBodies(
            MultiMate multi, Func<int, int> bodyOf, out int a, out int b)
        {
            a = -1; b = -1;
            var bodies = new List<int>();
            foreach (int i in multi.Components)
            {
                int body = bodyOf(i);
                if (!bodies.Contains(body)) bodies.Add(body);
            }
            if (bodies.Count != 2) return false;
            if (MateFacts.Is(multi.Mate, "WIDTH") && multi.EntityComponents.Length >= 3)
            {
                int width = bodyOf(multi.EntityComponents[0]);
                if (bodyOf(multi.EntityComponents[1]) != width) return false;
                for (int k = 2; k < multi.EntityComponents.Length; k++)
                    if (bodyOf(multi.EntityComponents[k]) == width) return false;
            }
            a = bodies[0];
            b = bodies[1];
            return true;
        }

        /// <summary>
        /// Reads the mates on three or more groups again, once the loop
        /// analysis has welded groups together. A mate whose groups now fall
        /// into two welded bodies holds the one moving joint between those
        /// bodies: that joint is resolved again with its own mates and this
        /// one, and becomes a weld when nothing is left free. A width must
        /// have its width faces on one body and its tab on the other, as in
        /// the grouping. Returns the joints it welded.
        ///
        /// Live CutterRig (2026-09-22): two plates are centred on a tab
        /// made of a face on the lead screw housing and a face on the frame.
        /// The housing is its own group, and only the loop analysis finds it
        /// welded to the frame. The grouping had dropped the mate over three
        /// groups, and the plates slid along the width.
        /// </summary>
        public static List<string> HoldAcrossWelds(
            MateGraph graph, RigidGroupingResult grouping, List<RigJoint> joints,
            Action<string> log)
        {
            var welded = new List<string>();
            if (grouping.UnreadMultiMates.Count == 0) return welded;
            string ground = null;
            foreach (var g in grouping.Groups)
                if (g.Grounded) { ground = g.Id; break; }

            // Groups joined by welds are one body from here on.
            var link = new Dictionary<string, string>();
            Func<string, string> body = null;
            body = g =>
            {
                string up;
                if (!link.TryGetValue(g, out up) || up == g) return g;
                string top = body(up);
                link[g] = top;
                return top;
            };
            foreach (var j in joints)
            {
                if (j.Type != JointType.Fixed || j.ParentGroup == null || j.ChildGroup == null)
                    continue;
                string a = body(j.ParentGroup), b = body(j.ChildGroup);
                if (a == b) continue;
                if (string.CompareOrdinal(a, b) < 0) link[b] = a;
                else link[a] = b;
            }

            var unread = new HashSet<string>(grouping.UnreadMultiMates);
            foreach (var mate in graph.Mates)
            {
                if (mate.Suppressed || MateFacts.Is(mate, "SYMMETRIC")) continue;
                if (!unread.Contains(mate.FeatureName ?? "?")) continue;

                var bodies = new string[mate.Entities.Count];
                var groupsTouched = new HashSet<string>();
                bool inert = false;
                for (int k = 0; k < mate.Entities.Count && !inert; k++)
                {
                    var e = mate.Entities[k];
                    string g = null;
                    if (e.ComponentId == null) g = ground;
                    else grouping.ComponentGroup.TryGetValue(e.ComponentId, out g);
                    if (g == null) { inert = true; break; }
                    groupsTouched.Add(g);
                    bodies[k] = body(g);
                }
                if (inert || groupsTouched.Count < 3) continue;
                var two = new List<string>();
                foreach (string b in bodies) if (!two.Contains(b)) two.Add(b);
                if (two.Count != 2) continue;
                if (MateFacts.Is(mate, "WIDTH") && bodies.Length >= 3)
                {
                    if (bodies[1] != bodies[0]) continue;
                    bool split = false;
                    for (int k = 2; k < bodies.Length; k++)
                        if (bodies[k] == bodies[0]) split = true;
                    if (split) continue;
                }

                RigJoint only = null;
                int count = 0;
                foreach (var j in joints)
                {
                    if (j.Type == JointType.Fixed || j.ParentGroup == null || j.ChildGroup == null)
                        continue;
                    string pa = body(j.ParentGroup), ch = body(j.ChildGroup);
                    if ((pa == two[0] && ch == two[1]) || (pa == two[1] && ch == two[0]))
                    {
                        only = j;
                        count++;
                    }
                }
                if (count != 1 || only.Coupling != null) continue;

                GroupEdge edge = null;
                foreach (var ed in grouping.Edges)
                    if ((ed.GroupA == only.ParentGroup && ed.GroupB == only.ChildGroup)
                        || (ed.GroupA == only.ChildGroup && ed.GroupB == only.ParentGroup))
                    { edge = ed; break; }
                if (edge == null) continue;
                var mates = new List<GraphMate>();
                foreach (var m in edge.Mates) if (!m.Suppressed) mates.Add(m);
                mates.Add(mate);
                if (!MotionResolver.Resolve(mates).IsRigid)
                {
                    log?.Invoke("mate " + mate.FeatureName + " holds " + only.Id
                        + " once its bodies are welded, but leaves it free to move");
                    continue;
                }

                string was = only.Type;
                only.Type = JointType.Fixed;
                only.TranslationLimit = null;
                only.RotationLimit = null;
                only.SourceMates.Add(new SourceMate { SwFeature = mate.FeatureName, Type = mate.TypeName });
                string note = "Read pairwise this is a " + was + ", but mate " + mate.FeatureName
                    + " holds it: that mate touches three groups, and two of them are welded "
                    + "together by the loops, so it is a mate between this joint's two bodies.";
                only.Notes = string.IsNullOrEmpty(only.Notes) ? note : only.Notes + " " + note;
                grouping.UnreadMultiMates.Remove(mate.FeatureName ?? "?");
                welded.Add(only.Id);
                log?.Invoke("mate " + mate.FeatureName + " welds " + only.Id
                    + ": two of its three groups are welded together by the loops");
            }
            return welded;
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
