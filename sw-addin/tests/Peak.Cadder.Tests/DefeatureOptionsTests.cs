using System.Collections.Generic;
using Peak.Cadder.Core;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// Which parts travel defeatured is the consumer's decision and arrives
    /// with the request. Two things have to hold for that to be safe: a
    /// component nobody named keeps the geometry it has always had, and two
    /// occurrences of one document with different settings stop sharing one
    /// mesh. The second is the one that bites silently: without it the first
    /// occurrence tessellated wins and every other one draws its shape.
    /// </summary>
    public class DefeatureOptionsTests
    {
        private static Dictionary<string, object> Request(string json)
        {
            return MiniJson.ParseObject(json);
        }

        [Fact]
        public void AComponentNobodyNamedTravelsAsItIs()
        {
            var options = DefeatureOptions.From(Request(
                "{\"defeature\": [{\"component\": \"c1\", \"size_m\": 0.012}]}"));
            Assert.NotNull(options.For("c1"));
            Assert.Null(options.For("c2"));
        }

        [Fact]
        public void ARequestWithoutTheKeyAsksForNothing()
        {
            var options = DefeatureOptions.From(Request("{\"quality\": 0.75}"));
            Assert.False(options.Any);
            Assert.Null(options.For("c1"));
        }

        [Fact]
        public void ASizeOfZeroIsNotAnInstructionToDefeature()
        {
            var options = DefeatureOptions.From(Request(
                "{\"defeature\": [{\"component\": \"c1\", \"size_m\": 0}]}"));
            Assert.False(options.Any);
            Assert.Null(options.For("c1"));
        }

        [Fact]
        public void TheSizeAndTheCurvedSwitchBothArrive()
        {
            var options = DefeatureOptions.From(Request(
                "{\"defeature\": [{\"component\": \"c1\", \"size_m\": 0.008, "
                + "\"curved\": true}]}"));
            var spec = options.For("c1");
            Assert.Equal(0.008, spec.Size, 9);
            Assert.True(spec.Curved);
        }

        [Fact]
        public void TwoSizesGiveTwoKeysSoTheMeshIsNotShared()
        {
            var options = DefeatureOptions.From(Request(
                "{\"defeature\": [{\"component\": \"c1\", \"size_m\": 0.008},"
                + " {\"component\": \"c2\", \"size_m\": 0.02}]}"));
            Assert.NotEqual(options.KeyFor("c1"), options.KeyFor("c2"));
        }

        [Fact]
        public void CurvedAloneIsEnoughToSplitTheKey()
        {
            var options = DefeatureOptions.From(Request(
                "{\"defeature\": [{\"component\": \"c1\", \"size_m\": 0.012},"
                + " {\"component\": \"c2\", \"size_m\": 0.012, \"curved\": true}]}"));
            Assert.NotEqual(options.KeyFor("c1"), options.KeyFor("c2"));
        }

        [Fact]
        public void TwoComponentsAskingAlikeShareOneKey()
        {
            var options = DefeatureOptions.From(Request(
                "{\"defeature\": [{\"component\": \"c1\", \"size_m\": 0.012},"
                + " {\"component\": \"c2\", \"size_m\": 0.012}]}"));
            Assert.Equal(options.KeyFor("c1"), options.KeyFor("c2"));
        }

        [Fact]
        public void APartLeftAloneCarriesNoKeyAtAll()
        {
            var options = DefeatureOptions.From(Request(
                "{\"defeature\": [{\"component\": \"c1\", \"size_m\": 0.012}]}"));
            Assert.Equal("", options.KeyFor("c2"));
        }

        [Fact]
        public void APartDocumentIsAnsweredByTheOneSpecItSent()
        {
            // A part document is one component and carries no component id.
            var options = DefeatureOptions.From(Request(
                "{\"defeature\": [{\"component\": \"c1\", \"size_m\": 0.012}]}"));
            var spec = options.For(null);
            Assert.NotNull(spec);
            Assert.Equal(0.012, spec.Size, 9);
        }
    }
}
