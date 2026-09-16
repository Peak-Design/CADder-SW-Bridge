using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Peak.SwToBlender.Core;
using Peak.SwToBlender.Core.Model;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.SwToBlender.Sw
{
    /// <summary>
    /// The appearances of a direct send: which SolidWorks appearance each
    /// face shows, read in full (colours, finish, texture, mapping, decals)
    /// and de-duplicated into the scene's material list.
    ///
    /// The ladder, highest first, as SolidWorks draws it and as the KeyShot
    /// exporter reads it:
    ///   an appearance on the occurrence, or on an assembly above it;
    ///   the face's own; its feature's; its body's; the part document's;
    ///   then the colour values (MaterialPropertyValues) of the face, body,
    ///   occurrence and part, for models coloured before appearances
    ///   existed; then a plain grey.
    /// Live usb_flash_drive2, 2026-09-15: a document appearance (green), a
    /// feature appearance (cream) and a face appearance (brushed steel) on
    /// one part, and a decal on that face.
    /// </summary>
    internal sealed class AppearanceTable
    {
        private readonly MeshScene _scene;
        private readonly Action<string> _log;
        private readonly AppearanceOptions _options;
        private readonly Dictionary<string, int> _byKey = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<IComponent2, Context> _contexts = new Dictionary<IComponent2, Context>();
        private readonly Dictionary<string, Dictionary<string, string>> _libraries =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        // One reading per appearance and scope: a render material is some
        // forty COM properties, and a part has thousands of faces.
        private readonly Dictionary<IRenderMaterial, Dictionary<string, AppearanceSpec>> _readings =
            new Dictionary<IRenderMaterial, Dictionary<string, AppearanceSpec>>();
        private readonly Dictionary<AppearanceSpec, int> _indexOf = new Dictionary<AppearanceSpec, int>();

        /// <summary>What one occurrence (or a part opened on its own) says
        /// about appearances, gathered once.</summary>
        internal sealed class Context
        {
            public IComponent2 Comp;
            public IModelDoc2 Doc;
            public string DocDir;
            public IRenderMaterial Assembly;
            public string AssemblyWhere;
            public readonly List<KeyValuePair<IFace2, IRenderMaterial>> Faces = new List<KeyValuePair<IFace2, IRenderMaterial>>();
            public readonly Dictionary<int, IRenderMaterial> Features = new Dictionary<int, IRenderMaterial>();
            public readonly Dictionary<string, IRenderMaterial> Bodies = new Dictionary<string, IRenderMaterial>(StringComparer.Ordinal);
            public IRenderMaterial Document;
            public object[] Decals;
            public double[,] AssemblyToPart;
            public int LegacyMaterial = -1;
        }

        public AppearanceTable(MeshScene scene, Action<string> log,
                              AppearanceOptions options = null)
        {
            _scene = scene;
            _log = log;
            _options = options ?? AppearanceOptions.Full;
            // Index 0 is always a plain grey, so a triangle whose appearance
            // could not be read still has somewhere to point.
            Add(new AppearanceSpec());
        }

        /// <summary>What decides the look of an occurrence beyond its
        /// document: an appearance from the assembly. Two occurrences of one
        /// part painted differently at assembly level must not share one
        /// mesh, whose triangles carry their materials.</summary>
        public string OccurrenceKey(IComponent2 comp)
        {
            var ctx = For(comp);
            if (ctx.Assembly == null) return "";
            var spec = Cached(ctx.Assembly, ctx, "component", null);
            return "|asm:" + spec.Key().GetHashCode().ToString("x8", CultureInfo.InvariantCulture);
        }

        public Context For(IComponent2 comp)
        {
            Context ctx;
            if (comp == null) return new Context();
            if (_contexts.TryGetValue(comp, out ctx)) return ctx;
            ctx = new Context { Comp = comp };
            ResolveLightweight(comp);
            try { ctx.Doc = comp.GetModelDoc2() as IModelDoc2; } catch { }
            Gather(ctx, SafeRenderMaterials(() => comp.GetRenderMaterials2((int)swDisplayStateOpts_e.swThisDisplayState, null)), comp);
            // An appearance on an assembly ABOVE the occurrence covers it
            // too, and the highest one wins.
            for (var parent = SafeParent(comp); parent != null; parent = SafeParent(parent))
            {
                foreach (var rm in SafeRenderMaterials(() => parent.GetRenderMaterials2((int)swDisplayStateOpts_e.swThisDisplayState, null)))
                    foreach (var e in SafeEntities(rm))
                        if (e is IComponent2 && SameComponent((IComponent2)e, parent))
                        {
                            ctx.Assembly = rm;
                            ctx.AssemblyWhere = SafeName(parent);
                        }
            }
            try { ctx.AssemblyToPart = MathOps.InvertRigid(SwFrames.ComponentWorld(comp)); } catch { }
            FinishContext(ctx);
            if (comp != null) _contexts[comp] = ctx;
            return ctx;
        }

        /// <summary>A part opened on its own: its document is the whole
        /// story, and its render materials are in part context.</summary>
        public Context ForPart(IModelDoc2 part)
        {
            var ctx = new Context { Doc = part };
            Gather(ctx, SafeRenderMaterials(() => part.Extension.GetRenderMaterials2((int)swDisplayStateOpts_e.swThisDisplayState, null)), null);
            FinishContext(ctx);
            return ctx;
        }

        private void FinishContext(Context ctx)
        {
            try { ctx.DocDir = Path.GetDirectoryName(ctx.Doc.GetPathName()); } catch { }
            try
            {
                var decals = _options.Decals && ctx.Doc != null
                    ? ctx.Doc.Extension.GetDecals() as object[] : null;
                if (decals != null && decals.Length > 0) ctx.Decals = decals;
            }
            catch { }
            if (_log != null)
                _log(string.Format(CultureInfo.InvariantCulture,
                    "appearance {0}: assembly {1}, {2} face, {3} feature, {4} body, document {5}, {6} decal(s)",
                    ctx.Comp != null ? SafeName(ctx.Comp) : SafeTitle(ctx.Doc),
                    ctx.Assembly == null ? "none" : Stem(ctx.Assembly) + " from " + ctx.AssemblyWhere,
                    ctx.Faces.Count, ctx.Features.Count, ctx.Bodies.Count,
                    ctx.Document == null ? "none" : Stem(ctx.Document),
                    ctx.Decals == null ? 0 : ctx.Decals.Length));
        }

        private static void Gather(Context ctx, IEnumerable<IRenderMaterial> materials, IComponent2 comp)
        {
            foreach (var rm in materials)
            {
                foreach (var e in SafeEntities(rm))
                {
                    if (e is IFace2)
                        ctx.Faces.Add(new KeyValuePair<IFace2, IRenderMaterial>((IFace2)e, rm));
                    else if (e is IFeature)
                    {
                        int id = -1;
                        try { id = ((IFeature)e).GetID(); } catch { }
                        if (id >= 0) ctx.Features[id] = rm;
                    }
                    else if (e is IBody2)
                    {
                        string name = null;
                        try { name = ((IBody2)e).Name; } catch { }
                        if (name != null) ctx.Bodies[name] = rm;
                    }
                    else if (e is IComponent2)
                    {
                        if (comp != null && SameComponent((IComponent2)e, comp) && ctx.Assembly == null)
                        {
                            ctx.Assembly = rm;
                            ctx.AssemblyWhere = SafeName(comp);
                        }
                    }
                    else if (e is IModelDoc2)
                        ctx.Document = rm;
                }
            }
        }

        /// <summary>The material index for one face. relative takes the
        /// face's part frame to the definition's frame (a rigid
        /// subassembly's child), or is null.</summary>
        public int Resolve(IFace2 face, IBody2 body, Context ctx, double[,] relative)
        {
            // "Do not send the appearances": every face keeps its plain
            // colour, read from the same values the older exports used,
            // and nothing else travels.
            if (!_options.Appearances)
            {
                var plain = FromValues(SafeValues(face), "face")
                    ?? FromValues(SafeValues(body), "body")
                    ?? (ctx.Comp != null ? FromValues(SafeValues(ctx.Comp), "component") : null)
                    ?? FromValues(PartValues(ctx.Doc), "part")
                    ?? new AppearanceSpec();
                plain.DropMapping();
                return Add(plain);
            }
            string source;
            var rm = Winner(face, body, ctx, out source);
            AppearanceSpec spec;
            if (rm != null)
            {
                spec = Cached(rm, ctx, source, relative);
                int known;
                if (ctx.Decals == null && _indexOf.TryGetValue(spec, out known)) return known;
                if (ctx.Decals != null) spec = Copy(spec);
            }
            else
            {
                spec = FromValues(SafeValues(face), "face")
                    ?? FromValues(SafeValues(body), "body")
                    ?? (ctx.Comp != null ? FromValues(SafeValues(ctx.Comp), "component") : null)
                    ?? FromValues(PartValues(ctx.Doc), "part")
                    ?? new AppearanceSpec();
            }
            if (ctx.Decals != null) AddDecals(face, ctx, spec, relative);
            if (!_options.TextureMapping) spec.DropMapping();
            int id = Add(spec);
            if (rm != null && ctx.Decals == null) _indexOf[spec] = id;
            return id;
        }

        private AppearanceSpec Cached(IRenderMaterial rm, Context ctx, string source, double[,] relative)
        {
            string scope = source + "|" + (ctx.DocDir ?? "") + "|" + MatrixKey(source == "component" ? ctx.AssemblyToPart : null)
                + "|" + MatrixKey(relative);
            Dictionary<string, AppearanceSpec> byScope;
            if (!_readings.TryGetValue(rm, out byScope))
            {
                byScope = new Dictionary<string, AppearanceSpec>(StringComparer.Ordinal);
                _readings[rm] = byScope;
            }
            AppearanceSpec spec;
            if (!byScope.TryGetValue(scope, out spec))
            {
                spec = Read(rm, ctx, source, relative);
                byScope[scope] = spec;
            }
            return spec;
        }

        private static string MatrixKey(double[,] m)
        {
            if (m == null) return "";
            var sb = new System.Text.StringBuilder();
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 4; c++)
                    sb.Append(m[r, c].ToString("G6", CultureInfo.InvariantCulture)).Append(',');
            return sb.ToString();
        }

        /// <summary>A reading to add a face's decals to, leaving the cached
        /// reading as it was.</summary>
        private static AppearanceSpec Copy(AppearanceSpec s)
        {
            return new AppearanceSpec
            {
                Source = s.Source, File = s.File, Category = s.Category, Colour = s.Colour,
                Primary = s.Primary, Secondary = s.Secondary, Tertiary = s.Tertiary, ColorForm = s.ColorForm,
                Diffuse = s.Diffuse, Specular = s.Specular, Reflectivity = s.Reflectivity, Roughness = s.Roughness,
                Glossy = s.Glossy, SpecularColour = s.SpecularColour, Transparency = s.Transparency,
                Translucency = s.Translucency, Emission = s.Emission, IndexOfRefraction = s.IndexOfRefraction,
                Shader = s.Shader, DoubleSided = s.DoubleSided, Texture = s.Texture, BumpTexture = s.BumpTexture,
                BumpMap = s.BumpMap, BumpAmplitude = s.BumpAmplitude, Mapping = s.Mapping, Library = s.Library,
            };
        }

        private static IRenderMaterial Winner(IFace2 face, IBody2 body, Context ctx, out string source)
        {
            source = "component";
            if (ctx.Assembly != null) return ctx.Assembly;
            source = "face";
            foreach (var kv in ctx.Faces)
            {
                try { if (ReferenceEquals(kv.Key, face) || kv.Key.IsSame(face)) return kv.Value; }
                catch { }
            }
            source = "feature";
            try
            {
                var feature = face.GetFeature() as IFeature;
                IRenderMaterial rm;
                if (feature != null && ctx.Features.TryGetValue(feature.GetID(), out rm)) return rm;
            }
            catch { }
            source = "body";
            try
            {
                IRenderMaterial rm;
                if (body != null && ctx.Bodies.TryGetValue(body.Name, out rm)) return rm;
            }
            catch { }
            source = "part";
            return ctx.Document;
        }

        private AppearanceSpec Read(IRenderMaterial rm, Context ctx, string source, double[,] relative)
        {
            var s = new AppearanceSpec { Source = source };
            s.File = SafeStr(() => rm.FileName);
            s.Category = P2mFile.Category(s.File);
            s.Library = Library(s.File);
            string dataDir = P2mFile.DataFolder(s.File);

            int form = SafeInt(() => rm.ColorForm, -1);
            int p = SafeInt(() => rm.PrimaryColor, 0), q = SafeInt(() => rm.SecondaryColor, 0), t = SafeInt(() => rm.TertiaryColor, 0);
            s.ColorForm = form;
            s.Primary = AppearanceSpec.FromColorRef(p);
            s.Secondary = AppearanceSpec.FromColorRef(q);
            s.Tertiary = AppearanceSpec.FromColorRef(t);
            s.Colour = AppearanceSpec.FromColorRef(AppearanceSpec.DisplayColourRef(form, p, q, t));

            s.Diffuse = SafeDouble(() => rm.Diffuse, 1.0);
            s.Specular = SafeDouble(() => rm.Specular, 0.0);
            s.SpecularColour = AppearanceSpec.FromColorRef(SafeInt(() => rm.SpecularColor, 0xFFFFFF));
            s.Reflectivity = SafeDouble(() => rm.Reflectivity, 0.0);
            s.Roughness = SafeDouble(() => rm.Roughness, 0.0);
            s.Glossy = SafeDouble(() => rm.Glossy, 0.0);
            s.Transparency = SafeDouble(() => rm.Transparency, 0.0);
            s.Translucency = SafeDouble(() => rm.Translucency, 0.0);
            s.Emission = SafeDouble(() => rm.Emission, 0.0);
            s.IndexOfRefraction = SafeDouble(() => rm.IndexOfRefraction, 1.0);
            s.Shader = SafeInt(() => rm.IlluminationShaderType, -1);
            s.DoubleSided = SafeBool(() => rm.DoubleSided);

            s.Texture = ResolveFile(SafeStr(() => rm.TextureFilename), ctx.DocDir, dataDir)
                ?? ResolveFile(LibraryValue(s.Library, "texture:color_texname"), ctx.DocDir, dataDir);
            s.BumpMap = SafeInt(() => rm.BumpMap, 0);
            s.BumpAmplitude = SafeDouble(() => rm.BumpAmplitude, 0.0);
            s.BumpTexture = ResolveFile(SafeStr(() => rm.BumpTextureFilename), ctx.DocDir, dataDir)
                ?? ResolveFile(LibraryValue(s.Library, "bumpTexture"), ctx.DocDir, dataDir)
                ?? ResolveFile(LibraryValue(s.Library, "texture:bump_file_texture"), ctx.DocDir, dataDir);

            s.Mapping = ReadMapping(rm, s.Library);
            // An appearance on the occurrence is placed in assembly space;
            // the triangles are in the part's.
            if (source == "component" && ctx.AssemblyToPart != null)
                s.Mapping = s.Mapping.Transformed(ctx.AssemblyToPart);
            if (relative != null) s.Mapping = s.Mapping.Transformed(relative);
            return s;
        }

        private static TextureMapping ReadMapping(IRenderMaterial rm, Dictionary<string, string> library)
        {
            var m = new TextureMapping
            {
                Type = SafeInt(() => rm.MappingType, 4),
                Width = SafeDouble(() => rm.Width, 0.0),
                Height = SafeDouble(() => rm.Height, 0.0),
                Rotation = SafeDouble(() => rm.RotationAngle, 0.0),
                XPosition = SafeDouble(() => rm.XPosition, 0.0),
                YPosition = SafeDouble(() => rm.YPosition, 0.0),
                WidthMirror = SafeBool(() => rm.WidthMirror),
                HeightMirror = SafeBool(() => rm.HeightMirror),
                FixedAspectRatio = SafeBool(() => rm.FixedAspectRatio),
                ProjectionReference = SafeInt(() => rm.ProjectionReference, -1),
            };
            if (!(m.Width > 0)) m.Width = P2mFile.Number(library, "initTextureWidth", 1.0);
            if (!(m.Height > 0)) m.Height = P2mFile.Number(library, "initTextureHeight", m.Width);
            try
            {
                double x, y, z;
                rm.GetUDirection2(out x, out y, out z); m.U = new[] { x, y, z };
                rm.GetVDirection2(out x, out y, out z); m.V = new[] { x, y, z };
                rm.GetCenterPoint2(out x, out y, out z); m.Centre = new[] { x, y, z };
            }
            catch { }
            return m;
        }

        /// <summary>The decals SolidWorks lays on this face. The per-face
        /// placement answers only for a face in its part's own context (live
        /// usb_case2, 2026-09-15: null in assembly context, complete in the
        /// part), so an assembly face is first mapped into its part.</summary>
        private void AddDecals(IFace2 face, Context ctx, AppearanceSpec spec, double[,] relative)
        {
            IFace2 partFace = face;
            if (ctx.Comp != null && ctx.Doc != null)
            {
                try { partFace = ctx.Doc.Extension.GetCorrespondingEntity(face) as IFace2 ?? face; }
                catch { partFace = face; }
            }
            foreach (var o in ctx.Decals)
            {
                var decal = o as Decal;
                if (decal == null) continue;
                FaceDecalProperties props = null;
                try { partFace.IGetDecalProperties(decal, ref props); } catch { }
                if (props == null && !ReferenceEquals(partFace, face))
                {
                    try { face.IGetDecalProperties(decal, ref props); } catch { }
                }
                if (props == null) continue;

                var d = new DecalSpec();
                var rm = o as IRenderMaterial;
                string dataDir = rm == null ? null : P2mFile.DataFolder(SafeStr(() => rm.FileName));
                d.Image = ResolveFile(SafeStr(() => props.TextureFilename), ctx.DocDir, dataDir)
                    ?? (rm == null ? null : ResolveFile(SafeStr(() => rm.TextureFilename), ctx.DocDir, dataDir));
                d.MaskType = SafeInt(() => ((IDecal)decal).MaskType, 0);
                d.MaskImage = ResolveFile(SafeStr(() => ((IDecal)decal).MaskFilename), ctx.DocDir, dataDir);
                d.MaskInvert = SafeBool(() => ((IDecal)decal).MaskInvert);
                if (rm != null)
                {
                    d.Mapping = ReadMapping(rm, null);
                    if (relative != null) d.Mapping = d.Mapping.Transformed(relative);
                }
                d.Face["u_scale"] = SafeDouble(() => props.TextureUScale, 0.0);
                d.Face["v_scale"] = SafeDouble(() => props.TextureVScale, 0.0);
                d.Face["translation_x"] = SafeDouble(() => props.TextureTranslationX, 0.0);
                d.Face["translation_y"] = SafeDouble(() => props.TextureTranslationY, 0.0);
                d.Face["translation_u"] = SafeDouble(() => props.TextureTranslationU, 0.0);
                d.Face["translation_v"] = SafeDouble(() => props.TextureTranslationV, 0.0);
                d.Face["angle"] = SafeDouble(() => props.TextureAngle, 0.0);
                d.Face["angle_uv"] = SafeDouble(() => props.TextureAngleUV, 0.0);
                d.Face["mirrored"] = SafeBool(() => props.TextureMirrored);
                d.Face["render_mode"] = SafeInt(() => props.TextureRenderMode, 0);
                spec.Decals.Add(d);
            }
        }

        private int Add(AppearanceSpec spec)
        {
            string key = spec.Key();
            int id;
            if (_byKey.TryGetValue(key, out id)) return id;
            bool glass = spec.IsGlass();
            var m = new MeshMaterial
            {
                Name = spec.MaterialName(),
                R = spec.Colour[0],
                G = spec.Colour[1],
                B = spec.Colour[2],
                A = glass ? 1.0 : 1.0 - AppearanceSpec.Clamp(spec.Transparency, 0, 1),
                Roughness = spec.PbrRoughness(),
                Metallic = spec.IsMetal() ? 1.0 : 0.0,
                Texture = spec.Texture,
                Appearance = key,
            };
            id = _scene.Materials.Count;
            _scene.Materials.Add(m);
            _byKey[key] = id;
            if (_log != null && id > 0)
                _log("appearance material " + id + ": " + m.Name + " [" + spec.Source + "]"
                    + (spec.Texture != null ? " texture " + spec.Texture : "")
                    + (spec.BumpTexture != null ? " bump " + spec.BumpTexture : "")
                    + (spec.Decals.Count > 0 ? " " + spec.Decals.Count + " decal(s)" : ""));
            return id;
        }

        /// <summary>A colour-values appearance (MaterialPropertyValues: RGB,
        /// ambient, diffuse, specular, shininess, transparency, emission), or
        /// null when unset.</summary>
        private static AppearanceSpec FromValues(double[] v, string source)
        {
            if (v == null || v.Length < 9 || v[0] < 0.0 || v[1] < 0.0 || v[2] < 0.0) return null;
            return new AppearanceSpec
            {
                Source = "colour:" + source,
                Colour = new[] { v[0], v[1], v[2] },
                Diffuse = v[4],
                Specular = v[5],
                Transparency = v[7],
                Emission = v[8],
            };
        }

        private Dictionary<string, string> Library(string file)
        {
            var empty = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(file)) return empty;
            Dictionary<string, string> d;
            if (_libraries.TryGetValue(file, out d)) return d;
            d = empty;
            try
            {
                if (System.IO.File.Exists(file)) d = P2mFile.Parse(System.IO.File.ReadAllText(file));
            }
            catch { }
            _libraries[file] = d;
            return d;
        }

        private static string LibraryValue(Dictionary<string, string> d, string key)
        {
            string v;
            return d != null && d.TryGetValue(key, out v) && !string.IsNullOrEmpty(v) ? v : null;
        }

        /// <summary>A texture file as a path that exists: as given; beside
        /// the document; under the SolidWorks data folder (library paths are
        /// relative to it). A file found nowhere is kept as given, so the
        /// Blender side can say which one is missing.</summary>
        internal static string ResolveFile(string path, string docDir, string dataDir)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string norm = path.Replace('/', '\\');
            try
            {
                if (Path.IsPathRooted(norm) && System.IO.File.Exists(norm)) return norm;
                string name = Path.GetFileName(norm);
                if (!string.IsNullOrEmpty(docDir))
                {
                    string beside = Path.Combine(docDir, name);
                    if (System.IO.File.Exists(beside)) return beside;
                }
                if (!string.IsNullOrEmpty(dataDir) && !Path.IsPathRooted(norm))
                {
                    string lib = Path.Combine(dataDir, norm);
                    if (System.IO.File.Exists(lib)) return lib;
                }
            }
            catch { }
            return norm;
        }

        private static void ResolveLightweight(IComponent2 comp)
        {
            if (comp == null) return;
            int state = -1;
            try { state = comp.GetSuppression(); } catch { }
            if (state == (int)swComponentSuppressionState_e.swComponentLightweight
                || state == (int)swComponentSuppressionState_e.swComponentFullyLightweight)
            {
                // A lightweight part has no document, and its appearances
                // live there (live cam-follower, 2026-09-15). The add-in
                // never saves, so the state change stays in the session.
                try { comp.SetSuppression2((int)swComponentSuppressionState_e.swComponentFullyResolved); } catch { }
            }
        }

        private static bool SameComponent(IComponent2 a, IComponent2 b)
        {
            if (ReferenceEquals(a, b)) return true;
            try { return string.Equals(a.Name2, b.Name2, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static IComponent2 SafeParent(IComponent2 c)
        {
            try { return c == null ? null : c.GetParent() as IComponent2; } catch { return null; }
        }

        private static IEnumerable<IRenderMaterial> SafeRenderMaterials(Func<object> read)
        {
            object[] raw = null;
            try { raw = read() as object[]; } catch { }
            if (raw == null) yield break;
            foreach (var r in raw)
            {
                var rm = r as IRenderMaterial;
                if (rm != null) yield return rm;
            }
        }

        private static object[] SafeEntities(IRenderMaterial rm)
        {
            try { return rm.GetEntities() as object[] ?? new object[0]; } catch { return new object[0]; }
        }

        private static double[] SafeValues(object entity)
        {
            try
            {
                if (entity is IFace2) return ((IFace2)entity).MaterialPropertyValues as double[];
                if (entity is IBody2) return ((IBody2)entity).MaterialPropertyValues as double[];
                if (entity is IComponent2) return ((IComponent2)entity).MaterialPropertyValues as double[];
            }
            catch { }
            return null;
        }

        private static double[] PartValues(IModelDoc2 doc)
        {
            try { return doc is IPartDoc ? ((IPartDoc)doc).MaterialPropertyValues as double[] : null; }
            catch { return null; }
        }

        private static string Stem(IRenderMaterial rm)
        {
            string f = SafeStr(() => rm.FileName);
            return string.IsNullOrEmpty(f) ? "?" : Path.GetFileNameWithoutExtension(f);
        }

        private static string SafeName(IComponent2 c)
        {
            try { return c.Name2; } catch { return "?"; }
        }

        private static string SafeTitle(IModelDoc2 d)
        {
            try { return d.GetTitle(); } catch { return "?"; }
        }

        private static string SafeStr(Func<string> f)
        {
            try { var s = f(); return string.IsNullOrEmpty(s) ? null : s; } catch { return null; }
        }

        private static int SafeInt(Func<int> f, int fallback)
        {
            try { return f(); } catch { return fallback; }
        }

        private static double SafeDouble(Func<double> f, double fallback)
        {
            try { return f(); } catch { return fallback; }
        }

        private static bool SafeBool(Func<bool> f)
        {
            try { return f(); } catch { return false; }
        }
    }
}
