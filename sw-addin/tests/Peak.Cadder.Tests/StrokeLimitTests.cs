using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A ram's stroke carried round to the driver of its loop. The closed
    /// form reads one triangle: the driver's pivot and the ram's two
    /// mounts. Two sides of it are rigid only when the ram's far mount sits
    /// on the driver's parent body and its near mount on the driver's own
    /// body.
    /// </summary>
    public class StrokeLimitTests
    {
        private static RigidGroup Group(string id, bool grounded = false)
        {
            return new RigidGroup { Id = id, Name = id, Grounded = grounded };
        }

        private static RigJoint Joint(string id, string type, string parent, string child,
                                      double[] axis, double[] origin)
        {
            return new RigJoint
            {
                Id = id,
                Type = type,
                ParentGroup = parent,
                ChildGroup = child,
                Axis = axis,
                SecondaryAxis = new double[] { 0, 1, 0 },
                Origin = origin,
            };
        }

        /// <summary>
        /// Live TongRig: two arms hinged on a base, and a ram between the
        /// arms, its rod pinned to arm A and its body to arm B. The stroke
        /// was carried onto arm A's hinge as if arm B stood still, but arm B
        /// turns about its own hinge, so the ram's far end moves. With the
        /// arms linked to open together, the derived stop was about twice
        /// the real one, and arm A opened past the ram's end of stroke.
        /// </summary>
        [Fact]
        public void AStrokeIsNotCarriedThroughAMountOnAnotherMovingBody()
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
            var armA = Joint("j001", JointType.Revolute, "g000", "g001", pin,
                             new double[] { -0.2645, -0.3057, 0.37 });
            var armB = Joint("j002", JointType.Revolute, "g000", "g002", pin,
                             new double[] { -0.2645, -0.3057, -0.37 });
            var rodPin = Joint("j003", JointType.Revolute, "g001", "g004", pin,
                               new double[] { -0.2375, -0.2327, 0.2948 });
            var bodyPin = Joint("j006", JointType.Revolute, "g002", "g003", pin,
                                new double[] { -0.15, -0.2353, -0.2925 });
            var stroke = Joint("j009", JointType.Cylindrical, "g004", "g003",
                               new double[] { 0, 0.0042873, 0.99999 },
                               new double[] { 0, -0.233, 0.2249 });
            stroke.TranslationLimit = new JointLimit { Min = -0.05, Max = 0.10, ValueAtRest = 0 };

            var result = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { armA, armB, rodPin, bodyPin, stroke });

            var loop = Assert.Single(result.Loops);
            Assert.Equal("aim_pair", loop.ClosureKind);
            Assert.Equal("j001", loop.SuggestedDriverJoint);
            Assert.Null(armA.RotationLimit);
            Assert.Empty(result.DerivedLimitJoints);
            Assert.Contains(result.Notes, n => n.Contains("j001") && n.Contains("j006"));
        }

        /// <summary>An under-mated pair between the frame and the ram's
        /// barrel, exported as a free joint.</summary>
        private static RigJoint FreeToBarrel(string barrel)
        {
            return new RigJoint
            {
                Id = "j000",
                Type = JointType.Free,
                ParentGroup = "g000",
                ChildGroup = barrel,
            };
        }

        /// <summary>
        /// A body's mount is its joint in the tree. A free joint is in no
        /// tree, but it has a child too, and listed first it was taken as
        /// the barrel's mount. It has no origin, so the stroke was never
        /// carried onto the driver, and the clamp swung past the ram's end
        /// with no note to say why.
        /// </summary>
        [Fact]
        public void AFreeJointIsNoMount()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true), Group("g001"), Group("g002"), Group("g003"),
            };
            var pin = new double[] { 0, 1, 0 };
            var clampPin = Joint("j001", JointType.Revolute, "g000", "g001", pin, new double[] { 0, 0, 0 });
            var barrelPin = Joint("j002", JointType.Revolute, "g000", "g002", pin, new double[] { 1, 0, 0 });
            var stroke = Joint("j003", JointType.Prismatic, "g002", "g003", new double[] { -1, 0, 1 },
                               new double[] { 0.5, 0, 0.5 });
            stroke.TranslationLimit = new JointLimit { Min = 0.3, Max = 0.8, ValueAtRest = 0.5 };
            var rodPin = Joint("j004", JointType.Cylindrical, "g001", "g003", pin, new double[] { 0, 0, 1 });

            var result = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { FreeToBarrel("g002"), clampPin, barrelPin, stroke, rodPin });

            var loop = Assert.Single(result.Loops);
            Assert.Equal("j003", loop.ClosureJoint);
            Assert.NotNull(clampPin.RotationLimit);
            Assert.Contains("j001", result.DerivedLimitJoints);
        }

        /// <summary>The same on live TongRig's ram: with a free joint to the
        /// ram's body listed first, the body's pin was not seated on the
        /// ram's axis, and the two halves aimed across the ram.</summary>
        [Fact]
        public void AFreeJointDoesNotKeepAPinOffTheRam()
        {
            var seated = new Dictionary<bool, double[]>();
            foreach (bool withFree in new[] { false, true })
            {
                var groups = new List<RigidGroup>
                {
                    Group("g000", grounded: true), Group("g001"), Group("g002"), Group("g003"), Group("g004"),
                };
                var pin = new double[] { 1, 0, 0 };
                var bodyPin = Joint("j006", JointType.Revolute, "g002", "g003", pin,
                                    new double[] { -0.15, -0.2353, -0.2925 });
                var joints = new List<RigJoint>
                {
                    Joint("j001", JointType.Revolute, "g000", "g001", pin, new double[] { -0.2645, -0.3057, 0.37 }),
                    Joint("j002", JointType.Revolute, "g000", "g002", pin, new double[] { -0.2645, -0.3057, -0.37 }),
                    Joint("j003", JointType.Revolute, "g001", "g004", pin, new double[] { -0.2375, -0.2327, 0.2948 }),
                    bodyPin,
                    Joint("j009", JointType.Cylindrical, "g004", "g003",
                          new double[] { 0, 0.0042873, 0.99999 }, new double[] { 0, -0.233, 0.2249 }),
                };
                if (withFree) joints.Insert(0, FreeToBarrel("g003"));

                LoopAnalyzer.Analyze(groups, joints);
                seated[withFree] = bodyPin.Origin;
            }
            Assert.NotEqual(-0.15, seated[false][0], 6);
            for (int k = 0; k < 3; k++) Assert.Equal(seated[false][k], seated[true][k], 9);
        }
    }
}
