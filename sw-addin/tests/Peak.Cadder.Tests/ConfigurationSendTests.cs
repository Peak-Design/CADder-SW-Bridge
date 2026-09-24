using System.Collections.Generic;
using Peak.Cadder.Bridge;
using Peak.Cadder.Core;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A send of one or more configurations: what each payload carries,
    /// what the progress dialog says, and what the user reads at the end.
    ///
    /// The export itself needs SolidWorks, so it is checked live. Each
    /// configuration is a complete send of its own, and one that fails
    /// must not hide among the ones that arrived.
    /// </summary>
    public class ConfigurationSendTests
    {
        private static readonly BlenderInstance Target = new BlenderInstance
        {
            Pid = 7, Port = 1, Token = "t", BlenderVersion = "5.1.0",
            BlendFile = @"C:\scenes\lift.blend",
        };

        private static SendToBlenderCommand.SendJob Job(
            string configuration, bool ok = true, string error = null)
        {
            var job = new SendToBlenderCommand.SendJob(configuration, "lift", @"C:\out", true);
            job.Error = error;
            if (error == null)
                job.Reply = new Dictionary<string, object> { { "ok", ok } };
            if (!ok)
                job.Reply["error"] = "no rig";
            return job;
        }

        [Fact]
        public void APayloadNamesItsConfigurationByItsRealName()
        {
            var payload = SendToBlenderCommand.BuildPayload(
                new AppSettings(), null, "lift_A_B.swmesh", null,
                sourceDocument: @"C:\cad\lift.SLDASM", configuration: "A/B");
            Assert.Equal("A/B", payload["configuration"]);
        }

        [Fact]
        public void APayloadWithNoConfigurationLeavesTheFieldOut()
        {
            var payload = SendToBlenderCommand.BuildPayload(
                new AppSettings(), null, "lift.swmesh", null);
            Assert.False(payload.ContainsKey("configuration"));
        }

        [Fact]
        public void ASendAppendsACopyOnlyWhenTheOptionIsOn()
        {
            var settings = new AppSettings();
            Assert.False(SendToBlenderCommand.BuildPayload(
                settings, null, "lift_A.swmesh", null).ContainsKey("append"));
            settings.AppendCopies = true;
            Assert.Equal(true, SendToBlenderCommand.BuildPayload(
                settings, null, "lift_A.swmesh", null)["append"]);
        }

        [Fact]
        public void ARefreshNeverAppendsACopy()
        {
            // Refresh Model brings the send and its copies up to date. A
            // copy made by a refresh would stack one more copy each time.
            var settings = new AppSettings { AppendCopies = true };
            var payload = SendToBlenderCommand.BuildPayload(
                settings, null, "lift_A.swmesh", "lift_A.rig.json", update: true);
            Assert.False(payload.ContainsKey("append"));
        }

        [Fact]
        public void OnlyASendWithLinksOffSaysSo()
        {
            var settings = new AppSettings();
            Assert.True(settings.LinkParts);
            Assert.False(SendToBlenderCommand.BuildPayload(
                settings, null, "lift_A.swmesh", null).ContainsKey("link_parts"));
            settings.LinkParts = false;
            Assert.Equal(false, SendToBlenderCommand.BuildPayload(
                settings, null, "lift_A.swmesh", null)["link_parts"]);
        }

        [Fact]
        public void AJobNamesItsFilesAfterTheStem()
        {
            var job = new SendToBlenderCommand.SendJob("A/B", "lift", @"C:\out", true);
            Assert.Equal("lift_A_B", job.Stem);
            Assert.Equal(@"C:\out\lift_A_B.swmesh", job.MeshPath);
            Assert.Equal(@"C:\out\lift_A_B.rig.json", job.ManifestPath);
            Assert.Equal(@"C:\out\lift_A_B.step", job.StepPath);
        }

        [Fact]
        public void APartHasNoManifest()
        {
            var job = new SendToBlenderCommand.SendJob("Default", "bracket", @"C:\out", false);
            Assert.Null(job.ManifestPath);
        }

        [Fact]
        public void TheDialogCountsTheJobsOfASendOfSeveral()
        {
            Assert.Equal("Importing lift_Open in Blender… (2 of 5)",
                SendToBlenderCommand.Doing(false, "lift_Open", 1, 5));
            Assert.Equal("Bringing lift_Open up to date in Blender… (1 of 2)",
                SendToBlenderCommand.Doing(true, "lift_Open", 0, 2));
        }

        [Fact]
        public void TheDialogOfASendOfOneSaysWhatItSaidBefore()
        {
            Assert.Equal("Importing lift_Default in Blender…",
                SendToBlenderCommand.Doing(false, "lift_Default", 0, 1));
        }

        [Fact]
        public void AReportOfOneJobIsTheSummaryAlone()
        {
            string report = SendToBlenderCommand.Report(
                new[] { Job("Default") }, false, Target, "Send to Blender");
            Assert.StartsWith("Sent lift_Default to Blender 5.1.0 at lift.blend", report);
        }

        [Fact]
        public void AFailedExportOfOneJobSaysSoAsBefore()
        {
            Assert.Equal("Send to Blender failed: the mates are broken",
                SendToBlenderCommand.Report(new[] { Job("Default", error: "the mates are broken") },
                    false, null, "Send to Blender"));
        }

        [Fact]
        public void AReportOfSeveralNamesEveryConfigurationAndEveryFailure()
        {
            string report = SendToBlenderCommand.Report(new[]
            {
                Job("Default"),
                Job("Open", error: "SolidWorks could not show the configuration Open."),
                Job("Closed", ok: false),
            }, false, Target, "Send to Blender");
            Assert.Contains("Default:\nSent lift_Default to Blender", report);
            Assert.Contains("Open:\nSend to Blender failed: SolidWorks could not show", report);
            Assert.Contains("Closed:\nBlender reported a failure for lift_Closed", report);
        }

        [Fact]
        public void ARefreshOfSeveralUsesTheRefreshSummary()
        {
            string report = SendToBlenderCommand.Report(
                new[] { Job("Default"), Job("Open") }, true, Target, "Refresh Model");
            Assert.Contains("Default:\nBlender took the assembly", report);
            Assert.Contains("Open:\nBlender took the assembly", report);
        }

        [Fact]
        public void OnlyAJobThatArrivedAndSucceededIsOk()
        {
            Assert.True(Job("Default").Ok);
            Assert.False(Job("Default", ok: false).Ok);
            Assert.False(Job("Default", error: "timed out").Ok);
        }

        [Fact]
        public void ALabSendTakesTheActiveConfigurationByDefault()
        {
            Assert.Equal(new string[] { null },
                SwCommandHandler.RequestedConfigurations(new Dictionary<string, object>()));
        }

        [Fact]
        public void ALabSendTakesOneOrSeveralConfigurations()
        {
            Assert.Equal(new[] { "Open" }, SwCommandHandler.RequestedConfigurations(
                new Dictionary<string, object> { { "configuration", "Open" } }));
            Assert.Equal(new[] { "Open", "Closed" }, SwCommandHandler.RequestedConfigurations(
                new Dictionary<string, object>
                {
                    { "configuration", "Open" },
                    { "configurations", new List<object> { "Closed", "Open" } },
                }));
        }

        [Fact]
        public void APosePushNamesTheConfigurationItMoves()
        {
            var payload = PosePush.PayloadOf("lift.SLDASM", @"C:\cad\lift.SLDASM",
                new List<object>(), "Open");
            Assert.Equal("Open", payload["configuration"]);
            Assert.Equal(@"C:\cad\lift.SLDASM", payload["source_document"]);
        }

        [Fact]
        public void APosePushWithNoConfigurationLeavesTheFieldOut()
        {
            var payload = PosePush.PayloadOf("lift.SLDASM", @"C:\cad\lift.SLDASM",
                new List<object>());
            Assert.False(payload.ContainsKey("configuration"));
        }
    }
}
