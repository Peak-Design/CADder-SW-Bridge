using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
// Vendored from NEXT-STEP-SW (Peak.NextStep) @ b081285 — the STEP appearance engine, merged into SW To Blender.

namespace Peak.SwToBlender.Appearance
{
    public sealed class PartMaterial
    {
        /// <summary>The STEP product name. SolidWorks takes it from the part
        /// file name.</summary>
        public string ProductName;
        public string Name;
        public string Database;
        /// <summary>kg/m^3.</summary>
        public double Density;
    }

    /// <summary>
    /// Writes the engineering material into the STEP file. This is the name, the
    /// description and the density. SolidWorks exports none of it, in any AP.
    ///
    /// This code uses the form that OCCT itself writes and reads. It was checked
    /// against the STEPper NEXT baselines in ci/baselines/mat_ap214.step:
    ///
    ///     PROPERTY_DEFINITION('material property','material name',#pd)
    ///       -> REPRESENTATION -> DESCRIPTIVE_REPRESENTATION_ITEM(name, description)
    ///     PROPERTY_DEFINITION('material property','density',#pd)
    ///       -> REPRESENTATION -> MEASURE_REPRESENTATION_ITEM('density', value, #kgPerM3)
    ///
    /// AP242 is NOT necessary for this. The output is the same in AP214 and in
    /// AP242, so no MBD licence applies. See FINDINGS.md 3b.1.
    /// </summary>
    public sealed class MaterialWriter
    {
        private readonly Part21 _step;
        private readonly Action<string> _log;
        private int _densityUnit = -1;

        public MaterialWriter(Part21 step, Action<string> log)
        {
            _step = step;
            _log = log;
        }

        /// <summary>Uses an existing representation context. Does not make a
        /// new one.</summary>
        private int AnyContext()
        {
            foreach (var kv in _step.Entities)
                if ((kv.Value.Key ?? "").IndexOf("GEOMETRIC_REPRESENTATION_CONTEXT",
                                                 StringComparison.Ordinal) >= 0)
                    return kv.Key;
            return -1;
        }

        /// <summary>
        /// The density unit. This code makes it once for each file.
        ///
        /// The form here copies the OCCT form exactly. It comes from
        /// ci/baselines/mat_ap214.step:
        ///
        ///     DERIVED_UNIT(( DERIVED_UNIT_ELEMENT(&lt;gram&gt;, 3.),
        ///                    DERIVED_UNIT_ELEMENT(&lt;centimetre&gt;, 2.) ))
        ///
        /// The dimensions are NOT kg/m^3. No reading of the exponents gives
        /// mass divided by length cubed. But OCCT writes this form, its reader
        /// returns the same value, and STEPper NEXT reads the density through
        /// OCCT.
        ///
        /// A correct DERIVED_UNIT of (kilogram, 1) and (metre, -3) was tried
        /// first. It came back as 0.0078 instead of 7800, because the reader
        /// scaled it against the millimetre length unit of the file. A form that
        /// works is better than a form that is correct on paper and wrong in the
        /// consumer.
        /// </summary>
        private int DensityUnit()
        {
            if (_densityUnit > 0) return _densityUnit;

            int gram = _step.NextId();
            _step.Append($"#{gram}=( MASS_UNIT() NAMED_UNIT(*) SI_UNIT($,.GRAM.) );");
            int centimetre = _step.NextId();
            _step.Append($"#{centimetre}=( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT(.CENTI.,.METRE.) );");
            int e1 = _step.NextId();
            _step.Append($"#{e1}=DERIVED_UNIT_ELEMENT(#{gram},3.);");
            int e2 = _step.NextId();
            _step.Append($"#{e2}=DERIVED_UNIT_ELEMENT(#{centimetre},2.);");
            _densityUnit = _step.NextId();
            _step.Append($"#{_densityUnit}=DERIVED_UNIT((#{e1},#{e2}));");
            return _densityUnit;
        }

        /// <summary>
        /// Blender cuts a material name at 60 characters. A suffix after that
        /// point disappears, and two groups then merge into one material again.
        /// The base name gives way instead.
        /// </summary>
        private const int MaxNameLength = 60;

