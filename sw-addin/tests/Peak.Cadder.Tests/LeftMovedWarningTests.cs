using System.Collections.Generic;
using Peak.Cadder.Core.Model;
using Peak.Cadder.Sw;
using Xunit;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// A component that the probes could not put back is named in the
    /// manifest, once, so the user hears about it in Blender. The restore
    /// logged it, and nothing else did: the STEP file, written after the
    /// probes, showed the component where the probe left it.
    /// </summary>
    public class LeftMovedWarningTests
    {
        private static ComponentMover.LeftMoved Moved(string name)
        {
            return new ComponentMover.LeftMoved { Name = name, Angle = 0.02, Distance = 0.0006 };
        }

        [Fact]
        public void NothingLeftMovedAddsNoWarning()
        {
            var warnings = new List<ManifestWarning>();
            ExportCommand.AddLeftMovedWarning(warnings, new List<ComponentMover.LeftMoved>(), null);
            ExportCommand.AddLeftMovedWarning(warnings, null, null);
            Assert.Empty(warnings);
        }

        [Fact]
        public void EachComponentIsNamedOnce()
        {
            // The limit sign probe and the relation probe can both leave
            // the same leaf moved.
            var warnings = new List<ManifestWarning>();
            ExportCommand.AddLeftMovedWarning(warnings,
                new List<ComponentMover.LeftMoved> { Moved("leaf-1"), Moved("cam-2"), Moved("leaf-1") },
                new List<WalkedComponent>());
            var w = Assert.Single(warnings);
            Assert.Equal("PROBE_LEFT_MOVED", w.Code);
            Assert.Contains("2 component(s)", w.Message);
            Assert.Contains("leaf-1, cam-2", w.Message);
            Assert.Empty(w.Components);
        }
    }
}
