using System.Collections.Generic;
using Peak.SwToBlender.Core;
using Peak.SwToBlender.Core.Model;
using Xunit;
using static Peak.SwToBlender.Tests.FixtureBuilder;

namespace Peak.SwToBlender.Tests
{
    /// <summary>
    /// The geometric cam route: a cam-follower mate the probe could not
    /// table (Oscar's cam-follower2, 2026-09-15: the cam free in its plane)
    /// hands the cam path's faces to the follower's own joint as a "cam"
    /// coupling, and the consumer holds the follower on them.
    /// </summary>
    public class CamContactTests
    {
        private static readonly double[] X = { 1, 0, 0 };
        private static readonly double[] Z = { 0, 0, 1 };

        private static GraphMate CamMate(GraphMateEntity follower, bool withFaces = true)
        {
            var m = Mate("CamMate1", "swMateCAMFOLLOWER",
                Cylinder("c001", Z, new[] { 0.0, 0.0, 0.0 }, 0.03), follower);
            if (withFaces)
            {
                m.CamComponentId = "c001";
                m.CamSurfacePoints = new[]
                {
                    new[] { 0.03, 0.0, -0.01 }, new[] { 0.03, 0.0, 0.01 }, new[] { 0.0, 0.03, 0.0 },
                };
                m.CamSurfaceTriangles = new[] { new[] { 0, 1, 2 } };
            }
            return m;
        }

        private static RigidGroupingResult Grouping()
        {
            var g = new RigidGroupingResult();
            g.ComponentGroup["c000"] = "g000";
            g.ComponentGroup["c001"] = "g001";
            g.ComponentGroup["c002"] = "g002";
            return g;
        }

        private static List<RigJoint> Joints(string followerType = JointType.Prismatic)
        {
            return new List<RigJoint>
            {
                new RigJoint
                {
                    Id = "j001", Type = JointType.Planar, ParentGroup = "g000", ChildGroup = "g001",
                    Origin = new[] { 0.0, 0.0, 0.0 }, Axis = (double[])Z.Clone(),
                },
                new RigJoint
                {
                    Id = "j002", Type = followerType, ParentGroup = "g000", ChildGroup = "g002",
                    Origin = new[] { 0.05, 0.0, 0.0 }, Axis = (double[])X.Clone(),
                    Notes = "A cam-follower mate rides this pair; the cam relation is not rigged.",
                },
                new RigJoint { Id = "j003", Type = JointType.Free, ParentGroup = "g001", ChildGroup = "g002" },
            };
        }

        private static MateGraph GraphWith(GraphMate mate)
        {
            return Graph(new[]
            {
                Comp("c000", "ground", isFixed: true), Comp("c001", "cam"), Comp("c002", "follower"),
            }, mate);
        }

        [Fact]
        public void AVertexFollowerGetsACamCouplingOnItsSlide()
        {
            var joints = Joints();
            var unread = new Dictionary<string, string> { { "CamMate1", "the cam is free in its plane" } };
            var modelled = CamContact.Resolve(
                Grouping(), GraphWith(CamMate(VertexEnt("c002", new[] { 0.03, 0.0, 0.0 }))),
                joints, new List<string>(), unread, null);

            Assert.Equal(new[] { "CamMate1" }, modelled);
            Assert.Empty(unread);
            var c = joints[1].Coupling;
            Assert.NotNull(c);
            Assert.Equal("cam", c.Kind);
            Assert.Equal("j001", c.DriverJoint);
            Assert.Equal("vertex", c.FollowerKind);
            Assert.Equal(Z, c.CamAxis);
            Assert.Single(c.CamSurfaceTriangles);
            Assert.Null(c.FollowerRadius);
            Assert.Equal("high", joints[1].Confidence);
            Assert.Contains("Cam contact", joints[1].Notes);
            Assert.DoesNotContain("not rigged", joints[1].Notes);
            Assert.Contains(joints[1].SourceMates, s => s.SwFeature == "CamMate1");
        }

