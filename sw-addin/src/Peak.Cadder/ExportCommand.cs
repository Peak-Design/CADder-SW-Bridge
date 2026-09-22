using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using Peak.Cadder.Core;
using Peak.Cadder.Core.Model;
using Peak.Cadder.Sw;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.Cadder
{
    /// <summary>
    /// The export command. Command-flow shape from Peak.NextStep's
    /// ExportCommand: guard, dialog, file picker, one Export method that does
    /// the work, and every failure lands in the log AND in front of the user.
    ///
    /// The pipeline: walk the assembly (WYSIWYG scope), read the mates, group
    /// rigid pairs, classify the residual freedoms, cut the loops, export the
    /// STEP, match the occurrences, write the manifest. The STEP and the
    /// manifest are written in the same pass so every name in the manifest
    /// matches the file exactly: that is the whole contract.
    /// </summary>
    /// <summary>What ExportBundle produced, for the caller's reporting.</summary>
    public sealed class RigExportOutcome
    {
        public string Report;
        public string StepPath;
        public string ManifestPath;
        public int Warnings;

        /// <summary>The assembly had mate errors and the user chose to
        /// export the geometry without a rig. There is no manifest.</summary>
        public bool GeometryOnly;

        /// <summary>The instance paths the export kept, or null when it kept
        /// everything. The caller reuses it for the mesh, because the
        /// selection itself is gone by the time the export returns: the DOF
        /// probe clears it while it drags components.</summary>
        public System.Collections.Generic.HashSet<string> KeepPaths;
    }

    public static class ExportCommand
    {
        public static void Run(ISldWorks app)
        {
            if (app == null) return;
            var model = app.ActiveDoc as IModelDoc2;
            var assembly = model as IAssemblyDoc;
            if (assembly == null)
            {
                app.SendMsgToUser2("Open an assembly first.",
                    (int)swMessageBoxIcon_e.swMbInformation,
                    (int)swMessageBoxBtn_e.swMbOk);
                return;
            }
            if (string.IsNullOrEmpty(model.GetPathName()))
            {
                // An unsaved assembly has no stable file name to derive the
                // STEP and manifest names from, and no persistent ids yet.
                app.SendMsgToUser2("Save the assembly before exporting.",
                    (int)swMessageBoxIcon_e.swMbWarning,
                    (int)swMessageBoxBtn_e.swMbOk);
                return;
            }

            var settings = AppSettings.Load(AddIn.Log);
            if (!ExportOptionsDialog.Show(settings)) return;
            settings.Save(AddIn.Log);

            string suggested = Path.ChangeExtension(model.GetPathName(), ".step");
            string stepPath;
            using (var dlg = new SaveFileDialog
            {
                Title = "Export rig manifest + STEP",
                Filter = "STEP files (*.step;*.stp)|*.step;*.stp",
                FileName = Path.GetFileName(suggested),
                InitialDirectory = Path.GetDirectoryName(suggested),
                OverwritePrompt = true,
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                stepPath = dlg.FileName;
            }
            string manifestPath = Path.ChangeExtension(stepPath, ".rig.json");

            var bar = Sw.SwProgressBar.Open(app, "Exporting to Blender", AddIn.Log);
            try
            {
                // Every stage that mutates model state restores it in its own
                // finally block (StepExporter restores the STEP AP preference;
                // the DOF probe, when a later milestone enables it, restores
                // fix flags and mate suppression). This catch is the last
                // line of defence, not the restore path.
                var outcome = ExportBundle(
                    app, model, assembly, stepPath, manifestPath, settings,
                    mateErrorPrompt: message => AskWithoutRig(app, message),
                    progress: bar);
                CloseBar(bar);
                app.SendMsgToUser2(outcome.Report,
                    (int)swMessageBoxIcon_e.swMbInformation,
                    (int)swMessageBoxBtn_e.swMbOk);

                if (settings.OpenFolder)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(
                            "explorer.exe", "/select,\"" + stepPath + "\"");
                    }
                    catch (Exception ex) { AddIn.Log("open folder: " + ex.Message); }
                }
            }
            catch (ExportCancelled)
            {
                AddIn.Log("export stopped by the user");
                app.SendMsgToUser2("The export was stopped. No files were written.",
                    (int)swMessageBoxIcon_e.swMbInformation,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
            catch (Exception ex)
            {
                AddIn.Log("export failed: " + ex);
                app.SendMsgToUser2("Export failed: " + ex.Message,
                    (int)swMessageBoxIcon_e.swMbStop,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
            finally { CloseBar(bar); }
        }

        /// <summary>Closes the progress bar, if it is one. A message box
        /// under a live bar is drawn behind it, so the bar goes first.
        /// </summary>
        internal static void CloseBar(ExportProgress progress)
        {
            var bar = progress as IDisposable;
            if (bar != null) bar.Dispose();
        }

        /// <summary>
        /// Manifest-only re-export (Oscar, 2026-08-23: iterating the rig
        /// analysis on a large assembly must not pay for a STEP write every
        /// time). Same pipeline minus the STEP/appearance stages; occurrence
        /// matching runs against the EXISTING STEP beside the manifest when
        /// one is there. No options dialog: the saved settings drive the
        /// probes, straight to the file picker.
        /// </summary>
        public static void RunManifestOnly(ISldWorks app)
        {
            if (app == null) return;
            var model = app.ActiveDoc as IModelDoc2;
            var assembly = model as IAssemblyDoc;
            if (assembly == null)
            {
                app.SendMsgToUser2("Open an assembly first.",
                    (int)swMessageBoxIcon_e.swMbInformation,
                    (int)swMessageBoxBtn_e.swMbOk);
                return;
            }
            if (string.IsNullOrEmpty(model.GetPathName()))
            {
                app.SendMsgToUser2("Save the assembly before exporting.",
                    (int)swMessageBoxIcon_e.swMbWarning,
                    (int)swMessageBoxBtn_e.swMbOk);
                return;
            }

            var settings = AppSettings.Load(AddIn.Log);
            string suggested = Path.ChangeExtension(model.GetPathName(), ".rig.json");
            string manifestPath;
            using (var dlg = new SaveFileDialog
            {
                Title = "Export rig manifest (no STEP write)",
                Filter = "Rig manifests (*.rig.json)|*.rig.json",
                FileName = Path.GetFileName(suggested),
                InitialDirectory = Path.GetDirectoryName(suggested),
                OverwritePrompt = true,
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK) return;
                manifestPath = dlg.FileName;
            }

            var bar = Sw.SwProgressBar.Open(app, "Exporting the rig", AddIn.Log);
            try
            {
                var outcome = ExportBundle(app, model, assembly,
                    ManifestStepPath(manifestPath), manifestPath, settings,
                    manifestOnly: true, progress: bar);
                CloseBar(bar);
                app.SendMsgToUser2(outcome.Report,
                    (int)swMessageBoxIcon_e.swMbInformation,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
            catch (ExportCancelled)
            {
                AddIn.Log("manifest-only export stopped by the user");
                app.SendMsgToUser2("The export was stopped. No files were written.",
                    (int)swMessageBoxIcon_e.swMbInformation,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
            catch (Exception ex)
            {
                AddIn.Log("manifest-only export failed: " + ex);
                app.SendMsgToUser2("Export failed: " + ex.Message,
                    (int)swMessageBoxIcon_e.swMbStop,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
            finally { CloseBar(bar); }
        }

        /// <summary>The STEP file a manifest describes: the same base name
        /// beside it. Path.ChangeExtension alone would turn x.rig.json into
        /// x.rig.step: the .rig marker must go with the .json.</summary>
        /// <summary>The mate errors, at most ten of them named.</summary>
        internal static string MateErrorReport(List<string> mateErrors)
        {
            const int shown = 10;
            var list = string.Join("\n  ",
                mateErrors.GetRange(0, Math.Min(shown, mateErrors.Count)).ToArray());
            if (mateErrors.Count > shown)
                list += "\n  ... and " + (mateErrors.Count - shown) + " more";
            return "Mate errors or over-defined mates are in this assembly:\n  " + list;
        }

        /// <summary>What the user is asked. Yes exports the geometry with no
        /// rig, No stops the export.</summary>
        internal static string MateErrorQuestion(string trouble)
        {
            return trouble
                + "\n\nSolidWorks does not solve these mates as they are modelled, "
                + "so a rig made from them would move wrongly.\n\n"
                + "Do you want to export the geometry without a rig?\n\n"
                + "Yes: export the geometry only, with no rig.\n"
                + "No: stop the export, so you can fix the mates.";
        }

        /// <summary>Asks whether to export the geometry without a rig.
        /// Yes continues, No stops the export.</summary>
        internal static bool AskWithoutRig(ISldWorks app, string message)
        {
            int answer = app.SendMsgToUser2(message,
                (int)swMessageBoxIcon_e.swMbQuestion,
                (int)swMessageBoxBtn_e.swMbYesNo);
            return answer == (int)swMessageBoxResult_e.swMbHitYes;
        }

        private static string ManifestStepPath(string manifestPath)
        {
            if (manifestPath.EndsWith(".rig.json", StringComparison.OrdinalIgnoreCase))
                return manifestPath.Substring(
                    0, manifestPath.Length - ".rig.json".Length) + ".step";
            return Path.ChangeExtension(manifestPath, ".step");
        }

        /// <summary>
        /// The whole rig+STEP export, reusable by both the ribbon command and
        /// the Blender bridge. Appearance repair and the flexible-twin STEP
        /// fix run between SaveAs3 and the occurrence matcher, so the manifest
        /// is matched (and hashed) against the FINAL file. With manifestOnly
        /// the STEP/appearance stages are skipped and the existing file on
        /// disk (if any) serves for matching and the hash.
        /// </summary>
        public static RigExportOutcome ExportBundle(
            ISldWorks app, IModelDoc2 model, IAssemblyDoc assembly,
            string stepPath, string manifestPath, AppSettings settings,
            bool manifestOnly = false, Func<string, bool> mateErrorPrompt = null,
            ExportProgress progress = null)
        {
            // The numbers beside each stage are its share of the whole
            // export, 0 to 100. They come from timing the samples here: the
            // two probes are most of a long export, because each of their
            // readings drags a component in the model.
            progress = progress ?? ExportProgress.None;
            // Read the selection before anything else touches the
            // document. The DOF probe clears the selection while it drags
            // components, so a keep set read later in the export is always
            // empty, and "only the selected components" quietly exported
            // everything (found 2026-09-16). Null means no restriction,
            // which is also what an empty selection gives.
            var keep = settings.OnlySelected
                ? Sw.Selection.KeepSet(model, AddIn.Log) : null;
            int ap = settings.Ap == 203 ? 203 : 214;
            bool runDofProbe = settings.RunDofProbe;
            // The appearance entity forms are AP214's; AP203 carries no
            // appearances to repair.
            bool repairAppearances = settings.RepairAppearances && ap == 214;
            // ── 1. The WYSIWYG walk and the mate graph ──────────────────────
            progress.Stage("Reading the assembly", 0, 8);
            var walked = AssemblyWalker.Walk(assembly, AddIn.Log);
            if (walked.Count == 0)
                throw new InvalidOperationException("The assembly has no components to export.");
            progress.Stage("Reading the mates", 8, 20);
            var graph = MateReader.Read(walked, AddIn.Log, model);
            // Mirror features carry no mate, so they are read straight off the
            // feature tree and paired by geometry.
            MirrorFeatureReader.Read(model, walked, graph, AddIn.Log);

            // ── 1b. Errored mates: the user decides ─────────────────────────
            // An errored or over-defining mate is one SolidWorks itself is
            // not solving faithfully: with an over-defined set, SolidWorks
            // picks which mate to ignore and the classifier cannot know
            // which. A rig built on that would be wrong, so the export
            // offers the geometry without a rig instead, and stops when the
            // caller has no one to ask (the listener and the test harness).
            // Suppressed mates are exempt: they are intentionally off and
            // skipped everywhere.
            var mateErrors = new List<string>();
            foreach (var gm in graph.Mates)
                if (gm.Error != null && !gm.Suppressed)
                    mateErrors.Add(gm.FeatureName + " " + gm.Error);
            if (mateErrors.Count > 0)
            {
                string trouble = MateErrorReport(mateErrors);
                AddIn.Log("export: " + mateErrors.Count + " mate error(s)");
                bool withoutRig = mateErrorPrompt != null
                    && mateErrorPrompt(MateErrorQuestion(trouble));
                if (!withoutRig)
                    throw new InvalidOperationException(
                        trouble + "\n\nFix or delete these mates and export again.");
                AddIn.Log("export: the user chose the geometry without a rig");
                var geometry = new RigExportOutcome
                {
                    GeometryOnly = true,
                    KeepPaths = keep,
                    Report = "Exported the geometry without a rig: the assembly has "
                        + mateErrors.Count + " mate error(s).",
                };
                if (manifestOnly) return geometry;
                StepPlusCommand.ExportAppearanceOnly(app, model, stepPath, settings);
                geometry.StepPath = stepPath;
                return geometry;
            }

            // ── 2. Groups, joints, loops ────────────────────────────────────
            // The mate analysis supplies the TOPOLOGY, which bodies hang off
            // which, the one thing SolidWorks will not tell you, because
            // "how much freedom does this have" is always answered relative to
            // ground and never relative to a parent you have yet to choose.
            // The solver supplies the KINEMATICS of each connection, which it
            // has already worked out for the whole assembly at once.
            progress.StopIfCancelled();
            // SolidWorks' own reading of what can move comes first, with the
            // limit mates out (see TakeLimitsOut), and the DOF probe reads in
            // the same state. Both change the model and put it back, so they
            // run BEFORE the STEP export: whatever a restore missed would at
            // least be visible in the exported geometry rather than baked
            // into an earlier file.
            RigidGroupingResult grouping;
            List<PairVerdict> verdicts;
            SolveState limitsOut = null;
            try
            {
                if (runDofProbe)
                {
                    progress.Stage("Reading what SolidWorks says can move", 20, 22);
                    limitsOut = TakeLimitsOut(model, walked);
                }
                progress.Stage("Grouping the bodies", 22, 24);
                grouping = RigidGrouper.Group(graph);
                verdicts = runDofProbe
                    ? ProbePairs(model, walked, grouping, progress)
                    : new List<PairVerdict>();
            }
            finally
            {
                PutLimitsBack(model, limitsOut, mateErrorPrompt != null ? app : null);
            }
            progress.StopIfCancelled();
            // The verdicts are read as a SET: a pair whose child is mated to a
            // third body is a weld when the probe called that third body rigid
            // too (the bolted-on buoyancy module) and a follower when it did
            // not (the cutting head on the lead screw rod). See SolverWelds.
            // The probe FIXES the parent body before reading, so pinning
            // an intermediate link of a mechanism freezes the mechanism and
            // every pair in it then reads rigid: live corpus 06
            // (2026-08-25): the four-bar's crank was welded into its coupler
            // and the linkage stopped working. The GROUND is already fixed,
            // so a ground-parent reading is the only one that leaves the
            // model untouched, and "no freedom relative to the world" is
            // then a fact about the assembly rather than about what the
            // probe just pinned.
            //
            // Keyed on the Grounded FLAG. GroupA is only "the group with the
            // lower list index", which coincides with the ground until an
            // assembly has none at all.
            string groundGroup = null;
            foreach (var g in grouping.Groups)
                if (g.Grounded) { groundGroup = g.Id; break; }
            if (groundGroup == null && verdicts.Count > 0)
                AddIn.Log("DOF probe: nothing grounds this assembly, so no "
                    + "reading can be taken relative to the world; the mate "
                    + "analysis stands alone for every pair");

            // The closure answers a DIFFERENT question from the weld gate:
            // not "may we merge this pair" but "which bodies did the probe
            // find rigid", which is what tells a bolted cluster from a
            // follower. So every rigid reading feeds it, whatever its parent
            // was: filtering by parent here would drop a member of a
            // genuine cluster and unweld the whole of it, and the
            // ground-parent rule is about what we ACT on, not about what the
            // probe saw.
            var claimed = new List<string[]>();
            foreach (var v in verdicts)
                if (v.Type == JointType.Fixed && !v.ProbeBlind && v.Weldable)
                    claimed.Add(new[] { v.GroupA, v.GroupB });
            var welds = new SolverWelds(graph, grouping, claimed);

            var solverRigid = new List<string[]>();
            foreach (var v in verdicts)
            {
                // Same three conditions `trustworthy` names above, taken
                // one at a time so the log can say which one failed.
                if (v.Type != JointType.Fixed || v.ProbeBlind || !v.Weldable)
                    continue;
                if (groundGroup == null || v.GroupA != groundGroup)
                {
                    AddIn.Log("DOF probe " + v.GroupA + "/" + v.GroupB
                        + ": reads rigid, but the probe had to FIX " + v.GroupA
                        + " to read it, and pinning a moving body freezes "
                        + "everything downstream of it; only a ground parent "
                        + "leaves the model as it was");
                    continue;
                }
                if (!welds.Believable(v.GroupA, v.GroupB))
                {
                    AddIn.Log("DOF probe " + v.GroupA + "/" + v.GroupB
                        + ": reads rigid, but the child is mated to a body the "
                        + "probe did NOT read rigid, so it may be a follower "
                        + "rather than welded");
                    continue;
                }
                // SolidWorks' own status, read with the limits out, outranks
                // the probe: the probe pins the whole parent group before it
                // reads, and with that pinned it read the clamps, the rams,
                // the cutting head and a bolt with a broken concentric all
                // rigid against the ground, while the status called every one
                // of them under-defined, which is what they are (live
                // CutterRig, 2026-09-21).
                string moving = MovesPerSolidWorks(grouping, graph, v.GroupB);
                if (moving != null)
                {
                    AddIn.Log("DOF probe " + v.GroupA + "/" + v.GroupB
                        + ": reads rigid, but SolidWorks says " + moving
                        + " can move, so it is not welded");
                    continue;
                }
                solverRigid.Add(new[] { v.ComponentA, v.ComponentB });
            }
            if (solverRigid.Count > 0)
            {
                AddIn.Log("DOF probe: the solver reads " + solverRigid.Count
                    + " pair(s) as having no relative freedom; regrouping");
                grouping = RigidGrouper.Group(graph, solverRigid);
            }
            LogGrounding(grouping);
            // The limit-sign probe only fires when a limit rests at a
            // degenerate pose (dragged to its hard stop and exported: live
            // corpus 01, 2026-08-23: the hinge at its horizontal limit rigged
            // mirrored). It nudges one component and restores, so like the
            // DOF probe it runs BEFORE the STEP export.
            progress.Stage("Classifying the joints", 58, 64);
            var signOracle = new LimitSignProbe(app, model, walked, grouping, AddIn.Log);
            var classification = JointClassifier.Classify(graph, grouping, signOracle);
            ApplySolverVerdicts(grouping, classification, verdicts, groundGroup);
            // Carrier links synthesized for tangent contacts are groups like
            // any other from here on: they join the loop graph and the
            // manifest, they just own no components.
            var allGroups = new List<RigidGroup>(grouping.Groups);
            allGroups.AddRange(classification.VirtualGroups);
            var loops = LoopAnalyzer.Analyze(allGroups, classification.Joints);
            // The loops can weld groups together, and a mate the grouping
            // could not read over three groups may then hold a joint.
            if (RigidGrouper.HoldAcrossWelds(graph, grouping, loops.Joints, AddIn.Log).Count > 0)
                loops = LoopAnalyzer.Analyze(allGroups, loops.Joints);
            // Symmetric couplings annotate the FINAL joint list and may
            // APPEND a mirror pair (two ground-rooted free joints for a
            // symmetric-only body pair), so they resolve before the manifest
            // is assembled: island detection must see those joints.
            var symWarnings = SymmetricCoupler.Resolve(
                graph, grouping, loops.Joints, loops.Loops);
            symWarnings.AddRange(
                MirrorFeatureCoupler.Resolve(graph, grouping, loops.Joints, loops.Loops));
            // Cam profiles and universal joints are functions the solver
            // knows and no mate records: the probe turns the driver through
            // a revolution and tables the driven joint. Same model-moving
            // rules as the DOF probe, so it runs only when that does, and
            // it restores every transform it touched.
            {
                progress.Stage("Reading the cams and the couplings", 64, 78);
                var unread = new Dictionary<string, string>();
                var modelled = runDofProbe
                    ? RelationProbe.Resolve(
                        app, model, walked, grouping, graph, loops.Joints, AddIn.Log,
                        settings.RelationStepDeg, unread)
                    : new List<string>();
                // A cam the probe could not table (free in its plane, on a
                // slide) or did not turn (probe off) travels as its faces
                // instead, and the consumer holds the follower on them.
                var contacts = CamContact.Resolve(
                    grouping, graph, loops.Joints, modelled, unread, AddIn.Log);
                modelled.AddRange(contacts);
                // A cam neither route could read keeps its warning, with the
                // reasons in it, so the user hears why rather than "not
                // rigged".
                foreach (var w in classification.Warnings)
                {
                    if (w.Code != "CAM_FOLLOWER") continue;
                    foreach (var kv in unread)
                    {
                        if (!w.Message.Contains("mate " + kv.Key + " ")) continue;
                        w.Message = "Cam-follower mate " + kv.Key + " is not rigged: " + kv.Value
                            + ". The follower's own joint is exported; pose it by hand to match the cam.";
                        break;
                    }
                }
                if (modelled.Count > 0)
                {
                    classification.Warnings.RemoveAll(w =>
                        w.Code == "CAM_FOLLOWER"
                        && modelled.Exists(n => w.Message.Contains(n)));
                    // The cam-follower pair's own joint carried the "not
                    // rigged" note; the relation now lives on the follower's
                    // mount joint as a table.
                    foreach (var j in loops.Joints)
                    {
                        if (string.IsNullOrEmpty(j.Notes)) continue;
                        bool contact = j.SourceMates.Exists(s => contacts.Contains(s.SwFeature));
                        if (!contact && !j.SourceMates.Exists(s => modelled.Contains(s.SwFeature))) continue;
                        j.Notes = j.Notes.Replace(
                            "A cam-follower mate rides this pair; the cam relation is not rigged.",
                            contact
                                ? "A cam-follower mate rides this pair; the cam relation is a cam "
                                  + "contact on the follower's own joint."
                                : "A cam-follower mate rides this pair; the cam relation is a table "
                                  + "coupling on the follower's own joint.");
                    }
                }
            }

            // ── 3. The STEP file, its post-processing, then the manifest ────
            // Every pass below: write, parse, rewrite, hash, parse again:
            // runs on the STAGING path, which is a local scratch file when the
            // target sits on a network drive. The finished file crosses the
            // wire once, at Publish.
            string sha1 = null;
            Appearance.AppearancePipelineResult post;
            MatchResult matches;
            using (var staging = manifestOnly
                ? StepStaging.ForRead(stepPath, AddIn.Log)
                : StepStaging.ForWrite(stepPath, AddIn.Log))
            {
            string workingStep = staging.WorkingPath;
            if (manifestOnly)
            {
                post = new Appearance.AppearancePipelineResult();
                if (File.Exists(workingStep))
                {
                    sha1 = StepExporter.Sha1Hex(workingStep);
                    post.Notes.Add("Manifest only: the STEP was not re-written; "
                        + "occurrences were matched against the existing "
                        + Path.GetFileName(stepPath) + ".");
                }
                else
                {
                    post.Notes.Add("Manifest only, and no STEP file sits beside the "
                        + "manifest: occurrence paths are null and the Blender side "
                        + "falls back to transform matching.");
                }
            }
            else
            {
                progress.StopIfCancelled();
                progress.Stage("Writing the STEP file", 78, 95);
                var step = StepExporter.Export(app, model, workingStep, ap, AddIn.Log,
                    exportAppearances: repairAppearances,
                    includeHidden: settings.IncludeHidden,
                    keep: keep);

                // Flexible-twin fix + appearance repair + materials, one parse,
                // one save. Errors here must not kill the export: the file as
                // SolidWorks wrote it is still usable.
                // NB: types under Appearance.* stay namespace-qualified here, a
                // using would make Part21 ambiguous against Sw.Part21.
                var flexRequests = FlexibleLayoutBuilder.Build(walked, AddIn.Log);
                try
                {
                    post = Appearance.AppearancePipeline.Run(model, workingStep,
                        repairAppearances, settings.DeInstance,
                        settings.EngineeringMaterial, settings.IncludeHidden,
                        flexRequests, AddIn.Log, keep);
                }
                catch (Exception ex)
                {
                    AddIn.Log("STEP post-processing failed: " + ex);
                    post = new Appearance.AppearancePipelineResult();
                    post.Notes.Add("STEP post-processing failed (" + ex.Message
                        + "); the file is as SolidWorks wrote it.");
                }
                sha1 = post.FileModified ? StepExporter.Sha1Hex(workingStep) : step.Sha1;
                staging.Publish();
            }

            if (File.Exists(workingStep))
            {
                try
                {
                    var matcher = new OccurrenceMatcher(new Part21(workingStep), AddIn.Log);
                    matches = matcher.Match(walked);
                }
                catch (Exception ex)
                {
                    // A manifest with null occurrence paths is degraded but
                    // usable: the Blender side falls back to transform
                    // matching. A dead export over a matcher bug is not.
                    AddIn.Log("occurrence matching failed: " + ex);
                    matches = new MatchResult();
                }
            }
            else
            {
                matches = new MatchResult();
            }
            }   // staging: the scratch STEP, if any, is removed here

            // A joint whose channel a coupling writes cannot be an input:
            // a driver is one-way, and pushing a cam's follower never turns
            // the cam. The relation probe runs after the loop analysis
            // chose the inputs, so with couplings added the analysis runs
            // again on its own joints (it is a fixed point of itself) and
            // chooses with the couplings known. Anything still driven is
            // pruned with a note.
            if (loops.Joints.Exists(j => j.Coupling != null && !string.IsNullOrEmpty(j.Coupling.DriverJoint)))
                loops = LoopAnalyzer.Analyze(allGroups, loops.Joints);
            LoopAnalyzer.PruneDrivenInputs(loops);

            var manifest = BuildManifest(
                app, model, walked, grouping, allGroups, classification, loops, matches,
                Path.GetFileName(stepPath), ap, sha1, post.FlexFix);
            manifest.Warnings.AddRange(symWarnings);

            progress.Stage("Writing the manifest", 95, 100);
            ManifestWriter.WriteFile(manifest, manifestPath);
            AddIn.Log("wrote " + manifestPath);

            string report = (manifestOnly
                    ? "Wrote " + Path.GetFileName(manifestPath) + " (manifest only)."
                    : "Exported " + Path.GetFileName(stepPath) + " + "
                        + Path.GetFileName(manifestPath) + ".") + "\n\n"
                + manifest.Components.Count + " component(s) in "
                + manifest.RigidGroups.Count + " rigid group(s), "
                + manifest.Joints.Count + " joint(s), "
                + manifest.Loops.Count + " loop(s).\n"
                + matches.MatchedCount + " of " + matches.ExportedCount
                + " occurrence(s) matched in the STEP file.\n"
                + manifest.Warnings.Count + " warning(s). See the manifest for details.";
            if (post.Notes.Count > 0)
                report += "\n\n" + string.Join("\n", post.Notes.ToArray());

            var outcome = new RigExportOutcome();
            outcome.Report = report;
            outcome.StepPath = stepPath;
            outcome.ManifestPath = manifestPath;
            outcome.Warnings = manifest.Warnings.Count;
            outcome.KeepPaths = keep;
            return outcome;
        }

        // ── What SolidWorks says can move ───────────────────────────────────

        /// <summary>The first top-level component of a group that SolidWorks
        /// calls under-defined with the limits out, or null.</summary>
        private static string MovesPerSolidWorks(
            RigidGroupingResult grouping, MateGraph graph, string groupId)
        {
            RigidGroup group = null;
            foreach (var g in grouping.Groups) if (g.Id == groupId) { group = g; break; }
            if (group == null) return null;
            var members = new HashSet<string>(group.Components);
            foreach (var c in graph.Components)
                if (members.Contains(c.Id) && c.ParentId == null && c.StatusFree == 2)
                    return c.Path ?? c.Id;
            return null;
        }

        /// <summary>
        /// Takes every limit mate out, top level and inside flexible
        /// subassemblies, and reads SolidWorks' constrained status of every
        /// component into GraphComponent.StatusFree. A limit mate counts as a
        /// fixed dimension, so with it in, a part behind it reads fully
        /// defined while it moves (live CutterRig, 2026-09-21: the whole
        /// cutting head). With the limits out, a top-level component that
        /// reads fully constrained cannot move, and RigidGrouper welds it.
        /// EditRebuild3 is the call that makes the status follow the change;
        /// Extension.Rebuild with swUpdateMates does not.
        ///
        /// Couplings stay in: a gear reads under-defined with its gear mate
        /// in, and the DOF probe's reading of a coupled pair depends on it.
        /// </summary>
        private static SolveState TakeLimitsOut(
            IModelDoc2 model, List<WalkedComponent> walked)
        {
            var state = SolveState.Suppress(model, walked, false, AddIn.Log);
            if (state.Count > 0 && !SolveState.Rebuild(model, "edit"))
                AddIn.Log("solve state: the rebuild after taking the limits out failed");
            foreach (var w in walked)
            {
                if (w.Graph == null || w.Comp == null || w.Graph.Suppressed) continue;
                try { w.Graph.StatusFree = w.Comp.GetConstrainedStatus(); }
                catch { w.Graph.StatusFree = 0; }
            }
            // Inside a flexible subassembly the status in the top solve says
            // nothing, so each child is read again in its own document.
            state.ReadSubStatus(walked);
            foreach (var w in walked)
            {
                if (w.Graph == null || w.Comp == null || w.Graph.Suppressed) continue;
                AddIn.Log("status: " + w.Graph.Id + " " + w.Graph.Path
                    + " parent=" + (w.Graph.ParentId ?? "-")
                    + " fixed=" + (w.Graph.IsFixed ? 1 : 0)
                    + " insub=" + (w.Graph.FixedInSubassembly ? 1 : 0)
                    + " on=" + w.Graph.ConstrainedStatus
                    + " free=" + w.Graph.StatusFree
                    + " sub=" + w.Graph.SubStatusFree);
            }
            return state;
        }

        /// <summary>
        /// Puts the limit mates back and rebuilds. A mate SolidWorks will not
        /// put back is still suppressed in its document, and a document such
        /// as a hydraulic ram can be shared by other assemblies, so the user
        /// is told which mates, when there is a user to tell.
        /// </summary>
        private static void PutLimitsBack(IModelDoc2 model, SolveState state, ISldWorks app)
        {
            if (state == null || state.Count == 0) return;
            var failed = state.Restore();
            state.RebuildSubDocuments();
            if (!SolveState.Rebuild(model, "edit"))
                AddIn.Log("solve state: the rebuild after putting the limits back failed");
            if (failed.Count == 0 || app == null) return;
            try
            {
                app.SendMsgToUser2(
                    "CADder Bridge could not put back these limit mates after it "
                    + "read the assembly. They are still suppressed:\n\n"
                    + string.Join("\n", failed.ToArray())
                    + "\n\nUnsuppress them before you save.",
                    (int)swMessageBoxIcon_e.swMbWarning,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
            catch { }
        }

        // ── Grounding diagnostics ───────────────────────────────────────────

        /// <summary>
        /// What the grouping produced, checked against SolidWorks both ways:
        /// what it welded because SolidWorks says it cannot move, and any
        /// component SolidWorks says CAN move that ended up in the ground.
        /// </summary>
        private static void LogGrounding(RigidGroupingResult grouping)
        {
            int grounded = 0;
            foreach (var g in grouping.Groups) if (g.Grounded) grounded++;

            AddIn.Log("grounding: " + grouping.Groups.Count + " rigid group(s) from "
                + grouping.ComponentGroup.Count + " component(s)");
            if (grouping.StatusWelds.Count > 0)
                AddIn.Log("grounding: " + grouping.StatusWelds.Count + " component(s) "
                    + "welded to the ground because SolidWorks says they cannot move: "
                    + string.Join(", ", grouping.StatusWelds.ToArray()));
            if (grouping.SubStatusWelds.Count > 0)
                AddIn.Log("grounding: " + grouping.SubStatusWelds.Count + " component(s) "
                    + "welded to their subassembly because SolidWorks says they cannot "
                    + "move in it: " + string.Join(", ", grouping.SubStatusWelds.ToArray()));
            foreach (string path in grouping.MergedAwayDofs)
                AddIn.Log("  WARNING SolidWorks says this can move, but it is "
                    + "welded to the ground: " + path);
            foreach (string mate in grouping.UnreadMultiMates)
                AddIn.Log("  WARNING mate " + mate + " ties three or more moving "
                    + "bodies together, and the rig cannot use it");

            // BuildResult unions every fixed component into one root, so this
            // cannot fire: it is here because the alternative failure is
            // silent in the add-in and cryptic in Blender ("child gNNN is
            // grounded; the exporter's spanning tree roots at grounded groups").
            if (grounded > 1)
                AddIn.Log("grounding WARNING: " + grounded + " grounded groups; a "
                    + "joint landing on any but the first will be rejected by the "
                    + "consumer");
        }

        // ── The solver's verdict on each pair ───────────────────────────────

        /// <summary>One probe reading, keyed by the two REPRESENTATIVE
        /// components rather than by group id, because the grouping is rebuilt
        /// once the verdicts are in and the ids do not survive that.</summary>
        private sealed class PairVerdict
        {
            public string GroupA;
            public string GroupB;
            public string ComponentA;
            public string ComponentB;
            public string Type = JointType.Free;
            public double[] Axis;
            public double[] Origin;
            public string RawStatuses;

            /// <summary>A mate spans this pair that the probe cannot neutralise:
            /// a path, a free slot, a coupling. SolidWorks counts several of
            /// those as constraints, so a "fixed" reading here would weld a
            /// mechanism shut; the verdict is kept for the log but never merges
            /// and never overrides.</summary>
            public bool ProbeBlind;

            /// <summary>Every slot came back Unused: the solver found
            /// nothing this body can do, which is the only reading that may
            /// merge a pair.</summary>
            public bool Weldable;

            /// <summary>Every freedom the solver reported, it also NAMED:
            /// a Static point AND a Static direction. Anything less is a
            /// reading that cannot overrule the mate analysis: a ball comes
            /// back with a Static centre and no particular axis, which is
            /// indistinguishable from a hinge without this.</summary>
            public bool Characterised;
        }

        /// <summary>
        /// Probes every connected pair of rigid groups. This runs BEFORE the
        /// classifier, because the solver's verdict decides two things the
        /// classifier would otherwise decide alone: whether the pair is one
        /// body at all, and which primitive it is.
        /// </summary>
        private static List<PairVerdict> ProbePairs(
            IModelDoc2 model, List<WalkedComponent> walked,
            RigidGroupingResult grouping, ExportProgress progress = null)
        {
            progress = progress ?? ExportProgress.None;
            var results = new List<PairVerdict>();
            if (grouping.Edges.Count == 0) return results;

            var byId = new Dictionary<string, WalkedComponent>();
            foreach (var w in walked)
                if (w.Comp != null) byId[w.Id] = w;

            // One representative per group NAMES the group in a verdict and
            // is the component read for a child. The full member list is what
            // gets FIXED for a parent: a rigid group is one body, and fixing a
            // single member of it leaves the rest of that body free to drift.
            var groupRep = new Dictionary<string, WalkedComponent>();
            var groupBody = new Dictionary<string, List<Component2>>();
            foreach (var g in grouping.Groups)
            {
                var body = new List<Component2>();
                foreach (var cid in g.Components)
                {
                    WalkedComponent w;
                    if (!byId.TryGetValue(cid, out w)) continue;
                    if (!groupRep.ContainsKey(g.Id)) groupRep[g.Id] = w;
                    body.Add(w.Comp);
                }
                groupBody[g.Id] = body;
            }

            // Batched by parent: fixing and unfixing the parent chain solves
            // the whole assembly, so every pair sharing a parent shares one
            // solve. Batches follow first appearance and pairs follow edge
            // order, so a re-run reads the same.
            var batchOrder = new List<string>();
            var batches = new Dictionary<string, List<GroupEdge>>();
            foreach (var edge in grouping.Edges)
            {
                WalkedComponent p, c;
                if (!groupRep.TryGetValue(edge.GroupA, out p)
                    || !groupRep.TryGetValue(edge.GroupB, out c))
                {
                    AddIn.Log("DOF probe skipped " + edge.GroupA + "/"
                        + edge.GroupB + ": one side owns no component to fix"
                        + " (a carrier, or an assembly-geometry ground), so"
                        + " no reading can be taken against it");
                    continue;
                }
                // The solver reads a flexible sub's internal pair through the
                // top document as fixed (live corpus 07, 2026-08-23: the
                // in-sub revolute probed [R1=0 R2=0 L1=0 L2=0 remaining=0]),
                // in-sub limit mates cannot be suppressed through top-context
                // handles, so the probe has no valid reading there and the
                // mate analysis stands alone.
                if (p.Parent != null || c.Parent != null)
                {
                    AddIn.Log("DOF probe skipped " + edge.GroupA + "/" + edge.GroupB
                        + ": pair lives inside a flexible subassembly");
                    continue;
                }
                List<GroupEdge> batch;
                if (!batches.TryGetValue(edge.GroupA, out batch))
                {
                    batch = new List<GroupEdge>();
                    batches[edge.GroupA] = batch;
                    batchOrder.Add(edge.GroupA);
                }
                batch.Add(edge);
            }

            var probe = new DofProbe(model, AddIn.Log);
            var started = DateTime.UtcNow;
            int probed = 0;
            int pairs = 0;
            foreach (var kv in batches) pairs += kv.Value.Count;
            progress.Stage("Measuring the freedom of " + pairs + " pair(s)", 24, 58, pairs);
            // Every limit mate in the assembly is already out for the whole
            // session (TakeLimitsOut), inside flexible subassemblies too:
            // the solver counts a limit as a fixed dimension wherever it
            // sits, and one in a closed loop through the child reads the
            // child rigid against ANY parent (live TongRig, 2026-09-14: both
            // arms welded to the base by the stroke limit on the cylinder
            // between them).
            {
                foreach (string parentGroup in batchOrder)
                {
                    var batch = batches[parentGroup];
                    var kids = new List<Component2>();
                    foreach (var edge in batch) kids.Add(groupRep[edge.GroupB].Comp);
                    var got = probe.ProbeAgainst(groupBody[parentGroup], kids);
                    for (int i = 0; i < batch.Count && i < got.Count; i++)
                    {
                        var v = new PairVerdict
                        {
                            GroupA = batch[i].GroupA,
                            GroupB = batch[i].GroupB,
                            ComponentA = groupRep[batch[i].GroupA].Id,
                            ComponentB = groupRep[batch[i].GroupB].Id,
                            Type = got[i].Type,
                            Axis = got[i].Axis,
                            Origin = got[i].Origin,
                            RawStatuses = got[i].RawStatuses,
                            ProbeBlind = ProbeIsBlind(batch[i])
                                      || CoupledElsewhere(grouping, batch[i]),
                            Weldable = got[i].Weldable,
                            Characterised = got[i].Characterised,
                        };
                        results.Add(v);
                        AddIn.Log("DOF probe " + batch[i].GroupA + "/" + batch[i].GroupB
                            + ": solver says " + v.Type + " [" + v.RawStatuses + "]"
                            + (v.ProbeBlind
                                ? " (advisory: a mate here is one the probe cannot neutralise)"
                                : ""));
                    }
                    probed += batch.Count;
                    progress.Step(probed);
                }
            }
            if (probed > 0)
                AddIn.Log("DOF probe: " + probed + " pair(s) in " + batchOrder.Count
                    + " parent batch(es), "
                    + ((int)(DateTime.UtcNow - started).TotalSeconds) + " s");
            return results;
        }

        /// <summary>True when a mate on this pair permits motion the probe
        /// cannot see past. The probe suppresses limit mates before reading,
        /// but it does nothing about a path, a free slot or a coupling, and
        /// SolidWorks' own DOF accounting counts several of those as
        /// constraints, so its answer would be "fixed" for something that
        /// plainly moves.</summary>
        private static bool ProbeIsBlind(GroupEdge edge)
        {
            foreach (var m in edge.Mates)
                if (MateFacts.PermitsMotion(m) && !MateFacts.IsLimitMate(m))
                    return true;
            return false;
        }

        /// <summary>True when a coupling mate ties the CHILD of this pair to
        /// a third body. The solver counts the tie as a constraint on the
        /// child, so the reading says how that third body is held, not how
        /// the child is mated to its parent. Live universal joint sample
        /// (2026-09-15): the female yoke read fixed to the bracket with the
        /// joint mate in place and revolute with it suppressed, and the
        /// chain of fixed verdicts it seeded welded the yoke to ground.</summary>
        private static bool CoupledElsewhere(RigidGroupingResult grouping, GroupEdge pair)
        {
            foreach (var edge in grouping.Edges)
            {
                if (ReferenceEquals(edge, pair)) continue;
                if (edge.GroupA != pair.GroupB && edge.GroupB != pair.GroupB) continue;
                foreach (var m in edge.Mates)
                    if (!m.Suppressed && MateFacts.IsCoupling(m)) return true;
            }
            return false;
        }

        /// <summary>
        /// The solver's verdict, applied to the classified joints. Where both
        /// name a primitive and they differ, the solver wins outright; where
        /// the solver names something it cannot really have seen: a screw
        /// read as a cylindrical, a path pair read as fixed: the mate
        /// analysis stands and the disagreement becomes a warning.
        /// </summary>
        private static void ApplySolverVerdicts(
            RigidGroupingResult grouping, ClassificationResult classification,
            List<PairVerdict> verdicts, string groundGroup)
        {
            if (verdicts.Count == 0) return;

            var groupOf = grouping.ComponentGroup;
            foreach (var j in classification.Joints)
            {
                PairVerdict verdict = null;
                foreach (var v in verdicts)
                {
                    string ga, gb;
                    if (!groupOf.TryGetValue(v.ComponentA, out ga)) continue;
                    if (!groupOf.TryGetValue(v.ComponentB, out gb)) continue;
                    if ((ga == j.ParentGroup && gb == j.ChildGroup)
                        || (ga == j.ChildGroup && gb == j.ParentGroup))
                    {
                        verdict = v;
                        break;
                    }
                }
                if (verdict == null) continue;

                bool adoptable = j.Coupling == null && VerdictMayOverrule(
                    j, verdict.Type, verdict.Characterised, verdict.ProbeBlind);
                if (adoptable && verdict.Type != j.Type
                    && PinnedReadingIsLoopLocked(classification.Joints, j,
                                                 verdict.GroupA, groundGroup))
                {
                    // Not a disagreement either: the reading is an artefact
                    // of the pinning, the same one the weld gate discounts.
                    AddIn.Log("DOF probe " + j.Id + ": reads " + verdict.Type
                        + ", but the probe had to FIX " + verdict.GroupA
                        + " to read a pair that sits on a loop, which freezes the "
                        + "loop; the reading is the loop's, not the joint's, and "
                        + "the mate analysis stands");
                    continue;
                }

                if (verdict.Type == j.Type)
                {
                    // Same type: the axes must be the same line where both
                    // exist, or one of the two is measuring something else.
                    if (adoptable && j.Axis != null && verdict.Axis != null
                        && Math.Abs(MathOps.Dot(j.Axis, verdict.Axis)) < 0.999)
                        Disagree(classification, j, verdict,
                            "the axes are not the same line");
                    continue;
                }

                if (adoptable)
                {
                    string note = JointClassifier.AdoptSolverVerdict(
                        j, verdict.Type, verdict.Axis, verdict.Origin);
                    j.Notes = string.IsNullOrEmpty(j.Notes) ? note : j.Notes + " " + note;
                    AddIn.Log("DOF probe: " + j.Id + " adopted the solver's " + verdict.Type);
                    continue;
                }

                if (!DisagreementIsWorthReporting(j, verdict.Type,
                                                  verdict.Characterised))
                {
                    if (verdict.Type == JointType.Fixed)
                        AddIn.Log("DOF probe " + j.Id + ": reads rigid ["
                            + verdict.RawStatuses + "], which was refused as "
                            + "a weld, so it is not reported against the joint");
                    continue;
                }

                Disagree(classification, j, verdict, null);
            }
        }

        /// <summary>
        /// Whether the solver's reading may replace what the mates said.
        ///
        /// Two conditions beyond the obvious. It must have NAMED what it saw:
        /// every freedom reported with a Static point and a Static
        /// direction, because an unnamed reading names a type it did not
        /// really see (live 2026-08-25: a puck flat on a plate reads
        /// "prismatic" off a Free rotation and one Static slide).
        ///
        /// And a BALL is never overruled. GetRemainingDOFs has two rotation
        /// slots; a ball has three rotations, so no reading it can produce
        /// tells a ball from a hinge. Live corpus 04 (2026-08-25): all three
        /// ball assemblies read revolute and were adopted, and the studs
        /// stopped tumbling. A limit of the API, not a judgement about the
        /// geometry.
        /// </summary>
        /// <summary>
        /// Whether a probe reading of this joint was taken with the loop it
        /// sits on frozen. The probe pins one body of the pair (GroupA) and
        /// reads the other's freedom. Pinning the ground changes nothing.
        /// Pinning a moving body of a pair that lies on a LOOP removes the
        /// mechanism's freedom: a one-degree loop with one more body held is
        /// rigid, and whatever slop is left (a con-rod sliding along its own
        /// parallel pins) reads as the pair's joint type. Welds from such
        /// readings were always refused; narrowings must be too. Live
        /// claw-mechanism.sldasm (2026-09-15): the con-rod's two pins read
        /// prismatic with the collar pinned, the ring then welded the
        /// collar's slide, and the claw shipped solid. A pair that is a
        /// bridge of the joint graph (no loop through it) keeps its own
        /// freedom whatever is pinned, so its reading stands.
        /// </summary>
        public static bool PinnedReadingIsLoopLocked(
            IList<RigJoint> joints, RigJoint joint, string pinnedGroup, string groundGroup)
        {
            if (joint == null) return false;
            if (pinnedGroup == null || pinnedGroup == groundGroup) return false;
            // Is the child still reachable from the parent without this
            // joint? Then the joint is on a loop.
            var adjacency = new Dictionary<string, List<RigJoint>>();
            foreach (var j in joints)
            {
                if (ReferenceEquals(j, joint) || j.Type == JointType.Free) continue;
                if (!adjacency.ContainsKey(j.ParentGroup)) adjacency[j.ParentGroup] = new List<RigJoint>();
                if (!adjacency.ContainsKey(j.ChildGroup)) adjacency[j.ChildGroup] = new List<RigJoint>();
                adjacency[j.ParentGroup].Add(j);
                adjacency[j.ChildGroup].Add(j);
            }
            var seen = new HashSet<string> { joint.ParentGroup };
            var queue = new Queue<string>();
            queue.Enqueue(joint.ParentGroup);
            while (queue.Count > 0)
            {
                string g = queue.Dequeue();
                if (g == joint.ChildGroup) return true;
                List<RigJoint> edges;
                if (!adjacency.TryGetValue(g, out edges)) continue;
                foreach (var e in edges)
                {
                    string other = e.ParentGroup == g ? e.ChildGroup : e.ParentGroup;
                    if (seen.Add(other)) queue.Enqueue(other);
                }
            }
            return false;
        }

        public static bool VerdictMayOverrule(
            RigJoint joint, string verdictType, bool characterised, bool blind)
        {
            if (joint == null || blind || !characterised) return false;
            if (joint.Type == JointType.Ball) return false;
            return JointClassifier.IsSolverPrimitive(joint.Type)
                && JointClassifier.IsSolverPrimitive(verdictType);
        }

        /// <summary>
        /// Whether a verdict that differs from the mate analysis is worth
        /// putting in the manifest as a PROBE_DISAGREES warning.
        ///
        /// A warning here is a claim that the MATE ANALYSIS is suspect, so it
        /// must not be raised when it is the reading that is:
        ///
        ///   * an unnamed reading named a type it did not really see;
        ///   * a ball cannot be seen at all through two rotation slots;
        ///   * a screw reads as any 1-DOF verdict once its coupling is
        ///     invisible, which is agreement rather than disagreement;
        ///   * and a RIGID verdict can only reach this point by having been
        ///     refused as a weld: a trusted one merges the pair, and then no
        ///     joint spans it to disagree with. Every one that arrives is the
        ///     artefact the ground-parent rule exists to discount: the probe
        ///     pinned a moving body and the mechanism froze. Live corpus 06
        ///     (2026-08-25) would otherwise stamp two of the four-bar's four
        ///     correct revolutes "low confidence: the solver reads this pair
        ///     as fixed", against a README that asks for no warnings at all.
        /// </summary>
        public static bool DisagreementIsWorthReporting(
            RigJoint joint, string verdictType, bool characterised)
        {
            if (joint == null || !characterised) return false;
            if (joint.Type == verdictType) return false;
            if (joint.Type == JointType.Fixed || joint.Type == JointType.Free) return false;
            if (joint.Type == JointType.Ball) return false;
            if (verdictType == JointType.Fixed) return false;
            if (joint.Type == JointType.Screw
                && (verdictType == JointType.Cylindrical
                    || verdictType == JointType.Revolute
                    || verdictType == JointType.Prismatic))
                return false;
            return true;
        }

        private static void Disagree(
            ClassificationResult classification, RigJoint j, PairVerdict verdict,
            string detail)
        {
            j.Confidence = "low";
            string note = "The SolidWorks solver reads this pair as "
                + verdict.Type + "; the mate analysis said " + j.Type
                + (detail == null ? "" : " (" + detail + ")")
                + ". The mate analysis is exported.";
            j.Notes = string.IsNullOrEmpty(j.Notes) ? note : j.Notes + " " + note;

            var w = new ManifestWarning();
            w.Code = "PROBE_DISAGREES";
            w.Joints.Add(j.Id);
            w.Message = "Joint " + j.Id + ": the mate analysis classified "
                + j.Type + " but the SolidWorks DOF probe reports "
                + verdict.Type + " (" + verdict.RawStatuses + "), and the "
                + "verdict could not be adopted"
                + (detail == null ? "" : ": " + detail)
                + ". The mate analysis is exported; check this joint first when "
                + "the rig moves wrong.";
            classification.Warnings.Add(w);
        }

        // ── Manifest assembly ───────────────────────────────────────────────

        private static RigManifest BuildManifest(
            ISldWorks app, IModelDoc2 model, List<WalkedComponent> walked,
            RigidGroupingResult grouping, List<RigidGroup> allGroups,
            ClassificationResult classification,
            LoopAnalysisResult loops, MatchResult matches,
            string stepFileName, int ap, string sha1,
            Appearance.FlexFixOutcome flexFix)
        {
            var manifest = new RigManifest();

            manifest.Generator.Version = AddIn.AddInVersion;
            try { manifest.Generator.SolidWorksVersion = app.RevisionNumber(); } catch { }
            manifest.Generator.ExportedUtc =
                DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",
                    System.Globalization.CultureInfo.InvariantCulture);

            manifest.StepExport.File = stepFileName;
            manifest.StepExport.Ap = ap == 203 ? "AP203" : "AP214";
            manifest.StepExport.Sha1 = sha1;
            manifest.StepExport.OccurrenceMatching = OccurrenceMatcher.MatcherId;

            var unmatched = new List<string>();
            var suppressed = new List<string>();
            foreach (var w in walked)
            {
                var g = w.Graph;
                var c = new ManifestComponent();
                c.Id = w.Id;
                c.SwPath = g.Path;
                c.SwPersistentId = ComponentIdentity.PersistIdBase64(model, w.Comp);
                c.Transform = g.Transform;
                c.BboxMin = g.BboxMin;
                c.BboxMax = g.BboxMax;
                c.Suppressed = g.Suppressed;
                c.SubassemblySolving = g.Solving;

                OccurrenceMatch match;
                if (matches.ByComponentId.TryGetValue(w.Id, out match))
                {
                    c.StepName = match.StepName;
                    c.StepOccurrencePath = match.StepPath;
                }
                else
                {
                    // step_name is required by the schema; the prediction
                    // below is the name SolidWorks conventionally writes.
                    // step_occurrence_path stays null on purpose: an honest
                    // null beats a guessed path that styles the wrong body.
                    c.StepName = w.DocName ?? g.Name;
                    c.StepOccurrencePath = null;
                    if (g.Suppressed) suppressed.Add(w.Id);
                    else unmatched.Add(w.Id);
                }
                manifest.Components.Add(c);
            }

            manifest.RigidGroups.AddRange(allGroups);
            manifest.Joints.AddRange(loops.Joints);
            manifest.Loops.AddRange(loops.Loops);
            manifest.Mechanisms.AddRange(loops.Mechanisms);
            foreach (var note in loops.Notes) AddIn.Log("mechanism: " + note);

            // Loop analysis can retire a classifier warning: a free joint
            // whose rotation lock became a coupling IS modelled, and one that
            // duplicates a declared loop's constraint is redundant, not
            // under-defined.
            foreach (var w in classification.Warnings)
            {
                if (w.Code == "UNDER_DEFINED")
                {
                    if (Overlaps(w.Joints, loops.CoupledFreeJointIds)) continue;
                    if (Overlaps(w.Joints, loops.RedundantFreeJointIds))
                    {
                        w.Code = "REDUNDANT_MATE";
                        w.Message = "The mates behind this free joint duplicate a "
                            + "constraint a declared loop already enforces; they are "
                            + "redundant in SolidWorks too and add nothing to the rig.";
                    }
                }
                manifest.Warnings.Add(w);
            }
            if (suppressed.Count > 0)
            {
                var w = new ManifestWarning();
                w.Code = "SUPPRESSED_SKIPPED";
                w.Components.AddRange(suppressed);
                w.Message = suppressed.Count + " suppressed component(s) are listed but "
                    + "belong to no rigid group and are absent from the STEP file.";
                manifest.Warnings.Add(w);
            }
            // No STEP file, no occurrences to match: a mesh-only export
            // carries its component ids in the .swmesh, so every component
            // is "unmatched" here by construction and the warning would
            // only say so (live cam-follower, 2026-09-15).
            if (unmatched.Count > 0 && !string.IsNullOrEmpty(sha1))
            {
                var w = new ManifestWarning();
                w.Code = "OCCURRENCE_UNMATCHED";
                w.Components.AddRange(unmatched);
                w.Message = unmatched.Count + " component(s) could not be matched to a STEP "
                    + "occurrence; their step_occurrence_path is null and the Blender side "
                    + "falls back to transform matching.";
                manifest.Warnings.Add(w);
            }
            AddIslandWarnings(manifest, grouping);
            AddSharedFlexibleWarnings(manifest, walked, flexFix);

            return manifest;
        }

        /// <summary>
        /// SHARED_FLEXIBLE_GEOMETRY: a STEP file stores ONE internal layout
        /// per subassembly document, so when a flexible instance is posed
        /// away from the document's saved positions AND the same document is
        /// inserted again, only one of those instances can import with its
        /// geometry where SolidWorks shows it (live corpus 07 flexible-sub2,
        /// 2026-08-22: the rigid twin imported at the flexible twin's angle).
        ///
        /// FlexiblePoseFixer now de-instances the definition in the STEP and
        /// reposes every instance: when that succeeded the manifest carries
        /// FLEXIBLE_DEINSTANCED (informational) instead. The warning survives
        /// only where the fix could not apply, and then names the two cures:
        /// the automatic one on import (CADder's Snap to SW Poses) and
        /// the manual one (Save As a separate document).
        /// </summary>
        private static void AddSharedFlexibleWarnings(
            RigManifest manifest, List<WalkedComponent> walked,
            Appearance.FlexFixOutcome flexFix)
        {
            foreach (var w in walked)
            {
                var g = w.Graph;
                if (g.Solving != "flexible" || g.Suppressed || g.FileName == null) continue;
                bool flexed = false;
                foreach (var child in w.Children)
                    if (child.Graph.MatePoseDelta != null) { flexed = true; break; }
                if (!flexed) continue;

                var twins = new List<string>();
                foreach (var other in walked)
                {
                    if (other == w || other.Graph.Suppressed) continue;
                    if (!string.Equals(other.Graph.FileName, g.FileName,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    twins.Add(other.Graph.Path);
                }
                if (twins.Count == 0) continue;

                bool allFixed = flexFix != null
                    && flexFix.FixedPaths.Contains(g.Path);
                if (allFixed)
                    foreach (var t in twins)
                        if (!flexFix.FixedPaths.Contains(t)) { allFixed = false; break; }

                var warning = new ManifestWarning();
                warning.Components.Add(w.Id);
                if (allFixed)
                {
                    warning.Code = "FLEXIBLE_DEINSTANCED";
                    warning.Message = "Flexible subassembly " + g.Path + " shares its "
                        + "document with " + string.Join(", ", twins.ToArray()) + "; the "
                        + "STEP file was de-instanced and every instance's internal "
                        + "layout matches SolidWorks. No action needed.";
                }
                else
                {
                    warning.Code = "SHARED_FLEXIBLE_GEOMETRY";
                    warning.Message = "Flexible subassembly " + g.Path + " is posed away from "
                        + "its document's saved positions, and the same document is also "
                        + "inserted as " + string.Join(", ", twins.ToArray()) + ". STEP stores "
                        + "one internal layout per document and the automatic de-instancing "
                        + "could not apply here, so the geometry of those other instance(s) "
                        + "will import at the shared pose. CADder's Snap to SW Poses "
                        + "corrects it after import; the manual cure is Save As a separate "
                        + "document.";
                }
                manifest.Warnings.Add(warning);
            }
        }

        private static bool Overlaps(List<string> ids, List<string> against)
        {
            foreach (var id in ids)
                if (against.Contains(id)) return true;
            return false;
        }

        /// <summary>
        /// DISCONNECTED_ISLAND: rigid groups that no chain of joints connects
        /// to a grounded group. The Blender side leaves them unparented, and
        /// the user deserves to hear it from the exporter rather than notice
        /// a floating bone.
        /// </summary>
        private static void AddIslandWarnings(RigManifest manifest, RigidGroupingResult grouping)
        {
            var groups = manifest.RigidGroups;
            if (groups.Count == 0) return;

            var index = new Dictionary<string, int>();
            for (int i = 0; i < groups.Count; i++) index[groups[i].Id] = i;

            var adjacency = new List<int>[groups.Count];
            for (int i = 0; i < adjacency.Length; i++) adjacency[i] = new List<int>();
            foreach (var edge in grouping.Edges)
            {
                int a, b;
                if (!index.TryGetValue(edge.GroupA, out a)) continue;
                if (!index.TryGetValue(edge.GroupB, out b)) continue;
                adjacency[a].Add(b);
                adjacency[b].Add(a);
            }
            // Synthesized joints (carrier chains, mirror pairs) connect
            // groups no mate-level edge covers: the manifest's own joint
            // list is the other half of the connectivity truth.
            foreach (var j in manifest.Joints)
            {
                int a, b;
                if (j.ParentGroup == null || j.ChildGroup == null) continue;
                if (!index.TryGetValue(j.ParentGroup, out a)) continue;
                if (!index.TryGetValue(j.ChildGroup, out b)) continue;
                adjacency[a].Add(b);
                adjacency[b].Add(a);
            }

            var reached = new bool[groups.Count];
            var queue = new Queue<int>();
            bool anyGrounded = false;
            for (int i = 0; i < groups.Count; i++)
            {
                if (!groups[i].Grounded) continue;
                anyGrounded = true;
                reached[i] = true;
                queue.Enqueue(i);
            }
            while (queue.Count > 0)
            {
                int g = queue.Dequeue();
                foreach (int other in adjacency[g])
                {
                    if (reached[other]) continue;
                    reached[other] = true;
                    queue.Enqueue(other);
                }
            }

            if (!anyGrounded)
            {
                var w = new ManifestWarning();
                w.Code = "DISCONNECTED_ISLAND";
                w.Message = "No component is fixed, so no rigid group is grounded and the "
                    + "armature has no root. Fix one component and re-export.";
                manifest.Warnings.Add(w);
                return;
            }

            var stranded = new List<string>();
            for (int i = 0; i < groups.Count; i++)
            {
                if (reached[i]) continue;
                stranded.AddRange(groups[i].Components);
            }
            if (stranded.Count > 0)
            {
                var w = new ManifestWarning();
                w.Code = "DISCONNECTED_ISLAND";
                w.Components.AddRange(stranded);
                w.Message = "Some rigid groups have no joint path to a grounded group; "
                    + "their bones will be left unparented.";
                manifest.Warnings.Add(w);
            }
        }
    }
}
