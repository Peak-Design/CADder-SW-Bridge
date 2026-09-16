using System.Collections.Generic;
using Peak.SwToBlender.Core.Model;

namespace Peak.SwToBlender.Core
{
    /// <summary>
    /// The DOF probe's "no relative freedom" verdicts, read as a set rather
    /// than one at a time.
    ///
    /// `GetRemainingDOFs` answers "does this component have freedom of its
    /// OWN", not "can it move relative to the pinned parent". Those are
    /// different questions the moment a third body is involved, and which one
    /// you got depends on what that third body is:
    ///
    ///   * The cutting head of live ClampRig is mated to the machine
    ///     body AND to the lead screw rod. It has no freedom of its own and
    ///     slides half a metre because the rod does. The probe reads it fixed;
    ///     merging on that welds the mechanism shut.
    ///   * The same assembly's buoyancy module is bolted on with nine M16s.
    ///     Every bolt is mated to the machine body AND to the module, so no
    ///     single PAIR is rigid, but the probe reads every pair of that
    ///     cluster fixed, and the cluster genuinely is one body.
    ///
    /// A per-pair "is anything else mated to the child" test cannot tell these
    /// apart: both have a third body. What separates them is what the probe
    /// said about that third body. The bolt's other partner is inside the same
    /// run of fixed verdicts; the cutting head's is not: its link to the rod
    /// was never read fixed. So the verdicts are unioned into closures first,
    /// and a verdict is believed only when every body the child is mated to
    /// lies inside the child's own closure.
    ///
    /// (Live ClampRig, 2026-08-24: the per-pair test rejected all 34 of
    /// the probe's fixed verdicts, and the manifest shipped thirty
    /// PROBE_DISAGREES warnings against joints the exporter had itself read
    /// correctly.)
    /// </summary>
    public sealed class SolverWelds
    {
        private readonly MateGraph _graph;
        private readonly RigidGroupingResult _grouping;
        private readonly string _ground;
        private readonly Dictionary<string, string> _closure =
            new Dictionary<string, string>();

        /// <param name="fixedGroupPairs">Every group pair the probe read as
        /// having no relative freedom, ordered {parent, child}. Blind
        /// verdicts (pairs carrying a mate the probe cannot neutralise)
        /// must be left out by the caller: they say nothing.</param>
        public SolverWelds(
            MateGraph graph, RigidGroupingResult grouping,
            IEnumerable<string[]> fixedGroupPairs)
        {
            _graph = graph;
            _grouping = grouping;
            if (grouping != null)
                foreach (var g in grouping.Groups)
                    if (g.Grounded) { _ground = g.Id; break; }

            if (fixedGroupPairs == null) return;
            foreach (var pair in fixedGroupPairs)
            {
                if (pair == null || pair.Length != 2) continue;
                if (pair[0] == null || pair[1] == null) continue;
                Union(pair[0], pair[1]);
            }
        }

        /// <summary>
        /// Whether the verdict on this pair may be acted on: every group the
        /// CHILD is mated to, other than the pair itself, must sit in the
        /// child's closure of fixed verdicts. Anything else is a third body
        /// the probe did not call rigid, and the child may be following it.
        /// </summary>
        public bool Believable(string parentGroup, string childGroup)
        {
            if (_graph == null || _grouping == null) return false;
            string childRoot = Find(childGroup);

            foreach (var mate in _graph.Mates)
            {
                if (mate.Suppressed) continue;
                bool touchesChild = false;
                List<string> outside = null;
                foreach (var e in mate.Entities)
                {
                    string group;
                    if (e.ComponentId == null) group = _ground;
                    else if (!_grouping.ComponentGroup.TryGetValue(e.ComponentId, out group))
                        continue;    // suppressed component: in no group
                    if (group == null) continue;
                    if (group == childGroup) { touchesChild = true; continue; }
                    if (group == parentGroup) continue;
                    if (outside == null) outside = new List<string>();
                    outside.Add(group);
                }
                if (!touchesChild || outside == null) continue;
                foreach (string g in outside)
                    if (Find(g) != childRoot) return false;
            }
            return true;
        }

        // ── Union-find over group ids ───────────────────────────────────────

        private string Find(string id)
        {
            if (id == null) return null;
            string root;
            if (!_closure.TryGetValue(id, out root)) return id;
            if (root == id) return id;
            root = Find(root);
            _closure[id] = root;
            return root;
        }

        private void Union(string a, string b)
        {
            string ra = Find(a), rb = Find(b);
            if (ra == rb) return;
            // Ordered so the closure is the same however the verdicts arrive.
            if (string.CompareOrdinal(ra, rb) <= 0) _closure[rb] = ra;
            else _closure[ra] = rb;
        }
    }
}
