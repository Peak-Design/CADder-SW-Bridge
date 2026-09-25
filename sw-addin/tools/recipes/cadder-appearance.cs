using System.Reflection;

/// <summary>
/// A read-only dump of what SolidWorks says about the appearances of a
/// document: each render material and decal of the model and of each part
/// that it uses, with the entities of each, and per face the colour values,
/// the texture coordinates and the decal properties. The appearance export
/// of the Bridge is built against this evidence: the API help does not say
/// in which frame the mapping vectors are, or what GetTessTextures returns
/// for a face with no texture. (Was Bridge\AppearanceProbe.cs.)
/// </summary>
public static class CadderAppearanceProbe
{
    private const int MaxFacesPerBody = 80;

    private static readonly string[] RenderMaterialNames = {
        "Ambient", "XPosition", "YPosition", "RotationAngle", "Direction1RotationAngle", "Direction2RotationAngle", "Width", "Height", "WidthMirror", "HeightMirror", "FixedAspectRatio", "FitWidth", "FitHeight", "FileName", "TextureFilename", "Diffuse", "Specular", "Transparency", "Emission", "MappingType", "ProjectionReference", "MaterialID", "IlluminationShaderType", "SpecularColor", "Glossy", "Roughness", "Reflectivity", "IndexOfRefraction", "Translucency", "MetallicMix", "MetallicRoughness", "MetallicScale", "MetallicAmplitude", "MetallicFlakeMaterial", "BumpBlend", "BumpMap", "BumpScale", "BumpRadius", "BumpAmplitude", "BumpDetail", "BumpSharpness", "BumpRoughLow", "BumpRoughHigh", "BumpTextureFilename", "PatternScale", "PrimaryColor", "SecondaryColor", "TertiaryColor", "ColorForm", "LinkToFile", "BumpUseMappingScale", "IgnoreMissingFile", "DensityOfHoles", "TransparencyMappingShaderType", "Brightness", "RoundSharpEdges", "DoubleSided" };
    private static readonly string[] DecalNames = { "DecalID", "Hidden", "MaskType", "MaskFilename", "MaskInvert" };
    private static readonly string[] FaceDecalNames = {
        "TextureTranslationX", "TextureTranslationY", "TextureUScale", "TextureVScale", "TextureAngle", "TextureFilename", "TextureFilenameID", "TextureRenderMode", "TextureTranslationU", "TextureTranslationV", "TextureAngleUV", "TextureMirrored", "TextureMapID", "TextureID" };

    public static Dictionary<string, object> Run(IModelDoc2 model, int maxFaces, int fullFace = -1)
    {
        if (model == null) throw new InvalidOperationException("Open a part or an assembly first.");
        var reply = new Dictionary<string, object>();
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
            try { all = assembly.GetComponents(false) as object[]; } catch (Exception) { }
            foreach (var o in all ?? new object[0])
            {
                var comp = o as IComponent2;
                if (comp == null) continue;
                ResolveLightweight(comp);
                comps.Add(new Dictionary<string, object>
                {
                    { "name", Safe(() => comp.Name2) },
                    { "file", Safe(() => comp.GetPathName()) },
                    { "values", Safe(() => comp.MaterialPropertyValues) },
                    { "render_materials", ComponentRenderMaterials(comp) },
                    { "decals", Safe(() => comp.GetDecalsCount()) },
                    { "faces", Faces(comp, maxFaces) },
                });
                var d = comp.GetModelDoc2() as IModelDoc2;
                string path = Safe(() => comp.GetPathName()) as string;
                if (d != null && path != null && seenDocs.Add(path))
                {
                    parts.Add(new Dictionary<string, object>
                    {
                        { "document", path },
                        { "values", Safe(() => (d as IPartDoc) != null ? (d as IPartDoc).MaterialPropertyValues : null) },
                        { "render_materials", RenderMaterials(d) },
                        { "decals", Decals(d) },
                    });
                }
            }
        }
        else if (model is IPartDoc)
        {
            reply["part_faces"] = PartFaces((IPartDoc)model, maxFaces);
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
        try { state = comp.GetSuppression(); } catch (Exception) { }
        if (state == (int)swComponentSuppressionState_e.swComponentLightweight
            || state == (int)swComponentSuppressionState_e.swComponentFullyLightweight)
        {
            try { comp.SetSuppression2((int)swComponentSuppressionState_e.swComponentFullyResolved); } catch (Exception) { }
        }
    }

