using Peak.SwToBlender.Core;
using Peak.SwToBlender.Core.Model;
using Xunit;
using static Peak.SwToBlender.Tests.FixtureBuilder;

namespace Peak.SwToBlender.Tests
{
    public class RigidGrouperTests
    {
        /// <summary>Two parallel non-collinear concentrics kill every rotation
        /// and the coincident kills the last slide: zero DOF, one group, no
        /// edge for the classifier to ever see.</summary>
        [Fact]
        public void BoltedPairMergesIntoOneGroup()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "base", isFixed: true),
                    Comp("c002", "flange"),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)),
                Concentric("Concentric2", "c001", "c002", Z, P(0.05, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0.01)));

            var result = RigidGrouper.Group(graph);

            Assert.Single(result.Groups);
            Assert.Equal("g000", result.Groups[0].Id);
            Assert.True(result.Groups[0].Grounded);
            Assert.Equal(new[] { "c001", "c002" }, result.Groups[0].Components);
            Assert.Empty(result.Edges);
        }

        /// <summary>The live hinge3 case (2026-08-22): pin concentric + face
        /// coincident + a side-face coincident whose normal is off the axis.
        /// The side face kills the spin (zero DOF) but the old hand-mirrored
        /// zero-DOF patterns had no row for it, so the pair stayed separate
        /// and the "fully defined" hinge rotated freely in Blender.</summary>
        [Fact]
        public void FullyDefinedHingeMergesIntoOneGroup()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "hinge base", isFixed: true),
                    Comp("c002", "hinge leaf"),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0.01)),
                CoincidentPlanes("Coincident2", "c001", "c002", X, P(0.02, 0, 0)));

            var result = RigidGrouper.Group(graph);

            Assert.Single(result.Groups);
            Assert.Equal(new[] { "c001", "c002" }, result.Groups[0].Components);
            Assert.Empty(result.Edges);
        }

        /// <summary>Three coincident planes with independent normals pin all
        /// six DOF: the fully-defined slider variant of hinge3.</summary>
        [Fact]
        public void ThreeIndependentPlanesMergeIntoOneGroup()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "rail", isFixed: true),
                    Comp("c002", "block"),
                },
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident2", "c001", "c002", Y, P(0, 0, 0)),
                CoincidentPlanes("Coincident3", "c001", "c002", X, P(0.02, 0, 0)));

            var result = RigidGrouper.Group(graph);

            Assert.Single(result.Groups);
            Assert.Empty(result.Edges);
        }

        /// <summary>Pinned and clocked: a fixed angle mate measured off the
        /// pin axis stops the spin, and the face coincident already stopped
        /// the slide, rigid.</summary>
        [Fact]
        public void ClockedPinMergesIntoOneGroup()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "base", isFixed: true),
                    Comp("c002", "bracket"),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0.01)),
                Mate("Angle1", "swMateANGLE",
                    PlaneEnt("c001", X, P(0, 0, 0)), PlaneEnt("c002", Y, P(0, 0, 0))));

            var result = RigidGrouper.Group(graph);

            Assert.Single(result.Groups);
            Assert.Empty(result.Edges);
        }

        /// <summary>Live corpus 07 (2026-08-22): the baseplate's coincidents
        /// grab the hinge sub's fixed base while its distance mate grabs the
        /// sub's own reference plane. Each component pair is non-rigid alone
        /// (two coincidents = prismatic, one distance = planar), but with the
        /// fixed-in-sub merge the union is welded solid: rigidity that only
        /// exists at the group level. The pair-level pass missed it and the
        /// classifier hit a zero-DOF edge it refuses to output.</summary>
        [Fact]
        public void GroupLevelRigidityMerges()
        {
            var subFrame = Comp("c001", "hinge");
            var fixedBase = Comp("c002", "hinge base");
            fixedBase.ParentId = "c001";
            fixedBase.FixedInSubassembly = true;

            var graph = Graph(
                new[]
                {
                    Comp("c003", "baseplate", isFixed: true),
                    subFrame,
                    fixedBase,
                },
                CoincidentPlanes("Coincident1", "c003", "c002", Z, P(0.04, 0.02, 0.01)),
                CoincidentPlanes("Coincident4", "c003", "c002", Y, P(0.01, 0, 0.01)),
                Mate("Distance2", "swMateDISTANCE",
                    PlaneEnt("c001", X, P(0.04, 0.02, 0.02)),
                    PlaneEnt("c003", X, P(0, 0, 0.01))));

            var result = RigidGrouper.Group(graph);

            Assert.Single(result.Groups);
            Assert.True(result.Groups[0].Grounded);
            Assert.Equal(new[] { "c003", "c001", "c002" }, result.Groups[0].Components);
            Assert.Empty(result.Edges);
        }

        /// <summary>The live ball4 variant (2026-08-22): an origin mate with
        /// "align axes" ticked exports as swMateCOORDINATE (the un-aligned one
        /// exports swMateCOINCIDENT: the type IS the align flag, nothing else
        /// in the API carries it). Aligned origins lock all six DOF, so the
        /// pair merges and no joint exists.</summary>
        [Fact]
        public void CoordinateMateMergesThePair()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "socket base", isFixed: true),
                    Comp("c002", "ball stud"),
                },
                Mate("Coincident3", "swMateCOORDINATE",
                    UnknownEnt("c002", P(0, 0, 0.02)), UnknownEnt(null, P(0, 0, 0.02))));

            var result = RigidGrouper.Group(graph);

            Assert.Single(result.Groups);
            Assert.Equal(new[] { "c001", "c002" }, result.Groups[0].Components);
            Assert.Empty(result.Edges);
        }

        /// <summary>The live planar5 variant (2026-08-22): a profile-centre
        /// mate WITH "lock rotation" pins all six DOF on its own. The tick
        /// lives only on the feature data: the raw entities are identical to
        /// the unlocked planar4, which classifies revolute.</summary>
        [Fact]
        public void LockedProfileCentreMergesThePair()
        {
            var pc = Mate("ProfileCenter1", "swMatePROFILECENTER",
                PlaneEnt("c001", Z, P(0, 0, 0.01)),
                PlaneEnt("c002", new double[] { 0, 0, -1 }, P(0, 0, 0.01)));
            pc.LockRotation = true;

            var graph = Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    Comp("c002", "puck"),
                },
                pc);

            var result = RigidGrouper.Group(graph);

            Assert.Single(result.Groups);
            Assert.Equal(new[] { "c001", "c002" }, result.Groups[0].Components);
            Assert.Empty(result.Edges);
        }

        /// <summary>Mate alignment flips the reported directions all the time;
        /// anti-parallel axes are still parallel for the zero-DOF check.</summary>
        [Fact]
        public void BoltedPairMergesWithFlippedSecondAxis()
        {
            var minusZ = new double[] { 0, 0, -1 };
            var graph = Graph(
                new[]
                {
                    Comp("c001", "base", isFixed: true),
                    Comp("c002", "flange"),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)),
                Concentric("Concentric2", "c001", "c002", minusZ, P(0.05, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0.01)));

            var result = RigidGrouper.Group(graph);

            Assert.Single(result.Groups);
            Assert.Empty(result.Edges);
        }

        /// <summary>
        /// Nothing is classified by what a part is CALLED. A bolt held by one
        /// concentric and a face coincident can spin, SolidWorks says so, and
        /// the rig says so too: whatever the file is named and whether or not
        /// it came from the Toolbox.
        ///
        /// There used to be a filter here that folded such a pair together on
        /// a file-name match against screw/bolt/washer/nut/pin/dowel/rivet. It
        /// silently changed KINEMATICS from metadata, and it misfired exactly
        /// where it hurt: "spindle" and "pinion" both contain "pin", and one
        /// concentric plus a face coincident is precisely the shape it welded,
        /// so a shaft became part of its housing with nothing in the manifest
        /// to show for it (Oscar, 2026-08-24, "joints should be classified
        /// ENTIRELY based on kinematics alone").
        /// </summary>
        [Theory]
        [InlineData("M6_socket_screw.sldprt")]
        [InlineData("spindle.sldprt")]
        [InlineData("bracket.sldprt")]
        public void HardwareNamesDoNotMergeAnything(string fileName)
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    Comp("c002", "part", fileName: fileName),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0.01)));

            var result = RigidGrouper.Group(graph);

            Assert.Equal(2, result.Groups.Count);
            var edge = Assert.Single(result.Edges);
            Assert.Equal("g000", edge.GroupA);
            Assert.Equal("g001", edge.GroupB);
        }

        /// <summary>And when the MATES pin it: a second concentric off the
        /// first axis: the same bolt merges. The verdict is kinematic and it
        /// comes from the geometry, which is the whole difference.</summary>
        [Fact]
        public void AGeometricallyPinnedBoltMerges()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    FullyDefined(Comp("c002", "m6", fileName: "M6_socket_screw.sldprt")),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)),
                Concentric("Concentric2", "c001", "c002", Z, P(0.05, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0.01)));

            var result = RigidGrouper.Group(graph);

            Assert.Single(result.Groups);
            Assert.Empty(result.Edges);
        }

        /// <summary>Suppressed components belong to no group, and any mate
        /// that touches one (or is itself suppressed), is inert. The
        /// suppressed LOCK here would have merged the pair if it counted.</summary>
        [Fact]
        public void SuppressedComponentsAndMatesAreExcluded()
        {
            var suppressedLock = Mate("Lock1", "swMateLOCK",
                PlaneEnt("c001", Z, P(0, 0, 0)), PlaneEnt("c002", Z, P(0, 0, 0)));
            suppressedLock.Suppressed = true;

            var graph = Graph(
                new[]
                {
                    Comp("c001", "base", isFixed: true),
                    Comp("c002", "arm"),
                    Comp("c003", "ghost", suppressed: true),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)),
                Concentric("Concentric2", "c001", "c003", X, P(0, 0, 0.05)),
                suppressedLock);

            var result = RigidGrouper.Group(graph);

            Assert.Equal(2, result.Groups.Count);
            Assert.Equal(new[] { "c001" }, result.Groups[0].Components);
            Assert.Equal(new[] { "c002" }, result.Groups[1].Components);
            Assert.False(result.ComponentGroup.ContainsKey("c003"));

            var edge = Assert.Single(result.Edges);
            var mate = Assert.Single(edge.Mates);
            Assert.Equal("Concentric1", mate.FeatureName);
        }

        /// <summary>g000 is the grounded group even when the fixed component
        /// is not first in the component list.</summary>
        [Fact]
        public void GroundedGroupIsG000()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "mover"),
                    Comp("c002", "base", isFixed: true),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)));

            var result = RigidGrouper.Group(graph);

            Assert.Equal(2, result.Groups.Count);
            Assert.Equal("g000", result.Groups[0].Id);
            Assert.True(result.Groups[0].Grounded);
            Assert.Equal(new[] { "c002" }, result.Groups[0].Components);
            Assert.Equal("g001", result.Groups[1].Id);
            Assert.False(result.Groups[1].Grounded);
            Assert.Equal("g000", result.ComponentGroup["c002"]);
            Assert.Equal("g001", result.ComponentGroup["c001"]);

            var edge = Assert.Single(result.Edges);
            Assert.Equal("g000", edge.GroupA);
            Assert.Equal("g001", edge.GroupB);
        }

        /// <summary>Two things fixed to the assembly have no freedom between
        /// them whether or not a mate happens to span them. Live
        /// ClampRig (2026-08-24) arrived with 18 grounded groups:
        /// eighteen separately fixed hose routes, and Blender refused the
        /// manifest, because a joint had landed on one of the later ones and a
        /// bone hierarchy cannot root at two places.</summary>
        [Fact]
        public void EveryFixedComponentSharesOneGround()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "hose a", isFixed: true),
                    Comp("c002", "frame", isFixed: true),
                    Comp("c003", "hose b", isFixed: true),
                    Comp("c004", "lever"),
                },
                Concentric("Concentric1", "c002", "c004", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c002", "c004", Z, P(0, 0, 0.01)));

            var result = RigidGrouper.Group(graph);

            Assert.Equal(2, result.Groups.Count);
            Assert.True(result.Groups[0].Grounded);
            Assert.False(result.Groups[1].Grounded);
            Assert.Equal(new[] { "c001", "c002", "c003" }, result.Groups[0].Components);

            // The invariant the consumer depends on: a grounded group is never
            // anybody's child, so it is always the LOW side of its edges.
            var edge = Assert.Single(result.Edges);
            Assert.Equal("g000", edge.GroupA);
        }

        /// <summary>
        /// "Fully defined" in SolidWorks means a component has no freedom of
        /// its OWN: NOT that it cannot move. A part fully mated to a moving
        /// one is fully defined and moves with it, which is why the status
        /// cannot weld anything on its own.
        ///
        /// This is the live case that proved it (ClampRig, 2026-08-24):
        /// the cutting head is mated to the machine body AND to the lead screw
        /// rod, reports fully defined, and slides half a metre. An earlier
        /// rule welded on that status and took the whole lead screw assembly
        /// and cutting head out of the rig.
        /// </summary>
        [Fact]
        public void AFullyDefinedFollowerIsNotWelded()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "body", isFixed: true),
                    UnderDefined(Comp("c002", "rod")),
                    FullyDefined(Comp("c003", "carriage")),
                },
                // The rod slides in the body.
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)),
                // The carriage rides the body and is pushed by the rod.
                CoincidentPlanes("Coincident1", "c001", "c003", X, P(0, 0, 0)),
                CoincidentPlanes("Coincident2", "c002", "c003", Z, P(0, 0, 0.4)));

            var result = RigidGrouper.Group(graph);

            Assert.Equal(3, result.Groups.Count);
            Assert.NotEqual(result.ComponentGroup["c001"], result.ComponentGroup["c002"]);
            Assert.NotEqual(result.ComponentGroup["c001"], result.ComponentGroup["c003"]);
        }

        /// <summary>The direction the constrained status CAN be read in: a
        /// component SolidWorks calls under-defined has freedom of its own, so
        /// finding it merged into a group means a lost degree of freedom. It
        /// is reported, not prevented: the merge may still be right, and the
        /// mate analysis is what decided it.</summary>
        [Fact]
        public void AnUnderDefinedComponentThatMergedIsReported()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "base", isFixed: true),
                    UnderDefined(Comp("c002", "flange")),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)),
                Concentric("Concentric2", "c001", "c002", Z, P(0.05, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0.01)));

            var result = RigidGrouper.Group(graph);

            Assert.Single(result.Groups);
            Assert.Equal(new[] { "flange-1" }, result.MergedAwayDofs);
        }

        /// <summary>A mate onto assembly-owned geometry still grounds on the
        /// phantom assembly body, with nothing fixed by hand.</summary>
        [Fact]
        public void AssemblyGeometryGroundsOnThePhantomAssembly()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "plate"),
                    Comp("c002", "bracket"),
                },
                Mate("Coincident1", "swMateCOINCIDENT",
                    PlaneEnt("c001", Z, P(0, 0, 0)), PlaneEnt(null, Z, P(0, 0, 0))),
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)));

            var result = RigidGrouper.Group(graph);

            Assert.True(result.Groups[0].Grounded);
            Assert.DoesNotContain(RigidGrouper.AssemblyGroundId, result.Groups[0].Components);
        }

    }
}
