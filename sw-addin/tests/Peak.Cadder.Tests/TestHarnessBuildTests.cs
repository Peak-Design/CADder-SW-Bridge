using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Peak.Cadder.Core;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The test harness is a tool for development, not a feature of the
    /// product. It opens, closes and changes documents over the localhost
    /// port, so a shipped add-in must not carry the switch for it at all.
    ///
    /// Two of these read the source rather than the behaviour. The tests run
    /// in a Debug build, where the harness is present by design, so a
    /// behavioural test could never see the Release side. Reading the source
    /// pins it in both configurations.
    /// </summary>
    public class TestHarnessBuildTests
    {
        private static string Source(params string[] parts)
        {
            var path = Path.Combine(RepositoryRoot(),
                "sw-addin", "src", "Peak.Cadder");
            foreach (var part in parts) path = Path.Combine(path, part);
            return File.ReadAllText(path);
        }

        private static string RepositoryRoot([CallerFilePath] string here = null)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(here));
            while (dir != null
                   && !File.Exists(Path.Combine(dir.FullName, "CHANGELOG.md")))
                dir = dir.Parent;
            Assert.True(dir != null, "no repository root above " + here);
            return dir.FullName;
        }

        [Fact]
        public void ThisBuildHasTheHarness()
        {
            // The tests build Debug. A Release test run would say the
            // opposite, and both are correct.
            Assert.True(AppSettings.LabBuild);
        }

        [Fact]
        public void TheSettingAloneDoesNotOpenTheGate()
        {
            var settings = new AppSettings { LabOps = false };
            Assert.False(settings.LabOpsAllowed);
            settings.LabOps = true;
            Assert.Equal(AppSettings.LabBuild, settings.LabOpsAllowed);
        }

        [Fact]
        public void TheBuildDecidesWhetherTheHarnessExists()
        {
            var source = Source("Core", "AppSettings.cs");
            Assert.Matches(new Regex(
                @"#if DEBUG\s+public const bool LabBuild = true;\s+"
                + @"#else\s+public const bool LabBuild = false;\s+#endif"),
                source);
        }

        [Fact]
        public void TheGateReadsTheBuildAndNotTheSetting()
        {
            var source = Source("Bridge", "SwCommandHandler.cs");
            Assert.Contains("if (!settings.LabOpsAllowed)", source);
            Assert.DoesNotContain("if (!settings.LabOps)", source);
        }

        [Fact]
        public void TheDialogOffersTheHarnessInADebugBuildOnly()
        {
            var source = Source("BlenderOptionsDialog.cs");
            var depth = 0;
            var guarded = true;
            foreach (var line in source.Split('\n'))
            {
                var text = line.Trim();
                if (text.StartsWith("#if DEBUG")) depth++;
                else if (text.StartsWith("#endif") && depth > 0) depth--;
                else if (text.Contains("_labOps") || text.Contains("labGroup")
                         || text.Contains("Test harness"))
                    guarded &= depth > 0;
            }
            Assert.True(guarded,
                "the harness controls must sit inside #if DEBUG");
        }
    }
}
