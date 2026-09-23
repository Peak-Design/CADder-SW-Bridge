using System.Collections.Generic;
using System.Linq;
using Peak.Cadder.Sw;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The rules that choose what a body is sent without, run on a model of
    /// a part instead of a live body. A face here is a name, a loop is a key
    /// and a width, and an edge knows its two faces: that is all the walk
    /// reads.
    /// </summary>
    public class SmallFeaturePlannerTests
    {
        internal sealed class Face
        {
            public string Name;
            public bool Plane;
            public List<Loop> Loops = new List<Loop>();
            public override string ToString() { return Name; }
        }

        internal sealed class Loop
        {
            public string Key;
            public double Extent;
            public bool Outer;
            public double[] Centre = { 0.0, 0.0, 0.0 };
            public List<Edge> Edges = new List<Edge>();
        }

        internal sealed class Edge
        {
            public Face A, B;
        }

        internal sealed class Model : IFeatureTopology<Face, Loop, Edge>
        {
            public bool IsPlane(Face face) { return face.Plane; }
            public IEnumerable<Loop> LoopsOf(Face face) { return face.Loops; }
            public IEnumerable<Edge> EdgesOf(Loop loop) { return loop.Edges; }
            public IList<Face> FacesOf(Edge edge) { return new List<Face> { edge.A, edge.B }; }
            public bool Same(Face a, Face b) { return ReferenceEquals(a, b); }

            public bool TryIsOuter(Loop loop, out bool outer)
            {
                outer = loop.Outer;
                return true;
            }

            public string LoopKey(Loop loop, out double extent)
            {
                extent = loop.Extent;
                return loop.Key;
            }

            public double[] LoopCentre(Loop loop) { return loop.Centre; }
        }

        private static Face F(string name, bool plane)
        {
            return new Face { Name = name, Plane = plane };
        }

        /// <summary>One closed edge between two faces: a loop on each,
        /// with the same key, as the two sides of one rim.</summary>
        private static void Rim(Face a, bool outerOnA, Face b, bool outerOnB,
                                string key, double extent, double depth = 0.0)
        {
            var edge = new Edge { A = a, B = b };
            var centre = new[] { 0.0, 0.0, -depth };
            a.Loops.Add(new Loop { Key = key, Extent = extent, Outer = outerOnA, Centre = centre, Edges = { edge } });
            b.Loops.Add(new Loop { Key = key, Extent = extent, Outer = outerOnB, Centre = centre, Edges = { edge } });
        }

        private static FeaturePlanner<Face, Loop, Edge>.Result Plan(
            IEnumerable<Face> faces, double dial = 0.012)
        {
            return new FeaturePlanner<Face, Loop, Edge>(new Model())
                .Choose(faces, dial, curved: false);
        }

        private static string Names(IEnumerable<Face> faces)
        {
            return string.Join(",", faces.Select(f => f.Name).OrderBy(n => n));
        }

        /// <summary>A plate with a blind counterbored hole: counterbore
        /// 10 mm, hole 5 mm, dial 12 mm.</summary>
        private static Dictionary<string, Face> Counterbore()
        {
            var top = F("top", true);
            var side = F("side", true);
            var under = F("under", true);
            var cbWall = F("cbWall", false);
            var annulus = F("annulus", true);
            var wall = F("wall", false);
            var bottom = F("bottom", true);
            Rim(top, true, side, true, "top-edge", 0.1);
            Rim(under, true, side, false, "under-edge", 0.1, 0.02);
            Rim(top, false, cbWall, true, "K1", 0.010);
            Rim(cbWall, false, annulus, true, "A", 0.010, 0.003);
            Rim(annulus, false, wall, true, "K2", 0.005, 0.003);
            Rim(wall, false, bottom, true, "B", 0.005, 0.008);
            return new[] { top, side, under, cbWall, annulus, wall, bottom }
                .ToDictionary(f => f.Name);
        }

        [Fact]
        public void ACounterboreGoesWholeWhenTheTopFaceComesFirst()
        {
            var p = Counterbore();
            var plan = Plan(new[] { p["top"], p["side"], p["under"], p["cbWall"],
                                    p["annulus"], p["wall"], p["bottom"] });
            // The hole in the counterbore floor goes too. Before, its rim
            // counted as walked because the counterbore's walk met it, so its
            // wall and bottom stayed inside the part as a capped shell.
            Assert.Equal("annulus,bottom,cbWall,wall", Names(plan.Gone));
            Assert.Empty(plan.Cap);
            Assert.Equal("top", Names(plan.Fill));
            Assert.Equal(2, plan.Features.Count(f => f.Declined == null));
        }

        [Fact]
        public void ACounterboreGoesWholeWhenTheAnnulusComesFirst()
        {
            var p = Counterbore();
            var plan = Plan(new[] { p["annulus"], p["side"], p["under"], p["cbWall"],
                                    p["top"], p["wall"], p["bottom"] });
            Assert.Equal("annulus,bottom,cbWall,wall", Names(plan.Gone));
            Assert.Empty(plan.Cap);
            Assert.Equal("top", Names(plan.Fill));
        }

        [Fact]
        public void AThroughHoleIsOneFeatureFromEitherSide()
        {
            var top = F("top", true);
            var side = F("side", true);
            var under = F("under", true);
            var wall = F("wall", false);
            Rim(top, true, side, true, "top-edge", 0.1);
            Rim(under, true, side, false, "under-edge", 0.1, 0.01);
            Rim(top, false, wall, true, "Kt", 0.006);
            Rim(under, false, wall, false, "Kb", 0.006, 0.01);
            var plan = Plan(new[] { top, side, under, wall });
            Assert.Equal("wall", Names(plan.Gone));
            Assert.Equal("top,under", Names(plan.Fill));
            Assert.Single(plan.Features);
        }
    }
}
