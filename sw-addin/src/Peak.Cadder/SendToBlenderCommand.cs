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
    /// the send itself asks nothing. The one exception is the Multiple
    /// configurations option, which asks which configurations to send.
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
        ///
        /// <paramref name="configurations"/> names the configurations to
        /// send, as Refresh Model does. Null asks the user when the Multiple
        /// configurations option is on, and takes the active configuration
        /// when it is off. Each configuration is a complete send of its own
        /// with its own files (ConfigurationNames.Stem), so each one stands
        /// in Blender in its own collection with its own rig. The exports
        /// run first, one after the other. Then the payloads go to Blender,
        /// one after the other, in the same order.
        /// </summary>
        public static void Run(ISldWorks app, bool native = false,
                               bool update = false, string rigMode = null,
                               IList<string> configurations = null)
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
            string documentPath = model.GetPathName();
            if (string.IsNullOrEmpty(documentPath))
            {
                app.SendMsgToUser2("Save the document before sending.",
                    (int)swMessageBoxIcon_e.swMbWarning,
                    (int)swMessageBoxBtn_e.swMbOk);
                return;
            }

            var settings = AppSettings.Load(AddIn.Log);
            // For this send only. Nothing here saves the settings.
            settings.OnlySelected = GeometryFollowsSelection(settings, update);
            var owner = ExportOptionsDialog.ActiveOwner();
            var assembly = model as IAssemblyDoc;
            var sending = configurations != null
                ? new List<string>(configurations)
                : ChooseConfigurations(owner, model, settings, assembly != null);
            if (sending == null || sending.Count == 0) return;     // the user cancelled
            // A CADder that does not match this add-in is asked about before
            // the export, which can take minutes, and before anything is sent
            // (VersionGate). Here when the Blender to send to is already
            // known, after the choice or the launch below otherwise.
            if (!VersionGate.Confirm(owner, KnownTarget(update, documentPath),
                                     AddIn.Log))
                return;
            string title = update ? "Refresh Model" : "Send to Blender";
            string barTitle = update ? "Refreshing the model in Blender" : "Sending to Blender";

            try
            {
                string baseName = Path.GetFileNameWithoutExtension(documentPath);
                string dir = ExportDir(settings, model, baseName);
                Directory.CreateDirectory(dir);

                // ── 1. Export each configuration, on this thread (COM) ──────
                // "Only the selected components" reads the selection once,
                // before anything changes the document. Showing another
                // configuration can clear it, and the probe of the first
                // export does clear it, so every configuration uses this
                // one reading. A component keeps its name in every
                // configuration, so the set means the same parts in each.
                HashSet<string> keep = settings.OnlySelected
                    ? Sw.Selection.KeepSet(model, AddIn.Log) : null;
                var jobs = new List<SendJob>();
                Dictionary<string, object> view = null;
                // The configuration that was active is shown again at the
                // end, also when an export fails or the user stops it.
                using (var shown = new ConfigurationSwitch(model, AddIn.Log))
                {
                    for (int i = 0; i < sending.Count; i++)
                    {
                        var job = new SendJob(sending[i] ?? shown.Original, baseName, dir,
                                              assembly != null);
                        jobs.Add(job);
                        try
                        {
                            job.Name(shown.Show(job.Configuration) ?? job.Configuration,
                                     baseName, dir, assembly != null);
                            AddIn.Log("send to blender: configuration " + job.Configuration
                                + " (" + (i + 1) + " of " + sending.Count + ") as " + job.Stem);
                            ExportConfiguration(app, model, settings, native,
                                sending.Count > 1
                                    ? barTitle + ": " + job.Configuration
                                      + " (" + (i + 1) + " of " + sending.Count + ")"
                                    : barTitle,
                                job, keep, sending.Count > 1);
                            // Blender turns its view once, after the last
                            // import, so only the last payload carries the
                            // view. It is read while its configuration is
                            // shown, because the box it frames is that
                            // configuration's.
                            if (settings.MatchView) view = ViewReader.Read(model);
                        }
                        catch (Core.ExportCancelled) { throw; }
                        catch (Exception ex)
                        {
                            // One configuration that fails does not stop the
                            // others. The report names it with its reason.
                            AddIn.Log("send to blender: " + job.Stem + " failed: " + ex);
                            job.Error = ex.Message;
                        }
                    }
                }

                var ready = jobs.FindAll(j => j.Error == null);
                if (ready.Count == 0)
                {
                    app.SendMsgToUser2(Report(jobs, update, null, title),
                        (int)swMessageBoxIcon_e.swMbStop,
                        (int)swMessageBoxBtn_e.swMbOk);
                    return;
                }

                // ── 2. Choose the Blender (needs UI, still this thread) ─────
                var instances = BlenderBridge.Discover(AddIn.Log);
                // A refresh goes to the Blender that holds the scene, and
                // asks only when two of them do.
                if (update)
                    instances = BlenderBridge.ForRefresh(instances, documentPath);
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

                // ── 3. Launch, check the versions, send ─────────────────────
                // The launch has a bar of its own, so the versions of the new
                // Blender are checked before anything goes to it.
                if (target == null)
                    target = ProgressDialog.Run(owner, title,
                        "Launching Blender…", () => BlenderBridge.Launch(exe, AddIn.Log));
                if (!VersionGate.Confirm(owner, target, AddIn.Log))
                    return;
                for (int i = 0; i < ready.Count; i++)
                    ready[i].Payload = BuildPayload(
                        settings, native ? null : ready[i].StepPath,
                        native ? ready[i].MeshPath : null,
                        ready[i].ManifestPath, update, rigMode,
                        i == ready.Count - 1 ? view : null,
                        documentPath, ready[i].Configuration);
                // The worker does only HTTP. A payload that fails does not
                // stop the ones after it: each is a complete send, and the
                // report says which ones arrived.
                var to = target;
                var failures = new Exception[ready.Count];
                ProgressDialog.Run(owner, title, Doing(update, ready[0].Stem, 0, ready.Count),
                    say =>
                    {
                        for (int i = 0; i < ready.Count; i++)
                        {
                            if (i > 0) say(Doing(update, ready[i].Stem, i, ready.Count));
                            try
                            {
                                ready[i].Reply = BlenderBridge.PostImport(
                                    to, ready[i].Payload, 30 * 60 * 1000, AddIn.Log);
                            }
                            catch (Exception ex) { failures[i] = ex; }
                        }
                        return true;
                    });
                for (int i = 0; i < ready.Count; i++)
                {
                    if (failures[i] == null) continue;
                    AddIn.Log("send to blender: " + ready[i].Stem + " did not reach Blender: "
                        + failures[i]);
                    ready[i].Error = failures[i].Message;
                }

                // ── 4. Report ───────────────────────────────────────────────
                bool anyOk = jobs.Exists(j => j.Ok);
                bool allOk = jobs.TrueForAll(j => j.Ok);
                // What the ribbon's Refresh Model gate reads: this session
                // has put this document into a Blender that is up.
                if (anyOk) AddIn.RememberSent(documentPath);
                if (anyOk && settings.FocusBlender)
                    BlenderBridge.Focus(target, AddIn.Log);
                app.SendMsgToUser2(Report(jobs, update, target, title),
                    allOk ? (int)swMessageBoxIcon_e.swMbInformation
                    : anyOk ? (int)swMessageBoxIcon_e.swMbWarning
                    : (int)swMessageBoxIcon_e.swMbStop,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
            catch (Core.ExportCancelled)
            {
                AddIn.Log("send to blender stopped by the user");
                app.SendMsgToUser2("The send was stopped. Nothing went to Blender.",
                    (int)swMessageBoxIcon_e.swMbInformation,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
            catch (Exception ex)
            {
                AddIn.Log("send to blender failed: " + ex);
                app.SendMsgToUser2(title + " failed: " + ex.Message,
                    (int)swMessageBoxIcon_e.swMbStop,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
        }

        /// <summary>
        /// The configurations a send takes when the caller does not say.
        /// With the Multiple configurations option on, the user ticks them
        /// (ConfigurationPickerDialog). Null when the user cancels. With
        /// the option off, or with one configuration in the document, the
        /// active configuration: there is nothing to choose.
        /// </summary>
        private static List<string> ChooseConfigurations(
            System.Windows.Forms.IWin32Window owner, IModelDoc2 model,
            AppSettings settings, bool isAssembly)
        {
            string active = Configurations.Active(model);
            if (!settings.MultipleConfigurations) return new List<string> { active };
            var names = Configurations.Names(model);
            if (names.Count <= 1) return new List<string> { active };
            return ConfigurationPickerDialog.Choose(owner, model.GetPathName(), names, active,
                isAssembly && settings.BuildRig);
        }

        /// <summary>
        /// Exports the configuration that SolidWorks shows now into the
        /// files of <paramref name="job"/>. The mate errors and the
        /// geometry without a rig work per configuration: a rig that one
        /// configuration cannot have does not take the rig from the
        /// others.
        /// </summary>
        private static void ExportConfiguration(
            ISldWorks app, IModelDoc2 model, AppSettings settings, bool native,
            string barTitle, SendJob job, HashSet<string> keep, bool several)
        {
            var assembly = model as IAssemblyDoc;
            // With several configurations, the question about the mates
            // says which configuration it is about.
            Func<string, string> about = message => several
                ? "Configuration: " + job.Configuration + "\n\n" + message : message;
            // The export stages run on this thread and can take minutes on a
            // large assembly. The bar says which stage is running, and it
            // closes before the first dialog: SolidWorks draws a message box
            // behind a live progress bar.
            var bar = Sw.SwProgressBar.Open(app, barTitle, AddIn.Log);
            try
            {
                if (native)
                {
                    AddIn.Log("send to blender (native): tessellating into " + job.MeshPath);
                    // The manifest still describes the kinematics. Only the
                    // geometry's route changes, so the STEP stages are the
                    // only thing skipped.
                    // "Only the selected components" applies to the
                    // geometry of either route, for a first send only
                    // (GeometryFollowsSelection). The manifest still
                    // describes the whole assembly, as it does for a STEP
                    // export.
                    // The rig export takes about three quarters of a direct
                    // send, the tessellation the rest.
                    bar.Window(0, 78);
                    if (assembly != null)
                    {
                        var outcome = ExportCommand.ExportBundle(
                            app, model, assembly, job.StepPath, job.ManifestPath, settings,
                            manifestOnly: true, matchStep: false,
                            mateErrorPrompt: message =>
                            {
                                bool yes = ExportCommand.WithoutBar(
                                    bar, () => ExportCommand.AskWithoutRig(app, about(message)));
                                // The geometry still goes, and it takes a
                                // while on a large assembly, so it gets a
                                // bar of its own.
                                if (yes) bar = Sw.SwProgressBar.Open(app, barTitle, AddIn.Log);
                                return yes;
                            },
                            progress: bar, keepPaths: keep);
                        // No rig: the geometry still goes, and the payload
                        // leaves out every rig stage (BuildPayload).
                        if (outcome.GeometryOnly) job.ManifestPath = null;
                    }
                    bar.Window(78, 100);
                    NativeExport.Write(app, model, job.MeshPath,
                        FinenessOf(settings), AddIn.Log,
                        settings.SeparateSolids, keep, bar,
                        AppearanceOptions.From(settings));
                }
                else
                {
                    AddIn.Log("send to blender: exporting " + job.StepPath);
                    if (assembly != null)
                    {
                        var outcome = ExportCommand.ExportBundle(
                            app, model, assembly, job.StepPath, job.ManifestPath, settings,
                            mateErrorPrompt: message => ExportCommand.WithoutBar(
                                bar, () => ExportCommand.AskWithoutRig(app, about(message))),
                            progress: bar, keepPaths: keep);
                        if (outcome.GeometryOnly) job.ManifestPath = null;
                    }
                    else
                    {
                        StepPlusCommand.ExportAppearanceOnly(app, model, job.StepPath, settings);
                    }
                }
            }
            finally { ExportCommand.CloseBar(bar); }
        }

        /// <summary>One configuration of a send: its files, the payload
        /// that goes to Blender, and what came back.</summary>
        internal sealed class SendJob
        {
            /// <summary>The configuration, as the document spells it.</summary>
            public string Configuration;

            /// <summary>The name of the files, and of the import in Blender
            /// (ConfigurationNames.Stem).</summary>
            public string Stem;

            public string StepPath;
            public string MeshPath;

            /// <summary>Null for a part, and for an assembly that goes
            /// without a rig.</summary>
            public string ManifestPath;

            public Dictionary<string, object> Payload;
            public Dictionary<string, object> Reply;

            /// <summary>Why the export failed or the payload did not reach
            /// Blender. Null when neither happened.</summary>
            public string Error;

            internal SendJob(string configuration, string baseName, string dir, bool assembly)
            {
                Name(configuration, baseName, dir, assembly);
            }

            /// <summary>Names the files after the configuration.</summary>
            internal void Name(string configuration, string baseName, string dir, bool assembly)
            {
                Configuration = configuration;
                Stem = ConfigurationNames.Stem(baseName, configuration);
                StepPath = Path.Combine(dir, Stem + ".step");
                MeshPath = Path.Combine(dir, Stem + ".swmesh");
                ManifestPath = assembly ? Path.Combine(dir, Stem + ".rig.json") : null;
            }

            /// <summary>Blender took the payload and reported success.</summary>
            public bool Ok
            {
                get { return Error == null && MiniJson.Flag(Reply, "ok"); }
            }
        }

        /// <summary>What the progress dialog says while Blender takes job
        /// <paramref name="index"/> (from 0) of <paramref name="count"/>.
        /// </summary>
        internal static string Doing(bool update, string stem, int index, int count)
        {
            string text = update
                ? "Bringing " + stem + " up to date in Blender…"
                : "Importing " + stem + " in Blender…";
            return count > 1 ? text + " (" + (index + 1) + " of " + count + ")" : text;
        }

        /// <summary>
        /// What to tell the user after a send: the summary of each job. A
        /// send of one configuration says what it said before 1.2.0. A send
        /// of several names each configuration above its summary, the ones
        /// that failed included, so that one failure does not hide among
        /// the successes.
        /// </summary>
        internal static string Report(IList<SendJob> jobs, bool update,
                                      BlenderInstance target, string title)
        {
            if (jobs == null || jobs.Count == 0) return "Nothing was sent.";
            if (jobs.Count == 1) return Result(jobs[0], update, target, title);
            var sb = new StringBuilder();
            foreach (var job in jobs)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(job.Configuration).Append(":\n")
                  .Append(Result(job, update, target, title));
            }
            return sb.ToString();
        }

        private static string Result(SendJob job, bool update, BlenderInstance target,
                                     string title)
        {
            if (job.Error != null) return title + " failed: " + job.Error;
            return update
                ? RefreshModelCommand.Summary(job.Reply)
                : Summarize(job.Reply, job.Stem, target);
        }

        /// <summary>
        /// The Blender a send goes to, when that is known before the export:
        /// the only running one (for a refresh, the only one that holds the
        /// document). Null when there are several (the user chooses after
        /// the export) or none (one is launched).
        /// </summary>
        private static BlenderInstance KnownTarget(bool update, string documentPath)
        {
            try
            {
                var instances = BlenderBridge.Discover(AddIn.Log);
                if (update) instances = BlenderBridge.ForRefresh(instances, documentPath);
                return instances.Count == 1 ? instances[0] : null;
            }
            catch (Exception ex)
            {
                // Never a reason not to send: the check runs again at the send.
                AddIn.Log("versions: could not list the running Blenders: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Whether a send cuts its geometry to the selected components.
        ///
        /// Never for an update. Blender compares the scene with the new
        /// file and removes every part the file does not hold, so a file
        /// cut to the selection deleted every part that was not selected,
        /// with the materials and modifiers the user had put on it. One
        /// click on a face selects its component, so a stray click before
        /// Refresh Model was enough.
        /// </summary>
        internal static bool GeometryFollowsSelection(AppSettings settings, bool update)
        {
            return settings.OnlySelected && !update;
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
        /// <summary>What the Export Options cut a send to: a quality name,
        /// Custom, or Relative Tessellation. The same settings, with the
        /// same meaning, as the STEP import in Blender.</summary>
        internal static BodyTessellator.Fineness FinenessOf(AppSettings settings)
        {
            if (settings.QualityRelative)
                return BodyTessellator.RelativeTo(
                    settings.QualityRelativeDistance, settings.QualityAngle);
            if (string.Equals(settings.QualityPreset, "CUSTOM",
                              StringComparison.OrdinalIgnoreCase))
                return BodyTessellator.Custom(
                    settings.QualityDistance, settings.QualityAngle);
            return BodyTessellator.FinenessFor(QualityDial(settings.QualityPreset));
        }

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
            Dictionary<string, object> view = null, string sourceDocument = null,
            string configuration = null)
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
                        // Custom and Relative, for the STEP route: the
                        // import dialog's own names for them.
                        { "lin_deflection_len", settings.QualityDistance },
                        { "ang_deflection_rot", settings.QualityAngle },
                        { "tessellation_relative", settings.QualityRelative },
                        { "lin_deflection_rel", settings.QualityRelativeDistance },
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
            // The document the scene comes from. Blender keeps it and names
            // it in every request it sends back, so SolidWorks answers for
            // this document and not for the one that is in front then.
            if (!string.IsNullOrEmpty(sourceDocument))
                payload["source_document"] = sourceDocument;
            // The configuration the files hold, by its real name. The file
            // names carry only the safe name (ConfigurationNames), and
            // Blender keeps this one on the import to name it back in every
            // request.
            if (!string.IsNullOrEmpty(configuration))
                payload["configuration"] = configuration;
            // A new copy beside the earlier send, for a send only: Refresh
            // Model brings the send and its copies up to date instead.
            if (settings.AppendCopies && !update) payload["append"] = true;
            // Blender links a part to the same part in the scene unless it
            // is told not to, so only "no" travels.
            if (!settings.LinkParts) payload["link_parts"] = false;
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
