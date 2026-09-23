using System;
using System.Collections.Generic;
using System.IO;
using Peak.Cadder.Sw;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A silent SaveAs3 leaves hidden components out of the STEP file, and
    /// "only the selected components" hides everything outside the keep set
    /// before the save. Such a component is not in the file by design. The
    /// matcher must not count it as exported, or the manifest warns that it
    /// could not be matched.
    /// </summary>
    public class OccurrenceInFileTests
    {
        private static OccurrenceMatcher Matcher()
        {
            string path = Path.GetTempFileName();
            File.WriteAllText(path,
                "ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;\n");
            try { return new OccurrenceMatcher(new Part21(path), null); }
            finally { File.Delete(path); }
        }

        private static WalkedComponent W(string path, WalkedComponent parent = null)
        {
            var w = new WalkedComponent { Id = path, Parent = parent };
            w.Graph.Path = path;
            return w;
        }

        [Fact]
        public void AHiddenComponentIsNotInTheFile()
        {
            var shown = W("Shown-1");
            var hidden = W("Hidden-1");
            var flex = W("Flex-1");
            var underHidden = W("Flex-1/Part-1", flex);
            var suppressed = W("Gone-1");
            suppressed.Graph.Suppressed = true;
            var off = new HashSet<string> { "Hidden-1", "Flex-1" };
            var matcher = Matcher();
            matcher.Visible = w => !off.Contains(w.Id);
            var result = matcher.Match(new List<WalkedComponent>
                { shown, hidden, flex, underHidden, suppressed });
            Assert.Equal(1, result.ExportedCount);
            Assert.Contains("Hidden-1", result.LeftOut);
            Assert.Contains("Flex-1/Part-1", result.LeftOut);
            Assert.DoesNotContain("Gone-1", result.LeftOut);
        }

        [Fact]
        public void HiddenComponentsCountWhenTheyAreExported()
        {
            var hidden = W("Hidden-1");
            var matcher = Matcher();
            matcher.IncludeHidden = true;
            matcher.Visible = w => false;
            var result = matcher.Match(new List<WalkedComponent> { hidden });
            Assert.Equal(1, result.ExportedCount);
            Assert.Empty(result.LeftOut);
        }

        [Fact]
        public void AComponentOutsideTheSelectionIsNotInTheFile()
        {
            var picked = W("Picked-1");
            var other = W("Other-1");
            var matcher = Matcher();
            matcher.Visible = w => true;
            matcher.Keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "picked-1" };
            var result = matcher.Match(new List<WalkedComponent> { picked, other });
            Assert.Equal(1, result.ExportedCount);
            Assert.Contains("Other-1", result.LeftOut);
        }
    }
}
