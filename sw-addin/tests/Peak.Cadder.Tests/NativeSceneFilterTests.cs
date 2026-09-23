using System;
using System.Collections.Generic;
using Peak.Cadder.Core;
using Peak.Cadder.Sw;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// What the direct send tessellates. It must send what the STEP route
    /// of the same export would have in the file: only the picked parts
    /// with "only the selected components", nothing SolidWorks does not
    /// draw, and nothing at all once the user has pressed Escape.
    /// </summary>
    public class NativeSceneFilterTests
    {
        private static HashSet<string> Keep(params string[] paths)
        {
            return new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void APickedPartInARigidSubassemblyBringsOnlyItself()
        {
            // Selection.KeepSet adds every ancestor of a pick, which the
            // STEP route needs to keep the pick visible.
            var filter = NativeSceneBuilder.PathFilter.For(Keep("Rig-1/Part-1", "Rig-1"));
            Assert.False(filter.Wants("Rig-1"));
            Assert.True(filter.KeepsBelow("Rig-1"));
            Assert.True(filter.Wants("Rig-1/Part-1"));
            Assert.False(filter.Wants("Rig-1/Part-2"));
        }

        [Fact]
        public void APickedSubassemblyBringsEverythingInIt()
        {
            // KeepSet adds the whole subtree of a picked subassembly.
            var filter = NativeSceneBuilder.PathFilter.For(
                Keep("Rig-1", "Rig-1/Part-1", "Rig-1/Part-2", "Rig-1/Sub-1", "Rig-1/Sub-1/Pin-1"));
            Assert.True(filter.Wants("Rig-1/Part-2"));
            Assert.True(filter.Wants("Rig-1/Sub-1/Pin-1"));
        }

        [Fact]
        public void APathNamingABranchAloneBringsTheBranch()
        {
            // Retessellate names exact placements, without ancestors or
            // subtree. A path that names a subassembly brings all of it.
            var filter = NativeSceneBuilder.PathFilter.For(Keep("Rig-1/Sub-1"));
            Assert.True(filter.Wants("Rig-1/Sub-1/Pin-1"));
            Assert.False(filter.Wants("Rig-1/Part-1"));
            Assert.True(filter.KeepsBelow("Rig-1"));
            Assert.False(filter.Wants("Rig-1"));
        }

        [Fact]
        public void NoKeepSetKeepsEverything()
        {
            Assert.Null(NativeSceneBuilder.PathFilter.For(null));
        }

        private static WalkedComponent Walked(string id, WalkedComponent parent)
        {
            var w = new WalkedComponent { Id = id, Parent = parent };
            w.Graph.Path = parent == null ? id : parent.Graph.Path + "/" + id;
            if (parent != null) parent.Children.Add(w);
            return w;
        }

        [Fact]
        public void APartUnderAHiddenFlexibleSubassemblyIsHidden()
        {
            var flex = Walked("Flex-1", null);
            flex.Graph.Solving = "flexible";
            var inner = Walked("Inner-1", flex);
            inner.Graph.Solving = "flexible";
            var part = Walked("Part-1", inner);
            var hidden = new HashSet<string> { "Flex-1" };
            Func<WalkedComponent, bool> visible = w => !hidden.Contains(w.Id);
            Assert.True(NativeSceneBuilder.HiddenAbove(part, visible));
            Assert.True(NativeSceneBuilder.HiddenAbove(inner, visible));
            Assert.False(NativeSceneBuilder.HiddenAbove(flex, visible));
            hidden.Clear();
            Assert.False(NativeSceneBuilder.HiddenAbove(part, visible));
        }

        private sealed class Stopped : ExportProgress
        {
            public override bool Cancelled { get { return true; } }
        }

        [Fact]
        public void EscapeStopsTheTessellation()
        {
            var walked = new List<WalkedComponent> { Walked("Part-1", null) };
            Assert.Throws<ExportCancelled>(() => NativeSceneBuilder.Build(
                walked, BodyTessellator.FinenessFor(0.5), null, progress: new Stopped()));
        }
    }
}
