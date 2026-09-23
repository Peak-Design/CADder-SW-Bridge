using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SolidWorks.Interop.sldworks;
// Vendored from NEXT-STEP-SW (Peak.NextStep) @ b081285: the STEP appearance engine, merged into CADder Bridge.

namespace Peak.Cadder.Appearance
{
    /// <summary>
    /// Read the engineering material assigned in SolidWorks.
    ///
    /// Name comes from IPartDoc.GetMaterialPropertyName2, for each
    /// configuration that the assembly uses. Density is taken from a
    /// mass-property evaluation where it can be: IMassProperty.Density
    /// reports the density actually in effect. That evaluation runs in the
    /// active configuration of the part, so a configuration with another
    /// material takes its density from the material database instead.
    /// </summary>
    public static class MaterialHarvester
    {
        /// <summary>The material of one configuration of one part
        /// document.</summary>
        internal sealed class ConfigurationMaterial
        {
            public string DocPath;
            /// <summary>The file name without the extension.</summary>
            public string DocName;
            /// <summary>The referenced configuration, or "" when there is
            /// none.</summary>
            public string Configuration;
            /// <summary>True when Configuration is the active configuration
            /// of the document.</summary>
            public bool IsActive;
            public string Material;
            public string Database;
            /// <summary>The material of the active configuration.</summary>
            public string ActiveMaterial;
            public string ActiveDatabase;
            /// <summary>The density of the document's mass properties, which
            /// SolidWorks evaluates in the active configuration.</summary>
            public double MassDensity;
        }

        public static List<PartMaterial> Harvest(IModelDoc2 model, Action<string> log)
        {
            var readings = new List<ConfigurationMaterial>();
            // One mass-property evaluation per document, as before: it
            // gives the density of the active configuration only.
            var massDensity = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            if (model is IPartDoc)
            {
                var m = Read(model, "", massDensity, log);
                if (m != null) readings.Add(m);
            }
            else if (model is IAssemblyDoc assy)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var comps = assy.GetComponents(false) as object[] ?? new object[0];
                foreach (var o in comps)
                {
                    var comp = o as IComponent2;
                    var doc = comp?.GetModelDoc2() as IModelDoc2;
                    if (!(doc is IPartDoc)) continue;

                    string cfg = SafeStr(() => comp.ReferencedConfiguration) ?? "";
                    string key = (doc.GetPathName() ?? "") + "|" + cfg;
                    if (!seen.Add(key)) continue;

                    var m = Read(doc, cfg, massDensity, log);
                    if (m != null) readings.Add(m);
                }
            }

            var fromDatabase = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            return ProductEntries(readings, (db, material) =>
            {
                string key = db + "|" + material;
                if (!fromDatabase.TryGetValue(key, out double d))
                    fromDatabase[key] = d = DatabaseDensity(db, material);
                return d;
            }, log);
        }

        /// <summary>
        /// The materials to write, one for each configuration of each part
        /// document, not one for each occurrence: the STEP product is shared.
        ///
        /// The harvest read one configuration per document, the first one it
        /// met, and named it after the file only. SolidWorks names the
        /// product of a non-default configuration 'doc_config'. A part used
        /// in two configurations with two materials therefore got the
        /// material of one of them, and the other product got none. The
        /// density came from the active configuration in every case.
        ///
        /// Each entry now carries the configuration in its product name,
        /// and the file name as BareName. MaterialWriter finds out from the
        /// file which configuration SolidWorks wrote without a suffix.
        /// </summary>
        internal static List<PartMaterial> ProductEntries(
            IEnumerable<ConfigurationMaterial> readings,
            Func<string, string, double> databaseDensity, Action<string> log)
        {
            var result = new List<PartMaterial>();
            foreach (var r in readings)
            {
                bool configured = !string.IsNullOrEmpty(r.Configuration);
                result.Add(new PartMaterial
                {
                    // SolidWorks names the STEP product after the part file.
                    ProductName = configured ? r.DocName + "_" + r.Configuration : r.DocName,
                    BareName = configured ? r.DocName : null,
                    Name = r.Material,
                    Database = CleanDatabase(r.Database),
                    Density = DensityOf(r, databaseDensity, log),
                });
            }
            return result;
        }

