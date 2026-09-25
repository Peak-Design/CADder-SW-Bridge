/// <summary>The options of CadderExport. The defaults export the manifest only.</summary>
public sealed class CadderExportOptions
{
    /// <summary>Also write the STEP file.</summary>
    public bool Step = false;

    /// <summary>Also write the direct-link mesh (.swmesh).</summary>
    public bool Mesh = false;

    /// <summary>The folder of the files. Null: WorkDir\cadder-export\name of the document.</summary>
    public string Dir = null;

    /// <summary>The configuration to export. Null: the active configuration.</summary>
    public string Configuration = null;

    /// <summary>Keep only the selected components in the geometry.</summary>
    public bool OnlySelected = false;

    /// <summary>The mesh fineness: the 0 to 1 dial. Null: the Export Options.</summary>
    public double? Quality = null;
}

public sealed partial class SwMcpScript
{
    /// <summary>
    /// Runs the export of the CADder Bridge on the active document, as Blender
    /// asks for it (the export of the listener): the manifest, and the STEP
    /// and the mesh on request. Returns the joint shape, the counts, each
    /// joint with its limits in mm and degrees, and the warnings. Keys that
    /// start with _ (paths, log, time) are not compared by recipe_test.
    /// </summary>
    private object CadderExport(CadderExportOptions opt = null)
    {
        opt = opt ?? new CadderExportOptions();
        if (doc == null) throw new InvalidOperationException("Open a part or an assembly first.");
        string path = doc.GetPathName();
        if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("Save the document first. The export names its files after it.");
        string stem = Path.GetFileNameWithoutExtension(path);
        string dir = opt.Dir ?? Path.Combine(WorkDir, "cadder-export", stem);
        Directory.CreateDirectory(dir);

        var fields = new Dictionary<string, object>
        {
            { "document_path", path },
            { "dir", dir },
            { "step", opt.Step },
            { "mesh", opt.Mesh },
            { "only_selected", opt.OnlySelected },
        };
        if (!string.IsNullOrEmpty(opt.Configuration)) fields["configuration"] = opt.Configuration;
        if (opt.Quality != null) fields["quality"] = opt.Quality.Value;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        long mark = Cadder.LogMark();
        Dictionary<string, object> reply;
        try { reply = Cadder.Ask(swApp, "export", fields); }
        finally { Cadder.Flush(); }
        var log = Cadder.LogSince(mark);
        object okValue;
        if (!reply.TryGetValue("ok", out okValue) || !(okValue is bool) || !(bool)okValue)
        {
            object error;
            reply.TryGetValue("error", out error);
            throw new InvalidOperationException("The export failed: " + error + "\n" + string.Join("\n", log.Skip(Math.Max(0, log.Count - 15))));
        }

        var report = new Dictionary<string, object>();
        report["document"] = stem;
        report["configuration"] = reply.ContainsKey("configuration") ? reply["configuration"] : null;
        report["warnings"] = reply.ContainsKey("warnings") ? reply["warnings"] : 0;
        report["limits_left_suppressed"] = reply.ContainsKey("limits_left_suppressed") ? reply["limits_left_suppressed"] : new List<object>();
        string manifest = reply.ContainsKey("manifest") ? reply["manifest"] as string : null;
        if (manifest != null && File.Exists(manifest)) CadderExportManifest(manifest, report);
        report["_manifest"] = manifest;
        report["_step"] = reply.ContainsKey("step") ? reply["step"] : null;
        report["_mesh"] = reply.ContainsKey("mesh") ? reply["mesh"] : null;
        report["_seconds"] = Math.Round(watch.Elapsed.TotalSeconds, 1);
        report["_log"] = log.Count > 40 ? log.GetRange(log.Count - 40, 40) : log;
        return report;
    }

    /// <summary>The parts of a manifest that a change of the readers or the classifier shows in.</summary>
    private void CadderExportManifest(string path, Dictionary<string, object> report)
    {
        var m = (Dictionary<string, object>)Cadder.Call("Core.MiniJson", "ParseObject", File.ReadAllText(path));
        Func<string, List<object>> list = key =>
        {
            object v;
            return m.TryGetValue(key, out v) && v is List<object> ? (List<object>)v : new List<object>();
        };
        var shape = new SortedDictionary<string, object>(StringComparer.Ordinal);
        var joints = new List<object>();
        foreach (var o in list("joints"))
        {
            var j = o as Dictionary<string, object>;
            if (j == null) continue;
            string type = j.ContainsKey("type") ? Convert.ToString(j["type"]) : "?";
            shape[type] = (shape.ContainsKey(type) ? (int)shape[type] : 0) + 1;
            var row = new Dictionary<string, object>
            {
                { "id", j.ContainsKey("id") ? j["id"] : null },
                { "type", type },
                { "parent", j.ContainsKey("parent_group") ? j["parent_group"] : null },
                { "child", j.ContainsKey("child_group") ? j["child_group"] : null },
                { "confidence", j.ContainsKey("confidence") ? j["confidence"] : null },
            };
            var limits = j.ContainsKey("limits") ? j["limits"] as Dictionary<string, object> : null;
            if (limits != null)
            {
                var rot = limits.ContainsKey("rotation") ? limits["rotation"] as Dictionary<string, object> : null;
                var tra = limits.ContainsKey("translation") ? limits["translation"] as Dictionary<string, object> : null;
                // Degrees and millimetres, so the 0.01 tolerance of recipe_test is small.
                if (rot != null) row["rotation_deg"] = new[] { Math.Round(ToDeg(Cadder.Num(rot, "min")), 3), Math.Round(ToDeg(Cadder.Num(rot, "max")), 3) };
                if (tra != null) row["translation_mm"] = new[] { Math.Round(ToMm(Cadder.Num(tra, "min")), 3), Math.Round(ToMm(Cadder.Num(tra, "max")), 3) };
            }
            var coupling = j.ContainsKey("coupling") ? j["coupling"] as Dictionary<string, object> : null;
            if (coupling != null)
                row["coupling"] = new Dictionary<string, object>
                {
                    { "kind", coupling.ContainsKey("kind") ? coupling["kind"] : null },
                    { "driver", coupling.ContainsKey("driver_joint") ? coupling["driver_joint"] : null },
                    { "ratio", Math.Round(Cadder.Num(coupling, "ratio"), 4) },
                };
            joints.Add(row);
        }
        report["joint_shape"] = shape;
        report["components"] = list("components").Count;
        report["rigid_groups"] = list("rigid_groups").Count;
        report["loops"] = list("loops").Count;
        report["mechanisms"] = list("mechanisms").Count;
        report["joints"] = joints;
        // A warning as its code and what it names, for example
        // "OCCURRENCE_UNMATCHED c002". The message holds numbers that can
        // change, so it is not compared.
        report["manifest_warnings"] = list("warnings").Select(o =>
        {
            var w = o as Dictionary<string, object>;
            if (w == null) return Convert.ToString(o);
            var names = new List<string>();
            foreach (string key in new[] { "components", "joints" })
            {
                object v;
                if (w.TryGetValue(key, out v) && v is List<object>)
                    names.AddRange(((List<object>)v).Select(x => Convert.ToString(x)));
            }
            return ((w.ContainsKey("code") ? Convert.ToString(w["code"]) : "?") + " " + string.Join(",", names)).Trim();
        }).ToList();
    }
}
