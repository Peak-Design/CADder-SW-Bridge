using Xunit;
using Peak.Cadder.Core;
using System.Collections.Generic;

namespace Peak.Cadder.Tests
{
    /// <summary>
    /// The progress reporter's arithmetic and its stop rule.
    ///
    /// The bar itself needs SolidWorks, so what is tested here is what the
    /// stages hand it: a position that only moves forward inside a stage,
    /// never past the stage's end, and a stop that reaches the export.
    /// </summary>
    public class ExportProgressTests
    {
        /// <summary>A reporter that keeps what it was told, and the
        /// position the real bar would show.</summary>
        private sealed class Recorder : ExportProgress
        {
            public readonly List<string> Labels = new List<string>();
            public readonly List<double> Positions = new List<double>();
            public bool Stop;

            private int _from, _to, _steps;

            public override void Stage(string label, int from, int to, int steps = 0)
            {
                Labels.Add(label);
                _from = from;
                _to = to;
                _steps = steps;
                Positions.Add(Position(from, to, 0, steps));
            }

            public override void Step(int done)
            {
                Positions.Add(Position(_from, _to, done, _steps));
            }

            public override bool Cancelled { get { return Stop; } }
        }

        [Fact]
        public void StepsRunFromTheStageStartToItsEnd()
        {
            var r = new Recorder();
            r.Stage("probe", 24, 58, 4);
            r.Step(1);
            r.Step(2);
            r.Step(4);
            Assert.Equal(new[] { 24.0, 32.5, 41.0, 58.0 }, r.Positions);
        }

        [Fact]
        public void AStageWithNothingToCountSitsAtItsStart()
        {
            var r = new Recorder();
            r.Stage("reading the mates", 8, 20);
            r.Step(3);
            Assert.Equal(new[] { 8.0, 8.0 }, r.Positions);
        }

        [Fact]
        public void MoreStepsThanPlannedStillStopAtTheStageEnd()
        {
            var r = new Recorder();
            r.Stage("geometry", 78, 100, 2);
            r.Step(9);
            Assert.Equal(100.0, r.Positions[1]);
        }

        [Fact]
        public void AWindowPlacesAHalfOfTheExportWithoutRenumberingIt()
        {
            // A direct send: the rig export takes 0 to 78 of the bar, and
            // the tessellation the rest. Each half still numbers its own
            // stages from 0 to 100.
            var r = new Recorder();
            r.Window(0, 78);
            r.Stage("manifest", 95, 100);
            r.Window(78, 100);
            r.Stage("geometry", 0, 100, 4);
            r.Step(2);
            r.Step(4);
            Assert.Equal(74.1, r.Positions[0], 3);
            Assert.Equal(78.0, r.Positions[1], 3);
            Assert.Equal(89.0, r.Positions[2], 3);
            Assert.Equal(100.0, r.Positions[3], 3);
        }

        [Fact]
        public void TheBarNeverGoesBackwardsBetweenTheTwoHalves()
        {
            var r = new Recorder();
            r.Window(0, 78);
            r.Stage("assembly", 0, 8);
            r.Stage("probe", 24, 58, 2);
            r.Step(2);
            r.Stage("manifest", 95, 100);
            r.Window(78, 100);
            r.Stage("geometry", 0, 100, 2);
            r.Step(1);
            r.Step(2);
            for (int i = 1; i < r.Positions.Count; i++)
                Assert.True(r.Positions[i] >= r.Positions[i - 1],
                    "the bar went back at step " + i);
        }

        [Fact]
        public void TheReporterThatShowsNothingNeverStops()
        {
            Assert.False(ExportProgress.None.Cancelled);
            ExportProgress.None.StopIfCancelled();
            ExportProgress.None.Stage("x", 0, 10, 3);
            ExportProgress.None.Step(1);
        }

        [Fact]
        public void AStoppedExportThrowsWhereItChecks()
        {
            var r = new Recorder { Stop = true };
            Assert.Throws<ExportCancelled>(() => r.StopIfCancelled());
        }
    }
}
