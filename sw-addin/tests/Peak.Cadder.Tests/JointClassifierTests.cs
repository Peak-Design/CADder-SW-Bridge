using System;
using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;
using static Peak.Cadder.Tests.FixtureBuilder;

namespace Peak.Cadder.Tests
{
    public class JointClassifierTests
    {
        private static ClassificationResult Run(MateGraph graph)
        {
            return JointClassifier.Classify(graph, RigidGrouper.Group(graph));
        }

        private static void AssertVector(double[] expected, double[] actual, double tol = 1e-9)
        {
            Assert.NotNull(actual);
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(expected[i], actual[i], tol);
        }

        /// <summary>Live corpus 07 (2026-08-22), the whole flexible-sub
        /// pipeline offline: the graph exactly as MateReader records it with
        /// the sub-document mate route, top coincidents on the fixed base,
        /// a distance on the sub's own reference plane (pinned to the sub
        /// node), and the internal pin mates. Fixed-in-sub plus the
        /// group-level rigidity sweep must weld baseplate, sub frame and base
        /// into one grounded group, leaving one limited revolute for the
        /// leaf.</summary>
        [Fact]
        public void FlexibleSubHingeEndToEnd()
        {
            var subFrame = Comp("c001", "hinge");
            subFrame.Solving = "flexible";
            var fixedBase = Comp("c002", "hinge base");
            fixedBase.ParentId = "c001";
            fixedBase.FixedInSubassembly = true;
            var leaf = Comp("c003", "hinge leaf");
            leaf.ParentId = "c001";

            var graph = Graph(
                new[]
                {
                    subFrame,
                    fixedBase,
                    leaf,
                    Comp("c004", "baseplate", isFixed: true),
                },
                CoincidentPlanes("Coincident1", "c004", "c002", Z, P(0.04, 0.02, 0.01)),
                CoincidentPlanes("Coincident4", "c004", "c002", Y, P(0.01, 0, 0.01)),
                Mate("Distance2", "swMateDISTANCE",
                    PlaneEnt("c001", X, P(0.04, 0.02, 0.02)),
                    PlaneEnt("c004", X, P(0, 0, 0.01))),
                Concentric("Concentric1", "c002", "c003", Z, P(0.04, 0.02, 0.028)),
                CoincidentPlanes("CoincidentSub", "c001", "c003", Z, P(0.04, 0.02, 0.02)),
                AngleLimit("LimitAngle1", "c001", "c003",
                    X, P(0.76099, 0.64877, 0), P(0.04, 0.02, 0.02),
                    min: 0.0, max: 1.5708, current: 0.709));

            var grouping = RigidGrouper.Group(graph);
            Assert.Equal(2, grouping.Groups.Count);
            Assert.True(grouping.Groups[0].Grounded);
            Assert.Equal(new[] { "c001", "c002", "c004" }, grouping.Groups[0].Components);
            Assert.Equal(new[] { "c003" }, grouping.Groups[1].Components);

            var classification = JointClassifier.Classify(graph, grouping);
            var joint = Assert.Single(classification.Joints);
            Assert.Equal(JointType.Revolute, joint.Type);
            Assert.Equal(1.0, Math.Abs(joint.Axis[2]), 9);
            AssertVector(new[] { 0.04, 0.02, 0.02 }, joint.Origin, 1e-6);
            Assert.NotNull(joint.RotationLimit);
            Assert.Equal(0.0, joint.RotationLimit.Min);
            Assert.Equal(1.5708, joint.RotationLimit.Max);
            Assert.Empty(classification.Warnings);
        }

        // ── Hinge ───────────────────────────────────────────────────────────