    private static List<object> RenderMaterials(IModelDoc2 d)
    {
        var list = new List<object>();
        object[] raw = null;
        try { raw = d.Extension.GetRenderMaterials2((int)swDisplayStateOpts_e.swThisDisplayState, null) as object[]; } catch (Exception) { }
        foreach (var r in raw ?? new object[0])
        {
            var rm = r as IRenderMaterial;
            if (rm == null) continue;
            var p = Properties(RenderMaterialNames, rm);
            AddVectors(rm, p);
            p["entities"] = Entities(rm);
            list.Add(p);
        }
        return list;
    }

    private static List<object> ComponentRenderMaterials(IComponent2 comp)
    {
        var list = new List<object>();
        object[] raw = null;
        try { raw = comp.GetRenderMaterials2((int)swDisplayStateOpts_e.swThisDisplayState, null) as object[]; } catch (Exception) { }
        foreach (var r in raw ?? new object[0])
        {
            var rm = r as IRenderMaterial;
            if (rm == null) continue;
            list.Add(new Dictionary<string, object> { { "FileName", Safe(() => rm.FileName) }, { "entities", Entities(rm) } });
        }
        return list;
    }

    private static List<object> Decals(IModelDoc2 d)
    {
        var list = new List<object>();
        object[] raw = null;
        try { raw = d.Extension.GetDecals() as object[]; } catch (Exception) { }
        foreach (var o in raw ?? new object[0])
        {
            var p = Properties(DecalNames, o);
            var rm = o as IRenderMaterial;
            p["is_render_material"] = rm != null;
            var decal = o as IDecal;
            if (decal != null)
            {
                p["mask_type"] = Safe(() => decal.MaskType);
                p["mask_file"] = Safe(() => decal.MaskFilename);
                p["mask_invert"] = Safe(() => decal.MaskInvert);
            }
            if (rm != null)
            {
                foreach (var kv in Properties(RenderMaterialNames, rm)) p["rm_" + kv.Key] = kv.Value;
                AddVectors(rm, p);
                p["entities"] = Entities(rm);
            }
            list.Add(p);
        }
        return list;
    }

    private static void AddVectors(IRenderMaterial rm, Dictionary<string, object> p)
    {
        double x = 0, y = 0, z = 0;
        try { rm.GetUDirection2(out x, out y, out z); p["u_direction"] = new[] { x, y, z }; } catch (Exception) { }
        try { rm.GetVDirection2(out x, out y, out z); p["v_direction"] = new[] { x, y, z }; } catch (Exception) { }
        try { rm.GetCenterPoint2(out x, out y, out z); p["center_point"] = new[] { x, y, z }; } catch (Exception) { }
    }

