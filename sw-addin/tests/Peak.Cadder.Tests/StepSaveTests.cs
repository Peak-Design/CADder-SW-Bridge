using System;
using System.IO;
using Peak.Cadder.Appearance;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The STEP rewrite replaces the file that SolidWorks wrote. When the
    /// write fails, the export reports that the file is as SolidWorks wrote
    /// it and stores the hash of that file. So a failed save must leave that
    /// file exactly as it was, never a part of the new text.
    /// </summary>
    public class StepSaveTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(),
            "cadder-save-" + Guid.NewGuid().ToString("N"));

        public StepSaveTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private string WriteStep()
        {
            string path = Path.Combine(_dir, "model.step");
            File.WriteAllText(path, "ISO-10303-21;\r\nHEADER;\r\nENDSEC;\r\nDATA;\r\n"
                + "#1=APPLICATION_CONTEXT('automotive design');\r\n"
                + "#2=PRODUCT_CONTEXT('',#1,'mechanical');\r\n"
                + "ENDSEC;\r\nEND-ISO-10303-21;\r\n");
            return path;
        }

        [Fact]
        public void AFailedSaveLeavesTheFileAsItWas()
        {
            string path = WriteStep();
            string before = File.ReadAllText(path);
            var step = new Part21(path);
            step.Replace(2, "#2=PRODUCT_CONTEXT('changed',#1,'mechanical');");
            // A lone surrogate cannot be written as UTF-8. The writer throws
            // after it has opened the target, as a full disk would.
            step.Append("#3=PRODUCT(" + Part21.Str("bad \uD800 name") + ",'','',(#2));");

            Assert.ThrowsAny<Exception>(() => step.Save(path));

            Assert.Equal(before, File.ReadAllText(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(_dir));
        }

        [Fact]
        public void ASaveReplacesTheFileAndLeavesNothingElse()
        {
            string path = WriteStep();
            var step = new Part21(path);
            step.Append("#3=PRODUCT('p','p','',(#2));");
            step.Save(path);

            Assert.Contains("#3=PRODUCT('p','p','',(#2));", File.ReadAllText(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(_dir));
        }
    }
}
