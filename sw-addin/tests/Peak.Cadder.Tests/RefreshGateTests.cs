using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Peak.Cadder.Bridge;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// When Refresh Model is offered, and which Blender a refresh goes to.
    ///
    /// Any registry file enabled the button before. A Blender that crashed
    /// or was ended leaves its file, and a new Blender that never got the
    /// send holds no scene of the document, so a refresh went to a scene
    /// without the model. Each Blender bridge now lists the documents its
    /// scenes hold, and an older bridge that lists nothing keeps the rule
    /// from before.
    /// </summary>
    public class RefreshGateTests : IDisposable
    {
        private const string Lift = @"C:\cad\lift.SLDASM";
        private const string Other = @"C:\cad\other.SLDASM";

        private readonly string _dir;

        public RefreshGateTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cadder-gate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private void Entry(int pid, string documentsJson)
        {
            File.WriteAllText(Path.Combine(_dir, pid + ".json"),
                "{\"pid\": " + pid + ", \"port\": 5" + pid % 1000 + ", \"token\": \"t\""
                + (documentsJson == null ? "" : ", \"documents\": " + documentsJson) + "}");
        }

        private static BlenderInstance Blender(int pid, params string[] documents)
        {
            return new BlenderInstance { Pid = pid, Port = 1, Token = "t",
                                         Documents = documents == null ? null : documents.ToList() };
        }

        private static BlenderInstance OlderBlender(int pid)
        {
            return new BlenderInstance { Pid = pid, Port = 1, Token = "t", Documents = null };
        }

        [Fact]
        public void TheRegistryListsTheDocumentsTheScenesHold()
        {
            Entry(101, "[\"" + Lift.Replace(@"\", @"\\") + "\"]");
            Entry(102, "[]");
            Entry(103, null);
            var read = BlenderBridge.ReadRegistry(_dir).ToDictionary(i => i.Pid);
            Assert.Equal(new[] { Lift }, read[101].Documents);
            Assert.Empty(read[102].Documents);
            Assert.Null(read[103].Documents);
        }

        [Fact]
        public void AFileBeingWrittenIsLeftOut()
        {
            Entry(101, "[]");
            File.WriteAllText(Path.Combine(_dir, "102.json"), "{\"pid\": 102, \"po");
            var read = BlenderBridge.ReadRegistry(_dir);
            Assert.Single(read);
            Assert.Equal(101, read[0].Pid);
        }

        [Fact]
        public void ABlenderThatHoldsTheDocumentEnablesRefresh()
        {
            var found = BlenderBridge.Holding(new[] { Blender(1, Other), Blender(2, Lift) }, Lift, false);
            Assert.NotNull(found);
            Assert.Equal(2, found.Pid);
        }

        [Fact]
        public void ABlenderWithADifferentDocumentDoesNot()
        {
            Assert.Null(BlenderBridge.Holding(new[] { Blender(1, Other) }, Lift, true));
        }

        [Fact]
        public void ABlenderThatHoldsNothingDoesNotEvenAfterASend()
        {
            // A new Blender after the one that got the send was ended: this
            // session sent the document, and still nothing holds it.
            Assert.Null(BlenderBridge.Holding(new[] { Blender(1) }, Lift, true));
        }

        [Fact]
        public void AnOlderBridgeKeepsTheRuleFromBefore()
        {
            Assert.NotNull(BlenderBridge.Holding(new[] { OlderBlender(1) }, Lift, true));
            Assert.Null(BlenderBridge.Holding(new[] { OlderBlender(1) }, Lift, false));
        }

        [Fact]
        public void NoBlenderAndNoDocumentMeanNoRefresh()
        {
            Assert.Null(BlenderBridge.Holding(new BlenderInstance[0], Lift, true));
            Assert.Null(BlenderBridge.Holding(new[] { Blender(1, Lift) }, "", true));
            Assert.Null(BlenderBridge.Holding(new[] { Blender(1, Lift) }, null, true));
        }

        [Fact]
        public void APathMatchesWhateverItsCaseAndSlashes()
        {
            Assert.True(Blender(1, Lift).Holds("c:/CAD/Lift.sldasm"));
            Assert.False(Blender(1, Lift).Holds(@"C:\cad\lift2.SLDASM"));
        }

        [Fact]
        public void ARefreshGoesToTheBlenderThatHoldsTheScene()
        {
            var all = new List<BlenderInstance> { Blender(1, Other), Blender(2, Lift), OlderBlender(3) };
            var chosen = BlenderBridge.ForRefresh(all, Lift);
            Assert.Single(chosen);
            Assert.Equal(2, chosen[0].Pid);
        }

        [Fact]
        public void TwoBlendersThatHoldTheSceneAreBothOffered()
        {
            var all = new List<BlenderInstance> { Blender(1, Lift), Blender(2, Lift, Other) };
            Assert.Equal(2, BlenderBridge.ForRefresh(all, Lift).Count);
        }

        [Fact]
        public void WhenNoneHoldsItARefreshIsOfferedToEveryBlender()
        {
            var all = new List<BlenderInstance> { OlderBlender(1), Blender(2, Other) };
            Assert.Equal(2, BlenderBridge.ForRefresh(all, Lift).Count);
        }
    }
}
