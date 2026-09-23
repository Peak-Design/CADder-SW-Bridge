using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Peak.Cadder.Sw;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A manifest written for the direct link, which sends a mesh and no
    /// STEP file. It read the STEP that an earlier STEP export left in the
    /// same folder, hashed it and matched against it. The manifest then
    /// named a file that has nothing to do with the send, and warned about
    /// every part added since. Now it names no hash and no occurrence
    /// path. Export Rig still matches against the STEP beside its
    /// manifest, which is what it is for.
    /// </summary>
    public class DirectLinkStepTests : IDisposable
    {
        private readonly string _dir;

        public DirectLinkStepTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cadder-step-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        /// <summary>A STEP file left over from an earlier export.</summary>
        private string StaleStep()
        {
            string path = Path.Combine(_dir, "lifter.step");
            File.WriteAllText(path,
                "ISO-10303-21;\nHEADER;\nENDSEC;\nDATA;\nENDSEC;\nEND-ISO-10303-21;\n");
            return path;
        }

        [Fact]
        public void TheDirectLinkNamesNoStepHash()
        {
            string step = StaleStep();
            var notes = new List<string>();
            MatchResult matches;
            string sha1 = ExportCommand.ReadExistingStep(
                step, false, new List<WalkedComponent>(), notes, out matches, null);
            Assert.Null(sha1);
            Assert.Empty(matches.ByComponentId);
            Assert.Single(notes);
            Assert.DoesNotContain("matched against", notes[0]);
        }

        [Fact]
        public void ExportRigStillHashesTheStepBesideIt()
        {
            string step = StaleStep();
            var notes = new List<string>();
            MatchResult matches;
            string sha1 = ExportCommand.ReadExistingStep(
                step, true, new List<WalkedComponent>(), notes, out matches, null);
            Assert.Equal(StepExporter.Sha1Hex(step), sha1);
        }

        [Fact]
        public void NoStepBesideExportRigIsNoHash()
        {
            var notes = new List<string>();
            MatchResult matches;
            Assert.Null(ExportCommand.ReadExistingStep(
                Path.Combine(_dir, "none.step"), true, new List<WalkedComponent>(),
                notes, out matches, null));
        }

        /// <summary>Both direct sends ask for the manifest without the
        /// STEP. Read off the source, because they need SolidWorks.</summary>
        [Fact]
        public void TheDirectSendsAskForNoStep()
        {
            string send = Source("SendToBlenderCommand.cs");
            int native = send.IndexOf("if (native)", StringComparison.Ordinal);
            int step = send.IndexOf("else", send.IndexOf("NativeExport.Write", native, StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.Contains("matchStep: false", send.Substring(native, step - native));

            string listener = Source("Bridge", "SwCommandHandler.cs");
            Assert.Contains("matchStep: withStep || !withMesh", listener);
        }

        private static string Source(params string[] parts)
        {
            var path = Path.Combine(RepositoryRoot(), "sw-addin", "src", "Peak.Cadder");
            foreach (var part in parts) path = Path.Combine(path, part);
            return File.ReadAllText(path);
        }

        private static string RepositoryRoot([CallerFilePath] string here = null)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(here));
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "schema")))
                dir = dir.Parent;
            Assert.True(dir != null, "no repository root above " + here);
            return dir.FullName;
        }
    }
}
