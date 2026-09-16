using System.Collections.Generic;
using Peak.SwToBlender.Core;
using Peak.SwToBlender.Core.Model;
using Xunit;
using static Peak.SwToBlender.Tests.FixtureBuilder;

namespace Peak.SwToBlender.Tests
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
    }
}
