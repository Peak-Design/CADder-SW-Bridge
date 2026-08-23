using System;
using System.Collections.Generic;
using Peak.SwToBlender.Core.Model;

namespace Peak.SwToBlender.Core
{
    public sealed class LoopAnalysisResult
    {
        public List<RigLoop> Loops = new List<RigLoop>();

        /// <summary>The input joints, same instances and order, with
        /// ParentGroup/ChildGroup oriented so the parent is nearer the
        /// grounded root. The Blender side parents bones exactly as given.</summary>
        public List<RigJoint> Joints = new List<RigJoint>();

        /// <summary>Free joints whose rotation lock became a gear coupling on
        /// the tree joints it constrains — their UNDER_DEFINED warning is
        /// obsolete: the mate IS modelled.</summary>
        public List<string> CoupledFreeJointIds = new List<string>();

        /// <summary>Free joints whose constraint is already enforced by a
        /// declared loop (redundant mates over a closed ring) — harmless, and
        /// their warning should say so instead of crying under-defined.</summary>
        public List<string> RedundantFreeJointIds = new List<string>();
    }

    /// <summary>
    /// Bone hierarchies cannot hold cycles, so every non-tree edge of the
    /// joint graph becomes a recorded loop with a chosen cut. The consumer
    /// parents along the tree and closes each loop at the cut with IK; it must
    /// not re-derive any of this.
    /// </summary>
    public static class LoopAnalyzer
    {
        public static LoopAnalysisResult Analyze(IList<RigidGroup> groups, IList<RigJoint> joints)
        {
            var result = new LoopAnalysisResult();
            foreach (var j in joints) result.Joints.Add(j);

            var groupIndex = new Dictionary<string, int>();
            for (int i = 0; i < groups.Count; i++) groupIndex[groups[i].Id] = i;

            var adjacency = new List<RigJoint>[groups.Count];
            for (int i = 0; i < adjacency.Length; i++) adjacency[i] = new List<RigJoint>();
            var usable = new List<RigJoint>(joints.Count);
            foreach (var j in joints)
            {
                // Free joints are under-mated pairs the consumer never
                // parents (SCHEMA.md), so they are not graph edges here
                // either. Counting them made the exporter's spanning tree
                // disagree with the consumer's — live corpus 06
                // parallelogram2 (2026-08-22): a redundant parallel mate
                // exported free but held a tree slot, every real ring joint
                // became a loop closure, and the Blender side rightly refused
                // the manifest as disconnected.
                if (j.Type == JointType.Free) continue;
                int a, b;
                if (!groupIndex.TryGetValue(j.ParentGroup, out a)) continue;
                if (!groupIndex.TryGetValue(j.ChildGroup, out b)) continue;
                if (a == b) continue;
                adjacency[a].Add(j);
                adjacency[b].Add(j);
                usable.Add(j);
            }
            foreach (var list in adjacency)
                list.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));

            // The first grounded group roots the main tree; further grounded
            // groups and stranded islands root their own trees so every joint
            // still gets an orientation.
            var roots = new List<int>();
            for (int i = 0; i < groups.Count; i++) if (groups[i].Grounded) roots.Add(i);
            for (int i = 0; i < groups.Count; i++) if (!groups[i].Grounded) roots.Add(i);

            var tree = new HashSet<string>();
            var walk = Bfs(groups.Count, adjacency, groupIndex, roots, null, tree);

