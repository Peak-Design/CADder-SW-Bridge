using Peak.Cadder.Core;
using System.Collections.Generic;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// What a send carries of the appearances, and what it leaves out.
    /// </summary>
    public class AppearanceOptionsTests
    {
        [Fact]
        public void EverythingTravelsByDefault()
        {
            var options = AppearanceOptions.From(new AppSettings());
            Assert.True(options.Appearances);
            Assert.True(options.Decals);
            Assert.True(options.TextureMapping);
        }

        [Fact]
        public void NoAppearancesMeansNoDecalsAndNoMappingEither()
        {
            // A decal and a mapping are parts of an appearance, so the one
            // switch turning off takes the other two with it, whatever the
            // settings file says.
            var options = AppearanceOptions.From(new AppSettings
            {
                ExportAppearances = false,
                ExportDecals = true,
                ExportTextureMapping = true,
            });
            Assert.False(options.Appearances);
            Assert.False(options.Decals);
            Assert.False(options.TextureMapping);
        }

        [Fact]
        public void DecalsCanBeLeftOutOnTheirOwn()
        {
            var options = AppearanceOptions.From(new AppSettings
            {
                ExportDecals = false,
            });
            Assert.True(options.Appearances);
            Assert.False(options.Decals);
            Assert.True(options.TextureMapping);
        }

        [Fact]
        public void ASpecWithoutItsMappingKeepsTheTileSize()
        {
            var spec = new AppearanceSpec { Texture = "checker.jpg" };
            spec.Mapping.Type = 2;                 // spherical
            spec.Mapping.Width = 0.004;
            spec.Mapping.Height = 0.004;
            spec.Mapping.Rotation = 30.0;
            spec.Mapping.Centre = new double[] { 1, 2, 3 };
            spec.Decals.Add(new DecalSpec { Image = "logo.png" });
            spec.DropMapping();

            var json = spec.ToJson();
            var mapping = (Dictionary<string, object>)json["mapping"];
            Assert.Equal(4, mapping["type"]);      // a box in the part's axes
            Assert.Equal(0.004, mapping["width"]);
            Assert.Equal(0.0, mapping["rotation"]);
            Assert.Equal(new double[] { 0, 0, 0 }, (double[])mapping["centre"]);
            // The image itself still travels, at the size it was given.
            Assert.Equal("checker.jpg", json["texture"]);

            // A decal keeps its own frame: that frame is where the decal
            // goes, and decals have their own switch.
            var decals = (List<object>)json["decals"];
            var first = (Dictionary<string, object>)decals[0];
            Assert.NotNull(first["mapping"]);
        }

        [Fact]
        public void TheKeyStillTellsTwoAppearancesApartWithoutMappings()
        {
            var one = new AppearanceSpec { Texture = "a.jpg" };
            var two = new AppearanceSpec { Texture = "b.jpg" };
            one.DropMapping();
            two.DropMapping();
            Assert.NotEqual(one.Key(), two.Key());
        }
    }
}
