using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using Peak.SwToBlender.Core;
using Peak.SwToBlender.Core.Model;
using Peak.SwToBlender.Sw;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.SwToBlender
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
    /// matches the file exactly — that is the whole contract.
    /// </summary>
    /// <summary>What ExportBundle produced, for the caller's reporting.</summary>
    public sealed class RigExportOutcome
    {
        public string Report;
        public string StepPath;
        public string ManifestPath;
        public int Warnings;
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

            try
            {
                // Every stage that mutates model state restores it in its own
                // finally block (StepExporter restores the STEP AP preference;
                // the DOF probe, when a later milestone enables it, restores
                // fix flags and mate suppression). This catch is the last
                // line of defence, not the restore path.
                var outcome = ExportBundle(app, model, assembly, stepPath, manifestPath, settings);
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
            catch (Exception ex)
            {
                AddIn.Log("export failed: " + ex);
                app.SendMsgToUser2("Export failed: " + ex.Message,
                    (int)swMessageBoxIcon_e.swMbStop,
                    (int)swMessageBoxBtn_e.swMbOk);
            }
        }

        /// <summary>
        /// Manifest-only re-export (Oscar, 2026-08-23: iterating the rig
        /// analysis on a large assembly must not pay for a STEP write every
        /// time). Same pipeline minus the STEP/appearance stages; occurrence
        /// matching runs against the EXISTING STEP beside the manifest when
        /// one is there. No options dialog — the saved settings drive the
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

            try
            {
                var outcome = ExportBundle(app, model, assembly,
                    ManifestStepPath(manifestPath), manifestPath, settings,
                    manifestOnly: true);
                app.SendMsgToUser2(outcome.Report,
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
        }

        /// <summary>The STEP file a manifest describes: the same base name
        /// beside it. Path.ChangeExtension alone would turn x.rig.json into
        /// x.rig.step — the .rig marker must go with the .json.</summary>
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
            bool manifestOnly = false)
        {
            int ap = settings.Ap == 203 ? 203 : 214;
            bool runDofProbe = settings.RunDofProbe;
            // The appearance entity forms are AP214's; AP203 carries no
            // appearances to repair.
            bool repairAppearances = settings.RepairAppearances && ap == 214;
            // ── 1. The WYSIWYG walk and the mate graph ──────────────────────
            var walked = AssemblyWalker.Walk(assembly, AddIn.Log);
            if (walked.Count == 0)
                throw new InvalidOperationException("The assembly has no components to export.");
            var graph = MateReader.Read(walked, AddIn.Log);

            // ── 1b. Errored mates abort the export ──────────────────────────
            // An errored or over-defining mate is one SolidWorks itself is
            // not solving faithfully — with an over-defined set, SolidWorks
            // picks which mate to ignore and the classifier cannot know
            // which. Exporting anyway would bake that guess into the rig, so
            // the user is sent to fix the assembly instead. Suppressed mates
            // are exempt: they are intentionally off and skipped everywhere.
            var mateErrors = new List<string>();
            foreach (var gm in graph.Mates)
                if (gm.Error != null && !gm.Suppressed)
                    mateErrors.Add(gm.FeatureName + " " + gm.Error);
            if (mateErrors.Count > 0)
            {
                const int shown = 10;
                var list = string.Join("\n  ",
                    mateErrors.GetRange(0, Math.Min(shown, mateErrors.Count)).ToArray());
                if (mateErrors.Count > shown)
                    list += "\n  ... and " + (mateErrors.Count - shown) + " more";
                throw new InvalidOperationException(
                    "The assembly has mate errors, so the exported kinematics "
                    + "would be wrong. Fix or delete these mates and re-export:\n  "
                    + list);
            }

            // ── 2. Groups, joints, loops — pure Core, no SolidWorks ─────────
            var grouping = RigidGrouper.Group(graph);
            // The limit-sign probe only fires when a limit rests at a
            // degenerate pose (dragged to its hard stop and exported — live
            // corpus 01, 2026-08-23: the hinge at its horizontal limit rigged
            // mirrored). It nudges one component and restores, so like the
            // DOF probe it runs BEFORE the STEP export.
            var signOracle = new LimitSignProbe(app, model, walked, grouping, AddIn.Log);
            var classification = JointClassifier.Classify(graph, grouping, signOracle);
            // Carrier links synthesized for tangent contacts are groups like
            // any other from here on: they join the loop graph and the
            // manifest, they just own no components.
            var allGroups = new List<RigidGroup>(grouping.Groups);
            allGroups.AddRange(classification.VirtualGroups);
            var loops = LoopAnalyzer.Analyze(allGroups, classification.Joints);
            // Symmetric couplings annotate the FINAL joint list and may
            // APPEND a mirror pair (two ground-rooted free joints for a
            // symmetric-only body pair), so they resolve before the manifest
            // is assembled — island detection must see those joints.
            var symWarnings = SymmetricCoupler.Resolve(graph, grouping, loops.Joints);

            // ── 2b. DOF probe cross-check (optional) ────────────────────────
            // The SolidWorks solver's own view of each pair's remaining
            // freedom, against the mate analysis. The probe mutates model
            // state (fix flags, limit-mate suppression) and restores it per
            // pair, so it runs BEFORE the STEP export: whatever the restore
            // missed would at least be visible in the exported geometry
            // rather than silently baked into a file exported earlier.
            if (runDofProbe)
                ProbeCrossCheck(model, walked, grouping, classification);

            // ── 3. The STEP file, its post-processing, then the manifest ────
            string sha1 = null;
            Appearance.AppearancePipelineResult post;
            if (manifestOnly)
            {
                post = new Appearance.AppearancePipelineResult();
                if (File.Exists(stepPath))
                {
                    sha1 = StepExporter.Sha1Hex(stepPath);
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
                var step = StepExporter.Export(app, model, stepPath, ap, AddIn.Log,
                    exportAppearances: repairAppearances,
                    includeHidden: settings.IncludeHidden);

                // Flexible-twin fix + appearance repair + materials, one parse,
                // one save. Errors here must not kill the export: the file as
                // SolidWorks wrote it is still usable.
                // NB: types under Appearance.* stay namespace-qualified here — a
                // using would make Part21 ambiguous against Sw.Part21.
                var flexRequests = FlexibleLayoutBuilder.Build(walked, AddIn.Log);
                try
                {
                    post = Appearance.AppearancePipeline.Run(model, stepPath,
                        repairAppearances, settings.DeInstance,
                        settings.EngineeringMaterial, settings.IncludeHidden,
                        flexRequests, AddIn.Log);
                }
                catch (Exception ex)
                {
                    AddIn.Log("STEP post-processing failed: " + ex);
                    post = new Appearance.AppearancePipelineResult();
                    post.Notes.Add("STEP post-processing failed (" + ex.Message
                        + "); the file is as SolidWorks wrote it.");
                }
                sha1 = post.FileModified ? StepExporter.Sha1Hex(stepPath) : step.Sha1;
            }

            MatchResult matches;
            if (File.Exists(stepPath))
            {
                try
                {
                    var matcher = new OccurrenceMatcher(new Part21(stepPath), AddIn.Log);
                    matches = matcher.Match(walked);
                }
                catch (Exception ex)
                {
                    // A manifest with null occurrence paths is degraded but
                    // usable — the Blender side falls back to transform
                    // matching. A dead export over a matcher bug is not.
                    AddIn.Log("occurrence matching failed: " + ex);
                    matches = new MatchResult();
                }
            }
            else
            {
                matches = new MatchResult();
            }

            var manifest = BuildManifest(
                app, model, walked, grouping, allGroups, classification, loops, matches,
                Path.GetFileName(stepPath), ap, sha1, post.FlexFix);
            manifest.Warnings.AddRange(symWarnings);

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
                + manifest.Warnings.Count + " warning(s) — see the manifest for details.";
            if (post.Notes.Count > 0)
                report += "\n\n" + string.Join("\n", post.Notes.ToArray());

            var outcome = new RigExportOutcome();
            outcome.Report = report;
            outcome.StepPath = stepPath;
            outcome.ManifestPath = manifestPath;
            outcome.Warnings = manifest.Warnings.Count;
            return outcome;
        }

        // ── DOF probe cross-check ───────────────────────────────────────────

        /// <summary>
        /// Joint types the probe can name, and what counts as agreement. A
        /// screw reads as any 1-DOF verdict (the probe cannot see the
        /// rot-slide coupling once its limit mates are suppressed); pin_slot
        /// and path have no probe vocabulary and are skipped, as are fixed
        /// and free joints (both already carry their own warnings).
        /// </summary>
        private static bool ProbeAgrees(RigJoint joint, ProbeVerdict verdict)
        {
            if (joint.Type == JointType.Screw)
                return verdict.Type == JointType.Cylindrical
                    || verdict.Type == JointType.Revolute
                    || verdict.Type == JointType.Prismatic;
            if (verdict.Type != joint.Type) return false;

            // Same type: the axes must be the same line where both exist.
            if (joint.Axis != null && verdict.Axis != null)
            {
                if (Math.Abs(MathOps.Dot(joint.Axis, verdict.Axis)) < 0.999) return false;
                if ((joint.Type == JointType.Revolute || joint.Type == JointType.Cylindrical)
                    && joint.Origin != null && verdict.Origin != null)
                {
                    var foot = MathOps.ClosestPointOnLineToPoint(
                        joint.Origin, verdict.Axis, verdict.Origin);
                    if (Math.Sqrt(MathOps.Distance2(joint.Origin, foot)) > 0.001) return false;
                }
            }
            return true;
        }

        private static void ProbeCrossCheck(
            IModelDoc2 model, List<WalkedComponent> walked,
            RigidGroupingResult grouping, ClassificationResult classification)
        {
            var byId = new Dictionary<string, WalkedComponent>();
            foreach (var w in walked)
                if (w.Comp != null) byId[w.Id] = w;

            var groupRep = new Dictionary<string, WalkedComponent>();
            foreach (var g in grouping.Groups)
                foreach (var cid in g.Components)
                {
                    WalkedComponent w;
                    if (byId.TryGetValue(cid, out w)) { groupRep[g.Id] = w; break; }
                }

            var probe = new DofProbe(model, AddIn.Log);
            foreach (var j in classification.Joints)
            {
                if (j.Type == JointType.Fixed || j.Type == JointType.Free
                    || j.Type == JointType.PinSlot || j.Type == JointType.Path)
                    continue;
                // A carrier chain shares one mate set across two joints; the
                // probe sees the PAIR's whole freedom and can confirm neither
                // half alone.
                WalkedComponent p, c;
                if (!groupRep.TryGetValue(j.ParentGroup, out p)
                    || !groupRep.TryGetValue(j.ChildGroup, out c))
                    continue;
                // The solver reads a flexible sub's internal pair through the
                // top document as fixed (live corpus 07, 2026-08-23: the
                // in-sub revolute probed [R1=0 R2=0 L1=0 L2=0 remaining=0]) —
                // in-sub limit mates cannot be suppressed through top-context
                // handles, so the probe has no valid reading there.
                if (p.Parent != null || c.Parent != null)
                {
                    AddIn.Log("DOF probe skipped " + j.Id
                        + ": pair lives inside a flexible subassembly");
                    continue;
                }

                var verdict = probe.Probe(p.Comp, c.Comp);
                AddIn.Log("DOF probe " + j.Id + " (" + j.Type + "): solver says "
                    + verdict.Type + " [" + verdict.RawStatuses + "]");
                if (ProbeAgrees(j, verdict)) continue;

                j.Confidence = "low";
                j.Notes = string.IsNullOrEmpty(j.Notes)
                    ? "" : j.Notes + " ";
                j.Notes += "The SolidWorks solver reads this pair as "
                    + verdict.Type + "; the mate analysis said " + j.Type
                    + ". The mate analysis is exported.";

                var w2 = new ManifestWarning();
                w2.Code = "PROBE_DISAGREES";
                w2.Joints.Add(j.Id);
                w2.Message = "Joint " + j.Id + ": the mate analysis classified "
                    + j.Type + " but the SolidWorks DOF probe reports "
                    + verdict.Type + " (" + verdict.RawStatuses + "). The mate "
                    + "analysis is exported; check this joint first when the rig "
                    + "moves wrong.";
                classification.Warnings.Add(w2);
            }
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
                c.IsFastener = MateFacts.IsFastener(g);
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
            if (unmatched.Count > 0)
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
        /// reposes every instance — when that succeeded the manifest carries
        /// FLEXIBLE_DEINSTANCED (informational) instead. The warning survives
        /// only where the fix could not apply, and then names the two cures:
        /// the automatic one on import (STEPper NEXT's Snap to SW Poses) and
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
                        + "will import at the shared pose. STEPper NEXT's Snap to SW Poses "
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
            // groups no mate-level edge covers — the manifest's own joint
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