    private static List<object> Entities(IRenderMaterial rm)
    {
        var list = new List<object>();
        object[] ents = null;
        try { ents = rm.GetEntities() as object[]; } catch (Exception) { }
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
        object info;
        try { bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out info) as object[]; } catch (Exception) { }
        object[] docDecals = null;
        try { docDecals = ((IModelDoc2)comp.GetModelDoc2()).Extension.GetDecals() as object[]; } catch (Exception) { }
        foreach (var b in bodies ?? new object[0]) AddFaces(b as IBody2, list, maxFaces, docDecals);
        return list;
    }

    private static List<object> PartFaces(IPartDoc p, int maxFaces)
    {
        var list = new List<object>();
        object[] bodies = null;
        try { bodies = p.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[]; } catch (Exception) { }
        object[] docDecals = null;
        try { docDecals = ((IModelDoc2)p).Extension.GetDecals() as object[]; } catch (Exception) { }
        foreach (var b in bodies ?? new object[0]) AddFaces(b as IBody2, list, maxFaces, docDecals);
        return list;
    }

    private static void AddFaces(IBody2 body, List<object> list, int maxFaces, object[] docDecals)
    {
        if (body == null) return;
        object[] faces = null;
        try { faces = body.GetFaces() as object[]; } catch (Exception) { }
        int n = 0;
        foreach (var o in faces ?? new object[0])
        {
            if (list.Count >= maxFaces || n++ >= MaxFacesPerBody) return;
            var f = o as IFace2;
            if (f == null) continue;
            int tris = 0;
            try { tris = f.GetTessTriangleCount(); } catch (Exception) { }
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
            try { decals = f.GetAllDecalProperties() as object[]; } catch (Exception) { }
            if (decals != null) item["decal_properties"] = decals.Select(dp => (object)Properties(FaceDecalNames, dp)).ToList();
            if (docDecals != null)
            {
                var dl = new List<object>();
                foreach (var dec in docDecals)
                {
                    var decal = dec as Decal;
                    if (decal == null) continue;
                    FaceDecalProperties props = null;
                    try { f.IGetDecalProperties(decal, ref props); } catch (Exception) { }
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
        if (arr is float[]) foreach (var v in (float[])arr) { if (list.Count >= n) break; list.Add((double)v); }
        else if (arr is double[]) foreach (var v in (double[])arr) { if (list.Count >= n) break; list.Add(v); }
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
                d[name] = v == null || v is string || v is bool || v is int || v is double || v is float ? v : v.ToString();
            }
            catch (Exception ex) { d[name] = "error: " + (ex.InnerException ?? ex).Message; }
        }
        return d;
    }

    private static object Safe(Func<object> f)
    {
        try
        {
            var v = f();
            // A SolidWorks object is not data for the result.
            return v != null && System.Runtime.InteropServices.Marshal.IsComObject(v) ? v.GetType().Name : v;
        }
        catch (Exception ex) { return "error: " + (ex.InnerException ?? ex).Message; }
    }
}

/// <summary>The options of CadderApplyAppearance.</summary>
public sealed class CadderAppearanceOptions
{
    /// <summary>The appearance file (.p2m), for example from the SOLIDWORKS appearance library.</summary>
    public string Path = null;

    /// <summary>In a part: "document", "body" or "face:N". In an assembly with Component: "component" or "face:N".</summary>
    public string Target = null;

    /// <summary>In an assembly: the instance path of the component (Name2, for example "sub-1/part-2").</summary>
    public string Component = null;

    /// <summary>The width and height of a texture tile, in metres. 0: as the file says.</summary>
    public double Width = 0, Height = 0;

    /// <summary>The mapping type (swMappingType value). -1: as the file says.</summary>
    public int MappingType = -1;

    /// <summary>The rotation of the texture, in radians. NaN: as the file says.</summary>
    public double Rotation = double.NaN;

    /// <summary>The U and V directions of the mapping (three values each). Null: as the file says.</summary>
    public double[] U = null, V = null;
}

public sealed partial class SwMcpScript
{
    /// <summary>
    /// A dump of the appearances and decals of the active document: each
    /// render material with its values and entities, for each part and
    /// component, and per face the colour values, texture coordinates and
    /// decal properties (at most maxFaces faces). In a part, face gives the
    /// full triangles and texture coordinates of that face. Reads only.
    /// </summary>
    private object CadderAppearances(int maxFaces = 60, int face = -1)
    {
        try { return CadderAppearanceProbe.Run(doc, maxFaces, face); }
        finally { Cadder.Flush(); }
    }

    /// <summary>
    /// Puts a library appearance (.p2m) on the active part (the document, its
    /// first body, or one face of it), or in an assembly on one component or
    /// one face of it. In memory: nothing is saved. Use it to put a known
    /// texture on a model and compare SolidWorks with Blender.
    /// </summary>
    private object CadderApplyAppearance(CadderAppearanceOptions opt)
    {
        if (opt == null || string.IsNullOrEmpty(opt.Path) || !File.Exists(opt.Path))
            throw new ArgumentException("Give Path: an appearance file (.p2m) that exists.");
        if (doc == null) throw new InvalidOperationException("Open a part or an assembly first.");
        var rm = doc.Extension.CreateRenderMaterial(opt.Path) as RenderMaterial;
        if (rm == null) throw new InvalidOperationException("CreateRenderMaterial returned nothing for " + opt.Path + ".");
        if (opt.Width > 0) rm.Width = opt.Width;
        if (opt.Height > 0) rm.Height = opt.Height;
        if (opt.MappingType >= 0) rm.MappingType = opt.MappingType;
        if (!double.IsNaN(opt.Rotation)) rm.RotationAngle = opt.Rotation;
        if (opt.U != null && opt.U.Length == 3) rm.SetUDirection2(opt.U[0], opt.U[1], opt.U[2]);
        if (opt.V != null && opt.V.Length == 3) rm.SetVDirection2(opt.V[0], opt.V[1], opt.V[2]);