            var nonTree = new List<RigJoint>();
            foreach (var j in usable)
                if (!tree.Contains(j.Id)) nonTree.Add(j);
            nonTree.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));

            int loopNumber = 1;
            foreach (var closureCandidate in nonTree)
            {
                // A previous swap may have pulled this edge into the tree; it
                // was that loop's business, not a loop of its own.
                if (tree.Contains(closureCandidate.Id)) continue;

                walk = Bfs(groups.Count, adjacency, groupIndex, roots, tree, null);
                var ring = CycleRing(closureCandidate, walk, groupIndex);
                if (ring == null) continue;

                // The consumer's IK solves ONLY the driven side of the cut, so
                // every loop joint except the driver's own edge must land on
                // that side: the cut goes just past the driver's moving group.
                // The driver itself must be an edge at the ring's anchor —
                // posing anything deeper drags IK-solved groups along. Found
                // live on corpus 06 (2026-08-22): the old "first revolute
                // member" cut removed the crank's own edge, the driver side
                // came out empty and the four-bar froze solid.
                RigJoint cut, driver;
                int n = ring.Edges.Count;
                if (n == 2)
                {
                    // Two joints between one pair of groups: no second moving
                    // body, so no driver side exists — the closure only
                    // re-constrains the same pair. The non-tree edge stays cut.
                    cut = closureCandidate;
                    driver = ReferenceEquals(ring.Edges[0], closureCandidate)
                        ? ring.Edges[1] : ring.Edges[0];
                }
                else if (PreferFirst(ring.Edges[0], ring.Edges[1],
                                     ring.Edges[n - 1], ring.Edges[n - 2]))
                {
                    driver = ring.Edges[0];
                    cut = ring.Edges[1];
                }
                else
                {
                    driver = ring.Edges[n - 1];
                    cut = ring.Edges[n - 2];
                }
                if (!ReferenceEquals(cut, closureCandidate))
                {
                    tree.Remove(cut.Id);
                    tree.Add(closureCandidate.Id);
                }

                var loop = new RigLoop();
                loop.Id = "loop" + loopNumber.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
                loopNumber++;
                var members = new List<RigJoint>(ring.Edges);
                members.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));
                foreach (var j in members) loop.MemberJoints.Add(j.Id);
                loop.ClosureJoint = cut.Id;
                loop.SuggestedDriverJoint = driver.Id;
                SetPlanarity(loop, members);
                result.Loops.Add(loop);
            }

            Orient(result.Joints, groups.Count, adjacency, groupIndex, roots, tree);
            SynthesizeParallelCouplings(
                result, groups.Count, adjacency, groupIndex, roots, tree);
            return result;
        }

        // ── Parallel-mate couplings ─────────────────────────────────────────

        /// <summary>
        /// A parallel mate between two moving links forms no joint (it kills
        /// two rotations and no translation), but when every tree joint on
        /// the paths between the pair rotates about one common direction z,
        /// locking their relative orientation about z means the SIGNED JOINT
        /// ANGLES on the two paths must stay equal — a 1:1 gear relation.
        /// Live corpus 06 parallelogram3 (2026-08-22): a corner pin replaced
        /// by parallel mates left three independent revolutes; SolidWorks
        /// still moved as a parallelogram, the rig did not. Only two-term
        /// relations are expressible (one coupling = one driver), and a
        /// relation already enforced by a declared loop's IK closure must NOT
        /// also become a driver — the two would fight.
        /// </summary>
        private static void SynthesizeParallelCouplings(
            LoopAnalysisResult result, int groupCount, List<RigJoint>[] adjacency,
            Dictionary<string, int> groupIndex, List<int> roots, HashSet<string> tree)
        {
            var walk = Bfs(groupCount, adjacency, groupIndex, roots, tree, null);

            var loopMembers = new HashSet<string>();
            foreach (var lp in result.Loops)
                foreach (var id in lp.MemberJoints) loopMembers.Add(id);

            var childDepth = new Dictionary<string, int>();   // tree joint id -> child group depth
            foreach (var j in result.Joints)
            {
                if (!tree.Contains(j.Id)) continue;
                int c;
                if (groupIndex.TryGetValue(j.ChildGroup, out c)) childDepth[j.Id] = walk.Depth[c];
            }

            foreach (var free in result.Joints)
            {
                if (free.Type != JointType.Free || !free.ResidualKnown) continue;
                if (free.ResidualRot == RotFreedom.Full
                    || free.ResidualRot == RotFreedom.AboutPoint) continue;

                int u, v;
                if (!groupIndex.TryGetValue(free.ParentGroup, out u)) continue;
                if (!groupIndex.TryGetValue(free.ChildGroup, out v)) continue;
                if (u == v || walk.Depth[u] < 0 || walk.Depth[v] < 0) continue;

                // Tree-path joints from each end up to the common ancestor.
                var pathU = new List<RigJoint>();
                var pathV = new List<RigJoint>();
                int uu = u, vv = v;
                bool broken = false;
                while (walk.Depth[uu] > walk.Depth[vv]) { pathU.Add(walk.ParentJoint[uu]); uu = walk.ParentGroup[uu]; }
                while (walk.Depth[vv] > walk.Depth[uu]) { pathV.Add(walk.ParentJoint[vv]); vv = walk.ParentGroup[vv]; }
                while (uu != vv)
                {
                    if (walk.ParentGroup[uu] < 0 || walk.ParentGroup[vv] < 0) { broken = true; break; }
                    pathU.Add(walk.ParentJoint[uu]);
                    uu = walk.ParentGroup[uu];
                    pathV.Add(walk.ParentJoint[vv]);
                    vv = walk.ParentGroup[vv];
                }
                if (broken || pathU.Count + pathV.Count != 2) continue;

                // Every path joint must spin about one common direction z for
                // "orientation locked about z" to mean "signed angles equal".
                var terms = new List<RigJoint>();
                var signs = new List<int>();     // +1 on the parent-side path, -1 on the child side, times the axis sense
                double[] z = null;
                bool eligible = true;
                for (int side = 0; side < 2; side++)
                {
                    var path = side == 0 ? pathU : pathV;
                    int pathSign = side == 0 ? 1 : -1;
                    foreach (var t in path)
                    {
                        bool rotates = t.Type == JointType.Revolute || t.Type == JointType.Cylindrical;
                        if (!rotates || t.Axis == null) { eligible = false; break; }
                        var axis = MathOps.Normalized(t.Axis);
                        if (z == null) z = axis;
                        else if (!MateFacts.IsParallel(axis, z)) { eligible = false; break; }
                        terms.Add(t);
                        signs.Add(MathOps.Dot(axis, z) > 0 ? pathSign : -pathSign);
                    }
                    if (!eligible) break;
                }
                if (!eligible || z == null) continue;

                // The mate must actually kill relative rotation about z: any
                // surviving rotation direction parallel to z leaves the
                // angles unrelated.
                if (free.ResidualRot != RotFreedom.None)
                {
                    if (free.ResidualRotDir == null) continue;
                    if (MateFacts.IsParallel(free.ResidualRotDir, z)) continue;
                }

                if (loopMembers.Contains(terms[0].Id) || loopMembers.Contains(terms[1].Id))
                {
                    result.RedundantFreeJointIds.Add(free.Id);
                    continue;
                }

                // Driven = the joint deeper in the tree (its bone hangs below
                // the driver's in the rig); equal depths fall to the higher
                // id, so the first-created ground joint stays the input.
                int d0, d1;
                childDepth.TryGetValue(terms[0].Id, out d0);
                childDepth.TryGetValue(terms[1].Id, out d1);
                int drivenIdx;
                if (d0 != d1) drivenIdx = d0 > d1 ? 0 : 1;
                else drivenIdx = string.CompareOrdinal(terms[0].Id, terms[1].Id) > 0 ? 0 : 1;
                if (terms[drivenIdx].Coupling != null) drivenIdx = 1 - drivenIdx;
                var driven = terms[drivenIdx];
                var driver = terms[1 - drivenIdx];
                if (driven.Coupling != null || driven == driver) continue;

                // Chained couplings are fine; a mutual pair takes the whole
                // manifest down in the consumer's dependency pre-flight.
                bool cycles = false;
                var visited = new HashSet<string>();
                for (var d = driver; d != null && d.Coupling != null
                     && d.Coupling.DriverJoint != null;)
                {
                    if (!visited.Add(d.Id) || d.Coupling.DriverJoint == driven.Id)
                    {
                        cycles = true;
                        break;
                    }
                    d = FindJoint(result.Joints, d.Coupling.DriverJoint);
                }
                if (cycles) continue;

                // signs[p]*theta_p + signs[q]*theta_q = 0, both in {-1, +1},
                // so theta_driven = -(sign_driver * sign_driven) * theta_driver.
                driven.Coupling = new JointCoupling
                {
                    Kind = "gear",
                    DriverJoint = driver.Id,
                    Ratio = -(double)(signs[drivenIdx] * signs[1 - drivenIdx]),
                };
                foreach (var sm in free.SourceMates)
                    driven.SourceMates.Add(new SourceMate { SwFeature = sm.SwFeature, Type = sm.Type });
                result.CoupledFreeJointIds.Add(free.Id);
            }
        }

        private static RigJoint FindJoint(List<RigJoint> joints, string id)
        {
            foreach (var j in joints)
                if (j.Id == id) return j;
            return null;
        }

        // ── Spanning tree ───────────────────────────────────────────────────

        private sealed class Walk
        {
            public int[] Depth;
            public int[] ParentGroup;          // -1 at a root
            public RigJoint[] ParentJoint;
        }

        /// <summary>
        /// BFS over the group graph. With restrictTo null it builds the
        /// initial spanning tree and records it into recordTree; with a tree
        /// set given it only walks those edges, recomputing depths and parents
        /// after a cut swap.
        /// </summary>
        private static Walk Bfs(
            int groupCount, List<RigJoint>[] adjacency, Dictionary<string, int> groupIndex,
            List<int> roots, HashSet<string> restrictTo, HashSet<string> recordTree)
        {
            var walk = new Walk();
            walk.Depth = new int[groupCount];
            walk.ParentGroup = new int[groupCount];
            walk.ParentJoint = new RigJoint[groupCount];
            var visited = new bool[groupCount];
            for (int i = 0; i < groupCount; i++) { walk.Depth[i] = -1; walk.ParentGroup[i] = -1; }

            var queue = new Queue<int>();
            foreach (int root in roots)
            {
                if (visited[root]) continue;
                visited[root] = true;
                walk.Depth[root] = 0;
                queue.Enqueue(root);
                while (queue.Count > 0)
                {
                    int g = queue.Dequeue();
                    foreach (var j in adjacency[g])
                    {
                        if (restrictTo != null && !restrictTo.Contains(j.Id)) continue;
                        int other = OtherEnd(j, g, groupIndex);
                        if (visited[other]) continue;
                        visited[other] = true;
                        walk.Depth[other] = walk.Depth[g] + 1;
                        walk.ParentGroup[other] = g;
                        walk.ParentJoint[other] = j;
                        if (recordTree != null) recordTree.Add(j.Id);
                        queue.Enqueue(other);
                    }
                }
            }
            return walk;
        }

        private static int OtherEnd(RigJoint j, int g, Dictionary<string, int> groupIndex)
        {
            int a = groupIndex[j.ParentGroup];
            int b = groupIndex[j.ChildGroup];
            return a == g ? b : a;
        }

        private sealed class Ring
        {
            public List<int> Groups = new List<int>();       // Groups[0] is the anchor, nearest its root
            public List<RigJoint> Edges = new List<RigJoint>(); // Edges[i] joins Groups[i] to Groups[(i+1) % n]
        }

        /// <summary>The fundamental cycle of a non-tree edge as an ordered
        /// ring starting at the anchor (the cycle group nearest the root):
        /// both tree paths up to the common ancestor plus the edge itself.
        /// Driver and cut selection need adjacency, which a sorted member
        /// list cannot give.</summary>
        private static Ring CycleRing(
            RigJoint closure, Walk walk, Dictionary<string, int> groupIndex)
        {
            int u = groupIndex[closure.ParentGroup];
            int v = groupIndex[closure.ChildGroup];
            if (walk.Depth[u] < 0 || walk.Depth[v] < 0) return null;

            var groupsU = new List<int>();
            var edgesU = new List<RigJoint>();
            var groupsV = new List<int>();
            var edgesV = new List<RigJoint>();
            while (walk.Depth[u] > walk.Depth[v])
            {
                groupsU.Add(u);
                edgesU.Add(walk.ParentJoint[u]);
                u = walk.ParentGroup[u];
            }
            while (walk.Depth[v] > walk.Depth[u])
            {
                groupsV.Add(v);
                edgesV.Add(walk.ParentJoint[v]);
                v = walk.ParentGroup[v];
            }
            while (u != v)
            {
                if (walk.ParentGroup[u] < 0 || walk.ParentGroup[v] < 0) return null;
                groupsU.Add(u);
                edgesU.Add(walk.ParentJoint[u]);
                u = walk.ParentGroup[u];
                groupsV.Add(v);
                edgesV.Add(walk.ParentJoint[v]);
                v = walk.ParentGroup[v];
            }

            // u == v is the anchor; the ring runs down the v side, across the
            // closure, and back up the u side.
            var ring = new Ring();
            ring.Groups.Add(u);
            for (int i = edgesV.Count - 1; i >= 0; i--)
            {
                ring.Edges.Add(edgesV[i]);
                ring.Groups.Add(groupsV[i]);
            }
            ring.Edges.Add(closure);
            for (int i = 0; i < edgesU.Count; i++)
            {
                ring.Groups.Add(groupsU[i]);
                ring.Edges.Add(edgesU[i]);
            }
            return ring;
        }

        // ── Loop annotations ────────────────────────────────────────────────

        /// <summary>Chooses between the two anchor-incident driver candidates
        /// (each paired with the cut its choice implies). A limited joint is
        /// the modelled input; a revolute drives best under hand-posing; a
        /// revolute cut is the only closure an IK point constraint models
        /// faithfully; ids break the tie so re-exports are stable.</summary>
        private static bool PreferFirst(
            RigJoint driverA, RigJoint cutA, RigJoint driverB, RigJoint cutB)
        {
            bool la = HasLimits(driverA), lb = HasLimits(driverB);
            if (la != lb) return la;
            bool ra = driverA.Type == JointType.Revolute;
            bool rb = driverB.Type == JointType.Revolute;
            if (ra != rb) return ra;
            bool ca = cutA.Type == JointType.Revolute;
            bool cb = cutB.Type == JointType.Revolute;
            if (ca != cb) return ca;
            return string.CompareOrdinal(driverA.Id, driverB.Id) <= 0;
        }

        private static bool HasLimits(RigJoint j)
        {
            return j.RotationLimit != null || j.TranslationLimit != null;
        }

        /// <summary>A loop is planar when every revolute member spins about
        /// the same direction (within 1e-6); the Blender side can then keep
        /// its IK in one plane.</summary>
        private static void SetPlanarity(RigLoop loop, List<RigJoint> members)
        {
            double[] normal = null;
            foreach (var j in members)
            {
                if (j.Type != JointType.Revolute || j.Axis == null) continue;
                if (normal == null) { normal = j.Axis; continue; }
                if (MathOps.Norm(MathOps.Cross(
                        MathOps.Normalized(normal), MathOps.Normalized(j.Axis))) >= 1e-6)
                {
                    loop.Planar = false;
                    loop.PlaneNormal = null;
                    return;
                }
            }
            loop.Planar = normal != null;
            loop.PlaneNormal = normal;
        }

        // ── Orientation ─────────────────────────────────────────────────────

        /// <summary>Parent is the end nearer the root. Tree joints follow the
        /// BFS discovery direction; closure joints orient by depth so their
        /// parent side is also rootward.</summary>
        private static void Orient(
            List<RigJoint> joints, int groupCount, List<RigJoint>[] adjacency,
            Dictionary<string, int> groupIndex, List<int> roots, HashSet<string> tree)
        {
            var walk = Bfs(groupCount, adjacency, groupIndex, roots, tree, null);
            foreach (var j in joints)
            {
                int a, b;
                if (!groupIndex.TryGetValue(j.ParentGroup, out a)) continue;
                if (!groupIndex.TryGetValue(j.ChildGroup, out b)) continue;
                if (a == b) continue;

                bool swap;
                if (tree.Contains(j.Id))
                    swap = ReferenceEquals(walk.ParentJoint[a], j);   // discovery entered at a
                else
                    swap = walk.Depth[a] >= 0 && walk.Depth[b] >= 0 && walk.Depth[b] < walk.Depth[a];

                if (swap)
                {
                    string t = j.ParentGroup;
                    j.ParentGroup = j.ChildGroup;
                    j.ChildGroup = t;
                }
            }
        }
    }
}
