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
    }
}
