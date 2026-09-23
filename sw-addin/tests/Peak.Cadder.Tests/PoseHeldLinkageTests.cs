using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;
using static Peak.Cadder.Tests.FixtureBuilder;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// At a cam's dwell the follower cannot move to first order, so
    /// SolidWorks calls it fully defined. A body whose only motion comes
    /// through the follower (a pushrod, a rocker on the frame) cannot move
    /// either at that pose, and reads fully defined too. The status passes
    /// must keep off all of them, or the linkage is welded to the ground and
    /// the cam drives nothing.
    /// </summary>
    public class PoseHeldLinkageTests
    {
        /// <summary>A valve train at a dwell: the cam on a hinge, a lifter
        /// on a slide, a pushrod with a ball at each end, and a rocker
        /// hinged to the frame. The lifter, the pushrod and the rocker all
        /// read fully defined.</summary>
        private static List<GraphComponent> ValveTrain()
        {
            return new List<GraphComponent>
            {
                Comp("c001", "frame", isFixed: true),
                Comp("c002", "cam"),
                StillWithLimitsOut(Comp("c003", "lifter")),
                StillWithLimitsOut(Comp("c004", "pushrod")),
                StillWithLimitsOut(Comp("c005", "rocker")),
            };
        }

        private static List<GraphMate> ValveTrainMates()
        {
            return new List<GraphMate>
            {
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident2", "c001", "c003", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident3", "c001", "c003", Y, P(0, 0, 0)),
                Mate("CamMateTangent1", "swMateCAMFOLLOWER",
                    Cylinder("c002", Z, P(0, 0, 0), 0.0762),
                    Cylinder("c003", Z, P(0.1137, 0, 0), 0.0375)),
                Mate("Concentric2", "swMateCONCENTRIC",
                    SphereEnt("c003", P(0.2, 0, 0)), SphereEnt("c004", P(0.2, 0, 0))),
                Mate("Concentric3", "swMateCONCENTRIC",
                    SphereEnt("c004", P(0.4, 0, 0)), SphereEnt("c005", P(0.4, 0, 0))),
                Concentric("Concentric4", "c001", "c005", Z, P(0.4, 0.1, 0)),
                CoincidentPlanes("Coincident4", "c001", "c005", Z, P(0, 0, 0.01)),
            };
        }

        [Fact]
        public void ALinkageDrivenThroughACamFollowerIsNotWeldedOnItsStatus()
        {
            var graph = Graph(ValveTrain().ToArray(), ValveTrainMates().ToArray());

            var result = RigidGrouper.Group(graph);

            string ground = result.ComponentGroup["c001"];
            Assert.NotEqual(ground, result.ComponentGroup["c003"]);
            Assert.NotEqual(ground, result.ComponentGroup["c004"]);
            Assert.NotEqual(ground, result.ComponentGroup["c005"]);
            Assert.Empty(result.StatusWelds);
            Assert.Contains("lifter-1", result.PoseHeldSkips);
            Assert.Contains("pushrod-1", result.PoseHeldSkips);
            Assert.Contains("rocker-1", result.PoseHeldSkips);
        }

        /// <summary>The ground stops the search: a part that reaches the
        /// follower only through the frame does not move with it, and its
        /// status still welds it.</summary>
        [Fact]
        public void APartJoinedToTheFollowerOnlyThroughTheGroundIsStillWelded()
        {
            var comps = ValveTrain();
            comps.Add(StillWithLimitsOut(Comp("c006", "bracket")));
            var mates = ValveTrainMates();
            // One face on the frame: a plane joint to the mates, still to
            // SolidWorks (the rest of what holds it is a countersink).
            mates.Add(CoincidentPlanes("Coincident5", "c001", "c006", X, P(-0.1, 0, 0)));
            var graph = Graph(comps.ToArray(), mates.ToArray());

            var result = RigidGrouper.Group(graph);

            Assert.Equal(result.ComponentGroup["c001"], result.ComponentGroup["c006"]);
            Assert.Equal(new[] { "c006" }, result.StatusWeldIds);
        }

        /// <summary>The same inside a flexible subassembly: the rocker
        /// reads fully defined in the sub's own document, and welding it to
        /// the sub's frame would freeze the train the same way.</summary>
        [Fact]
        public void ALinkageDrivenThroughACamFollowerIsNotWeldedToItsSubassembly()
        {
            var comps = ValveTrain();
            comps.Add(Flexible(Comp("c006", "rocker sub")));
            comps.Add(InSubFixed(Inside(Comp("c007", "rocker shaft"), "c006")));
            var rocker = comps.Find(c => c.Id == "c005");
            rocker.ParentId = "c006";
            rocker.StatusFree = 0;
            rocker.SubStatusFree = 3;
            var mates = ValveTrainMates();
            mates.RemoveAll(m => m.FeatureName == "Concentric4" || m.FeatureName == "Coincident4");
            // The sub's shaft is bolted to the frame, and the rocker turns
            // on the shaft.
            mates.Add(Concentric("Concentric5", "c001", "c007", Z, P(0.4, 0.1, 0)));
            mates.Add(Concentric("Concentric6", "c001", "c007", Z, P(0.5, 0.1, 0)));
            mates.Add(CoincidentPlanes("Coincident5", "c001", "c007", Z, P(0, 0, 0.02)));
            mates.Add(Concentric("Concentric4", "c007", "c005", Z, P(0.4, 0.1, 0)));
            mates.Add(CoincidentPlanes("Coincident4", "c007", "c005", Z, P(0, 0, 0.01)));
            var graph = Graph(comps.ToArray(), mates.ToArray());

            var result = RigidGrouper.Group(graph);

            Assert.NotEqual(result.ComponentGroup["c007"], result.ComponentGroup["c005"]);
            Assert.Empty(result.SubStatusWelds);
            Assert.Contains("rocker-1", result.PoseHeldSkips);
        }

        // ── The cam side ────────────────────────────────────────────────────

        /// <summary>A cam plate bolted to the frame, and a lifter on a slide
        /// that rides it. One face on the frame stands in for a fastening
        /// the mates cannot read as rigid (a countersink, a width), and the
        /// same for the bolt on the plate. All three read fully defined.
        /// </summary>
        private static MateGraph StationaryCam(string camComponent)
        {
            var cam = Mate("CamMateTangent1", "swMateCAMFOLLOWER",
                Cylinder("c002", Z, P(0, 0, 0), 0.0762),
                Cylinder("c003", Z, P(0.1137, 0, 0), 0.0375));
            cam.CamComponentId = camComponent;
            return Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    StillWithLimitsOut(Comp("c002", "cam plate")),
                    StillWithLimitsOut(Comp("c003", "lifter")),
                    StillWithLimitsOut(Comp("c004", "bolt")),
                },
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident2", "c001", "c003", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident3", "c001", "c003", Y, P(0, 0, 0)),
                CoincidentPlanes("Coincident4", "c002", "c004", X, P(-0.05, 0, 0)),
                cam);
        }

        /// <summary>At a dwell the follower cannot move, but the cam can:
        /// its profile does not change the contact as it turns. So a cam
        /// that reads fully defined is still, and its status welds it and
        /// what is bolted to it. Only the follower is kept off.</summary>
        [Fact]
        public void AStillCamIsWeldedOnItsStatusAndItsFollowerIsNot()
        {
            var result = RigidGrouper.Group(StationaryCam("c002"));

            string ground = result.ComponentGroup["c001"];
            Assert.Equal(ground, result.ComponentGroup["c002"]);
            Assert.Equal(ground, result.ComponentGroup["c004"]);
            Assert.NotEqual(ground, result.ComponentGroup["c003"]);
            Assert.Equal(new[] { "c002", "c004" }, result.StatusWeldIds);
            Assert.Equal(new[] { "lifter-1" }, result.PoseHeldSkips);
        }

        /// <summary>When the cam's faces were not read, nothing says which
        /// side is the cam, so both sides are kept off.</summary>
        [Fact]
        public void WithTheCamSideUnknownBothSidesAreKeptOff()
        {
            var result = RigidGrouper.Group(StationaryCam(null));

            Assert.Empty(result.StatusWeldIds);
            Assert.Contains("cam plate-1", result.PoseHeldSkips);
            Assert.Contains("lifter-1", result.PoseHeldSkips);
        }

        /// <summary>A path rail is the cam of a path mate: the part whose
        /// point rides the rail is kept off, and the rail is welded.</summary>
        [Fact]
        public void AStillPathRailIsWeldedOnItsStatusAndItsRiderIsNot()
        {
            var path = Mate("Path1", "swMatePATH",
                EdgeEnt("c002", X, P(0, 0, 0.02)),
                VertexEnt("c003", P(0.02, 0, 0.02)));
            path.PathPoints = new[] { P(0, 0, 0.02), P(0.05, 0, 0.02), P(0.1, 0.02, 0.02) };
            var graph = Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    StillWithLimitsOut(Comp("c002", "rail")),
                    StillWithLimitsOut(Comp("c003", "shuttle")),
                },
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)),
                path);

            var result = RigidGrouper.Group(graph);

            Assert.Equal(new[] { "c002" }, result.StatusWeldIds);
            Assert.Equal(new[] { "shuttle-1" }, result.PoseHeldSkips);
            Assert.NotEqual(result.ComponentGroup["c001"], result.ComponentGroup["c003"]);
        }
    }
}
