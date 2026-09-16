using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
// Vendored from NEXT-STEP-SW (Peak.NextStep) @ b081285: the STEP appearance engine, merged into CADder Bridge.

namespace Peak.Cadder.Appearance
{
    /// <summary>
    /// Read the engineering material assigned in SolidWorks.
    ///
    /// Name comes from IPartDoc.GetMaterialPropertyName2. Density is taken from
    /// a mass-property evaluation rather than by parsing the material database:
    /// IMassProperty.Density reports the density actually in effect, which also
    /// covers materials edited in the document or overridden per configuration.
    /// </summary>
    public static class MaterialHarvester
    {
        public static List<PartMaterial> Harvest(IModelDoc2 model, Action<string> log)
        {
            var results = new List<PartMaterial>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (model is IPartDoc)
            {
                var m = FromPart(model, log);
                if (m != null) results.Add(m);
                return results;
            }

            if (model is IAssemblyDoc assy)
            {
                var comps = assy.GetComponents(false) as object[] ?? new object[0];
                foreach (var o in comps)
                {
                    var comp = o as IComponent2;
                    var doc = comp?.GetModelDoc2() as IModelDoc2;
                    if (!(doc is IPartDoc)) continue;

                    // One material per referenced part document, not per
                    // occurrence: the STEP product is shared.
                    string key = doc.GetPathName() ?? "";
                    if (!seen.Add(key)) continue;

                    var m = FromPart(doc, log, comp.ReferencedConfiguration);
                    if (m != null) results.Add(m);
                }
            }
            return results;
        }

        private static PartMaterial FromPart(IModelDoc2 doc, Action<string> log,
                                             string configName = "")
        {
            var part = doc as IPartDoc;
            if (part == null) return null;

            string database = "";
            string name;
            try { name = part.GetMaterialPropertyName2(configName ?? "", out database); }
            catch (Exception ex) { log?.Invoke($"    material read failed: {ex.Message}"); return null; }

            if (string.IsNullOrWhiteSpace(name)) return null;

            double density = 0;
            try
            {
                var mp = doc.Extension.CreateMassProperty();
                if (mp != null) density = mp.Density;
            }
            catch (Exception ex) { log?.Invoke($"    density read failed: {ex.Message}"); }

            return new PartMaterial
            {
                // SolidWorks names the STEP product after the part file.
                ProductName = Path.GetFileNameWithoutExtension(doc.GetPathName() ?? ""),
                Name = name,
                Database = CleanDatabase(database),
                Density = density,
            };
        }

        /// <summary>The database comes back as a full path; the file name reads better.</summary>
        private static string CleanDatabase(string database)
        {
            if (string.IsNullOrWhiteSpace(database)) return "";
            try { return Path.GetFileNameWithoutExtension(database); }
            catch { return database; }
        }
    }
}
