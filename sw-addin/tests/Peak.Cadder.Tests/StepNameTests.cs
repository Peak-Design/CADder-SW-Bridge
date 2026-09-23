using System.IO;
using Xunit;
using SwPart21 = Peak.Cadder.Sw.Part21;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// ISO 10303-21 writes a character outside plain ASCII as a control
    /// directive inside the string. The Blender side reads names through
    /// OCCT, which decodes them, and the matcher compares the STEP product
    /// name with the SolidWorks file name. So the add-in's reader decodes
    /// them too, or a part named with an accent or a diameter sign never
    /// matches.
    /// </summary>
    public class StepNameTests
    {
        private static SwPart21 Load(string data)
        {
            string path = Path.GetTempFileName();
            File.WriteAllText(path,
                "ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\n" + data
                + "ENDSEC;\nEND-ISO-10303-21;\n");
            try { return new SwPart21(path); }
            finally { File.Delete(path); }
        }

        [Theory]
        [InlineData(@"Geh\X2\00E4\X0\use", "Gehäuse")]
        [InlineData(@"Shaft \X2\00D8\X0\20", "Shaft Ø20")]
        [InlineData(@"\X2\00C400D6\X0\", "ÄÖ")]
        [InlineData(@"Geh\X\E4use", "Gehäuse")]
        [InlineData(@"Geh\S\duse", "Gehäuse")]
        [InlineData(@"\X4\0001F600\X0\", "\U0001F600")]
        [InlineData(@"\X2\D83DDE00\X0\", "\U0001F600")]
        [InlineData(@"C:\\parts", @"C:\parts")]
        [InlineData(@"\PA\plain", "plain")]
        [InlineData("it''s", "it's")]
        [InlineData("Bracket", "Bracket")]
        public void AProductNameIsDecoded(string written, string name)
        {
            var step = Load("#1 = PRODUCT ( '" + written + "', '" + written + "', '', ( #2 ) ) ;\n");
            Assert.Equal(name, step.NameOf(1));
            Assert.Equal(name, step.QuotedStringAt(1, 1));
        }
    }
}