        /// <summary>
        /// The density of one configuration. The mass properties give the
        /// density of the active configuration, so they serve only that
        /// configuration and any other with the same material. Another
        /// material takes its density from the material database. When that
        /// is not found, the density is left out: a missing density is
        /// honest, a wrong one is not.
        /// </summary>
        private static double DensityOf(ConfigurationMaterial r,
            Func<string, string, double> databaseDensity, Action<string> log)
        {
            bool sameAsActive = r.ActiveMaterial != null
                && string.Equals(r.Material, r.ActiveMaterial, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.Database ?? "", r.ActiveDatabase ?? "",
                    StringComparison.OrdinalIgnoreCase);
            if (r.IsActive || sameAsActive) return r.MassDensity;

            double density = databaseDensity?.Invoke(r.Database, r.Material) ?? 0;
            if (density <= 0)
            {
                log?.Invoke($"    {r.DocName} ({r.Configuration}): no density found for "
                          + $"'{r.Material}'; the material is written without it");
                return 0;
            }
            return density;
        }

        private static ConfigurationMaterial Read(IModelDoc2 doc, string configName,
            Dictionary<string, double> massDensity, Action<string> log)
        {
            var part = doc as IPartDoc;
            if (part == null) return null;

            string database = "";
            string name;
            try { name = part.GetMaterialPropertyName2(configName ?? "", out database); }
            catch (Exception ex) { log?.Invoke($"    material read failed: {ex.Message}"); return null; }

            if (string.IsNullOrWhiteSpace(name)) return null;

            string active = SafeStr(() => doc.ConfigurationManager?.ActiveConfiguration?.Name) ?? "";
            bool isActive = string.IsNullOrEmpty(configName)
                || string.Equals(configName, active, StringComparison.OrdinalIgnoreCase);
            string activeName = name, activeDatabase = database;
            if (!isActive)
            {
                try { activeName = part.GetMaterialPropertyName2(active, out activeDatabase); }
                catch (Exception ex)
                {
                    log?.Invoke($"    material read failed: {ex.Message}");
                    activeName = null;
                    activeDatabase = null;
                }
            }

            string path = doc.GetPathName() ?? "";
            if (!massDensity.TryGetValue(path, out double density))
            {
                try
                {
                    var mp = doc.Extension.CreateMassProperty();
                    if (mp != null) density = mp.Density;
                }
                catch (Exception ex) { log?.Invoke($"    density read failed: {ex.Message}"); }
                massDensity[path] = density;
            }

            return new ConfigurationMaterial
            {
                DocPath = path,
                DocName = Path.GetFileNameWithoutExtension(path),
                Configuration = configName ?? "",
                IsActive = isActive,
                Material = name,
                Database = database,
                ActiveMaterial = activeName,
                ActiveDatabase = activeDatabase,
                MassDensity = density,
            };
        }

        /// <summary>
        /// The density of a named material in a SolidWorks material database
        /// (.sldmat), in kg/m^3, or 0 when the file or the material is not
        /// there. The file is XML, UTF-16 as SolidWorks writes it:
        ///   classification / material name="..." / physicalproperties /
        ///   DENS value="7858.000032"
        /// The values are in SI units.
        /// </summary>
        internal static double DatabaseDensity(string database, string material)
        {
            if (string.IsNullOrWhiteSpace(database) || string.IsNullOrEmpty(material))
                return 0;
            try
            {
                if (!Path.IsPathRooted(database) || !File.Exists(database)) return 0;
                var xml = XDocument.Load(database);
                foreach (var m in xml.Descendants().Where(e => e.Name.LocalName == "material"))
                {
                    if (!string.Equals((string)m.Attribute("name"), material,
                                       StringComparison.OrdinalIgnoreCase))
                        continue;
                    var dens = m.Descendants().FirstOrDefault(e => e.Name.LocalName == "DENS");
                    if (double.TryParse((string)dens?.Attribute("value"), NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out double value))
                        return value;
                }
            }
            catch (Exception) { }
            return 0;
        }

        /// <summary>The database comes back as a full path; the file name reads better.</summary>
        private static string CleanDatabase(string database)
        {
            if (string.IsNullOrWhiteSpace(database)) return "";
            try { return Path.GetFileNameWithoutExtension(database); }
            catch { return database; }
        }

        private static string SafeStr(Func<string> f)
        { try { return f(); } catch { return null; } }
    }
}
