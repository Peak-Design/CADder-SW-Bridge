using System.Collections.Generic;
using Peak.Cadder.Sw;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A part made only of surface bodies sent no geometry while the export
    /// asked for solids only (live CutterRig, 2026-09-21). Surfaces now
    /// travel too, and three things have to hold for that to be safe: the
    /// solids keep the body numbers they had, a hidden surface stays behind,
    /// and a surface-only part still gives something to draw.
    /// </summary>
    public class SurfaceBodyTests
    {
        private sealed class Body
        {
            public string Name;
            public bool Visible;
        }

        private static Body Shown(string name) => new Body { Name = name, Visible = true };

        private static Body Hidden(string name) => new Body { Name = name, Visible = false };

        private static List<string> Names(List<Body> bodies)
        {
            var names = new List<string>();
            foreach (var b in bodies) names.Add(b.Name);
            return names;
        }

        private static List<Body> Send(object[] solids, object[] sheets)
        {
            return NativeExport.BodiesToSend<Body>(solids, sheets, b => b.Visible);
        }

        [Fact]
        public void SolidsComeFirstSoTheirBodyNumbersDoNotMove()
        {
            var sent = Send(
                new object[] { Shown("solid1"), Shown("solid2") },
                new object[] { Shown("sheet1"), Shown("sheet2") });
            Assert.Equal(new[] { "solid1", "solid2", "sheet1", "sheet2" }, Names(sent));
        }

        [Fact]
        public void AHiddenSurfaceStaysBehind()
        {
            var sent = Send(
                new object[] { Shown("solid1") },
                new object[] { Hidden("construction"), Shown("skin") });
            Assert.Equal(new[] { "solid1", "skin" }, Names(sent));
        }

        [Fact]
        public void ASolidTravelsWhateverItsVisibilitySays()
        {
            // The solid routes kept their own rules: the part route asks
            // for hidden solids too, and the filter is for surfaces only.
            var sent = Send(new object[] { Hidden("solid1") }, null);
            Assert.Equal(new[] { "solid1" }, Names(sent));
        }

        [Fact]
        public void APartOfSurfacesOnlyStillSendsThem()
        {
            var sent = Send(null, new object[] { Shown("sheet1"), Shown("sheet2") });
            Assert.Equal(new[] { "sheet1", "sheet2" }, Names(sent));
        }

        [Fact]
        public void NoBodiesOfEitherKindSendsNothing()
        {
            Assert.Empty(Send(null, null));
            Assert.Empty(Send(new object[0], new object[0]));
        }

        [Fact]
        public void AnEntryThatIsNotABodyIsSkipped()
        {
            var sent = Send(
                new object[] { null, "not a body", Shown("solid1") },
                new object[] { 42, Shown("sheet1") });
            Assert.Equal(new[] { "solid1", "sheet1" }, Names(sent));
        }

        [Fact]
        public void ABodyThatGaveNothingIsNamedWithItsPart()
        {
            Assert.Equal("native export: body 3 of bracket gave no triangles",
                NativeExport.NoTriangles("bracket", 3));
            Assert.Equal("native export: body 1 of part gave no triangles",
                NativeExport.NoTriangles(null, 1));
        }
    }
}
