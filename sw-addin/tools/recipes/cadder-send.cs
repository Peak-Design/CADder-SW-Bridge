/// <summary>The options of CadderSend. The defaults are a direct-link send of the active configuration.</summary>
public sealed class CadderSendOptions
{
    /// <summary>The configurations to send. Null or empty: the active configuration.</summary>
    public List<string> Configurations = null;

    /// <summary>True: the direct link (a mesh). False: a STEP file.</summary>
    public bool Native = true;

    /// <summary>True: the payload of Refresh Model (bring the scene up to date).</summary>
    public bool Update = false;

    /// <summary>With Update: KEEP, APPEND or REGENERATE. Null: the setting.</summary>
    public string RigMode = null;

    /// <summary>"Append as a new copy" for this send only. Null: the setting.</summary>
    public bool? Append = null;

    /// <summary>"Link identical parts" for this send only. Null: the setting.</summary>
    public bool? LinkParts = null;

    /// <summary>Only the selected components. Null: as the ribbon (the option, and never for an update).</summary>
    public bool? OnlySelected = null;

    /// <summary>The Blender to send to. Null: the only one that runs (or the one that holds the document, for an update).</summary>
    public int? BlenderPid = null;

    /// <summary>The folder of the files. Null: the export folder of the Export Options, as the ribbon uses.</summary>
    public string Dir = null;

    /// <summary>The time limit of each payload in Blender.</summary>
    public int TimeoutS = 600;
}

public sealed partial class SwMcpScript
{
    /// <summary>
    /// Send to Blender with no dialogs. It uses the parts of the ribbon's send
    /// (ExportConfiguration, BuildPayload, BlenderBridge), so the files and
    /// the payloads are the ribbon's. It does not ask about the versions of
    /// CADder. Returns one row for each configuration with the reply of Blender.
    /// </summary>
    private object CadderSend(CadderSendOptions opt = null)
    {
        opt = opt ?? new CadderSendOptions();
        if (doc == null) throw new InvalidOperationException("Open a part or an assembly first.");
        string documentPath = doc.GetPathName();
        if (string.IsNullOrEmpty(documentPath)) throw new InvalidOperationException("Save the document before a send.");
        var log = Cadder.Log;
        var settings = Cadder.Call("Core.AppSettings", "Load", log, null);
        if (opt.Append != null) Cadder.Set(settings, "AppendCopies", opt.Append.Value);
        if (opt.LinkParts != null) Cadder.Set(settings, "LinkParts", opt.LinkParts.Value);
        bool onlySelected = opt.OnlySelected
            ?? (bool)Cadder.Call("SendToBlenderCommand", "GeometryFollowsSelection", settings, opt.Update);
        Cadder.Set(settings, "OnlySelected", onlySelected);
        long mark = Cadder.LogMark();
        bool isAssembly = doc is AssemblyDoc;
        string baseName = Path.GetFileNameWithoutExtension(documentPath);
        string dir = opt.Dir ?? (string)Cadder.Call("SendToBlenderCommand", "ExportDir", settings, doc, baseName);
        Directory.CreateDirectory(dir);
        string barTitle = opt.Update ? "Refreshing the model in Blender" : "Sending to Blender";

