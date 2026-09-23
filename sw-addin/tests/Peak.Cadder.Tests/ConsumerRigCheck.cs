using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The checks the consumer makes before it builds a rig (CADder
    /// rig/graph.py build), and the way it applies a mechanism option
    /// (rig/inputs.py apply). A manifest that fails here is a manifest
    /// Blender refuses, so the analyzer's tests ask the same questions.
    /// </summary>
    internal static class ConsumerRigCheck
    {
        /// <summary>What the consumer would refuse the manifest for, or
        /// null when it builds.</summary>
        public static string Problem(
            IList<RigidGroup> groups, IList<RigJoint> joints, IList<RigLoop> loops)
        {
            var grounded = new HashSet<string>();
            foreach (var g in groups) if (g.Grounded) grounded.Add(g.Id);
            var closures = new HashSet<string>();
            foreach (var lp in loops) closures.Add(lp.ClosureJoint);

            var byId = new Dictionary<string, RigJoint>();
            foreach (var j in joints) byId[j.Id] = j;

            // A free joint parents nothing, except the two halves of a
            // mirror pair.
            var mirrorDrivers = new HashSet<string>();
            foreach (var j in joints)
                if (j.Coupling != null && j.Coupling.Kind == "mirror" && j.Coupling.DriverJoint != null)
                    mirrorDrivers.Add(j.Coupling.DriverJoint);

            var parentOf = new Dictionary<string, RigJoint>();
            var root = new Dictionary<string, string>();
            string Find(string g)
            {
                if (!root.ContainsKey(g)) root[g] = g;
                while (root[g] != g) g = root[g] = root[root[g]];
                return g;
            }
            foreach (var j in joints)
            {
                bool mirror = mirrorDrivers.Contains(j.Id)
                              || (j.Coupling != null && j.Coupling.Kind == "mirror");
                if (j.Type == JointType.Free && !mirror) continue;
                if (closures.Contains(j.Id)) continue;
                if (j.ParentGroup == j.ChildGroup)
                    return "joint " + j.Id + ": parent and child are both " + j.ParentGroup;
                if (grounded.Contains(j.ChildGroup))
                    return "joint " + j.Id + ": child " + j.ChildGroup + " is grounded";
                RigJoint other;
                if (parentOf.TryGetValue(j.ChildGroup, out other))
                    return "group " + j.ChildGroup + " has two tree parents (joints "
                           + other.Id + " and " + j.Id + ")";
                string a = Find(j.ChildGroup), b = Find(j.ParentGroup);
                if (a == b) return "joint " + j.Id + " closes a cycle the exporter did not declare";
                root[a] = b;
                parentOf[j.ChildGroup] = j;
            }

            foreach (var lp in loops)
            {
                RigJoint cj;
                if (!byId.TryGetValue(lp.ClosureJoint ?? "", out cj))
                    return "loop " + lp.Id + ": no closure joint " + lp.ClosureJoint;
                if (Find(cj.ParentGroup) != Find(cj.ChildGroup))
                    return "loop " + lp.Id + ": closure " + cj.Id + " bridges two trees";
                var up = new List<string>();
                for (string g = cj.ParentGroup; ; )
                {
                    up.Add(g);
                    RigJoint pj;
                    if (!parentOf.TryGetValue(g, out pj)) break;
                    g = pj.ParentGroup;
                }
                var path = new HashSet<string> { cj.Id };
                string at = cj.ChildGroup;
                while (!up.Contains(at))
                {
                    var pj = parentOf[at];
                    path.Add(pj.Id);
                    at = pj.ParentGroup;
                }
                foreach (string g in up)
                {
                    if (g == at) break;
                    path.Add(parentOf[g].Id);
                }
                if (!path.SetEquals(lp.MemberJoints))
                    return "loop " + lp.Id + ": member_joints " + string.Join(",", lp.MemberJoints)
                           + " do not match the tree path plus closure " + string.Join(",", path);
            }

            // A coupling between tree joints is a dependency: the driven
            // bone reads the driver's. A cycle of them is refused.
            foreach (var j in joints)
            {
                var seen = new HashSet<string> { j.Id };
                for (var d = j; d.Coupling != null && d.Coupling.DriverJoint != null; )
                {
                    RigJoint next;
                    if (!byId.TryGetValue(d.Coupling.DriverJoint, out next)) break;
                    if (!seen.Add(next.Id))
                        return "rig dependency cycle through " + next.Id;
                    d = next;
                }
            }
            return null;
        }

        /// <summary>Every refusal over the manifest as written and over each
        /// mechanism option applied on its own, the way the consumer applies
        /// one. Empty when every configuration builds.</summary>
        public static List<string> EveryOption(IList<RigidGroup> groups, LoopAnalysisResult result)
        {
            var problems = new List<string>();
            string main = Problem(groups, result.Joints, result.Loops);
            if (main != null) problems.Add("as written: " + main);
            foreach (var mech in result.Mechanisms)
                for (int k = 1; k < mech.Inputs.Count; k++)
                {
                    List<RigJoint> joints;
                    List<RigLoop> loops;
                    Apply(result, mech, mech.Inputs[k], out joints, out loops);
                    string p = Problem(groups, joints, loops);
                    if (p != null) problems.Add(mech.Id + " input " + mech.Inputs[k].Joint + ": " + p);
                }
            return problems;
        }

        /// <summary>The joints and loops once `option` is applied from the
        /// manifest as written: its joints flipped, its limits taken, the
        /// mechanism's loops replaced. A coupled pair turns its coupling
        /// round instead.</summary>
        public static void Apply(
            LoopAnalysisResult result, RigMechanism mech, RigInputOption option,
            out List<RigJoint> joints, out List<RigLoop> loops)
        {
            joints = new List<RigJoint>();
            var byId = new Dictionary<string, RigJoint>();
            foreach (var j in result.Joints)
            {
                var c = new RigJoint
                {
                    Id = j.Id,
                    Type = j.Type,
                    ParentGroup = j.ParentGroup,
                    ChildGroup = j.ChildGroup,
                    RotationLimit = j.RotationLimit,
                    TranslationLimit = j.TranslationLimit,
                    Coupling = j.Coupling == null ? null : new JointCoupling
                    {
                        Kind = j.Coupling.Kind,
                        DriverJoint = j.Coupling.DriverJoint,
                        Ratio = j.Coupling.Ratio,
                    },
                };
                if (option.FlippedJoints.Contains(j.Id))
                {
                    c.ParentGroup = j.ChildGroup;
                    c.ChildGroup = j.ParentGroup;
                }
                joints.Add(c);
                byId[c.Id] = c;
            }
            foreach (var limit in option.JointLimits)
            {
                RigJoint j;
                if (!byId.TryGetValue(limit.Joint, out j)) continue;
                j.RotationLimit = limit.RotationLimit;
                j.TranslationLimit = limit.TranslationLimit;
            }
            loops = new List<RigLoop>();
            foreach (var lp in result.Loops)
                if (!mech.LoopIds.Contains(lp.Id)) loops.Add(lp);
            loops.AddRange(option.Loops);

            if (mech.CouplingPair && mech.Inputs.Count == 2)
            {
                RigJoint a, b;
                if (!byId.TryGetValue(mech.Inputs[0].Joint, out a)) return;
                if (!byId.TryGetValue(mech.Inputs[1].Joint, out b)) return;
                RigJoint driven = b.Coupling != null && b.Coupling.DriverJoint == a.Id ? b : a;
                RigJoint driver = driven == b ? a : b;
                if (driven.Id == option.Joint && driven.Coupling != null)
                {
                    var coupling = driven.Coupling;
                    driven.Coupling = null;
                    coupling.DriverJoint = driven.Id;
                    driver.Coupling = coupling;
                }
            }
        }
    }
}
