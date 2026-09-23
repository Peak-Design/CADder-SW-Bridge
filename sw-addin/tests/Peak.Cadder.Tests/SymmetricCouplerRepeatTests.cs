using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Xunit;
using static Peak.Cadder.Tests.FixtureBuilder;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// One mirror relation can be declared more than once: two symmetric
    /// mates over the same bodies, or a symmetric mate and a mirror feature.
    /// The second declaration must not add a second coupling, and above all
    /// not the reverse of the first: two joints that drive each other are a
    /// dependency cycle, and the consumer refuses the whole rig.
    /// </summary>
    public class SymmetricCouplerRepeatTests
    {
        private static RigidGroupingResult Grouping()
        {
            var g = new RigidGroupingResult();
            g.Groups.Add(new RigidGroup { Id = "g000", Name = "plate", Grounded = true, Components = { "c001" } });
            g.Groups.Add(new RigidGroup { Id = "g001", Name = "arm one", Components = { "c002" } });
            g.Groups.Add(new RigidGroup { Id = "g002", Name = "arm two", Components = { "c003" } });
            g.ComponentGroup["c001"] = "g000";
            g.ComponentGroup["c002"] = "g001";
            g.ComponentGroup["c003"] = "g002";
            return g;
        }

        private static RigJoint Hinge(string id, string child)
        {
            return new RigJoint
            {
                Id = id,
                Type = JointType.Revolute,
                ParentGroup = "g000",
                ChildGroup = child,
                Axis = (double[])Z.Clone(),
                SecondaryAxis = (double[])X.Clone(),
                Origin = new double[3],
            };
        }

        /// <summary>Symmetric mates between the two arms' side faces about
        /// the plate's mid plane. With `reversed`, the second mate lists
        /// arm two first.</summary>
        private static MateGraph TwoMates(bool reversed)
        {
            var one = PlaneEnt("c002", Y, P(0, 0.03, 0));
            var two = PlaneEnt("c003", Y, P(0, -0.03, 0));
            return Graph(
                new[]
                {
                    Comp("c001", "plate", isFixed: true),
                    Comp("c002", "arm one"),
                    Comp("c003", "arm two"),
                },
                Mate("Symmetric1", "swMateSYMMETRIC", one, two, PlaneEnt("c001", Y, P(0, 0, 0))),
                Mate("Symmetric2", "swMateSYMMETRIC",
                    reversed ? PlaneEnt("c003", Y, P(0, -0.03, 0)) : PlaneEnt("c002", Y, P(0, 0.03, 0)),
                    reversed ? PlaneEnt("c002", Y, P(0, 0.03, 0)) : PlaneEnt("c003", Y, P(0, -0.03, 0)),
                    PlaneEnt("c001", Y, P(0, 0, 0))));
        }

        private static void AssertNoCouplingCycle(List<RigJoint> joints)
        {
            var byId = new Dictionary<string, RigJoint>();
            foreach (var j in joints) byId[j.Id] = j;
            foreach (var j in joints)
            {
                var seen = new HashSet<string> { j.Id };
                for (var d = j; d.Coupling != null && d.Coupling.DriverJoint != null; )
                {
                    Assert.True(byId.TryGetValue(d.Coupling.DriverJoint, out var next));
                    Assert.True(seen.Add(next.Id), "a coupling cycle through " + next.Id);
                    d = next;
                }
            }
        }

        /// <summary>The second mate over the same pair finds the relation
        /// there already. It used to swap the two mounts, because the
        /// driven one had a coupling, and couple the old driver back to it.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ASecondMateOverThePairAddsNoCycle(bool reversed)
        {
            var joints = new List<RigJoint> { Hinge("j001", "g001"), Hinge("j002", "g002") };

            var warnings = SymmetricCoupler.Resolve(TwoMates(reversed), Grouping(), joints);

            Assert.Empty(warnings);
            AssertNoCouplingCycle(joints);
            Assert.Null(joints[0].Coupling);
            Assert.Equal("j001", joints[1].Coupling.DriverJoint);
            Assert.Equal(-1.0, joints[1].Coupling.Ratio ?? 0.0, 9);
        }

        /// <summary>A mirror feature over a pair a symmetric mate already
        /// couples: ExportCommand resolves the mates first, then the
        /// features, over the same joints.</summary>
        [Fact]
        public void AMirrorFeatureOverACoupledPairAddsNoCycle()
        {
            var graph = TwoMates(false);
            graph.Mates.RemoveAt(1);
            graph.MirrorPairs.Add(new GraphMirrorPair
            {
                FeatureName = "MirrorComponent1",
                SourceComponentId = "c003",
                MirroredComponentId = "c002",
                PlanePoint = new double[3],
                PlaneNormal = (double[])Y.Clone(),
            });
            var joints = new List<RigJoint> { Hinge("j001", "g001"), Hinge("j002", "g002") };

            var warnings = SymmetricCoupler.Resolve(graph, Grouping(), joints);
            warnings.AddRange(MirrorFeatureCoupler.Resolve(graph, Grouping(), joints));

            Assert.Empty(warnings);
            AssertNoCouplingCycle(joints);
            Assert.Null(joints[0].Coupling);
            Assert.Equal("j001", joints[1].Coupling.DriverJoint);
        }

        /// <summary>Two bodies with no mount at all become a mirror pair
        /// once. The second mate found them already joined by the pair's
        /// free joints, and warned that they would pose independently.</summary>
        [Fact]
        public void ASecondMateOverAFreePairIsNotAWarning()
        {
            var joints = new List<RigJoint>();

            var warnings = SymmetricCoupler.Resolve(TwoMates(true), Grouping(), joints);

            Assert.Empty(warnings);
            Assert.Equal(2, joints.Count);
            Assert.Equal("mirror", joints[1].Coupling.Kind);
            Assert.Equal(joints[0].Id, joints[1].Coupling.DriverJoint);
        }

        /// <summary>Mirrored bodies that each ride a loop: the first mate
        /// couples the loop drivers. The second found those drivers coupled,
        /// and fell back to coupling the bodies' own mounts, which sit
        /// inside the loops, where the loops set them.</summary>
        [Fact]
        public void ASecondMateOverMirroredLoopsAddsNothing()
        {
            MirroredFourBars.Build(out var groups, out var joints, out var graph,
                                   out var grouping, mates: 2);
            var loops = LoopAnalyzer.Analyze(groups, joints);

            var warnings = SymmetricCoupler.Resolve(graph, grouping, loops.Joints, loops.Loops);

            Assert.Empty(warnings);
            AssertNoCouplingCycle(loops.Joints);
            var coupled = loops.Joints.FindAll(j => j.Coupling != null);
            var only = Assert.Single(coupled);
            Assert.Equal("j005", only.Id);
            Assert.Equal("j001", only.Coupling.DriverJoint);
        }

        /// <summary>Three bodies mirrored in a ring of mates: arm one to
        /// arm two, arm two to arm three, arm three back to arm one. The
        /// last coupling would close a cycle through the other two, so it
        /// is refused with a reason.</summary>
        [Fact]
        public void AMirrorThatClosesACouplingChainIsRefused()
        {
            var joints = new List<RigJoint>
            {
                Hinge("j001", "g001"),
                Hinge("j002", "g002"),
                Hinge("j003", "g003"),
            };
            joints[1].Coupling = new JointCoupling { Kind = "gear", DriverJoint = "j001", Ratio = -1 };
            joints[2].Coupling = new JointCoupling { Kind = "gear", DriverJoint = "j002", Ratio = -1 };

            string reason = SymmetricCoupler.TryCouple(
                joints, "g000", "g003", "g001", new double[3], (double[])Y.Clone(),
                new SourceMate { SwFeature = "Symmetric3", Type = "swMateSYMMETRIC" },
                MirrorScope.Plane, "the symmetric mate's plane", null);

            Assert.NotNull(reason);
            Assert.Null(joints[0].Coupling);
            AssertNoCouplingCycle(joints);
        }
    }
}
