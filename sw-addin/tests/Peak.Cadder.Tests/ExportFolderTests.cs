using System;
using System.IO;
using Peak.Cadder.Core;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// Where an export writes.
    ///
    /// Every mode gives the export a folder of its own, named after the
    /// document. Before 2026-09-16 only the app-data mode did that, and
    /// "next to the assembly" dropped the STEP file, the manifest and the
    /// mesh straight into the project folder, one export over the last
    /// (Oscar).
    /// </summary>
    public class ExportFolderTests
    {
        private static string AppData
        {
            get { return ExportPaths.AppDataExports; }
        }

        [Fact]
        public void AppDataModeKeepsEachExportApart()
        {
            var settings = new AppSettings { ExportFolderMode = "temp" };
            Assert.Equal(Path.Combine(AppData, "cam-follower"),
                ExportPaths.For(
                    settings, @"C:\work\cam-follower.SLDASM", "cam-follower"));
        }

        [Fact]
        public void BesideTheAssemblyIsAFolderBesideIt()
        {
            var settings = new AppSettings { ExportFolderMode = "beside" };
            Assert.Equal(@"C:\work\cam-follower",
                ExportPaths.For(
                    settings, @"C:\work\cam-follower.SLDASM", "cam-follower"));
        }

        [Fact]
        public void AFolderOfYourOwnIsUsedAndStillNests()
        {
            var settings = new AppSettings
            {
                ExportFolderMode = "custom",
                ExportFolder = @"D:\to blender",
            };
            Assert.Equal(@"D:\to blender\cam-follower",
                ExportPaths.For(
                    settings, @"C:\work\cam-follower.SLDASM", "cam-follower"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void AFolderOfYourOwnWithNoFolderFallsBackToAppData(string folder)
        {
            // The export is the work and the folder is a preference: a mode
            // with nothing behind it must not fail the send.
            var settings = new AppSettings
            {
                ExportFolderMode = "custom",
                ExportFolder = folder,
            };
            Assert.Equal(Path.Combine(AppData, "cam-follower"),
                ExportPaths.For(
                    settings, @"C:\work\cam-follower.SLDASM", "cam-follower"));
        }

        [Fact]
        public void AnUnsavedDocumentStillHasSomewhereToGo()
        {
            var settings = new AppSettings { ExportFolderMode = "beside" };
            Assert.Equal(Path.Combine(AppData, "cam-follower"),
                ExportPaths.For(settings, null, "cam-follower"));
        }

        [Theory]
        [InlineData("cam-follower", "cam-follower")]
        [InlineData("rev 2.1", "rev 2.1")]
        [InlineData("rev 2.", "rev 2")]          // a folder cannot end in a dot
        [InlineData("a/b", "a_b")]
        [InlineData("", "export")]
        [InlineData(null, "export")]
        public void TheFolderIsNamedAfterTheDocument(string title, string want)
        {
            Assert.Equal(want, ExportPaths.FolderName(title));
        }

        [Fact]
        public void TheChosenFolderSurvivesASaveAndLoad()
        {
            string path = Path.Combine(
                Path.GetTempPath(), "cadder-settings-" + Guid.NewGuid().ToString("N"),
                "settings.json");
            try
            {
                new AppSettings
                {
                    ExportFolderMode = "custom",
                    ExportFolder = @"D:\to blender",
                }.Save(null, path);
                var read = AppSettings.Load(null, path);
                Assert.Equal("custom", read.ExportFolderMode);
                Assert.Equal(@"D:\to blender", read.ExportFolder);
            }
            finally
            {
                try { Directory.Delete(Path.GetDirectoryName(path), true); }
                catch { }
            }
        }
    }
}
