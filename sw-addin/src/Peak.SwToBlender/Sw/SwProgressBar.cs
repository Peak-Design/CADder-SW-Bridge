using Peak.SwToBlender.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;

namespace Peak.SwToBlender.Sw
{
    /// <summary>
    /// The SolidWorks progress bar, driven by the export's stages.
    ///
    /// One bar runs from 0 to 1000 for the whole export, and each stage
    /// moves it through its own share. The title says what the export is
    /// doing now, so a long stage still reads as work rather than as a
    /// freeze.
    ///
    /// UpdateProgress answers swUpdateProgressError_UserCancel when the
    /// user presses Escape. That answer is kept, and the export reads it
    /// at the next stage boundary. The bar itself is closed in Dispose,
    /// whatever ends the export.
    /// </summary>
    public sealed class SwProgressBar : ExportProgress, IDisposable
    {
        private const int Scale = 1000;

        private readonly Action<string> _log;
        private readonly System.Diagnostics.Stopwatch _clock
            = System.Diagnostics.Stopwatch.StartNew();
        private string _stage;
        private UserProgressBar _bar;
        private bool _cancelled;
        private int _from, _to, _steps;
        private int _shown = -1;

        /// <summary>How many times the bar was moved, and what SolidWorks
        /// answered the last time. The test harness reports both: the bar
        /// lives in the status bar, which no screenshot of the graphics
        /// view can show.</summary>
        public int Updates { get; private set; }
        public int LastAnswer { get; private set; }

        private SwProgressBar(UserProgressBar bar, Action<string> log)
        {
            _bar = bar;
            _log = log;
        }

        /// <summary>The bar, or the reporter that does nothing when
        /// SolidWorks will not give one. A missing bar must never stop an
        /// export.</summary>
        public static ExportProgress Open(ISldWorks app, string title, Action<string> log)
        {
            if (app == null) return ExportProgress.None;
            UserProgressBar bar = null;
            try
            {
                if (!app.GetUserProgressBar(out bar) || bar == null)
                    return ExportProgress.None;
                bar.Start(0, Scale, title);
            }
            catch (Exception ex)
            {
                if (log != null) log("progress bar: " + ex.Message);
                return ExportProgress.None;
            }
            return new SwProgressBar(bar, log);
        }

        public override void Stage(string label, int from, int to, int steps = 0)
        {
            _from = from;
            _to = to;
            _steps = steps;
            // The log keeps the timeline of the stages, which is what says
            // where a slow export spends its minutes.
            if (_log != null)
                _log("stage " + Position(from, to, 0, steps).ToString("0")
                     + "%: " + label + " (at "
                     + _clock.Elapsed.TotalSeconds.ToString("0.0") + " s)");
            _stage = label;
            if (_bar == null) return;
            try { _bar.UpdateTitle(label); } catch { }
            Place(Position(from, to, 0, steps));
        }

        public override void Step(int done)
        {
            Place(Position(_from, _to, done, _steps));
        }

        public override bool Cancelled { get { return _cancelled; } }

        /// <summary>Moves the bar, and reads the user's Escape out of the
        /// answer. The bar is only repainted when the position changes, so
        /// a stage with thousands of steps does not spend its time in the
        /// user interface.</summary>
        private void Place(double percent)
        {
            if (_bar == null) return;
            int position = (int)Math.Round(percent * Scale / 100.0);
            if (position < 0) position = 0;
            if (position > Scale) position = Scale;
            if (position == _shown) return;
            _shown = position;
            int answer;
            try { answer = _bar.UpdateProgress(position); }
            catch { return; }
            Updates++;
            LastAnswer = answer;
            if (answer == (int)swUpdateProgressError_e.swUpdateProgressError_UserCancel
                && !_cancelled)
            {
                _cancelled = true;
                if (_log != null) _log("progress: the user asked to stop the export");
            }
        }

        public void Dispose()
        {
            var bar = _bar;
            _bar = null;
            if (bar == null) return;
            try { bar.End(); } catch { }
            if (_log != null)
                _log("progress bar closed after "
                     + _clock.Elapsed.TotalSeconds.ToString("0.0") + " s"
                     + (_stage == null ? "" : ", last stage: " + _stage));
        }
    }
}
