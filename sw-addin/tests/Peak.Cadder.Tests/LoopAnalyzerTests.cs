using System;
using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;

namespace Peak.Cadder.Tests
{
    public class LoopAnalyzerTests
    {
        private static RigidGroup Group(string id, bool grounded = false)
        {
            var g = new RigidGroup();
            g.Id = id;
            g.Name = id;
            g.Grounded = grounded;
            return g;
        }

        private static RigJoint Joint(string id, string type, string parent, string child, double[] axis = null)
        {
            var j = new RigJoint();
            j.Id = id;
            j.Type = type;
            j.ParentGroup = parent;
            j.ChildGroup = child;
            j.Axis = axis;
            return j;
        }

        /// <summary>A free joint from a parallel mate: rotation locked except
        /// about the mated faces' normal, translations untouched.</summary>
        private static RigJoint ParallelFree(string id, string parent, string child, double[] normal)
        {
            var j = Joint(id, JointType.Free, parent, child);
            j.ResidualKnown = true;
            j.ResidualRot = RotFreedom.AboutDirection;
            j.ResidualRotDir = normal;
            return j;
        }

        /// <summary>
        /// A closure's origin must be a point that stands still when each
        /// of its two bodies moves on its own joint. The classifier is free
        /// to slide it along the joint's own axis (a rotation axis is a
        /// line), and that is harmless until the joint becomes a cut.
        ///
        /// Live wrench.sldasm (2026-09-16, Oscar): "everything rotates with
        /// the screw but it shouldn't". The centerlink rests on the end of
        /// a screw, on the screw's own axis, so it stands still while the
        /// screw turns. The origin had been slid 8.5 mm along the
        /// centerlink's edge, clear of that axis, and the closure point
        /// then swung round the screw once per turn.
        /// </summary>
        [Fact]
        public void AClosureOriginIsSeatedOnTheBodysOwnAxis()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"),
                Group("g002"),
            };
            var z = new double[] { 0, 0, 1 };
            var x = new double[] { 1, 0, 0 };
            // A spinner on the X axis through the origin, a link on a pin,
            // and the cut between them about Z. The cut's origin sits 8.5
            // mm off the spinner's axis, where a slide along Z put it.
            var spin = Joint("j001", JointType.Screw, "g000", "g001", x);
            spin.Origin = new double[] { 0, 0, 0 };
            var pin = Joint("j002", JointType.Revolute, "g000", "g002", z);
            pin.Origin = new double[] { 0.1, 0.05, 0 };
            var cut = Joint("j003", JointType.Revolute, "g001", "g002", z);
            cut.Origin = new double[] { 0.02, 0, -0.0085 };

