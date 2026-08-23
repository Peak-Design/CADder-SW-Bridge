using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Peak.SwToBlender.Bridge;
using Peak.SwToBlender.Core;
using Peak.SwToBlender.Sw;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.SwToBlender
{
    /// <summary>
    /// The one-click path: export the STEP (+ rig manifest for assemblies)
    /// with the persistent settings, no per-export dialogs, then hand the
    /// files to a running Blender over the localhost bridge — which imports,
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
        public static void Run(ISldWorks app)
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

            try
            {
                var assembly = model as IAssemblyDoc;
                string baseName = Path.GetFileNameWithoutExtension(model.GetPathName());
                string dir = ExportDir(settings, model, baseName);
                Directory.CreateDirectory(dir);
                string stepPath = Path.Combine(dir, baseName + ".step");
                string manifestPath = assembly != null
                    ? Path.Combine(dir, baseName + ".rig.json") : null;

                // ── 1. Export, on this thread (COM) ─────────────────────────
                AddIn.Log("send to blender: exporting " + stepPath);
                if (assembly != null)
                {
                    ExportCommand.ExportBundle(
                        app, model, assembly, stepPath, manifestPath, settings);
                }
                else
                {
                    StepPlusCommand.ExportAppearanceOnly(app, model, stepPath, settings);
                }

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
                        "No running Blender with the STEPper NEXT bridge was "
                        + "found. Start Blender, or enable auto-launch in "
                        + "Blender Options.",
                        (int)swMessageBoxIcon_e.swMbWarning,
                        (int)swMessageBoxBtn_e.swMbOk);
                    return;
                }
                string exe = target == null ? BlenderBridge.ResolveExe(settings) : null;
                if (target == null && exe == null)
                {
                    app.SendMsgToUser2(
                        "No Blender installation was found to launch. Set the "
                        + "executable in Blender Options.",
                        (int)swMessageBoxIcon_e.swMbWarning,
                        (int)swMessageBoxBtn_e.swMbOk);
                    return;
                }

                // ── 3. Launch + send, on a worker under the progress bar ────
                var payload = BuildPayload(settings, stepPath, manifestPath);
                string doing = target == null
                    ? "Launching Blender and importing " + baseName + "…"
                    : "Importing " + baseName + " in Blender…";
                var sent = ProgressDialog.Run(owner, "Send to Blender", doing, () =>
                {
                    var t = target ?? BlenderBridge.Launch(exe, AddIn.Log);
                    var resp = BlenderBridge.PostImport(
                        t, payload, 30 * 60 * 1000, AddIn.Log);
                    return new KeyValuePair<BlenderInstance,
                        Dictionary<string, object>>(t, resp);
                });

                // ── 4. Report ───────────────────────────────────────────────
                string summary = Summarize(sent.Value, baseName, sent.Key);
                bool ok = MiniJson.Flag(sent.Value, "ok");
                if (ok && settings.FocusBlender)
                    BlenderBridge.Focus(sent.Key, AddIn.Log);
                app.SendMsgToUser2(summary,
                    ok ? (int)swMessageBoxIcon_e.swMbInformation
                       : (int)swMessageBoxIcon_e.swMbStop,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
            catch (Exception ex)
            {
                AddIn.Log("send to blender failed: " + ex);
                app.SendMsgToUser2("Send to Blender failed: " + ex.Message,
                    (int)swMessageBoxIcon_e.swMbStop,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
        }

        private static string ExportDir(
            AppSettings settings, IModelDoc2 model, string baseName)
        {
            if (settings.ExportFolderMode == "beside")
                return Path.GetDirectoryName(model.GetPathName());
            // System.Environment spelled out: the sldworks interop also
            // declares an Environment type.
            return Path.Combine(
                System.Environment.GetFolderPath(
                    System.Environment.SpecialFolder.LocalApplicationData),
                "Peak", "SwToBlender", "exports", baseName);
        }

        private static Dictionary<string, object> BuildPayload(
            AppSettings settings, string stepPath, string manifestPath)
        {
            bool rig = manifestPath != null;
            return new Dictionary<string, object>
            {
                { "step", stepPath },
                { "manifest", manifestPath },
                { "steps", new Dictionary<string, object>
                    {
                        { "import", true },
                        { "match", rig },
                        { "sync_poses", rig && settings.SyncPoses },
                        { "build_rig", rig && settings.BuildRig },
                        { "relink", rig && settings.ParentGeometry },
                        { "cleanup", settings.CleanupEmpties },
                    }
                },
                { "import_options", new Dictionary<string, object>
                    {
                        { "hierarchy_types", settings.Hierarchy },
                        { "quality_preset", settings.QualityPreset },
                        { "up_as", settings.UpAxis },
                        { "fw_as", "YPOS" },
                    }
                },
            };
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
