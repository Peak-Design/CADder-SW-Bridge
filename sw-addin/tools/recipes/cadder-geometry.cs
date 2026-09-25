/// <summary>The options of the geometry checks.</summary>
public sealed class CadderGeometryOptions
{
    /// <summary>The widest small feature, in metres.</summary>
    public double MaxExtentM = 0.012;

    /// <summary>Also take curved features.</summary>
    public bool Curved = false;

    /// <summary>The mesh fineness: the 0 to 1 dial. Null: the Export Options.</summary>
    public double? Quality = null;

    /// <summary>A chord tolerance in metres, in place of the dial.</summary>
    public double? ChordM = null;

    /// <summary>The angle tolerance in radians, with ChordM.</summary>
    public double? AngleRad = null;
}

public sealed partial class SwMcpScript
{
    /// <summary>
    /// Counts the small features that each body of each part could go
    /// without, and the triangles that saves, with the closure of the body
    /// before and after (the edges with one triangle, which must stay 0).
    /// Reads only: it opens, changes and writes nothing.
    /// </summary>
    private object CadderSmallFeatures(CadderGeometryOptions opt = null)
    {
        opt = opt ?? new CadderGeometryOptions();
        if (doc == null) throw new InvalidOperationException("Open a part or an assembly first.");
        var log = Cadder.Log;
        try
        {
            var fineness = CadderFineness(opt);
            var parts = new List<object>();
            foreach (var kv in CadderPartsOf(doc))
            {
                var rows = new List<object>();
                int bodyIndex = 0;
                foreach (var body in CadderSolidBodies(kv.Value))
                {
                    bodyIndex++;
                    double tolerance = (double)Cadder.CallOn(fineness, "ChordFor", body);
                    var tess = CadderTessellation(body, tolerance, true);
                    var survey = Cadder.Call("Sw.SmallFeatureSurvey", "Survey", body, opt.MaxExtentM, tess, log, tolerance, opt.Curved);
                    // The body is closed before anything comes out, so it must be closed after too.
                    var was = CadderClosure(body, tess, null);
                    var plan = Cadder.Call("Sw.SmallFeatureSurvey", "Choose", body, opt.MaxExtentM, log, opt.Curved);
                    var now = CadderClosure(body, tess, plan);
                    var declined = new Dictionary<string, object>();
                    var sizes = new List<object>();
                    foreach (var f in (System.Collections.IEnumerable)Cadder.Get(survey, "Features"))
                    {
                        string why = (string)Cadder.Get(f, "Declined");
                        if (why == null) { sizes.Add(Cadder.Get(f, "Extent")); continue; }
                        declined[why] = (declined.ContainsKey(why) ? (int)declined[why] : 0) + 1;
                    }
                    rows.Add(new Dictionary<string, object>
                    {
                        { "body", bodyIndex },
                        { "faces", Cadder.Get(survey, "Faces") },
                        { "planar_faces", Cadder.Get(survey, "PlanarFaces") },
                        { "facets", Cadder.Get(survey, "Facets") },
                        { "facets_after", Cadder.Get(survey, "FacetsAfter") },
                        { "removed", Cadder.Get(survey, "Removed") },
                        { "declined", Cadder.Get(survey, "Declined") },
                        { "declined_why", declined },
                        { "filled_faces", Cadder.Get(survey, "FilledFaces") },
                        { "fill_before", Cadder.Get(survey, "FilledFacetsBefore") },
                        { "fill_after", Cadder.Get(survey, "FilledFacetsAfter") },
                        { "fill_refused", Cadder.Get(survey, "FillRefused") },
                        { "capped_faces", Cadder.Get(survey, "CappedFaces") },
                        { "cap_facets", Cadder.Get(survey, "CapFacets") },
                        { "open_before", was.Open + was.Doubled },
                        { "open_after", now.Open },
                        { "doubled_after", now.Doubled },
                        { "open_where", now.Where },
                        { "worst_area_slip", Cadder.Plain(Cadder.Get(survey, "WorstAreaSlip")) },
                        { "worst_area_where", Cadder.Get(survey, "WorstAreaWhere") },
                        { "removed_sizes_m", sizes },
                    });
                }
                if (rows.Count > 0) parts.Add(new { part = kv.Key, bodies = rows });
            }
            return new { maxExtentM = opt.MaxExtentM, parts };
        }
        finally { Cadder.Flush(); }
    }

