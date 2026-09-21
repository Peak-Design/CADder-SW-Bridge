using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A cut the consumer will not solve must not take its constraint with
    /// it. SolidWorks does not lose it, so neither may the manifest: what the
    /// rest of the ring cannot reproduce, the ring forbids, and the tree
    /// joints of that ring lose it.
    /// </summary>
    public class CutTransferTests
    {
        private static RigidGroup Group(string id, bool grounded = false)
        {
            return new RigidGroup { Id = id, Name = id, Grounded = grounded };
        }

        private static RigJoint Joint(
            string id, string type, string parent, string child,
            double[] axis, double[] origin)
        {
            return new RigJoint
            {
                Id = id,
                Type = type,
                ParentGroup = parent,
                ChildGroup = child,
                Axis = axis,
                SecondaryAxis = new double[] { 1, 0, 0 },
                Origin = origin,
            };
        }

        /// <summary>
        /// Live ClampRig (2026-08-24, Oscar): both hydraulic rams are
        /// pinned to the machine by a concentric. One is held along its pin by
        /// a width mate; the OTHER is held by a coincident between the two
        /// rams' front planes: it is located THROUGH the first ram. Read
        /// pairwise the second ram's joint is a cylindrical, because a
        /// concentric alone lets it slide; the mate that stops it is on the
        /// cut. Dropping the cut let that barrel slide along its pin in the
        /// rig when it cannot in SolidWorks.
        /// </summary>
        [Fact]
        public void ARamHeldOnlyThroughItsTwinStopsSlidingOnItsPin()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),   // machine body
                Group("g001"),                   // the ram with only a concentric
                Group("g002"),                   // the ram with the width mate
            };
            var pin = new double[] { 0, 1, 0 };

            var loose = Joint("j001", JointType.Cylindrical, "g000", "g001",
                              pin, new double[] { 0.525, 0.045, -0.316 });
            var held = Joint("j002", JointType.Revolute, "g000", "g002",
                             pin, new double[] { -0.525, 0.045, -0.316 });
            // The coincident between the two front planes: same normal as the
            // pins, so it takes away exactly the height the concentric left.
            var level = Joint("j003", JointType.Planar, "g001", "g002",
                              pin, new double[] { -0.614, 0, -0.667 });

            var result = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { loose, held, level });

            var loop = Assert.Single(result.Loops);
            Assert.Equal("j003", loop.ClosureJoint);
            Assert.Equal("none", loop.ClosureKind);

            Assert.Equal(JointType.Revolute, loose.Type);
            Assert.Contains("removes the slide", loose.Notes);
            Assert.Equal(JointType.Revolute, held.Type);
        }

        /// <summary>
        /// A ball is not skipped just because it has no axis: it has a twist
        /// system like anything else. Here the ring: one revolute about z and
        /// a plane whose normal is z, leaves it exactly one of its three
        /// rotations, and one rotation about a named line through the centre
        /// IS a revolute.
        /// </summary>
        [Fact]
        public void ABallTheRingLeavesOneRotationBecomesARevoluteAboutIt()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"),
                Group("g002"),
            };
            var z = new double[] { 0, 0, 1 };

            var ball = Joint("j001", JointType.Ball, "g000", "g001",
                             null, new double[3]);
            ball.SecondaryAxis = null;
            ball.RotationLimit = new JointLimit
            {
                Min = -0.4, Max = 0.4, ValueAtRest = 0.0,
            };
            var pin = Joint("j002", JointType.Revolute, "g000", "g002",
                            z, new double[] { 1, 0, 0 });
            var level = Joint("j003", JointType.Planar, "g001", "g002",
                              z, new double[] { 0.5, 0, 0 });

            var result = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { ball, pin, level });

            var loop = Assert.Single(result.Loops);
            Assert.Equal("none", loop.ClosureKind);

            Assert.Equal(JointType.Revolute, ball.Type);
            Assert.Equal(1.0, System.Math.Abs(MathOps.Dot(
                MathOps.Normalized(ball.Axis), z)), 9);
            // The swing cone measured rotations that are gone, so it goes too.
            Assert.Null(ball.RotationLimit);
            Assert.Contains("two of its three rotations", ball.Notes);
        }

        /// <summary>
        /// A screw's turn and slide are ONE coupled motion whose pitch this
        /// analysis does not carry, so what the ring leaves cannot be named
        /// safely. It must be said, not silently dropped: before this the
        /// switch fell through and the joint was exported with no hint that a
        /// constraint had been read and discarded.
        /// </summary>
        [Fact]
        public void AScrewIsSaidToBeUnnarrowableRatherThanSilentlyLeft()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"),
                Group("g002"),
            };
            var pin = new double[] { 0, 1, 0 };

            var screw = Joint("j001", JointType.Screw, "g000", "g001",
                              pin, new double[] { 0.525, 0.045, -0.316 });
            var held = Joint("j002", JointType.Revolute, "g000", "g002",
                             pin, new double[] { -0.525, 0.045, -0.316 });
            var level = Joint("j003", JointType.Planar, "g001", "g002",
                              pin, new double[] { -0.614, 0, -0.667 });

            LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { screw, held, level });

            Assert.Equal(JointType.Screw, screw.Type);
            Assert.Contains("one coupled motion whose pitch", screw.Notes);
            Assert.Equal("medium", screw.Confidence);
            // Said once, not once per sweep.
            Assert.Equal(1, CountOf(screw.Notes, "one coupled motion whose pitch"));
        }

        /// <summary>
        /// A coupling is an explicit mate DRIVING one of the joint's freedoms,
        /// and this analysis cannot see it: it reads every joint as if its
        /// freedoms were independent. Re-typing would leave the manifest
        /// declaring a driver for a channel the type no longer has.
        /// </summary>
        [Fact]
        public void ACoupledJointIsLeftAloneAndSaysSo()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"),
                Group("g002"),
            };
            var pin = new double[] { 0, 1, 0 };

            var driven = Joint("j001", JointType.Cylindrical, "g000", "g001",
                               pin, new double[] { 0.525, 0.045, -0.316 });
            driven.Coupling = new JointCoupling
            {
                Kind = "screw",
                LeadMPerRev = 0.005,
            };
            var held = Joint("j002", JointType.Revolute, "g000", "g002",
                             pin, new double[] { -0.525, 0.045, -0.316 });
            var level = Joint("j003", JointType.Planar, "g001", "g002",
                              pin, new double[] { -0.614, 0, -0.667 });

            LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { driven, held, level });

            // Without the coupling this is the ram case above, and it would
            // have come out revolute.
            Assert.Equal(JointType.Cylindrical, driven.Type);
            Assert.Contains("a coupling mate drives one of its freedoms",
                            driven.Notes);
            Assert.Equal(1, CountOf(driven.Notes,
                                    "a coupling mate drives one of its freedoms"));
        }

        private static int CountOf(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack)) return 0;
            int n = 0, at = 0;
            while ((at = haystack.IndexOf(needle, at, System.StringComparison.Ordinal)) >= 0)
            {
                n++;
                at += needle.Length;
            }
            return n;
        }

        /// <summary>The ram that already had its width mate keeps exactly what
        /// it had: a transfer may only ever take freedom away, and there is
        /// none here to take.</summary>
        [Fact]
        public void AJointTheRingFullyPermitsIsUntouched()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"),
                Group("g002"),
            };
            var pin = new double[] { 0, 1, 0 };

            var a = Joint("j001", JointType.Revolute, "g000", "g001",
                          pin, new double[] { 1, 0, 0 });
            var b = Joint("j002", JointType.Revolute, "g000", "g002",
                          pin, new double[] { -1, 0, 0 });
            var level = Joint("j003", JointType.Planar, "g001", "g002",
                              pin, new double[3]);

            LoopAnalyzer.Analyze(groups, new List<RigJoint> { a, b, level });

            Assert.Equal(JointType.Revolute, a.Type);
            Assert.Equal(JointType.Revolute, b.Type);
            Assert.Null(a.Notes);
            Assert.Null(b.Notes);
        }

        /// <summary>
        /// Live ClampRig's cutting head: it slides along the machine and
        /// is mated to the lead screw rod, which slides along the machine too.
        /// The ring is cut at the head's own slide, so what is left must say
        /// that the head slides along the machine RELATIVE TO THE ROD: not
        /// that it floats in a plane, which is what the contact mate says on
        /// its own.
        ///
        /// Run twice: once as the live machine stands, and once with the WHOLE
        /// mechanism turned 45 degrees about the plane's normal. A plane's two
        /// slides are named by a world seed, not by the mechanism, so a
        /// permitted direction diagonal to that seed spans neither of them and
        /// the joint used to be exported unnarrowed: the head floating in a
        /// plane again. The two runs are the same machine and must give the
        /// same joint.
        /// </summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(System.Math.PI / 4.0)]
        public void AContactInsideAChainedLoopKeepsOnlyTheSlideTheChainAllows(
            double yaw)
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),   // machine body
                Group("g001"),                   // lead screw rod
                Group("g002"),                   // cutting head
            };
            // Turned about the plane's normal, so the plane and its normal
            // are untouched and only the machine's direction inside it moves.
            double cos = System.Math.Cos(yaw), sin = System.Math.Sin(yaw);
            System.Func<double[], double[]> spin = v => new[]
            {
                cos * v[0] + sin * v[2], v[1], -sin * v[0] + cos * v[2],
            };
            var along = spin(new double[] { 0, 0, 1 });
            var up = new double[] { 0, 1, 0 };

            var rod = Joint("j001", JointType.Prismatic, "g000", "g001",
                            along, new double[3]);
            rod.TranslationLimit = new JointLimit { Min = 0, Max = 0.85 };
            var head = Joint("j002", JointType.Prismatic, "g000", "g002",
                             along, spin(new double[] { 0, 0, 0.1 }));
            var contact = Joint("j003", JointType.Planar, "g001", "g002",
                                up, spin(new double[] { 0, 0, 0.1 }));

            var result = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { rod, head, contact });

            var loop = Assert.Single(result.Loops);
            Assert.Equal("none", loop.ClosureKind);
            Assert.Equal("j002", loop.ClosureJoint);

            // The plane allowed two slides and a spin; the ring allows one
            // slide, along the machine.
            Assert.Equal(JointType.Prismatic, contact.Type);
            Assert.Equal(1.0, System.Math.Abs(
                MathOps.Dot(MathOps.Normalized(contact.Axis), along)), 9);
            Assert.Contains("removes its spin and one of its slides",
                            contact.Notes);
        }

        /// <summary>
        /// A scotch yoke, saved at the end of its stroke and 30 degrees away
        /// from it. The yoke slides on the machine; the crank pin runs in its
        /// slot. At the dead centre the pin is moving straight along the slot,
        /// so no admissible velocity moves the yoke AT ALL, and the loop
        /// velocity equation, which is all the span test is, cannot tell that
        /// from a yoke SolidWorks holds still. Read literally it welds the
        /// yoke to the machine and writes a note blaming SolidWorks for it.
        ///
        /// The same machine saved 30 degrees on narrows nothing, and it is the
        /// same machine. A real hold is in how the axes are ORIENTED; a dead
        /// centre is in where the parts happen to SIT.
        /// </summary>
        [Theory]
        [InlineData(0.0)]
        [InlineData(System.Math.PI / 6.0)]
        public void AYokeAtItsDeadCentreIsNotWeldedToTheMachine(double theta)
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),   // machine
                Group("g001"),                   // crank
                Group("g002"),                   // yoke
            };
            var spin = new double[] { 0, 0, 1 };
            const double r = 0.1;
            double px = r * System.Math.Cos(theta), py = r * System.Math.Sin(theta);

            var crank = Joint("j001", JointType.Revolute, "g000", "g001",
                              spin, new double[3]);
            // The pin in the slot: turns about z, slides up and down the slot.
            var slot = Joint("j002", JointType.PinSlot, "g001", "g002",
                             spin, new double[] { px, py, 0 });
            slot.SecondaryAxis = new double[] { 0, 1, 0 };
            var yoke = Joint("j003", JointType.Prismatic, "g000", "g002",
                             new double[] { 1, 0, 0 },
                             new double[] { px, 0, 0 });

            var result = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { crank, slot, yoke });

            var loop = Assert.Single(result.Loops);
            Assert.Equal("none", loop.ClosureKind);

            Assert.Equal(JointType.Prismatic, yoke.Type);
            Assert.DoesNotContain("SolidWorks does not permit it either",
                                  yoke.Notes ?? "");
        }

        /// <summary>
        /// Live corpus 06 fourbar (2026-08-25, Oscar), exact geometry: four
        /// pins about z, at the crank's ground pin, the crank/coupler pin,
        /// the coupler/rocker pin and the rocker's ground pin.
        ///
        /// Every pin in that assembly is a concentric PLUS a coincident, so
        /// every joint is a revolute. But for the coupler/rocker pair the
        /// only mate between THOSE TWO bodies is the concentric: the
        /// coincident that pins their height is between different pairs, so
        /// read pairwise that one joint comes out cylindrical. Its height is
        /// held THROUGH THE LOOP: three revolutes about z span a rotation
        /// about z and the two slides in the xy plane, and no combination of
        /// them moves anything along z at all.
        ///
        /// Shipped cylindrical, the consumer leaves that bone's slide
        /// channel unlocked and unlimited, and its loop IK only rotates,
        /// so the rocker slides straight off its pin, which SolidWorks does
        /// not permit.
        /// </summary>
        [Fact]
        public void AFourBarPinHeldThroughTheLoopLosesItsSlide()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),   // bar-ground
                Group("g001"),                   // bar-crank
                Group("g002"),                   // bar-coupler
                Group("g003"),                   // bar-rocker
            };
            var z = new double[] { 0, 0, 1 };

            var groundCrank = Joint("j001", JointType.Revolute, "g000", "g001",
                                    z, new double[] { 0.05, 0, 0.0025 });
            var crankCoupler = Joint("j002", JointType.Revolute, "g001", "g002",
                                     z, new double[] { 0.08, 0, 0.0075 });
            // The concentric-only pair: a slide along z the ring cannot make.
            var couplerRocker = Joint("j003", JointType.Cylindrical,
                                      "g002", "g003", z,
                                      new double[] { 0.009230769230769223,
                                                     -0.037305709701483496,
                                                     0.0075 });
            var rockerGround = Joint("j004", JointType.Revolute, "g003", "g000",
                                     z, new double[] { -0.05, 0, 0.0025 });

            var result = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { groundCrank, crankCoupler,
                                             couplerRocker, rockerGround });

            // The ring is the whole four-bar, and this joint is a MEMBER of
            // it, not the cut: nothing about the consumer's closure excuses
            // exporting a freedom the mechanism does not have.
            var loop = Assert.Single(result.Loops);
            Assert.Contains("j003", loop.MemberJoints);
            Assert.NotEqual("j003", loop.ClosureJoint);

            Assert.Equal(JointType.Revolute, couplerRocker.Type);
            Assert.Contains("removes the slide", couplerRocker.Notes);
            Assert.Null(couplerRocker.TranslationLimit);

            // A four-bar still moves: the three pins the mates already read
            // as revolutes keep every turn they had.
            Assert.Equal(JointType.Revolute, groundCrank.Type);
            Assert.Equal(JointType.Revolute, crankCoupler.Type);
            Assert.Equal(JointType.Revolute, rockerGround.Type);
        }

        /// <summary>
        /// Live corpus 06 parallelogram (2026-08-25, Oscar), exact geometry:
        /// the same concentric-only pin as the four-bar, one ring along, and
        /// away from any dead centre. Oscar: "in Blender the driven bone can
        /// be slid up and down, which SolidWorks does not allow."
        ///
        /// Nothing about this joint is subtle, three revolutes about z at
        /// three points that are not in a line span a rotation about z and
        /// both slides across it, and that span holds no motion along z at
        /// all. It was exported cylindrical for one reason only: its ring
        /// cuts to an IK closure, and the transfer used to skip every ring
        /// whose cut the consumer solves.
        /// </summary>
        [Fact]
        public void AParallelogramPinHeldThroughTheLoopLosesItsSlide()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),   // bar-ground
                Group("g001"),                   // the coupler bar
                Group("g002"),
                Group("g003"),
            };
            var z = new double[] { 0, 0, 1 };

            var leftGround = Joint("j001", JointType.Revolute, "g000", "g002",
                                   z, new double[] { -0.05, 0, 0.0025 });
            var rightGround = Joint("j002", JointType.Revolute, "g000", "g003",
                                    z, new double[] { 0.05, 0, 0.0025 });
            // The concentric-only pair.
            var leftTop = Joint("j003", JointType.Cylindrical, "g002", "g001",
                                z, new double[] { -0.05, 0.03, 0.0075 });
            var rightTop = Joint("j004", JointType.Revolute, "g003", "g001",
                                 z, new double[] { 0.05, 0.03, 0.0075 });

            var result = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { leftGround, rightGround,
                                             leftTop, rightTop });

            var loop = Assert.Single(result.Loops);
            Assert.Contains("j003", loop.MemberJoints);
            // Whether the pin is the cut or not no longer matters: since
            // 2026-09-15 the cut is narrowed like every other member.

            Assert.Equal(JointType.Revolute, leftTop.Type);
            Assert.Contains("removes the slide", leftTop.Notes);
            Assert.Null(leftTop.TranslationLimit);

            // A parallelogram still swings.
            Assert.Equal(JointType.Revolute, leftGround.Type);
            Assert.Equal(JointType.Revolute, rightGround.Type);
            Assert.Equal(JointType.Revolute, rightTop.Type);
        }

        /// <summary>
        /// A ring the consumer WILL solve is narrowed like any other. "The
        /// rest of the ring cannot reproduce this" is a fact about the
        /// assembly, not about the rig: SolidWorks forbids it whoever closes
        /// the loop, so removing it is right either way.
        ///
        /// This test used to assert the opposite: that a solved cut puts the
        /// constraint back, so nothing need be transferred. It does not. An
        /// IK closure re-joins ONE POINT and the solver only ROTATES, so a
        /// slide left on a tree joint of the ring is a channel nothing in the
        /// rig ever moves back; the consumer hands it to the user as a free
        /// translation on the bone.
        ///
        /// The three bodies here are a triangle of parallel-axis pins, which
        /// is a structure and not a mechanism: three moving bodies, three
        /// one-freedom joints, no mobility left at all. The two revolutes hold
        /// the third pin along its own axis as well, so Fixed is what
        /// SolidWorks does with it.
        /// </summary>
        [Fact]
        public void ASolvedLoopIsNarrowedLikeAnyOther()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"),
                Group("g002"),
            };
            var pin = new double[] { 0, 1, 0 };

            var crank = Joint("j001", JointType.Cylindrical, "g000", "g001",
                              pin, new double[] { -1, 0, 0 });
            var rocker = Joint("j002", JointType.Revolute, "g000", "g002",
                               pin, new double[] { 1, 0, 0 });
            var coupler = Joint("j003", JointType.Revolute, "g001", "g002",
                                pin, new double[] { 0, 0, 0.5 });

            var result = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { crank, rocker, coupler });

            Assert.Equal("ik", Assert.Single(result.Loops).ClosureKind);
            Assert.Equal(JointType.Fixed, crank.Type);
            Assert.Contains("removes both freedoms", crank.Notes);
        }
        /// <summary>
        /// Live 825 (2026-09-21, Oscar): a weld the classifier returned
        /// early had no origin. A plate re-mated to the lead screw put a
        /// planar on a ring through that weld, the ring left the plate one
        /// turn about the screw line, and reading every member's point to
        /// find that line failed on the weld. Every send after the re-mate
        /// stopped with "Object reference not set to an instance of an
        /// object".
        /// </summary>
        [Theory]
        [InlineData(JointType.Fixed)]
        [InlineData(JointType.Prismatic)]
        public void AMemberWithNoOriginDoesNotStopTheRingBeingRead(string pointless)
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"),
                Group("g002"),
            };
            var z = new double[] { 0, 0, 1 };
            var line = new double[] { 0, 0.0905, 0 };
            var weld = new RigJoint
            {
                Id = "j001", Type = pointless, ParentGroup = "g000", ChildGroup = "g001",
                Axis = pointless == JointType.Prismatic ? z : null,
            };
            var screw = Joint("j002", JointType.Revolute, "g001", "g002", z, line);
            var plate = Joint("j003", JointType.Planar, "g000", "g002", z,
                              new double[] { 0.0141, 0.0011, -0.0135 });

            var result = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { weld, screw, plate });

            Assert.Single(result.Loops);
            Assert.Null(weld.Origin);
        }
    }
}
