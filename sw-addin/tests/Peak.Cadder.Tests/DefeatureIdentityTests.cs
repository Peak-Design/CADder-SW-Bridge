using System.Collections.Generic;
using Peak.Cadder.Core;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A defeature row names the component the scene was built from. The
    /// component id is the manifest's number, and a part added in front of
    /// another moves every number after it. The row must then find the part
    /// it was about, by its persistent id, and never the part that took over
    /// its number: that part has holes nobody asked to lose.
    /// </summary>
    public class DefeatureIdentityTests
    {
        private static DefeatureOptions Rows(string json)
        {
            return DefeatureOptions.From(MiniJson.ParseObject(
                "{\"defeature\": [" + json + "]}"));
        }

        private static Dictionary<string, string> Present(params string[] pairs)
        {
            var present = new Dictionary<string, string>();
            for (int i = 0; i + 1 < pairs.Length; i += 2) present[pairs[i]] = pairs[i + 1];
            return present;
        }

        [Fact]
        public void ARowFollowsItsPartWhenTheNumbersMove()
        {
            // X was c005 when the scene was built. A part added in front of
            // it made X c006, and Y is c005 now.
            var options = Rows(
                "{\"component\": \"c005\", \"persistent_id\": \"PX\", \"size_m\": 0.008}")
                .ResolvedAgainst(Present("c005", "PY", "c006", "PX"));
            Assert.NotNull(options.For("c006"));
            Assert.Null(options.For("c005"));
        }

        [Fact]
        public void ARowWithoutAPersistentIdKeepsItsNumber()
        {
            // An older consumer sends the component id alone. It gets what
            // it always got.
            var options = Rows("{\"component\": \"c005\", \"size_m\": 0.008}")
                .ResolvedAgainst(Present("c005", "PY", "c006", "PX"));
            Assert.NotNull(options.For("c005"));
            Assert.Null(options.For("c006"));
        }

        [Fact]
        public void ARowForADeletedPartDefeaturesNothing()
        {
            // X is gone, and its old number now belongs to Y. Both carry a
            // persistent id and they differ, so the number names another
            // part.
            var options = Rows(
                "{\"component\": \"c005\", \"persistent_id\": \"PX\", \"size_m\": 0.008}")
                .ResolvedAgainst(Present("c005", "PY"));
            Assert.Null(options.For("c005"));
            Assert.Equal(1, options.Dropped);
        }

        [Fact]
        public void ANumberIsTrustedWhereSolidWorksGaveNoPersistentId()
        {
            var options = Rows(
                "{\"component\": \"c005\", \"persistent_id\": \"PX\", \"size_m\": 0.008}")
                .ResolvedAgainst(Present("c005", null));
            Assert.NotNull(options.For("c005"));
        }

        [Fact]
        public void APersistentMatchBeatsAStaleNumberForTheSamePart()
        {
            // Row one is about X, now c006. Row two still says c006, but its
            // own persistent id is gone. X keeps its own size.
            var options = Rows(
                "{\"component\": \"c005\", \"persistent_id\": \"PX\", \"size_m\": 0.008},"
                + " {\"component\": \"c006\", \"size_m\": 0.02}")
                .ResolvedAgainst(Present("c005", "PY", "c006", "PX"));
            Assert.Equal(0.008, options.For("c006").Size, 9);
            Assert.Null(options.For("c005"));
        }

        [Fact]
        public void APartDocumentStillGetsTheOneSpecItSent()
        {
            var options = Rows(
                "{\"component\": \"c001\", \"persistent_id\": \"PX\", \"size_m\": 0.008}")
                .ResolvedAgainst(Present("c001", "PY"));
            Assert.NotNull(options.For(null));
        }

        [Fact]
        public void NoMapLeavesTheRowsAsTheyCame()
        {
            var options = Rows(
                "{\"component\": \"c005\", \"persistent_id\": \"PX\", \"size_m\": 0.008}")
                .ResolvedAgainst(null);
            Assert.NotNull(options.For("c005"));
        }
    }
}
