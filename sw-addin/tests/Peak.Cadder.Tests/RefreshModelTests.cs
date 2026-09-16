using System.Collections.Generic;
using Peak.Cadder;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// What Refresh Model tells the user, from what Blender answered.
    ///
    /// The refresh itself needs SolidWorks and a running Blender, so it is
    /// checked live. What is checked here is the part a user reads, because
    /// a refresh that changed nothing looks exactly like one that failed
    /// unless the message says which it was.
    /// </summary>
    public class RefreshModelTests
    {
        private static Dictionary<string, object> Reply(
            List<object> added = null, List<object> removed = null,
            List<object> moved = null, List<object> reshaped = null,
            int kept = 0, Dictionary<string, object> rig = null)
        {
            var stages = new Dictionary<string, object>
            {
                {
                    "update", new Dictionary<string, object>
                    {
                        { "added", added ?? new List<object>() },
                        { "removed", removed ?? new List<object>() },
                        { "moved", moved ?? new List<object>() },
                        { "reshaped", reshaped ?? new List<object>() },
                        { "kept", (double)kept },
                    }
                },
            };
            if (rig != null) stages["rig"] = rig;
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "stages", stages },
            };
        }

        private static List<object> Parts(params string[] names)
        {
            var list = new List<object>();
            foreach (var name in names) list.Add(name);
            return list;
        }

        [Fact]
        public void WhatChangedIsCounted()
        {
            var reply = Reply(added: Parts("guard"), removed: Parts("clip"),
                              moved: Parts("arm"), kept: 7);
            Assert.Equal(
                "Blender is up to date: 1 added, 1 removed, 1 moved, 7 unchanged.",
                RefreshModelCommand.Summary(reply));
        }

        [Fact]
        public void NoChangeIsSaidPlainly()
        {
            Assert.Equal("Nothing has changed since the last send. "
                       + "9 part(s) left as they are.",
                RefreshModelCommand.Summary(Reply(kept: 9)));
        }

        [Fact]
        public void TheRigAnswerIsReportedWithIt()
        {
            var rig = new Dictionary<string, object>
            {
                { "mode", "APPEND" },
                { "added", Parts("guard") },
                { "removed", Parts("clip") },
            };
            Assert.Equal(
                "Blender is up to date: 1 added, 1 removed, 3 unchanged."
                + " The rig gained 1 bone(s) and lost 1.",
                RefreshModelCommand.Summary(
                    Reply(added: Parts("guard"), removed: Parts("clip"),
                          kept: 3, rig: rig)));
        }

        [Fact]
        public void ALockedRigIsSaidSoTheUserKnowsWhyNothingHappened()
        {
            var rig = new Dictionary<string, object> { { "locked", "cam_Rig" } };
            Assert.Contains("The rig cam_Rig is locked and was left alone.",
                RefreshModelCommand.Summary(
                    Reply(moved: Parts("arm"), kept: 2, rig: rig)));
        }

        [Fact]
        public void BlendersOwnReasonIsPassedOn()
        {
            var refused = new Dictionary<string, object>
            {
                { "ok", false },
                { "error", "mesh not found: C:\\temp\\gone.swmesh" },
            };
            Assert.Equal("mesh not found: C:\\temp\\gone.swmesh",
                RefreshModelCommand.Summary(refused));
        }

        [Fact]
        public void ARefusalWithNoReasonStillSaysSomething()
        {
            var refused = new Dictionary<string, object> { { "ok", false } };
            Assert.Equal("Blender refused the refresh.",
                RefreshModelCommand.Summary(refused));
        }

        [Fact]
        public void NoAnswerAtAllIsNotAnEmptyMessage()
        {
            Assert.Equal("Blender did not answer.",
                RefreshModelCommand.Summary(null));
        }

        [Fact]
        public void AnOlderBlenderThatCannotUpdateStillReports()
        {
            // No "update" stage: an add-on from before the refresh existed.
            var reply = new Dictionary<string, object>
            {
                { "ok", true },
                { "stages", new Dictionary<string, object>() },
            };
            Assert.Equal("Blender took the assembly, and reported no changes.",
                RefreshModelCommand.Summary(reply));
        }
    }
}
