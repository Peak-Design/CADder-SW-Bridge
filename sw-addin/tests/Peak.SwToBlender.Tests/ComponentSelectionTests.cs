using System.Collections.Generic;
using Peak.SwToBlender.Core;
using Xunit;

namespace Peak.SwToBlender.Tests
{
    /// <summary>
    /// Which components an update from CAD is about. The component id is the
    /// manifest's numbering, stable for an assembly nobody has edited. The
    /// persistent id is SolidWorks' own reference, which survives an edit,
    /// so it answers first.
    /// </summary>
    public class ComponentSelectionTests
    {
        private static Dictionary<string, string> Assembly()
        {
            return new Dictionary<string, string>
            {
                { "c001", "PERSIST-cam" },
                { "c002", "PERSIST-follower" },
                { "c003", null },            // SolidWorks gave none
            };
        }

        [Fact]
        public void NamingNothingMeansTheWholeAssembly()
        {
            var selection = ComponentSelection.Resolve(null, null, Assembly());
            Assert.True(selection.Everything);
            Assert.Empty(selection.Ids);
            Assert.Empty(selection.Missing);
        }

        [Fact]
        public void APersistentIdFindsTheComponentWhateverItIsNumbered()
        {
            var selection = ComponentSelection.Resolve(
                null, new[] { "PERSIST-follower" }, Assembly());
            Assert.Equal(new[] { "c002" }, selection.Ids);
            Assert.Empty(selection.Missing);
        }

        /// <summary>The case this exists for: a scene sent before a part was
        /// added in SolidWorks asks for "c002" and "PERSIST-follower", which
        /// are now different components. The persistent id wins, and the
        /// stale component id is not reported as missing.</summary>
        [Fact]
        public void AfterAnEditThePersistentIdWinsAndTheOldNumberIsNotReported()
        {
            var after = new Dictionary<string, string>
            {
                { "c001", "PERSIST-new-bracket" },
                { "c002", "PERSIST-cam" },
                { "c003", "PERSIST-follower" },
            };
            var selection = ComponentSelection.Resolve(
                new[] { "c009" }, new[] { "PERSIST-follower" }, after);
            Assert.Equal(new[] { "c003" }, selection.Ids);
            Assert.Empty(selection.Missing);
        }

        [Fact]
        public void AComponentIdStillWorksOnItsOwn()
        {
            var selection = ComponentSelection.Resolve(new[] { "c003" }, null, Assembly());
            Assert.Equal(new[] { "c003" }, selection.Ids);
            Assert.Empty(selection.Missing);
        }

        [Fact]
        public void WhatTheAssemblyNoLongerHoldsIsReported()
        {
            var selection = ComponentSelection.Resolve(
                new[] { "c009" }, new[] { "PERSIST-deleted" }, Assembly());
            Assert.Empty(selection.Ids);
            Assert.Contains("PERSIST-deleted", selection.Missing);
            Assert.Contains("c009", selection.Missing);
            Assert.False(selection.Everything);
        }
    }
}
