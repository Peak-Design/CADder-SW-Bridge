using System;

namespace Peak.Cadder.Core
{
    /// <summary>
    /// How far the export has got, and whether the user has asked it to
    /// stop.
    ///
    /// An export of a large assembly runs for minutes on the SolidWorks
    /// thread, which leaves SolidWorks looking frozen. Every stage says
    /// where it starts and ends on a 0 to 100 scale, and a stage with a
    /// countable job also reports its own steps. The numbers are written
    /// where the stages are, so the reader sees the cost of each stage
    /// beside the code that pays it.
    ///
    /// This base class does nothing. The listener and the tests use it, so
    /// a headless export never touches the user interface. SwProgressBar
    /// drives the real bar.
    /// </summary>
    public class ExportProgress
    {
        /// <summary>A reporter that shows nothing and never cancels.</summary>
        public static readonly ExportProgress None = new ExportProgress();

        private int _windowFrom, _windowTo = 100;

        /// <summary>The share of the bar the next stages may use.
        ///
        /// A direct send runs two halves: the rig export, which knows
        /// nothing about the geometry, and the tessellation. Each half
        /// numbers its own stages from 0 to 100, and the caller says where
        /// that half sits, so the bar never goes backwards between
        /// them.</summary>
        public void Window(int from, int to)
        {
            _windowFrom = from;
            _windowTo = to < from ? from : to;
        }

        /// <summary>A stage begins. <paramref name="from"/> and
        /// <paramref name="to"/> are its share of the whole export, 0 to
        /// 100. <paramref name="steps"/> is how many pieces of work it
        /// holds, or 0 when it cannot be counted.</summary>
        public virtual void Stage(string label, int from, int to, int steps = 0) { }

        /// <summary>The stage has finished <paramref name="done"/> of its
        /// steps.</summary>
        public virtual void Step(int done) { }

        /// <summary>The user pressed Escape while the bar was up.</summary>
        public virtual bool Cancelled { get { return false; } }

        /// <summary>Stops the export when the user has asked for it. Called
        /// at stage boundaries, so the model is never left half-probed.
        /// </summary>
        public void StopIfCancelled()
        {
            if (Cancelled) throw new ExportCancelled();
        }

        /// <summary>The share of the stage that is done, 0.0 to 1.0, for an
        /// implementation to place the bar. Shared here so the rule is
        /// written once.</summary>
        protected static double Fraction(int done, int steps)
        {
            if (steps <= 0) return 0.0;
            if (done <= 0) return 0.0;
            if (done >= steps) return 1.0;
            return (double)done / steps;
        }

        /// <summary>Where the bar sits, 0 to 100, inside the window the
        /// caller gave.</summary>
        protected double Position(int from, int to, int done, int steps)
        {
            if (to < from) to = from;
            double own = from + (to - from) * Fraction(done, steps);
            return _windowFrom + (_windowTo - _windowFrom) * own / 100.0;
        }
    }

    /// <summary>The user stopped the export. Not a failure: the caller says
    /// so plainly and writes nothing.</summary>
    public class ExportCancelled : Exception
    {
        public ExportCancelled() : base("The export was stopped.") { }
    }
}