        // The selection is read once, before a configuration is shown, as the ribbon reads it.
        var keep = onlySelected ? Cadder.Call("Sw.Selection", "KeepSet", doc, log) : null;
        var names = opt.Configurations != null && opt.Configurations.Count > 0
            ? opt.Configurations : new List<string> { null };
        var jobs = new List<object>();
        object view = null;
        try
        {
            var shown = (IDisposable)Cadder.New("Sw.ConfigurationSwitch", doc, log);
            try
            {
                for (int i = 0; i < names.Count; i++)
                {
                    string wanted = names[i] ?? (string)Cadder.Get(shown, "Original");
                    var job = Cadder.New("SendToBlenderCommand+SendJob", wanted, baseName, dir, isAssembly);
                    jobs.Add(job);
                    try
                    {
                        string now = (string)Cadder.CallOn(shown, "Show", wanted);
                        Cadder.CallOn(job, "Name", now ?? wanted, baseName, dir, isAssembly);
                        Cadder.Call("SendToBlenderCommand", "ExportConfiguration", swApp, doc, settings, opt.Native,
                            names.Count > 1 ? barTitle + ": " + wanted + " (" + (i + 1) + " of " + names.Count + ")" : barTitle,
                            job, keep, names.Count > 1);
                        if ((bool)Cadder.Get(settings, "MatchView")) view = Cadder.Call("Sw.ViewReader", "Read", doc);
                    }
                    catch (Exception ex) when (ex.GetType().Name != "ExportCancelled")
                    {
                        // One configuration that fails does not stop the others, as in the ribbon's send.
                        Cadder.Set(job, "Error", ex.Message);
                    }
                }
            }
            finally { shown.Dispose(); }

            var ready = jobs.Where(j => Cadder.Get(j, "Error") == null).ToList();
            if (ready.Count == 0)
                return new { ok = false, error = "no configuration was exported", jobs = CadderSendRows(jobs), log = Cadder.LogSince(mark) };

            var target = CadderSendTarget(settings, opt.Update, documentPath, opt.BlenderPid);
            for (int i = 0; i < ready.Count; i++)
            {
                var job = ready[i];
                var payload = Cadder.Call("SendToBlenderCommand", "BuildPayload", settings,
                    opt.Native ? null : Cadder.Get(job, "StepPath"),
                    opt.Native ? Cadder.Get(job, "MeshPath") : null,
                    Cadder.Get(job, "ManifestPath"), opt.Update, opt.RigMode,
                    i == ready.Count - 1 ? view : null, documentPath, Cadder.Get(job, "Configuration"));
                Cadder.Set(job, "Payload", payload);
                try
                {
                    Cadder.Set(job, "Reply", Cadder.Call("Bridge.BlenderBridge", "PostImport", target, payload, opt.TimeoutS * 1000, log));
                }
                catch (Exception ex)
                {
                    Cadder.Set(job, "Error", ex.Message);
                }
            }
            bool anyOk = jobs.Any(j => (bool)Cadder.Get(j, "Ok"));
            // What the ribbon's Refresh Model gate reads: this session sent the document.
            if (anyOk) Cadder.Call("AddIn", "RememberSent", documentPath);
            return new
            {
                ok = jobs.All(j => (bool)Cadder.Get(j, "Ok")),
                blender = new { pid = Cadder.Get(target, "Pid"), version = Cadder.Get(target, "BlenderVersion"), addon = Cadder.Get(target, "AddonVersion") },
                jobs = CadderSendRows(jobs),
                log = Cadder.LogSince(mark),
            };
        }
        finally { Cadder.Flush(); }
    }

    /// <summary>
    /// Pushes the poses of the open assembly to Blender: the cheap half of an
    /// export, for a mechanism that moved in SolidWorks. The rows are the
    /// answer of the listener to poses, which is what Blender reads.
    /// </summary>
    private object CadderPushPoses(int? blenderPid = null)
    {
        if (!(doc is AssemblyDoc)) throw new InvalidOperationException("The active document is not an assembly.");
        string documentPath = doc.GetPathName();
        long mark = Cadder.LogMark();
        try
        {
            var poses = Cadder.Ask(swApp, "poses", new Dictionary<string, object> { { "document_path", documentPath } });
            if (!(poses["ok"] is bool) || !(bool)poses["ok"]) throw new InvalidOperationException("poses failed: " + (poses.ContainsKey("error") ? poses["error"] : "?"));
            var components = (List<object>)poses["components"];
            string title = doc.GetTitle();
            var payload = new Dictionary<string, object>
            {
                { "source", "solidworks" },
                { "document", title },
                { "poses", new Dictionary<string, object> { { "document", title }, { "components", components } } },
            };
            if (!string.IsNullOrEmpty(documentPath)) payload["source_document"] = documentPath;
            string configuration = (string)Cadder.Call("Sw.Configurations", "Active", doc);
            if (!string.IsNullOrEmpty(configuration)) payload["configuration"] = configuration;

            var settings = Cadder.Call("Core.AppSettings", "Load", Cadder.Log, null);
            var target = CadderSendTarget(settings, true, documentPath, blenderPid);
            var reply = (Dictionary<string, object>)Cadder.Call("Bridge.BlenderBridge", "PostImport", target, payload, 5 * 60 * 1000, Cadder.Log);
            return new
            {
                ok = reply != null && reply.ContainsKey("ok") && reply["ok"] is bool && (bool)reply["ok"],
                sent = components.Count,
                moved = CadderPosesMoved(reply),
                blender = Cadder.Plain(reply, 4),
                log = Cadder.LogSince(mark),
            };
        }
        finally { Cadder.Flush(); }
    }

