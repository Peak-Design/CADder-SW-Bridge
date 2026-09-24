using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Peak.Cadder.Bridge;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// Which configurations of a document a Blender says it holds, and what
    /// Refresh Model does with that.
    ///
    /// A Blender from 1.2.0 on lists them in its registry file and in its
    /// ping answer. A scene sent by 1.1 holds the document with no
    /// configuration, and an older bridge does not say at all. Both of
    /// those refresh the active configuration, as every refresh did before.
    /// </summary>
    public class HeldConfigurationTests : IDisposable
    {
        private const string Lift = @"C:\cad\lift.SLDASM";
        private const string Other = @"C:\cad\other.SLDASM";

        private readonly string _dir;

        public HeldConfigurationTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cadder-held-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private void Entry(int pid, string configurationsJson)
        {
            File.WriteAllText(Path.Combine(_dir, pid + ".json"),
                "{\"pid\": " + pid + ", \"port\": 5" + pid % 1000 + ", \"token\": \"t\""
                + ", \"documents\": [\"" + Lift.Replace(@"\", @"\\") + "\"]"
                + (configurationsJson == null ? "" : ", \"configurations\": " + configurationsJson)
                + "}");
        }

        private static string Json(string path)
        {
            return "\"" + path.Replace(@"\", @"\\") + "\"";
        }

        private static BlenderInstance Blender(
            int pid, string document, params string[] configurations)
        {
            var inst = new BlenderInstance
            {
                Pid = pid, Port = 1, Token = "t",
                Documents = new List<string> { document },
            };
            if (configurations != null)
                inst.Configurations = new Dictionary<string, List<string>>
                {
                    { document, configurations.ToList() },
                };
            return inst;
        }

        [Fact]
        public void TheRegistrySaysWhichConfigurationsTheScenesHold()
        {
            Entry(101, "{" + Json(Lift) + ": [\"Default\", \"Open\"]}");
            var read = BlenderBridge.ReadRegistry(_dir).Single();
            Assert.Equal(new[] { "Default", "Open" }, read.ConfigurationsOf(Lift));
        }

        [Fact]
        public void AnOlderBridgeSaysNothing()
        {
            Entry(101, null);
            var read = BlenderBridge.ReadRegistry(_dir).Single();
            Assert.Null(read.Configurations);
            Assert.Empty(read.ConfigurationsOf(Lift));
            // It still holds the document: only the configurations are
            // unknown.
            Assert.True(read.Holds(Lift));
        }

        [Fact]
        public void ASceneFromOneOneHoldsTheDocumentWithNoConfiguration()
        {
            Entry(101, "{" + Json(Lift) + ": []}");
            var read = BlenderBridge.ReadRegistry(_dir).Single();
            Assert.NotNull(read.Configurations);
            Assert.Empty(read.ConfigurationsOf(Lift));
        }

        [Fact]
        public void AMalformedListDoesNotHideTheBlender()
        {
            // Not an object: the Blender is still found, with nothing said.
            Entry(101, "[\"Default\"]");
            // A value that is not a list, and names that are not strings,
            // are left out one by one.
            Entry(102, "{" + Json(Lift) + ": [\"Default\", 3, null, \"\"], "
                + Json(Other) + ": \"Open\"}");
            var read = BlenderBridge.ReadRegistry(_dir).ToDictionary(i => i.Pid);
            Assert.Null(read[101].Configurations);
            Assert.Equal(new[] { "Default" }, read[102].ConfigurationsOf(Lift));
            Assert.Empty(read[102].ConfigurationsOf(Other));
        }

        [Fact]
        public void APathMatchesWhateverItsCaseAndSlashes()
        {
            var inst = Blender(1, Lift, "Default");
            Assert.Equal(new[] { "Default" }, inst.ConfigurationsOf("c:/CAD/Lift.sldasm"));
            Assert.Empty(inst.ConfigurationsOf(Other));
            Assert.Empty(inst.ConfigurationsOf(null));
        }

        [Fact]
        public void TheHeldConfigurationsOfEveryBlenderCountOnce()
        {
            var all = new[]
            {
                Blender(1, Lift, "Default", "Open"),
                Blender(2, Lift, "Open", "Closed"),
                Blender(3, Other, "Folded"),
            };
            Assert.Equal(new[] { "Default", "Open", "Closed" },
                BlenderBridge.HeldConfigurations(all, Lift));
        }

        [Fact]
        public void ABlenderThatDoesNotHoldTheDocumentDoesNotCount()
        {
            // It lists a configuration for the path, and not the path in its
            // documents. Holds is the rule, so it does not count.
            var odd = Blender(1, Other);
            odd.Configurations = new Dictionary<string, List<string>>
            {
                { Lift, new List<string> { "Default" } },
            };
            Assert.Empty(BlenderBridge.HeldConfigurations(new[] { odd }, Lift));
        }

        [Fact]
        public void TwoHeldConfigurationsAskTheUser()
        {
            Assert.Null(RefreshModelCommand.ConfigurationsToRefresh(
                new List<string> { "Default", "Open" }, "Default"));
        }

        [Fact]
        public void OneHeldConfigurationIsRefreshedAlsoWhenAnotherIsActive()
        {
            Assert.Equal(new[] { "Open" }, RefreshModelCommand.ConfigurationsToRefresh(
                new List<string> { "Open" }, "Default"));
        }

        [Fact]
        public void ASceneFromOneOneRefreshesTheActiveConfiguration()
        {
            Assert.Equal(new[] { "Default" }, RefreshModelCommand.ConfigurationsToRefresh(
                new List<string>(), "Default"));
            Assert.Equal(new[] { "Default" }, RefreshModelCommand.ConfigurationsToRefresh(
                null, "Default"));
        }
    }
}
