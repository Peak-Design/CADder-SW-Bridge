using System.Collections.Generic;
using Peak.SwToBlender.Core;
using Xunit;

namespace Peak.SwToBlender.Tests
{
    /// <summary>
    /// The appearance model behind a direct send: the library file reader,
    /// the colour slot SolidWorks draws with, the material name a material
    /// database keys on, and the Blender reading of finish words that are
    /// not PBR values. The library lines are copied from SolidWorks 2022's
    /// own files.
    /// </summary>
    public class AppearanceSpecTests
    {
        private const string PolishedGold =
            "\"SurfaceFinishShaderType\" 0\r\n\"blurryReflections\" off\r\n\"bumpIsNormalMap\" on\r\n"
            + "\"bumpStrength\" 0.001\r\n\"bumpTexture\" \"\"\r\n\"col1\" 1 0.807843 0.498039\r\n"
            + "\"col2\" 0.968627 0.878431 0.6\r\n\"initTextureHeight\" 0.0254\r\n\"initTextureWidth\" 0.0254\r\n"
            + "\"reflectivity\" 0.65\r\n\"roughness\" 0.7\r\n\"specular_color\" 1 0.976471 0.839216\r\n"
            + "\"specular_factor\" 0.7\r\n\"sw_shader\" polishedgold\r\n\"transparency\" 0\r\n";

        private const string TireTread =
            "\"blurryReflections\" off\r\n\"bumpIsNormalMap\" on\r\n"
            + "\"bumpTexture\" \"images\\shaders\\surfacefinish/tiretread_n.dds\"\r\n"
            + "color texture \"bump_file_texture\" \"Images\\textures\\rubber\\texture\\tire tread bump.jpg\" \r\n"
            + "\"col1\" 1 1 1\r\n"
            + "color texture \"color_texname\" \"Images\\textures\\rubber\\texture\\tire tread.jpg\" \r\n"
            + "\"roughness\" 0.6\r\n\"specular_factor\" 0.5\r\n\"sw_shader\" tiretreadrubber\r\n";

        private const string Sandblasted =
            "\"blurryReflections\" on\r\n\"roughness\" 0.65\r\n\"specular_factor\" 1\r\n\"sw_shader\" sandblastedsteel\r\n";

        [Fact]
        public void TheLibraryFileReadsKeysColoursAndTextures()
        {
            var d = P2mFile.Parse(TireTread);
            Assert.Equal("off", d["blurryReflections"]);
            Assert.Equal("images\\shaders\\surfacefinish/tiretread_n.dds", d["bumpTexture"]);
            Assert.Equal("Images\\textures\\rubber\\texture\\tire tread.jpg", d["texture:color_texname"]);
            Assert.Equal("Images\\textures\\rubber\\texture\\tire tread bump.jpg", d["texture:bump_file_texture"]);
            Assert.Equal(new[] { 1.0, 1.0, 1.0 }, P2mFile.Colour(d, "col1"));
            Assert.Equal(0.6, P2mFile.Number(d, "roughness", -1), 6);
            Assert.Null(P2mFile.Colour(d, "col2"));
        }

        [Fact]
        public void TheCategoryAndDataFolderComeFromTheLibraryPath()
        {
            const string p = @"C:\Program Files\SOLIDWORKS 2022\SOLIDWORKS\data\graphics\materials\metal\steel\brushed steel.p2m";
            Assert.Equal("metal/steel", P2mFile.Category(p));
            Assert.Equal(@"C:\Program Files\SOLIDWORKS 2022\SOLIDWORKS\data", P2mFile.DataFolder(p));
            Assert.Null(P2mFile.Category(@"C:\Users\me\custom.p2m"));
        }

        /// <summary>Live usb_flash_drive2 (2026-09-15): polished gold holds
        /// primary (255,206,127) and secondary (247,224,153), and the part's
        /// colour values are the secondary.</summary>
        [Fact]
        public void TheDisplayedColourIsTheSecondForOneAndTwoColourAppearances()
        {
            int primary = 127 << 16 | 206 << 8 | 255;
            int secondary = 153 << 16 | 224 << 8 | 247;
            Assert.Equal(secondary, AppearanceSpec.DisplayColourRef(2, primary, secondary, secondary));
            Assert.Equal(primary, AppearanceSpec.DisplayColourRef(0, primary, secondary, secondary));
            var rgb = AppearanceSpec.FromColorRef(secondary);
            Assert.Equal(247 / 255.0, rgb[0], 9);
            Assert.Equal(153 / 255.0, rgb[2], 9);
        }

        private static AppearanceSpec Gold(double[] colour)
        {
            return new AppearanceSpec
            {
                File = @"C:\Program Files\SOLIDWORKS 2022\SOLIDWORKS\data\graphics\materials\metal\gold\polished gold.p2m",
                Category = "metal/gold",
                Colour = colour,
                Specular = 0.7,
                Library = P2mFile.Parse(PolishedGold),
            };
        }

        /// <summary>A file inside the SolidWorks appearance library.</summary>
        private static string LibraryPath(string name)
        {
            return @"C:\SW\data\graphics\materials\" + name;
        }

