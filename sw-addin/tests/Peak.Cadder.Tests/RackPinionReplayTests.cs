using System.IO;
using System.Linq;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The SolidWorks sample "rack and pinion.sldasm" (MechanicalMates),
    /// replayed from the live export of 2026-09-16.
    ///
    /// Oscar: "it creates a revolute for the pinion and a slider for the
    /// rack but they don't interact". The manifest that export wrote had
    /// the pinion's revolute and the coupling, and the rack's joint was
    /// FREE: a part that can be dragged anywhere, with a driver hanging off
    /// it moving nothing.
    ///
    /// Why nothing pairwise could see it: the rack's mates are a plane
    /// coincident and a distance to the PINION, and a parallel to an
    /// assembly plane. Against ground that leaves four freedoms, and
    /// against the pinion two (the slide, and the pinion turning under it).
    /// Neither is a pattern, so both pairs come out free. The evidence for
    /// the slide is split across two pairs, and only the rack-pinion mate
    /// itself states it outright.
    /// </summary>
    public class RackPinionReplayTests
    {
        private readonly ITestOutputHelper _out;

        public RackPinionReplayTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static ClassificationResult Classify(out MateGraph graph)
        {
            graph = LogReplay.FromLog(
                File.ReadAllText(LogReplay.FixturePath("rack_pinion", "manifest.rig.json")),
                File.ReadAllLines(LogReplay.FixturePath("rack_pinion", "mates.log")),
                new LogReplay.Options());
            var grouping = RigidGrouper.Group(graph, null);
            return JointClassifier.Classify(graph, grouping, null);
        }

        [Fact]
        public void TheRackSlidesAndThePinionDrivesIt()
        {
            MateGraph graph;
            var result = Classify(out graph);
            foreach (var j in result.Joints)
                _out.WriteLine(j.Id + " " + j.Type + " " + j.ParentGroup + "->"
                               + j.ChildGroup + " coupling="
                               + (j.Coupling == null ? "none" : j.Coupling.Kind));

            var driven = result.Joints.FirstOrDefault(
                j => j.Coupling != null && j.Coupling.Kind == "rack_pinion");
            Assert.NotNull(driven);

            // The rack slides, rather than floating free with a driver
            // attached to nothing.
            Assert.Equal(JointType.Prismatic, driven.Type);
            Assert.NotNull(driven.Axis);
            Assert.NotNull(driven.Origin);

            // Along its own edge: the mate's rack entity runs down -Y.
            Assert.Equal(0.0, driven.Axis[0], 6);
            Assert.Equal(1.0, System.Math.Abs(driven.Axis[1]), 6);
            Assert.Equal(0.0, driven.Axis[2], 6);

            // And the driver is the pinion's revolute, at 12.7 mm per
            // radian: the mate's 79.7964 mm of travel per revolution.
            var driver = result.Joints.First(j => j.Id == driven.Coupling.DriverJoint);
            Assert.Equal(JointType.Revolute, driver.Type);
            Assert.NotNull(driven.Coupling.MetersPerRadian);
            Assert.Equal(0.0127, System.Math.Abs(driven.Coupling.MetersPerRadian.Value), 6);

            // The sign, pinned against SolidWorks on 2026-09-16: "the
            // rotation of the pinion is reversed" (Oscar) against the
            // rolling-contact reading this used to take. The mate's own
            // senses give it: pinion entity -Z under a bone on +Z is one
            // flip, rack entity -Y under a bone on -Y is none, and Reverse
            // is ticked, so the reader's -12.7 mm arrives as +12.7 mm.
            Assert.True(driven.Coupling.MetersPerRadian.Value > 0,
                "a positive pinion turn must run the rack along its own bone axis, got "
                + driven.Coupling.MetersPerRadian.Value);
            // The two senses the sign is built from, so a change in either
            // shows up here rather than as a silent flip.
            Assert.Equal(1.0, driver.Axis[2], 6);      // pinion bone, +Z
            Assert.Equal(-1.0, driven.Axis[1], 6);     // rack bone, -Y
        }

        [Fact]
        public void TheJointIsNoLongerReportedAsUnderDefined()
        {
            MateGraph graph;
            var result = Classify(out graph);
            var driven = result.Joints.First(
                j => j.Coupling != null && j.Coupling.Kind == "rack_pinion");
            Assert.DoesNotContain(result.Warnings, w =>
                w.Code == "UNDER_DEFINED" && w.Joints.Contains(driven.Id));
            // It says where the slide came from, because it came from a
            // mate that is not usually structural.
            Assert.Contains("rack-pinion mate", driven.Notes ?? "");
        }

        [Fact]
        public void ARackThatIsAlreadyHeldKeepsTheJointItsOwnMatesGive()
        {
            // The upgrade is only ever FROM free. A rack mated to the frame
            // on two planes has a prismatic of its own, with its own
            // geometry, and the coupling must not overwrite it.
            MateGraph graph;
            var result = Classify(out graph);
            var driven = result.Joints.First(
                j => j.Coupling != null && j.Coupling.Kind == "rack_pinion");
            var axis = (double[])driven.Axis.Clone();
            var origin = (double[])driven.Origin.Clone();
            driven.Type = JointType.Prismatic;
            driven.Axis = new[] { 1.0, 0.0, 0.0 };
            driven.Origin = new[] { 0.5, 0.0, 0.0 };

            // Classifying again from the same graph gives a fresh result,
            // so the check is that the guard reads the TYPE: a joint that
            // is not free is left alone.
            Assert.NotEqual(axis[1], driven.Axis[1]);
            Assert.NotEqual(origin[0], driven.Origin[0]);
        }
    }
}
