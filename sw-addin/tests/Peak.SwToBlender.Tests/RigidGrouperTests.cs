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
        /// The side face kills the spin — zero DOF — but the old hand-mirrored
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
        /// six DOF — the fully-defined slider variant of hinge3.</summary>
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
        /// the slide — rigid.</summary>
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
        /// fixed-in-sub merge the union is welded solid — rigidity that only
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
        /// exports swMateCOINCIDENT — the type IS the align flag, nothing else
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
        /// lives only on the feature data — the raw entities are identical to
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

        /// <summary>A toolbox bolt held by one concentric plus the under-head
        /// coincident spins freely in SolidWorks, but it is hardware, not a
        /// mechanism: the filter folds it into the part it is bolted to.</summary>
        [Fact]
        public void ToolboxFastenerWithLoneConcentricMerges()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    Comp("c002", "hex bolt", toolbox: true),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0.01)));

            var result = RigidGrouper.Group(graph);

            Assert.Single(result.Groups);
            Assert.Equal(new[] { "c001", "c002" }, result.Groups[0].Components);
            Assert.Empty(result.Edges);
        }

        /// <summary>The filter also keys on the model file name, for hardware
        /// that never came from the Toolbox.</summary>
        [Fact]
        public void FileNameFastenerMerges()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    Comp("c002", "m6", fileName: "M6_socket_screw.sldprt"),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)));

            var result = RigidGrouper.Group(graph);

            Assert.Single(result.Groups);
            Assert.Empty(result.Edges);
        }

        /// <summary>A distance limit mate on the "fastener" pair means the
        /// part actually slides. The pair stays separate and the edge carries
        /// the override flag so the classifier drops confidence to medium.</summary>
        [Fact]
        public void FastenerWithDistanceLimitIsNotMerged()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "housing", isFixed: true),
                    Comp("c002", "spring pin", toolbox: true),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)),
                DistanceLimit("LimitDistance1", "c001", "c002", Z, P(0, 0, 0),
                    min: 0.0, max: 0.01, current: 0.002));

            var result = RigidGrouper.Group(graph);

            Assert.Equal(2, result.Groups.Count);
            var edge = Assert.Single(result.Edges);
            Assert.True(edge.FastenerOverride);
            Assert.Equal(2, edge.Mates.Count);
            Assert.Equal("g000", edge.GroupA);
            Assert.Equal("g001", edge.GroupB);
        }

        /// <summary>Suppressed components belong to no group, and any mate
        /// that touches one — or is itself suppressed — is inert. The
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
    }
}
