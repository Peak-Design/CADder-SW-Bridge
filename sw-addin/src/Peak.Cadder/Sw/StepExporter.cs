using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.Cadder.Sw
{
    public sealed class StepExportResult
    {
        /// <summary>SHA-1 of the written file, lowercase hex. The manifest
        /// carries it so the Blender side can refuse a stale STEP/manifest
        /// pair.</summary>
        public string Sha1;
    }

    /// <summary>
    /// The STEP export. Pattern from Peak-Release's StepConverter: the
    /// application protocol lives on ISldWorks as an APPLICATION-WIDE
    /// preference, not on the document, so it is saved before the export and
    /// restored in a finally block: the user's own STEP settings must
    /// survive an export, including a failing one.
    /// </summary>
    public static class StepExporter
    {
        /// <summary>Ap is 203 or 214. Throws on failure, with the SaveAs3
        /// error and warning codes in the message and the log. SaveAs3
        /// reports failure as a bool plus two opaque ints, and the codes are
        /// the only clue SolidWorks gives.</summary>
        /// <summary>swStepExportAppearances = 787. SW2024's swconst has the
        /// symbol, SW2022's (which this compiles against) does not, so the raw
        /// id is used and every access is guarded.</summary>
        private const int ExportAppearancesToggle = 787;

        public static StepExportResult Export(
            ISldWorks app, IModelDoc2 model, string targetPath, int ap, Action<string> log,
            bool exportAppearances = false, bool includeHidden = false,
            HashSet<string> keep = null)
        {
            int savedAp = app.GetUserPreferenceIntegerValue(
                (int)swUserPreferenceIntegerValue_e.swStepAP);
            bool savedAppearances = false, appearancesAvailable = exportAppearances;
            if (exportAppearances)
            {
                try { savedAppearances = app.GetUserPreferenceToggle(ExportAppearancesToggle); }
                catch { appearancesAvailable = false; }
            }

            bool ok;
            int errors = 0, warnings = 0;
            var restore = new List<KeyValuePair<IComponent2, int>>();
            try
            {
                app.SetUserPreferenceIntegerValue(
                    (int)swUserPreferenceIntegerValue_e.swStepAP, ap == 203 ? 203 : 214);
                if (appearancesAvailable)
                    app.SetUserPreferenceToggle(ExportAppearancesToggle, true);
                restore.AddRange(ApplyVisibility(model, includeHidden, keep, log));

                ok = model.Extension.SaveAs3(
                    targetPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null,
                    ref errors, ref warnings);
            }
            finally
            {
                RestoreVisibility(restore, log);
                try
                {
                    app.SetUserPreferenceIntegerValue(
                        (int)swUserPreferenceIntegerValue_e.swStepAP, savedAp);
                }
                catch (Exception ex)
                {
                    if (log != null) log("restore swStepAP: " + ex.Message);
                }
                if (appearancesAvailable)
                {
                    try { app.SetUserPreferenceToggle(ExportAppearancesToggle, savedAppearances); }
                    catch (Exception ex)
                    {
                        if (log != null) log("restore appearances toggle: " + ex.Message);
                    }
                }
            }

            if (!ok || !File.Exists(targetPath))
            {
                string message = "SolidWorks STEP export failed (AP" + ap
                    + ", errors=" + errors + ", warnings=" + warnings + ")";
                if (log != null) log(message);
                throw new InvalidOperationException(message);
            }
            if (warnings != 0 && log != null)
                log("STEP export completed with warnings=" + warnings);

            var result = new StepExportResult();
            result.Sha1 = Sha1Hex(targetPath);
            return result;
        }

        /// <summary>
        /// Shows hidden components for the export and returns the changed
        /// ones. A silent SaveAs3 leaves hidden components out and the API
        /// offers no preference for it, so visibility is the lever. Visibility
        /// and not suppression, on purpose: resolving a suppressed component
        /// rebuilds the assembly, which can disturb mates and in-context
        /// features; a visibility flip reverses exactly. (NEXT-STEP's hidden
        /// probe, vendored knowledge.)
        /// </summary>
        /// <summary>
        /// Puts every component into the visibility this export needs, and
        /// returns what it changed so the caller can put it back.
        ///
        /// One pass, not two. Revealing hidden components and hiding the ones
        /// outside a selection act on the same property, and a component that
        /// is both is touched by both: run as separate passes with separate
        /// undo lists, the order the restores happen in decides whether the
        /// user gets their assembly back.
        ///
        /// A silent SaveAs3 leaves out hidden components and the API has no
        /// preference to change it, so visibility is the only lever there is.
        /// Suppression is never touched: resolving a suppressed component
        /// rebuilds the assembly and can disturb mates and in-context
        /// features, where showing a hidden one changes only the display and
        /// reverses exactly.
        /// </summary>
        private static List<KeyValuePair<IComponent2, int>> ApplyVisibility(
            IModelDoc2 model, bool includeHidden, HashSet<string> keep,
            Action<string> log)
        {
            var changed = new List<KeyValuePair<IComponent2, int>>();
            if (!(model is IAssemblyDoc assy)) return changed;
            if (!includeHidden && keep == null) return changed;

            int hiddenState = (int)swComponentVisibilityState_e.swComponentHidden;
            int visibleState = (int)swComponentVisibilityState_e.swComponentVisible;
            int revealed = 0, trimmed = 0;

            foreach (var o in assy.GetComponents(false) as object[] ?? new object[0])
            {
                var comp = o as IComponent2;
                if (comp == null) continue;
                try
                {
                    if (comp.GetSuppression2() == (int)swComponentSuppressionState_e.swComponentSuppressed)
                        continue;

                    int was = comp.Visible;
                    int want = was;
                    if (keep != null && !keep.Contains(comp.Name2 ?? ""))
                        want = hiddenState;
                    else if (includeHidden)
                        want = visibleState;

                    if (want == was) continue;
                    comp.Visible = want;
                    changed.Add(new KeyValuePair<IComponent2, int>(comp, was));
                    if (want == visibleState) revealed++; else trimmed++;
                }
                catch (Exception ex) { log?.Invoke("visibility " + comp.Name2 + ": " + ex.Message); }
            }

            if (revealed > 0)
                log?.Invoke("    revealed " + revealed + " hidden component(s) for the export");
            if (trimmed > 0)
                log?.Invoke("    hid " + trimmed + " component(s) outside the selection");
            return changed;
        }

        /// <summary>
        /// Puts back every visibility ApplyVisibility changed. Runs in a
        /// finally: to leave an assembly showing components the user had
        /// hidden, or missing the ones the export trimmed, is a worse defect
        /// than any this add-in repairs.
        /// </summary>
        private static void RestoreVisibility(
            List<KeyValuePair<IComponent2, int>> changed, Action<string> log)
        {
            foreach (var entry in changed)
            {
                try { entry.Key.Visible = entry.Value; }
                catch (Exception ex) { log?.Invoke("restore visibility: " + ex.Message); }
            }
        }

        /// <summary>Public so the manifest can carry the hash of the FINAL
        /// file: the appearance pipeline rewrites the STEP after SaveAs3.</summary>
        public static string Sha1Hex(string path)
        {
            using (var sha = SHA1.Create())
            using (var stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
