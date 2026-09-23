using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Peak.Cadder.Appearance;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The engineering material of a part that the assembly uses in more
    /// than one configuration. Each configuration can have its own material.
    /// SolidWorks names the STEP product of a non-default configuration
    /// 'doc_config'. The mass properties of a document give the density of
    /// its active configuration only.
    /// </summary>
    public class MaterialConfigurationTests : IDisposable
    {
        private readonly List<string> _tempFiles = new List<string>();

        public void Dispose()
        {
            foreach (var f in _tempFiles)
                try { File.Delete(f); } catch (IOException) { }
        }

        private const string Db = @"C:\materials\custom.sldmat";

        private static MaterialHarvester.ConfigurationMaterial Reading(string config, bool active,
            string material, string activeMaterial = "Steel")
            => new MaterialHarvester.ConfigurationMaterial
            {
                DocPath = @"C:\parts\bracket.SLDPRT",
                DocName = "bracket",
                Configuration = config,
                IsActive = active,
                Material = material,
                Database = Db,
                ActiveMaterial = activeMaterial,
                ActiveDatabase = Db,
                MassDensity = 7850,
            };

        private static double FakeDatabase(string db, string material)
            => db == Db && material == "Aluminium" ? 2700 : 0;

        [Fact]
        public void EachConfigurationGetsItsOwnMaterialAndDensity()
        {
            var entries = MaterialHarvester.ProductEntries(new[]
            {
                Reading("Steel", true, "Steel"),
                Reading("Aluminium", false, "Aluminium"),
            }, FakeDatabase, null).ToDictionary(e => e.ProductName);

            Assert.Equal("Steel", entries["bracket_Steel"].Name);
            Assert.Equal(7850, entries["bracket_Steel"].Density);
            Assert.Equal("Aluminium", entries["bracket_Aluminium"].Name);
            Assert.Equal(2700, entries["bracket_Aluminium"].Density);
            // Which configuration SolidWorks writes without a suffix is for
            // the writer to find out from the file.
            Assert.All(entries.Values, e => Assert.Equal("bracket", e.BareName));
        }

        [Fact]
        public void TheActiveDensityServesAnotherConfigurationWithTheSameMaterial()
        {
            var entries = MaterialHarvester.ProductEntries(new[]
            {
                Reading("Long", false, "Steel"),
            }, (db, m) => throw new InvalidOperationException("no database read needed"), null)
                .ToDictionary(e => e.ProductName);

            Assert.Equal(7850, entries["bracket_Long"].Density);
        }

        [Fact]
        public void AnUnknownDensityIsLeftOutRatherThanWrong()
        {
            var entries = MaterialHarvester.ProductEntries(new[]
            {
                Reading("Brass", false, "Brass"),
            }, FakeDatabase, null).ToDictionary(e => e.ProductName);

            Assert.Equal("Brass", entries["bracket_Brass"].Name);
            Assert.Equal(0, entries["bracket_Brass"].Density);
        }

        [Fact]
        public void TheDensityComesFromTheMaterialDatabase()
        {
            // The layout of a SolidWorks .sldmat file, in UTF-16 as
            // SolidWorks writes it.
            string xml = "<?xml version=\"1.0\" encoding=\"utf-16\"?>\r\n"
                + "<mstns:materials xmlns:mstns=\"http://www.solidworks.com/sldmaterials\" version=\"2008.03\">\r\n"
                + "  <classification name=\"Aluminium Alloys\">\r\n"
                + "    <material name=\"6061 Alloy\" matid=\"1\">\r\n"
                + "      <physicalproperties>\r\n"
                + "        <EX displayname=\"Elastic modulus\" value=\"69000000000\" />\r\n"
                + "        <DENS displayname=\"Mass density\" value=\"2700.000000\" />\r\n"
                + "      </physicalproperties>\r\n"
                + "    </material>\r\n"
                + "  </classification>\r\n"
                + "</mstns:materials>\r\n";
            string path = Path.Combine(Path.GetTempPath(), "cadder-mat-" + Guid.NewGuid().ToString("N") + ".sldmat");
            File.WriteAllText(path, xml, Encoding.Unicode);
            _tempFiles.Add(path);

            Assert.Equal(2700.0, MaterialHarvester.DatabaseDensity(path, "6061 Alloy"), 6);
            Assert.Equal(0.0, MaterialHarvester.DatabaseDensity(path, "Unknown"));
            Assert.Equal(0.0, MaterialHarvester.DatabaseDensity("SOLIDWORKS Materials", "6061 Alloy"));
        }

        /// <summary>A file with one part product for each name, and the
        /// material name that each product got.</summary>
        private Dictionary<string, string> WriteMaterials(IEnumerable<PartMaterial> entries,
            params string[] productNames)
        {
            var f = new AppearanceStepFixture();
            int asm = f.Product("asm");
            double x = 0;
            foreach (var n in productNames)
                f.Use(asm, f.ColouredPart(n, 0.5, 0.5, 0.5), x += 10, 0, 0);
            string path = f.Write(_tempFiles);

            var step = new Part21(path);
            new MaterialWriter(step, null).Apply(entries);
            step.Save(path);

            // PROPERTY_DEFINITION('material property','material name',#pd)
            // -> PROPERTY_DEFINITION_REPRESENTATION -> REPRESENTATION
            // -> DESCRIPTIVE_REPRESENTATION_ITEM(name, database).
            var back = new Part21(path);
            var result = new Dictionary<string, string>();
            foreach (var pdef in back.ByType("PROPERTY_DEFINITION"))
            {
                if (!(back.ArgsOf(pdef) ?? "").Contains("'material name'")) continue;
                int pd = back.Refs(pdef)[0];
                int pdf = back.Refs(pd)[0];
                string product = back.NameOf(back.Refs(pdf)[0]);
                int pdr = back.ByType("PROPERTY_DEFINITION_REPRESENTATION")
                    .First(r => back.Refs(r)[0] == pdef);
                int item = back.Refs(back.Refs(pdr)[1])[0];
                result[product] = back.NameOf(item);
            }
            return result;
        }

        [Fact]
        public void TheWriterGivesEachConfigurationProductItsMaterial()
        {
            // Steel is written without a suffix: its suffixed name is not
            // in the file, so the bare product must be Steel.
            var entries = MaterialHarvester.ProductEntries(new[]
            {
                Reading("Steel", true, "Steel"),
                Reading("Aluminium", false, "Aluminium"),
            }, FakeDatabase, null);
            var got = WriteMaterials(entries, "bracket", "bracket_Aluminium");

            Assert.Equal("Steel", got["bracket"]);
            Assert.Equal("Aluminium", got["bracket_Aluminium"]);
        }

        [Fact]
        public void TheBareProductCanBeTheInactiveConfiguration()
        {
            var entries = MaterialHarvester.ProductEntries(new[]
            {
                Reading("Steel", true, "Steel"),
                Reading("Aluminium", false, "Aluminium"),
            }, FakeDatabase, null);
            var got = WriteMaterials(entries, "bracket", "bracket_Steel");

            Assert.Equal("Aluminium", got["bracket"]);
            Assert.Equal("Steel", got["bracket_Steel"]);
        }

        [Fact]
        public void AnAmbiguousBareProductGetsNoMaterial()
        {
            // Neither suffixed name is in the file, so the bare product can
            // be either configuration. A missing material is honest, a
            // wrong one is not.
            var entries = MaterialHarvester.ProductEntries(new[]
            {
                Reading("Steel", true, "Steel"),
                Reading("Aluminium", false, "Aluminium"),
            }, FakeDatabase, null);
            var got = WriteMaterials(entries, "bracket");

            Assert.False(got.ContainsKey("bracket"));
        }

        [Fact]
        public void AConfigurationUsedAloneStillReachesTheBareProduct()
        {
            var entries = MaterialHarvester.ProductEntries(new[]
            {
                Reading("Long", false, "Steel"),
            }, FakeDatabase, null);
            var got = WriteMaterials(entries, "bracket");

            Assert.Equal("Steel", got["bracket"]);
        }
    }
}