        [Fact]
        public void ALibraryAppearanceIsNamedAfterItsFileUnlessItsColourChanged()
        {
            Assert.Equal("polished gold", Gold(new[] { 0.968627, 0.878431, 0.6 }).MaterialName());
            Assert.Equal("polished gold #ff0000", Gold(new[] { 1.0, 0.0, 0.0 }).MaterialName());
            // A plain colour is named by colour and finish: the "color"
            // appearance has nothing else to tell two apart, and two greys of
            // different shininess did share a name (cam-follower, 2026-09-16).
            var plain = new AppearanceSpec
            {
                File = LibraryPath("color.p2m"),
                Colour = new[] { 0.0, 0.5, 0.75 },
                Specular = 1.0,
            };
            Assert.Equal("#0080bf gloss", plain.MaterialName());
            plain.Specular = 0.5;
            Assert.Equal("#0080bf satin", plain.MaterialName());
            plain.Specular = 0.2;
            Assert.Equal("#0080bf matte", plain.MaterialName());
        }

        [Fact]
        public void ADecalJoinsTheName()
        {
            var s = Gold(new[] { 0.968627, 0.878431, 0.6 });
            s.Decals.Add(new DecalSpec { Image = @"C:\logos\SolidWorks_Logo.png" });
            Assert.Equal("polished gold + SolidWorks_Logo", s.MaterialName());
        }

        [Fact]
        public void SharpReflectionsReadAsSmoothAndBlurredAsRough()
        {
            double gold = Gold(new[] { 1.0, 0.8, 0.5 }).PbrRoughness();
            var sand = new AppearanceSpec { Library = P2mFile.Parse(Sandblasted) };
            Assert.InRange(gold, 0.03, 0.2);
            Assert.InRange(sand.PbrRoughness(), 0.5, 0.9);
            Assert.True(sand.PbrRoughness() > gold);
            Assert.Equal(0.55, new AppearanceSpec { File = "red low gloss plastic.p2m" }.PbrRoughness(), 9);
        }

        [Fact]
        public void MetalAndGlassComeFromTheCategoryOrShader()
        {
            Assert.True(Gold(new[] { 1.0, 0.8, 0.5 }).IsMetal());
            Assert.True(new AppearanceSpec { Library = P2mFile.Parse(Sandblasted) }.IsMetal());
            Assert.False(new AppearanceSpec { Category = "plastic/high gloss" }.IsMetal());
            Assert.True(new AppearanceSpec { Category = "glass/gloss" }.IsGlass());
            Assert.True(new AppearanceSpec { Shader = 14 }.IsGlass());
        }

        [Fact]
        public void TheJsonCarriesTheRawValuesAndTheBlenderReading()
        {
            var s = Gold(new[] { 0.968627, 0.878431, 0.6 });
            s.Mapping.Width = 0.004;
            var parsed = TestJson.Parse(MiniJson.Write(s.ToJson()));
            Assert.Equal("polished gold", parsed["name"].Str);
            Assert.Equal("polishedgold", parsed["library"]["sw_shader"].Str);
            Assert.Equal(1.0, parsed["blender"]["metallic"].Num, 9);
            Assert.Equal(0.004, parsed["mapping"]["width"].Num, 9);
            Assert.Equal(4, (int)parsed["mapping"]["type"].Num);
            Assert.Equal(s.Key(), Gold(new[] { 0.968627, 0.878431, 0.6 }).Let(g => { g.Mapping.Width = 0.004; return g; }).Key());
        }

        /// <summary>Two faces that will look the same share one material,
        /// whatever scope the appearance came from and wherever its mapping
        /// centre sits. A plain colour carries the centre point of the part
        /// it came from, which made one grey into three materials on the cam
        /// sample (2026-09-16). An image changes that: then the mapping is
        /// exactly what the eye sees.</summary>
        [Fact]
        public void TheIdentityIgnoresWhatCannotBeSeen()
        {
            var a = Gold(new[] { 0.9, 0.8, 0.5 });
            var b = Gold(new[] { 0.9, 0.8, 0.5 });
            a.Source = "face";
            b.Source = "part";
            b.Mapping.Centre = new[] { 0.5, 0.25, 0.0 };
            Assert.Equal(a.Key(), b.Key());

            a.Texture = b.Texture = "checker.png";
            Assert.NotEqual(a.Key(), b.Key());

            var other = Gold(new[] { 0.1, 0.2, 0.3 });
            Assert.NotEqual(Gold(new[] { 0.9, 0.8, 0.5 }).Key(), other.Key());
        }

        [Fact]
        public void AMappingMovesWithTheFrameItIsSeenFrom()
        {
            var m = new TextureMapping { U = new[] { 1.0, 0, 0 }, V = new[] { 0, 1.0, 0 }, Centre = new[] { 0.1, 0, 0 } };
            // A quarter turn about Z and a shift of 1 m along X.
            var t = new double[,] { { 0, -1, 0, 1 }, { 1, 0, 0, 0 }, { 0, 0, 1, 0 }, { 0, 0, 0, 1 } };
            var moved = m.Transformed(t);
            Assert.Equal(new[] { 0.0, 1.0, 0.0 }, moved.U);
            Assert.Equal(new[] { -1.0, 0.0, 0.0 }, moved.V);
            Assert.Equal(1.0, moved.Centre[0], 9);
            Assert.Equal(0.1, moved.Centre[1], 9);
            Assert.Equal(new[] { 1.0, 0, 0 }, m.U);
        }
    }

    internal static class LetExtension
    {
        public static T Let<T>(this T value, System.Func<T, T> f) => f(value);
    }
}
