using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;
using static Peak.Cadder.Tests.FixtureBuilder;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// Where a gear, rack or coupler mate puts its coupling. The mate is not
    /// a joint: it relates the joints that mount its two bodies, and the
    /// classifier has to find those joints and pick which one drives.
    /// </summary>
    public class CouplingAttachTests
    {
        private static ClassificationResult Run(MateGraph graph)
        {
            return JointClassifier.Classify(graph, RigidGrouper.Group(graph));
        }

        private static GraphMate Gear(string feature, GraphMateEntity first, GraphMateEntity second)
        {
            var m = Mate(feature, "swMateGEAR", first, second);
            m.CouplingNumerator = first.Radius;
            m.CouplingDenominator = second.Radius;
            return m;
        }

        private static RigJoint ByChild(ClassificationResult result, MateGraph graph, string compId)
        {
            var grouping = RigidGrouper.Group(graph);
            string group = grouping.ComponentGroup[compId];
            foreach (var j in result.Joints)
                if (j.ChildGroup == group) return j;
            return null;
        }

        private static RigJoint Find(ClassificationResult result, string id)
        {
            foreach (var j in result.Joints)
                if (j.Id == id) return j;
            return null;
        }

        /// <summary>Every driver chain must end. Blender refuses the whole
        /// rig when two joints drive each other ("rig dependency cycle").</summary>
        private static void AssertNoCouplingCycle(ClassificationResult result)
        {
            foreach (var start in result.Joints)
            {
                var seen = new HashSet<string>();
                for (var j = start; j != null && j.Coupling != null
                     && j.Coupling.DriverJoint != null;
                     j = Find(result, j.Coupling.DriverJoint))
                    Assert.True(seen.Add(j.Id), "coupling cycle through " + j.Id);
            }
        }

        private static bool Unresolved(ClassificationResult result, string feature)
        {
            foreach (var w in result.Warnings)
                if (w.Code == "COUPLING_UNRESOLVED" && w.Message.Contains(feature))
                    return true;
            return false;
        }

        /// <summary>
        /// A planetary set with its ring gear fixed. The sun and the carrier
        /// turn on the ground, the planet turns on the carrier. The ring is
        /// part of the ground, and the ground is the parent of every joint
        /// on it, never a child. A search for "the joint that mounts the
        /// ring" picked the first ground joint in the list, which is the
        /// sun's. The planet-ring mate then made the planet drive the sun,
        /// the sun-planet mate made the sun drive the planet, and Blender
        /// refused the rig.
        /// </summary>
        [Fact]
        public void AFixedRingGearDrivesNoUnrelatedJoint()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "ring", isFixed: true),
                    Comp("c002", "sun"),
                    Comp("c003", "carrier"),
                    Comp("c004", "planet"),
                },
                Concentric("ConcentricSun", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("CoincidentSun", "c001", "c002", Z, P(0, 0, 0)),
                Concentric("ConcentricCarrier", "c001", "c003", Z, P(0, 0, 0.01)),
                CoincidentPlanes("CoincidentCarrier", "c001", "c003", Z, P(0, 0, 0.01)),
                Concentric("ConcentricPlanet", "c003", "c004", Z, P(0.03, 0, 0)),
                CoincidentPlanes("CoincidentPlanet", "c003", "c004", Z, P(0.03, 0, 0.005)),
                Gear("GearPR",
                    Cylinder("c004", Z, P(0.03, 0, 0), radius: 0.01),
                    Cylinder("c001", Z, P(0, 0, 0), radius: 0.04)),
                Gear("GearSP",
                    Cylinder("c002", Z, P(0, 0, 0), radius: 0.02),
                    Cylinder("c004", Z, P(0.03, 0, 0), radius: 0.01)));

            var result = Run(graph);

            AssertNoCouplingCycle(result);
            var sun = ByChild(result, graph, "c002");
            var planet = ByChild(result, graph, "c004");
            Assert.NotNull(sun);
            Assert.NotNull(planet);
            // The sun is an input. Only the sun-planet mesh couples the two.
            Assert.Null(sun.Coupling);
            Assert.NotNull(planet.Coupling);
            Assert.Equal(sun.Id, planet.Coupling.DriverJoint);
            Assert.True(Unresolved(result, "GearPR"),
                "the mesh with the fixed ring must be reported, not dropped in silence");
        }

        /// <summary>
        /// A gear on a swinging arm meshes a gear fixed to the frame. A door
        /// hinged on the frame about another axis comes first in the list.
        /// The fixed gear has no joint of its own, and the door hinge has
        /// nothing to do with the gears, so it must not be driven by them.
        /// </summary>
        [Fact]
        public void AGearOnAFixedGearLeavesAnUnrelatedHingeAlone()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    Comp("c002", "door"),
                    Comp("c003", "arm"),
                    Comp("c004", "arm gear"),
                },
                Concentric("ConcentricDoor", "c001", "c002", X, P(0, 0.2, 0)),
                CoincidentPlanes("CoincidentDoor", "c001", "c002", X, P(0.1, 0.2, 0)),
                Concentric("ConcentricArm", "c001", "c003", Z, P(0, 0, 0)),
                CoincidentPlanes("CoincidentArm", "c001", "c003", Z, P(0, 0, 0.01)),
                Concentric("ConcentricGear", "c003", "c004", Z, P(0.05, 0, 0)),
                CoincidentPlanes("CoincidentGear", "c003", "c004", Z, P(0.05, 0, 0.02)),
                Gear("GearFixed",
                    Cylinder("c004", Z, P(0.05, 0, 0), radius: 0.01),
                    Cylinder("c001", Z, P(0, 0, 0), radius: 0.04)));

            var result = Run(graph);

            foreach (var j in result.Joints)
                Assert.Null(j.Coupling);
            Assert.True(Unresolved(result, "GearFixed"));
        }

        /// <summary>
        /// Three gears that all mesh each other. Each mate on its own is a
        /// good coupling, but the last one read closes a ring of drivers:
        /// A drives B, B drives C, and C would drive A. That mate is
        /// reported and left out. Which mate is last depends on the edge
        /// order, so the test counts them.
        /// </summary>
        [Fact]
        public void AGearMateThatClosesARingOfDriversIsLeftOut()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    Comp("c002", "gear a"),
                    Comp("c003", "gear b"),
                    Comp("c004", "gear c"),
                },
                Concentric("ConcentricA", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("CoincidentA", "c001", "c002", Z, P(0, 0, 0)),
                Concentric("ConcentricB", "c001", "c003", Z, P(0.04, 0, 0)),
                CoincidentPlanes("CoincidentB", "c001", "c003", Z, P(0.04, 0, 0)),
                Concentric("ConcentricC", "c001", "c004", Z, P(0.02, 0.0346, 0)),
                CoincidentPlanes("CoincidentC", "c001", "c004", Z, P(0.02, 0.0346, 0)),
                Gear("GearAB",
                    Cylinder("c002", Z, P(0, 0, 0), radius: 0.02),
                    Cylinder("c003", Z, P(0.04, 0, 0), radius: 0.02)),
                Gear("GearBC",
                    Cylinder("c003", Z, P(0.04, 0, 0), radius: 0.02),
                    Cylinder("c004", Z, P(0.02, 0.0346, 0), radius: 0.02)),
                Gear("GearCA",
                    Cylinder("c004", Z, P(0.02, 0.0346, 0), radius: 0.02),
                    Cylinder("c002", Z, P(0, 0, 0), radius: 0.02)));

            var result = Run(graph);

            AssertNoCouplingCycle(result);
            int coupled = 0;
            foreach (var j in result.Joints)
                if (j.Coupling != null) coupled++;
            Assert.Equal(2, coupled);
            int unresolved = 0;
            foreach (var name in new[] { "GearAB", "GearBC", "GearCA" })
                if (Unresolved(result, name)) unresolved++;
            Assert.Equal(1, unresolved);
        }

        // ── A joint that is already driven ──────────────────────────────────

        private static RigJoint CoupledBy(ClassificationResult result, string feature)
        {
            foreach (var j in result.Joints)
            {
                if (j.Coupling == null) continue;
                foreach (var sm in j.SourceMates)
                    if (sm.SwFeature == feature) return j;
            }
            return null;
        }

        /// <summary>
        /// A train of three gears with an idler in the middle. Both mates
        /// name the idler second, and the second entity's side is the one
        /// driven, so the second mate wrote over the first: the idler
        /// followed the third gear, and the first gear turned on its own
        /// with nothing to say its mesh was dropped. The later mate must
        /// run the other way round instead, at the inverse ratio.
        /// </summary>
        [Fact]
        public void AnIdlerDrivenTwiceKeepsBothMeshes()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    Comp("c002", "gear a"),
                    Comp("c003", "idler"),
                    Comp("c004", "gear c"),
                },
                Concentric("ConcentricA", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("CoincidentA", "c001", "c002", Z, P(0, 0, 0)),
                Concentric("ConcentricB", "c001", "c003", Z, P(0.03, 0, 0)),
                CoincidentPlanes("CoincidentB", "c001", "c003", Z, P(0.03, 0, 0)),
                Concentric("ConcentricC", "c001", "c004", Z, P(0.07, 0, 0)),
                CoincidentPlanes("CoincidentC", "c001", "c004", Z, P(0.07, 0, 0)),
                Gear("GearAB",
                    Cylinder("c002", Z, P(0, 0, 0), radius: 0.02),
                    Cylinder("c003", Z, P(0.03, 0, 0), radius: 0.01)),
                Gear("GearCB",
                    Cylinder("c004", Z, P(0.07, 0, 0), radius: 0.03),
                    Cylinder("c003", Z, P(0.03, 0, 0), radius: 0.01)));

            var result = Run(graph);

            AssertNoCouplingCycle(result);
            Assert.DoesNotContain(result.Warnings, w => w.Code == "COUPLING_UNRESOLVED");
            var ab = CoupledBy(result, "GearAB");
            var cb = CoupledBy(result, "GearCB");
            Assert.NotNull(ab);
            Assert.NotNull(cb);
            Assert.NotSame(ab, cb);

            // GearCB states angle(C) : angle(idler) = 0.03 : 0.01. The gear
            // C side follows at 3, the idler side at 1/3.
            var gearC = ByChild(result, graph, "c004");
            double expected = ReferenceEquals(cb, gearC) ? 3.0 : 1.0 / 3.0;
            Assert.Equal(expected, System.Math.Abs(cb.Coupling.Ratio.Value), 9);
        }

        /// <summary>
        /// A gear that drives a lead screw. The screw joint carries its own
        /// lead as a coupling, and the gear mate wrote over it: the screw
        /// lost its lead. The screw now drives the gear instead.
        /// </summary>
        [Fact]
        public void AGearOnALeadScrewKeepsTheLead()
        {
            var screw = Mate("Screw1", "swMateSCREW",
                Cylinder("c001", Z, P(0, 0, 0)),
                Cylinder("c002", Z, P(0, 0, 0)));
            screw.LeadMPerRev = 0.002;
            var graph = Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    Comp("c002", "screw"),
                    Comp("c003", "gear"),
                },
                Concentric("ConcentricScrew", "c001", "c002", Z, P(0, 0, 0)),
                screw,
                Concentric("ConcentricGear", "c001", "c003", Z, P(0.03, 0, 0)),
                CoincidentPlanes("CoincidentGear", "c001", "c003", Z, P(0.03, 0, 0)),
                Gear("GearDrive",
                    Cylinder("c003", Z, P(0.03, 0, 0), radius: 0.02),
                    Cylinder("c002", Z, P(0, 0, 0), radius: 0.01)));

            var result = Run(graph);

            var lead = ByChild(result, graph, "c002");
            var gear = ByChild(result, graph, "c003");
            Assert.Equal(JointType.Screw, lead.Type);
            Assert.NotNull(lead.Coupling);
            Assert.Equal("screw", lead.Coupling.Kind);
            Assert.Equal(0.002, lead.Coupling.LeadMPerRev.Value, 12);
            Assert.NotNull(gear.Coupling);
            Assert.Equal("gear", gear.Coupling.Kind);
            Assert.Equal(lead.Id, gear.Coupling.DriverJoint);
        }

        /// <summary>
        /// Two pinions on one rack. A rack coupling cannot run the other
        /// way (it is metres of rack per radian of pinion), so the second
        /// mate is reported and the first one stays.
        /// </summary>
        [Fact]
        public void ASecondPinionOnOneRackIsReportedNotWrittenOver()
        {
            var first = Mate("RackPinionMate1", "swMateRACKPINION",
                EdgeEnt("c004", X, P(0, 0.01, 0)),
                Cylinder("c002", Z, P(0, 0, 0), radius: 0.01));
            first.MetersPerRadian = 0.01;
            var second = Mate("RackPinionMate2", "swMateRACKPINION",
                EdgeEnt("c004", X, P(0.1, 0.01, 0)),
                Cylinder("c003", Z, P(0.1, 0, 0), radius: 0.01));
            second.MetersPerRadian = 0.01;
            var graph = Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    Comp("c002", "pinion one"),
                    Comp("c003", "pinion two"),
                    Comp("c004", "rack"),
                },
                Concentric("ConcentricOne", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("CoincidentOne", "c001", "c002", Z, P(0, 0, 0)),
                Concentric("ConcentricTwo", "c001", "c003", Z, P(0.1, 0, 0)),
                CoincidentPlanes("CoincidentTwo", "c001", "c003", Z, P(0.1, 0, 0)),
                CoincidentPlanes("CoincidentRackSide", "c001", "c004", Y, P(0, 0.01, 0)),
                CoincidentPlanes("CoincidentRackFace", "c001", "c004", Z, P(0, 0, 0)),
                first,
                second);

            var result = Run(graph);

            var one = ByChild(result, graph, "c002");
            var two = ByChild(result, graph, "c003");
            var rack = ByChild(result, graph, "c004");
            Assert.NotNull(rack.Coupling);
            Assert.Equal(one.Id, rack.Coupling.DriverJoint);
            Assert.Null(two.Coupling);
            Assert.True(Unresolved(result, "RackPinionMate2"));
        }

        // ── Rack and pinion ─────────────────────────────────────────────────

        private static GraphMateEntity PinionEntity(string kind)
        {
            switch (kind)
            {
                case "axis":
                    return AxisEnt("c002", Z, P(0, 0, 0));
                case "circle":
                    var edge = EdgeEnt("c002", Z, P(0, 0, 0));
                    edge.Radius = 0.01;
                    return edge;
                default:
                    return Cylinder("c002", Z, P(0, 0, 0), radius: 0.01);
            }
        }

        private static MateGraph RackOnAFrame(GraphMateEntity rack, GraphMateEntity pinion)
        {
            var mate = Mate("RackPinionMate1", "swMateRACKPINION", rack, pinion);
            mate.MetersPerRadian = 0.01;
            return Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    Comp("c002", "pinion"),
                    Comp("c003", "rack"),
                },
                Concentric("ConcentricPinion", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("CoincidentPinion", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("CoincidentRackSide", "c001", "c003", Y, P(0, 0.01, 0)),
                CoincidentPlanes("CoincidentRackFace", "c001", "c003", Z, P(0, 0, 0)),
                mate);
        }

        /// <summary>
        /// SolidWorks takes a datum axis or a circular edge for the pinion
        /// as well as its face. Only a cylinder was known as the pinion, so
        /// with an axis or an edge the rack became the driver: the pinion's
        /// hinge carried a rack coupling driven by the rack, and a free
        /// rack stayed free. The reader lists the rack first.
        /// </summary>
        [Theory]
        [InlineData("cylinder")]
        [InlineData("axis")]
        [InlineData("circle")]
        public void ThePinionDrivesTheRackWhateverPicksIt(string pinionKind)
        {
            var graph = RackOnAFrame(
                EdgeEnt("c003", X, P(0, 0.01, 0)), PinionEntity(pinionKind));

            var result = Run(graph);

            var pinion = ByChild(result, graph, "c002");
            var rack = ByChild(result, graph, "c003");
            Assert.Equal(JointType.Revolute, pinion.Type);
            Assert.Equal(JointType.Prismatic, rack.Type);
            Assert.Null(pinion.Coupling);
            Assert.NotNull(rack.Coupling);
            Assert.Equal("rack_pinion", rack.Coupling.Kind);
            Assert.Equal(pinion.Id, rack.Coupling.DriverJoint);
        }

        /// <summary>A rack held by nothing but a parallel mate gets its
        /// slide from the rack-pinion mate, also when a datum axis picks the
        /// pinion.</summary>
        [Fact]
        public void AFreeRackOnAnAxisPickedPinionSlides()
        {
            var mate = Mate("RackPinionMate1", "swMateRACKPINION",
                EdgeEnt("c003", X, P(0, 0.01, 0)), AxisEnt("c002", Z, P(0, 0, 0)));
            mate.MetersPerRadian = 0.01;
            var graph = Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    Comp("c002", "pinion"),
                    Comp("c003", "rack"),
                },
                Concentric("ConcentricPinion", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("CoincidentPinion", "c001", "c002", Z, P(0, 0, 0)),
                ParallelPlanes("ParallelRack", "c001", "c003", Z, P(0, 0, 0)),
                mate);

            var result = Run(graph);

            var pinion = ByChild(result, graph, "c002");
            var rack = ByChild(result, graph, "c003");
            Assert.Equal(JointType.Prismatic, rack.Type);
            Assert.Equal(1.0, System.Math.Abs(rack.Axis[0]), 9);
            Assert.NotNull(rack.Coupling);
            Assert.Equal(pinion.Id, rack.Coupling.DriverJoint);
            Assert.DoesNotContain(result.Warnings,
                w => w.Code == "UNDER_DEFINED" && w.Joints.Contains(rack.Id));
        }

        /// <summary>A pinion picked by a face or a circular edge in the
        /// first slot means the pair came the other way round.</summary>
        [Theory]
        [InlineData("cylinder")]
        [InlineData("circle")]
        public void APinionListedFirstStillDrives(string pinionKind)
        {
            var graph = RackOnAFrame(
                PinionEntity(pinionKind), EdgeEnt("c003", X, P(0, 0.01, 0)));

            var result = Run(graph);

            var pinion = ByChild(result, graph, "c002");
            var rack = ByChild(result, graph, "c003");
            Assert.NotNull(rack.Coupling);
            Assert.Equal(pinion.Id, rack.Coupling.DriverJoint);
        }
    }
}
