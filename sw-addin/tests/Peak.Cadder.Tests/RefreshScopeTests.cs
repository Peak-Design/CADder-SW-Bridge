using System.Collections.Generic;
using Peak.Cadder.Bridge;
using Peak.Cadder.Core;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// What a refresh sends when "Only the selected components" is on.
    ///
    /// Blender brings a scene up to date by comparing it with the new
    /// file: a part that is not in the file has gone from the assembly, and
    /// its object is removed. A file cut to the selection therefore deleted
    /// every part that was not selected, with the materials and modifiers
    /// the user had put on them. So an update always carries the whole
    /// assembly, and so does an export Blender asks for.
    /// </summary>
    public class RefreshScopeTests
    {
        private static AppSettings OnlySelected()
        {
            return new AppSettings { OnlySelected = true };
        }

        [Fact]
        public void AFirstSendFollowsTheSelection()
        {
            Assert.True(SendToBlenderCommand.GeometryFollowsSelection(OnlySelected(), false));
        }

        [Fact]
        public void RefreshModelSendsTheWholeAssembly()
        {
            Assert.False(SendToBlenderCommand.GeometryFollowsSelection(OnlySelected(), true));
        }

        [Fact]
        public void TheSettingOffIsOffForEverySend()
        {
            var settings = new AppSettings { OnlySelected = false };
            Assert.False(SendToBlenderCommand.GeometryFollowsSelection(settings, false));
            Assert.False(SendToBlenderCommand.GeometryFollowsSelection(settings, true));
        }

        [Fact]
        public void AnExportBlenderAsksForIsTheWholeAssembly()
        {
            // Rebuild from CAD in Blender: a refresh or a new import of
            // the whole assembly. A stray click in SolidWorks must not cut it.
            var request = new Dictionary<string, object>
            {
                { "op", "export" }, { "mesh", true },
            };
            Assert.False(SwCommandHandler.OnlySelectedFor(request, OnlySelected(), false));
        }

        [Fact]
        public void TheLabRefreshSendsTheWholeAssembly()
        {
            var request = new Dictionary<string, object>
            {
                { "op", "send" }, { "update", true },
            };
            Assert.False(SwCommandHandler.OnlySelectedFor(request, OnlySelected(), true));
        }

        [Fact]
        public void TheLabSendFollowsTheRibbon()
        {
            var request = new Dictionary<string, object> { { "op", "send" } };
            Assert.True(SwCommandHandler.OnlySelectedFor(request, OnlySelected(), true));
        }

        [Fact]
        public void TheLabCanStillAskForTheSelection()
        {
            var request = new Dictionary<string, object>
            {
                { "op", "export" }, { "only_selected", true },
            };
            Assert.True(SwCommandHandler.OnlySelectedFor(
                request, new AppSettings { OnlySelected = false }, false));
        }
    }
}
