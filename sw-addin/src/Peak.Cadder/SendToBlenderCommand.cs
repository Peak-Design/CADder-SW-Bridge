using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Peak.Cadder.Bridge;
using Peak.Cadder.Core;
using Peak.Cadder.Sw;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.Cadder
{
    /// <summary>
    /// The one-click path: export the STEP (+ rig manifest for assemblies)
    /// with the persistent settings, no per-export dialogs, then hand the
    /// files to a running Blender over the localhost bridge, which imports,
    /// matches, snaps poses, builds the rig and parents the geometry in one
    /// go. The Keyshot-style flow: options live behind the Options button,
    /// the send itself asks nothing.
    ///
    /// Thread split: every SolidWorks call happens on the command thread
    /// BEFORE the progress dialog; the worker thread does only HTTP and
    /// process launch.
    /// </summary>
    public static class SendToBlenderCommand
    {
        /// <summary>
        /// Exports the active document and hands it to Blender.
        ///
        /// With <paramref name="native"/> the geometry is tessellated HERE
        /// and sent as a .swmesh instead of written as STEP for OpenCASCADE
        /// to rebuild. That is faster, carries appearances and component
        /// identity straight through, and gives up the solid model: the
        /// trade the two commands exist to offer.
        /// </summary>
        public static void Run(ISldWorks app, bool native = false,
                               bool update = false, string rigMode = null)
        {
            if (app == null) return;
            var model = app.ActiveDoc as IModelDoc2;
            if (model == null)
            {
                app.SendMsgToUser2("Open a part or assembly first.",
                    (int)swMessageBoxIcon_e.swMbInformation,
                    (int)swMessageBoxBtn_e.swMbOk);
                return;
            }
            if (string.IsNullOrEmpty(model.GetPathName()))
            {
                app.SendMsgToUser2("Save the document before sending.",
                    (int)swMessageBoxIcon_e.swMbWarning,
                    (int)swMessageBoxBtn_e.swMbOk);
                return;
            }

            var settings = AppSettings.Load(AddIn.Log);
            var owner = ExportOptionsDialog.ActiveOwner();
            // The export stages run on this thread and can take minutes on a
            // large assembly. The bar says which stage is running, and it
            // closes before the first dialog: SolidWorks draws a message box
            // behind a live progress bar.
            string title = update ? "Refresh Model" : "Send to Blender";
            var bar = Sw.SwProgressBar.Open(app, update
                ? "Refreshing the model in Blender" : "Sending to Blender", AddIn.Log);

            try
            {
                var assembly = model as IAssemblyDoc;
                string baseName = Path.GetFileNameWithoutExtension(model.GetPathName());
                string dir = ExportDir(settings, model, baseName);
                Directory.CreateDirectory(dir);
                string stepPath = Path.Combine(dir, baseName + ".step");
                string meshPath = Path.Combine(dir, baseName + ".swmesh");
                string manifestPath = assembly != null
                    ? Path.Combine(dir, baseName + ".rig.json") : null;

                // ── 1. Export, on this thread (COM) ─────────────────────────
                if (native)
                {
                    AddIn.Log("send to blender (native): tessellating into " + meshPath);
                    // The manifest still describes the kinematics; only the
                    // geometry's route changes, so the STEP stages are the
                    // only thing skipped.
                    // "Only the selected components" applies to the
                    // geometry of either route. The manifest still describes
                    // the whole assembly, as it does for a STEP export.
                    HashSet<string> keep = null;
                    // The rig export takes about three quarters of a direct
                    // send, the tessellation the rest.
                    bar.Window(0, 78);
                    if (assembly != null)
                    {
                        var outcome = ExportCommand.ExportBundle(
                            app, model, assembly, stepPath, manifestPath, settings,
                            manifestOnly: true,
                            mateErrorPrompt: message => ExportCommand.AskWithoutRig(app, message),
                            progress: bar);
                        // No rig: the geometry still goes, and the payload
                        // leaves out every rig stage (BuildPayload).
                        if (outcome.GeometryOnly) manifestPath = null;
                        // The export read the selection before its probes
                        // cleared it.
                        keep = outcome.KeepPaths;
                    }
                    else if (settings.OnlySelected)
                    {
                        keep = Sw.Selection.KeepSet(model, AddIn.Log);
                    }
                    bar.Window(78, 100);
                    NativeExport.Write(app, model, meshPath,
                        QualityDial(settings.QualityPreset), AddIn.Log,
                        settings.SeparateSolids, keep, bar,
                        AppearanceOptions.From(settings));
                }
                else
                {
                    AddIn.Log("send to blender: exporting " + stepPath);
                    if (assembly != null)
                    {
                        var outcome = ExportCommand.ExportBundle(
                            app, model, assembly, stepPath, manifestPath, settings,
                            mateErrorPrompt: message => ExportCommand.AskWithoutRig(app, message),
                            progress: bar);
                        if (outcome.GeometryOnly) manifestPath = null;
                    }
                    else
                    {
                        StepPlusCommand.ExportAppearanceOnly(app, model, stepPath, settings);
                    }
                }

                ExportCommand.CloseBar(bar);

                // ── 2. Choose the Blender (needs UI, still this thread) ─────
                var instances = BlenderBridge.Discover(AddIn.Log);
                BlenderInstance target = null;
                if (instances.Count == 1) target = instances[0];
                else if (instances.Count > 1)
                {
                    target = InstanceChooserDialog.Choose(owner, instances);
                    if (target == null) return;
                }
                else if (!settings.AutoLaunchBlender)
                {
                    app.SendMsgToUser2(
                        "No running Blender with the CADder bridge was "
                        + "found. Start Blender, or enable auto-launch in "
                        + "Export Options.",
                        (int)swMessageBoxIcon_e.swMbWarning,
                        (int)swMessageBoxBtn_e.swMbOk);
                    return;
                }
                string exe = target == null ? BlenderBridge.ResolveExe(settings) : null;
                if (target == null && exe == null)
                {
                    app.SendMsgToUser2(
                        "No Blender installation was found to launch. Set the "
                        + "executable in Export Options.",
                        (int)swMessageBoxIcon_e.swMbWarning,
                        (int)swMessageBoxBtn_e.swMbOk);
                    return;
                }

                // ── 3. Launch + send, on a worker under the progress bar ────
                var payload = BuildPayload(
                    settings, native ? null : stepPath, native ? meshPath : null,
                    manifestPath, update, rigMode,
                    settings.MatchView ? ViewReader.Read(model) : null);
                string doing = update
                    ? "Bringing " + baseName + " up to date in Blender…"
                    : target == null
                        ? "Launching Blender and importing " + baseName + "…"
                        : "Importing " + baseName + " in Blender…";
                var sent = ProgressDialog.Run(owner, title, doing, () =>
                {
                    var t = target ?? BlenderBridge.Launch(exe, AddIn.Log);
                    var resp = BlenderBridge.PostImport(
                        t, payload, 30 * 60 * 1000, AddIn.Log);
                    return new KeyValuePair<BlenderInstance,
                        Dictionary<string, object>>(t, resp);
                });

                // ── 4. Report ───────────────────────────────────────────────
                string summary = update
                    ? RefreshModelCommand.Summary(sent.Value)
                    : Summarize(sent.Value, baseName, sent.Key);
                bool ok = MiniJson.Flag(sent.Value, "ok");
                // What the ribbon's Refresh Model gate reads: this session
                // has put this document into a Blender that is up.
                if (ok) AddIn.RememberSent(model.GetPathName());
                if (ok && settings.FocusBlender)
                    BlenderBridge.Focus(sent.Key, AddIn.Log);
                app.SendMsgToUser2(summary,
                    ok ? (int)swMessageBoxIcon_e.swMbInformation
                       : (int)swMessageBoxIcon_e.swMbStop,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
            catch (Core.ExportCancelled)
            {
                ExportCommand.CloseBar(bar);
                AddIn.Log("send to blender stopped by the user");
                app.SendMsgToUser2("The send was stopped. Nothing went to Blender.",
                    (int)swMessageBoxIcon_e.swMbInformation,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
            catch (Exception ex)
            {
                ExportCommand.CloseBar(bar);
                AddIn.Log("send to blender failed: " + ex);
                app.SendMsgToUser2("Send to Blender failed: " + ex.Message,
                    (int)swMessageBoxIcon_e.swMbStop,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
            finally { ExportCommand.CloseBar(bar); }
        }

        /// <summary>The folder this export writes into: a folder of its
        /// own, named after the document, inside the root the user chose.
        /// The rule itself is in Core/ExportPaths, which no SolidWorks type
        /// reaches, so the tests can run it.</summary>
        internal static string ExportDir(
            AppSettings settings, IModelDoc2 model, string baseName)
        {
            string modelPath = null;
            try { modelPath = model == null ? null : model.GetPathName(); }
            catch { }
            return ExportPaths.For(settings, modelPath, baseName);
        }

        /// <summary>The quality preset as the 0..1 dial the tessellator
        /// takes. Named presets are what the options dialog already speaks;
        /// the dial is what a tolerance is computed from.</summary>
        internal static double QualityDial(string preset)
        {
            switch ((preset ?? "").ToUpperInvariant())
            {
                case "DRAFT": return 0.15;
                case "FINE": return 0.75;
                case "ULTRA": return 1.0;
                default: return 0.45;      // BALANCED
            }
        }

        internal static Dictionary<string, object> BuildPayload(
            AppSettings settings, string stepPath, string meshPath,
            string manifestPath, bool update = false, string rigMode = null,
            Dictionary<string, object> view = null)
        {
            bool rig = manifestPath != null;
            var payload = new Dictionary<string, object>
            {
                { "step", stepPath },
                { "mesh", meshPath },
                { "manifest", manifestPath },
                { "steps", new Dictionary<string, object>
                    {
                        { "import", stepPath != null },
                        // An UPDATE keeps the scene and changes what
                        // changed. Without it the import is replaced, which
                        // is right for a first send and wrong for a
                        // refresh.
                        { "update", update },
                        // A manifest sent on its own is matched against
                        // the import already standing in the scene: the
                        // STEP has not changed, only the rig has (live
                        // TongRig, 2026-09-14). With no import in the
                        // scene the match simply finds nothing, and says so.
                        { "match", rig },
                        // Syncing the poses, parenting the geometry and
                        // clearing the leftover empties always run: a rig
                        // that does not hold its geometry, or does not sit
                        // on it, is not a result anybody wants.
                        { "sync_poses", rig },
                        { "build_rig", rig && settings.BuildRig },
                        { "relink", rig && settings.BuildRig },
                        { "cleanup", true },
                    }
                },
                { "import_options", new Dictionary<string, object>
                    {
                        { "hierarchy_types", settings.Hierarchy },
                        { "quality_preset", settings.QualityPreset },
                        { "up_as", settings.UpAxis },
                        { "fw_as", "YPOS" },
                        { "import_curves", settings.ImportCurves },
                        { "separate_solids", settings.SeparateSolids },
                        { "tris_to_quads", settings.TrisToQuads },
                        { "uv_unwrap_compound", settings.UnwrapCompound },
                    }
                },
            };
            if (!string.IsNullOrEmpty(rigMode)) payload["rig_mode"] = rigMode;
            // Where SolidWorks is looking from. Blender turns its viewport
            // to the same angle when this is here, and leaves it alone when
            // it is not, so the setting travels as its presence.
            if (view != null) payload["view"] = view;
            return payload;
        }

        private static string Summarize(
            Dictionary<string, object> resp, string baseName, BlenderInstance inst)
        {
            var sb = new StringBuilder();
            if (!MiniJson.Flag(resp, "ok"))
            {
                sb.Append("Blender reported a failure for ").Append(baseName)
                  .Append(":\n\n")
                  .Append(MiniJson.Str(resp, "error", "unknown error"));
                return sb.ToString();
            }

            sb.Append("Sent ").Append(baseName).Append(" to ")
              .Append(inst.Describe()).Append(".\n");
            var stages = MiniJson.Obj(resp, "stages");
            var match = MiniJson.Obj(stages, "match");
            if (match != null)
            {
                var unmatched = MiniJson.Arr(match, "unmatched");
                var ambiguous = MiniJson.Arr(match, "ambiguous");
                sb.Append("\nMatched ").Append(MiniJson.Int(match, "matched"))
                  .Append(" component(s)");
                if (unmatched != null && unmatched.Count > 0)
                    sb.Append(", ").Append(unmatched.Count).Append(" unmatched");
                if (ambiguous != null && ambiguous.Count > 0)
                    sb.Append(", ").Append(ambiguous.Count).Append(" ambiguous");
                sb.Append(".");
            }
            var poses = MiniJson.Obj(stages, "poses");
            if (poses != null)
            {
                var moved = MiniJson.Arr(poses, "moved");
                if (moved != null && moved.Count > 0)
                    sb.Append("\n").Append(moved.Count)
                      .Append(" object(s) snapped to their SolidWorks poses.");
            }
            var rig = MiniJson.Obj(stages, "rig");
            if (rig != null)
            {
                sb.Append("\nRig: ").Append(MiniJson.Int(rig, "bones"))
                  .Append(" bone(s)");
                var warnings = MiniJson.Arr(rig, "warnings");
                if (warnings != null && warnings.Count > 0)
                    sb.Append(", ").Append(warnings.Count).Append(" warning(s)");
                sb.Append(".");
            }
            var relink = MiniJson.Obj(stages, "relink");
            if (relink != null)
                sb.Append("\nParented ").Append(MiniJson.Int(relink, "parented"))
                  .Append(" object(s) to the rig.");
            var cleanup = MiniJson.Obj(stages, "cleanup");
            if (cleanup != null && MiniJson.Int(cleanup, "removed_empties") > 0)
                sb.Append("\nRemoved ")
                  .Append(MiniJson.Int(cleanup, "removed_empties"))
                  .Append(" leftover empties.");
            var log = MiniJson.Arr(resp, "log");
            if (log != null)
                foreach (var line in log)
                    sb.Append("\n").Append(line);
            return sb.ToString();
        }
    }
}