    /// <summary>
    /// Asks if the texture coordinates of each planar face can be made again
    /// from its surface (Sw.PlaneUvCheck). A fill of the defeature gives its
    /// new points coordinates that SolidWorks must agree with, or a textured
    /// part shifts where it was defeatured. Reads only.
    /// </summary>
    private object CadderPlaneUv(CadderGeometryOptions opt = null)
    {
        opt = opt ?? new CadderGeometryOptions();
        if (doc == null) throw new InvalidOperationException("Open a part or an assembly first.");
        try
        {
            var fineness = CadderFineness(opt);
            var parts = new List<object>();
            foreach (var kv in CadderPartsOf(doc))
            {
                var rows = new List<object>();
                int bodyIndex = 0;
                foreach (var body in CadderSolidBodies(kv.Value))
                {
                    bodyIndex++;
                    var tess = CadderTessellation(body, (double)Cadder.CallOn(fineness, "ChordFor", body), true);
                    var check = Cadder.Call("Sw.PlaneUvCheck", "Check", body, tess, Cadder.Log);
                    if ((int)Cadder.Get(check, "Vertices") == 0) continue;
                    var row = (Dictionary<string, object>)Cadder.Plain(check, 1);
                    row["body"] = bodyIndex;
                    rows.Add(row);
                }
                if (rows.Count > 0) parts.Add(new { part = kv.Key, bodies = rows });
            }
            return new { toleranceM = Cadder.Static("Sw.PlaneUvCheck", "Tolerance"), parts };
        }
        finally { Cadder.Flush(); }
    }

    /// <summary>
    /// The tessellation of one face of the active part with the texture
    /// coordinates that SolidWorks gives it (GetTessTextures), at most limit
    /// points, in the space of the part. Shows what the appearance mapping
    /// does to a real surface.
    /// </summary>
    private object CadderTessUv(int face = 0, int limit = 40)
    {
        var p = doc as PartDoc;
        if (p == null) throw new InvalidOperationException("The active document is not a part.");
        var faces = new List<Face2>();
        foreach (var b in (object[])p.GetBodies2((int)swBodyType_e.swSolidBody, true) ?? new object[0])
            foreach (var f in (object[])((Body2)b).GetFaces() ?? new object[0])
                faces.Add((Face2)f);
        if (face < 0 || face >= faces.Count) throw new ArgumentException("face " + face + " of " + faces.Count);
        var chosen = faces[face];
        var tris = chosen.GetTessTriangles(true) as float[];
        var uvs = chosen.GetTessTextures() as float[];
        var points = new List<object>();
        if (tris != null)
        {
            int vertices = tris.Length / 9 * 3;
            int step = Math.Max(1, vertices / Math.Max(1, limit));
            for (int i = 0; i < vertices; i += step)
            {
                var one = new Dictionary<string, object> { { "x", tris[i * 3] }, { "y", tris[i * 3 + 1] }, { "z", tris[i * 3 + 2] } };
                if (uvs != null && i * 2 + 1 < uvs.Length)
                {
                    one["u"] = uvs[i * 2];
                    one["v"] = uvs[i * 2 + 1];
                }
                points.Add(one);
            }
        }
        return new
        {
            faces = faces.Count,
            face,
            surface = CadderSurfaceKind(chosen),
            triangles = tris == null ? 0 : tris.Length / 9,
            hasTextures = uvs != null,
            textureValues = uvs == null ? 0 : uvs.Length,
            points,
        };
    }

    // The closure check (was Sw.ClosureCheck in the Bridge) -------------------------------

