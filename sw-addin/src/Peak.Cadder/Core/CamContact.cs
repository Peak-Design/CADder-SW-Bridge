using System;
using System.Collections.Generic;
using Peak.Cadder.Core.Model;

namespace Peak.Cadder.Core
{
    /// <summary>
    /// The geometric route for a cam-follower mate: the cam path's faces
    /// travel in the manifest as a "cam" coupling on the follower's own
    /// joint, and the consumer holds the follower on them live, whatever the
    /// cam does. The relation probe's table needs a cam that turns about one
    /// fixed axis against the follower's base; a cam left free in its plane
    /// (Oscar's cam-follower2, 2026-09-15) has three inputs and no table
    /// holds it. The contact needs nothing moved: the faces are read off the
    /// model as they stand, so it also serves when the DOF probe is off.
    /// </summary>
    internal static class CamContact
    {
        private const string NotRigged =
            "A cam-follower mate rides this pair; the cam relation is not rigged.";

        /// <summary>Gives every cam-follower mate not in alreadyModelled a
        /// cam coupling on the follower's mount joint, or a reason in
        /// unread. Returns the mates it modelled, by feature name.</summary>
        public static List<string> Resolve(
            RigidGroupingResult grouping, MateGraph graph, List<RigJoint> joints,
            ICollection<string> alreadyModelled, IDictionary<string, string> unread,
            Action<string> log)
        {
            var modelled = new List<string>();
            log = log ?? delegate { };
            unread = unread ?? new Dictionary<string, string>();
            if (grouping == null || graph == null || joints == null) return modelled;

            foreach (var mate in graph.Mates)
            {
                if (!MateFacts.Is(mate, "CAMFOLLOWER") || mate.Suppressed) continue;
                string name = mate.FeatureName ?? "?";
                if (alreadyModelled != null && alreadyModelled.Contains(name)) continue;

                string reason;
                RigJoint driven;
                var coupling = Build(grouping, mate, joints, out driven, out reason);
                if (coupling == null)
                {
                    // The probe's reason (why no table) and this one (why no
                    // contact) are both true; the user gets both.
                    string earlier;
                    unread[name] = unread.TryGetValue(name, out earlier) && !string.IsNullOrEmpty(earlier)
                        ? earlier + ", and " + reason
                        : reason;
                    log("cam contact " + name + ": " + reason);
                    continue;
                }

                driven.Coupling = coupling;
                driven.SourceMates.Add(new SourceMate { SwFeature = mate.FeatureName, Type = mate.TypeName });
                driven.Confidence = "high";
                driven.Notes = Strip(driven.Notes, NotRigged);
                driven.Notes = Append(driven.Notes,
                    "Cam contact read off the model: the " + coupling.FollowerKind
                    + " follower rides the cam path's " + coupling.CamSurfaceTriangles.Length
                    + " triangle(s) in the consumer, driven by " + coupling.DriverJoint + ".");
                unread.Remove(name);
                modelled.Add(name);
                log("cam contact " + name + ": " + driven.Id + " (" + coupling.FollowerKind
                    + ") rides " + coupling.DriverJoint + "'s cam, "
                    + coupling.CamSurfaceTriangles.Length + " triangle(s)");
            }
            return modelled;
        }

