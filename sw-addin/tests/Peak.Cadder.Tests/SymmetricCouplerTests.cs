using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;
using static Peak.Cadder.Tests.FixtureBuilder;

namespace Peak.Cadder.Tests
{
    /// <summary>Three-body symmetric mates as couplings (corpus 14 sym3).
    /// The coupler is fed hand-built joints so each geometric verdict is
    /// pinned without depending on how the mounts happened to classify.</summary>
    public class SymmetricCouplerTests
    {
        private static RigidGroupingResult Grouping()
        {
            var g = new RigidGroupingResult();
            g.Groups.Add(new RigidGroup { Id = "g000", Name = "plate", Grounded = true, Components = { "c001" } });
            g.Groups.Add(new RigidGroup { Id = "g001", Name = "puck1", Components = { "c002" } });
            g.Groups.Add(new RigidGroup { Id = "g002", Name = "puck2", Components = { "c003" } });
            g.ComponentGroup["c001"] = "g000";
            g.ComponentGroup["c002"] = "g001";
            g.ComponentGroup["c003"] = "g002";
            return g;
        }

        private static RigJoint Mount(string id, string type, string child, double[] axis)
        {
            return new RigJoint
            {
                Id = id,
                Type = type,
                ParentGroup = "g000",
                ChildGroup = child,
                Axis = (double[])axis.Clone(),
                SecondaryAxis = new double[] { 0, 0, 1 },
                Origin = new double[3],
            };
        }