            var loops = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { spin, pin, cut });

            var loop = Assert.Single(loops.Loops);
            var seated = loops.Joints.Find(j => j.Id == loop.ClosureJoint);
            Assert.Equal("ik", loop.ClosureKind);
            // Still on its own axis, and now on the spinner's axis too.
            Assert.Equal(0.02, seated.Origin[0], 9);
            Assert.Equal(0.0, seated.Origin[1], 9);
            Assert.Equal(0.0, seated.Origin[2], 9);
        }

        /// <summary>A body whose own axis is PARALLEL to the closure's
        /// cannot carry a point on that axis anywhere, so nothing needs
        /// moving and the origin the classifier chose is kept.</summary>
        [Fact]
        public void AParallelAxisLeavesTheClosureOriginAlone()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"),
                Group("g002"),
            };
            var z = new double[] { 0, 0, 1 };
            var a = Joint("j001", JointType.Revolute, "g000", "g001", z);
            a.Origin = new double[] { 0, 0, 0 };
            var b = Joint("j002", JointType.Revolute, "g000", "g002", z);
            b.Origin = new double[] { 0.1, 0.05, 0 };
            var cut = Joint("j003", JointType.Revolute, "g001", "g002", z);
            cut.Origin = new double[] { 0.02, 0, -0.0085 };

            var loops = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { a, b, cut });
            var loop = Assert.Single(loops.Loops);
            var kept = loops.Joints.Find(j => j.Id == loop.ClosureJoint);
            Assert.Equal(-0.0085, kept.Origin[2], 9);
        }

        /// <summary>Classic four-bar: four groups in a ring, four revolutes
        /// about the same direction. Some joints are stated backwards on
        /// purpose: the analyzer must re-orient them rootward.</summary>
        [Fact]
        public void FourBarRingYieldsOnePlanarLoop()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"),
                Group("g002"),
                Group("g003"),
            };
            var axis = new double[] { 0, 0, 1 };
            var joints = new List<RigJoint>
            {
                Joint("j001", JointType.Revolute, "g000", "g001", axis),
                Joint("j002", JointType.Revolute, "g002", "g001", axis),   // backwards
                Joint("j003", JointType.Revolute, "g002", "g003", axis),
                Joint("j004", JointType.Revolute, "g003", "g000", axis),   // backwards
            };

            var result = LoopAnalyzer.Analyze(groups, joints);

            var loop = Assert.Single(result.Loops);
            Assert.Equal("loop001", loop.Id);
            Assert.Equal(new[] { "j001", "j002", "j003", "j004" }, loop.MemberJoints);
            // j001 drives (ground-incident revolute, lowest id) and the cut
            // sits just past its moving group, so the rest of the ring is
            // IK-solved.
            Assert.Equal("j002", loop.ClosureJoint);
            Assert.True(loop.Planar);
            Assert.NotNull(loop.PlaneNormal);
            Assert.Equal(axis, loop.PlaneNormal);
            Assert.Equal("j001", loop.SuggestedDriverJoint);

            // Every joint's parent end is nearer the grounded root:
            // g000 at depth 0, g001/g003 at 1, g002 at 2.
            Assert.Equal(4, result.Joints.Count);
            AssertOriented(result.Joints[0], "g000", "g001");
            AssertOriented(result.Joints[1], "g001", "g002");
            AssertOriented(result.Joints[2], "g003", "g002");
            AssertOriented(result.Joints[3], "g000", "g003");
        }

        /// <summary>
        /// A ring with one slide is a slider-crank, and it is cut AT the
        /// slide. No rotational solver can lengthen a sliding joint, so
        /// leaving it inside the solved chain freezes the mechanism, and
        /// cutting a pin instead leaves the slide locked in the tree, which
        /// freezes it just the same. Cut here and each sliding body hangs off
        /// its own pin, free to aim at the other.
        ///
        /// (Until 2026-08-24 this cut a revolute, on the reasoning that an IK
        /// point constraint over-constrains a sliding interface. True, and
        /// beside the point: the interface should not be in the chain at all.)
        /// </summary>
        [Fact]
        public void ARingWithOneSlideIsCutAtTheSlide()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"),
                Group("g002"),
                Group("g003"),
            };
            var axis = new double[] { 0, 0, 1 };
            var joints = new List<RigJoint>
            {
                Joint("j001", JointType.Revolute, "g000", "g001", axis),
                Joint("j002", JointType.Revolute, "g001", "g002", axis),
                Joint("j003", JointType.Prismatic, "g002", "g003", new double[] { 1, 0, 0 }),
                Joint("j004", JointType.Revolute, "g003", "g000", axis),   // backwards
            };

            var result = LoopAnalyzer.Analyze(groups, joints);

            var loop = Assert.Single(result.Loops);
            Assert.Equal(new[] { "j001", "j002", "j003", "j004" }, loop.MemberJoints);
            Assert.Equal("j003", loop.ClosureJoint);
            // j001 is the only edge touching neither sliding body, so it is
            // the one a hand can pose to work the mechanism.
            Assert.Equal("j001", loop.SuggestedDriverJoint);
            Assert.True(loop.Planar);

            // Tree j001/j002/j004: g001 at depth 1 under g000, g002 at 2
            // under g001, g003 at 1 under g000.
            AssertOriented(result.Joints[0], "g000", "g001");
            AssertOriented(result.Joints[1], "g001", "g002");
            AssertOriented(result.Joints[2], "g003", "g002");   // closure
            AssertOriented(result.Joints[3], "g000", "g003");
        }

        /// <summary>With no slide anywhere, the IK closure stands, and it is
        /// a point coincidence, so between two otherwise equal pairings the
        /// cut goes on the joint whose bodies SHARE a point. A planar cut
        /// would ask the consumer to pin two bodies at a point that slides in
        /// two directions.</summary>
        [Fact]
        public void WithNoSlideTheCutGoesOnAPin()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"),
                Group("g002"),
                Group("g003"),
            };
            var axis = new double[] { 0, 0, 1 };
            var joints = new List<RigJoint>
            {
                Joint("j001", JointType.Revolute, "g000", "g001", axis),
                Joint("j002", JointType.Cylindrical, "g001", "g002", axis),
                Joint("j003", JointType.Planar, "g002", "g003", axis),
                Joint("j004", JointType.Revolute, "g003", "g000", axis),
            };

            var result = LoopAnalyzer.Analyze(groups, joints);

            var loop = Assert.Single(result.Loops);
            Assert.Equal("j002", loop.ClosureJoint);
            Assert.Equal("j001", loop.SuggestedDriverJoint);
        }

        /// <summary>
        /// Oscar's question, 2026-08-24: with the loop cut at the ram, what
        /// stops the clamp? In SolidWorks the ram bottoms out and the clamp
        /// can go no further, so the stroke limit must arrive on the driver,
        /// converted through the triangle, or the clamp swings past the stop
        /// and leaves the rod behind.
        ///
        /// The triangle here is a right angle at rest, chosen so the answer
        /// can be read off by hand: A = (1,0,0), B = origin, C = (0,0,1), so
        /// |AB| = |BC| = 1 and the ram is √2 long. Shortening it to 1 closes
        /// the corner from 90° to 60°; lengthening it to √3 opens it to 120°.
        ///
        /// Run twice, because a stroke is a SIGNED displacement along the
        /// slide's own axis and the ram's length is a distance. Reverse the
        /// axis and the same mechanism reads as a falling coordinate; the
        /// answer must not change. Live ClampRig (2026-08-24) has one
        /// ram of each hand and derived +90° for one clamp and +38° for the
        /// other from the same triangle and the same 500 mm stroke.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ASliderCrankCarriesItsStrokeLimitOntoTheDriver(bool reversedSlide)
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),   // machine body
                Group("g001"),                   // clamp
                Group("g002"),                   // ram barrel
                Group("g003"),                   // ram rod
            };
            var pin = new double[] { 0, 1, 0 };
            double root2 = Math.Sqrt(2.0), root3 = Math.Sqrt(3.0);

            var clampPin = Joint("j001", JointType.Revolute, "g000", "g001", pin);
            clampPin.Origin = new double[] { 0, 0, 0 };            // B
            var barrelPin = Joint("j002", JointType.Revolute, "g000", "g002", pin);
            barrelPin.Origin = new double[] { 1, 0, 0 };           // A
            // A -> C, or the same line measured the other way round.
            var slideAxis = reversedSlide
                ? new double[] { 1, 0, -1 }
                : new double[] { -1, 0, 1 };
            var stroke = Joint("j003", JointType.Prismatic, "g002", "g003",
                               slideAxis);
            stroke.TranslationLimit = reversedSlide
                ? new JointLimit
                {
                    Min = -(0.5 + (root3 - root2)),
                    Max = -(0.5 + (1.0 - root2)),
                    ValueAtRest = -0.5,
                }
                : new JointLimit
                {
                    Min = 0.5 + (1.0 - root2),    // |AC| may shrink to 1
                    Max = 0.5 + (root3 - root2),  // and grow to sqrt(3)
                    ValueAtRest = 0.5,
                };
            var rodPin = Joint("j004", JointType.Cylindrical, "g001", "g003", pin);
            rodPin.Origin = new double[] { 0, 0, 1 };              // C

            var result = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { clampPin, barrelPin, stroke, rodPin });

            var loop = Assert.Single(result.Loops);
            Assert.Equal("j003", loop.ClosureJoint);
            Assert.Equal("j001", loop.SuggestedDriverJoint);

            // 90° at rest, 60° at the short end, 120° at the long end.
            Assert.NotNull(clampPin.RotationLimit);
            double half = Math.PI / 6.0;
            Assert.Equal(-half, clampPin.RotationLimit.Min, 9);
            Assert.Equal(half, clampPin.RotationLimit.Max, 9);
            Assert.Equal(0.0, clampPin.RotationLimit.ValueAtRest, 12);
            Assert.Contains("derived from the stroke limit", clampPin.Notes);

            // The rod keeps its own stroke: the two describe one constraint
            // and must not disagree.
            Assert.NotNull(stroke.TranslationLimit);
        }

        /// <summary>
        /// The two mounts of an aim pair are seated ON the ram they close.
        ///
        /// A pin's origin is only defined up to sliding along its own axis,
        /// and SolidWorks hands back whichever point the mate entity sat on.
        /// The aim closure stands in for the slide by pointing each half at
        /// the other's pivot, which is the slide only when both pivots are on
        /// the slide's axis.
        ///
        /// Live ClampRig (2026-08-24): the bore pin came in 45 mm above
        /// the ram's axis and the rod pin 31 mm below it, so the two halves
        /// aimed across the ram and the rod met the bore at an angle. Both
        /// pin lines cross the ram axis exactly at the ram's centre plane:
        /// every number below is off that assembly's own manifest.
        /// </summary>
        /// <summary>
        /// Live TongRig (2026-09-14): a hydraulic tong. Two arms hinged on
        /// a base, a cylinder between them whose body sits on a cone bore
        /// over the rod with a point-to-point stroke limit. That leaves the
        /// rod free to SPIN in the barrel, so the stroke arrives CYLINDRICAL,
        /// and the loop narrowing that kills the spin runs after closures
        /// are chosen. Read as "no slide in the ring", the ram was closed
        /// with IK on one arm: its own two joints stayed free inputs, and
        /// its length never changed when the arm was posed.
        ///
        /// A cylindrical whose axis passes through both of its mount pins is
        /// a ram's stroke, and the ring closes as an aim pair on it.
        /// </summary>
        [Fact]
        public void ARamWhoseRodMaySpinIsStillAnAimPair()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),   // base
                Group("g001"),                   // arm A
                Group("g002"),                   // arm B
                Group("g003"),                   // cylinder body
                Group("g004"),                   // cylinder rod
            };
            var pin = new double[] { 1, 0, 0 };
            var armA = Joint("j001", JointType.Revolute, "g000", "g001", pin);
            armA.Origin = new double[] { -0.2645, -0.3057, 0.37 };
            var armB = Joint("j002", JointType.Revolute, "g000", "g002", pin);
            armB.Origin = new double[] { -0.2645, -0.3057, -0.37 };
            var rodPin = Joint("j003", JointType.Revolute, "g001", "g004", pin);
            rodPin.Origin = new double[] { -0.2375, -0.2327, 0.2948 };
            var bodyPin = Joint("j006", JointType.Revolute, "g002", "g003", pin);
            bodyPin.Origin = new double[] { -0.15, -0.2353, -0.2925 };
            // The stroke, still cylindrical, its axis threading both pins.
            var stroke = Joint("j009", JointType.Cylindrical, "g004", "g003",
                               new double[] { 0, 0.0042873, 0.99999 });
            stroke.Origin = new double[] { 0, -0.233, 0.2249 };

            var result = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { armA, armB, rodPin, bodyPin, stroke });

            var loop = Assert.Single(result.Loops);
            Assert.Equal("aim_pair", loop.ClosureKind);
            Assert.Equal("j009", loop.ClosureJoint);
            // The driver is a pin touching neither half of the ram: one of
            // the arms, and the first by id.
            Assert.Equal("j001", loop.SuggestedDriverJoint);
        }

        /// <summary>
        /// The same tong with the rod eye's OWN mount pin mated by a
        /// concentric alone, so it arrives cylindrical too (live TongRig
        /// on the current DLL, 2026-09-14). That pin runs parallel to the
        /// arm pin beside it and fails the stroke test; whichever way the
        /// ring is walked it must neither hide the stroke nor veto it. The
        /// first version returned "ambiguous" on meeting any second
        /// cylindrical, and the ram went back to an IK closure.
        /// </summary>
        [Fact]
        public void ALoosePinBesideTheRamDoesNotHideItsStroke()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"), Group("g002"), Group("g003"), Group("g004"),
            };
            var pin = new double[] { 1, 0, 0 };
            var armA = Joint("j001", JointType.Revolute, "g000", "g001", pin);
            armA.Origin = new double[] { -0.2645, -0.3057, 0.37 };
            var armB = Joint("j002", JointType.Revolute, "g000", "g002", pin);
            armB.Origin = new double[] { -0.2645, -0.3057, -0.37 };
            var rodPin = Joint("j004", JointType.Cylindrical, "g001", "g004", pin);
            rodPin.Origin = new double[] { -0.2375, -0.2327, 0.2948 };
            var bodyPin = Joint("j008", JointType.Revolute, "g002", "g003", pin);
            bodyPin.Origin = new double[] { -0.15, -0.2353, -0.2925 };
            var stroke = Joint("j009", JointType.Cylindrical, "g003", "g004",
                               new double[] { 0, 0.0042873, 0.99999 });
            stroke.Origin = new double[] { 0, -0.233, 0.2248 };

            // Both joint orders, so both ring directions are exercised.
            foreach (var joints in new[]
            {
                new List<RigJoint> { armA, armB, rodPin, bodyPin, stroke },
                new List<RigJoint> { armB, armA, bodyPin, stroke, rodPin },
            })
            {
                var result = LoopAnalyzer.Analyze(groups, joints);
                var loop = Assert.Single(result.Loops);
                Assert.Equal("aim_pair", loop.ClosureKind);
                Assert.Equal("j009", loop.ClosureJoint);
            }
        }

        /// <summary>The same shape of ring with the cylindrical being a
        /// LOOSE PIN, not a stroke: corpus 06 fourbar's coupler-rocker pin,
        /// concentric only, its axis parallel to every other pin and a bar
        /// length away from them. Not a ram, and it must not become one.</summary>
        [Fact]
        public void ALoosePinInAFourBarIsNotARam()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"), Group("g002"), Group("g003"),
            };
            var z = new double[] { 0, 0, 1 };
            var a = Joint("j001", JointType.Revolute, "g000", "g001", z);
            a.Origin = new double[] { 0.05, 0, 0.0025 };
            var b = Joint("j002", JointType.Revolute, "g001", "g002", z);
            b.Origin = new double[] { 0.08, 0, 0.0075 };
            var loose = Joint("j003", JointType.Cylindrical, "g002", "g003", z);
            loose.Origin = new double[] { 0.0092308, -0.037306, 0.0075 };
            var d = Joint("j004", JointType.Revolute, "g003", "g000", z);
            d.Origin = new double[] { -0.05, 0, 0.0025 };

            var result = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { a, b, loose, d });

            var loop = Assert.Single(result.Loops);
            Assert.NotEqual("aim_pair", loop.ClosureKind);
        }

        /// <summary>
        /// Two loops of one mechanism that share a joint must be driven from
        /// the same input, or each makes the other's driver a driven bone
        /// and the consumer refuses the cycle (live TongRig, 2026-09-14).
        /// Here the second loop's own ranking would prefer its LIMITED
        /// anchor pin; the pin the first loop already drives wins instead.
        /// </summary>
        [Fact]
        public void LoopsThatShareAJointShareItsDriver()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"), Group("g002"), Group("g003"),
                Group("g005"), Group("g006"),
            };
            var z = new double[] { 0, 0, 1 };
            RigJoint Pin(string id, string p, string c, double x, double y)
            {
                var j = Joint(id, JointType.Revolute, p, c, z);
                j.Origin = new double[] { x, y, 0 };
                return j;
            }
            var shared = Pin("j001", "g000", "g001", 0, 0);
            var j002 = Pin("j002", "g000", "g002", 0.3, 0);
            var j003 = Pin("j003", "g001", "g003", 0, 0.1);
            var j004 = Pin("j004", "g003", "g002", 0.3, 0.1);
            var j007 = Pin("j007", "g001", "g005", 0, 0.2);
            var j008 = Pin("j008", "g005", "g006", 0.5, 0.2);
            var j009 = Pin("j009", "g000", "g006", 0.5, 0);
            j009.RotationLimit = new JointLimit { Min = -1, Max = 1, ValueAtRest = 0 };

            var result = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { shared, j002, j003, j004, j007, j008, j009 });

            Assert.Equal(2, result.Loops.Count);
            var drivers = new HashSet<string>();
            foreach (var lp in result.Loops) drivers.Add(lp.SuggestedDriverJoint);
            Assert.Equal(new[] { "j001" }, new List<string>(drivers).ToArray());
        }

        [Fact]
        public void AnAimPairSeatsBothItsPinsOnTheRamAxis()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),   // machine body
                Group("g013"),                   // clamp
                Group("g018"),                   // ram barrel
                Group("g019"),                   // ram rod
            };
            var pin = new double[] { 0, 1, 0 };

            var clampPin = Joint("j013", JointType.Revolute, "g000", "g013", pin);
            clampPin.Origin = new double[] { 0.45621065309788655, 0.105,
                                             -1.2600000000000042 };
            var barrelPin = Joint("j018", JointType.Revolute, "g000", "g018", pin);
            barrelPin.Origin = new double[] { 0.5249999999999665, 0.045,
                                              -0.31568475327996115 };
            var slide = Joint("j041", JointType.Prismatic, "g018", "g019",
                              new double[] { 0.2450864909632585, 0,
                                             -0.9695012181257519 });
            slide.Origin = new double[] { 0.6966653529012765, 0,
                                          -0.9947502052102066 };
            var rodPin = Joint("j040", JointType.Cylindrical, "g013", "g019", pin);
            rodPin.Origin = new double[] { 0.6966653529014326, -0.031,
                                           -0.9947502052101866 };

            var result = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { clampPin, barrelPin, slide, rodPin });

            var loop = Assert.Single(result.Loops);
            Assert.Equal("aim_pair", loop.ClosureKind);
            Assert.Equal("j041", loop.ClosureJoint);

            // Both pins slid down their own axes onto the ram's centre plane.
            Assert.Equal(0.0, barrelPin.Origin[1], 9);
            Assert.Equal(0.0, rodPin.Origin[1], 9);
            // ...and nowhere else: only the height moved.
            Assert.Equal(0.5249999999999665, barrelPin.Origin[0], 12);
            Assert.Equal(-0.31568475327996115, barrelPin.Origin[2], 12);
            Assert.Equal(0.6966653529014326, rodPin.Origin[0], 12);
            Assert.Equal(-0.9947502052101866, rodPin.Origin[2], 12);

            // Both now lie ON the ram's axis, which is the whole point: the
            // line joining them IS the slide direction.
            var along = new double[]
            {
                rodPin.Origin[0] - barrelPin.Origin[0],
                rodPin.Origin[1] - barrelPin.Origin[1],
                rodPin.Origin[2] - barrelPin.Origin[2],
            };
            var unit = MathOps.Normalized(along);
            Assert.Equal(1.0,
                Math.Abs(MathOps.Dot(unit, MathOps.Normalized(slide.Axis))), 9);

            // A pin that really does cross the ram says nothing about the
            // seating. The rod pin does carry a note, but a different one:
            // it is a concentric-only pair, so pairwise it reads cylindrical,
            // and CutTransfer takes the slide the ring cannot make. That is
            // the ram this whole analysis was written for.
            Assert.True(string.IsNullOrEmpty(barrelPin.Notes));
            Assert.DoesNotContain("clear of the axis", rodPin.Notes ?? "");
            Assert.Equal(JointType.Revolute, rodPin.Type);
            Assert.Contains("removes the slide", rodPin.Notes);

            // The clamp is not part of the pair and keeps its own height.
            Assert.Equal(0.105, clampPin.Origin[1], 12);
        }

        /// <summary>
        /// A pin that genuinely misses the ram cannot be seated onto it, so
        /// the manifest says by how much it misses instead of pretending.
        /// </summary>
        [Fact]
        public void APinThatMissesTheRamIsReportedRatherThanMoved()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"),                   // clamp
                Group("g002"),                   // barrel
                Group("g003"),                   // rod
            };
            var pin = new double[] { 0, 1, 0 };

            var clampPin = Joint("j001", JointType.Revolute, "g000", "g001", pin);
            clampPin.Origin = new double[] { 0, 0, 0 };
            // The bore pin sits 20 mm to the side of the ram's own line, and
            // no point of a vertical pin can reach it.
            var barrelPin = Joint("j002", JointType.Revolute, "g000", "g002", pin);
            barrelPin.Origin = new double[] { 1.0, 0.3, 0.02 };
            var slide = Joint("j003", JointType.Prismatic, "g002", "g003",
                              new double[] { -1, 0, 0 });
            slide.Origin = new double[] { 0, 0, 0 };
            var rodPin = Joint("j004", JointType.Cylindrical, "g001", "g003", pin);
            rodPin.Origin = new double[] { 0, 0.5, 0 };

            LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { clampPin, barrelPin, slide, rodPin });

            // Seated as far as it can be: down onto the ram's own height,
            // and the sideways miss reported in millimetres.
            Assert.Equal(0.0, barrelPin.Origin[1], 12);
            Assert.Equal(0.02, barrelPin.Origin[2], 12);
            Assert.Contains("20 mm clear of the axis", barrelPin.Notes);
            Assert.Contains("j003", barrelPin.Notes);
        }

        /// <summary>A pin running ALONG the ram has no nearest point: every
        /// point of it is the same distance away. Nothing is moved and the
        /// manifest says why.</summary>
        [Fact]
        public void APinParallelToTheRamIsLeftWhereItIs()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"),
                Group("g002"),
                Group("g003"),
            };
            var pin = new double[] { 0, 1, 0 };

            var clampPin = Joint("j001", JointType.Revolute, "g000", "g001", pin);
            clampPin.Origin = new double[] { 0, 0, 0 };
            // A bore pin along the ram: a ram free to spin about its own axis.
            var barrelPin = Joint("j002", JointType.Revolute, "g000", "g002",
                                  new double[] { 1, 0, 0 });
            barrelPin.Origin = new double[] { 1.0, 0.3, 0.0 };
            var slide = Joint("j003", JointType.Prismatic, "g002", "g003",
                              new double[] { -1, 0, 0 });
            slide.Origin = new double[] { 0, 0, 0 };
            var rodPin = Joint("j004", JointType.Cylindrical, "g001", "g003", pin);
            rodPin.Origin = new double[] { 0, 0.5, 0 };

            LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { clampPin, barrelPin, slide, rodPin });

            Assert.Equal(1.0, barrelPin.Origin[0], 12);
            Assert.Equal(0.3, barrelPin.Origin[1], 12);
            Assert.Contains("parallel to the slide", barrelPin.Notes);
        }

        /// <summary>
        /// An IK closure re-joins a cut by making one POINT meet again, so a
        /// cut whose bodies share no point cannot be closed that way: the
        /// consumer would drag a body to pin a face against a face.
        ///
        /// Live ClampRig (2026-08-24): the two hydraulic rams are held
        /// level by a coincidence between their subassembly mid-planes, which
        /// reads as a planar joint between the two barrels. Cut there and
        /// IK-closed, it pulled one barrel off the Damped Track aiming it at
        /// its own rod, and that ram stopped following its clamp.
        /// </summary>
        [Fact]
        public void ACutThatSharesNoPointIsLeftUnsolved()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),   // machine body
                Group("g001"),                   // one ram barrel
                Group("g002"),                   // the other
            };
            var pin = new double[] { 0, 1, 0 };

            var left = Joint("j001", JointType.Cylindrical, "g000", "g001", pin);
            left.Origin = new double[] { -1, 0, 0 };
            var right = Joint("j002", JointType.Revolute, "g000", "g002", pin);
            right.Origin = new double[] { 1, 0, 0 };
            var level = Joint("j003", JointType.Planar, "g001", "g002", pin);
            level.Origin = new double[] { 0, 0, 0 };

            var result = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { left, right, level });

            var loop = Assert.Single(result.Loops);
            Assert.Equal("j003", loop.ClosureJoint);
            Assert.Equal("none", loop.ClosureKind);
        }

        /// <summary>The same ring with a PIN in the middle is an ordinary
        /// four-bar and keeps its solved closure.</summary>
        [Fact]
        public void ACutOnAPinIsStillSolved()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"),
                Group("g002"),
            };
            var pin = new double[] { 0, 1, 0 };

            var left = Joint("j001", JointType.Revolute, "g000", "g001", pin);
            left.Origin = new double[] { -1, 0, 0 };
            var right = Joint("j002", JointType.Revolute, "g000", "g002", pin);
            right.Origin = new double[] { 1, 0, 0 };
            var coupler = Joint("j003", JointType.Revolute, "g001", "g002", pin);
            coupler.Origin = new double[] { 0, 0, 0.4 };

            var result = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { left, right, coupler });

            var loop = Assert.Single(result.Loops);
            Assert.Equal("j003", loop.ClosureJoint);
            Assert.Equal("ik", loop.ClosureKind);
        }

        /// <summary>A driver that carries its OWN limit mate keeps it: what
        /// SolidWorks measured beats what this can infer.</summary>
        [Fact]
        public void ADriverWithItsOwnLimitIsLeftAlone()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"),
                Group("g002"),
                Group("g003"),
            };
            var pin = new double[] { 0, 1, 0 };
            var clampPin = Joint("j001", JointType.Revolute, "g000", "g001", pin);
            clampPin.Origin = new double[] { 0, 0, 0 };
            clampPin.RotationLimit = new JointLimit { Min = 0, Max = 1, ValueAtRest = 0.25 };
            var barrelPin = Joint("j002", JointType.Revolute, "g000", "g002", pin);
            barrelPin.Origin = new double[] { 1, 0, 0 };
            var stroke = Joint("j003", JointType.Prismatic, "g002", "g003",
                               new double[] { 1, 0, -1 });
            stroke.TranslationLimit = new JointLimit { Min = 0.4, Max = 0.9, ValueAtRest = 0.5 };
            var rodPin = Joint("j004", JointType.Cylindrical, "g001", "g003", pin);
            rodPin.Origin = new double[] { 0, 0, 1 };

            LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { clampPin, barrelPin, stroke, rodPin });

            Assert.Equal(0.0, clampPin.RotationLimit.Min, 12);
            Assert.Equal(1.0, clampPin.RotationLimit.Max, 12);
            Assert.Equal(0.25, clampPin.RotationLimit.ValueAtRest, 12);
        }

        /// <summary>
        /// Live ClampRig's lead screw and cutting head (2026-08-24).
        /// Both slide along the machine on their own guides, and the head is
        /// mated to the rod, so in SolidWorks driving the lead screw carries
        /// the head with it.
        ///
        /// Cutting BETWEEN them left both hanging off ground as siblings, and
        /// nothing could then make one carry the other: the consumer's closure
        /// rotates a chain, and there is nothing here to rotate. Cutting an
        /// anchor edge instead turns the loop into a chain: pose the lead
        /// screw and the head simply comes along, which is what the mates said
        /// all along. The cut is then satisfied by the parenting, so there is
        /// nothing left to solve and the closure kind says so.
        /// </summary>
        [Fact]
        public void TwoBodiesSlidingOnGroundChainInsteadOfBecomingSiblings()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),   // machine body
                Group("g001"),                   // lead screw rod
                Group("g002"),                   // cutting head
            };
            var along = new double[] { 0, 0, 1 };

            var rod = Joint("j001", JointType.Prismatic, "g000", "g001", along);
            rod.Origin = new double[] { 0, 0, 0 };
            // The lead screw is the modelled input: it is the one with a
            // stroke, so it drives.
            rod.TranslationLimit = new JointLimit { Min = 0, Max = 0.85, ValueAtRest = 0.0 };
            var head = Joint("j002", JointType.Prismatic, "g000", "g002", along);
            head.Origin = new double[] { 0, 0, 0.1 };
            var contact = Joint("j003", JointType.Planar, "g001", "g002", along);
            contact.Origin = new double[] { 0, 0, 0.1 };

            var result = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { rod, head, contact });

            var loop = Assert.Single(result.Loops);
            Assert.Equal("j001", loop.SuggestedDriverJoint);
            Assert.Equal("j002", loop.ClosureJoint);
            Assert.Equal("none", loop.ClosureKind);

            // The tree that leaves: ground -> rod -> head. The head is a child
            // of the rod, not a sibling of it.
            AssertOriented(result.Joints[0], "g000", "g001");
            AssertOriented(result.Joints[2], "g001", "g002");
        }

        /// <summary>The same shape with a PIN at the anchor is an ordinary
        /// loop and keeps the solved closure: the chain rule is about two
        /// slides having nothing to rotate, not about ring size.</summary>
        [Fact]
        public void APinAtTheAnchorStillGetsASolvedClosure()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"),
                Group("g002"),
            };
            var axis = new double[] { 0, 0, 1 };
            var crank = Joint("j001", JointType.Revolute, "g000", "g001", axis);
            crank.Origin = new double[3];
            var rocker = Joint("j002", JointType.Revolute, "g000", "g002", axis);
            rocker.Origin = new double[] { 0.3, 0, 0 };
            var coupler = Joint("j003", JointType.Revolute, "g001", "g002", axis);
            coupler.Origin = new double[] { 0.15, 0.1, 0 };

            var result = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { crank, rocker, coupler });

            var loop = Assert.Single(result.Loops);
            Assert.Equal("j003", loop.ClosureJoint);
            Assert.Equal("ik", loop.ClosureKind);
        }

        /// <summary>
        /// The live ClampRig hydraulic ram, both hands. The machine is symmetric:
        /// each ram's bore is pinned to the body, the clamp is pinned to the
        /// body, and the ram's rod drives the clamp's lug, so the two sides
        /// must be planned identically. Under the old rule nothing but joint
        /// ID ORDER separated them: one side cut at the clamp pin (clamp
        /// drives, ram swings, correct) and the other cut at the ram's own
        /// stroke, which put the clamp inside the driven chain and froze it
        /// solid (2026-08-24). Whichever way the ids fall, the cut is the pin.
        /// </summary>
        [Theory]
        [InlineData("j001", "j002")]     // clamp edge sorts first
        [InlineData("j002", "j001")]     // ram edge sorts first
        public void BothHandsOfAMirroredRamGetTheSamePlan(
            string clampEdge, string ramEdge)
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),   // machine body
                Group("g001"),                   // clamp
                Group("g002"),                   // ram barrel
                Group("g003"),                   // ram rod
            };
            var pin = new double[] { 0, 1, 0 };
            var joints = new List<RigJoint>
            {
                Joint(clampEdge, JointType.Revolute, "g000", "g001", pin),
                Joint(ramEdge, JointType.Revolute, "g000", "g002", pin),
                Joint("j003", JointType.Prismatic, "g002", "g003",
                      new double[] { 0.245, 0, -0.969 }),
                Joint("j004", JointType.Cylindrical, "g001", "g003", pin),
            };

            var result = LoopAnalyzer.Analyze(groups, joints);

            var loop = Assert.Single(result.Loops);
            // The clamp is the input the operator poses, and the cut is the
            // ram's own stroke: barrel hangs off the body pin, rod off the
            // clamp pin, and the two halves aim at each other.
            Assert.Equal(clampEdge, loop.SuggestedDriverJoint);
            Assert.Equal("j003", loop.ClosureJoint);
            Assert.True(loop.Planar);
        }

        /// <summary>Live corpus 06 (2026-08-22): the ground–crank revolute
        /// must stay a tree edge and drive; the cut goes just past the crank
        /// so coupler and rocker are IK-solved. The old rule cut the driver's
        /// own edge and the four-bar froze solid. The rocker–coupler joint is
        /// cylindrical here because a lone concentric classifies that way:
        /// the driver preference must still avoid cutting it.</summary>
        [Fact]
        public void FourBarCutsJustPastTheDriver()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),   // ground
                Group("g001"),                   // coupler
                Group("g002"),                   // crank
                Group("g003"),                   // rocker
            };
            var axis = new double[] { 0, 0, -1 };
            var joints = new List<RigJoint>
            {
                Joint("j001", JointType.Revolute, "g000", "g002", axis),
                Joint("j002", JointType.Revolute, "g000", "g003", axis),
                Joint("j003", JointType.Revolute, "g001", "g002", axis),
                Joint("j004", JointType.Cylindrical, "g003", "g001", axis),
            };

            var result = LoopAnalyzer.Analyze(groups, joints);

            var loop = Assert.Single(result.Loops);
            Assert.Equal(new[] { "j001", "j002", "j003", "j004" }, loop.MemberJoints);
            Assert.Equal("j003", loop.ClosureJoint);
            Assert.Equal("j001", loop.SuggestedDriverJoint);
            Assert.True(loop.Planar);

            AssertOriented(result.Joints[0], "g000", "g002");
            AssertOriented(result.Joints[1], "g000", "g003");
            AssertOriented(result.Joints[2], "g002", "g001");   // closure re-oriented rootward
            AssertOriented(result.Joints[3], "g003", "g001");
        }

        /// <summary>Live corpus 06 parallelogram2 (2026-08-22): redundant
        /// parallel mates between opposing links export as free joints. Free
        /// joints must not be graph edges: the consumer never parents them,
        /// so a free "tree edge" here made every real ring joint a loop
        /// closure and the Blender side refused the manifest as
        /// disconnected. The ring must come out as exactly one loop with the
        /// free joints in no loop at all.</summary>
        [Fact]
        public void FreeJointsAreNotGraphEdges()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),   // bottom bar
                Group("g001"),                   // crank
                Group("g002"),                   // top bar
                Group("g003"),                   // second crank
            };
            var axis = new double[] { 0, 0, -1 };
            var joints = new List<RigJoint>
            {
                Joint("j001", JointType.Revolute, "g000", "g001", axis),
                ParallelFree("j002", "g000", "g002", new double[] { 1, 0, 0 }),
                Joint("j003", JointType.Revolute, "g000", "g003", axis),
                Joint("j004", JointType.Cylindrical, "g001", "g002", axis),
                ParallelFree("j005", "g001", "g003", new double[] { 0, -1, 0 }),
                Joint("j006", JointType.Revolute, "g002", "g003", axis),
            };

            var result = LoopAnalyzer.Analyze(groups, joints);

            var loop = Assert.Single(result.Loops);
            Assert.Equal(new[] { "j001", "j003", "j004", "j006" }, loop.MemberJoints);
            // Driving j001 would cut the cylindrical j004; driving j003 cuts
            // the revolute j006, so j003 drives.
            Assert.Equal("j006", loop.ClosureJoint);
            Assert.Equal("j003", loop.SuggestedDriverJoint);
            Assert.True(loop.Planar);

            // The parallel mates duplicate what the loop's IK closure already
            // enforces: no coupling may be synthesized on the ring joints (a
            // driver would fight the IK), and the free joints are reported
            // redundant rather than under-defined.
            foreach (var j in result.Joints) Assert.Null(j.Coupling);
            Assert.Empty(result.CoupledFreeJointIds);
            Assert.Equal(new[] { "j002", "j005" }, result.RedundantFreeJointIds);
        }

        /// <summary>Live corpus 06 parallelogram3 (2026-08-22): a corner pin
        /// deleted, parallel mates between opposing links instead. Pairwise
        /// that is three independent revolutes plus two free joints, but the
        /// parallel mates lock relative orientation about the common hinge
        /// direction, which makes the signed joint angles equal: gear
        /// couplings with geometric signs. crank2 follows crank1 1:1; the
        /// top bar counter-rotates on crank2's pin to stay level.</summary>
        [Fact]
        public void ParallelMatesBecomeGearCouplings()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),   // bottom bar
                Group("g001"),                   // crank 1
                Group("g002"),                   // top bar
                Group("g003"),                   // crank 2
            };
            var axis = new double[] { 0, 0, -1 };
            var joints = new List<RigJoint>
            {
                Joint("j001", JointType.Revolute, "g000", "g001", axis),
                ParallelFree("j002", "g000", "g002", new double[] { 1, 0, 0 }),
                Joint("j003", JointType.Revolute, "g000", "g003", axis),
                ParallelFree("j004", "g001", "g003", new double[] { 0, -1, 0 }),
                Joint("j005", JointType.Revolute, "g002", "g003", axis),   // backwards
            };

            var result = LoopAnalyzer.Analyze(groups, joints);

            Assert.Empty(result.Loops);
            Assert.Equal(new[] { "j002", "j004" }, result.CoupledFreeJointIds);
            Assert.Empty(result.RedundantFreeJointIds);

            var j003 = result.Joints[2];
            Assert.NotNull(j003.Coupling);
            Assert.Equal("gear", j003.Coupling.Kind);
            Assert.Equal("j001", j003.Coupling.DriverJoint);
            Assert.Equal(1.0, j003.Coupling.Ratio);

            var j005 = result.Joints[4];
            AssertOriented(j005, "g003", "g002");
            Assert.NotNull(j005.Coupling);
            Assert.Equal("gear", j005.Coupling.Kind);
            Assert.Equal("j003", j005.Coupling.DriverJoint);
            Assert.Equal(-1.0, j005.Coupling.Ratio);

            // The free joints stay free: the coupling models them, the tree
            // never parents them.
            Assert.Null(result.Joints[1].Coupling);
            Assert.Null(result.Joints[3].Coupling);
        }

        /// <summary>A pure chain has no non-tree edge and therefore no loop,
        /// whatever the joint count.</summary>
        [Fact]
        public void TreeOnlyAssemblyHasNoLoops()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"),
                Group("g002"),
            };
            var joints = new List<RigJoint>
            {
                Joint("j001", JointType.Revolute, "g000", "g001", new double[] { 0, 0, 1 }),
                Joint("j002", JointType.Prismatic, "g002", "g001", new double[] { 1, 0, 0 }),   // backwards
            };

            var result = LoopAnalyzer.Analyze(groups, joints);

            Assert.Empty(result.Loops);
            Assert.Equal(2, result.Joints.Count);
            AssertOriented(result.Joints[0], "g000", "g001");
            AssertOriented(result.Joints[1], "g001", "g002");
        }

        private static void AssertOriented(RigJoint joint, string parent, string child)
        {
            Assert.Equal(parent, joint.ParentGroup);
            Assert.Equal(child, joint.ChildGroup);
        }
        /// <summary>
        /// Two hydraulic rams that swing two clamps, as on live CutterRig
        /// (2026-09-21), with the two rods held coplanar by a mate between
        /// them and the two clamps by another. Both planar joints hold
        /// nothing, because every hinge turns about their normal. They must
        /// be the cuts. Cutting a rod pin instead hung one rod off the other,
        /// each ram's loop then ran through the other ram, and the manifest
        /// named a driver that was not in its own loop.
        /// </summary>
        [Fact]
        public void APlanarThatHoldsNothingIsTheCutAndEachRamKeepsItsOwnLoop()
        {
            var y = new double[] { 0, 1, 0 };
            var stroke = new double[] { 0.4228, 0, 0.9062 };
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"), Group("g002"),     // clamps
                Group("g003"), Group("g004"),     // ram 1: cylinder, rod
                Group("g005"), Group("g006"),     // ram 2: cylinder, rod
            };
            var joints = new List<RigJoint>
            {
                Joint("j001", JointType.Revolute, "g000", "g001", y),
                Joint("j002", JointType.Revolute, "g000", "g002", y),
                Joint("j003", JointType.Revolute, "g000", "g003", y),
                Joint("j004", JointType.Revolute, "g000", "g005", y),
                Joint("j012", JointType.Planar, "g001", "g002", y),
                Joint("j013", JointType.Revolute, "g001", "g004", y),
                Joint("j014", JointType.Revolute, "g002", "g006", y),
                Joint("j015", JointType.Prismatic, "g003", "g004", stroke),
                Joint("j016", JointType.Planar, "g006", "g004", y),
                Joint("j017", JointType.Prismatic, "g005", "g006", stroke),
            };
            var origins = new Dictionary<string, double[]>
            {
                { "j001", new[] { 0.29, 0.05, 0.56 } }, { "j002", new[] { -0.29, 0.05, 0.56 } },
                { "j003", new[] { 0.19, 0.05, 0.12 } }, { "j004", new[] { -0.19, 0.05, 0.12 } },
                { "j012", new[] { 0.0, 0.07, 0.0 } }, { "j013", new[] { 0.33, 0.05, 0.42 } },
                { "j014", new[] { -0.33, 0.05, 0.42 } }, { "j015", new[] { 0.33, 0.04, 0.42 } },
                { "j016", new[] { -0.33, 0.04, 0.42 } }, { "j017", new[] { -0.33, 0.04, 0.42 } },
            };
            foreach (var j in joints) j.Origin = origins[j.Id];

            var result = LoopAnalyzer.Analyze(groups, joints);

            foreach (var loop in result.Loops)
            {
                Assert.Contains(loop.SuggestedDriverJoint, loop.MemberJoints);
                foreach (var c in loop.DriverCandidates)
                    Assert.Contains(c.DriverJoint, loop.MemberJoints);
            }
            var byClosure = new Dictionary<string, RigLoop>();
            foreach (var loop in result.Loops) byClosure[loop.ClosureJoint] = loop;
            Assert.Equal("none", byClosure["j016"].ClosureKind);
            Assert.Equal("none", byClosure["j012"].ClosureKind);
            Assert.Equal(new[] { "j001", "j003", "j013", "j015" }, byClosure["j015"].MemberJoints);
            Assert.Equal(new[] { "j002", "j004", "j014", "j017" }, byClosure["j017"].MemberJoints);
        }
        /// <summary>
        /// A cutting head slides on the frame and a hub turns in the head.
        /// A blade is bolted to the hub (a pin off the axis) and held to an
        /// end plug by a concentric with its rotation locked, and the blade
        /// face lies on a frame plane, as on live CutterRig (2026-09-22).
        /// The plane holds nothing: the head slides in it and the hub turns
        /// about its normal. So the plane is the cut, the ring stays open,
        /// the hub's hinge stays in the tree and the blade turns. Closed at
        /// the hinge instead, the blade only followed the head.
        /// </summary>
        [Fact]
        public void ABladeOnAFramePlaneStillTurnsWithItsHub()
        {
            var y = new double[] { 0, 1, 0 };
            var z = new double[] { 0, 0, 1 };
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),      // frame
                Group("g007"),                      // cutting head
                Group("g008"),                      // blade
                Group("g009"),                      // hub
                Group("g010"),                      // end plug
            };
            var joints = new List<RigJoint>
            {
                Joint("j005", JointType.Prismatic, "g000", "g007", z),
                Joint("j006", JointType.Planar, "g000", "g008", y),
                Joint("j014", JointType.Revolute, "g007", "g009", y),
                Joint("j017", JointType.Revolute, "g008", "g009", y),
                Joint("j018", JointType.Prismatic, "g008", "g010", y),
                Joint("j019", JointType.Revolute, "g009", "g010", y),
            };
            var origins = new Dictionary<string, double[]>
            {
                { "j005", new[] { 0.0, 0.021, -0.004 } },
                { "j006", new[] { 0.0, -0.0315, -0.004 } },
                { "j014", new[] { 0.0, 0.0905, -0.004 } },
                { "j017", new[] { 0.052, -0.029, -0.034 } },
                { "j018", new[] { 0.0, 0.0105, -0.004 } },
                { "j019", new[] { 0.0, -0.035, -0.004 } },
            };
            foreach (var j in joints) j.Origin = origins[j.Id];

            var result = LoopAnalyzer.Analyze(groups, joints);

            var blade = result.Loops.Find(lp => lp.MemberJoints.Contains("j006"));
            Assert.NotNull(blade);
            Assert.Equal("j006", blade.ClosureJoint);
            Assert.Equal("none", blade.ClosureKind);
            Assert.Equal("j005", blade.SuggestedDriverJoint);
            Assert.Equal(2, blade.Mobility);
            foreach (var lp in result.Loops)
                Assert.NotEqual("j014", lp.ClosureJoint);
            Assert.Equal(JointType.Revolute, joints.Find(j => j.Id == "j014").Type);
            // The blade, hub and plug are one body.
            foreach (var id in new[] { "j017", "j018", "j019" })
                Assert.Equal(JointType.Fixed, joints.Find(j => j.Id == id).Type);
            var mech = result.Mechanisms.Find(m => m.LoopIds.Contains(blade.Id));
            Assert.NotNull(mech);
            Assert.Equal("j005", mech.Inputs[0].Joint);
        }
        /// <summary>
        /// A planar whose normal is square to the ring's hinges holds the
        /// bodies out of the hinges' plane, so it holds something and is not
        /// cut as one that holds nothing.
        /// </summary>
        [Fact]
        public void APlanarAcrossTheHingesIsNotCutAsHoldingNothing()
        {
            var x = new double[] { 1, 0, 0 };
            var z = new double[] { 0, 0, 1 };
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true), Group("g001"), Group("g002"),
            };
            var crank = Joint("j001", JointType.Revolute, "g000", "g001", z);
            crank.Origin = new double[] { 0, 0, 0 };
            var link = Joint("j002", JointType.Revolute, "g001", "g002", z);
            link.Origin = new double[] { 0.1, 0.05, 0 };
            var face = Joint("j003", JointType.Planar, "g000", "g002", x);
            face.Origin = new double[] { 0.3, 0.02, 0 };

            var result = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { crank, link, face });

            var loop = Assert.Single(result.Loops);
            Assert.False(loop.ClosureJoint == "j003" && loop.ClosureKind == "none"
                         && loop.Mobility > 1,
                         "the planar holds the bodies out of the hinges' plane");
        }
        /// <summary>
        /// A crank, a link and a pin in a slot, saved at the dead centre:
        /// crank and link stand in one line square to the slot. At that pose
        /// alone the slot seems to hold nothing, because the crank pin moves
        /// along the slot. One nudge off the pose shows the slot holds the
        /// link's end. It must not be cut as holding nothing.
        /// </summary>
        [Fact]
        public void APinSlotAtADeadCentreIsNotCutAsHoldingNothing()
        {
            var x = new double[] { 1, 0, 0 };
            var y = new double[] { 0, 1, 0 };
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true), Group("g001"), Group("g002"),
            };
            var crank = Joint("j001", JointType.Revolute, "g000", "g001", y);
            crank.Origin = new double[] { 0, 0, 0 };
            var link = Joint("j002", JointType.Revolute, "g001", "g002", y);
            link.Origin = new double[] { 0, 0, 0.1 };
            var slot = Joint("j003", JointType.PinSlot, "g000", "g002", y);
            slot.Origin = new double[] { 0, 0, 0.3 };
            slot.SecondaryAxis = x;

            var result = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { crank, link, slot });

            var loop = Assert.Single(result.Loops);
            Assert.False(loop.ClosureJoint == "j003" && loop.ClosureKind == "none",
                         "the slot was cut as holding nothing at its dead centre");
        }
        /// <summary>
        /// A ring of welds names a weld as its driver, because it has
        /// nothing else to name. That weld must not win the loops met after
        /// it. Live CutterRig (2026-09-22): the lead screw's housing sits
        /// on the frame through a ring of three welds, one of them shared
        /// with the cutting head's loop, and every loop of the mechanism
        /// took the weld as its input. The head could not be moved.
        /// </summary>
        [Fact]
        public void ARingOfWeldsDoesNotHandItsWeldToTheNextLoop()
        {
            var z = new double[] { 0, 0, 1 };
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g007"),      // cutting head
                Group("g014"),      // lead screw housing
                Group("g015"),      // housing part
                Group("g018"),      // screw rod
            };
            var joints = new List<RigJoint>
            {
                Joint("j005", JointType.Prismatic, "g000", "g007", z),
                Joint("j006", JointType.Fixed, "g000", "g014"),
                Joint("j007", JointType.Fixed, "g000", "g015"),
                Joint("j022", JointType.Revolute, "g007", "g018", z),
                Joint("j029", JointType.Fixed, "g014", "g015"),
                Joint("j031", JointType.Cylindrical, "g014", "g018", z),
            };
            var origins = new Dictionary<string, double[]>
            {
                { "j005", new[] { 0.0, 0.4, 0.3 } }, { "j006", new[] { 0.0, 0.5, 0.0 } },
                { "j007", new[] { 0.0, 0.5, 0.1 } }, { "j022", new[] { 0.0, 0.45, 0.3 } },
                { "j029", new[] { 0.0, 0.5, 0.1 } }, { "j031", new[] { 0.0, 0.45, 0.2 } },
            };
            foreach (var j in joints) j.Origin = origins[j.Id];
            joints.Find(j => j.Id == "j031").TranslationLimit =
                new JointLimit { Min = 0, Max = 0.3, ValueAtRest = 0.0005 };

            var result = LoopAnalyzer.Analyze(groups, joints);

            var headLoop = result.Loops.Find(lp => lp.MemberJoints.Contains("j005"));
            Assert.NotNull(headLoop);
            Assert.Equal("j005", headLoop.SuggestedDriverJoint);
            var mech = result.Mechanisms.Find(m => m.LoopIds.Contains(headLoop.Id));
            Assert.NotNull(mech);
            Assert.Equal("j005", mech.Inputs[0].Joint);
        }
        /// <summary>
        /// Two nut-and-washer rings hang off one plate through a ring of
        /// welds, as on live CutterRig (2026-09-22). Taken from another
        /// input, the analyser meets the rings in another order, so one loop
        /// id names two different rings under the two inputs. Every
        /// candidate must still name joints of its own loop: matched across
        /// inputs by id, one washer ring got the cut of the ring beside it,
        /// and the consumer refused the manifest. No weld is offered as an
        /// input.
        /// </summary>
        [Fact]
        public void CandidatesStayInTheirOwnRingAcrossInputs()
        {
            var x = new double[] { 1, 0, 0 };
            var y = new double[] { 0, 1, 0 };
            var z = new double[] { 0, 0, 1 };
            var groups = new List<RigidGroup>
            {
                Group("g007", grounded: true),      // plate
                Group("g008"), Group("g009"),       // two welded brackets
                Group("g010"), Group("g011"),       // nut and washer, side one
                Group("g012"), Group("g013"),       // nut and washer, side two
            };
            var joints = new List<RigJoint>
            {
                Joint("j018", JointType.Fixed, "g007", "g008", x),
                Joint("j019", JointType.Fixed, "g007", "g009", y),
                Joint("j020", JointType.Planar, "g007", "g011", z),
                Joint("j021", JointType.Planar, "g007", "g013", z),
                Joint("j024", JointType.Fixed, "g008", "g009", y),
                Joint("j025", JointType.Planar, "g008", "g010", z),
                Joint("j026", JointType.Planar, "g009", "g012", z),
                Joint("j027", JointType.Fixed, "g011", "g010", z),
                Joint("j028", JointType.Fixed, "g013", "g012", z),
            };
            var origins = new Dictionary<string, double[]>
            {
                { "j018", new[] { 0.04, 0.09, -0.10 } }, { "j019", new[] { -0.04, 0.07, -0.11 } },
                { "j020", new[] { 0.04, 0.08, -0.09 } }, { "j021", new[] { -0.04, 0.08, -0.09 } },
                { "j024", new[] { -0.04, 0.07, -0.11 } }, { "j025", new[] { 0.04, 0.08, -0.10 } },
                { "j026", new[] { -0.04, 0.08, -0.10 } }, { "j027", new[] { 0.04, 0.08, -0.09 } },
                { "j028", new[] { -0.04, 0.08, -0.09 } },
            };
            foreach (var j in joints) j.Origin = origins[j.Id];

            var result = LoopAnalyzer.Analyze(groups, joints);

            foreach (var lp in result.Loops)
            {
                foreach (var c in lp.DriverCandidates)
                {
                    Assert.Contains(c.DriverJoint, lp.MemberJoints);
                    Assert.Contains(c.ClosureJoint, lp.MemberJoints);
                }
            }
            foreach (var mech in result.Mechanisms)
                foreach (var option in mech.Inputs)
                {
                    Assert.NotEqual(JointType.Fixed,
                        joints.Find(j => j.Id == option.Joint).Type);
                    Assert.Equal(mech.LoopIds, option.Loops.ConvertAll(lp => lp.Id));
                    foreach (var lp in option.Loops)
                    {
                        foreach (var c in lp.DriverCandidates)
                        {
                            Assert.Contains(c.DriverJoint, lp.MemberJoints);
                            Assert.Contains(c.ClosureJoint, lp.MemberJoints);
                        }
                    }
                }
        }
        /// <summary>
        /// A cutting head slides on the frame and carries the rod of a lead
        /// screw, which slides in its welded housing with a limit mate, as
        /// on live CutterRig (2026-09-22). The head drives the loop, so the
        /// rod's stroke must stop the head: it had no limit of its own and
        /// ran past both ends of the screw.
        /// </summary>
        [Fact]
        public void AParallelSlidesStrokeStopsTheSlideThatDrives()
        {
            var z = new double[] { 0, 0, 1 };
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g007"),      // cutting head
                Group("g014"),      // lead screw housing
                Group("g018"),      // screw rod
            };
            var joints = new List<RigJoint>
            {
                Joint("j005", JointType.Prismatic, "g000", "g007", z),
                Joint("j006", JointType.Fixed, "g000", "g014"),
                Joint("j022", JointType.Revolute, "g007", "g018", z),
                Joint("j031", JointType.Cylindrical, "g014", "g018", z),
            };
            var origins = new Dictionary<string, double[]>
            {
                { "j005", new[] { 0.0, 0.4, 0.3 } }, { "j006", new[] { 0.0, 0.5, 0.0 } },
                { "j022", new[] { 0.0, 0.45, 0.3 } }, { "j031", new[] { 0.0, 0.45, 0.2 } },
            };
            foreach (var j in joints) j.Origin = origins[j.Id];
            joints.Find(j => j.Id == "j031").TranslationLimit =
                new JointLimit { Min = 0, Max = 0.3, ValueAtRest = 0.0005 };

            var result = LoopAnalyzer.Analyze(groups, joints);

            var loop = Assert.Single(result.Loops);
            Assert.Equal("j005", loop.SuggestedDriverJoint);
            var head = joints.Find(j => j.Id == "j005");
            Assert.NotNull(head.TranslationLimit);
            // The rod sits 0.5 mm off its bottom stop: the head goes down
            // 0.5 mm, and up the rest of the 300 mm.
            Assert.Equal(-0.0005, head.TranslationLimit.Min, 9);
            Assert.Equal(0.2995, head.TranslationLimit.Max, 9);
            Assert.Equal(0.0, head.TranslationLimit.ValueAtRest, 9);
        }

        /// <summary>A second free slide along the same axis leaves the loop
        /// two ways to move, so the stroke says nothing about the driver.</summary>
        [Fact]
        public void TwoFreeSlidesAlongTheAxisDeriveNoLimit()
        {
            var z = new double[] { 0, 0, 1 };
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g007"), Group("g014"), Group("g018"),
            };
            var joints = new List<RigJoint>
            {
                Joint("j005", JointType.Prismatic, "g000", "g007", z),
                Joint("j006", JointType.Prismatic, "g000", "g014", z),
                Joint("j022", JointType.Revolute, "g007", "g018", z),
                Joint("j031", JointType.Cylindrical, "g014", "g018", z),
            };
            foreach (var j in joints) j.Origin = new[] { 0.0, 0.45, 0.2 };
            joints.Find(j => j.Id == "j031").TranslationLimit =
                new JointLimit { Min = 0, Max = 0.3, ValueAtRest = 0.0005 };

            LoopAnalyzer.Analyze(groups, joints);

            foreach (var j in joints)
                if (j.Id != "j031") Assert.Null(j.TranslationLimit);
        }
        /// <summary>
        /// Of a four-bar's two ground links, the one that turns a full circle
        /// drives, whatever the ids say. The live weldingrobot linkage: a
        /// 35.35 mm crank and a 90 mm rocker on a 127.47 mm base, 125 mm
        /// coupler. The rocker swings 49 degrees and a control on it opens
        /// the loop past its toggles.
        /// </summary>
        [Fact]
        public void TheCrankOfAFourBarDrives()
        {
            var x = new double[] { 1, 0, 0 };
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true),
                Group("g001"),      // rocker
                Group("g002"),      // coupler
                Group("g003"),      // crank
            };
            var joints = new List<RigJoint>
            {
                Joint("j002", JointType.Revolute, "g000", "g001", x),
                Joint("j003", JointType.Revolute, "g001", "g002", x),
                Joint("j004", JointType.Revolute, "g000", "g003", x),
                Joint("j005", JointType.Revolute, "g002", "g003", x),
            };
            joints[0].Origin = new[] { 0.0, 0.0, 0.0 };
            joints[1].Origin = new[] { 0.0, 0.014487, 0.088823 };
            joints[2].Origin = new[] { 0.0, 0.12747, 0.0 };
            joints[3].Origin = new[] { 0.0, 0.12747, 0.03535 };

            var result = LoopAnalyzer.Analyze(groups, joints);

            var loop = Assert.Single(result.Loops);
            Assert.Equal("j004", loop.SuggestedDriverJoint);
            Assert.Equal("j004", result.Mechanisms[0].Inputs[0].Joint);
        }
    }
}
