using System;
using System.Collections.Generic;
using System.Reflection;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.Cadder.Bridge
{
    /// <summary>
    /// Read-only dump of everything SolidWorks says about the appearances of
    /// the active document: every render material and decal in the model
    /// and in each part it uses, with the entities each one is attached to,
    /// and per face the colour values, texture coordinates and decal
    /// properties. The evidence the appearance export is built against:
    /// the API documentation does not say which frame the mapping vectors
    /// are in, or what GetTessTextures returns for an untextured face.
    /// </summary>
    internal static class AppearanceProbe
    {
        private const int MaxFacesPerBody = 80;

        // Late-bound by name: the add-in embeds its interop types, so the
        // compiler keeps only the members this code calls, and reflection
        // over the embedded interface sees three properties of sixty.
        private static readonly string[] RenderMaterialNames = {
            "Ambient", "XPosition", "YPosition", "RotationAngle", "Direction1RotationAngle", "Direction2RotationAngle", "Width", "Height", "WidthMirror", "HeightMirror", "FixedAspectRatio", "FitWidth", "FitHeight", "FileName", "TextureFilename", "Diffuse", "Specular", "Transparency", "Emission", "MappingType", "ProjectionReference", "MaterialID", "IlluminationShaderType", "SpecularColor", "Glossy", "Roughness", "Reflectivity", "IndexOfRefraction", "Translucency", "MetallicMix", "MetallicRoughness", "MetallicScale", "MetallicAmplitude", "MetallicFlakeMaterial", "BumpBlend", "BumpMap", "BumpScale", "BumpRadius", "BumpAmplitude", "BumpDetail", "BumpSharpness", "BumpRoughLow", "BumpRoughHigh", "BumpTextureFilename", "PatternScale", "PrimaryColor", "SecondaryColor", "TertiaryColor", "ColorForm", "LinkToFile", "BumpUseMappingScale", "IgnoreMissingFile", "DensityOfHoles", "TransparencyMappingShaderType", "Brightness", "RoundSharpEdges", "DoubleSided" };
        private static readonly string[] DecalNames = {
            "DecalID", "Hidden", "MaskType", "MaskFilename", "MaskInvert" };
        private static readonly string[] FaceDecalNames = {
            "TextureTranslationX", "TextureTranslationY", "TextureUScale", "TextureVScale", "TextureAngle", "TextureFilename", "TextureFilenameID", "TextureRenderMode", "TextureTranslationU", "TextureTranslationV", "TextureAngleUV", "TextureMirrored", "TextureMapID", "TextureID" };

        public static Dictionary<string, object> Run(IModelDoc2 model, int maxFaces, int fullFace = -1)
        {
            var reply = new Dictionary<string, object> { { "ok", true } };
            if (model == null) return new Dictionary<string, object> { { "ok", false }, { "error", "no document" } };
            reply["document"] = Safe(() => model.GetPathName());
            reply["render_materials"] = RenderMaterials(model);
            reply["decals"] = Decals(model);

            var parts = new List<object>();
            var comps = new List<object>();
            var seenDocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var assembly = model as IAssemblyDoc;
            if (assembly != null)
            {
                object[] all = null;
                try { all = assembly.GetComponents(false) as object[]; } catch { }
                foreach (var o in all ?? new object[0])
                {
                    var comp = o as IComponent2;
                    if (comp == null) continue;
                    ResolveLightweight(comp);
                    var entry = new Dictionary<string, object>
                    {
                        { "name", Safe(() => comp.Name2) },
                        { "file", Safe(() => comp.GetPathName()) },
                        { "values", Safe(() => comp.MaterialPropertyValues) },
                        { "render_materials", ComponentRenderMaterials(comp) },
                        { "decals", Safe(() => comp.GetDecalsCount()) },
                        { "faces", Faces(comp, maxFaces) },
                    };
                    comps.Add(entry);
                    var doc = Safe(() => comp.GetModelDoc2()) as IModelDoc2;
                    string path = Safe(() => comp.GetPathName()) as string;
                    if (doc != null && path != null && seenDocs.Add(path))
                    {
                        parts.Add(new Dictionary<string, object>
                        {
                            { "document", path },
                            { "values", Safe(() => (doc as IPartDoc) != null ? (doc as IPartDoc).MaterialPropertyValues : null) },
                            { "render_materials", RenderMaterials(doc) },
                            { "decals", Decals(doc) },
                        });
                    }
                }
            }
            else if (model is IPartDoc)
            {
                reply["part_faces"] = PartFaces(model as IPartDoc, maxFaces);
                if (fullFace >= 0)
                {
                    try
                    {
                        var bodies = ((IPartDoc)model).GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                        var faces = ((IBody2)bodies[0]).GetFaces() as object[];
                        var f = (IFace2)faces[fullFace];
                        reply["full_face"] = new Dictionary<string, object>
                        {
                            { "index", fullFace },
                            { "triangles", f.GetTessTriangles(true) },
                            { "textures", f.GetTessTextures() },
                            { "normals", f.GetTessNorms() },
                        };
                    }
                    catch (Exception ex) { reply["full_face"] = "error: " + ex.Message; }
                }
            }
            reply["components"] = comps;
            reply["part_documents"] = parts;
            return reply;
        }

        private static void ResolveLightweight(IComponent2 comp)
        {
            int state = -1;
            try { state = comp.GetSuppression(); } catch { }
            if (state == (int)swComponentSuppressionState_e.swComponentLightweight
                || state == (int)swComponentSuppressionState_e.swComponentFullyLightweight)
            {
                try { comp.SetSuppression2((int)swComponentSuppressionState_e.swComponentFullyResolved); } catch { }
            }
        }

        private static List<object> RenderMaterials(IModelDoc2 doc)
        {
            var list = new List<object>();
            object[] raw = null;
            try
            {
                raw = doc.Extension.GetRenderMaterials2((int)swDisplayStateOpts_e.swThisDisplayState, null) as object[];
            }
            catch { }
            foreach (var r in raw ?? new object[0])
            {
                var rm = r as IRenderMaterial;
                if (rm == null) continue;
                var d = Properties(RenderMaterialNames, rm);
                double x = 0, y = 0, z = 0;
                try { rm.GetUDirection2(out x, out y, out z); d["u_direction"] = new[] { x, y, z }; } catch { }
                try { rm.GetVDirection2(out x, out y, out z); d["v_direction"] = new[] { x, y, z }; } catch { }
                try { rm.GetCenterPoint2(out x, out y, out z); d["center_point"] = new[] { x, y, z }; } catch { }
                d["entities"] = Entities(rm);
                list.Add(d);
            }
            return list;
        }

        private static List<object> ComponentRenderMaterials(IComponent2 comp)
        {
            var list = new List<object>();
            object[] raw = null;
            try { raw = comp.GetRenderMaterials2((int)swDisplayStateOpts_e.swThisDisplayState, null) as object[]; } catch { }
            foreach (var r in raw ?? new object[0])
            {
                var rm = r as IRenderMaterial;
                if (rm == null) continue;
                list.Add(new Dictionary<string, object>
                {
                    { "FileName", Safe(() => rm.FileName) },
                    { "entities", Entities(rm) },
                });
            }
            return list;
        }

        private static List<object> Decals(IModelDoc2 doc)
        {
            var list = new List<object>();
            object[] raw = null;
            try { raw = doc.Extension.GetDecals() as object[]; } catch { }
            foreach (var o in raw ?? new object[0])
            {
                var d = Properties(DecalNames, o);
                var rm = o as IRenderMaterial;
                d["is_render_material"] = rm != null;
                var decal = o as IDecal;
                if (decal != null)
                {
                    d["mask_type"] = Safe(() => decal.MaskType);
                    d["mask_file"] = Safe(() => decal.MaskFilename);
                    d["mask_invert"] = Safe(() => decal.MaskInvert);
                }
                if (rm != null)
                {
                    // Early-bound: the decal's dispatch interface does not
                    // answer the render material's names.
                    d["texture"] = Safe(() => rm.TextureFilename);
                    d["file"] = Safe(() => rm.FileName);
                    d["width"] = Safe(() => rm.Width);
                    d["height"] = Safe(() => rm.Height);
                    d["mapping_type"] = Safe(() => rm.MappingType);
                    d["projection_reference"] = Safe(() => rm.ProjectionReference);
                    d["x_position"] = Safe(() => rm.XPosition);
                    d["y_position"] = Safe(() => rm.YPosition);
                    d["rotation"] = Safe(() => rm.RotationAngle);
                    d["width_mirror"] = Safe(() => rm.WidthMirror);
                    d["height_mirror"] = Safe(() => rm.HeightMirror);
                    foreach (var kv in Properties(RenderMaterialNames, rm)) d["rm_" + kv.Key] = kv.Value;
                    double x = 0, y = 0, z = 0;
                    try { rm.GetUDirection2(out x, out y, out z); d["u_direction"] = new[] { x, y, z }; } catch { }
                    try { rm.GetVDirection2(out x, out y, out z); d["v_direction"] = new[] { x, y, z }; } catch { }
                    try { rm.GetCenterPoint2(out x, out y, out z); d["center_point"] = new[] { x, y, z }; } catch { }
                    d["entities"] = Entities(rm);
                }
                list.Add(d);
            }
            return list;
        }

        private static List<object> Entities(IRenderMaterial rm)
        {
            var list = new List<object>();
            object[] ents = null;
            try { ents = rm.GetEntities() as object[]; } catch { }
            foreach (var e in ents ?? new object[0])
            {
                string kind = e is IFace2 ? "face" : e is IFeature ? "feature" : e is IBody2 ? "body"
                    : e is IComponent2 ? "component" : e is IModelDoc2 ? "document" : (e == null ? "null" : "other");
                var item = new Dictionary<string, object> { { "kind", kind } };
                if (e is IFeature) item["name"] = Safe(() => ((IFeature)e).Name);
                if (e is IBody2) item["name"] = Safe(() => ((IBody2)e).Name);
                if (e is IComponent2) item["name"] = Safe(() => ((IComponent2)e).Name2);
                if (e is IFace2)
                {
                    var f = (IFace2)e;
                    item["feature"] = Safe(() => ((IFeature)f.GetFeature()).Name);
                    item["body"] = Safe(() => ((IBody2)f.GetBody()).Name);
                    item["area"] = Safe(() => f.GetArea());
                    item["component"] = Safe(() => ((IComponent2)((IEntity)f).GetComponent()).Name2);
                }
                list.Add(item);
            }
            return list;
        }

        private static List<object> Faces(IComponent2 comp, int maxFaces)
        {
            var list = new List<object>();
            object[] bodies = null;
            try { bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out object _) as object[]; } catch { }
            object[] docDecals = null;
            try { docDecals = ((IModelDoc2)comp.GetModelDoc2()).Extension.GetDecals() as object[]; } catch { }
            foreach (var b in bodies ?? new object[0])
                AddFaces(b as IBody2, list, maxFaces, docDecals);
            return list;
        }

        private static List<object> PartFaces(IPartDoc part, int maxFaces)
        {
            var list = new List<object>();
            object[] bodies = null;
            try { bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[]; } catch { }
            object[] docDecals = null;
            try { docDecals = ((IModelDoc2)part).Extension.GetDecals() as object[]; } catch { }
            foreach (var b in bodies ?? new object[0])
                AddFaces(b as IBody2, list, maxFaces, docDecals);
            return list;
        }

        private static void AddFaces(IBody2 body, List<object> list, int maxFaces, object[] docDecals)
        {
            if (body == null) return;
            object[] faces = null;
            try { faces = body.GetFaces() as object[]; } catch { }
            int n = 0;
            foreach (var o in faces ?? new object[0])
            {
                if (list.Count >= maxFaces || n++ >= MaxFacesPerBody) return;
                var f = o as IFace2;
                if (f == null) continue;
                int tris = 0;
                try { tris = f.GetTessTriangleCount(); } catch { }
                object tex = Safe(() => f.GetTessTextures());
                int texLen = tex is float[] ? ((float[])tex).Length : tex is double[] ? ((double[])tex).Length : -1;
                var item = new Dictionary<string, object>
                {
                    { "body", Safe(() => body.Name) },
                    { "feature", Safe(() => ((IFeature)f.GetFeature()).Name) },
                    { "area", Safe(() => f.GetArea()) },
                    { "values", Safe(() => f.MaterialPropertyValues) },
                    { "has_values", Safe(() => f.HasMaterialPropertyValues()) },
                    { "material_user_name", Safe(() => f.MaterialUserName) },
                    { "material_id_name", Safe(() => f.MaterialIdName) },
                    { "tess_triangles", tris },
                    { "tess_textures_length", texLen },
                    { "tess_textures_head", Head(tex, 12) },
                    { "tess_triangles_head", Head(Safe(() => f.GetTessTriangles(true)), 18) },
                    { "decals", Safe(() => f.GetDecalsCount()) },
                };
                object[] decals = null;
                try { decals = f.GetAllDecalProperties() as object[]; } catch { }
                if (decals != null)
                {
                    var dl = new List<object>();
                    foreach (var dp in decals) dl.Add(Properties(FaceDecalNames, dp));
                    item["decal_properties"] = dl;
                }
                if (docDecals != null)
                {
                    var dl = new List<object>();
                    foreach (var dec in docDecals)
                    {
                        var decal = dec as Decal;
                        if (decal == null) continue;
                        FaceDecalProperties props = null;
                        try { f.IGetDecalProperties(decal, ref props); } catch { }
                        if (props != null) dl.Add(Properties(FaceDecalNames, props));
                    }
                    if (dl.Count > 0) item["doc_decal_properties"] = dl;
                }
                list.Add(item);
            }
        }

        private static List<object> Head(object arr, int n)
        {
            var list = new List<object>();
            if (arr is float[])
                foreach (var v in (float[])arr) { if (list.Count >= n) break; list.Add((double)v); }
            else if (arr is double[])
                foreach (var v in (double[])arr) { if (list.Count >= n) break; list.Add(v); }
            return list;
        }

        private static Dictionary<string, object> Properties(string[] names, object o)
        {
            var d = new Dictionary<string, object>();
            if (o == null) return d;
            foreach (var name in names)
            {
                try
                {
                    var v = o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null);
                    if (v == null || v is string || v is bool || v is int || v is double || v is float)
                        d[name] = v;
                    else
                        d[name] = v.ToString();
                }
                catch (Exception ex)
                {
                    d[name] = "error: " + (ex.InnerException ?? ex).Message;
                }
            }
            return d;
        }

        private static object Safe(Func<object> f)
        {
            try
            {
                var v = f();
                if (v is double[] || v is float[] || v is int[] || v is string || v == null
                    || v is bool || v is int || v is double || v is IModelDoc2)
                    return v;
                return v;
            }
            catch (Exception ex)
            {
                return "error: " + (ex.InnerException ?? ex).Message;
            }
        }
    }
}