        /// <summary>The mirror plane is found geometrically: three parallel
        /// plane entities, the mirror the one midway between the others.</summary>
        private static MateGraph SymmetricGraph()
        {
            return Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    Comp("c002", "puck one"),
                    Comp("c003", "puck two"),
                },
                Mate("Symmetric1", "swMateSYMMETRIC",
                    PlaneEnt("c002", Y, P(0, 0.03, 0)),
                    PlaneEnt("c003", Y, P(0, -0.03, 0)),
                    PlaneEnt("c001", Y, P(0, 0, 0))));
        }

        /// <summary>
        /// The mirror plane is the plane that REFLECTS the other two onto each
        /// other, and mirrored planes need not be parallel to it: only to
        /// each other's reflection.
        ///
        /// Live ClampRig (2026-08-25, Oscar): one symmetric mate holds
        /// the two clamps so they open and close together. Their own planes
        /// are tilted 0.0822 degrees out of the machine's centre plane, which
        /// is a thousand times the parallel tolerance, so the old "three
        /// parallel planes" reading dropped the mate silently: no coupling
        /// and no warning, and the clamps posed independently. Every number
        /// below is off that assembly's own mate table; reflecting the first
        /// clamp's plane about the centre plane reproduces the second's to
        /// nine decimals.
        /// </summary>
        [Fact]
        public void MirroredPlanesNeedNotBeParallelToTheMirror()
        {
            var mirror = new double[] { 1, 0, 0 };
            var clampA = new double[] { -1, -2.8604E-17, -0.0014353 };
            var clampB = new double[] { 1, -4.0059E-17, -0.0014353 };

            var graph = Graph(
                new[]
                {
                    Comp("c001", "machine body", isFixed: true),
                    Comp("c002", "clamp one"),
                    Comp("c003", "clamp two"),
                },
                Mate("Symmetric5", "swMateSYMMETRIC",
                    PlaneEnt("c001", mirror, P(0, 0, 0)),
                    PlaneEnt("c002", clampA, P(-0.72657, 0.085, -1.0598)),
                    PlaneEnt("c003", clampB, P(0.72657, -0.085, -1.0598))));

            // Both clamps hang on their own vertical pin, as they do live.
            var joints = new List<RigJoint>
            {
                Mount("j001", JointType.Revolute, "g001", Y),
                Mount("j002", JointType.Revolute, "g002", Y),
            };

            var warnings = SymmetricCoupler.Resolve(graph, Grouping(), joints);

            Assert.Empty(warnings);
            Assert.Null(joints[0].Coupling);
            var c = joints[1].Coupling;
            Assert.NotNull(c);
            Assert.Equal("gear", c.Kind);
            Assert.Equal("j001", c.DriverJoint);
            // A reflection reverses orientation, and the pin survives it
            // unchanged (it lies in the mirror plane), so one clamp turns
            // exactly opposite the other.
            Assert.Equal(-1.0, c.Ratio ?? 0.0, 9);
        }

        [Fact]
        public void PrismaticPairBecomesLinearCoupler()
        {
            var joints = new List<RigJoint>
            {
                Mount("j001", JointType.Prismatic, "g001", X),
                Mount("j002", JointType.Prismatic, "g002", X),
            };
            var warnings = SymmetricCoupler.Resolve(SymmetricGraph(), Grouping(), joints);

            Assert.Empty(warnings);
            Assert.Null(joints[0].Coupling);
            var c = joints[1].Coupling;
            Assert.NotNull(c);
            Assert.Equal("linear_coupler", c.Kind);
            Assert.Equal("j001", c.DriverJoint);
            // The slide axis lies IN the mirror plane: its mirror is itself,
            // so the mirrored body slides the same way.
            Assert.Equal(1.0, c.Ratio.Value, 9);
            Assert.Contains(joints[1].SourceMates, s => s.SwFeature == "Symmetric1");
        }

        [Fact]
        public void MirroredSlideAxisFlipsTheRatio()
        {
            // Mounts along the mirror NORMAL, recorded with the same world
            // sense: the mirror reverses the direction, so the driven runs
            // opposite.
            var joints = new List<RigJoint>
            {
                Mount("j001", JointType.Prismatic, "g001", Y),
                Mount("j002", JointType.Prismatic, "g002", Y),
            };
            var warnings = SymmetricCoupler.Resolve(SymmetricGraph(), Grouping(), joints);
            Assert.Empty(warnings);
            Assert.Equal(-1.0, joints[1].Coupling.Ratio.Value, 9);
        }

        [Fact]
        public void RevolutePairBecomesCounterGear()
        {
            var joints = new List<RigJoint>
            {
                Mount("j001", JointType.Revolute, "g001", Z),
                Mount("j002", JointType.Revolute, "g002", Z),
            };
            var warnings = SymmetricCoupler.Resolve(SymmetricGraph(), Grouping(), joints);

            Assert.Empty(warnings);
            var c = joints[1].Coupling;
            Assert.NotNull(c);
            Assert.Equal("gear", c.Kind);
            // Reflections reverse orientation: a rotation mirrors into its
            // negative, and both axes here lie in the mirror plane.
            Assert.Equal(-1.0, c.Ratio.Value, 9);
        }

        [Fact]
        public void MixedMountTypesFallBackToTheWarning()
        {
            var joints = new List<RigJoint>
            {
                Mount("j001", JointType.Prismatic, "g001", X),
                Mount("j002", JointType.Revolute, "g002", Z),
            };
            var warnings = SymmetricCoupler.Resolve(SymmetricGraph(), Grouping(), joints);

            Assert.Null(joints[1].Coupling);
            var w = Assert.Single(warnings);
            Assert.Equal("SYMMETRIC_COUPLING", w.Code);
        }

        /// <summary>Live corpus 14 sym4 (2026-08-23): ONLY the symmetric
        /// mate between the two slides: both exported as free islands with
        /// a warning. The mirror IS the whole relation: two ground-rooted
        /// free joints are synthesized, the driven one carrying the 6-DOF
        /// mirror coupling with the mate's plane. Entity shape is the live
        /// log's: mirrored side planes plus an ASSEMBLY-owned bisector.</summary>
        [Fact]
        public void UnmountedPairOfPOINTSWarnsInsteadOfMirroring()
        {
            // The mirror coupling means what a PLANE-to-plane symmetry
            // constrains: the translation along the normal and the two
            // rotations that tilt it. Two symmetric POINTS constrain all
            // three translations and no rotation at all: a different set,
            // so reading them as planes would silently free two rotations
            // SolidWorks holds and hold two translations it frees.
            var minusX = new double[] { -1, 0, 0 };
            var graph = Graph(
                new[]
                {
                    Comp("c001", "rail", isFixed: true),
                    Comp("c002", "slide one"),
                    Comp("c003", "slide two"),
                },
                Mate("Symmetric5", "swMateSYMMETRIC",
                    VertexEnt("c003", P(0.029526, -0.01, 0.02)),
                    VertexEnt("c002", P(0.070474, -0.01, 0.02)),
                    PlaneEnt(null, minusX, P(0.05, 0, 0))));
            var joints = new List<RigJoint>();

            var warnings = SymmetricCoupler.Resolve(graph, Grouping(), joints);

            Assert.Empty(joints);
            var warning = Assert.Single(warnings);
            Assert.Equal("SYMMETRIC_COUPLING", warning.Code);
            Assert.Contains("planar faces", warning.Message);
            Assert.Contains("vertex", warning.Message);
        }

        [Fact]
        public void UnmountedPairSynthesizesTheMirrorPair()
        {
            var minusX = new double[] { -1, 0, 0 };
            var graph = Graph(
                new[]
                {
                    Comp("c001", "rail", isFixed: true),
                    Comp("c002", "slide one"),
                    Comp("c003", "slide two"),
                },
                Mate("Symmetric4", "swMateSYMMETRIC",
                    PlaneEnt("c003", X, P(0.029526, -0.01, 0.02)),
                    PlaneEnt("c002", minusX, P(0.070474, -0.01, 0.02)),
                    PlaneEnt(null, minusX, P(0.05, 0, 0))));
            var joints = new List<RigJoint>();

            var warnings = SymmetricCoupler.Resolve(graph, Grouping(), joints);

            Assert.Empty(warnings);
            Assert.Equal(2, joints.Count);
            var driver = joints[0];
            var driven = joints[1];
            Assert.Equal(JointType.Free, driver.Type);
            Assert.Equal("g000", driver.ParentGroup);
            Assert.Equal("g001", driver.ChildGroup);    // lower group id drives
            Assert.Null(driver.Coupling);
            Assert.Equal(JointType.Free, driven.Type);
            Assert.Equal("g000", driven.ParentGroup);
            Assert.Equal("g002", driven.ChildGroup);
            Assert.NotNull(driven.Coupling);
            Assert.Equal("mirror", driven.Coupling.Kind);
            Assert.Equal(driver.Id, driven.Coupling.DriverJoint);
            Assert.NotNull(driven.Coupling.MirrorPlaneNormal);
            Assert.Equal(1.0,
                System.Math.Abs(driven.Coupling.MirrorPlaneNormal[0]), 9);
            Assert.Equal(0.05, driven.Coupling.MirrorPlanePoint[0], 9);
            Assert.Contains(driver.SourceMates, s => s.SwFeature == "Symmetric4");
            Assert.Contains(driven.SourceMates, s => s.SwFeature == "Symmetric4");
        }

        /// <summary>A pair that IS mated to something else (even without a
        /// usable mount) keeps the honest warning: the mirror-pair
        /// synthesis is only for bodies whose whole relation is the
        /// symmetric mate.</summary>
        [Fact]
        public void UnmountedButMatedPairKeepsTheWarning()
        {
            var joints = new List<RigJoint>
            {
                new RigJoint
                {
                    Id = "j001",
                    Type = JointType.Ball,
                    ParentGroup = "g000",
                    ChildGroup = "g001",
                },
            };
            var warnings = SymmetricCoupler.Resolve(SymmetricGraph(), Grouping(), joints);

            var w = Assert.Single(warnings);
            Assert.Equal("SYMMETRIC_COUPLING", w.Code);
            Assert.Single(joints);    // nothing synthesized
        }

        [Fact]
        public void TwoBodySymmetricIsLeftToTheResolver()
        {
            // Both mirrored entities on ONE body: the pairwise resolver
            // already models this as a mid-plane coincidence: no coupling,
            // no warning.
            var graph = Graph(
                new[]
                {
                    Comp("c001", "channel", isFixed: true),
                    Comp("c002", "slide"),
                },
                Mate("Symmetric1", "swMateSYMMETRIC",
                    PlaneEnt("c001", Y, P(0, 0.03, 0)),
                    PlaneEnt("c001", Y, P(0, -0.03, 0)),
                    PlaneEnt("c002", Y, P(0, 0, 0))));
            var grouping = Grouping();
            var joints = new List<RigJoint>
            {
                Mount("j001", JointType.Prismatic, "g001", X),
            };
            var warnings = SymmetricCoupler.Resolve(graph, grouping, joints);
            Assert.Empty(warnings);
            Assert.Null(joints[0].Coupling);
        }

        // ── Mirrored lines ──────────────────────────────────────────────────

        /// <summary>The slide axis of each ram, as the two mounts carry it:
        /// the second is the first mirrored about the X plane.</summary>
        private static readonly double[] RamAxis =
            MathOps.Normalized(new[] { 0.42281, 0.0, 0.90622 });
        private static readonly double[] MirroredRamAxis =
            MathOps.Normalized(new[] { -0.42281, 0.0, 0.90622 });

        /// <summary>Two rod-end cylinders held symmetric about the
        /// assembly's Right plane, with the entity shape of the live log:
        /// the axes point opposite ways and carry SolidWorks' own noise,
        /// and each point sits wherever SolidWorks named the cylinder, 35 mm
        /// apart along the axes. `rodTwoX` moves the second cylinder.</summary>
        private static MateGraph MirroredCylinders(double rodTwoX)
        {
            return Graph(
                new[]
                {
                    Comp("c001", "machine body", isFixed: true),
                    Comp("c002", "rod one"),
                    Comp("c003", "rod two"),
                },
                Mate("Symmetric54", "swMateSYMMETRIC",
                    Cylinder("c002", new[] { -1.5245E-15, -1.0, 7.1129E-16 },
                             P(0.32991, 0.053, 0.41988), 0.015),
                    Cylinder("c003", new[] { 4.0211E-16, 1.0, -1.0388E-15 },
                             P(rodTwoX, 0.018, 0.41988), 0.015),
                    PlaneEnt(null, X, P(0, 0, 0))));
        }

        /// <summary>
        /// Two cylinder axes are mirror images when the reflected point of
        /// one lies ON the other line and the reflected direction is
        /// parallel to the other's: the points need not match along the axis.
        ///
        /// Live CutterRig (2026-09-21): one symmetric mate holds the two
        /// ram rod ends as mirror images, so the clamps they drive open and
        /// close together. The coupler compared the axes as planes, the
        /// points 35 mm apart along the axes failed that test, and the mate
        /// was dropped with no coupling and no warning.
        /// </summary>
        [Fact]
        public void MirroredCylinderAxesBecomeACoupling()
        {
            var joints = new List<RigJoint>
            {
                Mount("j001", JointType.Prismatic, "g001", RamAxis),
                Mount("j002", JointType.Prismatic, "g002", MirroredRamAxis),
            };

            var warnings = SymmetricCoupler.Resolve(
                MirroredCylinders(-0.32991), Grouping(), joints);

            Assert.Empty(warnings);
            Assert.Null(joints[0].Coupling);
            var c = joints[1].Coupling;
            Assert.NotNull(c);
            Assert.Equal("linear_coupler", c.Kind);
            Assert.Equal("j001", c.DriverJoint);
            // The mounts are mirror images with the same sense, so one rod
            // extends exactly as far as the other.
            Assert.Equal(1.0, c.Ratio ?? 0.0, 9);
            Assert.Contains(joints[1].SourceMates, s => s.SwFeature == "Symmetric54");
        }

        /// <summary>
        /// A mount measures its child against its parent. The mirror relates
        /// each body against its partner, so a mount that has the mirrored
        /// body as its PARENT runs the other way, and its sense flips.
        ///
        /// Live CutterRig (2026-09-21): rod one slides on a cylinder-to-rod
        /// joint, rod two on a rod-to-cylinder joint, the reverse. The
        /// mirror of rod one's axis is exactly opposite rod two's axis,
        /// which on its own says -1. With rod two as its joint's parent,
        /// that flips back to +1: the rods extend together.
        /// </summary>
        [Fact]
        public void AMountOnTheParentSideFlipsTheRatio()
        {
            var rodTwo = Mount("j002", JointType.Prismatic, "g003",
                MathOps.Normalized(new[] { 0.42281, 0.0, -0.90622 }));
            rodTwo.ParentGroup = "g002";
            var joints = new List<RigJoint>
            {
                Mount("j001", JointType.Prismatic, "g001", RamAxis),
                rodTwo,
            };

            var warnings = SymmetricCoupler.Resolve(
                MirroredCylinders(-0.32991), Grouping(), joints);

            Assert.Empty(warnings);
            var c = joints[1].Coupling;
            Assert.NotNull(c);
            Assert.Equal("linear_coupler", c.Kind);
            Assert.Equal("j001", c.DriverJoint);
            Assert.Equal(1.0, c.Ratio ?? 0.0, 9);
        }

        [Fact]
        public void LinesThatAreNotMirrorImagesWarn()
        {
            var joints = new List<RigJoint>
            {
                Mount("j001", JointType.Prismatic, "g001", RamAxis),
                Mount("j002", JointType.Prismatic, "g002", MirroredRamAxis),
            };

            // The second cylinder sits 30 mm off the mirror image of the first.
            var warnings = SymmetricCoupler.Resolve(
                MirroredCylinders(-0.29991), Grouping(), joints);

            Assert.Null(joints[0].Coupling);
            Assert.Null(joints[1].Coupling);
            var w = Assert.Single(warnings);
            Assert.Equal("SYMMETRIC_COUPLING", w.Code);
            Assert.Contains("Symmetric54", w.Message);
            Assert.Contains("not mirror images", w.Message);
        }

        /// <summary>Two mirrored LINES constrain four freedoms, not the six
        /// a free mirror pair locks, so an unmounted pair keeps the warning
        /// that points get.</summary>
        [Fact]
        public void UnmountedPairOfCylindersWarnsInsteadOfMirroring()
        {
            var joints = new List<RigJoint>();

            var warnings = SymmetricCoupler.Resolve(
                MirroredCylinders(-0.32991), Grouping(), joints);

            Assert.Empty(joints);
            var w = Assert.Single(warnings);
            Assert.Equal("SYMMETRIC_COUPLING", w.Code);
            Assert.Contains("cylinder", w.Message);
        }

        /// <summary>Three planes on three groups, none of which reflects the
        /// other two onto each other: the mate cannot be read, and the user
        /// is told so rather than left to find the bodies uncoupled.</summary>
        [Fact]
        public void ThreeBodyMateWithNoMirrorWarns()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    Comp("c002", "puck one"),
                    Comp("c003", "puck two"),
                },
                Mate("Symmetric1", "swMateSYMMETRIC",
                    PlaneEnt("c002", Y, P(0, 0.03, 0)),
                    PlaneEnt("c003", Y, P(0, -0.05, 0)),
                    PlaneEnt("c001", Y, P(0, 0, 0))));
            var joints = new List<RigJoint>
            {
                Mount("j001", JointType.Prismatic, "g001", X),
                Mount("j002", JointType.Prismatic, "g002", X),
            };

            var warnings = SymmetricCoupler.Resolve(graph, Grouping(), joints);

            Assert.Null(joints[1].Coupling);
            var w = Assert.Single(warnings);
            Assert.Equal("SYMMETRIC_COUPLING", w.Code);
            Assert.Contains("reflects the other two", w.Message);
        }

        // ── The same lines between TWO bodies, as the resolver reads them ───

        /// <summary>Both cylinders on one body, mirrored about a plane of the
        /// other: the body's own mirror plane of its two axes lies in that
        /// plane. A plane coincidence on the mirror's normal, the same as
        /// for two mirrored faces: the slide along X and the two tilts die.</summary>
        [Fact]
        public void TwoBodyMirroredCylindersArePlanarAboutTheMirror()
        {
            var mates = new List<GraphMate>
            {
                Mate("Symmetric1", "swMateSYMMETRIC",
                    Cylinder("c002", Y, P(0.04, 0.02, 0.01)),
                    Cylinder("c002", Y, P(-0.04, -0.03, 0.01)),
                    PlaneEnt("c001", X, P(0, 0, 0))),
            };

            var state = MotionResolver.Resolve(mates);

            Assert.Equal(0, state.Unmodelled);
            Assert.Equal(2, state.TransDirs.Count);
            foreach (var d in state.TransDirs)
                Assert.True(System.Math.Abs(MathOps.Dot(MathOps.Normalized(d), X)) < 1e-9,
                            "a slide along the mirror normal survived");
            Assert.Equal(RotFreedom.AboutDirection, state.Rot);
            Assert.True(System.Math.Abs(MathOps.Dot(state.RotDir, X)) > 1.0 - 1e-9);
        }

        /// <summary>Two coaxial cylinders square to the plane mirror onto
        /// themselves wherever the body slides along that axis. Read as
        /// normals, the axes killed that slide, which exists: now the mate
        /// is left unmodelled rather than killing a freedom.</summary>
        [Fact]
        public void CoaxialCylindersSquareToTheMirrorKeepTheirSlide()
        {
            var mates = new List<GraphMate>
            {
                Mate("Symmetric1", "swMateSYMMETRIC",
                    Cylinder("c002", X, P(0.03, 0.01, 0)),
                    Cylinder("c002", X, P(-0.03, 0.01, 0)),
                    PlaneEnt("c001", X, P(0, 0, 0))),
            };

            var state = MotionResolver.Resolve(mates);

            Assert.Equal(1, state.Unmodelled);
            Assert.Equal(3, state.TransDirs.Count);
            Assert.Equal(RotFreedom.Full, state.Rot);
        }

        /// <summary>One cylinder on the plane's own body: the other body's
        /// cylinder must sit on that cylinder's fixed mirror image. A
        /// concentric in different clothes: a turn about and a slide along
        /// the mirrored axis are all that remain.</summary>
        [Fact]
        public void CylinderMirroredFromThePlanesBodyIsALineCoincidence()
        {
            var mates = new List<GraphMate>
            {
                Mate("Symmetric1", "swMateSYMMETRIC",
                    Cylinder("c001", Y, P(0.04, 0.02, 0.01)),
                    Cylinder("c002", Y, P(-0.04, -0.03, 0.01)),
                    PlaneEnt("c001", X, P(0, 0, 0))),
            };

            var state = MotionResolver.Resolve(mates);

            Assert.Equal(0, state.Unmodelled);
            var slide = Assert.Single(state.TransDirs);
            Assert.True(System.Math.Abs(MathOps.Dot(MathOps.Normalized(slide), Y)) > 1.0 - 1e-9);
            Assert.Equal(RotFreedom.AboutLine, state.Rot);
            Assert.True(System.Math.Abs(MathOps.Dot(state.RotDir, Y)) > 1.0 - 1e-9);
            Assert.True(MateFacts.DistancePointToLine(
                P(-0.04, 0, 0.01), state.RotDir, state.RotPoint) < 1e-9);
        }
    }
}