        [Fact]
        public void ARollerCarriesItsRadiusAndAFlatFaceItsNormal()
        {
            var joints = Joints();
            CamContact.Resolve(
                Grouping(), GraphWith(CamMate(Cylinder("c002", Z, new[] { 0.035, 0.0, 0.0 }, 0.005))),
                joints, new List<string>(), new Dictionary<string, string>(), null);
            Assert.Equal("roller", joints[1].Coupling.FollowerKind);
            Assert.Equal(0.005, joints[1].Coupling.FollowerRadius);
            Assert.Equal(Z, joints[1].Coupling.FollowerAxis);

            joints = Joints();
            CamContact.Resolve(
                Grouping(), GraphWith(CamMate(PlaneEnt("c002", X, new[] { 0.03, 0.0, 0.0 }))),
                joints, new List<string>(), new Dictionary<string, string>(), null);
            Assert.Equal("flat", joints[1].Coupling.FollowerKind);
            Assert.Equal(X, joints[1].Coupling.FollowerNormal);
        }

        [Fact]
        public void ARockingFollowerIsRefusedWithBothReasons()
        {
            var joints = Joints(JointType.Revolute);
            var unread = new Dictionary<string, string> { { "CamMate1", "the cam is free in its plane" } };
            var modelled = CamContact.Resolve(
                Grouping(), GraphWith(CamMate(VertexEnt("c002", new[] { 0.03, 0.0, 0.0 }))),
                joints, new List<string>(), unread, null);

            Assert.Empty(modelled);
            Assert.Null(joints[1].Coupling);
            Assert.Contains("the cam is free in its plane, and ", unread["CamMate1"]);
            Assert.Contains("does not slide", unread["CamMate1"]);
        }

        [Fact]
        public void NoFacesMeansNoContact()
        {
            var joints = Joints();
            var unread = new Dictionary<string, string>();
            var modelled = CamContact.Resolve(
                Grouping(), GraphWith(CamMate(VertexEnt("c002", new[] { 0.03, 0.0, 0.0 }), withFaces: false)),
                joints, new List<string>(), unread, null);
            Assert.Empty(modelled);
            Assert.Contains("could not be read", unread["CamMate1"]);
        }

        [Fact]
        public void AMateTheProbeTabledIsLeftAlone()
        {
            var joints = Joints();
            joints[1].Coupling = new JointCoupling { Kind = "table", DriverJoint = "j001" };
            var modelled = CamContact.Resolve(
                Grouping(), GraphWith(CamMate(VertexEnt("c002", new[] { 0.03, 0.0, 0.0 }))),
                joints, new List<string> { "CamMate1" }, new Dictionary<string, string>(), null);
            Assert.Empty(modelled);
            Assert.Equal("table", joints[1].Coupling.Kind);
        }

        [Fact]
        public void TheWriterEmitsTheCamBlockWithSchemaKeys()
        {
            var m = new RigManifest();
            m.RigidGroups.Add(new RigidGroup { Id = "g000", Name = "ground", Grounded = true });
            m.RigidGroups.Add(new RigidGroup { Id = "g001", Name = "cam" });
            m.RigidGroups.Add(new RigidGroup { Id = "g002", Name = "follower" });
            var joints = Joints();
            CamContact.Resolve(
                Grouping(), GraphWith(CamMate(Cylinder("c002", Z, new[] { 0.035, 0.0, 0.0 }, 0.005))),
                joints, new List<string>(), new Dictionary<string, string>(), null);
            foreach (var j in joints) j.SecondaryAxis = j.Axis == null ? null : X;
            m.Joints.AddRange(joints);

            var parsed = TestJson.Parse(ManifestWriter.Write(m));
            var coupling = parsed["joints"].Items[1]["coupling"];
            Assert.Equal(new[] { "kind", "driver_joint", "ratio", "meters_per_radian", "lead_m_per_rev", "cam" },
                coupling.Keys);
            Assert.Equal(new[] { "axis", "origin", "surface", "follower" }, coupling["cam"].Keys);
            Assert.Equal(new[] { "points", "triangles" }, coupling["cam"]["surface"].Keys);
            Assert.Equal(new[] { "kind", "point", "axis", "radius", "normal" }, coupling["cam"]["follower"].Keys);
            Assert.Equal("roller", coupling["cam"]["follower"]["kind"].Str);
            Assert.Equal(3, coupling["cam"]["surface"]["points"].Items.Count);
        }
    }
}