        internal static string BucketName(string baseName, int bucketIndex)
        {
            if (bucketIndex <= 0) return baseName;
            string suffix = "." + bucketIndex.ToString("000", CultureInfo.InvariantCulture);
            int room = MaxNameLength - suffix.Length;
            if (baseName.Length > room) baseName = baseName.Substring(0, room);
            return baseName + suffix;
        }

        /// <summary>
        /// Attaches a material to every product whose name matches. Returns the
        /// number of products that got a material.
        ///
        /// With bucketIndexByProduct, this code splits one CAD material into one
        /// named variant for each appearance group. The names are then
        /// "Plain Carbon Steel", "Plain Carbon Steel.001" and so on.
        ///
        /// This is a workaround, and it has a cost that is worth stating. STEP
        /// has no relation between a material and an appearance. The two are
        /// separate by design, and no entity in AP214 or AP242 joins them. A
        /// consumer that builds one material for each material NAME therefore
        /// merges every copy of a part into one material and loses the colours.
        /// A number in the name makes those consumers build one material for
        /// each colour.
        ///
        /// The price is the name itself. The file reports
        /// "Plain Carbon Steel.001" to any tool that reads the material
        /// correctly, and that is not the name of a material. The user must
        /// therefore select this behaviour.
        /// </summary>
        public int Apply(IEnumerable<PartMaterial> materials,
                         IDictionary<int, int> bucketIndexByProduct = null)
        {
            int context = AnyContext();
            if (context < 0)
            {
                _log?.Invoke("    no representation context found; skipping materials");
                return 0;
            }

            var byName = new Dictionary<string, PartMaterial>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in materials)
                if (!string.IsNullOrEmpty(m?.ProductName) && !string.IsNullOrEmpty(m.Name))
                    byName[m.ProductName] = m;
            if (byName.Count == 0) return 0;

            int applied = 0;
            foreach (var product in _step.ByType("PRODUCT"))
            {
                string productName = _step.NameOf(product);
                if (productName == null || !byName.TryGetValue(productName, out var mat)) continue;

                int pd = ProductDefinitionOf(product);
                if (pd < 0)
                {
                    _log?.Invoke($"    {productName}: no product_definition; skipped");
                    continue;
                }

                int bucket = 0;
                bucketIndexByProduct?.TryGetValue(product, out bucket);
                string name = BucketName(mat.Name, bucket);

                WriteNamed(pd, context, "material name",
                           $"DESCRIPTIVE_REPRESENTATION_ITEM({Part21.Str(name)}," +
                           $"{Part21.Str(mat.Database ?? "")})");

                if (mat.Density > 0)
                    WriteNamed(pd, context, "density",
                               $"MEASURE_REPRESENTATION_ITEM('density'," +
                               $"{Part21.Num(mat.Density)},#{DensityUnit()})");

                applied++;
                _log?.Invoke($"    {productName} (#{product}): material '{name}' " +
                             $"density {mat.Density:F1} kg/m^3");
            }
            return applied;
        }

        private void WriteNamed(int productDefinition, int context, string role, string itemEntity)
        {
            int item = _step.NextId();
            _step.Append($"#{item}={itemEntity};");
            int rep = _step.NextId();
            _step.Append($"#{rep}=REPRESENTATION({Part21.Str(role)},(#{item}),#{context});");
            int pdef = _step.NextId();
            _step.Append($"#{pdef}=PROPERTY_DEFINITION('material property'," +
                         $"{Part21.Str(role)},#{productDefinition});");
            int pdr = _step.NextId();
            _step.Append($"#{pdr}=PROPERTY_DEFINITION_REPRESENTATION(#{pdef},#{rep});");
        }

        /// <summary>Walks PRODUCT_DEFINITION back to
        /// PRODUCT_DEFINITION_FORMATION and then to PRODUCT.</summary>
        private int ProductDefinitionOf(int product)
        {
            var formations = _step.ByType("PRODUCT_DEFINITION_FORMATION")
                .Concat(_step.ByType("PRODUCT_DEFINITION_FORMATION_WITH_SPECIFIED_SOURCE"))
                .Where(f => _step.Refs(f).Contains(product))
                .ToList();

            foreach (var f in formations)
            {
                var pd = _step.ByType("PRODUCT_DEFINITION").FirstOrDefault(p => _step.Refs(p).Contains(f));
                if (pd != 0) return pd;
            }
            return -1;
        }
    }
}