    /// <summary>The open and doubled edges of what the plan of a defeature sends.</summary>
    private sealed class CadderClosureReport
    {
        public int Open;
        public int Doubled;
        public int Triangles;
        public List<string> Where = new List<string>();
    }

    /// <summary>
    /// Counts the edges with one triangle (open) and with more than two
    /// (doubled) of what a plan sends, by position. plan null: the body as
    /// SolidWorks tessellated it, which must give 0. It builds the faces the
    /// way the export does: gone faces out, planar fills (PlaneRefill), caps
    /// (SurfaceCap).
    /// </summary>
    private CadderClosureReport CadderClosure(Body2 body, Tessellation tess, object plan)
    {
        var report = new CadderClosureReport();
        if (body == null || tess == null) return report;
        var log = Cadder.Log;
        var used = new Dictionary<long, int>();
        var owner = new Dictionary<long, string>();
        var places = new Dictionary<int, long>();
        foreach (var o in (object[])body.GetFaces() ?? new object[0])
        {
            var face = o as Face2;
            if (face == null) continue;
            if (plan != null && (bool)Cadder.CallOn(plan, "IsGone", face)) continue;
            System.Collections.IList tris = null;
            object rims = null;
            string how = CadderSurfaceKind(face);
            if (plan != null)
            {
                int at = (int)Cadder.CallOn(plan, "FillAt", face);
                if (at >= 0)
                {
                    rims = ((System.Collections.IList)Cadder.Get(plan, "FillHoles"))[at];
                    tris = (System.Collections.IList)Cadder.Call("Sw.PlaneRefill", "Build", face, tess, rims, log);
                    how += tris == null ? ", fill refused" : ", refilled";
                }
                else
                {
                    int lid = (int)Cadder.CallOn(plan, "CapAt", face);
                    if (lid >= 0) rims = ((System.Collections.IList)Cadder.Get(plan, "CapHoles"))[lid];
                }
            }
            if (tris != null) rims = null;    // the fill covered them
            if (tris == null) tris = (System.Collections.IList)Cadder.Call("Sw.PlaneRefill", "FaceTriangles", tess, face);
            if (tris == null) continue;
            var list = tris.Cast<int>().ToList();
            if (rims != null)
            {
                var cap = (System.Collections.IEnumerable)Cadder.Call("Sw.SurfaceCap", "Build", face, tess, rims, log);
                if (cap != null)
                {
                    foreach (var piece in cap) list.AddRange(((System.Collections.IEnumerable)Cadder.Get(piece, "Triangles")).Cast<int>());
                    how += ", capped";
                }
                else how += ", cap refused";
            }
            for (int i = 0; i + 2 < list.Count; i += 3)
            {
                report.Triangles++;
                long a = CadderPlace(tess, list[i], places), b = CadderPlace(tess, list[i + 1], places), c = CadderPlace(tess, list[i + 2], places);
                CadderCount(used, owner, a, b, how);
                CadderCount(used, owner, b, c, how);
                CadderCount(used, owner, c, a, how);
            }
        }
        foreach (var kv in used)
        {
            if (kv.Value == 2) continue;
            if (kv.Value > 2) report.Doubled++; else report.Open++;
            string where;
            if (owner.TryGetValue(kv.Key, out where) && !report.Where.Contains(where) && report.Where.Count < 8) report.Where.Add(where);
        }
        return report;
    }

    private static void CadderCount(Dictionary<long, int> used, Dictionary<long, string> owner, long a, long b, string how)
    {
        long key = a < b ? a * 1000003L + b : b * 1000003L + a;
        int had;
        used[key] = used.TryGetValue(key, out had) ? had + 1 : 1;
        string was;
        owner[key] = owner.TryGetValue(key, out was) && was != how ? was + " | " + how : how;
    }

