using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Peak.SwToBlender.Core
{
    /// <summary>
    /// Everything SolidWorks says about one appearance on one set of faces,
    /// in plain values: the render material's own properties, the library
    /// file (.p2m) it came from, how its texture is mapped, and the decals
    /// laid over it. Pure data, no COM, so the rules below are testable.
    ///
    /// SolidWorks has no PBR material. What travels is the appearance as
    /// SolidWorks holds it, plus a Blender reading of it. The raw values stay
    /// in the manifest of the material (as JSON on the Blender material), so
    /// a material database can replace the reading with an authored material
    /// by name later.
    /// </summary>
    public sealed class AppearanceSpec
    {
        /// <summary>face | feature | body | part | component | colour | default:
        /// the scope the winning appearance was attached at.</summary>
        public string Source = "default";

        /// <summary>The .p2m the appearance was made from, as SolidWorks
        /// reports it, and the library category ("metal/steel") when the file
        /// sits in the SolidWorks appearance library.</summary>
        public string File;
        public string Category;

        public double[] Colour = { 0.8, 0.8, 0.8 };
        public double[] Primary, Secondary, Tertiary;
        public int ColorForm = -1;

        public double Diffuse = 1.0, Specular, Reflectivity, Roughness, Glossy;
        public double[] SpecularColour;
        public double Transparency, Translucency, Emission, IndexOfRefraction = 1.0;
        public int Shader = -1;
        public bool DoubleSided;

        /// <summary>Absolute paths, or null. Texture is the colour image;
        /// BumpTexture the bump or normal map.</summary>
        public string Texture, BumpTexture;
        public int BumpMap;
        public double BumpAmplitude;

        public TextureMapping Mapping = new TextureMapping();

        /// <summary>The library file's own lines, key to value as written
        /// ("sw_shader" -> "polishedgold"). The values SolidWorks' renderer
        /// used, which the API does not expose (blurry reflections, the
        /// library colours, the texture tile size).</summary>
        public Dictionary<string, string> Library = new Dictionary<string, string>(StringComparer.Ordinal);

        public List<DecalSpec> Decals = new List<DecalSpec>();

        /// <summary>The name the Blender material takes: the appearance
        /// file's stem when its colour is the library colour, the stem with
        /// the colour when the user changed it, the colour alone for a plain
        /// colour. Stable across exports, which is what a material database
        /// keyed on names needs.</summary>
        public string MaterialName()
        {
            string stem = string.IsNullOrEmpty(File) ? null : Path.GetFileNameWithoutExtension(File);
            if (string.Equals(stem, "color", StringComparison.OrdinalIgnoreCase)) stem = null;
            string hex = Hex(Colour);
            string name;
            if (stem == null) name = hex + Finish();
            else if (LibraryColourMatches()) name = stem;
            else name = stem + " " + hex;
            foreach (var d in Decals)
                name += " + " + (string.IsNullOrEmpty(d.Image) ? "decal" : Path.GetFileNameWithoutExtension(d.Image));
            return name;
        }

        /// <summary>How shiny a plain colour is, as a word, so two colours
        /// that differ only in finish do not share one name. SolidWorks'
        /// "color" appearance carries the highlight strength and nothing
        /// else to tell them apart (live cam-follower, 2026-09-16: two greys
        /// at specular 1.0 and 0.3).</summary>
        private string Finish()
        {
            if (Specular >= 0.7) return " gloss";
            if (Specular >= 0.35) return " satin";
            return " matte";
        }

        /// <summary>True when the colour is one of the library file's
        /// colours (col1 or col2), or when there is no library file to
        /// compare with.</summary>
        public bool LibraryColourMatches()
        {
            bool any = false;
            foreach (var key in new[] { "col1", "col2", "col3" })
            {
                var c = P2mFile.Colour(Library, key);
                if (c == null) continue;
                any = true;
                if (Near(c, Colour, 1.5 / 255.0)) return true;
            }
            return !any;
        }

        /// <summary>A Blender roughness, 0..1. SolidWorks' "roughness" is not
        /// one (polished gold 0.7, brushed steel 0.25 in the library), so the
        /// reading comes from whether the reflections are blurred, how strong
        /// the highlight is, and the finish words in the name, in that order
        /// of trust.</summary>
        public double PbrRoughness()
        {
            string blurry;
            if (Library.TryGetValue("blurryReflections", out blurry))
            {
                double spec = P2mFile.Number(Library, "specular_factor", Specular);
                if (string.Equals(blurry, "off", StringComparison.OrdinalIgnoreCase))
                    return Clamp(0.05 + 0.25 * (1.0 - Clamp(spec, 0, 1)), 0.03, 0.35);
                double r = P2mFile.Number(Library, "roughness", 0.5);
                return Clamp(0.25 + 0.6 * r, 0.25, 0.9);
            }
            string n = (File ?? "").ToLowerInvariant();
            if (n.Contains("mirror") || n.Contains("chrome") || n.Contains("polished") || n.Contains("high gloss")) return 0.12;
            if (n.Contains("satin") || n.Contains("medium gloss")) return 0.35;
            if (n.Contains("low gloss") || n.Contains("brushed") || n.Contains("matte")) return 0.55;
            if (n.Contains("sandblast") || n.Contains("cast") || n.Contains("rough") || n.Contains("rubber")) return 0.75;
            return 0.4;
        }

        /// <summary>Metal: the library category, the shader kind, or the
        /// shader name says so.</summary>
        public bool IsMetal()
        {
            if (Category != null && Category.StartsWith("metal", StringComparison.OrdinalIgnoreCase)) return true;
            if (Shader == 4 || Shader == 6 || Shader == 7) return true;
            string s;
            if (Library.TryGetValue("sw_shader", out s))
            {
                s = s.ToLowerInvariant();
                foreach (var w in new[] { "steel", "gold", "silver", "chrome", "alumin", "brass", "bronze",
                                          "copper", "nickel", "titanium", "zinc", "iron", "galvanized", "platinum", "metal" })
                    if (s.Contains(w)) return true;
            }
            return false;
        }

        /// <summary>Glass or another see-through dielectric: transmission
        /// rather than alpha.</summary>
        public bool IsGlass()
        {
            if (Category != null && Category.StartsWith("glass", StringComparison.OrdinalIgnoreCase)) return true;
            return Shader == 14 || Shader == 15 || Shader == 16;
        }

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>
            {
                { "source", Source },
                { "file", File },
                { "category", Category },
                { "name", MaterialName() },
                { "colour", Colour },
                { "primary", Primary },
                { "secondary", Secondary },
                { "tertiary", Tertiary },
                { "color_form", ColorForm },
                { "diffuse", Diffuse },
                { "specular", Specular },
                { "specular_colour", SpecularColour },
                { "reflectivity", Reflectivity },
                { "roughness_sw", Roughness },
                { "glossy", Glossy },
                { "transparency", Transparency },
                { "translucency", Translucency },
                { "emission", Emission },
                { "ior", IndexOfRefraction },
                { "shader", Shader },
                { "double_sided", DoubleSided },
                { "texture", Texture },
                { "bump_texture", BumpTexture },
                { "bump_map", BumpMap },
                { "bump_amplitude", BumpAmplitude },
                { "mapping", Mapping.ToJson() },
                { "library", LibraryJson() },
                { "blender", new Dictionary<string, object>
                    {
                        { "roughness", PbrRoughness() },
                        { "metallic", IsMetal() ? 1.0 : 0.0 },
                        { "glass", IsGlass() },
                    }
                },
            };
            var decals = new List<object>();
            foreach (var dc in Decals) decals.Add(dc.ToJson());
            d["decals"] = decals;
            return d;
        }

        private Dictionary<string, object> LibraryJson()
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in Library) d[kv.Key] = kv.Value;
            return d;
        }

        /// <summary>Identity for de-duplication: two faces share a material
        /// when they will LOOK the same.
        ///
        /// What is left out matters. The scope the appearance was attached at
        /// says nothing about the result, and the texture mapping says
        /// nothing when there is no image to map: a plain colour carries the
        /// centre point of whatever part it came from, which made one grey
        /// into three materials on the cam sample (2026-09-16).</summary>
        public string Key()
        {
            var json = ToJson();
            json.Remove("source");
            if (Texture == null && BumpTexture == null && Decals.Count == 0)
                json.Remove("mapping");
            return MiniJson.Write(json);
        }

        public static string Hex(double[] rgb)
        {
            if (rgb == null || rgb.Length < 3) return "#cccccc";
            return string.Format(CultureInfo.InvariantCulture, "#{0:x2}{1:x2}{2:x2}",
                Byte(rgb[0]), Byte(rgb[1]), Byte(rgb[2]));
        }

        /// <summary>A COLORREF (0x00BBGGRR) as 0..1 RGB.</summary>
        public static double[] FromColorRef(int c)
        {
            return new[] { (c & 0xFF) / 255.0, ((c >> 8) & 0xFF) / 255.0, ((c >> 16) & 0xFF) / 255.0 };
        }

        /// <summary>The colour SolidWorks draws the appearance with. The
        /// render material holds up to three: a one- or two-colour
        /// appearance shows its second (the first is the highlight tint of
        /// the metals), a three-colour one its third. Live usb_flash_drive2,
        /// 2026-09-15: the second colour equals the part's colour values in
        /// every document, and the KeyShot exporter reads the same slots.</summary>
        public static int DisplayColourRef(int colorForm, int primary, int secondary, int tertiary)
        {
            switch (colorForm)
            {
                case 1:
                case 2:
                    return secondary;
                case 3:
                    return tertiary;
                case 0:
                    return primary;
                default:
                    return tertiary;
            }
        }

        private static int Byte(double v)
        {
            return (int)Math.Round(Clamp(v, 0, 1) * 255.0);
        }

        private static bool Near(double[] a, double[] b, double tol)
        {
            if (a == null || b == null || a.Length < 3 || b.Length < 3) return false;
            for (int i = 0; i < 3; i++)
                if (Math.Abs(a[i] - b[i]) > tol) return false;
            return true;
        }

        internal static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }

    /// <summary>
    /// How an image lies on the faces, in the PART's space (the definition's
    /// space in the mesh file). Type is SolidWorks' mapping: 0 surface (the
    /// face's own parameters), 1 planar projection, 2 spherical, 3
    /// cylindrical, 4 automatic (a box). Pinned live on usb_case2 with a
    /// checker, 2026-09-15; the API documentation names no values.
    /// </summary>
    public sealed class TextureMapping
    {
        public int Type = 4;
        public double Width = 1.0, Height = 1.0, Rotation, XPosition, YPosition;
        public bool WidthMirror, HeightMirror, FixedAspectRatio;
        public double[] U = { 1, 0, 0 }, V = { 0, 1, 0 }, Centre = { 0, 0, 0 };
        public int ProjectionReference = -1;

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                { "type", Type },
                { "width", Width },
                { "height", Height },
                { "rotation", Rotation },
                { "x", XPosition },
                { "y", YPosition },
                { "width_mirror", WidthMirror },
                { "height_mirror", HeightMirror },
                { "u", U },
                { "v", V },
                { "centre", Centre },
                { "projection_reference", ProjectionReference },
            };
        }

        /// <summary>The same mapping seen from another frame: m takes the
        /// frame the vectors are in to the part frame. For a rigid
        /// subassembly's child part, whose triangles are moved into the
        /// subassembly's space.</summary>
        public TextureMapping Transformed(double[,] m)
        {
            var t = (TextureMapping)MemberwiseClone();
            if (m == null) return t;
            t.U = MathOps.RotateVector(m, U);
            t.V = MathOps.RotateVector(m, V);
            t.Centre = MathOps.TransformPoint(m, Centre);
            return t;
        }
    }

    /// <summary>One decal on a face: the image, its mask, and where it lies.
    /// The placement is the decal's own projection (centre, U, V, width,
    /// height, rotation) plus the per-face values SolidWorks reports for it
    /// (scale, translation, angle), kept raw.</summary>
    public sealed class DecalSpec
    {
        public string Image;
        public int MaskType;
        public string MaskImage;
        public bool MaskInvert;
        public TextureMapping Mapping = new TextureMapping { Type = 1 };
        public Dictionary<string, object> Face = new Dictionary<string, object>();

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                { "image", Image },
                { "mask_type", MaskType },
                { "mask_image", MaskImage },
                { "mask_invert", MaskInvert },
                { "mapping", Mapping.ToJson() },
                { "face", Face },
            };
        }
    }

    /// <summary>
    /// Reads a SolidWorks appearance library file (.p2m). The format is one
    /// setting per line, the key in quotes then its value, and textures as
    /// `color texture "slot" "relative\path"`. Paths are relative to the
    /// SolidWorks data folder.
    /// </summary>
    public static class P2mFile
    {
        public static Dictionary<string, string> Parse(string text)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text)) return d;
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("color texture ", StringComparison.Ordinal))
                {
                    var parts = Quoted(line);
                    if (parts.Count >= 2) d["texture:" + parts[0]] = parts[1];
                    continue;
                }
                if (line[0] != '"') continue;
                int close = line.IndexOf('"', 1);
                if (close < 0) continue;
                string key = line.Substring(1, close - 1);
                string value = line.Substring(close + 1).Trim();
                if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                    value = value.Substring(1, value.Length - 2);
                d[key] = value;
            }
            return d;
        }

        public static double Number(Dictionary<string, string> d, string key, double fallback)
        {
            string s;
            double v;
            if (d != null && d.TryGetValue(key, out s)
                && double.TryParse(s.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return v;
            return fallback;
        }

        public static double[] Colour(Dictionary<string, string> d, string key)
        {
            string s;
            if (d == null || !d.TryGetValue(key, out s)) return null;
            var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return null;
            var c = new double[3];
            for (int i = 0; i < 3; i++)
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out c[i])) return null;
            return c;
        }

        /// <summary>The library category of an appearance file: the folders
        /// between "graphics\materials" and the file ("metal/steel"), or null
        /// for a file outside the library.</summary>
        public static string Category(string p2mPath)
        {
            if (string.IsNullOrEmpty(p2mPath)) return null;
            string norm = p2mPath.Replace('/', '\\');
            const string marker = "\\graphics\\materials\\";
            int at = norm.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;
            string rest = norm.Substring(at + marker.Length);
            int last = rest.LastIndexOf('\\');
            if (last <= 0) return "";
            return rest.Substring(0, last).Replace('\\', '/').ToLowerInvariant();
        }

        /// <summary>The SolidWorks data folder a library file sits under
        /// (the folder that holds "graphics" and "Images"), or null.</summary>
        public static string DataFolder(string p2mPath)
        {
            if (string.IsNullOrEmpty(p2mPath)) return null;
            string norm = p2mPath.Replace('/', '\\');
            int at = norm.IndexOf("\\graphics\\materials\\", StringComparison.OrdinalIgnoreCase);
            return at < 0 ? null : norm.Substring(0, at);
        }

        private static List<string> Quoted(string line)
        {
            var list = new List<string>();
            int i = 0;
            while (i < line.Length)
            {
                int open = line.IndexOf('"', i);
                if (open < 0) break;
                int close = line.IndexOf('"', open + 1);
                if (close < 0) break;
                list.Add(line.Substring(open + 1, close - open - 1));
                i = close + 1;
            }
            return list;
        }
    }
}