        /// <summary>Concentric plus a coincident plane whose normal runs along
        /// the axis: one rotation left. The mate cylinder is stated 0.04 above
        /// the plane so the origin's slide down to the plane is observable.</summary>
        [Fact]
        public void HingeIsRevoluteWithAxisOriginAndAngleLimit()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "hinge base", isFixed: true,
                        bboxMin: P(-0.03, -0.02, 0.0), bboxMax: P(0.03, 0.02, 0.018)),
                    Comp("c002", "hinge leaf",
                        bboxMin: P(-0.02, -0.02, 0.0), bboxMax: P(0.05, 0.02, 0.02)),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0.05)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0.01)),
                AngleLimit("LimitAngle1", "c001", "c002",
                    X, P(Math.Cos(0.1), Math.Sin(0.1), 0), P(0, 0, 0.01),
                    min: -0.5236, max: 0.7854, current: 0.1));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Revolute, joint.Type);
            Assert.Equal("j001", joint.Id);
            Assert.Equal("g000", joint.ParentGroup);
            Assert.Equal("g001", joint.ChildGroup);
            Assert.Equal("high", joint.Confidence);

            AssertVector(new double[] { 0, 0, 1 }, joint.Axis);
            AssertVector(P(0, 0, 0.01), joint.Origin);    // slid onto the plane

            Assert.NotNull(joint.RotationLimit);
            Assert.Equal(-0.5236, joint.RotationLimit.Min, 1e-12);
            Assert.Equal(0.7854, joint.RotationLimit.Max, 1e-12);
            Assert.Equal(0.1, joint.RotationLimit.ValueAtRest, 1e-12);
            Assert.Null(joint.TranslationLimit);

            Assert.Equal(3, joint.SourceMates.Count);
            Assert.Empty(result.Warnings);
        }

        /// <summary>The live bug from corpus assembly 01 (2026-08-22): the
        /// concentric entity reported −Z while the limit-angle dimension grows
        /// counter-clockwise about +Z, so the exported limits acted mirrored
        /// in Blender (0..75° became −45..+30° from rest). The axis must flip
        /// to +Z so the dimension grows with positive right-handed rotation.
        /// Numbers are the live assembly's: min 0, max 75°, rest 30°.</summary>
        [Fact]
        public void HingeAxisIsCanonicalAndLimitValuesKeepTheDimensionSense()
        {
            var minusZ = new double[] { 0, 0, -1 };
            var graph = Graph(
                new[]
                {
                    Comp("c001", "hinge base", isFixed: true,
                        bboxMin: P(-0.03, -0.02, -0.01), bboxMax: P(0.03, 0.02, 0.008)),
                    Comp("c002", "hinge leaf",
                        bboxMin: P(-0.019, -0.023, 0.0), bboxMax: P(0.049, 0.04, 0.008)),
                },
                Concentric("Concentric1", "c001", "c002", minusZ, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)),
                AngleLimit("LimitAngle1", "c001", "c002",
                    X, P(Math.Cos(0.5235987755983), Math.Sin(0.5235987755983), 0), P(0, 0, 0),
                    min: 0.0, max: 1.30899693899575, current: 0.5235987755983));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Revolute, joint.Type);
            Assert.Equal("high", joint.Confidence);

            // Canonical from the concentric's −Z; the dimension happens to
            // grow about +Z here, so the values stay as read.
            AssertVector(new double[] { 0, 0, 1 }, joint.Axis);
            Assert.NotNull(joint.SecondaryAxis);
            Assert.Equal(0.0, joint.SecondaryAxis[0] * joint.Axis[0]
                + joint.SecondaryAxis[1] * joint.Axis[1]
                + joint.SecondaryAxis[2] * joint.Axis[2], 1e-9);
            Assert.NotNull(joint.RotationLimit);
            Assert.Equal(0.0, joint.RotationLimit.Min, 1e-12);
            Assert.Equal(1.30899693899575, joint.RotationLimit.Max, 1e-12);
            Assert.Equal(0.5235987755983, joint.RotationLimit.ValueAtRest, 1e-12);
            Assert.Empty(result.Warnings);
        }

        /// <summary>The bone direction must be identical no matter the pose
        /// or entity order: the sign along the axis line is a pure function
        /// of the line (live 2026-08-23: the hinge bone flipped with the
        /// export pose).</summary>
        [Fact]
        public void AxisIsCanonicalWithoutLimits()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "hinge base", isFixed: true),
                    Comp("c002", "hinge leaf",
                        bboxMin: P(-0.02, -0.02, 0.0), bboxMax: P(0.05, 0.02, 0.02)),
                },
                Concentric("Concentric1", "c001", "c002",
                    new double[] { 0, 0, -1 }, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            AssertVector(new double[] { 0, 0, 1 }, joint.Axis);
        }

        /// <summary>A limit-angle mate whose faces are parallel in the rest
        /// pose sits at its dimension's zero, where the growth direction is
        /// undefined. The honest output keeps the limit but says the sign is
        /// a guess: confidence drops, axis stays canonical.</summary>
        [Fact]
        public void DegenerateAngleLimitKeepsAxisAndDropsConfidence()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "hinge base", isFixed: true),
                    Comp("c002", "hinge leaf",
                        bboxMin: P(-0.02, -0.02, 0.0), bboxMax: P(0.05, 0.02, 0.02)),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)),
                AngleLimit("LimitAngle1", "c001", "c002", X, X, P(0, 0, 0),
                    min: -0.5, max: 0.5, current: 0.0));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Revolute, joint.Type);
            AssertVector(new double[] { 0, 0, 1 }, joint.Axis);
            Assert.NotNull(joint.RotationLimit);
            Assert.Equal("medium", joint.Confidence);
            Assert.Contains("sign of the limits is a guess", joint.Notes);
        }

        /// <summary>The live hinge4 case (2026-08-22): the pin is held by a
        /// coincident mate between two temporary AXES instead of a concentric.
        /// Kinematically identical (rotate about + slide along the line) but
        /// the old pattern table only knew planes and points on coincidents
        /// and fell through to ball, so the limit was rejected with
        /// LIMIT_AXIS_MISMATCH and the leaf spun freely in every direction.</summary>
        [Fact]
        public void AxisCoincidentHingeIsRevoluteWithLimit()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "hinge base", isFixed: true,
                        bboxMin: P(-0.03, -0.02, -0.01), bboxMax: P(0.03, 0.02, 0.008)),
                    Comp("c002", "hinge leaf",
                        bboxMin: P(-0.02, -0.02, 0.0), bboxMax: P(0.05, 0.02, 0.02)),
                },
                Mate("Coincident2", "swMateCOINCIDENT",
                    AxisEnt("c001", Z, P(0, 0, 0)), AxisEnt("c002", Z, P(0, 0, 0))),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0.01)),
                AngleLimit("LimitAngle1", "c001", "c002",
                    X, P(Math.Cos(0.5235987755983), Math.Sin(0.5235987755983), 0), P(0, 0, 0.01),
                    min: 0.0, max: 1.30899693899575, current: 0.5235987755983));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Revolute, joint.Type);
            AssertVector(new double[] { 0, 0, 1 }, joint.Axis);
            Assert.NotNull(joint.RotationLimit);
            Assert.Equal(0.5235987755983, joint.RotationLimit.ValueAtRest, 1e-12);
            Assert.Empty(result.Warnings);
        }

        /// <summary>A fixed (non-limit) angle mate whose measured directions
        /// run along the pin axis does not block the spin: precession keeps
        /// the angle. The old table treated every angle mate as a rotation
        /// block and called this prismatic.</summary>
        [Fact]
        public void FixedAngleAlongAxisLeavesCylindrical()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "bearing block", isFixed: true),
                    Comp("c002", "shaft"),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)),
                Mate("Angle1", "swMateANGLE",
                    PlaneEnt("c001", Z, P(0, 0, 0)), PlaneEnt("c002", Z, P(0, 0, 0))));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Cylindrical, joint.Type);
            AssertVector(new double[] { 0, 0, 1 }, joint.Axis);
        }

        /// <summary>The prismatic counterpart of the hinge flip: the distance
        /// mate's faces report a −X normal with the block on its positive
        /// side, so the dimension grows along −X and the axis must follow.</summary>
        [Fact]
        public void SliderAxisFollowsTheDistanceDimensionDirection()
        {
            var minusX = new double[] { -1, 0, 0 };
            var graph = Graph(
                new[]
                {
                    Comp("c001", "rail", isFixed: true,
                        bboxMin: P(-0.1, -0.02, -0.02), bboxMax: P(0.1, 0.02, 0.0)),
                    Comp("c002", "block",
                        bboxMin: P(-0.05, -0.01, -0.01), bboxMax: P(0.01, 0.01, 0.01)),
                },
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident2", "c001", "c002", Y, P(0, 0, 0)),
                DistanceLimit("LimitDistance1", "c001", "c002", minusX, P(0.02, 0, 0),
                    min: 0.0, max: 0.1, current: 0.02));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Prismatic, joint.Type);
            // The axis is canonical (+X regardless of the dimension's
            // direction); the dimension grows toward −X, so the VALUES are
            // mirrored into the axis frame instead of flipping the bone.
            AssertVector(new double[] { 1, 0, 0 }, joint.Axis);
            Assert.NotNull(joint.TranslationLimit);
            Assert.Equal(-0.1, joint.TranslationLimit.Min, 1e-12);
            Assert.Equal(0.0, joint.TranslationLimit.Max, 1e-12);
            Assert.Equal(-0.02, joint.TranslationLimit.ValueAtRest, 1e-12);
            Assert.Empty(result.Warnings);
        }

        /// <summary>An anti-aligned coincident reports the plane normal
        /// flipped; parallelism to the axis must be sign-insensitive and the
        /// origin must land on the same plane point.</summary>
        [Fact]
        public void HingeWithFlippedPlaneNormalIsStillRevolute()
        {
            var minusZ = new double[] { 0, 0, -1 };
            var graph = Graph(
                new[]
                {
                    Comp("c001", "hinge base", isFixed: true),
                    Comp("c002", "hinge leaf",
                        bboxMin: P(-0.02, -0.02, 0.0), bboxMax: P(0.05, 0.02, 0.02)),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0.05)),
                CoincidentPlanes("Coincident1", "c001", "c002", minusZ, P(0, 0, 0.01)));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Revolute, joint.Type);
            AssertVector(P(0, 0, 0.01), joint.Origin);
        }

        // ── Slider ──────────────────────────────────────────────────────────

        /// <summary>Two non-parallel plane coincidences leave exactly the
        /// slide along their intersection line; the distance limit measures
        /// along that line and becomes the translation limit.</summary>
        [Fact]
        public void SliderIsPrismaticAlongPlaneIntersectionWithTranslationLimit()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "rail", isFixed: true,
                        bboxMin: P(-0.1, -0.02, -0.02), bboxMax: P(0.1, 0.02, 0.0)),
                    Comp("c002", "block",
                        bboxMin: P(-0.01, -0.01, -0.01), bboxMax: P(0.05, 0.01, 0.01)),
                },
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident2", "c001", "c002", Y, P(0, 0, 0)),
                DistanceLimit("LimitDistance1", "c001", "c002", X, P(0.02, 0, 0),
                    min: 0.0, max: 0.1, current: 0.02));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Prismatic, joint.Type);

            // The intersection of the z=0 and y=0 planes is the X line. The
            // SIGN is not arbitrary: the distance limit measures along +X with
            // the block on the positive side, so the axis must be oriented +X
            // for the dimension to grow with positive travel.
            Assert.NotNull(joint.Axis);
            Assert.Equal(1.0, joint.Axis[0], 1e-9);
            Assert.Equal(0.0, joint.Axis[1], 1e-9);
            Assert.Equal(0.0, joint.Axis[2], 1e-9);
            AssertVector(P(0, 0, 0), joint.Origin);

            Assert.NotNull(joint.TranslationLimit);
            Assert.Equal(0.0, joint.TranslationLimit.Min, 1e-12);
            Assert.Equal(0.1, joint.TranslationLimit.Max, 1e-12);
            Assert.Equal(0.02, joint.TranslationLimit.ValueAtRest, 1e-12);
            Assert.Null(joint.RotationLimit);
            Assert.Empty(result.Warnings);
        }

        /// <summary>SW2URDF's admitted bug was attaching a limit to whatever
        /// DOF existed. A distance limit measured across the slide direction
        /// fits nothing: no limit, LIMIT_AXIS_MISMATCH instead.</summary>
        [Fact]
        public void PerpendicularDistanceLimitIsRejectedWithWarning()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "rail", isFixed: true),
                    Comp("c002", "block"),
                },
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident2", "c001", "c002", Y, P(0, 0, 0)),
                DistanceLimit("LimitDistance1", "c001", "c002", Z, P(0, 0, 0),
                    min: 0.0, max: 0.1, current: 0.02));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Prismatic, joint.Type);
            Assert.Null(joint.TranslationLimit);
            Assert.Null(joint.RotationLimit);

            var warning = Assert.Single(result.Warnings);
            Assert.Equal("LIMIT_AXIS_MISMATCH", warning.Code);
            Assert.Contains(joint.Id, warning.Joints);
        }

        /// <summary>The live slider6 case (2026-08-22): a prismatic origin is
        /// kinematically arbitrary, and the mate planes reported a point off
        /// the moving part: the bone floated above the rail instead of
        /// sitting on the slide. The origin must be the child part's own
        /// origin.</summary>
        [Fact]
        public void PrismaticOriginAnchorsAtTheChildPartOrigin()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "rail", isFixed: true),
                    Comp("c002", "slide", at: P(0.05, 0.0, 0.01)),
                },
                CoincidentPlanes("Coincident1", "c001", "c002", X, P(0, -0.02, 0)),
                CoincidentPlanes("Coincident2", "c001", "c002", Y, P(0, -0.02, 0)));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Prismatic, joint.Type);
            AssertVector(P(0.05, 0.0, 0.01), joint.Origin);
        }

        /// <summary>The live slider2 case (2026-08-22): a single face
        /// coincident leaves a planar joint, and its origin, arbitrary like
        /// the prismatic one, came back as the far corner of the mated face.
        /// Same rule: the child part's origin.</summary>
        [Fact]
        public void PlanarOriginAnchorsAtTheChildPartOrigin()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "rail", isFixed: true),
                    Comp("c002", "slide", at: P(0.05, 0.0, 0.01)),
                },
                CoincidentPlanes("Coincident1", "c001", "c002", Y, P(0.1, -0.01, 0.01)));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Planar, joint.Type);
            AssertVector(P(0.05, 0.0, 0.01), joint.Origin);
        }

        // ── Ball ────────────────────────────────────────────────────────────

        /// <summary>Corpus 04 as the API documents it: a concentric between
        /// two spherical faces pins the centres: a ball, never a pin.</summary>
        [Fact]
        public void SphereConcentricIsBall()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "socket base", isFixed: true),
                    Comp("c002", "ball stud"),
                },
                Mate("Concentric1", "swMateCONCENTRIC",
                    SphereEnt("c001", P(0, 0, 0.02)), SphereEnt("c002", P(0, 0, 0.02))));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Ball, joint.Type);
            Assert.Null(joint.Axis);
            AssertVector(P(0, 0, 0.02), joint.Origin);
            Assert.Empty(result.Warnings);
        }

        /// <summary>The live corpus 04 failure shape (2026-08-22): one side is
        /// a proper sphere, the other arrived under a junk entity kind still
        /// carrying a leftover direction. The sphere must win: trusting the
        /// direction made the ball a cylindrical sliding along world X.</summary>
        [Fact]
        public void SphereBeatsMisreportedDirectionOnTheOtherEntity()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "socket base", isFixed: true),
                    Comp("c002", "ball stud"),
                },
                Mate("Concentric1", "swMateCONCENTRIC",
                    SphereEnt("c001", P(0, 0, 0.02)),
                    UnknownEnt("c002", P(0, 0, 0.02), X)));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Ball, joint.Type);
            Assert.Null(joint.Axis);
            AssertVector(P(0, 0, 0.02), joint.Origin);
        }

        /// <summary>A concentric whose entities are point-typed (a sphere
        /// reported as its centre point) still means pinned centres.</summary>
        [Fact]
        public void PointTypedConcentricFallsBackToBall()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "socket base", isFixed: true),
                    Comp("c002", "ball stud"),
                },
                Mate("Concentric1", "swMateCONCENTRIC",
                    PointEnt("c001", P(0, 0, 0.02)), PointEnt("c002", P(0, 0, 0.02))));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Ball, joint.Type);
            AssertVector(P(0, 0, 0.02), joint.Origin);
        }

        /// <summary>The live ball2 variant (2026-08-22): an origin coincidence
        /// WITHOUT align-axes arrives typed swMateCOINCIDENT with kind-unknown
        /// entities carrying only points (their direction slots hold (1,0,0)
        /// filler, which MateReader drops). One entity sits on the assembly
        /// itself. Pinned origins, free rotation: a ball.</summary>
        [Fact]
        public void CoincidentOriginPointsAreBall()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "socket base", isFixed: true),
                    Comp("c002", "ball stud"),
                },
                Mate("Coincident2", "swMateCOINCIDENT",
                    UnknownEnt("c002", P(0, 0, 0.02)), UnknownEnt(null, P(0, 0, 0.02))));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Ball, joint.Type);
            Assert.Null(joint.Axis);
            AssertVector(P(0, 0, 0.02), joint.Origin);
            Assert.Empty(result.Warnings);
        }

        /// <summary>An angle limit on a ball pair becomes the swing cone: the
        /// consumer applies rotation limits on a ball as a cone about the
        /// rest pose, so the exporter must attach rather than reject it.</summary>
        [Fact]
        public void BallRotationLimitAttachesAsCone()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "socket base", isFixed: true),
                    Comp("c002", "ball stud"),
                },
                Mate("Concentric1", "swMateCONCENTRIC",
                    SphereEnt("c001", P(0, 0, 0.02)), SphereEnt("c002", P(0, 0, 0.02))),
                AngleLimit("LimitAngle1", "c001", "c002", Z,
                    new double[] { 0.5, 0, 0.8660254037844386 }, P(0, 0, 0.02),
                    min: 0.0, max: 0.7853981633974483, current: 0.5235987755982988));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Ball, joint.Type);
            Assert.NotNull(joint.RotationLimit);
            Assert.Equal(0.5235987755982988, joint.RotationLimit.ValueAtRest, 1e-12);
            // The cone frame is the limit mate's own measurement geometry:
            // parent-side direction = cone axis, child-side direction = the
            // vector the [Min, Max] band constrains. Without them the
            // consumer could only fake the cone about the child's rest pose
            // (live corpus 04, 2026-08-23: a tilted export tilted the cone).
            AssertVector(Z, joint.Axis);
            AssertVector(new double[] { 0.5, 0, 0.8660254037844386 }, joint.SecondaryAxis);
            Assert.Empty(result.Warnings);
        }

        /// <summary>The cone axis is NOT canonicalized: its sign says which
        /// way the cone opens, unlike a DOF axis whose sign is convention. A
        /// socket facing −Y must keep its −Y cone.</summary>
        [Fact]
        public void BallConeAxisKeepsItsSign()
        {
            var minusY = new double[] { 0, -1, 0 };
            var graph = Graph(
                new[]
                {
                    Comp("c001", "socket base", isFixed: true),
                    Comp("c002", "ball stud"),
                },
                Mate("Concentric1", "swMateCONCENTRIC",
                    SphereEnt("c001", P(0, 0, 0.02)), SphereEnt("c002", P(0, 0, 0.02))),
                AngleLimit("LimitAngle1", "c001", "c002", minusY, minusY, P(0, 0, 0.02),
                    min: 0.0, max: 0.7853981633974483, current: 0.0));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Ball, joint.Type);
            AssertVector(minusY, joint.Axis);
            AssertVector(minusY, joint.SecondaryAxis);
            // Parked at 0 swing: nothing to resolve for an unsigned band, so
            // no guess note and no mirrored values.
            Assert.Equal(0.0, joint.RotationLimit.Min, 1e-12);
            Assert.Equal(0.7853981633974483, joint.RotationLimit.Max, 1e-12);
            Assert.Equal("high", joint.Confidence);
            Assert.Null(joint.Notes);
        }

        /// <summary>In-sub ball geometry describes the sub DOCUMENT's pose;
        /// a flexed instance transports the measured directions and the rest
        /// angle is recomputed as the actual angle between them. Both parts
        /// sit in the flexible subassembly, as only such a mate describes
        /// the document pose.</summary>
        [Fact]
        public void FlexedBallConeRestFollowsTheInstancePose()
        {
            var sub = Comp("c000", "ball joint", isFixed: true);
            sub.Solving = "flexible";
            var socket = Comp("c001", "socket base");
            socket.ParentId = "c000";
            socket.FixedInSubassembly = true;
            var stud = Comp("c002", "ball stud");
            stud.ParentId = "c000";
            double tilt = 0.3;
            var rx = MathOps.Identity4();
            rx[1, 1] = Math.Cos(tilt); rx[1, 2] = -Math.Sin(tilt);
            rx[2, 1] = Math.Sin(tilt); rx[2, 2] = Math.Cos(tilt);
            stud.MatePoseDelta = rx;

            var graph = Graph(
                new[]
                {
                    sub,
                    socket,
                    stud,
                },
                Mate("Concentric1", "swMateCONCENTRIC",
                    SphereEnt("c001", P(0, 0, 0)), SphereEnt("c002", P(0, 0, 0))),
                AngleLimit("LimitAngle1", "c001", "c002", Z, Z, P(0, 0, 0),
                    min: 0.0, max: 0.7853981633974483, current: 0.0));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Ball, joint.Type);
            AssertVector(Z, joint.Axis);
            AssertVector(new double[] { 0, -Math.Sin(tilt), Math.Cos(tilt) },
                joint.SecondaryAxis);
            Assert.Equal(tilt, joint.RotationLimit.ValueAtRest, 1e-9);
        }

        // ── Cylindrical ─────────────────────────────────────────────────────

        [Fact]
        public void LoneConcentricNonFastenerIsCylindrical()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "bearing block", isFixed: true),
                    Comp("c002", "shaft"),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Cylindrical, joint.Type);
            Assert.Equal("high", joint.Confidence);
            AssertVector(new double[] { 0, 0, 1 }, joint.Axis);
            Assert.Null(joint.RotationLimit);
            Assert.Null(joint.TranslationLimit);
            Assert.Empty(result.Warnings);
        }

        /// <summary>A concentric plus a distance LIMIT mate: the part spins
        /// and slides within a range, which is a cylindrical joint carrying a
        /// translation limit. This used to be "medium" confidence because a
        /// name-based fastener filter had to be talked out of welding it; with
        /// the filter gone the classification is ordinary and so is its
        /// confidence.</summary>
        [Fact]
        public void ConcentricPlusDistanceLimitIsALimitedCylindrical()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "housing", isFixed: true),
                    Comp("c002", "spring pin"),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)),
                DistanceLimit("LimitDistance1", "c001", "c002", Z, P(0, 0, 0),
                    min: 0.0, max: 0.01, current: 0.002));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Cylindrical, joint.Type);
            Assert.Equal("high", joint.Confidence);
            Assert.NotNull(joint.TranslationLimit);
            Assert.Equal(0.002, joint.TranslationLimit.ValueAtRest, 1e-12);
        }

        // ── Under-defined ───────────────────────────────────────────────────

        [Fact]
        public void UnderDefinedPairIsFreeWithWarning()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "base", isFixed: true),
                    Comp("c002", "floater"),
                },
                ParallelPlanes("Parallel1", "c001", "c002", Z, P(0, 0, 0)));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Free, joint.Type);
            Assert.Null(joint.Axis);
            Assert.Null(joint.Origin);

            var warning = Assert.Single(result.Warnings);
            Assert.Equal("UNDER_DEFINED", warning.Code);
            Assert.Contains(joint.Id, warning.Joints);
        }

        // ── Perpendicular mates ─────────────────────────────────────────────

        /// <summary>Live corpus 12 perp1 (2026-08-23): a hinge plus a
        /// REDUNDANT perpendicular (one normal on the hinge axis, rotation
        /// about it keeps the other normal perpendicular forever). The old
        /// rule applied each measured direction as an independent kill, and
        /// since a perpendicular's normals are mutually perpendicular by
        /// construction, one always failed: the pair welded rigid with no
        /// joint and no warning. The union is per MATE: rotation survives
        /// about EITHER of the mate's directions.</summary>
        [Fact]
        public void RedundantPerpendicularKeepsTheHinge()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "hinge base", isFixed: true,
                        bboxMin: P(-0.03, -0.02, -0.01), bboxMax: P(0.03, 0.02, 0.008)),
                    Comp("c002", "hinge leaf",
                        bboxMin: P(-0.02, -0.02, 0.0), bboxMax: P(0.05, 0.02, 0.02)),
                },
                Concentric("Concentric1", "c001", "c002",
                    new double[] { 0, 0, -1 }, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)),
                Mate("Perpendicular1", "swMatePERPENDICULAR",
                    PlaneEnt("c001", Z, P(0, 0, 0.008)),
                    PlaneEnt("c002", new double[] { -1, 0, 0 }, P(-0.01, -0.02, 0))));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Revolute, joint.Type);
            AssertVector(new double[] { 0, 0, 1 }, joint.Axis);
            Assert.Equal("high", joint.Confidence);
            Assert.Empty(result.Warnings);
        }

        /// <summary>Live corpus 12 perp2: the same hinge with a perpendicular
        /// between two SIDE faces, neither normal on the axis, so any spin
        /// changes the measured angle. Genuinely zero DOF: the pair merges
        /// rigid, as SolidWorks behaves.</summary>
        [Fact]
        public void SpinKillingPerpendicularMergesRigid()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "hinge base", isFixed: true),
                    Comp("c002", "hinge leaf"),
                },
                Concentric("Concentric1", "c001", "c002",
                    new double[] { 0, 0, -1 }, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)),
                Mate("Perpendicular2", "swMatePERPENDICULAR",
                    PlaneEnt("c001", new double[] { -1, 0, 0 }, P(-0.03, -0.02, -0.01)),
                    PlaneEnt("c002", new double[] { 0, -1, 0 }, P(-0.01, -0.02, 0))));

            var grouping = RigidGrouper.Group(graph);
            Assert.Single(grouping.Groups);
            Assert.Empty(JointClassifier.Classify(graph, grouping).Joints);
        }

        /// <summary>Live corpus 12 perp3: a perpendicular alone leaves five
        /// DOF no pattern covers, free plus the honest note, counted as ONE
        /// unmodelled mate (the flattened-directions rule counted each
        /// normal separately and said "2 mate(s)" for one).</summary>
        [Fact]
        public void LonePerpendicularIsFreeCountingOneMate()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "hinge base", isFixed: true),
                    Comp("c002", "hinge leaf"),
                },
                Mate("Perpendicular2", "swMatePERPENDICULAR",
                    PlaneEnt("c001", new double[] { -1, 0, 0 }, P(-0.03, -0.02, -0.01)),
                    PlaneEnt("c002", new double[] { 0, -1, 0 }, P(-0.011, -0.011, 0.008))));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Free, joint.Type);
            Assert.Equal("medium", joint.Confidence);
            Assert.Contains("1 mate(s) with no motion model", joint.Notes);
        }

        // ── Gear coupling ───────────────────────────────────────────────────

        /// <summary>Two revolutes on the frame plus a gear mate between the
        /// wheels: the gear edge itself is no joint, the annotation lands on
        /// the driven wheel's mount joint.</summary>
        [Fact]
        public void GearPairAnnotatesDrivenJointWithRatio()
        {
            var gearMate = Mate("Gear1", "swMateGEAR",
                Cylinder("c002", Z, P(0, 0, 0), radius: 0.02),
                Cylinder("c003", Z, P(0.06, 0, 0), radius: 0.04));
            gearMate.CouplingRatio = -2.0;

            var graph = Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    Comp("c002", "gear small"),
                    Comp("c003", "gear large"),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)),
                Concentric("Concentric2", "c001", "c003", Z, P(0.06, 0, 0)),
                CoincidentPlanes("Coincident2", "c001", "c003", Z, P(0.06, 0, 0)),
                gearMate);

            var result = Run(graph);

            Assert.Equal(2, result.Joints.Count);
            var driver = result.Joints[0];
            var driven = result.Joints[1];
            Assert.Equal(JointType.Revolute, driver.Type);
            Assert.Equal(JointType.Revolute, driven.Type);
            Assert.Null(driver.Coupling);

            Assert.NotNull(driven.Coupling);
            Assert.Equal("gear", driven.Coupling.Kind);
            Assert.Equal(driver.Id, driven.Coupling.DriverJoint);
            Assert.Equal(-2.0, driven.Coupling.Ratio);
            Assert.Contains(driven.SourceMates, s => s.SwFeature == "Gear1");
            Assert.Empty(result.Warnings);
        }

        /// <summary>Live corpus 08 (2026-08-22), exactly as recorded: the
        /// large gear was mate entity 1 with num:den = 1:2: the ANGULAR
        /// ratio θ(e1):θ(e2), and Reverse off, and the pair still rotated
        /// the SAME way at HALF speed in Blender. The driven (small) side
        /// must follow at den/num, negated for the external mesh.</summary>
        [Fact]
        public void GearRatioFromLiveEntityOrderCounterRotates()
        {
            var gearMate = Mate("GearMate1", "swMateGEAR",
                Cylinder("c002", Z, P(-0.015, 0, 0.02), radius: 0.02),
                Cylinder("c003", Z, P(0.015, 0, 0.02), radius: 0.01));
            gearMate.CouplingNumerator = 1.0;
            gearMate.CouplingDenominator = 2.0;
            gearMate.CouplingReverse = false;

            var graph = Graph(
                new[]
                {
                    Comp("c001", "gear plate", isFixed: true),
                    Comp("c002", "gear large"),
                    Comp("c003", "gear small"),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(-0.015, 0, 0.02)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(-0.015, 0, 0.01)),
                Concentric("Concentric2", "c001", "c003", Z, P(0.015, 0, 0.02)),
                CoincidentPlanes("Coincident2", "c001", "c003", Z, P(0.015, 0, 0.01)),
                gearMate);

            var result = Run(graph);

            Assert.Equal(2, result.Joints.Count);
            var driver = result.Joints[0];    // mounts the large gear
            var driven = result.Joints[1];
            Assert.Null(driver.Coupling);
            Assert.NotNull(driven.Coupling);
            Assert.Equal(driver.Id, driven.Coupling.DriverJoint);
            Assert.Equal(-2.0, driven.Coupling.Ratio.Value, 9);
        }

        /// <summary>Reverse ticked is the internal-mesh/same-way case; and
        /// anti-parallel mount axes flip the bone-local relation once more.
        /// Both flips together cancel back to a negative ratio.</summary>
        [Fact]
        public void GearRatioFollowsReverseAndAxisSense()
        {
            var gearMate = Mate("GearMate1", "swMateGEAR",
                Cylinder("c002", Z, P(-0.015, 0, 0.02), radius: 0.02),
                Cylinder("c003", Z, P(0.015, 0, 0.02), radius: 0.01));
            gearMate.CouplingNumerator = 1.0;
            gearMate.CouplingDenominator = 2.0;
            gearMate.CouplingReverse = true;

            var minusZ = new double[] { 0, 0, -1 };
            var graph = Graph(
                new[]
                {
                    Comp("c001", "gear plate", isFixed: true),
                    Comp("c002", "gear large"),
                    Comp("c003", "gear small"),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(-0.015, 0, 0.02)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(-0.015, 0, 0.01)),
                Concentric("Concentric2", "c001", "c003", minusZ, P(0.015, 0, 0.02)),
                CoincidentPlanes("Coincident2", "c001", "c003", Z, P(0.015, 0, 0.01)),
                gearMate);

            var result = Run(graph);
            var driven = result.Joints[1];
            Assert.NotNull(driven.Coupling);
            // Both mount axes canonicalize to +Z (the small gear's concentric
            // reported −Z), so the ratio sign flips with the axis to keep the
            // same physical counter-rotation: reverse: +, parallel axes: +.
            AssertVector(new double[] { 0, 0, 1 }, result.Joints[0].Axis);
            AssertVector(new double[] { 0, 0, 1 }, driven.Axis);
            Assert.Equal(2.0, driven.Coupling.Ratio.Value, 9);
        }

        /// <summary>Live corpus 07 flexible-sub2 (2026-08-22): the hinge
        /// flexed from the document's 30° to 75°, but the limit dimension is
        /// read through the sub document and still said 30°. Blender then
        /// allowed +45° past the limit. The child's MatePoseDelta (actual =
        /// delta × document pose) must shift value_at_rest by the rotation
        /// about the joint axis; the limit range itself never moves.</summary>
        [Fact]
        public void FlexedSubLimitRestFollowsTheInstancePose()
        {
            var subFrame = Comp("c001", "hinge");
            subFrame.Solving = "flexible";
            var fixedBase = Comp("c002", "hinge base");
            fixedBase.ParentId = "c001";
            fixedBase.FixedInSubassembly = true;
            var leaf = Comp("c003", "hinge leaf");
            leaf.ParentId = "c001";
            leaf.MatePoseDelta = RotationAboutZThrough(Math.PI / 4.0, 0.04, 0.02);

            var graph = Graph(
                new[]
                {
                    subFrame,
                    fixedBase,
                    leaf,
                    Comp("c004", "baseplate", isFixed: true),
                },
                CoincidentPlanes("Coincident1", "c004", "c002", Z, P(0.04, 0.02, 0.01)),
                CoincidentPlanes("Coincident4", "c004", "c002", Y, P(0.01, 0, 0.01)),
                Mate("Distance2", "swMateDISTANCE",
                    PlaneEnt("c001", X, P(0.04, 0.02, 0.02)),
                    PlaneEnt("c004", X, P(0, 0, 0.01))),
                Concentric("Concentric1", "c002", "c003", Z, P(0.04, 0.02, 0.028)),
                CoincidentPlanes("CoincidentSub", "c001", "c003", Z, P(0.04, 0.02, 0.02)),
                AngleLimit("LimitAngle1", "c001", "c003",
                    X, P(0.76099, 0.64877, 0), P(0.04, 0.02, 0.02),
                    min: 0.0, max: 1.5708, current: 0.5236));

            var result = Run(graph);
            var joint = Assert.Single(result.Joints);
            Assert.NotNull(joint.RotationLimit);
            Assert.Equal(0.0, joint.RotationLimit.Min);
            Assert.Equal(1.5708, joint.RotationLimit.Max);
            // The axis was oriented so the dimension grows with +rotation, so
            // the +45° flex lands as +45° of value_at_rest.
            Assert.Equal(0.5236 + Math.PI / 4.0, joint.RotationLimit.ValueAtRest, 9);
        }

        private static double[,] RotationAboutZThrough(double angle, double px, double py)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle);
            var m = MathOps.Identity4();
            m[0, 0] = c; m[0, 1] = -s;
            m[1, 0] = s; m[1, 1] = c;
            // t = (I − R) p for a rotation about the line through p along Z.
            m[0, 3] = px - (c * px - s * py);
            m[1, 3] = py - (s * px + c * py);
            return m;
        }

        // ── Degenerate limit sense: the flexed-range and oracle rungs ───────

        private static MateGraph DegenerateFlexedHinge(
            double flexAngle, double min, double max, double current)
        {
            var subFrame = Comp("c001", "hinge");
            subFrame.Solving = "flexible";
            var fixedBase = Comp("c002", "hinge base");
            fixedBase.ParentId = "c001";
            fixedBase.FixedInSubassembly = true;
            var leaf = Comp("c003", "hinge leaf");
            leaf.ParentId = "c001";
            if (flexAngle != 0.0)
                leaf.MatePoseDelta = RotationAboutZThrough(flexAngle, 0.04, 0.02);

            return Graph(
                new[]
                {
                    subFrame,
                    fixedBase,
                    leaf,
                    Comp("c004", "baseplate", isFixed: true),
                },
                CoincidentPlanes("Coincident1", "c004", "c002", Z, P(0.04, 0.02, 0.01)),
                CoincidentPlanes("Coincident4", "c004", "c002", Y, P(0.01, 0, 0.01)),
                Mate("Distance2", "swMateDISTANCE",
                    PlaneEnt("c001", X, P(0.04, 0.02, 0.02)),
                    PlaneEnt("c004", X, P(0, 0, 0.01))),
                // −Z on purpose: the as-found axis must need the flip so the
                // tests can SEE the resolution happen.
                Concentric("Concentric1", "c002", "c003",
                    new double[] { 0, 0, -1 }, P(0.04, 0.02, 0.028)),
                CoincidentPlanes("CoincidentSub", "c001", "c003", Z, P(0.04, 0.02, 0.02)),
                // Parallel measurement faces: the doc pose rests AT the stop,
                // where the geometric sign is undefined.
                AngleLimit("LimitAngle1", "c001", "c003", X, X, P(0.04, 0.02, 0.02),
                    min: min, max: max, current: current));
        }

        /// <summary>Live corpus 07 (2026-08-23): the sub DOCUMENT rests at
        /// its 0° stop (measurement faces parallel, geometric sign
        /// undefined), the instance is flexed +40.45°. Only ONE axis sense
        /// puts the flexed dimension inside the 0..75° range, so the sense is
        /// proven without any guess: the wrong sense rigged the leaf −40°
        /// out of range and Blender's limit constraint snapped it to the
        /// rest pose.</summary>
        [Fact]
        public void FlexedInstanceResolvesDegenerateLimitSign()
        {
            const double flex = 0.7059647539615261;
            var graph = DegenerateFlexedHinge(
                flex, min: 0.0, max: 1.30899693899575, current: 0.0);

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Revolute, joint.Type);
            // Flipped from the concentric's −Z: only +Z keeps the flexed
            // pose inside the limit range.
            AssertVector(new double[] { 0, 0, 1 }, joint.Axis);
            Assert.Equal("high", joint.Confidence);
            Assert.Null(joint.Notes);
            Assert.NotNull(joint.RotationLimit);
            Assert.Equal(flex, joint.RotationLimit.ValueAtRest, 9);
        }

        /// <summary>A small flex lands both senses inside the range: the
        /// range check alone proves nothing, but the flexed pose has
        /// rotated the measurement faces off parallel, so the geometry
        /// evaluated AT THE ACTUAL POSE resolves the sense (live
        /// 2026-08-23: the flexible hinge's limit direction depended on
        /// where the leaf happened to sit at export).</summary>
        [Fact]
        public void ActualPoseGeometryResolvesWhereRangeIsAmbiguous()
        {
            var graph = DegenerateFlexedHinge(
                0.3, min: 0.0, max: 1.30899693899575, current: 0.65);

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            AssertVector(new double[] { 0, 0, 1 }, joint.Axis);
            Assert.Equal("high", joint.Confidence);
            Assert.Null(joint.Notes);
        }

        /// <summary>A pure-translation flex keeps the faces parallel: every
        /// offline rung fails and the honest guess note must survive.</summary>
        [Fact]
        public void TranslatedFlexKeepsTheGuessNote()
        {
            var graph = DegenerateFlexedHinge(
                0.0, min: 0.0, max: 1.30899693899575, current: 0.0);
            var leaf = graph.Components.Find(c => c.Id == "c003");
            var slide = MathOps.Identity4();
            slide[0, 3] = 0.001;
            leaf.MatePoseDelta = slide;

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            // The axis is canonical even when the sense is unresolved: the
            // bone never flips, only the limit values stay a guess.
            AssertVector(new double[] { 0, 0, 1 }, joint.Axis);
            Assert.Equal("medium", joint.Confidence);
            Assert.Contains("sign of the limits is a guess", joint.Notes);
        }

        private sealed class StubOracle : ILimitSignOracle
        {
            public int Sign;
            public int Calls;
            public bool? LastRotational;
            public int ResolveSign(RigJoint joint, GraphMate mate, bool rotational)
            {
                Calls++;
                LastRotational = rotational;
                return Sign;
            }
        }

        /// <summary>Live corpus 01 (2026-08-23): a top-level hinge dragged to
        /// its horizontal stop and exported, degenerate pose, no flexed
        /// instance to learn from. The oracle (live SolidWorks perturbs and
        /// reads the dimension) is the last rung; its verdict orients the
        /// axis and clears the guess.</summary>
        [Fact]
        public void OracleResolvesTopLevelDegenerateLimitSign()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "hinge base", isFixed: true,
                        bboxMin: P(-0.03, -0.02, -0.01), bboxMax: P(0.03, 0.02, 0.008)),
                    Comp("c002", "hinge leaf",
                        bboxMin: P(-0.02, -0.02, 0.0), bboxMax: P(0.05, 0.02, 0.02)),
                },
                Concentric("Concentric1", "c001", "c002",
                    new double[] { 0, 0, -1 }, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)),
                AngleLimit("LimitAngle1", "c001", "c002", X, X, P(0.0175, 0, 0.008),
                    min: 0.0, max: 1.30899693899575, current: 0.0));

            var oracle = new StubOracle { Sign = -1 };
            var result = JointClassifier.Classify(graph, RigidGrouper.Group(graph), oracle);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(1, oracle.Calls);
            Assert.True(oracle.LastRotational);
            // The axis stays canonical; the oracle's left-handed verdict
            // mirrors the limit VALUES into the axis frame instead.
            AssertVector(new double[] { 0, 0, 1 }, joint.Axis);
            Assert.Equal(-1.30899693899575, joint.RotationLimit.Min, 1e-12);
            Assert.Equal(0.0, joint.RotationLimit.Max, 1e-12);
            Assert.Equal(0.0, joint.RotationLimit.ValueAtRest, 1e-12);
            Assert.Equal("high", joint.Confidence);
            Assert.Null(joint.Notes);

            // An oracle that cannot resolve either leaves the honest guess.
            oracle = new StubOracle { Sign = 0 };
            result = JointClassifier.Classify(graph, RigidGrouper.Group(graph), oracle);
            joint = Assert.Single(result.Joints);
            Assert.Equal(1, oracle.Calls);
            Assert.Equal("medium", joint.Confidence);
            Assert.Contains("sign of the limits is a guess", joint.Notes);
        }

        /// <summary>Live corpus 01 hinge5 (2026-08-23): same assembly as the
        /// hinge, parked at the same degenerate stop, the ONLY difference the
        /// "Flip dimension" tick on the limit-angle mate, in SolidWorks it
        /// opens the other way. Every rung fails at the stop; the tick picks
        /// the mirrored branch of the guess, so the two assemblies export
        /// opposite limit values on the SAME canonical axis.</summary>
        [Fact]
        public void FlippedDimensionMirrorsTheGuessAtTheDegenerateStop()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "hinge base", isFixed: true,
                        bboxMin: P(-0.03, -0.02, -0.01), bboxMax: P(0.03, 0.02, 0.008)),
                    Comp("c002", "hinge leaf",
                        bboxMin: P(-0.02, -0.02, 0.0), bboxMax: P(0.05, 0.02, 0.02)),
                },
                Concentric("Concentric1", "c001", "c002",
                    new double[] { 0, 0, -1 }, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)),
                WithFlippedDimension(
                    AngleLimit("LimitAngle1", "c001", "c002", X, X, P(0.0175, 0, 0.008),
                        min: 0.0, max: 1.30899693899575, current: 0.0)));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            AssertVector(new double[] { 0, 0, 1 }, joint.Axis);
            Assert.Equal(-1.30899693899575, joint.RotationLimit.Min, 1e-12);
            Assert.Equal(0.0, joint.RotationLimit.Max, 1e-12);
            Assert.Equal(0.0, joint.RotationLimit.ValueAtRest, 1e-12);
            // Still the guess baseline (the unflipped sense at a fully parked
            // pose is a convention pinned on one live corpus), so the note
            // and confidence stay honest.
            Assert.Equal("medium", joint.Confidence);
            Assert.Contains("sign of the limits is a guess", joint.Notes);
        }

        /// <summary>At any READABLE pose a flipped mate's faces have solved
        /// to the other side, so the geometric sign already reports the
        /// flipped sense from the entities themselves: the tick must never
        /// be multiplied on top of a geometric verdict.</summary>
        [Fact]
        public void FlippedDimensionDoesNotDoubleApplyWhereGeometryResolves()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "hinge base", isFixed: true,
                        bboxMin: P(-0.03, -0.02, -0.01), bboxMax: P(0.03, 0.02, 0.008)),
                    Comp("c002", "hinge leaf",
                        bboxMin: P(-0.019, -0.023, 0.0), bboxMax: P(0.049, 0.04, 0.008)),
                },
                Concentric("Concentric1", "c001", "c002",
                    new double[] { 0, 0, -1 }, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)),
                WithFlippedDimension(
                    AngleLimit("LimitAngle1", "c001", "c002",
                        X, P(Math.Cos(0.5235987755983), Math.Sin(0.5235987755983), 0), P(0, 0, 0),
                        min: 0.0, max: 1.30899693899575, current: 0.5235987755983)));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            AssertVector(new double[] { 0, 0, 1 }, joint.Axis);
            // Identical to the unflipped readable case: geometry wins.
            Assert.Equal(0.0, joint.RotationLimit.Min, 1e-12);
            Assert.Equal(1.30899693899575, joint.RotationLimit.Max, 1e-12);
            Assert.Equal(0.5235987755983, joint.RotationLimit.ValueAtRest, 1e-12);
            Assert.Equal("high", joint.Confidence);
            Assert.Null(joint.Notes);
        }

        /// <summary>The oracle mutates the live model, so it must not run
        /// when a cheaper rung already resolved the sense.</summary>
        [Fact]
        public void OracleIsNotConsultedWhenGeometryResolves()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "hinge base", isFixed: true,
                        bboxMin: P(-0.03, -0.02, -0.01), bboxMax: P(0.03, 0.02, 0.008)),
                    Comp("c002", "hinge leaf",
                        bboxMin: P(-0.019, -0.023, 0.0), bboxMax: P(0.049, 0.04, 0.008)),
                },
                Concentric("Concentric1", "c001", "c002",
                    new double[] { 0, 0, -1 }, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)),
                AngleLimit("LimitAngle1", "c001", "c002",
                    X, P(Math.Cos(0.5235987755983), Math.Sin(0.5235987755983), 0), P(0, 0, 0),
                    min: 0.0, max: 1.30899693899575, current: 0.5235987755983));

            var oracle = new StubOracle { Sign = 1 };
            var result = JointClassifier.Classify(graph, RigidGrouper.Group(graph), oracle);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(0, oracle.Calls);
            AssertVector(new double[] { 0, 0, 1 }, joint.Axis);
        }

        // ── Tangent contacts: carrier decomposition ─────────────────────────

        /// <summary>Live corpus 11 tangent1 (2026-08-22, raw log geometry): a
        /// puck lying on its side on a plate, held by ONE tangent mate. The
        /// residual is two slides plus two independent spins: no single
        /// joint, so the contact splits into planar(plate→carrier) then
        /// revolute(carrier→puck, its own axis).</summary>
        [Fact]
        public void TangentCylinderOnPlaneSplitsThroughCarrier()
        {
            var cylDir = new[] { 0.99095, -0.13425, 0.0 };
            var graph = Graph(
                new[]
                {
                    Comp("c001", "base plate", isFixed: true),
                    Comp("c002", "puck"),
                },
                Mate("Tangent1", "swMateTANGENT",
                    PlaneEnt("c001", Z, P(0, 0, 0.01)),
                    Cylinder("c002", cylDir, P(0.0020508, 0.0042856, 0.025), radius: 0.015)));

            var result = Run(graph);

            var carrier = Assert.Single(result.VirtualGroups);
            Assert.Equal("g002", carrier.Id);
            Assert.Empty(carrier.Components);
            Assert.False(carrier.Grounded);

            Assert.Equal(2, result.Joints.Count);
            var planar = result.Joints[0];
            var spin = result.Joints[1];

            Assert.Equal(JointType.Planar, planar.Type);
            Assert.Equal("g000", planar.ParentGroup);
            Assert.Equal("g002", planar.ChildGroup);
            AssertVector(new[] { 0.0, 0.0, 1.0 }, planar.Axis, 1e-9);
            Assert.Equal(0.01, planar.Origin[2], 9);

            Assert.Equal(JointType.Revolute, spin.Type);
            Assert.Equal("g002", spin.ParentGroup);
            Assert.Equal("g001", spin.ChildGroup);
            Assert.Equal(0.0, MathOps.Norm(MathOps.Cross(spin.Axis, MathOps.Normalized(cylDir))), 6);
            AssertVector(new[] { 0.0020508, 0.0042856, 0.025 }, spin.Origin, 1e-9);

            Assert.Empty(result.Warnings);

            // The carrier chain is ordinary tree material for the loop pass.
            var groups = new List<RigidGroup>(RigidGrouper.Group(graph).Groups);
            groups.AddRange(result.VirtualGroups);
            var loops = LoopAnalyzer.Analyze(groups, result.Joints);
            Assert.Empty(loops.Loops);
        }

        /// <summary>Live corpus 11 tangent2 (2026-08-22): two face-mated
        /// discs tangent rim to rim. The puck orbits the base disc AND spins
        /// (two rotations about offset parallel axes), so the pair becomes
        /// revolute(base axis) then revolute(puck axis) through a carrier.</summary>
        [Fact]
        public void TangentDiscOnDiscBecomesOrbitPlusSpin()
        {
            var minusZ = new double[] { 0, 0, -1 };
            var graph = Graph(
                new[]
                {
                    Comp("c001", "base disc", isFixed: true),
                    Comp("c002", "puck disc"),
                },
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0.01)),
                Mate("Tangent2", "swMateTANGENT",
                    Cylinder("c001", minusZ, P(0, 0, 0.01), radius: 0.05),
                    Cylinder("c002", Z, P(0.046612, 0.045303, 0), radius: 0.015)));

            var result = Run(graph);

            var carrier = Assert.Single(result.VirtualGroups);
            Assert.Equal(2, result.Joints.Count);
            var orbit = result.Joints[0];
            var spin = result.Joints[1];

            Assert.Equal(JointType.Revolute, orbit.Type);
            Assert.Equal("g000", orbit.ParentGroup);
            Assert.Equal(carrier.Id, orbit.ChildGroup);
            AssertVector(new[] { 0.0, 0.0, 0.0 }, new[] { orbit.Origin[0], orbit.Origin[1], 0.0 }, 1e-9);
            Assert.Equal(1.0, Math.Abs(orbit.Axis[2]), 9);

            Assert.Equal(JointType.Revolute, spin.Type);
            Assert.Equal(carrier.Id, spin.ParentGroup);
            Assert.Equal("g001", spin.ChildGroup);
            AssertVector(new[] { 0.046612, 0.045303, 0.0 }, spin.Origin, 1e-9);
            Assert.Equal(1.0, Math.Abs(spin.Axis[2]), 9);

            Assert.Empty(result.Warnings);
        }

        /// <summary>A vertex coincident with a face: two slides on the plane
        /// plus a full ball of rotation, planar(plane side) then ball(the
        /// vertex) through a carrier.</summary>
        [Fact]
        public void VertexOnFaceSplitsIntoPlanarPlusBall()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "table", isFixed: true),
                    Comp("c002", "wobbler"),
                },
                Mate("Coincident1", "swMateCOINCIDENT",
                    PlaneEnt("c001", Z, P(0, 0, 0.01)),
                    PointEnt("c002", P(0.03, 0.02, 0.01))));

            var result = Run(graph);
            var carrier = Assert.Single(result.VirtualGroups);
            Assert.Equal(2, result.Joints.Count);
            Assert.Equal(JointType.Planar, result.Joints[0].Type);
            Assert.Equal("g000", result.Joints[0].ParentGroup);
            Assert.Equal(carrier.Id, result.Joints[0].ChildGroup);
            AssertVector(new[] { 0.0, 0.0, 1.0 }, result.Joints[0].Axis);
            Assert.Equal(JointType.Ball, result.Joints[1].Type);
            Assert.Null(result.Joints[1].Axis);
            AssertVector(new[] { 0.03, 0.02, 0.01 }, result.Joints[1].Origin);
            Assert.Empty(result.Warnings);
        }

        /// <summary>Live corpus 15 cone1 (2026-08-23): concentric between
        /// two conical faces leaves the slide alive in SolidWorks, plain
        /// cylindrical, full confidence, no honesty note (the old
        /// "cones probably pin the apex" flag is retired by the live pin).</summary>
        [Fact]
        public void ConeConcentricIsCylindricalWithoutHonestyFlag()
        {
            var minusY = new double[] { 0, -1, 0 };
            var graph = Graph(
                new[]
                {
                    Comp("c001", "cone plug"),
                    Comp("c002", "cone base", isFixed: true),
                },
                Mate("Concentric1", "swMateCONCENTRIC",
                    ConeEnt("c002", minusY, P(0, 0, 0), 0.34907),
                    ConeEnt("c001", minusY, P(0, 0.039099, 0), 0.34907)));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Cylindrical, joint.Type);
            AssertVector(new double[] { 0, 1, 0 }, joint.Axis);
            Assert.Equal("high", joint.Confidence);
            Assert.Null(joint.Notes);
            Assert.Empty(result.Warnings);
        }

        /// <summary>Live corpus 15 cone3 (2026-08-23, raw log geometry): a
        /// cone lying tangent on a plate. Like the cylinder it slides and
        /// yaws on the plane and spins about its own axis, but the axis is
        /// tilted OUT of the plane by exactly the half-angle, which is where
        /// the carrier gate must sit (the in-plane gate rejected it and the
        /// pair exported free).</summary>
        [Fact]
        public void TangentConeOnPlaneSplitsIntoPlanarPlusTiltedSpin()
        {
            // The live numbers at full precision, straight off the export:
            // rounding the axis to five places moves the apex by 4e-7 m, and
            // the whole point of this test is that the apex lands exactly.
            var coneAxis = new[]
            {
                -0.8994102715858506, -0.272182631564461, -0.3420201433256696,
            };
            var graph = Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    Comp("c002", "cone"),
                },
                Mate("Tangent1", "swMateTANGENT",
                    ConeEnt("c002", coneAxis,
                            P(0.01563422574668991, 0.019435308129904547,
                              0.019396926207859044),
                            0.3490658503988659),
                    PlaneEnt("c001", Z, P(0, 0, 0.01))));

            var result = Run(graph);

            var carrier = Assert.Single(result.VirtualGroups);
            Assert.Equal(2, result.Joints.Count);
            var planar = result.Joints[0];
            var spin = result.Joints[1];
            Assert.Equal(JointType.Planar, planar.Type);
            Assert.Equal("g000", planar.ParentGroup);
            Assert.Equal(carrier.Id, planar.ChildGroup);
            AssertVector(new double[] { 0, 0, 1 }, planar.Axis);
            Assert.Equal(JointType.Revolute, spin.Type);
            Assert.Equal(carrier.Id, spin.ParentGroup);
            Assert.Equal("g001", spin.ChildGroup);
            // Pointing INTO the cone, not back down through the plate: the
            // bone sits at the apex, so the raw axis would run the wrong way.
            AssertVector(new[] { -coneAxis[0], -coneAxis[1], -coneAxis[2] },
                         spin.Axis, 1e-9);

            // Both halves are anchored on the APEX: where the axis meets the
            // plate. Everything the contact permits turns about that point,
            // and the mate entity's own point is the base-circle centre,
            // 27.5 mm away and 9.4 mm above the plate.
            var apex = new[] { -0.0090768683733867539, 0.011957151787993635, 0.01 };
            AssertVector(apex, spin.Origin, 1e-9);
            AssertVector(apex, planar.Origin, 1e-9);
            Assert.Empty(result.Warnings);
        }

        /// <summary>The apex is only the apex while the cone is LYING on the
        /// plane. A cone whose axis is square to the plane has no tangency to
        /// describe and must keep the ordinary anchor rather than divide by a
        /// vanishing denominator.</summary>
        [Fact]
        public void AConeSquareToThePlaneKeepsTheOrdinaryAnchor()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    Comp("c002", "cone"),
                },
                Mate("Tangent1", "swMateTANGENT",
                    ConeEnt("c002", X, P(0.02, 0.0, 0.05), 0.34907),
                    PlaneEnt("c001", Z, P(0, 0, 0.01))));

            var result = Run(graph);

            foreach (var j in result.Joints)
                Assert.All(j.Origin ?? new double[3],
                           v => Assert.True(!double.IsNaN(v) && !double.IsInfinity(v)));
        }

        /// <summary>Live corpus 16 pt1 (2026-08-23): the vertex arrived as
        /// ReferenceType 0: the "vertex" string was dead and the pair
        /// classified planar (two invented dead rotations). With the
        /// selection-type naming it splits like the typed-point twin above.</summary>
        [Fact]
        public void LiveVertexOnFaceSplitsIntoPlanarPlusBall()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    Comp("c002", "slide"),
                },
                Mate("Coincident1", "swMateCOINCIDENT",
                    PlaneEnt("c001", Z, P(0, 0, 0.01)),
                    VertexEnt("c002", P(0.018838, -0.0072042, 0.01))));

            var result = Run(graph);

            Assert.Single(result.VirtualGroups);
            Assert.Equal(2, result.Joints.Count);
            Assert.Equal(JointType.Planar, result.Joints[0].Type);
            Assert.Equal(JointType.Ball, result.Joints[1].Type);
            AssertVector(new[] { 0.018838, -0.0072042, 0.01 }, result.Joints[1].Origin);
            Assert.Empty(result.Warnings);
        }

        /// <summary>Live corpus 13 dist4 (2026-08-23): a vertex held at a
        /// DISTANCE from a face (hovering), splits exactly like the
        /// coincident twin: planar at the offset plus the ball at the
        /// vertex. It exported free/UNDER_DEFINED while the vertex was
        /// typeless.</summary>
        [Fact]
        public void VertexDistanceFromFaceSplitsIntoPlanarPlusBall()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "slide"),
                    Comp("c002", "plate", isFixed: true),
                },
                Mate("Distance4", "swMateDISTANCE",
                    VertexEnt("c001", P(0.018838, -0.0072042, 0.015)),
                    PlaneEnt("c002", Z, P(0, 0, 0.01))));

            var result = Run(graph);

            Assert.Single(result.VirtualGroups);
            Assert.Equal(2, result.Joints.Count);
            Assert.Equal(JointType.Planar, result.Joints[0].Type);
            Assert.Equal(JointType.Ball, result.Joints[1].Type);
            AssertVector(new[] { 0.018838, -0.0072042, 0.015 }, result.Joints[1].Origin);
            Assert.Empty(result.Warnings);
        }

        /// <summary>Live corpus 16 pt2 (2026-08-23): vertex on an edge. The
        /// edge itself arrived as a directionless point (the bead's rail
        /// vanished and the pair became a ball at the rail's endpoint);
        /// MateReader now recovers the direction from the underlying curve,
        /// and the bead rides the line: prismatic + ball through a carrier.</summary>
        [Fact]
        public void VertexOnRecoveredEdgeIsPrismaticPlusBall()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    Comp("c002", "slide"),
                },
                Mate("Coincident3", "swMateCOINCIDENT",
                    EdgeEnt("c001", X, P(-0.05, -0.05, 0.01)),
                    VertexEnt("c002", P(-0.0070289, -0.05, 0.01))));

            var result = Run(graph);

            Assert.Single(result.VirtualGroups);
            Assert.Equal(2, result.Joints.Count);
            Assert.Equal(JointType.Prismatic, result.Joints[0].Type);
            AssertVector(X, result.Joints[0].Axis);
            // The prismatic anchors at the VERTEX, not the edge endpoint.
            AssertVector(new[] { -0.0070289, -0.05, 0.01 }, result.Joints[0].Origin);
            Assert.Equal(JointType.Ball, result.Joints[1].Type);
            AssertVector(new[] { -0.0070289, -0.05, 0.01 }, result.Joints[1].Origin);
            Assert.Empty(result.Warnings);
        }

        /// <summary>Live corpus 16 pt3 (2026-08-23): a vertex riding a
        /// cylindrical FACE keeps the spin about the axis AND the slide
        /// along it (SolidWorks lets it orbit, slide and tumble): the
        /// cylinder side is a CYLINDRICAL carrier primitive, anchored at the
        /// vertex's foot on the axis, plus the ball at the vertex.</summary>
        [Fact]
        public void VertexOnCylinderFaceIsCylindricalPlusBall()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "round plate", isFixed: true),
                    Comp("c002", "slide"),
                },
                Mate("Coincident4", "swMateCOINCIDENT",
                    VertexEnt("c002", P(0.027647, -0.041661, 0.0047036)),
                    Cylinder("c001", new double[] { 0, 0, -1 }, P(0, 0, 0.01), radius: 0.05)));

            var result = Run(graph);

            Assert.Single(result.VirtualGroups);
            Assert.Equal(2, result.Joints.Count);
            var surface = result.Joints[0];
            var ball = result.Joints[1];
            Assert.Equal(JointType.Cylindrical, surface.Type);
            Assert.Equal(1.0, Math.Abs(surface.Axis[2]), 9);
            AssertVector(new[] { 0.0, 0.0, 0.0047036 }, surface.Origin, 1e-9);
            Assert.Equal(JointType.Ball, ball.Type);
            AssertVector(new[] { 0.027647, -0.041661, 0.0047036 }, ball.Origin);
            Assert.Empty(result.Warnings);
        }

        /// <summary>A vertex coincident with an axis slides ALONG the line
        /// (unlike an offset contact, which orbits it): prismatic then
        /// ball.</summary>
        [Fact]
        public void VertexOnAxisSplitsIntoPrismaticPlusBall()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "rail", isFixed: true),
                    Comp("c002", "bead"),
                },
                Mate("Coincident1", "swMateCOINCIDENT",
                    AxisEnt("c001", X, P(0, 0, 0.05)),
                    PointEnt("c002", P(0.02, 0, 0.05))));

            var result = Run(graph);
            Assert.Single(result.VirtualGroups);
            Assert.Equal(2, result.Joints.Count);
            Assert.Equal(JointType.Prismatic, result.Joints[0].Type);
            AssertVector(new[] { 1.0, 0.0, 0.0 }, result.Joints[0].Axis);
            AssertVector(new[] { 0.02, 0.0, 0.05 }, result.Joints[0].Origin);
            Assert.Equal(JointType.Ball, result.Joints[1].Type);
            AssertVector(new[] { 0.02, 0.0, 0.05 }, result.Joints[1].Origin);
            Assert.Empty(result.Warnings);
        }

        /// <summary>A ball rolling on a table: tangency of a sphere and a
        /// plane, planar plus ball at the centre.</summary>
        [Fact]
        public void SphereOnPlaneSplitsIntoPlanarPlusBall()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "table", isFixed: true),
                    Comp("c002", "marble"),
                },
                Mate("Tangent1", "swMateTANGENT",
                    PlaneEnt("c001", Z, P(0, 0, 0.01)),
                    SphereEnt("c002", P(0.05, 0.05, 0.025), radius: 0.015)));

            var result = Run(graph);
            Assert.Single(result.VirtualGroups);
            Assert.Equal(2, result.Joints.Count);
            Assert.Equal(JointType.Planar, result.Joints[0].Type);
            Assert.Equal(0.01, result.Joints[0].Origin[2], 9);
            Assert.Equal(JointType.Ball, result.Joints[1].Type);
            AssertVector(new[] { 0.05, 0.05, 0.025 }, result.Joints[1].Origin);
            Assert.Empty(result.Warnings);
        }

        /// <summary>A DISTANCE held between two parallel cylinders over a
        /// face coincident is the rim-tangent disc pair at an offset: the
        /// offset changes the dimension, never the freedom, so the same
        /// orbit + spin carrier appears.</summary>
        [Fact]
        public void DistanceBetweenCylindersBecomesOrbitPlusSpin()
        {
            var minusZ = new double[] { 0, 0, -1 };
            var graph = Graph(
                new[]
                {
                    Comp("c001", "base disc", isFixed: true),
                    Comp("c002", "satellite"),
                },
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0.01)),
                Mate("Distance1", "swMateDISTANCE",
                    Cylinder("c001", minusZ, P(0, 0, 0.01), radius: 0.05),
                    Cylinder("c002", Z, P(0.08, 0, 0), radius: 0.015)));

            var result = Run(graph);
            Assert.Single(result.VirtualGroups);
            Assert.Equal(2, result.Joints.Count);
            Assert.Equal(JointType.Revolute, result.Joints[0].Type);
            Assert.Equal(JointType.Revolute, result.Joints[1].Type);
            AssertVector(new[] { 0.08, 0.0, 0.0 }, result.Joints[1].Origin);
            Assert.Empty(result.Warnings);
        }

        /// <summary>A tangent mate that fits neither pattern (extra mates on
        /// the pair) stays on the honest path: free joint, UNDER_DEFINED.</summary>
        [Fact]
        public void UnrecognisedTangentComboStaysFreeWithWarning()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "base", isFixed: true),
                    Comp("c002", "roller"),
                },
                ParallelPlanes("Parallel1", "c001", "c002", Y, P(0, 0, 0)),
                Mate("Tangent1", "swMateTANGENT",
                    PlaneEnt("c001", Z, P(0, 0, 0.01)),
                    Cylinder("c002", X, P(0, 0, 0.025), radius: 0.015)));

            var result = Run(graph);
            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Free, joint.Type);
            Assert.Contains(result.Warnings, w => w.Code == "UNDER_DEFINED");
        }

        // ── The rest of the mate family ─────────────────────────────────────

        /// <summary>A slot mate with the centered/distance/percent option
        /// pins the pin along the slot; the spin about the pin axis is all
        /// that remains.</summary>
        [Fact]
        public void ConstrainedSlotIsRevolute()
        {
            var slotMate = Mate("Slot1", "swMateSLOT",
                Cylinder("c002", Z, P(0.02, 0.01, 0)),
                PlaneEnt("c001", Z, P(0, 0, 0)));
            slotMate.SlotConstraint = 1;    // centered

            var graph = Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    Comp("c002", "pin"),
                },
                slotMate);

            var result = Run(graph);
            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Revolute, joint.Type);
            AssertVector(new[] { 0.0, 0.0, 1.0 }, joint.Axis);
        }

        /// <summary>A cam-follower mate cannot drive a bone; the follower's
        /// own joint (from the other mates) still classifies, and a specific
        /// warning says the cam relation is the user's hand.</summary>
        [Fact]
        public void CamFollowerWarnsAndKeepsTheFollowerJoint()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    Comp("c002", "rocker"),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0.01)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)),
                Mate("CamMate1", "swMateCAMFOLLOWER",
                    PlaneEnt("c001", Y, P(0.02, 0.01, 0)),
                    PlaneEnt("c002", Y, P(0.02, 0.01, 0))));

            var result = Run(graph);
            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Revolute, joint.Type);
            Assert.Equal("medium", joint.Confidence);
            Assert.Contains(result.Warnings, w => w.Code == "CAM_FOLLOWER");
        }

        /// <summary>A universal joint couples the two shafts' spins 1:1; the
        /// cyclic fluctuation is not modelled and the confidence says so.</summary>
        [Fact]
        public void UniversalJointCouplesShafts()
        {
            var ujMate = Mate("UJ1", "swMateUNIVERSALJOINT",
                Cylinder("c002", Z, P(0, 0, 0.05)),
                Cylinder("c003", Y, P(0, 0.05, 0.1)));

            var graph = Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    Comp("c002", "shaft in"),
                    Comp("c003", "shaft out"),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0)),
                Concentric("Concentric2", "c001", "c003", Y, P(0, 0, 0.1)),
                CoincidentPlanes("Coincident2", "c001", "c003", Y, P(0, 0, 0.1)),
                ujMate);

            var result = Run(graph);
            Assert.Equal(2, result.Joints.Count);
            var driven = result.Joints[1];
            Assert.NotNull(driven.Coupling);
            Assert.Equal("gear", driven.Coupling.Kind);
            Assert.Equal(result.Joints[0].Id, driven.Coupling.DriverJoint);
            Assert.Equal(1.0, Math.Abs(driven.Coupling.Ratio.Value), 9);
            Assert.Equal("medium", driven.Confidence);
        }

        /// <summary>Live corpus 17 path1 (2026-08-23): the track is a 3D
        /// sketch owned by the ASSEMBLY and no component is fixed: the
        /// sampled 49 points were thrown away because the joint had no
        /// second group. Assembly-owned reference geometry IS ground: a
        /// virtual grounded group (empty components, like a carrier) anchors
        /// the path joint, and the "no component is fixed" island warning
        /// stays away.</summary>
        [Fact]
        public void AssemblySketchGroundsThePathJoint()
        {
            var mate = Mate("PathMate1", "swMatePATH",
                VertexEnt("c001", P(0.1237, 0.12771, -0.14105)),
                CurveEnt());
            mate.PathPoints = new[]
            {
                P(-0.0476, -0.0267, 0.1352),
                P(0.05, 0.05, 0.0),
                P(0.1237, 0.12771, -0.14105),
            };
            var graph = Graph(new[] { Comp("c001", "slide") }, mate);

            var grouping = RigidGrouper.Group(graph);
            Assert.Equal(2, grouping.Groups.Count);
            Assert.True(grouping.Groups[0].Grounded);
            Assert.Empty(grouping.Groups[0].Components);

            var result = JointClassifier.Classify(graph, grouping);
            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Path, joint.Type);
            Assert.Equal("g000", joint.ParentGroup);
            Assert.Equal("g001", joint.ChildGroup);
            Assert.NotNull(joint.PathPoints);
            Assert.Equal(3, joint.PathPoints.Length);
        }

        /// <summary>Live corpus 16 pt4 (2026-08-23): a corner COINCIDENT
        /// with an assembly 3D-sketch spline: a path mate in all but name.
        /// MateReader recovers and samples the curve onto the mate; the
        /// classifier rides the existing path machinery, origin at the
        /// vertex, rotation free (the "slide along and tumble").</summary>
        [Fact]
        public void VertexOnAssemblyCurveIsAPathJoint()
        {
            var vertex = P(0.15693, 0.12, -0.15643);
            var mate = Mate("Coincident5", "swMateCOINCIDENT",
                VertexEnt("c001", vertex),
                CurveEnt());
            mate.PathPoints = new[]
            {
                P(0.0, 0.1, 0.0),
                P(0.15693, 0.12, -0.15643),
                P(0.3, 0.15, -0.3),
            };
            var graph = Graph(new[] { Comp("c001", "slide") }, mate);

            var grouping = RigidGrouper.Group(graph);
            var result = JointClassifier.Classify(graph, grouping);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Path, joint.Type);
            AssertVector(vertex, joint.Origin);
            Assert.NotNull(joint.PathPoints);
        }

        /// <summary>A vertex COINCIDENT with a face no analytic joint
        /// describes: a torus, a fillet, a loft. SolidWorks mates to those
        /// as readily as to a plane and lets the point slide anywhere on the
        /// face, so the classifier rides the triangulation MateReader
        /// carried: origin at the contact point, axis the local surface
        /// normal, rotation free.</summary>
        [Fact]
        public void VertexOnFreeFormFaceIsASurfaceJoint()
        {
            var vertex = P(0.01, -0.02, 0.03);
            var graph = Graph(
                new[]
                {
                    Comp("c001", "shackle", isFixed: true),
                    Comp("c002", "rope"),
                },
                Mate("Coincident1", "swMateCOINCIDENT",
                    PatchEnt("c001", 0.03),
                    VertexEnt("c002", vertex)));

            var grouping = RigidGrouper.Group(graph);
            var result = JointClassifier.Classify(graph, grouping);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Surface, joint.Type);
            AssertVector(vertex, joint.Origin);
            AssertVector(new double[] { 0, 0, 1 }, joint.Axis);
            Assert.Equal(4, joint.SurfacePoints.Length);
            Assert.Equal(2, joint.SurfaceTriangles.Length);
            Assert.Empty(result.Warnings);
        }

        /// <summary>The patch is a LAST resort: a plane, a cylinder or any
        /// other surface with a joint of its own must never reach it, or a
        /// mesh would replace exact motion. MateReader gates on the surface
        /// type, so a planar face still arrives untriangulated and still
        /// splits into planar plus ball.</summary>
        [Fact]
        public void VertexOnAPlaneStillTakesTheAnalyticSplit()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    Comp("c002", "slide"),
                },
                Mate("Coincident1", "swMateCOINCIDENT",
                    PlaneEnt("c001", Z, P(0, 0, 0.01)),
                    VertexEnt("c002", P(0.01168, -0.0078775, 0.01))));

            var result = Run(graph);

            Assert.Equal(2, result.Joints.Count);
            Assert.Equal(JointType.Planar, result.Joints[0].Type);
            Assert.Equal(JointType.Ball, result.Joints[1].Type);
            Assert.All(result.Joints, j => Assert.Null(j.SurfaceTriangles));
        }

        /// <summary>A path mate whose curve sampled: the joint IS the path,
        /// origin at the follower's rest point, axis the local tangent.</summary>
        [Fact]
        public void SampledPathMateBecomesPathJoint()
        {
            var pathMate = Mate("Path1", "swMatePATH",
                AxisEnt("c001", X, P(0, 0, 0.02)),
                PointEnt("c002", P(0.02, 0, 0.02)));
            pathMate.PathPoints = new[]
            {
                P(0.0, 0.0, 0.02),
                P(0.05, 0.0, 0.02),
                P(0.1, 0.02, 0.02),
            };

            var graph = Graph(
                new[]
                {
                    Comp("c001", "track", isFixed: true),
                    Comp("c002", "shuttle"),
                },
                pathMate);

            var result = Run(graph);
            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Path, joint.Type);
            Assert.NotNull(joint.PathPoints);
            Assert.Equal(3, joint.PathPoints.Length);
            AssertVector(new[] { 0.02, 0.0, 0.02 }, joint.Origin);
            AssertVector(new[] { 1.0, 0.0, 0.0 }, joint.Axis, 1e-6);
            Assert.Empty(result.Warnings);
        }

        /// <summary>A path mate whose curve could NOT be sampled has nothing
        /// a consumer can follow: free plus a specific warning.</summary>
        [Fact]
        public void UnsampledPathMateIsFreeWithWarning()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "track", isFixed: true),
                    Comp("c002", "shuttle"),
                },
                Mate("Path1", "swMatePATH",
                    AxisEnt("c001", X, P(0, 0, 0.02)),
                    PointEnt("c002", P(0.05, 0, 0.02))));

            var result = Run(graph);
            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Free, joint.Type);
            Assert.Contains(result.Warnings, w => w.Code == "PATH_UNSAMPLED");
        }

        /// <summary>swMateLOCKTOSKETCH contains "LOCK" as a substring but is
        /// a sketch-driven positioner, not a weld: it must not freeze the
        /// pair. The concentric still classifies, freer-with-a-note.</summary>
        [Fact]
        public void LockToSketchIsNotALock()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "frame", isFixed: true),
                    Comp("c002", "rotor"),
                },
                Concentric("Concentric1", "c001", "c002", Z, P(0, 0, 0)),
                Mate("LockToSketch1", "swMateLOCKTOSKETCH",
                    PointEnt("c001", P(0.01, 0, 0)),
                    PointEnt("c002", P(0.01, 0, 0))));

            var result = Run(graph);
            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Cylindrical, joint.Type);
            Assert.Equal("medium", joint.Confidence);
        }

        /// <summary>The common symmetric-mate use: a moving plane centred
        /// between two fixed faces, is a plane coincidence with the
        /// mid-plane: a planar joint, not free.</summary>
        [Fact]
        public void SymmetricAboutFixedFacesIsPlanar()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "channel", isFixed: true),
                    Comp("c002", "slide"),
                },
                Mate("Symmetric1", "swMateSYMMETRIC",
                    PlaneEnt("c001", Y, P(0, -0.01, 0)),
                    PlaneEnt("c001", Y, P(0, 0.01, 0)),
                    PlaneEnt("c002", Y, P(0, 0, 0))));

            var result = Run(graph);
            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Planar, joint.Type);
            AssertVector(new[] { 0.0, 1.0, 0.0 }, joint.Axis);
            Assert.Empty(result.Warnings);
        }

        /// <summary>
        /// Live TongRig (2026-09-14). The base section's two side faces
        /// meet at an angle, 15 degrees either side of X, and are held
        /// symmetric about the ASSEMBLY's own Right plane. The mirrored
        /// planes are not parallel to each other, which the resolver used to
        /// read as "no motion model"; the base then kept a slide along X it
        /// does not have, and the whole tong exported as sliding relative to
        /// its ground.
        ///
        /// What the mate says is exact: the body's bisector of those two
        /// faces lies IN the mirror plane. A plane coincidence on the mirror
        /// normal.
        /// </summary>
        [Fact]
        public void SymmetricAboutAnAssemblyPlaneWithAngledFacesIsPlanar()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "base"),
                },
                Mate("Symmetric1", "swMateSYMMETRIC",
                    PlaneEnt("c001", new[] { 0.96593, 0.25882, 0.0 },
                             P(0.074863, 0.44746, -0.16)),
                    PlaneEnt("c001", new[] { -0.96593, 0.25882, 0.0 },
                             P(-0.22757, -0.12244, -0.16)),
                    PlaneEnt(null, X, P(0, 0, 0))));

            var state = MotionResolver.Resolve(graph.Mates);
            Assert.Equal(0, state.Unmodelled);
            // A plane coincidence on X: the two slides in the plane remain,
            // the one along X is gone.
            Assert.False(state.IsRigid);
            Assert.Equal(2, state.TransDirs.Count);
            foreach (var d in state.TransDirs)
                Assert.True(Math.Abs(MathOps.Dot(MathOps.Normalized(d), X)) < 1e-9,
                            "a slide along the mirror normal survived");
        }

        /// <summary>The same base with all three of its mates to the
        /// assembly: a coincident on Y and symmetrics whose mirrors are the
        /// X and Z planes. Three orthogonal plane coincidences are a weld,
        /// so the base joins the assembly ground rather than hanging off it
        /// by a joint.</summary>
        [Fact]
        public void ABaseHeldOnThreeAssemblyPlanesIsGround()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "base"),
                },
                Mate("Coincident17", "swMateCOINCIDENT",
                    PlaneEnt("c001", Y, P(-0.295, 0, -0.16)),
                    PlaneEnt(null, Y, P(0, 0, 0))),
                Mate("Symmetric1", "swMateSYMMETRIC",
                    PlaneEnt("c001", new[] { 0.96593, 0.25882, 0.0 },
                             P(0.074863, 0.44746, -0.16)),
                    PlaneEnt("c001", new[] { -0.96593, 0.25882, 0.0 },
                             P(-0.22757, -0.12244, -0.16)),
                    PlaneEnt(null, X, P(0, 0, 0))),
                Mate("Symmetric2", "swMateSYMMETRIC",
                    PlaneEnt("c001", Z, P(0.295, 0, 0.16)),
                    PlaneEnt("c001", new[] { 0.0, 0.0, -1.0 }, P(-0.295, 0, -0.16)),
                    PlaneEnt(null, Z, P(0, 0, 0))));

            var grouping = RigidGrouper.Group(graph);
            var ground = Assert.Single(grouping.Groups);
            Assert.True(ground.Grounded);
            Assert.Contains("c001", ground.Components);
        }

        // ── Secondary axis ──────────────────────────────────────────────────

        /// <summary>secondary_axis exists only to pin bone roll: it must be
        /// unit, orthogonal to the axis, and identical on every run.</summary>
        [Theory]
        [InlineData(new double[] { 0, 0, 1 })]
        [InlineData(new double[] { 1, 0, 0 })]
        [InlineData(new double[] { 0, 1, 0 })]
        [InlineData(new double[] { 1, 1, 1 })]
        [InlineData(new double[] { 0.3, -0.7, 0.2 })]
        [InlineData(new double[] { -0.577, 0.577, 0.577 })]
        public void SecondaryAxisIsUnitOrthogonalAndDeterministic(double[] axisDir)
        {
            MateGraph Build()
            {
                return Graph(
                    new[]
                    {
                        Comp("c001", "base", isFixed: true),
                        Comp("c002", "shaft"),
                    },
                    Concentric("Concentric1", "c001", "c002", axisDir, P(0, 0, 0)));
            }

            var first = Assert.Single(Run(Build()).Joints);
            var second = Assert.Single(Run(Build()).Joints);

            Assert.NotNull(first.Axis);
            Assert.NotNull(first.SecondaryAxis);
            Assert.Equal(1.0, MathOps.Norm(first.SecondaryAxis), 1e-9);
            Assert.Equal(0.0, Math.Abs(MathOps.Dot(first.Axis, first.SecondaryAxis)), 1e-9);

            // Deterministic: bit-for-bit equal across runs on the same graph.
            Assert.Equal(first.Axis, second.Axis);
            Assert.Equal(first.SecondaryAxis, second.SecondaryAxis);
        }

        // ── Width mates with a cylindrical tab (live corpus 05) ─────────────

        private static readonly double[] MinusX = { -1, 0, 0 };
        private static readonly double[] MinusY = { 0, -1, 0 };
        private static readonly double[] MinusZ = { 0, 0, -1 };

        /// <summary>The live planar2 (2026-08-22): puck face-coincident on the
        /// plate, one width mate centring the puck's cylinder between the
        /// plate's X-normal sides. The puck slides along Y and spins about its
        /// own axis: a pin in a slot. The old plane-tab width model killed
        /// the spin and exported prismatic. The secondary axis must be the
        /// slide direction, not a roll pick.</summary>
        [Fact]
        public void CoincidentPlusCylinderWidthIsPinSlot()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    Comp("c002", "puck"),
                },
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0.01)),
                Mate("Width1", "swMateWIDTH",
                    PlaneEnt("c001", X, P(0.05, -0.05, 0.01)),
                    PlaneEnt("c001", MinusX, P(-0.05, -0.05, 0.01)),
                    Cylinder("c002", MinusZ, P(0, 0, 0.02), 0.015)));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.PinSlot, joint.Type);
            Assert.Equal("high", joint.Confidence);
            AssertVector(new double[] { 0, 0, 1 }, joint.Axis);
            AssertVector(new double[] { 0, 1, 0 }, joint.SecondaryAxis);
            AssertVector(P(0, 0, 0.02), joint.Origin);
            Assert.Empty(result.Warnings);
        }

        /// <summary>Same pair with the width FIRST in the feature tree: the
        /// resolver reorders widths last internally, so the classification
        /// cannot depend on mate order.</summary>
        [Fact]
        public void WidthListedFirstStillClassifiesPinSlot()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    Comp("c002", "puck"),
                },
                Mate("Width1", "swMateWIDTH",
                    PlaneEnt("c001", X, P(0.05, -0.05, 0.01)),
                    PlaneEnt("c001", MinusX, P(-0.05, -0.05, 0.01)),
                    Cylinder("c002", MinusZ, P(0, 0, 0.02), 0.015)),
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0.01)));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.PinSlot, joint.Type);
            Assert.Equal("high", joint.Confidence);
            AssertVector(new double[] { 0, 0, 1 }, joint.Axis);
            AssertVector(new double[] { 0, 1, 0 }, joint.SecondaryAxis);
        }

        /// <summary>The live planar3 (2026-08-22): widths on BOTH side pairs
        /// pin the cylinder at the plate's centre but its own spin survives,
        /// a revolute, where the plane-tab width model merged the pair rigid.
        /// The origin slides down the axis onto the coincident plane.</summary>
        [Fact]
        public void TwoCylinderWidthsMakeACentredRevolute()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    Comp("c002", "puck"),
                },
                CoincidentPlanes("Coincident1", "c001", "c002", Z, P(0, 0, 0.01)),
                Mate("Width1", "swMateWIDTH",
                    PlaneEnt("c001", X, P(0.05, -0.05, 0.01)),
                    PlaneEnt("c001", MinusX, P(-0.05, -0.05, 0.01)),
                    Cylinder("c002", MinusZ, P(0, 0, 0.02), 0.015)),
                Mate("Width2", "swMateWIDTH",
                    PlaneEnt("c001", Y, P(-0.05, 0.05, 0.01)),
                    PlaneEnt("c001", MinusY, P(-0.05, -0.05, 0.01)),
                    Cylinder("c002", MinusZ, P(0, 0, 0.02), 0.015)));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Revolute, joint.Type);
            Assert.Equal("high", joint.Confidence);
            AssertVector(new double[] { 0, 0, 1 }, joint.Axis);
            AssertVector(P(0, 0, 0.01), joint.Origin);
            Assert.Empty(result.Warnings);
        }

        // ── Profile-centre mates (live corpus 05) ───────────────────────────

        /// <summary>The live planar4 (2026-08-22): a profile-centre mate
        /// without "lock rotation" pins the centre and leaves the spin about
        /// the mated faces' normal: a revolute at the centre. The raw entity
        /// params carry no trace of the tick; the fixture states the feature
        /// data's LockRotation the way MateReader records it.</summary>
        [Fact]
        public void ProfileCentreWithoutLockIsRevolute()
        {
            var graph = Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    Comp("c002", "puck"),
                },
                Mate("ProfileCenter1", "swMatePROFILECENTER",
                    PlaneEnt("c001", Z, P(0, 0, 0.01)),
                    PlaneEnt("c002", MinusZ, P(0, 0, 0.01))));

            var result = Run(graph);

            var joint = Assert.Single(result.Joints);
            Assert.Equal(JointType.Revolute, joint.Type);
            Assert.Equal("high", joint.Confidence);
            AssertVector(new double[] { 0, 0, 1 }, joint.Axis);
            AssertVector(P(0, 0, 0.01), joint.Origin);
            Assert.Empty(result.Warnings);
        }
    }
}
