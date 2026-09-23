using System.Collections.Generic;
using Peak.Cadder.Core.Model;

namespace Peak.Cadder.Core
{
    /// <summary>
    /// Which children of flexible subassemblies the DOF probe checks before
    /// they are welded to their subassembly on its own status, and against
    /// what.
    ///
    /// SolidWorks' status can call a part fully defined that still moves,
    /// when several freedoms are free at once (corpus hydraulic assembly,
    /// 2026-09-22: a slider behind two limits). At the top level the probe
    /// finds that (ExportCommand.VetoStatusWelds). Inside a flexible
    /// subassembly the top document reads every pair as fixed (live corpus
    /// 07, 2026-08-23), so the probe runs in the subassembly's own document,
    /// where its children are top level. The subassembly's grounded body is
    /// fixed there, and each child the status would weld is read against it.
    ///
    /// No SolidWorks types here, so the choice can be tested. The caller
    /// finds the components in the subassembly's document by name.
    /// </summary>
    public static class SubStatusVeto
    {
        /// <summary>One flexible subassembly to probe.</summary>
        public sealed class Probe
        {
            /// <summary>The flexible subassembly's component id.</summary>
            public string SubId;

            /// <summary>Its children that the mates alone make one body with
            /// its frame: fixed in it, or mated rigid to a part that is.
            /// </summary>
            public List<string> Ground = new List<string>();

            /// <summary>The children the status would weld, one list per body
            /// the mates alone make, in the order they were welded. One member
            /// of a body is read for all of it.</summary>
            public List<List<string>> Bodies = new List<List<string>>();
        }

        /// <summary>
        /// The probes for the children that <paramref name="withStatus"/>
        /// welds on their subassembly status. A child that the mates alone
        /// already put on its subassembly's frame is not probed: the weld
        /// does not depend on the status.
        /// </summary>
        public static List<Probe> Plan(MateGraph graph, RigidGroupingResult withStatus)
        {
            var plans = new List<Probe>();
            if (graph == null || withStatus == null || withStatus.SubStatusWeldIds.Count == 0)
                return plans;

            var mateOnly = RigidGrouper.Group(
                graph, null, null, statusWelds: false, subStatusWelds: false);
            var byId = new Dictionary<string, GraphComponent>();
            foreach (var c in graph.Components)
                if (c != null && c.Id != null) byId[c.Id] = c;

            var bySub = new Dictionary<string, Probe>();
            var bodyOf = new Dictionary<string, List<string>>();
            foreach (string id in withStatus.SubStatusWeldIds)
            {
                GraphComponent c;
                if (!byId.TryGetValue(id, out c) || c.ParentId == null) continue;
                string frame, body;
                if (!mateOnly.ComponentGroup.TryGetValue(c.ParentId, out frame)) continue;
                if (!mateOnly.ComponentGroup.TryGetValue(id, out body) || body == frame)
                    continue;

                Probe plan;
                if (!bySub.TryGetValue(c.ParentId, out plan))
                {
                    plan = new Probe { SubId = c.ParentId };
                    foreach (var other in graph.Components)
                    {
                        if (other == null || other.Suppressed || other.ParentId != c.ParentId)
                            continue;
                        string g;
                        if (mateOnly.ComponentGroup.TryGetValue(other.Id, out g) && g == frame)
                            plan.Ground.Add(other.Id);
                    }
                    bySub[c.ParentId] = plan;
                    plans.Add(plan);
                }

                List<string> members;
                string key = c.ParentId + "|" + body;
                if (!bodyOf.TryGetValue(key, out members))
                {
                    members = new List<string>();
                    bodyOf[key] = members;
                    plan.Bodies.Add(members);
                }
                members.Add(id);
            }
            return plans;
        }
    }
}
