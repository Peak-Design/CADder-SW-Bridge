using System.Collections.Generic;
using Peak.Cadder.Bridge;
using Peak.Cadder.Core;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// What an update from CAD gets back after the assembly was edited.
    ///
    /// The walk numbers the components in name order, so a part deleted or
    /// added in SolidWorks moves the number of every part after it. Blender
    /// holds the old numbers and the persistent ids, and asks with both. The
    /// answer must say which persistent id each row answers, and a stale
    /// number that now names another part must not add that part.
    /// </summary>
    public class PoseRequestTests
    {
        /// <summary>The assembly Blender was sent: c001 to c005.</summary>
        private static readonly string[] SentIds = { "c001", "c002", "c003", "c004", "c005" };
        private static readonly string[] SentPersistent = { "P1", "P2", "P3", "P4", "P5" };

        /// <summary>The same assembly after the part that was c003 is
        /// deleted. Every part after it moves up one number.</summary>
        private static Dictionary<string, string> AfterDelete()
        {
            return new Dictionary<string, string>
            {
                { "c001", "P1" },
                { "c002", "P2" },
                { "c003", "P4" },
                { "c004", "P5" },
            };
        }

        private static readonly double[,] Identity =
        {
            { 1, 0, 0, 0 }, { 0, 1, 0, 0 }, { 0, 0, 1, 0 }, { 0, 0, 0, 1 },
        };

        private static List<Dictionary<string, object>> Rows(
            ComponentSelection selection, Dictionary<string, string> present)
        {
            var rows = new List<Dictionary<string, object>>();
            foreach (var kv in present)
                if (selection.Everything || selection.Ids.Contains(kv.Key))
                    rows.Add(SwCommandHandler.PoseRow(
                        kv.Key, "part-" + kv.Value, kv.Value, Identity, selection));
            return rows;
        }

        [Fact]
        public void EveryRowSaysWhichPersistentIdItAnswers()
        {
            var present = AfterDelete();
            var selection = ComponentSelection.Resolve(SentIds, SentPersistent, present);
            var rows = Rows(selection, present);

            Assert.Equal(4, rows.Count);
            foreach (var row in rows)
            {
                // The number is the new walk's. The persistent id is what
                // lets Blender put the row on the part it holds.
                Assert.Equal(present[(string)row["id"]], row["sw_persistent_id"]);
                Assert.Equal(row["sw_persistent_id"], row["requested_id"]);
            }
            var third = rows.Find(r => (string)r["id"] == "c003");
            Assert.Equal("P4", third["requested_id"]);
            Assert.Contains("P3", selection.Missing);
        }

        [Fact]
        public void ARowAskedForByNumberOnlyHasNoRequestedId()
        {
            var present = AfterDelete();
            var selection = ComponentSelection.Resolve(new[] { "c002" }, null, present);
            var row = Rows(selection, present)[0];
            Assert.Equal("c002", row["id"]);
            Assert.False(row.ContainsKey("requested_id"));
        }

        /// <summary>Blender asks for X by its old number c005 and by its
        /// persistent id. X is now c006, and c005 is another part, Y. Only
        /// X may come back.</summary>
        [Fact]
        public void AStaleNumberThatNamesAnotherPartIsNotAdded()
        {
            var present = new Dictionary<string, string>
            {
                { "c004", "P-other" },
                { "c005", "P-Y" },
                { "c006", "P-X" },
            };
            var selection = ComponentSelection.Resolve(
                new[] { "c005" }, new[] { "P-X" }, present);
            Assert.Equal(new[] { "c006" }, selection.Ids);
            Assert.Empty(selection.Missing);
        }

        [Fact]
        public void ANumberIsStillTakenForAPartWithNoPersistentId()
        {
            // SolidWorks gave c003 no persistent id, so its number is the
            // only name Blender has for it.
            var present = new Dictionary<string, string>
            {
                { "c001", "P1" },
                { "c002", "P2" },
                { "c003", null },
            };
            var selection = ComponentSelection.Resolve(
                new[] { "c001", "c003" }, new[] { "P1" }, present);
            Assert.Equal(new[] { "c001", "c003" }, selection.Ids);
        }

        [Fact]
        public void ANumberStillCountsWhenNoPersistentIdAnswered()
        {
            // An older scene, or a part SolidWorks gave no persistent id
            // when it was sent: the numbers are all there is.
            var present = AfterDelete();
            var selection = ComponentSelection.Resolve(
                new[] { "c002" }, new[] { "P-unknown" }, present);
            Assert.Equal(new[] { "c002" }, selection.Ids);
        }
    }
}
