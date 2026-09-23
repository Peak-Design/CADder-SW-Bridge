using System;
using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// ExportCommand analyzes the loops again on the joints the first
    /// analysis returned, once a coupling has a driver, and the analyzer's
    /// own gears count. The second analysis must come to the same result:
    /// what the first one derived is no fact the second one may read as a
    /// mate's.
    /// </summary>
    public class AnalyzeAgainTests
    {
        private static RigidGroup Group(string id, bool grounded = false)
        {
            return new RigidGroup { Id = id, Name = id, Grounded = grounded };
        }

        private static RigJoint Joint(string id, string type, string parent, string child,
                                      double[] axis = null, double[] origin = null)
        {
            return new RigJoint
            {
                Id = id,
                Type = type,
                ParentGroup = parent,
                ChildGroup = child,
                Axis = axis,
                Origin = origin,
            };
        }

        private static int Count(string text, string part)
        {
            int n = 0;
            for (int at = (text ?? "").IndexOf(part, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
                n++;
            return n;
        }

        /// <summary>
        /// Live corpus 06 parallelogram3: the parallel mates become gears,
        /// and their free joints are listed as modelled, so they lose their
        /// under-defined warning. The second analysis found the gears there
        /// already, listed nothing, and ExportCommand kept both warnings for
        /// mates the rig models.
        /// </summary>
        [Fact]
        public void ParallelMatesStayModelled()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true), Group("g001"), Group("g002"), Group("g003"),
            };
            var axis = new double[] { 0, 0, -1 };
            RigJoint Free(string id, string parent, string child, double[] normal)
            {
                var j = Joint(id, JointType.Free, parent, child);
                j.ResidualKnown = true;
                j.ResidualRot = RotFreedom.AboutDirection;
                j.ResidualRotDir = normal;
                j.SourceMates.Add(new SourceMate { SwFeature = "Parallel" + id, Type = "swMatePARALLEL" });
                return j;
            }
            var joints = new List<RigJoint>
            {
                Joint("j001", JointType.Revolute, "g000", "g001", axis),
                Free("j002", "g000", "g002", new double[] { 1, 0, 0 }),
                Joint("j003", JointType.Revolute, "g000", "g003", axis),
                Free("j004", "g001", "g003", new double[] { 0, -1, 0 }),
                Joint("j005", JointType.Revolute, "g002", "g003", axis),
            };

            var first = LoopAnalyzer.Analyze(groups, joints);
            var again = LoopAnalyzer.Analyze(groups, first.Joints);

            Assert.Equal(new[] { "j002", "j004" }, first.CoupledFreeJointIds);
            Assert.Equal(new[] { "j002", "j004" }, again.CoupledFreeJointIds);
            Assert.Equal("j001", joints[2].Coupling.DriverJoint);
            Assert.Equal("j003", joints[4].Coupling.DriverJoint);
            Assert.Single(joints[2].SourceMates);
        }

        /// <summary>
        /// A stop carried onto the driver from the ram's stroke (see
        /// ASliderCrankCarriesItsStrokeLimitOntoTheDriver). The second
        /// analysis read it as the driver's own limit: it was not listed as
        /// derived, so an option that makes another joint the driver kept
        /// it, and its note was written again.
        /// </summary>
        [Fact]
        public void ADerivedStopIsDerivedAgain()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true), Group("g001"), Group("g002"), Group("g003"),
            };
            var pin = new double[] { 0, 1, 0 };
            double root2 = Math.Sqrt(2.0), root3 = Math.Sqrt(3.0);
            var clampPin = Joint("j001", JointType.Revolute, "g000", "g001", pin, new double[] { 0, 0, 0 });
            var barrelPin = Joint("j002", JointType.Revolute, "g000", "g002", pin, new double[] { 1, 0, 0 });
            var stroke = Joint("j003", JointType.Prismatic, "g002", "g003", new double[] { -1, 0, 1 });
            stroke.TranslationLimit = new JointLimit
            {
                Min = 0.5 + (1.0 - root2),
                Max = 0.5 + (root3 - root2),
                ValueAtRest = 0.5,
            };
            var rodPin = Joint("j004", JointType.Cylindrical, "g001", "g003", pin, new double[] { 0, 0, 1 });

            var first = LoopAnalyzer.Analyze(
                groups, new List<RigJoint> { clampPin, barrelPin, stroke, rodPin });
            var stop = clampPin.RotationLimit;
            var again = LoopAnalyzer.Analyze(groups, first.Joints);

            Assert.Contains("j001", first.DerivedLimitJoints);
            Assert.Contains("j001", again.DerivedLimitJoints);
            Assert.NotNull(clampPin.RotationLimit);
            Assert.Equal(stop.Min, clampPin.RotationLimit.Min, 12);
            Assert.Equal(stop.Max, clampPin.RotationLimit.Max, 12);
            Assert.Equal(1, Count(clampPin.Notes, "derived from the stroke limit"));
        }

        /// <summary>
        /// Live TongRig: the ram's pins are seated on the ram's axis, with a
        /// note where a pin passes clear of it. The note was written again
        /// on every round of the choice, and again on every analysis.
        /// </summary>
        [Fact]
        public void ASeatingNoteIsWrittenOnce()
        {
            var groups = new List<RigidGroup>
            {
                Group("g000", grounded: true), Group("g001"), Group("g002"), Group("g003"), Group("g004"),
            };
            var pin = new double[] { 1, 0, 0 };
            var joints = new List<RigJoint>
            {
                Joint("j001", JointType.Revolute, "g000", "g001", pin, new double[] { -0.2645, -0.3057, 0.37 }),
                Joint("j002", JointType.Revolute, "g000", "g002", pin, new double[] { -0.2645, -0.3057, -0.37 }),
                Joint("j003", JointType.Revolute, "g001", "g004", pin, new double[] { -0.2375, -0.2327, 0.2948 }),
                Joint("j006", JointType.Revolute, "g002", "g003", pin, new double[] { -0.15, -0.2353, -0.2925 }),
                Joint("j009", JointType.Cylindrical, "g004", "g003",
                      new double[] { 0, 0.0042873, 0.99999 }, new double[] { 0, -0.233, 0.2249 }),
            };

            var first = LoopAnalyzer.Analyze(groups, joints);
            int once = 0;
            foreach (var j in first.Joints) once += Count(j.Notes, "mm clear of the axis");
            Assert.True(once > 0, "a pin passes clear of the ram's axis");
            foreach (var j in first.Joints)
                Assert.True(Count(j.Notes, "mm clear of the axis") <= 1, j.Id + ": " + j.Notes);

            var again = LoopAnalyzer.Analyze(groups, first.Joints);
            foreach (var j in again.Joints)
                Assert.True(Count(j.Notes, "mm clear of the axis") <= 1, j.Id + ": " + j.Notes);
        }
    }
}