    /// <summary>How many parts Blender moved, from its reply (stages.poses.moved), or -1.</summary>
    private static int CadderPosesMoved(Dictionary<string, object> reply)
    {
        object stages, poses;
        if (reply == null || !reply.TryGetValue("stages", out stages) || !(stages is Dictionary<string, object>)) return -1;
        if (!((Dictionary<string, object>)stages).TryGetValue("poses", out poses) || !(poses is Dictionary<string, object>)) return -1;
        return (int)Cadder.Num((Dictionary<string, object>)poses, "moved", -1);
    }

    /// <summary>The Blender to send to: the one with the pid, the only one, or a new one when the settings allow a launch.</summary>
    private object CadderSendTarget(object settings, bool update, string documentPath, int? pid)
    {
        var log = Cadder.Log;
        var found = ((System.Collections.IEnumerable)Cadder.Call("Bridge.BlenderBridge", "Discover", log)).Cast<object>().ToList();
        if (pid != null)
        {
            var hit = found.FirstOrDefault(b => (int)Cadder.Get(b, "Pid") == pid.Value);
            if (hit == null) throw new InvalidOperationException("No Blender with the CADder bridge has pid " + pid + ". Running: " + CadderBlenders(found) + ".");
            return hit;
        }
        if (update)
            found = ((System.Collections.IEnumerable)Cadder.Call("Bridge.BlenderBridge", "ForRefresh", Cadder.Call("Bridge.BlenderBridge", "Discover", log), documentPath)).Cast<object>().ToList();
        if (found.Count == 1) return found[0];
        if (found.Count > 1)
            throw new InvalidOperationException(found.Count + " Blender instances can take the send: " + CadderBlenders(found) + ". Give BlenderPid.");
        if (!(bool)Cadder.Get(settings, "AutoLaunchBlender"))
            throw new InvalidOperationException("No running Blender with the CADder bridge, and auto-launch is off in the Export Options.");
        string exe = (string)Cadder.Call("Bridge.BlenderBridge", "ResolveExe", settings);
        if (exe == null) throw new InvalidOperationException("No Blender installation was found to launch.");
        return Cadder.Call("Bridge.BlenderBridge", "Launch", exe, log);
    }

    private static string CadderBlenders(List<object> found)
    {
        return found.Count == 0 ? "none" : string.Join(", ", found.Select(b => "pid " + Cadder.Get(b, "Pid") + " (" + Cadder.Get(b, "BlendFile") + ")"));
    }

    private static List<object> CadderSendRows(List<object> jobs)
    {
        return jobs.Select(j => (object)new Dictionary<string, object>
        {
            { "configuration", Cadder.Get(j, "Configuration") },
            { "stem", Cadder.Get(j, "Stem") },
            { "ok", Cadder.Get(j, "Ok") },
            { "error", Cadder.Get(j, "Error") },
            { "manifest", Cadder.Get(j, "ManifestPath") },
            { "mesh", Cadder.Get(j, "MeshPath") },
            { "step", Cadder.Get(j, "StepPath") },
            { "blender", Cadder.Plain(Cadder.Get(j, "Reply"), 4) },
        }).ToList();
    }
}