    /// <summary>A number for a point, the same for two faces that read one corner (a grid of 100 nm).</summary>
    private static long CadderPlace(Tessellation tess, int vertex, Dictionary<int, long> places)
    {
        long place;
        if (places.TryGetValue(vertex, out place)) return place;
        double[] p = null;
        try { p = tess.GetVertexPoint(vertex) as double[]; } catch (Exception) { }
        if (p == null || p.Length < 3) { places[vertex] = 0; return 0; }
        unchecked
        {
            long hash = 17;
            for (int k = 0; k < 3; k++) hash = hash * 1000003L + (long)Math.Round(p[k] / 1e-7);
            places[vertex] = hash;
            return hash;
        }
    }

    // Shared --------------------------------------------------------------------------------

    /// <summary>The fineness of the export for the options (SwCommandHandler.FinenessFrom, as for a request of Blender).</summary>
    private static object CadderFineness(CadderGeometryOptions opt)
    {
        var request = new Dictionary<string, object>();
        if (opt.Quality != null) request["quality"] = opt.Quality.Value;
        if (opt.ChordM != null) request["chord_m"] = opt.ChordM.Value;
        if (opt.AngleRad != null) request["angle_rad"] = opt.AngleRad.Value;
        var settings = Cadder.Call("Core.AppSettings", "Load", Cadder.Log, null);
        return Cadder.Call("Bridge.SwCommandHandler", "FinenessFrom", request, settings);
    }

    /// <summary>Every part document that the model uses, once each, by file name.</summary>
    private static List<KeyValuePair<string, ModelDoc2>> CadderPartsOf(ModelDoc2 model)
    {
        var found = new List<KeyValuePair<string, ModelDoc2>>();
        var assembly = model as AssemblyDoc;
        if (assembly == null)
        {
            if (model is PartDoc) found.Add(new KeyValuePair<string, ModelDoc2>(model.GetTitle(), model));
            return found;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in (object[])assembly.GetComponents(false) ?? new object[0])
        {
            var c = o as Component2;
            if (c == null || c.IsSuppressed()) continue;
            var d = c.GetModelDoc2() as ModelDoc2;
            if (!(d is PartDoc)) continue;
            string path = d.GetPathName();
            if (string.IsNullOrEmpty(path)) path = d.GetTitle();
            if (!seen.Add(path)) continue;
            found.Add(new KeyValuePair<string, ModelDoc2>(Path.GetFileNameWithoutExtension(path), d));
        }
        return found;
    }

    private static IEnumerable<Body2> CadderSolidBodies(ModelDoc2 d)
    {
        var p = d as PartDoc;
        if (p == null) yield break;
        foreach (var o in (object[])p.GetBodies2((int)swBodyType_e.swSolidBody, false) ?? new object[0])
            if (o is Body2) yield return (Body2)o;
    }

    /// <summary>A tessellation of the body with the settings of the export (shared vertices on each edge).</summary>
    private static Tessellation CadderTessellation(Body2 body, double tolerance, bool needParams)
    {
        var tess = body.GetTessellation(null) as Tessellation;
        if (tess == null) return null;
        tess.NeedFaceFacetMap = true;
        tess.NeedVertexNormal = true;
        tess.NeedVertexParams = needParams;
        tess.ImprovedQuality = true;
        tess.MatchType = (int)swTesselationMatchType_e.swTesselationMatchFacetTopology;
        tess.SurfacePlaneTolerance = tolerance;
        tess.SurfacePlaneAngleTolerance = 0.35;
        tess.CurveChordTolerance = tolerance;
        tess.CurveChordAngleTolerance = 0.35;
        return tess.Tessellate() ? tess : null;
    }

    private static string CadderSurfaceKind(Face2 face)
    {
        try
        {
            var s = face.GetSurface() as Surface;
            if (s == null) return "?";
            if (s.IsPlane()) return "plane";
            if (s.IsCylinder()) return "cylinder";
            if (s.IsCone()) return "cone";
            if (s.IsSphere()) return "sphere";
            if (s.IsTorus()) return "torus";
            return "surface";
        }
        catch (Exception) { return "?"; }
    }
}
