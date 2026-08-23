using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;

namespace Peak.SwToBlender.Appearance
{
    public sealed class AppearancePipelineResult
    {
        public int ColoursApplied;
        public int ColoursUnmatched;
        public bool FileModified;
        public FlexFixOutcome FlexFix;
        public List<string> Notes = new List<string>();
    }

    /// <summary>
    /// The STEP post-processing pass, in fixed order over ONE parse of the
    /// file: flexible-twin de-instancing first (it rewires the occurrence
    /// tables the colour matcher walks), then the NEXT-STEP appearance repair,
    /// then engineering materials (they must see the products the colour pass
    /// cloned), then a single save. Runs after SolidWorks wrote the file and
    /// before the rig occurrence matcher re-parses it.
    /// </summary>
    public static class AppearancePipeline
    {
        public static AppearancePipelineResult Run(
            IModelDoc2 model, string stepPath,
            bool repairAppearances, bool deInstance, bool includeMaterial,
            bool includeHidden, List<FlexFixRequest> flexRequests,
            Action<string> log)
        {
            var result = new AppearancePipelineResult();
            bool anythingToDo = repairAppearances || includeMaterial
                || (flexRequests != null && flexRequests.Count > 0);
            if (!anythingToDo) return result;

            var rw = new StepRewriter(stepPath, log);
            var assembly = model as IAssemblyDoc;
            List<StepRewriter.OccurrenceRef> occs = null;
            if (assembly != null) occs = rw.FindOccurrences();

            // ── Flexible-twin de-instancing ─────────────────────────────────
            if (occs != null && flexRequests != null && flexRequests.Count > 0)
            {
                result.FlexFix = FlexiblePoseFixer.Fix(
                    rw.Document, occs, rw.ChildrenByParentPd, flexRequests, log);
                if (result.FlexFix.PlacementsRetargeted > 0)
                {
                    result.FileModified = true;
                    result.Notes.Add(string.Format(
                        "De-instanced {0} flexible-twin definition(s) and reposed "
                        + "{1} internal placement(s) to the SolidWorks layout.",
                        result.FlexFix.DefinitionsCloned,
                        result.FlexFix.PlacementsRetargeted));
                }
                foreach (var note in result.FlexFix.Notes)
                    result.Notes.Add("Flexible twin: " + note);
            }

            // ── Appearance repair (NEXT-STEP engine) ────────────────────────
            if (repairAppearances && assembly != null)
            {
                var occurrences = AppearanceLadder.Resolve(model, log, includeHidden);

                int skipped = occurrences.Count(o => !o.Exported);
                if (skipped > 0)
                    result.Notes.Add(skipped
                        + " component(s) hidden or suppressed, and not exported.");

                int needFixing = occurrences.Count(
                    o => o.Exported && o.OverridesPartInternals);
                if (needFixing == 0)
                {
                    result.Notes.Add(occurrences.Count + " component(s). None "
                        + "carries an override, so no appearance needed repair.");
                }
                else
                {
                    var pairs = new AppearanceMatcher(model, log)
                        .Match(occurrences, rw);
                    result.ColoursUnmatched = pairs.Count(
                        p => p.Key.OverridesPartInternals && p.Value == null);
                    result.ColoursApplied = rw.ApplyOccurrenceColours(pairs, deInstance);
                    if (result.ColoursApplied > 0) result.FileModified = true;
                    result.Notes.Add(occurrences.Count + " component(s), "
                        + needFixing + " with an override. Restored "
                        + result.ColoursApplied + " appearance(s) "
                        + (deInstance ? "(de-instanced)." : "(instancing kept)."));
                }
            }
            else if (repairAppearances)
            {
                result.Notes.Add("Part document. No appearance needed repair.");
            }

            // ── Engineering materials ───────────────────────────────────────
            if (includeMaterial)
            {
                var materials = MaterialHarvester.Harvest(model, log);
                int withMaterial = new MaterialWriter(rw.Document, log)
                    .Apply(materials, deInstance ? rw.BucketIndexByProduct : null);
                if (withMaterial > 0) result.FileModified = true;
                result.Notes.Add(withMaterial > 0
                    ? "Wrote the engineering material for " + withMaterial + " part(s)."
                    : "No part has an engineering material.");
            }

            if (result.FileModified) rw.Save(stepPath);
            return result;
        }
    }
}
