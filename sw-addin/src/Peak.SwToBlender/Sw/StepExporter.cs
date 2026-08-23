using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Peak.SwToBlender.Sw
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
    /// restored in a finally block — the user's own STEP settings must
    /// survive an export, including a failing one.
    /// </summary>
    public static class StepExporter
    {
        /// <summary>Ap is 203 or 214. Throws on failure, with the SaveAs3
        /// error and warning codes in the message and the log — SaveAs3
        /// reports failure as a bool plus two opaque ints, and the codes are
        /// the only clue SolidWorks gives.</summary>
        /// <summary>swStepExportAppearances = 787. SW2024's swconst has the
        /// symbol, SW2022's (which this compiles against) does not, so the raw
        /// id is used and every access is guarded.</summary>
        private const int ExportAppearancesToggle = 787;

        public static StepExportResult Export(
            ISldWorks app, IModelDoc2 model, string targetPath, int ap, Action<string> log,
            bool exportAppearances = false, bool includeHidden = false)
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
            var revealed = new List<IComponent2>();
            try
            {
                app.SetUserPreferenceIntegerValue(
                    (int)swUserPreferenceIntegerValue_e.swStepAP, ap == 203 ? 203 : 214);
                if (appearancesAvailable)
                    app.SetUserPreferenceToggle(ExportAppearancesToggle, true);
                if (includeHidden) revealed.AddRange(RevealHidden(model, log));

                ok = model.Extension.SaveAs3(
                    targetPath, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null,
                    ref errors, ref warnings);
            }
            finally
            {
                Rehide(revealed, log);
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
        private static List<IComponent2> RevealHidden(IModelDoc2 model, Action<string> log)
        {
            var changed = new List<IComponent2>();
            var assy = model as IAssemblyDoc;
            if (assy == null) return changed;

            foreach (var o in assy.GetComponents(false) as object[] ?? new object[0])
            {
                var comp = o as IComponent2;
                if (comp == null) continue;
                try
                {
                    if (comp.GetSuppression2()
                        == (int)swComponentSuppressionState_e.swComponentSuppressed)
                        continue;
                    if (comp.Visible != (int)swComponentVisibilityState_e.swComponentHidden)
                        continue;
                    comp.Visible = (int)swComponentVisibilityState_e.swComponentVisible;
                    changed.Add(comp);
                }
                catch (Exception ex)
                {
                    if (log != null) log("reveal " + comp.Name2 + ": " + ex.Message);
                }
            }
            if (changed.Count > 0 && log != null)
                log("revealed " + changed.Count + " hidden component(s) for the export");
            return changed;
        }

        private static void Rehide(List<IComponent2> revealed, Action<string> log)
        {
            foreach (var comp in revealed)
            {
                try { comp.Visible = (int)swComponentVisibilityState_e.swComponentHidden; }
                catch (Exception ex)
                {
                    if (log != null) log("re-hide failed: " + ex.Message);
                }
            }
        }

        /// <summary>Public so the manifest can carry the hash of the FINAL
        /// file — the appearance pipeline rewrites the STEP after SaveAs3.</summary>
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