        object entity;
        string target;
        var assembly = doc as AssemblyDoc;
        if (assembly != null)
        {
            if (string.IsNullOrEmpty(opt.Component)) throw new ArgumentException("In an assembly, give Component.");
            var comp = assembly.GetComponentByName(opt.Component) as Component2;
            if (comp == null) throw new InvalidOperationException("No component " + opt.Component + ".");
            target = opt.Target ?? "component";
            entity = comp;
            if (target.StartsWith("face:", StringComparison.Ordinal))
            {
                object info;
                var bodies = comp.GetBodies3((int)swBodyType_e.swSolidBody, out info) as object[];
                var body = bodies == null || bodies.Length == 0 ? null : bodies[0] as Body2;
                if (body == null) throw new InvalidOperationException("The component has no solid body.");
                var face = CadderFaceAt(body, target);
                // The face as the assembly sees it, picked as a click picks it. A face
                // taken from the bodies of the component does not take an appearance.
                entity = CadderPickFace(comp, face) ?? face;
            }
        }
        else
        {
            var p = doc as PartDoc;
            if (p == null) throw new InvalidOperationException("The active document is not a part or an assembly.");
            target = opt.Target ?? "document";
            entity = doc;
            if (target == "body" || target.StartsWith("face:", StringComparison.Ordinal))
            {
                var bodies = p.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                var body = bodies == null || bodies.Length == 0 ? null : bodies[0] as Body2;
                if (body == null) throw new InvalidOperationException("The part has no solid body.");
                entity = target == "body" ? (object)body : CadderFaceAt(body, target);
            }
        }
        int id;
        rm.AddEntity(entity);
        bool added = doc.Extension.AddRenderMaterial(rm, out id);
        doc.GraphicsRedraw2();
        return new { added, materialId = id, target, component = opt.Component, file = rm.FileName, width = rm.Width, height = rm.Height, mappingType = rm.MappingType, rotation = rm.RotationAngle };
    }

    private static Face2 CadderFaceAt(Body2 body, string target)
    {
        int index;
        if (!int.TryParse(target.Substring(5), out index)) throw new ArgumentException("The face index is not a number: " + target);
        var faces = body.GetFaces() as object[];
        if (faces == null || index < 0 || index >= faces.Length) throw new ArgumentException("No face " + index + ".");
        return (Face2)faces[index];
    }

    /// <summary>The face of the assembly that a ray at the middle of the first facet of face hits, or null.</summary>
    private object CadderPickFace(Component2 comp, Face2 face)
    {
        try
        {
            var tris = face.GetTessTriangles(true) as float[];
            var norms = face.GetTessNorms() as float[];
            if (tris == null || tris.Length < 9 || norms == null || norms.Length < 3) return null;
            double[] p = { (tris[0] + tris[3] + tris[6]) / 3.0, (tris[1] + tris[4] + tris[7]) / 3.0, (tris[2] + tris[5] + tris[8]) / 3.0 };
            double[] n = { norms[0], norms[1], norms[2] };
            var data = comp.Transform2 == null ? null : comp.Transform2.ArrayData as double[];
            if (data != null && data.Length >= 12)
            {
                double[] q = new double[3], m = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    q[i] = p[0] * data[i] + p[1] * data[3 + i] + p[2] * data[6 + i] + data[9 + i];
                    m[i] = n[0] * data[i] + n[1] * data[3 + i] + n[2] * data[6 + i];
                }
                p = q;
                n = m;
            }
            const double off = 0.001;
            doc.ClearSelection2(true);
            if (!doc.Extension.SelectByRay(p[0] + n[0] * off, p[1] + n[1] * off, p[2] + n[2] * off, -n[0], -n[1], -n[2], 0.0005,
                    (int)swSelectType_e.swSelFACES, false, 0, 0)) return null;
            return ((SelectionMgr)doc.SelectionManager).GetSelectedObject6(1, -1);
        }
        catch (Exception) { return null; }
    }
}