        private static JointCoupling Build(
            RigidGroupingResult grouping, GraphMate mate, List<RigJoint> joints,
            out RigJoint driven, out string reason)
        {
            driven = null;
            reason = null;
            if (mate.CamSurfacePoints == null || mate.CamSurfaceTriangles == null
                || mate.CamSurfaceTriangles.Length == 0 || mate.CamComponentId == null)
            {
                reason = "the cam path's faces could not be read";
                return null;
            }
            string camGroup;
            if (!grouping.ComponentGroup.TryGetValue(mate.CamComponentId, out camGroup))
            {
                reason = "the cam's component is in no rigid group";
                return null;
            }
            GraphMateEntity follower = null;
            foreach (var e in mate.Entities)
            {
                if (e.ComponentId == null || e.ComponentId == mate.CamComponentId) continue;
                follower = e;
                break;
            }
            string followerGroup = null;
            if (follower == null || !grouping.ComponentGroup.TryGetValue(follower.ComponentId, out followerGroup))
            {
                reason = "no follower entity on another component";
                return null;
            }
            if (followerGroup == camGroup)
            {
                reason = "the cam and the follower are one rigid body";
                return null;
            }

            var edge = Between(joints, camGroup, followerGroup);
            driven = JointClassifier.FindMountJoint(
                joints, followerGroup, new[] { JointType.Prismatic }, edge);
            if (driven == null || driven.Type != JointType.Prismatic
                || driven.Axis == null || driven.Origin == null)
            {
                reason = "the follower's joint (" + Describe(driven)
                    + ") does not slide, and the contact reads a sliding follower";
                driven = null;
                return null;
            }
            if (driven.Coupling != null)
            {
                reason = driven.Id + " already carries a " + driven.Coupling.Kind + " coupling";
                return null;
            }
            var driver = JointClassifier.FindMountJoint(
                joints, camGroup,
                new[] { JointType.Revolute, JointType.Planar, JointType.Cylindrical, JointType.Prismatic },
                edge);
            if (driver == null || ReferenceEquals(driver, driven)
                || driver.ParentGroup == followerGroup || driver.ChildGroup == followerGroup)
            {
                reason = "no joint mounts the cam apart from the follower";
                return null;
            }
            if (driver.Axis == null || driver.Origin == null)
            {
                reason = "the cam's joint (" + Describe(driver) + ") has no axis to turn about";
                return null;
            }
            if (follower.Point == null)
            {
                reason = "the follower entity has no point";
                return null;
            }

            var c = new JointCoupling
            {
                Kind = "cam",
                DriverJoint = driver.Id,
                CamAxis = (double[])driver.Axis.Clone(),
                CamOrigin = (double[])driver.Origin.Clone(),
                CamSurfacePoints = mate.CamSurfacePoints,
                CamSurfaceTriangles = mate.CamSurfaceTriangles,
                FollowerPoint = (double[])follower.Point.Clone(),
            };
            switch (follower.EntityTypeName)
            {
                case "vertex":
                case "point":
                    c.FollowerKind = "vertex";
                    break;
                case "cylinder":
                case "sphere":
                    if (!(follower.Radius > 0))
                    {
                        reason = "the roller has no radius";
                        return null;
                    }
                    c.FollowerKind = "roller";
                    c.FollowerRadius = follower.Radius;
                    c.FollowerAxis = follower.Direction == null ? null : (double[])follower.Direction.Clone();
                    break;
                case "plane":
                    if (follower.Direction == null)
                    {
                        reason = "the flat follower has no normal";
                        return null;
                    }
                    c.FollowerKind = "flat";
                    c.FollowerNormal = (double[])follower.Direction.Clone();
                    break;
                default:
                    reason = "the follower is a " + (follower.EntityTypeName ?? "?")
                        + ", and the contact reads a vertex, a cylinder, a sphere or a plane";
                    return null;
            }
            return c;
        }

        private static RigJoint Between(List<RigJoint> joints, string a, string b)
        {
            foreach (var j in joints)
                if ((j.ParentGroup == a && j.ChildGroup == b) || (j.ParentGroup == b && j.ChildGroup == a))
                    return j;
            return null;
        }

        private static string Describe(RigJoint j)
        {
            return j == null ? "none" : j.Id + " " + j.Type;
        }

        private static string Strip(string notes, string sentence)
        {
            if (string.IsNullOrEmpty(notes)) return notes;
            string s = notes.Replace(sentence, "").Replace("  ", " ").Trim();
            return s.Length == 0 ? null : s;
        }

        private static string Append(string notes, string sentence)
        {
            return string.IsNullOrEmpty(notes) ? sentence : notes + " " + sentence;
        }
    }
}
