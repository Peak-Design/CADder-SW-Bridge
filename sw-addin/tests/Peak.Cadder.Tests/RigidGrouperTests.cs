using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;
using static Peak.Cadder.Tests.FixtureBuilder;

namespace Peak.Cadder.Tests
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
        ///
        /// The cause was the lead screw's limit mate (live CutterRig,
        /// 2026-09-21: the head reads under-defined with the limit out). So
        /// the status read with the limits IN welds nothing, and only
        /// StatusFree, read with them out, may.
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

        /// <summary>
        /// What SolidWorks says cannot move, read with the limit mates out,
        /// joins the ground even where the mates alone leave it free. Live
        /// CutterRig (2026-09-21): a plate held by a width between two other
        /// plates, which the mate analysis could not read, slid in Blender
        /// and not in SolidWorks.
        /// </summary>
        [Fact]
        public void APartSolidWorksCallsStillIsWelded()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    StillWithLimitsOut(Comp("c002", "plate")),
                },
                // Alone this concentric leaves a turn and a slide.
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)));

            var result = RigidGrouper.Group(graph);

            Assert.Single(result.Groups);
            Assert.True(result.Groups[0].Grounded);
            Assert.Equal(new[] { "plate-1" }, result.StatusWelds);
        }

        /// <summary>A part SolidWorks calls under-defined with the limits
        /// out keeps whatever the mates give it.</summary>
        [Fact]
        public void APartSolidWorksCallsMovingIsNotWelded()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    MovesWithLimitsOut(Comp("c002", "clamp")),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)));

            var result = RigidGrouper.Group(graph);

            Assert.Equal(2, result.Groups.Count);
            Assert.Empty(result.StatusWelds);
        }

        /// <summary>Inside a flexible subassembly the status does not follow
        /// the motion: a hinge leaf reads fully defined while it swings (live
        /// corpus 07, 2026-09-21). So it welds nothing there.</summary>
        [Fact]
        public void TheStatusOfAPartInsideAFlexibleSubassemblyWeldsNothing()
        {
            var leaf = StillWithLimitsOut(Comp("c003", "leaf"));
            leaf.ParentId = "c002";
            var graph = Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    Comp("c002", "hinge"),
                    leaf,
                },
                Concentric("Concentric1", "c001", "c003", Z, P(0, 0, 0)));

            var result = RigidGrouper.Group(graph);

            Assert.NotEqual(result.ComponentGroup["c001"], result.ComponentGroup["c003"]);
            Assert.Empty(result.StatusWelds);
        }

        /// <summary>
        /// A washer that SolidWorks calls fully defined in its subassembly's
        /// own document joins the subassembly's frame, even where the mates
        /// read here leave it free in a plane (live CutterRig, 2026-09-22:
        /// the cutting head's nuts and washers slid in Blender).
        /// </summary>
        [Fact]
        public void APartFullyDefinedInItsOwnSubassemblyJoinsItsFrame()
        {
            var washer = Inside(Comp("c004", "washer"), "c002");
            washer.SubStatusFree = 3;
            var graph = Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    Flexible(Comp("c002", "head")),
                    InSubFixed(Inside(Comp("c003", "plate"), "c002")),
                    washer,
                },
                Concentric("Concentric1", "c001", "c003", X, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c003", "c004", Z, P(0, 0, 0.01)));

            var result = RigidGrouper.Group(graph);

            Assert.Equal(result.ComponentGroup["c003"], result.ComponentGroup["c004"]);
            Assert.Equal(new[] { "washer-1" }, result.SubStatusWelds);
        }

        /// <summary>Under-defined in its own document says nothing about
        /// the parent, so the mates decide.</summary>
        [Fact]
        public void APartUnderDefinedInItsOwnSubassemblyIsLeftToTheMates()
        {
            var washer = Inside(Comp("c004", "washer"), "c002");
            washer.SubStatusFree = 2;
            var graph = Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    Flexible(Comp("c002", "head")),
                    InSubFixed(Inside(Comp("c003", "plate"), "c002")),
                    washer,
                },
                Concentric("Concentric1", "c001", "c003", X, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c003", "c004", Z, P(0, 0, 0.01)));

            var result = RigidGrouper.Group(graph);

            Assert.NotEqual(result.ComponentGroup["c003"], result.ComponentGroup["c004"]);
            Assert.Empty(result.SubStatusWelds);
        }

        /// <summary>A part the DOF probe found free is not welded on its
        /// status, and the mates decide (corpus hydraulic assembly,
        /// 2026-09-22: a slider read fully defined with two limits out).
        /// </summary>
        [Fact]
        public void AVetoedPartIsNotWeldedOnItsStatus()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "base", isFixed: true),
                    StillWithLimitsOut(Comp("c002", "slider")),
                },
                CoincidentPlanes("Coincident1", "c001", "c002", X, P(0, 0, 0)),
                CoincidentPlanes("Coincident2", "c001", "c002", Y, P(0, 0, 0)));

            var welded = RigidGrouper.Group(graph);
            var vetoed = RigidGrouper.Group(graph, null, new HashSet<string> { "c002" });
            var mateOnly = RigidGrouper.Group(graph, null, null, statusWelds: false);

            Assert.Equal(new[] { "c002" }, welded.StatusWeldIds);
            Assert.Equal(welded.ComponentGroup["c001"], welded.ComponentGroup["c002"]);
            Assert.Empty(vetoed.StatusWelds);
            Assert.NotEqual(vetoed.ComponentGroup["c001"], vetoed.ComponentGroup["c002"]);
            Assert.Empty(mateOnly.StatusWelds);
            Assert.NotEqual(mateOnly.ComponentGroup["c001"], mateOnly.ComponentGroup["c002"]);
        }

        /// <summary>A follower on a cam reads fully defined at a dwell and
        /// still moves when the cam turns on, so a cam mate keeps it off the
        /// status pass (corpus cam-follower, 2026-09-22).</summary>
        [Fact]
        public void APartHeldByACamIsNotWeldedOnItsStatus()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    Comp("c002", "cam"),
                    StillWithLimitsOut(Comp("c003", "lifter")),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident2", "c001", "c003", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident3", "c001", "c003", Y, P(0, 0, 0)),
                Mate("CamMateTangent1", "swMateCAMFOLLOWER",
                    Cylinder("c002", Z, P(0, 0, 0), 0.0762),
                    Cylinder("c003", Z, P(0.1137, 0, 0), 0.0375)));

            var result = RigidGrouper.Group(graph);

            Assert.NotEqual(result.ComponentGroup["c001"], result.ComponentGroup["c003"]);
            Assert.Empty(result.StatusWelds);
            Assert.Equal(new[] { "lifter-1" }, result.PoseHeldSkips);
        }

        /// <summary>A part bolted to a cam follower inside a flexible
        /// subassembly reads fully defined in the sub's document at a dwell,
        /// as the follower does. Welding it to the subassembly took the
        /// follower with it, and the cam moved nothing (review,
        /// 2026-09-23).</summary>
        [Fact]
        public void APartBoltedToACamFollowerInASubassemblyIsNotWeldedOnItsStatus()
        {
            var lifter = Inside(Comp("c004", "lifter"), "c002");
            lifter.SubStatusFree = 3;
            var cap = Inside(Comp("c005", "cap"), "c002");
            cap.SubStatusFree = 3;
            var graph = Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    Flexible(Comp("c002", "valve sub")),
                    InSubFixed(Inside(Comp("c003", "cam"), "c002")),
                    lifter,
                    cap,
                },
                Concentric("Concentric1", "c001", "c003", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c003", "c004", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident2", "c003", "c004", Y, P(0, 0, 0)),
                CoincidentPlanes("Coincident3", "c004", "c005", X, P(0.2, 0, 0)),
                CoincidentPlanes("Coincident4", "c004", "c005", Y, P(0, 0, 0)),
                CoincidentPlanes("Coincident5", "c004", "c005", Z, P(0, 0, 0)),
                Mate("CamMateTangent1", "swMateCAMFOLLOWER",
                    Cylinder("c003", Z, P(0, 0, 0), 0.0762),
                    Cylinder("c004", Z, P(0.1137, 0, 0), 0.0375)));

            var result = RigidGrouper.Group(graph);

            Assert.Equal(result.ComponentGroup["c004"], result.ComponentGroup["c005"]);
            Assert.NotEqual(result.ComponentGroup["c002"], result.ComponentGroup["c004"]);
            Assert.Empty(result.SubStatusWelds);
        }

        /// <summary>A component no active mate touches (a pattern or mirror
        /// instance) follows its seed, so its status welds nothing.</summary>
        [Fact]
        public void AnUnmatedPartIsNotWeldedOnItsStatus()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    StillWithLimitsOut(Comp("c002", "copy")),
                });

            var result = RigidGrouper.Group(graph);

            Assert.NotEqual(result.ComponentGroup["c001"], result.ComponentGroup["c002"]);
            Assert.Empty(result.StatusWelds);
        }

        /// <summary>A part SolidWorks calls under-defined that the mates
        /// merged into a MOVING group is where it belongs (a bolt on a
        /// swinging clamp moves because the clamp does): no report.</summary>
        [Fact]
        public void AnUnderDefinedPartOnAMovingBodyIsNotReported()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    MovesWithLimitsOut(Comp("c002", "clamp")),
                    MovesWithLimitsOut(Comp("c003", "bolt")),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)),
                Concentric("Concentric2", "c002", "c003", Z, P(0.1, 0, 0)),
                Concentric("Concentric3", "c002", "c003", Z, P(0.15, 0, 0)),
                CoincidentPlanes("Coincident1", "c002", "c003", Z, P(0, 0, 0.01)));

            var result = RigidGrouper.Group(graph);

            Assert.Equal(result.ComponentGroup["c002"], result.ComponentGroup["c003"]);
            Assert.Empty(result.MergedAwayDofs);
        }

        /// <summary>
        /// A plate whose tab is centred between faces on two OTHER plates.
        /// The width touches three components, so it used to be dropped and
        /// the plate slid in its groove. Once the two outer plates are one
        /// body, it is a width between two bodies (live CutterRig,
        /// 2026-09-21).
        /// </summary>
        [Fact]
        public void AWidthBetweenTwoPlatesHoldsATabOnceThePlatesAreOneBody()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "left plate", isFixed: true),
                    Comp("c002", "right plate", isFixed: true),
                    Comp("c003", "tab"),
                },
                CoincidentPlanes("Coincident1", "c001", "c003", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident2", "c001", "c003", Y, P(0, 0, 0)),
                Mate("Width1", "swMateWIDTH",
                    PlaneEnt("c001", X, P(-0.05, 0, 0)),
                    PlaneEnt("c002", X, P(0.05, 0, 0)),
                    PlaneEnt("c003", X, P(-0.02, 0, 0)),
                    PlaneEnt("c003", X, P(0.02, 0, 0))));

            var result = RigidGrouper.Group(graph);

            Assert.Single(result.Groups);
            Assert.Empty(result.UnreadMultiMates);
        }

        /// <summary>A Free width lets the tab sit anywhere between the faces,
        /// so it holds no position across them.</summary>
        [Fact]
        public void AFreeWidthLeavesTheTabItsSlide()
        {
            var width = Mate("Width1", "swMateWIDTH",
                PlaneEnt("c001", X, P(-0.05, 0, 0)),
                PlaneEnt("c002", X, P(0.05, 0, 0)),
                PlaneEnt("c003", X, P(-0.02, 0, 0)),
                PlaneEnt("c003", X, P(0.02, 0, 0)));
            width.WidthFree = true;
            var graph = Graph(
                new[]
                {
                    Comp("c001", "left plate", isFixed: true),
                    Comp("c002", "right plate", isFixed: true),
                    Comp("c003", "tab"),
                },
                CoincidentPlanes("Coincident1", "c001", "c003", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident2", "c001", "c003", Y, P(0, 0, 0)),
                width);

            var result = RigidGrouper.Group(graph);

            Assert.Equal(2, result.Groups.Count);
            Assert.Single(result.Edges);
            Assert.Contains(width, result.Edges[0].Mates);
        }

        /// <summary>A width face and a tab face on each of two bodies ties
        /// nothing between them, so the width is not read as one between
        /// those two bodies, and the rig is told it could not use it.</summary>
        [Fact]
        public void AWidthSplitAcrossItsRolesIsNotReadAsAPairMate()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    Comp("c002", "carriage"),
                    Comp("c003", "bracket", isFixed: true),
                },
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident2", "c001", "c002", Y, P(0, 0, 0)),
                Mate("Width1", "swMateWIDTH",
                    PlaneEnt("c001", X, P(-0.05, 0, 0)),
                    PlaneEnt("c002", X, P(0.05, 0, 0)),
                    PlaneEnt("c003", X, P(-0.02, 0, 0)),
                    PlaneEnt("c002", X, P(0.02, 0, 0))));

            var result = RigidGrouper.Group(graph);

            Assert.Equal(2, result.Groups.Count);
            Assert.Equal(new[] { "Width1" }, result.UnreadMultiMates);
        }

        /// <summary>
        /// Plates centred on a tab made of a face on a housing and a face on
        /// the frame. The housing keeps a slide pairwise, so the grouping
        /// reads the width over three groups and cannot use it. Once the
        /// loops have welded the housing to the frame, the width is between
        /// two bodies, and it holds the plates' slide (live CutterRig,
        /// 2026-09-22: the plates slid along the width).
        /// </summary>
        [Fact]
        public void AWidthHoldsItsJointOnceTheLoopsWeldTheTab()
        {
            var graph = PlatesOnASplitTab();
            var grouping = RigidGrouper.Group(graph);
            Assert.Equal(new[] { "Width1" }, grouping.UnreadMultiMates);
            string frame = grouping.ComponentGroup["c001"];
            string housing = grouping.ComponentGroup["c002"];
            string plates = grouping.ComponentGroup["c003"];
            var slide = new RigJoint
            {
                Id = "j001", Type = JointType.Prismatic, ParentGroup = frame, ChildGroup = plates,
                Axis = (double[])X.Clone(), Origin = P(0, 0, 0),
            };
            var weld = new RigJoint
            {
                Id = "j002", Type = JointType.Fixed, ParentGroup = frame, ChildGroup = housing,
                Axis = (double[])Z.Clone(), Origin = P(0, 0, 0),
            };

            var welded = RigidGrouper.HoldAcrossWelds(
                graph, grouping, new List<RigJoint> { slide, weld }, null);

            Assert.Equal(new[] { "j001" }, welded);
            Assert.Equal(JointType.Fixed, slide.Type);
            Assert.Contains(slide.SourceMates, s => s.SwFeature == "Width1");
            Assert.Empty(grouping.UnreadMultiMates);
        }

        /// <summary>While the housing still moves, the width stays over
        /// three bodies and holds nothing.</summary>
        [Fact]
        public void AWidthOverThreeMovingBodiesHoldsNothing()
        {
            var graph = PlatesOnASplitTab();
            var grouping = RigidGrouper.Group(graph);
            string frame = grouping.ComponentGroup["c001"];
            string housing = grouping.ComponentGroup["c002"];
            string plates = grouping.ComponentGroup["c003"];
            var slide = new RigJoint
            {
                Id = "j001", Type = JointType.Prismatic, ParentGroup = frame, ChildGroup = plates,
                Axis = (double[])X.Clone(), Origin = P(0, 0, 0),
            };
            var planar = new RigJoint
            {
                Id = "j002", Type = JointType.Planar, ParentGroup = frame, ChildGroup = housing,
                Axis = (double[])Z.Clone(), Origin = P(0, 0, 0),
            };

            var welded = RigidGrouper.HoldAcrossWelds(
                graph, grouping, new List<RigJoint> { slide, planar }, null);

            Assert.Empty(welded);
            Assert.Equal(JointType.Prismatic, slide.Type);
            Assert.Equal(new[] { "Width1" }, grouping.UnreadMultiMates);
        }

        private static MateGraph PlatesOnASplitTab()
        {
            return Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    Comp("c002", "housing"),
                    Comp("c003", "plates"),
                },
                // The plates slide along X on the frame.
                Concentric("Concentric1", "c001", "c003", X, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c003", Y, P(0, 0, 0)),
                // The housing sits flat on the frame, free in that plane.
                CoincidentPlanes("Coincident2", "c001", "c002", Z, P(0, 0, 0.1)),
                // Width faces on the plates, the tab split over housing and frame.
                Mate("Width1", "swMateWIDTH",
                    PlaneEnt("c003", X, P(-0.05, 0, 0)),
                    PlaneEnt("c003", X, P(0.05, 0, 0)),
                    PlaneEnt("c002", X, P(-0.02, 0, 0.1)),
                    PlaneEnt("c001", X, P(0.02, 0, 0))));
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
